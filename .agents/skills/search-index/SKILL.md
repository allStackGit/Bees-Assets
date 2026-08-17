---
name: search-index
description: Context-efficient Bees navigation. Use the compact concept index first, retrieve the smallest evidence packet that answers the task, and expand only when a concrete dependency remains unresolved.
---

# Search Index

`docs/engineering/CONTEXT_INDEX.md` is the first navigation layer. It is a routing map, not authority and not a checklist of documents to preload.

## Retrieval ladder

1. Convert the task into a few concrete concepts, aliases, symbols, asset/scene/prefab names, or error terms.
2. Find the closest row in `CONTEXT_INDEX.md`.
3. Build the smallest useful evidence packet from that row: normally the exact current implementation/assets, focused tests, and at most the directly relevant owner-document section.
4. Search exact symbol/file/prefab/scene/error names in the narrowest likely directory if the row is insufficient.
5. Expand to a scoped content search only for a specific unresolved relationship.
6. Use repository-wide search or broad owner-document reads only after narrower retrieval demonstrably fails or the task is itself broad.
7. Verify behavior-changing conclusions against current production source/assets/configuration/tests.

## Stop rule

Stop retrieval as soon as you can answer all four:

- What contract/behavior is being changed or repaired?
- Which current symbols/assets own it?
- Which important caller/callee, lifecycle, or cross-system dependency could invalidate a local fix?
- What evidence will validate the result?

Do not keep reading merely to increase general confidence. Additional context needs a named unresolved question.

## Context-budget defaults

For a focused/local task, prefer one index row plus the few files/sections it directly routes to. Reading an entire architecture, development-memory, regression, or testing document for one small symptom is a retrieval failure unless the symptom genuinely spans that document.

For cross-cutting work, add only the owner/invariant sections for the boundaries actually crossed. Broad audits and architecture reviews may intentionally exceed these defaults.

## What belongs in the index

Index only information that materially reduces future retrieval cost: common user/error aliases, authoritative starting symbols/files/assets, owner documentation, focused evidence, and important cross-system links.

Do not index every class/function/file, duplicate implementation details, paste search results, or turn the index into a second architecture document.

A good index entry should replace future searching. If an entry itself requires large follow-on reads for routine tasks, make the route more precise.

## Retrieval misses and maintenance

A retrieval miss occurs when a recurring area requires repeated broad searches, the user has to supply a known location, an existing durable fact is missed because its route is unclear, or a renamed/moved symbol leaves stale navigation.

Repair the smallest cause. Prefer a better alias/route over new prose. Use `docs/engineering/LEARNING_STATE.md` only for unresolved misses that cannot yet be repaired. Remove resolved misses rather than retaining history.

The index should answer: **where do I start, what nearby dependency matters, and what evidence should I inspect?**