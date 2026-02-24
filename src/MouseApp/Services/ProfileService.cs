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
                UseAutoOSSync      = false, // Global Default is the reference — auto-sync has no meaning here
                UseAutoRegSync     = false,
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
                UseAutoOSSync      = false,
                UseAutoRegSync     = false,
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
        // If UseAutoOSSync/UseAutoRegSync were previously true, they may have written
        // a non-neutral WinPointerSpeed or RegistryCurveOverride into the stored profile,
        // poisoning Effective(base) for all proof-of-match calculations on other profiles.
        var gd = globals[0];
        if (gd.UseAutoOSSync)
        {
            gd.UseAutoOSSync   = false;
            gd.WinPointerSpeed = null; // reset to neutral (10 = 1.0×)
        }
        if (gd.UseAutoRegSync)
        {
            gd.UseAutoRegSync        = false;
            gd.RegistryCurveOverride = null; // reset to neutral (1.0×)
        }
    }

    /// <summary>
    /// Returns the total effective sensitivity for a profile:
    ///   (SensMultiplier ?? 1.0) × WinMult[WinPointerSpeed] × (RegistryCurveOverride ?? 1.0).
    /// This is the single source-of-truth for MatchAccuracy comparisons.
    /// </summary>
    public double GetEffectiveSensitivity(MouseProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var sens     = profile.SensMultiplier ?? 1.0;
        var winIndex = Math.Clamp((profile.WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
        var winMult  = WinMultipliers[winIndex];
        var regCurve = profile.RegistryCurveOverride ?? 1.0;

        return sens * winMult * regCurve;
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

        // Win-level baseline of the Global Default: S_base × WinMult_base
        // Active profile's WinMult must satisfy: S_target × WinMult ≈ S_base × WinMult_base
        var sBase        = baseProfile.SensMultiplier ?? 1.0;
        if (sBase == 0) sBase = 1.0;
        var winBaseIdx   = Math.Clamp((baseProfile.WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
        var winBaseMult  = WinMultipliers[winBaseIdx];

        var sTarget = activeProfile.SensMultiplier ?? 1.0;
        if (sTarget == 0) sTarget = 1.0;

        var targetWinMult = (sBase * winBaseMult) / sTarget; // goal for WinMult

        int? winSpeedResult = activeProfile.WinPointerSpeed;
        double? regResult = activeProfile.RegistryCurveOverride;

        // STEP 1: Choose best Windows Pointer Speed
        if (activeProfile.UseAutoOSSync)
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
        if (activeProfile.UseAutoRegSync)
        {
            var baseEffective  = GetEffectiveSensitivity(baseProfile);
            if (baseEffective == 0) baseEffective = 1.0;
            var currentWinMult = WinMultipliers[Math.Clamp((winSpeedResult ?? 10) - 1, 0, WinMultipliers.Length - 1)];
            regResult = (sTarget * currentWinMult) == 0 ? 1.0 : baseEffective / (sTarget * currentWinMult);
        }

        return (winSpeedResult, regResult);
    }
}
