# Runtime lifecycle

## General ownership rules

- Pooled objects must reset every piece of per-use state, including counters, timers, cached references, transient UI children, collision/contact state, and coroutine generations. A reset that clears the visible state but leaves scheduling state behind is incomplete.
- Delayed callbacks/coroutines must not act on a pooled wrapper after reuse. Either keep the wrapper reserved until the delayed work finishes, capture immutable context, or invalidate callbacks with a lifecycle generation.
- Shared prefab/configuration arrays are templates. Copy them before per-squad mutation.
- Unity object identity must be explicit when identity matters. Before runtime `Setup`, many `MapObject`s have production `Id == 0`; use Transform/object identity for prefab authoring checks rather than ID-based equality.
- Capacity loops must use strict `< limit` semantics where the protocol says “at most N”; audit `<=` carefully around 64-ship Hive Mind payloads.
- Cast before division when producing percentages/normalized AI values. Integer division previously collapsed damaged-health information to 0/1.
- Focused partial files own their own imports. After splitting a large class, compile-check namespace dependencies in every partial rather than relying on the old monolith's broad `using` list.
- Connector whole-file writes must be checked immediately against their parent commit for accidental tail loss. For large files, fetch/restore the complete prior blob before changing imports or small statements.

## Timers and commands

- `ScaledTimer` is time-based in seconds. `startImmediate` must fire immediately and then schedule the next callback one normal interval later, not nearly two intervals later.
- Remove timers while iterating from the end/backwards or from a snapshot; forward removal can skip adjacent timers for a frame.
- Command-owned participant state should not depend on aggregate shared-environment collections. `FullRetreat`, for example, owns its own participant IDs even though a Warp Gate also keeps aggregate occupancy.
- Finalizers that mutate the collection being enumerated must be invoked from a snapshot (e.g. mining commands during asteroid teardown).
- `Command.Setup()` establishes `Squad.HasCommand`; queue runners should not blindly reassert that flag after `Execute()`, because an execution path can synchronously finalize and clear the command.
- Derived command `Execute()` implementations must stop immediately if `base.Execute()` finalized the command; otherwise they can schedule timers or mutate state on an already-dead pooled command.

## Targeting and Hive Mind

- `Squad.GetShips()` exposes the authoritative mutable list. Command/weapon/matchup targeting must work on snapshots before sort/shuffle.
- Float strategies must compare with `CompareTo` or equivalent; casting float differences to `int` loses meaningful sub-unit differences.
- Type-specific strategies (`Type A`, etc.) mean prefer the requested ship type. Matchup selection should therefore prefer the squad with the greatest matching count, consistent with weapon/command targeting.
- Random matchup selection should sample a uniform index; sorting by a random 0/1 key biases stable order toward earlier squads.
- Hive Mind matchup payloads are capped at 64 ships; health comparisons require fractional arithmetic.

## Pooling and combat lifecycles

- Barge charge/cooldown coroutines carry a lifecycle generation. Reset/cancel must invalidate suspended wind-up/cooldown callbacks rather than relying on `StopAllCoroutines`, which could cancel unrelated behavior.
- `ChargingBar` owns a Level timer. Reuse cancels the previous timer and charge values are clamped to 0-100; equality-only completion checks are unsafe if values can overshoot.
- Striker collision exit must clear cached contact directly; `OnTriggerExit2D` should not require `Collider.IsTouching` to prove contact ended.
- Destroying a Carrier must detach or reassign every `CarrierShip` reference before the Carrier wrapper can return to its pool. Consumers must represent “no surviving Carrier” as `Carrier == null`, never a stale pooled reference.
- `StrikerBomb` delayed damage must complete before the bomb wrapper returns to the projectile pool.
- A projectile owns its shooter-registration lifecycle: `Projectile.Kill()` removes itself from `Shooter.ProjectilesInFlight` even when the shooter is dead. GameState release cleanup remains a defensive backstop, not the normal owner.
- Lingering explosions must drain queued contacts with `while (queue.Count > 0)` or a snapshot; never use a shrinking `queue.Count` as the bound of a dequeue loop.
- One-hit explosion sets must be populated before applying obstacle/ship damage. A membership check without adding the contacted object does not prevent repeated damage.
- A dead Fire Barge remains out of the ship release pool for its five-second explosion/delayed-teardown lifetime so old callbacks/projectiles cannot observe a reused shooter wrapper.
- Special death overrides should delegate persistent loss/stat mutations to shared death accounting exactly once. Fire Barge previously incremented `ShipsLost` itself and then called `LogKilledStats()`, double-counting squad losses.
- Fog-of-war death fades freeze at the death position. Reusing a ship cancels old fade timers so an old vision hole cannot follow the new occupant.
- Warp Gate audio/UI children and squad boxes are reusable owned children; create them once and reactivate/reset rather than instantiating another child every pool lifecycle.
- `Turret` is split into lifecycle, aiming, and targeting partials. `Turret.ClearData()` must reset `TargetingPasses = 0` so a pooled turret cannot resume halfway through its three-pass firing cadence.

## Healing, mining, retreat

- Beehive reservation state is distinct from physically healing/docked state. Only damaged ships reserve slots; arrival transitions waiting -> healing exactly once; death/full-heal/hive loss releases the reservation immediately.
- Destroying a Beehive may affect ships actually inside the healing state, not ships merely travelling toward a reserved slot.
- Mining accepts only live mining-capable ships. A non-mining escort touching the asteroid must never inflate extraction.
- Mining resource allocation must conserve the exact amount removed from the asteroid; integer division remainders must be distributed rather than silently lost.
- Retreat/warp accounting is per command/squad. Shared gate occupancy is aggregate presentation/capacity state and must not decide another squad's completion.
