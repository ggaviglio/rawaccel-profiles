# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project identity

MouseApp is a Windows-only Avalonia UI (MVVM) desktop utility. It monitors the
foreground process/window and swaps mouse sensitivity "profiles," mathematically
balancing Raw Accel sensitivity, Windows Pointer Speed, and a Registry curve
override so effective cursor speed stays consistent across applications with
different DPI/resolution/acceleration settings. Always treat `README.md` as the
source of truth for product identity and GUI structure — read it before making
UI or behavioral changes.

## Build / run / package

There is no test suite in this repo (no test project exists).

```powershell
# Build (Debug)
dotnet build src/MouseApp/MouseApp.csproj -c Debug

# Run
dotnet run --project src/MouseApp/MouseApp.csproj

# Publish self-contained single-file win-x64 Release build
./scripts/publish.ps1

# Publish + zip into dist/MouseApp_v<version>.zip
./scripts/package.ps1
```

These same steps are also wired up as VS Code tasks (`build`, `clean`, `publish`, `package`; see `.vscode/tasks.json`).

Target framework is `net10.0`, `LangVersion` is `preview`. The app is Windows-only
(P/Invoke into `user32.dll`, registry access, `SystemParametersInfo`) — code that
touches hardware must be guarded with `OperatingSystem.IsWindows()` since the
project can still be built/analyzed cross-platform.

## Architecture

**MVVM with CommunityToolkit.Mvvm**, no DI container — `App.axaml.cs`
(`OnFrameworkInitializationCompleted`) manually constructs every service and
view-model in dependency order and wires them together. Read that method first
when tracing how anything is connected. Navigation between the Profile Editor
and Settings screens is done via `NavigationService` (a simple
`Current`/`CurrentChanged` pointer swap), not a router. Views are resolved from
view-models by `ViewLocator.cs` (naming convention, not registration).

New `MouseProfile`/ViewModel properties follow CommunityToolkit's
`[ObservableProperty]` + manual `partial void On*Changed` callback pattern —
mirror the existing style rather than introducing a different MVVM idiom.

### Core domain model

A `MouseProfile` (`Models/MouseProfile.cs`) bundles: a Raw Accel template
reference, a sensitivity multiplier, Windows Pointer Speed, a Registry curve
override, target screen-width, an EPP (Enhance Pointer Precision) toggle,
and a list of `TargetAssociation`s (exe name / full path / window class /
window title) used to auto-match foreground windows to the profile. Nearly
every numeric/bool field is nullable — **null means "inherit from the
Default"**, and each field has its own `SyncMode` (`Auto` / `Manual` / `Skip`)
governing whether it's computed automatically, set explicitly, or skipped.

The **Default** profile is a sentinel (`IsDefault = true`) that acts as the
mathematical anchor/baseline every other profile's compensation is computed
against. `ProfileService.NormalizeDefault` enforces invariants on it (e.g.
it can never carry `Auto`-computed OS/Registry values) on every
load/save.

### The math engine (compensation pipeline) — precision-critical

This is the most fragile part of the codebase. High-level pipeline, end to end:

1. `ProfileService.GetEffectiveSensitivity(profile)` computes a single
   dimensionless "effective sensitivity" number for a profile: sensitivity
   multiplier × Windows Pointer Speed multiplier (`WinMultipliers[]`, a 20-entry
   table) × registry curve factor (round-tripped through
   `MouseRegistryBridge.EncodeRegCurve`, 16.16 fixed-point) ×
   screen-width compensation. (No DPI compensation factor — DPI only ever fed
   the actual registry write via a single system-wide value, never a
   per-profile one; see the "manual DPI/Scale override" note further down.)
   It's a one-line `.Full` over
   `GetEffectiveSensitivityFactors(profile)`, which computes and returns those
   four factors individually (`EffectiveSensitivityFactors` record struct) so a
   caller that needs a *partial* product — the Profile Editor's match-accuracy
   display shows "Raw Accel only" and "Raw Accel + Win Speed" rows before the
   final combined row — can reuse the exact same calculation instead of
   re-deriving it by hand. This split exists because that hand-rolled duplicate
   is exactly what caused a real bug earlier: an edge-case guard got added to
   the real formula but not the duplicate, and the display silently stopped
   predicting reality. There is now exactly one implementation of this formula;
   don't reintroduce a second one in the ViewModel.
2. `ProfileService.CalculateCompensations(activeProfile)` — when a profile's
   `OSSyncMode`/`RegSyncMode` is `Auto`, solves backwards from
   `Effective(Default)` to pick the closest discrete Windows Pointer Speed
   step, then the exact Registry curve value that makes the profile's effective
   sensitivity match the Default's.
