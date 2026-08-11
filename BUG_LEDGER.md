# Bees Static Bug Audit Ledger

Branch: `bug-audit/static-2026-08-11`

This ledger records only defects validated by static code tracing. No tests, builds, simulations, executables, or GitHub Actions were run for this audit.

### BUG-001 — Steam identity path is unreachable
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `ConfigData.GetUserId()`  
**Description:** `GetUserId()` always returns the PlayerPrefs/random ID before the Steam initialization branch. Consequently a running Steam build can never use `SteamUser.GetSteamID()`, so server/user-data identity is permanently detached from the player's Steam64 identity.

### BUG-002 — Production socket configuration falls through to development
**Status:** Open  
**Location:** `Scripts/ConfigData.cs`, lazy `ConfigData.Socket` getter  
**Description:** Socket construction is `Test ? test : development`; `Production`, `ProductionServerHostname`, and `ProductionPort` are never selected. A production build made by disabling both Test and Development therefore connects to the development endpoint instead of the production endpoint.

### BUG-003 — Shooting hotkeys for ship types V/W/X cannot bind
**Status:** Open  
**Location:** `Scripts/ConfigData.Gameplay.cs`, `ShootingStrategyNames`; `Scripts/Levels/LevelInputManager.cs`, `LoadHotKeySettings()`  
**Description:** `ShootingStrategyNames` contains one combined string `"Type V, Type W, Type X"` instead of three names. `LoadHotKeySettings()` binds type-specific shooting hotkeys only when the exact hotkey name is in that set, so `Type V`, `Type W`, and `Type X` settings never receive an action.

### BUG-004 — Desktop player save files use the application install directory
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetBasePath()`  
**Description:** In the non-editor, non-mobile branch, mutable save data is placed under `Application.dataPath/SaveData` rather than `Application.persistentDataPath`. When local storage is enabled on a desktop install whose application directory is read-only or replaced during updates, normal save creation/writes can fail or be lost with the installation.

### BUG-005 — Mobile cache path is routed into SaveData
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetCachePath()`  
**Description:** Android/iPhone cache branches build their path with `BaseFolder` (`SaveData`) instead of `CacheFolder` (`SpriteCache`). Sprite-cache files therefore share the persistent save-data directory rather than the cache directory, breaking the intended save/cache storage boundary whenever sprite caching is used on mobile.

### BUG-006 — Runtime Level source has an unconditional UnityEditor dependency
**Status:** Open  
**Location:** `Scripts/Levels/Level.cs`, imports  
**Description:** `Level.cs` again unconditionally imports `UnityEditor` even though it is runtime gameplay code and the file does not require editor-only APIs for ordinary gameplay. This reintroduces a player-build compilation dependency on the editor-only UnityEditor assembly.

### BUG-007 — Random squad generation excludes the configured minimum
**Status:** Open  
**Location:** `Scripts/Scenes/Stage.cs`, generated enemy squad-count override logic; `Scripts/Utilities.cs`, `RandomInt()`  
**Description:** For a positive `GeneratedSquadCountMinimum`, Stage computes `RandomInt(max - min) + 1 + min`. `RandomInt(n)` returns `[0,n)`, so the resulting range is `[min + 1, max]`; the configured minimum is never generated. Training/runtime distributions therefore omit the smallest intended battle size.

### BUG-008 — Failed basic write responses are treated as completed writes
**Status:** Open  
**Location:** `Scripts/Server/Socket.cs`, `HandleBasicResponse()`; `Scripts/Data/DataFile.cs`, `WriteData()`; `Scripts/Levels/GameState.Commands.cs`, `StoreCommands()`  
**Description:** `HandleBasicResponse()` removes a standing request without checking `ServerResponse.Status`. `StoreUserData` and `StoreCommands` failures are therefore retired exactly like successes if a non-success basic response is received. `DataFile.WriteData()` already marks the new value loaded, and `GameState.StoreCommands()` clears stored outcomes immediately after sending, so such a response can permanently lose player persistence or Hive Mind reward data instead of remaining retryable. Current BeesServer normally leaves failed database writes pending rather than emitting a non-success basic response, so this client defect is dormant under that server behavior but remains unguarded.

