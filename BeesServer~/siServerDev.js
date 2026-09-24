const mysql = require('mysql2');
const http = require("http");    
const socket = require('websocket').server;
const cluster = require('cluster');
const { performance } = require('perf_hooks');
const fs = require('fs');
const xxh = require('xxhashjs');
const { pruneExpiredEntries } = require('./serverContracts');
/**
 * Common class that contains various utility functions and properties
 */
class Common {
    constructor() {
        this.pastNonces = new Map(); // All the nonces that have been previously generated

        // Returns a random number
        this.nonce = () => {
            return this.randomInt();
        };

        // Generates a random integer
        this.randomInt = () => {
            return Math.floor(Math.random() * 100000000000) + 1; // 100 billion
        }

        // Generates a unique nonce
        this.uniqueNonce = () => {
            let nonce = this.randomInt();
            while (this.pastNonces.has(nonce)) {
                nonce = this.randomInt();
            }
            this.pastNonces.set(nonce, true);
            return nonce;
        };

        // Deterministic function: input string to unsigned 64-bit integer string
        this.stringToInt64 = (input) => {
            // Use a constant seed; change only if you want a different hash space
            return xxh.h64(input, 0xABCD1234).toString(); // returns string representation of a 64-bit integer
        }
    }

    /**
     * Converts a Map to an Object
     * @param {Map} map - The map to convert.
     * @returns {Object} - The resulting object.
     */
    mapToObject(map) {
        var obj = {};
        map.forEach(function (v, k) {
            obj[k] = v;
        });
        return obj;
    }

    /**
     * Parses cookies from a cookie string
     * @param {string} cookie - The cookie string.
     * @returns {Object} - The parsed cookies as an object.
     */
    parseCookies(cookie) {
        let rx = /([^;=\s]*)=([^;]*)/g;
        let obj = {};
        for (let m; m = rx.exec(cookie);) {
            obj[m[1]] = decodeURIComponent(m[2]);
        }
        return obj;
    };

    /**
     * Gets the local time in "America/New_York" timezone
     * @returns {string} - The local time as a string.
     */
    getLocalTime() {
        return new Date().toLocaleString("en-US", { timeZone: "America/New_York" });
    };

    /**
     * Handles errors and logs them
     * @param {Error} error - The error object.
     * @param {string} [source="Unknown source"] - The source of the error.
     */
    handleError = (error, source = "Unknown source") => {
        console.log(`There was an error thrown by ${source}:\n`);
        console.error(error);
    };

    /**
     * Returns the current timestamp
     * @returns {number} - The current timestamp.
     */
    timer = () => {
        //return Date.now();
        return performance.now();
    };

    /**
     * Calculates the average of two numbers
     * @param {number} a - The first number.
     * @param {number} b - The second number.
     * @returns {number} - The average of the two numbers.
     */
    calculateAverage(a, b) {
        return Math.round(((a / b) * 100)) / 100;
    }

    /**
     * Calculates the percentage of two numbers
     * @param {number} a - The first number.
     * @param {number} b - The second number.
     * @returns {number} - The percentage of the two numbers.
     */
    calculatePercentage(a, b) {
        return Math.round(((a / b) * 10000)) / 100;
    }
}
/*
A class for handling all connections to the database
 */
class Database {
    constructor(host, user, password, database) {
        this.config = {
            connection: {
                host: host,
                user: user,
                password: password,
                database: database
            }
        };
        this.connection;
    }

    handleDisconnect = () => {
        console.log("Handling database connection/disconnection");
        this.connection = mysql.createPool({
            host     : this.config.connection.host,
            user     : this.config.connection.user,
            password : this.config.connection.password,
            database: this.config.connection.database,
            connectionLimit: 128,
            multipleStatements: true,
            charset: "utf8mb4",
            //debug: true
        }); // Recreate the connection, since
        // the old one cannot be reused.

        //this.connection.connect((err) => {              // The server is either down
        //    if(err) {                                     // or restarting (takes a while sometimes).
        //        console.log('error when connecting to db:', err);
        //        console.log("Connection issue, trying to handle.");
        //        setTimeout(this.handleDisconnect, 2000); // We introduce a delay before attempting to reconnect,
        //    }                                     // to avoid a hot loop, and to allow our node script to
        //});                                     // process asynchronous requests in the meantime.
                                                // If you're also serving http, display a 503 error.
        this.connection.on('error', (err) => {
            console.log('db error', err);
            if(err.code === 'PROTOCOL_CONNECTION_LOST') { // Connection to the MySQL server is usually
                console.log("Connection lost. Trying to handle.");
                this.handleDisconnect();                         // lost due to either server restart, or a connnection idle timeout (the wait_timeout server variable configures this)
            } else if (err.code === 4031) {
                console.log("Connection lost due to inactivity. Trying to handle.");
                this.handleDisconnect();                         // lost due to either server restart, or a connnection idle timeout (the wait_timeout server variable configures this)
            }else {
                console.log("Connection lost. Trying to handle.");
                console.error(`DB Error:`, err);
            }
        });
    }

    //query = (sql, values) => {
    //    return new Promise((resolve, reject) => {
    //        if (mysql){
    //            this.connection.query(sql, values, (err, result) => {
    //                return err ? reject({error: err}) : resolve(result);
    //            });
    //        }
    //    });

    //};

    query = (sql, values) => {
        return new Promise((resolve, reject) => {
            this.connection.getConnection((err, connection) => {
                if (err) {
                    return reject(err);
                }
                connection.query(sql, values, (error, results) => {
                    connection.release();  // important!
                    return error ? reject(error) : resolve(results);
                });
            });
        });
    };

}


/*
A class for handling individual requests and metadata (params, responses, etc)
 */
class SocketRequest {
    /**
     * Creates an instance of SocketRequest.
     * @param {Object} params - The parameters of the request.
     * @param {WebSocket} ws - The WebSocket connection.
     * @param {Object} server - The server instance.
     * @param {number} startTime - The start time of the request.
     * @param {number} timeOnQueue - The time spent on the queue.
     * @param {string} messageId - The message ID.
     */
    constructor(params, ws, server, startTime, timeOnQueue, messageId, connectionId) {
        this.params = params;
        this.ws = ws;
        this.server = server;
        this.connectionId = connectionId;
        this.timings = {
            type: "",
            matchup_id: "",
            cacheHit: false,
            hasSufficientUses: false,
            hasSufficientShootingUses: false,
            outcomes: -1,
            shootingOutcomes: -1,
            uses: -1,
            shootingUses: -1,
            startTime: startTime,
            timeOnQueue: timeOnQueue,
            dbTime: 0,
            getTargetingMatchup: 0,
            getTargetingOutcomesFromId: 0,
            getStrategicMatchup: 0,
            getStrategicOutcomesFromId: 0,
            getShootingMatchup: 0,
            getShootingOutcomesFromId: 0,
            getRelatedOutcomes: 0,
            getCachedStrategies: 0,
            insertTargetingOutcome: 0,
            insertShootingOutcomes: 0,
            insertStrategicOutcome: 0,
        };
        this.messageId = messageId;
    }

    /**
      * Logs statistics about the request.
      * @param {number} startLogTime - The start time of the logging.
      * @param {number} totalTime - The total time taken for the request.
      */
    logStats = (startLogTime, totalTime) => {
        /**
         * We drop the top uses and outcomes so that the average number of uses for command is more accurate
         * This is because some commands (Mainly just one) have a lot of outcomes and uses, and they skew the average
         * 
         * We can keep a rolling array of the top 20 commands with the most outcomes and uses and if a command
         * matches one of those we don't add it to the total outcomes and uses
         * 
         * It should also not get counted for the found/unFound strats so it doesn't influence that metric or the cache hits
         */
        let totalStrategiesRequested = this.server.foundStrats + this.server.unFoundStrats;
        let totalShootingStrategiesRequested = this.server.foundShootingStrats + this.server.unFoundShootingStrats;

        let average = common.calculateAverage(this.server.totalTime, this.server.recent_requests);
        let queueAverage = common.calculateAverage(this.server.totalQueueTime, this.server.recent_requests);
        let dbAverage = common.calculateAverage(this.server.totalDbTime, this.server.recent_requests);
        let outcomesAverage = common.calculateAverage(this.server.totalOutcomes, this.server.outcomeRequests);
        let shootingOutcomesAverage = common.calculateAverage(this.server.totalShootingOutcomes, this.server.shootingOutcomeRequests);
        let usesAverage = common.calculateAverage(this.server.totalUses, this.server.outcomeRequests);
        let shootingUsesAverage = common.calculateAverage(this.server.totalShootingUses, this.server.shootingOutcomeRequests);
        let averageCachedStrategyTime = common.calculateAverage(this.server.totalCacheTime, this.server.totalMatchupOrStrategyRequests);
        let averageCacheFilterTime = common.calculateAverage(this.server.totalCacheFilterTime, this.server.cacheFilters);
        let averageCachedDBTime = common.calculateAverage(this.server.totalCacheTime, this.server.totalCacheHits);
        let averageUncachedDBTime = common.calculateAverage(this.server.totalUncachedTime, this.server.totalCacheMisses);
        let averageConsolidationTime = common.calculateAverage(this.server.consolidationTime, this.server.consolidationCount);

        let percentageUnfound = common.calculatePercentage(this.server.unFoundStrats, totalStrategiesRequested);
        let percentageUnfoundShooting = common.calculatePercentage(this.server.unFoundShootingStrats, totalShootingStrategiesRequested);
        let percentageFilled = common.calculatePercentage(this.server.filledStrategies, totalStrategiesRequested);
        let percentageShootingFilled = common.calculatePercentage(this.server.filledShootingStrategies, totalShootingStrategiesRequested);
        let percentageOfSlowRequests = common.calculatePercentage((this.server.extraLongRequests + this.server.longRequests + this.server.mediumRequests), this.server.recent_requests);

        let filledStrategies = 0;
        let filledShootingStrategies = 0;

        this.server.uniqueCommandMatchups.forEach((value, key, map) => {
            if (value) {
                filledStrategies++;
            }
        });

        this.server.uniqueShootingMatchups.forEach((value, key, map) => {
            if (value) {
                filledShootingStrategies++;
            }
        });
        // This is the number of unique matchups that have been filled with strategies, if a matchup is used several times, it increased the filled for stats above (percentageFilled) but not for this
        let percentageMatchupsFilled = common.calculatePercentage(filledStrategies, this.server.uniqueCommandMatchups.size);
        let percentageShootingMatchupsFilled = common.calculatePercentage(filledShootingStrategies, this.server.uniqueShootingMatchups.size);

        let pendingInsertsUpdates = this.server.countPending();
        let lengths = {
            connections: this.server.connections.size,
            queue: this.server.queue.length,
            consolidationMap: this.server.consolidationMap.size,
            pendingRequests: this.server.pendingRequests.size,
            cachedStrategies: this.server.cachedStrategies.size,
            cachedTargetingStrategies: this.server.cachedTargetingStrategies.size,
            cachedShootingStrategies: this.server.cachedShootingStrategies.size,
            cachedMatchups: this.server.cachedMatchups.size,
            cachedTargetingMatchups: this.server.cachedTargetingMatchups.size,
            cachedShootingMatchups: this.server.cachedShootingMatchups.size,
            games: this.server.games.size,
            pendingInserts: pendingInsertsUpdates.inserts,
            pendingUpdates: pendingInsertsUpdates.updates,
            cachedMatchupsRecent: this.server.cachedMatchupsRecent.length,
            cachedTargetingMatchupsRecent: this.server.cachedTargetingMatchupsRecent.length,
            cachedShootingMatchupsRecent: this.server.cachedShootingMatchupsRecent.length
        };

        let averages = {
            insertAverage: common.calculateAverage(this.server.totalInsertTime, this.server.totalMatchupOrStrategyRequests),
            selectAverage: common.calculateAverage(this.server.totalSelectTime, this.server.totalMatchupOrStrategyRequests),
        };

        let timeInSeconds = (startLogTime - this.server.startTime) / 1000;

        let targetingCacheHitsPercentage = common.calculatePercentage(this.server.targetingCacheHits, this.server.targetingStrategyRequests);
        let shootingCacheHitsPercentage = common.calculatePercentage(this.server.shootingCacheHits, this.server.serverStrategiesServed);
        let strategyCacheHitsPercentage = common.calculatePercentage(this.server.strategyCacheHits, this.server.serverStrategiesServed);
        let averageWriteTime = common.calculateAverage(this.server.totalWriteTime, this.server.writesCount);
        let strategiesPerSecond = common.calculateAverage(this.server.serverStrategiesServed, timeInSeconds);
        let requestsPerSecond = common.calculateAverage(this.server.recent_requests, timeInSeconds);

        console.log(`
        Matchups with strats: ${this.server.foundStrats}, matchups with no strats: ${this.server.unFoundStrats} ${percentageUnfound}% unfound [${strategiesPerSecond}/s]
        Shooting Matchups with strats: ${this.server.foundShootingStrats}, matchups with no strats: ${this.server.unFoundShootingStrats} ${percentageUnfoundShooting}% unfound

        Command Requests with filled strats: ${this.server.filledStrategies}, ${percentageFilled}% filled
        Shooting Requests with filled strats: ${this.server.filledShootingStrategies}, ${percentageShootingFilled}% filled

        Matchups with filled strats: ${filledStrategies}, ${percentageMatchupsFilled}% filled
        Shooting matchups with filled strats: ${filledShootingStrategies}, ${percentageShootingMatchupsFilled}% filled

        Request #${this.messageId} (${this.params.Hash}) finished in ${totalTime}ms with ${this.server.pendingRequests.size} pending requests left and ${this.server.queue.length} requests left on the queue. 
        There was ${this.timings.timeOnQueue}ms spent on the queue, and ${this.timings.dbTime}ms waiting for the db. 
        The server average is ${average}ms total, ${queueAverage}ms queue time, and ${dbAverage}ms db time with ${averageCachedStrategyTime}ms average cache time
        Average cached hit db time: ${averageCachedDBTime} ms Uncached: ${averageUncachedDBTime} ms 
        for ${this.server.recent_requests} requests. (${requestsPerSecond}/s)  with ${this.server.extraLongRequests} XL and ${this.server.longRequests} L and ${this.server.mediumRequests} M ${percentageOfSlowRequests}% 

        There is an average of ${outcomesAverage} outcomes and ${usesAverage} uses (across all strategy options) for each strategy request
        There is an average of ${shootingOutcomesAverage} outcomes and ${shootingUsesAverage} uses (across all shooting strategy options) for each shooting strategy request

        There are ${this.server.topUsesHits} top uses hits and ${this.server.topShootingUsesHits} top shooting uses hits

        There are ${this.server.targetingCacheHits} / ${targetingCacheHitsPercentage}% targeting strategy cache hits, ${this.server.shootingCacheHits} / ${shootingCacheHitsPercentage}% shooting strategy cache hits, 
        and ${this.server.strategyCacheHits} / ${strategyCacheHitsPercentage}% strategy cache hits and ${this.server.cachedTargetingStrategies.size}, ${this.server.cachedShootingStrategies.size}, ${this.server.cachedStrategies.size} cache size
        and ${this.server.cachedTargetingMatchups.size}, ${this.server.cachedShootingMatchups.size}, ${this.server.cachedMatchups.size} matchup cache size
        Average strategy cache filter time: ${averageCacheFilterTime} ms. The average write time is ${averageWriteTime} ms. The average consolidation time is ${averageConsolidationTime} ms.
        There are ${this.server.consolidationMap.size} pending consolidations left.
        
        ---------------------------------------------------------------------------------------------------`, averages, this.timings, lengths, `Logging took ${common.timer() - startLogTime}ms`);

        console.log(`Top Uses:`, Array.from(this.server.topUses, ([matchup, uses]) => {
            return { matchupId: matchup, uses: uses, /* matchupString: this.server.matchupIdToMatchupstring.get(matchup) */ };
        }).sort((a, b) => b.uses - a.uses)/*.slice(200, 250)*/);
        console.log(`Top Shooting Uses:`, Array.from(this.server.topShootingUses, ([matchup, uses]) => {
            return { matchupId: matchup, uses: uses,/* matchupString: this.server.matchupIdToMatchupstring.get(matchup) */ };
        }).sort((a, b) => b.uses - a.uses)/*.slice(200, 250)*/);
    };

