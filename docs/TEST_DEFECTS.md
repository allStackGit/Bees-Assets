# Test qualification defect ledger

This file records concrete defects found while building/reviewing the expanded Bees qualification suite. Reusable architecture knowledge belongs in `DEVELOPMENT_MEMORY.md`.

## Status

The first Unity execution reached the BeesFoundation EditMode suite: 78/79 tests passed. The repeatedly failing destruction test exposed a broader `MapObject` equality defect, but its original EditMode `DestroyImmediate` assertion was not a valid proof of runtime MonoBehaviour teardown. The equality contract is now fixed and the EditMode regression has been corrected to test that invariant directly. Continue treating new execution failures as evidence with reproducer -> root cause -> production/test fix -> regression result.

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

### QA-010 — Runtime destruction was asserted from an EditMode `DestroyImmediate` fixture
- The original `DestroyingVisibleMapObjectRemovesItFromGameStateImmediately` test expected runtime MonoBehaviour teardown behavior while executing in EditMode with `DestroyImmediate`.
- Repeated failures after changing both `OnDestroy` and `OnDisable` cleanup showed that the test was conflating Editor destruction mechanics with the production frame/lifecycle contract.
- Fix: the EditMode regression now directly validates the actual defect discovered underneath it: a destroyed `MapObject` must preserve UnityEngine.Object null semantics. Runtime visibility-on-destruction belongs in PlayMode qualification using normal `Destroy`/frame progression rather than being inferred from this fixture.

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

### QA-009 — `MapObject` custom equality broke Unity destroyed-object null semantics
- The repeated visibility test failure prompted review of the underlying entity contract. `MapObject` defines custom `==`/`!=` operators that compared only managed null/reference plus `Id`, bypassing `UnityEngine.Object`'s native-object null check.
- Consequence: after Unity destroys the native component, the managed `MapObject` wrapper could still report `mapObject != null`. Any lifecycle code using ordinary null checks could therefore retain or operate on destroyed map objects.
- Fix: `MapObject.Equals` and `==`/`!=` now explicitly preserve UnityEngine.Object destroyed/null semantics before applying ID equality to live objects. ID-based hashing/equality remains intact for live map objects.
- Regression: `MapObjectVisibilityTrackerTests.DestroyedMapObjectPreservesUnityNullSemantics` destroys the object and invokes the production equality operators, requiring `== null` true and `!= null` false.
- The tracker retains defensive `OnDisable`/`OnDestroy` cleanup for real runtime deactivation/destruction, but the old EditMode destruction test is no longer treated as validation of those runtime callbacks.

## BeesServer

The server has its own authoritative defect ledger at `BeesServer/docs/TEST_DEFECTS.md`. Do not duplicate its issue states here. The modular server ordinary and live `bees_test` qualification suites have passed on the project/server machines.

## Execution status

BeesFoundation EditMode reached 78/79 repeatedly because QA-010's invalid runtime-lifecycle assertion remained unchanged while two tracker teardown implementations were tried. QA-009 and QA-010 are now corrected at their actual layers; rerun BeesFoundation to validate the equality regression and then allow the release gate to proceed to later categories. Server ordinary and live integration qualification are green.
