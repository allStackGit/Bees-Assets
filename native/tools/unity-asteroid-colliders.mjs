// Pure, auditable Unity Asteroid prefab-variant decoder.
//
// This intentionally reads ONLY the primary Collider referenced by the
// CollisionAsteroid MonoBehaviour, not the child proximity/clearance sensors.
// Unity can retain serialized overrides for array elements that are OUTSIDE
// the declared Array.size. Those must never become runtime collider vertices.
//
// This decoder rejects unsupported prefab inheritance, extra active polygon
// paths and missing vertices rather than silently inventing collision shapes.

function fail(name, message) {
    throw new Error(`${name}: ${message}`);
}

function documents(name, content) {
    const markers = [...content.matchAll(/^--- !u!\d+ &(-?\d+)\r?\n(\w+):/gm)];
    if (!markers.length) fail(name, 'missing Unity YAML documents');
    const docs = new Map();
    markers.forEach((match, index) => {
        if (docs.has(match[1])) fail(name, 'duplicate Unity fileID ' + match[1]);
        docs.set(match[1], {
            kind: match[2],
            body: content.slice(match.index + match[0].length,
                markers[index + 1]?.index ?? content.length),
        });
    });
    return docs;
}

function field(body, name) {
    return new RegExp(`^  ${name}: (.*)$`, 'm').exec(body)?.[1]?.trim();
}

function fileId(raw, name) {
    const match = /fileID: (-?\d+)/.exec(raw || '');
    if (!match) fail(name, 'expected Unity fileID, got ' + raw);
    return match[1];
}

function finiteNumber(raw, name) {
    const n = Number(raw);
    if (raw === undefined || raw === '' || !Number.isFinite(n)) {
        fail(name, 'expected finite numeric value, got ' + raw);
    }
    return n;
}

function xy(raw, name) {
    const m = /\{x: ([^,}]+), y: ([^,}]+)/.exec(raw || '');
    if (!m) fail(name, 'expected Vector2: ' + raw);
    return [finiteNumber(m[1], name), finiteNumber(m[2], name)];
}

