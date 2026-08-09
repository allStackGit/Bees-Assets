# Pathfinding and performance

- Existing `BeesPerformanceQualification` is a CPU regression baseline, not minimum-spec certification: one path worker, a 64x64 open grid, repeated real background searches, and repeated `GameState.ResetState` calls.
- Static pathfinding obstacles are authoritative through `GameState.Obstacles` and geometry sampled from `Obstacle.ClearanceMappingCollider`. Dense qualification should create real Obstacles/colliders rather than mutating private path arrays.
- `Pathfinder.InitializeMap()` discovers initial static obstacles via the real Unity object/tag path and `Obstacle.Setup(Level)`; stripped fixtures therefore need the production obstacle tag and a minimal real Pool/Stage context.
- Dynamic collision-asteroid avoidance is a separate cached layer rebuilt from live moving obstacles when enabled. Tests should register dynamic obstacles after base Pathfinder initialization and advance `Stage.FixedUpdates` to force a new layer snapshot.
- Authored obstacle layouts can be gameplay rules, not decoration. Placement/spawn/path qualification should inspect the exact mission prefab and use real collider geometry plus ship clearance.
- Hardware qualification should record CPU/core count, RAM, GPU/VRAM/API, resolution, OS, Unity version, and batch/headless status alongside performance results.
- Soak tests should watch both managed memory and stable pool/runtime counts. Prefer monotonic-growth/leak assertions over unrealistically tight absolute memory thresholds.
