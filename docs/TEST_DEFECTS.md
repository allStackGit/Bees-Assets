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

## BeesServer

No production defects have yet been confirmed during static qualification construction. New isolated production-class tests are designed to detect connection leaks, pool-recovery failures, WebSocket lifecycle defects, and duplicate-request concurrency errors when `npm test` is run.

## Production defects found during execution

No execution-discovered defects are recorded yet. QA-004 was found by static test-driven audit before the suite was run. When a production failure is confirmed during execution, record:

1. failing/reproducing test;
2. root cause;
3. production fix/commit;
4. regression result;
5. any reusable lesson copied to `DEVELOPMENT_MEMORY.md`.
