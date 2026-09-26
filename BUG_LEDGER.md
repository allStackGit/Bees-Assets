# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

The WAN actor empty-trajectory-queue crash and capped training-log flush stall were fixed. The focused WAN queue regression was added; the existing capped-upload regression was reviewed against the fix. Neither test was executed, per the audit's static-only constraint.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.

### BUG-001 — Discarded WAN batches remain marked accepted
**Location:** `Training/bees_wan_actor_training.py`, `WanActorBroker._discard_queued_batches_locked` and `submit_trajectory_batch`  
**Description:** A batch is entered into `_accepted_batch_ids` as soon as it is queued. A policy or control change clears queued/pending trajectories, but leaves those IDs remembered. If an actor did not receive the original HTTP response and retries the same `batch_id`, `submit_trajectory_batch` returns the remembered count before checking its now-stale policy/control epoch. The actor treats the retry as accepted even though the learner has discarded the batch, silently losing on-policy experience.
