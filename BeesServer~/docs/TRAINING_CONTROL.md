# Distributed training control

## Unified operator commands

The unified project layout is:

- Unity project root: `B:\\Bees`
- Git repository / Unity Assets folder: `B:\\Bees\\Assets`
- Builds: `B:\\Bees\\Builds`
- Durable training state/checkpoints: `B:\\Bees\\Training`
- Machine-local runtime/secrets: `B:\\Bees\\Runtime`, `B:\\Bees\\Secrets`

Day-to-day operation is through `Assets\\bees.ps1`:

```powershell
cd B:\Bees

.\Assets\bees.ps1 server
.\Assets\bees.ps1 build
.\Assets\bees.ps1 start
.\Assets\bees.ps1 stop
.\Assets\bees.ps1 status
.\Assets\bees.ps1 bundle -LogPercent 10
```

`server` starts or refreshes BeesServer in test mode on TCP `7146`. The Unity Editor connects to this endpoint at `seagrams7.softether.net:7146`, and test mode does not require Steam authentication. No Unity build is required.

`build` always creates Windows and Linux RL builds. Add `-FullGame` to also create the managed Windows gameplay build. Build folders remain outside both Git and Unity import, for example:

```text
B:\Bees\Builds\2026-09-24 RL Windows
B:\Bees\Builds\2026-09-24 RL Linux
B:\Bees\Builds\2026-09-24 Full Game Windows
```

Rebuilding the same type on the same day requires `-Force`.

`start` always starts/keeps the test-mode BeesServer and training-control plane. Before the first RL build exists, it remains idle with zero managed trainers so the Unity Editor can connect immediately. Once a compiled release exists, `start` also brings up the private tailnet gateway, central managed learner, publishes the release, and enables distributed training. `stop` disables training everywhere but leaves BeesServer available for gameplay; `stop -Server` also stops the managed BeesServer and tailnet gateway. `status` is a live dashboard; `status -Once` prints one snapshot.

The authoritative non-secret cluster configuration is `Assets\\Training\\bees.cluster.json`. Machine-specific credentials remain under `B:\\Bees\\Secrets` and are never checked in.

### Diagnostic upload bundle

`bundle` creates one upload-ready ZIP under `B:\\Bees\\Diagnostics` for the current training run. It is intended for external diagnosis of training health, speed, ship behavior, and policy learning without manually collecting files from each trainer.

For the active run, `bundle` first asks the live ML-Agents trainer thread for an immediate ONNX export of the current in-memory policy. This does not stop/restart training and does not create an extra optimizer checkpoint or alter the normal checkpoint schedule. The temporary diagnostic ONNX is deleted after the ZIP owns its copy. If the live export cannot complete, collection continues with the newest retained ONNX and records the snapshot failure/model lag in the manifest.

By default the bundle includes that current/fallback ONNX, the newest 10% of each text log with a 128 KiB minimum tail per text file, complete small JSON log metadata, current BeesServer/trainer status, cluster and PPO configuration, run/continual-service state, ML-Agents timer/training-status JSON, and a combined text log. Small error logs are therefore retained in full instead of losing the beginning of the exception. Remote trainer logs are read from the learner-owned `B:\\Bees\\Training\\TrainerLogs\\<run-id>` mirror populated by the existing verified log-upload protocol; the bundle command does not SSH into trainer machines. Managed remote supervisors also mirror their own console plus child worker/WAN output into run-scoped `remote-supervisor.log`, which is uploaded through the same path.

For the active run, `bundle` also runs a best-effort deterministic benchmark against the live snapshot. It reuses the authoritative frozen-ONNX evaluator, loads the same current model on both teams, and runs 20 fixed Wasp-versus-Gunship 1v1 matches (map 32, 30-second timeout, 5% health) with deterministic continuous and discrete ONNX outputs. This is an inference-only side process: it does not touch PPO, optimizer/checkpoint state, the running training environments, or the policy ABI. Its outcome and rolling turret-aim telemetry are stored in `status/deterministic-benchmark.json` and in `manifest.json`. The benchmark is capped at 180 seconds; an unavailable build/model, timeout, or evaluator failure is recorded as skipped/failed and never prevents the diagnostic ZIP.

Use a different percentage when needed:

```powershell
.\Assets\bees.ps1 bundle -LogPercent 25
```

To package a retained older run explicitly:

```powershell
.\Assets\bees.ps1 bundle -RunId bees-v19-r3-s1-... -LogPercent 10
```

