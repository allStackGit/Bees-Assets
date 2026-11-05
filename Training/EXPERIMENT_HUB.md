# Experiment Hub foundation

The dashboard remains at `http://127.0.0.1:7148` and now has Design, Train,
Monitor, Compare, and Machines tabs. The existing Bees experiment coordinator,
operator, queue, checkpoint history, release compatibility checks, and evaluations
remain authoritative.

## Currently available

- **Design:** Save, revise, and duplicate project configurations without scheduling
  them. Copy the current Bees editor into a draft, or select **Save as draft**
  directly from the Bees form. Saved Bees configurations use the existing
  validated Bees experiment queue. Other projects can save JSON configurations,
  freeze immutable pending jobs, and inspect future placement without execution.
- **Train:** Preserve existing Bees experiment queue, stage transitions,
  paused-run worker updates, and Unity maintenance rebuilds. For ordinary
  (non-experiment) Bees runs, the browser can start/resume the current run,
  request a fresh run, build an RL release, gracefully stop training, publish
  worker-only updates while stopped, and specify Unity environment arguments.
  These invoke existing operator code. Actions are refused while the experiment
  manager owns a run or has an armed queue. Ordinary runs are observed via the
  existing control plane, not inserted into the experiment database.
  Non-Bees projects can queue immutable jobs, cancel unstarted jobs,
  explicitly authorize locally approved CPU-only Linux or Windows execution,
  request native stops, and inspect native lifecycle reports. Real-host
  qualification is still required before treating either backend as deployable.
- **Monitor and Compare:** Existing Bees live status, performance, benchmark
  reports and research charts remain intact. Non-experiment training is shown
  as distinct from experiment-owned training. For Ants, Compare can plot up to
  four selected Hub jobs against cumulative experiment episodes or environment
  steps and show their most recent learning summaries. These plots are observational only.
- **Machines:** Show read-only Bees worker reports from the existing control
  plane. Manually register project-independent machines (CPU, RAM, GPU capacity)
  and preview CPU/RAM, GPU count, and per-GPU VRAM requirements. A machine
  can be enrolled with a one-time credential and run a read-only heartbeat
  agent on Windows or Linux. Fresh heartbeats cap CPU/RAM placement previews
  to observed values. The Machines table shows configured, freshly reported
  and outstanding claimed CPU/RAM side by side, and flags claims exceeding
  a connected agent's report. These are ledger claims, not measured usage.
  GPU inventory remains manually declared; no agent reserves or isolates
  resources yet.

## Data ownership

`experiment_hub_core.js` persists project definitions, drafts, and machine
inventory in `hub-v1.json` in the selected experiment-data directory. The
legacy `experiments-v1.json` is not migrated, rewritten, or replaced. New Hub
registry writes are atomic and only published in memory after they succeed.
If the optional Hub registry cannot be loaded, Bees experiment management
continues to run independently.

`experiment_hub_resources.js` performs pure admission calculations and
greedy, simultaneous workload placement. Requests include CPU slots, RAM, GPU
count or exact device IDs, and minimum physical VRAM. GPUs default to
**exclusive** allocation (including older reservations). A request may instead
use `gpu_mode: "shared"` with positive per-GPU `gpu_vram_mb` and
`gpu_compute_pct` (1–100), with explicit aggregation of claimed GPU memory and
admission budget. The Machines tab previews a single request or a JSON array
of concurrent workloads. For example, three independent jobs can have
different CPU/RAM footprints and share one physical GPU when their combined
VRAM and compute budgets fit.

`POST /api/hub/plan` accepts `{"jobs":[{"id":"bees_actor",
"resources":{"cpus":4,"ram_mb":8192,"gpu_count":1,"gpu_mode":"shared",
"gpu_vram_mb":8192,"gpu_compute_pct":40}}]}` and returns a placement preview,
**not** a queued job. The planner is greedy, does not optimize all possible
assignments, and does not mutate persistent jobs, reservations, or machines.
It may preview physically registered but unverified machines.

**Capacity budgets are not performance guarantees.** Compute percentages
do not enforce GPU execution time, prevent kernel contention, or establish
ROCm/CUDA quality-of-service. The proposed placement neither starts
processes nor grants hardware ownership. The installed local Linux supervisor can enforce CPU and memory limits
for an explicitly authorized CPU-only job, but a planner preview alone
provides neither execution authority nor GPU isolation. The UI requires explicit confirmation
before operating the Bees learner.

## Immutable, non-executable project jobs

The Train tab for a non-Bees project offers **Queue for future adapter**.
This freezes a selected saved configuration at its expected revision, its
project and name, a SHA-256 configuration fingerprint, and a validated
CPU/RAM/GPU resource request into `hub-v1.json`. A new job has
`status: "waiting_adapter"` and a reason explaining that no trusted executor
or OS-enforced isolation exists. Later edits to a saved configuration do not
change jobs that already reference it. The queue will reject a stale selected
revision rather than silently freezing a newer draft.

The UI can explicitly cancel a waiting job; its status and cancellation time
are retained. It cannot cancel running or unknown jobs through this route.
To continue a completed experiment, save a resume-mode configuration,
select its eligible predecessor in Train, and queue with
`continuation_of_job_id` in `POST /api/hub/jobs`. The Hub will keep
its original machine affinity and artifact lineage. Completion alone
does not schedule another stage or launch a process.
The read-only queued-job placement preview now rehearses the **same
round-robin project fairness, FIFO ordering, connected-agent requirement,
continuation host affinity and best-fit resource selection** as the actual
Hub lease allocator. It accounts for simulated earlier claims in the pass,
but creates no leases, changes no job state and launches no programs.
A later live allocation can differ when machine health or capacity has
changed. The separate general what-if planner at POST /api/hub/plan
continues to use a GPU-first greedy ordering, which is useful for capacity
exploration but is not a prediction of queue scheduling.
The Bees project is deliberately
rejected by this generic queue and continues to use its existing coordinator.

