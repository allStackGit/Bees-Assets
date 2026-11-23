# Halifax Unity simulation worker builds

This builds the existing Unity RL simulation independently of the native
rewrite and the running native-only Experiment Hub.

## Project layout

Keep the existing Unity project's matching Packages and ProjectSettings
directories. Do not substitute a blank project's package dependencies.

```text
~/ai/projects/bees-unity/
  Assets/              # allStackGit/Bees-Assets checkout
  Packages/            # original Unity project packages
  ProjectSettings/     # original Unity project settings
  Builds/              # compiled workers, outside Assets
  Logs/                # Unity build logs, outside Assets
```

Install the Unity Editor version specified by ProjectSettings/ProjectVersion.txt
(currently 6000.5.4f1), including Linux Dedicated Server Build Support.
Install Windows Build Support to build Windows workers on Halifax.
The command discovers a Unity Hub editor under ~/Unity/Hub/Editor or accepts
an explicit --unity-editor path or BEES_UNITY_EDITOR environment variable.

Node.js, Python 3 and Git are required for the wrapper. No ML-Agents Python
environment, ROCm learner, BeesServer or Hub service is needed to compile.

## Transfer the complete Unity project metadata

The Bees-Assets repository contains the Unity Assets tree, not the matching
Packages/ and ProjectSettings/ folders. On the original Windows Unity machine,
run this from the checked-out Assets directory to create a private bundle:

```powershell
python Training\bees_unity_project_metadata.py export --unity-root B:\Bees --output B:\Bees\unity-metadata.zip
```

Copy this ZIP privately to Halifax. After cloning the same Bees-Assets
branch as Assets/ in the directory above, import the missing sibling folders:

```sh
python3 Assets/Training/bees_unity_project_metadata.py import \
    --unity-root "$HOME/ai/projects/bees-unity" \
    --bundle /path/to/unity-metadata.zip
```

This copies only Packages and ProjectSettings, validates individual file
hashes and refuses to overwrite existing project metadata. Unity's Library,
Temp, local user settings, build outputs, and the Git-managed Assets source
are not part of the transfer. Use a private file transfer: project settings
can contain project-specific identifiers or configuration.

## Compile without changing training state

From the Unity project's Assets directory:

```sh
node Training/bees_build_workers.js --platform linux
# With Windows Build Support installed:
node Training/bees_build_workers.js --platform both
```

Artifacts appear under ../Builds/OfflineWorkers/<build-id>/, including
Linux/ and/or Windows/ Unity players, canonical worker ZIPs, and
worker-build-manifest.json with hashes, source commit, Unity version,
platform, build identity and dirty-worktree indicator.

Close the project in the Unity Editor before a batch build. An active
project lock is checked on Linux as well as Windows. Unity logs appear
under ../Logs/Build/.

IMPORTANT: Compiling workers does not change the active Bees release, run,
checkpoints, remote installations or Experiment Hub.

## Deploy through the existing Windows Bees cluster

Build both platforms from a clean, committed Unity source revision on Halifax:

```sh
node Training/bees_build_workers.js --platform both
```

Copy worker-build-manifest.json, rl-windows.zip and rl-linux.zip together
from the generated OfflineWorkers/<build-id>/ folder to a private incoming
folder on the Windows central Bees machine (for example Exeter). Sync that
machine's Bees-Assets checkout to the exact source commit before importing.
Keep its matching Unity Packages/manifest.json and ProjectSettings.

From the existing Windows Bees Unity project root:

```powershell
.\Assets\bees.ps1 build -OfflineWorkers 'B:\Bees\Incoming\offline-unity-ID\worker-build-manifest.json'
.\Assets\bees.ps1 start
.\Assets\bees.ps1 status -Once
```

The importer refuses uncommitted sources, mismatched commit/version/packages,
missing platforms, changed archive hashes and unsafe archive entries.
It copies both worker builds into immutable release storage, runs the normal
Bees run compatibility checks and compiled Windows environment validation,
then stages/distributes the release using the existing Bees control plane.
Use -NewRun only when an intentionally fresh training lineage is required.

