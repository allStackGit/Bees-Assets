'use strict';

const { spawn } = require('node:child_process');
const { buildContinualLearningSpec } = require('./rlContinualLearningLauncher');

const DEFAULT_RESTART_MS = 5000;
const DEFAULT_SERVER_POLL_MS = 1000;
const SUPERVISED_SERVER_PID_ENV = 'BEES_RL_SUPERVISED_SERVER_PID';

function installContinualLearningSupervisor(server, options = {}) {
    if (!server || Object.prototype.hasOwnProperty.call(server, '__beesContinualLearningSupervisor')) {
        return server?.__beesContinualLearningSupervisor || null;
    }

    const env = options.env || process.env;
    const spec = options.spec === undefined ? buildContinualLearningSpec(env) : options.spec;
    if (!spec) {
        Object.defineProperty(server, '__beesContinualLearningSupervisor', {
            configurable: false,
            enumerable: false,
            writable: false,
            value: null,
        });
        return null;
    }

    const spawnProcess = options.spawnProcess || spawn;
    const schedule = options.schedule || setTimeout;
    const cancel = options.cancel || clearTimeout;
    const restartMs = options.restartMs ?? DEFAULT_RESTART_MS;
    if (!Number.isFinite(restartMs) || restartMs < 0) {
        throw new TypeError('Continual-learning restart delay must be a non-negative finite number.');
    }

    let child = null;
    let restartTimer = null;
    let stopped = false;
    let generation = 0;

    const clearRestart = () => {
        if (restartTimer !== null) {
            cancel(restartTimer);
            restartTimer = null;
        }
    };

    const scheduleRestart = reason => {
        if (stopped || restartTimer !== null) return;
        console.error(
            `Bees continual-learning service stopped (${reason}); restarting in ${restartMs / 1000}s.`,
        );
        restartTimer = schedule(() => {
            restartTimer = null;
            start();
        }, restartMs);
    };

    const start = () => {
        if (stopped || child) return child;
        const childGeneration = ++generation;
        let processHandle;
        try {
            processHandle = spawnProcess(spec.executable, spec.args, {
                cwd: spec.cwd,
                env,
                stdio: 'inherit',
                windowsHide: true,
            });
        } catch (error) {
            scheduleRestart(`spawn error: ${error.message}`);
            return null;
        }
        child = processHandle;
        let terminalHandled = false;
        const terminal = reason => {
            if (terminalHandled || childGeneration !== generation) return;
            terminalHandled = true;
            child = null;
            scheduleRestart(reason);
        };
        processHandle.once('error', error => terminal(`spawn error: ${error.message}`));
        processHandle.once('exit', (code, signal) => {
            terminal(signal ? `signal ${signal}` : `status ${code}`);
        });
        console.log(`Bees continual-learning service started with PID ${processHandle.pid}.`);
        return processHandle;
    };

    const stop = () => {
        if (stopped) return;
        stopped = true;
        clearRestart();
        generation++;
        const current = child;
        child = null;
        if (current && current.exitCode === null && current.signalCode === null) {
            try { current.kill(); } catch { /* best-effort shutdown */ }
        }
    };

    const supervisor = Object.freeze({
        start,
        stop,
        get child() { return child; },
        get stopped() { return stopped; },
        spec,
    });
    Object.defineProperty(server, '__beesContinualLearningSupervisor', {
        configurable: false,
        enumerable: false,
        writable: false,
        value: supervisor,
    });

    process.once('exit', stop);
    start();
    return supervisor;
}

function processIsAlive(pid, signalProcess = process.kill) {
    try {
        signalProcess(pid, 0);
        return true;
    } catch (error) {
        return error?.code === 'EPERM';
    }
}

function installServerLifetimeGuard(supervisor, serverPid, options = {}) {
    if (!supervisor) throw new TypeError('Server lifetime guard requires a continual-learning supervisor.');
    if (!Number.isSafeInteger(serverPid) || serverPid <= 0) {
        throw new TypeError('Supervised BeesServer PID must be a positive integer.');
    }
    const pollMs = options.pollMs ?? DEFAULT_SERVER_POLL_MS;
    if (!Number.isFinite(pollMs) || pollMs <= 0) {
        throw new TypeError('Server lifetime poll delay must be a positive finite number.');
    }
    const schedule = options.schedule || setInterval;
    const cancel = options.cancel || clearInterval;
    const isAlive = options.isAlive || processIsAlive;
    const onServerExit = options.onServerExit || (() => {
        process.exitCode = 0;
        setImmediate(() => process.exit(0));
    });
    let timer = null;
    const check = () => {
        if (isAlive(serverPid)) return;
        if (timer !== null) {
            cancel(timer);
            timer = null;
        }
        console.error(`BeesServer PID ${serverPid} stopped; stopping continual-learning watchdog.`);
        supervisor.stop();
        onServerExit();
    };
    timer = schedule(check, pollMs);
    timer?.unref?.();
    return Object.freeze({
        stop() {
            if (timer !== null) {
                cancel(timer);
                timer = null;
            }
        },
        check,
        serverPid,
    });
}

function runWatchdog(env = process.env, options = {}) {
    const spec = buildContinualLearningSpec(env);
    if (!spec) {
        console.error('Continual-learning watchdog started without BEES_RL_CONTINUAL_AUTOSTART=1.');
        return 2;
    }
    const host = {};
    const supervisor = installContinualLearningSupervisor(host, { env, spec });
    const rawServerPid = String(env[SUPERVISED_SERVER_PID_ENV] || '').trim();
    if (rawServerPid) {
        const serverPid = Number(rawServerPid);
        if (!Number.isSafeInteger(serverPid) || serverPid <= 0) {
            console.error(`${SUPERVISED_SERVER_PID_ENV} must be a positive integer.`);
            supervisor?.stop();
            return 2;
        }
        installServerLifetimeGuard(supervisor, serverPid, options);
    }
    return 0;
}

if (require.main === module) {
    process.exitCode = runWatchdog();
}

module.exports = {
    DEFAULT_RESTART_MS,
    DEFAULT_SERVER_POLL_MS,
    SUPERVISED_SERVER_PID_ENV,
    installContinualLearningSupervisor,
    processIsAlive,
    installServerLifetimeGuard,
    runWatchdog,
};
