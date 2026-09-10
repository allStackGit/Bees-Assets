# Bees Continual Learning RL System — Implementation Specification

## Purpose

This document specifies a future Bees training architecture in which a shared neural policy improves from three sources at the same time:

1. **Dedicated headless ML-Agents training**
   - Many accelerated environments.
   - Self-play between Bees and Humans.
   - Scenario and matchup sampling.
   - The primary source of high-volume reinforcement-learning experience.

2. **Live game inference and experience collection**
   - Campaign.
   - Free Play.
   - Fish Tank mode.
   - Other normal gameplay modes.
   - AI ships use the latest **approved** RL snapshot at normal game speed.
   - Their gameplay can be recorded and used to improve later models.

3. **Human demonstration and opponent learning**
   - Player observations and actions are recorded during eligible gameplay.
   - The system can learn both:
     - how to imitate useful player behavior; and
     - how to defeat strategies players actually use.

The goal is **continual cumulative learning**: new versions should learn new strategies without unnecessarily forgetting old competencies.

The system must not assume that every gradient update is an improvement. Training may regress temporarily. Production deployment therefore uses validation, promotion, rollback, historical opponents, and permanent competency tests.

---

# 1. Core Design Principles

## 1.1 One learning system, two model states

Maintain a strict distinction between:

- **Training model**
  - Continuously updated.
  - May temporarily regress.
  - May be experimental.
  - Must never be assumed safe for live deployment.

- **Production champion**
  - Latest validated snapshot.
  - Used by normal player builds and Fish Tank.
  - Updated only after passing promotion tests.
  - Must be easy to roll back.

Do **not** let individual game clients directly mutate the production model.

The intended data flow is:

```text
Headless training + live AI gameplay + player demonstrations -> central trainer -> candidate network -> evaluation -> promoted snapshot -> all games
```

Or in expanded form:

```text
Headless RL environments ───────┐
                                │
Live AI gameplay ───────────────┼──> Central experience/training system
                                │              │
Human demonstrations ──────────┘              │
                                               v
                                      Candidate policy
                                               │
                                               v
                                      Evaluation / gates
                                               │
                                  pass --------┴-------- fail
                                   │                     │
                                   v                     v
                              New champion          Keep training
                                   │
                                   v
                         Distributed to clients
```

---

## 1.2 Continual learning requires active preservation

A large neural network may have enough capacity to represent many strategies, but capacity alone does not prevent catastrophic forgetting.

The system must deliberately preserve prior competence through:

- historical policy opponents;
- recurring evaluation against old strategies;
- curated scenarios;
- human demonstration replay;
- controlled opponent sampling;
- champion/challenger validation;
- optional later use of distillation or regularization.

The intended behavior is not:

```text
learn A -> learn B -> forget A -> learn C -> forget B
```

but:

```text
learn A
learn B while periodically testing/revisiting A
learn C while periodically testing/revisiting A and B
...
```

If the current policy starts losing to an old policy that it used to beat, that old policy should become more important in training again.

---

# 2. Repository and Implementation Procedure

Before modifying any Bees repository, implementation agents must follow that repository's current instructions.

For each of the following repositories involved in an implementation:

- `allStackGit/Bees-Assets`
- `allStackGit/Ants-v2`
- `allStackGit/BeesServer`

the implementing agent must first fetch the **current root `AGENTS.md`** from that repository and follow all required-reading instructions contained in it.

Do not rely on an older remembered copy of `AGENTS.md`.

This document is an architectural specification. Exact file locations, class names, interfaces, schemas, and repository ownership must be reconciled against the current codebase before implementation.

---

# 3. System Components

The full system should eventually contain the following logical components.

## 3.1 Headless training workers

Responsibilities:

- launch many Unity environments;
- run faster than real time where possible;
- sample maps, ships, health, team sizes, and scenarios;
- collect fresh trajectories;
- run current-policy self-play;
- periodically run against historical policies;
- report training metrics;
- recover from individual environment failures.

These workers should remain the highest-throughput source of RL experience.

---

## 3.2 Central trainer

Responsibilities:

- own the mutable training network;
- receive current headless rollouts;
- consume approved live-game experience where appropriate;
- consume human demonstration batches through an imitation-learning path;
- maintain optimizer/training state;
- create candidate snapshots;
- schedule historical opponents;
- track training generation/version metadata.

There should be a single authoritative training lineage unless intentionally running experimental branches.

---

## 3.3 Historical policy league

A persistent set of previous snapshots used as opponents and regression references.

It should contain more than just the immediately previous checkpoint.

Store:

- major champions;
- strategically distinct historical policies;
- policies exposing known weaknesses;
- milestone policies from different training phases;
- optional specialist/exploiter policies later.

This league is central to reducing self-play cycling and catastrophic forgetting.

---

## 3.4 Evaluation service

Runs candidates against:

- current champion;
- historical league;
- fixed competency scenarios;
- representative ship matchups;
- representative map sizes;
- representative team sizes;
- known tactical edge cases;
- optionally recorded player-derived situations.

It decides whether a candidate is safe to promote.

Training success and production success must be separate concepts.

---

## 3.5 Snapshot registry

Every exported network must have an immutable model identifier.

At minimum store:

```text
model_id
parent_model_id
training_run_id
training_step
created_at
game_build_version
observation_schema_version
action_schema_version
reward_schema_version
training_config_hash
source_checkpoint
promotion_status
evaluation_report_id
```

Recommended statuses:

