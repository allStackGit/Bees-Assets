'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server');

function mysqlStub() {
    return {
        createPool() {
            return { on() {} };
        },
    };
}

function makeServer() {
    return createServer({
        start: false,
        test: true,
        mysqlModule: mysqlStub(),
        websocketModule: { server: function FakeWebSocketServer() {} },
    }).server;
}

test('missing reconstructible cache files do not prevent startup', () => {
    const server = makeServer();
    server.cacheFolder = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-cache-missing-'));

    assert.doesNotThrow(() => server.loadCacheMaps());
    assert.equal(server.cachedMatchups.size, 0);
    assert.equal(server.cachedShootingMatchups.size, 0);
    assert.equal(server.cachedTargetingMatchups.size, 0);
});

test('malformed reconstructible cache file is discarded without aborting load', () => {
    const server = makeServer();
    server.cacheFolder = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-cache-bad-'));
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedMatchups.json'), '{not json');
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedShootingMatchups.json'), '');
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedTargetingMatchups.json'), '');

    assert.doesNotThrow(() => server.loadCacheMaps());
    assert.equal(server.cachedMatchups.size, 0);
});

test('legacy append-only cache entries still load', () => {
    const server = makeServer();
    server.cacheFolder = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-cache-valid-'));
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedMatchups.json'),
        '["A||B","123"],\n["C||D","456"],\n');
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedShootingMatchups.json'), '');
    fs.writeFileSync(path.join(server.cacheFolder, 'cachedTargetingMatchups.json'), '');

    server.loadCacheMaps();

    assert.equal(server.cachedMatchups.get('A||B'), '123');
    assert.equal(server.cachedMatchups.get('C||D'), '456');
});
