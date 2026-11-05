'use strict';

// Locally constructed, trusted adapter for Ants-v2's production CLI.
// The actual project checkout, Python environment, config and final
// qualification report are selected by the host administrator, never
// by a Hub experiment JSON document.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {readAntsProgress}=require('./experiment_hub_ants_metrics');

const SAFE_PATH=/^[A-Za-z0-9_./:@=+\-,]+$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HASH=/^[0-9a-f]{64}$/;
const MAX_EPISODES=100000000;

function verifiedFile(file,expectedSha256,fsApi=fs) {
    if(!HASH.test(String(expectedSha256||'')))
        throw Error('Pinned Ants config/report SHA-256 is required.');
    let current;
    try{current=fsApi.readFileSync(file);}catch{
        throw Error('Pinned Ants config/report is not readable on this host.');
    }
    const digest=crypto.createHash('sha256').update(current).digest('hex');
    if(digest!==expectedSha256)
        throw Error('Pinned Ants config/report content changed; revalidate qualification and adapter.');
}
function resolveOwnedCheckpoint(artifactRoot,rootId,fsApi=fs) {
    if(!UUID.test(String(rootId||'')))
        throw Error('Resume requires a valid Hub artifact lineage root.');
    const checkpoints=path.join(artifactRoot,rootId,'checkpoints');
    const pointer=path.join(checkpoints,'latest.json');
    let name;
    try {
        const raw=fsApi.readFileSync(pointer,'utf8');
        if(typeof raw!=='string'||raw.length>4096)
            throw Error('Unexpected durable checkpoint pointer size.');
        const value=JSON.parse(raw);
        name=value?.checkpoint;
    }catch(error) {
        throw Error('Cannot read durable Ants continuation pointer: '+
            String(error.message||error).slice(0,120));
    }
    if(typeof name!=='string'||
        !/^checkpoint_[a-zA-Z0-9_-]{1,120}$/.test(name))
        throw Error('Invalid Ants durable checkpoint pointer target.');
    const candidate=path.join(checkpoints,name);
    let directory;
    try {
        const root=fsApi.realpathSync(checkpoints);
        directory=fsApi.realpathSync(candidate);
        if(!directory.startsWith(root+path.sep)||
            !fsApi.statSync(directory).isDirectory())
            throw Error('Checkpoint directory is outside the approved artifact root.');
        const metadata=JSON.parse(fsApi.readFileSync(
            path.join(directory,'metadata.json'),'utf8'));
        if(!metadata||typeof metadata!=='object'||
            String(metadata.reason||'').toUpperCase()==='REGRESSION')
            throw Error('Forensic REGRESSION checkpoints cannot continue production.');
    }catch(error) {
        throw Error('Invalid or unavailable local Ants continuation checkpoint: '+
            String(error.message||error).slice(0,120));
    }
    // Ants itself validates full checkpoint manifests, writer provenance,
    // compatible runtime device identity, curriculum and optimizer state.
    return directory;
}
function createAntsProductionAdapter(options={}) {
    const {python,projectRoot,configPath,qualificationReport,
        artifactRoot,configSha256,qualificationSha256}=options;
    for(const [label,value] of Object.entries({
        python,projectRoot,configPath,qualificationReport,artifactRoot,
    })) {
        if(typeof value!=='string'||!path.isAbsolute(value)||
            !SAFE_PATH.test(value)||value.length>512)
            throw Error('Ants '+label+' must be a safe absolute Linux-local path.');
    }
    if(!HASH.test(String(configSha256||''))||
        !HASH.test(String(qualificationSha256||'')))
        throw Error('Pinned Ants config and qualification report digests are required.');
    const sourceRoot=projectRoot.replace(/\/+$/,'');
    if(projectRoot==='/'||artifactRoot==='/'||
        artifactRoot===sourceRoot||artifactRoot.startsWith(sourceRoot+'/'))
        throw Error('Ants artifact parent must be outside the source checkout to preserve clean source identity.');
    const fsApi=options.fsApi||fs;
    const check=()=>{
        verifiedFile(configPath,configSha256,fsApi);
        verifiedFile(qualificationReport,qualificationSha256,fsApi);
    };
    // Fail during local opt-in, not after the dashboard has accepted a job.
    check();
    return Object.freeze({
        executable:python,
        cwd:projectRoot,
        stop_policy:'interrupt-main',
        readProgress(context) {
            // This is a filesystem read only. Job context is native,
            // validated by the local bridge; no Hub-supplied path is used.
            if(!context||context.project_id!=='ants'||
                !UUID.test(String(context.job_id||'')))
                throw Error('Invalid local Ants progress ownership.');
            const rootId=context.artifact_lineage_root_id||context.job_id;
            if(!UUID.test(String(rootId))||
                (context.continuation_of_job_id&&
                    !UUID.test(String(context.continuation_of_job_id))))
                throw Error('Invalid Ants progress artifact lineage.');
            return readAntsProgress(artifactRoot,rootId,fsApi);
        },
        args(frozen,context) {
            if(!frozen||typeof frozen!=='object'||Array.isArray(frozen)||
                Object.keys(frozen).some(key=>key!=='episodes'&&key!=='mode')||
                !['new','resume',undefined].includes(frozen.mode)||
                !Number.isSafeInteger(frozen.episodes)||
                frozen.episodes<1||frozen.episodes>MAX_EPISODES||
                !context||!UUID.test(String(context.job_id||''))||
                context.project_id!=='ants')
                throw Error('Expected a bounded production Ants job with an episode count.');
            check();
            const resume=frozen.mode==='resume';
            const rootId=context.artifact_lineage_root_id||context.job_id;
            const prior=context.continuation_of_job_id||null;
            if(!UUID.test(String(rootId))||
                (resume&&(!UUID.test(String(prior))||
                    prior===context.job_id||rootId===context.job_id))||
                (!resume&&(prior!==null||rootId!==context.job_id)))
                throw Error('Ants new/resume job ancestry is inconsistent.');
            const root=path.join(artifactRoot,rootId);
            const args=['-m','antsim','train',
                '--config',configPath,
                '--artifact-root',root,
                '--qualification-report',qualificationReport,
                '--checkpoint-on-exit',
                '--episodes',String(frozen.episodes)];
            if(resume)args.push('--resume-from-checkpoint',
                resolveOwnedCheckpoint(artifactRoot,rootId,fsApi));
            return args;
        },
    });
}
module.exports={createAntsProductionAdapter};
