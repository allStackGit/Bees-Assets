# Bees experiments (initial version)

From the Bees project root, with Node installed and an existing RL build:

```sh
node Assets/Training/bees_experiments.js
```

Open **http://127.0.0.1:7148** on the same machine. Keep the process running while unattended; it is not yet installed as a boot-time service. The interface is intentionally bound to loopback and is not accessible directly from remote workers.

Queue one or more experiments. Each creates a **new run** from the current built release, using the cluster environment arguments with explicitly entered `--rl-*` overrides. Specify a maximum learner step count, maximum wall time, and no-progress timeout. Press **Run queue** to start. The manager requires the existing Bees operator to confirm its full graceful stop (including worker finalization) before starting the next run; disabling training alone is insufficient. **Stop safely** disarms the queue and requests coordinated stop of the active experiment.

Existing training must be stopped before starting a queue, and the latest release must match the release recorded when experiments were queued. If the operator or another process changes training ownership, the queue pauses rather than taking over an unrelated run. Stop requests are persisted, so restarting the coordinator does not silently cancel a requested graceful stop. After a crash during startup, the coordinator can resume monitoring a new run when the control-plane run identity and environment arguments match; ambiguous states pause the queue for operator inspection.

Summaries are written to `Training/Experiments/experiments-v1.json` **outside Assets**, not to the Git repository. It contains experiment definitions, run IDs, bounded sampled learner/worker metrics and outcomes. No raw trajectories or checkpoint copies are created; standard Bees checkpoint lifecycle is unchanged. Every sample is from training telemetry, **not an independent fixed-seed benchmark**; run-to-run results remain stochastic. Live worker summaries are included only after their training-control revision matches the active run; older worker windows are excluded. The UI supports per-run inspection, learning-curve comparisons and group aggregates. Frozen-checkpoint evaluation, PPO parameter sweeps, randomized seeds, scheduler boot persistence, and multi-machine validation are not implemented in this initial version.

A running experiment depends on the coordinator continuing to execute; if its process or host stops, it cannot enforce the step/time limit until restarted. For expensive unattended runs, keep the host and coordinator monitored. Do not treat this release as a crash-proof unattended training service until that behavior has been validated in the actual cluster.