The API is `POST /api/hub/jobs` with
`{configuration_id, expected_revision, resources}`,
`POST /api/hub/jobs/cancel` with `{job_id,confirm_cancel:true}`,
`GET /api/hub/jobs/plan` for a read-only view, and
`POST /api/hub/jobs/allocate` to claim Hub capacity. Jobs are queued
without initial claims. The registry limits outstanding non-Bees jobs to
100 and stored job records to 1,000; it refuses to silently delete old
experiment definitions.

### Atomic advisory job allocation

A currently connected machine can be allocated to a pending job using
`{project_id,job_ids:[...],machine_id,confirm_allocate:true}` (one job
when specifying a machine). Omitting `job_ids` considers all pending
jobs in that project. `{all_projects:true,confirm_allocate:true}` runs
a bounded cross-project round-robin pass, FIFO within each project.
Only the Hub ledger's estimated CPU, RAM and per-GPU budgets are admitted.

Allocation links the job and an `offered` coordination lease in the
**same atomic registry write**. The job remains `waiting_adapter` until a separate, explicitly
confirmed native start authorization. The agent's lease receipt alone
does not prove resource enforcement or authorize any program launch.
The Train tab reports a read-only native Start readiness reason for each
waiting non-Bees job. It uses the same CPU-only, trusted-adapter,
acknowledged-lease, native preflight, and continuation-lineage conditions
as actual start authorization. A "ready" label only enables the
separately confirmed Start action; it does not launch a job. Assigned
jobs remain assigned after coordinator restart or agent disconnection;
there is no automatic reclaim without evidence that workloads have
stopped. The read-only planner displays linked assignments separately
from hypothetical capacity for unassigned jobs, preventing a linked
job from being counted twice.

Cancelling an unstarted assigned job releases its lease in that same
atomic write. Releasing its lease through Machines clears the job's
machine assignment while leaving the job pending. Inconsistent
job-to-lease references fail closed without publishing partial changes.
A disk-write failure likewise publishes neither half of the update.

**This operation does not launch, isolate, pause or stop a process.**
Even an agent-acknowledged assignment is not a binding on independently
running Bees/Ants/LLM software. The Hub does not yet know how much GPU
time is being consumed by untracked workloads, and cannot enforce
a minimum performance share.

No saved project configuration may supply a program path, executable
argument list, shell command or privileged adapter name to this API.
An installed local trusted adapter, explicit dashboard authorization
and verified native CPU/RAM service limits are required before a
job can transition out of `waiting_adapter`.

### Opt-in native CPU-only queue automation

The non-Bees Train tab can explicitly **arm 1–16 selected, enrolled native
machines** using the loopback-only `POST /api/hub/automation` endpoint.
Arming requires a passing local CPU/RAM preflight, a current authenticated
agent instance, installed trusted project adapters, and confirmation that
the operator has accounted for Bees and any other **untracked workloads**
on those machines. The automation session is process-local and starts
**disarmed**, including after every coordinator restart. Disarming does
not stop a running service or release its capacity.

While armed, a coordinator pass every ten seconds performs **at most one
durable transition**: release capacity only after an authenticated native
`finished` or `stopped` report, authorize one CPU-only Start that passes
the same live readiness gate as the manual Start button, or claim available
CPU/RAM budget for an unassigned non-Bees job. Project queues are
interleaved fairly (FIFO within each project). An agent must first
acknowledge each fresh claim. Jobs requesting any GPU remain outside
this automation; existing Bees training remains under its own operator.

A failed or unverified managed job, disappearing/replaced agent, or
failed coordinator operation disarms the session without retrying an
uncertain native launch or freeing uncertain capacity. Stop/recovery of
failed jobs remains manual. This is **implemented orchestration logic,
not qualified unattended execution**: real Linux/Windows host limits,
subprocess ownership, networking, and coexistence must be verified before
arming it for meaningful production work. It does not implement
benchmark-based completion criteria, dynamic live rebalancing, or measured
CPU/GPU performance shares.

## Coordinated resource leases (not process isolation)

The Machines tab can issue a **standalone coordination lease** for a selected
project and a currently connected machine using the CPU, RAM and GPU fields
in **Placement preview**. A queued job can alternatively request a linked
lease using the Train tab. Issuing either requires explicit confirmation. The requested budget is
admitted using the same planner as the multi-workload preview; a new offer
counts against available capacity immediately so concurrent Hub requests cannot
overbook the same registered resources. Leases are persisted in `hub-v1.json`,
not in Bees' `experiments-v1.json`.

A lease goes from `offered` to `acknowledged` when the authenticated agent
reports its ID in a subsequent heartbeat. The agent checks that the sum of
offered CPU and RAM requests does not exceed its reported host capacity
before reporting observation. It does **not** reserve physical cores, cap
memory consumption, measure GPU availability, or start a workload.
Acknowledgement means **received and accounted for**, not *resources enforced*.
`receipt_fresh` is true only when an acknowledgement has been seen recently
during the current coordinator process and that machine's heartbeat is fresh.
Each successful heartbeat replaces that machine's fresh receipt set. The
coordinator independently checks that **all capacity-blocking claims**
(`active`, `offered`, and `acknowledged`, including older claims) still fit
the newly reported CPU/RAM capacity, even if an agent retransmits IDs from
a prior, larger capacity report. Missing IDs or an invalid physical GPU
binding immediately lose freshness, without releasing the durable claim.

