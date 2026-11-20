'use strict';

// Machine-installed CPU/GPU collector for an issued *portable* native MA-POCA
// work order. Experiment Hub remains the sole scheduler, lease owner and
// process supervisor. This adapter only selects a trusted local executable,
// checks the frozen lease against the collector's allocation and exposes
// the same verified rollout bundle artifacts as bees_native_collect.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {validateProgress} = require('./experiment_hub_progress');
const {capturePaths} = require('./experiment_hub_native_bundle_transfer');

const PROJECT = 'bees_native_collect';
const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA = /^[0-9a-f]{64}$/;
const PCI = /^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const ROCM_UUID = /^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const SAFE_ARG = /^[A-Za-z0-9_./:@=+\-,]+$/;
const safeLocalArg = value => typeof value === 'string' && value.length <= 1024 &&
    (process.platform === 'win32' ? !/[\x00\r\n]/.test(value) : SAFE_ARG.test(value));
const sha256 = value => crypto.createHash('sha256').update(value).digest('hex');
const PORTABLE_CONFIG_SHA256=sha256(JSON.stringify([
    PROJECT,'portable-collector-v1',
]));

function trustedFile(file,{preserveInvocation=false}={}) {
    if(typeof file !== 'string'||!path.isAbsolute(file)||!safeLocalArg(file))
        throw Error('Native portable collector needs an absolute safe local file path.');
    const resolved=fs.realpathSync(file);
    if(!fs.statSync(resolved).isFile()||!safeLocalArg(resolved))
        throw Error('Native portable collector executable/source was redirected.');
    // Invoking a venv's python symlink by the real target path can silently
    // select system site-packages instead of the machine's approved venv.
    // Preserve the invocation path, while pinning its actual content.
    return preserveInvocation?file:resolved;
}

function trustedDirectory(directory) {
    if(typeof directory!=='string'||!path.isAbsolute(directory)||
        !safeLocalArg(directory))
        throw Error('Native portable collector needs an existing absolute private directory.');
    const resolved=fs.realpathSync(directory);
    if(!fs.statSync(resolved).isDirectory()||!safeLocalArg(resolved))
        throw Error('Native portable collector directory was redirected.');
    return resolved;
}

function trustedReceiver(endpoint) {
    if(typeof endpoint!=='string'||!SAFE_ARG.test(endpoint))
        throw Error('Native collector needs an approved private receiver endpoint.');
    const url=new URL(endpoint);
    if(!['http:','https:'].includes(url.protocol)||!url.hostname||
        url.username||url.password||url.search||url.hash||
        !['','/'].includes(url.pathname))
        throw Error('Native collector receiver must be an HTTP(S) origin without credentials.');
    return url.origin;
}

