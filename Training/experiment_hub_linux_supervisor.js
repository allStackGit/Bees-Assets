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
const {inspectHost,availableMemoryMb}=require('./experiment_hub_host');
const {discoverAmdGpus}=require('./experiment_hub_gpu_inventory');
const {matchingLeaseBudget,startHeadroom,ensureLocalCpuRamCapacity}=
    require('./experiment_hub_resources');
const {verifyKernelLimits}=require('./experiment_hub_linux_cgroup');
const {resolveRocmDevices,deviceIdentity}=require('./experiment_hub_linux_gpu_devices');
const runDefault=promisify(execFile);
// Use known system binaries, not the agent's PATH.
const SYSTEMD_RUN='/usr/bin/systemd-run';
const SYSTEMCTL='/usr/bin/systemctl';
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
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
function checkedAdapterArgs(adapter,fn,parameters,context) {
    if(typeof fn!=='function')
        throw Error('Installed native adapter is missing an approved argument builder.');
    const args=fn.call(adapter,JSON.parse(JSON.stringify(parameters)),context);
    if(!Array.isArray(args)||args.length>64||
        args.some(a=>typeof a!=='string'||a.length>1024||!SAFE_ARG.test(a)))
        throw Error('Trusted adapter returned an unsafe invocation.');
    return args;
}
function gpuRuntimeProof(adapter,parameters,context) {
    const pci=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
    const ids=context?.gpu_pci_addresses;
    const dual=Array.isArray(ids)&&ids.length===2&&
        new Set(ids).size===2&&ids.every(p=>pci.test(String(p)))&&
        adapter?.gpu_dual_execution==='split-ppo';
    const multi=Array.isArray(ids)&&ids.length>=2&&ids.length<=8&&
        new Set(ids).size===ids.length&&ids.every(p=>pci.test(String(p)))&&
        adapter?.gpu_multi_execution==='independent-ppo';
    const single=!ids&&pci.test(String(context?.gpu_pci_address||''));
    if(!adapter||typeof adapter.executable!=='string'||
        !path.isAbsolute(adapter.executable)||
        !SAFE_ARG.test(adapter.executable)||
        !(dual||multi||single))
        throw Error('Invalid local ROCm runtime or leased PCI identity.');
    const rocrUuid=multi?ids.map(p=>adapter.rocr_gpu_uuids?.[p])
        :dual?null:Object.hasOwn(adapter.rocr_gpu_uuids||{},context.gpu_pci_address)
            ?adapter.rocr_gpu_uuids[context.gpu_pci_address]:null;
    if(rocrUuid!==null&&
        (Array.isArray(rocrUuid)
            ?rocrUuid.length!==ids.length||
                new Set(rocrUuid).size!==rocrUuid.length||
                rocrUuid.some(uuid=>typeof uuid!=='string'||!ROCM_UUID.test(uuid))
            :typeof rocrUuid!=='string'||!ROCM_UUID.test(rocrUuid)))
        throw Error('Invalid locally approved ROCr GPU UUID selector.');
    // The verifier's inputs must be identical during an unstarted
    // qualification and a later native Start. Dispatch IDs and evaluation
    // states legitimately differ across those phases, but have no bearing
    // on a frozen model profile's physical GPU selection.
    const verifierContext=Object.freeze({
        job_id:context.job_id,project_id:context.project_id,
        artifact_lineage_root_id:context.artifact_lineage_root_id,
        continuation_of_job_id:context.continuation_of_job_id||null,
        ...(dual||multi?{gpu_pci_addresses:ids.slice()}:
            {gpu_pci_address:context.gpu_pci_address}),
    });
    // Every selected GPU is qualified alone while the other GPUs must be
    // denied by the kernel device filter. Only then may the trusted native
    // service use the union of the leased render nodes.
    const singleProofs=multi?ids.map(p=>gpuRuntimeProof(adapter,parameters,{
        ...verifierContext,gpu_pci_addresses:undefined,gpu_pci_address:p
    })):null;
    return {executable:adapter.executable,
        verifyArgs:checkedAdapterArgs(adapter,adapter.gpu_verify_args,
            parameters,verifierContext),
        rocrUuid,...(singleProofs?{singleProofs}:{})};
}
function gpuSelectorArgs(rocrUuid) {
    const selectors=Array.isArray(rocrUuid)?rocrUuid:rocrUuid?[rocrUuid]:[];
    if(selectors.length>8||
        new Set(selectors).size!==selectors.length||
        selectors.some(uuid=>typeof uuid!=='string'||!ROCM_UUID.test(uuid)))
        throw Error('Invalid locally pinned ROCr GPU selector.');
    return [...(selectors.length?['--setenv=ROCR_VISIBLE_DEVICES='+selectors.join(',')]:[]),
        '--property=UnsetEnvironment=CUDA_VISIBLE_DEVICES HIP_VISIBLE_DEVICES'+
            (selectors.length?'':' ROCR_VISIBLE_DEVICES')+' GPU_DEVICE_ORDINAL'];
}
function validateLocalInput(job,lease,machineId,adapters,hostOs=os,gpuEnabled=false) {
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
        lr.cpus!==r.cpus||lr.ram_mb!==r.ram_mb)
        throw Error('Native CPU/RAM request does not match the lease or exceeds the local host limits.');
    if(!matchingLeaseBudget(r,lr))
        throw Error('Native resource budget differs from the acknowledged lease.');
    const adapter=adapters[job.project_id];
    if(!adapter||typeof adapter.args!=='function'||
        typeof adapter.executable!=='string'||
        !path.isAbsolute(adapter.executable)||
        !SAFE_ARG.test(adapter.executable)||
        (adapter.cwd!==undefined&&
            (typeof adapter.cwd!=='string'||!path.isAbsolute(adapter.cwd)||
             !SAFE_ARG.test(adapter.cwd))))
        throw Error('No valid locally installed trusted adapter for this project.');
    const bindings=lease.gpu_bindings||[];
    const binding=bindings[0];
    const cpuOnly=r.gpu_count===0&&lr.gpu_count===0&&
        !(r.gpus||[]).length&&!(lr.gpus||[]).length;
    const sharedGpu=r.gpu_mode==='shared'&&lr.gpu_mode==='shared'&&
        adapter.gpu_sharing==='cooperative-pytorch'&&
        Number.isSafeInteger(r.gpu_vram_mb)&&r.gpu_vram_mb>0&&
        Number.isSafeInteger(r.gpu_compute_pct)&&r.gpu_compute_pct>0&&
        r.gpu_compute_pct<=100;
    const single=r.gpu_count===1&&lr.gpu_count===1&&
        (r.gpu_mode==='exclusive'&&lr.gpu_mode==='exclusive'||sharedGpu);
    const dual=r.gpu_count===2&&lr.gpu_count===2&&
        r.gpu_mode==='exclusive'&&lr.gpu_mode==='exclusive'&&
        job.project_id==='tinybrains'&&job.frozen_parameters?.device==='dual'&&
        adapter.gpu_dual_execution==='split-ppo';
    const multi=r.gpu_count>=2&&r.gpu_count<=8&&
        lr.gpu_count===r.gpu_count&&
        r.gpu_mode==='exclusive'&&lr.gpu_mode==='exclusive'&&
        job.project_id==='courier'&&
        adapter.gpu_multi_execution==='independent-ppo';
    if(!cpuOnly&&(!gpuEnabled||typeof adapter.gpu_verify_args!=='function'||
        !(single||dual||multi)||bindings.length!==r.gpu_count||
        lr.gpus?.length!==r.gpu_count||
        bindings.some((bound,index)=>bound.id!==lr.gpus[index])||
        new Set(bindings.map(bound=>bound.pci_address)).size!==bindings.length||
        (r.gpus?.length&&r.gpus.some((id,index)=>id!==lr.gpus[index]))))
        throw Error('Native GPU launch needs a qualified physical PCI set and project-approved execution mode.');
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
    const context=Object.freeze({
        job_id:job.id,project_id:job.project_id,
        cpu_quota:r.cpus,ram_quota_mb:r.ram_mb,
        dispatch_id:job.dispatch_id||null,evaluation_suite_id:job.evaluation_stop?.suite_id||
            job.evaluation_stop?.all_of?.[0]?.suite_id||null,
        continuation_of_job_id:predecessor,
        artifact_lineage_root_id:lineageRoot,
        ...(job.expected_parent_model_sha256?
            {expected_parent_model_sha256:job.expected_parent_model_sha256}:{}),
        ...(!cpuOnly?(dual||multi
            ?{gpu_pci_addresses:bindings.map(bound=>bound.pci_address),
                gpu_mode:'exclusive'}
            :{gpu_pci_address:binding.pci_address,
                gpu_mode:r.gpu_mode,
                gpu_vram_mb:r.gpu_mode==='shared'?r.gpu_vram_mb:0,
                gpu_compute_pct:r.gpu_mode==='shared'?r.gpu_compute_pct:100,
                gpu_total_vram_mb:binding.vram_mb}):{}),
    });
    const argsFor=fn=>checkedAdapterArgs(adapter,fn,job.frozen_parameters,context);
    const runtimeProof=cpuOnly?null:gpuRuntimeProof(adapter,job.frozen_parameters,context);
    const stopFile=adapter.checkpoint_stop_file?.(context)??null;
    if(stopFile!==null&&(stopPolicy!=='interrupt-main'||
        typeof stopFile!=='string'||!path.isAbsolute(stopFile)||
        !SAFE_ARG.test(stopFile)))
        throw Error('Cooperative checkpoint Stop requires an absolute trusted Linux marker path and interrupt-capable fallback.');
    return {executable:adapter.executable,args:argsFor(adapter.args),cwd:adapter.cwd||null,
        cpus:r.cpus,ram_mb:r.ram_mb,stop_policy:stopPolicy,
        stopFile,
        gpu:cpuOnly?null:{...(single?{binding}:{}),
            bindings:bindings.slice(),...runtimeProof}};
}
function verifyUnconfinedDeviceAccess(plan,fsApi) {
    for(const device of [plan.kfd,...plan.selected,...plan.unselected]) {
        let fd;
        try{fd=fsApi.openSync(device.node,'r+');}
        catch{throw Error('GPU probe baseline cannot open device '+device.node);}
        finally{if(fd!==undefined)fsApi.closeSync(fd);}
    }
}
const terminalStates=new Set(['finished','stopped','failed']);
const numeric=value=>/^[0-9]+$/.test(value||'')&&
    Number.isSafeInteger(Number(value))?Number(value):null;
