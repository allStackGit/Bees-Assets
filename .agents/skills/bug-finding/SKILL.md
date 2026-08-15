---
name: bug-finding
description: Repository-wide static bug audit and repair loop with mandatory repository learning, impact analysis, test-contract review, permanent regression protection, two consecutive clean full-code passes, and checkpoint commits. Do not execute tests/builds/actions unless separately requested.
---

# Bug Finding

Perform a repository-wide static find/log/fix/review loop until the active bug ledger is empty and two deliberate full-code passes over the post-fix tree find no new validated defects.

## Setup

1. Read and follow `AGENTS.md` in full.
2. Fetch the latest `main` and create a new unique `bug-audit/...` branch before changing code, tests, ledgers, or repository memory. Never reuse an old audit branch unless the user explicitly requests it.
3. Invoke `.agents/skills/repo-learning/SKILL.md` for the entire workflow. Its impact analysis, test classification, regression protection, and knowledge-reconciliation rules are mandatory.
4. Read `docs/engineering/VALIDATION_POLICY.md`, relevant invariants/system memory, and `docs/engineering/REGRESSIONS.md` before judging behavior/tests.
5. Reconcile `BUG_LEDGER.md` against the current tree. Remove invalid/already-fixed/obsolete entries; retain every current validated defect.

## Execution restriction

This skill is static unless the user separately requests runtime validation.

- Do not run tests, Unity, builds, simulations, benchmarks, qualification, executables, or GitHub Actions as part of this skill alone.
- You may and should add/update tests required to protect fixes; review them statically and report that they were not executed.
- Runtime inability never excuses missing regression coverage or stale-test review.

## Phase 1 — Find and log

Perform complete repository passes, not focused scans. Systematically cover major subsystems, entry points, state/lifecycle transitions, persistence/serialization, configuration, assets/serialized contracts, async/concurrency/ordering, boundary/error paths, and cross-system assumptions.

For each suspected defect:

1. trace the reachable execution/data/state path through callers and callees;
2. check guards/invariants/alternate paths that could make it harmless;
3. identify concrete conditions producing incorrect behavior;
4. check relevant tests/fixtures and whether they still exercise the production contract;
5. log only defects supported by repository evidence, not style concerns or theoretical risk.

Continue until **two separate consecutive complete passes find zero new validated defects**. Any new defect resets the clean-pass count.

## Phase 2 — Fix every valid ledger entry

For each bug:

1. Reconfirm it on the current tree.
2. Perform the pre-change impact analysis required by `AGENTS.md`/repo-learning.
3. Identify the enduring requirement and classify affected tests: still valid, update-required, obsolete-and-replaced, or missing.
4. Implement the smallest robust fix for the underlying cause, not merely the visible symptom.
5. **For every reproducible regression, add a focused automated test that would have failed before the fix whenever practical.** The older standard of adding tests only “when useful” is superseded by this permanent-protection requirement.
6. If automation is genuinely impractical, document why and the strongest repeatable manual/system protection in `docs/engineering/REGRESSIONS.md`.
7. Statically review the production change, test changes, callers/callees, lifecycle/state flow, and neighboring contracts for regressions.
8. Update durable repository knowledge/invariants when the root cause reveals a reusable rule.
9. Remove the bug from `BUG_LEDGER.md` only when repository evidence shows it is fixed/disproved and its permanent protection is accounted for.

Do not weaken/delete a failing test to accommodate the fix without first proving the requirement itself intentionally changed and replacing any enduring protection.

If a fix reveals another validated defect, add it to the active ledger.

## Phase 3 — Re-audit after fixes

Production changes invalidate prior clean passes. After resolving the ledger, reset the clean count and repeat complete finding passes over the modified tree. Fixes can expose or introduce reachable defects elsewhere.

## Active bug ledger

`BUG_LEDGER.md` is a current work queue, not permanent history.

Use:

```markdown
### BUG-001 — Short issue name
**Location:** file/class/function or relevant lines  
**Description:** concrete incorrect behavior, code path, and triggering conditions
```

Keep IDs stable while active. Remove entries once fixed, disproved, obsolete, or unreachable.

Permanent lessons from fixed regressions belong in `docs/engineering/REGRESSIONS.md`, not in the active ledger.

## Required review angles

Across passes pay particular attention to:

- lifecycle/reset/pooling/cleanup ownership;
- stale references and duplicated callbacks;
- async/concurrency/order/cancellation;
- persistence/serialization/versioning;
- configuration and serialized asset/name contracts;
- boundary/index/null/missing-state/error paths;
- cross-system and caller/callee assumption mismatches;
- tests/mocks/fixtures that no longer reach production behavior;
- previously fixed regression classes that lack protection;
- interactions introduced by fixes from the prior cycle.

The second clean pass must challenge the first rather than repeat the same mechanical order.

## Final stop condition

Stop only when all are true:

1. `BUG_LEDGER.md` has no valid unresolved entries.
2. All production fixes from the cycle are complete.
3. Affected tests were classified; stale/missing coverage was repaired/replaced.
4. Reproducible fixed regressions have permanent protection where practical and permanent records/invariants are updated when warranted.
5. Repository memory was reconciled; stale facts discovered during the audit were corrected or marked.
6. Two consecutive complete post-fix passes found no new validated defects.
7. Runtime validation not executed because of this skill's static restriction is stated explicitly.

Do not claim mathematical bug-freedom; report exactly what the static audit established.

## Git checkpoint discipline

Commit accumulated audit changes whenever any trigger occurs:

1. after the 10th newly validated bug since the prior checkpoint;
2. immediately before finding -> fixing transition;
3. after the 10th bug fixed/disproved since the prior checkpoint;
4. immediately before fixing -> finding transition.

Include ledger, production, test, permanent-regression, and repository-learning changes produced by that work. Reset found/fixed counters after each checkpoint. Do not create empty commits. Never merge into `main` unless the user explicitly requests it.