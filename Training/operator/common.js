'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const assetsRoot = path.resolve(__dirname, '..', '..');
const beesRoot = path.resolve(assetsRoot, '..');

const paths = Object.freeze({
    assetsRoot,
    beesRoot,
    buildsRoot: path.join(beesRoot, 'Builds'),
    secretsRoot: path.join(beesRoot, 'Secrets'),
    runtimeRoot: path.join(beesRoot, 'Runtime'),
    logsRoot: path.join(beesRoot, 'Logs'),
    trainingRoot: path.join(beesRoot, 'Training'),
    remoteRoot: path.join(beesRoot, 'Remote'),
    serverRoot: path.join(assetsRoot, 'BeesServer~'),
    configPath: path.join(assetsRoot, 'Training', 'bees.cluster.json'),
    runtimeTrainingOptionsPath: path.join(beesRoot, 'Runtime', 'training-runtime-options.json'),
    latestReleasePath: path.join(beesRoot, 'Builds', 'latest-training-release.json'),
    workerTokenPath: path.join(beesRoot, 'Secrets', 'training-worker.token'),
    adminTokenPath: path.join(beesRoot, 'Secrets', 'training-admin.token'),
    wanTokenPath: path.join(beesRoot, 'Secrets', 'wan.token'),
    bootstrapTokenPath: path.join(beesRoot, 'Secrets', 'training-bootstrap.token'),
    environmentValidationTokenPath: path.join(beesRoot, 'Secrets', 'training-environment-validation.token'),
    runLifecycleRoot: path.join(beesRoot, 'Training', 'RunLifecycle'),
    runStatePath: path.join(beesRoot, 'Training', 'RunLifecycle', 'current.json'),
    runPlanPath: path.join(beesRoot, 'Runtime', 'pending-training-run.json'),
    runLifecycleScript: path.join(assetsRoot, 'Training', 'bees_run_lifecycle.py'),
    diagnosticBundleScript: path.join(assetsRoot, 'Training', 'bees_training_bundle.py'),
    diagnosticBenchmarkScript: path.join(assetsRoot, 'Training', 'bees_training_diagnostic_benchmark.py'),
    robustnessQualificationScript: path.join(assetsRoot, 'Training', 'bees_training_robustness_qualification.py'),
    releaseRuntimeScript: path.join(assetsRoot, 'Training', 'bees_release_runtime.py'),
    bootstrapBundleScript: path.join(assetsRoot, 'Training', 'bees_bootstrap_bundle.py'),
    remoteBootstrapTemplate: path.join(assetsRoot, 'Training', 'bees_remote_bootstrap.ps1'),
    remoteLinuxBootstrapTemplate: path.join(assetsRoot, 'Training', 'bees_remote_bootstrap.sh'),
    remoteRequirementsPath: path.join(assetsRoot, 'Training', 'bees_remote_requirements.txt'),
    learnerRequirementsPath: path.join(assetsRoot, 'Training', 'bees_learner_requirements.txt'),
    releaseRuntimeInstallRoot: path.join(beesRoot, 'Runtime', 'TrainingReleases'),
    bootstrapBundlePath: path.join(beesRoot, 'Remote', 'bees-bootstrap-bundle.zip'),
    serverPidPath: path.join(beesRoot, 'Runtime', 'bees-server.pid'),
    serverStatePath: path.join(beesRoot, 'Runtime', 'bees-server-state.json'),
    serverReleaseRoot: path.join(beesRoot, 'Runtime', 'ServerReleases'),
    centralAgentPidPath: path.join(beesRoot, 'Runtime', 'central-training-agent.pid'),
    centralAgentStatePath: path.join(beesRoot, 'Runtime', 'central-training-agent.json'),
    centralAgentInstallRoot: path.join(beesRoot, 'ManagedBuilds', 'central-learner'),
    centralAgentShutdownRequestPath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'worker-shutdown.request'),
    centralRuntimePointerPath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'release-runtime.json'),
    centralRuntimeReadyBuildPath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'runtime-ready-build.txt'),
    centralRuntimeStatePath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'active-runtime.json'),
    centralModelSnapshotRequestPath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'model-snapshot.request'),
    centralModelSnapshotResponsePath: path.join(beesRoot, 'ManagedBuilds', 'central-learner', 'model-snapshot.response.json'),
    localActorPidPath: path.join(beesRoot, 'Runtime', 'local-training-actor.pid'),
    localActorStatePath: path.join(beesRoot, 'Runtime', 'local-training-actor.json'),
    localActorInstallRoot: path.join(beesRoot, 'ManagedBuilds', 'local-actor'),
    localActorBuildRoot: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'Builds'),
    localActorKeyPath: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'actor-key.txt'),
    localActorShutdownRequestPath: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'worker-shutdown.request'),
    localActorRuntimePointerPath: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'release-runtime.json'),
    localActorRuntimeReadyBuildPath: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'runtime-ready-build.txt'),
    localActorRuntimeStatePath: path.join(beesRoot, 'ManagedBuilds', 'local-actor', 'active-runtime.json'),
    localActorOutLogPath: path.join(beesRoot, 'Logs', 'Training', 'local-actor.out.log'),
    localActorErrLogPath: path.join(beesRoot, 'Logs', 'Training', 'local-actor.err.log'),
    tailnetToolRoot: path.join(assetsRoot, 'Tools~', 'bees-tailnet-bridge'),
    tailnetRoot: path.join(beesRoot, 'Runtime', 'Tailnet'),
    tailnetBinRoot: path.join(beesRoot, 'Runtime', 'Tailnet', 'Bin'),
    tailnetBridgeManifestPath: path.join(beesRoot, 'Runtime', 'Tailnet', 'Bin', 'current.json'),
    tailnetGatewayPidPath: path.join(beesRoot, 'Runtime', 'Tailnet', 'gateway.pid'),
    tailnetGatewayStatePath: path.join(beesRoot, 'Runtime', 'Tailnet', 'gateway-state.json'),
    tailnetGatewayHealthPath: path.join(beesRoot, 'Runtime', 'Tailnet', 'gateway-health.txt'),
    tailnetGatewayLogPath: path.join(beesRoot, 'Logs', 'Training', 'tailnet-gateway.out.log'),
    tailnetGatewayErrPath: path.join(beesRoot, 'Logs', 'Training', 'tailnet-gateway.err.log'),
    tailnetAddressPath: path.join(beesRoot, 'Runtime', 'Tailnet', 'learner-ipv4.txt'),
});

