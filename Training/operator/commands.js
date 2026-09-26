'use strict';

const fs = require('node:fs');
const path = require('node:path');

const {
    GAMEPLAY_SERVER_PORT,
    ensureTokenFile,
    exists,
    getProcessIdentity,
    getStateReferencedLivePid,
    loadConfig,
    paths,
    readJson,
    readText,
    removeUtf8BomIfPresent,
    removeIfExists,
    resolvePython,
    resolveUnityEditor,
    runChecked,
    sleep,
    stopManagedProcessTree,
    testControl,
    testManagedProcessIdentity,
    testPythonCode,
} = require('./common');
const {
    getStatus,
    publishRelease,
    setDesiredState,
    stageRelease,
    waitReleaseRollout,
} = require('./control');
const {
    archiveTrainingRun,
    commitTrainingRunPlan,
    completeForcedNewRunPlan,
    convertReleaseToForcedRunPlan,
    ensureLearnerPython,
    ensureRunLifecycleMatchesRelease,
    getLatestRelease,
    getPendingForcedNewRunPlan,
    installReleaseTrainingRuntime,
    newTrainingRunPlan,
    saveLatestRelease,
} = require('./runtime');
const {
    assertCentralAgentCheckpointSafe,
    getRunningCentralAgentPid,
    prepareCentralReleaseRuntime,
    startCentralAgentIfNeeded,
} = require('./central');
const { startBeesServerIfNeeded } = require('./server');
const {
    ensureTailnetIdentity,
    getTailnetBridgePaths,
    prepareRemoteBootstrap,
    startTailnetGatewayIfNeeded,
} = require('./tailnet');
const { assertRlEnvironmentArgsValid } = require('./validation');
const { invokeBuild } = require('./build');
const { showStatus } = require('./status');

function getEnvironmentArgs(config, options) {
    if (Array.isArray(options.envArgs) && options.envArgs.length) {
        return options.envArgs.map(String);
    }
    return Array.isArray(config.environmentArgs) ? config.environmentArgs.map(String) : [];
}

async function invokeServer() {
    const config = loadConfig();
    const worker = ensureTokenFile(paths.workerTokenPath);
    const admin = ensureTokenFile(paths.adminTokenPath);
    await startBeesServerIfNeeded(config, worker, admin);
    console.log(
        'BeesServer test mode is online on port ' + GAMEPLAY_SERVER_PORT +
        ' for Unity Editor/gameplay connections. No Unity build or Steam authentication is required.'
    );
}

