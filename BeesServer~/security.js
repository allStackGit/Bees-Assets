'use strict';

const fs = require('node:fs');
const https = require('node:https');
const { AsyncLocalStorage } = require('node:async_hooks');
const WebSocketServer = require('websocket').server;
const { invalidateStrategyCache } = require('./gamePersistence');
const { patchOutcomeDurability } = require('./outcomeReservations');

const USER_DATA_TYPES = new Set(['get-user-data', 'store-user-data']);
const SHARED_READ_ONLY_FILES = new Set(['campaign_levels_data', 'challenge_levels_data']);
const MAX_WEBSOCKET_MESSAGE_BYTES = 8 * 1024 * 1024;
const AUTHENTICATION_IDLE_TIMEOUT_MS = 30 * 1000;
const CONSOLIDATION_RETRY_MS = 10 * 1000;

function sendResponse(request, status, extra = {}) {
    request.respond({ Type: request.params.Type, Hash: request.params.Hash, Status: status, ...extra });
}

function closeForConsolidation(connection) {
    if (typeof connection?.close === 'function') connection.close(1013, 'Server maintenance');
    else if (typeof connection?.drop === 'function') connection.drop(1013, 'Server maintenance');
}

function beginOutcomeWrite(server) {
    server.__beesInFlightOutcomeWrites = Number(server.__beesInFlightOutcomeWrites || 0) + 1;
}

function endOutcomeWrite(server) {
    server.__beesInFlightOutcomeWrites = Math.max(0, Number(server.__beesInFlightOutcomeWrites || 0) - 1);
}

function installInFlightWriteConsolidationGuard(server, schedule = setTimeout) {
    if (!server || server.__beesInFlightWriteConsolidationGuardInstalled) return server;
    server.__beesInFlightWriteConsolidationGuardInstalled = true;
    server.__beesInFlightOutcomeWrites = Number(server.__beesInFlightOutcomeWrites || 0);

    let consolidationRunner = server.runConsolidationQueue;
    const guardedRunner = async (...args) => {
        if (server.__beesInFlightOutcomeWrites > 0) {
            schedule(guardedRunner, CONSOLIDATION_RETRY_MS);
            return;
        }
        return consolidationRunner(...args);
    };

    Object.defineProperty(server, 'runConsolidationQueue', {
        configurable: true,
        get: () => guardedRunner,
        set: value => {
            if (typeof value !== 'function') throw new TypeError('runConsolidationQueue must be a function.');
            consolidationRunner = value;
        },
    });
    return server;
}

function authenticateSteamTicket(ticket, claimedUserId, options = {}) {
    const apiKey = options.apiKey || process.env.BEES_STEAM_WEB_API_KEY;
    const appId = options.appId || process.env.BEES_STEAM_APP_ID;
    const identity = options.identity || process.env.BEES_STEAM_AUTH_IDENTITY || 'bees-server';
    if (!apiKey || !appId) return Promise.reject(new Error('Production Steam authentication requires BEES_STEAM_WEB_API_KEY and BEES_STEAM_APP_ID.'));
    if (!ticket || !/^[0-9a-f]+$/i.test(ticket)) return Promise.reject(new Error('Missing or malformed Steam Web API authentication ticket.'));

    const url = new URL('https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/');
    url.searchParams.set('key', apiKey);
    url.searchParams.set('appid', String(appId));
    url.searchParams.set('ticket', ticket);
    url.searchParams.set('identity', identity);
    return new Promise((resolve, reject) => {
        const request = https.get(url, response => {
            let body = '';
            response.setEncoding('utf8');
            response.on('data', chunk => { body += chunk; });
            response.on('end', () => {
                if (response.statusCode !== 200) return reject(new Error(`Steam authentication returned HTTP ${response.statusCode}.`));
                try {
                    const steamId = JSON.parse(body)?.response?.params?.steamid;
                    if (!steamId || String(steamId) !== String(claimedUserId)) return reject(new Error('Steam ticket identity did not match the claimed user ID.'));
                    resolve(String(steamId));
                } catch (error) { reject(error); }
            });
        });
        request.setTimeout(10000, () => request.destroy(new Error('Steam authentication timed out.')));
        request.on('error', reject);
    });
}

function partitionStoredCommands(params, pendingInserts) {
    const staleOutcomeIds = [];
    const filter = items => (items || []).filter(item => {
        if (item?.DiscardReservation === true) return true;
        const id = Number(item?.OutcomeId);
        if (Number.isFinite(id) && pendingInserts?.has(id)) return true;
        if (Number.isFinite(id) && id > 0) staleOutcomeIds.push(id);
        return false;
    });
    return { commands: filter(params.Commands), shootingCommands: filter(params.ShootingCommands), targetingCommands: filter(params.TargetingCommands), staleOutcomeIds };
}

