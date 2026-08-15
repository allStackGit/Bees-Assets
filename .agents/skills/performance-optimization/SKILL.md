---
name: performance-optimization
description: Repository-wide Bees performance audit/optimization loop with mandatory repository learning, impact/test-contract analysis, regression protection, measurement when available, two clean passes, and checkpoint commits. Optimize frame time/resource use/low-end compatibility without changing gameplay or stability.
---

# Performance Optimization

Optimize Bees for high/stable frame rate, low CPU/GPU/memory/GC/resource use, and broad low-end compatibility while preserving the exact gameplay, persistence, network, lifecycle, and default-quality contracts.

## Setup

1. Read and follow `AGENTS.md`, `PROJECT_CONSTITUTION.md`, and relevant engineering docs.
2. Fetch latest `main` and create a new unique `performance/...` branch before changes unless the user explicitly requests another branch.
3. Invoke `.agents/skills/repo-learning/SKILL.md` continuously. Its impact analysis, test classification, regression protection, and knowledge reconciliation are mandatory.
4. Reconcile `PERFORMANCE_LEDGER.md` against the current tree; retain only validated worthwhile unresolved opportunities.
5. Identify available Unity profiling/benchmark/test/release-gate workflows. Runtime measurement strengthens evidence but static-only operation must still complete the workflow when execution is unavailable.

## Hard constraints

Performance may not come from:

- changing gameplay rules/results, campaign semantics, save/network compatibility, required content, or user-visible behavior;
- weakening validation, cleanup, error handling, synchronization, lifecycle or ownership checks;
- unsafe concurrency/nondeterministic ordering;
- stale/unbounded caches, retained pooled state, leaks, races, or use-after-lifetime behavior;
- reduced numerical correctness that can alter gameplay;
- hidden lower visual/simulation quality (explicit scalable quality options are acceptable if defaults remain intended);
- reducing workload/entity counts/features in a benchmark and presenting that as optimization.

Prefer small robust changes over broad rewrites when both remove the cost.

## Measurement discipline

When runtime execution is available:

1. establish a representative baseline for the same workload/configuration;
2. record relevant frame-time/CPU/GPU/allocation/memory/load metrics and environment context;
3. implement the optimization without changing semantic workload;
4. re-run the same workload;
5. reject/revert changes that merely move cost, increase stalls/variance/memory, or create correctness/stability risk without a justified net gain.

Do not invent performance numbers in static-only mode. Validate static opportunities from clear reachability/frequency/allocation/algorithmic/resource mechanisms.

## Phase 1 — Find opportunities

Perform complete repository passes over runtime/performance-sensitive code and serialized configuration. Cover per-frame/fixed-step loops, AI/targeting/pathfinding, physics, rendering/UI, loading/assets, pooling/spawn/despawn, persistence/networking/serialization, data structures, concurrency, GC/memory, and initialization/teardown.

For each opportunity:

- establish the concrete expensive mechanism and reachability/frequency;
- trace ownership/lifecycle and relevant invariants;
- identify a behavior-preserving optimization;
- estimate regression risk and what validation/test contract protects it;
- measure when available, otherwise record static evidence;
- log only plausible end-to-end improvements, not cosmetic micro-refactors.

Continue until two deliberate full passes find zero new worthwhile validated opportunities.

## Phase 2 — Implement every valid ledger entry

For each entry:

1. Reconfirm the cost.
2. Perform mandatory impact analysis.
3. Classify affected tests and identify behavior contracts at risk.
4. Implement the smallest robust optimization.
5. Add/update tests that protect behavior the optimization could accidentally change. If the optimization fixes/exposes a regression, permanent regression protection is mandatory under `AGENTS.md`.
6. Review lifecycle, stale pooled state, ordering, races, memory retention, numerical behavior, serialized assets, persistence/network contracts, and hidden quality changes.
7. Re-measure equivalent workload when possible; otherwise prove by code trace that the targeted cost was removed/reduced by construction.
8. Update durable performance knowledge/hot-path rules when useful.
9. Remove the ledger entry only when evidence supports keeping the change, or when the opportunity is disproved/not worthwhile.

Do not keep a change merely because it looks faster.

## Phase 3 — Repeat after changes

Optimization can shift bottlenecks or expose other costs. Reset prior clean passes after production changes and repeat full review until the final condition is met.

## Performance ledger

Use `PERFORMANCE_LEDGER.md` only for current unresolved opportunities:

```markdown
### PERF-001 — Short optimization name
**Location:** file/class/function  
**Cost:** concrete CPU/GPU/allocation/memory/I/O/synchronization cost and triggering conditions  
**Optimization:** behavior-preserving change  
**Evidence:** static trace and, when available, baseline measurement  
**Risk:** correctness/stability contract that must remain protected
```

Permanent regression lessons belong in `docs/engineering/REGRESSIONS.md`.

## Bees coverage priorities

Pay particular attention to:

- `Update`/`FixedUpdate`/timers/coroutines/event dispatch;
- repeated Unity API lookups, hierarchy searches, LINQ/boxing/closures/temp allocations/logging;
- pooling/instantiation/destruction and stale pooled state;
- pathfinding/steering/targeting/combat/projectile scaling;
- physics queries/contact processing/fixed timestep;
- rendering/material/mesh/UI rebuild/culling/overdraw/resource residency;
- asset loading/scene transitions/duplicate residency;
- save/network serialization/copying/state replication;
- thread oversubscription/lock contention/task creation/main-thread handoff;
- avoidable O(n^2)/full scans/repeated invariant calculations;
- unnecessary work for inactive/distant/invisible/sleeping/unchanged entities;
- GC pressure, retained references, peak memory and native-resource lifetime;
- frame-time spikes and low-end hardware behavior, not just average FPS.

## Final stop condition

Complete only when:

1. the performance ledger is empty;
2. all implemented changes preserve documented contracts;
3. affected tests were classified and stale/missing coverage handled;
4. regressions exposed/fixed received permanent protection;
5. equivalent before/after evidence exists where runtime measurement is available, or a defensible static mechanism exists otherwise;
6. durable repository/performance knowledge was reconciled;
7. two consecutive complete post-change passes find no new worthwhile validated opportunities.

Do not claim absolute maximum optimization; state the evidence actually available.

## Git checkpoint discipline

Commit accumulated work whenever:

1. 10 opportunities have been newly logged since the prior checkpoint;
2. finding -> optimizing transition occurs;
3. 10 opportunities have been resolved/rejected since the prior checkpoint;
4. optimizing -> finding transition occurs.

Include ledger, production, test/benchmark, regression-protection, measurement-note, and repository-learning changes. Reset counters after each checkpoint. Never merge into `main` unless explicitly requested.