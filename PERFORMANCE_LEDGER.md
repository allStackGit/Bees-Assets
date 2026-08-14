# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-012 — Reuse the Hive Mind startup callback across level setups
**Location:** `Scripts/Levels/Level.Setup.cs/SetupHivemind()`  
**Cost:** Every Hive Mind level setup calls `_initialCommandDelayTimer.Reuse(..., () => { AddTimer(_hivemindTimer); })`. The lambda captures the `Level` instance, so repeated training episodes create a fresh delegate for a callback whose behavior never changes. The timer objects themselves are already reused, making this callback allocation the remaining per-episode churn in this startup path.  
**Optimization:** Replace the captured lambda with a stable instance method callback (or a cached `Action`) that adds `_hivemindTimer`, and pass that callback to `Reuse()` each episode.  
**Evidence:** `ResetLevel()` ends by calling `SetupLevel()`, and `SetupLevel()` calls `SetupHivemind()` every episode. When `Stage.ActivateHiveMind` is true, `SetupHivemind()` recreates the captured callback even though `_hivemindTimer` and `_initialCommandDelayTimer` are persistent fields.  
**Risk:** Preserve the delayed ordering exactly: squads are queued first, `_hivemindTimer` is configured but not active, and it is added only when `_initialCommandDelayTimer` fires.

Clean static passes: 0 / 2.
