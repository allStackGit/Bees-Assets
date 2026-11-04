'use strict';

// Pure capacity planner. Compute percentages are admission budgets, not
// OS-enforced quotas or guarantees of physical GPU performance.
const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const int = x => Number.isSafeInteger(x) && x >= 0;

function request(input) {
    if (!input || typeof input !== 'object' || Array.isArray(input))
        throw Error('Invalid resource request.');
    const {cpus,ram_mb} = input;
    if (!int(cpus) || !cpus || !int(ram_mb) || !ram_mb)
        throw Error('CPU and memory requests must be positive integers.');
    const gpus=input.gpus==null?[]:input.gpus;
    if (!Array.isArray(gpus) || gpus.some(id=>typeof id!=='string'||!ID.test(id)) ||
        new Set(gpus).size!==gpus.length)
        throw Error('GPU IDs must be unique valid device names.');
    const gpu_count=input.gpu_count==null?gpus.length:input.gpu_count;
    const min_gpu_vram_mb=input.min_gpu_vram_mb==null?0:input.min_gpu_vram_mb;
    if (!int(gpu_count)||gpu_count>32||(gpus.length&&gpu_count!==gpus.length)||
        !int(min_gpu_vram_mb)||min_gpu_vram_mb>1000000||
        (!gpu_count&&min_gpu_vram_mb))
        throw Error('GPU count/minimum VRAM invalid; exact IDs must match count.');
    // An older request or reservation is exclusive, never silently shareable.
    const gpu_mode=input.gpu_mode==null?'exclusive':input.gpu_mode;
    const gpu_vram_mb=input.gpu_vram_mb==null?0:input.gpu_vram_mb;
    const gpu_compute_pct=input.gpu_compute_pct==null?100:input.gpu_compute_pct;
    if (!['exclusive','shared'].includes(gpu_mode)||
        !int(gpu_vram_mb)||gpu_vram_mb>1000000||
        !int(gpu_compute_pct)||gpu_compute_pct>100||
        (gpu_mode==='shared'&&(!gpu_count||!gpu_vram_mb||!gpu_compute_pct))||
        (gpu_mode==='exclusive'&&(gpu_vram_mb!==0||gpu_compute_pct!==100))||
        (!gpu_count&&(gpu_mode==='shared'||gpu_vram_mb)))
        throw Error('Shared GPUs need positive per-device VRAM and compute budgets; exclusive GPUs reserve the entire device.');
    return {cpus,ram_mb,gpus,gpu_count,min_gpu_vram_mb,
        gpu_mode,gpu_vram_mb,gpu_compute_pct};
}

function remaining(machine,reservations) {
    const claimed=reservations.filter(r=>r.machine_id===machine.id&&r.status==='active');
    // A live authenticated agent may reduce a manually declared CPU/RAM
    // budget but never increase it. Stale measurements are not used.
    const cpuBudget=machine.status==='connected' &&
        Number.isSafeInteger(machine.reported_cpus)
        ?Math.min(machine.cpus,machine.reported_cpus):machine.cpus;
    const ramBudget=machine.status==='connected' &&
        Number.isSafeInteger(machine.reported_ram_mb)
        ?Math.min(machine.ram_mb,machine.reported_ram_mb):machine.ram_mb;
    const usedCpus=claimed.reduce((sum,r)=>sum+(r.resources?.cpus||0),0);
    const usedRam=claimed.reduce((sum,r)=>sum+(r.resources?.ram_mb||0),0);
    const capacity=machine.gpus.map(g=>({id:g.id,vram_mb:g.vram_mb,
        available_vram_mb:g.vram_mb,available_compute_pct:100,exclusive:false}));
    const indexed=new Map(capacity.map(g=>[g.id,g]));
    for(const claim of claimed) {
        const resources=claim.resources||{};
        for(const id of resources.gpus||[]) {
            const gpu=indexed.get(id);
            if(!gpu)continue;
            // Unknown/legacy/invalid modes fail closed, avoiding overcommit.
            if(resources.gpu_mode!=='shared'||
                !int(resources.gpu_vram_mb)||!resources.gpu_vram_mb||
                !int(resources.gpu_compute_pct)||!resources.gpu_compute_pct) {
                gpu.exclusive=true;
            } else {
                gpu.available_vram_mb=Math.max(0,gpu.available_vram_mb-resources.gpu_vram_mb);
                gpu.available_compute_pct=Math.max(0,gpu.available_compute_pct-resources.gpu_compute_pct);
            }
        }
    }
    for(const gpu of capacity) {
        if(gpu.exclusive){gpu.available_vram_mb=0;gpu.available_compute_pct=0;}
    }
    return {cpus:Math.max(0,cpuBudget-usedCpus),
        ram_mb:Math.max(0,ramBudget-usedRam),
        gpus:capacity.filter(g=>!g.exclusive&&g.available_vram_mb>0&&
            g.available_compute_pct>0).map(g=>g.id),
        gpu_capacity:capacity};
}

