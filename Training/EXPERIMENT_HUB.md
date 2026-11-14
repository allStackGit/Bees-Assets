# Experiment Hub foundation

For a compact record of completed implementation and outstanding substantive
work, see [Experiment Hub implementation status](EXPERIMENT_HUB_IMPLEMENTATION_STATUS.md).

The dashboard remains at `http://127.0.0.1:7148` and now has Design, Train,
Monitor, Compare, and Machines tabs. The existing Bees experiment coordinator,
operator, queue, checkpoint history, release compatibility checks, and evaluations
remain authoritative. Project-neutral HTTP administration uses the same
`experiment_hub_admin.js` router in the Bees manager and the disposable
Windows browser qualification; the existing Bees-specific routes remain separate.
The supported research scope is Bees, Courier Ants under Basic Training
(pending its own locally approved adapter), and TinyBrains Ants-MAPOCA.
The unrelated Ants-v2 adapter has been retired and removed. Previously
stored Ants-v2 jobs and leases remain in the private registry for safe
operator inspection and terminal cleanup, but no new Ants-v2 jobs,
capacity claims or native starts are authorized.

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
  as distinct from experiment-owned training. Trusted non-Bees adapters
  can publish unit-typed metrics for cumulative project-step comparisons
  across an operator-selected set of runs, with additional comparison
  slots added on demand. These observations are never treated as
  independent evaluations or stopping criteria.
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
  Connected agents also report current host free RAM and sampled host CPU
  busy percentage across **all** software, including Bees, standalone
  trainers and the OS. GPU inventory reports observed host VRAM use and,
  when the driver provides it, GPU busy percentage. These readings are
  separate from Hub-native process counters and must not be summed with them.
  They expose competing workloads, but are neither attribution to individual
  processes nor guaranteed spare capacity. Before native Start, the Hub uses
  the latest available-memory observation (including reclaimable Linux cache)
  and a shared job's requested free VRAM budget to warn or block a launch.
  Windows and Linux workers recheck locally immediately before launch.
  Minimum VRAM for an exclusive device is a total-capacity selection
  constraint, not a requirement for an entirely idle graphics card.
  These observations can change after Start and are not an isolation limit.
  External workload usage is observational; reserve its expected CPU/RAM/GPU capacity explicitly in
  the Hub budget or a standalone lease before scheduling competing work.
  GPU identities are manually declared and must match fresh observations.
  No agent enforces per-process VRAM or GPU compute quotas.

## Native-only coordinator on Linux (no Unity)

For machines that should run TinyBrains, Courier or other approved native
experiments **without starting the legacy Bees operator**, use the dedicated
native-only coordinator. This is a persistent Hub, not the disposable
`--run-inert-ui` qualification launcher:

```sh
cd "$HOME/ai/projects/experiment-hub/Assets"
node Training/experiment_hub_standalone.js \
    --data-root "$HOME/ai/experiments/experiment-hub"
```

The explicit data root must be an absolute private directory (mode 0700)
outside the checkout. The coordinator binds the administrator dashboard
only to `127.0.0.1:7148` and the authenticated, machine-scoped agent listener
only to `127.0.0.1:7149`. It serves the existing Hub Design, Train,
Monitor, Compare and Machines controls but **does not import Bees training,
Unity or the Bees manager**, and Bees operator actions are unavailable. A
fresh registry starts with inert TinyBrains and Courier project definitions;
a definition is not an installed or authorized execution adapter.
Native automation starts disarmed. Stop the coordinator with Ctrl+C after
disarming any live automation. Native owners, claims and checkpoints are
not released merely because the coordinator exits.

Do not run the legacy Bees coordinator against this same data root, and do
not copy or merge a prior Hub registry over an active one. The existing
`experiment_hub_systemd.js coordinator` unit still runs
`bees_experiments.js`; it is **not** a service unit for this native-only
entry point. The initial standalone coordinator should be launched
explicitly in a foreground terminal; its real-machine service recovery
and multi-host production operation remain to be qualified.

Register and enroll `halifax` under **Machines**, preserving the one-time
machine token in a private local agent environment. For a co-located agent
use `EXPERIMENT_HUB_URL=http://127.0.0.1:7149`. Start with observation-only
agent heartbeats and verify it appears Connected before enabling any local
native adapter. Native Linux Starts still require explicit
`EXPERIMENT_HUB_ENABLE_EXECUTION=1`, a locally trusted adapter map, a
separate private supervisor journal, the current session's systemd preflight,
and a fresh authenticated Hub lease. Do not treat earlier inert service
qualification as production job authorization.

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
before assigning flexible workloads. Among otherwise eligible placements,
CPU-only jobs favor hosts reporting lower **host-wide** CPU activity (including
Bees), while single-GPU jobs favor greater observed physical free VRAM and
then lower GPU activity. The worker's fresh heartbeat provides these
observations; absent values never imply that a host or GPU is idle. The
Machines preview shows the relevant load alongside proposed placements.
These are rankings, **not** admission credits, performance guarantees or
automatic external workload reservations. The planner is not an exhaustive
global optimizer and does not mutate persistent jobs, reservations, or
machines. It may preview physically registered but unverified machines.

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
For TinyBrains, a saved configuration can continue an eligible predecessor
through `continuation_of_job_id`. The Hub freezes the artifact lineage
and initial machine affinity. Waiting successors have no capacity claims
and cannot start until a verified `finished` predecessor releases native
capacity; stopped, failed and unverified predecessors cannot advance a
checkpoint continuation. Multiple TinyBrains stages can be queued as
a single linear pipeline and the worker is pinned when the first stage
dispatches. A prequeued child is never a second execution owner.
The operator can cancel unstarted blocked stages, and the Monitor view
shows the blocking ancestor without automatically retrying uncertain work.

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
The latter freezes **2–16 TinyBrains stages in one atomic registry write**,
accepts an optional `continuation_of_job_id` for the initial stage,
and rejects the entire pipeline if any configuration, profile digest,
revision, resource request or predecessor is invalid. Later stages
inherit the original machine and checkpoint lineage. The Train tab
provides a visual pipeline builder.

For a seed study or parameter sweep, save one configuration per variant
and choose **Independent trials (no dependency)** in Train. The API is
`POST /api/hub/trials` with `{"confirm_queue":true,"stages":[...]}`.
All 2–16 saved revisions and resource requests are frozen atomically.
Every job starts with its own artifact lineage and is independently
eligible for capacity. Queueing issues no claim or Start authorization.
Train and Compare display the batch and original trial position. JSON
and CSV exports include the full batch ID so runs can be joined later.
Train can cancel the remaining queued trials of a batch atomically.
`POST /api/hub/trials/cancel-queued` requires `trial_batch_id`,
`expected_job_ids`, and `confirm_cancel:true`. A changed queued set rejects
the request. Dispatched trials remain under their native supervisor.
Cancel any unresolved candidate selection before cancelling its trials.

For **independent experiments**, including mixed approved non-Bees
projects, the same Train editor offers
`POST /api/hub/sequences` with
`{"confirm_queue":true,"stages":[...],"after_job_id":"optional-existing-job-id"}`.
All 2–16 stages freeze their saved-configuration revisions and requested
resources atomically. Their `after_job_id` dependencies are separate
from checkpoint `continuation_of_job_id`: each stage has its own
artifact lineage, with **no inherited weights, optimizer, checkpoint,
machine affinity or evaluation result**. Jobs may run on different
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
as the dashboard. Use `get 'progress?project_id=tinybrains'` for project history
or `get jobs/plan` to preview capacity. POST commands take an explicit JSON
file, for example `post jobs/move-claim --data move.json`. Use the same
fields and confirmation values as the dashboard API. The CLI also accepts
the existing `bees/control`, `bees/workers`, `bees/start`, `bees/stop`,
`bees/build`, and `bees/update-workers` routes on the production manager.
Requests use loopback only and default to port 7148. The same CLI also
supports Bees experiment coordinator JSON APIs under `manager/`, for example
`get manager/state`, `get 'manager/metric-history?id=EXPERIMENT_ID'`,
`post manager/queue --data queue.json`, `post manager/pause --data empty.json`,
and `post manager/resume --data empty.json`. These are experiment-owned
actions, not the separate `bees/start` and `bees/stop` operator routes.
The CLI uses an explicit list of supported JSON endpoints, retains the
existing server-side guards and confirmation requirements, and does not
support binary downloads or arbitrary process paths.

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
running Bees, TinyBrains, or other software. The Hub does not yet know how much GPU
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
on those machines. The automation session is process-local and starts **disarmed**. By
default a coordinator restart also disarms, unless the operator has
explicitly opted into the CPU-only restoration policy below. A second Arm
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

After restarting the coordinator, the operator may explicitly re-arm
automation with **Re-adopt previously automated native jobs** selected.
This can manage terminal capacity release for earlier automatic jobs
only after the current authenticated agent has freshly inspected each
running or terminal dispatch and acknowledged its matching native lease.
Adoption does not relaunch a service, repeat an ambiguous Start, or
resume a stopped job. A verified terminal report is released through
the same checkpoint and dependent-stage eligibility checks as a normal
automated completion; an ineligible handoff disarms for review.
Unreleased jobs with missing, stale, conflicting, or uncertain native
evidence block adoption until the operator inspects or releases them.
Re-adoption does not bypass host preflight or checkpoint continuation proof.

For unattended coordinator or agent recovery, the operator can separately check
**Automatically restore CPU-only automation after coordinator or agent restart**
when arming. This stores explicit, revocable permission in the Hub
registry; GPU claims, probes and Starts cannot use this option. The API uses
`resume_after_restart:true`, `confirm_restart_rearm:true` and the separate
`confirm_agent_restart_rearm:true` confirmation. Older saved permissions and
API requests without the last confirmation continue to cover coordinator
restarts only; disarm and re-arm to authorize agent recovery.
On coordinator startup or loss of agent continuity, scheduling pauses until every selected
machine reauthenticates, its preflight passes, every outstanding
previously automated dispatch has fresh native inspection and its
matching lease is acknowledged. A pending restart is visible in Train
and can be cancelled using Disarm. Restoring permission re-adopts
verified previous jobs; it never repeats an ambiguous Start or Stop.
Restoration only arms the scheduler: a later pass decides whether any
new job may Start. Unverified/failed services, in-flight Starts or
Stops or failed safety checks require operator review and revoke saved restart
permission. Credential rotation or revocation also cancels recovery; it is
not treated as an agent process restart. A replacement agent must pass the
existing active-instance exclusion period and re-inspect the original native
dispatch before recovery can use its observations. Normal
operator disarming also revokes it. The coordinator process itself
still needs an independently installed OS service supervisor to be
restarted; this option does not install one or enable machine boot startup.

An isolated Windows check ran the production agent through the compiled
launcher's `--run-once` path, killed and relaunched that launcher, and observed
the same native workload PID, dispatch and lease after fresh reconciliation.
Explicitly authorized agent recovery restored CPU scheduling, started the
second queued job once and released both signed terminal claims. A separate
run with coordinator-only permission remained disarmed. These checks do not
qualify production training, deployed service-account permissions or Linux.

In an isolated 90-second Windows Courier run, closing the worker listener
and reopening the coordinator and supervisor preserved the original
dispatch and lease. CPU automation restored after reconciliation, and a
later evaluation stopped training at round 7 with a signed successful exit
and released capacity. A separate run terminated and relaunched the
isolated coordinator process while the original worker stayed active.
In a 150-second Windows run through the production entry point with an
isolated registry and ports, its timer restored the original Courier
dispatch after process termination and relaunch. The same native owner
saved round 10 on Stop, and the Hub released its claim. The production
process logs a successful restoration once. OS service restart and host
reboot remain unqualified.

While armed, a coordinator pass every ten seconds performs **at most one
durable transition**: release capacity only for a job started in the
**current automation session**, or explicitly re-adopted after restart,
after an authenticated native `finished` or `stopped` report (activating an eligible prequeued successor in the
same write after `finished`), authorize one eligible Start that passes
the same live readiness gate as the manual Start button, or claim available
CPU/RAM budget for an unassigned CPU-only non-Bees job or, when opted in,
an exclusive single-GPU job. Jobs completed under older sessions require explicit capacity release
unless their manual Start opted into terminal release, or the operator
explicitly re-adopted their freshly verified running or terminal dispatch. A machine cannot
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
acknowledge each fresh claim. CPU-only is the default. Native single-GPU jobs can enter automation
only after explicit GPU claim and Start authorization. Linux ROCm also
requires a current device-denial probe for exclusive leases; Windows
CUDA allows locally approved exclusive or cooperative shared leases
with a pinned device UUID. Multi-GPU native jobs and unapproved GPU
sharing remain excluded. Existing Bees training remains under its own operator.

A failed or unverified managed job, disappearing/replaced agent, or
failed coordinator operation disarms the session without retrying an
uncertain native launch or freeing uncertain capacity. A manually stopped
stage with a queued successor also disarms after verified capacity release:
the successor stays blocked until operator review rather than permitting
unrelated queued jobs to continue silently. Stop/recovery of failed jobs
remains manual. Isolated Windows qualifications have verified
native Job Object CPU/RAM enforcement, concurrent automatic scheduling,
terminal receipt/release, dashboard controls, and heartbeat-loss disarming
on a real host. **Production Bees/TinyBrains coexistence and unattended project
training are still unverified**; real Linux services and GPU isolation remain
unqualified. The **generic non-Bees scheduler** supports frozen single-suite and
same-checkpoint multi-suite early-stop gates with verified,
checkpoint-preserving transitions to prequeued continuation stages.
It does not yet support Bees-style promotion between independently
competing policy lineages, dynamic live resource balancing, or enforced
performance shares. The existing Bees experiment manager separately
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
feature does not map Bees trainer IDs to physical machines, attribute
individual resource usage, or automatically change the Hub's reserved
budget when Bees or other training activity changes. Observed aggregate
host CPU/GPU activity can influence the relative order of eligible placements
but cannot prove or reserve spare execution capacity.
It is a conservative *operator-chosen admission budget*, not isolation,
a reserved CPU core set, or a throughput guarantee. For uncertain
coexistence, use a dedicated host or a much smaller Hub budget instead
of assuming Bees workers consume only what their status metrics suggest.

