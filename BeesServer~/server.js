'use strict';

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const Database = require('./database');
const { patchGame, invalidateStrategyCache } = require('./gamePersistence');
const { patchOutcomeDurability } = require('./outcomeReservations');
const { installCampaignCheckpoint } = require('./campaignCheckpoint');

const SOCKET_PATCHED = Symbol('beesSocketConnectionPatched');
const CONNECTION_STATE_TAIL = Symbol('beesConnectionStateTail');
const SERIALIZED_REQUEST_TYPES = new Set([
    'setup-level',
    'reconnect-level',
    'store-user-data',
    'get-user-data',
    'get-settings',
]);
const SHARED_SERVER_DATA_FILES = new Set([
    'campaign_levels_data',
    'challenge_levels_data',
]);

function pendingRequestKey(connectionId, hash) {
    return `${connectionId}:${hash}`;
}

function loadLegacyRuntime(options = {}) {
    const sourcePath = path.join(__dirname, 'siServerDev.js');
    let source = fs.readFileSync(sourcePath, 'utf8').replace(/\r\n?/g, '\n');

    const markers = {
        startup: 'let server = new Server(test, port);',
        start: '            console.log(`Worker ${process.pid} started`);\n            this.start();',
        messageParse:
            '            let request = new SocketRequest(JSON.parse(message.utf8Data), this.connection, this.server, common.timer(), 0, 0, this.id);',
        userDataRead:
            '                data = await this.user.getData(request.params.DataFile);\n' +
            '            } catch (e) {\n' +
            '                common.handleError(e);\n' +
            '            }\n' +
            '            if (data && data[0]) {',
        settingsRead:
            '                settings = await this.user.getSettings(request.params.DataFile, request.params.Version);\n' +
            '            } catch (e) {\n' +
            '                common.handleError(e);\n' +
            '            }\n' +
            '            if (settings) {',
        settingsUserIdCompare:
            '                settings = outcomes[0].userId === this.userId ? outcomes[0] : outcomes[1];',
        pendingRequestHas:
            '            if (this.server.pendingRequests.has(request.params.Hash)) {',
        pendingRequestSet:
            '                this.server.pendingRequests.set(request.params.Hash, 1);',
        shootingCacheReset:
            '            availableShootingStrats = cachedShootingStrategy.strats;\n' +
            '            timings.shootingOutcomes = availableShootingStrats.reduce',
        setupGame:
            '            // Create a new Game instance\n' +
            '            this.game = new Game(this.db, this.connection, this.server, request.params.LevelId, this.id);\n' +
            '            this.server.games.set(this.id, this.game);',
        reconnectLookup:
            '            this.game = this.server.games.get(request.params.GameId);',
        outcomeId:
            '    insertOutcome = (table, matchup_id, stratID, opponent_id) => {\n' +
            '        // Generate a unique ID for the new outcome\n' +
            '        let id = common.nonce();',
        outcomeUses:
            '                uses += strat_outcome.uses;',
        commandFallback:
            '            selectedStrat = this.pickStrat(this.originalStrats, matchup, matchup_id);',
        targetingKey:
            '        let start = common.timer();\n' +
            '        let matchup_id = this.findCachedTargetingMatchup(shipString); // check for a cached matchup\n\n' +
            '        // if you didn\'t find a cached matchup, get the matchup_id from the database and cache it\n' +
            '        if (!matchup_id) {\n' +
            '            matchup_id = this.getMatchup(shipString);\n' +
            '            this.addToCachedMatchup(matchup_id, "targeting_matchups", shipString);\n' +
            '        }',
        shootingKey:
            '        let shootingMatchup = this.getShipsFromMatchup(matchup);',
    };
    for (const [name, marker] of Object.entries(markers)) {
        if (!source.includes(marker)) throw new Error(`siServerDev.js ${name} marker changed; update server.js deliberately.`);
    }

    const stratFallbackLoop = 'i < availableStrats.length && !fellBackToBase';
    const shootingFallbackLoop = 'i < availableShootingStrats.length && !fellBackToBase';
    if (source.split(stratFallbackLoop).length - 1 !== 2) {
        throw new Error('siServerDev.js targeting/strategic fallback loops changed; update server.js deliberately.');
    }
    if (source.split(shootingFallbackLoop).length - 1 !== 1) {
        throw new Error('siServerDev.js shooting fallback loop changed; update server.js deliberately.');
    }

    source = source.replace(markers.start,
        '            console.log(`Worker ${process.pid} started`);\n' +
        '            if (!globalThis.__BEES_DEFER_START__) this.start();');
    source = source.replace(markers.messageParse,
        '            let parsedMessage;\n' +
        '            try {\n' +
        '                parsedMessage = JSON.parse(message && message.utf8Data);\n' +
        '            } catch (error) {\n' +
        '                common.handleError(error, "SocketConnection.message");\n' +
        '                return;\n' +
        '            }\n' +
        '            let request = new SocketRequest(parsedMessage, this.connection, this.server, common.timer(), 0, 0, this.id);');
    source = source.replace(markers.userDataRead,
        '                data = await this.user.getData(request.params.DataFile);\n' +
        '            } catch (e) {\n' +
        '                common.handleError(e);\n' +
        '                throw e;\n' +
        '            }\n' +
        '            if (data && data[0]) {');
    source = source.replace(markers.settingsRead,
        '                settings = await this.user.getSettings(request.params.DataFile, request.params.Version);\n' +
        '            } catch (e) {\n' +
        '                common.handleError(e);\n' +
        '                throw e;\n' +
        '            }\n' +
        '            if (settings) {');
    source = source.replace(markers.settingsUserIdCompare,
        '                settings = String(outcomes[0].userId) === String(this.userId) ? outcomes[0] : outcomes[1];');
    source = source.replace(markers.pendingRequestHas,
        '            const pendingKey = `${this.id}:${request.params.Hash}`;\n' +
        '            if (this.server.pendingRequests.has(pendingKey)) {');
    source = source.replace(markers.pendingRequestSet,
        '                this.server.pendingRequests.set(pendingKey, 1);');
    source = source.replace(markers.shootingCacheReset,
        '            // Keep the already-filtered cached list.\n' +
        '            timings.shootingOutcomes = availableShootingStrats.reduce');
    source = source.replace(markers.setupGame,
        '            // One socket may host multiple simultaneous Levels.\n' +
        '            if (!this.game) {\n' +
        '                this.game = new Game(this.db, this.connection, this.server, request.params.LevelId, this.id);\n' +
        '                this.server.games.set(this.id, this.game);\n' +
        '            } else {\n' +
        '                this.game.isActive = true;\n' +
        '            }');
    source = source.replace(markers.reconnectLookup,
        '            const requestedGame = this.server.games.get(request.params.GameId);\n' +
        '            if (requestedGame) {\n' +
        '                this.game = requestedGame;\n' +
        '            }');
    source = source.replace(markers.outcomeId,
        '    insertOutcome = (table, matchup_id, stratID, opponent_id) => {\n' +
        '        // Outcome IDs must be unique for as long as their pending metadata exists.\n' +
        '        let id = common.nonce();\n' +
        '        while (this.pendingInserts.has(id)) {\n' +
        '            id = common.nonce();\n' +
        '        }');
    source = source.replace(markers.outcomeUses,
        '                const outcomeUses = Number(strat_outcome.uses);\n' +
        '                if (!Number.isSafeInteger(outcomeUses) || outcomeUses < 0) {\n' +
        '                    throw new RangeError(`Unsafe outcome uses value: ${strat_outcome.uses}`);\n' +
        '                }\n' +
        '                uses += outcomeUses;');
    source = source.replace(markers.commandFallback,
        '            selectedStrat = this.pickStrat(this.possibleStrats, matchup, matchup_id);');

    source = source.replace(markers.targetingKey,
        '        let start = common.timer();\n' +
        '        const targetingMatchupKey = `target-v2:${shipString}`;\n' +
        '        let matchup_id = this.findCachedTargetingMatchup(targetingMatchupKey); // check for a cached matchup\n\n' +
        '        // if you didn\'t find a cached matchup, get the matchup_id from the database and cache it\n' +
        '        if (!matchup_id) {\n' +
        '            matchup_id = this.getMatchup(targetingMatchupKey);\n' +
        '            this.addToCachedMatchup(matchup_id, "targeting_matchups", targetingMatchupKey);\n' +
        '        }');
    source = source.replace(markers.shootingKey,
        '        let shootingMatchup = `shoot-v2:${this.getShipsFromMatchup(matchup)}`;');

    source = source.replaceAll(stratFallbackLoop, 'i < availableStrats.length');
    source = source.replaceAll(shootingFallbackLoop, 'i < availableShootingStrats.length');

    source = source.replace(markers.startup,
        'module.exports = { Server, SocketConnection, SocketRequest, Game, User, common };');

    const runtimeRequire = request => {
        if (request === 'mysql2' && options.mysqlModule) return options.mysqlModule;
        if (request === 'websocket' && options.websocketModule) return options.websocketModule;
        if (request === 'http' && options.httpModule) return options.httpModule;
        return require(request);
    };
    const sandbox = {
        module: { exports: {} }, exports: {}, require: runtimeRequire,
        __dirname, __filename: sourcePath, console, process, Buffer,
        setTimeout, clearTimeout, setInterval, clearInterval, setImmediate, clearImmediate,
        globalThis: null, __BEES_DEFER_START__: true,
    };
    sandbox.globalThis = sandbox;
    vm.runInNewContext(source, sandbox, { filename: sourcePath });
    return sandbox.module.exports;
}

