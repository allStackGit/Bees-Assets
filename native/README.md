# Bees unified native rewrite

This directory is the beginning of the Unity-independent replacement platform,
created from the current `feat/experiment-queue-ui` branch.

## Non-negotiable boundaries

- Exactly one authoritative set of gameplay rules must power both the rendered
  game and every headless/training/evaluation world. Do not create a simplified
  training ruleset.
- Gameplay state, clock, RNG, and entity ownership are per-world, never process
  globals. Keep presentation, server I/O and ML libraries outside `bees-sim`.
- Optimize the measured bottleneck. Do not replace mature native inference or
  physics libraries merely to reduce the dependency count.
- A failed update must not silently continue from a partially mutated world.
- Preserve existing user data, Hive Mind outcomes, policy ABI, scenarios,
  curriculum, metrics, deployment, authentication and experiment semantics
  until a deliberate compatibility migration is established.
- Linux, Windows and macOS are required targets for both the game and headless
  workers. Native development must work without the Unity editor.

## Hive Mind / server migration boundary

The native workspace now also contains `bees-hivemind`, a separate,
network-independent protocol and outcome-ownership crate. It preserves the
deployed `get-matchup-strategy`, `get-strategy` and
`store-commands` JSON fields, server command IDs **1–15**, shooting
IDs **1–40** and distinct targeting IDs **1–40**, exact 53-bit request
hashes, and unsigned matchup identities. Request hashes are allocated once by the client
transport for **all** message types; multiple Levels may share one WebSocket
and one server Hive Mind Game. Each strategy request captures its squad
lifecycle, so a late response for a retired squad becomes an explicit
reservation-discard obligation rather than an executable command.

For completed commands, the game must settle every issued primary/shooting
OutcomeId, independently decide whether a targeting reservation earned TSV,
and persist a StoreCommands submission before sending it. Native
`PendingStoreCommands` holds submissions until the authoritative server
responds. Status 200 acknowledges durable commit. A status-409 response
with `StaleOutcomeIds` is **partially committed** in the deployed server:
valid siblings have been written, so the native ledger retires that batch
but persists the unresolved stale IDs in a separate reconciliation record
rather than retrying already-committed outcomes. Only explicit reconciliation
retires that record. Other failures retain their pending submission. Transport authentication, reconnection, durable
client retry storage, the native squad-command executor, and the server-side
learning/persistence implementation remain separate work. **The existing
BeesServer production behavior has not been replaced in this increment.**

## Game command integration

`bees-gameplay` connects validated `bees-hivemind` selections to the
`bees-sim` combat world's single native movement implementation. It
preserves the server's learned strategy ID separately from the executable
Unity command (e.g., Aggressive/Circle/Swipe/In-and-Out become BombingRun
for bomber-only squads or Charge for barge-only squads). Ordinary squad
Move and Stop use authored ship offsets, 20-step formation compression,
nearest walkable-center fallback and each ship's shared A* path queue.

The source-matched movement phases of HiveMind **Hold**, **Random**,
**Patrol**, **Retreat**, **Closest Friendly**, and **Guard** are now wired. Random
finishes at arrival or command timeout; Patrol samples Unity's four-corner
route, rotates waypoints on its 3-second scaled callback, and uses the
separate configured patrol timeout. Retreat uses the Level's live target
squad position and weapon range to move directly away from a nearby enemy;
when already sufficiently far away it stops and finalizes after 3 seconds.
Unlike the other strategies, moving Retreat has no general timeout in Unity,
and its timer ends when all squad ships arrive. Delayed server responses must
still match the originally targeted enemy squad before activation.

