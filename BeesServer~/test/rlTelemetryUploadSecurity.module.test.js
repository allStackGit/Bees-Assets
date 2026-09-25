'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { EventEmitter } = require('node:events');
const { installRlTelemetryUploadSecurity } = require('../rlTelemetryUploadSecurity');
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
    installRlTelemetryUploadSecurity(runtime, options);
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

function telemetryRequest(overrides = {}) {
    const responses = [];
    return {
        request: {
            params: {
                Type: 'rl-telemetry-begin',
                Hash: 'telemetry-1',
                UserId: '76561198000000000',
                AuthTicket: 'abcdef',
                MatchId: 'match-1',
                ...overrides,
            },
            respond: response => responses.push(response),
        },
        responses,
    };
}

test('production telemetry upload is Steam-authenticated and bypasses legacy handler', async () => {
    const calls = [];
    const manager = {
        async handle(params, context) {
            calls.push({ params, context });
            return { Completed: false, UploadId: 'upload-1', NextOffset: 0 };
        },
    };
    const { runtime, legacyCalls } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlTelemetryUploadManager: manager,
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 51);
    const { request, responses } = telemetryRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].context, { userId: '76561198000000000', connectionId: '51' });
    assert.equal(legacyCalls(), 0);
    assert.equal(responses.length, 1);
    assert.equal(responses[0].Status, 200);
    assert.equal(responses[0].UploadId, 'upload-1');
});

test('test mode uses only its explicit request identity without Steam', async () => {
    const calls = [];
    const { runtime, legacyCalls } = runtimeFixture({
        rlTelemetryUploadManager: {
            async handle(params, context) {
                calls.push({ params, context });
                return { Completed: false, UploadId: 'test-upload', NextOffset: 0 };
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(true), 52);
    const { request, responses } = telemetryRequest({ AuthTicket: '' });

    assert.equal(await socket.handleMessage(request), true);
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].context, { userId: '76561198000000000', connectionId: '52' });
    assert.equal(legacyCalls(), 0);
    assert.equal(responses[0].Status, 200);
});

test('test mode still rejects telemetry without a bounded test identity', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        rlTelemetryUploadManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(true), 56);
    const { request, responses } = telemetryRequest({ UserId: '' });

    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-telemetry-begin',
        Hash: 'telemetry-1',
        Status: 401,
        ErrorCode: 'authentication-required',
    }]);
});

test('production never trusts the claimed user ID without Steam authentication', async () => {
    let managerCalls = 0;
    const { runtime, legacyCalls } = runtimeFixture({
        rlTelemetryUploadManager: {
            async handle() {
                managerCalls++;
                return {};
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 57);
    const { request, responses } = telemetryRequest();

    // Call the specialized layer directly before production security establishes authenticatedUserId.
    assert.equal(await socket.handleMessage(request), false);
    assert.equal(managerCalls, 0);
    assert.equal(legacyCalls(), 0);
    assert.deepEqual(responses, [{
        Type: 'rl-telemetry-begin',
        Hash: 'telemetry-1',
        Status: 401,
        ErrorCode: 'authentication-required',
    }]);
});

test('telemetry upload fails closed when no quarantine root is configured', async () => {
    const oldRoot = process.env.BEES_RL_TELEMETRY_UPLOAD_DIR;
    delete process.env.BEES_RL_TELEMETRY_UPLOAD_DIR;
    try {
        const { runtime, legacyCalls } = runtimeFixture({
            authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        });
        const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 53);
        const { request, responses } = telemetryRequest();

        assert.equal(await socket.handleMessage(request), true);
        assert.equal(legacyCalls(), 0);
        assert.deepEqual(responses, [{
            Type: 'rl-telemetry-begin',
            Hash: 'telemetry-1',
            Status: 503,
            ErrorCode: 'telemetry-upload-disabled',
        }]);
    } finally {
        if (oldRoot === undefined) delete process.env.BEES_RL_TELEMETRY_UPLOAD_DIR;
        else process.env.BEES_RL_TELEMETRY_UPLOAD_DIR = oldRoot;
    }
});

test('validation failures expose only bounded status and code', async () => {
    const { runtime, errors } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlTelemetryUploadManager: {
            async handle() {
                const error = new Error('/private/telemetry/path');
                error.statusCode = 409;
                error.code = 'payload-identity-mismatch';
                throw error;
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 54);
    const { request, responses } = telemetryRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.deepEqual(responses, [{
        Type: 'rl-telemetry-begin',
        Hash: 'telemetry-1',
        Status: 409,
        ErrorCode: 'payload-identity-mismatch',
    }]);
    assert.equal(errors.length, 0);
});

test('unexpected telemetry failures are logged without leaking internals', async () => {
    const { runtime, errors } = runtimeFixture({
        authenticateSteamTicket: async (_ticket, claimedUserId) => claimedUserId,
        rlTelemetryUploadManager: {
            async handle() {
                throw new Error('/private/telemetry/path');
            },
        },
    });
    const socket = new runtime.SocketConnection(new EventEmitter(), {}, serverFixture(false), 55);
    const { request, responses } = telemetryRequest();

    assert.equal(await socket.handleMessage(request), true);
    assert.deepEqual(responses, [{
        Type: 'rl-telemetry-begin',
        Hash: 'telemetry-1',
        Status: 500,
        ErrorCode: 'telemetry-upload-failed',
    }]);
    assert.equal(errors.length, 1);
    assert.equal(errors[0].label, 'RL telemetry upload');
});
