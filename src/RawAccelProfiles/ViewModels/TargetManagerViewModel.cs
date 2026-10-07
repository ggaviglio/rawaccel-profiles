using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RawAccelProfiles.Models;
using RawAccelProfiles.Services;

namespace RawAccelProfiles.ViewModels;

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

    /// <summary>Window title with a fallback, shown as the secondary line in the Active Windows picker.</summary>
    public string TitleDisplay => string.IsNullOrEmpty(Title) ? "[No Title]" : Title;

    /// <summary>Multi-line tooltip shown in the Active Windows picker. Includes Title -- found
    /// live that with horizontal scrolling disabled on the list, a long title trims with no
    /// other way to read the full text short of resizing the whole window.</summary>
    public string DetailTooltip
    {
        get
        {
            var path = string.IsNullOrEmpty(FullPath) ? "(path unavailable)" : FullPath;
            var cls  = string.IsNullOrEmpty(ClassName) ? "(unknown)" : ClassName;
            return $"Title: {TitleDisplay}\nPath:  {path}\nClass: {cls}";
        }
    }
}

// ── TargetManagerViewModel ───────────────────────────────────────────────────
public partial class TargetManagerViewModel : ViewModelBase
{
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
    public IRelayCommand                    SaveCommand           { get; }
    public IRelayCommand                    CancelCommand         { get; }

    /// <summary>Raised after Save or Cancel so the window can close itself.</summary>
    public event Action? RequestClose;

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
        SaveCommand           = new RelayCommand(ExecuteSave);
        CancelCommand         = new RelayCommand(() => RequestClose?.Invoke());

        // Eager discovery load
        ExecuteRefreshWindows();
    }

    // ── Command implementations ──────────────────────────────────────────────

    private void ExecuteRefreshWindows()
    {
        ActiveWindows.Clear();
        if (!OperatingSystem.IsWindows()) return;

        var entries = new List<WindowEntry>();

        WindowResolver.EnumWindows((hWnd, _) =>
        {
            if (!WindowResolver.IsWindowVisible(hWnd)) return true;

            // Class — checked first (cheap) against noise filter
            var className = WindowResolver.GetClassName(hWnd);
            if (WindowResolver.SystemNoiseClasses.Contains(className)) return true;

            // Title
            var title = WindowResolver.GetWindowTitle(hWnd);

            // Process info
            WindowResolver.GetWindowThreadProcessId(hWnd, out var pidUint);
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
        // Test against what's currently in the list, including unsaved edits —
        // not the last-committed state on the profile.
        var associations = Associations.ToList();

        if (associations.Count == 0)
        {
            foreach (var e in ActiveWindows) e.IsMatched = false;
            MatchSummary = "0 matched";
            return;
        }

        // Highlight entries in the visible Active Windows list. There used to be a second pass
        // scanning ALL windows (including hidden/tray) and reporting them as part of "profile
        // would activate on any of these" -- that claim was false for hidden windows:
        // ProcessWatcher (the actual runtime matcher) only acts on EVENT_SYSTEM_FOREGROUND, a
        // Win32 event that can't fire for a window that isn't visible, so a hidden/tray window
        // can never actually trigger a profile switch. Removed rather than reworded, since a
        // hidden-window match is never actionable information for this feature.
        int visibleMatches = 0;
        foreach (var entry in ActiveWindows)
        {
            entry.IsMatched = WindowMatchesAssociations(entry, associations);
            if (entry.IsMatched) visibleMatches++;
        }

        // Same "N matched" format whether or not there are associations to test (see the
        // associations.Count == 0 branch above) -- one consistent short message instead of a
        // separate long sentence for the no-associations case.
        MatchSummary = $"{visibleMatches} matched";
    }

    /// <summary>Delegates to <see cref="WindowResolver.MatchesAssociations"/> using the entry's attributes.</summary>
    private static bool WindowMatchesAssociations(WindowEntry entry, List<TargetAssociation> associations)
    {
        var attrs = new WindowResolver.WindowAttributes
        {
            ExeName  = entry.ExeName,
            FullPath = entry.FullPath,
            Class    = entry.ClassName,
            Title    = entry.Title,
        };
        return WindowResolver.MatchesAssociations(attrs, associations);
    }

    // ── Add / Remove ─────────────────────────────────────────────────────────────────

    private void ExecuteAddAsExe()
    {
        if (SelectedWindow is null) return;
        AddAssociation(new TargetAssociation
        {
            MatchType = TargetMatchType.ExeName,
            Value     = SelectedWindow.ExeName
        });
    }

    private void ExecuteAddAsPath()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.FullPath)) return;
        AddAssociation(new TargetAssociation
        {
            MatchType = TargetMatchType.FullPath,
            Value     = SelectedWindow.FullPath
        });
    }

    private void ExecuteAddAsTitle()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.Title)) return;
        AddAssociation(new TargetAssociation
        {
            MatchType = TargetMatchType.WindowTitle,
            Value     = SelectedWindow.Title
        });
    }

    private void ExecuteAddAsClass()
    {
        if (SelectedWindow is null || string.IsNullOrEmpty(SelectedWindow.ClassName)) return;
        AddAssociation(new TargetAssociation
        {
            MatchType = TargetMatchType.WindowClass,
            Value     = SelectedWindow.ClassName
        });
    }

    private void ExecuteAddManual()
    {
        if (string.IsNullOrWhiteSpace(ManualValue)) return;

        var value = ManualValue.Trim();
        // ExeName matching is an exact string compare against WindowResolver's ExeName, which
        // always ends in ".exe" (see that file) -- a manually-typed value missing it can never
        // match anything, silently, with no feedback. There's no legitimate case where a bare
        // name without ".exe" is what someone actually wants here, so fix it once at the source
        // (here, when the value is first saved) rather than re-normalizing on every match check.
        if (SelectedManualType == TargetMatchType.ExeName && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value += ".exe";

        AddAssociation(new TargetAssociation
        {
            MatchType = SelectedManualType,
            Value     = value
        });
        ManualValue = string.Empty;
    }

    private void ExecuteRemove(TargetAssociation? assoc)
    {
        if (assoc is null) return;
        Associations.Remove(assoc);
    }

    private void ExecuteSave()
    {
        _profile.TargetAssociations = Associations.ToList();
        _ = _profileService.SaveAsync();
        RequestClose?.Invoke();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AddAssociation(TargetAssociation assoc)
    {
        // Duplicate guard — same MatchType + Value (case-insensitive) already present
        bool duplicate = Associations.Any(a =>
            a.MatchType == assoc.MatchType &&
            string.Equals(a.Value, assoc.Value, StringComparison.OrdinalIgnoreCase));

        if (duplicate) return;

        Associations.Add(assoc);
    }
}
