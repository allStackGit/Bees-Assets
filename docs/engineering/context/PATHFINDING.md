# Pathfinding / Performance Route

Load this route only when exact pathfinding/movement/performance symbols and focused tests do not provide enough context.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| worker ownership / stale path publication | `Pathfinder`, `Pathfinder.Search`, `Ship.Movement`, `Ship.Lifecycle` | request ID + lifecycle ID + ship reference; invalidation may not cancel `Task.Run` work |
| tracked-target attack movement / replanning | `Ship.TrackedMovement`, `Aggressive` | active worker/retry ownership; meaningful distance/time replans; tracked-target tests |
| static obstacle rebuild / destruction | `Pathfinder.Obstacles`, `Obstacle`, `CanisterBomb`, `MarkStaticObstacleLayerDirty` | base-clearance rebuild; Level-scoped discovery |
| moving asteroids / dynamic avoidance | `CollisionAsteroid`, `Pathfinder.UpdateDynamicObstacleLayer`, `FoundNearbyAsteroid` | per-`Stage.FixedUpdates` snapshot; velocity padding; qualification tests |
| clearance / corridors / egress | `Pathfinder.Search`, `FindStaticEgressPath` | ship-size clearance; diagonal corner blocking; deterministic A* tie breaking |
| frame time / GC / low-end performance | actual hot path first | pools, physics, UI/rendering, pathfinding; focused benchmark/qualification before broad profiling |

Use the performance specialist skill only for an actual optimization procedure. Do not scan unrelated hot paths for a focused bug.
