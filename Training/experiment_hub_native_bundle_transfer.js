'use strict';

// Authenticated native MA-POCA source transport through the existing Hub
// agent listener. Completed collectors supply immutable physical captures;
// verified checkpoint owners supply bounded model packages to exact pinned
// successors. Neither route admits executables or arbitrary machine paths.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const {safeLogPath,safeNativeCollectorPath,safeNativeModelPath,MAX_FILE,MAX_CHUNK}=
    require('./experiment_hub_artifact_sync');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA=/^[a-f0-9]{64}$/;
const CAPTURE=/^(?:rollout-(?:0|[1-9][0-9]?|1[0-9]{2}|2[0-4][0-9]|25[0-5])\/)?decisions\.bin$/;
const JOURNAL=/^(?:rollout-(?:0|[1-9][0-9]?|1[0-9]{2}|2[0-4][0-9]|25[0-5])\/)?events\.jsonl$/;
const RESERVE=2_000_000_000n;

function fail(message,status=409) {
    const error=Error(message);
    error.status=status;
    throw error;
}

function capturePaths(receipt) {
    if(receipt?.schema_version!==1||receipt.kind!=='native_rollout_bundle'||
        !Array.isArray(receipt.captures)||!receipt.captures.length||
        receipt.captures.length>256)
        fail('Invalid native collector bundle receipt.');
    const files=new Map();
    for(const capture of receipt.captures) {
        if(!capture||Object.keys(capture).length!==2||
            !Object.hasOwn(capture,'policy_capture')||
            !Object.hasOwn(capture,'notifications'))
            fail('Native collector has an invalid capture set.');
        for(const [key,pattern] of [['policy_capture',CAPTURE],['notifications',JOURNAL]]) {
            const value=capture[key],relative=value?.path,digest=value?.sha256;
            if(typeof relative!=='string'||!pattern.test(relative)||
                !safeNativeCollectorPath(relative)||!SHA.test(String(digest||''))||
                files.has(relative))
                fail('Native collector has an invalid or duplicate capture path.');
            files.set(relative,digest);
        }
    }
    return files;
}

function regular(file) {
    let stat;
    try{stat=fs.lstatSync(file);}
    catch{fail('Native collector artifact is not mirrored yet.');}
    if(!stat.isFile()||stat.isSymbolicLink()||stat.nlink!==1||
        fs.realpathSync(file)!==file||
        (typeof process.getuid==='function'&&stat.uid!==process.getuid()))
        fail('Native collector artifact was redirected.');
    return stat;
}

function mirroredFile(root,collectorId,relative,expected) {
    const key=crypto.createHash('sha256').update(relative).digest('hex');
    const stage=path.join(root,'hub-remote-artifact-staging',collectorId,key+'.json');
    const content=path.join(root,'hub-remote-artifacts',collectorId,
        ...relative.split('/'));
    const metaStat=regular(stage);
    if(metaStat.size<10||metaStat.size>1024)
        fail('Invalid collector artifact verification record.');
    let meta;
    try{meta=JSON.parse(fs.readFileSync(stage,'utf8'));}
    catch{fail('Corrupt collector artifact verification record.');}
    const body=regular(content);
    if(meta?.complete!==true||meta.relative!==relative||
        meta.sha!==expected||!Number.isSafeInteger(meta.size)||
        meta.size<1||meta.size>MAX_FILE||body.size!==meta.size)
        fail('Collector artifact is not a verified complete mirror.');
    return {file:content,size:body.size,sha:meta.sha};
}

