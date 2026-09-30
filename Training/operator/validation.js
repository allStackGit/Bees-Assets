'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const {
    ensureDir,
    ensureTokenFile,
    exists,
    hmacSha256,
    paths,
    readText,
    removeIfExists,
    resolvePython,
    runChecked,
    sha256File,
    sha256Text,
    sleep,
    writeTextAtomic,
} = require('./common');

function killProcessTree(child) {
    if (!child || !child.pid) return;
    if (process.platform === 'win32') {
        spawnSync(
            'taskkill.exe',
            ['/PID', String(child.pid), '/T', '/F'],
            { windowsHide: true, stdio: 'ignore' },
        );
        return;
    }
    try {
        process.kill(-child.pid, 'SIGKILL');
    } catch (_) {
        try { process.kill(child.pid, 'SIGKILL'); } catch (_) {}
    }
}

function extractZip(python, archive, destination) {
    ensureDir(destination);
    const code = [
        'import pathlib,sys,zipfile',
        'archive=pathlib.Path(sys.argv[1])',
        'dest=pathlib.Path(sys.argv[2])',
        'with zipfile.ZipFile(archive,"r") as z:',
        '    z.extractall(dest)',
    ].join('\n');
    runChecked(python, ['-c', code, archive, destination], paths.assetsRoot);
}

function spawnValidationProcess(executable, args, cwd) {
    const child = spawn(executable, args.map(String), {
        cwd,
        windowsHide: true,
        detached: process.platform !== 'win32',
        stdio: 'ignore',
    });
    child.beesSpawnError = null;
    child.beesClosed = false;
    child.on('error', error => {
        child.beesSpawnError = error;
    });
    child.once('close', () => {
        child.beesClosed = true;
    });
    return child;
}

async function stopValidationProcess(child, timeoutMs = 5000) {
    if (!child) return;

    if (child.exitCode === null && child.signalCode === null) {
        killProcessTree(child);
    }

    const deadline = Date.now() + timeoutMs;
    while (!child.beesClosed && Date.now() < deadline) {
        await sleep(50);
    }
}

const STALE_VALIDATION_CANDIDATE_MS = 10 * 60 * 1000;

function cleanupStaleValidationCandidates(validationRoot, nowMs = Date.now()) {
    if (!exists(validationRoot)) return 0;
    let removed = 0;
    for (const entry of fs.readdirSync(validationRoot, { withFileTypes: true })) {
        if (!entry.isDirectory() || !entry.name.startsWith('candidate-')) continue;
        const candidate = path.join(validationRoot, entry.name);
        let stat;
        try {
            stat = fs.statSync(candidate);
        } catch (_) {
            continue;
        }
        if (nowMs - stat.mtimeMs < STALE_VALIDATION_CANDIDATE_MS) continue;
        try {
            removeIfExists(candidate, {
                recursive: true,
                maxRetries: 20,
                retryDelay: 100,
            });
            removed++;
        } catch (error) {
            if (!error || !['EPERM', 'EBUSY', 'ENOTEMPTY'].includes(error.code)) {
                throw error;
            }
        }
    }
    return removed;
}

function cleanupValidationCandidate(candidate) {
    try {
        removeIfExists(candidate, {
            recursive: true,
            maxRetries: 300,
            retryDelay: 100,
        });
    } catch (error) {
        if (error && ['EPERM', 'EBUSY', 'ENOTEMPTY'].includes(error.code)) {
            console.warn(
                'RL environment validation completed, but Windows still holds the temporary validation directory; ' +
                'leaving it for later cleanup: ' + candidate
            );
            return;
        }
        throw error;
    }
}

async function waitForExitOrMarker(child, logPath, timeoutMs, marker) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (child.beesSpawnError) throw child.beesSpawnError;
        if (child.exitCode !== null || child.signalCode !== null) {
            return { exited: true, code: child.exitCode, logText: exists(logPath) ? readText(logPath) : '' };
        }
        const logText = exists(logPath) ? readText(logPath) : '';
        if (marker && marker.test(logText)) {
            return { exited: false, code: null, logText, marker: true };
        }
        await sleep(200);
    }
    if (child.beesSpawnError) throw child.beesSpawnError;
    return {
        exited: child.exitCode !== null || child.signalCode !== null,
        code: child.exitCode,
        logText: exists(logPath) ? readText(logPath) : '',
        timeout: true,
    };
}

function validationProof(release, archiveSha, environmentArgs) {
    const encoded = environmentArgs.map(value =>
        Buffer.from(String(value), 'utf8').toString('base64')
    );
    const secret = ensureTokenFile(paths.environmentValidationTokenPath);
    return hmacSha256(
        secret,
        'bees-environment-validation-v2\n' +
        String(release.build_id) + '\n' +
        archiveSha + '\n' +
        encoded.join('\n'),
    );
}

function validationIdentity(release, archiveSha, environmentArgs) {
    return sha256Text(
        String(release.build_id) + os.EOL +
        archiveSha + os.EOL +
        JSON.stringify(environmentArgs.map(String))
    );
}

