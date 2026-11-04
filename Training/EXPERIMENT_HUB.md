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
  Non-Bees projects can queue immutable jobs and explicitly cancel unstarted
  jobs, but cannot start training.
- **Monitor and Compare:** Existing Bees live status, performance, benchmark
  reports and research charts remain intact. Non-experiment training is shown
  as distinct from experiment-owned training.
- **Machines:** Show read-only Bees worker reports from the existing control
  plane. Manually register project-independent machines (CPU, RAM, GPU capacity)
  and preview CPU/RAM, GPU count, and per-GPU VRAM requirements. A machine
  can be enrolled with a one-time credential and run a read-only heartbeat
  agent on Windows or Linux. Fresh heartbeats cap CPU/RAM placement previews
  to observed values. GPU inventory remains manually declared; no agent
  reserves or isolates resources yet.

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
processes nor grants hardware ownership. The future authenticated agent
and platform-specific adapters must prove isolation and live capacity before
any reservation becomes executable. The UI requires explicit confirmation
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
Read-only placement previews use the existing planner but never create leases
or launch programs. These are *possible* capacity assignments, not scheduling
decisions. Jobs with UUIDs use short ephemeral planner labels internally and
return their original UUIDs to the UI. The Bees project is deliberately
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
**same atomic registry write**. The job remains `waiting_adapter` and
cannot execute. The agent later acknowledges the lease as already
described above, but that does not prove resource enforcement. Assigned
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
argument list, shell command or privileged adapter name to this API. An
independently installed, trusted server/agent adapter and a verified native
resource-control backend are required before a future implementation can
safely transition any job out of `waiting_adapter`.

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

The agent has **no remote command interface** and does not start or stop
Bees, Ants, or any third-party training process. Enrolled machines are still
ineligible for Hub execution. It acknowledges coordination offers, but does not
enforce CPU, RAM, or GPU limits; even a claimed placement may interfere
with an existing untracked job.

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
authenticated heartbeat. The manager accepts reports only as
`backend:"none"` with all native limit/enforcement flags false; a
machine's Native controls column distinguishes available OS facilities
from an actually installed supervisor. Older agents without a report
remain observable, but are likewise ineligible for execution.

**No work may transition from `waiting_adapter` into running on the
strength of a heartbeat, a detected cgroup, or a coordination lease.**
Native process ownership, CPU and RAM control and validation, GPU
visibility/isolation, and trusted installed per-project adapters must
be implemented before enabling real execution. Merely installing
systemd or recognizing a Windows OS is insufficient.

## Local Linux systemd process supervisor (not connected to dispatch)

`experiment_hub_linux_supervisor.js` now provides a **locally invoked**
`LinuxSupervisor` API for executing a trusted adapter inside a transient
**user systemd service**, not in an unmanaged Node child process. This module
is not wired into the Hub's HTTP routes, agent heartbeat offers, or job state
transitions. No queued job automatically starts because the module exists.

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
CPU/memory properties using `systemctl --user show`. A service is not
reported as running until those controls have been inspected. A launch
timeout, missing unit, insufficient privileges, or mismatched limit is
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

This is **not yet a dashboard-controlled execution feature**. An explicit,
durable dispatch authorization and lifecycle handshake between the
Hub's job registry, the authenticated agent, and this local supervisor
must be developed before any pending job is allowed to run. Windows Job
Objects, Linux physical GPU mapping, ROCm/CUDA GPU isolation, workload
priority/rebalancing and cross-project production throughput are still
unimplemented.

## Not yet implemented

Hub-to-agent dispatch authorization, trusted installed project adapters,
Windows Job Objects, production qualification of local Linux systemd control,
GPU device verification and isolation, the multi-project job scheduler,
cross-project run history and metrics ingestion, safe live rebalancing, and
comprehensive CLI parity for all runtime configuration controls remain
future work. These
must not create a second owner of the Bees training lifecycle or treat
reporting a worker as proof of exclusive host ownership.

No arbitrary commands from saved project configurations are executed. The
dashboard remains loopback-only; remote access requires a separate trusted
transport. Production multi-machine / Unity behavior has not been validated
by the development environment.
