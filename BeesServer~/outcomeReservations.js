'use strict';

const { performance } = require('node:perf_hooks');

const RESERVATION_PREFIX = '__hivemind_outcome__:';
const CLEANUP_INTERVAL_MS = 60 * 1000;
const LAST_CLEANUP = Symbol('beesOutcomeReservationCleanup');
const DURABILITY_PATCHED = Symbol('beesOutcomeDurabilityPatched');
const DURABLE_STORE_TAIL = Symbol('beesOutcomeDurableStoreTail');

class StaleOutcomeReservationError extends Error {
    constructor(ids) {
        super(`Outcome reservation metadata is unavailable for: ${ids.join(', ')}`);
        this.name = 'StaleOutcomeReservationError';
        this.statusCode = 409;
        this.staleOutcomeIds = ids;
    }
}

function hasUserId(game) {
    return game?.userId !== undefined && game?.userId !== null && String(game.userId).trim() !== '';
}

function requireUserId(game) {
    if (!hasUserId(game)) throw new Error('Hive Mind outcome reservation requires a game user id.');
    return String(game.userId);
}

function reservationFilename(outcomeId) {
    const id = Number(outcomeId);
    if (!Number.isSafeInteger(id) || id <= 0) throw new RangeError(`Invalid outcome reservation id: ${outcomeId}`);
    return `${RESERVATION_PREFIX}${id}`;
}

function tableNameFromCode(table) {
    if (table === 0) return 'targeting_outcomes';
    if (table === 1) return 'shooting_outcomes';
    if (table === 2) return 'strategic_commands';
    return null;
}

function reservationFromPending(outcomeId, pending) {
    if (!pending || !tableNameFromCode(pending.table)) throw new TypeError(`Invalid pending outcome reservation ${outcomeId}.`);
    const strategyId = Number(pending.strategy_id);
    if (!Number.isSafeInteger(strategyId)) throw new RangeError(`Invalid strategy id for outcome reservation ${outcomeId}.`);
    return { outcome_id: Number(outcomeId), table: Number(pending.table), matchup_id: String(pending.matchup_id), strategy_id: strategyId, created_at: Date.now() };
}

function parseReservation(filename, contents) {
    if (!String(filename).startsWith(RESERVATION_PREFIX)) return null;
    let value;
    try { value = JSON.parse(contents); } catch { return null; }
    const id = Number(value?.outcome_id);
    if (!Number.isSafeInteger(id) || id <= 0 || reservationFilename(id) !== filename || !tableNameFromCode(Number(value?.table))) return null;
    const strategyId = Number(value.strategy_id);
    if (!Number.isSafeInteger(strategyId)) return null;
    return { outcome_id: id, table: Number(value.table), matchup_id: String(value.matchup_id), strategy_id: strategyId, created_at: Number(value.created_at) || 0 };
}

async function cleanupInvalidReservations(game, now = Date.now()) {
    if (now - Number(game[LAST_CLEANUP] || 0) < CLEANUP_INTERVAL_MS) return;
    game[LAST_CLEANUP] = now;
    const rows = await game.db.query('SELECT ID, filename, contents FROM stored_user_data WHERE userId = ? AND filename LIKE ?', [requireUserId(game), `${RESERVATION_PREFIX}%`]);
    const invalidIds = [];
    for (const row of rows || []) {
        if (!parseReservation(row.filename, row.contents)) invalidIds.push(row.ID);
    }
    if (invalidIds.length) await game.db.query('DELETE FROM stored_user_data WHERE ID IN (?)', [invalidIds]);
}

function strategyResultOutcomeIds(result) {
    if (!result || typeof result !== 'object') return [];
    return [...new Set([result.OutcomeId, result.ShootingStrategyOutcomeId]
        .filter(value => value !== undefined && value !== null)
        .map(Number)
        .filter(id => Number.isSafeInteger(id) && id > 0))];
}

function discardedOutcomeIds(commandGroups) {
    const ids = new Set();
    for (const commands of commandGroups) {
        for (const command of commands || []) {
            const id = Number(command?.OutcomeId);
            if (command?.DiscardReservation === true && Number.isSafeInteger(id) && id > 0) ids.add(id);
        }
    }
    return [...ids];
}

async function persistReservations(game, outcomeIds) {
    const userId = requireUserId(game);
    await cleanupInvalidReservations(game);
    const ids = [...new Set((outcomeIds || []).map(Number).filter(id => Number.isSafeInteger(id) && id > 0))];
    if (!ids.length) return;
    const reservations = ids.map(id => reservationFromPending(id, game.pendingInserts.get(id)));
    const filenames = reservations.map(item => reservationFilename(item.outcome_id));
    const collisions = await game.db.query('SELECT filename FROM stored_user_data WHERE userId = ? AND filename IN (?)', [userId, filenames]);
    if (collisions?.length) {
        for (const reservation of reservations) game.pendingInserts.delete(reservation.outcome_id);
        throw new Error(`Outcome reservation id collision for user ${userId}.`);
    }
    try {
        await game.db.query('INSERT INTO stored_user_data (userId, filename, contents) VALUES ?', [reservations.map(item => [userId, reservationFilename(item.outcome_id), JSON.stringify(item)])]);
    } catch (error) {
        for (const reservation of reservations) game.pendingInserts.delete(reservation.outcome_id);
        throw error;
    }
}

