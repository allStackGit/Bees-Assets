# Bees experiments (initial version)

From the Bees project root, with Node installed and an existing RL build:

```sh
node Assets/Training/bees_experiments.js
```

Open **http://127.0.0.1:7148** on the same machine. Keep the process running while unattended; it is not yet installed as a boot-time service. The interface is intentionally bound to loopback and is not accessible directly from remote workers.

Queue one or more experiments. Each creates a **new run** from the current built release, using the cluster environment arguments with explicitly entered `--rl-*` overrides. Specify a learner step budget, maximum wall time, and no-progress timeout. Step budgets use the existing published learner summaries, so stopping can overshoot by the summary interval and graceful-stop latency. Press **Run queue** to start. The manager requires the existing Bees operator to confirm its full graceful stop (including worker finalization) before starting the next run; disabling training alone is insufficient. **Stop safely** disarms the queue and requests coordinated stop of the active experiment.

Existing training must be stopped before starting a queue, and the latest release must match the release recorded when experiments were queued. If the operator or another process changes training ownership, the queue pauses rather than taking over an unrelated run. Stop requests are persisted, so restarting the coordinator does not silently cancel a requested graceful stop. After a crash during startup, the coordinator can resume monitoring a new run when the control-plane run identity and environment arguments match; ambiguous states pause the queue for operator inspection.

Summaries are written to `Training/Experiments/experiments-v1.json` **outside Assets**, not to the Git repository. It contains experiment definitions, run IDs, bounded sampled learner/worker metrics and outcomes. No raw trajectories or checkpoint copies are created; standard Bees checkpoint lifecycle is unchanged. Every sample is from training telemetry, **not an independent fixed-seed benchmark**; run-to-run results remain stochastic. Live worker summaries are included only after their training-control revision matches the active run; older worker windows are excluded. The UI supports per-run inspection, learning-curve comparisons and group aggregates. Expand **Worker Status** to see live remote-worker state, errors, throughput and queue depths from the existing training control; it is read-only, refreshes while expanded and stores no additional history. PPO parameter sweeps, randomized seeds, scheduler boot persistence, and multi-machine validation are not implemented.

A running experiment depends on the coordinator continuing to execute; if its process or host stops, it cannot enforce the step/time limit until restarted. For expensive unattended runs, keep the host and coordinator monitored. Do not treat this release as a crash-proof unattended training service until that behavior has been validated in the actual cluster.

## Fixed-seed checkpoint evaluation (first independent benchmark)

After a training run has exported an ONNX policy, select that file and an
**unchanged** reference ONNX policy. In a Python environment with the existing
Bees ML-Agents/ONNX evaluation dependencies, run:

```sh
python Assets/Training/bees_experiment_benchmark.py --benchmark Assets/Training/experiment_benchmark.example.json --candidate path/to/candidate.onnx --opponent path/to/reference.onnx --env path/to/UnityEvaluator.exe --build-id EXACT_BUILD_ID --run-id OPTIONAL_TRAINING_RUN_ID
```

This is an offline evaluation and does not start or modify training. It uses
the existing `bees_continual_evaluate.run_match_group` evaluator and deterministic
ONNX actions. The versioned JSON recipe sets the seed, match count, environment
scenario arguments, and behavior. A revision or content change creates a new
benchmark identity. The built Unity executable must support the existing RL
evaluation side channel and match the policies' observation/action signature.
The example recipe is an **initial proposed scenario**, not yet a calibrated
benchmark of broader Bees competence.

Each evaluation writes one small JSON summary under
`Training/Experiments/Benchmarks` (outside Assets), with the recipe hash,
candidate/reference ONNX hashes, Unity build ID, executable hash, seed, and
outcome metrics. No model copies or per-episode trajectories are retained.
Expand **Checkpoint benchmarks** in the web interface and click
**Refresh results** to compare available summaries. Results from different
recipe hashes, reference models or environment builds should not be pooled.

Revision 2 explicitly selects Wasp/Gunship; the original sampled defaults included
Queen, which cannot fit the example's map range. Five-match live evaluations of
two v26 exports completed with validated telemetry and appeared in the web UI.
Both timed out in all matches; these are pipeline checks, not evidence of competence.

## Optional unattended evaluation

Create `Training/Experiments/benchmark-settings.json` outside Assets with absolute
paths appropriate to the manager host:

```json
{
  "python": "PATH_TO_EXISTING_MLAGENTS_PYTHON",
  "recipe": "PATH_TO_BENCHMARK_JSON",
  "opponent": "PATH_TO_FROZEN_REFERENCE_ONNX",
  "environment": "PATH_TO_UNITY_EXECUTABLE",
  "build_id": "EXACT_EVALUATION_BUILD_ID"
}
```

Select **Evaluate final checkpoint before the next experiment** when queuing a
run. Each item records the selected paths and file hashes, without copying models.
After a budget-completed run's verified stop, the manager selects its latest
registered export by training step, verifies the registered artifact hash and runs
the same independent evaluator. It saves only the report filename on the item.
The queue advances only after evaluation succeeds; a failure preserves training
completion, records the error and disarms the queue. Manually stopped or early-ended
runs are not automatically evaluated.

For an existing finished run, expand its details and choose **Evaluate checkpoint**.
This also provides an explicit retry after a failed evaluation. Training and the
queue must be idle. A coordinator restart resumes a pending evaluation; if it
crashed after writing a report but before saving its filename, a repeated evaluation
can leave another immutable summary. Inspect all summaries before pooling results.

For CLI selection from the same registry, replace `--candidate FILE` with
`--candidate-run RUN_ID`; `--registry FILE` defaults to
`Training/registry.sqlite3`. This reads existing provenance without updating it.
PPO settings still use the existing release workflow; evaluation does not change them.
