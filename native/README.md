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

## Current status

The dependency-free `bees-sim` crate currently contains:

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
- A partial shared combat-world impact path for direct, piercing and explosion
  projectiles: queued trigger processing, health and TSV recalculation, shooter
  and Hive Mind command attribution, death events, strict shot expiry, and
  source-consistent projectile-end callback ordering. Ships and shots spawn
  through the same authored collider catalogs in rendered/headless contexts.
- An optional tactical reward consumer using the current RL team's shared
  positive/negative asymptotic shaping budgets, with mined cargo excluded
  from persistent-fleet damage reward on death.

**Not yet implemented:** complete normal user/Hive Mind movement,
weapon-specific targeting and projectile spawning, Striker Bomb delayed damage,
rocket explosion creation, native Hive Mind command updates and persistence,
squad/formation removal and full ship-death lifecycle, complete RL reward
delivery and episode termination, Unity-equivalent physics callback ordering,
obstacle contacts and the full collision-layer matrix, visibility and
observations, game modes, presentation, server/persistence, distributed PPO
and experiment orchestration.

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

## Projectile collider migration

`native/assets/projectile-colliders.json` records all 15 non-None
`ConfigData.ProjectileTypes`. Its source importer is:

```sh
node native/tools/import-unity-projectile-colliders.mjs --check
```

Omit `--check` to regenerate from Unity prefabs. Both importers use an explicit
serialized collider reference and reject shape assumptions. These are transition
tools, not production runtime dependencies.

The runtime shares one transformed-collider implementation between ships and
projectiles. Source-based shape geometry is necessary but **does not establish
Unity Physics2D callback/timestep parity**; validating that will require the
full gameplay integration and the original runtime.
