---
name: code-quality
description: Mandatory touched-code quality gate for Bees. Review every modified implementation/test/tooling/configuration area for correctness, clarity, maintainability, performance hazards and testability; fix safe local issues and route broader validated debt without uncontrolled refactoring.
---

# Code Quality

Apply this gate to every code-bearing file touched by a task and the immediate interfaces/ownership assumptions needed to judge it.

## Review dimensions

Check for:

- correctness, edge cases, failure behavior and invariant preservation;
- clear ownership, lifecycle, state mutation, pooling/reuse and cleanup;
- async publication/cancellation and Unity frame/physics ordering;
- serialized names/Resources/GUID/prefab/scene contracts and misleading asset assumptions;
- unnecessary complexity, deeply coupled flow, duplication and hidden temporal dependencies;
- names/types/APIs that make incorrect use easy or obscure intent;
- comments that contradict code or explain mechanics instead of the non-obvious reason/contract;
- testability and tests that exercise real Unity/game behavior instead of fragile implementation text when practical;
- avoidable per-frame/per-fixed-step allocations, hierarchy lookups, scans, synchronization, pathfinding/physics/render/UI work and memory retention;
- deterministic ordering and persistence/network compatibility;
- dead code, stale compatibility paths or abstractions made misleading by the change.

## Action rule

Fix a quality problem immediately when the improvement is clearly correct, local, low-risk, behavior-preserving (unless behavior change is the task) and proportionate to the touched area. Add/update tests when the improvement changes a protected contract.

Do not turn a narrow fix into a repository-wide cleanup. If a valuable improvement requires broader redesign, serialized migration, profiling, play testing or independent validation, record a concise validated entry in `QUALITY_LEDGER.md`.

Route findings by nature:

- incorrect behavior -> `BUG_LEDGER.md` / normal bug rules;
- plausible performance opportunity -> performance workflow/ledger;
- maintainability/clarity debt with no current behavioral defect -> `QUALITY_LEDGER.md`.

Do not log cosmetic preferences, formatting churn or speculative refactors.

## Quality ledger format

Use `QUALITY_LEDGER.md` only for current unresolved high-value maintainability work:

`### QUAL-001 — Short title`
`**Location:** path/symbol`
`**Problem:** concrete readability/complexity/ownership/testability cost`
`**Improvement:** bounded proposed direction`
`**Why deferred:** why it is unsafe or disproportionate in the current task`

Remove resolved/disproved entries; Git history preserves history.

## Completion

Re-read the final diff as a future human/agent maintainer. Changed code should be no harder to understand, should expose important assumptions rather than hide them, and should not introduce avoidable performance/defect risk. Feed recurring misunderstandings into continuous learning rather than relying on the current conversation.