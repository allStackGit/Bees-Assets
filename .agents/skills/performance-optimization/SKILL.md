---
name: performance-optimization
description: Perform a repository-wide game performance optimization loop for Bees: find and log validated performance opportunities, require two consecutive clean full-code passes, implement every worthwhile optimization, measure when execution is available, and repeat until no validated opportunities remain. Optimize for maximum frame rate, minimum CPU/GPU/memory/GC/resource use, and broad low-end hardware compatibility without degrading gameplay correctness or stability. Profiling and benchmarking are optional enhancements when execution is available; static-only execution must still complete the full workflow.
---

# Performance Optimization

Perform a repository-wide performance audit and optimization loop for Bees. Find, validate, record, implement, and re-review concrete performance improvements until repeated full-code review no longer reveals worthwhile opportunities.

The primary objective is to make the game run as fast and efficiently as practical while consuming as few resources as practical and remaining usable on the lowest-capability hardware reasonably supported by the project. Performance work must not reduce game stability, correctness, or required gameplay behavior.

## Setup

1. Read `AGENTS.md`, `CLAUDE.md`, and applicable repository instructions if present.
2. Fetch the latest `main`.
3. **Always create and check out a new working branch from the current `main` before making any optimization-ledger, benchmark, test, repository-memory, configuration, asset, or production-code changes.** Use a descriptive `performance/...` branch name. Never reuse or overwrite an existing optimization branch; if the intended name already exists, choose a new unique name.
4. **Invoke and follow `.agents/skills/repo-learning/SKILL.md` as a mandatory companion skill for the entire workflow.** Load the durable repository memory and focused documentation it requires before beginning the first optimization pass. The performance-optimization skill does not replace repository learning and must not proceed as though repository memory were optional.
5. Create or locate `PERFORMANCE_LEDGER.md` at the repository root. Use that single file for all current unresolved optimization opportunities.
6. Reconcile the existing ledger against the working tree before beginning:
   - remove entries already implemented, invalid, obsolete, unreachable, or no longer worthwhile;
   - retain every still-valid opportunity;
   - ensure retained entries follow the ledger format below.
7. Identify any available profiling, benchmarking, Unity Profiler, test, build, and representative gameplay workflows. These are optional evidence sources when runtime execution is available; their absence must not block or shorten the static optimization workflow.

## Repository Learning Requirement

Use the `repo-learning` skill continuously during every finding pass, optimization phase, measurement phase, and post-change review.

- Consult maintained repository memory before each full pass and before changing an unfamiliar subsystem so prior architecture, contracts, pitfalls, hot paths, and known performance facts reduce redundant rereading.
- Revalidate memory against current source whenever correctness or performance depends on a detail that may have changed.
- Record durable reusable performance knowledge when discovered, including important hot paths, scaling behavior, safe caching/pooling rules, ownership/lifetime constraints, and optimization pitfalls.
- Do not create a chronological profiling diary or duplicate existing memory.
- Replace stale knowledge and consolidate duplicates.
- Include repository-learning updates in the same required checkpoint commits as the optimization work that produced them.
- Continue using repository learning even in static-only execution mode; lack of profiling or benchmarking does not reduce this requirement.

## Execution Modes

This skill must work both with and without the ability to execute the project.

### Runtime-Capable Mode

When the environment can run the game, profiler, benchmarks, tests, or representative workloads, use runtime evidence where it materially improves confidence. Establish equivalent before/after measurements for substantial changes when practical.

### Static-Only Mode

When runtime execution, profiling, benchmarking, builds, or tests are unavailable, **continue the complete find/log/optimize/repeat workflow using static analysis and code tracing. Do not stop, defer the audit, or require the user to provide measurements.**

In static-only mode:

- validate performance opportunities from clear code-path evidence, reachability, frequency/scaling, allocation behavior, algorithmic complexity, redundant work, synchronization, resource lifetime, or engine/API cost;
- do not reject an otherwise well-supported optimization solely because no profiler or benchmark can be run;
- do not invent speedup percentages, frame-rate gains, allocation counts, or other measurements;
- mark runtime measurement as unavailable or pending when useful, but this does not by itself keep a ledger entry unresolved;
- statically review tests or benchmarks that are added, but do not require them to be executable for the workflow to proceed;
- use the same two-clean-pass stop condition, based on the strongest evidence available in that environment.

