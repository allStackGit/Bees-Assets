'use strict';

// Trusted adapters expose only scalar configuration controls from their actual
// installed config. No browser-supplied path, key, or executable is trusted.
const BAD_KEYS=new Set(['__proto__','prototype','constructor']);
const TRAINING=/^(?:model_config\.)?(?:training|optimizer|ppo|trainer|learning|self_play)(?:\.|$)/i;
function fieldsFor(config,project) {
    const base=project==='tinybrains' ? config?.model_config : config;
    if(!base||typeof base!=='object'||Array.isArray(base))return [];
    const fields=[];
    const visit=(node,segments)=>{
        if(fields.length>128||segments.length>7)return;
        for(const [key,value] of Object.entries(node)) {
            if(!/^[a-z][a-z0-9_]{0,63}$/i.test(key)||BAD_KEYS.has(key))continue;
            const path=segments.concat(key),joined=path.join('.');
            if(value&&typeof value==='object'&&!Array.isArray(value)) {
                if(path.length<7)visit(value,path);
                continue;
            }
            if(typeof value!=='number'&&typeof value!=='boolean')continue;
            if(typeof value==='number'&&!Number.isFinite(value))continue;
            // Trainer identities, seed references and runtime device selection
            // are controlled by the approved adapter/profile, not this editor.
            if(/(^|\.)(device|seed|update|updates|worker|workers|threads|checkpoint|version|id|port|path|hash|sha256)(_|\.|$)/i.test(joined))
                continue;
            if(project==='tinybrains'&&!['training','environment','reward','self_play'].includes(path[0]))
                continue;
            const leaf=key.toLowerCase(),integer=typeof value==='number'&&Number.isInteger(value);
            const ratio=/(?:^|_)(?:gamma|lambda|probability|rate|fraction|ppo_clip)(?:$|_)/.test(leaf);
            const positive=/(learning_rate|batch_size|buffer_size|epochs|colony_batch_size|turns|timeout|count|size|radius)/.test(leaf);
            const min=typeof value==='boolean'?undefined:positive?(leaf==='learning_rate'?0.000000001:1):value<0?-1000000000:0;
            const max=typeof value==='boolean'?undefined:ratio?1:
                leaf==='learning_rate'?1:integer?1000000000:1000000000;
            fields.push({key:joined,section:project==='tinybrains'
                    ?path[0]==='training'?'training':'environment'
                    :TRAINING.test(joined)?'training':'environment',
                label:key.replace(/_/g,' ').replace(/\b\w/g,c=>c.toUpperCase()),
                type:typeof value==='boolean'?'boolean':integer?'integer':'number',
                default:value,...(typeof value==='number'?{min,max}:{})});
        }
    };
    visit(base,[]);
    if(fields.length>128)throw Error('Installed configuration has too many editable fields.');
    return fields;
}
function applyOverrides(config,project,overrides) {
    if(overrides===undefined)return JSON.parse(JSON.stringify(config));
    if(!overrides||typeof overrides!=='object'||Array.isArray(overrides))
        throw Error('Design overrides must be a keyed object.');
    const fields=new Map(fieldsFor(config,project).map(field=>[field.key,field]));
    if(Object.keys(overrides).length>128)
        throw Error('Too many design overrides.');
    const result=JSON.parse(JSON.stringify(config));
    const root=project==='tinybrains'?result.model_config:result;
    for(const [key,value] of Object.entries(overrides)) {
        const field=fields.get(key);
        if(!field)throw Error('Unsupported '+project+' design parameter: '+key);
        if(field.type==='boolean'?typeof value!=='boolean':
            typeof value!=='number'||!Number.isFinite(value)||
            field.type==='integer'&&!Number.isSafeInteger(value)||
            value<field.min||value>field.max)
            throw Error('Invalid value for design parameter '+key+'.');
        const segments=key.split('.');
        let target=root;
        for(const part of segments.slice(0,-1)) {
            if(!Object.hasOwn(target,part)||!target[part]||
                typeof target[part]!=='object'||Array.isArray(target[part]))
                throw Error('Design parameter path changed: '+key);
            target=target[part];
        }
        if(!Object.hasOwn(target,segments.at(-1)))
            throw Error('Design parameter missing: '+key);
        target[segments.at(-1)]=value;
    }
    return result;
}
function described(config,project) {
    return {version:1,fields:fieldsFor(config,project)};
}
// An authenticated worker describes locally installed, approved defaults.
// Reject malformed descriptions before they become UI or placement evidence.
function validField(field) {
    if(!field||typeof field!=='object'||Array.isArray(field)||
        typeof field.key!=='string'||!/^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){0,6}$/i.test(field.key)||
        typeof field.label!=='string'||field.label.length>100||
        !['training','environment'].includes(field.section)||
        !['number','integer','boolean'].includes(field.type))
        return false;
    if(field.type==='boolean')return typeof field.default==='boolean';
    return typeof field.default==='number'&&Number.isFinite(field.default)&&
        typeof field.min==='number'&&Number.isFinite(field.min)&&
        typeof field.max==='number'&&Number.isFinite(field.max)&&
        field.min<=field.default&&field.default<=field.max&&
        field.min<=field.max&&
        (field.type!=='integer'||[field.min,field.max,field.default]
            .every(Number.isSafeInteger));
}
function validateCatalog(raw,installed=[],approved={}) {
    if(raw==null)return {};
    if(!raw||typeof raw!=='object'||Array.isArray(raw)||
        Object.keys(raw).some(key=>!['courier','tinybrains'].includes(key))||
        JSON.stringify(raw).length>32768)
        throw Error('Invalid bounded project Design catalogue.');
    const validateFields=entry=>{
        if(!entry||entry.version!==1||!Array.isArray(entry.fields)||
            entry.fields.length>128||entry.fields.some(field=>!validField(field))||
            new Set(entry.fields.map(field=>field.key)).size!==entry.fields.length)
            throw Error('Invalid project Design field definitions.');
        return {version:1,fields:entry.fields.map(field=>({...field}))};
    };
    const result={};
    if(raw.courier!==undefined) {
        if(!installed.includes('courier')||!/^[0-9a-f]{64}$/.test(raw.courier.config_sha256))
            throw Error('Courier Design defaults require the installed adapter digest.');
        result.courier={...validateFields(raw.courier),config_sha256:raw.courier.config_sha256};
    }
    if(raw.tinybrains!==undefined) {
        const profiles=raw.tinybrains?.profiles;
        if(!installed.includes('tinybrains')||!profiles||
            typeof profiles!=='object'||Array.isArray(profiles)||
            Object.keys(profiles).length>32||
            Object.entries(profiles).some(([id,entry])=>!Object.hasOwn(approved,id)||
                entry?.profile_sha256!==approved[id]))
            throw Error('TinyBrains Design defaults require approved profile identities.');
        if(!/^[0-9a-f]{64}$/.test(raw.tinybrains.source_sha256))
            throw Error('TinyBrains Design requires its frozen source run specification.');
        result.tinybrains={...validateFields(raw.tinybrains),
            source_sha256:raw.tinybrains.source_sha256,
            profiles:Object.fromEntries(Object.entries(profiles).map(([id,entry])=>
                [id,{profile_sha256:entry.profile_sha256}]))};
    }
    return result;
}
function supportsDesign(catalog,project,parameters) {
    const changes=parameters?.design_overrides;
    if(changes===undefined)return true;
    if(!changes||typeof changes!=='object'||Array.isArray(changes)||
        Object.keys(changes).length>128)return false;
    const entry=project==='courier'&&catalog?.courier?.config_sha256===parameters.config_sha256
        ?catalog.courier:project==='tinybrains'
            ?catalog?.tinybrains?.profiles?.[parameters.profile]:null;
    if(!entry||project==='tinybrains'&&
        (entry.profile_sha256!==parameters.profile_sha256||
            catalog.tinybrains.source_sha256!==parameters.design_source_sha256))
        return false;
    const known=new Map((entry.fields||catalog?.tinybrains?.fields||[]).map(field=>[field.key,field]));
    return Object.entries(changes).every(([key,value])=>{
        const field=known.get(key);
        return field&&(field.type==='boolean'?typeof value==='boolean':
            typeof value==='number'&&Number.isFinite(value)&&
            value>=field.min&&value<=field.max&&
            (field.type!=='integer'||Number.isSafeInteger(value)));
    });
}
module.exports={fieldsFor,applyOverrides,described,validateCatalog,supportsDesign};
