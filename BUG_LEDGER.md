# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

The WAN actor empty-trajectory-queue crash and capped training-log flush stall were fixed. A stale duplicate-acknowledgement race found in both fixed-topology and elastic brokers was fixed, with focused regression protection added. Invalid numeric matchup-mode values were fixed to accept only the named modes. No tests were executed, per the audit's static-only constraint. The initial episode-log scan now preserves a complete first record when its 4 MiB tail begins on a line boundary, with focused regression coverage added.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.
