# Experiment Hub foundation

The dashboard remains at `http://127.0.0.1:7148` and now has Design, Train,
Monitor, Compare, and Machines tabs. The existing Bees experiment coordinator,
operator, queue, checkpoint history, release compatibility checks, and evaluations
remain authoritative.

## Currently available

- **Design:** Save, revise, and duplicate project configurations without scheduling
  them. Copy the current Bees editor into a draft, or select **Save as draft**
  directly from the Bees form. Saved Bees configurations can be queued using
  the existing validated experiment queue. Other projects can store JSON
  configurations but cannot yet execute.
- **Train:** Preserve existing experiment queue, stage transitions, paused-run
  worker updates, and Unity maintenance rebuilds. For ordinary (non-experiment)
  Bees runs, the browser can start/resume the current run, request a fresh run,
  build an RL release, gracefully stop training, publish worker-only updates
  while stopped, and specify Unity environment arguments. These invoke existing
  operator code. Actions are refused while the experiment manager owns a run
  or has an armed queue. Ordinary runs are observed via the existing control
  plane; they are not retroactively inserted into the experiment database.
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

## Read-only machine agent enrollment

1. Register a machine with declared CPU slots, memory and GPU inventory in
   **Machines**. These values are planning ceilings, not discovered facts.
2. Select **Enroll**. Copy the one-time 256-bit token into a protected agent
   environment, together with `EXPERIMENT_HUB_MACHINE_ID`. The token is
   never returned in state refreshes, stored in cleartext by the Hub, or
   displayed again. **Rotate** invalidates the previous token; **Revoke**
   disables the agent until a new enrollment.
3. Start `node Training/experiment_hub_agent.js` on that machine using
   Node.js 18 or later. It sends read-only authenticated heartbeats every
   15 seconds and reports available CPU parallelism, total RAM, and OS.
   Set `EXPERIMENT_HUB_AGENT_TOKEN` from the enrollment output. Do not
   commit it or include it in logs or an unprotected shell history.
4. The Hub listens only on `127.0.0.1:7148`. A different computer needs
   a **trusted encrypted tunnel**, for example SSH local forwarding from
   a worker's port 17148 to the coordinator's `127.0.0.1:7148`.
   Set `EXPERIMENT_HUB_URL=http://127.0.0.1:17148` on that worker. The
   agent only accepts localhost HTTP targets; do not expose the manager
   directly on the LAN or Internet.
5. **Connected** means a correctly authenticated process reported recently,
   not that the process has exclusive control of the host. After 45 seconds
   without a heartbeat, it is **offline**. Last-seen time is retained in
   memory until coordinator restart. CPU and RAM observations are capped
   by registered limits. GPU memory and compute budgets are unverified
   estimates until native isolation/monitoring exists.

The agent has **no remote command interface** and does not start or stop
Bees, Ants, or any third-party training process. Enrolled machines are still
ineligible for Hub execution. The Hub does not yet enforce resource leases;
even a successful placement may interfere with an existing untracked job.

## Not yet implemented

Generic project execution adapters, authenticated resource-lease acknowledgments,
machine-level process ownership and OS-enforced CPU/RAM controls,
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
