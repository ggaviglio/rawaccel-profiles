using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Foreground-window hook that maps running processes to mouse profiles.
/// </summary>
public interface IProcessWatcher : IDisposable { }

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
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Snapshot of attributes resolved for a single HWND.</summary>
    private readonly struct WindowAttributes
    {
        public string Title     { get; init; }
        public string Class     { get; init; }
        public string ExeName   { get; init; }
        public string FullPath  { get; init; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private readonly ProfileService _profileService;
    private readonly Action<MouseProfile> _applyProfile;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>
    /// Window classes that are Windows shell / compositor internals and will never
    /// match a user profile. Checked with a cheap GetClassName call before the
    /// expensive Process.GetProcessById lookup in GetWindowAttributes.
    /// </summary>
    private static readonly HashSet<string> _systemNoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForegroundStaging",            // focus-transition staging window
        "XamlExplorerHostIslandWindow",  // alt-tab / Task View XAML island
        "Shell_TrayWnd",                 // taskbar
        "Shell_SecondaryTrayWnd",        // taskbar on secondary monitor
        "TopLevelWindowForOverflowXamlIsland", // system-tray overflow
        "NotifyIconOverflowWindow",      // legacy system-tray overflow
        "WorkerW",                       // desktop worker window
        "Progman",                       // program manager / desktop
        "DummyDWMListenerWindow",        // DWM internal
        "IME",                           // input method editor
        "MSCTFIME UI",                   // text-framework IME
        "ThumbnailDeviceHelperWnd",      // Explorer thumbnail cache helper (IsWindowVisible=true but no UI)
        "EdgeUiInputTopWndClass",        // Explorer edge-swipe / input host helper
    };

    private MouseProfile? _currentProfile;
    /// <summary>
    /// HWNDs that have fired a foreground-match event for the current profile.
    /// Only destroy events for HWNDs in this set trigger the Last-Man-Standing scan,
    /// filtering out spurious IME / child-window destroy noise.
    /// </summary>
    private readonly HashSet<IntPtr> _knownForegroundHwnds = new();

    public ProcessWatcher(ProfileService profileService, Action<MouseProfile> applyProfile)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _applyProfile   = applyProfile   ?? throw new ArgumentNullException(nameof(applyProfile));

        // When ProfileService rebuilds its collection (e.g. after a save/reload),
        // all MouseProfile object references are replaced. Reset watcher state so
        // _currentProfile doesn't silently hold a stale reference.
        _profileService.Profiles.CollectionChanged += (_, _) =>
        {
            _currentProfile = null;
            _knownForegroundHwnds.Clear();
            Debug.WriteLine("[ProcessWatcher] Profiles collection changed — watcher state reset.");
        };

        _winEventDelegate = new WinEventDelegate(WinEventProc);
        if (OperatingSystem.IsWindows())
        {
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_DESTROY,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);
        }
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
        // Fast pre-check: skip known system/shell window classes before the
        // expensive Process.GetProcessById lookup inside GetWindowAttributes.
        var classSb = new StringBuilder(256);
        GetClassName(hwnd, classSb, classSb.Capacity);
        if (_systemNoiseClasses.Contains(classSb.ToString()))
            return;

        var attr    = GetWindowAttributes(hwnd);
        var matched = _profileService.Profiles.FirstOrDefault(pr => GetWindowMatchesProfile(attr, pr));

        if (matched == null)
        {
            // Sticky rule: no match → maintain current active context
            Debug.WriteLine($"[ProcessWatcher] FOREGROUND — no match; HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\"; retaining current profile.");
            return;
        }

        Debug.WriteLine($"[ProcessWatcher] FOREGROUND — match found: \"{matched.ProfileName}\" (HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\")");

        // Track every HWND that foregrounds into a matching profile.
        // A set supports multiple instances of the same app (e.g. two Vivaldi windows).
        if (!ReferenceEquals(matched, _currentProfile))
        {
            _currentProfile = matched;
            _knownForegroundHwnds.Clear(); // new profile context — reset HWND set
        }
        _knownForegroundHwnds.Add(hwnd);

        var profile = matched;
        Debug.WriteLine($"[ProcessWatcher] Dispatching apply for '{profile.ProfileName}'.");
        _applyProfile(profile);
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

        var attr = GetWindowAttributes(hwnd);
        Debug.WriteLine($"[ProcessWatcher] DESTROY — active-profile HWND destroyed (HWND={hwnd} Exe={attr.ExeName} Path={attr.FullPath} Class=\"{attr.Class}\" Title=\"{attr.Title}\"); scanning desktop…");

        // Scan all remaining visible windows for any match with the active profile.
        // Apply the same noise-class filter as HandleForeground so system windows
        var activeProfile = _currentProfile;
        bool anyMatch = false;
        var scanClassSb = new StringBuilder(256);
        WindowAttributes survivorAttr = default;

        EnumWindows((w, _) =>
        {
            if (w == hwnd || !IsWindowVisible(w))
                return true;

            scanClassSb.Clear();
            GetClassName(w, scanClassSb, scanClassSb.Capacity);
            if (_systemNoiseClasses.Contains(scanClassSb.ToString()))
                return true;

            var wAttr = GetWindowAttributes(w);
            if (GetWindowMatchesProfile(wAttr, activeProfile))
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
    // Central Matching Engine (Polymorphic)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if <paramref name="attr"/> satisfies at least one
    /// <see cref="TargetAssociation"/> in <paramref name="profile"/>.
    /// Priority order: FullPath → ExeName → WindowClass → WindowTitle.
    /// </summary>
    private static bool GetWindowMatchesProfile(in WindowAttributes attr, MouseProfile profile)
    {
        if (profile.TargetAssociations == null || profile.TargetAssociations.Count == 0)
            return false;

        foreach (var ta in profile.TargetAssociations)
        {
            bool hit = ta.MatchType switch
            {
                // 1. Full path — highest accuracy
                TargetMatchType.FullPath    => string.Equals(ta.Value, attr.FullPath, StringComparison.OrdinalIgnoreCase),
                // 2. Exe name
                TargetMatchType.ExeName     => string.Equals(ta.Value, attr.ExeName,  StringComparison.OrdinalIgnoreCase),
                // 3. Window class
                TargetMatchType.WindowClass => string.Equals(ta.Value, attr.Class,    StringComparison.OrdinalIgnoreCase),
                // 4. Window title (substring)
                TargetMatchType.WindowTitle => attr.Title.Contains(ta.Value,          StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (hit)
            {
                if (ta.MatchType == TargetMatchType.FullPath)
                    Debug.WriteLine($"[MATCH] Path Found: {attr.FullPath}");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves all window/process attributes for a given HWND into a <see cref="WindowAttributes"/> struct.
    /// Uses <c>GetWindowThreadProcessId</c> + <c>Process.GetProcessById</c> to obtain the full executable
    /// path; falls back gracefully on Access Denied or process-exited errors.
    /// </summary>
    private static WindowAttributes GetWindowAttributes(IntPtr hwnd)
    {
        string exeName  = string.Empty;
        string fullPath = string.Empty;

        GetWindowThreadProcessId(hwnd, out var pidUint);
        if (pidUint != 0 && pidUint <= (uint)int.MaxValue)
        {
            try
            {
                using var p = Process.GetProcessById((int)pidUint);
                try
                {
                    // Use MainModule.FileName to obtain the full executable path
                    var full = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(full))
                    {
                        fullPath = full;
                        exeName  = Path.GetFileName(full).ToLowerInvariant();
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access Denied for system/elevated processes — fall back to ProcessName
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
                catch
                {
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
            }
            catch { /* process exited or otherwise inaccessible */ }
        }

        var classSb = new StringBuilder(256);
        GetClassName(hwnd, classSb, classSb.Capacity);

        var titleSb = new StringBuilder(512);
        GetWindowText(hwnd, titleSb, titleSb.Capacity);

        return new WindowAttributes
        {
            FullPath = fullPath,
            ExeName  = exeName,
            Class    = classSb.ToString(),
            Title    = titleSb.ToString()
        };
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        try
        {
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
