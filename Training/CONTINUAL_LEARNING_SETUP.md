# Bees continual-learning Python setup

The continual-learning tools extend the project's existing ML-Agents 1.1.0 environment. They do not replace or upgrade that environment.

The current frozen policy contract is ABI v8: behavior `BeesRL1v1`, 4722 vector observations, 34 continuous actions, and discrete branches `2x16,5,65,65,65`. ABI v8 appends one episode-progress value plus 20 reserved tail values after the prior 4701 observation fields. Existing tactical field indices are unchanged, but v7 models and demonstrations are not ABI-compatible with v8 and must not be relabeled or silently reused as v8 data.

## CPU baseline

Activate the same virtual environment used for Bees ML-Agents training, then install the frozen-policy runtime add-on:

```powershell
.\.venv-mlagents\Scripts\Activate.ps1
python -m pip install -r Training\requirements-continual.txt
```

Verify ONNX Runtime before starting a long run:

```powershell
python -c "import onnxruntime as ort; print(ort.__version__); print(ort.get_available_providers())"
```

`Training/continual_learning_config.json` uses `CPUExecutionProvider` for persistent historical opponents by default. With `historical_league.training_ratio` above zero, `bees_continual_train.py` performs an ONNX Runtime/provider preflight before ML-Agents starts. Authoritative offline evaluation and champion deployment packaging also require ONNX Runtime because they independently load immutable ONNX artifacts.

## Human demonstration capture and imitation

Player-facing builds can opt into passive Human/Hive Mind demonstration capture with:

```text
--rl-record-demonstrations
```

Recordings use the shared `BeesRL1v1` observation/action ABI and are stored below `Application.persistentDataPath/RlDemonstrations`. Each frozen policy ABI receives its own directory, with Human and Hive Mind recordings physically separated. The current layout is:

```text
RlDemonstrations/
  PolicyV8/
    capture-manifest.json
    Human/
      human-s0.demo
      human-cap-s0.demo
      ...
    HiveMind/
      hivemind-s0.demo
      hivemind-cap-s0.demo
      ...
```

`capture-manifest.json` records the exact behavior name, policy ABI/signature, observation size, and action shape. Capture fails closed if the directory contract does not match the compiled policy. Successful one-shot capability events such as ship specials, mining, healing, and warp are additionally written as isolated native ML-Agents demonstration episodes by `RlGameplayDemonstrationCapabilityCapture`.

To include a trusted native Human directory in a continual training run:

```powershell
python Training\bees_continual_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-full-v8-001 --resume `
  --continual-root="F:\RLDemo\BeesContinualV8" `
  --continual-game-build="2026.09.12" `
  --continual-human-demo-dir="C:\path\to\RlDemonstrations\PolicyV8\Human"
```

Before ML-Agents starts, the wrapper verifies the Human/PolicyV directory, capture manifest, frozen signature, non-empty `.demo` files, and file stability. It creates an immutable content-addressed snapshot and derives a runtime trainer YAML that adds behavioral cloning without modifying the committed base trainer YAML. `human_imitation` in `Training/continual_learning_config.json` controls the BC strength, step count, and batch size.

Do not point an ABI-v8 run at `PolicyV7`. There is no automatic v7-to-v8 demonstration conversion path.

### Central native `.demo` archive

Trusted native demonstrations can be imported into the persistent continual-learning store instead of being consumed immediately:

```powershell
python Training\bees_continual_native_demo.py `
  --root="F:\RLDemo\BeesContinualV8" `
  --demonstration-id="player-match-20260912-001-human-s0" `
  --model-id="bees-rl-v8-<deployed-model-id>" `
  --game-build="2026.09.12" `
  "C:\path\to\RlDemonstrations\PolicyV8\Human\human-s0.demo"
```

`model-id` identifies the compatible deployed-policy context for the captured match; it does not claim that the Human actions came from that model. Native ingestion validates the capture manifest/signature, ML-Agents behavior shape, configured payload/record limits, model compatibility, and immutable SHA-256 identities.

### Authenticated public-client upload quarantine

