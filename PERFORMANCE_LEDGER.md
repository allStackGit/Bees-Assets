# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-022 — Store queued path requests as values instead of heap objects
**Location:** `Scripts/Levels/Pathfinder.Search.cs`, `PathWaiting` / queued path request creation  
**Cost:** Every path request that cannot start immediately constructs a new `PathWaiting` class instance before enqueueing it. Dense movement can queue many requests while the fixed worker pool is occupied, adding one managed heap allocation for request metadata that is only read by value after enqueue.  
**Optimization:** Convert `PathWaiting` to a value type while preserving the same fields/constructor and `Queue<PathWaiting>` behavior.  
**Evidence:** Queue creation, dequeue, cancellation scanning, and remaining-request checks only read the queued request's ship, start/end positions, clearance, and waiting-time fields; no active code relies on `PathWaiting` object identity or mutates a shared queued instance through aliases.  
**Risk:** Preserve all field values, enqueue/dequeue order, timeout accounting, and ship-release checks exactly; do not change path-worker concurrency or path result ownership.

### PERF-023 — Skip per-life ship and weapon diagnostic naming during training
**Location:** `Scripts/Entities/Ships/Ship.Lifecycle.cs`, `Ship.Setup()`; `Scripts/Entities/Ships/Weapons/Weapon.cs`, `Setup()`  
**Cost:** Every pooled ship lifecycle formats and assigns a new `"Type #Id"` ship name, then every weapon formats another `"ShipName: WeaponType"` string. Episode resets and transient Queen/Scout/Carrier spawns repeatedly pay these diagnostic string and Unity object-name costs in dedicated training.  
**Optimization:** Preserve descriptive per-life names outside training, but skip rebuilding/assigning ship and weapon names when `Stage.IsTraining`, retaining stable prefab/previous diagnostic names just as pooled projectile and obstacle naming already does.  
**Evidence:** Active simulation, server protocol, targeting, damage, pooling, and registry paths identify ships/weapons by IDs, enum types, and references. Branch name consumers traced are diagnostic/log/UI text rather than gameplay identity.  
**Risk:** Do not alter runtime IDs, FleetShip identity, targeting, setup order, or non-training/editor names. Training diagnostics may display stable prefab/previous names instead of current per-life IDs.

### PERF-024 — Skip ship recoloring/visual-list work when the Stage is not rendering
**Location:** `Scripts/Entities/Ships/Ship.Visuals.cs`, `SetColor()`; `Scripts/Entities/Ships/Beacon.cs`, `SetColor()`  
**Cost:** Every ship spawn calls `SetColor()` even in nonrendering dedicated training. The method clears/rebuilds visual renderer lists, performs component/render-property work, and applies sprite colors despite those visuals being unused; Beacon performs override-specific renderer work before reaching the base method. This repeats across episode resets and transient spawns.  
**Optimization:** Return immediately from both the base and Beacon override when `!Stage.IsRendering`, leaving the rendering path unchanged.  
**Evidence:** `LevelConstructor`, `CarrierSquad`, and Scout beacon spawning call `SetColor()` after setup. Other visual paths such as `UpdateHealthBar()` already short-circuit when the Stage is not rendering, and training creation removes/disables presentation components without using color as gameplay state.  
**Risk:** Preserve all recoloring, custom squad color, sorting, sprite, and UI behavior for rendering Stages. Do not gate any nonvisual ship setup behind this check.

### PERF-025 — Move Barge training-only visual destruction out of pooled Setup
**Location:** `Scripts/Entities/Ships/Barge.cs`, `Setup()` / `Create()`  
**Cost:** In training, every pooled Barge `Setup()` destroys charge flare/animation objects even though those objects belong to the prefab instance and only need to be removed once. The flare list is emptied on first use, but the three animation references are still passed to Unity `Destroy` on every reuse.  
**Optimization:** Perform the training-only destruction/flare-list clear once in `Create()`, matching Factory, CarpenterBee, and Striker one-time presentation teardown, and remove it from per-life `Setup()`.  
**Evidence:** Barge instances come from the Stage `ObjectPool` and repeatedly call `Setup()`, while `Create()` runs only when the pooled wrapper is instantiated. The destroyed objects are never recreated per lifecycle and are never used in training branches.  
**Risk:** Keep ChargingBar per-life setup and all non-training charge animations unchanged. Do not move gameplay state initialization out of `Setup()`/`ClearData()`.

