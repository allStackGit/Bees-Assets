# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

1. **PERF-006 — Standing-request list allocated every rendered frame.** `Scripts/Server/Socket.cs/CheckStandingRequests()` calls `StandingRequests.ToList()` unconditionally. Early-return when empty and reuse a mutation-safe snapshot list.
2. **PERF-008 — Socket response envelope parsed twice.** `SocketResponseLifecycleGuard` now exposes a validated parsed envelope, but `Socket.Update()` still calls the byte-array suppression path and `Socket.Message()` reparses it. Pass the parsed JSON/envelope through normal response dispatch while preserving malformed-response behavior.
3. **PERF-014 — Mouse-edge pixel threshold projected four times per frame.** `Scripts/Levels/LevelInputManager.cs/CheckInputs()` repeats the same world-to-screen conversion; compute it once per invocation.
4. **PERF-015 — Custom-sprite pixel matching performs unnecessary square roots.** `Scripts/Utilities.cs/GetChangablePixelsForImage()` calls `Vector3.Distance()` for every color×pixel test. Compare squared RGB distance to a squared threshold and precompute source color values.
5. **PERF-020 — Full command targeting sorts recompute closest/furthest keys.** `Scripts/Levels/Commands/Command.cs/MakeTargetingQueue()` needs the full order but can cache each ship's squad-distance once before sorting.
