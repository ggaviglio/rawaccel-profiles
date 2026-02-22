# OVERVIEW: Mouse App

## 1. THE PRODUCT VISION
A lightweight utility built on Avalonia UI (MVVM). Monitors processes and swaps mouse sensitivity profiles. It can ensure mouse pointer speed remains consistent across different applications by mathematically balancing the Windows Pointer Speed and Registry-level curve override relative to the Global Default's Raw Accel sensitivity.

## 2. THE GUI STRUCTURE (Master-Detail)
The app uses a clean, integrated Sidebar + Pane layout:

* **Profile Sidebar (Left):** A high-density list of all saved profiles.
    * **The Anchor:** The first profile is always the **Global Default**. It serves as the baseline for all mathematical compensations.
* **Profile Editor (Right):** A vertical, scrollable area for the selected profile's parameters.
    * **Sparse Data Support:** Fields can be left blank to "skip" that specific setting.
    * **Template Management:** A dropdown to select from a library of saved Raw Accel `.json` configurations. Includes quick-actions to import the current `settings.json` into the library or delete existing templates.
    * **OS Sync Controls:** Optional toggles for Windows Pointer Speed and Registry overrides, allowing for automated balancing (applying a compensatory scale factor to the Windows pointer to offset Raw Accel deviations from the Global Default).
    * **Accuracy Info:** Real-time data providing "Proof" of the effective sensitivity match (e.g., displaying the calculated percentage of the mathematical match).    
* **Secondary Windows:**
    * **Target Manager:** A dual-pane utility for managing application associations.
        * **Discovery (Left):** Real-time list of active processes for one-click mapping.
        * **Management (Right):** The current profile's assigned list (Process Name/Path, Class, or Title).
        * **Manual Entry:** A dedicated input field to manually add associations to the list.
    * **Global Settings:** A separate view for system-wide configuration:
            * **Path Configuration:** Define the Raw Accel installation directory and executable paths.
            * **High-Precision Logic:** Opt-in toggle for the **Registry Bridge**. (When enabled, Profile Editor fields for Registry overrides become visible).
            * **Startup & Behavior:** Preferences for "Run at Startup," "Start Minimized," and Global Hotkey assignments.

## 3. CORE LOGIC STACK
**Raw Accel:** Manages the template library. Swaps the selected template into the Raw Accel directory as `settings.json` before overwriting sensitivity value. Applies changes with `writer.exe settings.json`.
**OS Pointer Speed:** Discrete logic (1–20) with a sync-toggle for Auto/Manual modes.
**High-Precision Fine-Tune:** A global opt-in feature. When enabled, it silently overrides the "Enhance Pointer Precision" Registry curve to bridge the gap left by OS integer steps. Registry updated on profile swap, changes only initialize upon Windows login/relog.
**Active Context Monitor:** Track process activity and window states, ensuring the correct profile is applied via Win32 event hooks. Logic prioritizes active focus for profile-to-profile switching, but requires a process-exit event to revert to the Global Default.