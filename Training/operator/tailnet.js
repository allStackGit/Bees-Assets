'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const {
    atomicReplace,
    ensureDir,
    ensureTokenFile,
    exists,
    findManagedProcessByOwnerToken,
    getProcessIdentity,
    getStateReferencedLivePid,
    paths,
    powershellExecutable,
    readJson,
    removeUtf8BomIfPresent,
    readText,
    removeIfExists,
    resolveCommand,
    runChecked,
    runSync,
    samePath,
    sha256File,
    sha256Text,
    sleep,
    stopManagedProcessTree,
    testManagedProcessIdentity,
    writeJsonAtomic,
    writeTextAtomic,
} = require('./common');
const { invokePythonJson } = require('./common');
const { resolveReleaseTrainingRuntime } = require('./runtime');

const GO_VERSION = '1.27.1';
const GO_WINDOWS_ZIP_SHA256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d';

function resolvePortableGo() {
    try {
        return resolveCommand('go');
    } catch (_) {}

    if (process.platform !== 'win32') {
        throw new Error('Go was not found on PATH.');
    }

    const toolchains = path.join(paths.runtimeRoot, 'Toolchains');
    ensureDir(toolchains);
    const root = path.join(toolchains, 'go' + GO_VERSION);
    const executable = path.join(root, 'go', 'bin', 'go.exe');
    if (exists(executable)) return executable;

    const archive = path.join(toolchains, 'go' + GO_VERSION + '.windows-amd64.zip');
    if (!exists(archive)) {
        const temporary = archive + '.download';
        const url = 'https://go.dev/dl/go' + GO_VERSION + '.windows-amd64.zip';
        console.log('Downloading portable Go ' + GO_VERSION + ' for the embedded Bees tailnet bridge...');
        let curl = '';
        try { curl = resolveCommand('curl.exe'); } catch (_) {}
        if (curl) {
            // Keep a partial download so an interrupted bootstrap can resume.
            runChecked(curl, [
                '--fail', '--location', '--retry', '3', '--retry-delay', '2',
                '--continue-at', '-', '--output', temporary, url,
            ], toolchains);
        } else {
            // Preserve the pre-refactor fallback for Windows installations without curl.exe.
            removeIfExists(temporary);
            const ps = powershellExecutable();
            runChecked(
                ps,
                [
                    '-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
                    'Invoke-WebRequest -UseBasicParsing -Uri $env:BEES_DOWNLOAD_URL -OutFile $env:BEES_DOWNLOAD_OUTPUT',
                ],
                toolchains,
                {
                    ...process.env,
                    BEES_DOWNLOAD_URL: url,
                    BEES_DOWNLOAD_OUTPUT: temporary,
                },
            );
        }
        const actual = sha256File(temporary);
        if (actual !== GO_WINDOWS_ZIP_SHA256) {
            removeIfExists(temporary);
            throw new Error(
                'Portable Go download failed SHA-256 verification. expected=' +
                GO_WINDOWS_ZIP_SHA256 + ' actual=' + actual
            );
        }
        fs.renameSync(temporary, archive);
    } else {
        const actual = sha256File(archive);
        if (actual !== GO_WINDOWS_ZIP_SHA256) {
            throw new Error('Cached portable Go archive failed SHA-256 verification: ' + archive);
        }
    }

    removeIfExists(root, { recursive: true });
    ensureDir(root);
    const ps = powershellExecutable();
    runChecked(ps, [
        '-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
        'Expand-Archive -LiteralPath $env:BEES_ARCHIVE -DestinationPath $env:BEES_DEST -Force',
    ], toolchains, { ...process.env, BEES_ARCHIVE: archive, BEES_DEST: root });
    if (!exists(executable)) {
        throw new Error('Portable Go extraction did not produce ' + executable);
    }
    return executable;
}

function getTailnetBridgeSourceHash() {
    const main = path.join(paths.tailnetToolRoot, 'main.go');
    const moduleFile = path.join(paths.tailnetToolRoot, 'go.mod');
    if (!exists(main) || !exists(moduleFile)) {
        throw new Error('Embedded tailnet bridge source is missing: ' + paths.tailnetToolRoot);
    }
    return sha256Text(readText(moduleFile) + require('node:os').EOL + readText(main));
}

