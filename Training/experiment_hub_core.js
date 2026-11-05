'use strict';

// Project-neutral definitions and job records. This never starts a Bees run.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {request: resourceRequest,placements: resourcePlacements} =
    require('./experiment_hub_resources');
const {validateProgress}=require('./experiment_hub_progress');
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
    return {cpus:value.cpus,ram_mb:value.ram_mb,
        platform:value.platform,execution,
        gpu_inventory_source:gpuSource,gpu_devices:observedGpus,
        local_executor:localExecutor&&['linux','win32'].includes(value.platform),
        native_preflight_passed:localExecutor&&
            ['linux','win32'].includes(value.platform)&&preflight,
        native_preflight_reason:reason||null,
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
    if(machine.native_preflight_passed!==true)
        return block('The local CPU/RAM supervisor preflight has not passed.');
    if(machine.agent_platform==='linux'&&
        (machine.execution_capabilities?.cgroup_v2_detected!==true||
            !['cpu','memory'].every(c=>
                machine.execution_capabilities?.cgroup_controllers.includes(c))))
        return block('Linux cgroup v2 CPU and memory controllers are unavailable.');
    if(!machine.installed_projects?.includes(job.project_id))
        return block('The assigned machine has no trusted adapter for this project.');
    const r=job.resources;
    if(!r||r.ram_mb<128||r.gpu_count!==0||
        (r.gpus||[]).length||lease.resources.gpu_count!==0)
        return block('Native GPU execution remains disabled; only CPU/RAM jobs may start.');
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
    return {eligible:true,reason:'Eligible for explicit CPU-only native Start authorization.'};
}
// Match the operator's cross-project FIFO fairness in both the read-only
// preview and the actual advisory claim pass.
function fairPending(jobs) {
    const buckets=new Map();
    for(const job of jobs) {
        if(!buckets.has(job.project_id))buckets.set(job.project_id,[]);
        buckets.get(job.project_id).push(job);
    }
    const ordered=[];
    while([...buckets.values()].some(bucket=>bucket.length)) {
        for(const bucket of buckets.values()) {
            if(bucket.length)ordered.push(bucket.shift());
        }
    }
    return ordered;
}
function allocationChoice(machines,reservations,job,preferredMachineId=null) {
    return resourcePlacements(machines,reservations,job.resources)
        .filter(p=>p.capacity_fits&&
            (!preferredMachineId||p.machine_id===preferredMachineId)&&
            (!job.continuation_machine_id||
                p.machine_id===job.continuation_machine_id))
        .sort((a,b)=>a.available.cpus-b.available.cpus||
            a.available.ram_mb-b.available.ram_mb||
            a.machine_id.localeCompare(b.machine_id))[0]||null;
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
        this.progressHistory = new ProgressHistory();
        this.executionOrderCursor = new Map();
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
                machine.agent_platform = report.platform;
                machine.execution_capabilities = copy(report.execution);
                machine.local_executor_enabled = report.local_executor;
                machine.native_preflight_passed = report.native_preflight_passed;
                machine.native_preflight_reason = report.native_preflight_reason;
                machine.installed_projects = report.installed_projects.slice();
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
            proof.current=Boolean(proof.status==='verified'&&
                job.status==='waiting_adapter'&&
                job.lease_id===proof.lease_id&&lease?.receipt_fresh&&
                machine?.status==='connected'&&
                machine.native_preflight_passed===true&&
                report?.instance_id===proof.agent_instance_id&&stillMatched);
            return job;
        });
        for(const job of snapshot.jobs) {
            if(job.status==='waiting_adapter'&&job.project_id!=='bees')
                job.native_start_readiness=nativeStartReadiness(snapshot,job);
        }
        return snapshot;
    }
    progressComparison(projectId) {
        this.project(projectId);
        return this.progressHistory.snapshot(projectId,this.state.jobs);
    }
    // These are conservative coordinator-side claims, NOT operating system
    // isolation or an authorization to execute a workload.
    // Build an offer in a caller-owned registry copy. A batch of job claims
    // can then be durably committed together or not at all.
    offerIn(next, projectId, machineId, wanted, jobId = null) {
        const project=this.project(projectId);
        const machine=this.snapshot().machines.find(m=>m.id===machineId);
        if(!machine || machine.status!=='connected')
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
        const released=next.reservations.filter(r=>r.status==='released').slice(-100);
        next.reservations=next.reservations.filter(r=>r.status!=='released')
            .concat(released,lease);
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
        for(const job of jobs) {
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
                value.machine_id||null);
            if(!chosen) {
                results.push({job_id:job.id,machine_id:null,lease_id:null,
                    status:'insufficient_capacity'});
                continue;
            }
            const lease=this.offerIn(next,job.project_id,chosen.machine_id,
                resourceRequest(job.resources),job.id);
            job.lease_id=lease.id;
            job.assigned_machine_id=lease.machine_id;
            job.assigned_at=now();
            job.status_reason='Hub coordination capacity claimed; execution still blocked.';
            results.push({job_id:job.id,machine_id:lease.machine_id,
                lease_id:lease.id,status:'claimed'});
        }
        if(results.some(r=>r.status==='claimed'))this.commit(next);
        return {results,executable:false,
            note:'Coordination only: no process started or hardware limits enforced.'};
    }
    // The administrative endpoint never supplies an executable. It persists
    // a single authorization for one already frozen, explicitly leased job.
    // Explicitly request an inert GPU device-filter proof, not a launch.
    requestGpuQualification(value) {
        if(!object(value)||value.confirm_qualify!==true||
            !UUID.test(String(value.job_id||'')))
            throw Error('Explicitly confirm the job whose GPU access will be probed.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        const lease=snapshot.reservations.find(l=>l.id===job?.lease_id);
        const machine=snapshot.machines.find(m=>m.id===job?.assigned_machine_id);
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
            throw Error('GPU access probe needs a fresh exclusive single-GPU lease on a qualified Linux agent.');
        if(job.gpu_qualification?.status==='requested')
            throw Error('GPU access probe is already requested for this job.');
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        selected.gpu_qualification={
            request_id:crypto.randomUUID(),lease_id:lease.id,
            machine_id:machine.id,status:'requested',
            requested_at:now(),
        };
        this.commit(next);
        return {job_id:job.id,lease_id:lease.id,
            request_id:selected.gpu_qualification.request_id,
            status:'requested',starts_training:false};
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
            // The operator may cancel an unstarted job or release the
            // advisory lease while a short probe is finishing. Consume
            // that exact old proof without restoring authority.
            if(job&&lease&&request?.status==='cancelled'&&
                request.request_id===report.dispatch_id&&
                request.lease_id===lease.id&&
                request.machine_id===machine.id&&
                lease.status==='released')
                continue;
            if(!job||!lease||!request||request.request_id!==report.dispatch_id||
                request.lease_id!==lease.id||request.machine_id!==machine.id||
                job.lease_id!==lease.id||job.assigned_machine_id!==machine.id||
                job.status!=='waiting_adapter'||lease.job_id!==job.id||
                lease.machine_id!==machine.id||lease.project_id!==job.project_id||
                lease.status!=='acknowledged')
                throw Error('GPU proof does not belong to this job and machine.');
            if(request.status!=='requested')continue;
            const pinned=lease.gpu_bindings;
            const verified=report.result==='verified'&&
                report.selected_pci_addresses.length===1&&
                pinned?.length===1&&
                report.selected_pci_addresses[0]===pinned[0].pci_address;
            if(report.result==='verified'&&!verified)
                throw Error('GPU proof does not match the frozen physical PCI allocation.');
            request.status=report.result;
            request.diagnostic=report.diagnostic||null;
            request.observed_at=now();
            request.agent_instance_id=instanceId;
            request.selected_pci_addresses=verified
                ?report.selected_pci_addresses.slice():[];
            changed=true;
        }
        return changed;
    }

    authorizeStart(value) {
        if(!object(value)||value.confirm_start!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly confirm which queued job to start.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        const readiness=nativeStartReadiness(snapshot,job);
        if(!readiness.eligible)
            throw Error(readiness.reason);
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        selected.status='start_requested';
        selected.dispatch_id=crypto.randomUUID();
        selected.start_requested_at=now();
        selected.status_reason='Start authorized; waiting for machine-local OS resource enforcement verification.';
        this.commit(next);
        return {id:selected.id,status:selected.status,dispatch_id:selected.dispatch_id};
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
        if(!lease)throw Error('Job/lease ownership cannot be verified.');
        job.status='stop_requested';
        job.stop_requested_at=now();
        job.status_reason='Stop requested; awaiting local service confirmation.';
        this.commit(next);
        return {id:job.id,status:job.status};
    }
    // The report is authenticated by the machine-token heartbeat. A second
    // machine, retired dispatch ID or unrelated lease cannot update the job.
    recordExecution(next,machine,items,installedProjects=[]) {
        if(!Array.isArray(items)||items.length>50)
            throw Error('Invalid execution reports.');
        const seen=new Set();
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
                ['memory_current_mb','cpu_time_ns','process_tasks'].some(k=>
                    report[k]!=null&&
                    (!Number.isSafeInteger(report[k])||report[k]<0)))
                throw Error('Invalid native execution report.');
            seen.add(report.job_id);
            const job=next.jobs.find(j=>j.id===report.job_id);
            const lease=next.reservations.find(l=>l.id===report.lease_id);
            if(!job||!lease||!installedProjects.includes(job.project_id)||
                job.dispatch_id!==report.dispatch_id||
                job.lease_id!==lease.id||lease.job_id!==job.id||
                lease.machine_id!==machine.id||job.assigned_machine_id!==machine.id||
                lease.status!=='acknowledged'||lease.project_id!==job.project_id)
                throw Error('Native execution report does not match its job and machine.');
            // Metrics cannot grant authority or change status: they are
            // validated only after machine/job/dispatch ownership checks.
            const progress=report.progress==null?null:
                validateProgress(report.progress,job.project_id);
            if(['finished','failed','stopped'].includes(job.status))continue;
            if(!['start_requested','launch_requested','running',
                    'stop_requested','unverified'].includes(job.status))
                throw Error('Job is not awaiting a native execution report.');
            const state=report.state==='running'&&job.status==='stop_requested'
                ?'stop_requested':report.state;
            if(state==='launch_requested'&&job.status!=='start_requested')continue;
            if(job.status==='stop_requested'&&report.state==='launch_requested')continue;
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
            // A single terminal progress projection survives a coordinator
            // restart. No per-heartbeat/history archive is persisted.
            if(['finished','failed','stopped'].includes(job.status)&&progress&&
                !job.final_progress) {
                job.final_progress=progress;
                changed=true;
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
        // Stops have priority over starts, which have priority over
        // inspections. Round-robin within each priority tier so long-running
        // services cannot starve other services of fresh observations.
        const priority=j=>j.status==='stop_requested'?0:
            j.status==='start_requested'?1:
            j.gpu_qualification?.status==='requested'?2:3;
        jobs.sort((a,b)=>priority(a)-priority(b));
        const last=this.executionOrderCursor.get(machineId);
        const ordered=[];
        for(const level of [0,1,2,3]) {
            const group=jobs.filter(j=>priority(j)===level);
            if(level>=2&&group.length>1) {
                const position=group.findIndex(j=>j.id===last);
                if(position>=0)
                    group.push(...group.splice(0,position+1));
            }
            ordered.push(...group);
        }
        for(const job of ordered) {
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id&&l.status==='acknowledged'&&l.receipt_fresh);
            if(!lease||!machine.installed_projects.includes(job.project_id))continue;
            const action=job.status==='stop_requested'?'stop':
                job.status==='start_requested'?'start':
                job.gpu_qualification?.status==='requested'?'qualify_gpu':'inspect';
            if(action==='start'&&machine.native_preflight_passed!==true)
                continue;
            if(action==='qualify_gpu'&&(
                machine.agent_platform!=='linux'||
                machine.native_preflight_passed!==true||
                job.resources.gpu_mode!=='exclusive'||
                job.resources.gpu_count!==1||
                job.gpu_qualification.lease_id!==lease.id||
                physicalGpuBindings(machine,{
                    gpu_inventory_source:machine.gpu_inventory_source,
                    gpu_devices:machine.observed_gpus,
                },lease.resources,lease.gpu_bindings)?.length!==1))
                continue;
            this.executionOrderCursor.set(machineId,job.id);
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
                },
                lease:{
                    id:lease.id,job_id:job.id,project_id:lease.project_id,
                    machine_id:lease.machine_id,issued_by:lease.issued_by,
                    status:lease.status,receipt_fresh:true,
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
        let changed=false;
        for (const id of ids) {
            const lease=next.reservations.find(x=>x.id===id &&
                x.machine_id===machine.id && x.issued_by==='experiment-hub');
            if (!lease || !['offered','acknowledged'].includes(lease.status)) continue;
            if(physicalGpuBindings(machine,report,lease.resources,lease.gpu_bindings)===null)
                continue;
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
        if(this.recordExecution(next,machine,reports,report.installed_projects))
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
        this.agentReports.set(machine.id,{
            ...report,instance_id:instance||null,received_ms:received,
        });
        // A GPU disappearing or moving PCI slots invalidates its lease
        // receipt immediately, not only after the general heartbeat TTL.
        for(const lease of this.state.reservations) {
            if(lease.machine_id===machine.id&&
                lease.status==='acknowledged'&&
                lease.resources?.gpu_count>0&&
                physicalGpuBindings(machine,report,lease.resources,lease.gpu_bindings)===null)
                this.leaseReceipts.delete(lease.id);
        }
        for (const id of ids) {
            const lease=this.state.reservations.find(x=>x.id===id &&
                x.machine_id===machine.id && x.status==='acknowledged' &&
                x.issued_by==='experiment-hub');
            if (lease&&physicalGpuBindings(machine,report,lease.resources,lease.gpu_bindings)!==null)
                this.leaseReceipts.set(id,received);
        }
        for(const item of reports) {
            this.executionTelemetry.set(item.job_id,{
                at:new Date(received).toISOString(),state:item.state,
                memory_current_mb:item.memory_current_mb??null,
                cpu_time_ns:item.cpu_time_ns??null,
                process_tasks:item.process_tasks??null,
                progress:item.progress==null?null:
                    validateProgress(item.progress,
                        this.state.jobs.find(j=>j.id===item.job_id).project_id),
                progress_issue:item.progress_issue||null,
            });
            if(item.progress) {
                const job=this.state.jobs.find(j=>j.id===item.job_id);
                this.progressHistory.record(job.id,job.project_id,
                    validateProgress(item.progress,job.project_id),received);
            }
        }
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
        this.commit(next);
        return copy(item);
    }
    queueConfiguration(value) {
        if (!object(value) || typeof value.configuration_id !== 'string')
            throw Error('Select a saved configuration to queue.');
        const selected = this.state.configurations.find(x=>x.id===value.configuration_id);
        if (!selected) throw Error('Saved configuration not found.');
        const project = this.project(selected.project_id);
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
        let parent=null;
        if(ancestorId!==null) {
            if(typeof ancestorId!=='string'||!UUID.test(ancestorId))
                throw Error('Select a valid predecessor job for continuation.');
            parent=this.state.jobs.find(j=>j.id===ancestorId);
            if(!parent||parent.project_id!==project.id||
                !['finished','stopped'].includes(parent.status)||
                !parent.capacity_released_at||!parent.dispatch_id||
                !parent.assigned_machine_id||!UUID.test(
                    String(parent.artifact_lineage_root_id||parent.id)))
                throw Error('Continuation requires a completed, capacity-released predecessor from this project.');
            if(this.state.jobs.some(j=>
                j.continuation_of_job_id===parent.id&&j.status!=='cancelled'))
                throw Error('This predecessor already has a non-cancelled continuation.');
        }
        if(project.id==='ants'&&
            (frozen.mode==='resume')!==Boolean(parent))
            throw Error('Ants resume mode requires a linked, completed predecessor job.');
        if (this.state.jobs.filter(j=>j.status==='waiting_adapter').length >= 100)
            throw Error('Too many pending jobs; cancel unused jobs first.');
        if (this.state.jobs.length >= 1000)
            throw Error('Job history limit reached. Export or archive older jobs before queueing more.');
        const jobId=crypto.randomUUID();
        const job = {
            id:jobId,
            project_id:project.id, adapter:project.adapter,
            continuation_of_job_id:parent?.id||null,
            artifact_lineage_root_id:parent
                ?(parent.artifact_lineage_root_id||parent.id):jobId,
            continuation_machine_id:parent?.assigned_machine_id||null,
            configuration_id:selected.id,
            configuration_revision:selected.revision,
            configuration_name:selected.name,
            configuration_sha256:crypto.createHash('sha256')
                .update(JSON.stringify(frozen)).digest('hex'),
            frozen_parameters:frozen, resources,
            status:'waiting_adapter',
            status_reason:'Queued; a separate machine claim and explicit native start authorization are required.',
            created_at:now(), cancelled_at:null,
        };
        const next=copy(this.state);
        next.jobs.push(job);
        this.commit(next);
        return copy(job);
    }
    cancelJob(value) {
        if (!object(value) || typeof value.job_id!=='string' ||
            value.confirm_cancel!==true)
            throw Error('Select a queued job and explicitly confirm cancellation.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if (!job) throw Error('Job not found.');
        if (job.status==='cancelled') return copy(job);
        if (job.status!=='waiting_adapter')
            throw Error('Only unstarted jobs can be cancelled without an execution adapter.');
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
        for(const job of ordered) {
            if(job.lease_id)continue;
            const claimCount=reservations.filter(r=>
                ['active','offered','acknowledged'].includes(r.status)).length;
            const chosen=claimCount<100
                ?allocationChoice(machines,reservations,job):null;
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
                    :'No currently connected machine has remaining budget',
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
