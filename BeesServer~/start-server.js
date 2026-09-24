'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

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

    const child = spawn(process.execPath, [serverPath, ...options.serverArgs], {
        cwd: __dirname,
        detached: options.background,
        stdio,
        env,
    });

    if (log) fs.closeSync(log.fd);

    child.on('error', error => {
        console.error(`Failed to start Bees server: ${error.message}`);
        process.exitCode = 1;
    });

    if (options.background) {
        child.unref();
        const destination = log ? `; logging to ${log.path}` : '';
        console.log(`Bees server started in background with PID ${child.pid}${destination}`);
        return child;
    }

    child.on('exit', (code, signal) => {
        if (signal) process.exitCode = 1;
        else process.exitCode = code ?? 1;
    });
    return child;
}

if (require.main === module) launchServer();

module.exports = { DEVELOPMENT_DATABASE, parseLauncherOptions, openLog, launchServer };