    // Adds statistics about the request
    addStats = () => {
        // Get the current time
        let startLogTime = common.timer();

        // Calculate the total time taken for the request
        let totalTime = (startLogTime - this.timings.startTime);

        // Add the total time to the server's total time
        this.server.totalTime += totalTime;

        // If there are outcomes for the request
        if (this.timings.outcomes > -1) {
            // Add the outcomes to the server's total outcomes
            this.server.totalOutcomes += this.timings.outcomes;

            // Add the uses to the server's total uses
            this.server.totalUses += this.timings.uses;

            // Increment the server's outcome requests count
            this.server.outcomeRequests++;

            // If the request has sufficient uses, increment the filled strategies count
            if (this.timings.hasSufficientUses) {
                this.server.filledStrategies++;
            }

        }
        if (this.timings.shootingOutcomes > -1) {
            //console.log(`Got shooting outcomes:`, this.timings);
            // Add the outcomes to the server's total outcomes
            this.server.totalShootingOutcomes += this.timings.shootingOutcomes;
            // Add the uses to the server's total uses
            this.server.totalShootingUses += this.timings.shootingUses;

            // Increment the server's outcome requests count
            this.server.shootingOutcomeRequests++;

            // If the request has sufficient uses, increment the filled strategies count
            if (this.timings.hasSufficientShootingUses) {
                this.server.filledShootingStrategies++;
            }
        }

        // If the request was a cache hit
        if (this.timings.cacheHit) {
            // Increment the server's total cache hits
            this.server.totalCacheHits++;

            // Add the database time to the server's total cached time
            this.server.totalCachedTime += this.timings.dbTime;
        } else {
            // Increment the server's total cache misses
            this.server.totalCacheMisses++;

            // Add the database time to the server's total uncached time
            this.server.totalUncachedTime += this.timings.dbTime;
        }

        // Add the database time to the server's total database time
        this.server.totalDbTime += this.timings.dbTime;

        // Add the insert times to the server's total insert time
        this.server.totalInsertTime += (this.timings.insertTargetingOutcome + this.timings.insertShootingOutcomes + this.timings.insertStrategicOutcome);

        // Add the select times to the server's total select time
        this.server.totalSelectTime += (this.timings.getTargetingMatchup + this.timings.getTargetingOutcomesFromId + this.timings.getStrategicMatchup + this.timings.getStrategicOutcomesFromId +
            this.timings.getRelatedOutcomes + this.timings.getShootingMatchup + this.timings.getShootingOutcomesFromId + this.timings.getCachedStrategies);

        // Increment the server's completed requests count
        this.server.requestsCompleted++;

        // Categorize the request based on its total time
        if (totalTime > 1000) {
            // If the request took more than 1000ms, increment the extra long requests count
            this.server.extraLongRequests++;
        } else if (totalTime > 500) {
            // If the request took more than 500ms, increment the long requests count
            this.server.longRequests++;
        } else if (totalTime > 250) {
            // If the request took more than 250ms, increment the medium requests count
            this.server.mediumRequests++;
        }

        // Log statistics every 1000 completed requests
        if (this.server.requestsCompleted % this.server.logFrequency === 0) {
            this.logStats(startLogTime, totalTime);
        }
    };


    // Sends a response to the client
    respond = (response) => {
        response.ProcessingTime = common.timer() - this.timings.startTime; // [debug]
        let json = JSON.stringify(response);
        if (!response) {
            console.error(`Sending empty response to client: ${this.params.Hash}:${this.params.Type}`);
        }
        else if (!json) {
            console.error(`Sending empty JSON to client: ${this.params.Hash}:${this.params.Type}`);
        }
        //else {
        //    console.log(`Sending response to client: ${this.params.Hash}:${this.params.Type}`);
        //}
        this.ws.sendUTF(json);
        this.addStats();
    }
}

/*
A class for handling user actions
*/
class User {
    userId;
    db

    constructor(db, userId){
        console.log(`Creating user #${userId}`);
        this.userId = userId;
        this.db = db;
    }
    /**
     * Determines the user ID based on the filename.
     * @param {string} filename - The name of the file.
     * @returns {number} - The user ID.
     */
    determineUserId(filename) {
        if (filename === "campaign_levels_data" || filename === "challenge_levels_data") {
            return 2; // manual override to force levels data to be the same for all users. Should not stay this way [alert]
        }
        return this.userId;
    }

    /**
     * Stores data for the user.
     * @param {string} filename - The name of the file.
     * @param {string} contents - The contents to store.
     * @returns {Promise<object>} - The result of the database query.
     */
    async storeData(filename, contents) {
        const user = this.determineUserId(filename);
        try {
            let result = await this.db.query(`SELECT filename, contents FROM stored_user_data WHERE userId = ? AND filename = ? ORDER BY ID DESC LIMIT 1`, [user, filename]);
            if (result && result.length > 0 && user != 2) {
                result = await this.db.query(`UPDATE stored_user_data SET contents = ? WHERE userId = ? AND filename = ?`, [contents, user, filename]);
            } else {
                result = await this.db.query(`INSERT INTO stored_user_data (userId, filename, contents) VALUES ?`, [[[user, filename, contents]]]);
            }
            return result;
        } catch (e) {
            common.handleError(e, 'storeData');
            throw e;
        }
    }

    /**
     * Retrieves data for the user.
     * @param {string} filename - The name of the file.
     * @returns {Promise<object>} - The result of the database query.
     */
    async getData(filename) {
        const user = this.determineUserId(filename);
        try {
            const result = await this.db.query(`SELECT filename, contents FROM stored_user_data WHERE userId = ? AND filename = ? ORDER BY ID DESC LIMIT 1`, [user, filename]);
            return result;
        } catch (e) {
            common.handleError(e, 'getData');
            throw e;
        }
    }

    /**
     * Retrieves settings for the user. If the user has specific settings it retreives those, otherwise it returns default settings for that version
     * @param {string} name - The name of the setting.
     * @param {string} version - The version of the setting.
     * @returns {Promise<object>} - The settings for the user.
     */
    async getSettings(name, version) {
        try {
            const outcomes = await this.db.query(`SELECT userId, contents FROM settings WHERE (userId = ? OR userId = ?) AND name = ? and version = ? ORDER BY Id DESC LIMIT 2`, [this.userId, 0, name, version]);
            let settings;
            if (outcomes.length > 1) {
                settings = outcomes[0].userId === this.userId ? outcomes[0] : outcomes[1];
            } else {
                settings = outcomes[0];
            }
            return settings;
        } catch (e) {
            common.handleError(e, 'getSettings');
            throw e;
        }
    }
}
/*
A class for handling game actions
 */
class Game {
    config;
    /**
    * Creates an instance of the Game class.
    * @param {object} db - The database connection object.
    * @param {object} ws - The WebSocket connection object.
    * @param {object} server - The server instance.
    * @param {number} level_id - The ID of the level.
    * @param {number} user_id - The ID of the user. (Unused)
    * @param {number} game_id - The ID of the game.
    */
    constructor(db, ws, server, level_id, game_id) {
        this.config = {
            stratBaseTSV: 50, // this number gets added to the equalized average TSV of every strategy so that small changes in value don't lead to large changes in uses
            stratMetric: "averageTSV", // the key of the strategy metric we're going to use. Things will break if you change this

            /*
            possible strat metrics:
            tsv (sum), averageTSV, weighted_tsv
            */
            minimumCommandUse: 25, // the minimum number of times a command needs to be used before we start counting its tsv, this has a direct affect on training time and training data usage
            maxOutcomesPerStratType: 5, // the maximum number of outcomes for a strat before it needs to be consolidated, the smaller this number, the less data is used but the more time is spent consolidating outcomes
            // updatesPerCommit: 1000
            
        };

        this.possibleStrats = [];
        this.shootingStrats = [];
        this.targetingStrats = [];
        this.possibleStratTypes = [
            { id: 1, name: "Aggressive" },
            { id: 2, name: "Defensive" }, // Retreating
            { id: 3, name: "Random" },
            { id: 4, name: "Circle" },
            { id: 5, name: "Right Swipe" },
            { id: 6, name: "Left Swipe" },
            { id: 7, name: "Closest Friendly" },
            { id: 8, name: "In and Out" },
            { id: 9, name: "Patrol" },
            { id: 10, name: "Guard" },
            { id: 11, name: "Scouting" },
            { id: 12, name: "Mining" },
            { id: 13, name: "Full Retreat" },
            { id: 14, name: "Hold" },
            { id: 15, name: "Heal" }
        ];

        this.shootingStratTypes = [
            "First Seen",           // 1
            "Random",               // 2
            "Revenge",              // 3
            "Most Dangerous",       // 4
            "Most Health",          // 5
            "Least Health",         // 6
            "Most Powerful",        // 7
            "Least Powerful",       // 8
            "Closest",              // 9
            "Furthest",             // 10
            "Most Range",           // 11
            "Least Range",          // 12
            "Fastest",              // 13
            "Slowest",              // 14
            "Most Valuable",        // 15
            "Least Valuable",       // 16

            "Type A",               // Queen            // 17
            "Type B",               // Hornet           // 18
            "Type C",               // Dreadnought      // 19
            "Type D",               // Gunship          // 20
            "Type E",               // Scout            // 21       
            "Type F",               // Wasp             // 22
            "Type G",               // Bumblebee        // 23
            "Type H",               // Flagship         // 24
            "Type I",               // Honeybee         // 25
            "Type J",               // Carpenter Bee    // 26
            "Type K",               // Leafcutter       // 27
            "Type L",               // Yellow Jacket    // 28
            "Type M",               // Beehive          // 29
            "Type N",               // Frigate          // 30
            "Type O",               // Carrier          // 31
            "Type P",               // Drone            // 32
            "Type Q",               // Striker          // 33
            "Type R",               // Factory          // 34
            "Type S",               // Cruiser          // 35
            "Type T",               // Barge            // 36
            "Type U",               // Fire Barge       // 37
            "Type V",               // Warp Gate        // 38
            "Type W",                // Beacon           // 39

            "Type X",               // Human Target     // 40
        ]; // 40

        this.targetingStratTypes = [
            "Random",               // 1
            "Revenge",              // 2
            "Most Dangerous",       // 3
            "Least Health",         // 4
            "Most Health",          // 5
            "Most Powerful",        // 6
            "Least Powerful",       // 7
            "Closest",              // 8
            "Furthest",             // 9
            "Most Range",           // 10
            "Least Range",          // 11
            "Fastest",              // 12
            "Slowest",              // 13
            "In Combat",            // 14
            "Gang Up",              // 15
            "Most Valuable",        // 16
            "Least Valuable",       // 17

            "Type A",               // 18
            "Type B",               // 19
            "Type C",               // 20
            "Type D",               // 21
            "Type E",               // 22
            "Type F",               // 23
            "Type G",               // 24
            "Type H",               // 25
            "Type I",               // 26
            "Type J",               // 27
            "Type K",               // 28
            "Type L",               // 29
            "Type M",               // 30
            "Type N",               // 31
            "Type O",               // 32
            "Type P",               // 33
            "Type Q",               // 34
            "Type R",               // 35
            "Type S",               // 36
            "Type T",               // 37
            "Type U",               // 38
            "Type V",               // 39
            "Type W"                // 40
        ]; // 40

        this.stratCategories = ["possible", "shooting", "targeting"];

        for (let i = 0; i < this.stratCategories.length; i++) {
            let strategyList = this[`${this.stratCategories[i]}Strats`];
            let types = this[`${this.stratCategories[i]}StratTypes`];

            for (let i = 0; i < types.length; i++) {
                strategyList.push({
                    id: types[i].id ? types[i].id : i + 1,
                    name: types[i].name ? types[i].name : types[i],
                    tsv: this.config.stratBaseTSV,
                    uses: 0,
                    averageTSV: this.config.stratBaseTSV,
                    //weighted_tsv: this.config.stratBaseTSV,
                    //last_five: 0,
                    //rng: 0,
                    //matchup: null,
                    //matchup_id: null,
                });
            }
        }

        this.level_id = level_id;
        this.db = db;
        this.ws = ws;
        this.server = server;
        this.pendingUpdates = [];
        this.pendingInserts = new Map();
        //this.deletes = new Map(); // holds all the deleted pending inserts that had been matched with an update and the time they were deleted
        //this.timedDeletes = new Map(); // holds all the deleted pending inserts that had been deleted after being unmatched for 2 hours or more
        this.time = common.timer();
        this.id = game_id;
        this.isActive = true;

        // this.checkForUpdates();
    }


