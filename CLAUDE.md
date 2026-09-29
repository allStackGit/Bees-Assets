# CLAUDE.md

Guidance for Claude Code and other agents that automatically read this file.

**Read and follow `AGENTS.md` before making any repository change.** It is the mandatory bootstrap for ordinary fixes as well as named skills.

After `AGENTS.md`, do **not** automatically load `docs/engineering/CONTEXT_INDEX.md` or broad repository documentation. For focused work, follow the direct route in `AGENTS.md` to the exact current source/assets/configuration/tests. Load a subsystem route under `docs/engineering/context/` only when it resolves a concrete contract or dependency question.

Use `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, the task crosses subsystem boundaries, the direct lookup misses, or the work is intentionally broad. Do **not** independently preload the constitution, system map, invariants, development memory, regression/testing history, or generic skill stack.

Stop expanding context once the affected contract, current symbols/assets, important dependency, and validation evidence are identified. Broad audits and architecture work may intentionally widen to the maintained model.

Specialist and generic skills are on-demand procedures. Use `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/search-index/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, or another specialist skill only when its procedure is relevant.

Durable repository knowledge should make future tasks cheaper. Prefer a precise direct/subsystem route and refreshed owner statements over startup reading. Repair stale facts and retrieval routes rather than adding overlapping notebooks. Retrieval misses/unresolved candidates belong in `docs/engineering/LEARNING_STATE.md`; high-value deferred maintainability debt belongs in `QUALITY_LEDGER.md`.

Tests are part of the change contract. Do not weaken/delete a failing test merely to make a change pass; classify affected tests according to `AGENTS.md`. A reproducible regression should receive focused permanent protection whenever practical.
