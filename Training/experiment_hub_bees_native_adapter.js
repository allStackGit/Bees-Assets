'use strict';

// Native Hub execution for independent Bees Unity MA-POCA training runs.
// This does not import or impersonate the legacy single-lineage Bees operator.
// Only an enrolled local administrator may install these trusted paths.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');

const UUID=/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/;
const SHA=/^[0-9a-f]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const SAFE_PATH=/^[A-Za-z0-9_./:@=+\-, ]+$/;
const FLAGS=/^--rl-[a-z0-9-]+(?:=[^\r\n\x00]{0,192})?$/i;
const HYPER={
    batch_size:[1,65536,true],
    buffer_size:[64,4194304,true],
    learning_rate:[0.000001,0.1,false],
    beta:[0,0.5,false],
    epsilon:[0.01,0.5,false],
    lambd:[0.7,1,false],
    num_epoch:[1,32,true],
};

function fileHash(source) {
    const digest=crypto.createHash('sha256');
    const fd=fs.openSync(source,'r');
    const block=Buffer.allocUnsafe(1024*1024);
    try{
        while(true) {
            const size=fs.readSync(fd,block,0,block.length,null);
            if(!size)break;
            digest.update(block.subarray(0,size));
        }
    }finally{fs.closeSync(fd);}
    return digest.digest('hex');
}
function trustedFile(raw,description,executable=false) {
    if(typeof raw!=='string'||!path.isAbsolute(raw)||!SAFE_PATH.test(raw))
        throw Error(description+' requires a safe, absolute installed path.');
    const resolved=fs.realpathSync(raw);
    if(!SAFE_PATH.test(resolved))throw Error(description+' resolved to an unsupported path.');
    const stat=fs.statSync(resolved);
    if(!stat.isFile()||(executable&&(stat.mode&0o111)===0))
        throw Error(description+' is not a usable installed file.');
    return resolved;
}
function jobPath(artifacts,context) {
    if(context.project_id!=='bees_native'||!UUID.test(context.job_id)||
        context.artifact_lineage_root_id!==context.job_id||
        context.continuation_of_job_id)
        throw Error('Independent Bees native job requires a fresh Hub-owned lineage.');
    return path.join(artifacts,context.job_id);
}
function normalize(parameters,context,approved) {
    const allowed=new Set([
        'max_steps','envs','device','seed','hyperparameters','environment_args',
        'worker_sha256','trainer_sha256',
    ]);
    if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
        Object.keys(parameters).some(key=>!allowed.has(key)))
        throw Error('Bees native job has unrecognized training parameters.');
    const maxSteps=parameters.max_steps,envs=parameters.envs,seed=parameters.seed;
    if(!Number.isSafeInteger(maxSteps)||maxSteps<1000||maxSteps>100000000000||
        !Number.isSafeInteger(envs)||envs<1||envs>64||
        !Number.isSafeInteger(context.cpu_quota)||envs>context.cpu_quota||
        !Number.isSafeInteger(seed)||seed<0||seed>2147483647)
        throw Error('Bees native training needs 1,000–100 billion steps, 1–64 environments within the CPU lease, and a valid seed.');
    const device=parameters.device;
    if(!['cpu','cuda'].includes(device)||
        (device==='cuda'?!PCI.test(String(context.gpu_pci_address||''))||
            Boolean(context.gpu_pci_addresses)||context.gpu_mode!=='exclusive':
            Boolean(context.gpu_pci_address||context.gpu_pci_addresses)))
        throw Error('Bees native device must match exactly one exclusive ROCm lease or a CPU-only allocation.');
    if(parameters.worker_sha256!==approved.worker_sha256||
        parameters.trainer_sha256!==approved.trainer_sha256)
        throw Error('Bees native job source hashes differ from the locally approved Unity worker and MA-POCA configuration.');
    const envArgs=parameters.environment_args??[];
    if(!Array.isArray(envArgs)||envArgs.length>32||
        envArgs.some(arg=>typeof arg!=='string'||arg.length>256||!FLAGS.test(arg)))
        throw Error('Bees native environment arguments must be 32 or fewer approved --rl-* tokens.');
    const overrides=parameters.hyperparameters??{};
    if(!overrides||typeof overrides!=='object'||Array.isArray(overrides)||
        Object.keys(overrides).some(key=>!Object.hasOwn(HYPER,key)))
        throw Error('Unknown Bees native PPO hyperparameter.');
    for(const [key,value] of Object.entries(overrides)) {
        const [min,max,integer]=HYPER[key];
        if(typeof value!=='number'||!Number.isFinite(value)||value<min||value>max||
            integer&&!Number.isSafeInteger(value))
            throw Error('Invalid Bees native PPO hyperparameter: '+key);
    }
    if(overrides.buffer_size!==undefined&&overrides.batch_size!==undefined&&
        (overrides.buffer_size<overrides.batch_size||
        overrides.buffer_size%overrides.batch_size!==0))
        throw Error('Bees native PPO buffer size must be a multiple of batch size.');
    return {maxSteps,envs,seed,device,
        environmentArgs:envArgs.slice(),hyperparameters:{...overrides}};
}