Closest Friendly selects the nearest eligible living same-side squad in
Level registry order, excluding itself and squads already running Closest
Friendly. It retains the selected squad's full lifecycle identity and
rechecks the live target on each three-second scaled command callback
(the first callback is delayed, unlike Patrol). It issues
`MoveToTrackedPoint` formation orders instead of blindly invalidating
pathfinding: active search and failed-search retries keep ownership, and
endpoint drift less than Unity's eight-unit threshold does not restart
navigation. Following ends when the original squad disappears or the
configured follow time elapses. The three-second polling interval makes
Unity's additional one-second path-replan throttle redundant for this
specific command; the shared tracked-movement method is not yet suitable
for arbitrary faster-following callers. Missing eligible targets leave
both server OutcomeId reservations for explicit discard rather than
recording invented command rewards.
Guard picks the nearest eligible non-guarding same-side squad once and
retains its exact lifecycle identity. It assigns one of four guarding slots
(up, left, right, down) based on the number of current guards of that target,
then tracks the protected squad's center plus the guard's live bounds width
or height and four units. Its timer starts immediately and expires at the
configured guard duration; death of the protected squad ends guarding early.
Within 1.5 units of the guard destination, the native command sets speed to
the protected squad's slowest authored ship speed, clamped per ship to its own
base speed. Guard finalization, replacement and retirement restore the
surviving ships to their authored speed cap. The Level's current squad
registry supplies the active command, protected target, bounds, and speed;
this includes user/scripted Guard commands, not only native HiveMind orders.
When no eligible protected squad exists, the host must both discard the
server reservations and ban Guard for subsequent strategy requests. These
live relationships are not inferred from ship proximity or invented by the
training simulator. Unity's per-fixed-step movement speed reset and
Squad.IsMatchingSpeed behavior still need end-to-end parity work when the
full native Level update loop is implemented; the command's speed-setting
and restoration paths alone do not prove full Guard parity.

Commands update squads in stable lifecycle-ID order so independent worlds
do not depend on randomized hash-table traversal.
The game must supply real squad bounds, membership, configured speeds,
live enemy squad identity/position/range where required, shooting/targeting
state and scaled update time; the director does not invent missing game state.
Squad movement and command polling use the world's canonical indexed ship
lookups instead of rescanning every ship for each member.
Unported command types return an explicit Unsupported result with their
server reservations intact. Command reward
totals and unused shooting/targeting settlement still belong to gameplay
attribution, then the separate HiveMind durable StoreCommands pipeline.
No native full-game client, server adapter or executable loop is wired yet.

## Low-overhead performance and command measurements

`Simulation.measurements()` provides cumulative cross-world
per-fixed-step counters (ticks, decisions, events, active-body snapshots),
plus integrated body counts, broadphase candidates, narrowphase tests and
contact entries already counted by the shared physics core. Each
`World.measurements()` also exposes the same bounded counters for its
current episode; `reset_episode` clears only that world's measurements,
not the simulation's cumulative totals. Wall-clock timings are collected
**only** when the caller enables a nonzero sample period; the fixed-size
log2-nanosecond histogram provides bounded-memory approximate latency
percentiles via `sampled_step_percentile_upper_nanos`. Failed updates
track sampled elapsed time separately so they cannot bias successful-step
throughput. Snapshots can be
subtracted for experiment windows; the histogram can be aggregated without
storing every frame. Sampled timing does not yet include an attached native
HiveMind command update or an end-to-end PPO training pass.

`NativeSquadCommandDirector.measurements()` independently reports update
calls, active commands examined, recurring and timeout callbacks, started,
completed, superseded and retired command counts, and peak active commands.
The director uses a lifecycle-ordered map so command iteration is
deterministic and does not allocate or sort an owner list every frame.
The tradeoff is logarithmic command insertion and removal; the relative
performance needs profiling at 1v1 and large-battle sizes. These are
**instrumentation and algorithmic changes**, not measured speedups:
runtime comparisons remain necessary once a native executable loop exists. The shared simulation
stays platform-independent for Windows and Linux.

## v26 batched policy interface

`bees-sim::policy_batch::PolicyBatch` provides a reusable, row-major
inference boundary for the exact v26 policy shape: 7,743 observation
floats, 16 continuous outputs and six discrete branches per agent.
A game-side observation encoder can fill a row **directly** using
`push_encoded` without constructing an intermediate 7,743-float vector;
each unwritten feature remains NaN and invalidates the row. Batches
preserve insertion order and explicit `(WorldId, agent_id)` identities,
so an inference backend can share one invocation across many worlds.

The batch exposes contiguous output buffers for the actual model backend
and validates **every** returned action before publishing decisions:
finite continuous values, discrete branch ranges, available weapon slots,
and current executable special-action masks. Missing/partial outputs are
never silently replaced with zero actions. Both observations and output
storage retain capacity across decisions, and all action choices must be
provided by an actual model or explicit user control.

