// Converts the authored primary BoxCollider2D for a verified prefab or
// direct Unity prefab variant. Uses fileID strings (not imprecise JS integers).
// The result contains local geometry and prefab-default transform separately;
// real maps can override transform at spawn without editing collider vertices.

function fail(name, reason) { throw new Error(name + ': ' + reason); }
function documents(name, source) {
    const marks = [...source.matchAll(/^--- !u!\d+ &(-?\d+)\r?\n(\w+):/gm)];
    if (!marks.length) fail(name, 'missing YAML documents');
    return new Map(marks.map((m,i) => [
        m[1],
        {kind:m[2], text:source.slice(m.index+m[0].length,
            marks[i+1]?.index ?? source.length)}
    ]));
}
function property(body, key) {
    return body.match(new RegExp('^  ' + key + ': ([^\\r\\n]+)', 'm'))?.[1];
}
function id(value, label) {
    const m = value?.match(/fileID: (-?\d+)/);
    if (!m) fail(label, 'missing fileID');
    return m[1];
}
function num(value, label) {
    const n = Number(value);
    if (value===undefined || value===null || value==='' || !Number.isFinite(n))
        fail(label, 'missing/invalid finite number: ' + value);
    return n;
}
function xy(str, label) {
    const m = str?.match(/\{x: ([^,}]+), y: ([^,}]+)/);
    if (!m) fail(label, 'missing vector2');
    return [num(m[1], label), num(m[2], label)];
}
function zQuaternion(str, label) {
    const m = str?.match(/\{x: ([^,}]+), y: ([^,}]+), z: ([^,}]+), w: ([^,}]+)/);
    if (!m) fail(label, 'missing quaternion');
    if (Math.abs(num(m[1],label))>0.0001 || Math.abs(num(m[2],label))>0.0001)
        fail(label, 'non-planar prefab rotation');
    return [num(m[3],label),num(m[4],label)];
}
function rootBase(name, content, kind, isTrigger) {
    const docs=documents(name,content);
    const candidates=[...docs.entries()].filter(([,d]) =>
        d.kind==='MonoBehaviour' && property(d.text,'ObstacleType')===String(kind)
        && property(d.text,'Collider'));
    if (candidates.length!==1) fail(name,'expected one authored obstacle component');
    const [entityId,entity]=candidates[0];
    const gameId=id(property(entity.text,'m_GameObject'),name);
    const collId=id(property(entity.text,'Collider'),name);
    const game=docs.get(gameId),coll=docs.get(collId);
    if (!game || game.kind!=='GameObject' || !coll ||
        coll.kind!=='BoxCollider2D' ||
        id(property(coll.text,'m_GameObject'),name)!==gameId ||
        property(coll.text,'m_IsTrigger')!==String(isTrigger))
        fail(name,'primary collider must be the expected root BoxCollider2D');
    const transform=[...docs.entries()].find(([,d]) =>
        d.kind==='Transform' && id(property(d.text,'m_GameObject'),name)===gameId);
    if (!transform) fail(name,'missing root Transform');
    return {
        ids:{entity:entityId,transform:transform[0],collider:collId,game:gameId},
        kind, isTrigger,
        layer:num(property(game.text,'m_Layer'),name),
        health:num(property(entity.text,'Health'),name),
        size:xy(property(coll.text,'m_Size'),name),
        offset:xy(property(coll.text,'m_Offset'),name),
        scale:xy(property(transform[1].text,'m_LocalScale'),name),
        position:xy(property(transform[1].text,'m_LocalPosition'),name),
        rotation:zQuaternion(property(transform[1].text,'m_LocalRotation'),name),
    };
}
function overrides(name,text,guid) {
    const match=text.match(/m_SourcePrefab: \{fileID: 100100000, guid: ([0-9a-f]{32}), type: 3\}/);
    if (!match || match[1]!==guid) fail(name,'unexpected prefab inheritance');
    const result=new Map();
    const r=/^    - target: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3\}\r?\n      propertyPath: ([^\r\n]+)\r?\n      value: ([^\r\n]*)/gm;
    for (const m of text.matchAll(r)) {
        if (m[2]!==guid) fail(name,'unknown override source GUID');
        result.set(m[1]+'/'+m[3],m[4].trim());
    }
    if (!result.size) fail(name,'empty overrides');
    return result;
}
function variant(name,text,base,guid) {
    const mod=overrides(name,text,guid);
    const value=(id,key,base)=>num(mod.get(id+'/'+key)??base,name+' '+key);
    const arr=(id,key,base)=>[
        value(id,key+'.x',base[0]),value(id,key+'.y',base[1])
    ];
    const z=value(base.ids.transform,'m_LocalRotation.z',base.rotation[0]);
    const w=value(base.ids.transform,'m_LocalRotation.w',base.rotation[1]);
    const x=value(base.ids.transform,'m_LocalRotation.x',0),
          y=value(base.ids.transform,'m_LocalRotation.y',0);
    if (Math.abs(x)>0.0001 || Math.abs(y)>0.0001)
        fail(name,'unsupported non-planar rotation');
    return {
        ...base,
        layer:value(base.ids.game,'m_Layer',base.layer),
        health:value(base.ids.entity,'Health',base.health),
        size:arr(base.ids.collider,'m_Size',base.size),
        offset:arr(base.ids.collider,'m_Offset',base.offset),
        scale:arr(base.ids.transform,'m_LocalScale',base.scale),
        position:arr(base.ids.transform,'m_LocalPosition',base.position),
        rotation:[z,w],
        kind:value(base.ids.entity,'ObstacleType',base.kind),
        isTrigger:value(base.ids.collider,'m_IsTrigger',base.isTrigger),
    };
}
function toAsset(name,src) {
    if (![0,1].includes(src.kind) || src.layer<0 || src.layer>=32 ||
        !Number.isInteger(src.layer) || src.size.some(v=>v<=0) ||
        src.scale.some(v=>v===0))
        fail(name,'invalid kind, collider dimensions or layer');
    const [z,w]=src.rotation;
    const len=z*z+w*w;
    if (Math.abs(len-1)>0.0002)
        fail(name,'invalid 2D unit quaternion');
    return {
        name,
        obstacle_type:src.kind,
        layer:src.layer,
        authored_health:src.health,
        default_pose:{
            position:src.position,
            rotation_degrees:2*Math.atan2(z,w)*180/Math.PI,
        },
        shape:{
            kind:'box',
            offset:src.offset.map((x,i)=>x*src.scale[i]),
            half_extents:src.size.map((x,i)=>Math.abs(x*src.scale[i])/2),
        },
    };
}
export function createBoxObstacleBatch(rootSources) {
    // rootSources: [[name, source], ...] where each root is the base prefab
    // followed by only its directly verified variants.
    const outputs=[];
    for (const group of rootSources) {
        const {baseName,baseContent,baseGuid,obstacleType,variants,isTrigger=1}=group;
        if (!/^[0-9a-f]{32}$/.test(baseGuid))
            fail(baseName,'invalid metadata GUID');
        const base=rootBase(baseName,baseContent,obstacleType,isTrigger);
        outputs.push(toAsset(baseName,base));
        for (const [name,source] of variants) {
            const input=variant(name,source,base,baseGuid);
            if (input.kind!==obstacleType || input.isTrigger!==isTrigger)
                fail(name,'primary collider kind/trigger changed');
            outputs.push(toAsset(name,input));
        }
    }
    if (new Set(outputs.map(x=>x.name)).size!==outputs.length)
        fail('box obstacles','duplicate prefab names');
    return outputs;
}