3. `MouseService.ApplyProfileAsync` applies the result to hardware, in a fixed
   order that matters: Raw Accel template + sensitivity (`RawAccelService`,
   invokes Raw Accel's `writer.exe`) → Windows Pointer Speed via
   `SystemParametersInfo` → Registry curve bridge
   (`MouseRegistryBridge.ApplyRegistryBridge`, which reads the *live* pointer
   speed multiplier from the registry, so pointer speed must already be
   committed) → EPP registry toggle. It dedupes against the last-applied
   hardware state so redundant profile-switch events don't re-issue writes.
4. `MouseRegistryBridge` implements the MarkC no-acceleration curve fix: it
   writes `SmoothMouseXCurve`/`SmoothMouseYCurve` such that
   `CursorMovement = RawInput × regResult`, independent of DPI and Windows
   Pointer Speed. `EncodeRegCurve` (used for prediction/display) and
   `CurveToBinary`/`ApplyRegistryBridge` (used for the actual write) must stay
   formula-identical or the match-accuracy display stops predicting reality.

**Do not modify the following without an explicit, scoped instruction to do so:**

- **`Services/MouseRegistryBridge.cs`**
  - `QuantizeToFixedPoint1616(double)` — private helper, the **single**
    implementation of "round a value to its 16.16 fixed-point `long`, but never
    let a genuinely nonzero value round down to a literal `0`" (a zero Y curve
    stops the pointer dead when EPP is on). Both `CurveToBinary` (the real
    registry write, per curve node) and `EncodeRegCurve` (display prediction,
    on node `[1]`) call this same helper — there used to be two independently
    written copies of this clamp (one per function) and they were only
    coincidentally in sync; if you need to change the clamp behavior, change it
    here once, not in either caller.
  - `EncodeRegCurve()` — the 16.16 round-trip model:
    `yBase1 = MarkCXCurve[1] × (150/DPI) / winSpeedMult`. Changing the anchor,
    the DPI factor, or the `winSpeedMult` division silently breaks every match
    accuracy display and every compensation calculation. An additional
    `EncodeRegCurve(double, out double step)` overload exists (exposes the same
    internal quantization step for display-precision use) — same formula,
    don't let the two overloads drift apart. Runs `regResult` (unfloored — see
    below) through `QuantizeToFixedPoint1616` on node `[1]` and converts back
    to regResult units. Reads DPI and Windows Pointer Speed **live from the
    current system state**, not from the profile being displayed/edited — for
    a profile that isn't the one currently applied to Windows, this is
    technically the wrong environment for prediction, but in practice only
    matters when `regResult` is already within about a factor of 2 of `step`
    (~1e-5–1e-4), since otherwise `EncodeRegCurve(x) ≈ x` regardless of which
    live DPI/speed set the granularity. Not worth refactoring into an
    explicit-parameter signature unless that edge case turns out to matter for
    realistic profile values.
  - `ApplyRegistryBridge()` → `CurveToBinary()` — the actual registry write:
    `Y[i] = X[i] × (150/DPI) / winSpeedMult × regResult`. Must stay
    formula-identical to `EncodeRegCurve` or the model stops predicting
    reality — enforced by both calling the shared `QuantizeToFixedPoint1616`
    helper described above instead of duplicating the clamp logic.
    `winSpeedMult` is read live from the registry immediately before this call
    (pointer speed is written first so the live value is already correct for
    the profile). Despite the "16.16" name, each curve node is stored as a
    **signed 64-bit integer**, not a 32-bit container — real overflow ceiling
    is `long.MaxValue / 65536` per node (~1.4×10^14), not what "16.16" might
    suggest; `QuantizeToFixedPoint1616` throws `ArgumentOutOfRangeException`
    rather than silently wrapping via an unchecked `(long)` cast, and never
    lets a genuinely nonzero curve value round down to a literal `0` (a zero Y
    curve stops the pointer dead when EPP is on) — that guard alone is
    sufficient and precise (computed from the real fixed-point step, not a
    guess), so there is no separate `regResult`-level floor above it.
  - **There used to be a `Math.Max(regResult, 0.01)` floor here (and mirrored
    in `EncodeRegCurve`) — it has been removed, deliberately, not just left
    alone.** History, so it doesn't get silently reintroduced: it predates
    this app's `QuantizeToFixedPoint1616` guard (confirmed via
    `git log --all -S"0.01" -- Services/MouseRegistryBridge.cs`: introduced
    once, commit `f10c1fb`, comment "clamp regResult to a small positive
    floor," never tuned or revisited since). Once the precise per-node
    zero-avoidance clamp existed, the `0.01` floor became pure redundancy that
    actively hurt accuracy: it doesn't prevent anything the per-node clamp
    doesn't already prevent more precisely, but it did flatten every
    `RegistryCurveOverride` value between `0` and `0.01` to an *identical*
    applied result — confirmed live in-app before the fix (`0.001`, `0.01`,
    `0.0001` all produced the same output) and after (they now differ
    proportionally, e.g. `0.001` vs `0.01` differ by roughly the expected 10×).
    If you're ever asked to reintroduce a `regResult` floor "for safety,"
    the answer is: it's not needed — `QuantizeToFixedPoint1616` already
    guarantees no literal zero, exactly, for any nonzero input.
  - The Raw Accel sensitivity tooltip's old stated max (`123456789012345.12`,
    `~6e-6` step) has been **verified false** and removed. Checked directly
    against Raw Accel's own source
    (github.com/RawAccelOfficial/rawaccel, `grapher/Models/Options/Option.cs`):
    the Sensitivity Multiplier field is constructed as
    `new Field(box, containingForm, defaultData)` with no min/max arguments,
    which falls through to `Field.cs`'s own defaults —
    `minData = double.MinValue, maxData = double.MaxValue`. Raw Accel imposes
    **no domain-specific bound or precision floor at all**; it's a plain,
    unbounded `double` the same as everywhere else in this app. This matters
    beyond the tooltip: `ProfileEditorViewModel.RecalculateMatchAccuracy()`
    used to hardcode 4 decimal places for the Raw Accel-only / Win Speed-only
    rows (and the Full Match row when EPP is off) on the assumption that Raw
    Accel had a `~6e-6`-scale floor analogous to the registry curve's real
    16.16-quantization floor — it doesn't, so those rows now use the
    uncapped double-precision budget instead (see below). The registry
    curve's floor (`EncodeRegCurve`'s live-computed `step`) is the one
    genuinely real, hardware-verified precision floor in this app; don't
    assume any other field has an equivalent one without checking the same way.
