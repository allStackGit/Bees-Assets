---
name: repo-learning
description: Context-efficient Bees repository learning. Route directly when possible, verify current code/assets/tests, widen only on evidence, and persist only knowledge that reduces future retrieval or repeated mistakes.
---

# Repository Learning

The objective is a progressively more accurate model of Bees that becomes **cheaper** to use over time. Similar future tasks should need fewer searches, broad reads, and reminders.

Related workflows remain separate: `.agents/skills/search-index/SKILL.md` for bounded discovery, `.agents/skills/continuous-learning/SKILL.md` for durable promotion decisions, and `.agents/skills/code-quality/SKILL.md` for touched-code quality review.

## 1. Direct route first

1. Start from the already-fetched root `AGENTS.md`.
2. If the task names or clearly implies the owner, inspect the exact current source/assets/configuration/tests first. Use the matching `docs/engineering/context/*.md` route only when it resolves a concrete contract/dependency question.
3. Read `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, the task crosses subsystem boundaries, the direct lookup misses, or the task itself is broad.
4. Load `PROJECT_CONSTITUTION.md`, `SYSTEM_MAP.md`, `INVARIANTS.md`, validation/regression history, or specialist skills only when task/risk/routing actually requires them.
5. Do not read unrelated maintained memory merely to become generally familiar with the repository.

### Task scopes

- **Focused/local** — exact implementation + focused evidence; add only the directly relevant subsystem/owner section when needed.
- **Cross-cutting** — add the routes/owner/invariant sections for boundaries actually crossed.
- **Broad/audit/architecture** — repository-wide reasoning is the task; deliberate broad loading is appropriate.

### Stop condition

Stop retrieving once you can state the enduring contract, affected current symbols/assets, important ownership/call-path dependency, and validation target. Widen only for a specific unresolved fact, contradiction, retrieval miss, or failed hypothesis.

Maintained knowledge narrows investigation; it never replaces verification against current code/assets/tests.

## 2. Build only the necessary pre-change model

Before editing behavior, identify the relevant contract, entrypoint, important callers/callees, owner/lifecycle, serialized/persistence/network boundary, and tests that are actually on the impact path. Classify affected tests under `AGENTS.md`. Do not use impact analysis to justify an unrelated repository scan.

## 3. Learn while working

A candidate lesson should have been materially expensive to rediscover, corrected stale maintained knowledge, exposed a recurring trap, revealed a reusable relationship, or shown that routing failed. Cheap lookups, transient task state, and facts already easy to reach are not durable learning.

## 4. Persist with positive context ROI

Before promotion ask whether the lesson is verified, likely to matter again, removes more future retrieval than its text costs, and can be expressed by refreshing an existing route/owner statement.

Prefer, in order: fix a direct route; fix a subsystem route; add a compact alias/cross-link; refresh/compress an owner statement; add new detail only when necessary. Never create another mandatory startup read to solve a local retrieval problem.

Owner documents include:

- `docs/engineering/CONTEXT_INDEX.md` — secondary global routing for ambiguous/cross-cutting work;
- `docs/engineering/context/*.md` — compact subsystem routes;
- `docs/engineering/SYSTEM_MAP.md` — ownership/call-path orientation;
- `docs/engineering/INVARIANTS.md` — stable must-preserve rules;
- `docs/DEVELOPMENT_MEMORY.md` — detailed current implementation/gameplay knowledge;
- `docs/engineering/REGRESSIONS.md` — reusable fixed-regression lessons/protection;
- `docs/engineering/LEARNING_STATE.md` — unresolved retrieval misses/candidates only.

Git history stores chronology. Replace stale/duplicate statements instead of appending them.

## 5. Retrieval failures are learning-system defects

If a recurring area requires broad search, a known location is repeatedly missed, or a renamed/moved symbol leaves stale navigation, repair the smallest cause: direct route, subsystem route, global router, owner statement, skill wording, or test guardrail. Do not duplicate the same fact in several documents.

## 6. Completion transaction

Before completion, classify each candidate as **promote**, **refresh**, **defer**, or **reject**. Update durable knowledge only when justified. A task with no worthwhile lesson should leave no learning-document churn.

For code-bearing changes, apply `.agents/skills/code-quality/SKILL.md` only to touched code and immediate interfaces. For pure documentation/skill changes, review the changed guidance directly; no production-code quality scan is required.

## Success metric

After repeated work in an area, a new agent should reach the correct current implementation and evidence faster than before. If maintained guidance causes increasingly large startup reads, compress or reroute it.
