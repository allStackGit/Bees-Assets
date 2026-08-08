# Bees development memory

This file records compact, reusable implementation knowledge that is expensive to rediscover. It is not a change log.

## Testing architecture

- Production runtime scripts still compile into Unity's predefined `Assembly-CSharp`. EditMode/PlayMode test assemblies cannot reference it directly, so existing tests intentionally use `Tests/*/RuntimeAssembly.cs` reflection adapters. Do not move production behind asmdefs merely to simplify a test unless that refactor is independently justified.
- Prefer EditMode for deterministic state/lifecycle tests and real-prefab tests that do not require a rendered frame. Use PlayMode for scene bootstrap, async worker completion, Unity destroyed-object semantics, frame progression, physics callbacks, rendering/GPU qualification, and long-running soak workloads.
- Authoritative command-line validation is the Unity Test Framework XML plus the exact executed-test count; Unity process exit/log noise alone is not sufficient.
- The release gate is `Tools/Run-BeesReleaseGate.ps1`; keep slow qualification categories opt-in so the correctness loop remains practical.

## Campaign qualification

- `CampaignMissionCatalog` is the authoritative persisted-ID -> setup/completion/terminal mapping. Automated scenario-ready missions are IDs 0-6; Titania 7-8 are intentionally `InDevelopment`; 9-11 have trigger graphs but no persisted campaign record.
- `CampaignScenarioIsolation` is the process-wide guard for isolated `Space` scene tests. It must be active before scene load so socket/audio bootstrap can suppress persistent/network side effects. Isolation has one owner at a time and should always be released by the scene host that acquired it.
- `CampaignScenarioSceneHost` loads the real `Space` scene additively, disables serialized `Behaviour`s on load, keeps the real Stage/prefab/pool references, creates an isolated Level/State shell, and owns teardown. It intentionally does not call the normal user-data/fleet bootstrap.
- Completed campaign setup methods are strongly coupled to real fleet spawning, command pools, menus, cutscene/dialogue state, camera/UI, and `ConfigData.CurrentShips`. `SavedSquad.GetAliveSquadShips()` resolves its `FleetShip`s through global `CurrentShips`, and `LevelConstructor` then requires full runtime ship stats/pool state. There is no existing isolated `CurrentShips`/fleet fixture. Do not run `CampaignMissionCatalog.Configure` in an automated scene test until that dependency is replaced or explicitly supplied; otherwise the test can read/mutate player data.
- Do not claim an end-to-end campaign test if it only scans source text or executes a parallel test-only mission graph. Full playthrough coverage should share the real objective logic while substituting user-data/UI/network side effects at explicit service boundaries.

## Combat, targeting, and replay qualification

- `CombatLifecycleIntegrationTests` builds deterministic in-memory Stage/Level/GameState/Squad/Ship/FleetShip/SavedSquad/Weapon graphs without opening sockets or reading saves. Extend that pattern for simultaneous-death and many-ship scenarios rather than creating scene dependencies.
- `Ship.LogAttackingDamage` is guarded by `target.Health > 0` before logging/killing. Repeated lethal callbacks should therefore be idempotent for stats and release queues; mass-death tests should assert this invariant rather than adding a second death guard in the test harness.
- `RangeCollider` owns weapon-range enter/exit bookkeeping. Ship exits remove `Weapon.ShipsWithinRange`, set `Weapon.HasCachedChanged`, and remove the reverse `WeaponsThatHaveUsWithinRange` relation while the exiting ship is still alive. Map-object visibility is shared derived state: each `MapObject` tracks the observing `RangeCollider` sources, and `GameState.PlayerVisibleMapObjects` should remove the object only after its final source exits. Setup/destruction clear that source ownership. Multi-producer derived-state sets should not use unconditional symmetric add/remove without ownership/reference counting.
- Enter/exit tests should use multiple simultaneously tracked objects and multiple observing ranges so stale callback fields (`_colliderEnter` vs `_colliderExit`) and missing source ownership cannot hide behind a single-source happy path.
- Live replay input kinds are exactly `user-command`, `user-move`, `hivemind-matchup-response`, and `hivemind-command-response`. User commands encode `squadItemId|CommandType|enemySquadItemId`; user movement encodes comma-separated Squad `ItemId`s plus invariant-culture X/Y; Hive Mind responses should remain opaque server JSON.
- Replay checkpoints must sort squads/ships by stable IDs before serialization. Never use `HashSet`/dictionary iteration order as replay evidence. Useful deterministic checkpoint fields are terminal Level state plus ship health/TSV/death/lifecycle/position/velocity/rotation.

