# Resource-efficient training workstream

Updated 2026-11-12. Branch: `feat/hub-resource-efficiency-20261112`.
Scope: TinyBrains, Courier, and future native projects. Bees integration belongs to the unified native rewrite. The Hub chooses **fixed** machine/CPU/RAM/GPU leases; trainers must optimize their own use of those leases.

## Implemented (not yet machine-qualified)

- Hub accepts and archives sustained project-neutral **stage-time evidence**: completed collection vs PPO optimization shares, observed rollout engine vs inference shares, and individual collector round-trip overhead where both timing sources exist. A five-of-six, >=2-minute majority flags a dominant stage in the existing Train/Monitor summary; this does **not** prove CPU/GPU saturation. The original project metrics and host observations continue through normal chart, history, comparison, and export paths.
- TinyBrains companion backend branch `allStackGit/TinyBrains:feat/hub-resource-efficiency-20261112`, based on `feat/linux-tinybrains-runtime`: a conservatively tuned split of a **fixed total PPO action target** among existing local and remote collectors. It uses sustained delivered actions/s, skips early warmup, requires >=90 seconds and six steady samples, a predicted >=18% improvement, and a >=30-update cooldown. All collectors remain running; per-update targets are immutable once submitted, including overlapped PPO collections. Explicit frozen weights override automatic balancing.
- The Hub persistent TinyBrains launcher opts into this capability by default when the installed backend advertises the new balancing module and no explicit weights were supplied. Older trainer installations retain their old behavior with an explicit stderr diagnostic. Counts of adjustments and changed allocations reach the existing metrics selector.

## Significant work remaining

1. **Actual live per-machine environment concurrency tuning:** TinyBrains currently creates one official engine with match concurrency fixed at startup; changing the Hub CPU quota alone cannot resize it safely. Implement independently reusable simulation environment instances, graceful retire at episode end, RAM-aware limits and stable, conservative throughput calibration without restarting the pool. Integrate analogous support in Courier's trainer, not merely its Hub adapter.
2. **GPU inference and PPO efficiency:** Batch across the expanded simulation pool and minimize excessive microbatches/host synchronizations without changing PPO update semantics; make a useful one-vs-two-device strategy driven by actual work, not GPU busy percentage.
3. **Detailed bottleneck attribution:** Instrument actual ingestion, queueing, transport/serialization, processing, GPU kernel/transfer pressure and memory effects within backends. Current reported stage dominance is narrower timing evidence, not a definitive hardware bottleneck classification. Keep new measurements in the Hub's existing project-neutral metrics path.
4. **Persistence and cross-project adoption:** Retain proven runtime settings across safe continuations when hardware/workload identity matches; connect the runtime mechanisms to Courier and any future adapters with minimal common contracts.
5. Qualify useful SPS and learning correctness on supported Windows/Linux and heterogeneous CPU/GPU systems after substantive development is complete.

Do not misreport the current stage metrics or remote action balancing as completed CPU environment autosizing or GPU batching. Prefer completed, measurable execution changes over more advisory monitoring or configuration layers.
