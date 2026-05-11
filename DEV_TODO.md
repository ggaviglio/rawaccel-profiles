# TODO LIST / SCRATCHPAD FOR BRAINSTORMING

## 🟢 ACTIVE / NEXT STEPS
- default to grabbing screendpi if skipped? instead of defaulting to 96/or global profile? or wait maybe not? this could be weird with other apps that set dpi/res. maybe just make dpi value in profile know how to grab autofill/grab?
- wildcard matching for target associations
-confirm data templates used and mvvm pattern is used


## 🟡 BRAINSTORMING / BACKLOG
- make sure it is clear with some description how the matching works and how registry override requires EPP
- hiding the auto button can cause the windows pointer sense to get stuck greyed out (is now default profile but cant change pointer speed)
- profile: no way to deselect raw accel template
- lookup raw accel sense decimal precision and enforce in gui (might already be used in app?)
- make sure "manual" mode is used for default when "auto" option is hidden, even if it was selected before being made the default
- sidebar: buttons with icons get uncentered when changing windows theme. button in profile editor doesnt do that. this seems like an old leftover fix that shouldnt be there or something.
- profileeditor: button has centering bug. jsut one of them though? make buttons uniform size?
- sidebar: hitting enter/esc doesnt make profile focused, therfore, pressing down arrow skips down to the plus button instead of going down the list
- sidebar: profile reordering (drag-to-reorder, or up/down buttons) 
- sidebar: collapsible sidebar (icons-only mode). or maybe collapse profile editor instead?
- settings: promote to dialog/flyout (revisit when settings grows)
- settings: feature visibility toggles (show/hide Registry Bridge, Environment, Sync modes in profile editor)
- settings: theme selection, hotkey config

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