A fresh receipt is mandatory for a new native **Start** or GPU device probe.
If reported host capacity falls below existing claims, the agent may stop
renewing receipts. An authenticated native agent may still receive **Stop**
and **Inspect** for a previously dispatched job with an acknowledged,
exactly matching job/project/machine lease. Those directives use the
machine-local ownership journal and cannot launch a workload; no lease is
automatically released or treated as safe to reallocate.

Leases are **not released automatically** when agents disconnect, tokens are
rotated/revoked, or the coordinator restarts: those conditions cannot establish
that an independent process has stopped. The ledger survives restart and
continues blocking Hub capacity until an operator explicitly selects
**Release**. Release does not stop processes; it only returns planning budget.
Old `active` reservations stay protected, and only records issued by the Hub
can be released through this endpoint. A bounded number of released records
is retained; no per-heartbeat record duplication occurs.

The relevant loopback API calls are `POST /api/hub/leases` with
`{project_id,machine_id,resources,confirm_request:true}` and
`POST /api/hub/leases/release` with
`{lease_id,confirm_release:true}`. The authenticated
`POST /api/hub/agent/heartbeat` optionally includes
`observed_lease_ids` and returns inert `lease_offers`. These are
data records, never commands, executable paths, scripts, or model inputs.

This is a scheduler-coordination foundation, **not an executable job
scheduler**. Other processes on the same machine, including existing Bees
workers and interactive LLMs, may consume untracked resources. Treat all
GPU VRAM and compute percentages as estimated budgets until trustworthy
per-device measurement and platform-level isolation are implemented.

## Read-only machine agent enrollment

1. Register a machine with declared CPU slots, memory and GPU inventory in
   **Machines**. These values are planning ceilings, not discovered facts.
2. Select **Enroll**. Copy the one-time 256-bit token into a protected agent
   environment, together with `EXPERIMENT_HUB_MACHINE_ID`. The token is
   never returned in state refreshes, stored in cleartext by the Hub, or
   displayed again. **Rotate** invalidates the previous token; **Revoke**
   disables the agent until a new enrollment.
3. Install `experiment_hub_agent.js` and `experiment_hub_host.js` in the
   same Training directory and start `node Training/experiment_hub_agent.js`
   on that machine using Node.js 18 or later. It sends read-only authenticated
   heartbeats every 15 seconds and reports CPU parallelism, RAM, operating
   system, and read-only native resource-control discovery.
   Set `EXPERIMENT_HUB_AGENT_TOKEN` from the enrollment output. Do not
   commit it or include it in logs or an unprotected shell history.
4. The Hub administrator dashboard listens at `127.0.0.1:7148` but
   **machine agents connect only to the separate `127.0.0.1:7149` listener**.
   This agent-only port accepts authenticated heartbeat/lease-receipt POSTs
   and cannot access any administrator or training route. A remote worker
   needs a trusted SSH local forwarding tunnel, for example mapping its
   local port 17149 to the coordinator's `127.0.0.1:7149`. Set
   `EXPERIMENT_HUB_URL=http://127.0.0.1:17149` on the worker. The agent
   defaults to `127.0.0.1:7149` when running alongside the coordinator.
   **Never forward port 7148 to worker machines**: it controls Bees
   training and the Hub's registration/credentials. The agent accepts
   only loopback HTTP destinations, and neither port should be exposed
   directly on the LAN or Internet.
5. **Connected** means a correctly authenticated process reported recently,
   not that the process has exclusive control of the host. After 45 seconds
   without a heartbeat, it is **offline**. Last-seen time is retained in
   memory until coordinator restart. CPU and RAM observations are capped
   by registered limits. GPU memory and compute budgets are unverified
   estimates until native isolation/monitoring exists.

An agent-port bind failure leaves the existing Bees coordinator operational;
the Hub agent service reports the listener failure and machines remain
unavailable until the port conflict is resolved.

By default the agent is **observation-only**. With an explicit local
opt-in and a trusted installed project adapter, it may process bounded
machine-bound `start`, `inspect` and `stop` directives for CPU-only
jobs. It never accepts a server-supplied executable, shell command or
environment override, and it cannot take over existing Bees training.

## Native execution readiness (discovery, not enforcement)

`experiment_hub_host.js` inspects local OS facilities without modifying
host state. On Linux, it reads the cgroup v2 controller list from sysfs
(`cpu`, `memory`, `io`, `pids`, `cpuset` when present). On Windows it
identifies that the required Job Objects execution backend has not yet
been installed. A detected cgroup hierarchy does **not** prove that
controllers are delegated to the agent, that the agent can supervise a
process safely from creation to termination, or that other workloads
are isolated. The detection does not create or modify cgroups, job
objects, running processes or OS settings.

The agent includes protocol version 1 read-only discovery in its
authenticated heartbeat. The manager accepts discovery only as
`backend:"none"` with native enforcement flags false; that report
describes the host **before any job is launched** and is not a service
attestation. Separately, the agent may explicitly advertise a locally
configured Linux or Windows adapter. Linux uses checked systemd CPU/RAM
limits; Windows uses the locally compiled, SHA-pinned Job Object owner.
Older agents without the installed execution bridge remain observation-only.

A heartbeat, detected cgroup or coordination lease alone **never**
permits execution. A locally opted-in installed project adapter, explicit
administrator start authorization, and successful native service/limit
verification are all required. Native GPU execution remains blocked on
all platforms. Windows CPU-only execution is implemented but has not
been compiled or qualified on a real Windows machine.