Runtime measurement increases confidence; it is not a prerequisite for this skill to function or complete.

## Hard Constraints

Performance is never allowed to come from silently making the game less correct or less stable.

- Do not change gameplay rules, simulation results, save compatibility, network contracts, required content, or user-visible behavior merely to make code faster.
- Do not remove validation, safety checks, cleanup, error handling, synchronization, or lifecycle handling unless code tracing proves it redundant and the replacement preserves the same contract.
- Do not trade deterministic or required ordering behavior for unsafe concurrency.
- Do not introduce unbounded caches, object retention, pooled stale state, memory leaks, resource leaks, race conditions, use-after-dispose behavior, or initialization-order hazards.
- Do not reduce numerical correctness or precision where it can alter gameplay behavior.
- Do not lower graphical or simulation quality as a hidden optimization. Optional scalable quality settings are acceptable when they preserve the current intended default behavior and improve low-end compatibility.
- Do not optimize editor-only or cold paths at the expense of runtime hot paths unless the change has independent value.
- Do not make broad architectural rewrites when a smaller robust change can remove the cost.
- Do not log style cleanup, generic refactors, or theoretical micro-optimizations without a concrete performance mechanism.

When a proposed change has meaningful regression risk, prefer a smaller optimization or add focused tests/validation around the affected contract.

## Measurement Discipline

Performance claims require evidence proportionate to the environment and the change.

When runtime execution is available:

1. Establish a representative baseline before changing the relevant path.
2. Measure the smallest stable workload that exercises the suspected bottleneck.
3. Record enough information to compare before and after: workload, configuration, relevant hardware/runtime context, and the metric being improved.
4. Re-run the same workload after the change.
5. Reject or revert changes that do not produce a meaningful improvement, merely move work elsewhere, increase variance, or regress another critical resource without a justified net benefit.

Useful metrics include:

- frame time and frame-rate distribution, especially p50/p95/p99 and spikes;
- main-thread and worker-thread CPU time;
- render-thread/GPU time and synchronization stalls;
- managed allocations per frame and GC frequency/pause time;
- native memory, managed memory, peak memory, and retained memory;
- object/component creation and destruction frequency;
- physics, pathfinding, AI, update-loop, serialization, networking, loading, and rendering costs;
- draw calls, batching, culling, overdraw, texture/mesh/resource residency where relevant;
- startup/load latency and scene-transition stalls;
- idle resource use and background work.

Do not benchmark a changed workload, reduced entity count, disabled feature, easier scene, or altered quality target and present it as an optimization.

When runtime execution is unavailable, static analysis may validate an opportunity when the performance mechanism is clear from the code path, such as avoidable per-frame allocation, repeated expensive lookup in a hot loop, needless O(n²) work, redundant serialization, unnecessary synchronization, repeated asset/resource loading, or avoidable work that is provably invariant. Record the evidence actually available and never invent a speedup number.

## Core Loop

Repeat the following cycle until the final stop condition is satisfied.

### Phase 1 — Find and Log Optimization Opportunities

Perform full repository passes looking for concrete, worthwhile runtime performance opportunities.

A full pass means systematically covering the entire relevant runtime codebase and performance-sensitive configuration, including major systems, update loops, rendering, AI, pathfinding, physics, input, UI, networking, persistence, loading, spawning/despawning, pooling, resource ownership, serialization, and cross-system data flows. Do not count a focused subsystem review or continuation of an incomplete pass as a full pass.

For each suspected opportunity:

