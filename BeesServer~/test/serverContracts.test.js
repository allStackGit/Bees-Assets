'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    pruneExpiredEntries,
    loadCacheMapSafely,
    classifyUserDataRead,
    nextUniquePendingId,
    withTransaction,
    releasePendingRequest,
    getOrCreateConnectionGame,
    databaseNameForMode,
    persistOutcomeBatches,
} = require('../serverContracts');

test('cache cleanup removes only entries older than the maximum age', () => {
    const cache = new Map([
        ['expired', { age: 99, value: 1 }],
        ['boundary', { age: 100, value: 2 }],
        ['fresh', { age: 150, value: 3 }],
    ]);

    assert.equal(pruneExpiredEntries(cache, 200, 100), 1);
    assert.deepEqual([...cache.keys()], ['boundary', 'fresh']);
});

test('cache cleanup uses elapsed age rather than treating timestamps as durations', () => {
    const cache = new Map([['recent', { age: 950_000 }]]);
    assert.equal(pruneExpiredEntries(cache, 1_000_000, 60_000), 0);
    assert.equal(cache.has('recent'), true);
});

test('cache cleanup is safe on an empty cache', () => {
    assert.equal(pruneExpiredEntries(new Map(), 10, 5), 0);
});

test('cache cleanup rejects invalid clocks, limits, and entries', () => {
    assert.throws(() => pruneExpiredEntries({}, 10, 5), /Map/);
    assert.throws(() => pruneExpiredEntries(new Map(), Number.NaN, 5), /now/);
    assert.throws(() => pruneExpiredEntries(new Map(), 10, -1), /maxAge/);
    assert.throws(() => pruneExpiredEntries(new Map([['bad', {}]]), 10, 5), /finite age/);
});

test('cache loader preserves large string IDs from append-only cache files', () => {
    const id = '18446744073709551615';
    const cache = loadCacheMapSafely(() => `["ABC","${id}"],["DEF","42"],`);
    assert.equal(cache.get('ABC'), id);
    assert.equal(cache.get('DEF'), '42');
});

test('cache loader treats a missing cache file as an empty recoverable cache', () => {
    const errors = [];
    const cache = loadCacheMapSafely(() => {
        const error = new Error('missing');
        error.code = 'ENOENT';
        throw error;
    }, error => errors.push(error.code));
    assert.equal(cache.size, 0);
    assert.deepEqual(errors, ['ENOENT']);
});

test('cache loader treats malformed or truncated cache contents as recoverable', () => {
    const errors = [];
    const cache = loadCacheMapSafely(() => '["ABC","123"', error => errors.push(error));
    assert.equal(cache.size, 0);
    assert.equal(errors.length, 1);
});

test('user-data read contract distinguishes existing and missing files', () => {
    assert.equal(classifyUserDataRead([{ filename: 'user_progress' }]), 'found');
    assert.equal(classifyUserDataRead([]), 'missing');
});

test('user-data read contract never converts a database error into missing data', () => {
    assert.equal(classifyUserDataRead(undefined, new Error('database unavailable')), 'error');
});

test('user-data read contract rejects invalid rows on a successful read', () => {
    assert.throws(() => classifyUserDataRead(undefined), /rows must be an array/);
});

test('pending outcome ID generator retries collisions without lifetime state', () => {
    const pending = new Map([[41, {}], [42, {}]]);
    const ids = [41, 42, 43];
    const id = nextUniquePendingId(pending, () => ids.shift());
    assert.equal(id, 43);
    assert.equal(pending.size, 2);
});

test('pending request release makes a stranded hash retryable', () => {
    const pending = new Map([[123, 1]]);
    assert.equal(releasePendingRequest(pending, 123), true);
    assert.equal(pending.has(123), false);
    assert.equal(releasePendingRequest(pending, 123), false);
});

test('connection game setup reuses an existing shared game without creating another', () => {
    const existing = { id: 7, pendingInserts: new Map([[1, {}]]) };
    let created = 0;
    const game = getOrCreateConnectionGame(existing, () => {
        created++;
        return { id: 8 };
    });

    assert.equal(game, existing);
    assert.equal(created, 0);
    assert.equal(game.pendingInserts.has(1), true);
});

test('connection game setup creates a game exactly once when none exists', () => {
    let created = 0;
    const game = getOrCreateConnectionGame(null, () => {
        created++;
        return { id: 9 };
    });

    assert.deepEqual(game, { id: 9 });
    assert.equal(created, 1);
});

test('connection game setup requires a factory only when creation is needed', () => {
    const existing = { id: 10 };
    assert.equal(getOrCreateConnectionGame(existing), existing);
    assert.throws(() => getOrCreateConnectionGame(null), /createGame/);
});

test('test mode selects isolated database and normal mode selects live database', () => {
    assert.equal(databaseNameForMode(true), 'bees_test');
    assert.equal(databaseNameForMode(false), 'ram');
});

test('outcome persistence waits for every non-empty batch before succeeding', async () => {
    const events = [];
    const result = await persistOutcomeBatches({
        targeting_outcomes: [[1, 2, 3]],
        shooting_outcomes: [],
        strategic_commands: [[4, 5, 6]],
    }, async (table, records) => {
        events.push(`start:${table}`);
        await Promise.resolve();
        events.push(`done:${table}:${records.length}`);
    });

    assert.equal(result, true);
    assert.deepEqual(events.sort(), [
        'done:strategic_commands:1',
        'done:targeting_outcomes:1',
        'start:strategic_commands',
        'start:targeting_outcomes',
    ]);
});

test('outcome persistence rejects instead of acknowledging a failed database write', async () => {
    const writes = [];
    await assert.rejects(
        persistOutcomeBatches({
            targeting_outcomes: [[1]],
            shooting_outcomes: [[2]],
            strategic_commands: [[3]],
        }, async (table) => {
            writes.push(table);
            if (table === 'shooting_outcomes') {
                throw new Error('database write failed');
            }
        }),
        /database write failed/
    );
    assert.equal(writes.includes('shooting_outcomes'), true);
});

function fakePool(events, failAt) {
    const connection = {
        beginTransaction(callback) {
            events.push('begin');
            callback(failAt === 'begin' ? new Error('begin failed') : null);
        },
        commit(callback) {
            events.push('commit');
            callback(failAt === 'commit' ? new Error('commit failed') : null);
        },
        rollback(callback) {
            events.push('rollback');
            callback(failAt === 'rollback' ? new Error('rollback failed') : null);
        },
        release() {
            events.push('release');
        },
    };
    return {
        getConnection(callback) {
            events.push('checkout');
            callback(null, connection);
        },
    };
}

test('transaction helper keeps work on one checked-out connection', async () => {
    const events = [];
    const result = await withTransaction(fakePool(events), async (connection) => {
        events.push('work');
        assert.ok(connection);
        return 7;
    });

    assert.equal(result, 7);
    assert.deepEqual(events, ['checkout', 'begin', 'work', 'commit', 'release']);
});

test('transaction helper rolls back and releases on work failure', async () => {
    const events = [];
    await assert.rejects(
        withTransaction(fakePool(events), async () => {
            events.push('work');
            throw new Error('write failed');
        }),
        /write failed/
    );
    assert.deepEqual(events, ['checkout', 'begin', 'work', 'rollback', 'release']);
});

test('transaction helper releases connection after commit failure', async () => {
    const events = [];
    await assert.rejects(withTransaction(fakePool(events, 'commit'), async () => {
        events.push('work');
    }), /commit failed/);
    assert.deepEqual(events, ['checkout', 'begin', 'work', 'commit', 'rollback', 'release']);
});
