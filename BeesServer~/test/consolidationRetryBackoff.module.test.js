'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    CONSOLIDATION_RETRY_MS,
    patchConsolidation,
} = require('../gamePersistence');

test('persistent consolidation read failure is requeued with timer backoff', async () => {
    const originalSetTimeout = global.setTimeout;
    const originalSetImmediate = global.setImmediate;
    const delays = [];
    let immediateCalls = 0;
    global.setTimeout = (_callback, delay) => {
        delays.push(delay);
        return 1;
    };
    global.setImmediate = () => {
        immediateCalls++;
        return 1;
    };

    try {
        const server = {
            connections: new Map(),
            consolidationMap: new Map(),
            consolidationQueue: [],
            totalConsolidatedRows: 0,
            totalConsolidatedInsertRows: 0,
            consolidationTime: 0,
            cachedStrategies: new Map(),
            cachedShootingStrategies: new Map(),
            cachedTargetingStrategies: new Map(),
            db: {
                async transaction() {
                    throw new Error('transaction should not start after read failure');
                },
            },
        };
        const game = {
            server,
            db: {
                async query() { throw new Error('database unavailable'); },
            },
            config: { maxOutcomesPerStratType: 10 },
            pendingUpdates: [],
            pendingInserts: new Map(),
        };
        server.consolidationQueue.push({ table: 2, matchup_id: 'matchup', game });

        patchConsolidation(server);
        await server.runConsolidationQueue();

        assert.equal(server.consolidationQueue.length, 1,
            'Failed maintenance work must remain queued for retry.');
        assert.deepEqual(delays, [CONSOLIDATION_RETRY_MS],
            'Persistent DB failure must retry on a timer, not in a hot loop.');
        assert.equal(immediateCalls, 0);
    } finally {
        global.setTimeout = originalSetTimeout;
        global.setImmediate = originalSetImmediate;
    }
});
