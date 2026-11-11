# Math engine reference

Reference detail for the compensation pipeline — formulas, precision edge cases, and the
bug history behind each protected function.

**`CLAUDE.md` holds the rules; this file holds the reasons.** The short version lives there:
a list of functions not to modify without an explicit, scoped instruction, one line each.
Read the matching section here before touching any of them. Every entry below exists because
something broke, was found live in the running app, and cost real time to diagnose — the
history is the point, not decoration.

If you are adding to this file: new findings go under the function they belong to, not at the
end.

---

## The pipeline, end to end

1. **`ProfileService.GetEffectiveSensitivity(profile)`** computes a single dimensionless
   "effective sensitivity" number for a profile: sensitivity multiplier × Windows Pointer
   Speed multiplier (`WinMultipliers[]`, a 20-entry table) × registry curve factor
   (round-tripped through `MouseRegistryBridge.EncodeRegCurve`, 16.16 fixed-point) ×
   screen-width compensation.

   There is no DPI compensation factor. DPI only ever fed the actual registry write via a
   single system-wide value, never a per-profile one — see
   [GetEffectiveSensitivityFactors](#geteffectivesensitivityfactorsprofile).

   It is a one-line `.Full` over `GetEffectiveSensitivityFactors(profile)`, which returns
   those four factors individually so a caller needing a *partial* product can reuse the
   exact same calculation instead of re-deriving it.

2. **`ProfileService.CalculateCompensations(activeProfile)`** — when a profile's
   `OSSyncMode`/`RegSyncMode` is `Auto`, solves backwards from `Effective(Default)` to pick
   the closest discrete Windows Pointer Speed step, then the exact Registry curve value that
   makes the profile's effective sensitivity match the Default's.

3. **`MouseService.ApplyProfileAsync`** applies the result to hardware, in a fixed order
   that matters:

   Raw Accel settings file + sensitivity (`RawAccelService`, invokes Raw Accel's
   `writer.exe`) → Windows Pointer Speed via `SystemParametersInfo` → Registry curve bridge
   (`MouseRegistryBridge.ApplyRegistryBridge`, which reads the *live* pointer speed
   multiplier from the registry, so pointer speed must already be committed) → EPP registry
   toggle.

   It dedupes against the last-applied hardware state so redundant profile-switch events
   don't re-issue writes.

4. **`MouseRegistryBridge`** implements the MarkC no-acceleration curve fix: it writes
   `SmoothMouseXCurve`/`SmoothMouseYCurve` such that
   `CursorMovement = RawInput × regResult`, independent of DPI and Windows Pointer Speed.

   `EncodeRegCurve` (prediction/display) and `CurveToBinary`/`ApplyRegistryBridge` (the
   actual write) must stay formula-identical, or the match-accuracy display stops predicting
   reality.

---

# `Services/MouseRegistryBridge.cs`

## `QuantizeToFixedPoint1616(double)`

Private helper. The **single** implementation of "round a value to its 16.16 fixed-point
`long`, but never let a genuinely nonzero value round down to a literal `0`" — a zero Y curve
stops the pointer dead when EPP is on.

Both `CurveToBinary` (the real registry write, per curve node) and `EncodeRegCurve` (display
prediction, on node `[1]`) call this same helper.

**Why it is shared:** there used to be two independently written copies of this clamp, one
per function, and they were only coincidentally in sync. If the clamp behaviour needs to
change, change it here once, not in either caller.

Despite the "16.16" name, each curve node is stored as a **signed 64-bit integer**, not a
32-bit container — the real overflow ceiling is `long.MaxValue / 65536` per node
(~1.4×10¹⁴), not what "16.16" might suggest. The function throws
`ArgumentOutOfRangeException` rather than silently wrapping via an unchecked `(long)` cast.

## `EncodeRegCurve()`

The 16.16 round-trip model:

    yBase1 = MarkCXCurve[1] × (150/DPI) / winSpeedMult

Changing the anchor, the DPI factor, or the `winSpeedMult` division silently breaks every
match-accuracy display and every compensation calculation.

An additional `EncodeRegCurve(double, out double step)` overload exposes the same internal
quantization step for display-precision use. Same formula — don't let the two overloads
drift apart.

Runs `regResult` (unfloored — see [the removed floor](#removed-the-mathmaxregresult-001-floor))
through `QuantizeToFixedPoint1616` on node `[1]` and converts back to regResult units.

**Known limitation, deliberately not fixed.** Reads DPI and Windows Pointer Speed **live
from the current system state**, not from the profile being displayed or edited. For a
profile that isn't the one currently applied to Windows, that is technically the wrong
environment for prediction — but in practice it only matters when `regResult` is already
within about a factor of 2 of `step` (~1e-5–1e-4), since otherwise
`EncodeRegCurve(x) ≈ x` regardless of which live DPI/speed set the granularity. Not worth
refactoring into an explicit-parameter signature unless that edge case turns out to matter
for realistic profile values.

## `ApplyRegistryBridge()` → `CurveToBinary()`

The actual registry write:

    Y[i] = X[i] × (150/DPI) / winSpeedMult × regResult

Must stay formula-identical to `EncodeRegCurve` or the model stops predicting reality —
enforced by both calling the shared `QuantizeToFixedPoint1616` helper rather than
duplicating the clamp logic.

`winSpeedMult` is read live from the registry immediately before this call. Pointer speed is
written first, so the live value is already correct for the profile.

The per-node clamp in `QuantizeToFixedPoint1616` is sufficient and precise — computed from
the real fixed-point step, not a guess — so there is no separate `regResult`-level floor
above it.

## Removed: the `Math.Max(regResult, 0.01)` floor

**This has been removed deliberately, not merely left alone.** Recorded here so it doesn't
get silently reintroduced.

It predated this app's `QuantizeToFixedPoint1616` guard. Confirmed via
`git log --all -S"0.01" -- Services/MouseRegistryBridge.cs`: introduced once, commit
`f10c1fb`, comment "clamp regResult to a small positive floor," never tuned or revisited.

Once the precise per-node zero-avoidance clamp existed, the `0.01` floor became pure
redundancy that actively hurt accuracy. It prevented nothing the per-node clamp doesn't
already prevent more precisely, but it did flatten every `RegistryCurveOverride` value
between `0` and `0.01` to an *identical* applied result.

Confirmed live in-app before the fix — `0.001`, `0.01` and `0.0001` all produced the same
output — and after, where they differ proportionally (`0.001` vs `0.01` differ by roughly
the expected 10×).

**If you are ever asked to reintroduce a `regResult` floor "for safety," the answer is that
it isn't needed:** `QuantizeToFixedPoint1616` already guarantees no literal zero, exactly,
for any nonzero input.

## Research note: Raw Accel imposes no precision floor

The Raw Accel sensitivity tooltip's old stated max (`123456789012345.12`, `~6e-6` step) has
been **verified false** and removed.

Checked directly against Raw Accel's own source
(github.com/RawAccelOfficial/rawaccel, `grapher/Models/Options/Option.cs`): the Sensitivity
Multiplier field is constructed as `new Field(box, containingForm, defaultData)` with no
min/max arguments, which falls through to `Field.cs`'s own defaults —
`minData = double.MinValue, maxData = double.MaxValue`.

Raw Accel imposes **no domain-specific bound or precision floor at all**. It is a plain,
unbounded `double`, the same as everywhere else in this app.

**This matters beyond the tooltip.** `RecalculateMatchAccuracy()` used to hardcode 4 decimal
places for the Raw Accel-only and Win Speed-only rows (and the Full Match row when EPP is
off) on the assumption that Raw Accel had a `~6e-6`-scale floor analogous to the registry
curve's real 16.16-quantization floor. It doesn't, so those rows now use the uncapped
double-precision budget instead.

The registry curve's floor (`EncodeRegCurve`'s live-computed `step`) is the one genuinely
real, hardware-verified precision floor in this app. **Don't assume any other field has an
equivalent one without checking the same way.**

---

# `Services/ProfileService.cs`

## `WinMultipliers[]`

The 20-entry table is the single source of truth for all Win Speed math. A wrong value means
wrong compensation applied to hardware.

## `GetEffectiveSensitivityFactors(profile)`

The single implementation of the 4-factor effective-sensitivity formula
(sens × winMult × regCurve × screenComp), returned as an `EffectiveSensitivityFactors`
record struct with `.RawOnly`, `.WinOnly` and `.Full` partial-product properties instead of
a single combined number. `GetEffectiveSensitivity()` is just `.Full` over this.

Uses `EncodeRegCurve` for the `RegCurve` factor, feeding every comparison — removing that
call is a silent precision regression.

**Why the struct exists.** The Profile Editor's match-accuracy display shows "Raw Accel
only" and "Raw Accel + Win Speed" rows before the final combined row. A hand-rolled
duplicate of this formula is exactly what caused a real bug earlier: an edge-case guard got
added to the real formula but not the duplicate, and the display silently stopped predicting
reality. There is now exactly one implementation — don't reintroduce a second one in the
ViewModel.

### The removed 5th factor, `dpiComp`

Removed along with the manual `TargetLogicalDpi` profile field it depended on.

Verified against the MarkC fix's own cited source and this app's own registry-write code
that DPI compensation only ever operated on a single system-wide value, never per-profile —
so a per-profile `dpiComp` ratio was mathematically always `1.0`. Dead weight, not a real
compensation.

Live DPI is still read correctly at apply time
(`MouseRegistryBridge.GetCurrentLogicalDpi`); this only removed the pointless manual
override and its display-side comparison.

### This method must not throw

The `regCurve` factor is computed in its own `try/catch` around `EncodeRegCurve`. An extreme
`RegistryCurveOverride` can overflow 16.16 fixed-point storage there — a real, intentional
exception, not a bug, see `QuantizeToFixedPoint1616` — and on that overflow `regCurve`
becomes `double.NaN` **instead of letting the exception propagate out of this method**.

This was a real bug found live. `sens`/`winMult` are computed successfully earlier in the
same method and have nothing to do with the registry curve, but before the fix one
overflowing factor took the entire struct down with it: Raw Accel Only and Win Speed Only
(which only need `.RawOnly`/`.WinOnly`, never `.RegCurve`) went blank any time EPP Override
alone was extreme.

Now only `.Full` — which actually multiplies `RegCurve` in — becomes `NaN`; the other
partial products stay real numbers. **Don't go back to letting this method throw.** Every
caller of `.RawOnly`/`.WinOnly` depends on it not doing so.

### The `sens`/`winIndex` fallback chain

These follow the same three-tier priority as every other factor in this function: the
profile's own value, then the Default's, then a hardcoded neutral (`1.0`× /
WinPointerSpeed `10`) only if *both* are blank.

They used to jump straight to the hardcoded neutral, skipping the Default-inheritance step
that screen width and registry curve already had. That contradicted this function's own doc
comment ("fully-null profile yields 100% match by definition") — the promise only held when
the Default itself happened to be neutral.

A Default with a real, non-neutral `WinPointerSpeed` (e.g. `5`) made a completely blank
profile show a **266.67%** match instead of 100%, because the code compared the Default's
real 0.375× against an assumed-neutral 1.0×.

Found live. Confirmed via `git blame` to be original code from the very first commit that
built this math engine, not a regression from a later change. The correct pattern already
existed elsewhere in this codebase — `ProfileEditorViewModel.GetBaseSens()` /
`GetBaseWinPointerSpeed()`, used by `ApplyAutoOSSync`/`ApplyAutoRegSync` — it had just never
been reconciled with this function.

## `CalculateCompensations()`

The apply-time math. **Must stay in sync with** `ProfileEditorViewModel`'s
`ApplyAutoOSSync`/`ApplyAutoRegSync`.

Its own `sTarget` (`activeProfile.SensMultiplier ?? ...`) had the identical
jump-straight-to-neutral bug as `sens` above, fixed the same way
(`?? baseProfile.SensMultiplier ?? 1.0`). This is the one function explicitly required to
stay in sync with the ViewModel's `GetBaseSens()`-based Auto-sync functions, so it needed
the same fix even though no profile in live testing happened to trigger a visible mismatch
from it — only reachable when `SensMultiplier` is blank *and* `OSSyncMode`/`RegSyncMode` is
`Auto` on the same profile.

## The fallback-chain audit

A follow-up line-by-line audit of every fallback in the two functions above, prompted by
those fixes, found three more instances of the same "profile → base → hardcoded default"
chain being incomplete.

### EPP had no final tier

`UseEnhancePointerPrecision` had a *correct* profile→base chain but no explicit final tier.
`profile.UseEnhancePointerPrecision ?? baseProfile?.UseEnhancePointerPrecision` left a
totally-unspecified EPP as `null`, and every downstream `useEpp != false` check silently
treated `null` as "on" — an artifact of C# nullable-bool comparison (`null != false` is
`true`), not a deliberate default.

Fixed via a single-source-of-truth helper, `ResolveEpp(profile, baseProfile)`, which adds
the missing `?? false` tier. `false` matches Windows' own out-of-the-box EPP default, and
matches what `MouseService`'s real apply path already does when EPP is unspecified
everywhere (it leaves the registry alone rather than writing anything).

Used at both `useEpp` call sites in this file. `ProfileEditorViewModel` has its own parallel
`GetEffectiveEpp()` for its four call sites.

### `rawRegValue` skipped the base tier

`GetEffectiveRegistryCurveOverride(profile) ?? 1.0` — unlike the correct
profile→base→default chain already used elsewhere in the same function. Fixed to:

    GetEffectiveRegistryCurveOverride(profile)
        ?? GetEffectiveRegistryCurveOverride(baseProfile)
        ?? 1.0

### `currentWinMult` skipped the base tier

`CalculateCompensations`'s `currentWinMult` lookup, inside the `RegSyncMode == Auto` step,
used `winSpeedResult ?? 10`, skipping `baseProfile.WinPointerSpeed`. Reachable whenever
`RegSyncMode` is `Auto` but `OSSyncMode` isn't, on a profile with a blank `WinPointerSpeed`.

This one is explicitly a sync-with-the-ViewModel bug: `ApplyAutoRegSync` already did it
correctly (`WinPointerSpeed ?? GetBaseWinPointerSpeed()`), so `CalculateCompensations` was
the side actually out of sync with what the docs already required it to match.

## `TryParsePositiveDouble` / `TryParsePositiveInt` / `CountMantissaDigits`

`public static`, deliberately living here and not in the ViewModel, so
`LenientPositiveDoubleConverter`/`LenientPositiveIntConverter` — private nested
`JsonConverter`s registered in `_jsonOptions.Converters`, next to `JsonStringEnumConverter`
— can call the *exact same* validity check `ProfileEditorViewModel`'s typing handlers use.

This is what makes `LoadAsync()` tolerant of a malformed `profiles.json` field instead of
crashing the whole app on startup. A value that fails this check — not a number, negative,
zero, or more than 15 significant digits — is read as `null`, identical to a blank field, at
both the typing layer and the file-loading layer.

If this ever needs to change, it changes once, here. **Don't let a second,
separately-maintained copy reappear in the ViewModel** — that already happened once, with a
load-time-only magnitude check, since deleted rather than kept as a second mechanism.

---

# `ViewModels/ProfileEditorViewModel.cs`

## Base-inheritance resolvers

`GetBaseSens()` / `GetBaseWinPointerSpeed()` / `GetBaseRegCurve()` / `GetEffectiveEpp()` /
`GetBaseYXRatio()` — this file's own base-inheritance resolvers, parallel to
`ProfileService`'s `GetEffectiveSensitivityFactors` doing the same resolution for a stored
`MouseProfile`. These read the ViewModel's own live-edit properties instead.

`GetEffectiveEpp()` mirrors `ProfileService.ResolveEpp` exactly (profile's own value →
base's → `false`).

Used by `ApplyAutoRawSync`/`ApplyAutoOSSync`/`ApplyAutoRegSync`/`RecalculateMatchAccuracy`
instead of each hand-typing `EppOverride ?? bp?.UseEnhancePointerPrecision` — which is what
all four did until the fallback-chain audit above. None of them had the final `?? false`
tier either, the same bug as `ProfileService`, found at the same time.

### `GetBaseYXRatio()`

The Default's own `YXRatioOverride`, fallback `1.0` (neutral). This is the fix for a real
bug found live in the already-committed Y/X Ratio Auto-sync feature.

`ApplyAutoYXRatioSync`/`ComputeIdealYXRatio` originally computed the Auto value as pure
`nativeAspect / profileAspect`, never multiplying by the Default's own ratio at all — the
same "jumped straight to a hardcoded neutral instead of falling back through the Default"
bug class as `sens`/`winIndex`/EPP/`regCurve` above, just freshly introduced in a newer
field rather than inherited from old code.

If the Default's own `YXRatioOverride` is ever non-neutral — someone typed a manual stretch
there for reasons unrelated to aspect-ratio compensation — every other profile's
Auto-computed correction must be `GetBaseYXRatio() × (nativeAspect / profileAspect)`, not
the aspect factor alone, or it silently discards that baseline instead of compensating
relative to it.

`ComputeIdealYXRatio()` is the one shared implementation of this formula, used by both
`ApplyAutoYXRatioSync` (which writes it into `YXRatioOverride` when `YXRatioSyncMode` is
`Auto`) and `YXRatioMatch`, so they can't drift into two different formulas.

**Verified live** via a controlled `profiles.json` edit — Default `YXRatioOverride = 1.05`,
one profile Auto-synced at a real aspect mismatch, one left Skip at a different real aspect
mismatch. The Auto profile's match stayed exactly `1` (proving Auto still hits the true
target even with a non-neutral Default), and the Skip profile's match came out to the
hand-derived `0.703125` (`1.05 ÷ (1.05 × 1.4222...)`), not `1` and not silently wrong.

## `YXRatioMatch`

A **public** computed property — not a resolver like the ones above, but relies on
`GetBaseYXRatio()`/`ComputeIdealYXRatio()`. Bound to the right-aligned display slot next to
the Y/X Ratio textbox, the same slot the other rows use for their match-% displays.

Compares the currently effective ratio (`YXRatioOverride ?? GetBaseYXRatio()` — Skip
inherits the Default's own baseline, the same fallback chain as every other field) against
`ComputeIdealYXRatio()`. `1.0` means fully compensated — the same concept as the other rows'
`100%`, expressed as a ratio instead of a percent. This field deliberately never got percent
formatting. Trivially always `1.0` in Auto mode, by construction.

**Replaces an earlier version bound directly to `YXRatioString`**, found live by the user to
be a real design flaw rather than a missing feature: that binding just echoed whatever the
textbox already showed, so Auto mode displayed a pointless duplicate of the number right
next to it, and Skip mode (the default for every profile) showed nothing at all even when a
real, uncorrected aspect-ratio mismatch existed — silently hiding exactly the information
this display exists to surface.

Re-raised (`OnPropertyChanged(nameof(YXRatioMatch))`) from `ApplyAutoYXRatioSync`, which
covers every Width/Height/sync-mode trigger that function already handles, and separately
from `OnYXRatioStringChanged`'s manual-typing path — the one trigger
`ApplyAutoYXRatioSync` doesn't cover, since typing a Manual value doesn't go through that
function's own `YXRatioOverride` write (the `_isSyncing` guard suppresses it there).

### Visibility and the Auto toggle

`IsYXRatioVisible` gates on `ShowYXRatio` alone, so the field appears and is editable
regardless of Auto-Match Environment, and `GetEffectiveYXRatioOverride` likewise gates on
`ShowYXRatio` alone so a manual value is actually applied rather than dropped.

`IsYXRatioAutoAvailable` gates only the **Auto toggle** on `ShowAutoMatchEnvironment`, a
direct mirror of `IsRawAutoAvailable`. Raw Accel sensitivity already solved this exact
problem the same way; Y/X used to hide its whole row instead, which made the Settings
toggle look broken — turning it on appeared to do nothing, and any value set was silently
discarded.

The match display beside the field is gated on the same condition. With Auto-Match off, both
screen dimensions resolve to null, `ComputeIdealYXRatio` falls through to an aspect factor
of `1.0`, and "ideal" collapses to the Default's own ratio — so the match would just echo
the textbox. That is the same duplicate-of-itself problem described above, so it collapses
instead.

## `ApplyAutoOSSync()`

Win Speed selection formula. Target is `Effective(base) / S_target`.

## `ApplyAutoRegSync()`

Registry remainder formula. Target is `Effective(base) / (S_target × WinMult)`.

`RegistryCurveOverride` itself is stored at **full double precision** — the raw computed
`remainder`, intentionally unrounded — so it can be correctly re-quantized fresh if
DPI/pointer speed ever change later. Only `RegistryCurveString` (the textbox display) is
capped, via `FormatRegistryCurveValue()`.

That cap used to be a flat `.ToString("G6")`, an arbitrary unjustified digit count in the
same category as the flat `G10`/15-digit guesses this file warns against elsewhere. It is
now derived from the real, live registry-curve quantization step (`EncodeRegCurve`'s own
`step`, converted to a decimal-place count via `Math.Ceiling(-Math.Log10(step))`).

Found live: the old `G6` showed one digit beyond what the real step could distinguish — e.g.
`0.998785` vs `0.99878` differ by `5e-6`, smaller than the ~`2.6e-5` step, and encoded
identically. The fix trims exactly that fake digit.

`FormatRegistryCurveValue()` derives that step-based cap and hands off to
`FormatFixedPointInput(value, domainDecimalPlaces)`.

## `FormatFixedPointExact(value, decimalPlaces)`

The actual digit-rendering primitive shared by both `FormatFixedPointInput` and
`FormatMatchPercent`. Neither calls `value.ToString("0." + new string('#', decimalPlaces))`
directly any more.

**The .NET formatting limitation this exists for.** Found live via the user's own "asdf Copy"
profile (`Sensitivity = 0.567`, whose Win Speed match works out to `1.7718749999999999%`):
calling that custom picture-format string directly on a `double` **silently caps the
rendered precision below what's requested**, for values whose shortest round-trip form needs
many digits.

Confirmed outside any app logic at all, with plain `.ToString()` calls: for
`1.7718749999999999`, `decimalPlaces` 6 through 17 (`"0.######"` through
`"0.#################"`) all rendered the *identical* truncated `"1.771875"` — 7 significant
digits. Asking for more `#` placeholders did not reveal any more real digits. Only
`.ToString("G17")` reliably produced the true value.

This is a genuine, reproducible .NET limitation, not a bug in this app's math or in
`GetRealSignificantDigits` — which correctly computed `realSigFigs = 17` for this value the
whole time, confirmed via a live diagnostic print inside the running app before the fix.

**The fix:** get the value's exact round-trip string via `"G17"` (the one format specifier
proven to always work), parse that into a `decimal` (`NumberStyles.Float`, since `G17` can
itself emit scientific notation for small magnitudes — decimal correctly expands that back
to plain digits), then apply the same `"0." + '#'*N` format string to the *decimal* instead
of the double. Decimal's own custom-format rendering does not share double's cap, also
verified directly with the same methodology before implementing.

Falls back to the old direct-double-format call only if the decimal parse itself fails.
Decimal has a real but much larger ~28–29 significant-digit budget, and this app's values
never legitimately need more than 17, so that fallback should be unreachable in practice — it
exists only so a truly pathological input can't crash the app instead of just rendering
imperfectly.

Live-verified post-fix on the exact profile that surfaced it: both the Win Speed and Full
Match rows now show the true `1.7718749999999999%`, not the previously-truncated
`1.771875%`.

**If either caller is touched again, don't reintroduce a direct `value.ToString(format, ...)`
call on a `double` for a computed `decimalPlaces` — go through this function.**

## `FormatFixedPointInput(value, domainDecimalPlaces)`

Shared display formatter for every raw-value textbox (`SensitivityString`,
`RegistryCurveString`), called from `LoadProfile`, `OnSensitivityChanged`,
`OnRegistryCurveOverrideChanged`, `ApplyAutoRawSync`, and `FormatRegistryCurveValue`.

Always renders fixed-point, **never** scientific "E" notation. That was the original bug:
typing `0.00001`, switching profiles away and back, used to silently show `1E-05` — a plain
`ToString(CultureInfo.InvariantCulture)` call reformatting what was typed rather than just
displaying it.

Shows exactly this value's own real round-trip precision (`GetRealSignificantDigits`),
further capped by a genuine domain-specific floor when one exists (the registry curve's live
step, via `FormatRegistryCurveValue`). Pass `int.MaxValue` for `domainDecimalPlaces` when no
such floor applies — Sensitivity has none of its own, see
[the Raw Accel research note](#research-note-raw-accel-imposes-no-precision-floor).

**The `50`-decimal-place cap inside this function** is a UI sanity bound against a
pathologically tiny value producing a multi-hundred-character string. It is unrelated to,
and not a substitute for, the ~15-digit round-trip-safety budget used elsewhere
(`TryParsePositiveDouble`, `FormatMatchPercent`) — don't conflate the two. A value that
large or precise should already have been rejected earlier, at typing, or loaded as blank
from `profiles.json`, so 50 is deliberately generous rather than a precision guarantee.

Digit rendering goes through `FormatFixedPointExact`, not a direct `.ToString(format)` call.

## `RecalculateMatchAccuracy()`

Display math. Calls `_profileService.GetEffectiveSensitivityFactors(_editingProfile)` and
reads off whichever partial product each row needs (`.RawOnly`, `.WinOnly`, `.Full`) instead
of re-deriving the formula by hand. Safe because `AutoPersist()` already writes every
current field onto `_editingProfile` before this runs (see
[the ordering rule](#onchanged-callback-ordering)), so the real math-engine function sees
the same values the ViewModel does.

Formats each of the three match-% rows via `FormatMatchPercent`, falling back to scientific
notation at extreme magnitudes — not a fixed `"G10"`/`"F2"`-style format string.

Only the registry curve (Full Match row, EPP on) has a real domain-specific precision floor,
computed by nudging the live `EncodeRegCurve` step and measuring the actual
percentage-point delta, not a hardcoded digit count. Raw Accel-only, Win Speed-only, and
Full Match with EPP off pass `int.MaxValue` as the domain floor; in that case the value's own
real precision is the only cap.

**Don't revert to a flat format, and don't reintroduce a fixed digit count for Raw Accel,
without re-deriving why.**

### Exception safety

Wraps its `GetEffectiveSensitivity(bp)`/`GetEffectiveSensitivityFactors(_editingProfile)`
calls in a `try/catch (ArgumentOutOfRangeException)` as a last-resort safety net, falling
back to a literal `"Overflow%"` on all three rows. This should rarely fire now that
`GetEffectiveSensitivityFactors` itself doesn't throw for the common case, but an unhandled
exception here previously **crashed the entire app** just from typing an extreme value into
a field, before ever pressing Apply. Keep this catch even if it looks redundant — the
failure mode it prevents is a hard crash, not a bad display.

`EncodeRegCurve` has a **second** direct call site further down the same method — the
regStep nudge used only to derive the Full Match row's decimal precision. It is *not*
covered by the try/catch above, because it runs after that block, and it crashed the app
live even after the first fix shipped. It is guarded separately: skipped entirely when
`fullMatchPercent` is already non-finite (the nudge is moot then — it will display
`"NaN%"`/`"Infinity%"` regardless of decimal count), plus its own defensive `try/catch`.

**If `EncodeRegCurve` ever grows a third call site, it needs the same treatment.** Grep for
`EncodeRegCurve(` rather than assuming these two are the only ones.

## `FormatMatchPercent`

Local function inside `RecalculateMatchAccuracy`.

Does **not** use a fixed significant-digit constant. An earlier version used `15`, then
inconsistently `16` in one branch — both guesses, and the inconsistency was a real bug caught
live.

Instead it computes, per value, the exact number of significant decimal digits that specific
`double` needs to round-trip back to itself bit-for-bit: try formatting at `G1`, `G2`, … and
reparsing until one matches exactly, else `17`, the documented maximum any double ever
needs. This is the same notion .NET's own default double formatting uses internally, made
explicit so both the fixed-point and scientific-notation paths share one consistent,
per-value-correct cap instead of two different fixed guesses.

A domain floor (registry curve, when real) is combined via `Math.Min` on top of this, never
replacing it.

`trueIntegerDigits` is deliberately **unclamped** — it can be ≤ 0 for values under 1, e.g.
`0.05` has `trueIntegerDigits = -1`. Computed via the shared
`GetTrueIntegerDigitCount(absValue)` helper (`Math.Floor(Math.Log10(absValue)) + 1`), also
used by `FormatFixedPointInput`, factored out after the formula was found hand-typed in both
places, written months apart.

Uses `'#'` (optional-digit) placeholders, not `'0'` (required-digit) ones, in its
fixed-point format strings. `'0'` pads with trailing zeros out to the cap even when they
aren't real digits — a clean typed `1.12312312` displayed as `"112.312312000000%"` — a milder
version of the exact fake-precision problem this whole rework was meant to eliminate.

### Four bugs, in the order they were found

**(1) Clamping `trueIntegerDigits` to "at least 1"** miscomputed decimal-place placement for
any value under 1 — treating `0.05` as if its first significant digit were in the tenths
place instead of the hundredths.

**(2) The no-domain-floor path had no "too small" fallback at all**, so a genuinely tiny but
nonzero percentage (e.g. `Sensitivity = 0.0000001`) rounded straight to a false `"0%"`,
silently hiding a real value.

**(3) `realSigFigs` can be smaller than `trueIntegerDigits`.** Found *after* (1) and (2)
shipped, by the user hitting it in completely ordinary use rather than an extreme test value.

Whenever a value has trailing zeros in its integer part — e.g. `150.0` round-trips at just 2
significant digits (`"1.5E+02"`) despite having 3 integer digit positions — then
`decimalPlaces = realSigFigs - trueIntegerDigits` goes **negative**, and the code was
treating "negative" as "too large for fixed-point". That incorrectly routed an ordinary
value like a `150%` match into scientific notation (`"1.5E+002%"`).

Fixed by separating the two concerns the bug conflated: `decimalPlaces` is now clamped at a
minimum of `0` (a value like `150.0` just needs 0 decimals — not the same thing as "too
large"), and "too large for fixed-point" is judged independently against a fixed ceiling
(`trueIntegerDigits > 15`), the same fixed budget used everywhere else in this app, not
derived from `realSigFigs`.

**If you touch this function again:** `decimalPlaces` (how many decimals to show) and "is
this value too large for fixed-point at all" are two different questions — don't let a
per-value round-trip quantity (`realSigFigs`) answer the second one.

**(4) The small-side mirror of (3)**, which went unfixed for a while after (3) shipped.

`tooLargeForFixed` was — correctly, after an earlier fix, see `da3f760` "Fix regression:
clean/round percentages wrongly showed scientific notation" — judged against
`trueIntegerDigits` alone, an absolute magnitude ceiling. But `tooSmallForFixed` was still
being judged against `decimalPlaces`, the same conflated quantity bug (3) had already been
fixed for on the large side.

`decimalPlaces` conflates two different things: how many leading zeros a genuinely tiny
value needs (a real "too small" concern) versus how many significant digits *this* value
happens to need to round-trip exactly, which has nothing to do with its magnitude. An
ordinary-looking value like `1.7718749999999999` (`0.567 × 0.03125 × 100`, needing all 17
digits purely from float rounding noise, not from being large or small) could push
`decimalPlaces` past 15 and misfire scientific notation on a value whose exponent is
literally `0` (`"1.77...E+000%"` — which buys nothing).

Found live via the user's own "asdf Copy" profile screenshot. Fixed the same way as (3):
`tooSmallForFixed` now checks `trueIntegerDigits < -absoluteIntegerDigitCeiling`,
independent of `decimalPlaces`, mirroring `tooLargeForFixed` exactly.

**Verified live at every extreme tried:** huge Sensitivity (`132123123112.312312...%`,
`1.2345678901234567E+19%`), tiny Sensitivity (`9.999999999999999E-006%`, not `"0%"`),
ordinary round values (`150%`, not `"1.5E+002%"`), and mid-range values across both
branches. Bug (4) specifically re-verified via UI Automation on the exact profile that
surfaced it — Win Speed and Full Match render `1.7718749999999999%` in fixed-point, not
scientific notation, and at full real precision rather than truncated.

## `On*Changed` callback ordering

**`AutoPersist()` must come before `RecalculateMatchAccuracy()` everywhere.** Swapping them
causes stale model reads.

## The `_isSyncing` guard

The `_isSyncing` guard plus the `wasSyncing` save/restore pattern in
`ApplyAutoOSSync`/`ApplyAutoRegSync`. **Removing any guard causes reentrancy corruption on
profile load.**

---

# `Services/MouseService.cs`

The dedup check in `ApplyProfileAsync` compares computed
`(winSpeed, regResult, settingsFile, sens, epp)` values, **not object identity**. Reverting
to `ReferenceEquals` means Default changes won't propagate to already-active profiles.

---

# `Models/MouseProfile.cs`

Field types for `SensMultiplier` (`double?`), `WinPointerSpeed` (`int?`) and
`RegistryCurveOverride` (`double?`). **If these change type, the null-fallback logic
throughout the math engine silently misbehaves.**