function authorizedBundleFile(hub,machineId,token,learnerId,collectorId,relative) {
    hub.authenticateAgent(machineId,token);
    if(!UUID.test(String(learnerId||''))||!UUID.test(String(collectorId||''))||
        !safeNativeCollectorPath(relative))
        fail('Invalid native rollout artifact request.',400);
    const learner=hub.state.jobs.find(job=>job.id===learnerId);
    const collector=hub.state.jobs.find(job=>job.id===collectorId);
    if(!learner||!['bees_native','bees_native_optimizer_worker'].includes(learner.project_id)||
        learner.assigned_machine_id!==machineId||!learner.lease_id||
        !['waiting_adapter','start_requested','launch_requested','running']
            .includes(learner.status)||
        !collector||collector.project_id!=='bees_native_collect'||
        collector.status!=='finished'||!collector.assigned_machine_id||
        !collector.lease_id||!collector.execution_updated_at)
        fail('Native rollout access requires a running or reserved learner and completed collector.');
    const pins=learner.frozen_parameters?.rollout_bundles;
    const pin=Array.isArray(pins)&&pins.length<=256&&
        pins.find(entry=>entry?.job_id===collectorId);
    if(!pin||!SHA.test(String(pin.sha256||''))||
        pins.filter(entry=>entry?.job_id===collectorId).length!==1)
        fail('Learner does not pin this collector receipt.');
    const root=path.dirname(hub.file);
    const verified=mirroredFile(root,collectorId,'rollout-bundle.json',pin.sha256);
    if(verified.size>1024*1024)
        fail('Native collector receipt exceeds the bounded schema size.');
    let receipt;
    try{receipt=JSON.parse(fs.readFileSync(verified.file,'utf8'));}
    catch{fail('Invalid verified collector receipt JSON.');}
    const files=capturePaths(receipt);
    if(collector.model_source_job_id&&(
        !SHA.test(String(collector.expected_parent_model_sha256||''))||
        receipt.input_checkpoint_sha256!==collector.expected_parent_model_sha256))
        fail('Collector experience differs from its exact Hub predecessor checkpoint.');
    if(collector.frozen_parameters?.collection_id!==undefined&&
        receipt.collection_id!==collector.frozen_parameters.collection_id)
        fail('Collector bundle differs from its frozen work order.');
    if(collector.frozen_parameters?.source_run_contract_sha256!==undefined&&
        receipt.source_run_contract_sha256!==
            collector.frozen_parameters.source_run_contract_sha256)
        fail('Collector physical source contract changed.');
    if(collector.frozen_parameters?.rollout_world_start!==undefined&&
        receipt.partition?.world_start!==
            collector.frozen_parameters.rollout_world_start)
        fail('Collector World window differs from its frozen allocation.');
    if(collector.frozen_parameters?.rollout_worlds!==undefined&&
        receipt.partition?.world_count!==collector.frozen_parameters.rollout_worlds)
        fail('Collector World count differs from its frozen allocation.');
    if(relative==='rollout-bundle.json')return verified;
    const digest=files.get(relative);
    if(!digest)fail('File is not listed in this collector receipt.');
    return mirroredFile(root,collectorId,relative,digest);
}

// The same signed artifact mirror transports a complete model checkpoint
// package, but never exposes arbitrary source-machine paths. A destination
// must already be a Hub job explicitly pinned to the finished source model.
function mirroredByMetadata(root,jobId,relative) {
    const key=crypto.createHash('sha256').update(relative).digest('hex');
    const metaFile=path.join(root,'hub-remote-artifact-staging',jobId,key+'.json');
    if(regular(metaFile).size>1024)
        fail('Invalid native model artifact metadata.');
    let meta;
    try{meta=JSON.parse(fs.readFileSync(metaFile,'utf8'));}
    catch{fail('Corrupt native model artifact metadata.');}
    if(meta?.relative!==relative || meta.complete!==true ||
        !SHA.test(String(meta.sha||'')))
        fail('Native source artifact is not a completed mirror.');
    return mirroredFile(root,jobId,relative,meta.sha);
}

