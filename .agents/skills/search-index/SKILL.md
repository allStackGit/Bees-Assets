---
name: search-index
description: Context-efficient Bees navigation. Use direct symbol/asset/test routes first, then the smallest subsystem or global routing layer needed for unresolved questions.
---

# Search Index

Direct route first. Root `AGENTS.md` contains common starting points; `docs/engineering/context/*.md` provides small subsystem routes; `docs/engineering/CONTEXT_INDEX.md` is a secondary router for ambiguous or cross-cutting work, not startup payload.

## Retrieval ladder

1. Convert the task into concrete symbols, files, asset/scene/prefab names, configuration names, tests, or error terms.
2. If the owner is obvious, inspect that exact current implementation/assets/tests immediately.
3. If a concrete contract/dependency remains unresolved, read the matching small subsystem route under `docs/engineering/context/`.
4. Use `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, boundaries cross, the direct lookup misses, or the task itself is broad.
5. Search exact names in the narrowest likely directory before scoped or repository-wide search.
6. Expand to broader owner-document reads only after narrower retrieval demonstrably fails or the task requires broad reasoning.
7. Verify behavior-changing conclusions against current production source/assets/configuration/tests.

## Stop rule

Stop retrieval as soon as you can answer:

- What contract/behavior is being changed or repaired?
- Which current symbols/assets own it?
- Which important caller/callee, lifecycle, or cross-system dependency could invalidate a local fix?
- What evidence will validate the result?

Do not keep reading merely to increase general confidence. Additional context needs a named unresolved question.

## Context-budget defaults

For focused/local work, prefer exact source/test retrieval with zero routing-document reads when that is sufficient. If routing is needed, read only the smallest relevant subsystem route. Reading an entire architecture, development-memory, regression, testing, or global routing document for a small obvious symptom is a retrieval failure unless the symptom genuinely spans it.

For cross-cutting work, add only the routes/owner/invariant sections for boundaries actually crossed. Broad audits and architecture reviews may intentionally exceed these defaults.

## What belongs in routing docs

Store only information that materially reduces future retrieval cost: common aliases, authoritative starting symbols/files/assets, focused evidence, and important cross-system links. Do not duplicate implementation detail or turn a router into a second architecture document.

A good route should replace future searching. If a route itself requires large follow-on reads for routine tasks, make it more precise or split it.

## Retrieval misses and maintenance

Repair the smallest cause of a miss. Prefer a direct/subsystem route over global prose. Use `docs/engineering/LEARNING_STATE.md` only for unresolved misses that cannot yet be repaired, and remove resolved misses rather than retaining chronology.

The routing system should answer: **where do I start, what nearby dependency matters, and what evidence should I inspect?**
