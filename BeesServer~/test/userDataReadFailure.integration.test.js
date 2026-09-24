'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('../server');

function createMysqlStub(readError) {
    return {
        createPool() {
            return {
                on() {},
                getConnection(callback) {
                    callback(null, {
                        query(sql, values, done) {
                            if (/SELECT filename, contents FROM stored_user_data/.test(sql)) {
                                done(readError);
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

test('get-user-data database failure is not reported as missing data', async () => {
    const readError = new Error('database unavailable');
    const { server } = createServer({
        start: false,
        test: true,
        mysqlModule: createMysqlStub(readError),
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
        Hash: 13001,
        UserId: 1000001,
        DataFile: 'fleet_data',
        Nonce: 7,
    });
    server.pendingRequests.set(13001, 1);

    await assert.rejects(socketConnection.handleMessage(request), readError);
    assert.equal(request.responses.length, 0,
        'A read failure must not emit the legacy null/missing-data response.');
    assert.equal(server.pendingRequests.has(13001), false,
        'The failed request hash must be released so the client can retry.');
});
