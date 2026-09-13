# Distributed public-learning setup

This guide covers the automatic public-player telemetry path and optional multi-machine ML-Agents rollout path. It extends the existing continual-learning system; it does not replace PPO, candidate registration, historical opponents, authoritative evaluation, promotion, deployment, or rollback.

## Safety and ownership invariants

- There is exactly one authoritative ML-Agents/PPO trainer process. Remote machines run Unity environments only; they never own optimizer state, checkpoints, candidate registration, evaluation, or promotion.
- Remote Unity environments use the native ML-Agents 1.1.0 communicator. No custom trajectory format is introduced.
- External communicator ports bind to `127.0.0.1` on the trainer. Remote machines reach them through SSH local forwarding; do not expose ML-Agents gRPC ports directly to a network.
- Public player telemetry is never treated as on-policy PPO experience. It passes authenticated BeesServer quarantine and independent central validation, then may create bounded scenario pressure. PPO learns from fresh rollouts against that pressure.
- An active training run has an immutable player-derived pressure selection. Public data arriving during the run is prepared for the next run rather than silently changing the current run's distribution.

## Automatic Production player telemetry

For Production desktop builds using the live RL controller (`Stage.ActivateHiveMind && Stage.ActivateBrains`), `RlLiveTelemetryRecorder` and `RlLiveTelemetryUploader` install automatically. No capture/upload command-line opt-in is required.

The recorder samples eligible user-controlled policy ships at the normal RL decision cadence. It records the shared policy observation plus movement, turret aim, fire, and the special-action branch. Successful ship-special/mining/healing/warp events call the recorder at the existing authoritative capability-event boundary so those actions are not inferred from aftermath.

Telemetry is split into bounded segments of at most 64 decision records. Drafts are durable below:

```text
Application.persistentDataPath/RlLiveTelemetry/PolicyV8/
  Draft/
  Pending/
  Invalid/
```

Completed segments move to `Pending`. The uploader removes a pending file only after BeesServer confirms immutable archival (including an identical duplicate). Network, authentication, and rate-limit failures leave the local backlog for a later retry. Locally malformed/incompatible payloads move to `Invalid` instead of consuming server quota repeatedly.

Public `agent_key` values are opaque per match (`agent-000`, `agent-001`, ...). Side, runtime ship ID, Steam user ID, and another stable cross-match player identifier are not encoded into that key. The server keeps authenticated user identity only at the quarantine boundary; the central store retains a store-local contributor bucket for diversity/rate controls.

Automatic telemetry can be disabled for a build/run with:

```text
--rl-disable-automatic-telemetry
```

This is an operational opt-out only; it does not change the policy ABI.

## BeesServer quarantine

BeesServer must run the RL telemetry upload service and have:

```text
BEES_RL_TELEMETRY_UPLOAD_DIR=<durable-server-directory>
```

The client uses the authenticated `rl-telemetry-begin`, `rl-telemetry-chunk`, and `rl-telemetry-complete` WSS requests. The server enforces the frozen ABI/signature, size/rate limits, connection/user ownership, SHA-256, and immutable quarantine metadata. Accepted batches remain `readyForIngestion=false`; server acceptance is not training approval.

The central trainer must be able to read that quarantine root (directly or through an operator-controlled synchronized/mounted copy). It revalidates payload bytes, model/deployment identity, frozen policy compatibility, observation/action shapes, action bounds, and contributor provenance independently.

## Automatic public tactic processing

Process the current quarantine once with:

```powershell
python Training\bees_continual_public_auto.py `
  --root="F:\RLDemo\BeesContinualV8" `
  --telemetry-quarantine="D:\BeesRlTelemetry" `
  --minimum-occurrences=1 `
  --minimum-contributors=1 `
  --total-target-fraction=0.10
```

Strict-valid batches are automatically approved **only for offline tactic mining**. That approval never sets `trusted_for_on_policy_rl` and never authorizes recorded player actions as PPO trajectories.

The current default thresholds are deliberately `1` occurrence and `1` contributor so one tester can exercise the entire capture -> upload -> quarantine -> validation -> mining -> scenario-pressure path. This is a testing-oriented default, not a claim that one player's tactic is statistically representative. Once public traffic is large enough to provide diversity, raise these thresholds (for example through the command-line options) before relying on player-derived pressure as a broad population signal. All other quarantine, schema, model/deployment, contributor-cap, scenario-cap, and fresh-PPO safeguards remain active.

Up to 32 mined tactic signatures may be activated at once because each is registered in both Bee/Human orientations and Unity's player-derived pressure registry supports at most 64 scenarios. The default total automatic target fraction is 10%; the existing player-derived pressure layer still enforces its 50% combined hard cap.

Because public `agent_key` identity deliberately does not assert a trusted Bee/Human side, automatic processing registers both possible orientations at equal share rather than trusting a client-side side claim.

Automatic state is published under:

```text
<continual-root>/metadata/automatic-public-learning/
  current.json
  generations/auto-public-<content-id>.json
