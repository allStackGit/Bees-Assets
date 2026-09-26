'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const {
    ensureDir,
    exists,
    findManagedProcessByOwnerToken,
    getProcessIdentity,
    getStateReferencedLivePid,
    paths,
    readJson,
    readText,
    removeIfExists,
    samePath,
    sha256File,
    sha256Text,
    sleep,
    testManagedProcessIdentity,
    testPythonCode,
    writeJsonAtomic,
    writeTextAtomic,
} = require('./common');
const { getStatus } = require('./control');
const {
    ensureLearnerPython,
    installReleaseTrainingRuntime,
    pruneReleaseTrainingRuntimes,
} = require('./runtime');

function buildCentralLearnerArgv(config, learnerPython, unity, runtimeRoot) {
    const service = path.join(runtimeRoot, 'bees_continual_elastic_wan_service.py');
    const trainerConfig = path.join(runtimeRoot, 'rl_1v1_config.yaml');
    const continualConfig = path.join(runtimeRoot, 'continual_learning_config.json');
    const telemetry = path.join(paths.trainingRoot, 'Telemetry');
    const models = path.join(paths.trainingRoot, 'Models');

    // Keep every argv item separate. No shell/string reparse is permitted in this path.
    return [
        path.resolve(learnerPython),
        service,
        '--root', paths.trainingRoot,
        '--assets-root', paths.assetsRoot,
        '--runtime-training-root', runtimeRoot,
        '--training-env', '{env}',
        '--telemetry-quarantine', telemetry,
        '--model-distribution-root', models,
        '--game-build-version', '{build_id}',
        '--run-id', '{run_id}',
        '--trainer-config', trainerConfig,
        '--continual-config', continualConfig,
        '--unity-editor', unity,
        '--unity-project-root', paths.beesRoot,
        '--generation-steps', String(config.generationSteps),
        '--num-envs', String(config.numLocalEnvs),
        '--platform', 'WindowsPlayer',
        '--bees-wan-actors', String(config.maxRemoteActors),
        '--bees-wan-min-actors', String(config.minRemoteActors),
        '--bees-wan-broker-port', String(config.brokerPort),
        '--bees-wan-auth-token-file', paths.wanTokenPath,
    ];
}

function newCentralLearnerLaunchCommand(config, learnerPython, unity, runtimeRoot) {
    const argv = buildCentralLearnerArgv(
        config,
        learnerPython,
        unity,
        runtimeRoot,
    );
    const required = [
        argv[0],
        path.join(runtimeRoot, 'bees_continual_elastic_wan_service.py'),
        path.join(runtimeRoot, 'rl_1v1_config.yaml'),
        path.join(runtimeRoot, 'continual_learning_config.json'),
    ];
    for (const item of required) {
        if (!exists(item)) throw new Error('Central release runtime is missing: ' + item);
    }
    ensureDir(path.join(paths.trainingRoot, 'Telemetry'));
    ensureDir(path.join(paths.trainingRoot, 'Models'));
    return argv;
}

function prepareCentralReleaseRuntime(config, bootstrapPython, unity, release) {
    ensureDir(paths.centralAgentInstallRoot);
    const installed = installReleaseTrainingRuntime(bootstrapPython, release, true);
    const runtimeRoot = path.resolve(String(installed.installed_root));
    const runtimeVersion = String(installed.runtime_version || '').trim().toLowerCase();
    const learnerPython = path.resolve(ensureLearnerPython(config, runtimeRoot));
    const launchCommand = newCentralLearnerLaunchCommand(
        config,
        learnerPython,
        unity,
        runtimeRoot,
    );

    writeJsonAtomic(paths.centralRuntimePointerPath, {
        schema_version: 1,
        build_id: String(release.build_id),
        runtime_version: runtimeVersion,
        runtime_root: runtimeRoot,
        python_executable: learnerPython,
        launch_command: launchCommand,
        prepared_utc: new Date().toISOString(),
    });
    writeTextAtomic(paths.centralRuntimeReadyBuildPath, String(release.build_id) + os.EOL, 'ascii');
    pruneReleaseTrainingRuntimes([runtimeRoot]);

    return {
        build_id: String(release.build_id),
        runtime_version: runtimeVersion,
        runtime_root: runtimeRoot,
        learner_python: learnerPython,
        launch_command: launchCommand,
    };
}

