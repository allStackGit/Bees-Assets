'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
    ensureDir,
    exists,
    getGitShortSha,
    invokePythonJson,
    paths,
    readJson,
    readText,
    removeIfExists,
    resolveGit,
    resolvePython,
    runChecked,
    sha256Text,
    testPythonCode,
    writeJsonAtomic,
    writeTextAtomic,
} = require('./common');

function pruneDirectories(root, keepPaths, keepNewest, temporarySuffix = '') {
    if (!exists(root)) return;
    const keep = new Set([...keepPaths].filter(Boolean).map(value => path.resolve(String(value)).toLowerCase()));
    const entries = fs.readdirSync(root, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => {
            const full = path.join(root, entry.name);
            const stat = fs.statSync(full);
            return { name: entry.name, full, mtimeMs: stat.mtimeMs };
        });

    const now = Date.now();
    for (const entry of entries) {
        if (temporarySuffix && entry.name.endsWith(temporarySuffix) && now - entry.mtimeMs > 60 * 60 * 1000) {
            removeIfExists(entry.full, { recursive: true });
        }
    }

    const normal = entries
        .filter(entry => !temporarySuffix || !entry.name.endsWith(temporarySuffix))
        .sort((a, b) => b.mtimeMs - a.mtimeMs);
    for (const entry of normal.slice(0, keepNewest)) keep.add(path.resolve(entry.full).toLowerCase());
    for (const entry of normal) {
        if (!keep.has(path.resolve(entry.full).toLowerCase())) {
            removeIfExists(entry.full, { recursive: true });
        }
    }
}

function referencedLearnerRoots(extraExecutables = []) {
    const roots = new Set();
    const venvBase = path.join(paths.runtimeRoot, 'LearnerPython');
    for (const executable of extraExecutables) {
        if (!executable) continue;
        const full = path.resolve(String(executable));
        const relative = path.relative(venvBase, full);
        if (!relative.startsWith('..') && !path.isAbsolute(relative)) {
            const first = relative.split(path.sep)[0];
            if (first) roots.add(path.join(venvBase, first));
        }
    }
    for (const statePath of [
        paths.centralRuntimePointerPath,
        paths.centralRuntimeStatePath,
        paths.centralAgentStatePath,
    ]) {
        if (!exists(statePath)) continue;
        try {
            const state = readJson(statePath);
            for (const key of ['python_executable', 'learner_python']) {
                const executable = String(state[key] || '').trim();
                if (!executable) continue;
                const relative = path.relative(venvBase, path.resolve(executable));
                if (!relative.startsWith('..') && !path.isAbsolute(relative)) {
                    const first = relative.split(path.sep)[0];
                    if (first) roots.add(path.join(venvBase, first));
                }
            }
        } catch (_) {}
    }
    return roots;
}

function pruneLearnerPythonRuntimes(extraExecutables = [], keepNewest = 3) {
    const base = path.join(paths.runtimeRoot, 'LearnerPython');
    pruneDirectories(base, referencedLearnerRoots(extraExecutables), keepNewest);
}

function ensureLearnerPython(config, requirementsRoot = '') {
    const root = requirementsRoot ? path.resolve(requirementsRoot) : path.join(paths.assetsRoot, 'Training');
    const learnerRequirements = path.join(root, 'bees_learner_requirements.txt');
    const remoteRequirements = path.join(root, 'bees_remote_requirements.txt');
    if (!exists(learnerRequirements)) {
        throw new Error('Learner Python requirements are missing: ' + learnerRequirements);
    }
    if (!exists(remoteRequirements)) {
        throw new Error('Shared Python requirements are missing: ' + remoteRequirements);
    }

    const basePython = resolvePython(config);
    if (!testPythonCode(basePython, 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)')) {
        throw new Error("Bees learner requires Python 3.10. Configured python resolved to '" + basePython + "'.");
    }

    const requirementsHash = sha256Text(
        readText(learnerRequirements) + os.EOL + readText(remoteRequirements)
    );
    const venvBase = path.join(paths.runtimeRoot, 'LearnerPython');
    const venvRoot = path.join(venvBase, requirementsHash);
    const venvPython = process.platform === 'win32'
        ? path.join(venvRoot, 'Scripts', 'python.exe')
        : path.join(venvRoot, 'bin', 'python');

    if (!exists(venvPython)) {
        console.log('Creating release-isolated learner Python environment at ' + venvRoot + '...');
        ensureDir(venvBase);
        runChecked(basePython, ['-m', 'venv', venvRoot], paths.assetsRoot);
    }

    const stampPath = path.join(venvRoot, 'bees-requirements.sha256');
    const currentStamp = exists(stampPath) ? readText(stampPath).trim() : '';
    const preflight = 'import sys, mlagents, torch, numpy, onnxruntime; raise SystemExit(0 if sys.version_info[:2] == (3,10) else 1)';
    let importsOk = currentStamp === requirementsHash && testPythonCode(venvPython, preflight);

    if (!importsOk) {
        console.log('Installing central learner dependencies for runtime ' + requirementsHash + '...');
        runChecked(venvPython, ['-m', 'pip', 'install', '--upgrade', 'pip'], root);
        runChecked(venvPython, ['-m', 'pip', 'install', '-r', learnerRequirements], root);
        importsOk = testPythonCode(venvPython, preflight);
        if (!importsOk) throw new Error('Central learner Python dependency preflight failed after installation.');
        writeTextAtomic(stampPath, requirementsHash, 'ascii');
    }

    pruneLearnerPythonRuntimes([venvPython]);
    return path.resolve(venvPython);
}

