using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Foreground-window hook that maps running processes to mouse profiles.
/// </summary>
public interface IProcessWatcher : IDisposable
{
    /// <summary>
    /// Rebuilds the internal profile-matching index from the current
    /// <see cref="ProfileService.Profiles"/> collection.
    /// Call after any property on a <see cref="MouseProfile"/> changes that
    /// affects matching (e.g. <see cref="MouseProfile.AutoSwitchEnabled"/>).
    /// </summary>
    void RebuildIndex();
}

/// <summary>
/// Unified Win32 hook that monitors foreground changes and window destruction.
/// Switches to a matching profile on foreground focus, and performs a "Last Man Standing"
/// desktop scan on window destroy to revert to the Global Default when no matching window
/// remains for the currently active profile.
/// </summary>
public sealed class ProcessWatcher : IProcessWatcher
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_DESTROY    = 0x8001;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private readonly ProfileService _profileService;
    private readonly Action<MouseProfile> _applyProfile;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook = IntPtr.Zero;

    private MouseProfile? _currentProfile;
    /// <summary>
    /// HWNDs that have fired a foreground-match event for the current profile.
    /// Only destroy events for HWNDs in this set trigger the Last-Man-Standing scan,
    /// filtering out spurious IME / child-window destroy noise.
    /// </summary>
    private readonly HashSet<IntPtr> _knownForegroundHwnds = new();

    // --- Profile-matching index (rebuilt whenever Profiles collection changes) ---
    // Exact-match types (ExeName, FullPath, WindowClass) use O(1) dictionary lookup.
    // WindowTitle uses substring matching so it remains a flat list that is only
    // scanned when the window title is actually retrieved (Stage 5).
    private Dictionary<string, List<MouseProfile>> _exeIndex   = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MouseProfile>> _pathIndex  = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MouseProfile>> _classIndex = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Value, MouseProfile Profile)> _titleRules = new();

    public ProcessWatcher(ProfileService profileService, Action<MouseProfile> applyProfile)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _applyProfile   = applyProfile   ?? throw new ArgumentNullException(nameof(applyProfile));

        // When ProfileService rebuilds its collection (e.g. after a save/reload),
        // all MouseProfile object references are replaced. Reset watcher state so
        // _currentProfile doesn't silently hold a stale reference.
        _profileService.Profiles.CollectionChanged += (_, e) =>
        {
            // Maintain per-profile PropertyChanged subscriptions.
            if (e.OldItems != null)
                foreach (MouseProfile p in e.OldItems)
                    p.PropertyChanged -= OnProfilePropertyChanged;
            if (e.NewItems != null)
                foreach (MouseProfile p in e.NewItems)
                    p.PropertyChanged += OnProfilePropertyChanged;

            _currentProfile = null;
            _knownForegroundHwnds.Clear();
            RebuildIndex();
            Debug.WriteLine("[ProcessWatcher] Profiles collection changed — watcher state reset.");
        };

        // Subscribe to all profiles already in the collection at construction time.
        foreach (var p in _profileService.Profiles)
            p.PropertyChanged += OnProfilePropertyChanged;

        _winEventDelegate = new WinEventDelegate(WinEventProc);
        if (OperatingSystem.IsWindows())
        {
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_DESTROY,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);
        }
        RebuildIndex();
    }

    // -------------------------------------------------------------------------
    // Central dispatch
    // -------------------------------------------------------------------------

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return;

            // Pre-filter: only react to events on real window objects.
            // idObject == 0 (OBJID_WINDOW) + idChild == 0 (CHILDID_SELF) excludes
            // caret, scroll-bar, menu-item, and other sub-object noise from EVENT_OBJECT_DESTROY.
            if (eventType == EVENT_OBJECT_DESTROY && (idObject != 0 || idChild != 0))
                return;

            if (eventType == EVENT_SYSTEM_FOREGROUND)
                HandleForeground(hwnd);
            else if (eventType == EVENT_OBJECT_DESTROY)
                HandleDestroy(hwnd);
        }
        catch (Exception ex)
        {
            // swallow — must never crash the hook thread
            Debug.WriteLine($"[ProcessWatcher] Unhandled exception in WinEventProc: {ex}");
        }
    }

    // -------------------------------------------------------------------------
    // EVENT_SYSTEM_FOREGROUND
    // -------------------------------------------------------------------------

    private void HandleForeground(IntPtr hwnd)
    {
        // Stage 1 — class name (cheap; already needed for noise filter).
        var className = WindowResolver.GetClassName(hwnd);
        if (WindowResolver.SystemNoiseClasses.Contains(className))
            return;

        // Stage 2 — class index look-up: zero process-open cost.
        MouseProfile? matched = FirstMatch(_classIndex.GetValueOrDefault(className));

        string exeName  = string.Empty;
        string fullPath = string.Empty;
        uint   pid      = 0;

        if (matched == null)
        {
            // Stage 3 — cheap ProcessName (no MainModule, no disk I/O).
            WindowResolver.GetWindowThreadProcessId(hwnd, out pid);
            if (pid != 0 && pid <= (uint)int.MaxValue)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
                catch { /* process exited */ }
            }

            if (!string.IsNullOrEmpty(exeName))
                matched = FirstMatch(_exeIndex.GetValueOrDefault(exeName));

            // Stage 4 — expensive MainModule.FileName; only when FullPath rules exist.
            if (matched == null && _pathIndex.Count > 0 && pid != 0 && pid <= (uint)int.MaxValue)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    try
                    {
                        var full = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(full))
                        {
                            fullPath = full;
                            exeName  = Path.GetFileName(full).ToLowerInvariant();
                            matched  = FirstMatch(_pathIndex.GetValueOrDefault(full));
                        }
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch { }
                }
                catch { }
            }
        }

        // Stage 5 — window title (cheap GetWindowText; only when title rules exist).
        string title = string.Empty;
        if (matched == null && _titleRules.Count > 0)
        {
            title = WindowResolver.GetWindowTitle(hwnd);
            foreach (var (value, profile) in _titleRules)
            {
                if (title.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    matched = profile;
                    break;
                }
            }
        }

        if (matched == null)
        {
            // Sticky rule: no match → maintain current active context.
            Debug.WriteLine($"[ProcessWatcher] FOREGROUND — no match; HWND={hwnd} Exe={exeName} Class=\"{className}\"; retaining current profile.");
            return;
        }

        Debug.WriteLine($"[ProcessWatcher] FOREGROUND — match found: \"{matched.ProfileName}\" (HWND={hwnd} Exe={exeName} Path={fullPath} Class=\"{className}\" Title=\"{title}\")");

        // Track every HWND that foregrounds into a matching profile.
        // A set supports multiple instances of the same app (e.g. two Vivaldi windows).
        if (!ReferenceEquals(matched, _currentProfile))
        {
            _currentProfile = matched;
            _knownForegroundHwnds.Clear(); // new profile context — reset HWND set
        }
        _knownForegroundHwnds.Add(hwnd);

        Debug.WriteLine($"[ProcessWatcher] Dispatching apply for '{matched.ProfileName}'.");
        _applyProfile(matched);
    }

    // -------------------------------------------------------------------------
    // EVENT_OBJECT_DESTROY  — "Last Man Standing"
    // -------------------------------------------------------------------------

    private void HandleDestroy(IntPtr hwnd)
    {
        // Only process destroy for HWNDs we actually saw as a foreground window.
        // This filters IME windows, MSCTFIME UI, CicMarshalWnd, and other auxiliary
        // child HWNDs that share the process exe but are never the foreground window.
        if (_currentProfile == null || !_knownForegroundHwnds.Remove(hwnd))
            return;

        var attr = WindowResolver.Resolve(hwnd);
        Debug.WriteLine($"[ProcessWatcher] DESTROY — active-profile HWND destroyed (HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\"); scanning desktop…");

        // Scan all remaining visible windows for any match with the active profile.
        // Apply the same noise-class filter as HandleForeground so system windows
        var activeProfile = _currentProfile;
        bool anyMatch = false;
        WindowResolver.WindowAttributes survivorAttr = default;

        // Pre-compute which attribute categories the active profile actually uses so
        // the EnumWindows scan can skip expensive MainModule.FileName when not needed.
        bool profileNeedsFullPath = activeProfile.TargetAssociations?.Any(
            ta => ta.MatchType == TargetMatchType.FullPath) == true;

        WindowResolver.EnumWindows((w, _) =>
        {
            if (w == hwnd || !WindowResolver.IsWindowVisible(w))
                return true;

            if (WindowResolver.SystemNoiseClasses.Contains(WindowResolver.GetClassName(w)))
                return true;

            var wAttr = WindowResolver.Resolve(w, profileNeedsFullPath);
            if (WindowResolver.MatchesAssociations(wAttr, activeProfile.TargetAssociations))
            {
                anyMatch = true;
                survivorAttr = wAttr;
                return false; // stop enumeration early
            }
            return true;
        }, IntPtr.Zero);

        if (anyMatch)
        {
            Debug.WriteLine($"[ProcessWatcher] DESTROY — survivor found; no revert. Survivor: HWND=? Exe={survivorAttr.ExeName} Path={survivorAttr.FullPath} Class=\"{survivorAttr.Class}\" Title=\"{survivorAttr.Title}\". Closed window: HWND={hwnd} Exe={attr.ExeName} Class=\"{attr.Class}\" Title=\"{attr.Title}\"");
            return;
        }

        // Last Man Standing — no windows remain for the active profile → revert to Global Default
        var global = _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault);
        if (global != null)
        {
            Debug.WriteLine($"[ProcessWatcher] DESTROY — last window closed; reverting to Global Default \"{global.ProfileName}\". Triggering window: HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\"");
            _currentProfile = global;
            _knownForegroundHwnds.Clear();
            _applyProfile(global);
        }
        else
        {
            Debug.WriteLine($"[ProcessWatcher] DESTROY — no Global Default found; cannot revert. Triggering window: HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\"");
        }
    }

    // -------------------------------------------------------------------------
    // Index management
    // -------------------------------------------------------------------------

    /// <summary>Rebuilds the profile-matching index. Also satisfies <see cref="IProcessWatcher.RebuildIndex"/>.</summary>
    public void RebuildIndex()
    {
        _exeIndex.Clear();
        _pathIndex.Clear();
        _classIndex.Clear();
        _titleRules.Clear();

        foreach (var profile in _profileService.Profiles)
        {
            if (!profile.AutoSwitchEnabled) continue;
            if (profile.TargetAssociations is not { Count: > 0 }) continue;
            foreach (var ta in profile.TargetAssociations)
            {
                if (string.IsNullOrEmpty(ta.Value)) continue;
                switch (ta.MatchType)
                {
                    case TargetMatchType.ExeName:     AddToIndex(_exeIndex,   ta.Value, profile); break;
                    case TargetMatchType.FullPath:    AddToIndex(_pathIndex,  ta.Value, profile); break;
                    case TargetMatchType.WindowClass: AddToIndex(_classIndex, ta.Value, profile); break;
                    case TargetMatchType.WindowTitle: _titleRules.Add((ta.Value, profile));       break;
                }
            }
        }

        Debug.WriteLine($"[ProcessWatcher] Index rebuilt — exe:{_exeIndex.Count} path:{_pathIndex.Count} class:{_classIndex.Count} title:{_titleRules.Count}");
    }

    /// <summary>
    /// Reacts to individual profile property changes.
    /// <see cref="MouseProfile.AutoSwitchEnabled"/> is the only property that changes
    /// index membership without a collection-level add/remove, so we rebuild on that alone.
    /// </summary>
    private void OnProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MouseProfile.AutoSwitchEnabled))
        {
            Debug.WriteLine($"[ProcessWatcher] AutoSwitchEnabled changed on '{(sender as MouseProfile)?.ProfileName}' \u2014 rebuilding index.");
            RebuildIndex();
        }
    }

    private static void AddToIndex(Dictionary<string, List<MouseProfile>> dict, string key, MouseProfile profile)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = new List<MouseProfile>();
        if (!list.Contains(profile))
            list.Add(profile);
    }

    /// <summary>Returns the first profile in <paramref name="list"/>, or <c>null</c>.</summary>
    private static MouseProfile? FirstMatch(List<MouseProfile>? list) =>
        list is { Count: > 0 } ? list[0] : null;

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        try
        {
            foreach (var p in _profileService.Profiles)
                p.PropertyChanged -= OnProfilePropertyChanged;

            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch
        {
            // swallow
        }
    }
}
