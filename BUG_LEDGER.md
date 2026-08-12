# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Dedicated Hive Mind training increments and saves player win progression
**Location:** `Scripts/Levels/Level.Runtime.cs`, `LevelOver()`; `Scripts/Scenes/HiveMindTrainingBootstrap.cs`; `Scripts/Scenes/Stage.cs`  
**Description:** Dedicated Hive Mind training sets `IsTrainingHiveMind = true`, `IsTrainingNueralNetwork = false`, and Stage later derives `IsTraining = true`. The dedicated runtime normally retains the default `ConfigData.CurrentGameMode = FreePlay`. `LevelOver()` enters its non-neural branch, computes a winner, then increments the corresponding FreePlay/Challenge/FishTank player win counter and calls `ConfigData.UserProgressData.Save()` before it reaches the later Hive Mind-training `SaveAndEnd()` branch. Consequently each completed automated training episode can mutate and persist player progression (normally FreePlay wins) even though training is simulation state and no player is controlling the episode. Winner calculation must remain available for training/outcome handling, but player counters, score UI, player-specific win state, and profile persistence must be suppressed while `Stage.IsTraining`.

The latest full static discovery pass completed with this single new validated defect. Because a defect was found, the clean-pass count is **0/2**.
