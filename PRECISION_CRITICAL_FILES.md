## Precision-Critical Files — Do Not Let Another Model Touch These

### 🔴 Highest Risk — Core Math

**Services/MouseRegistryBridge.cs**
- `EncodeRegCurve()` — the 16.16 round-trip model. Formula is `yBase1 = MarkCXCurve[1] × (150/DPI) / winSpeedMult`. If the anchor, the DPI factor, or the winSpeedMult division changes, every match accuracy display and every compensation calculation silently breaks.
- `ApplyRegistryBridge()` → `CurveToBinary()` — the actual registry write. Formula is `Y[i] = X[i] × (150/DPI) / winSpeedMult × regResult`. Must stay identical in formula to `EncodeRegCurve` or the model stops predicting reality. `winSpeedMult` is read live from the registry immediately before this call (MouseService writes pointer speed first to ensure the live value is already correct for the profile).

**Services/ProfileService.cs**
- `WinMultipliers[]` — the 20-entry table is the single source of truth for all Win Speed math. Wrong value = wrong compensation applied to hardware.
- `GetEffectiveSensitivity()` — uses `EncodeRegCurve`, feeds every comparison. Removing the encode call here is a silent precision regression.
- `CalculateCompensations()` — the apply-time math. Must stay in sync with the ViewModel's `ApplyAutoOSSync`/`ApplyAutoRegSync`.

**ViewModels/ProfileEditorViewModel.cs** — Math Engine section (~lines 305–440)
- `ApplyAutoOSSync()` — Win Speed selection formula. Target is `Effective(base) / S_target`.
- `ApplyAutoRegSync()` — Registry remainder formula. Target is `Effective(base) / (S_target × WinMult)`.
- `RecalculateMatchAccuracy()` — display math. Uses `EncodeRegCurve` on the reg curve value.
- The `On*Changed` callback **ordering** matters: `AutoPersist()` must come before `RecalculateMatchAccuracy()` everywhere. Swapping them causes stale model reads.
- The `_isSyncing` guard + `wasSyncing` save/restore pattern in `ApplyAutoOSSync`/`ApplyAutoRegSync`. Removing any guard causes reentrancy corruption on profile load.

### 🟡 Medium Risk — Correctness Depends On These

**[Services/MouseService.cs](src/MouseApp/Services/MouseService.cs)**
- The dedup check compares computed `(winSpeed, regResult, template, sens)` — not object identity. Reverting to `ReferenceEquals` means Global Default changes won't propagate to already-active profiles.

**Models/MouseProfile.cs**
- Field types for `SensMultiplier` (`double?`), `WinPointerSpeed` (`int?`), `RegistryCurveOverride` (`double?`). If these change type, null-fallback logic throughout the math engine silently misbehaves.