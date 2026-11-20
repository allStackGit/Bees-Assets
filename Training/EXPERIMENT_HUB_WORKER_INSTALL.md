# Experiment Hub worker installer — TinyBrains and Courier

This is the native Experiment Hub counterpart to `bees-remote-worker`. It
does **not** run the old Unity/Bees actors or replace an existing Hub agent.
Install **one agent per worker machine** with the CPU-only projects you need:

- `tinybrains`: authenticated official-engine rollout collector, two independent
  listener slots (12134 and 12135); one slot can join each Halifax GPU learner
- `courier`: authenticated Courier lineage/shared-policy collector, two
  independent listener slots (13134 and 13135)
- `tinybrains,courier`: both, in separate Python environments, sharing one
  machine's CPU/RAM ledger and native process supervisor

The installer uses a filtered checkout of this Hub branch, the matching
TinyBrains `feat/linux-tinybrains-runtime` and Courier
`feat/hub-independent-courier-ppo-20261119` branches. It installs CPU-only
Torch separately for each project. TinyBrains uses the published v0.5.0
official engine CLI with its upstream SHA-256 release verification and pinned
`Tiny-Brains/ants` / `ants-starter` source versions. No alternate game
simulation is provided.

## One-time approvals (not automated by an enrollment token)

1. Connect Halifax and the remote worker to the same **private Tailscale
   tailnet**. The installer installs Tailscale where absent, but the user
   must sign in; authorization is never silently bypassed.
2. On Halifax, keep an SSH server accessible **only to trusted peers**, with
   its host ED25519 key available to verify. The installer displays the
   scanned fingerprint and requires comparison with the output of
   `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` *on Halifax*.
   It then requests normal interactive SSH authorization to add the
   worker's dedicated public key. Only key-based SSH is used thereafter.
   The automatic port forward is strictly
   `127.0.0.1:17149 -> Halifax:127.0.0.1:7149`. Hub administrator
   port 7148 is **never** forwarded or exposed.
3. In **Hub → Machines**, register the worker **once** with its actual CPU
   and RAM capacity and click **Enroll**. The installer prompts for the
   one-time 64-character token without echo and keeps it in a private
   machine-owned environment file. It does not need an admin API token.
4. Generate and protect matching project keys on Halifax; install *exact
   copies* on the worker, using a trusted private transfer. Example Halifax
   creation (run only if no existing approved keys are in use):

   ```bash
   umask 077
   mkdir -p "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains"
   openssl rand -hex 32 > "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains/rollout.key"
   openssl rand -hex 32 > "$HOME/ai/experiments/experiment-hub/private-rollouts/courier.key"
   chmod 600 "$HOME/ai/experiments/experiment-hub/private-rollouts/"*.key \
     "$HOME/ai/experiments/experiment-hub/private-rollouts/tinybrains/rollout.key"
   ```

   The TinyBrains key must be in a separate private directory containing
   `rollout.key`; the Courier key is a 64-character hexadecimal string.
   Never paste these keys or the enrollment token into Hub configuration,
   experiment drafts, version control, or chat.

## Windows remote worker, such as Exeter

Save only `Training/experiment_hub_worker_install.ps1` from this branch
onto the worker (or use an existing checkout). The script fetches the trusted
Hub and training sources itself using the operator's GitHub access. Open
**elevated PowerShell** in the saved file's directory. Replace the placeholders
with the actual machine ID, Halifax Tailscale IP and pre-transferred private
key file paths:

```powershell
.\experiment_hub_worker_install.ps1 -Command install `
    -Projects 'tinybrains,courier' -InstallRoot 'B:\HubWorker' `
    -MachineId 'exeter' -HubSshTarget 'halifax@100.x.y.z' `
    -TinyBrainsKeyFile 'B:\Private\tinybrains-rollout.key' `
    -CourierKeyFile 'B:\Private\courier-rollout.key' -Start
```

