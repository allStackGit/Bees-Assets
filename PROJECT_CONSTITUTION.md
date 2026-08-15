# Bees Project Constitution

This file defines stable project requirements that ordinary fixes, refactors, optimizations, tests, and agent workflows may not silently weaken. If implementation conflicts with these requirements, treat the implementation as defective unless the project owner deliberately changes the project definition.

## Product and behavior

Bees is a real-time 2D fleet tactics game with persistent fleets/squads, campaign consequences, multiple battle modes, server-backed Hive Mind behavior, and substantial Unity scene/prefab authoring. Correctness includes both code behavior and serialized content/lifecycle behavior.

A fix must preserve intended gameplay, campaign progression, persistence, user-facing behavior, and supported content unless the task explicitly changes one of those contracts.

## Non-negotiable engineering contracts

1. **Persistent state is real gameplay state.** Fleet/squad composition, ship losses/stats, campaign progress, mined resources, settings, and related local/server data must not be silently discarded, cross-owned, duplicated, or written to the wrong level/user/mode.
2. **Runtime ownership must remain isolated.** Stage/Level/GameState state, request hashes, object registries, deferred releases, timers, and other per-level/per-lifecycle data may not leak across levels, pooled lifetimes, scene teardown, or reconnects.
3. **Pooled objects start clean.** Reused ships, squads, commands, projectiles, obstacles, timers, pathfinding state, references, and derived collections must not inherit behaviorally relevant state from a prior life.
4. **Asynchronous results require current ownership.** Background pathfinding or other delayed work may publish only to the request/lifecycle that owns it. Old requests, canceled work, prior pooled lifetimes, or older destinations must not overwrite newer state.
5. **Serialized names and runtime lookup contracts are part of code.** Scene, map, prefab, Resources path, enum/location, and serialized-reference changes must preserve the lookup rules used at runtime. In particular, map prefab normalization must remain consistent with the map locations/configuration it is intended to represent.
6. **Campaign identity is multi-source.** Mission behavior is defined by current mission catalog/intro data, runtime level data, trigger/objective logic, exact map/obstacle assets, ship mechanics, spawn geometry, persistence consequences, and UI/dialogue. Do not infer or rewrite campaign intent from one stale data source in isolation.
7. **Network and persistence contracts are cross-repository contracts.** Unity request/response shapes, versioning, request hashes/deduplication, game/level ownership, reconnect state, and storage routing must remain compatible with the actual server contract. Do not change one side speculatively.
8. **Deterministic evidence must remain deterministic.** Replay, scenario qualification, stable snapshots, and tests that depend on ordering must use stable identities/orderings rather than incidental HashSet/dictionary iteration or global cosmetic randomness.
9. **Safety/cleanup may not be optimized away.** Performance improvements must preserve validation, lifecycle cleanup, synchronization/ownership, numerical/gameplay semantics, required content, and the intended default quality. Lower quality may be offered only as an explicit scalable option, not hidden as an optimization.
10. **Intentionally incomplete content is not a defect by itself.** Development-status missions/assets should not be “fixed” by fabricating completion or weakening guards/tests. Preserve explicit in-development boundaries until the content itself is deliberately completed.

## Validation meaning

A green targeted test proves only that its exercised contract passed. It does not prove the whole game is safe. Scene/prefab, lifecycle, network, persistence, asynchronous, performance, and campaign changes require broader evidence appropriate to their impact.

Tests are executable specifications only while they still represent intended behavior. When code and a test disagree, investigate the requirement rather than automatically changing whichever is more convenient.

## Performance objective

Optimize for high and stable frame rate, low CPU/GPU/memory/GC/resource use, and broad low-end hardware compatibility without changing required gameplay behavior or reducing stability. Prefer end-to-end frame-time/resource improvements over microbenchmarks that merely move work elsewhere.

## Change rule

Every reproducible regression should leave the repository with stronger permanent protection than it had before the regression: preferably an automated regression test, plus an invariant or durable-memory update when the root cause reflects a reusable system rule.