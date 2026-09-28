# Bees Reinforcement Learning Design

This document records the canonical reinforcement-learning architecture for Bees. The original 1v1 proof has evolved into a reusable shared-policy training system. The policy interface described below is now a checkpoint compatibility contract: change training content freely where noted, but do not change the frozen observation/action/network ABI without intentionally starting a new incompatible policy generation.

## 1. End Goal

Train one shared neural-network policy capable of controlling either side and all ship types available to that side.

The same policy must adapt its behavior to the ship it controls and ultimately learn to:

- move and position ships;
- aim and independently control their weapons;
- use ship-specific capabilities;
- cooperate with friendly ships;
- navigate around relevant obstacles;
- react to Hive Mind-known enemies and environmental state;
- mine, heal, extract/warp, bomb, charge, deploy beacons, and use other represented ship mechanics where applicable;
- win battles while preserving as much persistent fleet value as practical;
- finish battles at a reasonable pace rather than stalling indefinitely.

Outside training, normally only one side will be NN-controlled. Training may use the same policy on both sides through self-play.

Expected battle scale is usually far below 100 ships, with roughly 100 ships being a practical upper-end battle size. The policy therefore uses bounded deterministic top-K tactical observations rather than an unbounded entity tensor.

## 2. Objective Priority

The objective is hierarchical:

1. **Win the battle.**
2. **Among successful strategies, preserve as much persistent fleet value as possible.**
3. **Among otherwise similar outcomes, finish sooner and avoid wasted time.**

The time preference is deliberately weak. The network should not sacrifice valuable ships merely to save a small amount of time. Its purpose is to prevent indefinite avoidance, stalling, or unnecessary delay rather than to force rushing.

A generous battle timeout should exist. Failing to win before timeout must not be an attractive strategy.

## 3. Reward Principles

The terminal win/loss result must dominate the reward.

TSV is used for intermediate reward shaping because it measures value destroyed or preserved rather than raw hit-point damage. Mining, healing, and successful extraction can also produce value-aligned capability reward through the episode coordinator. Reward magnitudes, curriculum distributions, matchup distributions, timeouts, map sizes, and other training parameters are tunable and do **not** form part of the neural-policy ABI.

Any real loss of a policy-controlled side's TSV must contribute to casualty-preservation shaping. Enemy damage credits the attacker and penalizes the damaged side. Friendly fire and unattributed/self/environmental damage penalize the damaged side without manufacturing positive credit for the same side or an arbitrary opponent. Ordinary enemy damage is shaped at impact and is not reconciled again at episode end, preventing double counting.

First side-wide discovery of strategically useful information also receives a deliberately small shaping reward. Discovery is defined by insertion into the side's shared Hive Mind/RL cache, not by each individual observer, so a second ship seeing an already-known object earns nothing. Enemy ships are valued by TSV. Mining asteroids, static obstacles, and map objects use their episode-start health/resource value as the available neutral-object value signal. Collision asteroids use size class because they can spawn dynamically throughout an episode.

Static discovery categories are normalized against the total discoverable value present when the episode begins. Current raw category budgets are:

- enemy ships: `0.06`;
- mining asteroids: `0.015`;
- static obstacles: `0.015`;
- map objects: `0.01`.

Collision asteroids have a separate raw budget of `0.025`. The Nth first side-wide collision-asteroid discovery uses the convergent weight `1 / ((N + 1) * (N + 2))`, multiplied by a bounded size-class weight. Therefore collision asteroids do not require a predicted episode-start spawn count, every finite new discovery retains a positive signal, and even an unbounded stream of spawned asteroids cannot consume more than the collision-asteroid discovery budget.

All **positive non-terminal shaping**, including combat/value outcomes, capability outcomes, and discovery, passes through one smooth asymptotic episode transform with a current maximum of `+2`. This is intentionally far below the `+10` terminal win reward. It is not a hard cap: the exact marginal transform remains positive for every finite positive outcome, so reaching a threshold never causes useful actions to stop receiving reward. Negative casualty/environmental shaping is not limited by this positive bound and cannot be used to reopen positive shaping headroom.

Discovery events that occur during battle startup remain represented in the shared caches until all currently policy-controlled ships have bound to their active RL agents. The coordinator then replays the cached first discoveries through per-episode ID guards, so startup timing cannot lose the discovery signal or award it twice.

Do **not** add tactical shaping such as rewards for moving toward an enemy, pointing at an enemy, flanking, or other hand-authored fighting behavior unless evidence proves it necessary. The network should be allowed to discover tactics itself.

### 3.1 Persistent fleet value

Current ship TSV is based on the ship type's configured maximum TSV and remaining health, plus minerals carried during the level. A destroyed ship has zero current TSV.

`MineralsMinedThisLevel` is intentionally part of TSV because minerals survive the battle if retained and can later be used to build ships.

