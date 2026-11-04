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
    if(!r||!lr||!Number.isSafeInteger(r.cpus)||r.cpus<1||
        r.cpus>hostOs.availableParallelism()||
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
    const args=adapter.args(JSON.parse(JSON.stringify(job.frozen_parameters)));
    if(!Array.isArray(args)||args.length>64||
        args.some(a=>typeof a!=='string'||a.length>1024||
            !SAFE_ARG.test(a)))
        throw Error('Trusted adapter returned an unsafe invocation.');
    return {executable:adapter.executable,args,cwd:adapter.cwd||null,
        cpus:r.cpus,ram_mb:r.ram_mb};
}
function launchArguments(record,invocation) {
    const args=['--user','--quiet','--service-type=exec',
        '--unit='+record.unit,'--description='+descriptionFor(record.job_id),
        '--property=CPUQuota='+(invocation.cpus*100)+'%',
        '--property=MemoryMax='+(invocation.ram_mb*1048576),
        '--property=KillMode=control-group','--property=TimeoutStopSec=20s',
        '--property=CPUAccounting=yes','--property=MemoryAccounting=yes'];
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
    async inspectUnit(record) {
        const out=await this.runner(SYSTEMCTL,
            ['--user','show',record.unit,'--no-pager',
                '--property=Id,Description,LoadState,ActiveState,SubState,Result,MainPID,MemoryMax,CPUQuotaPerSecUSec'],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        const p=parseShow(out.stdout);
        if(p.LoadState!=='loaded')return {state:'unverified',active:false};
        if(p.Id!==record.unit||p.Description!==descriptionFor(record.job_id))
            throw Error('Unit identity mismatch; refusing to control an unknown service.');
        const expectedMemory=record.ram_mb*1048576;
        const memory=Number(p.MemoryMax),cpu=quotaUsec(p.CPUQuotaPerSecUSec);
        if(!Number.isSafeInteger(memory)||memory!==expectedMemory||
            cpu!==record.cpus*1000000)
            throw Error('Systemd did not confirm the exact CPU and memory limits.');
        if(p.ActiveState==='active')
            return {state:'running',active:true,pid:Number(p.MainPID)||null};
        if(p.ActiveState==='inactive'||p.ActiveState==='failed')
            return {state:p.Result==='success'?'finished':'failed',
                active:false,pid:null};
        return {state:'unverified',active:false};
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
        const state=record.stop_requested_at && !result.active &&
            result.state==='finished'?'stopped':result.state;
        const updated={...record,state,
            observed_at:new Date().toISOString(),
            diagnostic:result.diagnostic||null};
        if(record.state!==updated.state ||
            (record.diagnostic||null)!==updated.diagnostic)
            this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        return {...updated,active:result.active};
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
        if(this.state.records.length>=MAX_RECORDS)
            throw Error('Supervisor history capacity reached; no launch permitted.');
        const record={
            job_id:job.id,lease_id:lease.id,project_id:job.project_id,
            unit:unitFor(job.id),state:'launch_requested',
            cpus:invocation.cpus,ram_mb:invocation.ram_mb,
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
        const existing=await this.inspectUnit(record);
        if(!existing.active)return this.reconcile(jobId);
        if(!record.stop_requested_at) {
            const updated={...record,state:'stop_requested',
                stop_requested_at:new Date().toISOString()};
            this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        }
        await this.runner(SYSTEMCTL,['--user','stop',record.unit],
            {timeout:30000,maxBuffer:16384,windowsHide:true});
        return this.reconcile(jobId);
    }
}
module.exports={LinuxSupervisor,validateLocalInput,unitFor,launchArguments,
    parseShow,quotaUsec};
