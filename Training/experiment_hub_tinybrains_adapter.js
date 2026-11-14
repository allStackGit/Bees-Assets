'use strict';

// Native CPU adapter for the TinyBrains Ants-MAPOCA trainer.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {evaluationReader,modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROFILE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA256=/^[0-9a-f]{64}$/;
const CUDA_UUID=/^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
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

function tinyBrainsImports(value) {
    if(!value||typeof value!=='object'||Array.isArray(value)||
        Object.keys(value).length>MAX_PROFILES||
        Object.entries(value).some(([id,pin])=>
            !PROFILE.test(id)||!pin||typeof pin!=='object'||Array.isArray(pin)||
            Object.keys(pin).some(key=>!['profile','sha256'].includes(key))||
            !PROFILE.test(String(pin.profile||''))||
            !SHA256.test(String(pin.sha256||''))))
        throw Error('Invalid TinyBrains trusted checkpoint import advertisement.');
    return Object.fromEntries(Object.entries(value).map(([id,pin])=>[id,{...pin}]));
}

function supportsTinyBrainsProfile(machine,job) {
    if(job.project_id!=='tinybrains')return true;
    const p=job.frozen_parameters;
    const profiles=machine.tinybrains_profiles;
    const cuda=machine.tinybrains_cuda_profiles?.includes(p?.profile)===true;
    const cpuFallback=p?.device==='cpu'&&
        machine.tinybrains_cpu_fallback_profiles?.includes(p.profile)===true&&
        job.resources?.gpu_count===0;
    const dual=p?.device==='dual'&&
        job.resources?.gpu_count===2&&job.resources?.gpu_mode==='exclusive'&&
        machine.agent_platform==='linux'&&
        machine.tinybrains_dual_gpu_profiles?.includes(p.profile)===true;
    const multi=p?.device==='multi'&&
        job.resources?.gpu_count>=3&&job.resources?.gpu_count<=8&&
        job.resources?.gpu_mode==='exclusive'&&
        machine.agent_platform==='linux'&&
        machine.tinybrains_multi_gpu_profiles?.includes(p.profile)===true;
    const gpu=job.resources?.gpu_count===1&&
        (job.resources.gpu_mode==='exclusive'||
            job.resources.gpu_mode==='shared'&&
                machine.gpu_shared_projects?.includes('tinybrains'));
    // The remote rollout protocol collects immutable CPU/GPU shards
    // independently of where the authoritative PPO optimizer runs.
    // A locally approved CPU fallback may own the learner while remote
    // workers independently use their chosen CPU/GPU leases.
    const remote=p?.remote_rollouts===true&&
        machine.agent_platform==='linux'&&
        (cpuFallback||job.resources?.gpu_count>=1)&&
        machine.tinybrains_remote_rollout_profiles?.includes(p.profile)===true;
    const imported=p?.import_checkpoint==null?null:
        machine.tinybrains_imports?.[p.import_checkpoint];
    return ['win32','linux'].includes(machine.agent_platform)&&
        (cuda?(cpuFallback||dual||multi||p?.device===undefined&&gpu):
            p?.device===undefined&&job.resources?.gpu_count===0)&&
        p&&Object.keys(p).every(key=>
            ['profile','profile_sha256','updates','threads','device','remote_rollouts','import_checkpoint',
                'import_checkpoint_sha256','rollout_action_weights'].includes(key))&&
        (p.rollout_action_weights===undefined||
            remote&&Array.isArray(p.rollout_action_weights)&&
            p.rollout_action_weights.length===machine.tinybrains_remote_rollout_count+1&&
            p.rollout_action_weights.every(w=>
                Number.isSafeInteger(w)&&w>=1&&w<=1024))&&
        (p.remote_rollouts===undefined||remote)&&
        (p.threads===undefined||
            Number.isSafeInteger(p.threads)&&p.threads>=1&&p.threads<=256&&
            machine.tinybrains_thread_overrides?.includes(p.profile)===true)&&
        (!machine.tinybrains_required_import_profiles?.includes(p.profile)||
            Boolean(job.continuation_of_job_id)||
            p.import_checkpoint!==undefined)&&
        (p.import_checkpoint===undefined
            ?p.import_checkpoint_sha256===undefined
            :!job.continuation_of_job_id&&
                typeof p.import_checkpoint==='string'&&
                p.import_checkpoint_sha256===imported?.sha256&&
                imported?.profile===p.profile)&&
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
    evaluationRoot,evaluationSuites,enginePath,checkpointImports={},
    rocrGpuUuids={}}) {
    for(const target of [projectRoot,pythonPath,artifactRoot]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('TinyBrains requires absolute local project, Python and artifact paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    const executable=fs.realpathSync(pythonPath);
    const windows=process.platform==='win32';
    // The operator supplies a physical PCI -> ROCr UUID mapping from the
    // installed machine inventory. Never infer a runtime ordinal from a
    // PCI address, and never accept a selector from saved experiment JSON.
    if(!rocrGpuUuids||typeof rocrGpuUuids!=='object'||
        Array.isArray(rocrGpuUuids)||
        Object.keys(rocrGpuUuids).length>32||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid))||
        new Set(Object.values(rocrGpuUuids).map(uuid=>uuid.toLowerCase())).size!==
            Object.keys(rocrGpuUuids).length||
        (windows&&Object.keys(rocrGpuUuids).length))
        throw Error('Invalid or duplicate operator-approved Linux ROCm PCI-to-UUID bindings.');
    const pinnedRocrUuids=Object.freeze({...rocrGpuUuids});
    if((!windows||enginePath!=null)&&(typeof enginePath!=='string'||!path.isAbsolute(enginePath)))
        throw Error('Specify the absolute path to the installed native TinyBrains engine.');
    const engine=fs.realpathSync(enginePath??path.join(project,'tools','tinybrains-0.5.0','tinybrains.exe'));
    if(!fs.statSync(project).isDirectory()||!fs.statSync(artifacts).isDirectory()||
        !fs.statSync(executable).isFile()||
        !fs.statSync(engine).isFile()||
        isWithin(project,artifacts)||isWithin(artifacts,project))
        throw Error('TinyBrains needs a native engine and a dedicated artifact directory outside the project.');
    if(enginePath!=null) {
        const probe=spawnSync(engine,['--version'],{cwd:project,encoding:'utf8',windowsHide:true,timeout:5000,maxBuffer:4096});
        if(probe.status!==0||probe.stdout?.trim()!=='tinybrains 0.5.0')
            throw Error('The installed TinyBrains engine did not report supported version 0.5.0.');
    }
    if(!profiles||typeof profiles!=='object'||Array.isArray(profiles)||!Object.keys(profiles).length||
        Object.keys(profiles).length>MAX_PROFILES)
        throw Error('Configure at least one locally approved TinyBrains profile.');
    const configs=new Map(Object.entries(profiles).map(([id,config])=>{
        if(!PROFILE.test(id)||!['cpu','cuda'].includes(config?.runtime?.device)||
            !Number.isSafeInteger(config.runtime.threads)||config.runtime.threads<1)
            throw Error('TinyBrains profiles require an approved CPU or CUDA device and thread count.');
        const content=JSON.stringify(config,null,2)+'\n';
        const sha256=crypto.createHash('sha256').update(content).digest('hex');
        return [id,{content,sha256,device:config.runtime.device}];
    }));

    // CUDA uses the trusted runtime wrapper even with the bundled Windows
    // engine. Verify that binary at adapter installation time instead of
    // advertising a GPU profile that can only fail after native launch.
    if(windows&&enginePath==null&&[...configs].some(([,item])=>item.device==='cuda')) {
        const probe=spawnSync(engine,['--version'],{
            cwd:project,encoding:'utf8',windowsHide:true,timeout:5000,maxBuffer:4096,
        });
        if(probe.status!==0||probe.stdout?.trim()!=='tinybrains 0.5.0')
            throw Error('Windows CUDA profiles require the installed TinyBrains 0.5.0 engine.');
    }
    validateApprovedProfiles(executable,project,configs);
    if(!checkpointImports||typeof checkpointImports!=='object'||
        Array.isArray(checkpointImports)||Object.keys(checkpointImports).length>32)
        throw Error('Invalid trusted TinyBrains checkpoint import map.');
    // Import locations are installed by the machine operator, not supplied
    // as executable paths by the coordinator. Source checkpoints remain untouched.
    const imports=new Map(Object.entries(checkpointImports).map(([id,entry])=>{
        if(!PROFILE.test(id)||!entry||!PROFILE.test(String(entry.profile||''))||
            !configs.has(entry.profile)||!SHA256.test(String(entry.sha256||''))||
            typeof entry.checkpoint!=='string'||!path.isAbsolute(entry.checkpoint))
            throw Error('Invalid operator-approved TinyBrains checkpoint import.');
        const checkpoint=fs.realpathSync(entry.checkpoint);
        if(!fs.statSync(checkpoint).isFile()||
            isWithin(artifacts,checkpoint)||isWithin(checkpoint,artifacts)||
            modelFileSha256(checkpoint)!==entry.sha256)
            throw Error('Trusted TinyBrains checkpoint import changed or points into Hub artifacts.');
        return [id,{profile:entry.profile,checkpoint,sha256:entry.sha256}];
    }));

    function jobRoot(context) {
        if(context.project_id!=='tinybrains'||!UUID.test(context.job_id)||
            !UUID.test(context.artifact_lineage_root_id)||
            (context.continuation_of_job_id
                ?!UUID.test(context.continuation_of_job_id)||
                    context.continuation_of_job_id===context.job_id||
                    context.artifact_lineage_root_id===context.job_id
                :context.artifact_lineage_root_id!==context.job_id))
            throw Error('Invalid TinyBrains job or checkpoint lineage.');
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
        executable,cwd:project,stop_policy:windows?'terminate-group':'interrupt-main',
        checkpoint_stop_file:context=>path.join(jobRoot(context),'STOP'),
        checkpoint_ready(context) {
            const pinned=context.frozen_parameters||{};
            const approved=configs.get(pinned.profile);
            if(!approved||approved.sha256!==pinned.profile_sha256)
                throw Error('TinyBrains checkpoint profile is not approved.');
            completedCheckpoint(jobRoot(context),approved.content);
            return true;
        },
        model_checkpoint_sha256(context) {
            const pinned=context.frozen_parameters||{};
            const approved=configs.get(pinned.profile);
            if(!approved||approved.sha256!==pinned.profile_sha256)
                throw Error('TinyBrains checkpoint profile is not approved.');
            return modelFileSha256(completedCheckpoint(jobRoot(context),approved.content));
        },
        ...evaluationReader('tinybrains',evaluationRoot,evaluationSuites,[project,artifacts]),
        tinybrains_profiles:Object.fromEntries([...configs].map(([id,item])=>[id,item.sha256])),
        tinybrains_cuda_profiles:[...configs].filter(([,item])=>item.device==='cuda').map(([id])=>id),
        tinybrains_imports:Object.fromEntries([...imports].map(([id,item])=>
            [id,{profile:item.profile,sha256:item.sha256}])),
        gpu_execution:[...configs].some(([,item])=>item.device==='cuda')?'cuda':null,
        ...(windows?{}:{rocr_gpu_uuids:pinnedRocrUuids}),
        gpu_sharing:windows&&[...configs].some(([,item])=>item.device==='cuda')
            ?'cooperative-pytorch':null,
        ...(windows||![...configs].some(([,item])=>item.device==='cuda')?{}:{
            gpu_verify_args(parameters,context) {
                const approved=configs.get(parameters?.profile);
                if(approved?.device!=='cuda'||approved.sha256!==parameters.profile_sha256||
                    !context.gpu_pci_address||context.gpu_cuda_uuid)
                    throw Error('ROCm verification requires a pinned GPU training profile and PCI device.');
                return ['-B',path.join(__dirname,'experiment_hub_tinybrains_rocm_verify.py'),
                    context.gpu_pci_address];
            },
        }),
        args(parameters,context) {
            const {profile,profile_sha256,updates,import_checkpoint,
                import_checkpoint_sha256}=parameters;
            const approved=configs.get(profile);
            if(Object.keys(parameters).some(key=>
                !['profile','profile_sha256','updates','import_checkpoint',
                    'import_checkpoint_sha256'].includes(key))||
                !approved||approved.sha256!==profile_sha256||
                !Number.isSafeInteger(updates)||updates<1||updates>10000000)
                throw Error('TinyBrains jobs require an approved profile, its SHA-256 and 1–10,000,000 updates.');
            if(approved.device==='cuda'
                ?!context.gpu_pci_address||
                    (windows
                        ?!CUDA_UUID.test(String(context.gpu_cuda_uuid||''))
                        :Boolean(context.gpu_cuda_uuid))
                :Boolean(context.gpu_pci_address||context.gpu_cuda_uuid))
                throw Error('TinyBrains CPU/CUDA profile does not match the locally bound native device.');
            if(windows&&approved.device==='cuda'&&context.gpu_mode==='shared'&&
                (!Number.isSafeInteger(context.gpu_vram_mb)||
                    context.gpu_vram_mb<1||
                    !Number.isSafeInteger(context.gpu_total_vram_mb)||
                    context.gpu_vram_mb>context.gpu_total_vram_mb||
                    !Number.isSafeInteger(context.gpu_compute_pct)||
                    context.gpu_compute_pct<1||context.gpu_compute_pct>100))
                throw Error('Shared Windows CUDA requires a pinned positive PyTorch VRAM ceiling.');
            if(import_checkpoint!==undefined
                ?context.continuation_of_job_id||!PROFILE.test(String(import_checkpoint))||
                    imports.get(import_checkpoint)?.profile!==profile||
                    imports.get(import_checkpoint)?.sha256!==import_checkpoint_sha256
                :import_checkpoint_sha256!==undefined)
                throw Error('A new TinyBrains lineage may import only a pinned approved optimizer checkpoint.');
            const out=jobRoot(context);
            let resume=[];
            if(context.continuation_of_job_id) {
                const parent=path.join(artifacts,context.continuation_of_job_id);
                const checkpoint=completedCheckpoint(parent,approved.content);
                if(context.expected_parent_model_sha256&&
                    modelFileSha256(checkpoint)!==context.expected_parent_model_sha256)
                    throw Error('TinyBrains promoted checkpoint no longer matches its evaluated model.');
                resume=['--resume',checkpoint];
            }
            // Validate the external source before creating any new job
            // artifacts. A stale import must not strand an otherwise
            // unlaunched job behind an empty output directory.
            const imported=import_checkpoint===undefined
                ?null:imports.get(import_checkpoint);
            if(imported&&modelFileSha256(imported.checkpoint)!==imported.sha256)
                throw Error('Original TinyBrains checkpoint changed before import.');
            // An existing directory may belong to a prior launch. Never reuse it.
            fs.mkdirSync(out,{mode:0o700});
            if(imported) {
                const dest=path.join(out,'imported-checkpoint.pt');
                fs.copyFileSync(imported.checkpoint,dest,fs.constants.COPYFILE_EXCL);
                if(modelFileSha256(dest)!==imported.sha256)
                    throw Error('Copied TinyBrains optimizer checkpoint did not match the pinned source.');
                resume=['--resume',dest];
            }
            const config=path.join(out,'hub-profile.json');
            fs.writeFileSync(config,approved.content,{flag:'wx',mode:0o600});
            const stderr=windows?path.join(out,'trainer.stderr.txt'):'-';
            const selectedCuda=windows&&approved.device==='cuda'
                ?context.gpu_cuda_uuid:null;
            const selectedRocm=!windows&&approved.device==='cuda'
                ?context.gpu_pci_address:null;
            const entry=enginePath!=null||selectedCuda||selectedRocm
                ?[path.join(__dirname,'experiment_hub_tinybrains_runtime.py'),engine,stderr,
                    ...(selectedCuda?['--hub-cuda',selectedCuda,
                        ...(context.gpu_mode==='shared'
                            ?['--hub-gpu-vram-mb',String(context.gpu_vram_mb)]:[])]:[]),
                    ...(selectedRocm?['--hub-rocm-pci',selectedRocm]:[])]
                :['-c',TRAIN_ENTRY,stderr];
            return ['-B',...entry,
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
module.exports={createTinyBrainsAdapter,tinyBrainsProfiles,tinyBrainsImports,supportsTinyBrainsProfile};
