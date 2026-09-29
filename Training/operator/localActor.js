'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

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
    spawn,
    stopManagedProcessTree,
    testManagedProcessIdentity,
    waitForSpawn,
    writeJsonAtomic,
    writeTextAtomic,
} = require('./common');

const LOCAL_ACTOR_ENTRYPOINT = 'bees_elastic_wan_actor_worker.py';

function localActorSettings(config) {
    const value = config && typeof config.localActor === 'object' && config.localActor
        ? config.localActor
        : {};
    const enabled = Boolean(value.enabled);
    const settings = {
        enabled,
        initialEnvs: Number(value.initialEnvs ?? 4),
        minEnvs: Number(value.minEnvs ?? 1),
        maxEnvs: Number(value.maxEnvs ?? 8),
        autoTune: value.autoTune !== false,
        torchDevice: String(value.torchDevice || 'cpu'),
    };
    if (!enabled) return settings;
    for (const [name, number] of [
        ['initialEnvs', settings.initialEnvs],
        ['minEnvs', settings.minEnvs],
        ['maxEnvs', settings.maxEnvs],
    ]) {
        if (!Number.isInteger(number)) {
            throw new Error('localActor.' + name + ' must be an integer.');
        }
    }
    if (
        settings.minEnvs < 1 ||
        settings.initialEnvs < settings.minEnvs ||
        settings.maxEnvs < settings.initialEnvs ||
        settings.maxEnvs > 64
    ) {
        throw new Error(
            'localActor env bounds must satisfy 1 <= minEnvs <= initialEnvs <= maxEnvs <= 64.'
        );
    }
    if (!settings.torchDevice.trim()) {
        throw new Error('localActor.torchDevice must be non-empty.');
    }
    return settings;
}

function localActorBrokerSlots(config) {
    return localActorSettings(config).enabled ? 1 : 0;
}

function ensureLocalActorKey() {
    ensureDir(paths.localActorInstallRoot);
    if (exists(paths.localActorKeyPath)) {
        const existing = readText(paths.localActorKeyPath).trim().toLowerCase();
        if (/^[0-9a-f]{32}$/.test(existing)) return existing;
        throw new Error('Local actor key is malformed: ' + paths.localActorKeyPath);
    }
    const key = crypto.randomBytes(16).toString('hex');
    writeTextAtomic(paths.localActorKeyPath, key + os.EOL, 'ascii');
    return key;
}

function localActorTrainerId(actorKey) {
    const host = os.hostname().toLowerCase().replace(/[^a-z0-9._-]+/g, '-');
    return 'local-' + (host || 'worker') + '-' + actorKey.slice(0, 8);
}

function buildLocalActorLaunchCommand(config, preparedRuntime, actorKey) {
    const settings = localActorSettings(config);
    if (!settings.enabled) {
        throw new Error('Local actor launch requested while localActor.enabled is false.');
    }
    const rawPython = String(preparedRuntime.learner_python || '').trim();
    const rawRuntimeRoot = String(preparedRuntime.runtime_root || '').trim();
    if (!rawPython || !rawRuntimeRoot) {
        throw new Error('Prepared local actor runtime is missing Python/runtime identity.');
    }
    const python = path.resolve(rawPython);
    const runtimeRoot = path.resolve(rawRuntimeRoot);
    const actor = path.join(runtimeRoot, LOCAL_ACTOR_ENTRYPOINT);
    if (!exists(python)) throw new Error('Local actor learner Python is missing: ' + python);
    if (!exists(actor)) throw new Error('Local actor runtime entrypoint is missing: ' + actor);
    return [
        python,
        actor,
        '--actor-key', actorKey,
        '--envs', '{worker_envs}',
        '--broker-host', '127.0.0.1',
        '--broker-port', String(config.brokerPort),
        '--env', '{env}',
        '--auth-token-file', paths.wanTokenPath,
        '--torch-device', settings.torchDevice,
    ];
}