    /**
     * Stores the state of the game.
     * @param {Array} commands - The strategic commands to store.
     * @param {Array} shootingCommands - The shooting commands to store.
     * @param {Array} targetingCommands - The targeting commands to store.
     * @returns {Promise<boolean>}
     */
    storeState = async (commands, shootingCommands, targetingCommands) => {
        // this stores the outcome of each strategic command
        commands.forEach((c) => {
            this.pendingUpdates.push({ table: `strategic_commands`, tsv: c.Tsv, id: Number(c.OutcomeId) });
            //console.log(`Storing (#${c.OutcomeId}:strategic_commands) in pendingUpdates array`);
        });

        shootingCommands.forEach((c) => {
            this.pendingUpdates.push({ table: `shooting_outcomes`, tsv: c.Tsv, id: Number(c.OutcomeId) });
            //console.log(`Storing (#${c.OutcomeId}:shooting_outcomes) in pendingUpdates array`);
        });

        targetingCommands.forEach((c) => {
            this.pendingUpdates.push({ table: `targeting_outcomes`, tsv: c.Tsv, id: Number(c.OutcomeId) });
            //console.log(`Storing (#${c.OutcomeId}:targeting_outcomes) in pendingUpdates array`);
        });

        await this.matchUpdatesWithInsertsAndCommit();

        return true;
    };

    matchUpdatesWithInsertsAndCommit = async () => {

        // Get the current time that the operation started
        let start = common.timer();

        // Create a copy of the pending updates and clear the pending updates array
        let updates = [...this.pendingUpdates];
        this.pendingUpdates = [];


        // Arrays to hold the values to be inserted into different tables
        let targeting = [];
        let shooting = [];
        let strategy = [];

        // Counter for the number of updates processed
        let updateCount = 0;

        // Iterate over each update
        for (let i = 0; i < updates.length; i++) {
            updateCount++;
            let update = updates[i];

            // Get the corresponding insert for the update
            let insert = this.pendingInserts.get(update.id);

            if (insert) {

                // console.log(`Matched update #${update.id} with insert and updated the strategic_outcome to ${update.tsv}.
                // There are ${common.pastNonces.size} past nonces and ${this.pendingInserts.size} pendingInserts left`);


                // If a matching insert is found, update the strategic outcome
                insert.strategic_outcome = update.tsv;

                // Prepare the values to be inserted into the database
                let values = [
                    insert.matchup_id, insert.strategy_id, insert.strategic_outcome
                ];

                // Add the values to the appropriate array based on the table name
                if (insert.table === 0) {
                    targeting.push(values);
                } else if (insert.table === 1) {
                    shooting.push(values);
                } else if (insert.table === 2) {
                    strategy.push(values);
                }

                // Remove the insert from the pending inserts map
                this.pendingInserts.delete(update.id);
                //this.deletes.set(update.id, common.getLocalTime()); // Add to deletes map to track deleted inserts
                //console.log(`Matched pending insert  (#${update.id}:${insert.table}) with pending update (#${update.id}:${update.table}) at ${common.getLocalTime()}.`);

            }
            //else {
            //    // If no matching insert is found, log an error. IT means that the game sent an update id that should correspond to an insert but it doesn't

            //    // This happens when there are lots of resent requests, or if the server restarts mid game (and possibly other times). Needs to be looked into [alert]
            //    //console.error(`Error: Could not find a matched insert for update #${update.id}:${update.table} at ${common.getLocalTime()}.`);
            //    if (this.deletes.has(update.id)) {
            //        // If the insert was previously deleted, log that information
            //        //console.error(`This insert was previously deleted at ${this.deletes.get(update.id)}. This means it was matched with a previous update.`);
            //    }
            //    else if (this.timedDeletes.has(update.id)) {
            //        // If the insert was previously deleted, log that information
            //        //console.error(`This insert was previously deleted at ${this.tiemdDeletes.get(update.id)}. This means it was deleted afer more than 2 hours.`);
            //    }
            //    else {
            //        console.error(`Error: Could not find a matched insert for update #${update.id}:${update.table} at ${common.getLocalTime()}.`);
            //        console.error(`We have no deleted insert that matches this update id. If the server didn't restart and clear all the 
            //        deletes, or if the game didn't disconnect and that fail to reconnect to the same game, then we don't know what happened.`);
            //    }
            //}
        }

        //console.log(`There are ${this.pendingInserts.size} pending Inserts without updates`);

        // Delete pending inserts if they've been around for more than 2 hours
        for (const [key, value] of this.pendingInserts) {
            if (start - value.time > 7200000) { // 2 hours
                this.pendingInserts.delete(key);
                //this.timedDeletes.set(key, common.getLocalTime()); // Add to timedDeletes map to track timed out inserts
                //console.log(`Deleted pending insert (#${key}:${value.table}@${value.timestamp})  at ${common.getLocalTime()} because it was unmatched for more than 2 hours.`); 
            }
        }

        // Insert the values into the respective tables
        if (targeting.length > 0) {
            this.insertIntoTable("targeting_outcomes", targeting);
        }
        if (shooting.length > 0) {
            this.insertIntoTable("shooting_outcomes", shooting);
        }
        if (strategy.length > 0) {
            this.insertIntoTable("strategic_commands", strategy);
        }


        //const insertPromises = [];
        //if (targeting.length > 0) {
        //    insertPromises.push(this.insertIntoTable("targeting_outcomes", targeting));
        //}
        //if (shooting.length > 0) {
        //    insertPromises.push(this.insertIntoTable("shooting_outcomes", shooting));
        //}
        //if (strategy.length > 0) {
        //    insertPromises.push(this.insertIntoTable("strategic_commands", strategy));
        //}
        //await Promise.all(insertPromises);


        // Calculate the total time taken for the operation
        let time = common.timer() - start;

        // Add the total time to the server's total insert time
        this.server.totalInsertTime += time;
        this.server.updateMatchingTime += time;
        this.server.updateMatchingCount++;

        //console.log(`Finished matching ${updateCount} updates with inserts after ${time} ms the average is ${common.calculateAverage(this.server.updateMatchingTime, this.server.updateMatchingCount)}ms`);
    };

    /**
     * Inserts records into the specified table.
     * @param {string} table - The name of the table to insert values into.
     * @param {Array} records - The records to insert into the table.
     * @returns {Promise<void>} - A promise that resolves when the insertion is complete.
     */
    insertIntoTable = async (table, records) => {
        return await new Promise(async (resolve, reject) => {
            let result;
            try {
                // Start the timer to measure the duration of the query
                let start = common.timer();

                // Execute the insert query
                result = await this.db.query(`INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome) VALUES ?`, [records]).catch((e) => {
                    common.handleError(e);
                });

                // Calculate the time taken for the query
                let time = common.timer() - start;

                // Add the time taken to the server's total insert time
                this.server.totalInsertTime += time;

                // console.log(`inserted ${values.length} matched inserts into ${table} in ${time}ms`);

                resolve();
            } catch (e) {
                // Handle any errors that occur during the query
                common.handleError(e);
                reject(e);
            }
        }).catch((e) => {
            // Handle any errors that occur during the promise
            common.handleError(e);
        });
    };

    /**
     * Retrieves or inserts a matchup based on the matchup string and table name.
     * @param {string} matchup - The matchup string to search for or insert.
     * @param {string} table - The name of the table to search in or insert into.
     * @returns {Promise<Object>} - A promise that resolves with an object containing the matchup ID and a boolean indicating if it is a new matchup.
     */
    getMatchup = (matchup) => {
        //console.log(`Converted matchup string to int64:`, matchup, common.stringToInt64(matchup));
        return common.stringToInt64(matchup); // Convert the matchup string to an int64 ID
    };

    /**
     * Adds up all the outcomes for a specific strategy and returns the total strategic value (TSV), uses, last five outcomes, and outcome count.
     * @param {Array} outcomes - The array of outcome objects.
     * @param {number} stratID - The ID of the strategy to filter outcomes by.
     * @param {number} opponent_id - The ID of the opponent (currently unused).
     * @returns {Object} - An object containing the total strategic value (TSV), uses, last five outcomes, and outcome count.
     */
    addOutcomes = (outcomes, stratID, opponent_id) => {
        let tsv = 0; // Total strategic value
        let uses = 0; // Total uses
        //let last_five = 0; // Sum of the last five outcomes
        let outcomeCount = 0; // Total number of outcomes

        // Iterate over each outcome
        for (let i = 0; i < outcomes.length; i++) {
            let strat_outcome = outcomes[i];
            // Check if the outcome belongs to the specified strategy
            if (stratID === strat_outcome.strategy_id) {
                // Calculate the strategic value for the outcome
                let stratValue = strat_outcome.strategic_outcome * strat_outcome.uses;

                // Uncomment the following lines if you want to give more weight to outcomes against a specific opponent
                // if (strat_outcome.opponent_id === opponent_id) {
                //     stratValue *= 3;
                // }

                // Add the strategic value to the last five outcomes if it is one of the last five
                //if (i + 5 >= outcomes.length) {
                //    last_five += stratValue;
                //}

                // Add the strategic value to the total strategic value
                tsv += stratValue;

                // Add the number of uses to the total uses
                uses += strat_outcome.uses;

                // Increment the outcome count
                outcomeCount++;
            }
        }

        // Return an object containing the total strategic value, uses, last five outcomes, and outcome count
        return {
            tsv: tsv,
            uses: uses,
            //last_five: last_five,
            outcomes: outcomeCount
        };
    };

    /**
     * Extracts the ship and enemy ship types from a matchup string.
     * @param {string} matchupString - The matchup string in the format "ships|enemies|".
     * @returns {string} - A string containing the ships and enemies in the format "ships|enemies|".
     */
    getShipsFromMatchup = (matchupString) => {
        // Copy the matchup string to a local variable
        let shipString = `${matchupString}`;

        // Extract the ships part of the string (before the first "|")
        let ships = shipString.substring(0, shipString.indexOf("|"));

        // Remove the ships part from the string
        shipString = shipString.substring(shipString.indexOf("|") + 1);

        // Extract the enemies part of the string (before the second "|")
        let enemies = shipString.substring(0, shipString.indexOf("|"));

        // Return the ships and enemies in the format "ships|enemies|"
        return `${ships}|${enemies}|`;
    };

    /**
     * Retrieves all strategic outcomes from the database for a given matchup ID.
     * @param {number} matchup_id - The ID of the matchup.
     * @param {string} table - The name of the table to query.
     * @returns {Promise<Array>} - A promise that resolves with an array of outcomes.
     */
    getOutcomesFromId = async (matchup_id, table) => {
        return await new Promise(async (resolve, reject) => {
            let outcomes;
            try {
                // Execute the query to select outcomes based on the matchup ID
                outcomes = await this.db.query(`SELECT ID, strategy_id, strategic_outcome, uses FROM ${table} WHERE matchup_id = ?`, [matchup_id]).catch((e) => {
                    common.handleError(e);
                });
                resolve(outcomes);
            } catch (e) {
                // Handle any errors that occur during the query
                common.handleError(e);
                reject(e);
            }
        }).catch((e) => {
            // Handle any errors that occur during the promise
            common.handleError(e);
        });
    };

    /**
     * Inserts a new pending outcome with the specified table into the pending inserts map and returns a unique ID for the inserted outcome.
     * @param {string} table - The name of the table to insert the outcome into.
     * @param {number} matchup_id - The ID of the matchup.
     * @param {number} stratID - The ID of the strategy.
     * @param {number} opponent_id - The ID of the opponent (currently unused).
     * @returns {number} - The unique ID of the inserted outcome.
     */
    insertOutcome = (table, matchup_id, stratID, opponent_id) => {
        // Generate a unique ID for the new outcome
        let id = common.nonce();

        // Add the new outcome to the pending inserts map
        this.pendingInserts.set(
            id,
            {
                matchup_id: matchup_id,
                strategy_id: stratID,
                table: table,
                time: common.timer(),
                //timestamp: common.getLocalTime(), // [debug]
            }
        );
        //console.log(`SET insert #${id}:${table} at ${common.getLocalTime()}`);
        return id;
    };

