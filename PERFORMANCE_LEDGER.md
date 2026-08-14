# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-003 — Avoid allocating the UI-color key list on every lookup
**Location:** `Scripts/ConfigData.Runtime.cs/GetUIColor(string name)`  
**Cost:** Every color lookup calls `Colors.Keys.ToList()` and then linearly scans that temporary list with `Contains` before performing a dictionary lookup. This allocates a list and does redundant work for each call. `GetUIColor()` is used broadly by ship/squad/UI setup and is called by `Ship.UpdateHealthBar()` on rendered health changes, so the allocation is reachable during combat as well as setup.  
**Optimization:** Use a single dictionary lookup (`TryGetValue`) and return the resolved color directly; only perform the existing `"error"` fallback lookup when the requested key is absent.  
**Evidence:** Current `GetUIColor()` materializes `Colors.Keys.ToList()` on every invocation. Current `Ship.Visuals.cs/UpdateHealthBar()` calls `GetUIColor("good"/"medium"/"bad")`, and additional squad/UI setup call sites use the same helper.  
**Risk:** Preserve the current unknown-name error log and `"error"` fallback behavior exactly.

### PERF-004 — Reuse the per-ship colored-prefab working list across pooled setups
**Location:** `Scripts/Entities/Ships/Ship.Visuals.cs/SetColor()`  
**Cost:** `SetColor()` begins with `ColoredPrefabs = OriginalColoredPrefabs.ToList();`, allocating a new `List<GameObject>` each time a ship is configured. Ships are pooled and reconfigured across level resets, and the level constructor calls `SetColor()` for each spawned ship, so repeated episodes repeatedly allocate these short-lived lists even when the ship has no custom color.  
**Optimization:** Keep a ship-owned mutable working list and refill it with `Clear()`/`AddRange(OriginalColoredPrefabs)` (or indexed copy) instead of replacing the list; direct indexed loops can also avoid the `List.ForEach` delegate path while preserving sprite/reset behavior.  
**Evidence:** Current `Ship.Visuals.cs` performs the `ToList()` unconditionally before checking `Squad.HasCustomColor`; pooled ship setup calls `SetColor()` after each spawn.  
**Risk:** `ColoredPrefabs` must remain a mutable list distinct from the serialized `OriginalColoredPrefabs` baseline so recoloring/reset logic cannot mutate the authored list or carry a prior pooled lifecycle's sprite state.

### PERF-005 — Avoid sprite-array allocation when recolor caches hit
**Location:** `Scripts/Entities/Ships/ShipAnimationController.cs/RecolorAnimationSprites()` and `Scripts/Entities/Ships/RemainsAnimationController.cs/RecolorAnimationSprites()`  
**Cost:** Both methods allocate `new Sprite[TotalSprites]` before checking the Stage-level recolor cache. On a cache hit, the newly allocated array is immediately discarded and replaced by the cached array. Pooled custom-colour ships can revisit these setup paths across rendered levels, so the cache avoids disk/sprite work but still produces avoidable GC allocations.  
**Optimization:** Compute the cache key and try the Stage dictionary first. Assign the cached array directly on a hit; allocate a new sprite array only on a cache miss before populating and storing it.  
**Evidence:** Current ship and remains animation controllers both assign `RecoloredSprites = new Sprite[TotalSprites]` before `LoadedShipAnimationSprites.ContainsKey(key)` / `LoadedRemainsSprites.ContainsKey(key)`.  
**Risk:** Preserve current cache key semantics, sprite index offsets (ship animation skips the base sprite), and the error behavior when a FleetShip lacks cached sprite data.

### PERF-006 — Pool static obstacle hierarchies used by repeated training episodes
**Location:** `Scripts/Levels/Level.Environment.cs/GenerateRandomObstacles()/SpawnObstacles()` and `Scripts/Levels/Level.Ending.cs/SaveAndEnd()`  
**Cost:** Hive Mind training deliberately randomizes the static-obstacle dimension. When random static obstacles are selected, `GenerateRandomObstacles()` instantiates a background plus 1–10 `ObstaclePrefab` GameObjects for that episode. Other authored obstacle-list paths also instantiate backgrounds/obstacles or an obstacle container. `SaveAndEnd()` later destroys the static obstacle GameObjects/background. This creates managed/native object and transform/component churn inside repeated level setup/teardown even though ships, projectiles, maps, and moving asteroids are otherwise pooled.  
**Optimization:** Introduce Stage-owned pooling/reuse for the common static obstacle prefab and obstacle background (and, where practical, authored obstacle containers), resetting transforms/collider state on checkout and returning them on teardown instead of destroying them. Prioritize the random training path first because it is guaranteed to recur across episodes.  
**Evidence:** Current `RandomizeOptions()` chooses static obstacles with a coin toss for Hive Mind training; an empty random obstacle specification reaches `GenerateRandomObstacles()`, which uses `Instantiate` for the background and each obstacle. Current `SaveAndEnd()` destroys `ObstacleMap.Obstacles` and `ObstacleMap.ObstacleBackground`.  
**Risk:** Pool reuse must fully reset scale, local position, collider enablement, pathfinder/static-obstacle registration, and any `MapObject` state. Authored obstacle-container ownership is more complex than the common random-obstacle prefab and should not be generalized until lifecycle reset is explicit.

Clean static passes: 0 / 2.