const GAMEPLAY_SERVER_PORT = 7146;

function exists(filePath) {
    try {
        fs.accessSync(filePath);
        return true;
    } catch (_) {
        return false;
    }
}

function ensureDir(directory) {
    fs.mkdirSync(directory, { recursive: true });
}

function removeIfExists(target, options = {}) {
    const recursive = Boolean(options.recursive);
    const maxRetries = recursive
        ? Math.max(0, Number.isInteger(options.maxRetries) ? options.maxRetries : 50)
        : 0;
    const retryDelay = recursive
        ? Math.max(0, Number.isInteger(options.retryDelay) ? options.retryDelay : 100)
        : 0;
    try {
        fs.rmSync(target, {
            force: true,
            recursive,
            maxRetries,
            retryDelay,
        });
    } catch (error) {
        if (error.code !== 'ENOENT') throw error;
    }
}

function removeUtf8BomIfPresent(filePath) {
    if (!exists(filePath)) return false;
    const bytes = fs.readFileSync(filePath);
    if (bytes.length < 3 || bytes[0] !== 0xEF || bytes[1] !== 0xBB || bytes[2] !== 0xBF) {
        return false;
    }
    const temp = filePath + '.nobom-' + process.pid;
    fs.writeFileSync(temp, bytes.subarray(3));
    atomicReplace(temp, filePath);
    return true;
}

function readText(filePath) {
    return fs.readFileSync(filePath, 'utf8');
}

function readJson(filePath, fallback = undefined) {
    try {
        const text = readText(filePath).replace(/^\uFEFF/, '');
        return JSON.parse(text);
    } catch (error) {
        if (fallback !== undefined && (error.code === 'ENOENT' || error instanceof SyntaxError)) {
            return fallback;
        }
        throw error;
    }
}

function atomicReplace(tempPath, destinationPath) {
    ensureDir(path.dirname(destinationPath));
    if (!exists(destinationPath) || process.platform !== 'win32') {
        fs.renameSync(tempPath, destinationPath);
        return;
    }

    // Windows does not expose File.Replace directly through node:fs. Keep the atomic replacement
    // guarantee by using the .NET primitive as a narrow OS adapter; paths travel through the
    // environment, never through a quoted command string.
    const backup = destinationPath + '.swap-backup';
    const ps = powershellExecutable();
    const env = {
        ...process.env,
        BEES_ATOMIC_SOURCE: path.resolve(tempPath),
        BEES_ATOMIC_DESTINATION: path.resolve(destinationPath),
        BEES_ATOMIC_BACKUP: path.resolve(backup),
    };
    const script =
        'Remove-Item -LiteralPath $env:BEES_ATOMIC_BACKUP -Force -ErrorAction SilentlyContinue;' +
        '[IO.File]::Replace($env:BEES_ATOMIC_SOURCE,$env:BEES_ATOMIC_DESTINATION,$env:BEES_ATOMIC_BACKUP,$true);' +
        'Remove-Item -LiteralPath $env:BEES_ATOMIC_BACKUP -Force -ErrorAction SilentlyContinue';

    let lastError = '';
    for (let attempt = 0; attempt < 300; attempt++) {
        const result = spawnSync(
            ps,
            ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
            {
                env,
                encoding: 'utf8',
                windowsHide: true,
                stdio: ['ignore', 'pipe', 'pipe'],
            },
        );
        if (!result.error && result.status === 0) return;
        lastError = result.error
            ? result.error.message
            : String(result.stderr || result.stdout || '').trim();
        if (attempt < 299) sleepSync(100);
    }
    throw new Error(
        'Atomic Windows file replacement failed for ' + destinationPath +
        (lastError ? ': ' + lastError : '')
    );
}

function writeTextAtomic(filePath, value, encoding = 'utf8') {
    ensureDir(path.dirname(filePath));
    const temp = filePath + '.new-' + process.pid + '-' + crypto.randomBytes(6).toString('hex');
    fs.writeFileSync(temp, value, { encoding });
    atomicReplace(temp, filePath);
}

function writeJsonAtomic(filePath, value) {
    writeTextAtomic(filePath, JSON.stringify(value, null, 2) + os.EOL);
}

function sha256Text(value) {
    return crypto.createHash('sha256').update(String(value), 'utf8').digest('hex');
}

function sha256File(filePath) {
    const hash = crypto.createHash('sha256');
    hash.update(fs.readFileSync(filePath));
    return hash.digest('hex');
}

