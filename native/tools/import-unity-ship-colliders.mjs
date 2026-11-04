#!/usr/bin/env node
// One-time/transition importer for the Unity ship hull geometry.
// Native gameplay owns the resulting native/assets/ship-colliders.json.
// Does not require the Unity Editor; works with Node on Linux/macOS/Windows.
//
// We deliberately resolve Entity.Collider's serialized file ID instead of
// taking the first collider in a prefab: vision, range and other sensors may
// also have colliders and would create incorrect ship hitboxes.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourceDir = path.join(root, 'Prefabs', 'Entities', 'Ships');
const target = path.join(root, 'native', 'assets', 'ship-colliders.json');

function fail(source, message) {
    throw new Error(`${source}: ${message}`);
}

function parseDocuments(source, text) {
    const headers = [...text.matchAll(/^--- !u!\d+ &(-?\d+)\r?\n(\w+):/gm)];
    const documents = new Map();
    for (let index = 0; index < headers.length; index++) {
        const header = headers[index];
        documents.set(header[1], {
            kind: header[2],
            body: text.slice(
                header.index + header[0].length,
                index + 1 === headers.length ? text.length : headers[index + 1].index,
            ),
        });
    }
    if (!documents.size) fail(source, 'no Unity prefab documents');
    return documents;
}

function field(body, key) {
    return new RegExp(`^  ${key}: (.+)$`, 'm').exec(body)?.[1]?.trim();
}

function fileId(source, value) {
    const result = /fileID: (-?\d+)/.exec(value || '');
    if (!result) fail(source, `missing serialized fileID in ${value}`);
    return result[1]; // Keep signed 64-bit Unity IDs as strings.
}

function vec2(source, raw) {
    const match = /\{x: ([^,]+), y: ([^,}]+)/.exec(raw || '');
    if (!match) fail(source, `invalid Unity Vector2: ${raw}`);
    const values = [Number(match[1]), Number(match[2])];
    if (values.some(value => !Number.isFinite(value))) {
        fail(source, `non-finite Unity Vector2: ${raw}`);
    }
    return values;
}

function parsePrefab(name, text) {
    const source = name + '.prefab';
    const documents = parseDocuments(source, text);
    function document(id) {
        const value = documents.get(id);
        if (!value) fail(source, `referenced component ${id} does not exist`);
        return value;
    }
    const entities = [...documents.values()].filter(doc =>
        doc.kind === 'MonoBehaviour' &&
        /^  ShipType: \d+/m.test(doc.body) &&
        /^  Collider: \{fileID: -?\d+\}/m.test(doc.body) &&
        /^  Body: \{fileID: -?\d+\}/m.test(doc.body) &&
        /^  Transform: \{fileID: -?\d+\}/m.test(doc.body)
    );
    if (entities.length !== 1) {
        fail(source, `expected one primary ship Entity, found ${entities.length}`);
    }
    const entity = entities[0];
    const colliderId = fileId(source, field(entity.body, 'Collider'));
    const collider = document(colliderId);
    const entityObjectId = fileId(source, field(entity.body, 'm_GameObject'));
    const gameObject = document(entityObjectId);
    const transform = document(fileId(source, field(entity.body, 'Transform')));
    const colliderObjectId = fileId(source, field(collider.body, 'm_GameObject'));
    if (colliderObjectId !== entityObjectId || gameObject.kind !== 'GameObject' ||
        transform.kind !== 'Transform') {
        fail(source, 'primary hull is not on the entity root GameObject');
    }

    const shipType = Number(field(entity.body, 'ShipType'));
    const layer = Number(field(gameObject.body, 'm_Layer'));
    const trigger = field(collider.body, 'm_IsTrigger') === '1';
    if (!Number.isInteger(shipType) || shipType < 0 ||
        !Number.isInteger(layer) || layer < 0 || layer >= 32 || !trigger) {
        fail(source, 'invalid ship type, collision layer or main trigger flag');
    }

    const scale = vec2(source, field(transform.body, 'm_LocalScale'));
    if (scale.some(value => value === 0)) {
        fail(source, 'zero-scale primary collider is not supported');
    }
    const offset = vec2(source, field(collider.body, 'm_Offset'))
        .map((value, i) => value * scale[i]);
    let shape;

    if (collider.kind === 'CircleCollider2D') {
        if (Math.abs(Math.abs(scale[0]) - Math.abs(scale[1])) > 1e-5) {
            fail(source, 'nonuniform scaled circle requires explicit physics parity work');
        }
        const radius = Number(field(collider.body, 'm_Radius')) * Math.abs(scale[0]);
        if (!Number.isFinite(radius) || radius < 0) fail(source, 'invalid circle radius');
        shape = { kind: 'circle', offset, radius };
    } else if (collider.kind === 'BoxCollider2D') {
        const halfExtents = vec2(source, field(collider.body, 'm_Size'))
            .map((value, i) => Math.abs(value * scale[i] * 0.5));
        if (halfExtents.some(value => value <= 0)) fail(source, 'invalid box size');
        shape = { kind: 'box', offset, half_extents: halfExtents };
    } else if (collider.kind === 'PolygonCollider2D') {
        const paths = /m_Paths:\r?\n([\s\S]*?)(?=\n  m_\w+:|$)/.exec(collider.body)?.[1] || '';
        const pathCount = (paths.match(/^    - /gm) || []).length;
        if (pathCount !== 1) {
            fail(source, `expected one polygon path, found ${pathCount}`);
        }
        const vertices = [...paths.matchAll(/\{x:\s*([^,]+), y:\s*([^}]+)\}/g)]
            .map(match => [Number(match[1]), Number(match[2])])
            .map(point => point.map((value, i) => value * scale[i] + offset[i]));
        if (vertices.length < 3 || vertices.some(point =>
            point.some(value => !Number.isFinite(value)))) {
            fail(source, 'invalid polygon vertices');
        }
        shape = { kind: 'polygon', vertices };
    } else {
        fail(source, `unsupported primary collider ${collider.kind}`);
    }

    return { name, ship_type: shipType, layer, trigger, shape };
}

const filenames = fs.readdirSync(sourceDir)
    .filter(filename => filename.endsWith('.prefab')).sort();
const ships = filenames.map(filename => {
    const name = filename.slice(0, -'.prefab'.length);
    return parsePrefab(name, fs.readFileSync(path.join(sourceDir, filename), 'utf8'));
}).sort((a, b) => a.ship_type - b.ship_type);

if (ships.length !== 24 || ships.some((ship, index) => ship.ship_type !== index)) {
    fail(sourceDir, 'the 24 authored ship type IDs must each appear exactly once (0 through 23)');
}

const manifest = {
    schema_version: 1,
    source: 'Unity Prefabs/Entities/Ships primary Entity.Collider',
    ships,
};
const generated = JSON.stringify(manifest, null, 2) + '\n';
if (process.argv.includes('--check')) {
    const current = fs.existsSync(target) ? fs.readFileSync(target, 'utf8') : '';
    if (current !== generated) {
        process.stderr.write(`Ship collider manifest is not synchronized: ${target}\n`);
        process.exitCode = 1;
    } else {
        process.stdout.write(`Verified ${ships.length} primary ship colliders.\n`);
    }
} else {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, generated);
    process.stdout.write(`Imported ${ships.length} primary ship colliders to ${target}\n`);
}