```text
training
candidate
rejected
champion
historical
retired
```

Never identify a model only as `latest.onnx`.

`latest` may be an alias, but the underlying model must have an immutable version.

---

## 3.6 Client model distributor

Normal game clients should periodically obtain the latest approved champion.

Requirements:

- clients only receive promoted models;
- model downloads are versioned;
- integrity is verified;
- clients can retain a known-good fallback;
- incompatible observation/action versions are rejected;
- rollout can be gradual if desired;
- rollback can happen without retraining.

---

## 3.7 Gameplay telemetry collector

Eligible gameplay should generate compact training records.

Possible sources:

- campaign;
- Free Play;
- Fish Tank;
- normal AI-vs-AI matches;
- human-vs-AI games;
- selected debugging/testing modes.

Telemetry must record enough metadata to reconstruct what generated the experience.

At minimum:

```text
match_id
client_id_or_anonymous_install_id
player_id_or_privacy_safe_identifier
game_build_version
model_id
mode
map/scenario configuration
ship composition
random seed where available
episode start/end time
result
observations
AI actions
human actions where applicable
rewards or reward-reconstructable events
termination reason
```

Sensitive personal information should not be required for training.

---

# 4. Three Learning Pipelines

## 4.1 Pipeline A — headless on-policy RL

This remains the core PPO path.

```text
current training policy
    -> headless Unity environments
    -> fresh observations/actions/rewards
    -> PPO update
    -> new current training policy
```

Important rule:

> PPO updates should primarily use trajectories generated by the current or sufficiently recent policy.

Do not create an enormous archive of old PPO trajectories and repeatedly treat them as fresh on-policy data.

Old trajectories become increasingly off-policy as the current model changes.

The better way to reuse old knowledge is to preserve old **policies** and generate **new rollouts** against them.

---

## 4.2 Pipeline B — live AI experience

When a player runs Campaign, Free Play, or Fish Tank:

- deployed AI uses the approved champion;
- match telemetry may be uploaded;
- the central system decides whether and how it can be used.

Possible uses:

1. scenario discovery;
2. opponent-strategy mining;
3. evaluation data;
4. supervised auxiliary training;
5. replay/environment reconstruction;
6. limited off-policy RL later if a suitable algorithm is introduced.

Do not blindly insert arbitrarily old live trajectories into normal PPO minibatches.

A preferred early implementation is:

```text
live match
  -> telemetry
  -> detect interesting situations/strategies
  -> reproduce or approximate those situations in headless environments
  -> collect fresh on-policy rollouts
  -> PPO
```

This preserves PPO's intended training assumptions.

---

## 4.3 Pipeline C — human demonstrations

Human play creates paired examples:

```text
observation -> human action
```

These can be used for:

- behavioral cloning;
- imitation auxiliary loss;
- optional GAIL-style learning;
- behavior distillation;
- player-style classification later.

Human demonstration replay can remain useful much longer than stale PPO rollout data because it is used as supervised/imitative evidence rather than pretending to be current on-policy RL experience.

The demonstration buffer should be persistent but curated.

---

# 5. Learning to Copy Players vs Learning to Beat Players

These are separate objectives and must remain separate in the architecture.

## 5.1 Copying players

Objective:

> Given a game state, reproduce useful human decisions.

Examples:

- kiting;
- firing at long range;
- turning before firing;
- retreating;
- focus fire;
- flanking;
- use of particular ship mechanics.

Use:

- behavioral cloning;
- weighted demonstration replay;
- optional imitation loss mixed with RL.

---

## 5.2 Beating players

Objective:

> Learn policies that counter strategies humans actually use.

Preferred approach:

1. identify strategic patterns from live matches;
2. preserve representative episodes or scenario descriptors;
3. generate opponents/scenarios based on them;
4. train the current policy against them;
5. evaluate whether the counter generalizes.

A player's trajectory can also be replayed as a scripted opponent where technically feasible.

Do not assume that copying a player's strategy automatically teaches the best counterstrategy.

---

# 6. Historical Policy League

## 6.1 Why the league exists

Plain latest-vs-latest self-play can cycle.

Example:

```text
Policy A beats common strategy X.
Policy B evolves to beat A.
Policy C evolves to beat B.
Policy C becomes weak to X again.
```

If A or another old policy exploiting X remains in the league, C's regression can be detected and trained against.

---

## 6.2 What to retain

Do not necessarily preserve every checkpoint forever.

Retain:

### Always retain

- every production champion;
- manually tagged important models;
- best-known model for major historical training phases;
- models that expose unique weaknesses in later policies.

### Consider retaining

- periodic milestone models;
- high-ELO models;
- models with unusual matchup matrices;
- strategically diverse models.

### Usually discard

- redundant checkpoints almost identical to neighbors;
- clearly broken/corrupted models;
- snapshots that add no useful behavioral diversity.

A 90 MB combined checkpoint is small enough that retaining hundreds or even thousands may be practical, but storage should still be managed intentionally.

---

## 6.3 League metadata

Each historical member should track:

```text
model_id
rating
creation_step
champion_generation
match_count
recent_win_rate_vs_current
strategic_tags
reason_retained
last_sampled_at
```

Optional later metadata:

```text
behavior_embedding
novelty_score
ship-specific ratings
map-specific ratings
```

---

# 7. Opponent Sampling

Uniformly choosing historical opponents is simple but inefficient.

The training scheduler should eventually combine several buckets.

Example starting distribution:

