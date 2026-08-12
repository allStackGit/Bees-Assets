---
name: repo-learning
description: Maintain concise, source-grounded repository knowledge so future agents can work with less rereading, avoid known pitfalls, and preserve an accurate understanding of architecture and development contracts.
---

# Repository Learning

Use this skill continuously while working in the repository. The goal is to maintain a compact, accurate working model of the codebase rather than a chronological activity log.

## Start

1. Read `AGENTS.md`, `CLAUDE.md`, and other repository instructions if present.
2. Read the repository's maintained durable memory, especially `docs/DEVELOPMENT_MEMORY.md`, plus any focused invariant/workflow documents relevant to the task.
3. Treat those documents as an index and prior model, not unquestioned truth. Revalidate important facts against current code when they matter to the task.

## During Work

As code is inspected or changed, identify reusable knowledge that will make future work faster or safer, including:

- architecture and subsystem responsibilities;
- important entry points, call paths, ownership, and data/state flow;
- stable contracts and invariants;
- non-obvious dependencies and cross-system assumptions;
- recurring pitfalls, misleading patterns, and fragile areas;
- canonical files, commands, configurations, and development procedures;
- discoveries that materially reduce future repository reading or repeated investigation.

Do not record every file read, command run, bug found, fix made, or transient investigation step.

## Maintain Durable Memory

Update the existing repository memory when useful knowledge is discovered or existing knowledge becomes stale.

- Prefer editing the document that already owns the topic instead of creating another overlapping file.
- Keep entries concise, current, and source-grounded.
- Replace or remove stale statements rather than appending contradictory history.
- Consolidate duplicated information.
- Record durable conclusions, not speculation.
- Avoid restating obvious code or information that is cheaper to rediscover than to maintain.
- Preserve the distinction between current production behavior, historical context, and unresolved uncertainty.

`docs/DEVELOPMENT_MEMORY.md` is the default home for general architecture and implementation knowledge unless a more specific maintained document clearly owns the information.

## Efficiency Rule

The repository memory should become a progressively better map of the codebase. Use it to narrow future reads, but still inspect the authoritative code whenever correctness depends on a detail that may have changed.

A successful update reduces future context and token cost without allowing stale summaries to replace source verification.
