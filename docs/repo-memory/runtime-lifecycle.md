# Runtime lifecycle

## General ownership rules

- Pooled objects must reset every piece of per-use state, including counters, timers, cached references, transient UI children, collision/contact state, and coroutine generations. A reset that clears the visible state but leaves scheduling state behind is incomplete.
- Delayed callbacks/coroutines must not act on a pooled wrapper after reuse. Either keep the wrapper reserved until the delayed work finishes, capture immutable context, or invalidate callbacks with a lifecycle generation.
- Shared prefab/configuration arrays are templates. Copy them before per-squad mutation.
- Pooled visual recoloring must start from immutable prefab-era sprites, not the currently displayed sprite from the previous lifecycle. Components such as `Beacon` that hold alternate runtime sprite fields must reset those fields to their original sprites before applying a new squad color.
- Unity object identity must be explicit when identity matters. Before runtime `Setup`, many `MapObject`s have production `Id == 0`; use Transform/object identity for prefab authoring checks rather than ID-based equality.
- Pooled `Entity` wrappers have mutable runtime `Id` values, and `Entity.GetHashCode()` is therefore lifecycle-scoped. Any hash-based collection that can outlive a member lifecycle must use `ReferenceIdentityComparer<T>` (or an immutable captured lifecycle ID), never the default Entity comparer. Otherwise pool reuse changes the member hash while it is still inside the collection and can corrupt `Contains`/`Remove` semantics or make a new lifecycle appear to inherit old visibility/contact state.
- Live caches that retain pooled wrappers must remove departing objects before reuse, even when reference-stable hashing is used. Stable hashing fixes collection integrity; it does not make an old sighting/contact valid for the new lifecycle. Hivemind observer/visibility sets are cleaned in `GameState.RemoveShip()` for this reason.
- Process-wide/static collections that retain Unity objects must prune destroyed wrappers at the corresponding scene/object teardown boundary. Unity's overloaded null comparison can identify destroyed wrappers, but a static `List`/`HashSet` still holds the managed reference until it is explicitly removed.
- Capacity loops must use strict `< limit` semantics where the protocol says “at most N”; audit `<=` carefully around 64-ship Hive Mind payloads.
- Cast before division when producing percentages/normalized AI values. Integer division previously collapsed damaged-health information to 0/1.
- Focused partial files own their own imports. After splitting a large class, compile-check namespace dependencies in every partial rather than relying on the old monolith's broad `using` list.
- Connector whole-file writes must be checked immediately against their parent commit for accidental tail loss. For large files, fetch/restore the complete prior blob before changing imports or small statements.
- Physics trigger helpers must reject invalid occupants at the boundary. A `Zone` must not add a null/dead non-Ship contact or invoke an unset callback merely because another collider overlaps it.
- Stage-wide tuning fields are not per-Level scratch state. In particular asteroid spawn intervals must be derived locally for each Level; dividing `Stage.CurrentAsteroid*SpawnRate` or rewriting `Stage.AsteroidMinimumSpawnRate` while configuring one Level compounds across sibling Levels and later resets.

## Timers and commands

