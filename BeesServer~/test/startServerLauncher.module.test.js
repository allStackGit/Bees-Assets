'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { parseLauncherOptions } = require('../start-server');

test('server launcher preserves legacy server arguments', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146']), {
        background: false,
        logFile: null,
        serverArgs: ['test', '7146'],
    });
});

test('server launcher supports detached background mode and default log file', () => {
    assert.deepEqual(parseLauncherOptions(['test', '7146', '--background', '--log']), {
        background: true,
        logFile: path.join('logs', 'bees-server.log'),
        serverArgs: ['test', '7146'],
    });
});

test('server launcher accepts a custom log path without forwarding launcher flags', () => {
    assert.deepEqual(parseLauncherOptions(['--background', '--log=logs/development.log', '7146']), {
        background: true,
        logFile: 'logs/development.log',
        serverArgs: ['7146'],
    });
});