The native Linux and Windows supervisors also refuse a new Start if
locally journaled unresolved Hub workloads plus the requested CPU/RAM
would exceed the host's physical capacity. A stopped but quarantined
or unverified job still reserves local admission capacity. This protects
against an inconsistent coordinator view; it does **not** count Bees
or other independently started processes, so the operator's smaller Hub
machine budget and physical free-memory check remain important.

The shared loopback route is `POST /api/hub/machines/budget`, using
`{machine_id,cpus,ram_mb,expected_cpus,expected_ram_mb,confirm_budget:true}`.
Existing Hub machine identity, enrollment, GPU bindings, leases and jobs
remain unchanged; the same capacity planner serves manual allocation,
automatic CPU-only admission and placement preview.

For a machine that also runs independently owned GPU workloads, **Machines →
Adjust Hub GPU scheduling budgets** sets `hub_vram_mb` and
`hub_compute_pct` on each configured GPU. This is a per-device ceiling
on new Hub GPU **admission**, separate from the physical VRAM and observed
host utilization; it is not an enforced compute or memory quota. Zero
disables new GPU placement for that device. Older GPU inventories without
these fields default to the full nominal VRAM and 100% compute budget.
A device withheld partially cannot accept a new **exclusive** Hub GPU
claim; cooperative shared claims can use the remaining budget only where
the locally approved executor actually supports sharing. Linux ROCm
jobs currently require one exclusive GPU, so a partial Hub budget
blocks those jobs rather than silently sharing with Bees.

The administrator route is `POST /api/hub/machines/gpu-budget` with
`{machine_id, expected_gpus, gpus, confirm_budget:true}`. The two arrays
are the exact previously displayed GPU configuration and the proposed
same-ID/same-PCI/same-nominal-VRAM configuration, including the budget
fields. An out-of-date expected snapshot, an identity change, armed
automation or any outstanding GPU-bearing lease is rejected.
The update is one atomic registry write, never a resize, Stop or
relabeling of an owned GPU. Rebinding an existing logical GPU ID retains its
prior Hub admission ceiling (clamped to any smaller new nominal VRAM), even
when an older caller omits these budget fields. Bind physical GPUs separately and account for
unmanaged software explicitly; no Hub admission policy controls the
resource use of unrelated processes.

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
   plus a machine-scoped, read-only independent-evaluation worklist GET.
   It cannot access any administrator or training route. A remote worker
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
verification are all required. Native Windows execution may explicitly opt into one cooperative CUDA device; Windows Job Objects still enforce only CPU/RAM. The Job Object backend has
been exercised in isolated Windows runs, but those results do not
establish qualified production workloads or the latest helper build.

### Opt-in Linux service startup and process-failure recovery

The project includes a **Linux user-systemd unit generator** for the
coordinator and the authenticated machine agent. This installs no service by
default, requires an explicit `--install`, and never enables or starts
either service itself:

```sh
# Review the generated coordinator unit before installing it.
node Training/experiment_hub_systemd.js coordinator
node Training/experiment_hub_systemd.js coordinator --install
systemctl --user daemon-reload
systemctl --user start bees-experiment-hub.service
systemctl --user status bees-experiment-hub.service

# On the machine running the Hub agent, store the machine ID, one-time
# enrollment token, and any locally approved execution opt-ins in a private
# environment file outside the checkout (chmod 600).
node Training/experiment_hub_systemd.js agent --env-file /absolute/private/hub-agent.env
node Training/experiment_hub_systemd.js agent --env-file /absolute/private/hub-agent.env --install
systemctl --user daemon-reload
systemctl --user start bees-experiment-hub-agent.service
```

Use `--training-dir` for an installed Training checkout outside this
script's current directory and `--node` for a pinned absolute Node binary.
The generator resolves paths, rejects unsafe unit syntax, refuses symlinked
or group-readable secret environment files, and refuses to overwrite an
existing unit. It emits no enrollment token. Units use systemd's bounded
`Restart=on-failure` rather than an in-process watchdog. Repeated failures
hit systemd's start limit and require inspection, not an endless relaunch.
The coordinator unit uses `KillMode=process` deliberately: its Bees
operator manages learner/worker lifecycles independently, and restarting
only the coordinator must not kill their processes. The agent uses
`KillMode=control-group`; its native training workloads belong to
independent transient systemd units with their own journals and leases.

Services are started explicitly for the current machine session. Generated
units have no installation target, so this setup does not enable boot or
login startup. After a reboot, leave the Hub stopped until an operator
starts it again. If upgrading a previously enabled unit, remove its existing
boot/login links with `systemctl --user disable UNIT` (without `--now`);
replacing unit text alone does not remove those links. Process-failure
recovery applies only to a service that
was already started. Expired credentials, missing private state, occupied
ports or a failed preflight cannot be treated as confirmed training recovery:
the Hub must reconcile exact native service and lease identities before
restoring any previously opted-in unattended CPU scheduling. No GPU
re-arming is authorized by this service setup. This source-only Linux
integration is **not yet qualified on Halifax**.

The same generator can install a separately budgeted **periodic independent
evaluator** on a trusted Linux host, without adding any native
training authority. Use `evaluator --project courier|tinybrains --machine
MACHINE_ID --python /absolute/python --suite /absolute/suite.json
--artifact-root /absolute/artifacts --scratch /absolute/scratch
--receipt-root /absolute/receipts --cpus 2 --ram-mb 4096`. Courier also
requires `--project-root` and `--snapshot-root`; TinyBrains on Linux
requires `--engine-path`. Repeat `--suite` for each independently required
suite in one coordinated same-checkpoint evaluation batch. The timer defaults
to a 180-second interval after the previous invocation finishes and a
two-hour timeout; optional `--interval` and `--timeout` are seconds.
Inspect generated service/timer units before using `--install`, then
explicitly start the generated `...-evaluator.timer` for this machine session. Units are
exclusive-create and not automatically enabled. systemd prevents overlapping
runs of the same one-shot service and enforces the specified CPU and RAM caps.
The evaluator still needs its approved project dependencies and access to
the model/receipt directories. On a coordinator-local machine the generator
defaults to the historical dashboard loopback port 7148; this must **never**
be forwarded to a remote worker. For a remote worker, add
`--env-file /absolute/private/hub-agent.env --port 17149` (the port of
the already established, trusted SSH forward to the coordinator's agent
listener at 7149). The file must be owned by the service user, mode 0600,
and contain that worker's enrolled `EXPERIMENT_HUB_AGENT_TOKEN`. The
generator passes the file through the systemd `EnvironmentFile` directive;
the Python evaluator sends the token only as an HTTP request header to the
restricted agent endpoint, never in its URL, command line or unit contents.
With `--env-file`, the generator defaults to port 7149 if the worker
is co-located with the Hub. The port 7148 dashboard route remains available
for already-installed coordinator-local evaluators without an environment file.
The agent endpoint returns only this machine's current eligible independent
evaluation targets, and native ownership and checkpoint verification remain
separate. A token alone cannot start, stop or promote work through this route.
CPU quotas are not exclusive cores, and these timers are not a substitute
for production coexistence qualification.

### Opt-in Windows service recovery, with manual startup

Task Scheduler launched an inert Node program successfully but did not retry
its nonzero exit in the isolated checks. Use the Windows service launcher for
the intended OS-owned recovery path, rather than relying on a logon task.
The launcher has no internal restart loop. Coordinator and agent modes own
their direct Node process; independently detached training owners keep their
existing lifecycle. Evaluator mode bounds and owns its complete process tree.

Build the reviewed launcher locally, then preview its installation:

```powershell
dotnet publish .\Training\experiment_hub_windows_service\ExperimentHubWindowsService.csproj -c Release -r win-x64 --self-contained true -o C:\Hub\Service

.\Training\experiment_hub_windows_service.ps1 -Role coordinator -ServiceExecutable C:\Hub\Service\ExperimentHubWindowsService.exe -ConfigurationFile C:\Hub\Private\coordinator.json -StateDirectory C:\Hub\Private
```

The executable, configuration parent directory and state directory must
already exist. Use `-NodePath` and `-TrainingDirectory` to pin installed
absolute local paths. Agents additionally require `-EnvironmentFile`; coordinators
can use it too. Environment files require Node 22 or newer. Their Hub settings
replace inherited `EXPERIMENT_HUB_*` settings. No arbitrary executable or command
comes from a queued project configuration.

After review, an administrator may repeat the command with `-Install`.
It creates a **disabled** LocalService service and an exclusive-create local
configuration. It refuses to overwrite an existing service or configuration.
Grant the chosen service account only the required read/execute access to
the installed launcher, Node, checkout and private environment file, plus
write access to its state and approved Hub data directories. Review account
access before starting; installing a service does not qualify these permissions.
Tokens are not embedded in the service command line.

In Services, select **Manual** startup and start the service explicitly.
Do not select Automatic or Automatic (Delayed Start), add a logon task, or
enable another boot launcher. A reboot leaves the Hub stopped until an
operator starts it again. Recovery requests three restarts, 60 seconds apart,
then no further action; the failure counter resets after a failure-free day.
An explicit service stop is a successful stop and requests no restart.

The service writes one bounded latest-lifecycle JSON file per service in
`-StateDirectory`, recording the observed Node identity and child exit status.
It also timestamps application console output and uncaught JavaScript failures
in `<service-name>.stdout.log` and `<service-name>.stderr.log`. Each channel
keeps its current file and one `.previous` file, capped at 1 MiB each (4 MiB
total per service). Oversized messages retain a marked tail. Keep this
directory private; diagnostic output can contain local configuration details.
The service logger does not intercept failures or change child exit status.
Direct writes outside JavaScript console and errors before the Node preload
runs are not captured by these files.

For local configuration diagnosis,
`ExperimentHubWindowsService.exe --run-once C:\Hub\Private\coordinator.json`
uses the same native ownership path without installing a service or retrying.
Its application console output goes to the same bounded files.
Confirm the existing owner is stopped before using this command.

Before console capture was added, isolated Windows checks exercised LocalService installation and
Service Control Manager recovery after both a child failure and a service-host
crash. Explicit stop did not restart the service; detached child processes
survived. A separate native check observed four failed launches (the initial
launch plus exactly three retries), then a stopped service with no further
restarts. Test-only retry delays were one second; the installed 60-second
delays were inspected through the native service configuration.

The console-enabled build passed suspended launch, environment files,
failed-child status and exit-code propagation through `--run-once`. Actual
console and uncaught-error output remained bounded across repeated failures,
rotation, an oversized message and a successful exit. A production-agent
process check also recovered its original inert native job, advanced a second
job once and released both claims with console capture enabled.

Current-build LocalService/SCM qualification, reboot behavior, real training
owner reconnection, deployed account access, production coexistence and
long-duration operation remain unqualified. All earlier temporary services
were removed; no production service was installed.

### Budgeted Windows independent evaluators

Use the same launcher with `-Role evaluator -Cpus 1 -RamMb 3072`. Its
private environment file points to an administrator-owned JSON configuration:

```text
EXPERIMENT_HUB_EVALUATOR_CONFIG=C:\Hub\Private\tinybrains-evaluator.json
```

```json
{
  "schema_version": 1,
  "project": "tinybrains",
  "machine": "worker",
  "python": "C:\\Projects\\TinyBrains\\.venv\\Scripts\\python.exe",
  "engine_path": "C:\\Projects\\TinyBrains\\tools\\tinybrains-0.5.0\\tinybrains.exe",
  "suites": ["C:\\Hub\\Private\\heldout.json"],
  "artifact_root": "C:\\Hub\\Data\\Artifacts",
  "scratch": "C:\\Hub\\Data\\EvaluationScratch",
  "receipt_root": "C:\\Hub\\Data\\EvaluationReceipts",
  "interval_seconds": 180,
  "timeout_seconds": 7200
}
```