The preservation objective is therefore not simply "lose the fewest hulls." Losing one valuable ship can be worse than losing multiple cheap ships.

### 3.2 Temporary spawned ships

Some ships can spawn additional ships for free during a battle. Those spawned units are real tactical assets and are dynamically assigned shared-policy agents when they require policy control, but temporary spawning must not manufacture persistent fleet-value reward.

## 4. Canonical Policy ABI v20

`Scripts/Scenes/RlPolicySchema.cs` is the executable checkpoint contract. Training startup validates it before agents are created, and the complete signature is emitted at startup. Resume a checkpoint only when its ABI signature matches.

Current identity:

- ABI version: `20`
- behavior name: `BeesRL1v1`
- vector observations: `7614`
- continuous actions: `16`
- discrete branches: five weapon-fire branches of `[2]`, followed by one special-action branch of `[5]`
- network: feed-forward, `128` hidden units, `3` layers, observation normalization enabled
- recurrent memory: none

ABI v20 freezes observation and action meaning, ordering, normalization, capacities, behavior identity, and network architecture. It also includes team-specific episode coordinate frames, per-slot weapon aim and fire, private allied communication, weapon-readiness latching, and healing's weapon-exclusive action rule. Changes to these semantics require an intentional incompatible policy generation.

### 4.1 What is frozen

The following are checkpoint-sensitive:

- observation count, order, meaning, and normalization;
- entity ordering and identifier encodings;
- fixed observation capacities;
- action count, branch order, branch sizes, and meanings;
- behavior name;
- network architecture and recurrence choice;
- coordinate-frame, weapon-readiness, communication, and special-action semantics.

Changing any of these requires a new ABI version and must be treated as incompatible with existing checkpoints.

### 4.2 What remains tunable

These may change while continuing a compatible ABI-v20 network:

- reward magnitudes and balancing;
- curriculum and matchup distributions;
- ships per side, provided fixed observation and weapon-slot limits are respected;
- map size and environment generation;
- episode duration and timeouts;
- PPO optimization hyperparameters that do not alter network architecture;
- self-play scheduling and evaluation criteria.

## 5. Observation Layout

The policy receives one fixed vector of `7614` values. The executable block sizes in `RlCombatPerception` total `7593`; the agent then appends one episode-progress value and twenty reserved values. Unused fixed-capacity slots are zero-filled.

| Observation block | Capacity and size |
| --- | ---: |
| Self state | 25 |
| Capability state | 12 |
| Parent carrier | 40 |
| Allies | 64 x 44 |
| Enemies | 64 x 40 |
| Own weapons | 5 x 15 |
| Mining asteroids | 8 x 7 |
| Map objects | 64 x 12 |
| Moving collision asteroids | 48 x 11 |
| Objective reservation | 16 |
| Local navigation grid | 21 x 21 |
| Team exploration grid | 16 x 16 |
| Episode progress | 1 |
| Reserved tail | 20 |

Ship observations use deterministic ordering by distance, type, fleet ID, and runtime ID. Relative directions and grids use the team-specific quarter-turn policy frame. Ship and weapon type values use the frozen scrambled scalar maps; map-object type uses four bits. The exact normalization and field order are defined in `RlCombatPerception` and `RlPolicySchema.Signature`.

Each ally slot includes a four-value private communication vector. Carrier children receive a dedicated observation of their live parent carrier even when it is outside the nearest-ally capacity. The objective block is currently reserved and zero-filled. The policy has no enemy weapon-mount slots in this ABI.

The policy supports five authored weapon slots. Training rejects a controlled ship with more than five authored weapons instead of aliasing excess weapons onto an existing action. Weapon list order is the stable slot identity.

## 6. Action Layout

### 6.1 Continuous actions — 16

- movement X and Y;
- one aim X/Y pair for each of the five authored weapon slots;
- four private communication values.

Each weapon retains its own aim direction. A non-dead-zone aim pair updates only that weapon slot.

### 6.2 Weapon-fire branches — five branches of two choices

For each authored weapon slot:

- `0`: cease fire for that slot;
- `1`: fire that slot.

A branch controls only its matching weapon. Missing or unsupported slots are masked or ignored according to the binding code.

### 6.3 Special-action branch — five choices

The choices are no action, ship-specific special action, mine, heal, and warp/extract. The exact eligibility masks and ship-specific behavior are maintained by `RlOneVsOneAgent`. Healing is exclusive with weapon fire for that decision.

The current ABI has no discrete ally, enemy, or map-object target branches. Future objective or targeting changes must use compatible reserved observation capacity only when their semantics can be added without changing the frozen checkpoint interface; otherwise they require a new ABI version.

## 7. Special Mechanics and Temporal State