function buildTailnetBridge() {
    const main = path.join(paths.tailnetToolRoot, 'main.go');
    const moduleFile = path.join(paths.tailnetToolRoot, 'go.mod');
    if (!exists(main) || !exists(moduleFile)) {
        throw new Error('Embedded tailnet bridge source is missing: ' + paths.tailnetToolRoot);
    }

    const sourceHash = getTailnetBridgeSourceHash();
    const versionRoot = path.join(paths.tailnetBinRoot, 'Versions', sourceHash);
    const versionWindows = path.join(versionRoot, 'bees-tailnet-bridge.exe');
    const versionLinux = path.join(versionRoot, 'bees-tailnet-bridge');
    ensureDir(versionRoot);

    if (!exists(versionWindows) || !exists(versionLinux)) {
        const go = resolvePortableGo();
        const source = path.join(paths.tailnetRoot, 'BuildSource');
        removeIfExists(source, { recursive: true });
        fs.cpSync(paths.tailnetToolRoot, source, { recursive: true, force: true });
        try {
            console.log('Building embedded Bees tailnet bridge ' + sourceHash.slice(0, 12) + ' for Windows...');
            runChecked(go, [
                'build', '-mod=mod', '-trimpath', '-ldflags=-s -w',
                '-o', versionWindows, '.',
            ], source, {
                ...process.env,
                GOOS: 'windows',
                GOARCH: 'amd64',
                CGO_ENABLED: '0',
            });

            console.log('Building embedded Bees tailnet bridge ' + sourceHash.slice(0, 12) + ' for Linux...');
            runChecked(go, [
                'build', '-mod=mod', '-trimpath', '-ldflags=-s -w',
                '-o', versionLinux, '.',
            ], source, {
                ...process.env,
                GOOS: 'linux',
                GOARCH: 'amd64',
                CGO_ENABLED: '0',
            });
        } finally {
            removeIfExists(source, { recursive: true });
        }
    }

    ensureDir(path.dirname(paths.tailnetBridgeManifestPath));
    writeJsonAtomic(paths.tailnetBridgeManifestPath, {
        schema_version: 2,
        source_hash: sourceHash,
        gateway_windows: versionWindows,
        distribution_windows: versionWindows,
        distribution_linux: versionLinux,
    });
}

function getTailnetBridgePaths() {
    const sourceHash = getTailnetBridgeSourceHash();
    let needsBuild = !exists(paths.tailnetBridgeManifestPath);
    if (!needsBuild) {
        try {
            needsBuild = String(readJson(paths.tailnetBridgeManifestPath).source_hash || '') !== sourceHash;
        } catch (_) {
            needsBuild = true;
        }
    }
    if (needsBuild) {
        console.log('Embedded Bees tailnet helper source changed; rebuilding helper only...');
        buildTailnetBridge();
    }
    const value = readJson(paths.tailnetBridgeManifestPath);
    for (const item of [
        value.gateway_windows,
        value.distribution_windows,
        value.distribution_linux,
    ]) {
        if (!item || !exists(String(item))) {
            throw new Error('Tailnet bridge manifest references a missing binary: ' + String(item || ''));
        }
    }
    return value;
}

function validateTailnetIp(value) {
    return /^100\.(?:\d{1,3}\.){2}\d{1,3}$/.test(String(value || '').trim());
}

function recoverGatewayState(bridge, state, label) {
    if (!state || testManagedProcessIdentity(state)) return state;
    const ownerToken = String(state.owner_token || '');
    if (String(state.status || '') === 'launching' && ownerToken) {
        const recovered = findManagedProcessByOwnerToken(bridge, ownerToken, label);
        if (recovered) {
            const value = { ...state, ...recovered, status: 'active' };
            writeJsonAtomic(paths.tailnetGatewayStatePath, value);
            writeTextAtomic(paths.tailnetGatewayPidPath, String(recovered.pid), 'ascii');
            return value;
        }
    }
    return state;
}

