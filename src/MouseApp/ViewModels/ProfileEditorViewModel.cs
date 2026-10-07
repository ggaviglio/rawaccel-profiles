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



    /// <summary>Common screen widths for the editable ScreenWidth ComboBox.</summary>
    public string[] ScreenWidthPresets { get; } = { "640", "800", "1024", "1152", "1280", "1366", "1440", "1600", "1680", "1920", "2560", "3440", "3840", "5120", "7680" };

    /// <summary>Common screen heights for the editable ScreenHeight ComboBox.</summary>
    public string[] ScreenHeightPresets { get; } = { "480", "600", "720", "768", "800", "864", "900", "1024", "1050", "1080", "1200", "1440", "2160", "4320" };

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

    /// <summary>False while SensitivityString holds text that failed the shared
    /// TryParsePositiveDouble check — a rejected keystroke, not a new validation rule; see
    /// OnSensitivityStringChanged. Drives an error indicator in the View; Sensitivity itself
    /// (the value actually used everywhere) never gets set to the invalid text.</summary>
    [ObservableProperty]
    private bool isSensitivityInputValid = true;

    // Three-way sync modes: Auto, Manual, Skip
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawAutoSync))]
    private SyncMode rawSyncMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOSAutoSync))]
    private SyncMode oSSyncMode;

    [ObservableProperty]
    private int? winPointerSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegAutoSync))]
    [NotifyPropertyChangedFor(nameof(IsYXRatioAutoSync))]
    private SyncMode regSyncMode;

    [ObservableProperty]
    private double? registryCurveOverride;

    [ObservableProperty]
    private string registryCurveString = string.Empty;

    /// <summary>Same idea as <see cref="IsSensitivityInputValid"/>, for RegistryCurveString.</summary>
    [ObservableProperty]
    private bool isRegistryCurveInputValid = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYXRatioAutoSync))]
    private SyncMode yXRatioSyncMode;

    [ObservableProperty]
    private double? yXRatioOverride;

    [ObservableProperty]
    private string yXRatioString = string.Empty;

    /// <summary>Same idea as <see cref="IsSensitivityInputValid"/>, for YXRatioString.</summary>
    [ObservableProperty]
    private bool isYXRatioInputValid = true;

    // ── Environment fields ──────────────────────────────────────────────────

    // TargetScreenWidth (int?) + string proxy for TextBox binding
    [ObservableProperty]
    private int? targetScreenWidth;

    [ObservableProperty]
    private string targetScreenWidthString = string.Empty;

    /// <summary>Same idea as <see cref="IsSensitivityInputValid"/>, for TargetScreenWidthString.</summary>
    [ObservableProperty]
    private bool isTargetScreenWidthInputValid = true;

    // TargetScreenHeight (int?) + string proxy for TextBox binding
    [ObservableProperty]
    private int? targetScreenHeight;

    [ObservableProperty]
    private string targetScreenHeightString = string.Empty;

    /// <summary>Same idea as <see cref="IsSensitivityInputValid"/>, for TargetScreenHeightString.</summary>
    [ObservableProperty]
    private bool isTargetScreenHeightInputValid = true;

    // EPP override — null = inherit, true = on, false = off
    [ObservableProperty]
    private bool? eppOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoSyncAvailable))]
    [NotifyPropertyChangedFor(nameof(IsRawAutoAvailable))]
    [NotifyPropertyChangedFor(nameof(IsYXRatioAutoAvailable))]
    [NotifyPropertyChangedFor(nameof(IsRawAutoSync))]
    [NotifyPropertyChangedFor(nameof(IsOSAutoSync))]
    [NotifyPropertyChangedFor(nameof(IsRegAutoSync))]
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

    /// <summary>
    /// Auto-sync only makes sense on non-Default profiles.
    /// The Default profile IS the reference — there is nothing to compensate relative to itself.
    /// </summary>
    public bool IsAutoSyncAvailable => !IsDefault;

    /// <summary>
    /// Advanced-option visibility, gated behind Settings (hidden by default -- see
    /// AppSettings.ShowAutoMatchEnvironment/ShowEppOverride). Read straight from
    /// _profileService.Settings rather than cached locally, and refreshed via the
    /// SettingsChanged subscription in the constructor, so toggling either Settings switch
    /// updates an already-open Profile Editor immediately, no restart needed.
    /// </summary>
    public bool IsAutoMatchEnvironmentVisible => _profileService.Settings.ShowAutoMatchEnvironment;

    /// <summary>Same idea as <see cref="IsAutoMatchEnvironmentVisible"/>, for the EPP Override row.</summary>
    public bool IsEppOverrideVisible => _profileService.Settings.ShowEppOverride;

    /// <summary>Same idea as <see cref="IsEppOverrideVisible"/>, for the Y/X Ratio row. Gated on
    /// ShowYXRatio alone: a manually-typed ratio is meaningful on its own, so the field stays
    /// usable with Auto-Match Environment off. Only the Auto toggle needs that setting -- see
    /// <see cref="IsYXRatioAutoAvailable"/>, which mirrors <see cref="IsRawAutoAvailable"/>.</summary>
    public bool IsYXRatioVisible => _profileService.Settings.ShowYXRatio;

    /// <summary>Whether the Y/X Ratio row's Auto toggle is offered at all. Direct mirror of
    /// <see cref="IsRawAutoAvailable"/>: Auto derives the ratio from this profile's Screen
    /// Width/Height against the Default's, so it is meaningless with those hidden
    /// (ShowAutoMatchEnvironment off). The field itself stays -- hiding a whole row because one
    /// of its modes is unavailable is what made the Y/X Settings toggle look broken.</summary>
    public bool IsYXRatioAutoAvailable =>
        IsAutoSyncAvailable && _profileService.Settings.ShowAutoMatchEnvironment && _profileService.Settings.ShowYXRatio;

    /// <summary>Same idea as <see cref="IsAutoMatchEnvironmentVisible"/>, for the base EPP row.</summary>
    public bool IsEppVisible => _profileService.Settings.ShowEpp;

    /// <summary>
    /// Raw Accel's own Auto toggle specifically needs Auto-Match Environment too, unlike Windows
    /// Pointer's and EPP's Auto toggles. Raw Accel's Auto solves "what Sensitivity cancels the
    /// *environment* difference" -- with no environment configured anywhere (ShowAutoMatchEnvironment
    /// off), that always trivializes to "= the Default's Sensitivity," a meaningless no-op to
    /// present as a working feature. Windows Pointer's/EPP's Auto instead solve "what compensates
    /// whatever Sensitivity turned out to be," which stays meaningful regardless of environment,
    /// so they don't need this same gating. Drives IsVisible on the toggle itself only -- the
    /// match% text next to it stays visible regardless (it's useful information about the
    /// current value either way, auto-computed or manually typed). Using real IsVisible here
    /// instead of the Opacity-based approach an earlier version used matters for exactly this:
    /// the toggle's Auto-sized column properly collapses to nothing when hidden, so match% sits
    /// directly next to the textbox instead of leaving a reserved-but-empty gap where the button
    /// used to be.
    /// </summary>
    public bool IsRawAutoAvailable => IsAutoSyncAvailable && _profileService.Settings.ShowAutoMatchEnvironment;

    /// <summary>
    /// Reports Auto as engaged only when it's actually available too (ShowAutoMatchEnvironment
    /// on), not just when RawSyncMode happens to be Auto. RawSyncMode itself is deliberately never
    /// reset just because Environment gets hidden -- ApplyAutoRawSync's guard already stops it
    /// from recomputing while unavailable, and doing it here too (in the getter, not the stored
    /// value) means re-enabling the setting resumes Auto automatically, with nothing to
    /// re-select. This one property already drives both the toggle's checked state and (via
    /// !IsRawAutoSync in the View) the Sensitivity textbox's lock state, so folding the
    /// availability check in here fixes both at once -- found live: without it, a profile with
    /// Auto on when Environment got hidden left the textbox permanently locked with no way to
    /// reach the one control (the now-dimmed, unclickable toggle) that could unlock it. Adding a
    /// second parallel property for the textbox alone would have meant duplicating the same
    /// change-notification wiring (OnRawSyncModeChanged, LoadProfile, SettingsChanged) a second
    /// time for no real benefit.
    /// </summary>
    public bool IsRawAutoSync
    {
        get => !IsDefault && RawSyncMode == SyncMode.Auto && _profileService.Settings.ShowAutoMatchEnvironment;
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

    /// <summary>Same idea as <see cref="IsRawAutoSync"/> -- gated on ShowAutoMatchEnvironment AND
    /// ShowYXRatio (both required), the same compound gate as <see cref="IsYXRatioVisible"/> and
    /// <see cref="ProfileService.GetEffectiveYXRatioOverride"/>.</summary>
    public bool IsYXRatioAutoSync
    {
        get => YXRatioSyncMode == SyncMode.Auto && IsYXRatioAutoAvailable;
        set
        {
            if (value)
                YXRatioSyncMode = SyncMode.Auto;
            else
                YXRatioSyncMode = YXRatioOverride.HasValue ? SyncMode.Manual : SyncMode.Skip;
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
        _profileService.SettingsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsAutoMatchEnvironmentVisible));
            OnPropertyChanged(nameof(IsEppOverrideVisible));
            OnPropertyChanged(nameof(IsEppVisible));
            OnPropertyChanged(nameof(IsYXRatioVisible));
            OnPropertyChanged(nameof(IsRawAutoAvailable));
            OnPropertyChanged(nameof(IsYXRatioAutoAvailable));
            OnPropertyChanged(nameof(IsRawAutoSync));
            OnPropertyChanged(nameof(IsYXRatioAutoSync));
            OnPropertyChanged(nameof(YXRatioMatch));

            // The four notifications above only refresh what's already displayed -- they don't
            // recompute anything. A profile whose Auto mode was guarded off while a section was
            // hidden (ApplyAutoRawSync/ApplyAutoRegSync's guards) doesn't get its value filled
            // back in just because the toggle reappears; nothing else was going to trigger that
            // until the next unrelated edit or a profile switch. Found live: re-enabling
            // Auto-Match Environment brought the toggle back checked, but left Sensitivity empty
            // until switching profiles and back. Re-running the same cascade LoadProfile already
            // uses (not a full reset -- this won't discard any other unsaved edit in progress)
            // fixes it for both Raw Accel and EPP Override at once, since ApplyAutoOSSync already
            // cascades through both.
            if (_editingProfile != null)
            {
                ApplyAutoOSSync();
                ApplyAutoYXRatioSync();
                AutoPersist();
                RecalculateMatchAccuracy();
            }
        };

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
            SensitivityString     = profile.SensMultiplier is { } sensVal ? FormatFixedPointInput(sensVal, int.MaxValue) : string.Empty;
            RawSyncMode           = profile.RawSyncMode;
            OSSyncMode            = profile.OSSyncMode;
            WinPointerSpeed       = profile.WinPointerSpeed;
            RegSyncMode           = profile.RegSyncMode;
            RegistryCurveOverride = profile.RegistryCurveOverride;
            RegistryCurveString   = profile.RegistryCurveOverride is { } regVal ? FormatFixedPointInput(regVal, int.MaxValue) : string.Empty;
            TargetScreenWidth       = profile.TargetScreenWidth;
            TargetScreenWidthString = profile.TargetScreenWidth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TargetScreenHeight       = profile.TargetScreenHeight;
            TargetScreenHeightString = profile.TargetScreenHeight?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            YXRatioSyncMode = profile.YXRatioSyncMode;
            YXRatioOverride = profile.YXRatioOverride;
            YXRatioString   = profile.YXRatioOverride is { } yxVal ? FormatFixedPointInput(yxVal, int.MaxValue) : string.Empty;
            EppOverride             = profile.UseEnhancePointerPrecision;
            IsDefault       = profile.IsDefault;

            // Loading itself now guarantees SensMultiplier/RegistryCurveOverride are always
            // either null or already-valid (see ProfileService's lenient JSON converters --
            // anything that fails TryParsePositiveDouble is read as null, same as a blank
            // field), so there's nothing left to check here.
            IsSensitivityInputValid       = true;
            IsRegistryCurveInputValid     = true;
            IsTargetScreenWidthInputValid = true;
            IsTargetScreenHeightInputValid = true;
            IsYXRatioInputValid           = true;
        }
        finally
        {
            _isSyncing = false;
        }

        RefreshAssociationsSummary();
        OpenTargetManagerCommand.NotifyCanExecuteChanged();
        // IsRawAutoSync/IsOSAutoSync/IsRegAutoSync/IsYXRatioAutoSync already got notified above,
        // declaratively, by the RawSyncMode/OSSyncMode/RegSyncMode/YXRatioSyncMode/IsDefault
        // assignments themselves.
        ApplyAutoOSSync();         // re-run with correct IsDefault + all loaded values
        ApplyAutoYXRatioSync();
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
        SensitivityString = newValue is { } sensVal ? FormatFixedPointInput(sensVal, int.MaxValue) : string.Empty;
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
            IsSensitivityInputValid = true;
            Sensitivity = null;
            if (RawSyncMode != SyncMode.Auto)
                RawSyncMode = SyncMode.Skip;
            return;
        }

        IsSensitivityInputValid = ProfileService.TryParsePositiveDouble(newValue, out var parsed);
        if (IsSensitivityInputValid)
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
        // IsOSAutoSync already notified declaratively by the OSSyncMode assignment that triggered
        // this callback in the first place -- see [NotifyPropertyChangedFor] on oSSyncMode.
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
        // IsRegAutoSync already notified declaratively by the RegSyncMode assignment that
        // triggered this callback in the first place -- see [NotifyPropertyChangedFor] on
        // regSyncMode.
    }

    partial void OnRegistryCurveOverrideChanged(double? oldValue, double? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        RegistryCurveString = newValue is { } regVal ? FormatFixedPointInput(regVal, int.MaxValue) : string.Empty;
        _isSyncing = false;
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnRegistryCurveStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            IsRegistryCurveInputValid = true;
            RegistryCurveOverride = null;
            if (RegSyncMode != SyncMode.Auto)
                RegSyncMode = SyncMode.Skip;
            return;
        }

        IsRegistryCurveInputValid = ProfileService.TryParsePositiveDouble(newValue, out var parsed);
        if (IsRegistryCurveInputValid)
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

    partial void OnYXRatioSyncModeChanged(SyncMode oldValue, SyncMode newValue)
    {
        if (_isSyncing) return;
        if (newValue == SyncMode.Skip)
        {
            _isSyncing = true;
            YXRatioOverride = null;
            YXRatioString   = string.Empty;
            _isSyncing = false;
        }
        ApplyAutoYXRatioSync();
        AutoPersist();
        RecalculateMatchAccuracy();
        // IsYXRatioAutoSync already notified declaratively by the YXRatioSyncMode assignment
        // that triggered this callback -- see [NotifyPropertyChangedFor] on yXRatioSyncMode.
    }

    partial void OnYXRatioOverrideChanged(double? oldValue, double? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        YXRatioString = newValue is { } yxVal ? FormatFixedPointInput(yxVal, int.MaxValue) : string.Empty;
        _isSyncing = false;
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnYXRatioStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            IsYXRatioInputValid = true;
            YXRatioOverride = null;
            if (YXRatioSyncMode != SyncMode.Auto)
                YXRatioSyncMode = SyncMode.Skip;
            return;
        }

        IsYXRatioInputValid = ProfileService.TryParsePositiveDouble(newValue, out var parsedRatio);
        if (IsYXRatioInputValid)
        {
            _isSyncing = true;
            YXRatioOverride = parsedRatio;
            _isSyncing = false;
        }

        if (YXRatioSyncMode == SyncMode.Skip)
            YXRatioSyncMode = SyncMode.Manual;

        // Not covered by ApplyAutoYXRatioSync (this is the Manual-typing path, and that
        // function's own YXRatioOverride write is suppressed here via the _isSyncing guard
        // above) -- YXRatioMatch's "effective" side just changed, so it needs its own refresh.
        OnPropertyChanged(nameof(YXRatioMatch));
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
        ApplyAutoYXRatioSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetScreenWidthStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            IsTargetScreenWidthInputValid = true;
            TargetScreenWidth = null;
            return;
        }

        IsTargetScreenWidthInputValid = ProfileService.TryParsePositiveInt(newValue, out var parsed);
        if (IsTargetScreenWidthInputValid)
        {
            _isSyncing = true;
            TargetScreenWidth = parsed;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        ApplyAutoYXRatioSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetScreenHeightChanged(int? oldValue, int? newValue)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        TargetScreenHeightString = newValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _isSyncing = false;
        ApplyAutoOSSync();
        ApplyAutoYXRatioSync();
        AutoPersist();
        RecalculateMatchAccuracy();
    }

    partial void OnTargetScreenHeightStringChanged(string? oldValue, string newValue)
    {
        if (_isSyncing) return;

        if (string.IsNullOrWhiteSpace(newValue))
        {
            IsTargetScreenHeightInputValid = true;
            TargetScreenHeight = null;
            return;
        }

        IsTargetScreenHeightInputValid = ProfileService.TryParsePositiveInt(newValue, out var parsedHeight);
        if (IsTargetScreenHeightInputValid)
        {
            _isSyncing = true;
            TargetScreenHeight = parsedHeight;
            _isSyncing = false;
        }

        ApplyAutoOSSync();
        ApplyAutoYXRatioSync();
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
        // IsRawAutoSync already notified declaratively by the RawSyncMode assignment that
        // triggered this callback in the first place -- see [NotifyPropertyChangedFor] on
        // rawSyncMode.
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

        // IsAutoSyncAvailable/IsRawAutoAvailable/IsRawAutoSync/IsOSAutoSync/IsRegAutoSync are all
        // declaratively wired to IsDefault via [NotifyPropertyChangedFor] on the isDefault field
        // above -- no manual OnPropertyChanged calls needed here, the generated setter already
        // raised them before this callback even started running.

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

    /// <summary>YXRatioOverride of the Default profile (fallback 1.0 = neutral, no manual
    /// stretch). The Default's own Raw Accel Y/X ratio isn't guaranteed to be neutral -- someone
    /// may have set it manually for reasons unrelated to aspect-ratio compensation -- so this is
    /// the base every other profile's Auto-computed ratio (and YXRatioMatch's "effective" side)
    /// must multiply against/inherit from, not a hardcoded 1.0. Same fallback-chain principle as
    /// GetBaseSens/GetBaseRegCurve above.</summary>
    private double GetBaseYXRatio()
    {
        var r = GetBaseProfile()?.YXRatioOverride ?? 1.0;
        return r == 0 ? 1.0 : r;
    }

    /// <summary>Same resolution as <see cref="ProfileService.ResolveEpp"/>, for this ViewModel's
    /// own live-edit state (<see cref="EppOverride"/>) instead of a stored MouseProfile -- this
    /// profile's own value, else the Default's, else false (EPP off) if neither specifies one.
    /// See ProfileService.ResolveEpp's doc comment for why false is the correct final tier.
    /// Gated on Settings.ShowEpp (same relationship as ProfileService.GetEffectiveUseEnhancePointerPrecision
    /// to ResolveEpp) so a hidden EPP row resolves to the same neutral false as "never set,"
    /// instead of this display-side resolver silently reading a stale stored value the user can
    /// no longer see or edit.</summary>
    private bool GetEffectiveEpp() =>
        _profileService.Settings.ShowEpp
            ? EppOverride ?? GetBaseProfile()?.UseEnhancePointerPrecision ?? false
            : false;

    /// <summary>TargetScreenWidth of the Default profile. Null if not declared, or if
    /// Settings.ShowAutoMatchEnvironment is off (see ProfileService.GetEffectiveTargetScreenWidth
    /// -- this delegation is what makes every Auto-sync function below treat a hidden Environment
    /// section as though no profile had ever set these fields, without each function needing its
    /// own check).</summary>
    private int? GetBaseScreenWidth() => _profileService.GetEffectiveTargetScreenWidth(GetBaseProfile());

    /// <summary>
    /// This profile's own TargetScreenWidth, or null whenever Settings.ShowAutoMatchEnvironment is
    /// off -- same idea as <see cref="GetBaseScreenWidth"/>, but for the profile currently being
    /// edited. TargetScreenWidth here is this ViewModel's own live property (kept in sync with the
    /// editing MouseProfile via LoadProfile/AutoPersist), not a MouseProfile object, so it can't
    /// route through ProfileService's MouseProfile-parameterized helper directly.
    /// </summary>
    private int? EffectiveTargetScreenWidth => _profileService.Settings.ShowAutoMatchEnvironment ? TargetScreenWidth : null;

    /// <summary>Same idea as <see cref="GetBaseScreenWidth"/>, for TargetScreenHeight.</summary>
    private int? GetBaseScreenHeight() => _profileService.GetEffectiveTargetScreenHeight(GetBaseProfile());

    /// <summary>Same idea as <see cref="EffectiveTargetScreenWidth"/>, for TargetScreenHeight.</summary>
    private int? EffectiveTargetScreenHeight => _profileService.Settings.ShowAutoMatchEnvironment ? TargetScreenHeight : null;


    /// <summary>
    /// Auto Raw Sensitivity: compute the exact raw sens so that
    ///   Sens × envScreenRatio == S_base
    /// giving Raw Accel Only = 100% before Win Speed or Reg Bridge contribute.
    /// </summary>
    private void ApplyAutoRawSync()
    {
        // ShowAutoMatchEnvironment off means the Auto toggle is dimmed/unavailable in the GUI
        // (see IsRawAutoAvailable) -- so don't keep silently recomputing and overwriting
        // Sensitivity in the background while it's unavailable, same reasoning as
        // ApplyAutoRegSync's guard. Without this, RawSyncMode staying Auto (nothing ever resets
        // it) would both lock the Sensitivity textbox and immediately overwrite anything typed
        // into it the next time this runs -- found live: exactly this happened, a profile got
        // stuck with Sensitivity locked to the Default's value with no way to edit it short of
        // re-enabling the setting.
        if (IsDefault || RawSyncMode != SyncMode.Auto || !_profileService.Settings.ShowAutoMatchEnvironment) return;

        var sBase  = GetBaseSens();
        var bp     = GetBaseProfile();

        double envScreenRatio = 1.0;
        var baseW   = GetBaseScreenWidth();
        var activeW = EffectiveTargetScreenWidth ?? baseW;
        if (baseW.HasValue && activeW.HasValue && baseW.Value > 0)
            envScreenRatio = (double)baseW.Value / activeW.Value;

        // Target: Sens × envScreenRatio = S_base
        var newSens = envScreenRatio == 0 ? sBase : sBase / envScreenRatio;

        var wasSyncing = _isSyncing;
        _isSyncing = true;
        Sensitivity       = newSens;
        SensitivityString = FormatFixedPointInput(newSens, int.MaxValue);
        _isSyncing = wasSyncing;
    }

    /// <summary>
    /// Auto Win Pointer Speed: choose the WinMult closest to Effective(base) / S_target,
    /// so that S_target × WinMult ≈ Effective(base) — compensating the screen width change as
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

            // Env correction: screen width changes the feel, so Win Speed should absorb
            // as much of the cross-environment delta as the 20 discrete steps allow.
            // envCorr = activeW/baseW — the inverse of what GetEffectiveSensitivity divides by.
            var baseWRaw   = GetBaseScreenWidth();
            var activeWRaw = EffectiveTargetScreenWidth ?? baseWRaw;
            double envCorrWin = 1.0;
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
        // ShowEppOverride off means the field is hidden and functionally inert (see
        // ProfileService.GetEffectiveRegistryCurveOverride's doc comment) -- so don't keep
        // silently recomputing and overwriting RegistryCurveOverride in the background while
        // it's hidden. The stored value just stays exactly as last set until the section is
        // shown again; nothing reads it as meaningful in the meantime either way.
        if (IsDefault || !_profileService.Settings.ShowEppOverride) return;

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
            var baseW   = GetBaseScreenWidth();
            var activeW = EffectiveTargetScreenWidth ?? baseW;

            double envCorrection = 1.0;
            if (baseW.HasValue && activeW.HasValue && baseW.Value > 0)
                envCorrection *= (double)activeW.Value / baseW.Value;

            var remainder = raw * envCorrection;

            var wasSyncing = _isSyncing;
            _isSyncing = true;
            RegistryCurveOverride = remainder;
            RegistryCurveString   = FormatRegistryCurveValue(remainder);
            _isSyncing = wasSyncing;
        }
    }

    /// <summary>
    /// The Y/X ratio actually needed for this profile to match the Default's real-world feel:
    /// GetBaseYXRatio() (the Default's own manual baseline, neutral 1.0 if it hasn't set one) ×
    /// (native aspect ÷ this profile's aspect). The aspect factor alone is exactly 1.0 whenever
    /// the two aspect ratios actually match, regardless of absolute resolution size (e.g.
    /// 1920x1080 vs. 1280x720 are both 16:9 -> 1.0); only diverges from 1.0 when the shape
    /// itself genuinely changes (e.g. 1920x1080 base vs. 800x600 profile -> ~1.333, meaning Y
    /// gets boosted 33% to match X's real-world feel -- see this session's stretch-ratio
    /// derivation). That aspect factor alone used to be the entire return value here -- found
    /// live to be wrong whenever the Default's own YXRatioOverride isn't neutral, the same
    /// "jumped straight to a hardcoded neutral instead of falling back through the Default"
    /// bug class as GetBaseSens/GetBaseWinPointerSpeed/GetEffectiveEpp/GetBaseRegCurve already
    /// had fixed for them (see CLAUDE.md, ProfileService.cs section) -- a manually-stretched
    /// Default's baseline was being silently discarded instead of compensated relative to.
    /// Neutral aspect factor (1.0) whenever either profile is missing Width or Height, mirroring
    /// every other Auto-sync function's graceful-blank behavior here. Shared by
    /// ApplyAutoYXRatioSync (writes this into YXRatioOverride when YXRatioSyncMode is Auto) and
    /// YXRatioMatch (below, always compares the current effective value against this target
    /// regardless of sync mode) so the two can't drift apart into two different formulas.
    /// </summary>
    private double ComputeIdealYXRatio()
    {
        var baseW   = GetBaseScreenWidth();
        var baseH   = GetBaseScreenHeight();
        var activeW = EffectiveTargetScreenWidth ?? baseW;
        var activeH = EffectiveTargetScreenHeight ?? baseH;

        double aspectFactor = 1.0;
        if (baseW.HasValue && baseH.HasValue && activeW.HasValue && activeH.HasValue
            && baseH.Value > 0 && activeH.Value > 0)
        {
            double baseAspect   = (double)baseW.Value / baseH.Value;
            double activeAspect = (double)activeW.Value / activeH.Value;
            if (activeAspect > 0)
                aspectFactor = baseAspect / activeAspect;
        }

        return GetBaseYXRatio() * aspectFactor;
    }

    /// <summary>
    /// Auto Y/X Ratio: sets YXRatioOverride to ComputeIdealYXRatio() whenever YXRatioSyncMode is
    /// Auto. Unlike ApplyAutoOSSync/ApplyAutoRegSync, this value doesn't feed the Windows-side
    /// effective-sensitivity math at all -- it's headed for Raw Accel's own settings.json (wired
    /// in a later step), so it has no bearing on the percent-match% display/RecalculateMatchAccuracy.
    /// Always re-raises YXRatioMatch (regardless of sync mode) since this function is the one
    /// place every Width/Height-changing trigger already funnels through -- Skip/Manual profiles
    /// need that comparison refreshed too whenever the *target* (ComputeIdealYXRatio) moves, even
    /// though their own effective value didn't change.
    /// </summary>
    private void ApplyAutoYXRatioSync()
    {
        if (IsDefault || !IsYXRatioVisible) return;

        if (YXRatioSyncMode == SyncMode.Auto)
        {
            double ratio = ComputeIdealYXRatio();

            var wasSyncing = _isSyncing;
            _isSyncing = true;
            YXRatioOverride = ratio;
            YXRatioString   = FormatFixedPointInput(ratio, int.MaxValue);
            _isSyncing = wasSyncing;
        }

        OnPropertyChanged(nameof(YXRatioMatch));
    }

    /// <summary>
    /// Always-on comparison between the currently effective Y/X ratio (this profile's own
    /// override, or the Default's baseline if Skipped -- same profile→base fallback chain as
    /// every other field) and ComputeIdealYXRatio() (what the aspect-ratio mismatch actually
    /// needs): 1.0 means fully compensated, exactly like the other rows' 100% match, just
    /// expressed as a ratio instead of a percent (this field was deliberately never given percent
    /// formatting -- see this session's design discussion). Trivially always 1.0 in Auto mode
    /// (effective is set equal to ideal by construction in ApplyAutoYXRatioSync). In Skip mode
    /// this reveals whether a real, uncorrected aspect-ratio mismatch exists instead of hiding it
    /// behind a blank textbox -- found live: the view used to bind this same display slot to
    /// YXRatioString itself (the textbox's own raw value), so Skip mode -- the default for every
    /// profile -- showed nothing even when the real match was well off 1.0, and Auto mode just
    /// showed a second copy of the exact number already visible in the textbox right next to it.
    /// </summary>
    public string YXRatioMatch
    {
        get
        {
            // Also blank when Auto-Match Environment is off: both screen dimensions resolve to
            // null then (ProfileService.GetEffectiveTargetScreenWidth), so ComputeIdealYXRatio
            // falls through to aspectFactor 1.0 and "ideal" collapses to the Default's own ratio.
            // The match would just echo the textbox -- the exact duplicate-of-itself problem this
            // display was rebuilt to stop showing when it was bound to YXRatioString.
            if (IsDefault || !IsYXRatioVisible || !_profileService.Settings.ShowAutoMatchEnvironment)
                return string.Empty;

            double ideal     = ComputeIdealYXRatio();
            double effective = YXRatioOverride ?? GetBaseYXRatio();
            double match     = ideal != 0 ? effective / ideal : 1.0;
            return FormatFixedPointInput(match, int.MaxValue);
        }
    }

    /// <summary>
    /// Three-row waterfall proof — all rows measure against Effective(base).
    ///
    ///   Raw Accel Only  : (curSens × envScreenRatio) / S_base
    ///       How well raw accel sensitivity alone offsets the full environmental delta
    ///       (screen width) before Win Speed or Reg Bridge contribute.
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
    /// <summary>
    /// The exact number of significant decimal digits THIS specific double needs to round-trip
    /// back to itself, bit-for-bit — not a fixed guess (15? 16? 17?). Doubles vary: some
    /// round-trip in a handful of digits, some genuinely need all 17. Same notion .NET's own
    /// default double formatting uses internally. Shared by FormatMatchPercent (the match%
    /// display, local to RecalculateMatchAccuracy) and FormatRegistryCurveValue (the
    /// Auto-computed Registry Curve textbox, below) so both derive real precision the same way
    /// instead of two independently-maintained copies that could drift apart.
    /// </summary>
    private static int GetRealSignificantDigits(double value)
    {
        for (int n = 1; n < 17; n++)
        {
            if (double.Parse(value.ToString("G" + n, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) == value)
                return n;
        }
        return 17;
    }

    /// <summary>
    /// How many digit positions <paramref name="absValue"/>'s integer part has -- e.g. 150.0 has
    /// 3, 0.05 has -1 (correctly placing its first significant digit in the hundredths position;
    /// see FormatMatchPercent's local use for why this must stay unclamped). Shared by
    /// FormatFixedPointInput and FormatMatchPercent so the same order-of-magnitude formula isn't
    /// hand-typed in two places.
    /// </summary>
    private static int GetTrueIntegerDigitCount(double absValue) => (int)Math.Floor(Math.Log10(absValue)) + 1;

    /// <summary>
    /// Renders <paramref name="value"/> fixed-point with exactly <paramref name="decimalPlaces"/>
    /// digit positions available (trailing zeros still trimmed by '#'). Both
    /// <see cref="FormatFixedPointInput"/> and FormatMatchPercent (local to
    /// RecalculateMatchAccuracy) need this same primitive, so it lives here as the one
    /// implementation instead of two copies.
    ///
    /// Does NOT call <c>value.ToString("0." + new string('#', decimalPlaces))</c> directly --
    /// found live to be a genuine, reproducible .NET formatting bug, not app logic: for a double
    /// whose shortest round-trip representation needs many digits (e.g.
    /// 1.7718749999999999, which needs all 17), asking the custom "0.####...#" format for more
    /// than ~6-7 decimal places silently returns the SAME truncated string no matter how many
    /// '#' placeholders are requested (verified directly via .ToString, no app code involved:
    /// decimalPlaces 6 through 17 all produced "1.771875"). Only the "G17" format specifier
    /// reliably reveals the true value ("1.7718749999999999") -- custom picture-format strings
    /// on double just don't honor arbitrary requested precision.
    ///
    /// The fix routes through <see cref="decimal"/> instead: get the value's exact round-trip
    /// string via "G17" (always correct -- that's the one specifier proven to work), parse it
    /// into a decimal (decimal's own custom-format rendering does not share double's cap -- also
    /// verified directly), then apply the same "0.####...#" format to the decimal. NumberStyles.Float
    /// is required on the parse because G17 can itself emit scientific notation for small
    /// magnitudes (e.g. "1.2345669999999999E-07"); decimal correctly expands that back to plain
    /// fixed-point digits. Falls back to the old double-format path only if the decimal parse
    /// itself fails (e.g. a magnitude/precision combination that overflows decimal's ~28-29
    /// digit budget) -- that combination should already be unreachable here in practice, since
    /// both callers route magnitudes that extreme to scientific notation before ever asking for
    /// fixed-point at all, but the fallback exists so this never crashes if that assumption is
    /// ever wrong.
    /// </summary>
    private static string FormatFixedPointExact(double value, int decimalPlaces)
    {
        string format = decimalPlaces > 0 ? "0." + new string('#', decimalPlaces) : "0";

        string exact = value.ToString("G17", CultureInfo.InvariantCulture);
        if (decimal.TryParse(exact, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec))
            return dec.ToString(format, CultureInfo.InvariantCulture);

        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a value for a raw input textbox (Sensitivity, RegistryCurveOverride), always in
    /// fixed-point notation — never switching to scientific "E" notation the way a plain
    /// <c>ToString(CultureInfo.InvariantCulture)</c> does for extreme magnitudes (that default
    /// behavior was the actual bug: typing "0.00001" and reloading the profile silently showed
    /// "1E-05" back — the field reformatting what you typed, not just displaying it). Shows
    /// exactly this value's own real round-trip precision (via <see cref="GetRealSignificantDigits"/>),
    /// further capped by a genuine domain-specific floor when one exists (e.g. the registry
    /// curve's live quantization step — see <see cref="FormatRegistryCurveValue"/>); pass
    /// <c>int.MaxValue</c> when no such floor applies (Sensitivity has none of its own — see
    /// CLAUDE.md's Raw Accel research note). Capped at 50 decimal places regardless, purely as a
    /// sanity bound against a pathologically tiny test value producing a multi-hundred-character
    /// string — not a precision-correctness limit the way the ~15 budget elsewhere is.
    /// </summary>
    private static string FormatFixedPointInput(double value, int domainDecimalPlaces)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture);
        if (value == 0) return "0";

        int realSigFigs = GetRealSignificantDigits(value);
        var absValue = Math.Abs(value);
        int trueIntegerDigits = GetTrueIntegerDigitCount(absValue);
        int naturalDecimalPlaces = Math.Max(0, realSigFigs - trueIntegerDigits);

        int decimalPlaces = domainDecimalPlaces == int.MaxValue
            ? naturalDecimalPlaces
            : Math.Min(domainDecimalPlaces, naturalDecimalPlaces);
        decimalPlaces = Math.Min(decimalPlaces, 50);

        return FormatFixedPointExact(value, decimalPlaces);
    }

    /// <summary>
    /// Formats an Auto-computed RegistryCurveOverride value for the textbox, capped by the
    /// real, live registry-curve quantization step (EncodeRegCurve's own step) instead of a
    /// flat guessed digit count (this replaces a hardcoded "G6") — the same "never show a fake
    /// digit" principle as FormatMatchPercent, applied to the raw value instead of a percentage.
    /// The underlying RegistryCurveOverride double itself is left at full precision (unchanged)
    /// so it can be correctly re-quantized fresh if DPI/pointer speed ever change later — only
    /// this display string is capped. Only for the Auto-computed path: a manually-typed
    /// RegistryCurveOverride is shown with <see cref="FormatFixedPointInput"/> directly (no
    /// domain cap) so it round-trips back exactly as typed instead of being silently trimmed to
    /// the hardware step on the next profile load.
    /// </summary>
    private static string FormatRegistryCurveValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture);

        double step;
        try
        {
            MouseRegistryBridge.EncodeRegCurve(value, out step);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Extreme enough to overflow 16.16 fixed-point storage (see QuantizeToFixedPoint1616)
            // — no real step to derive from; fall back to this value's own round-trip precision.
            step = 0;
        }
        int stepDecimalPlaces = step > 0 ? Math.Max(0, (int)Math.Ceiling(-Math.Log10(step))) : int.MaxValue;
        return FormatFixedPointInput(value, stepDecimalPlaces);
    }

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
        var useEpp      = GetEffectiveEpp();
        var rawRegCurve = RegistryCurveOverride ?? GetBaseRegCurve();

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
            if (double.IsNaN(value)) return "NaN%";
            if (double.IsInfinity(value)) return (value > 0 ? "Infinity" : "-Infinity") + "%";
            if (value == 0) return "0%";

            int realSigFigs = GetRealSignificantDigits(value);

            // Order-of-magnitude digit count, UNCLAMPED — zero or negative for values under 1
            // (e.g. 0.05 has trueIntegerDigits = -1, correctly placing its first significant
            // digit in the hundredths position). Clamping this to "at least 1" was a real bug:
            // it silently breaks decimal-place placement for any sub-1 value and can round a
            // genuinely nonzero percentage all the way down to a false "0%".
            var absValue = Math.Abs(value);
            int trueIntegerDigits = GetTrueIntegerDigitCount(absValue);

            // Decimal places needed to show realSigFigs significant digits starting from this
            // value's actual first significant digit, further capped by a real domain-specific
            // floor when one exists (Full Match, EPP on — domainDecimalPlaces there comes from
            // the registry curve's live-computed quantization step, not a guess. int.MaxValue,
            // see callers, means no such floor applies and this value's own precision is the
            // only cap). Clamped at a minimum of 0: realSigFigs can be SMALLER than
            // trueIntegerDigits for values with trailing zeros in the integer part (e.g. 150.0
            // round-trips at just 2 significant digits despite having 3 integer digit
            // positions) — that just means "0 decimals needed," not "too large to display."
            // Letting this go negative was a real regression found live: it made ordinary
            // round-ish percentages (not just deliberately extreme test values) incorrectly
            // render in scientific notation.
            int naturalDecimalPlaces = Math.Max(0, realSigFigs - trueIntegerDigits);
            int decimalPlaces = domainDecimalPlaces == int.MaxValue
                ? naturalDecimalPlaces
                : Math.Min(domainDecimalPlaces, naturalDecimalPlaces);

            // "Too large for fixed-point" must be judged against a fixed, absolute ceiling on
            // trueIntegerDigits (independent of realSigFigs) — NOT by whether decimalPlaces
            // would've gone negative before the clamp above. Whether THIS value's shortest
            // round-trip form happens to need fewer digits than its integer part is wide (see
            // above) has nothing to do with whether the integer part itself still fits within a
            // double's real precision. 15 is the same fixed ceiling used everywhere else
            // tonight for "double's own guaranteed-safe significant-digit budget."
            const int absoluteIntegerDigitCeiling = 15;
            bool tooLargeForFixed = trueIntegerDigits > absoluteIntegerDigitCeiling;

            // "Too small for fixed-point" must be judged the same way as tooLargeForFixed above
            // -- against trueIntegerDigits alone (an absolute magnitude ceiling), NOT against
            // decimalPlaces. decimalPlaces conflates two different things: how many leading
            // zeros a genuinely tiny value would need (a real "too small" concern -- an
            // unreasonable wall of zeros before the first real digit) vs. how many significant
            // digits THIS value happens to need to round-trip exactly, which has nothing to do
            // with its magnitude. A value with an ordinary magnitude (trueIntegerDigits near 0)
            // can still need many significant digits purely from float rounding noise (e.g.
            // 0.567 * 0.03125 * 100 == 1.7718749999999999, needing all 17 digits to round-trip)
            // -- that used to push decimalPlaces past 15 and misfire scientific notation on an
            // entirely ordinary-looking value, rendering a pointless "1.77...E+000%" (the
            // exponent is zero -- scientific notation buys nothing there). This is the exact
            // same conflation that tooLargeForFixed already had fixed for it above (see
            // da3f760, "Fix regression: clean/round percentages wrongly showed scientific
            // notation") -- that fix only covered the large side; this is its unfixed mirror on
            // the small side, found live via a WinPointerSpeed=1 profile. Precision is not
            // reduced by this change: every real significant digit still renders, just in
            // fixed-point instead of scientific notation, whenever the magnitude itself doesn't
            // actually warrant it.
            bool tooSmallForFixed = trueIntegerDigits < -absoluteIntegerDigitCeiling;

            if (tooLargeForFixed || tooSmallForFixed)
            {
                int sciDecimalPlaces = domainDecimalPlaces == int.MaxValue
                    ? Math.Max(0, realSigFigs - 1)
                    : Math.Min(domainDecimalPlaces, Math.Max(0, realSigFigs - 1));
                return value.ToString($"E{sciDecimalPlaces}", CultureInfo.InvariantCulture) + "%";
            }

            // '#' (not '0') after the decimal point omits trailing zeros instead of padding
            // them out to the cap — a clean typed 1.12312312 sensitivity shows as "112.312312%",
            // not "112.312312000000%" implying precision that was never real.
            // Routed through FormatFixedPointExact rather than a direct value.ToString(format,
            // ...) call -- see that method's doc comment for why a direct call silently caps
            // precision below what decimalPlaces requests for some values.
            return FormatFixedPointExact(value, decimalPlaces) + "%";
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
        _editingProfile.TargetScreenHeight            = TargetScreenHeight;
        _editingProfile.YXRatioSyncMode               = YXRatioSyncMode;
        _editingProfile.YXRatioOverride               = YXRatioOverride;
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
