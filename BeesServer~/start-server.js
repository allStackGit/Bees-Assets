'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { buildContinualLearningSpec } = require('./rlContinualLearningLauncher');
const {
    DEFAULT_RESTART_MS: CONTINUAL_RESTART_MS,
    installContinualLearningSupervisor,
} = require('./rlContinualLearningSupervisor');

// DEVELOPMENT POLICY: These credentials are intentionally committed directly in source for ease
// of access during the current development phase. The database does not currently contain important
// or private data. Security remains an architectural/design concern, but until that changes we prefer
// frictionless local development over secret management. Do not remove or externalize these defaults
// unless the development policy is explicitly changed.
const DEVELOPMENT_DATABASE = Object.freeze({
    host: '127.0.0.1',
    user: 'bees',
    password: '_#gg86gf-EVMuzS',
    name: 'ram',
});

function parseLauncherOptions(argv = process.argv.slice(2)) {
    let background = false;
    let logFile = null;
    const serverArgs = [];

    for (let index = 0; index < argv.length; index++) {
        const argument = String(argv[index]);
        if (argument === '--background' || argument === '-b') {
            background = true;
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

    return { background, logFile, serverArgs };
}

function openLog(logFile) {
    if (!logFile) return null;
    const resolved = path.resolve(process.cwd(), logFile);
    fs.mkdirSync(path.dirname(resolved), { recursive: true });
    return { path: resolved, fd: fs.openSync(resolved, 'a') };
}

function launchServer(options = parseLauncherOptions()) {
    const serverPath = path.join(__dirname, 'server.js');
    const log = openLog(options.logFile);
    const stdio = log
        ? ['ignore', log.fd, log.fd]
        : options.background
            ? 'ignore'
            : 'inherit';

    const env = {
        ...process.env,
        BEES_DB_HOST: process.env.BEES_DB_HOST || DEVELOPMENT_DATABASE.host,
        BEES_DB_USER: process.env.BEES_DB_USER || DEVELOPMENT_DATABASE.user,
        BEES_DB_PASSWORD: process.env.BEES_DB_PASSWORD || DEVELOPMENT_DATABASE.password,
        BEES_DB_NAME: process.env.BEES_DB_NAME || DEVELOPMENT_DATABASE.name,
    };
    // Validate autonomous-learning configuration before launching the gameplay server. If the
    // one-command learning system was explicitly enabled, incomplete host configuration is fatal.
    const continualSpec = buildContinualLearningSpec(env);

    const child = spawn(process.execPath, [serverPath, ...options.serverArgs], {
        cwd: __dirname,
        detached: options.background,
        stdio,
        env,
    });

    let continualSupervisor = null;
    let watchdog = null;
    if (continualSpec && options.background) {
        // The launcher exits after a background start, so give supervision to a detached watchdog.
        const watchdogPath = path.join(__dirname, 'rlContinualLearningSupervisor.js');
        watchdog = spawn(process.execPath, [watchdogPath], {
            cwd: __dirname,
            detached: true,
            stdio,
            env,
        });
        watchdog.on('error', error => {
            console.error(`Failed to start Bees continual-learning watchdog: ${error.message}`);
        });
        watchdog.unref();
    } else if (continualSpec) {
        continualSupervisor = installContinualLearningSupervisor({}, {
            env,
            spec: continualSpec,
            restartMs: CONTINUAL_RESTART_MS,
        });
    }

    if (log) fs.closeSync(log.fd);

    child.on('error', error => {
        continualSupervisor?.stop();
        console.error(`Failed to start Bees server: ${error.message}`);
        process.exitCode = 1;
    });

    if (options.background) {
        child.unref();
        const destination = log ? `; logging to ${log.path}` : '';
        const learning = continualSpec
            ? `; continual-learning watchdog PID ${watchdog?.pid ?? 'unknown'}`
            : '';
        console.log(`Bees server started in background with PID ${child.pid}${learning}${destination}`);
        return child;
    }

    child.on('exit', (code, signal) => {
        continualSupervisor?.stop();
        if (signal) process.exitCode = 1;
        else process.exitCode = code ?? 1;
    });
    return child;
}

if (require.main === module) launchServer();

module.exports = {
    DEVELOPMENT_DATABASE,
    CONTINUAL_RESTART_MS,
    parseLauncherOptions,
    openLog,
    launchServer,
};