function hmacSha256(secret, value) {
    if (!secret || String(secret).length < 32) {
        throw new Error('Environment validation secret must contain at least 32 characters.');
    }
    return crypto.createHmac('sha256', String(secret)).update(String(value), 'utf8').digest('hex');
}

function sleepSync(milliseconds) {
    if (milliseconds <= 0) return;
    const buffer = new SharedArrayBuffer(4);
    Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}

function sleep(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function resolveCommand(command) {
    if (!command) throw new Error('Required executable name is empty.');
    if (path.isAbsolute(command) || command.includes(path.sep)) {
        const resolved = path.resolve(command);
        if (!exists(resolved)) throw new Error("Required executable '" + command + "' was not found.");
        return resolved;
    }
    const resolver = process.platform === 'win32' ? 'where.exe' : 'which';
    const result = spawnSync(resolver, [command], {
        encoding: 'utf8',
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
    });
    const first = String(result.stdout || '').split(/\r?\n/).map(x => x.trim()).find(Boolean);
    if (result.status !== 0 || !first) {
        throw new Error("Required executable '" + command + "' was not found on PATH.");
    }
    return path.resolve(first);
}

function npmInvocationFromCommand(npmCommand, node = process.execPath) {
    const resolvedNpm = path.resolve(String(npmCommand));
    if (!/\.(?:cmd|bat)$/i.test(resolvedNpm)) {
        return {
            executable: resolvedNpm,
            args: [],
        };
    }

    // Node cannot reliably spawn Windows command shims directly with shell=false. Do not
    // reintroduce cmd.exe quoting: npm's installer shim lives beside its JavaScript CLI, so
    // execute that CLI through the already-selected Node runtime with a structured argv array.
    const npmCli = path.join(
        path.dirname(resolvedNpm),
        'node_modules',
        'npm',
        'bin',
        'npm-cli.js',
    );
    if (!exists(npmCli)) {
        throw new Error(
            'npm Windows shim was found at ' + resolvedNpm +
            ', but its sibling JavaScript CLI is missing: ' + npmCli
        );
    }
    return {
        executable: path.resolve(node),
        args: [path.resolve(npmCli)],
    };
}

function resolveNpmInvocation(node = process.execPath) {
    const npmCommand = resolveCommand(process.platform === 'win32' ? 'npm.cmd' : 'npm');
    return npmInvocationFromCommand(npmCommand, node);
}

function normalizeThreadedPolicyLag(value, fallback = 1) {
    const candidate = value == null ? fallback : Number(value);
    if (
        !Number.isSafeInteger(candidate) ||
        candidate < 0 ||
        candidate > 2147483647
    ) {
        throw new Error(
            'Threaded policy lag must be an integer in 0-2147483647; got ' + value + '.'
        );
    }
    return candidate;
}

function normalizeWanMaxQueuedBatches(value, fallback = 32) {
    const candidate = value == null ? fallback : Number(value);
    if (
        !Number.isSafeInteger(candidate) ||
        candidate < 1 ||
        candidate > 2147483647
    ) {
        throw new Error(
            'WAN backpressure queue must be an integer in 1-2147483647; got ' + value + '.'
        );
    }
    return candidate;
}

function normalizeLearnerOptimizations(value, fallback = null) {
    const base = fallback && typeof fallback === 'object'
        ? fallback
        : {
            syncCleanup: false,
            streamShards: 1,
            minibatchPrefetch: false,
            criticBaselineOverlap: false,
            cudaGraphs: false,
            profileOnce: false,
            nsightOnce: false,
            detailedDiagnostics: false,
        };
    const source = value && typeof value === 'object' ? value : base;
    const streamShards = Number(
        source.streamShards == null ? base.streamShards : source.streamShards
    );
    if (![1, 2, 4].includes(streamShards)) {
        throw new Error(
            'Learner stream shards must be one of 1, 2, or 4; got ' +
            String(source.streamShards) + '.'
        );
    }
    return {
        syncCleanup: Boolean(
            source.syncCleanup == null ? base.syncCleanup : source.syncCleanup
        ),
        streamShards,
        minibatchPrefetch: Boolean(
            source.minibatchPrefetch == null
                ? base.minibatchPrefetch
                : source.minibatchPrefetch
        ),
        criticBaselineOverlap: Boolean(
            source.criticBaselineOverlap == null
                ? base.criticBaselineOverlap
                : source.criticBaselineOverlap
        ),
        cudaGraphs: Boolean(
            source.cudaGraphs == null ? base.cudaGraphs : source.cudaGraphs
        ),
        profileOnce: Boolean(
            source.profileOnce == null ? base.profileOnce : source.profileOnce
        ),
        nsightOnce: Boolean(
            source.nsightOnce == null ? base.nsightOnce : source.nsightOnce
        ),
        detailedDiagnostics: Boolean(
            source.detailedDiagnostics == null
                ? base.detailedDiagnostics
                : source.detailedDiagnostics
        ),
    };
}

function normalizeLearnerDevice(value, fallback = null) {
    const candidate = value == null ? fallback : String(value).toLowerCase();
    if (candidate == null) return null;
    if (candidate !== 'cpu' && candidate !== 'cuda') {
        throw new Error('Learner device must be cpu or cuda; got ' + value + '.');
    }
    return candidate;
}

function normalizeLocalEnvs(value, fallback = 0) {
    const candidate = value == null ? fallback : Number(value);
    if (!Number.isSafeInteger(candidate) || candidate < 0 || candidate > 2147483647) {
        throw new Error('Local environment count must be an integer in 0-2147483647; got ' + value + '.');
    }
    return candidate;
}

const TRAINING_LOG_CATEGORIES = new Set([
    'training', 'learner-samples', 'learner-stages', 'wan-capacity',
    'wan-pipeline', 'wan-lag', 'wan-other', 'continual', 'ppo',
    'checkpoints', 'distributed', 'other',
]);

function normalizeLogCategories(value) {
    const raw = String(value == null ? 'all' : value).trim().toLowerCase();
    if (raw === 'all' || raw === 'none') return raw;
    const selected = raw.split(',').map(x => x.trim());
    if (!selected.length || selected.some(x => !TRAINING_LOG_CATEGORIES.has(x))) {
        throw new Error('Invalid training log categories: ' + String(value) +
            '. Use all, none, or comma-separated names from: ' +
            [...TRAINING_LOG_CATEGORIES].join(', '));
    }
    return [...new Set(selected)].join(',');
}

function applyRuntimeTrainingOptions(config, options = {}) {
    const result = {
        ...config,
        skipEvaluation: Boolean(config && config.skipEvaluation),
        workerDiagnostics: config && config.workerDiagnostics !== false,
        logCategories: normalizeLogCategories(config && config.logCategories),
        learnerDevice: normalizeLearnerDevice(config && config.learnerDevice),
        localOnly: Boolean(config && config.localOnly),
        numLocalEnvs: normalizeLocalEnvs(config && config.numLocalEnvs),
        learnerOptimizations: normalizeLearnerOptimizations(
            config && config.learnerOptimizations,
        ),
        localActor: {
            ...(config && typeof config.localActor === 'object' && config.localActor
                ? config.localActor
                : {}),
        },
    };
    // A persisted pre-v26 entropyBeta setting must never override trainer YAML.
    delete result.entropyBeta;
    if (Object.prototype.hasOwnProperty.call(options, 'threadedTraining')) {
        result.threadedTraining = Boolean(options.threadedTraining);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'threadedPolicyLag')) {
        result.threadedPolicyLag = normalizeThreadedPolicyLag(
            options.threadedPolicyLag,
            config && config.threadedPolicyLag != null ? config.threadedPolicyLag : 1,
        );
    } else {
        result.threadedPolicyLag = normalizeThreadedPolicyLag(
            result.threadedPolicyLag,
            1,
        );
    }
    if (Object.prototype.hasOwnProperty.call(options, 'wanMaxQueuedBatches')) {
        result.wanMaxQueuedBatches = normalizeWanMaxQueuedBatches(
            options.wanMaxQueuedBatches,
            config && config.wanMaxQueuedBatches != null ? config.wanMaxQueuedBatches : 32,
        );
    } else {
        result.wanMaxQueuedBatches = normalizeWanMaxQueuedBatches(
            result.wanMaxQueuedBatches,
            32,
        );
    }
    if (Object.prototype.hasOwnProperty.call(options, 'localTraining')) {
        result.localActor.enabled = Boolean(options.localTraining);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'localOnly')) {
        result.localOnly = Boolean(options.localOnly);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'numLocalEnvs')) {
        result.numLocalEnvs = normalizeLocalEnvs(options.numLocalEnvs);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'learnerDevice')) {
        result.learnerDevice = normalizeLearnerDevice(options.learnerDevice);
    }
    if (result.localOnly) result.localActor.enabled = false;
    if (Object.prototype.hasOwnProperty.call(options, 'learnerOptimizations')) {
        result.learnerOptimizations = normalizeLearnerOptimizations(
            options.learnerOptimizations,
            result.learnerOptimizations,
        );
    }
    if (Object.prototype.hasOwnProperty.call(options, 'skipEvaluation')) {
        result.skipEvaluation = Boolean(options.skipEvaluation);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'workerDiagnostics')) {
        result.workerDiagnostics = Boolean(options.workerDiagnostics);
    }
    if (Object.prototype.hasOwnProperty.call(options, 'logCategories')) {
        result.logCategories = normalizeLogCategories(options.logCategories);
    }
    return result;
}

