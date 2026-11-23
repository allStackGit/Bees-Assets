'use strict';

// Native Hub execution for independent Bees Unity MA-POCA training runs.
// This does not import or impersonate the legacy single-lineage Bees operator.
// Only an enrolled local administrator may install these trusted paths.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {prepare: prepareImmutableRuntime}=require('./experiment_hub_bees_immutable_runtime');

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
        'worker_sha256','trainer_sha256','evaluation_profile','evaluation_matches','batch_inference',
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
    const batching=parameters.batch_inference??true;
    if(typeof batching!=='boolean')
        throw Error('Bees batched cross-worker inference must be a boolean.');
    const profile=parameters.evaluation_profile??'off';
    const matches=parameters.evaluation_matches??8;
    if(!['off','scripted-maps-v1','scripted-coverage-v2'].includes(profile)||
        !Number.isSafeInteger(matches)||matches<1||matches>100)
        throw Error('Bees evaluation profile must be off or scripted-maps-v1 with 1–100 matches per scenario.');
    return {maxSteps,envs,seed,device,profile,matches,batching,
        environmentArgs:envArgs.slice(),hyperparameters:{...overrides}};
}

const DESIGN_FIELDS=Object.freeze([
    {key:'max_steps',label:'Training environment steps',section:'training',
        type:'integer',default:100000,min:1000,max:1000000000},
    {key:'envs',label:'Parallel Unity environments',section:'environment',
        type:'integer',default:8,min:1,max:64},
    {key:'seed',label:'Random seed',section:'training',
        type:'integer',default:42,min:0,max:2147483647},
    {key:'device',label:'Learner device',section:'training',
        type:'enum',default:'cuda',options:['cuda','cpu']},
    {key:'hyperparameters.batch_size',label:'PPO minibatch size',section:'training',
        type:'integer',default:4096,min:1,max:65536},
    {key:'hyperparameters.buffer_size',label:'PPO buffer size',section:'training',
        type:'integer',default:65536,min:64,max:4194304},
    {key:'hyperparameters.learning_rate',label:'PPO learning rate',section:'training',
        type:'number',default:0.001,min:0.000001,max:0.1},
    {key:'hyperparameters.beta',label:'Entropy coefficient',section:'training',
        type:'number',default:0.01,min:0,max:0.5},
    {key:'hyperparameters.epsilon',label:'PPO clip epsilon',section:'training',
        type:'number',default:0.15,min:0.01,max:0.5},
    {key:'hyperparameters.lambd',label:'GAE lambda',section:'training',
        type:'number',default:0.99,min:0.7,max:1},
    {key:'hyperparameters.num_epoch',label:'PPO epochs',section:'training',
        type:'integer',default:3,min:1,max:32},
    {key:'batch_inference',label:'Batch policy inference across Unity environments',
        section:'training',type:'boolean',default:true},
    {key:'evaluation_profile',label:'Post-training evaluation profile',section:'training',
        type:'enum',default:'off',options:['off','scripted-maps-v1','scripted-coverage-v2']},
    {key:'evaluation_matches',label:'Matches per evaluation scenario',section:'training',
        type:'integer',default:8,min:1,max:100},
]);
const METRICS=Object.freeze(['training_steps','elapsed_training_seconds',
    'environment_steps_per_second','mean_reward','mean_episode_steps',
    'policy_loss','value_loss','policy_entropy','self_play_elo',
    'self_play_win_rate','evaluation_elapsed_seconds',
    'evaluation_cases_total',
    'evaluation_cases_completed','evaluation_matches_completed',
    'evaluation_matches','evaluation_cases',
    'evaluation_win_rate','evaluation_timeout_rate','evaluation_score_rate',
    ...[1,2,3,4,5,6].flatMap(players=>[
        'evaluation_score_rate_'+players+'p',
        'evaluation_timeout_rate_'+players+'p',
    ]),
    ...[32,64,128,256].flatMap(size=>[
        'evaluation_score_rate_map'+size,
        'evaluation_timeout_rate_map'+size,
    ]),
    'evaluation_score_rate_pursuit','evaluation_score_rate_strafe']);

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
    // Native Hub source is captured once at enrollment using the same
    // immutable, content-addressed package used by Bees' legacy control
    // plane. A later Git checkout edit cannot change queued job behavior.
    const pinned=prepareImmutableRuntime({
        project,python,artifactRoot:artifacts,workerSha:fileHash(worker),
    });
    const trainer=trustedFile(pinned.trainer,'Frozen Bees trainer YAML');
    const training=trustedFile(pinned.training,'Frozen Bees ML-Agents learner');
    const runner=trustedFile(pinned.runner,'Frozen Bees native Hub runner');
    const evaluator=trustedFile(pinned.evaluator,'Frozen Bees benchmark evaluator');
    const evaluatorCore=trustedFile(pinned.evaluatorCore,'Frozen Bees evaluation runtime');
    const opponentScript=trustedFile(pinned.opponentScript,'Frozen Bees scripted opponent library');
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
        runtime_version:pinned.runtimeVersion,
        trainer_sha256:fileHash(trainer),
        training_sha256:fileHash(training),
        evaluator_sha256:fileHash(evaluator),
        evaluator_core_sha256:fileHash(evaluatorCore),
        opponent_script_sha256:fileHash(opponentScript),
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
            !SHA.test(String(receipt.checkpoint_sha256||''))||
            typeof receipt.model!=='string'||!path.isAbsolute(receipt.model)||
            typeof receipt.checkpoint!=='string'||!path.isAbsolute(receipt.checkpoint)||
            !receipt.model.startsWith(path.join(root,'results')+path.sep)||
            !receipt.checkpoint.startsWith(path.join(root,'results')+path.sep)||
            path.basename(receipt.checkpoint)!=='checkpoint.pt')
            throw Error('Invalid Bees native checkpoint receipt.');
        const model=fs.realpathSync(receipt.model);
        const optimizer=fs.realpathSync(receipt.checkpoint);
        const within=path.join(root,'results')+path.sep;
        if(!model.startsWith(within)||!optimizer.startsWith(within)||
            !fs.statSync(model).isFile()||!fs.statSync(optimizer).isFile()||
            fs.statSync(model).size<1||fs.statSync(optimizer).size<1||
            fileHash(model)!==receipt.sha256||
            fileHash(optimizer)!==receipt.checkpoint_sha256)
            throw Error('Native Bees model or optimizer checkpoint differs from committed bytes.');
        return receipt;
    }

    return {
        executable:python,cwd:project,stop_policy:'interrupt-main',
        design_catalog:{bees_native:{version:1,
            worker_sha256:approved.worker_sha256,
            runtime_version:approved.runtime_version,
            trainer_sha256:approved.trainer_sha256,
            fields:DESIGN_FIELDS.map(field=>({...field})),
            metric_catalog:{base:[...METRICS]}}},
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
                fs.readFileSync(path.join(pinned.root,'bees-runtime-version.txt'),'utf8').trim()!==
                    approved.runtime_version||
                fileHash(trainer)!==approved.trainer_sha256||
                fileHash(training)!==approved.training_sha256||
                fileHash(evaluator)!==approved.evaluator_sha256||
                fileHash(evaluatorCore)!==approved.evaluator_core_sha256||
                fileHash(opponentScript)!==approved.opponent_script_sha256)
                throw Error('Installed Bees Unity worker or trainer changed after native adapter enrollment.');
            const root=jobPath(artifacts,context);
            fs.mkdirSync(root,{mode:0o700});
            const job={
                schema_version:1,job_id:context.job_id,
                game_binary:worker,game_sha256:approved.worker_sha256,
                runtime_version:approved.runtime_version,
                training_script:training,training_sha256:approved.training_sha256,
                trainer_source:trainer,trainer_sha256:approved.trainer_sha256,
                output_root:root,num_envs:frozen.envs,max_steps:frozen.maxSteps,
                seed:frozen.seed,device:frozen.device,
                hyperparameters:frozen.hyperparameters,
                environment_args:frozen.environmentArgs,
                expected_gpu_pci:context.gpu_pci_address??null,
                evaluation_profile:frozen.profile,
                evaluation_matches:frozen.matches,
                batch_inference:frozen.batching,
                evaluator_script:evaluator,evaluator_sha256:approved.evaluator_sha256,
                evaluator_core:evaluatorCore,
                evaluator_core_sha256:approved.evaluator_core_sha256,
                opponent_script:opponentScript,
                opponent_script_sha256:approved.opponent_script_sha256,
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
                const value=JSON.parse(bytes.toString('utf8'));
                const summary=path.join(root,'evaluations','summary.json');
                try {
                    if(fs.realpathSync(summary)!==summary||
                        fs.statSync(summary).size>32768)
                        throw Error('Bees evaluation summary was redirected or is too large.');
                    const data=JSON.parse(fs.readFileSync(summary,'utf8'));
                    const receipt=checkpoint(context);
                    const trials=data.total_matches,wins=data.total_wins,
                        draws=data.total_draws,timeouts=data.total_timeouts,
                        losses=data.total_losses;
                    if(data.schema_version!==1||data.job_id!==context.job_id||
                        !['scripted-maps-v1','scripted-coverage-v2'].includes(data.profile)||
                        data.model_sha256!==receipt.sha256||
                        ![trials,wins,draws,timeouts,losses].every(x=>
                            Number.isSafeInteger(x)&&x>=0)||
                        !trials||wins+draws+losses!==trials||timeouts>draws)
                        throw Error('Bees native evaluation totals are inconsistent.');
                    const cases=data.cases;
                    const requiredCases=data.profile==='scripted-coverage-v2'?48:4;
                    if(data.case_count!==requiredCases||!Array.isArray(cases)||
                        cases.length!==requiredCases)
                        throw Error('Bees evaluation case inventory is incomplete.');
                    const unique=new Set();
                    const totals={matches:0,wins:0,draws:0,losses:0,timeouts:0};
                    const byPlayers=new Map(),byMaps=new Map(),byOpponents=new Map();
                    const addBucket=(buckets,key,item)=>{
                        const bucket=buckets.get(key)||{matches:0,wins:0,draws:0,timeouts:0};
                        bucket.matches+=item.matches;
                        bucket.wins+=item.wins;
                        bucket.draws+=item.draws;
                        bucket.timeouts+=item.timeouts;
                        buckets.set(key,bucket);
                    };
                    for(const item of cases) {
                        if(!item||typeof item!=='object'||
                            typeof item.case!=='string'||unique.has(item.case)||
                            !['pursuit','strafe'].includes(item.opponent)||
                            !Number.isSafeInteger(item.matches)||
                            item.matches<1||item.matches>100)
                            throw Error('Malformed or duplicated Bees evaluation case.');
                        unique.add(item.case);
                        for(const key of Object.keys(totals)) {
                            if(!Number.isSafeInteger(item[key])||
                                item[key]<0||item[key]>item.matches)
                                throw Error('Invalid Bees evaluation case accounting.');
                            totals[key]+=item[key];
                        }
                        if(item.wins+item.draws+item.losses!==item.matches||
                            item.timeouts>item.draws)
                            throw Error('Bees evaluation case outcomes are inconsistent.');
                        if(data.profile==='scripted-coverage-v2') {
                            const match=/^p([1-6])_map(32|64|128|256)_(pursuit|strafe)$/
                                .exec(item.case);
                            if(!match||item.opponent!==match[3])
                                throw Error('Bees coverage case identity is invalid.');
                            addBucket(byPlayers,Number(match[1]),item);
                            addBucket(byMaps,Number(match[2]),item);
                            addBucket(byOpponents,match[3],item);
                        }
                    }
                    for(const key of Object.keys(totals))
                        if(totals[key]!==data['total_'+key])
                            throw Error('Bees evaluation case totals do not agree with the summary.');
                    if(data.profile==='scripted-coverage-v2'&&
                        (byPlayers.size!==6||byMaps.size!==4||byOpponents.size!==2||
                         [...byPlayers.values(),...byMaps.values(),...byOpponents.values()]
                             .some(x=>x.matches<1)))
                        throw Error('Bees evaluation player-count coverage is incomplete.');
                    const rate=(wins+0.5*(draws-timeouts))/trials;
                    const measured={
                        evaluation_matches:{value:trials,unit:'count'},
                        evaluation_cases:{value:requiredCases,unit:'count'},
                        evaluation_win_rate:{value:wins/trials,unit:'ratio'},
                        evaluation_timeout_rate:{value:timeouts/trials,unit:'ratio'},
                        evaluation_score_rate:{value:rate,unit:'ratio'},
                    };
                    for(const [players,item] of byPlayers) {
                        measured['evaluation_score_rate_'+players+'p']={
                            value:(item.wins+0.5*(item.draws-item.timeouts))/item.matches,
                            unit:'ratio',
                        };
                        measured['evaluation_timeout_rate_'+players+'p']={
                            value:item.timeouts/item.matches,unit:'ratio',
                        };
                    }
                    const score=item=>
                        (item.wins+0.5*(item.draws-item.timeouts))/item.matches;
                    for(const [size,item] of byMaps) {
                        measured['evaluation_score_rate_map'+size]={
                            value:score(item),unit:'ratio',
                        };
                        measured['evaluation_timeout_rate_map'+size]={
                            value:item.timeouts/item.matches,unit:'ratio',
                        };
                    }
                    for(const [opponent,item] of byOpponents) {
                        measured['evaluation_score_rate_'+opponent]={
                            value:score(item),unit:'ratio',
                        };
                    }
                    value.source_modified_at=fs.statSync(summary).mtime.toISOString();
                    value.metrics={...value.metrics,...measured};
                }catch(error) {
                    if(error.code!=='ENOENT')throw error;
                }
                return validateProgress(value,'bees_native');
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
