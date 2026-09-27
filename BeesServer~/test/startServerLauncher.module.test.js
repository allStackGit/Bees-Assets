'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const {
    DEVELOPMENT_DATABASE,
    parseLauncherOptions,
    managedChildOwnerToken,
    trainingControlProbeConfig,
    supervisorRestartDelayMs,
    runSupervisor,
} = require('../start-server');

test('server launcher defaults to the bees development database', () => {
    assert.equal(DEVELOPMENT_DATABASE.name, 'bees');
});

test('server launcher preserves legacy server arguments', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146']), {
        background: false,
        supervisor: false,
        managedOwnerToken: '',
        logFile: null,
        serverArgs: ['test', '7146'],
    });
});

test('server launcher supports detached background mode and default log file', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146', '--background', '--log']), {
        background: true,
        supervisor: false,
        managedOwnerToken: '',
        logFile: path.join('logs', 'bees-server.log'),
        serverArgs: ['test', '7146'],
    });
});

test('server launcher accepts a custom log path without forwarding launcher flags', () => {
    assert.deepEqual(parseLauncherOptions(['--background', '--log=logs/development.log', '7146']), {
        background: true,
        supervisor: false,
        managedOwnerToken: '',
        logFile: 'logs/development.log',
        serverArgs: ['7146'],
    });
});


test('server launcher consumes the private supervisor flag', () => {
    assert.deepEqual(parseLauncherOptions(['--supervisor', 'test', '7146']), {
        background: false,
        supervisor: true,
        managedOwnerToken: '',
        logFile: null,
        serverArgs: ['test', '7146'],
    });
});

test('training control health probe normalizes wildcard listener host', () => {
    assert.deepEqual(trainingControlProbeConfig({
        BEES_TRAINING_CONTROL_ENABLED: '1',
        BEES_TRAINING_CONTROL_ADMIN_TOKEN: 'secret',
        BEES_TRAINING_CONTROL_HOST: '0.0.0.0',
        BEES_TRAINING_CONTROL_PORT: '7150',
    }), {
        host: '127.0.0.1',
        port: 7150,
        token: 'secret',
    });
});

test('training control health probe is disabled without complete managed control settings', () => {
    assert.equal(trainingControlProbeConfig({
        BEES_TRAINING_CONTROL_ENABLED: '1',
        BEES_TRAINING_CONTROL_PORT: '7150',
    }), null);
});


test('server launcher keeps managed owner token private from legacy server args', () => {
    assert.deepEqual(parseLauncherOptions([
        '--background',
        '--managed-owner-token', 'owner-secret',
        'test',
        '7146',
    ]), {
        background: true,
        supervisor: false,
        managedOwnerToken: 'owner-secret',
        logFile: null,
        serverArgs: ['test', '7146'],
    });
});


test('managed child owner token is deterministic and does not contain parent token', () => {
    const parent = 'owner-secret';
    const child = managedChildOwnerToken(parent);
    assert.equal(child.length, 64);
    assert.equal(child, managedChildOwnerToken(parent));
    assert.equal(child.includes(parent), false);
});


test('server supervisor retries when a replacement process fails to spawn', () => {
    const children = [];
    const timers = [];
    const fakeSpawn = () => {
        const child = new EventEmitter();
        child.pid = 1000 + children.length;
        child.exitCode = null;
        child.signalCode = null;
        child.kill = () => {};
        children.push(child);
        return child;
    };
    const fakeSetTimeout = (callback, delay) => {
        const timer = { callback, delay, unref() {} };
        timers.push(timer);
        return timer;
    };

    const supervisor = runSupervisor(
        { serverArgs: ['test', '7146'], managedOwnerToken: '' },
        {
            spawn: fakeSpawn,
            env: {},
            setTimeout: fakeSetTimeout,
            clearTimeout() {},
            registerSignals: false,
        },
    );

    assert.equal(children.length, 1);
    children[0].emit('error', new Error('simulated spawn failure'));
    assert.equal(timers.length, 1);
    assert.equal(timers[0].delay, 1000);

    timers.shift().callback();
    assert.equal(children.length, 2);
    children[1].emit('error', new Error('second simulated spawn failure'));
    assert.equal(timers.length, 1);
    assert.equal(timers[0].delay, 2000);

    supervisor.stop('SIGTERM');
});

test('server supervisor restart backoff is bounded', () => {
    assert.equal(supervisorRestartDelayMs(0), 1000);
    assert.equal(supervisorRestartDelayMs(1), 2000);
    assert.equal(supervisorRestartDelayMs(5), 30000);
    assert.equal(supervisorRestartDelayMs(100), 30000);
});
