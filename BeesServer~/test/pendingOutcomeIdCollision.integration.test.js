'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime } = require('../server');

function mysqlStub() {
    return { createPool() { return { on() {} }; } };
}

test('loaded runtime retries outcome IDs that already exist in pendingInserts', () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });

    const game = new runtime.Game(null, null, {}, 0, 1);
    game.pendingInserts.set(111, {
        matchup_id: 1,
        strategy_id: 1,
        table: 2,
        time: 0,
    });

    const ids = [111, 222];
    const originalNonce = runtime.common.nonce;
    runtime.common.nonce = () => ids.shift();
    try {
        const outcomeId = game.insertOutcome(2, 42, 3, 0);
        assert.equal(outcomeId, 222,
            'A collision with a pending outcome ID must be retried.');
        assert.equal(game.pendingInserts.has(111), true,
            'The existing pending reservation must not be overwritten.');
        assert.equal(game.pendingInserts.has(222), true,
            'The replacement ID must own the new pending reservation.');
        assert.equal(game.pendingInserts.get(222).matchup_id, 42);
        assert.equal(game.pendingInserts.get(222).strategy_id, 3);
    } finally {
        runtime.common.nonce = originalNonce;
    }
});
