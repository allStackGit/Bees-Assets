---
name: bug-finding
description: Perform a repository-wide static bug audit by tracing code paths and recording only validated defects in a single ledger. Use when asked to find, audit, scan, or catalogue bugs without running tests or GitHub Actions.
---

# Bug Finding

Perform a static, repository-wide bug audit. Find, validate, and record real defects without modifying production code.

## Setup

1. Read `AGENTS.md` and applicable repository instructions if present.
2. Read existing repository memory relevant to architecture, pitfalls, and known bugs.
3. Fetch the latest `main`.
4. Create a new audit branch from current `main`. Use a descriptive `bug-audit/...` branch name and avoid overwriting an existing branch.
5. Create or locate `BUG_LEDGER.md` at the repository root. Use that single file for all findings from this audit.
6. If an existing ledger contains entries without a `Status` field, add one. Use `Open` for a still-valid unresolved bug unless repository evidence establishes another status; never guess that a bug is fixed or invalid.

Do not modify production code.

## Restrictions

- Do not run tests.
- Do not run qualification suites, simulations, benchmarks, builds, or executables.
- Do not trigger, rerun, or rely on GitHub Actions.
- Do not make speculative bug reports.
- Do not record style issues, refactoring opportunities, theoretical risks, or unusual-looking code unless they produce a concrete incorrect behavior.
- Do not stop merely because several bugs have been found.

This is a static-analysis and code-tracing task.

## Audit Process

Explore the entire codebase systematically.

Work through major subsystems, entry points, state transitions, data flows, and important call chains rather than inspecting files in isolation.

For each potential defect:

1. Identify the suspected incorrect behavior.
2. Trace the relevant execution path through callers, callees, state, configuration, and data transformations.
3. Check surrounding code for protections, invariants, alternate paths, or assumptions that would make the suspected issue harmless.
4. Search references and usages where necessary to determine whether the problematic path is actually reachable.
5. Determine the concrete conditions under which the defect occurs.
6. Record it only when the code provides sufficient evidence that an incorrect result or behavior can occur.

A bug is validated when the defect can be demonstrated by reasoning from the code path itself. Runtime reproduction is not required, but speculation is insufficient.

If evidence is uncertain, continue investigating rather than logging the issue.

## Bug Ledger

Maintain one `BUG_LEDGER.md` containing all validated findings.

Each bug must use this format:

### BUG-001 — Short Issue Name
**Status:** Open  
**Location:** `path/to/file.ext`, class/function/method or relevant lines  
**Description:** Concise explanation of the incorrect behavior, the code path that causes it, and the conditions under which it occurs.

Every bug entry must contain exactly one `Status` field. Use these statuses:

- `Open` — validated and unresolved.
- `In Progress` — a fix is actively being worked on.
- `Fixed` — the corresponding defect has been corrected.
- `Invalid` — later investigation disproved the original finding.
- `Deferred` — still valid but intentionally postponed.

Newly validated findings must start as `Open`. Do not delete fixed, invalid, or deferred entries; preserve the audit history by changing their status. Do not mark a bug `Fixed` or `Invalid` without repository evidence supporting that status.

Continue sequential numbering for the entire audit.

Keep descriptions short but specific enough that another developer can understand and reproduce the reasoning without rediscovering the bug.

Do not duplicate an existing ledger entry. If a newly investigated path is another manifestation of the same underlying defect, update the existing entry only when useful.

## Coverage Discipline

Maintain private working notes or a temporary checklist of areas already examined so the audit progresses through the repository rather than repeatedly revisiting familiar code.

Prioritize:

- core execution paths
- state machines and lifecycle transitions
- persistence and serialization
- configuration handling
- boundary conditions
- error and recovery paths
- ownership and cleanup
- concurrency or ordering assumptions
- indexing, ranges, counts, and off-by-one behavior
- null, missing, and invalid state handling
- cross-system contracts
- code whose callers make assumptions not enforced by the callee

After completing the initial repository pass, perform additional passes focused on cross-system interactions and assumptions that may not be visible when reviewing individual components.

## Stop Condition

Do not stop after finding a particular number of bugs.

Continue until:

1. the codebase has been systematically covered,
2. important execution paths and subsystem interactions have been traced,
3. promising suspicious paths have either been validated and logged or disproven, and
4. another deliberate search pass fails to produce a new validated defect.

The goal is not to claim that the repository is bug-free. Continue until static analysis no longer reveals additional validated bugs.

## Git Discipline

All audit records belong on the dedicated audit branch.

Commit ledger updates periodically so findings are not lost. Keep commits limited to audit documentation and repository memory required by the audit.

Never merge the audit branch into `main` as part of this skill.
