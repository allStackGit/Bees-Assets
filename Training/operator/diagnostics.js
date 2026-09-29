'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

const {
    ensureDir,
    ensureTokenFile,
    exists,
    getStateReferencedLivePid,
    loadConfig,
    paths,
    readJson,
    readText,
    removeIfExists,
    resolvePython,
    runChecked,
    sleep,
    testControl,
    writeJsonAtomic,
} = require('./common');
const { getStatus } = require('./control');
const { getRunningCentralAgentPid } = require('./central');
const { getActiveRunId } = require('./build');
const { getStatusFrameLines } = require('./status');
const { killProcessTree } = require('./validation');

const DIAGNOSTIC_BENCHMARK_MATCHES = 20;
const DIAGNOSTIC_BENCHMARK_EPISODE_TIMEOUT_SECONDS = 30;
const DIAGNOSTIC_BENCHMARK_STARTUP_GRACE_MS = 120000;

function diagnosticBenchmarkTimeoutMs(
    matches = DIAGNOSTIC_BENCHMARK_MATCHES,
    episodeTimeoutSeconds = DIAGNOSTIC_BENCHMARK_EPISODE_TIMEOUT_SECONDS,
) {
    return Math.max(
        180000,
        Number(matches) * Number(episodeTimeoutSeconds) * 1000 +
            DIAGNOSTIC_BENCHMARK_STARTUP_GRACE_MS,
    );
}

function boundedDiagnosticTail(filePath, lines = 40) {
    if (!filePath || !exists(filePath)) return '';
    try {
        return readText(filePath).split(/\r?\n/).slice(-lines).join('\n');
    } catch (_) {
        return '';
    }
}

async function requestCentralDiagnosticModelSnapshot(status, targetRunId, outputPath) {
    let result = {
        schema_version: 1,
        status: 'skipped',
        run_id: targetRunId,
        requested_utc: new Date().toISOString(),
        reason: '',
    };

    try {
        if (!status) {
            result.reason = 'training control is unavailable';
            return;
        }
        const activeRun = status.desired && status.desired.run_id
            ? String(status.desired.run_id).trim()
            : '';
        if (!targetRunId) {
            result.reason = 'no active run could be determined';
            return;
        }
        if (activeRun !== targetRunId) {
            result.reason =
                'requested run ' + targetRunId + ' is not the active run ' + activeRun;
            return;
        }

        const central = (status.trainers || []).find(
            record => String(record.trainer_id) === 'central-learner'
        );
        if (!central) {
            result.reason = 'central learner is not registered';
            return;
        }
        if (central.stale || String(central.process_state) !== 'running') {
            result.reason =
                'central learner is not actively training (state=' +
                central.process_state + ' stale=' + central.stale + ')';
            return;
        }
        if (getRunningCentralAgentPid() <= 0) {
            result.reason = 'managed central learner process is not running';
            return;
        }

        ensureDir(paths.centralAgentInstallRoot);
        const requestId = require('node:crypto').randomBytes(16).toString('hex');
        removeIfExists(paths.centralModelSnapshotResponsePath);
        removeIfExists(paths.centralModelSnapshotRequestPath);
        writeJsonAtomic(paths.centralModelSnapshotRequestPath, {
            schema_version: 1,
            request_id: requestId,
            run_id: targetRunId,
            requested_utc: new Date().toISOString(),
        });

        console.log('Requesting current learner ONNX snapshot...');
        const deadline = Date.now() + 60000;
        while (Date.now() < deadline) {
            if (exists(paths.centralModelSnapshotResponsePath)) {
                try {
                    const response = readJson(paths.centralModelSnapshotResponsePath);
                    if (String(response.request_id) === requestId) {
                        result = response;
                        return;
                    }
                } catch (_) {}
            }
            await sleep(200);
        }
        result.status = 'timeout';
        result.reason =
            'live learner did not complete the diagnostic model snapshot within 60 seconds';
    } catch (error) {
        result.status = 'failed';
        result.reason = error.name + ': ' + error.message;
    } finally {
        removeIfExists(paths.centralModelSnapshotRequestPath);
        writeJsonAtomic(outputPath, result);
    }
}

