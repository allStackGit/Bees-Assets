'use strict';

// Project-neutral definitions and job records. This never starts a Bees run.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {request: resourceRequest,placements: resourcePlacements} =
    require('./experiment_hub_resources');
const {validateProgress}=require('./experiment_hub_progress');
const {tinyBrainsProfiles,supportsTinyBrainsProfile}=require('./experiment_hub_tinybrains_adapter');
const {evaluationPolicy,evaluationEvidence,suiteIds,supportsEvaluation}=
    require('./experiment_hub_evaluation');
const {ProgressHistory}=require('./experiment_hub_history');
const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const PCI = /^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const now = () => new Date().toISOString();
const AGENT_HEARTBEAT_TTL_MS = 45000;
function fingerprint(machineId, token) {
    return crypto.createHash('sha256').update(machineId + ':' + token).digest('hex');
}
function configuredGpus(input,requirePci=false) {
    if(!Array.isArray(input)||input.length>32||
        input.some(g=>!object(g)||!ID.test(String(g.id||''))||
            !Number.isSafeInteger(g.vram_mb)||g.vram_mb<1||
            g.vram_mb>1000000||
            (requirePci&&!PCI.test(String(g.pci_address||'')))||
            (g.pci_address!=null&&!PCI.test(String(g.pci_address))))||
        new Set(input.map(g=>g.id)).size!==input.length||
        new Set(input.filter(g=>g.pci_address).map(g=>g.pci_address)).size!==
            input.filter(g=>g.pci_address).length)
        throw Error('GPU IDs, PCI addresses, or nominal VRAM inventory invalid.');
    return input.map(g=>({
        id:g.id,vram_mb:g.vram_mb,
        ...(g.pci_address?{pci_address:g.pci_address}:{}),
    }));
}
function physicalGpuBindings(machine,report,resources,pinned) {
    if(!resources||resources.gpu_count===0)return [];
    const ids=resources.gpus;
    // A lease freezes logical ID -> physical PCI identity at allocation.
    // Never reconstruct a missing old pin from a later machine registration.
    if(!Number.isSafeInteger(resources.gpu_count)||resources.gpu_count<1||
        !Array.isArray(ids)||ids.length!==resources.gpu_count||
        !Array.isArray(pinned)||pinned.length!==ids.length||
        report.gpu_inventory_source!=='amdgpu-sysfs-v1')
        return null;
    const bindings=[];
    for(const id of ids) {
        const config=machine.gpus.find(g=>g.id===id);
        const bound=pinned.find(g=>g?.id===id);
        if(!config?.pci_address||!bound||
            !PCI.test(String(bound.pci_address))||
            bound.pci_address!==config.pci_address||
            !Number.isSafeInteger(bound.vram_mb)||bound.vram_mb<1||
            bound.vram_mb>config.vram_mb)
            return null;
        const physical=report.gpu_devices.find(g=>
            g.pci_address===bound.pci_address);
        if(!physical||physical.vram_mb<bound.vram_mb||
            bound.vram_mb<(resources.min_gpu_vram_mb||0)||
            (resources.gpu_mode==='shared'&&
                bound.vram_mb<(resources.gpu_vram_mb||0)))
            return null;
        bindings.push({id,pci_address:bound.pci_address,
            vram_mb:bound.vram_mb});
    }
    return bindings;
}
function boundedReport(value) {
    if (!object(value) ||
        !Number.isSafeInteger(value.cpus) || value.cpus < 1 || value.cpus > 4096 ||
        !Number.isSafeInteger(value.ram_mb) || value.ram_mb < 128 ||
        value.ram_mb > 100000000 ||
        typeof value.platform !== 'string' ||
        !/^[a-z0-9_-]{1,32}$/i.test(value.platform))
        throw Error('Invalid agent CPU, RAM, or platform report.');
    let execution={
        protocol_version:0,backend:'none',process_supervision:false,
        cpu_quota_enforced:false,memory_limit_enforced:false,
        gpu_isolation_enforced:false,cgroup_v2_detected:false,
        cgroup_controllers:[],reason:'Agent has not reported host isolation capabilities.',
    };
    if(value.execution!==undefined) {
        const e=value.execution;
        if(!object(e)||e.protocol_version!==1||e.platform!==value.platform||
            e.backend!=='none'||e.process_supervision!==false||
            e.cpu_quota_enforced!==false||e.memory_limit_enforced!==false||
            e.gpu_isolation_enforced!==false||
            typeof e.cgroup_v2_detected!=='boolean'||
            !Array.isArray(e.cgroup_controllers)||e.cgroup_controllers.length>5||
            e.cgroup_controllers.some(x=>
                !['cpu','memory','io','pids','cpuset'].includes(x))||
            new Set(e.cgroup_controllers).size!==e.cgroup_controllers.length||
            typeof e.reason!=='string'||e.reason.length>220)
            throw Error('Invalid agent execution capability report.');
        // A reported cgroup hierarchy is discovery only. No credential
        // or heartbeat can turn these flags into enforcement.
        execution={
            protocol_version:1,backend:'none',process_supervision:false,
            cpu_quota_enforced:false,memory_limit_enforced:false,
            gpu_isolation_enforced:false,
            cgroup_v2_detected:e.cgroup_v2_detected,
            cgroup_controllers:e.cgroup_controllers.slice(),reason:e.reason,
        };
    }
    const gpuSource=value.gpu_inventory_source||'none';
    const devices=value.gpu_devices==null?[]:value.gpu_devices;
    if(!['none','amdgpu-sysfs-v1'].includes(gpuSource)||
        (gpuSource==='amdgpu-sysfs-v1'&&value.platform!=='linux')||
        !Array.isArray(devices)||devices.length>32||
        (gpuSource==='none'&&devices.length)||
        devices.some(g=>!object(g)||!PCI.test(String(g.pci_address||''))||
            !Number.isSafeInteger(g.vram_mb)||g.vram_mb<1||
            g.vram_mb>1000000||
            (g.used_vram_mb!=null&&
                (!Number.isSafeInteger(g.used_vram_mb)||
                    g.used_vram_mb<0||g.used_vram_mb>g.vram_mb)))||
        new Set(devices.map(g=>g.pci_address)).size!==devices.length)
        throw Error('Invalid bounded physical GPU inventory.');
    const observedGpus=devices.map(g=>({
        pci_address:g.pci_address,vram_mb:g.vram_mb,
        used_vram_mb:g.used_vram_mb??null,
    }));
    const localExecutor=value.local_executor===true;
    const preflight=value.native_preflight_passed===true;
    const reason=value.native_preflight_reason;
    if((preflight&&!localExecutor)||
        (reason!=null&&
            (typeof reason!=='string'||reason.length>180)))
        throw Error('Invalid native execution qualification report.');
    const installed=value.installed_projects==null?[]:value.installed_projects;
    if(!Array.isArray(installed)||installed.length>32||
        installed.some(p=>typeof p!=='string'||!ID.test(p))||
        new Set(installed).size!==installed.length||
        (!localExecutor&&installed.length))
        throw Error('Invalid locally installed execution adapters.');
    const evaluators=value.evaluation_suites??{};
    if(!object(evaluators)||Object.keys(evaluators).length>installed.length||
        Object.keys(evaluators).some(project=>!installed.includes(project)))
        throw Error('Evaluation capability requires a corresponding installed adapter.');
    const profiles=tinyBrainsProfiles(value.tinybrains_profiles??{});
    if(Object.keys(profiles).length&&(!installed.includes('tinybrains')||value.platform!=='win32'))
        throw Error('TinyBrains profiles require an installed Windows adapter.');
    const gpuProjects=value.gpu_projects??[];
    if(!Array.isArray(gpuProjects)||new Set(gpuProjects).size!==gpuProjects.length||
        gpuProjects.length>installed.length||
        gpuProjects.some(project=>!installed.includes(project))||
        (gpuProjects.length&&value.platform!=='linux'))
        throw Error('GPU execution capability requires installed Linux adapters.');
    const evaluationSuites=Object.fromEntries(Object.entries(evaluators)
        .map(([project,ids])=>[project,suiteIds(ids)]));
    return {cpus:value.cpus,ram_mb:value.ram_mb,
        platform:value.platform,execution,
        gpu_inventory_source:gpuSource,gpu_devices:observedGpus,
        local_executor:localExecutor&&['linux','win32'].includes(value.platform),
        native_preflight_passed:localExecutor&&
            ['linux','win32'].includes(value.platform)&&preflight,
        native_preflight_reason:reason||null,
        evaluation_suites:localExecutor?evaluationSuites:{},
        tinybrains_profiles:profiles,
        gpu_projects:gpuProjects.slice(),
        installed_projects:['linux','win32'].includes(value.platform)&&
            localExecutor?installed.slice():[]};
}