function createBeesNativeAdapter({projectRoot,pythonPath,workerExecutable,artifactRoot}) {
    if(process.platform!=='linux')
        throw Error('The first native Bees Unity training adapter requires the Linux ROCm supervisor.');
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    if(!/^[A-Za-z0-9_./:@=+\-,]+$/.test(project)||
        !/^[A-Za-z0-9_./:@=+\-,]+$/.test(artifacts))
        throw Error('Bees native owner directories must be valid systemd argument paths.');
    if(!fs.statSync(project).isDirectory()||
        !fs.statSync(artifacts).isDirectory()||
        artifacts===project||artifacts.startsWith(project+path.sep)||
        project.startsWith(artifacts+path.sep))
        throw Error('Bees native artifacts must be outside the Unity checkout.');
    const python=trustedFile(pythonPath,'Bees native Python',true);
    const worker=trustedFile(workerExecutable,'Bees Linux dedicated worker',true);
    const trainer=trustedFile(path.join(project,'Training','rl_1v1_config.yaml'),
        'Bees trainer YAML');
    const training=trustedFile(path.join(project,'Training','bees_mlagents_learn.py'),
        'Bees ML-Agents learner');
    const runner=trustedFile(path.join(__dirname,'experiment_hub_bees_native_train.py'),
        'Bees native Hub runner');
    if(path.basename(worker)!=='Bees RL Training.x86_64')
        throw Error('Bees native Hub requires the dedicated Linux Unity worker.');
    const probe=spawnSync(python,['-c',
        'import sys,mlagents,torch,yaml,tensorboard;'+
        'assert sys.version_info[:2]==(3,10);'+
        'assert __import__("importlib.metadata",fromlist=["version"]).version("mlagents")=="1.1.0"'],
        {cwd:project,timeout:20000,encoding:'utf8',windowsHide:true,maxBuffer:16384});
    if(probe.error||probe.status!==0)
        throw Error('Bees native adapter requires an installed Python 3.10 ML-Agents 1.1.0 learner with PyTorch and TensorBoard.');
    const approved={
        worker_sha256:fileHash(worker),
        trainer_sha256:fileHash(trainer),
        training_sha256:fileHash(training),
    };

    function checkpoint(context) {
        const root=jobPath(artifacts,context);
        const metadata=path.join(root,'model.json');
        if(fs.realpathSync(metadata)!==metadata||
            fs.statSync(metadata).size>4096)
            throw Error('Bees checkpoint receipt is missing or redirected.');
        const receipt=JSON.parse(fs.readFileSync(metadata,'utf8'));
        if(receipt.schema_version!==1||receipt.job_id!==context.job_id||
            !SHA.test(String(receipt.sha256||''))||
            typeof receipt.model!=='string'||!path.isAbsolute(receipt.model)||
            !receipt.model.startsWith(path.join(root,'results')+path.sep))
            throw Error('Invalid Bees native model receipt.');
        const model=fs.realpathSync(receipt.model);
        if(!model.startsWith(path.join(root,'results')+path.sep)||
            fileHash(model)!==receipt.sha256)
            throw Error('Native Bees model bytes differ from recorded checkpoint.');
        return receipt;
    }

    return {
        executable:python,cwd:project,stop_policy:'interrupt-main',
        gpu_verify_args(parameters,context) {
            if(parameters?.device!=='cuda'||!PCI.test(String(context.gpu_pci_address||'')))
                throw Error('Bees GPU qualification requires a pinned ROCm PCI identity.');
            return ['-B',path.join(__dirname,
                'experiment_hub_tinybrains_rocm_verify.py'),context.gpu_pci_address];
        },
        // Both training and Unity worker hashes are carried in every saved
        // configuration and remain independently verified at machine Start.
        bees_native_versions:{...approved},
        artifact_export_dir(jobId) {
            if(!UUID.test(String(jobId)))throw Error('Invalid Bees native export identity.');
            return path.join(artifacts,jobId);
        },
        checkpoint_ready(context) {checkpoint(context);return true;},
        model_checkpoint_sha256(context) {return checkpoint(context).sha256;},
        args(parameters,context) {
            jobPath(artifacts,context);
            const frozen=normalize(parameters,context,approved);
            if(fileHash(worker)!==approved.worker_sha256||
                fileHash(trainer)!==approved.trainer_sha256||
                fileHash(training)!==approved.training_sha256)
                throw Error('Installed Bees Unity worker or trainer changed after native adapter enrollment.');
            const root=jobPath(artifacts,context);
            fs.mkdirSync(root,{mode:0o700});
            const job={
                schema_version:1,job_id:context.job_id,
                game_binary:worker,game_sha256:approved.worker_sha256,
                training_script:training,training_sha256:approved.training_sha256,
                trainer_source:trainer,trainer_sha256:approved.trainer_sha256,
                output_root:root,num_envs:frozen.envs,max_steps:frozen.maxSteps,
                seed:frozen.seed,device:frozen.device,
                hyperparameters:frozen.hyperparameters,
                environment_args:frozen.environmentArgs,
                expected_gpu_pci:context.gpu_pci_address??null,
            };
            const input=path.join(root,'job.json');
            fs.writeFileSync(input,JSON.stringify(job,null,2)+'\n',
                {flag:'wx',mode:0o600});
            return ['-B',runner,'--job',input];
        },
        readProgress(context) {
            const root=jobPath(artifacts,context);
            const progress=path.join(root,'progress.json');
            let fd;
            try {
                if(fs.realpathSync(progress)!==progress)
                    throw Error('Native Bees progress path is redirected.');
                fd=fs.openSync(progress,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||stat.size<1||stat.size>32768)
                    throw Error('Invalid Bees progress report size.');
                const bytes=Buffer.alloc(stat.size);
                if(fs.readSync(fd,bytes,0,bytes.length,0)!==bytes.length)
                    throw Error('Bees progress was modified during reading.');
                return validateProgress(JSON.parse(bytes.toString('utf8')),'bees_native');
            }catch(error) {
                if(error.code==='ENOENT')return null;
                throw error;
            }finally{
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}
module.exports={createBeesNativeAdapter};
