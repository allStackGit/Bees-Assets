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
    assert.match(unityBuild, /Start-Process -FilePath \$Unity[\s\S]*?-Wait -PassThru/);
    assert.match(unityBuild, /\$unityProcess\.ExitCode -ne 0/);
    assert.doesNotMatch(unityBuild, /Invoke-Checked \$Unity/);
});

test('bees.ps1 includes Unity log tail on nonzero build exit', () => {
    const source = fs.readFileSync(operatorPath, 'utf8');
    const unityBuild = source.match(/function Invoke-UnityBuild[\s\S]*?\n\}/)?.[0] || '';
    assert.match(unityBuild, /Get-Content -LiteralPath \$logPath -Tail 60/);
    assert.match(unityBuild, /Last Unity build log lines:/);
});
