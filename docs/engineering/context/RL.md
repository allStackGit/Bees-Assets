# RL / Training Route

Load this route only when a focused RL/training task needs contract or dependency context beyond the exact named source/config/test.

| Concern | Start with | Nearby evidence / boundary |
|---|---|---|
| unified / continual-learning training design, online player learning, replay/archive strategy | `Training/bees_continual_learning_rl_implementation.md` | verify implemented portions against current `Training/` and `RlOneVsOne*` code |
| dedicated ML-Agents combat training, RL 1v1, curriculum, env args | `RlOneVsOneTrainingBootstrap`, `RlOneVsOneTrainingOptions` | `RlOneVsOneAgent`, `RlOneVsOneEpisodeCoordinator`, `RlOneVsOneTrainingDurabilityGuard`, `Level.RandomSquadSetup`, `Training/rl_1v1_config.yaml`, focused EditMode tests |
| player-facing Campaign / Fish Tank RL controller selection | `RlLivePolicyAgent`, `Stage.ActivateBrains`, `Stage.ActivateHiveMind` | `Level.SetupHivemind`; `ActivateHiveMind` remains the AI enable/disable gate while `ActivateBrains` selects RL instead of Hive Mind; player-owned Campaign ships must remain player controlled |
| player / Hive Mind demonstration capture and human imitation | `RlGameplayDemonstrationAgent`, `RlGameplayDemonstrationCapabilityCapture`, `Training/bees_continual_train.py` | opt-in `--rl-record-demonstrations`; shared-ABI ML-Agents `.demo` files are partitioned under `Application.persistentDataPath/RlDemonstrations/PolicyV<ABI>/{Human,HiveMind}` with a fail-closed capture manifest; successful ship specials, mining, healing and warp are emitted as isolated native event-demo episodes built synchronously from the shared policy perception/current controls at the authoritative event boundary; `--continual-human-demo-dir` requires the configured Human/PolicyV directory and exact capture behavior/ABI/policy signature, preserves the capture manifest in an immutable content-addressed snapshot, and injects configurable behavioral cloning into a generated runtime YAML; live-RL-controlled ships are excluded; authenticated public upload/central native-demo ingestion is not implemented yet |
| Python trainer wrapper / batching / inference / diagnostics | `Training/bees_mlagents_learn.py` | sibling `*_tests.py`, compatibility and diagnostics modules |
| dedicated Hive Mind training vs Fish Tank | `HiveMindTrainingBootstrap` | scene `Hivemind Training`, `Stage.IsTrainingHiveMind`, random squads |
| historical ML-Agents experiment | `Brain.cs`, `Training/trainer_config.yaml` | dormant historical path; do not infer current dedicated RL or Hive Mind behavior from it |

Use exact current symbols and tests as authority. Load `PROJECT_CONSTITUTION.md`, `SYSTEM_MAP.md`, or `INVARIANTS.md` only when the RL change actually crosses a gameplay/product, lifecycle, persistence/network, or other protected boundary.