async function getCentralFallbackLaunchCommand(config, unity, preparedRuntime) {
    let canonicalBuild = '';
    if (exists(paths.adminTokenPath)) {
        try {
            const admin = readText(paths.adminTokenPath).trim();
            if (admin) {
                const status = await getStatus(config, admin);
                canonicalBuild = String(status.desired && status.desired.canonical_build_id || '').trim();
            }
        } catch (_) {
            canonicalBuild = '';
        }
    }

    if (!canonicalBuild || canonicalBuild === String(preparedRuntime.build_id)) {
        return {
            build_id: String(preparedRuntime.build_id),
            launch_command: [...preparedRuntime.launch_command],
        };
    }

    if (exists(paths.centralRuntimeStatePath)) {
        try {
            const state = readJson(paths.centralRuntimeStatePath);
            const stateBuild = String(state.build_id || '').trim();
            const statePython = String(state.python_executable || '').trim();
            const stateRoot = String(state.runtime_root || '').trim();
            if (stateBuild === canonicalBuild && statePython && stateRoot) {
                return {
                    build_id: stateBuild,
                    launch_command: newCentralLearnerLaunchCommand(config, statePython, unity, stateRoot),
                };
            }
        } catch (_) {}
    }

    let existing = null;
    if (exists(paths.centralAgentStatePath)) {
        try { existing = readJson(paths.centralAgentStatePath); } catch (_) {}
    }
    if (existing) {
        const existingPython = String(existing.learner_python || '').trim();
        const existingRoot = String(existing.release_runtime_root || '').trim();
        const cutoverCapable = Boolean(existing.runtime_cutover_capable);
        const fallbackBuild = String(existing.fallback_build_id || '').trim();
        if (cutoverCapable) {
            if (fallbackBuild === canonicalBuild && existingPython && existingRoot) {
                return {
                    build_id: fallbackBuild,
                    launch_command: newCentralLearnerLaunchCommand(config, existingPython, unity, existingRoot),
                };
            }
        } else {
            const currentBuildPath = path.join(paths.centralAgentInstallRoot, 'current.json');
            if (exists(currentBuildPath)) {
                try {
                    const current = readJson(currentBuildPath);
                    const currentBuild = String(current.build_id || '').trim();
                    if (currentBuild === canonicalBuild && existingPython && existingRoot) {
                        return {
                            build_id: currentBuild,
                            launch_command: newCentralLearnerLaunchCommand(config, existingPython, unity, existingRoot),
                        };
                    }
                } catch (_) {}
            }
        }
    }

    throw new Error(
        'Cannot safely restart the central supervisor while canonical build ' + canonicalBuild +
        ' differs from prepared build ' + preparedRuntime.build_id +
        ': no verified launch command bound to the canonical runtime is available.'
    );
}

function getRunningCentralAgentPid() {
    if (exists(paths.centralAgentStatePath)) {
        let state = null;
        try { state = readJson(paths.centralAgentStatePath); } catch (_) {}
        if (state && !testManagedProcessIdentity(state)) {
            const launchStatus = String(state.status || '');
            const ownerToken = String(state.owner_token || '');
            const supervisorPython = String(state.supervisor_python || '');
            if (launchStatus === 'launching' && ownerToken && supervisorPython) {
                const recovered = findManagedProcessByOwnerToken(
                    supervisorPython,
                    ownerToken,
                    'central training supervisor',
                );
                if (recovered) {
                    state = { ...state, ...recovered, status: 'active' };
                    writeJsonAtomic(paths.centralAgentStatePath, state);
                    writeTextAtomic(paths.centralAgentPidPath, String(recovered.pid), 'ascii');
                    console.log(
                        'Recovered central supervisor ownership before checkpoint-safety reconciliation (PID ' +
                        recovered.pid + ').'
                    );
                }
            }
        }
        if (state) {
            if (testManagedProcessIdentity(state)) return Number(state.pid);
            const livePid = getStateReferencedLivePid(state);
            if (livePid > 0) {
                throw new Error(
                    'Central learner state references live PID ' + livePid +
                    ' but its PID/start-time/executable identity does not match. Refusing to treat a possibly reused PID as the learner.'
                );
            }
        }
    }

    if (exists(paths.centralAgentPidPath)) {
        const legacyPid = Number(readText(paths.centralAgentPidPath).trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'Central learner PID ' + legacyPid +
                ' is recorded only in legacy PID-only state and cannot be proven to be the managed learner.'
            );
        }
    }
    return 0;
}

