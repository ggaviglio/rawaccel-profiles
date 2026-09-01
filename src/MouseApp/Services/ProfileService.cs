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
    /// Also requires <see cref="double.IsFinite"/>: a typed value like "1e999" has only one
    /// mantissa digit (passes the 15-digit check) but <see cref="double.TryParse"/> silently
    /// converts an out-of-range literal to <see cref="double.PositiveInfinity"/> instead of
    /// failing. That used to pass this check, get stored on the profile, and then crash the app
    /// on the next save -- <see cref="LenientPositiveDoubleConverter.Write"/> hands Infinity to
    /// <c>Utf8JsonWriter.WriteNumberValue</c>, which throws, since JSON has no representation for
    /// it. Found live via that exact crash.
    /// </summary>
    public static bool TryParsePositiveDouble(string text, out double parsed)
    {
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
            && double.IsFinite(parsed)
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
    /// (TargetScreenWidth) -- a pixel width of 0 or negative is not meaningful either.</summary>
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
    /// fields (WinPointerSpeed, TargetScreenWidth).</summary>
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
        var json = File.Exists(_dataFile)
            ? await File.ReadAllTextAsync(_dataFile).ConfigureAwait(false)
            : string.Empty;

        // An empty/whitespace-only file gets the same fallback as a missing one -- reachable if
        // a previous save was ever interrupted mid-write and left a truncated file behind (see
        // SaveAsync/Save's temp-file-then-replace pattern, added specifically so this can't
        // happen going forward; this branch is what makes recovering from an old one possible
        // instead of crashing JsonDocument.Parse on an empty string at startup).
        if (string.IsNullOrWhiteSpace(json))
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

    /// <summary>
    /// Writes to a "<c>.tmp</c>" sibling first, only replacing the real file once that write
    /// fully succeeds -- found live: the old code called <see cref="File.Create(string)"/>
    /// directly on <see cref="_dataFile"/>, which truncates it to empty immediately, before any
    /// of the new content is written. When <c>JsonSerializer.SerializeAsync</c> then threw
    /// partway through (an unrelated bug, since fixed, in <see cref="TryParsePositiveDouble"/>
    /// letting a non-finite value reach the writer), the file was left permanently empty --
    /// every previously-saved profile gone, with nothing to recover from, since nothing else in
    /// this codebase kept a backup. <see cref="File.Move(string, string, bool)"/>'s overwrite is
    /// atomic on the same volume, so a save that fails now leaves the previous good file exactly
    /// as it was, for ANY reason it might fail, not just this one already-fixed case.
    /// </summary>
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = BuildPersistedData();
            EnsureDirectoryExists();

            var tempFile = _dataFile + ".tmp";
            using (var fs = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(fs, data, _jsonOptions).ConfigureAwait(false);
                await fs.FlushAsync().ConfigureAwait(false);
            }
            File.Move(tempFile, _dataFile, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Synchronous save for settings-driven changes (checkbox toggles, folder pickers).
    /// Same temp-file-then-replace pattern as <see cref="SaveAsync"/> -- see its doc comment.</summary>
    public void Save()
    {
        _saveLock.Wait();
        try
        {
            var data = BuildPersistedData();
            EnsureDirectoryExists();

            var tempFile = _dataFile + ".tmp";
            File.WriteAllText(tempFile, JsonSerializer.Serialize(data, _jsonOptions));
            File.Move(tempFile, _dataFile, overwrite: true);
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
        // Same invariant for YXRatioOverride -- ApplyAutoYXRatioSync already refuses to compute
        // on the Default profile at the ViewModel level (IsDefault guard), but SyncMode itself
        // is a plain enum whose implicit default is Auto (first declared member) -- any profile,
        // Default included, that never had this field explicitly written (e.g. loaded from a
        // profiles.json predating this feature) deserializes to Auto, not Skip, unless something
        // corrects it. Caught this live: a batch of pre-existing profiles all silently came back
        // as YXRatioSyncMode=Auto after one save, purely from the enum default, before this fix.
        if (defaultProfile.YXRatioSyncMode == SyncMode.Auto)
        {
            defaultProfile.YXRatioSyncMode = SyncMode.Skip;
            defaultProfile.YXRatioOverride = null;
        }
    }

    /// <summary>
    /// Returns the total effective sensitivity for a profile, including environment compensation:
    ///   SensMultiplier × WinMult[WinPointerSpeed] × EncodeRegCurve(RegistryCurveOverride)
    ///   × (baseScreenWidth / profileScreenWidth)  [resolution compensation]
    /// RegCurve is round-tripped through 16.16 fixed-point encoding so this matches what
    /// Windows will actually apply — the single source-of-truth for MatchAccuracy comparisons.
    /// Null fields inherit from the Default profile; fully-null profile yields 100% match by definition.
    ///
    /// No DPI compensation factor: DPI only ever fed the actual registry-curve write via a single
    /// system-wide value (see MouseRegistryBridge), never a per-profile one, so a per-profile DPI
    /// comparison was always mathematically a no-op once verified against the real MarkC mechanism
    /// -- removed along with the manual TargetLogicalDpi field it depended on, rather than kept as
    /// permanently-1.0 dead weight in this formula.
    /// </summary>
    /// <summary>
    /// The individual multipliers <see cref="GetEffectiveSensitivity"/> combines into one number.
    /// Exposed so callers that need a *partial* product (e.g. the Profile Editor's match-accuracy
    /// display, which shows "Raw Accel only" and "Raw Accel + Win Speed" rows before the final
    /// combined row) can reuse the exact same per-factor calculation instead of re-deriving it —
    /// see the "single source of truth" note on <see cref="GetEffectiveSensitivityFactors"/>.
    /// </summary>
    public readonly record struct EffectiveSensitivityFactors(
        double Sens, double WinMult, double RegCurve, double ScreenComp)
    {
        /// <summary>Raw Accel sensitivity alone, with environment (screen width) compensation applied.</summary>
        public double RawOnly => Sens * ScreenComp;

        /// <summary>Raw Accel + Win Pointer Speed, with environment compensation — no registry curve yet.</summary>
        public double WinOnly => RawOnly * WinMult;

        /// <summary>All four factors combined — identical to <see cref="GetEffectiveSensitivity"/>'s result.</summary>
        public double Full => WinOnly * RegCurve;
    }

    /// <summary>
    /// Reads a profile's TargetScreenWidth/RegistryCurveOverride, but as null
    /// whenever the corresponding Settings toggle (ShowAutoMatchEnvironment/ShowEppOverride) is
    /// off -- regardless of what's actually stored. Hiding these fields in the Profile Editor is
    /// meant to make them functionally inert, not just invisible while still secretly affecting
    /// behavior (found live: an invisible-but-active setting is worse than a visible one that's
    /// easy to miss, which already happened once tonight). Every consumer of these two fields
    /// -- GetEffectiveSensitivityFactors, CalculateCompensations, MouseService.ApplyProfileAsync's
    /// registry write, and the ViewModel's own Auto-sync preview functions -- reads through these
    /// helpers instead of the raw MouseProfile property, so the "is this enabled" check exists in
    /// exactly one place per field. The stored value itself is never touched here; a profile's
    /// real data survives untouched under a hidden setting and resumes working the instant the
    /// setting is turned back on. Null-safe on <paramref name="profile"/> since callers often pass
    /// a `Profiles.FirstOrDefault(...)`-style lookup that can come back null.
    /// </summary>
    public int? GetEffectiveTargetScreenWidth(MouseProfile? profile) =>
        Settings.ShowAutoMatchEnvironment ? profile?.TargetScreenWidth : null;

    /// <summary>Same idea as <see cref="GetEffectiveTargetScreenWidth"/>, for TargetScreenHeight.
    /// Not yet consumed by the math engine -- plumbing only, added ahead of the aspect-ratio
    /// compensation feature it's meant to support.</summary>
    public int? GetEffectiveTargetScreenHeight(MouseProfile? profile) =>
        Settings.ShowAutoMatchEnvironment ? profile?.TargetScreenHeight : null;

    /// <summary>Same idea as <see cref="GetEffectiveTargetScreenWidth"/>, for RegistryCurveOverride
    /// (gated on ShowEppOverride instead of ShowAutoMatchEnvironment).</summary>
    public double? GetEffectiveRegistryCurveOverride(MouseProfile? profile) =>
        Settings.ShowEppOverride ? profile?.RegistryCurveOverride : null;

    /// <summary>Same idea as <see cref="GetEffectiveTargetScreenWidth"/>, for YXRatioOverride --
    /// gated on ShowAutoMatchEnvironment since the ratio is causally derived from Width/Height,
    /// which are already gated by it. Not yet consumed anywhere: doesn't feed the Windows-side
    /// effective-sensitivity math (GetEffectiveSensitivityFactors/CalculateCompensations) at all
    /// -- this is a separate value headed for Raw Accel's own settings.json, wired in a later
    /// step (RawAccelService.ApplySettingsFileAsync).</summary>
    public double? GetEffectiveYXRatioOverride(MouseProfile? profile) =>
        Settings.ShowAutoMatchEnvironment ? profile?.YXRatioOverride : null;

    /// <summary>
    /// Resolves whether EPP is active: the profile's own value, else the Global Default's, else
    /// <c>false</c> if neither specifies one. The final tier matters: previously every call site
    /// wrote <c>profile.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision</c>
    /// with no explicit last resort, so a totally-unspecified EPP stayed <c>null</c> -- and every
    /// downstream <c>useEpp != false</c> check silently treated <c>null</c> as "on" (an accident
    /// of C# nullable-bool comparison, not a deliberate default). <c>false</c> matches Windows'
    /// own out-of-the-box default and what <see cref="MouseService"/>'s real apply path already
    /// converges to when EPP is untouched everywhere (it leaves the registry alone rather than
    /// writing anything). Single implementation so all callers resolve EPP identically.
    /// </summary>
    public static bool ResolveEpp(MouseProfile? profile, MouseProfile? baseProfile) =>
        profile?.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision ?? false;

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

        var sens     = profile.SensMultiplier ?? baseProfile?.SensMultiplier ?? 1.0;
        var winIndex = Math.Clamp((profile.WinPointerSpeed ?? baseProfile?.WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1);
        var winMult  = WinMultipliers[winIndex];

        var useEpp = ResolveEpp(profile, baseProfile);

        // Registry curve: profile's own value, else the Global Default's, else neutral (1.0) if
        // neither specifies one.
        double rawRegValue = GetEffectiveRegistryCurveOverride(profile) ?? GetEffectiveRegistryCurveOverride(baseProfile) ?? 1.0;

        // When EPP is explicitly disabled, the curve has no effect — treat reg factor as 1.0.
        // EncodeRegCurve throws if rawRegValue is extreme enough to overflow 16.16 fixed-point
        // storage (a real, intentional guard — see CLAUDE.md). That failure is specific to the
        // registry curve alone; it must not take sens/winMult/screenComp down with it,
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

        // Screen width compensation: a wider logical canvas requires proportionally more hand movement.
        // Ratio is base/profile so a profile designed for a narrower screen gets boosted to match base feel.
        var baseScreenWidth    = GetEffectiveTargetScreenWidth(baseProfile) ?? GetEffectiveTargetScreenWidth(profile);
        var profileScreenWidth = GetEffectiveTargetScreenWidth(profile) ?? GetEffectiveTargetScreenWidth(baseProfile);
        double screenComp = 1.0;
        if (baseScreenWidth.HasValue && profileScreenWidth.HasValue && profileScreenWidth.Value > 0)
            screenComp = (double)baseScreenWidth.Value / profileScreenWidth.Value;

        return new EffectiveSensitivityFactors(sens, winMult, regCurve, screenComp);
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

        var sTarget = activeProfile.SensMultiplier ?? baseProfile.SensMultiplier ?? 1.0;
        if (sTarget == 0) sTarget = 1.0;

        // Effective environment values, computed once and reused by both steps below. Previously
        // Step 1 and Step 2 each independently re-fetched baseProfile (as a redundant second
        // query, "baseProfile2") and re-derived these same values from scratch — two
        // hand-typed copies of the identical formula that happened to still agree, but could have
        // silently drifted. Routed through GetEffectiveTargetScreenWidth so a value is treated as
        // absent whenever Settings.ShowAutoMatchEnvironment is off, exactly like a genuinely-blank
        // field already behaves — see that helper's doc comment.
        var baseWidth    = GetEffectiveTargetScreenWidth(baseProfile);
        var activeWidth  = GetEffectiveTargetScreenWidth(activeProfile) ?? baseWidth;

        // Env correction for STEP 1: screen width changes the feel, so Win Speed should absorb
        // as much of the cross-environment delta as the 20 discrete steps allow.
        // envCorrWin = activeW/baseW — the inverse of what GetEffectiveSensitivity divides by.
        double envCorrWin = 1.0;
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
            var currentWinMult = WinMultipliers[Math.Clamp((winSpeedResult ?? baseProfile.WinPointerSpeed ?? 10) - 1, 0, WinMultipliers.Length - 1)];
            var raw = (sTarget * currentWinMult) == 0 ? 1.0 : baseEffectiveSens / (sTarget * currentWinMult);

            // Env correction: the stored regResult must compensate for the active profile's screen
            // width so that GetEffectiveSensitivity(active) == GetEffectiveSensitivity(base).
            // Reuses the same baseWidth/activeWidth computed once above.
            double envCorrection = 1.0;
            if (baseWidth.HasValue && activeWidth.HasValue && baseWidth.Value > 0)
                envCorrection *= (double)activeWidth.Value / baseWidth.Value;

            regResult = raw * envCorrection;
        }

        return (winSpeedResult, regResult);
    }
}
