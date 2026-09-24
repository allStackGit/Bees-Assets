'use strict';

const Database = require('./database');
const { ensureLookupIndexes } = require('./schemaMigrations');

function parseMode(argv = process.argv.slice(2)) {
    const production = argv.includes('--production');
    const test = argv.includes('--test') || !production;
    if (production && process.env.BEES_ALLOW_PRODUCTION_MIGRATION !== '1') {
        throw new Error(
            'Production migration requires BEES_ALLOW_PRODUCTION_MIGRATION=1.');
    }
    return { production, test };
}

function requiredEnvironment(name) {
    const value = process.env[name];
    if (value === undefined || value === '') {
        throw new Error(`${name} is required for database migrations.`);
    }
    return value;
}

async function closePool(db) {
    if (!db.connection || typeof db.connection.end !== 'function') return;
    await new Promise((resolve, reject) => {
        db.connection.end(error => error ? reject(error) : resolve());
    });
}

async function main(argv = process.argv.slice(2)) {
    const mode = parseMode(argv);
    const host = process.env.BEES_DB_HOST || '127.0.0.1';
    const user = requiredEnvironment('BEES_DB_USER');
    const password = requiredEnvironment('BEES_DB_PASSWORD');
    const normalDatabase = process.env.BEES_DB_NAME || 'ram';

    const db = new Database(host, user, password, normalDatabase, null, mode.test);
    if (mode.production && db.config.connection.database !== normalDatabase) {
        throw new Error('Production migration selected an unexpected database.');
    }
    if (mode.test && db.config.connection.database !== Database.TestDatabase) {
        throw new Error('Test migration must target bees_test.');
    }

    db.handleDisconnect();
    try {
        const result = await ensureLookupIndexes(db);
        console.log(`Schema migration database: ${result.databaseName}`);
        if (result.applied.length > 0) {
            console.log(`Applied: ${result.applied.join(', ')}`);
        }
        if (result.alreadyPresent.length > 0) {
            console.log(`Already present: ${result.alreadyPresent.join(', ')}`);
        }
        return result;
    } finally {
        await closePool(db);
    }
}

if (require.main === module) {
    main().catch(error => {
        console.error(error);
        process.exitCode = 1;
    });
}

module.exports = { main, parseMode };
