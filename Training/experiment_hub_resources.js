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

// Check the original job budget against the reservation made for it.
 // The planner may choose GPU IDs when the request was unpinned, but
 // may not alter any requested resource quantity or GPU mode.
function matchingLeaseBudget(jobBudget,leaseBudget) {
    const requested=request(jobBudget),leased=request(leaseBudget);
    const fields=['cpus','ram_mb','gpu_count','min_gpu_vram_mb',
        'gpu_mode','gpu_vram_mb','gpu_compute_pct'];
    return fields.every(key=>requested[key]===leased[key])&&
        (!requested.gpus.length||
            requested.gpus.length===leased.gpus.length&&
            requested.gpus.every((id,index)=>id===leased.gpus[index]));
}
function remaining(machine,reservations) {
    // Both unacknowledged offers and acknowledged leases consume the Hub's
    // scheduling budget. Releasing only one side would permit overcommit.
    // Pre-existing 'active' claims remain protected.
    const claimed=reservations.filter(r=>r.machine_id===machine.id&&
        ['active','offered','acknowledged'].includes(r.status));
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
    // Physical GPU capacity is available only after matching a registered
    // logical GPU ID to a fresh authenticated PCI/VRAM sysfs observation.
    // Operator-entered names and stale/offline hardware cannot be claimed.
    // This remains advisory; it does NOT enforce GPU isolation or free VRAM.
    const verified=machine.status==='connected'&&
        Array.isArray(machine.verified_gpus)?machine.verified_gpus:[];
    // An operator can withhold a portion of a physical GPU's admission
    // budget for independently owned training (including Bees). These are
    // ledger limits, not VRAM or compute isolation.
    const capacity=verified.map(g=>({id:g.id,vram_mb:g.vram_mb,
        observed_gpu_busy_pct:Number.isSafeInteger(g.gpu_busy_pct)?g.gpu_busy_pct:null,
        observed_physical_free_vram_mb:
            Number.isSafeInteger(g.used_vram_mb)&&g.used_vram_mb>=0&&
            Number.isSafeInteger(g.observed_vram_mb??g.vram_mb)&&
            g.used_vram_mb<=(g.observed_vram_mb??g.vram_mb)
                ?(g.observed_vram_mb??g.vram_mb)-g.used_vram_mb:null,
        available_vram_mb:Math.min(g.vram_mb,g.hub_vram_mb??g.vram_mb),
        available_compute_pct:g.hub_compute_pct??100,exclusive:false}));
    const indexed=new Map(capacity.map(g=>[g.id,g]));
    for(const claim of claimed) {
        const resources=claim.resources||{};
        const ids=resources.gpus??[];
        const count=resources.gpu_count??(Array.isArray(ids)?ids.length:0);
        // A live legacy/stale claim with no trustworthy device mapping may
        // own any GPU on this host. Do not admit a second GPU claim until
        // its original reservation is reconciled or released.
        if(!Array.isArray(ids)||!int(count)||count>32||
            ids.length!==count||new Set(ids).size!==ids.length||
            ids.some(id=>!indexed.has(id))||
            (count===0&&(resources.gpu_mode!=null&&
                resources.gpu_mode!=='exclusive'||resources.gpu_vram_mb||
                resources.min_gpu_vram_mb||
                resources.gpu_compute_pct!=null&&
                resources.gpu_compute_pct!==100))) {
            for(const gpu of capacity)gpu.exclusive=true;
            continue;
        }
        for(const id of ids) {
            const gpu=indexed.get(id);
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
            a.available_compute_pct-b.available_compute_pct||
            (b.observed_physical_free_vram_mb??-1)-
                (a.observed_physical_free_vram_mb??-1)||
            (a.observed_gpu_busy_pct??100)-(b.observed_gpu_busy_pct??100));
        const selected=wanted.gpus.length
            ?wanted.gpus.map(id=>eligible.find(g=>g.id===id)).filter(Boolean)
            :eligible.slice(0,wanted.gpu_count);
        const capacityFits=wanted.cpus<=available.cpus&&
            wanted.ram_mb<=available.ram_mb&&selected.length===wanted.gpu_count;
        const online=machine.status==='connected';
        // This sample includes Bees, Hub jobs, desktop activity and other
        // external processes. It guides CPU-only placement between machines
        // that already fit; it never changes claimed capacity or licenses
        // CPU oversubscription. Absent/stale samples have no preference.
        const observedHostCpuBusy=online&&
            typeof machine.host_cpu_busy_pct==='number'&&
            Number.isFinite(machine.host_cpu_busy_pct)&&
            machine.host_cpu_busy_pct>=0&&machine.host_cpu_busy_pct<=100
                ?machine.host_cpu_busy_pct:null;
        const gpuDevice=selected.length===1
            ?machine.verified_gpus?.find(g=>g.id===selected[0].id):null;
        // A placement may be sensible for future work even when the
        // current host cannot safely launch it. Keep those distinct.
        const observed_headroom_block=online
            ?startHeadroom(wanted,machine.reported_available_ram_mb,gpuDevice):null;
        return {machine_id:machine.id,machine_name:machine.name,online,available,
            observed_headroom_block,observed_host_cpu_busy_pct:observedHostCpuBusy,
            observed_gpu_busy_pct:selected.length===1?
                selected[0].observed_gpu_busy_pct:null,
            observed_gpu_free_vram_mb:selected.length===1?
                selected[0].observed_physical_free_vram_mb:null,
            // Keep per-device observations distinct: physical VRAM on two
            // GPUs cannot be added into one useful memory budget.
            observed_gpu_devices:selected.map(g=>({
                id:g.id,
                busy_pct:g.observed_gpu_busy_pct,
                physical_free_vram_mb:g.observed_physical_free_vram_mb,
                hub_available_vram_mb:g.available_vram_mb,
                hub_available_compute_pct:g.available_compute_pct,
            })),
            // Heartbeats authenticate a reporting process, not reservation
            // enforcement. No job is executable through this planner yet.
            fits:false,capacity_fits:capacityFits,
            suggested_gpus:capacityFits?selected.map(g=>g.id):[],
            reason:!online?'No currently authenticated agent; capacity preview only'
                :capacityFits?'Agent connected; physical GPU identity matches, but resource isolation unavailable'
                :wanted.gpu_count&&!available.gpu_capacity.length
                    ?'No physical GPU matched registered PCI identity on this connected agent'
                    :'Insufficient CPU, RAM, verified GPU VRAM, or advisory GPU compute budget'};
    });
}

