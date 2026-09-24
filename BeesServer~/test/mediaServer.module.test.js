'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');
const { resolveMediaPath, parseRange } = require('../mediaServer');

test('resolveMediaPath keeps requests inside the configured media root', () => {
    const root = path.resolve('/tmp/bees-media');
    assert.equal(resolveMediaPath(root, '/v/movie.mp4'), path.join(root, 'movie.mp4'));
    assert.equal(resolveMediaPath(root, '/v/sub/clip.ogg'), path.join(root, 'sub', 'clip.ogg'));
    assert.equal(resolveMediaPath(root, '/v/../../etc/passwd'), null);
});

test('parseRange rejects malformed and out-of-bounds byte ranges', () => {
    assert.deepEqual(parseRange('bytes=0-9', 100), { start: 0, end: 9 });
    assert.deepEqual(parseRange('bytes=-10', 100), { start: 90, end: 99 });
    assert.deepEqual(parseRange('bytes=90-', 100), { start: 90, end: 99 });
    assert.equal(parseRange('items=0-9', 100), false);
    assert.equal(parseRange('bytes=100-110', 100), false);
    assert.equal(parseRange('bytes=20-10', 100), false);
    assert.equal(parseRange('bytes=0-', 0), false);
});
