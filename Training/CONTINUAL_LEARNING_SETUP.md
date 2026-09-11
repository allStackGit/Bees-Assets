# Bees continual-learning Python setup

The continual-learning tools extend the project's existing ML-Agents 1.1.0 environment. They do not replace or upgrade that environment.

## CPU baseline

Activate the same virtual environment used for Bees ML-Agents training, then install the frozen-policy runtime add-on:

```powershell
.\.venv-mlagents\Scripts\Activate.ps1
python -m pip install -r Training\requirements-continual.txt
```

Verify the runtime before starting a long run:

```powershell
python -c "import onnxruntime as ort; print(ort.__version__); print(ort.get_available_providers())"
```

The committed `Training/continual_learning_config.json` uses `CPUExecutionProvider` for persistent historical opponents. With `historical_league.training_ratio` above zero, `bees_continual_train.py` performs an ONNX Runtime/provider preflight before ML-Agents starts, so a missing package or unavailable provider fails early rather than after training is underway.

The authoritative offline evaluator also requires ONNX Runtime because candidate, champion, and historical policies are executed from immutable ONNX artifacts. `bees_continual_evaluate.py` uses `CPUExecutionProvider` by default and accepts `--onnx-provider` when another installed provider is intentionally selected.

## Human demonstration capture and imitation

Player-facing builds can opt into passive human/Hive Mind demonstration capture with:

```text
--rl-record-demonstrations
```

Recordings use the shared `BeesRL1v1` observation/action ABI and are stored below `Application.persistentDataPath/RlDemonstrations`. Each frozen policy ABI receives its own directory (for example, `PolicyV7`), with Human and Hive Mind recordings physically separated below that. The policy directory also contains `capture-manifest.json`, which records the exact behavior name, policy ABI/signature, observation size, and action shape. Capture fails closed rather than writing into a versioned directory whose existing manifest is incompatible or missing while demonstrations already exist.

For policy ABI v7, the normal layout is:

```text
RlDemonstrations/
  PolicyV7/
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

The passive recorder continuously captures movement, weapon aiming, and weapon firing. Successful one-shot capability events are additionally written as isolated native ML-Agents demonstration episodes by `RlGameplayDemonstrationCapabilityCapture`. This covers ship specials, mining, healing, and warp. At the authoritative event boundary, the capability recorder synchronously runs the same `RlCombatPerception` contract and the same movement/weapon-control encoding as the passive recorder, changes only the special-action branch, and writes a terminal record immediately after the event sample. This preserves the policy state immediately before policy-observable event changes and ensures the ML-Agents demonstration loader retains the event sample even if the ship leaves the level immediately afterward. If the capture contract is unavailable or incompatible, the event is skipped rather than fabricating a label.

The capability-event writer uses a narrow reflection bridge to pinned ML-Agents 1.1.0 internals for reading a `VectorSensor`'s completed vector and invoking the native `DemonstrationRecorder`/`DemonstrationWriter` serialization path. `BeesFoundation` tests guard those reflection contracts so a package change fails visibly instead of silently corrupting demonstrations.

To include a trusted set of native ML-Agents human `.demo` files directly in a continual training run, pass the current policy ABI's `Human` directory to the continual wrapper:

```powershell
python Training\bees_continual_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-full-001 --resume `
  --continual-root="F:\RLDemo\BeesContinual" `
  --continual-game-build="2026.09.11" `
  --continual-human-demo-dir="C:\path\to\RlDemonstrations\PolicyV7\Human"
```

Before ML-Agents starts, the wrapper requires the selected directory to be the `Human` directory under the configured `PolicyV<ABI>` capture root and validates its `capture-manifest.json` against the continual config's behavior name, ABI version, and exact frozen policy signature. It then verifies that the directory contains non-empty `.demo` files, rejects files explicitly named as Hive Mind recordings, hashes the selected files and capture manifest, and copies all of them into an immutable content-addressed snapshot under the continual-learning root. The snapshot manifest preserves the original capture metadata and its SHA-256 so the training set retains its ABI provenance. The source recordings must therefore be closed/stable before training starts. If either a recording or the capture manifest changes during snapshotting, startup fails instead of silently training against a moving or mislabeled dataset.

