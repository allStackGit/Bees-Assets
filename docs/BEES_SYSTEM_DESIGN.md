# Bees system design and architectural reference

Status: source-derived architectural review, 2026-08-02  
Scope: the Unity project under `R:\Bees` plus the external server implementation at `F:\RLDemo\BeesServer\siServerDev.js`  
Unity version: `6000.5.4f1` (`ProjectSettings/ProjectVersion.txt`)

## How to read this document

This document reconstructs behavior from code, serialized scenes/prefabs, JSON data, and the server implementation. Evidence labels are used where intent and implementation diverge:

- **Confirmed** means the named code or serialized asset directly implements the behavior.
- **Inference** means the interpretation best explains several confirmed facts but is not stated as a contract.
- **Question** identifies a decision that cannot be recovered confidently from the repository.
- **Confirmed defect** is reserved for a concrete contradictory or invalid implementation path, not merely risky design.

Paths are relative to `R:\Bees` unless an absolute path is shown; shortened script paths are relative to `Assets/Scripts` when the surrounding section makes that clear. This is a structural and source review; no runtime play-through, Unity build, server execution, or database inspection was performed. The project has no first-party test assembly or discoverable `[Test]`/`[UnityTest]` methods. `Assets/Scripts/Data/LevelData.cs`, `UserData.cs`, `UI Components/SettingsMenu.cs`, and `Entities/Ships/Ship.cs` import NUnit namespaces, but they do not contain tests.

## 1. Executive overview

### What the game is

**Confirmed.** Bees is a real-time 2D fleet tactics game fought between bee-themed and human-themed space fleets. A player builds persistent squads from a fleet, deploys them into a level, selects squads, gives movement/combat orders, chooses targeting rules, and fights an opposing side. Evidence is concentrated in:

- `Assets/Scripts/Scenes/SquadMaker.cs`: fleet/squad construction, formations, supply, opposing-force and level options.
- `Assets/Scripts/Scenes/Stage.cs`: runtime scene root, camera, player control, UI, audio, pool, and one or more simultaneous levels.
- `Assets/Scripts/Levels/Level.cs`: battle setup, environment, timers, victory, reset, saving, and cleanup.
- `Assets/Scripts/Levels/Squad.cs`: formation movement, user commands, Hive Mind requests, and squad-level targeting.
- `Assets/Scripts/Entities/Ships/Ship.cs`: movement, pathfinding handoff, combat state, damage, death, statistics, and rendering.
- `Assets/Scripts/ConfigData.cs`: factions, ship/weapon/projectile/command types, game modes, server configuration, and global data handles.

### Objective and core loop

**Confirmed for FreePlay, Challenge, FishTank, and campaign test battles.** The ordinary battle objective is to render the opposing side unable to continue: `GameState.IsSideKilled(side)` is true when the side has no ships or no mobile ships. When the last squad is removed, `Squad.Kill()` sets `GameState.GameOver` outside a normal campaign mission. `Level.LevelOver()` then decides the winner, finalizes commands, pauses the level, records progress/statistics, and schedules cleanup.

**Confirmed for Campaign.** Campaign objectives are not reducible to simple elimination. `Assets/Scripts/Levels/LeveLTriggers.cs` selects one of twelve mission scripts by `LevelOptions.Id` (0–11). Those scripts can spawn forces, gate control, run dialogue/cutscenes, award quest progress, detect zones or named-object deaths, and explicitly set `WinningSide` and `GameState.GameOver`. The campaign therefore uses scripted mission state as the authoritative objective layer.

The player-facing loop is:

1. Load settings and all user data from local JSON or the server.
2. Choose a mode and faction in `MainMenu`.
3. Build/select squads and level options in `SquadMaker`.
4. Load `Space` (or a specialized stage scene), construct the map and both forces, and establish a server-side game context.
5. Select squads; move, patrol, guard, hold, chase, retreat, heal, mine, manually fire, or select a shooting strategy.
6. Fight while the opposing AI receives remote Hive Mind strategies, or watch AI-versus-AI in FishTank/training configurations.
7. End by mission condition or side elimination; persist fleet/squad/progress data and Hive Mind command outcomes; return to squad selection, advance campaign/challenge, or restart another simulation.

### Major entities and resources

- **Stage**: a scene-level runtime host and shared-service owner.
- **Level**: one arena with one map and isolated mutable battle state.
- **GameState**: the authoritative registry of active ships, squads, projectiles, dynamic obstacles, selections, visibility, commands, and score counters for one level.
- **SavedSquad / SquadShip / FleetShip**: persistent squad composition, placement, and individual ship/stat records.
- **Squad / Ship**: runtime group and runtime combatant created from persistent records.
- **Command**: pooled runtime behavior attached to a squad; Hive Mind commands carry outcome IDs and TSV results.
- **Weapon / Projectile**: target selection, firing, collision, damage-in-flight accounting, and effects.
- **Map / StaticObstacle / CollisionAsteroid / MiningAsteroid / MapObject**: arena geometry and resources.
- **TSV**: a total strategic value measure derived from ship health/combat value and mined minerals. It serves score/statistics and is the learning outcome recorded for AI strategies (`Utilities.CalculateTsv`, `Ship.LogAttackingDamage`, `StoredCommand`, server `Game.storeState`).
- **Minerals**: gathered by Factory/Carpenter Bee mining commands, included in ship value and persistent fleet statistics.
- **Supply/capacity**: squad-maker constraint based on ship capacity/TSV (`FleetShip.GetCapacity`, `SavedSquad`, `SquadMaker`).

### Typical session progression

**Confirmed.** `Loading Screen` is build index 0, followed by `Main Menu`, `Squad Maker`, `Hivemind Training`, `Space`, and `Level Intro` in `ProjectSettings/EditorBuildSettings.asset`. The initial scene opens the lazily created `ConfigData.Socket`, retrieves three settings documents plus user progress, three fleets, three squad collections, and three level collections, then changes to Main Menu. Mode selection switches `ConfigData.CurrentShips` and `CurrentGameMode`. Squad Maker writes `ConfigData.LevelOptions`; the stage consumes a clone so a battle can mutate it without directly mutating the carried selection. At level end, active runtime objects are killed/deactivated, then deferred pool release occurs through `GameState.Release()`.

## 2. Repository map

### Runtime source

The project contains 187 C# files and about 39,053 physical lines under `Assets/Scripts`. It does not define a first-party `.asmdef`; gameplay therefore resides in Unity's generated `Assembly-CSharp` assembly. Third-party assemblies include NativeWebSocket and Steamworks.NET.

| Area | Responsibility and dependencies | Status |
|---|---|---|
| `Assets/Scripts/Scenes` | Scene lifecycle (`Scene`), mode transitions (`MainMenu`, `SquadMaker`, `LevelIntro`), and battle host (`Stage`). Depends on global `ConfigData`, server data readiness, UI, and levels. | Current, highly stateful. |
| `Assets/Scripts/Levels` | Arena setup, authoritative registries, input, squads, commands, pathfinding, triggers, timers, pools, and spawn construction. | Core current implementation. `LeveLTriggers.cs` is a large campaign-specific partial class. |
| `Assets/Scripts/Entities` | Base entities, obstacles, asteroids, map objects, ships, weapons, and projectiles. | Core current implementation. |
| `Assets/Scripts/Data` | JSON-backed domain/persistence models: fleets, squads, progress, settings, maps, and levels. | Current, with local/server dual storage. |
| `Assets/Scripts/Server` | Unity request/response DTOs, WebSocket transport, resend/deduplication, and response-to-runtime dispatch. | Current and required: gameplay pauses without a level server connection. |
| `Assets/Scripts/Settings` | Server-loaded `Configuration`, `StartingSettings`, and `ShipStats`/`ShipStatBlock`. | Current source of tunable configuration and ship stats. |
| `Assets/Scripts/UI Components` | HUD, squad action UI, dialogue/cutscene presentation, drag/drop squad editing, settings, codex, and menus. | Current; tightly coupled to concrete scene object references. |
| `Assets/Scripts/ConfigData.cs` | Global static registry, enums, endpoints, configuration/data instances, mode carry-over state, mapping tables, constants, and user-data setup. | Current but over-centralized; multiple sources of truth converge here. |
| `Assets/Scripts/Utilities.cs` | Mapping tables, random/hash helpers, geometry, TSV/firepower calculations, JSON helpers, sprite recoloring. | Current shared utility hub; changes have broad reach. |
| `Assets/Scripts/Ships.cs` | Facade joining a `FleetData` and `SavedSquadsData`; creates/replaces ships and squads and saves both. | Current persistence-domain service. |
| `Assets/Scripts/Entities/Ships/Brain.cs` | Former ML-Agents policy integration and observations/actions. Core Agent inheritance and methods are commented out. | Experimental/disabled. Do not describe `ActivateBrains` as functioning neural control without reactivation work. |
| `Assets/Scripts/Editor/MemorySnapshot.cs` | Editor-only memory snapshot utility. | Supporting/debug only. |

Largest responsibility concentrations are `LeveLTriggers.cs`, `Ship.cs`, `SquadMaker.cs`, `Pathfinder.cs`, `Utilities.cs`, `Level.cs`, `Squad.cs`, `ConfigData.cs`, `Pool.cs`, `LevelInputManager.cs`, and `Socket.cs`. These are architectural hotspots, not merely large files.

### Scenes

Build-enabled scenes are defined in `ProjectSettings/EditorBuildSettings.asset`:

- `Loading Screen.unity`: initial settings/data bootstrap and redirect.
- `Main Menu.unity`: game-mode selection, resets, and campaign continuation.
- `Squad Maker.unity`: persistent fleet/squad editor and level-option handoff.
- `Hivemind Training.unity`: Stage prefab variant; despite its name, current serialized `IsTrainingHiveMind` is `0` in the inspected scene while `ActivateHiveMind` is `1`. Treat the name as insufficient evidence of mode.
- `Space.unity`: main battle stage.
- `Level Intro.unity`: campaign introduction content based on `Resources/LevelIntros.json`.

