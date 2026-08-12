---
name: bug-finding
description: Perform a repository-wide static bug audit and repair loop: find and log validated defects, require two consecutive clean full-code passes, fix every ledgered bug, then repeat until the ledger is empty and two clean passes find nothing. Do not run tests or GitHub Actions unless explicitly requested separately.
---

# Bug Finding

Perform a repository-wide static bug audit and repair loop. Find, validate, record, and then fix real defects until repeated review no longer reveals any.

## Setup

1. Read `AGENTS.md` and applicable repository instructions if present.
2. Read existing repository memory relevant to architecture, pitfalls, and known bugs.
3. Fetch the latest `main`.
4. Create a new working branch from current `main`. Use a descriptive `bug-audit/...` branch name and avoid overwriting an existing branch.
5. Create or locate `BUG_LEDGER.md` at the repository root. Use that single file for all current findings.
6. Reconcile the existing ledger against the working tree before beginning:
   - remove entries that repository evidence shows are already fixed, invalid, obsolete, or unreachable;
   - retain every still-valid unresolved defect;
   - ensure retained entries follow the ledger format below.

## Restrictions

- Do not run tests.
- Do not run qualification suites, simulations, benchmarks, builds, or executables.
- Do not trigger, rerun, or rely on GitHub Actions.
- Do not log speculative defects.
- Do not log style issues, refactoring opportunities, theoretical risks, or unusual-looking code unless they cause concrete incorrect behavior.
- Do not stop after finding or fixing a particular number of bugs.
- Do not treat a partial review as a full pass.

This workflow relies on static analysis, code tracing, and careful review of the resulting fixes.

## Core Loop

Repeat the following cycle until the final stop condition is satisfied.

### Phase 1 — Find and Log Bugs

Perform full repository passes looking for validated defects.

A full pass means systematically covering the entire relevant codebase, including major subsystems, entry points, state transitions, data flows, important call chains, boundary behavior, and cross-system assumptions. Do not count a focused subsystem review or continuation of an incomplete pass as a full pass.

For each potential defect:

1. Identify the suspected incorrect behavior.
2. Trace the execution path through callers, callees, state, configuration, and data transformations.
3. Check for guards, invariants, alternate paths, or assumptions that make the suspected issue harmless.
4. Search references and usages to establish reachability where needed.
5. Determine the concrete conditions under which incorrect behavior occurs.
6. Log it only when the code itself provides sufficient evidence that the defect is real.

A bug is validated when incorrect behavior can be demonstrated by reasoning from the code path. Runtime reproduction is not required, but suspicion is insufficient.

Continue performing full passes until **two consecutive full passes find zero new validated defects**.

- If a pass finds one or more new bugs, log all of them, finish that full pass, reset the clean-pass count to zero, and begin another full pass.
- A clean pass counts only if the entire relevant codebase was reviewed and no new validated defects were found anywhere in that pass.
- The two clean passes must be separate deliberate passes, not one pass described twice.

Only after two consecutive clean full passes may the workflow move to Phase 2.

### Phase 2 — Fix Every Bug in the Ledger

Process every currently valid bug in `BUG_LEDGER.md`.

For each bug:

1. Reconfirm that the defect still exists in the current working tree.
2. Trace enough surrounding behavior to understand the intended contract and avoid a narrow fix that breaks another path.
3. Implement the smallest robust correction that resolves the underlying defect.
4. Statically review the change, its callers/callees, and affected state/data flows for regressions or incomplete handling.
5. Once repository evidence shows the defect is resolved, remove its ledger entry. If investigation disproves the entry instead, remove it as invalid.

Do not leave a validated bug unfixed merely because it is inconvenient, low severity, or outside the subsystem currently being inspected. Phase 2 is complete only when every ledger entry from that cycle has been resolved or disproved.

If fixing one bug reveals another validated defect, add the new defect to the ledger and continue resolving the existing ledger. The next audit cycle will independently re-examine the full codebase.

### Phase 3 — Repeat

After Phase 2, return to Phase 1 and perform fresh full-code audit passes against the modified codebase.

Fixes may expose, introduce, or make other defects reachable, so previous clean passes do not carry forward across a repair phase. The clean-pass count always resets to zero after production code changes.

Repeat Phases 1 and 2 for as many cycles as necessary.

## Bug Ledger

Maintain one `BUG_LEDGER.md` containing only currently valid unresolved findings.

Each bug must use this format:

### BUG-001 — Short Issue Name
**Location:** `path/to/file.ext`, class/function/method or relevant lines  
**Description:** Concise explanation of the incorrect behavior, the code path that causes it, and the conditions under which it occurs.

New findings should use the next unused sequential identifier. Keep identifiers stable while entries remain in the ledger. If the ledger becomes completely empty, a later cycle may restart at `BUG-001`.

The ledger is a current work queue, not permanent history. Remove entries once they are fixed, disproved, obsolete, or no longer reachable. Git history preserves prior findings.

Do not duplicate an existing entry. Multiple manifestations of one underlying defect should normally remain one bug entry.

## Coverage Discipline

Maintain private working notes or a temporary checklist of areas covered during each pass so a pass genuinely progresses through the entire codebase.

Pay particular attention to:

- core execution paths
- state machines and lifecycle transitions
- persistence and serialization
- configuration handling
- boundary conditions
- error and recovery paths
- ownership and cleanup
- concurrency and ordering assumptions
- indexing, ranges, counts, and off-by-one behavior
- null, missing, and invalid state handling
- cross-system contracts
- caller/callee assumption mismatches
- interactions changed by fixes made in the previous cycle

Use different review angles across passes where useful. The second clean pass should challenge the conclusions of the first rather than mechanically repeating the same inspection order.

## Final Stop Condition

Stop only when all of the following are simultaneously true:

1. `BUG_LEDGER.md` contains no unresolved bugs.
2. No production-code fixes remain to be made from the previous cycle.
3. Two consecutive, complete, deliberate full-code passes over the current post-fix codebase found zero new validated defects.

If either clean pass finds a defect, log it, reset the clean-pass count, complete the audit phase, fix the resulting ledger, and repeat the cycle.

Do not claim the repository is mathematically bug-free. The completion claim is only that repeated static analysis reached an empty ledger and two consecutive clean full-code passes.

## Git Discipline

Keep all audit records and fixes on the dedicated working branch unless the user explicitly instructs otherwise.

Commit progress periodically so findings and fixes are not lost. Keep commits coherent and limited to the audit/repair work and repository memory required by it.

Never merge the working branch into `main` as part of this skill unless the user explicitly requests the merge.
