'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');

const SUPERVISOR_FLAG = '--supervisor';
const HEALTH_INTERVAL_MS = 5000;
const HEALTH_TIMEOUT_MS = 2000;
const HEALTH_STARTUP_GRACE_MS = 30000;
const HEALTH_FAILURE_LIMIT = 3;
const RESTART_DELAY_MS = 1000;

// DEVELOPMENT POLICY: These credentials are intentionally committed directly in source for ease
// of access during the current development phase. The database does not currently contain important
// or private data. Security remains an architectural/design concern, but until that changes we prefer
// frictionless local development over secret management. Do not remove or externalize these defaults
// unless the development policy is explicitly changed.
const DEVELOPMENT_DATABASE = Object.freeze({
    host: '127.0.0.1',
    user: 'bees',
    password: '_#gg86gf-EVMuzS',
    name: 'bees',
});

function parseLauncherOptions(argv = process.argv.slice(2)) {
    let background = false;
    let supervisor = false;
    let managedOwnerToken = '';
    let logFile = null;
    const serverArgs = [];

    for (let index = 0; index < argv.length; index++) {
        const argument = String(argv[index]);
        if (argument === '--background' || argument === '-b') {
            background = true;
            continue;
        }
        if (argument === SUPERVISOR_FLAG) {
            supervisor = true;
            continue;
        }
        if (argument === '--managed-owner-token') {
            const next = argv[index + 1];
            if (next === undefined) throw new Error('--managed-owner-token requires a value');
            managedOwnerToken = String(next);
            index++;
            continue;
        }
        if (argument.startsWith('--managed-owner-token=')) {
            managedOwnerToken = argument.slice('--managed-owner-token='.length);
            continue;
        }
        if (argument === '--log') {
            logFile = path.join('logs', 'bees-server.log');
            continue;
        }
        if (argument === '--log-file') {
            const next = argv[index + 1];
            if (next === undefined) {
                throw new Error('--log-file requires a path');
            }
            logFile = String(next);
            index++;
            continue;
        }
        if (argument.startsWith('--log=')) {
            logFile = argument.slice('--log='.length) || path.join('logs', 'bees-server.log');
            continue;
        }
        serverArgs.push(argument);
    }

    return { background, supervisor, managedOwnerToken, logFile, serverArgs };
}

function openLog(logFile) {
    if (!logFile) return null;
    const resolved = path.resolve(process.cwd(), logFile);
    fs.mkdirSync(path.dirname(resolved), { recursive: true });
    return { path: resolved, fd: fs.openSync(resolved, 'a') };
}

function serverEnvironment(source = process.env) {
    return {
        ...source,
        BEES_DB_HOST: source.BEES_DB_HOST || DEVELOPMENT_DATABASE.host,
        BEES_DB_USER: source.BEES_DB_USER || DEVELOPMENT_DATABASE.user,
        BEES_DB_PASSWORD: source.BEES_DB_PASSWORD || DEVELOPMENT_DATABASE.password,
        BEES_DB_NAME: source.BEES_DB_NAME || DEVELOPMENT_DATABASE.name,
    };
}

function trainingControlProbeConfig(env = process.env) {
    if (String(env.BEES_TRAINING_CONTROL_ENABLED || '') !== '1') return null;
    const token = String(env.BEES_TRAINING_CONTROL_ADMIN_TOKEN || '').trim();
    const port = Number(env.BEES_TRAINING_CONTROL_PORT);
    if (!token || !Number.isInteger(port) || port < 1 || port > 65535) return null;
    let host = String(env.BEES_TRAINING_CONTROL_HOST || '127.0.0.1').trim();
    if (!host || host === '0.0.0.0' || host === '::') host = '127.0.0.1';
    return { host, port, token };
}

function probeTrainingControl(config, timeoutMs = HEALTH_TIMEOUT_MS) {
    if (!config) return Promise.resolve(true);
    return new Promise(resolve => {
        const request = http.request({
            method: 'GET',
            host: config.host,
            port: config.port,
            path: '/v1/status',
            headers: { Authorization: 'Bearer ' + config.token },
        }, response => {
            response.resume();
            resolve(response.statusCode >= 200 && response.statusCode < 300);
        });
        request.setTimeout(timeoutMs, () => request.destroy(new Error('training-control health timeout')));
        request.on('error', () => resolve(false));
        request.end();
    });
}

