# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## Project identity

RawAccelProfiles is a Windows-only Avalonia UI (MVVM) desktop utility. It monitors the
foreground process/window and swaps mouse sensitivity "profiles," mathematically balancing
Raw Accel sensitivity, Windows Pointer Speed, and a Registry curve override so effective
cursor speed stays consistent across applications with different DPI/resolution/acceleration
settings.

Always treat `README.md` as the source of truth for product identity and GUI structure —
read it before making UI or behavioral changes.

Named `RawAccelProfiles` in code (namespace, assembly, exe), `rawaccel-profiles` as the
GitHub repo, and **Raw Accel Profiles** in user-visible text (window title, tray tooltip).

## Documentation layers

Five places hold documentation in this repo, each for a different reader making a different
decision. Before adding or editing any of them, ask: **who is reading this, and what
decision are they about to make?**

- **`README.md`** — map-level product identity, for someone deciding whether/how to use the
  app or navigate the UI. What it is, how it's laid out. No field-by-field detail, no
  formulas, no bug history.
- **Tooltips (in-app)** — field-level, for a user's mouse hovering that exact control right
  now. Two jobs only: (1) what this field does / what happens if you touch it, and (2) any
  *live, actionable* value it exposes that isn't visible elsewhere (e.g. match-accuracy %, an
  input-validity constraint). No formulas, no "why we built it this way," no bug history — if
  a tooltip is explaining an implementation detail, it's in the wrong layer.
- **In-code comments** — for the next person editing *this exact line*. Only the non-obvious:
  a hidden constraint, an invariant, a workaround, "don't do X, it looks equivalent but
  isn't." The strictest of the five — default to none; write one only when removing it would
  confuse a future reader.
- **`MATH-ENGINE.md`** — the formulas, precision edge cases and bug history behind every
  protected function listed below. This file holds the *rules*; that file holds the
  *reasons*.
- **This file and other doc files (`TOOLTIPS.local.md`, etc.)** — for the next person (or
  Claude) *changing the system*, not using it. Architecture, invariants and working
  constraints.

## Build / run / release

There is no test suite in this repo (no test project exists). Behavioural changes are
verified by running the app.

```powershell
# Build (Debug)
dotnet build src/RawAccelProfiles/RawAccelProfiles.csproj -c Debug

# Run
dotnet run --project src/RawAccelProfiles/RawAccelProfiles.csproj

# Build the shippable exe (self-contained, single-file, win-x64)
dotnet publish src/RawAccelProfiles/RawAccelProfiles.csproj -c Release
```

Also wired up as VS Code tasks (`build`, `clean`, `publish`; see `.vscode/tasks.json`).

**Every publish property lives in the csproj** — `RuntimeIdentifier`, `SelfContained`,
`PublishSingleFile`, `PublishReadyToRun`, `IncludeNativeLibrariesForSelfExtract`. That is
deliberate: `dotnet publish -c Release` is then the entire command, identical in CI and
locally, so there is one definition of a release build rather than two that can drift.

There were two PowerShell wrapper scripts here (`publish.ps1`, `release.ps1`) and a
`/release` slash command. All three are gone. They existed only to wrap a long command line,
and the line was long solely because its configuration was in the wrong place. They were
also a second, untypeable definition of how to build, with the project name hardcoded in
three places — which is exactly how one of them ended up stale after the project was
renamed. **Don't reintroduce a build script;** if a publish setting needs changing, change
the csproj.

Because `RuntimeIdentifier` is set, build output is RID-qualified:
`bin/Debug/net10.0/win-x64/`, not `bin/Debug/net10.0/`. `.vscode/launch.json` depends on
that path.

### Releasing

**Pushing a `v*` tag is what cuts a release.** Ordinary pushes to `main` do nothing.

```bash
git tag v0.1.0
git push --tags
```

`.github/workflows/release.yml` then builds on a clean Windows runner, zips the exe,
generates a SHA256, and attaches both to a GitHub Release.

