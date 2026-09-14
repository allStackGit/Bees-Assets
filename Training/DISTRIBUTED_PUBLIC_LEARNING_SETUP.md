# Distributed continual-learning setup

This system is designed so that, after one-time machine configuration, ordinary server startup plus gameplay can continuously feed one validated central neural-policy lineage.

## Learning model

There is exactly one authoritative ML-Agents/PPO optimizer. Dedicated training environments contribute **directly** through fresh on-policy PPO rollouts. Ordinary desktop gameplay contributes **indirectly** through bounded off-policy telemetry that is validated, mined into scenario/tactic pressure, and then reproduced through fresh PPO rollouts.

Recorded gameplay actions are never inserted into PPO as if they were current-policy trajectories. Human, Hive Mind, and deployed-neural actions retain separate provenance and cannot cross-confirm one another as repeated source-specific tactics.

Candidate creation, historical-opponent evaluation, permanent competency checks, behavior sanity, promotion, deployment, hot distribution, and rollback remain mandatory. A client receives only a validated champion.

## What contributes automatically

Outside the dedicated ML-Agents training runtime, `RlLiveTelemetryRecorder` installs on ordinary desktop gameplay `Stage` scenes unless `--rl-disable-automatic-telemetry` is supplied.

Eligible policy-controlled ships are sampled through the frozen policy observation/action ABI regardless of whether the active controller is:

- a human/player;
- the Hive Mind; or
- the deployed neural network.

This means Human-vs-NN, Human-vs-Hive-Mind, Hive-Mind-vs-NN, Hive-Mind-vs-Hive-Mind, and deployed-NN-vs-deployed-NN gameplay can all contribute evidence. Production, Development, and explicit Test desktop builds use the same telemetry format. The dedicated training runtime is excluded from this recorder because it already contributes directly through PPO.

Successful ship-special, mining, healing, and warp actions are captured at their authoritative capability-event boundary. Telemetry is split into segments of at most 64 decisions and stored under:

```text
Application.persistentDataPath/RlLiveTelemetry/PolicyV8/
  Draft/
  Pending/
  Invalid/
```

Pending data is deleted only after BeesServer confirms immutable archival. Network/authentication/rate-limit failures retain the backlog for retry. Invalid local payloads are isolated rather than retried indefinitely.

The recorder requires a compatible validated deployment manifest so telemetry is tied to an exact model/ABI lineage. A build therefore needs an initial staged champion before it can participate in this loop.

## Server quarantine and client updates

BeesServer provides the authenticated telemetry quarantine and validated champion-distribution endpoints. Configure durable roots:

```text
BEES_RL_TELEMETRY_UPLOAD_DIR=<durable telemetry quarantine>
BEES_RL_MODEL_DISTRIBUTION_DIR=<published hot-bundle root>
```

Production/Development clients use the authenticated Steam boundary. Explicit Test clients use the configured test identity and test WebSocket server.

Every ordinary gameplay stage with a valid bundled champion also polls BeesServer for a newer compatible validated champion, even when the current mode is using only Player or Hive Mind control. The downloaded model is hash/manifest/ABI validated before becoming the cached champion. It is bound to ships only when a side actually requests neural control; failures do not change Player/Hive-Mind-only ownership. If neural control was requested and no compatible champion is usable, that side fails safely to Hive Mind.

## Autonomous central service

`Training/bees_continual_service.py` is the long-running central orchestration layer for local/direct workers. `Training/bees_continual_elastic_wan_service.py` preserves the same state machine while adding an elastic WAN actor pool to Exeter's normal local environments. Both advance one persistent training lineage through immutable generations:

```text
gameplay telemetry
  -> BeesServer quarantine
  -> central validation / provenance separation
  -> tactic + scenario pressure for next generation
  -> fresh PPO rollouts
  -> candidate
  -> authoritative evaluation
  -> validated champion
  -> published hot bundle
  -> gameplay clients
  -> more telemetry
```

Each generation freezes its player-derived pressure selection. Data arriving during an active generation becomes eligible for the next generation rather than mutating an in-progress training distribution.

The service persists its phase/generation state. Training, evaluation, Unity build, or publication failures do not replace the current champion; the failed phase is retried safely.

