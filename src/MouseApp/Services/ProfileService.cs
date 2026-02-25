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
    /// Serializes all file writes so concurrent AutoPersist + SetGlobalDefaultAsync calls
    /// never race on File.Create() and truncate each other's output.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

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
            // Create a sensible Global Default starting point
            var global = new MouseProfile
            {
                ProfileName        = "Global Default",
                SensMultiplier     = 1.0,
                OSSyncMode         = SyncMode.Skip, // Global Default is the reference — auto-sync has no meaning here
                RegSyncMode        = SyncMode.Skip,
                IsGlobalDefault    = true,
                TargetAssociations = new()
            };

            Profiles.Clear();
            Profiles.Add(global);
            await SaveAsync().ConfigureAwait(false);
            return;
        }

        using var fs = File.OpenRead(_dataFile);
        var list = await JsonSerializer.DeserializeAsync<List<MouseProfile>>(fs, _jsonOptions).ConfigureAwait(false) ?? new List<MouseProfile>();

        if (!list.Any())
        {
            list.Add(new MouseProfile
            {
                ProfileName        = "Global Default",
                SensMultiplier     = 1.0,
                OSSyncMode         = SyncMode.Skip,
                RegSyncMode        = SyncMode.Skip,
                IsGlobalDefault    = true,
                TargetAssociations = new()
            });
        }

        NormalizeGlobalDefault(list);

        Profiles.Clear();
        foreach (var p in list)
            Profiles.Add(p);
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var list = Profiles.ToList();
            NormalizeGlobalDefault(list);

            var dir = Path.GetDirectoryName(_dataFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(_dataFile);
            await JsonSerializer.SerializeAsync(fs, list, _jsonOptions).ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task SetGlobalDefaultAsync(MouseProfile p)
    {
        foreach (var pr in Profiles)
            pr.IsGlobalDefault = false;

        p.IsGlobalDefault = true;
        await SaveAsync().ConfigureAwait(false);
    }

    private static void NormalizeGlobalDefault(List<MouseProfile> list)
    {
        if (list.Count == 0)
            return;

        var globals = list.Where(p => p.IsGlobalDefault).ToList();
        if (globals.Count == 0)
        {
            list[0].IsGlobalDefault = true;
            return;
        }

        foreach (var g in globals.Skip(1))
            g.IsGlobalDefault = false;

        // The Global Default must never have auto-computed OS compensation values.
        // If OSSyncMode/RegSyncMode were previously Auto, they may have written
        // a non-neutral WinPointerSpeed or RegistryCurveOverride into the stored profile,
        // poisoning Effective(base) for all proof-of-match calculations on other profiles.
        var gd = globals[0];
        if (gd.OSSyncMode == SyncMode.Auto)
        {
            gd.OSSyncMode      = SyncMode.Skip;
            gd.WinPointerSpeed = null; // reset to neutral (10 = 1.0×)
        }
        if (gd.RegSyncMode == SyncMode.Auto)
        {
            gd.RegSyncMode           = SyncMode.Skip;
            gd.RegistryCurveOverride = null; // reset to neutral (1.0×)
        }
    }

    /// <summary>
    /// Returns the total effective sensitivity for a profile, including environment compensation:
    ///   SensMultiplier × WinMult[WinPointerSpeed] × EncodeRegCurve(RegistryCurveOverride)
    ///   × (96 / logicalDpi)  [DPI compensation when EPP active]
    ///   × (baseScreenWidth / profileScreenWidth)  [resolution compensation]
    /// RegCurve is round-tripped through 16.16 fixed-point encoding so this matches what
    /// Windows will actually apply — the single source-of-truth for MatchAccuracy comparisons.
    /// Null fields inherit from Global Default; fully-null profile yields 100% match by definition.
    /// </summary>
    public double GetEffectiveSensitivity(MouseProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var baseProfile = Profiles.FirstOrDefault(p => p.IsGlobalDefault) ?? Profiles.FirstOrDefault();

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

        double result = sens * winMult * regCurve;

        // DPI compensation: Windows amplifies SmoothMouseCurve output by (logicalDpi / 96) when EPP is
        // active. Dividing by that factor normalises the effective value so profiles at different DPI
        // settings remain directly comparable. Applied regardless of whether reg override is explicit or
        // defaulted — exactly one factor per profile.
        if (useEpp != false)
            result *= 96.0 / logicalDpi;

        // Screen width compensation: a wider logical canvas requires proportionally more hand movement.
        // Ratio is base/profile so a profile designed for a narrower screen gets boosted to match base feel.
        var baseScreenWidth    = baseProfile?.TargetScreenWidth ?? profile.TargetScreenWidth;
        var profileScreenWidth = profile.TargetScreenWidth ?? baseProfile?.TargetScreenWidth;
        if (baseScreenWidth.HasValue && profileScreenWidth.HasValue && profileScreenWidth.Value > 0)
            result *= (double)baseScreenWidth.Value / profileScreenWidth.Value;

        return result;
    }

    /// <summary>
    /// Calculates the coarse (WinPointerSpeed) and fine (RegistryCurveOverride) compensations
    /// so that the total multiplier equals 1/x where x = S_target / S_base.
    /// Returns a tuple of (WinPointerSpeed?, RegistryCurveOverride?).
    /// </summary>
    public (int? WinPointerSpeed, double? RegistryCurveOverride) CalculateCompensations(MouseProfile activeProfile)
    {
        if (activeProfile == null) throw new ArgumentNullException(nameof(activeProfile));

        var baseProfile = Profiles.FirstOrDefault(p => p.IsGlobalDefault) ?? Profiles.FirstOrDefault();
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
            var baseProfile2 = Profiles.FirstOrDefault(p => p.IsGlobalDefault) ?? Profiles.FirstOrDefault();
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
