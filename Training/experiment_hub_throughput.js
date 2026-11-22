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
                    Date.now()-w.recorded_at<=MAX_SAMPLE_AGE_MS)
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
    observe(job,machineId,progress,gpus=[]) {
        const key=workloadKey(job);
        const deviceKey=JSON.stringify(Array.isArray(gpus)?gpus.slice().sort():[]);
        const steps=progress?.steps_completed;
        const time=Date.parse(progress?.source_modified_at);
        if(!key||key.length>4096||typeof machineId!=='string'||!machineId||
            !Number.isSafeInteger(steps)||steps<0||!Number.isFinite(time))return;
        const identity=String(job.id);
        const prior=this.last.get(identity);
        this.last.delete(identity);
        this.last.set(identity,{key,machineId,deviceKey,steps,time});
        if(this.last.size>MAX_KEYS*4)this.last.delete(this.last.keys().next().value);
        if(!prior||prior.key!==key||prior.machineId!==machineId||prior.deviceKey!==deviceKey||
            steps<=prior.steps||time<=prior.time)return;
        const seconds=(time-prior.time)/1000;
        // Reject pathological clocks and stale sparse measurements.
        if(seconds<1||seconds>3600)return;
        const rate=(steps-prior.steps)/seconds;
        if(!Number.isFinite(rate)||rate<=0)return;
        const id=JSON.stringify([key,machineId,deviceKey]);
        let windows=this.windows.get(id)||[];
        windows.push({steps:steps-prior.steps,seconds,recorded_at:Date.now()});
        if(windows.length>MAX_WINDOWS)windows.shift();
        this.windows.delete(id);
        this.windows.set(id,windows);
        if(this.windows.size>MAX_KEYS)this.windows.delete(this.windows.keys().next().value);
        if(++this.dirty>=8)this.persist();
    }
    compare(job,a,b) {
        const key=workloadKey(job);
        if(!key)return 0;
        const score=placement=>{
            const devices=JSON.stringify((placement.suggested_gpus||[]).slice().sort());
            const windows=(this.windows.get(JSON.stringify([key,placement.machine_id,devices]))||[])
                .filter(w=>Date.now()-w.recorded_at<=MAX_SAMPLE_AGE_MS&&
                    w.recorded_at<=Date.now());
            if(windows.length<MIN_WINDOWS)return null;
            const total=windows.reduce((v,w)=>v+w.steps,0);
            const seconds=windows.reduce((v,w)=>v+w.seconds,0);
            return total/seconds;
        };
        const left=score(a),right=score(b);
        // Missing evidence is neutral. Only like-for-like workloads compete.
        return left===null||right===null?0:right-left;
    }
}
module.exports={ThroughputHistory,workloadKey};
