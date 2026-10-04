'use strict';

const crypto = require('node:crypto');
const path = require('node:path');

const {
    ensureDir,
    ensureTokenFile,
    exists,
    loadConfig,
    paths,
    readJson,
    removeIfExists,
    resolveUnityEditor,
    sleep,
    spawn,
    testControl,
    waitForSpawn,
} = require('./common');
const { queryUnityProcesses } = require('./build');
const { getStatus } = require('./control');
const { requestCentralDiagnosticModelSnapshot } = require('./diagnostics');
const {
    ensureLearnerPython,
    getTrainingCompatibilityFingerprint,
} = require('./runtime');
const { killProcessTree } = require('./validation');

const ARENAS_PER_ENVIRONMENT_FLAG = '--bees-rl-arenas-per-env';
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
        result.push(value);
    }
    result.push(ARENAS_PER_ENVIRONMENT_FLAG + '=1');
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

function removeEphemeralSnapshot(modelPath) {
    const resolved = String(modelPath || '').trim();
    if (!resolved) return;
    const name = path.basename(resolved).toLowerCase();
    if (!name.startsWith('diagnostic-') || path.extname(name) !== '.onnx') return;
    removeIfExists(resolved);
}

async function invokeObserve() {
    const config = loadConfig();
    const admin = ensureTokenFile(paths.adminTokenPath);
    assertUnityProjectClosed();

    if (!(await testControl(String(config.controlUrl), admin))) {
        throw new Error(
            'Training control is offline. The live in-memory learner policy cannot be observed.'
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

    ensureDir(paths.runtimeRoot);
    const observeId = crypto.randomBytes(12).toString('hex');
    const snapshotJson = path.join(
        paths.runtimeRoot,
        'observe-model-snapshot-' + observeId + '.json',
    );
    let modelPath = '';
    let observer = null;
    let editor = null;

    try {
        await requestCentralDiagnosticModelSnapshot(status, runId, snapshotJson);
        const snapshot = readJson(snapshotJson);
        if (String(snapshot.status) !== 'succeeded') {
            throw new Error(
                'Live learner snapshot failed: ' +
                String(snapshot.reason || snapshot.error || snapshot.status || 'unknown error')
            );
        }
        modelPath = String(snapshot.model_path || '').trim();
        if (!modelPath || !exists(modelPath)) {
            throw new Error('Live learner snapshot file is unavailable: ' + modelPath);
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
        console.log(
            'Observing current learner policy at step ' + Number(snapshot.step || 0) +
            ' for run ' + runId + '.'
        );
        console.log(
            'Visual environment arguments: ' +
            (environmentArgs.length ? environmentArgs.join(' ') : '(none)')
        );
        console.log(
            'Starting the local ONNX inference driver; this does not join PPO or submit experience.'
        );

        observer = spawn(
            python,
            [
                observerScript,
                '--model', modelPath,
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
        // Give the Python process a brief head start so its editor-port listener owns the socket first.
        await sleep(1000);

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
            'Unity Editor is opening the RL 1v1 Training scene. The same snapshot controls both teams.'
        );
        console.log(
            'Stop Play mode when you are finished; the inference driver will exit and the snapshot will be removed.'
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
                'RL visual inference driver exited with code ' + first.result.code + '.'
            );
        }
        console.log('RL observation ended. Unity Editor remains open.');
    } finally {
        removeIfExists(snapshotJson);
        removeEphemeralSnapshot(modelPath);
        if (observer && observer.exitCode === null && observer.signalCode === null) {
            killProcessTree(observer);
        }
    }
}

module.exports = {
    invokeObserve,
    visualEnvironmentArgs,
};