### BUG-009 — Global handled-response hashes grow without a bounded lifetime
**Status:** Open  
**Location:** `Scripts/Server/Socket.cs`, `TryClaimResponse()` / `HandleBasicResponse()` and response dispatch  
**Description:** Every response hash is inserted into the socket-global `HandledRequests` set by `TryClaimResponse()`. Level-owned strategy/setup responses have level ownership for later cleanup, but basic responses such as repeated `StoreCommands`/`StoreUserData` do not. Those hashes therefore accumulate for the process lifetime, producing unbounded memory growth in long-running/training sessions while also retaining stale dedupe identities indefinitely.

### BUG-010 — Campaign completion is persisted as a non-atomic three-file checkpoint
**Status:** Open  
**Location:** `Scripts/Levels/Level.Campaign.Endings.cs`, `SaveCampaignProgress()`  
**Description:** Campaign endings mutate progression and fleet/squad state, then `SaveCampaignProgress()` independently sends `UserProgressData.Save()`, `SaveSquadData()`, and `SaveFleetData()`. These are separate asynchronous persistence requests with no shared checkpoint/version/transaction, so interruption or partial server success can persist an advanced campaign level while retaining an older fleet or squad snapshot, leaving the next session in a state that never existed in memory.

### BUG-011 — Steam first-run flag is inverted
**Status:** Open  
**Location:** `Scripts/ConfigData.Runtime.cs`, `GetUserId()`, `HasPlayedBefore()`, and `SetupUserData()`  
**Description:** The Steam branch assigns `FirstTimePlaying = HasPlayedBefore()`, while `HasPlayedBefore()` returns true for an existing player and `SetupUserData()` passes `!FirstTimePlaying` as `shouldFileExist` to progress/fleet/squad/settings loaders. If the unreachable Steam branch in BUG-001 is enabled as written, returning players are therefore treated as first-time users and their existing files are not expected/loaded normally, while genuinely new Steam players are treated as though files should already exist. The assignment must be the inverse of `HasPlayedBefore()` (or the surrounding semantics renamed/reworked) before Steam identity can be safely activated.

### BUG-012 — Static scene registry retains every destroyed scene
**Status:** Open  
**Location:** `Scripts/ConfigData.cs`, `ConfigData.Scenes`; `Scripts/Scenes/Scene.cs`, `Start()`  
**Description:** Every `Scene` instance adds itself to the process-wide static `ConfigData.Scenes` list in `Start()`, but there is no corresponding removal anywhere in the repository. Unity destroys scene objects during normal scene changes, yet the static list keeps their managed `MonoBehaviour` wrappers strongly referenced. Repeated Main Menu/Squad Maker/Space/intro transitions therefore grow this registry for the lifetime of the process and retain obsolete scene wrappers (and their managed field graph) instead of allowing collection.

### BUG-013 — Hive Mind type-target bans use the player's unlock set instead of the strategy catalog
**Status:** Open  
**Location:** `Scripts/Data/UserProgressData.cs`, `AllShipTypes` initialization / `SetShipTypes()`; `Scripts/Levels/Squad.Commands.cs`, `MakeMatchupStrat()`; `Scripts/Levels/Commands/MatchupStrategy.cs`, `SortSquads()`  
**Description:** `AllShipTypes` is initialized only from the player's currently visible ship types plus Beacon/Drone/Striker, and `SetShipTypes()` does not rebuild it as more types become visible. `MakeMatchupStrat()` incorrectly treats that incomplete set as the universe from which absent enemy types should be banned. Type-specific strategies for omitted catalog types are therefore left eligible even when no enemy ship of that type exists. If the server selects one, `MatchupStrategy.SortSquads()` sorts every candidate with a zero count and returns the first visible squad, so the recorded/rewarded type-specific policy actually executes an arbitrary-target policy. This both produces incorrect behavior and contaminates Hive Mind targeting learning/outcome attribution.
