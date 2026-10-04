'use strict';

const path = require('node:path');

const {
    ensureTokenFile,
    exists,
    loadConfig,
    paths,
    removeIfExists,
    resolveUnityEditor,
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

    const unity = resolveUnityEditor(config);
    let observer = null;
    let editor = null;

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

        editor = spawn(
            unity,
            [
                '-projectPath', paths.beesRoot,
                '-executeMethod', EDITOR_EXECUTE_METHOD,
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

        const first = await Promise.race([
            waitForExit(observer).then(result => ({ source: 'observer', result })),
            waitForExit(editor).then(result => ({ source: 'editor', result })),
        ]);

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
        if (observer && observer.exitCode === null && observer.signalCode === null) {
            killProcessTree(observer);
        }
    }
}

module.exports = {
    invokeObserve,
    visualEnvironmentArgs,
};
