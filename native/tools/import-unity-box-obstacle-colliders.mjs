#!/usr/bin/env node
// Export the verified direct and second-level prefab variant obstacle
// families. Other obstacle types require explicit inheritance rules.
// --check compares the live Unity prefabs with the committed native manifest.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createBoxObstacleBatch, appendNestedBoxVariants } from './unity-box-obstacle-colliders.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const obstacles = path.join(root, 'Prefabs', 'Entities', 'Obstacles');
const output = path.join(root, 'native', 'assets', 'box-obstacle-colliders.json');

function group(folder, baseName, obstacleType, variantNames) {
    const basePath = path.join(folder, baseName + '.prefab');
    const baseContent = fs.readFileSync(basePath, 'utf8');
    const metadata = fs.readFileSync(basePath + '.meta', 'utf8');
    const baseGuid = /^guid: ([0-9a-f]{32})$/m.exec(metadata)?.[1];
    if (!baseGuid) throw new Error('Missing source .meta GUID: ' + basePath);
    return {
        baseName, baseContent, baseGuid, obstacleType,
        variants: variantNames.map(name => [
            name, fs.readFileSync(path.join(folder, name + '.prefab'), 'utf8')
        ])
    };
}

const names = [
    '_Obstacle', '1x1', '1x1 1', 'Big Wall', 'Corner Obstacle', 'Slanted',
    'Top Border', 'Bottom Border Variant', 'Left Border Variant', 'Right Border Variant',
    'Slanted Variant', 'Box'
];
const sources = [
    group(obstacles, '_Obstacle', 0,
        ['1x1', '1x1 1', 'Big Wall', 'Corner Obstacle', 'Slanted']),
    group(path.join(obstacles, 'Map Borders'), 'Top Border', 1,
        ['Bottom Border Variant', 'Left Border Variant', 'Right Border Variant'])
];
const parentAssets = createBoxObstacleBatch(sources);
function nested(name, parentName) {
    const parentMeta = fs.readFileSync(path.join(obstacles, parentName + '.prefab.meta'), 'utf8');
    const parentGuid = /^guid: ([0-9a-f]{32})$/m.exec(parentMeta)?.[1];
    if (!parentGuid) throw new Error('Missing source GUID: ' + parentName);
    return {
        name, parentName, parentGuid,
        source: fs.readFileSync(path.join(obstacles, name + '.prefab'), 'utf8'),
    };
}
const parsed = appendNestedBoxVariants(parentAssets, [
    nested('Slanted Variant', 'Slanted'),
    nested('Box', 'Corner Obstacle'),
]);
if (parsed.length !== names.length ||
    parsed.some((entry,index) => entry.name !== names[index])) {
    throw new Error('Unexpected native obstacle prefab identity/order');
}
const data = JSON.stringify({
    schema_version: 1,
    source: 'Prefabs/Entities/Obstacles root and Map Borders',
    obstacles: parsed,
}, null, 2) + '\n';

if (process.argv.includes('--check')) {
    if (!fs.existsSync(output) || fs.readFileSync(output, 'utf8') !== data) {
        process.stderr.write('Box obstacle collider catalog differs from Unity source: ' + output + '\n');
        process.exitCode = 1;
    } else {
        process.stdout.write('Verified ' + parsed.length + ' authored box obstacles.\n');
    }
} else {
    fs.writeFileSync(output, data);
    process.stdout.write('Imported ' + parsed.length + ' authored box obstacles.\n');
}
