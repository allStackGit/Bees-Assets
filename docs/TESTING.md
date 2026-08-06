# Bees testing guide

Status: expanded automated foundation, 2026-08-02  
Unity: `6000.5.4f1`  
Test Framework: `1.7.0`

## Current test structure

EditMode tests live under `Assets/Tests/EditMode` in the `Bees.EditModeTests` test assembly. PlayMode tests live under `Assets/Tests/PlayMode` in `Bees.PlayModeTests`. Production scripts still compile into Unity's predefined `Assembly-CSharp` assembly.

Unity assembly-definition assemblies cannot directly reference `Assembly-CSharp`. The initial tests therefore use `RuntimeAssembly`, a small test-only reflection adapter, rather than moving the entire production codebase behind an assembly definition. This is intentionally a transitional boundary: it gives the project immediate regression coverage without changing production compilation semantics. New testable subsystems should prefer explicit production assemblies or extracted pure collaborators when that can be done safely.

## Run the foundation suite

Close any Unity Editor instance that has `R:\Bees` open, then run from PowerShell:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode `
  -nographics `
  -projectPath 'R:\Bees' `
  -runTests `
  -testPlatform EditMode `
  -testCategory BeesFoundation `
  -testResults 'R:\Bees\Logs\BeesFoundationEditMode.xml' `
  -logFile 'R:\Bees\Logs\BeesFoundationEditMode.log'
```

Do not add `-quit`. Test Framework 1.6 exits when the run completes; an explicit `-quit` can terminate startup before the tests execute.

Authoritative output is `Logs/BeesFoundationEditMode.xml`. The log also contains ordinary project/import warnings, so test success should be read from the XML test-run totals rather than inferred from Unity's process exit alone.

The same tests are available in the Unity Test Runner under EditMode and category `BeesFoundation`.

The current validated result is 61/61 passing. See `Logs/BeesFoundationEditMode.xml`.

## Run the PlayMode harness

The scene-independent PlayMode harness uses category `BeesPlayModeFoundation`:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode `
  -nographics `
  -projectPath 'R:\Bees' `
  -runTests `
  -testPlatform PlayMode `
  -testCategory BeesPlayModeFoundation `
  -testResults 'R:\Bees\Logs\BeesFoundationPlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesFoundationPlayMode.log'
```

The harness verifies that the runner actually enters PlayMode and that Unity reports destroyed native objects as null after a frame boundary. Unity `6000.4.3f1` cannot run this suite reliably because of Unity issue UUM-140399 in the global Curl request cache; the project is currently validated with `6000.5.4f1`.

The current validated PlayMode result is 4/4 passing. See `Logs/BeesFoundationPlayMode.xml`.

## Run the campaign scenario harness

The deterministic campaign harness uses category `BeesCampaignScenario`:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform EditMode -testCategory BeesCampaignScenario `
  -testResults 'R:\Bees\Logs\BeesCampaignScenarioEditMode.xml' `
  -logFile 'R:\Bees\Logs\BeesCampaignScenarioEditMode.log'
```

`CampaignScenarioDriver` advances an already-created Level's real trigger graph without waiting for the runtime half-second timer and returns a state snapshot after each step. It preserves deferred-trigger semantics and one-shot cleanup. `CampaignObjectiveRules` is the first reusable objective primitive and now owns elimination winner resolution for the completed Pluto 3, Neptune 1, and Neptune 3 trigger graphs.

Only completed persisted missions 0–6 are scenario-enabled. Titania missions 7 and 8 are marked `InDevelopment`; the driver rejects them before calling mission setup, terminal logic, dialogue, persistence, or any trigger action. Missions 9–11 remain excluded because their persisted level records do not exist.

The current harness validates deterministic trigger driving, mission identity, completion-to-terminal authoring paths for IDs 0–6, and user-win, AI-win, simultaneous-wipe, and unfinished-battle elimination rules.

The `BeesCampaignScene` PlayMode category supplies the isolated scene host:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform PlayMode -testCategory BeesCampaignScene `
  -testResults 'R:\Bees\Logs\BeesCampaignScenePlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesCampaignScenePlayMode.log'
```

It loads the real `Space` scene additively for completed mission 2, activates isolation before loading, suppresses socket and `DontDestroyOnLoad` audio bootstrap, disables all serialized Behaviours before the first rendered frame, verifies the real Stage/prefab/pool wiring, creates an isolated mission-tagged Level shell, attaches `CampaignScenarioDriver`, and proves scene/static teardown. Titania IDs are rejected by the host constructor before `LoadSceneAsync`.