The ZIP contains `manifest.json` with the selected run, source paths, byte ranges, hashes, learner/model steps, model lag, deterministic-benchmark result, per-trainer uploaded-log freshness, structured diagnostics, and warnings. It automatically flags stale trainers, reported trainer errors, build/revision mismatches, missing expected trainers, missing/stale uploaded logs, failed live snapshots, and materially stale model exports. A missing ONNX, unavailable live status, or failed deterministic benchmark is recorded rather than preventing collection, so the command remains useful for diagnosing failed or newly started runs. When `-RunId` targets a historical run, current live learner steps are not mixed into that run's model-lag calculation and no current-policy benchmark is mislabeled as historical evidence.

Environment/scenario arguments may be supplied for one start with repeated `-EnvArg` values. Before any active trainer is changed, the operator extracts the exact packaged Windows RL artifact that will be distributed and runs that extracted build in validation-only mode so the same `RlOneVsOneTrainingOptions` parser used by training accepts the option names, values, roster constraints, and combinations. Only after that process exits successfully does the operator sign an attestation over the exact build artifact SHA-256 plus the exact ordered argument list with a dedicated local validation secret; BeesServer independently verifies that HMAC before it will create an environment-bearing rollout. Public build metadata and an admin token are therefore insufficient to fabricate a validation result. Invalid arguments fail before run-plan or control-state mutation. Once a canonical release exists, environment arguments are rollout-owned rather than mutable through the generic desired-state API; the low-level Node CLI refuses unvalidated environment changes and directs operators to `bees.ps1`. For example, a fresh 1v1 Wasp-versus-Gunship run on a 32-unit map with a 30-second timeout is:

```powershell
.\Assets\bees.ps1 start -NewRun -EnvArg @(
    "--rl-ships-per-side=1",
    "--rl-bee-ship-types=Wasp",
    "--rl-human-ship-types=Gunship",
    "--rl-map-size=32",
    "--rl-episode-timeout=30",
    "--rl-matchup-mode=fixed"
)
```

Omit `-NewRun` to apply the arguments while resuming the existing run. A forced new run reuses the current compiled build, archives the outgoing run before and after the coordinated stop, preserves its checkpoints, and starts the new run with a separate run id/checkpoint namespace.

Forced-new execution is a durable resumable operation rather than a shell-lifetime sequence. Before the latest release is rewritten, the operator persists the target run id, exact existing build id, compatibility identity, previous run identity, and exact environment-argument list in the run plan. That plan remains after the lifecycle commit and is cleared only after the incompatible rollout has promoted the target run and the outgoing run's terminal archive has succeeded. If the shell, BeesServer, or operator command is interrupted anywhere in between, a later ordinary `start` or repeated `start -NewRun` resumes that same target instead of allocating another run. A build refuses to overwrite an unfinished forced-new plan and directs the operator to resume it first. Pre-existing forced-new plans from before build/environment binding are recovered once using the current release/server-owned desired state and then cleared after terminal archival.

## Builds, run identity, and compatibility

Every compiled training release contains:

- immutable build id
- source Git commit
- run id
- compatibility key
- compatibility contract
- Windows/Linux dedicated artifacts and optional full-game artifact
- one immutable, content-addressed training-runtime archive containing the Python training/control runtime, pinned trainer/continual configuration, and pinned learner/remote requirements used by that release

The training-runtime archive is part of the release identity rather than a snapshot of whatever happens to be in the working tree when a trainer later restarts. It is pinned before Unity compilation begins, so edits made during a long Unity build cannot silently replace the Python/config runtime that will be paired with those artifacts. Its manifest records the build id, source commit, runtime content identity, exact file sizes/hashes, archive SHA-256, and required runtime files. After Unity compilation completes, the operator recomputes the semantic RL compatibility fingerprint and refuses to publish if it differs from the original run plan; this prevents a mid-build reward/observation/action/schema edit from being paired with the wrong run lineage. The operator verifies the runtime identity again before installation. Central and remote trainers therefore use the same release-owned runtime bytes during a rollout even if development continues in the working tree while or after the Unity build runs.

The central learner installs the verified runtime into an immutable versioned directory and launches its worker/service/subprocess scripts from that directory while still using the real Bees Unity project for Unity/game assets. Its Python dependencies are also isolated by the pinned release requirements hash, so installing dependencies for a newer release cannot mutate the environment required to roll back or resume an older release. A legacy release created before this contract may be pinned once from the current runtime solely to recover the in-progress deployment; subsequent builds always pin the runtime at build time.

