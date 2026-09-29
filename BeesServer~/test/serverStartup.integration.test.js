'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer, shouldStartTrainingControl } = require('../server');

function createRuntime() {
    const observed = {
        poolConfigs: [],
        listenPorts: [],
        websocketOptions: [],
        websocketHandlers: {},
    };
    const mysqlModule = {
        createPool(config) {
            observed.poolConfigs.push(config);
            return { on() {} };
        },
    };
    const httpModule = {
        createServer() {
            return {
                listen(port) {
                    observed.listenPorts.push(port);
                    return this;
                },
            };
        },
    };
    function FakeWebSocketServer(options) {
        observed.websocketOptions.push(options);
        this.on = (name, handler) => { observed.websocketHandlers[name] = handler; };
    }
    return {
        observed,
        options: {
            test: true,
            start: true,
            mysqlModule,
            httpModule,
            websocketModule: { server: FakeWebSocketServer },
            disableBackgroundJobs: true,
            dbName: 'bees_test',
            requireTestDb: true,
        },
    };
}

function withoutBackgroundTimers(work) {
    const originalTimeout = global.setTimeout;
    const originalInterval = global.setInterval;
    global.setTimeout = () => 0;
    global.setInterval = () => 0;
    try {
        return work();
    } finally {
        global.setTimeout = originalTimeout;
        global.setInterval = originalInterval;
    }
}

test('test mode keeps training control off unless the unified operator explicitly enables it', () => {
    assert.equal(shouldStartTrainingControl({ test: true }, {}), false);
    assert.equal(
        shouldStartTrainingControl(
            { test: true },
            { BEES_TEST_TRAINING_CONTROL_ENABLED: '1' },
        ),
        true,
    );
    assert.equal(shouldStartTrainingControl({ test: false }, {}), true);
    assert.equal(shouldStartTrainingControl({ test: false, trainingControl: false }, {}), false);
});

test('fixed test-mode startup wires isolated Database, HTTP and WebSocket', () => {
    const { observed, options } = createRuntime();
    const { server } = withoutBackgroundTimers(() => createServer(options));

    assert.equal(server.port, 7146);
    assert.equal(server.db.config.connection.database, 'bees_test');
    assert.equal(observed.poolConfigs.length, 1);
    assert.equal(observed.poolConfigs[0].database, 'bees_test');
    assert.deepEqual(observed.listenPorts, [7146]);
    assert.equal(observed.websocketOptions.length, 1);
    assert.equal(observed.websocketOptions[0].autoAcceptConnections, false);
    assert.equal(observed.websocketOptions[0].maxReceivedMessageSize, 1073741824);
    assert.equal(typeof observed.websocketHandlers.request, 'function');
});

test('test mode overrides an attempted production database selection with bees_test', () => {
    const { observed, options } = createRuntime();
    options.start = false;
    options.dbName = 'ram';

    const { server } = createServer(options);

    assert.equal(server.db.config.connection.database, 'bees_test',
        'Test mode must never honor a requested production database name.');
    server.db.handleDisconnect();
    assert.equal(observed.poolConfigs.length, 1);
    assert.equal(observed.poolConfigs[0].database, 'bees_test');
});

test('fixed WebSocket handler accepts requests during consolidation', () => {
    const { options } = createRuntime();
    options.start = false;
    const { server } = createServer(options);
    server.consolidationQueue.push({ reason: 'qualification' });
    let rejected = 0;
    let accepted = 0;

    server.handleWSRequest({
        origin: 'test://client',
        reject() { rejected++; },
        accept() { accepted++; return { on() {} }; },
    });

    assert.equal(rejected, 0);
    assert.equal(accepted, 1);
    assert.equal(server.connections.size, 1);
});

test('fixed WebSocket handler accepts and patches normal connections', () => {
    const { options } = createRuntime();
    options.start = false;
    const { server } = createServer(options);
    const handlers = {};
    let protocol;
    let origin;

    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept(acceptedProtocol, acceptedOrigin) {
            protocol = acceptedProtocol;
            origin = acceptedOrigin;
            return {
                on(name, handler) { handlers[name] = handler; },
                sendUTF() {},
            };
        },
    });

    assert.equal(protocol, 'game');
    assert.equal(origin, 'test://client');
    assert.equal(server.connections.size, 1);
    assert.equal(typeof handlers.message, 'function');
    assert.equal(typeof handlers.close, 'function');
});