Disabled or non-build scenes include older RL test rooms, `Sandbox`, `Survival`, `Live Main Menu`, 4K/downscaled variants, and visual tests. `Stage.cs` occurs in `Space`, `Survival`, `Live Main Menu`, and Hivemind variants. Scene-specific prefab overrides are behaviorally significant; for example `Survival` enables user control, audio, stats, random options, and Hive Mind, while `Live Main Menu` is AI-only and sets `IsTrainingHiveMind`.

### Prefabs, resources, and serialized configuration

- `Assets/Prefabs/UI/Stage.prefab` defines the complete serialized Stage contract; scenes override its mode flags and wire cameras, UI, pool, prefabs, audio, and debugging components.
- `Assets/Prefabs/Level.prefab` supplies the Level instance spawned by `Stage.SpawnLevels()`.
- `Assets/Prefabs/Entities/Ships` and subfolders define ship components, colliders, weapons, effects, and optional inactive Brain references.
- `Assets/Prefabs/Entities/Projectiles` defines projectile behavior types and serialized collision/animation values.
- `Assets/Prefabs/Entities/Obstacles` and `Assets/Resources/Obstacles` supply general and mission-specific geometry. `Level.SpawnObstacles()` uses `Resources.Load("Obstacles/<name>")` for named layouts.
- `Assets/Resources/LevelIntros.json` and portrait resources support campaign presentation.
- `Assets/SaveData` contains project seed/example JSON. Actual local runtime files use `Application.persistentDataPath/SaveData` through `ConfigData.GetBasePath()`.
- `Assets/SpriteCache` is a large generated/reused recoloring cache, not game logic.
- `Assets/Scripts/Data/campaign_levels.json` and `challenge_levels.json` are source data; server-side `User.determineUserId()` currently forces campaign/challenge level data to shared user ID 2.

### Training artifacts

`Training/results/bees82` and `bees83` hold historical ML-Agents checkpoints/models. `bees83/configuration.yaml` records POCA self-play. These artifacts prove that a neural experiment existed; they do not prove the current C# path works. `Brain` no longer inherits `Agent`, its action/observation methods are commented, ship prefabs serialize `HasBrain: 0`, and `Ship.Create()` has Brain setup commented. The active learned behavior is the external tabular/outcome-driven Hive Mind service, not the ONNX policy path.

### External server

`F:\RLDemo\BeesServer\siServerDev.js` is a 138 KB Node.js service using `websocket`, `mysql2`, `xxhashjs`, filesystem caches, and an HTTP server. It owns strategy selection/outcome persistence and optional user-data storage. It is outside this repository, so Unity changes and server protocol/schema changes are not versioned atomically here.

## 3. Runtime architecture

### High-level architecture and data flow

```text
Unity scene
  Scene (socket pump + data-readiness gate)
    |
    +-- ConfigData (global configuration, mode, persistence handles, Socket)
    |
    +-- Stage (shared pool/prefabs/UI/input/audio/camera; N Levels)
          |
          +-- Level (map/options/timers/objective/environment/server game id)
                |
                +-- GameState (authoritative active registries + score + deferred releases)
                +-- LevelConstructor -> SavedSquad -> Squad -> FleetShip -> Ship
                +-- Pathfinder <-> Ship.MoveToPoint / Ship.FixedUpdate
                +-- campaign Trigger graph -> dialogue/spawns/objective completion
                |
                +-- Squad command loop
                      -> MatchupStrategyRequest
                      -> Socket/WebSocket
                      -> Node SocketConnection -> Game -> MySQL/cache
                      <- targeting strategy
                      -> CommandRequest
                      <- command + shooting strategy + outcome IDs
                      -> pooled Command -> squad/ship movement and Weapon targeting
                      -> StoredCommand TSV outcome -> StoreCommandsRequest -> MySQL

Physics/event flow
  RangeCollider -> Weapon.ShipsWithinRange -> Weapon/Turret -> Projectile
  -> trigger queue -> Ship.LogAttackingDamage -> Ship.Kill -> Squad.Kill
  -> GameState.GameOver -> Level.LevelOver -> SaveAndEnd -> GameState.Release

Persistence flow
  UserData -> DataFile -> local JSON and/or Socket requests
  -> ConfigData data instances -> Ships facade -> SquadMaker/LevelConstructor
```

### Startup ordering

1. **Static initialization:** `ConfigData.Socket` is lazy. First socket access constructs it and immediately calls `MakeSocket()`; reading unrelated configuration no longer opens a connection. The selected endpoint is controlled by compile-time constants `Test` and `Development` in `ConfigData.cs`.
2. **Scene start:** The scene-specific `Start()` calls `Scene.Start()`. The first Scene becomes `ConfigData.SocketManager`, creates the network-disconnection dialogue, chooses thread count (`processorCount - 1`), and creates socket/resend/reconnect timers.
3. **Settings:** `Scene.LoadSettingsWhenOpen()` polls until the socket is open, then `ConfigData.LoadSettings()` requests `Configuration`, `StartingSettings`, and `ShipStats` from the server.
4. **User data:** `Scene.Update()` waits for all settings; the main scene then calls `ConfigData.SetupUserData()` and `CheckDataFiles()`. `UserData`/`DataFile` asynchronously populate progress, settings, fleets, squads, and levels.
5. **Finalize:** Once all data-loaded flags are true, `FinalizeSceneWithUserData()` constructs the three `Ships` facades and scene-specific UI/runtime state.
6. **Loading redirect:** `LoadingScreen.Update()` loads Main Menu when data and settings are ready and the version is not marked dead.

### Battle initialization

`Stage.FinalizeSceneWithUserData()` initializes prefab conversion dictionaries and `Pool`, spawns `LevelCount` Level prefabs, then wires non-training UI/input/audio and calls `Level.Setup()` for each. `Level.Setup()` constructs `LevelConstructor`, immediately sends `SetupLevelRequest`, adds a `GameState` component, and calls `SetupLevel()` without waiting for the setup response. Actual Hive Mind requests are gated later by `IsLevelSetupOnServer`.

`Level.SetupLevel()` clones/creates level options, resets prior state, applies stage overrides, chooses/spawns the map and environment, calls `SetupShips()`, enables fog/mining/triggers, and schedules Hive Mind polling. The setup response sets `IsLevelSetupOnServer`, `IsLevelConnectedToServer`, `ServerGameId`, and adds the level to `Socket.OpenLevels`.

### Main loops

- `Scene.Update()`: pumps socket messages every 0.1 seconds through a custom `Timer`, checks resends, controls reconnect UI, and gates finalization.
- `Stage.Update()`: calls `Scene.Update`, processes player input when permitted, updates camera following/movement, and logs debug data.
- `Level.Update()`: initiates `LevelOver`, freezes ordinary play on pause/network disconnect/uninitialized server level, starts timeouts, applies completed path results/launches queued work, and updates every `ScaledTimer`.
- `Ship.FixedUpdate()`: consumes an owned/current path result, then rotates/moves the Rigidbody2D; optional debug properties are refreshed.
- `Turret.FixedUpdate()` and projectile `FixedUpdate()`: aim/fire and drain queued trigger contacts.
- `Pathfinder` background tasks: run path search only on worker tasks; completion is queued and applied on the main thread by `Pathfinder.Update()`.
- Server `Server.runQueue()`: drains queued socket requests every ~1 ms; cache cleanup and idle-only consolidation have independent timers.

### Authority and communication

- **Active battle membership:** `GameState` is authoritative. Unity object hierarchies and `Squad._ships` are secondary indexes that must be kept synchronized.
- **Persistent ownership:** `FleetData` owns `FleetShip`; `SavedSquadsData` owns `SavedSquad`; `SquadShip` references fleet IDs. `Ships` coordinates the two collections.
- **Runtime health and motion:** `Ship` owns live health/TSV/position/movement. Persistent statistics are mirrored into `FleetShip` and `SavedSquad.Stats` during damage/end processing.
- **AI strategy selection:** the server is authoritative for strategy choice and outcome IDs; Unity is authoritative for whether a late response still belongs to the same level/squad and for executing it.
- **Objectives:** ordinary mode uses side viability; campaign uses the `LeveLTriggers` mission graph.
- **Communication style:** mostly direct references and global static access; callbacks occur through timers, Unity physics, socket handler dispatch, command finalization, and campaign `Trigger` delegates. There is no general event bus or dependency-injection boundary.

### Lifetime and cleanup rules

Runtime objects are usually created once by `Pool` and reused. `Create()` establishes immutable prefab/stage data; `Setup()` establishes per-use identity and state; `ClearData()` must erase previous-use state; `Kill()` removes the object from active registries and queues it for release or releases it directly; pool return often has empty callbacks. Therefore correctness depends on every type's `ClearData`/`Deactivate` implementation.

Ships, squads, commands, asteroids, and asteroid pieces use deferred release lists in `GameState`; projectiles return directly to a projectile pool on `Kill()`. `SaveAndEnd()` snapshots active collections before killing because kill mutates the registry, then calls `StoreCommands()` and `Release()`. Static obstacles and backgrounds are instantiated/destroyed rather than fully pooled.

## 4. System-by-system documentation

### 4.1 Global configuration and bootstrap

**Purpose.** Provide cross-scene state, enums, endpoints, constants, mappings, settings, and persistence roots.

**Key code.** `ConfigData.cs`, `Settings/Configuration.cs`, `StartingSettings.cs`, `ShipStats.cs`, `ShipStatBlock.cs`, `Scenes/Scene.cs`.

**Inputs/outputs.** Inputs are server settings responses, serialized Stage flags, Steam identity/development fallback, and carried menu choices. Outputs include faction sides, ship stats, data instances, `CurrentShips`, `LevelOptions`, and a singleton-like socket.

**Assumptions/invariants.** Side values are one-based and used as `side - 1` array indexes. Configuration must load before ship pool creation. Enum/name mappings in `Utilities` and strategy names on the server must remain synchronized.

**Failure modes.** Static initialization makes endpoint/network behavior hard to isolate. `ConfigData` mixes immutable schema, mutable session state, debug counters, and services. A missing/renamed server setting can prevent every scene from finalizing. `MaxThreads = processorCount - 1` can become zero on a single-core environment unless constrained elsewhere.

