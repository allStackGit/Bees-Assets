# Bees test qualification status

Status: expanded Unity qualification suite is green on the project machine; BeesServer ordinary and live `bees_test` qualification are also green.

## Purpose

The test program is a release-confidence system, not a coverage-count exercise. It is intended to prove deterministic runtime behavior and state ownership, pooled lifecycle safety, client/server/database contracts, async/pathfinding ownership, and performance compatibility over realistic workloads.

## Validated client qualification baseline

The user has completed the current release-gate run after the qualification fixes. All currently implemented Unity test layers passed:

- EditMode: passed.
- PlayMode: passed.
- Player tests: passed.

This supersedes the earlier pre-expansion baseline counts. Do not use the old 61/61 EditMode, 4/4 PlayMode, 2/2 performance, and 1/1 soak numbers as evidence for the current branch.

## Validated client coverage

### Foundation / combat / visibility

- deterministic simultaneous lethal hits;
- 24-target many-ship lethal sweep;
- exactly-once kill/stat/release behavior;
- final squad teardown and registry/release cardinality;
- actual exiting map object is removed rather than stale callback state;
- multiple weapon ranges/contact colliders retain visibility until the final source exits;
- deactivation/reset visibility ownership;
- Unity destroyed-object null semantics preserved for `MapObject`;
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
- asynchronous dense-grid searches through a guaranteed-valid production-clearance corridor;
- real `CollisionAsteroid` dynamic-layer movement and refresh timing with production map bounds;
- pathfinder worker lifecycle / stale-request rejection;
- repeated level lifecycle kill/release/reuse/reset coverage;
- hardware/environment logging;
- warmed reset-memory workload and broad leak tripwires;
- Player-level qualification execution.

These are regression workloads. Minimum-spec certification still requires target-machine measurements and a rendered long-duration battle.

## BeesServer status

The server qualification branch is `agent/server-qualification-contracts`.

Both the ordinary server suite and the live `bees_test` integration suite passed on the project/server machines. The live suite covers schema/data contracts plus real WebSocket/MySQL flow through test-user read/write, setup, strategy selection, transactional command persistence, post-write reread, disconnect/reconnect, and a second user-data update.

Known production defects found during the audit are fixed there: database inactivity recovery, pending request-hash cleanup, same-session transaction ownership, durable `store-commands` acknowledgements, strategy-read failure propagation, retry-safe/concurrent Game persistence, deterministic test-database selection, hot lookup indexes, and exact unsigned matchup handling.

`bees_test` is always selected in test mode; normal startup continues to use `ram`.

The server includes idempotent schema migrations for the two hot lookup keys:

- `stored_user_data(userId, filename, ID)`;
- `settings(userId, name, version, Id)`.

Production migration remains separate and requires explicit `BEES_ALLOW_PRODUCTION_MIGRATION=1`.

## Remaining architectural qualification

These are not currently known defects and are outside the completed automated release-gate coverage:

1. full end-to-end campaign playthrough fixtures through real mission setup;
2. complete recorded-battle replay host with checkpoint comparison during simulation;
3. rendered 30–60 minute representative battle certification;
4. target minimum-spec CPU/GPU/RAM matrix and final median/p95/p99/max budgets;
5. eventual outcome-table primary-key migration from `INT UNSIGNED` to `BIGINT UNSIGNED` before long-term training volume approaches exhaustion.

## Current evidence

No currently known client/server correctness defect is intentionally left unfixed in the qualification branches. Current automated evidence is green across Unity EditMode, PlayMode, Player tests, BeesServer ordinary tests, and BeesServer live `bees_test` integration. Future failures should be treated as new evidence and added to the defect ledger only after reproducing and classifying them.