# Bees development memory

This file records compact, reusable implementation knowledge that is expensive to rediscover. It is not a change log.

## Testing architecture

- Production runtime scripts still compile into Unity's predefined `Assembly-CSharp`. EditMode/PlayMode test assemblies cannot reference it directly, so existing tests intentionally use `Tests/*/RuntimeAssembly.cs` reflection adapters. Do not move production behind asmdefs merely to simplify a test unless that refactor is independently justified.
- Prefer EditMode for deterministic state/lifecycle tests and real-prefab tests that do not require a rendered frame. Use PlayMode for scene bootstrap, async worker completion, Unity destroyed-object semantics, frame progression, physics callbacks, rendering/GPU qualification, and long-running soak workloads.
- Authoritative command-line validation is the Unity Test Framework XML plus the exact executed-test count; Unity process exit/log noise alone is not sufficient.
- The release gate is `Tools/Run-BeesReleaseGate.ps1`; keep slow qualification categories opt-in so the correctness loop remains practical.

## Campaign qualification

- `CampaignMissionCatalog` is the authoritative persisted-ID -> setup/completion/terminal mapping. Automated scenario-ready missions are IDs 0-6; Titania 7-8 are intentionally `InDevelopment`; 9-11 have trigger graphs but no persisted campaign record.
- `CampaignScenarioIsolation` is the process-wide guard for isolated `Space` scene tests. It must be active before scene load so socket/audio bootstrap can suppress persistent/network side effects.
- `CampaignScenarioSceneHost` currently loads the real `Space` scene additively, disables serialized `Behaviour`s on load, keeps the real Stage/prefab/pool references, creates an isolated Level/State shell, and owns teardown. It intentionally does not yet call the normal user-data/fleet bootstrap.
- Completed campaign setup methods are strongly coupled to real fleet spawning, command pools, menus, cutscene/dialogue state, camera/UI, and `ConfigData.CurrentShips`. Do not claim an end-to-end campaign test if it only scans source text; scene-capable coverage needs explicit isolated fixtures for those dependencies.

## Combat and replay qualification

- `CombatLifecycleIntegrationTests` already builds deterministic in-memory Stage/Level/GameState/Squad/Ship/FleetShip/SavedSquad/Weapon graphs without opening sockets or reading saves. Extend this fixture for simultaneous-death and many-ship ordering scenarios rather than creating a second combat harness.
- Replay currently has deterministic seeding for both `Utilities` and `UnityEngine.Random`, versioned ordered traces, fixed-step dispatch, and live Level input capture. Remaining qualification should test event routing plus stable snapshots/checkpoints of simulation-owned state; replay tests must not depend on dictionary/hash-set iteration order.

## Performance qualification

- Existing `BeesPerformanceQualification` is deliberately a CPU regression baseline: one path worker, a 64x64 open path grid, 25 real background searches, and 10,000 real `GameState.ResetState` calls. It is not minimum-spec certification.
- New dense-obstacle workloads must feed obstacles through the real `Pathfinder` obstacle layer (`Obstacle` + collider / production ingestion) rather than directly mutating private clearance arrays, otherwise they do not qualify obstacle-map behavior.
- Long soak/performance tests should track both managed memory and stable pool/runtime baselines. Avoid asserting unrealistically tight absolute memory values; qualification thresholds should catch monotonic growth/leaks and large regressions while remaining usable across supported machines.
