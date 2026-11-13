'use strict';

// Opt-in Linux Docker workload owner. Docker daemon, rather than the
// systemd CLI client, owns the trainer's CPU/memory cgroup and device access.
// The hub controls only installed local adapters; it never accepts a container
// image, bind mount or command from a remote job specification.
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const crypto=require('node:crypto');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {validateLocalInput,gpuRuntimeProof}=
    require('./experiment_hub_linux_supervisor');
const {resolveRocmDevices,deviceIdentity}=
    require('./experiment_hub_linux_gpu_devices');
const {availableMemoryMb}=require('./experiment_hub_host');
const {ensureLocalCpuRamCapacity,startHeadroom}=
    require('./experiment_hub_resources');

const exec=promisify(execFile);
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SAFE=/^[A-Za-z0-9_./:@=+,\-]+$/;
const ACTIVE=new Set(['launch_requested','running','stop_requested','unverified']);
const STATES=new Set(['launch_requested','running','stop_requested','unverified','finished','stopped','failed']);

function containerName(id) {
    if(!UUID.test(String(id)))throw Error('Invalid Docker job ID.');
    return 'bees-hub-'+id.replace(/-/g,'');
}
function label(name){return 'org.bees.experiment-hub.'+name;}
function fixedDockerSpec(adapter) {
    const d=adapter?.docker;
    if(!d||typeof d.image!=='string'||!SAFE.test(d.image)||
        typeof d.workdir!=='string'||!d.workdir.startsWith('/')||!SAFE.test(d.workdir)||
        !Array.isArray(d.mounts)||d.mounts.length<2||d.mounts.length>8||
        d.mounts.some(m=>!m||typeof m.host!=='string'||!path.isAbsolute(m.host)||
            typeof m.target!=='string'||!m.target.startsWith('/')||
            !SAFE.test(m.host)||!SAFE.test(m.target)||
            typeof m.readonly!=='boolean')||
        !d.mounts.some(m=>m.readonly===false)||
        !d.env||typeof d.env!=='object'||Array.isArray(d.env)||
        Object.entries(d.env).some(([key,value])=>
            !/^[A-Z][A-Z0-9_]{0,63}$/.test(key)||typeof value!=='string'||
            value.length>1024||!SAFE.test(value))||
        typeof d.uid!=='number'||!Number.isSafeInteger(d.uid)||d.uid<0||
        typeof d.gid!=='number'||!Number.isSafeInteger(d.gid)||d.gid<0||
        (d.remote_host_network!==undefined&&typeof d.remote_host_network!=='boolean'))
        throw Error('Untrusted Docker image, mount or environment configuration.');
    for(const m of d.mounts) {
        if(fs.realpathSync(m.host)!==m.host||!fs.statSync(m.host).isDirectory())
            throw Error('Docker bind mount must be an existing, non-redirected directory.');
    }
    for(let i=0;i<d.mounts.length;i++)
        for(let j=i+1;j<d.mounts.length;j++) {
            const a=d.mounts[i],b=d.mounts[j];
            if(a.target===b.target||
                a.target.startsWith(b.target+'/')||
                b.target.startsWith(a.target+'/'))
                throw Error('Docker bind mounts may not overlap.');
        }
    return d;
}
function writeAtomic(file,value) {
    const tmp=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    try {
        fs.writeFileSync(tmp,JSON.stringify(value,null,2)+'\n',
            {flag:'wx',mode:0o600});
        // A successful rename alone does not prove the ownership journal
        // survives power loss. Sync the new file before publishing its name.
        const fd=fs.openSync(tmp,'r');
        try{fs.fsyncSync(fd);}
        finally{fs.closeSync(fd);}
        fs.renameSync(tmp,file);
        // Persist the directory entry before the local owner is authorized
        // to continue. An fsync failure remains an ambiguous, fail-closed
        // condition rather than proof that a trainer can be relaunched.
        const directory=fs.openSync(path.dirname(file),'r');
        try{fs.fsyncSync(directory);}
        finally{fs.closeSync(directory);}
    }finally {
        // A failed write or rename must not leave a private, stale copy
        // of native ownership behind or consume disk on every retry.
        if(fs.existsSync(tmp))fs.unlinkSync(tmp);
    }
}
function parseInspect(text) {
    const result=JSON.parse(text);
    if(!Array.isArray(result)||result.length!==1||!result[0]?.Id)
        throw Error('Docker returned an invalid container inspection.');
    return result[0];
}
// Read optional cgroup-v2 counters only after Docker's persisted ownership
// identity has been verified. A PID alone is never sufficient: the host
// cgroup must also contain the full, inspected Docker container ID.
function dockerCgroupCounters(doc,fsApi=fs,pathApi=path) {
    try {
        const id=String(doc?.Id||'').toLowerCase(),pid=doc?.State?.Pid;
        if(!/^[a-f0-9]{64}$/.test(id)||
            !Number.isSafeInteger(pid)||pid<1)return {};
        const membership=fsApi.readFileSync('/proc/'+pid+'/cgroup','utf8');
        if(typeof membership!=='string'||membership.length>8192)return {};
        const lines=membership.trim().split(/\r?\n/).filter(s=>s.startsWith('0::/'));
        if(lines.length!==1)return {};
        const segments=lines[0].slice(4).split('/').filter(Boolean);
        if(!segments.length||segments.length>32||
            segments.some(s=>s==='.'||s==='..'||!/^[-A-Za-z0-9_.:@]{1,128}$/.test(s)))return {};
        const ownerIndex=segments.findIndex(s=>s===id||s==='docker-'+id||
            s==='docker-'+id+'.scope');
        if(ownerIndex<0)return {};
        const root=fsApi.realpathSync('/sys/fs/cgroup');
        // The entrypoint may move into a delegated child cgroup; read
        // Docker's owning cgroup so sibling worker tasks are included.
        const dir=pathApi.join(root,...segments.slice(0,ownerIndex+1));
        if(fsApi.realpathSync(dir)!==dir)return {};
        const counter=name=>{
            try {
                const raw=fsApi.readFileSync(pathApi.join(dir,name),'utf8').trim();
                if(!/^[0-9]{1,16}$/.test(raw))return null;
                const value=Number(raw);
                return Number.isSafeInteger(value)?value:null;
            }catch{return null;}
        };
        let usage=null,throttled=null,throttleEvents=null,quotaPeriods=null;
        try {
            const raw=fsApi.readFileSync(pathApi.join(dir,'cpu.stat'),'utf8');
            if(typeof raw==='string'&&raw.length<=2048) {
                const field=name=>{
                    const match=new RegExp('(?:^|\\n)'+name+
                        ' ([0-9]{1,16})(?:\\n|$)').exec(raw);
                    const value=match?Number(match[1]):null;
                    return Number.isSafeInteger(value)?value:null;
                };
                const cpuMicroseconds=field('usage_usec');
                const throttledMicroseconds=field('throttled_usec');
                usage=cpuMicroseconds===null?null:cpuMicroseconds/1000000;
                throttled=throttledMicroseconds===null?null:
                    throttledMicroseconds/1000000;
                throttleEvents=field('nr_throttled');
                quotaPeriods=field('nr_periods');
            }
        }catch {
            // CPU accounting is unavailable; memory may still be readable.
        }
        const used=counter('memory.current'),peak=counter('memory.peak');
        const tasks=counter('pids.current');
        return {
            ...(usage===null?{}:{cpu_time_seconds:usage}),
            ...(throttled===null?{}:{cpu_throttled_seconds:throttled}),
            ...(throttleEvents===null?{}:{cpu_throttle_events:throttleEvents}),
            ...(quotaPeriods===null?{}:{cpu_quota_periods:quotaPeriods}),
            ...(used===null?{}:{memory_current_mb:Math.floor(used/1048576)}),
            ...(peak===null?{}:{peak_job_memory_bytes:peak}),
            ...(tasks===null?{}:{process_tasks:tasks}),
        };
    }catch {
        // Missing, unreadable, or renamed optional sensors are unknown.
        // Never let telemetry change Docker owner or training state.
        return {};
    }
}
class DockerSupervisor {
    constructor({root,machineId,adapters,gpuEnabled=false,dockerSudo=false}) {
        this.root=fs.realpathSync(root);
        this.machineId=machineId;
        this.adapters=adapters;
        this.gpuEnabled=gpuEnabled;
        this.gpuExecution='rocm';
        if(process.platform!=='linux'||!Object.keys(adapters).length||
            Object.keys(adapters).some(key=>key!=='tinybrains'))
            throw Error('The Docker backend currently supports only the Linux TinyBrains adapter.');
        for(const adapter of Object.values(adapters))fixedDockerSpec(adapter);
        this.command=dockerSudo?'/usr/bin/sudo':'/usr/bin/docker';
        this.prefix=dockerSudo?['-n','/usr/bin/docker']:[];
        this.file=path.join(this.root,'docker-owner.json');
        this.lock=path.join(this.root,'docker-owner.lock');
        this.probeFile=path.join(this.root,'docker-gpu-probe.json');
        const native=path.join(this.root,'supervisor-v1.json');
        if(fs.existsSync(path.join(this.root,'gpu-preflight-pending.json')))
            throw Error('Unresolved native GPU preflight blocks Docker backend adoption.');
        if(fs.existsSync(native)) {
            const existing=JSON.parse(fs.readFileSync(native,'utf8'));
            if(!Array.isArray(existing.records)||
                existing.records.some(r=>ACTIVE.has(r.state)))
                throw Error('An unresolved native supervisor owner blocks Docker adoption.');
        }
        this.journal=fs.existsSync(this.file)
            ?JSON.parse(fs.readFileSync(this.file,'utf8'))
            :{version:1,machine_id:this.machineId,revision:0,records:[]};
        if(this.journal.version!==1||this.journal.machine_id!==this.machineId||
            !Number.isSafeInteger(this.journal.revision)||
            !Array.isArray(this.journal.records)||
            this.journal.records.some(r=>!UUID.test(r.job_id)||!UUID.test(r.lease_id)||
                (r.artifact_lineage_root_id!==undefined&&
                    !UUID.test(r.artifact_lineage_root_id))||
                (r.continuation_of_job_id!==undefined&&
                    r.continuation_of_job_id!==null&&
                    !UUID.test(r.continuation_of_job_id))||
                !STATES.has(r.state)||r.container_name!==containerName(r.job_id)))
            throw Error('Invalid durable Docker supervisor journal.');
        this.state=this.journal.records;
        this.qualifiedAt=null;
    }
    async call(args,timeout=20000) {
        const response=await exec(this.command,[...this.prefix,...args],
            {timeout,maxBuffer:1024*1024,windowsHide:true});
        return response.stdout.trim();
    }
    commit(records) {
        let fd;
        try{fd=fs.openSync(this.lock,'wx',0o600);}
        catch{throw Error('Docker supervisor journal is locked; inspect ownership before recovery.');}
        try{
            const current=fs.existsSync(this.file)
                ?JSON.parse(fs.readFileSync(this.file,'utf8'))
                :{version:1,machine_id:this.machineId,revision:0,records:[]};
            if(current.version!==1||current.machine_id!==this.machineId||
                current.revision!==this.journal.revision)
                throw Error('Docker journal changed concurrently; reopen supervisor.');
            const committed={version:1,machine_id:this.machineId,
                revision:current.revision+1,records};
            // Publish ownership in memory only after the journal is written.
            // A failed write must leave the same revision retryable.
            writeAtomic(this.file,committed);
            this.journal=committed;
            this.state=records;
        }finally{
            fs.closeSync(fd);
            fs.unlinkSync(this.lock);
        }
    }
    snapshot(){return JSON.parse(JSON.stringify(this.state));}
    gpuProbeCleanupPending(){return fs.existsSync(this.probeFile);}
    // The journal is written before Docker creates any training container.
    // Only the absence of BOTH durable ownership and the deterministic
    // container, with no journal/probe operation in progress, can establish
    // that an offered Start failed before a trainer was launched.
    async reconcileAbsent(jobId) {
        if(this.state.some(record=>record.job_id===jobId)||
            fs.existsSync(this.lock)||this.gpuProbeCleanupPending())
            return {state:'unverified',diagnostic:
                'Docker ownership or GPU proof may still be in progress; inspect before recovery.'};
        const container=await this.inspect(containerName(jobId));
        if(container)
            return {state:'unverified',diagnostic:
                'Docker container exists without a matching ownership record; manual inspection required.'};
        return {state:'failed',prelaunch_absence_verified:true,diagnostic:
            'Pre-launch failure confirmed: no Docker owner record, container, or pending GPU proof.'};
    }
    async qualify(){
        if(this.gpuProbeCleanupPending())
            throw Error('An earlier Docker GPU proof requires operator cleanup.');
        const data=JSON.parse(await this.call(['info','--format','{{json .}}'],15000));
        if(String(data.CgroupVersion)!=='2'||!['systemd','cgroupfs'].includes(data.CgroupDriver))
            throw Error('Docker must enforce Linux cgroup v2 CPU and RAM limits.');
        for(const adapter of Object.values(this.adapters))
            await this.imageId(fixedDockerSpec(adapter).image);
        this.qualifiedAt=new Date().toISOString();
        return {qualified_at:this.qualifiedAt};
    }
    async imageId(tag) {
        const value=await this.call(['image','inspect','--format','{{.Id}}',tag]);
        if(!/^sha256:[0-9a-f]{64}$/.test(value))
            throw Error('Cannot pin the approved Docker image by content digest.');
        return value;
    }
    async inspect(name) {
        try {
            return parseInspect(await this.call(['container','inspect',name],12000));
        }catch(error) {
            // Only an explicit 'No such container' is evidence of absence.
            if(/No such (?:object|container):/.test(String(error.stderr||'')))return null;
            throw error;
        }
    }
    dockerArgs(d,imageId,invocation,record,plan) {
        const args=['create','--name='+record.container_name,
            '--label='+label('machine')+'='+this.machineId,
            '--label='+label('job')+'='+record.job_id,
            '--label='+label('lease')+'='+record.lease_id,
            '--cpus='+invocation.cpus,
            '--memory='+invocation.ram_mb+'m',
            '--memory-swap='+invocation.ram_mb+'m',
            '--pids-limit=512','--network='+(record.network_mode||'none'),'--restart=no',
            '--read-only','--cap-drop=ALL','--security-opt=no-new-privileges',
            // Full ROCm training may need substantial LLVM compilation scratch.
            // tmpfs remains capped and charged to the job's memory cgroup.
            '--tmpfs=/tmp:rw,size=2g,mode=1777',
            '--shm-size=1g','--user='+d.uid+':'+d.gid,
            '--workdir='+d.workdir];
        for(const m of d.mounts)
            args.push('--mount=type=bind,source='+m.host+',target='+m.target+
                (m.readonly?',readonly':''));
        for(const [key,value] of Object.entries(d.env))
            args.push('--env='+key+'='+value);
        if(plan){
            args.push('--device=/dev/kfd:/dev/kfd:rwm');
            for(const device of plan.selected)
                args.push('--device='+device.node+':'+device.node+':rwm');
            for(const id of new Set(['/dev/kfd',...plan.selected.map(d=>d.node)]
                .map(node=>fs.statSync(node).gid)))
                args.push('--group-add='+id);
        }else{
            // No accelerator devices for CPU-only jobs.
            for(const name of ['CUDA_VISIBLE_DEVICES','HIP_VISIBLE_DEVICES',
                'ROCR_VISIBLE_DEVICES','GPU_DEVICE_ORDINAL'])
                args.push('--env='+name+'=');
        }
        return args.concat(imageId,invocation.executable,...invocation.args);
    }
    verifyContainer(doc,record) {
        if(!doc||doc.Name!=='/'+record.container_name||
            doc.Config?.Labels?.[label('machine')]!==this.machineId||
            doc.Config?.Labels?.[label('job')]!==record.job_id||
            doc.Config?.Labels?.[label('lease')]!==record.lease_id||
            doc.Image!==record.image_id||
            doc.HostConfig?.NanoCpus!==record.cpus*1000000000||
            doc.HostConfig?.Memory!==record.ram_mb*1048576||
            doc.HostConfig?.MemorySwap!==record.ram_mb*1048576||
            doc.HostConfig?.NetworkMode!==(record.network_mode||'none')||
            doc.HostConfig?.ReadonlyRootfs!==true||
            doc.HostConfig?.Privileged!==false||
            doc.HostConfig?.AutoRemove!==false||
            doc.HostConfig?.RestartPolicy?.Name!=='no'||
            doc.Config?.User!==record.user||
            !Array.isArray(doc.HostConfig?.Devices))
            throw Error('Docker container identity or kernel resource controls differ from lease.');
        const gpuNodes=record.gpu_nodes||[record.gpu_node].filter(Boolean);
        const expected=gpuNodes.length
            ?new Set(['/dev/kfd',...gpuNodes]):new Set();
        const devices=doc.HostConfig.Devices.map(d=>d.PathOnHost);
        if(devices.length!==expected.size||
            devices.some(d=>!expected.has(d)))
            throw Error('Docker accelerator device allowance changed.');
        if(doc.State?.Running===true&&gpuNodes.length) {
            const pcis=record.gpu_pci_addresses||
                [record.gpu_pci_address].filter(Boolean);
            if(pcis.length!==gpuNodes.length||!record.gpu_identity)
                throw Error('Running Docker GPU owner lacks complete frozen PCI identity.');
            const current=resolveRocmDevices(pcis.map((pci,index)=>({
                id:'gpu'+index,pci_address:pci,vram_mb:1,
            })));
            if(deviceIdentity(current)!==record.gpu_identity||
                current.selected.some((device,index)=>device.node!==gpuNodes[index]))
                throw Error('Running Docker GPU render node no longer matches its frozen PCI lease.');
        }
        if(record.container_id&&doc.Id!==record.container_id)
            throw Error('Deterministic Docker container name was rebound to a different object.');
        // Docker can report ReadOnly:null for a writable --mount. Compare
        // the requested bind identity and the effective mount permissions;
        // neither omitted defaults nor presentation order confer trust.
        const binds=doc.HostConfig.Mounts;
        const effective=doc.Mounts;
        if(!Array.isArray(binds)||!Array.isArray(effective)||
            binds.length!==record.mounts.length||
            effective.length!==record.mounts.length||
            record.mounts.some(([source,target,readonly])=>{
                const configured=binds.find(m=>m.Type==='bind'&&
                    m.Source===source&&m.Target===target);
                const mounted=effective.find(m=>m.Type==='bind'&&
                    m.Source===source&&m.Destination===target);
                return typeof readonly!=='boolean'||!configured||!mounted||
                    (readonly?configured.ReadOnly!==true:
                        configured.ReadOnly!==false&&configured.ReadOnly!=null)||
                    mounted.RW!==!readonly;
            }))
            throw Error('Docker bind-mount permissions or targets changed.');
        const env=doc.Config.Env||[];
        if(Object.entries(record.env).some(([k,v])=>!env.includes(k+'='+v)))
            throw Error('Docker training environment differs from approved image configuration.');
        if(JSON.stringify(doc.Config.Cmd)!==JSON.stringify(record.command))
            throw Error('Docker training command differs from the frozen launch.');
    }
    async qualifyGpuDeviceAccess(bindings,proof) {
        if(!this.qualifiedAt||!this.gpuEnabled||!proof||
            !Array.isArray(bindings)||bindings.length<1||bindings.length>8||
            (bindings.length===2&&
                this.adapters.tinybrains?.gpu_dual_execution!=='split-ppo')||
            (bindings.length>2&&
                this.adapters.tinybrains?.gpu_multi_execution!=='ppo-local-collectors')||
            this.gpuProbeCleanupPending())
            throw Error('Approved Docker one- or two-GPU proof is unavailable.');
        const plan=resolveRocmDevices(bindings);
        if(bindings.length===1&&!plan.unselected.length)
            throw Error('Single-GPU Docker proof requires an excluded peer GPU.');
        const d=fixedDockerSpec(this.adapters.tinybrains);
        if(proof.executable!==this.adapters.tinybrains.executable)
            throw Error('GPU proof Python differs from the approved Docker adapter.');
        const image=await this.imageId(d.image);
        const probeName='bees-hub-proof-'+crypto.randomBytes(8).toString('hex');
        const flags=['run','--rm','--name='+probeName,'--network=none',
            '--read-only','--cap-drop=ALL','--security-opt=no-new-privileges',
            '--tmpfs=/tmp:rw,size=64m,mode=1777',
            '--cpus=1','--memory=4096m','--memory-swap=4096m',
            '--user='+d.uid+':'+d.gid,
            '--device=/dev/kfd:/dev/kfd:rwm'];
        for(const device of plan.selected)
            flags.push('--device='+device.node+':'+device.node+':rwm');
        for(const id of new Set(['/dev/kfd',...plan.selected.map(d=>d.node)]
            .map(node=>fs.statSync(node).gid)))
            flags.push('--group-add='+id);
        for(const m of d.mounts)
            flags.push('--mount=type=bind,source='+m.host+',target='+m.target+
                (m.readonly?',readonly':''));
        for(const [key,value] of Object.entries(d.env))
            flags.push('--env='+key+'='+value);
        flags.push('--workdir='+d.workdir,image,
            proof.executable,...proof.verifyArgs);
        writeAtomic(this.probeFile,{name:probeName,
            devices:plan.selected.map(d=>d.pci_address)});
        let answer,error=null;
        try {
            const output=await this.call(flags,90000);
            answer=JSON.parse(output.split('\n').at(-1));
            if(JSON.stringify(answer?.pci_addresses)!==
                JSON.stringify(bindings.map(b=>b.pci_address)))
                throw Error('Container HIP PCI identities do not match the Hub GPU lease.');
        }catch(exc){error=exc;}
        // A timed-out Docker CLI can leave a daemon-owned container alive.
        // Never silently erase the pending proof in that case.
        let remains=true;
        try{remains=(await this.inspect(probeName))!==null;}catch{}
        if(!remains)fs.unlinkSync(this.probeFile);
        if(error||remains)
            throw Error('Docker GPU proof failed or requires cleanup: '+
                String(error?.message||probeName).slice(0,130));
        return {selected:bindings.map(b=>b.pci_address),runtime_pci_verified:true};
    }
    async start(job,lease) {
        if(!this.qualifiedAt||this.gpuProbeCleanupPending())
            throw Error('Docker owner requires successful preflight and no unresolved GPU proof.');
        if(this.state.some(x=>x.job_id===job?.id))
            throw Error('Docker job already has an ownership record; reconcile instead.');
        const invocation=validateLocalInput(job,lease,this.machineId,
            this.adapters,os,this.gpuEnabled);
        const hostCpus=typeof os.availableParallelism==='function'
            ?os.availableParallelism():os.cpus().length;
        ensureLocalCpuRamCapacity(this.state,invocation.cpus,invocation.ram_mb,
            hostCpus,Math.floor(os.totalmem()/1048576));
        const freeRam=availableMemoryMb(os);
        const memoryBlock=startHeadroom(job.resources,freeRam);
        if(memoryBlock)throw Error(memoryBlock);
        const d=fixedDockerSpec(this.adapters[job.project_id]);
        const remoteNetwork=job.frozen_parameters?.remote_rollouts===true;
        if(remoteNetwork&&(job.project_id!=='tinybrains'||
            d.remote_host_network!==true))
            throw Error('Private tailnet access requires a trusted, opt-in TinyBrains Docker adapter.');
        if(invocation.gpu) {
            // The durable machine-local owner refuses overlaps even if the
            // coordinator's admission ledger has become stale.
            const active=this.state.filter(x=>ACTIVE.has(x.state));
            const pciIds=x=>x.gpu_pci_addresses||[x.gpu_pci_address].filter(Boolean);
            if(active.some(x=>(x.gpu_node||x.gpu_nodes?.length)&&!pciIds(x).length))
                throw Error('Unresolved physical GPU ownership lacks a trustworthy PCI identity.');
            for(const binding of invocation.gpu.bindings) {
                const contenders=active.filter(x=>pciIds(x).includes(binding.pci_address));
                if(!contenders.length)continue;
                const compatible=job.resources.gpu_mode==='shared'&&
                    this.adapters[job.project_id].gpu_sharing==='cooperative-pytorch'&&
                    contenders.every(x=>x.gpu_mode==='shared'&&
                        Number.isSafeInteger(x.gpu_vram_mb)&&x.gpu_vram_mb>0&&
                        Number.isSafeInteger(x.gpu_compute_pct)&&x.gpu_compute_pct>0);
                if(!compatible)
                    throw Error('A leased GPU has exclusive or unresolved Docker ownership.');
                const memory=contenders.reduce((n,x)=>n+x.gpu_vram_mb,
                    job.resources.gpu_vram_mb);
                const compute=contenders.reduce((n,x)=>n+x.gpu_compute_pct,
                    job.resources.gpu_compute_pct);
                if(memory>binding.vram_mb||compute>100)
                    throw Error('Local cooperative GPU commitments exceed PCI-device capacity.');
            }
        }
        let plan=null;
        if(invocation.gpu) {
            const bindings=invocation.gpu.bindings;
            const proof=gpuRuntimeProof(this.adapters[job.project_id],
                job.frozen_parameters,{job_id:job.id,
                    project_id:job.project_id,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    ...(bindings.length>=2
                        ?{gpu_pci_addresses:bindings.map(b=>b.pci_address)}
                        :{gpu_pci_address:bindings[0].pci_address})});
            await this.qualifyGpuDeviceAccess(bindings,proof);
            plan=resolveRocmDevices(bindings);
        }
        const imageId=await this.imageId(d.image);
        const name=containerName(job.id);
        if(await this.inspect(name))
            throw Error('Deterministic Docker job name already exists outside the ownership journal.');
        const record={
            job_id:job.id,lease_id:lease.id,project_id:job.project_id,
            artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
            continuation_of_job_id:job.continuation_of_job_id||null,
            container_name:name,container_id:null,
            image_id:imageId,cpus:invocation.cpus,ram_mb:invocation.ram_mb,
            network_mode:remoteNetwork?'host':'none',
            mounts:d.mounts.map(m=>[m.host,m.target,m.readonly]),
            user:d.uid+':'+d.gid,env:d.env,
            command:[invocation.executable,...invocation.args],
            ...(plan?{
                ...(plan.selected.length===1
                    ?{gpu_node:plan.selected[0].node,
                        gpu_pci_address:plan.selected[0].pci_address}
                    :{gpu_nodes:plan.selected.map(d=>d.node),
                        gpu_pci_addresses:plan.selected.map(d=>d.pci_address)}),
                gpu_identity:deviceIdentity(plan),
                gpu_mode:job.resources.gpu_mode,
                gpu_vram_mb:job.resources.gpu_mode==='shared'?job.resources.gpu_vram_mb:0,
                gpu_compute_pct:job.resources.gpu_mode==='shared'?job.resources.gpu_compute_pct:100}:{}),
            state:'launch_requested',
            stop_requested_at:null,checkpoint_stop_file:invocation.stopFile,
            requested_at:new Date().toISOString(),
        };
        this.commit([...this.state,record]);
        const args=this.dockerArgs(d,imageId,invocation,record,plan);
        try {
            await this.call(args,40000);
            const created=await this.inspect(name);
            this.verifyContainer(created,record);
            this.commit(this.state.map(r=>r.job_id===job.id
                ?{...r,container_id:created.Id}:r));
            await this.call(['start',name],30000);
        }catch(error) {
            throw Error('Docker launch ambiguous; ownership retained for reconciliation: '+
                String(error.message||error).slice(0,140));
        }
        return this.reconcile(job.id);
    }
    async reconcile(jobId) {
        const record=this.state.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown Docker job.');
        let doc;
        try{
            doc=await this.inspect(record.container_name);
            if(doc)this.verifyContainer(doc,record);
        }catch(error){
            return {state:'unverified',diagnostic:String(error.message||error).slice(0,220)};
        }
        if(!doc)
            return {state:'unverified',diagnostic:'Owned Docker container absent; do not relaunch or release claim without inspection.'};
        let state='unverified',verified=false;
        if(doc.State?.Running===true){
            state=record.stop_requested_at?'stop_requested':'running';
            if(record.stop_requested_at&&record.checkpoint_stop_file){
                try {
                    fs.writeFileSync(record.checkpoint_stop_file,'',{flag:'wx',mode:0o600});
                }catch(error){
                    if(error.code!=='EEXIST')
                        return {state:'stop_requested',diagnostic:
                            'STOP marker delivery failed: '+String(error.message||error).slice(0,150)};
                }
            }
        }else if(doc.State?.Status==='exited'){
            if(doc.State.ExitCode===0) {
                state=record.stop_requested_at?'stopped':'finished';
                verified=state==='stopped'&&Boolean(record.checkpoint_stop_file)&&
                    fs.existsSync(record.checkpoint_stop_file);
            }else state='failed';
        }
        const updated={...record,container_id:doc.Id,state,
            ...(state==='stopped'&&verified?{checkpoint_stop_verified:true}:{})};
        if(record.state!==state||record.container_id!==doc.Id||
            record.checkpoint_stop_verified!==updated.checkpoint_stop_verified)
            this.commit(this.state.map(r=>r.job_id===jobId?updated:r));
        const measured=['running','stop_requested'].includes(state)
            ?dockerCgroupCounters(doc):{};
        return {state,checkpoint_stop_verified:verified,...measured,
            ...(state==='failed'?{diagnostic:'Docker trainer exit code '+doc.State.ExitCode}:{}),
        };
    }
    async stop(jobId) {
        const record=this.state.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown Docker job.');
        if(!record.checkpoint_stop_file)
            throw Error('Docker jobs require checkpoint-aware STOP semantics.');
        const current=await this.reconcile(jobId);
        if(current.state!=='running'&&current.state!=='stop_requested')
            return current;
        if(!record.stop_requested_at){
            const updated={...record,stop_requested_at:new Date().toISOString(),
                state:'stop_requested'};
            this.commit(this.state.map(r=>r.job_id===jobId?updated:r));
        }
        // Never docker stop/kill here: PPO must finish its current update.
        return this.reconcile(jobId);
    }
}
module.exports={DockerSupervisor};