**Extension/testing seam.** Extract an immutable runtime configuration snapshot and inject a transport/data repository into Scene/Stage. Until then, reset every static loaded flag, data instance, scene list, socket collection, carried option, hash set, and current mode between tests.

### 4.2 Scene flow, game modes, and session orchestration

**Purpose.** Move the user through load, menu, squad construction, intro, and battle.

**Key code/assets.** `Scene`, `LoadingScreen`, `MainMenu`, `SquadMaker`, `LevelIntro`, `Stage`; enabled scenes in `EditorBuildSettings.asset`; `Stage.prefab` and scene overrides.

**Execution.** Main Menu selects `Campaign`, `FreePlay`, `Challenge`, or `FishTank`. Squad Maker chooses fleets/squads and builds `LevelOptions`. `ConfigData.LoadLevel()`/scene methods perform scene changes. Stage creates levels only after user data finalization.

**Edge cases.** Scene behavior depends on which Scene happens to become static `SocketManager`; `ConfigData.Scenes` has no visible removal in `Scene`, so destroyed-scene references may accumulate. Stage assumes specific `UIElements` indexes in campaign (`Destroy(Stage.UIElements[2])`, mutate `[3]`). Variants can silently drift because serialized overrides, not class code, define the mode.

**Safe extension.** Add a new scene through a Scene subclass that calls `base.Start()` and waits for `IsFinalized`; prefer a named serialized configuration object over another cluster of Stage booleans. Test the exact scene/prefab variant, not only the class.

### 4.3 Level construction and world/environment

**Purpose.** Materialize a battle from `LevelOptions`, a map, persistent squads, stage overrides, and random choices.

**Key code.** `Level.SetupLevel`, `RandomizeOptions`, `SetupMapAndCamera`, `SpawnObstacles`, `SpawnMiningAsteroids`, `SetupShips`; `LevelConstructor`; `UI Components/Map.cs`; `Data/Map.cs`, `LevelOptions.cs`, `ObstacleMap.cs`; obstacle prefabs/resources.

**Flow.** Options are cloned, then map index, obstacle set, collision asteroids, fog, and mining may be randomized. A pooled map establishes dimensions/bounds/starting positions. Static obstacle `MapObject`s are set up; dynamic asteroids enter `GameState`. `LevelConstructor.SetupShips()` selects random, override, or chosen squads, converts each `SavedSquad` to a pooled `Squad`, skips dead `FleetShip`s, obtains typed ships from the pool, sets them up, and positions formations. Carriers create minion drone/striker squads.

**Confirmed disabled feature.** Random enemy reinforcement activation ends with `&& false` in `Level.RandomizeOptions`; UI/data fields exist, but ordinary randomized reinforcements cannot activate through that branch.

**Assumptions.** Map indexes align across `ConfigData.Maps`, `Prefabs.Maps`, pool switch statements, and serialized assets. Named obstacle strings match Resources paths. A `SavedSquad` ship ID resolves to an existing `FleetShip`.

**Risks.** Randomness uses global utility calls with no exposed seed, hindering replay. Static obstacles are destroyed during teardown while dynamic objects are pooled. `HasObstacles` also enables Pathfinder, so an inconsistent option can make movement use an uninitialized pathfinder or ignore geometry. Carrier minion ownership crosses the parent squad, minion squad, and persistent saved squad.

**Testing seam.** `LevelConstructor` is a plain C# object but depends heavily on Level/Pool. Introduce a spawn factory and deterministic random provider. High-value integration tests should construct a minimal Level prefab and assert registry/persistent-loading flags after setup/teardown.

### 4.4 Runtime state and object pooling

**Purpose.** Track all active objects and amortize allocation.

**Key code.** `GameState.cs`, `Pool.cs`, `Entity.cs`; every pooled type's `Create`, `Setup`, `ClearData`, `Kill`, `Activate`, and `Deactivate`.

**Owned state.** `GameState` owns ship/squad/projectile/obstacle collections, `ShipsById`, selections, visibility maps, damage statuses, stored commands, score counters, and deferred-release lists. `Pool.ItemCount` provides stage-wide runtime IDs.

**Invariant.** An active Ship must be in both `GameState.Ships` and `ShipsById`, in exactly one live Squad, and have `FleetShip.IsLoadedIntoLevel == true`. Killing reverses those facts exactly once. Equivalent invariants apply to squads and their `SavedSquad.IsLoadedIntoLevel` flag.

**Confirmed contract.** `Pool.GetProjectileFromPool()` and `ReturnProjectileToPool()` currently cover the same projectile enum values and return each type to its corresponding pool, including `QueenSmall`, `QueenLarge`, `StrikerBomb`, `RocketExplosion`, `SplitShot`, `FireBargeExplosion`, and `FireTankExplosion`. This agreement is manually duplicated across switches, so an exhaustive enum round-trip test should protect it before other pooling work.

**Risks.** Pool callbacks do not reset state. Deferred release lists are only cleared by `ResetState`; repeated release without reset would double-release. Object identity changes per setup, so callbacks/requests must compare the correct runtime ID. Direct `Destroy` and pooling coexist.

**Safe extension.** Centralize enum-to-pool mappings rather than adding parallel switches for create/get/return/prewarm. For any new pooled type, add a lifecycle test: Create once, Setup A, Kill/release, Setup B, then assert no A references or active timers remain.

### 4.5 Squads, player orders, and formations

**Purpose.** Group ships into a selectable tactical unit, preserve formation offsets, and host one active command plus optional queued scripted commands.

**Key code.** `Squad.cs`, `CarrierSquad.cs`, `LevelInputManager.cs`, `Selector.cs`, `SquadActionBox.cs`, commands under `Levels/Commands`.

**Flow.** `SavedSquad.ToSquad(Level)` obtains/configures a runtime Squad. `Squad.Move(destination)` preserves offsets, compresses formation if necessary, and delegates to each Ship. User actions create pooled non-Hive-Mind commands (`UserAggressive`, `UserGuard`, `UserPatrol`, etc.) or modify flags such as matching speed, cease fire, chase, and shooting strategy. Scripted campaign sequences use `CommandQueue`; after command finalization `RunCommandQueue()` starts the next item or invokes its completion action.

**Assumptions.** A live squad has at least one ship; many calculated properties use `Max`, `Min`, or `First`. `AddShip()` recalculates permanent banned strategies from composition. User selection requires same-side, user controller, input enabled, and not already selected.

**Failure modes.** Empty-but-not-dead squads cause aggregation exceptions. `SetCommand` overwrites without itself finalizing the previous command; all callers must ensure transition discipline. `_temp*` fields deliberately reuse shared mutable scratch collections under a single-thread assumption. Carrier/minion squads complicate saved-squad loaded flags and death behavior.

**Testing seam.** Formation geometry, ban-set derivation, selection predicates, matchup strings, and target sorting can be unit tested with lightweight Ship/Squad fixtures. Player-input interpretation needs PlayMode tests because it relies on Unity's static Input and EventSystem.

### 4.6 Hive Mind AI and command execution

**Purpose.** Choose targets, strategic maneuvers, and shooting priorities using server-stored historical outcomes.

**Key code.** `Level.SetupHivemind/GetHiveMindCommands`; `Squad.MakeMatchupStrat`, `MatchupStrategy.SortSquads`, `MakeMatchupAndGetCommand`; `Socket.HandleMatchupResponse/HandleStrategicCommandResponse`; `Command` and derived types; server `Game.getMatchupStrategy`, `getStrategy`, `pickStrat`, `storeState`.

**Two-stage flow.** Every eligible Hive Mind squad enters `SquadsAwaitingCommands`. At 0.25-second intervals, in-bounds squads request a targeting/matchup strategy using up to 64 sorted ship-type letters and banned target types. The response chooses a local target squad through `MatchupStrategy.SortSquads`. Unity then builds a matchup string containing own/allied/enemy composition, range contact, and comparative-health bucket, bans context-invalid commands, and requests a command. The response provides command type, shooting strategy, and three outcome IDs. Unity obtains a pooled Command, substitutes BombingRun for all-bomber squads and Charge for all-barge squads in several attack modes, then executes it.

**Learning feedback.** Damage changes the active command's TSV. Finalization writes TSV into the matching `StoredCommand`. `GameState.StoreCommands()` sends finalized strategic, shooting, and targeting outcomes. Server `Game.storeState()` queues database updates; server strategy retrieval combines historical outcome value/uses with exploration and caches.

**Late-response protection.** Matchup requests preserve the persistent Squad ID; command requests preserve the runtime `ItemId`; handlers verify the squad is still the same and the level has not ended. Socket hashes are deduplicated globally and also recorded per Level for reset cleanup.

**Risks and divergences.** Unity and Node duplicate command/strategy name tables and numeric meaning without a shared schema. The server can return a banned command; Unity logs an error but still proceeds. A response with an unknown name can leave `_handleStrategicCommandResponse_command` stale/null before `SetCommand`. Command finalization indexes `OutcomeIdToPastCommandIndex` directly, making missing/duplicate outcome IDs fatal. Server game identity is connection-ID based, not a durable match identifier.

**Safe extension.** Adding a command requires synchronized enum/name mappings, server `possibleStratTypes`, pool create/get/return, response construction/execution, contextual bans, command persistence, and tests. A protocol schema/version handshake should precede broad AI changes.

### 4.7 Movement and asynchronous pathfinding

**Purpose.** Turn and move ships while routing around static and moving obstacles with ship-size clearance.

**Key code.** `Ship.MoveToPoint`, `Move`, `SetMovementVelocity`, `MergePathfindingPaths`, `HandleSupersededPathfindingRequest`; `Pathfinder.cs`; obstacle colliders/layers; `Level.CalculateShipClearances`.