function assertCentralAgentCheckpointSafe() {
    const pid = getRunningCentralAgentPid();
    if (pid <= 0) return;
    let safe = false;
    if (exists(paths.centralAgentStatePath)) {
        try {
            const state = readJson(paths.centralAgentStatePath);
            safe =
                Number(state.pid) === pid &&
                testManagedProcessIdentity(state) &&
                Boolean(state.graceful_checkpoint_shutdown);
        } catch (_) {
            safe = false;
        }
    }
    if (!safe) {
        throw new Error(
            'Running central learner PID ' + pid +
            ' is not backed by checkpoint-safe verified process identity. Refusing an operation that could stop the wrong process or lose optimizer progress.'
        );
    }
}

async function stopCentralAgentGracefully(pid, timeoutSeconds = 150) {
    if (pid <= 0) return true;
    let state = null;
    if (exists(paths.centralAgentStatePath)) {
        try { state = readJson(paths.centralAgentStatePath); } catch (_) {}
    }
    if (!state || Number(state.pid) !== Number(pid)) {
        throw new Error(
            'Refusing graceful-stop request for central learner PID ' + pid +
            ' because no matching managed process identity is recorded.'
        );
    }
    if (!testManagedProcessIdentity(state)) {
        const livePid = getStateReferencedLivePid(state);
        if (livePid > 0) {
            throw new Error(
                'Refusing graceful-stop request for central learner PID ' + pid +
                ' because the PID now belongs to a different process identity.'
            );
        }
        return true;
    }

    assertCentralAgentCheckpointSafe();
    ensureDir(paths.centralAgentInstallRoot);
    removeIfExists(paths.centralAgentShutdownRequestPath);
    writeTextAtomic(paths.centralAgentShutdownRequestPath, 'stop\n');

    const deadline = Date.now() + timeoutSeconds * 1000;
    while (Date.now() < deadline) {
        if (!testManagedProcessIdentity(state)) {
            removeIfExists(paths.centralAgentShutdownRequestPath);
            return true;
        }
        await sleep(250);
    }

    throw new Error(
        'Central learner PID ' + pid + ' is still finalizing its checkpoint after ' +
        timeoutSeconds + ' seconds. Refusing forced termination; the existing learner remains authoritative.'
    );
}

function commandIdentity(bootstrapPython, agent, supervisorArgs) {
    return sha256Text(
        path.resolve(bootstrapPython) + os.EOL +
        sha256File(agent) + os.EOL +
        sha256File(paths.workerTokenPath) + os.EOL +
        supervisorArgs.map(String).join(os.EOL)
    );
}