Run identity is calculated automatically by `Training/bees_run_lifecycle.py`. A compatible build keeps the existing run id and optimizer/checkpoint lineage. An incompatible training contract creates a new run id automatically. Ordinary `start` also resumes the current run by default. Use `start -NewRun` only when a fresh optimizer/checkpoint lineage is explicitly desired without rebuilding the Unity executable.

The compatibility fingerprint currently includes the continual-learning behavior/schema identity, frozen policy signature, network architecture settings, reward implementation, episode coordinator/reward dispatch implementation, policy-schema implementation, combat perception, action implementation, exploration-grid implementation, and episode ship identity. This makes policy/reward/observation/action changes fail safe even if a developer forgets to increment a manual schema version. Ordinary non-architectural PPO tuning and other compatible operational changes do not by themselves force a new run.

Durable continual-service phase state is run-scoped. Checkpoints/results remain under the run id in `B:\\Bees\\Training`; a build never deletes the outgoing run's optimizer state, checkpoints, telemetry, or other durable training data.

`generationSteps` remains the number of additional learner steps per continual generation. It is an evaluation/release cadence, not a reset interval.

## Automatic log preservation

Before every new build starts compiling, the currently active run is snapshotted into:

```text
B:\Bees\Assets\TrainingHistory~\runs\<run-id>\
```

The trailing `~` keeps the history outside Unity import while allowing Git to track it. Training logs are split into 48 MiB parts so individual tracked files remain below GitHub's per-file hard limit. Each snapshot contains a manifest with hashes and captured byte lengths.

The archive command then creates a Git commit containing only that run-history path and pushes the current branch to `origin`. Push uses a bounded retry/backoff budget so a transient network failure does not immediately abort an otherwise healthy build. The safety rule remains fail-closed: if the Git commit fails or the archive still cannot be pushed after the retry budget, `build` stops before compilation rather than silently proceeding with unprotected logs.

During an incompatible cutover, dedicated trainers stop only after the replacement is fully staged. The central learner first requests a graceful ML-Agents interruption and remains leased as `stopping` while ML-Agents writes its final checkpoint, ONNX export, `timers.json`, and training status. Each trainer fully flushes its outgoing run-scoped logs to the learner before reporting `stopped`. After the new run is promoted, the old run is snapshotted again so the terminal log tail is committed as well. If central checkpoint finalization does not complete, the cutover fails closed: the old learner remains authoritative and is not force-killed.

Checkpoint/model data is intentionally not copied into GitHub. It remains in the durable `B:\\Bees\\Training` run directories, which are not removed by build or rollout.

## Seamless release rollout

Publishing a build and activating it are separate operations.

For a compatible release or same-run static environment-argument change:

1. The old release/configuration keeps training until its assigned cutover. Environment-only changes use the same barrier without changing build/run compatibility identity.
2. The server snapshots the currently active/recently leased dedicated trainers into a rollout barrier. After any explicit restart-recollection window closes, that compatible membership is frozen and can only shrink.
3. Every trainer in that snapshot downloads and verifies the new Unity build and release-owned training runtime in the background.
4. Remote supervisors also fetch worker/WAN credentials, release metadata, and the embedded tailnet helper over the existing private bootstrap channel.
5. A trainer reports the release prepared only when its Unity artifact and required remote runtime are ready.
6. Once all still-required trainers are prepared, the server rolls dedicated trainers one at a time.
7. A required trainer whose dedicated control lease genuinely expires is removed from the compatible barrier. A late or returning trainer does not re-expand the in-flight barrier; it stays on the semantically compatible canonical build/configuration until promotion, then reconciles to the new canonical state.
8. For an environment-argument change, the central learner is cut over first. Every managed learner/actor process receives a SHA-256 identity of its exact ordered environment-argument list, and the WAN broker admits trajectories only when `run_id`, compatibility key, and environment identity all match. Old actors therefore cannot feed old-scenario trajectories into the newly configured learner while remotes restart one at a time.
9. Trainers already moved to the pending release/configuration stay there while the remaining trainers update.
10. For ordinary compatible code releases, remote trainers remain ordered before the central learner; environment-only transitions use the central-first rule above so the authoritative broker switches semantic environment identity before accepting newly configured actors.
11. Rollout completion is tracked as an explicit persisted acknowledgement for the trainer currently assigned the one-at-a-time rolling slot. Merely observing/echoing the shared control revision cannot mark a non-target trainer rolled; this is required for same-build environment changes because non-target trainers intentionally keep the old arguments while still seeing the new control revision.
12. A trainer counts as successfully rolled only after it heartbeats the exact pending build/hash as `running`, with no reported error, and with the rollout revision applied while it owns the rolling slot. After every remaining required dedicated trainer has provided that acknowledgement, the release becomes canonical.