ABI v20 deliberately remains feed-forward. Bees already supplies persistent Hive Mind knowledge for discovered living enemies, and important ability timing is represented explicitly rather than forcing the network to infer it through recurrence.

Examples:

- Barge exposes wind-up, active charge, cooldown phase, and normalized time until ready.
- Scout exposes remaining beacon capacity and cooldown.
- Striker exposes bomb readiness and its dedicated live parent-carrier state.
- mining/healing/warp eligibility is explicit.

If a later mechanic needs history, prefer adding semantics to already-reserved fields where valid. Adding recurrent memory to ABI v20 is not checkpoint-compatible.

## 8. Barge Charge Lifecycle

The Barge charge action reserves its RL charge phase immediately when wind-up begins. `SetChargePhase(1)` occurs before the first coroutine yield, preventing repeated policy decisions during the wind-up from reserving overlapping charge coroutines.

`HasStartedCharging` retains its historical campaign meaning: it becomes true only after the wind-up completes, immediately before active charge movement begins. Charge phase state is reset during pooled lifecycle cleanup and guarded by regression tests.

## 9. Dynamic Agent Provisioning and Reset

Training periodically counts all ships that require policy control, including dynamically spawned and capability-only ships, and provisions enough agents for the active side/team.

An agent owns one physical ship lifecycle per trajectory. Episode begin resets:

- bound ship state;
- participation state;
- runtime ship identity;
- decision counter;
- mining/healing action timers;
- retained per-weapon aim directions.

Releasing a ship clears direct RL turret control. Ship-specific pooled lifecycle cleanup remains responsible for resetting its own gameplay state.

## 10. Trainer Architecture

The canonical ABI-v20 network is the current ML-Agents PPO network:

- `normalize: true`
- `hidden_units: 128`
- `num_layers: 3`
- no recurrent `memory` block

The optimizer, reward, horizon, checkpoint, and self-play settings in `Training/rl_1v1_config.yaml` may be tuned as evidence accumulates so long as the network architecture itself remains compatible.

## 11. Training Progression

The original small 1v1 experiment remains useful as the first curriculum stage, but it is no longer the definition of the policy interface. The same ABI-v20 network should be retained while training complexity expands.

Recommended progression:

1. small armed 1v1 matchups on tiny maps;
2. broader randomized ship matchups, including asymmetric ones;
3. multiple ships and coordination;
4. collision/static environment complexity;
5. mining, healing, extraction, carrier-child, and other special mechanics;
6. larger squads and realistic battle compositions;
7. explicit objective modes using the reserved objective/target channels when needed.

Uneven matchups are useful. A weaker ship can still learn better survival, positioning, damage exchange, and cooperation behavior even when its isolated matchup is unfavorable.

Sampled 1v1 training uses a shuffled Cartesian Bee x Human cycle. Sampled multi-ship training samples unordered compositions with replacement uniformly on each side, rejects compositions in which an entire side is weaponless, and shuffles the accepted composition into formation slots so slot order does not become coupled to canonical combination order. Recent heavily imbalanced matchups may receive bounded extra replay weight without removing the permanent baseline sampling probability of any valid matchup.

## 12. Validation Gate Before Canonical Long Training

Before treating a long run as a keep-forever canonical checkpoint series:

- Unity must compile the branch;
- `RlPolicySchemaContractTests` and the relevant EditMode/Foundation tests must pass;
- the real training-scene PlayMode smoke must instantiate and bind the directly trainable roster through the shared policy path;
- the training scene must start and print ABI v20 with `observations=7614`, `continuous_actions=16`, `weapon_fire_branches=5x2`, and `special_branch=5`;
- every ship type intended for the curriculum must successfully bind without a schema overflow error;
- a short multi-episode smoke run must demonstrate clean resets and dynamic agent provisioning.

Once that gate passes, subsequent curriculum/reward tuning should not require discarding ABI-v20 checkpoints.

## 13. Lessons Carried Forward From Ants

The Ants project never established reliable fresh-start learning despite long runs. Bees RL should explicitly avoid repeating that failure mode.

Requirements for Bees RL work:

- prove actual fresh-start learning before assuming long duration will solve a failure;
- keep early curriculum stages small enough that failures are understandable;
- distinguish evaluator/training-pipeline correctness from learner capability;
- preserve evidence for reward, policy behavior, and win-rate trends;
- add environmental complexity incrementally while retaining the same policy ABI;
- do not call the system successful merely because infrastructure tests are green while actual behavior does not improve.

## 14. Intentional Future Changes

Reward design, curricula, environment distributions, self-play scheduling, qualification, and empirical TSV/value improvements remain open to evidence-driven iteration.

Observation/action/network changes are no longer ordinary tuning. Any such change must be reviewed as a policy ABI change, assigned a new schema version, and assumed checkpoint-incompatible unless proven otherwise.
