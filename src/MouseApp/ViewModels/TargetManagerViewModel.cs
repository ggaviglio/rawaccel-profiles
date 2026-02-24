using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

// ── Window-list entry ────────────────────────────────────────────────────────
/// <summary>Represents a single top-level window returned by the discovery scan.</summary>
public partial class WindowEntry : ObservableObject
{
    public IntPtr Hwnd      { get; }
    public string Title     { get; }
    public string ExeName   { get; }
    public string FullPath  { get; }
    public string ClassName { get; }

    /// <summary>Set by Test Match — true when this window satisfies a current association.</summary>
    [ObservableProperty]
    private bool isMatched;

    public WindowEntry(IntPtr hwnd, string title, string exeName, string fullPath, string className)
    {
        Hwnd      = hwnd;
        Title     = title;
        ExeName   = exeName;
        FullPath  = fullPath;
        ClassName = className;
    }

    public string DisplayText
    {
        get
        {
            var t = string.IsNullOrEmpty(Title) ? "[No Title]" : Title;
            return $"{t}  ({ExeName})";
        }
    }

    /// <summary>Multi-line tooltip shown in the Active Windows picker.</summary>
    public string DetailTooltip
    {
        get
        {
            var path = string.IsNullOrEmpty(FullPath) ? "(path unavailable)" : FullPath;
            var cls  = string.IsNullOrEmpty(ClassName) ? "(unknown)" : ClassName;
            return $"Path:  {path}\nClass: {cls}";
        }
    }
}

