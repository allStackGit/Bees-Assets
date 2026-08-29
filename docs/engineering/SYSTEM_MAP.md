# Bees Engineering System Map

Concise, maintained orientation for coding work. This file is intentionally smaller and more current than `docs/BEES_SYSTEM_DESIGN.md`. The older design review remains useful historical/source-analysis context, but dated claims in it must be revalidated; for example, its original statement that the project had no first-party tests is no longer current.

## Runtime ownership

- `ConfigData` — global configuration, enums, selected mode/data handles, settings, user-data bootstrap, and lazy socket access. Changes have wide reach.
- `Scene` / `Stage` — scene lifecycle, network/data-readiness pump, shared pools/prefabs/UI/input/audio/camera, and one or more Levels.
- `Level` — one battle arena: options, map/environment, timers, server game context, objectives, teardown, saving, and the owning `GameState`.
- `GameState` — authoritative per-Level runtime registries, selections/visibility, commands/outcomes, dynamic state, score/counters, request ownership, and deferred releases.
- `SavedSquad` / `FleetShip` / `SquadShip` — persistent fleet/squad identity and statistics. `DoesBelongToSavedSquad` and `IsLoadedIntoLevel` are derived runtime ownership flags rather than serialized truth.
- `Squad` / `Ship` / `Weapon` / `Command` — pooled runtime lifetimes constructed from persistent data. Their runtime IDs/item IDs are not persistent profile IDs.
- `Pool` plus object `Setup`/`ClearData`/`Kill` paths — object reuse boundary. Reacquired objects must behave as new lifetimes.
- `Pathfinder` plus `Ship` path request state — background path search and main-thread publication. Ship reference, path request identity, and pooled-lifecycle identity all matter.
- `RangeCollider` / `MapObjectVisibilityTracker` — derived range/visibility ownership. One observer/contact exiting must not erase another live source.
- campaign mission catalog/intro/trigger/objective code — campaign identity and terminal behavior; serialized map/obstacle assets and persistence data are part of the same contract.
- `Socket`, `StandingRequestSet`, `SocketResponseLifecycleGuard` and request/response DTOs — transport, resend/deduplication/reconnect, response-status policy and publication into the owning runtime objects.

## Identity boundaries

Do not treat all IDs as interchangeable:

- **account/user identity** — Steam/user profile identity used by Unity and BeesServer; the backend preserves Steam64-sized values exactly rather than through JavaScript `Number`;
- **persistent fleet identity** — `FleetShip.Id`, `SavedSquad.Id`, and `SquadShip.FleetId`; negative IDs identify generated/transient fleet/squad records;
- **runtime pooled identity** — `Squad.ItemId`, runtime `Ship.Id`, command/weapon/object IDs from `GameState`/`Pool`; valid only for the current Level/object lifetime;
- **request identity** — request `Hash`, owned by a standing request on Unity and connection-scoped on BeesServer;
- **learning action identity** — temporary positive `OutcomeId`, owned until durable commit or explicit discard and distinct from database row IDs;
- **database row identity** — physical SQL row `ID`; never use it as the client/server temporary OutcomeId.

When diagnosing stale-state bugs, first identify which namespace should own the mutation and which lifetime the evidence belongs to.

## Important data flows

### Battle

`Scene -> Stage -> Level -> GameState -> persistent SavedSquad/FleetShip -> pooled Squad/Ship -> Weapon/Projectile/Command`

Commands and targeting flow through Squad/Ship/Weapon/Projectile, while terminal state flows back through GameState/Level into persistent statistics/profile data and cleanup.

Delayed projectiles can outlive the command that fired them. Combat attribution therefore carries the originating command OutcomeId so later damage updates the original stored command/shooting outcome rather than whichever command happens to be active at impact.

### Pathfinding

`Ship movement order -> Pathfinder request ID + Ship lifecycle ID -> worker slot/Task.Run -> completed-result queue -> ownership checks -> current Ship path state`

Invalidating a request changes publication ownership; it does not cancel a `Task.Run` already executing. Tracked-target movement, dynamic-asteroid refresh, retry backoff, queued replacement requests and pool teardown are designed around that fact.

Static obstacles form the base clearance layer. Moving collision asteroids are overlaid once per `Stage.FixedUpdates` snapshot. Destructible static-obstacle changes dirty and rebuild the base layer, including all worker copies.

### Persistence/profile

`ConfigData -> UserData/DataFile -> local storage and/or Socket -> BeesServer stored_user_data`

Normal server-backed startup attempts the server read first; missing data and failed reads are different states. The client uses exact settings/version contracts and may rebuild malformed profile documents from current defaults rather than partially applying corrupt state.

Campaign/profile saves can be coalesced through `CampaignCheckpoint`: seven related profile documents are serialized into one reserved `__campaign_checkpoint__` payload, and BeesServer commits the complete profile checkpoint in one transaction. This is the cross-repository atomic profile boundary; ordinary individual file writes still exist for their intended paths.

### Hive Mind request/learning

`Squad matchup construction -> MatchupStrategyRequest/CommandRequest -> Socket standing request -> shared BeesServer connection Game -> strategy response + temporary OutcomeId -> runtime command/shooting behavior -> StoredCommand TSV -> StoreCommands -> durable learning history`

One Unity WebSocket can host multiple Levels that share one backend Hive Mind `Game`; the Unity `Level` remains the local lifecycle owner. Command/matchup responses additionally capture the runtime Squad item ID so a late response cannot mutate a recycled/dead Squad.

Strategic cache availability depends on banned strategies, and corrected targeting/shooting history on BeesServer uses versioned `target-v2:` / `shoot-v2:` key namespaces. Cross-repository changes to matchup construction, banned strategies, response identity or StoreCommands attribution require checking both sides.

