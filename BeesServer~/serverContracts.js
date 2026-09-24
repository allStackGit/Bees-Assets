'use strict';

function assertFiniteNonNegative(value, name) {
    if (!Number.isFinite(value) || value < 0) {
        throw new RangeError(`${name} must be a finite non-negative number`);
    }
}

function isMap(value) {
    // siServerDev.js is intentionally loaded through node:vm by server.js. Maps created in
    // that VM are genuine Maps but fail `instanceof Map` in this module's realm.
    return value instanceof Map || Object.prototype.toString.call(value) === '[object Map]';
}

/**
 * Removes strategy-cache entries older than maxAge using the same monotonic
 * time domain in which each entry's age was recorded.
 */
function pruneExpiredEntries(cache, now, maxAge) {
    if (!isMap(cache)) {
        throw new TypeError('cache must be a Map');
    }
    assertFiniteNonNegative(now, 'now');
    assertFiniteNonNegative(maxAge, 'maxAge');

    let removed = 0;
    for (const [key, entry] of cache) {
        if (!entry || !Number.isFinite(entry.age)) {
            throw new TypeError(`cache entry ${String(key)} must have a finite age`);
        }
        if (now - entry.age > maxAge) {
            cache.delete(key);
            removed++;
        }
    }
    return removed;
}

/**
 * Loads the append-only matchup cache without making cache files a startup
 * dependency. Missing, empty, truncated, or otherwise malformed cache files are
 * recoverable because matchup IDs can always be rebuilt from the database/hash.
 */
function loadCacheMapSafely(readText, onError = () => {}) {
    if (typeof readText !== 'function') {
        throw new TypeError('readText must be a function');
    }
    if (typeof onError !== 'function') {
        throw new TypeError('onError must be a function');
    }

    let text;
    try {
        text = readText();
    } catch (error) {
        onError(error);
        return new Map();
    }

    if (typeof text !== 'string' || text.trim() === '') {
        return new Map();
    }

    try {
        const trimmed = text.trim();
        // A complete cache file is a JSON array of entry arrays (starts with "[[").
        // Legacy append-only files are a comma-separated sequence of entry arrays and
        // therefore also start with "["; wrap only that legacy form in an outer array.
        const entries = trimmed.startsWith('[[')
            ? JSON.parse(trimmed)
            : JSON.parse(`[${trimmed.replace(/,\s*$/, '')}]`);
        if (!Array.isArray(entries)) {
            throw new TypeError('cache contents must decode to an array');
        }
        return new Map(entries);
    } catch (error) {
        onError(error);
        return new Map();
    }
}

/**
 * A database read failure must never be represented as a successful "missing
 * file" result. The client bootstraps defaults only for an explicit missing
 * result, so conflating these states can overwrite valid persisted user data.
 */
function classifyUserDataRead(rows, error = null) {
    if (error) {
        return 'error';
    }
    if (!Array.isArray(rows)) {
        throw new TypeError('rows must be an array when there is no read error');
    }
    return rows.length > 0 ? 'found' : 'missing';
}

/**
 * Generates an ID that is unique among currently pending entries. Outcome IDs
 * only need to be unique until their pending insert is durably stored, so this
 * avoids both collision overwrite and an unbounded lifetime uniqueness set.
 */
function nextUniquePendingId(pending, generateId) {
    if (!(pending instanceof Map)) {
        throw new TypeError('pending must be a Map');
    }
    if (typeof generateId !== 'function') {
        throw new TypeError('generateId must be a function');
    }

    let id = generateId();
    while (pending.has(id)) {
        id = generateId();
    }
    return id;
}

/**
 * Runs all transaction work on one checked-out pool connection. mysql/mysql2
 * transactions are connection-scoped; START/COMMIT/ROLLBACK through separate
 * pooled query calls do not form one transaction.
 */
async function withTransaction(pool, work) {
    if (!pool || typeof pool.getConnection !== 'function') {
        throw new TypeError('pool must provide getConnection');
    }
    if (typeof work !== 'function') {
        throw new TypeError('work must be a function');
    }

    const connection = await new Promise((resolve, reject) => {
        pool.getConnection((error, value) => error ? reject(error) : resolve(value));
    });

    try {
        await callConnectionMethod(connection, 'beginTransaction');
        const result = await work(connection);
        await callConnectionMethod(connection, 'commit');
        return result;
    } catch (error) {
        try {
            await callConnectionMethod(connection, 'rollback');
        } catch (rollbackError) {
            error.rollbackError = rollbackError;
        }
        throw error;
    } finally {
        connection.release();
    }
}

function callConnectionMethod(connection, method) {
    if (!connection || typeof connection[method] !== 'function') {
        return Promise.reject(new TypeError(`connection must provide ${method}`));
    }
    return new Promise((resolve, reject) => {
        connection[method]((error) => error ? reject(error) : resolve());
    });
}

/**
 * Ensures a request hash can be retried after queued work is abandoned because
 * its originating connection no longer exists or request processing fails.
 */
function releasePendingRequest(pendingRequests, hash) {
    if (!(pendingRequests instanceof Map)) {
        throw new TypeError('pendingRequests must be a Map');
    }
    return pendingRequests.delete(hash);
}

/**
 * One WebSocket connection owns one shared Hive Mind Game. Sibling levels send
 * separate setup-level messages on that socket, but those messages must not
 * replace the Game and discard pending outcomes already issued to another level.
 */
function getOrCreateConnectionGame(existingGame, createGame) {
    if (existingGame) {
        return existingGame;
    }
    if (typeof createGame !== 'function') {
        throw new TypeError('createGame must be a function');
    }
    return createGame();
}

/**
 * Test/integration server runs must never use the live RAM database.
 */
function databaseNameForMode(isTest) {
    return isTest ? 'bees_test' : 'ram';
}

/**
 * Outcome storage is acknowledged only after every non-empty database batch has
 * completed successfully. A rejected write must reject the whole operation so
 * the request remains retryable and pending outcome metadata can be retained.
 */
async function persistOutcomeBatches(batches, writeBatch) {
    if (!batches || typeof batches !== 'object' || Array.isArray(batches)) {
        throw new TypeError('batches must be an object');
    }
    if (typeof writeBatch !== 'function') {
        throw new TypeError('writeBatch must be a function');
    }

    const writes = [];
    for (const [table, records] of Object.entries(batches)) {
        if (!Array.isArray(records)) {
            throw new TypeError(`${table} records must be an array`);
        }
        if (records.length > 0) {
            writes.push(writeBatch(table, records));
        }
    }
    await Promise.all(writes);
    return true;
}

module.exports = {
    pruneExpiredEntries,
    loadCacheMapSafely,
    classifyUserDataRead,
    nextUniquePendingId,
    withTransaction,
    releasePendingRequest,
    getOrCreateConnectionGame,
    databaseNameForMode,
    persistOutcomeBatches,
};
