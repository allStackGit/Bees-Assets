'use strict';

// Immutable TinyBrains mixed-evaluation suite authoring. The local service
// owns private catalog state; Hub jobs own their separately frozen suite IDs.
// Browser input supplies names, IDs and numbers only, never paths or commands.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');

const HEX=/^[a-f0-9]{20}$/;
const UUID=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const SHA=/^[a-f0-9]{64}$/;
const MIX=/^tinybrains_mix_([a-f0-9]{20})_v([1-9][0-9]*)$/;
const HISTORY=/^tinybrains_mini_[a-f0-9]{20}$/;
const FROZEN=/^tinybrains_frozen_[a-f0-9]{20}$/;
const CHECKPOINT_NAME=/^(?:latest|update-[0-9]{6,8})\\.pt$/;
const STAGES=new Set(['basic_tiny','tiny_two_player','small_two_player',
    'all_two_player','full_multiplayer']);
const MODES=new Set(['configuration','all_77','season_76','custom']);
const CATALOG='mixed-evaluations-catalog.json';

function label(value,what='Name') {
    if(typeof value!=='string'||!value.trim()||value.length>80||
       /[\x00-\x1f\x7f]/.test(value))throw Error(what+' must be 1–80 printable characters.');
    return value.trim();
}
function privateFile(file) {
    if(!fs.existsSync(file))return null;
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink()||
        fs.realpathSync(file)!==file||stat.uid!==process.getuid()||
        (stat.mode&0o077)!==0||stat.size>1048576)
        throw Error('Evaluator catalog file is unsafe or too large.');
    return file;
}
function read(o) {
    const file=privateFile(path.join(o.suites,CATALOG));
    if(!file)return {version:1,templates:[],opponent_labels:{},
        opponent_models:[]};
    const data=JSON.parse(fs.readFileSync(file,'utf8'));
    if(!data||data.version!==1||!Array.isArray(data.templates)||
       data.templates.length>100||!data.opponent_labels||
       typeof data.opponent_labels!=='object'||Array.isArray(data.opponent_labels)||
       Object.keys(data.opponent_labels).length>40||
       Object.entries(data.opponent_labels).some(([id,name])=>
           !HISTORY.test(id)||typeof name!=='string'||name.length>80))
        throw Error('Invalid saved mixed-evaluation library.');
    const models=data.opponent_models??[];
    if(!Array.isArray(models)||models.length>40||
        new Set(models.map(item=>item?.id)).size!==models.length||
        models.some(item=>!item||typeof item!=='object'||
            Object.keys(item).sort().join(',')!==
                'checkpoint_sha256,id,label,manifest_sha256,model_sha256,ppo_updates,runtime_sha256,source_file,source_job_id'||
            !FROZEN.test(item.id)||!SHA.test(item.checkpoint_sha256)||
            item.id!=='tinybrains_frozen_'+item.checkpoint_sha256.slice(0,20)||
            !SHA.test(item.model_sha256)||!SHA.test(item.manifest_sha256)||
            !SHA.test(item.runtime_sha256)||!UUID.test(item.source_job_id)||
            !CHECKPOINT_NAME.test(item.source_file)||
            !Number.isSafeInteger(item.ppo_updates)||item.ppo_updates<1||
            typeof item.label!=='string'||!item.label.trim()||
            item.label.length>80||/[\\x00-\\x1f\\x7f]/.test(item.label)))
        throw Error('Invalid saved frozen-opponent library.');
    return {...data,opponent_models:models};
}
function saveCatalog(o,data) {
    const file=path.join(o.suites,CATALOG);
    privateFile(file);
    const raw=JSON.stringify(data,null,2)+'\n';
    if(Buffer.byteLength(raw)>1048576)
        throw Error('Mixed-evaluation library exceeds 1 MiB.');
    const pending=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    const fd=fs.openSync(pending,fs.constants.O_WRONLY|fs.constants.O_CREAT|
        fs.constants.O_EXCL,0o600);
    try {
        fs.writeFileSync(fd,raw,'utf8');
        fs.fsyncSync(fd);
    }finally{fs.closeSync(fd);}
    try{fs.renameSync(pending,file);}
    finally{if(fs.existsSync(pending))fs.unlinkSync(pending);}
}
function shaFile(file) {
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink()||fs.realpathSync(file)!==file)
        throw Error('Approved opponent file is redirected.');
    return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}
