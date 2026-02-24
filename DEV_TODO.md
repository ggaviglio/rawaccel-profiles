# TODO LIST / SCRATCHPAD FOR BRAINSTORMING

## 🟢 ACTIVE / NEXT STEPS

### Environment Awareness + EPP (spec in IMPLEMENTATION_MOUSE_APP.md §3)
See IMPLEMENTATION_MOUSE_APP.md for full math spec and guard rails before touching any of these.

**Chunk 1 — Math engine (PRECISION CRITICAL, confirm before Chunk 2):**
- [ ] `MouseProfile`: add `TargetScreenWidth: int?`, `TargetLogicalDpi: int?`, `UseEnhancePointerPrecision: bool?`
- [ ] `MouseRegistryBridge`: add `GetCurrentLogicalDpi()` P/Invoke, EPP read/write helpers
- [ ] `ProfileService.GetEffectiveSensitivity`: add screen width + DPI compensation terms
- [ ] `ProfileService.CalculateCompensations`: add envRatio to Step 1
- [ ] `ProfileEditorViewModel`: add `GetBaseScreenWidth()`, `GetBaseLogicalDpi()` helpers; update `ApplyAutoOSSync`, `ApplyAutoRegSync`, `RecalculateMatchAccuracy`
- [ ] `MouseService.ApplyProfileAsync`: wire `UseEnhancePointerPrecision` (asymmetric enable/disable — see spec §3.7)

**Chunk 2 — UI wiring (safe to hand off, no precision risk):**
- [ ] `ProfileEditorView.axaml`: add TargetScreenWidth, TargetLogicalDpi, EPP toggle fields
- [ ] `ProfileEditorViewModel`: new ObservableProperty fields + callbacks + LoadProfile + AutoPersist

### Other active
- add PRECISION_CRITICAL_FILES.md reference to copilot-instructions.md / agent prompts
- wildcard matching for target associations

## 🟡 BRAINSTORMING / BACKLOG
target manager
- make target manager close if app fully closes.
- only allow one target manager? only one per profile?
- make enter/esc work in target manager

- make it so global default cant be unchecked? hide match percentage
- indicator when profile is active
- cant return to last profile from settings screen, only others

- auto hide certain files or folder from ai?
- improve agent.mds. especially planner, make it more aware of the whole workflow with the different models
- keep active list of all open apps for better performance with long lists? (get app on start, watch launch and exit. only compare active list on focus change?)
- Command line wrapper /src/MouseApp.CLI

## ✅ COMPLETED / DELETE
