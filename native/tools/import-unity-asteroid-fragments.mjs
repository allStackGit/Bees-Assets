#!/usr/bin/env node
// Import the exact shard/debris prefab palettes serialized on the RL scene.
// Runtime assets retain prefab identities, not Unity GUIDs. Run --check to
// detect scene/prefab drift. This script never runs inside the native game.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const scenePath = 'Scenes/RL 1v1 Training.unity';
const sourceDir = 'Prefabs/Entities/Obstacles/Collision Asteroids';
const output = path.join(root, 'native/assets/asteroid-fragments.json');
const scene = fs.readFileSync(path.join(root, scenePath), 'utf8');

function readList(field) {
    const block = new RegExp(
        '^  ' + field + ':\\r?\\n((?:  - \\{[^\\n]+\\}\\r?\\n)+)', 'm'
    ).exec(scene)?.[1];
    if (!block) throw new Error('Missing prefab palette ' + field);
    const guids = [...block.matchAll(/guid: ([0-9a-f]{32})/g)].map(m => m[1]);
    if (!guids.length || guids.length !== block.split(/\\r?\\n/).filter(Boolean).length) {
        throw new Error('Malformed prefab palette ' + field);
    }
    return guids;
}

function sourceGuid(folder, name) {
    const file = path.join(root, sourceDir, folder, name + '.prefab.meta');
    const source = fs.readFileSync(file, 'utf8');
    const guid = /^guid:\\s*([0-9a-f]{32})\\s*$/m.exec(source)?.[1];
    if (!guid) throw new Error('No prefab GUID in ' + file);
    return guid;
}

function indexByGuid(folder, prefix, count) {
    const index = new Map();
    for (let n = 1; n <= count; n++) {
        const guid = sourceGuid(folder, prefix + n);
        if (index.has(guid)) throw new Error('Duplicate source GUID ' + guid);
        index.set(guid, n);
    }
    return index;
}

const asteroidGuids = indexByGuid('', 'Asteroid ', 28);
const pieceGuids = indexByGuid('Pieces', 'Piece ', 18);
function palette(field, index, expectedCount) {
    const guids = readList(field);
    if (guids.length !== expectedCount || new Set(guids).size !== guids.length) {
        throw new Error('Unexpected palette length or duplicate in ' + field);
    }
    return guids.map(guid => {
        const number = index.get(guid);
        if (!number) throw new Error('Unknown prefab in ' + field + ': ' + guid);
        return number;
    });
}

const manifest = {
    schema_version: 1,
    source: scenePath + ' Prefabs.BreakawayAsteroids / Prefabs.AsteroidPieces',
    shard_prefab_numbers: palette('BreakawayAsteroids', asteroidGuids, 13),
    piece_prefab_numbers: palette('AsteroidPieces', pieceGuids, 18),
};
const generated = JSON.stringify(manifest, null, 2) + '\n';
if (process.argv.includes('--check')) {
    if (!fs.existsSync(output) || fs.readFileSync(output, 'utf8') !== generated) {
        throw new Error('Asteroid fragment palette differs from the RL scene');
    }
} else {
    fs.writeFileSync(output, generated);
}
