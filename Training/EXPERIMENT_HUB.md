# Experiment Hub foundation

The dashboard remains at `http://127.0.0.1:7148` and now has Design, Train,
Monitor, Compare, and Machines tabs. The existing Bees experiment coordinator,
operator, queue, checkpoint history, release compatibility checks, and evaluations
remain authoritative. Project-neutral HTTP administration uses the same
`experiment_hub_admin.js` router in the Bees manager and the disposable
Windows browser qualification; the existing Bees-specific routes remain separate.

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
  explicitly authorize locally approved native Linux or Windows execution,
  request native stops, and inspect native lifecycle reports. Real-host
  qualification is still required before treating either backend as deployable.
- **Monitor and Compare:** Existing Bees live status, performance, benchmark
  reports and research charts remain intact. Non-experiment training is shown
  as distinct from experiment-owned training. For Ants, Compare can plot up to
  four selected Hub jobs against cumulative experiment episodes or environment
  steps. Other trusted project adapters can publish unit-typed metrics for a
  cumulative project-step chart. Train, Monitor and Compare show these
  summaries without treating them as independent evaluations or stopping criteria.
- **Machines:** Show read-only Bees worker reports from the existing control
  plane. Manually register project-independent machines (CPU, RAM, GPU capacity)
  and preview CPU/RAM, GPU count, and per-GPU VRAM requirements. A machine
  can be enrolled with a one-time credential and run a read-only heartbeat
  agent on Windows or Linux. Fresh heartbeats cap CPU/RAM placement previews
  to observed values. The Machines table shows configured, freshly reported
  and outstanding claimed CPU/RAM side by side, and flags claims exceeding
  a connected agent's report. Claims are scheduling ledger entries, not
  measured usage. The same table separately sums fresh CPU-core-rate and
  RAM-use samples from reporting Hub-managed native jobs, including the
  number of contributing jobs; these **exclude Bees, the OS, and other
  untracked software**, so they are not total host utilization.
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
**not** a queued job. The planner uses a bounded, capability-scarcity-first greedy order,
protecting exact machine affinity and uniquely capable large-VRAM hosts
before assigning flexible workloads. It is not an exhaustive global
optimizer and does not mutate persistent jobs, reservations, or machines.
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

The UI can explicitly cancel a waiting job, including a stage blocked on
its predecessor; its status and cancellation time are retained. It cannot
cancel running or unknown jobs through this route.
To continue an experiment, save a resume-mode configuration, select its
eligible predecessor in Train, and queue with `continuation_of_job_id` in
`POST /api/hub/jobs`. The Hub retains the original artifact lineage, and pins subsequent
execution to the first successfully dispatched machine. For Ants, the
predecessor can be waiting, dispatched, running, or finished pending
capacity release. The
frozen continuation then has `status: "waiting_predecessor"` and **cannot
claim CPU/RAM, be started manually, or be started by automation**.
When a verified `finished` predecessor's Hub capacity is released, the
same atomic registry update moves its waiting successor to
`waiting_adapter`. Automatic allocation and Start still require the
separately armed, qualified CPU-only scheduler; manual Start retains its
normal authorization gate. A `stopped`, `failed`, or `unverified`
predecessor never triggers automatic advancement. The operator can
cancel the blocked continuation. For a verified `stopped` Ants
predecessor whose capacity has already been released, Train also offers
an explicit **Approve stopped predecessor** action. Its API
`POST /api/hub/jobs/approve-stopped` requires
`{"job_id":"...","confirm_stopped_checkpoint_risk":true}`,
a matching released lease and durable native terminal report. Approval
only changes the blocked stage to `waiting_adapter` and pins its
original machine; it does not start or allocate a process. The next
trusted Ants adapter launch independently validates checkpoint integrity,
qualification and source lineage. Since the most recent durable
checkpoint may predate an interrupted training run, approval warns
that progress after that checkpoint could be lost. Failed, unverified
or still-running predecessors cannot use this path. The Ants adapter independently checks checkpoint viability
and qualification on every launch. This is completion-based stage
readiness, **not benchmark-driven early stopping**. Further continuation
stages can be prequeued against an already blocked Ants continuation
while its artifact lineage stays frozen. A complete Ants pipeline
can also be queued before the first stage is assigned to any machine:
the original host is pinned only when that initial stage actually runs,
and its identity propagates to the next eligible stage at successful
terminal capacity release. This supports a linear chain, not an arbitrary DAG;
only one non-cancelled successor is allowed per parent. Blocked stages
carry no capacity claims, and a parent with a non-cancelled child must
be cancelled from the end of the chain backward. Train and Monitor
trace the earliest non-waiting ancestor of a blocked pipeline, showing
the stage responsible for the delay and whether it is still running,
waiting for a release, needs checkpoint-risk approval, or requires
operator review. This is a read-only projection and never alters
execution ownership or automatically recovers failed work.
The read-only queued-job placement preview now rehearses the **same
round-robin project fairness, FIFO ordering, connected-agent requirement,
continuation host affinity and best-fit resource selection** as the actual
Hub lease allocator. When a fitting host has the project's adapter and a
passing native preflight, placement prefers it over advisory-only hosts.
An explicit machine selection can still create an advisory claim. The
preview accounts for simulated earlier claims and creates no leases.
A later live allocation can differ when machine health or capacity has
changed. The separate general what-if planner at POST /api/hub/plan
continues to use a GPU-first greedy ordering, which is useful for capacity
exploration but is not a prediction of queue scheduling.
The Bees project is deliberately
rejected by this generic queue and continues to use its existing coordinator.

The API is `POST /api/hub/jobs` with
`{configuration_id, expected_revision, resources}`, and
`POST /api/hub/pipelines` with
`{"confirm_queue":true,"stages":[{"configuration_id":"...","expected_revision":1,"resources":{"cpus":2,"ram_mb":2048,"gpu_count":0}},...]}`.
The latter freezes **2–16 Ants stages in one atomic registry write**,
accepts an optional `continuation_of_job_id` for the initial stage,
and rejects the entire pipeline if any configuration, revision, mode,
resource request, or predecessor is invalid. Stage one must use new
mode unless resuming an eligible predecessor; later stages must use
resume mode. The Train tab provides a visual pipeline builder.

