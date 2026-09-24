# BeesServer development memory

Compact reusable knowledge for backend changes; this is not a changelog.

## Runtime structure

- `server.js` is the runtime factory/main module. It loads the legacy domain/strategy classes from `siServerDev.js` without allowing legacy auto-start, then applies hardened Database/request/Game behavior. `start-server.js` is the operational launcher used by `npm start`; do not invoke the legacy monolith directly.
- `database.js` is the production Database implementation; `gamePersistence.js` owns fixed Game history/outcome persistence; `outcomeReservations.js` owns restart-durable temporary outcome identity; `security.js` owns production authentication/admission hardening; `schemaMigrations.js` owns idempotent schema/index upgrades; `cachePersistence.js` owns failure-safe cache-file writes.
- The VM boundary validates transformation markers and normalizes legacy source line endings before validation. If a semantic marker changes, fail closed rather than silently transforming the wrong code.
- Legacy `mysql2`, `websocket`, and `http` dependencies remain injectable for hermetic tests.

## Database selection and exact numeric identity

- Test mode is an explicit server-mode input and always selects database `bees_test`. Normal/non-test mode preserves production database `ram`.
- `mysql2` pools use `supportBigNumbers: true` and `bigNumberStrings: true`. Preserve 64-bit database identifiers as strings when they can exceed JavaScript's safe integer range.
- Matchup identity is `xxh.h64(matchup, fixedSeed).toString()`: a deterministic unsigned 64-bit decimal string stored in `BIGINT UNSIGNED`. Do not coerce matchup hashes to JS `Number`.
- `strategy_id` is an application-level foreign key into the hard-coded Game registries (command 1–15, targeting 1–40, shooting 1–40). IDs/order are persistent schema.
- Full schema/flow reference: `docs/DATABASE_MODEL.md`.

## Database, outcome, and consolidation ownership

- A pooled transaction must keep one borrowed connection for START, every mutation, and COMMIT/ROLLBACK; independent pool queries are not a transaction.
- Strategy-history SELECT failures propagate rather than becoming empty history.
- `gamePersistence.js` serializes `storeState` per Game. Pending outcome IDs remain owned until commit/discard; failed transactions preserve retryable IDs and remove only transient failed-request updates.
- Client-visible `store-commands` success remains downstream of durable commit.
- Each positive temporary `OutcomeId` may be rewarded at most once in one StoreCommands payload. Repeated copies are coalesced idempotently to one update; if that same ID is also explicitly discarded in the payload, discard wins because it is the terminal ownership action.
- Mixed stale/valid StoreCommands batches are partitioned: valid reservations commit transactionally and stale IDs are reported explicitly rather than aborting valid reward rows.
- An explicit `DiscardReservation` is terminal ownership independent of sibling writes. Its durable metadata is deleted before the remaining StoreCommands operation is processed, so a later stale 409 or write failure cannot strand an outcome the client has already retired.
- Durable reservation rows are not deleted merely because an outcome becomes old. A reconnect whose retained Game has expired can create a replacement Game and still resolve old outcome IDs from durable rows by user ID; commit/discard, not Game-map presence or wall-clock age, is the authoritative ownership boundary.
- Consolidation preserves exact total TSV and uses, uses per-strategy thresholds, executes delete/reinsert work through `Database.transaction`, and invalidates affected strategy caches after commit. Weighted TSV for consolidation is accumulated directly from source rows with `BigInt`; do not route the consolidation calculation through legacy Number-based `Game.addOutcomes()` or replace the exact representation with rounded `average * uses`.
- Consolidation and writers are coordinated. Unauthenticated sockets do not globally block consolidation; new admission/authentication cannot become a writer inside an active/queued consolidation snapshot window.
- Live socket membership is not sufficient to prove there are no writers. `security.js` counts in-flight StoreCommands outcome writes and guards whatever `runConsolidationQueue` implementation is later assigned; consolidation must remain deferred until that counter reaches zero, even if the originating WebSocket disconnects while its DB transaction is still completing.
- Recovery/export loops must honor filesystem backpressure. `recover-tables.js` awaits each file-handle write before requesting the next database batch so large dumps cannot accumulate an unbounded Writable buffer, and file errors propagate through the script's normal failure path.

## Outcome identity and Hive Mind requests