The wrapper derives a runtime trainer YAML instead of modifying `Training/rl_1v1_config.yaml`. It adds ML-Agents behavioral cloning for `BeesRL1v1`, pointing `demo_path` at the immutable snapshot. The generated YAML is also the configuration hashed into newly registered candidate lineage. Initial behavioral-cloning tuning is controlled by `human_imitation` in `Training/continual_learning_config.json`; it is deliberately configurable rather than part of the frozen neural-policy ABI. The config's `policy_signature` mirrors `RlPolicySchema.Signature` and must change with the frozen policy ABI rather than being carried forward by version number alone.

### Central native `.demo` archive

Trusted native demonstrations can also be imported into the persistent continual-learning store instead of being consumed immediately. Run this from the same ML-Agents virtual environment so the native demonstration parser is available:

```powershell
python Training\bees_continual_native_demo.py `
  --root="F:\RLDemo\BeesContinual" `
  --demonstration-id="player-match-20260911-001-human-s0" `
  --model-id="bees-rl-v7-<deployed-model-id>" `
  --game-build="2026.09.11" `
  "C:\path\to\RlDemonstrations\PolicyV7\Human\human-s0.demo"
```

`model-id` identifies the compatible deployed-policy context for the captured match; it does not claim that the human actions came from that model. `bees_continual_native_demo.py` uses the existing `demonstration_batches` registry and `experience/human-demos` archive. It validates the Human/PolicyV capture manifest and frozen signature, parses the native ML-Agents behavior shape, enforces the configured payload and record limits, rejects Hive Mind filenames, and requires a known compatible model context. Successful ingestion stores the original `.demo`, a byte-for-byte capture-manifest copy, and an immutable metadata sidecar containing their SHA-256 hashes and the trainable example count. Repeating identical ingestion is idempotent; reusing a demonstration ID for different content is rejected.

This central archive is deliberately separate from PPO trajectories and from automatic training selection. Local `--continual-human-demo-dir` remains the explicit trusted/curated behavioral-cloning input. Archived public batches must pass the separate curation boundary below before they can be materialized into a trainer-compatible Human directory.

### Authenticated public-client upload quarantine

A Production desktop build can separately opt into uploading previously closed Human captures with:

```text
--rl-upload-demonstrations
```

`RlDemonstrationUploader` snapshots only `.demo` files that already exist in the current `PolicyV<ABI>/Human` directory before the current run starts recording. It therefore never uploads the file an active `DemonstrationRecorder` is still writing; captures produced during the current run remain local and become eligible on a later launch. WebGL is excluded because the required Steam Web API authentication is unavailable there. Upload uses a dedicated WSS connection rather than the gameplay `Socket`, authenticates each request with the Steam Web API ticket, transfers the native demo in bounded chunks, and never deletes the local source file. The client, BeesServer, and trainer all apply the same 16 MiB maximum to the combined `.demo` plus capture-manifest payload.

BeesServer accepts only `Human` uploads through the three-request `rl-demo-begin` / `rl-demo-chunk` / `rl-demo-complete` protocol. Sessions are bound to the authenticated Steam user and WebSocket connection; the server enforces per-user/global active-upload limits and a per-user byte window, validates the exact frozen capture manifest, verifies the declared SHA-256, and writes content-addressed immutable `.demo`, capture-manifest, and metadata files into its configured quarantine inbox. The quarantine sidecar deliberately records `trust: authenticated-quarantine` and `readyForTraining: false`: Steam authentication establishes who uploaded the bytes, not that the play is good training data.

To validate and archive one server quarantine bundle centrally, supply its metadata sidecar plus the compatible deployed model context:

```powershell
python Training\bees_continual_public_demo.py `
  --root="F:\RLDemo\BeesContinual" `
  --model-id="bees-rl-v7-<deployed-model-id>" `
  "D:\BeesRlDemonstrations\incoming\rl-demo-<batch-id>.json"
```

`bees_continual_public_demo.py` independently verifies the quarantine schema/trust state, metadata filename and batch identity, demo and manifest byte counts/hashes, exact embedded/file capture-manifest agreement, the continual store's configured combined payload limit, and the native ML-Agents observation/action structure before delegating to the normal native-demo archive. The raw Steam user ID remains in the server-side quarantine for abuse handling and is intentionally not copied into the continual-learning store. Instead, a store-local secret HMAC maps it to a stable 64-hex contributor bucket stored separately from the native archive. This supports contributor balancing without placing the raw Steam identity in training provenance. The central public provenance record still sets `approved_for_training: false`.

