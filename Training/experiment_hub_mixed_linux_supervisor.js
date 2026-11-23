'use strict';

// One Halifax machine identity and admission budget, with independent durable
// native-systemd and Docker workload owners. A mixed agent is the only writer
// allowed to start work through this bridge; each owner retains its original
// job journal, checkpoint-stop semantics and reconciliation rules.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {LinuxSupervisor}=require('./experiment_hub_linux_supervisor');
const {DockerSupervisor}=require('./experiment_hub_docker_supervisor');
const {ensureLocalCpuRamCapacity}=require('./experiment_hub_resources');

const TERMINAL=new Set(['finished','stopped','failed']);
const unresolved=record=>!TERMINAL.has(record.state)||Boolean(record.quarantined_at);
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const BEES_ROLES=['bees_native','bees_rollout'];

class MixedLinuxSupervisor {
    constructor({root,machineId,adapters,gpuEnabled=false,dockerSudo=false,
        nativeFactory=options=>new LinuxSupervisor(options),
        dockerFactory=options=>new DockerSupervisor(options),
        hostOs=os,fsApi=fs}={}) {
        if(!adapters||typeof adapters!=='object'||Array.isArray(adapters)||
            !adapters.tinybrains?.docker||!adapters.courier||adapters.courier.docker||
            Object.keys(adapters).some(name=>
                !['tinybrains','courier',...BEES_ROLES].includes(name))||
            BEES_ROLES.some(role=>adapters[role]?.docker))
            throw Error('Mixed Linux execution needs Docker TinyBrains, native Courier and optional native Bees Unity roles.');
        const nativeAdapters={courier:adapters.courier};
        for(const role of BEES_ROLES)
            if(adapters[role])nativeAdapters[role]=adapters[role];
        this.native=nativeFactory({root,machineId,adapters:nativeAdapters,
            gpuEnabled:gpuEnabled&&BEES_ROLES.some(role=>Boolean(adapters[role]))});
        this.docker=dockerFactory({root,machineId,adapters:{tinybrains:adapters.tinybrains},
            gpuEnabled,dockerSudo,allowNativePeer:true});
        this.root=fsApi.realpathSync(root);
        this.lock=path.join(this.root,'mixed-launch.lock');
        this.hostOs=hostOs;
        this.fs=fsApi;
        this.adapters=adapters;
        this.gpuEnabled=gpuEnabled===true;
        this.gpuExecution='rocm';
        this.gpuProjects=['tinybrains',
            ...BEES_ROLES.filter(role=>Boolean(adapters[role]))];
        this.dockerSupervisor=this.docker;
        this.qualifiedAt=null;
        this.snapshot(); // Corrupt or colliding ownership is never silently adopted.
    }
    snapshot() {
        const native=this.native.snapshot(),docker=this.docker.snapshot();
        if(!Array.isArray(native)||!Array.isArray(docker)||
            new Set([...native,...docker].map(r=>r.job_id)).size!==native.length+docker.length||
            docker.some(r=>r.project_id!=='tinybrains'))
            throw Error('Mixed Linux owner journals overlap or contain an unexpected Docker project.');
        return [...native,...docker];
    }
    ownerForJob(jobId) {
        const n=this.native.snapshot().some(r=>r.job_id===jobId);
        const d=this.docker.snapshot().some(r=>r.job_id===jobId);
        if(n&&d)throw Error('Multiple machine-local owners claim one job; manual recovery required.');
        return n?this.native:d?this.docker:null;
    }
    async qualify() {
        this.qualifiedAt=null;
        if(this.fs.existsSync(this.lock))
            throw Error('Unresolved mixed launch lock; inspect machine ownership before retrying.');
        this.snapshot();
        await this.native.qualify();
        await this.docker.qualify();
        this.qualifiedAt=new Date().toISOString();
        return {qualified_at:this.qualifiedAt};
    }
    gpuProbeCleanupPending() {
        return this.native.gpuProbeCleanupPending()||this.docker.gpuProbeCleanupPending();
    }
    assertOtherBackendGpuAvailable(owner,bindings) {
        if(!Array.isArray(bindings)||!bindings.length||
            bindings.some(binding=>!PCI.test(String(binding?.pci_address||''))))
            throw Error('Mixed GPU operation requires pinned physical PCI devices.');
        const other=owner===this.native?this.docker:this.native;
        const requested=new Set(bindings.map(binding=>binding.pci_address));
        for(const record of other.snapshot().filter(unresolved)) {
            // An old native TinyBrains journal cannot be reassigned to the
            // Docker owner merely because its original PCI pin is missing.
            if(owner===this.docker&&record.project_id==='tinybrains')
                throw Error('Unresolved legacy native TinyBrains ownership blocks Docker GPU execution.');
            const pins=record.gpu_pci_addresses||
                (record.gpu_pci_address?[record.gpu_pci_address]:[]);
            if(!Array.isArray(pins)||
                (!pins.length&&(record.gpu_node||record.gpu_nodes?.length||
                    record.gpu_mode)))
                throw Error('Other mixed owner has unresolved GPU identity.');
            if(pins.some(pci=>requested.has(pci)))
                throw Error('A selected physical GPU remains owned by the other mixed backend.');
        }
    }
    async qualifyGpuDeviceAccess(bindings,proof) {
        if(!this.qualifiedAt||!this.gpuEnabled||this.gpuProbeCleanupPending())
            throw Error('Mixed Linux GPU qualification requires ready owners and no pending probe.');
        const role=proof?.project_id??'tinybrains';
        if(!['tinybrains',...BEES_ROLES].includes(role)||
            !this.adapters[role]||
            proof?.executable!==this.adapters[role].executable)
            throw Error('Mixed GPU proof must belong to an installed local adapter.');
        const owner=role==='tinybrains'?this.docker:this.native;
        this.assertOtherBackendGpuAvailable(owner,bindings);
        return owner.qualifyGpuDeviceAccess(bindings,proof);
    }
    async start(job,lease) {
        if(!this.qualifiedAt||this.gpuProbeCleanupPending())
            throw Error('Mixed Linux owners must qualify before a new job can start.');
        const owner=job?.project_id==='tinybrains'?this.docker:
            job?.project_id==='courier'||BEES_ROLES.includes(job?.project_id)
                ?this.native:null;
        if(!owner||!this.adapters[job.project_id])
            throw Error('Unknown mixed Linux project.');
        if(job.project_id==='courier'&&(job.resources?.gpu_count??0)!==0)
            throw Error('Courier must use native CPU-only execution.');
        // Serialize admission and the durable owner launch across mixed agents.
        // A crash leaves this lock behind for deliberate, manual reconciliation.
        let fd;
        try{fd=this.fs.openSync(this.lock,'wx',0o600);}
        catch{throw Error('Mixed launch already in progress or requires ownership recovery.');}
        try {
            const records=this.snapshot();
            if(records.some(r=>r.job_id===job.id))
                throw Error('Job already has a native or Docker ownership record; reconcile instead.');
            if((job.resources?.gpu_count??0)>0) {
                if(!this.gpuEnabled)
                    throw Error('The mixed owner has no approved ROCm GPU capability.');
                this.assertOtherBackendGpuAvailable(owner,lease?.gpu_bindings);
            }
            ensureLocalCpuRamCapacity(records,job.resources.cpus,job.resources.ram_mb,
                typeof this.hostOs.availableParallelism==='function'
                    ?this.hostOs.availableParallelism():this.hostOs.cpus().length,
                Math.floor(this.hostOs.totalmem()/1048576));
            return await owner.start(job,lease);
        }finally {
            this.fs.closeSync(fd);
            this.fs.unlinkSync(this.lock);
        }
    }
    async reconcile(jobId) {
        const owner=this.ownerForJob(jobId);
        if(!owner)throw Error('Mixed Linux job has no durable ownership record.');
        return owner.reconcile(jobId);
    }
    async stop(jobId,options) {
        const owner=this.ownerForJob(jobId);
        if(!owner)throw Error('Mixed Linux job has no durable ownership record.');
        return owner.stop(jobId,options);
    }
    async reconcileAbsent(jobId,projectId) {
        if(this.fs.existsSync(this.lock))
            return {state:'unverified',diagnostic:'Mixed launch lock may still own an unrecorded job.'};
        if(this.ownerForJob(jobId))
            return {state:'unverified',diagnostic:'Mixed ownership exists; reconcile the recorded owner.'};
        return projectId==='tinybrains'
            ?this.docker.reconcileAbsent(jobId)
            :{state:'unverified',diagnostic:'Native absence cannot prove that no systemd service was launched.'};
    }
}

module.exports={MixedLinuxSupervisor};