The exact Unity `RlPolicySchema.Signature` and ABI version are available
through `validate_model_abi`, to reject incompatible checkpoints even
when their tensor dimensions happen to match. The native policy module
also ports the source-defined episode progress, signed faction indicator
and 19 reserved observation features; inactive trajectories use zeros
except for the faction discriminator.

**Not yet implemented:** the remaining 7,722 perception features,
full ship/weapon/special-action execution, GPU model loading/inference,
and the ML-Agents-compatible learner/rollout loop. The new batch interface
is not a substitute for those gameplay or training contracts.

## Independent CPU rollout windows

`Simulation::run_parallel_window` advances each world through the **same**
`GameRules::step` as `Simulation::step_world`. Worlds are assigned to disjoint
CPU shards for multiple fixed steps per call; their state, RNG and
episode-local measurements are never shared. The single-world path remains
inline without launching threads. The caller supplies a policy/decision
callback and controls the number of CPU workers and the window length. The
callback reuses one action buffer per worker instead of allocating every tick.
The returned `ParallelWindow` includes real host wall elapsed and observed
world-steps/s, worker failure details, and summed physics measurements.
Individual failed worlds remain stopped until explicitly reset; panicking
workers invalidate the whole window.

This API is suitable for independent CPU inference or scripted rollout
windows where each arena's decisions can be generated locally. **Do not**
use it to invent surrogate neural decisions or force an externally batched
GPU policy into per-world synchronous calls. Batched GPU inference, rollout
transfer and PPO scheduling remain separate integration work.

## Cross-platform CPU throughput baseline

From the repository root, with Rust/Cargo installed on Windows or Linux:

```sh
cargo run --release --manifest-path native/Cargo.toml -p bees-sim --example rollout_throughput -- --worlds 16 --ships 2 --steps 1000 --workers 4
```

The CLI creates real authored Wasp/Gunship collider worlds, warms both
configurations, then compares the serial execution path against CPU-parallel
rollout windows. It emits CSV with observed wall seconds, world-steps/s and
the physics workload counters, so runs from different machines/configurations
can be compared. After each timed window, it compares the exact final
ship state, per-world RNG state, and physics-workload totals between
serial and parallel execution. This parity check is not included in the
reported elapsed time. Try larger `--ships` counts to measure arena-size scaling,
and adjust `--worlds` and `--workers` independently.

**Scope:** this is a movement/physics *baseline*, not a completed Bees game,
neural-inference benchmark or an end-to-end training measurement. The fixture
explicitly disables collision layers, firing, and AI because the authored
Unity collision-matrix and full game control path have not been integrated.
The production core retains its requirement for caller-supplied collision
rules; the benchmark does not change them.

## Current status

The portable `bees-sim` crate currently contains:

- A fixed-step multi-world dispatcher, per-world deterministic RNG, explicit
  episode reset and sampled timing/failure counters.
- The v26 policy's observation/action *shape* and actual action decoding:
  distinct team coordinate frames, dead zones, held turret aim, healing/fire
  exclusion and clamped communications.
- The existing RL-controlled ship motion algorithm: heading calculation,
  unrestricted timed turning, temporary current-speed behavior, stop sentinel
  and explicit hull-turn propagation to attached turret state.
- Base turret RL aim, three-pass readiness latch, authored cooldown callback
  contract, and firing eligibility.
- Projectile velocity, strict range expiry and continuation/command attribution.
- The reusable ScaledTimer state machine, including immediate execution and
  generation-preserving callback reuse semantics.
- Versioned native assets for **all 24 primary ship colliders**, migrated from
  their specific serialized `Entity.Collider` component references. The same
  collision catalog is loaded once per process and shared across worlds.
- Polygon, circular and box hulls (including concave polygon contact);
  reusable broadphase spatial grid; kinematic velocity integration; explicit
  layer-mask filtering; trigger-entry detection and per-step physics counters.
- Source-matched direct-projectile and explosion-contact eligibility rules,
  plus shared piercing/beam power budgets, Splitter Shot fragment generation,
  rocket acceleration and explosion hit tracking.
- A versioned asset catalog for **all 15 projectile types**, covering circles,
  rotated boxes, stationary explosions and the script-driven (non-colliding)
  Striker Bomb. The importer excludes visual-only impact animations.
- A shared, cached world-collider path for both ship and projectile geometry,
  including polygon-to-box and box-to-box trigger checks.
