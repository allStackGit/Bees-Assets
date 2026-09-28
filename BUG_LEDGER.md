# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

## Reported issues under investigation

- **Pluto II SSSS tooltip corruption (user-reported, not yet independently reproduced):** During the first multipage tooltip, a later page can render repeated lowercase `s` characters over time; turning the page away and returning reproduces it. The active tooltip path assigns each authored page and sets `maxVisibleCharacters` to `int.MaxValue`; the campaign guard only reads that text. The tracked Tooltip prefab's Message object has only its standard TextMeshProUGUI presentation components, with no attached reveal script. Static review has not found another tracked tooltip-text writer or a cause for the visible `s` output. REG-124's competing-writer fix is related but does not prove this report resolved. Runtime reproduction is unavailable under the static-only audit constraint.

## Audit status

- REG-087 was extended after elastic-path review found that rejected stale trajectory uploads also refreshed actor leases; elastic registration, reset acknowledgements, and uploads now use strict epoch checks before renewal. A focused stale/bool-epoch registration, acknowledgement, and upload cases were added but not run. The post-fix clean-pass count remains **0 / 2**.


- BUG-001 is resolved in both hybrid and zero-local initializer paths: broker validation now precedes local worker creation, and later startup failures close the broker and any created local manager. REG-154 records the fix and unrun regression coverage. The required post-fix clean-pass count remains **0 / 2**. Static review only.

- REG-153 hardens WAN actor and broker handling of boolean-shaped topology, policy, and epoch metadata; focused regression cases were added but not run. This production change leaves the post-fix clean-pass count at **0 / 2**. Static review only.


- REG-147 makes incompatible remote runtime cutover require a fresh stopped-trainer record whose `applied_revision` reaches the pending phase revision, matching the server's rollout barrier. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-146 treats any Unity worker exit before the full remote cohort completes as a failed distributed session, including a clean exit, then uses the common cleanup path to stop peers and the tunnel. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-145 keeps the continual telemetry watcher alive across configuration-load or store-initialization failures by retrying setup on the next scan interval. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-144 rejects boolean byte offsets and malformed non-object HTTP 409 response bodies in the training log client, preventing offset 1 from being inferred from `true` and keeping malformed conflicts within controlled protocol rejection. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-143 makes both training log readers verify that an opened handle still has the identity observed before selecting its cached cursor. This closes a stat/open rotation race that could misattribute episode metrics or append a replacement generation at the prior upload offset. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-142 adds an enemy-side guard at the shared weapon target validator, preserving the enemy-only rule even if a caller supplies a friendly candidate or range-cache ownership regresses. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-141 gives each rotated Unity episode-diagnostic log a new file identity and makes Python episode metrics reset their cursor on file replacement. This prevents a fast 8 MiB truncate-and-regrow from being mistaken for an append and splicing generations. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-140 retains the cluster probe lock while a failed environment-count probe is still rolling back to its measured baseline, even across subsequent unhealthy heartbeats. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-139 fixes a WAN actor spin after a late stale-upload response: when the broker confirms the actor already has current policy/control versions, the no-change sync path now clears stale/state-change signals. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-138 fixes oversized JSON and raw request handling: readers stop buffering and pause input at the limit; the handler returns HTTP 413 with `Connection: close` and destroys the request only after ending the response. This production change resets the post-fix clean-pass count to **0 / 2**. Static source review only; no tests or runtime checks were run.

- REG-137 caps server-side trainer logs at the same 64 MiB per-file limit as the uploader while preserving offset/reset semantics. Existing append/reset coverage remains valid; boundary protection is documented because tests were excluded by the static-only request. This production change resets the post-fix clean-pass count to **0 / 2**.

- REG-136 rejects symlinked or size-changed canonical artifacts at startup, opens downloads without following links where supported, validates the open file identity, and streams from that same handle. Worker-side SHA-256 verification remains. Existing tamper-on-reload coverage remains valid; a targeted symlink case is missing under the static-only constraint. This production change resets the post-fix clean-pass count to **0 / 2**.