- Database row `ID` and client/server temporary `OutcomeId` are different identities. SQL rows are created only after Unity returns final TSV through `store-commands`.
- One socket can host many simultaneous Unity Levels. Repeated `setup-level` requests reuse the connection's one Game so all pending outcome IDs share the correct ownership context.
- Disconnect marks the Game inactive; reconnect can take ownership of the retained Game, and if that Game has already been retired the reconnect path creates a replacement Game whose durability layer can resolve still-valid reservation metadata by user ID.
- Shooting identity is request-local and distinct from strategic ally context. Current Unity sends `ShootingMatchup` containing acting-squad plus enemy composition. `security.js` carries it through `AsyncLocalStorage` while the shared Game evaluates `get-strategy`; never put the current shooting key on mutable Game state because simultaneous training Levels can interleave.
- Strategy caches cross a `node:vm` realm. Cache checks/invalidation must use realm-safe Map recognition rather than host-realm `instanceof Map`.

## Settings and user-data contracts

- `settings` uses exact `(userId, name, version)` lookup; user-specific data can override global user 0. The server does not safely fall back between versions.
- Current Unity `ConfigData.Version` is 5. The reference dump contains the matching configuration/starting-settings/ship-stats rows.
- `stored_user_data` is opaque JSON by logical `(userId, filename)` with `ID` as physical primary key. Missing rows are expected first-run state; database/authentication errors are not missing-data responses.
- `campaign_levels_data` and `challenge_levels_data` are shared/read-only client data and currently use synthetic user ID 2. Do not use ID 2 for integration users.
- User-data mutations for one authenticated user are serialized so concurrent reads/writes cannot reorder profile state.
- `schemaMigrations.js` adds hot lookup indexes and numeric-capacity migrations idempotently from live metadata. Production migration remains explicit.

## Request, admission, and connection lifecycle

- Pending request ownership is connection-scoped (`connectionId:Hash`) so identical client hashes on different sockets are independent. Every terminal/abandoned path must release that key exactly once.
- Request-queue drains must stay linear. `server.queue` is an Array, so do not remove a large snapshot with repeated `shift()` calls; extract the entry snapshot once and iterate it so a burst cannot turn into O(n²) event-loop work.
- Connection-state ordering is a dependency barrier, not a global serialization policy. `setup-level`, `reconnect-level`, and user/settings state mutations append to the per-socket state tail; later ordinary requests must await the current tail before starting, but they must not append themselves to it, so independent strategy requests can run concurrently after state is ready.
- Production WebSocket payloads have a practical pre-authentication size cap. Do not restore the legacy ~1 GiB frame/message limits.
- Production Steam authentication has an idle deadline. Claimed user IDs must match the authenticated ticket identity; retained Games cannot be reconnected by a different authenticated owner.
- Authentication is single-flight per WebSocket connection. The request queue may dispatch distinct hashes concurrently, but unauthenticated messages must await one shared in-flight Steam authentication promise rather than opening one HTTPS authentication request per message. After authentication succeeds, normal request concurrency resumes.
- Normal HTTPS requests are completed promptly with an upgrade-required response rather than left open indefinitely.
- Hardened production startup must preserve the legacy lifecycle jobs it replaces: request/cache queues, cache-map persistence, cache cleaning, and inactive-Game cleanup.
- Background cache persistence must contain asynchronous filesystem failures and bounded serialization. Never rely on a `createWriteStream` try/catch without an `error` listener: stream errors occur asynchronously and can terminate Node. `cachePersistence.js` replaces the legacy writer, requeues an unwritten snapshot ahead of entries that arrived while a failed write was pending, and yields serialized cache slices lazily so a large snapshot is not duplicated in memory before disk writes begin.

## Test-server isolation and live integration

- `node server.js test` uses port 7146 and forces `bees_test`; normal production operation remains on `ram`.
- `BEES_REQUIRE_TEST_DB=1` is an additional defensive assertion. `BEES_DISABLE_BACKGROUND_JOBS=1` suppresses cache/consolidation background work while retaining the real request queue.
- Live integration requires an explicitly reserved high test user ID and refuses remote targets unless separately allowed.
- The live fixture mirrors Unity protocol: user-data write/read, setup-level, get-strategy, transactional store-commands, post-write strategy read, disconnect/reconnect, and another user-data update.
- The last recorded pre-audit qualification baseline (August 8, 2026) had ordinary and opt-in live `bees_test` suites passing. This audit itself is static-only and has not rerun them.

## Package reproducibility

- `package.json` and the committed lockfile are currently aligned: the lockfile root includes `mime-types`, `mysql2`, `object-sizeof`, `websocket`, `xxhashjs`, and the `eslint` development dependency with the same declared ranges.
- Keep `package-lock.json` regenerated with the project package manager whenever dependency declarations change; do not hand-author npm resolved/integrity metadata during a static-only audit.