### Public demonstration curation and BC selection

Public demonstrations require an explicit operator decision after structural validation. Approve a central native batch with a review reason and a normalized quality score:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinual" `
  approve demo-<central-batch-id> `
  --reviewer="manual-review-2026-09" `
  --reason="Clean long-range kiting example" `
  --quality-score=0.9
```

Approval does not automatically add the batch to training. Decisions are immutable; a later safety or quality problem can permanently exclude an approved batch from future sets:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinual" `
  revoke demo-<central-batch-id> `
  --reviewer="manual-review-2026-09" `
  --reason="Retrospective review found unusable play"
```

Create a training set only from an explicit list of approved central batch IDs:

```powershell
python Training\bees_continual_demo_curation.py `
  --root="F:\RLDemo\BeesContinual" `
  materialize demo-<batch-a> demo-<batch-b> demo-<batch-c>
```

Materialization rechecks the immutable native archive and approval hashes, rejects revoked or unapproved batches, requires a common capture-manifest contract, and applies the committed `human_imitation.public_min_quality_score` and `human_imitation.public_max_batches_per_contributor` limits. The current defaults are `0.5` and `8`. Contributor grouping uses the store-local HMAC key at `metadata/public-demo-contributor.key`; if contributor records already exist and this key disappears, ingestion fails closed rather than silently generating a new identity namespace that could bypass the per-contributor cap.

The result includes `human_demo_dir`, pointing to an immutable `public-approved-set-.../PolicyV<ABI>/Human` directory. Feed that directory to the existing continual trainer exactly like any other curated Human source:

```powershell
python Training\bees_continual_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-full-001 --resume `
  --continual-root="F:\RLDemo\BeesContinual" `
  --continual-game-build="2026.09.11" `
  --continual-human-demo-dir="<human_demo_dir returned by materialize>"
```

This preserves the existing trainer fail-closed path: the wrapper validates and snapshots the materialized Human directory again before enabling behavioral cloning. Public ingestion therefore remains distinct from approval, and approval remains distinct from selection for a particular training run.

### Player-derived adversarial matchup pressure

Approved demonstrations can also identify a tactic that should create fresh headless RL pressure instead of imitation data. Register an immutable scenario from one or more currently approved public batches:

```powershell
python Training\bees_continual_adversarial.py `
  --root="F:\RLDemo\BeesContinual" `
  register demo-<batch-a> demo-<batch-b> `
  --bee-composition="Wasp" `
  --human-composition="Gunship" `
  --target-fraction=0.10 `
  --rationale="Repeated long-range kiting tactic"
```

The returned `adv-...` ID is content-addressed from the source approvals, policy ABI, exact Bee/Human compositions, target fraction, and rationale. Registration does not copy an old trajectory into PPO. At training time the current policy receives a fresh episode with that fleet matchup. A scenario may request at most 50% of episodes, and the combined selected scenarios may request at most 50%, so normal sampled/adaptive training always remains at least half of the distribution.

Scenario sources are revalidated immediately before a training launch. If an underlying public demonstration is later revoked, its approval/archive identity changes, or the policy ABI is no longer compatible, the old adversarial scenario fails closed rather than continuing to influence training.

Launch continual training with an explicit immutable scenario selection using the adversarial wrapper:

```powershell
python Training\bees_continual_adversarial_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-adversarial-001 `
  --continual-root="F:\RLDemo\BeesContinual" `
  --continual-game-build="2026.09.11" `
  --continual-adversarial-scenarios=adv-<scenario-a>,adv-<scenario-b> `
  --env-args `
  --rl-matchup-mode=sampled `
  --rl-ships-per-side=1 `
  --rl-bee-ship-types=Wasp,Hornet `
  --rl-human-ship-types=Gunship,Frigate
```

Keep `--env-args` as a separate token and place Unity environment arguments after it. Each selected scenario must use the same `ships-per-side` value as the run, and every ship type used by a scenario must be included in the run's sampled Bee/Human candidate pools. Do not pass `--bees-adversarial-matchups` manually; `bees_continual_adversarial_train.py` derives it from the immutable registry and writes the exact scenario selection under `metadata/adversarial-training-runs`. Reusing a run ID with a different selection is rejected so candidate lineage cannot silently change pressure on resume.

