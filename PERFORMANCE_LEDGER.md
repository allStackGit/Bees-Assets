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

### PERF-011 — Stop dirtying the HUD layout every visible-clock frame
**Location:** `Scripts/UI Components/GameHudLayoutGuard.cs` — `LateUpdate()` / `ApplyLayout()`
**Cost:** While the clock is visible, `LateUpdate()` calls `ApplyLayout()` every frame and rewrites the speed button's `RectTransform.anchoredPosition` even when the clock and control geometry are unchanged, needlessly dirtying UI layout/canvas state.
**Optimization:** Reapply only when clock visibility or the relevant clock/speed geometry/position changes.
**Evidence:** The condition `clockVisible != _clockWasVisible || clockVisible` is true on every frame where the clock is visible.
**Risk:** Low. Dynamic HUD changes must still be detected before the next rendered frame.

### PERF-012 — Avoid redundant custom-animation sprite assignments
**Location:** `Scripts/Entities/Ships/ShipAnimationController.cs`, `Scripts/Entities/Ships/RemainsAnimationController.cs` — `LateUpdate()`
**Cost:** When no animation frame swap is requested, custom-colored animated ships/remains repeatedly assign `SpriteRenderer.sprite = CurrentSprite` every `LateUpdate`, even though that is already the current sprite.
**Optimization:** Assign the renderer only when `ShouldSwapSprite` advances the frame or when activation/reset explicitly restores a sprite.
**Evidence:** Both controllers retain `CurrentSprite`; their no-swap branches write that same reference every frame.
**Risk:** Low to medium. Preserve the reason the fallback assignment was added if another Animator can overwrite the sprite between callbacks; verify activation/Animator ordering before removal.

### PERF-013 — Avoid redundant targeting-marker activation calls
**Location:** `Scripts/Entities/Ships/Weapons/Turret.Aiming.cs` — `MoveTargetingMarker()`
**Cost:** Every active turret aim tick calls `TargetingMarker.SetActive(true/false)` even when marker visibility has not changed; aiming runs in `FixedUpdate()` for every non-cease-fire turret.
**Optimization:** Guard `SetActive` with `activeSelf` (or cached state) while continuing to update marker position whenever it is visible.
**Evidence:** `Turret.FixedUpdate()` calls `Aim()` each physics tick and `Aim()` always calls `MoveTargetingMarker()`.
**Risk:** Low. Visibility conditions remain unchanged; only redundant hierarchy calls are removed.

### PERF-014 — Compute the mouse-edge pixel threshold once per frame
**Location:** `Scripts/Levels/LevelInputManager.cs` — `CheckInputs()`
**Cost:** The same `Utilities.WorldUnitsToScreenPixels(Stage.MouseScrollDistanceFromEdge, Stage.Camera)` calculation is performed four times per frame. Each call performs two camera world-to-screen projections.
**Optimization:** Compute the Vector2 threshold once at the start of the edge checks and reuse its x/y values.
**Evidence:** All four calls use identical inputs within the same `CheckInputs()` invocation.
**Risk:** Low. Camera/resolution changes are still reflected each frame.

### PERF-015 — Remove square roots from custom-sprite changeable-pixel scanning
**Location:** `Scripts/Utilities.cs` — `GetChangablePixelsForImage()`
**Cost:** Custom sprite preparation calls `Vector3.Distance()` for every source-color × texture-pixel pair, paying a square root and reconstructing the target color vector inside the inner loop.
**Optimization:** Precompute the target RGB vector once per source color and compare squared RGB distance against the squared threshold.
**Evidence:** The method only tests whether distance is below a fixed threshold; exact distance is never consumed.
**Risk:** Low. Use the mathematically equivalent squared threshold and retain alpha filtering/pixel order.
