# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Generated-squad compatibility retains destroyed Stages forever
**Location:** `Scripts/Scenes/GeneratedSquadMinimumCompatibility.cs`, static `AdjustedStages` / `HandleSceneLoaded`  
**Description:** Every scene-loaded `Stage` with a positive generated-squad minimum is inserted into the process-wide static `HashSet<Stage> AdjustedStages`, but entries are never removed. Gameplay scene unload destroys the Stage's Unity object while the static set continues holding its managed wrapper, so repeated scene loads monotonically retain dead Stage references for the lifetime of the process. The set only needs to prevent a duplicate adjustment during one live Stage lifecycle; it must not own destroyed scene objects indefinitely.

Post-fix discovery pass 1 is still in progress. Because this pass found a new validated defect, the clean-pass count remains **0/2**.
