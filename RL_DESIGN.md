# Bees Reinforcement Learning Design

This document records the agreed initial design for reinforcement learning in Bees. It is intentionally narrower than a full implementation plan. The first priority is to prove that learning works in a small, understandable experiment before building reusable RL infrastructure.

## 1. End Goal

Train a single shared neural-network policy capable of controlling either side and all ship types available to that side.

The same policy must be able to control different ship types according to their actual capabilities. It should ultimately learn to:

- move and position ships;
- aim and control their weapons;
- select useful targets;
- cooperate with friendly ships;
- navigate around relevant obstacles;
- adapt its behavior to its own ship type, friendly forces, enemy forces, and map state;
- win battles while preserving as much persistent fleet value as practical;
- finish battles at a reasonable pace rather than wasting time or avoiding the opponent indefinitely.

Outside training, normally only one side will be NN-controlled. Training may use the same policy on both sides.

Expected battle scale is usually far below 100 ships, with roughly 100 ships being a practical upper-end battle size.

## 2. Objective Priority

The objective is hierarchical:

1. **Win the battle.**
2. **Among successful strategies, preserve as much persistent fleet value as possible.**
3. **Among otherwise similar outcomes, finish sooner and avoid wasted time.**

The time preference is deliberately weak. The network should not sacrifice valuable ships merely to save a small amount of time. Its purpose is to prevent indefinite avoidance, stalling, or unnecessary delay rather than to force rushing.

A generous battle timeout should exist. Failing to win before timeout must not be an attractive strategy.

## 3. Reward Principles

The terminal win/loss result must dominate the reward.

A small elapsed-time cost is applied so that otherwise-equivalent strategies prefer the one that finishes sooner.

TSV is used for intermediate reward shaping because it measures value destroyed rather than raw hit-point damage. TSV shaping remains subordinate to actually winning the battle.

For the first Wasp-vs-Gunship proof, the initial tunable reward magnitudes are:

- win: `+10`;
- loss: `-10`;
- TSV exchange: normalized by the combined starting TSV with scale `1`;
- elapsed time: at most `-0.1` for lasting the entire 120-second timeout.

These numbers are initial proof settings rather than permanent balance constants. They are deliberately separated by an order of magnitude so that a win is always much more important than shaping.

Do **not** add tactical shaping such as rewards for moving toward an enemy, pointing at an enemy, flanking, or other hand-authored fighting behavior unless later evidence proves it necessary. The network should be allowed to discover tactics itself.

### 3.1 Current TSV behavior

Current ship TSV is based on the ship type's configured maximum TSV and remaining health, plus minerals carried during the level:

`current TSV = max ship-type TSV * health factor + carried minerals while alive`

For a living ship:

`health factor = (max health + current health) / (2 * max health)`

A destroyed ship has zero current TSV.

This means damaging a ship removes part of its value, while destroying it removes the substantial remaining value. That is desirable for an intermediate combat signal because damaging a valuable target matters, but eliminating it matters more.

### 3.2 Minerals are persistent value

`MineralsMinedThisLevel` is intentionally part of TSV.

A ship carrying mined minerals has greater strategic value because those minerals survive the battle if retained and can later be used to build ships. The RL objective should therefore care more about preserving a mineral-carrying ship than an otherwise identical empty ship.

### 3.3 Persistent fleet value, not raw casualty count

The preservation objective is **not** simply "lose the fewest hulls."

It is better described as preserving as much persistent fleet value as possible. Losing one very valuable ship can therefore be worse than losing multiple cheap ships.

### 3.4 Ships spawned during a level

Some ships can spawn additional ships for free during the battle. Those spawned ships do not persist after the level.

Therefore:

- spawning a temporary ship must not directly create persistent-value reward;
- surviving temporary spawned ships must not inflate the final persistent fleet-value score;
- losing a temporary spawned ship is not a persistent fleet casualty;
- temporary spawned ships are still real tactical assets and threats during combat, so destroying an enemy temporary ship can still be useful to the combat-learning signal.

