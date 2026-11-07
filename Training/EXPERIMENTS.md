# Bees experiments (initial version)

From the Bees project root, with Node installed and an existing RL build:

```sh
node Assets/Training/bees_experiments.js
```

Open **http://127.0.0.1:7148** on the same machine. Keep the process running while unattended; it is not yet installed as a boot-time service. The interface is intentionally bound to loopback and is not accessible directly from remote workers. For scripted, local experiment administration, `node Assets/Training/experiment_hub_cli.js get manager/state` reads the coordinator and `post manager/<action> --data request.json` calls allow-listed JSON controls using the same dashboard request shapes and explicit confirmations. Native Hub jobs use `get state` / `post jobs/start` without the `manager/` prefix. The CLI cannot install or restart the coordinator service.

Queue one or more experiments. Each creates a **new run** from the current built release, using the cluster environment arguments with explicitly entered `--rl-*` overrides. Specify a learner step budget, maximum wall time, and no-progress timeout. Step budgets use the existing published learner summaries, so stopping can overshoot by the summary interval and graceful-stop latency. Press **Run queue** to start. The manager requires the existing Bees operator to confirm its full graceful stop (including worker finalization) before starting the next run; disabling training alone is insufficient. **Stop safely** disarms the queue and requests coordinated stop of the active experiment.

## Experiment data location and offline durability

The **Experiment data storage** panel controls only Experiment Manager data:
the queue database, frozen trainer configurations, evaluator snapshots,
benchmark reports, per-match CSVs, and full metric history. Its default is the
existing `Training/Experiments` directory alongside the learner installation,
**not** an Assets repository directory. Enter an absolute local or mounted
path and choose **Copy data and change directory** while the manager is fully
idle (not merely paused). The destination must be empty. The source is copied,
not deleted, and absolute references to copied files in the experiment
database are rebased. **Restart the Experiment Manager** after a successful
copy; it refuses further mutations until restart. When the configured drive
is absent on restart, startup fails rather than silently using an empty
database. Model/optimizer checkpoints, Unity binaries, the training registry,
and Bees' runtime paths are deliberately unaffected.

Evaluator and benchmark runner Python sources are copied into immutable,
content-addressed snapshots at experiment queue time. Previously frozen
experiments can recover the original pre-research evaluator from bundled
legacy source files; if their recorded code hash is unavailable, evaluation
**fails closed** rather than substituting a changed evaluator. Each snapshot
executes with the original project Python dependencies available from the
training installation; snapshot hashes alone do not freeze installed packages.

The manager continues to send bounded samples to the live dashboard, but
also appends every newly recorded minute sample to
`Training/Experiments/Metrics/<experiment-id>.jsonl` (under the configured
data directory). **Load full chart history** loads recorded samples on demand,
calculates rolling smoothing across the full sequence, then retains extrema
for bounded chart rendering. **Save complete metrics JSONL** downloads the
unreduced archive, and evidence ZIPs include it. Older data already discarded
by earlier versions cannot be reconstructed.

## Maintenance pause and resume

For temporary updates, select **Pause for maintenance** while an experiment is
running. Wait until the interface reports **Paused** before closing the manager
or updating code. The coordinator requests the normal graceful distributed
stop, verifies the stopped run and existing optimizer checkpoint, and retains
the same active experiment, stage, run ID, remaining queue, and trainer settings.
**Stop safely** instead permanently finalizes the experiment; do not use it for
maintenance.

After updating and restarting the Experiment Manager, select **Resume training**.
It verifies the frozen run/release/configuration, existing checkpoint, and
training-control ownership, then calls the ordinary same-run start. If
BeesServer was shut down during maintenance, this start can bring it back
online after verifying the pinned release and checkpoint. It does not
create a fresh policy, change beta, or advance the curriculum. The recorded
maintenance interval is excluded from the run's wall-time limit and the
no-progress clock resets on confirmed resume. The manager also excludes the
maintenance period from effective-training-time measurements.

**Paused** is only reported after a completed, verified stop. If checkpoint
finalization fails, the manager keeps the experiment in **Pausing** and reports
the error rather than claiming it is safe to update. Pausing and resuming are
not supported during curriculum transition or benchmark evaluation. If training
control, the pinned runtime/release, or checkpoint identity has changed,
resume fails closed; restore the original compatible release or inspect the
operator state instead of starting a different run. This workflow updates the
manager UI without rebuilding the pinned Unity/trainer release. It does not
replace the normal release-compatibility and checkpoint requirements for a
training-runtime change.

Existing training must be stopped before starting a queue, and the latest release must match the release recorded when experiments were queued. If the operator or another process changes training ownership, the queue pauses rather than taking over an unrelated run. Stop requests are persisted, so restarting the coordinator does not silently cancel a requested graceful stop. After a crash during startup, the coordinator can resume monitoring a new run when the control-plane run identity and environment arguments match; ambiguous states pause the queue for operator inspection.

