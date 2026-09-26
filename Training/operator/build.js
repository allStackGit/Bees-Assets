'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const {
    ensureDir,
    ensureTokenFile,
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
} = require('./common');
const {
    archiveTrainingRun,
    commitTrainingRunPlan,
    getLatestRelease,
    getPendingForcedNewRunPlan,
    getTrainingCompatibilityFingerprint,
    newReleaseTrainingRuntime,
    newTrainingRunPlan,
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
        try { return String(readJson(paths.runStatePath).run_id || '').trim(); } catch (_) {}
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

    const centralRuntime = prepareCentralReleaseRuntime(config, python, unity, release);
    await startCentralAgentIfNeeded(config, python, unity, release, centralRuntime);
    const status = await getStatus(config, adminToken);
    const pending = status.desired && status.desired.pending_release;
    const releaseBuild = String(release.build_id || '').trim();
    const releaseRun = String(release.run_id || '').trim();
    const releaseKey = String(release.compatibility_key || '').trim().toLowerCase();

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
        console.log(
            'Previous release is still rolling out (phase=' + pending.phase +
            '); finishing build ' + releaseBuild + ' before compiling another release.'
        );
        await waitReleaseRollout(config, adminToken, releaseBuild, releaseRun, releaseKey);
        return;
    }

    const desired = status.desired || {};
    if (
        String(desired.canonical_build_id || '').trim() === releaseBuild &&
        String(desired.run_id || '').trim() === releaseRun &&
        String(desired.compatibility_key || '').trim().toLowerCase() === releaseKey
    ) {
        return;
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
            if (currentRelease.run_id && currentRelease.compatibility_key) {
                await reconcileLatestReleaseBeforeBuild(
                    config, python, unity, admin, currentRelease
                );
            }
        }
    }

    const preBuildAdmin = exists(paths.adminTokenPath) ? readText(paths.adminTokenPath).trim() : '';
    const outgoingRun = await getActiveRunId(config, preBuildAdmin);
    if (outgoingRun) archiveTrainingRun(python, outgoingRun, 'pre-build');

    const plan = newTrainingRunPlan(python);
    if (plan.incompatible) {
        console.log('Training contract changed incompatibly. New run: ' + plan.run_id);
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
        incompatible: Boolean(plan.incompatible),
        contract: plan.contract,
        artifacts,
        training_runtime: trainingRuntime,
    };
    saveLatestRelease(release);
    commitTrainingRunPlan(python);

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
            assertCentralAgentCheckpointSafe();
            const centralRuntime = prepareCentralReleaseRuntime(config, python, unity, release);
            await startCentralAgentIfNeeded(config, python, unity, release, centralRuntime);
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
                config, admin, release, environmentArgs, validationKey
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
                if (release.previous_run_id) {
                    await sleep(2000);
                    archiveTrainingRun(
                        python,
                        String(release.previous_run_id),
                        'incompatible-run-final',
                    );
                }
                console.log('Incompatible cutover complete. Active run: ' + release.run_id);
            }
        }
    }

    return release;
}

module.exports = {
    assertUnityProjectAvailableForBatchBuild,
    getActiveRunId,
    getUnityBuildProgressStatus,
    invokeBuild,
    invokeUnityBuild,
    packageBuild,
    reconcileLatestReleaseBeforeBuild,
    resetBuildDirectory,
};