- REG-135 rejects unrepresentable and non-finite control-plane lease values before the supervisor converts or uses them. The regression invariant is documented in `docs/engineering/REGRESSIONS.md`; no test was added or run under the static-only request. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-118 closes callback processing immediately after campaign level teardown and adds a source-level regression guard. This production change resets the post-fix clean-pass count to **0 / 2**. The regression guard was not run.

- ML-Agents launcher review found and fixed a `--results-dir` argument-boundary defect: the default is now inserted before `--env-args`, and Unity-side flags no longer count as trainer settings. A focused regression case and REG-103 documentation were added; neither was executed. Static source review only.
- Uranus II now resolves the simultaneous-elimination case once, honoring the campaign tie rule (player loss), and its post-close dialogue continuation is driven after normal level polling stops. Confirmed from the trigger loop, `CloseLevel`, and level update lifecycle; source changes only, no tests or runtime checks run.
- Neptune II's AI-elimination trigger now excludes simultaneous player elimination, preventing win/loss callbacks from both running in one trigger pass. Both loss/retreat callbacks explicitly record the AI winner before teardown, so tie losses are not left with an unset winner. This follows the same tie-as-player-loss rule; verified by static trigger-order analysis only.
- Uranus III now requires a surviving mobile player side as well as surviving barges for the success branch. Its immobile Barge does not prevent `IsSideKilled` from reporting the player side eliminated; source review confirmed that mismatch.
- Malformed profile recovery now catches a failed write of fallback defaults, keeps profile readiness false, and reports controlled unavailability rather than letting the storage exception escape. Source review only; no tests or runtime validation were performed.
- Complete clean finding passes since the latest production changes: **0 / 2**. BUG-001 is resolved as REG-089: authorization-denied profile reads now receive terminal failure status and are not retried. Regression cases were added but remain unrun.
- REG-094 closes a training-log path traversal by rejecting `.` and `..` trainer/run path components; focused source regression cases were added but remain unrun.
- REG-095 prevents transient gameplay telemetry draft persistence failures from discarding the active segment or mixing observations across deployment identities; a focused static source guard was added but remains unrun.
- REG-096 keeps valid pending RL telemetry available for later upload when a transient local read fails; the payload is quarantined only after content has been successfully read and found invalid. A focused static source guard was added but remains unrun.
- Current focus: broad post-fix passes across continual-learning orchestration, RL action/movement and telemetry behavior, campaign dialogue/presentation, combat targeting, and remaining gameplay, persistence, networking, and UI subsystems. REG-091 prevents long artifact preparation from suspending trainer lease renewal, forcing synchronous download during control reconciliation, or reusing stale prepared-build state. REG-093 removes shared mutable temporaries from request-hash generation to preserve unique IDs across concurrent callers. REG-076 closes a WAN policy snapshot version-attribution race; REG-077 closes a stale WAN capacity-rate report; REG-078 closes replacement-file log cursor corruption; REG-079 closes a worker-step recovery requeue race; REG-080 preserves PPO entropy scale under dimension masking; REG-081 fixes a terminal-tick score omission in the legacy Pluto IV method; the catalog-selected campaign implementation already handled this ordering. REG-082 makes per-ship identity observations reproducible under seeded RL evaluation; REG-083 seeds the per-arena policy coordinate-frame stream for reproducible evaluation transforms; REG-084 corrects the PPO action-layout guard to match the frozen 16-continuous, six-branch ABI; REG-085 clears deferred campaign triggers when rebuilding a mission graph. REG-086 fixes elastic WAN argument parsing that could consume Unity arguments after `--env-args`; REG-087 prevents stale reset acknowledgments from renewing actor leases. The public native-demo quarantine handoff was also audited; REG-074 closes a staged-file integrity gap. REG-047 through REG-088 are fixed and recorded in the regression log. REG-088 moves delayed strategy-response recording behind the live level/squad ownership check; REG-089 makes authorization-denied profile reads terminal; REG-090 guards beehive collision and destruction callbacks against squadless RL healing reservations. REG-097 closes a learner-startup cleanup gap that could leak ML-Agents monkey patches after setup failure. REG-098 closes a WAN broker listener leak on serving-thread startup failure. REG-099 closes managed remote-supervisor startup and cleanup gaps that could leave stale PID/stream state. REG-100 ensures optimizer failure handling runs during environment-count restarts. REG-101 rechecks verified bundle metadata at each RL model chunk read. REG-102 releases unused reserved bytes when incomplete telemetry uploads expire. Regression protections for REG-047 through REG-052 and REG-055 through REG-096 were added but not run; REG-097 is documented as a source invariant without a test, per the static-only scope. REG-053 and REG-054 correct exception-stack diagnostics and are documented in the regression log.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.

