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
    archiveRunScript: path.join(assetsRoot, 'Training', 'bees_archive_training_run.py'),
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
    try {
        fs.rmSync(target, { force: true, recursive: Boolean(options.recursive) });
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

function loadConfig() {
    if (!exists(paths.configPath)) {
        throw new Error('Tracked training configuration is missing: ' + paths.configPath);
    }
    return readJson(paths.configPath);
}

function resolvePython(config) {
    return resolveCommand(config.python || 'python');
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
    throw new Error('Git was not found. Install Git for Windows or GitHub Desktop.');
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
    const result = runSync(executable, args, { cwd, env });
    const output = String(result.stdout || '');
    if (output) process.stdout.write(output);
    const errorOutput = String(result.stderr || '');
    if (errorOutput) process.stderr.write(errorOutput);
    return result;
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

function requestJson(baseUrl, token, method, requestPath, payload = null, timeoutMs = 5000) {
    const url = new URL(requestPath, baseUrl.endsWith('/') ? baseUrl : baseUrl + '/');
    const transport = url.protocol === 'https:' ? https : http;
    const body = payload === null ? null : Buffer.from(JSON.stringify(payload) + '\n', 'utf8');
    return new Promise((resolve, reject) => {
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
            response.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let value = {};
                try {
                    if (text) value = JSON.parse(text);
                } catch (_) {
                    reject(new Error('Control server returned invalid JSON: ' + text));
                    return;
                }
                if (response.statusCode < 200 || response.statusCode >= 300) {
                    reject(new Error('Control server HTTP ' + response.statusCode + ': ' + text));
                    return;
                }
                resolve(value);
            });
        });
        request.setTimeout(timeoutMs, () => request.destroy(new Error('training-control request timed out')));
        request.on('error', reject);
        if (body) request.write(body);
        request.end();
    });
}

async function testControl(baseUrl, token) {
    try {
        await requestJson(baseUrl, token, 'GET', '/v1/status', null, 1500);
        return true;
    } catch (_) {
        return false;
    }
}

function normalizeIso(value) {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) return '';
    return date.toISOString();
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
    if (process.platform !== 'win32') {
        try {
            process.kill(id, 0);
            return { pid: id, process_start_utc: '', executable_path: '' };
        } catch (_) {
            return null;
        }
    }
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
            process_start_utc: normalizeIso(value.process_start_utc),
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
    return process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
}

function testManagedProcessIdentity(state, expectedExecutable = '') {
    if (!state || !state.pid || !state.process_start_utc || !state.executable_path) return false;
    const current = getProcessIdentity(Number(state.pid));
    if (!current) return false;
    if (normalizeIso(current.process_start_utc) !== normalizeIso(state.process_start_utc)) return false;
    if (!samePath(current.executable_path, state.executable_path)) return false;
    if (expectedExecutable && !samePath(current.executable_path, expectedExecutable)) return false;
    return true;
}

function getStateReferencedLivePid(state) {
    const pid = Number(state && state.pid);
    return Number.isInteger(pid) && pid > 0 && isProcessAlive(pid) ? pid : 0;
}

function findManagedProcessByOwnerToken(expectedExecutable, ownerToken, label) {
    if (!ownerToken || process.platform !== 'win32') return null;
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
    atomicReplace,
    ensureDir,
    ensureTokenFile,
    exists,
    findManagedProcessByOwnerToken,
    getGitShortSha,
    getNamedFileSetSha256,
    getProcessIdentity,
    getStateReferencedLivePid,
    hmacSha256,
    invokePythonJson,
    isProcessAlive,
    loadConfig,
    normalizeIso,
    path,
    paths,
    powershellExecutable,
    readJson,
    readTail,
    removeUtf8BomIfPresent,
    readText,
    removeIfExists,
    requestJson,
    resolveCommand,
    resolveGit,
    resolvePython,
    resolveUnityEditor,
    runChecked,
    runSync,
    samePath,
    sha256File,
    sha256Text,
    sleep,
    sleepSync,
    spawn,
    stopManagedProcessTree,
    testControl,
    testManagedProcessIdentity,
    testPythonCode,
    writeJsonAtomic,
    writeTextAtomic,
};
