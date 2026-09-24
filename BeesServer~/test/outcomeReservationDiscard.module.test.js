'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    StaleOutcomeReservationError,
    RESERVATION_PREFIX,
    patchOutcomeDurability,
} = require('../outcomeReservations');

test('explicit durable discards are consumed before a terminal stale sibling result', async () => {
    const events = [];
    const stale = new StaleOutcomeReservationError([99]);
    const game = {
        userId: '12345678901234567',
        db: {
            transaction: async work => work(async (sql, params) => {
                events.push({ type: 'query', sql, params });
                return { affectedRows: 1 };
            }),
        },
        server: {
            totalInsertTime: 0,
            updateMatchingTime: 0,
            updateMatchingCount: 0,
        },
        pendingInserts: new Map(),
        pendingUpdates: [],
        storeState: async () => {
            events.push({ type: 'store' });
            throw stale;
        },
    };

    patchOutcomeDurability(game, () => {});

    await assert.rejects(
        game.storeState(
            [],
            [{ OutcomeId: 17, DiscardReservation: true }],
            [{ OutcomeId: 99, Tsv: 5 }]),
        error => error === stale,
    );

    const deletionIndex = events.findIndex(event =>
        event.type === 'query' && event.sql.startsWith('DELETE FROM stored_user_data'));
    const storeIndex = events.findIndex(event => event.type === 'store');
    assert.notEqual(deletionIndex, -1);
    assert.notEqual(storeIndex, -1);
    assert.ok(deletionIndex < storeIndex,
        'Explicit discard metadata must be deleted before sibling outcome processing can return a terminal 409.');

    const deletion = events[deletionIndex];
    assert.equal(deletion.params[0], '12345678901234567');
    assert.deepEqual(deletion.params[1], [`${RESERVATION_PREFIX}17`]);
});
