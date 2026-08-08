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
- Status: Open; regression committed, staged Database replacement available
- Found during: Database failure-recovery audit
- Reproducer: `Database.handleDisconnect rebuilds the pool after mysql2 inactivity error 4031` in `test/serverRuntime.integration.test.js`.
- Symptom: the embedded `Database.handleDisconnect` checks `err.code === 4031`. mysql2 represents the symbolic server error in `code` and the numeric MySQL server error number in `errno`, so a normal inactivity error shaped as `{ code: "ER_CLIENT_INTERACTION_TIMEOUT", errno: 4031 }` does not enter the recovery branch.
- Expected fix: switch production to the extracted `database.js` module or make the equivalent embedded change; the extracted module accepts `err.errno === 4031` and `err.code === "ER_CLIENT_INTERACTION_TIMEOUT"`.
- Regression status: monolith reproducer is committed and expected to fail until production is switched; `test/database.module.test.js` qualifies the staged replacement.
- Prevention: integration tests for driver errors should use the driver's actual error-object shape rather than only synthetic scalar codes.

### QS-002 — Pending-request cleanup is distributed and leaks hashes on cancellation/unsupported paths

- Type: Production
- Status: Open; regressions committed, SocketConnection cleanup refactor pending
- Found during: request lifecycle audit
- Reproducers:
  - `unknown request type clears its pending hash after responding` in `test/serverLifecycle.integration.test.js`;
  - `queued request from a disconnected socket releases its pending hash when discarded` in `test/serverConcurrency.integration.test.js`.
- Symptom: the unknown-request branch responds without deleting the request hash; separately, `Server.runQueue` discards requests whose connection vanished without releasing the hash. In both cases `server.pendingRequests` permanently retains work that is no longer active, blocking retries and allowing unbounded stale entries.
- Expected fix: give pending work a single completion/cancellation boundary that releases the hash exactly once. Request-type branches and queue-discard paths should call the same lifecycle primitive rather than manually owning map cleanup.
- Regression status: both reproducers are committed and expected to fail until production is refactored.
- Prevention: pending-work ownership should have one terminal cleanup path for success, error, unsupported requests, cancellation, and disconnect.

### QS-003 — Consolidation transaction does not hold one pooled connection

- Type: Production
- Status: Open; connection-identity regression committed, staged Database replacement available
- Found during: transaction-integrity audit
- Reproducer: `consolidation transaction uses one borrowed MySQL connection from START through COMMIT` in `test/serverTransactionConnection.integration.test.js`.
- Symptom: `Server.consolidateOutcomes` issues `START TRANSACTION`, DELETE/INSERT statements, and `COMMIT`/`ROLLBACK` through the embedded `Database.query`. That method calls `pool.getConnection()` and releases the connection for every statement, so the transaction statements can execute on different pooled MySQL sessions. SQL-order-only tests can pass while the writes are not actually protected by the intended transaction.
- Expected fix: switch production to `database.js` and execute consolidation through its `transaction(work)` callback, which borrows one connection for START, all mutations, COMMIT/ROLLBACK, then releases once.
- Regression status: connection-identity reproducer is committed and expected to fail until production is migrated; `test/database.module.test.js` verifies the staged transaction primitive.
- Prevention: transaction tests must assert connection/session identity, not merely statement ordering.

### QS-004 — `store-commands` can acknowledge before persistence and can acknowledge failed writes

- Type: Production
- Status: Open; regressions committed, Game persistence refactor pending
- Found during: command-outcome persistence audit
- Reproducers in `test/serverPersistence.integration.test.js`:
  - `storeState does not report success until matched outcome inserts finish`;
  - `storeState rejects when a matched outcome insert fails`.
- Symptom: `Game.matchUpdatesWithInsertsAndCommit` invokes `insertIntoTable(...)` without awaiting the returned promises. `Game.insertIntoTable` also catches database query errors and resolves instead of propagating them. `Game.storeState` can therefore resolve `true` while the INSERT is still pending, and can still resolve `true` after the INSERT fails; `SocketConnection` then returns HTTP-style status 200 to the game client.
- Expected fix: collect/await all table insert promises, make `insertIntoTable` propagate query failures, and only acknowledge `store-commands` after durable completion. If atomicity across the three outcome tables is required, execute them through the Database transaction boundary.
- Regression status: both reproducers are committed and expected to fail until production is refactored.
- Prevention: persistence acknowledgements must be downstream of awaited durable writes; helper layers must not swallow failures that determine client-visible success.

The isolated production-class suite also covers connection release, pool recovery, WebSocket startup/accept/reject, duplicate request-hash concurrency, connection cleanup, reconnect/replacement-game behavior, inactive-game expiry, and consolidation success/failure SQL paths.

## Production defects found during execution

No execution-discovered defects are recorded yet. QA-004, QA-007, QS-001, QS-002, QS-003, and QS-004 were found by static test-driven audit before the new suites were run. When a production failure is confirmed during execution, record:

1. failing/reproducing test;
2. root cause;
3. production fix/commit;
4. regression result;
5. any reusable lesson copied to `DEVELOPMENT_MEMORY.md`.
