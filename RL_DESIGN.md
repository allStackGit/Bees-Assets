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

Do **not** add tactical shaping such as rewards for moving toward an enemy, pointing at an enemy, flanking, or other hand-authored fighting behavior unless evidence proves it necessary. The network should be allowed to discover tactics itself.

### 3.1 Persistent fleet value

Current ship TSV is based on the ship type's configured maximum TSV and remaining health, plus minerals carried during the level. A destroyed ship has zero current TSV.

`MineralsMinedThisLevel` is intentionally part of TSV because minerals survive the battle if retained and can later be used to build ships.

The preservation objective is therefore not simply "lose the fewest hulls." Losing one valuable ship can be worse than losing multiple cheap ships.

### 3.2 Temporary spawned ships

Some ships can spawn additional ships for free during a battle. Those spawned units are real tactical assets and are dynamically assigned shared-policy agents when they require policy control, but temporary spawning must not manufacture persistent fleet-value reward.

## 4. Canonical Policy ABI v3

`Scripts/Scenes/RlPolicySchema.cs` is the executable policy contract. Training startup validates it before agents are created. `Tests/EditMode/RlPolicySchemaContractTests.cs` guards the same contract against accidental drift.

Current identity:

- ABI version: `3`
- behavior name: `BeesRL1v1`
- vector observations: `4685`
- continuous actions: `34`
- discrete branches: `16 x [2]`, then `[5, 65, 65, 65]`
- network: feed-forward, `128` hidden units, `2` hidden layers, observation normalization enabled
- recurrent memory: none

The 34 continuous actions are two movement values plus two independent aim values for each of the 16 authored weapon slots. The first 16 discrete branches independently cease/fire those same weapon slots. ABI v3 intentionally supersedes the earlier shared-aim/single-weapon-command ABI before canonical long-term training.

The complete schema signature is emitted at training startup. A checkpoint should be resumed only when its ABI signature matches.

### 4.1 What is frozen

The following are checkpoint-sensitive and must not change for ABI v3:

- observation count, order, meaning, and normalization semantics;
- entity ordering semantics;
- action count, branch order, branch sizes, and action meanings;
- ship/weapon/map-object identifier encodings;
- fixed observation capacities;
- behavior name;
- network architecture and recurrence choice.

Changing any of these requires an intentional new ABI version and should be treated as incompatible with existing canonical checkpoints.

### 4.2 What remains tunable

The following may be changed while continuing an ABI-v3 network:

- reward magnitudes and reward balancing;
- curriculum and matchup distributions;
- ships per side, provided observation semantics remain top-K and individual ships fit the fixed weapon schema;
- map size and environment generation;
- episode duration/timeouts;
- PPO optimization hyperparameters that do not alter network architecture;
- self-play scheduling and opponent-window settings;
- evaluation/qualification criteria.

## 5. Observation Layout

The policy receives one fixed vector. Unused slots are zero-filled so curriculum expansion does not change shape.

### 5.1 Identifier capacity

- ship type: 6 bits (`0-63`)
- weapon type: 6 bits (`0-63`)
- map-object type: 4 bits (`0-15`)

Current enum values are validated at training startup so an out-of-range new type cannot silently alias an existing identity.

### 5.2 Self state — 29 values

Self state includes:

- ship type;
- normalized absolute position and map dimensions;
- heading;
- health;
- movement/rotation values;
- physical size;
- sight, range, and firepower;
- mobility/bomber/carrier/weapon/turret flags;
- special-action presence/readiness;
- normalized friendly and Hive Mind-known ship counts.

### 5.3 General capability state — 12 values

A permanent generalized capability block exposes:

- ship-specific special-action presence;
- immediate readiness;
- remaining resource/charges;
- normalized time until ready;
- current ability phase;
- mining eligibility;
- healing eligibility;
- warp/extraction eligibility;
- carrier-child identity;
- whether a live parent carrier exists;
- two reserved capability channels.

This block is intentionally generalized so additional state for existing capability classes can be mapped without changing tensor size.

### 5.4 Parent carrier — 19 values

Carrier children receive a dedicated full entity observation for their live parent carrier. Other ships receive zeros.

This is required especially for Strikers, which must return to their specific carrier to reload. The relationship must not disappear merely because that carrier falls outside the generic nearest-allies list.

### 5.5 Friendly and enemy ships — 64 + 64 slots

Each entity slot contains 19 values including:

- presence;
- relative position;
- heading;
- health;
- movement/size/range/firepower state;
- mobility/bomber flags;
- 6-bit ship type.

Candidates are sorted deterministically by:

1. distance from the controlled ship;
2. ship type;
3. fleet ID;
4. runtime ID.

The permanent policy semantic is **nearest/relevant bounded state**, not "observe every ship no matter how large a battle becomes." Hive Mind may remember more entities than fit in the vector.

### 5.6 Own weapons — 16 slots

Each authored weapon slot receives 19 values including weapon identity, local position, combat characteristics, turret heading/readiness, and target/aim state where applicable.

Weapon list order is the stable slot identity. Continuous aim pair N and discrete fire branch N control exactly authored weapon N.

Excess weapons are never collapsed onto the final action. If a policy-controlled ship has more than 16 authored weapon slots, training is rejected by `RlPolicySchema.TryValidateShip` so the incompatibility is discovered before silently training the wrong control mapping.

### 5.7 Enemy weapon mounts — 16 slots

