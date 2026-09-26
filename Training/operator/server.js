'use strict';

const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');

const {
    GAMEPLAY_SERVER_PORT,
    ensureDir,
    ensureTokenFile,
    exists,
    findManagedProcessByOwnerToken,
    getNamedFileSetSha256,
    getProcessIdentity,
    getStateReferencedLivePid,
    paths,
    readJson,
    removeIfExists,
    requestJson,
    resolveCommand,
    runSync,
    samePath,
    sha256Text,
    sleep,
    stopManagedProcessTree,
    testControl,
    testManagedProcessIdentity,
    writeJsonAtomic,
    writeTextAtomic,
} = require('./common');

const SERVER_RUNTIME_FILES = Object.freeze([
    'start-server.js',
    'server.js',
    'siServerDev.js',
    'serverContracts.js',
    'database.js',
    'gamePersistence.js',
    'outcomeReservations.js',
    'campaignCheckpoint.js',
    'security.js',
    'cachePersistence.js',
    'rlDemonstrationUploads.js',
    'rlTelemetryUploadSecurity.js',
    'rlTelemetryUploads.js',
    'rlModelDistributionSecurity.js',
    'rlModelDistribution.js',
    'trainingControl.js',
    'trainingEnvOptimizer.js',
    'package.json',
    'package-lock.json',
]);

function controlProbeHost(config) {
    const host = String(config.controlHost || '127.0.0.1').trim();
    return host === '0.0.0.0' || host === '::' ? '127.0.0.1' : host;
}

function testTcpPortOpen(host, port, timeoutMs = 500) {
    return new Promise(resolve => {
        const socket = net.createConnection({ host, port: Number(port) });
        let settled = false;
        const finish = open => {
            if (settled) return;
            settled = true;
            socket.destroy();
            resolve(open);
        };
        socket.setTimeout(timeoutMs);
        socket.once('connect', () => finish(true));
        socket.once('timeout', () => finish(false));
        socket.once('error', () => finish(false));
    });
}

async function waitForTcpPortClosed(host, port, timeoutMs = 15000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (!(await testTcpPortOpen(host, port))) return true;
        await sleep(250);
    }
    return !(await testTcpPortOpen(host, port));
}

function runtimeEntries(root) {
    return SERVER_RUNTIME_FILES.map(name => ({ name, filePath: path.join(root, name) }));
}

function getBeesServerRuntimeSourceHash() {
    return getNamedFileSetSha256(runtimeEntries(paths.serverRoot));
}

function getBeesServerDependencyHash(root = paths.serverRoot) {
    return getNamedFileSetSha256([
        { name: 'package.json', filePath: path.join(root, 'package.json') },
        { name: 'package-lock.json', filePath: path.join(root, 'package-lock.json') },
    ]);
}

function testBeesServerRuntimeLoad(node, runtimeRoot) {
    const result = runSync(node, [
        '-e',
        "const runtime=require('./server'); runtime.loadLegacyRuntime();",
    ], {
        cwd: runtimeRoot,
        check: false,
        stdio: 'ignore',
        timeout: 30000,
    });
    return result.status === 0;
}

function testBeesServerStagedRuntime(runtimeRoot, expectedSourceHash, node = process.execPath) {
    if (!exists(runtimeRoot)) return false;
    const readyPath = path.join(runtimeRoot, 'bees-server-runtime.json');
    const nodeModules = path.join(runtimeRoot, 'node_modules');
    if (!exists(readyPath) || !exists(nodeModules)) return false;
    try {
        const ready = readJson(readyPath);
        if (String(ready.source_hash || '').trim().toLowerCase() !== expectedSourceHash) return false;
        if (getNamedFileSetSha256(runtimeEntries(runtimeRoot)) !== expectedSourceHash) return false;
        return testBeesServerRuntimeLoad(node, runtimeRoot);
    } catch (_) {
        return false;
    }
}

function pruneBeesServerRuntimes(keepRoots = [], keepNewest = 3) {
    if (!exists(paths.serverReleaseRoot)) return;
    const keep = new Set(keepRoots.filter(Boolean).map(value => path.resolve(String(value)).toLowerCase()));
    const entries = fs.readdirSync(paths.serverReleaseRoot, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => {
            const full = path.join(paths.serverReleaseRoot, entry.name);
            return { name: entry.name, full, mtimeMs: fs.statSync(full).mtimeMs };
        });
    const staleCutoff = Date.now() - 60 * 60 * 1000;
    for (const entry of entries) {
        if (entry.name.includes('.candidate-') && entry.mtimeMs < staleCutoff) {
            removeIfExists(entry.full, { recursive: true });
        }
    }
    const normal = entries
        .filter(entry => !entry.name.includes('.candidate-'))
        .sort((a, b) => b.mtimeMs - a.mtimeMs);
    for (const entry of normal.slice(0, keepNewest)) keep.add(path.resolve(entry.full).toLowerCase());
    for (const entry of normal) {
        if (!keep.has(path.resolve(entry.full).toLowerCase())) {
            removeIfExists(entry.full, { recursive: true });
        }
    }
}

