'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('../server');

function mysqlStub() {
    return {
        createPool() {
            return {
                on() {},
                getConnection(callback) {
                    callback(null, {
                        query(sql, values, done) { done(null, []); },
                        release() {},
                    });
                },
            };
        },
    };
}

function request(hash) {
    const responses = [];
    return {
        params: {
            Type: 'reconnect-level', Hash: hash, UserId: '1', LevelId: -1, GameId: 999999,
        },
        timings: { startTime: 0 },
        responses,
        respond(response) { responses.push(response); },
    };
}

test('sibling reconnects reuse a replacement Game when old Game already expired', async () => {
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return { on() {}, sendUTF() {} }; },
    });
    const connection = [...server.connections.values()][0];

    await connection.handleMessage(request(15001));
    const replacement = connection.game;
    replacement.pendingInserts.set(42, {
        matchup_id: 'm', strategy_id: 1, table: 2, time: performance.now(),
    });

    await connection.handleMessage(request(15002));

    assert.equal(connection.game, replacement);
    assert.equal(connection.game.pendingInserts.has(42), true,
        'A sibling reconnect must not overwrite the replacement Game and its pending outcomes.');
    assert.equal(server.games.get(connection.id), replacement);
});
