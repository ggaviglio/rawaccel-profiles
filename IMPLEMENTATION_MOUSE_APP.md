# IMPLEMENTATION SPEC: Mouse App

> **Written:** 2026-02-23 — reflects committed state `13d9772` plus planned features.  
> **Precision-critical files:** See `PRECISION_CRITICAL_FILES.md` before modifying any math.

---

## 1. PROJECT STRUCTURE

```
src/MouseApp/
├── Models/
│   └── MouseProfile.cs          — data model, all nullable fields
├── Services/
│   ├── ProfileService.cs        — SSOT for WinMultipliers, GetEffectiveSensitivity, CalculateCompensations
│   ├── MouseService.cs          — hardware apply, dedup by computed value tuple
│   ├── MouseRegistryBridge.cs   — 16.16 fixed-point write + EncodeRegCurve round-trip
│   ├── RawAccelDriverProvider.cs — template swap + writer.exe call
│   ├── ProcessWatcher.cs        — Win32 WinEvent hook, foreground + destroy handling
│   ├── NavigationService.cs
│   ├── SettingsService.cs
│   ├── TemplateService.cs
│   ├── AvaloniaStorageService.cs
│   └── AvaloniaWindowService.cs
├── ViewModels/
│   ├── ProfileEditorViewModel.cs — math engine, auto-sync, match accuracy display
│   ├── MainWindowViewModel.cs
│   ├── SettingsViewModel.cs
│   └── TargetManagerViewModel.cs
├── Views/
│   ├── MainWindow.axaml(.cs)
│   ├── ProfileEditorView.axaml(.cs)
│   ├── SettingsView.axaml(.cs)
│   └── TargetManagerWindow.axaml(.cs)
└── Bootstrap/
    └── ServiceCollectionExtensions.cs
```

---

## 2. CURRENT MATH ENGINE (as of 13d9772)

### 2.1 The Hardware Pipeline

```
Mouse hardware (CPI counts)
  → Raw Accel driver (kernel, double precision)
  → SmoothMouseYCurve / EPP (our regCurve, 16.16 fixed-point)
  → Windows Pointer Speed (WinMult, 20 discrete steps)
  → Cursor delta (physical pixels)
```

Raw Accel intercepts **before** Windows processes anything. EPP/SmoothMouseCurve only applies if "Enhance Pointer Precision" is enabled in Windows Mouse settings.

### 2.2 Effective Sensitivity Formula

```
Effective(profile) = SensMultiplier × WinMult[WinPointerSpeed] × EncodeRegCurve(RegistryCurveOverride)
```

Null fields inherit from Global Default (not from neutral 1.0), so a fully-null profile is a 100% match by definition.

Fallback chain for each field when null:
- `SensMultiplier ?? GlobalDefault.SensMultiplier ?? 1.0`
- `WinPointerSpeed ?? GlobalDefault.WinPointerSpeed ?? 10`  (index 9 = 1.0×)
- `RegistryCurveOverride ?? GlobalDefault.RegistryCurveOverride ?? 1.0`

### 2.3 WinMultipliers Table (single source of truth in ProfileService)

```csharp
// Index 0 = speed 1 (0.03125×), index 9 = speed 10 (1.0×), index 19 = speed 20 (3.5×)
{ 0.03125, 0.0625, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0,
  1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5 }
```

**DO NOT MODIFY.** These are the exact Windows-documented multipliers.

### 2.4 EncodeRegCurve (MouseRegistryBridge)

Round-trips `regResult` through 16.16 fixed-point encoding using the tightest anchor (2.5):

```csharp
long fixedVal = (long)Math.Round(2.5 * regResult * 65536.0);
return fixedVal / (2.5 * 65536.0);
// Step size ≈ 6.1e-6, max rounding error ≈ 3e-6 (sub-pixel)
```

**This formula must stay identical between `EncodeRegCurve` and `CurveToBinary` in `ApplyRegistryBridge`.** Breaking this parity means the display model no longer predicts reality.

### 2.5 Three-Row Match Accuracy Display

```
Raw Accel Only  : (curSens / S_base) × 100%
                  — how much raw DPI shifted; S_base denominator only

Win Speed Only  : (curSens × WinMult) / Effective(base) × 100%
                  — how close integer steps get you

Full Match      : (curSens × WinMult × EncodeRegCurve(regCurve)) / Effective(base) × 100%
                  — terminal accuracy after registry fine-tune
```

100% Full Match = exact compensation across all three layers.

### 2.6 Auto-Sync Targets