Summaries are written to `Training/Experiments/experiments-v1.json` **outside Assets**, not to the Git repository. It contains experiment definitions, run IDs, bounded sampled learner/worker metrics and outcomes. No raw trajectories or checkpoint copies are created; standard Bees checkpoint lifecycle is unchanged. Every sample is from training telemetry, **not an independent fixed-seed benchmark**; run-to-run results remain stochastic. Live worker summaries are included only after their training-control revision matches the active run; older worker windows are excluded. The UI supports per-run inspection, learning-curve comparisons and group aggregates. Expand **Worker Status** to see live remote-worker state, errors, throughput and queue depths from the existing training control; it is read-only, refreshes while expanded and stores no additional history. Trainer settings are frozen per queued experiment as described below. Automatic parameter sweeps and scheduler boot persistence are not implemented.

A running experiment depends on the coordinator continuing to execute; if its process or host stops, it cannot enforce the step/time limit until restarted. For expensive unattended runs, keep the host and coordinator monitored. Do not treat this release as a crash-proof unattended training service until that behavior has been validated in the actual cluster.

## First baseline: stationary 1v1 aiming

This is the **first pretraining stage**, not a beta comparison or a new curriculum
implementation. Use the existing **Queue an experiment** form; keep the trainer
configuration at the current release defaults (the frozen YAML records the
actual beta, lambda and network settings). Set the name to
`Baseline / stationary Wasp-Gunship` and use these environment overrides,
one per line:

```text
--rl-ships-per-side=1
--rl-bee-ship-types=Wasp
--rl-human-ship-types=Gunship
--rl-matchup-mode=fixed
--rl-map-size=24
--rl-episode-timeout=5
--rl-health-ratio=0.00001
--rl-disable-movement=true
--rl-static-obstacles=false
--rl-mining-asteroids=false
--rl-collision-asteroid-spawn-seconds=0
--rl-decision-period=5
```

Both ships have a single weapon with nominal range 40 and 6-by-6-unit hulls.
The placement code randomizes both positions and facings with collision/border
clearance. On the requested 24-unit square, the two ships start at close range;
verify the **effective** map size in a short live run because spawn-safety
logic can enlarge an arena when the requested dimensions are too small. The very small health
ratio gives each ship one hit point, so the first landed hit wins. The purpose
is to isolate **aim and fire decisions**, without movement or long-range search.
It does not establish mobile combat competence. Preserve random facing and
position; do not add spawn-handling code for this scenario.

The experiment manager requires numerical step and wall-time ceilings. Treat
them as **operational stop limits, not predicted learning times**: do not stop
at 5 million steps or claim convergence from the budget. Watch the existing
100-episode timeout window, shots and hits from **both** factions and the aim
telemetry. A useful *candidate* pretraining checkpoint has a sustained timeout
rate below 5% across successive windows, with both sides firing and actually
hitting (not merely declining to fire). Before calling it reusable, verify
that a full optimizer checkpoint loads and an ONNX export is registered, then
run the independent fixed-seed `experiment_benchmark.aiming.json` (100
matches) against an explicitly recorded, unchanged opponent. An initial
same-model-on-both-sides check only validates combat/evaluation mechanics, not
relative policy quality.

**Stage evaluation can use the versioned scripted controllers described below;
a frozen ONNX opponent is no longer required.**
Manual safe stop of a successful baseline does not trigger the manager's
automatic evaluation; evaluate the finished checkpoint explicitly afterward.
The queue starts a **fresh** policy/optimizer for each item: a second
queued experiment will **not** continue this network. To introduce movement
and longer ranges **within the same experiment/run**, use the generic curriculum
stages below. Only configure those transitions after checking that the proposed
scenario is physically and scientifically meaningful.
The checkpoint-finalization and stage-evaluation path passed a focused live
qualification on Exeter on 2026-10-31 using immutable release
`2026-10-31-172403-4909bbd32f05`. Run
`bees-v26-r6-s1-20261031T213146Z-b3ac8e22` saved readable checkpoints at
104,086 and 206,014 learner steps and registered distinct ONNX exports.
The same run resumed with all 159 Adam parameter states retained; optimizer
update counts advanced from 48 to 96. All three scripted scenarios completed
100 fixed-seed matches at both boundaries (600 matches, six distinct reports),
then the coordinator disarmed automatically. An isolated zero-action mechanics
probe observed an effective 24-by-24 arena and three exact 5-second timeouts.
This qualifies the save/evaluate/resume workflow, not policy quality or
crash-proof host supervision. Evidence is retained outside Assets under
`Training/Experiments/Qualification-20261031/`.

