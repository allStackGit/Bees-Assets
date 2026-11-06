'use strict';

// Windows CPU adapter for the TinyBrains Ants-MAPOCA trainer.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {evaluationReader}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROFILE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA256=/^[0-9a-f]{64}$/;
const MAX_PROFILES=32;
const TAIL_BYTES=256*1024;

function tinyBrainsProfiles(value) {
    if(!value||typeof value!=='object'||Array.isArray(value)||
        Object.keys(value).length>MAX_PROFILES||
        Object.entries(value).some(([id,sha])=>!PROFILE.test(id)||
            typeof sha!=='string'||!SHA256.test(sha)))
        throw Error('Invalid TinyBrains worker profile advertisement.');
    return Object.fromEntries(Object.entries(value));
}

function supportsTinyBrainsProfile(machine,job) {
    if(job.project_id!=='tinybrains')return true;
    const p=job.frozen_parameters;
    const profiles=machine.tinybrains_profiles;
    return machine.agent_platform==='win32'&&job.resources?.gpu_count===0&&
        p&&Object.keys(p).every(key=>['profile','profile_sha256','updates'].includes(key))&&
        Number.isSafeInteger(p.updates)&&p.updates>=1&&p.updates<=10000000&&
        typeof p.profile==='string'&&profiles&&Object.hasOwn(profiles,p.profile)&&
        profiles[p.profile]===p.profile_sha256;
}

// The Windows owner starts workloads without a console or inherited streams.
const TRAIN_ENTRY=[
    'import os, runpy, sys',
    "sys.stdout = open(os.devnull, 'w', encoding='utf-8')",
    "sys.stderr = open(sys.argv.pop(1), 'x', encoding='utf-8', buffering=1)",
    "runpy.run_module('ants_mapoca.cli', run_name='__main__', alter_sys=True)",
].join('; ');

function isWithin(parent,child) {
    const relative=path.relative(parent,child);
    return !path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep);
}

function validateApprovedProfiles(executable,project,configs) {
    const input=JSON.stringify([...configs].map(([id,item])=>[id,item.content]));
    if(Buffer.byteLength(input)>2*1024*1024)
        throw Error('TinyBrains profile definitions exceed the worker size limit.');
    const script=[
        'import json, sys',
        'from ants_mapoca.config import validate',
        'for profile, content in json.load(sys.stdin):',
        '    try:',
        '        validate(json.loads(content))',
        '    except (ValueError, KeyError, TypeError, AttributeError) as exc:',
        '        print(f"{profile}: {exc}")',
        '        sys.exit(1)',
    ].join('\n');
    const result=spawnSync(executable,['-B','-X','utf8','-c',script],{
        cwd:project,input,encoding:'utf8',windowsHide:true,timeout:15000,maxBuffer:8192,
    });
    if(result.status!==0) {
        const detail=(result.error?.message||result.stdout?.trim()||
            result.stderr?.trim().split(/\r?\n/).at(-1)||'validator did not complete').slice(0,180);
        throw Error('TinyBrains profile validation failed: '+detail);
    }
}