## One-time BeesServer autostart configuration

On the machine that hosts the central training system, BeesServer branch `rl/unified-training-demo-upload` can supervise the autonomous service. Configure these environment variables once:

```text
BEES_RL_CONTINUAL_AUTOSTART=1
BEES_RL_CONTINUAL_ROOT=<continual store root>
BEES_RL_ASSETS_ROOT=<Bees-Assets root>
BEES_RL_TRAINING_ENV=<Bees RL Training executable>
BEES_RL_TELEMETRY_UPLOAD_DIR=<same durable quarantine used by BeesServer>
BEES_RL_MODEL_DISTRIBUTION_DIR=<same distribution root used by BeesServer>
BEES_RL_GAME_BUILD_VERSION=<current compatible game build identity>
BEES_RL_UNITY_EDITOR=<Unity editor executable>
BEES_RL_UNITY_PROJECT_ROOT=<Unity project root>
```

Optional tuning/configuration variables include:

```text
BEES_RL_PYTHON
BEES_RL_TRAINER_CONFIG
BEES_RL_CONTINUAL_CONFIG
BEES_RL_COMPETENCY_SUITE
BEES_RL_CONTINUAL_RUN_ID
BEES_RL_PLATFORM
BEES_RL_GENERATION_STEPS
BEES_RL_NUM_ENVS
BEES_RL_CONTINUAL_RETRY_SECONDS
```

When autostart is enabled, `start-server.js` validates the required configuration before accepting a server-only launch. Normal foreground startup supervises the continual service directly. Background startup launches a detached continual-learning watchdog so the Python service still has process-level restart protection after the launcher exits.

After those machine-specific paths are configured and a compatible initial champion/training build exists, the intended local operating procedure is simply:

```text
start BeesServer
play Bees
```

No operator should need to manually ingest telemetry, curate normal public tactics, start another training generation, run candidate release, or republish an approved champion during healthy operation.

## Automatic public processing safeguards

Strict-valid gameplay batches can be automatically approved **only for offline tactic mining**. They never become trusted on-policy experience.

Automatic processing:

- independently revalidates server quarantine metadata, payload hashes, deployment/model identity, frozen policy compatibility, observations/actions, and contributor provenance;
- keeps Human, Hive Mind, and deployed-neural evidence separate;
- enforces contributor and batch caps;
- mines repeated tactical signatures and approximate geometry;
- registers both Bee/Human orientations where public `agent_key` does not prove side identity;
- activates at most 32 tactic signatures so both orientations fit Unity's 64-scenario registry;
- defaults to 10% automatic player-derived pressure and remains under the existing 50% combined hard cap.

The default occurrence/contributor thresholds remain permissive enough for a small testing population. Raise them when public traffic is large enough for stronger population-level evidence.

## Multi-machine rollout modes

There are two supported ways to add remote rollout machines. Both keep exactly one authoritative PPO optimizer/checkpoint owner on Exeter.

### Direct ML-Agents workers

The original direct mode is appropriate for LAN or otherwise low-latency connections. External workers are a suffix of ML-Agents worker IDs. The central launcher writes a content-hashed remote spec that pins assigned worker IDs, base port, run ID, and the exact final Unity `--env-args`, including frozen gameplay-derived pressure.

The remote machine runs `Training/bees_remote_worker.py` with that spec. The helper validates the spec, creates SSH local forwards to loopback-only ML-Agents ports on the trainer, and launches only its assigned Unity workers. Every ML-Agents decision still crosses the tunnel in this mode.

### Elastic WAN actors with local inference

`Training/bees_elastic_wan_training.py` and `Training/bees_elastic_wan_actor_worker.py` are the high-latency path. Exeter continues running its normal `BEES_RL_NUM_ENVS` local environments. Remote machines are purely additive: **zero through twelve** may be online at any moment, and every remote machine independently chooses **one through sixty-four** Unity environments.

The central configuration describes capacity, not a required topology. For Exeter running 32 local environments and accepting up to twelve remote machines:

```text
BEES_RL_NUM_ENVS=32
BEES_RL_WAN_ACTORS=12
BEES_RL_WAN_MIN_ACTORS=0
BEES_RL_WAN_BROKER_PORT=55051
BEES_RL_WAN_AUTH_TOKEN_FILE=<Exeter path to a 32+ character token file>
BEES_RL_WAN_MAX_QUEUED_BATCHES=32
# optional; default 120 seconds
BEES_RL_WAN_ACTOR_LEASE_SECONDS=120
```

`BEES_RL_WAN_ACTORS=12` reserves twelve stable remote actor slots. It does **not** mean twelve machines must be online. `BEES_RL_WAN_MIN_ACTORS=0` means Exeter trains normally with only its local environments and automatically begins consuming remote trajectories whenever a machine joins. The obsolete fixed `BEES_RL_WAN_ENVS_PER_ACTOR` setting is intentionally rejected.

Each remote machine chooses its own size. For example, actor 0 could run 8 environments while actor 1 runs 64:

```powershell
python Training\bees_elastic_wan_actor_worker.py `
  --actor-id=0 `
  --envs=8 `
  --ssh="trainer-user@exeter-host" `
  --env="D:\BeesRL\Bees RL Training.exe" `
  --auth-token-file="D:\BeesRL\wan-token.txt" `
  --torch-device=cpu
```

A second machine can use `--actor-id=1 --envs=64`, and so on through actor ID 11. Actor IDs are fixed 64-worker slots after Exeter's local worker IDs, so changing or stopping one actor does not renumber another actor's trajectories. A stopped machine ages out of the active topology after the lease interval; restarting it with the same actor ID reclaims the same worker-ID slot.

Remote actors run Unity and inference locally. They upload complete native ML-Agents `Trajectory` batches rather than making a WAN round trip for every decision. Every batch is tied to the exact central policy versions and environment-control epoch that generated it. A central policy/control change discards queued older batches; remote actors drain already-issued actions, discard unfinished fragments, synchronize the new policy, and continue. Exeter alone owns PPO updates, optimizer state, checkpoints, candidates, promotion, and release.

The broker binds only to `127.0.0.1`, is reached through SSH forwarding, and additionally requires the trainer bearer token. The token is trusted trainer infrastructure and must never be shipped in a normal gameplay build.

### Capacity diagnostics

Elastic WAN mode emits periodic lines beginning with:

```text
[Bees WAN capacity]
```

The regular diagnostic reports:

- active remote actor count;
- active remote environment count;
- Exeter's actual ML-Agents **trainer steps/sec**;
- accepted remote rollout steps/sec;
- central trajectory-queue occupancy; and
- backpressure events.

When remote capacity increases, the trainer records the pre-addition trainer-rate baseline and waits for a settling window. It then reports a marginal test such as:

```text
[Bees WAN capacity] marginal-test remote_envs=64->128 gain=+18.4% status=beneficial.
```

If another machine produces less than roughly a 3% trainer-step improvement while the central queue is filling or actors are being backpressured, it reports:

```text
[Bees WAN capacity] CAPACITY LIMIT LIKELY: ... Additional workers are unlikely to improve training speed.
```

A low gain **without** central queue pressure is reported separately as `no-measurable-gain`, because that can mean the added remote machine is itself slow rather than Exeter being saturated. The intended scaling procedure is therefore: start Exeter alone, add one remote machine, allow the marginal diagnostic to settle, then add another until the system reports likely central saturation or the desired throughput is reached.

The remote helper persists across central continual generations. During evaluation/release the training broker is absent, so the helper waits; when the next generation begins it automatically joins the new broker session with the same actor slot and environment count.

Every rollout machine must still have the same compatible training build. Automatic distribution of an entire training-build directory to arbitrary remote machines is infrastructure work outside the learning loop.

## Operational boundaries

The automatic loop does not bypass safety gates and does not make individual gameplay clients trainers. The central trainer is authoritative; gameplay is evidence; only validated champions are distributed.

WebGL does not participate in the desktop telemetry/hot-bundle path. Fully inert entities that have no movement, weapons, special, mining, healing, or warp action do not create policy action records themselves, although matches containing normal policy-controlled ships still contribute through those ships.