    /**
     * Extracts the types of enemy ships from a matchup string.
     * @param {string} matchup - The matchup string in the format "ships|enemies|".
     * @returns {Array<string>} - An array of enemy ship types.
     */
    getEnemyShipTypes = (matchup) => {
        // Extract the enemy ship part of the string (between the first and second "|")
        let shipString = matchup.substring(matchup.indexOf("|") + 1, matchup.lastIndexOf("|"));

        // Initialize an array to hold the enemy ship types
        let shipTypes = [];

        // Iterate over each character in the enemy ship string
        for (let i = 0; i < shipString.length; i++) {
            // Add the ship type to the array if it is not already included
            if (!shipTypes.includes(shipString.charAt(i))) {
                shipTypes.push(shipString.charAt(i));
            }
        }

        // Return the array of enemy ship types
        return shipTypes;
    };

    /**
     * Selects a strategy from a list of strategies based on a weighted random number generator (RNG).
     * This method assigns a random number to each strategy, sorts the strategies based on these random numbers,
     * and then selects the strategy with the highest random number.
     * @param {Array} stratList - The list of strategies to select from.
     * @param {string} matchupString - The matchup string associated with the strategies.
     * @param {number} matchup_id - The ID of the matchup.
     * @returns {Object|null} - The selected strategy object or null if the strategy list is empty.
     */
    pickStrat(stratList, matchupString, matchup_id) {
        let selectedStrat;
        let tempMetric = this.config.stratMetric;
        let equalizer = 0; // This is the amount we need to add to all strats to make them all positive


        // find the lowest value strategy
        let lowestStrat = stratList.sort((a, b) => {
            return a[tempMetric] - b[tempMetric];
        })[0];

        // if it's negative, set the equalizer value by 2x the absolute value (e.g. -10  + 20 becomes 10)
        if (lowestStrat[tempMetric] < 0) {
            equalizer = Math.abs(lowestStrat[tempMetric]) * 2;
        }

        // Iterate over each strategy in the strategy list
        for (let i = 0; i < stratList.length; i++) {
            let strat = stratList[i];

            // If the strategy metric is not a finite number, default to using "tsv"
            if (!Number.isFinite(strat[tempMetric])) {
                console.error(`There is no ${tempMetric} value for ${strat}`, matchupString, stratList);
                tempMetric = "tsv";
            }

            
            // Set the strat to not chosen
            strat.chosen = false;

            // Assign a random number to the strategy based on the selected metric
            strat[`equalized_${tempMetric}`] = strat[tempMetric] + equalizer; // [debug-info]
            strat.rng = Math.floor(Math.random() * (strat[`equalized_${tempMetric}`] + this.config.stratBaseTSV));
        }

        // If there are strategies in the list, sort them by the random number in descending order
        if (stratList.length > 0) {

            stratList = stratList.sort((a, b) => {
                return b.rng - a.rng;
            });

            // Select the strategy with the highest random number
            selectedStrat = stratList[0];


            selectedStrat.matchup = matchupString; // [debug-info]
            selectedStrat.matchup_id = matchup_id; // [debug-info]
            stratList[0].chosen = true; // [debug-info]
            return selectedStrat;
        }

        // If the strategy list is empty, log an error and return null
        console.error(`Stratlist is empty`);
        return null;
    }

    addToConsolidationQueue(table, matchup_id, outcomes, opponent_id) {
        if (outcomes && outcomes.length > this.config.maxOutcomesPerStratType && !this.server.consolidationMap.has(matchup_id)) {
            this.server.consolidationMap.set(matchup_id, {table: table, matchup_id: matchup_id, game: this});
        }
    }

    findCachedStrategies = (matchup_id) => {
        // return this.server.cachedStrategies.find((s) => s.matchup_id === matchup_id);
        return this.server.cachedStrategies.get(matchup_id);
    };

    findCachedTargetingStrategies = (matchup_id) => {
        return this.server.cachedTargetingStrategies.get(matchup_id);
    };

    findCachedShootingStrategies = (matchup_id) => {
        return this.server.cachedShootingStrategies.get(matchup_id);
    };

    findCachedMatchup = (matchupString) => {
        return this.server.cachedMatchups.get(matchupString);
    };

    findCachedTargetingMatchup = (matchupString) => {
        return this.server.cachedTargetingMatchups.get(matchupString);
    };

    findCachedShootingMatchup = (matchupString) => {
        return this.server.cachedShootingMatchups.get(matchupString);
    };

    /**
      * Adds a selected strategy to the cache if it is not already cached.
      * This method updates the appropriate cache based on the table name (`targeting_outcomes`, `strategic_commands`, or `shooting_outcomes`).
      * @param {Object} selectedStrat - The selected strategy object.
      * @param {number} matchup_id - The ID of the matchup.
      * @param {string} table - The name of the table (e.g., `targeting_outcomes`, `strategic_commands`, `shooting_outcomes`).
      * @param {Array} availableStrats - The list of available strategies.
      * @param {boolean} isCached - A boolean indicating if the strategy is already cached.
      */
    addToCachedStrategies = (selectedStrat, matchup_id, table, availableStrats, isCached) => {
        // If the strategy is not already cached
        if (!isCached && selectedStrat.uses > this.config.minimumCommandUse) {
            let start = common.timer(); // Start the timer to measure the duration of the caching process

            // Update the appropriate cache based on the table name
            if (table === "targeting_outcomes") {
                this.server.cachedTargetingStrategies.set(matchup_id, {
                    age: start,
                    strats: [...availableStrats],
                });
            } else if (table === "strategic_commands") {
                this.server.cachedStrategies.set(matchup_id, {
                    age: start,
                    strats: [...availableStrats],
                });
            } else {
                this.server.cachedShootingStrategies.set(matchup_id, {
                    age: start,
                    strats: [...availableStrats],
                });
            }

            // Add the time taken to the server's total cache time
            this.server.totalCacheTime += common.timer() - start;
        }
    }

    /**
     * Adds a matchup to the cache.
     * This method updates the appropriate cache based on the table name (`matchup_keys`, `shooting_matchups`, or `targeting_matchups`).
     * @param {number} matchup_id - The ID of the matchup.
     * @param {string} table - The name of the table (e.g., `matchup_keys`, `shooting_matchups`, `targeting_matchups`).
     * @param {string} matchupString - The matchup string.
     */
    addToCachedMatchup = (matchup_id, table, matchupString) => {
        let start = common.timer(); // Start the timer to measure the duration of the caching process

        // Update the appropriate cache based on the table name
        if (table === "matchup_keys") {
            this.server.cachedMatchups.set(matchupString, matchup_id);
            this.server.cachedMatchupsRecent.push([matchupString, matchup_id]);
        } else if (table === "shooting_matchups") {
            this.server.cachedShootingMatchups.set(matchupString, matchup_id);
            this.server.cachedShootingMatchupsRecent.push([matchupString, matchup_id]);
        } else {
            this.server.cachedTargetingMatchups.set(matchupString, matchup_id);
            this.server.cachedTargetingMatchupsRecent.push([matchupString, matchup_id]);
        }

        // Add the time taken to the server's total cache time
        this.server.totalCacheTime += common.timer() - start;
    }

    /**
     * Checks if the number of uses is in the top fifty uses and updates the top uses if necessary.
     * @param {map} uses
     * @param {number} matchupId
     * @param {number} uses
     * @returns
     */
    checkIfInTopUses = (usesMap, matchupId, uses) => {

        if (usesMap.has(matchupId)) {
            //console.log(`MatchupId (${matchupId}) is in the top uses with ${uses} uses`); 
            return true;
        }
        if (usesMap.size < this.server.topUsesCount) {
            //console.log(`MatchupId (${matchupId}) is being inserted the top uses with ${uses} uses because there are only ${this.server.topUses.size} entries`); 

            usesMap.set(matchupId, uses);
            return true;
        }

        for (const [key, value] of usesMap) {
            if (uses > value) {
                //console.log(`MatchupId (${matchupId}) is being inserted into the top uses with ${uses} uses because it has more uses than ${key}:${value}`); 
                usesMap.delete(key); // Delete the old value
                usesMap.set(matchupId, uses); // Set the new value
                this.checkIfInTopUses(usesMap, key, value); // Recursively check if the next value is in the top [uses]
                return true;
            }
        }
        //console.log(`MatchupId (${matchupId}) is not in the top uses with ${uses} uses`); 
        return false;
    }

