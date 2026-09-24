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
                        query(sql, values, done) {
                            if (/SELECT filename, contents FROM stored_user_data/.test(sql)) {
                                done(null, []);
                                return;
                            }
                            done(null, []);
                        },
                        release() {},
                    });
                },
            };
        },
    };
}

function makeConnection() {
    return {
        on() {},
        sendUTF() {},
    };
}

function makeRequest(params) {
    const responses = [];
    return {
        params,
        timings: { startTime: 0 },
        responses,
        respond(response) { responses.push(response); },
    };
}

test('missing user data returns one terminal null payload so the client can create defaults', async () => {
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: createMysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    });
    server.db.handleDisconnect();

    const connection = makeConnection();
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return connection; },
    });
    const socketConnection = [...server.connections.values()][0];
    const request = makeRequest({
        Type: 'get-user-data',
        Hash: 13002,
        UserId: '76561198000000000',
        DataFile: 'challenge_fleet_data',
        Nonce: 8,
    });
    server.pendingRequests.set(13002, 1);

    await socketConnection.handleMessage(request);

    assert.equal(request.responses.length, 1,
        'A missing row must produce a response rather than leaving startup waiting forever.');
    assert.equal(request.responses[0].Type, 'get-user-data');
    assert.equal(request.responses[0].Hash, 13002);
    assert.equal(request.responses[0].Filename, null);
    assert.equal(request.responses[0].Contents, null);
    assert.equal(server.pendingRequests.has(13002), false,
        'The missing-row request must release pending ownership after responding.');
});
