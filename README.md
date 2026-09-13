# OVERVIEW: Mouse App

## 1. Product Vision

A lightweight, portable Windows utility (Avalonia UI, MVVM) that monitors the active
window and automatically swaps mouse sensitivity profiles between applications —
keeping effective cursor speed consistent even when different games/apps expect
different DPI, resolution, or acceleration settings. Everything lives under `HKCU`
and the app's own folder; no admin rights required.

## 2. How It Works, In One Paragraph

One profile is marked the **Default** — the baseline every other profile's math
compensates against (not necessarily first in the list; profiles can be freely
reordered). Every other profile describes a target environment (Raw Accel template,
optionally a target screen size) and how far it should be reconciled toward the
Default's own effective sensitivity. When the foreground window matches a profile,
MouseApp solves backwards from the Default's effective sensitivity to pick a Raw
Accel sensitivity, a Windows Pointer Speed step, and (optionally) a precise Registry
curve override — so the match is computed, not eyeballed.

## 3. Layout

* **Sidebar (Left):** Saved profiles, with search, inline rename (double-click a
  name), and an action bar — Apply, Set as Default, Clone, Add, Delete, Reorder,
  Settings.
* **Profile Editor (Right):** The selected profile's settings, in cards:
    * **Raw Accel:** template picker (with import/duplicate/rename/delete for the
      template library), Sensitivity, and Y/X Sensitivity Ratio — each with its own
      optional Auto-sync toggle.
    * **Windows Pointer:** Pointer Speed (Auto-sync or manual), and an Enhance
      Pointer Precision override with its own Registry-curve fine-tune.
    * **Auto-Match Environment** *(optional, off by default)*: target screen
      width/height, used only to drive the Auto-sync math and Y/X ratio
      compensation — this app never changes your actual Windows resolution.
    * **App Rules:** which processes/paths/window classes/titles activate
      this profile; hidden for the Default profile.
    * Every field can be left blank to skip/inherit from the Default. Hover any
      field in the app for what it does and its live match-accuracy — the tooltips
      are the field-level reference; this document stays at the map level.
* **App Rules Manager:** Pick from currently-open windows (refresh on demand) or type a
  manual exe/path/class/title match; a "Test" button previews which open windows the
  current rule list would actually match.
* **Global Settings:** Raw Accel install/presets folder paths; startup & tray
  behavior (Run at Startup, Close to Tray, Start in Tray, and re-applying Raw Accel's
  settings.json on launch, since its driver doesn't survive a reboot on its own);
  which Advanced Profile Editor fields are visible; and troubleshooting actions to
  apply the MarkC no-acceleration curve fix system-wide without configuring any
  profile, or reset the mouse curve back to Windows' own default.

## 4. Under The Hood

* **Raw Accel:** Swaps the profile's chosen template into the Raw Accel directory as
  `settings.json`, patches in Sensitivity (and Y/X ratio, for aspect-ratio
  compensation), then applies with `writer.exe settings.json`.
* **Windows Pointer Speed:** The OS's own discrete 1–20 steps, Auto-computed or
  manual per profile.
* **Registry curve bridge:** An optional, more precise correction layer for the gap
  Windows' integer pointer-speed steps can't close on their own. The registry write
  and `WM_SETTINGCHANGE` broadcast happen immediately, but Windows doesn't actually
  re-read the curve until the next logon — so unlike Raw Accel sensitivity and
  Windows Pointer Speed (which do switch live), this layer's correction only takes
  real effect after a log off/on.
* **Foreground watcher:** A Win32 event hook (`SetWinEventHook`) tracks the active
  window and swaps profiles on match; reverts to Default once no window matching the
  active profile remains ("Last Man Standing"), suppressed during system shutdown so
  quitting apps at logoff doesn't spuriously revert.
* **Persistence:** Profiles and settings are plain JSON next to the executable —
  portable, no database, no admin rights.

Formulas, precision edge cases, and anything about the math engine's fragile
internals belong in `CLAUDE.md`, not here — treat that file as the source of truth
before modifying that code.
