'use strict';

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

function runSupervisor(options) {
    const serverPath = path.join(__dirname, 'server.js');
    const env = serverEnvironment();
    const healthConfig = trainingControlProbeConfig(env);
    let child = null;
    let stopping = false;
    let restartTimer = null;
    let healthTimer = null;
    let startedAt = 0;
    let consecutiveHealthFailures = 0;

    const clearRestart = () => {
        if (restartTimer) clearTimeout(restartTimer);
        restartTimer = null;
    };

    const scheduleRestart = () => {
        if (stopping || restartTimer) return;
        restartTimer = setTimeout(() => {
            restartTimer = null;
            startChild();
        }, RESTART_DELAY_MS);
    };

    const startChild = () => {
        if (stopping) return;
        startedAt = Date.now();
        consecutiveHealthFailures = 0;
        const childArgs = [...options.serverArgs];
        if (options.managedOwnerToken) {
            childArgs.push('--bees-managed-child-token=' + options.managedOwnerToken + '.child');
        }
        child = spawn(process.execPath, [serverPath, ...childArgs], {
            cwd: __dirname,
            detached: false,
            stdio: 'inherit',
            env,
        });
        console.log(`[Bees server supervisor] started server PID ${child.pid}`);

        child.on('error', error => {
            console.error(`[Bees server supervisor] server launch failed: ${error.message}`);
        });
        child.on('exit', (code, signal) => {
            const ended = child;
            child = null;
            if (stopping) return;
            console.error(
                '[Bees server supervisor] server exited unexpectedly ' +
                `pid=${ended && ended.pid ? ended.pid : 'unknown'} code=${code ?? 'none'} signal=${signal || 'none'}; restarting`
            );
            scheduleRestart();
        });
    };

    const stop = signal => {
        if (stopping) return;
        stopping = true;
        clearRestart();
        if (healthTimer) clearInterval(healthTimer);
        healthTimer = null;
        if (child && child.exitCode === null && child.signalCode === null) {
            try {
                child.kill(signal || 'SIGTERM');
            } catch (_) {}
            const forceTimer = setTimeout(() => {
                if (child && child.exitCode === null && child.signalCode === null) {
                    try { child.kill('SIGKILL'); } catch (_) {}
                }
            }, 5000);
            forceTimer.unref();
        }
    };

    process.on('SIGINT', () => stop('SIGINT'));
    process.on('SIGTERM', () => stop('SIGTERM'));

    startChild();

    if (healthConfig) {
        healthTimer = setInterval(async () => {
            if (stopping || !child || child.exitCode !== null || child.signalCode !== null) return;
            if (Date.now() - startedAt < HEALTH_STARTUP_GRACE_MS) return;
            const healthy = await probeTrainingControl(healthConfig);
            if (healthy) {
                consecutiveHealthFailures = 0;
                return;
            }
            consecutiveHealthFailures++;
            console.error(
                `[Bees server supervisor] training-control health probe failed ${consecutiveHealthFailures}/${HEALTH_FAILURE_LIMIT}`
            );
            if (consecutiveHealthFailures >= HEALTH_FAILURE_LIMIT && child) {
                consecutiveHealthFailures = 0;
                const unhealthyChild = child;
                try { unhealthyChild.kill('SIGTERM'); } catch (_) {}
                const forceTimer = setTimeout(() => {
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
                forceTimer.unref();
            }
        }, HEALTH_INTERVAL_MS);
        healthTimer.unref();
    }
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
    trainingControlProbeConfig,
    probeTrainingControl,
    runSupervisor,
    launchServer,
};
