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

class MixedLinuxSupervisor {
    constructor({root,machineId,adapters,gpuEnabled=false,dockerSudo=false,
        nativeFactory=options=>new LinuxSupervisor(options),
        dockerFactory=options=>new DockerSupervisor(options),
        hostOs=os,fsApi=fs}={}) {
        if(!adapters||typeof adapters!=='object'||Array.isArray(adapters)||
            !adapters.tinybrains?.docker||!adapters.courier||adapters.courier.docker||
            Object.keys(adapters).some(name=>
                !['tinybrains','courier','bees_native','bees_native_collect'].includes(name))||
            ['bees_native','bees_native_collect'].some(name=>adapters[name]?.docker))
            throw Error('Mixed Linux execution requires native Courier, Docker TinyBrains and optional native Bees adapters.');
        // Bees collectors and learners share the same systemd owner and its
        // durable job/lineage journal. Do not create a third process owner.
        // Mixed mode deliberately offers CPU-only native work: allowing a
        // native ROCm lease alongside Docker would require coordinated
        // device access/VRAM ownership, not merely combined CPU admission.
        const nativeAdapters={courier:adapters.courier};
        for(const project of ['bees_native','bees_native_collect']) {
            if(adapters[project])nativeAdapters[project]=adapters[project];
        }
        this.native=nativeFactory({root,machineId,adapters:nativeAdapters,
            gpuEnabled:false});
        this.docker=dockerFactory({root,machineId,adapters:{tinybrains:adapters.tinybrains},
            gpuEnabled,dockerSudo,allowNativePeer:true});
        this.root=fsApi.realpathSync(root);
        this.lock=path.join(this.root,'mixed-launch.lock');
        this.hostOs=hostOs;
        this.fs=fsApi;
        this.adapters=adapters;
        this.gpuEnabled=gpuEnabled===true;
        this.gpuExecution='rocm';
        this.gpuProjects=['tinybrains'];
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
    assertNoNativeGpuOwner() {
        if(this.native.snapshot().some(r=>unresolved(r)&&
            (r.gpu_pci_address||r.gpu_pci_addresses?.length||r.project_id==='tinybrains')))
            throw Error('An unresolved native GPU owner blocks mixed Docker GPU execution.');
    }
    async qualifyGpuDeviceAccess(bindings,proof) {
        if(!this.qualifiedAt||this.gpuProbeCleanupPending())
            throw Error('Mixed Linux GPU qualification requires both ready owners and no pending probe.');
        this.assertNoNativeGpuOwner();
        return this.docker.qualifyGpuDeviceAccess(bindings,proof);
    }
    async start(job,lease) {
        if(!this.qualifiedAt||this.gpuProbeCleanupPending())
            throw Error('Mixed Linux owners must qualify before a new job can start.');
        const project=job?.project_id;
        const owner=project==='tinybrains'?this.docker:
            Object.hasOwn(this.native.adapters,project||'')?this.native:null;
        if(!owner)throw Error('Unknown or uninstalled mixed Linux project.');
        if(owner===this.native&&(job.resources?.gpu_count??0)!==0)
            throw Error('Mixed Linux native jobs require CPU-only execution.');
        // Serialize admission and the durable owner launch across mixed agents.
        // A crash leaves this lock behind for deliberate, manual reconciliation.
        let fd;
        try{fd=this.fs.openSync(this.lock,'wx',0o600);}
        catch{throw Error('Mixed launch already in progress or requires ownership recovery.');}
        try {
            const records=this.snapshot();
            if(records.some(r=>r.job_id===job.id))
                throw Error('Job already has a native or Docker ownership record; reconcile instead.');
            if(owner===this.docker)this.assertNoNativeGpuOwner();
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
    async stop(jobId) {
        const owner=this.ownerForJob(jobId);
        if(!owner)throw Error('Mixed Linux job has no durable ownership record.');
        return owner.stop(jobId);
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
