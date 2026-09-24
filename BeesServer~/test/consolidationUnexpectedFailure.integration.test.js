'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer, patchGame } = require('../server');

function mysqlStub() {
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

test('unexpected consolidation aggregation failure stays retryable without rejecting', async () => {
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    const game = patchGame({
        config: { maxOutcomesPerStratType: 1 },
        db: { async query() { return [
            { matchup_id: 42, strategy_id: 3, strategic_outcome: 10, uses: 1 },
            { matchup_id: 42, strategy_id: 3, strategic_outcome: 20, uses: 'invalid' },
        ]; } },
        server,
        pendingUpdates: [],
        pendingInserts: new Map(),
    });
    server.consolidationQueue = [{ table: 2, matchup_id: 42, game }];

    const result = await server.consolidateOutcomes();
    assert.equal(result.hadFailure, true);
    assert.equal(server.consolidationQueue.length, 1,
        'Unexpected aggregation failure must leave its batch available for later retry.');
    assert.equal(server.consolidationQueue[0].matchup_id, 42);
});