## Linux native supervisor and authenticated dispatch (CPU-only)

`experiment_hub_linux_supervisor.js` now provides a **locally invoked**
`LinuxSupervisor` API for executing a trusted adapter inside a transient
**user systemd service**, not in an unmanaged Node child process. This module
is invoked by the machine-local execution bridge only after an operator
explicitly authorizes the job from the dashboard. No queued job starts
automatically because the module exists.

The caller must install a **trusted adapter in local code** for a specific
project. Only that adapter specifies an absolute executable path, optional
working directory, and a function that derives a bounded argv array from
the frozen configuration. Saved JSON cannot supply a shell command,
executable, environment override, or adapter implementation. The
supervisor also verifies the frozen configuration SHA-256 fingerprint,
matching job/lease/machine/project identities, a fresh acknowledged lease,
local CPU and memory budgets, and Linux cgroup v2 availability. The Bees
project is explicitly excluded to preserve the existing training operator.

The opt-in Linux backend uses `systemd-run --user` transient services with
`Type=exec`, `CPUQuota`, `MemoryMax`, group termination and a finite
stop timeout. CPU quota is total CPU time (100% per CPU), **not dedicated
cores**. Only **CPU/RAM-only jobs** are supported: any GPU request is rejected,
including jobs with advisory GPU VRAM or compute budgets. It uses no
privileged shell, no `sudo` and no Windows process fallback.

Before dispatch, the supervisor atomically writes a local launch-intent
record to a separate restricted-permission `supervisor-v1.json` ledger.
After dispatch it checks the exact unit name, description and reported
CPU/memory properties using `systemctl --user show`. **It also checks
the real kernel cgroup v2 `cpu.max` and `memory.max` limits and confirms
that the running main PID belongs to that cgroup.** Merely setting
systemd properties is insufficient to report a service as running.
If the kernel limits are missing or mismatched, the native service
becomes `unverified` and cannot be silently relaunched or have
its allocation released. If an initial launch does not pass kernel-limit verification, the
supervisor records a quarantine state and makes **one nonblocking
systemd stop request** after checking that the unit name and
description exactly match the locally recorded job. A quarantined job
remains `unverified` even if the stop subsequently completes; this
requires operator review because the service may have executed briefly
before the limit failure was detected. A failed/ambiguous cleanup is
reported, never silently retried or assumed successful. Previously
running services with a later limit mismatch are also marked
unverified, but their exact recorded unit can still be stopped from
the dashboard without allowing an unknown unit to be killed. Read-only
native status includes bounded current RAM, accumulated CPU time and task
count when the unit reports them. These are live observations returned by
the local API, **not** a high-volume metrics archive. Identical
observations do not trigger repeated registry writes. A launch timeout,
missing unit, insufficient privileges, or mismatched limit is
**unverified**, not automatically retried. On restart, existing records
may be reconciled with systemd; the module never relaunches them
implicitly. Stopping targets only a recorded and verified service,
persists stop intent before requesting service termination, and uses
systemd group termination rather than killing a guessed PID.

The local ledger uses atomic writes, a short exclusive file lock and
revision checks. If a crash leaves the lock file behind, do **not**
remove it automatically: inspect the recorded units and host before
manually recovering local supervision. No logs, command arguments or
secrets are stored in that ledger. The trusted adapter must not put
secrets in command-line arguments because systemd unit metadata and
process listings can expose them.

### Operator-initiated CPU-only execution

The manager accepts `POST /api/hub/jobs/start` with
`{job_id,confirm_start:true}` only for a frozen non-Bees job holding a
fresh acknowledged lease on a connected Linux agent that has explicitly
advertised the matching *local* installed adapter, CPU/memory cgroup
controllers, **and a passing current-process native preflight**. It atomically records `start_requested` and a unique
dispatch identity before returning a typed directive through the
restricted authenticated agent heartbeat on port 7149.

Execution is disabled by default. On the Linux worker, the machine
administrator must configure **all three local settings**:

- `EXPERIMENT_HUB_ENABLE_EXECUTION=1`
- `EXPERIMENT_HUB_LOCAL_ADAPTERS=/absolute/path/to/approved-adapters.js`
- `EXPERIMENT_HUB_LOCAL_ROOT=/absolute/path/to/private-supervisor-state`

On starting the opted-in agent, the local supervisor first runs an
**inert 30-second /usr/bin/sleep** in a uniquely named transient user
service with a 100% CPU quota and 256-MiB memory cap, reads the
corresponding kernel cgroup limits and process membership, and
requests a blocking systemd stop before reporting readiness.
It uses **no training process**, does not touch Bees or Ants artifacts,
and never runs until the host administrator explicitly enables native
execution. A failure leaves native starts blocked; the agent still
reports machine status and may stop or inspect previously owned jobs.
A failed cleanup requires operator inspection of the named preflight
unit. Qualification is deliberately **not persisted across agent
restarts**. It only verifies the local session; each real service is
separately checked against kernel controls. The coordinator trusts
this authenticated agent report, not independent remote attestation.

The adapter file must export an object keyed by registered non-Bees
project IDs. Each project has an absolute `executable`, optional
absolute `cwd`, and an `args(frozenParameters)` function producing
validated arguments. The machine administrator reviews and installs
this executable separately. Saved experiment configurations never
supply a program path or shell command. **This PR does not install
working Ants, LLM, or other project runners on a machine.** Local
adapter configuration and authentication secrets must not be committed.