function databaseFromLegacyConfig(legacyDatabase, options = {}) {
    const config = legacyDatabase.config.connection;
    const isTest = Boolean(options.test);
    const user = options.dbUser || process.env.BEES_DB_USER || (isTest ? 'bees_test' : null);
    const password = options.dbPassword || process.env.BEES_DB_PASSWORD || (isTest ? 'bees_test' : null);
    if (!user || !password) {
        throw new Error('BEES_DB_USER and BEES_DB_PASSWORD are required for production server startup.');
    }
    return new Database(
        options.dbHost || process.env.BEES_DB_HOST || config.host,
        user,
        password,
        options.dbName || process.env.BEES_DB_NAME || config.database,
        options.mysqlModule, isTest);
}

function patchSocketConnection(socketConnection, server) {
    if (!socketConnection || socketConnection[SOCKET_PATCHED]) return socketConnection;
    socketConnection[SOCKET_PATCHED] = true;
    socketConnection[CONNECTION_STATE_TAIL] = Promise.resolve();
    const originalHandleMessage = socketConnection.handleMessage.bind(socketConnection);
    const execute = async request => {
        try {
            if (request?.params?.Type === 'store-user-data' &&
                SHARED_SERVER_DATA_FILES.has(request.params.DataFile)) {
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    Status: 403,
                    Error: 'Shared server data is read-only to game clients.',
                });
                return;
            }
            const result = await originalHandleMessage(request);
            if (socketConnection.game) {
                const game = patchGame(socketConnection.game);
                if (socketConnection.user?.userId !== undefined && socketConnection.user?.userId !== null) {
                    game.userId = String(socketConnection.user.userId);
                }
                patchOutcomeDurability(game, invalidateStrategyCache);
            }
            return result;
        } finally {
            if (request?.params?.Hash !== undefined) {
                server.pendingRequests.delete(pendingRequestKey(socketConnection.id, request.params.Hash));
                server.pendingRequests.delete(request.params.Hash);
            }
        }
    };
    socketConnection.handleMessage = request => {
        if (!SERIALIZED_REQUEST_TYPES.has(request?.params?.Type)) {
            return socketConnection[CONNECTION_STATE_TAIL].then(() => execute(request));
        }
        const operation = socketConnection[CONNECTION_STATE_TAIL].then(() => execute(request));
        socketConnection[CONNECTION_STATE_TAIL] = operation.catch(() => undefined);
        return operation;
    };
    return socketConnection;
}