For **independent experiments**, including mixed Ants and other
non-Bees projects, the same Train editor offers
`POST /api/hub/sequences` with
`{"confirm_queue":true,"stages":[...],"after_job_id":"optional-existing-job-id"}`.
All 2–16 stages freeze their saved-configuration revisions and requested
resources atomically. Their `after_job_id` dependencies are separate
from checkpoint `continuation_of_job_id`: each stage has its own
artifact lineage, with **no inherited weights, optimizer, checkpoint,
machine affinity or evaluation result**. Ants configurations in an
independent sequence must use `mode:"new"`. Jobs may run on different
qualified hosts. A verified native `finished` predecessor unblocks the next job after
durable capacity release. A `stopped` predecessor also unblocks its
independent successor only if its own independently evaluated stop target
was met and the native Stop and capacity release were verified. Manual
Stops without that evidence, failures, and unverified predecessors never
advance. Native success is not an independent benchmark pass. Every stage still needs separate CPU/RAM
capacity admission and authorized native execution, and the automation
session must be explicitly armed for unattended CPU-only starts.
Each parent permits one non-cancelled successor across both sequence
types. The Train tab shows a read-only cross-project chain overview and
can cancel **all unstarted future jobs** from a selected current
predecessor in one atomic action:
`POST /api/hub/jobs/cancel-future` with
`{"after_job_id":"...","confirm_cancel":true}`. This cancels queued
descendants from the end backward, releases their unstarted Hub claims
and leaves the selected predecessor and any native process untouched.
If any downstream job has started, is unverified or has conflicting
ownership, the entire cancellation is refused without a partial write.
Individual cancellation remains available after clearing children.
`POST /api/hub/jobs/cancel` with `{job_id,confirm_cancel:true}`,
`GET /api/hub/jobs/plan` for a read-only view, and
`POST /api/hub/jobs/allocate` to claim Hub capacity. Jobs are queued
without initial claims. The registry limits outstanding non-Bees jobs to
100 and stored job records to 1,000; it refuses to silently delete old
experiment definitions.

### Loopback operator CLI

`node Training/experiment_hub_cli.js get state` returns the same Hub state
as the dashboard. Use `get 'progress?project_id=ants'` for project history
or `get jobs/plan` to preview capacity. POST commands take an explicit JSON
file, for example `post jobs/move-claim --data move.json`. Use the same
fields and confirmation values as the dashboard API. The CLI also accepts
the existing `bees/control`, `bees/workers`, `bees/start`, `bees/stop`,
`bees/build`, and `bees/update-workers` routes on the production manager.
Requests use loopback only and default to port 7148.

### Atomic advisory job allocation

Train can move an unstarted claim to the selected machine in one update.
The replacement requires a fresh agent receipt and any GPU access proof must
be rerun against the new lease. The source stays claimed if the destination
cannot fit the job or the claim changed before confirmation. A dispatched
job, pinned continuation or claim with an in-flight GPU access probe cannot
be moved to another host. Stage several moves in Train to exchange capacity
between full hosts. `POST /api/hub/jobs/move-claims` accepts up to 100 distinct
`{job_id, expected_lease_id, machine_id}` entries in `moves` with
`confirm_move:true`. A stale or infeasible move leaves the entire batch unchanged.

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

### Isolated transport smoke and inert Linux qualification

Run `node Training/experiment_hub_smoke.js` from the checkout to exercise
an **ephemeral, randomly ported loopback HTTP listener**, using the same
restricted worker HTTP handler as the production coordinator and the real
Node agent's request serialization. It checks rejected administrator
routes, invalid host/origin/content-type, malformed/oversized requests,
machine credential rejection, lease acknowledgements, explicit release
and coordinator reload. This script creates its registry beneath the OS
temporary directory and removes it. It never imports the Bees operator,
contacts the live dashboard or runs a native/training process. It does
**not** qualify the administrator listener, SSH forwarding, Linux systemd,
Windows Job Objects or remote-worker deployment.

On an isolated Linux machine with a working **user systemd instance**
and cgroup v2 `cpu` and `memory` delegation, the next local qualification
can run without enrolling any agent or starting the Bees manager:

```bash
node Training/experiment_hub_native_smoke_linux.js --preflight
node Training/experiment_hub_native_smoke_linux.js --run-inert
```

`--preflight` launches then stops the supervisor's fixed inert sleep
service and verifies the effective kernel CPU/RAM cgroup limits.
`--run-inert` also starts and stops one 30-second `/usr/bin/sleep`
job under the native supervisor, verifies ownership, and reconciles its
terminal result. Both use a separate private temporary supervisor
ledger; only a verified clean result deletes it. An ambiguous failure
preserves the ledger and prints its path for manual inspection; **do not
retry blindly**. This is an operator-initiated, local service test,
not training or proof of multi-machine readiness.

For a separate operator-controlled end-to-end dashboard/agent smoke,
the checked-in `Training/experiment_hub_smoke_adapter_linux.js` is an
inert, fixed-path trusted adapter for project ID `smoke` only. After
the standalone preflight succeeds, the machine operator can
install that module through `EXPERIMENT_HUB_LOCAL_ADAPTERS` alongside a
separate absolute `EXPERIMENT_HUB_LOCAL_ROOT`, after enrolling the worker
and successfully running the native systemd preflight. Queue a `smoke`
configuration with **exactly** `{"kind":"inert"}`, request 1 CPU and
256 MiB RAM, and explicitly authorize one Start. It launches only
`/usr/bin/sleep 30` under the native supervisor. Observe the kernel
limits, Stop and terminal reporting before trying automation or any real
project adapter. Do not run it on a machine carrying unaccounted Bees or
other active workloads.

Manual batch claims and the read-only pending-queue preview continue
to preserve FIFO within each project and interleave projects fairly. They
now prefer an alternative capable host when a flexible job would
otherwise consume the **only** placement available to a later queued
job. This single-host lookahead respects existing offered/acknowledged
leases, does not reorder jobs, does not guarantee a globally optimal
packing, and does not authorize native execution. The separate
multi-workload what-if planner can reorder proposed workloads by
capacity scarcity; its result is **not** a guarantee of the FIFO live
claim order.

### Opt-in native queue automation

The non-Bees Train tab can explicitly **arm 1–16 selected, enrolled native
machines** using the loopback-only `POST /api/hub/automation` endpoint.
Arming requires a passing local CPU/RAM preflight, a current authenticated
agent instance, installed trusted project adapters, and confirmation that
the operator has accounted for Bees and any other **untracked workloads**
on those machines. The automation session is process-local and starts
**disarmed**, including after every coordinator restart. A second Arm
request is rejected while an automation session is active; otherwise it
could abandon ownership of running jobs. Disarming does
not stop a running service or release its capacity. Automatic Start
requests are tagged with the ephemeral arming session and agent instance:
after disarm, restart, or replacement, the coordinator will **not issue a
new Start directive** for an older automatic request. A command already
delivered to the agent may nevertheless be executing; use manual Stop
and native inspection to resolve it. Re-arming does not revive an older
session's pending Start. The Train tab
retains the last actual scheduler transition, its timestamp and a
separate current waiting reason instead of repeatedly overwriting
the meaningful event during idle polling.