The agent generates a fresh process-instance identity when it starts.
While a native agent's authenticated heartbeat is fresh, a different
instance cannot take over its machine identity or receive native
instructions. Replacement agents become eligible after that instance
expires (45 seconds); this does not release job leases or bypass
the deterministic systemd unit and local ownership ledger. The
worker-only endpoint also enforces a 32-KiB request byte limit.

The agent sends a native report only after its local systemd supervisor
inspects exact service identity, systemd `CPUQuota`/`MemoryMax`
properties, kernel `cpu.max`/`memory.max` limits, and process membership.
Repeated launch instructions are reconciled against the durable local
journal, never executed twice. One directive is delivered per heartbeat:
stops take priority, then starts, then GPU device probes, then
inspections. Jobs rotate fairly within each priority level using
independent cursors. Once an authenticated native report confirms local
Stop intent, that job moves into ordinary inspection while it shuts down;
a lost or expired report restores urgent Stop priority. This prevents a
slow shutdown from monopolizing observations of unrelated jobs. The
manager records `running`, `finished`, `failed`, `stopped`, or
`unverified` status from the authenticated machine and exposes
current memory use, CPU time and task count without per-poll history
duplication. Only lifecycle reports accepted by the coordinator update live
telemetry or Ants comparison samples; late state regressions and duplicate
terminal reports cannot overwrite a completed job's last valid metrics.
Ants live learning progress retains its latest accepted source snapshot when
a report repeats or predates it, is for another experiment, or temporarily
omits progress. Fresh native CPU/RAM usage still updates independently.

If the coordinator commits a terminal native report but its HTTP response
is lost, the agent retries that same report. An operator may have already
released the completed job's capacity. The coordinator accepts an exact
matching terminal state from that job's original authenticated machine,
lease and dispatch after release, without restoring the lease, changing
the completed job or adding new live metrics. Any mismatched ownership or
nonterminal late report remains rejected. This receipt recovery does not
authorize another start or silently free capacity.

`POST /api/hub/jobs/stop` with `{job_id,confirm_stop:true}` records
`stop_requested`. If the worker is offline, the request persists
until it reconnects. A verified native stop is required before the
job can be marked `stopped`. For `finished`, `failed`, or `stopped`
jobs, an operator can **Release completed capacity**; the original
machine and lease IDs remain in the completed job history. Active or
unverified jobs cannot release capacity through this route.

Any unknown native unit, lost local ownership record, unmatched
configuration hash, failed verification, or uncertain launch leaves
the job `unverified` without relaunch or automatic capacity release.
This design requires inspection before recovery, rather than treating
a network disconnect as evidence the process ended.

**Real deployment is still unverified.** The lifecycle has been
exercised using isolated HTTP, filesystem, cryptography, and systemd
simulations, not Halifax's Debian user service. Linux systemd
delegation/permissions, real process termination and limits,
Windows Job Objects, enforceable GPU isolation, and cross-project
throughput/rebalancing remain unqualified.

## Physical AMDGPU identity and bounded advisory allocation

