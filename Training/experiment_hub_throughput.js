'use strict';

// Project-neutral measured placement history. This never changes PPO,
// workload parameters, leases, or native process ownership.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const MAX_KEYS=256;
const MAX_WINDOWS=12;
const MIN_WINDOWS=2;
const MAX_SAMPLE_AGE_MS=7*24*60*60*1000;

function workloadKey(job) {
    if(!job||typeof job.project_id!=='string'||!job.resources)return null;
    // Configurations with different learning semantics are never pooled.
    return JSON.stringify([job.project_id,job.frozen_parameters||null,
        job.resources.cpus,job.resources.ram_mb,job.resources.gpu_count,
        job.resources.gpu_mode,job.resources.gpu_vram_mb,
        job.resources.gpu_compute_pct,job.resources.min_gpu_vram_mb]);
}


const MEASURE_NAME=/^[a-z][a-z0-9_]{0,63}$/;

// Progress steps count completed trainer updates in several backends, not
// useful simulation decisions. Prefer a monotonic useful-work counter and
// then a producer-reported useful-work rate. Only fall back to update rate.
function observedMeasure(progress) {
    const metrics=progress?.metrics||{};
    for(const name of ['training_total_useful_steps','useful_steps_completed']) {
        const item=metrics[name];
        if(item?.unit==='count'&&Number.isSafeInteger(item.value)&&item.value>=0)
            return {name,kind:'counter',value:item.value};
    }
    const explicit=['training_useful_steps_per_second','useful_steps_per_second',
        'ant_decisions_per_second','simulation_match_steps_per_second',
        'engine_steps_per_second'];
    const extra=Object.keys(metrics).filter(name=>name.endsWith('_useful_steps_per_second'))
        .sort();
    for(const name of [...explicit,...extra]) {
        const item=metrics[name];
        if(item?.unit==='rate'&&Number.isFinite(item.value)&&item.value>=0)
            return {name,kind:'rate',value:item.value};
    }
    return {name:'progress_updates',kind:'counter',value:progress.steps_completed};
}
// The same physical GPU can serve a workload alone or alongside entirely
// different neural networks. Those are different throughput experiments.
// Associate each measured placement with the frozen identities of OTHER
// Hub-owned native jobs actively sharing its GPUs. Unknown/external GPU
// processes are not inferred from host utilization.
function gpuCohort(machineId,gpus,context,excludeJobId=null) {
    if(!context||!Array.isArray(context.jobs)||
        !Array.isArray(context.reservations)||!Array.isArray(gpus))
        return null;
    const byId=new Map(context.jobs.map(j=>[j.id,j]));
    return JSON.stringify(gpus.slice().sort().map(gpu=>{
        const peers=[];
        for(const lease of context.reservations) {
            if(lease.machine_id!==machineId||lease.job_id===excludeJobId||
                !['active','offered','acknowledged'].includes(lease.status)||
                !Array.isArray(lease.resources?.gpus)||
                !lease.resources.gpus.includes(gpu))continue;
            const peer=byId.get(lease.job_id);
            if(!peer||!['running','stop_requested'].includes(peer.status)||
                peer.lease_id!==lease.id)continue;
            const key=workloadKey(peer);
            if(key)peers.push(crypto.createHash('sha256').update(key)
                .digest('hex').slice(0,16));
        }
        return peers.sort();
    }));
}

function rateEvidence(windows,now=Date.now()) {
    const fresh=windows.filter(w=>w.recorded_at<=now&&
        now-w.recorded_at<=MAX_SAMPLE_AGE_MS);
    const measure=fresh.at(-1)?.measure||'progress_updates';
    const matched=fresh.filter(w=>(w.measure||'progress_updates')===measure);
    if(matched.length<MIN_WINDOWS)return null;
    const seconds=matched.reduce((sum,w)=>sum+w.seconds,0);
    const work=matched.reduce((sum,w)=>sum+(w.work??w.steps),0);
    if(!(seconds>0)||!Number.isFinite(work)||work<0)return null;
    return {measure,rate:work/seconds,samples:matched.length,
        last_observed_at:new Date(Math.max(...matched.map(w=>w.recorded_at))).toISOString()};
}