function lookupSuite(o,id) {
    if(!/^[a-z][a-z0-9_]{0,63}$/.test(id))throw Error('Unsafe suite identity.');
    const file=privateFile(path.join(o.suites,id+'.json'));
    if(!file)return null;
    const suite=JSON.parse(fs.readFileSync(file,'utf8'));
    return suite.suite_id===id?suite:null;
}
function availableOpponents(o) {
    const catalog=read(o),labels=catalog.opponent_labels;
    const list=[];
    let approvedVersions={};
    const marker=privateFile(path.join(o.suites,'hub-approved-latest.json'));
    if(marker) {
        const approved=JSON.parse(fs.readFileSync(marker,'utf8'));
        if(approved?.version===1&&typeof approved.nano==='string'&&
            typeof approved.micro==='string')
            approvedVersions={nano:approved.nano,micro:approved.micro};
    }
    for(const id of ['nano','micro']) {
        const selected=approvedVersions[id];
        const safeSelected=typeof selected==='string'&&
            /^tinybrains_(?:nano|micro)_tiny_v(?:[3-9]|[1-9][0-9]{1,2})$/.test(selected)&&
            selected.startsWith('tinybrains_'+id+'_');
        const source=lookupSuite(o,'tinybrains_'+id+'_season_v1')||
            (safeSelected?lookupSuite(o,selected):null)||
            lookupSuite(o,'tinybrains_'+id+'_tiny_v3');
        if(!source)continue;
        list.push({
            id,label:id==='nano'?'Nano':'Micro',
            checkpoint_sha256:null,
            model_sha256:source.opponent_model_sha256,
            manifest_sha256:source.opponent_manifest_sha256,
            model_dir:path.join(o.project,'upstream/ants-starter/models',id+'-bc'),
        });
    }
    const entries=fs.readdirSync(o.suites).filter(name=>
        /^(?:tinybrains_mini_batch128_v1|tinybrains_mini_[a-f0-9]{20})\.json$/.test(name));
    for(const file of entries) {
        const id=file.slice(0,-5);
        const suite=lookupSuite(o,id);
        if(!suite||suite.opponent!=='mini')continue;
        const labelValue=labels[id]||
            (id==='tinybrains_mini_batch128_v1'
                ?'Frozen Mini (Batch 128)'
                :'Frozen checkpoint '+String(suite.opponent_source_job||'').slice(0,12));
        list.push({id,label:labelValue,
            checkpoint_sha256:suite.opponent_checkpoint_sha256,
            source_job_id:suite.opponent_source_job,
            model_sha256:suite.opponent_model_sha256,
            manifest_sha256:suite.opponent_manifest_sha256,
            model_dir:path.join(o.suites,'opponents',id)});
    }
    for(const model of catalog.opponent_models) {
        list.push({id:model.id,label:model.label,
            checkpoint_sha256:model.checkpoint_sha256,
            source_job_id:model.source_job_id,
            model_sha256:model.model_sha256,
            manifest_sha256:model.manifest_sha256,
            model_dir:path.join(o.suites,'opponents',model.id)});
    }
    return list.filter(item=>{
        try {
            return SHA.test(String(item.model_sha256||''))&&
                SHA.test(String(item.manifest_sha256||''))&&
                shaFile(path.join(item.model_dir,'model.onnx'))===item.model_sha256&&
                shaFile(path.join(item.model_dir,'manifest.json'))===item.manifest_sha256;
        }catch{return false;}
    }).sort((a,b)=>a.label.localeCompare(b.label));
}
function candidates(hub) {
    return hub.snapshot().configurations.filter(c=>c.project_id==='tinybrains')
        .map(c=>{
            // Explain the actual saved training environment without treating
            // concurrency/transport settings as match gameplay rules.
            let environment=null;
            try { environment=sourceDefaults(hub,c); }
            catch { /* An offline/older worker may not advertise defaults. */ }
            return {id:c.id,revision:c.revision,name:c.name,
                stage:c.parameters.training_stage||'full_multiplayer',
                ...(environment?{environment}:{}),
            };
        });
}
function boards(o) {
    const root=path.join(o.project,'maps/hive-minds/boards');
    if(fs.realpathSync(root)!==root)
        throw Error('Installed competitive maps directory was redirected.');
    const mapIds=fs.readdirSync(root).filter(name=>name.endsWith('.json'))
        .map(name=>name.slice(0,-5)).sort();
    if(mapIds.length!==76||new Set(mapIds).size!==76)
        throw Error('Expected exactly 76 installed competitive maps.');
    const counts={2:0,4:0,6:0};
    for(const id of mapIds) {
        if(!/^[a-z0-9_-]{1,80}$/.test(id))
            throw Error('Invalid approved board name.');
        const file=path.join(root,id+'.json');
        const data=JSON.parse(fs.readFileSync(file,'utf8'));
        if(data.id!==id||![2,4,6].includes(data.players))
            throw Error('Installed board identity/player count differs.');
        counts[data.players]++;
    }
    if(counts[2]!==30||counts[4]!==34||counts[6]!==12)
        throw Error('Installed season maps have changed.');
    return mapIds;
}
function mapsForStage(o,stage,mode,selectedMaps,configuredMapIds=undefined) {
    const all=['basic-tiny-2p',...boards(o)];
    if(mode==='season_76')return all.slice(1);
    if(mode==='all_77')return all;
    if(mode==='custom') {
        if(!Array.isArray(selectedMaps)||!selectedMaps.length||selectedMaps.length>77||
            selectedMaps.some(id=>typeof id!=='string'||!all.includes(id))||
            new Set(selectedMaps).size!==selectedMaps.length)
            throw Error('Choose 1–77 distinct installed evaluation maps.');
        return all.filter(id=>selectedMaps.includes(id));
    }
    const two=id=>id==='basic-tiny-2p'||/-2p(?:-|$)/.test(id);
    const tiny=id=>id==='basic-tiny-2p'||/^tiny(?:wide)?-/.test(id);
    const small=id=>tiny(id)||/^small(?:wide)?-/.test(id);
    const filters={basic_tiny:id=>id==='basic-tiny-2p',
        tiny_two_player:id=>two(id)&&tiny(id),
        small_two_player:id=>two(id)&&small(id),
        all_two_player:two,full_multiplayer:()=>true};
    if(!STAGES.has(stage))throw Error('Unknown configured TinyBrains map stage.');
    if(mode==='configuration'&&configuredMapIds!==undefined) {
        if(!Array.isArray(configuredMapIds)||configuredMapIds.length<1||
            configuredMapIds.length>all.length||
            new Set(configuredMapIds).size!==configuredMapIds.length||
            configuredMapIds.some(id=>!all.includes(id)||!filters[stage](id)))
            throw Error('Saved training Design contains an invalid custom map selection.');
        return all.filter(id=>configuredMapIds.includes(id));
    }
    return all.filter(filters[stage]);
}
function sourceDefaults(hub,config) {
    const p=config.parameters;
    const parameterSha=crypto.createHash('sha256')
        .update(JSON.stringify(p)).digest('hex');
    const frozen=config.environment_snapshot;
    const validSnapshot=frozen?.parameters_sha256===parameterSha&&
        frozen?.profile_sha256===p.profile_sha256&&
        SHA.test(String(frozen.source_sha256||''))&&
        frozen.parameters&&typeof frozen.parameters==='object'&&
        Number.isSafeInteger(frozen.parameters.max_turns)&&
        frozen.parameters.max_turns>=1&&frozen.parameters.max_turns<=1000;
    let parameters;
    if(validSnapshot) {
        // Saved Design values remain available without an agent heartbeat.
        parameters={...frozen.parameters};
    }else{
        const host=hub.snapshot().machines.find(m=>m.status==='connected'&&
            m.design_catalog?.tinybrains?.profiles?.[p.profile]?.profile_sha256===
                p.profile_sha256);
        if(!host||!Array.isArray(host.design_catalog.tinybrains.fields))
            throw Error('This older Design has no frozen environment defaults. Reconnect the approved TinyBrains worker and re-save the Design once.');
        if(p.design_source_sha256&&
            p.design_source_sha256!==host.design_catalog.tinybrains.source_sha256)
            throw Error('Connected TinyBrains environment schema differs from the Design source.');
        const fields=host.design_catalog.tinybrains.fields
            .filter(item=>item.key.startsWith('environment.')&&
                item.section==='environment'&&item.default!==undefined);
        parameters=Object.fromEntries(fields.map(field=>[
            field.key.slice('environment.'.length),
            p.design_overrides?.[field.key]??field.default,
        ]));
    }
    const sourceTurn=parameters.max_turns;
    if(!Number.isSafeInteger(sourceTurn)||sourceTurn<1||sourceTurn>1000)
        throw Error('An approved environment.max_turns default is required.');
    const cap={basic_tiny:300,tiny_two_player:300,small_two_player:500,
        all_two_player:750,full_multiplayer:sourceTurn}[p.training_stage||'full_multiplayer'];
    if(!cap)throw Error('Saved configuration has an unknown training map stage.');
    return {stage:p.training_stage||'full_multiplayer',
        max_turns:Math.min(sourceTurn,cap),parameters,
        ...(Array.isArray(p.training_map_ids)?{training_map_ids:p.training_map_ids}:{}),
    };
}
function saveEvaluation(o,hub,value) {
    if(!value||typeof value!=='object'||Array.isArray(value)||
        Object.keys(value).some(key=>!['action','confirm_action','id',
            'expected_revision','name','configuration_id','configuration_revision',
            'map_mode','map_ids','max_turns','seeds','rotations','opponent_ids'].includes(key)))
        throw Error('Unexpected custom evaluation fields.');
    const name=label(value.name,'Evaluation name');
    if(!UUID.test(String(value.configuration_id||''))||
        !Number.isSafeInteger(value.configuration_revision)||
        value.configuration_revision<1||!MODES.has(value.map_mode)||
        !Array.isArray(value.seeds)||value.seeds.length<1||value.seeds.length>16||
        value.seeds.some(n=>!Number.isSafeInteger(n)||n<0||n>=2**32)||
        new Set(value.seeds).size!==value.seeds.length||
        !Number.isSafeInteger(value.rotations)||value.rotations<1||value.rotations>8||
        !Array.isArray(value.opponent_ids)||value.opponent_ids.length<1||
        value.opponent_ids.length>16||
        new Set(value.opponent_ids).size!==value.opponent_ids.length)
        throw Error('Invalid saved evaluation configuration, map sampling or opponent set.');
    const config=hub.snapshot().configurations.find(c=>
        c.id===value.configuration_id&&c.revision===value.configuration_revision&&
        c.project_id==='tinybrains');
    if(!config)throw Error('Selected saved TinyBrains Design revision is not available.');
    const environment=sourceDefaults(hub,config);
    const turns=value.max_turns??environment.max_turns;
    if(!Number.isSafeInteger(turns)||turns<1||turns>1000)
        throw Error('Evaluation max turns must be a whole number from 1 to 1000.');
    const maps=mapsForStage(o,environment.stage,value.map_mode,value.map_ids,
        environment.training_map_ids);
    if(!maps.length)
        throw Error('The selected training environment contains no approved maps.');
    const catalog=read(o),available=new Map(
        availableOpponents(o).map(item=>[item.id,item]));
    const pool=value.opponent_ids.map(id=>{
        const item=available.get(id);
        if(!item)throw Error('Selected evaluation opponent is not a verified frozen model: '+String(id));
        const {source_job_id,...frozen}=item;
        return {...frozen,name:undefined,
            label:frozen.label};
    }).map(item=>({
        id:item.id,label:item.label,model_dir:item.model_dir,
        model_sha256:item.model_sha256,
        manifest_sha256:item.manifest_sha256,
        checkpoint_sha256:item.checkpoint_sha256,
    }));
    const allBoards=Object.fromEntries(maps.map(id=>{
        const file=path.join(o.project,'maps/hive-minds/boards',id+'.json');
        const data=id==='basic-tiny-2p'?{players:2}:
            JSON.parse(fs.readFileSync(file,'utf8'));
        return [id,data.players];
    }));
    const games=Object.values(allBoards).reduce((a,n)=>a+n,0)*
        value.seeds.length*value.rotations;
    if(games>2048)
        throw Error('Evaluation would exceed 2048 matches. Reduce seeds or rotations.');
    const current=value.id==null?null:catalog.templates.find(t=>t.id===value.id);
    if(value.id!=null&&(!HEX.test(String(value.id))||!current||
        value.expected_revision!==current.revision))
        throw Error('Evaluation revision changed. Reload before editing.');
    if(catalog.templates.length>=100&&!current)
        throw Error('Saved evaluation library is full.');
    // The installed independent evaluator has a bounded service namespace.
    // Refuse a new immutable revision BEFORE publishing it when that
    // namespace is full, rather than writing a suite which later makes
    // every evaluator's setup/status fail.
    const currentSuites=fs.readdirSync(o.suites).filter(file=>
        file.endsWith('.json')&&MIX.test(file.slice(0,-5)));
    if(currentSuites.length>=20)
        throw Error('The current evaluator service limit (20 mixed suite revisions) is reached. Existing evaluations remain available; archive old revisions before adding more.');
    const id=current?current.id:crypto.randomBytes(10).toString('hex');
    const revision=current?current.revision+1:1;
    const suiteId='tinybrains_mix_'+id+'_v'+revision;
    const configurationSha=crypto.createHash('sha256')
        .update(JSON.stringify(config.parameters)).digest('hex');
    const suite={
        schema_version:2,suite_id:suiteId,
        maps,seeds:value.seeds,max_turns:turns,rotations:value.rotations,
        environment_source:{configuration_id:config.id,revision:config.revision,
            sha256:configurationSha,stage:environment.stage},
        // Full visible source-environment snapshot for reproducibility.
        // Maps/max_turns are the match rules above. Other native engine
        // fields are training-worker settings, not CLI match overrides.
        environment_parameters:environment.parameters,
        opponents:pool,
    };
    const raw=JSON.stringify(suite,null,2)+'\n';
    if(Buffer.byteLength(raw)>32768)
        throw Error('Mixed evaluation definition exceeds 32 KiB.');
    const file=path.join(o.suites,suiteId+'.json');
    // First publish immutable suite bytes; a failed catalog update leaves
    // an unreferenced immutable file, never a partly rewritten suite.
    const descriptor=fs.openSync(file,fs.constants.O_WRONLY|
        fs.constants.O_CREAT|fs.constants.O_EXCL,0o600);
    try {
        fs.writeFileSync(descriptor,raw,'utf8');
        fs.fsyncSync(descriptor);
    }finally{fs.closeSync(descriptor);}
    const record={id,revision,name,suite_id:suiteId,
        configuration_id:config.id,configuration_revision:config.revision,
        map_mode:value.map_mode,
        ...(value.map_mode==='custom'?{map_ids:maps}:{}),
        max_turns:turns,seeds:value.seeds,
        rotations:value.rotations,opponent_ids:value.opponent_ids,
        games,created_at:current?.created_at||new Date().toISOString(),
        updated_at:new Date().toISOString()};
    if(current)catalog.templates.splice(catalog.templates.indexOf(current),1,record);
    else catalog.templates.push(record);
    saveCatalog(o,catalog);
    return {record,message:'Saved immutable mixed evaluation '+name+
        ' revision '+revision+' ('+games+' matches). Install its evaluator service and restart the idle local agent before selecting the suite in Design.'};
}
function deleteEvaluation(o,value) {
    const catalog=read(o);
    if(!HEX.test(String(value.id||'')))throw Error('Invalid saved evaluation ID.');
    const current=catalog.templates.find(row=>row.id===value.id);
    if(!current||current.revision!==value.expected_revision)
        throw Error('Saved evaluation revision changed; reload before deleting.');
    catalog.templates.splice(catalog.templates.indexOf(current),1);
    saveCatalog(o,catalog);
    return {message:'Saved evaluation removed from the editable library. Immutable versions, running jobs and historical receipts remain unchanged.'};
}
function nameOpponent(o,sourceId,name) {
    if(!UUID.test(String(sourceId||'')))throw Error('Select a verified completed checkpoint.');
    const id='tinybrains_mini_'+sourceId.replace(/-/g,'').slice(0,20);
    if(!lookupSuite(o,id))throw Error('The selected checkpoint has not been frozen.');
    const catalog=read(o);
    catalog.opponent_labels[id]=label(name,'Opponent name');
    saveCatalog(o,catalog);
    return {message:'Frozen opponent saved as '+catalog.opponent_labels[id]+'. Existing checkpoints and evaluations remain unchanged.'};
}
// An archived version must not be in the editable library, any saved Design
// evaluation policy, a project default, or any non-cancelled job. This
// inventory is shared by status, cleanup and update/delete admission.
function referencedSuites(hub) {
    const state=hub.snapshot();
    const ids=new Set();
    const add=policy=>{
        for(const rule of policy?.all_of||[policy])
            if(typeof rule?.suite_id==='string')ids.add(rule.suite_id);
    };
    for(const project of state.projects||[]) {
        add(project.default_evaluation_monitor);
        add(project.default_evaluation_stop);
    }
    for(const configuration of state.configurations||[]) {
        add(configuration.evaluation_monitor);
        add(configuration.evaluation_stop);
    }
    for(const job of state.jobs||[])if(job.status!=='cancelled') {
        add(job.evaluation_monitor);
        add(job.evaluation_stop);
    }
    return ids;
}
function unusedVersions(o,hub) {
    const active=new Set(read(o).templates.map(item=>item.suite_id));
    const referenced=referencedSuites(hub);
    return fs.readdirSync(o.suites).filter(file=>file.endsWith('.json')&&
        MIX.test(file.slice(0,-5))&&
        !active.has(file.slice(0,-5))&&!referenced.has(file.slice(0,-5)))
        .map(file=>file.slice(0,-5)).sort();
}
// Register only the locally exported, hash-verified model bytes; HTTP
// requests may choose a finished job and a label, never model paths.
function registerFrozenOpponent(o,record) {
    if(!record||typeof record!=='object'||!FROZEN.test(record.id)||
        !SHA.test(record.checkpoint_sha256)||
        record.id!=='tinybrains_frozen_'+record.checkpoint_sha256.slice(0,20)||
        !SHA.test(record.model_sha256)||!SHA.test(record.manifest_sha256)||
        !SHA.test(record.runtime_sha256)||!UUID.test(record.source_job_id)||
        !CHECKPOINT_NAME.test(record.source_file)||
        !Number.isSafeInteger(record.ppo_updates)||record.ppo_updates<1)
        throw Error('Invalid frozen checkpoint opponent identity.');
    const frozen={...record,label:label(record.label,'Opponent name')};
    const dir=path.join(o.suites,'opponents',record.id);
    if(shaFile(path.join(dir,'model.onnx'))!==record.model_sha256||
        shaFile(path.join(dir,'manifest.json'))!==record.manifest_sha256)
        throw Error('Saved frozen opponent weights differ from their immutable hashes.');
    const catalog=read(o);
    const old=catalog.opponent_models.find(item=>item.id===record.id);
    if(old) {
        for(const key of ['checkpoint_sha256','model_sha256','manifest_sha256',
            'runtime_sha256','source_file','source_job_id','ppo_updates'])
            if(old[key]!==record[key])
                throw Error('The same frozen opponent identity has different original evidence.');
        old.label=frozen.label;
    }else{
        if(catalog.opponent_models.length>=40)
            throw Error('The frozen opponent library is full (40 models).');
        catalog.opponent_models.push(frozen);
    }
    saveCatalog(o,catalog);
    return {id:frozen.id,label:frozen.label,message:
        'Saved '+frozen.label+' as a verified reusable frozen opponent. No evaluation service or training job was started.'};
}
function customSuites(o) {
    const files=fs.existsSync(o.suites)?fs.readdirSync(o.suites):[];
    const names=files.filter(file=>MIX.test(file.slice(0,-5))&&file.endsWith('.json'));
    // Leave room in the Hub's 64-suite/service cap for existing evaluator
    // families. One damaged optional mixed file never disables Nano/Micro.
    if(names.length>20)throw Error('Mixed evaluation archive has reached 20 revisions.');
    return Object.fromEntries(names.flatMap(file=>{
        const name=file.slice(0,-5),match=MIX.exec(name);
        try {
            const source=privateFile(path.join(o.suites,file));
            if(!source||fs.statSync(source).size>32768)
                throw Error('Suite file is missing or oversized.');
            const doc=JSON.parse(fs.readFileSync(source,'utf8'));
            if(doc.schema_version!==2||doc.suite_id!==name||
                !Array.isArray(doc.maps)||!doc.maps.length||
                !Array.isArray(doc.opponents)||!doc.opponents.length)
                throw Error('Incomplete immutable mixed suite.');
            return [['mix-'+match[1]+'-v'+match[2],name]];
        }catch(error) {
            console.warn('[hub-evaluations] Ignoring invalid optional mixed suite '+name+
                ': '+String(error.message||error).slice(0,150));
            return [];
        }
    }));
}
function library(o,hub) {
    return {templates:read(o).templates,
        opponents:availableOpponents(o).map(({id,label,source_job_id,checkpoint_sha256})=>
            ({id,label,source_job_id,checkpoint_sha256})),
        configurations:candidates(hub),
        archivable_count:unusedVersions(o,hub).length,
        maps:['basic-tiny-2p',...boards(o)]};
}
module.exports={read,library,customSuites,saveEvaluation,deleteEvaluation,nameOpponent,
    registerFrozenOpponent,referencedSuites,unusedVersions};