**Flow.** Direct movement first box-casts for a live obstacle. When blocked, positions are converted to a scale-4 grid and `Pathfinder.FindPath` allocates a monotonically increasing request ID and captures the Ship's pooled-lifecycle ID. An idle worker slot starts immediately or the request enters `PathsWaiting`. The main thread snapshots dynamic obstacle clearance before `Task.Run`. A* uses hard clearance, preferred-clearance penalties, diagonal corner checks, egress from invalid starts, nearest valid destinations, and smoothing. Worker completion enters a concurrent queue. `ApplyCompletedPathResults()` requires reference-identical Ship ownership plus matching request, lifecycle, and worker-slot IDs before publishing; it clears the slot before accepting or rejecting the result. `Ship.FixedUpdate()` verifies the request ID again before merging waypoints.

**Important contract.** Unity APIs and live collections must not be accessed from the background search. New destinations during an active request are retained as a pending destination. `Ship.ClearData()` advances `PathfindingLifecycleId` and clears all request/completion state, so an old task cannot become valid merely because a new Pathfinder reuses the same numeric request ID. Queued requests are retained individually; `ShipsQueued` membership is removed only after no request for that exact Ship reference remains. Stale results must never overwrite the latest order; current request/lifecycle/slot checks are explicitly designed for rapid drag input and pooled reuse.

**Failure modes.** Dynamic-layer snapshots can become stale while a task runs. Request and lifecycle IDs are signed integers and theoretically wrap after billions of increments; this is not expected in a practical session, but equality tokens are not globally unique identifiers. Grid caching is Stage-wide by size; obstacle layers remain per Pathfinder. Scale 4 can miss geometry smaller than a cell, as the source comment warns. `Pathfinder` comments say some logic only works for rectangular maps.

**Testing seam.** Most grid/search methods are pure enough to test after extracting Unity collider sampling. Test narrow corridors by clearance, diagonal corner blocking, invalid start egress, unreachable destination, smoothing safety, rapid supersession, killed/reused ship, and moving-obstacle refresh.

### 4.8 Visibility and targeting

**Purpose.** Maintain player fog-of-war and the set of enemies available to the Hive Mind and weapons.

**Key code.** `FogOfWarVision`, `HivemindVision`, `RangeCollider`, `ProximityCollider`, `GameState.HivemindShips/VisionCache/SpottedShips`, `Weapon`, `MatchupStrategy`.

**Flow.** Vision/range trigger colliders update dictionaries or sets. `GameState.GetShipsVisibleToHiveMind(side)` unions per-spotter sets and excludes dead ships. For a player-controlled side, `GetSquadsVisibleToHiveMind` intentionally returns all enemy squads; for AI it derives squads from visible ships. Weapons maintain their own ID-keyed in-range dictionaries and select by the current shooting strategy.

**Risks.** Visibility exists in several representations (`HivemindShips`, `VisionCache`, `SpottedShips`, weapon ranges, player-visible map objects). Trigger exits caused by death must clean reverse references. Duplicate Squad results can emerge when several visible ships share a squad. Colliders/layer matrices are hidden serialized dependencies; the Fire Tank neutral explosion required both code and Physics2D layer contact.

**Testing seam.** Test trigger enter/exit/death, two spotters seeing the same ship, pooled ID replacement, fog enabled/disabled, and every shooting/targeting strategy against ties and empty sets.

### 4.9 Weapons, projectiles, damage, TSV, and death

**Purpose.** Convert target selection into shots, resolve collisions, update health/value/statistics, and remove entities safely.

**Key code.** `Weapon`, `Turret`, `RangeCollider`; projectile hierarchy; `Ship.LogDamage`, `LogAttackingDamage`, `Kill`; specialized ship classes; `ShipDamageStatus`.

**Flow.** Range triggers populate weapon targets. Weapon/turret timers fire a typed pooled projectile through `Level.AddProjectile`/projectile `Setup`. `Projectile.Setup` first clears state from the prior pooled use, then assigns the new owner, target, position, angle, range, and power; this ordering is required because derived `ClearData` implementations reset additional runtime fields. Projectile collision is queued and processed in `FixedUpdate`, avoiding immediate mutation inside trigger callbacks. Valid enemy contact calls `Ship.LogAttackingDamage`; health loss recalculates TSV, attributes damage/kills to attacker fleet/squad, adjusts command TSV, and calls `Ship.Kill` at zero health. Kill drops visuals (unless teardown), cleans visibility/range references, marks in-flight projectiles' shooter dead, removes ship from GameState and Squad, cancels timers/weapons, and kills the empty squad. The squad then may set GameOver.

**Special rules.** Fire Barge explosions can damage friendly ships under explicit conditions. Fire Tank explosions are neutral hazards and damage either faction once per ship while harmful (`RocketExplosion.ShipCollision`). Beacons do not count as a lost ship for statistics. Side viability treats a fleet of only immobile structures as killed.

**Assumptions.** Damage is applied once per collision (explosions track hits); `Power` recorded as damage-in-flight is later removed; killer references may be null for hazards. Projectile layer/tag configuration must match code.

**Failure modes.** Reverse range sets and `ShipDamageStatus` are shared mutable bookkeeping. A projectile may outlive its shooter. Teardown and combat death take different branches. Specialized projectile and ship overrides can omit base cleanup. Clearing a projectile after assigning new-use values can erase those values; this previously reset LaserBeam's new angle to zero and is now protected by the lifecycle matrix. Parallel create/get/return pool switches can drift when a new enum value is added.

**Testing seam.** Create a matrix by projectile type, shooter/target side, harmless flag, repeated overlap, shooter death, and teardown. Assert health, TSV, stats, command value, registries, reverse ranges, and exact pool ownership.

### 4.10 Environmental hazards and resources

**Purpose.** Add destructible geometry, collision hazards, and mining objectives.

**Key code.** `StaticObstacle`, `MapObject`, `CollisionAsteroid`, `AsteroidPiece`, `MiningAsteroid`, `CanisterBomb`, `Mining` command, Level spawn timers.

**Behavior.** Collision asteroids move, collide by size/health, crack below half health, spawn shards/pieces, and are deferred to pools. Mining asteroids accept Factory/Carpenter Bee ships executing a Mining command; mining transfers value over repeated timers and finalizes commands when depleted. MapObjects take projectile damage and are destroyed. Fire Tank `CanisterBomb` produces the dedicated FireTankExplosion projectile.

**Risks.** Asteroid collision uses many reciprocal sets and delayed timers, so double-contact and simultaneous destruction are sensitive to order. Static/dynamic obstacle representations differ. `Zone.OnTriggerEnter2D` assumes the collider has a Ship and calls the delegate without null checks. Random asteroid timing and directions are not replayable.

### 4.11 Campaign triggers, dialogue, and objectives

**Purpose.** Script narrative missions and tutorial/progression behavior.

**Key code.** `CampaignMissionCatalog.cs`, `LeveLTriggers.cs`, `Trigger.cs`, `Zone.cs`, `CutsceneManager`, `DialogueManager`, `LevelIntro`, campaign level JSON/resources.

**Execution.** `SetTriggers()` clears prior triggers and delegates ID lookup to `CampaignMissionCatalog`, the authoritative mapping from persisted ID/name to setup method. Unknown IDs now fail explicitly instead of silently producing a mission with no triggers. Each mission builds a closure-heavy graph of `Trigger(Func<bool>, Action, name)` objects. Every 0.5 seconds `CheckTriggers()` evaluates current triggers, runs satisfied actions, and moves staged `NextTriggers` into the active list. Actions directly mutate ships, UI, camera, control flags, spawns, and completion state.

**Risks.** Mission logic is a 2,852-line partial class with captured local variables and magic IDs. Refactoring a ship, UI field, or objective can break only one late mission branch. Trigger precision is documented as five seconds in a stale comment, while current scheduling is 0.5 seconds—an internal documentation contradiction. Campaign completion behavior is duplicated across mission blocks.

**Safe extension.** New campaign objectives currently belong in a new mission method plus level data and intro resources, with a matching `CampaignMissionCatalog` definition. A data-driven mission state machine would be safer. The catalog contains trigger implementations for IDs 9-11, but `campaign_levels.json` currently contains records only for 0-8; those definitions are explicitly marked `HasPersistedLevelData = false`. At minimum, create one deterministic PlayMode scenario per persisted mission with dialogue skipped and assert every terminal branch.

### 4.12 Persistence and progression

**Purpose.** Store fleet identity/statistics, squad composition, user progress, levels, and hotkeys locally and/or in MySQL through the server.

**Key code.** `UserData`, `DataFile`, all `Data/*Data` classes, `Ships`, `ConfigData.SetupUserData/CheckDataFiles`, server `User` and socket handlers.

**Flow.** `UserData.SetupFile` creates/loads defaults. `DataFile` chooses local filesystem or server according to `Configuration.UseLocalStorage`; mirror flags can write both. Server data is requested asynchronously and polled by `Socket.CheckStandingRequests`/`DataFile.WaitForResponse`. `Ships` coordinates fleet and squad persistence. End-of-level saving updates battles, wins, damage, minerals, deaths/replacements, and mode progress.

**Assumptions.** Compact JSON field names and enum numeric values are stable. All referenced FleetShip IDs exist. Writes are whole-document replacements. Server identifies a user from the client-provided user ID; current server code has no visible authentication check.

**Risks.** There is no schema version/migration layer for user documents. Empty FleetData/SavedSquad serialization, string escaping, locale-dependent LevelOptions numbers, and partial `DataFile` replacement were corrected and have golden coverage, but older documents and full schema evolution still lack migration tests. Local and server mirrors can diverge without conflict resolution. Server forces shared campaign/challenge level data to user 2 as a temporary override. Server settings version is independent from `ConfigData.Version` comments and setup code hard-codes request version 6 in places.

**Testing seam.** Golden-file round trips for empty/minimal/full/corrupt/old data; local/server repository contract tests; interrupted save; missing FleetShip; enum evolution; and mirrored-store failure.

### 4.13 UI, visualization, audio, metrics, and debugging

**Purpose.** Present selections, commands, scores, minimap/fog, dialogue, settings, statistics, and developer telemetry.

**Key code.** `GameMenus`, `SquadActionBox`, `LevelInputManager`, `Selector`, `CutsceneManager`, `DialogueManager`, `SettingsMenu`, `AudioController`, `UIAudioController`, `DebugLogger`, numerous UI prefabs.

