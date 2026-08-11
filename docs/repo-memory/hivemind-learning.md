# Hive Mind learning memory

Compact facts that are expensive to rediscover when auditing or changing Hive Mind training.

## Runtime settings/database contract

- Current client `ConfigData.Version` is 5. `ram.sql` contains the matching global settings rows for `configuration`, `starting-settings`, and `ship-stats` (IDs 14, 15, 16 respectively). Do not infer version-5 behavior from the newer version-7 configuration row.
- Version-5 ship stats contain all 24 current `ShipTypes` entries including `Human Target`. For every row, the parallel weapon arrays (`Range`, `Power`, `RateOfFire`, `RotationRates`, `ProjectileValue`, `ProjectileTypes`, `WeaponSoundTypes`, `WeaponTypes`) have matching lengths. Serialized weapon/projectile/sound names match the current conversion vocabulary.
- The dump has only five tables: `settings`, `stored_user_data`, `strategic_commands`, `shooting_outcomes`, and `targeting_outcomes`. Matchup strings are not retained in SQL; only their hashed `matchup_id` survives, so historical hash fragmentation cannot be decomposed after the fact.
- BeesServer's current `test` command-line flag changes the port but the production monolith still constructs its database connection against live `ram`. Test/integration execution must select `bees_test`; do not run destructive/integration server tests against the current monolith until this is integrated.

## Current historical learning coverage

- In the supplied `ram.sql`, each learning layer represents 1,210 accumulated uses, but experience is extremely sparse: 706 strategic matchups / 966 matchup-strategy pairs (max pair uses 9), 699 shooting matchups / 1,085 pairs (max 6), and 84 targeting matchups / 794 pairs (max 7).
- Server `minimumCommandUse` is 25 and selection falls back while any available strategy is at or below that threshold. In this dump zero pairs exceed 25 and zero matchups have completed the all-strategies-sufficient condition. Historical data therefore has not reached the learned/exploitation phase.
- Strategic matchup construction includes acting ships, nearby relevant allies, enemy composition, an in-range flag, and comparative-health bucket. Server shooting keys are derived from the composition portions of that strategic key. Nearby allies therefore fragment shooting experience even though they are not part of the acting squad's target-priority policy. Treat exact-key sparsity/generalization as a pre-training design issue, not just a need for more episodes.
- Outcome IDs returned during strategy selection are initially only in-memory `Game.pendingInserts`; SQL rows are created when a later StoreCommands update matches the ID. The supplied dump therefore has no `uses=0` rows. Intentionally discarded shooting/targeting IDs can age out of `pendingInserts` without polluting SQL.

## Reward/attribution invariants

- `Command.Tsv` is a strategic reward channel. It mixes combat with command-specific effects such as mining, healing, spotting/vision, and retreat consequences. It must not be serialized directly as shooting-policy reward.
- `StoredCommand.ShootingTsv` is the combat-only shooting-policy channel. `StoreCommands` serializes it for `ShootingCommands`; strategic and targeting outcomes continue to use full `StoredCommand.Tsv`.
- Shooting and targeting outcomes should only be persisted when the executed command actually uses the server-selected enemy context. Current enemy-dependent command family: Aggressive, BombingRun, Charge, Retreat, CircleSquad, RightSwipe, LeftSwipe, InAndOut. Non-attack commands may fire opportunistically, but their preselected-enemy shooting key does not describe the actual candidate set and must not be used for policy learning.
- Retreat is a temporary exception: current `Socket.HandleStrategicCommandResponse` executes Retreat with `FirstSeen` even though the server supplies another shooting-strategy outcome ID. Until that handler is fixed, do not persist Retreat shooting outcomes.
- Targetless commands must not create shooting/targeting training rows because their shooting matchup has no selected enemy composition. Historical dump evidence: 71 exact `ownShips||` shooting hashes account for 467/1,210 uses; those rows mirrored no-enemy strategic use counts and already contain mixed non-shooting reward. Existing historical shooting data is therefore contaminated and must be deliberately cleaned/migrated before trusting learned shooting values.
- Projectiles snapshot their originating Hive Mind command `OutcomeId`. Rocket explosions, SplitterShot children, Fire Tank explosions, and delayed Striker bombs must inherit that ID. Combat credit uses the active command only if its outcome still matches; otherwise it updates the retained `StoredCommand`. Never revert to crediting whichever command happens to be active when a delayed projectile lands.
- `PastCommands` and `OutcomeIdToPastCommandIndex` are the stable per-level attribution store. Finalized command wrappers are not returned to the pool until level teardown, after command storage, so delayed projectile credit can safely resolve by outcome ID during the episode.

## Outcome persistence invariants

- A StoreCommands success response must mean every matched outcome row has been durably written. Current production violates this: `matchUpdatesWithInsertsAndCommit()` removes matched pending inserts, launches `insertIntoTable()` without awaiting it, and `insertIntoTable()` swallows query rejection. `storeState()` can therefore return true/Status 200 before writes finish or after a write fails. Fix this before training; failed writes must reject and preserve/reconstruct retryable state.
- `pendingRequests` is also a retry ownership boundary. Current server registers the request hash before processing, but failure paths generally do not remove it. Client timeout resends reuse the same hash and are then rejected as already pending forever. Every failed/abandoned request path must release its hash before retry.
- Pending inserts must not be deleted before durable storage is known to have succeeded. If batching is refactored, either delete only after successful write/transaction commit or restore unmatched metadata on failure.

## Known production-server blockers still open

- Strategy caches are time-based and not invalidated when new outcomes are committed, so mature policy values can remain stale for up to the configured cache age.
- Cached shooting selection filters absent `Type X` strategies and then currently overwrites the filtered list with the full cached list; once shooting caches become active this can select a ship-type priority that is absent from the enemy composition.
- Minimum-use exploration stops aggregating at the first underused strategy, leaving later strategies at default usage counts and weakening the intended balancing/banning behavior.
- Cache files are reconstructible optimization state but `loadCacheMaps()` synchronously assumes all files exist and parse; missing/malformed files can prevent server startup.
- Consolidation currently issues START TRANSACTION / DELETE / INSERT / COMMIT through independent pool queries; MySQL transactions are connection-scoped, so this is not one atomic transaction. Use the checked-out-connection `withTransaction` contract.
- `serverContracts.js` contains executable contracts for several intended fixes (`nextUniquePendingId`, `withTransaction`, `releasePendingRequest`, `getOrCreateConnectionGame`, `databaseNameForMode`, `persistOutcomeBatches`), but production `siServerDev.js` has not integrated most of them. Passing helper tests is not evidence that production behavior is fixed.
- `siServerDev.js` is a large monolith and the available GitHub connector only provides whole-file replacement writes. Prefer a real local checkout/diff-capable environment before integrating server fixes rather than reconstructing the entire file through chat output.
