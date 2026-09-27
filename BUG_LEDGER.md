# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

## Audit status

- Complete clean finding passes since the latest production changes: **0 / 2**.
- Current focus: broad post-fix passes across continual-learning orchestration, RL action/movement and telemetry behavior, campaign dialogue/presentation, combat targeting, and remaining gameplay, persistence, networking, and UI subsystems. REG-076 closes a WAN policy snapshot version-attribution race; REG-077 closes a stale WAN capacity-rate report. The public native-demo quarantine handoff was also audited; REG-074 closes a staged-file integrity gap. REG-047 through REG-077 are fixed and recorded in the regression log. Regression protections for REG-047 through REG-052 and REG-055 through REG-077 were added but not run; REG-053 and REG-054 correct exception-stack diagnostics and are documented in the regression log.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.
