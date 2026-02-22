using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MouseApp.Models;

namespace MouseApp.Services;

public class ProfileService
{
    private readonly string _dataFile;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    // Lookup table mapping Windows Pointer Speed (1..20) to their effective multipliers
    private static readonly double[] WinMultipliers =
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
                ProfileName = "Global Default",
                SensMultiplier = 1.0,
                UseAutoOSSync = true,
                UseAutoRegSync = true,
                IsGlobalDefault = true,
                TargetExecutables = new()
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
                ProfileName = "Global Default",
                SensMultiplier = 1.0,
                UseAutoOSSync = true,
                UseAutoRegSync = true,
                IsGlobalDefault = true,
                TargetExecutables = new()
            });
        }

        NormalizeGlobalDefault(list);

        Profiles.Clear();
        foreach (var p in list)
            Profiles.Add(p);
    }

    public async Task SaveAsync()
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

        var sBase = baseProfile.SensMultiplier ?? 1.0;
        var sTarget = activeProfile.SensMultiplier ?? sBase;
        var x = sTarget / sBase;
        if (x == 0) x = 1.0;

        var targetMultiplier = 1.0 / x; // goal for WinMult * RegMult

        int? winSpeedResult = activeProfile.WinPointerSpeed;
        double? regResult = activeProfile.RegistryCurveOverride;

        // STEP 1: Choose best Windows Pointer Speed
        if (activeProfile.UseAutoOSSync)
        {
            int bestIndex = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < WinMultipliers.Length; i++)
            {
                double diff = Math.Abs(WinMultipliers[i] - targetMultiplier);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestIndex = i;
                }
            }
            winSpeedResult = bestIndex + 1; // 1-based
        }

        // STEP 2: Calculate exact registry remainder so WinMult × RegCurve == targetMultiplier
        if (activeProfile.UseAutoRegSync)
        {
            double currentWinMult = WinMultipliers[Math.Clamp((winSpeedResult ?? 10) - 1, 0, WinMultipliers.Length - 1)];
            regResult = currentWinMult == 0 ? 1.0 : targetMultiplier / currentWinMult;
        }

        return (winSpeedResult, regResult);
    }
}
