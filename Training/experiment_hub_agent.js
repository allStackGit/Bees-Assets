#!/usr/bin/env node
'use strict';

// Cross-platform Experiment Hub agent. Observation-only by default.
// Native workload ownership requires explicit machine-local adapter opt-in;
// it cannot accept executable paths or shell commands from the Hub.
const http = require('node:http');
const os = require('node:os');
const crypto=require('node:crypto');
const {inspectHost,availableMemoryMb}=require('./experiment_hub_host');
const {discoverAmdGpus,discoverNvidiaGpus}=require('./experiment_hub_gpu_inventory');
const {fromEnvironment}=require('./experiment_hub_execution');
const {syncArtifacts}=require('./experiment_hub_artifact_agent');
const {syncNativeBundles}=require('./experiment_hub_native_bundle_transfer');
const {MAX_BODY_BYTES}=require('./experiment_hub_agent_listener');
const {request: resourceRequest}=require('./experiment_hub_resources');

const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const TOKEN = /^[0-9a-f]{64}$/;
const INTERVAL_MS = 15000;
let lastCpuSample=null;
let lastCpuBusyPct=null;

// Read global host counters, including Bees and independently started
// programs. A native job's CPU accounting is separate and must not be
// added to this figure. Samples closer than 5 seconds retain the last rate.
function hostCpuBusyPct(hostOs=os,nowMs=Date.now()) {
    const cores=hostOs.cpus();
    if(!Array.isArray(cores)||!cores.length)return null;
    let total=0,idle=0;
    for(const core of cores) {
        const times=core?.times;
        if(!times)return null;
        for(const key of ['user','nice','sys','idle','irq']) {
            const value=times[key];
            if(!Number.isFinite(value)||value<0)return null;
            total+=value;
        }
        idle+=times.idle;
    }
    const next={at:nowMs,cores:cores.length,total,idle};
    if(!lastCpuSample||lastCpuSample.cores!==next.cores||
        nowMs<=lastCpuSample.at) {
        lastCpuSample=next;
        lastCpuBusyPct=null;
        return null;
    }
    if(nowMs-lastCpuSample.at<5000)return lastCpuBusyPct;
    const elapsed=total-lastCpuSample.total,notBusy=idle-lastCpuSample.idle;
    lastCpuSample=next;
    if(elapsed<=0||notBusy<0||notBusy>elapsed) {
        lastCpuBusyPct=null;
        return null;
    }
    lastCpuBusyPct=Math.round((1-notBusy/elapsed)*10000)/100;
    return lastCpuBusyPct;
}

function endpoint(input) {
    const url = new URL(input || 'http://127.0.0.1:7149');
    if (url.protocol !== 'http:' ||
        !['127.0.0.1','localhost'].includes(url.hostname) ||
        url.username || url.password || url.search || url.hash ||
        !['','/'].includes(url.pathname)) {
        throw Error('Agent endpoint must be a local HTTP address; use a trusted SSH tunnel for remote machines.');
    }
    url.pathname='/api/hub/agent/heartbeat';
    return url;
}

function observations(machineId) {
    if (typeof machineId !== 'string' || !ID.test(machineId))
        throw Error('EXPERIMENT_HUB_MACHINE_ID must match a registered machine ID.');
    const platform=os.platform();
    // Missing or temporarily unreadable GPU telemetry blocks new GPU
    // claims, but must not disconnect healthy CPU-only native services.
    let gpus=[];
    if(platform==='linux') {
        try{gpus=discoverAmdGpus();}
        catch{gpus=[];}
    }else if(platform==='win32') {
        try{gpus=discoverNvidiaGpus();}
        catch(error){
            gpus=[];
        }
    }
    return {
        machine_id:machineId,
        cpus:Math.max(1,typeof os.availableParallelism==='function'
            ?os.availableParallelism():os.cpus().length),
        ram_mb:Math.max(128,Math.floor(os.totalmem()/1048576)),
        free_ram_mb:Math.max(0,Math.floor(os.freemem()/1048576)),
        available_ram_mb:availableMemoryMb(),
        platform,
        execution:inspectHost(),
        // Both inventories are physical observations, not execution quotas.
        gpu_inventory_source:platform==='linux'?'amdgpu-sysfs-v1':
            platform==='win32'?'nvidia-smi-v1':'none',
        gpu_devices:gpus,
    };
}