**The tag is the single source of truth for the version.** It is passed to the build as
`-p:Version`, so the binary's `ProductVersion` and the zip's filename cannot disagree with
the tag. `<Version>` in the csproj is only the fallback for local builds — don't rely on
editing it to version a release.

The workflow stages by **allowlist**, copying only the exe. The publish folder is also where
the app writes `profiles.json` if the exe is ever run from there, and that file holds
settings and real profile data. A blocklist would ship whatever nobody thought to exclude.
It prints what it left behind, so a genuinely-needed new sidecar file can't be dropped
silently.

Target framework is `net10.0`, `LangVersion` is `preview`. The app is Windows-only (P/Invoke
into `user32.dll`, registry access, `SystemParametersInfo`) — code that touches hardware must
be guarded with `OperatingSystem.IsWindows()`, since the project can still be built and
analyzed cross-platform.

## Architecture

**MVVM with CommunityToolkit.Mvvm, no DI container.** `App.axaml.cs`
(`OnFrameworkInitializationCompleted`) manually constructs every service and view-model in
dependency order and wires them together. **Read that method first** when tracing how
anything is connected.

There is **no** `ViewLocator` and **no** `NavigationService` — this doc described both for a
while; neither has ever existed, and nothing in `App.axaml` registers an
`Application.DataTemplates` entry. Don't assume a view-model gets a view for free by naming
convention. The two real resolution paths:

- **The one always-on view, `ProfileEditorView`, is instantiated directly in XAML** —
  `MainWindow.axaml`'s `<views:ProfileEditorView DataContext="{Binding ProfileEditor}" />`.
  There is no in-place navigation and no screen swapping; the Profile Editor *is* the main
  content area, permanently.
- **Every other screen is a modal `Window` opened through `IWindowService`**
  (`Services/AvaloniaWindowService.cs`) — Settings, Target Manager, and the Raw Accel
  settings-file name dialog. Each gets a fresh view-model per open, handed to a `Window`
  subclass's second constructor (`SettingsWindow(SettingsViewModel)` etc.), which sets
  `DataContext` and subscribes to the view-model's `RequestClose` event. **That's the pattern
  to follow for a new screen: add a method to `IWindowService`, not a route.**

Both paths are explicit and typed, which is what lets every `.axaml` root carry an
`x:DataType` and compile its bindings (`AvaloniaUseCompiledBindingsByDefault` is `true` in
the csproj).

New `MouseProfile`/ViewModel properties follow CommunityToolkit's `[ObservableProperty]` +
manual `partial void On*Changed` callback pattern — mirror the existing style rather than
introducing a different MVVM idiom.

### Component inventory

**Services** (`src/RawAccelProfiles/Services/`)

| File | Responsibility |
|---|---|
| `ProfileService.cs` | The hub. Owns `Profiles`, `Settings`, all persistence, and the math engine. Everything else depends on it. |
| `MouseRegistryBridge.cs` | `static`. MarkC no-acceleration curve: registry curve read/write, live DPI, live pointer-speed multiplier, EPP toggle. |
| `MouseService.cs` / `IMouseService.cs` | Applies a resolved profile to hardware in the required order, deduping against last-applied state. |
| `RawAccelService.cs` | Applies a Raw Accel settings file + sensitivity live, by invoking Raw Accel's `writer.exe`. |
| `RawAccelSettingsRepository.cs` | The *library* of Raw Accel settings files — import, duplicate, rename, delete, list. Pure file persistence; distinct from `RawAccelService`, which applies one. |
| `ProcessWatcher.cs` / `IProcessWatcher` | Win32 foreground hook; matches windows to profiles and drives auto-switching. |
| `WindowResolver.cs` | `static`. Win32 window-attribute resolution and the noise-class filter. Single source of truth shared by `ProcessWatcher` and `TargetManagerViewModel`. |
| `AvaloniaWindowService.cs` / `IWindowService.cs` | Opens the three modal windows, keeping ViewModels UI-agnostic. |
| `AvaloniaStorageService.cs` / `IStorageService.cs` | Folder picker only (`PickFolderAsync`). |

