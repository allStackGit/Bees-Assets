'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const Database = require('../database');

function makePoolFactory() {
    const pools = [];
    const mysqlStub = {
        createPool(config) {
            const pool = {
                config,
                handlers: {},
                on(name, handler) { this.handlers[name] = handler; },
            };
            pools.push(pool);
            return pool;
        },
    };
    return { mysqlStub, pools };
}

test('test-mode Database always selects bees_test', () => {
    const database = new Database('host', 'user', 'pass', 'ram', {}, true);
    assert.equal(database.config.connection.database, 'bees_test');
});

test('non-test Database preserves the configured production database', () => {
    const database = new Database('host', 'user', 'pass', 'ram', {}, false);
    assert.equal(database.config.connection.database, 'ram');
});

test('Database pool preserves BIGINT values as strings', () => {
    const { mysqlStub, pools } = makePoolFactory();
    const database = new Database('host', 'user', 'pass', 'ram', mysqlStub, false);

    database.handleDisconnect();

    assert.equal(pools[0].config.supportBigNumbers, true);
    assert.equal(pools[0].config.bigNumberStrings, true);
});

test('Database normalizes returned userId fields to decimal strings', async () => {
    const database = new Database('host', 'user', 'pass', 'db', {});
    database.connection = {
        getConnection(callback) {
            callback(null, {
                query(sql, values, done) {
                    done(null, [
                        { userId: 123, contents: 'override' },
                        { userId: 0, contents: 'global' },
                    ]);
                },
                release() {},
            });
        },
    };

    assert.deepEqual(await database.query('SELECT userId, contents FROM settings', []), [
        { userId: '123', contents: 'override' },
        { userId: '0', contents: 'global' },
    ]);
});

test('extracted Database rebuilds its pool for mysql2 inactivity errors', () => {
    const { mysqlStub, pools } = makePoolFactory();
    const database = new Database('host', 'user', 'pass', 'db', mysqlStub);

    database.handleDisconnect();
    assert.equal(pools.length, 1);

    pools[0].handlers.error({
        code: 'ER_CLIENT_INTERACTION_TIMEOUT',
        errno: 4031,
    });

    assert.equal(pools.length, 2);
});

test('extracted Database query releases its borrowed connection on success and failure', async () => {
    const database = new Database('host', 'user', 'pass', 'db', {});
    let releases = 0;
    let shouldFail = false;
    database.connection = {
        getConnection(callback) {
            callback(null, {
                query(sql, values, done) {
                    if (shouldFail) return done(new Error('query failed'));
                    return done(null, [{ ok: true }]);
                },
                release() { releases++; },
            });
        },
    };

    assert.deepEqual(await database.query('SELECT 1', []), [{ ok: true }]);
    assert.equal(releases, 1);

    shouldFail = true;
    await assert.rejects(database.query('BROKEN', []), /query failed/);
    assert.equal(releases, 2);
});

test('extracted Database transaction keeps all statements on one borrowed connection', async () => {
    const database = new Database('host', 'user', 'pass', 'db', {});
    const statements = [];
    let borrows = 0;
    let releases = 0;
    database.connection = {
        getConnection(callback) {
            borrows++;
            const connectionId = borrows;
            callback(null, {
                query(sql, values, done) {
                    if (typeof values === 'function') {
                        done = values;
                    }
                    statements.push({ connectionId, sql });
                    done(null, { ok: true });
                },
                release() { releases++; },
            });
        },
    };

    const result = await database.transaction(async (query) => {
        await query('DELETE FROM x WHERE id = ?', [1]);
        await query('INSERT INTO x VALUES ?', [[[1]]]);
        return 17;
    });

    assert.equal(result, 17);
    assert.equal(borrows, 1);
    assert.equal(releases, 1);
    assert.deepEqual(statements.map(statement => statement.sql), [
        'START TRANSACTION',
        'DELETE FROM x WHERE id = ?',
        'INSERT INTO x VALUES ?',
        'COMMIT',
    ]);
    assert.equal(new Set(statements.map(statement => statement.connectionId)).size, 1);
});

test('extracted Database transaction rolls back on the same connection and releases once', async () => {
    const database = new Database('host', 'user', 'pass', 'db', {});
    const statements = [];
    let releases = 0;
    database.connection = {
        getConnection(callback) {
            callback(null, {
                query(sql, values, done) {
                    if (typeof values === 'function') {
                        done = values;
                    }
                    statements.push(sql);
                    done(null, {});
                },
                release() { releases++; },
            });
        },
    };

    await assert.rejects(database.transaction(async (query) => {
        await query('DELETE FROM x');
        throw new Error('mutation failed');
    }), /mutation failed/);

    assert.deepEqual(statements, [
        'START TRANSACTION',
        'DELETE FROM x',
        'ROLLBACK',
    ]);
    assert.equal(releases, 1);
});
