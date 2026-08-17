# CLAUDE.md

Guidance for Claude Code and other agents that automatically read this file.

**Read and follow `AGENTS.md` before making any repository change.** It is the mandatory workflow for ordinary fixes as well as named skills.

After `AGENTS.md`, use `docs/engineering/CONTEXT_INDEX.md` as the compact router. Do **not** independently preload the constitution, system map, invariants, development memory, regression/testing history, or generic skill stack. Load only the current source/assets/tests and owner-document sections that the task, router, or a concrete unresolved dependency requires.

For focused work, stop expanding context once the affected contract, current symbols/assets, important dependency, and validation evidence are identified. Broad audits and architecture work may intentionally widen to the full maintained model.

Specialist and generic skills are on-demand procedures. Use `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/search-index/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, or another specialist skill when its procedure is actually relevant; their requirements that matter globally are already summarized in `AGENTS.md`.

Durable repository knowledge should make future tasks cheaper. Prefer precise context-index routes and refreshed owner statements over new startup reading. Repair stale facts and retrieval routes rather than adding overlapping notebooks. Retrieval misses/unresolved candidates belong in `docs/engineering/LEARNING_STATE.md`; high-value deferred maintainability debt belongs in `QUALITY_LEDGER.md`.

Tests are part of the change contract. Do not weaken/delete a failing test merely to make a change pass; classify affected tests according to `AGENTS.md`. A reproducible regression should receive focused permanent protection whenever practical.