**ViewModels** (`src/RawAccelProfiles/ViewModels/`)

| File | Notes |
|---|---|
| `ProfileEditorViewModel.cs` | By far the largest (~1600 lines). Holds the display-side math — see `MATH-ENGINE.md`. |
| `MainWindowViewModel.cs` | Sidebar: profile list, search filter, the action-bar commands. |
| `SettingsViewModel.cs` | Settings window. Its properties proxy onto an `AppSettings` instance, which is why they are hand-written rather than `[ObservableProperty]` — see the note below. |
| `TargetManagerViewModel.cs` | App Rules: live window picker and rule matching. |
| `RawAccelSettingsNameDialogViewModel.cs` | Tiny name-entry dialog. |
| `ViewModelBase.cs` | Empty `ObservableObject` subclass. Every ViewModel derives from it. |

**Views** (`src/RawAccelProfiles/Views/`) — `MainWindow`, `ProfileEditorView`,
`SettingsWindow`, `TargetManagerWindow`, `RawAccelSettingsNameDialog`.

`SettingsViewModel`'s hand-written properties are **not** drift to be cleaned up:
`[ObservableProperty]` generates a backing field and structurally cannot express a property
that proxies onto `Settings.X`. Converting them would be a bug.

### Core domain model

A `MouseProfile` (`Models/MouseProfile.cs`) bundles: a Raw Accel settings-file reference, a
sensitivity multiplier, Windows Pointer Speed, a Registry curve override, target screen
width/height, a Y/X ratio override, an EPP (Enhance Pointer Precision) toggle, and a list of
`TargetAssociation`s (exe name / full path / window class / window title) used to auto-match
foreground windows to the profile.

Nearly every numeric/bool field is nullable — **null means "inherit from the Default"** — and
each field has its own `SyncMode` (`Auto` / `Manual` / `Skip`) governing whether it's
computed automatically, set explicitly, or skipped.

The **Default** profile is a sentinel (`IsDefault = true`) acting as the mathematical
anchor every other profile's compensation is computed against. `ProfileService.NormalizeDefault`
enforces invariants on it (e.g. it can never carry `Auto`-computed OS/Registry values) on
every load and save.

## Protected code

**Do not modify anything below without an explicit, scoped instruction to do so.** Each entry
is listed with the one-line reason it is protected. **Read the matching section of
`MATH-ENGINE.md` before touching any of them** — every one of these is protected because
something already broke there, was found live in the running app, and cost real time to
diagnose.

**`Services/MouseRegistryBridge.cs`**

| Function | Why protected |
|---|---|
| `QuantizeToFixedPoint1616` | Single implementation of the never-round-to-zero clamp. A zero Y curve stops the pointer dead when EPP is on. Previously existed as two copies that were only coincidentally in sync. |
| `EncodeRegCurve` (both overloads) | The 16.16 round-trip model. Changing the anchor, DPI factor or `winSpeedMult` division silently breaks every match display and compensation. |
| `ApplyRegistryBridge` / `CurveToBinary` | The real registry write. Must stay formula-identical to `EncodeRegCurve`. |

There used to be a `Math.Max(regResult, 0.01)` floor here. **It was removed deliberately and
must not be reintroduced** — `QuantizeToFixedPoint1616` already guarantees no literal zero,
exactly, for any nonzero input, and the floor actively destroyed accuracy below `0.01`.

**`Services/ProfileService.cs`**

| Function | Why protected |
|---|---|
| `WinMultipliers[]` | 20-entry table, single source of truth for all Win Speed math. A wrong value means wrong compensation applied to hardware. |
| `GetEffectiveSensitivityFactors` | The one implementation of the 4-factor formula. Must not throw — callers of `.RawOnly`/`.WinOnly` depend on that. |
| `CalculateCompensations` | Apply-time math. Must stay in sync with the ViewModel's `ApplyAutoOSSync`/`ApplyAutoRegSync`. |
| `ResolveEpp` | Single source of truth for the EPP fallback chain, including the final `?? false` tier. |
| `TryParsePositiveDouble` / `TryParsePositiveInt` / `CountMantissaDigits` | Shared by typed GUI input *and* JSON loading. Don't let a second copy reappear in the ViewModel. |

