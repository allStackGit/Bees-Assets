'use strict';

const { spawn } = require('node:child_process');
const net = require('node:net');
const config = require('./testServerConfig');

const root = __dirname;
const env = {
    ...process.env,
    BEES_DB_HOST: config.database.host,
    BEES_DB_USER: config.database.user,
    BEES_DB_PASSWORD: config.database.password,
    BEES_DB_NAME: config.database.name,
    BEES_REQUIRE_TEST_DB: '1',
    BEES_LIVE_INTEGRATION: '1',
    BEES_TEST_SERVER_URL: config.server.url,
    BEES_TEST_USER_ID: String(config.integrationUserId),
};

function run(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, {
            cwd: root,
            env,
            stdio: 'inherit',
        });
        child.once('error', reject);
        child.once('exit', (code, signal) => {
            if (code === 0) resolve();
            else reject(new Error(`${command} ${args.join(' ')} failed (${signal || code}).`));
        });
    });
}

function isPortOpen(port, host) {
    return new Promise(resolve => {
        const socket = net.createConnection({ port, host });
        let settled = false;
        const finish = value => {
            if (settled) return;
            settled = true;
            socket.destroy();
            resolve(value);
        };
        socket.once('connect', () => finish(true));
        socket.once('error', () => finish(false));
        socket.setTimeout(1000, () => finish(false));
    });
}

async function requireFreePort(port, host) {
    if (await isPortOpen(port, host)) {
        throw new Error(
            `Port ${port} is already in use on ${host}. Stop the running Bees test server before running npm test.`);
    }
}

function waitForPort(port, host, timeoutMs = 15000) {
    const started = Date.now();
    return new Promise((resolve, reject) => {
        const attempt = () => {
            const socket = net.createConnection({ port, host });
            socket.once('connect', () => {
                socket.destroy();
                resolve();
            });
            socket.once('error', () => {
                socket.destroy();
                if (Date.now() - started >= timeoutMs) {
                    reject(new Error(`Timed out waiting for Bees test server on ${host}:${port}.`));
                    return;
                }
                setTimeout(attempt, 100);
            });
        };
        attempt();
    });
}

async function stopChild(child) {
    if (!child || child.exitCode !== null) return;
    child.kill('SIGTERM');
    await new Promise(resolve => {
        const timer = setTimeout(() => {
            if (child.exitCode === null) child.kill('SIGKILL');
            resolve();
        }, 3000);
        child.once('exit', () => {
            clearTimeout(timer);
            resolve();
        });
    });
}

async function main() {
    console.log(`Qualifying Bees test environment: ${config.database.name} @ ${config.server.host}:${config.server.port}`);

    await requireFreePort(config.server.port, config.server.host);
    await run(process.execPath, ['migrate.js', '--test']);
    await run(process.execPath, ['--check', 'server.js']);
    await run(process.execPath, ['--check', 'start-server.js']);
    await run(process.execPath, ['--check', 'rlContinualLearningLauncher.js']);
    await run(process.execPath, ['--check', 'rlContinualLearningSupervisor.js']);
    await run(process.execPath, ['--check', 'rlTrainerControl.js']);
    await run(process.execPath, ['--check', 'campaignCheckpoint.js']);
    await run(process.execPath, ['--check', 'siServerDev.js']);
    await run(process.execPath, ['--check', 'migrate.js']);

    const server = spawn(process.execPath, ['server.js', 'test', String(config.server.port)], {
        cwd: root,
        env,
        stdio: 'inherit',
    });

    let serverFailure = null;
    server.once('exit', (code, signal) => {
        if (code !== 0 && code !== null) {
            serverFailure = new Error(`Temporary Bees test server exited early (${signal || code}).`);
        }
    });

    try {
        await waitForPort(config.server.port, config.server.host);
        if (serverFailure) throw serverFailure;
        await run(process.execPath, ['--test']);
        if (serverFailure) throw serverFailure;
    } finally {
        await stopChild(server);
    }

    console.log('Bees test environment passed. Run `npm run start:test` to start the same server for Unity.');
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
