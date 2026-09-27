# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger is the current work queue for validated unresolved defects; fixed findings and permanent protections are recorded in `docs/engineering/REGRESSIONS.md`.

## Active validated defects

### BUG-001 — Elastic WAN topology growth rejects queued rollouts
**Location:** `Training/bees_elastic_wan_actor_session.py`, `_apply_live_rollout_horizons`; `Training/bees_elastic_wan_training.py` and `Training/bees_elastic_wan_zero_local.py`, `_inject_remote_batches`  
**Description:** When remote workers join, the actor lowers its dynamic rollout horizon, but already-completed trajectories may remain in its upload queue or the central learner queue with the previous, larger horizon. The learner then compares those in-flight trajectories against the new smaller `AgentManager._max_trajectory_length` and raises, failing training even though the batch was produced under the same policy/control epoch and remains within the configured ML-Agents `time_horizon`.

## Audit status

- Complete clean finding passes since the latest production changes: **0 / 2**.
- Current focus: broad post-fix passes across continual-learning orchestration, campaign dialogue/presentation, combat targeting, and remaining gameplay, persistence, networking, and UI subsystems. REG-047 through REG-051 are fixed; focused regression tests were added but not run.
- Test fixtures requiring explicit process identities were updated; an orderly-close lifecycle regression case was added.
- No tests, builds, Unity, simulations, or other runtime validation were run, per the static-only audit scope.