function observedOffers(offers,observation) {
    if (!Array.isArray(offers)||offers.length>100)
        throw Error('Invalid coordination lease offer list.');
    const ids=new Set(),budgets=new Map();
    let claimedCpus=0,claimedRam=0;
    for (const offer of offers) {
        const r=offer?.resources;
        if (!offer || typeof offer.id!=='string' ||
            !/^[0-9a-f-]{36}$/.test(offer.id) ||
            !['offered','acknowledged'].includes(offer.status) ||
            ids.has(offer.id) || !r ||
            !Number.isSafeInteger(r.cpus) || r.cpus<1 ||
            !Number.isSafeInteger(r.ram_mb) || r.ram_mb<1)
            throw Error('Invalid coordination lease offer.');
        // A malformed persisted GPU intent is not a CPU-only claim,
        // even when its advertised GPU count is zero.
        let budget;
        try{budget=resourceRequest(r);}
        catch{throw Error('Invalid coordination lease resource budget.');}
        budgets.set(offer.id,budget);
        ids.add(offer.id);
        claimedCpus+=r.cpus;
        claimedRam+=r.ram_mb;
    }
    // Do not claim to have seen a workload arrangement beyond observed
    // CPU/RAM capacity. Physical availability and GPU identity are unproven.
    if (claimedCpus>observation.cpus || claimedRam>observation.ram_mb)
        return [];
    const deviceByPci=new Map((observation.gpu_devices||[])
        .map(g=>[g.pci_address,g]));
    const gpuClaims=new Map(),approved=[];
    for(const offer of offers) {
        // Use the already-validated canonical budget. Older CPU-only
        // leases can legitimately omit gpu_count and gpus altogether.
        const r=budgets.get(offer.id);
        if(r.gpu_count===0) {
            approved.push(offer.id);
            continue;
        }
        if(!['amdgpu-sysfs-v1','nvidia-smi-v1'].includes(observation.gpu_inventory_source)||
            !Number.isSafeInteger(r.gpu_count)||r.gpu_count<1||
            !Array.isArray(r.gpus)||r.gpus.length!==r.gpu_count||
            !Array.isArray(offer.gpu_bindings)||
            offer.gpu_bindings.length!==r.gpu_count)
            continue;
        const proposed=[];
        let safe=true;
        for(const id of r.gpus) {
            const binding=offer.gpu_bindings.find(g=>g?.id===id);
            const physical=deviceByPci.get(binding?.pci_address);
            if(!binding||!physical||
                (observation.gpu_inventory_source==='nvidia-smi-v1'&&
                    (!binding.cuda_uuid||binding.cuda_uuid!==physical.cuda_uuid))||
                !Number.isSafeInteger(binding.vram_mb)||
                binding.vram_mb<1||binding.vram_mb>physical.vram_mb||
                binding.vram_mb<(r.min_gpu_vram_mb||0)||
                proposed.some(p=>p.pci===binding.pci_address)) {
                safe=false;break;
            }
            const previous=gpuClaims.get(binding.pci_address)||
                {exclusive:false,used_vram:0,used_compute:0};
            const perDevice=r.gpu_mode==='shared'?r.gpu_vram_mb:binding.vram_mb;
            const compute=r.gpu_mode==='shared'?r.gpu_compute_pct:100;
            if(previous.exclusive||
                !Number.isSafeInteger(perDevice)||perDevice<1||
                !Number.isSafeInteger(compute)||compute<1||
                previous.used_vram+perDevice>binding.vram_mb||
                previous.used_compute+compute>100||
                (r.gpu_mode==='exclusive'&&previous.used_compute!==0)) {
                safe=false;break;
            }
            proposed.push({pci:binding.pci_address,
                exclusive:r.gpu_mode!=='shared',
                used_vram:previous.used_vram+perDevice,
                used_compute:previous.used_compute+compute});
        }
        if(!safe)continue;
        for(const next of proposed)gpuClaims.set(next.pci,{
            exclusive:next.exclusive,
            used_vram:next.used_vram,used_compute:next.used_compute,
        });
        approved.push(offer.id);
    }
    return approved;
}