async function waitForExit(child, timeoutMs) {
    let spawnError = null;
    child.once('error', error => {
        spawnError = error;
    });

    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (spawnError) throw spawnError;
        if (child.exitCode !== null || child.signalCode !== null) {
            return { timeout: false, code: child.exitCode };
        }
        await sleep(100);
    }

    // taskkill/process.kill can return before inherited file handles have fully closed. Wait for
    // process termination before diagnostic cleanup so a best-effort timeout cannot make bundle
    // collection fail with a transient sharing violation.
    killProcessTree(child);
    const killDeadline = Date.now() + 5000;
    while (Date.now() < killDeadline) {
        if (child.exitCode !== null || child.signalCode !== null) break;
        await sleep(100);
    }
    return { timeout: true, code: child.exitCode };
}

async function invokeCentralDiagnosticBenchmark(targetRunId, snapshotJson, outputJson) {
    let result = {
        schema_version: 1,
        status: 'skipped',
        benchmark: 'deterministic-wasp-vs-gunship-v1',
        run_id: targetRunId,
        requested_utc: new Date().toISOString(),
        reason: '',
    };
    let stdout = '';
    let stderr = '';

    try {
        if (!exists(paths.diagnosticBenchmarkScript)) {
            result.reason =
                'diagnostic benchmark helper is missing: ' +
                paths.diagnosticBenchmarkScript;
            return;
        }
        if (!exists(snapshotJson)) {
            result.reason = 'live model snapshot metadata is unavailable';
            return;
        }

        const snapshot = readJson(snapshotJson);
        if (String(snapshot.status) !== 'succeeded') {
            result.reason = 'live model snapshot status is ' + String(snapshot.status);
            return;
        }
        if (String(snapshot.run_id) !== targetRunId) {
            result.reason =
                'live model snapshot belongs to run ' + String(snapshot.run_id);
            return;
        }

        const modelPath = String(snapshot.model_path || '');
        if (!modelPath || !exists(modelPath)) {
            result.reason = 'live model snapshot file is unavailable: ' + modelPath;
            return;
        }

        const currentBuildPath = path.join(
            paths.centralAgentInstallRoot,
            'current.json',
        );
        if (!exists(currentBuildPath)) {
            result.reason = 'central learner has no installed current build manifest';
            return;
        }

        const currentBuild = readJson(currentBuildPath);
        const environmentPath = String(currentBuild.entrypoint || '');
        if (!environmentPath || !exists(environmentPath)) {
            result.reason =
                'central learner training executable is unavailable: ' +
                environmentPath;
            return;
        }

        let learnerPython = '';
        const currentBuildId = String(currentBuild.build_id || '').trim();
        if (exists(paths.centralRuntimeStatePath)) {
            try {
                const runtimeState = readJson(paths.centralRuntimeStatePath);
                const runtimeBuild = String(runtimeState.build_id || '').trim();
                if (
                    currentBuildId &&
                    runtimeBuild &&
                    runtimeBuild !== currentBuildId
                ) {
                    result.reason =
                        'central learner runtime/build identity is inconsistent: build=' +
                        currentBuildId + ' runtime=' + runtimeBuild;
                    return;
                }
                learnerPython = String(runtimeState.python_executable || '');
            } catch (_) {
                result.reason =
                    'central learner active runtime state is unreadable: ' +
                    paths.centralRuntimeStatePath;
                return;
            }
        }

        if (!learnerPython && exists(paths.centralAgentStatePath)) {
            try {
                learnerPython = String(
                    readJson(paths.centralAgentStatePath).learner_python || ''
                );
            } catch (_) {}
        }
        if (!learnerPython) {
            learnerPython = path.join(
                paths.runtimeRoot,
                'LearnerPython',
                'Scripts',
                'python.exe',
            );
        }
        if (!exists(learnerPython)) {
            result.reason =
                'managed learner Python is unavailable: ' + learnerPython;
            return;
        }

        const benchmarkId = require('node:crypto').randomBytes(12).toString('hex');
        stdout = path.join(
            paths.runtimeRoot,
            'diagnostic-benchmark-' + benchmarkId + '.out.log',
        );
        stderr = path.join(
            paths.runtimeRoot,
            'diagnostic-benchmark-' + benchmarkId + '.err.log',
        );
        const stdoutFd = fs.openSync(stdout, 'a');
        const stderrFd = fs.openSync(stderr, 'a');
        let child;
        try {
            console.log(
                'Running deterministic diagnostic benchmark (' +
                DIAGNOSTIC_BENCHMARK_MATCHES + ' fixed 1v1 matches)...'
            );
            child = spawn(
                learnerPython,
                [
                    paths.diagnosticBenchmarkScript,
                    '--env', environmentPath,
                    '--model', modelPath,
                    '--output', outputJson,
                    '--matches', String(DIAGNOSTIC_BENCHMARK_MATCHES),
                ],
                {
                    cwd: paths.assetsRoot,
                    windowsHide: true,
                    stdio: ['ignore', stdoutFd, stderrFd],
                },
            );
        } finally {
            fs.closeSync(stdoutFd);
            fs.closeSync(stderrFd);
        }

        const timeoutMs = diagnosticBenchmarkTimeoutMs();
        const exit = await waitForExit(child, timeoutMs);
        if (exit.timeout) {
            result.status = 'timeout';
            result.reason =
                'deterministic benchmark exceeded ' +
                Math.round(timeoutMs / 1000) + ' seconds';
            result.stdout_tail = boundedDiagnosticTail(stdout);
            result.stderr_tail = boundedDiagnosticTail(stderr);
            writeJsonAtomic(outputJson, result);
            console.warn(result.reason);
            return;
        }

        if (exit.code !== 0) {
            const stdoutTail = boundedDiagnosticTail(stdout);
            const stderrTail = boundedDiagnosticTail(stderr);
            if (exists(outputJson)) {
                try {
                    const failure = readJson(outputJson);
                    failure.stdout_tail = stdoutTail;
                    failure.stderr_tail = stderrTail;
                    writeJsonAtomic(outputJson, failure);
                    console.warn(
                        'Deterministic benchmark failed: ' +
                        String(failure.error || failure.reason || '')
                    );
                    return;
                } catch (_) {}
            }
            result.status = 'failed';
            result.reason =
                'benchmark process exited with code ' + exit.code +
                (stderrTail ? ': ' + stderrTail.split(/\r?\n/).slice(-1)[0] : '');
            result.stdout_tail = stdoutTail;
            result.stderr_tail = stderrTail;
            writeJsonAtomic(outputJson, result);
            console.warn(result.reason);
            return;
        }

        if (!exists(outputJson)) {
            result.status = 'failed';
            result.reason =
                'benchmark process succeeded without writing its result JSON';
            writeJsonAtomic(outputJson, result);
            console.warn(result.reason);
        }
    } catch (error) {
        result.status = 'failed';
        result.reason = error.name + ': ' + error.message;
        writeJsonAtomic(outputJson, result);
        console.warn('Deterministic benchmark failed: ' + result.reason);
    } finally {
        if (stdout) removeIfExists(stdout);
        if (stderr) removeIfExists(stderr);
        if (!exists(outputJson)) writeJsonAtomic(outputJson, result);
    }
}