### Dedicated Hive Mind training

`Hivemind Training scene + game mode != FishTank -> HiveMindTrainingBootstrap -> Stage training flags -> 16 non-rendered randomized Levels using the real Hive Mind request/learning path`

The same Unity scene can also be player-facing Fish Tank, so the scene name alone does not imply training. Current automated Hive Mind training is separate from the mostly commented historical ML-Agents `Brain` implementation and `Training/trainer_config.yaml` experiment.

### Campaign

`CampaignMissionCatalog + LevelIntro + runtime level data + trigger/objective code + exact map/obstacle prefab + ship mechanics/spawn geometry + dialogue/UI + persistence consequences`

Do not use `SaveData/`, old JSON, trigger source, or an individual prefab as a sole source of mission truth.

### Screen-space UI

`CanvasScaler/root canvas -> ResponsiveScreenLayoutGuard viewport-wrapper repair -> GameHudLayoutGuard semantic gameplay placement -> RootCanvasCompatibilityGuard final ownership-boundary correction`

`ResponsiveScreenLayoutGuard` owns legacy viewport/screen-wrapper geometry and must not move arbitrary nested UI. `GameHudLayoutGuard` owns gameplay semantics: the scoreboard and ordinary Game Speed are pinned flush to their top corners; blank mission-objective panels are suppressed; the Squad Tabs root is made screen-sized and tabs start from the live scoreboard right edge (or the top-left canvas edge), then wrap into additional rows whenever a visible mission-objective panel or narrow viewport reduces the available width; timed mission HUD placement remains mission-aware; and the action box/minimap are pinned flush to the bottom corners. The `Space` scene has historically carried a stale `GameMenus.Scoreboard` reference to the Summary panel, so the guard validates the assigned object's name and resolves the actual live `Scoreboard` when necessary. Scoreboard/mission-panel/tab geometry crosses sibling transforms through world space rather than assuming one object owns another. `RootCanvasCompatibilityGuard` does not position Squad Tabs. It repairs only children that actually represent a viewport: a fixed legacy reference-sized owner or a full-stretch child that essentially fills its parent. Full-stretch inset panels such as the authored main-menu panel retain their offsets instead of being expanded to the parent. The guard still gives taller-display surplus to a dominant fixed-height body when a fixed footer/tool row must remain at the real bottom, clamps whole direct root-canvas interactive islands that are actually outside the canvas, and gives explicit screen-edge navigation controls such as BACK/CONTINUE/SKIP a small rendering margin.

### Squad Maker placement coordinates

`responsive DropZone host -> SquadMakerDragWorkspace fixed 600x340 logical canvas -> Dropper canonical world-offset validation/formation -> SquadShip.Offset persistence`

The responsive DropZone owns available presentation space, not gameplay scale. `SquadMakerDragWorkspace` creates one fixed 600x340 logical placement surface inside that host; a host that is too small may uniformly scale the surface down, but never stretches or changes its logical coordinates. `Dropper` converts pointer screen positions into that logical surface and then into canonical world offsets. Manual placement, proximity checks, snapping, auto-drop formations, loaded squad offsets and formation generation therefore use the same canonical distances regardless of display size. `SquadShip.Offset` remains the persistent world-space contract. `SquadMakerDragWorkspaceResizeGuard` suppresses the legacy resize callback that cleared/rebuilt the current squad, preserves its screen-metric/color-picker refresh responsibilities, and calls `Dropper.RefreshWorkspacePresentation` so resize changes only rendered positions/scales.

## Coordinate-space warning

Gameplay/pathfinding positions are commonly Level-local while Unity 2D physics APIs operate in world space. `PathfinderObstacleScope` performs explicit conversion for pathfinding obstacle sampling, while weapon line-of-fire checks use world-space transforms/physics. Before changing geometry code, identify which coordinate space each API expects.

## Testing architecture

- EditMode tests and PlayMode tests live under `Tests/` and are described by `docs/TESTING.md`.
- Production gameplay still compiles in Unity's predefined `Assembly-CSharp`; test assemblies use `RuntimeAssembly` reflection adapters where direct references are unavailable.
- Prefer EditMode for deterministic state/contracts and PlayMode for scene/frame/physics/async Unity behavior.
- Real-worker pathfinding, dense/static obstacle, dynamic-obstacle, lifecycle soak and hardware/performance qualification live in PlayMode.
- XML test results plus executed-test counts are authoritative for command-line Unity test runs.
- The external BeesServer repository has its own tests; Unity changes that alter its protocol still require cross-repository reasoning even though this repository cannot enforce the server suite by itself.

## High-risk change surfaces

Inspect broader dependencies before modifying:

- `ConfigData`, `Utilities`, `Level`, `GameState`, `Pool`, `Ship`, `Squad`, `Pathfinder`, `Socket`;
- persistent FleetShip/SavedSquad identity versus pooled runtime object identity;
- command/outcome attribution and delayed projectile lifetimes;
- campaign trigger/catalog/level-data and map/obstacle-prefab wiring;
- object pooling and cleanup;
- async/background work and publication ownership;
- reconnect/deduplication/persistence/authentication;
- scene bootstrap and user-data finalization;
- physics/range/visibility ownership and coordinate spaces;
- replay/deterministic evidence;
- performance-sensitive per-frame/per-fixed-step loops.

## Maintenance rule

This map is navigation, not proof. When a task reveals that ownership, call paths, identity namespaces or canonical sources changed, update this file and/or `docs/DEVELOPMENT_MEMORY.md` in the same task. Remove stale statements rather than leaving multiple contradictory maps.