- **`Services/ProfileService.cs`**
  - `WinMultipliers[]` — the 20-entry table is the single source of truth for
    all Win Speed math. A wrong value means wrong compensation applied to
    hardware.
  - `GetEffectiveSensitivityFactors(profile)` — the single implementation of
    the 4-factor effective-sensitivity formula (sens × winMult × regCurve ×
    screenComp), returned as an `EffectiveSensitivityFactors` record
    struct (`.RawOnly`, `.WinOnly`, `.Full` partial-product properties) instead
    of a single combined number. `GetEffectiveSensitivity()` is just `.Full`
    over this. Uses `EncodeRegCurve` for the `RegCurve` factor, feeding every
    comparison — removing that call is a silent precision regression.
    Used to be a 5th factor, `dpiComp` — removed along with the manual
    `TargetLogicalDpi` profile field it depended on. Verified against the
    MarkC fix's own cited source and this app's own registry-write code that
    DPI compensation only ever operated on a single system-wide value (never
    per-profile), so a per-profile `dpiComp` ratio was mathematically always
    `1.0` — dead weight, not a real compensation. Live DPI is still read
    correctly at apply-time (`MouseRegistryBridge.GetCurrentLogicalDpi`),
    this only removed the now-pointless manual override and its display-side
    comparison. The
    `regCurve` factor is computed in its own `try/catch` around
    `EncodeRegCurve` — an extreme `RegistryCurveOverride` can overflow 16.16
    fixed-point storage there (a real, intentional exception, not a bug — see
    `QuantizeToFixedPoint1616`), and on that overflow `regCurve` becomes
    `double.NaN` **instead of letting the exception propagate out of this
    method**. This was a real bug found live: `sens`/`winMult` are computed
    successfully earlier in this same method and have nothing to do with the
    registry curve, but before this fix, one overflowing factor took the
    entire struct down with it — Raw Accel Only and Win Speed Only (which
    only need `.RawOnly`/`.WinOnly`, never `.RegCurve`) went blank any time
    EPP Override alone was extreme. Now only `.Full` (which actually
    multiplies `RegCurve` in) becomes `NaN`; the other partial products stay
    real numbers. Don't go back to letting this method throw — every caller
    of `.RawOnly`/`.WinOnly` depends on it not doing so.
  - `sens`/`winIndex` (the `SensMultiplier`/`WinPointerSpeed` factors) follow
    the same three-tier priority as every other factor in this function: the
    profile's own value, then the Default's, then a hardcoded neutral
    (`1.0`× / WinPointerSpeed `10`) only if *both* are blank. They used to
    jump straight to the hardcoded neutral, skipping the Default-inheritance
    step screen width/registry curve already had. That contradicted this
    function's own doc comment ("fully-null profile yields 100% match by
    definition") — the promise only held when the Default itself
    happened to be neutral. A Default with a real, non-neutral
    `WinPointerSpeed` (e.g. `5`) made a completely blank profile show a
    266.67% match instead of 100%, because the code compared the Default's
    real 0.375× against an assumed-neutral 1.0×. Found live; confirmed via
    `git blame` to be original code from the very first commit that built
    this math engine, not a regression from any later change. The correct
    pattern already existed elsewhere in this codebase —
    `ProfileEditorViewModel.GetBaseSens()`/`GetBaseWinPointerSpeed()`, used by
    `ApplyAutoOSSync`/`ApplyAutoRegSync` — it had just never been reconciled
    with this function.
  - `CalculateCompensations()` — the apply-time math. Must stay in sync with
    `ProfileEditorViewModel`'s `ApplyAutoOSSync`/`ApplyAutoRegSync`. Its own
    `sTarget` (`activeProfile.SensMultiplier ?? ...`) had the identical
    jump-straight-to-neutral bug as `sens` above, fixed the same way
    (`?? baseProfile.SensMultiplier ?? 1.0`) — this is the one function
    explicitly required to stay in sync with the ViewModel's
    `GetBaseSens()`-based Auto-sync functions, so it needed the same fix even
    though no profile in live testing happened to trigger a visible mismatch
    from it yet (only reachable when `SensMultiplier` is blank *and*
    `OSSyncMode`/`RegSyncMode` is `Auto` on the same profile).
  - A follow-up line-by-line audit of every fallback in these two functions
    (prompted by the fixes above) found three more instances of the same
    "profile → base → hardcoded default" chain being incomplete:
    - **EPP** (`UseEnhancePointerPrecision`) had a *correct* profile→base
      chain but no explicit final tier — `profile.UseEnhancePointerPrecision
      ?? baseProfile?.UseEnhancePointerPrecision` left a totally-unspecified
      EPP as `null`, and every downstream `useEpp != false` check silently
      treated `null` as "on" (an artifact of C# nullable-bool comparison —
      `null != false` is `true` — not a deliberate default). Fixed via a new
      single-source-of-truth helper, `ResolveEpp(profile, baseProfile)`,
      which adds the missing `?? false` tier: false matches Windows' own
      out-of-the-box EPP default, and matches what `MouseService`'s real
      apply path already does when EPP is unspecified everywhere (it leaves
      the registry alone rather than writing anything — see
      `MouseService.ApplyProfileAsync` below). Used at both `useEpp`
      call sites in this file; `ProfileEditorViewModel` has its own parallel
      `GetEffectiveEpp()` for its four call sites (its own live-edit
      `EppOverride` property stands in for `profile.UseEnhancePointerPrecision`,
      same reasoning otherwise — see that file's Math Engine section below).
    - `rawRegValue` (the registry curve input) skipped the `baseProfile` tier
      entirely — `GetEffectiveRegistryCurveOverride(profile) ?? 1.0`, unlike
      the correct profile→base→default chain already used elsewhere in this
      function. Fixed to
      `GetEffectiveRegistryCurveOverride(profile) ??
      GetEffectiveRegistryCurveOverride(baseProfile) ?? 1.0`.
    - `CalculateCompensations`'s `currentWinMult` lookup (inside the
      `RegSyncMode == Auto` step) used `winSpeedResult ?? 10`, skipping
      `baseProfile.WinPointerSpeed` — reachable whenever `RegSyncMode` is
      `Auto` but `OSSyncMode` isn't, on a profile with a blank
      `WinPointerSpeed`. This one is explicitly a sync-with-the-ViewModel
      bug: `ProfileEditorViewModel.ApplyAutoRegSync` already did this
      correctly (`WinPointerSpeed ?? GetBaseWinPointerSpeed()`), so
      `CalculateCompensations` was the one side actually out of sync with
      what CLAUDE.md already required it to match.
  - `TryParsePositiveDouble`/`TryParsePositiveInt`/`CountMantissaDigits` —
    `public static`, deliberately living here and not in the ViewModel, so
    `LenientPositiveDoubleConverter`/`LenientPositiveIntConverter` (private
    nested `JsonConverter`s registered in `_jsonOptions.Converters`, next to
    `JsonStringEnumConverter`) can call the *exact same* validity check
    `ProfileEditorViewModel`'s typing handlers use. This is what makes
    `LoadAsync()` tolerant of a malformed `profiles.json` field instead of
    crashing the whole app on startup (see Persistence, above) — a value that
    fails this check (not a number, negative, zero, or more than 15
    significant digits) is read as `null`, identical to a blank field, at
    both the typing layer and the file-loading layer. If this ever needs to
    change, it changes once, here — don't let a second, separately-maintained
    copy reappear in the ViewModel (already happened once tonight with a
    load-time-only magnitude check; deleted, not kept as a second mechanism).