- `ScaledTimer` is time-based in seconds. `startImmediate` must fire immediately and then schedule the next callback one normal interval later, not nearly two intervals later.
- `ScaledTimer.Reuse()` starts a new timer generation. If a timer callback cancels/reuses that same timer (for example when a command changes from a multi-second cadence to 0.25 seconds), the still-running old `Update()` call must not subtract the completed old interval from the freshly reset `Elapsed` state. Generation ownership protects callback-driven self-reconfiguration.
- Remove timers while iterating from the end/backwards or from a snapshot; forward removal can skip adjacent timers for a frame.
- One-time progression timers must be one-shot. The 30-minute Fish Tank unlock must not use `isRecurring=true`, which would repeatedly pause/show the unlock dialogue every additional 30 minutes.
- Command-owned participant state should not depend on aggregate shared-environment collections. `FullRetreat`, for example, owns its own participant IDs even though a Warp Gate also keeps aggregate occupancy.
- Movement commands must have an overall finalization escape hatch when ship pathfinding can exhaust its own retries. A stopped ship does not release `Squad.HasCommand`; `FullRetreat` therefore uses `StandardMaxCommandTime` in addition to ship-level failed-path retry limits.
- Finalizers that mutate the collection being enumerated must be invoked from a snapshot (e.g. mining commands during asteroid teardown).
- `Command.Setup()` establishes `Squad.HasCommand`; queue runners should not blindly reassert that flag after `Execute()`, because an execution path can synchronously finalize and clear the command.
- Derived command `Execute()` implementations must stop immediately if `base.Execute()` finalized the command; otherwise they can schedule timers or mutate state on an already-dead pooled command.
- Command-specific side effects must be undone in the command's general `SetFinalize` path, not only its normal timeout/completion callback. Guard speed matching and reciprocal guard membership are lifecycle-owned state.
- A command whose semantics mean “stay here” must explicitly cancel inherited movement. Starting `Hold`, or an already-safe `Retreat`, cannot assume finalizing the prior command stopped ship movement/path retries.
- Ship-owned timers must be cancelled before the Ship wrapper can enter the pool. This includes combat, asteroid recheck, failed-path retry, and hover/info timers across separate Ship partials.
- An explicit `StopMoving()` must cancel pending path-retry work so an old failed-path callback cannot restart movement after a new order/cancel.
- A Striker-only `BombingRun` must remain active after its enemy squad dies until the Strikers have returned to a live Carrier, or no Carrier remains. A single return-to-carrier movement update is insufficient because finalizing the command cancels the timer that continues the trip.
- Multi-ship commands own the whole participating squad lifecycle unless explicitly documented otherwise. `Charge` must not finalize/reset every Barge when only the first Barge completes; all remaining live Barges must complete their charge/cooldown run first.

## Targeting and Hive Mind

- `Squad.GetShips()` exposes the authoritative mutable list. Command/weapon/matchup targeting must work on snapshots before sort/shuffle.
- Float strategies must compare with `CompareTo` or equivalent; casting float differences to `int` loses meaningful sub-unit differences.
- Type-specific strategies (`Type A`, etc.) mean prefer the requested ship type. Matchup selection should therefore prefer the squad with the greatest matching count, consistent with weapon/command targeting.
- Random matchup selection should sample a uniform index; sorting by a random 0/1 key biases stable order toward earlier squads.
- Hive Mind matchup payloads are capped at 64 ships; health comparisons require fractional arithmetic.
- Visible-squad queries must deduplicate by squad. Returning one squad entry per visible ship biases random matchup selection toward larger squads.
- The Hive Mind awaiting-command queue is idempotent: null/dead squads are ignored and a squad already waiting must not be enqueued again.
- Target-queue exhaustion is normal terminal state. Ship/command helpers must return/finalize cleanly when the enemy squad has no remaining valid ships instead of dequeuing/dereferencing an empty target queue.
- Pending damage equal to a target's remaining health is already lethal coverage; target selection must use strict `<` rather than `<=` when deciding whether more damage should be reserved.
- Composition-derived command bans must be refreshed after casualties as well as ship additions. A squad that becomes defenseless or bomber-only must not keep the command eligibility of its earlier composition.

## Pooling and combat lifecycles

