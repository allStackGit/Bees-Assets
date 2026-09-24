'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { EventEmitter } = require('node:events');
const {
    SHARED_READ_ONLY_FILES,
    partitionStoredCommands,
    installRuntimeSecurity,
} = require('../security');

test('partitionStoredCommands separates stale outcomes from current reservations', () => {
    const pending = new Map([[11, {}], [22, {}]]);
    const result = partitionStoredCommands({
        Commands: [{ OutcomeId: 11 }, { OutcomeId: 12 }],
        ShootingCommands: [{ OutcomeId: 22 }],
        TargetingCommands: [{ OutcomeId: 23 }],
    }, pending);

    assert.deepEqual(result.commands, [{ OutcomeId: 11 }]);
    assert.deepEqual(result.shootingCommands, [{ OutcomeId: 22 }]);
    assert.deepEqual(result.targetingCommands, []);
    assert.deepEqual(result.staleOutcomeIds.sort((a, b) => a - b), [12, 23]);
});

test('shared campaign and challenge level files are server-authored', () => {
    assert.equal(SHARED_READ_ONLY_FILES.has('campaign_levels_data'), true);
    assert.equal(SHARED_READ_ONLY_FILES.has('challenge_levels_data'), true);
    assert.equal(SHARED_READ_ONLY_FILES.has('levels_data'), false);
});

test('runtime security replaces the malformed-frame-prone websocket listener', () => {
    class LegacySocketConnection {
        constructor(connection, db, server, id) {
            this.connection = connection;
            this.db = db;
            this.server = server;
            this.id = id;
            connection.on('message', async message => JSON.parse(message.utf8Data));
            this.handleMessage = async () => true;
        }
    }
    class SocketRequest {
        constructor(params, connection, server, startTime, queueTime, messageId, connectionId) {
            this.params = params;
            this.connectionId = connectionId;
            this.timings = { startTime };
        }
    }
    const runtime = {
        SocketConnection: LegacySocketConnection,
        SocketRequest,
        common: { timer: () => 1, handleError: () => {} },
    };
    installRuntimeSecurity(runtime);

    const connection = new EventEmitter();
    const server = { test: true, pendingRequests: new Map(), queue: [] };
    new runtime.SocketConnection(connection, {}, server, 7);
    assert.doesNotThrow(() => connection.emit('message', { type: 'utf8', utf8Data: '{bad json' }));
    assert.equal(server.queue.length, 0);
});

test('concurrent unauthenticated requests share one Steam authentication attempt', async () => {
    class LegacySocketConnection {
        constructor(connection, db, server, id) {
            this.connection = connection;
            this.db = db;
            this.server = server;
            this.id = id;
            this.handleMessage = async () => true;
        }
    }

    let authenticationCalls = 0;
    let resolveAuthentication;
    const authenticationGate = new Promise(resolve => { resolveAuthentication = resolve; });
    const runtime = {
        SocketConnection: LegacySocketConnection,
        common: { timer: () => 1, handleError: () => {} },
    };
    installRuntimeSecurity(runtime, {
        authenticateSteamTicket: async () => {
            authenticationCalls++;
            return authenticationGate;
        },
    });

    const connection = new EventEmitter();
    const server = {
        test: false,
        isRunningConsolidation: false,
        games: new Map(),
        pendingRequests: new Map(),
        queue: [],
    };
    const socket = new runtime.SocketConnection(connection, {}, server, 17);
    const request = hash => ({
        params: {
            Type: 'get-strategy',
            Hash: hash,
            UserId: '76561198000000000',
            AuthTicket: 'abcdef',
        },
        respond: () => {},
    });

    const first = socket.handleMessage(request('first'));
    const second = socket.handleMessage(request('second'));
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(authenticationCalls, 1,
        'One socket must own one in-flight Steam authentication request regardless of request hash.');

    resolveAuthentication('76561198000000000');
    assert.equal(await first, true);
    assert.equal(await second, true);
    assert.equal(socket.authenticatedUserId, '76561198000000000');
});