This is a scene-bootstrap smoke test, not yet a full Pluto 3 playthrough: it deliberately does not run `Stage.FinalizeSceneWithUserData` or `Pluto3Pushback`, because those paths still require isolated fleet, ship-stat, dialogue, UI, pooling, and persistence fixtures. Those dependencies are the next layer to replace before mission setup can safely execute without touching player data.

## Run the performance qualification

Performance checks are opt-in so the fast regression suites stay practical:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode `
  -nographics `
  -projectPath 'R:\Bees' `
  -runTests `
  -testPlatform PlayMode `
  -testCategory BeesPerformanceQualification `
  -testResults 'R:\Bees\Logs\BeesPerformancePlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesPerformancePlayMode.log'
```

The qualification runs 25 real background searches on an open 64x64 path grid with one worker and 10,000 real `GameState.ResetState` calls. Budgets are 1000 ms for map construction, 250 ms for request p95, and 1500 ms for the reset loop. The current validated result is 2/2 passing. These are CPU regression baselines, not yet evidence for the minimum supported physical PC; run this category on each target hardware tier before release.

## Extended soak and release gate

`BeesSoakQualification` runs 1,000 Honeybee/Squad kill, deferred-release, pool-reacquire, dirty-state injection, and Level-reset cycles. The current validated result is 1/1 passing. This is a lifecycle stress test, not a timed rendered battle soak.

With the interactive Unity Editor closed, the full local release gate is:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File 'R:\Bees\Tools\Run-BeesReleaseGate.ps1'
```

It runs EditMode foundation, the completed-mission campaign scenario category, PlayMode foundation, the isolated campaign scene smoke, performance, soak, and `npm test` in `F:\RLDemo\BeesServer`; waits for Unity; parses authoritative XML; and exits nonzero for failed or missing results. Use `-SkipQualification` for the fast correctness gate or `-SkipServer` when the separate server checkout is intentionally unavailable.

## Foundation coverage

The first suite protects:

- `ScaledTimer.Reuse` clearing per-use state.
- Immediate and canceled timer behavior.
- `GameState.ResetState` clearing runtime indexes, outcome mappings, visibility-derived state, per-round command counters, and derived presence/selection flags.
- A 100-cycle `GameState.ResetState` soak that repopulates state before every reset.
- Ordinary side-elimination semantics: no ships, only immobile ships, and at least one mobile ship.
- Ship and Squad add/remove registry symmetry, persistent `IsLoadedIntoLevel` flags, and deferred-release queues.
- The real `Ship.Kill(..., endKill: true)` cleanup path for a final ship: ship removal, empty-squad death, persistent flag cleanup, and exactly one deferred release for each live object.
- Exhaustive pool routing and two-cycle `Setup`/`Kill` reuse for all 15 current projectile types using the real serialized prefabs. The lifecycle matrix covers ordinary shots, Rocket and StrikerBomb timer cancellation, BeamCannon ownership, FireBargeExplosion registration, GameState/shooter removal, derived hit-history reset, idempotent Kill, exactly one pool return, and same-instance reuse.
- Level teardown removes that level's request hashes from the socket-wide handled-request set while preserving hashes owned by other levels.
- A 100-cycle PlayMode soak repeatedly kills a real Honeybee and its empty Squad, releases both through `GameState.Release`, reacquires the same pooled instances, injects previous-life state, and resets the Level runtime state. Every cycle asserts clean registries, release queues, timers, request hashes, persistent squad references, flags, and stable inactive pool counts.
- Reading configuration no longer opens the game WebSocket. `ConfigData.Socket` is created lazily on first socket use, keeping tests and offline tooling deterministic.
- Reconnect responses updating the `Level` that owns the reconnect request, including connection state, server game ID, and handled-request hash.
- Asynchronous path completion ownership: an older request cannot overwrite a newer request, a result from a previous pooled-Ship lifecycle cannot publish even when its numeric request ID matches, accepted results release their worker-slot ownership, and an old queued lifecycle cannot suppress the reused Ship's new queued request.
- Every `Ship.ClearData` advances the pathfinding lifecycle token and resets request IDs, completion flags, pending destinations, and path values. The 100-cycle PlayMode soak injects previous-life path state before reacquisition and verifies this reset on every cycle.
- Real-worker PlayMode overlap: two actual `Task.Run` searches are issued, only the newest result publishes, and work submitted before `Ship.ClearData` cannot publish into the reused lifecycle.
- Socket responses are claimed once, matched by request hash and response type, and rejected for ended levels, dead squads, or stale pooled `Squad.ItemId` values.
- Command outcomes reject duplicate IDs atomically and finalize only their own valid stored-command mapping; missing/stale mappings fail safely. Deferred release lists drain exactly once.
- Persistence golden cases cover empty fleet/squad data, escaped strings, invariant-culture level JSON, stable enum ordering, JSON booleans, local/server/mirrored routing, and atomic malformed-write rejection without local or simulated-server side effects.
- Campaign IDs, setup methods, and terminal methods are centralized in `CampaignMissionCatalog`; tests verify contiguous unique IDs, persisted data, intro dispatch, and that every persisted terminal path sets `GameOver`. Missions 9-11 are explicitly marked as lacking records in `campaign_levels.json`.
- Real combat integration covers nonlethal and lethal health/TSV, fleet and squad statistics, command TSV, damage status, kills, registries, deferred release, reverse weapon-range cleanup, repeated-death idempotence, and harmless mode.
- Projectile eligibility has a pure friendly-fire matrix for normal shots, Fire Barge friendly damage, Fire Tank neutral hazards, harmless explosions, dead targets, and repeated hits.
- All runtime command types round-trip through their object pools, in addition to all 15 projectile types.
- `SimulationReplayRandomScope` reproduces both random streams and restores them. Live Levels record user commands, movement, and owned Hive Mind responses at `Stage.FixedUpdates`; `SimulationReplayPlayer` preserves same-step ordering and rejects skipped/backward host steps.