function patchServer(server, runtime) {
    const common = runtime.common;
    server.loadCacheMaps = () => {
        console.log('Loading cache maps');
        for (const cache of ['cachedMatchups', 'cachedShootingMatchups', 'cachedTargetingMatchups']) {
            const loaded = new Map();
            const cachePath = path.join(server.cacheFolder, `${cache}.json`);
            try {
                if (fs.existsSync(cachePath)) {
                    let text = fs.readFileSync(cachePath, 'utf8');
                    if (text.trim() !== '') {
                        text = `[${text.trimEnd().replace(/,\s*$/, '')}]`;
                        for (const entry of JSON.parse(text)) if (Array.isArray(entry) && entry.length >= 2) loaded.set(entry[0], entry[1]);
                    }
                }
            } catch (error) {
                common.handleError(error, `loadCacheMaps.${cache}`);
            }
            server[cache] = loaded;
        }
    };
    server.handleWSRequest = request => {
        if (!server.originIsAllowed(request.origin)) { request.reject(); return; }
        let id = common.nonce();
        while (server.connections.has(id)) id = common.nonce();
        const connection = new runtime.SocketConnection(request.accept('game', request.origin), server.db, server, id);
        patchSocketConnection(connection, server);
        server.connections.set(id, connection);
    };

    let queueScheduled = false;
    const queuePush = server.queue.push.bind(server.queue);
    const scheduleQueue = () => {
        if (queueScheduled) return;
        queueScheduled = true;
        setImmediate(() => {
            queueScheduled = false;
            server.runQueue();
        });
    };
    server.queue.push = (...requests) => {
        const length = queuePush(...requests);
        if (requests.length > 0) scheduleQueue();
        return length;
    };
    server.runQueue = () => {
        queueScheduled = false;
        const queuedRequests = server.queue.splice(0, server.queue.length);
        for (const request of queuedRequests) {
            const connection = server.connections.get(request.connectionId);
            if (!connection) {
                if (request.params?.Hash !== undefined) {
                    server.pendingRequests.delete(pendingRequestKey(request.connectionId, request.params.Hash));
                    server.pendingRequests.delete(request.params.Hash);
                }
                continue;
            }
            server.recent_requests++;
            request.messageId = server.recent_requests;
            request.timeOnQueue = common.timer() - request.timings.startTime;
            server.totalQueueTime += request.timeOnQueue;
            Promise.resolve(connection.handleMessage(request)).catch(error => common.handleError(error, 'runQueue.handleMessage'));
            server.requestsStarted++;
        }
        if (server.queue.length > 0) scheduleQueue();
    };
    server.consolidateOutcomes = async () => {
        const BATCH_SIZE = 1000;
        const start = common.timer();
        try {
            for (let offset = 0; offset < server.consolidationQueue.length; offset += BATCH_SIZE) {
                const batch = server.consolidationQueue.slice(offset, offset + BATCH_SIZE).map(entry => ({ ...entry, table:
                    entry.table === 0 ? 'targeting_outcomes' : entry.table === 1 ? 'shooting_outcomes' : entry.table === 2 ? 'strategic_commands' : entry.table }));
                let outcomeResults;
                try {
                    outcomeResults = await Promise.all(batch.map(({ matchup_id, table, game }) => patchGame(game).getOutcomesFromId(matchup_id, table)));
                } catch (error) {
                    common.handleError(error, 'consolidateOutcomes.read');
                    continue;
                }
                const grouped = {};
                for (let index = 0; index < batch.length; index++) {
                    const { matchup_id, table, game } = batch[index];
                    const outcomes = outcomeResults[index];
                    if (!outcomes || outcomes.length <= game.config.maxOutcomesPerStratType) continue;
                    const values = [];
                    for (const strategyId of [...new Set(outcomes.map(outcome => outcome.strategy_id))]) {
                        const aggregate = game.addOutcomes(outcomes, strategyId);
                        if (!aggregate || !Number(aggregate.uses)) continue;
                        values.push([matchup_id, strategyId, Math.round(aggregate.tsv / aggregate.uses), aggregate.uses]);
                    }
                    if (!grouped[table]) grouped[table] = [];
                    grouped[table].push({ matchup_id, values });
                }
                try {
                    await server.db.transaction(async query => {
                        for (const [table, entries] of Object.entries(grouped)) {
                            const matchupIds = entries.map(entry => entry.matchup_id);
                            const deleted = await query(`DELETE FROM ${table} WHERE matchup_id IN (?)`, [matchupIds]);
                            server.totalConsolidatedRows += deleted.affectedRows || 0;
                            const values = entries.flatMap(entry => entry.values);
                            if (values.length) {
                                const inserted = await query(`INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome, uses) VALUES ?`, [values]);
                                server.totalConsolidatedInsertRows += inserted.affectedRows || 0;
                            }
                        }
                    });
                } catch (error) {
                    common.handleError(error, 'consolidateOutcomes.write');
                }
            }
        } catch (error) {
            common.handleError(error, 'consolidateOutcomes.unexpected');
        } finally {
            console.log(`Consolidated ${server.totalConsolidatedRows} rows and inserted ${server.totalConsolidatedInsertRows} rows in ${(common.timer() - start).toFixed(2)}ms`);
            server.totalConsolidatedRows = 0;
            server.totalConsolidatedInsertRows = 0;
            server.consolidationQueue = [];
        }
    };
    return server;
}

