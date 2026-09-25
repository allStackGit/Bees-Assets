'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const operatorPath = path.resolve(__dirname, '..', '..', 'bees.ps1');

test('bees.ps1 remains valid PowerShell syntax', { skip: process.platform !== 'win32' }, () => {
    const command =
        '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(' +
        "'" + operatorPath.replace(/'/g, "''") + "'" +
        ', [ref]$null, [ref]$errors) | Out-Null; ' +
        'if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }';

    const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], {
        encoding: 'utf8',
    });

    assert.equal(
        result.status,
        0,
        'bees.ps1 failed PowerShell parsing:\n' + (result.stderr || result.stdout || ''),
    );
});

test('bees.ps1 preserves environment_args as a JSON array', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const matches = source.match(/environment_args=@\(\$envArgs\)/g) || [];
    assert.equal(matches.length, 2,
        'Both training-control state updates must wrap environment args in @() so empty and single-item values serialize as JSON arrays.');
});

test('bees.ps1 normalizes environment args before using Count', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(source, /\$envArgs=@\(Get-EnvironmentArgs \$config\)/,
        'Invoke-Start must normalize Get-EnvironmentArgs output to an array before reading .Count.');
});

test('bees.ps1 process helper does not shadow PowerShell automatic args', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.doesNotMatch(source, /function Invoke-Checked\(\[string\]\$Exe,\[string\[\]\]\$Args,/i);
    assert.match(source, /function Invoke-Checked\(\[string\]\$Exe,\[string\[\]\]\$ArgumentList,/);
    assert.match(source, /& \$Exe @ArgumentList/);
});

test('bees.ps1 suppresses lifecycle command stdout before returning parsed plan', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(
        source,
        /function New-TrainingRunPlan[\s\S]*?\$null=Invoke-Checked \$Python @\([\s\S]*?\$RunLifecycleScript,'plan'[\s\S]*?Get-Content -LiteralPath \$RunPlanPath -Raw \| ConvertFrom-Json/,
        'New-TrainingRunPlan must return only the parsed plan object, not Python stdout plus the plan.'
    );
});

test('bees.ps1 prefers resumable curl for portable Go downloads', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(source, /Get-Command 'curl\.exe'/);
    assert.match(source, /'--continue-at','-','--output',\$temporary,\$url/);
    assert.match(source, /Invoke-WebRequest -UseBasicParsing -Uri \$url -OutFile \$temporary/);
});

test('bees.ps1 validates Unity project availability and build entrypoints', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(source, /Temp\\UnityLockfile/);
    assert.match(source, /Assert-UnityProjectAvailableForBatchBuild/);
    assert.match(source, /Unity exited without producing the expected build entrypoint/);
    assert.match(source, /Bees RL Training\.exe/);
    assert.match(source, /Bees RL Training\.x86_64/);
});

test('bees.ps1 stages Unity builds before moving them into dated folders', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(source, /BuildStaging/);
    assert.match(source, /-beesOutput',\$staging/);
    assert.match(source, /Move-Item -LiteralPath \$staging -Destination \$Output/);
    assert.match(source, /Matching executable\(s\) found elsewhere:/);
});

test('bees.ps1 waits for Unity entrypoint visibility after batch exit', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    assert.match(source, /entrypointDeadline=\[DateTime\]::UtcNow\.AddSeconds\(30\)/);
    assert.match(source, /Start-Sleep -Milliseconds 250/);
});

test('bees.ps1 explicitly waits for Unity GUI process completion', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const unityBuild = source.match(/function Invoke-UnityBuild[\s\S]*?\n\}/)?.[0] || '';
    assert.match(unityBuild, /Start-Process -FilePath \$Unity[\s\S]*?-PassThru/);
    assert.match(unityBuild, /while\(-not \$unityProcess\.WaitForExit\(1000\)\)/);
    assert.match(unityBuild, /\$unityProcess\.ExitCode -ne 0/);
    assert.doesNotMatch(unityBuild, /Invoke-Checked \$Unity/);
});

test('bees.ps1 shows live Unity-like build phase and elapsed status', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const unityBuild = source.match(/function Invoke-UnityBuild[\s\S]*?\n\}/)?.[0] || '';
    assert.match(source, /function Get-UnityBuildProgressStatus/);
    assert.match(source, /Processing scene:/);
    assert.match(source, /Compiling shaders/);
    assert.match(source, /Packing sprite atlases/);
    assert.match(source, /Refreshing assets/);
    assert.match(source, /Building player/);
    assert.match(source, /Finalizing build/);
    assert.match(unityBuild, /\$phase=Get-UnityBuildProgressStatus \$logPath/);
    assert.match(unityBuild, /Write-Progress -Activity \$progressActivity/);
    assert.match(unityBuild, /-CurrentOperation \$phase/);
    assert.match(unityBuild, /elapsed\.ToString\('hh\\:mm\\:ss'\)/);
    assert.match(unityBuild, /Write-Progress -Activity \$progressActivity -Completed/);
});