function prepareLocalActorReleaseRuntime(config, release, preparedRuntime) {
    const settings = localActorSettings(config);
    if (!settings.enabled) return null;
    const actorKey = ensureLocalActorKey();
    const launchCommand = buildLocalActorLaunchCommand(
        config,
        preparedRuntime,
        actorKey,
    );
    ensureDir(paths.localActorInstallRoot);
    writeJsonAtomic(paths.localActorRuntimePointerPath, {
        schema_version: 1,
        build_id: String(release.build_id),
        runtime_version: String(preparedRuntime.runtime_version),
        runtime_root: path.resolve(String(preparedRuntime.runtime_root)),
        python_executable: path.resolve(String(preparedRuntime.learner_python)),
        launch_command: launchCommand,
        prepared_utc: new Date().toISOString(),
    });
    writeTextAtomic(
        paths.localActorRuntimeReadyBuildPath,
        String(release.build_id) + os.EOL,
        'ascii',
    );
    return {
        actor_key: actorKey,
        trainer_id: localActorTrainerId(actorKey),
        launch_command: launchCommand,
    };
}

function localActorSupervisorCommandHash(
    bootstrapPython,
    agent,
    supervisorArgs,
    fallbackCommand,
    runtimeVersion,
) {
    return sha256Text([
        path.resolve(bootstrapPython),
        sha256File(agent),
        ...supervisorArgs.map(String),
        String(runtimeVersion || ''),
        ...fallbackCommand.map(String),
    ].join(os.EOL));
}

async function stopLocalActorGracefully(state, bootstrapPython, timeoutSeconds = 45) {
    if (!state || !testManagedProcessIdentity(state)) {
        const livePid = getStateReferencedLivePid(state);
        if (livePid > 0) {
            throw new Error(
                'Local actor state references live PID ' + livePid +
                ' but its managed process identity does not match. Refusing to stop a possibly reused PID.'
            );
        }
        return true;
    }
    ensureDir(paths.localActorInstallRoot);
    removeIfExists(paths.localActorShutdownRequestPath);
    writeTextAtomic(paths.localActorShutdownRequestPath, 'stop' + os.EOL, 'ascii');
    const deadline = Date.now() + timeoutSeconds * 1000;
    while (Date.now() < deadline) {
        if (!testManagedProcessIdentity(state)) return true;
        await sleep(250);
    }
    stopManagedProcessTree(state, bootstrapPython, 'local training actor');
    await sleep(250);
    if (testManagedProcessIdentity(state)) {
        throw new Error('Local actor process tree remained alive after managed termination.');
    }
    return true;
}

async function stopConfiguredLocalActor(config, bootstrapPython) {
    const settings = localActorSettings(config);
    if (settings.enabled || !exists(paths.localActorStatePath)) return;
    let state = null;
    try { state = readJson(paths.localActorStatePath); } catch (_) {}
    if (state) await stopLocalActorGracefully(state, bootstrapPython);
    removeIfExists(paths.localActorStatePath);
    removeIfExists(paths.localActorPidPath);
}