**`ViewModels/ProfileEditorViewModel.cs`**

| Function | Why protected |
|---|---|
| `GetBaseSens` / `GetBaseWinPointerSpeed` / `GetBaseRegCurve` / `GetEffectiveEpp` / `GetBaseYXRatio` | The base-inheritance resolvers. Every one of these exists because a fallback chain skipped the Default tier and produced silently wrong numbers. |
| `ComputeIdealYXRatio` / `YXRatioMatch` | One shared Y/X formula, so Auto-sync and the match display can't drift apart. |
| `ApplyAutoOSSync` / `ApplyAutoRegSync` / `ApplyAutoRawSync` / `ApplyAutoYXRatioSync` | The Auto-sync formulas, and the `_isSyncing` / `wasSyncing` guards. Removing a guard causes reentrancy corruption on profile load. |
| `FormatFixedPointExact` | Works around a real .NET limitation: a `"0.###"` picture format on a `double` silently truncates precision. Don't reintroduce a direct `.ToString(format)` call on a double. |
| `FormatFixedPointInput` / `FormatRegistryCurveValue` | Never render scientific notation in a raw-value textbox; caps derive from the real live quantization step, not a guessed digit count. |
| `RecalculateMatchAccuracy` / `FormatMatchPercent` | Per-value round-trip precision, not a fixed digit count. Four separate bugs were fixed here; both `EncodeRegCurve` call sites need their guards. |
| `GetRealSignificantDigits` / `GetTrueIntegerDigitCount` | Shared precision helpers, factored out after being found hand-typed in two places. |

**Ordering rule:** in every `On*Changed` callback, `AutoPersist()` must come **before**
`RecalculateMatchAccuracy()`. Swapping them causes stale model reads.

**`Services/MouseService.cs`** — the dedup check in `ApplyProfileAsync` compares computed
values, not object identity. Reverting to `ReferenceEquals` means Default changes won't
propagate to already-active profiles.

**`Models/MouseProfile.cs`** — field types for `SensMultiplier` (`double?`),
`WinPointerSpeed` (`int?`), `RegistryCurveOverride` (`double?`). If these change type, the
null-fallback logic throughout the math engine silently misbehaves.

## Foreground-window profile switching

`ProcessWatcher` (`Services/ProcessWatcher.cs`) is a Win32 `SetWinEventHook` listener
(`EVENT_SYSTEM_FOREGROUND` + `EVENT_OBJECT_DESTROY`) maintaining its own match index — by exe
name, full path and window class via dictionary lookups; by window title via substring scan —
built from `TargetAssociation`s across all profiles with `AutoSwitchEnabled`.

On foreground change it applies the matching profile. On window destroy it implements "Last
Man Standing": if no other visible window still matches the currently-active profile, it
reverts to the Default.

That revert is suppressed during system logoff/shutdown (`NotifySessionEnding`, wired to
`desktop.ShutdownRequested`) so quitting apps during shutdown don't spuriously revert
registry state.

The index is rebuilt whenever the `Profiles` collection changes, or a profile's
`AutoSwitchEnabled`/`TargetAssociations` changes.

The hook is installed **out-of-context**, so its callback arrives on the thread that
installed it — the UI thread, since the watcher is constructed in `App.axaml.cs`. Code there
that looks like it is marshalling from a background thread generally isn't.

## Persistence

**`ProfileService` owns all persistence.** It writes a single `profiles.json` next to the exe
(`AppContext.BaseDirectory`), serialized behind a `SemaphoreSlim` write lock to prevent
concurrent-save truncation races.

