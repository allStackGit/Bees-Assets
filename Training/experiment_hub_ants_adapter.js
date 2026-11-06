'use strict';

// Locally constructed, trusted adapter for Ants-v2's production CLI.
// The actual project checkout, Python environment, config and final
// qualification report are selected by the host administrator, never
// by a Hub experiment JSON document.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {readAntsProgress}=require('./experiment_hub_ants_metrics');
const {suiteIds}=require('./experiment_hub_evaluation');

const SAFE_PATH=/^[A-Za-z0-9_./:@=+\-,]+$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HASH=/^[0-9a-f]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
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
function inside(dir,parent) {
    const relative=path.relative(parent,dir);
    return relative===''||(!path.isAbsolute(relative)&&relative!=='..'&&
        !relative.startsWith('..'+path.sep));
}

function ownedArtifactRoot(artifactRoot,rootId,sourceRoot,resume,fsApi) {
    const parent=fsApi.realpathSync(artifactRoot);
    if(!SAFE_PATH.test(parent.split(path.sep).join('/'))||
        parent===path.parse(parent).root||inside(parent,fsApi.realpathSync(sourceRoot))||
        !fsApi.statSync(parent).isDirectory())
        throw Error('Ants artifact parent must be a dedicated directory outside the source checkout.');
    const root=path.join(parent,rootId);
    if(!resume) {
        try{fsApi.mkdirSync(root,{mode:0o700});}
        catch(error){if(error.code!=='EEXIST')throw error;}
    }
    if(fsApi.realpathSync(root)!==root||!fsApi.statSync(root).isDirectory()||
        (!resume&&fsApi.readdirSync(root).length!==0))
        throw Error('Ants artifact lineage must be an owned directory, empty for a new job.');
    return root;
}

function resolveOwnedCheckpoint(root,fsApi) {
    const checkpoints=path.join(root,'checkpoints');
    const pointer=path.join(checkpoints,'latest.json');
    let name;
    try {
        if(fsApi.realpathSync(checkpoints)!==checkpoints||fsApi.realpathSync(pointer)!==pointer)
            throw Error('Checkpoint pointer path is redirected.');
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
        directory=fsApi.realpathSync(candidate);
        if(directory!==candidate||
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
function evaluationReader(options,fsApi,sourceRoot,artifactRoot) {
    if(options.evaluationRoot==null&&options.evaluationSuites==null)return {};
    if(typeof options.evaluationRoot!=='string'||!path.isAbsolute(options.evaluationRoot)||
        !SAFE_PATH.test(options.evaluationRoot)||options.evaluationRoot.length>512)
        throw Error('Ants evaluation root must be a safe absolute Linux-local directory.');
    const suites=Object.freeze(suiteIds(options.evaluationSuites));
    const root=fsApi.realpathSync(options.evaluationRoot);
    const trainingRoot=fsApi.realpathSync(artifactRoot);
    const checkout=fsApi.realpathSync(sourceRoot);
    if(root===path.parse(root).root||!fsApi.statSync(root).isDirectory()||
        inside(root,checkout)||inside(checkout,root)||
        inside(root,trainingRoot)||inside(trainingRoot,root))
        throw Error('Ants evaluation receipts need a directory separate from source and training artifacts.');
    return {
        evaluation_suites:suites,
        readEvaluation(context) {
            if(context?.project_id!=='ants'||!UUID.test(String(context.job_id||''))||
                !UUID.test(String(context.dispatch_id||''))||!suites.includes(context.suite_id))
                throw Error('Invalid Ants independent evaluation identity.');
            const file=path.join(root,context.dispatch_id+'.'+context.suite_id+'.json');
            let fd;
            try {
                if(fsApi.realpathSync(root)!==root)
                    throw Error('Ants evaluation directory identity changed.');
                fd=fsApi.openSync(file,fs.constants.O_RDONLY|
                    fs.constants.O_NOFOLLOW|fs.constants.O_NONBLOCK);
                const stat=fsApi.fstatSync(fd);
                if(!stat.isFile()||stat.size<1||stat.size>4096)
                    throw Error('Invalid Ants evaluation receipt file.');
                const buffer=Buffer.alloc(4097);
                const length=fsApi.readSync(fd,buffer,0,buffer.length,0);
                if(length!==stat.size)throw Error('Ants evaluation receipt changed during read.');
                return JSON.parse(buffer.toString('utf8',0,length));
            }catch(error){
                if(error.code==='ENOENT'&&fd===undefined)return null;
                throw error;
            }finally{
                if(fd!==undefined)fsApi.closeSync(fd);
            }
        },
    };
}

function rocmVerifier(options,fsApi,check) {
    const {gpuVerifierPath,gpuVerifierSha256,configPath,qualificationReport}=options;
    if(gpuVerifierPath==null&&gpuVerifierSha256==null)return {};
    if(typeof gpuVerifierPath!=='string'||!path.isAbsolute(gpuVerifierPath)||
        !SAFE_PATH.test(gpuVerifierPath)||gpuVerifierPath.length>512)
        throw Error('Ants ROCm verifier requires a safe absolute local path.');
    const checkVerifier=()=>verifiedFile(gpuVerifierPath,gpuVerifierSha256,fsApi);
    checkVerifier();
    return {
        gpu_verify_args(_frozen,context) {
            if(context?.project_id!=='ants'||!UUID.test(String(context.job_id||''))||
                !PCI.test(String(context.gpu_pci_address||'')))
                throw Error('Ants ROCm verification requires the leased PCI identity.');
            check();
            checkVerifier();
            return ['-B',gpuVerifierPath,configPath,qualificationReport,context.gpu_pci_address];
        },
    };
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
        ...rocmVerifier(options,fsApi,check),
        ...evaluationReader(options,fsApi,sourceRoot,artifactRoot),
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
            const root=ownedArtifactRoot(artifactRoot,rootId,sourceRoot,resume,fsApi);
            const args=['-m','antsim','train',
                '--config',configPath,
                '--artifact-root',root,
                '--qualification-report',qualificationReport,
                '--checkpoint-on-exit',
                '--episodes',String(frozen.episodes)];
            if(resume)args.push('--resume-from-checkpoint',
                resolveOwnedCheckpoint(root,fsApi));
            return args;
        },
    });
}
module.exports={createAntsProductionAdapter};
