# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

### BUG-001 — Expiry cleanup can remove an in-flight telemetry upload
**Location:** `BeesServer~/rlTelemetryUploads.js`, `RlTelemetryUploadManager.handle()` and `cleanupExpired()`  
**Description:** A concurrent request runs cleanup before its own chunk/complete operation is queued. Cleanup uses the last completed activity timestamp and does not account for a chunk or completion already in flight, so a slow filesystem operation that crosses the idle cutoff can have its session removed and partial file unlinked while it is still processing. The worker then loses a valid upload and subsequent chunks receive `unknown-upload`.

## Audit status

- Complete clean finding passes since the latest production changes: **0 / 2**.
- Current focus: broad post-fix passes across continual-learning orchestration, RL action/movement behavior, campaign dialogue/presentation, combat targeting, and remaining gameplay, persistence, networking, and UI subsystems. REG-047 through REG-056 are fixed; focused regression tests for REG-047 through REG-052, REG-055, and REG-056 were added but not run. REG-053 and REG-054 correct exception-stack diagnostics and are documented in the regression log.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.
