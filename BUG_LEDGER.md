# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

### BUG-001 — Authorization-denied profile reads remain pending and retry forever
**Status:** Open  
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`, `Scripts/Server/Socket.cs`, `Scripts/Data/DataFile.cs`, `Scripts/Settings/ServerSettings.cs`, `BeesServer~/security.js`  
**Evidence:** production security returns status 403 when a request's claimed user ID differs from the authenticated Steam identity. The response guard's data-read branch intercepts every status >= 400 and leaves the standing request in place. `Socket.CheckForResends` therefore resends it; the data-file/settings waiters have no terminal 403 state and cannot complete or surface an error. The documented response-lifecycle policy identifies 403 as terminal.  
**Impact:** a stale or mismatched identity can keep profile/settings bootstrap unresolved while generating repeated denied requests. The read is not incorrectly converted to empty state, but it has no terminal recovery path.  
**Required correction:** make 403 terminal for profile/settings reads, stop resending, and propagate a failure state to their owners without treating the response as missing data. Add focused client regression coverage; keep database/authentication errors distinct from first-run absence.

## Audit status

- Complete clean finding passes since the latest production changes: **0 / 2**. BUG-001 was found in the socket/profile-read 403 lifecycle; no fix or runtime validation is claimed yet.
- Current focus: broad post-fix passes across continual-learning orchestration, RL action/movement and telemetry behavior, campaign dialogue/presentation, combat targeting, and remaining gameplay, persistence, networking, and UI subsystems. REG-076 closes a WAN policy snapshot version-attribution race; REG-077 closes a stale WAN capacity-rate report; REG-078 closes replacement-file log cursor corruption; REG-079 closes a worker-step recovery requeue race; REG-080 preserves PPO entropy scale under dimension masking; REG-081 fixes a terminal-tick score omission in the legacy Pluto IV method; the catalog-selected campaign implementation already handled this ordering. REG-082 makes per-ship identity observations reproducible under seeded RL evaluation; REG-083 seeds the per-arena policy coordinate-frame stream for reproducible evaluation transforms; REG-084 corrects the PPO action-layout guard to match the frozen 16-continuous, six-branch ABI; REG-085 clears deferred campaign triggers when rebuilding a mission graph. REG-086 fixes elastic WAN argument parsing that could consume Unity arguments after `--env-args`; REG-087 prevents stale reset acknowledgments from renewing actor leases. The public native-demo quarantine handoff was also audited; REG-074 closes a staged-file integrity gap. REG-047 through REG-088 are fixed and recorded in the regression log. REG-088 moves delayed strategy-response recording behind the live level/squad ownership check. Regression protections for REG-047 through REG-052 and REG-055 through REG-088 were added but not run; REG-053 and REG-054 correct exception-stack diagnostics and are documented in the regression log.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.