```text
50% current/recent self-play
20% historical league
15% opponents currently exposing weaknesses
10% diverse/random historical opponents
 5% special regression/edge-case opponents
```

These exact percentages are initial tuning values, not requirements.

The scheduler should be configurable.

---

## 7.1 Prioritize forgotten skills

If evaluation shows:

```text
current model used to beat Historical-137 at 75%
current model now beats Historical-137 at only 35%
```

increase Historical-137's training probability.

This creates a continual-learning feedback mechanism:

```text
forgetting detected
    -> forgotten opponent sampled more
    -> fresh experience generated
    -> competence restored
```

---

## 7.2 Avoid overfitting to one historical enemy

Sampling must remain diverse.

Do not respond to every regression by training almost exclusively against one opponent, because that can create another cycle.

Use:

- minimum diversity quotas;
- capped opponent weights;
- decaying weakness bonuses;
- broad evaluation matrices.

---

# 8. Permanent Competency Suite

The system needs tests that do not move with the training population.

Create a permanent set of scenarios representing things the AI should continue to know.

Categories should include:

- every playable Bee ship;
- every playable Human ship;
- weaponless ships;
- charge/self-destruct ships;
- carriers/factories/spawners;
- long-range vs short-range combat;
- high/low health;
- different map sizes;
- 1v1;
- multi-ship battles;
- asymmetric team values;
- obstacle-rich situations if applicable;
- retreat/kiting situations;
- aiming and first-fire scenarios;
- known historic tactical failures.

For every candidate, retain per-scenario results.

Never rely only on a single aggregate win rate.

---

# 9. Candidate Evaluation and Promotion

## 9.1 Candidate creation

At configured intervals:

```text
current training state
    -> freeze/export immutable candidate
    -> queue evaluation
```

Training may continue while evaluation runs, provided candidate identity is immutable.

---

## 9.2 Promotion criteria

A candidate should normally need to satisfy all of the following:

1. **Champion comparison**
   - statistically credible performance against current champion.

2. **Historical league**
   - no unacceptable broad regression.

3. **Permanent competency suite**
   - no critical capability drops.

4. **Behavior sanity checks**
   - no obvious pathological behavior.

5. **Compatibility**
   - correct observation/action schema.

6. **Runtime checks**
   - inference performance acceptable.

Do not require absolute monotonic improvement on every individual matchup. That may be impossible.

Instead define:

- critical no-regression thresholds;
- aggregate improvement requirements;
- tolerated small regressions;
- hard blockers.

---

## 9.3 Example promotion policy

Illustrative only:

```text
Candidate must:
- beat current champion with >52% estimated win rate,
- have no critical scenario below its hard minimum,
- have no major historical matchup regression >15 percentage points,
- maintain acceptable aggregate historical-league performance,
- pass inference/runtime validation.
```

Tune using empirical variance.

---

## 9.4 Rejected candidates

A rejected candidate is not necessarily discarded.

Store:

- model;
- evaluation report;
- reason for rejection.

Its weaknesses may be useful for future training or debugging.

---

# 10. Champion Rollout

When a candidate passes:

1. mark previous champion historical;
2. mark candidate as champion;
3. publish immutable model package;
4. update server's approved-model pointer;
5. clients fetch it according to rollout rules;
6. monitor live metrics;
7. rollback automatically or manually if production problems occur.

The trainer may continue from its newer internal state. Promotion does not require freezing training globally.

---

# 11. Human Demonstration Buffer

## 11.1 Store demonstrations separately from PPO replay

Recommended logical stores:

```text
ppo_current_rollouts
human_demonstrations
live_match_archive
historical_models
evaluation_results
```

Do not collapse these into one undifferentiated replay buffer.

---

## 11.2 Demonstration quality

Not all players are good teachers.

Potential quality signals:

- player success/win rate;
- damage efficiency;
- survival;
- objective completion;
- tactical novelty;
- performance relative to expected ship matchup;
- repeated consistency;
- whether an action led to positive downstream outcomes.

Never make rank alone the sole quality measure.

Interesting losing strategies may still be useful as opponents.

---

## 11.3 Weighting

Demonstration sampling can combine:

```text
competence
novelty
recency
scenario rarity
ship coverage
player diversity
```

Cap any single player's contribution so one user cannot dominate the learned behavior.

---

## 11.4 Retention

A long demonstration history is desirable.

However:

- deduplicate near-identical states;
- downsample extremely common trivial situations;
- retain rare/strategic situations;
- preserve good examples across old game versions only if compatible.

---

# 12. Replay Strategy

The phrase "replay buffer" must refer to several distinct mechanisms.

## 12.1 PPO rollout buffer

Short lived.

Contains sufficiently current on-policy experience.

Purpose:

- normal PPO optimization.

---

## 12.2 Demonstration replay

Long lived.

Contains human state/action examples.

Purpose:

- behavioral cloning;
- imitation auxiliary training.

---

## 12.3 Historical opponent replay

Long lived, but stores models rather than trajectories.

Purpose:

- generate fresh experience against old strategies.

This is the preferred mechanism for protecting PPO competence.

---

## 12.4 Scenario replay

Long lived.

Contains:

- seeds;
- setup parameters;
- map configurations;
- ship compositions;
- important reconstructed states where feasible.

Purpose:

- regenerate difficult or important situations with the current policy.

---

# 13. Preventing Catastrophic Forgetting

Use multiple defenses.

## Layer 1 — historical opponents