- A physics-only world adapter wired into the shared scheduler. It explicitly
  rejects PPO actions until the complete gameplay rules are migrated.
- A partial shared combat-world impact path with projectile trigger queues,
  health and TSV recalculation, shooter/Hive Mind attribution, death events,
  and source-consistent shot expiry and cleanup. Frigate rockets now accelerate
  with their authored speed cap and create untargeted explosions that inherit
  root-shot/command attribution and ownership without applying direct damage.
- Outgoing target-damage reservations use a single owner per reservation:
  weapon targeting/firing reserves once, projectile expiry releases once, and
  a rocket transfers its reservation to the resulting explosion before dying.
  Reservation totals continue to update with target health.
- Ended projectile physical/metadata storage is reused, so its capacity tracks
  peak concurrent shots instead of increasing across the entire run. World
  entity IDs remain monotonically increasing independently of pooled slots.
  Ships and shots spawn through the same authored collider catalogs in
  rendered/headless contexts.
- Striker Bomb's two-stage fuse, original target/shooter identity checks and
  target-relative attachment are implemented in the shared combat world.
- Frigate, Fire Barge and Fire Tank explosion animation events are imported
  from their Unity clips. Their radius changes modify the actual physics
  collider; harmlessness disables new damage; Kill releases the reservation
  and returns the projectile slot to the pool.
- Leafcutter SplitterShot now uses its authored four-way split, BeeSmall
  child speed, original shot/command attribution and untargeted child
  reservation semantics. Children spawn before a valid parent impact
  finishes; map-border contacts and ordinary range expiry do not split.
- Obstacle bodies now participate in the same kinematic physics as ships
  and projectiles, with a separate, layer-filtered obstacle trigger queue.
  Ordinary projectile impacts can damage collision asteroids, terminate
  against borders or record static-obstacle hits; rockets explode on
  non-border obstacles and explosion projectiles deduplicate asteroid hits.
  All **28 authored collision asteroid variants** are imported with both
  primary and child proximity polygons (including inherited vertex
  overrides, transform scale, size class, and separate collision layers). A
  second catalog provides **eight static box variants and four map borders**
  with authored dimensions and prefab-default poses, while a third imports the
  standalone **208-vertex Polygon Obstacle** gameplay collider (excluding its
  separate proximity sensor). Destruction events
  include Unity's size-class-dependent shard and piece counts. Destroyed
  collision asteroids leave the active ID registry and their inactive
  physics/metadata slots are recycled on subsequent spawns, with new entity
  IDs and world-local reuse metrics. End-of-level destruction suppresses
  fragment events. The shared physics now reports separate primary-hull
  and authored-proximity entry/exit contact streams, including directed
  asteroid-to-obstacle proximity contacts. Map borders track asteroid
  entry/exit history and remove exiting asteroids without fragmentation.
  The native asset catalog preserves authored OriginalHealth separately
  from an episode's effective training durability. The shared combat world
  now uses proximity and primary contacts for asteroid/asteroid collisions
  and asteroid/ship damage, with Unity's size comparison, health accounting
  and 0.35-second delayed destruction. Ship contacts require explicit
  authored SizeClass and OriginalHealth (never inferred from adjusted
  training durability); original health comes from server-loaded ship stats
  and SizeClass can legitimately be zero (Beacon and Human Target).
  Missing profiles fail instead of applying guessed damage. Trigger ordering
  still requires comparison with Unity. Configured arenas now use the exact
  RL scene's 13 shard-prefab and 18 debris-prefab palettes. Destroyed
  asteroids spawn real shared-physics shard entities with original size/
  collider shapes, per-prefab training health, inherited angular velocity,
  movement, authored border-entry flags and sibling collision exclusions.
  The dedicated shard pool retains prefab/speed across reuse; cosmetic pieces
  have pooled, non-colliding motion and timed fade/expiry state. All fragment
  draws use the scheduler's seeded world-local RNG. Map/pathfinder invalidation,
  Unity-exact random sequences/callback ordering, renderer integration and
  complete observer cleanup remain unimplemented.
- An optional tactical reward consumer using the current RL team's shared
  positive/negative asymptotic shaping budgets, with mined cargo excluded
  from persistent-fleet damage reward on death.