async function assertRlEnvironmentArgsValid(config, release, environmentArgs, python = null) {
    const args = [...(environmentArgs || [])].map(String);
    const artifact = (release.artifacts || []).find(item =>
        String(item.role) === 'dedicated' &&
        String(item.platform) === 'WindowsPlayer'
    );
    if (!artifact) {
        throw new Error('Latest release has no local dedicated Windows artifact for RL environment validation.');
    }

    const entrypoint = String(artifact.entrypoint || '');
    const archivePath = path.resolve(String(artifact.archive || ''));
    if (!exists(archivePath)) {
        throw new Error('RL environment validation archive is missing: ' + archivePath);
    }
    const archiveSha = sha256File(archivePath);
    const proof = validationProof(release, archiveSha, args);
    const validationKey = validationIdentity(release, archiveSha, args);
    const validationRoot = path.join(paths.runtimeRoot, 'RlEnvironmentValidation');
    const stamp = path.join(validationRoot, validationKey + '.ok');
    if (exists(stamp)) return proof;

    ensureDir(validationRoot);
    cleanupStaleValidationCandidates(validationRoot);
    ensureDir(path.join(paths.logsRoot, 'Training'));
    const candidate = path.join(
        validationRoot,
        'candidate-' + archiveSha.slice(0, 12) + '-' + process.pid + '-' +
        require('node:crypto').randomBytes(8).toString('hex'),
    );
    removeIfExists(candidate, { recursive: true });

    const activePython = python || resolvePython(config);
    let child = null;
    try {
        extractZip(activePython, archivePath, candidate);
        const executable = path.join(candidate, entrypoint);
        if (!exists(executable)) {
            throw new Error(
                'RL environment validation archive does not contain its declared entrypoint: ' +
                entrypoint
            );
        }

        const validatorSha = sha256File(executable);
        const log = path.join(
            paths.logsRoot,
            'Training',
            'rl-environment-validation-' + validationKey.slice(0, 12) + '.log',
        );
        removeIfExists(log);

        // argv remains structured all the way into CreateProcess. This deliberately avoids
        // shell command-line reparsing and its path-with-spaces failure mode.
        const validationArgs = [
            '-batchmode',
            '-nographics',
            '-logFile', log,
            '--rl-validate-options-only',
            ...args,
        ];
        child = spawnValidationProcess(executable, validationArgs, candidate);
        let outcome = await waitForExitOrMarker(
            child,
            log,
            60000,
            null,
        );
        let validationSucceeded = false;

        if (!outcome.exited) {
            if (/RL training configuration /.test(outcome.logText)) {
                killProcessTree(child);
                console.warn(
                    'Release ' + release.build_id +
                    ' predates validation-only exit support; accepted environment arguments from its compiled startup validation marker.'
                );
                validationSucceeded = true;
            } else {
                killProcessTree(child);
                throw new Error('RL environment validation timed out after 60 seconds. See ' + log);
            }
        } else if (outcome.code === 0) {
            if (!/RL training command-line validation succeeded:/.test(outcome.logText)) {
                throw new Error(
                    'RL environment validator exited successfully without its authoritative success marker. See ' +
                    log
                );
            }
            validationSucceeded = true;
        } else {
            const legacyFlagRejected =
                /Invalid RL training command-line configuration: Unknown RL training option '--rl-validate-options-only'\./
                    .test(outcome.logText);
            if (!legacyFlagRejected) {
                throw new Error(
                    'Invalid RL environment arguments; validator exited with code ' +
                    outcome.code + '. See ' + log
                );
            }
        }

        if (!validationSucceeded) {
            const legacyLog = path.join(
                paths.logsRoot,
                'Training',
                'rl-environment-validation-' + validationKey.slice(0, 12) + '-legacy.log',
            );
            removeIfExists(legacyLog);
            console.warn(
                'Release ' + release.build_id +
                ' predates --rl-validate-options-only; validating with its compiled startup parser instead.'
            );
            child = spawnValidationProcess(
                executable,
                ['-batchmode', '-nographics', '-logFile', legacyLog, ...args],
                candidate,
            );
            const deadline = Date.now() + 60000;
            let legacySucceeded = false;
            while (Date.now() < deadline) {
                if (child.beesSpawnError) {
                    throw new Error('Legacy RL environment validator failed to spawn: ' + child.beesSpawnError.message);
                }
                const text = exists(legacyLog) ? readText(legacyLog) : '';
                if (/Invalid RL training command-line configuration:/.test(text)) {
                    killProcessTree(child);
                    throw new Error(
                        'Invalid RL environment arguments; legacy compiled-build validator rejected the requested environment. See ' +
                        legacyLog
                    );
                }
                if (/RL training configuration /.test(text)) {
                    legacySucceeded = true;
                    break;
                }
                if (child.exitCode !== null || child.signalCode !== null) break;
                await sleep(200);
            }

            if (!legacySucceeded) {
                const code = child.exitCode;
                killProcessTree(child);
                if (code !== null) {
                    throw new Error(
                        'Legacy RL environment validator exited with code ' + code +
                        ' before reporting a validation result. See ' + legacyLog
                    );
                }
                throw new Error(
                    'Legacy RL environment validation timed out after 60 seconds. See ' + legacyLog
                );
            }
            killProcessTree(child);
        }

        writeTextAtomic(
            stamp,
            'build=' + String(release.build_id) + os.EOL +
            'validator_sha256=' + validatorSha + os.EOL +
            'archive_sha256=' + archiveSha + os.EOL +
            'args_sha256=' + validationKey + os.EOL +
            'release_validation_key=' + proof + os.EOL,
        );
    } finally {
        await stopValidationProcess(child);
        cleanupValidationCandidate(candidate);
    }

    return proof;
}

module.exports = {
    assertRlEnvironmentArgsValid,
    cleanupStaleValidationCandidates,
    extractZip,
    killProcessTree,
    validationIdentity,
    validationProof,
};
