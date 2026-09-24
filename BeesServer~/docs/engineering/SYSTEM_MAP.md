# BeesServer System Map

A concise current map for orienting development. `docs/DEVELOPMENT_MEMORY.md` owns detailed reusable implementation knowledge and `docs/DATABASE_MODEL.md` owns detailed schema/data-flow documentation; update those rather than expanding this into another large architecture report.

## Runtime entrypoints and layering

- `server.js` — current modular runtime factory/main module. It loads retained legacy domain/strategy classes from `siServerDev.js`, verifies/transforms expected source markers, executes them through `node:vm`, then installs modular runtime patches.
- `start-server.js` — operational launcher used by `npm start`, including background/log modes and current development credential defaults/environment overrides.
- `siServerDev.js` — legacy monolith/source of retained `User`/`Game`/strategy classes; direct legacy behavior is not automatically current production behavior once modular overlays are applied.
- `run-tests.js` — canonical qualification orchestrator for the disposable test environment.
- `migrate.js` / `schemaMigrations.js` — explicit schema migration path.

Important overlay owners include:

- `database.js` — MySQL pool/database selection, exact numeric handling, transactions, queries and failure behavior;
- `gamePersistence.js` — serialized per-Game outcome/history persistence, availability-sensitive caches and authoritative exact consolidation implementation;
- `outcomeReservations.js` — restart-durable temporary OutcomeId reservation identity and terminal commit/discard behavior;
- `security.js` — authentication, admission, request/user ownership, production WebSocket hardening and writer/consolidation coordination;
- `cachePersistence.js` — failure-safe bounded cache-file persistence;
- `campaignCheckpoint.js` — atomic multi-document profile checkpoint persistence;
- `schemaMigrations.js` — idempotent index/numeric-capacity schema upgrades.

## Identity boundaries

Do not treat all IDs as interchangeable:

- **account/user identity** — Steam64/player identity. Preserve exact decimal representation; do not coerce unsafe values through JavaScript `Number`.
- **request identity** — Unity request `Hash`; server ownership is connection-scoped (`connectionId:Hash`) so the same client hash on different sockets is independent.
- **server Game identity** — Hive Mind learning/session container shared by sibling Unity Levels on one WebSocket and reconnectable/replacable as defined by ownership rules.
- **temporary OutcomeId** — positive learning-action identity returned to Unity and retained until durable commit or explicit discard; restart durability lives in reserved `stored_user_data` records.
- **matchup identity** — deterministic xxHash64 decimal string used in `BIGINT UNSIGNED` learning-table keys.
- **strategy identity** — numeric strategy registry ID; persistent data schema and not safe to reorder casually.
- **physical row ID** — table primary key; distinct from temporary OutcomeId and matchup/strategy identities.

## Main request/data flow

```text
Unity client
  -> WebSocket connection
  -> authentication/admission
  -> connection-scoped pending hash ownership
  -> per-socket state barrier when setup/reconnect/user/settings state requires ordering
  -> shared connection Game / strategy logic
       -> request-local shooting context where required
       -> matchup/availability-aware history/cache lookup
       -> temporary OutcomeId + durable reservation
  -> store-commands
       -> reservation validation / explicit discard handling
       -> serialized per-Game persistence
       -> one-connection MySQL transaction
       -> durable outcome/history rows + reservation cleanup
       -> cache invalidation/update
  -> response to Unity only after required durable work succeeds
```

One socket may host multiple Unity Levels. The backend intentionally reuses one Hive Mind `Game` across repeated setup-level calls. Sibling reconnects also converge on one replacement Game if the retained Game has expired, preventing pending-learning ownership from fragmenting.

Request-local context must remain request-local even when Levels share connection/Game infrastructure. Current shooting identity uses `AsyncLocalStorage` rather than mutable `Game` fields.

## Coordination domains

Before changing concurrency, identify the domain being protected:

1. **per-request/connection** — pending hash ownership and exactly-once release;
2. **per-socket state** — setup/reconnect/user/settings mutations form an ordering barrier; ordinary strategy requests await the current barrier but remain mutually concurrent;
3. **per-user profile** — user-data/profile mutations are serialized where logical file ordering matters;
4. **per-Game learning persistence** — `Game.storeState` writes are serialized so OutcomeId ownership/commit order stays coherent;
5. **server-wide consolidation window** — consolidation excludes active/in-flight StoreCommands writers and coordinates admission; socket disconnect alone does not end writer ownership;
6. **filesystem writer** — cache/recovery paths explicitly coordinate snapshot ownership, backpressure and async failure handling.