**Not yet implemented:** complete normal user/Hive Mind movement,
weapon-specific targeting and projectile spawning, remaining authored
static/border/mining obstacle prefabs and the full collision-layer matrix,
moving-asteroid AI, final ship collision-profile wiring from game assets,
other level-specific fragment palettes, complete map/pathfinder/observer
registry cleanup,
Unity-equivalent Animator/FixedUpdate/Physics2D ordering, native Hive Mind
command updates and persistence, squad/formation removal and full ship-death
lifecycle, complete RL reward delivery and episode termination, visibility
and observations, game modes, presentation, server/persistence, distributed
PPO and experiment orchestration.

The current native physics is a **partial implementation**, not a valid
standalone Bees battle. It must not produce training or evaluation samples
until full gameplay parity is achieved. Source tests and collision models
do not imply that Unity/Box2D physics parity is already established.

## Source-of-truth porting map

| Gameplay or service contract | Existing authoritative source |
| --- | --- |
| Authored primary ship hull assets | `Prefabs/Entities/Ships/*.prefab` via serialized `Entity.Collider` |
| Core geometry, timing and body orientation | `Scripts/Entities/Entity.cs`, `Scripts/Utilities.cs`, `Scripts/ScaledTimer.cs` |
| Base turret aim, targeting and shot cadence | `Scripts/Entities/Ships/Weapons/Turret*.cs` |
| Shared health, value and hit accounting | `Scripts/Entities/Ships/Ship.Combat.cs`, `Scripts/Utilities.cs` |
| Ported tactical reward budget | `Scripts/Scenes/RlOneVsOneReward.cs`, `RlOneVsOneEpisodeCoordinator.cs` |
| Ships, movement and subclass behavior | `Scripts/Entities/Ships/` |
| Weapons, projectiles, damage and collisions | `Scripts/Entities/Ships/Weapons/`, `Scripts/Entities/Projectiles/`, `Scripts/Entities/` |
| Scenes, game modes, map generation and UI | `Scripts/Scenes/`, `Scripts/Levels/`, `Scripts/UI Components/` |
| RL policy v26 and perceptions | `Scripts/Scenes/RlPolicySchema.cs`, `RlCombatPerception.cs`, `RlOneVsOneAgent.cs` |
| Rewards, visibility, episode termination | `Scripts/Scenes/RlOneVsOneEpisodeCoordinator.cs`, `RlOneVsOneReward.cs` |
| PPO/MA-POCA and distributed actors | `Training/bees_mlagents_structured_policy.py`, `bees_mlagents_ppo_compat.py`, `bees_wan_actor_worker.py` |
| Experiment scheduling and curriculum | `Training/bees_experiments.js`, `Training/experiment-curriculum.json` |
| Authentication, strategy learning and persistence | `BeesServer~/`, repository `allStackGit/BeesServer` |

The first end-to-end executable milestone must have both a rendered and
headless mode linked to the **same** actual combat implementation. Existing
runtime and experimental branches remain the reference until replacement
parity, cross-platform operation and performance are demonstrated.

## Build interface

From `native/`, when a Rust toolchain is installed, run
`cargo test --workspace` and `cargo check --workspace --all-targets`.
The simulation runtime is independent of Unity and Python; its immutable
asset catalog uses serde/serde_json once during initialization. The importer
is a separate, optional Node script for the migration period. These commands
exercise the library source and unit contracts; they do **not** prove game,
physics, model or distributed-system parity.

Every native gameplay feature must be implemented once in the shared core.
Rendering and learning modules must call that implementation rather than
copying its rules into platform-specific or training-only code.

## Authored collider migration

`native/assets/ship-colliders.json` is the versioned **native** source for
primary ship hulls. To regenerate it from current Unity source during migration,
run from a checkout of this repository:

```sh
node native/tools/import-unity-ship-colliders.mjs
```

The importer resolves the explicit root `Entity.Collider` file ID, including
negative IDs, and distinguishes the hull from attached sensing/range colliders.
It incorporates authored XY transform scale (the Human Target prefab uses
50×) and rejects unsupported shapes or ambiguous layouts. Once Unity assets
are retired, the native catalog remains authoritative and should be maintained
through native asset tooling rather than depending on Unity at runtime.

The physics layer accepts a required collision-layer matrix, but no verified
export of the existing Unity Physics2D matrix is yet available. Do not replace
this missing contract with an allow-all default in gameplay or training.