function nativeModelPackage(hub,source) {
    if(source?.project_id!=='bees_native'||source.status!=='finished'||
        source.checkpoint_ready_verified!==true||
        !SHA.test(String(source.checkpoint_model_sha256||''))||
        !source.capacity_released_at||!source.dispatch_id||
        !source.execution_updated_at)
        fail('A completed, released and checkpoint-verified native Bees model source is required.');
    if(source.frozen_parameters?.optimizer_group===true) {
        const bundle=source.resource_bundle;
        const members=hub.state.jobs.filter(job=>
            job.resource_bundle?.id===bundle?.id).sort((a,b)=>
            a.resource_bundle.index-b.resource_bundle.index);
        const proof=source.optimizer_completion;
        if(bundle?.index!==1||!Number.isSafeInteger(bundle.size)||
            bundle.size<2||bundle.size>8||members.length!==bundle.size||
            !SHA.test(String(proof?.input_sha256||''))||
            !SHA.test(String(proof?.model_consensus_sha256||''))||
            members.some((job,index)=>job.resource_bundle.index!==index+1||
                job.project_id!==(index===0?'bees_native':'bees_native_optimizer_worker')||
                job.status!=='finished'||!job.capacity_released_at||
                !job.dispatch_id||!job.execution_updated_at||
                job.optimizer_completion?.group_id!==bundle.id||
                job.optimizer_completion?.node_rank!==index||
                job.optimizer_completion?.node_count!==bundle.size||
                job.optimizer_completion?.input_sha256!==proof.input_sha256||
                job.optimizer_completion?.model_consensus_sha256!==
                    proof.model_consensus_sha256))
            fail('The original synchronized optimizer group lacks matching verified rank receipts and released native owners.');
    }
    const root=path.dirname(hub.file),jobId=source.id;
    const signed=mirroredByMetadata(root,jobId,'round-result.json');
    if(signed.size>64*1024)fail('Invalid native model completion receipt size.');
    let receipt;
    try{receipt=JSON.parse(fs.readFileSync(signed.file,'utf8'));}
    catch{fail('Invalid native model completion receipt.');}
    if(receipt?.schema_version!==1||
        receipt.kind!=='native_training_round_complete'||
        (source.frozen_parameters?.optimizer_group===true&&
            (receipt.learner_metrics?.optimizer_global_input_sha256!==
                source.optimizer_completion.input_sha256||
             receipt.learner_metrics?.optimizer_gpu_model_consensus_sha256!==
                source.optimizer_completion.model_consensus_sha256))||
        receipt.next_checkpoint_sha256!==source.checkpoint_model_sha256||
        !SHA.test(String(receipt.next_manifest_sha256||''))||
        receipt.learner_metrics?.next_checkpoint_sha256!==
            source.checkpoint_model_sha256||
        receipt.learner_metrics?.next_manifest_sha256!==
            receipt.next_manifest_sha256)
        fail('Mirrored native model completion disagrees with its Hub checkpoint identity.');
    const manifest=mirroredFile(root,jobId,'learner/manifest.json',
        receipt.next_manifest_sha256);
    if(manifest.size>1024*1024)fail('Native model manifest exceeds its bounded size.');
    let document;
    try{document=JSON.parse(fs.readFileSync(manifest.file,'utf8'));}
    catch{fail('Invalid mirrored native model manifest.');}
    // The publisher and receiver must agree on ONE model-set contract.
    // Reuse the receiver's slot and dependency validator before admitting a
    // purported completed checkpoint as a transferable Hub model source.
    const included=modelDependencies(document,source.checkpoint_model_sha256);
    included.set('learner/manifest.json',receipt.next_manifest_sha256);
    return {root,jobId,included};
}

// A completed optimizer model must be fully mirrored before any remote
// physical collector can be reserved for the next training round. Verify
// the checkpoint, manifest and every frozen opponent as one bounded package.
function verifiedNativeModelMirror(hub,source) {
    const model=nativeModelPackage(hub,source);
    for(const [relative,digest] of model.included)
        mirroredFile(model.root,model.jobId,relative,digest);
    return true;
}

