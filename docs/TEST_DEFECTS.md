# Test qualification defect ledger

This file records concrete defects found while building/reviewing the expanded Bees qualification suite. Reusable architecture knowledge belongs in `DEVELOPMENT_MEMORY.md`.

## Status

All currently known Bees client defects found during this qualification pass are fixed on `agent/complete-test-qualification-suite` and await their first Unity execution. New execution failures should be added here with reproducer -> root cause -> production/test fix -> regression result.

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
- Fixed with the conflict-free `MapObjectVisibilityTracker` runtime companion plus per-range contact counts. Visibility now survives overlapping observers/contacts, releases on deactivation/final exit, removes destroyed objects, and self-heals across `GameState.ResetState`.
- Regressions: `RangeColliderVisibilityTests` and `MapObjectVisibilityTrackerTests`.

### QA-008 — Unsigned server matchup IDs were represented as signed Unity `long`
- BeesServer matchup identity is unsigned xxHash64 stored/transported as a decimal string and can exceed `long.MaxValue`.
- `CommandResponse` and `MatchupStrategyResponse` exposed matchup IDs as signed `long`, creating a latent overflow/deserialization risk.
- Fixed by storing only matchup-ID fields as exact strings; numeric temporary OutcomeIds remain unchanged.
- Regression: `ServerMatchupIdSerializationTests` uses identifiers above signed 64-bit range.

## BeesServer

The server has its own authoritative defect ledger at `BeesServer/docs/TEST_DEFECTS.md`. Do not duplicate its issue states here. The current modular server branch has fixes/regressions for database recovery, request-hash ownership, transaction connection ownership, durable command persistence, strategy-read failures, concurrent/retry persistence, deterministic `bees_test` selection, hot lookup indexes, and exact 64-bit matchup handling.

## Execution status

No production defect has yet been discovered by executing the new qualification suites because the expanded Unity/server suites have not had their first project-machine run. The next evidence source is the user's run; any failures should be diagnosed rather than merely recorded as failing tests.
