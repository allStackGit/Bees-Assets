'use strict';

// Opt-in Windows supervisor. One independently installed, pinned .NET helper
// owns each Windows Job Object for the entire workload lifetime. The agent
// uses the helper's authenticated pipe and optional adapter stop marker.
// No PowerShell, shell, PID guessing, arbitrary command API or Bees ownership.
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const net=require('node:net');
const crypto=require('node:crypto');
const {spawn,execFile}=require('node:child_process');
const {promisify}=require('node:util');
const runDefault=promisify(execFile);
const {matchingLeaseBudget,startHeadroom,ensureLocalCpuRamCapacity}=
    require('./experiment_hub_resources');
const {availableMemoryMb}=require('./experiment_hub_host');
const {discoverNvidiaGpus}=require('./experiment_hub_gpu_inventory');
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const ID=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA=/^[a-f0-9]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const CUDA_UUID=/^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const terminal=new Set(['finished','failed','stopped']);
const noControls=s=>typeof s==='string'&&s.length>0&&s.length<=1024&&
    !/[\0\r\n]/.test(s);
function writeAtomic(fsApi,file,value,cryptoApi=crypto) {
    const temp=file+'.'+cryptoApi.randomBytes(8).toString('hex')+'.tmp';
    try {
        fsApi.writeFileSync(temp,JSON.stringify(value)+'\n',{flag:'wx',mode:0o600});
        fsApi.renameSync(temp,file);
    }finally{
        if(fsApi.existsSync(temp))fsApi.unlinkSync(temp);
    }
}
function validateInvocation(job,lease,machineId,adapters,hostOs,pathApi,cryptoApi,
    gpuEnabled=false,gpuInventory=discoverNvidiaGpus) {
    if(!job||!lease||!UUID.test(String(job.id))||
        !UUID.test(String(lease.id))||job.status!=='waiting_adapter'||
        job.adapter!=='unconfigured'||job.project_id==='bees'||
        !ID.test(String(job.project_id))||job.lease_id!==lease.id||
        lease.issued_by!=='experiment-hub'||
        lease.status!=='acknowledged'||lease.receipt_fresh!==true||
        lease.machine_id!==machineId||lease.project_id!==job.project_id||
        lease.job_id!==job.id)
        throw Error('Authenticated, matching job/lease required for local Windows launch.');
    const frozen=job.frozen_parameters;
    if(!frozen||typeof frozen!=='object'||Array.isArray(frozen)||
        cryptoApi.createHash('sha256').update(JSON.stringify(frozen))
            .digest('hex')!==job.configuration_sha256)
        throw Error('Frozen Windows job configuration fingerprint mismatch.');
    const r=job.resources,lr=lease.resources;
    // Job Object CPU rates are percentages of the *system's* processors,
    // not the Node process's affinity-limited available parallelism.
    const cores=hostOs.cpus().length;
    const available=typeof hostOs.availableParallelism==='function'
        ?hostOs.availableParallelism():cores;
    if(!Number.isSafeInteger(cores)||cores<1||
        !Number.isSafeInteger(available)||available<1||
        !r||!lr||!Number.isSafeInteger(r.cpus)||r.cpus<1||
        r.cpus>available||r.cpus>cores||r.cpus!==lr.cpus||
        !Number.isSafeInteger(r.ram_mb)||r.ram_mb<128||
        r.ram_mb>Math.floor(hostOs.totalmem()/1048576)||
        r.ram_mb!==lr.ram_mb)
        throw Error('Windows native job CPU/RAM request is invalid.');
    if(!matchingLeaseBudget(r,lr))
        throw Error('Windows resource budget differs from the acknowledged lease.');
    const availableRam=availableMemoryMb(hostOs);
    const ramBlock=startHeadroom(r,availableRam);
    if(ramBlock)throw Error(ramBlock);
    const adapter=adapters[job.project_id];
    if(!adapter||typeof adapter.args!=='function'||
        adapter.stop_policy!=='terminate-group'||
        !noControls(adapter.executable)||
        !pathApi.isAbsolute(adapter.executable)||
        (adapter.cwd!=null&&
            (!noControls(adapter.cwd)||!pathApi.isAbsolute(adapter.cwd))))
        throw Error('Install a Windows-local adapter explicitly allowing force-stop only.');
    let gpu=null;
    if(r.gpu_count!==0) {
        const binding=lease.gpu_bindings?.[0];
        const cooperative=r.gpu_mode==='shared';
        if(!gpuEnabled||adapter.gpu_execution!=='cuda'||
            r.gpu_count!==1||lr.gpu_count!==1||
            lr.gpu_mode!==r.gpu_mode||
            !(r.gpu_mode==='exclusive'||
                cooperative&&adapter.gpu_sharing==='cooperative-pytorch')||
            lr.gpus?.length!==1||!Array.isArray(lease.gpu_bindings)||
            lease.gpu_bindings.length!==1||
            (r.gpus?.length>0&&r.gpus[0]!==lr.gpus[0])||
            binding?.id!==lr.gpus[0]||
            !PCI.test(String(binding.pci_address||''))||
            !CUDA_UUID.test(String(binding.cuda_uuid||''))||
            (cooperative&&(!Number.isSafeInteger(r.gpu_vram_mb)||
                r.gpu_vram_mb<1||r.gpu_vram_mb>binding.vram_mb||
                !Number.isSafeInteger(r.gpu_compute_pct)||
                r.gpu_compute_pct<1||r.gpu_compute_pct>100)))
            throw Error('Windows CUDA requires one physically pinned GPU and an approved exclusive or cooperative share.');
        const current=gpuInventory();
        const selected=Array.isArray(current)?current.find(device=>
            device.pci_address===binding.pci_address&&
            device.cuda_uuid===binding.cuda_uuid&&
            Number.isSafeInteger(device.vram_mb)&&device.vram_mb>=binding.vram_mb):null;
        if(!selected)
            throw Error('The CUDA device identity or physical VRAM changed before Start.');
        const gpuBlock=startHeadroom(r,availableRam,selected);
        if(gpuBlock)throw Error(gpuBlock);
        gpu={pci_address:binding.pci_address,cuda_uuid:binding.cuda_uuid,
            mode:r.gpu_mode,vram_mb:cooperative?r.gpu_vram_mb:0,
            compute_pct:cooperative?r.gpu_compute_pct:100,
            total_vram_mb:binding.vram_mb};
    }else if(lr.gpu_count!==0||(r.gpus||[]).length||(lr.gpus||[]).length)
        throw Error('Unexpected GPU resources in a CPU-only Windows lease.');
    const predecessor=job.continuation_of_job_id||null,
        root=job.artifact_lineage_root_id||job.id;
    if(!UUID.test(String(root))||
        (predecessor!==null&&(!UUID.test(String(predecessor))||
            predecessor===job.id||root===job.id))||
        (predecessor===null&&root!==job.id))
        throw Error('Invalid Windows continuation ancestry.');
    const context=Object.freeze({
        job_id:job.id,project_id:job.project_id,
        cpu_quota:r.cpus,
        dispatch_id:job.dispatch_id||null,evaluation_suite_id:job.evaluation_stop?.suite_id||
            job.evaluation_stop?.all_of?.[0]?.suite_id||null,
        artifact_lineage_root_id:root,continuation_of_job_id:predecessor,
        ...(job.expected_parent_model_sha256?
            {expected_parent_model_sha256:job.expected_parent_model_sha256}:{}),
        ...(gpu?{gpu_pci_address:gpu.pci_address,gpu_cuda_uuid:gpu.cuda_uuid,
            gpu_mode:gpu.mode,gpu_vram_mb:gpu.vram_mb,
            gpu_total_vram_mb:gpu.total_vram_mb,
            gpu_compute_pct:gpu.compute_pct}:{}),
    });
    const stopFile=adapter.checkpoint_stop_file?.(context)??null;
    if(stopFile!==null&&(!noControls(stopFile)||!pathApi.isAbsolute(stopFile)))
        throw Error('Windows checkpoint Stop requires an absolute local request path.');
    const argv=adapter.args(JSON.parse(JSON.stringify(frozen)),context);
    if(!Array.isArray(argv)||argv.length>64||argv.some(x=>
        typeof x!=='string'||x.length>1024||/[\0\r\n]/.test(x)))
        throw Error('Windows adapter returned unsafe native arguments.');
    return {executable:adapter.executable,args:argv,cwd:adapter.cwd||'',
        cpus:r.cpus,hostCpus:cores,ramMb:r.ram_mb,
        root,predecessor,stopFile,gpu};
}
// The coordinator and agent both budget shares. Recheck the private
// Windows native journal before launching so unresolved local owners
// cannot be oversubscribed after a coordinator restart.
function ensureLocalGpuCapacity(records,gpu) {
    if(!gpu)return;
    let reservedVram=gpu.mode==='shared'?gpu.vram_mb:gpu.total_vram_mb;
    let reservedCompute=gpu.compute_pct;
    for(const record of records) {
        if(terminal.has(record.state)||
            record.gpu_pci_address!==gpu.pci_address)continue;
        if(gpu.mode!=='shared'||record.gpu_mode!=='shared'||
            record.gpu_cuda_uuid!==gpu.cuda_uuid||
            !Number.isSafeInteger(record.gpu_vram_mb)||
            record.gpu_vram_mb<1||
            !Number.isSafeInteger(record.gpu_compute_pct)||
            record.gpu_compute_pct<1)
            throw Error('Another native job retains exclusive or unverified Windows CUDA ownership.');
        reservedVram+=record.gpu_vram_mb;
        reservedCompute+=record.gpu_compute_pct;
    }
    if(reservedVram>gpu.total_vram_mb||reservedCompute>100)
        throw Error('Shared native CUDA capacity exceeds the physical device budget.');
}