/** Decode a prefab variant of an already imported primary-box variant.
 * The original Obstacle.Collider is on the root, so child art scale changes
 * cannot change the physics outline. Root scale/collider edits are rejected.
 */
export function appendNestedBoxVariants(assets, nested) {
    const result = [...assets];
    for (const spec of nested) {
        const {name, parentName, parentGuid, source} = spec;
        const parent = result.find(item => item.name === parentName);
        if (!parent) fail(name, 'missing intermediate prefab ' + parentName);
        if (!/^[0-9a-f]{32}$/.test(parentGuid))
            fail(name, 'missing intermediate prefab GUID');
        const mods = overrides(name, source, parentGuid);
        const rootCandidates = [...mods.keys()]
            .filter(key => key.endsWith('/m_LocalPosition.x'))
            .map(key => key.split('/')[0])
            .filter(id => mods.has(id + '/m_LocalRotation.w'));
        if (rootCandidates.length !== 1)
            fail(name, 'cannot uniquely identify root transform overrides');
        const rootTransformId = rootCandidates[0];
        for (const [key] of mods) {
            const split = key.indexOf('/');
            const id = key.slice(0, split);
            const property = key.slice(split + 1);
            if (id === rootTransformId &&
                (property.startsWith('m_LocalScale.') ||
                 property === 'm_Parent' ||
                 property.startsWith('m_Father'))) {
                fail(name, 'unexpected geometry-affecting root override: ' + property);
            }
            if (property === 'Collider' ||
                property === 'ObstacleType' ||
                property === 'm_Layer' ||
                property === 'm_IsTrigger' ||
                property.startsWith('m_Points.') ||
                property.startsWith('m_Size.') ||
                property.startsWith('m_Offset.')) {
                fail(name, 'unsupported primary-collider override: ' + property);
            }
        }
        const value = (prop, fallback) => num(
            mods.get(rootTransformId + '/' + prop) ?? fallback, name + ' ' + prop);
        const basePosition = parent.default_pose.position;
        const position = [
            value('m_LocalPosition.x', basePosition[0]),
            value('m_LocalPosition.y', basePosition[1])
        ];
        const baseHalfRadians = parent.default_pose.rotation_degrees * Math.PI / 360;
        const z = value('m_LocalRotation.z', Math.sin(baseHalfRadians));
        const w = value('m_LocalRotation.w', Math.cos(baseHalfRadians));
        if (Math.abs(value('m_LocalRotation.x', 0)) > 0.0001 ||
            Math.abs(value('m_LocalRotation.y', 0)) > 0.0001 ||
            Math.abs(z * z + w * w - 1) > 0.0003) {
            fail(name, 'unsupported or invalid nested rotation');
        }
        const rotation_degrees = 2 * Math.atan2(z, w) * 180 / Math.PI;
        if (result.some(item => item.name === name))
            fail(name, 'duplicate inherited obstacle');
        result.push({
            ...parent,
            name,
            default_pose: {position, rotation_degrees},
            shape: {
                kind: 'box',
                offset: [...parent.shape.offset],
                half_extents: [...parent.shape.half_extents]
            },
        });
    }
    return result;
}
