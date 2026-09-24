'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
    CHECKPOINT_FILE,
    REQUIRED_FILES,
    parseCheckpoint,
    storeCampaignCheckpoint,
} = require('../campaignCheckpoint');

function makeCheckpoint() {
    return JSON.stringify({
        user_progress: '{"CurrentHumanCampaignLevel":8}',
        saved_squads_data: '[{"Id":10}]',
        fleet_data: '[{"Id":11}]',
        campaign_saved_squads_data: '[{"Id":1}]',
        campaign_fleet_data: '[{"Id":2}]',
        challenge_saved_squads_data: '[{"Id":20}]',
        challenge_fleet_data: '[{"Id":21}]',
    });
}

test('profile checkpoint uses one database transaction for all profile documents', async () => {
    const queries = [];
    let transactionCalls = 0;
    const user = {
        determineUserId: () => '42',
        db: {
            transaction: async callback => {
                transactionCalls++;
                await callback(async (sql, values) => {
                    queries.push({ sql, values });
                    if (sql.startsWith('SELECT')) return [];
                    return { affectedRows: 1 };
                });
            },
        },
    };

    assert.equal(await storeCampaignCheckpoint(user, makeCheckpoint()), true);
    assert.equal(transactionCalls, 1);

    const selects = queries.filter(entry => entry.sql.startsWith('SELECT'));
    const inserts = queries.filter(entry => entry.sql.startsWith('INSERT'));
    const updates = queries.filter(entry => entry.sql.startsWith('UPDATE'));

    assert.equal(selects.length, 1,
        'Profile existence should be checked with one batched read, not one SELECT per document.');
    assert.deepEqual(selects[0].values[0], ['42']);
    assert.deepEqual(selects[0].values[1], REQUIRED_FILES);
    assert.equal(inserts.length, REQUIRED_FILES.length,
        'Every missing profile document must be created inside the transaction.');
    assert.equal(updates.length, 0);
});

test('profile checkpoint updates all existing profile documents after one batched existence read', async () => {
    const queries = [];
    const user = {
        determineUserId: () => '42',
        db: {
            transaction: async callback => {
                await callback(async (sql, values) => {
                    queries.push({ sql, values });
                    if (sql.startsWith('SELECT')) {
                        return REQUIRED_FILES.map(filename => ({ userId: '42', filename }));
                    }
                    return { affectedRows: 1 };
                });
            },
        },
    };

    assert.equal(await storeCampaignCheckpoint(user, makeCheckpoint()), true);
    assert.equal(queries.filter(entry => entry.sql.startsWith('SELECT')).length, 1);
    assert.equal(queries.filter(entry => entry.sql.startsWith('UPDATE')).length, REQUIRED_FILES.length);
    assert.equal(queries.filter(entry => entry.sql.startsWith('INSERT')).length, 0);
});

test('profile checkpoint rejects an incomplete payload before opening a transaction', async () => {
    let transactionCalls = 0;
    const user = {
        determineUserId: () => '42',
        db: {
            transaction: async () => { transactionCalls++; },
        },
    };

    await assert.rejects(
        () => storeCampaignCheckpoint(user, JSON.stringify({ user_progress: '{}' })),
        /saved_squads_data/);
    assert.equal(transactionCalls, 0);
});

test('campaign checkpoint reserved filename is stable', () => {
    assert.equal(CHECKPOINT_FILE, '__campaign_checkpoint__');
    assert.doesNotThrow(() => parseCheckpoint(makeCheckpoint()));
});
