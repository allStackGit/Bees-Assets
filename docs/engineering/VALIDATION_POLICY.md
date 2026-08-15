# Bees Validation and Test-Health Policy

This file supplements the concrete commands and current suite inventory in `docs/TESTING.md`. It defines how tests must evolve with the code.

## Tests are requirements, not fossils

A test is valuable only while it exercises a real production contract. Existing tests must not be preserved blindly, but they also must not be deleted or weakened merely because implementation changed.

For every behavior-affecting change, classify affected tests:

- **Still valid** — no semantic change needed.
- **Update required** — the requirement remains, but setup/assertions/fixtures are stale.
- **Obsolete and replace** — the old behavior was deliberately retired; preserve any still-valid underlying requirement with replacement coverage.
- **Missing** — add focused coverage for a contract or regression that was previously unprotected.

When a test is stale, identify why it is stale. A stale test often reveals a stale fixture, mock, reflection adapter, serialized assumption, or architecture note as well.

## Regression rule

For every reproducible regression that is fixed, add a focused test that would have failed before the fix whenever practical. Prefer the smallest test that protects the enduring contract rather than reproducing implementation details.

If automation is genuinely impractical (for example, a visual/UI/hardware-only issue that cannot be made deterministic), record the regression in `docs/engineering/REGRESSIONS.md` with:

- root cause;
- why automated protection is impractical;
- the strongest manual/system test that should catch recurrence;
- any invariant/documentation change that reduces recurrence risk.

## Widening validation

Do not stop at the first passing reproducer. Use the strongest applicable sequence allowed by the environment/task:

1. focused reproducer or changed test;
2. related EditMode/PlayMode category;
3. broader correctness/foundation suite;
4. full local release gate;
5. representative scene/play test, campaign scenario, soak, performance qualification, or hardware validation when the impact warrants it.

Examples that normally need broader-than-unit validation include:

- scene/prefab/Resources/map normalization changes;
- pool/reuse/teardown changes;
- async pathfinding or threading ownership;
- physics/range/visibility behavior;
- user-data/persistence/network/reconnect changes;
- campaign setup/objective/terminal logic;
- UI behavior that depends on Unity frames or serialized wiring;
- performance work that could shift stalls, memory, GC, or frame timing.

A static-only skill may intentionally not execute tests. It must still create/update the appropriate coverage and explicitly report that execution remains pending.

## Test-health audits

Use `.agents/skills/test-health/SKILL.md` periodically and whenever a cluster of stale/flaky/misleading tests is discovered.

A test-health audit should look for:

- tests that assert obsolete implementation details instead of requirements;
- tests whose setup no longer reaches the production path they claim to cover;
- mocks/reflection adapters/fixtures that bypass newly important production behavior;
- tests that can pass without the intended assertion path executing;
- order dependence or leaked static/Unity state;
- timing/flakiness and overly broad timeouts hiding deadlocks/hangs;
- duplicate tests with diverging expectations;
- slow tests that belong in an opt-in category rather than the fast correctness loop;
- regressions fixed in Git history/durable memory with no permanent test;
- production contracts covered only by source-text assertions when a direct behavioral test is feasible;
- tests tied to old mission IDs, prefab names, serialized paths, configuration versions, or test harness architecture.

Test health and code health are separate questions. A green suite can be stale; a failing suite can reveal a real code regression or a stale test. Diagnose which before editing either side.

## System-level invariant checks

Unit tests are not enough for every contract. Maintain and extend higher-level checks such as:

- loading representative/all supported maps and validating map-prefab coverage;
- instantiating real prefabs/pools and cycling object lifetimes;
- isolated scene bootstrap and teardown;
- campaign objective/terminal-path scenarios;
- real background path-worker overlap/cancellation;
- persistence malformed-write/ownership cases;
- reconnect/request-deduplication ownership;
- representative combat/mass-death/lifecycle stress;
- long soak and named-hardware performance qualification when release confidence requires it.

## Repository-governance checks

`Tests/EditMode/EngineeringGuardrailTests.cs` uses category `BeesEngineeringGuardrails`. Run that category whenever the engineering-policy/skill/regression files change, and include it in any future unfiltered full-suite/release workflow.

It is intentionally separate from `BeesFoundation` so adding the governance checks does not silently invalidate a previously recorded foundation test count before Unity has been rerun. Numeric pass counts in `docs/TESTING.md` are last-run snapshots, not proof for a changed branch.

## Validation evidence

- Treat Unity Test Framework XML plus exact executed-test counts as authoritative command-line evidence, per `docs/TESTING.md`.
- Do not carry a “current validated result” forward after source/test/configuration changes without rerunning it.
- Record the exact suite/category/configuration used when reporting validation.
- A test that was not run must be reported as not run; do not infer success from static review.

## Editing tests safely

Before modifying a failing test, answer:

1. What enduring requirement was this test intended to protect?
2. Does current production behavior still need that requirement?
3. Is the test reaching the current production path?
4. Did fixtures/mocks/serialized assets/configuration change underneath it?
5. Will the proposed replacement fail if the original regression returns?

If those questions cannot be answered, investigate before changing the assertion.