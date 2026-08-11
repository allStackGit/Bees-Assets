# Bees Static Bug Audit Ledger

Branch: `bug-audit/static-2026-08-11`

This ledger records only defects validated by static code tracing. No tests, builds, simulations, executables, or GitHub Actions were run for this audit.

### BUG-001 — Steam identity path is unreachable
**Location:** `Scripts/ConfigData.cs`, `ConfigData.GetUserId()`  
**Description:** `GetUserId()` always returns the PlayerPrefs/random ID before the Steam initialization branch. Consequently a running Steam build can never use `SteamUser.GetSteamID()`, so server/user-data identity is permanently detached from the player's Steam64 identity.

### BUG-002 — Production socket configuration falls through to development
**Location:** `Scripts/ConfigData.cs`, lazy `ConfigData.Socket` getter  
**Description:** Socket construction is `Test ? test : development`; `Production`, `ProductionServerHostname`, and `ProductionPort` are never selected. A production build made by disabling both Test and Development therefore connects to the development endpoint instead of the production endpoint.

### BUG-003 — Shooting hotkeys for ship types V/W/X cannot bind
**Location:** `Scripts/ConfigData.cs`, `ShootingStrategyNames`; `Scripts/Levels/LevelInputManager.cs`, `LoadHotKeySettings()`  
**Description:** `ShootingStrategyNames` contains one combined string `"Type V, Type W, Type X"` instead of three names. `LoadHotKeySettings()` binds type-specific shooting hotkeys only when the exact hotkey name is in that set, so `Type V`, `Type W`, and `Type X` settings never receive an action.

### BUG-004 — Desktop player save files use the application install directory
**Location:** `Scripts/ConfigData.cs`, `GetBasePath()`  
**Description:** In the non-editor, non-mobile branch, mutable save data is placed under `Application.dataPath/SaveData` rather than `Application.persistentDataPath`. When local storage is enabled on a desktop install whose application directory is read-only or replaced during updates, normal save creation/writes can fail or be lost with the installation.

### BUG-005 — Mobile cache path is routed into SaveData
**Location:** `Scripts/ConfigData.cs`, `GetCachePath()`  
**Description:** Android/iPhone cache branches build their path with `BaseFolder` (`SaveData`) instead of `CacheFolder` (`SpriteCache`). Sprite-cache files therefore share the persistent save-data directory rather than the cache directory, breaking the intended save/cache storage boundary whenever sprite caching is used on mobile.

### BUG-006 — Runtime Level source has an unconditional UnityEditor dependency
**Location:** `Scripts/Levels/Level.cs`, imports  
**Description:** `Level.cs` unconditionally imports `UnityEditor` even though it is runtime gameplay code and no UnityEditor API is used there. Because the gameplay assembly is included in player builds while UnityEditor is editor-only, this source creates a player-build compilation dependency on an unavailable editor assembly.

### BUG-007 — Random squad generation excludes the configured minimum
**Location:** `Scripts/Scenes/Stage.cs`, generated enemy squad-count override logic; `Scripts/Utilities.cs`, `RandomInt()`  
**Description:** For a positive `GeneratedSquadCountMinimum`, Stage computes `RandomInt(max - min) + 1 + min`. `RandomInt(n)` returns `[0,n)`, so the resulting range is `[min + 1, max]`; the configured minimum is never generated. Training/runtime distributions therefore omit the smallest intended battle size.

### BUG-008 — Failed basic write responses are treated as completed writes
**Location:** `Scripts/Server/Socket.cs`, `HandleBasicResponse()`; `Scripts/Data/DataFile.cs`, `WriteData()`; `Scripts/Levels/GameState.Commands.cs`, `StoreCommands()`  
**Description:** `HandleBasicResponse()` removes a standing request without checking `ServerResponse.Status`. `StoreUserData` and `StoreCommands` failures are therefore retired exactly like successes. `DataFile.WriteData()` already marks the new value loaded, and `GameState.StoreCommands()` clears the stored outcomes immediately after sending, so a server-side write failure can permanently lose player persistence or Hive Mind reward data instead of remaining retryable.

### BUG-009 — Global handled-response hashes grow without a bounded lifetime
**Location:** `Scripts/Server/Socket.cs`, `TryClaimResponse()` / `HandleBasicResponse()` and response dispatch  
**Description:** Every response hash is inserted into the socket-global `HandledRequests` set by `TryClaimResponse()`. Level-owned strategy/setup responses have level ownership for later cleanup, but basic responses such as repeated `StoreCommands`/`StoreUserData` do not. Those hashes therefore accumulate for the process lifetime, producing unbounded memory growth in long-running/training sessions while also retaining stale dedupe identities indefinitely.