function sendHeartbeat(url,machineId,token,observedLeaseIds=[],
    executionReports=[],advertisement={},gpuQualificationReports=[],
    requestExecutionOrders=true,closeAgentSession=false) {
    if(typeof requestExecutionOrders!=='boolean')
        return Promise.reject(Error('Native instruction request preference must be boolean.'));
    if(typeof closeAgentSession!=='boolean'||
        (closeAgentSession&&requestExecutionOrders))
        return Promise.reject(Error('Agent sign-off requires native instruction delivery disabled.'));
    if (typeof token !== 'string' || !TOKEN.test(token))
        return Promise.reject(Error('Missing/invalid EXPERIMENT_HUB_AGENT_TOKEN.'));
    if (!Array.isArray(observedLeaseIds) || observedLeaseIds.length>100 ||
        observedLeaseIds.some(id=>typeof id!=='string' ||
            !/^[0-9a-f-]{36}$/.test(id)))
        return Promise.reject(Error('Invalid lease acknowledgement IDs.'));
    if(!Array.isArray(executionReports)||executionReports.length>50||
        !Array.isArray(gpuQualificationReports)||
        gpuQualificationReports.length>4||
        !advertisement||typeof advertisement!=='object')
        return Promise.reject(Error('Invalid local execution report batch.'));
    const payload=JSON.stringify({
        ...observations(machineId),host_cpu_busy_pct:hostCpuBusyPct(),
        observed_lease_ids:observedLeaseIds,
        request_execution_orders:requestExecutionOrders,
        close_agent_session:closeAgentSession,
        execution_reports:executionReports,
        gpu_qualification_reports:gpuQualificationReports,
        local_executor:advertisement.local_executor===true,
        installed_projects:advertisement.installed_projects||[],
        evaluation_suites:advertisement.evaluation_suites||{},
        tinybrains_profiles:advertisement.tinybrains_profiles||{},
        tinybrains_cuda_profiles:advertisement.tinybrains_cuda_profiles||[],
        tinybrains_thread_overrides:advertisement.tinybrains_thread_overrides||[],
        tinybrains_cpu_fallback_profiles:advertisement.tinybrains_cpu_fallback_profiles||[],
        tinybrains_required_import_profiles:advertisement.tinybrains_required_import_profiles||[],
        tinybrains_remote_rollout_profiles:advertisement.tinybrains_remote_rollout_profiles||[],
        tinybrains_remote_rollout_count:advertisement.tinybrains_remote_rollout_count||0,
        tinybrains_remote_rollout_endpoints:advertisement.tinybrains_remote_rollout_endpoints||[],
        tinybrains_remote_key_fingerprint:advertisement.tinybrains_remote_key_fingerprint||null,
        tinybrains_rollout_endpoint:advertisement.tinybrains_rollout_endpoint||null,
        tinybrains_rollout_key_fingerprint:advertisement.tinybrains_rollout_key_fingerprint||null,
        tinybrains_rollout_thread_limit:advertisement.tinybrains_rollout_thread_limit||0,
        tinybrains_dual_gpu_profiles:advertisement.tinybrains_dual_gpu_profiles||[],
        tinybrains_imports:advertisement.tinybrains_imports||{},
        gpu_projects:advertisement.gpu_projects||[],
        gpu_shared_projects:advertisement.gpu_shared_projects||[],
        native_preflight_passed:advertisement.native_preflight_passed===true,
        native_preflight_reason:advertisement.native_preflight_reason||null,
        agent_instance_id:advertisement.instance_id||null,
    });
    // Progress fits its own bounded envelope; the complete heartbeat
    // separately includes physical inventory, leases and native receipts.
    if(Buffer.byteLength(payload)>MAX_BODY_BYTES)
        return Promise.reject(Error('Agent heartbeat exceeds '+MAX_BODY_BYTES+
            ' bytes, including native reports and machine observations.'));
    return new Promise((resolve,reject)=>{
        const req=http.request(url,{
            method:'POST',timeout:10000,
            headers:{
                // The manager remains loopback-bound and expects its own
                // origin. This works with a local SSH forwarded port.
                Host:'127.0.0.1:7149',
                'Content-Type':'application/json',
                'Content-Length':Buffer.byteLength(payload),
                'X-Hub-Agent-Token':token,
            },
        },res=>{
            let text='';
            res.setEncoding('utf8');
            res.on('data',chunk=>{
                text+=chunk;
                if(text.length>200000)req.destroy(Error('Oversized hub reply.'));
            });
            res.on('end',()=>{
                let parsed;
                try{parsed=JSON.parse(text);}catch{reject(Error('Invalid hub response.'));return;}
                if(res.statusCode!==200||parsed?.status!=='connected') {
                    reject(Error(parsed?.error||'Agent heartbeat refused: HTTP '+res.statusCode));
                    return;
                }
                resolve(parsed);
            });
        });
        req.on('timeout',()=>req.destroy(Error('Agent heartbeat timed out.')));
        req.on('error',reject);
        req.end(payload);
    });
}

