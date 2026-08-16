---
name: search-index
description: Context-efficient Bees repository navigation. Use the compact concept index before broad searches, follow targeted owner-document/source/asset/test links, record retrieval misses, and keep the index small and current.
---

# Search Index

Use `docs/engineering/CONTEXT_INDEX.md` as the first navigation layer. It is a routing map, not an authority.

## Retrieval ladder

1. Convert the task into a few gameplay/system concepts, aliases, symbols, asset names or error terms.
2. Search `CONTEXT_INDEX.md` for those terms and related concepts.
3. Read the linked owner-document sections and inspect the named current source/assets/tests.
4. If unresolved, search exact symbol/file/prefab/scene/error names in the narrowest likely directory.
5. Expand to scoped content search, then repository-wide search only when narrower retrieval fails.
6. Verify material conclusions against current production source, serialized assets/configuration and tests.

Do not preload all of `docs/DEVELOPMENT_MEMORY.md` for a focused task. Use index/heading routes unless the task is genuinely broad.

## What belongs in the index

Index concepts that materially shorten future retrieval: subsystem names/common aliases, authoritative entrypoints/symbols/files/assets, owner documentation, focused tests/validation and important cross-system relationships. Prefer one compact row or bullet per concept.

Do **not** index every class/function/file, copy detailed implementation facts, paste search results or turn the index into a second architecture document.

## Retrieval misses and maintenance

A retrieval miss occurs when a reusable area cannot be located efficiently: repeated broad searches are needed, the user supplies the location, an existing lesson is overlooked, or a renamed/moved symbol leaves a stale route. Record unresolved misses in `docs/engineering/LEARNING_STATE.md`; once the correct route is established, update the index and remove the miss.

When touched code/assets/docs rename or relocate an indexed concept, update its row in the same task. Add useful synonyms learned from errors/user language and remove stale/duplicate routes. If the index grows too large, move detail to owner docs while keeping a compact top-level route.

The index should answer: **where should I start, what else is connected, and what evidence should I inspect?**