While armed, a coordinator pass every ten seconds performs **at most one
durable transition**: release capacity only for a job started in the
**current automation session** after an authenticated native `finished`
or `stopped` report (activating an eligible prequeued successor in the
same write after `finished`), authorize one eligible Start that passes
the same live readiness gate as the manual Start button, or claim available
CPU/RAM budget for an unassigned CPU-only non-Bees job or, when opted in,
an exclusive single-GPU job. Jobs completed under
older sessions require explicit capacity release unless their manual Start
opted into terminal release. A machine cannot
receive a second automatic Start authorization while another Start is
awaiting its native running/terminal acknowledgement. Project queues are
interleaved fairly (FIFO within each project). Because automation makes
only one transition per tick, its CPU/RAM claim and native Start passes
retain **separate per-session round-robin project cursors**. A
continuously ready earlier project therefore cannot take every
successive allocation or Start ahead of another ready project. The
cursors are ephemeral, reset on disarm or coordinator restart, and
never override capacity, preflight, machine affinity, or pending-Start
safety gates. The automatic CPU-only claim pass also avoids consuming
the sole remaining qualified adapter host of a later queued job when an
equivalent host is available to the current job. It preserves project
round-robin order, and checks the installed adapter inventory before
treating a future host as capable. The manual allocation pass and read-only queue preview
keep their stable per-project interleave rather than guessing what a
future armed scheduler session will choose. An agent must first
acknowledge each fresh claim. CPU-only is the default. Exclusive
single-GPU jobs can enter this automation only with the explicit claim,
probe and Start options described in the Linux native supervisor section.
Shared and multi-GPU requests remain excluded. Existing Bees training
remains under its own operator.

A failed or unverified managed job, disappearing/replaced agent, or
failed coordinator operation disarms the session without retrying an
uncertain native launch or freeing uncertain capacity. Stop/recovery of
failed jobs remains manual. Isolated Windows qualifications have verified
native Job Object CPU/RAM enforcement, concurrent automatic scheduling,
terminal receipt/release, dashboard controls, and heartbeat-loss disarming
on a real host. **Production Bees/Ants coexistence and unattended project
training are still unverified**; real Linux services and GPU isolation remain
unqualified. The **generic non-Bees scheduler** supports frozen independent-evaluation
stop thresholds, but not Bees-style multi-stage benchmark promotion,
dynamic live rebalancing, or enforced performance shares. The existing Bees experiment manager separately
supports an opt-in periodic, frozen development-benchmark completion
target, documented in `EXPERIMENTS.md`. It stops only after verified
checkpoint evaluation and safe training shutdown; actual Unity
qualification of this new option remains outstanding.

### Shared-host scheduling budgets for existing Bees and new projects

In **Machines → Adjust Hub scheduling budget**, select a registered machine
and choose how many CPU slots and MiB of RAM the Hub may admit. A connected
agent's reported host CPU/RAM caps still apply; the effective Hub budget
is the smaller of the operator's configured number and the fresh host
observation. For example, a host reporting 8 logical CPUs and 32 GiB RAM
could be configured with only **2 CPU slots and 4096 MiB** for Hub jobs,
leaving the remainder outside Hub admission for an independently owned
Bees learner, workers, the OS, or interactive programs.

Budget changes require an explicit confirmation, the original displayed
CPU/RAM values, and **disarmed native queue automation**. Stale updates are
rejected. A reduction below outstanding Hub claims is refused; the
change neither resizes nor stops any running native process. Claims
remain durable across agent disconnects and coordinator restarts. This
feature does not map Bees trainer IDs to physical machines, measure
their host usage, or automatically adjust when Bees/Ants activity changes.
It is a conservative *operator-chosen admission budget*, not isolation,
a reserved CPU core set, or a throughput guarantee. For uncertain
coexistence, use a dedicated host or a much smaller Hub budget instead
of assuming Bees workers consume only what their status metrics suggest.

The shared loopback route is `POST /api/hub/machines/budget`, using
`{machine_id,cpus,ram_mb,expected_cpus,expected_ram_mb,confirm_budget:true}`.
Existing Hub machine identity, enrollment, GPU bindings, leases and jobs
remain unchanged; the same capacity planner serves manual allocation,
automatic CPU-only admission and placement preview.

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
machine-bound `start`, `inspect` and `stop` directives for eligible native
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
verification are all required. Native Windows execution is CPU-only and has not
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

When arming automation, operators can optionally let the session move
its own queued claims between armed machines after a CPU/RAM capacity drop
or loss of a required adapter, evaluation suite, TinyBrains profile, or
GPU eligibility. GPU claims can move only before a probe has
been requested. Manual claims, checkpoint-pinned jobs and dispatched jobs
stay in place. Each move uses the same atomic lease replacement as Train
and requires a fresh destination receipt.
Disarming or restarting ends ownership of earlier claims.

An armed session can also opt into starting qualified GPU claims
(`start_qualified_gpu:true`). A separate option, `claim_exclusive_gpu:true`,
lets the session claim for queued single-GPU jobs on armed Linux hosts with
the required adapter, current PCI inventory and at least two observed AMD
GPUs. The access-denial check needs a second device. Shared
and multi-GPU requests stay outside automation. With
`probe_exclusive_gpu:true`, the session can request one inert access probe
for each exclusive GPU lease it creates, after a fresh worker receipt.
Disarm or restart suspends delivery of pending automatic probes. Train can
explicitly resume the same request. Train can also withdraw a probe
before its first worker offer, keeping the claim available for a later
manual probe or move. Withdrawal does not requeue an automatic probe for
the same lease. The first offer is durable and blocks withdrawal even
if the reply was lost. Older pending requests lack that delivery
evidence and require operator review. A rejected or stale proof also
requires operator review. The Hub retains the claim after an offer until
the worker reports the result. If a rejection leaves cleanup unconfirmed,
inspect the worker, then select **Recheck GPU probe cleanup** in Train.
The recheck can run while the leased GPU is missing. It checks only
cleanup and cannot authorize GPU access. A worker report confirming
cleanup clears the hold and allows a separate release or move. If the
worker still has a pending probe record, resolve it locally before
rechecking. A new GPU access probe remains a separate action. Deploy the coordinator
before the workers so it can retain claims for older rejection receipts
that omit cleanup evidence. The normal native Start gates still apply, including
the current proof for the exact lease and agent instance and the
in-service device and runtime checks. Verified jobs started by the
session use its existing completion release path.

