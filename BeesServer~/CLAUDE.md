# CLAUDE.md

Guidance for coding agents working in BeesServer.

**Read and follow `AGENTS.md` first.** Its rules are mandatory for ordinary work as well as named skills.

After `AGENTS.md`, use `docs/engineering/CONTEXT_INDEX.md` as the compact router. Do **not** independently preload the constitution, system map, invariants, development/database memory, validation/regression history, or generic skill stack. Load only the current source/tests/schema and owner-document sections that the task, router, or a concrete unresolved dependency requires.

For focused work, stop expanding context once the affected protocol/data contract, current symbols/schema, important dependency, and validation evidence are identified. Broad audits and architecture work may intentionally widen to the full maintained model.

Specialist and generic skills are on-demand procedures. Use `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/search-index/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, or another specialist skill when its procedure is actually relevant; their globally applicable requirements are already summarized in `AGENTS.md`.

Durable repository knowledge should make future tasks cheaper. Prefer precise context-index routes and refreshed owner statements over new startup reading. Repair stale facts and retrieval routes rather than adding overlapping notebooks. Retrieval misses/unresolved candidates belong in `docs/engineering/LEARNING_STATE.md`; high-value deferred maintainability debt belongs in `QUALITY_LEDGER.md`.

Canonical server entrypoint is `server.js`; operational launcher is `start-server.js`. Do not invoke the legacy `siServerDev.js` monolith as the production entrypoint.

Canonical validation is `npm test`, which uses disposable `bees_test` and a temporary test server. Never run automated validation against production `ram`, and never run production migrations without an explicit production request.

For bug audits use `.agents/skills/bug-finding/SKILL.md`; for performance work use `.agents/skills/performance-optimization/SKILL.md`; for dedicated test-suite auditing use `.agents/skills/test-health/SKILL.md`. Load those procedures only when the task invokes them.
