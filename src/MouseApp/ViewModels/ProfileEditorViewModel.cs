using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly RawAccelSettingsRepository  _rawAccelSettingsRepository;
    private readonly ProfileService   _profileService;
    private readonly IWindowService   _windowService;

    /// <summary>Injected after construction via <see cref="WireMouseService"/>. Null until wired.</summary>
    private IMouseService? _mouseService;

    /// <summary>Raised after a profile is manually applied to hardware via Apply Now.</summary>
    public event Action<MouseProfile>? ProfileManuallyApplied;

    /// <summary>The profile currently loaded into the editor. Null when no profile is selected.</summary>
    private MouseProfile? _editingProfile;

    /// <summary>Guards against circular property-changed cascades during auto-sync.</summary>
    private bool _isSyncing;

    // ── Raw Accel settings-file management panel state ──────────────────────


    // ── Collections exposed to the View ────────────────────────────────────
    /// <summary>Null entry at index 0 allows users to unset/deselect a settings file.</summary>
    public ObservableCollection<string?> RawAccelSettingsFiles { get; } = new();

    /// <summary>Null entry allows a sparse (unset) selection at the top of the list.</summary>
    public ObservableCollection<int?> WinPointerOptions { get; } = new();

    /// <summary>EPP override options: null = Skip, true = Enable, false = Disable.</summary>
    public bool?[] EppOptions { get; } = { null, true, false };



    /// <summary>Preset numeric DPI values for the editable LogicalDpi ComboBox.</summary>
    public string[] DpiPresets { get; } = { "96", "120", "144", "168", "192", "240", "288" };

    /// <summary>Common screen widths for the editable ScreenWidth ComboBox.</summary>
    public string[] ScreenWidthPresets { get; } = { "1280", "1366", "1440", "1600", "1920", "2560", "3440", "3840", "5120", "7680" };

    // ── Profile fields ──────────────────────────────────────────────────────
    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    private string? selectedRawAccelSettingsFile;

    // Sensitivity (double?) + its string proxy for TextBox binding
    [ObservableProperty]
    private double? sensitivity;

    [ObservableProperty]
    private string sensitivityString = string.Empty;

    // Three-way sync modes: Auto, Manual, Skip
    [ObservableProperty]
    private SyncMode rawSyncMode;

    [ObservableProperty]
    private SyncMode oSSyncMode;

    [ObservableProperty]
    private int? winPointerSpeed;

    [ObservableProperty]
    private SyncMode regSyncMode;

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
    [NotifyPropertyChangedFor(nameof(IsAutoSyncAvailable))]
    private bool isDefault;

    // ── Derived display ─────────────────────────────────────────────────────
    /// <summary>
    /// Raw-Accel-only proof: (Sens × envScreenRatio × envDpiRatio) / S_base × 100.
    /// Shows how well raw accel sensitivity alone offsets the full environmental delta
    /// (screen resolution + logical DPI) before Win Speed or Registry Bridge contribute.
    /// </summary>
    [ObservableProperty]
    private string rawAccelMatchAccuracy = "N/A";

    /// <summary>
    /// Win-speed-only proof: (Sens × WinMult) / Effective(Default) × 100.
    /// Shows how close Raw Accel + WinPointerSpeed alone gets you, before the registry bridge.
    /// </summary>
    [ObservableProperty]
    private string winSpeedMatchAccuracy = "N/A";

    /// <summary>
    /// Full proof: (Sens × WinMult × RegCurve) / Effective(Default) × 100.
    /// 100.00% = exact compensation across all three factors.
    /// </summary>
    [ObservableProperty]
    private string matchAccuracy = "N/A";

    // ── Target Manager (read-only summary shown in the editor) ──────────────
    public ObservableCollection<TargetAssociation> TargetAssociationsSummary { get; } = new();

    /// <summary>True when Target Manager is interactive.</summary>
    public bool IsTargetManagerEnabled => true;

    /// <summary>
    /// Auto-sync only makes sense on non-Default profiles.
    /// The Default profile IS the reference — there is nothing to compensate relative to itself.
    /// </summary>
    public bool IsAutoSyncAvailable => !IsDefault;

    public bool IsRawAutoSync
    {
        get => !IsDefault && RawSyncMode == SyncMode.Auto;
        set
        {
            if (value)
                RawSyncMode = SyncMode.Auto;
            else
                RawSyncMode = Sensitivity.HasValue ? SyncMode.Manual : SyncMode.Skip;
            OnPropertyChanged();
        }
    }

    public bool IsOSAutoSync
    {
        get => !IsDefault && OSSyncMode == SyncMode.Auto;
        set
        {
            if (value)
                OSSyncMode = SyncMode.Auto;
            else
                OSSyncMode = WinPointerSpeed.HasValue ? SyncMode.Manual : SyncMode.Skip;
            OnPropertyChanged();
        }
    }

    public bool IsRegAutoSync
    {
        get => !IsDefault && RegSyncMode == SyncMode.Auto;
        set
        {
            if (value)
                RegSyncMode = SyncMode.Auto;
            else
                RegSyncMode = RegistryCurveOverride.HasValue ? SyncMode.Manual : SyncMode.Skip;
            OnPropertyChanged();
        }
    }

    /// <summary>True when the current profile has no target associations.</summary>
    public bool IsTargetAssociationsEmpty => TargetAssociationsSummary.Count == 0;

    // ── Commands ────────────────────────────────────────────────────────────
    public IAsyncRelayCommand SaveCommand              { get; }
    public IAsyncRelayCommand ApplyCommand             { get; }
    public IAsyncRelayCommand OpenTargetManagerCommand { get; }

    // Raw Accel settings-file management quick-actions
    public IAsyncRelayCommand ImportCurrentSettingsCommand { get; }
    public IAsyncRelayCommand DuplicateRawAccelSettingsCommand     { get; }
    public IAsyncRelayCommand RenameRawAccelSettingsCommand        { get; }
    public IRelayCommand      DeleteRawAccelSettingsCommand        { get; }
    public IRelayCommand      OpenRawAccelGuiCommand       { get; }

    // ── Constructor ─────────────────────────────────────────────────────────
    public ProfileEditorViewModel(RawAccelSettingsRepository rawAccelSettingsRepository, ProfileService profileService, IWindowService windowService)
    {
        _rawAccelSettingsRepository = rawAccelSettingsRepository ?? throw new ArgumentNullException(nameof(rawAccelSettingsRepository));
        _profileService  = profileService  ?? throw new ArgumentNullException(nameof(profileService));
        _windowService   = windowService   ?? throw new ArgumentNullException(nameof(windowService));

        RefreshRawAccelSettingsFiles();
        _rawAccelSettingsRepository.FilesChanged += (_, _) => RefreshRawAccelSettingsFiles();

        WinPointerOptions.Add(null); // sparse / unset sentinel
        for (int i = 1; i <= 20; i++) WinPointerOptions.Add(i);

        _profileService.Profiles.CollectionChanged += OnProfilesCollectionChanged;

        SaveCommand              = new AsyncRelayCommand(ExecuteSaveAsync);
        ApplyCommand             = new AsyncRelayCommand(ExecuteApplyAsync, () => _editingProfile != null);
        OpenTargetManagerCommand = new AsyncRelayCommand(ExecuteOpenTargetManagerAsync,
                                       () => _editingProfile != null);

        ImportCurrentSettingsCommand = new AsyncRelayCommand(ExecuteImportCurrentSettingsAsync);
        DuplicateRawAccelSettingsCommand     = new AsyncRelayCommand(ExecuteDuplicateRawAccelSettingsAsync, () => SelectedRawAccelSettingsFile != null);
        RenameRawAccelSettingsCommand        = new AsyncRelayCommand(ExecuteRenameRawAccelSettingsAsync,    () => SelectedRawAccelSettingsFile != null);
        DeleteRawAccelSettingsCommand        = new RelayCommand(ExecuteDeleteRawAccelSettings,    () => SelectedRawAccelSettingsFile != null);
        OpenRawAccelGuiCommand       = new RelayCommand(ExecuteOpenRawAccelGui);
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
            SelectedRawAccelSettingsFile      = profile.RawAccelSettingsFile;
            Sensitivity           = profile.SensMultiplier;
            SensitivityString     = profile.SensMultiplier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            RawSyncMode           = profile.RawSyncMode;
            OSSyncMode            = profile.OSSyncMode;
            WinPointerSpeed       = profile.WinPointerSpeed;
            RegSyncMode           = profile.RegSyncMode;
            RegistryCurveOverride = profile.RegistryCurveOverride;
            RegistryCurveString   = profile.RegistryCurveOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TargetScreenWidth       = profile.TargetScreenWidth;
            TargetScreenWidthString = profile.TargetScreenWidth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TargetLogicalDpi        = profile.TargetLogicalDpi;
            TargetLogicalDpiString  = profile.TargetLogicalDpi?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            EppOverride             = profile.UseEnhancePointerPrecision;
            IsDefault       = profile.IsDefault;
        }
        finally
        {
            _isSyncing = false;
        }

        RefreshAssociationsSummary();
        OpenTargetManagerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRawAutoSync));
        OnPropertyChanged(nameof(IsOSAutoSync));
        OnPropertyChanged(nameof(IsRegAutoSync));
        ApplyAutoOSSync();         // re-run with correct IsDefault + all loaded values
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
            if (RawSyncMode != SyncMode.Auto)
                RawSyncMode = SyncMode.Skip;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            _isSyncing = true;
            Sensitivity = parsed;
            _isSyncing = false;
        }

        if (RawSyncMode == SyncMode.Skip)
            RawSyncMode = SyncMode.Manual;

        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnOSSyncModeChanged(SyncMode oldValue, SyncMode newValue)
    {
        if (_isSyncing) return;
        if (newValue == SyncMode.Skip)
        {
            _isSyncing = true;
            WinPointerSpeed = null;
            _isSyncing = false;
        }
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
        OnPropertyChanged(nameof(IsOSAutoSync));
    }

    partial void OnWinPointerSpeedChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;

        // Manual speed edit while AutoOSSync is on -> honour auto, ignore manual input
        if (OSSyncMode == SyncMode.Auto)
        {
            ApplyAutoOSSync();
            AutoPersist();
            RecalculateMatchAccuracy();
            return;
        }

        if (newValue == null && OSSyncMode != SyncMode.Auto)
            OSSyncMode = SyncMode.Skip;
        else if (newValue != null && OSSyncMode == SyncMode.Skip)
            OSSyncMode = SyncMode.Manual;

        ApplyAutoRegSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnRegSyncModeChanged(SyncMode oldValue, SyncMode newValue)
    {
        if (_isSyncing) return;
        if (newValue == SyncMode.Skip)
        {
            _isSyncing = true;
            RegistryCurveOverride = null;
            RegistryCurveString   = string.Empty;
            _isSyncing = false;
        }
        ApplyAutoRegSync();
        AutoPersist();
        RecalculateMatchAccuracy();
        OnPropertyChanged(nameof(IsRegAutoSync));
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
            if (RegSyncMode != SyncMode.Auto)
                RegSyncMode = SyncMode.Skip;
            return;
        }

        if (double.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            _isSyncing = true;
            RegistryCurveOverride = parsed;
            _isSyncing = false;
        }

        if (RegSyncMode == SyncMode.Skip)
            RegSyncMode = SyncMode.Manual;

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

    partial void OnRawSyncModeChanged(SyncMode oldValue, SyncMode newValue)
    {
        if (_isSyncing) return;
        if (newValue == SyncMode.Skip)
        {
            _isSyncing = true;
            Sensitivity       = null;
            SensitivityString = string.Empty;
            _isSyncing = false;
        }
        OnPropertyChanged(nameof(IsRawAutoSync));
        ApplyAutoOSSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnSelectedRawAccelSettingsFileChanged(string? oldValue, string? newValue)
    {
        AutoPersist();
        DuplicateRawAccelSettingsCommand.NotifyCanExecuteChanged();
        RenameRawAccelSettingsCommand.NotifyCanExecuteChanged();
        DeleteRawAccelSettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDefaultChanged(bool oldValue, bool newValue)
    {
        if (_isSyncing || _editingProfile == null) return;

        OnPropertyChanged(nameof(IsAutoSyncAvailable));
        OnPropertyChanged(nameof(IsRawAutoSync));
        OnPropertyChanged(nameof(IsOSAutoSync));
        OnPropertyChanged(nameof(IsRegAutoSync));

        if (newValue)
        {
            // SetDefaultAsync atomically clears all others and saves.
            _ = _profileService.SetDefaultAsync(_editingProfile);
        }
        else
        {
            // Prevent unchecking when this is the only global default — we must always
            // have exactly one. Revert the checkbox silently and bail.
            bool isOnlyDefault = _profileService.Profiles.Count(p => p.IsDefault) <= 1
                                 && _editingProfile.IsDefault;
            if (isOnlyDefault)
            {
                _isSyncing = true;
                IsDefault = true;  // revert the VM back to true
                _isSyncing = false;
                return;
            }
            // Profile was previously default but another is now: just persist the false.
            _editingProfile.IsDefault = false;
            _ = _profileService.SaveAsync();
        }
    }

    // ── Math Engine ─────────────────────────────────────────────────────────

    private MouseProfile? GetBaseProfile() =>
        _profileService.Profiles.FirstOrDefault(p => p.IsDefault)
        ?? _profileService.Profiles.FirstOrDefault();

    /// <summary>Raw Accel SensMultiplier of the Default profile (fallback 1.0).</summary>
    private double GetBaseSens()
    {
        var s = GetBaseProfile()?.SensMultiplier ?? 1.0;
        return s == 0 ? 1.0 : s;
    }

    /// <summary>WinPointerSpeed index (1-20) of the Default profile (fallback 10 = neutral).</summary>
    private int GetBaseWinPointerSpeed() => GetBaseProfile()?.WinPointerSpeed ?? 10;

    /// <summary>RegistryCurveOverride of the Default profile (fallback 1.0 = neutral).</summary>
    private double GetBaseRegCurve()
    {
        var r = GetBaseProfile()?.RegistryCurveOverride ?? 1.0;
        return r == 0 ? 1.0 : r;
    }

    /// <summary>TargetScreenWidth of the Default profile. Null if not declared.</summary>
    private int? GetBaseScreenWidth() => GetBaseProfile()?.TargetScreenWidth;

    /// <summary>TargetLogicalDpi of the Default profile. Null if not declared (no DPI normalization).</summary>
    private int? GetBaseLogicalDpi() => GetBaseProfile()?.TargetLogicalDpi;


    /// <summary>
    /// Auto Raw Sensitivity: compute the exact raw sens so that
    ///   Sens × envScreenRatio × envDpiRatio == S_base
    /// giving Raw Accel Only = 100% before Win Speed or Reg Bridge contribute.
    /// </summary>
    private void ApplyAutoRawSync()
    {
        if (IsDefault || RawSyncMode != SyncMode.Auto) return;

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
        if (IsDefault) { ApplyAutoRegSync(); return; }

        ApplyAutoRawSync(); // runs first — updates Sensitivity before Win Speed target is computed

        if (OSSyncMode == SyncMode.Auto)
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
        if (IsDefault) return;

        if (RegSyncMode == SyncMode.Auto)
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
        // Nothing meaningful to preview yet — mirrors AutoPersist's own early-return.
        if (_editingProfile == null) return;

        var bp      = GetBaseProfile();
        var sBase   = GetBaseSens();

        double fullBase;
        ProfileService.EffectiveSensitivityFactors factors;
        try
        {
            fullBase = bp != null ? _profileService.GetEffectiveSensitivity(bp) : sBase;
            if (fullBase == 0) fullBase = 1.0;

            // AutoPersist() already wrote every current field onto _editingProfile before this
            // runs (see the On*Changed ordering rule above), so the exact same math-engine
            // calculation used at apply-time can be reused here directly instead of a
            // hand-rolled duplicate of the formula — one implementation, shared by the real
            // apply path and this preview.
            factors = _profileService.GetEffectiveSensitivityFactors(_editingProfile);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A typed value is extreme enough that the registry curve's 16.16 fixed-point
            // encoding would overflow (QuantizeToFixedPoint1616's real, intentional guard —
            // see CLAUDE.md). This is a genuine "can't even predict this" state, not a display
            // bug — show it plainly instead of letting the exception crash the whole app.
            // MouseService.ApplyProfileAsync already handles this exact exception the same way
            // (catch + no-op) for the real write; this mirrors that for the live preview, which
            // has no equivalent try/catch around it and would otherwise take the app down just
            // from typing a number into a field.
            RawAccelMatchAccuracy = "Overflow%";
            WinSpeedMatchAccuracy = "Overflow%";
            MatchAccuracy = "Overflow%";
            return;
        }

        // Still needed directly (not part of the shared formula): useEpp gates whether the
        // Full Match row's decimal precision responds to the registry curve's live step below,
        // and rawRegCurve is the raw (unencoded) value EncodeRegCurve needs to derive that step.
        var useEpp      = EppOverride ?? bp?.UseEnhancePointerPrecision;
        var rawRegCurve = RegistryCurveOverride ?? 1.0;

        var winEff  = factors.WinOnly;
        var fullEff = factors.Full;

        // Row 1 display: raw sens scaled by ALL environment factors (screen width + DPI).
        // This shows how well raw accel alone offsets the full environmental delta,
        // before Win Speed or the Registry Bridge contribute anything.
        var rawEffDisplay = factors.RawOnly;

        // Display precision reflects real-world distinguishability, not an arbitrary digit
        // count. Raw Accel Only / Win Speed Only never touch the registry curve, and Raw Accel
        // itself has no precision floor of its own to derive a digit count from — verified
        // against Raw Accel's own source (its Sensitivity Multiplier field is a plain, unbounded
        // double; see CLAUDE.md). So these rows (and the Full Match row whenever EPP is off, since
        // the registry curve is forced neutral with no quantization in that case) get no
        // domain-specific cap at all — just the double-precision budget below.
        //
        // A `double` only ever holds ~15-17 real significant decimal digits, regardless of
        // domain or of whether those digits land before or after the decimal point. Fixed-
        // point notation stops being honest once a value is so large that even its integer
        // digits exceed that budget, or so small that the domain-appropriate decimal count
        // would round it away to "0.0000%" and hide a real nonzero value. In both cases,
        // scientific notation (matching what "%" values naturally do at extreme magnitudes)
        // shows exactly the real significant digits instead of fabricated ones.
        static string FormatMatchPercent(double value, int domainDecimalPlaces)
        {
            const int doubleSignificantDigitBudget = 15;
            // Clamp immediately, before any formatting: a caller passing "no domain cap"
            // (int.MaxValue, see below) must never reach ToString($"F{...}")/$"E{...}" with that
            // raw value — .NET throws FormatException for an out-of-range precision specifier.
            // Clamping once here, ahead of every return path (including the value==0 case),
            // means neither branch below can be the one that forgets to.
            domainDecimalPlaces = Math.Min(domainDecimalPlaces, doubleSignificantDigitBudget);

            // '#' (not '0') after the decimal point omits trailing zeros instead of padding
            // them out to the cap. The cap itself (places) is still the real precision ceiling
            // — a domain floor when one genuinely exists, double's own budget otherwise — this
            // only removes digits that happen to be zero, never digits that actually
            // distinguish the value. Without this, a clean value like a typed 1.12312312
            // sensitivity displayed as "112.312312000000%" instead of "112.312312%", implying
            // precision that was never real — exactly the kind of fake digit this whole
            // formatting rework exists to avoid.
            static string FixedFormat(int places) => places > 0 ? "0." + new string('#', places) : "0";

            if (double.IsNaN(value)) return "NaN%";
            if (double.IsInfinity(value)) return (value > 0 ? "Infinity" : "-Infinity") + "%";
            if (value == 0) return (0.0).ToString(FixedFormat(domainDecimalPlaces), CultureInfo.InvariantCulture) + "%";

            var absValue = Math.Abs(value);
            int integerDigits = absValue < 1 ? 1 : (int)Math.Floor(Math.Log10(absValue)) + 1;

            bool tooLargeForFixed = integerDigits > doubleSignificantDigitBudget;
            bool tooSmallForFixed = Math.Round(value, domainDecimalPlaces) == 0;

            if (tooLargeForFixed || tooSmallForFixed)
                return value.ToString($"E{domainDecimalPlaces}", CultureInfo.InvariantCulture) + "%";

            int decimalPlaces = Math.Min(domainDecimalPlaces, Math.Max(0, doubleSignificantDigitBudget - integerDigits));
            return value.ToString(FixedFormat(decimalPlaces), CultureInfo.InvariantCulture) + "%";
        }

        // No domain-specific floor for either row (see comment above) — int.MaxValue means
        // FormatMatchPercent's own double-precision budget is the only cap in effect.
        const int noDomainFloor = int.MaxValue;
        var rawAccelPercent = (rawEffDisplay / sBase) * 100.0;
        var winSpeedPercent = (winEff / fullBase) * 100.0;
        RawAccelMatchAccuracy = FormatMatchPercent(rawAccelPercent, noDomainFloor);
        WinSpeedMatchAccuracy = FormatMatchPercent(winSpeedPercent, noDomainFloor);

        // Full Match additionally goes through the registry curve when EPP is active, whose
        // live-computed quantization step IS a real, verified precision floor (unlike Raw
        // Accel's) — measure the actual percentage-point delta it produces for this profile
        // rather than assuming a fixed digit count. When EPP is off the curve is neutral (no
        // quantization at all), so fall back to the same uncapped double-precision budget.
        var fullMatchPercent = (fullEff / fullBase) * 100.0;
        int fullMatchDecimalPlaces = noDomainFloor;
        // Skip the nudge entirely when fullMatchPercent isn't finite (NaN/Infinity) — it's
        // already going to display as "NaN%"/"Infinity%" regardless of decimal count, and
        // computing it would call EncodeRegCurve(rawRegCurve, ...) a second time with the exact
        // same rawRegCurve that likely already overflowed once inside GetEffectiveSensitivityFactors
        // (that's what made fullMatchPercent non-finite in the first place). This is a second,
        // separate call site to the same throwing function — not covered by the try/catch
        // around GetEffectiveSensitivityFactors above — found live when it crashed the app the
        // same way. The try/catch below is kept anyway as a last-resort safety net.
        if (useEpp != false && double.IsFinite(fullMatchPercent))
        {
            try
            {
                MouseRegistryBridge.EncodeRegCurve(rawRegCurve, out double regStep);
                var nudgedPercent = (winEff * (factors.RegCurve + regStep) / fullBase) * 100.0;
                var deltaPercent  = Math.Abs(nudgedPercent - fullMatchPercent);
                if (deltaPercent > 0)
                    fullMatchDecimalPlaces = Math.Max(0, (int)Math.Ceiling(-Math.Log10(deltaPercent)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Precision refinement only — fall back to the uncapped double-precision budget.
            }
        }
        MatchAccuracy = FormatMatchPercent(fullMatchPercent, fullMatchDecimalPlaces);
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
        _editingProfile.RawAccelSettingsFile      = SelectedRawAccelSettingsFile;
        _editingProfile.SensMultiplier        = Sensitivity;
        _editingProfile.RawSyncMode           = RawSyncMode;
        _editingProfile.OSSyncMode            = OSSyncMode;
        _editingProfile.WinPointerSpeed       = WinPointerSpeed;
        _editingProfile.RegSyncMode           = RegSyncMode;
        _editingProfile.RegistryCurveOverride = RegistryCurveOverride;
        _editingProfile.TargetScreenWidth             = TargetScreenWidth;
        _editingProfile.TargetLogicalDpi              = TargetLogicalDpi;
        _editingProfile.UseEnhancePointerPrecision    = EppOverride;
        // IsDefault is intentionally NOT written here.
        // It is exclusively managed by OnIsDefaultChanged → SetDefaultAsync,
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
        ProfileManuallyApplied?.Invoke(_editingProfile);
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
        // In normal operation AutoPersist handles all saves; IsDefault is managed
        // exclusively by OnIsDefaultChanged \u2192 SetDefaultAsync, never here.
        if (_editingProfile == null)
        {
            var draft = new MouseProfile
            {
                ProfileName           = ProfileName,
                RawAccelSettingsFile      = SelectedRawAccelSettingsFile,
                SensMultiplier        = Sensitivity,
                OSSyncMode            = OSSyncMode,
                WinPointerSpeed       = WinPointerSpeed,
                RegSyncMode           = RegSyncMode,
                RegistryCurveOverride = RegistryCurveOverride,
                IsDefault       = false, // new profiles are never the default on creation
                TargetAssociations    = new()
            };
            _profileService.Profiles.Add(draft);
            _editingProfile = draft;
        }

        AutoPersist();
        await Task.CompletedTask;
    }

    // ── Raw Accel settings-file management execute methods ──────────────────

    /// <summary>
    /// Returns the configured Raw Accel install directory, or the portable default (app folder)
    /// if unset -- e.g. when Raw Accel's binaries are bundled alongside MouseApp itself.
    /// </summary>
    private string ResolveRawAccelDirectory()
    {
        var configured = _profileService.Settings?.RawAccelDirectory;
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : AppContext.BaseDirectory;
    }

    private void ExecuteOpenRawAccelGui()
    {
        if (!OperatingSystem.IsWindows()) return;

        var rawAccelDir = ResolveRawAccelDirectory();
        var exePath = Path.Combine(rawAccelDir, "rawaccel.exe");
        if (!File.Exists(exePath)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                WorkingDirectory = rawAccelDir,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort launch; swallow failures (e.g. missing exe, permission issues)
        }
    }

    private async Task ExecuteImportCurrentSettingsAsync()
    {
        var rawAccelDir = ResolveRawAccelDirectory();

        var name = await _windowService.ShowRawAccelSettingsNameDialogAsync("Save current Raw Accel settings as:", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;

        var sourcePath = Path.Combine(rawAccelDir, "settings.json");
        var result = _rawAccelSettingsRepository.Import(sourcePath, name);
        if (result != null)
            SelectedRawAccelSettingsFile = result;
    }

    private async Task ExecuteDuplicateRawAccelSettingsAsync()
    {
        if (SelectedRawAccelSettingsFile == null) return;
        var baseName = SelectedRawAccelSettingsFile.Replace(".json", string.Empty,
                           StringComparison.OrdinalIgnoreCase);

        var name = await _windowService.ShowRawAccelSettingsNameDialogAsync("Duplicate settings as:", baseName + " - copy");
        if (string.IsNullOrWhiteSpace(name)) return;

        var result = _rawAccelSettingsRepository.Duplicate(SelectedRawAccelSettingsFile, name);
        if (result != null)
            SelectedRawAccelSettingsFile = result;
    }

    private async Task ExecuteRenameRawAccelSettingsAsync()
    {
        if (SelectedRawAccelSettingsFile == null) return;
        var oldName  = SelectedRawAccelSettingsFile;
        var baseName = oldName.Replace(".json", string.Empty, StringComparison.OrdinalIgnoreCase);

        var name = await _windowService.ShowRawAccelSettingsNameDialogAsync("Rename settings to:", baseName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var result = _rawAccelSettingsRepository.Rename(oldName, name);
        if (result == null) return; // rename failed (collision, blacklisted, or missing source)

        // Keep every profile that referenced the old filename pointed at the renamed file.
        await _profileService.RetargetSettingsFileReferencesAsync(oldName, result);

        SelectedRawAccelSettingsFile = result;
    }

    private void ExecuteDeleteRawAccelSettings()
    {
        if (SelectedRawAccelSettingsFile == null) return;
        var toDelete = SelectedRawAccelSettingsFile;
        _rawAccelSettingsRepository.Delete(toDelete);
        // RefreshRawAccelSettingsFiles fires via FilesChanged; clear selection if it still references deleted item
        if (SelectedRawAccelSettingsFile == toDelete)
            SelectedRawAccelSettingsFile = null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void RefreshRawAccelSettingsFiles()
    {
        RawAccelSettingsFiles.Clear();
        RawAccelSettingsFiles.Add(null); // Blank/unset entry so users can deselect a settings file
        foreach (var t in _rawAccelSettingsRepository.GetAll())
            RawAccelSettingsFiles.Add(t);
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RecalculateMatchAccuracy();
}
