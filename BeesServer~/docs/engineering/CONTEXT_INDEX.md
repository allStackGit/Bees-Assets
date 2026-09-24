# BeesServer Context Index

Compact routing map for agents. Search this before broad repository scans, then inspect linked current source/tests/schema. Detailed facts belong in owner documents; this index is navigation, not authority.

| Concept / useful aliases | Start with | Current code / symbols to locate | Evidence / related concepts |
|---|---|---|---|
| runtime layering, startup, modular/legacy boundary | `SYSTEM_MAP.md` → Runtime entrypoints | `server.js`, `security.js`, `gamePersistence.js`, `start-server.js`, `siServerDev.js` | legacy source transform markers; overlay order; startup tests |
| test startup vs production startup | `docs/LIVE_INTEGRATION_TESTING.md`; startup tests | legacy test-mode `Server.start`, production `security.patchServerStart` | test mode intentionally differs; production has TLS/auth/8 MiB admission cap |
| development launcher credentials | `start-server.js`; development memory | checked-in dev DB defaults + environment overrides | deliberate current dev policy; direct production runtime credential guard differs |
| database, pool, transaction, rollback | `DATABASE_MODEL.md`; invariants → Database | `database.js`, `Database.transaction` | one borrowed connection; failure propagation; reconnection; `database.module.test.js` |
| user ID, Steam64, bigint exactness | `DATABASE_MODEL.md` → Identity | `database.js` result normalization, `security.js` auth | user IDs returned as decimal strings; never JS `Number` when unsafe |
| identity namespaces, OutcomeId vs row ID | `DATABASE_MODEL.md` → Identity | request hash, Game, durable reservation, SQL row IDs | account/user; connection request key; temporary OutcomeId; physical row ID |
| atomic campaign/profile checkpoint | `campaignCheckpoint.js`; Bees client `CampaignCheckpoint` | `__campaign_checkpoint__`, seven profile docs | one server transaction; shared-user insert semantics; campaign checkpoint tests |
| OutcomeId, durable reservation, StoreCommands | development memory → Outcome ownership; database model | `outcomeReservations.js`, `gamePersistence.js`, StoreCommands handler | reservation survives restart/reconnect/Game retirement; commit/discard; stale siblings |
| StoreCommands acknowledgement, retry, stale 409 | `security.js`, `outcomeReservations.js` | `game.storeState`, terminal 409 response | success only after durable commit; explicit discard is terminal before sibling writes |
| per-Game store serialization | `gamePersistence.js` | `STORE_TAIL`, patched `Game.storeState` | one Game's writes serialized; separate from per-user/per-socket/global coordination |
| request hash, connection scope, exactly-once release | `security.js`, `server.js` | `connectionId:Hash`, pending set, `releasePendingRequest` | identical client hash may exist on different sockets; integration tests |
| per-socket state ordering | `server.js` patchSocketConnection | state tail for setup/reconnect/user/settings | ordinary strategy requests await current barrier but do not join it; connection-ordering tests |
| per-user profile serialization | `security.js` | `runUserProfileOperation`, per-user tail | distinct from per-socket ordering and Game persistence serialization |
| authentication, Steam, identity binding | `security.js` | single-flight auth promise, ticket validation, idle deadline | claimed user must equal authenticated Steam identity; reconnect owner binding |
| shooting matchup, concurrent Levels | `security.js`, `server.js` | AsyncLocalStorage request context, `ShootingMatchup` | request-local; never mutable shared Game state |
| shared backend Game across Unity Levels | `server.js`; multi-Level/reconnect tests | repeated `setup-level`, `connection.Game`, replacement reconnect Game | one WebSocket shares one Hive Mind Game; sibling reconnects share replacement Game |
| strategic cache + banned strategies | `gamePersistence.js` | stable availability signature, `strategyCacheKey`, invalidation | cache varies by sorted unique banned-strategy set; strategyAvailabilityCache tests |
| shooting cache filtering | `server.js` legacy transform + tests | already-filtered history contract | do not reapply wrong matchup filtering after cache lookup |
| targeting/shooting learning-key namespace | `server.js`; `learningKeyNamespace.integration.test.js` | `target-v2:`, `shoot-v2:` | corrected keys intentionally isolate old misattributed historical learning rows |
| strategy aggregation, underused fallback | transformed legacy Game + full aggregation test | `getStrat`, available strategies | inspect full available set before fallback; do not stop at first underused strategy |
| consolidation, exact totals | `gamePersistence.js` | batch select/delete/reinsert, BigInt weighted total | max two exact rows; quotient/remainder; commit before cache invalidation |
| consolidation vs active writers | `security.js`, `gamePersistence.js` | in-flight StoreCommands writer count, admission barrier | socket disconnect does not prove writer completion; tests cover handoff/retry |
| cache persistence | `cachePersistence.js` | snapshot swap, lazy chunks, requeue on write failure | bounded serialization; entries arriving during failed write preserved after snapshot |
| recovery/export backpressure | `recover-tables.js` | ordered ID pagination, awaited writes, retry | database batches cannot outrun filesystem; retry module tests |
| user data, missing vs read failure | `DATABASE_MODEL.md`; server contracts/tests | `User.getData`, modular error transforms | missing row is first-run state; DB failure must not masquerade as missing |
| settings, exact version | `DATABASE_MODEL.md` | `User.getSettings`, current Unity `ConfigData.Version` | exact user/name/version lookup; user-specific then global; no invented version fallback |
| schema, migrations, current physical types | `DATABASE_MODEL.md` → Current migrated schema | `schemaMigrations.js`, `migrate.js`, `databaseSchema.live.test.js` | `settings.userId BIGINT`; learning `uses BIGINT UNSIGNED`; hot indexes; production opt-in |
| persistent strategy IDs | `DATABASE_MODEL.md` → Strategy identity | legacy Game registries, Unity conversion tables | command 1–15; targeting/shooting 1–40; IDs/order are stored-data compatibility |
| request queue, event loop | `server.js`, `requestQueue.module.test.js` | queue snapshot drain + scheduled next drain | linear burst handling; no repeated front shifts; ordinary request concurrency preserved |
| live test topology | validation policy; `docs/LIVE_INTEGRATION_TESTING.md` | `run-tests.js`, `testServerConfig.js`, `test/` | `npm test` migrates bees_test, starts temp real server, Node suite, teardown |
| Unity protocol/client boundary | `SYSTEM_MAP.md` → Cross-project boundary; enclosing `docs/engineering/CONTEXT_INDEX.md` | request/response shapes, settings, user data, reconnect, learning keys | inspect Bees-Assets before wire/identity/persistence changes |
| media server | `mediaServer.js` | safe path resolution, byte ranges | independent HTTP media utility; traversal/range tests |
| maintenance debt, legacy monolith | `QUALITY_LEDGER.md` | `siServerDev.js`, duplicate consolidation layers, tracked `node_modules` | refactor only with strategy-ID/protocol/test preservation |
| agent learning, context, retrieval | this file; `LEARNING_STATE.md`; repo-learning skill | `.agents/skills/{repo-learning,continuous-learning,search-index,code-quality}` | repeated misses; quality ledger; engineering guardrail tests |

## Retrieval rules

- Start with the matching row and named document section; do not automatically read all detailed memory/database history.
- Search exact symbol, request name, SQL/table, strategy name or error term before broad scans.
- Name the coordination domain before changing concurrency: per-request/connection, per-socket state, per-user profile, per-Game persistence, or server-wide consolidation.
- Name the identity namespace before converting a value: account/user, request hash, server Game, temporary OutcomeId, matchup hash, strategy ID, or physical row ID.
- When behavior is supplied by a modular overlay, inspect both the overlay and the transformed legacy contract it replaces; do not assume direct `siServerDev.js` behavior is authoritative production behavior.
- For Unity-facing behavior, inspect the Unity client in the enclosing Bees-Assets repository as well as server tests.
- When one concept repeatedly requires another, add the relationship here rather than copying implementation detail.
- Update stale routes when touched code moves. Material behavior must still be verified from current source/tests/schema and client contracts when relevant.