## Multi-stage training (same optimizer lineage)

New experiments load a **reusable, editable 19-stage combat curriculum** from
`Training/experiment-curriculum.json`. Each stage initially receives **20 million
learner steps** (380 million total), and the first stage supplies the starting
Wasp/Gunship environment flags. In the dashboard, expand individual stages to
edit their name, step budget, timeout observation target or `--rl-*` overrides,
reorder/remove stages, keep only the first N stages, or change all stage budgets
at once. **Save current curriculum as default** persists a reusable override to
`Training/Experiments/curriculum-default.json` outside Assets. **Reload saved
default curriculum** restores it to the form. Editing this default never changes
already queued or running experiments, whose effective stages are frozen.

There is no fixed number-of-stages cap or arbitrary experiment step/time ceiling.
Leave the time limit blank to disable it; the optional no-progress limit is an
independent safety control. **Continue indefinitely on the final stage** disables
the total step budget while preserving all earlier scheduled stage changes.
Learner steps must still be accurately representable as JavaScript safe integers.

The default **periodic evaluation** cadence is whichever produces more
checkpoints: every 100 million learner steps or at each 25% of a finite
experiment's cumulative step budget. For the 380M-step default, the planned
checkpoints are **95M, 190M, 285M and 380M**. Unlimited runs use 100M
intervals. Checkpoints may fall *within* a curriculum stage; the manager safely
stops, pins the ONNX checkpoint and its fixed benchmark suite, then resumes the
same run, optimizer and environment. A checkpoint coincident with a stage change
reuses its graceful stop. Actual stop steps can overshoot planned thresholds
because learner summaries and distributed checkpoint shutdown are asynchronous.
The other evaluation modes (each stage, final only, none) remain available.
Automatic evaluations require a frozen benchmark suite.

The experiment manager now accepts optional **curriculum stages** in the queue
form. The existing initial environment arguments and first-stage step budget
define stage 1. Expand **Curriculum stages** to name that stage and add further
stages. Each later stage defines a name, **additional** learner steps and
optional `--rl-*` overrides. Overrides inherit the preceding stage's effective
arguments (including removal of mutually exclusive fixed/ranged map flags).
All stage definitions are frozen on queue creation. **Edit queued** in the Runs table
allows an unstarted experiment's name, budgets, stages, trainer settings, seed, and
evaluation mode to be revised. Saving replaces its frozen definition and trainer
snapshot while preserving its queue position and identity. Previously recorded
releases and benchmark definitions remain pinned. Active, transitioning, and
finished experiments cannot be edited. Before an experiment starts, the manager
checks **every stage's environment arguments** with the
existing built-Unity startup validator; invalid later-stage arguments cannot
silently consume an earlier stage's training budget. A control-plane ownership
change during this preflight disarms the queue. For example, a hypothetical
first-stage budget of 30 million steps followed by 20 million additional steps
would change environments near 30 million cumulative learner steps and end the
run around 50 million; actual transition steps may overshoot published learner
summaries and graceful-stop latency.

Unlike the queue between **experiments**, a stage transition does **not**
request a new run. The coordinator persists its transition intent, requests
the operator's normal graceful stop, requires an existing optimizer checkpoint,
then starts the next environment with the **same run ID, trainer YAML, policy
and optimizer lineage**. The regular ML-Agents checkpoint resume path restores
learning; checkpoint compatibility and correct resumption must still be
qualified on Exeter. If consecutive stages have identical effective environment
arguments **and stage evaluations are disabled**, their boundary is recorded
without an unnecessary stop/restart. With stage evaluations enabled, even an
identical-environment boundary requires a graceful stop, fixed-seed suite and
checkpoint resume so its performance is measured at that exact stage.
Repeated manager cycles or a restart during a transition
reconcile the control-plane run ID/environment against the saved stages; an
ambiguous identity disarms the queue. A stop that occurred before its completion
could be durably recorded is also treated as ambiguous, rather than silently
restarting a possibly externally stopped run. A manual stop during a transition
halts the run rather than advancing it. The manager does not clone or duplicate a
checkpoint to create competing branches.

An optional stage **timeout target** is *observational only*: the first step
at which three consecutive timestamped training samples meet that threshold
is recorded as a provisional milestone. These overlapping worker windows are
not independent fixed-seed evaluations. **Stage transitions occur only at
fixed cumulative step boundaries**, not when a timeout target is reached.
Review timeouts, both factions' shots/hits, and independent fixed-seed results
before concluding a capability was mastered or deciding the next stage's
difficulty. Preserve the same curriculum and trainer settings except beta
when screening multiple independent beta runs.