The strongest initial defense.

The current model repeatedly encounters old strategies.

---

## Layer 2 — permanent competency testing

Detect regressions early.

---

## Layer 3 — targeted resampling

If a skill degrades, increase relevant scenarios/opponents.

---

## Layer 4 — demonstration replay

Preserve strong human behaviors.

---

## Layer 5 — balanced scenario distribution

Do not allow one popular ship/map/mode to consume nearly all training.

---

## Layer 6 — distillation or policy preservation

Optional later addition.

When creating a new policy, add a loss encouraging it to preserve important outputs from a previous champion on representative old observations.

Conceptually:

```text
total_loss =
    PPO_loss
  + imitation_loss
  + preservation_loss
```

Weights must be tuned carefully.

Too much preservation prevents learning new strategies.

Too little permits forgetting.

Do not implement this before the simpler league/evaluation system is working unless testing demonstrates a need.

---

# 14. Network Capacity

A larger model may reduce interference by giving the policy more representational capacity, but do not treat parameter count as the primary solution.

Before increasing network size, measure:

- whether training loss suggests capacity limits;
- whether old competencies disappear despite replay;
- whether larger models improve the historical matchup matrix;
- inference cost in normal player games;
- training throughput.

If one monolithic policy eventually proves inadequate, future architectures may include:

- recurrent policies;
- explicit strategy/context conditioning;
- ship-specific feature modules;
- shared trunk + specialist heads;
- mixture-of-experts.

These are future options, not initial requirements.

---

# 15. Live Gameplay at 1x Physics

Live games should use inference only.

The user's local game simulation must not pause for training.

For every AI decision:

```text
game state
    -> local production model inference
    -> action
```

Experience collection should be asynchronous to gameplay where possible.

The client may buffer telemetry and upload in chunks.

Do not require a round trip to the training server for every decision.

---

# 16. Offline / Disconnected Play

Normal gameplay should remain possible when the training/server infrastructure is unavailable.

The client should:

- retain its last valid champion;
- continue local inference;
- buffer a bounded amount of training telemetry;
- retry upload later;
- discard oldest low-value telemetry if the buffer is full.

A trainer/server outage must not make normal gameplay unplayable.

---

# 17. Version Compatibility

Every experience record must include:

```text
game_build_version
model_id
observation_schema_version
action_schema_version
reward_schema_version
scenario_schema_version
```

The trainer must reject or transform incompatible data.

Do not silently mix trajectories produced under materially different observation or action meanings.

---

## 17.1 Schema changes

When observations/actions change:

1. increment schema version;
2. prevent incompatible old models from loading;
3. decide whether old demonstrations can be migrated;
4. retain old data for analysis even if it cannot train the current model;
5. establish a new baseline/champion generation if necessary.

---

# 18. Data Validation

Incoming player telemetry is untrusted.

Validate:

- valid game version;
- valid model ID;
- legal ship configuration;
- legal actions;
- plausible timestamps;
- plausible physics values;
- episode consistency;
- checksums/signatures where appropriate;
- duplicate submissions;
- malformed payloads;
- impossible reward/damage values.

Never allow arbitrary client-provided "reward" values to become trusted training labels without server validation.

---

# 19. Anti-Poisoning and Abuse

Once public players influence training, assume some users may intentionally attempt to distort it.

Defenses:

- server-side validation;
- per-user contribution caps;
- per-install contribution caps;
- anomaly detection;
- duplicate detection;
- minimum diversity requirements;
- delayed incorporation of untrusted data;
- quarantining suspicious batches;
- aggregate rather than trusting individual claims;
- signed/identified official builds where appropriate.

For imitation learning, high-volume low-quality behavior should not overwhelm useful examples.

---

# 20. Privacy

Collect only data needed for training and diagnostics.

Prefer:

- pseudonymous IDs;
- game-state/action telemetry;
- model and build identifiers.

Avoid unnecessary personally identifying information.

Provide any required player-facing disclosures/consent according to the product's eventual privacy requirements.

Training data retention should be configurable.

---

# 21. Match and Trajectory Format

A conceptual episode record:

```json
{
  "match_id": "...",
  "game_build": "...",
  "mode": "campaign",
  "model_id": "...",
  "observation_schema": 4,
  "action_schema": 3,
  "reward_schema": 7,
  "scenario": {
    "map_size": 128,
    "health_ratio": 1.0,
    "bee_ships": ["Wasp"],
    "human_ships": ["Gunship"]
  },
  "participants": [...],
  "steps": [...],
  "outcome": {...},
  "integrity": {...}
}
```

Actual serialization should be compact and likely binary/compressed for large-scale use.

Do not make JSON-per-frame the production storage format unless measurements show it is acceptable.

---

# 22. Experience Ingestion Pipeline

Recommended flow:

```text
Client
  -> local batch
  -> authenticated upload
  -> ingestion endpoint
  -> validation
  -> deduplication
  -> metadata extraction
  -> raw archive
  -> feature/trajectory processing
  -> training queues
```

Separate raw archival from processed training datasets.

This allows the processing logic to improve later without losing original data.

---

# 23. Scenario Mining

One of the best uses of live player data may be identifying situations the headless trainer rarely generates.

Examples:

- unusual long-range standoffs;
- retreat patterns;
- focus-fire situations;
- unexpected ramming strategies;
- clever carrier usage;
- map-edge exploitation;
- pathological AI behavior.

The system should eventually be able to promote such scenarios into:

- training scenario pools;
- competency tests;
- historical regression cases.

---

# 24. Ratings and Matchup Matrix

A single ELO number is insufficient.

Maintain a matrix such as:

```text
current model vs:
    current champion
    historical champion 1
    historical champion 2
    ...
```

Also segment where practical by:

- Bee ship type;
- Human ship type;
- map-size band;
- ships-per-side;
- scenario category.

This makes forgetting visible.

Example:

```text
Overall stronger: yes
Wasp vs Gunship: stronger
Hornet vs Frigate: unchanged
Yellow Jacket competence: severe regression
Large-map aiming: improved
Small-map collision behavior: worse
```

That is much more useful than one aggregate rating.

---

# 25. Diversity Metrics

Track whether training is collapsing into a narrow behavior.

Useful metrics may include:

- first-fire distance;
- first-hit distance;
- aiming accuracy;
- shots fired;
- damage by source;
- movement distance;
- average opponent distance;
- ramming frequency;
- edge/corner occupancy;
- idle time;
- retreat/chase transitions;
- survival time;
- ship ability usage;
- child/spawn behavior.

These should build on the compact episode-level logging already being developed for Bees.

Use metrics for diagnosis rather than directly assuming every metric has an ideal value.

---

# 26. Detecting Strategy Collapse

Create alerts/evaluation failures for pathological behaviors such as:

- all ships move to one corner;
- continuous firing away from the target;
- no firing despite having a weapon;
- no movement;
- endless circling;
- collision/ramming becoming universal regardless of matchup;
- exploiting a simulation bug;
- timeout rate spikes.

A candidate showing strong reward but obvious exploitative pathology should not be promoted automatically.

---

# 27. Reward Integrity

Continual learning amplifies reward-design mistakes.

If the reward function permits a cheap exploit, large-scale self-play and live-data training may discover and reinforce it.

Therefore:

- keep reward definitions versioned;
- test reward changes independently;
- log reward components;
- include exploit/pathology tests;
- avoid promoting candidates solely on reward.

Actual game success and competency evaluation should remain primary.

---

# 28. Training Scheduler

The scheduler decides what the trainer sees next.

Inputs:

```text
recent performance
historical regressions
ship coverage
map coverage
scenario coverage
live-player discoveries
demonstration availability
training throughput
```

Output:

```text
next scenario distribution
next opponent distribution
imitation/RL mix
```

Begin with simple configurable probabilities.

Only add sophisticated adaptive scheduling after baseline behavior is measurable.

---

# 29. Proposed Initial Training Mix

For the first continual-learning version, keep it simple.

Example:

```text
Headless environment distribution:
    60% current-policy/recent-policy self-play
    20% historical-policy opponents
    10% known weak matchups/scenarios
    10% broad random coverage

Optimization:
    primary: PPO
    auxiliary: small human behavioral-cloning loss
```

These numbers should be configuration values, not hard-coded constants.

---

# 30. Human Imitation Loss

Human imitation should initially be a controlled auxiliary objective.

Conceptually:

```text
L_total = L_PPO + lambda_human * L_BC
```

Start with a relatively low `lambda_human`.

Why:

- players are not always optimal;
- the AI should be able to surpass humans;
- imitation should introduce useful tactics, not force permanent imitation.

Track whether increasing imitation weight improves actual evaluation.

---

# 31. Separate Training Datasets by Purpose

Recommended categories:

```text
expert-ish human demonstrations
general human demonstrations
human opponent behaviors
AI-vs-AI live episodes
pathological/failure episodes
evaluation-only episodes
```

Do not train equally on all categories.

Failure episodes can be extremely useful for testing without being useful for imitation.

---

# 32. Player Strategy Profiles

Optional future feature.

Instead of treating every player trajectory independently, derive strategy clusters such as:

```text
aggressive rammer
long-range kiter
defensive retreat
carrier swarm
focus-fire
map-edge play
```

Training could then sample strategy classes rather than individual users.

Benefits:

- less privacy-sensitive;
- better generalization;
- prevents one high-volume player dominating;
- easier to ensure strategic diversity.

Not needed for the first implementation.

---

# 33. Specialist / Exploiter Agents

Optional later feature inspired by league-style self-play.

Maintain secondary agents whose job is to find weaknesses in the champion.

Examples:

- Wasp-vs-Gunship exploiter;
- long-range combat exploiter;
- ramming exploiter;
- large-map exploiter.

If a specialist consistently defeats the main policy, its discovered behavior becomes training pressure for the main policy.

This adds complexity and should come after historical league training works.

---

# 34. Model Preservation / Distillation

Optional later defense against forgetting.

Store a representative observation set from old champions.

During later training, occasionally compare:

```text
old champion action distribution
vs
new policy action distribution
```

Penalize excessive divergence when the old behavior remains desirable.

Do not preserve every old output blindly.

If the old champion made a bad decision, the new policy should be free to improve it.

Possible preservation targets:

- actions on known-good historical states;
- latent representations;
- value estimates;
- tactical classifications.

---

# 35. Model Architecture Evolution

Do not change model architecture casually once player telemetry depends on it.

Every architecture revision should record:

```text
architecture_version
observation_schema
action_schema
compatible_predecessors
migration_method
```

When increasing network size, investigate whether weights can be transferred safely.

Otherwise treat the new architecture as a new training generation and evaluate accordingly.

---

# 36. Production Inference Performance

Live normal-speed gameplay has different constraints from headless training.

