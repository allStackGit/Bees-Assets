---
name: performance-optimization
description: Perform a repository-wide BeesServer performance optimization loop: find and log validated bottlenecks, require two consecutive clean full-code passes, implement every worthwhile semantics-preserving optimization, measure equivalent workloads when safe execution is available, maintain repository learning, and repeat until no validated opportunities remain.
---

# Performance Optimization

Optimize BeesServer for lower latency, higher throughput, bounded memory, efficient database/I/O use, and event-loop resilience without weakening persistence, security, protocol, learning-history, or production/test isolation.

## Setup

1. Read and follow `AGENTS.md` and `.agents/skills/repo-learning/SKILL.md` throughout.
2. Fetch latest `main` unless the user specified another base.
3. Always create a new unique `performance/...` working branch before optimization-ledger, benchmark, test, memory, configuration, or runtime changes.
4. Read constitution, invariants, system map, validation policy, detailed development memory/database model, and relevant permanent regressions.
5. Reconcile root `PERFORMANCE_LEDGER.md`: remove implemented/invalid/obsolete/not-worthwhile entries and retain all still-valid opportunities.
6. Identify safe representative test/benchmark paths. Runtime measurement is useful when available but static analysis must still work when execution is unavailable.

## Non-negotiable constraints

A speedup is invalid if it:

- changes Unity-visible request/response semantics or persistent strategy identity;
- loses, duplicates, misattributes, rounds, or acknowledges outcome/history data incorrectly;
- weakens real transaction, rollback, retry, reservation, reconnect, authentication, or ownership guarantees;
- turns an error into empty/success state;
- weakens exact 64-bit numeric representation;
- changes consolidation totals/threshold semantics or allows it to race writers;
- bypasses required cache invalidation or creates stale authoritative state;
- relaxes production/test database isolation or production migration gates;
- introduces unbounded queues/caches/buffers/retries, memory leaks, starvation, event-loop stalls, races, or cleanup hazards;
- improves a benchmark by removing required work, using easier data, or testing a different path.

Prefer the smallest robust optimization that preserves all relevant invariants.

## Execution modes

### Runtime-capable

When safe execution is available, use representative equivalent before/after measurements. Prefer disposable/local/test infrastructure and `bees_test`. Never benchmark by mutating production `ram`.

Record enough context to compare meaningfully: workload, request volume/concurrency, dataset size, server mode, relevant DB/test configuration, Node version when material, and measured metric.

### Static-only

When tests/benchmarks/server execution are unavailable, continue the complete find/log/optimize/review loop using code-path evidence. Do not invent latency, throughput, CPU, I/O, or memory improvements numerically. A static optimization must have a clear mechanism such as reduced algorithmic complexity, fewer allocations/copies, fewer DB round trips, bounded concurrency, less serialization, or eliminated redundant work.

## Phase 1 — Find and log opportunities

Perform complete passes over performance-sensitive runtime behavior, including request queues/dispatch, authentication, Game/strategy lookup, caches, StoreCommands persistence, database queries/transactions/index use, consolidation, file persistence/recovery, schema/migrations, timers/background jobs, startup, logging, serialization and tests/benchmarks.

For each suspected opportunity:

1. establish reachability/frequency/scale;
2. identify the concrete CPU, memory, allocation, event-loop, network, DB, filesystem, lock/barrier, serialization, or algorithmic cost;
3. trace ownership/lifecycle/concurrency and correctness constraints;
4. verify existing caching/batching/indexing/parallelism does not already eliminate it;
5. identify a behavior-preserving optimization and its main regression risk;
6. measure a safe baseline when available, otherwise record static evidence;
7. log only opportunities plausibly meaningful end-to-end.

Continue until **two consecutive complete passes find zero new validated worthwhile opportunities**.

## Phase 2 — Implement every ledger entry

For each valid opportunity:

1. reconfirm cost against current source;
2. trace affected protocol/persistence/security/concurrency/schema contracts;
3. retain a representative baseline when runtime evidence is available;
4. implement the smallest robust optimization;
5. classify affected tests and add/update contract tests or benchmarks when useful;
6. review specifically for data loss, changed ordering, stale cache/reservation state, transaction boundary errors, numeric precision loss, starvation, memory growth, production-safety weakening, and hidden reduced work;
7. re-measure the equivalent workload when safe execution is available; otherwise prove the targeted cost was reduced by construction;
8. update durable repository performance knowledge when reusable;
9. remove the ledger entry when the available evidence supports the optimization, or reject/revert it when benefit is negligible or risk outweighs benefit.

After production changes, reset clean-pass count and return to Phase 1 because optimizations can shift bottlenecks.

## Ledger format

### PERF-001 — Short optimization name
**Location:** `path`, function/class/area  
**Cost:** Concrete cost and conditions/scale.  
**Optimization:** Semantics-preserving proposed change.  
**Evidence:** Static trace and safe baseline when available.  
**Guardrail:** Contract(s) that must remain unchanged.

Keep `PERFORMANCE_LEDGER.md` as unresolved work only; Git history preserves prior entries.

## Server optimization coverage

Pay particular attention to:

- queue drains and array operations under large bursts;
- repeated parsing/stringification/copying/hash computation;
- accidental serialization of independent requests;
- redundant authentication/network work;
- MySQL query counts, transaction round trips, connection churn and missing/unused indexes;
- N+1 queries and repeated existence/history lookups that can be safely batched;
- cache hit/miss behavior and invalidation costs;
- large Map/array scans, sorting, O(n^2) consolidation/history operations;
- exact `BigInt` aggregation implemented without unnecessary conversions;
- StoreCommands serialization scope and opportunities to increase concurrency without violating one-Game ownership;
- consolidation scheduling/backoff and writer coordination;
- cache serialization snapshots, filesystem backpressure and retained duplicate buffers;
- recovery/export batch sizing and I/O flow;
- timers/background work, idle CPU, logging and cleanup scans;
- VM transformation/startup overhead only when meaningful to actual operational latency;
- memory retention by inactive Games, reservations, pending requests, caches and closures;
- benchmarks/tests that measure helper speed while missing end-to-end DB/WebSocket cost.

Optimize p95/p99/event-loop resilience and memory bounds as well as average throughput.

## Completion and Git discipline

Stop only when the performance ledger is empty, all accepted optimizations/tests/memory updates are complete, available evidence supports preserving semantics, and two consecutive complete post-change passes find no new worthwhile opportunities.

If runtime execution was unavailable, explicitly say measurements/tests were not run.

Commit checkpoints whenever 10 opportunities are found, finding -> optimizing, 10 opportunities are resolved/rejected, or optimizing -> finding. Include related tests/ledger/memory changes. Never merge into `main` unless explicitly requested.