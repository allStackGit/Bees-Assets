# Bees repository memory

This directory is the successor to `docs/DEVELOPMENT_MEMORY.md`.

The old file remains a historical reference. New durable knowledge should be added to the smallest relevant file here rather than appended to one monolith.

## Reading order

Read only the files relevant to the work being performed:

- `testing-and-architecture.md` — assembly/test boundaries, qualification rules, source-organization conventions.
- `campaign.md` — campaign sequence, persistence model, Titania/Minesweeper/Beenoculars authoring knowledge.
- `runtime-lifecycle.md` — pooling, timers, commands, targeting, carrier/bomber/healing/mining/retreat invariants.
- `pathfinding-and-performance.md` — obstacle ingestion, dynamic avoidance, performance qualification.
- `networking-and-data.md` — socket recovery, server/data contracts, persisted IDs/settings migration.
- `assets-and-audio.md` — Fire Tank visuals/debris and UI/gameplay audio ownership.

## Memory rules

- Record only knowledge that is expensive to rediscover and likely to matter again.
- Prefer invariants, ownership boundaries, protocol contracts, and known pitfalls over change logs.
- Update an existing statement instead of adding a second contradictory version.
- Do not duplicate the same fact across files; link to the owning topic when useful.
- Keep temporary branch status, test-run counts, and one-off debugging transcripts out of repository memory.
- Source files that repeatedly become too large to edit safely should be split along existing ownership boundaries; prefer partial classes for Unity components when private state must remain shared.
