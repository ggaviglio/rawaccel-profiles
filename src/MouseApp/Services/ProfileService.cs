using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MouseApp.Models;

namespace MouseApp.Services;

public class ProfileService
{
    private readonly string _dataFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes all file writes so concurrent AutoPersist + SetDefaultAsync calls
    /// (and settings saves, since both share this file) never race on File.Create()
    /// and truncate each other's output.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    private sealed class PersistedData
    {
        public AppSettings        Settings { get; set; } = new();
        public List<MouseProfile> Profiles { get; set; } = new();
    }

    /// <summary>App-wide settings, persisted in the same file as profiles.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Raised when <see cref="Settings"/> is saved via <see cref="Save"/>.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// Windows Pointer Speed (1–20) → effective mouse multiplier.
    /// Index 0 = speed 1 (0.03125×), index 9 = speed 10 (1.0×, neutral), index 19 = speed 20 (3.5×).
    /// Shared with <c>ProfileEditorViewModel</c> — single source of truth.
    /// </summary>
    public static readonly double[] WinMultipliers =
    {
        0.03125, 0.0625, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0,
        1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5
    };

    public ObservableCollection<MouseProfile> Profiles { get; } = new();

    public ProfileService(string? dataFile = null)
    {
        _dataFile = dataFile ?? Path.Combine(AppContext.BaseDirectory, "profiles.json");
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_dataFile))
        {
            // Create a sensible Default starting point
            var defaultProfile = new MouseProfile
            {
                ProfileName        = "Default",
                SensMultiplier     = 1.0,
                OSSyncMode         = SyncMode.Skip, // Default is the reference — auto-sync has no meaning here
                RegSyncMode        = SyncMode.Skip,
                IsDefault          = true,
                TargetAssociations = new()
            };

            Profiles.Clear();
            Profiles.Add(defaultProfile);
            Settings = new AppSettings();
            await SaveAsync().ConfigureAwait(false);
            return;
        }

        var json = await File.ReadAllTextAsync(_dataFile).ConfigureAwait(false);

        List<MouseProfile> list;
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Pre-merge format: a bare profiles array with no settings section.
                list     = JsonSerializer.Deserialize<List<MouseProfile>>(json, _jsonOptions) ?? new List<MouseProfile>();
                Settings = new AppSettings();
            }
            else
            {
                var data = JsonSerializer.Deserialize<PersistedData>(json, _jsonOptions) ?? new PersistedData();
                list     = data.Profiles;
                Settings = data.Settings;
            }
        }

        if (!list.Any())
        {
            list.Add(new MouseProfile
            {
                ProfileName        = "Default",
                SensMultiplier     = 1.0,
                OSSyncMode         = SyncMode.Skip,
                RegSyncMode        = SyncMode.Skip,
                IsDefault          = true,
                TargetAssociations = new()
            });
        }

        NormalizeDefault(list);

        Profiles.Clear();
        foreach (var p in list)
            Profiles.Add(p);
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = BuildPersistedData();
            EnsureDirectoryExists();

            using var fs = File.Create(_dataFile);
            await JsonSerializer.SerializeAsync(fs, data, _jsonOptions).ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Synchronous save for settings-driven changes (checkbox toggles, folder pickers).</summary>
    public void Save()
    {
        _saveLock.Wait();
        try
        {
            var data = BuildPersistedData();
            EnsureDirectoryExists();

            File.WriteAllText(_dataFile, JsonSerializer.Serialize(data, _jsonOptions));
        }
        finally
        {
            _saveLock.Release();
        }

        try
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // swallow listener exceptions
        }
    }

    private PersistedData BuildPersistedData()
    {
        var list = Profiles.ToList();
        NormalizeDefault(list);
        return new PersistedData { Settings = Settings, Profiles = list };
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task SetDefaultAsync(MouseProfile p)
    {
        foreach (var pr in Profiles)
            pr.IsDefault = false;

        p.IsDefault = true;
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Updates every profile referencing <paramref name="oldFileName"/> as its RawAccelSettingsFile
    /// to point at <paramref name="newFileName"/> instead, then persists. Call this immediately
    /// after a successful RawAccelSettingsRepository.Rename to keep profiles in sync with the renamed file.
    /// </summary>
    public async Task RetargetSettingsFileReferencesAsync(string oldFileName, string newFileName)
    {
        bool changed = false;
        foreach (var p in Profiles)
        {
            if (string.Equals(p.RawAccelSettingsFile, oldFileName, StringComparison.OrdinalIgnoreCase))
            {
                p.RawAccelSettingsFile = newFileName;
                changed = true;
            }
        }

        if (changed)
            await SaveAsync().ConfigureAwait(false);
    }

    private static void NormalizeDefault(List<MouseProfile> list)
    {
        if (list.Count == 0)
            return;

        var defaults = list.Where(p => p.IsDefault).ToList();
        if (defaults.Count == 0)
        {
            list[0].IsDefault = true;
            return;
        }

        foreach (var d in defaults.Skip(1))
            d.IsDefault = false;

        // The Default profile must never have auto-computed OS compensation values.
        // If OSSyncMode/RegSyncMode were previously Auto, they may have written
        // a non-neutral WinPointerSpeed or RegistryCurveOverride into the stored profile,
        // poisoning Effective(base) for all proof-of-match calculations on other profiles.
        var defaultProfile = defaults[0];
        if (defaultProfile.OSSyncMode == SyncMode.Auto)
        {
            defaultProfile.OSSyncMode      = SyncMode.Skip;
            defaultProfile.WinPointerSpeed = null; // reset to neutral (10 = 1.0×)
        }
        if (defaultProfile.RegSyncMode == SyncMode.Auto)
        {
            defaultProfile.RegSyncMode           = SyncMode.Skip;
            defaultProfile.RegistryCurveOverride = null; // reset to neutral (1.0×)
        }
    }

    /// <summary>
    /// Returns the total effective sensitivity for a profile, including environment compensation:
    ///   SensMultiplier × WinMult[WinPointerSpeed] × EncodeRegCurve(RegistryCurveOverride)
    ///   × (96 / logicalDpi)  [DPI compensation when EPP active]
    ///   × (baseScreenWidth / profileScreenWidth)  [resolution compensation]
    /// RegCurve is round-tripped through 16.16 fixed-point encoding so this matches what
    /// Windows will actually apply — the single source-of-truth for MatchAccuracy comparisons.
    /// Null fields inherit from the Default profile; fully-null profile yields 100% match by definition.
    /// </summary>
    /// <summary>
    /// The individual multipliers <see cref="GetEffectiveSensitivity"/> combines into one number.
    /// Exposed so callers that need a *partial* product (e.g. the Profile Editor's match-accuracy
    /// display, which shows "Raw Accel only" and "Raw Accel + Win Speed" rows before the final
    /// combined row) can reuse the exact same per-factor calculation instead of re-deriving it —
    /// see the "single source of truth" note on <see cref="GetEffectiveSensitivityFactors"/>.
    /// </summary>
    public readonly record struct EffectiveSensitivityFactors(
        double Sens, double WinMult, double RegCurve, double DpiComp, double ScreenComp)
    {
        /// <summary>Raw Accel sensitivity alone, with environment (DPI/screen) compensation applied.</summary>
        public double RawOnly => Sens * DpiComp * ScreenComp;

        /// <summary>Raw Accel + Win Pointer Speed, with environment compensation — no registry curve yet.</summary>
        public double WinOnly => RawOnly * WinMult;

        /// <summary>All five factors combined — identical to <see cref="GetEffectiveSensitivity"/>'s result.</summary>
        public double Full => WinOnly * RegCurve;
    }

    /// <summary>
    /// Computes the five independent multipliers that make up a profile's effective sensitivity,
    /// without combining them. This is the single implementation of the math — both
    /// <see cref="GetEffectiveSensitivity"/> (the real, apply-time number) and the Profile Editor's
    /// match-accuracy display call this same method, so a fix or edge-case guard made here (or in
    /// <see cref="MouseRegistryBridge.EncodeRegCurve(double)"/>, which supplies the regCurve factor)
    /// can never silently apply to only one of the two consumers.
    /// </summary>
    public EffectiveSensitivityFactors GetEffectiveSensitivityFactors(MouseProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var baseProfile = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();

        var sens     = profile.SensMultiplier ?? 1.0;
        var winIndex = Math.Clamp((profile.WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
        var winMult  = WinMultipliers[winIndex];

        // EPP/DPI compensation — only applies when EPP is active.
        // Windows scales SmoothMouseCurve output by (logicalDpi / 96) internally,
        // so we divide by that factor to keep the effective value environment-neutral.
        var useEpp     = profile.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision;
        var logicalDpi = profile.TargetLogicalDpi ?? baseProfile?.TargetLogicalDpi ?? 96;

        // Registry curve fallback is 1.0 (neutral). DPI compensation is handled separately below
        // via a dedicated factor so the two corrections are never compounded.
        double rawRegValue = profile.RegistryCurveOverride ?? 1.0;

        // When EPP is explicitly disabled, the curve has no effect — treat reg factor as 1.0.
        double regCurve = (useEpp == false) ? 1.0 : MouseRegistryBridge.EncodeRegCurve(rawRegValue);

        // DPI compensation: Windows amplifies SmoothMouseCurve output by (logicalDpi / 96) when EPP is
        // active. Dividing by that factor normalises the effective value so profiles at different DPI
        // settings remain directly comparable. Applied regardless of whether reg override is explicit or
        // defaulted — exactly one factor per profile.
        double dpiComp = (useEpp != false) ? 96.0 / logicalDpi : 1.0;

        // Screen width compensation: a wider logical canvas requires proportionally more hand movement.
        // Ratio is base/profile so a profile designed for a narrower screen gets boosted to match base feel.
        var baseScreenWidth    = baseProfile?.TargetScreenWidth ?? profile.TargetScreenWidth;
        var profileScreenWidth = profile.TargetScreenWidth ?? baseProfile?.TargetScreenWidth;
        double screenComp = 1.0;
        if (baseScreenWidth.HasValue && profileScreenWidth.HasValue && profileScreenWidth.Value > 0)
            screenComp = (double)baseScreenWidth.Value / profileScreenWidth.Value;

        return new EffectiveSensitivityFactors(sens, winMult, regCurve, dpiComp, screenComp);
    }

    public double GetEffectiveSensitivity(MouseProfile profile) => GetEffectiveSensitivityFactors(profile).Full;

    /// <summary>
    /// Calculates the coarse (WinPointerSpeed) and fine (RegistryCurveOverride) compensations
    /// so that the total multiplier equals 1/x where x = S_target / S_base.
    /// Returns a tuple of (WinPointerSpeed?, RegistryCurveOverride?).
    /// </summary>
    public (int? WinPointerSpeed, double? RegistryCurveOverride) CalculateCompensations(MouseProfile activeProfile)
    {
        if (activeProfile == null) throw new ArgumentNullException(nameof(activeProfile));

        var baseProfile = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();
        if (baseProfile == null) return (activeProfile.WinPointerSpeed, activeProfile.RegistryCurveOverride);

        // Effective(base) = S_base × WinMult_base × EncodeRegCurve(RegCurve_base)
        // This is the single reference value both compensation steps target.
        var baseEffectiveSens = GetEffectiveSensitivity(baseProfile);
        if (baseEffectiveSens == 0) baseEffectiveSens = 1.0;

        var sTarget = activeProfile.SensMultiplier ?? 1.0;
        if (sTarget == 0) sTarget = 1.0;

        // Env correction for STEP 1: DPI and screen width change the feel, so Win Speed should absorb
        // as much of the cross-environment delta as the 20 discrete steps allow.
        // envCorrWin = dpi/96 × activeW/baseW — the inverse of what GetEffectiveSensitivity divides by.
        var useEppWin  = activeProfile.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision;
        var activeDpiWin = activeProfile.TargetLogicalDpi ?? baseProfile?.TargetLogicalDpi ?? 96;
        var baseWWin   = baseProfile?.TargetScreenWidth;
        var activeWWin = activeProfile.TargetScreenWidth ?? baseWWin;
        double envCorrWin = 1.0;
        if (useEppWin != false) envCorrWin *= (double)activeDpiWin / 96.0;
        if (baseWWin.HasValue && activeWWin.HasValue && baseWWin.Value > 0)
            envCorrWin *= (double)activeWWin.Value / baseWWin.Value;

        var targetWinMult = (baseEffectiveSens * envCorrWin) / sTarget; // goal for WinMult — matches ApplyAutoOSSync in ViewModel

        int? winSpeedResult = activeProfile.WinPointerSpeed;
        double? regResult = activeProfile.RegistryCurveOverride;

        // STEP 1: Choose best Windows Pointer Speed
        if (activeProfile.OSSyncMode == SyncMode.Auto)
        {
            int bestIndex  = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < WinMultipliers.Length; i++)
            {
                double diff = Math.Abs(WinMultipliers[i] - targetWinMult);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestIndex = i;
                }
            }
            winSpeedResult = bestIndex + 1; // 1-based
        }

        // STEP 2: RegCurve = Effective(base) / (S_target × WinMult_chosen)
        // so that S_target × WinMult × RegCurve == Effective(base) exactly.
        if (activeProfile.RegSyncMode == SyncMode.Auto)
        {
            var currentWinMult = WinMultipliers[Math.Clamp((winSpeedResult ?? 10) - 1, 0, WinMultipliers.Length - 1)];
            var raw = (sTarget * currentWinMult) == 0 ? 1.0 : baseEffectiveSens / (sTarget * currentWinMult);

            // Env correction: the stored regResult must compensate for the active profile's DPI
            // and screen width so that GetEffectiveSensitivity(active) == GetEffectiveSensitivity(base).
            var baseProfile2 = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();
            var activeDpi   = activeProfile.TargetLogicalDpi ?? baseProfile2?.TargetLogicalDpi ?? 96;
            var baseW2      = baseProfile2?.TargetScreenWidth;
            var activeW     = activeProfile.TargetScreenWidth ?? baseW2;
            var useEppReg   = activeProfile.UseEnhancePointerPrecision ?? baseProfile2?.UseEnhancePointerPrecision;

            double envCorrection = (useEppReg != false) ? (double)activeDpi / 96.0 : 1.0;
            if (baseW2.HasValue && activeW.HasValue && baseW2.Value > 0)
                envCorrection *= (double)activeW.Value / baseW2.Value;

            regResult = raw * envCorrection;
        }

        return (winSpeedResult, regResult);
    }
}
