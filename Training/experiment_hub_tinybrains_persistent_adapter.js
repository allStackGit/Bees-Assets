'use strict';

// Locally trusted TinyBrains persistent-PPO adapter for the opt-in Docker owner.
// A Hub job copies only its seed checkpoint, frozen run spec and gate proof;
// frozen opponents remain referenced from an immutable, read-only source run.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROFILE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA=/^[0-9a-f]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const TAIL=256*1024;
const PROJECT_IN='/workspace/projects/tinybrains/Ants-MAPOCA';
const HUB_IN='/workspace/projects/experiment-hub/Assets';
const JOBS_IN='/workspace/experiments/experiment-hub/tinybrains-jobs';
const SOURCE_IN='/workspace/experiments/tinybrains/mini-learning';
const CACHE_IN='/workspace/cache/tinybrains';

function fullDir(name) {
    if(typeof name!=='string'||!path.isAbsolute(name)||
        fs.realpathSync(name)!==name||!fs.statSync(name).isDirectory())
        throw Error('Local TinyBrains path must be an existing non-symlink directory.');
    if(!/^[A-Za-z0-9_./-]+$/.test(name))
        throw Error('Local TinyBrains paths cannot contain whitespace or shell syntax.');
    return name;
}
function runRelative(folder,filename) {
    if(typeof filename!=='string')throw Error('Invalid frozen run relative path.');
    const segments=filename.replace(/\\/g,'/').split('/');
    if(segments.some(part=>!part||part==='.'||part==='..'||part.includes(':')))
        throw Error('Unsafe saved gate checkpoint path.');
    return path.join(folder,...segments);
}
function stageFile(source,target) {
    fs.mkdirSync(path.dirname(target),{recursive:true,mode:0o700});
    fs.copyFileSync(source,target,fs.constants.COPYFILE_EXCL);
}
function createTinyBrainsPersistentDockerAdapter({
    projectRoot,hubRoot,artifactRoot,sourceRunRoot,engineCacheRoot,
    seedCheckpointSha256,profiles,
    image='tinybrains-rocm:halifax',sourceWindowsRoot='B:/TinyBrains/Ants-MAPOCA',
}) {
    if(process.platform!=='linux'||!SHA.test(String(seedCheckpointSha256||'')))
        throw Error('Persistent TinyBrains Docker import requires Linux and a pinned checkpoint hash.');
    const project=fullDir(projectRoot),hub=fullDir(hubRoot),
        artifacts=fullDir(artifactRoot),source=fullDir(sourceRunRoot),
        cache=fullDir(engineCacheRoot);
    if(project===artifacts||project===source||artifacts===source||
        source.startsWith(artifacts+'/')||artifacts.startsWith(source+'/'))
        throw Error('Hub job artifacts and immutable TinyBrains import must not overlap.');
    const engine=path.join(project,'tools/tinybrains-0.5.0/tinybrains');
    const result=spawnSync(engine,['--version'],{
        timeout:5000,encoding:'utf8',windowsHide:true,
    });
    if(result.status!==0||result.stdout.trim()!=='tinybrains 0.5.0')
        throw Error('The Linux TinyBrains 0.5.0 engine is unavailable.');
    const original=path.join(source,'self-play/latest.pt');
    if(modelFileSha256(original)!==seedCheckpointSha256)
        throw Error('Imported Exeter checkpoint differs from operator-pinned SHA-256.');
    for(const name of ['run-spec.json','gate-passed.json']) {
        if(!fs.statSync(path.join(source,name)).isFile())
            throw Error('The saved TinyBrains run is missing '+name);
    }
    const gate=JSON.parse(fs.readFileSync(path.join(source,'gate-passed.json'),'utf8'));
    const candidate=runRelative(source,gate.checkpoint);
    if(modelFileSha256(candidate)!==gate.checkpoint_sha256)
        throw Error('Original gate evidence is missing or has changed.');
    const frozen=fs.readFileSync(path.join(source,'run-spec.json'));
    const evidence=fs.readFileSync(path.join(source,'gate-passed.json'));
    if(!profiles||typeof profiles!=='object'||Array.isArray(profiles)||
        !Object.keys(profiles).length||Object.keys(profiles).length>16)
        throw Error('Specify approved local persistent PPO profiles.');
    const approved=new Map(Object.entries(profiles).map(([id,p])=>{
        if(!PROFILE.test(id)||p?.device!=='cuda'||
            !['threads','live_matches','waves','rollout_actions','device'].every(k=>Object.hasOwn(p,k))||
            Object.keys(p).length!==5||
            [p.threads,p.live_matches,p.waves,p.rollout_actions].some(n=>!Number.isSafeInteger(n)||n<1)||
            p.live_matches%p.waves||p.threads>256||
            p.live_matches>512||p.rollout_actions>1048576)
            throw Error('Invalid approved TinyBrains persistent profile.');
        const text=JSON.stringify(p,null,2)+'\n';
        return [id,{profile:p,text,sha256:crypto.createHash('sha256').update(text).digest('hex')}];
    }));
    if(typeof sourceWindowsRoot!=='string'||
        !/^[A-Za-z]:\/[A-Za-z0-9_./-]+$/.test(sourceWindowsRoot))
        throw Error('Invalid original Windows project path mapping.');
    const originalSpec=JSON.parse(frozen);
    if(originalSpec?.model_config?.training?.algorithm!=='colony_ppo'||
        !Number.isSafeInteger(originalSpec.self_play_updates))
        throw Error('Original run is not the approved persistent colony-PPO lineage.');

    function targetFor(context) {
        if(context.project_id!=='tinybrains'||!UUID.test(context.job_id)||
            !UUID.test(context.artifact_lineage_root_id)||
            (context.continuation_of_job_id&&!UUID.test(context.continuation_of_job_id)))
            throw Error('Invalid Hub TinyBrains lineage identity.');
        return path.join(artifacts,context.job_id);
    }
    function checkpointReady(out,text) {
        const profile=path.join(out,'hub-profile.json');
        const latest=path.join(out,'self-play/latest.pt');
        const stat=fs.statSync(latest);
        if(!stat.isFile()||stat.size<1||
            fs.readFileSync(profile,'utf8')!==text||
            !fs.readFileSync(path.join(out,'run-spec.json')).equals(frozen))
            throw Error('Finished TinyBrains job does not have its original frozen lineage and checkpoint.');
        const status=JSON.parse(fs.readFileSync(path.join(out,'status.json'),'utf8'));
        if(!['stopped','self_play_paused','self_play_completed'].includes(status.phase)||
            !Number.isSafeInteger(status.ppo_updates)||status.ppo_updates<1)
            throw Error('TinyBrains job has no finished checkpoint-boundary status.');
        return latest;
    }
    const docker={
        image,workdir:PROJECT_IN,uid:process.getuid(),gid:process.getgid(),
        mounts:[
            {host:project,target:PROJECT_IN,readonly:true},
            {host:hub,target:HUB_IN,readonly:true},
            {host:artifacts,target:JOBS_IN,readonly:false},
            {host:source,target:SOURCE_IN,readonly:true},
            {host:cache,target:CACHE_IN,readonly:false},
        ],
        env:{
            HOME:'/tmp',PYTHONDONTWRITEBYTECODE:'1',
            TINYBRAINS_SOURCE_ROOT:sourceWindowsRoot,
            TINYBRAINS_RUNS_ROOT:'/workspace/experiments/tinybrains',
            TINYBRAINS_HOME:CACHE_IN,
            TINYBRAINS_REGISTRY:PROJECT_IN+'/games.toml',
            TINYBRAINS:PROJECT_IN+'/tools/tinybrains-0.5.0/tinybrains',
            PYTHONPATH:PROJECT_IN+'/src:'+PROJECT_IN+'/upstream/ants/baselines/src:'+HUB_IN+'/Training',
        },
    };
    const executable='/opt/tinybrains-venv/bin/python';
    const runner=HUB_IN+'/Training/experiment_hub_tinybrains_persistent.py';
    return {
        executable,cwd:PROJECT_IN,docker,stop_policy:'interrupt-main',
        gpu_execution:'cuda',
        tinybrains_profiles:Object.fromEntries([...approved].map(([id,p])=>[id,p.sha256])),
        tinybrains_cuda_profiles:[...approved.keys()],
        tinybrains_imports:Object.fromEntries([...approved.keys()].map(id=>[
            'exeter_'+id,{profile:id,sha256:seedCheckpointSha256},
        ])),
        checkpoint_stop_file:ctx=>path.join(targetFor(ctx),'STOP'),
        gpu_verify_args(parameters,context){
            const item=approved.get(parameters?.profile);
            if(!item||item.sha256!==parameters.profile_sha256||
                !PCI.test(String(context.gpu_pci_address||'')))
                throw Error('Invalid approved TinyBrains ROCm proof.');
            return ['-B',HUB_IN+'/Training/experiment_hub_tinybrains_rocm_verify.py',
                context.gpu_pci_address];
        },
        args(parameters,context) {
            const {profile,profile_sha256,updates,import_checkpoint,
                import_checkpoint_sha256}=parameters;
            const item=approved.get(profile);
            if(!item||item.sha256!==profile_sha256||
                Object.keys(parameters).some(k=>!['profile','profile_sha256','updates',
                    'import_checkpoint','import_checkpoint_sha256'].includes(k))||
                !Number.isSafeInteger(updates)||updates<1||updates>10000000||
                !PCI.test(String(context.gpu_pci_address||'')))
                throw Error('Invalid immutable TinyBrains Hub training request.');
            const parent=context.continuation_of_job_id;
            if(parent?
                import_checkpoint!==undefined||import_checkpoint_sha256!==undefined:
                import_checkpoint!=='exeter_'+profile||
                    import_checkpoint_sha256!==seedCheckpointSha256)
                throw Error('A new run must import the pinned Exeter lineage or explicitly continue its parent.');
            let sourceCheckpoint=original;
            if(parent) {
                const predecessor=path.join(artifacts,parent);
                sourceCheckpoint=checkpointReady(predecessor,item.text);
                if(context.expected_parent_model_sha256&&
                    modelFileSha256(sourceCheckpoint)!==context.expected_parent_model_sha256)
                    throw Error('Continuation checkpoint identity changed.');
            }
            const sourceHash=modelFileSha256(sourceCheckpoint);
            const out=targetFor(context);
            if(fs.existsSync(out)) {
                if(fs.existsSync(path.join(out,'status.json'))||
                    fs.readFileSync(path.join(out,'hub-profile.json'),'utf8')!==item.text||
                    modelFileSha256(path.join(out,'self-play/latest.pt'))!==sourceHash)
                    throw Error('Previously launched or changed Hub training output cannot be re-used.');
            }else{
                fs.mkdirSync(out,{mode:0o700});
                stageFile(sourceCheckpoint,path.join(out,'self-play/latest.pt'));
                stageFile(path.join(source,'run-spec.json'),path.join(out,'run-spec.json'));
                stageFile(path.join(source,'gate-passed.json'),path.join(out,'gate-passed.json'));
                stageFile(candidate,runRelative(out,gate.checkpoint));
                fs.writeFileSync(path.join(out,'hub-profile.json'),item.text,{flag:'wx',mode:0o600});
            }
            return ['-B',runner,'--run',JOBS_IN+'/'+context.job_id,
                '--updates',String(updates),'--threads',String(item.profile.threads),
                '--live-matches',String(item.profile.live_matches),
                '--waves',String(item.profile.waves),
                '--actions',String(item.profile.rollout_actions),
                '--rocm-pci',context.gpu_pci_address];
        },
        checkpoint_ready(context){
            const item=approved.get(context.frozen_parameters?.profile);
            if(!item||item.sha256!==context.frozen_parameters?.profile_sha256)
                throw Error('Unknown approved checkpoint profile.');
            checkpointReady(targetFor(context),item.text);
            return true;
        },
        model_checkpoint_sha256(context){
            const item=approved.get(context.frozen_parameters?.profile);
            if(!item||item.sha256!==context.frozen_parameters?.profile_sha256)
                throw Error('Unknown approved checkpoint profile.');
            return modelFileSha256(checkpointReady(targetFor(context),item.text));
        },
        readProgress(context){
            const file=path.join(targetFor(context),'self-play/history.jsonl');
            let fd;
            try{
                fd=fs.openSync(file,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||!stat.size)return null;
                const offset=Math.max(0,stat.size-TAIL);
                const buffer=Buffer.alloc(stat.size-offset);
                fs.readSync(fd,buffer,0,buffer.length,offset);
                const str=buffer.toString('utf8');
                const end=str.lastIndexOf('\n');
                if(end<0)return null;
                const start=str.lastIndexOf('\n',end-1);
                if(start<0&&offset>0)return null;
                const row=JSON.parse(str.slice(start+1,end));
                if(!Number.isSafeInteger(row.update)||row.update<1||
                    !Number.isFinite(row.wall_seconds)||row.wall_seconds<=0||
                    !Number.isSafeInteger(row.engine_steps)||row.engine_steps<0)
                    throw Error('Invalid TinyBrains completed-update telemetry.');
                return validateProgress({
                    schema_version:1,source:'hub-metrics-v1',experiment_id:context.job_id,
                    segment_id:'training',source_modified_at:stat.mtime.toISOString(),
                    steps_completed:row.update,metrics:{
                        update_wall_time:{value:row.wall_seconds,unit:'seconds'},
                        engine_steps_per_second:{value:row.engine_steps/row.wall_seconds,unit:'rate'},
                    },
                },context.project_id);
            }catch(error){
                if(error.code==='ENOENT')return null;
                throw error;
            }finally{
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}
module.exports={createTinyBrainsPersistentDockerAdapter};