async function invokeStart(options = {}) {
    const config = loadConfig();
    const worker = ensureTokenFile(paths.workerTokenPath);
    const admin = ensureTokenFile(paths.adminTokenPath);
    ensureTokenFile(paths.wanTokenPath);
    ensureTokenFile(paths.bootstrapTokenPath);

    await startBeesServerIfNeeded(config, worker, admin);

    let envArgs = getEnvironmentArgs(config, options);
    if (!exists(paths.latestReleasePath)) {
        if (options.newRun) {
            throw new Error(
                "Cannot force a new training run before the first RL build exists. Run '.\\Assets\\bees.ps1 build' first."
            );
        }
        await setDesiredState(config, admin, {
            training_enabled: false,
            environment_args: envArgs,
        });
        console.log(
            'Unified Bees server/control is online on gameplay port ' + GAMEPLAY_SERVER_PORT + '.'
        );
        console.log(
            'No training release exists yet, so no managed trainers were started. The Unity Editor can connect now.'
        );
        console.log(
            'Environment arguments: ' +
            (envArgs.length ? envArgs.join(' ') : '(none; defaults)')
        );
        await sleep(1000);
        await showStatus(config, admin, true, options.refreshSeconds || 2);
        return;
    }

    removeUtf8BomIfPresent(paths.latestReleasePath);
    let release = getLatestRelease();
    if (!release.run_id || !release.compatibility_key) {
        throw new Error(
            "Latest release predates automatic run lifecycle metadata. Run '.\\Assets\\bees.ps1 build' first."
        );
    }

    const bootstrapPython = resolvePython(config);
    if (!testPythonCode(
        bootstrapPython,
        'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)'
    )) {
        throw new Error(
            "Bees release tooling requires Python 3.10. Configured python resolved to '" +
            bootstrapPython + "'."
        );
    }

    const installedReleaseRuntime = installReleaseTrainingRuntime(
        bootstrapPython, release, true
    );
    const releaseRuntimeRoot = String(installedReleaseRuntime.installed_root);
    const python = ensureLearnerPython(config, releaseRuntimeRoot);
    if (!exists(python)) {
        throw new Error('Managed learner Python executable is missing: ' + python);
    }

    ensureRunLifecycleMatchesRelease(python, release);
    assertCentralAgentCheckpointSafe();

    let forcedPlan = getPendingForcedNewRunPlan();
    const resumeForcedNewRun = Boolean(forcedPlan);
    let outgoingRun = '';
    let environmentValidationKey = '';

    if (!resumeForcedNewRun) {
        environmentValidationKey = await assertRlEnvironmentArgsValid(
            config, release, envArgs, bootstrapPython
        );
    }

    if (resumeForcedNewRun) {
        let planBuild = String(forcedPlan.build_id || '').trim();
        const planRun = String(forcedPlan.run_id || '').trim();
        const planKey = String(forcedPlan.compatibility_key || '').trim().toLowerCase();
        const planPreviousRun = String(forcedPlan.previous_run_id || '').trim();
        const planPreviousKey = String(
            forcedPlan.previous_compatibility_key || ''
        ).trim().toLowerCase();
        const releaseBuild = String(release.build_id || '').trim();
        const releaseRun = String(release.run_id || '').trim();
        const releaseKey = String(release.compatibility_key || '').trim().toLowerCase();

        if (!planBuild) {
            planBuild = releaseBuild;
            console.warn(
                'Resuming a legacy forced-new run plan without a persisted build binding; binding this recovery attempt to latest release ' +
                releaseBuild + '.'
            );
        } else if (planBuild !== releaseBuild) {
            throw new Error(
                'Pending forced-new operation targets build ' + planBuild +
                ' but latest release is ' + releaseBuild +
                '. Refusing to guess which release should own the run.'
            );
        }

        outgoingRun = planPreviousRun;
        if (!Object.prototype.hasOwnProperty.call(forcedPlan, 'environment_args')) {
            if (options.newRun) {
                console.warn(
                    'Legacy forced-new plan has no persisted environment arguments; using the arguments supplied on this retry.'
                );
            } else {
                const resumeStatus = await getStatus(config, admin);
                envArgs = ((resumeStatus.desired && resumeStatus.desired.environment_args) || [])
                    .map(String);
                console.warn(
                    'Legacy forced-new plan has no persisted environment arguments; using the server-owned desired arguments for one-time recovery.'
                );
            }
        } else {
            envArgs = (forcedPlan.environment_args || []).map(String);
        }

        environmentValidationKey = await assertRlEnvironmentArgsValid(
            config, release, envArgs, bootstrapPython
        );

        if (releaseRun === planRun && releaseKey === planKey) {
            console.log(
                'Resuming interrupted forced new-run operation: target=' +
                planRun + ' build=' + planBuild + '.'
            );
        } else if (
            releaseRun === planPreviousRun &&
            releaseKey === planPreviousKey
        ) {
            release = convertReleaseToForcedRunPlan(
                release, forcedPlan, outgoingRun
            );
            saveLatestRelease(release);
            commitTrainingRunPlan(python);
            console.log(
                'Recovered forced new-run intent before release staging: target=' +
                planRun + ' build=' + planBuild + '.'
            );
        } else {
            throw new Error(
                'Pending forced-new operation does not match either the latest release or its recorded predecessor. plan=' +
                planRun + ' previous=' + planPreviousRun + ' release=' + releaseRun
            );
        }
    } else if (options.newRun) {
        let status = await getStatus(config, admin);
        const pending = status.desired && status.desired.pending_release;
        if (pending) {
            const pendingBuild = String(pending.build_id || '').trim();
            const pendingRun = String(pending.run_id || '').trim();
            const pendingKey = String(pending.compatibility_key || '').trim().toLowerCase();
            const pendingIncompatible = Boolean(pending.incompatible);
            const latestBuild = String(release.build_id || '').trim();
            const latestRun = String(release.run_id || '').trim();
            const latestKey = String(release.compatibility_key || '').trim().toLowerCase();

            if (
                !pendingIncompatible &&
                pendingBuild === latestBuild &&
                pendingRun === latestRun &&
                pendingKey === latestKey
            ) {
                console.log(
                    'Latest compatible release is still rolling out (phase=' + pending.phase +
                    '); waiting for build ' + pendingBuild +
                    ' to become canonical before forcing the new run.'
                );
                status = await waitReleaseRollout(
                    config, admin, pendingBuild, pendingRun, pendingKey
                );
            } else {
                throw new Error(
                    'Cannot force a new training run while a different or incompatible release rollout is pending (build=' +
                    pendingBuild + ' run=' + pendingRun + ' phase=' + pending.phase +
                    ' incompatible=' + pendingIncompatible + ').'
                );
            }
        }

        outgoingRun = String(
            status.desired && status.desired.run_id || release.run_id || ''
        ).trim();
        archiveTrainingRun(python, outgoingRun, 'forced-new-precutover');

        forcedPlan = newTrainingRunPlan(python, {
            forceNew: true,
            buildId: String(release.build_id),
            environmentArgs: envArgs,
        });
        if (
            forcedPlan.previous_run_id &&
            outgoingRun &&
            String(forcedPlan.previous_run_id) !== outgoingRun
        ) {
            throw new Error(
                'Run lifecycle state disagrees with active training run. lifecycle=' +
                forcedPlan.previous_run_id + ' active=' + outgoingRun
            );
        }

        release = convertReleaseToForcedRunPlan(
            release, forcedPlan, outgoingRun
        );
        saveLatestRelease(release);
        commitTrainingRunPlan(python);
        console.log(
            'Forcing fresh training run: ' + release.run_id +
            ' (same build ' + release.build_id + ').'
        );
    }

    const performForcedNewRun = Boolean(options.newRun || resumeForcedNewRun);
    const unity = resolveUnityEditor(config);
    const centralRuntime = prepareCentralReleaseRuntime(
        config, bootstrapPython, unity, release
    );
    ensureTailnetIdentity(config);
    prepareRemoteBootstrap(config, python, release);
    await publishRelease(config, admin, release);
    await startTailnetGatewayIfNeeded(config);
    await startCentralAgentIfNeeded(
        config, bootstrapPython, unity, release, centralRuntime
    );

    let staged;
    let desired;
    if (performForcedNewRun) {
        staged = await stageRelease(
            config, admin, release, envArgs, environmentValidationKey
        );
        desired = await setDesiredState(config, admin, { training_enabled: true });
    } else {
        const preEnvironmentStatus = await getStatus(config, admin);
        const pending = preEnvironmentStatus.desired &&
            preEnvironmentStatus.desired.pending_release;
        if (pending) {
            const pendingBuild = String(pending.build_id || '').trim();
            const pendingRun = String(pending.run_id || '').trim();
            const pendingKey = String(pending.compatibility_key || '').trim().toLowerCase();
            if (
                !pending.incompatible &&
                pendingBuild === String(release.build_id || '').trim() &&
                pendingRun === String(release.run_id || '').trim() &&
                pendingKey === String(release.compatibility_key || '').trim().toLowerCase()
            ) {
                console.log(
                    'Finishing the existing compatible release rollout before applying environment arguments.'
                );
                await waitReleaseRollout(
                    config, admin, pendingBuild, pendingRun, pendingKey
                );
            } else {
                throw new Error(
                    'Cannot change environment arguments while a different/incompatible release rollout is pending.'
                );
            }
        }

        staged = await stageRelease(
            config, admin, release, envArgs, environmentValidationKey
        );
        desired = await setDesiredState(config, admin, { training_enabled: true });
        if (staged.pending_release) {
            await waitReleaseRollout(
                config,
                admin,
                String(release.build_id),
                String(release.run_id),
                String(release.compatibility_key),
            );
        }
    }

    if (performForcedNewRun) {
        await waitReleaseRollout(
            config,
            admin,
            String(release.build_id),
            String(release.run_id),
            String(release.compatibility_key),
        );
        if (outgoingRun) {
            await sleep(2000);
            archiveTrainingRun(python, outgoingRun, 'forced-new-final');
        }
        completeForcedNewRunPlan(forcedPlan, release);
        console.log(
            'Forced new-run cutover complete. Active run: ' + release.run_id
        );
    }

    console.log(
        'Training requested: build=' + release.build_id +
        ' run=' + release.run_id + ' revision=' + desired.revision
    );
    const finalStatus = await getStatus(config, admin);
    const finalPending = finalStatus.desired &&
        finalStatus.desired.pending_release;
    if (finalPending) {
        console.log(
            'Release rollout: ' + finalPending.phase +
            ' incompatible=' + Boolean(finalPending.incompatible)
        );
    } else {
        console.log('Release rollout: complete');
    }
    console.log(
        'Environment arguments: ' +
        (envArgs.length ? envArgs.join(' ') : '(none; defaults)')
    );
    await sleep(1000);
    await showStatus(config, admin, true, options.refreshSeconds || 2);
}