// Queue-time source of truth for physical experience. Only terminal,
// machine-owned collector receipts that have been completely mirrored can
// become immutable optimizer inputs. Reuse the download authorization's
// capture schema and source work-order checks, never job-supplied file paths.
function verifiedNativeCollectorPin(hub,collectorId) {
    if(!UUID.test(String(collectorId||'')))fail('Invalid native collector job ID.',400);
    const collector=hub.state.jobs.find(job=>job.id===collectorId);
    if(collector?.project_id!=='bees_native_collect'||
        collector.status!=='finished'||!collector.assigned_machine_id||
        !collector.lease_id||!collector.dispatch_id||
        !collector.execution_updated_at||!collector.capacity_released_at)
        fail('Optimizer inputs require finished, released and authenticated native collectors.');
    const root=path.dirname(hub.file);
    const mirrored=mirroredByMetadata(root,collectorId,'rollout-bundle.json');
    if(mirrored.size>1024*1024)fail('Native collector receipt exceeds 1 MiB.');
    let receipt;
    try{receipt=JSON.parse(fs.readFileSync(mirrored.file,'utf8'));}
    catch{fail('Native collector receipt is not valid JSON.');}
    const captures=capturePaths(receipt);
    if(!SHA.test(String(receipt.input_checkpoint_sha256||''))||
        !SHA.test(String(receipt.input_manifest_sha256||''))||
        !SHA.test(String(receipt.source_run_contract_sha256||''))||
        !SHA.test(String(receipt.policy_worker_sha256||''))||
        (receipt.learner_slot!==null&&
            (!Number.isSafeInteger(receipt.learner_slot)||
             receipt.learner_slot<0||receipt.learner_slot>0xffffffff))||
        !receipt.partition||
        typeof receipt.partition.source_run_id!=='string'||
        !receipt.partition.source_run_id||
        !Number.isSafeInteger(receipt.partition.world_start)||
        receipt.partition.world_start<0||
        !Number.isSafeInteger(receipt.partition.world_count)||
        receipt.partition.world_count<1||
        receipt.partition.world_count>1024||
        receipt.partition.world_start+receipt.partition.world_count>
            Number.MAX_SAFE_INTEGER||
        !Number.isSafeInteger(receipt.seed_offset)||
        receipt.seed_offset<0)
        fail('Native collector model or physical partition identity is incomplete.');
    // These immutable native worker counts and physical GPU placements were
    // accepted by the machine's Hub lease before Start. The optimizer must
    // never ingest a different allocation merely because a bundle arrived
    // through a correctly authenticated artifact mirror.
    const cpus=collector.resources?.cpus;
    const gpuCount=collector.resources?.gpu_count??0;
    const workerCount=receipt.rollout_processes;
    const physicsCount=receipt.physics_workers_per_process;
    const rollout=receipt.rollout_metrics;
    const proof=rollout?.gpu_inference;
    const lease=hub.state.reservations.find(item=>
        item.id===collector.lease_id&&item.job_id===collector.id&&
        item.machine_id===collector.assigned_machine_id&&
        item.project_id===collector.project_id&&
        item.issued_by==='experiment-hub');
    const bindings=lease?.gpu_bindings||[];
    const pci=proof?.gpu_pci_addresses;
    if(!lease||lease.status!=='released'||!lease.released_at||
        !Number.isSafeInteger(cpus)||cpus<1||
        !Number.isSafeInteger(workerCount)||workerCount<1||
        workerCount>256||workerCount>receipt.partition.world_count||
        captures.size!==2*workerCount||
        !Number.isSafeInteger(physicsCount)||physicsCount<1||
        physicsCount>128||workerCount*physicsCount>cpus||
        !Number.isSafeInteger(rollout?.successful_world_steps)||
        rollout.successful_world_steps<1||
        !Number.isSafeInteger(rollout.successful_policy_rows)||
        rollout.successful_policy_rows<1||
        typeof rollout.wall_seconds!=='number'||
        !Number.isFinite(rollout.wall_seconds)||rollout.wall_seconds<=0||
        receipt.rollout_worlds!==receipt.partition.world_count||
        (collector.frozen_parameters?.rollout_workers!==undefined&&
            receipt.rollout_processes!==collector.frozen_parameters.rollout_workers)||
        (Number.isSafeInteger(collector.frozen_parameters?.physics_workers)&&
            physicsCount!==collector.frozen_parameters.physics_workers)||
        (gpuCount>0
            ?(![1,2].includes(gpuCount)||
                collector.resources?.gpu_mode!=='exclusive'||
                !Array.isArray(bindings)||bindings.length!==gpuCount||
                receipt.leased_gpu_inference!==true||
                (receipt.leased_gpu_count??1)!==gpuCount||
                workerCount<gpuCount||
                proof?.verified_workers!==workerCount||
                !Number.isSafeInteger(proof?.model_forwards)||
                proof.model_forwards<workerCount||
                !Number.isSafeInteger(proof?.agent_rows)||
                proof.agent_rows<proof.model_forwards||
                proof.agent_rows>rollout.successful_policy_rows||
                (pci!==undefined&&(
                    !Array.isArray(pci)||new Set(pci).size!==pci.length||
                    pci.some(address=>!bindings.some(bound=>bound.pci_address===address))
                ))||
                (gpuCount===2&&(
                    !Array.isArray(pci)||pci.length!==2||
                    bindings.some(bound=>!pci.includes(bound.pci_address))
                )))
            :(receipt.leased_gpu_inference===true||
                receipt.leased_gpu_count!==undefined)))
        fail('Native collector execution evidence exceeds or differs from its Hub CPU/GPU lease.');
    if(collector.model_source_job_id&&(
        !SHA.test(String(collector.expected_parent_model_sha256||''))||
        receipt.input_checkpoint_sha256!==collector.expected_parent_model_sha256))
        fail('Collector experience differs from its exact Hub predecessor checkpoint.');
    if(collector.frozen_parameters?.collection_id!==undefined&&
        receipt.collection_id!==collector.frozen_parameters.collection_id||
        collector.frozen_parameters?.source_run_contract_sha256!==undefined&&
        receipt.source_run_contract_sha256!==
            collector.frozen_parameters.source_run_contract_sha256||
        collector.frozen_parameters?.rollout_world_start!==undefined&&
        receipt.partition.world_start!==collector.frozen_parameters.rollout_world_start||
        collector.frozen_parameters?.rollout_worlds!==undefined&&
        receipt.partition.world_count!==collector.frozen_parameters.rollout_worlds||
        collector.frozen_parameters?.seed_offset!==receipt.seed_offset)
        fail('Collector receipt differs from its original frozen physical work order.');
    // Completed mirror metadata is durable SHA proof. Require every declared
    // capture and journal to be present, rather than queuing a GPU group that
    // cannot download its experience later.
    for(const [relative,digest] of captures)
        mirroredFile(root,collectorId,relative,digest);
    return {
        pin:{job_id:collectorId,sha256:mirrored.sha},
        model_sha256:receipt.input_checkpoint_sha256,
        manifest_sha256:receipt.input_manifest_sha256,
        source_contract_sha256:receipt.source_run_contract_sha256,
        policy_worker_sha256:receipt.policy_worker_sha256,
        learner_slot:receipt.learner_slot,
        seed_offset:receipt.seed_offset,
        partition:receipt.partition,
        // These are authenticated collector-receipt measurements, not a
        // fleet-wide wall clock. Distinct machines execute concurrently;
        // consumers must not divide summed steps by summed wall seconds
        // and call the result distributed training throughput.
        work:{
            successful_world_steps:rollout.successful_world_steps,
            successful_policy_rows:rollout.successful_policy_rows,
            collector_wall_seconds:rollout.wall_seconds,
            rollout_processes:workerCount,
            physics_workers_per_process:physicsCount,
            leased_gpu_count:gpuCount,
            ...(gpuCount>0?{
                verified_gpu_model_forwards:proof.model_forwards,
                verified_gpu_agent_rows:proof.agent_rows,
            }:{}),
        },
    };
}

