'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createServer, patchGame } = require('../server');

function createMysqlStub() {
    const observed = { pools: [], queries: [] };
    const mysql = {
        createPool() {
            const handlers = {};
            const pool = {
                handlers,
                on(name, handler) { handlers[name] = handler; },
                getConnection(callback) {
                    const connection = {
                        released: false,
                        query(sql, values, done) {
                            observed.queries.push({ connection, sql, values });
                            done(null, { affectedRows: 1 });
                        },
                        release() { this.released = true; },
                    };
                    callback(null, connection);
                },
            };
            observed.pools.push(pool);
            return pool;
        },
    };
    return { mysql, observed };
}

function createRuntimeOptions(mysql) {
    return {
        start: false,
        test: true,
        mysqlModule: mysql,
        websocketModule: { server: function FakeWebSocketServer() {} },
    };
}

function makeConnection() {
    const handlers = {};
    const responses = [];
    return {
        handlers,
        responses,
        on(name, handler) { handlers[name] = handler; },
        sendUTF(payload) { responses.push(JSON.parse(payload)); },
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

test('fixed runtime uses extracted Database and recognizes mysql2 inactivity errors', () => {
    const { mysql, observed } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));

    server.db.handleDisconnect();
    assert.equal(observed.pools.length, 1);
    observed.pools[0].handlers.error({
        code: 'ER_CLIENT_INTERACTION_TIMEOUT',
        errno: 4031,
    });
    assert.equal(observed.pools.length, 2,
        'mysql2 inactivity errors must rebuild the pool.');
});

test('unsupported request completes and releases its pending hash', async () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    const connection = makeConnection();
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return connection; },
    });
    const patchedConnection = [...server.connections.values()][0];
    const request = makeRequest({ Type: 'unsupported-test-type', Hash: 12001 });
    server.pendingRequests.set(12001, 1);

    await patchedConnection.handleMessage(request);

    assert.equal(server.pendingRequests.has(12001), false);
    assert.equal(request.responses.length, 1);
});

test('discarded queued work releases its pending hash after disconnect', () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    const request = {
        connectionId: 999,
        params: { Hash: 12002 },
        timings: { startTime: 0 },
    };
    server.pendingRequests.set(12002, 1);
    server.queue.push(request);

    const originalSetTimeout = global.setTimeout;
    global.setTimeout = () => 0;
    try {
        server.runQueue();
    } finally {
        global.setTimeout = originalSetTimeout;
    }

    assert.equal(server.pendingRequests.has(12002), false);
    assert.equal(server.queue.length, 0);
});

test('duplicate concurrent request hashes are queued only once', async () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    const connection = makeConnection();
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return connection; },
    });

    const payload = { utf8Data: JSON.stringify({ Type: 'get-user-data', Hash: 12003, UserId: 1000001, DataFile: 'x' }) };
    await connection.handlers.message(payload);
    await connection.handlers.message(payload);

    assert.equal(server.pendingRequests.size, 1);
    assert.equal(server.queue.length, 1,
        'A duplicate hash must not enqueue duplicate concurrent work.');
});

test('reconnect-level reactivates the retained game and close marks it inactive', async () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    const connection = makeConnection();
    server.handleWSRequest({
        origin: 'test://client',
        reject() { throw new Error('unexpected rejection'); },
        accept() { return connection; },
    });
    const socketConnection = [...server.connections.values()][0];
    const existingGame = { id: 77, isActive: false, time: -1 };
    server.games.set(77, existingGame);
    const request = makeRequest({
        Type: 'reconnect-level', Hash: 12004, UserId: 1000001, GameId: 77, LevelId: 2,
    });
    server.pendingRequests.set(12004, 1);

    await socketConnection.handleMessage(request);
    assert.equal(socketConnection.game, existingGame);
    assert.equal(existingGame.isActive, true);
    assert.equal(request.responses[0].GameId, 77);
    assert.equal(server.pendingRequests.has(12004), false);

    connection.handlers.close(1000, 'qualification close');
    assert.equal(existingGame.isActive, false);
    assert.equal(typeof existingGame.time, 'number');
    assert.equal(server.connections.size, 0);
});