function readRuntimeTrainingOptions(
    defaultPolicyLag = 1,
    defaultWanMaxQueuedBatches = 32,
    defaultLearnerOptimizations = null,
) {
    if (!exists(paths.runtimeTrainingOptionsPath)) return null;
    const value = readJson(paths.runtimeTrainingOptionsPath);
    if (
        !value ||
        value.schema_version !== 1 ||
        typeof value.threadedTraining !== 'boolean' ||
        typeof value.localTraining !== 'boolean'
    ) {
        throw new Error(
            'Runtime training options are malformed: ' + paths.runtimeTrainingOptionsPath
        );
    }
    return {
        ...value,
        threadedPolicyLag: normalizeThreadedPolicyLag(
            value.threadedPolicyLag,
            defaultPolicyLag,
        ),
        wanMaxQueuedBatches: normalizeWanMaxQueuedBatches(
            value.wanMaxQueuedBatches,
            defaultWanMaxQueuedBatches,
        ),
        learnerOptimizations: normalizeLearnerOptimizations(
            value.learnerOptimizations,
            defaultLearnerOptimizations,
        ),
        skipEvaluation: Boolean(value.skipEvaluation),
        workerDiagnostics: value.workerDiagnostics !== false,
        logCategories: normalizeLogCategories(value.logCategories),
        localOnly: Boolean(value.localOnly),
        numLocalEnvs: normalizeLocalEnvs(value.numLocalEnvs),
        learnerDevice: normalizeLearnerDevice(value.learnerDevice),
    };
}

