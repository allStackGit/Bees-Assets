'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { EventEmitter } = require('node:events');
const { installRuntimeSecurity } = require('../security');

function runtimeFixture(options = {}) {
    let legacyCalls = 0;
    class LegacySocketConnection {
        constructor(connection, db, server, id) {
            this.connection = connection;
            this.db = db;
            this.server = server;
            this.id = id;
            this.handleMessage = async () => {
                legacyCalls++;
                return true;
            };
        }
    }
    const errors = [];
    const runtime = {
        SocketConnection: LegacySocketConnection,
        common: {
            timer: () => 1,
            handleError: (error, label) => errors.push({ error, label }),
        },
    };
    installRuntimeSecurity(runtime, options);
    return { runtime, errors, legacyCalls: () => legacyCalls };
}

function serverFixture(testMode = false) {
    return {
        test: testMode,
        isRunningConsolidation: false,
        games: new Map(),
        pendingRequests: new Map(),
        queue: [],
    };
}

function uploadRequest(overrides = {}) {
    const responses = [];
    return {
        request: {
            params: {
                Type: 'rl-demo-begin',
                Hash: 'upload-1',
                UserId: '76561198000000000',
                AuthTicket: 'abcdef',
                ...overrides,
            },
            respond: response => responses.push(response),
        },
        responses,
    };
}

test('production RL upload is handled only after Steam authentication and never reaches legacy handler', async () => {
    const managerCalls = [];
    const manager = {
        async handle(params, context) {
            managerCalls.push({ params, context });
            return { UploadId: 'upload-session', NextOffset: 0, ChunkBytes: 524288 };
        },
    };
    const { runtime, legacyCalls } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlDemonstrationUploadManager: manager,
    });
    const connection = new EventEmitter();
    const server = serverFixture(false);
    const socket = new runtime.SocketConnection(connection, {}, server, 17);
    const { request, responses } = uploadRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.equal(managerCalls.length, 1);
    assert.equal(managerCalls[0].context.userId, '76561198000000000');
    assert.equal(managerCalls[0].context.connectionId, '17');
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-demo-begin',
        Hash: 'upload-1',
        Status: 200,
        UploadId: 'upload-session',
        NextOffset: 0,
        ChunkBytes: 524288,
    }]);
});

test('claimed upload user cannot differ from authenticated Steam identity', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        authenticateSteamTicket: async () => '76561198000000001',
        rlDemonstrationUploadManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const connection = new EventEmitter();
    const server = serverFixture(false);
    const socket = new runtime.SocketConnection(connection, {}, server, 18);
    const { request, responses } = uploadRequest();

    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{ Type: 'rl-demo-begin', Hash: 'upload-1', Status: 403 }]);
});

test('test-mode authentication shortcut cannot mutate RL upload quarantine anonymously', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        rlDemonstrationUploadManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const connection = new EventEmitter();
    const server = serverFixture(true);
    const socket = new runtime.SocketConnection(connection, {}, server, 19);
    const { request, responses } = uploadRequest();

    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-demo-begin',
        Hash: 'upload-1',
        Status: 401,
        ErrorCode: 'authentication-required',
    }]);
});

test('configured upload manager errors preserve bounded status/code without exposing exception text', async () => {
    const { runtime, errors } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlDemonstrationUploadManager: {
            async handle() {
                const error = new Error('private server filesystem detail');
                error.statusCode = 429;
                error.code = 'upload-rate-limit';
                throw error;
            },
        },
    });
    const connection = new EventEmitter();
    const server = serverFixture(false);
    const socket = new runtime.SocketConnection(connection, {}, server, 20);
    const { request, responses } = uploadRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.deepEqual(responses, [{
        Type: 'rl-demo-begin',
        Hash: 'upload-1',
        Status: 429,
        ErrorCode: 'upload-rate-limit',
    }]);
    assert.equal(errors.length, 0, 'Expected client validation/rate-limit errors should not be logged as server failures.');
});

test('upload endpoint fails closed when no quarantine root is configured', async () => {
    const oldRoot = process.env.BEES_RL_DEMO_UPLOAD_DIR;
    delete process.env.BEES_RL_DEMO_UPLOAD_DIR;
    try {
        const { runtime } = runtimeFixture({
            authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        });
        const connection = new EventEmitter();
        const server = serverFixture(false);
        const socket = new runtime.SocketConnection(connection, {}, server, 21);
        const { request, responses } = uploadRequest();

        assert.equal(await socket.handleMessage(request), true);
        assert.deepEqual(responses, [{
            Type: 'rl-demo-begin',
            Hash: 'upload-1',
            Status: 503,
            ErrorCode: 'upload-disabled',
        }]);
    } finally {
        if (oldRoot === undefined) delete process.env.BEES_RL_DEMO_UPLOAD_DIR;
        else process.env.BEES_RL_DEMO_UPLOAD_DIR = oldRoot;
    }
});