The Linux agent now observes AMDGPU cards directly from the kernel's
\`/sys/class/drm/card*/device\` sysfs hierarchy. Reports contain only a
PCI bus/device/function address, total VRAM MiB and (when available)
currently used VRAM MiB. Missing drivers, missing VRAM counters and
disappearing devices are treated as **unavailable**, not inferred from
model names or manually registered nominal capacity. PCI slot addresses
are stable labels only while the machine's hardware topology remains
unchanged; they are not permanent GPU serial numbers. Windows GPU
discovery is not implemented by this Linux-specific probe.

Registration may include a GPU's \`pci_address\`, for example:

    [{"id":"gpu0","vram_mb":32768,"pci_address":"0000:03:00.0"}]

For an already registered machine, the Machines tab provides
**Bind physical AMD GPUs**. The operator selects a connected Linux
machine, reviews the actual observed PCI addresses, and confirms a
complete logical-ID-to-PCI mapping. The server rejects unknown,
duplicate or malformed addresses and refuses to rebind a machine while
it has outstanding GPU-bearing leases. It never silently changes a
running job's GPU selection or existing lease identity.

GPU-bearing coordination leases now persist their exact logical-ID-
to-PCI mapping and observed VRAM capacity at allocation time. Older
leases without a pinned physical identity cannot become trusted merely
because a machine registration was edited later. The frozen device
binding survives coordinator restarts and must still match the
current authenticated hardware report for each acknowledgement.

Only connected machines with a fresh matching AMDGPU report expose
\`verified_gpus\` to the Hub's capacity planner. The available per-device
budget is capped at the **smaller** of registered and reported VRAM;
manual names without matching PCI evidence contribute **zero GPU
allocation capacity**. Each lease freezes its selected logical GPU IDs.
The coordinator sends their expected physical PCI mapping back to the
authenticated agent, which independently verifies the requested device
and tracks exclusive/shared claim budgets before acknowledging. A
missing, moved or mismatched device invalidates the coordinator's
existing fresh receipt immediately. Reappearance alone does not
restore receipt freshness; another matching acknowledgement is needed.

### Linux GPU device-filter qualification (GPU execution still disabled)

The local AMDGPU device resolver maps the frozen PCI identity to
a kernel DRM render character node under /dev/dri. It verifies the
current sysfs PCI address, driver/vendor, exact character-device
major/minor, and current total VRAM. Missing nodes, ambiguous GPU
partitions, moved PCI devices and insufficient VRAM are rejected.

The Linux supervisor now has an optional, **locally invoked**
`qualifyGpuDeviceAccess(bindings)` method. It first proves that the
same ordinary user account can open the selected render device,
an excluded render device, and /dev/kfd. It then starts an inert,
uniquely named systemd user service with DevicePolicy=closed and
DeviceAllow entries for only /dev/kfd and the selected render node.
The fixed Node probe must demonstrate that /dev/kfd and the
selected render device remain openable **while the excluded,
previously openable GPU becomes inaccessible**. Failure, unavailable
systemd eBPF device filtering, an ambiguous unit or unconfirmed
cleanup leaves qualification blocked. The probe opens and closes
device files but does not submit GPU work or run training. Every
explicit job request runs a **fresh** device-access proof, not a
cached pass from a different job. Before launching a probe, the local
supervisor records its unique systemd unit and description in
`<EXPERIMENT_HUB_LOCAL_ROOT>/gpu-preflight-pending.json`. A confirmed
stopped or absent unit clears this record; ambiguous cleanup or an agent
crash leaves it in place and blocks further probes **across agent restarts**.
To recover, inspect the recorded unit with `systemctl --user show <unit>`
from the same Linux account, verify its exact identity and that it is
stopped/absent (stop the matching unit if necessary), and only then
remove the pending JSON record manually. Never clear it on the strength
of a stale agent heartbeat. This first version requires at least two
openable AMD render nodes and one selected GPU per proof.

The Train tab now offers **Check GPU device access** for an unstarted
non-Bees job with an acknowledged, exclusive single-GPU lease on a
qualified Linux agent. The operator must explicitly confirm the
action. `POST /api/hub/jobs/qualify-gpu` records a request identity
bound to that job, lease and machine; the authenticated agent receives
`qualify_gpu` as a separate non-executable order with the lease's
frozen PCI mapping. The agent sends a bounded
`gpu_qualification_reports` heartbeat result independently of all
native execution reports. A negative result, wrong PCI mapping,
released lease, missing agent or stale request cannot authorize a
new process. Releasing or cancelling an unstarted job cancels a
pending request, and a late matching report cannot restore it.

The job's historical probe outcome is durable and visible on the
dashboard, but `gpu_qualification.current` is a computed display-only
indicator requiring the same live agent process instance, currently
acknowledged matching lease and physical device, and passing native
CPU/RAM preflight. It is no longer considered current if the coordinator restarts, agent
restarts, agent goes offline or hardware/lease evidence becomes stale.
A disappearance or mismatched physical GPU observation permanently
marks the previous proof stale, even if the same PCI address later
reappears and the lease is reacknowledged.
A fresh explicit probe is required to obtain current evidence for a
different job; a result for one job never qualifies another.

**These controls are not yet connected to native GPU launching.**
The Hub still refuses GPU-bearing Start requests, even for an
acknowledged exclusive lease. Before enabling them, Halifax must
prove the device filter in its actual systemd user session and
demonstrate ROCm cannot reach excluded hardware through another
interface. Access filtering is not a per-process VRAM limit, a
compute-share guarantee or isolation from unrelated host jobs.

**Identity is not isolation.** The physical VRAM-used counter may
include unrelated host workloads, while the planner's reserved VRAM
and GPU compute percentages represent only the Hub's own advisory
budget ledger. No kernel-enforced per-process GPU VRAM limit,
exclusive device node restriction, guaranteed GPU compute partition,
or ROCm execution device selection has been qualified. The Hub
therefore still **refuses to start GPU-bearing jobs**, even when both
PCI identity and advisory capacity match. These observations make
future scheduling safer; they do not demonstrate that Bees, Ants and
LLM jobs can share a GPU without slowdown.

## Windows native Job Object owner (opt-in, CPU/RAM only)

The Windows backend is provided as **source only** under
\`Training/experiment_hub_windows_owner/\` (a self-contained .NET 8 project).
The machine administrator must review, build and locally install its
helper executable; the Hub does not ship a compiled native binary.
An example build is:

    dotnet publish Training/experiment_hub_windows_owner/ExperimentHubWindowsOwner.csproj -c Release -r win-x64 --self-contained true

Compute the local published executable's SHA-256, and set
\`EXPERIMENT_HUB_WINDOWS_HELPER\` to that executable's absolute path and
\`EXPERIMENT_HUB_WINDOWS_HELPER_SHA256\` to its lowercase 64-character
hash. The existing machine-local execution opt-in, adapter module
and private journal root settings remain required. There is **no
PowerShell dependency** in the Windows process supervisor.

A Windows-local project adapter must return a reviewed executable,
bounded argv, and explicitly specify \`stop_policy:'terminate-group'\`.
The Hub never accepts a path or shell command from configuration JSON.
The Ants-v2 production adapter currently uses Linux-only checkpoint-aware
SIGINT and is **not** approved for this Windows force-stop backend.
Bees training stays under its existing operator, not this helper.

On the explicitly opted-in worker, the helper's inert \`--probe\`
creates only a \`--probe-child\` of itself, suspended and assigned to
a Windows Job Object with CPU and memory limits. It verifies the native
settings and process association; no project runner or experiment
artifact is involved. Successful preflight is process-local, not
durable evidence after restart. The helper's installed SHA-256 is also
rechecked before every job launch.

Each authorized job writes a private one-time request and a durable
ownership record before starting an independent helper. **Configure
the Windows private journal directory with restrictive NTFS access
control for the agent's Windows account.** POSIX-style file modes
alone do not reliably configure NTFS ACLs. Do not place passwords,
API tokens, or other secrets in adapter command-line arguments or
experiment JSON, because process command lines and request files
may reveal them to locally privileged users. The helper
creates a Windows Job Object with job-wide committed memory limits,
a CPU-rate **hard cap**, and kill-on-last-handle-close semantics.
It creates the locally selected workload **suspended**, assigns it
to the limited Job Object, verifies the limits and assignment,
then resumes the workload. CPU slots translate into a share of host
logical processing capacity; they are **not dedicated cores**.
Windows' job memory limit applies to job committed memory, which
is not identical to instantaneous physical RAM usage.

The independent helper owns the Job Object handle for the lifetime
of the workload; when that last handle closes unexpectedly, Windows
terminates the owned process tree. A same-user named pipe accepts
only token-authenticated \`inspect\` and \`stop\` requests, not
commands or alternate executables. Signed terminal receipts allow
the local supervisor to distinguish durable completion from an
unknown or lost helper. Ambiguous starts remain unverified with
one bounded local stop attempt and **no automatic relaunch**.

**Windows Stop force-terminates the process tree** via Job Objects.
It cannot promise checkpoint-safe interruption, so Windows adapters
must explicitly acknowledge that behavior. CPU-only jobs mask
common CUDA/HIP/ROCm selector environment variables, but do not
provide enforceable GPU isolation; GPU requests remain blocked.

**Not yet qualified on actual Windows hardware.** The .NET helper
has not been compiled or executed on a Windows host in this work.
The Node bridge and manager were exercised with simulated Windows
status pipes and filesystem ownership. Build, process containment,
OS-level quota enforcement, session lifecycle, reconnection,
production adapter compatibility and real stop semantics remain
unverified and must not be inferred from the simulations.

## Ants-v2 production adapter (locally installed, CPU-only)

`Training/experiment_hub_ants_adapter.js` exports
`createAntsProductionAdapter(...)`, a locally configured trusted adapter
for the real `python -m antsim train` entry point. It is a factory, **not**
an automatically installed runner. No Ants-v2 source, qualification
gate, reward, observation, PPO, checkpoint, or production curriculum
semantics are modified.

The machine administrator must supply absolute paths for the qualified
Ants checkout, Python 3.12 environment, production YAML, schema-5 final
qualification report, and a dedicated artifact parent directory. The
adapter requires pinned SHA-256 digests of both the YAML and report at
startup **and again before each launch**; it refuses changed files.
The per-job artifact parent **must be outside the Ants source checkout**
to preserve its clean source identity. It passes the report unchanged into Ants' own production training
authorization. That authorization independently requires the exact
current source, configuration and resolved runtime/device lineage
used by the passing qualification report. This adapter cannot authorize
training on its own. A clean and properly qualified **CPU runtime**
is necessary because the Hub's current execution backend does not
allocate GPU resources.

Example local module at `/etc/experiment-hub/approved-adapters.js`
(paths are illustrative and must be changed to installed locations):

```javascript
const {createAntsProductionAdapter} =
    require('/opt/bees/Training/experiment_hub_ants_adapter');
