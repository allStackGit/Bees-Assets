'use strict';

const fs = require('node:fs');
const crypto = require('node:crypto');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const {
    ensureDir,
    ensureTokenFile,
    invokePythonJson,
    exists,
    getGitShortSha,
    getProcessIdentity,
    paths,
    powershellExecutable,
    readJson,
    readTail,
    readText,
    removeIfExists,
    resolvePython,
    resolveUnityEditor,
    runChecked,
    runSync,
    sleep,
    testControl,
    writeJsonAtomic,
} = require('./common');
const {
    commitTrainingRunPlan,
    getLatestRelease,
    getPendingForcedNewRunPlan,
    getTrainingCompatibilityFingerprint,
    resolveReleaseTrainingRuntime,
    newReleaseTrainingRuntime,
    newTrainingRunPlan,
    recoverTrainingRunLifecycle,
    saveLatestRelease,
    ensureRunLifecycleMatchesRelease,
} = require('./runtime');
const {
    getStatus,
    publishRelease,
    stageRelease,
    waitReleaseRollout,
} = require('./control');
const {
    assertCentralAgentCheckpointSafe,
    prepareCentralReleaseRuntime,
    startCentralAgentIfNeeded,
} = require('./central');
const {
    buildTailnetBridge,
    ensureTailnetIdentity,
    getTailnetBridgeSourceHash,
    prepareRemoteBootstrap,
    startTailnetGatewayIfNeeded,
} = require('./tailnet');
const { startBeesServerIfNeeded } = require('./server');
const { assertRlEnvironmentArgsValid } = require('./validation');
const {
    prepareLocalActorReleaseRuntime,
    startLocalActorIfNeeded,
} = require('./localActor');

function formatLocalDate(date = new Date()) {
    const year = String(date.getFullYear()).padStart(4, '0');
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return year + '-' + month + '-' + day;
}

function formatLocalTime(date = new Date()) {
    return String(date.getHours()).padStart(2, '0') +
        String(date.getMinutes()).padStart(2, '0') +
        String(date.getSeconds()).padStart(2, '0');
}

function resetBuildDirectory(directory, force) {
    if (exists(directory)) {
        const notEmpty = fs.readdirSync(directory).length > 0;
        if (notEmpty && !force) {
            throw new Error('Build directory is not empty: ' + directory + ". Use -Force to replace today's build.");
        }
        if (force) removeIfExists(directory, { recursive: true });
    }
    ensureDir(directory);
}

function queryUnityProcesses(projectPath) {
    if (process.platform !== 'win32') return { project: [], all: [] };
    const ps = powershellExecutable();
    const env = { ...process.env, BEES_UNITY_PROJECT: path.resolve(projectPath) };
    const script = [
        '$n=[IO.Path]::GetFullPath($env:BEES_UNITY_PROJECT).TrimEnd([char[]]"\\\\/");',
        '$p=@();try{$p=@(Get-CimInstance Win32_Process -Filter "Name = \'Unity.exe\'" -ErrorAction Stop)}catch{};',
        '$live=@(Get-Process -Name \'Unity\' -ErrorAction SilentlyContinue);',
        '$v=[ordered]@{',
        'all=@($live|ForEach-Object{[int]$_.Id});',
        'project=@($p|Where-Object{$_.CommandLine -and ([string]$_.CommandLine).IndexOf($n,[StringComparison]::OrdinalIgnoreCase) -ge 0}|ForEach-Object{[ordered]@{pid=[int]$_.ProcessId;command_line=[string]$_.CommandLine}})',
        '};$v|ConvertTo-Json -Compress -Depth 4',
    ].join('');
    const result = runSync(
        ps,
        ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
        { env, check: false, stdio: ['ignore', 'pipe', 'ignore'] },
    );
    if (result.status !== 0 || !String(result.stdout || '').trim()) return { project: [], all: [] };
    try {
        const value = JSON.parse(String(result.stdout).trim());
        return {
            project: Array.isArray(value.project) ? value.project : value.project ? [value.project] : [],
            all: Array.isArray(value.all) ? value.all : value.all ? [value.all] : [],
        };
    } catch (_) {
        return { project: [], all: [] };
    }
}

