'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime, pendingRequestKey } = require('../server');

function fakeConnection() {
    const listeners = new Map();
    return {
        on(event, callback) { listeners.set(event, callback); },
        emitMessage(params) {
            const callback = listeners.get('message');
            assert.ok(callback, 'SocketConnection must register a message listener.');
            return callback({ utf8Data: JSON.stringify(params) });
        },
    };
}

test('same request hash is independent across WebSocket connections', async () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: { createPool() { return { on() {} }; } },
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    const server = {
        pendingRequests: new Map(),
        queue: [],
    };
    const db = {};
    const firstTransport = fakeConnection();
    const secondTransport = fakeConnection();
    new runtime.SocketConnection(firstTransport, db, server, 11);
    new runtime.SocketConnection(secondTransport, db, server, 22);

    await firstTransport.emitMessage({ Hash: 777, Type: 'test-request' });
    await secondTransport.emitMessage({ Hash: 777, Type: 'test-request' });

    assert.equal(server.queue.length, 2,
        'Equal hashes from different connections must both enter the server queue.');
    assert.equal(server.pendingRequests.has(pendingRequestKey(11, 777)), true);
    assert.equal(server.pendingRequests.has(pendingRequestKey(22, 777)), true);

    await firstTransport.emitMessage({ Hash: 777, Type: 'test-request' });
    assert.equal(server.queue.length, 2,
        'A resend with the same hash on one connection must remain deduplicated.');
});
