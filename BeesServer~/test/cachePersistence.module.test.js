'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { cacheChunks, writeCacheSafely } = require('../cachePersistence');

test('cache chunk producer is lazy and bounded to the requested slice size', () => {
    const chunks = cacheChunks([['a', 1], ['b', 2], ['c', 3]], 1);
    assert.equal(Array.isArray(chunks), false,
        'Cache serialization must not allocate an array containing every serialized chunk.');
    assert.deepEqual([...chunks], [
        '["a",1],',
        '["b",2],',
        '["c",3],',
    ]);
});

test('cache writer uses all-data file-handle writes for each lazy chunk', async () => {
    const written = [];
    let closed = false;
    const fsModule = {
        promises: {
            async open() {
                return {
                    async writeFile(contents, encoding) {
                        written.push([contents, encoding]);
                    },
                    async close() { closed = true; },
                };
            },
        },
    };
    const server = {
        cacheFolder: '.', cacheSliceSize: 1, useFullDiskWrite: false,
        writesCount: 0, totalWriteTime: 0,
        cachedMatchupsRecent: [['a', 1], ['b', 2]],
        common: { handleError() {} },
    };

    assert.equal(await writeCacheSafely(server, 'cachedMatchups', fsModule), true);
    assert.deepEqual(written, [
        ['["a",1],', 'utf8'],
        ['["b",2],', 'utf8'],
    ]);
    assert.equal(closed, true);
    assert.deepEqual(server.cachedMatchupsRecent, []);
});

test('cache write failure is contained and restores the unwritten delta', async () => {
    const failure = new Error('disk full');
    const fsModule = {
        promises: {
            async open() { throw failure; },
        },
    };
    const errors = [];
    const server = {
        cacheFolder: '.',
        cacheSliceSize: 1000,
        useFullDiskWrite: false,
        writesCount: 0,
        totalWriteTime: 0,
        cachedMatchupsRecent: [['a', '1'], ['b', '2']],
        common: { handleError(error, source) { errors.push({ error, source }); } },
    };

    const result = await writeCacheSafely(server, 'cachedMatchups', fsModule);

    assert.equal(result, false);
    assert.deepEqual(server.cachedMatchupsRecent, [['a', '1'], ['b', '2']]);
    assert.equal(errors.length, 1);
    assert.equal(errors[0].error, failure);
    assert.equal(errors[0].source, 'writeCacheToDisk.cachedMatchups');
});

test('cache writer preserves entries appended while a failed write is pending', async () => {
    let rejectOpen;
    const openGate = new Promise((resolve, reject) => { rejectOpen = reject; });
    const fsModule = { promises: { open: () => openGate } };
    const server = {
        cacheFolder: '.', cacheSliceSize: 1000, useFullDiskWrite: false,
        writesCount: 0, totalWriteTime: 0,
        cachedMatchupsRecent: [['old', '1']],
        common: { handleError() {} },
    };

    const writing = writeCacheSafely(server, 'cachedMatchups', fsModule);
    server.cachedMatchupsRecent.push(['new', '2']);
    rejectOpen(new Error('write failed'));

    assert.equal(await writing, false);
    assert.deepEqual(server.cachedMatchupsRecent, [['old', '1'], ['new', '2']]);
});
