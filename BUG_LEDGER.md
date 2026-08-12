# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Training setup can replace and persist dead ships from the player's saved fleet
**Location:** `Scripts/Levels/Level.Reset.cs`, `ResetGameData()`; `Scripts/Ships.cs`, `ReplaceDeadSquadShips()`  
**Description:** `ResetGameData()` unconditionally calls `ConfigData.CurrentShips.ReplaceDeadSquadShips(...)` before every setup/reset, including dedicated Hive Mind training. Fully random training squads are transient, but `CurrentShips` still points at the player's persisted FreePlay fleet by default. If a persisted saved squad contains a dead member and a compatible available fleet replacement, `ReplaceDeadSquadShips()` removes/replaces that saved-squad member and immediately calls `SaveSquadData()`, which routes into the profile checkpoint. Starting or resetting automated training can therefore mutate and persist player fleet/squad data even though training should be simulation-only. Persisted-fleet dead-ship reconciliation must be skipped while `Stage.IsTraining`; training's transient random squads do not require it.

The latest complete post-fix static pass found this single new validated defect. The clean-pass count is **0/2**.