**ApplyAutoOSSync** — choose WinMult closest to:
```
target = Effective(base) / S_target
```

**ApplyAutoRegSync** — solve exact remainder:
```
regResult = Effective(base) / (S_target × WinMult_chosen)
```

Both always call `GetEffectiveSensitivity(baseProfile)` — never compute base independently.

### 2.7 CalculateCompensations (ProfileService, apply-time)

Same math as above, used when ProcessWatcher fires and MouseService needs to write hardware values. Must stay in sync with ViewModel auto-sync methods. Both steps use `baseEffectiveSens = GetEffectiveSensitivity(baseProfile)`.

### 2.8 Dedup in MouseService

Hardware writes are skipped when the computed state tuple is unchanged:
```csharp
(int? winSpeed, double? regResult, string? template, double? sens)
```
Dedup happens **after** `CalculateCompensations` runs — so Global Default changes always propagate on next focus event.

---

## 3. PLANNED FEATURE: ENVIRONMENT AWARENESS

> **Status:** Spec only. Not yet implemented.  
> **Implementation:** Two chunks — see Section 5.

### 3.1 The Problem

The current math ensures all profiles have the same **physical multiplier ratio**. But physical feel also depends on:

1. **Display resolution** — more pixels on the same screen = more hand movement needed to cross it  
2. **Windows logical DPI** — Windows internally scales the SmoothMouseCurve output by `logicalDPI / 96` before applying it, so `regResult = 1.0` is only a true MarkC fix at 96 DPI (100% scaling)

Currently `regResult = 1.0` at 150% scaling delivers 1.5× more cursor movement through EPP than intended — the written curve values are calibrated to 96 DPI.

### 3.2 New MouseProfile Fields

```csharp
// All nullable — null means inherit from Global Default
int?  TargetScreenWidth   // horizontal resolution this profile is designed for, e.g. 1920
int?  TargetLogicalDpi    // Windows logical DPI this profile is designed for, e.g. 96, 120, 144
bool? UseEnhancePointerPrecision  // EPP on/off for this profile
```

**Null semantics (consistent with all other fields):**
- Null = inherit from Global Default
- Global Default null = no environment normalization applied (existing behavior preserved — zero regression)

### 3.3 Updated Effective Sensitivity Formula

```
Effective(profile) =
    SensMultiplier
  × WinMult[WinPointerSpeed]
  × EncodeRegCurve(RegistryCurveOverride)
  × (ScreenWidth_base / ScreenWidth_profile)    -- resolution compensation
  × (96.0 / LogicalDpi_profile)                 -- DPI compensation (EPP correction)
```

The two new terms:
- **ScreenWidth ratio is inverted** — a wider screen needs more multiplier to maintain the same cm-per-crossing feel, so base/active normalizes correctly
- **DPI ratio is `96 / profileDPI`** — at 150% (144 DPI), EPP amplifies by 1.5× so we divide by 1.5 to get net 1.0×

When fields are null (inherit from Global Default), both ratios resolve to `1.0` — formula collapses to current behavior.

**True MarkC neutral baseline (EPP active, any DPI):**
```
neutralRegResult = 96.0 / effectiveLogicalDpi
```
e.g. at 125% scaling (120 DPI): `neutralRegResult = 0.8`

### 3.4 Auto-Sync Target Updates

**ApplyAutoOSSync target** (was `Effective(base) / S_target`):
```
target = (Effective(base) × envRatio) / S_target

where envRatio = (ScreenWidth_base / ScreenWidth_active)
              × (LogicalDpi_active / 96.0) / (LogicalDpi_base / 96.0)
             = (ScreenWidth_base / ScreenWidth_active)
              × (LogicalDpi_active / LogicalDpi_base)
```

**ApplyAutoRegSync** solves the remainder after Win Speed is chosen — formula structure unchanged, but `Effective(base)` now includes the env terms from 3.3.

### 3.5 Match Accuracy Row Updates

```
Raw Accel Only  : (curSens / S_base) × 100%
                  — UNCHANGED. This row is always DPI/res independent.

Win Speed Only  : (curSens × WinMult × envRatio) / Effective(base) × 100%
                  — now includes envRatio

Full Match      : (curSens × WinMult × EncodeRegCurve(reg) × envRatio) / Effective(base) × 100%
                  — includes envRatio + DPI correction through EncodeRegCurve
```

Where `envRatio = (ScreenWidth_base / ScreenWidth_active) × (LogicalDpi_base / LogicalDpi_active)`.