## Pathfinding and performance qualification

- Existing `BeesPerformanceQualification` is deliberately a CPU regression baseline: one path worker, a 64x64 open path grid, 25 real background searches, and 10,000 real `GameState.ResetState` calls. It is not minimum-spec certification.
- Static pathfinding obstacles are authoritative through `GameState.Obstacles`, and geometry is sampled from `Obstacle.ClearanceMappingCollider`. Dense tests must use real `Obstacle` + collider objects; mutating private clearance arrays bypasses the production ingestion path.
- Initial static obstacle ingestion is discovery-driven: `Pathfinder.InitializeMap()` calls `GameObject.FindGameObjectsWithTag("Obstacle")`, then `Obstacle.Setup(Level)`, which obtains IDs through `Stage.Pool`. Dense qualification fixtures therefore need the real `Obstacle` tag and at least a minimal real `Pool`; adding objects only to `GameState.Obstacles` is insufficient.
- Dynamic avoidance is rebuilt from live `CollisionAsteroid`/`AsteroidPiece` objects when `Level.ActivateCollisionAsteroids` is enabled. Production moving obstacles are registered after the base Pathfinder exists; stripped tests should mirror that lifecycle with `GameState.AddObstacle` + `Pathfinder.AddObstacle` instead of exposing a dynamic obstacle to initial static-map discovery. The dynamic layer is cached by `Stage.FixedUpdates`; a movement qualification test must advance that `int` value to force a new layer snapshot.
- Hardware qualification logs should record CPU/core count, RAM, GPU/VRAM/API, resolution, OS, Unity version, and whether Unity was running in batch/headless mode alongside PERF results. Thresholds become minimum-spec evidence only after running on named target machines.
- Long soak/performance tests should track both managed memory and stable pool/runtime baselines. Avoid unrealistically tight absolute memory values; qualification thresholds should catch monotonic growth/leaks and large regressions while remaining usable across supported machines.

## BeesServer qualification

- `BeesServer/siServerDev.js` is an executable monolith: it defines `Common`, `Database`, `SocketRequest`, `SocketConnection`, `User`, `Game`, and `Server`, exports none of them, and ends by instantiating `Server`. Its production classes can still be tested safely by loading the source into Node `vm`, replacing only the final startup statement with exports, and stubbing `mysql2`, HTTP, WebSocket, and timers.
- The production `Database.query` borrows a pool connection, releases it after either SQL success or SQL failure, and rejects pool-acquisition failures. `handleDisconnect` rebuilds the pool on connection-loss errors; inactivity recovery must be tested with the mysql2 error object's actual `code`/`errno` shape.
- `SocketConnection` deduplicates concurrent work using `server.pendingRequests` keyed by request `Hash` before appending to `server.queue`; WebSocket close removes the connection from `server.connections`, and if it owns a Game it marks that Game inactive and timestamps it. `reconnect-level` reuses and reactivates an existing `server.games` entry when available, otherwise creates a replacement Game keyed by the new connection ID.
- `Server.removeOldGames` owns the two-hour expiry policy for inactive Games. Qualification should distinguish inactive-expired, inactive-recent, and active-old entries.
- `Server.consolidateOutcomes` already owns transaction semantics by issuing `START TRANSACTION`, batch DELETE/INSERT operations, then `COMMIT` or `ROLLBACK` through `Database.query`. The `Database` class has no higher-level transaction helper, but rollback/commit behavior is still a real existing contract and can be tested by stubbing `db.query` on the actual Server.
