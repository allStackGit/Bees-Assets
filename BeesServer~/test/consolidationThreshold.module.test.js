'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { needsConsolidation, patchGame, loadConsolidationOutcomes } = require('../gamePersistence');

function rows(strategyCount, rowsPerStrategy) {
    const result = [];
    let id = 1;
    for (let strategy = 1; strategy <= strategyCount; strategy++) {
        for (let row = 0; row < rowsPerStrategy; row++) {
            result.push({
                ID: id++,
                strategy_id: strategy,
                strategic_outcome: 1,
                uses: 1,
            });
        }
    }
    return result;
}

function makeServer() {
    return {
        consolidationMap: new Map(),
        consolidationQueue: [],
        connections: new Map(),
        cachedStrategies: new Map(),
        cachedShootingStrategies: new Map(),
        cachedTargetingStrategies: new Map(),
        totalConsolidatedRows: 0,
        totalConsolidatedInsertRows: 0,
        consolidationTime: 0,
    };
}

test('many consolidated strategies do not trigger maintenance by total row count', () => {
    assert.equal(needsConsolidation(rows(40, 1), 5), false,
        '40 strategies with one row each are already compact and must not be reconsolidated.');
    assert.equal(needsConsolidation(rows(15, 2), 5), false,
        'Lossless quotient/remainder aggregates may legitimately use two rows per strategy.');
});

test('one overgrown strategy triggers maintenance even when others are compact', () => {
    const outcomes = rows(10, 1);
    for (let i = 0; i < 5; i++) {
        outcomes.push({ ID: 100 + i, strategy_id: 3, strategic_outcome: i, uses: 1 });
    }
    assert.equal(needsConsolidation(outcomes, 5), true,
        'Strategy 3 has six rows and should trigger consolidation.');
});

test('patched Game queues consolidation by canonical table plus matchup id', () => {
    const server = makeServer();
    const game = patchGame({
        server,
        db: { async query() { return []; } },
        config: { maxOutcomesPerStratType: 5 },
        pendingUpdates: [],
        pendingInserts: new Map(),
    });
    const overgrown = rows(1, 6);

    game.addToConsolidationQueue(0, '123', overgrown);
    game.addToConsolidationQueue(1, '123', overgrown);
    game.addToConsolidationQueue(0, '123', overgrown);

    assert.equal(server.consolidationMap.size, 2,
        'Targeting and shooting may share a numeric hash and must remain separate maintenance jobs.');
    assert.equal(server.consolidationMap.has('targeting_outcomes:123'), true);
    assert.equal(server.consolidationMap.has('shooting_outcomes:123'), true);
});

test('consolidation reads sharing a database and table are fetched in one query and regrouped', async () => {
    const observed = [];
    const db = {
        async query(sql, values) {
            observed.push({ sql, values });
            return [
                { matchup_id: '101', ID: 1, strategy_id: 3, strategic_outcome: 10, uses: 1 },
                { matchup_id: '102', ID: 2, strategy_id: 4, strategic_outcome: 20, uses: 1 },
            ];
        },
    };
    const server = makeServer();
    const gameA = patchGame({
        server, db,
        config: { maxOutcomesPerStratType: 5 },
        pendingUpdates: [], pendingInserts: new Map(),
    });
    const gameB = patchGame({
        server, db,
        config: { maxOutcomesPerStratType: 5 },
        pendingUpdates: [], pendingInserts: new Map(),
    });

    const results = await loadConsolidationOutcomes([
        { table: 'strategic_commands', matchup_id: '101', game: gameA },
        { table: 'strategic_commands', matchup_id: '102', game: gameB },
    ]);

    assert.equal(observed.length, 1, 'Two matchups in one table/database should use one SELECT.');
    assert.deepEqual(observed[0].values, [['101', '102']]);
    assert.deepEqual(results.map(result => result.map(row => row.matchup_id)), [['101'], ['102']]);
});
