'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { patchGame, exactConsolidatedRows } = require('../gamePersistence');

function makeGame(db, inserts = []) {
    return patchGame({
        db,
        server: {
            totalInsertTime: 0,
            updateMatchingTime: 0,
            updateMatchingCount: 0,
            totalConsolidatedRows: 0,
            totalConsolidatedInsertRows: 0,
            consolidationQueue: [],
            cachedStrategies: new Map(),
            cachedShootingStrategies: new Map(),
            cachedTargetingStrategies: new Map(),
        },
        pendingUpdates: [],
        pendingInserts: new Map(inserts),
    });
}

function insert(id, table = 2) {
    return [id, {
        matchup_id: `matchup-${id}`,
        strategy_id: id % 10 || 1,
        table,
        time: performance.now(),
    }];
}

test('storeState remains pending until its transaction commits', async () => {
    let releaseWrite;
    const writeGate = new Promise(resolve => { releaseWrite = resolve; });
    const statements = [];
    const game = makeGame({
        async transaction(work) {
            await work(async (sql, values) => {
                statements.push({ sql, values });
                await writeGate;
                return { affectedRows: 1 };
            });
        },
        async query() { return []; },
    }, [insert(101)]);

    let resolved = false;
    const storing = game.storeState([{ OutcomeId: 101, Tsv: 17 }], [], [])
        .then(value => { resolved = true; return value; });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(resolved, false);
    assert.equal(game.pendingInserts.has(101), true,
        'Outcome ownership must remain until the transaction commits.');

    releaseWrite();
    assert.equal(await storing, true);
    assert.equal(game.pendingInserts.has(101), false);
    assert.equal(game.pendingUpdates.length, 0);
    assert.match(statements[0].sql, /^INSERT INTO strategic_commands/);
});

test('committed outcomes invalidate only their affected strategy caches', async () => {
    const game = makeGame({
        async transaction(work) {
            return work(async () => ({ affectedRows: 1 }));
        },
        async query() { return []; },
    }, [insert(201, 2), insert(202, 1), insert(203, 0)]);

    game.server.cachedStrategies.set('matchup-201', { stale: true });
    game.server.cachedStrategies.set('unrelated', { keep: true });
    game.server.cachedShootingStrategies.set('matchup-202', { stale: true });
    game.server.cachedTargetingStrategies.set('matchup-203', { stale: true });

    assert.equal(await game.storeState(
        [{ OutcomeId: 201, Tsv: 4 }],
        [{ OutcomeId: 202, Tsv: 5 }],
        [{ OutcomeId: 203, Tsv: 6 }]), true);

    assert.equal(game.server.cachedStrategies.has('matchup-201'), false);
    assert.equal(game.server.cachedShootingStrategies.has('matchup-202'), false);
    assert.equal(game.server.cachedTargetingStrategies.has('matchup-203'), false);
    assert.equal(game.server.cachedStrategies.has('unrelated'), true,
        'Unrelated learned cache entries must not be evicted.');
});

test('failed storeState preserves cached policy and outcome IDs for retry', async () => {
    const failure = new Error('database unavailable');
    const game = makeGame({
        async transaction() { throw failure; },
        async query() { return []; },
    }, [insert(102)]);
    game.server.cachedStrategies.set('matchup-102', { stillAuthoritative: true });

    await assert.rejects(
        game.storeState([{ OutcomeId: 102, Tsv: 18 }], [], []), failure);

    assert.equal(game.pendingInserts.has(102), true,
        'A failed write must leave the pending insert available for a client retry.');
    assert.equal(game.pendingUpdates.length, 0,
        'The failed request payload itself should not accumulate in pendingUpdates.');
    assert.equal(game.server.cachedStrategies.has('matchup-102'), true,
        'Cache invalidation must occur only after durable commit.');
});

test('a retry can persist the same outcome ID after a transient failure', async () => {
    let attempts = 0;
    const game = makeGame({
        async transaction(work) {
            attempts++;
            if (attempts === 1) throw new Error('temporary outage');
            return work(async () => ({ affectedRows: 1 }));
        },
        async query() { return []; },
    }, [insert(103)]);

    await assert.rejects(
        game.storeState([{ OutcomeId: 103, Tsv: 19 }], [], []), /temporary outage/);
    assert.equal(game.pendingInserts.has(103), true);

    assert.equal(await game.storeState([{ OutcomeId: 103, Tsv: 19 }], [], []), true);
    assert.equal(game.pendingInserts.has(103), false);
    assert.equal(attempts, 2);
});

