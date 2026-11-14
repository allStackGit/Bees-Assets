'use strict';

// Immutable native MA-POCA rollout receipts and captures transported through
// the machine-authenticated Hub listener. Only completed collector jobs can
// supply bytes, and only a learner explicitly pinning that collector's
// receipt SHA may retrieve them. No executable, checkpoint or model transfer.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const {safeLogPath,safeNativeCollectorPath,MAX_FILE,MAX_CHUNK}=
    require('./experiment_hub_artifact_sync');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA=/^[a-f0-9]{64}$/;
const CAPTURE=/^(?:rollout-(?:0|[1-9][0-9]{0,2})\/)?decisions\.bin$/;
const JOURNAL=/^(?:rollout-(?:0|[1-9][0-9]{0,2})\/)?events\.jsonl$/;
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
    if(!learner||learner.project_id!=='bees_native'||
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
    let receipt;
    try{receipt=JSON.parse(fs.readFileSync(verified.file,'utf8'));}
    catch{fail('Invalid verified collector receipt JSON.');}
    const files=capturePaths(receipt);
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

function bundleUrl(endpoint,machineId,learnerId,collectorId,relative) {
    const url=new URL(endpoint);
    if(url.protocol!=='http:'||!['127.0.0.1','localhost'].includes(url.hostname))
        fail('Native bundle transfer requires the authenticated loopback Hub tunnel.');
    url.pathname='/api/hub/agent/native-bundle-file';
    url.search='';
    for(const [key,value] of Object.entries({
        machine_id:machineId,learner_job_id:learnerId,
        collector_job_id:collectorId,file:relative,
    }))url.searchParams.set(key,value);
    return url;
}

async function downloadFile(endpoint,machineId,token,learnerId,collectorId,
    relative,expectedSha,folder) {
    const url=bundleUrl(endpoint,machineId,learnerId,collectorId,relative);
    const destination=path.join(folder,...relative.split('/'));
    fs.mkdirSync(path.dirname(destination),{recursive:true,mode:0o700});
    const fd=fs.openSync(destination,
        fs.constants.O_WRONLY|fs.constants.O_CREAT|fs.constants.O_EXCL|
            (fs.constants.O_NOFOLLOW||0),0o600);
    let ok=false;
    try {
        await new Promise((resolve,reject)=>{
            const request=http.request(url,{
                method:'GET',timeout:60000,
                headers:{Host:'127.0.0.1:7149',
                    'X-Hub-Agent-Token':token},
            },async response=>{
                if(response.statusCode!==200) {
                    response.resume();
                    return reject(Error('Native bundle transfer rejected: HTTP '+
                        response.statusCode));
                }
                const size=Number(response.headers['content-length']);
                if(!Number.isSafeInteger(size)||size<1||size>MAX_FILE||
                    response.headers['x-native-bundle-sha256']!==expectedSha)
                    return reject(Error('Invalid native bundle file identity.'));
                let free;
                try {
                    const volume=fs.statfsSync(folder,{bigint:true});
                    free=BigInt(volume.bavail)*BigInt(volume.bsize);
                }catch{return reject(Error('Native bundle requires verified free disk space.'));}
                if(free<RESERVE+BigInt(size))
                    return reject(Error('Native bundle transfer violates disk reserve.'));
                const digest=crypto.createHash('sha256');
                let offset=0;
                try {
                    for await(const chunk of response) {
                        offset+=chunk.length;
                        if(offset>size)throw Error('Native bundle response exceeded its advertised length.');
                        digest.update(chunk);
                        let position=0;
                        while(position<chunk.length) {
                            const written=fs.writeSync(fd,chunk,position,
                                chunk.length-position,offset-chunk.length+position);
                            if(written<1)throw Error('Native bundle file write stopped.');
                            position+=written;
                        }
                    }
                    if(offset!==size||digest.digest('hex')!==expectedSha)
                        throw Error('Native bundle content differs from its pinned SHA-256.');
                    fs.fsyncSync(fd);
                    resolve();
                }catch(error){reject(error);}
            });
            request.on('timeout',()=>request.destroy(Error('Native bundle transfer timed out.')));
            request.on('error',reject);
            request.end();
        });
        ok=true;
    }finally {
        fs.closeSync(fd);
        if(!ok)fs.unlinkSync(destination);
    }
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

async function syncNativeBundles(job,adapter,endpoint,machineId,token) {
    const pins=job?.frozen_parameters?.rollout_bundles;
    if(job?.project_id!=='bees_native'||!Array.isArray(pins)||
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

module.exports={capturePaths,authorizedBundleFile,syncNativeBundles};