test('bees.ps1 includes Unity log tail on nonzero build exit', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const unityBuild = source.match(/function Invoke-UnityBuild[\s\S]*?\n\}/)?.[0] || '';
    assert.match(unityBuild, /Get-Content -LiteralPath \$logPath -Tail 60/);
    assert.match(unityBuild, /Last Unity build log lines:/);
});


test('bees.ps1 uses a .zip temporary path for remote runtime compression', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const remoteBootstrap = source.match(/function Prepare-RemoteBootstrap[\s\S]*?\n\}/)?.[0] || '';
    assert.match(remoteBootstrap, /bees-remote-runtime\.new\.zip/);
    assert.match(remoteBootstrap, /Compress-Archive[\s\S]*?-DestinationPath \$runtimeZipTemp/);
    assert.match(remoteBootstrap, /Install-AtomicFile \$runtimeZipTemp \$runtimeZip/);
    assert.doesNotMatch(remoteBootstrap, /\$runtimeZipTemp="\$runtimeZip\.new"/);
});


test('bees.ps1 live status reserves one region and rewrites it in place', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const statusBlock = source.match(/function Get-StatusFrameLines[\s\S]*?function Invoke-Status/)?.[0] || '';
    assert.doesNotMatch(statusBlock, /Clear-Host/);
    assert.match(statusBlock, /function Initialize-LiveStatusRegion/);
    assert.match(statusBlock, /function Write-LiveStatusFrame/);
    assert.match(statusBlock, /\[Console\]::SetCursorPosition/);
    assert.match(statusBlock, /PadRight\(\$width\)/);
    assert.match(statusBlock, /\$region=Initialize-LiveStatusRegion/);
    assert.match(statusBlock, /Write-LiveStatusFrame \$lines \$region\.Top \$region\.Height/);
    assert.match(statusBlock, /if\(\[Console\]::IsOutputRedirected\)/);
    assert.doesNotMatch(statusBlock, /\$inPlace/);
});

test('remote launchers show staged startup progress and unbuffered worker output', () => {
    const windowsPath = path.resolve(__dirname, '..', '..', 'Training', 'bees_remote_bootstrap.ps1');
    const linuxPath = path.resolve(__dirname, '..', '..', 'Training', 'bees_remote_bootstrap.sh');
    const windows = fs.readFileSync(windowsPath, 'utf8');
    const linux = fs.readFileSync(linuxPath, 'utf8');
    for (const stage of ['Stage 1/5', 'Stage 2/5', 'Stage 3/5', 'Stage 4/5', 'Stage 5/5']) {
        assert.match(windows, new RegExp(stage.replace('/', '\\/')));
        assert.match(linux, new RegExp(stage.replace('/', '\\/')));
    }
    assert.match(windows, /& \$venvPython -u @workerArgs/);
    assert.match(linux, /"\$VENV_PYTHON" -u "\$\{WORKER_ARGS\[@\]\}"/);
});

test('managed remote worker emits a recurring live status heartbeat', () => {
    const workerPath = path.resolve(__dirname, '..', '..', 'Training', 'bees_managed_remote_worker.py');
    const source = fs.readFileSync(workerPath, 'utf8');
    assert.match(source, /def _remote_status_summary\(/);
    assert.match(source, /next_status = now \+ 5\.0/);
    assert.match(source, /print\(_remote_status_summary\(args, trainer_id\), flush=True\)/);
    assert.match(source, /"state=\{state\} envs=\{args\.envs\} build=\{build_id\}/);
});


test('bees.ps1 generates a Windows cmd wrapper that bypasses execution policy only for the worker process', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const remoteBootstrap = source.match(/function Prepare-RemoteBootstrap[\s\S]*?\n\}/)?.[0] || '';
    assert.match(remoteBootstrap, /bees-remote-worker\.cmd/);
    assert.match(remoteBootstrap, /powershell\.exe -NoProfile -ExecutionPolicy Bypass -File/);
    assert.match(remoteBootstrap, /bees-remote-worker\.ps1/);
    assert.match(remoteBootstrap, /%\*/);
    assert.doesNotMatch(remoteBootstrap, /Set-ExecutionPolicy/);
});