function authorizedModelFile(hub,machineId,token,recipientId,sourceId,relative) {
    hub.authenticateAgent(machineId,token);
    if(!UUID.test(String(recipientId||''))||
        !UUID.test(String(sourceId||''))||recipientId===sourceId||
        !safeNativeModelPath(relative))
        fail('Invalid native model transfer identity.',400);
    const recipient=hub.state.jobs.find(job=>job.id===recipientId);
    const source=hub.state.jobs.find(job=>job.id===sourceId);
    if(!recipient||
        !['bees_native','bees_native_collect','bees_native_optimizer_worker']
            .includes(recipient.project_id)||
        recipient.assigned_machine_id!==machineId||!recipient.lease_id||
        recipient.model_source_job_id!==sourceId||
        !['waiting_adapter','start_requested','launch_requested','running']
            .includes(recipient.status)||
        recipient.expected_parent_model_sha256!==source?.checkpoint_model_sha256)
        fail('Native recipient does not hold this exact checkpoint handoff.');
    const packageFiles=nativeModelPackage(hub,source);
    const expected=packageFiles.included.get(relative);
    if(!expected)fail('Requested native model is not part of the original checkpoint manifest.');
    return mirroredFile(packageFiles.root,sourceId,relative,expected);
}

function bundleUrl(endpoint,machineId,learnerId,sourceId,relative,kind='bundle') {
    const url=new URL(endpoint);
    if(url.protocol!=='http:'||!['127.0.0.1','localhost'].includes(url.hostname))
        fail('Native transfer requires the authenticated loopback Hub tunnel.');
    if(!['bundle','model'].includes(kind))
        fail('Unknown native transfer kind.');
    url.pathname=kind==='model'
        ?'/api/hub/agent/native-model-file'
        :'/api/hub/agent/native-bundle-file';
    url.search='';
    for(const [key,value] of Object.entries({
        machine_id:machineId,learner_job_id:learnerId,
        [kind==='model'?'source_job_id':'collector_job_id']:sourceId,
        file:relative,
    }))url.searchParams.set(key,value);
    return url;
}