A Production desktop build can separately opt into uploading previously closed Human captures with:

```text
--rl-upload-demonstrations
```

`RlDemonstrationUploader` snapshots only files already closed before the current run begins recording. Upload uses a dedicated Steam-authenticated WSS connection and bounded chunks. The client, BeesServer, and trainer enforce the same 16 MiB combined demonstration-plus-manifest cap.

BeesServer accepts only `Human` uploads through `rl-demo-begin`, `rl-demo-chunk`, and `rl-demo-complete`. Sessions are bound to the authenticated Steam user and connection, rate/size limited, hash checked, and quarantined with `readyForTraining: false`. The server's accepted capture policy is part of the cross-repository frozen ABI contract; for the current build it is ABI v8 / 4722 observations with the exact `RlPolicySchema.Signature`.

Archive one validated server quarantine bundle centrally with:

```powershell
python Training\bees_continual_public_demo.py `
  --root="F:\RLDemo\BeesContinualV8" `
  --model-id="bees-rl-v8-<deployed-model-id>" `
  "D:\BeesRlDemonstrations\incoming\rl-demo-<batch-id>.json"
```

The raw Steam user ID stays in server quarantine. The continual store retains only a store-local HMAC contributor bucket for balancing and leaves the archived public batch `approved_for_training: false`.

### Public demonstration curation and BC selection

Approve a structurally valid public batch only after review:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinualV8" `
  approve demo-<central-batch-id> `
  --reviewer="manual-review-2026-09" `
  --reason="Clean long-range kiting example" `
  --quality-score=0.9
```

Revoke an approved batch permanently if later review finds a problem:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinualV8" `
  revoke demo-<central-batch-id> `
  --reviewer="manual-review-2026-09" `
  --reason="Retrospective review found unusable play"
```

Materialize only an explicit approved selection:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinualV8" `
  materialize demo-<batch-a> demo-<batch-b> demo-<batch-c>
```

Materialization rechecks archive/approval hashes, rejects revoked or unapproved batches, requires a common capture contract, and enforces `human_imitation.public_min_quality_score` plus `human_imitation.public_max_batches_per_contributor`. The returned `human_demo_dir` is accepted by `--continual-human-demo-dir` and is validated/snapshotted again by the trainer wrapper.

## Player-derived adversarial training

Phase 7 keeps old player demonstrations out of PPO trajectories. Approved Human data is used to identify or script pressure; the learning side still generates fresh on-policy responses.

### Mine and review tactics

Mine repeated explainable signatures from approved current-ABI demonstrations:

```powershell
python Training\bees_continual_adversarial_mine.py `
  --root="F:\RLDemo\BeesContinualV8" `
  demo-<batch-a> demo-<batch-b> demo-<batch-c> `
  --minimum-count=2
```

For one approved demonstration, inspect non-authoritative tactical geometry suggestions:

```powershell
python Training\bees_continual_adversarial_suggest.py `
  --root="F:\RLDemo\BeesContinualV8" `
  demo-<batch-a>
```

ABI v8 appends its episode-progress/reserved tail after the fields consumed by the miner/suggester, so the existing ship/range/map indices retain their meaning. The tools still require the exact current observation size and fail closed on another ABI.

### Register reviewed scenario pressure

After review, register an immutable scenario from one or more approved batches:

```powershell
python Training\bees_continual_adversarial.py `
  --root="F:\RLDemo\BeesContinualV8" `
  register demo-<batch-a> demo-<batch-b> `
  --bee-composition="Wasp" `
  --human-composition="Gunship" `
  --target-fraction=0.10 `
  --rationale="Repeated long-range kiting tactic" `
  --map-size=96 `
  --spawn-separation-ratio=0.50
```

`--map-size` and `--spawn-separation-ratio` are optional but must be supplied together. The returned `adv-...` identity binds the approved source provenance, policy ABI, compositions, requested pressure, rationale, and optional geometry. A single scenario and the combined selected scenario set are both capped so at least half of training remains outside player-derived pressure.

### Attach an approved action replay

