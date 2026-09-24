'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const liveEnabled = process.env.BEES_LIVE_INTEGRATION === '1';
const EXPECTED_DATABASE = 'bees_test';
const CURRENT_CLIENT_SETTINGS_VERSION = 5;

const REQUIRED_TABLE_COLUMNS = {
    settings: ['Id', 'userId', 'version', 'name', 'contents', 'created_date', 'edited_date'],
    stored_user_data: ['ID', 'userId', 'filename', 'contents', 'created_date', 'edited_date'],
    strategic_commands: ['ID', 'matchup_id', 'strategy_id', 'strategic_outcome', 'uses'],
    shooting_outcomes: ['ID', 'matchup_id', 'strategy_id', 'strategic_outcome', 'uses'],
    targeting_outcomes: ['ID', 'matchup_id', 'strategy_id', 'strategic_outcome', 'uses'],
};

const REQUIRED_SETTINGS_FIELDS = {
    configuration: [
        'IsDeadVersion', 'UseLocalStorage', 'MirrorStorage', 'MaxSquadSize',
        'MaxSquadWidth', 'MaxSquadHeight', 'RotationMultiplier', 'HumanSide', 'BeeSide',
        'DoesUserHaveController', 'StorageChunkSize', 'StandardMaxTimeOnQueue', 'TimeScale',
        'AISquadPatrolTime', 'AIPatrolMaxSize', 'AISquadGuardTime', 'AISquadFollowingTime',
        'CarrierCarryDroneMax', 'CarrierCarryStrikerMax', 'CarrierSquadCount', 'TotalLevels',
        'SquadSavingStatusAlertTitle', 'SquadSavingStatusAlert', 'AISide', 'UserSide',
        'SquadMakerFirstSide', 'SquadMakerSecondSide', 'CensoredWords', 'Tooltips',
    ],
    'starting-settings': [
        'SupplyCapacity', 'HumanStartingShips', 'BeeStartingShips',
        'HumanCampaignStartingShips', 'BeeCampaignStartingShips',
        'HumanChallengeStartingShips', 'BeeChallengeStartingShips',
    ],
    'ship-stats': [
        'ShipType', 'Description', 'CodexDescription', 'Health', 'Range', 'Power', 'Sight',
        'Tsv', 'ProjectileValue', 'RateOfFire', 'RotationRates', 'Speed', 'WeaponTypes',
        'WeaponSoundTypes', 'ProjectileTypes',
    ],
};

function requireLiveDbConfig() {
    const host = process.env.BEES_DB_HOST || '127.0.0.1';
    const user = process.env.BEES_DB_USER;
    const password = process.env.BEES_DB_PASSWORD;
    if (!user || password === undefined) {
        throw new Error(
            'BEES_DB_USER and BEES_DB_PASSWORD are required for live database qualification.');
    }
    return { host, user, password };
}

async function withConnection(work) {
    const mysql = require('mysql2/promise');
    const config = requireLiveDbConfig();
    const connection = await mysql.createConnection({
        ...config,
        database: EXPECTED_DATABASE,
        supportBigNumbers: true,
        bigNumberStrings: true,
        charset: 'utf8mb4',
    });
    try {
        const [[selected]] = await connection.query('SELECT DATABASE() AS database_name');
        assert.equal(selected.database_name, EXPECTED_DATABASE,
            'Live qualification must never execute against the production database.');
        return await work(connection);
    } finally {
        await connection.end();
    }
}

function assertSerializedFields(contents, fields, label) {
    assert.equal(typeof contents, 'string', `${label} contents must be text.`);
    for (const field of fields) {
        const pattern = new RegExp(`"${field}"\\s*:`);
        assert.match(contents, pattern,
            `${label} is missing serialized field '${field}'.`);
    }
}

async function getIndexes(connection, table) {
    const [rows] = await connection.query(
        `SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME
         FROM information_schema.STATISTICS
         WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ?
         ORDER BY INDEX_NAME, SEQ_IN_INDEX`,
        [EXPECTED_DATABASE, table]);

    const indexes = new Map();
    for (const row of rows) {
        if (!indexes.has(row.INDEX_NAME)) indexes.set(row.INDEX_NAME, []);
        indexes.get(row.INDEX_NAME).push(row.COLUMN_NAME);
    }
    return indexes;
}

