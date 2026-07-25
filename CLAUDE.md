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

### The math engine (compensation pipeline)

This is the most fragile part of the codebase. The exact do-not-touch
functions, fields, and invariants are maintained in a dedicated file, imported
here so it's always in context:

@PRECISION_CRITICAL_FILES.md

High-level pipeline, end to end:

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

Treat any change here as a precision-critical edit: read the imported file in
full first, and preserve the `_isSyncing`/`wasSyncing` reentrancy guards and
the `AutoPersist()`-before-`RecalculateMatchAccuracy()` callback ordering in
`ProfileEditorViewModel`.

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