function prepareBeesServerRuntime(node = process.execPath) {
    const sourceHash = getBeesServerRuntimeSourceHash();
    ensureDir(paths.serverReleaseRoot);
    const runtimeRoot = path.join(paths.serverReleaseRoot, sourceHash);
    if (testBeesServerStagedRuntime(runtimeRoot, sourceHash, node)) {
        return {
            source_hash: sourceHash,
            dependency_hash: getBeesServerDependencyHash(runtimeRoot),
            runtime_root: path.resolve(runtimeRoot),
        };
    }

    const candidate = runtimeRoot + '.candidate-' + require('node:crypto').randomBytes(8).toString('hex');
    ensureDir(candidate);
    try {
        for (const name of SERVER_RUNTIME_FILES) {
            const source = path.join(paths.serverRoot, name);
            if (!exists(source)) throw new Error('BeesServer runtime source is missing: ' + source);
            fs.copyFileSync(source, path.join(candidate, name));
        }

        const dependencyHash = getBeesServerDependencyHash(candidate);
        const npm = resolveCommand(process.platform === 'win32' ? 'npm.cmd' : 'npm');
        console.log('Pre-staging BeesServer runtime ' + sourceHash.slice(0, 12) + ' while the current server remains online...');
        const install = runSync(npm, ['ci'], { cwd: candidate });
        if (install.stdout) process.stdout.write(String(install.stdout));
        if (install.stderr) process.stderr.write(String(install.stderr));

        for (const name of SERVER_RUNTIME_FILES.filter(name => name.endsWith('.js'))) {
            runSync(node, ['--check', path.join(candidate, name)], { cwd: candidate });
        }
        if (!testBeesServerRuntimeLoad(node, candidate)) {
            throw new Error('Prepared BeesServer runtime could not load its server module.');
        }

        const actualHash = getNamedFileSetSha256(runtimeEntries(candidate));
        if (actualHash !== sourceHash) {
            throw new Error(
                'BeesServer source changed while staging. expected=' + sourceHash + ' staged=' + actualHash
            );
        }

        writeJsonAtomic(path.join(candidate, 'bees-server-runtime.json'), {
            schema_version: 1,
            source_hash: sourceHash,
            dependency_hash: dependencyHash,
            prepared_utc: new Date().toISOString(),
        });

        if (exists(runtimeRoot)) {
            if (testBeesServerStagedRuntime(runtimeRoot, sourceHash, node)) {
                removeIfExists(candidate, { recursive: true });
            } else {
                let activeState = null;
                if (exists(paths.serverStatePath)) {
                    try { activeState = readJson(paths.serverStatePath); } catch (_) {}
                }
                const activeRuntime = activeState ? String(activeState.runtime_root || '').trim() : '';
                if (activeRuntime && samePath(activeRuntime, runtimeRoot) && testManagedProcessIdentity(activeState)) {
                    throw new Error(
                        'Active BeesServer runtime failed staged verification; refusing to mutate its live runtime directory: ' +
                        runtimeRoot
                    );
                }
                removeIfExists(runtimeRoot, { recursive: true });
                fs.renameSync(candidate, runtimeRoot);
            }
        } else {
            fs.renameSync(candidate, runtimeRoot);
        }
    } finally {
        if (exists(candidate)) removeIfExists(candidate, { recursive: true });
    }

    if (!testBeesServerStagedRuntime(runtimeRoot, sourceHash, node)) {
        throw new Error('BeesServer staged runtime failed post-install verification: ' + runtimeRoot);
    }

    return {
        source_hash: sourceHash,
        dependency_hash: getBeesServerDependencyHash(runtimeRoot),
        runtime_root: path.resolve(runtimeRoot),
    };
}

