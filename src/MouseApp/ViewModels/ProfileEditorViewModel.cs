using System;
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
    // Windows Pointer Speed multiplier table — index 0 = speed 1, index 19 = speed 20
    private static readonly double[] WinMultipliers =
    {
        0.03125, 0.0625, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0,
        1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5
    };

    private readonly TemplateService _templateService;
    private readonly ProfileService _profileService;

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
    private bool isGlobalDefault;

    // ── Derived display ─────────────────────────────────────────────────────
    /// <summary>
    /// Displays (CurrentEffective / GlobalDefaultEffective) x 100 as a percentage.
    /// 100.00% means the compensation is exact.
    /// </summary>
    [ObservableProperty]
    private string matchAccuracy = "N/A";

    // ── Commands ────────────────────────────────────────────────────────────
    public IAsyncRelayCommand SaveCommand { get; }

    // ── Constructor ─────────────────────────────────────────────────────────
    public ProfileEditorViewModel(TemplateService templateService, ProfileService profileService)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _profileService  = profileService  ?? throw new ArgumentNullException(nameof(profileService));

        RefreshTemplates();
        _templateService.TemplatesChanged += (_, _) => RefreshTemplates();

        WinPointerOptions.Add(null); // sparse / unset sentinel
        for (int i = 1; i <= 20; i++) WinPointerOptions.Add(i);

        _profileService.Profiles.CollectionChanged += OnProfilesCollectionChanged;

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync);
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

    // ── Math Engine ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the SensMultiplier of the Global Default profile (fallback: 1.0).
    /// Used so that auto-sync formulas are relative to S_base, not hardcoded 1.0.
    /// </summary>
    private double GetBaseSensMultiplier()
    {
        var baseProfile = _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault)
                       ?? _profileService.Profiles.FirstOrDefault();
        var sBase = baseProfile?.SensMultiplier ?? 1.0;
        return sBase == 0 ? 1.0 : sBase;
    }

    /// <summary>
    /// When UseAutoOSSync is enabled, sets WinPointerSpeed to the index in
    /// WinMultipliers that is closest to S_base / S_target (= 1/x).
    /// Always delegates to ApplyAutoRegSync afterward.
    /// </summary>
    private void ApplyAutoOSSync()
    {
        if (UseAutoOSSync)
        {
            var sBase   = GetBaseSensMultiplier();
            var sTarget = Sensitivity ?? 1.0;
            if (sTarget == 0) sTarget = 1.0;

            // x = S_target / S_base  →  target = 1/x = S_base / S_target
            var x      = sTarget / sBase;
            if (x == 0) x = 1.0;
            var target = 1.0 / x;

            int bestIndex = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < WinMultipliers.Length; i++)
            {
                double diff = Math.Abs(WinMultipliers[i] - target);
                if (diff < minDiff)
                {
                    minDiff   = diff;
                    bestIndex = i;
                }
            }

            _isSyncing = true;
            WinPointerSpeed = bestIndex + 1; // 1-based
            _isSyncing = false;
        }

        ApplyAutoRegSync();
    }

    /// <summary>
    /// When UseAutoRegSync is enabled, calculates RegistryCurveOverride as the
    /// exact remainder so that WinMult × RegCurve == S_base / S_target (= 1/x),
    /// achieving 100% MatchAccuracy relative to the Global Default.
    /// </summary>
    private void ApplyAutoRegSync()
    {
        if (UseAutoRegSync)
        {
            var sBase   = GetBaseSensMultiplier();
            var sTarget = Sensitivity ?? 1.0;
            if (sTarget == 0) sTarget = 1.0;

            var x = sTarget / sBase;
            if (x == 0) x = 1.0;
            var targetMultiplier = 1.0 / x; // S_base / S_target

            var winIndex  = Math.Clamp((WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
            var winMult   = WinMultipliers[winIndex];
            var remainder = winMult == 0 ? 1.0 : targetMultiplier / winMult;

            _isSyncing = true;
            RegistryCurveOverride = remainder;
            RegistryCurveString   = remainder.ToString("G6", CultureInfo.InvariantCulture);
            _isSyncing = false;
        }
    }

    /// <summary>
    /// Recalculates the Proof-of-Match display.
    /// Total Effective = (SensMultiplier ?? 1.0) × WinMult × (RegistryCurveOverride ?? 1.0)
    /// MatchAccuracy   = (CurrentTotalEffective / GlobalDefaultTotalEffective) × 100
    /// 100.00% means the compensation is exact across all three factors.
    /// </summary>
    private void RecalculateMatchAccuracy()
    {
        var baseProfile = _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault)
                       ?? _profileService.Profiles.FirstOrDefault();

        if (baseProfile == null)
        {
            MatchAccuracy = "N/A";
            return;
        }

        double baseEffective = _profileService.GetEffectiveSensitivity(baseProfile);
        if (baseEffective == 0)
        {
            MatchAccuracy = "N/A";
            return;
        }

        // Build total effective from the editor's current values (all three factors)
        var curSens      = Sensitivity ?? 1.0;
        var curWinIndex  = Math.Clamp((WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
        var curWinMult   = WinMultipliers[curWinIndex];
        var curRegCurve  = RegistryCurveOverride ?? 1.0;
        var curEffective = curSens * curWinMult * curRegCurve;

        var accuracy = (curEffective / baseEffective) * 100.0;
        MatchAccuracy = accuracy.ToString("0.00", CultureInfo.InvariantCulture) + "%";
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
        _editingProfile.IsGlobalDefault       = IsGlobalDefault;

        _ = _profileService.SaveAsync();
    }

    // ── Save Command ────────────────────────────────────────────────────────

    private async Task ExecuteSaveAsync()
    {
        // Commit all current VM values to the profile object.
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
                IsGlobalDefault       = IsGlobalDefault,
                TargetExecutables     = new()
            };
            _profileService.Profiles.Add(draft);
            _editingProfile = draft;
        }

        // Use SetGlobalDefaultAsync for the exclusive-default logic; otherwise plain save.
        if (IsGlobalDefault)
            await _profileService.SetGlobalDefaultAsync(_editingProfile).ConfigureAwait(false);
        else
            AutoPersist();
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