The loopback CLI accepts the same operations. For an undelivered probe,
write `{"job_id":"<job UUID>","expected_request_id":"<probe UUID>","confirm_withdraw":true}`
to `withdraw.json` and run:

```powershell
node Training/experiment_hub_cli.js post jobs/withdraw-gpu-probe --data withdraw.json
```

For a held claim after an uncertain rejection, write
`{"job_id":"<job UUID>","confirm_qualify":true,"confirm_cleanup":true}`
to `cleanup.json` and run:

```powershell
node Training/experiment_hub_cli.js post jobs/qualify-gpu --data cleanup.json
```

Use `get state` to inspect the worker result before releasing the claim.

### Operator-initiated native execution

Train can opt an individual Start into automatic release on verified
finish or Stop (`release_on_terminal:true` in the Start request). The Hub
records completion and releases the job's lease in the same update, so
an eligible successor can advance after a verified finish. The setting
survives coordinator restart. Failed or unverified jobs retain their
claims for review. This applies to explicitly started CPU and GPU jobs.

To retire a project adapter while a job is still owned, keep the same
machine enrollment, supervisor directory, and Windows helper pin where
applicable. Restart the agent with the adapter removed from its local
module. An empty module export (`module.exports = {};`) enables native
recovery only when local execution is explicitly enabled. Inspection and
Stop still require the original job and lease in the native ownership
journal. New Starts and GPU probes require an installed adapter.

Train can withdraw a Start while it is still awaiting its first worker
delivery. The Hub records the first offer before returning the worker
order. A lost reply therefore leaves ownership uncertain and requires
native inspection. Pending Starts written before the first-offer marker
also require inspection. A withdrawn job keeps its lease and returns to the
queue. Disarm automation on that host before withdrawing. If an offered
Start loses a launch prerequisite, the Hub sends Inspect while the
prerequisite is unavailable.

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
current memory use, cumulative CPU time and task count without per-poll
history duplication. Train and Monitor also show **average CPU-core
equivalents** computed from native cumulative CPU nanoseconds divided
by the server-observed time between two authenticated reports (0.50
means half of one logical CPU core on average). The rate requires
the same dispatch and agent instance, continuous running/stop intent,
a nondecreasing counter, and an interval of 1–120 seconds. It is
ephemeral and observational, not a capacity claim or scheduler input.
Windows may provide CPU time without a separate RAM measurement,
and those observations are displayed independently. Only lifecycle reports accepted by the coordinator update live
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

### Linux GPU device-filter qualification and opt-in launch

The local AMDGPU device resolver maps the frozen PCI identity to
a kernel DRM render character node under /dev/dri. It verifies the
current sysfs PCI address, driver/vendor, exact character-device
major/minor, and current total VRAM. Missing nodes, ambiguous GPU
partitions, moved PCI devices and insufficient VRAM are rejected.

The Linux supervisor now has an optional, **locally invoked**
`qualifyGpuDeviceAccess(bindings)` method. It first proves that the
same ordinary user account can open the selected render device,
every resolved excluded AMD render device, and /dev/kfd. It then starts an inert,
uniquely named systemd user service with DevicePolicy=closed and
DeviceAllow entries for only /dev/kfd and the selected render node.
The fixed Node probe must demonstrate that /dev/kfd and the
selected render device remain openable **while every baseline-openable
excluded AMD render device becomes inaccessible**. The supervisor checks
the PCI and character-device mapping again after cleanup to reject
reenumeration during the probe. Failure, unavailable
systemd eBPF device filtering, an ambiguous unit or unconfirmed
cleanup leaves qualification blocked. The probe opens and closes
device files but does not submit GPU work or run training. Every
explicit job request runs a **fresh** device-access proof, not a
cached pass from a different job. Before launching a probe, the local
supervisor records its unique systemd unit and description in
`<EXPERIMENT_HUB_LOCAL_ROOT>/gpu-preflight-pending.json`. The systemd
service has a 5-second start timeout, a 10-second runtime
limit, and a 3-second stop timeout. The agent still requires cleanup
evidence before releasing the probe record. A confirmed stopped or absent
unit clears this record; ambiguous cleanup or an agent
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

#### Opt-in native Linux GPU launch

After deployment qualification, a Linux agent can set
`EXPERIMENT_HUB_ENABLE_ROCM=1` and advertise a locally installed adapter that
implements `gpu_verify_args(parameters, context)`. The verifier runs the
adapter executable with these arguments and must return exactly one ROCm
runtime device PCI address in `{"pci_addresses":["0000:03:00.0"]}`. The
adapter must inspect actual runtime devices and use the same runtime and
device selection in its workload. Both adapter hooks receive the leased
`context.gpu_pci_address`. The agent removes inherited accelerator selectors
so the runtime probe and workload start with the same device visibility.

Queue placement prefers fitting hosts advertising the adapter's GPU capability.
Train requires explicit Start and a current access probe for the same
acknowledged exclusive single-GPU lease. The native service resolves the PCI
mapping again and runs the device access and runtime checks in its own
systemd cgroup before the training executable can start. The local journal
also rejects unresolved ownership of the same PCI GPU. CPU-only automation remains the default. A GPU authorization that loses its proof
before delivery holds its claim for operator inspection.

Halifax still needs real qualification of the systemd device policy and
the adapter's ROCm visibility, execution and stop behavior. The planner
accounts for Hub claims. The device policy provides no per-process VRAM
quota, compute share, or exclusivity from unrelated host workloads.

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

### Browser dashboard + local Windows agent qualification

After the isolated Windows `--run-queue` and `--run-recovery` modes
pass, use the same PowerShell session that holds the built/pinned
`EXPERIMENT_HUB_WINDOWS_HELPER` and
`EXPERIMENT_HUB_WINDOWS_HELPER_SHA256` values. From the repository
root (`B:\\Bees\\Assets` on the current Windows checkout):

```powershell
node .\\Training\\experiment_hub_dashboard_qualification.js --run-inert-ui
```

Open the **ephemeral loopback URL printed by the program**, **not**
`127.0.0.1:7148`. This is an isolated instance serving the same Hub
HTML/CSS and `experiment_hub_ui.js`, but without the embedded legacy
Bees browser script or any Bees operator routes. It uses a disposable
registry, a pre-enrolled Windows agent `smoke`, the real worker-only
loopback heartbeat listener, and a locally pinned native helper.
The only approved adapter executes the fixed 30-second
`--smoke-child`; it never runs Bees, Ants or user-supplied commands.
The registered coordination budget is **2 CPUs / 512 MiB RAM**.