The step budget is a safety/experimental boundary, not a declaration of
competence. Periodic checkpoints are separate from stage boundaries and use
the same candidate pinning, provenance and recovery rules described below.
Keep the manager running: while it is offline, it cannot enforce
boundaries; an unexpected state may require operator intervention. The
existing maximum wall time and no-progress stop limits apply to the **entire
run**, not to each curriculum stage. Optional **per-stage fixed-seed evaluation**
uses the frozen suite below. The previous final-only evaluation remains
available as a separate mode. Opponents may be versioned scripted controllers
or a frozen ONNX model, as recorded in the evaluation plan.

## Per-stage fixed-seed benchmark suite

To observe **adaptation and retention**, configure a frozen benchmark suite in
`Training/Experiments/benchmark-settings.json` outside Assets. The three
versioned scripted opponents require **no pretrained network**:

```json
{
  "python": "PATH_TO_EXISTING_MLAGENTS_PYTHON",
  "environment": "PATH_TO_UNITY_EXECUTABLE",
  "build_id": "EXACT_EVALUATION_BUILD_ID",
  "scenarios": [
    {
      "id": "aiming",
      "recipe": "ABSOLUTE_PATH_TO_experiment_benchmark.scripted-aiming.json",
      "opponent": "scripted:stationary"
    },
    {
      "id": "moving",
      "recipe": "ABSOLUTE_PATH_TO_experiment_benchmark.scripted-moving.json",
      "opponent": "scripted:strafe"
    },
    {
      "id": "pursuit",
      "recipe": "ABSOLUTE_PATH_TO_experiment_benchmark.scripted-pursuit.json",
      "opponent": "scripted:pursuit"
    }
  ]
}
```

The scripted controllers live in `Training/bees_experiment_scripted.py` and
operate solely on the existing BeesRL1v1 observation/action tensors: they do
not have Unity ground-truth positions or an extra training reward. **Stationary**
aims/fires but does not move; **strafe** tracks and orbits a visible enemy;
**pursuit** approaches a detected opponent and otherwise follows a fixed
search pattern. These are simple baselines, not optimal game-playing agents.
Their code is frozen by SHA-256 along with the recipe, evaluator, Unity build
and candidate provenance. A different script version or opponent mode produces
a different comparison identity.

The recipes, opponent identities, executable and evaluation code are hashed
and frozen **when the experiment is queued**. The same three scenarios,
opponents, seeds and build are used at **every** stage, including earlier
competencies. The new recipes match the proposed curriculum: 24 units/5 s
stationary, 32–64 units/15 s moving and 96–128 units/30 s pursuit, all
one-hit Wasp versus Gunship combat. They remain **uncalibrated screening
benchmarks** until a live evaluation confirms useful discriminating behavior.
Verify the actual physical arena dimensions on 24-unit maps before treating
the nominal recipe size as effective.

The default is periodic evaluation; choose **Every curriculum stage** in the
queue form to retain the earlier per-stage evaluation behavior. Stage
transitions remain controlled by **cumulative learner steps**, not
benchmark results. An optional periodic-evaluation benchmark target can
**end the entire run early**, but it does not skip curriculum stages or
use training metrics as qualification evidence. At each boundary
(including the last), the manager:

1. Coordinates the existing graceful distributed stop; training and worker
   rollout must be fully stopped before evaluation.
2. Requires a saved optimizer checkpoint and a **registered ONNX export at or
   beyond the last observed learner step**. It pins that candidate's exact
   file path, SHA-256 and registry training step once for the whole suite.
3. Runs each recipe against its own **frozen scripted or ONNX opponent**
   (unchanged across all candidates and stages), storing one immutable report
   per scenario. Every report records experiment ID, stage
   index/name, cumulative boundary step, selected candidate step/hash,
   recipe/build/evaluator identities and outcomes.
4. After all reports are verified, resumes the **same optimizer lineage** in
   the next stage, or marks the final stage completed and advances to the next
   experiment.

**Optional verified benchmark completion:** The Bees queue form can freeze a
minimum win rate, maximum timeout rate, minimum evaluated matches per
scenario (at least 30), and 1–4 required consecutive passing periodic
checkpoints. The feature is disabled by default and rejects nonperiodic
evaluation schedules or recipes with too few matches. Every development
scenario must meet both limits from its validated integer match outcomes.
Decisions are stored per checkpoint with frozen report file hashes,
candidate/build/evaluator/recipe identities are verified, and the
contributing successful reports are rechecked after a coordinator restart.
If a later milestone was skipped because the learner progressed too far,
consecutive passes refer to evaluations actually performed, not adjacent
milestone numbers. A conflicting external restart after target success
disarms the queue for manual inspection rather than silently progressing. When the required
consecutive evaluations pass, the manager completes the experiment **only
after the usual verified graceful checkpoint stop**, without restarting or
re-stopping the learner, and can proceed to the next independently queued
experiment. A failed scenario resets the consecutive count. Evaluation
errors disarm the queue with training stopped. The separately reserved
held-out benchmark suite is never used for stopping decisions. Repeated
screening against fixed development seeds is susceptible to selection
bias; passing this target is a development screening result, **not**
independent held-out validation or evidence of statistical superiority.