The production reset fixes explicitly clear `ShipsById`, `OutcomeIdToPastCommandIndex`, `PlayerVisibleMapObjects`, `FogOfWarVisions`, `UserCommands`, `AICommands`, `HasSelectedSquads`, `HasWarpGates`, `HasBeehives`, `IsPaused`, `GameOver`, and `LevelEnded`.

The reconnect handler now delegates its state mutation to a small deterministic method. This prevents the response from assigning `ServerGameId` through the unrelated cached setup-level field and makes the ownership rule directly testable without opening a WebSocket.

`Level.ResetGameData` now uses `HashSet.ExceptWith` when removing its handled-request hashes. The previous LINQ `Except` call discarded its result and therefore did not mutate the socket-wide set.

The external `BeesServer` repository extracts live cache expiry into side-effect-free `serverContracts.js`. `npm test` runs four Node tests for age direction, the exact boundary, empty caches, and invalid inputs, then syntax-checks `siServerDev.js`. The current result is 4/4 passing without starting database or network services.

## Test isolation rules

- Every created `GameObject` must be destroyed in teardown.
- Do not open sockets, read user saves, or rely on the current scene in EditMode unit tests.
- Reset or avoid `ConfigData` static state unless the test owns the entire process.
- Do not depend on test order.
- Use a category for every suite intended for command-line execution.
- A bug fix should have a failing reproducer before the production change whenever practical.
- Prefer PlayMode tests for physics callbacks, scene setup, and asynchronous frame behavior. Real-prefab tests that do not require a frame may run in EditMode for faster, more reliable feedback.
- Treat XML results and the specific executed-test count as the validation record.

## Remaining qualification gaps

1. A scene-capable campaign runner must still skip dialogue and drive every objective branch end to end; current tests protect authoring and terminal contracts but do not play missions.
2. Add full combat integration assertions for health, TSV, statistics, reverse range sets, simultaneous death, and teardown—not only eligibility and pool lifecycle.
3. Replay capture is live, but event-specific playback adapters and deterministic Unity physics/state snapshots remain.
4. Run on named minimum-spec hardware and add dense-obstacle, battle, rendered UI/GPU, memory-growth, headless-throughput, and timed 30-60 minute soak gates.
5. Server database transactions, WebSocket flows, reconnects, concurrency, and failure recovery need isolated adapters or disposable test infrastructure.

Item 2 now refers only to deterministic simultaneous-kill and representative many-ship scenarios; single-target health, TSV, statistics, reverse-range cleanup, idempotence, and teardown are covered by `CombatLifecycleIntegrationTests`.
