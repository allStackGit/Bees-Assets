'use strict';

// Machine-local bridge from authenticated, machine-bound job directives to
// a separately installed trusted adapter. This module has no HTTP listener.
const os=require('node:os');
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {LinuxSupervisor,gpuRuntimeProof}=require('./experiment_hub_linux_supervisor');
const {DockerSupervisor}=require('./experiment_hub_docker_supervisor');
const {MixedLinuxSupervisor}=require('./experiment_hub_mixed_linux_supervisor');
const {WindowsSupervisor}=require('./experiment_hub_windows_supervisor');
const {inspectHost}=require('./experiment_hub_host');
const {matchingLeaseBudget}=require('./experiment_hub_resources');
const {validateProgress}=require('./experiment_hub_progress');
const {evaluationEvidence,evaluationRules,suiteIds,supportsEvaluation}=
    require('./experiment_hub_evaluation');
const ID=/^[a-z][a-z0-9_-]{0,47}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const dockerCapable=supervisor=>supervisor instanceof DockerSupervisor||
    supervisor?.dockerSupervisor instanceof DockerSupervisor;

class LocalExecution {
    constructor({machineId,supervisor,projects}) {
        if(!ID.test(String(machineId||''))||!supervisor||
            !Array.isArray(projects)||
            projects.some(p=>!ID.test(p)||p==='bees')||
            new Set(projects).size!==projects.length)
            throw Error('Invalid machine-local execution adapter configuration.');
        this.machineId=machineId;
        this.supervisor=supervisor;
        this.projects=projects.slice();
        this.evaluationSuites=Object.fromEntries(projects.flatMap(project=>{
            const adapter=supervisor.adapters?.[project];
            if(adapter?.evaluation_suites==null)return [];
            if(typeof adapter.readEvaluation!=='function')
                throw Error('Advertised evaluation suites require a local reader.');
            return [[project,suiteIds(adapter.evaluation_suites)]];
        }));
    }
    advertisement(){
        return {local_executor:true,installed_projects:this.projects.slice(),
            evaluation_suites:structuredClone(this.evaluationSuites),
            tinybrains_profiles:{...this.supervisor.adapters?.tinybrains?.tinybrains_profiles},
            tinybrains_cuda_profiles:[...(this.supervisor.adapters?.tinybrains?.tinybrains_cuda_profiles||[])],
            tinybrains_thread_overrides:[...(this.supervisor.adapters?.tinybrains?.tinybrains_thread_overrides||[])],
            tinybrains_cpu_fallback_profiles:[...(this.supervisor.adapters?.tinybrains?.tinybrains_cpu_fallback_profiles||[])],
            tinybrains_required_import_profiles:[...(this.supervisor.adapters?.tinybrains?.tinybrains_required_import_profiles||[])],
            tinybrains_remote_rollout_profiles:[...(this.supervisor.adapters?.tinybrains?.tinybrains_remote_rollout_profiles||[])],
            tinybrains_remote_rollout_count:this.supervisor.adapters?.tinybrains?.tinybrains_remote_rollout_count||0,
            tinybrains_remote_rollout_endpoints:[...(this.supervisor.adapters?.tinybrains?.tinybrains_remote_rollout_endpoints||[])],
            tinybrains_remote_key_fingerprint:this.supervisor.adapters?.tinybrains?.tinybrains_remote_key_fingerprint||null,
            tinybrains_rollout_endpoint:this.supervisor.adapters?.tinybrains_rollout?.tinybrains_rollout_endpoint||null,
            tinybrains_rollout_key_fingerprint:this.supervisor.adapters?.tinybrains_rollout?.tinybrains_rollout_key_fingerprint||null,
            tinybrains_rollout_thread_limit:this.supervisor.adapters?.tinybrains_rollout?.tinybrains_rollout_thread_limit||0,
            tinybrains_dual_gpu_profiles:this.supervisor.gpuEnabled===true&&
                dockerCapable(this.supervisor)&&
                this.supervisor.adapters?.tinybrains?.gpu_dual_execution==='split-ppo'
                ?[...(this.supervisor.adapters.tinybrains.tinybrains_dual_gpu_profiles||[])]:[],
            tinybrains_imports:{...this.supervisor.adapters?.tinybrains?.tinybrains_imports},
            gpu_projects:this.projects.filter(project=>
                this.supervisor.gpuEnabled===true&&
                (!this.supervisor.gpuProjects||this.supervisor.gpuProjects.includes(project))&&
                (this.supervisor.gpuExecution==='cuda'
                    ?this.supervisor.adapters?.[project]?.gpu_execution==='cuda'
                    :typeof this.supervisor.adapters?.[project]?.gpu_verify_args==='function')),
            gpu_shared_projects:this.projects.filter(project=>
                this.supervisor.gpuEnabled===true&&
                this.supervisor.adapters?.[project]?.gpu_execution==='cuda'&&
                this.supervisor.adapters?.[project]?.gpu_sharing==='cooperative-pytorch'&&
                (this.supervisor.gpuExecution==='cuda'||dockerCapable(this.supervisor)))};
    }
    async handle(order) {
        if(!order||!['start','stop','inspect','qualify_gpu'].includes(order.action)||
            !UUID.test(String(order.dispatch_id||''))||
            !order.job||!order.lease||
            !UUID.test(String(order.job.id||''))||
            !UUID.test(String(order.lease.id||''))||
            order.job.lease_id!==order.lease.id||
            order.lease.job_id!==order.job.id||
            order.lease.machine_id!==this.machineId||
            order.lease.project_id!==order.job.project_id||
            !ID.test(String(order.job.project_id||''))||order.job.project_id==='bees'||
            order.lease.issued_by!=='experiment-hub'||
            !(order.lease.status==='acknowledged'||order.action==='inspect'&&order.lease.status==='released'))
            throw Error('Rejected invalid or unauthorized local dispatch instruction.');
        const {job,lease}=order;
        if(order.action==='start'&&!this.projects.includes(job.project_id))
            throw Error('A locally installed project adapter is required for Start.');
        const localAdapter=this.supervisor.adapters?.[job.project_id];
        if(order.action==='start'&&!supportsEvaluation(this.evaluationSuites,job))
            throw Error('Local adapter does not support the requested evaluation suite.');
        if(order.action==='qualify_gpu') {
            // Report local qualification failures for authenticated orders.
            // A small, bounded HIP probe is permitted, never a trainer Start.
            // Native ownership must be ruled out before a rejection can
            // confirm that the coordinator may release the claim.
            let jobUnowned=false;
            try {
                jobUnowned=!this.supervisor.snapshot().some(r=>r.job_id===job.id);
                if(!jobUnowned)
                    throw Error('GPU access probe refused for an already owned native service.');
                if(!this.projects.includes(job.project_id))
                    throw Error('A locally installed project adapter is required for GPU qualification.');
                if(!matchingLeaseBudget(job.resources,lease.resources))
                    throw Error('GPU access probe resource budget differs from its lease.');
                if(!job.frozen_parameters||typeof job.frozen_parameters!=='object'||
                    Array.isArray(job.frozen_parameters)||
                    crypto.createHash('sha256')
                        .update(JSON.stringify(job.frozen_parameters)).digest('hex')!==
                            job.configuration_sha256)
                    throw Error('GPU access probe frozen configuration fingerprint mismatch.');
                const count=job.resources?.gpu_count;
                const dual=count===2&&
                    dockerCapable(this.supervisor)&&
                    localAdapter?.gpu_dual_execution==='split-ppo'&&
                    job.frozen_parameters?.device==='dual'&&
                    job.resources?.gpu_mode==='exclusive';
                const single=count===1;
                if(typeof this.supervisor.qualifyGpuDeviceAccess!=='function'||
                    !this.supervisor.qualifiedAt||
                    job.status!=='waiting_adapter'||
                    !(dual||single&&(job.resources?.gpu_mode==='exclusive'||
                        job.resources?.gpu_mode==='shared'&&
                        dockerCapable(this.supervisor)&&
                        localAdapter?.gpu_sharing==='cooperative-pytorch'))||
                    lease.resources?.gpu_mode!==job.resources?.gpu_mode||
                    lease.resources?.gpu_count!==count||
                    lease.status!=='acknowledged'||lease.receipt_fresh!==true||
                    !Array.isArray(lease.gpu_bindings)||
                    lease.gpu_bindings.length!==count||
                    lease.gpu_bindings.some((bound,i)=>
                        bound.id!==lease.resources.gpus?.[i]||
                        (job.resources.gpus?.length>0&&bound.id!==job.resources.gpus[i])))
                    throw Error('GPU access probe requires a qualified frozen one- or two-GPU Linux lease.');
                // The bounded device-filter service verifies both allowed /
                // denied render nodes and the actual installed HIP runtime.
                // Read-only verifier construction must not create the
                // trainer's output directory or start the training engine.
                const runtime=gpuRuntimeProof(localAdapter,job.frozen_parameters,
                    Object.freeze({
                        job_id:job.id,project_id:job.project_id,
                        artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                        continuation_of_job_id:job.continuation_of_job_id||null,
                        ...(dual
                            ?{gpu_pci_addresses:lease.gpu_bindings.map(b=>b.pci_address)}
                            :{gpu_pci_address:lease.gpu_bindings[0].pci_address}),
                    }));
                const result=await this.supervisor.qualifyGpuDeviceAccess(
                    lease.gpu_bindings,{...runtime,ram_mb:job.resources.ram_mb,
                        // Select the already installed physical owner.
                        // The project comes from the authenticated leased
                        // job, never from the GPU probe's executable path.
                        project_id:job.project_id});
                if(!Array.isArray(result?.selected)||
                    result.selected.length!==count||
                    result.selected.some((pci,i)=>
                        pci!==lease.gpu_bindings[i].pci_address)||
                    result.runtime_pci_verified!==true)
                    throw Error('GPU preflight did not verify all leased HIP runtime PCI devices.');
                return {kind:'gpu_qualification',
                    job_id:job.id,lease_id:lease.id,
                    dispatch_id:order.dispatch_id,
                    result:'verified',
                    selected_pci_addresses:result.selected};
            }catch(error) {
                return {kind:'gpu_qualification',
                    job_id:job.id,lease_id:lease.id,
                    dispatch_id:order.dispatch_id,
                    result:'rejected',selected_pci_addresses:[],
                    cleanup_verified:jobUnowned&&this.supervisor.gpuProbeCleanupPending?.()===false,
                    diagnostic:String(error.message||error).slice(0,160)};
            }
        }
        let observed,diagnostic=null;
        try {
            const record=this.supervisor.snapshot().find(r=>r.job_id===job.id);
            if(record&&(record.lease_id!==lease.id||
                record.project_id!==job.project_id))
                throw Error('Existing locally supervised service has different lease ownership.');
            if(order.action==='start') {
                // An ambiguous, previously issued launch is reconciled, never
                // launched again, including after agent/coordinator restarts.
                observed=record
                    ?await this.supervisor.reconcile(job.id)
                    :await this.supervisor.start({...job,dispatch_id:order.dispatch_id},lease);
            }else if(!record) {
                // Only the Docker backend can verify an absent deterministic
                // container after excluding a concurrent journal/GPU probe.
                // Native Linux and Windows keep missing ownership unverified.
                if(order.action==='inspect'&&
                    typeof this.supervisor.reconcileAbsent==='function')
                    observed=await this.supervisor.reconcileAbsent(job.id,job.project_id);
                else {
                    observed={state:'unverified'};
                    diagnostic='Missing local ownership record; inspect the native supervisor before recovery.';
                }
            }else if(order.action==='stop') {
                observed=await this.supervisor.stop(job.id);
            }else {
                observed=await this.supervisor.reconcile(job.id);
            }
        }catch(error) {
            diagnostic=String(error.message||error).slice(0,220);
            const record=this.supervisor.snapshot().find(r=>r.job_id===job.id);
            if(record&&record.lease_id===lease.id&&record.project_id===job.project_id) {
                try{observed=await this.supervisor.reconcile(job.id);}
                catch{observed=null;}
            }else if(!record&&order.action==='start'&&
                typeof this.supervisor.reconcileAbsent==='function') {
                // Preserve the actual pre-launch rejection when Docker proves
                // that no owned container could have started.
                try{observed=await this.supervisor.reconcileAbsent(job.id,job.project_id);}
                catch{observed=null;}
            }
            if(!observed)observed={state:'unverified'};
        }
        const state=['launch_requested','running','stop_requested','finished','failed','stopped']
            .includes(observed.state)?observed.state:'unverified';
        if(!diagnostic && typeof observed.diagnostic==='string')
            diagnostic=observed.diagnostic.slice(0,220);
        const report={
            job_id:job.id,lease_id:lease.id,
            dispatch_id:order.dispatch_id,state,
            ...(state==='failed'&&observed.prelaunch_absence_verified===true
                ?{prelaunch_absence_verified:true}:{}),
            ...(diagnostic?{diagnostic}:{}),
            ...(Number.isSafeInteger(observed.memory_current_mb)&&
                observed.memory_current_mb>=0
                ?{memory_current_mb:observed.memory_current_mb}:{}),
            ...(Number.isSafeInteger(observed.cpu_time_ns)&&
                observed.cpu_time_ns>=0
                ?{cpu_time_ns:observed.cpu_time_ns}:{}),
            ...(Number.isFinite(observed.cpu_time_seconds)&&
                observed.cpu_time_seconds>=0&&observed.cpu_time_seconds<=1e12
                ?{cpu_time_seconds:observed.cpu_time_seconds}:{}),
            ...(Number.isFinite(observed.cpu_throttled_seconds)&&
                observed.cpu_throttled_seconds>=0&&
                observed.cpu_throttled_seconds<=1e12
                ?{cpu_throttled_seconds:observed.cpu_throttled_seconds}:{}),
            ...(Number.isSafeInteger(observed.cpu_throttle_events)&&
                observed.cpu_throttle_events>=0
                ?{cpu_throttle_events:observed.cpu_throttle_events}:{}),
            ...(Number.isSafeInteger(observed.cpu_quota_periods)&&
                observed.cpu_quota_periods>=0
                ?{cpu_quota_periods:observed.cpu_quota_periods}:{}),
            ...(Number.isSafeInteger(observed.peak_job_memory_bytes)&&
                observed.peak_job_memory_bytes>=0
                ?{peak_job_memory_bytes:observed.peak_job_memory_bytes}:{}),
            ...(Number.isSafeInteger(observed.process_tasks)&&
                observed.process_tasks>=0
                ?{process_tasks:observed.process_tasks}:{}),
        };
        // Optional adapter metrics are observational. A malformed or
        // unavailable summary must never turn verified service state into
        // an unverified start/stop or influence the policy/checkpoint.
        const owned=this.supervisor.snapshot().find(r=>r.job_id===job.id);
        const ownsArtifacts=owned&&owned.lease_id===lease.id&&owned.project_id===job.project_id&&
            (owned.artifact_lineage_root_id||owned.job_id)===
                (job.artifact_lineage_root_id||job.id);
        if(ownsArtifacts&&(state==='running'&&lease.status==='acknowledged'||
            lease.status==='released'&&['finished','stopped'].includes(state)&&job.final_checkpoint_sha256)&&job.evaluation_stop&&
            supportsEvaluation(this.evaluationSuites,job)) {
            const results=[];
            for(const rule of evaluationRules(job.evaluation_stop)) {
                try {
                    const result=localAdapter.readEvaluation(Object.freeze({
                        job_id:job.id,project_id:job.project_id,
                        artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                        dispatch_id:order.dispatch_id,suite_id:rule.suite_id,
                    }));
                    if(result!=null&&(state==='running'||result.checkpoint_sha256===job.final_checkpoint_sha256))
                        results.push(evaluationEvidence(result,rule,order.dispatch_id));
                }catch(error) {
                    report.evaluation_issue=(rule.suite_id+': '+
                        String(error.message||error)).slice(0,160);
                }
            }
            if(job.evaluation_stop.all_of) {
                if(results.length)report.evaluations=results;
            }else if(results.length) {
                report.evaluation=results[0];
            }
        }
        const checkpointProject=['courier','tinybrains','bees_native'].includes(job.project_id);
        if(checkpointProject&&(lease.status!=='released'||
            order.action==='inspect'&&job.reverify_checkpoint===true||
            report.evaluation||report.evaluations)&&
            (state==='finished'||state==='stopped'&&observed.checkpoint_stop_verified===true)) {
            // A successful native exit, including natural completion, does
            // not establish that a resumable checkpoint exists. Reuse the
            // adapter's own continuation artifact contract.
            try {
                if(!ownsArtifacts||typeof localAdapter?.checkpoint_ready!=='function'||
                    await localAdapter.checkpoint_ready(Object.freeze({
                        job_id:job.id,project_id:job.project_id,
                        artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                        continuation_of_job_id:job.continuation_of_job_id||null,
                        frozen_parameters:job.frozen_parameters,
                    }))!==true)
                    throw Error('Local adapter did not verify resumable checkpoint artifacts.');
                if(state==='finished')report.checkpoint_ready_verified=true;
                else report.checkpoint_stop_verified=true;
            }catch(error) {
                report.diagnostic=('Native '+(state==='finished'?'Finish':'checkpoint Stop')+
                    ' completed without verified resumable checkpoint: '+
                    String(error.message||error)).slice(0,220);
            }
        }
        if(checkpointProject&&
            (report.checkpoint_ready_verified||report.checkpoint_stop_verified)&&
            typeof localAdapter?.model_checkpoint_sha256==='function') {
            try {
                const digest=await localAdapter.model_checkpoint_sha256(Object.freeze({
                    job_id:job.id,project_id:job.project_id,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    frozen_parameters:job.frozen_parameters,
                }));
                if(!/^[a-f0-9]{64}$/.test(String(digest||'')))
                    throw Error('Invalid resumable model fingerprint.');
                report.checkpoint_model_sha256=digest;
            }catch(error) {
                report.diagnostic=('Cannot prove the exact evaluated model for candidate promotion: '+
                    String(error.message||error)).slice(0,220);
            }
        }
        if(lease.status!=='released'&&ownsArtifacts&&typeof localAdapter?.readProgress==='function'&&
            ['running','stop_requested','finished','failed','stopped']
                .includes(state)) {
            try {
                const result=await localAdapter.readProgress(Object.freeze({
                    job_id:job.id,project_id:job.project_id,
                    state,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                }));
                if(result!==null&&result!==undefined)
                    report.progress=validateProgress(result,job.project_id);
            }catch(error) {
                report.progress_issue=String(error.message||error).slice(0,160);
            }
        }
        return report;
    }
}
function fromEnvironment(env=process.env,options={}) {
    if(env.EXPERIMENT_HUB_ENABLE_EXECUTION!=='1')return null;
    const hostOs=options.hostOs||os;
    if(!['linux','win32'].includes(hostOs.platform()))
        throw Error('Native execution is supported only on Linux and Windows.');
    const adapterFile=env.EXPERIMENT_HUB_LOCAL_ADAPTERS;
    const root=env.EXPERIMENT_HUB_LOCAL_ROOT;
    if(typeof adapterFile!=='string'||!path.isAbsolute(adapterFile)||
        typeof root!=='string'||!path.isAbsolute(root))
        throw Error('Specify absolute local adapter module and supervisor state directory.');
    // This module path is chosen by the machine administrator in local
    // environment configuration, never by the Hub, a job or a heartbeat.
    const adapters=(options.loadAdapters||require)(adapterFile);
    if(!adapters||typeof adapters!=='object'||Array.isArray(adapters)||
        Object.entries(adapters).some(([project,adapter])=>
            !ID.test(project)||project==='bees'||
            !adapter||typeof adapter!=='object'||
            typeof adapter.executable!=='string'||
            !path.isAbsolute(adapter.executable)||
            typeof adapter.args!=='function'||
            (adapter.cwd!=null&&
                (typeof adapter.cwd!=='string'||!path.isAbsolute(adapter.cwd)))))
        throw Error('Supply a valid locally trusted non-Bees adapter map, or an empty map for native recovery.');
    if(hostOs.platform()==='win32'&&Object.entries(adapters).some(
        ([,adapter])=>adapter.stop_policy!=='terminate-group'))
        throw Error('Windows adapters must explicitly opt into force-terminating Job Object stop semantics.');
    const machineId=env.EXPERIMENT_HUB_MACHINE_ID;
    if(!ID.test(String(machineId||'')))throw Error('Invalid local machine identity.');
    let supervisor;
    if(hostOs.platform()==='linux') {
        if(Object.keys(adapters).length) {
            const capabilities=(options.inspectHost||inspectHost)();
            if(!capabilities.cgroup_v2_detected||
                !['cpu','memory'].every(c=>capabilities.cgroup_controllers.includes(c)))
                throw Error('Required Linux CPU and memory controllers are not available.');
        }
        const docker=env.EXPERIMENT_HUB_LINUX_BACKEND==='docker';
        const mixed=env.EXPERIMENT_HUB_LINUX_BACKEND==='mixed';
        if(env.EXPERIMENT_HUB_LINUX_BACKEND!==undefined&&
            !['native','docker','mixed'].includes(env.EXPERIMENT_HUB_LINUX_BACKEND))
            throw Error('Unknown local Linux execution backend.');
        if(!docker&&!mixed) {
            const journal=path.join(root,'docker-owner.json');
            if(fs.existsSync(journal)) {
                const saved=JSON.parse(fs.readFileSync(journal,'utf8'));
                if(!Array.isArray(saved.records)||
                    saved.records.some(r=>!['finished','stopped','failed'].includes(r.state)))
                    throw Error('Unresolved Docker job ownership blocks native backend activation.');
            }
            if(fs.existsSync(path.join(root,'docker-gpu-probe.json')))
                throw Error('Unresolved Docker GPU proof blocks native backend activation.');
        }
        const local={root,machineId,adapters,
            gpuEnabled:env.EXPERIMENT_HUB_ENABLE_ROCM==='1',
            dockerSudo:(docker||mixed)&&env.EXPERIMENT_HUB_DOCKER_SUDO==='1'};
        supervisor=options.createSupervisor
            ?options.createSupervisor(local)
            :mixed?new MixedLinuxSupervisor(local)
                :docker?new DockerSupervisor(local):new LinuxSupervisor(local);
    }else {
        const helperPath=env.EXPERIMENT_HUB_WINDOWS_HELPER;
        const helperSha256=env.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256;
        if(typeof helperPath!=='string'||!path.isAbsolute(helperPath)||
            !/^[a-f0-9]{64}$/.test(String(helperSha256||'')))
            throw Error('Specify a locally installed absolute Windows owner path and its SHA-256.');
        const local={root,machineId,adapters,helperPath,helperSha256,
            gpuEnabled:env.EXPERIMENT_HUB_ENABLE_CUDA==='1'};
        supervisor=options.createSupervisor
            ?options.createSupervisor(local)
            :new WindowsSupervisor(local);
    }
    return new LocalExecution({machineId,supervisor,
        projects:Object.keys(adapters)});
}
module.exports={LocalExecution,fromEnvironment};