test('inactive retained games expire after two hours but active games do not', () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    server.games.set(1, { isActive: false, time: -8000000 });
    server.games.set(2, { isActive: false, time: Number.MAX_SAFE_INTEGER });
    server.games.set(3, { isActive: true, time: -8000000 });

    server.removeOldGames();

    assert.equal(server.games.has(1), false);
    assert.equal(server.games.has(2), true);
    assert.equal(server.games.has(3), true);
});

test('patched Game waits for matched outcome persistence before storeState resolves', async () => {
    let releaseWrite;
    const writeGate = new Promise(resolve => { releaseWrite = resolve; });
    const server = {
        totalInsertTime: 0,
        updateMatchingTime: 0,
        updateMatchingCount: 0,
    };
    const game = patchGame({
        db: {
            async transaction(work) {
                return work(async () => {
                    await writeGate;
                    return { affectedRows: 1 };
                });
            },
            async query() { return []; },
        },
        server,
        pendingUpdates: [],
        pendingInserts: new Map([[123, {
            matchup_id: 55,
            strategy_id: 3,
            table: 2,
            time: performance.now(),
        }]]),
    });
    game.storeState = async (commands, shootingCommands, targetingCommands) => {
        commands.forEach(command => game.pendingUpdates.push({
            table: 'strategic_commands', tsv: command.Tsv, id: Number(command.OutcomeId),
        }));
        shootingCommands.forEach(command => game.pendingUpdates.push({
            table: 'shooting_outcomes', tsv: command.Tsv, id: Number(command.OutcomeId),
        }));
        targetingCommands.forEach(command => game.pendingUpdates.push({
            table: 'targeting_outcomes', tsv: command.Tsv, id: Number(command.OutcomeId),
        }));
        await game.matchUpdatesWithInsertsAndCommit();
        return true;
    };

    let completed = false;
    const storing = game.storeState([{ OutcomeId: 123, Tsv: 8 }], [], [])
        .then(result => { completed = true; return result; });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(completed, false, 'storeState must not resolve before the INSERT finishes.');

    releaseWrite();
    assert.equal(await storing, true);
    assert.equal(completed, true);
});

test('patched Game propagates matched outcome write failures', async () => {
    const failure = new Error('write failed');
    const game = patchGame({
        db: {
            async transaction(work) {
                return work(async () => { throw failure; });
            },
            async query() { return []; },
        },
        server: { totalInsertTime: 0, updateMatchingTime: 0, updateMatchingCount: 0 },
        pendingUpdates: [{ id: 124, tsv: 9 }],
        pendingInserts: new Map([[124, {
            matchup_id: 56,
            strategy_id: 4,
            table: 2,
            time: performance.now(),
        }]]),
    });

    await assert.rejects(game.matchUpdatesWithInsertsAndCommit(), failure);
});

test('patched strategy history reads propagate database failures', async () => {
    const failure = new Error('select failed');
    const game = patchGame({
        db: { async query() { throw failure; } },
        server: {}, pendingUpdates: [], pendingInserts: new Map(),
    });

    await assert.rejects(
        game.getOutcomesFromId(5, 'strategic_commands'), failure);
});

test('consolidation delegates mutations to one Database transaction boundary', async () => {
    const { mysql } = createMysqlStub();
    const { server } = createServer(createRuntimeOptions(mysql));
    let transactionCalls = 0;
    const statements = [];
    server.db.transaction = async work => {
        transactionCalls++;
        return work(async (sql, values) => {
            statements.push({ sql, values });
            return { affectedRows: 2 };
        });
    };
    const game = patchGame({
        config: { maxOutcomesPerStratType: 1 },
        db: { async query() { return [
            { strategy_id: 3, strategic_outcome: 10, uses: 1 },
            { strategy_id: 3, strategic_outcome: 20, uses: 1 },
        ]; } },
        server,
        pendingUpdates: [], pendingInserts: new Map(),
        addOutcomes(records, strategyId) {
            const matches = records.filter(record => record.strategy_id === strategyId);
            return {
                tsv: matches.reduce((sum, record) => sum + record.strategic_outcome * record.uses, 0),
                uses: matches.reduce((sum, record) => sum + record.uses, 0),
            };
        },
    });
    server.consolidationQueue = [{ table: 2, matchup_id: 42, game }];

    await server.consolidateOutcomes();

    assert.equal(transactionCalls, 1);
    assert.match(statements[0].sql, /^DELETE FROM strategic_commands/);
    assert.match(statements[1].sql, /^INSERT INTO strategic_commands/);
});
