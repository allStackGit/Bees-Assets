# AGENTS.md

Mandatory engineering rules for every coding agent and development task in this repository.

## Minimum bootstrap and context budget

1. Follow the user's explicit request and branch target.
2. After this file, the only unconditional repository read is `docs/engineering/CONTEXT_INDEX.md`.
3. Read `docs/engineering/CONTEXT_INDEX.md` as the compact router. Start with the row matching the request, protocol name, table, symbol, error, or operational concept.
4. For a focused task, load only the relevant owner-document section(s), current source/tests/schema, and focused evidence identified by the router. **Do not preload** `PROJECT_CONSTITUTION.md`, all of `SYSTEM_MAP.md`, all of `INVARIANTS.md`, detailed development/database history, validation/regression history, or the generic skill stack merely because they exist.
5. Read `PROJECT_CONSTITUTION.md` when the task can change runtime behavior, persistence/protocol/authentication/schema semantics, production safety, learning-data meaning, or another project-definition boundary.
6. Read relevant sections of `SYSTEM_MAP.md` and/or `INVARIANTS.md` when the change crosses request/data/transaction ownership, modular/legacy boundaries, schema/protocol boundaries, security, or another high-risk area. Read a specialist skill only when the task actually invokes that procedure. Generic skills are reference procedures, not mandatory startup payload.
7. Broad audits, architecture work, unfamiliar cross-cutting changes, or unresolved focused investigations may deliberately widen to the full maintained model.

### Retrieval stop rule

Once you can name the affected contract, current symbols/schema, important ownership or caller/callee boundary, and the evidence needed to validate the change, **stop loading context**. Expand only for a concrete unresolved dependency, contradiction, retrieval miss, or failed hypothesis.

For documentation/skill/agent-workflow-only changes, the normal fast path is: `AGENTS.md -> CONTEXT_INDEX.md -> affected docs/skills -> direct guardrail tests if any`. Do not load backend/database architecture unless the guidance change actually depends on it.

Maintained documentation is a source-grounded navigation model, not unquestioned truth. Revalidate material facts against current code/tests/schema and the Unity client when a cross-project contract depends on them.

## Branch discipline

Respect an explicitly named branch. Otherwise, do not make ordinary development changes directly on `main`; create a descriptive task branch from the latest appropriate base. Specialist skills may impose stricter rules.

## Change safety

Before behavior-affecting edits, trace the affected path far enough to understand the enduring protocol/data/safety contract, important callers/callees, request/user/Game/transaction ownership, persistence/schema implications, security boundary, Unity compatibility, and performance implications that are actually relevant. A narrow symptom fix is incomplete if a known indirect dependency can invalidate it, but this is not permission for an unrelated repository-wide scan.

If implementation conflicts with the constitution or a confirmed invariant, implementation is presumed defective unless the project owner explicitly changes the requirement.

For every behavior-affecting change, classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never delete, skip, weaken, loosen or rewrite a test merely to obtain a pass. A reproducible regression should gain focused automated protection whenever practical.

When execution is available, widen evidence proportionally:

`focused node:test reproducer -> affected subsystem/integration -> complete npm test -> Unity/client cross-layer validation when protocol contracts changed`

`npm test` is canonical server qualification and uses disposable `bees_test`. Never point automated tests at production database `ram`, run a production migration, set `BEES_ALLOW_PRODUCTION_MIGRATION=1`, or mutate production data/schema unless the user explicitly requests that production operation. Never claim old results validate changed source.

## GitHub Actions prohibition

**Do not use GitHub Actions for development, testing, patch application, builds, qualification, or verification. Do not trigger, dispatch, rerun, wait for, inspect, or cite GitHub Actions workflow runs as task evidence. Required validation must be run locally or by an explicitly user-controlled non-Actions environment.**

## Repository learning must make future work cheaper

Use `.agents/skills/search-index/SKILL.md`, `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, and `.agents/skills/code-quality/SKILL.md` when their procedures are needed; do not preload them all at task start.

- `CONTEXT_INDEX.md` is the primary reusable routing layer. Prefer improving a route over adding another mandatory document.
- Persist only verified knowledge that was expensive to rediscover, prevents a likely repeated mistake, or materially shortens future retrieval.
- A new durable note, index entry, or required read should have positive context ROI: it should remove more future searching/reading than it adds.
- Update existing owner documents rather than accumulating overlapping notebooks. Replace stale statements; Git history already stores chronology.
- Retrieval misses or repeated mistakes should trigger the smallest routing/skill/test guardrail repair that prevents recurrence.
- A task may legitimately produce no documentation change. Do not create filler learning artifacts.
- Code-quality review is scoped to touched code and immediate interfaces; documentation-only tasks do not require a production-code quality scan.

## Completion gate

Do not call a behavior-affecting task complete until applicable items are true: root cause/intent is understood; important protocol/persistence/transaction/concurrency/security/schema dependencies were checked; affected tests were classified and stale/missing coverage handled; validation reached the strongest practical level; no safety/test/database-isolation contract was weakened; touched code received proportionate quality review; any durable learning/retrieval miss was reconciled; and remaining uncertainty or unexecuted validation is reported.

The target is evidence that intended behavior works and that the next similar task requires **less** context, not more.