Note: when all env fields are null, `envRatio = 1.0` and display is identical to current behavior.

### 3.6 Registry Neutral Baseline

`GetEffectiveSensitivity` uses `EncodeRegCurve(RegistryCurveOverride ?? neutralRegResult)` where:
```csharp
double neutralRegResult = 96.0 / (profile.TargetLogicalDpi ?? baseProfile.TargetLogicalDpi ?? 96);
```

This replaces the current hardcoded `?? 1.0` fallback for `RegistryCurveOverride` when DPI is declared.

### 3.7 EPP Toggle (UseEnhancePointerPrecision)

The SmoothMouseCurve only affects cursor movement when EPP is enabled. The toggle must be applied asymmetrically to avoid a transient bad state:

**Disabling EPP:**
```csharp
// Safe to apply live — goes from "possible acceleration" to "no acceleration"
// Write MouseSpeed=0 to HKCU\Control Panel\Mouse
// Then SPI_SETMOUSE broadcast to apply immediately
```

**Enabling EPP:**
```csharp
// Write registry curve first (so EPP activates into a known-good curve)
// Write MouseSpeed=1 to HKCU\Control Panel\Mouse
// NO SPI broadcast — EPP activates on next session start when curve is also reloaded
// (Prevents transient window where EPP is on but default Windows curve is still loaded)
```

If `UseEnhancePointerPrecision` is null, the profile does not touch EPP state — existing user setting is preserved.

When EPP is explicitly set to false, `RegistryCurveOverride` and the reg curve math become irrelevant for that profile — `GetEffectiveSensitivity` should treat the reg factor as `1.0` regardless of stored value.

---

## 4. GUARD RAILS FOR IMPLEMENTING MODELS

See `PRECISION_CRITICAL_FILES.md` for the full list. Summary of invariants that must never be violated:

1. `EncodeRegCurve` formula must stay identical to `CurveToBinary` in `ApplyRegistryBridge`
2. `WinMultipliers[]` table is fixed — never edit values
3. `AutoPersist()` must always be called **before** `RecalculateMatchAccuracy()` in every callback
4. `_isSyncing` save/restore pattern (`wasSyncing`) must be preserved in `ApplyAutoOSSync`/`ApplyAutoRegSync`
5. `CalculateCompensations` in `ProfileService` must stay in sync with `ApplyAutoOSSync`/`ApplyAutoRegSync` in `ProfileEditorViewModel` — both are apply paths for the same math
6. Dedup in `MouseService` compares computed tuples — never revert to `ReferenceEquals`
7. All null field fallbacks must read from Global Default, not from neutral constants

---

## 5. IMPLEMENTATION CHUNKS

### Chunk 1 — Math Engine + Model (PRECISION CRITICAL)
**Do not combine with Chunk 2. Confirm math outputs before proceeding.**

Files touched:
- `Models/MouseProfile.cs` — add 3 new nullable fields
- `Services/ProfileService.cs` — `GetEffectiveSensitivity` env terms, `CalculateCompensations` env ratio
- `Services/MouseRegistryBridge.cs` — `GetCurrentLogicalDpi()` P/Invoke, EPP read/write helpers, neutral baseline
- `Services/MouseService.cs` — wire `UseEnhancePointerPrecision` into `ApplyProfileAsync`
- `ViewModels/ProfileEditorViewModel.cs` — new `GetBaseScreenWidth()`, `GetBaseLogicalDpi()` helpers; update `ApplyAutoOSSync`, `ApplyAutoRegSync`, `RecalculateMatchAccuracy`

**Confirmation test after Chunk 1:**
- Build passes
- Two profiles with identical settings → Full Match shows 100%
- Profile with `TargetScreenWidth = null` → envRatio = 1.0 → no change from current behavior
- Profile with `TargetLogicalDpi = 144` vs base `96` → Full Match compensates to 100% when regResult is auto-synced

### Chunk 2 — UI Wiring (safe to hand off independently)
**AXAML + ViewModel surface only. Does not touch the math engine.**

Files touched:
- `Views/ProfileEditorView.axaml` — add fields for TargetScreenWidth, TargetLogicalDpi, EPP toggle
- `ViewModels/ProfileEditorViewModel.cs` — new `[ObservableProperty]` fields, string proxies, `On*Changed` callbacks, `LoadProfile` additions, `AutoPersist` additions

Chunk 2 has no precision risk. A separate model can handle it with only `PRECISION_CRITICAL_FILES.md` as a constraint (to confirm it's not touching anything in that list).
