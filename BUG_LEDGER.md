# Bees Static Bug Audit Ledger

Branch: `bug-audit/static-2026-08-11`

This ledger records only defects validated by static code tracing. No tests, builds, simulations, executables, or GitHub Actions were run for this audit.

### BUG-001 — Steam identity path is unreachable
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `ConfigData.GetUserId()`  
**Description:** `GetUserId()` always returns the PlayerPrefs/random ID before the Steam initialization branch. Consequently a running Steam build can never use `SteamUser.GetSteamID()`, so server/user-data identity is permanently detached from the player's Steam64 identity.

### BUG-002 — Production socket configuration falls through to development
**Status:** Open  
**Location:** `Scripts/ConfigData.cs`, lazy `ConfigData.Socket` getter  
**Description:** Socket construction is `Test ? test : development`; `Production`, `ProductionServerHostname`, and `ProductionPort` are never selected. A production build made by disabling both Test and Development therefore connects to the development endpoint instead of the production endpoint.

### BUG-003 — Shooting hotkeys for ship types V/W/X cannot bind
**Status:** Open  
**Location:** `Scripts/ConfigData.Gameplay.cs`, `ShootingStrategyNames`; `Scripts/Levels/LevelInputManager.cs`, `LoadHotKeySettings()`  
**Description:** `ShootingStrategyNames` contains one combined string `"Type V, Type W, Type X"` instead of three names. `LoadHotKeySettings()` binds type-specific shooting hotkeys only when the exact hotkey name is in that set, so `Type V`, `Type W`, and `Type X` settings never receive an action.

### BUG-004 — Desktop player save files use the application install directory
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetBasePath()`  
**Description:** In the non-editor, non-mobile branch, mutable save data is placed under `Application.dataPath/SaveData` rather than `Application.persistentDataPath`. When local storage is enabled on a desktop install whose application directory is read-only or replaced during updates, normal save creation/writes can fail or be lost with the installation.

### BUG-005 — Mobile cache path is routed into SaveData
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetCachePath()`  
**Description:** Android/iPhone cache branches build their path with `BaseFolder` (`SaveData`) instead of `CacheFolder` (`SpriteCache`). Sprite-cache files therefore share the persistent save-data directory rather than the cache directory, breaking the intended save/cache storage boundary whenever sprite caching is used on mobile.

### BUG-006 — Runtime Level source has an unconditional UnityEditor dependency
**Status:** Open  
**Location:** `Scripts/Levels/Level.cs`, imports  
**Description:** `Level.cs` again unconditionally imports `UnityEditor` even though it is runtime gameplay code and the file does not require editor-only APIs for ordinary gameplay. This reintroduces a player-build compilation dependency on the editor-only UnityEditor assembly.

### BUG-007 — Random squad generation excludes the configured minimum
**Status:** Open  
**Location:** `Scripts/Scenes/Stage.cs`, generated enemy squad-count override logic; `Scripts/Utilities.cs`, `RandomInt()`  
**Description:** For a positive `GeneratedSquadCountMinimum`, Stage computes `RandomInt(max - min) + 1 + min`. `RandomInt(n)` returns `[0,n)`, so the resulting range is `[min + 1, max]`; the configured minimum is never generated. Training/runtime distributions therefore omit the smallest intended battle size.

### BUG-008 — Failed basic write responses are treated as completed writes
**Status:** Open  
**Location:** `Scripts/Server/Socket.cs`, `HandleBasicResponse()`; `Scripts/Data/DataFile.cs`, `WriteData()`; `Scripts/Levels/GameState.Commands.cs`, `StoreCommands()`  
**Description:** `HandleBasicResponse()` removes a standing request without checking `ServerResponse.Status`. `StoreUserData` and `StoreCommands` failures are therefore retired exactly like successes if a non-success basic response is received. `DataFile.WriteData()` already marks the new value loaded, and `GameState.StoreCommands()` clears stored outcomes immediately after sending, so such a response can permanently lose player persistence or Hive Mind reward data instead of remaining retryable. Current BeesServer normally leaves failed database writes pending rather than emitting a non-success basic response, so this client defect is dormant under that server behavior but remains unguarded.

