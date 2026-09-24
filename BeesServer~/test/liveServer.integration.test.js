'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const liveEnabled = process.env.BEES_LIVE_INTEGRATION === '1';

function requireLiveConfiguration() {
    const url = process.env.BEES_TEST_SERVER_URL;
    const userId = Number.parseInt(process.env.BEES_TEST_USER_ID || '', 10);
    if (!url) throw new Error('BEES_TEST_SERVER_URL is required for live integration tests.');
    if (!Number.isInteger(userId) || userId < 1000000) {
        throw new Error('BEES_TEST_USER_ID must be an explicitly reserved integer >= 1000000.');
    }

    const parsed = new URL(url);
    const isLocal = parsed.hostname === '127.0.0.1' || parsed.hostname === 'localhost' || parsed.hostname === '::1';
    if (!isLocal && process.env.BEES_ALLOW_REMOTE_TEST_SERVER !== '1') {
        throw new Error(
            'Refusing to mutate a non-local server. Set BEES_ALLOW_REMOTE_TEST_SERVER=1 only for an isolated test server/database.');
    }
    return { url, userId };
}

function connect(url) {
    const { client: WebSocketClient } = require('websocket');
    return new Promise((resolve, reject) => {
        const client = new WebSocketClient();
        client.once('connectFailed', reject);
        client.once('connect', connection => resolve(connection));
        client.connect(url, 'game');
    });
}

function nextHash() {
    return Math.floor(Date.now() * 1000 + Math.random() * 1000);
}

function request(connection, payload, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
            cleanup();
            reject(new Error(`Timed out waiting for ${payload.Type} (${payload.Hash}).`));
        }, timeoutMs);
        const onError = error => {
            cleanup();
            reject(error);
        };
        const onMessage = message => {
            if (message.type !== 'utf8') return;
            const response = JSON.parse(message.utf8Data);
            if (response.Hash !== payload.Hash) return;
            cleanup();
            resolve(response);
        };
        const cleanup = () => {
            clearTimeout(timeout);
            connection.removeListener('error', onError);
            connection.removeListener('message', onMessage);
        };

        connection.on('error', onError);
        connection.on('message', onMessage);
        connection.sendUTF(JSON.stringify(payload));
    });
}

function close(connection) {
    if (!connection) return;
    try { connection.close(); } catch (_) { /* best-effort test cleanup */ }
}

test('live test user exercises user data, strategy persistence and reconnect flow', {
    skip: !liveEnabled,
    timeout: 30000,
}, async () => {
    const { url, userId } = requireLiveConfiguration();
    const dataFile = '__integration_test__state';
    const firstContents = JSON.stringify({
        marker: 'bees-live-integration',
        phase: 1,
        run: new Date().toISOString(),
    });
    const secondContents = JSON.stringify({
        marker: 'bees-live-integration',
        phase: 2,
        run: new Date().toISOString(),
    });

    let firstConnection;
    let secondConnection;
    try {
        firstConnection = await connect(url);

        const storeFirst = await request(firstConnection, {
            Type: 'store-user-data',
            Hash: nextHash(),
            UserId: userId,
            DataFile: dataFile,
            Contents: firstContents,
        });
        assert.equal(storeFirst.Status, 200);

        const getFirst = await request(firstConnection, {
            Type: 'get-user-data',
            Hash: nextHash(),
            UserId: userId,
            DataFile: dataFile,
            Nonce: nextHash(),
        });
        assert.equal(getFirst.UserId, userId);
        assert.equal(getFirst.Filename, dataFile);
        assert.equal(getFirst.Contents, firstContents);

        const setup = await request(firstConnection, {
            Type: 'setup-level',
            Hash: nextHash(),
            UserId: userId,
            LevelId: 0,
        });
        assert.equal(setup.Status, 200);
        assert.ok(setup.GameId);
        const gameId = setup.GameId;

        const strategy = await request(firstConnection, {
            Type: 'get-strategy',
            Hash: nextHash(),
            Matchup: 'A|B|',
            BannedStrats: [],
            OpponentId: userId,
        });
        assert.equal(strategy.Type, 'get-strategy');
        assert.ok(strategy.OutcomeId,
            'A real command strategy request must allocate a strategic outcome ID.');
        assert.ok(strategy.ShootingStrategyOutcomeId,
            'A real command strategy request must allocate a shooting outcome ID.');

        const storeCommands = await request(firstConnection, {
            Type: 'store-commands',
            Hash: nextHash(),
            Commands: [{ Tsv: 7, OutcomeId: strategy.OutcomeId }],
            ShootingCommands: [{ Tsv: 5, OutcomeId: strategy.ShootingStrategyOutcomeId }],
            TargetingCommands: [],
        });
        assert.equal(storeCommands.Status, 200,
            'store-commands success is returned only after the fixed persistence transaction completes.');

        // Read the same matchup again after persistence. The exact selected strategy is random,
        // but the request must successfully traverse the just-written outcome tables.
        const strategyAfterWrite = await request(firstConnection, {
            Type: 'get-strategy',
            Hash: nextHash(),
            Matchup: 'A|B|',
            BannedStrats: [],
            OpponentId: userId,
        });
        assert.equal(strategyAfterWrite.Type, 'get-strategy');
        assert.ok(strategyAfterWrite.OutcomeId);
        assert.ok(strategyAfterWrite.ShootingStrategyOutcomeId);

        close(firstConnection);
        firstConnection = null;
        await new Promise(resolve => setTimeout(resolve, 50));

        secondConnection = await connect(url);
        const reconnect = await request(secondConnection, {
            Type: 'reconnect-level',
            Hash: nextHash(),
            UserId: userId,
            LevelId: 0,
            GameId: gameId,
        });
        assert.equal(reconnect.Status, 200);
        assert.equal(reconnect.GameId, gameId,
            'A reconnect should resume the same in-memory game while it is retained.');

        const storeSecond = await request(secondConnection, {
            Type: 'store-user-data',
            Hash: nextHash(),
            UserId: userId,
            DataFile: dataFile,
            Contents: secondContents,
        });
        assert.equal(storeSecond.Status, 200);

        const getSecond = await request(secondConnection, {
            Type: 'get-user-data',
            Hash: nextHash(),
            UserId: userId,
            DataFile: dataFile,
            Nonce: nextHash(),
        });
        assert.equal(getSecond.Contents, secondContents,
            'The reserved test row must be updateable through the real WebSocket/database path.');
    } finally {
        close(firstConnection);
        close(secondConnection);
    }
});
