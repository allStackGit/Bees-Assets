---
name: test-health
description: Audit and repair BeesServer test-suite health: find stale, misleading, flaky, bypassed, over-mocked, obsolete, or missing tests and reconcile them with current runtime, database, protocol, security, and persistence contracts.
---

# Test Health

Use this skill when the test suite itself needs review or whenever repeated stale-test failures suggest coverage no longer tracks the production contract.

## Setup

1. Read `AGENTS.md` and follow `.agents/skills/repo-learning/SKILL.md` throughout.
2. Read `docs/engineering/VALIDATION_POLICY.md`, `docs/DEVELOPMENT_MEMORY.md`, `docs/DATABASE_MODEL.md`, `docs/LIVE_INTEGRATION_TESTING.md`, and relevant invariants/regression history.
3. Establish current production entrypoints and actual `npm test` orchestration before judging tests.
4. Do not assume an old test is correct merely because it predates the implementation change, and do not assume implementation is correct merely because a test is inconvenient.

## Audit targets

Review tests and harnesses for:

- assertions tied to retired legacy implementation details rather than maintained behavior;
- source-text/string tests where a direct behavioral contract test is practical and safer;
- mocks/fakes that bypass the production path, error propagation, transaction, concurrency, or cleanup they claim to verify;
- tests that accidentally serialize concurrency and therefore cannot reproduce races;
- timeouts/sleeps/port/process assumptions that create nondeterministic failures;
- fixtures that no longer match Unity request/response payloads, versioned settings, schema, IDs, authentication, reconnect, or outcome semantics;
- database tests that fail to prove one-connection transactions, rollback, exact bigint identity, or test database selection when those are the contract;
- live tests that silently skip because environment variables, names, gates, or runner orchestration changed;
- tests whose assertions can pass even when the production call is removed/broken;
- duplicate tests with diverging expectations;
- tests that make unsafe production assumptions or could target `ram`;
- missing regression tests for defects recorded in current history/ledger;
- stale documentation claiming coverage that the current suite no longer provides.

## Classify before changing

Every suspect test must be classified as one of:

1. **Valid** — current contract and meaningful execution path.
2. **Stale setup/assertion** — intended contract remains; repair the test.
3. **Obsolete implementation contract** — replace it with coverage for the enduring requirement, or remove only if the requirement truly no longer exists.
4. **Misleading/ineffective** — rewrite so it would fail on the defect it claims to catch.
5. **Flaky** — remove nondeterminism without weakening the assertion.
6. **Missing coverage** — add a focused test for an uncovered important contract.

For any production behavior changed during test-health repair, apply the normal impact analysis and regression rules; this skill is not permission for incidental broad refactoring.

## Validation

When execution is available, validate repaired tests at increasing scope:

`focused node --test -> related test set -> npm test`

Use only `bees_test` for database/live validation. A client protocol test may also require Bees-Assets validation.

When execution is unavailable, statically review syntax/imports/fixtures and report the exact validation that remains unexecuted.

## Durable outcome

Update `docs/engineering/VALIDATION_POLICY.md` or detailed repository memory only for reusable test-system lessons. Do not maintain a chronological test-audit diary.

If a stale or ineffective test allowed a real regression to escape, record the root cause and replacement protection in `docs/engineering/REGRESSIONS.md`.

The goal is not maximum test count. The goal is a suite whose failures and passes carry accurate information about the current production contract.