// Observed pressure breaks ties only *after* feasibility/headroom, never
// creates capacity. CPU-only jobs prefer hosts with less current total CPU
// activity (including Bees). Single-GPU jobs favor more physical free VRAM,
 // then lower measured GPU activity. Unknown measurements do not claim
// that a device is idle or available.
function observedLoadPreference(a,b,resources) {
    const cpuOnly=resources.gpu_count===0;
    if(cpuOnly) {
        const busy=x=>Number.isFinite(x.observed_host_cpu_busy_pct)
            ?x.observed_host_cpu_busy_pct:100;
        return busy(a)-busy(b);
    }
    if(resources.gpu_count===1) {
        const free=x=>Number.isSafeInteger(x.observed_gpu_free_vram_mb)
            ?x.observed_gpu_free_vram_mb:-1;
        const freeDelta=free(b)-free(a);
        if(freeDelta)return freeDelta;
        const busy=x=>Number.isFinite(x.observed_gpu_busy_pct)
            ?x.observed_gpu_busy_pct:100;
        return busy(a)-busy(b);
    }
    return 0;
}

// Greedy what-if placements for simultaneous jobs. Never write reservations,
// grant leases, launch programs, or claim authenticated machine ownership.
function plan(machines,reservations,jobs) {
    if(!Array.isArray(jobs)||jobs.length>100)
        throw Error('Enter at most 100 proposed workloads.');
    const work=jobs.map((job,index)=>{
        if(!job||typeof job!=='object'||typeof job.id!=='string'||!ID.test(job.id))
            throw Error('Every workload needs a valid unique ID.');
        if(job.machine_id!=null&&
            (typeof job.machine_id!=='string'||!ID.test(job.machine_id)))
            throw Error('Invalid machine affinity for workload.');
        return {id:job.id,index,resources:request(job.resources),
            machine_id:job.machine_id||null};
    });
    if(new Set(work.map(j=>j.id)).size!==work.length)
        throw Error('Workload IDs must be unique.');
    // A flexible workload must not consume the only host capable of a
    // pinned or large-VRAM workload. Rank scarce initial placement options
    // first; unlike repeated global searches this is bounded to one preview
    // pass per workload and does not affect the training hot path.
    for(const item of work) {
        item.options=placements(machines,reservations,item.resources)
            .filter(p=>p.capacity_fits&&
                (!item.machine_id||p.machine_id===item.machine_id)).length;
    }
    work.sort((a,b)=>
        Number(a.options===0)-Number(b.options===0)||
        a.options-b.options||
        b.resources.gpu_count-a.resources.gpu_count||
        Number(b.resources.gpu_mode==='exclusive')-Number(a.resources.gpu_mode==='exclusive')||
        b.resources.min_gpu_vram_mb-a.resources.min_gpu_vram_mb||
        b.resources.gpu_vram_mb-a.resources.gpu_vram_mb||
        b.resources.cpus-a.resources.cpus||
        b.resources.ram_mb-a.resources.ram_mb||a.index-b.index);
    const claimed=reservations.slice(),results=[];
    for(const item of work) {
        const eligible=placements(machines,claimed,item.resources)
            .filter(p=>p.capacity_fits&&
                (!item.machine_id||p.machine_id===item.machine_id))
            .sort((a,b)=>Number(Boolean(a.observed_headroom_block))-
                    Number(Boolean(b.observed_headroom_block))||
                // Prefer a presently connected worker whenever both fit.
                // Offline hosts remain visible as future-capacity fallbacks.
                Number(b.online)-Number(a.online)||
                observedLoadPreference(a,b,item.resources)||
                a.available.cpus-b.available.cpus||
                a.available.ram_mb-b.available.ram_mb||
                a.machine_id.localeCompare(b.machine_id));
        const chosen=eligible[0];
        if(chosen)claimed.push({machine_id:chosen.machine_id,status:'active',
            resources:{...item.resources,gpus:chosen.suggested_gpus}});
        results.push({id:item.id,machine_id:chosen?.machine_id||null,
            machine_name:chosen?.machine_name||null,
            suggested_gpus:chosen?.suggested_gpus||[],
            capacity_fits:Boolean(chosen),online:Boolean(chosen?.online),
            observed_headroom_block:chosen?.observed_headroom_block||null,
            executable:false,
            reason:chosen?(chosen.online
                ?'Advisory only: no enforced reservation or adapter'
                :'Capacity fits; machine agent unverified')
                :'No remaining machine capacity for concurrent workload',
            index:item.index});
    }
    return results.sort((a,b)=>a.index-b.index).map(({index,...row})=>row);
}
// A last-mile admission check against observed *host-wide* consumption.
// The scheduler's forward-looking leases remain authoritative for planned
// capacity; these observations detect competing unmanaged applications.
// GPU memory here is physical device usage, not GPU compute isolation.
function startHeadroom(resources,availableRamMb,device=null) {
    const wanted=request(resources);
    if(Number.isSafeInteger(availableRamMb)&&availableRamMb>=0&&
        availableRamMb<wanted.ram_mb)
        return 'Available host memory is below the requested native job RAM limit.';
    // min_gpu_vram_mb selects a device by total capacity. Only a shared
    // gpu_vram_mb field promises a specific per-job free-memory budget.
    // Requiring full device capacity to be unused would incorrectly reject
    // an exclusive GPU that is also serving a Windows desktop.
    const requiredVram=wanted.gpu_count===1&&wanted.gpu_mode==='shared'
        ?wanted.gpu_vram_mb:0;
    if(requiredVram>0&&device&&
        Number.isSafeInteger(device.vram_mb)&&device.vram_mb>0&&
        Number.isSafeInteger(device.used_vram_mb)&&
        device.used_vram_mb>=0&&device.used_vram_mb<=device.vram_mb&&
        device.vram_mb-device.used_vram_mb<requiredVram)
        return 'Observed physical free VRAM is below the requested native GPU memory budget.';
    return null;
}
// Defense in depth at the native process owner, not a replacement for
// coordinator leases or observed free-memory checks. A supervisor restored
// from disk must count all unresolved local services before accepting another
// physical CPU/RAM commitment. "Unverified" and quarantined records still
// own their budget until their exact native owner is reconciled.
function ensureLocalCpuRamCapacity(records,cpus,ramMb,hostCpus,hostRamMb) {
    if(!Array.isArray(records)||
        [cpus,ramMb,hostCpus,hostRamMb].some(n=>
            !Number.isSafeInteger(n)||n<1)||
        cpus>hostCpus||ramMb>hostRamMb)
        throw Error('Native job CPU/RAM commitment exceeds the physical host budget.');
    let usedCpus=0,usedRam=0;
    for(const record of records) {
        if(!record||typeof record!=='object')
            throw Error('Invalid persisted native ownership record.');
        if(['finished','failed','stopped'].includes(record.state)&&
            !record.quarantined_at)continue;
        if(!Number.isSafeInteger(record.cpus)||record.cpus<1||
            !Number.isSafeInteger(record.ram_mb)||record.ram_mb<1)
            throw Error('Unresolved native ownership has no trustworthy CPU/RAM budget.');
        usedCpus+=record.cpus;
        usedRam+=record.ram_mb;
        if(usedCpus+cpus>hostCpus||usedRam+ramMb>hostRamMb)
            throw Error('Unresolved native workloads already reserve the remaining physical CPU/RAM capacity.');
    }
}
module.exports={request,matchingLeaseBudget,remaining,placements,plan,startHeadroom,
    observedLoadPreference,ensureLocalCpuRamCapacity};