This transitional route uses the existing Windows Bees learner and
worker deployment. **The Windows learner still needs its existing Unity Editor
for champion policy hot-bundle publication**, even though simulation workers
are compiled on Halifax. It is not safe to remove that Editor until hot-bundle
building runs on Halifax or is independently relocated.

The separate Halifax ROCm learner is implemented as a configured Python
environment option, but still requires runtime qualification.

## Compile current champion policy bundles on Halifax

The existing Unity hot-reload builder can run under Halifax's Editor as well.
First stage the **current, approved** Bees deployment model and matching
\`BeesRL1v1Deployment.json\` into \`Assets/Resources/RlPolicy/\` (the same two
resources used by the established Windows hot-release process). These files
must contain a full deployment identity and policy ABI/signature. The separate
native Hub \`policy-candidate/\` manifest is *not* a deployment manifest and
cannot be substituted for it.

From the Halifax Unity project's \`Assets/\` directory:

\`\`\`sh
node Training/bees_build_hot_bundles.js --platform both
\`\`\`

This invokes the existing Unity \`RlLivePolicyHotBundleBuilder\` separately
for WindowsPlayer and LinuxPlayer, verifies the compiled bundle and sidecar
against the staged deployment manifest and model bytes, and creates
\`../Builds/OfflineHotBundles/<build-id>/\` with the two platform-specific
folders and an unsigned, unpublished build manifest. Windows and Linux
Build Support must be installed for the selected targets.

Copy the platform folder with \`bees-rl-policy.bundle\` and
\`bees-rl-policy.metadata.json\` to the machine owning the authoritative
continual-learning store. **Only that owner** should run the existing
\`bees_continual_hot_bundle.py\` publisher with its configured
\`--root\`, \`--bundle-metadata\`, and \`--distribution-root\` arguments.
It rechecks the deployment, model hash and policy ABI before publication.
This workflow allows Unity compilation on Halifax without requiring
a Windows Unity Editor for that step; the running Windows manager remains
the authority for its current network and distribution.

## View and control Bees from the Halifax Experiment Hub

The current native Hub remains the scheduler for TinyBrains and Courier. To
use the same dashboard for Bees, **keep the original Bees Experiment Manager
running on the Windows learner** (do not start two managers against its data).
That Bees manager already owns run continuity, environment validation,
training controls, remote worker rollout and evaluation.

Set up a private SSH local port forward from Halifax to the Windows machine's
existing loopback-only Bees manager on TCP 7148:

```sh
ssh -N -L 127.0.0.1:7158:127.0.0.1:7148 <Exeter-SSH-hostname>
```

To allow a browser on Halifax to open the full original Bees manager UI
through this tunnel, also set `BEES_EXPERIMENTS_FORWARDED_PORT=7158`
on the **Windows Bees manager** before its next safe service start/restart.
The manager still binds only to its original loopback listener on 7148;
the forwarded Host and Origin are allowlisted explicitly. The Hub's Bees
panel then provides a link to the original experiment designer, queue,
evaluation controls, and logs. Without this optional setting, the Hub's
embedded Bees status and control bridge still works.

Configure `EXPERIMENT_HUB_BEES_BRIDGE_PORT=7158` in Halifax's **standalone
Hub systemd service environment**, then restart the coordinator during a
safe idle window. Port 7158 is the local SSH listener, not a public web port.
Keep the tunnel authenticated and persistent with the operator's preferred
service management. The bridge rejects remote hosts, arbitrary proxy
paths and overlapping Hub ports.

The Hub's **Bees Unity training — external coordinator** panel can:

- Read authoritative Bees training state, workers and run history.
- Gracefully stop and resume Bees or explicitly start a new optimizer run.
- Import a completed Halifax worker build from the manifest path **as seen on
  the Windows Bees manager**. The worker ZIPs must already have been copied.
- Show status and error information without automatically retrying a command
  whose outcome is uncertain.

The native Hub does not acquire Bees leases or schedule Bees workers. Its own
registry and current TinyBrains/Courier jobs remain unchanged. The bridge is
disabled by default if the environment variable is absent.


## Prepare the Halifax ROCm learner without Unity

Use a dedicated **Python 3.10** environment with the matching
\`mlagents==1.1.0\` and \`mlagents-envs==1.1.0\` Python packages, NumPy below 2,
ONNX Runtime, psutil, and a ROCm-enabled PyTorch build compatible with the
installed ROCm stack and Radeon GPUs. Refer to AMD's ROCm PyTorch installation
matrix before selecting a torch wheel; do not apply the CUDA-only Windows
requirements file to this environment.

From Assets/:

```sh
export BEES_LEARNER_PYTHON=/absolute/path/to/rocm-python310/bin/python
node Training/bees_learner_preflight.js --device cuda
```

The probe checks the ML-Agents package versions, Python version,
required imports, NumPy compatibility, accelerator availability, and
reports the PyTorch backend. ROCm appears as device `cuda` with backend
`rocm` in PyTorch. A CPU-only preflight is available with `--device cpu`.

Use `BEES_PYTHON` for a machine-local bootstrap Python override when the
tracked cluster config's `python` command does not exist. The external
learner environment is *never modified or pruned* by the Bees operator.
The existing Windows CUDA learner environment remains managed as before.

This is dependency preflight only; it does not assert successful PPO training,
checkpoint export, two-GPU utilization, or distributed qualification.

## Two-GPU learner and local actor on Halifax

Bees has separate managed processes for PPO updates (central learner) and
local simulation/inference rollouts (local actor). For the two Radeon GPUs,
assign them to different process owners in the operator environment:

```sh
export BEES_LEARNER_PYTHON=/absolute/path/to/rocm-python310/bin/python
export BEES_GPU_BACKEND=rocm
export BEES_LEARNER_GPU_INDEX=0
export BEES_LOCAL_ACTOR_GPU_INDEX=1
export BEES_LOCAL_ACTOR_TORCH_DEVICE=cuda
node Training/bees_learner_preflight.js --device cuda --gpu-index 0
node Training/bees_learner_preflight.js --device cuda --gpu-index 1
```

With a canonical Bees training release already staged, enable its existing
managed local actor using `node Training/bees_operator.js runtime
--local-training --learner-device cuda` from Assets/. Use the normal
`bees_operator.js start` command to start a stopped run. Do not run two
independent Bees coordinators against the same release/run state.

Each process receives one GPU through `ROCR_VISIBLE_DEVICES` (or
`CUDA_VISIBLE_DEVICES` for an explicitly selected NVIDIA backend).
Bindings are part of the managed process configuration hash: a changed
assignment requires normal graceful replacement rather than silent reuse.
The local-actor GPU and the learner GPU are both checked with a real
matrix operation before a new pinned process starts. Keep these variables
in the persistent operator service environment; a shell export does not
affect an already-running service.

This separates PPO training from local actor GPU inference. It does not
replicate the PPO optimizer across both GPUs or guarantee a throughput
increase when rollout inference is CPU- or communication-bound. The
native Hub still needs admission of these GPU claims before co-scheduling
other projects on Halifax.

## Run independent Bees jobs in the native Experiment Hub

The first-class \`bees_native\` project uses the Hub's existing resource
reservations and machine-owned Linux systemd supervisor. This mode starts
independent Unity MA-POCA jobs and does **not** use the legacy centralized
Bees manager or require its training queue to be running.

After a Linux Unity dedicated-server worker has been compiled and the
Python 3.10 ROCm/ML-Agents environment is installed, set the following
on the enrolled Halifax **Hub agent service**:

```text
EXPERIMENT_HUB_WORKER_PROJECTS=tinybrains,courier,bees_native
EXPERIMENT_HUB_BEES_ASSETS_ROOT=/absolute/path/to/Bees-Assets-checkout
EXPERIMENT_HUB_BEES_PYTHON=/absolute/path/to/rocm-python310/bin/python
EXPERIMENT_HUB_BEES_LINUX_WORKER=/absolute/path/to/Bees RL Training.x86_64
EXPERIMENT_HUB_ENABLE_ROCM=1
```

Retain the agent's existing machine ID, enrollment identity, local root,
GPU inventory and cgroup-v2 configuration. The local adapter independently
checks the installed Unity worker, trainer YAML, ML-Agents entrypoint and
Python. The Hub displays the approved worker/trainer SHA-256 pins in its
Bees Unity (native training) Design tab. Create a configuration there,
allocate up to 64 Unity environments within the assigned CPU count, and
request either a CPU-only lease or **one exclusive GPU**. The same verified
claim is enforced at native Start, with the approved physical ROCm PCI ID.
Native Bees runs receive dedicated output directories, protected communicator
port blocks and independent model/checkpoint hashes. Their reported PPO
statistics are observational, not evaluation success.

Native Hub Bees Unity jobs preserve the legacy learner/actor separation.
Without WAN actors, they run the learner and all environments locally.
Independent post-training evaluation supports the versioned four-case and
48-case scripted coverage suites. To add Hub-reserved remote Unity actors,
use the independently owned WAN group below.

### Native Hub WAN training: Linux learner, Linux or Windows actors

The native Hub runs one authoritative Bees MA-POCA learner (Linux CPU/ROCm)
and 1–8 separately resource-leased Unity simulation/inference actors. Actors
can run on Linux (CPU or one exclusive ROCm GPU) or Windows (CPU). PPO,
optimizer, checkpoints and post-training evaluations belong only to the
learner. This mode does not alter the legacy Bees Experiment Manager.

**Build identity.** A Linux and Windows Unity player legitimately have
different binary hashes. Build both from one source checkout using
\`node Training/bees_build_workers.js --platform both\`, then transfer the
Linux/Windows worker build directories and the exact, unedited
\`worker-build-manifest.json\` to their respective machines. The manifest
records the complete player-tree hash of each platform and provides a shared
\`wan_release_sha256\`. Each agent verifies its own installed player tree
and the shared manifest at enrollment and Start. For Linux-only groups, the
exact complete-player hash can identify the common release without a manifest;
mixed-platform groups require the shared two-platform manifest.

**Halifax learner agent** (append to the existing installed agent variables):

\`\`\`text
EXPERIMENT_HUB_WORKER_PROJECTS=tinybrains,courier,bees_native
EXPERIMENT_HUB_BEES_ASSETS_ROOT=/absolute/path/to/Bees-Assets
EXPERIMENT_HUB_BEES_PYTHON=/absolute/path/to/python310/bin/python
EXPERIMENT_HUB_BEES_LINUX_WORKER=/absolute/path/to/Bees RL Training.x86_64
EXPERIMENT_HUB_BEES_WORKER_MANIFEST=/absolute/path/to/worker-build-manifest.json
EXPERIMENT_HUB_BEES_WAN_KEY=/absolute/private/path/bees-wan.hex
EXPERIMENT_HUB_BEES_WAN_BROKER_PORT=55051
\`\`\`

Keep the existing machine identity, GPU inventory, ROCm settings and native
supervisor enrollment. The broker listens on **127.0.0.1:55051**, not a
public interface. Only one WAN learner can bind this fixed port at a time.

**Linux actor agent** (can include other installed Hub projects):

\`\`\`text
EXPERIMENT_HUB_WORKER_PROJECTS=bees_rollout
EXPERIMENT_HUB_BEES_ASSETS_ROOT=/absolute/path/to/Bees-Assets
EXPERIMENT_HUB_BEES_PYTHON=/absolute/path/to/python310/bin/python
EXPERIMENT_HUB_BEES_LINUX_WORKER=/absolute/path/to/Bees RL Training.x86_64
EXPERIMENT_HUB_BEES_WORKER_MANIFEST=/absolute/path/to/worker-build-manifest.json
EXPERIMENT_HUB_BEES_WAN_KEY=/absolute/private/path/bees-wan.hex
EXPERIMENT_HUB_BEES_WAN_FORWARD_PORT=56051
\`\`\`

**Windows CPU actor agent:** set the same \`EXPERIMENT_HUB_BEES_ASSETS_ROOT\`,
\`EXPERIMENT_HUB_BEES_PYTHON\`, \`EXPERIMENT_HUB_BEES_WORKER_MANIFEST\`,
\`EXPERIMENT_HUB_BEES_WAN_KEY\`, and \`EXPERIMENT_HUB_BEES_WAN_FORWARD_PORT\`
variables to Windows absolute paths, and use
\`EXPERIMENT_HUB_BEES_WINDOWS_WORKER=C:\path\to\Bees RL Training.exe\`.
Set \`EXPERIMENT_HUB_WORKER_PROJECTS=bees_rollout\` alongside any other
approved installed projects. Windows actors use the Hub's existing
machine-owned Windows Job Object supervisor and must request **zero GPUs**.

The \`EXPERIMENT_HUB_BEES_WAN_KEY\` file contains the same 64 lowercase
hexadecimal characters on all machines. On Linux it must be a private real
file (mode 0600); on Windows restrict it with local NTFS permissions. Do
not commit, upload through Hub configurations, or place SSH credentials in
the repository. The Hub advertises only a SHA-256 fingerprint of the key.

**Private connection.** Linux actors may own their SSH forward within the
same native Hub service. Configure all five additional Linux-agent variables:

\`\`\`text
EXPERIMENT_HUB_BEES_WAN_LEARNER_MACHINE=halifax
EXPERIMENT_HUB_BEES_WAN_SSH_TAILNET_IP=100.100.100.100
EXPERIMENT_HUB_BEES_WAN_SSH_USER=your_learner_ssh_user
EXPERIMENT_HUB_BEES_WAN_SSH_IDENTITY_FILE=/absolute/private/path/id_ed25519
EXPERIMENT_HUB_BEES_WAN_SSH_KNOWN_HOSTS_FILE=/absolute/path/known_hosts
\`\`\`

Replace the sample address with the learner's real Tailscale IPv4. A fixed
SSH port may be configured using
\`EXPERIMENT_HUB_BEES_WAN_SSH_PORT=22\`. The learner-side forwarded broker
port defaults to 55051 and is configured using
\`EXPERIMENT_HUB_BEES_WAN_BROKER_PORT\` when different. Only a pinned host
key and noninteractive key-based SSH login are accepted. The actor runner
starts and supervises the SSH forward, and fails its own Hub service if the
forward exits.

Without the managed SSH settings (including on Windows), provision an
authenticated private forward before starting the group. For example:

\`\`\`sh
ssh -N -o ExitOnForwardFailure=yes -o StrictHostKeyChecking=yes \
    -L 127.0.0.1:56051:127.0.0.1:55051 user@learner-tailnet-ip
\`\`\`

Actor traffic must remain on private loopback and the authenticated broker.
The native learner now **requires all reserved actors to register before
beginning training**; a missing tunnel cannot silently turn the experiment
into a local-only run. The agent's machine heartbeat by itself does not
prove that a manually installed forwarding service is available.

**Use in Hub.** Save a \`bees_native\` Design configuration with
\`wan_actors=N\` and the locally advertised broker, worker and shared-release
hashes. Use the **Bees Unity — distributed WAN training** controls to select
the learner, 1–8 distinct remote actor machines, each CPU/RAM/GPU budget and
Unity environment count. The reservation uses
\`POST /api/hub/distributed/bees\`. The response is a standard
\`resource_bundle\` with no Starts authorized; group Start and Stop use the
existing \`/api/hub/resource-bundles/*\` actions after fresh lease receipts.
All remote actors remain tied to their original learner and private session.
A terminated actor requests a graceful learner Stop/checkpoint; uncertain
native ownership or failed machine leases still require reconciliation.
Automatic forwarding does not install Unity, Python, SSH access, or the
source release on remote machines.

### Portable native policy candidates

A fully completed native Hub job exports `policy-candidate/` under its
Hub job artifact directory. It contains `model.onnx`, a frozen manifest
with the exact Unity worker/trainer/runtime and checkpoint hashes, and a
matching evaluation summary when evaluation was requested.

After exporting the job artifacts from the Hub, verify the candidate locally
on Halifax, Exeter, or any other receiving machine using Python 3.10+:

```sh
python3 Assets/Training/bees_native_policy_candidate.py \
    /path/to/exported-job/policy-candidate
```

Verification checks the portable network bytes, manifest integrity and
evaluation accounting without installing Unity or a GPU learner. It does
not authenticate the origin of the files or authorize promotion. The Unity
player hot-bundle and any live model replacement still require the original
explicit release/compatibility checks. A cooperative checkpoint Stop
preserves optimizer continuation but does not publish a completed candidate.