async function startLocalActorIfNeeded(
    config,
    bootstrapPython,
    release,
    preparedRuntime,
) {
    const settings = localActorSettings(config);
    if (!settings.enabled) {
        await stopConfiguredLocalActor(config, bootstrapPython);
        return null;
    }
    const prepared = prepareLocalActorReleaseRuntime(
        config,
        release,
        preparedRuntime,
    );
    const agent = path.join(paths.assetsRoot, 'Training', 'bees_training_worker_agent.py');
    if (!exists(agent)) throw new Error('Local actor worker supervisor is missing: ' + agent);

    const supervisorArgs = [
        '-u', agent,
        '--server-url', String(config.controlUrl),
        '--token-file', paths.workerTokenPath,
        '--trainer-id', prepared.trainer_id,
        '--role', 'dedicated',
        '--platform', process.platform === 'win32' ? 'WindowsPlayer' : 'LinuxPlayer',
        '--install-root', paths.localActorBuildRoot,
        '--runtime-ready-file', paths.localActorRuntimeReadyBuildPath,
        '--runtime-cutover-pointer', paths.localActorRuntimePointerPath,
        '--runtime-cutover-entrypoint', LOCAL_ACTOR_ENTRYPOINT,
        '--runtime-state-file', paths.localActorRuntimeStatePath,
        '--shutdown-request-file', paths.localActorShutdownRequestPath,
        '--worker-envs', String(settings.initialEnvs),
        '--worker-envs-min', String(settings.minEnvs),
        '--worker-envs-max', String(settings.maxEnvs),
    ];
    if (settings.autoTune) supervisorArgs.push('--auto-worker-envs');

    const commandHash = localActorSupervisorCommandHash(
        bootstrapPython,
        agent,
        supervisorArgs,
        prepared.launch_command,
        preparedRuntime.runtime_version,
    );

    let existing = null;
    if (exists(paths.localActorStatePath)) {
        try { existing = readJson(paths.localActorStatePath); } catch (_) {}
        if (existing && !testManagedProcessIdentity(existing)) {
            const ownerToken = String(existing.owner_token || '');
            const launchStatus = String(existing.status || '');
            if (launchStatus === 'launching' && ownerToken) {
                const recovered = findManagedProcessByOwnerToken(
                    bootstrapPython,
                    ownerToken,
                    'local training actor',
                );
                if (recovered) {
                    existing = { ...existing, ...recovered, status: 'active' };
                    writeJsonAtomic(paths.localActorStatePath, existing);
                    writeTextAtomic(paths.localActorPidPath, String(recovered.pid), 'ascii');
                }
            }
        }
        if (existing && testManagedProcessIdentity(existing)) {
            if (
                testManagedProcessIdentity(existing, bootstrapPython) &&
                String(existing.command_hash || '') === commandHash
            ) {
                return prepared.trainer_id;
            }
            await stopLocalActorGracefully(existing, bootstrapPython);
        } else if (existing) {
            const livePid = getStateReferencedLivePid(existing);
            if (livePid > 0) {
                throw new Error(
                    'Local actor state references live PID ' + livePid +
                    ' but ownership does not match. Refusing automatic replacement.'
                );
            }
        }
        removeIfExists(paths.localActorStatePath);
        removeIfExists(paths.localActorPidPath);
    }

    if (exists(paths.localActorPidPath)) {
        const legacyPid = Number(readText(paths.localActorPidPath).trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'Local actor PID ' + legacyPid +
                ' is from legacy PID-only state and cannot be safely replaced automatically.'
            );
        }
        removeIfExists(paths.localActorPidPath);
    }

    ensureDir(paths.localActorInstallRoot);
    ensureDir(path.dirname(paths.localActorOutLogPath));
    removeIfExists(paths.localActorShutdownRequestPath);

    const ownerToken = crypto.randomBytes(16).toString('hex');
    const launchArgs = [
        ...supervisorArgs,
        '--owner-token', ownerToken,
        '--',
        ...prepared.launch_command,
    ];
    const launchIntent = {
        schema_version: 1,
        status: 'launching',
        owner_token: ownerToken,
        executable_path: path.resolve(bootstrapPython),
        command_hash: commandHash,
        trainer_id: prepared.trainer_id,
        release_runtime_version: String(preparedRuntime.runtime_version),
        runtime_cutover_pointer: paths.localActorRuntimePointerPath,
        runtime_ready_file: paths.localActorRuntimeReadyBuildPath,
        runtime_state_file: paths.localActorRuntimeStatePath,
        started_utc: new Date().toISOString(),
    };
    writeJsonAtomic(paths.localActorStatePath, launchIntent);

    const stdoutFd = fs.openSync(paths.localActorOutLogPath, 'a');
    const stderrFd = fs.openSync(paths.localActorErrLogPath, 'a');
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
    await waitForSpawn(child, 'Local training actor supervisor');
    if (!child.pid) throw new Error('Local actor supervisor did not return a PID.');
    child.unref();

    await sleep(100);
    const identity = getProcessIdentity(child.pid);
    if (!identity) {
        throw new Error(
            'Local actor supervisor exited before its managed process identity could be established.'
        );
    }
    if (!samePath(identity.executable_path, bootstrapPython)) {
        throw new Error(
            'Local actor supervisor PID was observed with an unexpected executable; refusing PID-only cleanup.'
        );
    }

    writeJsonAtomic(paths.localActorStatePath, {
        ...launchIntent,
        ...identity,
        status: 'active',
    });
    writeTextAtomic(paths.localActorPidPath, String(identity.pid), 'ascii');
    console.log(
        'Local training actor started: ' + prepared.trainer_id +
        ' pid=' + identity.pid +
        ' envs=' + settings.initialEnvs +
        (settings.autoTune
            ? ' auto=' + settings.minEnvs + '-' + settings.maxEnvs
            : ' fixed')
        + '.'
    );
    return prepared.trainer_id;
}

module.exports = {
    LOCAL_ACTOR_ENTRYPOINT,
    buildLocalActorLaunchCommand,
    localActorBrokerSlots,
    localActorSettings,
    localActorTrainerId,
    prepareLocalActorReleaseRuntime,
    startLocalActorIfNeeded,
    stopLocalActorGracefully,
};
