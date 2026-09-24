'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { EventEmitter } = require('node:events');
const { installRlModelDistributionSecurity } = require('../rlModelDistributionSecurity');
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
    installRlModelDistributionSecurity(runtime, options);
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

function modelRequest(overrides = {}) {
    const responses = [];
    return {
        request: {
            params: {
                Type: 'rl-model-current',
                Hash: 'model-1',
                UserId: '76561198000000000',
                AuthTicket: 'abcdef',
                Platform: 'StandaloneWindows64',
                PolicyAbiVersion: 8,
                PolicySignature: 'policy',
                ...overrides,
            },
            respond: response => responses.push(response),
        },
        responses,
    };
}

test('production model distribution is Steam-authenticated and never reaches legacy handler', async () => {
    const calls = [];
    const manager = {
        async handle(params, context) {
            calls.push({ params, context });
            return {
                Platform: params.Platform,
                UpToDate: false,
                DeploymentId: 'deploy-' + 'a'.repeat(24),
                ModelId: 'bees-rl-v8-' + 'b'.repeat(24),
            };
        },
    };
    const { runtime, legacyCalls } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlModelDistributionManager: manager,
    });
    const connection = new EventEmitter();
    const socket = new runtime.SocketConnection(connection, {}, serverFixture(false), 41);
    const { request, responses } = modelRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].context.userId, '76561198000000000');
    assert.equal(calls[0].context.connectionId, '41');
    assert.equal(legacyCalls(), 0);
    assert.equal(responses.length, 1);
    assert.equal(responses[0].Status, 200);
    assert.equal(responses[0].DeploymentId, 'deploy-' + 'a'.repeat(24));
});

test('test mode can read validated model distribution with its explicit test identity', async () => {
    const calls = [];
    const { runtime, legacyCalls } = runtimeFixture({
        rlModelDistributionManager: {
            async handle(params, context) {
                calls.push({ params, context });
                return { Platform: params.Platform, UpToDate: true };
            },
        },
    });
    const connection = new EventEmitter();
    const socket = new runtime.SocketConnection(connection, {}, serverFixture(true), 42);
    const { request, responses } = modelRequest({ AuthTicket: '' });

    assert.equal(await socket.handleMessage(request), true);
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].context, { userId: '76561198000000000', connectionId: '42' });
    assert.equal(legacyCalls(), 0);
    assert.equal(responses[0].Status, 200);
});

test('test mode still rejects model distribution without a bounded test identity', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        rlModelDistributionManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(true), 46);
    const { request, responses } = modelRequest({ UserId: '' });

    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-model-current',
        Hash: 'model-1',
        Status: 401,
        ErrorCode: 'authentication-required',
    }]);
});

test('production never trusts claimed model-distribution identity without Steam authentication', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        rlModelDistributionManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 47);
    const { request, responses } = modelRequest();

    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-model-current',
        Hash: 'model-1',
        Status: 401,
        ErrorCode: 'authentication-required',
    }]);
});

test('model distribution endpoint fails closed when no distribution root is configured', async () => {
    const oldRoot = process.env.BEES_RL_MODEL_DISTRIBUTION_DIR;
    delete process.env.BEES_RL_MODEL_DISTRIBUTION_DIR;
    try {
        const { runtime, legacyCalls } = runtimeFixture({
            authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        });
        const connection = new EventEmitter();
        const socket = new runtime.SocketConnection(connection, {}, serverFixture(false), 43);
        const { request, responses } = modelRequest();

        assert.equal(await socket.handleMessage(request), true);
        assert.equal(legacyCalls(), 0);
        assert.deepEqual(responses, [{
            Type: 'rl-model-current',
            Hash: 'model-1',
            Status: 503,
            ErrorCode: 'model-distribution-disabled',
        }]);
    } finally {
        if (oldRoot === undefined) delete process.env.BEES_RL_MODEL_DISTRIBUTION_DIR;
        else process.env.BEES_RL_MODEL_DISTRIBUTION_DIR = oldRoot;
    }
});

test('distribution validation errors preserve bounded status/code without exposing internals', async () => {
    const { runtime, errors } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlModelDistributionManager: {
            async handle() {
                const error = new Error('/private/model/storage/path');
                error.statusCode = 409;
                error.code = 'incompatible-client-policy';
                throw error;
            },
        },
    });
    const connection = new EventEmitter();
    const socket = new runtime.SocketConnection(connection, {}, serverFixture(false), 44);
    const { request, responses } = modelRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.deepEqual(responses, [{
        Type: 'rl-model-current',
        Hash: 'model-1',
        Status: 409,
        ErrorCode: 'incompatible-client-policy',
    }]);
    assert.equal(errors.length, 0);
});

test('unexpected distribution failures are logged but not exposed to clients', async () => {
    const { runtime, errors } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlModelDistributionManager: {
            async handle() {
                throw new Error('/private/model/storage/path');
            },
        },
    });
    const connection = new EventEmitter();
    const socket = new runtime.SocketConnection(connection, {}, serverFixture(false), 45);
    const { request, responses } = modelRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.deepEqual(responses, [{
        Type: 'rl-model-current',
        Hash: 'model-1',
        Status: 500,
        ErrorCode: 'model-distribution-failed',
    }]);
    assert.equal(errors.length, 1);
    assert.equal(errors[0].label, 'RL model distribution');
});
