'use strict';

const { AsyncLocalStorage } = require('node:async_hooks');
const { performance } = require('node:perf_hooks');

const PATCHED = Symbol('beesGamePersistencePatched');
const STORE_TAIL = Symbol('beesStoreStateTail');
const CONSOLIDATION_PATCHED = Symbol('beesConsolidationPatched');
const CONSOLIDATION_QUEUE_KEYS = Symbol('beesConsolidationQueueKeys');
const CACHE_CONTEXT = Symbol('beesStrategyCacheContext');
const CONSOLIDATION_RETRY_MS = 10000;

function availabilitySignature(bannedStrategies) {
    if (!bannedStrategies || bannedStrategies.length === 0) return '';
    if (bannedStrategies.length === 1) return String(bannedStrategies[0]);
    return [...new Set(bannedStrategies.map(value => String(value)))].sort().join(',');
}

function strategyCacheKey(matchupId, signature) {
    return `${String(matchupId)}|${signature || ''}`;
}

function deleteCacheVariants(cache, matchupId) {
    if (!cache?.delete) return;
    const rawKey = String(matchupId);
    const prefix = `${rawKey}|`;
    cache.delete(matchupId);
    cache.delete(rawKey);
    if (!cache.keys) return;
    for (const key of cache.keys()) {
        const text = String(key);
        if (text === rawKey || text.startsWith(prefix)) cache.delete(key);
    }
}

function invalidateStrategyCache(server, table, matchupId) {
    if (!server) return;
    if (table === 'strategic_commands') {
        deleteCacheVariants(server.cachedStrategies, matchupId);
    } else if (table === 'shooting_outcomes') {
        server.cachedShootingStrategies?.delete(matchupId);
        server.cachedShootingStrategies?.delete(String(matchupId));
    } else if (table === 'targeting_outcomes') {
        deleteCacheVariants(server.cachedTargetingStrategies, matchupId);
    }
}

function toUpdates(commands, table) {
    const updates = [];
    const discardedOutcomeIds = [];
    for (const command of commands || []) {
        const id = Number(command?.OutcomeId);
        if (!Number.isSafeInteger(id) || id <= 0) continue;
        if (command?.DiscardReservation === true) {
            discardedOutcomeIds.push(id);
            continue;
        }
        const tsv = Number(command?.Tsv);
        if (!Number.isSafeInteger(tsv)) {
            throw new RangeError(`Invalid TSV for outcome ${id}.`);
        }
        updates.push({ table, tsv, id });
    }
    return { updates, discardedOutcomeIds };
}

function toStoreOperation(commands, shootingCommands, targetingCommands) {
    const groups = [
        [commands, 'strategic_commands'],
        [shootingCommands, 'shooting_outcomes'],
        [targetingCommands, 'targeting_outcomes'],
    ];
    const discardedOutcomeIds = new Set();

    // Discard is terminal ownership for the whole StoreCommands payload, regardless of
    // which command group contains the marker or whether a reward copy appears first.
    for (const [items] of groups) {
        for (const command of items || []) {
            const id = Number(command?.OutcomeId);
            if (command?.DiscardReservation === true &&
                Number.isSafeInteger(id) && id > 0) {
                discardedOutcomeIds.add(id);
            }
        }
    }

    const rewardedOutcomeIds = new Set();
    const updates = [];
    for (const [items, table] of groups) {
        for (const command of items || []) {
            const id = Number(command?.OutcomeId);
            if (!Number.isSafeInteger(id) || id <= 0 ||
                command?.DiscardReservation === true ||
                discardedOutcomeIds.has(id) ||
                rewardedOutcomeIds.has(id)) {
                continue;
            }
            const tsv = Number(command?.Tsv);
            if (!Number.isSafeInteger(tsv)) {
                throw new RangeError(`Invalid TSV for outcome ${id}.`);
            }
            rewardedOutcomeIds.add(id);
            updates.push({ table, tsv, id });
        }
    }

    return { updates, discardedOutcomeIds: [...discardedOutcomeIds] };
}