All paths must already exist. The artifact, scratch and receipt directories
must be separate. For Courier, use `"project":"courier"` and add
`project_root` and `snapshot_root`, also separate; omit `engine_path`.
The optional loopback `port` defaults to 7148 **only for a trusted
evaluator running beside the coordinator**. For a Windows worker on a
separate machine, set `"port":17149` (or the worker's actual local SSH
forward to the coordinator's restricted agent listener on 7149), and
include `EXPERIMENT_HUB_AGENT_TOKEN=<that worker's enrolled token>` in
the private service environment file. An `EXPERIMENT_HUB_MACHINE_ID`
entry, if present, must match the evaluator JSON's `machine`. The
Windows evaluator checks the token's format and machine identity
before beginning its polling loop; the coordinator independently
authenticates every worklist query and returns only that worker's
eligible jobs. Never forward the privileged 7148 dashboard listener to
ordinary worker hosts. The service account needs access to its private
environment file and the existing SSH forward, not the dashboard.

Supply 1–8 distinct suite paths; multiple suites run in one
same-checkpoint batch. Intervals and timeouts accept 60–86,400 seconds.
A new poll waits until the previous poll has finished, then waits the
configured interval. Configuration is read once at startup and its
exact file hash is recorded; restart to apply edits.

```powershell
.\Training\experiment_hub_windows_service.ps1 -Role evaluator -Name Bees-ExperimentHub-evaluator-tinybrains -Cpus 1 -RamMb 3072 -ServiceExecutable C:\Hub\Service\ExperimentHubWindowsService.exe -ConfigurationFile C:\Hub\Private\evaluator-service.json -StateDirectory C:\Hub\Private -EnvironmentFile C:\Hub\Private\evaluator.env
```

Preview installation before using `-Install`, then select **Manual** startup
in Services and explicitly start it. The account needs its approved Python,
project dependencies, engine and cache, read access to checkpoints/suites and
write access to evaluator scratch, receipts and service state. Use distinct
service names for different evaluation profiles. No boot or logon startup
is enabled by this setup. Before installation, the compiled launcher's
`--run-once` command with an evaluator service configuration performs exactly
one constrained poll without registering a service.

The Windows Job Object applies CPU and committed-memory caps to the Node
controller and its complete evaluator subtree. The controller verifies native
job membership and limits before invoking the installed project poller.
Verified Windows console hosts stay inside those budgets; an unexpected
application child prevents another poll. Timeouts and launcher termination
close the evaluator job, stopping its tree. Coordinator/agent mode retains
its existing independent-training ownership behavior. These CPU quotas do
not reserve exclusive cores or isolate GPU devices. Account for the extra
evaluation budget when declaring shared-host training capacity; it is not
automatically deducted from the Hub's training registry.

`<service-name>.evaluation.json` retains the latest poll outcome, exact local
configuration hash, elapsed time, observed service-job CPU counter delta and
the service job's lifetime peak committed memory. A completed poll can have
no eligible target; experiment outcomes still come from checkpoint-pinned
receipts. Python failures are logged and the next scheduled poll can retry
after its complete tree has exited. Containment failures and timeouts stop
the controller and use the service's bounded OS recovery policy.

Windows diagnostic-mode checks exercised actual native limits, periodic
non-overlap, duplicate-owner refusal, allocation beyond the memory cap,
stray-child rejection, timeout/tree cleanup and launcher crash cleanup.
Production pollers completed 24 TinyBrains games and 16 Courier held-out
survival scenarios under a 1-CPU / 3-GiB budget with pinned receipts and no
owned workspace left. These checks did not install a service or qualify its
deployed account, production coexistence, long-duration operation or Linux.

## Linux native supervisor and authenticated dispatch (CPU/RAM, opt-in ROCm)

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
cores**. CPU-only jobs need only the normal native preflight; optional
single-GPU ROCm jobs additionally require an explicit, current GPU
qualification for the exact exclusive lease, the systemd device policy
and the in-cgroup HIP runtime proof documented below. Shared Linux
GPU claims remain unsupported. The backend uses no privileged shell,
no `sudo` and no Windows process fallback.

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
It uses **no training process**, does not touch Bees or user training artifacts,
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
working TinyBrains, LLM, or other project runners on a machine.** Local
adapter configuration and authentication secrets must not be committed.

The agent generates a fresh process-instance identity when it starts.
While a native agent's authenticated heartbeat is fresh, a different
instance cannot take over its machine identity or receive native
instructions. Replacement agents become eligible after that instance
expires (45 seconds); this does not release job leases or bypass
the deterministic systemd unit and local ownership ledger. The
worker-only endpoint also enforces a 64-KiB heartbeat request byte limit,
independent of the 32-KiB trainer-progress envelope. The agent checks
complete request size before sending; oversize messages cannot update native
ownership, leases or checkpoints.

The agent sends a native report only after its local systemd supervisor
inspects exact service identity, systemd `CPUQuota`/`MemoryMax`
properties, kernel `cpu.max`/`memory.max` limits, and process membership.
While an agent awaits a bounded, long-running native command (including
ROCm qualification), its existing 15-second heartbeat interval continues
with `request_execution_orders:false`. That busy heartbeat refreshes
machine liveness and acknowledged lease receipts but cannot issue another
Start, Stop or GPU probe. The currently executing command retains exclusive
local ownership, and its result is reported only after any in-flight busy
heartbeat completes.

For `experiment_hub_agent.js --once`, the final heartbeat uses both
`request_execution_orders:false` and `close_agent_session:true`, even when
there is no new work. It acknowledges completed work, closes that native
agent session immediately, revokes ephemeral lease-receipt freshness and
cannot persist another Start offer. Durable native jobs and lease claims
are **not** released or restarted. A replacement process may enroll
immediately without waiting for heartbeat expiry, but must freshly
reconcile previously owned native work. One-shot sign-off disarms armed
native automation; unattended scheduling requires a continuous agent.
Repeated launch instructions are reconciled against the durable local
journal, never executed twice. One directive is delivered per heartbeat:
stops take priority; pending starts and overdue native inspections share
a fair rotation so neither can indefinitely starve the other. GPU
device probes and fresh native inspections follow. Explicit checkpoint
reinspection remains ahead of ordinary fresh inspection. Jobs rotate
fairly within each priority level using independent cursors. Once an
authenticated native report confirms local
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
telemetry or project comparison samples; late state regressions and duplicate
terminal reports cannot overwrite a completed job's last valid metrics.
Live learning progress retains its latest accepted source snapshot when
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
production Windows coexistence, enforceable GPU isolation, and cross-project
throughput/rebalancing remain unqualified. Isolated Windows Job Object
qualifications do not establish the same guarantees in production.

## Physical AMDGPU identity and bounded advisory allocation

The Linux agent now observes AMDGPU cards directly from the kernel's
\`/sys/class/drm/card*/device\` sysfs hierarchy. Reports contain only a
PCI bus/device/function address, total VRAM MiB and (when available)
currently used VRAM MiB. Missing drivers, missing VRAM counters and
disappearing devices are treated as **unavailable**, not inferred from
model names or manually registered nominal capacity. PCI slot addresses
are stable labels only while the machine's hardware topology remains
unchanged; they are not permanent GPU serial numbers. Windows agents independently discover NVIDIA PCI addresses, total/used VRAM and CUDA UUIDs using read-only `nvidia-smi`. Missing used-VRAM counters remain unknown; unavailable or malformed driver telemetry leaves CUDA GPUs unclaimable without disabling CPU heartbeats. Observed used VRAM is not a safe per-job free-space guarantee and does not enforce a reservation.

Registration may include a GPU's \`pci_address\`, for example:

    [{"id":"gpu0","vram_mb":32768,"pci_address":"0000:03:00.0"}]

For an already registered machine, the Machines tab provides
**Bind physical GPUs**. The operator selects a connected Linux AMDGPU or Windows NVIDIA
machine, reviews observed PCI addresses, and confirms a
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

Only connected machines with a fresh matching physical GPU report expose
\`verified_gpus\` to the Hub's capacity planner. The available per-device
budget is capped at the **smaller** of registered and reported VRAM;
manual names without matching PCI evidence contribute **zero GPU
allocation capacity**. Each lease freezes its selected logical GPU IDs.
The coordinator sends the expected physical PCI mapping (and on Windows the CUDA UUID) back to the
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

The Linux supervisor has an explicitly invoked
`qualifyGpuDeviceAccess(bindings, runtimeProof)` method. It first proves that
the same ordinary user can open the selected render device, every resolved
excluded AMD render device, and /dev/kfd. A uniquely named systemd user
service then applies DevicePolicy=closed and DeviceAllow entries for only
/dev/kfd and the selected render node. The in-cgroup probe requires
the selected device and /dev/kfd to remain accessible while every
baseline-openable excluded GPU is inaccessible.

For a native job, the agent constructs `runtimeProof` solely from the
installed trusted adapter and the frozen job/lease. The same short-lived
service also invokes that adapter's fixed ROCm verifier under the pinned
`ROCR_VISIBLE_DEVICES` selector, requiring the HIP/PyTorch device PCI
identity to match the leased GPU. It performs only a tiny compute operation:
**no trainer or optimizer starts and no training artifacts are created**.
An old direct call with just `bindings` still runs an access-only device
probe; the machine agent explicitly rejects that narrower result as native
Start qualification. A wrong/unsupported selector is rejected before a
training ownership journal is written.

The supervisor rechecks PCI and character-device identity after cleanup.
Unavailable device filtering, a mismatched runtime, ambiguous unit, or
uncertain cleanup blocks qualification. Each job needs a fresh proof.
Before dispatching, the supervisor writes the unique unit identity to
`<EXPERIMENT_HUB_LOCAL_ROOT>/gpu-preflight-pending.json`. Device-only
probes retain a 5-second start and 10-second runtime bound; native job
probes use a 25-second start and 35-second runtime bound with memory
limited to the requested job RAM (maximum 4 GiB). Both use a 3-second
stop bound. The record is cleared only after the exact service is
confirmed stopped or absent; uncertain cleanup or an agent crash holds
the record across restarts.
To recover, inspect the recorded unit with `systemctl --user show <unit>`
from the same Linux account, verify its exact identity and that it is
stopped/absent (stop the matching unit if necessary), and only then
remove the pending JSON record manually. Never clear it on the strength
of a stale agent heartbeat. This first version requires at least two
openable AMD render nodes and one selected GPU per proof.

The Train tab offers **Check GPU device access** for an unstarted
non-Bees job with an acknowledged, exclusive single-GPU lease on a
qualified Linux agent. This action now verifies device denial **and** the
installed HIP runtime's physical GPU identity without running training.
The operator must explicitly confirm the action.
`POST /api/hub/jobs/qualify-gpu` records a request identity
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

### Halifax TinyBrains persistent PPO through Docker (opt-in)

The Linux agent can use `EXPERIMENT_HUB_LINUX_BACKEND=docker` instead of the
native systemd owner when **only** the approved TinyBrains Docker adapter is
installed. Existing native jobs must be terminal and native GPU proofs cleared
before switching owners; Docker and native jobs cannot coexist under this
agent owner. The backend uses Docker's cgroup-v2 CPU/memory controls, a
single leased AMD render device plus `/dev/kfd`, verified physical HIP PCI
identity, read-only source mounts and a persistent, named container per job.
The host's Docker service, not a systemd client process, owns the workload.
The local journal and unresolved GPU proof are retained across restarts;
ambiguous operations are never automatically retried or removed.
Running Docker owners also expose optional read-only cgroup-v2 CPU time
(`cpu.stat: usage_usec`), memory use/peak, and task counts when the host
process cgroup matches the verified full Docker container identity.
The Hub derives job CPU-core usage from consecutive cumulative CPU samples;
missing or inaccessible counters are not interpreted as zero. If available,
`job_cpu_throttled_seconds` and `job_cpu_throttle_events` expose cumulative
CPU-quota throttling from the verified Docker or native Linux
systemd cgroup. The cumulative `job_cpu_quota_periods` counts eligible
quota intervals; `job_cpu_quota_throttled_periods_pct` is computed from
the changes in throttled events and quota periods across continuous
authenticated reports of the same owner. A missing, reset, or nonadvancing
denominator produces no percentage. These measures describe local quota
pressure, not necessarily all CPU contention: inherited ancestor limits
and other scheduling delays may not appear. Totals are not per-update PPO
phase timings, and their growth is not a measure of marginal throughput.
These kernel counters describe the owned workload, not GPU kernel efficiency.

For Halifax, create the dedicated
`~/ai/experiments/experiment-hub/tinybrains-jobs` directory and use
`Training/experiment_hub_tinybrains_halifax_adapter.example.js` as the
locally approved `EXPERIMENT_HUB_LOCAL_ADAPTERS` module. It pins the
verified imported 7,199-update checkpoint, original run evidence and
persistent PPO profile. The original
`~/ai/experiments/tinybrains/mini-learning` is mounted read-only; the
source checkout is read-only; each Hub job copies only its initial
`self-play/latest.pt`, frozen spec and gate checkpoint to its own writable
job directory. The original STOP file stays in the source run and is never
removed by the Hub. A staged Hub continuation uses a new job ID and retains
the previous job's frozen opponent files. The TinyBrains checkout mounted into
Docker must include support for `learning_path --max-ppo-updates` and portable
frozen-opponent path resolution. Each Hub job's `updates` is an *additional*
update count: the Linux TinyBrains `--max-ppo-updates` option accepts this
per-invocation count and can extend training beyond the original frozen run
spec's ceiling. The source run spec and imported checkpoint are not rewritten.

The locally trusted TinyBrains persistent adapter can use
`cpuParallelism:'allocation'` to select its CPU thread count once from the
job's frozen CPU lease instead of the profile's default threads. Explicit
`threads` in a frozen job configuration still wins, preserving existing
jobs. This is fixed-allocation concurrency, **not** measured optimal
threading or an adaptive tuner; test larger core counts before asserting
a throughput benefit. The Halifax example enables this mode. The default
for other installations remains `'profile'`.

The Halifax example now opts into TinyBrains' `TINYBRAINS_PPO_PROFILE=1` via its
locally trusted Docker adapter. It records per-update PPO host-wall stages
(data preparation, forward/loss, backward, metric readback, gradient
processing, Adam step, finite checks), minibatch counts, and board-shape
workload in the normal job history and Hub Compare/export data. These times
include existing GPU synchronization and are **not** GPU kernel-only times.
The profiler adds no extra CUDA synchronization; disable it by setting
`profilePpo:false` in the locally approved adapter for normal throughput
runs. The fixed profile digest, checkpoint lineage and PPO settings do not
change when profiling is enabled. The TinyBrains source checkout must have
the matching instrumented optimizer before enabling the adapter flag.

The agent needs *noninteractive* Docker permission: direct access to the
Docker Unix socket or, only after explicit administrator approval, a working
`sudo -n /usr/bin/docker info` with
`EXPERIMENT_HUB_DOCKER_SUDO=1`. Docker permission is effectively host-root
authority; do not enable it casually, and never pass images, bind mounts or
commands from remote job data. Set `EXPERIMENT_HUB_ENABLE_EXECUTION=1` and
`EXPERIMENT_HUB_ENABLE_ROCM=1` only for the approved local adapter. A
Docker CPU/memory limit does not impose a GPU VRAM or compute-time cap.
The Hub must still qualify the selected physical GPU and the operator must
explicitly Start each first GPU job. Stop writes the fixed job STOP marker,
waits for the trainer to flush a checkpoint and exit, and does not kill
unfinished PPO updates. Containers remain available for inspect/recovery;
the supervisor does not delete a container whose ownership is ambiguous.

### Halifax mixed native + TinyBrains ROCm execution (opt-in)

To run both projects under the **same enrolled Halifax machine**, select
`EXPERIMENT_HUB_LINUX_BACKEND=mixed`. This is an explicit third execution
mode; the existing `native` and `docker` modes retain their exclusive
ownership checks. Mixed mode requires the trusted Courier native adapter and
the trusted TinyBrains persistent Docker adapter; it can additionally load
`bees_native` training and `bees_native_collect` collection adapters through
the same systemd owner, not independently enrolled agents. The operator-approved example is
`Training/experiment_hub_halifax_mixed_adapter.example.js`.

The mixed agent keeps the original, independent systemd and Docker durable
journals in one private `EXPERIMENT_HUB_LOCAL_ROOT`. It serializes launches
using `mixed-launch.lock` and checks the **combined** unresolved CPU/RAM
commitment against the real host before invoking either owner. Both projects
still use their existing adapter-specific checkpoint/STOP and progress
contracts. Courier remains CPU-only. When ROCm is enabled and a trusted
`bees_native` learner or `bees_native_collect` collector adapter is installed,
those jobs or TinyBrains can use individually leased GPUs: native Bees uses
the existing systemd HIP verifier, while TinyBrains retains Docker's verifier.
Mixed mode serializes GPU qualification and launch with the same lock and
rejects conflicting or unidentifiable GPU ownership across both ledgers.
Native Bees GPU use (learner **or** collector) is **single-device exclusive**;
it cannot cooperatively share a GPU with a Docker job. Independent GPUs can serve both projects
concurrently under their separate physical leases. The Hub's normal machine
reservations remain the cross-job scheduling budget.
Do not run a second local agent or an independent supervisor writer against
the same owner root. A leftover mixed launch lock after a crash is an
ownership-recovery condition, **not** a reason to delete its file blindly.

Before opting in, check both existing owner ledgers and any GPU preflight
journals, ensure that their identities belong to this machine, and reconcile
any unresolved launches. The agent will not resume or start previous jobs on
startup. Existing terminal jobs remain in their respective ledgers; do not
delete or migrate them to change modes. Create only the required new job-artifact directories;
do not modify TinyBrains' imported checkpoint or original run.

For the Halifax layout in the example:

```sh
mkdir -p "$HOME/ai/experiments/experiment-hub/courier-jobs"
chmod 700 "$HOME/ai/experiments/experiment-hub/courier-jobs"
# Configure these variables in the existing private agent environment:
# EXPERIMENT_HUB_LINUX_BACKEND=mixed
# EXPERIMENT_HUB_LOCAL_ADAPTERS=/home/halifax/ai/projects/experiment-hub/Assets/Training/experiment_hub_halifax_mixed_adapter.example.js
# EXPERIMENT_HUB_ENABLE_EXECUTION=1
# EXPERIMENT_HUB_ENABLE_ROCM=1
# EXPERIMENT_HUB_DOCKER_SUDO=1  # ONLY with explicitly approved noninteractive Docker access
```

To enable native Bees alongside those jobs, extend the trusted local adapter
map with `bees_native` and/or `bees_native_collect`, each produced by
`createNativeBeesAdapter` in
`Training/experiment_hub_native_bees_adapter.js`. Supply the actual absolute
Python interpreter, native `policy_rollout` binary, source run configuration,
trainer configuration, starting checkpoint, and existing private artifact
directory; set `learnerSlot` for training against a distinct opponent.
Collectors use `collectOnly: true` and their own artifact directory; a
verified inbox learner additionally uses `bundleInbox`. All configured
files and complete model manifests must pass the adapter's content checks
before the agent can execute or advertise them. The optional frozen
`physics_workers` job parameter sets native physics threads per rollout
process (1–128). It may be combined with `rollout_workers` to run several
model/rollout processes; their product must fit the job's allocated CPU count.
One process with several physics threads shares one batched model worker, while
several processes have separate model workers. A frozen `rollout_worlds`
parameter (1–1024) can replicate the actual authored Level into more
independent, deterministically seeded arenas without changing the scene or
creating another combat implementation. Use it to give a larger CPU lease
enough independent simulation work; `rollout_workers` and
`physics_workers` are separately bounded by the same lease.
Set `physics_workers: "allocation"` to derive the per-process physics
thread count from the allocated CPUs and available independent worlds.
This capacity-based setting is deterministic, **not** a measured optimum
or an adaptive performance tuner. Leaving it unset preserves the source
run's existing physics-worker setting (one by default). The Hub reports actual
`rollout_parallel_physics_rounds`, Stage-serialized arenas and summed worker
CPU-wall time only when a completed native runner supplies those measurements.
Cross-machine native collection transport uses the existing private Hub
agent listener, not a shared network filesystem. On each collector machine,
enable `EXPERIMENT_HUB_SYNC_ARTIFACTS=1` with its installed
`bees_native_collect` adapter. Once the collector's native service finishes,
its approved `rollout-bundle.json`, `events.jsonl`, and `decisions.bin`
captures are mirrored to the Hub with chunk offsets and verified SHA-256.
The learner needs a trusted `bees_native` adapter configured with
`bundleInbox` and frozen `rollout_bundles` entries pinning each completed
collector job ID and **exact receipt SHA-256**. Before native Start, the
learner agent fetches only those declared captures over its authenticated
loopback/SSH-forwarded Hub connection, verifies every file, and atomically
publishes `bundleInbox/<collector-job-id>/`. A missing, changed, or
incomplete artifact prevents the learner from starting; subsequent dispatch
attempts can reuse verified files. No model, checkpoint, executable, or
arbitrary worker file is accepted by this transport.

The current mirrored-file ceiling is **500 MB per capture**, **1,024 files**
(enough for 256 two-file rollout shards plus receipts and diagnostics), and
**5 GB per collector job**, with a 2 GB free-space reserve at the Hub and learner.
Larger captures require a separately qualified transport allocation rather
than silently truncating the learning input. Collector model weights must
still be locally installed and pinned; the transport moves completed
experience only, not arbitrary training binaries.

Existing continuation and collection receipts remain in the same native
systemd owner ledger and combined CPU/RAM budget as Courier. A native
Bees GPU learner or collector uses an exclusive one-GPU lease and the adapter's
`rocrGpuUuids` mapping when a stable per-device ROCr selector is available.
Run the authenticated GPU preflight and confirm that both selected and
excluded render nodes are enforced before starting real training. GPU
allocation does not prove useful model throughput or successful training.

Both systemd CPU/memory enforcement and Docker cgroup-v2 enforcement must
pass local preflight before either backend advertises Start readiness. A
TinyBrains GPU job still requires its **current** bounded, leased PCI/HIP
runtime proof and a real successful container launch. The same qualification
now applies to a native Bees ROCm learner or collector through the systemd
owner; the mixed agent prevents either backend from starting on a PCI device whose
ownership remains unresolved in the other backend. This mode has not
established live Halifax qualification until the operator observes Courier
and TinyBrains concurrently running, with distinct durable owners, correct
combined CPU/RAM limits, a single selected R9700, and completed checkpoint
stops. In Compare, run the 6+6 CPU pair before the 12+12 pair with frozen
per-project configuration, identical GPU binding, and original learning
metrics. Missing PPO metrics or sample counts must be reported as missing,
not inferred from throughput.

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
On a multi-GPU ROCm machine, the **locally installed** TinyBrains adapter may
also supply `rocrGpuUuids`, a map from canonical physical PCI addresses to
operator-verified ROCr GPU UUIDs, for example
`{"0000:03:00.0":"GPU-0123456789abcdef"}` (replace both example values
with the actual matching GPU). Obtain the PCI-to-ROCr identity from trusted
host-local ROCm/AMD-SMI inventory; do not assume the host's GPU zero is
the leased device or derive the UUID from a guessed sysfs serial number.
AMD recommends `ROCR_VISIBLE_DEVICES` for Linux and supports GPU UUIDs
(see https://rocm.docs.amd.com/en/latest/reference/environment-variables/index.html).
For a matching lease, systemd sets `ROCR_VISIBLE_DEVICES` to the pinned
local UUID **for both** `ExecStartPre` and the trainer's process, while
clearing the other accelerator selectors. The strict in-cgroup kernel
device-denial probe and HIP/PyTorch PCI attestation still apply; a stale,
incorrect or unsupported UUID blocks Start. If the local adapter supplies
no matching UUID, existing selector-clearing behavior is retained and
the native launch must independently demonstrate exactly one visible
correct PCI GPU. No enrollment token or saved experiment configuration
may override this operator-owned mapping.

Queue placement prefers fitting hosts advertising the adapter's GPU capability.
Train requires explicit Start and a current device-access **plus runtime**
probe for the same acknowledged exclusive single-GPU lease. Native Start
resolves the PCI mapping again and repeats the access and runtime checks
within the owned training service's cgroup. The trainer then re-verifies
the HIP PCI identity in its own Python process before loading the model. The local journal
also rejects unresolved ownership of the same PCI GPU. CPU-only automation remains the default. A GPU authorization that loses its proof
before delivery holds its claim for operator inspection.

Halifax still needs real qualification of the systemd device policy and
the adapter's ROCm visibility, execution and stop behavior. The planner
accounts for Hub claims. The device policy provides no per-process VRAM
quota, compute share, or exclusivity from unrelated host workloads.

## Windows native Job Object owner (CPU/RAM, opt-in cooperative CUDA)

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

### Opt-in Windows CUDA and existing TinyBrains optimizer checkpoints

A Windows NVIDIA agent reports PCI/VRAM/CUDA UUID through `nvidia-smi`.
Register the observed PCI GPU in **Machines** and use **Bind physical GPUs**
to freeze the logical-to-physical mapping. Rebuild the locally reviewed
Windows owner helper and update both its absolute path and SHA-256 pin.
When CUDA is enabled, its native preflight proves that a synthetic GPU
UUID reaches the suspended Job Object child through
`CUDA_VISIBLE_DEVICES`. Old CPU-only helpers fail this check. This
proves selector propagation, not driver health or CUDA training readiness.
Set `EXPERIMENT_HUB_ENABLE_EXECUTION=1` and
`EXPERIMENT_HUB_ENABLE_CUDA=1` in the private agent environment,
alongside the existing machine ID/token, approved adapter module,
private supervisor root and pinned helper values.

Only a locally approved adapter advertising `gpu_execution:'cuda'`
can receive Windows CUDA jobs. Each job requests one physical GPU,
either exclusively or through a locally opted-in
`gpu_sharing:'cooperative-pytorch'` adapter. Shared jobs specify positive
`gpu_vram_mb` and `gpu_compute_pct` budgets; these are cooperative
scheduler reservations, not OS GPU quotas. Its lease pins the physical
PCI address, CUDA UUID, and exact budgets. The worker
validates that identity at acknowledgement and immediately before Start.
The Windows owner creates the child suspended under kernel CPU/RAM limits
and sets `CUDA_VISIBLE_DEVICES` to the pinned UUID. Before the trainer
starts, TinyBrains' Python child verifies that the selector matches the
pinned UUID, sees exactly one CUDA-capable PyTorch device, and executes a
small CUDA operation. CUDA jobs use the fixed, locally installed
TinyBrains runtime wrapper; the CPU-only inline entry remains unchanged.
This child-side readiness check is not GPU isolation. An exclusive
Hub-owned job refuses all unresolved local owners on the same GPU. Multiple
cooperative shared TinyBrains jobs may run concurrently on that device
only when their *durably recorded* VRAM and compute budgets fit its
physical capacity. TinyBrains applies a per-process PyTorch caching
allocator VRAM ceiling before importing the trainer. This ceiling is
cooperative, not a kernel GPU memory limit; non-PyTorch allocations
and other unrelated programs remain outside it. GPU compute percent
is an admission target, **not a processing-time guarantee**. Hardware
or OS GPU isolation is not established. Do not assume independently
running training processes honor Hub allocations.
Automatic GPU allocation and Start require separate explicit Arm options
and are not restored unattended after a coordinator restart.

For a previously standalone TinyBrains run, **stop the old trainer first**.
Identify and fingerprint its complete optimizer checkpoint (usually
`latest.pt`; verify its actual location). Configure an operator-approved
source on the **machine-local** TinyBrains adapter, for example:

```js
checkpointImports: {
    previous: {
        profile: 'mini_cuda',
        checkpoint: 'B:/actual/verified/checkpoint/latest.pt',
        sha256: 'replace-with-the-real-64-character-lowercase-sha256',
    },
},
```

This example path and digest are placeholders; they must be replaced
with verified local values. In Design, choose the locally approved
checkpoint import in the TinyBrains profile picker. The saved configuration
freezes both `"import_checkpoint":"previous"` and
`"import_checkpoint_sha256":"<the actual digest>"` alongside its approved
`profile`, `profile_sha256`, and `updates`. The local adapter verifies
the source digest, copies it into an exclusive new Hub-owned job directory,
verifies the copy, and invokes TinyBrains' optimizer `--resume`. It never
modifies the original checkpoint. The imported job creates a new Hub
lineage; subsequent queued continuation stages use its Hub checkpoint.
The approved profile must be compatible with the checkpoint, or the
trainer can reject the resume. This is not in-place adoption of an
independently running trainer.

**Windows CUDA Start, checkpoint import, stop and cross-project
coexistence are not yet validated on a live NVIDIA host.** CPU-only
isolated qualification does not establish those behaviors.

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
`--smoke-child`; it never runs production training or user-supplied commands.
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
is no arbitrary command or production training adapter installed.
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
**No production training or production registry is touched.**

These are controlled local tests, not production deployment. A failing
native smoke retains its private journal and prints the location; do
not retry an ambiguous launch or delete those records before inspecting
the owned service. A passing suite does **not** qualify browser/admin
workflows, independent remote workers, GPU isolation or concurrent
production projects.

A Windows-local project adapter must return a reviewed executable,
bounded argv, and explicitly specify \`stop_policy:'terminate-group'\`.
The Hub never accepts a path or shell command from configuration JSON.
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
then resumes the workload. CPU slots translate into a share of total system logical processing
capacity; they are **not dedicated cores**. The Node agent and .NET
owner must agree on the active logical processor count across all
Windows processor groups before native Start is enabled. A process
affinity limit can make the available-processor count smaller than
the system-wide count; this must not inflate Job Object CPU quotas.
A mismatch blocks native execution until the host configuration is
qualified.
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

Windows Stop defaults to terminating the process tree via Job Objects.
A trusted adapter can supply a `checkpoint_stop_file` callback for an
absolute path in its private job directory. The workload watches that
file and exits after saving. The supervisor persists the request,
reconciles marker delivery, and requires a signed successful exit for
automatic release. Job Object cleanup still protects the process tree.

On Windows and Linux, neither natural completion nor a successful native
checkpoint Stop alone proves that the files required to resume are present.
Before advertising `checkpoint_ready_verified` for a finished job or
`checkpoint_stop_verified` for a checkpoint-preserving Stop, the
machine-local bridge requires the trusted adapter's
`checkpoint_ready(context)` to return `true` after checking the saved
artifacts and pinned configuration.
TinyBrains checks `latest.pt` and its profile; Courier checks the
checkpoint summary, approved environment and population archives.
If verification fails, the job remains terminal and its diagnostic is
reported, but any prequeued checkpoint successor stays blocked while
independent successors can still advance after a natural finish. Armed
automation releases verified capacity and disarms for operator review
rather than silently waiting on that blocked checkpoint stage. New
TinyBrains/Courier jobs freeze this proof requirement when queued.
Previously completed jobs lacking the new proof field retain their
existing continuation behavior: the local adapter must verify the
checkpoint and pinned configuration at native Start before creating the
next workload. New jobs cannot use this legacy fallback. These
structural checks do not replace a real trainer resume qualification.
CPU-only jobs mask common CUDA/HIP/ROCm selector variables. Opted-in
single-GPU Windows CUDA and Linux ROCm jobs use platform-specific GPU
selection and checks. The Windows cooperative TinyBrains CUDA path can apply a PyTorch
caching-allocator memory ceiling; it does not constrain other CUDA
allocations, guarantee compute shares or isolate the GPU. Shared Linux
GPU Starts remain blocked.

The .NET owner and browser-controlled CPU-only lifecycle have passed
isolated Windows qualifications, including signed terminal recovery.
Those earlier runs do not qualify the **current** owner/agent version,
which now also requires a system-wide processor-count match. Rebuild the
locally pinned helper and rerun the inert native and browser workflows
before installing production adapters. Production mixed-project CPU
contention, forced-stop effects on real checkpoints, and unattended
evaluation handoff are still unqualified; simulated or isolated passes
do not establish production readiness.

## Project-neutral training measurements

For a non-Bees project, a trusted local adapter may return a
`hub-metrics-v1` observation through its existing `readProgress` method.
The wire contract is strictly bounded: one project-owned experiment ID,
segment ID, ISO source modification time, cumulative nonnegative
`steps_completed`, and 1–192 numeric measurements named by lowercase
slug. Every measurement has an explicit value and unit; supported units
are `scalar`, `ratio`, `count`, `rate`, `seconds`, `milliseconds`,
`mb` and `bytes`. Ratios are 0–1, counts are nonnegative integers,
and rates/times/memory are nonnegative. Payloads are capped at 32 KiB. The adapter, not Hub config JSON, owns
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

The shared Compare view allows an arbitrary number of selected runs, with
multiple independently plotted metrics. The horizontal axis can show recorded
project steps or, where an adapter provides `elapsed_training_seconds` in
seconds, trainer-reported elapsed training time. A missing or incompatible
time measurement is never inferred from heartbeat timestamps. Both the CSV
and JSON comparison exports retain the chosen axis and each point's x value.
CSV keeps selected metric columns as numeric values (including negative
rewards and losses); JSON includes only
selected progress metrics while retaining job and evaluation provenance. **Train** can
freeze an optional per-job list of preferred chart measurements. This is an
archive/visualization preference; it never changes PPO model settings or
manufactures metrics the project's adapter did not record. The native Hub
retains a bounded history of up to 256 chart points for each of 256 jobs;
project-owned full-resolution histories remain authoritative. When explicit
metric preferences are present, the Hub retains operator-selected fields first
and then fills spare chart slots with observed host CPU/GPU resources, without
adding unrelated trainer fields. With no explicit preference, measured host resource
usage and the PPO update counter take priority within the 96-field chart cap;
remaining slots retain trainer metrics. Source reports may contain up to 256
metrics (within the unchanged 32-KiB source-progress envelope), while charts
retain at most 96 per observation. Select high-index collector metrics or any of the 32 supported
indexed physical-GPU measurements in Train before starting the job to
prioritize their chart retention; all received source measurements remain in the raw diagnostic
archive regardless of chart selection. Full project telemetry is not filtered
by these display choices. Host GPU utilization/VRAM/power measurements describe
the pinned physical device, not isolated per-job GPU consumption. Passive
optional temperature readings are reported in °C from identified AMD
edge/junction hwmon labels or NVIDIA's GPU-core sensor. Passive power
measurements in W come from AMD hwmon (microwatt readings) or NVIDIA
nvidia-smi. Unknown, unavailable or unreadable sensors remain absent.
Power draw is not per-job energy consumption, and a high temperature
alone does not establish throttling. These measurements never alter GPU admission,
resource leases, job execution or training workloads. The native
Hub also appends each distinct **received** validated source update to private
`hub-observations-v1/<job-id>.jsonl` in its data directory, retaining source
trainer metrics and observed resource metrics independently of chart limits.
When a valid native owner observation has no new trainer update, the Hub
also appends host-only CPU, RAM and physical GPU measurements to
`hub-observations-v1/<job-id>-resources.jsonl`. These records have
`observation_kind:host_only`, no trainer step count and no fabricated
trainer source timestamp. `trainer_source_status` distinguishes an adapter
returning no progress (`absent`), a source read or validation failure
(`read_error`), or a source version the Hub already recorded
(`not_advanced`). Host-only resource samples are appended only for reported
running or stop-requested processes, not terminal inspections; neither
observation alone proves the trainer has stalled. Complete Export includes both files and marks
host-only rows separately in the long-form observation CSV. The host-only
series may continue while the trainer is stalled, but still cannot prove
GPU work or account for unobserved updates.
These files are diagnostic observations, not authoritative trainer histories;
missed worker updates cannot be reconstructed. A failed archive write does not
stop training and appears as an archive warning in Compare. This archive is
append-only and should be included when managing the Hub's disk capacity.

**Export complete job diagnostics** in Compare packages the selected local
job histories and run-scoped text/JSON logs into independent ZIP archives of
at most 500,000,000 bytes each. Choose several jobs and up to 256
metric names; optionally include raw replays. This export selection is
independent of the 96-field per-observation chart cache. No checkpoint, model, private key or credential files are
intentionally included. Every part has a segment manifest for reconstructing
large logs; each entry records the SHA-256 of the uncompressed segment bytes.
These hashes let an operator verify and reassemble files from split ZIP parts
without confusing a segment's checksum with a hash of a whole training run.
The overall manifest reports jobs without local artifacts and
individual unavailable or excluded log sources by job ID and source label;
no private host paths are included in that report. A job with only the Hub's
received observation archive still appears in `missing_artifact_job_ids`:
those observations do not prove that its original trainer histories were
captured.
Select **Export every recorded metric** to include all locally retained
project-neutral source and host-resource measurements in the derived CSVs,
including fields missing from the 96-metric chart cache. This does not require
a script or guessing metric names. The existing selected-metrics mode remains
available; complete exports may contain much more data and still split into
independently downloadable ZIP parts of at most 500 MB. Remote-only trainer artifacts that have not been collected are reported
missing. An optional, authenticated artifact mirror can collect selected
finished-job text histories without a shared filesystem mount.

Set `EXPERIMENT_HUB_SYNC_ARTIFACTS=1` on a machine already configured with
`EXPERIMENT_HUB_ENABLE_EXECUTION=1` and an installed Courier, persistent
TinyBrains or native Bees collector adapter. The adapter—not the Hub or a saved configuration—selects
the job's private artifact directory. After the local native owner reaches a
terminal state, the agent sends approved log snapshots to the existing
loopback/SSH-forwarded machine endpoint on port 7149. Heartbeats and native
Stop/Inspect commands are independent of this lower-priority transfer.
Each HTTP chunk is limited to 1 MiB, a single file snapshot to 500 MB,
and the receiving Hub to 256 files / 5 GB per ordinary job, or
1,024 files / 5 GB for a native Bees collector. The Hub also
requires at least 2 GB of verified free filesystem space after each chunk
so optional diagnostics cannot consume the checkpoint/journal disk reserve.
Each cycle transfers up to 32 MiB for diagnostics or 128 MiB for native
collector captures, resumes from a verified staging offset, and
publishes a file atomically only after its complete SHA-256 matches.
The coordinator stores private completed snapshots in
`hub-remote-artifacts/<job-id>/`; unfinished pieces are not export inputs.
Complete Export includes these original trainer-log copies as
`agent-artifacts` alongside Hub observation histories, or under the
managed collector's linked-log source if collected separately.

Automatic mirroring is opt-in because **text logs can contain secrets even
when their filenames are innocuous**. It excludes credential-related paths,
model/checkpoint files, symlinks and replays, but does not inspect and redact
arbitrary log *contents*. Use only with trusted project adapters and trusted
training outputs. It copies selected supported files, not model weights or
a guaranteed full-resolution snapshot of every original run: files too
large, unsupported names, unavailable owners, or machine failures remain
outside the mirror. The `--once` agent does not run background log mirroring.
Uncollected trainer artifacts remain explicitly unavailable in the export
manifest rather than inferred from heartbeat summaries.
When no project artifact root is locally mounted, the coordinator can still
export its own recorded Hub observations and metadata, marking each selected
job's project-owned artifacts as unavailable. An existing but unsafe or
redirected configured root is never silently substituted.
When metrics are selected (or all recorded metrics are requested), the archive also includes
`hub-selected-metric-samples.csv`: a metric-filtered, long-form CSV from the
bounded Hub comparison history, with job IDs, timestamps, project steps,
segments, values, and units. This CSV is **not** the full-resolution training
history. Native projects additionally include
`hub-selected-observation-metrics.csv` containing every selected measurement
in each locally retained Hub observation, before chart downsampling, with an
origin column distinguishing trainer and resource measurements. The complete
append-only Hub observations JSONL and project-owned histories are included as
separate raw sources. These are not atomic cross-machine snapshots, and no
received-observation CSV can supply trainer updates the Hub never observed.
Running jobs can append to their logs during export. Each file is captured
through its size when opened, not as one atomic multi-machine snapshot;
`export-manifest.json` lists files that grew while being copied and the
captured byte boundary. The Hub also warns about such files when parts are
ready. Rewritten or redirected files fail export rather than silently mixing
unverified bytes. Download the numbered parts before the private export
session expires. The loopback CLI also supports the same export sessions
without loading ZIP parts into Node.js memory:

```sh
node Training/experiment_hub_cli.js post exports --data request.json
node Training/experiment_hub_cli.js get exports/<session-id>
node Training/experiment_hub_cli.js get exports/<session-id>/part/1 --output part-0001.zip
```

Use the returned session ID and repeat the last command for each numbered
part. Output files are created exclusively: an existing download is never
overwritten, and incomplete transfers are rejected. Export sessions and
artifact access remain governed by the local coordinator, not a new remote
transfer endpoint. Selected chart metrics never truncate the underlying source
histories; files that change during export may require a later capture.
Native project exports also include `hub-resource-claims.json`, which pins
each selected job's persisted Hub CPU/RAM/GPU lease and physical GPU bindings.
For managed distributed TinyBrains learners this file includes each linked
collector's machine assignment, pinned thread count and matching resource
lease, even if the collector's raw logs are unavailable.
The same learner export also includes coordinator-received
`tinybrains_rollout` collector observations under source labels containing
each full collector UUID. The long-form observation CSV identifies these
rows by their **collector job ID and project**, rather than attributing them
to the learner. A missing collector observation is listed in the manifest.
When an operator provides an approved `tinybrains_rollout` machine mount
with per-collector directories named by the collector job UUID, the learner
export includes those child-owned text/JSON log files as distinct sources
as well. A missing collector mount appears in `missing_sources`; the reason
`mount_not_configured` explicitly marks a collector with no approved mount
mapping, rather than silently treating its original logs as complete. Child
files never count as evidence that the learner's own trainer histories
were obtained. This is an operator-provided read-only mount, **not**
automatic worker-to-coordinator artifact transfer. Neither Hub observation
file proves that the worker's original trainer log
was obtained or that the allocated cores performed useful work. Missing leases
remain null. This is historical allocation evidence, **not** a measurement
of utilization or proof that a GPU completed useful training work.
The native coordinator reads TinyBrains jobs under the Hub data root's
`tinybrains-jobs/` directory. For another project (including Courier),
install a private mode-0600 `job-export-roots.json` in the same Hub data
root, mapping project IDs to approved, absolute, owner-owned artifact
directories; these are never specified in HTTP requests. For jobs on other
machines, the same file can additionally map **operator-mounted, read-only**
worker artifact directories to the enrolled machine IDs and project IDs:

```json
{
  "@machine_roots": {
    "exeter": {"tinybrains": "/mnt/exeter/tinybrains",
               "tinybrains_rollout": "/mnt/exeter/rollout"},
    "warwick": {"courier": "/mnt/warwick/courier"}
  }
}
```

The coordinator uses the mounted `<root>/<job-uuid>/` only for jobs
actually assigned to that machine and project, preserving the existing
approved file-extension, symlink and credential exclusions. A missing mount
is reported in the export manifest; an existing redirected/unowned mount
is rejected. Project-owned histories remain on their training machines
unless the operator independently provides a secure mounted directory
(e.g., read-only SSHFS/SMB with appropriate owner mapping). This is
**not** automatic file transfer and does not grant the Hub SSH access or
allow arbitrary commands. The legacy Bees coordinator continues to use
its existing operator-owned metrics and run-scoped log paths.
Inspect archives before sharing them, as ordinary application logs can
contain sensitive content.

Generic metric names do not grant stopping, model promotion or benchmark
authority. A concrete project adapter must be installed and qualified to
produce real measurements. Host GPU utilization and VRAM values are physical
device observations, **not per-job GPU accounting**. For native jobs,
`job_cpu_lease_utilization_pct` reports sampled process CPU-core equivalents
as a percentage of the job's fixed CPU budget, using continuous authenticated
CPU-time observations. Linux systemd and Windows native owners also supply
optional cumulative CPU seconds for long-running multicore jobs once exact
integer nanoseconds exceed JavaScript's safe range. This is diagnostic
precision, not a change in enforcement or terminal receipt authentication.
It is not a measure of marginal throughput, and short sampling intervals
may show values above 100%. Where a trusted native supervisor provides
per-job task and memory counters, `job_process_tasks` reports the observed
task count and `job_memory_lease_utilization_pct` compares sampled job
memory against its frozen RAM allocation. Missing counters remain absent.

### Selecting and promoting independently trained models

In **Compare**, choose 2–16 independently trained Courier or TinyBrains
jobs and a saved next-stage configuration. Completed candidates can be
ranked immediately; active or prequeued candidates can instead have a
selection scheduled for the existing native automation scheduler.
The API supports 2–16 candidates:
`POST /api/hub/candidates/rank` (preview),
`POST /api/hub/candidates/promote` (queue winner immediately),
`POST /api/hub/candidates/schedule` (defer until all candidates complete),
and `POST /api/hub/candidates/cancel` (cancel an unresolved selection).
The deferred decision, winning checkpoint identity and queued continuation
are persisted; selection does not directly launch a workload.

Ranking uses the equal-weight mean evaluation success rate across
**identical frozen suite definitions, minimum thresholds and trial counts**,
with stable job-ID tie-breaking. Training metrics do not determine winners.
Compare now offers a read-only ranking preview before committing or reviewing
a model promotion, including every candidate's per-suite successes/trials.
The displayed percentages are screening estimates, not proof of a significant
quality difference; previewing does not reserve resources or queue a job.
Every candidate needs an independent artifact lineage, a successfully
finished or evaluation-stopped native service, verified resumable
checkpoint, released capacity and a complete matching benchmark receipt.
The exact policy bytes used for evaluation must match the model artifact
that its native adapter would resume: Courier pins `lineage-0.zip` and
TinyBrains pins `latest.pt`. Single-suite and multi-suite evaluations retain
up to eight recent checkpoint cohorts so candidate selection can locate
verified receipts for the actual final model, not just the last observed
intermediate evaluation. Both single- and multi-suite streams freeze the
first accepted suite fingerprint and reject a changed or missing digest
once pinned, without granting authority to legacy unpinned receipts. The promoted continuation pins that SHA-256
again at native launch. A result from an earlier snapshot cannot
silently substitute for the final model, even if the latest checkpoint
is otherwise resumable. If no matching final-policy evaluation exists,
the candidate is ineligible until that evidence exists; the Hub never
assumes a final training checkpoint is the evaluated checkpoint.
The scheduled evaluator also targets verified final checkpoints with
released capacity and missing receipts. It uses the recorded model hash
and retains the result for ranking without changing the job's terminal
state. A deferred selection allows up to 24 hours after the last
candidate releases capacity for these receipts to arrive. Post-terminal
evaluator worklists and native Inspect orders are bounded to 24 hours after
verified release; a still-waiting candidate selection extends that window
to 24 hours after its last contender releases capacity. Already retained
receipts remain available after expiration, but historical missing receipts
cannot consume worker inspection slots indefinitely. An expired, incomplete
selection disarms for review instead of assuming a benchmark result.
An isolated Windows Courier run evaluated two independently trained final
checkpoints against two suites each. Automation promoted the tie-break
winner, resumed its exact checkpoint on the original worker, and
released the continuation's claim after completion.
The same workflow also passed with two separately enrolled workers on
one Windows host, each with its own supervisor, artifacts and evaluator.
Selection scheduling refuses conflicting suite fingerprints already known
at plan creation. Physical multi-host execution remains unqualified.
All prospective candidates must use the same approved continuation
environment/profile fingerprint. The native continuation remains on
its original machine with the original artifact lineage.

A deferred selection resolves atomically when all candidates have
released verified capacity. Missing evidence, failed candidates, changed
definitions or incompatible checkpoint lineages record a review-required
decision and disarm automatic execution instead of guessing a winner.
Pending/review-required selections can be cancelled without stopping
training jobs. A review-required plan can also be re-ranked against fresh
verified evidence and explicitly completed with **Review & queue verified
winner**. The existing promotion endpoint requires `confirm_review:true`
when resuming such a plan; it rechecks the original frozen candidate IDs,
continuation configuration revision and resources before one atomic commit.
Unresolved plans reserve their candidate cohort against duplicate selection
or unrelated immediate promotion. No manual review bypasses benchmark or
checkpoint identity checks, and review never re-arms automation.
Editing the selected next-stage configuration during the competition
invalidates its frozen revision and requires cancelling/replacing the plan.

### Independent evaluation stops for non-Bees jobs

A draft can set an optional `evaluation_stop` alongside its adapter parameters.
Set its optional `suite_sha256` in Design to require a specific suite
definition. Courier and TinyBrains receipts include the hash of the
parsed suite JSON serialized with Python `json.dumps(suite, sort_keys=True)`.
Run this from the Hub's Training directory to compute it before queuing:

```text
python -B -c "import json,sys; from experiment_hub_eval_io import suite_digest; print(suite_digest(json.load(open(sys.argv[1], encoding='utf-8'))))" path/to/suite.json
```

Older receipts can still satisfy unpinned policies through the adapter
contract, but the scheduled evaluator does not reuse an existing receipt
unless its fingerprint matches the current local suite definition.
Compare identifies missing fingerprints and differing definitions,
even when the IDs match.
Design accepts a suite ID, minimum trial count and minimum success rate, for
example `{"suite_id":"heldout","min_trials":100,"min_success_rate":0.8}`.
For a multi-benchmark stopping gate, fill **All required benchmark suites**
with a JSON array instead of the single-suite fields. The coordinator freezes
`{"all_of":[{"suite_id":"short","min_trials":100,"min_success_rate":0.8},
{"suite_id":"long","min_trials":100,"min_success_rate":0.7}]}`
with **2–8 distinct suite IDs** and optional `suite_sha256` per rule.
A provided hash pins the suite from the start. Without an explicit pin,
the Hub freezes each suite's fingerprint from its first authenticated
evaluation receipt; later changes under that suite ID block progression
and require a new experiment. Multi-suite receipts must contain their
suite fingerprints; old single-suite behavior remains compatible.
The worker must advertise all required suites. For an all-of gate, run
**one coordinated scheduled evaluator with a repeated `--suite` argument**
for every required frozen suite; do not separately schedule single-suite
pollers for that dispatch. The batch evaluator pins one immutable checkpoint
before running the suites, publishes matching receipts using a durable
pending-batch record, and resumes incomplete publication after interruption.
A single-suite poller skips all-of jobs. The coordinator records at most
eight checkpoint cohorts and requests Stop **only when every threshold
passes on the exact same checkpoint SHA-256**. A successful short-range
evaluation from one checkpoint cannot be combined with long-range evaluation
from another. Schedule the batch frequently enough to see retained
checkpoint snapshots. Previously scheduled single-suite experiments
retain their original format and behavior. This is an early-stop policy:
a natural trainer finish still completes an independent sequence under
its existing rules, even if no evaluation stop target was reached.

Both scheduled pollers publish a small `.workspace.json` ownership record in
their private `--scratch` directory before exporting models or writing replays.
The next invocation takes the same kernel lock and reclaims only that recorded
workspace, even when no job remains eligible. Receipts and checkpoint cursors
are retained. The ownership record remains outside the workspace until cleanup
finishes, so interrupted cleanup can also be recovered. Corrupt ownership or
a redirected workspace stops evaluation for inspection; unrecorded scratch
directories from older versions are left for manual review. The surrounding
scheduler must contain the evaluator's complete process tree when stopping it.

Windows execution checks covered abrupt single/batch worker exits, live-lock
exclusion, redirected paths, junction target preservation and readonly model
cleanup. Actual TinyBrains evaluation then completed 12 single-suite and 24
batch held-out games with matching checkpoint receipts and no retained owned
workspace. This qualifies evaluation mechanics and cleanup, not playing strength,
production resource coexistence or Linux runtime behavior.

Queueing freezes the rule. Only hosts advertising the requested suite(s)
for that project can claim or start the job. Agents without suite
advertisements remain eligible for jobs without an evaluation stop.

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
The job retains capacity until terminal handling. An all-of Stop
preserves the qualifying receipt for each suite and the common checkpoint
hash. After a verified native Stop and capacity release, a qualifying evaluation
can advance an independent
`after_job_id` sequence, with fresh allocation and Start authorization for
its successor. A queued Courier/TinyBrains checkpoint continuation also
advances only when the local supervisor confirms checkpoint-preserving Stop
and the adapter verifies a resumable artifact. It resumes the last saved
checkpoint, which can postdate the evaluated policy snapshot. Manual Stops,
missing checkpoint proof and failures do not advance queued stages.
Training progress has no stopping authority.

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
highest reported CPU time (seconds where available, retaining older
nanosecond-only summaries), the highest sampled RAM, and the observed
time window. Monitor, the JSON comparison export and `get state` expose
the retained summary after restart. Missing counters remain null. The
summary covers the observations still in memory when the job ends, plus
any terminal counters. A coordinator restart before completion can
shorten that window. Sampled RAM does not measure the OS lifetime peak.
Windows owners also report their OS Job Object peak-memory counter
separately. Version-2 signed terminal receipts carry that counter and final
CPU time when its nanosecond value is representable; sufficiently long
multicore jobs rely on previously received live CPU-second observations. Legacy signed
receipts remain valid and leave those terminal counters unavailable.
Deploy the updated supervisor before updating the pinned Windows owner.

## TinyBrains Ants-MAPOCA adapter

`Training/experiment_hub_tinybrains_adapter.js` provides
`createTinyBrainsAdapter` for native Windows CPU/CUDA and Linux CPU/ROCm
training jobs. Prepare a
TinyBrains Ants-MAPOCA checkout with its native engine and cartridge,
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
TinyBrains YAML configuration with `runtime.device` set to `cpu`, or with
Windows CUDA or Linux ROCm opt-in, `cuda`. The
adapter runs TinyBrains' own validator before advertising profiles. An
invalid profile prevents adapter startup and names the rejected profile.
Compute
the SHA-256 of the UTF-8 bytes of `JSON.stringify(profile, null, 2) + '\n'`
after parsing that JSON. In Design, choose an approved profile from a
connected worker, enter the update count, and select **Use profile
and update count**. The picker fills the draft without saving or starting it.
For API callers, set `profile`, `profile_sha256`, and `updates` (a positive
integer up to 10,000,000). CPU profiles need a CPU/RAM claim without a GPU;
CUDA/ROCm profiles require one GPU plus CPU/RAM. Linux ROCm still
requires an exclusive, kernel-filtered GPU and its access probe.
Windows TinyBrains may instead request a `shared` GPU with explicit
`gpu_vram_mb` and `gpu_compute_pct` when the approved adapter
advertises cooperative PyTorch sharing. The worker advertises
profile device modes and sharing capability alongside immutable digests.
The worker advertises approved profile names and digests. The Hub matches
them before claiming capacity and checks again before dispatch. Install
the updated worker before enabling placement on the updated coordinator.
The adapter independently verifies the digest at launch.

On Linux, set `enginePath` to the absolute path of a locally installed
native TinyBrains 0.5.0 executable. The adapter probes its version before
advertising profiles. Use the same engine with `--engine-path` for both
evaluation commands. Newly launched Linux TinyBrains jobs use the same
checkpoint-aware `STOP` marker as Windows instead of interrupting the
trainer at an arbitrary update. The native supervisor durably records the
Stop request, retries marker delivery on the exact verified running service
after agent restart, and waits for a successful systemd terminal receipt
and a verified resumable checkpoint before marking a checkpoint Stop.
A pending marker request holds its resource claim until native completion
is confirmed. Previously launched Linux jobs without the stored marker
retain the original systemd SIGINT Stop behavior; no old job is silently
adopted or relaunched.
Linux also accepts approved `runtime.device: "cuda"` profiles when the
installed Python environment uses ROCm-enabled PyTorch. Configure
`EXPERIMENT_HUB_ENABLE_ROCM=1` and use the existing Linux exclusive
single-GPU device-filter and access-denial qualification path. At native
Start the trusted TinyBrains adapter's fixed ROCm pre-start verifier
checks that the selected PCI GPU remains AMD, PyTorch identifies its HIP
runtime, exactly one GPU is visible to the service, and a tiny GPU
operation succeeds. It also queries HIP's `hipDeviceGetPCIBusId` for
runtime device ordinal zero and requires the returned PCI address to match
the leased GPU. Merely seeing the expected DRM device in sysfs and one
PyTorch GPU is not sufficient: an absent HIP identity API, mismatched PCI
address or ambiguous enumeration blocks native Start. The actual TinyBrains
training Python process repeats the same HIP-device identity and tiny compute
check before importing the training CLI, using a frozen
`--hub-rocm-pci` argument generated by its installed adapter. Thus the
independent pre-start service check cannot, by itself, attest the runtime
selected by the separately launched trainer. The approved Python
environment must expose the ROCm HIP runtime library (`libamdhip64.so`).
The Linux device-filter qualification requires a second physical
AMD GPU to prove access denial and remains mandatory; the profile does
not bypass it. This is not an enforced GPU compute/VRAM partition and
has not been exercised on Halifax.
Native Linux TinyBrains training, evaluation and Stop still need runtime
qualification. Windows can keep using the bundled executable or select
an explicit `enginePath`.

Each launch uses a fresh job directory containing its exact profile,
training history, checkpoints and `trainer.stderr.txt` for startup and
runtime errors. A one-CPU, 3 GiB claim supported natural completion and
managed Stop with the isolated Windows smoke profile. A 1 GiB claim
failed during PyTorch initialization on that worker. Size production
claims for the selected profile and environment. Compare uses completed trainer updates
as project steps and shows update wall time and engine steps per second.
New Windows TinyBrains launches use the trainer's `STOP` marker to exit
at an update boundary. The Hub waits for the signed successful exit and
released capacity before allowing an explicit continuation. Older
force-stopped jobs have no checkpoint proof. In Train, choose an eligible
predecessor and a draft with the same
profile digest. The continuation runs on the original worker and its
update count is additional. It reads the predecessor's checkpoint and
writes into its own job directory, leaving earlier checkpoints intact.
An isolated Windows run stopped at update 12, reopened the Hub and
native supervisor, and completed update 13 on the original worker.
Train can also queue 2–16 TinyBrains continuation stages atomically.
A successor stays unclaimed until its predecessor finishes with a verified
resumable checkpoint and releases capacity. With native queue automation armed for the original worker,
the Hub can allocate, start, and release each stage without a separate
operator action. In an isolated Windows run, two one-update stages
finished at cumulative updates 1 and 2. Peak outstanding claims was
one and both signed exits survived a coordinator restart with no
capacity held. Manual Stops hold prequeued stages for review. A verified
evaluation-triggered Stop advances the next checkpoint stage only when
the native supervisor certifies checkpoint-preserving shutdown and
capacity is released. The continuation loads the last saved checkpoint,
which can be later than the checkpoint that met the evaluation target.
Native Windows CUDA profiles require explicit local GPU opt-in and
one PCI/UUID-pinned device. The approved TinyBrains adapter also permits
cooperative shared GPU Starts using a PyTorch allocator memory ceiling.
GPU compute shares, access by other programs and VRAM outside the
PyTorch allocator are not isolated or enforced.

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
`--artifact-root`, `--suite`, `--scratch`, and `--receipt-root`.
For a multi-suite gate, pass `--suite short.json --suite long.json`
in one invocation instead of launching independent pollers. The poller
queries the local coordinator (port 7148 by default when running beside
the coordinator). On a remote worker, use `--port 17149` and provide
`EXPERIMENT_HUB_AGENT_TOKEN` from the private agent environment through
the scheduled evaluator's `--env-file`; only the restricted 7149 agent
port should be forwarded. It selects a completed numbered checkpoint for
an eligible running dispatch, and evaluates at most
one checkpoint batch per invocation. Set the profile's `training.checkpoint_every` so a
numbered checkpoint appears while the job is still running. The poller
leaves the changing `latest.pt` alone. Use persistent scratch to retain
checkpoint watermarks across temporary worker disconnects (up to
1,000 dispatches), and retry publication independently of coordinator availability
once a completed evaluation is recorded. The trusted adapter and Hub
accept the receipt only for the exact running dispatch and suite.
A separate host needs a trusted loopback forward and read access to the
training artifacts.

For reproducible comparisons, pin the evaluation runtime as well as the
scenario. Run the following with the evaluator's Python environment and its
same `TINYBRAINS_HOME` / `TINYBRAINS_REGISTRY` settings, after the cartridge
has been installed locally:

```powershell
python -B Training/experiment_hub_evaluation_runtime.py --project tinybrains --engine-path $engine
```

Save the returned manifest alongside the suite and add its `runtime_sha256`
to the suite JSON. The fingerprint covers the native CLI, actual cartridge
and map bytes, Nano weights/manifest, project and baseline Python sources,
export observations, evaluator code, Python/platform and dependency versions.
It excludes absolute installation paths. The evaluator verifies this pin
before loading the candidate and again after scoring; a mismatch publishes
no receipt and leaves the checkpoint eligible for retry. The runtime pin
is part of the suite fingerprint already frozen by the Hub, so changing
it requires a new suite ID/experiment and cannot silently mix comparisons.
Inspect changed manifest entries before approving a new pin. This does not
isolate an evaluator from concurrent changes or replace an immutable deployment.

Existing suites without `runtime_sha256` retain their previous behavior and
hashes; their suite fingerprints identify the scenario only. They do not
prove identical evaluator code, opponents or dependencies. Runtime pins
include the OS and dependency build versions, so Windows/Linux environments
need separately approved suites. Native Linux execution remains unqualified.

Windows qualification exercised runtime-pinned batches through the native
CPU/RAM-contained evaluator: 24 actual TinyBrains games and 16 Courier
scenarios. Private source drift was refused without consuming the checkpoint;
restoring the source allowed retry. Drift after another eight actual Courier
scenarios discarded the result. Altered private Nano weights were refused,
and identical bytes in another directory retained their fingerprint. The
receipt reader and isolated ranking fixtures rejected different suite hashes.
These checks qualify pin enforcement and integration, not policy strength,
long-duration operation or the deployed service account.

In an isolated Windows run, the scheduled evaluator played both seats
against Nano from a numbered TinyBrains checkpoint. The Hub retained
the pinned suite receipt and kept training when the result was below
the stop threshold. A subsequent manual Stop produced a signed terminal
receipt, and the released claim survived a registry reopen. Qualifying
TinyBrains evaluation-triggered Stop and remote evaluation remain open.

### Cross-project fixed CPU concurrency and scaling evidence

The reusable `fixedCpuParallelism` rule selects worker count from a
machine-local CPU lease exactly once, bounded by the adapter's approved
maximum. It is currently used by the persistent TinyBrains and Courier
adapters, but does **not** distribute a single job across machines by itself.
Courier can opt into `cpuParallelism:'allocation'` in its trusted adapter;
a configuration with `workers` omitted then uses up to 64 leased CPU cores.
Explicit `workers` continues to use the saved count. The default is
`'profile'` to preserve deployed configurations. Other project adapters
must implement their own safe consumption of the fixed budget.

Compare includes **Observed resource scaling** for selected runs with a
recorded throughput metric (unit `rate`). It summarizes bounded chart
samples from the latest continuous segment and shows throughput per allocated
CPU core. Marginal figures are shown only for completed jobs with at least
five samples, matching frozen configurations, and exactly one monotonically
increased resource dimension. For distributed TinyBrains learners, the
allocator sums the learner and its separately leased remote collectors;
the comparison requires the same set of machines, the same learner/collector
roles, and unchanged GPU modes. Comparisons also require a persisted Hub-issued resource lease for every
job (including CPU-only jobs), with matching frozen CPU, RAM, GPU count,
GPU mode and per-device shared compute/VRAM claims. Matched run groups
must keep each host's RAM, shared-GPU budget and minimum GPU-VRAM
constraint fixed; otherwise changing those alongside CPU/GPU counts
would confound marginal throughput. For GPU jobs the comparison also
requires physical PCI GPU identities:
CPU scaling must use the same devices, while GPU scaling may add devices
but must retain existing ones. Missing or mismatched historical claims
disable marginal estimates rather than guessing allocation equivalence. It refuses
incomplete group records and CPU/GPU reallocations that reduce resources
on one host while increasing another. Distributed rows also show the
total frozen remote collector threads versus CPUs reserved for those
collectors. A thread count smaller than the CPU lease is an observable
allocation/concurrency mismatch, not proof that remaining CPUs are unused
or that adding threads would improve throughput. These remain *reserved*
resources: they do not prove useful utilization or incremental speed.
All such comparisons remain observational, not independent evidence of
causality, energy efficiency, or full-resolution training performance.
Export the complete job histories for definitive throughput investigation.
Compare also has a project-neutral **Recorded time metrics** panel showing
the top observed medians, minima, maxima and sample counts from each selected
run's latest chart segment. Valid `seconds` and `milliseconds` source values
are both displayed as seconds; the original stored units are not changed.
This includes recorded optimizer/rollout phase timings when an adapter
supplies them. Known cumulative elapsed-training and active-training clocks
are excluded from the stage-timing ranking, but remain chartable and
exportable. When a trusted adapter publishes `active_training_time` in
seconds (as Courier checkpoints do), Compare offers a separate active-time
horizontal axis. Active time may exclude pauses, and it is never inferred
from the Hub heartbeat or interchanged with elapsed training time.
A duration unit alone cannot distinguish other cumulative clocks from
individual stage durations, so the panel does not add overlapping
stages or infer a causal bottleneck.

## Courier Ants (Basic Training)

Register the project as `courier`. Add a `courier` entry to the worker's
trusted adapter module using `createCourierAdapter` from
`experiment_hub_courier_adapter.js`. Supply absolute `projectRoot`,
`pythonPath`, `artifactRoot`, and `configPath`. Use the installed Basic
Training Python environment and a dedicated artifact directory outside
the checkout. Linux paths must meet the supervisor's path restrictions.

Set the draft's `config_sha256` to the approved environment file's SHA-256,
and set `seed`, `workers`, `max_rounds`, and `checkpoint_rounds`. A
`max_rounds` of zero enables continuous training. The adapter copies the
approved environment into a fresh per-job directory, and the Hub reports
durable checkpoint rounds, generation, and active training time. Set a
CPU/RAM claim appropriate for the worker count and checkpoint workload.
The trainer's own loopback dashboard uses an OS-assigned port.

New Courier jobs on **Windows and Linux** use the local
`hub-stop.request` checkpoint Stop marker through the existing trusted
Courier wrapper. The wrapper dispatches the trainer's SIGINT handler after
it is installed, letting Courier save its final checkpoint and exit normally.
The Hub requires the exact native terminal evidence (a signed Windows
Job Object receipt or a Linux systemd terminal receipt) and a verified
resumable checkpoint before releasing capacity for continuation. A pending
request holds capacity while training is active or the final state remains
uncertain. Previously launched Linux services without a persisted marker
keep their systemd interrupt Stop; older Windows launches without a marker
retain Job Object termination.
For checkpoint continuation, select a naturally finished predecessor
with released capacity, or queue a checkpoint pipeline in Train. You
can also select a Courier job that saved on Stop and released capacity.
Linux requires a matching systemd terminal receipt with a successful
exit code of zero. Manual Stops hold prequeued checkpoint stages for
review. A verified evaluation-triggered checkpoint Stop advances the
next prequeued stage after capacity release; it resumes the final saved
checkpoint, which can postdate the evaluated policy snapshot. Failed or
unverified Stops never advance checkpoint continuations.
Cancel any pending successor before queuing an explicit continuation. Keep
the same environment digest. Each stage runs on the original worker
and copies the completed population into its own directory before
resuming. A positive round limit is additional for continuations.

For evaluation-driven stopping, set `evaluationRoot` to a separate trusted
receipt directory and `evaluationSuites` to the approved suite IDs. Use
`experiment_hub_courier_evaluate.py --help` with the Basic Training Python
environment. Run it on a trusted, immutable copy of the policy from the
same dispatch and pass that policy's SHA-256 and the job's
`hub-environment.json`. The suite JSON contains `suite_id`, `config_sha256`,
`seed`, `trials`, and `horizon`. Use a dedicated seed and a new suite ID when
changing the scenario definition. The success count is the number of
trials alive at the fixed horizon. Configure the matching suite and
thresholds in Design.

Courier suites also accept `runtime_sha256`. Obtain it using the Basic
Training Python environment:

```powershell
python -B Training/experiment_hub_evaluation_runtime.py --project courier --project-root $project
```

Save the returned manifest and copy its fingerprint into the suite before
pinning the suite SHA-256 in Design. It identifies `courier_env.py`,
`courier_safe_ppo.py`, evaluator code, Python/platform and dependency versions.
Both explicit and scheduled evaluations verify it before and after scoring.
The separate `config_sha256` continues to pin the environment JSON.

For scheduled evaluation, run `experiment_hub_courier_eval_poll.py` with
the same Python environment on a separately budgeted CPU schedule. For an
all-of gate pass `--suite short.json --suite long.json` (one argument
per required suite) in a **single** scheduled invocation. For an ordinary
single-suite gate, pass one `--suite` as before. Its
`--snapshot-root` must match the adapter's optional `snapshotRoot` and be
separate from source, live artifacts and receipts. For jobs with an
evaluation stop policy, the Courier launch bridge exports lineage 0 after
each successful durable checkpoint. It publishes an immutable copy at
`<snapshot-root>/<dispatch-id>/round-<n>.zip` and keeps at most two published
policies per dispatch. The poller picks the latest unseen round from a
live eligible dispatch and persists its cursor and pending receipt in
`--scratch`. Retain or remove completed dispatch directories under the
worker's artifact retention policy.

## Atomic multi-machine resource bundles (all native projects)

For independent native jobs across TinyBrains, Courier and any newly
installed project adapter, **Train → Reserve a multi-machine resource
bundle** selects 2–16 saved configurations, explicit connected machines,
and immutable CPU/RAM/GPU resource budgets. The operator confirms one
atomic `POST /api/hub/resource-bundles` request:
`{"confirm_queue":true,"members":[{"configuration_id":"<saved-uuid>","expected_revision":1,"machine_id":"<registered-machine>","resources":{"cpus":2,"ram_mb":2048,"gpu_count":0}},...]}`.
Every member must have an approved local project adapter and native CPU/RAM
preflight; GPUs additionally need matching physical device registration
and the locally approved GPU mode. All members and claims are committed in
one durable registry update, or none are committed. Duplicate jobs are
not launched, machines are not re-enrolled, and the allocation does not
change during execution. Bundle machines cannot be moved by manual claim
changes or the Hub's automatic queued-claim rebalancing: cancel a fully
unstarted bundle and submit a new one to change placement. Each member
receives its own native owner, lease, artifact lineage and existing GPU
qualification/Start/Stop gates. After the agents acknowledge their fixed
claims and any GPU access probes complete, **Start entire bundle** approves
all 2–16 native Starts in one durable transaction, or none if a member is
no longer eligible. The equivalent API is
`POST /api/hub/resource-bundles/start` with
`{"bundle_id":"<uuid>","expected_job_ids":["<ordered-member-uuid>",...],"confirm_start":true,"release_on_terminal":true}`.
The last flag is optional and releases each member's claim only after a
verified native `finished` or `stopped` report. Machine supervisors still
launch independently, and physical GPU compute shares are advisory.

To leave a reserved bundle awaiting machine readiness without repeatedly
returning to Train, choose **Schedule auto-start when ready**, or POST
`/api/hub/resource-bundles/schedule-start` with
`{"bundle_id":"<uuid>","expected_job_ids":["<ordered-member-uuid>",...],"confirm_schedule":true}`.
This grants **one future atomic group Start** only after every pinned job,
machine, outstanding lease and native Start gate is freshly qualified.
It never changes resource requests, automatically retries a dispatched job,
or assumes a persisted `running` label proves ownership. Every such
launch freezes `release_on_terminal:true` for safe unattended terminal
capacity release. If a member's dispatch, machine or lease identity changes,
the approval needs review; no partial Start is issued. Ordinary CPU queue
automation cannot start individual members while a group approval is pending.

CPU-only groups may additionally set
`"resume_after_restart":true,"confirm_restart_rearm":true` (separate
explicit approval) to retain their pending *unstarted* permission after
a coordinator restart. Fresh agent/lease/native preflight proof is required
again. Without that approval, a restart disarms the plan. CPU approvals
expire after 24 hours. For bundles containing a GPU, the operator must
set `"confirm_gpu_start":true`; GPU approval lasts at most 20 minutes,
requires the originally authenticated agent identities, and **never**
survives a coordinator restart or agent replacement.

Train shows pending, authorized, cancelled and review-required launch
states. **Cancel scheduled launch** leaves the original reserved jobs
untouched; the API is
`POST /api/hub/resource-bundles/cancel-start-plan` with
`{"plan_id":"<uuid>","confirm_cancel":true}`. The existing bundle
Stop/cancel operations also revoke any outstanding future launch authority.

**Stop / close bundle** performs the corresponding atomic native shutdown
request for its exact current members:
`POST /api/hub/resource-bundles/stop` with
`{"bundle_id":"<uuid>","expected_job_ids":["<ordered-member-uuid>",...],"confirm_stop":true}`.
Unsent Starts are withdrawn and their jobs cancelled; native owners that may
already have launched receive durable Stop requests and retain their claims
until verified terminal receipts. Already verified `finished`/`stopped`
members release their capacity; failed owners retain their original claims
for review. No process, checkpoint or history is deleted. Group shutdown
requests and release-on-terminal intent survive coordinator restart.

A bundle is **resource coordination**, not a distributed learner.
There is no automatic rollout transport, policy synchronization, gradient
aggregation, common checkpoint or simultaneous Start across members. The
existing project-specific TinyBrains remote rollout group (below) remains
the only supported Hub multi-machine *single training* path; do not
substitute an ordinary resource bundle for it. A continuation job or
TinyBrains remote-rollout service is not eligible for an independent bundle.
Bees still uses its own operator.

Bundle membership and native statuses remain in the registry after restart
and appear in Train. To withdraw an entirely unstarted bundle, use
**Cancel all unstarted members** or
`POST /api/hub/resource-bundles/cancel` with
`{"bundle_id":"<uuid>","expected_job_ids":["<uuid>",...],"confirm_cancel":true}`.
The exact current members are checked before claims are released, and
a started, dispatched, or uncertain member blocks cancellation of the
whole bundle. For a bundle that has already begun execution, use
**Stop / close bundle** instead; it never infers that an unknown native owner
has terminated. Unresolved bundles are prioritized in the Train panel over
older completed history.

## Trusted distributed TinyBrains rollout group (opt-in)

The `feat/hub-resource-experiments-20261110` Hub branch and matching
`feat/linux-tinybrains-runtime` TinyBrains branch have an **unqualified**
multi-machine rollout path. A single Halifax trainer owns the PPO optimizer,
checkpoint, evaluation and STOP marker. Each remote machine owns an independent
`tinybrains_rollout` native job under its own acknowledged CPU/RAM lease,
optionally with **one exclusive Windows CUDA GPU**. The learner sends a frozen
behavior policy and collects the returned on-policy shards before optimizing.
By default, each collector receives an equal action target. For heterogeneous
fixed machines, Design can freeze an optional `rollout_action_weights` array,
for example `[2,8,4]` for the learner and two ordered remote collectors.
This divides the configured total action target proportionally, assigning at
least one action to each collector and preserving the total. The chosen
weights never change during a run and do not resize CPU/GPU leases; use
measured per-collector delivered throughput to select meaningful weights.
The Hub records each collector's assigned `action_target` as a chart metric.
The distributed queue also freezes the Train metric selection and adds each
collector's decisions/s and action target, plus the collector count (up to
96 chart fields total). After a finished or stopped distributed learner has
at least five **paired**, complete collector-rate/target observations in the
latest uninterrupted chart segment, **Compare** displays advisory
`rollout_action_weights` for a *future* run. The ratios use per-collector
median delivered decisions/s, reduced to bounded integer weights; they are
a starting estimate, not a guarantee of faster PPO updates. Rates from
different action targets may change when their work shares change.
**Use proposed weights in Design** only fills the input for an approved
matching profile: review the full draft and save explicitly. Nothing
rebalances a running job, changes its resource lease, or starts training.
Re-measure when worker machines, assigned CPUs/GPUs, network or model change.
Remote workers never publish or advance learner checkpoints.

Install the *same* randomly generated 32+-byte HMAC key on each trusted host
in a private local file; on Linux use mode 0600. Never put the key in the Hub
configuration JSON. Use Tailscale IPv4 addresses in 100.64.0.0/10 and allow
the selected listener port only over the private tailnet; do not expose the
service to the public Internet.

1. On Halifax, configure the trusted `createTinyBrainsPersistentDockerAdapter`
   with `remoteCollectors:{endpoints:['100.x.y.z:12134'],keyDirectory:'/private/local/path'}`.
   Its directory must contain `rollout.key`, readable by the Docker training
   identity. `enableDualGpu:true` is a separate explicit opt-in when using
   both verified R9700s; ordinary jobs remain network-isolated.
2. On each remote host, install
   `createTinyBrainsRemoteRolloutAdapter({projectRoot,pythonPath,keyPath,
   cacheRoot,listenIp,port,threads,enableCuda})` as the trusted
   `tinybrains_rollout` native project adapter. Set `enableCuda:true` only
   for a Windows worker with an installed working PyTorch CUDA environment;
   its job then requires one *exclusive*, PCI/UUID-pinned GPU. With
   `enableCuda:false`, the worker uses CPU only.
3. Register the `tinybrains_rollout` project and remote machines in the Hub,
   and enroll their native agents. Save a TinyBrains configuration with
   `remote_rollouts:true` and the approved profile/checkpoint import.
   In **Train**, choose the saved learner configuration, enter its local
   CPU/RAM and one- or two-GPU budget, then use **Queue and reserve entire
   group**. Select Halifax and provide one CPU/RAM resource budget per
   approved endpoint in the shown order. Each collector may set an independent
   `threads` count no greater than its leased CPUs or its locally approved
   thread limit. A worker budget may also request one exclusive Windows CUDA
   GPU; no shared remote GPU is admitted. Collector slot 0 is the learner's
   local rollout; slots 1–8 correspond to approved remote endpoints.
   Updated remote workers report `collector_N_model_prepare_seconds`
   (policy/critic weight loading plus first-use allocation) so that model
   reuse can be measured separately from end-to-end collection time.
   Remote workers also report optional `collector_N_snapshot_cache_files`
   and `collector_N_snapshot_cache_bytes`: retained active frozen opponent
   artifacts after each successful update. These measure the worker's
   temporary snapshot disk footprint, not the learner's checkpoint storage;
   they should remain bounded by the active opponent manifest.
   With locally approved TinyBrains `profilePpo:true`, the collector's
   `rollout_engine_step_seconds` and `rollout_learner_inference_seconds`
   distinguish official-engine stepping (including transport) from
   learner policy/critic inference, CPU readbacks and record assembly.
   They are host-wall observations on each collector, not exclusive CPU
   consumption or GPU kernel time. The Hub exposes both per-collector
   measurements; legacy histories without them remain valid.
   The Hub also derives `collector_N_decisions_per_second` and
   `collector_N_engine_steps_per_second` from delivered work divided
   by `collector_N_elapsed_seconds`. For the learner's local collector
   (slot 0), elapsed time includes collection and any GPU stream wait;
   for remote workers (slots 1–8), it includes the complete request/
   response roundtrip (serialization, transfer and worker compute).
   These are measured delivered-work rates, **not** marginal CPU-core
   gains. Older workers with only remote roundtrip timing remain
   compatible, but cannot report local collector throughput.
   The learner separately reports `remote_policy_snapshot_seconds`, the
   time to prepare one frozen CPU weight snapshot shared by all remote
   collector requests in that PPO update. It is absent for local-only
   training. The separate `remote_opponent_snapshot_seconds` records the
   time spent reading and hashing each frozen opponent artifact once per
   update, instead of repeating disk I/O and hashing for every remote host.
   Both are preparation costs within full `collection_seconds`. Compare
   them with delivered decisions/s to identify bottlenecks. The learner
   also reports `rollout_model_refresh_seconds` for its local frozen actor
   and critic copies, now reused after each completed collection instead
   of being reallocated on every update. `collection_seconds` includes
   this model refresh from submission to completed collection.
   The Hub creates each worker configuration, all jobs and all machine
   reservations atomically; if any physical worker or capacity is missing,
   no job or claim is committed. No process is started by this action.
4. Allow each host's authenticated agent to acknowledge its offered claim,
   and qualify the learner's GPU access before launch. In **Train**, use
   **Start collectors only** to authorize all remote native Starts atomically
   without authorizing the learner, **Start coordinated group** to preapprove
   one learner GPU Start when every collector is freshly verified running, or
   **Start recoverable coordinated group** to separately approve resumption of
   that *pending* learner Start after a coordinator restart. All three use
   `POST /api/hub/distributed/tinybrains/start-collectors` with
   `{"learner_job_id":"<uuid>","expected_collector_job_ids":["<uuid>",...],"confirm_start":true}`.
   Coordinated launch also supplies `"start_learner_when_ready":true,
   "confirm_learner_start":true`. Restart recovery additionally supplies
   `"resume_after_restart":true,"confirm_restart_rearm":true`.

   To approve the **entire group before its leases or GPU probes finish**,
   choose **Schedule full group when ready** in Train, or POST
   `/api/hub/distributed/tinybrains/schedule-start` with
   `{"learner_job_id":"<uuid>","expected_collector_job_ids":["<uuid>",...],"confirm_schedule":true,"confirm_learner_start":true}`.
   The coordinator freezes the original learner/collector host assignments,
   leases, ordered HMAC endpoints and key fingerprint. Once every original
   machine has freshly acknowledged capacity and passed native admission,
   the existing atomic collector Start action is authorized together with
   one bounded future learner Start. The learner still waits for every
   original collector's authenticated running dispatch; this is not a
   second training protocol. All members, including the learner, release
   their capacity after verified native terminal receipts.

   This preapproved group launch expires after 20 minutes. Separately pass
   `"resume_after_restart":true,"confirm_restart_rearm":true` only to
   retain its *unfulfilled* authorization through coordinator restart;
   fresh machine/lease/GPU/HMAC proof is still mandatory and neither
   a failed nor interrupted native owner is retried. Without approval,
   restart disarms the future Start. Cancel before dispatch with
   `POST /api/hub/distributed/tinybrains/cancel-start-plan` and
   `{"plan_id":"<uuid>","confirm_cancel":true}`. Cancellation preserves
   the reserved group. Individual Start/cancellation must first resolve
   the outstanding group plan.

   Both the original remote collector Starts and the one-use future learner
   permission are committed **atomically** and expire after 20 minutes.
   Ordinary approval is disarmed on coordinator restart, agent replacement,
   lost lease or HMAC identity mismatch. Separately approved recovery retains
   only that *one unfulfilled* future permission; after a coordinator restart
   the Hub must see fresh authenticated agent/lease receipts and every
   **original native collector dispatch** verified running. It checks the
   exact pinned machine, lease, HMAC fingerprint, endpoint ordering and the
   learner's current GPU/native access qualification before authorizing the
   learner. Replacing the agent does not bypass these barriers. No previously
   failed, uncertain, or interrupted trainer is automatically retried, and
   persisted `running` status alone cannot authorize GPU training.

   The Train row shows pending, authorized or disarmed launch status and
   offers **Cancel future learner Start** without stopping any collectors.
   The equivalent API is
   `POST /api/hub/distributed/tinybrains/cancel-launch` with
   `{"learner_job_id":"<uuid>","confirm_cancel":true}`.
   All collector Starts are one durable transition, with no partial
   authorization if any host is ineligible. Neither action promises
   simultaneous native execution; each machine's supervisor still enforces
   its fixed resources. If a collector reports a verified terminal failure
   before learner Start under coordinated approval, the Hub automatically
   abandons the pending launch and issues durable native Stops for its other
   sidecars, retaining claims until verified termination. Unverified native
   owners are never treated as safe to release.
5. Without coordinated approval, wait for the collectors' fresh running
   receipts and explicitly Start the learner. Check `self-play/history.jsonl`: `collector_samples` reports
   each shard's ant decisions and engine steps plus remote transfer time and
   a stable endpoint hash. Compare **end-to-end** actions/s, not just GPU busy
   percentage. For a graceful stop, Stop the learner at a PPO checkpoint
   boundary. For groups created with the atomic queue action, the Hub
   requests native Stop for its remote sidecars once the learner's terminal
   report is verified; their own terminal receipts authorize automatic
   resource release. Unverified or failed native owners retain their
   reservations for operator inspection. Do not delete source runs,
   checkpoints or independent trainer directories.

To abort a **never-dispatched** learner with already-started collectors,
choose **Abort distributed startup** in Train, or POST
`/api/hub/distributed/tinybrains/abort-startup` with
`{"learner_job_id":"<uuid>","expected_collector_job_ids":["<uuid>",...],"confirm_abort":true}`.
This durably withdraws undelivered Starts, sends native Stop requests to
dispatched sidecars, and blocks any new group Starts. Verified terminal
collector claims are released, while failed services lacking explicit
prelaunch-absence evidence and unverified processes retain their reservations
for manual review. Only after **all** sidecars are cancelled or their native
leases have been safely released does the Hub cancel the unstarted learner
and release its GPU/CPU claim. This cleanup intent survives coordinator
restart. A separately approved, bounded pending learner Start may also survive
coordinator restart, but only after fresh native proof; ordinary approvals do
not. Abort never stops a dispatched learner or deletes checkpoints. Group members cannot change
machines or accept a new queued dependency during teardown.

This path is initially queued as one atomic resource group and can receive
one bounded unattended launch approval. It is not live-rebalanced, and
unknown, failed or interrupted native services are never automatically
restarted.
Collector interruptions fail the learner's active PPO update rather than
allowing incomplete or mixed-policy data into its optimizer. After a
**checkpoint-verified finished or stopped** learner has released its capacity,
choose a saved TinyBrains continuation configuration with the same profile
pin (and no external checkpoint import), select that predecessor in Train,
and select **Queue and reserve entire group** again. The new group inherits
the learner's checkpoint lineage and original Halifax host, but receives
new remote worker jobs, leases, and authenticated runtime sessions.
Failed or unverified native processes must first be inspected; this is not
an automatic retry. Remote collectors cannot join an already running
learner without a new job. Qualification requires a brief real remote-collector
run, one full PPO update, a checkpoint/STOP/resume, and verified worker
absence/timeout behavior on actual hosts.

## Not yet implemented

Production installation and qualification of project-specific adapters,
real-machine Windows CUDA and Linux systemd/ROCm qualification,
enforceable OS GPU compute and VRAM quotas, production-qualified Linux cooperative shared-GPU execution,
independent non-PyTorch shared GPU adapters, seamless adoption of
independently running trainers,
production coexistence qualification of the
CPU-only scheduler, production independent evaluators for non-Bees adapters,
trusted production-source metrics adapters for additional
projects, safe live rebalancing, and comprehensive
CLI parity for all runtime configuration controls remain future work.
Isolated Windows Job Object and browser-controlled CPU-only queue/disconnect
qualifications have passed on real hardware; these do not establish
production Bees and TinyBrains execution safety. These
must not create a second owner of the Bees training lifecycle or treat
reporting a worker as proof of exclusive host ownership.

No arbitrary commands from saved project configurations are executed. The
dashboard remains loopback-only; remote access requires a separate trusted
transport. Production multi-machine / Unity behavior has not been validated
by the development environment.
