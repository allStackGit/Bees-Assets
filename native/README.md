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

**Not yet implemented:** complete ship types, normal user/Hive Mind movement,
all weapon variants, complete projectile/damage behavior, spatial physics and
collision resolution, visibility and observations, rewards and episode lifecycle,
gameplay modes, graphical presentation, server/persistence, distributed PPO and
experiment management.

The code calculates commands, timers and physics-facing velocity, **not a valid
standalone battle**. The real simulation will only be valid when those rules
are integrated with one shared collision and gameplay state implementation.
Do not use this incomplete engine for training or benchmark comparisons.

## Source-of-truth porting map

| Gameplay or service contract | Existing authoritative source |
| --- | --- |
| Core geometry, timing and body orientation | `Scripts/Entities/Entity.cs`, `Scripts/Utilities.cs`, `Scripts/ScaledTimer.cs` |
| Base turret aim, targeting and shot cadence | `Scripts/Entities/Ships/Weapons/Turret*.cs` |
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
This workspace is intentionally independent of the Unity Editor, Node, Python,
the GPU and the operating-system-specific windowing libraries. These commands
exercise the library source and unit contracts; they do **not** prove game,
physics, model or distributed-system parity.

Every native gameplay feature must be implemented once in the shared core.
Rendering and learning modules must call that implementation rather than
copying its rules into platform-specific or training-only code.
