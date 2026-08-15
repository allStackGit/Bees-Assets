# CLAUDE.md

Guidance for Claude Code and other agents that automatically read this file.

Read and follow `AGENTS.md` before making any repository change. `AGENTS.md` is the mandatory engineering workflow for ordinary fixes as well as named skills.

Also load the documents it requires, especially:

- `PROJECT_CONSTITUTION.md`;
- `docs/DEVELOPMENT_MEMORY.md`;
- `docs/engineering/INVARIANTS.md`;
- `docs/engineering/SYSTEM_MAP.md`;
- `docs/engineering/VALIDATION_POLICY.md`;
- `docs/engineering/REGRESSIONS.md`;
- `docs/TESTING.md`;
- `.agents/skills/repo-learning/SKILL.md`.

Do not create overlapping status/memory documents when an existing maintained file owns the topic. Repair stale facts instead of appending contradictory notes.

Tests are part of the change contract. Do not weaken/delete a failing test merely to make a change pass; classify it as still valid, update-required, obsolete-and-replaced, or missing according to `AGENTS.md`.

A reproducible regression should receive a focused automated test that would have caught it whenever practical, plus durable root-cause/protection knowledge when the lesson is reusable.