function placements(machines,reservations,input) {
    const wanted=request(input);
    return machines.map(machine=>{
        const available=remaining(machine,reservations);
        const eligible=available.gpu_capacity.filter(g=>
            g.vram_mb>=wanted.min_gpu_vram_mb&&
            (!wanted.gpus.length||wanted.gpus.includes(g.id))&&
            (wanted.gpu_mode==='exclusive'
                ?g.available_vram_mb===g.vram_mb&&g.available_compute_pct===100
                :g.available_vram_mb>=wanted.gpu_vram_mb&&
                    g.available_compute_pct>=wanted.gpu_compute_pct)
        ).sort((a,b)=>a.available_vram_mb-b.available_vram_mb||
            a.available_compute_pct-b.available_compute_pct);
        const selected=wanted.gpus.length
            ?wanted.gpus.map(id=>eligible.find(g=>g.id===id)).filter(Boolean)
            :eligible.slice(0,wanted.gpu_count);
        const capacityFits=wanted.cpus<=available.cpus&&
            wanted.ram_mb<=available.ram_mb&&selected.length===wanted.gpu_count;
        const online=machine.status==='connected';
        return {machine_id:machine.id,machine_name:machine.name,online,available,
            // Heartbeats authenticate a reporting process, not reservation
            // enforcement. No job is executable through this planner yet.
            fits:false,capacity_fits:capacityFits,
            suggested_gpus:capacityFits?selected.map(g=>g.id):[],
            reason:!online?'No currently authenticated agent; capacity preview only'
                :capacityFits?'Agent connected; resource isolation unavailable'
                :'Insufficient CPU, RAM, GPU VRAM, or GPU compute budget'};
    });
}

// Greedy what-if placements for simultaneous jobs. Never write reservations,
// grant leases, launch programs, or claim authenticated machine ownership.
function plan(machines,reservations,jobs) {
    if(!Array.isArray(jobs)||jobs.length>100)
        throw Error('Enter at most 100 proposed workloads.');
    const work=jobs.map((job,index)=>{
        if(!job||typeof job!=='object'||typeof job.id!=='string'||!ID.test(job.id))
            throw Error('Every workload needs a valid unique ID.');
        return {id:job.id,index,resources:request(job.resources)};
    });
    if(new Set(work.map(j=>j.id)).size!==work.length)
        throw Error('Workload IDs must be unique.');
    work.sort((a,b)=>b.resources.gpu_count-a.resources.gpu_count||
        Number(b.resources.gpu_mode==='exclusive')-Number(a.resources.gpu_mode==='exclusive')||
        b.resources.cpus-a.resources.cpus||
        b.resources.ram_mb-a.resources.ram_mb||a.index-b.index);
    const claimed=reservations.slice(),results=[];
    for(const item of work) {
        const eligible=placements(machines,claimed,item.resources)
            .filter(p=>p.capacity_fits)
            .sort((a,b)=>a.available.cpus-b.available.cpus||
                a.available.ram_mb-b.available.ram_mb||
                a.machine_id.localeCompare(b.machine_id));
        const chosen=eligible[0];
        if(chosen)claimed.push({machine_id:chosen.machine_id,status:'active',
            resources:{...item.resources,gpus:chosen.suggested_gpus}});
        results.push({id:item.id,machine_id:chosen?.machine_id||null,
            machine_name:chosen?.machine_name||null,
            suggested_gpus:chosen?.suggested_gpus||[],
            capacity_fits:Boolean(chosen),online:Boolean(chosen?.online),
            executable:false,
            reason:chosen?(chosen.online
                ?'Advisory only: no enforced reservation or adapter'
                :'Capacity fits; machine agent unverified')
                :'No remaining machine capacity for concurrent workload',
            index:item.index});
    }
    return results.sort((a,b)=>a.index-b.index).map(({index,...row})=>row);
}
module.exports={request,remaining,placements,plan};