`RlOneVsOnePerArenaMatchups` samples player-derived pressure independently in each arena. Reserved episodes are labeled internally by scenario ID, while all non-reserved episodes continue through the existing baseline/adaptive matchup sampler. Player-derived outcomes are intentionally not added to adaptive-matchup history, keeping external player pressure independently measurable. When adversarial pressure is configured, Unity emits one compact `player_derived_pressure` summary every 1000 prepared episodes per arena with the observed rate and per-scenario counts.

This is the first Phase 7 implementation boundary. It converts reviewed repeated tactics into measurable fresh matchup pressure, but it does not yet reconstruct player movement/spatial state, replay a scripted action sequence, automatically mine tactics from uploads, or add scenario-specific counterplay results to the authoritative promotion evaluator. Those are separate later Phase 7 steps.

## Permanent competency suite

When `promotion.min_competency_cases` is greater than zero, pin the trusted permanent competency suite in the continual-learning store before recording promotion-eligible evaluations:

```powershell
python Training\bees_continual_learning.py --root <store> pin-competency-suite <suite.json>
```

The pinned contract includes each case's name, opponent model ID, match count, minimum score, metric, critical flag, and environment arguments. Candidate evaluations must contain exactly the same normalized contract; an arbitrary easier suite cannot satisfy the promotion gate.

Use `--replace` only for an intentional permanent-suite revision:

```powershell
python Training\bees_continual_learning.py --root <store> pin-competency-suite <suite.json> --replace
```

Replacing the suite changes the promotion-policy fingerprint, so evaluations recorded under the previous suite must be rerun before promotion. `status` reports the current suite fingerprint and case count.

A suite uses schema version 1:

```json
{
  "schema_version": 1,
  "cases": [
    {
      "name": "large-map-aiming",
      "opponent_model_id": "bees-rl-v7-...",
      "matches": 200,
      "minimum": 0.55,
      "metric": "score_rate",
      "critical": true,
      "env_args": ["--rl-map-size", "128"]
    }
  ]
}
```

The evaluator's `--competency-suite` file must describe the same permanent contract that was pinned in the store.

## Promotion runtime gate

Promotion-eligible evaluation measures the wall-clock duration of every successful candidate `onnxruntime.InferenceSession.run()` batch. The committed promotion policy requires the worst observed candidate batch to be no slower than:

```json
"max_inference_batch_milliseconds": 100.0
```

This threshold is part of the promotion configuration, so changing it changes the promotion-policy fingerprint and makes older evaluations stale. The evaluator report records the configured threshold, worst observed candidate batch, and candidate inference-call count under `evaluator.runtime_latency`.

If the threshold is omitted, `runtime_checks_passed` fails closed. Injected/custom match runners also cannot claim authoritative runtime checks even if they provide synthetic timing values. The latency gate measures ONNX session execution itself; it does not include Unity simulation time or opponent inference time.

## GPU ONNX Runtime (optional)

Do not install CPU `onnxruntime` and `onnxruntime-gpu` side by side. If frozen-policy inference itself needs GPU acceleration, replace the CPU package with an `onnxruntime-gpu` build compatible with the machine's CUDA/cuDNN stack, then verify that `CUDAExecutionProvider` appears in `ort.get_available_providers()` before selecting it.

For historical-opponent training, set:

```json
"historical_league": {
  "training_onnx_provider": "CUDAExecutionProvider"
}
```

For offline evaluation, pass:

```powershell
--onnx-provider CUDAExecutionProvider
```

PyTorch's `--torch-device=cuda` and ONNX Runtime's execution provider are independent settings. A working CUDA PyTorch installation does not by itself prove that the ONNX Runtime CUDA provider is installed or compatible.

## When ONNX Runtime is required

Persistent historical-opponent training requires ONNX Runtime when `historical_league.training_ratio > 0`. Setting that ratio to `0` disables the historical ONNX opponent path, so ordinary Bees/ML-Agents training and continual candidate registration do not need this add-on. Offline authoritative evaluation still requires ONNX Runtime regardless of the historical training ratio.

This add-on file intentionally does not repin the rest of the Python stack. `Training/bees_mlagents_learn.py` and the persistent historical-opponent bridge are explicitly written and guarded for ML-Agents 1.1.0; use the project's existing ML-Agents virtual environment rather than creating a second dependency stack from this file.