function launchEnvironment(config, workerToken, adminToken) {
    const environmentValidationSecret = ensureTokenFile(paths.environmentValidationTokenPath);
    return {
        ...process.env,
        BEES_TRAINING_CONTROL_ENABLED: '1',
        BEES_TRAINING_CONTROL_TOKEN: workerToken,
        BEES_TRAINING_CONTROL_ADMIN_TOKEN: adminToken,
        BEES_TRAINING_ENVIRONMENT_VALIDATION_SECRET: environmentValidationSecret,
        BEES_TRAINING_CONTROL_HOST: String(config.controlHost),
        BEES_TRAINING_CONTROL_PORT: String(config.controlPort),
        BEES_TRAINING_CONTROL_STATE: path.join(paths.trainingRoot, 'Control', 'state.json'),
        BEES_TRAINING_ARTIFACT_ROOT: path.join(paths.trainingRoot, 'Control', 'Artifacts'),
        BEES_TRAINING_LOG_ROOT: path.join(paths.trainingRoot, 'TrainerLogs'),
        BEES_TEST_TRAINING_CONTROL_ENABLED: '1',
    };
}

function getBeesServerLaunchConfigHash(config, workerToken, adminToken) {
    const environmentValidationSecret = ensureTokenFile(paths.environmentValidationTokenPath);
    const payload = {
        control_url: String(config.controlUrl),
        control_host: String(config.controlHost),
        control_port: Number(config.controlPort),
        gameplay_port: GAMEPLAY_SERVER_PORT,
        worker_token_sha256: sha256Text(workerToken),
        admin_token_sha256: sha256Text(adminToken),
        environment_validation_secret_sha256: sha256Text(environmentValidationSecret),
        control_state: path.join(paths.trainingRoot, 'Control', 'state.json'),
        artifact_root: path.join(paths.trainingRoot, 'Control', 'Artifacts'),
        log_root: path.join(paths.trainingRoot, 'TrainerLogs'),
        db_host: String(process.env.BEES_DB_HOST || ''),
        db_user: String(process.env.BEES_DB_USER || ''),
        db_password_sha256: process.env.BEES_DB_PASSWORD ? sha256Text(process.env.BEES_DB_PASSWORD) : '',
        db_name: String(process.env.BEES_DB_NAME || ''),
        require_test_db: String(process.env.BEES_REQUIRE_TEST_DB || ''),
        disable_background_jobs: String(process.env.BEES_DISABLE_BACKGROUND_JOBS || ''),
    };
    return sha256Text(JSON.stringify(payload));
}

function writeBeesServerManagedState(
    identity,
    sourceHash,
    dependencyHash,
    runtimeRoot,
    configHash,
    status,
    rollbackReason = '',
    ownerToken = '',
) {
    const state = {
        schema_version: 5,
        pid: Number(identity.pid),
        process_start_utc: String(identity.process_start_utc),
        executable_path: String(identity.executable_path),
        source_hash: sourceHash,
        dependency_hash: dependencyHash,
        runtime_root: path.resolve(runtimeRoot),
        config_hash: configHash,
        owner_token: ownerToken || String(identity.owner_token || ''),
        status,
        started_utc: new Date().toISOString(),
    };
    if (rollbackReason) state.rollback_reason = rollbackReason;
    writeJsonAtomic(paths.serverStatePath, state);
    writeTextAtomic(paths.serverPidPath, String(identity.pid), 'ascii');
}