// ── TargetManagerViewModel ───────────────────────────────────────────────────
public partial class TargetManagerViewModel : ViewModelBase
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── Noise filter — kept in sync with ProcessWatcher._systemNoiseClasses ──
    // These classes are shell/compositor internals that will never be meaningful
    // profile targets. Filtering them keeps the Active Windows list clean.
    private static readonly HashSet<string> _systemNoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "WorkerW",
        "Progman",
        "DummyDWMListenerWindow",
        "IME",
        "MSCTFIME UI",
        "ThumbnailDeviceHelperWnd",
        "EdgeUiInputTopWndClass",
    };

    // ── State ────────────────────────────────────────────────────────────────
    private readonly MouseProfile   _profile;
    private readonly ProfileService _profileService;

    // ── Observable collections ───────────────────────────────────────────────
    public ObservableCollection<WindowEntry>       ActiveWindows { get; } = new();
    public ObservableCollection<TargetAssociation> Associations  { get; } = new();

    public IEnumerable<TargetMatchType> MatchTypeOptions { get; } = Enum.GetValues<TargetMatchType>();

    // ── Observable properties ────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAsExeCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAsPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAsTitleCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAsClassCommand))]
    private WindowEntry? selectedWindow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddManualCommand))]
    private string manualValue = string.Empty;

    [ObservableProperty]
    private TargetMatchType selectedManualType = TargetMatchType.ExeName;

    // ── Commands ─────────────────────────────────────────────────────────────
    public IRelayCommand                    RefreshWindowsCommand { get; }
    public IRelayCommand                    TestMatchCommand      { get; }
    public IRelayCommand                    AddAsExeCommand       { get; }
    public IRelayCommand                    AddAsPathCommand      { get; }
    public IRelayCommand                    AddAsTitleCommand     { get; }
    public IRelayCommand                    AddAsClassCommand     { get; }
    public IRelayCommand                    AddManualCommand      { get; }
    public IRelayCommand<TargetAssociation> RemoveCommand         { get; }

    /// <summary>Summary line shown below the Active Windows list after a Test Match run.</summary>
    [ObservableProperty]
    private string matchSummary = string.Empty;

    // ── Constructor ──────────────────────────────────────────────────────────
    public TargetManagerViewModel(MouseProfile profile, ProfileService profileService)
    {
        _profile        = profile        ?? throw new ArgumentNullException(nameof(profile));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));

        // Mirror existing associations
        foreach (var ta in _profile.TargetAssociations)
            Associations.Add(ta);

        RefreshWindowsCommand = new RelayCommand(ExecuteRefreshWindows);
        TestMatchCommand      = new RelayCommand(ExecuteTestMatch);
        AddAsExeCommand       = new RelayCommand(ExecuteAddAsExe,   () => SelectedWindow is not null);
        AddAsPathCommand      = new RelayCommand(ExecuteAddAsPath,  () => SelectedWindow is not null && !string.IsNullOrEmpty(SelectedWindow.FullPath));
        AddAsTitleCommand     = new RelayCommand(ExecuteAddAsTitle, () => SelectedWindow is not null && !string.IsNullOrEmpty(SelectedWindow.Title));
        AddAsClassCommand     = new RelayCommand(ExecuteAddAsClass, () => SelectedWindow is not null && !string.IsNullOrEmpty(SelectedWindow.ClassName));
        AddManualCommand      = new RelayCommand(ExecuteAddManual,  () => !string.IsNullOrWhiteSpace(ManualValue));
        RemoveCommand         = new RelayCommand<TargetAssociation>(ExecuteRemove);

        // Eager discovery load
        ExecuteRefreshWindows();
    }

    // ── Command implementations ──────────────────────────────────────────────

    private void ExecuteRefreshWindows()
    {
        ActiveWindows.Clear();
        if (!OperatingSystem.IsWindows()) return;

        var entries = new List<WindowEntry>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            // Class — checked first (cheap) against noise filter
            var classSb = new StringBuilder(256);
            GetClassName(hWnd, classSb, classSb.Capacity);
            var className = classSb.ToString();
            if (_systemNoiseClasses.Contains(className)) return true;

            // Title
            var titleSb = new StringBuilder(256);
            GetWindowText(hWnd, titleSb, titleSb.Capacity);
            var title = titleSb.ToString();

            // Process info
            GetWindowThreadProcessId(hWnd, out var pidUint);
            string exeName  = string.Empty;
            string fullPath = string.Empty;
            if (pidUint != 0 && pidUint <= (uint)int.MaxValue)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pidUint);
                    try
                    {
                        var fp = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(fp))
                        {
                            fullPath = fp;
                            exeName  = Path.GetFileName(fp).ToLowerInvariant();
                        }
                    }
                    catch { /* access denied – use ProcessName fallback */ }

                    if (string.IsNullOrEmpty(exeName))
                        exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
                catch { exeName = "[inaccessible]"; }
            }

            // Skip purely shell / helper windows with no exe info
            if (string.IsNullOrEmpty(exeName)) return true;

            entries.Add(new WindowEntry(hWnd, title, exeName, fullPath, className));
            return true;
        }, IntPtr.Zero);

        foreach (var e in entries)
            ActiveWindows.Add(e);

        // Clear stale match results whenever the window list is rebuilt.
        foreach (var entry in ActiveWindows)
            entry.IsMatched = false;
        MatchSummary = string.Empty;
    }

    // ── Test Match ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs each window in the Active Windows list through the same staged
    /// matching logic used by <see cref="ProcessWatcher"/> and highlights hits.
    /// </summary>
    private void ExecuteTestMatch()
    {
        var associations = _profile.TargetAssociations;

        if (associations == null || associations.Count == 0)
        {
            foreach (var e in ActiveWindows) e.IsMatched = false;
            MatchSummary = "No associations defined — add targets first.";
            return;
        }

        int matchCount = 0;
        foreach (var entry in ActiveWindows)
        {
            entry.IsMatched = WindowMatchesAssociations(entry, associations);
            if (entry.IsMatched) matchCount++;
        }

        MatchSummary = matchCount == 0
            ? "No open windows match the current associations."
            : $"{matchCount} of {ActiveWindows.Count} windows match — profile would activate on any of these.";
    }

    /// <summary>
    /// Mirrors <c>ProcessWatcher.GetWindowMatchesProfile</c> exactly.
    /// Must be kept in sync if the matching logic in ProcessWatcher changes.
    /// </summary>
    private static bool WindowMatchesAssociations(WindowEntry entry, List<TargetAssociation> associations)
    {
        foreach (var ta in associations)
        {
            bool hit = ta.MatchType switch
            {
                TargetMatchType.FullPath    => string.Equals(ta.Value, entry.FullPath,  StringComparison.OrdinalIgnoreCase),
                TargetMatchType.ExeName     => string.Equals(ta.Value, entry.ExeName,   StringComparison.OrdinalIgnoreCase),
                TargetMatchType.WindowClass => string.Equals(ta.Value, entry.ClassName, StringComparison.OrdinalIgnoreCase),
                TargetMatchType.WindowTitle => entry.Title.Contains(ta.Value,           StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (hit) return true;
        }
        return false;
    }

    // ── Add / Remove ─────────────────────────────────────────────────────────────────

    private void ExecuteAddAsExe()
    {
        if (SelectedWindow is null) return;
        AddAndPersist(new TargetAssociation
        {
            MatchType = TargetMatchType.ExeName,
            Value     = SelectedWindow.ExeName
        });
    }

    private void ExecuteAddAsPath()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.FullPath)) return;
        AddAndPersist(new TargetAssociation
        {
            MatchType = TargetMatchType.FullPath,
            Value     = SelectedWindow.FullPath
        });
    }

    private void ExecuteAddAsTitle()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.Title)) return;
        AddAndPersist(new TargetAssociation
        {
            MatchType = TargetMatchType.WindowTitle,
            Value     = SelectedWindow.Title
        });
    }

    private void ExecuteAddAsClass()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.ClassName)) return;
        AddAndPersist(new TargetAssociation
        {
            MatchType = TargetMatchType.WindowClass,
            Value     = SelectedWindow.ClassName
        });
    }

    private void ExecuteAddManual()
    {
        if (string.IsNullOrWhiteSpace(ManualValue)) return;
        AddAndPersist(new TargetAssociation
        {
            MatchType = SelectedManualType,
            Value     = ManualValue.Trim()
        });
        ManualValue = string.Empty;
    }

    private void ExecuteRemove(TargetAssociation? assoc)
    {
        if (assoc is null) return;
        Associations.Remove(assoc);
        SyncAndSave();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AddAndPersist(TargetAssociation assoc)
    {
        // Duplicate guard — same MatchType + Value (case-insensitive) already present
        bool duplicate = Associations.Any(a =>
            a.MatchType == assoc.MatchType &&
            string.Equals(a.Value, assoc.Value, StringComparison.OrdinalIgnoreCase));

        if (duplicate) return;

        Associations.Add(assoc);
        SyncAndSave();
    }

    private void SyncAndSave()
    {
        _profile.TargetAssociations = Associations.ToList();
        _ = _profileService.SaveAsync();
    }
}