function object(value) {
    return value && typeof value === 'object' && !Array.isArray(value);
}
function copy(value) { return JSON.parse(JSON.stringify(value)); }
function name(value, label) {
    if (typeof value !== 'string' || !value.trim() || value.length > 100)
        throw Error(label + ' must contain 1–100 characters.');
    return value.trim();
}
function config(value) {
    if (!object(value)) throw Error('Configuration parameters must be a JSON object.');
    const serialized = JSON.stringify(value);
    if (serialized.length > 65536) throw Error('Configuration exceeds 64 KiB.');
    return JSON.parse(serialized);
}
function gpuStartBlock(job,lease,machine) {
    const r=job.resources,allocated=lease.resources;
    if(r.gpu_count===0&&allocated.gpu_count===0&&
        !(r.gpus||[]).length&&!(allocated.gpus||[]).length)return null;
    if(r.gpu_count!==1||allocated.gpu_count!==1||
        r.gpu_mode!=='exclusive'||allocated.gpu_mode!=='exclusive'||
        allocated.gpus?.length!==1||
        (r.gpus?.length&&r.gpus[0]!==allocated.gpus[0]))
        return 'Native GPU Start requires one exclusively leased GPU.';
    if(machine.agent_platform!=='linux'||!machine.gpu_projects?.includes(job.project_id))
        return 'The Linux host and local adapter have not opted into native GPU execution.';
    if(!(machine.observed_gpus?.length>=2))
        return 'Native GPU Start requires a second observed GPU to verify access denial.';
    if(job.gpu_qualification?.current!==true)
        return 'A current device access probe for this exact GPU lease is required.';
    return null;
}
function gpuProbeBlock(job,lease,machine,requireSecondDevice=true) {
    if(!job||job.project_id==='bees'||job.adapter!=='unconfigured'||
        job.status!=='waiting_adapter'||job.dispatch_id||
        !lease||lease.job_id!==job.id||lease.project_id!==job.project_id||
        lease.machine_id!==job.assigned_machine_id||
        lease.issued_by!=='experiment-hub'||
        lease.status!=='acknowledged'||!lease.receipt_fresh||
        !machine||machine.status!=='connected'||
        machine.agent_platform!=='linux'||machine.local_executor_enabled!==true||
        machine.native_preflight_passed!==true||
        !machine.installed_projects?.includes(job.project_id)||
        job.resources.gpu_mode!=='exclusive'||
        job.resources.gpu_count!==1||
        (job.resources.gpus?.length>0&&
            job.resources.gpus[0]!==lease?.resources.gpus?.[0])||
        lease.resources.gpu_mode!=='exclusive'||
        lease.resources.gpu_count!==1||lease.resources.gpus?.length!==1||
        physicalGpuBindings(machine,{
            gpu_inventory_source:machine.gpu_inventory_source,
            gpu_devices:machine.observed_gpus,
        },lease.resources,lease.gpu_bindings)?.length!==1)
        return 'GPU access probe needs a fresh exclusive single-GPU lease on a qualified Linux agent.';
    if(requireSecondDevice&&!(machine.observed_gpus?.length>=2))
        return 'GPU access probe requires a second observed GPU to verify access denial.';
    return null;
}
// Pure, non-authorizing readiness projection. The dashboard and the actual
// start authorization share one eligibility contract; displaying readiness
// does not claim a reservation or dispatch any native process.
function nativeStartReadiness(snapshot, job) {
    const block=reason=>({eligible:false,reason});
    if(!job||job.status!=='waiting_adapter'||
        job.adapter!=='unconfigured'||job.project_id==='bees')
        return block('Only queued non-Bees jobs support Hub native starts.');
    if(!job.lease_id)
        return block('Coordinate machine capacity before requesting Start.');
    const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
        l.job_id===job.id&&l.project_id===job.project_id &&
        l.machine_id===job.assigned_machine_id&&
        l.issued_by==='experiment-hub'&&l.status==='acknowledged');
    if(!lease||!lease.receipt_fresh)
        return block('A fresh, acknowledged matching machine lease is required.');
    const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
    if(!machine||machine.status!=='connected')
        return block('The assigned machine must have a fresh authenticated heartbeat.');
    if(!['linux','win32'].includes(machine.agent_platform)||
        machine.local_executor_enabled!==true)
        return block('A supported locally installed native executor is required.');
    if(!machine.installed_projects?.includes(job.project_id))
        return block('The assigned machine has no trusted adapter for this project.');
    if(machine.native_preflight_passed!==true)
        return block('The local CPU/RAM supervisor preflight has not passed.');
    if(machine.agent_platform==='linux'&&
        (machine.execution_capabilities?.cgroup_v2_detected!==true||
            !['cpu','memory'].every(c=>
                machine.execution_capabilities?.cgroup_controllers.includes(c))))
        return block('Linux cgroup v2 CPU and memory controllers are unavailable.');
    if(!supportsTinyBrainsProfile(machine,job))
        return block('The assigned worker does not approve this TinyBrains CPU profile and update count.');
    if(!supportsEvaluation(machine.evaluation_suites,job))
        return block('The assigned adapter does not advertise evaluation suite '+job.evaluation_stop.suite_id+'.');
    const r=job.resources;
    if(!r||r.ram_mb<128)
        return block('Native Start requires at least 128 MiB RAM.');
    const gpuBlock=gpuStartBlock(job,lease,machine);
    if(gpuBlock)return block(gpuBlock);
    if(r.cpus>machine.reported_cpus||r.ram_mb>machine.reported_ram_mb)
        return block('Requested CPU/RAM exceeds the machine\'s fresh reported capacity.');
    if(job.continuation_of_job_id) {
        const parent=snapshot.jobs.find(j=>j.id===job.continuation_of_job_id);
        if(!parent||parent.project_id!==job.project_id||
            !['finished','stopped'].includes(parent.status)||
            !parent.capacity_released_at||
            parent.assigned_machine_id!==job.assigned_machine_id||
            (parent.artifact_lineage_root_id||parent.id)!==
                job.artifact_lineage_root_id||
            job.continuation_machine_id!==job.assigned_machine_id)
            return block('Continuation lineage, completed parent, or original machine is not verified.');
    }
    if(job.after_job_id) {
        const parent=snapshot.jobs.find(j=>j.id===job.after_job_id);
        // Written by the same atomic commit that releases the finished
        // predecessor, or after verifying an already-released lease at
        // queue creation. Lease history is bounded and may later be pruned.
        if(!parent||parent.status!=='finished'||
            !parent.capacity_released_at||!parent.dispatch_id||
            !parent.execution_updated_at||
            typeof job.predecessor_ready_at!=='string'||
            !Number.isFinite(Date.parse(job.predecessor_ready_at))||
            job.continuation_of_job_id||
            job.artifact_lineage_root_id!==job.id||
            job.continuation_machine_id)
            return block('Independent dependency has not finished and released verified native capacity.');
    }
    return {eligible:true,reason:r.gpu_count
        ?'Eligible for explicit exclusive GPU Start with in-service verification.'
        :'Eligible for explicit CPU-only native Start authorization.'};
}
// Match the operator's cross-project FIFO fairness in both the read-only
// preview and the actual advisory claim pass.
function fairPending(jobs,lastProjectId=null) {
    const buckets=new Map();
    for(const job of jobs) {
        if(!buckets.has(job.project_id))buckets.set(job.project_id,[]);
        buckets.get(job.project_id).push(job);
    }
    // The read-only preview and manual allocation retain their stable order.
    // A one-transition automation tick resumes after the previously served
    // project instead of resetting its priority on every invocation.
    const rotated=[...buckets];
    const after=rotated.findIndex(([id])=>id===lastProjectId);
    if(after>=0)rotated.push(...rotated.splice(0,after+1));
    const ordered=[];
    while(rotated.some(([,bucket])=>bucket.length)) {
        for(const [,bucket] of rotated) {
            if(bucket.length)ordered.push(bucket.shift());
        }
    }
    return ordered;
}
// Read-only pipeline diagnosis, never an execution authorization.
function waitingStageReason(job,byId) {
    const visited=new Set([job.id]);
    let dependent=job;
    for(let depth=1;depth<=1000;depth++) {
        const parentId=dependent.continuation_of_job_id||dependent.after_job_id;
        const parent=byId.get(parentId);
        if(!parent||visited.has(parentId))
            return {status:'broken_lineage',depth,
                reason:'Prequeued stage has missing or cyclic predecessor lineage; operator review required.'};
        visited.add(parentId);
        if(parent.status==='waiting_predecessor') {
            dependent=parent;
            continue;
        }
        const label=parent.configuration_name||parent.id.slice(-8);
        let status,explanation;
        if(parent.status==='waiting_adapter') {
            status='queued';explanation='has not started';
        }else if(['start_requested','launch_requested'].includes(parent.status)) {
            status='starting';explanation='is starting';
        }else if(parent.status==='running') {
            status='running';explanation='is still running';
        }else if(parent.status==='stop_requested') {
            status='stopping';explanation='has a pending Stop request; automatic advancement is blocked';
        }else if(parent.status==='finished'&&!parent.capacity_released_at) {
            status='release_pending';explanation='finished; capacity release is required';
        }else if(parent.status==='stopped') {
            if(dependent.after_job_id) {
                status='needs_review';
                explanation='stopped without finishing; independent dependent jobs will not auto-advance';
            }else if(parent.project_id==='tinybrains') {
                status='needs_review';
                explanation='stopped without finishing; cancel the queued TinyBrains continuation stages';
            }else {
                status='operator_approval';
                explanation=parent.capacity_released_at
                    ?'stopped; explicit checkpoint-risk approval is required'
                    :'stopped; capacity release and explicit checkpoint-risk approval are required';
            }
        }else {
            status='needs_review';explanation=
                'is '+parent.status+'; pipeline will not auto-advance without operator review';
        }
        return {status,depth,blocked_by_job_id:parent.id,
            blocked_by_status:parent.status,
            reason:'Stage '+label+' '+explanation+'.'};
    }
    return {status:'broken_lineage',depth:1000,
        reason:'Prequeued pipeline depth exceeds the job-history bound; operator review required.'};
}
function allocationBlockReason(machines,job,preferredMachineId=null) {
    const candidates=preferredMachineId
        ?machines.filter(machine=>machine.id===preferredMachineId):machines;
    if(!candidates.length)
        return preferredMachineId?'The selected worker is offline or unavailable.'
            :'No worker is connected.';
    if(job.project_id==='tinybrains'&&
        !candidates.some(machine=>supportsTinyBrainsProfile(machine,job)))
        return 'No eligible Windows worker advertises this TinyBrains CPU profile and digest, or the update count is invalid.';
    return 'No eligible connected worker has enough remaining capacity.';
}
function allocationChoice(machines,reservations,job,preferredMachineId=null,
    following=[],futureMachinesFor=()=>machines) {
    const eligibleOptions=(selected,hosts,preferred=null)=>{
        const eligible=hosts.filter(machine=>
            supportsEvaluation(machine.evaluation_suites,selected)&&
            supportsTinyBrainsProfile(machine,selected)&&
            (!preferred||preferred===machine.id)&&
            (!selected.continuation_machine_id||selected.continuation_machine_id===machine.id));
        const fits=resourcePlacements(eligible,reservations,selected.resources)
            .filter(p=>p.capacity_fits);
        const r=selected.resources;
        const cpuOnly=r.gpu_count===0&&!(r.gpus||[]).length;
        const exclusiveGpu=r.gpu_count===1&&r.gpu_mode==='exclusive';
        if(r.ram_mb<128||(!cpuOnly&&!exclusiveGpu))return fits;
        const ready=new Set(eligible.filter(machine=>
            machine.local_executor_enabled===true&&machine.native_preflight_passed===true&&
            machine.installed_projects?.includes(selected.project_id)&&
            (machine.agent_platform==='win32'||machine.agent_platform==='linux'&&
                machine.execution_capabilities?.cgroup_v2_detected===true&&
                ['cpu','memory'].every(controller=>
                    machine.execution_capabilities.cgroup_controllers.includes(controller)))&&
            (cpuOnly||machine.gpu_projects?.includes(selected.project_id)&&
                machine.observed_gpus?.length>=2)
        ).map(machine=>machine.id));
        const runnable=fits.filter(p=>ready.has(p.machine_id));
        if(runnable.length)return runnable;
        if(exclusiveGpu) {
            const optedIn=new Set(eligible.filter(machine=>
                machine.gpu_projects?.includes(selected.project_id)).map(machine=>machine.id));
            const optedInFits=fits.filter(p=>optedIn.has(p.machine_id));
            if(optedInFits.length)return optedInFits;
        }
        return fits;
    };
    const options=eligibleOptions(job,machines,preferredMachineId)
        .sort((a,b)=>a.available.cpus-b.available.cpus||
            a.available.ram_mb-b.available.ram_mb||
            a.machine_id.localeCompare(b.machine_id));
    if(options.length<2||!following.length)return options[0]||null;
    // Preserve FIFO jobs while avoiding a placement that would block a
    // later job with exactly one capable host. This is advisory lookahead:
    // no lease, execution permission or native ownership is conferred.
    const soleOptions=following.filter(next=>!next.lease_id)
        .map(next=>{
            const places=eligibleOptions(next,futureMachinesFor(next));
            return places.length===1
                ?{job:next,machine_id:places[0].machine_id}:null;
        }).filter(Boolean);
    if(!soleOptions.length)return options[0];
    const remainingOptionLoss=option=>{
        const shadow=reservations.concat([{
            machine_id:option.machine_id,status:'offered',
            resources:{...job.resources,
                gpus:option.suggested_gpus},
        }]);
        let lost=0;
        const machine=machines.find(m=>m.id===option.machine_id);
        for(const later of soleOptions) {
            if(later.machine_id!==option.machine_id)continue;
            if(!resourcePlacements([machine],shadow,later.job.resources)[0]
                .capacity_fits)lost++;
        }
        return lost;
    };
    const loss=new Map(options.map(option=>[
        option.machine_id,remainingOptionLoss(option),
    ]));
    options.sort((a,b)=>loss.get(a.machine_id)-loss.get(b.machine_id));
    return options[0];
}
function writeAtomic(file, value) {
    fs.mkdirSync(path.dirname(file), {recursive:true});
    const pending = file + '.' + process.pid + '.' + crypto.randomBytes(6).toString('hex') + '.tmp';
    try {
        fs.writeFileSync(pending, JSON.stringify(value, null, 2) + '\n', {flag:'wx'});
        fs.renameSync(pending, file);
    } finally {
        if (fs.existsSync(pending)) fs.unlinkSync(pending);
    }
}
function claimsFitCpuRam(reservations,machine,reportedCpus,reportedRam) {
    const outstanding=reservations.filter(lease=>lease.machine_id===machine.id&&
        ['active','offered','acknowledged'].includes(lease.status));
    return outstanding.every(lease=>
        Number.isSafeInteger(lease.resources?.cpus)&&lease.resources.cpus>0&&
        Number.isSafeInteger(lease.resources?.ram_mb)&&lease.resources.ram_mb>0)&&
        outstanding.reduce((sum,lease)=>sum+lease.resources.cpus,0)<=Math.min(machine.cpus,reportedCpus)&&
        outstanding.reduce((sum,lease)=>sum+lease.resources.ram_mb,0)<=Math.min(machine.ram_mb,reportedRam);
}

function summarizeResourceSample(previous,report,observedAt) {
    if(!['running','stop_requested','finished','failed','stopped'].includes(report.state)||
        (report.cpu_time_ns==null&&report.memory_current_mb==null&&
         report.peak_job_memory_bytes==null))
        return previous;
    const highest=(a,b)=>a==null?b??null:b==null?a:Math.max(a,b);
    return {
        first_observed_at:previous?.first_observed_at||observedAt,
        last_observed_at:observedAt,
        cpu_time_ns_high_water:highest(previous?.cpu_time_ns_high_water,report.cpu_time_ns),
        peak_observed_memory_mb:highest(previous?.peak_observed_memory_mb,report.memory_current_mb),
        peak_job_memory_bytes:highest(previous?.peak_job_memory_bytes,report.peak_job_memory_bytes),
    };
}

function gpuProbeHoldsClaim(job) {
    const proof=job.gpu_qualification;
    return Boolean(job.lease_id&&proof?.lease_id===job.lease_id&&
        (proof.cleanup_required||
            (proof.status==='requested'&&proof.first_offered_at!==null)));
}

