# Distributed training control

## Unified operator commands

With the unified project layout, the Unity project root is `B:\\Bees` and the Git repository is `B:\\Bees\\Assets`. Day-to-day operation is through one PowerShell entry point:

```powershell
cd B:\Bees

.\Assets\bees.ps1 build
.\Assets\bees.ps1 start
.\Assets\bees.ps1 stop
.\Assets\bees.ps1 status
```

`build` always produces a Windows RL build and Linux RL build. Add `-FullGame` to also produce the managed Windows gameplay build:

```powershell
.\Assets\bees.ps1 build -FullGame
```

Build folders are siblings of `Assets`, for example `B:\\Bees\\Builds\\2026-09-24 RL Windows`, `... RL Linux`, and optionally `... Full Game Windows`. Because `Builds` is outside the Git root (`B:\\Bees\\Assets`) and outside Unity's `Assets` import tree, it is neither tracked by Git nor imported/compiled by Unity. Rebuilding the same type on the same day requires `-Force`, preventing stale files from being mixed into a new player.

The build command also creates immutable upload ZIPs and `B:\\Bees\\Builds\\latest-training-release.json`. The release id combines date, time, and source Git commit so repeated same-day builds remain immutable-safe. `start` publishes those archives, activates the release, starts/keeps the centrally managed learner, and writes the configured environment arguments as the authoritative desired state. `stop` disables training everywhere but leaves BeesServer online for gameplay/control; use `stop -Server` when the BeesServer process itself should also exit. `status` is a live dashboard; use `status -Once` for one snapshot.

The authoritative non-secret cluster configuration is checked into Git at `Assets\\Training\\bees.cluster.json`. Edit that file on the training branch so local environment counts, ports, generation size, remote capacity, and similar operational changes are versioned with the code. Machine-specific secrets remain under `B:\\Bees\\Secrets`, outside Git/Unity.

`generationSteps` is the number of additional global learner steps in one continual-learning generation. With the default `1000000`, generation 0 trains to 1,000,000 total steps and evaluates/releases; generation 1 resumes the same optimizer/checkpoint lineage and trains to 2,000,000 total steps; generation 2 trains to 3,000,000, and so on. It is an evaluation/release cadence, not a reset interval.

`start` also prepares `B:\\Bees\\Remote\\bees-remote-runtime.zip` plus one Windows launcher (`bees-remote-worker.ps1`) and one Linux launcher (`bees-remote-worker.sh`). The files are no longer numbered. Copy the matching launcher to any remote machine and run the same file there; the learner-side WAN broker assigns the first available actor slot automatically. Each remote installation persists a random actor key locally so reconnects can reclaim the same live slot safely without embedding a machine-specific actor number in the copied launcher.

The only normal per-machine tuning argument is the environment count. On Windows, run `bees-remote-worker.ps1 -Envs 24`; on Linux, run `bash bees-remote-worker.sh --envs 24`. If `Envs`/`--envs` is omitted, the remote supervisor uses four times the CPU threads available to that process, capped at the current 64-environment actor limit. Linux CPU affinity/cgroups are respected through `sched_getaffinity`; Windows process affinity is respected through `GetProcessAffinityMask`. Both launchers use SSH/SCP to fetch the current worker runtime and worker-only tokens, create/update an isolated Python 3.10 environment, start the control tunnel, download the canonical platform-specific Unity build through BeesServer, and join the elastic rollout pool. Neither launcher contains the admin token.

On Linux, the launcher installs missing OpenSSH/download prerequisites through a supported package manager when necessary, installs a user-local `uv` runtime, provisions Python 3.10, and uses `remoteLinuxInstallRoot` (default `.local/share/bees-training`) under the user's home directory. The shared Python supervisor reports `WindowsPlayer` on Windows and `LinuxPlayer` on Linux, so BeesServer distributes the correct canonical build automatically.

The learner must be reachable by SSH. When `remoteSshTarget` is blank, `start` generates launchers targeting `<current-user>@<current-computer-name>`, which is convenient on a LAN. Set `remoteSshTarget` in the tracked config to a resolvable LAN address, VPN address, or WAN SSH hostname when that automatic target is not appropriate. Key-based SSH is recommended for unattended reconnects.


BeesServer can act as the desired-state authority for distributed RL workers. The control service is separate from the gameplay WebSocket so training operations do not alter the Unity request/response protocol.

## Server setup

Set separate `BEES_TRAINING_CONTROL_TOKEN` (worker access) and `BEES_TRAINING_CONTROL_ADMIN_TOKEN` (operator changes) before starting BeesServer. When the worker token is present, BeesServer starts the training-control listener on `127.0.0.1:7150` by default. Keep the default loopback binding and use SSH/private-network forwarding when practical; set `BEES_TRAINING_CONTROL_HOST` only when the control port is intentionally exposed on a protected network. Other overrides are `BEES_TRAINING_CONTROL_PORT`, `BEES_TRAINING_CONTROL_STATE`, `BEES_TRAINING_ARTIFACT_ROOT`, and `BEES_TRAINING_CONTROL_LEASE_SECONDS`.

