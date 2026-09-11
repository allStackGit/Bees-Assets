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
      "opponent_model_id": "bees-rl-v6-...",
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
