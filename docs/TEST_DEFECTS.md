# Test qualification defect ledger

This file records concrete defects found while building/reviewing the expanded Bees qualification suite. Reusable architecture knowledge belongs in `DEVELOPMENT_MEMORY.md`.

## Status

The first Unity execution reached the BeesFoundation EditMode suite: 78/79 tests passed and one production visibility-lifecycle defect was exposed. QA-009 is fixed on `agent/complete-test-qualification-suite` and awaits rerun. Continue treating new execution failures as evidence with reproducer -> root cause -> production/test fix -> regression result.

## Fixed test-harness defects

### QA-001 — Dynamic obstacle fixture reflection type
- `Stage.FixedUpdates` is `int`; the first fixture attempted to assign `long` values through reflection.
- Fixed by using the production field type.

### QA-002 — Dense obstacle fixture bypassed Pathfinder discovery
- The first fixture populated `GameState.Obstacles` without the `Obstacle` tag used by `Pathfinder.InitializeMap()` discovery.
- Fixed by using real tagged `Obstacle` objects, real collider geometry, and the minimal Stage/Pool path required by `Obstacle.Setup`.

### QA-003 — Moving asteroid fixture used the wrong lifecycle
- The first fixture created a `CollisionAsteroid` before the base Pathfinder existed, unlike production.
- Fixed by building the base map first, then registering the moving asteroid through `GameState.AddObstacle` + `Pathfinder.AddObstacle`.

### QA-005 — Hardware qualification used deferred teardown
- Test-owned Level state could survive into a later test because teardown used `Object.Destroy`.
- Fixed with deterministic `Object.DestroyImmediate` for test-owned objects that do not require frame-boundary semantics.

### QA-006 — Range visibility fixture used wrong type/identity
- `MapObject` is in the global namespace, and its equality/hash are ID-based. The first fixture used the wrong CLR name and duplicate default IDs.
- Fixed by resolving global `MapObject` and assigning distinct production-style IDs.

## Fixed production defects

### QA-004 — Map-object exit removed stale enter object
- `RangeCollider.OnTriggerExit2D` used `_colliderEnter` in the map-object exit branch, unlike the ship/projectile branches that use `_colliderExit`.
- With multiple objects in range, one exit could remove a different object and leave the actual exiting object visible.
- Fixed by owning the actual exiting object/contact and covered by `RangeColliderVisibilityTests`.

### QA-007 — Shared map-object visibility lacked source ownership
- `PlayerVisibleMapObjects` is shared derived state, but each weapon range previously performed unconditional add/remove operations.
- An object observed by multiple ranges could disappear when the first observer exited.
- Fixed with the conflict-free `MapObjectVisibilityTracker` runtime companion plus per-range contact counts. Visibility now survives overlapping observers/contacts, releases on deactivation/final exit, and self-heals across `GameState.ResetState`.
- Regressions: `RangeColliderVisibilityTests` and `MapObjectVisibilityTrackerTests`.

### QA-008 — Unsigned server matchup IDs were represented as signed Unity `long`
- BeesServer matchup identity is unsigned xxHash64 stored/transported as a decimal string and can exceed `long.MaxValue`.
- `CommandResponse` and `MatchupStrategyResponse` exposed matchup IDs as signed `long`, creating a latent overflow/deserialization risk.
- Fixed by storing only matchup-ID fields as exact strings; numeric temporary OutcomeIds remain unchanged.
- Regression: `ServerMatchupIdSerializationTests` uses identifiers above signed 64-bit range.

### QA-009 — Destroyed visible MapObject survived in player visibility
- First BeesFoundation execution: `DestroyingVisibleMapObjectRemovesItFromGameStateImmediately` failed, leaving one destroyed reference in `GameState.PlayerVisibleMapObjects` after `DestroyImmediate`.
- Root cause: tracker teardown relied on normal `HashSet.Remove(_mapObject)` while Unity had already transitioned the component into destroyed-object semantics. `MapObject` equality/hash is ID-based, so teardown-time lookup is not a safe lifecycle boundary.
- Fix: `MapObjectVisibilityTracker.OnDestroy` preserves the existing public HashSet instance, snapshots entries by managed reference identity, clears the set, then re-adds every survivor except the exact object being destroyed. This avoids changing `MapObject.cs` and keeps the Fire Tank branch isolated.
- Regression: existing `MapObjectVisibilityTrackerTests.DestroyingVisibleMapObjectRemovesItFromGameStateImmediately`; rerun pending.

## BeesServer

The server has its own authoritative defect ledger at `BeesServer/docs/TEST_DEFECTS.md`. Do not duplicate its issue states here. The modular server ordinary and live `bees_test` qualification suites have passed on the project/server machines.

## Execution status

BeesFoundation EditMode first run: 78/79 passed, exposing QA-009. QA-009 is fixed and the release gate should be rerun from BeesFoundation so subsequent categories can execute. Server ordinary and live integration qualification are green.