### PERF-026 — Avoid Fire Barge audio lookup when audio is disabled
**Location:** `Scripts/Entities/Ships/FireBarge.cs`, `Kill()`  
**Cost:** Every Fire Barge detonation calls `Explosion.GetComponent<AudioSource>()` before checking `Stage.ActivateAudio`. Dedicated training disables audio, so the component lookup is guaranteed unused work on a recurring combat path.  
**Optimization:** Resolve the explosion `AudioSource` only inside the existing `ActivateAudio` branch immediately before playback.  
**Evidence:** `ExplosionSound` is assigned in `Kill()` and used only by the guarded `ExplosionSound.Play()` call. No later lifecycle code consumes the cached field.  
**Risk:** Preserve audio playback exactly when audio is enabled and do not change explosion setup, damage, pooling, or detonation timing.

### PERF-027 — Remove no-op ObjectPool get/release callbacks
**Location:** `Scripts/Levels/Pool.cs`, `Setup()` and empty `OnTake...` / `OnReturn...` callbacks  
**Cost:** High-frequency ship, projectile, squad, and command pools invoke action-on-get/action-on-release delegates on every pool operation even though the configured callback bodies contain no executable code. This adds pure delegate dispatch to projectile firing, deaths, episode reset, command turnover, and transient spawning.  
**Optimization:** Pass `null` for empty action-on-get/action-on-release callbacks, as the map/obstacle pools already do; remove the unused empty callback methods if no callers remain.  
**Evidence:** Branch callback implementations for ships, projectiles, squads, and commands consist solely of comments. Unity `ObjectPool<T>` accepts null callbacks, and several existing pools in the same setup already use null actions.  
**Risk:** Do not remove create/destroy callbacks or any future callback that performs state reset. Only callbacks proven empty on the branch should be removed.

### PERF-028 — Use constant-time duplicate suppression for Hive Mind command queue
**Location:** `Scripts/Levels/GameState.Commands.cs`, `AddToSquadsAwaitingHiveMindCommands()`; `Scripts/Levels/Level.Ending.cs`, Hive Mind dequeue path; reset cleanup  
**Cost:** Every Hive Mind squad requeue calls `Queue<Squad>.Contains`, which linearly scans the pending FIFO. Command completion continuously requeues squads, and out-of-bounds squads can be dequeued/requeued on the 0.25-second Hive Mind timer across many simultaneous Levels.  
**Optimization:** Maintain a reference-identity companion `HashSet<Squad>` for membership while preserving the existing FIFO queue for ordering. Centralize dequeue/clear operations so queue and set remain synchronized.  
**Evidence:** The active queue is added through `AddToSquadsAwaitingHiveMindCommands()`, drained by `Level.GetHiveMindCommands()`, and cleared during reset. No consumer depends on duplicate entries; the current `Contains` explicitly enforces uniqueness.  
**Risk:** Preserve exact FIFO order and at-most-one queued instance per live squad. Use reference identity because pooled squad runtime IDs/state can change across lifecycles, and keep reset/kill cleanup synchronized.

### PERF-029 — Replace Hive Mind bounds LINQ with an indexed early-exit loop
**Location:** `Scripts/Levels/Squad.Commands.cs`, `IsInBounds()`  
**Cost:** `IsInBounds()` calls `GetShips().All(s => s.IsInBounds())` whenever the squad has not yet reached bounds. Hive Mind dispatch runs every 0.25 seconds, so out-of-bounds squads repeatedly pay LINQ/delegate/enumerator overhead while being requeued.  
**Optimization:** Scan the squad's ship list directly with an indexed loop, return false on the first out-of-bounds ship, and cache `_isInBounds = true` only when every ship passes.  
**Evidence:** `Level.GetHiveMindCommands()` calls `Squad.IsInBounds()` for each dequeued live squad and requeues failures. The current LINQ operation is a pure all-elements predicate with early-exit semantics that an indexed loop can reproduce exactly.  
**Risk:** Preserve empty-list semantics (`All` returns true), the sticky `_isInBounds` cache, and each ship's own bounds-cache behavior.

