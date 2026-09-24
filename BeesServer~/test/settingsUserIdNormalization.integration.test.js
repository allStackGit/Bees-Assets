'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadLegacyRuntime } = require('../server');

test('legacy User.getSettings prefers numeric DB row for equivalent string user ID', async () => {
    const runtime = loadLegacyRuntime({
        mysqlModule: { createPool() { return { on() {} }; } },
        websocketModule: { server: function FakeWebSocketServer() {} },
    });

    const user = new runtime.User({
        async query() {
            return [
                { userId: 123456, contents: 'user-specific' },
                { userId: 0, contents: 'global' },
            ];
        },
    }, '123456');

    const settings = await user.getSettings('configuration', 5);
    assert.equal(settings.contents, 'user-specific');
});
