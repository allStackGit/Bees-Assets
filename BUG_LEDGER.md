# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

The WAN actor empty-trajectory-queue crash was fixed and has a focused regression test. The change and test were not executed, per the audit's static-only constraint.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.
