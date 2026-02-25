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

---

## Pending Task — Remove Admin Status from Settings UI

### What to remove

**`src/MouseApp/Views/SettingsView.axaml`** — delete these two elements:

1. `<TextBlock Text="{Binding AdminStatus}" />`
2. The "Refresh Admin Status" `<Button>` inside the bottom `<StackPanel>`. After removal the row should contain only the Save button — collapse the wrapping `<StackPanel Orientation="Horizontal" Spacing="8">` to a bare `<Button Content="Save" Command="{Binding SaveCommand}" />`.

**`src/MouseApp/ViewModels/SettingsViewModel.cs`** — delete all of the following:

- `RefreshAdminStatus();` call in the constructor
- `RefreshAdminStatusCommand = new RelayCommand(...)` assignment in the constructor
- `public bool IsAdmin { get; private set; }` property
- `public string AdminStatus => ...` computed property
- `public IRelayCommand RefreshAdminStatusCommand { get; } = new RelayCommand(() => { });` declaration
- `private Task RefreshAdminStatusAsync()` method + body
- `private void RefreshAdminStatus()` method + body
- Any `using` directives that become unused as a result (check `System.Security.Principal`)

### What NOT to touch
- `MouseService.cs` has its own `IsRunningAsAdministrator()` static helper and `RestartElevated()` — **do not touch those**.
- All other settings rows (directory paths, registry bridge toggle, run at startup) stay exactly as-is.

### Verification
`dotnet build src/MouseApp/MouseApp.csproj -c Debug` — 0 errors. The 8 pre-existing CA1416 warnings in `MouseRegistryBridge.cs` are acceptable.