async function consolidateOutcomesSafely(server, common) {
    const work = server.consolidationQueue;
    const failed = [];
    server.consolidationQueue = [];
    const start = common.timer();
    for (let offset = 0; offset < work.length; offset += 1000) {
        const originalBatch = work.slice(offset, offset + 1000);
        const batch = originalBatch.map(entry => ({ ...entry,
            table: entry.table === 0 ? 'targeting_outcomes' : entry.table === 1 ? 'shooting_outcomes' : entry.table === 2 ? 'strategic_commands' : entry.table }));
        try {
            const outcomeResults = await Promise.all(batch.map(({ matchup_id, table, game }) => game.getOutcomesFromId(matchup_id, table)));
            const grouped = {};
            for (let index = 0; index < batch.length; index++) {
                const { matchup_id, table, game } = batch[index];
                const outcomes = outcomeResults[index];
                if (!outcomes || outcomes.length <= game.config.maxOutcomesPerStratType) continue;
                const values = [];
                for (const strategyId of [...new Set(outcomes.map(outcome => outcome.strategy_id))]) {
                    const aggregate = game.addOutcomes(outcomes, strategyId);
                    if (aggregate && Number(aggregate.uses)) values.push([matchup_id, strategyId, Math.round(aggregate.tsv / aggregate.uses), aggregate.uses]);
                }
                if (!grouped[table]) grouped[table] = [];
                grouped[table].push({ matchup_id, values });
            }
            await server.db.transaction(async query => {
                for (const [table, entries] of Object.entries(grouped)) {
                    const matchupIds = entries.map(entry => entry.matchup_id);
                    if (!matchupIds.length) continue;
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
            common.handleError(error, 'consolidateOutcomes');
            failed.push(...originalBatch);
        }
    }
    server.consolidationQueue.push(...failed);
    console.log(`Consolidated ${server.totalConsolidatedRows} rows and inserted ${server.totalConsolidatedInsertRows} rows in ${(common.timer() - start).toFixed(2)}ms; ${failed.length} entries retained for retry.`);
    server.totalConsolidatedRows = 0;
    server.totalConsolidatedInsertRows = 0;
    return { hadFailure: failed.length > 0, failedCount: failed.length, processedCount: work.length - failed.length };
}

function installRuntimeSecurity(runtime, options = {}) {
    if (!runtime?.SocketConnection || runtime.__beesSecurityInstalled) return runtime;
    runtime.__beesSecurityInstalled = true;
    const shootingMatchupContext = new AsyncLocalStorage();
    const authenticateTicket = options.authenticateSteamTicket || authenticateSteamTicket;

    if (runtime.Game?.prototype && !Object.getOwnPropertyDescriptor(runtime.Game.prototype, 'originalStrats')) {
        Object.defineProperty(runtime.Game.prototype, 'originalStrats', {
            configurable: true,
            get() { return this.__beesOriginalStrats || this.possibleStrats; },
            set(value) { this.__beesOriginalStrats = value; },
        });
    }

    if (runtime.Game?.prototype && !runtime.Game.prototype.__beesBigIntUsesNormalized) {
        const legacyAddOutcomes = runtime.Game.prototype.addOutcomes;
        runtime.Game.prototype.addOutcomes = function addOutcomesWithNumericUses(outcomes, strategyId) {
            const normalized = (outcomes || []).map(outcome => {
                const uses = Number(outcome.uses);
                if (!Number.isSafeInteger(uses) || uses < 0) throw new RangeError(`Unsafe outcome uses value: ${outcome.uses}`);
                return { ...outcome, uses };
            });
            return legacyAddOutcomes.call(this, normalized, strategyId);
        };
        Object.defineProperty(runtime.Game.prototype, '__beesBigIntUsesNormalized', { value: true });
    }

    if (runtime.Game?.prototype && !runtime.Game.prototype.__beesShootingIdentityPatched) {
        const legacyGetShipsFromMatchup = runtime.Game.prototype.getShipsFromMatchup;
        runtime.Game.prototype.getShipsFromMatchup = function getShipsFromMatchupWithRequestIdentity(matchup) {
            const requestIdentity = shootingMatchupContext.getStore();
            if (typeof requestIdentity === 'string' && requestIdentity.length > 0) return requestIdentity;
            return legacyGetShipsFromMatchup.call(this, matchup);
        };
        Object.defineProperty(runtime.Game.prototype, '__beesShootingIdentityPatched', { value: true });
    }

    const LegacySocketConnection = runtime.SocketConnection;
    class HardenedSocketConnection extends LegacySocketConnection {
        constructor(connection, db, server, id) {
            super(connection, db, server, id);

            this.__beesAuthenticationIdleTimer = null;
            this.__beesAuthenticationPromise = null;
            if (!server.test) {
                this.__beesAuthenticationIdleTimer = setTimeout(() => {
                    if (this.authenticatedUserId) return;
                    console.warn(`Closing unauthenticated idle WebSocket connection ${id}.`);
                    if (typeof connection.close === 'function') connection.close(1008, 'Authentication timeout');
                    else if (typeof connection.drop === 'function') connection.drop(1008, 'Authentication timeout');
                }, AUTHENTICATION_IDLE_TIMEOUT_MS);
                if (typeof connection.on === 'function') {
                    connection.on('close', () => {
                        if (this.__beesAuthenticationIdleTimer) clearTimeout(this.__beesAuthenticationIdleTimer);
                        this.__beesAuthenticationIdleTimer = null;
                    });
                }
            }

            if (typeof connection.removeAllListeners === 'function') connection.removeAllListeners('message');
            if (typeof connection.on === 'function') connection.on('message', message => {
                try {
                    if (!message ||
                        (message.type !== undefined && message.type !== 'utf8') ||
                        typeof message.utf8Data !== 'string') {
                        return console.warn(`Rejected non-UTF8 WebSocket frame on connection ${id}.`);
                    }
                    const parsed = JSON.parse(message.utf8Data);
                    if (!parsed || typeof parsed !== 'object') throw new TypeError('Request JSON must be an object.');
                    const request = new runtime.SocketRequest(parsed, connection, server, runtime.common.timer(), 0, 0, id);
                    const pendingKey = `${id}:${request.params.Hash}`;
                    if (server.pendingRequests.has(pendingKey)) return;
                    server.pendingRequests.set(pendingKey, 1);
                    server.queue.push(request);
                } catch (error) { runtime.common.handleError(error, 'SocketConnection.message'); }
            });

            const legacyHandleMessage = this.handleMessage.bind(this);
            this.handleMessage = async request => {
                const insecureAllowed = Boolean(server.test);
                const claimedUserId = request?.params?.UserId !== undefined ? String(request.params.UserId) : null;
                if (!insecureAllowed && server.isRunningConsolidation) {
                    closeForConsolidation(connection);
                    return false;
                }
                if (!insecureAllowed && !this.authenticatedUserId) {
                    try {
                        if (!this.__beesAuthenticationPromise) {
                            const authentication = Promise.resolve(
                                authenticateTicket(request?.params?.AuthTicket, claimedUserId)
                            ).then(userId => {
                                this.authenticatedUserId = String(userId);
                                return this.authenticatedUserId;
                            });
                            this.__beesAuthenticationPromise = authentication;
                            authentication.then(
                                () => {
                                    if (this.__beesAuthenticationPromise === authentication) {
                                        this.__beesAuthenticationPromise = null;
                                    }
                                },
                                () => {
                                    if (this.__beesAuthenticationPromise === authentication) {
                                        this.__beesAuthenticationPromise = null;
                                    }
                                },
                            );
                        }
                        await this.__beesAuthenticationPromise;
                        if (this.__beesAuthenticationIdleTimer) clearTimeout(this.__beesAuthenticationIdleTimer);
                        this.__beesAuthenticationIdleTimer = null;
                    } catch (error) {
                        runtime.common.handleError(error, 'Steam authentication');
                        sendResponse(request, 401);
                        return false;
                    }
                    if (server.isRunningConsolidation) {
                        closeForConsolidation(connection);
                        return false;
                    }
                }
                if (!insecureAllowed && claimedUserId && claimedUserId !== String(this.authenticatedUserId)) { sendResponse(request, 403); return false; }
                if (request.params.Type === 'store-user-data' && SHARED_READ_ONLY_FILES.has(request.params.DataFile)) { sendResponse(request, 403); return false; }

                if (this.game) {
                    if (this.authenticatedUserId) this.game.userId = String(this.authenticatedUserId);
                    patchOutcomeDurability(this.game, invalidateStrategyCache);
                }

                if (!insecureAllowed && request.params.Type === 'reconnect-level') {
                    const requestedGame = server.games.get(request.params.GameId);
                    if (requestedGame?.__beesOwnerUserId && requestedGame.__beesOwnerUserId !== this.authenticatedUserId) {
                        sendResponse(request, 403);
                        return false;
                    }
                }

                if (request.params.Type === 'store-commands' && this.game) {
                    beginOutcomeWrite(server);
                    try {
                        await this.game.storeState(request.params.Commands, request.params.ShootingCommands, request.params.TargetingCommands);
                        sendResponse(request, 200);
                    } catch (error) {
                        runtime.common.handleError(error, 'store-commands');
                        const status = Number.isInteger(error?.statusCode) ? error.statusCode : 500;
                        const extra = Array.isArray(error?.staleOutcomeIds) ? { StaleOutcomeIds: error.staleOutcomeIds } : {};
                        sendResponse(request, status, extra);
                    } finally {
                        endOutcomeWrite(server);
                    }
                    return true;
                }

                const invoke = async () => {
                    const invokeLegacy = () => legacyHandleMessage(request);
                    const shootingMatchup = request?.params?.Type === 'get-strategy'
                        ? request.params.ShootingMatchup
                        : null;
                    const result = typeof shootingMatchup === 'string' && shootingMatchup.length > 0
                        ? await shootingMatchupContext.run(shootingMatchup, invokeLegacy)
                        : await invokeLegacy();
                    if (!insecureAllowed && this.game && (request.params.Type === 'setup-level' || request.params.Type === 'reconnect-level')) {
                        if (!this.game.__beesOwnerUserId) this.game.__beesOwnerUserId = this.authenticatedUserId;
                        this.game.userId = String(this.authenticatedUserId);
                    }
                    return result;
                };
                if (!USER_DATA_TYPES.has(request.params.Type)) return invoke();

                const userKey = String(this.authenticatedUserId || claimedUserId || this.user_id || id);
                if (!server.__beesUserDataTails) server.__beesUserDataTails = new Map();
                const previous = server.__beesUserDataTails.get(userKey) || Promise.resolve();
                const operation = previous.catch(() => undefined).then(invoke);

                let tail;
                tail = operation.then(
                    () => undefined,
                    () => undefined,
                ).finally(() => {
                    if (server.__beesUserDataTails.get(userKey) === tail) server.__beesUserDataTails.delete(userKey);
                });
                server.__beesUserDataTails.set(userKey, tail);
                return operation;
            };
        }
    }
    runtime.SocketConnection = HardenedSocketConnection;

    if (runtime.Server) {
        const LegacyServer = runtime.Server;
        class HardenedServer extends LegacyServer {
            constructor(...args) {
                super(...args);
                installInFlightWriteConsolidationGuard(this);

                for (const cacheName of ['cachedStrategies', 'cachedTargetingStrategies', 'cachedShootingStrategies']) {
                    this[cacheName] = new Map(this[cacheName] || []);
                }

                const legacyStart = this.start.bind(this);
                this.start = () => {
                    if (this.test) return legacyStart();
                    const keyPath = process.env.BEES_TLS_KEY_PATH;
                    const certPath = process.env.BEES_TLS_CERT_PATH;
                    if (!keyPath || !certPath) throw new Error('Production BeesServer requires BEES_TLS_KEY_PATH and BEES_TLS_CERT_PATH.');
                    this.db.handleDisconnect();
                    const httpServer = https.createServer(
                        { key: fs.readFileSync(keyPath), cert: fs.readFileSync(certPath) },
                        (_request, response) => {
                            const body = 'WebSocket upgrade required.\n';
                            response.writeHead(426, {
                                'Content-Type': 'text/plain; charset=utf-8',
                                'Content-Length': Buffer.byteLength(body),
                                Connection: 'close',
                            });
                            response.end(body);
                        }).listen(this.port);
                    const wsServer = new WebSocketServer({
                        httpServer,
                        maxReceivedFrameSize: MAX_WEBSOCKET_MESSAGE_BYTES,
                        maxReceivedMessageSize: MAX_WEBSOCKET_MESSAGE_BYTES,
                        autoAcceptConnections: false,
                    });
                    wsServer.on('request', request => {
                        if (this.isRunningConsolidation || this.consolidationQueue.length > 0) {
                            request.reject();
                            return;
                        }
                        this.handleWSRequest(request);
                    });
                    setTimeout(() => {
                        this.loadCacheMaps();
                        this.saveCacheMaps();
                        this.runQueues();
                        this.runCacheClean();
                    }, 1000);
                    setInterval(this.removeOldGames, 600000);
                };
                let consolidator = this.consolidateOutcomes;
                Object.defineProperty(this, 'consolidateOutcomes', {
                    configurable: true,
                    get: () => consolidator,
                    set: value => {
                        if (typeof value !== 'function') throw new TypeError('consolidateOutcomes must be a function.');
                        consolidator = value;
                    },
                });
            }
        }
        runtime.Server = HardenedServer;
    }
    return runtime;
}

module.exports = {
    USER_DATA_TYPES,
    SHARED_READ_ONLY_FILES,
    MAX_WEBSOCKET_MESSAGE_BYTES,
    AUTHENTICATION_IDLE_TIMEOUT_MS,
    CONSOLIDATION_RETRY_MS,
    beginOutcomeWrite,
    endOutcomeWrite,
    installInFlightWriteConsolidationGuard,
    authenticateSteamTicket,
    partitionStoredCommands,
    consolidateOutcomesSafely,
    installRuntimeSecurity,
};