- **`ViewModels/ProfileEditorViewModel.cs`** (Math Engine section, ~lines 305–440)
  - `GetBaseSens()`/`GetBaseWinPointerSpeed()`/`GetBaseRegCurve()`/
    `GetEffectiveEpp()` — this file's own base-inheritance resolvers, parallel
    to `ProfileService`'s `GetEffectiveSensitivityFactors` doing the same
    resolution for a stored `MouseProfile` (these read the ViewModel's own
    live-edit properties instead). `GetEffectiveEpp()` mirrors
    `ProfileService.ResolveEpp` exactly (profile's own value → base's →
    `false`) — see that function's doc comment for why `false` is the
    correct last resort. Used by `ApplyAutoRawSync`/`ApplyAutoOSSync`/
    `ApplyAutoRegSync`/`RecalculateMatchAccuracy` instead of each hand-typing
    `EppOverride ?? bp?.UseEnhancePointerPrecision` (which is what all four
    did until the fallback-chain audit below — none of them had the final
    `?? false` tier either, same bug as `ProfileService`, found at the same
    time).
  - `ApplyAutoOSSync()` — Win Speed selection formula: target is
    `Effective(base) / S_target`.
  - `ApplyAutoRegSync()` — Registry remainder formula: target is
    `Effective(base) / (S_target × WinMult)`. `RegistryCurveOverride` itself is
    stored at **full double precision** (the raw computed `remainder`,
    intentionally unrounded) so it can be correctly re-quantized fresh if
    DPI/pointer speed ever change later — only `RegistryCurveString` (the
    textbox display) is capped, via `FormatRegistryCurveValue()`. That used to
    be a flat `.ToString("G6")` — an arbitrary, unjustified digit count in the
    same category as the flat `G10`/`15`-digit guesses this file already
    warns against elsewhere. Replaced with a cap derived from the real,
    live registry-curve quantization step (`EncodeRegCurve`'s own `step`,
    converted to a decimal-place count via `Math.Ceiling(-Math.Log10(step))`).
    Found live: the old `G6` was showing one digit beyond what the real step
    could distinguish (e.g. `0.998785` vs `0.99878` — differ by `5e-6`,
    smaller than the ~`2.6e-5` step — encoded identically); the fix trims
    exactly that fake digit. `FormatRegistryCurveValue()` itself just derives
    that step-based cap and hands off to `FormatFixedPointInput(value,
    domainDecimalPlaces)` — the shared formatter (below) that actually builds
    the display string, combining this domain-specific cap with the value's
    own round-trip precision (`GetRealSignificantDigits()`).
  - `FormatFixedPointInput(value, domainDecimalPlaces)` — shared display
    formatter for every raw-value textbox (`SensitivityString`,
    `RegistryCurveString`), called from `LoadProfile`, `OnSensitivityChanged`,
    `OnRegistryCurveOverrideChanged`, `ApplyAutoRawSync`, and
    `FormatRegistryCurveValue`. Always renders fixed-point, **never**
    scientific "E" notation — that was the actual original bug: typing
    `0.00001` and switching profiles away and back used to silently show
    `1E-05` back (a plain `ToString(CultureInfo.InvariantCulture)` call
    reformatting what was typed, not just displaying it). Shows exactly this
    value's own real round-trip precision (`GetRealSignificantDigits`),
    further capped by a genuine domain-specific floor when one exists (the
    registry curve's live step, via `FormatRegistryCurveValue`); pass
    `int.MaxValue` for `domainDecimalPlaces` when no such floor applies
    (Sensitivity has none of its own — see the Raw Accel research note above).
    The `50`-decimal-place cap inside this function is a UI sanity bound
    against a pathologically tiny value producing a multi-hundred-character
    string — unrelated to, and not a substitute for, the ~15-digit
    round-trip-safety budget used elsewhere in this file
    (`TryParsePositiveDouble` in `ProfileService.cs`, `FormatMatchPercent`
    below); don't conflate the two if this function is touched again.
    A value this large or precise should already have been rejected earlier
    (typing) or loaded as blank (profiles.json) — see "Invalid GUI input" and
    Persistence, above — so 50 is deliberately generous, not a precision
    guarantee.
  - `RecalculateMatchAccuracy()` — display math. Calls
    `_profileService.GetEffectiveSensitivityFactors(_editingProfile)` and reads
    off whichever partial product each row needs (`.RawOnly`, `.WinOnly`,
    `.Full`) instead of re-deriving the formula by hand — safe because
    `AutoPersist()` already writes every current field onto `_editingProfile`
    before this runs (see the ordering rule below), so the real math-engine
    function sees the same values the ViewModel does. Formats each of the
    three match-% rows via `FormatMatchPercent`, falling back to scientific
    notation at extreme magnitudes — not a fixed `"G10"`/`"F2"`-style format
    string. Only the registry curve (Full Match row, EPP on) has a real
    domain-specific precision floor — computed by nudging the live
    `EncodeRegCurve` step and measuring the actual percentage-point delta, not
    a hardcoded digit count. Raw Accel-only, Win Speed-only, and Full Match
    with EPP off pass `int.MaxValue` as the domain floor (i.e. no domain cap —
    see the Raw Accel research note above for why); in that case the value's
    own real precision (see next paragraph) is the only cap. Don't revert to a
    flat format, and don't reintroduce a fixed digit count for Raw Accel,
    without re-deriving why (see git history for the reasoning).
  - `FormatMatchPercent` (local function inside `RecalculateMatchAccuracy`) —
    does **not** use a fixed significant-digit constant (an earlier version
    used `15`, then inconsistently `16` in one branch — both were guesses, and
    the inconsistency was a real bug caught live). Instead it computes, per
    value, the exact number of significant decimal digits that specific
    `double` needs to round-trip back to itself bit-for-bit: try formatting at
    `G1`, `G2`, ... and reparsing until one matches exactly (else `17`, the
    documented maximum any double ever needs). This is the same notion .NET's
    own default double formatting uses internally, made explicit so both the
    fixed-point and scientific-notation paths share one consistent,
    per-value-correct cap instead of two different fixed guesses. A domain
    floor (registry curve, when real) is combined via `Math.Min` on top of
    this, never replacing it. `trueIntegerDigits` (the value's order-of-magnitude
    digit count) is deliberately **unclamped** — can be ≤ 0 for values under 1
    (e.g. `0.05` has `trueIntegerDigits = -1`) — computed via the shared
    `GetTrueIntegerDigitCount(absValue)` helper (`Math.Floor(Math.Log10(absValue))
    + 1`), also used by `FormatFixedPointInput` above — factored out after this
    exact formula was found hand-typed in both places, one written months apart
    from the other.
    Three real bugs were found and fixed building this, in order: (1) clamping
    `trueIntegerDigits` to "always at least 1" miscomputed decimal-place
    placement for any value under 1 (e.g. treating `0.05` as if its first
    significant digit were in the tenths place instead of the hundredths
    place); (2) the no-domain-floor path had no "too small" fallback at all,
    so a genuinely tiny but nonzero percentage (e.g. `Sensitivity = 0.0000001`)
    rounded straight to a false `"0%"`, silently hiding a real value; (3) —
    found *after* (1) and (2) shipped, by the user hitting it in completely
    ordinary use, not an extreme test value — `realSigFigs` (how many digits
    THIS value needs for round-trip) can be **smaller** than
    `trueIntegerDigits` (how many integer-part digit positions it has)
    whenever the value has trailing zeros in its integer part — e.g. `150.0`
    round-trips at just 2 significant digits (`"1.5E+02"`) despite having 3
    integer digit positions. `decimalPlaces = realSigFigs - trueIntegerDigits`
    went **negative** in that case, and the code was treating "negative"
    as "too large for fixed-point" — incorrectly routing an ordinary value
    like a `150%` match into scientific notation (`"1.5E+002%"`). Fixed by
    separating the two concerns that bug conflated: `decimalPlaces` itself is
    now clamped at a minimum of `0` (a value like `150.0` just needs 0
    decimals — that's not the same thing as "too large"), and "too large for
    fixed-point" is judged independently, against a fixed ceiling
    (`trueIntegerDigits > 15`) — the same fixed budget used everywhere else in
    this app, not derived from `realSigFigs`. If you touch this function
    again: `decimalPlaces` (how many decimals to show) and "is this value too
    large for fixed-point at all" are two different questions — don't let a
    per-value round-trip quantity (`realSigFigs`) answer the second one.
    Verified live at every extreme tried: huge Sensitivity
    (`132123123112.312312...%`, `1.2345678901234567E+19%`), tiny Sensitivity
    (`9.999999999999999E-006%`, not `"0%"`), ordinary round values (`150%`,
    not `"1.5E+002%"`), and mid-range values across both branches. Uses `'#'`
    (optional-digit) placeholders, not `'0'` (required-digit) ones, in its
    fixed-point format strings — `'0'` pads
    with trailing zeros out to the cap even when they're not real digits
    (a clean typed `1.12312312` displayed as `"112.312312000000%"`), which is
    a milder version of the exact fake-precision problem this whole rework
    was meant to eliminate. `RecalculateMatchAccuracy()` itself wraps its
    `GetEffectiveSensitivity(bp)`/`GetEffectiveSensitivityFactors(_editingProfile)`
    calls in a `try/catch (ArgumentOutOfRangeException)` as a last-resort
    safety net (falls back to literal `"Overflow%"` on all three rows) — this
    should rarely fire now that `GetEffectiveSensitivityFactors` itself
    doesn't throw for the common case (see above), but an unhandled exception
    here previously crashed the entire app just from typing an extreme value
    into a field, before ever pressing Apply. Keep this catch even if it
    looks redundant; the failure mode it prevents is a hard crash, not a bad
    display. `EncodeRegCurve` has a **second** direct call site further down
    the same method — the regStep nudge used only to derive the Full Match
    row's decimal precision — that is *not* covered by the try/catch above
    (it runs after that block) and crashed the app live even after the first
    fix shipped. Guarded separately: skipped entirely when `fullMatchPercent`
    is already non-finite (the nudge is moot then — it'll display
    `"NaN%"`/`"Infinity%"` regardless of decimal count) plus its own
    defensive `try/catch`. If `EncodeRegCurve` ever grows a third call site,
    it needs the same treatment — grep for `EncodeRegCurve(` to check
    instead of assuming these two are the only ones.
  - `On*Changed` callback **ordering** matters: `AutoPersist()` must come
    before `RecalculateMatchAccuracy()` everywhere. Swapping them causes stale
    model reads.
  - The `_isSyncing` guard + `wasSyncing` save/restore pattern in
    `ApplyAutoOSSync`/`ApplyAutoRegSync`. Removing any guard causes reentrancy
    corruption on profile load.
- **`Services/MouseService.cs`** — the dedup check in `ApplyProfileAsync`
  compares computed `(winSpeed, regResult, template, sens, epp)` values, not
  object identity. Reverting to `ReferenceEquals` means Default changes
  won't propagate to already-active profiles.
- **`Models/MouseProfile.cs`** — field types for `SensMultiplier` (`double?`),
  `WinPointerSpeed` (`int?`), `RegistryCurveOverride` (`double?`). If these
  change type, the null-fallback logic throughout the math engine silently
  misbehaves.

### Foreground-window profile switching

`ProcessWatcher` (`Services/ProcessWatcher.cs`) is a Win32 `SetWinEventHook`
listener (`EVENT_SYSTEM_FOREGROUND` + `EVENT_OBJECT_DESTROY`) that maintains
its own match index (by exe name, full path, window class — dictionary
lookups; window title — substring scan) built from `TargetAssociation`s across
all profiles with `AutoSwitchEnabled`. On foreground change it applies the
matching profile; on window destroy it implements "Last Man Standing": if no
other visible window still matches the currently-active profile, it reverts to
the Default. This revert is suppressed during system logoff/shutdown
(`NotifySessionEnding`, wired to `desktop.ShutdownRequested`) so quitting apps
during shutdown doesn't spuriously revert registry state. The index is rebuilt
whenever the `Profiles` collection changes or a profile's
`AutoSwitchEnabled`/`TargetAssociations` changes.

### Persistence

- `ProfileService` persists `Profiles` to `profiles.json` next to the exe
  (`AppContext.BaseDirectory`), serialized with a `SemaphoreSlim` write lock to
  prevent concurrent-save truncation races.
- **Loading tolerates a malformed numeric field instead of crashing the app.**
  `LoadAsync()` used to deserialize the whole file with plain
  `JsonSerializer.Deserialize<PersistedData>` — if any single field had the
  wrong JSON shape (e.g. a string where `SensMultiplier` expects a number),
  deserialization threw and nothing caught it (`App.axaml.cs` calls
  `LoadAsync().GetAwaiter().GetResult()` directly, before any window exists),
  so one bad value in the file crashed the entire app on startup. Fixed with
  two private `JsonConverter`s registered in `_jsonOptions.Converters`
  (`LenientPositiveDoubleConverter`/`LenientPositiveIntConverter`, next to the
  `JsonStringEnumConverter` already there): for `double?`/`int?` fields, read
  the raw JSON number and run it through `TryParsePositiveDouble`/
  `TryParsePositiveInt` (the same shared check typed GUI input uses — see
  "Invalid GUI input" above); if it doesn't pass — not a number at all,
  negative, zero, or too many digits — the field loads as `null`, identical to
  a blank field, instead of throwing. Every flavor of "invalid" is treated the
  *same* way on purpose: an earlier version tried to keep a bad-but-numeric
  value visible on load (showing the mangled number with a warning icon,
  distinct from a non-numeric value which would just be dropped) and that was
  deliberately simplified away — two different behaviors for "invalid"
  depending on *how* it's invalid was worse than one consistent rule. The rest
  of a profile (name, target associations, every other field) is unaffected
  by one bad field — nothing about loading drops a whole profile.
- `SettingsService` persists `AppSettings` (Raw Accel directory, templates
  directory, startup/tray behavior).
- `TemplateService` manages the library of Raw Accel `.json` curve templates
  (import/rename/delete), stored under `AppSettings.TemplatesDirectory`
  (defaults to the portable app folder).
- Everything is portable/per-user under `HKCU` and the app's own folder — no
  admin elevation is required for normal operation (`writer.exe` and all
  registry writes are HKCU-scoped).

## Working style notes

- **Avalonia styling: define shared `Style Selector="X.class-name"` rules in
  `App.axaml` for anything repeated across multiple elements — font weight,
  spacing, sizing — instead of hardcoding the same values inline on each
  one.** Repeated inline values drift out of sync the moment one gets tuned
  and the others don't. Happened for real in `SettingsWindow.axaml`: each
  section header independently hardcoded `FontWeight="SemiBold"
  Margin="0,4,0,0"`, and the toggle-vs-folder-picker rows each hardcoded
  their own spacing — the exact mistake `Button.dialog-action` was already
  introduced earlier to avoid, just not carried through consistently. A
  genuinely one-off value on a single element is fine; the tell that
  something belongs in a shared style is the same value getting
  copy-pasted onto more than one element.
- No code-behind logic in Views — implement behavior in ViewModels and Commands.
- Nullable fields on `MouseProfile` follow the fallback pattern
  `profile.Field ?? globalDefault.Field ?? hardcodedFallback`.
- Never touch the math-engine functions listed above without an explicit,
  scoped instruction to do so.
- **Invalid GUI input (`Sensitivity`/`RegistryCurveOverride`/`TargetScreenWidth`)
  is flagged, never silently discarded or auto-corrected.**
  `TryParsePositiveDouble`/`TryParsePositiveInt` — positive, and for the double
  variant no more than 15 significant digits (the real budget a double can hold
  without silently losing precision, IEEE 754, not a guess) — live as `public
  static` methods on **`ProfileService`**, not the ViewModel. Deliberately
  placed there: `ProfileService`'s own JSON loading (see Persistence, below)
  needs the exact same check, so "is this value valid" has one implementation
  used by both typing and loading, instead of two independently-maintained
  copies that drifted apart once already (an earlier version had a second,
  looser magnitude-only check for loaded values — replaced, not kept
  alongside). `ProfileEditorViewModel`'s `On*StringChanged` handlers call
  `ProfileService.TryParsePositiveDouble`/`TryParsePositiveInt` and set a
  matching `IsXxxInputValid` observable bool from the result; the View overlays
  a small red ⚠ `TextBlock` (`Classes="input-error-icon"`) on the field when
  it's `false`. The underlying property is never written for a rejected value
  (Apply always uses the last valid value, never whatever's sitting unconfirmed
  in a textbox) — the flag only makes that already-safe behavior visible
  instead of silent. Unconditionally reset to `true` in `LoadProfile` — loading
  itself now guarantees a stored value is always either blank or already-valid
  (see Persistence), so there's nothing left to check by the time a profile
  reaches the editor.
  **Avalonia gotcha found live, twice**: a plain `Style Selector="TextBox.input-error"`
  setting `BorderBrush` was silently overridden by the theme's own `:focus`
  styling while a TextBox was actually focused (red border only appeared after
  clicking away — exactly when the flag stops being useful), and separately by
  the theme's `:pointerover` styling on the two editable ComboBoxes (border
  vanished on hover). An explicit `:focus`-scoped selector variant of the same
  style did **not** fix the first case either — the theme's state styling wins
  at the control template's internal level, not just via selector specificity,
  so don't assume a same-specificity pseudo-class override will beat it; verify
  live. Ended up **removing the border approach entirely** (a cue that's only
  sometimes visible reads as broken, which is worse than no cue) in favor of a
  separate overlaid `TextBlock.input-error-icon` glyph — not part of either
  control's template, so no theme-internal state styling can hide it, and it
  behaves identically on both TextBox and ComboBox. The editable Screen Width
  ComboBox needs a larger right `Margin` on its icon instance (`24` vs `6`)
  than the plain TextBoxes, set per-instance rather than in the shared style,
  so the icon clears the ComboBox's own built-in dropdown arrow instead of
  overlapping it. (There used to be a second editable ComboBox here, Scale —
  removed along with the rest of the manual DPI/Scale override; see the
  `GetEffectiveSensitivityFactors` note above.)