async function main() {
    const url=endpoint(process.env.EXPERIMENT_HUB_URL);
    const id=process.env.EXPERIMENT_HUB_MACHINE_ID;
    const token=process.env.EXPERIMENT_HUB_AGENT_TOKEN;
    observations(id);
    if(!TOKEN.test(token||''))throw Error('Set the one-time enrolled agent credential in EXPERIMENT_HUB_AGENT_TOKEN.');
    // Disabled unless the operator explicitly configures local adapter code,
    // a private supervisor ledger and the opt-in execution environment flag.
    const executor=fromEnvironment(process.env);
    const syncLogs=process.env.EXPERIMENT_HUB_SYNC_ARTIFACTS==='1';
    if(syncLogs&&!executor)
        throw Error('Log sync requires an opted-in native execution adapter.');
    let nativePreflightReason=null;
    if(executor?.projects.length) {
        try {
            await executor.supervisor.qualify();
        }catch(error) {
            nativePreflightReason=String(error.message||error).slice(0,180);
            console.error('[experiment-hub-agent] Native resource preflight: '+
                nativePreflightReason);
        }
    }
    const advertisement={
        ...(executor?executor.advertisement():{}),
        native_preflight_passed:Boolean(executor?.supervisor.qualifiedAt),
        native_preflight_reason:nativePreflightReason,
        instance_id:crypto.randomUUID(),
    };
    let active=false,executingNative=false,busyHeartbeat=null,
        last='initial',receipts=[],pendingReports=[],pendingGpuReports=[];
    const artifactCache=new Map();
    let artifactSync=null;
    const collectArtifacts=()=>{
        if(!syncLogs||!executor||active||executingNative||artifactSync)
            return;
        const work=syncArtifacts(executor,url,id,token,artifactCache)
            .catch(error=>console.error('[experiment-hub-agent] Artifact mirror: '+
                String(error.message||error).slice(0,180)))
            .finally(()=>{if(artifactSync===work)artifactSync=null;});
        artifactSync=work;
    };
    const once=process.argv.includes('--once');
    const poll=async(expedite=false)=>{
        if(active) {
            if(executingNative&&!busyHeartbeat) {
                // The existing interval must maintain machine liveness while
                // a bounded systemd/Job Object command is still running.
                // Do not deliver another instruction or replay an unflushed
                // report. Its native owner remains the only process authority.
                const inFlight=sendHeartbeat(url,id,token,receipts,[],
                    advertisement,[],false)
                    .catch(error=>console.error('[experiment-hub-agent] Busy heartbeat: '+
                        String(error.message||error)))
                    .finally(()=>{if(busyHeartbeat===inFlight)busyHeartbeat=null;});
                busyHeartbeat=inFlight;
            }
            return;
        }
        active=true;
        try{
            const response=await sendHeartbeat(url,id,token,receipts,
                pendingReports,advertisement,pendingGpuReports);
            // The coordinator accepted these reports. Any subsequent agent
            // crash is reconciled using the native service journal.
            pendingReports=[];
            pendingGpuReports=[];
            // A corrupt advisory offer must not prevent this worker from
            // executing an independent native Stop or Inspect instruction.
            // Reject all lease receipts for this poll, but keep native
            // ownership reconciliation available.
            try {
                receipts=observedOffers(response.lease_offers||[],observations(id));
            }catch(error) {
                receipts=[];
                console.error('[experiment-hub-agent] Lease offers rejected: '+
                    String(error.message||error));
            }
            if(executor) {
                const orders=response.execution_orders||[];
                if(!Array.isArray(orders)||orders.length>1)
                    throw Error('Invalid machine execution order batch.');
                if(orders.length) {
                    let result;
                    executingNative=true;
                    try {
                        // No native service is launched until every requested
                        // collector receipt and binary capture has been
                        // authenticated, downloaded and SHA-256 verified.
                        // Transfer failures leave this Start unreported and
                        // ownerless; the Hub can safely retry the same order
                        // after collectors finish mirroring their files.
                        if(orders[0].action==='start'&&
                            orders[0].job?.project_id==='bees_native') {
                            await syncNativeBundles(orders[0].job,
                                executor.supervisor.adapters?.bees_native,
                                url,id,token);
                        }
                        result=await executor.handle(orders[0]);
                    }finally {
                        executingNative=false;
                        // A busy keepalive might still be on the wire. Do
                        // not race a fresh native report against that older
                        // heartbeat's authenticated machine observation.
                        if(busyHeartbeat)await busyHeartbeat;
                    }
                    const gpuResult=result?.kind==='gpu_qualification';
                    if(gpuResult)pendingGpuReports=[result];
                    else pendingReports=[result];
                    // Return consequential results and urgent native observations
                    // promptly. An empty receipt inspection can wait until the
                    // next regular heartbeat, which bounds follow-up traffic.
                    const releasedInspect=orders[0].action==='inspect'&&orders[0].lease.status==='released';
                    if(expedite&&(releasedInspect?(result?.evaluation||result?.evaluations?.length):
                        gpuResult||orders[0].action==='start'||
                        orders[0].refresh_native_observation===true||
                        ['stop_requested','finished','failed','stopped'].includes(result?.state)))
                        setTimeout(()=>poll(true),0);
                    if(['finished','failed','stopped'].includes(result?.state))
                        setTimeout(collectArtifacts,0);
                }
            }
            if(last!=='connected')
                console.log('[experiment-hub-agent] connected ('+id+
                    (executor
                        ?!executor.projects.length
                            ?'; native recovery only'
                            :advertisement.native_preflight_passed
                                ?'; native CPU/memory preflight passed'
                                :'; native starts blocked (preflight failed)'
                        :'; observation only, no job execution')+').');
            last='connected';
        }catch(error){
            const message=String(error.message||error);
            if(last!==message)console.error('[experiment-hub-agent] '+message);
            last=message;
        }finally{active=false;}
    };
    // Start the existing heartbeat cadence before a potentially slow native
    // directive. It now sends an observation-only heartbeat while that
    // directive is in flight, rather than skipping a whole TTL.
    const timer=setInterval(()=>poll(true),INTERVAL_MS);
    // Large telemetry transfers run separately from the 15-second agent
    // heartbeat and never block native Stop/Inspect report delivery.
    const mirrorTimer=!once&&syncLogs
        ?setInterval(collectArtifacts,60000):null;
    await poll(!once);
    if(!once)collectArtifacts();
    if(once) {
        clearInterval(timer);
        if(last!=='connected')process.exitCode=1;
        else {
            try {
                // Always close the one-shot session, even when there are no
                // native reports or lease receipts. An empty final report
                // still tells the coordinator that this process will exit:
                // it must not keep this instance connected for another TTL.
                await sendHeartbeat(url,id,token,receipts,pendingReports,
                    advertisement,pendingGpuReports,false,true);
            }catch(error) {
                console.error('[experiment-hub-agent] Final sign-off failed: '+error.message);
                process.exitCode=1;
            }
        }
        return;
    }
}

if(require.main===module)main().catch(error=>{
    console.error('[experiment-hub-agent] '+error.message);
    process.exitCode=1;
});
module.exports={endpoint,observations,observedOffers,sendHeartbeat};
