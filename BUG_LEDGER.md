# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

_None logged at this checkpoint. The repository audit remains in progress; this is not a claim that the codebase has no other defects._

## Audit status

- Complete clean finding passes since the latest production changes: **0 / 2**.
- Current focus: distributed WAN actor ownership, lease renewal and release, shutdown upload draining, and batch retry/deduplication.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.
