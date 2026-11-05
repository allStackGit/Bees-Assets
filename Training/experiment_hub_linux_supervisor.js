'use strict';

// Local, opt-in Linux service supervisor. Not reachable through the Hub HTTP
// server or the heartbeat protocol. Only installed adapter code may choose an
// executable and arguments; saved experiment JSON cannot provide a command.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {promisify}=require('node:util');
const {execFile}=require('node:child_process');
const {inspectHost}=require('./experiment_hub_host');
const {verifyKernelLimits}=require('./experiment_hub_linux_cgroup');
const {resolveRocmDevices}=require('./experiment_hub_linux_gpu_devices');
const runDefault=promisify(execFile);
// Use known system binaries, not the agent's PATH.
const SYSTEMD_RUN='/usr/bin/systemd-run';
const SYSTEMCTL='/usr/bin/systemctl';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const SAFE_ARG=/^[A-Za-z0-9_./:@=+\-,]+$/;
const MAX_RECORDS=1000;

function unitFor(jobId) {
    if(typeof jobId!=='string'||!UUID.test(jobId))throw Error('Invalid job identity.');
    return 'experiment-hub-'+jobId.replace(/-/g,'')+'.service';
}
function descriptionFor(jobId) {
    return 'Experiment Hub job '+jobId;
}
function parseShow(text) {
    if(typeof text!=='string'||text.length>16384)
        throw Error('Invalid systemd service report.');
    const result={};
    for(const line of text.split('\n')) {
        if(!line.includes('='))continue;
        const pos=line.indexOf('=');
        const key=line.slice(0,pos),value=line.slice(pos+1).trim();
        if(/^[A-Za-z][A-Za-z0-9]*$/.test(key))result[key]=value;
    }
    return result;
}
function quotaUsec(value) {
    const match=typeof value==='string'&&value.match(/^([0-9]+(?:\.[0-9]+)?)(us|ms|s)$/);
    if(!match)return null;
    const scale={us:1,ms:1000,s:1000000}[match[2]];
    const amount=Number(match[1])*scale;
    return Number.isSafeInteger(amount)?amount:null;
}
function validateLocalInput(job,lease,machineId,adapters,hostOs=os) {
    if(!job||!lease||job.status!=='waiting_adapter'||
        job.project_id==='bees'||job.adapter!=='unconfigured'||
        lease.issued_by!=='experiment-hub'||
        lease.status!=='acknowledged'||lease.receipt_fresh!==true||
        lease.machine_id!==machineId||lease.job_id!==job.id||
        lease.id!==job.lease_id||lease.project_id!==job.project_id||
        !UUID.test(String(job.id))||!UUID.test(String(lease.id)))
        throw Error('A fresh, job-linked, acknowledged local lease is required.');
    if(!job.frozen_parameters||typeof job.frozen_parameters!=='object'||
        Array.isArray(job.frozen_parameters)||
        typeof job.configuration_sha256!=='string'||
        crypto.createHash('sha256').update(JSON.stringify(job.frozen_parameters))
            .digest('hex')!==job.configuration_sha256)
        throw Error('Frozen configuration fingerprint mismatch.');
    const r=job.resources,lr=lease.resources;
    const cpuLimit=typeof hostOs.availableParallelism==='function'
        ?hostOs.availableParallelism():hostOs.cpus().length;
    if(!r||!lr||!Number.isSafeInteger(r.cpus)||r.cpus<1||
        r.cpus>cpuLimit||
        !Number.isSafeInteger(r.ram_mb)||r.ram_mb<128||
        r.ram_mb>Math.floor(hostOs.totalmem()/1048576)||
        r.gpu_count!==0||lr.gpu_count!==0||
        (r.gpus||[]).length||(lr.gpus||[]).length||
        lr.cpus!==r.cpus||lr.ram_mb!==r.ram_mb)
        throw Error('Only CPU/RAM jobs within the local host limit are supported.');
    const adapter=adapters[job.project_id];
    if(!adapter||typeof adapter.args!=='function'||
        typeof adapter.executable!=='string'||
        !path.isAbsolute(adapter.executable)||
        !SAFE_ARG.test(adapter.executable)||
        (adapter.cwd!==undefined&&
            (typeof adapter.cwd!=='string'||!path.isAbsolute(adapter.cwd)||
             !SAFE_ARG.test(adapter.cwd))))
        throw Error('No valid locally installed trusted adapter for this project.');
    const stopPolicy=adapter.stop_policy||'terminate-group';
    if(!['terminate-group','interrupt-main'].includes(stopPolicy))
        throw Error('Unknown locally installed native stop policy.');
    const predecessor=job.continuation_of_job_id||null;
    const lineageRoot=job.artifact_lineage_root_id||job.id;
    if(!UUID.test(String(lineageRoot))||
        (predecessor!==null&&(!UUID.test(String(predecessor))||
            predecessor===job.id||lineageRoot===job.id))||
        (predecessor===null&&lineageRoot!==job.id))
        throw Error('Invalid immutable local continuation ancestry.');
    const args=adapter.args(JSON.parse(JSON.stringify(job.frozen_parameters)),
        Object.freeze({
            job_id:job.id,project_id:job.project_id,
            continuation_of_job_id:predecessor,
            artifact_lineage_root_id:lineageRoot,
        }));
    if(!Array.isArray(args)||args.length>64||
        args.some(a=>typeof a!=='string'||a.length>1024||
            !SAFE_ARG.test(a)))
        throw Error('Trusted adapter returned an unsafe invocation.');
    return {executable:adapter.executable,args,cwd:adapter.cwd||null,
        cpus:r.cpus,ram_mb:r.ram_mb,stop_policy:stopPolicy};
}
function launchArguments(record,invocation) {
    const args=['--user','--quiet','--service-type=exec',
        '--unit='+record.unit,'--description='+descriptionFor(record.job_id),
        '--property=CPUQuota='+(invocation.cpus*100)+'%',
        '--property=MemoryMax='+(invocation.ram_mb*1048576),
        '--property=KillMode='+
            (invocation.stop_policy==='interrupt-main'?'mixed':'control-group'),
        '--property=KillSignal='+
            (invocation.stop_policy==='interrupt-main'?'SIGINT':'SIGTERM'),
        '--property=TimeoutStopSec='+
            (invocation.stop_policy==='interrupt-main'?'600s':'20s'),
        ...(invocation.stop_policy==='interrupt-main'
            ?['--property=SuccessExitStatus=130']:[]),
        '--property=CPUAccounting=yes','--property=MemoryAccounting=yes',
        // These masks keep default CUDA/ROCm selectors from using a GPU
        // inside CPU-budgeted jobs; they do NOT create GPU device isolation.
        '--setenv=CUDA_VISIBLE_DEVICES=',
        '--setenv=HIP_VISIBLE_DEVICES=',
        '--setenv=ROCR_VISIBLE_DEVICES=',
        '--setenv=GPU_DEVICE_ORDINAL='];
    if(invocation.cwd)args.push('--working-directory='+invocation.cwd);
    // This is a systemd-run option boundary, not a shell.
    return args.concat('--',invocation.executable,...invocation.args);
}
function writeAtomic(file,data,fsApi=fs) {
    const pending=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    try{
        fsApi.writeFileSync(pending,JSON.stringify(data,null,2)+'\n',
            {flag:'wx',mode:0o600});
        fsApi.renameSync(pending,file);
    }finally{
        if(fsApi.existsSync(pending))fsApi.unlinkSync(pending);
    }
}
class LinuxSupervisor {
    constructor({root,machineId,adapters={},runner=runDefault,fsApi=fs,
        hostOs=os,hostInspector=inspectHost}={}) {
        if(!root||!path.isAbsolute(root)||!machineId||
            !/^[a-z][a-z0-9_-]{0,47}$/.test(machineId))
            throw Error('A local absolute data directory and machine identity are required.');
        if(hostOs.platform()!=='linux')
            throw Error('Native Linux supervisor unavailable on this operating system.');
        this.root=root;this.file=path.join(root,'supervisor-v1.json');
        this.lock=path.join(root,'supervisor.lock');
        // A GPU preflight may outlive the agent after a timeout or crash.
        // Persist its exact service identity before dispatching systemd.
        this.gpuProbePending=path.join(root,'gpu-preflight-pending.json');
        this.machineId=machineId;this.adapters=adapters;this.runner=runner;
        this.fs=fsApi;this.hostOs=hostOs;this.hostInspector=hostInspector;
        // Qualification is process-local. No persisted historical probe
        // can authorize a launch after the agent or OS has restarted.
        this.qualifiedAt=null;
        fsApi.mkdirSync(root,{recursive:true,mode:0o700});
        this.state=fsApi.existsSync(this.file)
            ?JSON.parse(fsApi.readFileSync(this.file,'utf8'))
            :{version:1,revision:0,records:[]};
        if(this.state?.version!==1||!Number.isSafeInteger(this.state.revision)||
            !Array.isArray(this.state.records)||this.state.records.length>MAX_RECORDS||
            this.state.records.some(r=>!r||!UUID.test(String(r.job_id))||
                r.unit!==unitFor(r.job_id))||
            new Set(this.state.records.map(r=>r.job_id)).size!==this.state.records.length)
            throw Error('Invalid local supervisor ledger; refusing to start.');
    }
    // Every mutation is guarded by an exclusive local lock and a revision
    // check. A crash leaving a lock requires manual recovery, not a blind retry.
    commit(records) {
        let fd;
        try{fd=this.fs.openSync(this.lock,'wx',0o600);}
        catch{throw Error('Local supervisor ledger locked; inspect existing units before recovery.');}
        try{
            const onDisk=this.fs.existsSync(this.file)
                ?JSON.parse(this.fs.readFileSync(this.file,'utf8'))
                :{version:1,revision:0,records:[]};
            if(onDisk.version!==1||onDisk.revision!==this.state.revision)
                throw Error('Supervisor ledger changed concurrently; reopen the supervisor.');
            const next={version:1,revision:this.state.revision+1,records};
            writeAtomic(this.file,next,this.fs);
            this.state=next;
        }finally{
            this.fs.closeSync(fd);
            this.fs.unlinkSync(this.lock);
        }
    }
    snapshot(){return JSON.parse(JSON.stringify(this.state.records));}
    async qualify() {
        if(this.qualifiedAt)return {qualified_at:this.qualifiedAt};
        const host=this.hostInspector();
        if(!host.cgroup_v2_detected||
            !['cpu','memory'].every(c=>host.cgroup_controllers.includes(c)))
            throw Error('Native preflight: Linux CPU/memory controllers unavailable.');
        // Only an inert local sleep program is started. No project adapter,
        // job, checkpoint, experiment artifact or network command is involved.
        const id=crypto.randomUUID();
        const unit='experiment-hub-preflight-'+id.replace(/-/g,'')+'.service';
        const description='Experiment Hub local resource preflight '+id;
        const args=['--user','--quiet','--service-type=exec',
            '--unit='+unit,'--description='+description,
            '--property=CPUQuota=100%',
            '--property=MemoryMax=268435456',
            '--property=KillMode=control-group',
            '--property=CPUAccounting=yes','--property=MemoryAccounting=yes',
            '--','/usr/bin/sleep','30'];
        let error=null,verified=false;
        try{
            await this.runner(SYSTEMD_RUN,args,
                {timeout:12000,maxBuffer:16384,windowsHide:true});
            const shown=await this.runner(SYSTEMCTL,
                ['--user','show',unit,'--no-pager',
                 '--property=Id,Description,LoadState,ActiveState,MainPID,ControlGroup,MemoryMax,CPUQuotaPerSecUSec'],
                {timeout:12000,maxBuffer:16384,windowsHide:true});
            const p=parseShow(shown.stdout);
            if(p.LoadState!=='loaded'||p.ActiveState!=='active'||
                p.Id!==unit||p.Description!==description||
                Number(p.MemoryMax)!==268435456||
                quotaUsec(p.CPUQuotaPerSecUSec)!==1000000)
                throw Error('Preflight service identity or systemd controls not verified.');
            verifyKernelLimits(p,1,256,this.fs);
            verified=true;
        }catch(e){error=e;}
        // Even an ambiguous systemd-run timeout can leave a service behind.
        // Never auto-retry it, and never stop a service with unknown identity.
        try{
            const shown=await this.runner(SYSTEMCTL,
                ['--user','show',unit,'--no-pager',
                    '--property=Id,Description,LoadState,ActiveState'],
                {timeout:12000,maxBuffer:16384,windowsHide:true});
            const p=parseShow(shown.stdout);
            if(p.LoadState!=='not-found') {
                if(p.Id!==unit||p.Description!==description)
                    throw Error('Preflight service ownership is ambiguous; inspect it manually.');
                await this.runner(SYSTEMCTL,['--user','stop',unit],
                    {timeout:12000,maxBuffer:16384,windowsHide:true});
                const ended=await this.runner(SYSTEMCTL,
                    ['--user','show',unit,'--no-pager',
                        '--property=Id,Description,LoadState,ActiveState'],
                    {timeout:12000,maxBuffer:16384,windowsHide:true});
                const after=parseShow(ended.stdout);
                if(!['inactive','failed'].includes(after.ActiveState)&&
                    after.LoadState!=='not-found')
                    throw Error('Preflight service stop was not confirmed.');
            }
        }catch(cleanup){
            this.qualifiedAt=null;
            throw Error('Native preflight cleanup not confirmed: '+
                String(cleanup.message||cleanup).slice(0,155));
        }
        if(error)throw Error('Native preflight failed: '+
            String(error.message||error).slice(0,155));
        if(!verified)throw Error('Native preflight did not verify kernel controls.');
        this.qualifiedAt=new Date().toISOString();
        return {qualified_at:this.qualifiedAt};
    }