## Local native verification

From the repository root, with Node.js and a Rust/Cargo toolchain installed:

```sh
node native/tools/verify-native.mjs
```

This checks the five authored asset catalogs against the current Unity
prefabs **without rewriting them**, then compiles and runs the native Rust
tests. An optional `--assets-only` skips Cargo explicitly and does **not**
establish native compilation. These checks establish neither Unity
Physics2D parity nor complete battle readiness.

## Collision asteroid prefab migration

`native/assets/asteroid-colliders.json` (schema 3) contains all three
authored polygons for each of the 28 `Collision Asteroids/Asteroid N.prefab`
variants: primary damage hull (layer 19), nearby-entity proximity trigger
(layer 20), and separate navigation clearance mapping (layer 26).
Standalone prefabs and inherited Unity YAML overrides are decoded by a
single source importer:

```sh
node native/tools/import-unity-asteroid-colliders.mjs --check
```

Omit `--check` to regenerate. The importer uses the serialized
`Obstacle.Collider` reference and **honors the declared path-array size**:
serialized point overrides for inactive paths are not collision geometry.
It rejects unsupported multiple *active* polygon paths in any collider.
Proximity and clearance each apply their own authored child scale/offset
and layer, rather than scaling the primary hull by a guessed radius.
Navigation geometry is updated from the same obstacle pose as the combat
hull but is never included in damage/proximity contact streams.
`ObstacleBody.navigation_bounds`, `navigation_contains_point` and
`navigation_closest_point` expose the data used by Unity's
`Pathfinder.GetObstaclePoints`, and
`CombatWorld.dynamic_navigation_asteroids` enumerates the currently
live asteroids rather than keeping a stale pathfinder registration list.
The separate `NavigationGrid` now rasterizes live dynamic asteroids into
a caller-owned blocked-cell mask using Unity's four-unit grid scale,
2.5-second absolute-velocity bounds padding, five point-overlap probes,
closest-point contact and projected-velocity probe. Its explicit map-space
conversion callbacks preserve the Level's local/world transform boundary.
The `NavigationClearance` and `NavigationSearch` modules now consume this
same occupancy rasterizer for static obstacle masks and moving asteroids.
They reproduce Unity's two-pass clearance distances, signed-static egress
map, preferred-clearance penalties, eight-neighbor A* with diagonal
corner checks, static-obstacle egress, and route smoothing. Per-search
arrays and heap storage are reusable, with route points returned in
Level-local grid coordinates. The destination-clearance check and
nearest-walkable ring search are also available; the Level retains
responsibility for its exact ForceBounds behavior.

`NavigationClearance.are_statically_connected` caches source-equivalent
static connectivity regions by ship clearance. It permits blocked starts
through to static-egress recovery and clears cached components when static
geometry changes.

`CombatWorld` owns per-ship `NavigationRequests`: repeated requests
coalesce, changed destinations invalidate existing work, and old search
results are rejected by request and ship-lifecycle identity. A superseded
running job requeues only the most recent destination, using the ship's
current position once its prior worker completes. Invalidating an active
result preserves its occupied worker slot until it actually finishes.
The reusable `NavigationRuntime` owns the Level map's full affine transform,
static/dynamic clearance maps and A* arrays. It refreshes directly from
`CombatWorld`'s live obstacles; `refresh_world_navigation` and
`solve_next_world_navigation_job` provide the shared integration boundary.
For interactive native clients, `NavigationScheduler::pump` now connects
these APIs to bounded background CPU workers. Each worker reuses its own A*
arrays; a per-fixed-frame immutable clearance snapshot is shared by Arc.
Results are applied only on the world-owning thread. Pending static-layer
rebuilds wait until active searches finish, while newer dynamic-asteroid
snapshots can coexist with older in-flight searches. Worker shutdown drains
owned jobs without leaving the channel receiver blocked. The five-second
Unity search deadline is enforced only for these interactive workers, not
the deterministic synchronous/headless search API.