For a reviewed 1v1 scenario, one approved source batch can be attached as a bounded scripted movement/aim/fire replay:

```powershell
python Training\bees_continual_adversarial_replay.py `
  --root="F:\RLDemo\BeesContinualV8" `
  register adv-<scenario-id> `
  --source-batch=demo-<batch-a> `
  --side=Human `
  --record-count=1200
```

The source must already belong to the scenario. Replay compilation revalidates the approved archive, exact current policy behavior, first-episode bounds, ship identities, and action contract. Capability/target actions fail closed; the current scripted format covers movement, turret aim, and fire only. The replay side is excluded from PPO learner ownership and becomes neutral after the recorded prefix.

You can compile/check one attachment explicitly:

```powershell
python Training\bees_continual_adversarial_replay.py `
  --root="F:\RLDemo\BeesContinualV8" `
  compile adv-<scenario-id>
```

The training launcher automatically builds the content-addressed replay catalog for selected scenarios that have attachments, so operators do not pass the Unity replay-catalog flag manually.

### Train against selected pressure

```powershell
python Training\bees_continual_adversarial_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-adversarial-v8-001 `
  --continual-root="F:\RLDemo\BeesContinualV8" `
  --continual-game-build="2026.09.12" `
  --continual-adversarial-scenarios=adv-<scenario-a>,adv-<scenario-b> `
  --env-args `
  --rl-matchup-mode=sampled `
  --rl-ships-per-side=1 `
  --rl-bee-ship-types=Wasp,Hornet `
  --rl-human-ship-types=Gunship,Frigate
```

Keep `--env-args` as a separate token. Do not manually pass `--bees-adversarial-matchups`, tactical-geometry flags, or `--bees-adversarial-replay-catalog`; the immutable scenario/replay registries are authoritative. The exact scenario and replay-catalog identity is recorded under `metadata/adversarial-training-runs`; changing it requires a new ML-Agents run ID.

### Measure counterplay

Use paired diagnostic evaluation to compare a candidate with a baseline under the same selected pressure:

```powershell
python Training\bees_continual_adversarial_evaluate.py `
  --root="F:\RLDemo\BeesContinualV8" `
  --candidate="bees-rl-v8-<candidate-id>" `
  --baseline="bees-rl-v8-<baseline-id>" `
  --opponent="bees-rl-v8-<opponent-id>" `
  --env="F:\RLDemo\Bees RL Training" `
  --scenario=adv-<scenario-a> `
  --scenario=adv-<scenario-b> `
  --matches=200 `
  --seed=36
```

The immutable report records candidate/baseline score rates and pressure-weighted deltas. It is intentionally `promotion_eligible: false`; player-derived diagnostics do not silently change the pinned permanent competency suite. Richer replay of intermediate world state, capabilities/targets, or arbitrary multi-ship Human episodes remains future Phase 7 work.

## Permanent competency suite and promotion

When `promotion.min_competency_cases` is greater than zero, pin the trusted permanent suite before recording promotion-eligible evaluations:

```powershell
python Training\bees_continual_learning.py `
  --root="F:\RLDemo\BeesContinualV8" `
  pin-competency-suite <suite.json>
```

Use `--replace` only for an intentional suite revision. Replacing the suite changes the promotion-policy fingerprint and invalidates older promotion evidence.

A current-ABI suite uses model IDs from the same v8 registry, for example:

```json
{
  "schema_version": 1,
  "cases": [
    {
      "name": "large-map-aiming",
      "opponent_model_id": "bees-rl-v8-...",
      "matches": 200,
      "minimum": 0.55,
      "metric": "score_rate",
      "critical": true,
      "env_args": ["--rl-map-size", "128"]
    }
  ]
}
```

Normal candidates must pass the authoritative evaluator before `promote`. The one exception is the explicitly audited generation-zero bootstrap for an empty registry:

```powershell
python Training\bees_continual_bootstrap.py `
  --store="F:\RLDemo\BeesContinualV8" `
  --candidate="bees-rl-v8-<candidate-id>" `
  --reason="Trusted generation-zero baseline"