test('storeState operations for one Game are serialized', async () => {
    let releaseFirst;
    const firstGate = new Promise(resolve => { releaseFirst = resolve; });
    let transactionCount = 0;
    let activeTransactions = 0;
    let maxActiveTransactions = 0;
    const game = makeGame({
        async transaction(work) {
            transactionCount++;
            const ordinal = transactionCount;
            activeTransactions++;
            maxActiveTransactions = Math.max(maxActiveTransactions, activeTransactions);
            try {
                if (ordinal === 1) await firstGate;
                return await work(async () => ({ affectedRows: 1 }));
            } finally {
                activeTransactions--;
            }
        },
        async query() { return []; },
    }, [insert(104), insert(105)]);

    const first = game.storeState([{ OutcomeId: 104, Tsv: 20 }], [], []);
    const second = game.storeState([{ OutcomeId: 105, Tsv: 21 }], [], []);
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(transactionCount, 1,
        'The second store operation must wait behind the first Game transaction.');
    releaseFirst();
    assert.equal(await first, true);
    assert.equal(await second, true);
    assert.equal(transactionCount, 2);
    assert.equal(maxActiveTransactions, 1);
    assert.equal(game.pendingInserts.size, 0);
});

test('unmatched outcome updates are consumed without opening an empty transaction', async () => {
    let transactions = 0;
    const game = makeGame({
        async transaction() { transactions++; },
        async query() { return []; },
    });

    assert.equal(await game.storeState([{ OutcomeId: 999999, Tsv: 1 }], [], []), true);
    assert.equal(transactions, 0);
    assert.equal(game.pendingUpdates.length, 0);
});

test('discard markers release unused outcome reservations without a database write', async () => {
    let transactions = 0;
    const game = makeGame({
        async transaction() { transactions++; },
        async query() { return []; },
    }, [insert(301, 0), insert(302, 1)]);

    assert.equal(await game.storeState([
        { OutcomeId: 301, Tsv: 0, DiscardReservation: true },
        { OutcomeId: 302, Tsv: 0, DiscardReservation: true },
    ], [], []), true);

    assert.equal(transactions, 0);
    assert.equal(game.pendingInserts.has(301), false);
    assert.equal(game.pendingInserts.has(302), false);
    assert.equal(game.pendingUpdates.length, 0);
});

test('discarded reservations stay released when a rewarded write fails', async () => {
    const failure = new Error('database unavailable');
    const game = makeGame({
        async transaction() { throw failure; },
        async query() { return []; },
    }, [insert(401, 2), insert(402, 1)]);

    await assert.rejects(game.storeState([
        { OutcomeId: 401, Tsv: 9 },
        { OutcomeId: 402, Tsv: 0, DiscardReservation: true },
    ], [], []), failure);

    assert.equal(game.pendingInserts.has(401), true,
        'Rewarded outcome must remain available for retry.');
    assert.equal(game.pendingInserts.has(402), false,
        'Discarded outcome must not re-enter the two-hour pending window.');
    assert.equal(game.pendingUpdates.length, 0);
});

function reconstructed(rows) {
    return {
        uses: rows.reduce((sum, row) => sum + row[3], 0),
        tsv: rows.reduce((sum, row) => sum + (row[2] * row[3]), 0),
    };
}

test('exact consolidation preserves fractional positive average without changing value', () => {
    const rows = exactConsolidatedRows('m', 7, 1, 2);
    assert.deepEqual(reconstructed(rows), { uses: 2, tsv: 1 });
    assert.equal(rows.length, 2);
});

test('exact consolidation preserves fractional negative average without changing value', () => {
    const rows = exactConsolidatedRows('m', 7, -5, 3);
    assert.deepEqual(reconstructed(rows), { uses: 3, tsv: -5 });
    assert.ok(rows.length <= 2);
});

test('exact consolidation keeps already integral averages in one row', () => {
    const rows = exactConsolidatedRows('m', 7, 24, 6);
    assert.deepEqual(rows, [['m', 7, 4, 6]]);
    assert.deepEqual(reconstructed(rows), { uses: 6, tsv: 24 });
});
