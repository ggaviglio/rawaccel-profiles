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
The `Profile` model represents the configuration state for a specific context. 

**MANDATE: Sparse Data Support**
To support the "Sparse Data" requirement (where fields can be left blank to "skip" a setting), all configurable parameters *must* utilize nullable types.

**`Profile` Class Properties:**
*   `ProfileName` (`string`): The unique identifier for the profile.
*   `RawAccelTemplatePath` (`string?`): Path to the base `.json` configuration in the template library.
*   `SensMultiplier` (`double?`): The UI scaling factor applied to the Raw Accel sensitivity.
*   `WinPointerSpeed` (`int?`): Discrete Windows OS pointer speed (Range: 1–20).
*   `IsGlobalDefault` (`bool`): Flag indicating if this profile is the Anchor/Baseline. (Only one profile may have this set to `true`).
*   `TargetProcessName` (`string?`): The primary executable name used by the watcher to trigger the profile swap. *(Note: May be expanded to `List<string>?` or a dedicated `Target` object in future iterations to support the dual-pane Target Manager's multi-association requirements).*

## 3. THE MATH ANCHOR (Scaling Logic)
The application ensures consistent Effective DPI across different applications by mathematically balancing OS-level and Driver-level sensitivities.

*   **Base Baseline:** The profile where `IsGlobalDefault == true`. All mathematical compensations are calculated relative to this profile's settings.
*   **Logical Invariant (The Inverse Scale Factor):** 
    *   Let $S_{base}$ be the Global Default Raw Accel sensitivity.
    *   Let $S_{target}$ be the active profile's Raw Accel sensitivity.
    *   Let $x = S_{target} / S_{base}$ (The factor of sensitivity increase/decrease).
    *   **Rule:** If Raw Accel sensitivity increases by factor $x$, the Windows Pointer Speed (and corresponding Registry-level curve override) *must* decrease by a factor of $1/x$ (snapped to the closest available registry step) to maintain a consistent Effective DPI.

## 4. SERVICE DEFINITIONS (Interfaces Only)
To maintain strict MVVM separation and testability, core logic is abstracted into the following interfaces:

*   **`IMouseService`**
    *   *Intent:* Handles all direct manipulation of mouse parameters. Responsible for swapping Raw Accel JSON files, executing `writer.exe`, and updating the Windows Registry for high-precision curve overrides and OS Pointer Speed.
*   **`IProcessWatcher`**
    *   *Intent:* Monitors active context. Utilizes Win32 Hooks (`SetWinEventHook`) to detect foreground window focus changes. Prioritizes active focus for profile-to-profile switching and listens for process-exit events to revert to the Global Default.
*   **`IProfileManager`**
    *   *Intent:* Handles CRUD (Create, Read, Update, Delete) operations for the application's profile storage (e.g., reading/writing the master `profiles.json` data store) and manages the Raw Accel template library.