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

    [ObservableProperty]
    private bool useAutoRawSync;

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

    // ── Environment fields ──────────────────────────────────────────────────

    // TargetScreenWidth (int?) + string proxy for TextBox binding
    [ObservableProperty]
    private int? targetScreenWidth;

    [ObservableProperty]
    private string targetScreenWidthString = string.Empty;

    // TargetLogicalDpi (int?) + string proxy for TextBox binding
    [ObservableProperty]
    private int? targetLogicalDpi;

    [ObservableProperty]
    private string targetLogicalDpiString = string.Empty;

    // EPP override — null = inherit, true = on, false = off
    [ObservableProperty]
    private bool? eppOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetManagerEnabled))]
    [NotifyPropertyChangedFor(nameof(IsAutoSyncAvailable))]
    [NotifyCanExecuteChangedFor(nameof(OpenTargetManagerCommand))]
    private bool isGlobalDefault;

    // ── Derived display ─────────────────────────────────────────────────────
    /// <summary>
    /// Raw-Accel-only proof: (Sens × envScreenRatio × envDpiRatio) / S_base × 100.
    /// Shows how well raw accel sensitivity alone offsets the full environmental delta
    /// (screen resolution + logical DPI) before Win Speed or Registry Bridge contribute.
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
            UseAutoRawSync        = profile.UseAutoRawSync;
            UseAutoOSSync         = profile.UseAutoOSSync;
            WinPointerSpeed       = profile.WinPointerSpeed;
            UseAutoRegSync        = profile.UseAutoRegSync;
            RegistryCurveOverride = profile.RegistryCurveOverride;
            RegistryCurveString   = profile.RegistryCurveOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TargetScreenWidth       = profile.TargetScreenWidth;
            TargetScreenWidthString = profile.TargetScreenWidth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TargetLogicalDpi        = profile.TargetLogicalDpi;
            TargetLogicalDpiString  = profile.TargetLogicalDpi?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            EppOverride             = profile.UseEnhancePointerPrecision;
            IsGlobalDefault       = profile.IsGlobalDefault;
        }
        finally
        {
            _isSyncing = false;
        }

        RefreshAssociationsSummary();
        OpenTargetManagerCommand.NotifyCanExecuteChanged();
        ApplyAutoOSSync();         // re-run with correct IsGlobalDefault + all loaded values
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
        AutoPersist();             // persist before recalc so GetEffectiveSensitivity sees current model
        RecalculateMatchAccuracy();
    }

    partial void OnSensitivityStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            Sensitivity = null;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            _isSyncing = true;
            Sensitivity = parsed;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnUseAutoOSSyncChanged(bool oldValue, bool newValue)
    {
        if (_isSyncing) return;
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnWinPointerSpeedChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;

        // Manual speed edit while AutoOSSync is on -> honour auto, ignore manual input
        if (UseAutoOSSync)
        {
            ApplyAutoOSSync();
            AutoPersist();
            RecalculateMatchAccuracy();
            return;
        }

        ApplyAutoRegSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnUseAutoRegSyncChanged(bool oldValue, bool newValue)
    {
        if (_isSyncing) return;
        ApplyAutoRegSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnRegistryCurveOverrideChanged(double? oldValue, double? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        RegistryCurveString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnRegistryCurveStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            RegistryCurveOverride = null;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            _isSyncing = true;
            RegistryCurveOverride = parsed;
            _isSyncing = false;
        }

        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetScreenWidthChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        TargetScreenWidthString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetScreenWidthStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            TargetScreenWidth = null;
            return;
        }

        if (int.TryParse(newValue, out var parsed) && parsed > 0)
        {
            _isSyncing = true;
            TargetScreenWidth = parsed;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetLogicalDpiChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        TargetLogicalDpiString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetLogicalDpiStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            TargetLogicalDpi = null;
            return;
        }

        if (int.TryParse(newValue, out var parsed) && parsed > 0)
        {
            _isSyncing = true;
            TargetLogicalDpi = parsed;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnEppOverrideChanged(bool? oldValue, bool? newValue)
    {
        if (_isSyncing) return;
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnUseAutoRawSyncChanged(bool oldValue, bool newValue)
    {
        if (_isSyncing) return;
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
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

    /// <summary>WinPointerSpeed index (1-20) of the Global Default (fallback 10 = neutral).</summary>
    private int GetBaseWinPointerSpeed() => GetBaseProfile()?.WinPointerSpeed ?? 10;

    /// <summary>RegistryCurveOverride of the Global Default (fallback 1.0 = neutral).</summary>
    private double GetBaseRegCurve()
    {
        var r = GetBaseProfile()?.RegistryCurveOverride ?? 1.0;
        return r == 0 ? 1.0 : r;
    }

    /// <summary>TargetScreenWidth of the Global Default. Null if not declared.</summary>
    private int? GetBaseScreenWidth() => GetBaseProfile()?.TargetScreenWidth;

    /// <summary>TargetLogicalDpi of the Global Default. Null if not declared (no DPI normalization).</summary>
    private int? GetBaseLogicalDpi() => GetBaseProfile()?.TargetLogicalDpi;


    /// <summary>
    /// Auto Raw Sensitivity: compute the exact raw sens so that
    ///   Sens × envScreenRatio × envDpiRatio == S_base
    /// giving Raw Accel Only = 100% before Win Speed or Reg Bridge contribute.
    /// </summary>
    private void ApplyAutoRawSync()
    {
        if (IsGlobalDefault || !UseAutoRawSync) return;

        var sBase  = GetBaseSens();
        var bp     = GetBaseProfile();

        var useEpp     = EppOverride ?? bp?.UseEnhancePointerPrecision;
        var logicalDpi = TargetLogicalDpi ?? GetBaseLogicalDpi() ?? 96;

        double envScreenRatio = 1.0;
        var baseW   = GetBaseScreenWidth();
        var activeW = TargetScreenWidth ?? baseW;
        if (baseW.HasValue && activeW.HasValue && baseW.Value > 0)
            envScreenRatio = (double)baseW.Value / activeW.Value;

        double envDpiRatio = (useEpp != false) ? (96.0 / logicalDpi) : 1.0;

        // Target: Sens × envScreenRatio × envDpiRatio = S_base
        var envFactor = envScreenRatio * envDpiRatio;
        var newSens   = envFactor == 0 ? sBase : sBase / envFactor;

        var wasSyncing = _isSyncing;
        _isSyncing = true;
        Sensitivity       = newSens;
        SensitivityString = newSens.ToString("G6", CultureInfo.InvariantCulture);
        _isSyncing = wasSyncing;
    }

    /// <summary>
    /// Auto Win Pointer Speed: choose the WinMult closest to Effective(base) / S_target,
    /// so that S_target × WinMult ≈ Effective(base) — compensating the raw DPI change as
    /// closely as possible using only the integer Win Pointer Speed steps.
    /// </summary>
    private void ApplyAutoOSSync()
    {
        if (IsGlobalDefault) { ApplyAutoRegSync(); return; }

        ApplyAutoRawSync(); // runs first — updates Sensitivity before Win Speed target is computed

        if (UseAutoOSSync)
        {
            var bp      = GetBaseProfile();
            var baseEff = bp != null ? _profileService.GetEffectiveSensitivity(bp) : GetBaseSens();
            if (baseEff == 0) baseEff = 1.0;

            // Null Sensitivity = inherit base raw sens — target is still Effective(base) / baseSens = baseWinMult*baseReg
            var sTarget = Sensitivity ?? GetBaseSens();
            if (sTarget == 0) sTarget = 1.0;

            // Env correction: DPI and screen width change the feel, so Win Speed should absorb
            // as much of the cross-environment delta as the 20 discrete steps allow.
            // envCorr = dpi/96 × activeW/baseW — the inverse of what GetEffectiveSensitivity divides by.
            var activeEppRaw = EppOverride ?? bp?.UseEnhancePointerPrecision;
            var activeDpiRaw = TargetLogicalDpi ?? GetBaseLogicalDpi() ?? 96;
            var baseWRaw     = GetBaseScreenWidth();
            var activeWRaw   = TargetScreenWidth ?? baseWRaw;
            double envCorrWin = 1.0;
            if (activeEppRaw != false) envCorrWin *= (double)activeDpiRaw / 96.0;
            if (baseWRaw.HasValue && activeWRaw.HasValue && baseWRaw.Value > 0)
                envCorrWin *= (double)activeWRaw.Value / baseWRaw.Value;

            var target = (baseEff * envCorrWin) / sTarget;

            int bestIndex  = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < ProfileService.WinMultipliers.Length; i++)
            {
                double diff = Math.Abs(ProfileService.WinMultipliers[i] - target);
                if (diff < minDiff) { minDiff = diff; bestIndex = i; }
            }

            var wasSyncing = _isSyncing;
            _isSyncing = true;
            WinPointerSpeed = bestIndex + 1;
            _isSyncing = wasSyncing;
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

            // Null Sensitivity/WinPointerSpeed = inherit base values so registry compensates the real remainder
            var sTarget  = Sensitivity ?? GetBaseSens();
            if (sTarget == 0) sTarget = 1.0;

            var winIndex  = Math.Clamp((WinPointerSpeed ?? GetBaseWinPointerSpeed()) - 1, 0, ProfileService.WinMultipliers.Length - 1);
            var winMult   = ProfileService.WinMultipliers[winIndex];
            var raw       = (sTarget * winMult) == 0 ? 1.0 : baseEff / (sTarget * winMult);

            // Env correction: scale regResult so that GetEffectiveSensitivity(active) == GetEffectiveSensitivity(base).
            var activeDpi = TargetLogicalDpi ?? GetBaseLogicalDpi() ?? 96;
            var baseW     = GetBaseScreenWidth();
            var activeW   = TargetScreenWidth ?? baseW;

            var useEppReg = EppOverride ?? bp?.UseEnhancePointerPrecision;
            double envCorrection = (useEppReg != false) ? (double)activeDpi / 96.0 : 1.0;
            if (baseW.HasValue && activeW.HasValue && baseW.Value > 0)
                envCorrection *= (double)activeW.Value / baseW.Value;

            var remainder = raw * envCorrection;

            var wasSyncing = _isSyncing;
            _isSyncing = true;
            RegistryCurveOverride = remainder;
            RegistryCurveString   = remainder.ToString("G6", CultureInfo.InvariantCulture);
            _isSyncing = wasSyncing;
        }
    }
    /// <summary>
    /// Three-row waterfall proof — all rows measure against Effective(base).
    ///
    ///   Raw Accel Only  : (curSens × envScreenRatio × envDpiRatio) / S_base
    ///       How well raw accel sensitivity alone offsets the full environmental delta
    ///       (screen width + logical DPI) before Win Speed or Reg Bridge contribute.
    ///
    ///   Win Speed Only  : (curSens × WinMult) / Effective(base)
    ///       How close integer Win Pointer Speed steps get you back to Effective(base).
    ///       Auto-Win targets Effective(base) / S_target.
    ///
    ///   Full Match      : (curSens × WinMult × EncodeRegCurve(regCurve)) / Effective(base)
    ///       The terminal accuracy after the registry bridge fine-tunes the remainder.
    ///       EncodeRegCurve round-trips through 16.16 fixed-point — this is the real
    ///       value Windows stores, revealing the ~6.1e-6 (0.0006%) precision floor.
    ///       100.00% = exact compensation across all three layers.
    /// </summary>
    private void RecalculateMatchAccuracy()
    {
        var bp      = GetBaseProfile();
        var sBase   = GetBaseSens();

        var fullBase = bp != null ? _profileService.GetEffectiveSensitivity(bp) : sBase;
        if (fullBase == 0) fullBase = 1.0;

        // Null fields inherit from Global Default — a fully-null profile is a perfect 100% match.
        var curSens     = Sensitivity ?? GetBaseSens();
        var curWinIndex = Math.Clamp((WinPointerSpeed ?? GetBaseWinPointerSpeed()) - 1, 0, ProfileService.WinMultipliers.Length - 1);
        var curWinMult  = ProfileService.WinMultipliers[curWinIndex];

        // EPP/DPI normalization — mirrors GetEffectiveSensitivity but for the currently displayed values.
        var useEpp     = EppOverride ?? bp?.UseEnhancePointerPrecision;
        var logicalDpi = TargetLogicalDpi ?? GetBaseLogicalDpi() ?? 96;

        // Use the 16.16-encoded reg curve — this is what Windows will actually apply.
        // Fallback is 1.0 (neutral); DPI correction is applied separately via envDpiRatio below,
        // matching the single-factor approach in GetEffectiveSensitivity.
        var rawRegCurve = RegistryCurveOverride ?? 1.0;
        var curRegCurve = MouseRegistryBridge.EncodeRegCurve(rawRegCurve);

        if (useEpp == false) curRegCurve = 1.0;

        // Screen width env ratio for Win Speed and Full Match rows.
        double envScreenRatio = 1.0;
        var baseW    = GetBaseScreenWidth();
        var profileW = TargetScreenWidth ?? baseW;
        if (baseW.HasValue && profileW.HasValue && profileW.Value > 0)
            envScreenRatio = (double)baseW.Value / profileW.Value;

        // DPI ratio — reverses Windows' logicalDpi/96 amplification.
        double envDpiRatio = (useEpp != false) ? (96.0 / logicalDpi) : 1.0;

        var rawEff  = curSens;
        var winEff  = rawEff  * curWinMult * envScreenRatio * envDpiRatio;
        var fullEff = winEff  * curRegCurve;

        // Row 1 display: raw sens scaled by ALL environment factors (screen width + DPI).
        // This shows how well raw accel alone offsets the full environmental delta,
        // before Win Speed or the Registry Bridge contribute anything.
        // Separate variable — rawEff is left as curSens so envScreenRatio/envDpiRatio
        // are not double-counted in the winEff/fullEff chain above.
        var rawEffDisplay = curSens * envScreenRatio * envDpiRatio;

        // Row 1: Raw Accel Only — all env factors, no Win Speed, no Reg Bridge.
        RawAccelMatchAccuracy = ((rawEffDisplay / sBase) * 100.0).ToString("G10", CultureInfo.InvariantCulture) + "%";
        WinSpeedMatchAccuracy = ((winEff  / fullBase) * 100.0).ToString("G10", CultureInfo.InvariantCulture) + "%";
        MatchAccuracy         = ((fullEff / fullBase) * 100.0).ToString("G10", CultureInfo.InvariantCulture) + "%";
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
        _editingProfile.UseAutoRawSync        = UseAutoRawSync;
        _editingProfile.UseAutoOSSync         = UseAutoOSSync;
        _editingProfile.WinPointerSpeed       = WinPointerSpeed;
        _editingProfile.UseAutoRegSync        = UseAutoRegSync;
        _editingProfile.RegistryCurveOverride = RegistryCurveOverride;
        _editingProfile.TargetScreenWidth             = TargetScreenWidth;
        _editingProfile.TargetLogicalDpi              = TargetLogicalDpi;
        _editingProfile.UseEnhancePointerPrecision    = EppOverride;
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