function newReleaseTrainingRuntime(python, buildId, sourceCommit, archivePath) {
    if (!exists(paths.releaseRuntimeScript)) {
        throw new Error('Training runtime packager is missing: ' + paths.releaseRuntimeScript);
    }
    return invokePythonJson(python, [
        paths.releaseRuntimeScript, 'package',
        '--assets-root', paths.assetsRoot,
        '--output', archivePath,
        '--build-id', buildId,
        '--source-commit', sourceCommit,
    ]);
}

function saveLatestRelease(release) {
    ensureDir(path.dirname(paths.latestReleasePath));
    writeJsonAtomic(paths.latestReleasePath, release);
}

function getLatestRelease() {
    if (!exists(paths.latestReleasePath)) {
        throw new Error("No release exists. Run '.\\Assets\\bees.ps1 build' first.");
    }
    return readJson(paths.latestReleasePath);
}

function resolveReleaseTrainingRuntime(python, release, allowLegacyPin = false) {
    let runtime = release.training_runtime;
    if (!runtime) {
        if (!allowLegacyPin) {
            throw new Error('Release ' + release.build_id + ' has no immutable training runtime. Rebuild the release.');
        }
        const legacyBuild = String(release.build_id || '').trim();
        if (!legacyBuild) throw new Error('Legacy release has no build_id.');
        const packageRoot = path.join(paths.buildsRoot, 'Packages', legacyBuild);
        ensureDir(packageRoot);
        const archive = path.join(packageRoot, 'training-runtime.zip');
        console.warn(
            'Release ' + legacyBuild +
            ' predates immutable training runtimes. Pinning the current Training runtime once for recovery; the next build will pin its runtime at build time.'
        );
        runtime = newReleaseTrainingRuntime(python, legacyBuild, getGitShortSha(), archive);
        runtime.legacy_pinned_after_build = true;
        release.training_runtime = runtime;
        if (release.schema_version != null) release.schema_version = 3;
        saveLatestRelease(release);
    }

    const archivePath = String(runtime.archive || '').trim();
    const archiveSha = String(runtime.archive_sha256 || '').trim().toLowerCase();
    const runtimeVersion = String(runtime.runtime_version || '').trim().toLowerCase();
    const buildId = String(release.build_id || '').trim();
    if (!archivePath || !archiveSha || !runtimeVersion) {
        throw new Error('Release ' + buildId + ' has incomplete immutable training runtime metadata.');
    }

    return invokePythonJson(python, [
        paths.releaseRuntimeScript, 'verify',
        '--archive', archivePath,
        '--expected-sha256', archiveSha,
        '--expected-version', runtimeVersion,
        '--expected-build-id', buildId,
    ]);
}

function installReleaseTrainingRuntime(python, release, allowLegacyPin = false) {
    const runtime = resolveReleaseTrainingRuntime(python, release, allowLegacyPin);
    return invokePythonJson(python, [
        paths.releaseRuntimeScript, 'install',
        '--archive', String(runtime.archive),
        '--destination-root', paths.releaseRuntimeInstallRoot,
        '--expected-sha256', String(runtime.archive_sha256),
        '--expected-version', String(runtime.runtime_version),
        '--expected-build-id', String(release.build_id),
    ]);
}

function releaseRuntimeKeepRoots(extraRoots = []) {
    const keep = new Set(extraRoots.filter(Boolean).map(String));
    for (const statePath of [
        paths.centralRuntimePointerPath,
        paths.centralRuntimeStatePath,
        paths.centralAgentStatePath,
    ]) {
        if (!exists(statePath)) continue;
        try {
            const state = readJson(statePath);
            for (const key of ['runtime_root', 'release_runtime_root']) {
                if (state[key]) keep.add(String(state[key]));
            }
        } catch (_) {}
    }
    return keep;
}

function pruneReleaseTrainingRuntimes(extraRoots = [], keepNewest = 4) {
    pruneDirectories(
        paths.releaseRuntimeInstallRoot,
        releaseRuntimeKeepRoots(extraRoots),
        keepNewest,
        '.tmp',
    );
}

