'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('../server');

function createMysqlStub() {
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

function makeConnection() {
    return { on() {}, sendUTF() {} };
}

function makeRequest(hash, levelId) {
    const responses = [];
    return {
        params: {
            Type: 'setup-level',
            Hash: hash,
            UserId: '76561198000000000',
            LevelId: levelId,
        },
        timings: { startTime: 0 },
        responses,
        respond(response) { responses.push(response); },
    };
}

test('multiple setup-level requests on one socket reuse one Game', async () => {
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: createMysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    const transport = makeConnection();
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return transport; },
    });
    const socketConnection = [...server.connections.values()][0];

    const first = makeRequest(14001, 1);
    await socketConnection.handleMessage(first);
    const sharedGame = socketConnection.game;
    sharedGame.pendingInserts.set(987654, {
        matchup_id: '55', strategy_id: 3, table: 2, time: performance.now(),
    });

    const second = makeRequest(14002, 2);
    await socketConnection.handleMessage(second);

    assert.equal(socketConnection.game, sharedGame,
        'A second Level on the same connection must not replace its Game.');
    assert.equal(socketConnection.game.pendingInserts.has(987654), true,
        'Pending outcome ownership from the first Level must survive later Level setup.');
    assert.equal(first.responses[0].GameId, second.responses[0].GameId,
        'All Levels on one socket should receive the shared Game ID.');
    assert.equal(server.games.size, 1);
});
