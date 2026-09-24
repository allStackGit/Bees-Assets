'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime } = require('../server');

function mysqlStub() {
    return { createPool() { return { on() {} }; } };
}

test('loaded runtime aggregates every strategy before minimum-use fallback', () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });

    const methods = Object.getOwnPropertyNames(runtime.Game.prototype)
        .filter(name => typeof runtime.Game.prototype[name] === 'function')
        .map(name => runtime.Game.prototype[name].toString())
        .join('\n');

    assert.doesNotMatch(methods, /availableStrats\.length\s*&&\s*!fellBackToBase/,
        'Targeting/strategic aggregation must not stop at the first underused strategy.');
    assert.doesNotMatch(methods, /availableShootingStrats\.length\s*&&\s*!fellBackToBase/,
        'Shooting aggregation must not stop at the first underused strategy.');
});