The manager writes each completed scenario's report filename to the experiment
record. If interrupted after a report was atomically written but before that
filename was recorded, it re-links the matching immutable report by a stable
evaluation identity. It never substitutes a later or older ONNX candidate
mid-suite. A failed evaluation **disarms the queue and leaves training stopped**;
the manager records the error and retains successful reports. After inspecting
the stopped run, **Run queue** retries the remaining work. An ambiguous
external restart or missing checkpoint does not silently advance the stage.

The **Checkpoint benchmarks** panel can filter by scenario and curriculum
stage. Only compare rows from identical recipe, frozen opponent, Unity build,
evaluator, and **same stage index**. The training telemetry curve is
observational, not an independent fixed-seed evaluation. This suite does not
automatically establish causal significance from one training seed per setting.
A stage suite may take substantial real time; do not assume the next training
stage starts immediately after the step boundary.

A legacy single `recipe` in `benchmark-settings.json` remains supported and
works as a one-scenario suite. **Final checkpoint only** runs its first
configured recipe, preserving the previous final-evaluation behavior.
A queued item may use either stage-suite mode or final-only mode, not both.
Automatic stage evaluation requires the versioned scripted policy or a
frozen ONNX opponent, plus a registered candidate export. There is no
requirement to train an opponent network first.

## Fixed-seed checkpoint evaluation (first independent benchmark)

After a training run has exported an ONNX policy, select that file and an
**unchanged** reference ONNX policy. In a Python environment with the existing
Bees ML-Agents/ONNX evaluation dependencies, run:

```sh
python Assets/Training/bees_experiment_benchmark.py --benchmark Assets/Training/experiment_benchmark.example.json --candidate path/to/candidate.onnx --opponent path/to/reference.onnx --env path/to/UnityEvaluator.exe --build-id EXACT_BUILD_ID --run-id OPTIONAL_TRAINING_RUN_ID
```

This is an offline evaluation and does not start or modify training. It uses
the existing `bees_continual_evaluate.run_match_group` evaluator and deterministic
ONNX actions. The `--opponent` flag also accepts `scripted:stationary`,
`scripted:strafe` or `scripted:pursuit`; both modes use authoritative
Unity match outcomes. The versioned JSON recipe sets the seed, match count, environment
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
Evaluation does not change trainer settings.

## Optional research-grade comparisons

Ordinary experiments remain **one run, one seed**, with no research-replicate
overhead. Check **Queue fresh independent-seed runs** to prepare 2–32 sequential
fresh runs of the same frozen curriculum, settings and release with consecutive
ML-Agents initialization seeds. All runs are linked by a study ID and execute
through the ordinary queue, so no separate trainer service is introduced.
Normally the Unity scenario root is independently randomized. Optionally
set **Scenario root seed** when queuing an experiment to pin the process-root
scenario RNG stream across runs; it is recorded in the frozen stage arguments
as `--rl-scenario-seed=<int>`. This requires a Unity build containing the new
flag. Without a value, new Unity processes retain a fresh random seed.
The Unity process combines a specified seed with its machine identity
and ML-Agents worker port before deriving per-arena streams, so multiple
parallel workers do not replay identical scenarios. Hold the machine/worker
topology fixed for comparable controlled-seed studies; a changed host or port
changes its scenario stream. Launches without a worker port fall back to a
non-reproducible process identity and report a warning. Asynchronous
distributed scheduling and external samplers still prevent bit-for-bit
deterministic training; matching seeds alone are not proof of identical
experiences.

Each independent benchmark saves its existing immutable summary report **plus
a compact per-match CSV** containing the authoritative Unity side-channel
outcome, episode number/duration, shots, hits, damage and starting/final team
strength. The report records the CSV filename, SHA-256 and row count.
The benchmark seed initializes a scenario *stream*; per-match independent
seeds and initial coordinates are not emitted and must not be inferred.

Open **Research comparisons** to compare only training runs with the same
study ID, effective curriculum, training configuration except seed, release,
checkpoint target, Unity/evaluator build, recipe and opponent. The table shows
win/timeout means, between-seed SD and (for at least two independent runs)
approximate Student-t intervals **across trained networks**, not across
correlated matches. One-run results have no between-seed interval. The summary
is calculated on demand from immutable reports. **Save research CSV** exports
the analysis-ready table and its provenance keys. **Between-configuration
comparisons** use an approximate Welch interval for the difference in win
rates when both conditions have at least two independent trained networks;
only matching curriculum, benchmark, release and execution settings can be
compared. **Sample efficiency** reports the earliest *evaluated checkpoint*
where each model's win rate meets a configurable threshold; models not
observed meeting it remain censored. These are descriptive analyses and
do not replace a preregistered hypothesis or held-out confirmation.