function saveRuntimeTrainingOptions(options) {
    const value = {
        schema_version: 1,
        threadedTraining: Boolean(options && options.threadedTraining),
        threadedPolicyLag: normalizeThreadedPolicyLag(
            options && options.threadedPolicyLag,
            1,
        ),
        wanMaxQueuedBatches: normalizeWanMaxQueuedBatches(
            options && options.wanMaxQueuedBatches,
            32,
        ),
        localTraining: Boolean(options && options.localTraining),
        learnerOptimizations: normalizeLearnerOptimizations(
            options && options.learnerOptimizations,
        ),
        skipEvaluation: Boolean(options && options.skipEvaluation),
        workerDiagnostics: !options || options.workerDiagnostics !== false,
        logCategories: normalizeLogCategories(options && options.logCategories),
        localOnly: Boolean(options && options.localOnly),
        numLocalEnvs: normalizeLocalEnvs(options && options.numLocalEnvs),
        learnerDevice: normalizeLearnerDevice(options && options.learnerDevice),
        updated_utc: new Date().toISOString(),
    };
    writeJsonAtomic(paths.runtimeTrainingOptionsPath, value);
    return value;
}

function loadConfig() {
    if (!exists(paths.configPath)) {
        throw new Error('Tracked training configuration is missing: ' + paths.configPath);
    }
    const config = readJson(paths.configPath);
    const runtimeOptions = readRuntimeTrainingOptions(
        normalizeThreadedPolicyLag(config.threadedPolicyLag, 1),
        normalizeWanMaxQueuedBatches(config.wanMaxQueuedBatches, 32),
        normalizeLearnerOptimizations(config.learnerOptimizations),
    );
    return runtimeOptions
        ? applyRuntimeTrainingOptions(config, runtimeOptions)
        : applyRuntimeTrainingOptions(config);
}

function resolvePython(config) {
    // The tracked cluster config is shared with Windows. A Linux host should not
    // silently resolve its "python" alias to an incompatible system Python.
    const configured = String(process.env.BEES_PYTHON || config.python || 'python');
    if (process.platform === 'linux' && configured === 'python') {
        try { return resolveCommand('python3.10'); } catch (_) {}
    }
    return resolveCommand(configured);
}

function hostTrainingPlatform() {
    if (process.platform === 'win32') return 'WindowsPlayer';
    if (process.platform === 'linux') return 'LinuxPlayer';
    throw new Error('Central Bees training is supported on Windows and Linux only.');
}

function resolveGit() {
    try {
        return resolveCommand('git');
    } catch (_) {}
    if (process.platform === 'win32') {
        const candidates = [];
        if (process.env.LOCALAPPDATA) {
            const desktopRoot = path.join(process.env.LOCALAPPDATA, 'GitHubDesktop');
            if (exists(desktopRoot)) {
                const apps = fs.readdirSync(desktopRoot, { withFileTypes: true })
                    .filter(entry => entry.isDirectory() && entry.name.startsWith('app-'))
                    .sort((a, b) => b.name.localeCompare(a.name));
                for (const app of apps) {
                    candidates.push(path.join(desktopRoot, app.name, 'resources', 'app', 'git', 'cmd', 'git.exe'));
                }
            }
        }
        if (process.env.ProgramFiles) {
            candidates.push(path.join(process.env.ProgramFiles, 'Git', 'cmd', 'git.exe'));
            candidates.push(path.join(process.env.ProgramFiles, 'Git', 'bin', 'git.exe'));
        }
        const found = candidates.find(exists);
        if (found) return path.resolve(found);
    }
    throw new Error('Git was not found. Install Git and make it available on PATH.');
}

function resolveUnityEditor(config) {
    const configured = process.env.BEES_UNITY_EDITOR || config.unityEditor;
    if (configured && exists(configured)) return path.resolve(configured);
    if (process.platform === 'win32') {
        const root = 'C:\\Program Files\\Unity\\Hub\\Editor';
        if (exists(root)) {
            const versions = fs.readdirSync(root, { withFileTypes: true })
                .filter(entry => entry.isDirectory())
                .map(entry => entry.name)
                .sort((a, b) => b.localeCompare(a));
            for (const version of versions) {
                const candidate = path.join(root, version, 'Editor', 'Unity.exe');
                if (exists(candidate)) return path.resolve(candidate);
            }
        }
    }
    throw new Error('Unity Editor was not found. Set BEES_UNITY_EDITOR or unityEditor in ' + paths.configPath + '.');
}

function runSync(executable, args = [], options = {}) {
    const result = spawnSync(executable, args.map(String), {
        cwd: options.cwd || paths.assetsRoot,
        env: options.env || process.env,
        encoding: options.encoding === null ? null : 'utf8',
        windowsHide: options.windowsHide !== false,
        stdio: options.stdio || ['ignore', 'pipe', 'pipe'],
        timeout: options.timeout,
        maxBuffer: options.maxBuffer || 64 * 1024 * 1024,
    });
    if (result.error) throw result.error;
    if (options.check !== false && result.status !== 0) {
        const stderr = String(result.stderr || '').trim();
        const stdout = String(result.stdout || '').trim();
        throw new Error(
            executable + ' exited with code ' + result.status + ' while running ' + args.join(' ') + '.' +
            (stderr ? '\n' + stderr : stdout ? '\n' + stdout : '')
        );
    }
    return result;
}

function runChecked(executable, args = [], cwd = paths.assetsRoot, env = process.env) {
    // Setup/build helpers can run for minutes. Preserve the old operator's live child output
    // instead of buffering an entire pip/npm/go/archive operation until it exits.
    return runSync(executable, args, {
        cwd,
        env,
        stdio: 'inherit',
    });
}

