'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const {
    parseOptions,
    requireReleaseIdentity,
} = require('../trainingControlCli');

test('release activation requires run identity and compatibility hash', () => {
    const { command, values } = parseOptions([
        'activate-build',
        '--build-id', 'release-1',
        '--run-id', 'run-1',
        '--compatibility-key', 'A'.repeat(64),
        '--incompatible',
    ]);
    assert.equal(command, 'activate-build');
    assert.deepEqual(requireReleaseIdentity(values, command), {
        build_id: 'release-1',
        run_id: 'run-1',
        compatibility_key: 'a'.repeat(64),
        incompatible: true,
    });
});

test('release activation rejects a build id without a run identity', () => {
    const { command, values } = parseOptions([
        'activate-build',
        '--build-id', 'release-1',
    ]);
    assert.throws(
        () => requireReleaseIdentity(values, command),
        /requires --run-id/,
    );
});

test('start accepts ordinary no-build resume without release identity', () => {
    const { command, values } = parseOptions([
        'start',
        '--env-arg', '--rl-map-size',
        '--env-arg', '64',
    ]);
    assert.equal(command, 'start');
    assert.deepEqual(values.envArgs, ['--rl-map-size', '64']);
    assert.equal(values.build_id, undefined);
});
