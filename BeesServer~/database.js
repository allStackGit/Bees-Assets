'use strict';

class Database {
    static TestDatabase = 'bees_test';

    static selectDatabase(normalDatabase, isTest = false) {
        return isTest ? Database.TestDatabase : normalDatabase;
    }

    /**
     * User IDs cross the client/server boundary as decimal strings so Steam64 values are
     * lossless in JavaScript. mysql2 returns ordinary INT columns as numbers even when
     * bigNumberStrings is enabled, which made per-user settings comparisons fail under
     * strict equality (for example 123 !== "123"). Normalize any returned userId field
     * to the same string representation used by the wire contract.
     */
    static normalizeUserIds(results) {
        if (!Array.isArray(results) || results.length === 0) return results;
        const firstRow = results[0];
        if (!firstRow || Array.isArray(firstRow) ||
            !Object.prototype.hasOwnProperty.call(firstRow, 'userId')) {
            return results;
        }
        for (const row of results) {
            if (row && row.userId != null) {
                row.userId = String(row.userId);
            }
        }
        return results;
    }

    constructor(host, user, password, database, mysqlModule = null, isTest = false) {
        this.mysql = mysqlModule || require('mysql2');

        // Connection selection belongs to the caller. server.js/start-server.js resolve the
        // intentionally checked-in development defaults and any environment overrides before
        // constructing Database. Keeping this constructor argument-driven prevents a test runner's
        // BEES_DB_NAME from silently rewriting unrelated non-test Database instances.
        this.config = {
            connection: {
                host,
                user,
                password,
                database: Database.selectDatabase(database, isTest),
            },
        };
        this.connection = undefined;
    }

    handleDisconnect = () => {
        console.log('Handling database connection/disconnection');
        this.connection = this.mysql.createPool({
            host: this.config.connection.host,
            user: this.config.connection.user,
            password: this.config.connection.password,
            database: this.config.connection.database,
            connectionLimit: 128,
            multipleStatements: true,
            charset: 'utf8mb4',
            supportBigNumbers: true,
            bigNumberStrings: true,
        });

        this.connection.on('error', (err) => {
            console.log('db error', err);
            const isRecoverableDisconnect =
                err.code === 'PROTOCOL_CONNECTION_LOST' ||
                err.code === 'ER_CLIENT_INTERACTION_TIMEOUT' ||
                err.errno === 4031;

            if (isRecoverableDisconnect) {
                console.log('Connection lost. Trying to handle.');
                this.handleDisconnect();
                return;
            }

            console.error('DB Error:', err);
        });
    };

    getConnection = () => new Promise((resolve, reject) => {
        this.connection.getConnection((err, connection) => {
            if (err) {
                reject(err);
                return;
            }
            resolve(connection);
        });
    });

    queryConnection = (connection, sql, values) => new Promise((resolve, reject) => {
        connection.query(sql, values, (error, results) => {
            if (error) {
                reject(error);
                return;
            }
            resolve(Database.normalizeUserIds(results));
        });
    });

    query = (sql, values) => {
        // mysql2 pools can acquire/release an appropriate connection internally for ordinary
        // one-shot queries. Retain the explicit checkout fallback for lightweight test doubles
        // and keep transactions on one borrowed connection below.
        if (typeof this.connection?.query !== 'function') {
            return this.getConnection().then(async connection => {
                try {
                    return await this.queryConnection(connection, sql, values);
                } finally {
                    connection.release();
                }
            });
        }
        return new Promise((resolve, reject) => {
            this.connection.query(sql, values, (error, results) => {
                if (error) {
                    reject(error);
                    return;
                }
                resolve(Database.normalizeUserIds(results));
            });
        });
    };

    transaction = async (work) => {
        if (typeof work !== 'function') {
            throw new TypeError('Database.transaction requires a callback.');
        }

        const connection = await this.getConnection();
        const query = (sql, values) => this.queryConnection(connection, sql, values);
        try {
            await query('START TRANSACTION');
            const result = await work(query);
            await query('COMMIT');
            return result;
        } catch (error) {
            try {
                await query('ROLLBACK');
            } catch (rollbackError) {
                error.rollbackError = rollbackError;
            }
            throw error;
        } finally {
            connection.release();
        }
    };
}

module.exports = Database;