    // This is an on-demand, inert device-cgroup qualification API.
    // It does NOT authorize or start a GPU workload. Hub GPU starts remain
    // blocked until deployment-specific ROCm and isolation verification.
    async qualifyGpuDeviceAccess(bindings) {
        if(!this.qualifiedAt)
            throw Error('CPU/RAM preflight must pass before GPU device checks.');
        if(this.gpuPreflightCleanupUnconfirmed===true||
            this.fs.existsSync(this.gpuProbePending))
            throw Error('Previous GPU access probe cleanup unconfirmed; inspect the recorded local service before retry.');
        const plan=resolveRocmDevices(bindings,{fsApi:this.fs});
        // A proof of denial requires a second GPU render node that the same
        // unconfined account can open. Do not mistake a UNIX ACL failure for
        // a working eBPF device filter.
        if(!plan.unselected.length||plan.selected.length!==1)
            throw Error('GPU access qualification needs one selected and at least one excluded AMD render node.');
        const denied=plan.unselected.map(device=>device.node).sort();
        const openable=[plan.kfd.node,plan.selected[0].node,...denied];
        for(const node of openable) {
            let handle;
            try{handle=this.fs.openSync(node,'r+');}
            catch{throw Error('GPU probe baseline cannot open device '+node);}
            finally{if(handle!==undefined)this.fs.closeSync(handle);}
        }
        const nonce=crypto.randomUUID(),unit=
            'experiment-hub-gpu-preflight-'+nonce.replace(/-/g,'')+'.service';
        const description='Experiment Hub GPU device preflight '+nonce;
        // Acquire an exclusive, crash-persistent intent before a native
        // service can be created. Never overwrite another probe's record.
        // A crash or uncertain cleanup deliberately requires an operator to
        // inspect the recorded systemd unit before clearing this file.
        let pendingFd;
        try {
            pendingFd=this.fs.openSync(this.gpuProbePending,'wx',0o600);
            this.fs.writeFileSync(pendingFd,
                JSON.stringify({unit,description})+'\n');
        }finally {
            if(pendingFd!==undefined)this.fs.closeSync(pendingFd);
        }
        const args=['--user','--quiet','--pipe','--collect',
            '--service-type=exec','--unit='+unit,
            '--description='+description,
            '--property=CPUQuota=100%',
            '--property=MemoryMax=268435456',
            ...plan.device_properties.map(p=>'--property='+p),
            '--',process.execPath,
            path.join(__dirname,'experiment_hub_linux_gpu_probe.js'),
            plan.kfd.node,plan.selected[0].node,...denied];
        let failure=null;
        try {
            const response=await this.runner(SYSTEMD_RUN,args,
                {timeout:20000,maxBuffer:8192,windowsHide:true});
            const actual=JSON.parse(response.stdout.trim());
            if(actual.kfd!==plan.kfd.node||
                actual.allowed!==plan.selected[0].node||
                !Array.isArray(actual.denied)||
                actual.denied.length!==denied.length||
                denied.some((node,index)=>actual.denied[index]!==node)||
                actual.kfd_access!==true||actual.allowed_access!==true||
                !Array.isArray(actual.denied_access)||
                actual.denied_access.length!==denied.length||
                actual.denied_access.some(access=>access!==false))
                throw Error('Linux device filter did not prove allowed and denied GPU access.');
        }catch(error){failure=error;}
        // A timed-out device probe may still be running. Only control the
        // uniquely named, exactly matching service, never an arbitrary PID.
        try {
            const show=await this.runner(SYSTEMCTL,
                ['--user','show',unit,'--no-pager',
                    '--property=Id,Description,LoadState,ActiveState'],
                {timeout:12000,maxBuffer:16384,windowsHide:true});
            const state=parseShow(show.stdout);
            if(state.LoadState!=='not-found') {
                if(state.LoadState!=='loaded'||state.Id!==unit||
                    state.Description!==description)
                    throw Error('GPU preflight unit identity is ambiguous.');
                if(['active','activating','deactivating'].includes(state.ActiveState)) {
                    await this.runner(SYSTEMCTL,
                        ['--user','stop',unit],
                        {timeout:12000,maxBuffer:16384,windowsHide:true});
                    const ended=await this.runner(SYSTEMCTL,
                        ['--user','show',unit,'--no-pager',
                            '--property=Id,Description,LoadState,ActiveState'],
                        {timeout:12000,maxBuffer:16384,windowsHide:true});
                    const after=parseShow(ended.stdout);
                    if(after.LoadState!=='not-found'&&
                        (after.LoadState!=='loaded'||after.Id!==unit||
                         after.Description!==description||
                         !['inactive','failed'].includes(after.ActiveState)))
                        throw Error('GPU preflight cleanup was not confirmed.');
                }else if(!['inactive','failed'].includes(state.ActiveState)) {
                    throw Error('GPU preflight service state is uncertain.');
                }
            }
        }catch(cleanup) {
            // Do not create more preflight services while a previous
            // ambiguous one may still be running.
            this.gpuPreflightCleanupUnconfirmed=true;
            throw Error('GPU device access preflight cleanup not verified: '+
                String(cleanup.message||cleanup).slice(0,160));
        }
        // An unambiguous stopped/missing unit is the only condition that
        // allows a future probe, including after an agent restart.
        try{this.fs.unlinkSync(this.gpuProbePending);}
        catch(error){
            this.gpuPreflightCleanupUnconfirmed=true;
            throw Error('GPU preflight record cleanup not verified: '+
                String(error.message||error).slice(0,160));
        }
        if(failure)
            throw Error('GPU device access preflight failed: '+
                String(failure.message||failure).slice(0,160));
        const observed=resolveRocmDevices(bindings,{fsApi:this.fs});
        const identities=devices=>devices.map(device=>
            [device.pci_address||null,device.node,device.major,device.minor].join(':'))
            .sort().join('\n');
        if(identities([plan.kfd,...plan.selected,...plan.unselected])!==
            identities([observed.kfd,...observed.selected,...observed.unselected]))
            throw Error('GPU device identity changed during the access probe.');
        return {qualified_at:new Date().toISOString(),
            selected:plan.selected.map(g=>g.pci_address)};
    }

