#!/usr/bin/env node
// Extract the authored gameplay PolygonCollider2D from the standalone
// Polygon Obstacle prefab, excluding its child ProximityCollider.
// The primary collider's local vertices and root Transform scale are
// combined once; default placement is stored separately for map overrides.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourcePath = path.join(root, 'Prefabs', 'Entities', 'Obstacles', 'Polygon Obstacle.prefab');
const output = path.join(root, 'native', 'assets', 'polygon-obstacle-colliders.json');

export function importPolygonObstacle(text) {
    const markers = [...text.matchAll(/^--- !u!\d+ &(-?\d+)\r?\n(\w+):/gm)];
    const docs = new Map(markers.map((match, i) => [
        match[1], {
            type: match[2],
            body: text.slice(match.index + match[0].length,
                markers[i + 1]?.index ?? text.length)
        }
    ]));
    const fail = message => { throw new Error('Polygon Obstacle: ' + message); };
    const property = (body, name) =>
        new RegExp('^  ' + name + ': ([^\\r\\n]+)', 'm').exec(body)?.[1];
    const fileId = value => /fileID: (-?\d+)/.exec(value ?? '')?.[1];
    const number = (value, desc) => {
        if (value === undefined || value === '' || !Number.isFinite(Number(value)))
            fail('nonfinite ' + desc + ': ' + value);
        return Number(value);
    };
    const pair = (raw, desc) => {
        const match = /\{x: ([^,}]+), y: ([^,}]+)/.exec(raw ?? '');
        if (!match) fail('missing vector ' + desc);
        return [number(match[1], desc), number(match[2], desc)];
    };
    const candidates = [...docs.entries()].filter(([,doc]) =>
        doc.type === 'MonoBehaviour' && property(doc.body, 'Collider'));
    if (candidates.length !== 1) fail('expected one gameplay Obstacle.Collider reference');
    const obstacle = candidates[0][1].body;
    // Missing ObstacleType is the Unity enum's default 0 = StaticObstacle.
    const obstacleType = number(property(obstacle, 'ObstacleType') ?? '0', 'ObstacleType');
    if (obstacleType !== 0) fail('not a static obstacle');
    const colliderId = fileId(property(obstacle, 'Collider'));
    const collider = docs.get(colliderId);
    const goId = fileId(property(obstacle, 'm_GameObject'));
    const game = docs.get(goId);
    if (!collider || collider.type !== 'PolygonCollider2D' ||
        !game || game.type !== 'GameObject' ||
        fileId(property(collider.body, 'm_GameObject')) !== goId ||
        property(collider.body, 'm_IsTrigger') !== '1')
        fail('primary collider must be a root PolygonCollider2D trigger');

    const transforms = [...docs.entries()].filter(([,doc]) =>
        doc.type === 'Transform' &&
        fileId(property(doc.body, 'm_GameObject')) === goId);
    if (transforms.length !== 1) fail('missing/duplicate primary Transform');
    const transform = transforms[0][1].body;
    if (fileId(property(transform, 'm_Father')) !== '0')
        fail('primary collider is unexpectedly parented');
    const scale = pair(property(transform, 'm_LocalScale'), 'root scale');
    if (scale.some(value => value === 0)) fail('zero-scale collider');
    const position = pair(property(transform, 'm_LocalPosition'), 'root position');
    const rotation = property(transform, 'm_LocalRotation');
    const q = /\{x: ([^,}]+), y: ([^,}]+), z: ([^,}]+), w: ([^,}]+)/.exec(rotation ?? '');
    if (!q || number(q[1], 'qx') !== 0 || number(q[2], 'qy') !== 0)
        fail('unsupported 3D collider rotation');
    const z = number(q[3], 'qz'), w = number(q[4], 'qw');
    if (Math.abs(z * z + w * w - 1) > 0.0003)
        fail('invalid unit rotation');
    const offset = pair(property(collider.body, 'm_Offset'), 'collider offset');
    const paths = /  m_Points:\r?\n    m_Paths:\r?\n([\s\S]*?)(?=^  m_UseDelaunayMesh:)/m.exec(collider.body)?.[1];
    if (!paths) fail('missing explicit polygon paths');
    const vertices = [];
    let activePaths = 0;
    for (const line of paths.split(/\r?\n/)) {
        if (/^    - - \{x: /.test(line)) {
            activePaths++;
            if (activePaths !== 1) fail('multiple polygons require compound physics');
            const [x, y] = pair(line, 'first vertex');
            vertices.push([(x + offset[0]) * scale[0], (y + offset[1]) * scale[1]]);
        } else if (/^      - \{x: /.test(line)) {
            if (activePaths !== 1) fail('vertex without path');
            const [x, y] = pair(line, 'vertex');
            vertices.push([(x + offset[0]) * scale[0], (y + offset[1]) * scale[1]]);
        } else if (line.trim()) {
            fail('unsupported polygon encoding: ' + line.trim());
        }
    }
    if (activePaths !== 1 || vertices.length < 3 ||
        vertices.some(v => v.some(n => !Number.isFinite(n))))
        fail('invalid active polygon');
    let twiceArea = 0;
    for (let i = 0; i < vertices.length; i++) {
        const [x, y] = vertices[i];
        const [nx, ny] = vertices[(i + 1) % vertices.length];
        twiceArea += x * ny - y * nx;
    }
    if (Math.abs(twiceArea) <= 1e-6) fail('degenerate polygon');
    const layer = number(property(game.body, 'm_Layer'), 'collision layer');
    if (!Number.isInteger(layer) || layer >= 32 || layer < 0)
        fail('invalid collision layer');
    const health = number(property(obstacle, 'Health'), 'authored health');
    return {
        schema_version: 1,
        source: 'Prefabs/Entities/Obstacles/Polygon Obstacle.prefab gameplay Collider',
        obstacles: [{
            name: 'Polygon Obstacle',
            obstacle_type: 0,
            layer,
            authored_health: health,
            default_pose: {
                position,
                rotation_degrees: 2 * Math.atan2(z, w) * 180 / Math.PI
            },
            shape: {kind: 'polygon', vertices}
        }]
    };
}

if (process.argv[1] &&
    path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const data = JSON.stringify(importPolygonObstacle(fs.readFileSync(sourcePath, 'utf8')), null, 2) + '\n';
    if (process.argv.includes('--check')) {
        if (!fs.existsSync(output) || fs.readFileSync(output, 'utf8') !== data) {
            process.stderr.write('Polygon obstacle catalog differs from Unity source: ' + output + '\n');
            process.exitCode = 1;
        }
    } else {
        fs.writeFileSync(output, data);
    }
}