function ensureTailnetIdentity(config) {
    const transport = String(config.remoteTransport || 'tailnet').trim().toLowerCase();
    if (transport !== 'tailnet') return;

    const bridges = getTailnetBridgePaths();
    const bridge = String(bridges.gateway_windows);
    const hostname = String(config.tailnetLearnerName || 'bees-learner').trim();
    if (!/^[A-Za-z0-9-]{1,63}$/.test(hostname)) {
        throw new Error('tailnetLearnerName must contain only letters, digits, and dashes.');
    }

    const stateDir = path.join(paths.tailnetRoot, 'LearnerState');
    ensureDir(stateDir);
    ensureDir(path.dirname(paths.tailnetAddressPath));

    let gatewayState = null;
    if (exists(paths.tailnetGatewayStatePath)) {
        try { gatewayState = readJson(paths.tailnetGatewayStatePath); } catch (_) {}
    }
    gatewayState = recoverGatewayState(bridge, gatewayState, 'embedded tailnet gateway');
    if (gatewayState) {
        if (testManagedProcessIdentity(gatewayState)) {
            if (!exists(paths.tailnetAddressPath)) {
                throw new Error(
                    'Embedded tailnet gateway is running but its persisted learner IPv4 address is missing. Refusing to start a second tsnet server against the same state directory.'
                );
            }
            const ip = readText(paths.tailnetAddressPath).trim();
            if (!validateTailnetIp(ip)) {
                throw new Error('Embedded tailnet gateway is running but its persisted learner IPv4 address is invalid: ' + ip);
            }
            console.log('Embedded Bees tailnet identity already active at ' + ip + '; reusing the live gateway state.');
            return;
        }
        const livePid = getStateReferencedLivePid(gatewayState);
        if (livePid > 0) {
            throw new Error(
                'Tailnet gateway state references live PID ' + livePid +
                ' but its persisted PID/start-time/executable identity does not match. Refusing concurrent authentication against a possibly unrelated process/state owner.'
            );
        }
    }

    console.log('Checking embedded Bees tailnet identity. On first use, open the Tailscale login URL shown below.');
    runChecked(bridge, [
        'auth',
        '--state', stateDir,
        '--hostname', hostname,
        '--ip-file', paths.tailnetAddressPath,
    ], paths.assetsRoot);

    if (!exists(paths.tailnetAddressPath)) {
        throw new Error('Embedded tailnet authentication did not produce a learner IPv4 address.');
    }
    const ip = readText(paths.tailnetAddressPath).trim();
    if (!validateTailnetIp(ip)) {
        throw new Error('Unexpected learner tailnet IPv4 address: ' + ip);
    }
}

function testTailnetGatewayHealth(maxAgeSeconds = 15) {
    if (!exists(paths.tailnetGatewayHealthPath)) return false;
    try {
        const age = (Date.now() - fs.statSync(paths.tailnetGatewayHealthPath).mtimeMs) / 1000;
        return age >= 0 && age <= maxAgeSeconds;
    } catch (_) {
        return false;
    }
}