For an incompatible release:

1. All active trainers continue the old run while the replacement is downloaded and verified.
2. When every active dedicated trainer is prepared, the server requests a coordinated stop.
3. Remote trainers terminate their managed Unity/process trees and flush outgoing run logs; the central learner remains heartbeating as `stopping` until ML-Agents has finalized the current optimizer checkpoint, ONNX model, timers, and status files.
4. Only after the central save is complete and all trainers report `stopped` does the server promote the new build, compatibility key, and run id.
5. Trainers restart under the new run. The old run's checkpoint/results tree remains intact.

This keeps update interruption limited to the actual process restart/cutover rather than download, extraction, dependency installation, or artifact verification. If training is disabled after a compatible release has already reached `rolling`, the release is already fully prepared on every required trainer; the control state promotes it immediately rather than waiting for trainers whose desired mode is now `stopped` to report `running` on the pending build. Incompatible `stopping` barriers do not use this shortcut. The central learner must always re-register and confirm its stopped/checkpointed state. A non-central dedicated remote whose control lease genuinely expires may be removed from the incompatible barrier because dedicated workers fail closed on the same lease and cannot remain an authoritative old-run trainer; if it reconnects before promotion it is re-added to the stop barrier, and if it reconnects afterward it reconciles directly to the new canonical run.

The release barrier is restart-durable. When a release is staged, the control state persists the required dedicated trainer identities/platforms and the rollout phase revision. Restarting BeesServer during `preparing`, `rolling`, or `stopping` does not treat an empty in-memory trainer map as success: recently leased trainers are reconstructed from the persisted registry rather than being silently forgotten. For a compatible rollout, a required dedicated trainer whose control lease has genuinely expired is removed from the barrier because dedicated workers already fail closed and stop their managed trainer when that lease expires; if that worker later reconnects, it reconciles to the then-current canonical release before training again. A non-central remote that stays online but continuously reports a release-specific preparation failure, or repeatedly fails its assigned compatible roll restart, receives a bounded recovery grace (by default two control leases, at least 60 seconds). If the same failure persists through that grace, only that remote is removed from the compatible barrier so healthy capacity can continue; the worker remains registered and keeps reconciling to the canonical release afterward. The failure timer is persisted across control-server restart and cleared immediately if the remote recovers. The central learner is never eligible for this compatible bypass because it owns the optimizer/checkpoint lineage. Incompatible run cutovers remain stricter about semantic ownership but not about dead machines: the central learner is never pruned and must re-register and confirm the coordinated checkpoint/stop, while a non-central remote whose dedicated control lease expires may be removed so a disconnected machine cannot deadlock a new run forever. A remote that returns before promotion is re-added to the incompatible stop barrier. The control state also retains recently active dedicated trainer identities so a server restart immediately before staging cannot collapse a live cluster to an empty barrier. Older schema-4 pending releases use one control-lease recollection window during migration rather than being promoted immediately from an empty registry.

The build command automatically stages a successful build into a control server that is already online. Before compiling another release, it first reconciles the previously persisted latest release: an in-flight rollout is allowed to finish, and a release that was saved before an interrupted publish/stage step is published and staged instead of being silently replaced. This prevents a second build from spending a full Unity compile only to collide with the previous pending barrier. Compatible rollouts therefore continue after `build` returns. For an incompatible release, `build` waits for the coordinated cutover and performs the final old-run log archive before returning.

## Managed BeesServer updates

The operator records a deterministic SHA-256 runtime identity for the managed BeesServer from the launcher plus the exact local JavaScript dependency graph used by the managed server process, including the transformed legacy `siServerDev.js` source, together with `package.json` and `package-lock.json`. A focused regression guard recursively follows local `require()` dependencies and requires that graph to match the operator's runtime allowlist. Runtime code or dependency edits therefore refresh the server, while test runners, test configuration, training-control CLI tooling, migration/recovery utilities, lint configuration, documentation, and engineering notes do not recycle a healthy control plane. This deliberately avoids disconnecting trainers merely because non-runtime repository material changed without allowing a newly introduced runtime dependency to escape restart identity.