The exact dense-reward weighting for temporary spawned ships is not yet fixed. The important requirement is that free temporary spawning must not become a way to manufacture persistent reward.

### 3.5 Manual TSV values

Current maximum TSV values are developer-rated. They are acceptable for the first RL experiments because they already provide a useful value-weighted combat signal.

Longer term, it is desirable to reduce or eliminate manually assigned combat value. A possible future approach is to estimate a ship type's combat value empirically from battle outcomes/self-play, for example by measuring how much the presence of that ship changes expected battle success.

Persistent economic value and learned combat usefulness do not necessarily need to be the same quantity.

## 4. Observations

The policy should use entity-based observations rather than fixed slots such as "enemy 1, enemy 2, enemy 3." Fleet sizes and obstacle counts vary.

Most spatial information should be expressed relative to the ship being controlled where practical, so the same policy does not need to relearn identical geometry at different absolute map positions.

### 4.1 Self

Each controlled ship must at minimum know:

- its position;
- its ship type;
- its current health / maximum health.

Additional movement/orientation state that is required to control the real ship correctly should be included when the action interface is finalized.

### 4.2 Friendly ships

Each controlled ship should observe every friendly ship, including at minimum:

- position relative to the controlled ship;
- ship type;
- health / maximum health.

Additional state should be included only where it is actually relevant to decision-making for real Bees ship mechanics.

### 4.3 Enemy ships and Hive Mind vision

A controlled ship should observe every enemy currently known through its side's Hive Mind vision, including at minimum:

- position relative to the controlled ship;
- ship type;
- health / maximum health.

The intended Hive Mind knowledge rule is persistent: once any appropriate friendly observer has seen an enemy ship, that side's Hive Mind continues to know that living enemy's current location/state even after it leaves sensor range. The sighting should disappear when the enemy itself is removed/destroyed, and level reset should clear the memory.

A separate branch (`fix/persistent-hivemind-vision`) contains the change that prevents the death of the original observer from erasing the remembered enemy. This RL branch was intentionally created from `main`, so that dependency must be reconciled before RL relies on the corrected behavior.

### 4.4 Weapons

A controlled ship must know enough about each of its weapons to aim and use it correctly, including:

- weapon position relative to the ship;
- weapon orientation/aim state where applicable;
- weapon identity/type and the state necessary to use its actual mechanics;
- fire/charge state where applicable.

For ordinary cooldown-based weapons with unlimited ammunition and no friendly-fire risk, repeatedly requesting fire while the weapon is cooling down is acceptable: the real weapon mechanics can enforce the rate of fire.

Charge weapons are different. Their action and observation interfaces must expose enough information for the network to learn when to charge, hold, and release.

There is no limited ammunition and no friendly-fire risk to manage.

### 4.5 Collision obstacles

The network must observe obstacles that ships can collide with, including static obstacles and collision asteroids.

It needs enough information to understand the obstacle's relevant geometry rather than only its center point. For moving asteroids, it also needs enough movement state to predict and avoid their path.

The exact geometric representation is not yet fixed.

### 4.6 Mining asteroids

Mining asteroids should be observable, but they are not collision hazards for ships.

Their observation can therefore remain much simpler, initially including:

- position relative to the controlled ship;
- health / remaining mineable state as represented by the game.

### 4.7 Projectiles

Do **not** include projectile observations for projectile dodging. Dodging individual projectiles is outside the intended behavior and would create a large amount of unnecessary observation data.

## 5. Actions

The final policy must be capable of controlling, for each ship:

- movement;
- weapon aiming;
- firing;
- charge-weapon behavior where applicable.

The exact numerical/discrete action representation has not yet been decided and should be based on the real movement and weapon mechanics rather than invented independently of them.

## 6. Network Structure and Scaling

Use one shared policy for all controlled ships and both factions. Ship type and relevant ship/weapon state allow the same network to behave differently for different units.

The observation architecture should support variable-size sets of:

