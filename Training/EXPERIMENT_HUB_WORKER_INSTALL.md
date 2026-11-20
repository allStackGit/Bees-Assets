# Experiment Hub worker installer — direct Tailscale transport

This is the native Experiment Hub counterpart to `bees-remote-worker`.
One enrolled Windows or Linux machine runs **one permanent Hub agent**,
even if no simulation is installed. Selecting an eligible machine for a
TinyBrains or Courier job requests that simulation automatically; the
trusted worker updater installs its independently versioned runtime,
dependencies and native adapters. The Machines tab also offers explicit
per-project installation.

The coordinator publishes the Hub, TinyBrains
`feat/linux-tinybrains-runtime`, and Courier
`feat/hub-independent-courier-ppo-20261119` branches. Halifax packages the
pinned upstream TinyBrains source and verified official v0.5.0 engine for
both operating systems. Workers do not need Git, GitHub credentials or
a simplified replacement simulation.

## How transport works

No SSH server, SSH keys, tunnel or public router port forwarding is required.
Both Halifax and the worker **must be signed in to the same authorized
Tailscale network**. The standalone coordinator is explicitly configured to
listen on:

- Administrator dashboard: `127.0.0.1:7148` only
- Local machine-agent API: `127.0.0.1:7149` (preserved for Halifax itself)
- Remote machine-agent API: `100.97.43.98:7149` on Halifax's Tailscale
  interface only, when explicitly enabled

The listener rejects wildcard, public and unrelated LAN addresses. Its remote
HTTP routes remain limited to machine-token-authenticated heartbeats, leases,
independent evaluation worklists and bounded artifact transfers; it does not
expose administrative routes. Tailscale supplies the encrypted transport;
the Hub also validates each enrolled worker's separate machine credential.
Restrict access to Halifax TCP 7149 to approved workers in the Tailscale policy
and, where appropriate, the host firewall. Tailscale membership alone is not
an authorization to execute experiments.

### Enable Halifax's direct listener

After `tailscale status` confirms Halifax is signed in at `100.97.43.98`,
configure `EXPERIMENT_HUB_AGENT_TAILNET_IP=100.97.43.98` in the **standalone
coordinator's own process environment**. If running it manually, the command is:

```bash
cd "$HOME/ai/projects/experiment-hub/Assets"
EXPERIMENT_HUB_AGENT_TAILNET_IP=100.97.43.98 \
node Training/experiment_hub_standalone.js \
  --data-root "$HOME/ai/experiments/experiment-hub"
```

If the standalone coordinator is systemd-managed, put
`Environment=EXPERIMENT_HUB_AGENT_TAILNET_IP=100.97.43.98` in the **existing
coordinator service's** `[Service]` section, then reload systemd and restart
that service only when the Hub's native owners and jobs are safely reconciled.
Do **not** run a second coordinator against the same data root. Existing local
Halifax agents keep `EXPERIMENT_HUB_URL=http://127.0.0.1:7149` without
changes. Verify the new remote listener locally using `ss -ltn`; it must
show `100.97.43.98:7149`, `127.0.0.1:7149` and
`127.0.0.1:7148`, never `0.0.0.0:7148`.

The Tailscale address must actually belong to the local `tailscale0`
interface. If it is unavailable or incorrectly configured, standalone
startup fails instead of opening another interface.

## One-file worker installation

Use one installer per machine. It installs Tailscale if needed and displays the
normal Tailscale browser sign-in link. **Completing that link is the only manual
enrollment step.** Sign in as the same Tailscale user as Halifax.

The coordinator verifies the new node through its local Tailscale daemon
(`tailscale whois` and `tailscale status`), automatically registers its
machine inventory, generates a private machine token and securely provisions
TinyBrains/Courier HMAC rollout credentials. Only the verified same-owner node
may receive them. A previously registered or active machine is never silently
reassigned; an interrupted first install may retry from the same node.

The worker downloads the current approved, SHA-256-checked private software
archives directly from Halifax's Tailscale listener. **No SSH keys or tunnels,
GitHub login on the worker, Hub enrollment token, pre-copied key files,
manual Machine registration, or install-time project setup are required.**

