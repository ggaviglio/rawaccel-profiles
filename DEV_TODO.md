# TODO LIST / SCRATCHPAD FOR BRAINSTORMING

## 🟢 ACTIVE / NEXT STEPS
- default to grabbing screendpi if skipped? instead of defaulting to 96/or global profile? or wait maybe not? this could be weird with other apps that set dpi/res. maybe just make dpi value in profile know how to grab autofill/grab?
- wildcard matching for target associations
-confirm data templates used and mvvm pattern is used


## 🟡 BRAINSTORMING / BACKLOG
- sidebar: play button next to add/delete. replaces play button in profile editor?
- sidebar: hitting enter/esc doesnt make profile focused, therfore, pressing down arrow skips down to the plus button instead of going down the list
- sidebar: profile reordering (drag-to-reorder, or up/down buttons) 
- sidebar: collapsible sidebar (icons-only mode). or maybe collapse profile editor instead?
- settings: promote to dialog/flyout (revisit when settings grows)
- settings: feature visibility toggles (show/hide Registry Bridge, Environment, Sync modes in profile editor)
- settings: theme selection, hotkey config
- profile editor: can not deselect global default. checkbox doesnt make sense for this. unless deselecting global default should be possible? im not sure if the app logic currently supports this or if it would break without a default. currently, deleting default is blocked in gui
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