Changed server runtimes are now prepared transactionally under `Runtime/ServerReleases/<source-hash>` before the live process is touched. The operator copies only the runtime allowlist into a candidate directory, installs that candidate's exact `package-lock.json` with `npm ci`, runs Node syntax checks over every runtime JavaScript file, loads the transformed legacy runtime as a dependency/startup preflight, and verifies that the copied bytes still match the source hash. A failed source/dependency/preflight update therefore leaves the currently healthy control server running. Prepared runtimes are immutable and content-addressed; an existing runtime is reverified rather than modified in place, and a runtime currently backing the live server is never deleted/replaced if verification fails.

Only after preparation succeeds do `server`, `start`, and live-cluster `build` stop a verified owned server and launch the prepared runtime. Managed state records the runtime directory, runtime/dependency identity, launch configuration identity, PID, process start time, executable, and whether the process is still `starting` or has reached authenticated-control `active` health. The previous ownership record is intentionally left in place while the old process is stopped; as soon as the replacement PID/start/executable identity is verified, the replacement state is atomically persisted *before* the health wait. If the operator shell dies in either half of that handoff, the next command can still prove process ownership: a dead old record is safely cleared, a live `starting` replacement is safely reconciled, and a replacement that became healthy after the shell died is promoted to `active` rather than treated as an unmanaged server.

If a source-only replacement still fails to become authenticated-control healthy after cutover, and the previous launch configuration is unchanged, the operator automatically relaunches the previous verified immutable runtime and records the rollback reason instead of leaving the control plane down. Configuration-changing replacements remain fail-closed because the operator does not guess old credentials/ports. Cached server runtimes are revalidated by source hash and by actually loading the server/legacy dependency graph before reuse, so a damaged `node_modules` tree cannot become a permanent retry loop. The active runtime is never replaced in place. The active runtime plus the newest rollback candidates are retained; older inactive snapshots and crash-left candidate directories are pruned on a bounded basis so rollback safety does not cause unbounded disk growth. Persisted desired training state, artifact catalog, dedicated-trainer registry, and active rollout barrier remain outside these runtime directories and therefore survive cutover, rollback, operator-shell interruption, or runtime pruning.

If the control port is occupied by a server that was not launched/recorded by the Bees operator, the script refuses to kill it automatically. Stop that unmanaged server once and rerun the command; subsequent source refreshes can then be automatic.

The managed BeesServer state also records a hash of its effective launch configuration: control URL/host/port, gameplay port, hashed worker/admin tokens, the hashed environment-validation attestation secret, durable control/artifact/log paths, and the inherited database/test-mode environment inputs used by the managed launcher. Startup reconciles persisted process ownership before probing the desired control endpoint. Therefore a rotated token, changed control port, or temporarily unhealthy old endpoint can safely replace a server whose PID/start-time/executable identity is still provably owned by the operator instead of leaving that process orphaned or reporting an unowned-port error. If the target endpoint/port belongs to a process that cannot be proven to be the recorded managed BeesServer, startup still fails closed and never kills it.

Managed-process ownership is never inferred from PID existence alone. BeesServer, the central learner, and the embedded tailnet gateway persist the process PID, exact process start time, and executable path. The central supervisor and gateway additionally persist a unique launch-intent token atomically before process creation; if the operator shell dies between launch and final PID/start-time persistence, the next invocation locates exactly one process with the expected executable and token, reconstructs its full identity, and atomically promotes the state to active before any authentication, checkpoint, archive, or replacement decision. Automatic restart/stop first proves that the persisted identity still matches the live process. Ownership is intentionally separate from desired executable/configuration identity: once the persisted identity proves the process is ours, a legitimate Node path, release-specific Python path, tailnet helper path, token, port, or command/config change may safely replace that owned process. A stale PID that has been reused by another process still fails closed and is never killed. Legacy PID-only records are also fail-closed: if their recorded PID is still live, the operator refuses to terminate it automatically and requires that one legacy process to be stopped once before it is relaunched under identity-safe state.

## Private remote workers

`start` prepares one copy-and-run launcher per remote platform:

```text
B:\Bees\Remote\bees-remote-worker.cmd
B:\Bees\Remote\bees-remote-worker.sh
```

Each launcher is self-extracting and contains its platform bootstrap plus embedded tailnet helper. The learner publishes one complete `bees-bootstrap-bundle.zip` for private bootstrap delivery; it is not copied manually to remote machines. A standalone `bees-remote-runtime.zip` mirror is retained only for migration from pre-bundle gateways and is not part of the steady-state gateway interface.

