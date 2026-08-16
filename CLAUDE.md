# CLAUDE.md

Guidance for Claude Code and other agents that automatically read this file.

**Read and follow `AGENTS.md` before making any repository change.** It is the mandatory workflow for ordinary fixes as well as named skills.

Do not independently preload the older full documentation list. Follow the context-budgeted loading order in `AGENTS.md`:

1. `PROJECT_CONSTITUTION.md` for stable project meaning.
2. `docs/engineering/CONTEXT_INDEX.md`, `docs/engineering/SYSTEM_MAP.md`, and `docs/engineering/INVARIANTS.md` for compact orientation.
3. `.agents/skills/search-index/SKILL.md` to select only task-relevant detailed memory, testing/regression material, source, assets and tests.
4. `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, and `.agents/skills/code-quality/SKILL.md` for the entire task.

Broad audits may intentionally load the full maintained model. Focused work should not repeatedly digest unrelated `docs/DEVELOPMENT_MEMORY.md` sections.

Durable knowledge belongs in the existing owner documents. Retrieval misses/unresolved candidates belong in `docs/engineering/LEARNING_STATE.md`; high-value deferred maintainability debt belongs in `QUALITY_LEDGER.md`. Repair stale facts and retrieval routes rather than adding overlapping notebooks.

Tests are part of the change contract. Do not weaken/delete a failing test merely to make a change pass; classify it as still valid, update-required, obsolete-and-replaced, or missing according to `AGENTS.md`. A reproducible regression should receive focused permanent protection whenever practical.
