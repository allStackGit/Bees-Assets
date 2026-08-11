# Bees audit checkpoint — 2026-08-11

## Status

- Full-audit clean-pass count: **0/2**. The current first pass is still dirty because it continued finding confirmed defects.
- This is a **stable stopping point**, not a readiness claim. Do not call the project qualification/training/production ready from this checkpoint.
- Authoritative reference SQL for this project is **`bees_test.sql`**. Do not use the replaced `ram.sql` for current conclusions.
- Current stable heads at this checkpoint:
  - `allStackGit/Bees-Assets`: `dbda0ea6651017930c7e3aae58db2bf55cd32976`
  - `allStackGit/BeesServer`: `bb246e11d787ce29fe9240d18155dd5680f94e9c`
- Neither head had GitHub CI status attached when this checkpoint was written. Committed regression tests are not equivalent to executed validation.

## Development rule reinforced

- Oversized files are never a reason to defer a confirmed fix. If connector/file size makes a change risky, first split the component into cohesive partials/helpers, preserve behavior, and then make the targeted fix in the smaller owner.
- Apply this immediately to remaining oversized owners such as `ConfigData`, `Level`, `Stage`, `Utilities`, and `Ships` when size interferes with safe editing.

## Important fixes completed during this audit stretch

Server/runtime fixes include:

- User-data/settings DB read failures no longer masquerade as missing data.
- Consolidation read/transaction/aggregation failures no longer wedge the service; retry now backs off instead of hot-looping.
- Consolidation now preserves exact total reward/use counts, preserves work added during an active pass, and only triggers when an individual strategy exceeds the row threshold.
- Strategy caches are invalidated after durable writes and cache availability is scoped to request-local strategy availability.
- Missing/malformed reconstructible cache files no longer prevent startup.
- Multiple Levels on one socket reuse one Game; expired-Game sibling reconnects reuse the replacement Game.
- Pending outcome IDs cannot silently overwrite one another inside a live Game.
- Cross-client request-hash collisions are removed by scoping pending request dedupe to `(connectionId, hash)`.
- Corrected targeting/shooting learning uses clean `target-v2:` / `shoot-v2:` namespaces so legacy contaminated rows do not bias new training.
- Minimum-use exploration aggregates all strategies before deciding which are underused.
- Client/server discard protocol removes intentionally unused secondary outcome reservations instead of retaining them for the full two-hour expiry.
- Settings-user-ID comparisons normalize string/numeric representations.
- Schema migration widens `settings.userId` and lifetime learning `uses` counters; live-schema qualification checks those widths.

Unity/training fixes include:

- Dense enemy and ally matchup capping is deterministic instead of collection-order dependent.
- Composition command bans refresh after ship removal as well as addition.
- Temporary Mining/Heal/FullRetreat unavailability no longer becomes a permanent squad ban.
- Dedicated Hive Mind training suppresses direct missing-file default persistence as well as ordinary `UserData.Save()` writes.
- Barge-only squads now keep one canonical Hive Mind attack policy rather than training several server IDs that all execute Charge.
- The accidental duplicate `RefreshCompositionCommandBans()` partial-class method introduced while fixing barges was removed; the active implementation is in `Squad.Movement.cs` and the regression guards against duplicate definitions.

## Confirmed open blockers / continuation items

Resume the first audit pass from these items, in roughly this order:

1. **Decompose `ConfigData.cs` and fix the confirmed production runtime defects rather than deferring them:**
   - `GetUserId()` returns before the Steam branch, making Steam identity unreachable.
   - Before enabling Steam identity, verify/fix `FirstTimePlaying` semantics: the unreachable branch currently assigns `FirstTimePlaying = HasPlayedBefore()`, while user-data setup passes `!FirstTimePlaying` as `shouldFileExist`.
   - `ConfigData.Socket` chooses only Test vs Development; Production hostname/port is never constructed.
   - `ShootingStrategyNames` combines `"Type V, Type W, Type X"` into one entry, so individual custom hotkeys for V/W/X are not recognized by `LevelInputManager`.
   - Built-player cache path uses `BaseFolder` instead of `CacheFolder`; desktop non-editor paths also use `Application.dataPath`. Current version-5 configuration has local storage/mirroring disabled, so the save-path problem is dormant, while the cache-folder boundary remains wrong when caching is used.

2. **Decompose/fix `Level.cs` production build issue:** remove unconditional runtime `using UnityEditor;` (no actual UnityEditor usage was found).

3. **Decompose/fix `Stage.cs` random squad-count off-by-one:** a positive configured minimum is excluded (`minimum + 1 .. maximum`), so the intended smallest training battles never occur.

4. **Handled-response hash lifetime:** `Socket.HandledRequests` globally claims all response hashes, but only Level-owned strategy responses are reliably removed. Repeated `StoreCommands`/basic responses and stale rejected squad responses can accumulate for the life of a process. Fix with bounded/time-based response dedupe; do not simply remove hashes immediately, because late duplicate responses must still be suppressed.

5. **Server restart reward durability / idempotency:** a server restart destroys in-memory pending outcome metadata. Adding matchup/strategy metadata to `StoreCommands` alone is insufficient because a transaction may have committed before the crash while its acknowledgement was lost, causing replay to double-count. Correct fix requires a durable reward-event/idempotency key/receipt so retry after restart is exactly-once.

6. **Shooting-key ally fragmentation:** current strategic wire matchup mixes acting ships and nearby allies before the server derives the shooting key. Correcting this should preserve ally context for strategic learning while deriving shooting identity from the acting squad + enemy composition only. Treat this as a coordinated wire/key version change, not a lossy server-only workaround.

7. **Training distribution vs obstacles:** automated Hive Mind training disables static/collision obstacles while obstacle presence is not represented in the strategic matchup key. This means movement-command values learned obstacle-free are applied in obstacle-heavy real battles. Decide explicitly whether to add obstacle context to the key and/or include obstacle scenarios in the training curriculum; do not enable obstacles blindly without considering cost/key semantics.

8. **Deployment migration:** current authoritative `bees_test.sql` still has `settings.userId INT` and learning `uses MEDIUMINT UNSIGNED`. Run the guarded migration before relying on exact Steam64 per-user settings or long-run counter capacity. Production migration requires the existing explicit production opt-in.

## Validation facts

- Focused extracted server-module tests were executed for some database/persistence changes during this audit, including the database normalization path and exact consolidation arithmetic/property checks.
- The complete BeesServer test suite and Unity EditMode/PlayMode/release gate were **not** executed for the final combined repository heads at this checkpoint.
- Therefore the next session must not count this as a clean audit pass merely because the repositories are in a committed/stable state.

## Audit continuation rule

Continue fixing confirmed bugs during the first full pass. Only after an entire fresh pass finds **no new bugs** should the clean-pass counter become 1/2. Then restart an independent second full pass; only a second consecutive full pass with no bugs reaches 2/2.