// Decimal nanosecond uptime remains measurable as diagnostic seconds after
// total CPU nanoseconds exceed the safe integer range.
const cpuTimeSeconds=value=>{
    if(!/^[0-9]+$/.test(value||''))return null;
    const seconds=Number(value)/1e9;
    return Number.isFinite(seconds)&&seconds<=1e12?seconds:null;
};

function terminalResult(record,result,exitStatus) {
    const expectedInterrupt=record.stop_requested_at&&
        record.stop_policy==='interrupt-main'&&result==='exit-code'&&exitStatus==='130';
    if(result==='success'||expectedInterrupt)return {state:'finished'};
    const reason=/^[a-z][a-z-]{0,31}$/.test(result||'')?result:'unknown';
    const status=/^[0-9A-Z]{1,32}$/.test(exitStatus||'')?exitStatus:'unknown';
    return {state:'failed',
        diagnostic:'Linux workload failed (systemd result: '+reason+', main status: '+status+').'};
}
function checkpointStopVerified(record,receipt) {
    return Boolean(['courier','tinybrains'].includes(record.project_id)&&record.stop_requested_at&&
        record.stop_policy==='interrupt-main'&&receipt?.result==='success'&&
        receipt.exit_code==='exited'&&receipt.exit_status==='0');
}
function launchArguments(record,invocation,gpuPlan) {
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
        '--property=CPUAccounting=yes','--property=MemoryAccounting=yes'];
    if(gpuPlan) {
        const {bindings,verifyArgs,rocrUuid}=invocation.gpu;
        const check=path.join(__dirname,'experiment_hub_linux_gpu_launch_check.js');
        if(!SAFE_ARG.test(process.execPath)||!SAFE_ARG.test(check))
            throw Error('GPU pre-start verifier requires unambiguous local executable paths.');
        const physical=bindings.length===1
            ?[bindings[0].id,bindings[0].pci_address,bindings[0].vram_mb]
            :['--multi',bindings.length,...bindings.flatMap(binding=>
                [binding.id,binding.pci_address,binding.vram_mb])];
        args.push(...gpuPlan.device_properties.map(p=>'--property='+p),
            '--property=TimeoutStartSec='+(bindings.length>1?'45s':'25s'),
            ...gpuSelectorArgs(rocrUuid),
            '--property=ExecStartPre='+[process.execPath,check,...physical,
                deviceIdentity(gpuPlan),invocation.executable,...verifyArgs].join(' '));
    }else {
        // CPU adapter masks preserve existing selector behavior.
        args.push('--setenv=CUDA_VISIBLE_DEVICES=','--setenv=HIP_VISIBLE_DEVICES=',
            '--setenv=ROCR_VISIBLE_DEVICES=','--setenv=GPU_DEVICE_ORDINAL=');
    }
    if(record.receipt_path) {
        const command=[process.execPath,path.join(__dirname,'experiment_hub_linux_terminal.js'),
            record.receipt_path,record.job_id,record.lease_id];
        if(command.some(arg=>/["\\%$\x00-\x1f]/.test(arg)))
            throw Error('Linux terminal receipt requires unambiguous local paths.');
        args.push('--property=ExecStopPost='+command.map(arg=>'"'+arg+'"').join(' '));
    }
    if(invocation.cwd)args.push('--working-directory='+invocation.cwd);
    // This is a systemd-run option boundary, not a shell.
    return args.concat('--',invocation.executable,...invocation.args);
}
function writeAtomic(file,data,fsApi=fs) {
    const pending=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    try{
        fsApi.writeFileSync(pending,JSON.stringify(data,null,2)+'\n',
            {flag:'wx',mode:0o600});
        // The durable owner journal is recorded before asking systemd to
        // launch a native trainer. Flush data and the renamed directory
        // entry before publishing its new in-memory revision.
        const fd=fsApi.openSync(pending,'r');
        try{fsApi.fsyncSync(fd);}
        finally{fsApi.closeSync(fd);}
        fsApi.renameSync(pending,file);
        const dirFd=fsApi.openSync(path.dirname(file),'r');
        try{fsApi.fsyncSync(dirFd);}
        finally{fsApi.closeSync(dirFd);}
    }finally{
        if(fsApi.existsSync(pending))fsApi.unlinkSync(pending);
    }
}
class LinuxSupervisor {
    constructor({root,machineId,adapters={},runner=runDefault,fsApi=fs,
        hostOs=os,hostInspector=inspectHost,gpuEnabled=false}={}) {
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
        this.gpuEnabled=gpuEnabled===true;
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
                r.unit!==unitFor(r.job_id)||
                (r.checkpoint_stop_file!=null&&
                    (r.stop_policy!=='interrupt-main'||
                        typeof r.checkpoint_stop_file!=='string'||
                        !path.isAbsolute(r.checkpoint_stop_file)||
                        !SAFE_ARG.test(r.checkpoint_stop_file))))||
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

    // On-demand GPU qualification: the fixed device probe can optionally
    // run the trusted adapter's bounded HIP identity check, but never trains.
    gpuProbeCleanupPending() {
        return this.gpuPreflightCleanupUnconfirmed===true||
            this.fs.existsSync(this.gpuProbePending);
    }

    async qualifyGpuDeviceAccess(bindings,runtimeProof=null) {
        if(!this.qualifiedAt)
            throw Error('CPU/RAM preflight must pass before GPU device checks.');
        if(this.gpuProbeCleanupPending())
            throw Error('Previous GPU access probe cleanup unconfirmed; inspect the recorded local service before retry.');
        if(Array.isArray(bindings)&&bindings.length>1) {
            // A union of N GPUs can have no unselected render nodes on a
            // two-GPU host. Prove kernel allow/deny and HIP PCI identity
            // for *each* GPU separately before authorizing the union.
            if(bindings.length>8||!runtimeProof||
                !Array.isArray(runtimeProof.singleProofs)||
                runtimeProof.singleProofs.length!==bindings.length||
                !Number.isSafeInteger(runtimeProof.ram_mb))
                throw Error('Multi-GPU preflight requires one trusted physical proof per GPU.');
            for(let i=0;i<bindings.length;i++) {
                const result=await this.qualifyGpuDeviceAccess([bindings[i]],{
                    ...runtimeProof.singleProofs[i],ram_mb:runtimeProof.ram_mb});
                if(result.runtime_pci_verified!==true||
                    result.selected.length!==1||
                    result.selected[0]!==bindings[i].pci_address)
                    throw Error('An individual leased GPU did not pass HIP device isolation.');
            }
            const full=resolveRocmDevices(bindings,{fsApi:this.fs});
            if(full.selected.length!==bindings.length||
                full.selected.some((g,i)=>g.pci_address!==bindings[i].pci_address))
                throw Error('Combined native GPU set changed after isolation qualification.');
            return {qualified_at:new Date().toISOString(),
                selected:bindings.map(b=>b.pci_address),
                runtime_pci_verified:true};
        }
        // A real native Start would create a durable ownership journal. Do
        // the same tiny HIP runtime attestation here *before* that Start,
        // within the short-lived, device-restricted qualification service.
        // The executable and arguments come only from the locally installed
        // project adapter, never from the Hub or experiment JSON.
        if(runtimeProof!==null&&(
            !runtimeProof||typeof runtimeProof.executable!=='string'||
            !path.isAbsolute(runtimeProof.executable)||
            !SAFE_ARG.test(runtimeProof.executable)||
            !Array.isArray(runtimeProof.verifyArgs)||
            runtimeProof.verifyArgs.length>64||
            runtimeProof.verifyArgs.some(a=>typeof a!=='string'||
                a.length>1024||!SAFE_ARG.test(a))||
            !Number.isSafeInteger(runtimeProof.ram_mb)||
            runtimeProof.ram_mb<128||runtimeProof.ram_mb>1000000))
            throw Error('Invalid trusted Linux GPU runtime qualification request.');
        const proofArgs=runtimeProof===null?[]:gpuSelectorArgs(runtimeProof.rocrUuid);
        const plan=resolveRocmDevices(bindings,{fsApi:this.fs});
        // A proof of denial requires a second GPU render node that the same
        // unconfined account can open. Do not mistake a UNIX ACL failure for
        // a working eBPF device filter.
        if(!plan.unselected.length||plan.selected.length!==1)
            throw Error('GPU access qualification needs one selected and at least one excluded AMD render node.');
        const denied=plan.unselected.map(device=>device.node).sort();
        verifyUnconfinedDeviceAccess(plan,this.fs);
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
            '--property=MemoryMax='+(runtimeProof===null?268435456:
                Math.min(runtimeProof.ram_mb,4096)*1048576),
            // Bound the native service even if the agent or systemd-run exits.
            '--property=TimeoutStartSec='+(runtimeProof===null?'5s':'25s'),
            '--property=RuntimeMaxSec='+(runtimeProof===null?'10s':'35s'),
            '--property=TimeoutStopSec=3s',
            '--property=KillMode=control-group',
            ...plan.device_properties.map(p=>'--property='+p),
            ...proofArgs,
            '--',process.execPath,
            ...(runtimeProof===null
                ?[path.join(__dirname,'experiment_hub_linux_gpu_probe.js'),
                    plan.kfd.node,plan.selected[0].node,...denied]
                :[path.join(__dirname,'experiment_hub_linux_gpu_launch_check.js'),
                    bindings[0].id,bindings[0].pci_address,bindings[0].vram_mb,
                    deviceIdentity(plan),runtimeProof.executable,...runtimeProof.verifyArgs])];
        let failure=null;
        try {
            const response=await this.runner(SYSTEMD_RUN,args,
                {timeout:runtimeProof===null?20000:45000,
                    maxBuffer:8192,windowsHide:true});
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
            if(runtimeProof!==null&&
                (!Array.isArray(actual.pci_addresses)||
                    actual.pci_addresses.length!==1||
                    actual.pci_addresses[0]!==bindings[0].pci_address))
                throw Error('Linux GPU runtime preflight did not attest the leased PCI device.');
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
        if(deviceIdentity(plan)!==deviceIdentity(observed))
            throw Error('GPU device identity changed during the access probe.');
        return {qualified_at:new Date().toISOString(),
            selected:plan.selected.map(g=>g.pci_address),
            runtime_pci_verified:runtimeProof!==null};
    }

    terminalAfterUnload(record) {
        const absent={state:'unverified',active:false,unit_exists:false};
        if(record.native_terminal_at&&terminalStates.has(record.state))
            return {...absent,state:record.state,cpu_time_ns:record.terminal_cpu_time_ns??null,
                cpu_time_seconds:record.terminal_cpu_time_seconds??null,
                diagnostic:record.diagnostic||null};
        const receipt=this.readTerminalReceipt(record);
        if(!receipt)return absent;
        return {...absent,...terminalResult(record,receipt.result,receipt.exit_status),
            checkpoint_stop_verified:checkpointStopVerified(record,receipt)};
    }
    readTerminalReceipt(record) {
        if(!record.native_verified_at||!record.receipt_path||record.quarantined_at)
            return null;
        let fd;
        try {
            fd=this.fs.openSync(record.receipt_path,
                fs.constants.O_RDONLY|fs.constants.O_NONBLOCK|fs.constants.O_NOFOLLOW);
            const stat=this.fs.fstatSync(fd);
            if(!stat.isFile()||stat.size<1||stat.size>2048)
                throw Error('Invalid Linux terminal receipt file.');
            const bytes=Buffer.alloc(2049);
            const count=this.fs.readSync(fd,bytes,0,bytes.length,0);
            if(count!==stat.size)throw Error('Linux terminal receipt changed during read.');
            const receipt=JSON.parse(bytes.toString('utf8',0,count));
            if(receipt.version!==1||receipt.job_id!==record.job_id||
                receipt.lease_id!==record.lease_id||
                typeof receipt.result!=='string'||!/^[a-z][a-z-]{0,31}$/.test(receipt.result)||
                !['exited','killed','dumped'].includes(receipt.exit_code)||
                !/^[0-9A-Z]{1,32}$/.test(receipt.exit_status))
                throw Error('Linux terminal receipt does not match its native job.');
            return receipt;
        }catch(error){
            if(error.code==='ENOENT'&&fd===undefined)return null;
            throw error;
        }finally{
            if(fd!==undefined)this.fs.closeSync(fd);
        }
    }
    async inspectUnit(record,{requireKernel=true}={}) {
        const out=await this.runner(SYSTEMCTL,
            ['--user','show',record.unit,'--no-pager',
                '--property=Id,Description,LoadState,ActiveState,SubState,Result,MainPID,MemoryMax,CPUQuotaPerSecUSec,MemoryCurrent,CPUUsageNSec,TasksCurrent,KillMode,KillSignal,ExecMainStatus,ControlGroup'],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        const p=parseShow(out.stdout);
        if(p.LoadState==='not-found')
            return this.terminalAfterUnload(record);
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
            // systemctl stop --no-block can leave a loaded unit in its
            // teardown phase after the verified main PID has exited.
            // Only a previously kernel-verified service with a *durable*
            // Stop intent may wait for terminal evidence without a PID.
            // A missing PID during normal running remains unverified.
            if(record.stop_requested_at&&record.native_verified_at&&
                p.MainPID==='0') {
                return {state:'stop_requested',active:false,unit_exists:true,
                    pid:null,cpu_time_ns:numeric(p.CPUUsageNSec),
                    cpu_time_seconds:cpuTimeSeconds(p.CPUUsageNSec)};
            }
            const kernel=requireKernel
                ?verifyKernelLimits(p,record.cpus,record.ram_mb,this.fs):null;
            const memoryBytes=numeric(p.MemoryCurrent);
            return {state:'running',active:true,unit_exists:true,
                kernel_limits_verified:kernel?.verified===true,
                cpu_throttled_seconds:kernel?.cpu_throttled_seconds??null,
                cpu_throttle_events:kernel?.cpu_throttle_events??null,
                pid:numeric(p.MainPID)||null,
                memory_current_mb:memoryBytes==null?null:
                    Math.round(memoryBytes/1048576),
                cpu_time_ns:numeric(p.CPUUsageNSec),
                    cpu_time_seconds:cpuTimeSeconds(p.CPUUsageNSec),
                process_tasks:numeric(p.TasksCurrent)};
        }
        if(p.ActiveState==='inactive'||p.ActiveState==='failed') {
            const result=terminalResult(record,p.Result,p.ExecMainStatus);
            const receipt=['courier','tinybrains'].includes(record.project_id)&&record.stop_requested_at&&
                result.state==='finished'?this.readTerminalReceipt(record):null;
            return {...result,checkpoint_stop_verified:checkpointStopVerified(record,receipt),
                active:false,unit_exists:true,pid:null,cpu_time_ns:numeric(p.CPUUsageNSec),
                    cpu_time_seconds:cpuTimeSeconds(p.CPUUsageNSec)};
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
        // The trainer owns checkpoint boundaries. Once a Stop is durable,
        // deliver its fixed marker only while the exact systemd unit is
        // identified and running. A newly degraded kernel-limit observation
        // must not strand a requested cooperative Stop; service identity and
        // the configured CPU/RAM properties were still verified by inspectUnit.
        // Reconcile can repeat delivery without re-signalling or relaunching.
        let markerIssue=null;
        if(record.stop_requested_at&&record.checkpoint_stop_file&&
            !record.quarantined_at&&result.active&&
            result.unit_exists===true) {
            try {
                this.fs.writeFileSync(record.checkpoint_stop_file,'',
                    {flag:'wx',mode:0o600});
            }catch(error) {
                if(error.code!=='EEXIST')
                    markerIssue='Checkpoint Stop delivery pending: '+
                        String(error.message||error).slice(0,175);
            }
        }
        const state=record.quarantined_at?'unverified'
            :record.stop_requested_at&&result.active?'stop_requested'
            :record.stop_requested_at&&!result.active&&
                result.state==='finished'?'stopped':result.state;
        const updated={...record,state,
            observed_at:new Date().toISOString(),
            diagnostic:markerIssue||result.diagnostic||null};
        if(state==='stopped'&&result.checkpoint_stop_verified===true)
            updated.checkpoint_stop_verified=true;
        if(!record.native_verified_at&&result.active&&result.kernel_limits_verified)
            updated.native_verified_at=updated.observed_at;
        if(!record.native_terminal_at&&terminalStates.has(state)) {
            updated.native_terminal_at=updated.observed_at;
            updated.terminal_cpu_time_ns=result.cpu_time_ns??null;
            updated.terminal_cpu_time_seconds=result.cpu_time_seconds??null;
        }
        if(record.state!==updated.state||
            record.native_verified_at!==updated.native_verified_at||
            record.native_terminal_at!==updated.native_terminal_at||
            record.checkpoint_stop_verified!==updated.checkpoint_stop_verified||
            (record.diagnostic||null)!==updated.diagnostic)
            this.commit(this.state.records.map(r=>r.job_id===jobId?updated:r));
        return {...updated,active:result.active,
            pid:result.pid??null,
            memory_current_mb:result.memory_current_mb??null,
            cpu_time_ns:result.cpu_time_ns??
                (terminalStates.has(state)?updated.terminal_cpu_time_ns??null:null),
            // Preserve the decimal seconds path even when total nanoseconds
            // exceed JavaScript's safe integer range, including after unload.
            cpu_time_seconds:result.cpu_time_seconds??
                (terminalStates.has(state)?updated.terminal_cpu_time_seconds??null:null),
            cpu_throttled_seconds:result.cpu_throttled_seconds??null,
            cpu_throttle_events:result.cpu_throttle_events??null,
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
            this.adapters,this.hostOs,this.gpuEnabled);
        if(job.resources?.gpu_count===2)
            throw Error('Two-GPU TinyBrains needs the locally qualified Docker backend.');
        const cpuSlots=typeof this.hostOs.availableParallelism==='function'
            ?this.hostOs.availableParallelism():this.hostOs.cpus().length;
        ensureLocalCpuRamCapacity(this.state.records,invocation.cpus,
            invocation.ram_mb,cpuSlots,
            Math.floor(this.hostOs.totalmem()/1048576));
        const availableRam=availableMemoryMb(this.hostOs,this.fs);
        const ramBlock=startHeadroom(job.resources,availableRam);
        if(ramBlock)throw Error(ramBlock);
        if(job.continuation_of_job_id) {
            const parent=this.state.records.find(r=>
                r.job_id===job.continuation_of_job_id);
            if(!parent||parent.project_id!==job.project_id||
                (parent.state!=='finished'&&!(parent.state==='stopped'&&
                    ['courier','tinybrains'].includes(parent.project_id)&&parent.checkpoint_stop_verified===true))||
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
        const gpuPlan=invocation.gpu
            ?resolveRocmDevices([invocation.gpu.binding],{fsApi:this.fs}):null;
        if(gpuPlan&&(!gpuPlan.unselected.length||this.fs.existsSync(this.gpuProbePending)||
            this.state.records.some(r=>r.gpu_pci_address===invocation.gpu.binding.pci_address&&
                !['finished','stopped','failed'].includes(r.state))))
            throw Error('GPU launch requires excluded-device evidence and unambiguous local PCI ownership.');
        if(gpuPlan)verifyUnconfinedDeviceAccess(gpuPlan,this.fs);
        if(gpuPlan) {
            const physical=discoverAmdGpus({fsApi:this.fs}).find(device=>
                device.pci_address===invocation.gpu.binding.pci_address);
            const gpuBlock=startHeadroom(job.resources,availableRam,physical);
            if(gpuBlock)throw Error(gpuBlock);
        }
        const record={
            job_id:job.id,lease_id:lease.id,project_id:job.project_id,
            ...(gpuPlan?{gpu_pci_address:invocation.gpu.binding.pci_address}:{}),
            continuation_of_job_id:job.continuation_of_job_id||null,
            artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
            unit:unitFor(job.id),state:'launch_requested',
            cpus:invocation.cpus,ram_mb:invocation.ram_mb,
            stop_policy:invocation.stop_policy,
            ...(invocation.stopFile?{checkpoint_stop_file:invocation.stopFile}:{}),
            requested_at:new Date().toISOString(),observed_at:null,diagnostic:null,
        };
        record.receipt_path=path.resolve(this.root,'job-'+job.id+'.linux-receipt.json');
        if(this.fs.existsSync(record.receipt_path))
            throw Error('A terminal receipt already exists for this job identity.');
        const args=launchArguments(record,invocation,gpuPlan);
        this.commit(this.state.records.concat(record));
        try{
            await this.runner(SYSTEMD_RUN,args,
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
        // A checkpoint-aware adapter receives a STOP marker instead of a
        // systemd SIGINT. Its normal successful exit is verified by the
        // existing signed-by-service terminal receipt and adapter checkpoint
        // contract. A marker failure remains pending for reconciliation.
        if(updated.checkpoint_stop_file)return this.reconcile(jobId);
        await this.runner(SYSTEMCTL,['--user','stop','--no-block',record.unit],
            {timeout:12000,maxBuffer:16384,windowsHide:true});
        return this.reconcile(jobId);
    }
}
module.exports={LinuxSupervisor,validateLocalInput,gpuRuntimeProof,
    unitFor,launchArguments,parseShow,quotaUsec};
