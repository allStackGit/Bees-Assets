'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime } = require('../server');

function mysqlStub() {
    return {
        createPool() {
            return { on() {} };
        },
    };
}

test('loaded runtime does not restore unfiltered cached shooting strategies', () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });

    const methods = Object.getOwnPropertyNames(runtime.Game.prototype)
        .filter(name => typeof runtime.Game.prototype[name] === 'function')
        .map(name => runtime.Game.prototype[name].toString())
        .join('\n');

    assert.match(methods,
        /cachedShootingStrategy\.strats\.filter/,
        'The runtime must filter cached shooting strategies against unavailable target types.');
    assert.doesNotMatch(methods,
        /availableShootingStrats\s*=\s*cachedShootingStrategy\.strats\s*;/,
        'The filtered cached shooting list must not be overwritten by the raw cache.');
});