Desired state is persisted under `logs/training-control-state.json` by default. State schema 3 separates dedicated and full-game artifact catalogs; existing schema-2 deployments migrate their prior artifact to both roles to preserve pre-upgrade behavior. Canonical build archives are copied into the server-owned `training-artifacts/` directory and remain available after server restarts. On control-plane startup, active canonical artifacts are rechecked for exact size and SHA-256; tampered or truncated canonical bytes fail startup rather than being distributed.

## Canonical builds

Package a compiled build with `Training/bees_package_training_build.py`. The package is a ZIP containing the complete compiled build and an entrypoint such as `Bees.exe` or `Bees.x86_64`.

Publish it from the BeesServer host. The CLI uses `BEES_TRAINING_CONTROL_ADMIN_TOKEN` (or its `_FILE` variant):

```text
node trainingControlCli.js publish-build --role dedicated --platform WindowsPlayer --build-id 2026-09-24-a --archive C:\\Builds\\BeesWindows.zip --entrypoint "Bees RL Training.exe"
```

Publish every required role/platform artifact under the same logical `--build-id`. Dedicated Windows and Linux training builds use role `dedicated`; the optional Windows gameplay build uses role `full-game`. Publishing only stages immutable artifacts; it does not activate them. Activating `release-42` makes that one source release authoritative across all managed machines. A missing dedicated platform artifact blocks that dedicated trainer from starting (and prevents cluster start when the trainer is currently connected). A missing optional full-game artifact does not block PPO training; that gameplay client remains inference-only until a matching full-game artifact is published.

## Start, stop, arguments, and status

```text
node trainingControlCli.js activate-build --build-id 2026-09-24-a
node trainingControlCli.js start --build-id 2026-09-24-a --env-arg --rl-map-size --env-arg 64
node trainingControlCli.js set-args --env-arg --rl-map-size --env-arg 128
node trainingControlCli.js stop
node trainingControlCli.js status
```

Desired-state changes, including canonical build activation and environment arguments, increment a persistent revision. Staging an inactive build does not disturb running workers. Workers observe desired-state revisions on heartbeats and reconcile automatically.

## Managed workers

For normal Windows or Linux rollout workers, prefer the generated one-file launcher described above. The lower-level managed-worker command remains available for debugging or nonstandard deployments.

Run `Training/bees_training_worker_agent.py` persistently on each trainer machine. The launch command after `--` must contain `{env}`. Use `{env_args}` where the server-owned environment argument list belongs.

Example dedicated worker:

```text
python Training/bees_training_worker_agent.py --server-url http://server:7150 --token-file C:\\Bees\\training-control.token --trainer-id exeter-actor-0 --role dedicated --platform WindowsPlayer --install-root C:\\Bees\\ManagedBuilds -- python Training\\bees_elastic_wan_actor_worker.py --actor-id 0 --envs 32 --ssh user@learner --env {env} --auth-token-file C:\\Bees\\wan.token
```

The worker agent also exports the current server argument list as `BEES_TRAINING_ENV_ARGS_JSON`. The legacy SSH remote-worker launcher honors that value when it is managed by the control plane. Elastic/WAN actors continue receiving their actual ML-Agents run/environment configuration from the current central learner session.

For a managed full game, use `--role full-game`. A server lease loss does not terminate the game process. Player-facing Bees continues using the deployed policy for inference, and `RlLiveTelemetryRecorder` continues writing pending telemetry locally even if the uploader or central trainer is unavailable.

## Failure and update semantics

Dedicated workers terminate the complete managed process tree after the BeesServer lease expires, including ML-Agents/Unity descendants. If BeesServer is reachable but the active canonical build is missing, incompatible, or cannot be verified, they stop immediately rather than continuing with stale code. They restart only after the server is reachable, the platform-equivalent canonical build is available, and desired state says `training`.

Full-game workers keep the game running during a lease outage. Their local control-state file is marked offline/inference. Unity also checks the timestamp and lease duration in that file, so a crashed local supervisor eventually forces inference even if it cannot rewrite the file. When BeesServer returns, heartbeats resume. If the current executable/config still matches the canonical revision, the running game can return to training in place. If the canonical build or command-line environment arguments changed, the live game remains inference-only and is not killed; the agent applies the canonical build/config on the next natural game launch.

Build downloads are staged, checked against the server-advertised size and SHA-256, validated against ZIP path traversal, extracted into a temporary versioned directory, then atomically activated. A partial or invalid download never replaces the usable build.