module.exports = {
    ants: createAntsProductionAdapter({
        python:'/opt/ants/.venv/bin/python',
        projectRoot:'/opt/ants',
        configPath:'/opt/ants/configs/qualified-cpu.yaml',
        qualificationReport:'/opt/ants/analysis/qualification-final.json',
        artifactRoot:'/srv/ants/hub-runs',
        configSha256:'<64-hex-digit SHA-256 of the YAML>',
        qualificationSha256:'<64-hex-digit SHA-256 of the final report>',
    }),
};
```

Compute pins locally with `sha256sum` after qualification. The example
placeholder digests intentionally cannot start any work. Save this
module outside the checkout and set the three local execution opt-in
environment variables described above, including
`EXPERIMENT_HUB_LOCAL_ADAPTERS` pointing to it. Register project ID
`ants` in the Hub, then save an immutable non-Bees configuration with
`{"mode":"new","episodes":1000}`. Request CPU/RAM capacity with
`gpu_count:0`, coordinate a connected Linux machine, wait for the
fresh lease receipt and explicitly authorize **Start on Linux**.

The adapter accepts `mode:"new"` (or omitted mode) for a fresh run
and `mode:"resume"` for an explicitly linked, completed predecessor.
Both require an integer `episodes` between 1 and 100,000,000.
For resume, this number is the *additional* number of episodes that
Ants' own production CLI permits, subject to its lifetime limit;
it is not an alternative curriculum or a replacement qualification.

The Hub stores `continuation_of_job_id`,
`artifact_lineage_root_id`, and an immutable original-machine
affinity. The parent must be terminal (`finished` or `stopped`),
have a verified native dispatch, and have its Hub capacity explicitly
released. Only one non-cancelled continuation may descend from a
parent, and the continuation must run on the original machine.
The local supervisor independently checks its own durable parent
service journal for an inactive, completed predecessor.

The trusted adapter resolves `checkpoints/latest.json` beneath the
original job's artifact root, rejects an invalid or external pointer
and `REGRESSION` forensic checkpoints, and passes the resolved
checkpoint directory to `--resume-from-checkpoint`. Ants then checks
the complete checkpoint manifest, persisted policy/optimizer/curriculum
state, writer/source lineage, qualification report and resolved runtime
device compatibility. A missing, stale or incompatible checkpoint
fails closed; the Hub does **not** manufacture a resume checkpoint,
automatically retry, or downgrade to policy-only initialization.

For a fresh run, artifacts live at
`<configured-parent>/<job-uuid>`. All subsequent generations use
the **same original artifact root**, preserving rollback references
and metrics lineage rather than copying partial checkpoints into a
new experiment. Each generation has a distinct Hub job ID and
systemd unit for process ownership and history. The CLI always uses
the pinned configuration/report and `--checkpoint-on-exit`.

Policy-only initialization and optimizer-regime continuation remain
unsupported: they intentionally have different learning, optimizer,
curriculum and rollback semantics and must never be confused with
full-state resume.

For Ants the trusted adapter selects `stop_policy:'interrupt-main'`:
the Linux supervisor configures `KillSignal=SIGINT`,
`KillMode=mixed`, and a 600-second stop grace window. A requested
stop is persisted before systemd is asked to stop the service
asynchronously. The supervisor does not send repeated interrupts after
an ambiguous stop request. This lets Ants' existing `KeyboardInterrupt`
path make its own checkpoint decision. A handled interrupt returns
exit code 130; the supervisor recognizes that as a requested stop, not
an unexplained training failure. **An interruption during an active
mutation cycle may deliberately skip an exit checkpoint to protect
integrity**, retaining the previous durable checkpoint; if the grace
period expires, systemd may forcibly end the process.

CPU-budgeted services mask CUDA/HIP/ROCm GPU selection environment
variables, but this is not kernel-enforced GPU device isolation.
Other GPU-aware code may ignore those variables. Ants' runtime/device
qualification must match what actually launches; a GPU-qualified
runtime should fail Ants' own authorization when the actual execution
environment resolves to CPU. CPU quotas do not grant dedicated cores
or automatically adjust Ants' rollout/evaluation worker counts.
Size the CPU/RAM request for the qualified production configuration
to avoid oversubscribed on-policy training and throughput collapse.

### Read-only production progress

The Ants adapter now reads the exact production
`<artifact-lineage>/experiments/<experiment-id>/summary.json` generated
atomically by Ants. A job lineage must contain one unambiguous experiment
directory; canonical filesystem paths must remain inside its approved
artifact root. Missing summaries are shown as awaiting progress;
ambiguous roots, malformed summaries and invalid metrics are rejected
without changing native job lifecycle status.

The agent sends a bounded, validated projection of Ants' own
`episodes_completed`, `latest_policy_version`, current curriculum
level, **current curriculum rolling-window success rate** and its
episode count, **segment success rate**, cumulative environment and
optimization steps, and last-episode simulation/PPO throughput. The
success rates have deliberately different denominators and must not
be treated as interchangeable, qualification evidence, or promotion
decisions. The source summary modification timestamp is displayed to
distinguish fresh observations from data last written before a long
episode. Continuations report the same experiment's cumulative
episodes, not episodes begun since the latest Hub job.

Live observations remain in coordinator memory and expire with the
authenticated agent's heartbeat. **Only one bounded final progress
snapshot** is retained in the durable Hub job ledger after a native
terminal report; no Ants CSV/JSONL, grouped histories, checkpoint data
or event logs are copied over the agent transport. The Train and
Monitor tabs use the same formatting. This integration has no
authority to start/stop work, release capacity, alter Ants PPO/
curriculum, or modify its source files.

### Bounded learning curves and run comparisons

The coordinator now samples the already-validated Ants production
progress projection into `experiment_hub_history.js`, with a fixed
one-minute sampling bucket, a maximum of **96 points per job**, and a
maximum of **24 jobs** in memory. Repeated reads of an unchanged
source summary do not create artificial points. On saturation, older
points are decimated to preserve a useful overview; when the episode
clock or policy version decreases, or a segment changes, the graph
breaks the line rather than fabricating uninterrupted learning.

`GET /api/hub/progress?project_id=ants` is a loopback-only,
**read-only** chart endpoint. It transmits bounded ephemeral samples
only when the Compare tab requests them, instead of copying series into
every ten-second Hub status response. No chart series is written to
`hub-v1.json`, and a coordinator restart intentionally clears it.
The latest job metrics and one final persisted snapshot remain usable
after restart; historical curves are not reconstructed from unrelated
metrics files.

The Compare tab supports up to four manually selected jobs, an
X-axis choice of cumulative experiment episodes or environment steps,
and a Y-axis choice of curriculum-window success, segment success,
curriculum level, policy version, last-episode simulation rate, or PPO
unique samples/s. Environment steps account for unequal episode lengths
when reported by Ants; missing step counts are not replaced by guessed
values. Source segment changes, policy/episode rollback and decreases
in reported cumulative environment steps break chart lines, including
after one-minute sample coalescing and bounded decimation. The PPO chart does not treat a non-updating episode as a
measured zero-throughput optimizer update. Jobs sharing the original
Ants artifact lineage are labeled as continuations, not independent
replicates. The chart calls out differences in frozen configuration
fingerprints (with a short list of differing top-level fields), assigned
machine identity, and currently reported curriculum level. Matching
fingerprints do not prove equal seeds, evaluation scenarios or
environment/runtime identity. **The curves do not normalize differing
level, seed, scenario, hardware, or configuration conditions** and are
not qualification, promotion, benchmark or superiority evidence.
No new Ants metric meaning or production behavior is introduced.

The adapter/native lifecycle has been exercised only with bounded
simulations of filesystem, qualification pinning, metrics and systemd
status. **No real Ants training or Linux service qualification has run here.**

## Not yet implemented

Installed project-specific Windows and Linux adapters, real-machine
qualification of Windows Job Objects and Linux systemd execution, GPU isolation
and runtime device-selection enforcement, live qualification of the opt-in
CPU-only scheduler, benchmark- and capability-driven stop criteria, metrics
ingestion for additional projects, safe live rebalancing, and comprehensive
CLI parity for all runtime configuration controls remain future work. These
must not create a second owner of the Bees training lifecycle or treat
reporting a worker as proof of exclusive host ownership.

No arbitrary commands from saved project configurations are executed. The
dashboard remains loopback-only; remote access requires a separate trusted
transport. Production multi-machine / Unity behavior has not been validated
by the development environment.