class ExperimentHub {
    constructor(root) {
        this.file = path.join(root, 'hub-v1.json');
        this.state = fs.existsSync(this.file) ? JSON.parse(fs.readFileSync(this.file, 'utf8')) : {
            version:1,
            projects:[{id:'bees', name:'Bees', adapter:'bees', description:'Distributed Unity reinforcement learning'}],
            configurations:[],
            jobs:[],
            machines:[],
            reservations:[],
        };
        // Heartbeats are ephemeral. After a coordinator restart, a machine is
        // not considered connected until it authenticates again.
        this.agentReports = new Map();
        // Receipt freshness is process-local, never inferred after restart.
        // Persisted leases remain capacity-blocking until explicitly released.
        this.leaseReceipts = new Map();
        // Native process usage observations are ephemeral, not registry history.
        this.executionTelemetry = new Map();
        this.progressHistory = new ProgressHistory(path.join(root,'hub-chart-v1.json'));
        this.executionOrderCursor = new Map();
        // Native queue automation is an explicitly armed, process-local
        // session. Restarting the coordinator never restores permission to
        // start unattended workloads, even though claims/jobs persist.
        this.automation = {
            enabled:false,machine_ids:[],agent_instances:{},managed_job_ids:[],
            rebalance_queued:false,start_qualified_gpu:false,claim_exclusive_gpu:false,probe_exclusive_gpu:false,claimed_lease_ids:[],
            session_id:null,
            armed_at:null,last_tick_at:null,last_action:null,last_error:null,
            last_transition_at:null,waiting_reason:null,
            last_claimed_project_id:null,last_started_project_id:null,
        };
        const s = this.state;
        if (!object(s) || s.version !== 1 ||
            !['projects','configurations','jobs','machines','reservations'].every(key=>Array.isArray(s[key])))
            throw Error('Experiment Hub registry invalid; refusing to start.');
        if (!s.projects.some(p=>p.id==='bees' && p.adapter==='bees'))
            throw Error('Experiment Hub Bees project identity changed; refusing to start.');
    }
    // Publish in-memory changes only after the complete registry is durably
    // written. A full disk must not leave the API believing a draft was saved.
    commit(next) {
        writeAtomic(this.file, next);
        this.state = next;
        // A released/pruned claim cannot retain an in-memory receipt.
        const outstanding=new Set(next.reservations.filter(r=>
            ['offered','acknowledged'].includes(r.status)).map(r=>r.id));
        for(const id of this.leaseReceipts.keys())
            if(!outstanding.has(id))this.leaseReceipts.delete(id);
    }
    snapshot() {
        const snapshot = copy(this.state);
        const observedAt = Date.now();
        snapshot.machines = snapshot.machines.map(machine => {
            const report = this.agentReports.get(machine.id);
            const enrolled = typeof machine.agent_key_sha256 === 'string';
            const fresh = enrolled && report &&
                observedAt >= report.received_ms &&
                observedAt - report.received_ms < AGENT_HEARTBEAT_TTL_MS;
            // Never leak even a credential verifier through any dashboard API.
            delete machine.agent_key_sha256;
            machine.agent_enrolled = enrolled;
            machine.status = !enrolled ? 'unverified' : fresh ? 'connected' : 'offline';
            machine.last_seen_at = report
                ? new Date(report.received_ms).toISOString() : null;
            machine.gpu_inventory_source=fresh?report.gpu_inventory_source:'none';
            machine.observed_gpus=fresh?copy(report.gpu_devices):[];
            // Only PCI-address-bound configured GPUs enter the planner.
            // A manually named GPU without a matching physical identity
            // remains advisory and unavailable for even a Hub capacity claim.
            machine.verified_gpus=fresh?machine.gpus.flatMap(configured=>{
                if(!configured.pci_address)return [];
                const actual=report.gpu_devices.find(g=>
                    g.pci_address===configured.pci_address);
                return actual?[{
                    id:configured.id,pci_address:configured.pci_address,
                    vram_mb:Math.min(configured.vram_mb,actual.vram_mb),
                    observed_vram_mb:actual.vram_mb,
                    used_vram_mb:actual.used_vram_mb,
                }]:[];
            }):[];
            if (fresh) {
                machine.reported_cpus = report.cpus;
                machine.reported_ram_mb = report.ram_mb;
                machine.capacity_overcommitted=!claimsFitCpuRam(snapshot.reservations,machine,report.cpus,report.ram_mb);
                machine.agent_platform = report.platform;
                machine.execution_capabilities = copy(report.execution);
                machine.local_executor_enabled = report.local_executor;
                machine.native_preflight_passed = report.native_preflight_passed;
                machine.native_preflight_reason = report.native_preflight_reason;
                machine.installed_projects = report.installed_projects.slice();
                machine.evaluation_suites = copy(report.evaluation_suites);
                machine.tinybrains_profiles = copy(report.tinybrains_profiles||{});
                machine.gpu_projects = report.gpu_projects.slice();
            }
            return machine;
        });
        snapshot.jobs = snapshot.jobs.map(job=>{
            const observed=this.executionTelemetry.get(job.id);
            const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
            const live=observed&&machine?.status==='connected'&&
                observedAt-new Date(observed.at).getTime()<AGENT_HEARTBEAT_TTL_MS
                ?observed:null;
            return {...job,live_execution:live};
        });
        snapshot.reservations = snapshot.reservations.map(lease => ({
            ...lease,
            receipt_fresh:lease.status==='acknowledged' &&
                snapshot.machines.some(machine=>machine.id===lease.machine_id &&
                    machine.status==='connected') &&
                this.leaseReceipts.has(lease.id) &&
                observedAt >= this.leaseReceipts.get(lease.id) &&
                observedAt - this.leaseReceipts.get(lease.id) < AGENT_HEARTBEAT_TTL_MS,
        }));
        snapshot.jobs=snapshot.jobs.map(job=>{
            const proof=job.gpu_qualification;
            if(!proof)return job;
            proof.holds_claim=gpuProbeHoldsClaim(job);
            const machine=snapshot.machines.find(m=>m.id===proof.machine_id);
            const lease=snapshot.reservations.find(l=>l.id===proof.lease_id);
            const report=this.agentReports.get(proof.machine_id);
            const pinned=lease?.gpu_bindings;
            const stillMatched=Boolean(lease&&machine&&
                pinned?.length===1&&
                proof.selected_pci_addresses?.length===1&&
                proof.selected_pci_addresses[0]===pinned[0].pci_address&&
                machine.verified_gpus?.some(g=>
                    g.pci_address===pinned[0].pci_address&&
                    g.vram_mb>=pinned[0].vram_mb));
            proof.automation_paused=Boolean(proof.status==='requested'&&proof.automation_session_id&&
                !this.automationSessionCurrent(proof.automation_session_id,proof.machine_id));
            proof.current=Boolean(proof.status==='verified'&&
                ['waiting_adapter','start_requested'].includes(job.status)&&
                job.lease_id===proof.lease_id&&lease?.receipt_fresh&&
                machine?.status==='connected'&&
                machine.native_preflight_passed===true&&
                report?.instance_id===proof.agent_instance_id&&stillMatched);
            return job;
        });
        const jobsById=new Map(snapshot.jobs.map(job=>[job.id,job]));
        for(const job of snapshot.jobs) {
            if(job.status==='waiting_predecessor')
                job.predecessor_wait=waitingStageReason(job,jobsById);
            if(job.status==='waiting_adapter'&&job.project_id!=='bees')
                job.native_start_readiness=nativeStartReadiness(snapshot,job);
            if(job.status==='waiting_adapter'&&job.lease_id&&job.resources?.gpu_count===1&&
                job.resources.gpu_mode==='exclusive') {
                const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
                job.gpu_probe_block=gpuProbeBlock(job,lease,machine);
            }
            if(job.status==='start_requested'&&job.automation_start_session_id)
                job.automation_start_paused=!(
                    this.automation.enabled&&
                    this.automation.session_id===job.automation_start_session_id&&
                    this.automation.agent_instances[job.assigned_machine_id]===
                        this.agentReports.get(job.assigned_machine_id)?.instance_id&&
                    snapshot.machines.some(m=>m.id===job.assigned_machine_id&&
                        m.status==='connected'));
        }
        snapshot.automation=this.automationStatus();
        return snapshot;
    }
    automationSessionCurrent(sessionId,machineId) {
        const instance=this.automation.agent_instances[machineId];
        return Boolean(instance&&this.automation.enabled&&this.automation.session_id===sessionId&&
            instance===this.agentReports.get(machineId)?.instance_id);
    }
    automationStatus() {
        const {agent_instances,managed_job_ids,claimed_lease_ids,session_id,...publicState}=this.automation;
        return {...copy(publicState),managed_jobs:managed_job_ids.length};
    }
    configureAutomation(value) {
        if(!object(value)||typeof value.enabled!=='boolean')
            throw Error('Choose whether to arm or disarm native queue automation.');
        if(value.enabled===false) {
            if(value.confirm_disarm!==true)
                throw Error('Explicitly confirm disarming native automation.');
            this.automation={
                enabled:false,machine_ids:[],agent_instances:{},managed_job_ids:[],
                rebalance_queued:false,start_qualified_gpu:false,claim_exclusive_gpu:false,probe_exclusive_gpu:false,claimed_lease_ids:[],
                session_id:null,armed_at:null,last_tick_at:this.automation.last_tick_at,
                last_action:'Disarmed by operator.',last_error:null,
                last_transition_at:this.automation.last_transition_at,
                waiting_reason:null,
                last_claimed_project_id:null,last_started_project_id:null,
            };
            return this.automationStatus();
        }
        if(this.automation.enabled)
            throw Error('Native automation is already armed; disarm explicitly before starting another session.');
        if(value.probe_exclusive_gpu===true&&value.claim_exclusive_gpu!==true)
            throw Error('Automatic GPU probes require automatic exclusive GPU claims.');
        const ids=value.machine_ids;
        if(value.confirm_arm!==true||value.confirm_untracked_load!==true||
            !Array.isArray(ids)||ids.length<1||ids.length>16||
            ids.some(id=>typeof id!=='string'||!ID.test(id))||
            new Set(ids).size!==ids.length)
            throw Error('Explicitly confirm 1–16 distinct machines and responsibility for untracked host workloads.');
        const snapshot=this.snapshot();
        const agentInstances={};
        for(const id of ids) {
            const machine=snapshot.machines.find(m=>m.id===id);
            const report=this.agentReports.get(id);
            if(!machine||machine.status!=='connected'||
                !['linux','win32'].includes(machine.agent_platform)||
                machine.local_executor_enabled!==true||
                machine.native_preflight_passed!==true||
                !machine.installed_projects?.length||
                (machine.agent_platform==='linux'&&
                    (machine.execution_capabilities?.cgroup_v2_detected!==true||
                        !['cpu','memory'].every(c=>
                            machine.execution_capabilities?.cgroup_controllers.includes(c))))||
                !UUID.test(String(report?.instance_id||'')))
                throw Error('Every armed machine needs a fresh authenticated native agent, preflight and installed project adapter.');
            if(snapshot.jobs.some(j=>j.assigned_machine_id===id&&
                !j.capacity_released_at&&['unverified','failed'].includes(j.status)))
                throw Error('Resolve unverified or failed native jobs before arming this machine.');
            agentInstances[id]=report.instance_id;
        }
        this.automation={
            enabled:true,machine_ids:ids.slice(),agent_instances:agentInstances,
            managed_job_ids:[],session_id:crypto.randomUUID(),
            rebalance_queued:value.rebalance_queued===true,claimed_lease_ids:[],
            start_qualified_gpu:value.start_qualified_gpu===true,
            claim_exclusive_gpu:value.claim_exclusive_gpu===true,
            probe_exclusive_gpu:value.probe_exclusive_gpu===true,
            armed_at:now(),last_tick_at:null,
            last_action:value.start_qualified_gpu===true
                ?'Armed for CPU jobs and explicitly qualified GPU claims; no job dispatched yet.'
                :value.claim_exclusive_gpu===true
                    ?'Armed for CPU jobs and exclusive GPU claims; GPU Starts require operator approval.'
                    :'Armed for qualified CPU-only jobs; no job dispatched yet.',
            last_error:null,last_transition_at:null,waiting_reason:null,
            last_claimed_project_id:null,last_started_project_id:null,
        };
        return this.automationStatus();
    }
    assertAutomationContinuity(observedAt) {
        for(const id of this.automation.machine_ids) {
            const report=this.agentReports.get(id);
            if(!report||observedAt<report.received_ms||
                observedAt-report.received_ms>=AGENT_HEARTBEAT_TTL_MS||
                report.instance_id!==this.automation.agent_instances[id]||
                report.native_preflight_passed!==true||report.local_executor!==true)
                throw Error('Machine '+id+' lost qualified agent continuity; operator must re-arm.');
        }
    }
    disarmUnsafeAutomation(error) {
        this.automation.enabled=false;
        this.automation.last_error=String(error.message||error).slice(0,240);
        this.automation.last_action='Automation disarmed; operator review required.';
        this.automation.waiting_reason=null;
    }
    // At most one durable transition per pass. Existing lifecycle methods
    // remain the only admission, start and release authorities. Stop/failed
    // recovery is deliberately manual; this never retries an uncertain job.
    tickAutomation() {
        const session=this.automation;
        if(!session.enabled)return this.automationStatus();
        session.last_tick_at=now();
        const transitioned=message=>{
            session.last_action=message;
            session.last_transition_at=now();
            session.waiting_reason=null;
            return this.automationStatus();
        };
        try {
            const snapshot=this.snapshot();
            const selected=snapshot.machines.filter(m=>
                session.machine_ids.includes(m.id));
            this.assertAutomationContinuity(Date.now());
            const unsafe=snapshot.jobs.find(j=>
                session.machine_ids.includes(j.assigned_machine_id)&&
                !j.capacity_released_at&&
                ['unverified','failed'].includes(j.status));
            if(unsafe)throw Error('Job '+unsafe.id+' requires operator review before further automatic execution.');
            const complete=snapshot.jobs.find(j=>
                session.managed_job_ids.includes(j.id)&&
                session.machine_ids.includes(j.assigned_machine_id)&&
                !j.capacity_released_at&&j.execution_updated_at&&
                ['finished','stopped'].includes(j.status)&&
                snapshot.reservations.some(l=>l.id===j.lease_id&&
                    l.job_id===j.id&&l.project_id===j.project_id&&
                    l.machine_id===j.assigned_machine_id&&
                    l.issued_by==='experiment-hub'&&l.status==='acknowledged'));
            if(complete) {
                this.releaseLease({lease_id:complete.lease_id,confirm_release:true});
                session.managed_job_ids=session.managed_job_ids.filter(id=>id!==complete.id);
                return transitioned('Released verified capacity: '+complete.project_id+
                    ' / '+(complete.configuration_name||complete.id.slice(-8))+'.');
            }
            const pending=snapshot.jobs.filter(j=>
                j.adapter==='unconfigured'&&j.status==='waiting_adapter'&&
                j.project_id!=='bees');
            // Claims and starts can become eligible at different times,
            // so fairness needs an independent cursor for each operation.
            const pendingStarts=fairPending(pending,session.last_started_project_id);
            const pendingClaims=fairPending(pending,session.last_claimed_project_id);
            const blockedMachines=new Set(snapshot.jobs.filter(j=>
                ['stop_requested','unverified','failed'].includes(j.status)&&
                !j.capacity_released_at).map(j=>j.assigned_machine_id));
            // A coordinator Start authorization is durable and can outlive
            // the next scheduler pass. Wait for its native acknowledgement
            // before authorizing another service launch on the same host.
            const startingMachines=new Set(snapshot.jobs.filter(j=>
                ['start_requested','launch_requested'].includes(j.status)&&
                !j.capacity_released_at).map(j=>j.assigned_machine_id));
            for(const job of pendingStarts) {
                if((job.resources.gpu_count!==0&&!session.start_qualified_gpu)||!job.lease_id||
                    !session.machine_ids.includes(job.assigned_machine_id)||
                    blockedMachines.has(job.assigned_machine_id)||
                    startingMachines.has(job.assigned_machine_id)||
                    job.native_start_readiness?.eligible!==true)continue;
                this.authorizeStart({job_id:job.id,confirm_start:true},'automation');
                session.managed_job_ids.push(job.id);
                session.last_started_project_id=job.project_id;
                return transitioned('Authorized '+(job.resources.gpu_count?'qualified GPU':'CPU-only')+
                    ' Start: '+job.project_id+
                    ' / '+(job.configuration_name||job.id.slice(-8))+'.');
            }
            const capable=selected.filter(m=>!blockedMachines.has(m.id)&&
                ['linux','win32'].includes(m.agent_platform)&&
                (m.agent_platform!=='linux'||
                    (m.execution_capabilities?.cgroup_v2_detected===true&&
                     ['cpu','memory'].every(c=>
                        m.execution_capabilities?.cgroup_controllers.includes(c)))));
            const overcommitted=new Set(selected.filter(m=>m.capacity_overcommitted).map(m=>m.id));
            const queuedLeases=new Set(pending.map(job=>job.lease_id));
            session.claimed_lease_ids=session.claimed_lease_ids.filter(id=>queuedLeases.has(id));
            const owned=new Set(session.claimed_lease_ids);
            const canHostJob=(machine,job)=>
                machine.installed_projects?.includes(job.project_id)&&
                (job.resources.gpu_count===0||
                    machine.agent_platform==='linux'&&
                    machine.gpu_projects?.includes(job.project_id)&&
                    machine.observed_gpus?.length>=2);
            const eligibleHosts=job=>capable.filter(machine=>canHostJob(machine,job));
            if(session.rebalance_queued) {
                for(const job of pendingClaims) {
                    if(!owned.has(job.lease_id)||job.dispatch_id||job.continuation_machine_id)
                        continue;
                    const gpu=job.resources.gpu_count!==0;
                    // Keep any already requested or observed probe with its claim.
                    if(gpu&&job.gpu_qualification?.lease_id===job.lease_id)continue;
                    const source=selected.find(m=>m.id===job.assigned_machine_id);
                    const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                    if(!source||!lease)continue;
                    const gpuUnavailable=gpu&&(!canHostJob(source,job)||
                        physicalGpuBindings(source,{
                            gpu_inventory_source:source.gpu_inventory_source,
                            gpu_devices:source.observed_gpus,
                        },lease.resources,lease.gpu_bindings)===null);
                    if(!overcommitted.has(source.id)&&!gpuUnavailable)continue;
                    const eligible=eligibleHosts(job).filter(m=>
                        m.id!==source.id&&!overcommitted.has(m.id));
                    const chosen=allocationChoice(eligible,snapshot.reservations,job);
                    if(!chosen)continue;
                    const moved=this.moveJobClaim({job_id:job.id,expected_lease_id:job.lease_id,
                        machine_id:chosen.machine_id,confirm_move:true});
                    session.claimed_lease_ids=session.claimed_lease_ids
                        .filter(id=>id!==moved.released_lease_id).concat(moved.lease_id);
                    return transitioned('Moved queued '+(gpu?'GPU':'CPU')+' claim from '+job.assigned_machine_id+
                        ' to '+moved.machine_id+'; awaiting a new receipt.');
                }
            }
            if(session.probe_exclusive_gpu) {
                for(const job of pendingClaims) {
                    if(job.resources.gpu_count!==1||!owned.has(job.lease_id)||
                        job.gpu_qualification?.lease_id===job.lease_id||
                        startingMachines.has(job.assigned_machine_id))
                        continue;
                    const machine=capable.find(m=>m.id===job.assigned_machine_id);
                    const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                    if(!machine?.gpu_projects?.includes(job.project_id)||
                        (machine.observed_gpus?.length||0)<2||!lease?.receipt_fresh)
                        continue;
                    this.requestGpuQualification({job_id:job.id,confirm_qualify:true},'automation');
                    return transitioned('Requested exclusive GPU access probe on '+machine.id+
                        ' for '+job.configuration_name+'.');
                }
            }
            const claimable=job=>{
                const r=job.resources;
                return !job.lease_id&&r.ram_mb>=128&&(
                    r.gpu_count===0&&!(r.gpus||[]).length||
                    session.claim_exclusive_gpu&&r.gpu_count===1&&r.gpu_mode==='exclusive');
            };
            for(const [index,job] of pendingClaims.entries()) {
                if(!claimable(job))continue;
                // Keep the fair project order while preserving sole fitting
                // hosts for later CPU or exclusive GPU jobs.
                const following=pendingClaims.slice(index+1).filter(claimable);
                const chosen=allocationChoice(eligibleHosts(job),snapshot.reservations,
                    job,null,following,eligibleHosts);
                if(!chosen)continue;
                const result=this.allocateJobs({
                    all_projects:true,job_ids:[job.id],
                    machine_id:chosen.machine_id,confirm_allocate:true,
                });
                if(result.results[0]?.status!=='claimed')
                    throw Error('Selected placement did not produce an atomic claim.');
                if(session.rebalance_queued||job.resources.gpu_count!==0&&session.probe_exclusive_gpu)
                    session.claimed_lease_ids.push(result.results[0].lease_id);
                session.last_claimed_project_id=job.project_id;
                return transitioned((job.resources.gpu_count?'Claimed exclusive GPU: ':'Claimed CPU/RAM: ')+
                    job.project_id+' / '+
                    (job.configuration_name||job.id.slice(-8))+
                    ' on '+chosen.machine_id+'; awaiting agent receipt.');
            }
            const manualComplete=snapshot.jobs.some(j=>
                session.machine_ids.includes(j.assigned_machine_id)&&
                !session.managed_job_ids.includes(j.id)&&!j.capacity_released_at&&
                ['finished','stopped'].includes(j.status)&&j.lease_id);
            session.waiting_reason=manualComplete
                ?'Completed jobs outside this automation session retain capacity until manually released.'
                :overcommitted.size
                    ?'Outstanding claims exceed CPU/RAM capacity on '+[...overcommitted].join(', ')+
                        '; move idle claims or release verified capacity.'
                    :session.start_qualified_gpu||session.claim_exclusive_gpu
                        ?'Awaiting capacity, agent receipts or an operator GPU access probe.'
                        :'No eligible CPU-only transition; awaiting agent receipts or available capacity.';
            return this.automationStatus();
        }catch(error) {
            // An ambiguous state must never provoke repeated automatic
            // dispatches. Native services and claims remain untouched.
            this.disarmUnsafeAutomation(error);
            return this.automationStatus();
        }
    }
    evaluationTargets(projectId,machineId,suiteId) {
        this.project(projectId);
        if(!ID.test(machineId||'')||!ID.test(suiteId||'')||
            !this.state.machines.some(machine=>machine.id===machineId))
            throw Error('Select a registered machine and evaluation suite.');
        if(projectId==='tinybrains') {
            const report=this.agentReports.get(machineId);
            const age=Date.now()-(report?.received_ms??0);
            if(!report?.local_executor||report.platform!=='win32'||
                age<0||age>=AGENT_HEARTBEAT_TTL_MS||
                !report.evaluation_suites?.tinybrains?.includes(suiteId))
                return [];
        }
        return this.state.jobs.filter(job=>
            job.project_id===projectId&&job.assigned_machine_id===machineId&&
            job.status==='running'&&job.evaluation_stop?.suite_id===suiteId&&
            UUID.test(job.dispatch_id||'')&&UUID.test(job.artifact_lineage_root_id||job.id))
            .map(job=>({job_id:job.id,dispatch_id:job.dispatch_id,
                artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                min_trials:job.evaluation_stop.min_trials,
                min_success_rate:job.evaluation_stop.min_success_rate}));
    }
    progressComparison(projectId) {
        this.project(projectId);
        return this.progressHistory.snapshot(projectId,this.state.jobs);
    }
    // These are conservative coordinator-side claims, NOT operating system
    // isolation or an authorization to execute a workload.
    // Build an offer in a caller-owned registry copy. A batch of job claims
    // can then be durably committed together or not at all.
    offerIn(next, projectId, machineId, wanted, jobId = null,
        machine=this.snapshot().machines.find(m=>m.id===machineId)) {
        const project=this.project(projectId);
        if(!machine || machine.id!==machineId || machine.status!=='connected')
            throw Error('Machine must have a currently authenticated agent.');
        const place=resourcePlacements([machine],next.reservations,wanted)[0];
        if(!place.online || !place.capacity_fits)
            throw Error('Requested capacity is unavailable on this machine.');
        if(next.reservations.filter(r=>
            ['active','offered','acknowledged'].includes(r.status)).length>=100)
            throw Error('Maximum simultaneous coordination claims reached.');
        const lease={
            id:crypto.randomUUID(),issued_by:'experiment-hub',
            project_id:project.id,machine_id:machine.id,
            status:'offered',created_at:now(),acknowledged_at:null,
            released_at:null,
            resources:{...wanted,gpus:place.suggested_gpus},
            gpu_bindings:place.suggested_gpus.map(id=>{
                const observed=machine.verified_gpus?.find(g=>g.id===id);
                if(!observed||!PCI.test(observed.pci_address))
                    throw Error('Cannot freeze physical GPU identity at lease allocation.');
                return {id,pci_address:observed.pci_address,
                    vram_mb:observed.vram_mb};
            }),
        };
        if(jobId!==null)lease.job_id=jobId;
        // Terminal jobs still need their original lease to prove native
        // ownership on continuation, dependency creation and report replay.
        const retainedJobLeases=new Set(next.jobs
            .filter(job=>job.capacity_released_at&&job.lease_id)
            .map(job=>job.lease_id));
        const standaloneHistory=next.reservations.filter(r=>
            r.status==='released'&&!retainedJobLeases.has(r.id)).slice(-100);
        next.reservations=next.reservations.filter(r=>
            r.status!=='released'||retainedJobLeases.has(r.id))
            .concat(standaloneHistory,lease);
        return lease;
    }
    requestLease(value) {
        if(!object(value)||value.confirm_request!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Select a machine and explicitly confirm the coordination lease.');
        const next=copy(this.state);
        const lease=this.offerIn(next,value.project_id,value.machine_id,
            resourceRequest(value.resources));
        this.commit(next);
        return {...copy(lease),receipt_fresh:false};
    }
    // Releasing a Hub-owned job claim also unassigns its unstarted job
    // in the same durable update, so job and lease can never disagree.
    releaseIn(next, lease) {
        if(!lease || lease.issued_by!=='experiment-hub' ||
            !['offered','acknowledged'].includes(lease.status))
            throw Error('Only an outstanding Hub coordination lease can be released.');
        if(lease.job_id) {
            const job=next.jobs.find(j=>j.id===lease.job_id);
            if(!job || job.lease_id!==lease.id||
                !['waiting_adapter','finished','failed','stopped'].includes(job.status))
                throw Error('Cannot release an unknown, active, or unverified managed job.');
            if(job.status==='waiting_adapter') {
                if(job.dispatch_id)throw Error('Cannot release a claim after native dispatch.');
                if(gpuProbeHoldsClaim(job))
                    throw Error('Wait for the offered GPU probe and verified cleanup before releasing its claim.');
                if(job.gpu_qualification?.status==='requested') {
                    job.gpu_qualification.status='cancelled';
                    job.gpu_qualification.diagnostic=
                        'Lease released before GPU access proof completed.';
                }
                job.lease_id=null;
                job.assigned_machine_id=null;
                job.assigned_at=null;
                job.status_reason='No allocation or trusted execution adapter is installed.';
            }else {
                // Retain historical machine and lease IDs for provenance after
                // a native terminal report has been received and reviewed.
                job.capacity_released_at=now();
                // Successful native completion and capacity release are
                // the only automatic dependency advancement authorities.
                // Checkpoint-continuations preserve lineage and host;
                // independent sequences preserve neither.
                if(job.status==='finished') {
                    const children=next.jobs.filter(j=>
                        j.status==='waiting_predecessor'&&
                        (j.continuation_of_job_id===job.id||
                         j.after_job_id===job.id));
                    if(children.length>1)throw Error(
                        'Multiple successor jobs claim one predecessor; refusing release.');
                    const child=children[0];
                    if(child?.continuation_of_job_id) {
                        if(child.project_id!==job.project_id||
                            child.artifact_lineage_root_id!==
                                (job.artifact_lineage_root_id||job.id)||
                            (job.continuation_machine_id&&
                                job.continuation_machine_id!==job.assigned_machine_id)||
                            (child.continuation_machine_id&&
                                child.continuation_machine_id!==job.assigned_machine_id))
                            throw Error('Prequeued stage lineage changed; refusing capacity release.');
                        child.continuation_machine_id=job.assigned_machine_id;
                    }else if(child) {
                        if(child.after_job_id!==job.id||
                            child.continuation_of_job_id||
                            child.artifact_lineage_root_id!==child.id||
                            child.continuation_machine_id)
                            throw Error('Independent sequence identity changed; refusing capacity release.');
                    }
                    if(child) {
                        child.status='waiting_adapter';
                        child.status_reason='Predecessor finished and released capacity; job is eligible for separate allocation and native authorization.';
                        child.predecessor_ready_at=now();
                    }
                }
            }
        }
        lease.status='released';
        lease.released_at=now();
        return lease;
    }
    releaseLease(value) {
        if(!object(value)||value.confirm_release!==true||
            typeof value.lease_id!=='string')
            throw Error('Explicitly confirm which lease should be released.');
        const next=copy(this.state);
        const lease=next.reservations.find(x=>x.id===value.lease_id &&
            x.issued_by==='experiment-hub');
        if(!lease)throw Error('Hub-issued lease not found.');
        if(lease.status!=='released') {
            this.releaseIn(next,lease);
            this.commit(next);
        }
        this.leaseReceipts.delete(lease.id);
        return {...copy(lease),receipt_fresh:false};
    }
    // Queue placement is operator-triggered and advisory, never a process launch.
    // All selected jobs, claim records, and linked IDs commit atomically.
    allocateJobs(value) {
        if(!object(value)||value.confirm_allocate!==true)
            throw Error('Explicitly confirm the advisory allocation.');
        const allProjects=value.all_projects===true;
        if(allProjects && value.project_id!=null)
            throw Error('Specify either all projects or one project.');
        const project=allProjects?null:this.project(value.project_id);
        if(project?.adapter==='bees')
            throw Error('Bees uses its existing coordinator, not Hub allocation.');
        const requested=value.job_ids;
        if(requested!==undefined && (!Array.isArray(requested) ||
            requested.length<1 || requested.length>100 ||
            new Set(requested).size!==requested.length ||
            requested.some(id=>typeof id!=='string')))
            throw Error('Select 1–100 distinct pending job IDs.');
        const next=copy(this.state);
        const candidates=next.jobs.filter(j=>
            (allProjects||j.project_id===project.id) &&
            j.adapter==='unconfigured' &&
            j.status==='waiting_adapter' &&
            (requested===undefined || requested.includes(j.id)));
        // Fair interleaving across project queues, preserving FIFO within
        // each project. This is only a claim planner, not job execution.
        const jobs=fairPending(candidates);
        if(requested && jobs.length!==requested.length)
            throw Error('One or more selected jobs are unavailable or no longer pending.');
        if(!jobs.length)throw Error('No pending jobs to allocate.');
        if(value.machine_id!=null &&
            (!ID.test(String(value.machine_id))||jobs.length!==1))
            throw Error('A preferred machine may only be selected for one job.');
        const machines=this.snapshot().machines.filter(m=>m.status==='connected');
        const results=[];
        for(const [index,job] of jobs.entries()) {
            if(job.lease_id) {
                const lease=next.reservations.find(l=>l.id===job.lease_id &&
                    l.job_id===job.id && l.project_id===job.project_id &&
                    ['offered','acknowledged'].includes(l.status));
                if(!lease)throw Error('Job allocation ledger inconsistent; no changes committed.');
                results.push({job_id:job.id,machine_id:lease.machine_id,
                    lease_id:lease.id,status:'already_claimed'});
                continue;
            }
            const chosen=allocationChoice(machines,next.reservations,job,
                value.machine_id||null,jobs.slice(index+1));
            if(!chosen) {
                results.push({job_id:job.id,machine_id:null,lease_id:null,
                    status:'insufficient_capacity',
                    reason:allocationBlockReason(machines,job,value.machine_id)});
                continue;
            }
            const lease=this.claimJobIn(next,job,chosen.machine_id);
            results.push({job_id:job.id,machine_id:lease.machine_id,
                lease_id:lease.id,status:'claimed'});
        }
        if(results.some(r=>r.status==='claimed'))this.commit(next);
        return {results,executable:false,
            note:'Coordination only: no process started or hardware limits enforced.'};
    }
    claimJobIn(next,job,machineId,machine) {
        const lease=this.offerIn(next,job.project_id,machineId,
            resourceRequest(job.resources),job.id,machine);
        job.lease_id=lease.id;
        job.assigned_machine_id=lease.machine_id;
        job.assigned_at=now();
        job.status_reason='Hub coordination capacity claimed; execution still blocked.';
        return lease;
    }
    moveJobClaim(value) {
        const result=this.moveJobClaims({moves:[value],confirm_move:value?.confirm_move});
        return result.moves[0];
    }
    moveJobClaims(value) {
        const moves=value?.moves;
        if(!object(value)||value.confirm_move!==true||!Array.isArray(moves)||
            moves.length<1||moves.length>100||moves.some(move=>!object(move)||
                !UUID.test(String(move.job_id||''))||
                !UUID.test(String(move.expected_lease_id||''))||
                !ID.test(String(move.machine_id||'')))||
            new Set(moves.map(move=>move.job_id)).size!==moves.length)
            throw Error('Select distinct queued claims and explicitly confirm their destinations.');
        const next=copy(this.state);
        const machines=new Map(this.snapshot().machines.map(machine=>[machine.id,machine]));
        const transfers=moves.map(move=>{
            const job=next.jobs.find(j=>j.id===move.job_id);
            if(!job||job.adapter!=='unconfigured'||job.status!=='waiting_adapter'||job.dispatch_id)
                throw Error('Only an unstarted non-Bees job claim can move.');
            if(job.lease_id!==move.expected_lease_id)
                throw Error('A job claim changed. Refresh Train before moving it.');
            const source=next.reservations.find(l=>l.id===job.lease_id&&l.job_id===job.id&&
                l.project_id===job.project_id&&l.machine_id===job.assigned_machine_id);
            if(!source||source.machine_id===move.machine_id)
                throw Error('Select a different destination for the current job claim.');
            if(job.continuation_machine_id&&job.continuation_machine_id!==move.machine_id)
                throw Error('Checkpoint continuation must stay on the original machine.');
            const machine=machines.get(move.machine_id);
            if(machine?.status!=='connected'||!machine.installed_projects?.includes(job.project_id)||
                !supportsEvaluation(machine.evaluation_suites,job)||!supportsTinyBrainsProfile(machine,job))
                throw Error('Destination requires a connected adapter supporting the project, configuration and evaluation suite.');
            return {job,source,machine};
        });
        // Release all sources only in the draft ledger so full hosts can swap.
        for(const {source} of transfers)this.releaseIn(next,source);
        // Reserve exact device requests before flexible ones at each destination.
        const ordered=transfers.slice().sort((a,b)=>
            Number(Boolean(b.job.resources.gpus?.length))-Number(Boolean(a.job.resources.gpus?.length))||
            (b.job.resources.min_gpu_vram_mb||0)-(a.job.resources.min_gpu_vram_mb||0)||
            (b.job.resources.gpu_count||0)-(a.job.resources.gpu_count||0));
        const replacements=new Map();
        for(const {job,machine} of ordered) {
            try{replacements.set(job.id,this.claimJobIn(next,job,machine.id,machine));}
            catch(error){
                throw Error('Cannot move '+job.configuration_name+' to '+machine.id+': '+error.message);
            }
        }
        this.commit(next);
        return {moves:transfers.map(({job,source,machine})=>({
            job_id:job.id,machine_id:machine.id,
            released_lease_id:source.id,lease_id:replacements.get(job.id).id,
        }))};
    }
    // Explicitly request an inert GPU device-filter proof, not a launch.
    requestGpuQualification(value,origin='operator') {
        if(!object(value)||value.confirm_qualify!==true||
            !UUID.test(String(value.job_id||'')))
            throw Error('Explicitly confirm the job whose GPU access will be probed.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        const lease=snapshot.reservations.find(l=>l.id===job?.lease_id);
        const machine=snapshot.machines.find(m=>m.id===job?.assigned_machine_id);
        const block=gpuProbeBlock(job,lease,machine);
        if(block)throw Error(block);
        const session=this.automation;
        if(origin==='automation'&&(!session.probe_exclusive_gpu||
            !session.claimed_lease_ids.includes(lease.id)||
            !this.automationSessionCurrent(session.session_id,machine.id)))
            throw Error('This session no longer authorizes an automatic GPU probe.');
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        if(job.gpu_qualification?.status==='requested') {
            if(origin!=='operator'||!job.gpu_qualification.automation_paused||
                job.gpu_qualification.lease_id!==lease.id)
                throw Error('GPU access probe is already requested for this job.');
            delete selected.gpu_qualification.automation_session_id;
        }else selected.gpu_qualification={
            request_id:crypto.randomUUID(),lease_id:lease.id,
            machine_id:machine.id,status:'requested',
            requested_at:now(),first_offered_at:null,
            ...(job.gpu_qualification?.lease_id===lease.id&&job.gpu_qualification.cleanup_required
                ?{cleanup_required:true}:{}),
            ...(origin==='automation'?{automation_session_id:session.session_id}:{}),
        };
        this.commit(next);
        return {job_id:job.id,lease_id:lease.id,
            request_id:selected.gpu_qualification.request_id,
            status:'requested',starts_training:false};
    }
    withdrawGpuQualification(value) {
        if(!object(value)||value.confirm_withdraw!==true||
            !UUID.test(String(value.expected_request_id||'')))
            throw Error('Explicitly confirm which GPU access probe to withdraw.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        const proof=job?.gpu_qualification;
        if(job?.status!=='waiting_adapter'||!job.lease_id||job.dispatch_id||
            proof?.lease_id!==job.lease_id||proof.request_id!==value.expected_request_id||
            proof.status!=='requested'||proof.first_offered_at!==null)
            throw Error('Only a matching GPU probe that has never been offered can be withdrawn.');
        proof.status='cancelled';
        proof.diagnostic='Operator withdrew this undelivered GPU access probe.';
        this.commit(next);
        return {job_id:job.id,request_id:proof.request_id,status:proof.status};
    }
    // A GPU preflight report never changes the job's execution state.
    recordGpuQualifications(next,machine,reports,instanceId) {
        if(!Array.isArray(reports)||reports.length>4)
            throw Error('Invalid GPU qualification report count.');
        const seen=new Set();
        let changed=false;
        for(const report of reports) {
            if(!object(report)||!UUID.test(String(report.job_id||''))||
                !UUID.test(String(report.lease_id||''))||
                !UUID.test(String(report.dispatch_id||''))||
                !['verified','rejected'].includes(report.result)||
                (report.cleanup_verified!==undefined&&typeof report.cleanup_verified!=='boolean')||
                (report.result==='verified'&&report.cleanup_verified===false)||
                seen.has(report.job_id)||
                !Array.isArray(report.selected_pci_addresses)||
                report.selected_pci_addresses.length>1||
                report.selected_pci_addresses.some(p=>!PCI.test(String(p)))||
                (report.diagnostic!=null&&
                    (typeof report.diagnostic!=='string'||report.diagnostic.length>160)))
                throw Error('Invalid bounded GPU preflight report.');
            seen.add(report.job_id);
            const job=next.jobs.find(j=>j.id===report.job_id);
            const lease=next.reservations.find(l=>l.id===report.lease_id);
            const request=job?.gpu_qualification;
            if(!job||!lease||lease.issued_by!=='experiment-hub'||
                lease.job_id!==job.id||lease.machine_id!==machine.id||
                lease.project_id!==job.project_id)
                throw Error('GPU proof does not belong to this job and machine.');
            // An acknowledged report may be retried after the response is
            // lost, even if the operator has since replaced the request or
            // released the lease. A stale receipt has no authority.
            if(lease.status==='released')continue;
            if(lease.status!=='acknowledged'||job.lease_id!==lease.id||
                job.assigned_machine_id!==machine.id)
                throw Error('GPU proof does not belong to this job and machine.');
            if(request?.lease_id!==lease.id||request.machine_id!==machine.id||
                request.request_id!==report.dispatch_id)
                continue;
            if(request.status!=='requested')continue;
            if(request.first_offered_at===null)
                throw Error('GPU access report arrived before its probe was offered.');
            if(job.status!=='waiting_adapter')
                throw Error('GPU access probe no longer belongs to a queued job.');
            const pinned=lease.gpu_bindings;
            const verified=report.result==='verified'&&
                report.selected_pci_addresses.length===1&&
                pinned?.length===1&&
                report.selected_pci_addresses[0]===pinned[0].pci_address;
            if(report.result==='verified'&&!verified)
                throw Error('GPU proof does not match the frozen physical PCI allocation.');
            request.status=report.result;
            request.cleanup_required=!verified&&report.cleanup_verified!==true;
            request.diagnostic=report.diagnostic||null;
            request.observed_at=now();
            request.agent_instance_id=instanceId;
            request.selected_pci_addresses=verified
                ?report.selected_pci_addresses.slice():[];
            changed=true;
        }
        return changed;
    }

    authorizeStart(value,origin='operator') {
        if(!['operator','automation'].includes(origin))
            throw Error('Unknown native Start authorization source.');
        if(!object(value)||value.confirm_start!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly confirm which queued job to start.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        const readiness=nativeStartReadiness(snapshot,job);
        if(!readiness.eligible)
            throw Error(readiness.reason);
        if(origin==='automation'&&(
            (job.resources.gpu_count!==0&&!this.automation.start_qualified_gpu)||!this.automation.enabled||
            !this.automation.machine_ids.includes(job.assigned_machine_id)||
            !UUID.test(String(this.automation.session_id||''))))
            throw Error('Native automation session no longer authorizes this Start.');
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        selected.status='start_requested';
        if(origin==='automation')
            selected.automation_start_session_id=this.automation.session_id;
        selected.dispatch_id=crypto.randomUUID();
        selected.start_requested_at=now();
        selected.start_first_offered_at=null;
        if(origin==='operator'&&value.release_on_terminal===true)
            selected.release_on_terminal=true;
        selected.status_reason='Start authorized; waiting for machine-local OS resource enforcement verification.';
        this.commit(next);
        return {id:selected.id,status:selected.status,dispatch_id:selected.dispatch_id};
    }
    withdrawStart(value) {
        if(!object(value)||value.confirm_withdraw!==true||
            !UUID.test(String(value.expected_dispatch_id||'')))
            throw Error('Explicitly confirm which Start authorization to withdraw.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if(!job||job.status!=='start_requested'||job.dispatch_id!==value.expected_dispatch_id||
            job.start_first_offered_at!==null)
            throw Error('Only a matching Start that has never been offered to a worker can be withdrawn.');
        if(this.automation.enabled&&this.automation.machine_ids.includes(job.assigned_machine_id))
            throw Error('Disarm automation for this machine before withdrawing Start.');
        job.status='waiting_adapter';
        delete job.dispatch_id;
        delete job.start_requested_at;
        delete job.start_first_offered_at;
        delete job.release_on_terminal;
        delete job.automation_start_session_id;
        job.status_reason='Undelivered Start withdrawn. The queued job retains its existing claim.';
        this.commit(next);
        return {id:job.id,status:job.status,lease_id:job.lease_id};
    }
    authorizeStop(value) {
        if(!object(value)||value.confirm_stop!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly confirm which managed job to stop.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if(!job||!job.dispatch_id||
            !['start_requested','launch_requested','running','unverified'].includes(job.status))
            throw Error('Only a dispatched, nonterminal job may request a native stop.');
        const lease=next.reservations.find(l=>l.id===job.lease_id &&
            l.job_id===job.id&&l.status==='acknowledged');
        if(job.status==='start_requested'&&job.start_first_offered_at===null)
            return this.withdrawStart({job_id:job.id,expected_dispatch_id:job.dispatch_id,
                confirm_withdraw:true});
        if(!lease)throw Error('Job/lease ownership cannot be verified.');
        job.status='stop_requested';
        job.stop_requested_at=now();
        job.status_reason='Stop requested; awaiting local service confirmation.';
        this.commit(next);
        return {id:job.id,status:job.status};
    }
    // The report is authenticated by the machine-token heartbeat. A second
    // machine, retired dispatch ID or unrelated lease cannot update the job.
    recordExecution(next,machine,items,accepted=new Map(),evaluationSuites={},
        receivedMs=Date.now()) {
        if(!Array.isArray(items)||items.length>50)
            throw Error('Invalid execution reports.');
        const seen=new Set();
        const observedAt=new Date(receivedMs).toISOString();
        let changed=false;
        for(const report of items) {
            if(!object(report)||typeof report.job_id!=='string'||
                typeof report.lease_id!=='string'||
                typeof report.dispatch_id!=='string'||
                !['launch_requested','running','stop_requested','finished','failed','stopped','unverified']
                    .includes(report.state)||
                seen.has(report.job_id)||
                (report.diagnostic!=null&&
                    (typeof report.diagnostic!=='string'||report.diagnostic.length>220))||
                (report.progress_issue!=null&&
                    (typeof report.progress_issue!=='string'||
                        report.progress_issue.length>160))||
                ['memory_current_mb','cpu_time_ns','peak_job_memory_bytes','process_tasks'].some(k=>
                    report[k]!=null&&
                    (!Number.isSafeInteger(report[k])||report[k]<0)))
                throw Error('Invalid native execution report.');
            seen.add(report.job_id);
            const job=next.jobs.find(j=>j.id===report.job_id);
            const lease=next.reservations.find(l=>l.id===report.lease_id);
            if(!job||job.start_first_offered_at===null||!lease||
                job.dispatch_id!==report.dispatch_id||
                job.lease_id!==lease.id||lease.job_id!==job.id||
                lease.machine_id!==machine.id||job.assigned_machine_id!==machine.id||
                lease.project_id!==job.project_id)
                throw Error('Native execution report does not match its job and machine.');
            // An agent can lose the HTTP acknowledgement after the Hub has
            // committed a terminal report and an operator has released its
            // capacity. Accept only the identical terminal state and exact
            // former ownership; never restore the lease or update the job.
            if(lease.status==='released'&&lease.released_at&&
                job.capacity_released_at&&
                ['finished','failed','stopped'].includes(job.status)&&
                report.state===job.status)
                continue;
            if(lease.status!=='acknowledged')
                throw Error('Native execution report requires an acknowledged lease.');
            // Metrics cannot grant authority or change status: they are
            // validated only after machine/job/dispatch ownership checks.
            const progress=report.progress==null?null:
                validateProgress(report.progress,job.project_id);
            if(['finished','failed','stopped'].includes(job.status))continue;
            if(!['start_requested','launch_requested','running',
                    'stop_requested','unverified'].includes(job.status))
                throw Error('Job is not awaiting a native execution report.');
            // Inspect an unverified service before retrying Stop. Once the
            // worker confirms it is running, restore the durable Stop intent.
            const state=report.state==='running'&&
                (job.status==='stop_requested'||job.stop_requested_at)
                ?'stop_requested':report.state;
            if(state==='launch_requested'&&job.status!=='start_requested')continue;
            if(job.status==='stop_requested'&&report.state==='launch_requested')continue;
            // Only reports accepted into the lifecycle may update live
            // telemetry and comparison samples. A delayed older report
            // must not make a terminal job appear to be running again.
            let evaluation=null,evaluationIssue=null;
            if(state==='running'&&job.evaluation_stop&&
                supportsEvaluation(evaluationSuites,job)) {
                evaluationIssue=typeof report.evaluation_issue==='string'
                    ?report.evaluation_issue.slice(0,160):null;
                if(report.evaluation!=null) {
                    try {
                        evaluation=evaluationEvidence(
                            report.evaluation,job.evaluation_stop,job.dispatch_id);
                    }catch(error) {
                        evaluationIssue=error.message;
                    }
                }
            }
            const previous=this.executionTelemetry.get(job.id);
            const resources=summarizeResourceSample(
                previous?.dispatch_id===job.dispatch_id?previous.resource_usage:null,
                report,observedAt);
            accepted.set(report.job_id,{progress,evaluation,evaluationIssue,resources});
            if(job.status!==state||
                (job.execution_diagnostic||null)!==(report.diagnostic||null)) {
                job.status=state;
                job.status_reason=state==='unverified'
                    ?'Native service state requires inspection; no automatic retry.'
                    :state==='stop_requested'
                    ?'Stop requested; native process still reported running.'
                    :state==='running'
                    ?'Locally verified native service running.'
                    :'Native service state reported by its machine-local supervisor.';
                job.execution_diagnostic=report.diagnostic||null;
                job.execution_updated_at=now();
                changed=true;
            }
            if(evaluation&&evaluation.trials>=job.evaluation_stop.min_trials&&
                evaluation.successes/evaluation.trials>=job.evaluation_stop.min_success_rate) {
                job.status='stop_requested';
                job.stop_requested_at=now();
                job.status_reason='Independent evaluation target met; awaiting native Stop.';
                job.evaluation_stop_evidence=evaluation;
                changed=true;
            }else if(evaluation&&JSON.stringify(evaluation)!==JSON.stringify(job.last_evaluation)) {
                job.last_evaluation=evaluation;
                changed=true;
            }
            // Keep the last accepted source version if the terminal report
            // omitted progress or supplied an older/unrelated summary.
            // This observation never authorizes an execution transition.
            if(['finished','failed','stopped'].includes(job.status)) {
                if(resources)job.final_resource_usage=resources;
                const source=job.project_id==='ants'
                    ?'ants-summary-v1':'hub-metrics-v1';
                const latest=previous?.dispatch_id===job.dispatch_id&&
                    previous.progress?.source===source
                    ?previous.progress:null;
                const selected=latest&&(!progress||
                    progress.experiment_id!==latest.experiment_id||
                    Date.parse(progress.source_modified_at)<
                        Date.parse(latest.source_modified_at))
                    ?latest:progress;
                if(selected&&!job.final_progress) {
                    job.final_progress=selected;
                    changed=true;
                }
                if(job.release_on_terminal&&['finished','stopped'].includes(job.status))
                    this.releaseIn(next,lease);
            }
        }
        return changed;
    }
    executionOrders(machineId) {
        const snapshot=this.snapshot();
        const machine=snapshot.machines.find(m=>m.id===machineId);
        if(!machine||machine.status!=='connected'||
            machine.local_executor_enabled!==true)return [];
        const jobs=snapshot.jobs.filter(j=>j.assigned_machine_id===machineId &&
            (j.dispatch_id&&['start_requested','launch_requested','running',
                'stop_requested','unverified'].includes(j.status)||
             j.status==='waiting_adapter'&&
                j.gpu_qualification?.status==='requested'));
        // A native Stop acknowledgement means the local supervisor has
        // persisted its stop intent. Further observations only need Inspect;
        // repeatedly dispatching Stop would starve unrelated running jobs.
        // A lost/stale acknowledgement keeps Stop urgent until confirmed.
        const actionFor=j=>j.status==='stop_requested'
            ?j.live_execution?.state==='stop_requested'?'inspect':'stop'
            :j.status==='start_requested'?'start'
            :j.gpu_qualification?.status==='requested'?'qualify_gpu':'inspect';
        const priority=j=>({stop:0,start:1,qualify_gpu:2,inspect:3})[actionFor(j)];
        jobs.sort((a,b)=>priority(a)-priority(b));
        const ordered=[];
        for(const level of [0,1,2,3]) {
            const group=jobs.filter(j=>priority(j)===level);
            // Keep a separate cursor for each priority tier. An urgent
            // Stop must not reset fair rotation among pending Starts.
            const last=this.executionOrderCursor.get(machineId+':'+level);
            if(group.length>1) {
                const position=group.findIndex(j=>j.id===last);
                if(position>=0)
                    group.push(...group.splice(0,position+1));
            }
            ordered.push(...group);
        }
        for(const job of ordered) {
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id&&l.project_id===job.project_id&&
                l.machine_id===machineId&&l.status==='acknowledged');
            if(!lease)continue;
            const adapterInstalled=machine.installed_projects.includes(job.project_id);
            let action=actionFor(job);
            if(action==='start') {
                const automationValid=!job.automation_start_session_id||
                    (this.automation.enabled&&
                     this.automation.session_id===job.automation_start_session_id&&
                     this.automation.agent_instances[machineId]===
                        this.agentReports.get(machineId)?.instance_id);
                if(!adapterInstalled||!automationValid||!lease.receipt_fresh||
                    machine.native_preflight_passed!==true||
                    !supportsEvaluation(machine.evaluation_suites,job)||
                    !supportsTinyBrainsProfile(machine,job)||
                    gpuStartBlock(job,lease,machine)) {
                    // A previously offered Start may already own a process.
                    // Recover observation without re-authorizing a launch.
                    if(typeof job.start_first_offered_at!=='string')continue;
                    action='inspect';
                }
            }
            if(action==='qualify_gpu'&&(
                job.gpu_qualification.lease_id!==lease.id||
                job.gpu_qualification.automation_paused||
                gpuProbeBlock(job,lease,machine,job.gpu_qualification.first_offered_at===null)))
                continue;
            if(action==='start'&&job.start_first_offered_at==null) {
                // Persist the point of no return before the order can leave
                // the coordinator, including when the HTTP reply is lost.
                const next=copy(this.state);
                next.jobs.find(j=>j.id===job.id).start_first_offered_at=now();
                this.commit(next);
            }
            if(action==='qualify_gpu'&&job.gpu_qualification.first_offered_at==null) {
                const next=copy(this.state);
                next.jobs.find(j=>j.id===job.id).gpu_qualification.first_offered_at=now();
                this.commit(next);
            }
            this.executionOrderCursor.set(machineId+':'+priority(job),job.id);
            return [{
                action,dispatch_id:action==='qualify_gpu'
                    ?job.gpu_qualification.request_id:job.dispatch_id,
                job:{
                    id:job.id,project_id:job.project_id,adapter:job.adapter,
                    lease_id:job.lease_id,status:'waiting_adapter',
                    frozen_parameters:copy(job.frozen_parameters),
                    configuration_sha256:job.configuration_sha256,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                    resources:copy(job.resources),
                    ...(job.evaluation_stop?{evaluation_stop:copy(job.evaluation_stop)}:{}),
                },
                lease:{
                    id:lease.id,job_id:job.id,project_id:lease.project_id,
                    machine_id:lease.machine_id,issued_by:lease.issued_by,
                    status:lease.status,receipt_fresh:lease.receipt_fresh,
                    resources:copy(lease.resources),
                    gpu_bindings:copy(lease.gpu_bindings||[]),
                },
            }];
        }
        return [];
    }
    bindMachineGpus(value) {
        if(!object(value)||value.confirm_bind!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Explicitly confirm the machine whose GPU bindings will change.');
        const gpu=configuredGpus(value.gpus,true);
        const snapshot=this.snapshot();
        const machine=snapshot.machines.find(m=>m.id===value.machine_id);
        if(!machine||machine.status!=='connected'||
            machine.gpu_inventory_source!=='amdgpu-sysfs-v1')
            throw Error('A fresh authenticated Linux AMDGPU inventory is required to bind physical GPUs.');
        if(gpu.some(g=>!machine.observed_gpus.some(actual=>
            actual.pci_address===g.pci_address)))
            throw Error('Configured PCI GPU does not match a currently observed physical device.');
        if(this.state.reservations.some(l=>l.machine_id===machine.id&&
            ['active','offered','acknowledged'].includes(l.status)&&
            (l.resources?.gpu_count>0||(l.resources?.gpus||[]).length)))
            throw Error('Release all GPU-bearing leases before rebinding machine GPU identities.');
        const next=copy(this.state);
        next.machines.find(m=>m.id===machine.id).gpus=gpu;
        this.commit(next);
        return {machine_id:machine.id,gpus:copy(gpu)};
    }
    enrollMachine(value) {
        if (!object(value) || !ID.test(String(value.machine_id || '')))
            throw Error('Invalid machine identity.');
        const next = copy(this.state);
        const machine = next.machines.find(x => x.id === value.machine_id);
        if (!machine) throw Error('Machine not registered.');
        if (machine.agent_key_sha256 && value.confirm_rotate !== true)
            throw Error('Explicitly confirm replacing this agent credential.');
        if (value.confirm_enroll !== true)
            throw Error('Explicitly confirm issuing a machine credential.');
        // 256-bit secrets are not stored in plaintext. This value is returned
        // only by this enrollment call; future snapshots never reveal it.
        const token = crypto.randomBytes(32).toString('hex');
        machine.agent_key_sha256 = fingerprint(machine.id, token);
        this.commit(next);
        this.agentReports.delete(machine.id);
        for (const lease of this.state.reservations)
            if (lease.machine_id===machine.id) this.leaseReceipts.delete(lease.id);
        for(const job of this.state.jobs)
            if(job.assigned_machine_id===machine.id)
                this.executionTelemetry.delete(job.id);
        return {machine_id:machine.id, agent_token:token};
    }
    revokeMachine(value) {
        if (!object(value) || !ID.test(String(value.machine_id || '')) ||
            value.confirm_revoke !== true)
            throw Error('Explicitly confirm the machine credential revocation.');
        const next = copy(this.state);
        const machine = next.machines.find(x => x.id === value.machine_id);
        if (!machine) throw Error('Machine not registered.');
        delete machine.agent_key_sha256;
        this.commit(next);
        this.agentReports.delete(machine.id);
        for (const lease of this.state.reservations)
            if (lease.machine_id===machine.id) this.leaseReceipts.delete(lease.id);
        for(const job of this.state.jobs)
            if(job.assigned_machine_id===machine.id)
                this.executionTelemetry.delete(job.id);
        return {machine_id:machine.id, revoked:true};
    }
    heartbeat(value, token) {
        if (!object(value) || !ID.test(String(value.machine_id || '')))
            throw Error('Invalid agent identity.');
        // Respond generically for unknown IDs and invalid tokens.
        const machine = this.state.machines.find(x=>x.id===value.machine_id);
        if (!machine || !machine.agent_key_sha256 ||
            typeof token !== 'string' || !/^[0-9a-f]{64}$/.test(token)) {
            const error = Error('Agent authentication failed.');
            error.status = 401; throw error;
        }
        const expected = Buffer.from(machine.agent_key_sha256,'hex');
        const supplied = Buffer.from(fingerprint(machine.id,token),'hex');
        if (expected.length !== supplied.length ||
            !crypto.timingSafeEqual(expected,supplied)) {
            const error = Error('Agent authentication failed.');
            error.status = 401; throw error;
        }
        const report = boundedReport(value);
        const received = Date.now();
        const instance=value.agent_instance_id;
        if(report.local_executor &&
            (typeof instance!=='string'||
             !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(instance)))
            throw Error('Native execution agent requires a process instance identity.');
        const previous=this.agentReports.get(machine.id);
        if(previous&&previous.local_executor&&
            received>=previous.received_ms&&
            received-previous.received_ms<AGENT_HEARTBEAT_TTL_MS&&
            instance!==previous.instance_id) {
            const error=Error('Another authenticated native agent instance is still active.');
            error.status=409;throw error;
        }
        const ids=value.observed_lease_ids==null?[]:value.observed_lease_ids;
        if (!Array.isArray(ids) || ids.length>100 ||
            ids.some(id=>typeof id!=='string'||!/^[0-9a-f-]{36}$/.test(id)) ||
            new Set(ids).size!==ids.length)
            throw Error('Invalid observed lease IDs.');
        // Only this authenticated machine may acknowledge its own offers.
        // No lease is evidence of OS resource enforcement.
        const next=copy(this.state);
        // Receipt IDs are based on the agent's previous offer response,
        // while this heartbeat reports the current host capacity. Reject
        // carried-over IDs if *all* outstanding claims no longer fit.
        const withinCapacity=claimsFitCpuRam(next.reservations,machine,report.cpus,report.ram_mb);
        const acknowledgedIds=new Set();
        let changed=false;
        for (const id of ids) {
            if(!withinCapacity)break;
            const lease=next.reservations.find(x=>x.id===id &&
                x.machine_id===machine.id && x.issued_by==='experiment-hub');
            if (!lease || !['offered','acknowledged'].includes(lease.status)) continue;
            if(physicalGpuBindings(machine,report,lease.resources,lease.gpu_bindings)===null)
                continue;
            acknowledgedIds.add(id);
            if (lease.status==='offered') {
                lease.status='acknowledged';
                lease.acknowledged_at=new Date(received).toISOString();
                changed=true;
            }
        }
        const reports=value.execution_reports==null?[]:value.execution_reports;
        if(reports.length&&(!report.local_executor||
            !['linux','win32'].includes(report.platform)))
            throw Error('Native lifecycle reports require a locally enabled supported executor.');
        const acceptedReports=new Map();
        if(this.recordExecution(next,machine,reports,acceptedReports,report.evaluation_suites,received))
            changed=true;
        const gpuReports=value.gpu_qualification_reports==null
            ?[]:value.gpu_qualification_reports;
        if(gpuReports.length&&(!report.local_executor||
            report.platform!=='linux'||!report.native_preflight_passed))
            throw Error('GPU access reports require a qualified Linux executor.');
        if(this.recordGpuQualifications(next,machine,gpuReports,instance))
            changed=true;
        // A GPU that disappeared or changed after a successful access
        // proof may be reenumerated with different render-node identity.
        // Re-acknowledging the same PCI address must never revive that
        // old cgroup access proof. An explicit new probe is required.
        for(const job of next.jobs) {
            const proof=job.gpu_qualification;
            if(proof?.status!=='verified'||proof.machine_id!==machine.id)
                continue;
            const lease=next.reservations.find(l=>l.id===proof.lease_id);
            if(!lease||physicalGpuBindings(machine,report,
                lease.resources,lease.gpu_bindings)===null) {
                proof.status='stale';
                proof.diagnostic='Physical GPU identity changed or disappeared; request a fresh device access probe.';
                changed=true;
            }
        }
        if (changed) this.commit(next);
        // Do not mark receipt or liveness until the durable acknowledgement
        // succeeds. A disk failure leaves the previous lease available.
        // Check the previous heartbeat before replacing it. A reconnect
        // must not erase an expired gap before the scheduler observes it.
        if(this.automation.enabled) {
            try{this.assertAutomationContinuity(received);}
            catch(error){this.disarmUnsafeAutomation(error);}
        }
        this.agentReports.set(machine.id,{
            ...report,instance_id:instance||null,received_ms:received,
        });
        // A successful heartbeat supersedes the previous receipt set for
        // this machine. Missing IDs, reduced CPU/RAM capacity, or changed GPU
        // identity revoke *freshness* immediately, without freeing the
        // persisted capacity claims or touching native processes.
        for(const lease of this.state.reservations) {
            if(lease.machine_id!==machine.id)continue;
            if(!acknowledgedIds.has(lease.id))
                this.leaseReceipts.delete(lease.id);
            else if(lease.status==='acknowledged')
                this.leaseReceipts.set(lease.id,received);
        }
        let changedHistory=false,terminalHistory=false;
        for(const item of reports) {
            if(!acceptedReports.has(item.job_id))continue;
            const {progress,evaluation,evaluationIssue,resources}=acceptedReports.get(item.job_id);
            const previousTelemetry=this.executionTelemetry.get(item.job_id);
            const previousProgress=previousTelemetry?.progress||null;
            const job=this.state.jobs.find(j=>j.id===item.job_id);
            // Comparison history rejects duplicate, out-of-order and
            // unrelated experiment snapshots. Keep the last accepted
            // progress visible while fresh native process metrics arrive.
            const progressAccepted=progress&&this.progressHistory.record(
                job.id,job.project_id,progress,received);
            if(progressAccepted)changedHistory=true;
            // A terminal report may omit new metrics. Still flush any
            // previously sampled history before the coordinator exits.
            if(['finished','failed','stopped'].includes(job.status))
                terminalHistory=true;
            // Estimate CPU-core equivalents from authenticated server
            // arrival times, not a worker-supplied wall clock. Neither a
            // restarted agent nor a replaced native dispatch is continuous.
            const elapsedMs=received-Date.parse(previousTelemetry?.at||'');
            const cpuBefore=previousTelemetry?.cpu_time_ns;
            const cpuAfter=item.cpu_time_ns;
            const continuous=previousTelemetry?.dispatch_id===item.dispatch_id&&
                previousTelemetry.agent_instance_id===instance&&
                ['running','stop_requested'].includes(previousTelemetry.state)&&
                ['running','stop_requested'].includes(item.state);
            const cpuCores=continuous&&
                Number.isSafeInteger(cpuBefore)&&Number.isSafeInteger(cpuAfter)&&
                cpuAfter>=cpuBefore&&Number.isFinite(elapsedMs)&&
                elapsedMs>=1000&&elapsedMs<=120000
                ?(cpuAfter-cpuBefore)/(elapsedMs*1000000):null;
            this.executionTelemetry.set(item.job_id,{
                at:new Date(received).toISOString(),state:item.state,
                dispatch_id:item.dispatch_id,agent_instance_id:instance||null,
                memory_current_mb:item.memory_current_mb??null,
                peak_job_memory_bytes:item.peak_job_memory_bytes??null,
                cpu_time_ns:cpuAfter??null,
                cpu_cores_used:cpuCores,
                resource_usage:resources,
                process_tasks:item.process_tasks??null,
                progress:progressAccepted?progress:previousProgress,
                progress_issue:item.progress_issue||null,
                evaluation:evaluation||previousTelemetry?.evaluation||null,
                evaluation_issue:evaluationIssue,
            });
        }
        if(changedHistory||terminalHistory)
            this.progressHistory.persist(received,terminalHistory);
        return {machine_id:machine.id,status:'connected',
            heartbeat_ttl_seconds:Math.floor(AGENT_HEARTBEAT_TTL_MS/1000),
            lease_offers:this.state.reservations.filter(x=>
                x.machine_id===machine.id && x.issued_by==='experiment-hub' &&
                ['offered','acknowledged'].includes(x.status)
            ).map(x=>({
                id:x.id,status:x.status,resources:copy(x.resources),
                // This is an expected PCI binding, not GPU isolation or
                // a process launch token. The agent checks it against sysfs.
                gpu_bindings:physicalGpuBindings(machine,report,x.resources,x.gpu_bindings)||[],
            })),
            execution_orders:this.executionOrders(machine.id)};
    }
    project(id) {
        const project = this.state.projects.find(p=>p.id===id);
        if (!project) throw Error('Unknown project.');
        return project;
    }
    addProject(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase project ID.');
        if (this.state.projects.some(p=>p.id===value.id)) throw Error('Project already exists.');
        // New projects are definitions only until a trusted server-side adapter is installed.
        const project = {
            id:value.id, name:name(value.name,'Project name'), adapter:'unconfigured',
            description:String(value.description||'').slice(0,500), created_at:now(),
        };
        const next = copy(this.state);
        next.projects.push(project);
        this.commit(next);
        return copy(project);
    }
    saveConfiguration(value) {
        if (!object(value)) throw Error('Missing configuration.');
        const project = this.project(value.project_id);
        const params = config(value.parameters);
        const stop=evaluationPolicy(value.evaluation_stop);
        if(stop&&project.adapter==='bees')
            throw Error('Bees evaluation targets use the existing Bees experiment configuration.');
        const time = now();
        const next = copy(this.state);
        let item;
        if (value.id) {
            item = next.configurations.find(c=>c.id===value.id && c.project_id===project.id);
            if (!item) throw Error('Configuration not found.');
            if (item.revision !== value.revision) throw Error('Configuration changed; reopen it before saving.');
            item.name = name(value.name,'Configuration name');
            item.parameters = params;
            item.revision += 1;
            item.updated_at = time;
        } else {
            item = {
                id:crypto.randomUUID(), project_id:project.id,
                name:name(value.name,'Configuration name'), parameters:params,
                revision:1, created_at:time, updated_at:time,
            };
            next.configurations.push(item);
        }
        if(stop)item.evaluation_stop=stop;
        else if(Object.hasOwn(value,'evaluation_stop'))delete item.evaluation_stop;
        this.commit(next);
        return copy(item);
    }
    queueConfiguration(value) {
        const next=copy(this.state);
        const job=this.queueIn(next,value);
        this.commit(next);
        return copy(job);
    }
    // Independent jobs may be chained across non-Bees projects. These
    // dependencies do not imply checkpoint inheritance, machine affinity,
    // shared experiments, or evaluation success.
    queueSequence(value) {
        if(!object(value)||value.confirm_queue!==true||
            !Array.isArray(value.stages)||value.stages.length<2||
            value.stages.length>16||
            (value.after_job_id!=null&&!UUID.test(String(value.after_job_id))))
            throw Error('Explicitly confirm 2–16 independent sequential jobs.');
        const next=copy(this.state),jobs=[];
        let predecessor=value.after_job_id??null;
        for(const stage of value.stages) {
            if(!object(stage)||Object.keys(stage).some(key=>
                !['configuration_id','expected_revision','resources'].includes(key)))
                throw Error('Each sequential job must select a saved configuration and resources.');
            const job=this.queueIn(next,{
                ...stage,...(predecessor?{after_job_id:predecessor}:{})
            });
            jobs.push(copy(job));
            predecessor=job.id;
        }
        this.commit(next);
        return {jobs,executable:false,
            note:'Independent jobs frozen together; each native Start still needs separately authorized CPU-only execution.'};
    }
    // One durable write for the full linear checkpoint-continuation
    // pipeline: either all stages have validated lineage, or none do.
    queuePipeline(value) {
        if(!object(value)||value.confirm_queue!==true||
            !Array.isArray(value.stages)||value.stages.length<2||
            value.stages.length>16)
            throw Error('Explicitly confirm a 2–16-stage pipeline.');
        const next=copy(this.state),jobs=[];
        let predecessor=value.continuation_of_job_id??null;
        for(const stage of value.stages) {
            if(!object(stage)||Object.keys(stage).some(key=>
                !['configuration_id','expected_revision','resources'].includes(key)))
                throw Error('Each pipeline stage must select one saved configuration and resource request.');
            const job=this.queueIn(next,{
                ...stage,continuation_of_job_id:predecessor,
            });
            if(!['ants','tinybrains'].includes(job.project_id))
                throw Error('Checkpoint pipelines require the Ants or TinyBrains adapter.');
            jobs.push(copy(job));
            predecessor=job.id;
        }
        this.commit(next);
        return {jobs,executable:false,
            note:'Frozen pipeline only; native execution remains subject to separate authorization.'};
    }
    queueIn(next,value) {
        if (!object(value) || typeof value.configuration_id !== 'string')
            throw Error('Select a saved configuration to queue.');
        const selected = next.configurations.find(x=>x.id===value.configuration_id);
        if (!selected) throw Error('Saved configuration not found.');
        const project=next.projects.find(p=>p.id===selected.project_id);
        if(!project)throw Error('Unknown project.');
        if (project.adapter === 'bees')
            throw Error('Bees jobs use the existing validated Bees experiment queue.');
        if (project.adapter !== 'unconfigured')
            throw Error('Unknown execution adapter; refusing to schedule.');
        if (value.expected_revision !== selected.revision)
            throw Error('Draft changed since selection; reload before queueing.');
        // Freeze the exact input and resources. No executable, shell snippet,
        // directory, or process arguments can be supplied in the job request.
        const frozen = config(selected.parameters);
        const resources = resourceRequest(value.resources);
        const ancestorId=value.continuation_of_job_id==null
            ?null:value.continuation_of_job_id;
        const afterId=value.after_job_id==null?null:value.after_job_id;
        if(afterId!==null&&ancestorId!==null)
            throw Error('A job cannot both depend on an independent predecessor and continue its checkpoint.');
        let parent=null,dependency=null,waitingPredecessor=false;
        let dependencyReady=false;
        if(ancestorId!==null) {
            if(typeof ancestorId!=='string'||!UUID.test(ancestorId))
                throw Error('Select a valid predecessor job for continuation.');
            parent=next.jobs.find(j=>j.id===ancestorId);
            const pinnedRoot=parent?.artifact_lineage_root_id||parent?.id;
            const supportsPrequeue=['ants','tinybrains'].includes(project.id);
            const futureStage=supportsPrequeue&&
                ['waiting_adapter','waiting_predecessor'].includes(parent?.status);
            if(!parent||parent.project_id!==project.id||
                !UUID.test(String(pinnedRoot||''))||
                !(parent.dispatch_id&&parent.assigned_machine_id||futureStage))
                throw Error('Continuation requires a dispatched predecessor or an eligible prequeued stage.');
            // Supported adapters can prequeue a bounded, linear sequence.
            // Future stages inherit the original host and artifact root
            // without claiming capacity or launching any process.
            waitingPredecessor=supportsPrequeue&&(
                ['start_requested','launch_requested','running'].includes(parent.status)||
                (parent.status==='finished'&&!parent.capacity_released_at)||
                futureStage);
            if(!waitingPredecessor&&
                (!['finished','stopped'].includes(parent.status)||
                 !parent.capacity_released_at))
                throw Error('Continuation requires a finished, capacity-released predecessor (or an eligible active or prequeued predecessor).');
            if(next.jobs.some(j=>
                (j.continuation_of_job_id===parent.id||
                 j.after_job_id===parent.id)&&j.status!=='cancelled'))
                throw Error('This predecessor already has a non-cancelled successor.');
        }
        if(parent&&project.id==='tinybrains'&&
            ((!waitingPredecessor&&parent.status!=='finished')||
                typeof parent.frozen_parameters?.profile_sha256!=='string'||
                parent.frozen_parameters.profile_sha256!==selected.parameters?.profile_sha256))
            throw Error('TinyBrains continuation requires identical profile digests and a predecessor that is finished or can still finish.');
        if(afterId!==null) {
            if(!UUID.test(String(afterId)))
                throw Error('Select a valid independent predecessor.');
            dependency=next.jobs.find(j=>j.id===afterId);
            if(!dependency||dependency.project_id==='bees'||
                !['waiting_adapter','waiting_predecessor',
                  'start_requested','launch_requested','running',
                  'finished'].includes(dependency.status)||
                (dependency.status==='finished'&&
                    (!dependency.dispatch_id||!dependency.execution_updated_at||
                     !dependency.assigned_machine_id)))
                throw Error('Independent dependency needs an eligible non-Bees predecessor.');
            if(next.jobs.some(j=>
                (j.continuation_of_job_id===afterId||
                 j.after_job_id===afterId)&&j.status!=='cancelled'))
                throw Error('This predecessor already has a non-cancelled successor.');
            waitingPredecessor=dependency.status!=='finished'||
                !dependency.capacity_released_at;
            if(!waitingPredecessor) {
                // A dependency selected after its parent was released must
                // still prove terminal lease ownership before freezing an
                // immediately eligible job. Pruned old history fails closed.
                const released=next.reservations.find(l=>
                    l.id===dependency.lease_id&&
                    l.job_id===dependency.id&&
                    l.project_id===dependency.project_id&&
                    l.machine_id===dependency.assigned_machine_id&&
                    l.issued_by==='experiment-hub'&&
                    l.status==='released'&&Boolean(l.released_at));
                if(!released)
                    throw Error('Cannot queue against a finished dependency whose released native lease is no longer verifiable.');
                dependencyReady=true;
            }
        }
        if(project.id==='ants'&&
            (frozen.mode==='resume')!==Boolean(parent))
            throw Error('Ants resume mode requires a linked, completed predecessor job.');
        if (next.jobs.filter(j=>['waiting_adapter',
            'waiting_predecessor'].includes(j.status)).length >= 100)
            throw Error('Too many pending jobs; cancel unused jobs first.');
        if (next.jobs.length >= 1000)
            throw Error('Job history limit reached. Export or archive older jobs before queueing more.');
        const jobId=crypto.randomUUID();
        const job = {
            id:jobId,
            project_id:project.id, adapter:project.adapter,
            continuation_of_job_id:parent?.id||null,
            after_job_id:dependency?.id||null,
            predecessor_ready_at:dependencyReady?now():null,
            artifact_lineage_root_id:parent
                ?(parent.artifact_lineage_root_id||parent.id):jobId,
            continuation_machine_id:parent
                ?(parent.continuation_machine_id||
                    (parent.dispatch_id?parent.assigned_machine_id:null)):null,
            configuration_id:selected.id,
            configuration_revision:selected.revision,
            configuration_name:selected.name,
            configuration_sha256:crypto.createHash('sha256')
                .update(JSON.stringify(frozen)).digest('hex'),
            frozen_parameters:frozen, resources,
            ...(selected.evaluation_stop
                ?{evaluation_stop:evaluationPolicy(selected.evaluation_stop)}:{}),
            status:waitingPredecessor?'waiting_predecessor':'waiting_adapter',
            status_reason:waitingPredecessor
                ?'Prequeued stage; awaiting successful predecessor completion and capacity release.'
                :'Queued; a separate machine claim and explicit native start authorization are required.',
            created_at:now(), cancelled_at:null,
        };
        next.jobs.push(job);
        return job;
    }
    // A stopped Ants predecessor never advances its pipeline automatically.
    // Operator approval only unblocks coordination: it neither claims
    // capacity nor bypasses the trusted adapter's checkpoint verification.
    approveStoppedContinuation(value) {
        if(!object(value)||value.confirm_stopped_checkpoint_risk!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly approve the stopped predecessor and possible checkpoint rollback.');
        const next=copy(this.state);
        const child=next.jobs.find(j=>j.id===value.job_id);
        if(!child||child.project_id!=='ants'||
            child.status!=='waiting_predecessor'||
            !UUID.test(String(child.continuation_of_job_id||'')))
            throw Error('Only blocked Ants continuation stages may be approved.');
        const parent=next.jobs.find(j=>j.id===child.continuation_of_job_id);
        const lease=next.reservations.find(l=>l.id===parent?.lease_id&&
            l.job_id===parent.id&&l.project_id==='ants'&&
            l.machine_id===parent.assigned_machine_id&&
            l.issued_by==='experiment-hub'&&l.status==='released'&&
            Boolean(l.released_at));
        if(!parent||parent.project_id!=='ants'||
            parent.status!=='stopped'||!parent.capacity_released_at||
            !parent.dispatch_id||!parent.execution_updated_at||
            !parent.assigned_machine_id||!lease||
            child.artifact_lineage_root_id!==
                (parent.artifact_lineage_root_id||parent.id)||
            (parent.continuation_machine_id&&
                parent.continuation_machine_id!==parent.assigned_machine_id)||
            (child.continuation_machine_id&&
                child.continuation_machine_id!==parent.assigned_machine_id))
            throw Error('Stopped predecessor lacks verified terminal ownership, released capacity, or matching artifact lineage.');
        child.continuation_machine_id=parent.assigned_machine_id;
        child.status='waiting_adapter';
        child.predecessor_approved_at=now();
        child.status_reason='Operator approved continuation after stopped predecessor; trusted adapter must still validate the latest durable checkpoint at launch.';
        this.commit(next);
        return copy(child);
    }
    // Cancel only future, never-dispatched jobs in one durable update.
    // The selected predecessor is untouched; its native supervisor remains
    // the sole execution owner.
    cancelFutureJobs(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.after_job_id||'')))
            throw Error('Explicitly confirm the predecessor whose queued successors should be cancelled.');
        const next=copy(this.state);
        let parent=next.jobs.find(j=>j.id===value.after_job_id);
        if(!parent)throw Error('Selected predecessor job not found.');
        const visited=new Set([parent.id]),future=[];
        while(true) {
            const children=next.jobs.filter(j=>j.status!=='cancelled'&&
                (j.after_job_id===parent.id||
                 j.continuation_of_job_id===parent.id));
            if(children.length>1)
                throw Error('Predecessor has conflicting successor branches; inspect registry.');
            if(!children.length)break;
            const child=children[0];
            if(visited.has(child.id)||future.length>=1000)
                throw Error('Invalid or cyclic queued dependency chain.');
            if(!['waiting_adapter','waiting_predecessor'].includes(
                    child.status)||child.dispatch_id||
                (child.status==='waiting_predecessor'&&child.lease_id))
                throw Error('A downstream job was dispatched or has uncertain state; cancel future jobs only after native review.');
            visited.add(child.id);
            future.push(child);
            parent=child;
        }
        if(!future.length)throw Error('No queued successor jobs to cancel.');
        const cancelledIds=future.map(job=>job.id);
        const expected=value.expected_job_ids;
        if(Object.hasOwn(value,'expected_job_ids')&&
            (!Array.isArray(expected)||expected.length!==cancelledIds.length||
             cancelledIds.some((id,index)=>id!==expected[index])))
            throw Error('The queued successors changed. Review the refreshed sequence before cancelling.');
        const cancellationTime=now();
        // Descendants first. Reuse existing unstarted lease release and
        // GPU-qualification cancellation, then commit every state together.
        for(const job of future.reverse()) {
            if(job.lease_id) {
                const lease=next.reservations.find(l=>l.id===job.lease_id&&
                    l.job_id===job.id&&
                    ['offered','acknowledged'].includes(l.status));
                if(!lease)
                    throw Error('Queued job lease ownership changed; no cancellation committed.');
                this.releaseIn(next,lease);
            }
            job.status='cancelled';
            job.status_reason='Future queued job cancelled by confirmed operator action.';
            job.cancelled_at=cancellationTime;
        }
        this.commit(next);
        return {cancelled_job_ids:cancelledIds,
            cancelled_count:cancelledIds.length,starts_stopped:0};
    }
    cancelJob(value) {
        if (!object(value) || typeof value.job_id!=='string' ||
            value.confirm_cancel!==true)
            throw Error('Select a queued job and explicitly confirm cancellation.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if (!job) throw Error('Job not found.');
        if (job.status==='cancelled') return copy(job);
        if (job.dispatch_id||!['waiting_adapter','waiting_predecessor'].includes(job.status))
            throw Error('Only unstarted jobs can be cancelled without an execution adapter.');
        if(next.jobs.some(j=>
            (j.continuation_of_job_id===job.id||j.after_job_id===job.id)&&
            j.status!=='cancelled'))
            throw Error('Cancel the queued successor stage before cancelling its predecessor.');
        if(job.lease_id) {
            const lease=next.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id && ['offered','acknowledged'].includes(l.status));
            if(!lease)throw Error('Job allocation ledger inconsistent; refusing cancellation.');
            this.releaseIn(next,lease);
        }
        job.status='cancelled';
        job.status_reason='Cancelled before any execution was permitted.';
        job.cancelled_at=now();
        this.commit(next);
        return copy(job);
    }
    plannedJobs() {
        const jobs=this.state.jobs.filter(j=>j.status==='waiting_adapter');
        const snapshot=this.snapshot();
        // Rehearse the same per-project FIFO interleave and best-fit
        // decisions as allocateJobs, but do not mint leases or mutate state.
        // The separate POST /api/hub/plan remains a general GPU-first
        // what-if optimizer and is not a prediction of the queue pass.
        // Claimed jobs still occupy their position in each project's
        // FIFO queue. Skipping them *before* interleaving would give
        // a project extra priority compared with allocateJobs.
        const ordered=fairPending(jobs);
        const reservations=copy(snapshot.reservations);
        const machines=snapshot.machines.filter(m=>m.status==='connected');
        const proposed=[];
        for(const [index,job] of ordered.entries()) {
            if(job.lease_id)continue;
            const claimCount=reservations.filter(r=>
                ['active','offered','acknowledged'].includes(r.status)).length;
            const chosen=claimCount<100
                ?allocationChoice(machines,reservations,job,null,
                    ordered.slice(index+1)):null;
            if(chosen)reservations.push({
                machine_id:chosen.machine_id,status:'offered',
                resources:{...resourceRequest(job.resources),
                    gpus:chosen.suggested_gpus},
            });
            proposed.push({id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,
                status:job.status,claimed:false,executable:false,
                machine_id:chosen?.machine_id||null,
                machine_name:chosen?.machine_name||null,
                suggested_gpus:chosen?.suggested_gpus||[],
                capacity_fits:Boolean(chosen),online:Boolean(chosen?.online),
                reason:claimCount>=100
                    ?'Hub claim limit reached'
                    :chosen?'Would receive advisory capacity on this agent'
                    :allocationBlockReason(machines,job),
            });
        }
        const claimed=jobs.filter(j=>j.lease_id).map(job=>{
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id && ['offered','acknowledged'].includes(l.status));
            if(!lease)return {id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,claimed:true,
                status:'ledger_inconsistent',executable:false};
            const machine=snapshot.machines.find(m=>m.id===lease.machine_id);
            return {id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,
                status:job.status,claimed:true,lease_id:lease.id,
                lease_status:lease.status,receipt_fresh:lease.receipt_fresh,
                machine_id:lease.machine_id,
                machine_name:machine?.name||lease.machine_id,
                online:machine?.status==='connected',capacity_fits:false,
                suggested_gpus:lease.resources.gpus,executable:false,
                reason:'Existing advisory lease, not a new capacity placement'};
        });
        const byId=new Map([...claimed,...proposed].map(j=>[j.id,j]));
        return {jobs:jobs.map(j=>byId.get(j.id)),note:
            'Read-only preview of the fair allocation pass, not a persistent claim, OS isolation or execution permission.'};
    }
    updateMachineBudget(value) {
        if(!object(value)||value.confirm_budget!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Explicitly confirm the machine whose Hub budget will change.');
        const cpu=value.cpus,ram=value.ram_mb;
        if(!Number.isSafeInteger(cpu)||cpu<1||cpu>4096||
            !Number.isSafeInteger(ram)||ram<128||ram>100000000)
            throw Error('Hub CPU and RAM budgets must be positive bounded integers.');
        if(this.automation.enabled)
            throw Error('Disarm native queue automation before changing machine budgets.');
        const machine=this.state.machines.find(m=>m.id===value.machine_id);
        if(!machine)throw Error('Machine not registered.');
        if(value.expected_cpus!==machine.cpus||
            value.expected_ram_mb!==machine.ram_mb)
            throw Error('Machine budget changed; refresh the displayed values before saving.');
        const claimed=this.state.reservations.filter(l=>
            l.machine_id===machine.id&&
            ['active','offered','acknowledged'].includes(l.status));
        const claimedCpu=claimed.reduce((total,l)=>total+l.resources.cpus,0);
        const claimedRam=claimed.reduce((total,l)=>total+l.resources.ram_mb,0);
        // Never reduce admission below claims for processes that may already
        // exist. Budgets are bookkeeping, not OS controls and never Stop jobs.
        if(cpu<claimedCpu||ram<claimedRam)
            throw Error('Release or finish existing Hub claims before reducing capacity below their total.');
        if(cpu===machine.cpus&&ram===machine.ram_mb)
            return {machine_id:machine.id,cpus:cpu,ram_mb:ram,
                claimed_cpus:claimedCpu,claimed_ram_mb:claimedRam,changed:false};
        const next=copy(this.state);
        const updated=next.machines.find(m=>m.id===machine.id);
        updated.cpus=cpu;updated.ram_mb=ram;
        updated.budget_updated_at=now();
        this.commit(next);
        return {machine_id:machine.id,cpus:cpu,ram_mb:ram,
            claimed_cpus:claimedCpu,claimed_ram_mb:claimedRam,changed:true};
    }
    addMachine(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase machine ID.');
        if (this.state.machines.some(m=>m.id===value.id)) throw Error('Machine already registered.');
        const cpu = Number(value.cpus), ram = Number(value.ram_mb);
        if (!Number.isSafeInteger(cpu) || cpu < 1 || cpu > 4096 ||
            !Number.isSafeInteger(ram) || ram < 128 || ram > 100000000)
            throw Error('Machine CPU and RAM capacity must be positive integers.');
        const gpu=configuredGpus(value.gpus==null?[]:value.gpus);
        const machine = {
            id:value.id, name:name(value.name,'Machine name'),
            // All machines start inactive. An installed agent must prove health
            // before claims can be applied; a manually entered inventory is not authority.
            status:'unverified', cpus:cpu, ram_mb:ram, gpus:copy(gpu),
            created_at:now(), last_seen_at:null,
        };
        const next = copy(this.state);
        next.machines.push(machine);
        this.commit(next);
        return copy(machine);
    }
}
module.exports = {ExperimentHub, writeAtomic};
