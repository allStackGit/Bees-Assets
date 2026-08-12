# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Authorization failures can be mistaken for missing user data
**Location:** `Scripts/Server/Socket.cs`, `Message()` / `HandleUserDataResponse()`; `Scripts/Data/DataFile.cs`  
**Description:** `HandleUserDataResponse()` decides that a remote file is missing solely from empty `Filename`/`Contents` and never checks the response `Status`. Production BeesServer authentication/authorization failures such as 401/403 are basic responses containing only type/hash/status, so they enter the missing-file branch. The client then creates default profile data in memory and calls `WriteData()`, which can attempt to persist those defaults. Authentication or other error responses must remain retry/error states and must never invoke the missing-row initialization path.

### BUG-002 — A mixed stale/valid StoreCommands batch can permanently lose valid rewards
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`, `ShouldKeepWriteRequestPending()`; `Scripts/Server/Socket.cs`, `HandleBasicResponse()`; `Scripts/Levels/GameState.Commands.cs`, `StoreCommands()`; cross-repository BeesServer `outcomeReservations.js`  
**Description:** one `StoreCommands` request carries every finalized outcome for the level. BeesServer aborts the entire batch with 409 if even one outcome ID is stale and reports the stale subset, but the client treats every StoreCommands 409 as terminal, does not deserialize `StaleOutcomeIds`, and retires the entire standing request. Because the server wrote none of the batch, every otherwise-valid outcome in that request is then lost. The protocol must discard/reject only stale IDs and retry or persist the valid subset.

### BUG-003 — Shooting learning identity is fragmented by nearby allied ships
**Location:** `Scripts/Levels/Squad.Commands.cs`, `MakeMatchupAndGetCommand()`; `Scripts/Levels/Squad.Combat.cs`, `GetPotentialAllies()`; cross-repository BeesServer shooting-key derivation  
**Description:** the strategic matchup concatenates the acting squad and nearby allied ships into the first segment before the server derives its shooting key from the first two matchup segments. Consequently identical acting-squad/enemy shooting situations hash to different shooting matchups when unrelated nearby allies differ. This fragments shooting outcome history and prevents the target-priority policy from learning/generalizing over the intended acting-squad + enemy composition. Ally context should remain available to strategic-command learning while shooting identity is versioned from the acting squad and enemies only.

### BUG-004 — Automated Hive Mind training excludes obstacle states used by learned policies in normal play
**Location:** `Scripts/Levels/Level.cs`, `RandomizeOptions()`; `Scripts/Scenes/HiveMindTrainingBootstrap.cs`  
**Description:** `RandomizeOptions()` gates both static-obstacle and collision-asteroid selection with `!Stage.IsTraining`, so dedicated Hive Mind training never samples those environments. The strategic matchup key also contains no obstacle context, while the resulting movement-command values are later reused in ordinary battles that can contain obstacles. Training therefore learns obstacle-free command values and applies them to materially different obstacle-heavy states. The curriculum/key contract must explicitly represent obstacle context and/or train representative obstacle scenarios rather than silently excluding them.

### BUG-005 — Pooled ships can inherit cease-fire from a previous squad
**Location:** `Scripts/Entities/Ships/Ship.Lifecycle.cs`, `ClearData()` / `Setup()`; `Scripts/Levels/LevelConstructor.cs`, `SpawnShipsAndSquads()`; `Scripts/Levels/Squad.cs`, `SetSquadCeaseFire()`  
**Description:** `Ship.ClearData()` does not reset `IsCeaseFire`. During spawning, `SpawnShipsAndSquads()` only calls `squad.SetSquadCeaseFire(true)` when the new squad's `CeaseFire` flag is true; a normal squad never explicitly writes `false` to its ships. A pooled ship that previously belonged to a cease-fire squad can therefore be reused in a normal squad with `IsCeaseFire` still true, causing its weapons to suppress firing until some later user/UI action happens to clear the flag. Each pooled lifecycle must initialize the ship cease-fire state from the new squad (or reset it to false before setup).
