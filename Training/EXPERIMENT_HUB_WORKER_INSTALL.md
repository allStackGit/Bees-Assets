# Experiment Hub worker installer — direct Tailscale transport

This is the native Experiment Hub counterpart to `bees-remote-worker`. It
does **not** run the Unity/Bees actors or replace an existing Hub agent.
Each enrolled Windows or Linux worker has **one agent** and may provide:

- `tinybrains`: official-engine CPU rollout collectors, listener slots
  12134 and 12135, one for each independent Halifax GPU learner
- `courier`: authenticated Courier lineage/shared-policy collectors,
  listener slots 13134 and 13135
- `tinybrains,courier`: both, in separate pinned Python environments
  under one machine's native CPU/RAM ledger and process supervisor

The installer fetches this Hub branch and the corresponding TinyBrains
`feat/linux-tinybrains-runtime` and Courier
`feat/hub-independent-courier-ppo-20261119` branches. It installs CPU-only
Torch per project; the official TinyBrains CLI v0.5.0 and original simulation
dependencies are verified and pinned. It does not substitute a simplified
simulation.

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
the Hub still validates each enrolled worker's separate machine credential.
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

## One-time worker approvals

1. Log in to Tailscale on the worker. The installer installs the client if
   absent but does not sign the machine into your private network.
2. In **Hub → Machines**, register the worker with its real CPU/RAM and
   click **Enroll**. The installer prompts privately for its one-time
   64-character machine token; it never takes a dashboard administrator token.
3. Generate and privately transfer matching TinyBrains and/or Courier HMAC
   rollout keys from Halifax. Keep them out of Git, chat and Hub experiment
   configuration. If no approved keys exist, generate them on Halifax:

```bash
umask 077
mkdir -p "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains"
openssl rand -hex 32 > "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains/rollout.key"
openssl rand -hex 32 > "$HOME/ai/experiments/experiment-hub/private-rollouts/courier.key"
chmod 600 "$HOME/ai/experiments/experiment-hub/private-rollouts/"*.key \
  "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains/rollout.key"
```

The TinyBrains private directory must contain `rollout.key`; the Courier
key file must contain exactly 64 hexadecimal characters. Do not regenerate
or overwrite keys already in use.

## Windows worker (for example, Exeter)

Save only `Training/experiment_hub_worker_install.ps1` from this branch
or run it from an approved checkout. In **elevated PowerShell**:

```powershell
.\experiment_hub_worker_install.ps1 -Command install `
  -Projects 'tinybrains,courier' -InstallRoot 'B:\HubWorker' `
  -MachineId 'exeter' -HubTailnetIp '100.97.43.98' `
  -TinyBrainsKeyFile 'B:\Private\tinybrains-rollout.key' `
  -CourierKeyFile 'B:\Private\courier-rollout.key' -Start
```

The installer obtains the Hub and training sources using authorized GitHub
access. It provisions Python 3.11, Node 22+, .NET 8, separate project runtimes,
and the native Windows LocalService Job Object owner. It does not install
OpenSSH or create an SSH tunnel. Service installation is manual-start
unless `-Start` is supplied. Existing owned jobs must finish or be reconciled
before changing the service.

## Linux worker

On Debian/Ubuntu, save `Training/experiment_hub_worker_install.sh`:

```bash
bash ./experiment_hub_worker_install.sh install \
  --projects tinybrains,courier --root "$HOME/ai/experiment-hub-worker" \
  --machine-id remote_machine --hub-tailnet-ip 100.97.43.98 \
  --tinybrains-key /private/tinybrains-rollout.key \
  --courier-key /private/courier-rollout.key --start
```

The script installs Git, Node, Tailscale if missing, a pinned isolated Python
3.11.18 via uv, approved project packages, the private agent environment and
one user-systemd service. It does not enable reboot startup automatically,
install SSH or alter a separately enrolled Hub agent.

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

## Limits

The installed software has not yet passed full real-machine qualification
of simultaneous TinyBrains/Courier distributed runs. Host firewall/Tailscale
access policy, operator enrolment, rollout key transfer, GitHub access, native
preflight and experiment Start remain intentional approvals. GPU compute/VRAM
quotas are not enforced by a TCP tunnel or by Tailscale. No installer claims
throughput or releases uncertain native owners.