**Behavior.** Stage owns UI references, while GameState/Squad/Ship directly update selection/action widgets. Input hotkeys are loaded from `UserSettingsData` and bound to action delegates. DebugLogger exposes command counts, completions, timeouts, and object state; many runtime classes maintain `__` inspector mirrors. Server logs queue/cache/outcome metrics.

**Risks.** UI and gameplay are bidirectionally coupled. Serialized missing references or array reorderings can fail setup. Debug property updates can allocate heavily and are guarded by flags, but `Stage.DebugLogger.LogData()` is still called each frame. `LevelInputManager.HasPauseInput()` currently returns `false` with an alert comment, so the tester pause-toggle branch in `Level.Update()` cannot resume through that method—suspicious/possibly intentionally disabled for beta.

## 5. Important execution traces

### Trace A: application startup

1. Unity loads `Loading Screen.unity` (build index 0).
2. First access to lazy `ConfigData.Socket` constructs it and begins a WebSocket connection.
3. `LoadingScreen.Start()` calls `Scene.Start()`.
4. The scene becomes `SocketManager`, configures target frame rate/thread count, network dialogue, and timers.
5. `LoadSettingsWhenOpen()` sees an open socket and calls `ConfigData.LoadSettings()`.
6. `Socket.Update()` drains responses; settings DTOs populate `Configuration`, `StartingSettings`, and `ShipStats`.
7. `Scene.Update()` calls `ConfigData.SetupUserData()` and `CheckDataFiles()`.
8. Each `UserData` creates a `DataFile`; local mode reads JSON, server mode sends `get-user-data` requests. `WaitForData()` callbacks populate objects and loaded flags.
9. `Scene.FinalizeSceneWithUserData()` creates FreePlay/Campaign/Challenge `Ships` facades.
10. `LoadingScreen.Update()` loads Main Menu.

### Trace B: battle and level startup

1. Menu/Squad Maker set `ConfigData.CurrentGameMode`, `CurrentShips`, side configuration, and `LevelOptions`.
2. `Space.unity` loads; `Stage.Start()` calls `Scene.Start()`.
3. After data readiness, `Stage.FinalizeSceneWithUserData()` calls `Prefabs.LoadConversions()`, `Pool.Setup()`, `SpawnLevels()`, UI/input/audio setup, then `SetupLevels()`.
4. `Level.Setup()` creates `LevelConstructor`, sends `SetupLevelRequest`, creates/sets up `GameState`, then calls `SetupLevel()`.
5. `SetupLevel()` clones options, resets state, configures Stage overrides and environment, obtains the pooled map, spawns obstacles, and constructs ships/squads.
6. It sets fog/mining/campaign triggers, selects the first user squad, and calls `SetupHivemind()`.
7. Server `SocketConnection.handleMessage("setup-level")` constructs a `Game` and responds with `GameId`.
8. Unity `HandleSetupLevelResponse()` marks the Level connected/setup. The recurring Hive Mind timer can now send strategy requests.

### Trace C: creation/spawning of a ship

1. `LevelConstructor.SetupShips(side)` chooses saved/random/override squads.
2. `SpawnShipsAndSquads()` calls `SavedSquad.ToSquad(Level)` and adds the live Squad to GameState.
3. For each non-dead `SquadShip`, it resolves `FleetShip` and calls `InstantiateShip(fleetShip.Type)`.
4. `InstantiateShip` switches on `ShipTypes` and calls the matching `ObjectPool<T>.Get()`, then parents it to the map.
5. `Ship.Setup(Level, FleetShip, Squad, offset)` assigns runtime ID/health/name, clears previous pooled state, registers Hive Mind visibility, adds itself to GameState, sets structure flags/UI markers, sets up every Weapon, activates remains data, and activates the entity.
6. Caller adds Ship to Squad, recolors it, applies formation/speed/cease-fire/chase settings, and includes its TSV in initial side value.
7. `PositionSquads()` computes rows around the start point, writes squad/ship transforms and offsets, and optionally gives an initial movement order.

### Trace D: how AI chooses what to do

1. `SetupHivemind()` queues eligible AI squads and starts a recurring 0.25-second timer after `InitialCommandDelay`.
2. `GetHiveMindCommands()` dequeues squads; out-of-bounds squads move to their start and are requeued.
3. `Squad.MakeMatchupStrat()` sends own composition, opponent ID, and banned target types.
4. Server `Game.getMatchupStrategy()` resolves/caches a matchup, filters strategy options, selects one from outcome history/exploration, inserts an outcome record, and responds.
5. `Socket.HandleMatchupResponse()` validates request ownership, sets `Squad.MatchupStrategy`, and locally sorts visible enemy squads.
6. `MakeMatchupAndGetCommand(target)` constructs the tactical matchup and contextual banned commands, then sends `CommandRequest`.
7. Server `Game.getStrategy()` selects command and shooting strategies and allocates outcome IDs.
8. `HandleStrategicCommandResponse()` validates level/squad, obtains/configures a pooled command, and invokes its typed `Execute` method.
9. Command timers repeatedly move or evaluate the squad. Weapons select targets using the selected shooting strategy.
10. Command finalization records TSV and requeues the squad for another AI choice.

### Trace E: movement with an obstacle and rapid retargeting

1. Squad or Command calls `Ship.MoveToPoint(destination)`.
2. Ship checks bounds and box-casts its footprint. If clear, it sets a direct target.
3. If blocked, Ship clears the old destination and calls `Pathfinder.FindPath`, which assigns a new request ID and snapshots the grid.
4. A free worker runs immediately; otherwise a `PathWaiting` entry is queued.
5. A second order increments the request ID and records the latest pending destination rather than allowing the old result to own movement.
6. Worker posts `PathResult` to `_completedPaths`.
7. `Pathfinder.ApplyCompletedPathResults()` rejects mismatched slot/request results.
8. `Ship.FixedUpdate()` rejects any remaining mismatch, or merges valid points into `DestinationQueue` and follows them.
9. `HandleSupersededPathfindingRequest()` starts a new request for the latest destination.

### Trace F: damage, death, and removal

1. A Weapon fires a pooled Projectile and records damage-in-flight toward the target.
2. Projectile trigger enqueues the Ship; `Projectile.FixedUpdate()` calls `ShipCollision`.
3. Faction/ignore/explosion-once rules pass; `Ship.LogAttackingDamage()` subtracts clamped damage, recalculates target TSV, updates attacker/target statistics and active command TSV.
4. At health zero, target `Ship.Kill(killer, killerFleetShip, killerSavedSquad)` runs once.
5. It drops death visuals, cleans visibility/range state, marks persistent death/stats, removes itself from `GameState` and `Squad`, flags in-flight projectiles, cancels timers/weapons, and deactivates.
6. If the Squad is empty, `Squad.Kill()` finalizes its command, clears selection/UI, removes it from GameState, and outside normal campaign sets GameOver if the side is no longer viable.
7. `Level.Update()` detects GameOver and calls `LevelOver()`.
8. `SaveAndEnd()` kills remaining entities with teardown semantics, persists data/outcomes, and `GameState.Release()` returns deferred objects to pools.

## 6. Domain model and terminology

| Term | Meaning, ownership, and lifecycle |
|---|---|
| Stage | Scene-level host created from `Stage.prefab`; owns Pool, prefab registry, UI/input/audio/cameras, shared path grids, and Levels. Lives for the Unity scene. |
| Level | One arena spawned by Stage; owns options, map/environment, timers, Pathfinder, GameState, server game ID, and mission triggers. Can reset repeatedly in training. |
| GameState | Level-owned authoritative active registry and score/visibility/command bookkeeping component. Reset between rounds. |
| FleetShip | Persistent individual ship record created/owned by FleetData; owns stable ID, type/name, death and lifetime statistics. Runtime Ship mirrors its stats/type. |
| SquadShip | Persistent placement/reference inside SavedSquad; connects a FleetShip ID to formation offset. |
| SavedSquad | Persistent composition and tactical defaults; owned by SavedSquadsData. Converted to a live Squad per level. |
| Squad | Pooled live grouping; owns live Ship list, formation/command/selection state, bans, and target strategy. Removed when empty. |
| Ship | Pooled live combatant; owns health/TSV/motion/weapons and references FleetShip/Squad/Level. Runtime `Id` changes per setup. |
| Side | One-based faction index. `Configuration.HumanSide`, `BeeSide`, `UserSide`, and `AISide` can swap. Never assume enum order equals side. |
| Hive Mind | Active server-backed strategy system. It is not the disabled ML-Agents Brain. |
| Matchup strategy | Server-selected rule for choosing an enemy squad; Unity executes the sorting rule locally. |
| Command | Live squad behavior such as Aggressive, Retreat, Patrol, Mining, Heal, or Hold. A Hive Mind command is a learning sample with outcome IDs. |
| Shooting strategy | Target ordering applied to squad ships/weapons, such as First Seen, Closest, Most Valuable, or type-specific. |
| Matchup string | Compact sorted ship-type and tactical context representation used as server lookup key. |
| TSV | Strategic value, recalculated from live ship condition/value and used as command outcome/score signal. |
| OpponentId | User/opponent dimension passed to server strategy lookup; exact production semantics are not fully documented. |
| Pathfinding request ID | Monotonic per-Pathfinder token proving which asynchronous result belongs to the latest Ship order within one runtime lifecycle. |
| Pathfinding lifecycle ID | Per-Ship token advanced by `Ship.ClearData`; distinguishes work submitted for an earlier use of the same pooled Ship object. |
| Clearance | Ship footprint in scale-4 grid cells; computed from half of the longer collider dimension, rounded up and minimum-clamped. |
| Trigger | Campaign condition/action closure checked periodically; one-shot via `HasBeenTriggered`. |
| ScaledTimer | Level-managed callback timer that respects game time/pause semantics; recurring or one-shot and explicitly canceled. |
| Pool ItemCount/ItemId | Stage-wide runtime identity source; distinct from persistent FleetShip/SavedSquad IDs and server outcome IDs. |
| GameId | Server-side Game key, currently derived from WebSocket connection ID; used for reconnect attempts. |

## 7. Behavioral rules and invariants