function waitForSpawn(child, label, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        let settled = false;
        const timer = setTimeout(() => {
            if (settled) return;
            settled = true;
            reject(new Error(label + ' did not report a successful spawn within ' + timeoutMs + ' ms.'));
        }, timeoutMs);

        child.once('spawn', () => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            resolve(child);
        });
        child.once('error', error => {
            if (settled) {
                // Keep a post-spawn error from becoming an unhandled EventEmitter error while
                // still leaving the process lifecycle visible in the operator logs.
                console.error(label + ' process error: ' + error.message);
                return;
            }
            settled = true;
            clearTimeout(timer);
            reject(new Error(label + ' failed to spawn: ' + error.message));
        });
    });
}

function testPythonCode(python, code) {
    const result = runSync(python, ['-c', code], { check: false, stdio: 'ignore' });
    return result.status === 0;
}

function invokePythonJson(python, args, cwd = paths.assetsRoot) {
    const result = runSync(python, args, { cwd });
    const output = String(result.stdout || '').trim();
    if (!output) throw new Error(python + ' produced no JSON output for ' + args[0] + '.');
    try {
        return JSON.parse(output);
    } catch (_) {
        throw new Error('Invalid JSON from ' + args[0] + ': ' + output);
    }
}

function ensureTokenFile(filePath) {
    ensureDir(path.dirname(filePath));
    if (exists(filePath)) {
        const value = readText(filePath).trim();
        if (value) return value;
    }
    const value = crypto.randomBytes(32).toString('hex');
    writeTextAtomic(filePath, value + os.EOL);
    return value;
}

function getGitShortSha() {
    const git = resolveGit();
    const result = runSync(git, ['rev-parse', '--short=12', 'HEAD'], { cwd: paths.assetsRoot });
    const value = String(result.stdout || '').trim();
    if (!value) throw new Error('git rev-parse returned an empty commit id.');
    return value;
}

function getNamedFileSetSha256(entries) {
    const seen = new Set();
    const manifest = entries.map(entry => {
        const name = String(entry.name || '').replaceAll('\\', '/');
        if (!name) throw new Error('Content-hash entry name must be non-empty.');
        if (seen.has(name)) throw new Error('Content-hash entry is duplicated: ' + name);
        seen.add(name);
        if (!exists(entry.filePath)) throw new Error('Content-hash source file is missing: ' + entry.filePath);
        const stat = fs.statSync(entry.filePath);
        return { name, length: stat.size, sha256: sha256File(entry.filePath) };
    }).sort((a, b) => a.name.localeCompare(b.name));
    if (!manifest.length) throw new Error('Content-hash file set must not be empty.');
    return sha256Text(JSON.stringify(manifest));
}

function requestJsonOnce(baseUrl, token, method, requestPath, payload, timeoutMs) {
    const url = new URL(requestPath, baseUrl.endsWith('/') ? baseUrl : baseUrl + '/');
    const transport = url.protocol === 'https:' ? https : http;
    const body = payload === null ? null : Buffer.from(JSON.stringify(payload) + '\n', 'utf8');
    return new Promise((resolve, reject) => {
        let settled = false;
        const finishResolve = value => {
            if (settled) return;
            settled = true;
            resolve(value);
        };
        const finishReject = error => {
            if (settled) return;
            settled = true;
            reject(error);
        };
        const request = transport.request(url, {
            method,
            headers: {
                Authorization: 'Bearer ' + token,
                Accept: 'application/json',
                ...(body ? {
                    'Content-Type': 'application/json',
                    'Content-Length': body.length,
                } : {}),
            },
        }, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(chunk));
            response.on('aborted', () => {
                const error = new Error(
                    'training-control response was aborted for ' +
                    String(method).toUpperCase() + ' ' + requestPath
                );
                error.code = 'ECONNRESET';
                finishReject(error);
            });
            response.on('error', finishReject);
            response.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let value = {};
                try {
                    if (text) value = JSON.parse(text);
                } catch (_) {
                    finishReject(new Error('Control server returned invalid JSON: ' + text));
                    return;
                }
                if (response.statusCode < 200 || response.statusCode >= 300) {
                    finishReject(new Error('Control server HTTP ' + response.statusCode + ': ' + text));
                    return;
                }
                finishResolve(value);
            });
        });
        request.setTimeout(
            timeoutMs,
            () => {
                const error = new Error(
                    'training-control ' + String(method).toUpperCase() +
                    ' request timed out after ' + timeoutMs + ' ms'
                );
                error.code = 'ETIMEDOUT';
                request.destroy(error);
            },
        );
        request.on('error', finishReject);
        if (body) request.write(body);
        request.end();
    });
}

function isTransientControlTransportError(error) {
    const code = String(error && error.code || '').toUpperCase();
    return code === 'ECONNRESET' ||
        code === 'ECONNABORTED' ||
        code === 'ECONNREFUSED' ||
        code === 'EPIPE' ||
        code === 'ETIMEDOUT' ||
        code === 'EHOSTUNREACH' ||
        code === 'ENETUNREACH';
}

