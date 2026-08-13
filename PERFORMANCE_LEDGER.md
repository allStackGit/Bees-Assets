# Performance Ledger

Static-only performance audit. Measurements are not claimed where the current environment cannot run Unity profiling/qualification. Entries below are validated from repeated runtime code paths and are removed as they are resolved or disproved.

### PERF-001 — Reuse the Level timer snapshot
**Location:** `Scripts/Levels/Level.Runtime.cs` — `UpdateTimers()`
**Cost:** Every active frame with at least one `ScaledTimer` allocates a new array via `Timers.ToArray()`, creating persistent GC pressure in timer-heavy battles and training.
**Optimization:** Preserve mutation-safe snapshot semantics with a reusable list/array buffer instead of allocating a fresh snapshot each frame.
**Evidence:** `Level.Update()` calls `UpdateTimers()` each unpaused frame; `UpdateTimers()` snapshots the full timer list before processing callbacks because callbacks may add/cancel timers.
**Risk:** Low. Snapshot membership/order must remain identical for the current update; newly added timers must not run until a later update.

### PERF-002 — Check the targeting cache before building a fresh enemy list
**Location:** `Scripts/Entities/Ships/Weapons/Weapon.cs` — `GetPotentialEnemyTargetShips()`
**Cost:** A new LINQ-backed target list is constructed before the method checks `HasCachedChanged`/`CachedShootingStrategy`; on cache hits the new list is immediately discarded.
**Optimization:** Return `CachedTargetingQueue` before constructing `_shipQueue` when the existing cache-validity condition is true.
**Evidence:** `Turret.TargetingSequence()` repeatedly calls `Targeting()`; range trigger changes already mark `HasCachedChanged`, and current code returns the cached queue after performing the unused list construction.
**Risk:** Low. The optimization uses the exact existing cache-validity predicate and changes only when the unused list is built.

### PERF-003 — Skip damage-status lookup during the fallback targeting pass
**Location:** `Scripts/Entities/Ships/Weapons/Weapon.cs` — `DetermineTargetShip()`
**Cost:** `Level.State.GetShipDamageStatus()` is called for every valid candidate even when `useShipDamageStatus` is false, although the returned value is ignored in that branch.
**Optimization:** Perform the damage-status lookup only inside the `useShipDamageStatus` branch.
**Evidence:** `Targeting()` and `FireNext()` deliberately run a second `DetermineTargetShip(..., false)` pass when reservation-aware selection cannot choose a target.
**Risk:** Low. The fallback branch does not read or mutate the returned status today.

### PERF-004 — Index ship damage reservations instead of linearly scanning
**Location:** `Scripts/Levels/GameState.Queries.cs`, `Scripts/Levels/GameState.cs` — `GetShipDamageStatus()` / `ShipDamageStatuses`
**Cost:** Every reservation lookup linearly scans `ShipDamageStatuses[side - 1]` with `FirstOrDefault`; targeting and projectile paths call this repeatedly as the status list grows.
**Optimization:** Maintain an ID-keyed dictionary per side for O(1) lookup while preserving the existing `ShipDamageStatus` objects/list if other code requires enumeration.
**Evidence:** `DetermineTargetShip()`, projectile setup/fire, and combat bookkeeping repeatedly call `GetShipDamageStatus()`; ship IDs are already level-scoped unique keys through `ShipsById`.
**Risk:** Medium. Reset/removal lifecycle must keep the index synchronized and must not replace status object identity referenced by projectiles.

### PERF-005 — Stop copying the whole path grid every static-only frame
**Location:** `Scripts/Levels/Pathfinder.Obstacles.cs` — `UpdateDynamicObstacleLayer()`
**Cost:** When collision asteroids are disabled, the entire `_baseClearance` array is copied into `_dynamicClearance` again whenever `Stage.FixedUpdates` changes and a path/occupancy query reaches this method, even though no dynamic obstacle can have changed it.
**Optimization:** Track whether `_dynamicClearance` already matches the static base and copy only after a static rebuild or transition from a dynamic-obstacle snapshot.
**Evidence:** The static branch uses frame number as its invalidation condition despite `_baseClearance` changing only through the explicit static-dirty/rebuild path.
**Risk:** Medium. Dynamic-to-static transitions and static obstacle rebuilds must force exactly one refresh before reuse.