The following are precise candidates for automated tests.

1. **Side indexing:** every live side is 1 or 2 before indexing `[side - 1]`. Evidence: GameState arrays, Squad bans, visibility, damage status.
2. **Unique runtime identity:** `GameState.GetId()` increments `Stage.Pool.ItemCount`; no two simultaneously active stage objects may share ItemId/Id. Evidence: `GameState.GetId`, entity setup methods.
3. **Ship registry consistency:** after `Ship.Setup`, Ship is in `GameState.Ships` and `ShipsById`, `FleetShip.IsLoadedIntoLevel` is true, and it is in one Squad after caller completion. After Kill, all reverse conditions hold. Evidence: `Ship.Setup/Kill`, `GameState.AddShip/RemoveShip`, `Squad.AddShip/RemoveShip`.
4. **Squad death:** a live Squad with zero ships must be killed/removed; an empty squad must never be used by Max/Min properties. Evidence: `Ship.Kill`, `Squad.Kill` and calculated properties.
5. **Ordinary victory:** a side is defeated if it has zero ships or zero mobile ships. Evidence: `GameState.IsSideKilled`.
6. **Campaign authority:** normal campaign missions do not set GameOver solely in `Squad.Kill`; their trigger graph must set terminal state. Evidence: `Squad.Kill` mode guard and `LeveLTriggers` terminal actions.
7. **One active command:** if `Squad.HasCommand` is true, `GetCommand()` is non-null, live, and belongs to that Squad; finalization clears both and queues release exactly once. Evidence: `Command.Setup/Finalize`, `Squad.SetCommand`.
8. **Outcome ownership:** a Hive Mind command with positive OutcomeId has exactly one `StoredCommand` index until it is stored; finalization updates that entry. Evidence: `GameState.AddCommand`, `Command.Finalize`, `StoreCommands`.
9. **AI visibility:** AI target selection may only use live ships in the union of its Hivemind visibility sets; the user side with a player sees all enemy squads by design. Evidence: `GameState.GetShipsVisibleToHiveMind/GetSquadsVisibleToHiveMind`.
10. **Late server response:** a response may mutate a squad only if its standing request exists, the request still owns the same squad identity, and the level is not ended. Evidence: Socket response handlers and request `HasSameSquad`.
11. **Path ownership:** a path result may affect a Ship only if the worker slot still contains the same Ship reference, request ID, and lifecycle ID, and the Ship's current request and lifecycle IDs equal the result. Consuming an owned result must release all worker-slot ownership fields whether the result is accepted or rejected. Evidence: `Pathfinder.ApplyCompletedPathResult`, `Ship.FixedUpdate`, `Ship.ClearData`.
12. **Movement clearance:** path nodes and diagonals must satisfy hard ship clearance; preferred clearance affects cost but may not override hard collision. Evidence: Pathfinder search/corner/clearance methods.
13. **Pool round trip:** every enum value obtainable from a pool must return to the same pool after Kill; no previous-use collision queues, hit history, harmless state, or timers survive reuse. Projectile pooled state must be cleared before new-use values are assigned, and repeated Kill must not return the same instance twice. Evidence: `Pool.GetProjectileFromPool/ReturnProjectileToPool`, `Projectile.Setup/ClearData/Kill`, derived projectile cleanup, and `ProjectilePoolTests`.
14. **Damage clamp:** health never falls below zero; damage and TSV attribution use at most remaining health. Evidence: `Ship.LogDamage/LogAttackingDamage`.
15. **Explosion uniqueness:** a RocketExplosion damages a given Ship at most once per activation and not after `IsHarmless`. Evidence: `_shipsHit`, `HasHitShip`, `ShipCollision`.
16. **Fire Tank neutrality:** FireTankExplosion ignores faction friendliness and can damage either side; other explosions retain their rules. Evidence: `RocketExplosion.ShipCollision`.
17. **Projectile cleanup:** killing a projectile removes its damage-in-flight entry, removes it from shooter if applicable, removes it from GameState, deactivates it, and returns it once. Evidence: `Projectile.Kill`.
18. **Persistence reference integrity:** every SquadShip FleetShip ID resolves in the active FleetData before level construction. Evidence: `SquadShip.GetFleetShip`, `LevelConstructor`.
19. **Settings/data gate:** gameplay scene finalization occurs only after all three global settings loads and all eleven user-data loads are complete: progress, user settings, three fleets, three saved-squad sets, and three level-data sets. Evidence: `ConfigData.AreAllSettingsLoaded/IsAllUserDataLoaded`, `Scene.Update`.
20. **Teardown mutation safety:** any loop that kills active registry items must snapshot (`ToArray/ToList`) or remove from the head deliberately. Evidence: `SaveAndEnd`, `ResetLevel`.

## 8. Testing guide

### Current state

Unity Test Framework 1.7.0 is installed. The project has separate `Bees.EditModeTests` and `Bees.PlayModeTests` assemblies. Because production scripts remain in predefined `Assembly-CSharp`, the initial suites use a test-only reflection adapter rather than forcing an immediate assembly migration. As of 2026-08-02, `BeesFoundation` passes 49/49 EditMode tests and `BeesPlayModeFoundation` passes 4/4 PlayMode tests. Coverage now includes timers/state reset, ship/squad/command release lifecycle, socket response ownership, command outcome indexing, persistence golden cases, campaign dispatch/data contracts, projectile damage policy, exhaustive projectile and runtime-command pooling, deterministic replay primitives, path ownership, a real-worker overlap/reuse scenario, and a 100-cycle pooled lifecycle soak. The opt-in `BeesPerformanceQualification` category passes 1/1 with its current single-worker open-grid pathfinding gate; see `docs/TESTING.md` and `docs/PERFORMANCE_QUALIFICATION.md`.

### Unit-testable components

- Matchup string sorting and comparative-health bucketing (`Squad.AddToMatchup`, extracted builder logic).
- `MatchupStrategy.SortSquads` and weapon shooting-strategy ordering with deterministic fixtures.
- TSV/firepower/geometry helpers in `Utilities` and `FleetShip`.
- Fleet/SavedSquad/LevelOptions JSON round trips and cloning.
- `ScaledTimer` cancellation/recurrence/pause behavior.
- Pathfinder grid transforms, clearance, search, diagonal constraints, nearest destination, and smoothing after collider sampling is separated.
- Server `Game` selection/outcome aggregation with a fake database and deterministic RNG; server request dispatch with a fake WebSocket.

### Integration boundaries

- **Persistence repository:** DataFile ↔ local filesystem or fake socket.
- **Spawn lifecycle:** LevelConstructor ↔ Pool ↔ GameState ↔ persistent models.
- **Command lifecycle:** fake strategy response ↔ Socket handler ↔ pooled Command ↔ Squad ↔ StoredCommand.
- **Combat lifecycle:** RangeCollider/Weapon ↔ Projectile ↔ Ship/Squad/GameState.
- **Pathfinding concurrency:** Ship order ↔ worker completion ↔ main-thread apply.
- **Scene bootstrap:** socket/settings/user-data readiness ↔ Scene finalization.

### End-to-end PlayMode scenarios

1. FreePlay minimal human-versus-bee battle: load, spawn, select, issue move/attack, kill side, show summary, cleanup, restart.
2. Server disconnect/reconnect during outstanding setup and command requests; no double command and Level resumes only after reconnect.
3. Every campaign mission ID reaches each win/loss branch with dialogue automatically advanced.
4. Mining victory/continuation: asteroid depletion, minerals and TSV, command finalization, save/reload.
5. Obstacle course with rapid repeated drag destinations and dynamic asteroids; final destination wins and no stale path is applied.
6. Pool soak: hundreds of short rounds with object counts, registry counts, timers, and references returning to baseline.

### Determinism opportunities

`SimulationReplayRandomScope` now seeds both `System.Random` through `Utilities` and `UnityEngine.Random`, restoring both afterward. `SimulationReplayTrace` stores a version, seed, and ordered fixed-step events with opaque payloads suitable for user inputs and server responses. These are primitives, not a connected replay pipeline: capture still must be wired into input/socket boundaries and playback into an explicit fixed-step driver. Physics remains order-sensitive, so deterministic scenarios should use fixed timestep, explicit simulation, and controlled collider activation.

### What to mock/replace

- Socket/WebSocket and server time.
- MySQL database and filesystem cache on Node.
- DataFile storage.
- Unity static Input (wrap it) and audio.
- Random source and time provider.
- Pool/spawn factory for pure LevelConstructor tests.

### State reset checklist

Reset `ConfigData` loaded flags/data/current mode/current ships/level options/scenes/socket collections/hashes; `Pool.ItemCount`; every GameState collection; Level timers/triggers/pathfinder workers; static utility scratch state if added; Time scale; Physics2D simulation; and server caches/pending requests/games. Verify no worker task from the previous test remains capable of publishing into a reused Ship.

### Prioritized first tests

1. **P0 – projectile setup/kill reuse matrix (protected).** All 15 real prefabs now run through two complete lifecycles, including derived reset state, GameState and shooter symmetry, Rocket/StrikerBomb timers, BeamCannon ownership, FireBargeExplosion registration, idempotent Kill, exactly one return, and same-instance reuse. The fixture reproduced and now protects the setup-order defect that cleared LaserBeam's newly assigned angle.
2. **P0 – ship/squad/GameState lifecycle.** Spawn one squad, kill ships in different orders, assert registries, loaded flags, selection, command, and deferred releases.
3. **P0 – stale path result/rapid drag (protected).** The focused ownership suite rejects request A after B is issued, rejects an old lifecycle after pooled reuse even when numeric request IDs collide, preserves current queued work behind an old lifecycle entry, and verifies slot cleanup. A full-grid PlayMode scenario now overlaps real worker tasks and verifies newest-request and pooled-lifecycle ownership.
4. **P0 – socket late/duplicate response (protected).** Old squad ItemId, ended level, duplicated hash, response-type mismatch, and missing standing requests cannot claim or execute a command.
5. **P0 – command finalization/outcome indexing (protected).** Duplicate IDs are rejected atomically; valid finalization updates exactly its stored outcome and queues one release; missing/stale mappings fail explicitly rather than corrupting another record.
6. **P1 – damage/friendly-fire matrix (eligibility protected).** Pure policy coverage protects enemy damage, ordinary friendly immunity, Fire Tank neutrality, Fire Barge special rules, harmless/dead/repeated hits. Full health/TSV/stat integration remains.
7. **P1 – setup/teardown soak.** Repeated Level Setup/SaveAndEnd keeps counts and timers stable.
8. **P1 – persistence golden round trips (core cases protected).** Empty fleet/squad, escaping, invariant-culture level data, and malformed replacement rollback are covered. Full progress/settings documents, legacy versions, and missing references remain.
9. **P1 – side-elimination semantics.** Mobile ship, only immobile structures, simultaneous deaths, and campaign override.
10. **P1 – server cache expiry.** Fresh entries remain; expired entries are removed. Current server condition fails this expected invariant.
11. **P2 – formation compression/bounds.** Large squads, narrow maps, reinforcement positions, immobile members.
12. **P2 – all campaign terminal branches.** One scenario per mission ID and major quest branch.

