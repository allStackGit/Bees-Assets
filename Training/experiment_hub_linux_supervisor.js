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
        this.machineId=machineId;this.adapters=adapters;this.runner=runner;
        this.fs=fsApi;this.hostOs=hostOs;this.hostInspector=hostInspector;
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
        const state=record.stop_requested_at&&result.active
            ?'stop_requested'
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
    async start(job,lease) {
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
            // A systemd command timeout is ambiguous: the service could have
            // been created. Never delete the launch intent or auto-retry.
            const observed=await this.reconcile(job.id);
            throw Error('Service launch not confirmed ('+observed.state+
                '): '+String(error.message||error).slice(0,180));
        }
        const observed=await this.reconcile(job.id);
        if(observed.state!=='running'&&observed.state!=='finished')
            throw Error('Service dispatched but native controls not verified; inspect '+record.unit);
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