function createNativeBeesPortableCollectorAdapter({
    pythonPath, rolloutBinary, artifactRoot, scratchRoot, tokenFile, receiverUrl,
    rocrGpuUuids = {},
}) {
    if(!rocrGpuUuids||typeof rocrGpuUuids!=='object'||Array.isArray(rocrGpuUuids)||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid)))
        throw Error('Invalid machine-owned native GPU PCI-to-ROCr UUID binding.');
    const rocr_gpu_uuids=Object.freeze({...rocrGpuUuids});
    const executable=trustedFile(pythonPath,{preserveInvocation:true});
    const binary=trustedFile(rolloutBinary);
    const artifacts=trustedDirectory(artifactRoot);
    const scratch=trustedDirectory(scratchRoot);
    if(artifacts===scratch)throw Error('Hub collection artifacts and scratch must differ.');
    const credential=trustedFile(tokenFile);
    const stat=fs.statSync(credential);
    if(stat.size<32||stat.size>512||
        process.platform!=='win32'&&(stat.mode&0o077)!==0)
        throw Error('Native bundle credential must be a private local secret file.');
    const receiver=trustedReceiver(receiverUrl);
    const tools=path.resolve(__dirname,'../native/tools');
    const names=[
        'run_portable_distributed_collector.py',
        'run_distributed_collector.py',
        'train_policy_round.py',
        'transfer_policy_inputs.py',
        'transfer_policy_bundle.py',
        'native_bundle_receiver.py',
        'native_distributed_collection.py',
        'native_gpu_allocation.py',
        'native_training_artifacts.py',
        'policy_worker.py',
    ];
    const pinned=new Map([
        [executable,sha256(fs.readFileSync(executable))],
        [binary,sha256(fs.readFileSync(binary))],
        ...names.map(name=>{
            const file=trustedFile(path.join(tools,name));
            return [file,sha256(fs.readFileSync(file))];
        }),
    ]);
    const runner=trustedFile(path.join(tools,names[0]));
    const gpuVerifier=trustedFile(path.join(__dirname,
        'experiment_hub_tinybrains_rocm_verify.py'));
    pinned.set(gpuVerifier,sha256(fs.readFileSync(gpuVerifier)));
    // Hub identifies the portable collection *protocol*, while source
    // contract, seed and World windows remain immutable job parameters.
    // Different Windows/Linux worker paths and binary bytes are installed
    // and verified independently on each machine before every launch.
    const configSha256=PORTABLE_CONFIG_SHA256;

    function jobRoot(context) {
        if(context?.project_id!==PROJECT||!UUID.test(String(context.job_id||'')))
            throw Error('Invalid native portable collector job identity.');
        return path.join(artifacts,context.job_id);
    }

    function verifiedReceipt(context) {
        const output=jobRoot(context);
        const file=path.join(output,'rollout-bundle.json');
        let stat;
        try{stat=fs.lstatSync(file);}
        catch(error){if(error.code==='ENOENT')return null;throw error;}
        if(!stat.isFile()||stat.isSymbolicLink()||stat.size<1||
            stat.size>2*1024*1024||fs.realpathSync(file)!==file)
            throw Error('Native collector receipt is not a regular bounded artifact.');
        const receipt=JSON.parse(fs.readFileSync(file,'utf8'));
        const metrics=receipt.rollout_metrics;
        // Start validates the downloaded work order. Progress and completion
        // must separately bind the observed receipt to the same immutable Hub
        // assignment, not simply accept an unrelated file under this job ID.
        const frozen=context.frozen_parameters;
        if(!frozen||frozen.config_sha256!==configSha256||
            !SHA.test(String(frozen.collection_id||''))||
            !SHA.test(String(frozen.source_run_contract_sha256||''))||
            !Number.isSafeInteger(frozen.seed_offset)||
            !Number.isSafeInteger(frozen.rollout_world_start)||
            !Number.isSafeInteger(frozen.rollout_worlds)||
            !Number.isSafeInteger(frozen.rollout_workers)||
            !Number.isSafeInteger(frozen.physics_workers)||
            receipt.collection_id!==frozen.collection_id||
            receipt.source_run_contract_sha256!==frozen.source_run_contract_sha256||
            receipt.seed_offset!==frozen.seed_offset||
            receipt.partition?.world_start!==frozen.rollout_world_start||
            receipt.partition?.world_count!==frozen.rollout_worlds||
            receipt.rollout_worlds!==frozen.rollout_worlds||
            receipt.rollout_processes!==frozen.rollout_workers||
            receipt.physics_workers_per_process!==frozen.physics_workers)
            throw Error('Native portable collector metrics differ from its frozen Hub work order.');
        if(receipt?.kind!=='native_rollout_bundle'||receipt.schema_version!==1||
            !SHA.test(String(receipt.collection_id||''))||
            !SHA.test(String(receipt.input_checkpoint_sha256||''))||
            !SHA.test(String(receipt.input_manifest_sha256||''))||
            !SHA.test(String(receipt.source_run_contract_sha256||''))||
            !Number.isSafeInteger(receipt.seed_offset)||receipt.seed_offset<0||
            !Number.isSafeInteger(receipt.rollout_worlds)||receipt.rollout_worlds<1||
            !Number.isSafeInteger(receipt.rollout_processes)||receipt.rollout_processes<1||
            !Number.isSafeInteger(receipt.physics_workers_per_process)||
            receipt.physics_workers_per_process<1||
            !Number.isSafeInteger(receipt.partition?.world_start)||
            receipt.partition.world_start<0||
            receipt.partition?.world_count!==receipt.rollout_worlds||
            !metrics||!Number.isSafeInteger(metrics.successful_policy_rows)||
            metrics.successful_policy_rows<1||
            !Number.isSafeInteger(metrics.successful_world_steps)||
            metrics.successful_world_steps<1||
            typeof metrics.wall_seconds!=='number'||!Number.isFinite(metrics.wall_seconds)||
            metrics.wall_seconds<=0)
            throw Error('Native portable collector receipt is invalid or incomplete.');
        const gpu=metrics.gpu_inference;
        const leased=receipt.leased_gpu_inference===true;
        const expectedCount=Array.isArray(context.gpu_pci_addresses)
            ?context.gpu_pci_addresses.length
            :context.gpu_pci_address?1:0;
        if((leased!==Boolean(expectedCount))||
            (leased&&(![1,2].includes(receipt.leased_gpu_count??1)||
                (receipt.leased_gpu_count??1)!==expectedCount||
                receipt.leased_gpu_count>receipt.rollout_processes||
                (expectedCount===2&&(
                    !Array.isArray(gpu?.gpu_pci_addresses)||
                    gpu.gpu_pci_addresses.length!==2||
                    new Set(gpu.gpu_pci_addresses).size!==2||
                    gpu.gpu_pci_addresses.some(pci=>
                        !context.gpu_pci_addresses.includes(pci))
                ))))||
            (!leased&&receipt.leased_gpu_count!==undefined)||
            (leased&&!gpu)||
            (gpu!=null&&(
                !Number.isSafeInteger(gpu.verified_workers)||
                gpu.verified_workers!==receipt.rollout_processes||
                !Number.isSafeInteger(gpu.model_forwards)||
                gpu.model_forwards<gpu.verified_workers||
                !Number.isSafeInteger(gpu.agent_rows)||
                gpu.agent_rows<gpu.model_forwards||
                gpu.agent_rows>metrics.successful_policy_rows
            )))
            throw Error('Native collector GPU work lacks completed model-forward evidence.');
        capturePaths(receipt);
        return {receipt,stat};
    }

    function verifiedLearnerDelivery(context) {
        const completed=verifiedReceipt(context);
        if(!completed)return false;
        const output=jobRoot(context);
        const target=path.join(output,'delivery-acknowledgement.json');
        let stat;
        try { stat=fs.lstatSync(target); }
        catch(error) {
            if(error.code==='ENOENT')return false;
            throw error;
        }
        if(!stat.isFile()||stat.isSymbolicLink()||stat.size<1||
            stat.size>4096||fs.realpathSync(target)!==target)
            throw Error('Native collector learner delivery proof is not a bounded owned file.');
        const proof=JSON.parse(fs.readFileSync(target,'utf8'));
        const frozen=context.frozen_parameters;
        const fields=[
            'schema_version','kind','hub_job_id','collector_id','collection_id',
            'source_run_contract_sha256','bundle_sha256','archive_sha256','archive_bytes',
        ];
        if(!proof||typeof proof!=='object'||Array.isArray(proof)||
            Object.keys(proof).length!==fields.length||
            fields.some(field=>!Object.hasOwn(proof,field))||
            proof.schema_version!==1||
            proof.kind!=='native_collector_upload_accepted'||
            proof.hub_job_id!==context.job_id||
            proof.collector_id!==frozen.collector_id||
            proof.collection_id!==frozen.collection_id||
            proof.source_run_contract_sha256!==frozen.source_run_contract_sha256||
            !SHA.test(String(proof.bundle_sha256||''))||
            proof.bundle_sha256!==sha256(fs.readFileSync(
                path.join(output,'rollout-bundle.json')))||
            !SHA.test(String(proof.archive_sha256||''))||
            !Number.isSafeInteger(proof.archive_bytes)||proof.archive_bytes<1)
            throw Error('Native collector learner delivery does not match the issued Hub work.');
        return true;
    }

    async function reconcileLearnerDelivery(context) {
        // The receiver may have committed the upload while the collector's
        // HTTP response or local acknowledgement was lost. Reconcile only
        // this exact frozen collection via the already-authenticated learner
        // endpoint; never launch another process or claim a new Hub lease.
        const local=verifiedReceipt(context);
        if(!local)return false;
        if(verifiedLearnerDelivery(context))return true;
        const frozen=context.frozen_parameters;
        if(!/^[A-Za-z0-9_-]{1,96}$/.test(String(frozen.collector_id||''))||
            !/^[A-Za-z0-9_-]{1,96}$/.test(String(frozen.bundle_directory||''))||
            !SHA.test(String(frozen.collection_id||'')))
            throw Error('Native collector has no frozen delivery identity.');
        const credentialBytes=fs.readFileSync(credential,'utf8').trim();
        if(credentialBytes.length<32||credentialBytes.length>256||
            /[^\x21-\x7e]/.test(credentialBytes))
            throw Error('Native learner delivery credential has an invalid format.');
        const endpoint=new URL('/v1/bundles/'+frozen.collector_id,receiver);
        const response=await fetch(endpoint,{
            method:'GET',redirect:'error',
            headers:{Authorization:'Bearer '+credentialBytes},
            signal:AbortSignal.timeout(6000),
        });
        if(response.status===404)return false;
        if(!response.ok)throw Error('Native learner delivery lookup failed: HTTP '+response.status);
        let body='',size=0;
        for await(const chunk of response.body) {
            size+=chunk.length;
            if(size>8192)throw Error('Native learner delivery response exceeds the bounded receipt.');
            body+=Buffer.from(chunk).toString('utf8');
        }
        const accepted=JSON.parse(body);
        const receiptFile=path.join(jobRoot(context),'rollout-bundle.json');
        const bundleSha=sha256(fs.readFileSync(receiptFile));
        if(!accepted||accepted.kind!=='native_bundle_received'||
            accepted.collector_id!==frozen.collector_id||
            accepted.bundle_sha256!==bundleSha||
            !SHA.test(String(accepted.archive_sha256||''))||
            !Number.isSafeInteger(accepted.archive_bytes)||
            accepted.archive_bytes<1)
            throw Error('Learner accepted a different or invalid native collection.');
        const archive=path.join(scratch,
            'native-collector-'+frozen.collection_id,frozen.bundle_directory+'.tar');
        let stat;
        try { stat=fs.lstatSync(archive); }
        catch(error) {
            if(error.code==='ENOENT')return false;
            throw error;
        }
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.size!==accepted.archive_bytes||
            fs.realpathSync(archive)!==archive)
            throw Error('Original native upload archive is unavailable or redirected.');
        const hash=crypto.createHash('sha256');
        for await(const chunk of fs.createReadStream(archive))hash.update(chunk);
        if(hash.digest('hex')!==accepted.archive_sha256)
            throw Error('Native upload archive differs from learner-accepted bytes.');

        const proof={
            schema_version:1,kind:'native_collector_upload_accepted',
            hub_job_id:context.job_id,collector_id:frozen.collector_id,
            collection_id:frozen.collection_id,
            source_run_contract_sha256:frozen.source_run_contract_sha256,
            bundle_sha256:bundleSha,archive_sha256:accepted.archive_sha256,
            archive_bytes:accepted.archive_bytes,
        };
        const output=jobRoot(context);
        const target=path.join(output,'delivery-acknowledgement.json');
        const temporary=path.join(output,
            '.delivery-acknowledgement-'+crypto.randomUUID()+'.pending');
        try {
            let fd;
            try {
                fd=fs.openSync(temporary,'wx',0o600);
                fs.writeFileSync(fd,JSON.stringify(proof)+'\n');
                fs.fsyncSync(fd);
            }finally {
                if(fd!==undefined)fs.closeSync(fd);
            }
            try { fs.linkSync(temporary,target); }
            catch(error) {
                if(error.code!=='EEXIST'||!verifiedLearnerDelivery(context))
                    throw error;
            }
            if(process.platform!=='win32') {
                const directory=fs.openSync(output,'r');
                try { fs.fsyncSync(directory); }
                finally { fs.closeSync(directory); }
            }
        }finally {
            try { fs.unlinkSync(temporary); }
            catch(error) { if(error.code!=='ENOENT')throw error; }
        }
        return verifiedLearnerDelivery(context);
    }

    function committedRolloutProgress(context) {
        const frozen=context?.frozen_parameters;
        if(!frozen||!SHA.test(String(frozen.collection_id||''))||
            !/^[A-Za-z0-9_-]{1,96}$/.test(String(frozen.bundle_directory||''))||
            !Number.isSafeInteger(frozen.rollout_workers)||
            frozen.rollout_workers<1||frozen.rollout_workers>256)
            return null;
        const attempt=path.join(scratch,
            'native-collector-'+frozen.collection_id,frozen.bundle_directory);
        const measurements=[];
        const validCount=value=>Number.isSafeInteger(value)&&value>=0;
        const regularFile=file=>{
            let stat;
            try { stat=fs.lstatSync(file); }
            catch(error) { if(error.code==='ENOENT')return null; throw error; }
            if(!stat.isFile()||stat.isSymbolicLink()||
                !fs.realpathSync(file).startsWith(scratch+path.sep))
                throw Error('Native progress file is not an owned collector artifact.');
            return stat;
        };
        for(let index=0;index<frozen.rollout_workers;index++) {
            const directory=frozen.rollout_workers===1?attempt:
                path.join(attempt,'rollout-'+index);
            const progressFile=path.join(directory,'native-host-checkpoint.progress.json');
            const stat=regularFile(progressFile);
            if(!stat)continue;
            if(stat.size<1||stat.size>8192)
                throw Error('Native host progress exceeds its bounded projection.');
            const runFile=path.join(directory,'run.json');
            const sourceStat=regularFile(runFile);
            if(!sourceStat||sourceStat.size<1||sourceStat.size>512*1024)
                throw Error('Native host progress has no immutable run identity.');
            const run=JSON.parse(fs.readFileSync(runFile,'utf8'));
            const row=JSON.parse(fs.readFileSync(progressFile,'utf8'));
            if(row?.schema_version!==1||
                row.kind!=='native_host_checkpoint_progress'||
                !run||typeof run!=='object'||run.run_id!==row.run_id||
                typeof row.run_id!=='string'||!row.run_id||
                run.host_recovery_checkpoint!==path.join(directory,'native-host-checkpoint.json')||
                !['rounds','successful_world_steps','successful_policy_rows',
                    'completed_episodes','model_generation'].every(key=>validCount(row[key]))||
                typeof row.elapsed_wall_seconds!=='number'||
                !Number.isFinite(row.elapsed_wall_seconds)||
                row.elapsed_wall_seconds<0)
                throw Error('Native host progress is detached from its committed run identity.');
            measurements.push({row,stat});
        }
        if(!measurements.length)return null;
        const sum=key=>measurements.reduce((total,item)=>total+item.row[key],0);
        const steps=sum('successful_policy_rows'),worldSteps=sum('successful_world_steps');
        if(!validCount(steps)||!validCount(worldSteps))
            throw Error('Native collector progress exceeds its safe integer range.');
        const seconds=measurements.reduce(
            (largest,item)=>Math.max(largest,item.row.elapsed_wall_seconds),0);
        const newest=measurements.reduce((latest,item)=>
            item.stat.mtime>latest?item.stat.mtime:latest,
            new Date(0));
        return validateProgress({
            schema_version:1,source:'hub-metrics-v1',
            experiment_id:context.job_id,segment_id:'collection',
            source_modified_at:newest.toISOString(),
            steps_completed:steps,
            metrics:{
                rollout_world_steps:{value:worldSteps,unit:'count'},
                rollout_policy_rows:{value:steps,unit:'count'},
                rollout_completed_episodes:{
                    value:sum('completed_episodes'),unit:'count',
                },
                rollout_committed_rounds:{value:sum('rounds'),unit:'count'},
                rollout_checkpointed_workers:{
                    value:measurements.length,unit:'count',
                },
                rollout_elapsed_wall_seconds:{value:seconds,unit:'seconds'},
            },
        },PROJECT);
    }

    return {
        executable,
        cwd:tools,
        stop_policy:'terminate-group',
        config_sha256:configSha256,
        gpu_execution:'cuda',
        gpu_dual_execution:'native-rollouts',
        rocr_gpu_uuids,
        gpu_verify_args(_parameters,context) {
            const ids=context?.gpu_pci_addresses??[context?.gpu_pci_address];
            if(!Array.isArray(ids)||![1,2].includes(ids.length)||
                ids.some(pci=>!PCI.test(String(pci)))||
                new Set(ids).size!==ids.length)
                throw Error('Native portable collector needs one or two frozen PCI GPU identities.');
            return ['-B',gpuVerifier,...ids];
        },
        args(parameters,context) {
            const required=[
                'config_sha256','collector_id','collection_id','seed_offset',
                'rollout_workers','physics_workers','rollout_world_start',
                'rollout_worlds','bundle_directory','source_run_contract_sha256',
            ];
            const dualGpu=Array.isArray(context?.gpu_pci_addresses);
            const gpu=context?.gpu_pci_address!==undefined||dualGpu;
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).length!==required.length||
                required.some(key=>!Object.hasOwn(parameters,key))||
                parameters.config_sha256!==configSha256||
                !/^[A-Za-z0-9_-]{1,96}$/.test(String(parameters.collector_id||''))||
                !SHA.test(String(parameters.collection_id||''))||
                !SHA.test(String(parameters.source_run_contract_sha256||''))||
                !Number.isSafeInteger(parameters.seed_offset)||parameters.seed_offset<0||
                !Number.isSafeInteger(parameters.rollout_world_start)||
                parameters.rollout_world_start<0||
                !Number.isSafeInteger(parameters.rollout_worlds)||
                parameters.rollout_worlds<1||parameters.rollout_worlds>1024||
                !Number.isSafeInteger(parameters.rollout_workers)||
                parameters.rollout_workers<1||
                parameters.rollout_workers>parameters.rollout_worlds||
                !Number.isSafeInteger(parameters.physics_workers)||
                parameters.physics_workers<1||parameters.physics_workers>128||
                !/^[A-Za-z0-9_-]{1,96}$/.test(String(parameters.bundle_directory||''))||
                context?.continuation_of_job_id!=null||
                (dualGpu&&(
                    context.gpu_pci_addresses.length!==2||
                    context.gpu_pci_addresses.some(pci=>!PCI.test(String(pci)))||
                    new Set(context.gpu_pci_addresses).size!==2||
                    parameters.rollout_workers<2||parameters.rollout_worlds<2
                ))||
                (gpu&&(
                    context.gpu_mode!=='exclusive'||
                    (!dualGpu&&!PCI.test(String(context.gpu_pci_address)))
                ))||
                !Number.isSafeInteger(context?.cpu_quota)||
                parameters.rollout_workers*parameters.physics_workers>context.cpu_quota)
                throw Error('Native portable collector must fit the exact Hub-frozen CPU allocation and work order.');
            for(const [file,digest] of pinned) {
                if(sha256(fs.readFileSync(file))!==digest)
                    throw Error('Native portable collector runtime changed since adapter installation.');
            }
            return ['-B',runner,
                '--input-url',receiver,'--collector-id',parameters.collector_id,
                '--scratch',scratch,'--rollout-binary',binary,
                '--bundle-inbox',artifacts,'--token-file',credential,
                '--hub-job-id',context.job_id,
                '--cpu-quota',String(context.cpu_quota),
                '--expected-collection-id',parameters.collection_id,
                '--expected-bundle-directory',parameters.bundle_directory,
                '--expected-source-contract',parameters.source_run_contract_sha256,
                '--expected-seed-offset',String(parameters.seed_offset),
                '--expected-world-start',String(parameters.rollout_world_start),
                '--expected-worlds',String(parameters.rollout_worlds),
                '--expected-workers',String(parameters.rollout_workers),
                '--expected-physics-workers',String(parameters.physics_workers),
                ...(gpu?['--permit-leased-gpu',
                    ...(dualGpu?['--leased-gpu-count','2']:[])]:[])];
        },
        artifact_export_dir(jobId) {
            if(!UUID.test(String(jobId||'')))
                throw Error('Invalid native portable collector artifact identity.');
            return path.join(artifacts,jobId);
        },
        // Service exit is not collection completion. The executable may
        // have terminated after its World checkpoint but before durable
        // bundle publication. The learner independently verifies all
        // captured bytes before admitting the same bundle for optimization.
        completion_ready(context) {
            return reconcileLearnerDelivery(context);
        },
        readProgress(context) {
            const completed=verifiedReceipt(context);
            if(!completed)return committedRolloutProgress(context);
            const {receipt,stat}=completed,metrics=receipt.rollout_metrics;
            return validateProgress({
                schema_version:1,source:'hub-metrics-v1',
                experiment_id:context.job_id,segment_id:'collection',
                source_modified_at:stat.mtime.toISOString(),
                steps_completed:metrics.successful_policy_rows,
                metrics:{
                    rollout_world_steps:{value:metrics.successful_world_steps,unit:'count'},
                    rollout_policy_rows:{value:metrics.successful_policy_rows,unit:'count'},
                    rollout_wall_seconds:{value:metrics.wall_seconds,unit:'seconds'},
                    rollout_workers:{value:receipt.rollout_processes,unit:'count'},
                    rollout_physics_workers_per_process:{
                        value:receipt.physics_workers_per_process,unit:'count',
                    },
                    ...(Array.isArray(metrics.gpu_inference?.per_device)
                        ?Object.fromEntries(metrics.gpu_inference.per_device.flatMap(
                            (gpu,index)=>[
                                [`rollout_gpu_${index}_world_steps`,
                                    {value:gpu.successful_world_steps,unit:'count'}],
                                [`rollout_gpu_${index}_policy_rows`,
                                    {value:gpu.successful_policy_rows,unit:'count'}],
                                [`rollout_gpu_${index}_model_forwards`,
                                    {value:gpu.model_forwards,unit:'count'}],
                                [`rollout_gpu_${index}_agent_rows`,
                                    {value:gpu.gpu_agent_rows,unit:'count'}],
                            ],
                        )):{}),
                    ...(metrics.gpu_inference?{
                        rollout_gpu_verified_workers:{
                            value:metrics.gpu_inference.verified_workers,unit:'count',
                        },
                        rollout_gpu_model_forwards:{
                            value:metrics.gpu_inference.model_forwards,unit:'count',
                        },
                        rollout_gpu_agent_rows:{
                            value:metrics.gpu_inference.agent_rows,unit:'count',
                        },
                    }:{}),
                    rollout_world_steps_per_second:{
                        value:metrics.successful_world_steps/metrics.wall_seconds,unit:'rate',
                    },
                    rollout_policy_rows_per_second:{
                        value:metrics.successful_policy_rows/metrics.wall_seconds,unit:'rate',
                    },
                    ...(receipt.delivery?{
                        rollout_delivery_bytes:{value:receipt.delivery.bytes,unit:'bytes'},
                        rollout_delivery_seconds:{
                            value:receipt.delivery.copy_wall_seconds,unit:'seconds',
                        },
                    }:{}),
                },
            },PROJECT);
        },
    };
}

module.exports={createNativeBeesPortableCollectorAdapter,PORTABLE_CONFIG_SHA256};