In the **Windows qualification** project, use **Machines** to confirm
`smoke` is connected with native preflight passed. In **Design**, save
a draft such as `Inert browser run` with parameters
`{"kind":"inert"}`. In **Train**, replace the default resources with
`{"cpus":1,"ram_mb":256,"gpu_count":0}`, then select **Queue job**,
**Coordinate capacity**, wait for the fresh agent receipt, and select
**Start native job**. Use **Monitor** to inspect the running service.
Before 30 seconds elapse, choose **Stop managed job** in **Train**;
otherwise allow the inert child to finish naturally. After a verified
`stopped` or `finished` result, select **Release completed capacity**
and confirm **Machines** has zero outstanding reservations. The
browser's explicit **Arm** control can separately exercise isolated
automatic scheduling; it is disabled by default. Do not rotate or
revoke `smoke`'s session-managed credential.

#### Automated browser queue and disconnect qualification

Use a **fresh isolated dashboard session** for each scenario:

1. **Automatic CPU/RAM scheduling:** Save a fixed inert configuration,
   queue **three** jobs with resources
   `{"cpus":1,"ram_mb":256,"gpu_count":0}` each, and in **Train**
   select `smoke` under native queue automation and confirm **Arm**.
   Do not click Start manually. The qualification panel should show
   at most **2 simultaneously running jobs**, with claims no higher
   than **2 CPUs and 512 MiB**; the third waits. When the first jobs
   finish, the scheduler must release signed terminal capacity and start
   the third. Wait for all three to finish with released claims, then
   explicitly **Disarm** and exit. The PowerShell shutdown summary
   reports peak concurrency, peak claimed resources, completed jobs,
   and outstanding claims. Neither observed memory usage nor physical
   host-exclusive reservation is asserted by these figures.
2. **Missing heartbeats while jobs are running:** Start a new isolated
   session and queue three more fixed inert CPU-only jobs. Arm the
   scheduler and wait until **two** jobs report running. Immediately
   click **Pause agent heartbeats** in the isolated qualification panel.
   Leave them paused for **at least 50 seconds**, past the normal
   45-second heartbeat expiry. The agent should show **offline** and
   automation should **disarm for lost qualified continuity**. The native
   owners may finish their 30-second children while heartbeats are
   absent; this does not release Hub claims automatically. Select
   **Resume agent heartbeats**. The *same* agent session should reconnect
   and inspect its existing native ownership without a duplicate Start,
   report the two terminal services, and leave automation disarmed.
   Release their held terminal claims manually, and cancel any remaining
   unstarted job. Confirm zero active claims before exiting.

The heartbeat controls are **qualification-only**; they suspend the
in-process agent's authenticated HTTP updates without closing the
coordinator, killing its native children, or replacing its credentials.
The safety scheduler continues running independently while heartbeats
are paused. Live numbers distinguish Hub CPU/RAM **claims** from native
CPU time/process counts. Windows Job Object memory *usage* is currently
not measured; the enforced per-job limit is not a usage measurement.
An offline test does not establish resilience to an actual machine power
failure or network partition. Never re-arm automation during an
unverified, disconnected or unreleased native lifecycle.

Press **Ctrl+C** in the launching PowerShell window once claims are
released. The temporary private registry is intentionally preserved for
inspection. If native status becomes `unverified` or a listener fails,
do not issue repeated native Starts or delete the printed private
journal path. This exercises the browser/agent integration on real
Windows but is **not** production deployment or GPU qualification.

### Linux isolated browser qualification (native systemd)

The same `experiment_hub_dashboard_qualification.js --run-inert-ui`
command now supports Linux without any Windows helper. First qualify a
**dedicated, idle Linux host** with an active user systemd instance,
delegated cgroup v2 CPU/memory controllers, at least two logical CPUs
and 512 MiB RAM:

```bash
node Training/experiment_hub_native_smoke_linux.js --preflight
node Training/experiment_hub_native_smoke_linux.js --run-inert
node Training/experiment_hub_dashboard_qualification.js --run-inert-ui
```

Open the printed **ephemeral loopback URL**, not port 7148. The
qualification dashboard/worker use the real Linux supervisor and the
checked-in fixed `smoke` adapter, which only launches
`/usr/bin/sleep 30` under a transient user-systemd service; there
is no arbitrary command or production Bees/Ants adapter installed.
The temporary coordinator registry and supervisor journal remain
separate from production. The browser's project/machine names say
**Linux qualification** and **Isolated local Linux agent**.

Perform the same **three CPU-only queued jobs** and optional
**heartbeat loss/reconnection** procedures as the Windows isolated
dashboard qualification above. The registered claim ceiling remains
2 CPUs / 512 MiB; native CPUQuota and MemoryMax must be checked for
each launched Linux service. Windows helper environment variables are
not read on Linux. Leave automation disarmed and release only verified
terminal claims before closing the launcher. Any uncertain service
state requires local journal and exact systemd unit inspection, not
restarting the job. A passing Windows test does not establish Linux
kernel-limit enforcement, and the Linux path remains **unverified on
a real Linux machine** until the above commands complete.

### Windows-first isolated qualification (no Halifax or Linux required)

Use a checkout of this PR branch on a Windows machine with Node.js 18+
and the .NET 8 SDK. First review the pinned owner helper source; compile
it into a **temporary directory outside the repository**, so no large
binary becomes part of a commit. From the checkout root in PowerShell:

```powershell
$ErrorActionPreference = 'Stop'

# This first check needs Node only, not the .NET SDK.
node .\Training\experiment_hub_smoke.js
if ($LASTEXITCODE -ne 0) { throw 'Worker HTTP qualification failed.' }

# The remaining Windows-native checks use a reviewed, locally built helper.
$publish = Join-Path $env:TEMP ('experiment-hub-owner-' + [guid]::NewGuid().ToString('N'))
dotnet publish .\Training\experiment_hub_windows_owner\ExperimentHubWindowsOwner.csproj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Windows helper build failed.' }
$env:EXPERIMENT_HUB_WINDOWS_HELPER = Join-Path $publish 'ExperimentHubWindowsOwner.exe'
$env:EXPERIMENT_HUB_WINDOWS_HELPER_SHA256 = (Get-FileHash $env:EXPERIMENT_HUB_WINDOWS_HELPER -Algorithm SHA256).Hash.ToLowerInvariant()
node .\Training\experiment_hub_native_smoke_windows.js --preflight
if ($LASTEXITCODE -ne 0) { throw 'Windows Job Object preflight failed.' }
node .\Training\experiment_hub_native_smoke_windows.js --run-inert
if ($LASTEXITCODE -ne 0) { throw 'Windows Start/Stop qualification failed.' }
node .\Training\experiment_hub_windows_end_to_end_smoke.js --run-inert
if ($LASTEXITCODE -ne 0) { throw 'Integrated Windows qualification failed.' }

# Run only after the preceding independent Windows qualifications pass.
node .\Training\experiment_hub_windows_end_to_end_smoke.js --run-queue
if ($LASTEXITCODE -ne 0) { throw 'Windows concurrent queue qualification failed.' }
node .\Training\experiment_hub_windows_end_to_end_smoke.js --run-recovery
if ($LASTEXITCODE -ne 0) { throw 'Windows in-flight coordinator recovery qualification failed.' }
```

