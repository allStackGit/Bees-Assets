'use strict';

const LOOKUP_INDEXES = [
    {
        table: 'stored_user_data',
        name: 'idx_stored_user_data_user_file_id',
        columns: ['userId', 'filename', 'ID'],
    },
    {
        table: 'settings',
        name: 'idx_settings_user_name_version_id',
        columns: ['userId', 'name', 'version', 'Id'],
    },
];

const COLUMN_CAPACITY_MIGRATIONS = [
    {
        table: 'settings',
        column: 'userId',
        name: 'settings_user_id_bigint',
        requiredDataType: 'bigint',
        sql: 'ALTER TABLE `settings` MODIFY COLUMN `userId` BIGINT NOT NULL DEFAULT 0',
    },
    ...['strategic_commands', 'shooting_outcomes', 'targeting_outcomes'].map(table => ({
        table,
        column: 'uses',
        name: `${table}_uses_bigint_unsigned`,
        requiredDataType: 'bigint',
        requiredColumnTypePattern: /unsigned/i,
        sql: `ALTER TABLE \`${table}\` MODIFY COLUMN \`uses\` BIGINT UNSIGNED NOT NULL DEFAULT 1`,
    })),
];

// Backward-compatible export name retained for existing migration callers/tests.
const USER_ID_MIGRATIONS = COLUMN_CAPACITY_MIGRATIONS.filter(
    migration => migration.table === 'settings' && migration.column === 'userId');

async function getIndexColumns(db, databaseName, table) {
    const rows = await db.query(
        `SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME
         FROM information_schema.STATISTICS
         WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ?
         ORDER BY INDEX_NAME, SEQ_IN_INDEX`,
        [databaseName, table]
    );

    const indexes = new Map();
    for (const row of rows) {
        if (!indexes.has(row.INDEX_NAME)) indexes.set(row.INDEX_NAME, []);
        indexes.get(row.INDEX_NAME).push(row.COLUMN_NAME);
    }
    return indexes;
}

async function getColumnType(db, databaseName, table, column) {
    const rows = await db.query(
        `SELECT DATA_TYPE, COLUMN_TYPE
         FROM information_schema.COLUMNS
         WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND COLUMN_NAME = ?`,
        [databaseName, table, column]
    );
    if (!rows || rows.length !== 1) {
        throw new Error(`Expected ${databaseName}.${table}.${column} to exist exactly once.`);
    }
    return rows[0];
}

function hasPrefix(indexes, columns) {
    return [...indexes.values()].some(existing =>
        columns.every((column, index) => existing[index] === column));
}

function migrationAlreadyApplied(definition, migration) {
    if (String(definition.DATA_TYPE).toLowerCase() !== migration.requiredDataType) {
        return false;
    }
    if (migration.requiredColumnTypePattern &&
        !migration.requiredColumnTypePattern.test(String(definition.COLUMN_TYPE))) {
        return false;
    }
    return true;
}

async function ensureColumnCapacity(db, databaseName) {
    const applied = [];
    const alreadyPresent = [];

    for (const migration of COLUMN_CAPACITY_MIGRATIONS) {
        const definition = await getColumnType(
            db, databaseName, migration.table, migration.column);
        if (migrationAlreadyApplied(definition, migration)) {
            alreadyPresent.push(migration.name);
            continue;
        }

        await db.query(migration.sql);
        applied.push(migration.name);
    }

    return { applied, alreadyPresent };
}

async function ensureUserIdCapacity(db, databaseName) {
    const result = { applied: [], alreadyPresent: [] };
    for (const migration of USER_ID_MIGRATIONS) {
        const definition = await getColumnType(
            db, databaseName, migration.table, migration.column);
        if (migrationAlreadyApplied(definition, migration)) {
            result.alreadyPresent.push(migration.name);
        } else {
            await db.query(migration.sql);
            result.applied.push(migration.name);
        }
    }
    return result;
}

async function ensureLookupIndexes(db) {
    const databaseName = db?.config?.connection?.database;
    if (!databaseName) {
        throw new Error('Database name is required to apply BeesServer schema migrations.');
    }

    const capacity = await ensureColumnCapacity(db, databaseName);
    const applied = [...capacity.applied];
    const alreadyPresent = [...capacity.alreadyPresent];

    for (const migration of LOOKUP_INDEXES) {
        const indexes = await getIndexColumns(db, databaseName, migration.table);
        if (hasPrefix(indexes, migration.columns.slice(0, 2))) {
            alreadyPresent.push(migration.name);
            continue;
        }

        const columnSql = migration.columns.map(column => `\`${column}\``).join(', ');
        await db.query(
            `CREATE INDEX \`${migration.name}\` ON \`${migration.table}\` (${columnSql})`
        );
        applied.push(migration.name);
    }

    return { databaseName, applied, alreadyPresent };
}

module.exports = {
    LOOKUP_INDEXES,
    USER_ID_MIGRATIONS,
    COLUMN_CAPACITY_MIGRATIONS,
    ensureLookupIndexes,
    ensureUserIdCapacity,
    ensureColumnCapacity,
    getColumnType,
    getIndexColumns,
    hasPrefix,
    migrationAlreadyApplied,
};
