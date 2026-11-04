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
  and preview CPU/RAM, GPU count, and per-GPU VRAM requirements. Manually
  registered machines remain *unverified*. Candidate placements do not claim
  or allocate actual hardware.

## Data ownership

`experiment_hub_core.js` persists project definitions, drafts, and machine
inventory in `hub-v1.json` in the selected experiment-data directory. The
legacy `experiments-v1.json` is not migrated, rewritten, or replaced. New Hub
registry writes are atomic and only published in memory after they succeed.
If the optional Hub registry cannot be loaded, Bees experiment management
continues to run independently.

`experiment_hub_resources.js` performs pure admission calculations; it
does not execute jobs or enforce isolation. A verified GPU may be reserved
exclusively in the eventual scheduler, but this preview **is not** a physical
reservation. The UI requires explicit confirmation before operating the
Bees learner.

## Not yet implemented

Generic project execution adapters, authenticated machine agents, OS-enforced
CPU/GPU reservations, the multi-project job scheduler, cross-project run
history and metrics ingestion, safe live rebalancing, and comprehensive CLI
parity for all runtime configuration controls remain future work. These
must not create a second owner of the Bees training lifecycle or treat
reporting a worker as proof of exclusive host ownership.

No arbitrary commands from saved project configurations are executed. The
dashboard remains loopback-only; remote access requires a separate trusted
transport. Production multi-machine / Unity behavior has not been validated
by the development environment.