function assertUnityProjectAvailableForBatchBuild() {
    const lock = path.join(paths.beesRoot, 'Temp', 'UnityLockfile');
    if (!exists(lock)) return;

    const processes = queryUnityProcesses(paths.beesRoot);
    if (!exists(lock)) return;

    if (processes.project.length) {
        const pids = processes.project.map(item => item.pid).join(', ');
        throw new Error(
            "The Bees Unity project is open in a live Unity Editor process (PID(s): " +
            pids + "). Close that Editor before running '.\\Assets\\bees.ps1 build'."
        );
    }

    if (!processes.all.length) {
        removeIfExists(lock);
        console.warn('Removed stale Unity lock file because no Unity Editor process is running: ' + lock);
        return;
    }

    throw new Error(
        'UnityLockfile exists for the Bees project, and Unity process(es) are running (PID(s): ' +
        processes.all.join(', ') +
        '), but their command lines could not be proven to own ' + paths.beesRoot +
        '. Refusing to remove the lock automatically. Close Unity and retry; if the lock still exists after all Unity processes exit, the next build will remove it as stale.'
    );
}

function getUnityBuildProgressStatus(logPath) {
    if (!exists(logPath)) return 'Starting Unity';
    const lines = readTail(logPath, 120, 512 * 1024);
    for (let index = lines.length - 1; index >= 0; index--) {
        const line = String(lines[index] || '').trim();
        let match;
        if ((match = line.match(/^Opening scene '(.+)'$/))) {
            return 'Processing scene: ' + path.basename(match[1]);
        }
        if ((match = line.match(/^Importing '[^']+ - Path: (.+)'/))) {
            return 'Importing: ' + match[1];
        }
        if (/shader.*compil|compil.*shader/i.test(line)) return 'Compiling shaders';
        if (/script.*compil|compil.*script/i.test(line)) return 'Compiling scripts';
        if (/SpriteAtlasPacking/i.test(line)) return 'Packing sprite atlases';
        if (/Asset Pipeline Refresh/i.test(line)) return 'Refreshing assets';
        if (/building player|buildpipeline|player build/i.test(line)) return 'Building player';
        if (/copying|copy file|copy files/i.test(line)) return 'Copying build files';
        if (/Build Finished|result=Succeeded|Batchmode quit/i.test(line)) return 'Finalizing build';
    }
    return 'Building player';
}

function waitForChild(child) {
    return new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', (code, signal) => resolve({ code, signal }));
    });
}

function findFilesByName(root, name, limit = 20) {
    const found = [];
    if (!exists(root)) return found;
    const queue = [root];
    while (queue.length && found.length < limit) {
        const current = queue.shift();
        let entries = [];
        try { entries = fs.readdirSync(current, { withFileTypes: true }); } catch (_) { continue; }
        for (const entry of entries) {
            const full = path.join(current, entry.name);
            if (entry.isDirectory()) queue.push(full);
            else if (entry.isFile() && entry.name === name) found.push(full);
        }
    }
    return found;
}

async function invokeUnityBuild(unity, method, output, entrypoint, logName) {
    const logRoot = path.join(paths.logsRoot, 'Build');
    ensureDir(logRoot);
    const logPath = path.join(logRoot, logName);
    const stagingRoot = path.join(paths.runtimeRoot, 'BuildStaging');
    ensureDir(stagingRoot);
    const stageName = method.replace(/[^A-Za-z0-9_.-]/g, '_');
    const staging = path.join(stagingRoot, stageName);
    removeIfExists(staging, { recursive: true });
    ensureDir(staging);

    const args = [
        '-batchmode',
        '-quit',
        '-projectPath', paths.beesRoot,
        '-executeMethod', method,
        '-beesOutput', staging,
        '-logFile', logPath,
    ];
    console.log('Unity: ' + method + ' -> ' + output);

    // Keep argv structured. In particular, paths under "Program Files" and B:\Bees are never
    // flattened into a command string and reparsed by PowerShell.
    const child = spawn(unity, args, {
        cwd: paths.beesRoot,
        windowsHide: true,
        stdio: 'ignore',
    });
    const started = Date.now();
    let lastStatus = '';
    const timer = setInterval(() => {
        const phase = getUnityBuildProgressStatus(logPath);
        if (phase !== lastStatus || Date.now() - started < 5000) {
            const seconds = Math.floor((Date.now() - started) / 1000);
            console.log('Unity build: ' + phase + ' - elapsed ' + seconds + 's');
            lastStatus = phase;
        }
    }, 5000);
    timer.unref();

    let result;
    try {
        result = await waitForChild(child);
    } finally {
        clearInterval(timer);
    }
    if (result.code !== 0) {
        const tail = exists(logPath) ? readTail(logPath, 60, 1024 * 1024).join(os.EOL) : '';
        throw new Error(
            unity + ' exited with code ' + String(result.code) + '.' +
            (tail ? os.EOL + 'Last Unity build log lines:' + os.EOL + tail : ' Check ' + logPath)
        );
    }

    const stagedEntrypoint = path.join(staging, entrypoint);
    const deadline = Date.now() + 30000;
    while (!exists(stagedEntrypoint) && Date.now() < deadline) await sleep(250);
    if (!exists(stagedEntrypoint)) {
        const found = findFilesByName(paths.buildsRoot, entrypoint);
        const tail = exists(logPath) ? readTail(logPath, 40, 1024 * 1024).join(os.EOL) : '';
        throw new Error(
            'Unity exited without producing the expected staged build entrypoint: ' + stagedEntrypoint +
            (found.length ? os.EOL + 'Matching executable(s) found elsewhere:' + os.EOL + found.join(os.EOL) : '') +
            (tail ? os.EOL + 'Last Unity build log lines:' + os.EOL + tail : '')
        );
    }

    removeIfExists(output, { recursive: true });
    ensureDir(path.dirname(output));
    fs.renameSync(staging, output);
}

