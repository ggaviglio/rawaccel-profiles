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
override, target DPI/screen-width, an EPP (Enhance Pointer Precision) toggle,
and a list of `TargetAssociation`s (exe name / full path / window class /
window title) used to auto-match foreground windows to the profile. Nearly
every numeric/bool field is nullable — **null means "inherit from the Global
Default"**, and each field has its own `SyncMode` (`Auto` / `Manual` / `Skip`)
governing whether it's computed automatically, set explicitly, or skipped.

The **Global Default** is a sentinel profile (`IsGlobalDefault = true`) that
acts as the mathematical anchor/baseline every other profile's compensation is
computed against. `ProfileService.NormalizeGlobalDefault` enforces invariants
on it (e.g. it can never carry `Auto`-computed OS/Registry values) on every
load/save.

### The math engine (compensation pipeline) — precision-critical

This is the most fragile part of the codebase. High-level pipeline, end to end:

1. `ProfileService.GetEffectiveSensitivity(profile)` computes a single
   dimensionless "effective sensitivity" number for a profile: sensitivity
   multiplier × Windows Pointer Speed multiplier (`WinMultipliers[]`, a 20-entry
   table) × registry curve factor (round-tripped through
   `MouseRegistryBridge.EncodeRegCurve`, 16.16 fixed-point) × DPI compensation ×
   screen-width compensation. It's a one-line `.Full` over
   `GetEffectiveSensitivityFactors(profile)`, which computes and returns those
   five factors individually (`EffectiveSensitivityFactors` record struct) so a
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
   `Effective(GlobalDefault)` to pick the closest discrete Windows Pointer Speed
   step, then the exact Registry curve value that makes the profile's effective
   sensitivity match the Global Default's.
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
    the 5-factor effective-sensitivity formula (sens × winMult × regCurve ×
    dpiComp × screenComp), returned as an `EffectiveSensitivityFactors` record
    struct (`.RawOnly`, `.WinOnly`, `.Full` partial-product properties) instead
    of a single combined number. `GetEffectiveSensitivity()` is just `.Full`
    over this. Uses `EncodeRegCurve` for the `RegCurve` factor, feeding every
    comparison — removing that call is a silent precision regression. The
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
  - `CalculateCompensations()` — the apply-time math. Must stay in sync with
    `ProfileEditorViewModel`'s `ApplyAutoOSSync`/`ApplyAutoRegSync`.
- **`ViewModels/ProfileEditorViewModel.cs`** (Math Engine section, ~lines 305–440)
  - `ApplyAutoOSSync()` — Win Speed selection formula: target is
    `Effective(base) / S_target`.
  - `ApplyAutoRegSync()` — Registry remainder formula: target is
    `Effective(base) / (S_target × WinMult)`.
  - `RecalculateMatchAccuracy()` — display math. Calls
    `_profileService.GetEffectiveSensitivityFactors(_editingProfile)` and reads
    off whichever partial product each row needs (`.RawOnly`, `.WinOnly`,
    `.Full`) instead of re-deriving the formula by hand — safe because
    `AutoPersist()` already writes every current field onto `_editingProfile`
    before this runs (see the ordering rule below), so the real math-engine
    function sees the same values the ViewModel does. Formats each of the
    three match-% rows via `FormatMatchPercent`, capped by `double`'s own
    ~15-digit significant-figure budget, falling back to scientific notation
    at extreme magnitudes — not a fixed `"G10"`/`"F2"`-style format string.
    Only the registry curve (Full Match row, EPP on) has a real domain-specific
    precision floor beneath the double-precision cap — computed by nudging the
    live `EncodeRegCurve` step and measuring the actual percentage-point delta,
    not a hardcoded digit count. Raw Accel-only, Win Speed-only, and Full Match
    with EPP off pass `int.MaxValue` as the domain floor (i.e. no domain cap —
    see the Raw Accel research note above for why). Don't revert to a flat
    format, and don't reintroduce a fixed digit count for Raw Accel, without
    re-deriving why (see git history for the reasoning).
    `FormatMatchPercent` clamps `domainDecimalPlaces` to the double-precision
    budget **immediately**, before any branch — a caller passing the
    `int.MaxValue` "no domain cap" sentinel straight into a composite format
    string (`$"E{domainDecimalPlaces}"`) throws `FormatException` ("Format
    specifier was invalid"), a real crash found live when this rework first
    shipped. Also uses `'#'` (optional-digit) placeholders, not `'0'`
    (required-digit) ones, in its fixed-point format strings — `'0'` pads
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
    display.
  - `On*Changed` callback **ordering** matters: `AutoPersist()` must come
    before `RecalculateMatchAccuracy()` everywhere. Swapping them causes stale
    model reads.
  - The `_isSyncing` guard + `wasSyncing` save/restore pattern in
    `ApplyAutoOSSync`/`ApplyAutoRegSync`. Removing any guard causes reentrancy
    corruption on profile load.
- **`Services/MouseService.cs`** — the dedup check in `ApplyProfileAsync`
  compares computed `(winSpeed, regResult, template, sens, epp)` values, not
  object identity. Reverting to `ReferenceEquals` means Global Default changes
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
the Global Default. This revert is suppressed during system logoff/shutdown
(`NotifySessionEnding`, wired to `desktop.ShutdownRequested`) so quitting apps
during shutdown doesn't spuriously revert registry state. The index is rebuilt
whenever the `Profiles` collection changes or a profile's
`AutoSwitchEnabled`/`TargetAssociations` changes.

### Persistence

- `ProfileService` persists `Profiles` to `profiles.json` next to the exe
  (`AppContext.BaseDirectory`), serialized with a `SemaphoreSlim` write lock to
  prevent concurrent-save truncation races.
- `SettingsService` persists `AppSettings` (Raw Accel directory, templates
  directory, registry-bridge opt-in, startup/tray behavior).
- `TemplateService` manages the library of Raw Accel `.json` curve templates
  (import/rename/delete), stored under `AppSettings.TemplatesDirectory`
  (defaults to the portable app folder).
- Everything is portable/per-user under `HKCU` and the app's own folder — no
  admin elevation is required for normal operation (`writer.exe` and all
  registry writes are HKCU-scoped).

## Working style notes

- No code-behind logic in Views — implement behavior in ViewModels and Commands.
- Nullable fields on `MouseProfile` follow the fallback pattern
  `profile.Field ?? globalDefault.Field ?? hardcodedFallback`.
- Never touch the math-engine functions listed above without an explicit,
  scoped instruction to do so.