That one file holds **both** `AppSettings` and the `Profiles` list. The name predates
settings being folded in, and it stays: `profiles.json` describes its contents rather than
the app, and it was deliberately not called `settings.json` to avoid colliding with Raw
Accel's own `settings.json` in the same folder. There is no separate settings service.

`RawAccelSettingsRepository` manages the *library* of Raw Accel settings files (`.json`
curves) — import, duplicate, rename, delete, list — stored under
`AppSettings.RawAccelSettingsDirectory`, which defaults to the portable app folder. It is
pure file persistence with no business logic beyond blacklisting protected filenames, and is
distinct from `RawAccelService`, which applies one such file to hardware.

Everything is portable and per-user under `HKCU` and the app's own folder. **No admin
elevation is required** for normal operation — `writer.exe` and all registry writes are
HKCU-scoped.

### Loading tolerates a malformed numeric field instead of crashing

`LoadAsync()` used to deserialize with a plain `JsonSerializer.Deserialize<PersistedData>`.
If any single field had the wrong JSON shape — a string where `SensMultiplier` expects a
number — deserialization threw, and nothing caught it: `App.axaml.cs` calls
`LoadAsync().GetAwaiter().GetResult()` directly, before any window exists. One bad value
crashed the entire app on startup.

Fixed with two private `JsonConverter`s registered in `_jsonOptions.Converters`
(`LenientPositiveDoubleConverter`/`LenientPositiveIntConverter`, next to the
`JsonStringEnumConverter` already there). For `double?`/`int?` fields they read the raw JSON
number and run it through `TryParsePositiveDouble`/`TryParsePositiveInt` — the same shared
check typed GUI input uses. If it doesn't pass (not a number, negative, zero, or too many
digits) the field loads as `null`, identical to a blank field, instead of throwing.

**Every flavor of "invalid" is treated the same way on purpose.** An earlier version tried to
keep a bad-but-numeric value visible on load, showing the mangled number with a warning icon,
distinct from a non-numeric value which would just be dropped. That was deliberately
simplified away — two different behaviors for "invalid" depending on *how* it's invalid was
worse than one consistent rule.

The rest of a profile — name, target associations, every other field — is unaffected by one
bad field. Nothing about loading drops a whole profile.

## Working style notes

- **No code-behind logic in Views** — implement behavior in ViewModels and Commands.
  `MainWindow.axaml.cs` currently breaks this with four inline-rename handlers plus a
  `IsVisibleObserver` helper class, reaching through `DataContext` casts into the Model and
  holding a View-owned `_currentlyEditing` field; that is known and tracked, not a pattern
  to copy. Layout *measurement* in code-behind is a deliberate exception — see below.
- **Nullable fields on `MouseProfile` follow `profile.Field ?? globalDefault.Field ??
  hardcodedFallback`.** Skipping the middle tier is the single most common bug in this
  codebase — it has been found and fixed at least six separate times. If you write a `??`
  chain against a profile field, check it has all three tiers.
- **Never touch the protected functions above** without an explicit, scoped instruction.

### Avalonia styling

Define shared `Style Selector="X.class-name"` rules in `App.axaml` for anything repeated
across multiple elements — font weight, spacing, sizing — instead of hardcoding the same
values inline on each one. Repeated inline values drift out of sync the moment one gets tuned
and the others don't.

That happened for real in `SettingsWindow.axaml`: each section header independently hardcoded
`FontWeight="SemiBold" Margin="0,4,0,0"`, and the toggle-vs-folder-picker rows each hardcoded
their own spacing — the exact mistake `Button.dialog-action` had already been introduced to
avoid, just not carried through.

A genuinely one-off value on a single element is fine. The tell that something belongs in a
shared style is the same value getting copy-pasted onto more than one element.

### Window and pane minimum sizes are measured, never guessed

`MainWindow.axaml.cs` measures the action bar to get the sidebar column's `MinWidth`, and
reads the content pane's real desired width from the first layout pass for `Window.MinWidth`.
`TargetManagerWindow.axaml.cs` does the same for its own bottom row, replacing a hardcoded
`700` that had gone stale.