async function invokeBundle(options = {}) {
    const config = loadConfig();
    const python = resolvePython(config);
    if (!exists(paths.diagnosticBundleScript)) {
        throw new Error(
            'Training diagnostic bundle helper is missing: ' +
            paths.diagnosticBundleScript
        );
    }

    ensureDir(paths.runtimeRoot);
    const admin = ensureTokenFile(paths.adminTokenPath);
    const bundleId = require('node:crypto').randomBytes(12).toString('hex');
    const statusJson = path.join(
        paths.runtimeRoot,
        'diagnostic-status-' + bundleId + '.json',
    );
    const statusText = path.join(
        paths.runtimeRoot,
        'diagnostic-status-' + bundleId + '.txt',
    );
    const snapshotJson = path.join(
        paths.runtimeRoot,
        'diagnostic-model-snapshot-' + bundleId + '.json',
    );
    const benchmarkJson = path.join(
        paths.runtimeRoot,
        'diagnostic-deterministic-benchmark-' + bundleId + '.json',
    );
    const runtimeStateJson = path.join(
        paths.runtimeRoot,
        'diagnostic-runtime-state-' + bundleId + '.json',
    );
    let status = null;
    let controlOnline = false;

    try {
        try {
            controlOnline = await testControl(String(config.controlUrl), admin);
            if (controlOnline) {
                status = await getStatus(config, admin);
            } else {
                console.warn(
                    'Training control is offline; collecting the diagnostic bundle from durable local state.'
                );
            }
        } catch (error) {
            controlOnline = false;
            console.warn(
                'Could not query live training-control state: ' + error.message
            );
        }

        const targetRun = options.runId
            ? String(options.runId)
            : (
                status && status.desired && status.desired.run_id
                    ? String(status.desired.run_id).trim()
                    : await getActiveRunId(config, '')
            );
        await requestCentralDiagnosticModelSnapshot(
            status,
            targetRun,
            snapshotJson,
        );
        if (options.evaluate) {
            await invokeCentralDiagnosticBenchmark(
                targetRun,
                snapshotJson,
                benchmarkJson,
            );
        } else {
            console.log(
                'Deterministic policy evaluation skipped; use bundle -Evaluate to include it.'
            );
        }

        if (controlOnline) {
            try {
                status = await getStatus(config, admin);
                writeJsonAtomic(statusJson, status);
            } catch (error) {
                controlOnline = false;
                console.warn(
                    'Could not capture live training-control JSON: ' + error.message
                );
            }
        }

        if (controlOnline) {
            try {
                const lines = await getStatusFrameLines(config, admin);
                fs.writeFileSync(statusText, lines.join('\n') + '\n', 'utf8');
            } catch (error) {
                console.warn(
                    'Could not capture readable training status: ' + error.message
                );
            }
        } else {
            let serverSupervisor = { state_file_present: false };
            if (exists(paths.serverStatePath)) {
                try {
                    const persisted = readJson(paths.serverStatePath);
                    serverSupervisor = {
                        state_file_present: true,
                        pid: Number(persisted.pid || 0),
                        pid_alive: Boolean(getStateReferencedLivePid(persisted)),
                        process_start_utc: String(persisted.process_start_utc || ''),
                        executable_path: String(persisted.executable_path || ''),
                        status: String(persisted.status || ''),
                        source_hash: String(persisted.source_hash || ''),
                        runtime_root: String(persisted.runtime_root || ''),
                        config_hash: String(persisted.config_hash || ''),
                    };
                } catch (error) {
                    serverSupervisor = {
                        state_file_present: true,
                        unreadable: error.name + ': ' + error.message,
                    };
                }
            }
            writeJsonAtomic(runtimeStateJson, {
                schema_version: 1,
                captured_utc: new Date().toISOString(),
                control_online: false,
                run_id: targetRun || '',
                server_supervisor: serverSupervisor,
            });
            const offlineLines = [
                'Bees distributed learning status  ' + new Date().toLocaleString(),
                '='.repeat(78),
                'Server: OFFLINE/UNREACHABLE - diagnostic bundle is using durable local state',
                'Run: ' + (targetRun || '(unknown)'),
                'Server supervisor: pid=' +
                    (serverSupervisor.pid || '-') +
                    ' pid_alive=' + Boolean(serverSupervisor.pid_alive) +
                    ' state=' + (serverSupervisor.status || '-') +
                    ' runtime=' + String(serverSupervisor.source_hash || '').slice(0, 12),
            ];
            fs.writeFileSync(statusText, offlineLines.join('\n') + '\n', 'utf8');
        }

        const args = [
            paths.diagnosticBundleScript,
            '--bees-root', paths.beesRoot,
            '--assets-root', paths.assetsRoot,
            '--log-percent', String(options.logPercent == null ? 10 : options.logPercent),
            '--output-root', path.join(paths.beesRoot, 'Diagnostics'),
        ];
        if (targetRun) args.push('--run-id', String(targetRun));
        if (exists(statusJson)) args.push('--status-json', statusJson);
        if (exists(statusText)) args.push('--status-text', statusText);
        if (exists(snapshotJson)) args.push('--snapshot-json', snapshotJson);
        if (exists(benchmarkJson)) args.push('--benchmark-json', benchmarkJson);
        if (exists(runtimeStateJson)) args.push('--runtime-state-json', runtimeStateJson);

        runChecked(python, args, paths.assetsRoot);
    } finally {
        for (const temporary of [
            statusJson,
            statusText,
            snapshotJson,
            benchmarkJson,
            runtimeStateJson,
        ]) {
            removeIfExists(temporary);
        }
    }
}

module.exports = {
    diagnosticBenchmarkTimeoutMs,
    invokeBundle,
    invokeCentralDiagnosticBenchmark,
    requestCentralDiagnosticModelSnapshot,
};