function polygonPaths(name, body) {
    const match = /  m_Points:\r?\n    m_Paths:\r?\n([\s\S]*?)(?=^  [a-zA-Z_])/m.exec(body);
    if (!match) fail(name, 'missing authored m_Points.m_Paths');
    const paths = [];
    for (const line of match[1].split(/\r?\n/)) {
        if (/^    - - \{x: /.test(line)) {
            paths.push([xy(line, name)]);
        } else if (/^      - \{x: /.test(line)) {
            if (!paths.length) fail(name, 'polygon vertex before path');
            paths[paths.length - 1].push(xy(line, name));
        } else if (line.trim()) {
            fail(name, 'unsupported polygon YAML path encoding: ' + line);
        }
    }
    if (!paths.length || paths.some(p => p.length < 3)) {
        fail(name, 'invalid source polygon vertices');
    }
    return paths;
}

function baseAsteroid(name, content) {
    const docs = documents(name, content);
    const entities = [...docs.entries()].filter(([, doc]) =>
        doc.kind === 'MonoBehaviour' &&
        field(doc.body, 'ObstacleType') === '2' &&
        field(doc.body, 'Collider'));
    if (entities.length !== 1) fail(name, 'expected one CollisionAsteroid component');
    const entity = entities[0][1];
    const colliderId = fileId(field(entity.body, 'Collider'), name);
    const collider = docs.get(colliderId);
    if (!collider || collider.kind !== 'PolygonCollider2D' ||
        field(collider.body, 'm_IsTrigger') !== '1') {
        fail(name, 'main Collider must be a trigger PolygonCollider2D');
    }
    const gameId = fileId(field(entity.body, 'm_GameObject'), name);
    const game = docs.get(gameId);
    if (!game || game.kind !== 'GameObject' ||
        fileId(field(collider.body, 'm_GameObject'), name) !== gameId) {
        fail(name, 'main collider must be on root obstacle GameObject');
    }
    const transformEntry = [...docs.entries()].find(([, doc]) =>
        doc.kind === 'Transform' &&
        fileId(field(doc.body, 'm_GameObject'), name) === gameId);
    if (!transformEntry) fail(name, 'missing root Transform');
    const scale = xy(field(transformEntry[1].body, 'm_LocalScale'), name);
    const offset = xy(field(collider.body, 'm_Offset'), name);
    const paths = polygonPaths(name, collider.body);
    return {
        ids: {
            entity: entities[0][0], game: gameId, collider: colliderId,
            transform: transformEntry[0],
        },
        size_class: finiteNumber(field(entity.body, 'SizeClass'), name),
        layer: finiteNumber(field(game.body, 'm_Layer'), name),
        scale, offset, paths,
    };
}

function readModifications(name, content, expectedGuid) {
    const source = /m_SourcePrefab: \{fileID: 100100000, guid: ([0-9a-f]+), type: 3\}/.exec(content);
    if (!source || source[1] !== expectedGuid) {
        fail(name, 'variant does not directly inherit the verified asteroid base prefab');
    }
    const mods = new Map();
    const regex = /^    - target: \{fileID: (-?\d+), guid: ([0-9a-f]+), type: 3\}\r?\n      propertyPath: ([^\r\n]+)\r?\n      value: ([^\r\n]*)/gm;
    for (const match of content.matchAll(regex)) {
        if (match[2] !== expectedGuid) fail(name, 'modification references another prefab GUID');
        mods.set(match[1] + '/' + match[3], match[4].trim());
    }
    if (!mods.size) fail(name, 'variant has no recognized modifications');
    return mods;
}

function asteroidFromVariant(name, content, base, baseGuid) {
    const mods = readModifications(name, content, baseGuid);
    const get = (id, fieldName, fallback) =>
        mods.get(id + '/' + fieldName) ?? fallback;
    const number = (id, key, fallback) =>
        finiteNumber(get(id, key, fallback), name + ' ' + key);
    const sizeClass = number(base.ids.entity, 'SizeClass', base.size_class);
    const layer = number(base.ids.game, 'm_Layer', base.layer);
    const scale = [
        number(base.ids.transform, 'm_LocalScale.x', base.scale[0]),
        number(base.ids.transform, 'm_LocalScale.y', base.scale[1]),
    ];
    const offset = [
        number(base.ids.collider, 'm_Offset.x', base.offset[0]),
        number(base.ids.collider, 'm_Offset.y', base.offset[1]),
    ];
    const count = number(base.ids.collider, 'm_Points.m_Paths.Array.size',
        base.paths.length);
    if (!Number.isInteger(count) || count !== 1) {
        fail(name, 'multiple active polygon paths require explicit compound physics');
    }
    const key = 'm_Points.m_Paths.Array.data[0].Array.';
    const n = number(base.ids.collider, key + 'size', base.paths[0].length);
    if (!Number.isInteger(n) || n < 3 || n > 4096) {
        fail(name, 'invalid active polygon vertex count');
    }
    const vertices = [];
    for (let i = 0; i < n; i++) {
        const source = base.paths[0][i];
        const x = get(base.ids.collider, key + `data[${i}].x`, source?.[0]);
        const y = get(base.ids.collider, key + `data[${i}].y`, source?.[1]);
        vertices.push([finiteNumber(x, name + ' vertex x ' + i),
            finiteNumber(y, name + ' vertex y ' + i)]);
    }
    const trigger = number(base.ids.collider, 'm_IsTrigger', 1);
    if (trigger !== 1) fail(name, 'primary collider no longer a trigger');
    return {sizeClass, layer, scale, offset, vertices};
}

function toAsset(name, value) {
    const {sizeClass, layer, scale, offset, vertices} = value;
    if (!Number.isInteger(sizeClass) || sizeClass < 1 || sizeClass > 255 ||
        !Number.isInteger(layer) || layer < 0 || layer >= 32 ||
        scale.some(x => x === 0)) {
        fail(name, 'invalid size class, layer or scale');
    }
    // The Unity transform scales both the collider offset and the vertices.
    const local = vertices.map(([x,y]) => [
        (x + offset[0]) * scale[0], (y + offset[1]) * scale[1]
    ]);
    if (local.some(v => v.some(c => !Number.isFinite(c)))) {
        fail(name, 'nonfinite transformed polygon vertex');
    }
    return {
        name, size_class: sizeClass, layer,
        shape: {kind:'polygon', vertices:local}
    };
}

/// Inputs are literal Unity source files from the same revision as the manifest.
/// The public variant input is intentionally explicit for reproducible checks.
/// Supports source validation in bounded batches when repository APIs
/// cannot fetch the entire prefab directory in one operation.
export function createAsteroidBatch(baseContent, baseGuid, otherPrefabs) {
    const base = baseAsteroid('Asteroid 1', baseContent);
    if (!/^[0-9a-f]{32}$/.test(baseGuid)) {
        fail('Asteroid 1', 'invalid source .meta GUID');
    }
    const outputs = [toAsset('Asteroid 1', {
        sizeClass: base.size_class, layer: base.layer,
        scale: base.scale, offset: base.offset, vertices: base.paths[0],
    })];
    const seen = new Set([1]);
    for (const [name, text] of otherPrefabs) {
        const match = /^Asteroid (\\d+)$/.exec(name);
        if (!match) fail(name, 'unrecognized asteroid prefab name');
        const index = Number(match[1]);
        if (index < 2 || index > 28 || seen.has(index)) {
            fail(name, 'duplicate or unexpected prefab');
        }
        seen.add(index);
        const variant = text.includes('PrefabInstance:')
            ? asteroidFromVariant(name, text, base, baseGuid)
            : (() => {
                const standalone = baseAsteroid(name, text);
                if (standalone.paths.length !== 1) {
                    fail(name, 'multiple active polygon paths need compound physics');
                }
                return {
                    sizeClass: standalone.size_class, layer: standalone.layer,
                    scale: standalone.scale, offset: standalone.offset,
                    vertices: standalone.paths[0],
                };
            })();
        outputs.push(toAsset(name, variant));
    }
    return outputs;
}

export function createAsteroidManifest(baseContent, baseGuid, otherPrefabs,
    healthIncrement)
{
    const asteroidTypes = createAsteroidBatch(baseContent, baseGuid, otherPrefabs);
    asteroidTypes.sort((a,b) =>
        Number(a.name.split(' ')[1]) - Number(b.name.split(' ')[1]));
    if (asteroidTypes.length !== 28 ||
        asteroidTypes.some((a,i) => a.name !== 'Asteroid ' + (i+1))) {
        fail('Collision Asteroids', 'expected all 28 asteroid prefabs exactly once');
    }
    return {
        schema_version: 1,
        source: 'Prefabs/Entities/Obstacles/Collision Asteroids and ConfigData.Gameplay',
        collision_asteroid_health_increment: healthIncrement,
        asteroids: asteroidTypes,
    };
}