    /**
     * Retrieves or generates a strategy for a given matchup.
     * This method checks the cache, queries the database, processes outcomes, and selects a strategy.
     * @param {string} shipString - The string representing the ships in the matchup.
     * @param {number} opponent_id - The ID of the opponent.
     * @param {string} hash - The hash of the request.
     * @param {Object} timings - The timings object to record various timing metrics.
     * @param {Array} banned - The list of banned strategies.
     * @returns {Object} - The selected strategy and its details.
     */
    async getMatchupStrategy(shipString, opponent_id, hash, timings, bannedStrats) {
        let start = common.timer();
        let matchup_id = this.findCachedTargetingMatchup(shipString); // check for a cached matchup

        // if you didn't find a cached matchup, get the matchup_id from the database and cache it
        if (!matchup_id) {
            matchup_id = this.getMatchup(shipString);
            this.addToCachedMatchup(matchup_id, "targeting_matchups", shipString);
        }

        //if (!this.server.matchupIdToMatchupstring.has(matchup_id)) { // [debug]
        //    this.server.matchupIdToMatchupstring.set(matchup_id, shipString); // Store the matchup string and its ID in the map
        //}

        timings.getTargetingMatchup = common.timer() - start;
        timings.dbTime += timings.getTargetingMatchup;

        start = common.timer();
        // Select all the strategic outcomes from the database for that matchup ID
        let cachedStrategy = this.findCachedTargetingStrategies(matchup_id);
        let selectedStrat;
        let availableStrats = [];



        // console.log(`1. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats);

        // if there's a cached command strategy and the cached strategies aren't banned, then use the cached strategies
        let isCached = false;
        if (cachedStrategy) {
            availableStrats = cachedStrategy.strats.filter((strat) => {
                return !bannedStrats.includes(strat.name);
            });
            if (availableStrats.length > 0) {
                isCached = true;
            }
        }


        if (isCached){
            this.server.targetingCacheHits++;
            timings.getCachedStrategies += common.timer() - start;
            timings.dbTime += timings.getCachedStrategies;
            timings.targetingCacheHit = true;
            this.server.totalCacheTime += timings.getCachedStrategies;
            // console.log(`3. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
        }
        // if the strategy is not cached and it's not a new matchup, get the outcomes from the database
        else {

            // store the possible strats in a separate array so we can reset to them if we need to
            for (let i = 0; i < this.targetingStrats.length; i++) {
                availableStrats.push({ ...this.targetingStrats[i] });
            }

            // filter out the banned strategies
            availableStrats = availableStrats.filter((strat) => {
                return !bannedStrats.includes(strat.name);
            });
            let fellBackToBase = false;
            // console.log(`4. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
            let outcomes = await this.getOutcomesFromId(matchup_id, "targeting_outcomes"); // get the outcomes for the matchup from the database
            timings.getTargetingOutcomesFromId = common.timer() - start;
            timings.dbTime += timings.getTargetingOutcomesFromId;

            this.addToConsolidationQueue(0, matchup_id, outcomes, opponent_id);

            // Loop through all the outcomes and add up all the TSV
            if (outcomes && outcomes.length > 0){

                // console.log(`request #${hash} with outcomes`);
                // console.log(`There were outcomes for ${matchup}`);
                // if (outcomes.length > this.config.maxOutcomesPerStratType){
                //     this.consolidateOutcomes("strategic_commands");
                // }

                for (let i = 0; i < availableStrats.length && !fellBackToBase; i++){
                    let strat = availableStrats[i];

                    let outcome_value = this.addOutcomes(outcomes, strat.id, opponent_id);
                    strat.tsv = outcome_value.tsv;
                    strat.uses = outcome_value.uses;
                    //strat.last_five = outcome_value.last_five;
                    strat.outcomes = outcome_value.outcomes;


                    // If the strategy has less than the minimum number of uses, revert to random decision
                    if (strat.uses <= this.config.minimumCommandUse){                     

                        // reset all the strategies to their base value
                        for (let i = 0; i < availableStrats.length; i++) {
                            let strat = availableStrats[i];
                            strat.averageTSV = this.config.stratBaseTSV;
                            //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                        }
                        fellBackToBase = `Not enough outcomes for ${strat.name}`; // [debug-info]

                    }
                    // if all of the strategies meet the minimum command use, then calculate the average TSV and the weighted TSV
                    else{
                        // console.log(`There were outcomes for ${matchup} with strat ${strat.name}. Calculating stats`);
                        strat.averageTSV = Math.round(strat.tsv/strat.uses);
                        // This line makes the actual metric be based on the overall performance of the strat with high weight
                        // being placed on the most recent five encounters, and from encounters with this specific opponent
                        //strat.weighted_tsv = strat.averageTSV+strat.last_five;
                    }



                    // strat[this.config.stratMetric] = this.config.stratBaseTSV; // [alert] this is an override
                }

                // if not all the strats have been used enough, ban all the ones that have been used to make sure we try new strats
                if (fellBackToBase) {

                    for (let i = 0; i < availableStrats.length; i++) {
                        let strat = availableStrats[i];
                        if (strat.uses > this.config.minimumCommandUse) {
                            bannedStrats.push(strat.name);
                            strat.banned = true;
                            strat.bannedReason = `This strat isn't being used because it's been used before and one of the strats doesn't have enough uses. Fellbacktobase: ${fellBackToBase}`;
                        }
                    }

                    availableStrats = availableStrats.filter((strat) => {
                        return !bannedStrats.includes(strat.name);
                    });
                }
            }
            // if there are no outcomes for this matchup, reset all the strategies to their base value
            else {
                // store the possible strats in a separate array so we can reset to them if we need to
                for (let i = 0; i < this.targetingStrats.length; i++) {
                    availableStrats.push({ ...this.targetingStrats[i] });
                }
                // filter out the banned strategies
                availableStrats = availableStrats.filter((strat) => {
                    return !bannedStrats.includes(strat.name);
                });

                // reset all the strategies to their base value
                for (let i = 0; i < availableStrats.length; i++) {
                    let strat = availableStrats[i];
                    strat.averageTSV = this.config.stratBaseTSV;
                    //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                }

            }

        }



        // Select a strategy from the available strategies
        if (availableStrats.length > 0) {
            selectedStrat = this.pickStrat(availableStrats, shipString, matchup_id);
            //console.log(`Pickstrat called for #${hash}:targeting`);
            while (bannedStrats.includes(selectedStrat.name)) {
                selectedStrat = this.pickStrat(availableStrats, shipString, matchup_id);
                console.error(`Selected targeting strat is banned: ${selectedStrat.name}`);
            }
        } else {
            selectedStrat = this.pickStrat(this.targetingStrats, shipString, matchup_id);
            console.error(`There were no available targeting strats to pick for ${shipString}`);
            console.error(`Banned strats`, bannedStrats);
            console.error(`Available strats`, availableStrats);
        }

        start = common.timer();
        this.addToCachedStrategies(selectedStrat, matchup_id, "targeting_outcomes", availableStrats, isCached);
        const targetingResultId = this.insertOutcome(0, matchup_id, selectedStrat.id, opponent_id);
        timings.insertTargetingOutcome = common.timer() - start;
        timings.dbTime += timings.insertTargetingOutcome;

        //if (common.nonce() % this.server.logFrequency === 0) {
        //    console.log("matchup ->", {
        //        Type: "get-matchup-strategy",
        //        Hash: hash,
        //        Name: selectedStrat.name,
        //        MatchupString: selectedStrat.matchup,
        //        StrategyId: selectedStrat.id,
        //        MatchupID: selectedStrat.matchup_id,
        //        HistoricalTsv: selectedStrat.tsv, 
        //        HistoricalUses: selectedStrat.uses,
        //        AverageTsv: selectedStrat.averageTSV,
        //        Rng: selectedStrat.rng,
        //        //WeightedTsv: selectedStrat.weighted_tsv,
        //        OutcomeId: targetingResultId,
        //        CachedStrategy: cachedStrategy ? cachedStrategy.strats : [],
        //    }, availableStrats, bannedStrats);
        //}

        if (targetingResultId) {
            return {
                Type: "get-matchup-strategy",
                Hash: hash,
                Name: selectedStrat.name,
                MatchupString: selectedStrat.matchup,  // [debug-info?]
                StrategyId: selectedStrat.id,
                MatchupID: selectedStrat.matchup_id,  // [debug-info?]
                HistoricalTsv: selectedStrat.tsv, // [debug-info]
                HistoricalUses: selectedStrat.uses, // [debug-info]
                AverageTsv: selectedStrat.averageTSV, // [debug-info]
                Rng: selectedStrat.rng, // [debug-info?]
                //WeightedTsv: selectedStrat.weighted_tsv,
                OutcomeId: targetingResultId,
            };
        }
    }

    /**
     * Retrieves or generates a strategy for a given matchup.
     * This method checks the cache, queries the database, processes outcomes, and selects a strategy.
     * @param {string} matchup - The string representing the matchup.
     * @param {Array} bannedStrats - The list of banned strategies.
     * @param {number} opponent_id - The ID of the opponent.
     * @param {string} hash - The hash of the request.
     * @param {Object} timings - The timings object to record various timing metrics.
     * @returns {Object} - The selected strategy and its details.
     */
    getStrategy = async (matchup, bannedStrats, opponent_id, hash, timings) => {

        // console.log(`request #${hash} before awaiting getMatchup`);

        //Get the matchup ID corresponding to the matchup string either by finding the existing one or creating a new one
        let start = common.timer();
        let matchup_id = this.findCachedMatchup(matchup); // check for a cached matchup
        let isFound = false;

        // if you didn't find a cached matchup, get the matchup_id from the database and cache it
        if (!matchup_id){
            matchup_id = this.getMatchup(matchup);
            this.addToCachedMatchup(matchup_id, "matchup_keys", matchup);
        }

        //if (!this.server.matchupIdToMatchupstring.has(matchup_id)) { // [debug]
        //    this.server.matchupIdToMatchupstring.set(matchup_id, matchup); // Store the matchup string and its ID in the map
        //}

        timings.matchup_id = matchup_id;
        timings.getStrategicMatchup = common.timer() - start;
        timings.dbTime += timings.getStrategicMatchup;

        // console.log(`request #${hash} after awaiting getMatchup`);


        // console.log(`request #${hash} before awaiting getOutcomesFromId`);


        // Select all the strategic outcomes from the database for that matchup ID
        start = common.timer();
        let cachedStrategy = this.findCachedStrategies(matchup_id); // see if there's a cached command strategy
        let selectedStrat;
        let availableStrats = [];


        // console.log(`1. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats);

        // if there's a cached command strategy and the cached strategies aren't banned, then use the cached strategies
        let isCached = false;
        if (cachedStrategy){
            availableStrats = cachedStrategy.strats.filter((strat) => {
                return !bannedStrats.includes(strat.name);
            });
            if (availableStrats.length > 0){
                isCached = true;
            }
        }
        // console.log(`2. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
        if (isCached){
            isFound = true;
            this.server.foundStrats++;
            this.server.strategyCacheHits++;
            timings.getCachedStrategies += common.timer() - start;
            timings.dbTime += timings.getCachedStrategies;

            timings.outcomes = availableStrats.reduce((accumulator, currentstrat) => accumulator + currentstrat.outcomes, 0);
            timings.uses = availableStrats.reduce((accumulator, currentstrat) => accumulator + currentstrat.uses, 0);


            timings.cacheHit = true;
            // console.log(`Uses and outcomes of cached strategy: ${timings.uses}, ${timings.outcomes}`);
            this.server.totalCacheTime += timings.getCachedStrategies;
            // console.log(`3. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
        }
        // if the strategy is not cached and it's not a new matchup, get the outcomes from the database
        else{

            // store the possible strats in a separate array so we can reset to them if we need to
            for (let i = 0; i < this.possibleStrats.length; i++) {
                availableStrats.push({ ...this.possibleStrats[i] });
            }

            // filter out the banned strategies
            let fellBackToBase = false;
            availableStrats = availableStrats.filter((strat) => {
                return !bannedStrats.includes(strat.name);
            });
            // console.log(`4. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
            let outcomes = await this.getOutcomesFromId(matchup_id, "strategic_commands"); // get the outcomes for the matchup from the database
            timings.getStrategicOutcomesFromId = common.timer() - start;
            timings.dbTime += timings.getStrategicOutcomesFromId;

            this.addToConsolidationQueue(2, matchup_id, outcomes, opponent_id);
            if (outcomes){
                timings.outcomes = 0;
                timings.uses = 0;
            }
            // Loop through all the outcomes and add up all the TSV
            if (outcomes && outcomes.length > 0){

                // console.log(`request #${hash} with outcomes`);
                // console.log(`There were outcomes for ${matchup}`);
                // if (outcomes.length > this.config.maxOutcomesPerStratType){
                //     this.consolidateOutcomes("strategic_commands");
                // }

                for (let i = 0; i < availableStrats.length && !fellBackToBase; i++){
                    let strat = availableStrats[i];

                    let outcome_value = this.addOutcomes(outcomes, strat.id, opponent_id);
                    strat.tsv = outcome_value.tsv;
                    strat.uses = outcome_value.uses;
                    //strat.last_five = outcome_value.last_five;
                    strat.outcomes = outcome_value.outcomes;
                    timings.uses += strat.uses;
                    timings.outcomes += strat.outcomes;

                    // If the strategy has less than the minimum number of uses, revert to random decision
                    if (strat.uses <= this.config.minimumCommandUse){
                     

                        // reset all the strategies to their base value
                        for (let i = 0; i < availableStrats.length; i++) {
                            let strat = availableStrats[i];
                            strat.averageTSV = this.config.stratBaseTSV;
                            //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                        }
                        fellBackToBase = `Not enough outcomes for ${strat.name}`;

                    }
                    // if all of the strategies meet the minimum command use, then calculate the average TSV and the weighted TSV
                    else{
                        // console.log(`There were outcomes for ${matchup} with strat ${strat.name}. Calculating stats`);
                        strat.averageTSV = Math.round(strat.tsv/strat.uses);
                        // This line makes the actual metric be based on the overall performance of the strat with high weight
                        // being placed on the most recent five encounters, and from encounters with this specific opponent
                        //strat.weighted_tsv = strat.averageTSV+strat.last_five;
                    }



                    // strat[this.config.stratMetric] = this.config.stratBaseTSV; // [alert] this is an override
                }

                isFound = true;
                this.server.foundStrats++;

                // if not all the strats have been used enough, ban all the ones that have been used to make sure we try new strats
                if (fellBackToBase) {

                    timings.fellBackToBase = true;
                    for (let i = 0; i < availableStrats.length; i++) {
                        let strat = availableStrats[i];
                        if (strat.uses > this.config.minimumCommandUse) {
                            bannedStrats.push(strat.name);
                            strat.banned = true;
                            strat.bannedReason = `This strat isn't being used because it's been used before and one of the strats doesn't have enough uses. Fellbacktobase: ${fellBackToBase}`;
                        }
                    }

                    availableStrats = availableStrats.filter((strat) => {
                        return !bannedStrats.includes(strat.name);
                    });
                }
                
            }
            // if there are no outcomes for this matchup, reset all the strategies to their base value
            else {
                // store the possible strats in a separate array so we can reset to them if we need to
                for (let i = 0; i < this.possibleStrats.length; i++) {
                    availableStrats.push({ ...this.possibleStrats[i] });
                }

                // filter out the banned strategies
                availableStrats = availableStrats.filter((strat) => {
                    return !bannedStrats.includes(strat.name);
                });

                // reset all the strategies to their base value
                for (let i = 0; i < availableStrats.length; i++) {
                    let strat = availableStrats[i];
                    strat.averageTSV = this.config.stratBaseTSV;
                    //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                }

                // record server stats
                isFound = false;
                this.server.unFoundStrats++;
            }

        }
        



        // pick the strat now that all of the outcomes have been added up
        if (availableStrats.length > 0) {
            selectedStrat = this.pickStrat(availableStrats, matchup, matchup_id);
            //console.log(`Pickstrat called for #${hash}:command`);
            while (bannedStrats.includes(selectedStrat.name)) {
                selectedStrat = this.pickStrat(availableStrats, matchup, matchup_id);
                console.error(`Selected command strat is banned: ${selectedStrat.name}`);
            }
        } else {
            selectedStrat = this.pickStrat(this.originalStrats, matchup, matchup_id);
            console.error(`There were no available command strats to pick for ${matchup}`);
            console.error(`Banned strats`, bannedStrats);
            console.error(`Available strats`, availableStrats);
        }



        // Start doing the same for shooting strategies

        //Get the matchup ID corresponding to the matchup string either by finding the existing one or creating a new one
        start = common.timer();
        let shootingMatchup = this.getShipsFromMatchup(matchup);
        let shootingMatchup_id = this.findCachedShootingMatchup(shootingMatchup); // check for a cached matchup
        let isShootingStrategyFound = false;
        let bannedShootingStrats = [];



        // if you didn't find a cached matchup, get the matchup_id from the database and cache it
        if (!shootingMatchup_id) {
            shootingMatchup_id = this.getMatchup(shootingMatchup);
            this.addToCachedMatchup(shootingMatchup_id, "shooting_matchups", shootingMatchup);
        }

        //if (!this.server.matchupIdToMatchupstring.has(shootingMatchup_id)) { // debug]
        //    this.server.matchupIdToMatchupstring.set(shootingMatchup_id, shootingMatchup); // Store the matchup string and its ID in the map
        //}

        timings.shooting_matchup_id = matchup_id;
        timings.getShootingMatchup = common.timer() - start;
        timings.dbTime += timings.getShootingMatchup;


        // Select all the strategic outcomes from the database for that matchup ID
        start = common.timer();
        let cachedShootingStrategy = this.findCachedShootingStrategies(shootingMatchup_id); // see if there's a cached shooting strategy
        let selectedShootingStrat;
        let availableShootingStrats = [];

        // store the possible strats in a separate array so we can reset to them if we need to
        for (let i = 0; i < this.shootingStrats.length; i++) {
            availableShootingStrats.push({ ...this.shootingStrats[i] });
        }

        let shipTypes = this.getEnemyShipTypes(shootingMatchup);
        // ban all the ship targeting strats that include ships that aren't in the enemy matchup
        for (let i = 0; i < availableShootingStrats.length; i++) {
            let strat = availableShootingStrats[i];
            if (strat.name.includes("Type ")) {
                let shipType = strat.name.substring(5);
                if (!shipTypes.includes(shipType)) {
                    bannedShootingStrats.push(`Type ${shipType}`);
                    strat.banned = true;
                    strat.bannedReason = "Type strat when that ship is not available";
                }
            }
        }

        // console.log(`1. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats);

        // if there's a cached command strategy and the cached strategies aren't banned, then use the cached strategies
        let isShootingCached = false;
        if (cachedShootingStrategy) {
            availableShootingStrats = cachedShootingStrategy.strats.filter((strat) => {
                return !bannedShootingStrats.includes(strat.name);
            });
            if (availableShootingStrats.length > 0) {
                isShootingCached = true;
            } else {
                availableShootingStrats = [];
                for (let i = 0; i < this.shootingStrats.length; i++) {
                    availableShootingStrats.push({ ...this.shootingStrats[i] });
                }
            }
        }
        // console.log(`2. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
        if (isShootingCached) {
            isShootingStrategyFound = true;
            this.server.foundShootingStrats++;
            this.server.shootingCacheHits++;
            timings.getCachedStrategies += common.timer() - start;
            timings.dbTime += timings.getCachedStrategies;
            this.server.totalCacheTime += timings.getCachedStrategies;
            timings.shootingCacheHit = true;

            availableShootingStrats = cachedShootingStrategy.strats;
            timings.shootingOutcomes = availableShootingStrats.reduce((accumulator, currentstrat) => accumulator + currentstrat.outcomes, 0);
            timings.shootingUses = availableShootingStrats.reduce((accumulator, currentstrat) => accumulator + currentstrat.uses, 0);


            timings.shootingCacheHit = true;
            // console.log(`Uses and outcomes of cached strategy: ${timings.uses}, ${timings.outcomes}`);
            // console.log(`3. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
        }
        // if the strategy is not cached and it's not a new matchup, get the outcomes from the database
        else {
            // filter out the banned strategies
            let fellBackToBase = false;
            availableShootingStrats = availableShootingStrats.filter((strat) => {
                return !bannedShootingStrats.includes(strat.name);
            });
            // console.log(`4. available strats for ${matchup} - #${hash}`, "available", availableStrats, "banned", bannedStrats,`isCached: ${isCached}`, "cachedStrats", cachedStrategy ? cachedStrategy.strats : "");
            let outcomes = await this.getOutcomesFromId(shootingMatchup_id, "shooting_outcomes");
            timings.getShootingOutcomesFromId += common.timer() - start;
            timings.dbTime += timings.getShootingOutcomesFromId;

            this.addToConsolidationQueue(1, shootingMatchup_id, outcomes, opponent_id);

            if (outcomes) {
                timings.shootingOutcomes = 0;
                timings.shootingUses = 0;
            }
            // Loop through all the outcomes and add up all the TSV
            if (outcomes && outcomes.length > 0) {

                // console.log(`request #${hash} with outcomes`);
                // console.log(`There were outcomes for ${matchup}`);
                // if (outcomes.length > this.config.maxOutcomesPerStratType){
                //     this.consolidateOutcomes("strategic_commands");
                // }
                for (let i = 0; i < availableShootingStrats.length && !fellBackToBase; i++) {
                    let strat = availableShootingStrats[i];

                    let outcome_value = this.addOutcomes(outcomes, strat.id, opponent_id);
                    strat.tsv = outcome_value.tsv;
                    strat.uses = outcome_value.uses;
                    //strat.last_five = outcome_value.last_five;
                    strat.outcomes = outcome_value.outcomes;
                    timings.shootingUses += strat.uses;
                    timings.shootingOutcomes += strat.outcomes;

                    // If the strategy has less than the minimum number of uses, revert to random decision
                    if (strat.uses <= this.config.minimumCommandUse) {

                        // reset all the strategies to their base value
                        for (let i = 0; i < availableShootingStrats.length; i++) {
                            let strat = availableShootingStrats[i];
                            strat.averageTSV = this.config.stratBaseTSV;
                            //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                        }
                        fellBackToBase = `Not enough outcomes for ${strat.name}`;

                    }
                    // if all of the strategies meet the minimum command use, then calculate the average TSV and the weighted TSV
                    else {
                        // console.log(`There were outcomes for ${matchup} with strat ${strat.name}. Calculating stats`);
                        strat.averageTSV = Math.round(strat.tsv / strat.uses);
                        // This line makes the actual metric be based on the overall performance of the strat with high weight
                        // being placed on the most recent five encounters, and from encounters with this specific opponent
                        //strat.weighted_tsv = strat.averageTSV + strat.last_five;
                    }



                    // strat[this.config.stratMetric] = this.config.stratBaseTSV; // [alert] this is an override
                }

                isShootingStrategyFound = true;
                this.server.foundShootingStrats++;

                // if not all the strats have been used enough, ban all the ones that have been used to make sure we try new strats
                if (fellBackToBase) {

                    timings.shootingFellBackToBase = true;
                    for (let i = 0; i < availableShootingStrats.length; i++) {
                        let strat = availableShootingStrats[i];
                        if (strat.uses > this.config.minimumCommandUse) {
                            bannedShootingStrats.push(strat.name);
                            strat.banned = true;
                            strat.bannedReason = `This strat isn't being used because it's been used before and one of the strats doesn't have enough uses. Fellbacktobase: ${fellBackToBase}`;
                        }
                    }

                    availableShootingStrats = availableShootingStrats.filter((strat) => {
                        return !bannedShootingStrats.includes(strat.name);
                    });
                }
                
            }
            // if there are no outcomes for this matchup, reset all the strategies to their base value
            else {
                // filter out the banned strategies
                availableShootingStrats = availableShootingStrats.filter((strat) => {
                    return !bannedShootingStrats.includes(strat.name);
                });

                // reset all the strategies to their base value
                for (let i = 0; i < availableShootingStrats.length; i++) {
                    let strat = availableShootingStrats[i];
                    strat.averageTSV = this.config.stratBaseTSV;
                    //strat[this.config.stratMetric] = this.config.stratBaseTSV;
                }

                // record server stats
                isShootingStrategyFound = false;
                this.server.unFoundShootingStrats++;
            }
        }
        

        if (availableShootingStrats.length > 0) {
            selectedShootingStrat = this.pickStrat(availableShootingStrats, shootingMatchup, shootingMatchup_id);
            //console.log(`Pickstrat called for #${hash}:shooting`);
            while (bannedShootingStrats.includes(selectedShootingStrat.name)) {
                selectedShootingStrat = this.pickStrat(availableShootingStrats, shootingMatchup, shootingMatchup_id);
                console.error(`Selected shooting strat is banned: ${selectedShootingStrat.name}`);
            }
        } else {
            selectedShootingStrat = this.pickStrat(this.shootingStrats, shootingMatchup, shootingMatchup_id);
            console.error(`There were no available shooting strats to pick for ${shootingMatchup}`);
            console.error(`Banned strats`, bannedShootingStrats);
            console.error(`Available strats`, availableShootingStrats);
        }

        // if all the strategies have enough uses, record that for the server statistics
        if (selectedStrat.uses > this.config.minimumCommandUse) {
            timings.hasSufficientUses = true;
        }

        if (selectedShootingStrat.uses > this.config.minimumCommandUse) {
            timings.hasSufficientShootingUses = true;
        }

        this.server.uniqueCommandMatchups.set(matchup_id, timings.hasSufficientUses);
        this.server.uniqueShootingMatchups.set(shootingMatchup_id, timings.hasSufficientShootingUses);


        start = common.timer();
        this.addToCachedStrategies(selectedShootingStrat, shootingMatchup_id, "shooting_outcomes", availableShootingStrats, isShootingCached);
        let shootingResultId = this.insertOutcome(1, shootingMatchup_id, selectedShootingStrat.id, opponent_id);
        timings.insertShootingOutcomes = common.timer() - start;
        timings.dbTime += timings.insertShootingOutcomes;

        start = common.timer();
        this.addToCachedStrategies(selectedStrat, matchup_id, "strategic_commands", availableStrats, isCached);
        let resultId = this.insertOutcome(2, matchup_id, selectedStrat.id, opponent_id);
        timings.insertStrategicOutcome = common.timer() - start;
        timings.dbTime += timings.insertStrategicOutcome;


        // console.log(`Got unique random outcome ids: ${shootingResult}, ${result}`);
        // console.log(`Matchup ${matchup}`);
        // console.log("possible strats", this.possibleStrats);
        // console.log("possible strats", this.shootingStrats);

        // console.log(`Shooting matchups with strats: ${this.server.foundShootingStrats}, matchups with no strats: ${this.server.unFoundShootingStrats} ${Math.round(((this.server.unFoundShootingStrats/(this.server.foundShootingStrats+this.server.unFoundShootingStrats))*10000))/100}% unfound`);
        // console.log(`Targeting matchups with strats: ${this.server.foundTargetingStrats}, matchups with no strats: ${this.server.unFoundTargetingStrats} ${Math.round(((this.server.unFoundTargetingStrats/(this.server.foundTargetingStrats+this.server.unFoundTargetingStrats))*10000))/100}% unfound`);
        // console.log(`\n\n`);
        // console.log(`Server handling ${this.server.recent_requests} requests since startup`);

        // Don't count this strategy if it's one of the most commonly used strategies


        if (this.checkIfInTopUses(this.server.topUses, matchup_id, timings.uses)) {
            timings.uses = -1;
            timings.outcomes = -1;

            if (isFound) {
                this.server.foundStrats--;
            } else {
                this.server.unFoundStrats--;
            }

            this.server.topUsesHits++;
        }


        if (this.checkIfInTopUses(this.server.topShootingUses, shootingMatchup_id, timings.shootingUses)) {
            timings.shootingUses = -1;
            timings.shootingOutcomes = -1;

            if (isShootingStrategyFound) {
                this.server.foundShootingStrats--;
            } else {
                this.server.unFoundShootingStrats--;
            }

            this.server.topShootingUsesHits++;
        }

        this.server.serverStrategiesServed++;

        if (resultId){
            let stratReturn = {
                Type: "get-strategy",
                Name: selectedStrat.name,
                MatchupString: matchup,
                StrategyId: selectedStrat.id,
                MatchupID: matchup_id,
                OutcomeId: resultId,
                Hash: hash,
                HistoricalTsv: selectedStrat.tsv, // [debug-info]
                AverageTsv: selectedStrat.averageTSV, // [debug-info]
                Rng: selectedStrat.rng, // [debug-info]
                HistoricalUses: selectedStrat.uses, // [debug-info]
                ShootingStrategyName: selectedShootingStrat.name,
                ShootingStrategyMatchupString: selectedShootingStrat.matchup,
                ShootingStrategyMatchupID: selectedShootingStrat.matchup_id,
                ShootingStrategyId: selectedShootingStrat.id,
                ShootingStrategyOutcomeId: shootingResultId,
                ShootingHistoricalTsv: selectedShootingStrat.tsv, // [debug-info]
                ShootingAverageTsv: selectedShootingStrat.averageTSV, // [debug-info]
                ShootingRng: selectedShootingStrat.rng, // [debug-info]
                ShootingHistoricalUses: selectedShootingStrat.uses, // [debug-info]
                IsCached: isCached, // [debug-info]
                

            };

            //if (common.nonce() % this.server.logFrequency === 0) {
            //    console.log("strategy ->", stratReturn);
            //    console.log("available/banned command strats ->", availableStrats, bannedStrats);
            //    console.log("available/banned shooting strats ->", availableShootingStrats, bannedShootingStrats);
            //}

            return stratReturn;
        }else{
            console.error(`Could not insert a strategic command result for #${hash}`);
        }
    };

}
/*
A class for handling a socket connection to a client computer by receiving requests
 */
class SocketConnection {
    constructor(connection, db, server, id){
        this.connection = connection;
        this.server = server;
        this.db = db;
        this.id = id;
        this.start = common.timer();
        this.connection.on('message', async (message) => {
            // console.log(`Received message`, message);

            // Push the message to the server's queue with the connection ID and start time
            //this.server.queue.push({ message: message, connection_id: this.id, startTime: common.timer() });

            let request = new SocketRequest(JSON.parse(message.utf8Data), this.connection, this.server, common.timer(), 0, 0, this.id);

            // Check if the request is already pending
            if (this.server.pendingRequests.has(request.params.Hash)) {
                // If the request is already pending, do nothing and return
                console.log(`Request #${request.params.Hash} is already pending`);
                return;
            } else {
                // Mark the request as pending
                //console.log(`Request #${request.params.Hash} received`);
                this.server.pendingRequests.set(request.params.Hash, 1);
                this.server.queue.push(request);
            }

            

        });
        this.connection.on('close', (reasonCode, description) => {
            let msg = `\nPeer user #${this.user_id || "null"} disconnected from the web socket at ${common.getLocalTime()}. Reason: [${reasonCode}, ${description}]`;
            console.log(msg);
            //console.error(msg);
            if (this.id){
                common.pastNonces.delete(this.id);
                this.server.connections.delete(this.id);
            }
            if (this.game) {
                this.game.isActive = false;
                this.game.time = common.timer();
            }
        });

    }

    /**
     * Handles incoming messages from the client.
     * This method processes the message, determines the request type, and performs the appropriate action.
     * @param {Object} message - The incoming message object.
     */
    handleMessage = async (request) => {
        // Parse the message and create a new SocketRequest instance
        //let request = new SocketRequest(JSON.parse(message.utf8Data), this.connection, this.server, message.startTime, message.queueTime, message.messageId);

        // Check if the request is already pending
        //if (this.server.pendingRequests.has(request.params.Hash)) {
        //    // If the request is already pending, do nothing and return
        //    console.log(`Request #${request.params.Hash} is already pending`);
        //    return;
        //} else {
        //    // Mark the request as pending
        //    //console.log(`Request #${request.params.Hash} received`);
        //    this.server.pendingRequests.set(request.params.Hash, 1);
        //}

        // Set the request type in the timings object
        request.timings.type = request.params.Type;

        // Handle different request types
        if (request.params.Type === "get-matchup-strategy") {
            let strategy;
            this.server.totalMatchupOrStrategyRequests++;
            this.server.targetingStrategyRequests++;
            try {
                // Get the matchup strategy
                strategy = await this.game.getMatchupStrategy(request.params.Ships, request.params.OpponentId, request.params.Hash, request.timings, request.params.Banned);
            } catch (e) {
                console.log(request.params);
                console.log("ERROR with matchup strategy request", e);
                common.handleError(e);
            }
            if (strategy) {
                // Respond with the strategy and remove the request from pending
                strategy.ServerLatency = (common.timer() - request.timings.startTime);
                request.respond(strategy);
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else if (request.params.Type === "get-strategy") {
            let strategy;
            this.server.totalMatchupOrStrategyRequests++;
            try {
                // Get the strategy
                strategy = await this.game.getStrategy(request.params.Matchup, request.params.BannedStrats, request.params.OpponentId, request.params.Hash, request.timings);
            } catch (e) {
                console.log("ERROR with strategy request");
                common.handleError(e);
            }
            if (strategy) {
                // Respond with the strategy and remove the request from pending
                strategy.ServerLatency = (common.timer() - request.timings.startTime);
                request.respond(strategy);
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else if (request.params.Type === "store-commands") {
            let data;
            try {
                // Store the commands
                data = await this.game.storeState(request.params.Commands, request.params.ShootingCommands, request.params.TargetingCommands);
            } catch (e) {
                common.handleError(e);
            }
            if (data) {
                // Respond with success status and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    Status: 200
                });
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else if (request.params.Type === "setup-level") {
            if (!this.user) {
                // Create a new User instance if not already created
                this.user = new User(this.db, request.params.UserId);
                this.user_id = this.user.userId;
            }
            request.params.Version = 6;
            // Create a new Game instance
            this.game = new Game(this.db, this.connection, this.server, request.params.LevelId, this.id);
            this.server.games.set(this.id, this.game);
            // Respond with success status and remove the request from pending
            request.respond({
                Type: request.params.Type,
                Hash: request.params.Hash,
                GameId: this.game.id,
                Status: 200
            });
            this.server.pendingRequests.delete(request.params.Hash);
        }
        else if (request.params.Type === "reconnect-level") {
            console.log(`Trying to reconnect to a disconnected game`); // [check]
            if (!this.user) {
                // Create a new User instance if not already created
                this.user = new User(this.db, request.params.UserId);
                this.user_id = this.user.userId;
            }
            request.params.Version = 6;
            // Try to find an existing game with that id
            this.game = this.server.games.get(request.params.GameId);

            if (!this.game) {
                console.log(`Could not find an active game to reconnect to, making a new one`);
                // Create a new Game instance because we could not find an existing game
                this.game = new Game(this.db, this.connection, this.server, request.params.LevelId, this.id);
                this.server.games.set(this.id, this.game);
            } else {
                // If we did find an existing game, set it to active
                console.log(`Found an active game to reconnect to`);
                this.game.isActive = true;
            }

            // Respond with success status and remove the request from pending
            request.respond({
                Type: request.params.Type,
                Hash: request.params.Hash,
                GameId: this.game.id,
                Status: 200
            });
            this.server.pendingRequests.delete(request.params.Hash);
        }
        else if (request.params.Type === "store-user-data") {
            if (!this.user) {
                // Create a new User instance if not already created
                this.user = new User(this.db, request.params.UserId);
                this.user_id = this.user.userId;
            }
            let data;
            try {
                // Store the user data
                data = await this.user.storeData(request.params.DataFile, request.params.Contents);
            } catch (e) {
                common.handleError(e);
            }
            if (data) {
                // Respond with success status and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    Status: 200
                });
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else if (request.params.Type === "get-user-data") {
            if (!this.user) {
                // Create a new User instance if not already created
                this.user = new User(this.db, request.params.UserId);
                this.user_id = this.user.userId;
            }
            let data;
            try {
                // Get the user data
                data = await this.user.getData(request.params.DataFile);
            } catch (e) {
                common.handleError(e);
            }
            if (data && data[0]) {
                // Respond with the user data and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    UserId: this.user.userId,
                    Nonce: request.params.Nonce,
                    Filename: data[0].filename,
                    Contents: data[0].contents
                });
                this.server.pendingRequests.delete(request.params.Hash);
            } else {
                console.log(`Could not get data`, data);
                // Respond with null data and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    UserId: this.user.userId,
                    Nonce: request.params.Nonce,
                    Filename: null,
                    Contents: null
                });
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else if (request.params.Type === "get-settings") {
            if (!this.user) {
                // Create a new User instance if not already created
                this.user = new User(this.db, request.params.UserId);
                this.user_id = this.user.userId;
            }
            let settings;
            try {
                // Get the user settings
                settings = await this.user.getSettings(request.params.DataFile, request.params.Version);
            } catch (e) {
                common.handleError(e);
            }
            if (settings) {
                // Respond with the user settings and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    UserId: this.user.userId,
                    Nonce: request.params.Nonce,
                    Filename: request.params.DataFile,
                    Contents: settings.contents
                });
                this.server.pendingRequests.delete(request.params.Hash);
            } else {
                // Respond with null settings and remove the request from pending
                request.respond({
                    Type: request.params.Type,
                    Hash: request.params.Hash,
                    UserId: this.user.userId,
                    Nonce: request.params.Nonce,
                    Filename: null,
                    Contents: null
                });
                this.server.pendingRequests.delete(request.params.Hash);
            }
        } else {
            // Handle unknown request types
            console.log("ERROR: Unknown request type", request);
            request.respond({
                Type: request.params.Type,
                Hash: request.params.Hash,
                Status: 200
            });
        }
    };

}
/*
A class for handling the server setup and queue
 */
class Server {
    constructor(test, port) {
        this.test = test;
        this.db = new Database("127.0.0.1", "bees", "_#gg86gf-EVMuzS", "ram"); // ramdrew | b_team | ram
        this.port = port;
        this.connections = new Map();
        this.recent_requests = 0;
        this.requestsStarted = 0;
        this.requestsCompleted = 0;
        this.queue = [];
        this.consolidationMap = new Map();
        this.consolidationQueue = [];
        this.updateQueue = [];
        this.pendingRequests = new Map();
        this.cachedStrategies = new Map();
        this.cachedTargetingStrategies = new Map();
        this.cachedShootingStrategies = new Map();
        this.cachedMatchups = new Map();
        this.cachedTargetingMatchups = new Map();
        this.cachedShootingMatchups = new Map();
        this.games = new Map();
        this.maxCachedStrategyAge = 1200000; // 20 minutes
        this.cpus = 4;
        this.useCluster = false;
        this.totalTime = 0;
        this.totalQueueTime = 0;
        this.totalDbTime = 0;
        this.totalUncachedTime = 0;
        this.totalCachedTime = 0;
        this.totalCacheHits = 0;
        this.totalCacheMisses = 0;
        this.totalMatchupOrStrategyRequests = 0;
        this.targetingStrategyRequests = 0;
        this.targetingCacheHits = 0;
        this.shootingCacheHits = 0;
        this.strategyCacheHits = 0;
        this.totalInsertTime = 0;
        this.totalSelectTime = 0;
        this.totalCacheTime = 0;
        this.totalCacheFilterTime = 0;
        this.cacheFilters = 0;
        this.totalOutcomes = 0;
        this.totalShootingOutcomes = 0;
        this.totalUses = 0;
        this.totalShootingUses = 0;
        this.outcomeRequests = 0;
        this.shootingOutcomeRequests = 0;
        this.filledStrategies = 0;
        this.filledShootingStrategies = 0;
        this.logFrequency = 100000; // How frequently information is logged, the higher the number the less frequent
        this.isRunningConsolidation = false; // Whether or not the server is currently consolidating outcomes
        this.topUsesCount = 150; // The number of top uses to keep track of

        this.cacheSliceSize = 1000;
        // this.cachedMatchupsWriteStream = fs.createWriteStream(`./cachedMatchups.json`, { flags : 'a' });
        // this.cachedTargetingMatchupsWriteStream = fs.createWriteStream(`./cachedTargetingMatchups.json`, { flags : 'a' });
        // this.cachedShootingMatchupsWriteStream = fs.createWriteStream(`./cachedShootingMatchups.json`, { flags : 'a' });
        this.cacheWriteQueue = ["cachedMatchups", "cachedShootingMatchups", "cachedTargetingMatchups"];
        this.useFullDiskWrite = false;
        this.cachedMatchupsWritePoint = 0;
        this.cachedShootingMatchupsWritePoint = 0;
        this.cachedTargetingMatchupsWritePoint = 0;
        this.totalWriteTime = 0;
        this.writesCount = 0;
        this.cachedMatchupsRecent = [];
        this.cachedTargetingMatchupsRecent = [];
        this.cachedShootingMatchupsRecent = [];
        this.extraLongRequests = 0;
        this.longRequests = 0;
        this.mediumRequests = 0;
        this.startTime = common.timer();
        this.topUses = new Map(); // a map of the top 50 uses amounts keyed by their matchup Id
        this.topShootingUses = new Map(); // a map of the top 50 shooting uses amounts keyed by their matchup Id
        this.topUsesHits = 0;
        this.topShootingUsesHits = 0;
        this.uniqueCommandMatchups = new Map(); // A map of all the unique command matchups and whether or not they've been filled
        this.uniqueShootingMatchups = new Map(); // A map of all the unique shooting matchups and whether or not they've been filled
        this.serverStrategiesServed = 0; // The number of command strategies served by the server
        this.totalConsolidatedRows = 0;
        this.totalConsolidatedInsertRows = 0;
        //this.matchupIdToMatchupstring = new Map(); // A map of all the matchup IDs to their matchup strings, for testing purposes [debug]

        /*
        Keeping track of the found and unfound strats for debugging purposes
         */
        this.unFoundStrats = 0;
        this.foundStrats = 0;
        this.unFoundShootingStrats = 0;
        this.foundShootingStrats = 0;
        this.unFoundTargetingStrats = 0;
        this.foundTargetingStrats = 0;
        this.consolidationTime = 0;
        this.consolidationCount = 0;
        this.updateMatchingTime = 0;
        this.updateMatchingCount = 0;
        this.cacheFolder = "./"; //R:\\Cache

        if (this.test){
            this.port = 7146;
        }


        if (cluster.isMaster && this.useCluster) {
            console.log(`Primary ${process.pid} is running`);

            // Fork workers.
            for (let i = 0; i < this.cpus; i++) {
                cluster.fork();
            }

            cluster.on('exit', (worker, code, signal) => {
                console.log(`worker ${worker.process.pid} died`, code, signal);
            });
        }
        else {
            // Workers can share any TCP connection
            // In this case it is an HTTP server


            console.log(`Worker ${process.pid} started`);
            this.start();
        }
    }

    countPending = () => {
        let inserts = 0;
        let updates = 0;
        for (const [key, value] of this.games) {
            inserts += value.pendingInserts.size;
            updates += value.pendingUpdates.length;
        }
        return {inserts: inserts, updates: updates};
    };

    /**
     * Called on a timer to periodically remove old, inactive games from the server.
     */
    removeOldGames = () => {
        // Get the current time that the operation started
        let start = common.timer();

        for (const [key, game] of this.games) {
            if (!game.isActive && start - game.time > 7200000) { // 2 hours
                this.games.delete(key);
                console.log(`Deleted old game (#${key}) at ${common.getLocalTime()} because it was inactive for more than 2 hours.`); 
            }
        }
    }

    saveCacheMaps = () => {
        setInterval(() => {
            if (this.connections.size === 0){
                let cache = this.cacheWriteQueue.shift();
                // console.log(`Write loop for ${cache}`);
                this.writeCacheToDisk(cache);
                this.cacheWriteQueue.push(cache);
            }

            // console.log(`Finished calling writeCache for ${cache}\n`);
        }, 600000); // 10 minutes
    };

    writeCacheToDisk = (cache) => {

        return new Promise(async (resolve, reject) => {

             console.log(`Writing cache file: ${cache}.json`);
            let start = common.timer();
            this.writesCount++;
            try {
                let content = this[`${cache}Recent`];
                this[`${cache}Recent`] = [];
                // console.log(`Content to array done for ${cache} in ${(common.timer() - start)}ms. Length: ${content.length}.`);
                if (this.useFullDiskWrite){
                    content = JSON.stringify(content);
                    // console.log(`Stringified content for ${cache}`);
                    fs.writeFile(`./${cache}.json`, content, function(err) {
                        if(err) {
                            return console.error(`Cache file writing error for ${cache}.json: `, err);
                        }
                        // console.log(`\nThe ${cache}.json file was saved!\n`);
                    });
                }else{
                    // let stream = this[`${cache}WriteStream`];
                    let stream = fs.createWriteStream(`${this.cacheFolder}/${cache}.json`, { flags : 'a' });
                    // console.log(`Created write stream to ${cache}.json`);
                    let limit = content.length/this.cacheSliceSize;
                    for (let i = 0; i < limit; i++) {
                        let slice = content.slice((i*this.cacheSliceSize), ((i+1)*this.cacheSliceSize));
                        let string = JSON.stringify(slice).slice(1,-1);
                        if (string !== "" && string !== "[]"){
                            // console.log(`Writing ${string} to ${cache}.json`);

                            stream.write(`${string},`);
                            // if (i == 0){
                            //     stream.write(`[${string},\n`);
                            // }
                            // else if (i < limit-1){
                            //     stream.write(`${string},\n`);
                            // }
                            // else{
                            //     stream.write(`${string}]`);
                            // }
                            // console.log(`Wrote line #${(i*this.cacheSliceSize)}`);
                        }else{
                            // console.log(`String is blank: ${string}`, slice);
                        }

                    }
                    // let time = common.timer()-start;
                    // this.totalWriteTime += time;
                    // let averageWriteTime = Math.round(((this.totalWriteTime / this.writesCount)*100))/100;
                    // console.log(`Finished write stream for ${cache}.json in ${time}ms. The average write time is ${averageWriteTime}ms`);
                    // resolve();
                    stream.end();

                    stream.on('finish', () => {
                        let time = common.timer()-start;
                        this.totalWriteTime += time;
                        let averageWriteTime = Math.round(((this.totalWriteTime / this.writesCount)*100))/100;
                        console.log(`Finished write stream for ${cache}.json in ${time}ms. The average write time is ${averageWriteTime}ms`);
                        resolve();
                    });

                }

            }catch (e){
                console.log(`Error while writing cache data to disk:`, e);
                reject();
            }

        }).catch((e) => {
            common.handleError(e);
        });

    }

    loadCacheMaps = () => {
        console.log(`Loading cache maps`);
        // "cachedMatchups", "cachedShootingMatchups",
        ["cachedMatchups", "cachedShootingMatchups", "cachedTargetingMatchups"].forEach((cache) => {
            this[cache] = new Map();

            console.log(`Reading ${cache}.json`);

            let cacheString = fs.readFileSync(`${this.cacheFolder}/${cache}.json`, 'utf8');
            if (cacheString.trim() === ""){
                cacheString = "{}";
            }else{
                cacheString = `[${cacheString.slice(0, -1)}]`;
            }
            let array = Array.from(JSON.parse(cacheString));
            for (let i = 0; i < array.length; i++){
                let entry = array[i];
                this[cache].set(entry[0], entry[1]);
            }


            console.log(`Read ${cache}.json. The map has ${this[cache].size} entries.`);

            // console.log(this[cache]);
            // setTimeout(() => {
            //     console.log(`Read ${cache}.json. The map has ${this[cache].size} entries.`);
            // }, 30000);
        });
        this.saveCacheMaps();

        console.log();

    };

   /**
    *Sums all the outcomes for a matchup id and all its strategies and then inserts new records for each strategy with their sum and deletes all the old ones.
    Currently, we ignore the opponent_id for calculating the value of a strat and for inserting the new consolidated strats but at some point we probably shouldn't

    This reduces disk usage and speeds up read times by reducing the number of rows but it locks up the disk for a long time (hours)
     when there's a lot of outcomes to delete
    * @param {any} table
    * @param {any} matchup_id
    * @param {any} opponent_id
    * @param {any} game
    * @returns
    */
    consolidateOutcomes = async () => {
        // Number of items to process per batch
        const BATCH_SIZE = 1000;
        let batchQueue;
        let start = common.timer();

        //console.log(`Consolidation Queue Length: ${this.consolidationQueue.length}`);
        // Process the consolidation queue in batches
        for (let i = 0; i < this.consolidationQueue.length; i += BATCH_SIZE) {
            // Slice out the current batch
            const batch = this.consolidationQueue.slice(i, i + BATCH_SIZE);
            //console.log(`Batch Length: ${batch.length}`);
            batchQueue = [];


            batch.forEach((b) => {
                if (b.table === 0) {
                    b.table = "targeting_outcomes";
                }
                else if (b.table === 1) {
                    b.table = "shooting_outcomes";
                }
                else if (b.table === 2) {
                    b.table = "strategic_commands";
                }
            });

            const outcomePromises = batch.map(({ matchup_id, table, game }) => {
                return game.getOutcomesFromId(matchup_id, table)
            });

            const outcomeResults = await Promise.all(outcomePromises);

            // Process each item in the batch
            for (let j = 0; j < batch.length; j++) {
                const { matchup_id, table, game } = batch[j];
                const outcomes = outcomeResults[j];

                //console.log(`Outcomes Length: ${outcomes.length}`);

                // If too many outcomes exist for a strategy type, consolidate them
                if (outcomes && outcomes.length > game.config.maxOutcomesPerStratType) {
                    let values = [];

                    // Extract unique strategy IDs
                    let stratList = [...new Set(outcomes.map((o) => o.strategy_id))];
                    let outcome_value;

                    // Loop through each strategy and aggregate its outcomes
                    for (let k = 0; k < stratList.length; k++) {
                        outcome_value = game.addOutcomes(outcomes, stratList[k]);

                        // Store consolidated values: matchup_id, strategy_id, average value, number of uses
                        values.push([
                            matchup_id,
                            stratList[k],
                            Math.round(outcome_value.tsv / outcome_value.uses),
                            outcome_value.uses
                        ]);
                    }

                    // Add to batch queue for database insertion
                    batchQueue.push({ m: matchup_id, t: table, v: values });
                }
            }

            //console.log(`Batch Queue Length: ${batchQueue.length}`);
            // Group inserts by table name
            const insertsByTable = {};

            for (const task of batchQueue) {
                const { m: matchup_id, t: table, v: values } = task;

                if (!insertsByTable[table]) insertsByTable[table] = [];
                insertsByTable[table].push({ matchup_id, values });
            }

            //console.log(`Inserts by table Lengths: ${insertsByTable["strategic_outcomes"].length}`);
            // Start transaction
            let success = true;
            await this.db.query('START TRANSACTION');

            for (const [table, entries] of Object.entries(insertsByTable)) {
                try {
                    // First, delete all relevant matchup_ids for this table
                    const matchupIds = entries.map(entry => entry.matchup_id);
                    const deleteResult = await this.db.query(
                        `DELETE FROM ${table} WHERE matchup_id IN (?)`,
                        [matchupIds]
                    );
                    //console.log(`RAN SQL: DELETE FROM ${table} WHERE matchup_id IN (${matchupIds.join(', ')}) | ${deleteResult.affectedRows} results`);

                    // Sum up deleted rows
                    this.totalConsolidatedRows += deleteResult.affectedRows;

                    // Flatten all value arrays into a single array of tuples
                    const allValues = entries.flatMap(entry => entry.values);

                    // Insert all at once
                    if (allValues.length > 0) {
                        const insertResult = await this.db.query(
                            `INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome, uses) VALUES ?`,
                            [allValues]
                        );

                        //console.log(`RAN SQL: INSERT INTO ${table} (matchup_id, strategy_id, strategic_outcome, uses) VALUES ${allValues.join(', ')} | ${insertResult.affectedRows} results`);

                        this.totalConsolidatedInsertRows += insertResult.affectedRows;
                    }
                } catch (e) {
                    common.handleError(e);
                    success = false;
                    // optional: ROLLBACK here if desired
                }
            }

            // Commit after all tables are done
            if (success) {
                await this.db.query('COMMIT');
                //console.log(`Committed batch ${i / BATCH_SIZE + 1}`);

            } else {
                await this.db.query('ROLLBACK');
                console.error(`Rolled back batch ${i / BATCH_SIZE + 1} due to an error`);
            }
        }

        console.log(`Consolidated ${this.totalConsolidatedRows} rows and inserted ${this.totalConsolidatedInsertRows} rows in ${(common.timer() - start).toFixed(2)}ms`);
        this.totalConsolidatedRows = 0; // reset the counter for the next run
        this.totalConsolidatedInsertRows = 0; // reset the counter for the next run
        this.consolidationQueue = []; // clear the queue after processing
    }

    runConsolidationQueue = async () => {
        // console.log("Running consolidation queue");
        if ((this.consolidationMap.size > 0 || this.consolidationQueue.length > 0) && this.connections.size === 0) { // don't run consolidations if we're busy with requests

            // if this is the first time running the queue since the last time the queue was empty, filter the queue
            if (!this.isRunningConsolidation) {
                this.isRunningConsolidation = true;

                this.consolidationQueue = Array.from(this.consolidationMap.values()); // can't sort because we don't know the occurences till later
                this.consolidationMap = new Map();
                this.consolidationCount += this.consolidationQueue.length;


                //console.log(`It took ${common.timer() - start}ms to filter the initial queue. There were ${maxLength} entries and now there are ${this.consolidationQueue.length} entries\n\n`);
            }

            let start = common.timer();
            //console.log(`Got an entry for ${request.matchup_id} from ${request.table} off the consolidation queue. Queue is ${this.consolidationQueue.length} entries long`);

            await this.consolidateOutcomes();

            //this.consolidationQueue = this.consolidationQueue.filter((q) => {
            //    return q.matchup_id !== request.matchup_id;
            //});
            this.consolidationTime += common.timer() - start;

            setImmediate(this.runConsolidationQueue);
        } else {
            //console.log(`No entries in the consolidation queue, waiting for more entries. Or, there are active connections`);
            this.isRunningConsolidation = false;
            setTimeout(() => {
                this.runConsolidationQueue();
            }, 10000);
        }

    };

    runQueue = () => {
        let queueLength = this.queue.length;
        let request;
        let connection;
        for (let i = 0; i < queueLength; i++){
            // console.log(`server queue`, this.queue);
            if (this.queue.length > 0){
                request = this.queue.shift();
                connection = this.connections.get(request.connectionId);
                if (connection) {
                    //console.log(`Making socket connection handle request`, request.timeOnQueue, request.startTime);
                    this.recent_requests++;
                    request.messageId = this.recent_requests;
                    request.timeOnQueue = common.timer() - request.timings.startTime;
                    this.totalQueueTime += request.timeOnQueue;
                    //console.log(`Making socket connection handle request 2`, request.timeOnQueue, request.startTime);
                    connection.handleMessage(request);
                    this.requestsStarted++;
                }else{
                    console.log(`No connection with ${request.connectionId}`, this.connections);

                }
            }
        }
        setTimeout(() => {
            this.runQueue();
        }, 1);
        //setImmediate(this.runQueue);
    };

    runCacheClean = () => {
        setInterval(() => {
            const time = common.timer();
            this.cacheFilters++;
            // console.log(`Checking the cached Strategies. There are ${this.cachedStrategies.size} entries`);
            // console.log(`Checking the cached Targeting Strategies. There are ${this.cachedTargetingStrategies.size} entries`);
            // console.log(`Checking the cached Shooting Strategies. There are ${this.cachedShootingStrategies.size} entries`);
            // console.log(`Checking the cached Matchups. There are ${this.cachedMatchups.size} entries`);
            // console.log(`Checking the cached Targeting Matchups. There are ${this.cachedTargetingMatchups.size} entries`);
            // console.log(`Checking the cached Shooting Matchups. There are ${this.cachedShootingMatchups.size} entries`);

            pruneExpiredEntries(this.cachedStrategies, time, this.maxCachedStrategyAge);
            pruneExpiredEntries(this.cachedTargetingStrategies, time, this.maxCachedStrategyAge);
            pruneExpiredEntries(this.cachedShootingStrategies, time, this.maxCachedStrategyAge);

             console.log(`Filtered out old strategies. There are now ${this.cachedStrategies.size} entries`);
             console.log(`Filtered out old targeting strategies. There are now ${this.cachedTargetingStrategies.size} entries`);
            let totalTime = common.timer() - time;
             console.log(`Filtered out old shooting strategies. There are now ${this.cachedShootingStrategies.size} entries. It took ${totalTime}ms`);
            this.totalCacheFilterTime += totalTime;
            
        }, this.maxCachedStrategyAge);
    };

    runQueues = () => {
        this.runQueue();
        this.runConsolidationQueue(); 
        // this.runUpdateQueue();
    };

    start = () => {
        try {
            this.db.handleDisconnect();
            let httpServer = http.createServer({}, this.httpServerHandle).listen(this.port);
            let wsServer = new socket({
                httpServer: httpServer,
                maxReceivedFrameSize: 1073741824, // 1024*1024*1024 1 GiB
                maxReceivedMessageSize: 1073741824, // 1024*1024*1024 1 GiB
                autoAcceptConnections: false
            });
            wsServer.on('request', (request) => {this.handleWSRequest(request)});
            setTimeout(() => {
                this.loadCacheMaps();
                this.runQueues();
                this.runCacheClean();
            }, 1000);
            setInterval(() => {
                this.removeOldGames();
            }, 600000); // 10m 


        }
        catch (error) {
            console.log("\nThere was an error with either the http server or the websocket server.", error);
        }
    }

    httpServerHandle = (req, res) => {
        console.log("trying to connect with http", req, res);
    };

    handleWSRequest = (request) => {
        if (!this.originIsAllowed(request.origin) || this.consolidationQueue.length > 0) {
            request.reject();
            console.log(`\nWebsocket connection from origin ${request.origin} rejected at ${common.getLocalTime()} because either [${!this.originIsAllowed(request.origin)}] | [${this.consolidationQueue.length > 0}]`);
            return;
        }

        let id = common.nonce();
        this.connections.set(id, new SocketConnection(request.accept('game', request.origin), this.db, this, id));
        console.log(`Websocket connection from origin ${request.origin} accepted at ${common.getLocalTime()} by process #${process.pid}`);


    };

    originIsAllowed = (origin) => {
        // [alert] put logic here to detect whether the specified origin is allowed.
        return true;
    };

}

const common = new Common();

let test = false;
let port = 7143;
console.log(process.argv);
for (let i = 0; i < process.argv.length; i++){
    if (process.argv[i].toLowerCase() === "test"){
        test = true;
    }
    if (!isNaN(process.argv[i])) {
        //console.log(`Found a number in the arguments: ${process.argv[i]}`);
        port = parseInt(process.argv[i]);
        
    }
}
console.log(`Running server on ${port} with process #${process.pid}`);

//process.on('uncaughtException', function (exception) {
//    console.error(`Uncaught exception found by process at ${common.getLocalTime()}`);
//    console.error(exception); 
//});
//process.on('unhandledRejection', (exception, promise) => {
//    console.error(`Unhandled rejection found by process at ${common.getLocalTime()}`);
//    console.error(exception);
//});
//const originalExit = process.exit;
//process.exit = code => { console.log(new Error().stack); process.exit(code); }

//try {
//    let server = new Server(test);
//    console.log(`Created server`);

//} catch (e) {
//    console.error(`Error while running server:`, e);
//}

let server = new Server(test, port);


