#!/usr/bin/env node
// Migration-only conversion of every collision-asteroid prefab into the
// portable, immutable native geometry catalog. Node is not needed at runtime.
// Use --check in a checkout to fail on any prefab/manifest divergence.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createAsteroidManifest } from './unity-asteroid-colliders.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourceDir = path.join(root, 'Prefabs', 'Entities', 'Obstacles', 'Collision Asteroids');
const output = path.join(root, 'native', 'assets', 'asteroid-colliders.json');
const baseFile = path.join(sourceDir, 'Asteroid 1.prefab');

const metadata = fs.readFileSync(baseFile + '.meta', 'utf8');
const baseGuid = /^guid:\s*([0-9a-f]{32})\s*$/m.exec(metadata)?.[1];
if (!baseGuid) throw new Error('Missing GUID for Asteroid 1 base prefab');

const config = fs.readFileSync(path.join(root, 'Scripts', 'ConfigData.Gameplay.cs'), 'utf8');
const increment = Number(/CollisionAsteroidHealthIncrement\s*=\s*(\d+)/.exec(config)?.[1]);
if (!Number.isSafeInteger(increment) || increment <= 0) {
    throw new Error('Missing CollisionAsteroidHealthIncrement in Unity gameplay config');
}

const names = fs.readdirSync(sourceDir)
    .filter(name => /^Asteroid \d+\.prefab$/.test(name))
    .sort((a, b) => Number(a.match(/\d+/)[0]) - Number(b.match(/\d+/)[0]));
if (names.length !== 28) {
    throw new Error(`Expected 28 collision asteroid prefabs; found ${names.length}`);
}

const otherPrefabs = names.slice(1).map(file => [
    file.slice(0, -7),
    fs.readFileSync(path.join(sourceDir, file), 'utf8'),
]);
const manifest = createAsteroidManifest(
    fs.readFileSync(baseFile, 'utf8'), baseGuid, otherPrefabs, increment,
);
const generated = JSON.stringify(manifest, null, 2) + '\n';

if (process.argv.includes('--check')) {
    if (!fs.existsSync(output) || fs.readFileSync(output, 'utf8') !== generated) {
        process.stderr.write('Asteroid collider catalog differs from Unity source: ' + output + '\n');
        process.exitCode = 1;
    } else {
        process.stdout.write(`Verified ${manifest.asteroids.length} collision asteroids.\n`);
    }
} else {
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, generated);
    process.stdout.write(`Imported ${manifest.asteroids.length} collision asteroids.\n`);
}
