# AGENTS.md

Mandatory engineering rules for every coding agent and every development task in this repository. These rules apply whether or not the user names a skill.

## Authority and required reading

1. Follow the user's explicit request and branch target.
2. Read `PROJECT_CONSTITUTION.md` before changing behavior.
3. Read `docs/DEVELOPMENT_MEMORY.md`, `docs/engineering/INVARIANTS.md`, `docs/engineering/SYSTEM_MAP.md`, `docs/engineering/VALIDATION_POLICY.md`, and the relevant parts of `docs/engineering/REGRESSIONS.md`.
4. Read `docs/TESTING.md` for the current Unity test topology and commands.
5. Read and follow `.agents/skills/repo-learning/SKILL.md` for the entire task. Named skills do not replace repository learning.
6. Treat maintained documentation as a source-grounded model, not unquestioned truth. Revalidate facts that matter against current code/assets/tests and repair stale documentation when found.

If implementation conflicts with the project constitution or a confirmed invariant, implementation is presumed defective unless the project owner explicitly changes the requirement. Existing tests are evidence, not authority: a stale test must be repaired rather than used to redefine intended behavior.

## Branch discipline

Respect a branch explicitly named by the user. Otherwise, do not make ordinary development changes directly on `main`; create a descriptive task branch from the latest appropriate base. Specialized skills may impose stricter branch/commit rules and those remain in force.

## Mandatory pre-change impact analysis

Before editing production code, tests, serialized assets, scenes, prefabs, configuration, or persistence data, determine the likely impact surface. Inspect the relevant callers/callees and explicitly consider, as applicable:

- user-visible gameplay and campaign behavior;
- scene/Stage/Level/GameState lifecycle and cleanup;
- pooling, object reuse, timers, deferred release, and stale state;
- async pathfinding/work ownership, ordering, cancellation, and lifecycle tokens;
- physics/collision callbacks and Unity frame/lifecycle ordering;
- map, obstacle, prefab, resource, scene, and serialized-name contracts;
- local/server persistence, save compatibility, request/response and deduplication contracts;
- replay/determinism and stable ordering;
- UI/dialogue/input ownership;
- performance/resource behavior and low-end hardware constraints;
- tests, fixtures, mocks/reflection adapters, manual play tests, and release-gate coverage that encode the affected contract.

A narrow symptom fix is not complete until surrounding assumptions and indirect dependencies have been checked.

## Test contract review is part of every change

For every behavior-affecting change, classify affected tests before completion:

1. **Still valid** — continues to test the intended requirement.
2. **Must be updated** — requirement remains but setup/assertions are stale.
3. **Obsolete and must be replaced** — old behavior is intentionally removed; preserve the underlying requirement with replacement coverage when one still exists.
4. **Missing** — add focused coverage for the newly exposed requirement/regression.

Do not delete, skip, weaken, loosen, or rewrite a failing test merely to make a change pass. Determine first whether production code or the test contract is wrong. When a stale test is removed, identify what requirement it represented and either preserve that requirement elsewhere or document why the requirement itself was deliberately retired.

For a reproducible bug/regression, the fix is incomplete until a focused automated regression test exists that would have caught the defect, unless automation is genuinely impractical. In that exception, record the reason and the strongest practical manual/system-level protection in `docs/engineering/REGRESSIONS.md`.

## Validation ladder

When execution is available and permitted by the active skill, widen validation rather than stopping at the first green test:

`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play test/system qualification when risk warrants it`

Use the strongest applicable level. Changes to scenes, prefabs, map/prefab normalization, campaign setup, physics, pooling, async lifecycle, persistence/network contracts, or frame-dependent UI usually require more than an isolated EditMode unit test.

A specialized static-analysis skill may prohibit executing tests/builds. That restriction does not waive test review, regression-test creation, or reporting that runtime validation remains unexecuted.

Never claim validation from old XML/logs after code has changed. Tie claims to the exact tests/results actually executed for the current change.

## Regression records

`BUG_LEDGER.md` is a temporary queue of current unresolved static findings. It is not permanent history.

`docs/engineering/REGRESSIONS.md` is the permanent protection ledger. When a real regression is fixed, record the root cause and the durable protection that should prevent recurrence. Prefer a focused automated test; also link the invariant or durable-memory update when relevant. A regression is not considered fully closed while its permanent-protection field is empty.

## Repository learning and reminders

Repository knowledge must improve as work proceeds.

- Update an existing owner document instead of appending a new overlapping notebook.
- Replace/remove stale statements; do not accumulate contradictory history.
- Distinguish confirmed current behavior, historical context, and unresolved hypotheses.
- Promote stable architectural rules and repeated pitfalls into `docs/engineering/INVARIANTS.md` or the project constitution when appropriate.
- Promote durable architecture/call-path knowledge into `docs/DEVELOPMENT_MEMORY.md` or `docs/engineering/SYSTEM_MAP.md`.
- If the user has to remind an agent of a lasting engineering fact, treat that as a signal that the fact should be persisted in the repository during the same task after verifying it.
- Never rely on conversational memory as the sole location of an engineering requirement.

## Completion gate

Do not call a behavior-affecting task complete until all applicable items are true:

- the root cause/intent is understood well enough to avoid a symptom-only patch;
- impact analysis covered important indirect dependencies;
- affected tests were classified and stale/missing coverage was handled;
- reproducible regressions received permanent protection where feasible;
- validation widened to the strongest practical level allowed by the task/environment;
- no known failing/stale test was hidden or weakened;
- durable repository knowledge was reconciled and stale facts discovered during the task were corrected or clearly marked;
- any remaining unexecuted validation, manual-only coverage, or uncertainty is reported explicitly.

The standard is not merely “the requested symptom disappeared.” The standard is evidence that the intended behavior works, surrounding contracts remain intact, and the repository is harder to regress than before the change.