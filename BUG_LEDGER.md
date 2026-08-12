# Bees Bug Ledger

Branch: `bug-audit/2026-08-12-0645`

Only defects validated by static code tracing are recorded here. No tests, builds, game executables, benchmarks, or GitHub Actions were run.

### BUG-001 — Profile state can still be split across progress, fleet, and squad files
**Location:** `Scripts/Data/UserData.cs`, `Scripts/Data/DataFile.cs`, `Scripts/Scenes/MainMenu.cs`, `Scripts/Levels/Level.cs`  
**Description:** Active campaign saves now route the three campaign persistence members through one transaction, but the underlying cross-file invariant is broader. `user_progress` owns global Fleet/SavedSquad IDs and mode progression while FreePlay/Challenge fleet and squad objects are written separately, and `ResetCampaign()` runs from a main menu whose default game mode is FreePlay so its reset writes can also bypass the campaign checkpoint. A process/network/database failure between these independent writes can persist a new fleet/squad snapshot without the matching global counters or persist progress without the matching mode snapshot, causing stale progression or reuse of already-persisted IDs after restart. The profile save boundary must be transactional for the corresponding progress + fleet + squad set regardless of which mode or reset path initiated the save.