Do not replace one domain with global serialization merely because it is simpler.

## Persistence domains

- strategy/history/outcome tables — learned history and consolidation inputs;
- durable OutcomeId reservation files — temporary identity surviving restart/reconnect/Game retirement until commit/discard;
- `stored_user_data` — opaque logical user files plus reserved server-owned metadata;
- reserved `__campaign_checkpoint__` — seven related profile documents committed atomically by `campaignCheckpoint.js`;
- `settings` — exact user/name/version documents;
- shared campaign/challenge level data — synthetic shared-user records consumed by the game;
- cache files/maps — performance layer, never authority that may silently override durable DB semantics.

## Hive Mind history/cache boundaries

- strategic and targeting cache variants include a stable banned-strategy/availability signature so a request cannot reuse a matchup cache produced under a different allowed-strategy set;
- corrected targeting/shooting history uses `target-v2:` / `shoot-v2:` key namespaces to avoid silently reusing older misattributed history;
- strategy aggregation must inspect the available strategy set before underused/fallback selection rather than stopping at the first candidate;
- consolidation preserves exact weighted TSV/uses with `BigInt`, commits delete/reinsert changes atomically, then invalidates affected caches.

These are learning-data compatibility rules, not ordinary cache/string implementation details.

## Schema/runtime boundary

The August 8 reference dump is not the whole current schema story. `schemaMigrations.js` and `test/databaseSchema.live.test.js` define the migrated requirements used by qualification, including:

- `settings.userId BIGINT`;
- learning-table `uses BIGINT UNSIGNED`;
- `stored_user_data(userId, filename, ID)` and `settings(userId, name, version, Id)` lookup indexes.

See `docs/DATABASE_MODEL.md` for the reference-dump versus current-migrated distinction.

## Test topology

`npm test` invokes `run-tests.js`, which:

1. loads `testServerConfig.js`;
2. requires the test port to be free;
3. migrates `bees_test`;
4. syntax-checks critical runtime files;
5. starts a temporary real server in test mode;
6. runs Node's complete `--test` suite, including live schema/WebSocket/MySQL tests enabled by the test environment;
7. shuts the server down and fails non-zero on any failure.

Test-mode server startup deliberately uses the repository's test path and does not prove every production TLS/authentication/admission behavior. Production startup hardening has its own direct integration/contract tests.

Focused `node --test <test-file>` runs are useful for iteration when their dependencies are understood, but they do not replace full `npm test` qualification for behavior that crosses runtime/database/WebSocket boundaries.

## Cross-repository boundary

Bees-Assets owns the Unity client. Changes to request/response DTO shape, matchup construction/key representation, settings versions, user-data/profile checkpoint semantics, authentication identity, reconnect/shared-Game behavior, OutcomeId attribution or StoreCommands behavior may require coordinated client tests/changes. Do not infer client compatibility from server tests alone.

## Durable documentation owners

- stable project trust boundary: `PROJECT_CONSTITUTION.md`
- must-preserve engineering contracts: `docs/engineering/INVARIANTS.md`
- compact retrieval map: `docs/engineering/CONTEXT_INDEX.md`
- detailed backend working memory: `docs/DEVELOPMENT_MEMORY.md`
- schema/data model: `docs/DATABASE_MODEL.md`
- test-server operation: `docs/LIVE_INTEGRATION_TESTING.md`
- validation/test-health rules: `docs/engineering/VALIDATION_POLICY.md`
- historical qualification defects: `docs/TEST_DEFECTS.md`
- permanent regression protections: `docs/engineering/REGRESSIONS.md`
- unresolved learning/retrieval state: `docs/engineering/LEARNING_STATE.md`
- maintainability debt: `QUALITY_LEDGER.md`
- unresolved current defect/performance queues: `BUG_LEDGER.md`, `PERFORMANCE_LEDGER.md`

## Maintenance rule

This map is orientation, not proof. Revalidate current source/tests/schema before relying on implementation detail, and keep direct legacy behavior separate from the modular production behavior layered over it.