function removeUpdates(game, ownedUpdates) {
    if (!ownedUpdates.length || !game.pendingUpdates?.length) return;
    const owned = new Set(ownedUpdates);
    game.pendingUpdates = game.pendingUpdates.filter(update => !owned.has(update));
}

function strategyRowCounts(outcomes) {
    const counts = new Map();
    for (const outcome of outcomes || []) {
        const strategyId = outcome.strategy_id;
        counts.set(strategyId, (counts.get(strategyId) || 0) + 1);
    }
    return counts;
}

function needsConsolidation(outcomes, maxOutcomesPerStratType) {
    const maximum = Number(maxOutcomesPerStratType);
    if (!Number.isSafeInteger(maximum) || maximum <= 0) {
        throw new RangeError(`Invalid maxOutcomesPerStratType: ${maxOutcomesPerStratType}`);
    }
    for (const count of strategyRowCounts(outcomes).values()) {
        if (count > maximum) return true;
    }
    return false;
}

function toExactBigInt(value, label) {
    if (typeof value === 'bigint') return value;
    if (typeof value === 'number') {
        if (!Number.isSafeInteger(value)) throw new RangeError(`${label} must be a safe integer.`);
        return BigInt(value);
    }
    if (typeof value === 'string' && /^-?\d+$/.test(value)) return BigInt(value);
    throw new RangeError(`${label} must be an integer.`);
}

function exactStrategyAggregate(outcomes, strategyId) {
    let totalTsv = 0n;
    let totalUses = 0n;
    for (const outcome of outcomes || []) {
        if (outcome.strategy_id != strategyId) continue;
        const uses = outcome.uses === undefined || outcome.uses === null
            ? 1n
            : toExactBigInt(outcome.uses, 'Outcome uses');
        const outcomeValue = toExactBigInt(outcome.strategic_outcome, 'Strategic outcome');
        totalTsv += outcomeValue * uses;
        totalUses += uses;
    }
    return { tsv: totalTsv, uses: totalUses };
}

function exactStrategyAggregates(outcomes) {
    const aggregates = new Map();
    for (const outcome of outcomes || []) {
        const strategyId = outcome.strategy_id;
        let aggregate = aggregates.get(strategyId);
        if (!aggregate) {
            aggregate = { tsv: 0n, uses: 0n };
            aggregates.set(strategyId, aggregate);
        }
        const uses = outcome.uses === undefined || outcome.uses === null
            ? 1n
            : toExactBigInt(outcome.uses, 'Outcome uses');
        const outcomeValue = toExactBigInt(outcome.strategic_outcome, 'Strategic outcome');
        aggregate.tsv += outcomeValue * uses;
        aggregate.uses += uses;
    }
    return aggregates;
}

function exactConsolidatedRows(matchupId, strategyId, totalTsv, totalUses) {
    const uses = toExactBigInt(totalUses, 'Consolidated uses');
    const tsv = toExactBigInt(totalTsv, 'Consolidated TSV');
    if (uses <= 0n) return [];
    if (uses > BigInt(Number.MAX_SAFE_INTEGER)) {
        throw new RangeError('Consolidated uses exceed the runtime safe-integer contract.');
    }

    const quotient = tsv / uses;
    const remainder = tsv % uses;
    const quotientNumber = Number(quotient);
    if (!Number.isSafeInteger(quotientNumber)) {
        throw new RangeError('Consolidated strategic outcome exceeds the runtime safe-integer contract.');
    }

    const usesNumber = Number(uses);
    if (remainder === 0n) {
        return [[matchupId, strategyId, quotientNumber, usesNumber]];
    }

    const absoluteRemainder = remainder < 0n ? -remainder : remainder;
    const remainderUses = Number(absoluteRemainder);
    const quotientUses = usesNumber - remainderUses;
    const adjustedQuotient = quotientNumber + (remainder > 0n ? 1 : -1);
    if (!Number.isSafeInteger(adjustedQuotient)) {
        throw new RangeError('Consolidated strategic outcome remainder exceeds the runtime safe-integer contract.');
    }

    const rows = [];
    if (quotientUses > 0) {
        rows.push([matchupId, strategyId, quotientNumber, quotientUses]);
    }
    rows.push([matchupId, strategyId, adjustedQuotient, remainderUses]);
    return rows;
}