function packageBuild(python, source, archive, entrypoint) {
    ensureDir(path.dirname(archive));
    removeIfExists(archive);
    runChecked(python, [
        path.join(paths.assetsRoot, 'Training', 'bees_package_training_build.py'),
        '--source', source,
        '--output', archive,
        '--entrypoint', entrypoint,
    ], paths.assetsRoot);
}

async function getActiveRunId(config, adminToken = '') {
    if (adminToken) {
        try {
            if (await testControl(String(config.controlUrl), adminToken)) {
                const status = await getStatus(config, adminToken);
                if (status.desired && status.desired.run_id) {
                    return String(status.desired.run_id).trim();
                }
            }
        } catch (_) {}
    }
    if (exists(paths.runStatePath)) {
        try {
            const runId = String(readJson(paths.runStatePath).run_id || '').trim();
            if (runId) return runId;
        } catch (_) {}
    }
    if (exists(paths.latestReleasePath)) {
        try {
            const runId = String(readJson(paths.latestReleasePath).run_id || '').trim();
            if (runId) return runId;
        } catch (_) {}
    }
    return '';
}

async function reconcileLatestReleaseBeforeBuild(config, python, unity, adminToken, release) {
    ensureRunLifecycleMatchesRelease(python, release);
    const preflight = await getStatus(config, adminToken);
    const environmentArgs = ((preflight.desired && preflight.desired.environment_args) || []).map(String);
    const validationKey = await assertRlEnvironmentArgsValid(
        config, release, environmentArgs, python
    );
    const releaseBuild = String(release.build_id || '').trim();
    const releaseRun = String(release.run_id || '').trim();
    const releaseKey = String(release.compatibility_key || '').trim().toLowerCase();
    const preflightDesired = preflight.desired || {};
    const preflightPending = preflightDesired.pending_release;
    if (
        !preflightPending &&
        String(preflightDesired.canonical_build_id || '').trim() === releaseBuild &&
        String(preflightDesired.run_id || '').trim() === releaseRun &&
        String(preflightDesired.compatibility_key || '').trim().toLowerCase() === releaseKey
    ) {
        return {};
    }

    const centralRuntime = await prepareCentralReleaseRuntime(config, python, unity, release);
    await startCentralAgentIfNeeded(config, python, unity, release, centralRuntime);
    const status = await getStatus(config, adminToken);
    const pending = status.desired && status.desired.pending_release;

    if (pending) {
        const pendingBuild = String(pending.build_id || '').trim();
        const pendingRun = String(pending.run_id || '').trim();
        const pendingKey = String(pending.compatibility_key || '').trim().toLowerCase();
        if (pendingBuild !== releaseBuild || pendingRun !== releaseRun || pendingKey !== releaseKey) {
            throw new Error(
                'Training control has a pending release that differs from latest release metadata. pending=' +
                pendingBuild + '/' + pendingRun + ' latest=' + releaseBuild + '/' + releaseRun
            );
        }

        const trainers = Array.isArray(status.trainers) ? status.trainers : [];
        const central = trainers.find(
            record => String(record.trainer_id || '') === 'central-learner'
        );
        const centralState = central ? String(central.process_state || '') : '';
        const centralError = central ? String(central.last_error || '').trim() : '';
        const repairableCompatibleStall =
            !Boolean(pending.incompatible) &&
            !Boolean(release.incompatible) &&
            centralError &&
            centralState !== 'running';

        if (repairableCompatibleStall) {
            console.warn(
                'Previous compatible release ' + releaseBuild +
                ' is still pending while the central learner is unhealthy (' +
                centralState + ': ' + centralError +
                '); compiling a newer same-run compatible repair release that may explicitly ' +
                'supersede this exact pending build.'
            );
            return { supersedeCompatibleBuildId: releaseBuild };
        }

        console.log(
            'Previous release is still rolling out (phase=' + pending.phase +
            '); finishing build ' + releaseBuild + ' before compiling another release.'
        );
        await waitReleaseRollout(config, adminToken, releaseBuild, releaseRun, releaseKey);
        return {};
    }

    const desired = status.desired || {};
    if (
        String(desired.canonical_build_id || '').trim() === releaseBuild &&
        String(desired.run_id || '').trim() === releaseRun &&
        String(desired.compatibility_key || '').trim().toLowerCase() === releaseKey
    ) {
        return {};
    }

    console.log(
        'Latest release ' + releaseBuild +
        ' was persisted but is not canonical; reconciling it before compiling another release.'
    );
    ensureTailnetIdentity(config);
    prepareRemoteBootstrap(config, python, release);
    await publishRelease(config, adminToken, release);
    await startTailnetGatewayIfNeeded(config);
    const staged = await stageRelease(
        config, adminToken, release, environmentArgs, validationKey
    );
    if (staged.pending_release) {
        await waitReleaseRollout(config, adminToken, releaseBuild, releaseRun, releaseKey);
    } else {
        const after = await getStatus(config, adminToken);
        const afterDesired = after.desired || {};
        if (
            String(afterDesired.canonical_build_id || '').trim() !== releaseBuild ||
            String(afterDesired.run_id || '').trim() !== releaseRun ||
            String(afterDesired.compatibility_key || '').trim().toLowerCase() !== releaseKey
        ) {
            throw new Error(
                'Previous release reconciliation returned without making ' +
                releaseBuild + '/' + releaseRun + ' canonical.'
            );
        }
    }
    return {};
}