1. Identify the expensive operation or resource cost.
2. Establish that the path is reachable and sufficiently frequent, heavy, bursty, or memory-intensive to matter.
3. Trace callers, callees, ownership, lifetime, data flow, and affected invariants.
4. Check whether existing caching, pooling, batching, culling, throttling, parallelism, engine behavior, or guards already eliminate the apparent cost.
5. Identify a concrete optimization that preserves behavior and stability.
6. Estimate risk and the resource expected to improve.
7. Measure the baseline when execution is available and useful; otherwise use static evidence without treating missing measurement as a blocker.
8. Log the opportunity only when the mechanism is validated and the expected benefit is plausibly meaningful.

Prefer systemic hot-path improvements over cosmetic micro-optimizations. Still include small changes when they occur at very high frequency, remove allocations or synchronization from a critical loop, reduce hardware requirements, or combine into a meaningful cumulative saving.

Continue performing full passes until **two consecutive full passes find zero new validated worthwhile optimization opportunities**.

- If a pass finds one or more new opportunities, log all of them, finish that full pass, reset the clean-pass count to zero, and begin another full pass.
- A clean pass counts only if the entire relevant runtime codebase was reviewed and no new validated worthwhile opportunities were found anywhere in that pass.
- The two clean passes must be separate deliberate passes using different review angles where useful.

Only after two consecutive clean full passes may the workflow move to Phase 2.

### Phase 2 — Implement Every Opportunity in the Ledger

Process every currently valid entry in `PERFORMANCE_LEDGER.md`.

For each entry:

1. Reconfirm that the cost still exists in the current working tree.
2. Trace enough surrounding behavior to understand correctness, lifecycle, ownership, threading, and gameplay contracts.
3. Establish or retain a baseline measurement when execution is available; otherwise retain the static evidence supporting the opportunity.
4. Implement the smallest robust optimization that addresses the underlying cost.
5. Add or update focused tests when useful to protect behavior that the optimization could accidentally change. Tests should validate contracts, not implementation details.
6. Review the change for stability hazards, changed ordering, stale pooled state, lifetime errors, race conditions, memory retention, numerical changes, hidden quality reductions, and cross-system regressions.
7. Re-measure the same representative workload when execution is available. In static-only mode, perform a careful post-change trace to verify that the targeted cost was actually removed or reduced by construction.
8. Remove the ledger entry when the optimization is implemented and the available evidence supports keeping it. If investigation disproves the opportunity, the gain is negligible, or the regression risk outweighs the gain, revert/omit the change and remove the entry as invalid or not worthwhile.

Do not keep a change merely because it looks faster. The optimized code should have a defensible performance mechanism and preserve the game's contract.

If implementing one optimization reveals another validated opportunity, add the new entry to the ledger and continue resolving the existing ledger. The next audit cycle will independently re-examine the full codebase.

### Phase 3 — Repeat

After Phase 2, return to Phase 1 and perform fresh full-code optimization passes against the modified codebase.

Optimizations can shift bottlenecks, change allocation patterns, expose different hot paths, or make previous costs newly significant. Previous clean passes do not carry forward across a production optimization phase. Reset the clean-pass count to zero after production changes.

Repeat Phases 1 and 2 for as many cycles as necessary.

## Performance Ledger

Maintain one `PERFORMANCE_LEDGER.md` containing only currently valid unresolved opportunities.

Each opportunity must use this format:

### PERF-001 — Short Optimization Name
**Location:** `path/to/file.ext`, class/function/method or relevant lines  
**Cost:** Concrete CPU, GPU, allocation, memory, I/O, synchronization, loading, or other runtime cost and the conditions under which it occurs.  
**Optimization:** Concise description of the proposed behavior-preserving change.  
**Evidence:** Static trace and, when available, baseline measurement demonstrating why the opportunity is real.  
**Risk:** Main correctness/stability risk that must be preserved or checked.

New findings should use the next unused sequential identifier. Keep identifiers stable while entries remain in the ledger. If the ledger becomes completely empty, a later cycle may restart at `PERF-001`.

The ledger is a current work queue, not permanent history. Remove entries once they are implemented, disproved, obsolete, or judged not worthwhile. Git history preserves prior findings.

Do not duplicate an existing entry. Multiple symptoms of one underlying bottleneck should normally remain one entry.

## Bees Optimization Coverage

