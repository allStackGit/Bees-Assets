# Test qualification defect ledger

This file records defects discovered while building and reviewing the expanded qualification suite. It is intentionally separate from `DEVELOPMENT_MEMORY.md`: this is a concrete issue ledger, while development memory stores reusable architecture knowledge.

## Status key

- **Test harness**: problem was in a new/existing test or fixture, not production behavior.
- **Production**: problem exists in runtime/server behavior and should receive a regression test plus production fix.
- **Open**: identified but not fixed.
- **Fixed**: corrected on the qualification branch and statically reviewed; still awaiting execution unless noted otherwise.

## Bees-Assets

### QA-001 — Dynamic obstacle fixture assigned wrong reflection type

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: Dynamic-obstacle qualification review
- Symptom: new test assigned `long` values to `Stage.FixedUpdates`; production field is `int`, so `FieldInfo.SetValue` would throw at runtime.
- Fix: all qualification assignments now use `int`.
- Prevention: verify reflected field types against production declarations during section review.

### QA-002 — Dense obstacle fixture initially bypassed production Pathfinder discovery

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: Dense-obstacle qualification review
- Symptom: obstacles were added to `GameState` but not tagged `Obstacle`. `Pathfinder.InitializeMap()` discovers initial geometry with `GameObject.FindGameObjectsWithTag("Obstacle")`, so the test could have measured an effectively open grid while claiming dense-obstacle coverage.
- Fix: dense obstacles are now real tagged `Obstacle` objects with `BoxCollider2D`/`ClearanceMappingCollider` geometry and a minimal real Stage `Pool` for `Obstacle.Setup`.
- Prevention: performance tests must enter systems through the same discovery/registration path as production.

### QA-003 — Moving asteroid fixture lifecycle did not match production

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: Dynamic-obstacle qualification review
- Symptom: the first fixture created a `CollisionAsteroid` before `Pathfinder` construction. Production collision asteroids are registered after the base path map exists; invoking their normal setup in the stripped fixture would require unrelated `Level.Map`/spawn state and an already assigned Pathfinder.
- Fix: the fixture now constructs the base Pathfinder first, then registers the real `CollisionAsteroid` through `GameState.AddObstacle` and `Pathfinder.AddObstacle`, matching the production moving-obstacle lifecycle while avoiding unrelated spawn/UI dependencies.
- Prevention: distinguish initial static-map ingestion from post-map dynamic obstacle registration.

### QA-004 — Map-object range exit removed the last-entered object instead of the exited object

- Type: Production
- Status: Fixed, awaiting Unity run
- Found during: Combat/range lifecycle audit
- Reproducer: `RangeColliderVisibilityTests.ExitingMapObjectRemovesTheExitedObjectNotTheLastEnteredObject`
- Symptom: the `Object` branch of `RangeCollider.OnTriggerExit2D` checked `_colliderEnter` and removed its `MapObject`, while ship/projectile branches correctly used `_colliderExit`. With multiple objects entering a weapon range, one object's exit could leave the exited object visible and remove a different object from `GameState.PlayerVisibleMapObjects`.
- Root cause: copy/paste variable mix-up in the final `OnTriggerExit2D` branch.
- Fix: the branch now uses the exiting collider and delegates visibility ownership to the exited `MapObject`.
- Regression status: test and production fix are committed; execution pending.
- Prevention: enter/exit callback tests should include at least two simultaneously tracked objects so stale callback-local state cannot accidentally satisfy the assertion.

### QA-005 — Hardware qualification used deferred teardown

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: Hardware/memory qualification review
- Symptom: the memory-growth test created its own Level object but used `Object.Destroy` in `finally`, allowing the test to finish before Unity processed destruction and potentially leaking native state into the following qualification test.
- Fix: the test now uses `Object.DestroyImmediate` for deterministic test-owned teardown.
- Prevention: test-owned objects that do not require frame-boundary destruction semantics should be removed synchronously before the test completes.

### QA-006 — Range visibility regression fixture used the wrong MapObject type/identity

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: RangeCollider regression review
- Symptom: the first regression fixture requested `Assets.Scripts.Entities.MapObject`, but `MapObject` is in the global namespace. It also left every object at default ID 0 even though `MapObject.Equals/GetHashCode` are ID-based, causing two test objects to collapse to one HashSet entry.
- Fix: the fixture now resolves global `MapObject` and assigns distinct IDs to every test object.
- Prevention: reflection fixtures must verify both CLR namespace and any production equality/identity semantics before asserting collection cardinality.

### QA-007 — Shared map-object visibility had no observing-source ownership

