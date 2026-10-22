'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
    ensureTokenFile,
    exists,
    loadConfig,
    paths,
    removeIfExists,
    resolveUnityEditor,
    runChecked,
    sleep,
    spawn,
    testControl,
    waitForSpawn,
} = require('./common');
const { queryUnityProcesses } = require('./build');
const { getStatus } = require('./control');
const {
    ensureLearnerPython,
    getTrainingCompatibilityFingerprint,
} = require('./runtime');
const { killProcessTree } = require('./validation');

const ARENAS_PER_ENVIRONMENT_FLAG = '--bees-rl-arenas-per-env';
const OBSERVE_VISIBLE_OBSTACLES_FLAG = '--bees-rl-observe-visible-obstacles';
const EDITOR_EXECUTE_METHOD = 'BeesRlObserveLauncher.Begin';
const EDITOR_STOP_FILE_FLAG = '--bees-rl-observe-stop-file=';

function visualEnvironmentArgs(environmentArgs) {
    const source = Array.isArray(environmentArgs)
        ? environmentArgs.map(String)
        : [];
    const result = [];
    for (let index = 0; index < source.length; index++) {
        const value = source[index];
        if (value.toLowerCase() === ARENAS_PER_ENVIRONMENT_FLAG) {
            if (index + 1 < source.length) index++;
            continue;
        }
        if (value.toLowerCase().startsWith(ARENAS_PER_ENVIRONMENT_FLAG + '=')) {
            continue;
        }
        if (value.toLowerCase() === OBSERVE_VISIBLE_OBSTACLES_FLAG) {
            continue;
        }
        result.push(value);
    }
    result.push(ARENAS_PER_ENVIRONMENT_FLAG + '=1');
    result.push(OBSERVE_VISIBLE_OBSTACLES_FLAG);
    return result;
}

function waitForExit(child) {
    // A process may exit while Unity is launching, before this observer attaches.
    // Do not wait forever for an exit event that has already fired.
    if (child.exitCode !== null || child.signalCode !== null) {
        return Promise.resolve({ code: child.exitCode, signal: child.signalCode });
    }
    return new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', (code, signal) => resolve({ code, signal }));
    });
}

function assertUnityProjectClosed() {
    const lock = path.join(paths.beesRoot, 'Temp', 'UnityLockfile');
    const processes = queryUnityProcesses(paths.beesRoot);
    if (processes.project.length) {
        const pids = processes.project.map(item => item.pid).join(', ');
        throw new Error(
            'The Bees Unity project is already open (PID(s): ' + pids + '). ' +
            "Close that Editor before running '.\\Assets\\bees.ps1 observe' so the observer can " +
            'launch it with the active training environment arguments.'
        );
    }

    if (!exists(lock)) return;
    if (!processes.all.length) {
        removeIfExists(lock);
        console.warn('Removed stale Unity lock file before observation: ' + lock);
        return;
    }

    throw new Error(
        'UnityLockfile exists for the Bees project while Unity process(es) are running (PID(s): ' +
        processes.all.join(', ') +
        '), but ownership of the project could not be proven. Close Unity and retry.'
    );
}

