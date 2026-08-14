# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-002 — Refresh active combat timers without list churn
**Location:** `Scripts/Entities/Ships/Ship.Combat.cs/SetCombatTimer()`, `Scripts/Levels/Level.Runtime.cs/AddTimer()/CancelTimer()/UpdateTimers()`, `Scripts/Entities/Ships/Weapons/Turret.Targeting.cs/Fire()`  
**Cost:** Every turret shot calls `SetCombatTimer()` on both attacker and target. When a combat timer is already active, the method removes it from `Level.Timers`, calls `Reuse`, then adds it again. Each membership change increments the Level timer collection version, so the next `UpdateTimers()` rebuilds `_loopTimers` from the entire timer list. Sustained multi-ship fire therefore converts a simple timeout refresh into repeated list mutation plus whole-timer-snapshot copying.  
**Optimization:** When `_combatTimer` is already active, reset the existing timer in place with `ScaledTimer.Reuse()` without removing/re-adding it; only add the timer when transitioning from inactive to active. Preserve the existing callback behavior that cancels membership when combat actually times out.  
**Evidence:** `Turret.Fire()` invokes attacker and target `SetCombatTimer()` per shot; `ScaledTimer.Reuse()` explicitly resets `Elapsed`, cancellation state, and reuse generation safely; `Level.UpdateTimers()` copies all timers whenever `_timerCollectionVersion` changes. The cost is directly proportional to weapon-fire frequency and active timer count.  
**Risk:** The timer must remain exactly once in `Level.Timers`, and a timeout callback must still remove it before a later inactive→active transition. Incorrect active-state handling could leave combat stuck true or update a timer that is no longer owned by the Level.

Clean static passes: 0 / 2.
