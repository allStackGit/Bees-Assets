'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    exactConsolidatedRows,
    exactStrategyAggregate,
    toStoreOperation,
} = require('../gamePersistence');

function reconstructExactly(rows) {
    return rows.reduce((aggregate, row) => ({
        uses: aggregate.uses + BigInt(row[3]),
        tsv: aggregate.tsv + (BigInt(row[2]) * BigInt(row[3])),
    }), { uses: 0n, tsv: 0n });
}

test('one StoreCommands request rewards a repeated outcome reservation at most once', () => {
    const operation = toStoreOperation(
        [
            { OutcomeId: 711, Tsv: 10 },
            { OutcomeId: '711', Tsv: 10 },
        ],
        [],
        []);

    assert.deepEqual(operation.updates, [
        { table: 'strategic_commands', tsv: 10, id: 711 },
    ]);
});

test('explicit discard wins over a duplicate reward marker in the same payload', () => {
    const operation = toStoreOperation(
        [
            { OutcomeId: 712, Tsv: 10 },
            { OutcomeId: 712, Tsv: 0, DiscardReservation: true },
        ],
        [],
        []);

    assert.deepEqual(operation.updates, []);
    assert.deepEqual(operation.discardedOutcomeIds, [712]);
});

test('exact consolidation preserves weighted TSV above Number.MAX_SAFE_INTEGER', () => {
    const outcomes = [
        { strategy_id: 7, strategic_outcome: 8_000_000, uses: '2000000000' },
        { strategy_id: 7, strategic_outcome: 7_999_999, uses: '1' },
    ];
    const aggregate = exactStrategyAggregate(outcomes, 7);

    assert.ok(aggregate.tsv > BigInt(Number.MAX_SAFE_INTEGER));
    assert.equal(aggregate.uses, 2_000_000_001n);

    const rows = exactConsolidatedRows('18446744073709551614', 7, aggregate.tsv, aggregate.uses);
    assert.ok(rows.length > 0,
        'An unsafe Number-sized weighted total must never turn into an empty replacement set.');
    assert.deepEqual(reconstructExactly(rows), {
        uses: aggregate.uses,
        tsv: aggregate.tsv,
    });
});
