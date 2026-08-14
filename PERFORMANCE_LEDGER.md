# Performance Ledger

Static-only audit; no runtime measurements are claimed. This ledger contains unresolved validated optimization opportunities only.

### PERF-014 — Parse shooting matchup without splitting the full strategic key
**Location:** `Scripts/Server/CommandRequest.cs`, `BuildShootingMatchupIdentity()`  
**Cost:** Every Hive Mind `CommandRequest` reconstructs the shooting-policy identity by calling `string.Split('|')` on the full strategic matchup. The request needs only the second segment, so each command request allocates the split array and all parsed segment strings before allocating the required final `actingShips|enemyShips|` result. Dedicated Hive Mind training runs up to 16 simultaneous Levels and repeatedly creates command requests, making this avoidable GC work part of the training command hot path.  
**Optimization:** Locate the first and second delimiters directly and extract only the enemy segment, preserving the exact canonical trailing-delimiter result and current null/malformed-input behavior without materializing the full segment array.  
**Evidence:** `CommandRequest` invokes `BuildShootingMatchupIdentity()` in its constructor for every `GetStrategy` request. The current implementation uses `(strategicMatchup ?? string.Empty).Split('|')` and reads only index 1. Existing `ShootingMatchupProtocolTests` require `ABCDEF|XYZ|1|2 -> ABC|XYZ|` and preservation of an empty enemy segment; repository Hive Mind memory documents the canonical `actingShips|enemyShips|` shape and 16-Level training runtime.  
**Risk:** Preserve the exact second-segment interpretation when there is no delimiter, one delimiter, an empty enemy segment, extra trailing segments, or a null strategic matchup; do not change the protocol's required trailing `|`.

Clean static passes: 0 / 2.