Run them **in order** and stop on the first error. The first exercises
the production worker-only listener on ephemeral real loopback HTTP with
a disposable coordinator registry, without a native child. The second
validates the helper's fixed `--probe` and kernel Job Object controls.
The third starts and terminates one fixed 30-second `--smoke-child`
inside the Job Object, then verifies the signed terminal receipt after
reopening the Windows supervisor. The final test connects a disposable
Hub registry and real agent HTTP transport to a *single* manual
allocation, authenticated native Start, Stop, terminal receipt,
explicit capacity release and coordinator restart.

`--run-queue` explicitly arms the isolated CPU-only scheduler for two
fixed inert project adapters, constrains the registered machine to **2 CPUs
and 512 MiB RAM**, and proves two concurrent native jobs fit while a third
waits. It then requests controlled native Stops, checks authenticated
terminal reporting and automatic capacity reuse, and confirms a queued
GPU request never executes. The host must report at least 2 logical CPUs
and 512 MiB RAM. The mode always disarms automation on success.

`--run-recovery` runs a single inert Job Object, restarts **only the
disposable Hub coordinator** while that native service is running, and
checks that a reauthenticated agent **inspects** the already-owned process
instead of relaunching it. Then it explicitly stops the service, verifies
the terminal receipt and releases the claim. All modes run on randomly
selected ephemeral loopback ports and private temporary registries.
**No Bees/Ants training or production registry is touched.**

These are controlled local tests, not production deployment. A failing
native smoke retains its private journal and prints the location; do
not retry an ambiguous launch or delete those records before inspecting
the owned service. A passing suite does **not** qualify browser/admin
workflows, independent remote workers, GPU isolation or concurrent
production projects.

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

## Ants-v2 production adapter (locally installed)

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
Pre-create the artifact parent outside the Ants source checkout. The adapter
creates a private per-lineage directory for a fresh job before calling the
Ants CLI. It requires that directory to be empty for a new job. Continuations
reuse their existing directory and reject redirected checkpoint paths. It passes the report unchanged into Ants' own production training
authorization. That authorization independently requires the exact
current source, configuration and resolved runtime/device lineage
used by the passing qualification report. This adapter cannot authorize
training on its own. By default the adapter advertises CPU execution.

For a qualified ROCm installation, set the local adapter options
`gpuVerifierPath` to the absolute path of
`experiment_hub_ants_rocm_verify.py` and `gpuVerifierSha256` to its pinned
digest. Enable the Linux agent's ROCm opt-in and use a qualification
report from the exact Ants source, YAML, and ROCm runtime. The verifier
runs under the training service's device policy, validates the standard
Ants authorization, checks the leased PCI device in PyTorch's loaded HIP
runtime, and completes a small tensor operation before training starts.
Real systemd and ROCm verification on Halifax is still required.

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

An Ants adapter can optionally receive independent evaluation receipts from
a separate trusted evaluator. Set `evaluationRoot` to a pre-created directory
separate from the source and training artifact trees, and set
`evaluationSuites` to the suite IDs that the evaluator serves. The evaluator
atomically publishes a bounded version-1 Hub evaluation report to
`<evaluationRoot>/<dispatch_id>.<suite_id>.json`. Keep this directory under
the evaluator operator's control. The adapter only reads the current
dispatch's receipt. Ants' training summaries remain observational.

`Training/experiment_hub_ants_evaluate.py` can produce this receipt on a
separately budgeted CPU evaluator using the matching Ants Python checkout.
Give it a trusted, immutable checkpoint from the selected dispatch and the
SHA-256 of its `manifest.json`. Keep the suite definition stable and choose
a new suite ID when changing the level, trial count, or seed. For example:

```json
{"suite_id":"level_1_heldout","level":1,"trials":100,"seed":9048534}
```

```sh
python Training/experiment_hub_ants_evaluate.py \
  --checkpoint /srv/evaluator/inbox/checkpoints/checkpoint_00000100_manual \
  --checkpoint-sha256 "$MANIFEST_SHA256" --dispatch-id "$DISPATCH_ID" \
  --suite /etc/experiment-hub/level_1_heldout.json \
  --scratch /srv/evaluator/scratch --receipt-root /srv/evaluator/receipts
```

The producer uses a separate held-out seed namespace, the checkpoint's
production model and environment, a fixed curriculum level, and stochastic
Ants outcomes. It writes only to scratch and the receipt directory. The
evaluator operator supplies the dispatch and checkpoint association for
this on-demand command. The worker must be able to read the private receipt.