async function startBeesServerRuntimeProcess(
    config,
    node,
    runtimeRoot,
    workerToken,
    adminToken,
    serverLog,
    runtimeIdentity,
    rollbackReason = '',
    timeoutSeconds = 30,
) {
    const launcher = path.join(runtimeRoot, 'start-server.js');
    if (!exists(launcher)) throw new Error('Prepared BeesServer runtime is missing its launcher: ' + launcher);

    const ownerToken = require('node:crypto').randomBytes(16).toString('hex');
    const result = runSync(node, [
        launcher,
        '--background',
        '--managed-owner-token', ownerToken,
        '--log=' + serverLog,
        'test',
        String(GAMEPLAY_SERVER_PORT),
    ], {
        cwd: runtimeRoot,
        env: launchEnvironment(config, workerToken, adminToken),
    });
    const output = (String(result.stdout || '') + String(result.stderr || '')).trim();
    if (output) console.log(output);
    const match = output.match(/PID\s+(\d+)/i);
    const launchedPid = match ? Number(match[1]) : 0;
    if (!launchedPid) throw new Error('BeesServer launcher succeeded without reporting the managed child PID.');

    const identity = getProcessIdentity(launchedPid);
    if (!identity || !samePath(identity.executable_path, node)) {
        if (identity) {
            try { stopManagedProcessTree(identity, node, 'failed BeesServer candidate'); } catch (_) {}
        }
        throw new Error('Could not establish the BeesServer candidate process identity after launch.');
    }

    writeBeesServerManagedState(
        identity,
        runtimeIdentity.source_hash,
        runtimeIdentity.dependency_hash,
        runtimeIdentity.runtime_root,
        runtimeIdentity.config_hash,
        'starting',
        rollbackReason,
        ownerToken,
    );

    const deadline = Date.now() + timeoutSeconds * 1000;
    while (Date.now() < deadline) {
        if (await testControl(config.controlUrl, adminToken)) {
            writeBeesServerManagedState(
                identity,
                runtimeIdentity.source_hash,
                runtimeIdentity.dependency_hash,
                runtimeIdentity.runtime_root,
                runtimeIdentity.config_hash,
                'active',
                rollbackReason,
                ownerToken,
            );
            return identity;
        }
        if (!testManagedProcessIdentity(identity, node)) {
            throw new Error(
                'BeesServer candidate PID ' + launchedPid +
                ' exited before the control endpoint became healthy. Check ' + serverLog + '.'
            );
        }
        await sleep(500);
    }

    if (testManagedProcessIdentity(identity, node)) {
        try { stopManagedProcessTree(identity, node, 'unhealthy BeesServer candidate'); } catch (_) {}
    }
    throw new Error(
        'BeesServer candidate did not become reachable at ' + config.controlUrl +
        ' within ' + timeoutSeconds + ' seconds. Check ' + serverLog + '.'
    );
}