class ThroughputHistory {
    constructor(root=null) {
        this.last=new Map();
        this.windows=new Map();
        this.file=root?path.join(root,'hub-throughput-v1.json'):null;
        this.dirty=0;
        if(this.file)this.load();
    }
    load() {
        try {
            const stat=fs.lstatSync(this.file);
            if(!stat.isFile()||stat.isSymbolicLink()||stat.size>2*1024*1024)
                return;
            const parsed=JSON.parse(fs.readFileSync(this.file,'utf8'));
            if(parsed.version!==1||!Array.isArray(parsed.windows))return;
            for(const entry of parsed.windows.slice(-MAX_KEYS)) {
                if(!Array.isArray(entry)||entry.length!==2||
                    typeof entry[0]!=='string'||entry[0].length>4096||
                    !Array.isArray(entry[1]))continue;
                const samples=entry[1].filter(w=>w&&
                    Number.isSafeInteger(w.steps)&&w.steps>0&&
                    Number.isFinite(w.seconds)&&w.seconds>=1&&w.seconds<=3600&&
                    Number.isFinite(w.recorded_at)&&w.recorded_at<=Date.now()&&
                    Date.now()-w.recorded_at<=MAX_SAMPLE_AGE_MS&&
                    (w.measure===undefined||
                        typeof w.measure==='string'&&MEASURE_NAME.test(w.measure))&&
                    (w.work===undefined||
                        Number.isFinite(w.work)&&w.work>=0&&w.work<=1e15))
                    .slice(-MAX_WINDOWS);
                if(samples.length)this.windows.set(entry[0],samples);
            }
        }catch(error) {
            if(error.code!=='ENOENT')this.load_issue=String(error.message||error);
        }
    }
    persist() {
        if(!this.file||!this.dirty)return;
        const tmp=this.file+'.'+process.pid+'.'+crypto.randomBytes(6).toString('hex')+'.tmp';
        try {
            const body=JSON.stringify({version:1,windows:[...this.windows]})+String.fromCharCode(10);
            if(Buffer.byteLength(body)>2*1024*1024)throw Error('Throughput history exceeds size limit.');
            fs.writeFileSync(tmp,body,{flag:'wx',mode:0o600});
            fs.renameSync(tmp,this.file);
            this.dirty=0;
            this.persist_issue=null;
        }catch(error) {
            this.persist_issue=String(error.message||error);
        }finally {
            try{fs.unlinkSync(tmp);}catch(error){if(error.code!=='ENOENT')this.persist_issue=String(error.message||error);}
        }
    }
    observe(job,machineId,progress,gpus=[],context=null) {
        const key=workloadKey(job);
        const deviceKey=JSON.stringify(Array.isArray(gpus)?gpus.slice().sort():[]);
        const cohort=gpuCohort(machineId,gpus,context,job?.id);
        const steps=progress?.steps_completed;
        const time=Date.parse(progress?.source_modified_at);
        if(!key||key.length>4096||typeof machineId!=='string'||!machineId||
            !Number.isSafeInteger(steps)||steps<0||!Number.isFinite(time))return;
        const measure=observedMeasure(progress);
        const identity=JSON.stringify([job.id,job.dispatch_id||null,
            progress.segment_id||null]);
        const prior=this.last.get(identity);
        // Ignore duplicate/out-of-order progress; replacing the baseline
        // here would overstate the next completed update's useful SPS.
        if(prior&&(time<=prior.time||steps===prior.steps))return;
        const next={key,machineId,deviceKey,cohort,steps,time,measure};
        this.last.delete(identity);
        this.last.set(identity,next);
        if(this.last.size>MAX_KEYS*4)this.last.delete(this.last.keys().next().value);
        if(!prior||prior.key!==key||prior.machineId!==machineId||
            prior.deviceKey!==deviceKey||prior.cohort!==cohort||
            steps<prior.steps||
            prior.measure.name!==measure.name||
            prior.measure.kind!==measure.kind)return;
        const seconds=(time-prior.time)/1000;
        if(seconds<1||seconds>3600)return;
        const work=measure.kind==='rate'
            ?measure.value*seconds:measure.value-prior.measure.value;
        if(!Number.isFinite(work)||work<0||work>1e15)return;
        const id=JSON.stringify(cohort===null
            ?[key,machineId,deviceKey]
            :[key,machineId,deviceKey,cohort]);
        let windows=this.windows.get(id)||[];
        // Never combine PPO updates and actual useful decisions in one
        // placement score, or mix different backends' rate definitions.
        if(windows.length&&(windows.at(-1).measure||'progress_updates')!==measure.name)
            windows=[];
        windows.push({steps:steps-prior.steps,work,measure:measure.name,
            seconds,recorded_at:Date.now()});
        if(windows.length>MAX_WINDOWS)windows.shift();
        this.windows.delete(id);
        this.windows.set(id,windows);
        if(this.windows.size>MAX_KEYS)this.windows.delete(this.windows.keys().next().value);
        if(++this.dirty>=8)this.persist();
    }
    summary() {
        const now=Date.now(),rows=[];
        for(const [identity,windows] of this.windows) {
            let parts;
            try{parts=JSON.parse(identity);}catch{continue;}
            if(!Array.isArray(parts)||![3,4].includes(parts.length))continue;
            const evidence=rateEvidence(windows,now);
            if(!evidence)continue;
            let project,gpus;
            try{project=JSON.parse(parts[0])[0];gpus=JSON.parse(parts[2]);}catch{continue;}
            if(typeof project!=='string'||!Array.isArray(gpus))continue;
            const row={project_id:project,machine_id:parts[1],
                gpu_ids:gpus,
                configuration_hash:crypto.createHash('sha256').update(parts[0]).digest('hex').slice(0,16),
                work_measure:evidence.measure,
                observed_work_per_second:evidence.rate,
                samples:evidence.samples,
                last_observed_at:evidence.last_observed_at};
            if(parts.length===4) {
                try {
                    const peers=JSON.parse(parts[3]);
                    if(Array.isArray(peers)&&peers.length===gpus.length&&
                        peers.every(x=>Array.isArray(x)))
                        row.hub_gpu_peer_counts=peers.map(x=>x.length);
                }catch{}
            }
            if(evidence.measure==='training_total_useful_steps'||
                evidence.measure==='useful_steps_completed'||
                evidence.measure.endsWith('_useful_steps_per_second'))
                row.useful_steps_per_second=evidence.rate;
            rows.push(row);
        }
        return rows;
    }
    compare(job,a,b,context=null) {
        const key=workloadKey(job);
        if(!key)return 0;
        const score=placement=>{
            const gpus=placement.suggested_gpus||[];
            const devices=JSON.stringify(gpus.slice().sort());
            const cohort=gpuCohort(placement.machine_id,gpus,context,job?.id);
            const identity=cohort===null
                ?[key,placement.machine_id,devices]
                :[key,placement.machine_id,devices,cohort];
            return rateEvidence(this.windows.get(JSON.stringify(identity))||[]);
        };
        const left=score(a),right=score(b);
        // Missing or non-comparable measurements are neutral, not zero SPS.
        if(!left||!right||left.measure!==right.measure)return 0;
        if(Math.abs(right.rate-left.rate)<=0.05*Math.max(left.rate,right.rate))
            return 0;
        return right.rate-left.rate;
    }
}
module.exports={ThroughputHistory,workloadKey};
