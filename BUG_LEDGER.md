# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

The WAN actor empty-trajectory-queue crash and capped training-log flush stall were fixed. The focused WAN queue regression was added; the existing capped-upload regression was reviewed against the fix. Neither test was executed, per the audit's static-only constraint.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.
