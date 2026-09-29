# Continual learning setup

This is a short operator/maintainer reference for the implemented continual-learning path. It is not an architecture specification.

## Sources of truth

- Policy ABI: `Scripts/Scenes/RlPolicySchema.cs`
- Trainer settings: `Training/rl_1v1_config.yaml`
- Structured policy: `Training/bees_mlagents_structured_policy.py`
- Continual compatibility and promotion settings: `Training/continual_learning_config.json`
- Registry/state rules: `Training/bees_continual_learning.py`
- Autonomous generation loop: `Training/bees_continual_service.py`

The current contract is ABI v21: `BeesRL1v1`, 7,614 observations, 16 continuous actions, and discrete branches `2,2,2,2,2,5`. Models, demonstrations, telemetry, server upload gates, and production deployment must match the exact current policy signature. Do not relabel data from an older ABI.

## Python environment

Use the existing ML-Agents environment and install the continual-learning additions:

```powershell
.\.venv-mlagents\Scripts\Activate.ps1
python -m pip install -r Training\requirements-continual.txt
```

Historical-opponent training and authoritative ONNX evaluation/deployment require a working ONNX Runtime provider. `Training/continual_learning_config.json` selects the provider used for historical opponents.

## Normal operation

Managed training should normally be controlled through `bees.ps1` / `Training/bees_operator.js`. The central continual service owns the persistent optimizer lineage, generation boundaries, candidate registration, evaluation/release phase, and publication of an approved champion.

Use the lower-level Python entrypoints directly only for focused diagnostics, repair, or explicit manual workflows. Their current command-line interfaces are the authority; use `--help` rather than copying old command examples from documentation.

Important entrypoints:

- `bees_continual_service.py` — autonomous train/release/publish loop
- `bees_continual_train.py` / `bees_continual_auto_train.py` — continual training wrappers
- `bees_continual_evaluate.py` — candidate evaluation
- `bees_continual_release.py` — guarded candidate release
- `bees_continual_deployment.py` — immutable champion deployment package
- `bees_continual_hot_release.py` — platform hot bundle publication
- `bees_continual_release_rollback.py` — guarded rollback
- `bees_continual_demo_curation.py` — public demonstration approval/revocation
- `bees_continual_adversarial*.py` — reviewed player-derived pressure/replay tools

## Data rules

- PPO updates use fresh Unity rollouts.
- Human demonstrations are supervised/imitation evidence, not stale PPO trajectories.
- Live gameplay telemetry is archived/curated separately and may be used to discover pressure or evaluation cases.
- Historical policies are replayed by generating fresh rollouts against them.
- A candidate is not a production champion until the current promotion path accepts it.
- Production clients accept only compatible validated champion bundles and fall back to Hive Mind if neural deployment cannot be used safely.

Player capture flags currently include `--rl-record-demonstrations` and `--rl-upload-demonstrations`. Capture/upload formats are ABI-bound and validated by both client and server.

Keep this file short. When implementation changes, update the executable/configuration owner first and only update this reference if an operator-facing route changed.