class WindowsSupervisor {
    constructor({root,machineId,adapters,helperPath,helperSha256,
        gpuEnabled=false,gpuInventory=discoverNvidiaGpus,
        fsApi=fs,pathApi=path.win32,hostOs=os,cryptoApi=crypto,
        run=runDefault,spawnProcess=spawn,netApi=net}={}) {
        if(hostOs.platform()!=='win32'||!pathApi.isAbsolute(root||'')||
            !pathApi.isAbsolute(helperPath||'')||
            !ID.test(String(machineId||''))||!SHA.test(String(helperSha256||'')))
            throw Error('Windows native owner requires an absolute local helper, root and SHA-256 pin.');
        this.root=root;this.machineId=machineId;this.adapters=adapters||{};
        this.helperPath=helperPath;this.helperSha256=helperSha256;
        this.fs=fsApi;this.path=pathApi;this.hostOs=hostOs;
        this.crypto=cryptoApi;this.run=run;
        this.gpuEnabled=gpuEnabled===true;this.gpuExecution='cuda';
        this.gpuInventory=gpuInventory;
        this.spawnProcess=spawnProcess;this.net=netApi;
        this.file=pathApi.join(root,'windows-supervisor-v1.json');
        this.lock=pathApi.join(root,'windows-supervisor.lock');
        fsApi.mkdirSync(root,{recursive:true,mode:0o700});
        this.state=fsApi.existsSync(this.file)
            ?JSON.parse(fsApi.readFileSync(this.file,'utf8'))
            :{version:1,revision:0,records:[]};
        if(this.state.version!==1||
            !Number.isSafeInteger(this.state.revision)||
            !Array.isArray(this.state.records)||
            this.state.records.length>1000||
            this.state.records.some(r=>!r||!UUID.test(String(r.job_id))||
                !UUID.test(String(r.lease_id))||!SHA.test(String(r.token))||
                (r.checkpoint_stop_file!=null&&(!noControls(r.checkpoint_stop_file)||
                    !this.path.isAbsolute(r.checkpoint_stop_file))))||
            new Set(this.state.records.map(r=>r.job_id)).size!==
                this.state.records.length)
            throw Error('Invalid Windows local ownership journal.');
        this.qualifiedAt=null;
    }
    snapshot(){return JSON.parse(JSON.stringify(this.state.records));}
    commit(records) {
        let lock;
        try{lock=this.fs.openSync(this.lock,'wx',0o600);}
        catch{throw Error('Windows local ownership journal locked; inspect the helper before recovery.');}
        try{
            const current=this.fs.existsSync(this.file)
                ?JSON.parse(this.fs.readFileSync(this.file,'utf8'))
                :{version:1,revision:0,records:[]};
            if(current.version!==1||current.revision!==this.state.revision)
                throw Error('Native journal changed concurrently.');
            const next={version:1,revision:this.state.revision+1,records};
            writeAtomic(this.fs,this.file,next,this.crypto);
            this.state=next;
        }finally{
            this.fs.closeSync(lock);
            this.fs.unlinkSync(this.lock);
        }
    }
    verifyHelper() {
        const data=this.fs.readFileSync(this.helperPath);
        if(!data||data.length>200000000)
            throw Error('Installed Windows owner helper is missing or too large.');
        const actual=this.crypto.createHash('sha256').update(data).digest('hex');
        if(actual!==this.helperSha256)
            throw Error('Installed Windows Job Object helper hash changed.');
    }
    async qualify() {
        if(this.qualifiedAt)return {qualified_at:this.qualifiedAt};
        this.verifyHelper();
        const result=await this.run(this.helperPath,['--probe'],
            {windowsHide:true,timeout:20000,maxBuffer:4096});
        let reply;
        try{reply=JSON.parse(result.stdout);}
        catch{throw Error('Windows owner preflight returned invalid data.');}
        if(reply?.passed!==true||reply.cpu_hard_cap_verified!==true||
            reply.memory_limit_verified!==true||
            reply.job_process_assignment_verified!==true)
            throw Error('Windows Job Object CPU/RAM and suspended-assignment preflight failed.');
        if(this.gpuEnabled&&reply.cuda_uuid_selection_supported!==true)
            throw Error('Installed Windows native owner did not prove CUDA UUID selector propagation; rebuild and re-pin the current helper.');
        // The quota denominator must match the Windows kernel's active
        // processors across all groups. A .NET process-affinity count is
        // not sufficient for a system-wide Job Object CPU percentage.
        const hostCpus=this.hostOs.cpus().length;
        if(!Number.isSafeInteger(hostCpus)||hostCpus<1||
            reply.system_logical_processors!==hostCpus)
            throw Error('Windows owner and Node disagree on total system logical processors; native Starts blocked.');
        this.qualifiedAt=new Date().toISOString();
        return {qualified_at:this.qualifiedAt};
    }
    pipeName(id){return '\\\\.\\pipe\\experiment-hub-'+id.replace(/-/g,'');}
    send(record,action) {
        return new Promise((resolve,reject)=>{
            let done=false,buffer='';
            const finish=(error,value)=>{
                if(done)return;
                done=true;
                socket.destroy();
                if(error)reject(error);else resolve(value);
            };
            const socket=this.net.createConnection(this.pipeName(record.job_id));
            socket.setTimeout(3000,()=>finish(Error('Native owner status timed out.')));
            socket.on('error',error=>finish(error));
            socket.on('connect',()=>socket.write(JSON.stringify({
                token:record.token,action,
            })+'\n'));
            socket.on('data',chunk=>{
                buffer+=chunk.toString('utf8');
                if(buffer.length>4096)return finish(Error('Oversized native owner status.'));
                const pos=buffer.indexOf('\n');
                if(pos<0)return;
                try{
                    const value=JSON.parse(buffer.slice(0,pos));
                    if(value.error)return finish(Error('Windows owner refused inspection.'));
                    finish(null,value);
                }catch{return finish(Error('Invalid native owner status.'));}
            });
            socket.on('end',()=>finish(Error('Native owner closed without status.')));
        });
    }
    verifyReceipt(record) {
        const file=this.path.join(this.root,'job-'+record.job_id+'.receipt.json');
        if(!this.fs.existsSync(file))return null;
        const bytes=this.fs.readFileSync(file,'utf8');
        if(typeof bytes!=='string'||bytes.length>2048)
            throw Error('Native terminal receipt oversized.');
        const reply=JSON.parse(bytes);
        if(reply.job_id!==record.job_id||!terminal.has(reply.state)||
            !Number.isSafeInteger(reply.exit_code)||reply.exit_code<0||
            reply.exit_code>4294967295||!SHA.test(String(reply.receipt_hmac)))
            throw Error('Malformed Windows owner terminal receipt.');
        let signed=record.job_id+'\n'+reply.state+'\n'+reply.exit_code;
        const counters={};
        if(reply.receipt_version!==undefined) {
            if(reply.receipt_version!==2)throw Error('Unsupported Windows owner terminal receipt version.');
            for(const key of ['cpu_time_ns','peak_job_memory_bytes']) {
                const value=reply[key];
                if(value!==null&&(!Number.isSafeInteger(value)||value<0))
                    throw Error('Invalid Windows owner terminal counters.');
                counters[key]=value;
            }
            signed+='\n2\n'+(counters.cpu_time_ns??'')+'\n'+(counters.peak_job_memory_bytes??'');
        }
        const expected=this.crypto.createHmac('sha256',
            Buffer.from(record.token,'hex')).update(signed).digest('hex');
        if(!this.crypto.timingSafeEqual(Buffer.from(expected,'hex'),
            Buffer.from(reply.receipt_hmac,'hex')))
            throw Error('Windows owner terminal receipt authentication failed.');
        return {state:reply.state,active:false,owner_verified:true,exit_code:reply.exit_code,...counters};
    }
    verifyReply(record,report) {
        // Match .NET Math.Round's midpoint-to-even rule used by the
        // native Job Object owner. JavaScript Math.round uses midpoint-up.
        const raw=10000*record.cpus/record.host_cpus;
        const floor=Math.floor(raw);
        const rounded=raw-floor===0.5&&floor%2===0?floor:Math.round(raw);
        const expected=Math.max(1,Math.min(10000,rounded));
        if(!report||report.job_id!==record.job_id||
            report.owner_verified!==true||
            report.cpu_rate_units!==expected||
            report.ram_limit_mb!==record.ram_mb||
            !['running','stop_requested','finished','failed','stopped']
                .includes(report.state)||
            typeof report.active!=='boolean'||
            (terminal.has(report.state)&&report.active)||
            (!terminal.has(report.state)&&!report.active))
            throw Error('Native Windows owner identity or hard limits not verified.');
        const integer=k=>Number.isSafeInteger(report[k])&&report[k]>=0
            ?report[k]:null;
        const exitCode=integer('exit_code');
        const cpuSeconds=Number.isFinite(report.cpu_time_seconds)&&
            report.cpu_time_seconds>=0&&report.cpu_time_seconds<=1e12
            ?report.cpu_time_seconds:null;
        return {state:report.state,active:report.active,
            exit_code:terminal.has(report.state)&&exitCode!==null&&exitCode<=4294967295?exitCode:null,
            cpu_time_ns:integer('cpu_time_ns'),
            cpu_time_seconds:cpuSeconds,
            peak_job_memory_bytes:integer('peak_job_memory_bytes'),
            process_tasks:integer('process_tasks')};
    }
    async observe(record) {
        const receipt=this.verifyReceipt(record);
        if(receipt)return receipt;
        try {
            return this.verifyReply(record,await this.send(record,'inspect'));
        }catch(error) {
            // An owner closing its control pipe and atomically publishing
            // a signed terminal receipt are separate events. This affects
            // both requested Stops and natural child completion. The pipe
            // can confirm exit before the receipt is published, so a
            // reopened supervisor may already have a terminal record.
            if(!terminal.has(record.state)&&
                !['launch_requested','running','stop_requested'].includes(record.state))
                throw error;
            for(let attempt=0;attempt<20;attempt++) {
                const completed=this.verifyReceipt(record);
                if(completed)return completed;
                await new Promise(resolve=>setTimeout(resolve,100));
            }
            throw error;
        }
    }
    async reconcile(jobId) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown Windows locally owned job.');
        let observation,issue=null;
        try{observation=await this.observe(record);}
        catch(error){
            observation={state:'unverified',active:false};
            issue=String(error.message||error).slice(0,220);
        }
        const checkpointStop=Boolean(record.stop_requested_at&&record.checkpoint_stop_file);
        if(observation.active&&checkpointStop&&
            !record.quarantined_at) {
            try {
                this.fs.writeFileSync(record.checkpoint_stop_file,'',{flag:'wx',mode:0o600});
            }catch(error) {
                if(error.code!=='EEXIST')issue='Checkpoint Stop delivery pending: '+
                    String(error.message||error).slice(0,180);
            }
        }
        let state=observation.state;
        if(record.quarantined_at)state='unverified';
        else if(record.stop_requested_at) {
            if(observation.active)state='stop_requested';
            else if(terminal.has(state)) {
                if(!checkpointStop&&state!=='failed')state='stopped';
                else if(!observation.owner_verified)state='stop_requested';
                else state=state==='finished'?'stopped':'failed';
            }
        }
        if(state==='failed'&&observation.exit_code!=null)
            issue='Windows workload exited with code '+observation.exit_code+'.';
        const updated={...record,state,diagnostic:issue};
        if(updated.state!==record.state||updated.diagnostic!==record.diagnostic)
            this.commit(this.state.records.map(x=>
                x.job_id===jobId?updated:x));
        return {state,active:observation.active,
            checkpoint_stop_verified:checkpointStop&&state==='stopped'&&
                observation.owner_verified===true&&observation.exit_code===0,
            diagnostic:issue,
            cpu_time_ns:observation.cpu_time_ns??null,
            // Keep the owner's verified decimal-seconds counter when the
            // nanosecond total is no longer a safe JavaScript integer.
            cpu_time_seconds:observation.cpu_time_seconds??null,
            peak_job_memory_bytes:observation.peak_job_memory_bytes??null,
            process_tasks:observation.process_tasks??null};
    }
    async quarantineLaunch(jobId,reason) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('No Windows job ownership ledger to quarantine.');
        if(record.quarantined_at)return;
        // Persist the intent before contacting an owner whose previous
        // response could be missing or invalid. Never send this command
        // again automatically after an ambiguous result.
        const updated={...record,state:'unverified',
            quarantined_at:new Date().toISOString(),
            stop_requested_at:new Date().toISOString(),
            diagnostic:String(reason||'Native owner not verified').slice(0,220)};
        this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        try {
            // Only this same-user, locally token-authorized helper can
            // receive the request. No PID guessing or unrelated process kill.
            await this.send(updated,'stop');
        }catch {
            // An inaccessible owner may still hold the kernel job handle.
            // Keep the claimed capacity and require manual inspection.
        }
    }
    async start(job,lease) {
        if(!this.qualifiedAt)
            throw Error('Windows Job Object preflight must pass before start.');
        this.verifyHelper();
        if(this.state.records.some(x=>x.job_id===job?.id))
            throw Error('Windows job already has a durable local launch record.');
        const invocation=validateInvocation(job,lease,this.machineId,
            this.adapters,this.hostOs,this.path,this.crypto,
            this.gpuEnabled,this.gpuInventory);
        const cpuSlots=Math.min(this.hostOs.cpus().length,
            typeof this.hostOs.availableParallelism==='function'
                ?this.hostOs.availableParallelism():this.hostOs.cpus().length);
        ensureLocalCpuRamCapacity(this.state.records,invocation.cpus,
            invocation.ramMb,cpuSlots,
            Math.floor(this.hostOs.totalmem()/1048576));
        ensureLocalGpuCapacity(this.state.records,invocation.gpu);
        if(invocation.predecessor) {
            const parent=this.state.records.find(r=>
                r.job_id===invocation.predecessor);
            const checkpointStopped=['courier','tinybrains'].includes(parent?.project_id)&&parent.state==='stopped'&&
                parent.checkpoint_stop_file&&parent.stop_requested_at;
            if(!parent||parent.project_id!==job.project_id||
                (parent.state!=='finished'&&!checkpointStopped)||
                (parent.artifact_lineage_root_id||parent.job_id)!==
                    invocation.root)
                throw Error('Windows continuation requires a finished or checkpoint-stopped predecessor in the local journal.');
            const observed=await this.observe(parent);
            if(observed.active||observed.state!=='finished'||(checkpointStopped&&
                (!observed.owner_verified||observed.exit_code!==0)))
                throw Error('Windows predecessor native owner has not verified successful checkpoint completion.');
        }
        if(this.state.records.length>=1000)
            throw Error('Windows native ownership journal full.');
        const token=this.crypto.randomBytes(32).toString('hex');
        const specPath=this.path.join(this.root,'job-'+job.id+'.spec.json');
        const receiptPath=this.path.join(this.root,'job-'+job.id+'.receipt.json');
        if(this.fs.existsSync(specPath)||this.fs.existsSync(receiptPath))
            throw Error('Existing Windows native job artifacts require manual inspection.');
        const spec={jobId:job.id,token,
            executable:invocation.executable,args:invocation.args,
            cwd:invocation.cwd,cpus:invocation.cpus,
            hostCpus:invocation.hostCpus,ramMb:invocation.ramMb,
            ...(invocation.gpu?{gpuUuid:invocation.gpu.cuda_uuid}:{}),
            receiptPath};
        // Persist the exact locally approved job request *before* creating
        // any owner process. A crash leaves an inspectable, non-repeatable job.
        writeAtomic(this.fs,specPath,spec,this.crypto);
        const record={job_id:job.id,lease_id:lease.id,
            project_id:job.project_id,
            continuation_of_job_id:invocation.predecessor,
            artifact_lineage_root_id:invocation.root,
            ...(invocation.stopFile?{checkpoint_stop_file:invocation.stopFile}:{}),
            token,cpus:invocation.cpus,host_cpus:invocation.hostCpus,
            ram_mb:invocation.ramMb,
            ...(invocation.gpu?{gpu_pci_address:invocation.gpu.pci_address,
                gpu_cuda_uuid:invocation.gpu.cuda_uuid,
                gpu_mode:invocation.gpu.mode,
                gpu_vram_mb:invocation.gpu.vram_mb,
                gpu_compute_pct:invocation.gpu.compute_pct}:{}),
            state:'launch_requested',
            requested_at:new Date().toISOString(),diagnostic:null};
        this.commit([...this.state.records,record]);
        try {
            const owner=this.spawnProcess(this.helperPath,['--run',specPath],{
                windowsHide:true,detached:true,stdio:'ignore',
            });
            await new Promise((resolve,reject)=>{
                owner.once('spawn',resolve);
                owner.once('error',reject);
            });
            owner.unref();
        }catch(error){
            // A failed spawn does not prove that the independent owner
            // process was never created. Keep durable ownership evidence.
            const message=String(error.message||error).slice(0,150);
            await this.quarantineLaunch(job.id,message);
            throw Error('Windows owner launch unverified: '+message);
        }
        for(let attempt=0;attempt<6;attempt++) {
            const result=await this.reconcile(job.id);
            if(result.state==='running'||terminal.has(result.state))
                return result;
            await new Promise(resolve=>setTimeout(resolve,200));
        }
        await this.quarantineLaunch(job.id,'Native owner limits or status not verified.');
        throw Error('Windows owner has not confirmed native job limits; quarantined local job requires inspection.');
    }
    async stop(jobId) {
        const record=this.state.records.find(r=>r.job_id===jobId);
        if(!record)throw Error('Unknown Windows locally owned job.');
        const inspected=await this.reconcile(jobId);
        if(!inspected.active)return inspected;
        if(record.stop_requested_at)return inspected;
        const updated={...record,stop_requested_at:new Date().toISOString(),
            state:'stop_requested'};
        this.commit(this.state.records.map(r=>
            r.job_id===jobId?updated:r));
        if(updated.checkpoint_stop_file)return this.reconcile(jobId);
        try{
            this.verifyReply(updated,await this.send(updated,'stop'));
        }catch{
            // A lost reply could mean that termination already happened.
            // Never issue a second TerminateJobObject command automatically.
        }
        // The common inspection path waits for signed owner handoff,
        // whether the child was stopped or finished on its own.
        return this.reconcile(jobId);
    }
}
module.exports={WindowsSupervisor,validateInvocation};
