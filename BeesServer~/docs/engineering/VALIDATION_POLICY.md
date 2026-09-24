# BeesServer Validation and Test-Health Policy

Tests are maintained specifications, not historical artifacts. Production code changes and test-suite changes must be reviewed together.

## Canonical validation levels

Use the narrowest useful level first, then widen according to risk.

1. **Focused reproducer** — one `node:test` file or minimal deterministic contract test.
2. **Affected subsystem/integration set** — related module and integration tests.
3. **Full server qualification** — `npm test`, which prepares/migrates `bees_test`, syntax-checks critical runtime files, starts the temporary real server, runs the complete Node suite including live MySQL/WebSocket/schema tests, then tears down.
4. **Cross-layer Bees validation** — required when a server change alters a Unity-facing protocol, serialized identity, settings/data version, reconnect contract, or another client-consumed behavior.
5. **Production operational verification** — only for an explicitly requested deployment/production operation; never substitute production `ram` for automated test data.

A green focused test establishes local plausibility, not repository-wide safety.

## Test database safety

- Automated tests use only `bees_test`.
- Do not repoint `testServerConfig.js`, environment variables, or test helpers at `ram`.
- Do not run `npm run migrate:production` or set its production opt-in merely to validate code.
- The reserved integration user and disposable test database exist to make destructive/integration checks safe; preserve those guardrails.
- If a test unexpectedly attempts production access, treat that as a test/runtime defect rather than bypassing the guard.

## Mandatory change-time test classification

For every behavior-affecting change, classify relevant tests as:

- **Still valid** — contract remains correct and coverage still exercises it.
- **Must be updated** — intended contract remains, but fixture/setup/assertion/API shape is stale.
- **Obsolete and must be replaced** — old behavior was deliberately retired; identify and preserve any underlying enduring requirement.
- **Missing** — add focused coverage for a newly exposed contract or regression.

Do not fix a stale test by weakening the underlying safety rule. Do not fix production merely to satisfy a test that encodes a retired implementation detail. Determine which contract is authoritative first.

## Regression rule

For a reproducible defect, the fix is incomplete until a focused automated test exists that would have caught the original failure, unless automation is genuinely impractical. Tests should assert the durable contract/root cause rather than copy the implementation.

If automation is impractical, record in `docs/engineering/REGRESSIONS.md`:

- why an automated reproducer is impractical;
- the strongest repeatable live/manual/cross-layer verification available;
- the invariant or operational protection that prevents recurrence.

## Test-health audits

Periodically run `.agents/skills/test-health/SKILL.md`. Look specifically for:

- tests asserting obsolete legacy implementation instead of current modular runtime contracts;
- source-text tests that no longer exercise runtime behavior when a direct behavioral test is practical;
- mocks/fakes that bypass the failure/concurrency path they claim to cover;
- tests that silently skip live integration because required environment gates changed;
- fixtures that no longer match Unity payloads, schema, settings versions, or authentication semantics;
- tests that pass regardless of the production implementation;
- flaky timing/port/process tests without deterministic ownership/cleanup;
- concurrency tests that serialize the code and therefore fail to exercise races;
- database tests that do not verify transaction connection identity, rollback, exact bigint representation, or failure propagation where those are the contract;
- stale counts/status text or historical validation claims being mistaken for current evidence;
- old fixed-regression coverage that has lost the assertion which originally protected the bug.

Deleting a stale test is not enough. Identify the requirement it represented and preserve it with replacement coverage if that requirement still exists.

## System-level validation triggers

Prefer full `npm test` rather than only focused tests for changes involving:

- `server.js`, VM transforms, startup, queues, authentication/admission, connection lifecycle;
- MySQL configuration, migrations, transactions, user/settings/outcome persistence;
- live WebSocket request/response handling or reconnect behavior;
- request ordering/concurrency, outcome reservation ownership, consolidation/writer coordination;
- cache persistence/recovery or filesystem failure handling;
- changes to test environment, `run-tests.js`, `testServerConfig.js`, or live-test gates.

For a Unity-facing wire contract change, also require client-side validation on the corresponding Bees-Assets branch/worktree when available.

## Evidence discipline

A validation claim must identify what was actually run on the current source. Old passing runs, historical `docs/TEST_DEFECTS.md` status, or pre-change test results are useful history but are not proof for a changed branch.

If execution is unavailable or prohibited by the active skill, report validation as unexecuted and perform the strongest static review available. Never convert “not run” into “passed.”