async function requestJson(baseUrl, token, method, requestPath, payload = null, timeoutMs = null) {
    const effectiveTimeoutMs = timeoutMs == null
        ? (String(method).toUpperCase() === 'GET' ? 5000 : 30000)
        : Number(timeoutMs);
    const maximumAttempts = 4;
    let lastError = null;
    for (let attempt = 1; attempt <= maximumAttempts; attempt++) {
        try {
            return await requestJsonOnce(
                baseUrl,
                token,
                method,
                requestPath,
                payload,
                effectiveTimeoutMs,
            );
        } catch (error) {
            lastError = error;
            if (!isTransientControlTransportError(error) || attempt >= maximumAttempts) {
                error.message =
                    'training-control ' + String(method).toUpperCase() + ' ' +
                    requestPath + ' failed after ' + attempt + ' attempt(s): ' +
                    error.message;
                throw error;
            }
            const delayMs = 250 * attempt;
            console.warn(
                'Transient training-control transport failure (' +
                String(error.code || error.message) + ') for ' +
                String(method).toUpperCase() + ' ' + requestPath +
                '; retrying attempt ' + (attempt + 1) + '/' + maximumAttempts + '.'
            );
            await sleep(delayMs);
        }
    }
    throw lastError;
}

async function testControl(baseUrl, token) {
    try {
        await requestJson(baseUrl, token, 'GET', '/v1/status', null, 5000);
        return true;
    } catch (_) {
        return false;
    }
}

function powershellExecutable() {
    if (process.platform !== 'win32') return resolveCommand('pwsh');
    try {
        return resolveCommand('powershell.exe');
    } catch (_) {
        return resolveCommand('pwsh.exe');
    }
}

function getProcessIdentity(pid) {
    const id = Number(pid);
    if (!Number.isInteger(id) || id <= 0) return null;
    if (process.platform === 'linux') {
        // /proc stat field 22 is the kernel start tick. Combining it with the
        // boot ID makes the persisted identity safe against PID reuse/reboots.
        // Do not use process.kill(pid, 0): it proves liveness, not ownership.
        try {
            const stat = readText('/proc/' + id + '/stat');
            const closing = stat.lastIndexOf(')');
            if (closing < 0) return null;
            const fields = stat.slice(closing + 2).trim().split(/\s+/);
            const startTicks = fields[19];
            if (!/^\d+$/.test(startTicks || '')) return null;
            const bootId = readText('/proc/sys/kernel/random/boot_id').trim();
            if (!/^[0-9a-f-]{36}$/i.test(bootId)) return null;
            const executable = fs.realpathSync('/proc/' + id + '/exe');
            return {
                pid: id,
                process_start_utc: 'linux-boot-ticks:' + bootId + ':' + startTicks,
                executable_path: executable,
            };
        } catch (_) {
            return null;
        }
    }
    if (process.platform !== 'win32') return null;
    const ps = powershellExecutable();
    const script =
        '$p=Get-Process -Id ' + id + ' -ErrorAction SilentlyContinue;' +
        'if($null -eq $p){exit 3};' +
        '$v=[ordered]@{pid=[int]$p.Id;process_start_utc=$p.StartTime.ToUniversalTime().ToString("o");executable_path=[IO.Path]::GetFullPath([string]$p.Path)};' +
        '$v|ConvertTo-Json -Compress';
    const result = runSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
        check: false,
        stdio: ['ignore', 'pipe', 'ignore'],
    });
    if (result.status !== 0) return null;
    try {
        const value = JSON.parse(String(result.stdout || '').trim());
        return {
            pid: Number(value.pid),
            // Preserve PowerShell's exact round-trip timestamp. Parsing through JS Date truncates
            // Windows process-start precision and weakens the PID-reuse ownership check.
            process_start_utc: String(value.process_start_utc || ''),
            executable_path: path.resolve(String(value.executable_path)),
        };
    } catch (_) {
        return null;
    }
}

function isProcessAlive(pid) {
    const id = Number(pid);
    if (!Number.isInteger(id) || id <= 0) return false;
    try {
        process.kill(id, 0);
        return true;
    } catch (_) {
        return false;
    }
}

function samePath(left, right) {
    if (!left || !right) return false;
    const a = path.resolve(String(left));
    const b = path.resolve(String(right));
    if (process.platform === 'win32') return a.toLowerCase() === b.toLowerCase();
    // /proc/PID/exe resolves interpreter symlinks. The configured Python may
    // be a venv launcher or /usr/bin/python3.10 symlink to that same binary.
    try {
        return fs.realpathSync(a) === fs.realpathSync(b);
    } catch (_) {
        return a === b;
    }
}

function testManagedProcessIdentity(state, expectedExecutable = '') {
    if (!state || !state.pid || !state.process_start_utc || !state.executable_path) return false;
    const current = getProcessIdentity(Number(state.pid));
    if (!current) return false;
    if (String(current.process_start_utc) !== String(state.process_start_utc)) return false;
    if (!samePath(current.executable_path, state.executable_path)) return false;
    if (expectedExecutable && !samePath(current.executable_path, expectedExecutable)) return false;
    return true;
}

function getStateReferencedLivePid(state) {
    const pid = Number(state && state.pid);
    return Number.isInteger(pid) && pid > 0 && isProcessAlive(pid) ? pid : 0;
}

