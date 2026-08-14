# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-011 — Remove visible map objects without rebuilding the entire set
**Location:** `Scripts/Entities/Ships/Weapons/MapObjectVisibilityTracker.cs/RemoveFromVisibleSet()`  
**Cost:** When the last observing `RangeCollider` leaves a map object, and again on tracker disable/destroy, `RemoveFromVisibleSet()` copies every other entry in `GameState.PlayerVisibleMapObjects` to `_visibleSurvivors`, clears the entire `HashSet`, re-adds all survivors, then clears the temporary list. Removing one visible object is therefore O(V) and rewrites the full set. Weapon-range contacts can enter/exit repeatedly as ships move, so the cost scales with both visible-object count and contact churn.  
**Optimization:** Remove the tracked object directly from the visibility set using a reference-safe/stable-hash path, retaining the existing survivor rebuild only as a defensive fallback if direct removal cannot be proven safe for a destroyed Unity wrapper. Cache the tracker alongside contact ownership in `RangeCollider` if that simplifies reliable removal without repeated component lookup.  
**Evidence:** Current `RemoveFromVisibleSet()` enumerates the full visibility set into `_visibleSurvivors`, clears `PlayerVisibleMapObjects`, and repopulates it. Current `MapObject` uses a stable nonzero `Id` hash after setup and its `Equals` checks `ReferenceEquals` before Unity-null semantics; current tracker tests explicitly cover destroyed-object null behavior and reset self-healing.  
**Risk:** Unity destroyed-object semantics are the reason this code needs careful qualification. Direct removal must be covered for live exit, `OnDisable`, `OnDestroy`, and GameState reset/re-enter cases so a destroyed wrapper cannot remain in `PlayerVisibleMapObjects` or cause another visible object to be lost.

Clean static passes: 0 / 2.