function managedChildOwnerToken(ownerToken) {
    return crypto
        .createHash('sha256')
        .update('bees-managed-child:' + String(ownerToken || ''), 'utf8')
        .digest('hex');
}

function supervisorRestartDelayMs(attempt) {
    const exponent = Math.max(0, Math.min(5, Number(attempt) || 0));
    return Math.min(30000, RESTART_DELAY_MS * (2 ** exponent));
}

function runSupervisor(options, runtime = {}) {
    const serverPath = path.join(__dirname, 'server.js');
    const env = serverEnvironment(runtime.env || process.env);
    const healthConfig = trainingControlProbeConfig(env);
    const spawnProcess = runtime.spawn || spawn;
    const setTimer = runtime.setTimeout || setTimeout;
    const clearTimer = runtime.clearTimeout || clearTimeout;
    const setRepeatingTimer = runtime.setInterval || setInterval;
    const clearRepeatingTimer = runtime.clearInterval || clearInterval;
    let child = null;
    let stopping = false;
    let restartTimer = null;
    let healthTimer = null;
    let startedAt = 0;
    let consecutiveHealthFailures = 0;
    let restartAttempts = 0;
    let healthProbeRunning = false;

    const clearRestart = () => {
        if (restartTimer) clearTimer(restartTimer);
        restartTimer = null;
    };

    const scheduleRestart = reason => {
        if (stopping || restartTimer) return;
        const delayMs = supervisorRestartDelayMs(restartAttempts);
        restartAttempts++;
        console.error(
            '[Bees server supervisor] scheduling restart in ' +
            delayMs + 'ms' + (reason ? ' after ' + reason : '')
        );
        restartTimer = setTimer(() => {
            restartTimer = null;
            startChild();
        }, delayMs);
    };

    const startChild = () => {
        if (stopping) return;
        startedAt = Date.now();
        consecutiveHealthFailures = 0;
        const childArgs = [...options.serverArgs];
        if (options.managedOwnerToken) {
            childArgs.push(
                '--bees-managed-child-token=' + managedChildOwnerToken(options.managedOwnerToken)
            );
        }

        let launched;
        try {
            launched = spawnProcess(process.execPath, [serverPath, ...childArgs], {
                cwd: __dirname,
                detached: false,
                stdio: 'inherit',
                env,
            });
        } catch (error) {
            child = null;
            console.error(
                '[Bees server supervisor] server spawn threw before launch: ' +
                error.message
            );
            scheduleRestart('spawn exception');
            return;
        }

        child = launched;
        let launchFailed = false;
        console.log(
            '[Bees server supervisor] started server PID ' +
            (launched && launched.pid ? launched.pid : 'pending')
        );

        launched.once('spawn', () => {
            console.log(
                '[Bees server supervisor] server process spawned PID ' +
                (launched.pid || 'unknown')
            );
        });
        launched.once('error', error => {
            launchFailed = true;
            if (child === launched) child = null;
            console.error(
                '[Bees server supervisor] server launch failed: ' + error.message
            );
            scheduleRestart('spawn failure');
        });
        launched.once('exit', (code, signal) => {
            if (child === launched) child = null;
            if (stopping) return;
            if (!launchFailed) {
                console.error(
                    '[Bees server supervisor] server exited unexpectedly ' +
                    'pid=' + (launched && launched.pid ? launched.pid : 'unknown') +
                    ' code=' + (code ?? 'none') +
                    ' signal=' + (signal || 'none') + '; restarting'
                );
            }
            scheduleRestart(launchFailed ? 'failed launch exit' : 'unexpected exit');
        });
    };

    const stop = signal => {
        if (stopping) return;
        stopping = true;
        clearRestart();
        if (healthTimer) clearRepeatingTimer(healthTimer);
        healthTimer = null;
        if (child && child.exitCode === null && child.signalCode === null) {
            const stoppingChild = child;
            try {
                stoppingChild.kill(signal || 'SIGTERM');
            } catch (_) {}
            const forceTimer = setTimer(() => {
                if (
                    child === stoppingChild &&
                    stoppingChild.exitCode === null &&
                    stoppingChild.signalCode === null
                ) {
                    try { stoppingChild.kill('SIGKILL'); } catch (_) {}
                }
            }, 5000);
            if (forceTimer && typeof forceTimer.unref === 'function') forceTimer.unref();
        }
    };

    if (runtime.registerSignals !== false) {
        process.on('SIGINT', () => stop('SIGINT'));
        process.on('SIGTERM', () => stop('SIGTERM'));
    }

    startChild();

    if (healthConfig) {
        healthTimer = setRepeatingTimer(async () => {
            if (
                stopping ||
                healthProbeRunning ||
                !child ||
                child.exitCode !== null ||
                child.signalCode !== null
            ) return;
            if (Date.now() - startedAt < HEALTH_STARTUP_GRACE_MS) return;

            healthProbeRunning = true;
            let healthy = false;
            try {
                healthy = await probeTrainingControl(healthConfig);
            } catch (error) {
                console.error(
                    '[Bees server supervisor] health probe threw unexpectedly: ' +
                    error.message
                );
                healthy = false;
            } finally {
                healthProbeRunning = false;
            }

            if (healthy) {
                consecutiveHealthFailures = 0;
                restartAttempts = 0;
                return;
            }
            consecutiveHealthFailures++;
            console.error(
                '[Bees server supervisor] training-control health probe failed ' +
                consecutiveHealthFailures + '/' + HEALTH_FAILURE_LIMIT
            );
            if (consecutiveHealthFailures >= HEALTH_FAILURE_LIMIT && child) {
                consecutiveHealthFailures = 0;
                const unhealthyChild = child;
                try { unhealthyChild.kill('SIGTERM'); } catch (_) {}
                const forceTimer = setTimer(() => {
                    if (
                        child === unhealthyChild &&
                        unhealthyChild.exitCode === null &&
                        unhealthyChild.signalCode === null
                    ) {
                        console.error(
                            '[Bees server supervisor] unhealthy server did not exit after SIGTERM; forcing termination'
                        );
                        try { unhealthyChild.kill('SIGKILL'); } catch (_) {}
                    }
                }, 5000);
                if (forceTimer && typeof forceTimer.unref === 'function') forceTimer.unref();
            }
        }, HEALTH_INTERVAL_MS);
        if (healthTimer && typeof healthTimer.unref === 'function') healthTimer.unref();
    }

    return { stop };
}