Measure:

- inference latency;
- CPU usage;
- GPU usage;
- allocation/GC impact;
- frame-time impact;
- performance with many ships;
- ONNX model size;
- batch-inference behavior where applicable.

A larger network that improves training results but causes player-visible frame drops is not automatically acceptable.

---

# 37. Central Synchronization

"All learning is synchronized" should mean:

- one authoritative model lineage;
- all accepted experience eventually contributes to the centralized process;
- promoted snapshots are globally versioned;
- clients know exactly which snapshot they are using.

It should **not** mean:

- every client immediately performs local gradient updates;
- every player receives every intermediate training step;
- training pauses until all clients synchronize.

Use asynchronous data collection with centralized model promotion.

---

# 38. Concurrency

At any moment there may be:

- many headless workers;
- many players using champion N;
- some players still using champion N-1;
- candidate N+1 under evaluation;
- trainer already producing N+2 internally.

This is acceptable.

Every record must carry `model_id`.

The system should tolerate delayed uploads from older champions.

---

# 39. Freshness Rules

Define explicit age rules for each data type.

Example:

```text
PPO rollouts:
    very short-lived

live AI trajectories:
    archive indefinitely if useful,
    but do not automatically use as on-policy PPO

human demonstrations:
    long-lived if schema-compatible

historical models:
    long-lived

evaluation cases:
    long-lived

scenario seeds/configurations:
    long-lived
```

---

# 40. Handling Game Updates

A gameplay update may invalidate learned behavior.

Classify changes:

### Cosmetic/non-semantic
Likely safe.

### Balance changes
Old demonstrations may still help but require caution.

### Observation/action changes
Require schema migration or reset.

### Physics/mechanics changes
Old trajectories may become invalid.

### Major game redesign
May require a new model generation.

The snapshot registry must record compatibility.

---

# 41. Training Regressions

Regression is expected.

When detected:

1. determine scope;
2. identify historical opponent/scenario exposing it;
3. increase targeted sampling;
4. continue training;
5. do not promote until recovered;
6. retain the regressed candidate for analysis if useful.

Do not automatically roll back the **training** model every time it regresses.

That can prevent exploration.

Rollback primarily applies to the **production champion**.

---

# 42. Plateau Handling

If learning plateaus:

Check in roughly this order:

1. environment throughput;
2. reward signal;
3. scenario/opponent diversity;
4. historical league pressure;
5. hyperparameters;
6. observation quality;
7. action representation;
8. network capacity;
9. algorithm limitations.

Do not immediately assume a larger neural network is required.

---

# 43. Success Metrics

Track more than training reward.

Primary metrics:

```text
candidate vs champion win rate
historical league score
competency-suite score
ship/map coverage
critical regression count
```

Secondary metrics:

```text
ELO/rating
episode reward
episode length
timeout rate
aiming metrics
damage efficiency
first-fire distance
first-hit distance
behavior-diversity metrics
```

Operational metrics:

```text
episodes/second
steps/second
trainer utilization
environment failure rate
telemetry upload rate
ingestion failures
model-download failures
```

---

# 44. Evaluation Statistical Confidence

Do not promote from tiny sample sizes.

Evaluation runner should continue until:

- minimum match count is reached; and
- uncertainty is sufficiently narrow;

or until:

- candidate clearly passes/fails.

Use deterministic seeds for some regression cases and randomized seeds for general strength testing.

---

# 45. Fixed vs Random Evaluation

Use both.

## Fixed

Advantages:

- exact regression comparison;
- reproducibility.

## Random

Advantages:

- generalization;
- prevents memorizing benchmark cases.

A candidate must perform acceptably on both.

---

# 46. Shadow Evaluation on Live Games

Optional later feature.

A newer candidate may observe live states without controlling ships.

For each state:

```text
champion chooses actual action
candidate independently chooses shadow action
```

Record differences.

This allows production-distribution evaluation before deployment.

Do not allow shadow inference to harm game performance.

---

# 47. Rollback

Always retain at least:

```text
current champion
previous champion
several known-good historical champions
```

Server should be able to change the approved model pointer quickly.

Clients should verify the requested model and fall back if download/load fails.

---

# 48. Failure Recovery

## Headless worker crashes

- restart failed environment;
- do not kill entire run;
- record crash rate;
- quarantine repeatedly failing scenarios.

## Trainer crash

- restore latest training checkpoint;
- preserve optimizer state where possible.

## Evaluation crash

- retry independently;
- candidate remains unpromoted.

## Server outage

- trainer can continue where dependencies permit;
- clients continue last valid champion;
- telemetry buffers locally within limits.

## Corrupt model

- checksum failure prevents promotion/download;
- revert to known-good champion.

---

# 49. Storage Layout

Conceptual server-side organization:

```text
/models/
    /champions/
    /candidates/
    /historical/

/training/
    /checkpoints/
    /configs/
    /optimizer-state/

/experience/
    /raw-live/
    /human-demos/
    /processed/

/evaluation/
    /reports/
    /fixed-scenarios/
    /results/

/metadata/
    model-registry
    schema-registry
    training-runs
```

Actual storage technology may be filesystem, database, object storage, or hybrid.

---

# 50. Checkpoint Retention

Given current model/checkpoint sizes, storage is unlikely to be the limiting resource initially.

Use tiered retention:

- frequent recent checkpoints;
- less frequent old checkpoints;
- permanent champions;
- permanent important historical strategies.