## 9. Bug-investigation guide

### Confirmed defects resolved after this review

1. **Server cache cleanup comparison was reversed.** At review time, `runCacheClean()` deleted when `time - age < maxCachedStrategyAge`, removing fresh entries and retaining expired ones. It was corrected on 2026-08-02 to delete only when age exceeds the maximum. The external `BeesServer` repository's Git diff contains only the three cache comparisons. A standalone Node regression test is still needed because the monolithic server file starts database/network services when loaded.
2. **Reconnect response assigned through the wrong Level field.** At review time, `Socket.HandleReconnectLevelResponse()` set `_setupLevel.ServerGameId` instead of the reconnect request's Level. It was corrected on 2026-08-02 by applying the response to `handleReconnectLevelResponseLevel` through a deterministic helper. `SocketReconnectTests.ApplyReconnectResponseUpdatesTheReconnectedLevel` now protects connection state, game ID, and handled-request ownership.
3. **Projectile friendly-fire ownership used an unset source.** `Projectile.ShipCollision` and `RocketExplosion.ShipCollision` compared the target to the projectile component's `Side`, but `Projectile.Setup` does not assign that field. Eligibility is now centralized in `ProjectileDamagePolicy` and uses `Shooter.Side`; Fire Barge and Fire Tank exceptions are explicit matrix cases.
4. **A one-logical-processor machine configured zero path workers.** `Scene.Start` assigned `SystemInfo.processorCount - 1`, while `Pathfinder` sizes all worker arrays and dispatch loops from `ConfigData.MaxThreads`. It now clamps the worker count to at least one. The performance qualification deliberately runs with one worker.
5. **Several persistence writers produced invalid or locale-dependent JSON.** Empty FleetData/SavedSquad values, escaped ship/level names, and comma-decimal cultures had failing golden reproducers. Those writers now use Newtonsoft JSON objects/arrays, and `DataFile.SetContents` commits only after successful deserialization.
6. **Deferred release queues could release the same object again.** `GameState.Release` returned queued objects without clearing the queues. It now drains each list before returning its snapshot to pools.

### Suspicious code and architectural risks

| Risk | Likely symptom | First investigation point |
|---|---|---|
| Multiple active registries and reverse sets | Ghost targets, selected dead squads, damage attribution to reused object | Ship/Squad Kill, RangeCollider exits, GameState collections |
| Pool reset is manual | State from prior life: old target, color, timer, collision hit set | Type-specific `ClearData/Deactivate` and pool callback |
| Async path tasks plus pooled Ships | Ship moves to an old destination after respawn; worker slot remains stuck; a reused Ship never starts its new queued path | inspect request/lifecycle IDs, `ApplyCompletedPathResult`, `PathsWaiting`/`ShipsQueued`, and `Ship.ClearData`; run `PathfinderResultOwnershipTests` plus the lifecycle soak |
| Socket static lifetime and global dedupe | Responses ignored after reset or requests resent into wrong scene | `HandledRequests`, per-Level handled set, `ResetGameData`, `OpenLevels` |
| Pause depends on network/setup | Frozen battle with UI responsive, timeouts not advancing | `Level.Update`, `IsLevelConnectedToServer`, NetworkDisconnection |
| Campaign closure graph | Mission never advances or completes twice | current mission method, Trigger/NextTriggers, captured flags |
| Randomness everywhere | Non-reproducible formation, target, map, asteroid failure | Utilities random calls and server `Math.random` |
| Client/server duplicated strategy schema | Unknown/banned command, wrong type-specific targeting | ConfigData/Utilities mappings vs server arrays |
| Persistence has no schema migration | Old save fails or silently maps enum to wrong type | compact JSON loaders and enum ordering |
| UI indexes/concrete references | Null/index errors only in one scene variant | Stage serialized overrides and `UIElements` access |
| Dynamic obstacle/path grid staleness | Collision despite valid path or oscillating replans | `UpdateDynamicObstacleLayer`, dirty flags, box casts |
| Damage-in-flight bookkeeping | AI refuses target or over/underestimates lethal damage | `ShipDamageStatus`, Projectile Kill/target changes |
| Server pending request retained on exception | Request hash permanently considered pending, no response | every `handleMessage` catch branch and `pendingRequests.delete` |
| Server authentication/origin absent | Unauthorized data/strategy access | `originIsAllowed()` always true; client-supplied user IDs |
| Hard-coded server database credentials | Credential exposure and difficult rotation | Server constructor; move to environment/secret store (do not copy credential into logs/docs) |
| 1 GiB WebSocket limits | Memory exhaustion from a client message | server WebSocket construction |
| Shared level data forced to user 2 | Edits affect all users or duplicates accumulate | `User.determineUserId/storeData` |
| Disabled tester pause input | Cannot resume by expected key | `LevelInputManager.HasPauseInput` |
| Stale comments/docs | Incorrect timing/feature assumptions | Trigger precision comment, training names, CLAUDE line counts |

When debugging, capture four identities together: persistent FleetShip/SavedSquad ID, runtime Ship/Squad ItemId, request Hash, and server OutcomeId/GameId. Most stale-state failures are impossible to distinguish with only a GameObject name.

## 10. Feature-development guide

### Add a new ship type

Update `ConfigData.ShipTypes/ShipTypeLetters` and every faction/category set; Utilities name/letter/side mappings; `ShipStats` data and server-provided settings; ship subclass/prefab/colliders/weapons/visuals; `Prefabs` references/conversion dictionaries; Pool typed fields/setup/create/get/return/prewarm; `LevelConstructor.InstantiateShip`; Squad Maker/Codex/UI sprites; targeting type strategies on both Unity and server if addressable; persistence compatibility; remains/explosion/minimap/fog layers. Tests: pool reuse, spawn/kill, stats/TSV, faction targeting, path clearance, persistence round trip, and specialized behavior.

### Add a new ship behavior or tactical command

For a local/user-only behavior, add a Command subclass and action/input/UI integration. For a Hive Mind behavior also update `CommandTypes`, Utilities name mappings, all pool switches, contextual bans, `Socket.HandleStrategicCommandResponse` setup and typed execute dispatch, Node `possibleStratTypes`, database strategy records/schema expectations, storage, and timeout/finalization. Test substitution rules, no-enemy behavior, command replacement/finalization, server banned strategy, and pooling.

### Add a new level or campaign mission

For a normal battle, add/serialize a `LevelOptions` record and any map/obstacle resource. For campaign, also add the next ID dispatch in `LeveLTriggers.SetTriggers`, a mission method, intro JSON/portraits, progression/unlock behavior, and Squad Maker level UI. Avoid relying on list position if a stable ID exists. Test win, loss, retreat, dialogue skip, restart, save, and missing required fleet composition.

### Add an environmental obstacle or hazard

Choose static instantiated geometry, pooled dynamic Obstacle, or MapObject. Set tags/layers/Physics2D matrix; implement State registration and teardown; integrate Pathfinder static/dynamic sampling and dirty marking; define projectile/ship collision and faction rules; add prefab/resource references and level options. Test different ship clearances, rotations, map edges, repeated collisions, teardown, and no-render training.

### Add a level objective

Current extension point is campaign `Trigger` graphs and `GameState` counters. Define the authoritative completion state, its UI representation, persistence/progression effect, and interaction with side elimination. Do not set only `WinningSide`; terminal code expects `GameOver`. Test simultaneous completion/failure and delayed callbacks after end.

### Add a statistic or metric

Decide whether it is per-live-entity, per-battle, persistent per FleetShip/SavedSquad, user progression, debug-only, or server operational telemetry. Update the one authoritative owner, all damage/death/end branches, compact JSON serialization/loading, defaults/migration, mirrored persistence, UI, and server schema if applicable. Test combat death versus teardown, spawned negative-ID ships, beacon exclusions, and repeated saves.

### Add a UI visualization

Add the prefab and serialized Stage/GameMenus reference, then expose data through a read-only view model if possible rather than mutating GameState from the widget. Define behavior for training/no-render, scene variants, pause/disconnect, selection changes, and pooled entity death. Test missing/empty data, resolution variants, and scene load/unload.

### Add a genetic trait or training signal

**Current architecture warning.** No genetic system exists, and ML-Agents Brain execution is disabled. The active trainable signal is command TSV sent to Node. For a Hive Mind signal, update `StoredCommand`, damage/mining attribution, request DTOs, server outcome tables/aggregation/selection, and backward compatibility. For neural training, first restore a coherent Agent-based Brain, observations/action spec, prefab BehaviorParameters/model wiring, group registration/reward/end semantics, and a reproducible training scene; historical ONNX/checkpoints are not enough. Keep the two learning systems explicitly separate.

## 11. Open questions

