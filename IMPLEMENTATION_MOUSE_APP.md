# IMPLEMENTATION.md

## 1. DIRECTORY MAP (Workspace Skeleton)
The project utilizes a nested `/src` structure to isolate core logic from project metadata and build artifacts.

### Root Level
- **`/`**: Contains `.sln`, `.gitignore`, and project documentation (`IMPLEMENTATION.md`, etc.).
- **`/Scripts`**: Non-C# automation or maintenance scripts (PowerShell, Python).
- **`/src`**: Primary source code container.

### Application Core (`/src/MouseApp/`)
The main project strictly adheres to the Avalonia MVVM pattern:
- **`/Assets`**: Icons, logos, and default Raw Accel JSON templates.
- **`/Helpers`**: Static utility classes, math converters, and Win32 P/Invoke signatures.
- **`/Models`**: Pure data structures (e.g., `Profile.cs`).
- **`/Services`**: Implementation of core logic and OS-level interactions.
- **`/ViewModels`**: Presentation logic and state management.
- **`/Views`**: XAML files and strictly empty code-behinds.

## 2. CORE DATA MODEL (The Profile)
The `Profile` model represents the configuration state for a specific context. It serves as the primary data source for the Profile Editor and the input for the Scaling Logic.

### Mandate: Sparse Data Support
To support "skipping" settings, all configurable parameters—excluding the primary identifier and the anchor flags—**must** utilize nullable types.

### `Profile` Class Properties

| Property | Type | Nullable | Intent / UI Mapping |
| :--- | :--- | :--- | :--- |
| **ProfileName** | `string` | No | Primary display name and ID. |
| **RawAccelTemplate** | `string?` | Yes | Filename/Path of the `.json` in the Template Library. |
| **SensMultiplier** | `double?` | Yes | The user-defined scaling factor for Raw Accel sensitivity. |
| **UseAutoOSSync** | `bool` | No | **Toggle:** Auto-calculates `WinPointerSpeed`. |
| **WinPointerSpeed** | `int?` | Yes | Manual OS speed (1–20) used if `UseAutoOSSync` is false. |
| **UseAutoRegSync** | `bool` | No | **Toggle:** Auto-calculates `RegistryCurveOverride`. |
| **RegistryCurveOverride**| `double?` | Yes | The numerical value for high-precision fine-tuning. |
| **IsGlobalDefault** | `bool` | No | **The Anchor:** Used as base for all relative math. |
| **TargetExecutables** | `List<string>` | No | Collection of process names (e.g., ["game.exe"]). |

## 3. THE MATH ANCHOR (Scaling Logic)
The application ensures consistent Effective DPI by mathematically balancing OS-level and Driver-level sensitivities.

* **Base Baseline:** The profile where `IsGlobalDefault == true`. All compensations are relative to this profile.
* **The Inverse Scale Factor ($1/x$):**
    * Let $S_{base}$ be the Global Default Raw Accel sensitivity.
    * Let $S_{target}$ be the active profile's Raw Accel sensitivity.
    * $x = S_{target} / S_{base}$
* **The Compensation Rule:** 1. **Coarse Adjustment:** If `UseAutoOSSync` is true, the `WinPointerSpeed` is set to the integer (1-20) that gets closest to the $1/x$ target.
    2. **Fine Adjustment:** If `UseAutoRegSync` is true, the `RegistryCurveOverride` (double) is calculated to cover the exact remainder between the snapped Windows speed and the true $1/x$ target.
    * **Result:** Total Multiplier ($W_{speed} \times R_{curve}$) perfectly equals $1/x$.

## 4. SERVICE DEFINITIONS (Interfaces Only)

* **`IMouseService`**
    * **Intent:** Orchestrates hardware/OS parameter manipulation.
    * **Elevation Policy:**
        * **Startup/Toggle:** Verify Admin rights if Registry Bridge is enabled; trigger UAC self-elevation if missing.
        * **Goal:** Ensure profile transitions remain "Silent" without mid-session UAC prompts.
    * **Raw Accel:** Manages template swapping and executes `writer.exe` for instant driver updates.
    * **OS Sync:** Applies `winSpeedResult` (1-20) to the registry and invokes `SystemParametersInfo(SPI_SETMOUSE)` for real-time application.
    * **Registry Bridge (Global Opt-in):** * **Implementation:** Integrates `MouseRegistryBridge` to scale the `SmoothMouseYCurve` using 16.16 fixed-point math.
        * **Behavior:** Writes 64-bit Little-Endian binary values to `HKCU\Control Panel\Mouse` based on the `regResult` remainder.

* **`IProcessWatcher`**
    * **Intent:** Efficiently monitors active context via Win32 `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`).
    * **Logic:** Maps focused window properties (Process Name/Title) to profile associations; triggers the "Apply" sequence on `IProfileManager`.

* **`IProfileManager`** (implemented via `ProfileService`)
    * **Intent:** CRUD for `profiles.json` and calculation coordination.
    * **Logic:** Executes `CalculateCompensations` to derive `winSpeedResult` and `regResult` relative to the `IsGlobalDefault` anchor.