If already present, reuse a trusted installed source checkout with
`-TinyBrainsRoot` or `-CourierRoot`. Missing prerequisites are installed
using winget; the helper and Windows service launcher are built *locally*
from the checked-out source with .NET 8 and the native resource-owner binary
is SHA-256 pinned. The service uses the existing Windows LocalService
native owner; sources are read-only to the service, while its private
journal, collector caches, and TinyBrains engine cache are writable.
Installations are initially manual-start; `-Start` opts in to starting
the installed service after all authentication succeeds. Windows Node 22+,
Python 3.11 and administrator approval are required.

The installer can be run again after signing into Tailscale or granting
SSH access. It refuses to overwrite an existing enrollment token or
running native owner; use `-Command update` after all Hub jobs have
terminated and the agent is stopped.

## Linux remote worker

On Debian/Ubuntu, save `Training/experiment_hub_worker_install.sh`
from this branch (or use the checked-out copy):

```bash
bash ./experiment_hub_worker_install.sh install \
  --projects tinybrains,courier --root "$HOME/ai/experiment-hub-worker" \
  --machine-id remote_machine --hub-ssh-target halifax@100.x.y.z \
  --tinybrains-key /private/tinybrains-rollout.key \
  --courier-key /private/courier-rollout.key --start
```

The installer provisions Node, Git, SSH, Tailscale, an isolated Python
3.11.18 via uv, verified TinyBrains CLI, project packages, a private
agent environment and a **user-systemd** native agent service. It does not
enable automatic startup on reboot or replace a preexisting Hub agent.
For updates, stop active Hub jobs and the agent first, then use `update`
with the same `--root` value. For status, use `status`.

## Enabling Halifax distributed learners

Only after the remote worker advertises both private endpoints, edit
Halifax's existing `~/.config/experiment-hub/agent.env` (mode 0600).
The trusted Halifax adapter examples now support these optional settings:

```text
EXPERIMENT_HUB_TINYBRAINS_REMOTE_ENDPOINTS=100.EXETER.IP:12134,100.EXETER.IP:12135
EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY=/home/halifax/ai/experiments/experiment-hub/private-rollouts/tinybrains
EXPERIMENT_HUB_COURIER_REMOTE_ENDPOINTS=100.EXETER.IP:13134,100.EXETER.IP:13135
EXPERIMENT_HUB_COURIER_REMOTE_KEY_PATH=/home/halifax/ai/experiments/experiment-hub/private-rollouts/courier.key
```

Replace `100.EXETER.IP` with the real Exeter Tailscale IPv4 address.
The TinyBrains Docker learner must read the private `rollout.key` through
its already approved Docker runtime. Both machine adapters validate private
HMAC fingerprints; wrong keys or endpoint lists block native Starts.

**Do not restart Halifax's agent, or either machine's worker agent, while
unresolved native jobs exist.** Update the checkout, reconcile existing
job owners in the Hub, then restart the agent after the new settings are
approved. The standalone Hub coordinator is an independent service.

In the Hub, register the native projects `tinybrains_rollout` and
`courier_lineage`, verify the worker machine's advertised listener slots,
then use **Train → Distributed TinyBrains learner + remote collectors**
or **Train → Distributed Courier training**. Each group requires its own
explicit reservations and Start authorization. For the two-run TinyBrains
experiment, select 16 Halifax CPU threads and one exclusive GPU per
learner, plus 6 Exeter CPU threads per remote collector (one listener
slot per learner). Nano/Micro evaluations remain configured separately
on the two learner configurations.

### Limits and safeguards

This is a source installer, not a signed distribution. GitHub access to
private repositories, tailnet login, SSH identity verification, worker
enrollment, private rollout key transfer, real native preflight and
experiment Start all require operator action. Windows and Linux production
operation have **not been qualified** by completing two real distributed
training runs. No script starts experiments, bypasses OS resource limits,
releases uncertain native owners, or claims that installation alone proves
training throughput. All implementation updates are explicit and
fast-forward-only; runtime journals and imported checkpoints are retained.
