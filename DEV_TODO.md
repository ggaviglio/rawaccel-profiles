# TODO LIST / SCRATCHPAD FOR BRAINSTORMING

## 🟢 ACTIVE / NEXT STEPS
- wildcard matching for target associations
- **FIX REGISTRY BRIDGE**: Research correct MarkC XCurve + YCurve values per DPI (100%/125%/150%).
  Decode the actual .reg files from the MarkC fix ZIP (donewmouseaccel.blogspot.com).
  Determine YCurve formula so regResult=1.0 = exact MarkC feel, regResult=2.0 = 2× faster.
  Update `MarkCXCurve_TODO` / `MarkCYBase_TODO` in `MouseRegistryBridge.cs` and remove the
  `NotImplementedException` placeholder. See also: `EncodeRegCurve` needs same update.

## 🟡 BRAINSTORMING / BACKLOG
-what is "inherite system setting" for epp?
target manager
- make target manager close if app fully closes.
- only allow one target manager? only one per profile?
- make enter/esc work in target manager

- make it so global default cant be unchecked? hide match percentage
- indicator when profile is active
- cant return to last profile from settings screen, only others

- auto hide certain files or folder from ai?
- improve agent.mds. especially planner, make it more aware of the whole workflow with the different models
- make apply now work, skip dedup check?
- keep active list of all open apps for better performance with long lists? (get app on start, watch launch and exit. only compare active list on focus change?)
- Command line wrapper /src/MouseApp.CLI
- remove admin/elevation features from app (writer.exe runs without admin, all other writes are HKCU)

## ✅ COMPLETED / DELETE
