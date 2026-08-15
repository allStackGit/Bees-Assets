---
name: test-health
description: Audit and repair test-suite health without weakening requirements: detect stale, misleading, flaky, bypassed, duplicate, or missing regression coverage; update/replace tests against current production contracts; preserve permanent protection.
---

# Test Health

Use this skill when tests appear stale, contradictory, flaky, excessively slow, misleading, or incomplete, and periodically as a maintenance audit. It may also be invoked as a focused phase inside ordinary development when a change exposes test-suite drift.

## Setup

1. Read `AGENTS.md`, `.agents/skills/repo-learning/SKILL.md`, `docs/TESTING.md`, and `docs/engineering/VALIDATION_POLICY.md`.
2. Load the relevant system map/invariants/regression history before judging a test stale.
3. Respect the user's requested branch. If this is a standalone audit and no branch is specified, work on a dedicated `test-health/...` branch rather than `main`.
4. Do not assume a failing test is stale and do not assume a passing test is healthy.

## Audit questions

For each affected suite/subsystem, determine:

- What enduring requirement is the test supposed to protect?
- Does its setup still reach the current production path?
- Are mocks, reflection adapters, fixtures, serialized assets, IDs, paths, configuration, or timing assumptions current?
- Can the test pass without exercising the behavior named by the test?
- Does it assert implementation shape where a behavioral contract would be safer?
- Is it order-dependent or leaking static/Unity state?
- Is a timeout masking a deadlock/hang rather than detecting it?
- Is there duplicated coverage with divergent expectations?
- Is a slow test in the correct opt-in/release category?
- Does a previously fixed regression lack a test that would catch recurrence?
- Is a high-risk production path represented only by source-text checks when direct behavioral coverage is feasible?

## Classification and repair

Classify every test touched by the audit:

1. **Still valid** — keep it; improve clarity/isolation only if useful.
2. **Update required** — preserve the requirement but repair stale setup/assertions/fixtures.
3. **Obsolete and replace** — retire the old behavior intentionally and add replacement coverage for any enduring requirement.
4. **Missing** — add focused coverage.

Never delete or loosen a test solely to make the suite green. Establish the intended production contract first.

Prefer tests that fail for the actual defect/contract violation and pass for multiple valid implementations. Avoid tests that merely search for one newly added source string when a behavioral test is practical.

## Regression protection

For every confirmed regression encountered:

- ensure a focused regression test would have failed before the fix whenever practical;
- update `docs/engineering/REGRESSIONS.md` when there is a reusable root-cause lesson;
- promote general ownership/lifecycle rules into `docs/engineering/INVARIANTS.md` or durable memory;
- if automation is impractical, document a repeatable manual/system protection and the reason.

## Validation

When execution is available, run progressively broader validation after test-health changes:

`changed test -> related category -> broader foundation/correctness suite -> release gate or representative PlayMode/system check when warranted`

Treat exact Unity XML results and executed-test counts as evidence. Do not infer success from compilation, logs, or old result files.

If the environment cannot run Unity, statically review the changed tests and report them as unexecuted rather than claiming they pass.

## Completion

Finish only when:

- stale tests in scope have been updated/replaced or deliberately retired with their requirements accounted for;
- no failure was hidden through weaker assertions, broad skips, larger timeouts without cause, or bypassed production paths;
- missing regression coverage found in scope has been added where practical;
- test documentation/durable knowledge discovered stale during the audit has been reconciled;
- validation status and remaining manual-only gaps are explicit.

The goal is not maximum test count. The goal is a suite whose failures and passes still mean something about the current product.