Halifax stores rollout secrets in its private data directory:
`private-rollouts/tinybrains/rollout.key` and `private-rollouts/courier.key`.
The coordinator initializes a missing secret once and never overwrites one in
use. The worker fetches only the selected simulation's secret through its
machine-authenticated Tailscale release API. The local learner must use these same secrets (or configure the corresponding
`EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY` and
`EXPERIMENT_HUB_COURIER_REMOTE_KEY_PATH` coordinator environment variables
to point to the learner's existing approved secrets).

### Windows worker (Exeter, Warwick, Northumberland, Seagrams)

Download the one-file `Training/experiment_hub_worker_install.cmd` from this
branch and **double-click it**. Windows requests administrator permission
through its normal UAC prompt; no PowerShell command or manual setup is
needed.

Follow the Tailscale browser sign-in link if shown. The installer automatically
enrolls using the signed-in node name (for example `exeter`), installs the
base agent prerequisites, native Windows service and update task, and starts
the agent. It does **not** install Python, uv, TinyBrains or Courier during
enrollment. When the Hub requests one of those simulations, the machine's
updater installs a pinned, machine-local Python 3.11.18 using a checksum-verified
standalone uv executable and prepares the simulation independently. No Git
or machine-wide Python installation is necessary.

### Linux worker

On Debian/Ubuntu download the one-file
`Training/experiment_hub_worker_install.sh` and run:

```bash
bash ./experiment_hub_worker_install.sh
```

Follow the Tailscale browser sign-in link if shown. The script installs the
required software, provisions the agent and creates a user-systemd service with
lingering and an automatic two-minute update timer. It starts the agent without
additional arguments.

The base installer does not accept a project selection. Optional flags can
change its installation directory. For a CPU-only independent job, assign
the machine in Train or a resource bundle: this requests the missing
simulation. For one independent CPU-only job, **Start native job** can be
approved once while installation is pending; the Hub saves that exact
machine/lease/configuration approval and issues the native Start only after
installation, fresh authenticated lease and all native preflight checks
pass. This approval expires after 24 hours and is disarmed on coordinator
restart; it can also be cancelled in Train. A resource bundle can use
**Schedule auto-start when ready** to launch its approved members after
those same gates pass. On a resource-constrained machine, unrequested
simulations are not downloaded.

Native Bees execution is not yet supported by this worker: the unfinished
Unity-independent Bees runtime does not yet expose a trusted Hub training
adapter. Do not confuse provisioning a Bees source release with a runnable
Bees job.

### Update an existing SSH-forwarded worker

First stop and reconcile **all native jobs** and stop the worker's Hub agent.
Run the updated installer with its existing installation root and an explicit
Halifax Tailscale address:

- Windows: `-Command update -HubTailnetIp '100.97.43.98'`
- Linux: `update --hub-tailnet-ip 100.97.43.98`

Both update paths preserve the enrolled machine token, project selection,
source paths, native journal and private rollout keys. They replace the legacy
`EXPERIMENT_HUB_URL=http://127.0.0.1:17149` with
`EXPERIMENT_HUB_URL=http://100.97.43.98:7149` and remove SSH-tunnel
environment settings. They do not create or start experiments.
Previously generated SSH key files may remain in the private installation
directory until the operator deletes them; they are no longer read by the
native Hub agent.

## Distributed learners and evaluators

After the worker advertises its approved listener slots, configure only the
trusted Halifax learner adapter's local private settings:

```text
EXPERIMENT_HUB_TINYBRAINS_REMOTE_ENDPOINTS=100.EXETER.IP:12134,100.EXETER.IP:12135
EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY=/home/halifax/ai/experiments/experiment-hub/private-rollouts/tinybrains
EXPERIMENT_HUB_COURIER_REMOTE_ENDPOINTS=100.EXETER.IP:13134,100.EXETER.IP:13135
EXPERIMENT_HUB_COURIER_REMOTE_KEY_PATH=/home/halifax/ai/experiments/experiment-hub/private-rollouts/courier.key
```

The TinyBrains Docker learner must have access to its approved HMAC
`rollout.key`. Learners and collectors independently validate pinned
machine/lease and key identities. In **Train**, use **Distributed TinyBrains
learner + remote collectors** or **Distributed Courier training**, with
separate explicit resource reservations and Start approvals. For the proposed
two-learner study, select one exclusive Halifax GPU and 16 Halifax CPU
threads per learner, plus 6 Exeter CPU threads per remote collector slot.

Remote scheduled evaluators now also use direct Tailscale. Linux systemd
evaluator generation takes `--hub-host 100.97.43.98 --port 7149 --env-file
/path/to/private/agent.env`; Windows evaluator configuration sets
`"hub_host":"100.97.43.98"` and `"port":7149`. Both require the worker's
own enrollment token. Coordinator-local evaluators retain their existing
localhost settings. Do not forward or expose dashboard port 7148.

## Regenerating the Windows launcher

The source of truth is `Training/experiment_hub_worker_install.ps1`; the
checked-in `Training/experiment_hub_worker_install.cmd` is generated from
that file. To update either installer:

```bash
node Training/generate_experiment_hub_worker_cmd.js --write
node Training/generate_experiment_hub_worker_cmd.js --check
```

Commit the changed PowerShell source **and** generated CMD in the same branch.
The CMD packages the installer for first use; it is not the live worker
runtime.

**Unattended updater (implemented in this branch):** The Windows bootstrap
registers a SYSTEM scheduled task, and Linux installs a lingering user-systemd
timer. Every two minutes a separately installed updater retrieves the
coordinator's machine-token-authenticated release manifest over Tailscale,
downloads complete Git archives with strict SHA-256 and size verification, and
stages them in immutable version-specific directories. It keeps prior
software, native journals, enrollment tokens, private rollout keys and job
data intact. Installed versions and update states appear in **Machines**.
A worker code change regenerates Windows's native owner/service binaries
from reviewed source; Python simulation releases get separate environments.
A failed activation restores the previous service configuration and reports
the failure. No installed worker needs private-repository Git credentials, even during
initial setup.

**Coordinator setup:** The Hub's release publisher pulls approved Git refs
using Halifax's GitHub access. Without additional configuration it publishes
only the worker itself. To publish simulation sources, set
`EXPERIMENT_HUB_RELEASE_SOURCES` in the **coordinator service environment**
to JSON mapping approved existing **local Git checkout paths** and branches:

```json
{
  "worker": {
    "path": "/home/halifax/ai/projects/experiment-hub/Assets",
    "branch": "feat/hub-resource-experiments-20261110"
  },
  "tinybrains": {
    "path": "/home/halifax/ai/projects/tinybrains",
    "branch": "feat/linux-tinybrains-runtime"
  },
  "courier": {
    "path": "/home/halifax/ai/projects/basic-training",
    "branch": "feat/hub-independent-courier-ppo-20261119"
  }
}
```

Replace the example checkout paths with the actual local paths. Only
configured components are published; never put tokens or GitHub credentials
in this JSON. The coordinator fetches approved branches every two minutes,
stages them for remote machines, fast-forwards clean matching local
checkouts at an idle native-job boundary, and restarts its user-systemd
agent/coordinator before authorizing remote activation. A dirty checkout
or failed private Git fetch is not overwritten. Check
`GET http://127.0.0.1:7148/api/hub/software/releases` locally for
published versions and errors.

**Mid-job override:** In **Machines**, explicitly select **Override update**
on the affected machine. Linux can refresh its agent/worker code while
separately systemd-owned native jobs keep running on their original runtime.
Windows Job Objects cannot safely replace active worker processes without
a checkpoint/restart contract; Windows and simulation updates remain staged
until the native job is terminal. A simulation's observation/action/model
contract must not silently change mid-run. **A compatible, checkpointed
mid-job simulation restart/continuation is not yet implemented**, so the
override does not claim to perform one. The coordinator retains native
leases during agent disappearance. Disconnect/reconnect transitions and
affected job IDs are recorded durably; loss of connectivity alone is never
proof a worker's native job stopped.

**Scope:** The installer supports the TinyBrains/Courier native worker.
The original Bees Unity remote worker and the unfinished unified-native
Bees runtime are not automatically converted to this installer, and the
new native Bees simulation is not yet an executable Hub worker project.

## Required unattended update and recovery behavior

These define the intended full behavior. Unattended release staging, deferred
activation, Linux worker-code override, status reporting, source synchronization
and offline history are implemented; checkpointed live simulation migration,
Bees native execution integration and full-machine qualification remain
outstanding.

- New versions of Bees, TinyBrains, Courier and the native Hub worker must
  be published through the trusted coordinator and staged on every
  applicable enrolled machine, including the learner. Private project
  sources must not require interactive GitHub login on each worker.
- **Default:** retain the exact currently running simulation, worker and
  learner versions for all in-flight native jobs. Activate staged versions
  automatically on idle machines after their jobs finish, without
  requiring a new installer. Prevent incompatible new allocations
  during a version change.
- **Explicit override:** the operator can authorize activation while a
  job is running. Update remote workers individually, where their native
  runtime supports checkpoint/reconnection, so unaffected collectors and
  the learner continue training. Preserve job/dispatch identity,
  checkpoints, training progress, resource claims and enrollment.
  A learner/protocol-breaking update requires coordinated versioning and
  explicit confirmation of any unavoidable interruption; never silently
  replace an active process or change a frozen training contract.
- **Machine disconnect:** record a durable lost-connectivity timestamp,
  affected machine and assigned jobs. Do not fail/stop the parent
  experiment or release an uncertain native ownership claim merely
  because a heartbeat expires. Continue on remaining connected workers,
  excluding the offline worker from new allocations. On reconnect,
  record the restored timestamp and reconcile its original native
  dispatch and checkpoint before allowing it to rejoin. A learner
  deprived of all collectors can pause progress; that is not evidence
  that its job has failed.
- An update failure must retain or restore the previous runnable
  version and report its status. The Machines view should distinguish
  current, staged, waiting for job, applying, failed, and offline.

Current code already derives an offline machine status from an expired
authenticated heartbeat and retains resource reservations pending native
proof. This is not a durable outage history or seamless mid-job
upgrade mechanism. Existing automation may disarm when agent continuity
is lost; this must not be mistaken for stopping an already-owned job.

## Limits

The installed software has not yet passed full real-machine qualification
of simultaneous TinyBrains/Courier distributed runs. Host firewall/Tailscale
access policy, operator enrolment, rollout key transfer, GitHub access, native
preflight and experiment Start remain intentional approvals. GPU compute/VRAM
quotas are not enforced by a TCP tunnel or by Tailscale. No installer claims
throughput or releases uncertain native owners.