The private training transport uses the embedded Tailscale userspace library. No separate Tailscale installation, VPN driver, SSH server/client, Windows training account, SSH key, password, SCP step, or additional router port forwarding is required for control, WAN rollouts, or bootstrap. Remote Unity players still use the normal public Bees gameplay/settings endpoint, just like other game clients.

On first use, the learner and each remote worker print a Tailscale authorization URL. Their identities are persisted. The generated launcher contains the learner's private tailnet address and bootstrap credential.

The learner gateway exposes only these private tailnet services:

- control, normally 7150
- WAN rollout broker, normally 55051
- bootstrap service, normally 7151

The gateway process itself is reconciled idempotently. Re-running `start`, performing a live-cluster `build`, or atomically replacing the complete bootstrap bundle does not restart a healthy gateway. Both `start` and live-cluster `build` nevertheless run the reconciler every time, so a crashed/missing gateway is recreated even when no helper/configuration version changed. A healthy gateway is restarted only when process-level configuration changes, such as its executable/helper identity, hostname, ports, bootstrap-bundle path, or bootstrap credential identity. This keeps existing remote tailnet sessions alive across ordinary build/start operations while still making those operations self-healing. When the persisted gateway process is already live, `start` also reuses that authenticated tsnet identity instead of starting a second one-shot `auth` process against the same state directory.

The bootstrap endpoint requires its own bearer token. It serves one immutable outer ZIP snapshot containing the current remote Python runtime, worker token, WAN token, release metadata, and versioned Windows/Linux tailnet helper. Remote preparation is candidate-first: the next outer ZIP and both copy-and-run launchers are generated and validated away from their public paths; the launchers are atomically installed first and the complete outer ZIP is atomically promoted last. A preparation/validation failure therefore leaves the previous usable launcher/bundle set intact, and one request cannot observe a new release file paired with an old runtime or helper. The gateway then copies that complete publication file to a private per-request snapshot before sending it, allowing the next generation to be published on Windows without waiting for a slow WAN download. It never serves the admin token.

After a worker is bootstrapped with the current launcher, future runtime/helper releases are fetched and staged automatically. `bees-remote-runtime.zip` is the exact training-runtime archive pinned into the pending/canonical release; `start` does not rebuild it from the current working tree. Before staging it, a remote independently verifies both the downloaded ZIP SHA-256 and the runtime content version against the release metadata, then verifies the archive's own version marker before activation. A mismatched or corrupted runtime therefore never reaches the worker-token/runtime cutover path. Tailnet helper binaries are published from immutable source-hash version directories rather than by overwriting a live executable. When helper source changes, the learner gateway is restarted onto the new immutable version after the updated bootstrap payload is prepared. The supervisor switches remote runtime/helper releases only at that trainer's assigned build cutover.

The launcher starts the long-running supervisor as a detached background process after bootstrap completes. Closing the terminal or logging out does not intentionally stop training. Re-running the start command is idempotent while that supervisor is still running. Stop it through the same launcher so the supervisor follows its managed cleanup path. Planned supervisor/runtime restarts now request a graceful worker-agent shutdown first; the worker agent in turn requests the WAN actor to stop, and the actor closes its ML-Agents environment manager/Unity workers before exiting. The worker agent gives a remote actor up to 20 seconds to exit cleanly and the supervisor gives the worker agent up to 30 seconds; a hung non-checkpoint-owning remote still falls back to the existing force-kill path so rollout cannot block indefinitely:

```cmd
bees-remote-worker.cmd
bees-remote-worker.cmd stop
```

```bash
bash bees-remote-worker.sh
bash bees-remote-worker.sh stop
```

The launcher records the supervisor PID under the worker install root only for local running/stopped detection; the stop command does not blindly kill that PID. It writes a local shutdown request, waits up to 45 seconds for the supervisor to stop its managed worker and private transport, and fails without force-killing if cleanup does not complete. Bootstrap/supervisor console output is redirected to local worker logs while the existing run-scoped supervisor log continues to be mirrored through the training-log upload path.

Machines using an older launcher need one final copy of the newly generated launcher to gain background start/stop behavior. After that transition, routine runtime/helper releases remain self-updating and do not require recopying the launcher.

Environment count is automatically optimized per remote machine by default:

```cmd
bees-remote-worker.cmd
bees-remote-worker.cmd -Envs 24
```

```bash
bash bees-remote-worker.sh
bash bees-remote-worker.sh --envs 24
```

