'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    CONSOLIDATION_RETRY_MS,
    beginOutcomeWrite,
    endOutcomeWrite,
    installInFlightWriteConsolidationGuard,
} = require('../security');

test('consolidation is deferred while an outcome write remains in flight', async () => {
    const calls = [];
    let scheduled = null;
    const server = {
        runConsolidationQueue: async () => { calls.push('initial'); },
    };

    installInFlightWriteConsolidationGuard(server, (callback, delay) => {
        scheduled = { callback, delay };
    });

    beginOutcomeWrite(server);
    await server.runConsolidationQueue();

    assert.deepEqual(calls, []);
    assert.ok(scheduled);
    assert.equal(scheduled.delay, CONSOLIDATION_RETRY_MS);

    endOutcomeWrite(server);
    await scheduled.callback();
    assert.deepEqual(calls, ['initial']);
});

test('the consolidation guard preserves later runner replacements and write-count balance', async () => {
    const calls = [];
    const server = {
        runConsolidationQueue: async () => { calls.push('initial'); },
    };

    installInFlightWriteConsolidationGuard(server, () => {});
    server.runConsolidationQueue = async () => { calls.push('replacement'); };

    beginOutcomeWrite(server);
    beginOutcomeWrite(server);
    endOutcomeWrite(server);
    assert.equal(server.__beesInFlightOutcomeWrites, 1);
    endOutcomeWrite(server);
    endOutcomeWrite(server);
    assert.equal(server.__beesInFlightOutcomeWrites, 0);

    await server.runConsolidationQueue();
    assert.deepEqual(calls, ['replacement']);
});
