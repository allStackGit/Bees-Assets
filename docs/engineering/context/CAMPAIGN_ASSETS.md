# Campaign / Maps / Assets Route

Load this route only when a focused campaign or asset task needs contract/background beyond the exact mission/map/prefab/resource and focused tests.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| campaign mission IDs / triggers / progression | `CampaignMissionCatalog`, `LevelIntro`, exact mission trigger/objective code | persisted runtime level data; setup/terminal paths; persistence/dialogue only when actually affected |
| maps / prefabs / Resources / normalization | exact asset plus consuming code | serialized names/GUIDs, map/config names, `.meta` identity; campaign assets are gameplay |
| Pluto IV / Titania II shield clock / Game Speed | exact mission code and `GameHudLayoutGuard` | shared shield/timer HUD; validate both missions when shared code changes |

For behavior/persistence meaning changes, consult `PROJECT_CONSTITUTION.md` and the relevant invariant/owner section. Do not load unrelated campaign history for a single asset correction.