For normal gameplay, callers supply a `GameMovementProfile` with the
actual configured `ShipTurningRadius` and rendered ship half-extents.
Then `move_ship_to_point` applies the Level's explicit bounds
and collision-asteroid activation settings, sweeps the source-sized rotated
BoxCast against real layer-19 polygon/circle colliders, and chooses direct
movement or A*. Movement locks preserve the latest order and reevaluate
the actual obstacle geometry on unlock. `move_ship_direct`,
`move_ship_in_direction`, and accepted routes use `GameShipSteering`
in the same pre-physics step as RL-controlled `ShipMotion`. Failed routes
use Unity's five attempts separated by two-second timers; asteroid proximity
entries trigger replanning and recurring one-second nearby-asteroid checks
do not invalidate active workers. Route waypoints, egress counts, turning
dead bands, variable speed and StopMoving share the existing physics core.

**Integration still incomplete:** squad/Hive Mind/user command
dispatch must supply authored movement profiles and Level orders, and the
native game loop must call the path scheduler at the appropriate fixed-frame
boundary. The native sweep uses continuous authored-shape geometry but does
not promise Box2D's precise hit ordering or Unity's 16-hit non-alloc buffer.
Multi-map placement and gameplay/rendering parity remain unverified.
As requested, these changes have not been compiled or executed.

The catalog stores normal `SizeClass × CollisionAsteroidHealthIncrement` durability;
training must still pass its effective-health override when spawning, while
collision rules retain the original authored health.

## Fragment prefab palettes

The RL training scene's serialized `BreakawayAsteroids` and
`AsteroidPieces` GUID lists are imported to
`native/assets/asteroid-fragments.json`. To verify or regenerate them:

```sh
node native/tools/import-unity-asteroid-fragments.mjs --check
```

The shared `AsteroidFragmentPalette` preserves the scene's exact selection
list rather than choosing arbitrary small asteroid shapes. Set
`CombatWorld.configure_asteroid_fragments` with the game Level's map bounds,
half-extents, Stage maximum asteroid speed, and optional RL health ratio.
The scheduler automatically passes its world-local RNG through
`CombatWorldRules`; direct `step_fixed` deliberately rejects a configured
fragment world unless the caller uses `step_fixed_with_random`. Debris-piece
fade indexes are available to the future renderer; no independent training
fragment simulation exists. Reproducing historical Unity `.NET Random`
draw sequences and cross-episode pool persistence remains outstanding.

## Static boxes and map borders

`native/assets/box-obstacle-colliders.json` records the directly inherited
`_Obstacle` box variants, the two second-level `Slanted Variant` and `Box`
prefabs, and all four authored map-border variants. To verify
against the exact prefab source revisions:

```sh
node native/tools/import-unity-box-obstacle-colliders.mjs --check
```

Omit `--check` to regenerate. Static/border boxes can be spawned by name
through `BoxObstacleCatalog` and `CombatWorld.spawn_box_obstacle`, with an
optional map-specific pose. The imported box dimensions reflect prefab
scaling; **runtime map-dependent resizing is not yet integrated**. Other
obstacle layouts and unrecognized prefab inheritance chains remain outside
this catalog until their relevant gameplay colliders are traced.

## Standalone polygon obstacle

`native/assets/polygon-obstacle-colliders.json` is generated from the
standalone `Polygon Obstacle.prefab`:

```sh
node native/tools/import-unity-polygon-obstacle.mjs --check
```

The import follows `Obstacle.Collider`, not `ProximityCollider`, and
retains the entire concave primary polygon and the authored root scaling.
`PolygonObstacleCatalog` and `CombatWorld.spawn_polygon_obstacle`
share that geometry between rendering and training.

## Projectile collider migration

`native/assets/projectile-colliders.json` records all 15 non-None
`ConfigData.ProjectileTypes`. Its source importer is:

```sh
node native/tools/import-unity-projectile-colliders.mjs --check
```

Omit `--check` to regenerate from Unity prefabs. Both importers use an explicit
serialized collider reference and reject shape assumptions. These are transition
tools, not production runtime dependencies.

Frigate Rocket speed is exported from the prefab's `MaxSpeed` (160);
the explosion's authored collider is loaded from projectile type 13. During
migration, the native combat world requires one shared immutable
`Arc<ProjectileAssetCatalog>` at construction. Explosion event callbacks now run in the native
simulation, but their ordering against Unity Animator and Physics2D ticks
has not been verified in a running reference build.

The runtime shares one transformed-collider implementation between ships and
projectiles. Source-based shape geometry is necessary but **does not establish
Unity Physics2D callback/timestep parity**; validating that will require the
full gameplay integration and the original runtime.