function normalizeConsolidationTable(table) {
    if (table === 0) return 'targeting_outcomes';
    if (table === 1) return 'shooting_outcomes';
    if (table === 2) return 'strategic_commands';
    return table;
}

function consolidationKey(table, matchupId) {
    return `${normalizeConsolidationTable(table)}:${matchupId}`;
}

function getConsolidationQueueKeys(server) {
    if (!server[CONSOLIDATION_QUEUE_KEYS]) {
        server[CONSOLIDATION_QUEUE_KEYS] = new Set();
        for (const entry of server.consolidationQueue || []) {
            server[CONSOLIDATION_QUEUE_KEYS].add(consolidationKey(entry.table, entry.matchup_id));
        }
    }
    return server[CONSOLIDATION_QUEUE_KEYS];
}

function requeueConsolidations(server, entries) {
    const queuedKeys = getConsolidationQueueKeys(server);
    for (const entry of entries) {
        const key = consolidationKey(entry.table, entry.matchup_id);
        if (!queuedKeys.has(key)) {
            queuedKeys.add(key);
            server.consolidationQueue.push(entry);
        }
    }
}

function adoptQueuedConsolidations(server) {
    if (!server?.consolidationMap || server.consolidationMap.size === 0) return 0;

    const queuedKeys = getConsolidationQueueKeys(server);
    let adopted = 0;
    for (const entry of server.consolidationMap.values()) {
        const key = consolidationKey(entry.table, entry.matchup_id);
        if (!queuedKeys.has(key)) {
            queuedKeys.add(key);
            server.consolidationQueue.push(entry);
            adopted++;
        }
    }
    server.consolidationMap.clear();
    return adopted;
}

function hasBlockingConnections(server) {
    for (const connection of server?.connections?.values?.() || []) {
        if (server.test || connection?.authenticatedUserId) return true;
    }
    return false;
}

async function loadConsolidationOutcomes(batch) {
    const results = Array.from({ length: batch.length }, () => []);
    const databaseGroups = new Map();

    for (let index = 0; index < batch.length; index++) {
        const entry = batch[index];
        const game = patchGame(entry.game);
        let tableGroups = databaseGroups.get(game.db);
        if (!tableGroups) {
            tableGroups = new Map();
            databaseGroups.set(game.db, tableGroups);
        }
        let group = tableGroups.get(entry.table);
        if (!group) {
            group = { matchupIds: new Map(), entries: [] };
            tableGroups.set(entry.table, group);
        }
        const matchupKey = String(entry.matchup_id);
        if (!group.matchupIds.has(matchupKey)) group.matchupIds.set(matchupKey, entry.matchup_id);
        group.entries.push({ index, matchupKey });
    }

    const reads = [];
    for (const [database, tableGroups] of databaseGroups) {
        for (const [table, group] of tableGroups) {
            reads.push((async () => {
                const matchupIds = [...group.matchupIds.values()];
                const rows = await database.query(
                    `SELECT matchup_id, ID, strategy_id, strategic_outcome, uses FROM ${table} WHERE matchup_id IN (?)`,
                    [matchupIds]
                );
                const allRows = rows || [];
                const rowsByMatchup = new Map();
                let missingMatchupId = false;
                for (const row of allRows) {
                    if (row?.matchup_id === undefined || row?.matchup_id === null) {
                        missingMatchupId = true;
                        continue;
                    }
                    const key = String(row.matchup_id);
                    let matchingRows = rowsByMatchup.get(key);
                    if (!matchingRows) {
                        matchingRows = [];
                        rowsByMatchup.set(key, matchingRows);
                    }
                    matchingRows.push(row);
                }

                if (missingMatchupId) {
                    if (group.matchupIds.size !== 1) {
                        throw new Error(`Batched consolidation read from ${table} did not return matchup_id.`);
                    }
                    for (const entry of group.entries) results[entry.index] = allRows;
                    return;
                }

                for (const entry of group.entries) {
                    results[entry.index] = rowsByMatchup.get(entry.matchupKey) || [];
                }
            })());
        }
    }

    await Promise.all(reads);
    return results;
}

