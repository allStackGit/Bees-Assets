# AGENTS.md

Mandatory engineering rules for every coding agent and development task in this repository.

## Authority and context-budgeted required reading

1. Follow the user's explicit request and branch target.
2. Read `PROJECT_CONSTITUTION.md` before changing behavior.
3. Read `docs/engineering/CONTEXT_INDEX.md`, `docs/engineering/SYSTEM_MAP.md`, and `docs/engineering/INVARIANTS.md` first. Use them to identify the smallest authoritative context for the task.
4. Follow `.agents/skills/search-index/SKILL.md` to load only relevant sections of `docs/DEVELOPMENT_MEMORY.md`, `docs/engineering/VALIDATION_POLICY.md`, `docs/engineering/REGRESSIONS.md`, `docs/TESTING.md`, and focused subsystem documents. Broad audits may require full reads; focused work should not preload unrelated memory.
5. Read and follow `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/search-index/SKILL.md`, and `.agents/skills/code-quality/SKILL.md` for the entire task. Named specialist skills do not replace these.
6. Maintained documentation is a source-grounded model, not unquestioned truth. Revalidate material facts against current code, assets, configuration and tests; repair stale knowledge when found.

If implementation conflicts with the constitution or a confirmed invariant, implementation is presumed defective unless the project owner explicitly changes the requirement. Existing tests are evidence, not authority; stale tests must be repaired rather than used to redefine intended behavior.

## Branch discipline

Respect an explicitly named branch. Otherwise, do not make ordinary development changes directly on `main`; create a descriptive task branch from the latest appropriate base. Specialist skills may impose stricter branch/commit rules.

## Pre-change impact analysis

Before editing production code, tests, serialized assets, scenes, prefabs, configuration or persistence data, trace the affected path and important callers/callees. Explicitly consider as applicable:

- user-visible gameplay/campaign behavior and mission identity;
- Scene/Stage/Level/GameState lifecycle, teardown and cleanup;
- pooling, object reuse, timers, deferred release and stale state;
- async pathfinding/work ownership, ordering, cancellation and lifecycle tokens;
- physics/collision/frame ordering;
- map, obstacle, prefab, Resources, scene and serialized-name/GUID contracts;
- local/server persistence, save compatibility, request/response, reconnect and deduplication;
- replay/determinism and stable ordering;
- UI/dialogue/input ownership and resolution/aspect-ratio behavior;
- frame time, allocations, memory and low-end hardware constraints;
- tests, fixtures, reflection adapters, manual play tests and release-gate coverage.

A narrow symptom fix is incomplete until important indirect dependencies are checked.

## Test contracts and validation

For every behavior-affecting change, classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never delete, skip, weaken, loosen or rewrite a test merely to make a change pass. A reproducible regression should gain focused automated protection that would have caught it whenever practical; otherwise record the strongest repeatable protection in `docs/engineering/REGRESSIONS.md`.

When execution is available, widen evidence appropriately:

`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play test/system qualification when risk warrants it`

Scene/prefab/map normalization, pooling/lifecycle, async pathfinding, physics, persistence/network and frame-dependent UI usually require broader-than-unit evidence. Never claim old XML/logs validate changed source.

## Continuous learning, retrieval, and quality

Every substantive task must leave future agents at least as easy to orient as before.

- Start from `docs/engineering/CONTEXT_INDEX.md`; prefer targeted retrieval over repeated broad scans.
- Run the continuous-learning transaction from `.agents/skills/continuous-learning/SKILL.md`: candidate lessons must be promoted, refreshed, deferred with evidence needed, or rejected.
- Update existing owner documents rather than accumulating overlapping notebooks. Replace stale statements and distinguish confirmed current behavior, history and uncertainty.
- Retrieval misses and unresolved learning candidates belong in `docs/engineering/LEARNING_STATE.md`; it is active control state, not history.
- Durable architecture/call-path knowledge belongs in development memory/system map; stable rules belong in invariants; fixed regression lessons belong in `REGRESSIONS.md`.
- Repeated agent mistakes despite existing guidance must trigger review of the relevant skill/index/test guardrail, not another duplicate reminder.
- Before completion, run the touched-code quality gate in `.agents/skills/code-quality/SKILL.md`. Fix clear local quality problems when safe; route broader maintainability debt to `QUALITY_LEDGER.md`, bugs to `BUG_LEDGER.md`, and performance opportunities to `PERFORMANCE_LEDGER.md` when that ledger exists/should be used by the performance workflow.

The learning system must reduce future context cost. Do not grow memory or the context index with cheap lookups, transient facts, duplicated statements or unsupported assumptions.

## Completion gate

Do not call a behavior-affecting task complete until applicable items are true: root cause/intent is understood; impact analysis covered important dependencies; tests were classified and stale/missing coverage handled; validation reached the strongest practical level; no test/safety contract was weakened for convenience; touched code passed the quality review; durable knowledge/context index were reconciled; learning candidates were resolved or explicitly deferred; and remaining uncertainty/unexecuted validation is reported.

The standard is evidence that intended behavior works, surrounding contracts remain intact, the repository is harder to regress, and the next agent can understand the affected area with less unnecessary context.