async function loadReservations(game, outcomeIds) {
    const ids = [...new Set((outcomeIds || []).map(Number).filter(id => Number.isSafeInteger(id) && id > 0))];
    if (!ids.length) return new Map();
    const rows = await game.db.query('SELECT ID, filename, contents FROM stored_user_data WHERE userId = ? AND filename IN (?) ORDER BY ID DESC', [requireUserId(game), ids.map(reservationFilename)]);
    const reservations = new Map();
    for (const row of rows || []) {
        const reservation = parseReservation(row.filename, row.contents);
        if (reservation && !reservations.has(reservation.outcome_id)) reservations.set(reservation.outcome_id, reservation);
    }
    return reservations;
}

async function deleteReservations(game, outcomeIds, query) {
    const ids = [...new Set((outcomeIds || []).map(Number).filter(id => Number.isSafeInteger(id) && id > 0))];
    if (!ids.length) return;
    await query('DELETE FROM stored_user_data WHERE userId = ? AND filename IN (?)', [requireUserId(game), ids.map(reservationFilename)]);
}

function patchOutcomeDurability(game, invalidateStrategyCache) {
    if (!game || game[DURABILITY_PATCHED]) return game;
    game[DURABILITY_PATCHED] = true;
    game[DURABLE_STORE_TAIL] = Promise.resolve();

    const wrapStrategyRequest = original => async (...args) => {
        const result = await original(...args);
        if (hasUserId(game)) await persistReservations(game, strategyResultOutcomeIds(result));
        return result;
    };
    if (typeof game.getStrategy === 'function') game.getStrategy = wrapStrategyRequest(game.getStrategy.bind(game));
    if (typeof game.getMatchupStrategy === 'function') game.getMatchupStrategy = wrapStrategyRequest(game.getMatchupStrategy.bind(game));

    game.matchUpdatesWithInsertsAndCommit = async () => {
        const start = performance.now();
        const updates = [...game.pendingUpdates];
        const durable = hasUserId(game) ? await loadReservations(game, updates.map(update => update.id)) : new Map();
        const missing = [...new Set(updates
            .map(update => Number(update.id))
            .filter(id => !durable.has(id) && !game.pendingInserts.has(id)))];
        const missingSet = new Set(missing);
        const availableUpdates = updates.filter(update => !missingSet.has(Number(update.id)));

        const buckets = { targeting_outcomes: [], shooting_outcomes: [], strategic_commands: [] };
        const matchedIds = [];
        const durableIds = [];
        const affected = {
            targeting_outcomes: new Set(),
            shooting_outcomes: new Set(),
            strategic_commands: new Set(),
        };
        for (const update of availableUpdates) {
            const id = Number(update.id);
            const insert = durable.get(id) || game.pendingInserts.get(id);
            const table = tableNameFromCode(Number(insert.table));
            if (!table || table !== update.table) throw new Error(`Outcome reservation ${id} does not match ${update.table}.`);
            const tsv = Number(update.tsv);
            if (!Number.isSafeInteger(tsv)) throw new RangeError(`Invalid TSV for outcome reservation ${id}.`);
            const matchupId = String(insert.matchup_id);
            buckets[table].push([matchupId, Number(insert.strategy_id), tsv]);
            matchedIds.push(id);
            if (durable.has(id)) durableIds.push(id);
            affected[table].add(matchupId);
        }

        await game.db.transaction(async query => {
            for (const [table, records] of Object.entries(buckets)) {
                if (records.length) await query(`INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome) VALUES ?`, [records]);
            }
            if (hasUserId(game)) await deleteReservations(game, durableIds, query);
        });
        for (const [table, matchupIds] of Object.entries(affected)) {
            for (const matchupId of matchupIds) invalidateStrategyCache(game.server, table, matchupId);
        }
        for (const id of matchedIds) game.pendingInserts.delete(id);

        let ownsPrefix = game.pendingUpdates.length >= updates.length;
        for (let index = 0; ownsPrefix && index < updates.length; index++) {
            if (game.pendingUpdates[index] !== updates[index]) ownsPrefix = false;
        }
        if (ownsPrefix) {
            game.pendingUpdates.splice(0, updates.length);
        } else {
            const owned = new Set(updates);
            game.pendingUpdates = game.pendingUpdates.filter(update => !owned.has(update));
        }
        const elapsed = performance.now() - start;
        game.server.totalInsertTime += elapsed;
        game.server.updateMatchingTime += elapsed;
        game.server.updateMatchingCount++;

        if (missing.length) throw new StaleOutcomeReservationError(missing);
    };

    const originalStoreState = game.storeState.bind(game);
    game.storeState = (commands, shootingCommands, targetingCommands) => {
        const discarded = discardedOutcomeIds([commands, shootingCommands, targetingCommands]);
        const operation = game[DURABLE_STORE_TAIL].then(async () => {
            if (discarded.length && hasUserId(game)) {
                await game.db.transaction(query => deleteReservations(game, discarded, query));
            }
            return originalStoreState(commands, shootingCommands, targetingCommands);
        });
        game[DURABLE_STORE_TAIL] = operation.catch(() => undefined);
        return operation;
    };
    return game;
}

module.exports = { StaleOutcomeReservationError, RESERVATION_PREFIX, strategyResultOutcomeIds, discardedOutcomeIds, patchOutcomeDurability };
