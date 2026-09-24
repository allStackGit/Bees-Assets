'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { patchSocketConnection } = require('../server');

function request(type, hash) {
    return { params: { Type: type, Hash: hash } };
}

test('ordinary requests wait for prior connection-state mutations without serializing each other', async () => {
    let releaseSetup;
    const setupGate = new Promise(resolve => { releaseSetup = resolve; });
    let releaseStrategies;
    const strategyGate = new Promise(resolve => { releaseStrategies = resolve; });
    const started = [];

    const socket = {
        id: 9,
        game: null,
        user: null,
        async handleMessage(current) {
            started.push(current.params.Hash);
            if (current.params.Type === 'setup-level') await setupGate;
            else await strategyGate;
            return true;
        },
    };
    const server = { pendingRequests: new Map() };
    patchSocketConnection(socket, server);

    const setup = socket.handleMessage(request('setup-level', 'setup'));
    const firstStrategy = socket.handleMessage(request('get-strategy', 'strategy-a'));
    const secondStrategy = socket.handleMessage(request('get-strategy', 'strategy-b'));
    await new Promise(resolve => setImmediate(resolve));

    assert.deepEqual(started, ['setup'],
        'Requests that depend on Game state must not overtake an earlier setup/reconnect mutation.');

    releaseSetup();
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(started, ['setup', 'strategy-a', 'strategy-b'],
        'Once the state tail settles, ordinary requests should be free to start concurrently.');

    releaseStrategies();
    assert.equal(await setup, true);
    assert.equal(await firstStrategy, true);
    assert.equal(await secondStrategy, true);
});