- Type: Production
- Status: Fixed, awaiting Unity run
- Found during: Range visibility ownership audit
- Reproducer: `RangeColliderVisibilityTests.MapObjectRemainsVisibleUntilEveryObservingWeaponRangeHasExited`
- Symptom: every weapon range added the same `MapObject` to a global `GameState.PlayerVisibleMapObjects` HashSet, but any one range exit removed it globally. An object observed by two weapons therefore became invisible as soon as the first weapon lost range.
- Root cause: the global visibility set stored visible objects but not which range colliders still owned visibility.
- Fix: `MapObject` now tracks observing `RangeCollider` sources. Range enter adds its source and keeps the object globally visible; range exit removes only that source and removes global visibility only after the final source exits. MapObject setup clears source ownership and destruction clears global visibility.
- Regression status: overlapping-range test and production fix are committed; execution pending.
- Prevention: shared derived-state sets fed by multiple producers need explicit source ownership/reference counting rather than symmetric unconditional add/remove calls.

## BeesServer

### QS-001 — MySQL inactivity recovery checks the wrong mysql2 error property

- Type: Production
- Status: Open; regression committed, production patch pending
- Found during: Database failure-recovery audit
- Reproducer: `Database.handleDisconnect rebuilds the pool after mysql2 inactivity error 4031` in `test/serverRuntime.integration.test.js`.
- Symptom: `Database.handleDisconnect` checks `err.code === 4031`. mysql2 represents the symbolic server error in `code` and the numeric MySQL server error number in `errno`, so a normal inactivity error shaped as `{ code: "ER_CLIENT_INTERACTION_TIMEOUT", errno: 4031 }` does not enter the recovery branch.
- Expected fix: accept `err.errno === 4031` and/or `err.code === "ER_CLIENT_INTERACTION_TIMEOUT"` in addition to `PROTOCOL_CONNECTION_LOST`.
- Regression status: reproducer is committed on `agent/server-qualification-contracts`; expected to fail until the production branch is patched.
- Prevention: integration tests for driver errors should use the driver's actual error-object shape rather than only synthetic scalar codes.

### QS-002 — Completed unknown requests leak their pending hash

- Type: Production
- Status: Open; regression committed, production patch pending
- Found during: request lifecycle audit
- Reproducer: `unknown request type clears its pending hash after responding` in `test/serverLifecycle.integration.test.js`.
- Symptom: the unknown-request branch calls `request.respond(...)` but does not delete `request.params.Hash` from `server.pendingRequests`. The completed hash therefore remains permanently pending, future duplicates are discarded, and the pending map can grow indefinitely with unsupported request types.
- Expected fix: centralize request completion/cleanup so every terminal response path clears the hash exactly once; do not rely on every request-type branch remembering the deletion independently.
- Regression status: reproducer is committed on `agent/server-qualification-contracts`; expected to fail until production is patched.
- Prevention: pending-work ownership should have a single completion/finally boundary rather than distributed branch cleanup.

### QS-003 — Consolidation transaction does not hold one pooled connection

- Type: Production
- Status: Open; connection-identity regression committed, production refactor pending
- Found during: transaction-integrity audit
- Reproducer: `consolidation transaction uses one borrowed MySQL connection from START through COMMIT` in `test/serverTransactionConnection.integration.test.js`.
- Symptom: `Server.consolidateOutcomes` issues `START TRANSACTION`, DELETE/INSERT statements, and `COMMIT`/`ROLLBACK` through `Database.query`. `Database.query` calls `pool.getConnection()` and releases the connection for every statement, so the transaction statements can execute on different pooled MySQL sessions. SQL-order-only tests can pass while the writes are not actually protected by the intended transaction.
- Expected fix: extract/extend `Database` with a transaction primitive that borrows one connection, executes the entire callback through that connection, commits/rolls back there, and releases only after completion. `consolidateOutcomes` should use that boundary.
- Regression status: connection-identity reproducer is committed on `agent/server-qualification-contracts`; expected to fail until production is refactored.
- Prevention: transaction tests must assert connection/session identity, not merely statement ordering.

The isolated production-class suite also covers connection release, pool recovery, WebSocket startup/accept/reject, duplicate request-hash concurrency, connection cleanup, reconnect/replacement-game behavior, inactive-game expiry, and consolidation success/failure SQL paths.

## Production defects found during execution

No execution-discovered defects are recorded yet. QA-004, QA-007, QS-001, QS-002, and QS-003 were found by static test-driven audit before the new suites were run. When a production failure is confirmed during execution, record:

1. failing/reproducing test;
2. root cause;
3. production fix/commit;
4. regression result;
5. any reusable lesson copied to `DEVELOPMENT_MEMORY.md`.