async function startBeesServerIfNeeded(config, workerToken, adminToken) {
    const node = process.execPath;
    const configHash = getBeesServerLaunchConfigHash(config, workerToken, adminToken);
    const probeHost = controlProbeHost(config);

    // Prepare and validate replacement bytes before disturbing the current service.
    const prepared = prepareBeesServerRuntime(node);
    const sourceHash = prepared.source_hash;
    const dependencyHash = prepared.dependency_hash;
    const runtimeRoot = path.resolve(prepared.runtime_root);

    let state = null;
    let owned = false;
    if (exists(paths.serverStatePath)) {
        try { state = readJson(paths.serverStatePath); } catch (_) {}
    }

    if (state) {
        if (testManagedProcessIdentity(state)) {
            owned = true;
        } else {
            const ownerToken = String(state.owner_token || '').trim();
            if (ownerToken) {
                const recovered = findManagedProcessByOwnerToken(node, ownerToken, 'BeesServer supervisor');
                if (recovered) {
                    state = {
                        ...state,
                        ...recovered,
                        status: String(state.status || 'starting'),
                    };
                    writeJsonAtomic(paths.serverStatePath, state);
                    writeTextAtomic(paths.serverPidPath, String(recovered.pid), 'ascii');
                    owned = true;
                    console.log('Recovered BeesServer supervisor ownership after interrupted state reconciliation (PID ' + recovered.pid + ').');
                }
            }
            if (!owned) {
                const livePid = getStateReferencedLivePid(state);
                if (livePid > 0) {
                    throw new Error(
                        'BeesServer state references live PID ' + livePid +
                        ' but its PID/start-time/executable ownership does not match. Refusing to kill a possibly reused PID.'
                    );
                }
                if (ownerToken) {
                    const childToken = sha256Text('bees-managed-child:' + ownerToken);
                    const orphan = findManagedProcessByOwnerToken(node, childToken, 'orphaned BeesServer child');
                    if (orphan) {
                        console.log('Stopping orphaned BeesServer child PID ' + orphan.pid + ' left by a dead supervisor.');
                        stopManagedProcessTree(orphan, node, 'orphaned BeesServer child');
                    }
                }
                removeIfExists(paths.serverPidPath);
                removeIfExists(paths.serverStatePath);
                state = null;
            }
        }
    } else if (exists(paths.serverPidPath)) {
        const legacyPid = Number(fs.readFileSync(paths.serverPidPath, 'utf8').trim());
        if (legacyPid > 0 && getProcessIdentity(legacyPid)) {
            throw new Error(
                'BeesServer PID ' + legacyPid +
                ' is from legacy PID-only state and cannot be proven safe to kill automatically. Stop that legacy server once, then rerun the command.'
            );
        }
        removeIfExists(paths.serverPidPath);
    }

    const online = await testControl(String(config.controlUrl), adminToken);
    if (online) {
        if (!state || !owned) {
            throw new Error(
                'BeesServer is online but has no matching managed process identity. Refusing an automatic restart because an unrelated process could own the live endpoint.'
            );
        }
        const runtimeMatches = state.runtime_root && samePath(state.runtime_root, runtimeRoot);
        if (testManagedProcessIdentity(state, node) &&
            String(state.source_hash || '') === sourceHash &&
            String(state.config_hash || '') === configHash &&
            runtimeMatches) {
            if (String(state.status || '') !== 'active') {
                writeBeesServerManagedState(
                    state, sourceHash, dependencyHash, runtimeRoot, configHash, 'active',
                    String(state.rollback_reason || ''), String(state.owner_token || '')
                );
            }
            pruneBeesServerRuntimes([runtimeRoot]);
            return;
        }
        console.log('BeesServer executable/runtime/launch configuration changed; restarting the verified managed server without changing desired training state.');
    } else if (owned) {
        console.log('Managed BeesServer is not accepting the desired control endpoint/token; restarting the verified owned process to converge launch configuration.');
    }

    if (owned) {
        // Import lazily to avoid module initialization cycles.
        const { assertCentralAgentCheckpointSafe } = require('./central');
        assertCentralAgentCheckpointSafe();
        stopManagedProcessTree(state, node, 'BeesServer');
        await waitForTcpPortClosed(probeHost, Number(config.controlPort), 15000);
    }

    if (await testTcpPortOpen(probeHost, Number(config.controlPort))) {
        throw new Error(
            'Training-control port ' + config.controlPort +
            ' is already in use but did not accept this admin token. The process is not the verified managed BeesServer, so it will not be killed automatically.'
        );
    }

    ensureDir(path.join(paths.logsRoot, 'Server'));
    ensureDir(path.join(paths.trainingRoot, 'Control'));
    const serverLog = path.join(paths.logsRoot, 'Server', 'bees-server.log');
    const previous = state;
    const previousConfigHash = previous ? String(previous.config_hash || '') : '';
    const previousSourceHash = previous ? String(previous.source_hash || '') : '';
    const previousRuntimeRoot = previous ? String(previous.runtime_root || '').trim() : '';
    const previousDependencyHash = previous ? String(previous.dependency_hash || '') : '';

    const desiredIdentity = {
        source_hash: sourceHash,
        dependency_hash: dependencyHash,
        runtime_root: runtimeRoot,
        config_hash: configHash,
    };

    try {
        await startBeesServerRuntimeProcess(
            config, node, runtimeRoot, workerToken, adminToken, serverLog, desiredIdentity
        );
    } catch (error) {
        const replacementError = error.message;
        if (previousRuntimeRoot &&
            previousConfigHash === configHash &&
            previousSourceHash &&
            testBeesServerStagedRuntime(previousRuntimeRoot, previousSourceHash, node)) {
            console.warn(
                'Replacement BeesServer failed after cutover; restoring previously verified runtime ' +
                previousSourceHash + '.'
            );
            try {
                await startBeesServerRuntimeProcess(
                    config,
                    node,
                    previousRuntimeRoot,
                    workerToken,
                    adminToken,
                    serverLog,
                    {
                        source_hash: previousSourceHash,
                        dependency_hash: previousDependencyHash,
                        runtime_root: path.resolve(previousRuntimeRoot),
                        config_hash: previousConfigHash,
                    },
                    replacementError,
                );
                pruneBeesServerRuntimes([previousRuntimeRoot]);
                throw new Error(
                    'Replacement BeesServer failed, but the previous verified runtime was restored successfully. replacement_error=' +
                    replacementError
                );
            } catch (rollbackError) {
                if (rollbackError.message.startsWith('Replacement BeesServer failed, but')) throw rollbackError;
                throw new Error(
                    'Replacement BeesServer failed and rollback also failed. replacement_error=' +
                    replacementError + ' rollback_error=' + rollbackError.message
                );
            }
        }
        throw error;
    }

    pruneBeesServerRuntimes([runtimeRoot]);
}

module.exports = {
    SERVER_RUNTIME_FILES,
    controlProbeHost,
    getBeesServerDependencyHash,
    getBeesServerLaunchConfigHash,
    getBeesServerRuntimeSourceHash,
    launchEnvironment,
    prepareBeesServerRuntime,
    pruneBeesServerRuntimes,
    startBeesServerIfNeeded,
    startBeesServerRuntimeProcess,
    testBeesServerStagedRuntime,
    testTcpPortOpen,
    waitForTcpPortClosed,
    writeBeesServerManagedState,
};
