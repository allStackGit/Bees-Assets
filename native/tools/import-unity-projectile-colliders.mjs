#!/usr/bin/env node
// Migration-only exporter for authored Unity projectile collider geometry.
// Runtime consumes native/assets/projectile-colliders.json without Unity or Node.
//
// Unlike visual impact-splash prefabs, actual projectiles have a serialized
// Projectile.Type and Projectile.HasCollider. The Collider field points to
// the authoritative collision shape, not the visual sprite dimensions.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourceDir = path.join(root, 'Prefabs', 'Entities', 'Projectiles');
const target = path.join(root, 'native', 'assets', 'projectile-colliders.json');

function fail(source, message) {
    throw new Error(`${source}: ${message}`);
}

function documents(source, content) {
    const sections = [...content.matchAll(/^--- !u!\d+ &(-?\d+)\r?\n(\w+):/gm)];
    if (!sections.length) fail(source, 'no Unity prefab documents');
    const objects = new Map();
    sections.forEach((part, i) => objects.set(part[1], {
        kind: part[2],
        body: content.slice(part.index + part[0].length,
            i + 1 < sections.length ? sections[i + 1].index : content.length),
    }));
    return objects;
}

function property(body, name) {
    return new RegExp(`^  ${name}: (.+)$`, 'm').exec(body)?.[1]?.trim();
}

function fileId(source, raw) {
    const match = /fileID: (-?\d+)/.exec(raw || '');
    if (!match) fail(source, `missing file ID: ${raw}`);
    return match[1];
}

function vector(source, raw) {
    const match = /\{x: ([^,]+), y: ([^,}]+)/.exec(raw || '');
    if (!match) fail(source, `malformed Vector2: ${raw}`);
    const values = [Number(match[1]), Number(match[2])];
    if (values.some(value => !Number.isFinite(value))) fail(source, 'nonfinite Vector2');
    return values;
}

function importProjectile(name, content) {
    const source = name + '.prefab';
    const docs = documents(source, content);
    const match = [...docs.values()].filter(doc => doc.kind === 'MonoBehaviour' &&
        /^  Type: \d+/m.test(doc.body) &&
        /^  HasCollider: [01]/m.test(doc.body) &&
        /^  Collider: \{fileID: -?\d+\}/m.test(doc.body));
    if (!match.length) return null; // visual effects and sounds only
    if (match.length !== 1) fail(source, 'ambiguous projectile components');
    const entity = match[0];
    const find = id => {
        const doc = docs.get(id);
        if (!doc) fail(source, `missing referenced prefab component ${id}`);
        return doc;
    };
    const gameId = fileId(source, property(entity.body, 'm_GameObject'));
    const game = find(gameId);
    const transform = find(fileId(source, property(entity.body, 'Transform')));
    const type = Number(property(entity.body, 'Type'));
    const layer = Number(property(game.body, 'm_Layer'));
    const has_body = property(entity.body, 'HasBody') === '1';
    const hasCollider = property(entity.body, 'HasCollider') === '1';
    if (!Number.isInteger(type) || type < 1 || type > 15 ||
        !Number.isInteger(layer) || layer < 0 || layer >= 32 ||
        game.kind !== 'GameObject' || transform.kind !== 'Transform') {
        fail(source, 'invalid identity, layer, or transform');
    }

    let shape = null;
    const colliderId = fileId(source, property(entity.body, 'Collider'));
    if (hasCollider) {
        const collider = find(colliderId);
        if (fileId(source, property(collider.body, 'm_GameObject')) !== gameId ||
            property(collider.body, 'm_IsTrigger') !== '1') {
            fail(source, 'primary projectile collider is not a root trigger');
        }
        const scale = vector(source, property(transform.body, 'm_LocalScale'));
        const offset = vector(source, property(collider.body, 'm_Offset'))
            .map((v, i) => v * scale[i]);
        if (collider.kind === 'CircleCollider2D') {
            if (Math.abs(Math.abs(scale[0]) - Math.abs(scale[1])) > 1e-6) {
                fail(source, 'nonuniform scaled circle requires explicit physics work');
            }
            const radius = Number(property(collider.body, 'm_Radius')) * Math.abs(scale[0]);
            if (!Number.isFinite(radius) || radius < 0) fail(source, 'invalid circle radius');
            shape = { kind: 'circle', offset, radius };
        } else if (collider.kind === 'BoxCollider2D') {
            const half_extents = vector(source, property(collider.body, 'm_Size'))
                .map((v, i) => Math.abs(v * scale[i] * 0.5));
            if (half_extents.some(v => v <= 0)) fail(source, 'invalid box dimensions');
            shape = { kind: 'box', offset, half_extents };
        } else {
            fail(source, `unsupported projectile collider: ${collider.kind}`);
        }
    } else if (colliderId !== '0') {
        fail(source, 'non-collidable projectile unexpectedly has a collider');
    }
    // Frigate Rocket has a prefab-specific maximum acceleration speed.
    // Read it from the same authoritative projectile component rather than
    // embedding a second magic constant in Rust or the worker.
    if (type === 5) {
        const rocket_max_speed = Number(property(entity.body, 'MaxSpeed'));
        if (!Number.isFinite(rocket_max_speed) || rocket_max_speed <= 0) {
            fail(source, 'Frigate Rocket requires a positive authored MaxSpeed');
        }
        return { name, projectile_type: type, layer, has_body, rocket_max_speed, shape };
    }
    return { name, projectile_type: type, layer, has_body, shape };
}

const projectiles = fs.readdirSync(sourceDir)
    .filter(name => name.endsWith('.prefab'))
    .map(filename => importProjectile(filename.slice(0, -7),
        fs.readFileSync(path.join(sourceDir, filename), 'utf8')))
    .filter(Boolean)
    .sort((a, b) => a.projectile_type - b.projectile_type);

if (projectiles.length !== 15 ||
    projectiles.some((entry, index) => entry.projectile_type !== index + 1)) {
    fail(sourceDir, 'each of the 15 ConfigData.ProjectileTypes must have one collider definition');
}

const manifest = {
    schema_version: 1,
    source: 'Unity Prefabs/Entities/Projectiles, Entity.Collider and ConfigData.ProjectileTypes',
    projectiles,
};
const generated = JSON.stringify(manifest, null, 2) + '\n';
if (process.argv.includes('--check')) {
    const existing = fs.existsSync(target) ? fs.readFileSync(target, 'utf8') : '';
    if (existing !== generated) {
        process.stderr.write(`Projectile asset manifest is not synchronized: ${target}\n`);
        process.exitCode = 1;
    } else {
        process.stdout.write(`Verified ${projectiles.length} projectile colliders.\n`);
    }
} else {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, generated);
    process.stdout.write(`Imported ${projectiles.length} projectile colliders.\n`);
}
