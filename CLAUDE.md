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
   screen-width compensation. This is the single source of truth used both for
   "match accuracy" display and as the target for compensation math.
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
    don't let the two overloads drift apart. Before quantizing, it applies
    `Math.Max(regResult, 0.01)` — **the same floor `ApplyRegistryBridge` applies
    to the real write** — then runs the floored value through
    `QuantizeToFixedPoint1616` on node `[1]` and converts back to regResult
    units. Both the 0.01 floor and the zero-avoidance clamp must be present
    here or an extreme (e.g. near-zero) override under-predicts what the real
    write would actually do — this was a real, verified bug (not just cosmetic):
    an EPP Override of `0.00001` briefly displayed a wildly wrong "6025993%"
    match because only the zero-avoidance clamp had been added to the display
    path and the 0.01 floor was missing entirely. Reads DPI and Windows Pointer
    Speed **live from the current system state**, not from the profile being
    displayed/edited — for a profile that isn't the one currently applied to
    Windows, this is technically the wrong environment for prediction, but in
    practice only matters when `regResult` is already within about a factor of
    2 of `step` (~1e-5–1e-4), since otherwise `EncodeRegCurve(x) ≈ x` regardless
    of which live DPI/speed set the granularity. Not worth refactoring into an
    explicit-parameter signature unless that edge case turns out to matter for
    realistic profile values.
  - `ApplyRegistryBridge()` → `CurveToBinary()` — the actual registry write:
    `Y[i] = X[i] × (150/DPI) / winSpeedMult × regResult`. Must stay
    formula-identical to `EncodeRegCurve` or the model stops predicting
    reality — enforced now by both calling the shared
    `QuantizeToFixedPoint1616` helper described above instead of duplicating
    the clamp logic. `winSpeedMult` is read live from the registry immediately
    before this call (pointer speed is written first so the live value is
    already correct for the profile). Despite the "16.16" name, each curve node
    is stored as a **signed 64-bit integer**, not a 32-bit container — real
    overflow ceiling is `long.MaxValue / 65536` per node (~1.4×10^14), not what
    "16.16" might suggest; `QuantizeToFixedPoint1616` throws
    `ArgumentOutOfRangeException` rather than silently wrapping via an
    unchecked `(long)` cast. `Math.Max(regResult, 0.01)` is applied *before*
    the DPI/winSpeedMult division, so it doesn't fully protect against a large
    enough DPI still shrinking a curve value below the smallest representable
    fixed-point step — that's what the zero-avoidance clamp inside
    `QuantizeToFixedPoint1616` is for. A zero breakpoint here means a genuinely
    frozen cursor at real-world usage speeds when EPP is on, not just a display
    bug — this guard is about actual applied hardware behavior.
  - The Raw Accel sensitivity tooltip's stated max (`123456789012345.12`) is
    **unverified** — no derivation or source anywhere in the repo, looks like
    placeholder text. Don't treat it as a real researched limit.
- **`Services/ProfileService.cs`**
  - `WinMultipliers[]` — the 20-entry table is the single source of truth for
    all Win Speed math. A wrong value means wrong compensation applied to
    hardware.
  - `GetEffectiveSensitivity()` — uses `EncodeRegCurve`, feeds every
    comparison. Removing the encode call here is a silent precision regression.
  - `CalculateCompensations()` — the apply-time math. Must stay in sync with
    `ProfileEditorViewModel`'s `ApplyAutoOSSync`/`ApplyAutoRegSync`.
- **`ViewModels/ProfileEditorViewModel.cs`** (Math Engine section, ~lines 305–440)
  - `ApplyAutoOSSync()` — Win Speed selection formula: target is
    `Effective(base) / S_target`.
  - `ApplyAutoRegSync()` — Registry remainder formula: target is
    `Effective(base) / (S_target × WinMult)`.
  - `RecalculateMatchAccuracy()` — display math; uses `EncodeRegCurve` on the
    reg curve value. Formats each of the three match-% rows with dynamically
    derived decimal places (from the real Raw Accel/registry-curve precision
    floor, further capped by `double`'s own ~15-digit significant-figure
    budget), falling back to scientific notation at extreme magnitudes —
    not a fixed `"G10"`/`"F2"`-style format string. Don't revert to a flat
    format without re-deriving why (see git history for the reasoning).
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
