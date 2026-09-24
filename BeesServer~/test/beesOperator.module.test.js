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