function newTrainingRunPlan(python, options = {}) {
    if (!exists(paths.runLifecycleScript)) {
        throw new Error('Training run lifecycle helper is missing: ' + paths.runLifecycleScript);
    }
    if (options.buildId && !options.forceNew) {
        throw new Error('-BuildId is only valid for a forced-new training run plan.');
    }
    ensureDir(paths.runLifecycleRoot);
    ensureDir(paths.runtimeRoot);
    removeIfExists(paths.runPlanPath);
    const args = [
        paths.runLifecycleScript, 'plan',
        '--assets-root', paths.assetsRoot,
        '--state', paths.runStatePath,
        '--out', paths.runPlanPath,
    ];
    if (options.forceNew) {
        args.push('--force-new');
        if (options.buildId) args.push('--build-id', String(options.buildId));
        const environmentJson = JSON.stringify([...(options.environmentArgs || [])].map(String));
        args.push('--environment-args-base64', Buffer.from(environmentJson, 'utf8').toString('base64'));
    }
    runChecked(python, args, paths.assetsRoot);
    return readJson(paths.runPlanPath);
}

function commitTrainingRunPlan(python) {
    runChecked(python, [
        paths.runLifecycleScript, 'commit',
        '--state', paths.runStatePath,
        '--plan', paths.runPlanPath,
    ], paths.assetsRoot);
}

function getPendingForcedNewRunPlan() {
    if (!exists(paths.runPlanPath)) return null;
    const plan = readJson(paths.runPlanPath);
    return plan && plan.forced_new_run ? plan : null;
}

function getTrainingCompatibilityFingerprint(python) {
    return invokePythonJson(python, [
        paths.runLifecycleScript, 'fingerprint',
        '--assets-root', paths.assetsRoot,
    ]);
}

function ensureRunLifecycleMatchesRelease(python, release) {
    const releaseRun = String(release.run_id || '').trim();
    const releaseKey = String(release.compatibility_key || '').trim().toLowerCase();
    if (!releaseRun || !releaseKey) throw new Error('Release is missing run lifecycle identity.');

    let state = null;
    if (exists(paths.runStatePath)) state = readJson(paths.runStatePath);
    if (state &&
        String(state.run_id || '').trim() === releaseRun &&
        String(state.compatibility_key || '').trim().toLowerCase() === releaseKey) {
        return;
    }

    if (exists(paths.runPlanPath)) {
        const plan = readJson(paths.runPlanPath);
        if (plan &&
            String(plan.run_id || '').trim() === releaseRun &&
            String(plan.compatibility_key || '').trim().toLowerCase() === releaseKey) {
            commitTrainingRunPlan(python);
            console.log('Recovered pending training run lifecycle commit for ' + releaseRun + '.');
            return;
        }
    }

    throw new Error(
        'Run lifecycle state disagrees with latest release. lifecycle=' +
        (state ? String(state.run_id || '').trim() : '(missing)') +
        ' release=' + releaseRun
    );
}

function convertReleaseToForcedRunPlan(release, plan, outgoingRun) {
    const copy = JSON.parse(JSON.stringify(release));
    copy.run_id = String(plan.run_id);
    copy.previous_run_id = outgoingRun;
    copy.compatibility_key = String(plan.compatibility_key);
    copy.incompatible = true;
    copy.contract = plan.contract;
    return copy;
}

function completeForcedNewRunPlan(plan, release) {
    if (!exists(paths.runPlanPath)) return;
    const current = readJson(paths.runPlanPath);
    const currentBuild = String(current.build_id || '').trim();
    const buildMatches = !currentBuild || currentBuild === String(release.build_id || '').trim();
    if (!current ||
        !current.forced_new_run ||
        !buildMatches ||
        String(current.run_id || '').trim() !== String(release.run_id || '').trim() ||
        String(current.compatibility_key || '').trim().toLowerCase() !== String(release.compatibility_key || '').trim().toLowerCase()) {
        throw new Error('Refusing to clear a forced-new run plan that no longer matches the completed release.');
    }
    removeIfExists(paths.runPlanPath);
}

function archiveTrainingRun(python, runId, reason) {
    if (!runId) return;
    if (!exists(paths.archiveRunScript)) {
        throw new Error('Training log archive helper is missing: ' + paths.archiveRunScript);
    }
    console.log('Archiving and pushing training logs for run ' + runId + ' (' + reason + ')...');
    runChecked(python, [
        paths.archiveRunScript,
        '--assets-root', paths.assetsRoot,
        '--bees-root', paths.beesRoot,
        '--run-id', String(runId),
        '--reason', String(reason),
        '--git-executable', resolveGit(),
    ], paths.assetsRoot);
}

module.exports = {
    archiveTrainingRun,
    commitTrainingRunPlan,
    completeForcedNewRunPlan,
    convertReleaseToForcedRunPlan,
    ensureLearnerPython,
    ensureRunLifecycleMatchesRelease,
    getLatestRelease,
    getPendingForcedNewRunPlan,
    getTrainingCompatibilityFingerprint,
    installReleaseTrainingRuntime,
    newReleaseTrainingRuntime,
    newTrainingRunPlan,
    pruneLearnerPythonRuntimes,
    pruneReleaseTrainingRuntimes,
    resolveReleaseTrainingRuntime,
    saveLatestRelease,
};
