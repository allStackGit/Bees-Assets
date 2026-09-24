'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { pruneExpiredEntries } = require('../serverContracts');

test('cache cleanup accepts a Map created inside the legacy VM realm', () => {
    const cache = vm.runInNewContext(`new Map([
        ['expired', { age: 10 }],
        ['fresh', { age: 95 }]
    ])`);

    assert.equal(cache instanceof Map, false,
        'The regression requires a genuine Map from a different JavaScript realm.');
    assert.equal(Object.prototype.toString.call(cache), '[object Map]');

    assert.equal(pruneExpiredEntries(cache, 100, 20), 1);
    assert.equal(cache.has('expired'), false);
    assert.equal(cache.has('fresh'), true);
});
