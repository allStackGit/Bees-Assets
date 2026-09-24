---
name: bug-finding
description: Perform a repository-wide static BeesServer bug audit and repair loop: find and log validated defects, require two consecutive clean full-code passes, fix every ledgered bug, add/update regression tests without executing them, maintain repository learning, and repeat until the ledger is empty and two clean passes find nothing.
---

# Bug Finding

Perform a repository-wide static bug audit and repair loop for BeesServer.

## Setup

1. Read and follow `AGENTS.md` and `.agents/skills/repo-learning/SKILL.md` for the entire workflow.
2. Fetch latest `main` unless the user specified another base.
3. Always create a new unique `bug-audit/...` working branch before making changes. Never reuse/overwrite an existing audit branch unless the user explicitly requests that branch.
4. Read the project constitution, engineering invariants/system map/validation policy, detailed development memory, database model, live-integration safety documentation, and relevant regression history.
5. Reconcile root `BUG_LEDGER.md`: remove entries proven fixed/invalid/obsolete/unreachable, retain every still-valid unresolved defect.

## Restrictions

- Do not run tests, `npm test`, live integration, server processes, migrations, production operations, or GitHub Actions as part of this skill unless the user separately and explicitly requests execution.
- You may and should create/modify tests needed to protect validated defects, but only statically review them in this workflow.
- Never access or mutate production database `ram`.
- Do not log speculation, style cleanup, generic refactors, or theoretical risks as bugs.
- Do not weaken a test, security guard, transaction boundary, production/test isolation, or protocol contract to make a defect disappear.

## Phase 1 — Find and log

Perform complete deliberate passes over the relevant repository. A full pass systematically covers runtime entrypoints and modular/legacy boundary, request parsing/dispatch, authentication/admission, per-connection ordering, Game/outcome lifecycle, persistence/transactions, database/schema/migrations, caches, consolidation, files/recovery, background jobs, operational launch/shutdown, and tests/fixtures that expose contract mismatches.

For each suspected defect:

1. trace the reachable execution path through callers/callees/state/data;
2. check guards, alternate paths, retry/failure behavior, concurrency, cleanup and ownership;
3. check relevant invariants and client/database contracts;
4. determine concrete conditions that cause incorrect behavior;
5. log only defects supported by code evidence.

Continue full passes until **two consecutive complete passes find zero new validated defects**. A pass that finds any new bug resets the clean-pass count.

## Phase 2 — Fix every ledger entry

For each current bug:

1. reconfirm it against current source;
2. identify root cause and affected contracts, not just the visible symptom;
3. perform mandatory impact analysis, including transaction/ownership/concurrency/security/schema/protocol implications as relevant;
4. implement the smallest robust correction;
5. classify affected tests and add/update focused regression coverage that would have caught the original defect whenever practical;
6. statically review the fix, tests, caller/callee paths, failures, retries, cleanup, exact numeric behavior, and cross-layer effects;
7. update durable repository knowledge and `docs/engineering/REGRESSIONS.md` when the lesson is reusable;
8. remove the bug from `BUG_LEDGER.md` only after source evidence shows it is fixed or disproved.

If automation is genuinely impractical, document the reason and strongest repeatable protection in the permanent regression ledger.

Fix every valid ledger entry. A low-severity or inconvenient confirmed defect does not remain merely because another subsystem was the original focus.

## Phase 3 — Repeat

After production fixes, reset the clean-pass count and return to Phase 1. Fixes can introduce or expose defects, so pre-fix clean passes never carry forward.

Stop only when:

- `BUG_LEDGER.md` is empty;
- all production fixes from the cycle are complete;
- required regression tests are present and statically reviewed;
- durable learning/regression protection is reconciled;
- two consecutive complete post-fix static passes find no new validated defects.

Do not claim mathematical bug-freedom. Report that the static workflow reached an empty ledger plus two clean passes, and state explicitly that tests/live integration were not executed unless separately requested.

## Ledger format

Use one root `BUG_LEDGER.md` with current unresolved findings only:

### BUG-001 — Short issue name
**Location:** `path`, function/class/area  
**Description:** Concrete incorrect behavior, causal path, and triggering conditions.

Keep IDs stable while entries remain. Remove resolved/disproved entries; Git history and permanent regression records preserve history.

## High-risk review areas

Pay particular attention to:

- connection-scoped request hashes and exactly-once release;
- state barrier versus intentionally concurrent request ordering;
- authentication single-flight and identity binding;
- WebSocket disconnect/reconnect/Game ownership;
- request-local shooting identity versus shared mutable Game state;
- MySQL pool/transaction connection identity and rollback paths;
- 64-bit integer/hash precision across JS/SQL/JSON;
- reservation commit/discard/retry/stale-batch ownership;
- StoreCommands acknowledgement ordering;
- consolidation versus in-flight writers and cache invalidation;
- cross-realm Map/object behavior through `node:vm`;
- cache-file async errors, snapshot requeue, backpressure and memory bounds;
- user-data mutation ordering and settings/version exact lookup;
- production/test database isolation and migration gates;
- queue algorithms under bursts, background lifecycle jobs, timers, shutdown and cleanup;
- stale tests/mocks that no longer traverse current modular runtime behavior.

## Git checkpoints

Keep all audit/fix/test/memory changes on the dedicated branch. Commit accumulated work whenever any of these occurs:

1. 10 newly validated bugs since the previous commit;
2. finding -> fixing transition;
3. 10 bugs resolved/disproved since the previous commit;
4. fixing -> finding transition.

One coherent checkpoint may satisfy coincident triggers; do not create empty commits. Include related tests, ledger changes, and repository-learning updates in the same checkpoint. Never merge into `main` as part of this skill unless explicitly requested.