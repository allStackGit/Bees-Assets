---
name: repo-learning
description: Mandatory context-efficient repository-learning and change-safety workflow for Bees. Load the smallest useful maintained model, verify it against current code/assets/tests, perform impact/test analysis, run touched-code quality review, and reconcile durable learning before completion.
---

# Repository Learning

Use this skill for every development task. The objective is not more notes; it is a progressively more accurate model of Bees that is cheaper to reload and harder to misuse.

## 1. Load context efficiently

1. Read `AGENTS.md` and `PROJECT_CONSTITUTION.md`.
2. Read `docs/engineering/CONTEXT_INDEX.md`, `docs/engineering/SYSTEM_MAP.md`, and `docs/engineering/INVARIANTS.md`.
3. Follow `.agents/skills/search-index/SKILL.md` to select only relevant sections of `docs/DEVELOPMENT_MEMORY.md`, `docs/engineering/VALIDATION_POLICY.md`, `docs/engineering/REGRESSIONS.md`, `docs/TESTING.md`, tests/assets and focused subsystem documents.
4. Check `docs/engineering/LEARNING_STATE.md` for a retrieval miss, repeated mistake or unresolved candidate relevant to the task.
5. Broad audits may deliberately load the full maintained model. Focused tasks should not consume unrelated campaign/UI/pathfinding/persistence detail merely because it exists.

Maintained knowledge narrows investigation; it never replaces verification of code/assets/tests that can change.

## 2. Build the pre-change model

Before editing, identify the enduring behavior/contract, entrypoints, important callers/callees, state owners/lifecycles, serialized assets, persistence/network boundaries and tests. Consider pooling, async publication, physics/frame ordering, map/prefab/resource contracts, campaign identity, replay/determinism, UI ownership and performance/resource behavior as applicable.

Classify affected tests using the four states in `AGENTS.md` before completion. Do not begin with a symptom patch and postpone impact analysis until a test happens to pass.

## 3. Learn while working

Follow `.agents/skills/continuous-learning/SKILL.md` continuously. Capture candidate lessons when investigation reveals expensive-to-rediscover architecture, ownership/data flow, serialized relationships, recurring traps, cross-system relationships, useful debugging/validation/optimization procedures, stale knowledge, user corrections or retrieval failures.

Do not commit every observation. A candidate becomes durable only after source/asset/test verification and classification.

## 4. Maintain the model, not a chronology

Use the existing owner:

- `docs/DEVELOPMENT_MEMORY.md` — detailed current implementation/gameplay knowledge;
- `docs/engineering/SYSTEM_MAP.md` — concise ownership/call-path orientation;
- `docs/engineering/INVARIANTS.md` — stable must-preserve rules;
- `docs/engineering/REGRESSIONS.md` — fixed regression root causes and protection;
- `docs/engineering/CONTEXT_INDEX.md` — retrieval aliases and links, not detailed truth;
- `docs/engineering/LEARNING_STATE.md` — unresolved candidates/retrieval misses/curation debt only.

Replace stale statements, consolidate duplicates, distinguish current behavior/history/hypotheses, and change `PROJECT_CONSTITUTION.md` only for deliberate project-definition changes.

## 5. Regression and user-reminder learning

A reproducible regression should leave focused permanent protection and any reusable rule in the correct owner document. If automation is impractical, record why and the strongest repeatable protection.

A lasting user correction/reminder is a learning-system signal: verify it, persist it when appropriate, and if the user had to repeat an already-recorded fact, repair retrieval/index/skill enforcement rather than duplicating the fact.

## 6. Touched-code quality gate

Before completion, follow `.agents/skills/code-quality/SKILL.md` over every touched implementation/test/tooling/configuration area. Fix clear local defects or clarity problems that are safe and in scope. Route broader validated debt to the appropriate active ledger without turning a narrow change into an uncontrolled refactor.

## 7. Learning transaction and reconciliation

Before completion:

1. Re-read changed behavior/tests/assets from a future-maintainer perspective.
2. Resolve every candidate lesson as **promote**, **refresh**, **defer**, or **reject** under the continuous-learning skill.
3. Update the context index when a new alias/relationship would materially shorten future retrieval.
4. Reconcile stale maintained knowledge and relevant open learning-state items.
5. Confirm regression protection/test classification and report unexecuted validation/uncertainty.

A task may legitimately produce no durable lesson, but the learning transaction must still be considered. Never create filler documentation just to prove learning occurred.

## Efficiency target

The desired trend is that future agents identify the correct subsystem, assets, invariants, tests and danger surfaces with fewer broad reads/searches. If maintained memory itself becomes expensive to load, improve the index/structure and split by ownership rather than accepting permanent context growth.