function patchConsolidation(server) {
    if (!server || server[CONSOLIDATION_PATCHED]) return server;
    server[CONSOLIDATION_PATCHED] = true;
    getConsolidationQueueKeys(server);

    server.consolidateOutcomes = async () => {
        const BATCH_SIZE = 1000;
        const started = performance.now();
        let hadFailure = false;
        const ownedQueue = server.consolidationQueue.splice(0, server.consolidationQueue.length);
        getConsolidationQueueKeys(server).clear();
        try {
            for (let offset = 0; offset < ownedQueue.length; offset += BATCH_SIZE) {
                const batch = ownedQueue.slice(offset, offset + BATCH_SIZE).map(entry => ({
                    ...entry,
                    table: normalizeConsolidationTable(entry.table),
                }));

                let outcomeResults;
                try {
                    outcomeResults = await loadConsolidationOutcomes(batch);
                } catch (error) {
                    hadFailure = true;
                    requeueConsolidations(server, batch);
                    continue;
                }

                let grouped;
                let affectedCaches;
                try {
                    grouped = {};
                    affectedCaches = [];
                    for (let index = 0; index < batch.length; index++) {
                        const { matchup_id, table, game } = batch[index];
                        const outcomes = outcomeResults[index];
                        if (!needsConsolidation(outcomes, game.config.maxOutcomesPerStratType)) continue;

                        const values = [];
                        for (const [strategyId, aggregate] of exactStrategyAggregates(outcomes)) {
                            if (aggregate.uses === 0n) continue;
                            values.push(...exactConsolidatedRows(
                                matchup_id,
                                strategyId,
                                aggregate.tsv,
                                aggregate.uses));
                        }
                        if (!grouped[table]) grouped[table] = [];
                        grouped[table].push({ matchup_id, values });
                        affectedCaches.push([table, matchup_id]);
                    }
                } catch (error) {
                    hadFailure = true;
                    requeueConsolidations(server, batch);
                    continue;
                }

                try {
                    await server.db.transaction(async query => {
                        for (const [table, entries] of Object.entries(grouped)) {
                            const matchupIds = [...new Set(entries.map(entry => entry.matchup_id))];
                            if (matchupIds.length === 0) continue;
                            const deleted = await query(`DELETE FROM ${table} WHERE matchup_id IN (?)`, [matchupIds]);
                            server.totalConsolidatedRows += deleted.affectedRows || 0;
                            const values = entries.flatMap(entry => entry.values);
                            if (values.length) {
                                const inserted = await query(
                                    `INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome, uses) VALUES ?`,
                                    [values]);
                                server.totalConsolidatedInsertRows += inserted.affectedRows || 0;
                            }
                        }
                    });
                    for (const [table, matchupId] of affectedCaches) {
                        invalidateStrategyCache(server, table, matchupId);
                    }
                } catch (error) {
                    hadFailure = true;
                    requeueConsolidations(server, batch);
                }
            }
        } finally {
            adoptQueuedConsolidations(server);
            console.log(`Consolidated ${server.totalConsolidatedRows} rows and inserted ${server.totalConsolidatedInsertRows} rows in ${(performance.now() - started).toFixed(2)}ms`);
            server.totalConsolidatedRows = 0;
            server.totalConsolidatedInsertRows = 0;
        }
        return { hadFailure };
    };

    server.runConsolidationQueue = async () => {
        if (hasBlockingConnections(server)) {
            server.isRunningConsolidation = false;
            setTimeout(server.runConsolidationQueue, CONSOLIDATION_RETRY_MS);
            return;
        }

        adoptQueuedConsolidations(server);
        if (server.consolidationQueue.length === 0) {
            server.isRunningConsolidation = false;
            setTimeout(server.runConsolidationQueue, CONSOLIDATION_RETRY_MS);
            return;
        }

        server.isRunningConsolidation = true;
        const started = performance.now();
        const result = await server.consolidateOutcomes();
        server.consolidationTime += performance.now() - started;

        if (result?.hadFailure) {
            setTimeout(server.runConsolidationQueue, CONSOLIDATION_RETRY_MS);
        } else if (server.consolidationQueue.length > 0 || server.consolidationMap.size > 0) {
            setImmediate(server.runConsolidationQueue);
        } else {
            server.isRunningConsolidation = false;
            setTimeout(server.runConsolidationQueue, CONSOLIDATION_RETRY_MS);
        }
    };

    return server;
}

