# Bees test qualification status

Status: expanded qualification suite awaiting first Unity run.

## Purpose

The test program is a release-confidence system, not a coverage-count exercise. It is intended to prove that changes preserve:

1. deterministic runtime behavior and state ownership;
2. pooled-object and teardown lifecycle safety across repeated reuse;
3. combat, campaign, persistence, replay, and server contracts at their real boundaries;
4. asynchronous/pathfinding ownership under overlap and reuse;
5. performance compatibility on supported hardware, including long-session stability.

Fast correctness tests should remain cheap enough to run during development. Slow hardware/performance/soak qualification is deliberately separated so release evidence can be collected without making every edit expensive.

## Previously validated baseline

The baseline documented before this branch is:

- `BeesFoundation` EditMode: 61/61 passing.
- `BeesPlayModeFoundation`: 4/4 passing.
- `BeesPerformanceQualification`: 2/2 passing.
- `BeesSoakQualification`: 1/1 passing.
- BeesServer `npm test`: 4/4 passing.

Those numbers do **not** include the tests introduced on `agent/complete-test-qualification-suite`; the new tests must be run before their counts are recorded as validated.

## New coverage awaiting validation

### Combat scenarios — `BeesFoundation`

`CombatScenarioQualificationTests` extends the existing single-target integration fixture with:

- opposing lethal hits in one deterministic simulation ordering;
- exactly-once kill/stat/release assertions after repeated lethal callbacks;
- a 24-target many-ship lethal sweep;
- final empty-squad teardown and persistent loaded-state cleanup;
- registry and release-queue cardinality checks after mass deaths.

This closes the documented deterministic simultaneous-death/many-ship correctness gap if the tests pass.

### Replay adapters/checkpoints — `BeesFoundation`

`SimulationReplayQualification` and `ReplayQualificationTests` add:

- parsers for the production `user-command` payload;
- parsers for the production `user-move` payload using invariant culture;
- opaque routing for the two Hive Mind response payloads;
- fail-closed behavior for unknown replay event kinds;
- canonical simulation checkpoints sorted by stable Squad/Ship IDs;
- checkpoint coverage for health, TSV, death state, pathfinding lifecycle, position, velocity, rotation, and terminal Level state.

This provides the missing event-specific replay boundary and deterministic state-comparison primitive. It does not yet constitute a full recorded-battle replay host; a future host must feed parsed events back through the real command/socket application boundaries and compare checkpoints during a running simulation.

### Campaign scene isolation — `BeesCampaignScenario`, `BeesCampaignScene`

New tests verify:

- isolation has one exclusive owner and idempotent disposal;
- missions 7–11 cannot acquire a ready-scenario isolation scope;
- every completed mission ID 0–6 can sequentially load and unload the real `Space` scene through the isolated host;
- every load retains real Stage/Prefab/Pool wiring;
- socket and persistent audio bootstrap remain suppressed;
- scene/static isolation is clean after every mission.

This strengthens the real-scene boundary but does **not** claim full campaign playthrough coverage. The mission setup methods still directly require fleet data, ship statistics, command pools, dialogue/cutscene state, menus/UI, camera/input, and persistence. A full objective-branch runner requires those dependencies to be isolated behind testable services or fixtures before `CampaignMissionCatalog.Configure` can safely run end to end.

### Pathfinding/performance — `BeesPerformanceQualification`

In addition to the existing open-grid baseline:

- `DenseObstacleQualificationTests` constructs actual `Obstacle` + `BoxCollider2D` objects and lets the production Pathfinder ingest their `ClearanceMappingCollider`s;
- 20 real asynchronous searches run through a dense 64x64 grid with alternating ship clearances 1 and 3;
- dense setup and p95 request regression budgets are recorded;
- `DynamicObstacleQualificationTests` uses an actual `CollisionAsteroid` with `Rigidbody2D`, verifies its blocked region moves between fixed-step dynamic-layer refreshes, and times 100 real moving-obstacle refreshes.

These are development regression budgets. They are not a declaration of minimum supported hardware.

### Hardware/memory — `BeesHardwareQualification`

`HardwareQualificationTests`:

- emits CPU/core count, RAM, GPU/vendor/VRAM, graphics API, resolution, Unity version, OS, and batch-mode state into the qualification log;
- runs a warmed 10,000-reset memory workload;
- records managed/native before/after values;
- uses broad leak-tripwire limits rather than hardware-specific performance thresholds.

This supplies reproducible hardware context for qualification records and an initial memory-growth gate. Minimum-spec acceptance thresholds still need to be chosen from actual target-machine measurements.

## Commands for the new categories

Use Unity `6000.5.4f1`; close the interactive editor first.

### Fast EditMode correctness

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform EditMode -testCategory BeesFoundation `
  -testResults 'R:\Bees\Logs\BeesFoundationEditMode.xml' `
  -logFile 'R:\Bees\Logs\BeesFoundationEditMode.log'
```

### Campaign deterministic scenarios

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform EditMode -testCategory BeesCampaignScenario `
  -testResults 'R:\Bees\Logs\BeesCampaignScenarioEditMode.xml' `
  -logFile 'R:\Bees\Logs\BeesCampaignScenarioEditMode.log'
```

### Campaign real-scene isolation

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform PlayMode -testCategory BeesCampaignScene `
  -testResults 'R:\Bees\Logs\BeesCampaignScenePlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesCampaignScenePlayMode.log'
```

### Pathfinding/performance regression

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform PlayMode -testCategory BeesPerformanceQualification `
  -testResults 'R:\Bees\Logs\BeesPerformancePlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesPerformancePlayMode.log'
```

### Hardware and memory qualification

Run this category on every candidate minimum-spec machine. `-nographics` is acceptable for the memory/runtime checks, but GPU/render qualification must eventually use a rendered build/test harness.

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' `
  -batchmode -nographics -projectPath 'R:\Bees' -runTests `
  -testPlatform PlayMode -testCategory BeesHardwareQualification `
  -testResults 'R:\Bees\Logs\BeesHardwarePlayMode.xml' `
  -logFile 'R:\Bees\Logs\BeesHardwarePlayMode.log'
```

## Remaining architectural qualification work

Two large items cannot be honestly completed by adding isolated assertions alone:

1. **Full campaign playthroughs.** The real mission setup methods must first receive isolated substitutes/adapters for fleet/user data, UI/dialogue/camera/input, command-pool side effects, and persistence. Once that boundary exists, the scene host can call `CampaignMissionCatalog.Configure` and drive every real objective branch through `CampaignScenarioDriver`.
2. **Rendered 30–60 minute battle certification.** The existing lifecycle soak plus new memory tests cover reuse/leak regressions, but a representative rendered battle needs a deterministic battle fixture/build and target-hardware budgets for fixed-update CPU, allocations, frame median/p95/p99/max, GPU/UI, and memory over time.

The separate BeesServer qualification branch adds isolated tests against the actual production `Database`, `Server`, and `SocketConnection` classes by loading `siServerDev.js` without its final startup statement. It covers DB connection release/failure, pool recovery, HTTP/WebSocket startup, accept/reject behavior, duplicate request-hash concurrency, and connection cleanup. True transaction rollback/commit tests remain blocked because the production Database class currently has no transaction API.
