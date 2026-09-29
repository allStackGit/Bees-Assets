---
name: repo-learning
description: Context-efficient BeesServer repository learning. Route first, verify current code/tests/schema, widen only on evidence, and persist only knowledge that reduces future retrieval or repeated mistakes.
---

# Repository Learning

The objective is a progressively more accurate backend model that becomes **cheaper** to use over time. Similar future tasks should require fewer searches, fewer broad reads, and fewer repeated explanations.

## 1. Route before reading

1. Start from the already-fetched root `AGENTS.md`.
2. Read `docs/engineering/CONTEXT_INDEX.md` and choose the closest protocol/data/runtime concept or alias. Use `.agents/skills/search-index/SKILL.md` when the routing/search procedure itself is needed.
3. Inspect the named current source/tests/schema and only the owner-document section(s) needed to understand the contract.
4. Load `PROJECT_CONSTITUTION.md`, `SYSTEM_MAP.md`, `INVARIANTS.md`, detailed database/development memory, validation history, or specialist skills only when the task/routing/risk requires them.
5. Do not read unrelated maintained backend history merely to become generally familiar with the repository.

### Task scopes

- **Focused/local** — one request type, module, test, table, cache, migration, or small refactor. Default to the index row, exact implementation, focused evidence, and at most the directly relevant owner-document section unless blocked.
- **Cross-cutting** — multiple request/user/Game/transaction/schema/client boundaries. Load only the additional routed owner/invariant sections defining those boundaries.
- **Broad/audit/architecture** — repository-wide reasoning is the task; deliberate broad loading is appropriate.

### Stop condition

Stop retrieving once you can state: the enduring protocol/data/safety contract, affected current symbols/schema, important ownership/call-path dependency, and validation target. Widen only because a specific fact remains unresolved, sources contradict, a search misses, or the first hypothesis fails.

Maintained knowledge narrows investigation; it never replaces verification of changed facts against current code/tests/schema and the Unity client where relevant.

## 2. Build only the necessary pre-change model

Before editing behavior, identify the relevant contract, runtime entrypoint, important callers/callees, request/data/transaction owner, persistence/schema/client boundary, and tests. Consider modular/legacy layering, exact numeric identity, reservations, authentication, consolidation/writer coordination, filesystem/cache behavior, background jobs, and performance only when they are on the actual impact path.

Classify affected tests under `AGENTS.md`. Do not use “impact analysis” as justification for an unrelated repository scan.

## 3. Learn while working

Treat something as a candidate lesson when it was materially expensive to rediscover, corrected stale maintained knowledge, exposed a recurring trap, revealed a reusable cross-system relationship, or showed that routing failed.

Cheap lookups, command history, transient task state, and facts already easy to reach are not durable learning.

## 4. Persist with positive context ROI

Before promoting a lesson, ask:

1. Is it verified from current source/tests/schema/client contracts as applicable?
2. Is it likely to matter again?
3. Will storing/routing it eliminate more future searching or reading than the added text costs?
4. Can an existing route or owner statement be refreshed instead of adding another fact?

Prefer, in order: correct a stale route; add a compact alias/cross-link; refresh/compress an owner statement; add new detail only when necessary. Never create another mandatory startup read to solve a local retrieval problem.

Owner documents remain:

- `docs/engineering/CONTEXT_INDEX.md` — compact routing/aliases;
- `docs/engineering/SYSTEM_MAP.md` — concise runtime/ownership orientation;
- `docs/engineering/INVARIANTS.md` — stable must-preserve rules;
- `docs/DEVELOPMENT_MEMORY.md` — detailed current backend implementation knowledge;
- `docs/DATABASE_MODEL.md` — schema/data identity and persistence flow;
- `docs/engineering/REGRESSIONS.md` — reusable fixed-regression lessons/protection;
- `docs/engineering/LEARNING_STATE.md` — unresolved retrieval misses/candidates only.

Git history stores chronology. Replace stale/duplicate statements instead of appending them.

## 5. Retrieval failures are learning-system defects

If the user supplies a location that should have been routable, a known fact is repeatedly missed, or broad searching was required for a recurring area, fix the smallest cause: index alias, stale route, overgrown owner section, skill wording, or test guardrail. Do not duplicate the fact in several documents.

## 6. Completion transaction

Use `.agents/skills/continuous-learning/SKILL.md` for the closed-loop candidate disposition workflow.

Before completion, consider each candidate as **promote**, **refresh**, **defer**, or **reject**. Update durable knowledge only when justified. A task with no worthwhile lesson should leave no learning-document churn.

For code-bearing changes, apply `.agents/skills/code-quality/SKILL.md` only to touched code and immediate interfaces. For pure documentation/skill changes, review the changed guidance directly; no production-code quality scan is required.

## Success metric

After repeated work in an area, a new agent should reach the correct current runtime path and evidence faster than before. If maintained memory causes increasingly large startup reads, the learning system is failing and should be compressed or rerouted.