async function startCentralAgentIfNeeded(
    config,
    bootstrapPython,
    unity,
    release,
    preparedRuntime = null,
) {
    ensureDir(paths.runtimeRoot);
    ensureDir(path.join(paths.logsRoot, 'Training'));
    ensureDir(paths.centralAgentInstallRoot);
    const outLog = path.join(paths.logsRoot, 'Training', 'central-agent.out.log');
    const errLog = path.join(paths.logsRoot, 'Training', 'central-agent.err.log');

    if (!preparedRuntime) {
        preparedRuntime = prepareCentralReleaseRuntime(config, bootstrapPython, unity, release);
    }

    const agent = path.join(paths.assetsRoot, 'Training', 'bees_training_worker_agent.py');
    if (!exists(agent)) throw new Error('Stable central training supervisor is missing: ' + agent);
    if (!testPythonCode(bootstrapPython, 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')) {
        throw new Error('Central training supervisor requires Python 3.10: ' + bootstrapPython);
    }

    const fallback = await getCentralFallbackLaunchCommand(config, unity, preparedRuntime);
    const supervisorArgs = [
        '-u', agent,
        '--server-url', String(config.controlUrl),
        '--token-file', paths.workerTokenPath,
        '--trainer-id', 'central-learner',
        '--role', 'dedicated',
        '--platform', 'WindowsPlayer',
        '--install-root', paths.centralAgentInstallRoot,
        '--runtime-ready-file', paths.centralRuntimeReadyBuildPath,
        '--runtime-cutover-pointer', paths.centralRuntimePointerPath,
        '--runtime-state-file', paths.centralRuntimeStatePath,
        '--shutdown-request-file', paths.centralAgentShutdownRequestPath,
    ];
    const commandHash = commandIdentity(bootstrapPython, agent, supervisorArgs);

    let existing = null;
    if (exists(paths.centralAgentStatePath)) {
        try { existing = readJson(paths.centralAgentStatePath); } catch (_) {}
        if (existing && !testManagedProcessIdentity(existing)) {
            const launchStatus = String(existing.status || '');
            const ownerToken = String(existing.owner_token || '');
            if (launchStatus === 'launching' && ownerToken) {
                const recovered = findManagedProcessByOwnerToken(
                    bootstrapPython,
                    ownerToken,
                    'central training supervisor',
                );
                if (recovered) {
                    existing = { ...existing, ...recovered, status: 'active' };
                    writeJsonAtomic(paths.centralAgentStatePath, existing);
                    writeTextAtomic(paths.centralAgentPidPath, String(recovered.pid), 'ascii');
                    console.log(
                        'Recovered central supervisor ownership after interrupted launch (PID ' +
                        recovered.pid + ').'
                    );
                }
            }
        }

        if (existing) {
            if (testManagedProcessIdentity(existing)) {
                if (
                    testManagedProcessIdentity(existing, bootstrapPython) &&
                    String(existing.command_hash || '') === commandHash &&
                    Boolean(existing.runtime_cutover_capable)
                ) {
                    return;
                }
                console.log(
                    'Central supervisor/control configuration changed; checkpointing the learner before replacing the verified supervisor.'
                );
                await stopCentralAgentGracefully(Number(existing.pid));
            } else {
                const livePid = getStateReferencedLivePid(existing);
                if (livePid > 0) {
                    throw new Error(
                        'Central learner state references live PID ' + livePid +
                        ' but its managed process identity does not match. Refusing to stop or replace a possibly reused PID.'
                    );
                }
                removeIfExists(paths.centralAgentStatePath);
            }
        }
    }

    if (exists(paths.centralAgentPidPath)) {
        const legacyPid = Number(readText(paths.centralAgentPidPath).trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'Central learner PID ' + legacyPid +
                ' is from legacy PID-only state. Refusing to stop it automatically because the PID may have been reused.'
            );
        }
    }

    const ownerToken = crypto.randomBytes(16).toString('hex');
    const launchArgs = [
        ...supervisorArgs,
        '--owner-token', ownerToken,
        '--',
        ...fallback.launch_command.map(String),
    ];

    const launchIntent = {
        schema_version: 4,
        status: 'launching',
        owner_token: ownerToken,
        executable_path: path.resolve(bootstrapPython),
        command_hash: commandHash,
        supervisor_python: path.resolve(bootstrapPython),
        learner_python: String(preparedRuntime.learner_python),
        release_runtime_root: String(preparedRuntime.runtime_root),
        release_runtime_version: String(preparedRuntime.runtime_version),
        runtime_cutover_pointer: paths.centralRuntimePointerPath,
        runtime_ready_file: paths.centralRuntimeReadyBuildPath,
        runtime_state_file: paths.centralRuntimeStatePath,
        runtime_cutover_capable: true,
        fallback_build_id: String(fallback.build_id),
        graceful_checkpoint_shutdown: true,
        argv_transport: 'node-spawn-array-v1',
        started_utc: new Date().toISOString(),
    };
    writeJsonAtomic(paths.centralAgentStatePath, launchIntent);
    removeIfExists(paths.centralAgentPidPath);

    const stdoutFd = fs.openSync(outLog, 'a');
    const stderrFd = fs.openSync(errLog, 'a');
    let child;
    try {
        child = spawn(bootstrapPython, launchArgs, {
            cwd: paths.assetsRoot,
            detached: true,
            windowsHide: true,
            stdio: ['ignore', stdoutFd, stderrFd],
        });
    } finally {
        fs.closeSync(stdoutFd);
        fs.closeSync(stderrFd);
    }
    if (!child.pid) throw new Error('Central supervisor process did not return a PID.');
    child.unref();

    await sleep(100);
    const identity = getProcessIdentity(child.pid);
    if (!identity || !samePath(identity.executable_path, bootstrapPython)) {
        try {
            if (process.platform === 'win32') {
                require('node:child_process').spawnSync(
                    'taskkill.exe',
                    ['/PID', String(child.pid), '/T', '/F'],
                    { windowsHide: true, stdio: 'ignore' },
                );
            } else {
                process.kill(child.pid, 'SIGKILL');
            }
        } catch (_) {}
        throw new Error('Could not establish the stable central supervisor process identity after launch.');
    }

    writeJsonAtomic(paths.centralAgentStatePath, {
        ...launchIntent,
        ...identity,
        status: 'active',
    });
    writeTextAtomic(paths.centralAgentPidPath, String(identity.pid), 'ascii');
    console.log('Stable central training supervisor started with PID ' + identity.pid + '.');
}

module.exports = {
    assertCentralAgentCheckpointSafe,
    buildCentralLearnerArgv,
    getCentralFallbackLaunchCommand,
    getRunningCentralAgentPid,
    newCentralLearnerLaunchCommand,
    prepareCentralReleaseRuntime,
    startCentralAgentIfNeeded,
    stopCentralAgentGracefully,
};