function patchStrategyCaches(game) {
    if (!game || game[CACHE_CONTEXT]) return game;
    const context = new AsyncLocalStorage();
    game[CACHE_CONTEXT] = context;

    const originalGetStrategy = typeof game.getStrategy === 'function'
        ? game.getStrategy.bind(game)
        : null;
    const originalGetMatchupStrategy = typeof game.getMatchupStrategy === 'function'
        ? game.getMatchupStrategy.bind(game)
        : null;
    const originalFindStrategic = typeof game.findCachedStrategies === 'function'
        ? game.findCachedStrategies.bind(game)
        : null;
    const originalFindTargeting = typeof game.findCachedTargetingStrategies === 'function'
        ? game.findCachedTargetingStrategies.bind(game)
        : null;
    const originalAdd = typeof game.addToCachedStrategies === 'function'
        ? game.addToCachedStrategies.bind(game)
        : null;

    if (originalGetStrategy) {
        game.getStrategy = (matchup, bannedStrats, ...rest) =>
            context.run({
                table: 'strategic_commands',
                signature: availabilitySignature(bannedStrats),
            }, () => originalGetStrategy(matchup, bannedStrats, ...rest));
    }

    if (originalGetMatchupStrategy) {
        game.getMatchupStrategy = (shipString, opponentId, hash, timings, bannedStrats) =>
            context.run({
                table: 'targeting_outcomes',
                signature: availabilitySignature(bannedStrats),
            }, () => originalGetMatchupStrategy(shipString, opponentId, hash, timings, bannedStrats));
    }

    if (originalFindStrategic) {
        game.findCachedStrategies = matchupId => {
            const active = context.getStore();
            if (active?.table !== 'strategic_commands') return originalFindStrategic(matchupId);
            return game.server.cachedStrategies.get(strategyCacheKey(matchupId, active.signature));
        };
    }

    if (originalFindTargeting) {
        game.findCachedTargetingStrategies = matchupId => {
            const active = context.getStore();
            if (active?.table !== 'targeting_outcomes') return originalFindTargeting(matchupId);
            return game.server.cachedTargetingStrategies.get(strategyCacheKey(matchupId, active.signature));
        };
    }

    if (originalAdd) {
        game.addToCachedStrategies = (selectedStrat, matchupId, table, availableStrats, isCached) => {
            const active = context.getStore();
            let cacheMatchupId = matchupId;
            if ((table === 'strategic_commands' || table === 'targeting_outcomes') &&
                active?.table === table) {
                cacheMatchupId = strategyCacheKey(matchupId, active.signature);
            }
            return originalAdd(selectedStrat, cacheMatchupId, table, availableStrats, isCached);
        };
    }

    return game;
}

