# Bees asynchronous distributed MA-POCA (v2)

**Development branch:** `feature/async-distributed-poca-v2`
**Starting point:** stable `main` commit `654841686045b65cedf2e06b8333130988f81016`
**Historical implementation:** `2becf6e8bc45532e3e9ad8b64a8e4038c20eeb16` (rolled back, useful reference)

## Agreed design — do not reinterpret between sessions

The objective is **higher effective Bees learning throughput by adding CPU capacity**, while maintaining acceptable learning quality. We explicitly accept bounded asynchronous policy and gradient lag; **exact sequential PPO/Adam equivalence is not a requirement**. It is not valid to reject this design merely because minibatches cannot use the latest weights.

- Exeter alone owns policy/critic checkpoints, authoritative Adam state and optimizer-step ordering. No worker updates its own trainable model or optimizer moments.
- Rollout workers generate experience independently under the existing threaded `PolicyLag` limit. This *rollout-policy* lag is distinct from *gradient* staleness.
- Dedicated or hybrid remote CPU workers evaluate masked Bees MA-POCA gradients against **immutable actor + critic snapshots**; allow multiple outstanding minibatches and multiple jobs using one snapshot.
- Exeter validates session, claim, job identity, model ABI, policy/critic parameter names, shapes, finite values and **optimizer revision** before any gradient is applied. An optimizer revision advances by one **per authoritative Adam step**, not once per Unity rollout policy publication or environment step.
- Configure and record the maximum acceptable `current_optimizer_revision - snapshot_optimizer_revision`. Explicitly reject future, mismatched and over-age gradients. Study how this bound affects learning; do not assume the existing `PolicyLag 4` means four Adam updates.
- One authoritative Adam instance applies admitted gradients; a failed, expired or invalid remote job must never stall the live learner. Normal GPU PPO and all rollout operations remain available as a fallback.
- The *existing* Bees action masks, per-dimension PPO clipping, MA-POCA centralized critic and group-size weighting are part of the required loss. Do not treat masked dimensions as training failures or silently change PPO's objective.

## Findings to preserve

The previous one-job CPU experiment was shadow-only: remote gradients were **never applied**. A representative 512-sample minibatch took about 0.54 s for the Linux compute but 32.4 s overall round trip (other runs 14–32 s). The transfer/payload and serialized job lifecycle need measurement before speed claims. Snapshot cache did not help because the central probe allocated a new snapshot for each job. Previous CPU/GPU actor-gradient relative error varied and reached 15%; critic agreement was near exact. Gaussian PPO ratio underflow was measured *after* action masking and has **not** been diagnosed as a training bug.

The old probe allowed just one outstanding request, so it was not an end-to-end benchmark of the agreed parallel architecture. Its snapshot registration/claim machinery and CPU reconstruction are reusable; the single-job probe, status surface and live runtime wiring should not be restored wholesale.

## Implementation sequence and gating

1. **Foundation (this branch):** port isolated CPU gradient computation and the bounded session-scoped exchange; add central scheduling with explicit optimizer revision and reusable snapshots. None of these modules is imported by the production runtime.
2. **Controlled correctness and transport measurements:** execute actual Bees minibatches and actor/critic snapshot through Exeter CPU vs CUDA vs remote CPU; compare losses, masked active gradients and parameter-wise disagreement. Instrument end-to-end bytes, serialize/deserialize, queue, snapshot fetch/cache, inference/backward, result upload and host-side decode. Record multiple concurrent jobs for one reused snapshot.
3. **Asynchronous integration:** route jobs through the existing authenticated WAN broker, add optional remote CPU worker supervision, and apply *only* admitted gradients through Exeter Adam. Central must maintain a monotonically increasing optimizer revision across checkpoints/restarts. Start with shadow-only and then isolated optimizer copies, not the running production run.
4. **Throughput and learning qualification:** compare completed, **accepted** gradient updates per wall-second, remote transfer utilization, learner SPS and policy quality at equal wall time to GPU-only baseline. Sweep CPU workers, concurrent jobs, payload sizes and maximum lag. Demonstrate useful speed gains before merging anything to `main`.

## Safety / current status

The branch now has an opt-in `--learning-cpus` sidecar in the managed Linux and Windows launchers; the *default remains zero*, and no central PPO dispatch is enabled. Authenticated broker endpoints, cached worker snapshots, bounded job scheduling and a standalone Exeter-only Adam gradient applier exist. **The applier has not yet been wired into the PPO update loop.** No live remote gradient can be applied with this branch in its current state.

The remaining required code is the central asynchronous dispatcher/collector, authoritative optimizer-revision accounting for each local and remote Adam step, checkpoint/restart ownership, explicit runtime controls, and a real end-to-end benchmark. Actual Bees model comparison and WAN hardware validation must be completed before production use.

Do not replace this architecture with a synchronous per-minibatch remote round trip. Do not claim a throughput improvement on the basis of CPU compute timing alone.