async function startTailnetGatewayIfNeeded(config) {
    const transport = String(config.remoteTransport || 'tailnet').trim().toLowerCase();
    if (transport !== 'tailnet') return;

    const bridges = getTailnetBridgePaths();
    const bridge = String(bridges.gateway_windows);
    const stateDir = path.join(paths.tailnetRoot, 'LearnerState');
    const hostname = String(config.tailnetLearnerName || 'bees-learner').trim();
    const controlPort = Number(config.controlPort);
    const brokerPort = Number(config.brokerPort);
    const bootstrapPort = Number(config.tailnetBootstrapPort || 7151);
    for (const port of [controlPort, brokerPort, bootstrapPort]) {
        if (!Number.isInteger(port) || port < 1 || port > 65535) {
            throw new Error('Tailnet gateway ports must be in 1-65535.');
        }
    }
    if (new Set([controlPort, brokerPort, bootstrapPort]).size !== 3) {
        throw new Error('controlPort, brokerPort, and tailnetBootstrapPort must be distinct.');
    }
    for (const required of [paths.bootstrapBundlePath, paths.bootstrapTokenPath]) {
        if (!exists(required)) throw new Error('Tailnet gateway input is missing: ' + required);
    }

    const args = [
        'gateway-supervisor',
        '--state', stateDir,
        '--hostname', hostname,
        '--control-port', String(controlPort),
        '--broker-port', String(brokerPort),
        '--bootstrap-port', String(bootstrapPort),
        '--bootstrap-bundle', paths.bootstrapBundlePath,
        '--bootstrap-token', paths.bootstrapTokenPath,
        '--health-file', paths.tailnetGatewayHealthPath,
    ];
    const configHash = sha256Text(
        path.resolve(bridge) + require('node:os').EOL +
        args.join(require('node:os').EOL) + require('node:os').EOL +
        'bootstrap-token-sha256=' + sha256File(paths.bootstrapTokenPath)
    );

    let state = null;
    if (exists(paths.tailnetGatewayStatePath)) {
        try { state = readJson(paths.tailnetGatewayStatePath); } catch (_) {}
    }
    state = recoverGatewayState(bridge, state, 'embedded tailnet gateway');

    if (state && !testManagedProcessIdentity(state) && state.owner_token) {
        const childToken = sha256Text('bees-managed-child:' + state.owner_token);
        const orphan = findManagedProcessByOwnerToken(
            bridge, childToken, 'orphaned embedded tailnet gateway child'
        );
        if (orphan) {
            console.log('Stopping orphaned embedded tailnet gateway child PID ' + orphan.pid + ' left by a dead supervisor.');
            stopManagedProcessTree(orphan, bridge, 'orphaned embedded tailnet gateway child');
        }
    }

    if (state) {
        if (testManagedProcessIdentity(state)) {
            if (testManagedProcessIdentity(state, bridge) &&
                String(state.config_hash || '') === configHash &&
                testTailnetGatewayHealth()) {
                const ip = readText(paths.tailnetAddressPath).trim();
                console.log(
                    'Embedded tailnet gateway already healthy at ' + ip +
                    ': control=' + controlPort + ' broker=' + brokerPort +
                    ' bootstrap=' + bootstrapPort + ' (PID ' + state.pid + ').'
                );
                return;
            }
            stopManagedProcessTree(state, bridge, 'embedded tailnet gateway');
        } else {
            const livePid = getStateReferencedLivePid(state);
            if (livePid > 0) {
                throw new Error(
                    'Embedded tailnet gateway state references live PID ' + livePid +
                    ' but the persisted process identity does not match. Refusing to kill a possibly reused PID.'
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
                'Embedded tailnet gateway PID ' + legacyPid +
                ' is from legacy PID-only state and cannot be proven safe to kill automatically. Stop that legacy gateway once, then rerun the command.'
            );
        }
        removeIfExists(paths.tailnetGatewayPidPath);
    }

    ensureDir(path.dirname(paths.tailnetGatewayLogPath));
    const ownerToken = crypto.randomBytes(16).toString('hex');
    const launchArgs = [...args, '--owner-token', ownerToken];
    const launchIntent = {
        schema_version: 3,
        status: 'launching',
        owner_token: ownerToken,
        executable_path: path.resolve(bridge),
        config_hash: configHash,
        argv_transport: 'node-spawn-array-v1',
        started_utc: new Date().toISOString(),
    };
    writeJsonAtomic(paths.tailnetGatewayStatePath, launchIntent);
    removeIfExists(paths.tailnetGatewayPidPath);
    removeIfExists(paths.tailnetGatewayHealthPath);

    const outFd = fs.openSync(paths.tailnetGatewayLogPath, 'a');
    const errFd = fs.openSync(paths.tailnetGatewayErrPath, 'a');
    let child;
    try {
        child = spawn(bridge, launchArgs, {
            cwd: paths.assetsRoot,
            detached: true,
            windowsHide: true,
            stdio: ['ignore', outFd, errFd],
        });
    } finally {
        fs.closeSync(outFd);
        fs.closeSync(errFd);
    }
    child.unref();
    await sleep(250);
    if (!child.pid) {
        throw new Error('Embedded tailnet gateway did not return a process id.');
    }
    const identity = getProcessIdentity(child.pid);
    if (!identity || !samePath(identity.executable_path, bridge)) {
        throw new Error('Could not establish the embedded tailnet gateway process identity after launch.');
    }

    const deadline = Date.now() + 30000;
    while (Date.now() < deadline && !testTailnetGatewayHealth()) {
        if (!testManagedProcessIdentity(identity, bridge)) {
            throw new Error('Embedded tailnet gateway exited before its service heartbeat became healthy. Check ' + paths.tailnetGatewayErrPath);
        }
        await sleep(250);
    }
    if (!testTailnetGatewayHealth()) {
        try { stopManagedProcessTree(identity, bridge, 'unhealthy embedded tailnet gateway'); } catch (_) {}
        throw new Error('Embedded tailnet gateway did not become service-ready within 30 seconds. Check ' + paths.tailnetGatewayErrPath);
    }

    writeJsonAtomic(paths.tailnetGatewayStatePath, { ...launchIntent, ...identity, status: 'active' });
    writeTextAtomic(paths.tailnetGatewayPidPath, String(identity.pid), 'ascii');
    const ip = readText(paths.tailnetAddressPath).trim();
    console.log(
        'Embedded tailnet gateway online at ' + ip +
        ': control=' + controlPort + ' broker=' + brokerPort +
        ' bootstrap=' + bootstrapPort + ' (PID ' + identity.pid + ').'
    );
}

function escapePowerShellSingleQuoted(value) {
    return String(value).replaceAll("'", "''");
}

function escapeBashDoubleQuoted(value) {
    return String(value)
        .replaceAll('\\', '\\\\')
        .replaceAll('"', '\\"')
        .replaceAll('$', '\\$')
        .replaceAll('`', '\\`');
}

function formatBase64(buffer, width = 120) {
    const value = buffer.toString('base64');
    const lines = [];
    for (let offset = 0; offset < value.length; offset += width) {
        lines.push(value.slice(offset, offset + width));
    }
    return lines.join('\n');
}

function createWindowsPayloadZip(python, payloadRoot, outputZip) {
    const code = [
        'import pathlib,sys,zipfile',
        'root=pathlib.Path(sys.argv[1])',
        'out=pathlib.Path(sys.argv[2])',
        'with zipfile.ZipFile(out,"w",zipfile.ZIP_DEFLATED) as z:',
        '    [z.write(p,p.relative_to(root).as_posix()) for p in root.rglob("*") if p.is_file()]',
    ].join('\n');
    runChecked(python, ['-c', code, payloadRoot, outputZip], paths.assetsRoot);
}

function validatePowerShellFile(filePath) {
    const ps = powershellExecutable();
    const env = { ...process.env, BEES_PS_PARSE_FILE: filePath };
    const script =
        '$e=$null;$null=[System.Management.Automation.Language.Parser]::ParseFile($env:BEES_PS_PARSE_FILE,[ref]$null,[ref]$e);' +
        'if($e.Count){$e|ForEach-Object{Write-Error $_.Message};exit 2}';
    runSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
        env,
        cwd: paths.assetsRoot,
    });
}