A separately configured
\`Training/Experiments/heldout-benchmark-settings.json\` can be **reserved**
when queuing an experiment. Use the same schema as
\`Training/Experiments/benchmark-settings.json\`, but with distinct benchmark
recipes/seeds. The pinned suite never runs during development; after choosing
the final configuration, use **Run held-out validation** on its finished run.
The final evaluation is immutable, clearly tagged \`heldout\`, and separate
from the development curves. Do not inspect and optimize against these final
results repeatedly, or they cease to be genuinely held out.

Use **Export study evidence ZIP** (or the single-run equivalent) for a compact
snapshot of the frozen experiment definitions, full preserved JSONL metrics,
YAML configuration snapshots, available hashed benchmark source/recipes,
result reports and individual-match CSVs. The archive manifest records file hashes.
The ZIP intentionally excludes raw training trajectories and large ONNX/Unity
binaries; it is **not a self-contained executable replication environment**.
Preserve the separately identified model weights, Unity build, runtime and
relevant Git revision when preparing a research release. Older reports may
lack match-level CSVs because they were written before this enhancement.

## Independent trainer configurations

Expand **Trainer configuration** when queuing an experiment. Defaults come from
the current immutable release runtime. Beta, lambda, learning rate, batch/buffer
size, epoch count, network depth, normalization and ML-Agents seed
are explicit. Each experiment starts a fresh policy and optimizer; checkpoint
warm starts are not supported by this form. Ordinary Unity training generates
an independent process-root scenario seed, so the ML-Agents seed alone cannot
reproduce identical training encounters. It also does not replace the separate
historical-opponent sampler's existing seed.

A content-addressed YAML snapshot is stored once under
`Training/Experiments/Configurations`. The item records its hash, resolved
settings, baseline hash, runtime version and operator execution settings.
Launch verifies the snapshot and release, then passes that exact file through
the existing central learner launcher. The coordinator compares the generated
run-scoped YAML settings with the snapshot; a mismatch triggers a safe stop
and disarms the queue. Run details show the verified generated configurations.
The complete YAML includes fixed reward, self-play and scheduling settings too.

The v26 structured policy requires feedforward width 128. Layer count and
observation normalization can vary in a fresh run, with network settings included
in its checkpoint compatibility key. Arbitrary width or recurrent architecture
changes require a separate versioned policy implementation and are rejected here.
Illustrative settings are not recommendations; short validation runs do not
establish which PPO configuration learns best.

### Rebuild Unity RL players without creating a new experiment

Pause the active experiment and wait for **Paused**. Select **Rebuild Unity
(experimental)**. The dashboard warns that changes in simulation physics,
weapon behavior, rewards or timing can invalidate before/after comparisons,
even if the agent's observation/action interface has not changed. It records
the event as a solid **U** line on the graphs and in the run details.

This operation builds new Windows and Linux Unity RL players and validates
the current stage's environment arguments. It preserves the current run ID,
optimizer checkpoint, curriculum stage, frozen trainer configuration and
learner Python runtime. Only an unchanged RL compatibility fingerprint is
accepted. A different training contract is rejected rather than starting
a new network. A successful build is published through the existing
compatible release rollout; the manager updates the pinned Unity build
identity only after BeesServer confirms canonical activation. Select
**Resume training** to continue that same experiment.

A failed compilation before publication leaves the old build canonical.
If publication becomes incomplete or ownership cannot be proven, the
experiment stays paused instead of trying an unsafe resume. An unchanged
compatibility fingerprint establishes interface compatibility, **not**
scientific comparability; interpret results before/after **U** as separate
conditions until validated.

### Update remote worker software without ending an experiment

From the Experiment Manager, choose **Pause for maintenance** and wait for
**Paused**. Select **Update worker software**, then **Resume training**.
The dashboard does not create a new run, advance the curriculum, rebuild
Unity, or replace the frozen optimizer/checkpoint/trainer configuration.

This action builds an authenticated worker-only package from the **currently
pinned learner release**, substituting only
`bees_managed_remote_worker.py` and `bees_training_worker_agent.py`.
The regular remote bootstrap updater distributes it to all connected remote
supervisors. The central learner continues using its original pinned runtime;
ordinary same-run starts preserve the worker-only package selection. Workers
may adopt the new runtime during the pause or on reconnect. After resuming,
check Worker Status for the live-resize capability flag; a worker still
missing it may require examining its bootstrap/connection health.

This deliberately updates the remote worker supervisor/agent only. Changes
to Unity, the model, WAN transport protocol, trainer configuration, or the
central learner are **not** silently distributed this way. A failed
publication leaves the experiment paused; do not use New Run to recover.

## Dashboard use and worker controls

The experiment dashboard automatically selects recent runs with saved learner
samples. The chart supports raw values, rolling means of the last 5/15/30
recorded samples, hover readouts (raw/rolling/running mean), and horizontal
axes of learner steps or observed training hours. Select additional runs
using their **Plot** checkboxes. A **Download logs ZIP** button on each
started experiment exports the newest **1%, 5%, 10%, 25%, 50%, or 100%**
of each available run-scoped uploaded worker or learner log. The default is
5%; 100% exports all available lines. The manifest records each source's
original and exported byte counts. This does not alter source logs, interrupt
training, include checkpoints or shared multi-run logs, or retrieve remote
bytes not yet uploaded.

**Mean Group Reward** is the default reward
curve, parsed from ML-Agents learner summaries; individual Mean Reward remains
selectable. The PNG button exports the displayed graph and its legend without
sending data to a separate service. Charts use a zero baseline for nonnegative
metrics; signed group rewards retain negative values rather than clipping them.

During a stage evaluation the learner is intentionally stopped, so no
training-reward/throughput samples exist during that interval. The coordinator
captures the last actual pre-stop sample and first fresh post-resume summary.
Only the **actual event** is marked on the chart: **E** for checkpoint
evaluation or **T** for a curriculum transition without evaluation. Hovering
shows the configured threshold and actual reached step. The time axis counts
observed training time, not benchmark downtime. Transition detection polls
every ten seconds and uses the most recent reported learner step (reported in
50,000-step training summaries), so it cannot promise an exact-step stop.
The manager retains up to 20,000 original minute samples **in its live
database** without repeatedly halving old data; an append-only JSONL
archive retains all newly recorded samples beyond that window. Historical
samples already discarded before archiving cannot be reconstructed.
Old sparse points are connected rather than artificially broken based on
an arbitrary one-million-step spacing rule.
Historical samples only contain fields collected at that time; new group
rewards, PPO timing and Human combat values are **not backfilled** when
upgrading the manager. Restart the experiment coordinator with the new source
to begin recording newly added fields.

**Checkpoint evaluations** default to the active beta run's latest
evaluated checkpoint, with three readable results: **Stationary aiming**
(24×24, 5s), **Moving combat** (32–64, 15s), and **Long-range pursuit**
(96–128, 30s). Each test plays 100 independent fixed-seed Unity matches
against its unchanged scripted opponent. The **Whose results?** selector
opens any saved experiment, a beta-run comparison, or the full archive
including earlier qualification and legacy tests. **Which checkpoint?**
selects a particular stage checkpoint or the full history; **Which test?**
narrows the three scenarios. The optional comparison table shows results
side by side; full hashes, seeds, and build details are available only
under **Technical details**. There are three tests **per checkpoint**, not
three lifetime reports; no old reports are deleted or re-evaluated by the
dashboard. Compare wins, losses, timeouts, and hits/shots against the same
recipe, opponent, build and stage; hit accuracy alone does not establish
winning combat ability. The 95% Wilson intervals in the detailed view
summarize uncertainty within the 100-match sample, not variation across
independently trained networks.

On the training-speed graphs (learner steps/s, PPO utilization/timings,
and worker pipeline metrics), small vertical markers identify observed changes
to each remote worker's active environment count. Hover a marker for the worker
and before/after count; markers follow either learner steps or observed training
hours. Changes are sampled from the existing worker status while the manager is
running (at each manager cycle, and more frequently while Worker Status is
open), so changes made between observations cannot be reconstructed. The first
observed worker count is a baseline, not a change. Older manual capacity
requests may appear as approximate, explicitly unconfirmed historical markers.

The Worker Status table shows each remote worker's cumulative accepted and
consumed experiences reported by its current process; those raw counters
can reset on restart. The +/- controls set a **persistent** remote-worker
environment target while its authenticated worker supervisor is online.
The remote must advertise support from the current runtime and an admissible
min/max capacity. Disabled +/- controls now show the specific reason inline
(stale/offline worker, unsupported deployed runtime, missing safe limits,
or release rollout). Updating only the dashboard does not add that
capability to an older pinned worker runtime.
Clicking +/- explicitly requests a one-environment change relative
to the observed worker count (or a pending explicit override), without
a blocking confirmation popup. The row distinguishes request acceptance from
the worker's reported application; the control server still requires the
explicit active-training authorization sent by this click. Each accepted
adjustment records a capacity-change marker on the active experiment: compare
throughput before and after separately. The remote supervisor uses the
existing live Unity resize file for fixed-count as well as automatically tuned
workers, so a healthy live adjustment does not restart that worker's actor,
other workers, or the learner. Deployed older supervisors still require a
compatible worker-runtime rollout; updating the dashboard alone cannot
retrofit their behavior. The **Auto** (automatically tuned workers) or
**Reset** (locally configured manual workers) button clears the dashboard
override and restores the worker's previous tuning or fixed target.
A fixed UI target pauses the automatic optimizer for that worker. Changes
made while training is stopped persist in authenticated training control
and take effect when that worker next runs; offline/unsupported workers
cannot accept changes. The control server, experiment dashboard, and
remote worker runtime must all be deployed together before using these
controls.

## Quality and training-efficiency comparisons

The experiment manager can compare PPO epoch counts or any other supported
trainer configuration on two independent axes: **learner steps** and **observed
training hours**. Choose a graph metric (timeout, hit rate, win rate, reward,
learner throughput, PPO update seconds, minibatches/s, optimizer busy %, or
worker pipeline rate) and change **Horizontal axis** between steps and training
hours. Fixed-seed stage evaluations remain the primary quality checks; the
benchmark stage matrix now also shows training hours and effective steps/s
at each checkpoint.

Expand **Training efficiency** for stage-by-stage rates, epochs, mean completed
PPO update duration, optimizer minibatches/s, PPO busy share, worker
production/admission/consumption, broker occupancy and upload queues. Its
details show available PPO preparation, backward and optimizer-step timings.
The coordinator reuses the existing status/worker/PPO telemetry and saves only
bounded minute-resolution samples plus small per-stage aggregates; it does
not duplicate learner trajectories, record raw profiles, or enable diagnostics.

**Effective steps/s** = observed increase in authoritative learner steps /
observed active-training wall time, not worker OptStep/s (the environment
auto-tuner probe), nor GPU gradient updates/s. Time is accumulated only while
the manager observes training enabled with the experiment's run ID and active
stage; planned evaluation/checkpoint stops are excluded. The first interval
after startup/resume and intervals when the manager is unavailable are
unobserved, so this is not exact end-to-end elapsed time. PPO busy share uses
the central learner's cumulative optimizer-busy counter, tolerates completed
updates that span status polls, and rebases on learner restarts. Unavailable
metrics remain blank, not zero. CPU/GPU and detailed PPO timing readings
require the existing PPO diagnostics option; enabling it can change throughput,
so keep it **consistent across all compared configurations**.

For an epochs comparison, queue independent runs with identical Unity release,
training curriculum, beta, seeds, runtime flags, worker topology and hardware,
changing only `num_epoch` (for example **1, 3 and 6**). The v26 base PPO
buffer is 65,536 steps and batch size is 4,096: these settings nominally give
16, 48 and 96 actor **and** critic minibatches per PPO update, respectively.
Compare fixed-seed benchmark outcomes at equal learner steps and at the same
observed training hours. A queue/upstream backlog or high PPO busy fraction is
a **pressure signal**, not proof of the limiting system component; confirm
from sustained worker/broker trends and PPO timing. Cross-host throughput
comparisons (such as Exeter versus Halifax) are not controlled epochs experiments.

## Combat screening suite

Separate `experiment_benchmark.short.json`, `.medium.json` and `.long.json`
recipes preserve the original example and its reports. Each requests 100 matches,
seed 10031, Wasp/Gunship, quarter health on both sides, decision period 5, and
no static obstacles or mining asteroids. Map ranges and episode timeouts are:

| Scenario | Map range | Timeout |
| --- | --- | --- |
| Short aiming | 32–40 | 60 seconds |
| Medium combat | 64–80 | 90 seconds |
| Long engagement | 96–112 | 120 seconds |

Use every recipe with the same frozen opponent for every candidate. The same
seed initializes the scenario streams; all matches in a report use their
deterministic sequence. Faction roles are fixed, so compare candidate networks
in the same role and use the frozen network's self-comparison as a control.
These scenarios measure a narrow matchup, not general combat strength.

Simulation speed is explicitly fixed at 20 in these recipes and recorded in
reports. Changing speed can change Unity outcomes: do not pool speeds.
The evaluator reapplies it after arena resets because gameplay setup restores
the stage's time scale. Owned Unity processes terminate with their evaluator,
including after an abrupt coordinator restart.

The web table shows wins, timeouts, hit/shot counts, damage and mean episode
duration. Win and timeout ranges are 95% Wilson intervals conditional on this
screening sample; fixed seeds, correlated episodes and one training seed limit
broader statistical conclusions. Shot accuracy is descriptive, without treating
correlated shots as independent trials. Early five-match probes produced close
range damage but no medium/long hits; calibration remains provisional until
larger screening results demonstrate useful distinctions.