// A Unity-only maintenance rebuild never runs the ordinary build planner:
// that planner can select a new run and repackage mutable learner source.
// The paused experiment owns the same optimizer, run, Python runtime and
// frozen trainer config while Unity game behavior may intentionally change.
async function invokeMaintenanceUnityRebuild(options = {}) {
    const config = require('./common').loadConfig();
    const python = resolvePython(config);
    const release = getLatestRelease();
    const expected = String(options.expectedRunId || '');
    if (!expected || release.run_id !== expected ||
        release.build_id !== options.expectedBuildId ||
        release.experiment_config?.sha256 !== options.expectedConfigSha) {
        throw Error('Unity rebuild requires the exact paused experiment run, build and trainer configuration.');
    }
    if (getPendingForcedNewRunPlan()) {
        throw Error('Pending forced-new-run plan prevents a same-run Unity maintenance rebuild.');
    }
    ensureRunLifecycleMatchesRelease(python, release);
    const admin = readText(paths.adminTokenPath).trim();
    const status = await getStatus(config, admin);
    const desired = status.desired || {};
    if (desired.training_enabled || desired.pending_release ||
        desired.run_id !== release.run_id ||
        desired.canonical_build_id !== release.build_id ||
        desired.compatibility_key !== release.compatibility_key) {
        throw Error('Unity rebuild requires stopped training on the exact canonical build with no pending release.');
    }
    const originalKey = String(release.compatibility_key || '').toLowerCase();
    const checkKey = () => String(
        getTrainingCompatibilityFingerprint(python).compatibility_key || ''
    ).toLowerCase();
    if (checkKey() !== originalKey) {
        throw Error('Unity source changes the RL compatibility contract. Rebuild refused to protect the existing optimizer.');
    }
    assertUnityProjectAvailableForBatchBuild();
    const unity = resolveUnityEditor(config);
    const stamp = new Date();
    const sha = getGitShortSha();
    const buildId = formatLocalDate(stamp) + '-' + formatLocalTime(stamp) +
        '-unity-maintenance-' + sha + '-' + crypto.randomBytes(3).toString('hex');
    const packageRoot = path.join(paths.buildsRoot, 'Packages', buildId);
    const buildRoot = path.join(paths.buildsRoot, 'MaintenanceUnity', buildId);
    const windows = path.join(buildRoot, 'Windows');
    const linux = path.join(buildRoot, 'Linux');
    ensureDir(packageRoot);
    const pinned = resolveReleaseTrainingRuntime(python, release);
    const rebaseScript = path.join(paths.assetsRoot, 'Training', 'bees_pinned_runtime_rebase.py');
    const rebase = (source, archive, oldSha) => invokePythonJson(python, [
        rebaseScript,
        '--source', source, '--source-sha256', oldSha,
        '--old-build-id', release.build_id, '--new-build-id', buildId,
        '--source-commit', sha, '--output', archive,
    ]);
    const trainingRuntime = rebase(pinned.archive,
        path.join(packageRoot, 'training-runtime.zip'), pinned.archive_sha256);
    if (trainingRuntime.runtime_version !== pinned.runtime_version) {
        throw Error('Unity-only build modified the pinned learner runtime.');
    }
    // If the dashboard previously installed updated remote supervisors,
    // carry those same bytes forward independently of the frozen learner.
    const overridePath = path.join(paths.runtimeRoot, 'worker-runtime-override.json');
    let workerOverride = null;
    if (exists(overridePath)) {
        const previous = readJson(overridePath);
        if (previous.run_id === release.run_id &&
            previous.build_id === release.build_id &&
            previous.compatibility_key === release.compatibility_key) {
            const workerRuntime = previous.training_runtime;
            if (!workerRuntime?.archive || !workerRuntime?.archive_sha256) {
                throw Error('Existing remote-only runtime metadata is incomplete; refusing to downgrade workers.');
            }
            const rebasedWorker = rebase(workerRuntime.archive,
                path.join(packageRoot, 'worker-overlay.zip'), workerRuntime.archive_sha256);
            workerOverride = {
                run_id: release.run_id, build_id: buildId,
                compatibility_key: release.compatibility_key,
                training_runtime: rebasedWorker,
            };
        }
    }

    await invokeUnityBuild(unity, 'BeesCommandLineBuild.BuildWindowsRl',
        windows, 'Bees RL Training.exe', buildId + '-rl-windows.log');
    await invokeUnityBuild(unity, 'BeesCommandLineBuild.BuildLinuxRl',
        linux, 'Bees RL Training.x86_64', buildId + '-rl-linux.log');
    if (checkKey() !== originalKey) {
        throw Error('RL compatibility contract changed during Unity compilation. The old release remains active.');
    }
    const windowsZip = path.join(packageRoot, 'rl-windows.zip');
    const linuxZip = path.join(packageRoot, 'rl-linux.zip');
    packageBuild(python, windows, windowsZip, 'Bees RL Training.exe');
    packageBuild(python, linux, linuxZip, 'Bees RL Training.x86_64');
    const candidate = {
        ...release,
        build_id: buildId,
        source_commit: sha,
        created_utc: new Date().toISOString(),
        previous_run_id: release.previous_run_id,
        incompatible: false,
        preserve_run_override: false,
        training_runtime: trainingRuntime,
        artifacts: [
            { role: 'dedicated', platform: 'WindowsPlayer', folder: windows,
                archive: windowsZip, entrypoint: 'Bees RL Training.exe' },
            { role: 'dedicated', platform: 'LinuxPlayer', folder: linux,
                archive: linuxZip, entrypoint: 'Bees RL Training.x86_64' },
        ],
        unity_maintenance: {
            previous_build_id: release.build_id,
            at: new Date().toISOString(),
            warning: 'Unity simulation implementation may have changed; comparisons across this point require review.',
        },
    };
    const envArgs = (desired.environment_args || []).map(String);
    // The new compiled Windows player must validate *the current stage's*
    // environment arguments before the control plane may stage a new build.
    const validationKey = await assertRlEnvironmentArgsValid(
        config, candidate, envArgs, python
    );
    const again = await getStatus(config, admin);
    if (again.desired?.training_enabled || again.desired?.pending_release ||
        again.desired?.run_id !== release.run_id ||
        again.desired?.canonical_build_id !== release.build_id) {
        throw Error('Training control changed while compiling. The candidate was not published.');
    }

    let bootstrapPublished = false;
    try {
        if (!config.localOnly && exists(paths.tailnetAddressPath)) {
            bootstrapPublished = true;
            prepareRemoteBootstrap(config, python, candidate,
                workerOverride ? { workerOverride, persistWorkerOverride: false } : {});
            await startTailnetGatewayIfNeeded(config);
        }
        await publishRelease(config, admin, candidate);
        const staged = await stageRelease(config, admin, candidate, envArgs, validationKey);
        if (staged.pending_release) {
            await waitReleaseRollout(config, admin, buildId,
                release.run_id, release.compatibility_key);
        }
        // Persist only when the coordinated control-plane release is canonical.
        saveLatestRelease(candidate);
        if (workerOverride) writeJsonAtomic(overridePath, workerOverride);
        const centralRuntime = await prepareCentralReleaseRuntime(config, python, unity, candidate);
        await startCentralAgentIfNeeded(config, python, unity, candidate, centralRuntime);
        prepareLocalActorReleaseRuntime(config, candidate, centralRuntime);
    } catch (error) {
        // If the control-plane remained on the original build, undo the
        // published candidate bootstrap. Otherwise a resumed old worker can
        // wait forever for a candidate build that never became canonical.
        if (bootstrapPublished) {
            try {
                const after = await getStatus(config, admin);
                if (!after.desired?.pending_release &&
                    after.desired?.canonical_build_id === release.build_id &&
                    after.desired?.run_id === release.run_id &&
                    getLatestRelease().build_id === release.build_id) {
                    // The original worker-runtime pointer was retained until
                    // canonical promotion, so ordinary bootstrap publication
                    // restores the original pinned runtime or worker overlay.
                    prepareRemoteBootstrap(config, python, release);
                }
            } catch (rollbackError) {
                throw Error('Unity rebuild failed: ' + error.message +
                    '; worker bootstrap rollback failed: ' + rollbackError.message +
                    '. Leave the experiment paused and inspect the package.');
            }
        }
        throw error;
    }
    return {
        run_id: release.run_id,
        old_build_id: release.build_id,
        build_id: buildId,
        runtime_version: trainingRuntime.runtime_version,
        compatibility_key: release.compatibility_key,
    };
}

