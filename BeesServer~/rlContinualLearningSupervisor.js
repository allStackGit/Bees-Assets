'use strict';

const { spawn } = require('node:child_process');
const { buildContinualLearningSpec } = require('./rlContinualLearningLauncher');

const DEFAULT_RESTART_MS = 5000;

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

function runWatchdog(env = process.env) {
    const spec = buildContinualLearningSpec(env);
    if (!spec) {
        console.error('Continual-learning watchdog started without BEES_RL_CONTINUAL_AUTOSTART=1.');
        return 2;
    }
    const host = {};
    installContinualLearningSupervisor(host, { env, spec });
    return 0;
}

if (require.main === module) {
    process.exitCode = runWatchdog();
}

module.exports = {
    DEFAULT_RESTART_MS,
    installContinualLearningSupervisor,
    runWatchdog,
};
