'use strict';

// Project-neutral measured placement history. This never changes PPO,
// workload parameters, leases, or native process ownership.
const MAX_KEYS=256;
const MAX_WINDOWS=12;
const MIN_WINDOWS=2;

function workloadKey(job) {
    if(!job||typeof job.project_id!=='string'||!job.resources)return null;
    // Configurations with different learning semantics are never pooled.
    return JSON.stringify([job.project_id,job.frozen_parameters||null,
        job.resources.cpus,job.resources.ram_mb,job.resources.gpu_count,
        job.resources.gpu_mode,job.resources.gpu_vram_mb,
        job.resources.gpu_compute_pct,job.resources.min_gpu_vram_mb]);
}

class ThroughputHistory {
    constructor() {
        this.last=new Map();
        this.windows=new Map();
    }
    observe(job,machineId,progress,gpus=[]) {
        const key=workloadKey(job);
        const deviceKey=JSON.stringify(Array.isArray(gpus)?gpus.slice().sort():[]);
        const steps=progress?.steps_completed;
        const time=Date.parse(progress?.source_modified_at);
        if(!key||typeof machineId!=='string'||!machineId||
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
        windows.push({steps:steps-prior.steps,seconds});
        if(windows.length>MAX_WINDOWS)windows.shift();
        this.windows.delete(id);
        this.windows.set(id,windows);
        if(this.windows.size>MAX_KEYS)this.windows.delete(this.windows.keys().next().value);
    }
    compare(job,a,b) {
        const key=workloadKey(job);
        if(!key)return 0;
        const score=placement=>{
            const devices=JSON.stringify((placement.suggested_gpus||[]).slice().sort());
            const windows=this.windows.get(JSON.stringify([key,placement.machine_id,devices]));
            if(!windows||windows.length<MIN_WINDOWS)return null;
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
