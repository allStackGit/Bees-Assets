'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    availabilitySignature,
    strategyCacheKey,
    invalidateStrategyCache,
    patchStrategyCaches,
} = require('../gamePersistence');

function makeGame() {
    const server = {
        cachedStrategies: new Map(),
        cachedTargetingStrategies: new Map(),
        cachedShootingStrategies: new Map(),
    };
    const game = {
        server,
        findCachedStrategies(matchupId) {
            return server.cachedStrategies.get(matchupId);
        },
        findCachedTargetingStrategies(matchupId) {
            return server.cachedTargetingStrategies.get(matchupId);
        },
        addToCachedStrategies(selectedStrat, matchupId, table, availableStrats) {
            const cache = table === 'strategic_commands'
                ? server.cachedStrategies
                : table === 'targeting_outcomes'
                    ? server.cachedTargetingStrategies
                    : server.cachedShootingStrategies;
            cache.set(matchupId, { strats: availableStrats });
        },
        async getStrategy(matchup, banned) {
            const matchupId = '42';
            const cached = this.findCachedStrategies(matchupId);
            if (cached) return cached.strats.map(s => s.name);
            const available = [{ name: 'Mining' }, { name: 'Hold' }]
                .filter(s => !banned.includes(s.name));
            this.addToCachedStrategies({ uses: 30 }, matchupId, 'strategic_commands', available, false);
            await new Promise(resolve => setImmediate(resolve));
            return available.map(s => s.name);
        },
        async getMatchupStrategy(shipString, opponentId, hash, timings, banned) {
            const matchupId = '84';
            const cached = this.findCachedTargetingStrategies(matchupId);
            if (cached) return cached.strats.map(s => s.name);
            const available = [{ name: 'Type A' }, { name: 'Random' }]
                .filter(s => !banned.includes(s.name));
            this.addToCachedStrategies({ uses: 30 }, matchupId, 'targeting_outcomes', available, false);
            await new Promise(resolve => setImmediate(resolve));
            return available.map(s => s.name);
        },
    };
    patchStrategyCaches(game);
    return game;
}

test('availability signatures are stable regardless of ban ordering or duplicates', () => {
    assert.equal(availabilitySignature(['Mining', 'Heal', 'Mining']), 'Heal,Mining');
    assert.equal(availabilitySignature(['Heal', 'Mining']), 'Heal,Mining');
    assert.equal(strategyCacheKey('42', 'Heal,Mining'), '42|Heal,Mining');
});

test('strategic cache does not permanently retain a temporary Mining ban', async () => {
    const game = makeGame();

    assert.deepEqual(await game.getStrategy('m', ['Mining']), ['Hold']);
    assert.deepEqual(await game.getStrategy('m', []), ['Mining', 'Hold']);

    assert.equal(game.server.cachedStrategies.has('42|Mining'), true);
    assert.equal(game.server.cachedStrategies.has('42|'), true);
});

test('targeting cache varies with enemy-type availability', async () => {
    const game = makeGame();

    assert.deepEqual(
        await game.getMatchupStrategy('ships', 0, 1, {}, ['Type A']),
        ['Random']);
    assert.deepEqual(
        await game.getMatchupStrategy('ships', 0, 2, {}, []),
        ['Type A', 'Random']);

    assert.equal(game.server.cachedTargetingStrategies.has('84|Type A'), true);
    assert.equal(game.server.cachedTargetingStrategies.has('84|'), true);
});

test('concurrent requests keep their own availability contexts', async () => {
    const game = makeGame();

    const [withoutMining, withMining] = await Promise.all([
        game.getStrategy('m', ['Mining']),
        game.getStrategy('m', []),
    ]);

    assert.deepEqual(withoutMining, ['Hold']);
    assert.deepEqual(withMining, ['Mining', 'Hold']);
    assert.deepEqual(
        game.server.cachedStrategies.get('42|Mining').strats.map(s => s.name),
        ['Hold']);
    assert.deepEqual(
        game.server.cachedStrategies.get('42|').strats.map(s => s.name),
        ['Mining', 'Hold']);
});

test('new outcomes invalidate every availability variant for a matchup', () => {
    const game = makeGame();
    game.server.cachedStrategies.set('42|Mining', {});
    game.server.cachedStrategies.set('42|Heal', {});
    game.server.cachedStrategies.set('42|', {});
    game.server.cachedStrategies.set('other|Mining', {});
    game.server.cachedTargetingStrategies.set('84|Type A', {});
    game.server.cachedTargetingStrategies.set('84|', {});

    invalidateStrategyCache(game.server, 'strategic_commands', '42');
    invalidateStrategyCache(game.server, 'targeting_outcomes', '84');

    assert.equal(game.server.cachedStrategies.has('42|Mining'), false);
    assert.equal(game.server.cachedStrategies.has('42|Heal'), false);
    assert.equal(game.server.cachedStrategies.has('42|'), false);
    assert.equal(game.server.cachedStrategies.has('other|Mining'), true);
    assert.equal(game.server.cachedTargetingStrategies.has('84|Type A'), false);
    assert.equal(game.server.cachedTargetingStrategies.has('84|'), false);
});
