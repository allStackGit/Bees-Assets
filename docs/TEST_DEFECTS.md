# Test qualification defect ledger

This file records concrete defects found while building/reviewing the expanded Bees qualification suite. Reusable architecture knowledge belongs in `DEVELOPMENT_MEMORY.md`.

## Status

The current Unity qualification run is green: EditMode, PlayMode, and Player tests all passed after the fixes below. No known client qualification defect remains open from this pass.

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
- The original destruction test expected runtime MonoBehaviour teardown behavior while executing in EditMode with `DestroyImmediate`.
- Fix: the EditMode regression now validates the actual equality invariant directly; runtime destruction belongs in PlayMode with normal frame progression.
- Validation: EditMode passed on the completed release-gate run.

### QA-011 — Hardware reset fixture allowed real `Level.Update` on an incomplete synthetic Level
- First PlayMode run produced repeated `NullReferenceException` logs from `Level.Update` line 562 across hardware, soak, worker, performance, and harness tests.
- Root cause: `HardwareQualificationTests` created a synthetic `Level` only to own `GameState`, but left the Behaviour enabled while intentionally omitting Stage/socket/input wiring. Its first `yield return null` allowed production `Level.Update` to execute and contaminate later tests if the log failure interrupted cleanup.
- Fix: disable the synthetic Level immediately after creation. The test continues to exercise `GameState.ResetState` only, which is its intended scope.
- Validation: PlayMode passed on the completed release-gate run.

### QA-012 — Dynamic-obstacle fixture omitted production Level bounds
- `CanOccupyDestination(new Vector2(80, 0), 1)` returned false before testing obstacle occupancy because `Level.MinX/MaxX/MinY/MaxY` were left at defaults.
- Fix: initialize bounds to `[-128, 128]` consistently with the fixture's 256x256 map before creating Pathfinder.
- Validation: PlayMode passed on the completed release-gate run.

### QA-013 — Dense-obstacle qualification requested routes beside/through its own obstacle field
- The first request completed but returned no path. The fixture's variable endpoints were not guaranteed legal once production preferred-clearance buffering was applied.
- Fix: preserve a deliberately wide central corridor in the dense obstacle field and issue all qualification requests through known-valid grid coordinates inside that corridor, while still alternating explicit clearances 1 and 3.
- Validation: PlayMode passed on the completed release-gate run.

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
- `MapObject` custom `==`/`!=` bypassed `UnityEngine.Object`'s native-object null check, so destroyed wrappers could still report non-null.
- Fix: destroyed/null semantics are preserved before applying ID equality to live objects.
- Regression: `MapObjectVisibilityTrackerTests.DestroyedMapObjectPreservesUnityNullSemantics`.
- Validation: EditMode passed on the completed release-gate run.

## BeesServer

The server has its own authoritative defect ledger at `BeesServer/docs/TEST_DEFECTS.md`. The modular server ordinary and live `bees_test` qualification suites have passed on the project/server machines.

## Execution status

- BeesFoundation/EditMode: passed.
- PlayMode: passed after QA-011/QA-012/QA-013 fixes.
- Player tests: passed.
- Campaign scene coverage passed, including sequential missions 0–6 and scene-host rejection of missions 7–8.
- BeesServer ordinary suite: passed.
- BeesServer live `bees_test` integration: passed.

No known defect from this qualification pass is awaiting rerun.