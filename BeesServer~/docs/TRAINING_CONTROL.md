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

By default it includes the newest ONNX file, the newest 10% of each text log, complete small JSON log metadata, current BeesServer/trainer status, cluster and PPO configuration, run/continual-service state, ML-Agents timer/training-status JSON, and a combined text log. Remote trainer logs are read from the learner-owned `B:\\Bees\\Training\\TrainerLogs\\<run-id>` mirror populated by the existing verified log-upload protocol; the bundle command does not SSH into trainer machines.

Use a different percentage when needed:

```powershell
.\Assets\bees.ps1 bundle -LogPercent 25
```

To package a retained older run explicitly:

```powershell
.\Assets\bees.ps1 bundle -RunId bees-v19-r3-s1-... -LogPercent 10
```

The ZIP contains `manifest.json` with the selected run, source paths, byte ranges, hashes, and any missing-data warnings. A missing ONNX or unavailable live status is recorded as a warning rather than preventing collection, so the command remains useful for diagnosing failed or newly started runs.

Environment/scenario arguments may be supplied for one start with repeated `-EnvArg` values. For example, a fresh 1v1 Wasp-versus-Gunship run on a 32-unit map with a 30-second timeout is:

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

## Builds, run identity, and compatibility

Every compiled training release contains:

- immutable build id
- source Git commit
- run id
- compatibility key
- compatibility contract
- Windows/Linux dedicated artifacts and optional full-game artifact

Run identity is calculated automatically by `Training/bees_run_lifecycle.py`. A compatible build keeps the existing run id and optimizer/checkpoint lineage. An incompatible training contract creates a new run id automatically. Ordinary `start` also resumes the current run by default. Use `start -NewRun` only when a fresh optimizer/checkpoint lineage is explicitly desired without rebuilding the Unity executable.

The compatibility fingerprint currently includes the continual-learning behavior/schema identity, frozen policy signature, network architecture settings, reward implementation, policy-schema implementation, combat perception, action implementation, exploration-grid implementation, and episode ship identity. This makes policy/reward/observation/action changes fail safe even if a developer forgets to increment a manual schema version. Ordinary non-architectural PPO tuning and other compatible operational changes do not by themselves force a new run.

Durable continual-service phase state is run-scoped. Checkpoints/results remain under the run id in `B:\\Bees\\Training`; a build never deletes the outgoing run's optimizer state, checkpoints, telemetry, or other durable training data.

`generationSteps` remains the number of additional learner steps per continual generation. It is an evaluation/release cadence, not a reset interval.

## Automatic log preservation

Before every new build starts compiling, the currently active run is snapshotted into:

```text
B:\Bees\Assets\TrainingHistory~\runs\<run-id>\
```

The trailing `~` keeps the history outside Unity import while allowing Git to track it. Training logs are split into 48 MiB parts so individual tracked files remain below GitHub's per-file hard limit. Each snapshot contains a manifest with hashes and captured byte lengths.

The archive command then creates a Git commit containing only that run-history path and pushes the current branch to `origin`. If the Git commit or push fails, `build` stops before compilation rather than silently proceeding with unprotected logs.

During an incompatible cutover, dedicated trainers stop only after the replacement is fully staged. The central learner first requests a graceful ML-Agents interruption and remains leased as `stopping` while ML-Agents writes its final checkpoint, ONNX export, `timers.json`, and training status. Each trainer fully flushes its outgoing run-scoped logs to the learner before reporting `stopped`. After the new run is promoted, the old run is snapshotted again so the terminal log tail is committed as well.

Checkpoint/model data is intentionally not copied into GitHub. It remains in the durable `B:\\Bees\\Training` run directories, which are not removed by build or rollout.

## Seamless release rollout

Publishing a build and activating it are separate operations.

For a compatible release:

1. The old release keeps training.
2. Every active dedicated trainer downloads and verifies the new Unity build in the background.
3. Remote supervisors also fetch the matching Python runtime, worker/WAN credentials, release metadata, and embedded tailnet helper over the existing private bootstrap channel.
4. A trainer reports the release prepared only when its Unity artifact and required remote runtime are ready.
5. Once all active trainers are prepared, the server rolls dedicated trainers one at a time.
6. Trainers already moved to the pending release stay there while the remaining trainers update.
7. The central learner is ordered after remote trainers.
8. After all dedicated trainers report the new build, it becomes canonical.

For an incompatible release:

1. All active trainers continue the old run while the replacement is downloaded and verified.
2. When every active dedicated trainer is prepared, the server requests a coordinated stop.
3. Remote trainers terminate their managed Unity/process trees and flush outgoing run logs; the central learner remains heartbeating as `stopping` until ML-Agents has finalized the current optimizer checkpoint, ONNX model, timers, and status files.
4. Only after the central save is complete and all trainers report `stopped` does the server promote the new build, compatibility key, and run id.
5. Trainers restart under the new run. The old run's checkpoint/results tree remains intact.

This keeps update interruption limited to the actual process restart/cutover rather than download, extraction, dependency installation, or artifact verification.