```

Generation-zero bootstrap is one-time only; later champion changes must use normal evaluation/promotion.

### Promotion runtime gate

Promotion-eligible evaluation measures every successful candidate `onnxruntime.InferenceSession.run()` batch. The committed policy currently requires the worst observed candidate batch to be no slower than:

```json
"max_inference_batch_milliseconds": 100.0
```

Changing this threshold changes the promotion-policy fingerprint and makes older evaluations stale. Injected/custom match runners cannot claim authoritative runtime checks from synthetic timing.

## Champion deployment into Unity player builds

Phase 8 now has a fail-closed build-time deployment path. It does not yet implement server/client hot model distribution.

Package and publish the registry's current champion:

```powershell
python Training\bees_continual_deployment.py `
  --root="F:\RLDemo\BeesContinualV8" `
  publish
```

Packaging accepts only the registry's current champion. It independently verifies the registered artifact SHA-256, frozen compatibility/signature, passing promotion evidence or explicit generation-zero provenance, and real ONNX loadability. It writes an immutable content-addressed package below the continual store and atomically updates `deployment/current-deployment.json`.

Before making a player build, stage that exact published champion into the Unity Assets tree:

```powershell
python Training\bees_continual_unity_bundle.py `
  --root="F:\RLDemo\BeesContinualV8" `
  --assets-root="R:\Bees\Assets"
```

The installer calls the same publish/validation path again and atomically writes build inputs under:

```text
Resources/RlPolicy/BeesRL1v1.onnx
Resources/RlPolicy/BeesRL1v1Deployment.json
```

Those two generated files are ignored by Git. They are staging inputs, not the deployment authority. Unity imports the ONNX as an Inference Engine `ModelAsset` during normal asset import/build processing.

At runtime, `RlLivePolicyModelBootstrap` validates the bundled deployment manifest against the compiled `RlPolicySchema`, binds the imported champion to dynamically created `RlLivePolicyAgent`s with `SetModel`, and forces `BehaviorType.InferenceOnly`. If the manifest/model is missing, incompatible, conflicting, or cannot be bound, it clears `ActivateBrains`, disables the live RL agents, and restarts `Level.SetupHivemind()` so player-facing AI fails back to the existing Hive Mind instead of remaining on the live agent's inert heuristic.

After every promotion or rollback, rerun `bees_continual_unity_bundle.py` before the next player build. A rollback changes the authoritative current champion and therefore restages the prior champion package.

ABI compatibility is strict. A v7 champion cannot be deployed into the current v8 player build. A compatible v8 champion must first be registered/evaluated/promoted (or explicitly bootstrapped as generation zero in a new v8 store).

What remains unfinished in Phase 8 is automatic remote distribution: there is not yet a server protocol that delivers a newly promoted model to already-built clients or hot-swaps a downloaded champion at runtime. The supported release path today is promotion/rollback -> verified immutable package -> Unity Resources staging -> player build -> runtime inference-only binding/fallback.

## GPU ONNX Runtime (optional)

Do not install CPU `onnxruntime` and `onnxruntime-gpu` side by side. If frozen-policy inference needs GPU acceleration, replace the CPU package with a GPU build compatible with the machine's CUDA/cuDNN stack and verify `CUDAExecutionProvider` appears in `ort.get_available_providers()`.

For historical-opponent training:

```json
"historical_league": {
  "training_onnx_provider": "CUDAExecutionProvider"
}
```

For offline evaluation:

```powershell
--onnx-provider CUDAExecutionProvider
```

PyTorch's `--torch-device=cuda` and ONNX Runtime's execution provider are independent settings.

## When ONNX Runtime is required

Persistent historical-opponent training requires ONNX Runtime when `historical_league.training_ratio > 0`. Setting the ratio to `0` disables the historical ONNX opponent path. Authoritative offline evaluation and champion deployment packaging still require ONNX Runtime.

This add-on intentionally does not repin the rest of the Python stack. `Training/bees_mlagents_learn.py` and the historical-opponent bridge remain guarded for the project's existing ML-Agents environment.