async function downloadFile(endpoint,machineId,token,learnerId,sourceId,
    relative,expectedSha,folder,kind='bundle') {
    if(kind==='model'?!safeNativeModelPath(relative):!safeNativeCollectorPath(relative))
        fail('Invalid native transfer path.');
    if(expectedSha===null
        ?kind!=='model'||relative!=='learner/manifest.json'
        :!SHA.test(String(expectedSha||'')))
        fail('Invalid native transfer artifact digest.');
    const url=bundleUrl(endpoint,machineId,learnerId,sourceId,relative,kind);
    const destination=path.join(folder,...relative.split('/'));
    fs.mkdirSync(path.dirname(destination),{recursive:true,mode:0o700});
    const fd=fs.openSync(destination,
        fs.constants.O_WRONLY|fs.constants.O_CREAT|fs.constants.O_EXCL|
            (fs.constants.O_NOFOLLOW||0),0o600);
    let ok=false,receivedSha;
    try {
        receivedSha=await new Promise((resolve,reject)=>{
            const request=http.request(url,{
                method:'GET',timeout:60000,
                headers:{Host:'127.0.0.1:7149',
                    'X-Hub-Agent-Token':token},
            },async response=>{
                if(response.statusCode!==200) {
                    response.resume();
                    return reject(Error('Native artifact transfer rejected: HTTP '+
                        response.statusCode));
                }
                const size=Number(response.headers['content-length']);
                const abort=message=>{
                    response.destroy();
                    reject(Error(message));
                };
                if(!Number.isSafeInteger(size)||size<1||size>MAX_FILE||
                    ['rollout-bundle.json','learner/manifest.json'].includes(relative)&&
                        size>1024*1024)
                    return abort('Invalid native artifact transfer size.');
                const advertised=response.headers[
                    kind==='model'?'x-native-model-sha256':'x-native-bundle-sha256'];
                if(!SHA.test(String(advertised||''))||
                    (expectedSha!==null&&advertised!==expectedSha))
                    return abort('Native artifact differs from its authorized SHA-256.');
                let free;
                try {
                    const volume=fs.statfsSync(folder,{bigint:true});
                    free=BigInt(volume.bavail)*BigInt(volume.bsize);
                }catch{return abort('Native transfer requires verified free disk space.');}
                if(free<RESERVE+BigInt(size))
                    return abort('Native artifact transfer violates disk reserve.');
                const digest=crypto.createHash('sha256');
                let offset=0;
                try {
                    for await(const chunk of response) {
                        offset+=chunk.length;
                        if(offset>size)throw Error('Native transfer exceeded its advertised length.');
                        digest.update(chunk);
                        let position=0;
                        while(position<chunk.length) {
                            const written=fs.writeSync(fd,chunk,position,
                                chunk.length-position,offset-chunk.length+position);
                            if(written<1)throw Error('Native transfer file write stopped.');
                            position+=written;
                        }
                    }
                    if(offset!==size||digest.digest('hex')!==advertised)
                        throw Error('Native artifact content differs from its pinned SHA-256.');
                    fs.fsyncSync(fd);
                    resolve(advertised);
                }catch(error){reject(error);}
            });
            request.on('timeout',()=>request.destroy(Error('Native transfer timed out.')));
            request.on('error',reject);
            request.end();
        });
        ok=true;
    }finally {
        fs.closeSync(fd);
        if(!ok)fs.unlinkSync(destination);
    }
    return receivedSha;
}

async function verifyLocalBundle(directory,expectedReceiptSha) {
    const receiptFile=path.join(directory,'rollout-bundle.json');
    if(!fs.existsSync(receiptFile)||regular(receiptFile).size>1024*1024)
        return false;
    const sha=async file=>{
        const digest=crypto.createHash('sha256');
        for await(const part of fs.createReadStream(file,{highWaterMark:MAX_CHUNK}))
            digest.update(part);
        return digest.digest('hex');
    };
    if(await sha(receiptFile)!==expectedReceiptSha)return false;
    const receipt=JSON.parse(fs.readFileSync(receiptFile,'utf8'));
    const files=capturePaths(receipt);
    for(const [relative,digest] of files) {
        const file=path.join(directory,...relative.split('/'));
        if(!fs.existsSync(file)||regular(file).size>MAX_FILE||
            await sha(file)!==digest)return false;
    }
    return true;
}