Pay particular attention to:

- per-frame `Update`, `FixedUpdate`, `LateUpdate`, coroutines, timers, and event dispatch;
- expensive Unity API calls repeated in hot paths;
- avoidable `GetComponent`, scene/object searches, hierarchy traversal, LINQ, boxing, closures, iterator allocations, temporary collections, strings, and logging in runtime loops;
- object instantiation/destruction that should use safe pooling;
- pooled objects retaining stale state or references;
- pathfinding, steering, targeting, combat, squad, ship, projectile, and AI work that scales poorly with entity count;
- physics queries, collision layers, contact processing, and fixed-timestep workload;
- rendering submission, material/mesh churn, batching, culling, visibility, particle/VFX costs, UI rebuilds, and unnecessary GPU synchronization;
- asset loading/unloading, duplicate resource residency, texture/mesh memory, scene loading, and asynchronous loading opportunities;
- serialization, save/load, networking, packet construction, copying, parsing, and redundant state replication;
- thread oversubscription, lock contention, task/job creation overhead, false parallelism, and main-thread handoff costs;
- data structures with avoidable O(n²) or repeated full-scan behavior;
- repeated calculations that are invariant within a frame, tick, entity lifetime, scene, or configuration lifetime;
- unnecessary work for inactive, distant, invisible, sleeping, disabled, or unchanged entities;
- GC pressure, fragmentation, retained references, peak-memory spikes, and native resource lifetime;
- graceful scaling to older CPUs, limited GPU throughput/VRAM, and constrained system memory without breaking required behavior;
- initialization and loading paths that cause large stalls or resource spikes;
- interactions introduced by optimizations from the previous cycle.

Optimize total frame-time behavior, not just average FPS. A change that improves average throughput but creates worse stalls, memory spikes, or instability is not automatically an improvement.

## Final Stop Condition

Stop only when all of the following are simultaneously true:

1. `PERFORMANCE_LEDGER.md` contains no unresolved opportunities.
2. No production optimization remains to be made from the previous cycle.
3. Any tests or benchmarks judged useful have been added or updated and statically reviewed; their absence or inability to run does not block static-only completion.
4. For changes where runtime measurement is available, equivalent before/after evidence shows no material regression in critical resources. For static-only changes, code tracing shows that the targeted cost was removed or reduced by construction without violating the hard constraints.
5. Two consecutive complete, deliberate full-code passes over the current post-optimization codebase found zero new validated worthwhile optimization opportunities.

Do not claim the game is maximally optimized in an absolute sense. Completion means the repository reached an empty performance ledger and two consecutive clean full-code passes under the evidence available in the execution environment. Missing profiling or benchmarking alone is never a reason to prevent completion.

## Git Discipline

Keep all audit records, optimizations, tests/benchmarks, and repository-learning changes on the dedicated working branch unless the user explicitly instructs otherwise.

Commit all accumulated optimization changes whenever **any** of these triggers occurs:

1. **10 opportunities found:** immediately after the 10th newly validated opportunity since the previous commit is logged in `PERFORMANCE_LEDGER.md`.
2. **Finding → optimizing transition:** immediately before leaving Phase 1 for Phase 2, commit all pending ledger/repository-memory/baseline work even if fewer than 10 opportunities were found since the previous commit.
3. **10 opportunities resolved:** immediately after the 10th opportunity since the previous commit is implemented, disproved, or rejected and its ledger entry is removed. Include the corresponding production, test/benchmark, measurement-note, ledger, and repository-memory changes.
4. **Optimizing → finding transition:** immediately before leaving Phase 2 and returning to Phase 1, commit all pending optimization/test/benchmark/ledger/repository-memory changes even if fewer than 10 opportunities were resolved since the previous commit.

Treat each commit as a checkpoint. After committing, restart the found/resolved counters from zero. Do not create empty commits. If multiple triggers coincide, one checkpoint commit satisfies all triggers occurring at that point.

Keep commits coherent and limited to performance work and repository memory required by it. Never merge the working branch into `main` as part of this skill unless the user explicitly requests the merge.
