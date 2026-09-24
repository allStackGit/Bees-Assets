# BeesServer Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, servers, executables, benchmarks, simulations, package-manager commands, or GitHub Actions were run.

Performance-regression audit baseline: `performance/2026-08-13-server-audit`.

Finding phase: one validated regression found, followed by 2 / 2 consecutive clean full static finding passes.

Fix phase: BUG-012 repaired. Existing static regression tests already encode the required duplicate-reward and discard-wins contracts; no redundant test file was added.

Post-fix verification: 2 / 2 consecutive clean full static passes found no remaining validated defects.

### BUG-012 — StoreCommands optimization lost request-wide outcome deduplication
**Status:** Resolved  
**Location:** `gamePersistence.js` — `toStoreOperation`  
**Introduced by:** performance rewrite of StoreCommands parsing  
**Resolution:** Restored request-wide ownership sets while preserving the optimized parsing/batching path. Discard IDs are collected across strategic, shooting, and targeting groups before rewards are processed; discard therefore remains terminal regardless of group/order. Positive reward IDs are then coalesced across the entire payload so one temporary outcome reservation can contribute at most one learning update.
