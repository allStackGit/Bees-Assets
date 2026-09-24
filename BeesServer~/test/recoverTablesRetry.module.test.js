'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { executeWithRetry, writeRecoveryBatch } = require('../recover-tables');

test('executeWithRetry retries the identical query instead of advancing past a row', async () => {
    const calls = [];
    const connection = {
        async execute(query) {
            calls.push(query);
            if (calls.length < 3) throw new Error('transient');
            return [[{ ID: 41 }], []];
        },
    };

    const result = await executeWithRetry(connection, 'SELECT ID FROM sample WHERE ID > 40', 3);
    assert.equal(calls.length, 3);
    assert.ok(calls.every(query => query === calls[0]));
    assert.equal(result[0][0].ID, 41);
});

test('executeWithRetry aborts after repeated failures rather than silently skipping data', async () => {
    const connection = { execute: async () => { throw new Error('still failing'); } };
    await assert.rejects(
        executeWithRetry(connection, 'SELECT ID FROM sample WHERE ID > 40', 2),
        /still failing/);
});

test('writeRecoveryBatch waits for the filesystem write and propagates failures', async () => {
    let releaseWrite;
    const gate = new Promise(resolve => { releaseWrite = resolve; });
    let completed = false;
    const handle = {
        async writeFile(contents, encoding) {
            assert.equal(contents, 'batch');
            assert.equal(encoding, 'utf8');
            await gate;
            completed = true;
        },
    };

    const writing = writeRecoveryBatch(handle, 'batch');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(completed, false);
    releaseWrite();
    await writing;
    assert.equal(completed, true);

    const failure = new Error('disk failure');
    await assert.rejects(
        writeRecoveryBatch({ writeFile: async () => { throw failure; } }, 'batch'),
        failure);
});
