# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Shooting identity omits the delimiter required by server enemy-type parsing
**Location:** `Scripts/Server/CommandRequest.cs`, `CommandRequest()` / `GetStrategy.ShootingMatchup`; cross-repository BeesServer `siServerDev.js`, `getEnemyShipTypes()`  
**Description:** The acting-squad shooting identity is currently serialized as `ships|enemies` with only one `|`. BeesServer's shooting filter calls `getEnemyShipTypes()`, which extracts enemy composition with `substring(firstPipe + 1, lastPipe)` and therefore requires the normal `ships|enemies|` shape. With only one delimiter JavaScript `substring` swaps the reversed bounds and extracts the delimiter itself, so no real enemy ship type is detected and every type-specific shooting strategy is banned for these requests. The request-specific shooting identity must preserve the trailing matchup delimiter (and the server contract should continue receiving the same two-segment shape as the legacy parser expects).

Post-fix discovery pass 2 is still in progress. Because this pass found a new validated defect, the clean-pass count has reset to **0/2**.
