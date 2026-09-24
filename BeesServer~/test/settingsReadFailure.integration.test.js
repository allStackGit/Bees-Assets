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
                            if (/SELECT userId, contents FROM settings/.test(sql)) {
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

test('get-settings database failure is not reported as missing settings', async () => {
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
        Type: 'get-settings',
        Hash: 13002,
        UserId: 1000001,
        DataFile: 'configuration',
        Version: 5,
        Nonce: 8,
    });
    server.pendingRequests.set(13002, 1);

    await assert.rejects(socketConnection.handleMessage(request), readError);
    assert.equal(request.responses.length, 0,
        'A settings read failure must not emit the legacy null/missing-settings response.');
    assert.equal(server.pendingRequests.has(13002), false,
        'The failed request hash must be released so the client can retry.');
});
