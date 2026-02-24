using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly TemplateService  _templateService;
    private readonly ProfileService   _profileService;
    private readonly IWindowService   _windowService;

    /// <summary>Injected after construction via <see cref="WireMouseService"/>. Null until wired.</summary>
    private IMouseService? _mouseService;

    /// <summary>The profile currently loaded into the editor. Null when no profile is selected.</summary>
    private MouseProfile? _editingProfile;

    /// <summary>Guards against circular property-changed cascades during auto-sync.</summary>
    private bool _isSyncing;

    // ── Collections exposed to the View ────────────────────────────────────
    public ObservableCollection<string> Templates { get; } = new();

    /// <summary>Null entry allows a sparse (unset) selection at the top of the list.</summary>
    public ObservableCollection<int?> WinPointerOptions { get; } = new();

    // ── Profile fields ──────────────────────────────────────────────────────
    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    private string? selectedTemplate;

    // Sensitivity (double?) + its string proxy for TextBox binding
    [ObservableProperty]
    private double? sensitivity;

    [ObservableProperty]
    private string sensitivityString = string.Empty;

    // OS-level auto-sync toggle
    [ObservableProperty]
    private bool useAutoOSSync;

    [ObservableProperty]
    private int? winPointerSpeed;

    // Registry Bridge auto-sync toggle
    [ObservableProperty]
    private bool useAutoRegSync;

    [ObservableProperty]
    private double? registryCurveOverride;

    [ObservableProperty]
    private string registryCurveString = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetManagerEnabled))]
    [NotifyPropertyChangedFor(nameof(IsAutoSyncAvailable))]
    [NotifyCanExecuteChangedFor(nameof(OpenTargetManagerCommand))]
    private bool isGlobalDefault;

    // ── Derived display ─────────────────────────────────────────────────────
    /// <summary>
    /// Raw-Accel-only proof: Sens / Effective(GlobalDefault) × 100.
    /// Shows the raw sensitivity delta before any OS compensation is applied.
    /// </summary>
    [ObservableProperty]
    private string rawAccelMatchAccuracy = "N/A";

    /// <summary>
    /// Win-speed-only proof: (Sens × WinMult) / Effective(GlobalDefault) × 100.
    /// Shows how close Raw Accel + WinPointerSpeed alone gets you, before the registry bridge.
    /// </summary>
    [ObservableProperty]
    private string winSpeedMatchAccuracy = "N/A";

    /// <summary>
    /// Full proof: (Sens × WinMult × RegCurve) / Effective(GlobalDefault) × 100.
    /// 100.00% = exact compensation across all three factors.
    /// </summary>
    [ObservableProperty]
    private string matchAccuracy = "N/A";

    // ── Target Manager (read-only summary shown in the editor) ──────────────
    public ObservableCollection<TargetAssociation> TargetAssociationsSummary { get; } = new();

    /// <summary>True when Target Manager is interactive (not Global Default).</summary>
    public bool IsTargetManagerEnabled => !IsGlobalDefault;

    /// <summary>
    /// Auto-sync only makes sense on non-Global-Default profiles.
    /// The Global Default IS the reference — there is nothing to compensate relative to itself.
    /// </summary>
    public bool IsAutoSyncAvailable => !IsGlobalDefault;

    /// <summary>True when the current profile has no target associations.</summary>
    public bool IsTargetAssociationsEmpty => TargetAssociationsSummary.Count == 0;

    // ── Commands ────────────────────────────────────────────────────────────
    public IAsyncRelayCommand SaveCommand              { get; }
    public IAsyncRelayCommand ApplyCommand             { get; }
    public IAsyncRelayCommand OpenTargetManagerCommand { get; }

    // ── Constructor ─────────────────────────────────────────────────────────
    public ProfileEditorViewModel(TemplateService templateService, ProfileService profileService, IWindowService windowService)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _profileService  = profileService  ?? throw new ArgumentNullException(nameof(profileService));
        _windowService   = windowService   ?? throw new ArgumentNullException(nameof(windowService));

        RefreshTemplates();
        _templateService.TemplatesChanged += (_, _) => RefreshTemplates();

        WinPointerOptions.Add(null); // sparse / unset sentinel
        for (int i = 1; i <= 20; i++) WinPointerOptions.Add(i);

        _profileService.Profiles.CollectionChanged += OnProfilesCollectionChanged;

        SaveCommand              = new AsyncRelayCommand(ExecuteSaveAsync);
        ApplyCommand             = new AsyncRelayCommand(ExecuteApplyAsync, () => _editingProfile != null);
        OpenTargetManagerCommand = new AsyncRelayCommand(ExecuteOpenTargetManagerAsync,
                                       () => _editingProfile != null && !IsGlobalDefault);
    }

    // ── Profile loading ─────────────────────────────────────────────────────

    /// <summary>Populates all editor fields from <paramref name="profile"/>.</summary>
    public void LoadProfile(MouseProfile profile)
    {
        _editingProfile = profile;

        _isSyncing = true;
        try
        {
            ProfileName           = profile.ProfileName;
            SelectedTemplate      = profile.RawAccelTemplate;
            Sensitivity           = profile.SensMultiplier;
            SensitivityString     = profile.SensMultiplier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UseAutoOSSync         = profile.UseAutoOSSync;
            WinPointerSpeed       = profile.WinPointerSpeed;
            UseAutoRegSync        = profile.UseAutoRegSync;
            RegistryCurveOverride = profile.RegistryCurveOverride;
            RegistryCurveString   = profile.RegistryCurveOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            IsGlobalDefault       = profile.IsGlobalDefault;
        }
        finally
        {
            _isSyncing = false;
        }

        RefreshAssociationsSummary();
        OpenTargetManagerCommand.NotifyCanExecuteChanged();
        RecalculateMatchAccuracy();
    }

    // ── Partial property callbacks ──────────────────────────────────────────

    partial void OnProfileNameChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;
        AutoPersist();
    }

    partial void OnSensitivityChanged(double? oldValue, double? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        SensitivityString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;

        ApplyAutoOSSync();
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnSensitivityStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            Sensitivity = null;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            _isSyncing = true;
            Sensitivity = parsed;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnUseAutoOSSyncChanged(bool oldValue, bool newValue)
    {
        ApplyAutoOSSync();
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnWinPointerSpeedChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;

        // Manual speed edit while AutoOSSync is on -> honour auto, ignore manual input
        if (UseAutoOSSync)
        {
            ApplyAutoOSSync();
            return;
        }

        ApplyAutoRegSync();
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnUseAutoRegSyncChanged(bool oldValue, bool newValue)
    {
        ApplyAutoRegSync();
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnRegistryCurveOverrideChanged(double? oldValue, double? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        RegistryCurveString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;
        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnRegistryCurveStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            RegistryCurveOverride = null;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            _isSyncing = true;
            RegistryCurveOverride = parsed;
            _isSyncing = false;
        }

        RecalculateMatchAccuracy();
        AutoPersist();
    }

    partial void OnSelectedTemplateChanged(string? oldValue, string? newValue)
    {
        AutoPersist();
    }

    partial void OnIsGlobalDefaultChanged(bool oldValue, bool newValue)
    {
        if (_isSyncing || _editingProfile == null) return;

        if (newValue)
        {
            // SetGlobalDefaultAsync atomically clears all others and saves.
            _ = _profileService.SetGlobalDefaultAsync(_editingProfile);
        }
        else
        {
            // Prevent unchecking when this is the only global default — we must always
            // have exactly one. Revert the checkbox silently and bail.
            bool isOnlyDefault = _profileService.Profiles.Count(p => p.IsGlobalDefault) <= 1
                                 && _editingProfile.IsGlobalDefault;
            if (isOnlyDefault)
            {
                _isSyncing = true;
                IsGlobalDefault = true;  // revert the VM back to true
                _isSyncing = false;
                return;
            }
            // Profile was previously default but another is now: just persist the false.
            _editingProfile.IsGlobalDefault = false;
            _ = _profileService.SaveAsync();
        }

        OpenTargetManagerCommand.NotifyCanExecuteChanged();
    }

    // ── Math Engine ─────────────────────────────────────────────────────────

    private MouseProfile? GetBaseProfile() =>
        _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault)
        ?? _profileService.Profiles.FirstOrDefault();

    /// <summary>Raw Accel SensMultiplier of the Global Default (fallback 1.0).</summary>
    private double GetBaseSens()
    {
        var s = GetBaseProfile()?.SensMultiplier ?? 1.0;
        return s == 0 ? 1.0 : s;
    }

    /// <summary>WinMultipliers entry for the Global Default's WinPointerSpeed (fallback index 9 = 1.0×).</summary>
    private double GetBaseWinMult()
    {
        var bp  = GetBaseProfile();
        var idx = Math.Clamp((bp?.WinPointerSpeed ?? 10) - 1, 0, ProfileService.WinMultipliers.Length - 1);
        return ProfileService.WinMultipliers[idx];
    }

    /// <summary>
    /// Auto Win Pointer Speed: choose the WinMult closest to (S_base × WinMult_base) / S_target,
    /// so that S_target × WinMult ≈ S_base × WinMult_base (matching the global default’s win-level effective).
    /// </summary>
    private void ApplyAutoOSSync()
    {
        if (IsGlobalDefault) { ApplyAutoRegSync(); return; }

        if (UseAutoOSSync)
        {
            var sTarget = Sensitivity ?? 1.0;
            if (sTarget == 0) sTarget = 1.0;
            var target = (GetBaseSens() * GetBaseWinMult()) / sTarget;

            int bestIndex  = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < ProfileService.WinMultipliers.Length; i++)
            {
                double diff = Math.Abs(ProfileService.WinMultipliers[i] - target);
                if (diff < minDiff) { minDiff = diff; bestIndex = i; }
            }

            _isSyncing = true;
            WinPointerSpeed = bestIndex + 1;
            _isSyncing = false;
        }

        ApplyAutoRegSync();
    }

    /// <summary>
    /// Auto Registry Bridge: RegCurve = Effective(base) / (S_target × WinMult_current),
    /// covering the exact gap so that S_target × WinMult × RegCurve == Effective(base).
    /// </summary>
    private void ApplyAutoRegSync()
    {
        if (IsGlobalDefault) return;

        if (UseAutoRegSync)
        {
            var bp      = GetBaseProfile();
            var baseEff = bp != null ? _profileService.GetEffectiveSensitivity(bp) : 1.0;
            if (baseEff == 0) baseEff = 1.0;

            var sTarget  = Sensitivity ?? 1.0;
            if (sTarget == 0) sTarget = 1.0;

            var winIndex  = Math.Clamp((WinPointerSpeed ?? 10) - 1, 0, ProfileService.WinMultipliers.Length - 1);
            var winMult   = ProfileService.WinMultipliers[winIndex];
            var remainder = (sTarget * winMult) == 0 ? 1.0 : baseEff / (sTarget * winMult);

            _isSyncing = true;
            RegistryCurveOverride = remainder;
            RegistryCurveString   = remainder.ToString("G6", CultureInfo.InvariantCulture);
            _isSyncing = false;
        }
    }

    /// <summary>
    /// Three-row waterfall proof — each row’s denominator matches its numerator’s scope:
    ///   Raw Accel Only  : curSens                     / S_base
    ///   Win Speed Only  : curSens × WinMult           / (S_base × WinMult_base)
    ///   Full Match      : curSens × WinMult × RegCurve / Effective(base)
    /// 100% on Full Match = profile’s effective feel exactly equals the Global Default.
    /// On the Global Default itself all three rows always show 100%.
    /// </summary>
    private void RecalculateMatchAccuracy()
    {
        var bp       = GetBaseProfile();
        var sBase    = GetBaseSens();
        var wmBase   = GetBaseWinMult();
        var winBase  = sBase * wmBase;   // S_base × WinMult_base
        var fullBase = bp != null ? _profileService.GetEffectiveSensitivity(bp) : sBase;
        if (fullBase == 0) fullBase = 1.0;

        var curSens     = Sensitivity ?? 1.0;
        var curWinIndex = Math.Clamp((WinPointerSpeed ?? 10) - 1, 0, ProfileService.WinMultipliers.Length - 1);
        var curWinMult  = ProfileService.WinMultipliers[curWinIndex];
        var curRegCurve = RegistryCurveOverride ?? 1.0;

        var rawEff  = curSens;
        var winEff  = rawEff  * curWinMult;
        var fullEff = winEff  * curRegCurve;

        RawAccelMatchAccuracy = ((rawEff  / sBase)    * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        WinSpeedMatchAccuracy = ((winEff  / winBase)  * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        MatchAccuracy         = ((fullEff / fullBase) * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    // ── Auto-Persist ────────────────────────────────────────────────────────

    /// <summary>
    /// Commits all current editor values into _editingProfile and fires a background save.
    /// Called at the end of every On[Property]Changed chain, after auto-sync math is done.
    /// Skipped while _isSyncing is true to prevent cascaded saves during bulk loads.
    /// </summary>
    private void AutoPersist()
    {
        if (_editingProfile == null || _isSyncing) return;

        _editingProfile.ProfileName           = ProfileName;
        _editingProfile.RawAccelTemplate      = SelectedTemplate;
        _editingProfile.SensMultiplier        = Sensitivity;
        _editingProfile.UseAutoOSSync         = UseAutoOSSync;
        _editingProfile.WinPointerSpeed       = WinPointerSpeed;
        _editingProfile.UseAutoRegSync        = UseAutoRegSync;
        _editingProfile.RegistryCurveOverride = RegistryCurveOverride;
        // IsGlobalDefault is intentionally NOT written here.
        // It is exclusively managed by OnIsGlobalDefaultChanged → SetGlobalDefaultAsync,
        // which performs the atomic exclusive-flag update and file write in one shot.

        _ = _profileService.SaveAsync();
    }

    // ── Target Manager ──────────────────────────────────────────────────────

    private async Task ExecuteOpenTargetManagerAsync()
    {
        if (_editingProfile is null) return;
        // Do NOT ConfigureAwait(false) — the summary refresh mutates an ObservableCollection
        // and must run on the Avalonia UI thread.
        await _windowService.ShowTargetManagerAsync(_editingProfile, _profileService);
        // Single, safe refresh after the window is fully closed.
        RefreshAssociationsSummary();
    }

    private void RefreshAssociationsSummary()
    {
        TargetAssociationsSummary.Clear();
        foreach (var ta in _editingProfile?.TargetAssociations ?? new List<TargetAssociation>())
            TargetAssociationsSummary.Add(ta);
        OnPropertyChanged(nameof(IsTargetAssociationsEmpty));
    }

    // ── Save / Apply Commands ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the currently loaded profile to hardware immediately,
    /// bypassing the foreground-window watcher.
    /// </summary>
    private async Task ExecuteApplyAsync()
    {
        if (_editingProfile == null || _mouseService == null) return;

        System.Diagnostics.Debug.WriteLine($"[ProfileEditor] Manual apply: '{_editingProfile.ProfileName}'");
        await _mouseService.ApplyProfileAsync(_editingProfile).ConfigureAwait(false);
    }

    /// <summary>
    /// Provides the <see cref="IMouseService"/> dependency after construction.
    /// Called from <c>App.axaml.cs</c> once both the editor VM and the service are available.
    /// </summary>
    public void WireMouseService(IMouseService mouseService)
    {
        _mouseService = mouseService ?? throw new ArgumentNullException(nameof(mouseService));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteSaveAsync()
    {
        // This path is only hit if _editingProfile is null (e.g. edge-case on first load).
        // In normal operation AutoPersist handles all saves; IsGlobalDefault is managed
        // exclusively by OnIsGlobalDefaultChanged \u2192 SetGlobalDefaultAsync, never here.
        if (_editingProfile == null)
        {
            var draft = new MouseProfile
            {
                ProfileName           = ProfileName,
                RawAccelTemplate      = SelectedTemplate,
                SensMultiplier        = Sensitivity,
                UseAutoOSSync         = UseAutoOSSync,
                WinPointerSpeed       = WinPointerSpeed,
                UseAutoRegSync        = UseAutoRegSync,
                RegistryCurveOverride = RegistryCurveOverride,
                IsGlobalDefault       = false, // new profiles are never the default on creation
                TargetAssociations    = new()
            };
            _profileService.Profiles.Add(draft);
            _editingProfile = draft;
        }

        AutoPersist();
        await Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void RefreshTemplates()
    {
        Templates.Clear();
        foreach (var t in _templateService.GetTemplates())
            Templates.Add(t);
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RecalculateMatchAccuracy();
}