With no explicit environment count, the worker starts near four times its available logical CPU threads, capped by its RAM budget and the 64-env actor limit. BeesServer then measures rollout steps/sec only after the central learner drains current-policy batches for training, probes nearby environment counts one worker at a time, keeps changes that improve sustained throughput, and backs off changes that do not. Queue admission alone does not count as useful throughput. The learner drains queued current-policy batches fairly across ready actors, one batch per actor per round before taking a second from the same actor, while still consuming extra batches from fast actors when slower actors have nothing queued. A measurement window in which a worker produced rollout steps but the learner consumed none is treated as a starved/invalid sample and retried rather than as zero useful throughput. Measurements include warm-up/cooldown periods and hysteresis so normal training noise does not continuously restart workers. Stable workers are periodically retested because the optimum can change with workload or machine load, but a worker that reports a process error or a recent internal WAN/Unity session failure enters a 15-minute stability hold before any new exploratory env-count restart; after the hold it remeasures the accepted baseline before probing again. Planned env-count transitions and release cutovers do not count as instability. Optimization pauses during release cutovers and when training is disabled.

Passing `-Envs N` / `--envs N` is an explicit fixed override and disables auto tuning for that worker. Auto-tuned workers never exceed their startup RAM-derived cap. Linux affinity/cgroups and Windows process affinity are respected. Remote ML-Agents environment managers retain the built-in rolling restart-rate guard (by default one restart per worker per 60 seconds) but disable the separate lifetime restart cap, so isolated Unity failures over a long training session do not eventually poison an otherwise healthy actor while rapid repeated crashes still fail closed. Repeated WAN/session reconnect failures use bounded exponential backoff up to 30 seconds and reset after a healthy session or intentional generation change. Actor slots are assigned centrally; each installation keeps a persistent actor key for safe reconnects. WAN actor registration is also bound to the active semantic lineage (`run_id` + `compatibility_key` + the SHA-256 identity of the exact static environment-argument list). Exact build id is carried for diagnostics but is intentionally not part of that gate, because a compatible code rollout may move remote executables while preserving the same semantic lineage. Environment identity is separate so a same-run scenario/configuration change cannot mix old and new trajectories during staggered restarts. When an optimizer-driven env-count change deliberately replaces a worker process, a current broker-issued replacement claim allows the same persistent actor key to take its existing slot immediately; arbitrary second processes remain rejected, and the replaced instance loses heartbeat/trajectory authority as soon as the handoff completes. The live `status` table labels the per-worker optimizer sample as `OptExp/s` (learner-consumed experience used for env tuning) and separately labels the ML-Agents global training-step rates as `LearnerAvgStep/s` and `LearnerLiveStep/s`, so those non-equivalent rates are not presented as interchangeable SPS values. It also shows current/desired env count, optimizer phase, rolling true turret aim error/<5° rate, and combat hits-per-shot rather than labeling all-source damage events as ordinary shooting accuracy. WAN capacity logs report cumulative backpressure plus the last-60-second count/rate.

Windows bootstraps Python 3.10 when necessary. Linux uses a user-local `uv`/Python 3.10 environment.

## Control service state

BeesServer's training-control HTTP service is separate from the gameplay WebSocket. Worker and admin access use separate bearer tokens.

The operator normally sets these automatically:

- `BEES_TRAINING_CONTROL_TOKEN`
- `BEES_TRAINING_CONTROL_ADMIN_TOKEN`
- `BEES_TRAINING_CONTROL_STATE`
- `BEES_TRAINING_ARTIFACT_ROOT`
- `BEES_TRAINING_LOG_ROOT`

State schema 5 persists:

- desired training state and environment arguments
- current canonical build
- active run id and compatibility key
- pending release/rollout phase
- dedicated and full-game immutable artifact catalogs

Schema-2, schema-3, and schema-4 state migrate forward. Canonical artifacts are server-owned copies and are rechecked for exact size/SHA-256 when state is loaded.

Dedicated workers fail closed when the control lease expires. The default control lease is 60 seconds; worker heartbeats run every 5 seconds and individual control requests time out after 5 seconds, so a brief control-plane stall does not consume most of the lease or unnecessarily recycle a healthy trainer. Loss of control authority is distinct from a local reconciliation error: after a successful heartbeat, an ancillary artifact/runtime-state/local-status failure does not kill a dedicated trainer if the already-running process still exactly matches the server-desired build hash/id, run id, compatibility key, environment arguments, and worker environment count. The error remains visible and reconciliation retries on the next heartbeat. If any of those identities differ, or the desired mode is no longer training, the worker still fails closed and stops the stale process immediately. Local diagnostic-state write failure is also best-effort and cannot by itself crash the supervisor. Full-game clients fall back to inference and are not killed merely because control is unavailable.

