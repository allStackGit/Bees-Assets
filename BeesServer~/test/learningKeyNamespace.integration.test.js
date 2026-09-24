'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime } = require('../server');

function mysqlStub() {
    return { createPool() { return { on() {} }; } };
}

test('corrected targeting and shooting policies use clean v2 key namespaces', () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });

    const targeting = runtime.Game.prototype.getMatchupStrategy.toString();
    const allMethods = Object.getOwnPropertyNames(runtime.Game.prototype)
        .filter(name => typeof runtime.Game.prototype[name] === 'function')
        .map(name => runtime.Game.prototype[name].toString())
        .join('\n');

    assert.match(targeting, /target-v2:/,
        'Target-selection learning must not reuse legacy pre-attribution-fix hashes.');
    assert.match(allMethods, /shoot-v2:/,
        'Shooting learning must not reuse legacy pre-attribution-fix hashes.');
});
