# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-002 — Refresh active combat timers without list churn
**Location:** `Scripts/Entities/Ships/Ship.Combat.cs/SetCombatTimer()`, `Scripts/Levels/Level.Runtime.cs/AddTimer()/CancelTimer()/UpdateTimers()`, `Scripts/Entities/Ships/Weapons/Turret.Targeting.cs/Fire()`  
**Cost:** Every turret shot calls `SetCombatTimer()` on both attacker and target. When a combat timer is already active, the method removes it from `Level.Timers`, calls `Reuse`, then adds it again. Each membership change increments the Level timer collection version, so the next `UpdateTimers()` rebuilds `_loopTimers` from the entire timer list. Sustained multi-ship fire therefore converts a simple timeout refresh into repeated list mutation plus whole-timer-snapshot copying.  
**Optimization:** When `_combatTimer` is already active, reset the existing timer in place with `ScaledTimer.Reuse()` without removing/re-adding it; only add the timer when transitioning from inactive to active. Preserve the existing callback behavior that cancels membership when combat actually times out.  
**Evidence:** `Turret.Fire()` invokes attacker and target `SetCombatTimer()` per shot; `ScaledTimer.Reuse()` explicitly resets `Elapsed`, cancellation state, and reuse generation safely; `Level.UpdateTimers()` copies all timers whenever `_timerCollectionVersion` changes. The cost is directly proportional to weapon-fire frequency and active timer count.  
**Risk:** The timer must remain exactly once in `Level.Timers`, and a timeout callback must still remove it before a later inactive→active transition. Incorrect active-state handling could leave combat stuck true or update a timer that is no longer owned by the Level.

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
