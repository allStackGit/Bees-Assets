# BeesServer Engineering Invariants

These are must-preserve implementation contracts distilled from current source and maintained repository memory. Revalidate them against authoritative code when a task depends on a detail, and update this file when the contract deliberately changes.

## Runtime and legacy boundary

- `server.js` is the modular runtime factory/main module; `start-server.js` is the normal operational launcher. Do not treat direct legacy `siServerDev.js` startup as the current production path.
- The modular runtime intentionally loads/transforms legacy classes through `node:vm`. Transformation markers are a fail-closed compatibility boundary; normalize harmless line-ending differences but do not silently accept semantically missing markers.
- Cross-realm objects created by the VM must be handled with realm-safe type checks. Host-realm `instanceof` is not generally authoritative.

## Database and numeric identity

- Test mode always selects `bees_test`; normal mode uses production `ram`. Automated qualification must never mutate `ram`.
- A transaction owns one borrowed connection from begin through commit/rollback. Do not issue transactional statements through independent pool queries.
- `BIGINT`/hash/outcome-related values that can exceed `Number.MAX_SAFE_INTEGER` remain exact strings or `BigInt` as appropriate. Matchup identity is an unsigned xxHash64 decimal string and must not be coerced to `Number`.
- Persistent strategy IDs/order are application/schema compatibility surfaces.
- Database read failures that affect correctness propagate; they are not equivalent to an empty result.

## Outcome and learning history ownership

- Database row IDs and temporary client/server `OutcomeId` values are distinct identities.
- Pending outcome IDs remain owned until successful durable commit or explicit terminal discard. Failed transactions preserve retryable ownership.
- Duplicate reward copies of one positive OutcomeId within a payload are idempotently coalesced. Explicit discard is terminal ownership and wins over sibling reward use for that ID.
- Mixed stale/valid StoreCommands batches may preserve valid writes while reporting stale IDs; one stale reservation must not invalidate unrelated valid work unless the protocol contract explicitly requires it.
- Durable reservation ownership is not ended merely by Game-map retirement, disconnect, or age.
- Client-visible StoreCommands success is downstream of successful durable persistence.
- Consolidation preserves exact total TSV/uses, uses the proper transaction boundary, invalidates affected caches after commit, and must not round exact totals through legacy floating/Number aggregation paths.

## Connection, request, and authentication ordering

- Request ownership is connection-scoped; identical client hashes on different sockets are independent.
- Every terminal or abandoned request path releases its ownership key exactly once.
- Per-socket state barriers order setup/reconnect/user-state mutations before dependent requests. Independent ordinary strategy requests should remain concurrent once the required state tail is ready.
- Request-queue draining stays linear; avoid repeated front-removal patterns that make burst processing O(n^2).
- Authentication is single-flight per WebSocket connection. Concurrent unauthenticated messages share the same in-flight authentication work.
- Claimed user identity must match authenticated identity. Retained Games may not be reconnected by a different owner.
- Shooting matchup identity is request-local; do not store it on mutable shared Game state. Preserve the request context mechanism used for concurrent Levels.
- Production pre-authentication message/frame sizes remain bounded.

## Consolidation and active writers

- Consolidation must not race active/in-flight StoreCommands writers. A WebSocket disconnect does not prove the database writer has finished.
- Admission/writer coordination must preserve the snapshot boundary: a new writer cannot enter an active/queued consolidation window in a way that invalidates its assumptions.
- Retry/backoff and failure paths must leave caches/history in a coherent retryable state.

## Persistence, filesystem, and background work

- User-data mutations for one authenticated user remain serialized where ordering matters.
- Settings lookup is exact by user/name/version; do not invent a version fallback that the Unity client/server contract does not define.
- Cache persistence must handle asynchronous filesystem errors explicitly, preserve unwritten snapshots for retry, and avoid duplicating large snapshots unboundedly in memory.
- Export/recovery loops honor filesystem backpressure rather than reading database batches faster than disk writes can complete.
- Modular startup must preserve required lifecycle jobs it replaces: request/cache processing, cache persistence/cleaning, consolidation as configured, and inactive-Game cleanup.

## Schema and production operations

- Schema migrations are idempotent and derived from live metadata where designed that way.
- Production migration remains explicitly gated; test setup may migrate only `bees_test`.
- Hot logical lookup indexes and numeric capacity changes are part of runtime correctness/performance and must remain compatible with existing data.
- `stored_user_data` is opaque JSON storage; missing rows are legitimate first-run state, while DB/auth errors are not “missing data.”

## Validation

- `npm test` is the canonical complete server qualification: it prepares/migrates `bees_test`, syntax-checks critical entrypoints, starts a temporary real test server, runs the complete Node test suite including live MySQL/WebSocket checks, and tears the server down.
- Production `ram` is never a validation target.
- A cross-layer protocol/schema change is not fully validated by server unit tests alone when Unity serialization/parsing/identity semantics are affected.