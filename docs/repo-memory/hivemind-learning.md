# Hive Mind learning memory

Compact facts that are expensive to rediscover when auditing or changing Hive Mind training.

## Runtime settings and training configuration

- Current client `ConfigData.Version` is 5. The reference SQL contains matching global settings rows for `configuration`, `starting-settings`, and `ship-stats`; do not infer version-5 behavior from newer rows.
- Platform/user identifiers can exceed JavaScript's safe integer range. Keep Steam/user IDs as invariant decimal strings across JSON/Node and bind them losslessly to MySQL BIGINT fields.
- `HiveMindTrainingBootstrap` owns the dedicated `Hivemind Training` runtime. It enables Hive Mind training, disables neural-network training/player control/rendering, uses 16 simultaneous Levels, a 420-second timeout, a 1-second initial command delay, and the complete primary Bee/Human fleet rather than profile unlocks. The player-facing Fish Tank mode shares the scene and must not receive this bootstrap.
- The Stage can host 1/2/4/8/12/16 Levels on one socket. BeesServer must therefore reuse one connection-owned `Game` across all `setup-level` requests; pending outcome IDs are session/Game state, not per-Level server objects.
- Large `Level` responsibilities are split into partials. `Level.Environment.cs` owns environment randomization, clearance, static obstacles, collision-asteroid spawning, and mining-asteroid spawning; keep environment fixes there rather than growing `Level.cs` again.
- Dedicated Hive Mind training samples both static-obstacle/no-static and collision-asteroid/no-asteroid dimensions. Do not reintroduce a blanket `!Stage.IsTraining` gate around those environment choices; that silently trains only obstacle-free command values.
- Asteroid spawn timing is Level-owned. Derive each Level's interval from the serialized Stage baseline (`AsteroidMinimumSpawnRate` / `AsteroidMaxSpawnRate`) and apply option multipliers locally. Never use zero-initialized/shared `CurrentAsteroid*` fields as mutable per-Level scratch state.
- `GeneratedSquadMinimumCompatibility` normalizes the legacy exclusive-minimum formula once for the Stage lifetime. Training resets reuse that same corrected minimum so the 4-squad lower bound does not disappear after the first episode.

## Matchup identity and historical coverage

- The supplied historical SQL represents sparse experience. Matchup strings themselves are not stored in SQL; only hashed `matchup_id` values survive, so historical key fragmentation cannot be reconstructed later from the database alone.
- Strategic matchup identity intentionally includes acting ships, relevant nearby allies, enemy composition, an in-range flag, and comparative-health bucket.
- Shooting identity is deliberately separate from strategic ally context. The client sends `GetStrategy.ShootingMatchup` as acting-squad composition plus enemy composition; BeesServer carries that request-local value through `AsyncLocalStorage` while evaluating the shared `Game`. Never derive the current shooting key from the strategic first segment again, because nearby allies fragment target-priority history.
- Request-local shooting identity is required for the 16-Level training runtime. Do not store the current shooting key in mutable `Game` state: simultaneous `get-strategy` requests would overwrite one another.
- High-density matchup construction has a remaining design consideration rather than a validated current defect: when more than 64 relevant ships are visible, any future representative-sampling change must be deterministic before relying on exact keys for large battles.

## Reward and attribution invariants

- `Command.Tsv` is a strategic reward channel. It mixes combat with command-specific effects such as mining, healing, spotting/vision, and retreat consequences. It must not be serialized directly as shooting-policy reward.
- `StoredCommand.ShootingTsv` is the combat-only shooting-policy channel. `StoreCommands` serializes it for shooting outcomes; strategic and targeting outcomes use full command TSV.
- Projectiles snapshot their originating Hive Mind command `OutcomeId`. Delayed rockets/explosions/split shots/Striker bombs must route credit by that stable outcome ID rather than whichever pooled command is active when damage lands.
- Same-side damage is negative combat reward for the attacking command. Fire Tank and Fire Barge explosions can damage friendlies; classify by attacker side vs target side.
- Fire Barge chain reactions have two owners: the originating Barge command receives same-side penalties, while an enemy killer can receive the historical chain-reaction bonus. Snapshot the killer's outcome identity at death and reset it on pooled reuse.
- `PastCommands` plus `OutcomeIdToPastCommandIndex` are the stable per-Level attribution store. Finalized command wrappers remain unavailable for reuse until level teardown, after command storage.
- Scout-created Beacons and ordinary Beacons share one pool. `Ship.ClearData()` clears `MotherSquad`; Hive Mind vision credits `MotherSquad` only for a current Scout-minion Beacon and otherwise credits the Beacon's own squad.

## Outcome persistence invariants

- A `store-commands` success response means the matched outcome mutations committed durably. Server persistence is serialized per `Game`; failed transactions preserve retryable reservation metadata.
- Durable outcome reservations survive process restart and are not expired merely because wall-clock age exceeds a normal long-running level. Reservation ownership ends when the outcome is committed/discarded or the owning retained Game lifecycle is deliberately retired.
- A mixed stale/valid StoreCommands batch must not sacrifice valid rewards. The server partitions stale IDs, commits the valid subset transactionally, then reports stale IDs explicitly.
- Database row `ID` and temporary client/server `OutcomeId` are different identities. SQL rows are created only when final TSV is returned through `store-commands`.
- Every terminal request path must release `server.pendingRequests` ownership. Request hashes are scoped by connection where duplicate client hashes can coexist across sockets.
- Strategy caches must invalidate after committed outcome writes. The server runtime crosses a `node:vm` boundary, so Map recognition must be cross-realm-safe rather than relying on host-realm `instanceof Map`.
- Consolidation must preserve exact `sum(strategic_outcome * uses)` and total uses, use per-strategy thresholds, invalidate affected caches, and execute delete/reinsert work transactionally. Production admission/authentication is coordinated so a writer cannot enter between a consolidation snapshot and replacement transaction.

## Server/runtime boundaries

- Production WebSocket messages are capped before application authentication; do not restore the legacy ~1 GiB admission sizes.
- Unauthenticated idle sockets have a deadline and do not globally block consolidation. New admission/authentication is rejected or reconnected while consolidation is active/queued rather than allowing concurrent writers.
- Production startup preserves inactive-Game cleanup and cache-map persistence timers that the hardened startup replaces from the legacy sequence.
- Ordinary HTTPS requests are completed promptly with an upgrade-required response; never leave non-WebSocket HTTP requests open indefinitely.
- `package-lock.json` in BeesServer still predates the current `package.json` runtime dependencies. It must be regenerated with the project package manager before release; do not hand-author transitive lock data during a static-only audit.
