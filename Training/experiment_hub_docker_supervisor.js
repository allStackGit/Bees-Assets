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
        typeof d.gid!=='number'||!Number.isSafeInteger(d.gid)||d.gid<0)
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
    fs.writeFileSync(tmp,JSON.stringify(value,null,2)+'\n',{flag:'wx',mode:0o600});
    fs.renameSync(tmp,file);
}
function parseInspect(text) {
    const result=JSON.parse(text);
    if(!Array.isArray(result)||result.length!==1||!result[0]?.Id)
        throw Error('Docker returned an invalid container inspection.');
    return result[0];
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
            this.journal={version:1,machine_id:this.machineId,
                revision:current.revision+1,records};
            writeAtomic(this.file,this.journal);
            this.state=records;
        }finally{
            fs.closeSync(fd);
            fs.unlinkSync(this.lock);
        }
    }
    snapshot(){return JSON.parse(JSON.stringify(this.state));}
    gpuProbeCleanupPending(){return fs.existsSync(this.probeFile);}
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
            '--pids-limit=512','--network=none','--restart=no',
            '--read-only','--cap-drop=ALL','--security-opt=no-new-privileges',
            '--tmpfs=/tmp:rw,size=64m,mode=1777',
            '--shm-size=1g','--user='+d.uid+':'+d.gid,
            '--workdir='+d.workdir];
        for(const m of d.mounts)
            args.push('--mount=type=bind,source='+m.host+',target='+m.target+
                (m.readonly?',readonly':''));
        for(const [key,value] of Object.entries(d.env))
            args.push('--env='+key+'='+value);
        if(plan){
            args.push('--device=/dev/kfd:/dev/kfd:rwm',
                '--device='+plan.selected[0].node+':'+plan.selected[0].node+':rwm');
            for(const id of new Set([
                fs.statSync('/dev/kfd').gid,
                fs.statSync(plan.selected[0].node).gid,
            ]))args.push('--group-add='+id);
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
            doc.HostConfig?.NetworkMode!=='none'||
            doc.HostConfig?.ReadonlyRootfs!==true||
            doc.HostConfig?.Privileged!==false||
            doc.HostConfig?.AutoRemove!==false||
            doc.HostConfig?.RestartPolicy?.Name!=='no'||
            doc.Config?.User!==record.user||
            !Array.isArray(doc.HostConfig?.Devices))
            throw Error('Docker container identity or kernel resource controls differ from lease.');
        const expected=record.gpu_node
            ?new Set(['/dev/kfd',record.gpu_node]):new Set();
        const devices=doc.HostConfig.Devices.map(d=>d.PathOnHost);
        if(devices.length!==expected.size||
            devices.some(d=>!expected.has(d)))
            throw Error('Docker accelerator device allowance changed.');
        if(record.container_id&&doc.Id!==record.container_id)
            throw Error('Deterministic Docker container name was rebound to a different object.');
        const binds=(doc.HostConfig.Mounts||[]).map(m=>[m.Source,m.Target,m.ReadOnly]);
        if(JSON.stringify(binds)!==JSON.stringify(record.mounts))
            throw Error('Docker bind-mount permissions or targets changed.');
        const env=doc.Config.Env||[];
        if(Object.entries(record.env).some(([k,v])=>!env.includes(k+'='+v)))
            throw Error('Docker training environment differs from approved image configuration.');
        if(JSON.stringify(doc.Config.Cmd)!==JSON.stringify(record.command))
            throw Error('Docker training command differs from the frozen launch.');
    }
    async qualifyGpuDeviceAccess(bindings,proof) {
        if(!this.qualifiedAt||!this.gpuEnabled||!proof||
            !Array.isArray(bindings)||bindings.length!==1||
            this.gpuProbeCleanupPending())
            throw Error('Exclusive Docker GPU proof is unavailable.');
        const plan=resolveRocmDevices(bindings);
        if(!plan.unselected.length)
            throw Error('Docker GPU qualification requires an excluded peer GPU.');
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
            '--device=/dev/kfd:/dev/kfd:rwm',
            '--device='+plan.selected[0].node+':'+plan.selected[0].node+':rwm'];
        for(const id of new Set([fs.statSync('/dev/kfd').gid,
            fs.statSync(plan.selected[0].node).gid]))
            flags.push('--group-add='+id);
        for(const m of d.mounts)
            flags.push('--mount=type=bind,source='+m.host+',target='+m.target+
                (m.readonly?',readonly':''));
        for(const [key,value] of Object.entries(d.env))
            flags.push('--env='+key+'='+value);
        flags.push('--workdir='+d.workdir,image,
            proof.executable,...proof.verifyArgs);
        writeAtomic(this.probeFile,{name:probeName,device:plan.selected[0].pci_address});
        let answer,error=null;
        try {
            const output=await this.call(flags,90000);
            answer=JSON.parse(output.split('\n').at(-1));
            if(JSON.stringify(answer?.pci_addresses)!==
                JSON.stringify([bindings[0].pci_address]))
                throw Error('Container HIP PCI identity does not match Hub GPU lease.');
        }catch(exc){error=exc;}
        // A timed-out Docker CLI can leave a daemon-owned container alive.
        // Never silently erase the pending proof in that case.
        let remains=true;
        try{remains=(await this.inspect(probeName))!==null;}catch{}
        if(!remains)fs.unlinkSync(this.probeFile);
        if(error||remains)
            throw Error('Docker GPU proof failed or requires cleanup: '+
                String(error?.message||probeName).slice(0,130));
        return {selected:[bindings[0].pci_address],runtime_pci_verified:true};
    }
    async start(job,lease) {
        if(!this.qualifiedAt||this.gpuProbeCleanupPending())
            throw Error('Docker owner requires successful preflight and no unresolved GPU proof.');
        if(this.state.some(x=>x.job_id===job?.id))
            throw Error('Docker job already has an ownership record; reconcile instead.');
        const invocation=validateLocalInput(job,lease,this.machineId,
            this.adapters,os,this.gpuEnabled);
        if(this.state.some(x=>ACTIVE.has(x.state)&&x.gpu_pci_address&&
            x.gpu_pci_address===invocation.gpu?.binding.pci_address))
            throw Error('Hub already owns an unresolved container on this physical GPU.');
        const d=fixedDockerSpec(this.adapters[job.project_id]);
        let plan=null;
        if(invocation.gpu) {
            const proof=gpuRuntimeProof(this.adapters[job.project_id],
                job.frozen_parameters,{job_id:job.id,
                    project_id:job.project_id,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    gpu_pci_address:invocation.gpu.binding.pci_address});
            await this.qualifyGpuDeviceAccess([invocation.gpu.binding],proof);
            plan=resolveRocmDevices([invocation.gpu.binding]);
        }
        const imageId=await this.imageId(d.image);
        const name=containerName(job.id);
        if(await this.inspect(name))
            throw Error('Deterministic Docker job name already exists outside the ownership journal.');
        const record={
            job_id:job.id,lease_id:lease.id,project_id:job.project_id,
            container_name:name,container_id:null,
            image_id:imageId,cpus:invocation.cpus,ram_mb:invocation.ram_mb,
            mounts:d.mounts.map(m=>[m.host,m.target,m.readonly]),
            user:d.uid+':'+d.gid,env:d.env,
            command:[invocation.executable,...invocation.args],
            ...(plan?{gpu_node:plan.selected[0].node,
                gpu_pci_address:plan.selected[0].pci_address,
                gpu_identity:deviceIdentity(plan)}:{}),
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
        return {state,checkpoint_stop_verified:verified,
            memory_current_mb:null,cpu_time_ns:null,process_tasks:null,
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
