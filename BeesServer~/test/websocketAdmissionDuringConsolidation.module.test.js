'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { patchServer } = require('../server');

class FakeSocketConnection {
    constructor(connection, db, server, id) {
        this.connection = connection;
        this.db = db;
        this.server = server;
        this.id = id;
        this.handleMessage = async () => {};
    }
}

test('pending consolidation does not reject a new websocket connection', () => {
    const server = {
        db: {},
        connections: new Map(),
        consolidationQueue: [{ matchup_id: 1 }],
        queue: [],
        pendingRequests: new Map(),
        cacheFolder: '.',
        cachedMatchups: new Map(),
        cachedShootingMatchups: new Map(),
        cachedTargetingMatchups: new Map(),
        originIsAllowed: () => true,
    };
    const runtime = {
        common: {
            nonce: () => 123,
            timer: () => 0,
            handleError: () => {},
        },
        SocketConnection: FakeSocketConnection,
    };

    patchServer(server, runtime);

    let rejected = false;
    let accepted = false;
    server.handleWSRequest({
        origin: 'game-client',
        reject: () => { rejected = true; },
        accept: (protocol) => {
            assert.equal(protocol, 'game');
            accepted = true;
            return {};
        },
    });

    assert.equal(rejected, false);
    assert.equal(accepted, true);
    assert.equal(server.connections.size, 1);
    assert.equal(server.connections.has(123), true);
});