function prepareRemoteBootstrap(config, python, release) {
    for (const required of [
        paths.remoteBootstrapTemplate,
        paths.remoteLinuxBootstrapTemplate,
        paths.remoteRequirementsPath,
    ]) {
        if (!exists(required)) throw new Error('Remote bootstrap input is missing: ' + required);
    }

    const maxActors = Number(config.maxRemoteActors);
    if (!Number.isInteger(maxActors) || maxActors < 1 || maxActors > 12) {
        throw new Error('maxRemoteActors must be in 1-12.');
    }
    const transport = String(config.remoteTransport || 'tailnet').trim().toLowerCase();
    if (transport !== 'tailnet') {
        throw new Error("Generated remote launchers require remoteTransport=tailnet; got '" + transport + "'.");
    }

    const controlPort = Number(config.controlPort);
    const brokerPort = Number(config.brokerPort);
    const bootstrapPort = Number(config.tailnetBootstrapPort || 7151);
    for (const port of [controlPort, brokerPort, bootstrapPort]) {
        if (!Number.isInteger(port) || port < 1 || port > 65535) {
            throw new Error('Configured Bees ports must be in 1-65535.');
        }
    }
    if (new Set([controlPort, brokerPort, bootstrapPort]).size !== 3) {
        throw new Error('controlPort, brokerPort, and tailnetBootstrapPort must be distinct.');
    }

    if (!exists(paths.tailnetAddressPath)) {
        throw new Error('Learner tailnet address is missing. Authenticate the embedded tailnet first.');
    }
    const tailnetTarget = readText(paths.tailnetAddressPath).trim();
    if (!validateTailnetIp(tailnetTarget)) throw new Error('Unexpected learner tailnet IPv4 address: ' + tailnetTarget);

    const installRoot = String(config.remoteInstallRoot || '%LOCALAPPDATA%\\BeesTraining');
    const linuxInstallRoot = String(config.remoteLinuxInstallRoot || '.local/share/bees-training');
    const torchDevice = String(config.remoteTorchDevice || 'cpu');
    const bootstrapToken = ensureTokenFile(paths.bootstrapTokenPath);
    const bridges = getTailnetBridgePaths();
    const windowsBridge = String(bridges.distribution_windows);
    const linuxBridge = String(bridges.distribution_linux);
    const windowsBridgeName = 'bees-tailnet-bridge-windows.exe';
    const linuxBridgeName = 'bees-tailnet-bridge-linux';

    ensureDir(paths.remoteRoot);
    ensureDir(paths.runtimeRoot);
    const runtime = resolveReleaseTrainingRuntime(python, release, true);
    const releaseRuntimeArchive = String(runtime.archive);

    const runtimeZip = path.join(paths.remoteRoot, 'bees-remote-runtime.zip');
    const runtimeZipTemp = path.join(paths.remoteRoot, 'bees-remote-runtime.new.zip');
    removeIfExists(runtimeZipTemp);
    fs.copyFileSync(releaseRuntimeArchive, runtimeZipTemp);
    atomicReplace(runtimeZipTemp, runtimeZip);

    if (!exists(paths.bootstrapBundleScript)) {
        throw new Error('Remote bootstrap bundle publisher is missing: ' + paths.bootstrapBundleScript);
    }
    const bundleCandidate = path.join(paths.runtimeRoot, 'bees-bootstrap-bundle.candidate.zip');
    const windowsCandidate = path.join(paths.runtimeRoot, 'bees-remote-worker.candidate.cmd');
    const linuxCandidate = path.join(paths.runtimeRoot, 'bees-remote-worker.candidate.sh');
    for (const candidate of [bundleCandidate, windowsCandidate, linuxCandidate]) removeIfExists(candidate);

    // Releases written by Windows PowerShell 5.1 before the UTF-8 fix may still carry a BOM.
    // The Python bootstrap publisher reads strict UTF-8 JSON, so repair that legacy artifact at
    // the boundary before it is embedded for remotes.
    removeUtf8BomIfPresent(paths.latestReleasePath);

    const bundle = invokePythonJson(python, [
        paths.bootstrapBundleScript,
        '--output', bundleCandidate,
        '--runtime', releaseRuntimeArchive,
        '--worker-token', paths.workerTokenPath,
        '--wan-token', paths.wanTokenPath,
        '--release', paths.latestReleasePath,
        '--windows-bridge', windowsBridge,
        '--linux-bridge', linuxBridge,
    ]);
    if (String(bundle.build_id) !== String(release.build_id)) {
        throw new Error(
            'Published bootstrap bundle build identity disagrees with release. bundle=' +
            bundle.build_id + ' release=' + release.build_id
        );
    }

    let windowsBody = readText(paths.remoteBootstrapTemplate);
    const replacements = {
        '__BEES_TAILNET_LEARNER__': escapePowerShellSingleQuoted(tailnetTarget),
        '__BEES_TAILNET_BOOTSTRAP_PORT__': String(bootstrapPort),
        '__BEES_CONTROL_PORT__': String(controlPort),
        '__BEES_BROKER_PORT__': String(brokerPort),
        '__BEES_TAILNET_BRIDGE_FILE__': windowsBridgeName,
        '__BEES_TAILNET_BRIDGE_SHA256__': sha256File(windowsBridge),
        '__BEES_BOOTSTRAP_TOKEN__': escapePowerShellSingleQuoted(bootstrapToken),
        '__BEES_INSTALL_ROOT__': escapePowerShellSingleQuoted(installRoot),
        '__BEES_TORCH_DEVICE__': escapePowerShellSingleQuoted(torchDevice),
    };
    for (const [key, value] of Object.entries(replacements)) windowsBody = windowsBody.replaceAll(key, value);

    const payloadRoot = path.join(paths.runtimeRoot, 'remote-windows-bootstrap-payload');
    const payloadZip = path.join(paths.runtimeRoot, 'remote-windows-bootstrap-payload.zip');
    removeIfExists(payloadRoot, { recursive: true });
    removeIfExists(payloadZip);
    ensureDir(payloadRoot);
    let windowsPayload;
    try {
        const generated = path.join(payloadRoot, 'bees-remote-worker.ps1');
        fs.writeFileSync(generated, windowsBody, 'utf8');
        validatePowerShellFile(generated);
        fs.copyFileSync(windowsBridge, path.join(payloadRoot, windowsBridgeName));
        createWindowsPayloadZip(python, payloadRoot, payloadZip);
        windowsPayload = formatBase64(fs.readFileSync(payloadZip));
    } finally {
        removeIfExists(payloadRoot, { recursive: true });
        removeIfExists(payloadZip);
    }

    const windowsCmd = `@echo off
setlocal EnableExtensions
echo [Bees remote] launching Windows training worker...
set "BEES_BOOTSTRAP_DIR=%TEMP%\\BeesTrainingBootstrap"
set "BEES_SELF=%~f0"
set "BEES_PAYLOAD_ZIP=%BEES_BOOTSTRAP_DIR%\\payload.zip"
if exist "%BEES_BOOTSTRAP_DIR%" rd /s /q "%BEES_BOOTSTRAP_DIR%"
mkdir "%BEES_BOOTSTRAP_DIR%" >nul 2>&1
echo [Bees remote] extracting bundled bootstrap...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:BEES_SELF);$m=[regex]::Match($t,'(?ms)^::BEES_PAYLOAD_BEGIN\\r?\\n(?<payload>.*?)\\r?\\n::BEES_PAYLOAD_END\\s*$');if(-not $m.Success){throw 'Embedded Bees payload block not found.'};$b=$m.Groups['payload'].Value -replace '\\s','';$bytes=[Convert]::FromBase64String($b);if($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B){throw 'Embedded Bees payload is not a valid ZIP archive.'};[IO.File]::WriteAllBytes($env:BEES_PAYLOAD_ZIP,$bytes);Expand-Archive -LiteralPath $env:BEES_PAYLOAD_ZIP -DestinationPath $env:BEES_BOOTSTRAP_DIR -Force"
if errorlevel 1 (
  echo [Bees remote] failed to extract the bundled bootstrap.
  exit /b 1
)
echo [Bees remote] starting PowerShell bootstrap...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BEES_BOOTSTRAP_DIR%\\bees-remote-worker.ps1" %*
set "BEES_EXIT=%ERRORLEVEL%"
if not "%BEES_EXIT%"=="0" echo [Bees remote] worker exited with code %BEES_EXIT%.
rd /s /q "%BEES_BOOTSTRAP_DIR%" >nul 2>&1
exit /b %BEES_EXIT%
::BEES_PAYLOAD_BEGIN
${windowsPayload}
::BEES_PAYLOAD_END
`;
    fs.writeFileSync(windowsCandidate, windowsCmd, 'utf8');

    let linuxBody = readText(paths.remoteLinuxBootstrapTemplate);
    const linuxReplacements = {
        '__BEES_TAILNET_LEARNER__': escapeBashDoubleQuoted(tailnetTarget),
        '__BEES_TAILNET_BOOTSTRAP_PORT__': String(bootstrapPort),
        '__BEES_CONTROL_PORT__': String(controlPort),
        '__BEES_BROKER_PORT__': String(brokerPort),
        '__BEES_TAILNET_BRIDGE_FILE__': linuxBridgeName,
        '__BEES_TAILNET_BRIDGE_SHA256__': sha256File(linuxBridge),
        '__BEES_BOOTSTRAP_TOKEN__': escapeBashDoubleQuoted(bootstrapToken),
        '__BEES_LINUX_INSTALL_ROOT__': escapeBashDoubleQuoted(linuxInstallRoot),
        '__BEES_TORCH_DEVICE__': escapeBashDoubleQuoted(torchDevice),
    };
    for (const [key, value] of Object.entries(linuxReplacements)) linuxBody = linuxBody.replaceAll(key, value);
    linuxBody = linuxBody.replace(/\r\n?/g, '\n');
    const linuxBridgePayload = formatBase64(fs.readFileSync(linuxBridge));

    let linuxWrapper = `#!/usr/bin/env bash
set -euo pipefail

echo "[Bees remote] launching Linux training worker..."

have() { command -v "$1" >/dev/null 2>&1; }
sudo_cmd() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
    elif have sudo; then
        sudo "$@"
    else
        echo "error: root privileges are required to install base64/coreutils, but sudo is unavailable." >&2
        return 1
    fi
}
if ! have base64; then
    echo "[Bees remote] installing base64/coreutils prerequisite..."
    if have apt-get; then sudo_cmd apt-get update && sudo_cmd apt-get install -y coreutils
    elif have dnf; then sudo_cmd dnf install -y coreutils
    elif have yum; then sudo_cmd yum install -y coreutils
    elif have zypper; then sudo_cmd zypper --non-interactive install coreutils
    elif have pacman; then sudo_cmd pacman -Sy --noconfirm coreutils
    else echo "error: base64 is required and no supported package manager was found." >&2; exit 2
    fi
fi

BOOTSTRAP_DIR="\${TMPDIR:-/tmp}/bees-training-bootstrap-$$"
rm -rf "$BOOTSTRAP_DIR"
mkdir -p "$BOOTSTRAP_DIR"
trap 'rm -rf "$BOOTSTRAP_DIR"' EXIT

echo "[Bees remote] extracting bundled bootstrap..."
cat > "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" <<'__BEES_INNER_SCRIPT__'
${linuxBody}
__BEES_INNER_SCRIPT__

base64 -d > "$BOOTSTRAP_DIR/${linuxBridgeName}" <<'__BEES_BRIDGE_PAYLOAD__'
${linuxBridgePayload}
__BEES_BRIDGE_PAYLOAD__

chmod 700 "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" "$BOOTSTRAP_DIR/${linuxBridgeName}"
echo "[Bees remote] starting shell bootstrap..."
set +e
bash "$BOOTSTRAP_DIR/bees-remote-worker-inner.sh" "$@"
BEES_EXIT=$?
set -e
exit "$BEES_EXIT"
`;
    linuxWrapper = linuxWrapper.replace(/\r\n?/g, '\n');
    if (!linuxWrapper.startsWith('#!/usr/bin/env bash\n')) {
        throw new Error('Generated Linux remote launcher has an invalid shebang/newline layout.');
    }
    fs.writeFileSync(linuxCandidate, linuxWrapper, 'utf8');

    atomicReplace(windowsCandidate, path.join(paths.remoteRoot, 'bees-remote-worker.cmd'));
    atomicReplace(linuxCandidate, path.join(paths.remoteRoot, 'bees-remote-worker.sh'));
    atomicReplace(bundleCandidate, paths.bootstrapBundlePath);

    for (const entry of fs.readdirSync(paths.remoteRoot, { withFileTypes: true })) {
        if (!entry.isFile()) continue;
        if (/^bees-remote-worker-.*\.(ps1|cmd|sh)$/.test(entry.name)) {
            removeIfExists(path.join(paths.remoteRoot, entry.name));
        }
    }
    removeIfExists(path.join(paths.remoteRoot, windowsBridgeName));
    removeIfExists(path.join(paths.remoteRoot, linuxBridgeName));

    console.log('Remote launchers prepared in ' + paths.remoteRoot + '.');
    console.log('No SSH account, SSH keys, SSH server, port forwarding, or separate Tailscale installation is required.');
    console.log('Windows: run bees-remote-worker.cmd to start in the background; run bees-remote-worker.cmd stop to stop it.');
    console.log("Linux:   run 'bash bees-remote-worker.sh' to start in the background; run 'bash bees-remote-worker.sh stop' to stop it.");
    console.log('Pass -Envs N (Windows) or --envs N (Linux) only to pin a fixed environment count.');
}

module.exports = {
    buildTailnetBridge,
    ensureTailnetIdentity,
    getTailnetBridgePaths,
    getTailnetBridgeSourceHash,
    prepareRemoteBootstrap,
    resolvePortableGo,
    startTailnetGatewayIfNeeded,
    testTailnetGatewayHealth,
};