The policy has 16 detailed enemy weapon-mount observations. They expose owner ship type, weapon type, relative mount position, range/power/rotation characteristics, and turret heading/readiness where relevant. These supplement the aggregate range/firepower state already carried by every observed enemy ship.

### 5.8 Mining asteroids — 8 slots

Mining asteroid observations include relative position, resource fraction, geometry, and mining activity. Mining asteroids are resources rather than ship collision hazards.

### 5.9 Map objects — 64 slots

Map objects use fixed typed slots with relative geometry, health/activity, and targetability state.

### 5.10 Moving collision asteroids — 48 slots

Collision asteroid observations include geometry, heading, velocity, health, and destructibility.

### 5.11 Static navigation grid — 13 x 13

Persistent static collision geometry and map boundaries are represented in a local `13 x 13` occupancy grid with 10-unit cells.

### 5.12 Reserved objective state — 16 values

Sixteen values are permanently reserved for explicit future objective state such as defend/capture/escort/reach-location summaries. They are currently zero-filled.

Future objective mechanics must populate this reserved block or use the already-reserved entity-target branches rather than increasing observation shape.

### 5.13 Projectiles

Individual projectiles are intentionally not observed for projectile dodging. Dodging individual shots is outside the intended policy behavior and would add large unnecessary state.

## 6. Action Layout

### 6.1 Continuous actions — 34

- movement X
- movement Y
- weapon slot 0 aim X/Y
- weapon slot 1 aim X/Y
- ...
- weapon slot 15 aim X/Y

Movement controls the real ship movement primitive. Every authored weapon slot has its own retained aim direction. A non-dead-zone aim pair updates only that slot's direction, so all turrets can hold different targets and can be updated independently in the same policy decision.

### 6.2 Weapon fire branches — 16 branches x 2 choices

For each authored weapon slot N:

- `0`: cease fire for slot N
- `1`: fire slot N

All 16 branches are read every decision. A slot's fire command is paired with that slot's independent continuous aim direction. Missing/non-turret authored slots ignore the command and their fire action is masked when the agent is bound.

### 6.3 Special-action branch — 5 choices

- no special action
- ship-specific special action
- mine
- heal
- warp/extract

Ship-specific handling currently covers mechanics such as Yellow Jacket detonation, Striker bombing, Fire Barge detonation, Barge charging, and Scout beacon deployment. Mining, Beehive healing, and Warp Gate extraction are primitive spatial capabilities exposed separately.

Queen/Carrier spawning behavior that is automatic game logic is intentionally not converted into an artificial policy button. Spawned units that need control are provisioned shared-policy agents dynamically.

### 6.4 Reserved entity-target branches

Three target-selection branches are permanently reserved:

- ally target: 65 choices (`none + 64 slots`)
- enemy target: 65 choices (`none + 64 slots`)
- map-object target: 65 choices (`none + 64 slots`)

They are currently masked except for `none`. A future mechanic that genuinely requires explicit entity selection can use them without changing action shape.

## 7. Special Mechanics and Temporal State

ABI v3 deliberately remains feed-forward. Bees already supplies persistent Hive Mind knowledge for discovered living enemies, and important ability timing is represented explicitly rather than forcing the network to infer it through recurrence.

Examples:

- Barge exposes wind-up, active charge, cooldown phase, and normalized time until ready.
- Scout exposes remaining beacon capacity and cooldown.
- Striker exposes bomb readiness and its dedicated live parent-carrier state.
- mining/healing/warp eligibility is explicit.

If a later mechanic needs history, prefer adding semantics to already-reserved fields where valid. Adding recurrent memory to ABI v3 is not checkpoint-compatible.

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

The canonical ABI-v3 network is the current ML-Agents PPO network:

- `normalize: true`
- `hidden_units: 128`
- `num_layers: 2`
- no recurrent `memory` block

The optimizer, reward, horizon, checkpoint, and self-play settings in `Training/rl_1v1_config.yaml` may be tuned as evidence accumulates so long as the network architecture itself remains compatible.

## 11. Training Progression

The original small 1v1 experiment remains useful as the first curriculum stage, but it is no longer the definition of the policy interface. The same ABI-v3 network should be retained while training complexity expands.

Recommended progression:

1. small armed 1v1 matchups on tiny maps;
2. broader randomized ship matchups, including asymmetric ones;
3. multiple ships and coordination;
4. collision/static environment complexity;
5. mining, healing, extraction, carrier-child, and other special mechanics;
6. larger squads and realistic battle compositions;
7. explicit objective modes using the reserved objective/target channels when needed.

Uneven matchups are useful. A weaker ship can still learn better survival, positioning, damage exchange, and cooperation behavior even when its isolated matchup is unfavorable.

## 12. Validation Gate Before Canonical Long Training

Before treating a long run as a keep-forever canonical checkpoint series:

- Unity must compile the branch;
- `RlPolicySchemaContractTests` and the relevant EditMode/Foundation tests must pass;
- the training scene must start and print ABI v3 with `observations=4685`, `continuous_actions=34`, `weapon_fire_branches=16x2`, `special_branch=5`, and 65-choice ally/enemy/map-object target branches;
- every ship type intended for the curriculum must successfully bind without a schema overflow error;
- a short multi-episode smoke run must demonstrate clean resets and dynamic agent provisioning.

Once that gate passes, subsequent curriculum/reward tuning should not require discarding ABI-v3 checkpoints.

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