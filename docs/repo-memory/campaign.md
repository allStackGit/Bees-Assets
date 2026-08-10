# Campaign

## Sequence and persistence

- Current intended sequence is 0 Anomaly, 1 Reinforcements, 2 Pushback, 3 Bluer Pastures, 4 Seize the Means, 5 Of Production, 6 Pressing Forward, 7 Minesweeper, 8 Beenoculars, then the currently scripted Uranus arc 9-11.
- `SaveData/` is a local/testing snapshot, not authoritative campaign design. `Scripts/Data/campaign_levels.json` is also stale relative to the current catalog. Before end-to-end campaign qualification, verify the actual server-backed `campaign_levels_data` against `CampaignMissionCatalog`/`LevelIntro`.
- The campaign is persistent attrition/resource warfare: human losses persist, Bee losses/veterans persist, resource extraction changes later strength, and retreat can preserve strategic value. Do not test/refactor missions as independent RTS scenarios.
- Some failure branches intentionally advance twice because the first advance skips a conditional mission: Seize the Means failure skips Of Production, and On the Offensive failure/no Factory skips On the Defensive. Do not "fix" these as duplicate progression without checking `CAMPAIGN_DESIGN_GUIDE.md`.
- Player campaign mineral rewards must be collected only from `Configuration.UserSide` squads. AI mining is strategic enemy state and must not be added to `UserProgressData.MinedTSV` or `State.PlayerMineralsReceived`.
- `Level` is reused across campaign missions and retries. Mission-only booleans/counters must therefore be reset explicitly or scoped to the current `LevelOptions` instance. In particular, retreat completion and carrier-intro completion must not leak into a later mission/retry.
- `NextTriggers` is deferred executable state, not harmless bookkeeping. Clear both `Triggers` and `NextTriggers` before configuring a new campaign mission, and reset `HasContinuousTriggers` so the new mission establishes its own trigger policy. A stale trigger can retain references to prior mission UI/entities and fire after reset.
- `SquadShip.Offset`/runtime `Ship.OffsetFromCenter` is the authored saved formation. Runtime level placement must translate those offsets exactly and must not rescale them by ship type. Large/medium spacing belongs in Squad Maker or generated formation templates when offsets are created; applying another size multiplier during `Squad.SetStartingPosition()` distorts custom and mixed formations.

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
- Human staging must preserve each player's saved intra-squad formation exactly. If the mission relocates a squad toward Titania, translate every ship in that squad by one shared delta; never individually restage ships and never call `SetOffsets()` as part of mission staging. Candidate translated formations should be checked per ship for bounds, Titania clearance, authored obstacles, and overlap with already staged squads. If no safe translated position is found, preserve the squad's existing formation and position rather than corrupting its offsets.
- Titania staging and Bee entry-lane physics checks operate on level/map-local coordinates; convert candidate points through `Map.Transform` before using world-space `Physics2D` queries if the Level may be offset from world origin.
- Bee reinforcements should remain visually off-map, but their entry segment must use real openings through the authored obstacle layout.
- Bee target selection remains server/Hive Mind controlled; mission authoring should not add client-only targeting rules.
- The authored “last mission failed” line is still unsupported until a reliable persisted prior-mission outcome exists. Do not infer it from transient `WinningSide`/scene state.

## Mission-source organization

- `LeveLTriggers.cs` is a compatibility stub. Do not put active mission logic back into it.
- Shared campaign behavior/endings live in focused `Level.Campaign.*` partial files; Titania missions keep their dedicated source files.
- New/actively developed mission code should live in mission- or planet-specific `Level` partial files. If a planet file becomes large enough that narrow edits are unsafe, split again by mission rather than recreating a monolith.
- Legacy trigger methods should be migrated only after catalog/external references are checked; obsolete duplicate implementations should be deleted rather than copied into new partials.
- Tooltip-disabled branches must bypass tutorial UI and any pause/unpause callbacks owned by that tutorial. Setting a "tutorial seen" flag is not sufficient if the UI objects are still instantiated or the mission remains paused.