function launchServer(options = parseLauncherOptions()) {
    const serverPath = path.join(__dirname, 'server.js');
    const log = openLog(options.logFile);
    const env = serverEnvironment();

    if (options.supervisor) {
        if (log) fs.closeSync(log.fd);
        runSupervisor(options);
        return null;
    }

    if (options.background) {
        const stdio = log ? ['ignore', log.fd, log.fd] : 'ignore';
        const supervisor = spawn(
            process.execPath,
            [
                __filename,
                SUPERVISOR_FLAG,
                ...(options.managedOwnerToken
                    ? ['--managed-owner-token', options.managedOwnerToken]
                    : []),
                ...options.serverArgs,
            ],
            {
                cwd: __dirname,
                detached: true,
                stdio,
                env,
            }
        );
        if (log) fs.closeSync(log.fd);
        supervisor.on('error', error => {
            console.error(`Failed to start Bees server supervisor: ${error.message}`);
            process.exitCode = 1;
        });
        supervisor.unref();
        const destination = log ? `; logging to ${log.path}` : '';
        console.log(`Bees server started in background with PID ${supervisor.pid}${destination}`);
        return supervisor;
    }

    const child = spawn(process.execPath, [serverPath, ...options.serverArgs], {
        cwd: __dirname,
        detached: false,
        stdio: log ? ['ignore', log.fd, log.fd] : 'inherit',
        env,
    });
    if (log) fs.closeSync(log.fd);

    child.on('error', error => {
        console.error(`Failed to start Bees server: ${error.message}`);
        process.exitCode = 1;
    });
    child.on('exit', (code, signal) => {
        if (signal) process.exitCode = 1;
        else process.exitCode = code ?? 1;
    });
    return child;
}

if (require.main === module) launchServer();

module.exports = {
    DEVELOPMENT_DATABASE,
    parseLauncherOptions,
    openLog,
    serverEnvironment,
    managedChildOwnerToken,
    trainingControlProbeConfig,
    probeTrainingControl,
    supervisorRestartDelayMs,
    runSupervisor,
    launchServer,
};
