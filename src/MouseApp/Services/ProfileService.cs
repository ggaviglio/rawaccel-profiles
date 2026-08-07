using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        Converters    = { new JsonStringEnumConverter(), new LenientPositiveDoubleConverter(), new LenientPositiveIntConverter() }
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

    /// <summary>
    /// Sensitivity and RegistryCurveOverride both only accept strictly-positive numbers (0 or
    /// negative is not a meaningful multiplier for either), and no more than 15 significant
    /// digits -- the same guaranteed-safe round-trip budget IEEE 754 double precision offers. A
    /// double literally cannot hold more than that without silently losing information; a huge
    /// typed number (20+ digits) that skipped this check used to display fine while typing, then
    /// visibly fall apart (a wall of trailing zeros) the next time it had to become text again,
    /// because nothing had ever told the user precision was about to be lost.
    /// Public and static (not just for typed GUI input) because <see cref="LenientPositiveDoubleConverter"/>
    /// below reuses this exact same check when reading profiles.json, so a value that's invalid
    /// for one reason (typed) is invalid for the identical reason when loaded from disk --
    /// one rule, one place, instead of two independently hand-typed copies that could drift.
    /// </summary>
    public static bool TryParsePositiveDouble(string text, out double parsed)
    {
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
            && parsed > 0
            && CountMantissaDigits(text) <= 15)
        {
            return true;
        }
        parsed = 0;
        return false;
    }

    /// <summary>
    /// Counts digit characters in the mantissa of a typed number, skipping leading zeros (e.g.
    /// "007" has 1) and ignoring anything after 'e'/'E' so a legitimately-typed exponent isn't
    /// miscounted as extra mantissa precision.
    /// </summary>
    private static int CountMantissaDigits(string text)
    {
        int eIndex = text.IndexOfAny(new[] { 'e', 'E' });
        ReadOnlySpan<char> mantissa = eIndex >= 0 ? text.AsSpan(0, eIndex) : text.AsSpan();

        bool seenNonzero = false;
        int count = 0;
        foreach (char c in mantissa)
        {
            if (c < '0' || c > '9') continue;
            if (c != '0') seenNonzero = true;
            if (seenNonzero) count++;
        }
        return count;
    }

    /// <summary>Same idea as <see cref="TryParsePositiveDouble"/>, for the int-typed fields
    /// (TargetScreenWidth, TargetLogicalDpi) -- a pixel width or DPI of 0 or negative is not
    /// meaningful either.</summary>
    public static bool TryParsePositiveInt(string text, out int parsed) =>
        int.TryParse(text, out parsed) && parsed > 0;

    /// <summary>
    /// Reads a SensMultiplier/RegistryCurveOverride field using the exact same rule typed input
    /// already has to pass (<see cref="TryParsePositiveDouble"/>) -- anything that isn't a real,
    /// positive, <=15-digit number (a word, a negative number, too many digits, or just the
    /// wrong JSON shape) is treated as if the field were left blank, instead of throwing and
    /// taking down the whole profile load. Every kind of "invalid" is handled the identical way,
    /// on purpose -- there's no special case for "invalid but still a number."
    /// </summary>
    private sealed class LenientPositiveDoubleConverter : System.Text.Json.Serialization.JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number) return null;
            string rawText = Encoding.UTF8.GetString(reader.ValueSpan);
            return TryParsePositiveDouble(rawText, out var parsed) ? parsed : null;
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }

    /// <summary>Same idea as <see cref="LenientPositiveDoubleConverter"/>, for the int-typed
    /// fields (WinPointerSpeed, TargetScreenWidth, TargetLogicalDpi).</summary>
    private sealed class LenientPositiveIntConverter : System.Text.Json.Serialization.JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number) return null;
            string rawText = Encoding.UTF8.GetString(reader.ValueSpan);
            return TryParsePositiveInt(rawText, out var parsed) ? parsed : null;
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
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
    /// Reads a profile's TargetLogicalDpi/TargetScreenWidth/RegistryCurveOverride, but as null
    /// whenever the corresponding Settings toggle (ShowAutoMatchEnvironment/ShowEppOverride) is
    /// off -- regardless of what's actually stored. Hiding these fields in the Profile Editor is
    /// meant to make them functionally inert, not just invisible while still secretly affecting
    /// behavior (found live: an invisible-but-active setting is worse than a visible one that's
    /// easy to miss, which already happened once tonight). Every consumer of these three fields
    /// -- GetEffectiveSensitivityFactors, CalculateCompensations, MouseService.ApplyProfileAsync's
    /// registry write, and the ViewModel's own Auto-sync preview functions -- reads through these
    /// helpers instead of the raw MouseProfile property, so the "is this enabled" check exists in
    /// exactly one place per field. The stored value itself is never touched here; a profile's
    /// real data survives untouched under a hidden setting and resumes working the instant the
    /// setting is turned back on. Null-safe on <paramref name="profile"/> since callers often pass
    /// a `Profiles.FirstOrDefault(...)`-style lookup that can come back null.
    /// </summary>
    public int? GetEffectiveTargetLogicalDpi(MouseProfile? profile) =>
        Settings.ShowAutoMatchEnvironment ? profile?.TargetLogicalDpi : null;

    /// <summary>Same idea as <see cref="GetEffectiveTargetLogicalDpi"/>, for TargetScreenWidth.</summary>
    public int? GetEffectiveTargetScreenWidth(MouseProfile? profile) =>
        Settings.ShowAutoMatchEnvironment ? profile?.TargetScreenWidth : null;

    /// <summary>Same idea as <see cref="GetEffectiveTargetLogicalDpi"/>, for RegistryCurveOverride
    /// (gated on ShowEppOverride instead of ShowAutoMatchEnvironment).</summary>
    public double? GetEffectiveRegistryCurveOverride(MouseProfile? profile) =>
        Settings.ShowEppOverride ? profile?.RegistryCurveOverride : null;

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
        var logicalDpi = GetEffectiveTargetLogicalDpi(profile) ?? GetEffectiveTargetLogicalDpi(baseProfile) ?? 96;

        // Registry curve fallback is 1.0 (neutral). DPI compensation is handled separately below
        // via a dedicated factor so the two corrections are never compounded.
        double rawRegValue = GetEffectiveRegistryCurveOverride(profile) ?? 1.0;

        // When EPP is explicitly disabled, the curve has no effect — treat reg factor as 1.0.
        // EncodeRegCurve throws if rawRegValue is extreme enough to overflow 16.16 fixed-point
        // storage (a real, intentional guard — see CLAUDE.md). That failure is specific to the
        // registry curve alone; it must not take sens/winMult/dpiComp/screenComp down with it,
        // since Raw Accel Only and Win Speed Only (which only need those other factors) have
        // nothing to do with the registry curve and remain perfectly computable. Represent an
        // unrepresentable regCurve as NaN instead of throwing, so only .Full (which actually
        // multiplies it in) becomes NaN — RawOnly/WinOnly stay real, valid numbers.
        double regCurve;
        if (useEpp == false)
        {
            regCurve = 1.0;
        }
        else
        {
            try { regCurve = MouseRegistryBridge.EncodeRegCurve(rawRegValue); }
            catch (ArgumentOutOfRangeException) { regCurve = double.NaN; }
        }

        // DPI compensation: Windows amplifies SmoothMouseCurve output by (logicalDpi / 96) when EPP is
        // active. Dividing by that factor normalises the effective value so profiles at different DPI
        // settings remain directly comparable. Applied regardless of whether reg override is explicit or
        // defaulted — exactly one factor per profile.
        double dpiComp = (useEpp != false) ? 96.0 / logicalDpi : 1.0;

        // Screen width compensation: a wider logical canvas requires proportionally more hand movement.
        // Ratio is base/profile so a profile designed for a narrower screen gets boosted to match base feel.
        var baseScreenWidth    = GetEffectiveTargetScreenWidth(baseProfile) ?? GetEffectiveTargetScreenWidth(profile);
        var profileScreenWidth = GetEffectiveTargetScreenWidth(profile) ?? GetEffectiveTargetScreenWidth(baseProfile);
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

        // Effective environment values, computed once and reused by both steps below. Previously
        // Step 1 and Step 2 each independently re-fetched baseProfile (as a redundant second
        // query, "baseProfile2") and re-derived these same three values from scratch — two
        // hand-typed copies of the identical formula that happened to still agree, but could have
        // silently drifted. Routed through GetEffectiveTargetLogicalDpi/GetEffectiveTargetScreenWidth
        // so a value is treated as absent whenever Settings.ShowAutoMatchEnvironment is off,
        // exactly like a genuinely-blank field already behaves — see those helpers' doc comments.
        var useEppWin    = activeProfile.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision;
        var effectiveDpi = GetEffectiveTargetLogicalDpi(activeProfile) ?? GetEffectiveTargetLogicalDpi(baseProfile) ?? 96;
        var baseWidth    = GetEffectiveTargetScreenWidth(baseProfile);
        var activeWidth  = GetEffectiveTargetScreenWidth(activeProfile) ?? baseWidth;

        // Env correction for STEP 1: DPI and screen width change the feel, so Win Speed should absorb
        // as much of the cross-environment delta as the 20 discrete steps allow.
        // envCorrWin = dpi/96 × activeW/baseW — the inverse of what GetEffectiveSensitivity divides by.
        double envCorrWin = 1.0;
        if (useEppWin != false) envCorrWin *= (double)effectiveDpi / 96.0;
        if (baseWidth.HasValue && activeWidth.HasValue && baseWidth.Value > 0)
            envCorrWin *= (double)activeWidth.Value / baseWidth.Value;

        var targetWinMult = (baseEffectiveSens * envCorrWin) / sTarget; // goal for WinMult — matches ApplyAutoOSSync in ViewModel

        int? winSpeedResult = activeProfile.WinPointerSpeed;
        double? regResult = GetEffectiveRegistryCurveOverride(activeProfile);

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
            // Reuses the same effectiveDpi/baseWidth/activeWidth computed once above.
            double envCorrection = (useEppWin != false) ? (double)effectiveDpi / 96.0 : 1.0;
            if (baseWidth.HasValue && activeWidth.HasValue && baseWidth.Value > 0)
                envCorrection *= (double)activeWidth.Value / baseWidth.Value;

            regResult = raw * envCorrection;
        }

        return (winSpeedResult, regResult);
    }
}