```

Generation records are immutable/content-addressed. `current.json` is only the pointer to the latest validated generation.

## One-machine automatic continual training

Use `bees_continual_auto_train.py` to process public telemetry, freeze current pressure into the run, start the normal continual trainer, and keep scanning quarantine for the next run:

```powershell
python Training\bees_continual_auto_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-public-v8-001 `
  --num-envs=4 `
  --continual-root="F:\RLDemo\BeesContinualV8" `
  --continual-game-build="2026.09.13" `
  --continual-public-telemetry-quarantine="D:\BeesRlTelemetry" `
  --continual-public-watch-seconds=30 `
  --env-args `
  --rl-matchup-mode=sampled `
  --rl-ships-per-side=1 `
  --rl-bee-ship-types=Wasp,Hornet `
  --rl-human-ship-types=Gunship,Frigate
```

Keep `--env-args` as a separate token and place it after all Python/trainer/wrapper options. ML-Agents treats its remaining tokens as Unity arguments.

The initial automatic scenario selection is recorded through the same immutable adversarial-run selection boundary as manually reviewed pressure. Newly arriving data found by the background watcher does not change that active selection; start/resume under a new run ID to consume the newer automatic generation.

## Multi-machine rollout training

External workers are a suffix of ML-Agents worker IDs. For eight total environments with four remote environments, workers `0-3` are local and `4-7` are external.

The central machine must specify both the external count and a session-spec output path:

```powershell
python Training\bees_continual_auto_train.py Training\rl_1v1_config.yaml `
  --env="F:\RLDemo\Bees RL Training" `
  --run-id=bees-public-dist-v8-001 `
  --num-envs=8 `
  --base-port=5005 `
  --bees-external-envs=4 `
  --bees-remote-spec="F:\RLDemo\remote-specs\bees-public-dist-v8-001.json" `
  --continual-root="F:\RLDemo\BeesContinualV8" `
  --continual-game-build="2026.09.13" `
  --continual-public-telemetry-quarantine="D:\BeesRlTelemetry" `
  --env-args `
  --rl-matchup-mode=sampled `
  --rl-ships-per-side=1 `
  --rl-bee-ship-types=Wasp,Hornet `
  --rl-human-ship-types=Gunship,Frigate
```

Before ML-Agents starts, the wrapper writes the remote spec atomically. The spec content-hashes:

- the assigned external worker IDs;
- base port;
- run ID; and
- the exact final Unity `--env-args`, including automatically injected player-derived pressure.

Copy that JSON file unchanged to each rollout machine. `bees_remote_worker.py` validates its content hash before launching anything. A machine may launch all assigned external workers or an explicit subset; it cannot select an ID absent from the central spec.

Example remote machine running workers `4-7`:

```powershell
python Training\bees_remote_worker.py `
  --ssh="trainer-user@trainer-host" `
  --env="D:\BeesRL\Bees RL Training.exe" `
  --spec="D:\BeesRL\bees-public-dist-v8-001.json" `
  --worker-ids=4-7
```

The helper creates forwards equivalent to:

```text
remote 127.0.0.1:5009 -> trainer 127.0.0.1:5009  (worker 4)
remote 127.0.0.1:5010 -> trainer 127.0.0.1:5010  (worker 5)
remote 127.0.0.1:5011 -> trainer 127.0.0.1:5011  (worker 6)
remote 127.0.0.1:5012 -> trainer 127.0.0.1:5012  (worker 7)
```

It then launches the local Unity executable with each forwarded `--mlagents-port` and the exact `unity_args` from the signed-by-content session spec. If the SSH tunnel or any Unity rollout process fails, the helper stops the local worker group instead of leaving a partially connected topology running.

Use the **same training build** on every machine. The session spec pins arguments but does not currently hash an entire Unity build directory; build distribution remains an operator/deployment responsibility.

For distributed continual learning without automatic public telemetry, use `Training/bees_continual_distributed_train.py`. For non-continual ML-Agents training, use `Training/bees_distributed_mlagents_learn.py`. Both use the same `--bees-external-envs` + `--bees-remote-spec` contract.

## Promotion and deployment remain unchanged

Distributed rollout workers and automatic public telemetry do not bypass the existing candidate/champion gates. Stable exports are still registered by the continual trainer, and promotion still requires the authoritative current-champion comparison, complete historical regression coverage, permanent competency suite, behavior sanity evidence, and runtime compatibility checks. Champion publication, hot distribution, health monitoring, and guarded rollback remain the existing release path.