Four traps, all found live, all previously shipped as bugs:

- **A `*` Grid column does not grow to fit its children.** The main content column is `Auto`,
  so it always takes its full natural width and the `*` sidebar absorbs every squeeze. The
  only thing that actually protects the action bar is a `MinWidth` on the `ColumnDefinition`
  itself. Without it the bar was arranged 252 wide against the 266 it needs, and the buttons
  (each pinned at `MinWidth="28"`) overlapped instead of shrinking.
- **A synthetic `Measure()` from `Opened` under-reports the content width** (473 against a
  real 487) and **cannot** reserve the scrollbar at any available height, because scrollbar
  visibility is settled during arrange, not measure. `Measure(new Size(∞, 1))` was written to
  do exactly that and silently did not. Read the number a real layout pass produced instead.
- **A live subscription recomputing `MinWidth` on every layout change** forces the whole
  window wider during a vertical-only resize (reverted in `9e205f2`). Use a one-shot
  `LayoutUpdated` hook that unsubscribes itself.
- **`Window.MinWidth` assigned after `Show()` is too late** for `App.axaml.cs`'s saved-size
  restore, which clamps the remembered width against the **XAML** value. Raising a minimum
  never widens an already-open window, so the window can open below its own minimum and stay
  there — which is what made an earlier correct fix to the number look like it had done
  nothing. The restore path needs its own clamp (`if (ClientSize.Width < MinWidth)`).

The action bar also carries `UseLayoutRounding="False"` on `SidebarHost`. `Window.MinWidth`
reserves the content scrollbar's width even when no scrollbar is showing, so those spare
pixels land in the sidebar and don't divide evenly by 8; layout rounding snapped some columns
up and others down, making the buttons visibly uneven at small widths. It is set on the
sidebar rather than the action bar alone so the profile list above rounds the same way and
the two stay aligned.

### Invalid GUI input is flagged, never silently discarded or auto-corrected

`TryParsePositiveDouble`/`TryParsePositiveInt` — positive, and for the double variant no more
than 15 significant digits (the real IEEE 754 budget, not a guess) — live as `public static`
methods on **`ProfileService`**, not the ViewModel, so typing and JSON loading share one
implementation. See Persistence above.

`ProfileEditorViewModel`'s `On*StringChanged` handlers call them and set a matching
`IsXxxInputValid` observable bool; the View overlays a small red ⚠ `TextBlock`
(`Classes="input-error-icon"`) when it's `false`.

The underlying property is **never written for a rejected value** — Apply always uses the
last valid value, never whatever's sitting unconfirmed in a textbox. The flag only makes that
already-safe behavior visible instead of silent. It is unconditionally reset to `true` in
`LoadProfile`, since loading now guarantees a stored value is either blank or already valid.

**Avalonia gotcha, found live twice.** A plain `Style Selector="TextBox.input-error"` setting
`BorderBrush` was silently overridden by the theme's own `:focus` styling while a TextBox was
focused — the red border only appeared after clicking away, exactly when the flag stops being
useful — and separately by the theme's `:pointerover` styling on the editable ComboBoxes,
where the border vanished on hover.

An explicit `:focus`-scoped selector variant did **not** fix it either: the theme's state
styling wins at the control template's internal level, not just via selector specificity.
Don't assume a same-specificity pseudo-class override will beat it — verify live.

The border approach was **removed entirely** (a cue that's only sometimes visible reads as
broken, which is worse than no cue) in favor of a separately overlaid
`TextBlock.input-error-icon` glyph. It isn't part of either control's template, so no
theme-internal state styling can hide it, and it behaves identically on TextBox and ComboBox.

The editable Screen Width ComboBox needs a larger right `Margin` on its icon instance (`24`
vs `6`), set per-instance rather than in the shared style, so the icon clears the ComboBox's
built-in dropdown arrow instead of overlapping it.
