# Campaign

## Sequence and persistence

- Current intended sequence is 0 Anomaly, 1 Reinforcements, 2 Pushback, 3 Bluer Pastures, 4 Seize the Means, 5 Of Production, 6 Pressing Forward, 7 Minesweeper, 8 Beenoculars, then the currently scripted Uranus arc 9-11.
- `SaveData/` is a local/testing snapshot, not authoritative campaign design. `Scripts/Data/campaign_levels.json` is also stale relative to the current catalog. Before end-to-end campaign qualification, verify the actual server-backed `campaign_levels_data` against `CampaignMissionCatalog`/`LevelIntro`.
- The campaign is persistent attrition/resource warfare: human losses persist, Bee losses/veterans persist, resource extraction changes later strength, and retreat can preserve strategic value. Do not test/refactor missions as independent RTS scenarios.

## Minesweeper

- Authored battlefield: `Resources/Obstacles/Minesweeper.prefab`.
- It contains 45 authored obstacles and 30 Fire Tanks; all 30 tanks serialize a `TargetObstacle`. Not every obstacle is therefore tank-removable.
- Fire Tanks are interactive `MapObject`s discovered through weapon range/proximity visibility. Their intended teaching flow is discovery dialogue followed by dialogue after the first controlled demolition.
- Fire Tank health is 250; explosion power is 350 and can damage either side. Destruction opens the linked obstacle and dirties pathfinding, so this is a route-selection/controlled-demolition mission rather than merely an avoidance fight.
- Static obstacle geometry destroyed during play must deregister from `ObstacleMap.Obstacles` before Unity destroys its GameObject, or level teardown can later dereference a destroyed wrapper.
- Ship clearance materially changes viable routes; inspect authored geometry rather than inferring routes from trigger code.

## Beenoculars

- Authored battlefield: `Resources/Obstacles/Bee-noculars.prefab`. Its long wall segments form lanes/pockets; it is not an open arena.
- Core objective is a prolonged multidirectional defense of an immobile Titania target. Yellow Jackets are useful interception pressure because they are expendable contact bombers.
- Human staging should be centered on the Titania objective with per-ship obstacle/spacing clearance, not by translating a wide fleet formation to a persisted map anchor.
- Bee reinforcements should remain visually off-map, but their entry segment must use real openings through the authored obstacle layout.
- Bee target selection remains server/Hive Mind controlled; mission authoring should not add client-only targeting rules.
- The authored “last mission failed” line is still unsupported until a reliable persisted prior-mission outcome exists. Do not infer it from transient `WinningSide`/scene state.

## Mission-source organization

- New/actively developed mission code should live in mission- or planet-specific `Level` partial files rather than expanding `LeveLTriggers.cs` further.
- Legacy trigger methods in the monolith should be migrated one mission/planet at a time, with catalog routing switched to the focused implementation before legacy deletion.
