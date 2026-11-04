# Experiment Hub foundation

The existing `bees_experiments.js` remains the authoritative active Bees experiment
coordinator, with its existing queue, checkpoints, release barriers, and evaluations.
`experiment_hub_core.js` stores separate project definitions, reusable configuration
drafts, and machine inventory in `Training/Experiments/hub-v1.json` (or the selected
experiment-data directory). Existing `experiments-v1.json` is not migrated or rewritten.

New projects start as *unconfigured*: saving a definition is supported, but starting
it is explicitly prohibited until a trusted project execution adapter is installed.
Bees runs continue through the existing verified Bees queue and operator. No code
entered through the web UI is executed.

Machine records start *unverified*. `experiment_hub_resources.js` computes
conservative CPU, RAM and exclusively assigned GPU reservations from verified
inventory; its calculations are admission guidance, not OS-level enforcement.
No reservation or remote-work launch is claimed on an unverified machine.

Next implementation boundaries are authenticated machine agents, OS resource isolation
(cgroups or Windows equivalents), a durable job lifecycle with crash recovery, and a
Bees adapter that reconciles already active runs before accepting control. These
must use the same centralized ownership authority, not a second Bees worker scheduler.

The platform intentionally favors fail-closed behavior while existing production
Bees training is running. Generic cross-project execution is **not enabled** by this
foundation alone.
