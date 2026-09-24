'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { adoptQueuedConsolidations } = require('../gamePersistence');

test('adopts consolidation-map entries queued during an active pass', () => {
    const existing = { table: 2, matchup_id: 'existing' };
    const added = { table: 1, matchup_id: 'late' };
    const server = {
        consolidationQueue: [existing],
        consolidationMap: new Map([['late', added]]),
    };

    assert.equal(adoptQueuedConsolidations(server), 1);
    assert.deepEqual(server.consolidationQueue, [existing, added]);
    assert.equal(server.consolidationMap.size, 0);
});

test('does not duplicate work already present in the active queue', () => {
    const existing = { table: 2, matchup_id: 'same' };
    const server = {
        consolidationQueue: [existing],
        consolidationMap: new Map([['same', { table: 2, matchup_id: 'same' }]]),
    };

    assert.equal(adoptQueuedConsolidations(server), 0);
    assert.equal(server.consolidationQueue.length, 1);
    assert.equal(server.consolidationMap.size, 0);
});

test('table identity is part of consolidation deduplication', () => {
    const server = {
        consolidationQueue: [{ table: 2, matchup_id: 'same-hash' }],
        consolidationMap: new Map([['other', { table: 1, matchup_id: 'same-hash' }]]),
    };

    assert.equal(adoptQueuedConsolidations(server), 1);
    assert.equal(server.consolidationQueue.length, 2);
});