async function invokeBuild(options = {}) {
    const config = require('./common').loadConfig();
    const python = resolvePython(config);
    const unfinishedPlan = getPendingForcedNewRunPlan();
    if (unfinishedPlan) {
        const build = String(unfinishedPlan.build_id || '').trim() || 'legacy-unbound';
        throw new Error(
            'A forced new-run operation is still unfinished for build ' + build +
            ' run=' + unfinishedPlan.run_id +
            ". Run '.\\Assets\\bees.ps1 start' to resume/finalize it before creating another build."
        );
    }

    const sourceSha = getGitShortSha();
    const unity = resolveUnityEditor(config);
    assertUnityProjectAvailableForBatchBuild();

    let supersedeCompatibleBuildId = '';
    if (exists(paths.latestReleasePath) && exists(paths.adminTokenPath)) {
        const admin = readText(paths.adminTokenPath).trim();
        const controlOnline = admin ? await testControl(String(config.controlUrl), admin) : false;
        const managedServerExists = exists(paths.serverStatePath);
        if (admin && (controlOnline || managedServerExists)) {
            const worker = ensureTokenFile(paths.workerTokenPath);
            await startBeesServerIfNeeded(config, worker, admin);
            if (!(await testControl(String(config.controlUrl), admin))) {
                throw new Error(
                    'Managed BeesServer reconciliation completed without a reachable training-control endpoint before build.'
                );
            }
            const currentRelease = getLatestRelease();
            const controlStatus = await getStatus(config, admin);
            const desired = controlStatus.desired || {};
            const activeRun = String(desired.run_id || '').trim();
            const activeKey = String(desired.compatibility_key || '').trim().toLowerCase();
            const fingerprint = getTrainingCompatibilityFingerprint(python);
            const sourceKey = String(fingerprint.compatibility_key || '').trim().toLowerCase();
            const releaseRun = String(currentRelease.run_id || '').trim();
            const releaseKey = String(currentRelease.compatibility_key || '').trim().toLowerCase();

            if (
                activeRun &&
                activeKey &&
                sourceKey === activeKey &&
                (releaseRun !== activeRun || releaseKey !== activeKey)
            ) {
                console.warn(
                    'Latest release metadata is not canonical and current source exactly matches ' +
                    'the active cluster compatibility contract; treating the persisted release as ' +
                    'an interrupted/stale build and recovering lifecycle state to active run ' +
                    activeRun + '.'
                );
                recoverTrainingRunLifecycle(python, activeRun, activeKey);
            } else if (currentRelease.run_id && currentRelease.compatibility_key) {
                const reconciliation = await reconcileLatestReleaseBeforeBuild(
                    config, python, unity, admin, currentRelease
                );
                supersedeCompatibleBuildId = String(
                    (reconciliation && reconciliation.supersedeCompatibleBuildId) || ''
                ).trim();
            }
        }
    }

    const preserveExistingRun = Boolean(
        options.preserveRun ||
        (!options.newRun && exists(paths.runStatePath))
    );
    const plan = newTrainingRunPlan(python, {
        preserveRun: preserveExistingRun,
    });
    if (plan.incompatible && plan.preserve_run_override) {
        console.warn(
            'Training contract changed incompatibly but checkpoint shape is compatible; preserving existing run: ' +
            plan.run_id + '. Trainers will stop together before the new compatibility contract becomes active.'
        );
    } else if (plan.incompatible) {
        console.log(
            'Training contract changed incompatibly and -NewRun was explicitly supplied. ' +
            'New run: ' + plan.run_id
        );
    } else if (plan.new_run) {
        console.log('Creating initial training run: ' + plan.run_id);
    } else {
        console.log('Training contract is compatible; continuing run ' + plan.run_id + '.');
    }

    let previousBridgeHash = null;
    if (exists(paths.tailnetBridgeManifestPath)) {
        try { previousBridgeHash = String(readJson(paths.tailnetBridgeManifestPath).source_hash || ''); } catch (_) {}
    }
    buildTailnetBridge();
    const currentBridgeHash = getTailnetBridgeSourceHash();
    const tailnetBridgeChanged = previousBridgeHash !== currentBridgeHash;

    ensureDir(paths.buildsRoot);
    const now = new Date();
    const date = formatLocalDate(now);
    const time = formatLocalTime(now);
    const buildId = date + '-' + time + '-' + sourceSha;
    const windowsBuild = path.join(paths.buildsRoot, date + ' RL Windows');
    const linuxBuild = path.join(paths.buildsRoot, date + ' RL Linux');
    const gameBuild = path.join(paths.buildsRoot, date + ' Full Game Windows');
    const packageRoot = path.join(paths.buildsRoot, 'Packages', buildId);

    if (exists(packageRoot)) {
        if (!options.force) {
            throw new Error('Package directory exists: ' + packageRoot + '. Use -Force.');
        }
        removeIfExists(packageRoot, { recursive: true });
    }
    ensureDir(packageRoot);

    // Pin the Python/config runtime before Unity compilation begins.
    const trainingRuntimeArchive = path.join(packageRoot, 'training-runtime.zip');
    const trainingRuntime = newReleaseTrainingRuntime(
        python, buildId, sourceSha, trainingRuntimeArchive
    );

    resetBuildDirectory(windowsBuild, Boolean(options.force));
    resetBuildDirectory(linuxBuild, Boolean(options.force));
    if (options.fullGame) resetBuildDirectory(gameBuild, Boolean(options.force));

    await invokeUnityBuild(
        unity,
        'BeesCommandLineBuild.BuildWindowsRl',
        windowsBuild,
        'Bees RL Training.exe',
        date + '-rl-windows.log',
    );
    await invokeUnityBuild(
        unity,
        'BeesCommandLineBuild.BuildLinuxRl',
        linuxBuild,
        'Bees RL Training.x86_64',
        date + '-rl-linux.log',
    );
    if (options.fullGame) {
        await invokeUnityBuild(
            unity,
            'BeesCommandLineBuild.BuildWindowsFullGame',
            gameBuild,
            'Bees.exe',
            date + '-full-game-windows.log',
        );
    }

    const postBuildFingerprint = getTrainingCompatibilityFingerprint(python);
    const postBuildKey = String(postBuildFingerprint.compatibility_key || '').trim().toLowerCase();
    const plannedKey = String(plan.compatibility_key || '').trim().toLowerCase();
    if (!postBuildKey || postBuildKey !== plannedKey) {
        throw new Error(
            'Training compatibility contract changed while Unity was building. Refusing to publish a mixed release. planned=' +
            plannedKey + ' current=' + postBuildKey + '. Re-run the build from the current source.'
        );
    }

    const windowsZip = path.join(packageRoot, 'rl-windows.zip');
    const linuxZip = path.join(packageRoot, 'rl-linux.zip');
    packageBuild(python, windowsBuild, windowsZip, 'Bees RL Training.exe');
    packageBuild(python, linuxBuild, linuxZip, 'Bees RL Training.x86_64');
    const artifacts = [
        {
            role: 'dedicated',
            platform: 'WindowsPlayer',
            folder: windowsBuild,
            archive: windowsZip,
            entrypoint: 'Bees RL Training.exe',
        },
        {
            role: 'dedicated',
            platform: 'LinuxPlayer',
            folder: linuxBuild,
            archive: linuxZip,
            entrypoint: 'Bees RL Training.x86_64',
        },
    ];
    if (options.fullGame) {
        const gameZip = path.join(packageRoot, 'full-game-windows.zip');
        packageBuild(python, gameBuild, gameZip, 'Bees.exe');
        artifacts.push({
            role: 'full-game',
            platform: 'WindowsPlayer',
            folder: gameBuild,
            archive: gameZip,
            entrypoint: 'Bees.exe',
        });
    }

    const release = {
        schema_version: 3,
        build_id: buildId,
        source_commit: sourceSha,
        created_utc: new Date().toISOString(),
        run_id: String(plan.run_id),
        previous_run_id: plan.previous_run_id ? String(plan.previous_run_id) : null,
        compatibility_key: String(plan.compatibility_key),
        previous_compatibility_key: plan.previous_compatibility_key
            ? String(plan.previous_compatibility_key)
            : null,
        incompatible: Boolean(plan.incompatible),
        preserve_run_override: Boolean(plan.preserve_run_override),
        contract: plan.contract,
        artifacts,
        training_runtime: trainingRuntime,
    };
    let releasePersisted = false;
    const persistReleaseLifecycle = () => {
        if (releasePersisted) return;
        saveLatestRelease(release);
        commitTrainingRunPlan(python);
        releasePersisted = true;
    };

    console.log('');
    console.log('Build complete: ' + buildId + '  run=' + release.run_id);
    for (const artifact of artifacts) {
        console.log('  ' + artifact.role + ' ' + artifact.platform + '  ' + artifact.folder);
    }

    if (exists(paths.adminTokenPath)) {
        const admin = readText(paths.adminTokenPath).trim();
        const controlOnline = admin ? await testControl(String(config.controlUrl), admin) : false;
        const managedServerExists = exists(paths.serverStatePath);
        if (admin && (controlOnline || managedServerExists)) {
            const worker = ensureTokenFile(paths.workerTokenPath);
            await startBeesServerIfNeeded(config, worker, admin);
            if (!(await testControl(String(config.controlUrl), admin))) {
                throw new Error(
                    'Managed BeesServer reconciliation completed without a reachable training-control endpoint.'
                );
            }

            const preStageStatus = await getStatus(config, admin);
            const environmentArgs =
                ((preStageStatus.desired && preStageStatus.desired.environment_args) || []).map(String);
            const validationKey = await assertRlEnvironmentArgsValid(
                config, release, environmentArgs, python
            );
            // Do not advance latest-release/run lifecycle state until the newly built
            // artifacts have passed environment validation. If validation fails, the
            // previously canonical release remains the durable recovery point.
            persistReleaseLifecycle();
            assertCentralAgentCheckpointSafe();
            const centralRuntime = await prepareCentralReleaseRuntime(config, python, unity, release);
            await startCentralAgentIfNeeded(config, python, unity, release, centralRuntime);
            prepareLocalActorReleaseRuntime(config, release, centralRuntime);
            console.log(
                'Training control is online; staging this release without stopping the active cluster.'
            );
            if (exists(paths.tailnetAddressPath)) {
                prepareRemoteBootstrap(config, python, release);
                if (tailnetBridgeChanged) {
                    console.log(
                        'Embedded tailnet helper changed; reconciling the private gateway onto the new immutable helper version.'
                    );
                }
                await startTailnetGatewayIfNeeded(config);
            }
            await publishRelease(config, admin, release);
            const staged = await stageRelease(
                config,
                admin,
                release,
                environmentArgs,
                validationKey,
                supersedeCompatibleBuildId
                    ? { supersedeCompatibleBuildId }
                    : {},
            );
            console.log(
                'Release staged: build=' + buildId +
                ' phase=' + (staged.pending_release ? staged.pending_release.phase : 'active')
            );

            if (release.incompatible) {
                await waitReleaseRollout(
                    config,
                    admin,
                    buildId,
                    String(release.run_id),
                    String(release.compatibility_key),
                );
                console.log(
                    release.preserve_run_override
                        ? 'Compatibility cutover complete; preserved run: ' + release.run_id
                        : 'Incompatible cutover complete. Active run: ' + release.run_id
                );
                await startLocalActorIfNeeded(
                    config,
                    python,
                    release,
                    centralRuntime,
                );
            } else if (!staged.pending_release) {
                await startLocalActorIfNeeded(
                    config,
                    python,
                    release,
                    centralRuntime,
                );
            }
        }
    }

    // Offline builds have no control-plane validation step, so persist only after all
    // local build/package work above has completed successfully.
    persistReleaseLifecycle();
    return release;
}

module.exports = {
    assertUnityProjectAvailableForBatchBuild,
    getActiveRunId,
    getUnityBuildProgressStatus,
    invokeBuild,
    invokeMaintenanceUnityRebuild,
    invokeUnityBuild,
    packageBuild,
    queryUnityProcesses,
    reconcileLatestReleaseBeforeBuild,
    resetBuildDirectory,
};