- Barge charge/cooldown coroutines carry a lifecycle generation. Reset/cancel must invalidate suspended wind-up/cooldown callbacks rather than relying on `StopAllCoroutines`, which could cancel unrelated behavior.
- `ChargingBar` owns a Level timer. Reuse cancels the previous timer and charge values are clamped to 0-100; equality-only completion checks are unsafe if values can overshoot.
- Striker collision exit must clear cached contact directly; `OnTriggerExit2D` should not require `Collider.IsTouching` to prove contact ended.
- Destroying a Carrier must detach or reassign every same-side `CarrierShip` reference before the Carrier wrapper can return to its pool. Replacement Carriers and dependent craft must be searched with `GameState.GetShips(Carrier.Side)`, not human-only queries. Consumers must represent “no surviving Carrier” as `Carrier == null`, never a stale pooled reference.
- `StrikerBomb` delayed damage must complete before the bomb wrapper returns to the projectile pool.
- A projectile owns its shooter-registration lifecycle: `Projectile.Kill()` removes itself from `Shooter.ProjectilesInFlight` even when the shooter is dead. GameState release cleanup remains a defensive backstop, not the normal owner.
- Lingering explosions must drain queued contacts with `while (queue.Count > 0)` or a snapshot; never use a shrinking `queue.Count` as the bound of a dequeue loop.
- One-hit explosion sets must be populated before applying obstacle/ship damage. A membership check without adding the contacted object does not prevent repeated damage.
- A dead Fire Barge remains out of the ship release pool for its five-second explosion/delayed-teardown lifetime so old callbacks/projectiles cannot observe a reused shooter wrapper.
- Special death overrides should delegate persistent loss/stat mutations to shared death accounting exactly once. Fire Barge previously incremented `ShipsLost` itself and then called `LogKilledStats()`, double-counting squad losses.
- Fog-of-war death fades freeze at the death position and retain the lifecycle `Level` that owns their timers/registry. A pooled Ship can move to another Level before the fade finishes; old callbacks must never resolve ownership through the Ship's new `Level`.
- Warp Gate audio/UI children and squad boxes are reusable owned children; create them once and reactivate/reset rather than instantiating another child every pool lifecycle.
- `Turret` is split into lifecycle, aiming, and targeting partials. `Turret.ClearData()` must reset `TargetingPasses = 0` so a pooled turret cannot resume halfway through its three-pass firing cadence.
- `ShipDamageStatus` and `SpottedShip` hold direct Ship wrappers; `GameState.RemoveShip()` must remove those records before the wrapper can be pooled/reidentified.
- Nearby asteroid lists may temporarily retain destroyed wrappers; consumers must prune/filter dead/null asteroids before avoidance or targeting decisions.
- Lifecycle role flags such as `IsMinionSquad`, `IsMinionShip`, and `IsCarrierShip` must remain valid through ownership-sensitive deregistration, then be cleared before the pooled wrapper can be reused for a different role.
- Temporary Queen/Scout/Carrier child squads and ships share parent `SavedSquad`/`FleetShip` identities for behavior/stat accounting. Child registration and teardown must never claim or release the parent's `IsLoadedIntoLevel` state. Child roles must therefore be established before `Setup()`/registry insertion; `CarrierSquad.ClearData()` reasserts its subclass role during base setup before `GameState.AddSquad()` runs.

## Pathfinding ownership

- Ship path requests are protected by both `PathfindingRequestId` and `PathfindingLifecycleId`. A completed background path may modify a Ship only when both still match.
- Reinitializing a `Pathfinder` while one of its `Task.Run` searches still owns the instance's scratch arrays is unsafe even if Ship lifecycle IDs reject the eventual stale result. A reset must retire that Pathfinder instance (or otherwise wait/cancel workers) rather than mutate its arrays under active workers.
- Static obstacle-map rebuilds have the same immutability requirement as Pathfinder reset. `RebuildStaticObstacleLayer()` replaces shared static clearance and rewrites per-thread clearance snapshots, so it must never run while a background search is active. When destructible static geometry changes (for example a Fire Tank opens a route), reserve idle worker slots, let existing workers finish against the conservative old map, rebuild atomically on the main thread, then release queued searches onto the new map.
- Queue membership is not authoritative identity for pooled Ships; queued requests carry explicit request/lifecycle IDs and stale membership must not suppress a new lifecycle's request.

## Healing, mining, retreat

- Beehive reservation state is distinct from physically healing/docked state. Only damaged ships reserve slots; arrival transitions waiting -> healing exactly once; death/full-heal/hive loss releases the reservation immediately. Damaged ships that could not get an initial slot remain pending, and newly freed slots must be reassigned before a `Heal` command is allowed to finalize.
- Destroying a Beehive may affect ships actually inside the healing state, not ships merely travelling toward a reserved slot.
- Mining accepts only live mining-capable ships. A non-mining escort touching the asteroid must never inflate extraction.
- Mining resource allocation must conserve the exact amount removed from the asteroid; integer division remainders must be distributed rather than silently lost.
- Retreat/warp accounting is per command/squad. Shared gate occupancy is aggregate presentation/capacity state and must not decide another squad's completion.
