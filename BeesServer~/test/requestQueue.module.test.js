'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { patchServer } = require('../server');

function makeRuntime() {
    return {
        common: {
            nonce: () => 1,
            timer: () => 10,
            handleError(error) { throw error; },
        },
        SocketConnection: class {},
    };
}

test('request queue drains its entry snapshot without repeated Array.shift', async () => {
    const handled = [];
    const connection = {
        handleMessage(request) {
            handled.push(request.params.Hash);
            return true;
        },
    };
    const queue = [
        { connectionId: 1, params: { Hash: 1 }, timings: { startTime: 0 } },
        { connectionId: 1, params: { Hash: 2 }, timings: { startTime: 0 } },
        { connectionId: 1, params: { Hash: 3 }, timings: { startTime: 0 } },
    ];
    queue.shift = () => { throw new Error('quadratic shift drain must not be used'); };

    const server = {
        queue,
        connections: new Map([[1, connection]]),
        pendingRequests: new Map(),
        recent_requests: 0,
        totalQueueTime: 0,
        requestsStarted: 0,
        cacheFolder: '.',
    };
    patchServer(server, makeRuntime());

    server.runQueue();
    await Promise.resolve();

    assert.deepEqual(handled, [1, 2, 3]);
    assert.equal(server.queue.length, 0);
    assert.equal(server.requestsStarted, 3);
});

test('request queue schedules one drain on demand and does not poll while idle', async () => {
    const handled = [];
    const scheduled = [];
    const connection = {
        handleMessage(request) {
            handled.push(request.params.Hash);
            return true;
        },
    };
    const server = {
        queue: [],
        connections: new Map([[1, connection]]),
        pendingRequests: new Map(),
        recent_requests: 0,
        totalQueueTime: 0,
        requestsStarted: 0,
        cacheFolder: '.',
    };

    const originalSetImmediate = global.setImmediate;
    global.setImmediate = callback => {
        scheduled.push(callback);
        return scheduled.length;
    };
    try {
        patchServer(server, makeRuntime());
        server.runQueue();
        assert.equal(scheduled.length, 0, 'An empty drain must not schedule idle polling.');

        server.queue.push({ connectionId: 1, params: { Hash: 10 }, timings: { startTime: 0 } });
        server.queue.push({ connectionId: 1, params: { Hash: 11 }, timings: { startTime: 0 } });
        assert.equal(scheduled.length, 1, 'Concurrent pushes should share one scheduled drain.');

        scheduled.shift()();
        await Promise.resolve();
        assert.deepEqual(handled, [10, 11]);
        assert.equal(server.queue.length, 0);
        assert.equal(scheduled.length, 0, 'The queue should return to an unscheduled idle state.');
    } finally {
        global.setImmediate = originalSetImmediate;
    }
});