async function fileDigest(file) {
    const digest=crypto.createHash('sha256');
    for await(const part of fs.createReadStream(file,{highWaterMark:MAX_CHUNK}))
        digest.update(part);
    return digest.digest('hex');
}

// Every next-round native actor, collector and optimizer rank consumes the
// full portable model set. The checkpoint alone would silently discard fixed
// opponents, and a manifest alone could refer to another machine's paths.
function modelDependencies(document,expectedCheckpoint) {
    if(document?.format!=='bees-native-model-v1'||
        document.abi_version!==26||
        typeof document.policy_signature!=='string'||
        !Buffer.byteLength(document.policy_signature,'utf8')||
        Buffer.byteLength(document.policy_signature,'utf8')>8192||
        !Array.isArray(document.models)||!document.models.length||
        document.models.length>256)
        fail('Invalid native model package manifest.');
    const dependencies=new Map([['learner/checkpoint.pt',expectedCheckpoint]]);
    const slots=new Set();
    let learnerRefs=0;
    for(const model of document.models) {
        const digest=model?.sha256,relative=model?.path;
        if(!Number.isSafeInteger(model?.slot)||model.slot<0||
            model.slot>0xffffffff||slots.has(model.slot)||
            !['mlagents','onnx','torchscript'].includes(model.backend)||
            !SHA.test(String(digest||''))||typeof relative!=='string')
            fail('Invalid native model package dependency or duplicate model slot.');
        slots.add(model.slot);
        if(relative==='checkpoint.pt') {
            if(digest!==expectedCheckpoint||model.backend!=='mlagents')
                fail('Native package learner checkpoint identity changed.');
            learnerRefs++;
        }else if(/^opponents\/[a-f0-9]{64}\.model$/.test(relative)&&
                relative==='opponents/'+digest+'.model') {
            dependencies.set('learner/'+relative,digest);
        }else fail('Native model package contains an unapproved artifact path.');
    }
    if(!learnerRefs)fail('Native model package has no committed learner checkpoint.');
    return dependencies;
}

async function verifyLocalModelPackage(directory,expectedCheckpoint) {
    const folder=path.join(directory,'learner');
    const manifest=path.join(folder,'manifest.json');
    if(!fs.existsSync(manifest)||regular(manifest).size>1024*1024)
        return false;
    const document=JSON.parse(fs.readFileSync(manifest,'utf8'));
    const dependencies=modelDependencies(document,expectedCheckpoint);
    dependencies.set('learner/manifest.json',null);
    for(const [relative,digest] of dependencies) {
        const file=path.join(directory,...relative.split('/'));
        if(!fs.existsSync(file)||regular(file).size>MAX_FILE)
            return false;
        if(digest!==null&&await fileDigest(file)!==digest)return false;
    }
    return true;
}

