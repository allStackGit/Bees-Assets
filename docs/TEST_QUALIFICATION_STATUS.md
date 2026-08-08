# Bees test qualification status

Status: expanded client/server qualification suite implemented; awaiting first project-machine execution.

## Purpose

The test program is a release-confidence system, not a coverage-count exercise. It is intended to prove deterministic runtime behavior and state ownership, pooled lifecycle safety, client/server/database contracts, async/pathfinding ownership, and performance compatibility over realistic workloads.

## Previously validated baseline

Before `agent/complete-test-qualification-suite`:

- `BeesFoundation` EditMode: 61/61 passing.
- `BeesPlayModeFoundation`: 4/4 passing.
- `BeesPerformanceQualification`: 2/2 passing.
- `BeesSoakQualification`: 1/1 passing.
- previous BeesServer suite: 4/4 passing.

Those counts do not include the new work below.

## New client coverage awaiting validation

### Foundation / combat / visibility

- deterministic simultaneous lethal hits;
- 24-target many-ship lethal sweep;
- exactly-once kill/stat/release behavior;
- final squad teardown and registry/release cardinality;
- actual exiting map object is removed rather than stale callback state;
- multiple weapon ranges/contact colliders retain visibility until the final source exits;
- deactivation/destruction/reset visibility cleanup;
- unsigned server matchup IDs are preserved as exact strings even above `long.MaxValue`.

Production defects found by this audit are fixed on the branch and recorded in `docs/TEST_DEFECTS.md`.

### Replay

- production `user-command` and `user-move` parsers;
- opaque Hive Mind response routing;
- fail-closed unknown event handling;
- canonical simulation checkpoints sorted by stable Squad/Ship identity;
- health/TSV/death/path-lifecycle/transform/terminal-state checkpoint fields.

A complete recorded-battle playback host is still future architectural qualification work.

### Campaign

- exclusive/idempotent scenario isolation ownership;
- rejection of non-ready mission IDs;
- completed missions 0–6 sequentially load/unload the real `Space` scene through the isolated host;
- real Stage/prefab/pool wiring is retained while socket/audio bootstrap is suppressed;
- clean scene/static teardown after each mission.

This does not yet run every full mission objective path through real mission setup; that still requires explicit substitutes for fleet/user data, dialogue/UI/camera/input, command pools, and persistence.

### Pathfinding / performance / memory

- dense real tagged `Obstacle` ingestion with small/large clearances;
- 20 asynchronous dense-grid searches;
- real `CollisionAsteroid` dynamic-layer movement and refresh timing;
- hardware/environment logging;
- warmed reset-memory workload and broad leak tripwires.

These are regression workloads. Minimum-spec certification still requires target-machine measurements and a rendered long-duration battle.

## BeesServer status

The server qualification branch is `agent/server-qualification-contracts`.

Known production defects found during the static/test-driven audit are fixed there: database inactivity recovery, pending request-hash cleanup, same-session transaction ownership, durable `store-commands` acknowledgements, strategy-read failure propagation, retry-safe/concurrent Game persistence, deterministic test-database selection, and exact unsigned matchup handling.

`bees_test` is always selected in test mode; normal startup continues to use `ram`.

The server now includes idempotent schema migrations for the two hot lookup keys:

- `stored_user_data(userId, filename, ID)`;
- `settings(userId, name, version, Id)`.

`npm run test:live` applies those migrations to `bees_test` before running schema/data-contract and real WebSocket/MySQL integration tests. Production migration is separate and requires explicit `BEES_ALLOW_PRODUCTION_MIGRATION=1`.

The earlier claim that version-5 settings were missing from the supplied dump was retracted after full-file verification. The dump contains settings versions 2–7, including current version 5 and its required serialized fields.

The existing `package-lock.json` predates newly declared `mysql2`/`websocket` runtime dependencies. This environment cannot reach npm's public registry to regenerate the transitive lock graph. Run `npm install` once on the project machine before server qualification; it should regenerate the lockfile correctly.

## Remaining architectural qualification after the first run

These are not currently known defects and should not block executing the implemented suite:

1. full end-to-end campaign playthrough fixtures through real mission setup;
2. complete recorded-battle replay host with checkpoint comparison during simulation;
3. rendered 30–60 minute representative battle certification;
4. target minimum-spec CPU/GPU/RAM matrix and final median/p95/p99/max budgets;
5. eventual outcome-table primary-key migration from `INT UNSIGNED` to `BIGINT UNSIGNED` before long-term training volume approaches exhaustion.

## Immediate next evidence

No currently known client/server correctness defect is intentionally left unfixed in the qualification branches. The next step is the first complete Unity + BeesServer run. Treat any failure from that run as new evidence: reproduce, determine whether it is harness or production behavior, fix it, and retain the regression.
