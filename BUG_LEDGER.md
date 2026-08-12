# Bees Bug Ledger

Branch: `bug-audit/2026-08-12-0645`

Only defects validated by static code tracing are recorded here. No tests, builds, game executables, benchmarks, or GitHub Actions were run.

### BUG-001 — Successful server write acknowledgements are treated as failures
**Status:** Open  
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`; BeesServer `store-commands` / `store-user-data` response contract  
**Description:** The response lifecycle guard only recognizes `Status == 1` as a successful basic write. BeesServer returns HTTP-style `Status: 200` for successful `store-commands` and `store-user-data` requests. The guard therefore swallows valid acknowledgements, leaves the standing request pending, and allows timeout/resend logic to repeat writes the server already committed. The accompanying static regression test encodes the same incorrect `Status == 1` assumption.

### BUG-002 — Campaign progression is saved before the atomic campaign checkpoint
**Status:** Open  
**Location:** `Scripts/Data/UserProgressData.cs`, `Scripts/Levels/Level.Campaign.Endings.cs`, `Scripts/CampaignCheckpoint.cs`  
**Description:** Campaign mission endings call `AdvanceToNextLevel()`, whose `SetCurrentLevel()` immediately saves `user_progress`, and only afterward call the atomic campaign checkpoint that stores `user_progress`, campaign squads, and campaign fleet together. If the first write succeeds and the checkpoint fails, campaign progression can advance while the corresponding fleet/squad state remains old, recreating the split-brain persistence state the checkpoint was introduced to prevent.