1. **Which serialized Stage scene is the production battle source of truth?** Several Stage variants differ materially, and scene names conflict with some flag values. This affects every runtime assumption and release test matrix.
2. **Is server availability intentionally mandatory for all gameplay, including local storage and user-controlled battles?** `Level.Update` pauses while not connected/setup, even though much simulation is local. An offline-mode answer changes Scene/Level/Socket architecture.
3. **What is the intended durable identity of a server Game?** Current ID is tied to a socket connection nonce and reconnect may create a replacement. This affects outcome continuity and multi-level stages.
4. **What exactly is `OpponentId`?** It is sent into matching/outcome queries but user-facing matchmaking semantics are not documented. This affects learning isolation and database indexes.
5. **Should Hive Mind strategies generalize across opponents?** Server consolidation comments say opponent ID is currently ignored in consolidated results. The statistical meaning needs an explicit decision.
6. **Are immobile-only sides intentionally defeated?** `IsSideKilled` says yes, so Warp Gates/Beehives alone cannot continue. This matters for retreat/heal/objective missions.
7. **Should mining delay battle completion?** `Level.Update` contains a commented condition that would allow mining to continue after GameOver. Current behavior ends immediately. This affects resource economy and AI reward.
8. **Are enemy reinforcements intentionally disabled?** Data/UI and code exist, but `RandomizeOptions` hard-disables the branch. Removing `&& false` would expose unverified lifecycle paths.
9. **What is the intended ML-Agents status?** Historical models/configs exist but live integration is commented. Delete/archive or restore it; leaving flags and assets suggests functionality that source does not provide.
10. **What is the version contract?** Client `ConfigData.Version` is 5, server setup assigns request Version 6, and settings use name/version lookup. This affects compatibility and dead-version behavior.
11. **Should campaign/challenge level data be globally shared via user 2?** Server comment says it should not remain. The answer affects authoring, security, and migrations.
12. **Are campaign missions 9-11 intentionally unavailable in persisted level data?** Their complete trigger/setup methods exist and are catalogued, but `campaign_levels.json` stops at ID 8. This affects whether the three Uranus missions should be authored, hidden as unfinished, or removed from current progression.
13. **Are server security controls provided upstream?** The inspected process accepts every origin, appears to trust client user IDs, allows huge messages, and contains database connection material. A reverse proxy could mitigate some issues, but no evidence was in scope.
14. **Should a server-returned banned/unknown strategy be rejected and re-requested?** Unity currently logs a banned command and continues. This affects protocol error handling.
15. **What guarantees the Fire Tank explosion's synthetic Shooter/FleetShip/SavedSquad references?** Neutral damage still flows through attacker-based `LogAttackingDamage`; the intended attribution for environmental kills should be specified.
16. **Can a Stage legitimately contain more than one Level in production?** Layouts support 1/2/4/8/12/16 and server collections are multi-level, but most UI/input/camera code targets only `PrimaryLevel`. This determines whether multi-arena support is current or experimental.

## 12. Reference index

### System → key files

| System | Key files |
|---|---|
| Bootstrap/global state | `ConfigData.cs`; `Scenes/Scene.cs`; `Settings/*` |
| Scene/mode flow | `LoadingScreen.cs`; `MainMenu.cs`; `SquadMaker.cs`; `Stage.cs`; `EditorBuildSettings.asset` |
| Arena/world | `Levels/Level.cs`; `LevelConstructor.cs`; `Data/LevelOptions.cs`; `UI Components/Map.cs` |
| Runtime registry/lifetime | `GameState.cs`; `Pool.cs`; `Entity.cs` |
| Squads/orders | `Squad.cs`; `LevelInputManager.cs`; `SquadActionBox.cs`; `Levels/Commands/*` |
| Ships/combat | `Entities/Ships/Ship.cs`; specialized ship classes; `Weapons/*`; `Projectiles/*`; `ProjectileDamagePolicy.cs` |
| Navigation | `Pathfinder.cs`; `Ship.MoveToPoint`; obstacle classes/prefabs |
| Environment/mining | `CollisionAsteroid.cs`; `MiningAsteroid.cs`; `StaticObstacle.cs`; `MapObject.cs`; `Commands/Mining.cs` |
| Campaign | `CampaignMissionCatalog.cs`; `LeveLTriggers.cs`; `Trigger.cs`; `Zone.cs`; `CutsceneManager.cs`; campaign JSON/resources |
| Persistence | `Data/UserData.cs`; `Data/DataFile.cs`; data models; `Ships.cs` |
| Replay/determinism | `Levels/SimulationReplay.cs`; `Utilities.UseDeterministicRandom` |
| Performance qualification | `Tests/PlayMode/PerformanceQualificationTests.cs`; `docs/PERFORMANCE_QUALIFICATION.md` |
| Unity transport | `Server/Socket.cs`; request/response DTOs |
| Node strategy/data service | `F:\RLDemo\BeesServer\siServerDev.js` |
| Debug/metrics | `DebugLogger.cs`; `__` fields; server `SocketRequest.logStats` |
| Disabled neural experiment | `Entities/Ships/Brain.cs`; `Training/results/*`; Brain prefabs |

### Important class → responsibility

| Class | Responsibility |
|---|---|
| `Scene` | Socket pumping, reconnect UX, settings/data readiness, finalization gate |
| `Stage` | Shared battle-scene services and Level hosting |
| `Level` | Arena setup, timers, objectives, reset/save/end, environment |
| `GameState` | Authoritative active-state registry and deferred release |
| `LevelConstructor` | Convert persistent squads/fleets into positioned runtime entities |
| `Pool` | Typed ObjectPool construction and routing |
| `Squad` | Group state, formation, selection, commands, Hive Mind request building |
| `Ship` | Live combatant, movement/path handoff, damage/death/stats |
| `Weapon`/`Turret` | Range cache, targeting strategy, firing cadence/aim |
| `Projectile` | Collision queue, damage delivery, effect and pool cleanup |
| `ProjectileDamagePolicy` | Pure friendly-fire/explosion eligibility rules |
| `Pathfinder` | Clearance-aware asynchronous grid search and ownership validation |
| `CampaignMissionCatalog` | Stable campaign ID/name/setup mapping and persisted-data status |
| `SimulationReplayTrace` | Versioned seed and fixed-step external-event record |
| `Command` | Squad behavior lifecycle and learned outcome finalization |
| `ConfigData` | Global schema/configuration/session/persistence registry |
| `DataFile` | Local/server JSON transport abstraction |
| `Ships` | Fleet and saved-squad aggregate service |
| Unity `Socket` | WebSocket lifecycle, request tracking, resend/dedupe, runtime dispatch |
| Node `Server` | Connections, queues, caches, consolidation, database pool |
| Node `Game` | Strategy lookup/selection and outcome persistence for one connection game |
| Node `User` | User document and settings queries |

### Workflow → entry method

| Workflow | Entry |
|---|---|
| Initial load | `LoadingScreen.Start` → `Scene.Start` |
| Settings/user data | `ConfigData.LoadSettings`; `ConfigData.SetupUserData` |
| Battle scene finalize | `Stage.FinalizeSceneWithUserData` |
| Level creation | `Level.Setup` → `Level.SetupLevel` |
| Force spawning | `LevelConstructor.SetupShips/SpawnShipsAndSquads` |
| Ship spawn | `LevelConstructor.InstantiateShip` → `Ship.Setup` |
| Player input | `Stage.Update` → `LevelInputManager.Update` |
| AI cycle | `Level.GetHiveMindCommands` → `Squad.MakeMatchupStrat` |
| Strategy response | `Socket.HandleMatchupResponse/HandleStrategicCommandResponse` |
| Movement | `Squad.Move`/`Ship.MoveToPoint` |
| Path search | `Pathfinder.FindPath` → `BTFindPath` → `ApplyCompletedPathResults` |
| Fire/damage | `Weapon`/`Turret` → `Level.AddProjectile` → `Ship.LogAttackingDamage` |
| Death | `Ship.Kill` → `Squad.Kill` |
| Victory | `Squad.Kill` or campaign Trigger → `Level.LevelOver` |
| Teardown/save | `Level.SaveAndEnd` → `GameState.StoreCommands/Release` |
| User save | `UserData.Save` → `DataFile.WriteData` |
| Server request | Node `SocketConnection.handleMessage` |

### Configuration concept → source of truth

| Concept | Source of truth |
|---|---|
| Build scene order | `ProjectSettings/EditorBuildSettings.asset` |
| Runtime mode flags | Stage prefab plus concrete scene overrides |
| Factions/enums/constants/endpoints | `ConfigData.cs` and server name tables (must agree) |
| Side assignment/user-vs-AI | server-loaded `Configuration`, then `ConfigData.SwapSides`/Stage overrides |
| Ship stats | server-loaded `ShipStats`/`ShipStatBlock` |
| Starting inventories/unlocks | `StartingSettings` and `UserProgressData` |
| Current level selection | carried `ConfigData.LevelOptions`, cloned by `Level.SetupLevel` |
| Map catalog | `ConfigData.Maps`, Prefabs map list, Pool map switches |
| Obstacles | `LevelOptions`, Resources obstacle name, prefabs, Physics2D layers |
| Commands/strategies | ConfigData enums + Utilities mappings + Node arrays/database |
| Persistence backend | `Configuration.UseLocalStorage` and mirror flags |
| User identity | `ConfigData.GetUserId`; server trusts request user ID in inspected code |
| Campaign objective | `LeveLTriggers.SetTriggers` by `LevelOptions.Id` |
| Ordinary victory | `GameState.IsSideKilled` and `Squad.Kill` |

## Architectural findings to carry forward

The most important structural fact is that Bees is not one monolithic simulation loop: it is a Unity-authoritative tactical simulation coupled to a server-authoritative strategy-selection and persistence service. Runtime correctness depends on identity and lifecycle handoffs across persistent objects, pooled Unity objects, asynchronous path requests, WebSocket request hashes, and server outcome IDs.

The largest uncertainties are the intended production scene/configuration, the current status of neural training, exact server opponent/version semantics, multi-Level support, and several intentionally disabled features. These should be resolved as product decisions before large refactors.

The highest-risk areas are pooled-object reset and enum routing, async pathfinding with reuse, client/server schema drift, campaign trigger coupling, global static bootstrap/persistence, and the external server's correctness/security boundaries. The two concrete defects originally identified by this review were corrected on 2026-08-02; the Unity reconnect path has automated coverage, while the server cache path still needs an isolated Node test seam.