- friendly ships;
- Hive Mind-known enemy ships;
- collision obstacles;
- mining asteroids;
- weapons belonging to the controlled ship.

A shared entity encoder with pooling or attention is a likely long-term structure. Avoid hard-coding a maximum sequence of semantically ordered entity slots.

At the expected scale (normally well below 100 total ships), inference should be practical. Outside training only one side normally requires NN inference.

If repeated per-ship encoding later becomes expensive, the world entities can be encoded once per decision step and queried by each controlled ship. Do not build that optimization before actual workload demonstrates a need.

## 7. First Learning Experiment

The first experiment must remain small and disposable. Its purpose is to prove that Bees RL can learn at all, not to construct the final training platform.

The first proof is now fixed as:

1. Dedicated scene: `RL 1v1 Training`.
2. Map: `120 x 120`, reusing the normal Space/Titania map plumbing but resizing it only for this scene.
3. Bee ship: one **Wasp**.
4. Human ship: one **Gunship**.
5. No static obstacles, collision asteroids, mining asteroids, fog of war, reinforcements, or other environment dimensions.
6. Starting positions are randomized on opposite points of a circle with radius 30, giving 60 units of initial separation.
7. Initial ship facings are randomized independently.
8. Both sides are intended to use the same shared policy during training.
9. Episode ends when one side dies or after **120 seconds**.
10. Reward uses dominant win/loss (`+10/-10`), smaller normalized net TSV exchange, and a maximum full-episode time penalty of `-0.1`.
11. Repeat rapidly from fresh starts.

Wasp-versus-Gunship is intentionally allowed to be asymmetric. A nearly always-losing ship can still receive useful intermediate learning signal because better behavior destroys more enemy TSV before losing. Terminal win/loss remains dominant so dealing damage and dying cannot become preferable to winning.

The first success criterion is behavioral learning from a fresh network: movement and weapon control should improve from random behavior toward engaging, aiming, firing, and producing progressively better combat outcomes. Fresh-start learning must be demonstrated rather than inferred from checkpoint continuation.

## 8. Expansion Strategy

Do not build a large curriculum, qualification framework, evaluator stack, or generalized RL system before the first experiment learns.

Once a small experiment learns reliably:

- change one major source of complexity at a time;
- add additional ship types;
- add multiple ships and coordination;
- add collision obstacles and moving asteroids;
- add mining/economic behavior where appropriate;
- progress toward realistic battles.

Uneven matchups are useful because the network should eventually learn how to behave when a direct fight is unfavorable. TSV shaping makes an asymmetric first duel usable because losing episodes can still distinguish better and worse combat behavior.

## 9. Lessons Carried Forward From Ants

The Ants project never established reliable fresh-start learning despite long runs. Bees RL should explicitly avoid repeating that failure mode.

Requirements for early Bees RL work:

- prove actual learning before building extensive infrastructure;
- keep the first experiment small enough that failure is understandable;
- distinguish evaluator/training-pipeline correctness from learner capability;
- test fresh-start learning separately from checkpoint continuation;
- do not interpret long training duration as evidence that learning will eventually appear;
- do not call the system successful because tests are green while actual fresh-start behavior does not improve;
- preserve enough evidence to see whether reward, policy behavior, and win rate actually improve;
- add complexity only after the simpler predecessor works.

## 10. Still Open

The following decisions have deliberately not yet been fixed:

- exact observation fields beyond the agreed minimum for movement/orientation and special ship states;
- exact representation of obstacle geometry;
- exact movement action representation;
- exact weapon/charge action representation;
- policy architecture details (pooling vs attention, embedding sizes, etc.);
- RL algorithm and training hyperparameters;
- exact treatment/weighting of temporary spawned ships in dense TSV shaping;
- how and when manually assigned maximum TSV should be replaced by empirically learned combat value.

The initial first-proof reward magnitudes are fixed for the first attempt but remain tunable based on actual learning evidence.

These open decisions should be resolved from the real Bees mechanics and evidence from the smallest working experiments rather than by building speculative infrastructure in advance.