Do not keep terabytes merely because checkpoints are available.

Behavioral importance matters more than raw checkpoint count.

---

# 51. Configuration

All important continual-learning settings should be configurable.

Example:

```yaml
continual_learning:
  historical_league:
    enabled: true
    target_fraction: 0.20
    max_members: 1000

  regression_sampling:
    enabled: true
    target_fraction: 0.10

  human_imitation:
    enabled: true
    loss_weight: 0.05

  candidate:
    checkpoint_interval_steps: 250000

  promotion:
    min_matches_vs_champion: 500
    max_critical_regressions: 0
```

Exact field names must fit the existing Bees configuration architecture.

---

# 52. Observability Dashboard

Eventually provide a dashboard showing:

- current training model;
- current production champion;
- last promotion;
- candidate evaluation status;
- current steps/sec;
- headless worker health;
- historical league size;
- champion-vs-history performance;
- critical regressions;
- player experience ingestion volume;
- demonstration buffer size;
- per-ship performance.

The system should make regressions visible before players notice them.

---

# 53. Recommended Implementation Phases

Do not build the entire final architecture in one change.

## Phase 1 — model identity and promotion foundation

Implement:

- immutable model IDs;
- snapshot registry;
- candidate vs champion distinction;
- manual promotion;
- rollback;
- model metadata.

Acceptance criteria:

- game can load a specific immutable model;
- server can identify current champion;
- previous champion remains available;
- no player client receives an unapproved candidate.

---

## Phase 2 — permanent evaluation suite

Implement:

- fixed scenarios;
- automated candidate evaluation;
- per-matchup results;
- regression report;
- promotion gate.

Acceptance criteria:

- candidate cannot be promoted without a report;
- historical matchup regressions are visible;
- critical failures block promotion.

---

## Phase 3 — historical policy league

Implement:

- retain champion history;
- load historical opponent models;
- sample historical opponents during headless training;
- track current-vs-history performance.

Acceptance criteria:

- current model can train against arbitrary compatible old champion;
- sampling probabilities are configurable;
- old-policy rollouts are generated fresh.

---

## Phase 4 — adaptive anti-forgetting

Implement:

- detect meaningful regressions;
- boost sampling probability for exposing opponents/scenarios;
- cap boosts;
- maintain diversity.

Acceptance criteria:

- deliberate test regression produces increased resampling;
- sampling normalizes after competence recovers.

---

## Phase 5 — live game telemetry

Implement:

- episode recording;
- batching/compression;
- upload;
- validation;
- raw archival;
- schema/version metadata.

Acceptance criteria:

- normal gameplay remains unaffected when upload fails;
- telemetry can be traced to exact model/build;
- invalid data is rejected.

---

## Phase 6 — player action demonstrations

Implement:

- record human observations/actions;
- demonstration dataset;
- quality filters;
- behavioral cloning training path;
- configurable BC weight.

Acceptance criteria:

- trainer can improve supervised action prediction on held-out demonstrations;
- disabling imitation reproduces normal PPO behavior;
- one player cannot dominate the buffer.

---

## Phase 7 — player-derived adversarial training

Implement:

- identify useful live strategies/scenarios;
- create headless equivalents or scripted replay;
- sample them in RL;
- measure resulting counter-strategy performance.

Acceptance criteria:

- a repeated player tactic can be converted into measurable training pressure;
- improved counterplay is visible in evaluation.

---

## Phase 8 — automatic champion deployment

Implement:

- automatic candidate queue;
- evaluation;
- promotion;
- client distribution;
- monitoring;
- rollback protection.

Acceptance criteria:

- only passing candidates reach clients;
- rollback can restore prior champion;
- model compatibility failures are handled safely.

---

## Phase 9 — advanced continual-learning techniques

Only after measurements justify them:

- policy distillation;
- preservation regularization;
- strategy clustering;
- specialist/exploiter agents;
- behavioral embeddings;
- mixture-of-experts;
- offline RL algorithms;
- population-based training.

---

# 54. Testing Strategy

## Unit tests

Cover:

- model metadata validation;
- schema compatibility;
- opponent sampler;
- regression weighting;
- promotion rules;
- telemetry validation;
- deduplication;
- demonstration weighting.

---

## Integration tests

Cover:

```text
train -> candidate -> evaluate -> promote -> client load
```

and:

```text
client match -> upload -> validate -> archive -> training pipeline
```

and:

```text
historical model -> opponent selection -> fresh rollout -> PPO
```

---

## Regression tests

Create deliberate tests for:

- candidate loses old competence;
- candidate exploits one benchmark;
- malformed telemetry;
- duplicate telemetry;
- stale client uploads;
- model/schema mismatch;
- server unavailable;
- corrupt ONNX;
- historical model unavailable.

---

## Long-run tests

Before enabling automatic continual deployment, run sustained tests measuring:

- whether performance oscillates;
- whether historical competence decays;
- whether adaptive replay recovers it;
- whether player demonstrations destabilize PPO;
- whether model promotion rate becomes excessive;
- whether training becomes bottlenecked by evaluation.

---

# 55. Required Invariants

The implementation should enforce these invariants.

