# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

1. **PERF-050 — The recurring socket resend poll allocates a StandingRequests snapshot.** `Scripts/Server/Socket.cs/CheckForResends()` calls `StandingRequests.ToList()` every one-second poll. Rebuild a persistent snapshot buffer instead. `GetStandingRequest()` also uses a LINQ predicate for every response; use a direct set scan without introducing a mutable-hash index. The connector currently blocks both the large Socket replacement and the smaller reusable poll-helper write.
2. **PERF-052 — Stage ship-type options recreate lists during per-level setup.** `Level.Reset` now reuses random-level candidates and skips all training-side squad-list work, and `Level.Setup` assigns ship clearances in one pass without the old per-type filtering. `Stage.SetConfigOptionsAndOverrides()` still replaces `BeeShipTypes`/`HumanShipTypes` with `ToList()` or new singleton lists on each level setup instead of refilling the persistent Stage-owned lists.
