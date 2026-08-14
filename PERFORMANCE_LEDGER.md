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

Clean static passes: 0 / 2.