## Robustness qualification

Before a real training operation, the operator can run the focused distributed-training robustness gate without starting/stopping the live cluster or changing desired state:

```powershell
.\Assets\bees.ps1 qualify
```

The gate runs the focused Python orchestration/runtime/bootstrap/control suites, the BeesServer training-control Node suite, the whole `bees.ps1` PowerShell parse contract when PowerShell is available, and the tailnet bridge Go tests when a Go toolchain is available. It is intended to catch operator syntax regressions, release/runtime identity drift, resumability errors, worker/control lease regressions, WAN slot/reconnect issues, bootstrap publication problems, and control-state rollout failures before they reach an expensive training run. Missing Node is a qualification failure because server control is mandatory; missing Go is reported as a skip because the operator can bootstrap the pinned portable Go toolchain during an actual bridge build.

The qualification command is observational with respect to the live cluster: it does not start BeesServer, stage a release, change training state, create a run, restart a trainer, archive a run, or publish a bootstrap generation. It reuses or prepares the managed learner Python dependency environment for the current training requirements so WAN/ML-Agents-adjacent tests run against the dependencies the training stack actually expects; that local dependency cache preparation is the only setup side effect.

## Lower-level control CLI

`bees.ps1` is the normal operator interface. `trainingControlCli.js` remains available for diagnostics and unusual deployments.

Artifact publication is still explicit:

```text
node trainingControlCli.js publish-build --role dedicated --platform WindowsPlayer --build-id release-42 --archive C:\Builds\rl-windows.zip --entrypoint "Bees RL Training.exe"
```

Manual release activation is now run-aware and uses the coordinated rollout endpoint:

```text
node trainingControlCli.js activate-build --build-id release-42 --run-id bees-v18-r3-s1-... --compatibility-key <64-hex-sha256>
```

Add `--incompatible` only when that release intentionally starts a new run. The same release identity arguments may be supplied to `start`; `start` without a build id simply enables the already active release.

Other lower-level commands remain:

```text
node trainingControlCli.js status
node trainingControlCli.js stop
```

Environment/scenario changes intentionally do not have a raw CLI shortcut anymore: `trainingControlCli.js set-args` and `start --env-arg` fail before making a control request. Use `bees.ps1 start -EnvArg ...`, which runs the compiled-build validator and supplies the artifact-bound validation proof required by the release endpoint.

Direct canonical-build mutation is rejected by the generic desired-state API because it would bypass run identity and staged rollout; canonical build changes must use the run-aware release endpoint.

## Failure semantics

Build and runtime downloads are versioned, SHA-256 verified, path-traversal checked, and atomically installed. Training runtime installation is content-addressed and idempotent: an existing runtime directory is reverified rather than overwritten, and extraction uses a temporary directory before atomic activation. The central learner and remotes both verify the release's declared runtime identity before using it. Remote bootstrap publication is transaction-like at the filesystem boundary: runtime, release metadata, worker/WAN credentials, and both helper binaries are validated and written into one temporary outer ZIP, then that one file is atomically promoted with bounded retry for transient Windows sharing violations. A worker therefore receives either the previous complete bootstrap generation or the next complete generation, never a cross-generation mixture.

A dedicated trainer never switches to an unverified artifact. A compatible rollout keeps other trainers running while one trainer restarts and does not count that trainer complete until the pending build/hash is running at the rollout revision without a reported error. An incompatible rollout does not promote the new run until the central learner has staged the replacement, re-registered after any server restart, finalized the old checkpoint, and confirmed its managed process stopped, and every still-leased remote remaining in the persisted barrier has also confirmed the old process stopped. Lease-expired non-central remotes reduce capacity instead of permanently blocking promotion; reconnecting remotes reconcile through the active barrier or the new canonical run.

Trainer logs are uploaded by verified append offsets into:

```text
B:\Bees\Training\TrainerLogs\<run-id>\<trainer-id>\
```

Local worker logs are also run-scoped, preventing bytes from an old run from being re-attributed to a new one after restart.

Full-game processes keep the current player session alive when a canonical executable/config changes. They switch to inference immediately when required and apply the new executable/config on the next natural game launch.