### PERF-030 — Reuse command targeting comparison delegates for capturing sort cases
**Location:** `Scripts/Levels/Commands/Command.cs`, `MakeTargetingQueue()`  
**Cost:** Enemy-targeting command setup rebuilds targeting queues and uses capturing lambdas for Closest, Furthest, and ship-type-priority sorting. Those closures are recreated when commands are prepared, adding managed allocations to continuous Hive Mind command turnover.  
**Optimization:** Replace the capturing lambdas with instance comparison methods; store the currently preferred ship-type letter in a field for the type-priority comparator. Keep existing noncapturing comparison behavior unchanged.  
**Evidence:** `Command.Setup()` calls `RebuildTargetingQueues()` whenever an enemy squad exists. `Weapon` already uses reusable comparison methods for equivalent targeting sorts, while base `Command` captures `_targetingDistanceKeys`/a local `type` in lambdas.  
**Risk:** Preserve all sort directions, tie behavior, Random/FirstSeen handling, and the type-priority rule. Do not change targeting queue rebuild timing or command semantics.

### PERF-031 — Index standing server requests by request hash
**Location:** `Scripts/Server/Socket.cs`, `StandingRequests` / `GetStandingRequest()` and request lifecycle removals  
**Cost:** `GetStandingRequest(hash)` linearly scans the entire outstanding-request `HashSet`. It is used by normal response dispatch, lifecycle guards, and data/settings waiters; multi-Level Hive Mind training keeps many command/matchup requests outstanding, so response handling repeatedly performs O(n) lookup for an already-unique stable hash.  
**Optimization:** Maintain a `Dictionary<long, ServerRequest>` alongside the iterable `HashSet`, populate it from `LogRequest`, centralize production removals so both structures stay synchronized, and use dictionary lookup with a defensive fallback for externally/test-inserted requests.  
**Evidence:** Production additions to `StandingRequests` are centralized in `Socket.LogRequest`; branch removals occur in a finite set of Socket/data/auth/level lifecycle paths. `ServerRequest.Hash` is assigned once in the base constructor and is the protocol response key.  
**Risk:** Do not change resend behavior, duplicate response suppression, iteration semantics, or request lifetime. Prevent stale dictionary references on every production removal and tolerate tests/tools that directly manipulate the public `StandingRequests` set.

### PERF-032 — Reuse the Level-owned ObstacleMap container across episodes
**Location:** `Scripts/Levels/Level.Environment.cs`, `SpawnObstacles()`; `Scripts/Levels/Level.Ending.cs`, obstacle teardown  
**Cost:** Every obstacle-enabled episode creates a fresh `ObstacleMap` and its internal `List<StaticObstacle>` even though the Level instance is reused and static obstacle objects/backgrounds are already pooled. Hive Mind episode resets therefore create short-lived container garbage independent of the actual obstacle content.  
**Optimization:** Create the Level-owned `ObstacleMap` lazily once, then clear/reinitialize its existing list/background before each spawn instead of replacing the container.  
**Evidence:** Runtime `ObstacleMap` contains only an Id, background reference, and mutable obstacle list. Pooled-layout teardown already releases every static obstacle/background, clears `ObstacleMap.Obstacles`, and nulls the background; nonpooled layouts are destroyed before the next setup. No active code relies on a new container reference per episode.  
**Risk:** Ensure stale nonpooled obstacle references are cleared before reuse, preserve the current Id contract, and do not use the unfinished generic `ObstacleMap` pools whose code explicitly warns that their ownership model needs redesign.

Clean static passes: 0 / 2.