async function invokeStop(options = {}) {
    const config = loadConfig();
    const admin = ensureTokenFile(paths.adminTokenPath);
    assertCentralAgentCheckpointSafe();

    if (await testControl(String(config.controlUrl), admin)) {
        const desired = await setDesiredState(
            config, admin, { training_enabled: false }
        );
        console.log('Training stop requested at revision ' + desired.revision + '.');

        const deadline = Date.now() + 180000;
        let running = [];
        while (Date.now() < deadline) {
            const status = await getStatus(config, admin);
            running = (status.trainers || []).filter(record =>
                !record.stale &&
                String(record.role) === 'dedicated' &&
                String(record.process_state) !== 'stopped'
            );
            if (!running.length) break;
            await sleep(500);
        }
        if (running.length) {
            const names = running
                .map(record => record.trainer_id + ':' + record.process_state)
                .join(', ');
            throw new Error(
                'Dedicated trainers are still finalizing after 180 seconds (' +
                names +
                '). Refusing to stop BeesServer while checkpoint/log preservation is incomplete.'
            );
        }
    } else {
        if (options.server && getRunningCentralAgentPid() > 0) {
            throw new Error(
                'Training control is offline while the central learner is still running. Refusing to stop BeesServer because checkpoint completion cannot be coordinated.'
            );
        }
        console.warn(
            'Training control is offline; dedicated workers should fail closed after lease expiry.'
        );
    }

    if (!options.server) return;

    let serverState = null;
    if (exists(paths.serverStatePath)) {
        try { serverState = readJson(paths.serverStatePath); } catch (_) {}
    }
    if (serverState) {
        if (testManagedProcessIdentity(serverState)) {
            stopManagedProcessTree(serverState, process.execPath, 'BeesServer');
            console.log('BeesServer stopped.');
        } else {
            const livePid = getStateReferencedLivePid(serverState);
            if (livePid > 0) {
                throw new Error(
                    'Refusing to stop BeesServer PID ' + livePid +
                    ' because its persisted process identity does not match the live process.'
                );
            }
        }
        removeIfExists(paths.serverPidPath);
        removeIfExists(paths.serverStatePath);
    } else if (exists(paths.serverPidPath)) {
        const legacyPid = Number(readText(paths.serverPidPath).trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'Refusing to stop legacy BeesServer PID ' + legacyPid +
                ' because PID-only ownership cannot exclude PID reuse.'
            );
        }
        removeIfExists(paths.serverPidPath);
    }

    const bridges = getTailnetBridgePaths();
    const gatewayExecutable = String(bridges.gateway_windows);
    let gatewayState = null;
    if (exists(paths.tailnetGatewayStatePath)) {
        try { gatewayState = readJson(paths.tailnetGatewayStatePath); } catch (_) {}
    }
    if (gatewayState) {
        if (testManagedProcessIdentity(gatewayState)) {
            stopManagedProcessTree(
                gatewayState, gatewayExecutable, 'embedded tailnet gateway'
            );
            console.log('Embedded Bees tailnet gateway stopped.');
        } else {
            const livePid = getStateReferencedLivePid(gatewayState);
            if (livePid > 0) {
                throw new Error(
                    'Refusing to stop embedded tailnet gateway PID ' + livePid +
                    ' because its persisted process identity does not match the live process.'
                );
            }
        }
        removeIfExists(paths.tailnetGatewayStatePath);
        removeIfExists(paths.tailnetGatewayPidPath);
        removeIfExists(paths.tailnetGatewayHealthPath);
    } else if (exists(paths.tailnetGatewayPidPath)) {
        const legacyPid = Number(readText(paths.tailnetGatewayPidPath).trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'Refusing to stop legacy tailnet gateway PID ' + legacyPid +
                ' because PID-only ownership cannot exclude PID reuse.'
            );
        }
        removeIfExists(paths.tailnetGatewayPidPath);
    }
}

async function invokeStatus(options = {}) {
    const config = loadConfig();
    const admin = ensureTokenFile(paths.adminTokenPath);
    await showStatus(
        config,
        admin,
        Boolean(options.once),
        Number(options.refreshSeconds || 2),
    );
}

async function invokeQualify() {
    const config = loadConfig();
    const python = ensureLearnerPython(config);
    const unity = resolveUnityEditor(config);
    if (!exists(paths.robustnessQualificationScript)) {
        throw new Error(
            'Training robustness qualification helper is missing: ' +
            paths.robustnessQualificationScript
        );
    }
    console.log(
        'Running local distributed-training robustness qualification. Live training/server/run state will not be changed.'
    );
    runChecked(python, [
        paths.robustnessQualificationScript,
        '--bees-root', paths.beesRoot,
        '--assets-root', paths.assetsRoot,
        '--unity-editor', unity,
    ], paths.assetsRoot);
}

module.exports = {
    getEnvironmentArgs,
    invokeBuild,
    invokeQualify,
    invokeServer,
    invokeStart,
    invokeStatus,
    invokeStop,
};