function applyTestIsolation(server, launch) {
    const requireTestDatabase = launch.requireTestDb === true || process.env.BEES_REQUIRE_TEST_DB === '1';
    if (requireTestDatabase && !/test/i.test(server.db.config.connection.database)) throw new Error(`Refusing test server database '${server.db.config.connection.database}'. BEES_REQUIRE_TEST_DB requires a database name containing "test".`);
    if (launch.disableBackgroundJobs === true || process.env.BEES_DISABLE_BACKGROUND_JOBS === '1') {
        server.loadCacheMaps = () => {};
        server.runCacheClean = () => {};
        server.runConsolidationQueue = () => {};
    }
}

function parseLaunchOptions(argv = process.argv) {
    let test = false, port = 7143;
    for (const argument of argv) {
        if (String(argument).toLowerCase() === 'test') test = true;
        if (!Number.isNaN(Number(argument)) && String(argument).trim() !== '') port = Number.parseInt(argument, 10);
    }
    return { test, port };
}

function createServer(options = {}) {
    const runtime = loadLegacyRuntime(options);
    installCampaignCheckpoint(runtime);
    const launch = { ...parseLaunchOptions(options.argv || process.argv), ...options };
    const server = new runtime.Server(Boolean(launch.test), launch.port || 7143);
    server.db = databaseFromLegacyConfig(server.db, launch);
    patchServer(server, runtime);
    applyTestIsolation(server, launch);
    if (launch.start !== false) server.start();
    return { server, runtime };
}

if (require.main === module) createServer();
module.exports = {
    createServer,
    loadLegacyRuntime,
    patchGame,
    patchSocketConnection,
    patchServer,
    applyTestIsolation,
    pendingRequestKey,
};
