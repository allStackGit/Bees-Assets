# RL / Training Route

Load this route only when a focused RL/training task needs contract or dependency context beyond the exact named source/config/test.

| Concern | Start with | Nearby evidence / boundary |
|---|---|---|
| unified / continual-learning training design, online player learning, replay/archive strategy | `Training/bees_continual_learning_rl_implementation.md` | verify implemented portions against current `Training/` and `RlOneVsOne*` code |
| dedicated ML-Agents combat training, RL 1v1, curriculum, env args | `RlOneVsOneTrainingBootstrap`, `RlOneVsOneTrainingOptions` | `RlOneVsOneAgent`, `RlOneVsOneEpisodeCoordinator`, `RlOneVsOneTrainingDurabilityGuard`, `Level.RandomSquadSetup`, `Training/rl_1v1_config.yaml`, focused EditMode tests |
| player-facing Campaign / Fish Tank RL controller selection | `RlLivePolicyAgent`, `Stage.ActivateBrains`, `Stage.ActivateHiveMind` | `Level.SetupHivemind`; `ActivateHiveMind` remains the AI enable/disable gate while `ActivateBrains` selects RL instead of Hive Mind; player-owned Campaign ships must remain player controlled |
| player / Hive Mind demonstration capture for centralized learning | `RlGameplayDemonstrationAgent` | opt-in `--rl-record-demonstrations`; records the shared `BeesRL1v1` observation/action ABI as ML-Agents `.demo` files under `Application.persistentDataPath/RlDemonstrations`; live-RL-controlled ships are excluded; one-shot special actions still require authoritative event hooks; central upload/ingestion is not implemented yet |
| Python trainer wrapper / batching / inference / diagnostics | `Training/bees_mlagents_learn.py` | sibling `*_tests.py`, compatibility and diagnostics modules |
| dedicated Hive Mind training vs Fish Tank | `HiveMindTrainingBootstrap` | scene `Hivemind Training`, `Stage.IsTrainingHiveMind`, random squads |
| historical ML-Agents experiment | `Brain.cs`, `Training/trainer_config.yaml` | dormant historical path; do not infer current dedicated RL or Hive Mind behavior from it |

Use exact current symbols and tests as authority. Load `PROJECT_CONSTITUTION.md`, `SYSTEM_MAP.md`, or `INVARIANTS.md` only when the RL change actually crosses a gameplay/product, lifecycle, persistence/network, or other protected boundary.