    async inspectUnit(record,{requireKernel=true}={}) {
        const out=await this.runner(SYSTEMCTL,
            ['--user','show',record.unit,'--no-pager',
                '--property=Id,Description,LoadState,ActiveState,SubState,Result,MainPID,MemoryMax,CPUQuotaPerSecUSec,MemoryCurrent,CPUUsageNSec,TasksCurrent,KillMode,KillSignal,ExecMainStatus,ControlGroup'],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        const p=parseShow(out.stdout);
        if(p.LoadState==='not-found')
            return {state:'unverified',active:false,unit_exists:false};
        if(p.LoadState!=='loaded')
            return {state:'unverified',active:false,unit_exists:true};
        if(p.Id!==record.unit||p.Description!==descriptionFor(record.job_id))
            throw Error('Unit identity mismatch; refusing to control an unknown service.');
        const expectedMemory=record.ram_mb*1048576;
        const memory=Number(p.MemoryMax),cpu=quotaUsec(p.CPUQuotaPerSecUSec);
        if(!Number.isSafeInteger(memory)||memory!==expectedMemory||
            cpu!==record.cpus*1000000)
            throw Error('Systemd did not confirm the exact CPU and memory limits.');
        if(record.stop_policy==='interrupt-main'&&
            (p.KillMode!=='mixed'||
                !['SIGINT','2'].includes(p.KillSignal)))
            throw Error('Systemd did not confirm checkpoint-aware interrupt semantics.');
        if(p.ActiveState==='active'||p.ActiveState==='deactivating') {
            const kernel=requireKernel
                ?verifyKernelLimits(p,record.cpus,record.ram_mb,this.fs):null;
            const numeric=value=>/^[0-9]+$/.test(value||'')&&
                Number.isSafeInteger(Number(value))?Number(value):null;
            const memoryBytes=numeric(p.MemoryCurrent);
            return {state:'running',active:true,unit_exists:true,
                kernel_limits_verified:kernel?.verified===true,
                pid:numeric(p.MainPID)||null,
                memory_current_mb:memoryBytes==null?null:
                    Math.round(memoryBytes/1048576),
                cpu_time_ns:numeric(p.CPUUsageNSec),
                process_tasks:numeric(p.TasksCurrent)};
        }
        if(p.ActiveState==='inactive'||p.ActiveState==='failed') {
            // Ants' reviewed CLI returns 130 after a handled SIGINT, even
            // when it deliberately preserved the previous durable checkpoint.
            const expectedInterrupt=record.stop_requested_at&&
                record.stop_policy==='interrupt-main'&&
                p.Result==='exit-code'&&p.ExecMainStatus==='130';
            return {state:p.Result==='success'||expectedInterrupt
                ?'finished':'failed',
                active:false,unit_exists:true,pid:null};
        }
        return {state:'unverified',active:false,unit_exists:true};
    }
    // Does not restart anything. A crash after the start request leaves a
    // durable record that must be inspected before an operator can proceed.
    async reconcile(jobId) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown locally supervised job.');
        let result;
        try{result=await this.inspectUnit(record);}
        catch(error){result={state:'unverified',active:false,
            diagnostic:String(error.message||error).slice(0,220)};}
        const state=record.quarantined_at?'unverified'
            :record.stop_requested_at&&result.active?'stop_requested'
            :record.stop_requested_at&&!result.active&&
                result.state==='finished'?'stopped':result.state;
        const updated={...record,state,
            observed_at:new Date().toISOString(),
            diagnostic:result.diagnostic||null};
        if(record.state!==updated.state ||
            (record.diagnostic||null)!==updated.diagnostic)
            this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        return {...updated,active:result.active,
            pid:result.pid??null,
            memory_current_mb:result.memory_current_mb??null,
            cpu_time_ns:result.cpu_time_ns??null,
            process_tasks:result.process_tasks??null};
    }
    async quarantineUnverified(jobId,reason) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown launch record; no native cleanup authority.');
        // This narrow operation is allowed to stop a service whose limits
        // are wrong, but only after independent deterministic identity
        // verification. Never stop an unrelated unit by guessed PID.
        const output=await this.runner(SYSTEMCTL,
            ['--user','show',record.unit,'--no-pager',
                '--property=Id,Description,LoadState,ActiveState'],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        const status=parseShow(output.stdout);
        if(status.LoadState!=='not-found'&&
            (status.Id!==record.unit||
                status.Description!==descriptionFor(record.job_id)))
            throw Error('Unverified native unit has unexpected identity; manual recovery required.');
        if(!record.quarantined_at) {
            const updated={...record,state:'unverified',
                quarantined_at:new Date().toISOString(),
                stop_requested_at:record.stop_requested_at||
                    new Date().toISOString(),
                diagnostic:String(reason||'Kernel verification failed.').slice(0,220)};
            this.commit(this.state.records.map(x=>x.job_id===jobId?updated:x));
        }
        if(status.LoadState==='loaded'&&
            ['active','activating','deactivating'].includes(status.ActiveState)) {
            await this.runner(SYSTEMCTL,
                ['--user','stop','--no-block',record.unit],
                {timeout:12000,maxBuffer:16384,windowsHide:true});
        }
        return {state:'unverified',quarantined:true};
    }
    async start(job,lease) {
        if(!this.qualifiedAt)
            throw Error('Linux kernel resource preflight must pass before native start.');
        const host=this.hostInspector();
        if(!host.cgroup_v2_detected||
            !['cpu','memory'].every(c=>host.cgroup_controllers.includes(c)))
            throw Error('Linux cgroup v2 CPU and memory controllers are not available.');
        if(job&&this.state.records.some(r=>r.job_id===job.id))
            throw Error('Job already has a local launch record; reconcile rather than restart.');
        const invocation=validateLocalInput(job,lease,this.machineId,
            this.adapters,this.hostOs);
        if(job.continuation_of_job_id) {
            const parent=this.state.records.find(r=>
                r.job_id===job.continuation_of_job_id);
            if(!parent||parent.project_id!==job.project_id||
                !['finished','stopped'].includes(parent.state)||
                (parent.artifact_lineage_root_id||parent.job_id)!==
                    job.artifact_lineage_root_id)
                throw Error('Local predecessor is missing, unverified or not terminal.');
            const native=await this.inspectUnit(parent);
            // Finished user transient units may be garbage-collected by
            // systemd. A durable terminal journal plus a truly not-found
            // unit is acceptable; a loaded failed/unknown unit is not.
            if(native.active||
                !(native.unit_exists===false||native.state==='finished'))
                throw Error('Predecessor service is not demonstrably inactive.');
        }
        if(this.state.records.length>=MAX_RECORDS)
            throw Error('Supervisor history capacity reached; no launch permitted.');
        // Refuse to create a service when a deterministic unit already
        // exists but the local ledger has lost ownership of it.
        const unit=unitFor(job.id);
        const existing=await this.inspectUnit({
            job_id:job.id,unit,cpus:invocation.cpus,ram_mb:invocation.ram_mb,
        });
        if(existing.unit_exists!==false)
            throw Error('A matching systemd unit already exists without a local ownership record.');
        const record={
            job_id:job.id,lease_id:lease.id,project_id:job.project_id,
            continuation_of_job_id:job.continuation_of_job_id||null,
            artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
            unit:unitFor(job.id),state:'launch_requested',
            cpus:invocation.cpus,ram_mb:invocation.ram_mb,
            stop_policy:invocation.stop_policy,
            requested_at:new Date().toISOString(),observed_at:null,diagnostic:null,
        };
        this.commit(this.state.records.concat(record));
        try{
            await this.runner(SYSTEMD_RUN,launchArguments(record,invocation),
                {timeout:30000,maxBuffer:16384,windowsHide:true});
        }catch(error){
            // A systemd-run timeout can mean that a process already started.
            // Keep the durable ownership record and attempt a narrow stop
            // only for this unit's exact verified identity; never re-launch.
            const reason=String(error.message||error).slice(0,180);
            let cleanup=null;
            try{await this.quarantineUnverified(job.id,reason);}
            catch(e){cleanup=String(e.message||e).slice(0,150);}
            throw Error('Service launch not confirmed; native job quarantined.'+
                (cleanup?' Cleanup requires inspection: '+cleanup:'')+
                ' Cause: '+reason);
        }
        const observed=await this.reconcile(job.id);
        if(observed.state!=='running'&&observed.state!=='finished') {
            let cleanup=null;
            try{
                await this.quarantineUnverified(job.id,
                    observed.diagnostic||'Native CPU/RAM limits not verified.');
            }catch(e){cleanup=String(e.message||e).slice(0,150);}
            throw Error('Service dispatched but native controls not verified.'+
                (cleanup?' Cleanup requires inspection: '+cleanup:'')+
                ' Inspect '+record.unit);
        }
        return observed;
    }
    async stop(jobId) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown locally supervised job.');
        // Re-check exact unit identity AND quota properties before stopping.
        // A service with failed/missing kernel limits must still be
        // stoppable by its owning agent. Exact unit identity and the
        // original systemd resource properties remain required.
        const existing=await this.inspectUnit(record,{requireKernel:false});
        if(!existing.active)return this.reconcile(jobId);
        if(record.stop_requested_at)
            return this.reconcile(jobId);
        // Persist the stop intent first. No automatic retry is safe if the
        // service-manager call or agent crashes while signal delivery is unknown.
        const updated={...record,state:'stop_requested',
            stop_requested_at:new Date().toISOString()};
        this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        await this.runner(SYSTEMCTL,['--user','stop','--no-block',record.unit],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        return this.reconcile(jobId);
    }
}
module.exports={LinuxSupervisor,validateLocalInput,unitFor,launchArguments,
    parseShow,quotaUsec};
