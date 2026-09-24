# Distributed training control

BeesServer can act as the desired-state authority for distributed RL workers. The control service is separate from the gameplay WebSocket so training operations do not alter the Unity request/response protocol.

## Server setup

Set separate `BEES_TRAINING_CONTROL_TOKEN` (worker access) and `BEES_TRAINING_CONTROL_ADMIN_TOKEN` (operator changes) before starting BeesServer. When the worker token is present, BeesServer starts the training-control listener on `127.0.0.1:7150` by default. Keep the default loopback binding and use SSH/private-network forwarding when practical; set `BEES_TRAINING_CONTROL_HOST` only when the control port is intentionally exposed on a protected network. Other overrides are `BEES_TRAINING_CONTROL_PORT`, `BEES_TRAINING_CONTROL_STATE`, `BEES_TRAINING_ARTIFACT_ROOT`, and `BEES_TRAINING_CONTROL_LEASE_SECONDS`.

Desired state is persisted under `logs/training-control-state.json` by default. Canonical build archives are copied into the server-owned `training-artifacts/` directory and remain available after server restarts.

## Canonical builds

Package a compiled build with `Training/bees_package_training_build.py`. The package is a ZIP containing the complete compiled build and an entrypoint such as `Bees.exe` or `Bees.x86_64`.

Publish it from the BeesServer host. The CLI uses `BEES_TRAINING_CONTROL_ADMIN_TOKEN` (or its `_FILE` variant):

```text
node trainingControlCli.js publish-build --platform WindowsPlayer --build-id 2026-09-24-a --archive C:\\Builds\\BeesWindows.zip --entrypoint Bees.exe
```

Publish Linux separately with its own platform and entrypoint. Workers receive only the canonical artifact for their declared platform.

## Start, stop, arguments, and status

```text
node trainingControlCli.js start --env-arg --rl-map-size --env-arg 64
node trainingControlCli.js set-args --env-arg --rl-map-size --env-arg 128
node trainingControlCli.js stop
node trainingControlCli.js status
```

Every desired-state or canonical-build change increments a persistent revision. Workers observe the revision on their next heartbeat and reconcile automatically.

## Managed workers

Run `Training/bees_training_worker_agent.py` persistently on each trainer machine. The launch command after `--` must contain `{env}`. Use `{env_args}` where the server-owned environment argument list belongs.

Example dedicated worker:

```text
python Training/bees_training_worker_agent.py --server-url http://server:7150 --token-file C:\\Bees\\training-control.token --trainer-id exeter-actor-0 --role dedicated --platform WindowsPlayer --install-root C:\\Bees\\ManagedBuilds -- python Training\\bees_elastic_wan_actor_worker.py --actor-id 0 --envs 32 --ssh user@learner --env {env} --auth-token-file C:\\Bees\\wan.token
```

The worker agent also exports the current server argument list as `BEES_TRAINING_ENV_ARGS_JSON`. The legacy SSH remote-worker launcher honors that value when it is managed by the control plane. Elastic/WAN actors continue receiving their actual ML-Agents run/environment configuration from the current central learner session.

For a managed full game, use `--role full-game`. A server lease loss does not terminate the game process. Player-facing Bees continues using the deployed policy for inference, and `RlLiveTelemetryRecorder` continues writing pending telemetry locally even if the uploader or central trainer is unavailable.

## Failure and update semantics

Dedicated workers terminate their managed process when they cannot renew the BeesServer lease. They restart only after the server is reachable and desired state says `training`.

Full-game workers keep the game running during a lease outage. Their local control-state file is marked offline/inference. When BeesServer returns, heartbeats resume and the newest desired revision is reconciled.

Build downloads are staged, checked against the server-advertised size and SHA-256, validated against ZIP path traversal, extracted into a temporary versioned directory, then atomically activated. A partial or invalid download never replaces the usable build.