- REG-119 fixes POSIX owned-child cleanup returning before signal-status re-delivery when no process group remains. Verified statically; no tests or runtime checks were performed. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-120 moves remote SSH tunnel creation inside installed signal handling and its cleanup scope, preventing a stop during startup from orphaning the tunnel. Verified statically only; no tests or runtime checks were performed. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-121 fixes stale pre-reset worker results being published after full-cohort recovery; all workers now resume from reset observations. Verified through static comparison with the pinned ML-Agents recovery/reset flow and response postprocessing. No tests or runtime checks were run. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-122 sanitizes externally configured Unity behavior names before using them in diagnostic model filenames, with a digest suffix to preserve distinct names. Verified statically against the pinned ML-Agents `BehaviorParameters` property and the snapshot export path; no tests or runtime checks were run. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-123 fixes launcher option parsing across the ML-Agents `--env-args` remainder delimiter; Unity-side tokens now pass through unchanged. Verified statically against the pinned parser declaration. No tests or runtime checks were run. This production fix resets the post-fix clean-pass count to **0 / 2**.

- REG-029/124 now remove the Pluto II tooltip's per-frame text correction entirely and author the plural range wording in its source page. Added a focused static source guard for the text-ownership contract; it has not been executed. Static call-path review confirms the campaign guard no longer writes tooltip text and page navigation reloads the corrected source string. No tests or runtime checks were run. The post-fix clean-pass count is **0 / 2**.

- REG-125 adds a SHA-256 prefix comparison to training-log offset recovery. Matching prefixes may resume; mismatched or unavailable prefixes fail closed without overwriting the server copy. Verified statically across client, server, and uploader. No tests or runtime checks were run. The post-fix clean-pass count is **0 / 2**.

- REG-126 makes desired-state patches transactional: all fields are validated before state mutation, and persistence failure restores the prior state object. Verified statically in the BeesServer control store. No tests or runtime checks were run. The post-fix clean-pass count is **0 / 2**.


- REG-127 restores in-memory control state when state persistence fails across rollout transitions, release staging, artifact catalog changes, and dedicated heartbeats. Rollout snapshots are taken only at mutation points to avoid cloning build catalogs on every poll. Verified statically; no tests or runtime checks were run. The post-fix clean-pass count is **0 / 2**.

- REG-128 clears RL ally-communication values when pooled ships reset, preventing observations from reading a previous ship lifecycle. A focused static source assertion was added and not run. Verified from `Ship.Setup`/`ClearData`, agent cleanup, and ally-slot observation flow. No runtime checks were run. The post-fix clean-pass count is **0 / 2**.

- REG-129 removes unreferenced canonical build copies when artifact state persistence fails or a build-ID immutability check rejects a new copy; temporary staging files are also cleaned on copy/rename errors. Added focused Node regression assertions; not run. Verified by static tracing of copy, catalog mutation, rollback, and cleanup. The post-fix clean-pass count is **0 / 2**.

- REG-130 ensures a dedicated heartbeat rejected by trainer-registry persistence does not mutate environment-count optimizer state. Added a focused Node regression case, not run; reviewed statically. The post-fix clean-pass count is **0 / 2**.

- REG-131 prevents a managed elastic actor from being stopped solely because Unity/policy startup exceeds the process-age grace while startup health is actively refreshed. Stale or missing startup health remains a failure. Added focused Python regression cases; not run. Static review only. The post-fix clean-pass count is **0 / 2**.