The build command automatically stages a successful build into a control server that is already online. Compatible rollouts therefore continue after `build` returns. For an incompatible release, `build` waits for the coordinated cutover and performs the final old-run log archive before returning.

## Managed BeesServer updates

The operator records the Git tree identity of `BeesServer~` when it launches the managed server. `server`, `start`, and live-cluster `build` detect a changed server tree and restart the managed BeesServer automatically while preserving the persisted training desired state and artifact catalog.

If the control port is occupied by a server that was not launched/recorded by the Bees operator, the script refuses to kill it automatically. Stop that unmanaged server once and rerun the command; subsequent source refreshes can then be automatic.

## Private remote workers

`start` prepares one copy-and-run launcher per remote platform:

```text
B:\Bees\Remote\bees-remote-worker.cmd
B:\Bees\Remote\bees-remote-worker.sh
```

Each launcher is self-extracting and contains its platform bootstrap plus embedded tailnet helper. The server still keeps `bees-remote-runtime.zip` for private bootstrap delivery; it is not copied manually to remote machines.

The private training transport uses the embedded Tailscale userspace library. No separate Tailscale installation, VPN driver, SSH server/client, Windows training account, SSH key, password, SCP step, or additional router port forwarding is required for control, WAN rollouts, or bootstrap. Remote Unity players still use the normal public Bees gameplay/settings endpoint, just like other game clients.

On first use, the learner and each remote worker print a Tailscale authorization URL. Their identities are persisted. The generated launcher contains the learner's private tailnet address and bootstrap credential.

The learner gateway exposes only these private tailnet services:

- control, normally 7150
- WAN rollout broker, normally 55051
- bootstrap service, normally 7151

The bootstrap endpoint requires its own bearer token. It serves the current remote Python runtime, worker token, WAN token, release metadata, and versioned Windows/Linux tailnet helper. It never serves the admin token.

After a worker is bootstrapped with the current launcher, future runtime/helper releases are fetched and staged automatically. Tailnet helper binaries are published from immutable source-hash version directories rather than by overwriting a live executable. When helper source changes, the learner gateway is restarted onto the new immutable version after the updated bootstrap payload is prepared. The supervisor switches remote runtime/helper releases only at that trainer's assigned build cutover.

Machines that were already running a launcher from before this self-update mechanism existed need one final manual bootstrap with the newly generated launcher. After that transition, routine builds do not require recopying the launcher.

The normal per-machine tuning argument is environment count:

```cmd
bees-remote-worker.cmd
bees-remote-worker.cmd -Envs 24
```

```bash
bash bees-remote-worker.sh
bash bees-remote-worker.sh --envs 24
```

If omitted, the worker uses four times the logical CPU threads available to the process, capped at 64. Linux affinity/cgroups and Windows process affinity are respected. Actor slots are assigned centrally; each installation keeps a persistent actor key for safe reconnects.

Windows bootstraps Python 3.10 when necessary. Linux uses a user-local `uv`/Python 3.10 environment.

## Control service state

BeesServer's training-control HTTP service is separate from the gameplay WebSocket. Worker and admin access use separate bearer tokens.

The operator normally sets these automatically:

- `BEES_TRAINING_CONTROL_TOKEN`
- `BEES_TRAINING_CONTROL_ADMIN_TOKEN`
- `BEES_TRAINING_CONTROL_STATE`
- `BEES_TRAINING_ARTIFACT_ROOT`
- `BEES_TRAINING_LOG_ROOT`

State schema 4 persists:

- desired training state and environment arguments
- current canonical build
- active run id and compatibility key
- pending release/rollout phase
- dedicated and full-game immutable artifact catalogs

Schema-2 and schema-3 state migrate forward. Canonical artifacts are server-owned copies and are rechecked for exact size/SHA-256 when state is loaded.

Dedicated workers fail closed when the control lease expires. Full-game clients fall back to inference and are not killed merely because control is unavailable.

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
node trainingControlCli.js set-args --env-arg --rl-map-size --env-arg 128
node trainingControlCli.js stop
```

Direct canonical-build mutation is no longer used by the CLI because it would bypass run identity and staged rollout.

## Failure semantics

Build and runtime downloads are versioned, SHA-256 verified, path-traversal checked, and atomically installed. The learner publishes live bootstrap ZIPs/helper binaries/release metadata by atomic replacement so a worker polling during compilation cannot consume a partially written file.

A dedicated trainer never switches to an unverified artifact. A compatible rollout keeps other trainers running while one trainer restarts. An incompatible rollout does not promote the new run until every active dedicated trainer has staged the replacement and confirmed the old managed process stopped.

Trainer logs are uploaded by verified append offsets into:

```text
B:\Bees\Training\TrainerLogs\<run-id>\<trainer-id>\
```

Local worker logs are also run-scoped, preventing bytes from an old run from being re-attributed to a new one after restart.

Full-game processes keep the current player session alive when a canonical executable/config changes. They switch to inference immediately when required and apply the new executable/config on the next natural game launch.