function patchGame(game) {
    if (!game || game[PATCHED]) return game;
    game[PATCHED] = true;
    game[STORE_TAIL] = Promise.resolve();
    patchConsolidation(game.server);
    patchStrategyCaches(game);

    game.getOutcomesFromId = async (matchupId, table) =>
        game.db.query(
            `SELECT ID, strategy_id, strategic_outcome, uses FROM ${table} WHERE matchup_id = ?`,
            [matchupId]
        );

    game.addToConsolidationQueue = (table, matchupId, outcomes) => {
        if (!needsConsolidation(outcomes, game.config.maxOutcomesPerStratType)) return;
        const key = consolidationKey(table, matchupId);
        if (!game.server.consolidationMap.has(key) && !getConsolidationQueueKeys(game.server).has(key)) {
            game.server.consolidationMap.set(key, {
                table,
                matchup_id: matchupId,
                game,
            });
        }
    };

    game.insertIntoTable = async (table, records) => {
        const start = performance.now();
        const result = await game.db.query(
            `INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome) VALUES ?`,
            [records]
        );
        game.server.totalInsertTime += performance.now() - start;
        return result;
    };

    game.matchUpdatesWithInsertsAndCommit = async () => {
        const start = performance.now();
        const updates = [...game.pendingUpdates];
        const buckets = {
            targeting_outcomes: [],
            shooting_outcomes: [],
            strategic_commands: [],
        };
        const matchedInsertIds = [];
        const affectedMatchups = [];

        for (const update of updates) {
            const insert = game.pendingInserts.get(update.id);
            if (!insert) continue;
            const values = [insert.matchup_id, insert.strategy_id, update.tsv];
            let table;
            if (insert.table === 0) table = 'targeting_outcomes';
            else if (insert.table === 1) table = 'shooting_outcomes';
            else if (insert.table === 2) table = 'strategic_commands';
            else continue;
            buckets[table].push(values);
            matchedInsertIds.push(update.id);
            affectedMatchups.push([table, insert.matchup_id]);
        }

        const hasWrites = Object.values(buckets).some(records => records.length > 0);
        if (hasWrites) {
            await game.db.transaction(async (query) => {
                for (const [table, records] of Object.entries(buckets)) {
                    if (records.length === 0) continue;
                    await query(
                        `INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome) VALUES ?`,
                        [records]
                    );
                }
            });
        }

        for (const [table, matchupId] of affectedMatchups) {
            invalidateStrategyCache(game.server, table, matchupId);
        }
        for (const id of matchedInsertIds) game.pendingInserts.delete(id);
        removeUpdates(game, updates);

        const now = performance.now();
        for (const [key, value] of game.pendingInserts) {
            if (now - value.time > 7200000) game.pendingInserts.delete(key);
        }

        const elapsed = performance.now() - start;
        game.server.totalInsertTime += elapsed;
        game.server.updateMatchingTime += elapsed;
        game.server.updateMatchingCount++;
    };

    game.storeState = (commands, shootingCommands, targetingCommands) => {
        const { updates: ownedUpdates, discardedOutcomeIds } =
            toStoreOperation(commands, shootingCommands, targetingCommands);
        const operation = game[STORE_TAIL].then(async () => {
            for (const outcomeId of discardedOutcomeIds) game.pendingInserts.delete(outcomeId);

            game.pendingUpdates.push(...ownedUpdates);
            try {
                await game.matchUpdatesWithInsertsAndCommit();
                return true;
            } catch (error) {
                removeUpdates(game, ownedUpdates);
                throw error;
            }
        });

        game[STORE_TAIL] = operation.catch(() => undefined);
        return operation;
    };

    return game;
}

module.exports = {
    CONSOLIDATION_RETRY_MS,
    patchGame,
    patchConsolidation,
    patchStrategyCaches,
    adoptQueuedConsolidations,
    availabilitySignature,
    strategyCacheKey,
    deleteCacheVariants,
    exactStrategyAggregate,
    exactConsolidatedRows,
    invalidateStrategyCache,
    loadConsolidationOutcomes,
    strategyRowCounts,
    needsConsolidation,
    toStoreOperation,
    toUpdates,
};