For automatic handoff, run `experiment_hub_ants_eval_poll.py` periodically
from a separately budgeted CPU service. Provide `--machine` (the Ants training
host's Hub ID), `--artifact-root` (its mounted read-only artifact parent),
`--suite`, `--scratch` and `--receipt-root`. The default Hub port is 7148
on loopback. An evaluator on another machine needs a trusted loopback
forward to the coordinator. Use private persistent scratch and share the
receipt directory with the training agent's adapter. The scheduler owns
the cadence, CPU budget, and lifetime of each invocation.

Each invocation selects an eligible running dispatch and evaluates at most
one newer checkpoint. Fresh jobs can use their first visible checkpoint.
For continuations, the first visible checkpoint sets a watermark because
it may belong to the predecessor. The evaluator rotates among eligible
dispatches, retains the last attempted episode across invocations and
temporary worker disconnects, and leaves an already-passing receipt available
for the worker. Old inactive dispatch watermarks are evicted only when the
bounded 1,000-entry scratch limit is reached. Once an
evaluation result is durably recorded in scratch, it can be published or
retried without a second Hub availability check: the unique dispatch and
suite are validated again by the trusted running adapter and coordinator
before they can authorize Stop. A heartbeat gap during evaluation must not
discard a completed result. Publication to an already-terminal dispatch is
inert. A failed evaluation itself waits for a newer checkpoint. Keep a
stable suite ID and scratch directory for the lifetime of the schedule.

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

The coordinator samples the already-validated Ants production progress
projection or a project-neutral summary from a locally installed trusted
adapter into `experiment_hub_history.js`, with a fixed
one-minute sampling bucket, a maximum of **96 points per job**, and a
maximum of **24 jobs** in memory and in a separately bounded
`hub-chart-v1.json` visualization archive. Accepted new samples are saved
at most once per minute, or when a job finishes; a sudden stop can lose
recent unsaved chart points. The archive is atomic, capped at 4 MiB,
validated on restore and never used for training or scheduling decisions.
An invalid archive is preserved for inspection rather than overwritten;
storage failures leave agent heartbeats and training lifecycle unaffected
and are reported in Compare. Repeated reads of an unchanged
source summary do not create artificial points. On saturation, older
points are decimated to preserve a useful overview; when the episode
clock or policy version decreases, or a segment changes, the graph
breaks the line rather than fabricating uninterrupted learning.

`GET /api/hub/progress?project_id=<project>` is a loopback-only,
**read-only** chart endpoint. It transmits bounded visualization samples
only when the Compare tab requests them, instead of copying series into
every ten-second Hub status response. No chart series is written to
`hub-v1.json`. The separately bounded chart archive survives coordinator
restarts, while unsaved samples remain ephemeral.
The latest job metrics and one final persisted snapshot remain usable
after restart; saved bounded visualization curves are restored from the
separate chart archive, not reconstructed from unrelated metrics files.

The Compare tab supports up to four manually selected jobs, an
X-axis choice of cumulative experiment episodes or environment steps,
and a Y-axis choice of curriculum-window success, segment success,
curriculum level, policy version, last-episode simulation rate, or PPO
unique samples/s. A curriculum-level filter excludes observations from
other levels and preserves line breaks across omitted intervals; the
horizontal axis remains cumulative, not normalized to level start.
The selected runs and samples can be exported as UTF-8 CSV or JSON for
external analysis, with configuration fingerprints, machine identity,
checkpoint lineage, source timestamps and plot-selection metadata.
Configuration bodies, checkpoints and private artifacts are not exported.
CSV escapes untrusted labels to prevent spreadsheet formula execution.
These are **observed training measurements, not benchmark results**. Environment steps account for unequal episode lengths
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
For a non-Bees, non-Ants project, a trusted local adapter may return a
`hub-metrics-v1` observation through its existing `readProgress` method.
The wire contract is strictly bounded: one project-owned experiment ID,
segment ID, ISO source modification time, cumulative nonnegative
`steps_completed`, and 1–12 numeric measurements named by lowercase
slug. Every measurement is `{"value": number, "unit": "scalar|ratio|count|rate|seconds"}`
(the unit must be one of those five literal values). Ratios are 0–1,
counts are nonnegative integers, rates and seconds are nonnegative, and
the payload is capped at 4 KiB. The adapter, not Hub config JSON, owns
the trusted metric source and semantics. Example observational payload:

```json
{
  "schema_version": 1,
  "source": "hub-metrics-v1",
  "experiment_id": "run_1",
  "segment_id": "phase_1",
  "source_modified_at": "2026-11-05T20:00:00.000Z",
  "steps_completed": 1000,
  "metrics": {
    "loss": {"value": 0.42, "unit": "scalar"},
    "accuracy": {"value": 0.81, "unit": "ratio"}
  }
}
```

Generic projects can chart a selected reported measurement against
cumulative project steps. The chart refuses incompatible units, includes
negative scalar values, and exports selected numeric measurements with
their units in CSV/JSON. Generic metric names do not grant stopping,
model promotion or benchmark authority. A concrete project adapter must
still be installed and independently qualified to produce real metrics.

The Ants Compare tab also provides a read-only, curriculum-level
**first-observed rolling-window target** screening table. The operator
chooses a success percentage and minimum number of episodes represented
by each source rolling window. Per selected run, the table identifies
the first retained observation meeting the target, cumulative training
episodes and environment steps at that observation, and the latest
eligible episode count. Runs without a retained qualifying observation
are explicitly censored, not marked as failures. This is bounded and
possibly decimated training history, **not** an independently evaluated
first success, benchmark qualification, or automatic stopping rule.
The table requires a single curriculum level to avoid comparing
unrelated training phases.

No new Ants metric meaning or production behavior is introduced.

The adapter/native lifecycle has been exercised only with bounded
simulations of filesystem, qualification pinning, metrics and systemd
status. **No real Ants training or Linux service qualification has run here.**

### Independent evaluation stops for non-Bees jobs

A draft can set an optional `evaluation_stop` alongside its adapter parameters.
Design accepts a suite ID, minimum trial count and minimum success rate, for
example `{"suite_id":"heldout","min_trials":100,"min_success_rate":0.8}`.
Queueing freezes the rule. Only hosts advertising the requested suite for
that project can claim or start the job. Agents without suite advertisements
remain eligible for jobs without an evaluation stop.

The locally installed adapter declares `evaluation_suites`, an array of one
to eight suite IDs, and provides synchronous `readEvaluation(context)`.
It reads completed, independently generated evidence for the requested suite
and dispatch and returns either null or a version 1 report with `suite_id`,
`dispatch_id`, `checkpoint_sha256`, `trials` and `successes` alongside
`schema_version:1`. The adapter owns evaluator provenance and held-out data
selection. The evaluator-target API lists a job only after a fresh authenticated
heartbeat and a running native observation from the current worker instance;
persisted `running` status alone is not sufficient after a disconnect or
restart. The Hub verifies ownership and the frozen threshold, records the
triggering report, and requests Stop through the existing native supervisor.
The job retains capacity until terminal handling. After a verified native
Stop and capacity release, a qualifying evaluation can advance an independent
`after_job_id` sequence, with fresh allocation and Start authorization for
its successor. It cannot automatically advance a checkpoint continuation:
the last durable checkpoint may predate the Stop, and the existing manual
approval/cancellation rules still apply. Training progress has no stopping
authority.

### Linux terminal recovery

New Linux units write a private `ExecStopPost` receipt before systemd
can unload them. A missing unit can be recovered from the receipt only
after the local supervisor previously verified that job's live kernel
limits. The receipt must match its job and lease. A missing unit without
that evidence remains unverified. The supervisor retains terminal CPU
time when systemd still exposes the counter. Real systemd qualification
must exercise both a natural exit and an explicit Stop.

### Completed-job resource observations

When a native job reaches a verified terminal state, the Hub retains the
highest reported CPU time, the highest sampled RAM, and the observed
time window. Monitor, the JSON comparison export and `get state` expose
the retained summary after restart. Missing counters remain null. The
summary covers the observations still in memory when the job ends, plus
any terminal counters. A coordinator restart before completion can
shorten that window. Sampled RAM does not measure the OS lifetime peak.
Windows owners also report their OS Job Object peak-memory counter
separately. Version-2 signed terminal receipts carry that counter and final
CPU time even if the coordinator missed all live samples. Legacy signed
receipts remain valid and leave those terminal counters unavailable.
Deploy the updated supervisor before updating the pinned Windows owner.

## TinyBrains Ants-MAPOCA adapter

`Training/experiment_hub_tinybrains_adapter.js` provides
`createTinyBrainsAdapter` for fresh Windows CPU training jobs. Prepare a
TinyBrains Ants-MAPOCA checkout with its Windows engine and cartridge,
and install the package into the Python environment the worker will use.
Use a dedicated, existing artifact directory outside the checkout.
Register a Hub project with ID `tinybrains` and include this entry in the
Windows agent's locally approved adapter module:

```js
const {createTinyBrainsAdapter} = require('D:/Hub/experiment_hub_tinybrains_adapter.js');
const fs = require('node:fs');
module.exports = {
    tinybrains: createTinyBrainsAdapter({
        projectRoot: 'D:/TinyBrains/Ants-MAPOCA',
        pythonPath: 'D:/TinyBrains/Ants-MAPOCA/.venv/Scripts/python.exe',
        artifactRoot: 'D:/Hub/tinybrains-jobs',
        profiles: {mini: JSON.parse(fs.readFileSync('D:/Hub/mini-cpu.json', 'utf8'))},
    }),
};
```

The operator prepares each approved profile as a JSON equivalent of a
TinyBrains YAML configuration with `runtime.device` set to `cpu`. The
adapter runs TinyBrains' own validator before advertising profiles. An
invalid profile prevents adapter startup and names the rejected profile.
Compute
the SHA-256 of the UTF-8 bytes of `JSON.stringify(profile, null, 2) + '\n'`
after parsing that JSON. In Design, choose an approved profile from a
connected Windows worker, enter the update count, and select **Use profile
and update count**. The picker fills the draft without saving or starting it.
For API callers, set `profile`, `profile_sha256`, and `updates` (a positive
integer up to 10,000,000). Use a CPU/RAM claim on the configured Windows worker.
The worker advertises approved profile names and digests. The Hub matches
them before claiming capacity and checks again before dispatch. Install
the updated worker before enabling placement on the updated coordinator.
The adapter independently verifies the digest at launch.

Each launch uses a fresh job directory containing its exact profile,
training history, checkpoints and `trainer.stderr.txt` for startup and
runtime errors. A one-CPU, 3 GiB claim supported natural completion and
managed Stop with the isolated Windows smoke profile. A 1 GiB claim
failed during PyTorch initialization on that worker. Size production
claims for the selected profile and environment. Compare uses completed trainer updates
as project steps and shows update wall time and engine steps per second.
Windows Stop terminates the Job Object process tree. A forced stop does
not establish that a checkpoint was saved at the stop boundary.
TinyBrains can continue a naturally finished job after its claim is
released. In Train, choose that predecessor and a draft with the same
profile digest. The continuation runs on the original worker and its
update count is additional. It reads the predecessor's checkpoint and
writes into its own job directory, leaving earlier checkpoints intact.
Train can also queue 2–16 TinyBrains continuation stages atomically.
A successor stays unclaimed until its predecessor finishes and releases
capacity. With native queue automation armed for the original worker,
the Hub can allocate, start, and release each stage without a separate
operator action. In an isolated Windows run, two one-update stages
finished at cumulative updates 1 and 2. Peak outstanding claims was
one and both signed exits survived a coordinator restart with no
capacity held. Stopped jobs are ineligible for continuation. GPU
execution remains outside this adapter's contract.

For evaluation-driven stopping, set the adapter's `evaluationRoot` to a
trusted evaluator-owned directory separate from the checkout and training
artifacts, and set `evaluationSuites` to the approved suite IDs. A separate
evaluator must publish the version-1 Hub result atomically to
`<evaluationRoot>/<dispatch_id>.<suite_id>.json`. Configure the job's
evaluation stop policy for that suite in Design. Training history is
observational.

Run `Training/experiment_hub_tinybrains_evaluate.py` from a separately
budgeted CPU evaluator with a matching TinyBrains checkout and Python
environment. Pass an operator-approved checkpoint, its SHA-256, and
the Hub dispatch it belongs to. The evaluator copies and verifies the
checkpoint before loading it, exports the candidate with TinyBrains'
native checks, and plays Nano on both seats for each map/seed pair.
A win counts as a success. Draws count toward trials. Use held-out
seeds and a stable suite ID for a fixed map, seed, and turn-limit set.

```json
{"suite_id":"nano_heldout","maps":["basic-tiny-2p"],"seeds":[419033,419039],"max_turns":150}
```

```powershell
python -B Training/experiment_hub_tinybrains_evaluate.py --checkpoint $checkpoint --checkpoint-sha256 $digest --dispatch-id $dispatch --suite $suite --scratch $scratch --receipt-root $receipts
```

Use pre-created private scratch and receipt directories. The command above
evaluates one explicit checkpoint. For scheduled evaluation, run
`Training/experiment_hub_tinybrains_eval_poll.py` with `--machine`,
`--artifact-root`, `--suite`, `--scratch`, and `--receipt-root`. The poller
queries the local coordinator (port 7148 by default), selects a completed
numbered checkpoint for an eligible running dispatch, and evaluates at most
one per invocation. Set the profile's `training.checkpoint_every` so a
numbered checkpoint appears while the job is still running. The poller
leaves the changing `latest.pt` alone. Use persistent scratch to retain
checkpoint watermarks across temporary worker disconnects (with the same
1,000-entry limit as Ants), and retry publication independently of coordinator availability
once a completed evaluation is recorded. The trusted adapter and Hub
accept the receipt only for the exact running dispatch and suite.
A separate host needs a trusted loopback forward and read access to the
training artifacts.

## Not yet implemented

Production installation and qualification of project-specific adapters,
real-machine Linux systemd and ROCm qualification, GPU compute and VRAM
quotas, production coexistence qualification of the
CPU-only scheduler, production independent evaluators for non-Bees adapters,
trusted production-source metrics adapters for additional
projects, safe live rebalancing, and comprehensive
CLI parity for all runtime configuration controls remain future work.
Isolated Windows Job Object and browser-controlled CPU-only queue/disconnect
qualifications have passed on real hardware; these do not establish
production Bees/Ants execution safety. These
must not create a second owner of the Bees training lifecycle or treat
reporting a worker as proof of exclusive host ownership.

No arbitrary commands from saved project configurations are executed. The
dashboard remains loopback-only; remote access requires a separate trusted
transport. Production multi-machine / Unity behavior has not been validated
by the development environment.