### BUG-009 — Global handled-response hashes grow without a bounded lifetime
**Status:** Open  
**Location:** `Scripts/Server/Socket.cs`, `TryClaimResponse()` / `HandleBasicResponse()` and response dispatch  
**Description:** Every response hash is inserted into the socket-global `HandledRequests` set by `TryClaimResponse()`. Level-owned strategy/setup responses have level ownership for later cleanup, but basic responses such as repeated `StoreCommands`/`StoreUserData` do not. Those hashes therefore accumulate for the process lifetime, producing unbounded memory growth in long-running/training sessions while also retaining stale dedupe identities indefinitely.

### BUG-010 — Campaign completion is persisted as a non-atomic three-file checkpoint
**Status:** Open  
**Location:** `Scripts/Levels/Level.Campaign.Endings.cs`, `SaveCampaignProgress()`  
**Description:** Campaign endings mutate progression and fleet/squad state, then `SaveCampaignProgress()` independently sends `UserProgressData.Save()`, `SaveSquadData()`, and `SaveFleetData()`. These are separate asynchronous persistence requests with no shared checkpoint/version/transaction, so interruption or partial server success can persist an advanced campaign level while retaining an older fleet or squad snapshot, leaving the next session in a state that never existed in memory.

### BUG-011 — Steam first-run flag is inverted
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetUserId()`, `HasPlayedBefore()`, and `SetupUserData()`  
**Description:** The Steam branch assigns `FirstTimePlaying = HasPlayedBefore()`, while `HasPlayedBefore()` returns true for an existing player and `SetupUserData()` passes `!FirstTimePlaying` as `shouldFileExist` to progress/fleet/squad/settings loaders. If the unreachable Steam branch in BUG-001 is enabled as written, returning players are therefore treated as first-time users and their existing files are not expected/loaded normally, while genuinely new Steam players are treated as though files should already exist. The assignment must be the inverse of `HasPlayedBefore()` (or the surrounding semantics renamed/reworked) before Steam identity can be safely activated.

### BUG-012 — Static scene registry retains every destroyed scene
**Status:** Open  
**Location:** `Scripts/ConfigData.cs`, `ConfigData.Scenes`; `Scripts/Scenes/Scene.cs`, `Start()`  
**Description:** Every `Scene` instance adds itself to the process-wide static `ConfigData.Scenes` list in `Start()`, but there is no corresponding removal anywhere in the repository. Unity destroys scene objects during normal scene changes, yet the static list keeps their managed `MonoBehaviour` wrappers strongly referenced. Repeated Main Menu/Squad Maker/Space/intro transitions therefore grow this registry for the lifetime of the process and retain obsolete scene wrappers (and their managed field graph) instead of allowing collection.

### BUG-013 — Hive Mind type-target bans use the player's unlock set instead of the strategy catalog
**Status:** Open  
**Location:** `Scripts/Data/UserProgressData.cs`, `AllShipTypes` initialization / `SetShipTypes()`; `Scripts/Levels/Squad.Commands.cs`, `MakeMatchupStrat()`; `Scripts/Levels/Commands/MatchupStrategy.cs`, `SortSquads()`  
**Description:** `AllShipTypes` is initialized only from the player's currently visible ship types plus Beacon/Drone/Striker, and `SetShipTypes()` does not rebuild it as more types become visible. `MakeMatchupStrat()` incorrectly treats that incomplete set as the universe from which absent enemy types should be banned. Type-specific strategies for omitted catalog types are therefore left eligible even when no enemy ship of that type exists. If the server selects one, `MatchupStrategy.SortSquads()` sorts every candidate with a zero count and returns the first visible squad, so the recorded/rewarded type-specific policy actually executes an arbitrary-target policy. This both produces incorrect behavior and contaminates Hive Mind targeting learning/outcome attribution.

### BUG-014 — Collision-asteroid shards are returned to the full-asteroid pool
**Status:** Open  
**Location:** `Scripts/Entities/CollisionAsteroid.cs`, `SpawnBreakAwayAsteroids()` / `Kill()`; `Scripts/Levels/GameState.Registry.cs`, `Release()`; `Scripts/Levels/Pool.cs`, collision-asteroid pools  
**Description:** Large asteroid destruction obtains breakaway shards from `CollisionAsteroidShardPool`, but shard deaths enter the same `AsteroidsToRelease` list as full asteroids. `GameState.Release()` returns every item in that list through `ReturnCollisionAsteroidToPool()` instead of routing shard instances back through `ReturnCollisionAsteroidShardToPool()`. After a shard dies, the full-asteroid pool can therefore serve a shard prefab as a normal spawned asteroid, while the shard pool permanently loses that instance and creates replacements, corrupting asteroid type/size distribution and pool ownership over time.

### BUG-015 — Losing Titania 2 advances and saves the campaign as if the mission was won
**Status:** Open  
**Location:** `Scripts/Levels/Titania2Beenoculars.cs`, `Titania2BeenocularsCampaign()`, `ResolveTitania2()`, `Titania2CampaignEnding()`; `Scripts/UI Components/CutsceneManager.cs`; `Scripts/UI Components/DialogueManager.cs`  
**Description:** `Titania2BeenocularsCampaign()` registers `Titania2CampaignEnding` as the cutscene manager's end-dialogue action. `ResolveTitania2()` then calls `PlayDialogueSection(..., true)` for both the success and failure dialogue. The `true` flag makes `DialogueManager` call `CutsceneManager.EndDialogue()` when that dialogue completes, and `EndDialogue()` invokes the registered end action. Consequently a failed mission (Titania destroyed or the player's side killed) still executes `Titania2CampaignEnding()`, which adds `State.PlayerScore`, calls `AdvanceToNextLevel()`, saves progress/squads/fleet, sets `GameOver`, and shows the level summary. A loss therefore permanently advances the campaign to Uranus instead of leaving Titania 2 incomplete.

### BUG-016 — Losing Neptune 1 advances the campaign twice and skips Neptune 2
**Status:** Open  
**Location:** `Scripts/Levels/Level.Campaign.Endings.cs`, `Neptune1Ending()`  
**Description:** `Neptune1Ending()` calls `AdvanceToNextLevel()` inside the non-user-win branch, then calls `AdvanceToNextLevel()` again unconditionally near the end of the method. When the AI wins, persisted campaign level 4 therefore advances to 6 instead of 5, skipping Neptune 2 (`Of Production`) entirely. The same ending invocation also proceeds to save this doubly advanced state.

### BUG-017 — Uranus 1 can advance twice and skip Uranus 2
**Status:** Open  
**Location:** `Scripts/Levels/Level.Campaign.Endings.cs`, `Uranus1Ending()`; `Scripts/Levels/Level.Campaign.Uranus1.cs`; `Scripts/Levels/Level.Campaign.Endings.cs`, `Neptune3Ending()`  
**Description:** `Uranus1Ending()` conditionally calls `AdvanceToNextLevel()` when the AI wins or when the player has no live Factory, then calls `AdvanceToNextLevel()` again unconditionally. Either condition therefore advances persisted mission 9 to mission 11, skipping Uranus 2. The no-Factory path is concretely reachable because `Neptune3Ending()` marks every Factory in the fleet dead when that mission ends with the AI as winner, yet still advances the campaign to Uranus and unlocks the Carrier; `HasShipsOfType(Factory)` later filters to alive fleet ships and is false.

### BUG-018 — An initial socket failure can remain permanently unretried
**Status:** Open  
**Location:** `Scripts/Scenes/Scene.cs`, `Update()` / `AutomaticConnectionRetry()`; `Scripts/Server/Socket.cs`, `Error()` / `Close()`  
**Description:** Automatic reconnect only runs while `Socket.HasClosed` is true, and `AutomaticConnectionRetry()` independently returns when `HasClosed` is false. However an initial transport failure can report `OnError` without subsequently reporting `OnClose`; `Socket.Error()` only logs the error and leaves `HasClosed` false. In that reachable state the socket is not open, no retry timer runs, and the retry callback would refuse to reconnect even if called, leaving startup permanently disconnected until manual intervention or restart.

### BUG-019 — Campaign and challenge mission selection depends on persisted row order
**Status:** Open  
**Location:** `Scripts/Data/LevelData.cs`, `GetLevels()`; `Scripts/Scenes/SquadMaker.cs`, `SetupForCampaign()`, `SetupForChallengeMode()`, `LoadLevel()`  
**Description:** `LevelData` appends levels in the order received from persisted/server JSON and `GetLevels()` returns that unsorted list. Squad Maker then builds `_levelOptionIndexesToLevels` sequentially from that list but calls `LoadLevel()` with the persisted mission ID as though it were the list index. If rows arrive out of ID order, campaign/challenge mission ID N selects whichever level happened to be the Nth row, giving the wrong map, supply capacity, enemy/options data, and selected mission. A sparse/misaligned set of IDs can instead leave no dictionary entry for the current ID and fail lookup entirely.

### BUG-020 — Normal Squad Maker launch clears the prepared level handoff before Space loads
**Status:** Open  
**Location:** `Scripts/Scenes/SquadMaker.cs`, `ProcessStartingLevel()`; `Scripts/Scenes/SquadMakerPersistence.cs`, `HandleSceneUnloaded()`; `Scripts/Levels/Level.cs`, `SetupLevel()`  
**Description:** For campaign/challenge launch from the first Squad Maker side, `ProcessStartingLevel()` clones `_chosenLevel` into `ConfigData.LevelOptions`, sets `IsUserLoadingCustomEnemySquads = true`, adds the player's chosen squads, and then leaves Squad Maker for Space. `SquadMakerPersistence.HandleSceneUnloaded()` runs during that normal transition and, because the side is still the first side and the custom-enemy flag is true, immediately clears both the flag and `ConfigData.LevelOptions`. `Level.SetupLevel()` consequently sees no prepared level and constructs a new generated `LevelOptions` instead of launching the selected campaign/challenge mission, discarding its authored map/enemy/options configuration and chosen-level identity.

### BUG-021 — Campaign can be invoked before Main Menu server data is ready
**Status:** Open  
**Location:** `Scenes/Main Menu.unity`, Campaign button binding; `Scripts/Scenes/Scene.cs`, asynchronous finalization flow; `Scripts/Scenes/MainMenu.cs`, `ConfirmPlayCampaign()` / `PlayCampaign()`  
**Description:** The Main Menu scene exposes an active Campaign button whose click binding invokes `ConfirmPlayCampaign()` immediately, while `Scene.Update()` loads settings/user data asynchronously and only later calls `FinalizeSceneWithUserData()`. The audit snapshot has no gate tying that button to `IsFinalized`/`AreAllSettingsLoaded`/`IsAllUserDataLoaded`. A user who clicks Campaign during this startup window reaches `ConfirmPlayCampaign()`/`PlayCampaign()` before `UserProgressData`, `CampaignShips`, or campaign level data are guaranteed to exist, causing null-dependent campaign startup logic or an invalid partially initialized handoff instead of waiting for the authoritative Main Menu readiness boundary.

### BUG-022 — Squad starting formations can place ships inside obstacles
**Status:** Open  
**Location:** `Scripts/Levels/LevelConstructor.cs`, `PositionSquads()`; `Scripts/Levels/Squad.cs`, `SetStartingPosition()`  
**Description:** Initial and reinforcement placement computes a center from map bounds and then `SetStartingPosition()` blindly applies every ship's formation offset (including enlarged offsets for Queen/Bumblebee/wide ships). Neither method checks the occupied destination for each ship against the pathfinder/obstacle grid. On levels with obstacles, a center that is itself usable can therefore place one or more offset formation members directly into blocked space; the squad begins intersecting an obstacle before any normal pathfinding command can correct it.

### BUG-023 — Squad combat source does not compile because Vector2 is unresolved
**Status:** Open  
**Location:** `Scripts/Levels/Squad.Combat.cs`, `GetPotentialEnemies()` / `GetPotentialAllies()`  
**Description:** `Squad.Combat.cs` declares local `Vector2` values but imports only project ship types plus `System`, `System.Collections.Generic`, and `System.Linq`; it does not import `UnityEngine` or qualify `Vector2`. Because `Vector2` is a UnityEngine type, this source fails compilation with CS0246 until `using UnityEngine;` is restored.

### BUG-024 — EditMode squad-pool test directly references an unavailable runtime assembly
**Status:** Open  
**Location:** `Tests/EditMode/SquadPoolRoleResetTests.cs`, imports / fields / `SetUp()` / test body  
**Description:** `SquadPoolRoleResetTests` imports `Assets.Scripts.Levels` and directly types fields and calls against `GameState` and `Squad`. The EditMode test assembly does not reference the generated runtime `Assembly-CSharp` assembly that owns those types, so the test source cannot resolve the namespace/types and prevents the EditMode test assembly from compiling. The later main fix removes the compile-time dependency and accesses the runtime types through the repository's `RuntimeAssembly` reflection helper.