async function syncNativeModels(job,adapter,endpoint,machineId,token) {
    const sourceId=job?.model_source_job_id;
    if(sourceId==null)return {models:0};
    // A checkpoint-continuing owner is pinned by Hub to its predecessor's
    // original machine and artifact lineage. Its installed native adapter
    // verifies those source bytes locally before Start. Forcing this owner
    // through the remote-model inbox would fail when the machine has no
    // inbox installed, and waste disk/network copying its own checkpoint.
    // Remote worker ranks still require the authenticated mirrored package.
    if(job.project_id==='bees_native'&&
        job.continuation_of_job_id===sourceId)
        return {models:0,local_checkpoint:true};
    if(!['bees_native','bees_native_collect','bees_native_optimizer_worker']
        .includes(job?.project_id)||
        !UUID.test(String(sourceId))||sourceId===job.id||
        !SHA.test(String(job.expected_parent_model_sha256||''))||
        typeof adapter?.model_inbox_dir!=='function')
        fail('A pinned native model successor requires its locally approved model inbox.');
    const inbox=adapter.model_inbox_dir();
    if(typeof inbox!=='string'||!path.isAbsolute(inbox)||
        fs.realpathSync(inbox)!==inbox||!fs.lstatSync(inbox).isDirectory())
        fail('Native model inbox directory was redirected.');
    const destination=path.join(inbox,sourceId);
    // Always fetch the small, authenticated source manifest before accepting
    // a cached model package. Checkpoint SHA alone does not identify the
    // frozen opponents or model routing in a previously staged manifest.
    // This retains the expensive checkpoint bytes on the destination machine
    // when the complete manifest and every dependency are unchanged.
    const stage=fs.mkdtempSync(path.join(inbox,'.native-model-'));
    try {
        const manifestSha=await downloadFile(endpoint,machineId,token,job.id,sourceId,
            'learner/manifest.json',null,stage,'model');
        if(fs.existsSync(destination)) {
            const cached=path.join(destination,'learner','manifest.json');
            if(!fs.existsSync(cached)||regular(cached).size>1024*1024||
                await fileDigest(cached)!==manifestSha||
                !await verifyLocalModelPackage(destination,job.expected_parent_model_sha256))
                fail('Existing native model package differs from its authenticated source manifest.');
            return {models:1};
        }
        const document=JSON.parse(fs.readFileSync(
            path.join(stage,'learner','manifest.json'),'utf8'));
        const dependencies=modelDependencies(document,job.expected_parent_model_sha256);
        for(const [relative,digest] of dependencies)
            await downloadFile(endpoint,machineId,token,job.id,sourceId,
                relative,digest,stage,'model');
        if(!await verifyLocalModelPackage(stage,job.expected_parent_model_sha256))
            fail('Transferred native model package is incomplete or differs from its SHA-256 manifest.');
        if(fs.existsSync(destination))
            fail('Native model package was published concurrently; refusing overwrite.');
        fs.renameSync(stage,destination);
    }finally {
        if(fs.existsSync(stage))fs.rmSync(stage,{recursive:true,force:true});
    }
    return {models:1};
}

async function syncNativeBundles(job,adapter,endpoint,machineId,token) {
    const pins=job?.frozen_parameters?.rollout_bundles;
    if(!['bees_native','bees_native_optimizer_worker'].includes(job?.project_id)||
        !Array.isArray(pins)||
        !pins.length)return {bundles:0};
    if(pins.length>256||typeof adapter?.bundle_inbox_dir!=='function')
        fail('A native bundle learner requires its installed private inbox.');
    const inbox=adapter.bundle_inbox_dir();
    if(typeof inbox!=='string'||!path.isAbsolute(inbox)||
        fs.realpathSync(inbox)!==inbox||!fs.lstatSync(inbox).isDirectory())
        fail('Native bundle inbox was redirected.');
    const seen=new Set();
    let delivered=0;
    for(const pin of pins) {
        if(!UUID.test(String(pin?.job_id||''))||
            !SHA.test(String(pin?.sha256||''))||seen.has(pin.job_id))
            fail('Invalid or repeated native collector receipt pin.');
        seen.add(pin.job_id);
        const destination=path.join(inbox,pin.job_id);
        if(fs.existsSync(destination)) {
            if(!await verifyLocalBundle(destination,pin.sha256))
                fail('Existing native collector bundle differs from its pinned artifacts.');
            delivered++;continue;
        }
        const stage=fs.mkdtempSync(path.join(inbox,'.native-bundle-'));
        try {
            await downloadFile(endpoint,machineId,token,job.id,pin.job_id,
                'rollout-bundle.json',pin.sha256,stage);
            const receipt=JSON.parse(fs.readFileSync(
                path.join(stage,'rollout-bundle.json'),'utf8'));
            for(const [relative,digest] of capturePaths(receipt))
                await downloadFile(endpoint,machineId,token,job.id,pin.job_id,
                    relative,digest,stage);
            if(!await verifyLocalBundle(stage,pin.sha256))
                fail('Staged native collector bundle failed final verification.');
            if(fs.existsSync(destination))
                fail('A native bundle was published concurrently; do not overwrite it.');
            fs.renameSync(stage,destination);
            delivered++;
        }finally {
            if(fs.existsSync(stage))fs.rmSync(stage,{recursive:true,force:true});
        }
    }
    return {bundles:delivered};
}

module.exports={capturePaths,authorizedBundleFile,authorizedModelFile,
    verifiedNativeCollectorPin,verifiedNativeModelMirror,
    syncNativeBundles,syncNativeModels,nativeModelPackage};