function createTinyBrainsAdapter({projectRoot,pythonPath,artifactRoot,profiles,
    evaluationRoot,evaluationSuites}) {
    for(const target of [projectRoot,pythonPath,artifactRoot]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('TinyBrains requires absolute local project, Python and artifact paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    const executable=fs.realpathSync(pythonPath);
    if(!fs.statSync(project).isDirectory()||!fs.statSync(artifacts).isDirectory()||
        !fs.statSync(executable).isFile()||
        !fs.statSync(path.join(project,'tools','tinybrains-0.5.0','tinybrains.exe')).isFile()||
        isWithin(project,artifacts)||isWithin(artifacts,project))
        throw Error('TinyBrains needs the Windows engine and a dedicated artifact directory outside the project.');
    if(!profiles||typeof profiles!=='object'||Array.isArray(profiles)||!Object.keys(profiles).length||
        Object.keys(profiles).length>MAX_PROFILES)
        throw Error('Configure at least one locally approved TinyBrains CPU profile.');
    const configs=new Map(Object.entries(profiles).map(([id,config])=>{
        if(!PROFILE.test(id)||config?.runtime?.device!=='cpu'||
            !Number.isSafeInteger(config.runtime.threads)||config.runtime.threads<1)
            throw Error('TinyBrains profiles require a valid ID and explicit CPU runtime.');
        const content=JSON.stringify(config,null,2)+'\n';
        const sha256=crypto.createHash('sha256').update(content).digest('hex');
        return [id,{content,sha256}];
    }));

    validateApprovedProfiles(executable,project,configs);

    function jobRoot(context) {
        if(context.project_id!=='tinybrains'||!UUID.test(context.job_id)||
            !UUID.test(context.artifact_lineage_root_id)||
            (context.continuation_of_job_id
                ?!UUID.test(context.continuation_of_job_id)||
                    context.continuation_of_job_id===context.job_id||
                    context.artifact_lineage_root_id===context.job_id
                :context.artifact_lineage_root_id!==context.job_id)||
            context.gpu_pci_address)
            throw Error('Invalid TinyBrains CPU job or checkpoint lineage.');
        return path.join(artifacts,context.job_id);
    }

    function completedCheckpoint(source,profileContent) {
        const checkpoint=path.join(source,'latest.pt');
        const profile=path.join(source,'hub-profile.json');
        if([checkpoint,profile].some(file=>{
            if(fs.realpathSync(file)!==file)return true;
            const stat=fs.statSync(file);
            return !stat.isFile()||stat.size<1;
        })||fs.readFileSync(profile,'utf8')!==profileContent)
            throw Error('TinyBrains checkpoint or approved profile is missing or invalid.');
        return checkpoint;
    }

    return {
        executable,cwd:project,stop_policy:'terminate-group',
        checkpoint_stop_file:context=>path.join(jobRoot(context),'STOP'),
        checkpoint_ready(context) {
            const pinned=context.frozen_parameters||{};
            const approved=configs.get(pinned.profile);
            if(!approved||approved.sha256!==pinned.profile_sha256)
                throw Error('TinyBrains checkpoint profile is not approved.');
            completedCheckpoint(jobRoot(context),approved.content);
            return true;
        },
        ...evaluationReader('tinybrains',evaluationRoot,evaluationSuites,[project,artifacts]),
        tinybrains_profiles:Object.fromEntries([...configs].map(([id,item])=>[id,item.sha256])),
        args(parameters,context) {
            const {profile,profile_sha256,updates}=parameters;
            const approved=configs.get(profile);
            if(Object.keys(parameters).some(key=>!['profile','profile_sha256','updates'].includes(key))||
                !approved||approved.sha256!==profile_sha256||
                !Number.isSafeInteger(updates)||updates<1||updates>10000000)
                throw Error('TinyBrains jobs require an approved profile, its SHA-256 and 1–10,000,000 updates.');
            const out=jobRoot(context);
            let resume=[];
            if(context.continuation_of_job_id) {
                const parent=path.join(artifacts,context.continuation_of_job_id);
                resume=['--resume',completedCheckpoint(parent,approved.content)];
            }
            // An existing directory may belong to a prior launch. Never reuse it.
            fs.mkdirSync(out,{mode:0o700});
            const config=path.join(out,'hub-profile.json');
            fs.writeFileSync(config,approved.content,{flag:'wx',mode:0o600});
            return ['-B','-c',TRAIN_ENTRY,path.join(out,'trainer.stderr.txt'),
                'train','--config',config,'--out',out,
                '--updates',String(updates),...resume];
        },
        readProgress(context) {
            const out=jobRoot(context);
            const history=path.join(out,'history.jsonl');
            let fd;
            try {
                if(fs.realpathSync(history)!==history)
                    throw Error('TinyBrains progress path is redirected.');
                fd=fs.openSync(history,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||!stat.size)return null;
                const tail=Buffer.alloc(Math.min(stat.size,TAIL_BYTES));
                const offset=stat.size-tail.length;
                const bytes=fs.readSync(fd,tail,0,tail.length,offset);
                const text=tail.toString('utf8',0,bytes);
                const end=text.lastIndexOf('\n');
                if(end<0)return null;
                const start=text.lastIndexOf('\n',end-1);
                if(start<0&&offset>0)return null;
                const row=JSON.parse(text.slice(start+1,end));
                if(!Number.isSafeInteger(row.update)||row.update<1||
                    !Number.isSafeInteger(row.engine_steps)||row.engine_steps<0||
                    !Number.isFinite(row.wall_seconds)||row.wall_seconds<=0)
                    throw Error('Invalid completed TinyBrains training update.');
                return validateProgress({
                    schema_version:1,source:'hub-metrics-v1',experiment_id:context.job_id,
                    segment_id:'training',source_modified_at:stat.mtime.toISOString(),
                    steps_completed:row.update,
                    metrics:{
                        update_wall_time:{value:row.wall_seconds,unit:'seconds'},
                        engine_steps_per_second:{value:row.engine_steps/row.wall_seconds,unit:'rate'},
                    },
                },context.project_id);
            }catch(error) {
                if(error.code==='ENOENT')return null;
                throw error;
            }finally{
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}
module.exports={createTinyBrainsAdapter,tinyBrainsProfiles,supportsTinyBrainsProfile};
