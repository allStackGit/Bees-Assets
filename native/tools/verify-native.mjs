#!/usr/bin/env node
// Local, nonmutating verification of the native Bees rewrite.
//
// This is an operator/developer command, not an extra simulation path:
//   node native/tools/verify-native.mjs
//   node native/tools/verify-native.mjs --assets-only
//
// The default runs all Unity-to-native asset checks and the actual Cargo test
// suite. A successful asset-only run does NOT constitute a native build.

import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const checks = [
    'import-unity-ship-colliders.mjs',
    'import-unity-projectile-colliders.mjs',
    'import-unity-asteroid-colliders.mjs',
    'import-unity-box-obstacle-colliders.mjs',
    'import-unity-polygon-obstacle.mjs',
];
const flags = process.argv.slice(2);
if (flags.some(flag => flag !== '--assets-only')) {
    process.stderr.write('Usage: node native/tools/verify-native.mjs [--assets-only]\n');
    process.exit(2);
}

function run(label, executable, args) {
    process.stdout.write('\n' + label + '\n');
    const result = spawnSync(executable, args, {
        cwd: root,
        stdio: 'inherit',
        shell: false,
    });
    if (result.error) {
        process.stderr.write(label + ': ' + result.error.message + '\n');
        return false;
    }
    if (result.signal) {
        process.stderr.write(label + ': terminated by ' + result.signal + '\n');
        return false;
    }
    if (result.status !== 0) {
        process.stderr.write(label + ': exit status ' + result.status + '\n');
        return false;
    }
    return true;
}

let assetsPassed = true;
// Keep checking all asset families after a failure to reveal which authored
// Unity source differs. A failed family may not pass silently on to Cargo.
for (const check of checks) {
    const label = 'Verify authored assets: ' + check;
    const args = [resolve(root, 'native', 'tools', check), '--check'];
    if (!run(label, process.execPath, args)) assetsPassed = false;
}

if (!assetsPassed) {
    process.stderr.write('\nNative verification failed: an asset catalog is not synchronized.\n');
    process.exit(1);
}
if (flags.includes('--assets-only')) {
    process.stdout.write('\nAsset catalogs match their Unity sources (Rust NOT checked).\n');
    process.exit(0);
}

const cargo = process.platform === 'win32' ? 'cargo.exe' : 'cargo';
if (!run('Compile and test shared native Rust simulation',
    cargo, ['test', '--manifest-path', 'native/Cargo.toml', '--workspace', '--all-targets']))
{
    process.stderr.write(
        '\nCargo verification failed or cargo was unavailable. ' +
        'No native compilation/test success can be claimed.\n',
    );
    process.exit(1);
}
process.stdout.write(
    '\nAuthored assets and native Rust tests passed. ' +
    'Unity/Physics2D gameplay parity still requires live evaluation.\n',
);
