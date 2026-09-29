# RL / Training Route

Use this file only to locate the current RL owners. Source and configuration are authoritative; this document intentionally does not duplicate large policy signatures, trainer settings, or historical design notes.

## Current policy contract

- Behavior: `BeesRL1v1`
- Policy ABI: v21
- Observations: 7,614 vector values
- Continuous actions: 16
- Discrete branches: `2,2,2,2,2,5`
- Weapon slots: 5
- Healing is weapon-exclusive for that decision.

The executable ABI is owned by `Scripts/Scenes/RlPolicySchema.cs` and `Scripts/Scenes/RlOneVsOneAgent.cs`. Python/server compatibility copies must match that contract exactly.

## Owners

| Concern | Start here |
|---|---|
| dedicated Unity training lifecycle and environment arguments | `RlOneVsOneTrainingBootstrap.cs`, `RlOneVsOneTrainingOptions.cs` |
| observations and actions | `RlCombatPerception.cs`, `RlOneVsOneAgent.cs`, `RlPolicySchema.cs` |
| rewards and episode completion | `RlOneVsOneEpisodeCoordinator.cs`, `RlOneVsOneReward.cs` |
| trainer hyperparameters | `Training/rl_1v1_config.yaml` |
| structured actor/encoder implementation | `Training/bees_mlagents_structured_policy.py` |
| ML-Agents compatibility patches and launcher | `Training/bees_mlagents_ppo_compat.py`, `Training/bees_mlagents_learn.py` |
| continual registry, evaluation, release and promotion | `Training/bees_continual_learning.py`, `Training/bees_continual_service.py`, `Training/continual_learning_config.json` |
| production neural control | `RlProductionControllerRouter.cs`, `RlLivePolicyAgent.cs`, `RlLivePolicyModelBootstrap.cs`, `RlLivePolicyModelUpdater.cs` |
| demonstrations / live telemetry | `RlGameplayDemonstrationAgent.cs`, `RlDemonstrationUploader.cs`, `RlLiveTelemetryRecorder.cs`, corresponding `Training/bees_continual_*` ingestion tools |
| distributed trainers and release control | `Training/bees_training_worker_agent.py`, `Training/operator/`, `BeesServer~/trainingControl.js` |
| operator commands | `bees.ps1`, `Training/bees_operator.js` |

## Boundaries

Dedicated neural training, production neural inference, and Hive Mind training are separate paths. Do not infer one from another.

Normal PPO experience must be current/fresh rollout data. Human demonstrations and live gameplay archives are separate continual-learning inputs; they are not ordinary stale PPO replay.

For cross-boundary changes involving BeesServer, persistence, model distribution, or authenticated uploads, read the relevant server instructions and verify both sides of the contract.