### PERF-006 — Reuse the standing-request snapshot
**Location:** `Scripts/Server/Socket.cs` — `Update()` / `CheckStandingRequests()`
**Cost:** Every rendered frame calls `StandingRequests.ToList()`, including frames where the set is empty, creating a short-lived list and backing array repeatedly.
**Optimization:** Return immediately when empty and reuse a snapshot list (`Clear` + `AddRange`) when mutation-safe iteration is required.
**Evidence:** `Socket.Update()` calls `CheckStandingRequests()` unconditionally each frame; the snapshot exists to tolerate request lifecycle mutation while waiting for user-data/settings responses.
**Risk:** Low. Preserve snapshot iteration so removals/replacements during `WaitForResponse()` cannot invalidate enumeration.

### PERF-007 — Throttle handled-response pruning
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs` — `Update()` / `PruneHandledResponses()`
**Cost:** Every rendered frame walks the entire handled-response hash set and the retention dictionary; history can contain thousands of entries and retention is 120 seconds, so frame-rate polling is unnecessary work.
**Optimization:** Keep `CampaignCheckpoint.FlushIfReady()` responsive, but run response-history pruning on a coarse interval (for example once per second) while retaining the same 120-second/max-count policy.
**Evidence:** The guard runs as a persistent `DontDestroyOnLoad` MonoBehaviour and `PruneHandledResponses()` has no frame-sensitive requirement.
**Risk:** Low. Pruning may occur up to one interval later but duplicate-suppression semantics and bounded history remain unchanged.

### PERF-008 — Parse each socket response envelope only once
**Location:** `Scripts/Server/Socket.cs`, `Scripts/Server/SocketResponseLifecycleGuard.cs`
**Cost:** Each dequeued response is UTF-8 decoded and deserialized into `ServerResponse` inside `ShouldSuppressResponse()`, then normal responses are decoded and deserialized into `ServerResponse` again in `Socket.Message()` before typed handlers parse their payload.
**Optimization:** Decode/parse the envelope once in `Socket.Message()` and pass that parsed envelope to lifecycle suppression before claiming/dispatching it.
**Evidence:** `Socket.Update()` currently calls `ShouldSuppressResponse(this, byte[])` before `Message(byte[])`; both methods independently perform the same UTF-8 conversion and `JsonUtility.FromJson<ServerResponse>`.
**Risk:** Medium. Error/suppression ordering must remain the same, especially 401/403/409 handling and duplicate response claiming.

### PERF-009 — Precompute closest/furthest targeting distances once per sort
**Location:** `Scripts/Entities/Ships/Weapons/Weapon.cs` — `MakeSortedTargetingList()`
**Cost:** Closest/furthest comparators call `DistanceTo()` for both operands on every sort comparison. `DistanceTo()` invokes collider `ClosestPoint()` and a square-root distance, turning one sort into many repeated native/geometry calculations per candidate.
**Optimization:** Populate a reusable ID-to-distance map once for the current target list, then sort using the cached scalar values.
**Evidence:** `List<T>.Sort` performs O(n log n) comparisons; target transforms cannot advance during the synchronous sort, so all comparisons are against the same game-state instant.
**Risk:** Low to medium. Cache must be cleared/rebuilt for every closest/furthest sort and use each candidate's current distance exactly once.

### PERF-010 — Avoid redundant rocket-flare SetActive calls every physics tick
**Location:** `Scripts/Entities/Ships/Ship.Movement.cs`, `Scripts/Entities/Ships/Ship.Visuals.cs` — `SetMovementVelocity()` / `SetRocketFlares()`
**Cost:** Every moving ship with rocket flares calls `SetRocketFlares()` each fixed update; that method repeatedly sends identical `GameObject.SetActive(true/false)` calls to center and side flare objects even when the turn state has not changed.
**Optimization:** Cache the currently applied flare state and only toggle objects when transitioning between stopped/straight/turn-left/turn-right states.
**Evidence:** `SetMovementVelocity()` invokes the method unconditionally when `HasRocketFlares`; existing `AreRocketFlaresOutOfSync` already shows the code tracks flare state conceptually, but it still performs the native activation calls each tick.
**Risk:** Medium. Reset/stop/pool lifecycle must clear cached state so reused ships and resumed movement apply the correct flare visibility immediately.
