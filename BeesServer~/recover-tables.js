'use strict';

const fs = require('node:fs');
const path = require('node:path');
const mysql = require('mysql2/promise');

const config = {
    host: process.env.BEES_DB_HOST || 'localhost',
    user: process.env.BEES_DB_USER || 'root',
    password: process.env.BEES_DB_PASSWORD || '',
    database: process.env.BEES_DB_NAME || 'ram',
    batchSize: 10000,
    tables: ['strategic_commands'],
    outputDir: './table_dumps',
    maxQueryAttempts: 3,
};

async function executeWithRetry(connection, query, attempts = config.maxQueryAttempts) {
    let lastError;
    for (let attempt = 1; attempt <= attempts; attempt++) {
        try {
            return await connection.execute(query);
        } catch (error) {
            lastError = error;
            console.error(`Query attempt ${attempt}/${attempts} failed: ${error.message}`);
        }
    }
    throw lastError;
}

async function writeRecoveryBatch(fileHandle, contents) {
    await fileHandle.writeFile(contents, 'utf8');
}

async function recoverTables(options = config) {
    fs.mkdirSync(options.outputDir, { recursive: true });
    const connection = await mysql.createConnection({
        host: options.host,
        user: options.user,
        password: options.password,
        database: options.database,
    });

    try {
        for (const table of options.tables) {
            console.log(`Recovering table: ${table}`);
            const outputPath = path.join(options.outputDir, `${table}.sql`);
            const fileHandle = await fs.promises.open(outputPath, 'w');
            let lastId = 0;
            let totalRecovered = 0;

            try {
                while (true) {
                    const query = `SELECT * FROM \`${table}\` WHERE ID > ${lastId} ORDER BY ID ASC LIMIT ${options.batchSize}`;
                    const [rows, fields] = await executeWithRetry(
                        connection,
                        query,
                        options.maxQueryAttempts ?? 3);
                    if (rows.length === 0) break;

                    const columns = fields.map(field => `\`${field.name}\``).join(', ');
                    const insertHead = `INSERT INTO \`${table}\` (${columns}) VALUES\n`;
                    const values = rows.map(row =>
                        `(${fields.map(field => formatValue(row[field.name])).join(', ')})`
                    ).join(',\n');
                    await writeRecoveryBatch(fileHandle, insertHead + values + ';\n');

                    lastId = rows[rows.length - 1].ID;
                    totalRecovered += rows.length;
                    process.stdout.write(`  Rows recovered: ${totalRecovered}\r`);
                }
            } finally {
                await fileHandle.close();
            }

            console.log(`\nFinished: ${table}. Total rows recovered: ${totalRecovered}\n`);
        }
    } finally {
        await connection.end();
    }
}

function formatValue(val) {
    if (val === null || val === undefined) return 'NULL';
    if (typeof val === 'number') return val;
    return `'${escapeSQL(val.toString())}'`;
}

function escapeSQL(str) {
    return str
        .replace(/\\/g, '\\\\')
        .replace(/'/g, "\\'")
        .replace(/\n/g, '\\n')
        .replace(/\r/g, '\\r')
        .replace(/\t/g, '\\t');
}

if (require.main === module) {
    recoverTables().catch(error => {
        console.error(`Recovery aborted without skipping rows: ${error.stack || error.message}`);
        process.exitCode = 1;
    });
}

module.exports = { config, executeWithRetry, writeRecoveryBatch, recoverTables, formatValue, escapeSQL };
