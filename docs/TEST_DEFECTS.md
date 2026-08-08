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
- Fix: the branch now checks `_colliderExit.CompareTag("Object")` and removes `_colliderExit.GetComponent<MapObject>()`.
- Regression status: test and production fix are committed; execution pending.
- Prevention: enter/exit callback tests should include at least two simultaneously tracked objects so stale callback-local state cannot accidentally satisfy the assertion.

### QA-005 — Hardware qualification used deferred teardown

- Type: Test harness
- Status: Fixed, awaiting Unity run
- Found during: Hardware/memory qualification review
- Symptom: the memory-growth test created its own Level object but used `Object.Destroy` in `finally`, allowing the test to finish before Unity processed destruction and potentially leaking native state into the following qualification test.
- Fix: the test now uses `Object.DestroyImmediate` for deterministic test-owned teardown.
- Prevention: test-owned objects that do not require frame-boundary destruction semantics should be removed synchronously before the test completes.

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

The isolated production-class suite also covers connection release, pool recovery, WebSocket startup/accept/reject, duplicate request-hash concurrency, and connection cleanup. Reconnect/game lifecycle and consolidation transaction tests are being added on the server qualification branch.

## Production defects found during execution

No execution-discovered defects are recorded yet. QA-004 and QS-001 were found by static test-driven audit before the new suites were run. When a production failure is confirmed during execution, record:

1. failing/reproducing test;
2. root cause;
3. production fix/commit;
4. regression result;
5. any reusable lesson copied to `DEVELOPMENT_MEMORY.md`.
