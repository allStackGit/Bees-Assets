# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

The WAN actor empty-trajectory-queue crash and capped training-log flush stall were fixed. A stale duplicate-acknowledgement race found in both fixed-topology and elastic brokers was fixed, with focused regression protection added. Invalid numeric matchup-mode values were also fixed to reject values outside the defined enum. No tests were executed, per the audit's static-only constraint.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.