function findManagedProcessByOwnerToken(expectedExecutable, ownerToken, label) {
    if (!ownerToken) return null;
    if (process.platform === 'linux') {
        const matches = [];
        for (const entry of fs.readdirSync('/proc')) {
            if (!/^\d+$/.test(entry)) continue;
            const pid = Number(entry);
            let argv;
            try {
                argv = fs.readFileSync('/proc/' + entry + '/cmdline', 'utf8')
                    .split('\0').filter(Boolean);
            } catch (error) {
                if (['ENOENT', 'ESRCH', 'EACCES', 'EPERM'].includes(error.code)) continue;
                throw error;
            }
            if (!argv.some((arg, index) =>
                arg === '--owner-token' && argv[index + 1] === String(ownerToken))) continue;
            const identity = getProcessIdentity(pid);
            if (identity && samePath(identity.executable_path, expectedExecutable)) {
                matches.push(identity);
            }
        }
        if (matches.length > 1) {
            throw new Error('Multiple ' + label + ' processes claim managed owner token ' +
                ownerToken + ' (PIDs ' + matches.map(item => item.pid).join(',') +
                '). Refusing ambiguous recovery.');
        }
        return matches[0] || null;
    }
    if (process.platform !== 'win32') return null;
    const ps = powershellExecutable();
    const env = {
        ...process.env,
        BEES_QUERY_EXECUTABLE: path.resolve(expectedExecutable),
        BEES_QUERY_OWNER_TOKEN: String(ownerToken),
    };
    const script =
        '$e=[IO.Path]::GetFullPath($env:BEES_QUERY_EXECUTABLE);' +
        '$t=$env:BEES_QUERY_OWNER_TOKEN;' +
        '$m=@(Get-CimInstance Win32_Process -ErrorAction Stop|Where-Object{$_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath([string]$_.ExecutablePath),$e,[StringComparison]::OrdinalIgnoreCase) -and ([string]$_.CommandLine).Contains($t)}|Select-Object ProcessId,ExecutablePath,CommandLine);' +
        '$m|ConvertTo-Json -Compress';
    const result = runSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
        env,
        check: false,
        stdio: ['ignore', 'pipe', 'pipe'],
    });
    if (result.status !== 0) throw new Error('Could not inspect ' + label + ' processes.');
    const raw = String(result.stdout || '').trim();
    if (!raw) return null;
    let matches = JSON.parse(raw);
    if (!Array.isArray(matches)) matches = [matches];
    if (matches.length > 1) {
        throw new Error('Multiple ' + label + ' processes claim managed owner token ' + ownerToken + ' (PIDs ' + matches.map(x => x.ProcessId).join(',') + '). Refusing ambiguous recovery.');
    }
    if (!matches.length) return null;
    const identity = getProcessIdentity(Number(matches[0].ProcessId));
    if (!identity || !samePath(identity.executable_path, expectedExecutable)) {
        throw new Error('Recovered ' + label + ' process does not match its persisted executable identity.');
    }
    return identity;
}

function stopManagedProcessTree(state, expectedExecutable, label) {
    if (!testManagedProcessIdentity(state)) {
        const pid = getStateReferencedLivePid(state);
        if (pid > 0) {
            throw new Error('Refusing to stop ' + label + ' PID ' + pid + ' because its persisted PID/start-time/executable ownership does not match the live process. The PID may have been reused.');
        }
        return false;
    }
    if (expectedExecutable && !testManagedProcessIdentity(state, expectedExecutable)) {
        console.log(label + ' desired executable changed; safely replacing verified owned process ' + state.executable_path + '.');
    }
    if (process.platform === 'win32') {
        runSync('taskkill.exe', ['/PID', String(state.pid), '/T', '/F'], { check: false, stdio: 'ignore' });
    } else {
        try { process.kill(-Number(state.pid), 'SIGKILL'); } catch (_) {
            try { process.kill(Number(state.pid), 'SIGKILL'); } catch (_) {}
        }
    }
    return true;
}

function rotateManagedLog(filePath) {
    ensureDir(path.dirname(filePath));
    const previous = filePath + '.previous';
    removeIfExists(previous);
    if (exists(filePath)) {
        fs.renameSync(filePath, previous);
    }
}

function readTail(filePath, maxLines = 1000, maxBytes = 1024 * 1024) {
    if (!exists(filePath)) return [];
    const stat = fs.statSync(filePath);
    const start = Math.max(0, stat.size - maxBytes);
    const fd = fs.openSync(filePath, 'r');
    try {
        const buffer = Buffer.alloc(stat.size - start);
        fs.readSync(fd, buffer, 0, buffer.length, start);
        let lines = buffer.toString('utf8').split(/\r?\n/);
        if (start > 0 && lines.length) lines = lines.slice(1);
        if (lines.length > maxLines) lines = lines.slice(-maxLines);
        return lines;
    } finally {
        fs.closeSync(fd);
    }
}

module.exports = {
    GAMEPLAY_SERVER_PORT,
    applyRuntimeTrainingOptions,
    atomicReplace,
    ensureDir,
    ensureTokenFile,
    exists,
    findManagedProcessByOwnerToken,
    getGitShortSha,
    getNamedFileSetSha256,
    getProcessIdentity,
    getStateReferencedLivePid,
    hostTrainingPlatform,
    hmacSha256,
    invokePythonJson,
    isProcessAlive,
    loadConfig,
    path,
    paths,
    powershellExecutable,
    readJson,
    readRuntimeTrainingOptions,
    readTail,
    rotateManagedLog,
    removeUtf8BomIfPresent,
    readText,
    removeIfExists,
    requestJson,
    resolveCommand,
    resolveGit,
    resolveNpmInvocation,
    resolvePython,
    resolveUnityEditor,
    npmInvocationFromCommand,
    runChecked,
    runSync,
    samePath,
    saveRuntimeTrainingOptions,
    sha256File,
    sha256Text,
    sleep,
    sleepSync,
    spawn,
    stopManagedProcessTree,
    testControl,
    testManagedProcessIdentity,
    testPythonCode,
    waitForSpawn,
    writeJsonAtomic,
    writeTextAtomic,
};