async function invokeObserve() {
    const config = loadConfig();
    const admin = ensureTokenFile(paths.adminTokenPath);
    assertUnityProjectClosed();

    if (!(await testControl(String(config.controlUrl), admin))) {
        throw new Error(
            'Training control is offline. The live learner policy cannot be observed.'
        );
    }

    const status = await getStatus(config, admin);
    const desired = status.desired || {};
    if (!desired.training_enabled) {
        throw new Error('Training is not enabled; there is no live learner policy to observe.');
    }

    const runId = String(desired.run_id || '').trim();
    const compatibilityKey = String(desired.compatibility_key || '').trim().toLowerCase();
    if (!runId || !compatibilityKey) {
        throw new Error('Active training status is missing run/policy compatibility identity.');
    }

    if (!exists(paths.wanTokenPath)) {
        throw new Error(
            'The active training broker authentication token is missing: ' + paths.wanTokenPath
        );
    }

    const python = ensureLearnerPython(config);
    const fingerprint = getTrainingCompatibilityFingerprint(python);
    const sourceKey = String(fingerprint.compatibility_key || '').trim().toLowerCase();
    if (!sourceKey || sourceKey !== compatibilityKey) {
        throw new Error(
            'Current Unity source does not match the active training policy compatibility. ' +
            'Observe from source matching the active run before visually judging the policy. ' +
            'source=' + sourceKey + ' active=' + compatibilityKey
        );
    }

    const environmentArgs = visualEnvironmentArgs(desired.environment_args || []);
    const observerScript = path.join(
        paths.assetsRoot,
        'Training',
        'bees_training_observe.py',
    );
    if (!exists(observerScript)) {
        throw new Error('Training observer helper is missing: ' + observerScript);
    }

    // The learner may be training-enabled but not have published its first policies.
    // In that state, opening Unity first blocks ML-Agents waiting for actions while
    // the policy driver waits for the broker. Fail before entering Play mode instead.
    runChecked(python, [
        observerScript,
        '--check-ready',
        '--broker-port', String(config.brokerPort),
        '--token-file', paths.wanTokenPath,
        '--run-id', runId,
    ], paths.assetsRoot);

    const unity = resolveUnityEditor(config);
    let observer = null;
    let editor = null;
    let stopSignalDirectory = null;
    let stopSignalPoll = null;
    let stopOnInput = null;
    let stopOnSigint = null;
    let stdinWasPaused = true;

    try {
        console.log('Observing live training policies for run ' + runId + '.');
        console.log(
            'Visual environment arguments: ' +
            (environmentArgs.length ? environmentArgs.join(' ') : '(none)')
        );
        console.log(
            'Starting the training-style policy driver. It samples the same live Torch policies ' +
            'and self-play opponents published to rollout workers, but it never registers as an ' +
            'actor or submits trajectories.'
        );

        observer = spawn(
            python,
            [
                observerScript,
                '--broker-port', String(config.brokerPort),
                '--token-file', paths.wanTokenPath,
                '--run-id', runId,
                '--timeout-wait', '600',
            ],
            {
                cwd: paths.assetsRoot,
                windowsHide: false,
                stdio: 'inherit',
            },
        );
        await waitForSpawn(observer, 'RL visual observer');

        // Unity's Editor client tries the external communicator immediately on entering Play.
        // Give the Python process time to authenticate to the broker and open the editor-port listener.
        await sleep(1000);
        if (observer.exitCode !== null || observer.signalCode !== null) {
            throw new Error(
                'RL visual policy driver exited before the Unity Editor could connect.'
            );
        }

        stopSignalDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-rl-observe-'));
        const stopSignalFile = path.join(stopSignalDirectory, 'editor-stopped.signal');
        const heartbeatFile = stopSignalFile + '.heartbeat';

        editor = spawn(
            unity,
            [
                '-projectPath', paths.beesRoot,
                '-executeMethod', EDITOR_EXECUTE_METHOD,
                EDITOR_STOP_FILE_FLAG + stopSignalFile,
                ...environmentArgs,
            ],
            {
                cwd: paths.beesRoot,
                windowsHide: false,
                stdio: 'ignore',
            },
        );
        await waitForSpawn(editor, 'Unity Editor observer');

        console.log(
            'Unity Editor is opening the RL 1v1 Training scene. Live training policy assignments ' +
            'control the two teams; Torch policies sample exploration exactly as rollout inference does.'
        );
        console.log(
            'Stop Play mode when you are finished; the policy driver will exit. The observed ' +
            'environment does not contribute experience to PPO.'
        );

        // The Editor publishes a stop signal and a separate play/edit heartbeat.
        // The heartbeat covers a missed play-mode callback; a long silent heartbeat
        // means Unity stopped making progress even if its process remains alive.
        let hasEnteredPlay = false;
        let lastHeartbeatTime = Date.now();
        const stoppedInUnity = new Promise(resolve => {
            stopSignalPoll = setInterval(() => {
                if (exists(stopSignalFile)) {
                    resolve({ source: 'unity-stopped' });
                    return;
                }
                if (exists(heartbeatFile)) {
                    const phase = fs.readFileSync(heartbeatFile, 'utf8').trim();
                    lastHeartbeatTime = fs.statSync(heartbeatFile).mtimeMs;
                    if (phase === 'playing') {
                        hasEnteredPlay = true;
                    } else if (phase === 'edit' && hasEnteredPlay) {
                        resolve({ source: 'unity-stopped' });
                        return;
                    }
                }
                if (hasEnteredPlay && Date.now() - lastHeartbeatTime > 60000) {
                    resolve({ source: 'unity-unresponsive' });
                }
            }, 250);
        });

        // A Unity window can remain hung after Close, so offer an independent
        // command to end observation without requiring the Editor callback.
        const operatorCancelled = new Promise(resolve => {
            stopOnSigint = () => resolve({ source: 'operator-cancelled' });
            process.on('SIGINT', stopOnSigint);
            if (process.stdin.isTTY) {
                stdinWasPaused = process.stdin.isPaused();
                stopOnInput = value => {
                    if (/^(q|quit)$/i.test(String(value).trim())) {
                        resolve({ source: 'operator-cancelled' });
                    }
                };
                process.stdin.on('data', stopOnInput);
                process.stdin.resume();
                console.log('Press Q then Enter (or Ctrl+C) to end observation if Unity does not respond.');
            }
        });
        const first = await Promise.race([
            waitForExit(observer).then(result => ({ source: 'observer', result })),
            waitForExit(editor).then(result => ({ source: 'editor', result })),
            stoppedInUnity,
            operatorCancelled,
        ]);

        if (first.source === 'unity-stopped' ||
            first.source === 'unity-unresponsive' ||
            first.source === 'operator-cancelled') {
            console.log(first.source === 'unity-unresponsive'
                ? 'Unity Editor heartbeat stopped for 60 seconds; ending observation.'
                : first.source === 'operator-cancelled'
                    ? 'Observation cancelled by operator.'
                    : 'Unity observation stopped; ending Python policy driver.');
            return;
        }

        if (first.source === 'editor') {
            if (observer.exitCode === null && observer.signalCode === null) {
                killProcessTree(observer);
            }
            if (first.result.code !== 0 && first.result.code !== null) {
                throw new Error(
                    'Unity Editor observation process exited with code ' + first.result.code + '.'
                );
            }
            console.log('Unity Editor closed; RL observation ended.');
            return;
        }

        if (first.result.code !== 0 && first.result.code !== null) {
            throw new Error(
                'RL visual policy driver exited with code ' + first.result.code + '.'
            );
        }
        console.log('RL observation ended. Unity Editor remains open.');
    } finally {
        if (stopSignalPoll !== null) {
            clearInterval(stopSignalPoll);
        }
        if (stopOnSigint !== null) {
            process.off('SIGINT', stopOnSigint);
        }
        if (stopOnInput !== null) {
            process.stdin.off('data', stopOnInput);
            if (stdinWasPaused) process.stdin.pause();
        }
        if (observer && observer.exitCode === null && observer.signalCode === null) {
            killProcessTree(observer);
        }
        if (stopSignalDirectory !== null) {
            removeIfExists(stopSignalDirectory, { recursive: true });
        }
    }
}

module.exports = {
    invokeObserve,
    visualEnvironmentArgs,
};
