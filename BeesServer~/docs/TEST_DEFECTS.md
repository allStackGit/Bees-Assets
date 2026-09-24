# BeesServer qualification defect ledger

Concrete defects found while expanding server qualification. Reusable architectural lessons belong in `DEVELOPMENT_MEMORY.md`.

## Fixed on the modular runtime

### QS-001 — MySQL inactivity recovery checked the wrong mysql2 property
- Production defect. Legacy behavior compared numeric `4031` to `err.code`.
- Fix: `database.js` recognizes protocol loss, `ER_CLIENT_INTERACTION_TIMEOUT`, and `errno === 4031`.

### QS-002 — Pending request hashes leaked on unsupported/cancelled work
- Production defect. Unsupported requests and disconnected queued work could retain their hash indefinitely.
- Fix: fixed runtime centralizes completion cleanup and queue-discard cleanup.

### QS-003 — Consolidation transactions could cross pooled connections
- Production defect. START/DELETE/INSERT/COMMIT were independent pooled queries.
- Fix: `Database.transaction(work)` pins one connection for the complete transaction.

### QS-004 — `store-commands` could acknowledge failed/incomplete persistence
- Production defect. Writes were not awaited and write failures were swallowed.
- Fix: `gamePersistence.js` awaits transactional writes and propagates failures.

### QS-005 — Strategy-history SELECT failures were treated as no history
- Production defect. Database outages could look like valid empty history.
- Fix: fixed Game history reads propagate query failures.

### QS-006 — Runtime dependencies were undeclared
- Packaging defect. `mysql2` and `websocket` were production imports but absent from `package.json`.
- Fix: both are now declared runtime dependencies and `server.js` is the package/start entrypoint.
- Repository artifact note: the existing `package-lock.json` predates those declarations. If the project-machine `npm install` regenerated it, commit the generated lockfile before merge/release.

### QS-007 — Failed/concurrent command persistence could lose or race outcome ownership
- Production defect. Pending IDs were removed before commit and concurrent `store-commands` calls could overlap one Game's pending collections.
- Fix: pending IDs survive failed commits, retries reuse them, and Game persistence is serialized.
- Regression: `test/gamePersistence.module.test.js`.

### QS-008 — Test database selection depended on process command-line state
- Qualification/runtime defect in the first modular implementation.
- Fix: server mode is now passed explicitly into `Database`; test mode always selects `bees_test`, non-test preserves the configured production database.
- `mysql2` pool configuration also enables `supportBigNumbers` + `bigNumberStrings` so unsigned 64-bit identifiers remain exact if returned by future queries.

### QS-009 — RETRACTED: current Unity settings version absent from dump
- This was an analysis error caused by incomplete extraction of the large SQL dump, not a database defect.
- Full-file verification shows global `configuration`, `starting-settings`, and `ship-stats` rows for versions 2 through 7, including current Unity version 5.
- Version-5 payloads contain the current fields previously thought missing.
- The live schema test now validates row/version/serialized-field presence without incorrectly forcing Node strict-JSON parsing onto Unity-authored settings text.

### QS-010 — Hot logical lookup indexes were absent
- Schema/performance finding.
- The dump has primary-key IDs, but no composite index supporting `stored_user_data(userId, filename)` or `settings(userId, name, version)`.
- Fix: `schemaMigrations.js` idempotently adds `stored_user_data(userId, filename, ID)` and `settings(userId, name, version, Id)` indexes.
- `npm run test:live` applies the migration to `bees_test` before qualification. `npm run migrate:production` applies the same migration to the normal database only when `BEES_ALLOW_PRODUCTION_MIGRATION=1` is explicitly set.
- Regression: `test/schemaMigrations.test.js`; live schema qualification verifies both index prefixes after migration.

### QS-011 — Legacy runtime loader treated CRLF as a source-contract change
- Qualification/runtime compatibility defect discovered on the first project-machine `npm test` run.
- Root cause: `server.js` intentionally validates exact legacy startup markers before VM transformation, but its multiline marker used LF while the checked-out legacy source uses CRLF. The semantic constructor/startup code had not changed.
- Fix: normalize `\r\n` / `\r` to `\n` immediately after reading `siServerDev.js`, then perform the same fail-closed marker validation and replacement.
- Regression: the runtime/startup integration tests traverse this loader and passed on subsequent project-machine runs.

### QS-012 — Startup qualification expected rejection instead of forced test-database isolation
- Test-harness defect discovered on the second project-machine `npm test` run.
- Root cause: the test predated QS-008's stronger invariant. Test mode no longer accepts a caller-selected database and then validates its name; `Database.selectDatabase(..., true)` unconditionally forces `bees_test`.
- Fix: the regression deliberately requests `ram` in test mode and verifies both the Database configuration and created pool still use `bees_test`.
- Validation: the corrected ordinary server suite passed completely.

## Fixed cross-layer contract issue

- BeesServer matchup identity is unsigned xxHash64 represented as a decimal string and stored as `BIGINT UNSIGNED`.
- Unity `CommandResponse` / `MatchupStrategyResponse` previously exposed matchup IDs as signed `long`. The client qualification branch now stores matchup IDs as strings and has EditMode regressions with values above `long.MaxValue`.

## Remaining scheduled schema risk, not current blocker

- Outcome tables use `INT UNSIGNED` auto-increment primary keys. Their counters are roughly 0.58–0.95 billion in the supplied dump, with a maximum around 4.29 billion. This is not presently near exhaustion, but should be migrated to `BIGINT UNSIGNED` before future high-volume training approaches the limit.

## Integration-test safety

- Test server database is exactly `bees_test`; normal non-test startup remains on `ram`.
- Live mutation tests require a reserved `UserId >= 1000000` and refuse remote targets without a second explicit opt-in.
- `test:live` applies safe idempotent test-schema indexes, then runs schema/data-contract qualification and real WebSocket/data-flow integration.

## Validation status

**Server qualification is green on the project Linux server.** The corrected ordinary `npm test` suite passed completely, and the opt-in live `bees_test` integration suite also passed completely. The live run exercised the real database/schema/settings checks, test-user data flow, strategy request/persistence/history path, and disconnect/reconnect flow against `bees_test` without touching `ram`.

No currently known BeesServer correctness defect remains open from this qualification pass. New failures should be treated as new evidence and recorded here with reproducer -> root cause -> fix -> regression status.