1. **Production clients never train the shared network directly.**
2. **Every inference model has an immutable ID.**
3. **Every recorded trajectory identifies the model that generated it.**
4. **Old PPO trajectories are not blindly reused as current on-policy PPO experience.**
5. **Historical policies can be replayed as opponents to generate fresh experience.**
6. **Human demonstrations are stored separately from PPO rollouts.**
7. **A training checkpoint does not become production champion without evaluation.**
8. **Previous champions remain recoverable.**
9. **Schema-incompatible experience is never silently mixed.**
10. **Untrusted player data is validated before training use.**
11. **The training system can regress without immediately degrading production AI.**
12. **Historical competence is explicitly measured.**

---

# 56. First Practical Version

The smallest version that captures most of the value would be:

```text
1. Continue existing headless PPO/self-play.
2. Keep every important champion snapshot.
3. Train ~20% of headless matches against historical champions.
4. Maintain permanent evaluation scenarios.
5. Evaluate candidates against champion + historical league.
6. Promote only validated candidates.
7. Record live player trajectories.
8. Store human observation/action pairs.
9. Add a small behavioral-cloning auxiliary loss from good player examples.
10. Convert important player-discovered tactics into fresh headless scenarios.
```

This should be attempted before advanced continual-learning algorithms.

---

# 57. Expected Long-Term Behavior

The desired steady-state loop is:

```text
Players and headless workers produce experience
                    |
                    v
              Central trainer
                    |
                    v
             Candidate policy
                    |
        +-----------+------------+
        |                        |
        v                        v
Current champion           Historical league
        |                        |
        +-----------+------------+
                    |
                    v
               Evaluation
                    |
            passes promotion
                    |
                    v
              New champion
                    |
                    v
       Players receive new model
                    |
                    v
        New strategies emerge
                    |
                    +----> back into training
```

Over time the policy should ideally accumulate broader competence:

```text
basic combat
+ ranged tactics
+ ramming counters
+ ship-specific mechanics
+ multiplayer coordination
+ tactics discovered by real players
+ counters to real-player strategies
+ preserved historical competencies
```

There is no guarantee of perfectly monotonic improvement.

The system is designed so that:

- regressions are detected;
- production is protected from most regressions;
- forgotten strategies return as training pressure;
- new human strategies continuously expand the training distribution.

---

# 58. What Not to Do

Avoid these designs:

### Direct client-side global training

```text
player's local model -> gradient update -> shared production model
```

Problems:

- synchronization;
- poisoning;
- nondeterminism;
- security;
- regression;
- version conflicts.

---

### Deploying every checkpoint

Training checkpoints are not production releases.

---

### Infinite stale PPO replay

A massive trajectory archive does not make PPO a continual-learning algorithm.

Use historical policies to regenerate current experience instead.

---

### Latest-vs-latest self-play only

This encourages cycling and forgetting.

---

### Treating every human action as expert behavior

Players can be inexperienced, experimenting, AFK, or intentionally adversarial.

---

### One aggregate score

It can conceal severe regressions in particular ships or situations.

---

# 59. Open Questions to Resolve During Implementation

These should be answered empirically rather than guessed.

1. What fraction of headless training should use historical opponents?
2. How many historical models are needed before returns diminish?
3. Which human demonstrations are useful enough to imitate?
4. What imitation-loss weight improves performance without constraining exploration?
5. Which live trajectories should become reconstructed scenarios?
6. How much evaluation is required for statistically useful promotion decisions?
7. How much regression should be tolerated for a large improvement elsewhere?
8. Which capabilities are hard promotion blockers?
9. When does network capacity become a real limitation?
10. At what scale does storage or telemetry bandwidth become material?
11. Is PPO still sufficient once live/offline data becomes a major portion of total experience?
12. Would specialist agents materially improve weakness discovery?

Record these decisions and their experimental evidence as the system evolves.

---

# 60. Final Recommended Architecture

The recommended architecture is:

```text
                  ┌──────────────────────────┐
                  │ Headless ML-Agents farms │
                  │ accelerated / many envs  │
                  └─────────────┬────────────┘
                                │ fresh PPO
                                v
┌───────────────┐      ┌──────────────────────┐
│ Player games  │----->│ Experience ingestion │
│ Campaign      │      └──────────┬───────────┘
│ Free Play     │                 │
│ Fish Tank     │                 ├── human demos
└──────┬────────┘                 ├── scenarios
       │                          └── live analytics
       │                                  │
       │                                  v
       │                         ┌──────────────────┐
       │                         │ Central trainer  │
       │                         └────────┬─────────┘
       │                                  │
       │                                  v
       │                         ┌──────────────────┐
       │                         │ Candidate model  │
       │                         └────────┬─────────┘
       │                                  │
       │                    ┌─────────────┴─────────────┐
       │                    v                           v
       │             Historical league          Competency suite
       │                    │                           │
       │                    └─────────────┬─────────────┘
       │                                  v
       │                         ┌──────────────────┐
       │                         │ Evaluation gate  │
       │                         └────────┬─────────┘
       │                                  │ pass
       │                                  v
       │                         ┌──────────────────┐
       └-------------------------│ Champion snapshot│
              inference          └──────────────────┘
```

The most important rule is:

> **Learning may be continuous. Deployment must remain validated and versioned.**

The most important anti-forgetting mechanism is:

> **Preserve old policies and important scenarios, then force the current policy to demonstrate that it still understands them.**

The most important distinction for player learning is:

> **Use player demonstrations to teach useful behavior; use player-derived strategies and scenarios to teach the AI how to defeat those behaviors.**

If implemented in stages, this architecture can support a Bees AI that learns continuously from high-speed simulation, its own deployed gameplay, and the collective strategies of real players without making every transient training regression immediately visible in production.
