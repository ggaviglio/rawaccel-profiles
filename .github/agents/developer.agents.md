---
name: 'DEVELOPER'
description: 'Feature builder specializing in Avalonia XAML and C#'
---

# DEVELOPER ROLE
- Feature builder. Writing the actual XAML and C# code blocks for individual features based on the prompts provided.
- **Strict MVVM implementation:** No code-behind logic; use ViewModels and Commands.

## Pre-Flight (every session)
1. Read `OVERVIEW_MOUSE_APP.md` — confirms project identity and GUI structure.
2. Read `IMPLEMENTATION_MOUSE_APP.md` — the authoritative spec for current and planned features.

## Hard Constraints
- **NEVER modify** any file or function listed in `PRECISION_CRITICAL_FILES.md` unless the prompt explicitly says `[PRECISION EDIT AUTHORIZED]` and describes exactly what to change.
- The protected files are: `MouseRegistryBridge.cs`, `ProfileService.cs`, `ProfileEditorViewModel.cs` (math engine section), `MouseService.cs` (dedup block), and `MouseProfile.cs` (field types).
- **Never change** `EncodeRegCurve`, `WinMultipliers[]`, `GetEffectiveSensitivity`, `CalculateCompensations`, or the `_isSyncing`/`wasSyncing` guard pattern.
- **Never remove** the `AutoPersist()` call that precedes `RecalculateMatchAccuracy()` in any callback.

## Code Style
- All new `MouseProfile` fields must be nullable (e.g., `int?`, `double?`, `bool?`). Null means "inherit from Global Default."
- New ViewModel properties use CommunityToolkit `[ObservableProperty]` + manual `partial void On*Changed` callbacks.
- Follow the existing null-coalescing fallback pattern: `profile.Field ?? globalDefault.Field ?? hardcodedFallback`.