function hasIndexPrefix(indexes, prefix) {
    return [...indexes.values()].some(columns =>
        prefix.every((column, index) => columns[index] === column));
}

test('bees_test contains the required application tables and columns', {
    skip: !liveEnabled,
}, async () => {
    await withConnection(async connection => {
        const [rows] = await connection.query(
            `SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE
             FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = ?
             ORDER BY TABLE_NAME, ORDINAL_POSITION`,
            [EXPECTED_DATABASE]);

        const byTable = new Map();
        for (const row of rows) {
            if (!byTable.has(row.TABLE_NAME)) byTable.set(row.TABLE_NAME, new Map());
            byTable.get(row.TABLE_NAME).set(row.COLUMN_NAME, row.COLUMN_TYPE);
        }

        for (const [table, columns] of Object.entries(REQUIRED_TABLE_COLUMNS)) {
            assert.ok(byTable.has(table), `Missing required table '${table}'.`);
            for (const column of columns) {
                assert.ok(byTable.get(table).has(column),
                    `Table '${table}' is missing required column '${column}'.`);
            }
        }

        assert.match(byTable.get('settings').get('userId'), /bigint/i,
            'settings.userId must be BIGINT so SteamID64 identities fit without truncation.');
        assert.match(byTable.get('stored_user_data').get('userId'), /bigint/i,
            'stored_user_data.userId must remain BIGINT for SteamID64 identities.');

        for (const table of ['strategic_commands', 'shooting_outcomes', 'targeting_outcomes']) {
            assert.match(byTable.get(table).get('matchup_id'), /bigint.*unsigned/i,
                `${table}.matchup_id must remain BIGINT UNSIGNED for xxHash64 IDs.`);
            assert.match(byTable.get(table).get('uses'), /bigint.*unsigned/i,
                `${table}.uses must be BIGINT UNSIGNED because consolidation accumulates lifetime policy uses.`);
        }
    });
});

test('outcome tables contain only strategy IDs understood by the current server', {
    skip: !liveEnabled,
}, async () => {
    await withConnection(async connection => {
        const expectedRanges = {
            strategic_commands: [1, 15],
            targeting_outcomes: [1, 40],
            shooting_outcomes: [1, 40],
        };

        for (const [table, [minimum, maximum]] of Object.entries(expectedRanges)) {
            const [[row]] = await connection.query(
                `SELECT MIN(strategy_id) AS minimum_id, MAX(strategy_id) AS maximum_id FROM ${table}`);
            if (row.minimum_id === null) continue;
            assert.ok(Number(row.minimum_id) >= minimum,
                `${table} contains a strategy ID below ${minimum}.`);
            assert.ok(Number(row.maximum_id) <= maximum,
                `${table} contains a strategy ID above ${maximum}; historical rows may no longer map to the server registry.`);
        }
    });
});

test('bees_test has current-version settings fields required by Unity', {
    skip: !liveEnabled,
}, async () => {
    await withConnection(async connection => {
        const [rows] = await connection.query(
            `SELECT name, contents
             FROM settings
             WHERE userId = 0 AND version = ?
             ORDER BY Id DESC`,
            [CURRENT_CLIENT_SETTINGS_VERSION]);

        const latest = new Map();
        for (const row of rows) {
            if (!latest.has(row.name)) latest.set(row.name, row.contents);
        }

        for (const [settingsName, fields] of Object.entries(REQUIRED_SETTINGS_FIELDS)) {
            assert.ok(latest.has(settingsName),
                `Missing global '${settingsName}' settings for Unity ConfigData.Version ${CURRENT_CLIENT_SETTINGS_VERSION}.`);
            assertSerializedFields(
                latest.get(settingsName), fields,
                `${settingsName} v${CURRENT_CLIENT_SETTINGS_VERSION}`);
        }
    });
});

test('bees_test has indexes for the two hot logical lookup keys', {
    skip: !liveEnabled,
}, async () => {
    await withConnection(async connection => {
        const userDataIndexes = await getIndexes(connection, 'stored_user_data');
        assert.equal(hasIndexPrefix(userDataIndexes, ['userId', 'filename']), true,
            'stored_user_data must index (userId, filename[, ID]).');

        const settingsIndexes = await getIndexes(connection, 'settings');
        assert.equal(hasIndexPrefix(settingsIndexes, ['userId', 'name', 'version']), true,
            'settings must index (userId, name, version[, Id]).');
    });
});
