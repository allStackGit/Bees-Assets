# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-003 — Avoid allocating the UI-color key list on every lookup
**Location:** `Scripts/ConfigData.Runtime.cs/GetUIColor(string name)`  
**Cost:** Every color lookup calls `Colors.Keys.ToList()` and then linearly scans that temporary list with `Contains` before performing a dictionary lookup. This allocates a list and does redundant work for each call. `GetUIColor()` is used broadly by ship/squad/UI setup and is called by `Ship.UpdateHealthBar()` on rendered health changes, so the allocation is reachable during combat as well as setup.  
**Optimization:** Use a single dictionary lookup (`TryGetValue`) and return the resolved color directly; only perform the existing `"error"` fallback lookup when the requested key is absent.  
**Evidence:** Current `GetUIColor()` materializes `Colors.Keys.ToList()` on every invocation. Current `Ship.Visuals.cs/UpdateHealthBar()` calls `GetUIColor("good"/"medium"/"bad")`, and additional squad/UI setup call sites use the same helper.  
**Risk:** Preserve the current unknown-name error log and `"error"` fallback behavior exactly.

### PERF-006 — Pool static obstacle hierarchies used by repeated training episodes
**Location:** `Scripts/Levels/Level.Environment.cs/GenerateRandomObstacles()/SpawnObstacles()` and `Scripts/Levels/Level.Ending.cs/SaveAndEnd()`  
**Cost:** Hive Mind training deliberately randomizes the static-obstacle dimension. When random static obstacles are selected, `GenerateRandomObstacles()` instantiates a background plus 1–10 `ObstaclePrefab` GameObjects for that episode. Other authored obstacle-list paths also instantiate backgrounds/obstacles or an obstacle container. `SaveAndEnd()` later destroys the static obstacle GameObjects/background. This creates managed/native object and transform/component churn inside repeated level setup/teardown even though ships, projectiles, maps, and moving asteroids are otherwise pooled.  
**Optimization:** Introduce Stage-owned pooling/reuse for the common static obstacle prefab and obstacle background (and, where practical, authored obstacle containers), resetting transforms/collider state on checkout and returning them on teardown instead of destroying them. Prioritize the random training path first because it is guaranteed to recur across episodes.  
**Evidence:** Current `RandomizeOptions()` chooses static obstacles with a coin toss for Hive Mind training; an empty random obstacle specification reaches `GenerateRandomObstacles()`, which uses `Instantiate` for the background and each obstacle. Current `SaveAndEnd()` destroys `ObstacleMap.Obstacles` and `ObstacleMap.ObstacleBackground`.  
**Risk:** Pool reuse must fully reset scale, local position, collider enablement, pathfinder/static-obstacle registration, and any `MapObject` state. Authored obstacle-container ownership is more complex than the common random-obstacle prefab and should not be generalized until lifecycle reset is explicit.

Clean static passes: 0 / 2.
