'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const {
    DEVELOPMENT_DATABASE,
    parseLauncherOptions,
    trainingControlProbeConfig,
} = require('../start-server');

test('server launcher defaults to the bees development database', () => {
    assert.equal(DEVELOPMENT_DATABASE.name, 'bees');
});

test('server launcher preserves legacy server arguments', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146']), {
        background: false,
        supervisor: false,
        logFile: null,
        serverArgs: ['test', '7146'],
    });
});

test('server launcher supports detached background mode and default log file', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146', '--background', '--log']), {
        background: true,
        supervisor: false,
        logFile: path.join('logs', 'bees-server.log'),
        serverArgs: ['test', '7146'],
    });
});

test('server launcher accepts a custom log path without forwarding launcher flags', () => {
    assert.deepEqual(parseLauncherOptions(['--background', '--log=logs/development.log', '7146']), {
        background: true,
        supervisor: false,
        logFile: 'logs/development.log',
        serverArgs: ['7146'],
    });
});


test('server launcher consumes the private supervisor flag', () => {
    assert.deepEqual(parseLauncherOptions(['--supervisor', 'test', '7146']), {
        background: false,
        supervisor: true,
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
