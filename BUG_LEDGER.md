# Bug Ledger

Static-only repository audit on `rl/initial-design-work`. This ledger tracks validated, unresolved defects for the active audit; fixed regressions belong in `docs/engineering/REGRESSIONS.md`.

### BUG-001 — Capped training logs remain permanently pending
**Location:** `Training/bees_training_worker_agent.py`, `TrainingLogUploader._has_pending_local_bytes`  
**Description:** when a log exceeds `MAX_FILE_UPLOAD_BYTES`, the uploader intentionally stops at the cap, but pending-byte detection compares the capped upload position with the full file size. `flush_all` then repeats without progress and raises after its pass limit. The existing `test_training_log_uploader_caps_each_uploaded_file` exercises an eight-byte file with a four-byte cap and calls `flush_all`.

Finding passes: 0 / 2 consecutive clean full-code passes after the latest production fix. The repository-wide audit remains in progress; no full-pass completion is claimed.
