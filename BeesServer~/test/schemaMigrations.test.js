'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { ensureLookupIndexes } = require('../schemaMigrations');

function makeDb(existingByTable = {}, columnTypes = {}) {
    const calls = [];
    const defaults = {
        'settings.userId': { DATA_TYPE: 'bigint', COLUMN_TYPE: 'bigint' },
        'strategic_commands.uses': { DATA_TYPE: 'bigint', COLUMN_TYPE: 'bigint unsigned' },
        'shooting_outcomes.uses': { DATA_TYPE: 'bigint', COLUMN_TYPE: 'bigint unsigned' },
        'targeting_outcomes.uses': { DATA_TYPE: 'bigint', COLUMN_TYPE: 'bigint unsigned' },
    };
    const db = {
        config: { connection: { database: 'bees_test' } },
        async query(sql, values) {
            calls.push({ sql, values });
            if (sql.includes('information_schema.COLUMNS')) {
                const key = `${values[1]}.${values[2]}`;
                return [columnTypes[key] || defaults[key]];
            }
            if (sql.includes('information_schema.STATISTICS')) {
                const table = values[1];
                const definitions = existingByTable[table] || [];
                return definitions.flatMap(definition =>
                    definition.columns.map((column, index) => ({
                        INDEX_NAME: definition.name,
                        SEQ_IN_INDEX: index + 1,
                        COLUMN_NAME: column,
                    })));
            }
            if (sql.startsWith('CREATE INDEX')) return { affectedRows: 0 };
            if (sql.startsWith('ALTER TABLE ')) return { affectedRows: 0 };
            throw new Error(`Unexpected query: ${sql}`);
        },
    };
    return { db, calls };
}

test('adds the hot lookup indexes when capacity migrations are already present', async () => {
    const { db, calls } = makeDb();

    const result = await ensureLookupIndexes(db);

    assert.deepEqual(result.applied, [
        'idx_stored_user_data_user_file_id',
        'idx_settings_user_name_version_id',
    ]);
    const creates = calls.filter(call => call.sql.startsWith('CREATE INDEX'));
    assert.equal(creates.length, 2);
    assert.match(creates[0].sql,
        /stored_user_data.*`userId`, `filename`, `ID`/);
    assert.match(creates[1].sql,
        /settings.*`userId`, `name`, `version`, `Id`/);
});

test('widens settings userId so SteamID64 values fit', async () => {
    const { db, calls } = makeDb({}, {
        'settings.userId': { DATA_TYPE: 'int', COLUMN_TYPE: 'int(11)' },
    });

    const result = await ensureLookupIndexes(db);

    assert.equal(result.applied.includes('settings_user_id_bigint'), true);
    const alters = calls.filter(call => call.sql.startsWith('ALTER TABLE `settings`'));
    assert.equal(alters.length, 1);
    assert.match(alters[0].sql, /MODIFY COLUMN `userId` BIGINT NOT NULL DEFAULT 0/);
});

test('widens all lifetime learning use counters beyond MEDIUMINT capacity', async () => {
    const medium = { DATA_TYPE: 'mediumint', COLUMN_TYPE: 'mediumint(8) unsigned' };
    const { db, calls } = makeDb({}, {
        'strategic_commands.uses': medium,
        'shooting_outcomes.uses': medium,
        'targeting_outcomes.uses': medium,
    });

    const result = await ensureLookupIndexes(db);

    for (const table of ['strategic_commands', 'shooting_outcomes', 'targeting_outcomes']) {
        assert.equal(result.applied.includes(`${table}_uses_bigint_unsigned`), true);
        const alter = calls.find(call => call.sql.startsWith(`ALTER TABLE \`${table}\``));
        assert.ok(alter, `Expected ${table}.uses migration.`);
        assert.match(alter.sql, /MODIFY COLUMN `uses` BIGINT UNSIGNED NOT NULL DEFAULT 1/);
    }
});

test('unsigned requirement is enforced even when uses is already BIGINT', async () => {
    const { db, calls } = makeDb({}, {
        'strategic_commands.uses': { DATA_TYPE: 'bigint', COLUMN_TYPE: 'bigint' },
    });

    const result = await ensureLookupIndexes(db);

    assert.equal(result.applied.includes('strategic_commands_uses_bigint_unsigned'), true);
    assert.equal(calls.some(call =>
        call.sql.startsWith('ALTER TABLE `strategic_commands`')), true);
});

test('does not rewrite capacity columns when they are already correct', async () => {
    const { db, calls } = makeDb();

    const result = await ensureLookupIndexes(db);

    assert.equal(result.alreadyPresent.includes('settings_user_id_bigint'), true);
    assert.equal(result.alreadyPresent.includes('strategic_commands_uses_bigint_unsigned'), true);
    assert.equal(result.alreadyPresent.includes('shooting_outcomes_uses_bigint_unsigned'), true);
    assert.equal(result.alreadyPresent.includes('targeting_outcomes_uses_bigint_unsigned'), true);
    assert.equal(calls.some(call => call.sql.startsWith('ALTER TABLE ')), false);
});

test('is idempotent when equivalent lookup prefixes already exist', async () => {
    const { db, calls } = makeDb({
        stored_user_data: [{
            name: 'some_existing_index',
            columns: ['userId', 'filename', 'ID'],
        }],
        settings: [{
            name: 'another_existing_index',
            columns: ['userId', 'name', 'version', 'Id'],
        }],
    });

    const result = await ensureLookupIndexes(db);

    assert.equal(result.applied.length, 0);
    assert.equal(result.alreadyPresent.length, 6);
    assert.equal(calls.some(call => call.sql.startsWith('CREATE INDEX')), false);
    assert.equal(calls.some(call => call.sql.startsWith('ALTER TABLE ')), false);
});

test('does not mistake a differently ordered index for the required lookup prefix', async () => {
    const { db } = makeDb({
        stored_user_data: [{ name: 'wrong_order', columns: ['ID', 'userId', 'filename'] }],
        settings: [{ name: 'wrong_order', columns: ['Id', 'userId', 'name', 'version'] }],
    });

    const result = await ensureLookupIndexes(db);

    assert.equal(result.applied.length, 2);
});

test('refuses migration without an explicit selected database', async () => {
    await assert.rejects(
        ensureLookupIndexes({ config: { connection: {} }, query: async () => [] }),
        /Database name is required/);
});
