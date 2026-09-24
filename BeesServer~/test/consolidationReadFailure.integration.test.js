'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer, patchGame } = require('../server');

function createMysqlStub() {
    return {
        createPool() {
            return {
                on() {},
                getConnection(callback) {
                    callback(null, {
                        query(sql, values, done) { done(null, []); },
                        release() {},
                    });
                },
            };
        },
    };
}

test('consolidation read failure remains queued for retry without rejecting', async () => {
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: createMysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    const readError = new Error('transient read failure');
    const game = patchGame({
        config: { maxOutcomesPerStratType: 1 },
        db: { async query() { throw readError; } },
        server,
        pendingUpdates: [],
        pendingInserts: new Map(),
        addOutcomes() { throw new Error('must not aggregate after failed read'); },
    });
    server.consolidationQueue = [{ table: 2, matchup_id: 42, game }];

    const result = await server.consolidateOutcomes();

    assert.equal(result.hadFailure, true);
    assert.equal(server.consolidationQueue.length, 1,
        'A transient read failure must keep its consolidation batch retryable.');
    assert.equal(server.consolidationQueue[0].matchup_id, 42);
});
