'use strict';

// Project-neutral definitions and job records. This never starts a Bees run.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {request: resourceRequest,matchingLeaseBudget,placements: resourcePlacements,
    startHeadroom,observedLoadPreference} = require('./experiment_hub_resources');
const {validateProgress,selectChartMetrics}=require('./experiment_hub_progress');
const {tinyBrainsProfiles,tinyBrainsImports,supportsTinyBrainsProfile}=require('./experiment_hub_tinybrains_adapter');
const {evaluationPolicy,evaluationEvidence,evaluationRules,evaluationComplete,suiteIds,supportsEvaluation}=
    require('./experiment_hub_evaluation');
const {ProgressHistory}=require('./experiment_hub_history');
const {ObservationArchive}=require('./experiment_hub_observations');
const {validateSelection}=require('./experiment_hub_native_historical');
const {verifiedNativeCollectorPin,verifiedNativeModelMirror,verifiedNativeModelPin}=
    require('./experiment_hub_native_bundle_transfer');
const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const SHA256 = /^[0-9a-f]{64}$/;
function rolloutEndpoint(value) {
    if(typeof value!=='string')return false;
    const parts=value.split(':');
    if(parts.length!==2||!/^[0-9]{1,5}$/.test(parts[1]))return false;
    const octets=parts[0].split('.');
    return octets.length===4&&octets.every(p=>/^[0-9]{1,3}$/.test(p)&&Number(p)<=255)&&
        Number(octets[0])===100&&Number(octets[1])>=64&&Number(octets[1])<=127&&
        Number(parts[1])>=1&&Number(parts[1])<=65535;
}
const PCI = /^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const CUDA_UUID = /^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const now = () => new Date().toISOString();
const AGENT_HEARTBEAT_TTL_MS = 45000;
const DISTRIBUTED_LAUNCH_TTL_MS = 20 * 60 * 1000;
const FINAL_EVALUATION_GRACE_MS = 24 * 60 * 60 * 1000;
function fingerprint(machineId, token) {
    return crypto.createHash('sha256').update(machineId + ':' + token).digest('hex');
}
function configuredGpus(input,requirePci=false) {
    if(!Array.isArray(input)||input.length>32||
        input.some(g=>!object(g)||!ID.test(String(g.id||''))||
            !Number.isSafeInteger(g.vram_mb)||g.vram_mb<1||
            g.vram_mb>1000000||
            (g.hub_vram_mb!=null&&
                (!Number.isSafeInteger(g.hub_vram_mb)||g.hub_vram_mb<0||
                    g.hub_vram_mb>g.vram_mb))||
            (g.hub_compute_pct!=null&&
                (!Number.isSafeInteger(g.hub_compute_pct)||g.hub_compute_pct<0||
                    g.hub_compute_pct>100))||
            (requirePci&&!PCI.test(String(g.pci_address||'')))||
            (g.pci_address!=null&&!PCI.test(String(g.pci_address))))||
        new Set(input.map(g=>g.id)).size!==input.length||
        new Set(input.filter(g=>g.pci_address).map(g=>g.pci_address)).size!==
            input.filter(g=>g.pci_address).length)
        throw Error('GPU IDs, PCI addresses, or nominal VRAM inventory invalid.');
    return input.map(g=>({
        id:g.id,vram_mb:g.vram_mb,
        ...(g.pci_address?{pci_address:g.pci_address}:{}),
        hub_vram_mb:g.hub_vram_mb??g.vram_mb,
        hub_compute_pct:g.hub_compute_pct??100,
    }));
}
function physicalGpuBindings(machine,report,resources,pinned) {
    // Legacy claims can omit GPU fields, but inconsistent GPU budgets
    // must never be treated as CPU-only or acknowledged after rebinding.
    let validated;
    try { validated=resourceRequest(resources); }
    catch { return null; }
    if(validated.gpu_count===0)return [];
    const ids=resources.gpus;
    // A lease freezes logical ID -> physical PCI identity at allocation.
    // Never reconstruct a missing old pin from a later machine registration.
    if(!Number.isSafeInteger(resources.gpu_count)||resources.gpu_count<1||
        !Array.isArray(ids)||ids.length!==resources.gpu_count||
        !Array.isArray(pinned)||pinned.length!==ids.length||
        !['amdgpu-sysfs-v1','nvidia-smi-v1'].includes(report.gpu_inventory_source))
        return null;
    const bindings=[];
    for(const id of ids) {
        const config=machine.gpus.find(g=>g.id===id);
        const bound=pinned.find(g=>g?.id===id);
        if(!config?.pci_address||!bound||
            !PCI.test(String(bound.pci_address))||
            bound.pci_address!==config.pci_address||
            !Number.isSafeInteger(bound.vram_mb)||bound.vram_mb<1||
            bound.vram_mb>config.vram_mb)
            return null;
        const physical=report.gpu_devices.find(g=>
            g.pci_address===bound.pci_address);
        if(!physical||physical.vram_mb<bound.vram_mb||
            (report.gpu_inventory_source==='nvidia-smi-v1'&&
                (!CUDA_UUID.test(String(bound.cuda_uuid||''))||
                    bound.cuda_uuid!==physical.cuda_uuid))||
            bound.vram_mb<(resources.min_gpu_vram_mb||0)||
            (resources.gpu_mode==='shared'&&
                bound.vram_mb<(resources.gpu_vram_mb||0)))
            return null;
        bindings.push({id,pci_address:bound.pci_address,
            vram_mb:bound.vram_mb,
            ...(report.gpu_inventory_source==='nvidia-smi-v1'
                ?{cuda_uuid:bound.cuda_uuid}:{})});
    }
    return bindings;
}
function boundedReport(value) {
    if (!object(value) ||
        !Number.isSafeInteger(value.cpus) || value.cpus < 1 || value.cpus > 4096 ||
        !Number.isSafeInteger(value.ram_mb) || value.ram_mb < 128 ||
        value.ram_mb > 100000000 ||
        typeof value.platform !== 'string' ||
        !/^[a-z0-9_-]{1,32}$/i.test(value.platform))
        throw Error('Invalid agent CPU, RAM, or platform report.');
    let execution={
        protocol_version:0,backend:'none',process_supervision:false,
        cpu_quota_enforced:false,memory_limit_enforced:false,
        gpu_isolation_enforced:false,cgroup_v2_detected:false,
        cgroup_controllers:[],reason:'Agent has not reported host isolation capabilities.',
    };
    if(value.execution!==undefined) {
        const e=value.execution;
        if(!object(e)||e.protocol_version!==1||e.platform!==value.platform||
            e.backend!=='none'||e.process_supervision!==false||
            e.cpu_quota_enforced!==false||e.memory_limit_enforced!==false||
            e.gpu_isolation_enforced!==false||
            typeof e.cgroup_v2_detected!=='boolean'||
            !Array.isArray(e.cgroup_controllers)||e.cgroup_controllers.length>5||
            e.cgroup_controllers.some(x=>
                !['cpu','memory','io','pids','cpuset'].includes(x))||
            new Set(e.cgroup_controllers).size!==e.cgroup_controllers.length||
            typeof e.reason!=='string'||e.reason.length>220)
            throw Error('Invalid agent execution capability report.');
        // A reported cgroup hierarchy is discovery only. No credential
        // or heartbeat can turn these flags into enforcement.
        execution={
            protocol_version:1,backend:'none',process_supervision:false,
            cpu_quota_enforced:false,memory_limit_enforced:false,
            gpu_isolation_enforced:false,
            cgroup_v2_detected:e.cgroup_v2_detected,
            cgroup_controllers:e.cgroup_controllers.slice(),reason:e.reason,
        };
    }
    const freeRam=value.free_ram_mb??null;
    if(freeRam!==null&&
        (!Number.isSafeInteger(freeRam)||freeRam<0||freeRam>value.ram_mb))
        throw Error('Invalid host free RAM observation.');
    const cpuBusy=value.host_cpu_busy_pct??null;
    if(cpuBusy!==null&&(!Number.isFinite(cpuBusy)||cpuBusy<0||cpuBusy>100))
        throw Error('Invalid host-wide CPU utilization sample.');
    const availableRam=value.available_ram_mb??null;
    if(availableRam!==null&&
        (!Number.isSafeInteger(availableRam)||availableRam<0||availableRam>value.ram_mb))
        throw Error('Invalid host available memory observation.');
    const gpuSource=value.gpu_inventory_source||'none';
    const devices=value.gpu_devices==null?[]:value.gpu_devices;
    if(!['none','amdgpu-sysfs-v1','nvidia-smi-v1'].includes(gpuSource)||
        (gpuSource==='amdgpu-sysfs-v1'&&value.platform!=='linux')||
        (gpuSource==='nvidia-smi-v1'&&value.platform!=='win32')||
        !Array.isArray(devices)||devices.length>32||
        (gpuSource==='none'&&devices.length)||
        devices.some(g=>!object(g)||!PCI.test(String(g.pci_address||''))||
            (gpuSource==='nvidia-smi-v1'
                ?!CUDA_UUID.test(String(g.cuda_uuid||'')):g.cuda_uuid!=null)||
            !Number.isSafeInteger(g.vram_mb)||g.vram_mb<1||
            g.vram_mb>1000000||
            (g.used_vram_mb!=null&&
                (!Number.isSafeInteger(g.used_vram_mb)||
                    g.used_vram_mb<0||g.used_vram_mb>g.vram_mb))||
            (g.gpu_busy_pct!=null&&
                (!Number.isSafeInteger(g.gpu_busy_pct)||
                    g.gpu_busy_pct<0||g.gpu_busy_pct>100))||
            ['temperature_c','hotspot_temperature_c'].some(k=>
                g[k]!=null&&(!Number.isFinite(g[k])||
                    g[k]<0||g[k]>150))||
            (g.power_w!=null&&(!Number.isFinite(g.power_w)||
                g.power_w<0||g.power_w>1500)))||
        new Set(devices.map(g=>g.pci_address)).size!==devices.length||
        (gpuSource==='nvidia-smi-v1'&&
            new Set(devices.map(g=>g.cuda_uuid.toLowerCase())).size!==devices.length))
        throw Error('Invalid bounded physical GPU inventory.');
    const observedGpus=devices.map(g=>({
        pci_address:g.pci_address,vram_mb:g.vram_mb,
        used_vram_mb:g.used_vram_mb??null,
        gpu_busy_pct:g.gpu_busy_pct??null,
        temperature_c:g.temperature_c??null,
        hotspot_temperature_c:g.hotspot_temperature_c??null,
        power_w:g.power_w??null,
        ...(gpuSource==='nvidia-smi-v1'?{cuda_uuid:g.cuda_uuid}:{}),
    }));
    const localExecutor=value.local_executor===true;
    const preflight=value.native_preflight_passed===true;
    const reason=value.native_preflight_reason;
    if((preflight&&!localExecutor)||
        (reason!=null&&
            (typeof reason!=='string'||reason.length>180)))
        throw Error('Invalid native execution qualification report.');
    const installed=value.installed_projects==null?[]:value.installed_projects;
    if(!Array.isArray(installed)||installed.length>32||
        installed.some(p=>typeof p!=='string'||!ID.test(p))||
        new Set(installed).size!==installed.length||
        (!localExecutor&&installed.length))
        throw Error('Invalid locally installed execution adapters.');
    const evaluators=value.evaluation_suites??{};
    if(!object(evaluators)||Object.keys(evaluators).length>installed.length||
        Object.keys(evaluators).some(project=>!installed.includes(project)))
        throw Error('Evaluation capability requires a corresponding installed adapter.');
    const curriculumSources=value.native_bees_curriculum_sources??[];
    if(!Array.isArray(curriculumSources)||curriculumSources.length>8||
        (curriculumSources.length&&
            (!localExecutor||!installed.includes('bees_native_collect')||
             !['win32','linux'].includes(value.platform)))||
        curriculumSources.some((stage,index)=>!object(stage)||
            Object.keys(stage).sort().join(',')!==
                'source_run_contract_sha256,start_round'||
            !Number.isSafeInteger(stage.start_round)||
            stage.start_round<1||stage.start_round>8||
            (index===0?stage.start_round!==1:
                stage.start_round<=curriculumSources[index-1].start_round)||
            !SHA256.test(String(stage.source_run_contract_sha256||''))))
        throw Error('Invalid pinned native Bees collector curriculum capability.');
    const profiles=tinyBrainsProfiles(value.tinybrains_profiles??{});
    if(Object.keys(profiles).length&&(!installed.includes('tinybrains')||!['win32','linux'].includes(value.platform)))
        throw Error('TinyBrains profiles require an installed native adapter.');
    const cudaProfiles=value.tinybrains_cuda_profiles??[];
    if(!Array.isArray(cudaProfiles)||cudaProfiles.length>Object.keys(profiles).length||
        cudaProfiles.some(id=>typeof id!=='string'||!Object.hasOwn(profiles,id))||
        new Set(cudaProfiles).size!==cudaProfiles.length||
        (cudaProfiles.length&&(!installed.includes('tinybrains')||
            !['linux','win32'].includes(value.platform))))
        throw Error('CUDA/ROCm TinyBrains profiles require an installed native adapter.');
    const threadOverrides=value.tinybrains_thread_overrides??[];
    if(!Array.isArray(threadOverrides)||
        threadOverrides.length>Object.keys(profiles).length||
        threadOverrides.some(id=>typeof id!=='string'||!Object.hasOwn(profiles,id))||
        new Set(threadOverrides).size!==threadOverrides.length)
        throw Error('TinyBrains thread override requires a locally approved profile.');
    const cpuFallback=value.tinybrains_cpu_fallback_profiles??[];
    if(!Array.isArray(cpuFallback)||cpuFallback.length>cudaProfiles.length||
        cpuFallback.some(id=>typeof id!=='string'||!cudaProfiles.includes(id))||
        new Set(cpuFallback).size!==cpuFallback.length)
        throw Error('TinyBrains CPU fallback requires an approved GPU profile.');
    const dualProfiles=value.tinybrains_dual_gpu_profiles??[];
    if(!Array.isArray(dualProfiles)||dualProfiles.length>cudaProfiles.length||
        dualProfiles.some(id=>typeof id!=='string'||!cudaProfiles.includes(id))||
        new Set(dualProfiles).size!==dualProfiles.length||
        (dualProfiles.length&&value.platform!=='linux'))
        throw Error('Dual GPU TinyBrains requires locally qualified Linux ROCm profiles.');
    const remoteRollouts=value.tinybrains_remote_rollout_profiles??[];
    const remoteCount=value.tinybrains_remote_rollout_count??0;
    const remoteEndpoints=value.tinybrains_remote_rollout_endpoints??[];
    const remoteFingerprint=value.tinybrains_remote_key_fingerprint??null;
    const workerEndpoint=value.tinybrains_rollout_endpoint??null;
    const workerFingerprint=value.tinybrains_rollout_key_fingerprint??null;
    const workerThreadLimit=value.tinybrains_rollout_thread_limit??0;
    if(!Array.isArray(remoteRollouts)||remoteRollouts.length>cudaProfiles.length||
        remoteRollouts.some(id=>typeof id!=='string'||!cudaProfiles.includes(id))||
        new Set(remoteRollouts).size!==remoteRollouts.length||
        (remoteRollouts.length&&value.platform!=='linux')||
        !Number.isSafeInteger(remoteCount)||remoteCount<0||remoteCount>8||
        Boolean(remoteRollouts.length)!==Boolean(remoteCount)||
        !Array.isArray(remoteEndpoints)||remoteEndpoints.length!==remoteCount||
        remoteEndpoints.some(endpoint=>!rolloutEndpoint(endpoint))||
        new Set(remoteEndpoints).size!==remoteEndpoints.length||
        (remoteCount>0?!SHA256.test(String(remoteFingerprint||'')):remoteFingerprint!==null)||
        (workerEndpoint===null)!==(workerFingerprint===null)||
        (workerEndpoint!==null&&(!rolloutEndpoint(workerEndpoint)||
            !SHA256.test(String(workerFingerprint||''))||
            !installed.includes('tinybrains_rollout')))||
        (installed.includes('tinybrains_rollout')&&workerEndpoint===null)||
        !Number.isSafeInteger(workerThreadLimit)||workerThreadLimit<0||workerThreadLimit>256||
        Boolean(workerThreadLimit)!==Boolean(workerEndpoint))
        throw Error('Remote rollout requires a locally installed Linux TinyBrains GPU adapter.');
    const requiredImports=value.tinybrains_required_import_profiles??[];
    if(!Array.isArray(requiredImports)||requiredImports.length>Object.keys(profiles).length||
        requiredImports.some(id=>typeof id!=='string'||!Object.hasOwn(profiles,id))||
        new Set(requiredImports).size!==requiredImports.length)
        throw Error('Pinned-seed requirement must reference approved TinyBrains profiles.');
    const imports=tinyBrainsImports(value.tinybrains_imports??{});
    if(Object.keys(imports).length&&!installed.includes('tinybrains')||
        Object.values(imports).some(item=>!Object.hasOwn(profiles,item.profile)))
        throw Error('Trusted TinyBrains checkpoint imports require an approved profile.');
    const gpuProjects=value.gpu_projects??[];
    if(!Array.isArray(gpuProjects)||new Set(gpuProjects).size!==gpuProjects.length||
        gpuProjects.length>installed.length||
        gpuProjects.some(project=>!installed.includes(project))||
        (gpuProjects.length&&!['linux','win32'].includes(value.platform)))
        throw Error('GPU execution capability requires installed native adapters.');
    const gpuShared=value.gpu_shared_projects??[];
    if(!Array.isArray(gpuShared)||gpuShared.length>gpuProjects.length||
        gpuShared.some(p=>typeof p!=='string'||!gpuProjects.includes(p))||
        new Set(gpuShared).size!==gpuShared.length||
        (gpuShared.length&&!['win32','linux'].includes(value.platform)))
        throw Error('Shared GPU execution requires an opted-in native adapter.');
    const gpuDualNative=value.gpu_dual_native_projects??[];
    if(!Array.isArray(gpuDualNative)||gpuDualNative.length>3||
        new Set(gpuDualNative).size!==gpuDualNative.length||
        gpuDualNative.some(project=>
            !['bees_native','bees_native_collect','bees_native_optimizer_worker'].includes(project)||
            !gpuProjects.includes(project))||
        (gpuDualNative.length&&value.platform!=='linux'))
        throw Error('Native Bees dual-GPU capability needs a locally approved Linux adapter.');
    const evaluationSuites=Object.fromEntries(Object.entries(evaluators)
        .map(([project,ids])=>[project,suiteIds(ids)]));
    return {cpus:value.cpus,ram_mb:value.ram_mb,
        free_ram_mb:freeRam,available_ram_mb:availableRam,
        host_cpu_busy_pct:cpuBusy,
        platform:value.platform,execution,
        gpu_inventory_source:gpuSource,gpu_devices:observedGpus,
        local_executor:localExecutor&&['linux','win32'].includes(value.platform),
        native_preflight_passed:localExecutor&&
            ['linux','win32'].includes(value.platform)&&preflight,
        native_preflight_reason:reason||null,
        evaluation_suites:localExecutor?evaluationSuites:{},
        native_bees_curriculum_sources:localExecutor
            ?curriculumSources.map(copy):[],
        tinybrains_profiles:profiles,
        tinybrains_cuda_profiles:cudaProfiles.slice(),
        tinybrains_thread_overrides:threadOverrides.slice(),
        tinybrains_cpu_fallback_profiles:cpuFallback.slice(),
        tinybrains_required_import_profiles:requiredImports.slice(),
        tinybrains_dual_gpu_profiles:dualProfiles.slice(),
        tinybrains_remote_rollout_profiles:remoteRollouts.slice(),
        tinybrains_remote_rollout_count:remoteCount,
        tinybrains_remote_rollout_endpoints:remoteEndpoints.slice(),
        tinybrains_remote_key_fingerprint:remoteFingerprint,
        tinybrains_rollout_endpoint:workerEndpoint,
        tinybrains_rollout_key_fingerprint:workerFingerprint,
        tinybrains_rollout_thread_limit:workerThreadLimit,
        tinybrains_imports:imports,
        gpu_projects:gpuProjects.slice(),
        gpu_shared_projects:gpuShared.slice(),
        gpu_dual_native_projects:gpuDualNative.slice(),
        installed_projects:['linux','win32'].includes(value.platform)&&
            localExecutor?installed.slice():[]};
}

function object(value) {
    return value && typeof value === 'object' && !Array.isArray(value);
}
function copy(value) { return JSON.parse(JSON.stringify(value)); }
function name(value, label) {
    if (typeof value !== 'string' || !value.trim() || value.length > 100)
        throw Error(label + ' must contain 1–100 characters.');
    return value.trim();
}
function config(value) {
    if (!object(value)) throw Error('Configuration parameters must be a JSON object.');
    const serialized = JSON.stringify(value);
    if (serialized.length > 65536) throw Error('Configuration exceeds 64 KiB.');
    return JSON.parse(serialized);
}
// The installer pins physical Level sources. Pick the same immutable stage
// boundary as native_curriculum.selected_curriculum_run; unknown capability
// is not evidence that a collector can execute the selected environment.
function nativeBeesStageSource(machine,round) {
    const sources=machine?.native_bees_curriculum_sources;
    if(!Array.isArray(sources)||!sources.length||
        !Number.isSafeInteger(round)||round<1||round>8)return null;
    let selected=null;
    for(const stage of sources) {
        if(stage.start_round>round)break;
        selected=stage.source_run_contract_sha256;
    }
    return selected;
}
function dualGpuApproved(job,machine) {
    if(!job||!machine||machine.agent_platform!=='linux'||
        job.resources?.gpu_count!==2||
        job.resources?.gpu_mode!=='exclusive')
        return false;
    if(job.project_id==='tinybrains')
        return job.frozen_parameters?.device==='dual'&&
            machine.tinybrains_dual_gpu_profiles
                ?.includes(job.frozen_parameters.profile)===true;
    return ['bees_native','bees_native_collect','bees_native_optimizer_worker'].includes(job.project_id)&&
        machine.gpu_dual_native_projects?.includes(job.project_id)===true;
}
function gpuStartBlock(job,lease,machine) {
    const r=job.resources,allocated=lease.resources;
    if(r.gpu_count===0&&allocated.gpu_count===0&&
        !(r.gpus||[]).length&&!(allocated.gpus||[]).length)return null;
    const sharedApproved=['win32','linux'].includes(machine.agent_platform)&&
        r.gpu_mode==='shared'&&allocated.gpu_mode==='shared'&&
        machine.gpu_shared_projects?.includes(job.project_id);
    const single=r.gpu_count===1&&allocated.gpu_count===1&&
        (r.gpu_mode==='exclusive'&&allocated.gpu_mode==='exclusive'||sharedApproved);
    const dual=dualGpuApproved(job,machine)&&allocated.gpu_count===2&&
        allocated.gpu_mode==='exclusive';
    if(!(single||dual)||allocated.gpus?.length!==r.gpu_count||
        (r.gpus?.length&&r.gpus.some((id,i)=>id!==allocated.gpus[i])))
        return 'Native GPU Start requires one GPU or two approved, exclusively bound ROCm GPUs.';
    if(!machine.gpu_projects?.includes(job.project_id))
        return 'The local adapter has not opted into native GPU execution.';
    if(machine.agent_platform==='win32') {
        if(machine.gpu_inventory_source!=='nvidia-smi-v1'||
            physicalGpuBindings(machine,{
                gpu_inventory_source:machine.gpu_inventory_source,
                gpu_devices:machine.observed_gpus||[],
            },allocated,lease.gpu_bindings)?.length!==1)
            return 'Windows CUDA Start requires a fresh PCI/UUID-pinned NVIDIA lease.';
        return null; // Cooperative CUDA visibility, not kernel GPU isolation.
    }
    if(machine.agent_platform!=='linux')
        return 'Native GPU execution is unavailable on this operating system.';
    if((machine.observed_gpus?.length??0)<r.gpu_count)
        return 'Native GPU Start requires enough freshly observed physical devices for its exact GPU lease.';
    if(job.gpu_qualification?.current!==true)
        return 'Current device access and HIP runtime qualification for this exact GPU lease are required.';
    return null;
}
function nativeResourceBlock(job,lease) {
    try {
        return matchingLeaseBudget(job.resources,lease.resources)
            ?null:'Stored job resource budget differs from its reserved lease.';
    }catch {
        return 'Stored job or lease resource budget is invalid.';
    }
}
function gpuProbeWorkerReady(job,lease,machine) {
    return Boolean(job&&job.project_id!=='bees'&&job.adapter==='unconfigured'&&
        job.status==='waiting_adapter'&&!job.dispatch_id&&
        lease&&lease.job_id===job.id&&lease.project_id===job.project_id&&
        lease.machine_id===job.assigned_machine_id&&lease.issued_by==='experiment-hub'&&
        lease.status==='acknowledged'&&machine?.status==='connected'&&
        machine.agent_platform==='linux'&&machine.local_executor_enabled===true&&
        machine.native_preflight_passed===true);
}
function gpuProbeBlock(job,lease,machine) {
    const count=job?.resources?.gpu_count;
    const dual=dualGpuApproved(job,machine);
    if(!gpuProbeWorkerReady(job,lease,machine)||!lease.receipt_fresh||
        !machine.installed_projects?.includes(job.project_id)||
        !(count===1&&(job.resources.gpu_mode==='exclusive'||
            job.resources.gpu_mode==='shared'&&
                machine.gpu_shared_projects?.includes(job.project_id))||dual)||
        (job.resources.gpus?.length>0&&
            job.resources.gpus.some((id,i)=>id!==lease?.resources.gpus?.[i]))||
        lease.resources.gpu_mode!==job.resources.gpu_mode||
        lease.resources.gpu_count!==count||lease.resources.gpus?.length!==count||
        physicalGpuBindings(machine,{
            gpu_inventory_source:machine.gpu_inventory_source,
            gpu_devices:machine.observed_gpus,
        },lease.resources,lease.gpu_bindings)?.length!==count)
        return 'GPU access probe requires a fresh approved one- or two-GPU lease on a qualified Linux agent.';
    const resourceBlock=nativeResourceBlock(job,lease);
    if(resourceBlock)return resourceBlock;
    if(!(machine.observed_gpus?.length>=2))
        return 'GPU access probe requires at least two observed physical GPUs.';
    return null;
}
// Native finish can complete an independent sequence. Checkpoint continuations
// additionally require a verified resumable artifact; independent sequences
// never inherit an optimizer or model checkpoint.
function independentSequenceComplete(job) {
    // Retired Ants-v2 records remain inspectable, but cannot unlock
    // new experiment work or previously queued dependent launches.
    if(!job||job.project_id==='ants')return false;
    if(job.status==='finished')return true;
    if(job.status!=='stopped'||!job.evaluation_stop||
        !job.evaluation_stop_evidence||!UUID.test(String(job.dispatch_id||''))||
        !job.stop_requested_at||!job.execution_updated_at)
        return false;
    return evaluationComplete(job.evaluation_stop_evidence,
        job.evaluation_stop,job.dispatch_id);
}
function checkpointContinuationComplete(job) {
    if(!['courier','tinybrains','bees_native'].includes(job?.project_id))return false;
    return job.status==='finished'
        ?job.checkpoint_ready_verified===true||
            (job.project_id!=='bees_native'&&job.checkpoint_proof_required!==true)
        :job.status==='stopped'&&job.checkpoint_stop_verified===true;
}
// A failed dispatch occupies no checkpoint lineage slot only after the
// original worker has verified that no Docker trainer was ever created,
// the capacity is released, and no live successor depends on the attempt.
function occupiesSuccessorSlot(job,jobs) {
    if(job.status==='cancelled')return false;
    if(job.status==='failed'&&job.prelaunch_absence_verified===true&&
        Boolean(job.capacity_released_at)&&
        !jobs.some(child=>child.status!=='cancelled'&&
            (child.continuation_of_job_id===job.id||child.after_job_id===job.id)))
        return false;
    return true;
}
// A finished native job may lack checkpoint proof even though the trainer exited
// successfully. Only an explicit administrator request may authorize another
// authenticated inspection. The existing owner and dispatch must attest the
// artifact; this routine never promotes based on filesystem guesses.
function finishCheckpointReinspection(job,report,observedAt) {
    if(!job.checkpoint_reinspection_requested_at||
        job.project_id!=='tinybrains'||job.status!=='finished')
        return false;
    const fingerprint=String(report.checkpoint_model_sha256||'');
    if(report.state==='finished'&&report.checkpoint_ready_verified===true&&
        SHA256.test(fingerprint)&&
        (!job.checkpoint_model_sha256||job.checkpoint_model_sha256===fingerprint)) {
        job.checkpoint_ready_verified=true;
        job.checkpoint_model_sha256=fingerprint;
        job.checkpoint_reinspected_at=observedAt;
        job.status_reason='Resumable checkpoint verified by operator-requested authenticated inspection; original Finish report retained.';
    }else {
        job.status_reason=('Checkpoint reinspection could not verify resumable artifacts: '+
            (report.diagnostic||report.state||'missing proof')).slice(0,220);
    }
    job.checkpoint_reinspection_requested_at=null;
    return true;
}
// Preserve a bounded set of independent evaluation receipts by exact model
// identity for both single-suite and multi-suite gates. A final resumable
// checkpoint may differ from the last observed intermediate checkpoint.
function retainEvaluationCohorts(job,receipts) {
    const cohorts=job.evaluation_candidates||[];
    let changed=false;
    for(const receipt of receipts) {
        const index=cohorts.findIndex(c=>
            c.checkpoint_sha256===receipt.checkpoint_sha256);
        const cohort=index>=0?cohorts[index]:{
            checkpoint_sha256:receipt.checkpoint_sha256,receipts:{}};
        if(JSON.stringify(cohort.receipts[receipt.suite_id])===
            JSON.stringify(receipt))continue;
        cohort.receipts[receipt.suite_id]=receipt;
        if(index>=0)cohorts.splice(index,1);
        cohorts.unshift(cohort);
        if(cohorts.length>8)cohorts.length=8;
        changed=true;
    }
    if(changed)job.evaluation_candidates=cohorts;
    return changed;
}
function missingFinalEvaluationRules(job) {
    if(!job.capacity_released_at||!checkpointContinuationComplete(job)||
        !independentSequenceComplete(job)||!SHA256.test(job.checkpoint_model_sha256||''))return [];
    const cohort=job.evaluation_candidates?.find(row=>row.checkpoint_sha256===job.checkpoint_model_sha256);
    return evaluationRules(job.evaluation_stop).filter(rule=>{
        const receipt=cohort?.receipts?.[rule.suite_id];
        try {
            const evidence=evaluationEvidence(receipt,rule,job.dispatch_id);
            return evidence.checkpoint_sha256!==job.checkpoint_model_sha256||
                !evidence.suite_sha256||evidence.trials<rule.min_trials||
                (job.evaluation_suite_definitions?.[rule.suite_id]&&
                    evidence.suite_sha256!==job.evaluation_suite_definitions[rule.suite_id]);
        }catch {
            return true;
        }
    });
}
// Both the starting checkpoint and every subsequent native PPO checkpoint
// use the same final fixed-reference evaluation decision. Incomplete suite
// coverage is never interpreted as a failed target or an invitation to
// allocate more physical workers.
function nativeRoundFinalEvaluation(job) {
    const rules=evaluationRules(job.evaluation_stop);
    const missing=rules.length?missingFinalEvaluationRules(job):[];
    if(missing.length||!rules.length)
        return {rules,missing,receipts:[],passed:false};
    const cohort=job.evaluation_candidates?.find(item=>
        item.checkpoint_sha256===job.checkpoint_model_sha256);
    const receipts=rules.map(rule=>cohort?.receipts?.[rule.suite_id]);
    if(receipts.some(receipt=>!receipt))
        throw Error('Native round evaluator lacks a verified receipt for the completed checkpoint.');
    const passed=evaluationComplete(
        job.evaluation_stop.all_of?{all_of:receipts}:receipts[0],
        job.evaluation_stop,job.dispatch_id,
    );
    return {rules,missing,receipts,passed};
}
// A newly approved native round may use independent evaluation suites
// different from those frozen on its source's finished training job. Keep
// this second evaluation *on the plan*, not by rewriting the source job's
// historical stopping policy. The plan ID is a separate evaluator dispatch
// identity for the same authenticated checkpoint and machine.
function nativeBaselinePlan(snapshot,job,at=Date.now()) {
    if(job?.project_id!=='bees_native'||job.status!=='finished'||
        !checkpointContinuationComplete(job)||!job.capacity_released_at||
        !UUID.test(String(job.dispatch_id||''))||
        !SHA256.test(String(job.checkpoint_model_sha256||'')))return null;
    return (snapshot.native_round_plans||[]).find(plan=>
        plan.status==='pending_collectors'&&plan.completed_rounds===0&&
        plan.origin_source_job_id===job.id&&
        plan.source_job_id===job.id&&
        plan.source_model_sha256===job.checkpoint_model_sha256&&
        plan.initial_evaluation_stop&&
        Number.isFinite(Date.parse(plan.approved_at))&&
        at>=Date.parse(plan.approved_at)&&
        at<Date.parse(plan.approved_at)+FINAL_EVALUATION_GRACE_MS&&
        evaluationRules(plan.initial_evaluation_stop).some(rule=>
            !(plan.initial_evaluation_receipts||[]).some(receipt=>{
                try {
                    const evidence=evaluationEvidence(receipt,rule,plan.id);
                    return evidence.checkpoint_sha256===job.checkpoint_model_sha256&&
                        SHA256.test(String(evidence.suite_sha256||''))&&
                        evidence.trials>=rule.min_trials;
                }catch{return false;}
            })
        )
    )||null;
}
// Accept only machine-authenticated receipts from the existing native
// evaluator, identified by a frozen plan and the original checkpoint. A
// baseline uses a separate dispatch ID: it never mutates the finished
// source job's independent training/evaluation evidence.
function retainNativeBaselineEvaluation(next,job,report,evaluationSuites) {
    const submitted=report.native_baseline_evaluation;
    if(submitted==null)return false;
    if(!object(submitted)||!UUID.test(String(submitted.plan_id||''))||
        !Array.isArray(submitted.receipts)||!submitted.receipts.length||
        submitted.receipts.length>8||Object.keys(submitted).some(key=>
            !['plan_id','receipts'].includes(key))||
        job.project_id!=='bees_native'||job.status!=='finished'||
        report.state!=='finished'||!checkpointContinuationComplete(job)||
        !job.capacity_released_at||
        !SHA256.test(String(job.checkpoint_model_sha256||'')))
        throw Error('Invalid independent native baseline evaluation report.');
    const plan=(next.native_round_plans||[]).find(item=>
        item.id===submitted.plan_id&&item.source_job_id===job.id&&
        item.origin_source_job_id===job.id&&
        item.source_model_sha256===job.checkpoint_model_sha256&&
        item.initial_evaluation_stop);
    if(!plan)
        throw Error('Native baseline evaluator has no original approved plan and checkpoint owner.');
    // An Inspect already issued by the original Hub may report after the
    // operator disarmed the sequence or a failed baseline triggered normal
    // collection. Do not rearm it or reject the unrelated valid heartbeat.
    if(plan.status!=='pending_collectors'&&
        !(plan.status==='complete'&&plan.completed_rounds===0&&
          plan.initial_checkpoint_sha256===job.checkpoint_model_sha256))
        return false;
    if(!supportsEvaluation(evaluationSuites,{
        project_id:job.project_id,evaluation_stop:plan.initial_evaluation_stop,
    }))
        throw Error('Native baseline evaluator lacks the originally approved suite owner.');
    const rules=evaluationRules(plan.initial_evaluation_stop);
    const retained=[...(plan.initial_evaluation_receipts||[])];
    const seen=new Set();
    let changed=false;
    for(const value of submitted.receipts) {
        const rule=rules.find(item=>item.suite_id===value?.suite_id);
        if(!rule||seen.has(rule.suite_id))
            throw Error('Native baseline reported an unapproved or duplicate suite.');
        seen.add(rule.suite_id);
        const receipt=evaluationEvidence(value,rule,plan.id);
        if(receipt.checkpoint_sha256!==job.checkpoint_model_sha256||
            !SHA256.test(String(receipt.suite_sha256||''))||
            receipt.trials<rule.min_trials)
            throw Error('Native baseline result has no complete fixed-reference checkpoint evidence.');
        const index=retained.findIndex(item=>item.suite_id===rule.suite_id);
        if(index>=0) {
            if(JSON.stringify(retained[index])!==JSON.stringify(receipt))
                throw Error('Previously accepted native baseline evidence changed.');
        }else {
            if(plan.status!=='pending_collectors')
                throw Error('A completed native evaluation plan cannot acquire new receipts.');
            retained.push(receipt);
            changed=true;
        }
    }
    if(changed) {
        plan.initial_evaluation_receipts=retained;
        plan.updated_at=now();
    }
    return changed;
}
// A missing final receipt cannot justify inspecting a historical terminal
// job forever. A waiting candidate plan extends the grace period until
// 24 hours after its last contender releases verified native capacity.
function finalEvaluationPending(snapshot,job,observedAt) {
    if(!missingFinalEvaluationRules(job).length)return false;
    let releasedAt=Date.parse(job.capacity_released_at);
    if(!Number.isFinite(releasedAt)||observedAt<releasedAt)return false;
    if(job.project_id==='bees_native'&&
        job.frozen_parameters?.optimizer_group===true) {
        // Rank zero can publish a verified checkpoint while other GPU
        // machines still hold their original leases. Independent holdout
        // evaluation belongs to the completed synchronized optimizer, not
        // just its checkpoint-writing process. Start the grace period only
        // after the final original rank has finished and released.
        if(nativeOptimizerGroupBlock(snapshot,job))return false;
        const members=snapshot.jobs.filter(member=>
            member.resource_bundle?.id===job.resource_bundle.id);
        const lastRelease=Math.max(...members.map(member=>
            Date.parse(member.capacity_released_at)));
        if(!Number.isFinite(lastRelease)||observedAt<lastRelease)return false;
        releasedAt=lastRelease;
    }
    let deadline=releasedAt+FINAL_EVALUATION_GRACE_MS;
    for(const plan of snapshot.candidate_selections||[]) {
        if(plan.status!=='waiting'||!plan.candidate_job_ids?.includes(job.id))
            continue;
        const candidates=plan.candidate_job_ids.map(id=>
            snapshot.jobs.find(candidate=>candidate.id===id));
        const incomplete=candidates.filter(candidate=>!candidate||
            !['finished','stopped'].includes(candidate.status)||
            !candidate.capacity_released_at);
        if(incomplete.length) {
            // Only a fresh native observation keeps an old final model
            // eligible while another contender is training. A persisted
            // running status cannot prolong work across an agent outage.
            if(incomplete.every(candidate=>candidate?.dispatch_id&&
                ['running','stop_requested'].includes(candidate.status)&&
                candidate.live_execution?.dispatch_id===candidate.dispatch_id&&
                ['running','stop_requested'].includes(
                    candidate.live_execution.state)))return true;
            continue;
        }
        const lastRelease=Math.max(...candidates.map(candidate=>
            Date.parse(candidate.capacity_released_at)));
        if(Number.isFinite(lastRelease))
            deadline=Math.max(deadline,lastRelease+FINAL_EVALUATION_GRACE_MS);
    }
    return observedAt<deadline;
}
function retainFinalEvaluations(job,report) {
    const rules=missingFinalEvaluationRules(job);
    if(!rules.length||report.checkpoint_model_sha256!==job.checkpoint_model_sha256)return false;
    const raw=job.evaluation_stop.all_of?report.evaluations:report.evaluation?[report.evaluation]:[];
    if(!Array.isArray(raw)||raw.length>8)return false;
    const definitions={...job.evaluation_suite_definitions};
    const receipts=[];
    for(const rule of rules) {
        const matching=raw.filter(receipt=>receipt?.suite_id===rule.suite_id);
        if(matching.length!==1)continue;
        try {
            const receipt=evaluationEvidence(matching[0],rule,job.dispatch_id);
            if(receipt.checkpoint_sha256!==job.checkpoint_model_sha256||!receipt.suite_sha256||
                receipt.trials<rule.min_trials||
                (definitions[rule.suite_id]&&definitions[rule.suite_id]!==receipt.suite_sha256))continue;
            definitions[rule.suite_id]=receipt.suite_sha256;
            receipts.push(receipt);
        }catch { /* Keep other valid suites eligible for comparison. */ }
    }
    if(!receipts.length)return false;
    job.evaluation_suite_definitions=definitions;
    return retainEvaluationCohorts(job,receipts);
}
// Only compare policies that have actually been evaluated and remain the
// exact bytes used for native checkpoint continuation. Training charts and
// a checkpoint-ready flag alone cannot select a winning model.
function comparableCandidate(job) {
    if(!job||!['courier','tinybrains','bees_native'].includes(job.project_id)||
        !['finished','stopped'].includes(job.status)||
        !checkpointContinuationComplete(job)||
        !independentSequenceComplete(job)||
        !SHA256.test(String(job.checkpoint_model_sha256||'')))
        throw Error('Candidate needs a verified, completed native checkpoint and model fingerprint.');
    const policy=evaluationPolicy(job.evaluation_stop);
    if(!policy)throw Error('Candidate lacks a frozen independent evaluation policy.');
    const rules=evaluationRules(policy);
    const sha=job.checkpoint_model_sha256;
    let receipts;
    if(policy.all_of) {
        const proved=job.evaluation_stop_evidence?.all_of;
        const cohort=(job.evaluation_candidates||[])
            .find(c=>c.checkpoint_sha256===sha);
        receipts=Array.isArray(proved)&&
            proved.every(r=>r?.checkpoint_sha256===sha)
            ?proved:rules.map(rule=>cohort?.receipts?.[rule.suite_id]);
    }else {
        const cohort=(job.evaluation_candidates||[])
            .find(c=>c.checkpoint_sha256===sha);
        receipts=[cohort?.receipts?.[policy.suite_id]||(job.evaluation_stop_evidence?.checkpoint_sha256===sha
            ?job.evaluation_stop_evidence
            :job.last_evaluation?.checkpoint_sha256===sha
                ?job.last_evaluation:null)];
    }
    if(receipts.length!==rules.length)
        throw Error('Candidate evaluations do not cover the frozen suite set.');
    const evidence=rules.map((rule,index)=>{
        const receipt=evaluationEvidence(receipts[index],rule,job.dispatch_id);
        if(receipt.checkpoint_sha256!==sha||
            !SHA256.test(String(receipt.suite_sha256||''))||
            receipt.trials<rule.min_trials||
            job.evaluation_suite_definitions?.[rule.suite_id]&&
                job.evaluation_suite_definitions[rule.suite_id]!==receipt.suite_sha256)
            throw Error('Candidate evaluation differs from its resumable checkpoint or frozen suite.');
        return receipt;
    });
    const suites=evidence.map(r=>({
        suite_id:r.suite_id,suite_sha256:r.suite_sha256,
        successes:r.successes,trials:r.trials,
    }));
    const score=evidence.reduce((n,r)=>n+r.successes/r.trials,0)/evidence.length;
    return {job_id:job.id,project_id:job.project_id,
        checkpoint_sha256:sha,suites,score,
        policy_signature:JSON.stringify(rules.map(rule=>[
            rule.suite_id,rule.min_trials,rule.min_success_rate,
        ]).sort(([a],[b])=>a.localeCompare(b))),
        comparison_signature:JSON.stringify(suites.map(r=>[
            r.suite_id,r.suite_sha256,r.trials,
        ]).sort(([a],[b])=>a.localeCompare(b))),
    };
}

// Native process exit is not optimizer completion. Accept the machine
// authenticated, independently verified source receipt as durable group
// evidence, bound to the exact original Hub lease and dispatch.
// A committed rank-zero checkpoint is not yet a released generation.
function verifiedReleaseRecovery(job,raw,state) {
    if(raw==null)return null;
    const grouped=job?.frozen_parameters?.optimizer_group===true;
    const keys=['checkpoint_sha256',...(grouped?
        ['input_sha256','model_consensus_sha256']:[])];
    if(state!=='failed'||job?.project_id!=='bees_native'||
        job.frozen_parameters?.continual_release!==true||!object(raw)||
        Object.keys(raw).sort().join(',')!==keys.slice().sort().join(',')||
        keys.some(key=>!SHA256.test(String(raw[key]||'')))||
        grouped&&job.resource_bundle?.index!==1)
        throw Error('Invalid native continual release recovery proof.');
    return copy(raw);
}
function verifiedOptimizerCompletion(job,raw,state) {
    if(raw==null)return null;
    const bundle=job?.resource_bundle;
    const keys=['group_id','node_rank','node_count','input_sha256',
        'model_consensus_sha256',
        ...(raw?.learner_work!==undefined?['learner_work']:[]),
        ...(raw?.historical_opponent_usage!==undefined?['historical_opponent_usage']:[]),
        ...(raw?.continual_release!==undefined?['continual_release']:[])];
    if(state!=='finished'||job?.frozen_parameters?.optimizer_group!==true||
        !bundle||!object(raw)||
        Object.keys(raw).sort().join(',')!==keys.sort().join(',')||
        raw.group_id!==bundle.id||
        raw.node_rank!==bundle.index-1||raw.node_count!==bundle.size||
        !SHA256.test(String(raw.input_sha256||''))||
        !SHA256.test(String(raw.model_consensus_sha256||'')))
        throw Error('Native optimizer completion differs from its frozen Hub owner and verified collective.');
    if(raw.learner_work!==undefined) {
        const work=raw.learner_work;
        const fields=['optimizer_input_steps','learner_wall_seconds',
            'trajectory_processing_wall_seconds','optimizer_update_wall_seconds',
            'learner_process_cpu_seconds','synchronized_minibatches'];
        if(bundle.index!==1||!object(work)||
            Object.keys(work).sort().join(',')!==fields.sort().join(',')||
            !Number.isSafeInteger(work.optimizer_input_steps)||
            work.optimizer_input_steps<1||
            !Number.isSafeInteger(work.synchronized_minibatches)||
            work.synchronized_minibatches<1||
            fields.slice(1,5).some(name=>
                typeof work[name]!=='number'||!Number.isFinite(work[name])||
                work[name]<0||work[name]>1e12)||
            work.learner_wall_seconds<=0||
            work.optimizer_update_wall_seconds<=0||
            work.trajectory_processing_wall_seconds+
                work.optimizer_update_wall_seconds>work.learner_wall_seconds+0.01)
            throw Error('Native publishing learner measurements differ from its synchronized completion.');
    }
    if(raw.historical_opponent_usage!==undefined) {
        const usage=raw.historical_opponent_usage;
        const selection=job.parent_collection_selection;
        const fields=['manifest_sha256','opponent_model_id','opponent_slot',
            'opponent_decision_rows','total_decision_rows'];
        if(bundle.index!==1||!selection?.opponent_model_id||!object(usage)||
            Object.keys(usage).sort().join(',')!==fields.sort().join(',')||
            usage.manifest_sha256!==job.expected_parent_manifest_sha256||
            usage.manifest_sha256!==selection.effective_manifest_sha256||
            usage.opponent_model_id!==selection.opponent_model_id||
            usage.opponent_slot!==selection.opponent_slot||
            !Number.isSafeInteger(usage.opponent_decision_rows)||
            usage.opponent_decision_rows<0||
            !Number.isSafeInteger(usage.total_decision_rows)||
            usage.total_decision_rows<1||
            usage.opponent_decision_rows>usage.total_decision_rows)
            throw Error('Native historical usage differs from its pinned physical collection.');
    }
    const requiresRelease=job.project_id==='bees_native'&&
        job.frozen_parameters?.continual_release===true;
    if(requiresRelease||raw.continual_release!==undefined) {
        const release=raw.continual_release;
        const fields=['checkpoint_sha256','parent_model_id','model_id','training_step',
            'evaluation_report_id','passed','deployment_id','deployed_model_id',
            ...(release?.release_attempt_wall_seconds!==undefined?['release_attempt_wall_seconds']:[]),
            ...(release?.collection_selection!==undefined?['collection_selection']:[])];
        const modelId=/^bees-rl-v[0-9]+-[0-9a-f]{24}$/;
        if(!requiresRelease||bundle.index!==1||!object(release)||
            Object.keys(release).sort().join(',')!==fields.sort().join(',')||
            !SHA256.test(String(release.checkpoint_sha256||''))||
            ![release.parent_model_id,release.model_id,release.deployed_model_id]
                .every(id=>modelId.test(String(id||'')))||
            !/^eval-[0-9a-f]{24}$/.test(String(release.evaluation_report_id||''))||
            !/^native-[0-9a-f]{24}$/.test(String(release.deployment_id||''))||
            !Number.isSafeInteger(release.training_step)||release.training_step<1||
            typeof release.passed!=='boolean'||
            (release.release_attempt_wall_seconds!==undefined&&
                (!Number.isFinite(release.release_attempt_wall_seconds)||
                 release.release_attempt_wall_seconds<0||release.release_attempt_wall_seconds>1e12)))
            throw Error('Native optimizer completion lacks the exact authorized generation release.');
        if(release.collection_selection!==undefined) {
            const selection=validateSelection(release.collection_selection);
            if(selection.input_checkpoint_sha256!==release.checkpoint_sha256||
                selection.opponent_slot!==job.frozen_parameters.refresh_opponent_slot)
                throw Error('Native historical selection differs from the frozen self-play route.');
        }
    }
    return copy(raw);
}

// Read-only multi-round measurement projection from durable, verified Hub
// decisions. These completed groups have already reconciled their original
// physical collector receipts, synchronized optimizer and final evaluations.
// Never use chart-cache samples for training provenance or stopping decisions.
function nativeBeesRoundTrainingSummary(plan) {
    const rounds=plan.round_history||[];
    let worldSteps=0,policyRows=0,collectorSeconds=0,modelForwards=0;
    let optimizerRows=0,learnerSeconds=0,ppoSeconds=0,processingSeconds=0;
    let learnerCpuSeconds=0,collectorCoverage=0,learnerCoverage=0;
    let releaseCoverage=0,promotions=0,releaseSeconds=0,releaseTimingCoverage=0;
    let historicalSelections=0,historicalOpponents=0;
    let historicalRoundsMeasured=0,historicalRoundsPlayed=0,historicalRows=0,historicalTotalRows=0;
    const evaluations=new Map();
    for(const entry of rounds) {
        const work=entry.collector_work_totals;
        if(work&&work.scope==='sum_of_independent_native_collectors') {
            worldSteps+=work.successful_world_steps;
            policyRows+=work.successful_policy_rows;
            collectorSeconds+=work.sum_collector_wall_seconds;
            modelForwards+=work.verified_gpu_model_forwards;
            collectorCoverage++;
        }
        const learner=entry.optimizer_group?.learner_work;
        if(learner) {
            optimizerRows+=learner.optimizer_input_steps;
            learnerSeconds+=learner.learner_wall_seconds;
            ppoSeconds+=learner.optimizer_update_wall_seconds;
            processingSeconds+=learner.trajectory_processing_wall_seconds;
            learnerCpuSeconds+=learner.learner_process_cpu_seconds;
            learnerCoverage++;
        }
        const usage=entry.optimizer_group?.historical_opponent_usage;
        if(usage) {
            historicalRoundsMeasured++;
            if(usage.opponent_decision_rows>0)historicalRoundsPlayed++;
            historicalRows+=usage.opponent_decision_rows;
            historicalTotalRows+=usage.total_decision_rows;
        }
        const release=entry.continual_release;
        if(release) {
            releaseCoverage++;
            if(release.passed)promotions++;
            if(release.collection_selection) {
                historicalSelections++;
                if(release.collection_selection.opponent_model_id!==null)historicalOpponents++;
            }
            if(Number.isFinite(release.release_attempt_wall_seconds)) {
                releaseSeconds+=release.release_attempt_wall_seconds;
                releaseTimingCoverage++;
            }
        }
        for(const receipt of entry.evaluation_receipts||[]) {
            // A suite name alone cannot establish comparability across
            // checkpoints. Group only identical frozen suite definitions.
            const key=receipt.suite_id+':'+receipt.suite_sha256+':'+receipt.trials;
            if(!evaluations.has(key))evaluations.set(key,{
                suite_id:receipt.suite_id,suite_sha256:receipt.suite_sha256,
                trials_per_checkpoint:receipt.trials,
                measured_rounds:0,latest_round:null,latest_success_rate:null,
                latest_checkpoint_sha256:null,best_success_rate:null,
            });
            const suite=evaluations.get(key),rate=receipt.successes/receipt.trials;
            suite.measured_rounds++;
            suite.latest_round=entry.round;
            suite.latest_success_rate=rate;
            suite.latest_checkpoint_sha256=receipt.checkpoint_sha256;
            suite.best_success_rate=suite.best_success_rate===null
                ?rate:Math.max(suite.best_success_rate,rate);
        }
    }
    for(const receipt of plan.initial_evaluation_receipts||[]) {
        const suite=evaluations.get(
            receipt.suite_id+':'+receipt.suite_sha256+':'+receipt.trials);
        if(suite)suite.initial_checkpoint_success_rate=
            receipt.successes/receipt.trials;
    }
    return {
        scope:'verified_native_hub_multi_round_history',
        completed_rounds:rounds.length,
        target_rounds:plan.round_count,
        latest_checkpoint_sha256:rounds.at(-1)?.checkpoint_sha256||null,
        collector_rounds_measured:collectorCoverage,
        learner_rounds_measured:learnerCoverage,
        successful_world_steps:worldSteps,
        successful_policy_rows:policyRows,
        verified_gpu_model_forwards:modelForwards,
        // Collector seconds are overlapping occupied time, never fleet
        // elapsed seconds or an end-to-end training SPS denominator.
        sum_independent_collector_wall_seconds:collectorSeconds,
        world_steps_per_occupied_collector_second:
            collectorSeconds>0?worldSteps/collectorSeconds:null,
        optimizer_input_steps_measured:optimizerRows,
        publishing_learner_wall_seconds:learnerSeconds,
        ppo_update_wall_seconds:ppoSeconds,
        trajectory_processing_wall_seconds:processingSeconds,
        publishing_learner_process_cpu_seconds:learnerCpuSeconds,
        optimizer_input_rows_per_ppo_update_second:
            ppoSeconds>0?optimizerRows/ppoSeconds:null,
        continual_generations_released:releaseCoverage,
        continual_promotions_passed:promotions,
        continual_historical_selections:historicalSelections,
        continual_historical_opponents_selected:historicalOpponents,
        historical_opponent_rounds_measured:historicalRoundsMeasured,
        historical_opponent_rounds_played:historicalRoundsPlayed,
        historical_opponent_decision_rows:historicalRows,
        historical_opponent_measured_decision_rows:historicalTotalRows,
        continual_release_attempts_timed:releaseTimingCoverage,
        sum_completed_continual_release_attempt_wall_seconds:releaseSeconds,
        independent_evaluations:[...evaluations.values()],
    };
}

// Rank zero alone owns the published checkpoint. A successful Finish
// must prove optimizer consensus and durable model identity before its
// lease is released. A confirmed native Stop has no valid consensus
// checkpoint to publish: the terminal OS receipt itself settles ownership.
function nativeOptimizerTerminalProof(job) {
    if(job.status==='stopped'||job.frozen_parameters?.optimizer_group!==true)
        return true;
    if(!job.optimizer_completion)return false;
    return job.project_id!=='bees_native'||
        job.checkpoint_ready_verified===true&&
        SHA256.test(String(job.checkpoint_model_sha256||''))&&
        (job.frozen_parameters?.continual_release!==true||
            job.optimizer_completion.continual_release?.checkpoint_sha256===
                job.checkpoint_model_sha256);
}

// A distributed Bees checkpoint is suitable for continuation only after
// every originally leased optimizer machine has returned a verified terminal
// worker result and released its own native claim. Rank-zero checkpoint
// consensus alone does not prove that remote worker ownership was settled.
function nativeOptimizerGroupBlock(snapshot,parent) {
    if(parent?.project_id!=='bees_native'||
        parent.frozen_parameters?.optimizer_group!==true)return null;
    const group=parent.resource_bundle;
    if(!group||group.index!==1||!UUID.test(String(group.id||''))||
        !Number.isSafeInteger(group.size)||group.size<2||group.size>8)
        return 'The distributed optimizer checkpoint has no original Hub owner group.';
    const members=snapshot.jobs.filter(job=>job.resource_bundle?.id===group.id)
        .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
    const owner=members[0]?.optimizer_completion;
    if(members.length!==group.size||
        !owner||!nativeOptimizerTerminalProof(parent)||
        !SHA256.test(String(owner.input_sha256||''))||
        !SHA256.test(String(owner.model_consensus_sha256||''))||
        members.some((job,index)=>job.resource_bundle.index!==index+1||
            job.project_id!==(index===0?'bees_native':'bees_native_optimizer_worker')||
            job.status!=='finished'||!job.dispatch_id||
            !job.execution_updated_at||!job.capacity_released_at||
            !job.optimizer_completion||
            job.optimizer_completion.group_id!==group.id||
            job.optimizer_completion.node_rank!==index||
            job.optimizer_completion.node_count!==group.size||
            job.optimizer_completion.input_sha256!==owner.input_sha256||
            job.optimizer_completion.model_consensus_sha256!==
                owner.model_consensus_sha256))
        return 'Distributed PPO checkpoint continuation requires every original optimizer rank to agree on training inputs and learned model state, finish and release its lease.';
    return null;
}

// A pinned successor model source must retain verified checkpoint ownership
// and the full original distributed group must have completed and released.
function nativeModelSourceBlock(snapshot,job) {
    if(!job?.model_source_job_id)return null;
    const source=snapshot.jobs.find(item=>item.id===job.model_source_job_id);
    if(!source||source.id===job.id||source.project_id!=='bees_native'||
        source.status!=='finished'||!checkpointContinuationComplete(source)||
        !source.capacity_released_at||!source.dispatch_id||
        !source.execution_updated_at||
        !SHA256.test(String(source.checkpoint_model_sha256||''))||
        source.checkpoint_model_sha256!==job.expected_parent_model_sha256||
        (job.expected_parent_manifest_sha256!==undefined&&
            !SHA256.test(String(job.expected_parent_manifest_sha256)))||
        (source.optimizer_completion?.continual_release?.collection_selection&&
            job.expected_parent_manifest_sha256!==undefined&&
            job.expected_parent_manifest_sha256!==source.optimizer_completion
                .continual_release.collection_selection.effective_manifest_sha256))
        return 'The selected native model source has no verified, released checkpoint matching the job pin.';
    return nativeOptimizerGroupBlock(snapshot,source);
}

// A failed collector resumes its already issued physical work order with
// a new Hub job/lease. This is not a model checkpoint continuation: the
// captured World belongs to the immutable collection ID and same machine.
function collectorReplacementBlock(snapshot,job) {
    if(!job?.collector_replacement_of_job_id)return null;
    const parent=snapshot.jobs.find(item=>
        item.id===job.collector_replacement_of_job_id);
    if(job.project_id!=='bees_native_collect'||!parent||
        parent.id===job.id||parent.project_id!==job.project_id||
        parent.status!=='failed'||!parent.capacity_released_at||
        !parent.dispatch_id||!parent.start_first_offered_at||
        !parent.execution_updated_at||!parent.assigned_machine_id||
        parent.configuration_id!==job.configuration_id||
        parent.configuration_revision!==job.configuration_revision||
        parent.configuration_sha256!==job.configuration_sha256||
        JSON.stringify(parent.frozen_parameters)!==
            JSON.stringify(job.frozen_parameters)||
        parent.resources?.gpu_count!==job.resources?.gpu_count||
        parent.resources?.gpu_mode!==job.resources?.gpu_mode||
        (parent.model_source_job_id||null)!==
            (job.model_source_job_id||null)||
        (parent.expected_parent_model_sha256||null)!==
            (job.expected_parent_model_sha256||null)||
        (parent.expected_parent_manifest_sha256||null)!==
            (job.expected_parent_manifest_sha256||null)||
        job.continuation_machine_id!==parent.assigned_machine_id||
        job.artifact_lineage_root_id!==(parent.artifact_lineage_root_id||parent.id)||
        job.assigned_machine_id&&
            job.assigned_machine_id!==parent.assigned_machine_id||
        !snapshot.reservations.some(lease=>
            lease.id===parent.lease_id&&lease.job_id===parent.id&&
            lease.project_id===parent.project_id&&
            lease.machine_id===parent.assigned_machine_id&&
            lease.issued_by==='experiment-hub'&&
            lease.status==='released'&&Boolean(lease.released_at))||
        snapshot.jobs.some(other=>other.id!==job.id&&
            other.collector_replacement_of_job_id===parent.id&&
            other.status!=='cancelled'))
        return 'Collector replacement requires a failed, released, same-machine physical predecessor and identical frozen work order.';
    if(job.resources.cpus<
        job.frozen_parameters.rollout_workers*
        job.frozen_parameters.physics_workers)
        return 'Replacement CPU lease cannot execute its pinned native rollout allocation.';
    return null;
}

// Shared by planning, capacity claims, automation and native Start.
function predecessorBlock(snapshot,job) {
    const replacement=collectorReplacementBlock(snapshot,job);
    if(replacement)return replacement;
    if(job.continuation_of_job_id&&job.after_job_id)
        return 'A job cannot both continue a checkpoint and follow an independent predecessor.';
    if(job.continuation_of_job_id) {
        const parent=snapshot.jobs.find(j=>j.id===job.continuation_of_job_id);
        if(!parent||parent.project_id!==job.project_id||
            !checkpointContinuationComplete(parent)||!parent.capacity_released_at||
            !parent.assigned_machine_id||
            (parent.artifact_lineage_root_id||parent.id)!==
                job.artifact_lineage_root_id||
            job.continuation_machine_id!==parent.assigned_machine_id||
            (job.assigned_machine_id&&
                job.assigned_machine_id!==parent.assigned_machine_id))
            return 'Continuation lineage, completed parent, or original machine is not verified.';
        const groupBlock=nativeOptimizerGroupBlock(snapshot,parent);
        if(groupBlock)return groupBlock;
    }
    if(job.after_job_id) {
        const parent=snapshot.jobs.find(j=>j.id===job.after_job_id);
        // The release records readiness atomically. A prior lease can later
        // be archived, so do not require the historical lease for each claim.
        if(!independentSequenceComplete(parent)||
            !parent.capacity_released_at||!parent.dispatch_id||
            !parent.execution_updated_at||
            typeof job.predecessor_ready_at!=='string'||
            !Number.isFinite(Date.parse(job.predecessor_ready_at))||
            job.artifact_lineage_root_id!==job.id||
            job.continuation_machine_id)
            return 'Independent predecessor has not completed and released verified native capacity.';
        const groupBlock=nativeOptimizerGroupBlock(snapshot,parent);
        if(groupBlock)return groupBlock;
    }
    return null;
}
// Pure, non-authorizing readiness projection. The dashboard and the actual
// start authorization share one eligibility contract; displaying readiness
// does not claim a reservation or dispatch any native process.
function nativeStartReadiness(snapshot, job) {
    const block=reason=>({eligible:false,reason});
    if(job?.distributed_abort_requested_at)
        return block('The distributed group is aborting; no new learner Start is permitted.');
    if(!job||job.status!=='waiting_adapter'||
        job.adapter!=='unconfigured'||['bees','ants'].includes(job.project_id))
        return block('Only queued non-Bees jobs support Hub native starts.');
    const predecessor=predecessorBlock(snapshot,job);
    if(predecessor)return block(predecessor);
    const modelSource=nativeModelSourceBlock(snapshot,job);
    if(modelSource)return block(modelSource);
    if(!job.lease_id)
        return block('Coordinate machine capacity before requesting Start.');
    const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
        l.job_id===job.id&&l.project_id===job.project_id &&
        l.machine_id===job.assigned_machine_id&&
        l.issued_by==='experiment-hub'&&l.status==='acknowledged');
    if(!lease||!lease.receipt_fresh)
        return block('A fresh, acknowledged matching machine lease is required.');
    const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
    if(!machine||machine.status!=='connected')
        return block('The assigned machine must have a fresh authenticated heartbeat.');
    if(!['linux','win32'].includes(machine.agent_platform)||
        machine.local_executor_enabled!==true)
        return block('A supported locally installed native executor is required.');
    if(!machine.installed_projects?.includes(job.project_id))
        return block('The assigned machine has no trusted adapter for this project.');
    if(machine.native_preflight_passed!==true)
        return block('The local CPU/RAM supervisor preflight has not passed.');
    if(machine.agent_platform==='linux'&&
        (machine.execution_capabilities?.cgroup_v2_detected!==true||
            !['cpu','memory'].every(c=>
                machine.execution_capabilities?.cgroup_controllers.includes(c))))
        return block('Linux cgroup v2 CPU and memory controllers are unavailable.');
    if(!supportsTinyBrainsProfile(machine,job))
        return block('The assigned worker does not approve this TinyBrains profile, GPU mode, checkpoint import and update count.');
    if(!distributedRolloutHost(snapshot,job,machine))
        return block('The remote collector is not assigned to an active learner and its pinned physical endpoint.');
    if(job.project_id==='tinybrains'&&job.frozen_parameters?.remote_rollouts===true) {
        const count=machine.tinybrains_remote_rollout_count;
        const expectedEndpoints=machine.tinybrains_remote_rollout_endpoints;
        const keyFingerprint=machine.tinybrains_remote_key_fingerprint;
        if(!Number.isSafeInteger(count)||count<1||count>8||
            !Array.isArray(expectedEndpoints)||expectedEndpoints.length!==count||
            expectedEndpoints.some(endpoint=>!rolloutEndpoint(endpoint))||
            new Set(expectedEndpoints).size!==count||
            !SHA256.test(String(keyFingerprint||'')))
            return block('The learner has no complete, locally pinned remote CPU collector identities.');
        const workers=snapshot.jobs.filter(worker=>
            worker.project_id==='tinybrains_rollout'&&
            worker.frozen_parameters?.learner_job_id===job.id&&
            worker.status!=='cancelled');
        if(workers.length!==count)
            return block('The learner requires exactly '+count+' separately reserved remote CPU collector jobs.');
        const hosts=new Set([machine.id]);
        const matchedEndpoints=new Set();
        for(const worker of workers) {
            // A persisted "running" status is not proof the service is still
            // present. Require a recent authenticated owner observation too.
            if(worker.status!=='running'||worker.live_execution?.state!=='running'||
                worker.live_execution.dispatch_id!==worker.dispatch_id||
                worker.capacity_released_at||
                !worker.dispatch_id||![0,1].includes(worker.resources?.gpu_count)||
                (worker.resources.gpu_count===1&&worker.resources.gpu_mode!=='exclusive'))
                return block('Every remote CPU collector must be running under its own active Hub job.');
            if(!worker.assigned_machine_id||hosts.has(worker.assigned_machine_id))
                return block('Every remote CPU collector requires a distinct worker machine.');
            hosts.add(worker.assigned_machine_id);
            const host=snapshot.machines.find(m=>m.id===worker.assigned_machine_id);
            const owned=snapshot.reservations.find(l=>
                l.id===worker.lease_id&&l.job_id===worker.id&&
                l.project_id==='tinybrains_rollout'&&
                l.machine_id===worker.assigned_machine_id&&
                l.issued_by==='experiment-hub'&&
                l.status==='acknowledged'&&l.receipt_fresh===true);
            if(!host||host.status!=='connected'||host.local_executor_enabled!==true||
                host.native_preflight_passed!==true||
                !host.installed_projects?.includes('tinybrains_rollout')||!owned||
                nativeResourceBlock(worker,owned)||
                gpuStartBlock(worker,owned,host))
                return block('The remote collector has lost its authenticated host or CPU/RAM ownership.');
            if(!expectedEndpoints.includes(host.tinybrains_rollout_endpoint)||
                matchedEndpoints.has(host.tinybrains_rollout_endpoint)||
                host.tinybrains_rollout_key_fingerprint!==keyFingerprint)
                return block('Remote CPU collector endpoint or key identity differs from the learner\'s pinned worker set.');
            matchedEndpoints.add(host.tinybrains_rollout_endpoint);
        }
    }
    if(!supportsEvaluation(machine.evaluation_suites,job))
        return block('The assigned adapter does not advertise all required evaluation suites.');
    const r=job.resources;
    if(!r||r.ram_mb<128)
        return block('Native Start requires at least 128 MiB RAM.');
    // Recovered jobs and leases must meet the original resource contract,
    // not just the CPU-only shortcut in the native readiness projection.
    const resourceBlock=nativeResourceBlock(job,lease);
    if(resourceBlock)return block(resourceBlock);
    const gpuBlock=gpuStartBlock(job,lease,machine);
    if(gpuBlock)return block(gpuBlock);
    if(r.cpus>machine.reported_cpus||r.ram_mb>machine.reported_ram_mb)
        return block('Requested CPU/RAM exceeds the machine\'s fresh reported capacity.');
    const bound=lease.gpu_bindings?.[0];
    const device=bound?machine.observed_gpus?.find(g=>
        g.pci_address===bound.pci_address):null;
    const headroom=startHeadroom(r,machine.reported_available_ram_mb,device);
    if(headroom)return block(headroom);
    return {eligible:true,reason:r.gpu_count
        ?machine.agent_platform==='win32'
            ?r.gpu_mode==='shared'
                ?'Eligible for cooperative shared CUDA Start: PyTorch allocator VRAM ceiling; GPU compute share is advisory.'
                :'Eligible for explicit cooperative CUDA Start; Windows cannot isolate GPU access.'
            :r.gpu_mode==='shared'
                ?'Eligible for cooperative ROCm Start: PyTorch allocator VRAM ceiling; GPU compute budget is advisory.'
                :'Eligible for explicit exclusive GPU Start with in-service verification.'
        :'Eligible for explicit CPU-only native Start authorization.'};
}
// Match the operator's cross-project FIFO fairness in both the read-only
// preview and the actual advisory claim pass.
function fairPending(jobs,lastProjectId=null) {
    const buckets=new Map();
    for(const job of jobs) {
        if(!buckets.has(job.project_id))buckets.set(job.project_id,[]);
        buckets.get(job.project_id).push(job);
    }
    // The read-only preview and manual allocation retain their stable order.
    // A one-transition automation tick resumes after the previously served
    // project instead of resetting its priority on every invocation.
    const rotated=[...buckets];
    const after=rotated.findIndex(([id])=>id===lastProjectId);
    if(after>=0)rotated.push(...rotated.splice(0,after+1));
    const ordered=[];
    while(rotated.some(([,bucket])=>bucket.length)) {
        for(const [,bucket] of rotated) {
            if(bucket.length)ordered.push(bucket.shift());
        }
    }
    return ordered;
}
// Read-only pipeline diagnosis, never an execution authorization.
function waitingStageReason(job,byId) {
    const visited=new Set([job.id]);
    let dependent=job;
    for(let depth=1;depth<=1000;depth++) {
        const parentId=dependent.continuation_of_job_id||dependent.after_job_id;
        const parent=byId.get(parentId);
        if(!parent||visited.has(parentId))
            return {status:'broken_lineage',depth,
                reason:'Prequeued stage has missing or cyclic predecessor lineage; operator review required.'};
        visited.add(parentId);
        if(parent.status==='waiting_predecessor') {
            dependent=parent;
            continue;
        }
        const label=parent.configuration_name||parent.id.slice(-8);
        let status,explanation;
        if(parent.status==='waiting_adapter') {
            status='queued';explanation='has not started';
        }else if(['start_requested','launch_requested'].includes(parent.status)) {
            status='starting';explanation='is starting';
        }else if(parent.status==='running') {
            status='running';explanation='is still running';
        }else if(parent.status==='stop_requested') {
            status='stopping';explanation='has a pending Stop request; automatic advancement is blocked';
        }else if(parent.status==='finished'&&dependent.continuation_of_job_id&&
            !checkpointContinuationComplete(parent)) {
            status='needs_review';
            explanation='finished without a verified resumable checkpoint; checkpoint continuation is blocked';
        }else if(parent.status==='finished'&&!parent.capacity_released_at) {
            status='release_pending';explanation='finished; capacity release is required';
        }else if(parent.status==='stopped') {
            if(dependent.after_job_id) {
                if(independentSequenceComplete(parent)&&!parent.capacity_released_at) {
                    status='release_pending';
                    explanation='met its evaluation stop target; verified capacity release is required';
                }else {
                    status='needs_review';
                    explanation='stopped without an eligible released evaluation result; dependent jobs remain blocked';
                }
            }else {
                status='needs_review';
                explanation=checkpointContinuationComplete(parent)
                    ?'saved a checkpoint on Stop; cancel the pending stage before queuing an explicit continuation'
                    :'stopped without a verified resumable checkpoint';
            }
        }else {
            status='needs_review';explanation=
                'is '+parent.status+'; pipeline will not auto-advance without operator review';
        }
        return {status,depth,blocked_by_job_id:parent.id,
            blocked_by_status:parent.status,
            reason:'Stage '+label+' '+explanation+'.'};
    }
    return {status:'broken_lineage',depth:1000,
        reason:'Prequeued pipeline depth exceeds the job-history bound; operator review required.'};
}
// A remote collector has one physical endpoint approved by the learner's
// machine-local adapter. CPU capacity alone cannot authorize another host:
// a wrong claim would strand the learner and could launch an unrelated service.
function distributedRolloutHost(snapshot,job,machine) {
    if(job.project_id!=='tinybrains_rollout')return true;
    const parent=snapshot.jobs.find(j=>j.id===job.frozen_parameters?.learner_job_id);
    if(!parent||parent.project_id!=='tinybrains'||
        parent.distributed_abort_requested_at||
        parent.frozen_parameters?.remote_rollouts!==true||
        !['waiting_adapter','start_requested','launch_requested','running'].includes(parent.status)||
        !parent.assigned_machine_id||!machine||
        machine.id===parent.assigned_machine_id||
        machine.status!=='connected'||
        !machine.installed_projects?.includes('tinybrains_rollout'))return false;
    const learner=snapshot.machines.find(m=>m.id===parent.assigned_machine_id);
    if(!learner||learner.status!=='connected'||
        !learner.tinybrains_remote_rollout_endpoints?.includes(machine.tinybrains_rollout_endpoint)||
        learner.tinybrains_remote_key_fingerprint!==machine.tinybrains_rollout_key_fingerprint)
        return false;
    if(!Number.isSafeInteger(machine.tinybrains_rollout_thread_limit)||
        machine.tinybrains_rollout_thread_limit<1||
        (job.frozen_parameters?.threads!==undefined&&
            (!Number.isSafeInteger(job.frozen_parameters.threads)||
                job.frozen_parameters.threads<1||
                job.frozen_parameters.threads>machine.tinybrains_rollout_thread_limit||
                job.frozen_parameters.threads>job.resources?.cpus)))return false;
    if(snapshot.jobs.some(other=>other.id!==job.id&&
        other.project_id==='tinybrains_rollout'&&
        other.status!=='cancelled'&&
        other.frozen_parameters?.learner_job_id===parent.id&&
        other.assigned_machine_id===machine.id))return false;
    return true;
}
function allocationBlockReason(machines,job,preferredMachineId=null) {
    const candidates=preferredMachineId
        ?machines.filter(machine=>machine.id===preferredMachineId):machines;
    if(!candidates.length)
        return preferredMachineId?'The selected worker is offline or unavailable.'
            :'No worker is connected.';
    if(job.project_id==='tinybrains'&&
        !candidates.some(machine=>supportsTinyBrainsProfile(machine,job)))
        return 'No eligible worker approves this TinyBrains profile, GPU mode, checkpoint import and update count.';
    return 'No eligible connected worker has enough remaining capacity.';
}
function allocationChoice(machines,reservations,job,preferredMachineId=null,
    following=[],futureMachinesFor=()=>machines,snapshot=null) {
    const eligibleOptions=(selected,hosts,preferred=null)=>{
        const eligible=hosts.filter(machine=>
            supportsEvaluation(machine.evaluation_suites,selected)&&
            supportsTinyBrainsProfile(machine,selected)&&
            (!snapshot||distributedRolloutHost(snapshot,selected,machine))&&
            (!preferred||preferred===machine.id)&&
            (!selected.continuation_machine_id||selected.continuation_machine_id===machine.id));
        const fits=resourcePlacements(eligible,reservations,selected.resources)
            .filter(p=>p.capacity_fits);
        const r=selected.resources;
        const cpuOnly=r.gpu_count===0&&!(r.gpus||[]).length;
        const exclusiveGpu=r.gpu_count===1&&r.gpu_mode==='exclusive';
        const nativeGpu=r.gpu_count===1&&['exclusive','shared'].includes(r.gpu_mode);
        const dualGpu=r.gpu_count===2&&r.gpu_mode==='exclusive'&&
            ['tinybrains','bees_native','bees_native_collect']
                .includes(selected.project_id);
        if(r.ram_mb<128||(!cpuOnly&&!nativeGpu&&!dualGpu))return fits;
        const ready=new Set(eligible.filter(machine=>
            machine.local_executor_enabled===true&&machine.native_preflight_passed===true&&
            machine.installed_projects?.includes(selected.project_id)&&
            (machine.agent_platform==='win32'||machine.agent_platform==='linux'&&
                machine.execution_capabilities?.cgroup_v2_detected===true&&
                ['cpu','memory'].every(controller=>
                    machine.execution_capabilities.cgroup_controllers.includes(controller)))&&
            (cpuOnly||machine.gpu_projects?.includes(selected.project_id)&&
                (machine.agent_platform==='win32'
                    ?machine.gpu_inventory_source==='nvidia-smi-v1'&&machine.observed_gpus?.length>=1&&
                        (r.gpu_mode!=='shared'||machine.gpu_shared_projects?.includes(selected.project_id))
                    :(r.gpu_count===2
                        ?dualGpu&&dualGpuApproved(selected,machine)
                        :r.gpu_mode==='exclusive'||r.gpu_mode==='shared'&&
                            machine.gpu_shared_projects?.includes(selected.project_id))&&
                        machine.observed_gpus?.length>=2))
        ).map(machine=>machine.id));
        const runnable=fits.filter(p=>ready.has(p.machine_id));
        if(runnable.length)return runnable;
        if(exclusiveGpu) {
            const optedIn=new Set(eligible.filter(machine=>
                machine.gpu_projects?.includes(selected.project_id)).map(machine=>machine.id));
            const optedInFits=fits.filter(p=>optedIn.has(p.machine_id));
            if(optedInFits.length)return optedInFits;
        }
        return fits;
    };
    const options=eligibleOptions(job,machines,preferredMachineId)
        .sort((a,b)=>
            Number(Boolean(a.observed_headroom_block))-
                Number(Boolean(b.observed_headroom_block))||
            observedLoadPreference(a,b,job.resources)||
            a.available.cpus-b.available.cpus||
            a.available.ram_mb-b.available.ram_mb||
            a.machine_id.localeCompare(b.machine_id));
    if(options.length<2||!following.length)return options[0]||null;
    // Preserve FIFO jobs while avoiding a placement that would block a
    // later job with exactly one capable host. This is advisory lookahead:
    // no lease, execution permission or native ownership is conferred.
    const soleOptions=following.filter(next=>!next.lease_id)
        .map(next=>{
            const places=eligibleOptions(next,futureMachinesFor(next));
            return places.length===1
                ?{job:next,machine_id:places[0].machine_id}:null;
        }).filter(Boolean);
    if(!soleOptions.length)return options[0];
    const remainingOptionLoss=option=>{
        const shadow=reservations.concat([{
            machine_id:option.machine_id,status:'offered',
            resources:{...job.resources,
                gpus:option.suggested_gpus},
        }]);
        let lost=0;
        const machine=machines.find(m=>m.id===option.machine_id);
        for(const later of soleOptions) {
            if(later.machine_id!==option.machine_id)continue;
            if(!resourcePlacements([machine],shadow,later.job.resources)[0]
                .capacity_fits)lost++;
        }
        return lost;
    };
    const loss=new Map(options.map(option=>[
        option.machine_id,remainingOptionLoss(option),
    ]));
    options.sort((a,b)=>
        Number(Boolean(a.observed_headroom_block))-
            Number(Boolean(b.observed_headroom_block))||
        loss.get(a.machine_id)-loss.get(b.machine_id)||
        observedLoadPreference(a,b,job.resources));
    return options[0];
}
function writeAtomic(file, value) {
    fs.mkdirSync(path.dirname(file), {recursive:true});
    const pending = file + '.' + process.pid + '.' + crypto.randomBytes(6).toString('hex') + '.tmp';
    try {
        fs.writeFileSync(pending, JSON.stringify(value, null, 2) + '\n', {flag:'wx'});
        fs.renameSync(pending, file);
    } finally {
        if (fs.existsSync(pending)) fs.unlinkSync(pending);
    }
}
function claimsFitCpuRam(reservations,machine,reportedCpus,reportedRam) {
    const outstanding=reservations.filter(lease=>lease.machine_id===machine.id&&
        ['active','offered','acknowledged'].includes(lease.status));
    return outstanding.every(lease=>
        Number.isSafeInteger(lease.resources?.cpus)&&lease.resources.cpus>0&&
        Number.isSafeInteger(lease.resources?.ram_mb)&&lease.resources.ram_mb>0)&&
        outstanding.reduce((sum,lease)=>sum+lease.resources.cpus,0)<=Math.min(machine.cpus,reportedCpus)&&
        outstanding.reduce((sum,lease)=>sum+lease.resources.ram_mb,0)<=Math.min(machine.ram_mb,reportedRam);
}

// Diagnostic CPU seconds preserve useful utilization measurements after
// the legacy nanosecond integer becomes unrepresentable over JSON.
function observedCpuSeconds(report) {
    if(Number.isFinite(report?.cpu_time_seconds)&&
        report.cpu_time_seconds>=0&&report.cpu_time_seconds<=1e12)
        return report.cpu_time_seconds;
    return Number.isSafeInteger(report?.cpu_time_ns)&&report.cpu_time_ns>=0
        ?report.cpu_time_ns/1e9:null;
}
function summarizeResourceSample(previous,report,observedAt) {
    if(!['running','stop_requested','finished','failed','stopped'].includes(report.state)||
        (observedCpuSeconds(report)===null&&report.memory_current_mb==null&&
         report.peak_job_memory_bytes==null&&
         report.cpu_throttled_seconds==null&&report.cpu_throttle_events==null&&
         report.cpu_quota_periods==null))
        return previous;
    const highest=(a,b)=>a==null?b??null:b==null?a:Math.max(a,b);
    return {
        first_observed_at:previous?.first_observed_at||observedAt,
        last_observed_at:observedAt,
        cpu_time_ns_high_water:highest(previous?.cpu_time_ns_high_water,report.cpu_time_ns),
        cpu_time_seconds_high_water:highest(
            previous?.cpu_time_seconds_high_water,observedCpuSeconds(report)),
        cpu_throttled_seconds_high_water:highest(
            previous?.cpu_throttled_seconds_high_water,report.cpu_throttled_seconds),
        cpu_throttle_events_high_water:highest(
            previous?.cpu_throttle_events_high_water,report.cpu_throttle_events),
        cpu_quota_periods_high_water:highest(
            previous?.cpu_quota_periods_high_water,report.cpu_quota_periods),
        peak_observed_memory_mb:highest(previous?.peak_observed_memory_mb,report.memory_current_mb),
        peak_job_memory_bytes:highest(previous?.peak_job_memory_bytes,report.peak_job_memory_bytes),
    };
}

function gpuProbeHoldsClaim(job) {
    const proof=job.gpu_qualification;
    return Boolean(job.lease_id&&proof?.lease_id===job.lease_id&&
        (proof.cleanup_required||
            (proof.status==='requested'&&proof.first_offered_at!==null)));
}

// The whole original bundle is the unit of operator authorization.
// Validate exact membership and frozen member order before any draft is changed.
function exactResourceBundle(snapshot,value,confirmation) {
    if(!object(value)||value[confirmation]!==true||
        !UUID.test(String(value.bundle_id||''))||
        !Array.isArray(value.expected_job_ids)||
        value.expected_job_ids.length<2||value.expected_job_ids.length>16||
        value.expected_job_ids.some(id=>!UUID.test(String(id)))||
        new Set(value.expected_job_ids).size!==value.expected_job_ids.length)
        throw Error('Confirm the exact 2–16 resource-bundle members and operation.');
    const members=snapshot.jobs.filter(job=>job.resource_bundle?.id===value.bundle_id)
        .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
    if(members.length!==value.expected_job_ids.length||
        members.some((job,index)=>job.resource_bundle.index!==index+1||
            job.resource_bundle.size!==members.length||
            job.id!==value.expected_job_ids[index]))
        throw Error('Bundle membership or order changed; refresh before authorizing an action.');
    return members;
}

class ExperimentHub {
    constructor(root) {
        this.file = path.join(root, 'hub-v1.json');
        this.state = fs.existsSync(this.file) ? JSON.parse(fs.readFileSync(this.file, 'utf8')) : {
            version:1,
            projects:[{id:'bees', name:'Bees', adapter:'bees', description:'Distributed Unity reinforcement learning'}],
            configurations:[],
            jobs:[],
            machines:[],
            reservations:[],
        };
        // Heartbeats are ephemeral. After a coordinator restart, a machine is
        // not considered connected until it authenticates again.
        this.agentReports = new Map();
        // Receipt freshness is process-local, never inferred after restart.
        // Persisted leases remain capacity-blocking until explicitly released.
        this.leaseReceipts = new Map();
        // Native process usage observations are ephemeral, not registry history.
        this.executionTelemetry = new Map();
        this.progressHistory = new ProgressHistory(path.join(root,'hub-chart-v1.json'));
        this.observations = new ObservationArchive(root);
        this.executionOrderCursor = new Map();
        // Coordinated learner Starts are one-use durable approvals bound
        // to the exact already-reserved collector dispatches. A coordinator
        // restart revokes ordinary approvals; explicit recovery permission
        // still requires fresh native owner and GPU readiness proof.
        // Native queue automation is an explicitly armed, process-local
        // session. Durable opt-in recovery still requires fresh native and
        // lease proof before a new session can start unattended workloads.
        this.automation = {
            enabled:false,machine_ids:[],agent_instances:{},managed_job_ids:[],
            rebalance_queued:false,start_qualified_gpu:false,claim_exclusive_gpu:false,probe_exclusive_gpu:false,claimed_lease_ids:[],
            session_id:null,
            armed_at:null,last_tick_at:null,last_action:null,last_error:null,
            last_transition_at:null,waiting_reason:null,
            last_claimed_project_id:null,last_started_project_id:null,
        };
        const s = this.state;
        if(s.automation_restart)
            this.automation.last_action='Opted-in CPU automation awaiting fresh native reconciliation after coordinator restart.';
        if (!object(s) || s.version !== 1 ||
            !['projects','configurations','jobs','machines','reservations'].every(key=>Array.isArray(s[key])))
            throw Error('Experiment Hub registry invalid; refusing to start.');
        if (!s.projects.some(p=>p.id==='bees' && p.adapter==='bees'))
            throw Error('Experiment Hub Bees project identity changed; refusing to start.');
        // No new native Start is issued on startup. Only explicitly
        // restart-approved plans remain pending; fresh agents must later
        // re-prove their exact native ownership and original leases.
        const resetBundles=(s.bundle_start_plans||[]).some(plan=>
            plan.status==='pending'&&plan.resume_after_restart!==true);
        const resetDistributed=(s.distributed_launch_plans||[]).some(plan=>
            plan.status==='pending');
        const resetQueuedDistributed=(s.distributed_group_plans||[]).some(plan=>
            plan.status==='pending');
        const resetNativeRounds=(s.native_round_plans||[]).some(plan=>
            ['pending_collectors','awaiting_collectors',
             'pending_optimizer','awaiting_optimizer'].includes(plan.status));
        if(resetBundles||resetDistributed||resetQueuedDistributed||resetNativeRounds) {
            const next=copy(s);
            // A restart cannot silently authorize a new GPU-intensive
            // collection or learner stage, even when old native jobs remain
            // supervised on their original machines.
            for(const plan of next.native_round_plans||[])
                if(['pending_collectors','awaiting_collectors',
                    'pending_optimizer','awaiting_optimizer'].includes(plan.status)) {
                    plan.restart_phase=plan.status;
                    plan.status='disarmed';
                    plan.reason='Coordinator restarted; continuing this training sequence requires explicit renewed authorization.';
                    plan.updated_at=now();
                }
            for(const plan of next.bundle_start_plans||[])
                if(plan.status==='pending'&&plan.resume_after_restart!==true) {
                    plan.status='disarmed';
                    plan.reason='Coordinator restarted; this launch requires a new explicit approval.';
                    plan.resolved_at=now();
                }
            for(const plan of next.distributed_launch_plans||[])
                if(plan.status==='pending') {
                    if(plan.resume_after_restart===true) {
                        plan.recovery_pending=true;
                        plan.reason='Coordinator restarted; waiting for fresh native collector and learner proof.';
                    }else {
                        plan.status='disarmed';
                        plan.reason='Coordinator restarted without explicit learner recovery approval.';
                        plan.resolved_at=now();
                    }
                }
            for(const plan of next.distributed_group_plans||[])
                if(plan.status==='pending') {
                    if(plan.resume_after_restart===true) {
                        plan.recovery_pending=true;
                        plan.reason='Coordinator restarted; waiting for fresh native capacity and GPU proof.';
                    }else {
                        plan.status='disarmed';
                        plan.reason='Coordinator restarted without a future distributed group Start approval.';
                        plan.resolved_at=now();
                    }
                }
            this.commit(next);
        }
    }
    // Publish in-memory changes only after the complete registry is durably
    // written. A full disk must not leave the API believing a draft was saved.
    commit(next) {
        writeAtomic(this.file, next);
        this.state = next;
        // A released/pruned claim cannot retain an in-memory receipt.
        const outstanding=new Set(next.reservations.filter(r=>
            ['offered','acknowledged'].includes(r.status)).map(r=>r.id));
        for(const id of this.leaseReceipts.keys())
            if(!outstanding.has(id))this.leaseReceipts.delete(id);
    }
    snapshot() {
        const snapshot = copy(this.state);
        snapshot.evaluator_services=(snapshot.evaluator_services||[]).map(
            ({agent_instance_id,...item})=>({
                ...item,
                // A coordinator/agent restart cannot replay a prior
                // machine-owned service command. The dashboard may request
                // a NEW explicit command once its fresh agent reconnects.
                pending_fresh:item.status==='pending'&&
                    this.agentReports.get(item.machine_id)?.instance_id===
                        agent_instance_id,
            }));
        snapshot.hub_observation_archive_issue=this.observations.error;
        snapshot.native_round_plans=(snapshot.native_round_plans||[]).map(plan=>{
            const {agent_instances,...display}=plan;
            return {...display,
                training_summary:nativeBeesRoundTrainingSummary(plan),
            };
        });
        snapshot.bundle_start_plans=(snapshot.bundle_start_plans||[]).map(plan=>{
            if(plan.status!=='pending')return plan;
            const jobs=plan.member_job_ids.map(id=>snapshot.jobs.find(job=>job.id===id));
            return {...plan,waiting_reason:jobs.some(job=>!job)
                ?'A saved bundle member is missing.'
                :jobs.some(job=>job.status!=='waiting_adapter'||job.dispatch_id)
                    ?'A member changed native status; plan needs review.'
                :'Waiting for matching fresh machine lease receipts and native Start readiness.'};
        });
        snapshot.distributed_group_plans=(snapshot.distributed_group_plans||[]).map(plan=>({
            id:plan.id,learner_job_id:plan.learner_job_id,
            collector_job_ids:plan.collector_job_ids.slice(),
            status:plan.status,reason:plan.reason,
            expires_at:plan.expires_at,
            resume_after_restart:plan.resume_after_restart===true,
            recovery_pending:plan.recovery_pending===true,
        }));
        snapshot.distributed_launches=(snapshot.distributed_launch_plans||[]).map(intent=>({
            learner_job_id:intent.learner_job_id,
            collector_job_ids:Object.keys(intent.collector_dispatches),
            status:intent.status,reason:intent.reason,
            approved_at:intent.approved_at,
            expires_at:intent.expires_at,
            recovery_pending:intent.recovery_pending===true,
            resume_after_restart:intent.resume_after_restart===true,
        }));
        delete snapshot.distributed_launch_plans;
        // Preserve existing Ants-v2 registry provenance without presenting
        // it as a supported executable project.
        snapshot.projects=snapshot.projects.map(project=>project.id==='ants'
            ?{...project,name:'Ants-v2 (retired)',retired:true}:project);
        const observedAt = Date.now();
        snapshot.machines = snapshot.machines.map(machine => {
            const report = this.agentReports.get(machine.id);
            const enrolled = typeof machine.agent_key_sha256 === 'string';
            const fresh = enrolled && report &&
                observedAt >= report.received_ms &&
                observedAt - report.received_ms < AGENT_HEARTBEAT_TTL_MS;
            // Never leak even a credential verifier through any dashboard API.
            delete machine.agent_key_sha256;
            machine.agent_enrolled = enrolled;
            machine.status = !enrolled ? 'unverified' : fresh ? 'connected' : 'offline';
            machine.last_seen_at = report
                ? new Date(report.received_ms).toISOString() : null;
            machine.gpu_inventory_source=fresh?report.gpu_inventory_source:'none';
            machine.observed_gpus=fresh?copy(report.gpu_devices):[];
            // Only PCI-address-bound configured GPUs enter the planner.
            // A manually named GPU without a matching physical identity
            // remains advisory and unavailable for even a Hub capacity claim.
            machine.verified_gpus=fresh?machine.gpus.flatMap(configured=>{
                if(!configured.pci_address)return [];
                const actual=report.gpu_devices.find(g=>
                    g.pci_address===configured.pci_address);
                return actual?[{
                    id:configured.id,pci_address:configured.pci_address,
                    vram_mb:Math.min(configured.vram_mb,actual.vram_mb),
                    hub_vram_mb:Math.min(configured.hub_vram_mb??configured.vram_mb,
                        configured.vram_mb,actual.vram_mb),
                    hub_compute_pct:configured.hub_compute_pct??100,
                    observed_vram_mb:actual.vram_mb,
                    used_vram_mb:actual.used_vram_mb,
                    gpu_busy_pct:actual.gpu_busy_pct,
                    temperature_c:actual.temperature_c,
                    hotspot_temperature_c:actual.hotspot_temperature_c,
                    power_w:actual.power_w,
                    ...(actual.cuda_uuid?{cuda_uuid:actual.cuda_uuid}:{}),
                }]:[];
            }):[];
            if (fresh) {
                machine.reported_cpus = report.cpus;
                machine.reported_ram_mb = report.ram_mb;
                machine.reported_free_ram_mb = report.free_ram_mb;
                machine.reported_available_ram_mb = report.available_ram_mb;
                machine.host_cpu_busy_pct = report.host_cpu_busy_pct;
                machine.capacity_overcommitted=!claimsFitCpuRam(snapshot.reservations,machine,report.cpus,report.ram_mb);
                machine.agent_platform = report.platform;
                machine.execution_capabilities = copy(report.execution);
                machine.local_executor_enabled = report.local_executor;
                machine.native_preflight_passed = report.native_preflight_passed;
                machine.native_preflight_reason = report.native_preflight_reason;
                machine.installed_projects = report.installed_projects.slice();
                machine.evaluation_suites = copy(report.evaluation_suites);
                machine.native_bees_curriculum_sources=
                    copy(report.native_bees_curriculum_sources);
                machine.tinybrains_profiles = copy(report.tinybrains_profiles||{});
                machine.tinybrains_cuda_profiles=report.tinybrains_cuda_profiles.slice();
                machine.tinybrains_thread_overrides=report.tinybrains_thread_overrides.slice();
                machine.tinybrains_cpu_fallback_profiles=report.tinybrains_cpu_fallback_profiles.slice();
                machine.tinybrains_required_import_profiles=report.tinybrains_required_import_profiles.slice();
                machine.tinybrains_dual_gpu_profiles=report.tinybrains_dual_gpu_profiles.slice();
                machine.tinybrains_remote_rollout_profiles=report.tinybrains_remote_rollout_profiles.slice();
                machine.tinybrains_remote_rollout_count=report.tinybrains_remote_rollout_count;
                machine.tinybrains_remote_rollout_endpoints=report.tinybrains_remote_rollout_endpoints.slice();
                machine.tinybrains_remote_key_fingerprint=report.tinybrains_remote_key_fingerprint;
                machine.tinybrains_rollout_endpoint=report.tinybrains_rollout_endpoint;
                machine.tinybrains_rollout_key_fingerprint=report.tinybrains_rollout_key_fingerprint;
                machine.tinybrains_rollout_thread_limit=report.tinybrains_rollout_thread_limit;
                machine.tinybrains_imports=copy(report.tinybrains_imports);
                machine.gpu_projects = report.gpu_projects.slice();
                machine.gpu_shared_projects=report.gpu_shared_projects.slice();
                machine.gpu_dual_native_projects=report.gpu_dual_native_projects.slice();
            }
            return machine;
        });
        snapshot.jobs = snapshot.jobs.map(job=>{
            const observed=this.executionTelemetry.get(job.id);
            const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
            const age=observedAt-Date.parse(observed?.at||'');
            const live=observed&&machine?.status==='connected'&&
                age>=0&&age<AGENT_HEARTBEAT_TTL_MS?observed:null;
            return {...job,live_execution:live};
        });
        snapshot.reservations = snapshot.reservations.map(lease => ({
            ...lease,
            receipt_fresh:lease.status==='acknowledged' &&
                snapshot.machines.some(machine=>machine.id===lease.machine_id &&
                    machine.status==='connected') &&
                this.leaseReceipts.has(lease.id) &&
                observedAt >= this.leaseReceipts.get(lease.id) &&
                observedAt - this.leaseReceipts.get(lease.id) < AGENT_HEARTBEAT_TTL_MS,
        }));
        snapshot.jobs=snapshot.jobs.map(job=>{
            const proof=job.gpu_qualification;
            if(!proof)return job;
            proof.holds_claim=gpuProbeHoldsClaim(job);
            const machine=snapshot.machines.find(m=>m.id===proof.machine_id);
            const lease=snapshot.reservations.find(l=>l.id===proof.lease_id);
            const report=this.agentReports.get(proof.machine_id);
            const pinned=lease?.gpu_bindings;
            const stillMatched=Boolean(lease&&machine&&
                [1,2].includes(pinned?.length)&&
                proof.selected_pci_addresses?.length===pinned.length&&
                pinned.every((bound,index)=>
                    proof.selected_pci_addresses[index]===bound.pci_address&&
                    machine.verified_gpus?.some(g=>
                        g.pci_address===bound.pci_address&&
                        g.vram_mb>=bound.vram_mb)));
            proof.automation_paused=Boolean(proof.status==='requested'&&proof.automation_session_id&&
                !this.automationSessionCurrent(proof.automation_session_id,proof.machine_id));
            proof.current=Boolean(proof.status==='verified'&&
                ['waiting_adapter','start_requested'].includes(job.status)&&
                job.lease_id===proof.lease_id&&lease?.receipt_fresh&&
                machine?.status==='connected'&&
                machine.native_preflight_passed===true&&
                report?.instance_id===proof.agent_instance_id&&stillMatched);
            return job;
        });
        const jobsById=new Map(snapshot.jobs.map(job=>[job.id,job]));
        for(const job of snapshot.jobs) {
            // Keep independent-sequence eligibility a coordinator-owned
            // decision. The UI must not reimplement evidence validation.
            job.independent_sequence_complete=independentSequenceComplete(job);
            job.checkpoint_continuation_complete=checkpointContinuationComplete(job)&&
                !nativeOptimizerGroupBlock(snapshot,job);
            if(job.status==='waiting_predecessor')
                job.predecessor_wait=waitingStageReason(job,jobsById);
            if(job.status==='waiting_adapter'&&job.project_id!=='bees')
                job.native_start_readiness=nativeStartReadiness(snapshot,job);
            if(job.status==='waiting_adapter'&&job.lease_id&&
                [1,2].includes(job.resources?.gpu_count)) {
                const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                const machine=snapshot.machines.find(m=>m.id===job.assigned_machine_id);
                job.gpu_probe_block=gpuProbeBlock(job,lease,machine);
                job.gpu_probe_cleanup_ready=Boolean(gpuProbeWorkerReady(job,lease,machine)&&
                    job.gpu_qualification?.lease_id===lease?.id&&
                    job.gpu_qualification.cleanup_required&&job.gpu_qualification.status!=='requested');
            }
            if(job.status==='start_requested'&&job.automation_start_session_id)
                job.automation_start_paused=!(
                    this.automation.enabled&&
                    this.automation.session_id===job.automation_start_session_id&&
                    this.automation.agent_instances[job.assigned_machine_id]===
                        this.agentReports.get(job.assigned_machine_id)?.instance_id&&
                    snapshot.machines.some(m=>m.id===job.assigned_machine_id&&
                        m.status==='connected'));
        }
        snapshot.automation=this.automationStatus();
        return snapshot;
    }
    automationSessionCurrent(sessionId,machineId) {
        const instance=this.automation.agent_instances[machineId];
        return Boolean(instance&&this.automation.enabled&&this.automation.session_id===sessionId&&
            instance===this.agentReports.get(machineId)?.instance_id);
    }
    automationStatus() {
        const {agent_instances,managed_job_ids,claimed_lease_ids,session_id,...publicState}=this.automation;
        return {...copy(publicState),managed_jobs:managed_job_ids.length,
            resume_after_restart:this.automation.enabled&&
                Boolean(this.state.automation_restart),
            resume_after_agent_restart:this.automation.enabled&&
                this.state.automation_restart?.agent_restart===true,
            restart_rearm_pending:!this.automation.enabled&&
                Boolean(this.state.automation_restart)};
    }
    configureAutomation(value,origin='operator') {
        if(!['operator','restart'].includes(origin))
            throw Error('Unknown native automation authorization source.');
        if(!object(value)||typeof value.enabled!=='boolean')
            throw Error('Choose whether to arm or disarm native queue automation.');
        if(value.enabled===false) {
            if(value.confirm_disarm!==true)
                throw Error('Explicitly confirm disarming native automation.');
            this.automation.enabled=false;
            if(this.state.automation_restart) {
                const next=copy(this.state);
                delete next.automation_restart;
                this.commit(next);
            }
            this.automation={
                enabled:false,machine_ids:[],agent_instances:{},managed_job_ids:[],
                rebalance_queued:false,start_qualified_gpu:false,claim_exclusive_gpu:false,probe_exclusive_gpu:false,claimed_lease_ids:[],
                session_id:null,armed_at:null,last_tick_at:this.automation.last_tick_at,
                last_action:'Disarmed by operator.',last_error:null,
                last_transition_at:this.automation.last_transition_at,
                waiting_reason:null,
                last_claimed_project_id:null,last_started_project_id:null,
            };
            return this.automationStatus();
        }
        if(this.automation.enabled)
            throw Error('Native automation is already armed; disarm explicitly before starting another session.');
        if((this.state.candidate_selections||[])
            .some(p=>p.status==='needs_review'))
            throw Error('Cancel or resolve review-required candidate selections before arming native automation.');
        if(this.state.automation_restart&&origin!=='restart')
            throw Error('Restart restoration is pending; cancel it with Disarm before manually arming.');
        if(value.probe_exclusive_gpu===true&&value.claim_exclusive_gpu!==true)
            throw Error('Automatic GPU probes require automatic exclusive GPU claims.');
        if(value.resume_after_restart===true&&
            (value.confirm_restart_rearm!==true||
             value.start_qualified_gpu===true||
             value.claim_exclusive_gpu===true||
             value.probe_exclusive_gpu===true))
            throw Error('Unattended restart restoration needs explicit permission and CPU-only scheduling.');
        if(value.confirm_agent_restart_rearm===true&&value.resume_after_restart!==true)
            throw Error('Agent recovery requires explicitly authorized CPU-only restart restoration.');
        const ids=value.machine_ids;
        if(value.confirm_arm!==true||value.confirm_untracked_load!==true||
            !Array.isArray(ids)||ids.length<1||ids.length>16||
            ids.some(id=>typeof id!=='string'||!ID.test(id))||
            new Set(ids).size!==ids.length)
            throw Error('Explicitly confirm 1–16 distinct machines and responsibility for untracked host workloads.');
        if(value.adopt_reconciled_running===true&&
            value.confirm_adopt_running!==true)
            throw Error('Explicitly confirm adoption of previously managed running services.');
        const snapshot=this.snapshot();
        const agentInstances={};
        for(const id of ids) {
            const machine=snapshot.machines.find(m=>m.id===id);
            const report=this.agentReports.get(id);
            if(!machine||machine.status!=='connected'||
                !['linux','win32'].includes(machine.agent_platform)||
                machine.local_executor_enabled!==true||
                machine.native_preflight_passed!==true||
                !machine.installed_projects?.length||
                (machine.agent_platform==='linux'&&
                    (machine.execution_capabilities?.cgroup_v2_detected!==true||
                        !['cpu','memory'].every(c=>
                            machine.execution_capabilities?.cgroup_controllers.includes(c))))||
                !UUID.test(String(report?.instance_id||'')))
                throw Error('Every armed machine needs a fresh authenticated native agent, preflight and installed project adapter.');
            if(snapshot.jobs.some(j=>j.assigned_machine_id===id&&
                !j.capacity_released_at&&['unverified','failed'].includes(j.status)))
                throw Error('Resolve unverified or failed native jobs before arming this machine.');
            agentInstances[id]=report.instance_id;
        }
        const adopted=[];
        if(value.adopt_reconciled_running===true) {
            const observedAt=Date.now();
            const leases=new Map(snapshot.reservations.map(lease=>[lease.id,lease]));
            for(const job of snapshot.jobs) {
                if(!ids.includes(job.assigned_machine_id)||
                    !job.automation_start_session_id||job.capacity_released_at)
                    continue;
                // A prior automated launch remains owned by its original
                // supervisor. Only a freshly inspected running or terminal
                // dispatch may be re-adopted for capacity release; never
                // retry an old Start, Stop, or an ambiguous report.
                const lease=leases.get(job.lease_id);
                const observed=this.executionTelemetry.get(job.id);
                const age=observedAt-Date.parse(observed?.at||'');
                if(!['running','finished','stopped'].includes(job.status)||
                    (job.status==='running'&&job.stop_requested_at)||
                    !UUID.test(String(job.dispatch_id||''))||
                    !lease||lease.issued_by!=='experiment-hub'||
                    lease.job_id!==job.id||
                    lease.project_id!==job.project_id||
                    lease.machine_id!==job.assigned_machine_id||
                    lease.status!=='acknowledged'||lease.receipt_fresh!==true||
                    observed?.state!==job.status||
                    observed.dispatch_id!==job.dispatch_id||
                    observed.agent_instance_id!==
                        agentInstances[job.assigned_machine_id]||
                    !(age>=0&&age<AGENT_HEARTBEAT_TTL_MS))
                    throw Error('Prior automated job '+job.id+
                        ' is not a freshly verified native running or terminal dispatch. Resolve its Start, Stop, or capacity release before adoption.');
                adopted.push(job.id);
            }
        }
        const next=copy(this.state);
        if(value.resume_after_restart===true)
            next.automation_restart={
                machine_ids:ids.slice(),
                rebalance_queued:value.rebalance_queued===true,
                ...(value.confirm_agent_restart_rearm===true?{agent_restart:true}:{}),
                authorized_at:now(),
            };
        else delete next.automation_restart;
        if(JSON.stringify(next.automation_restart||null)!==
            JSON.stringify(this.state.automation_restart||null))
            this.commit(next);
        this.automation={
            enabled:true,machine_ids:ids.slice(),agent_instances:agentInstances,
            managed_job_ids:adopted,session_id:crypto.randomUUID(),
            rebalance_queued:value.rebalance_queued===true,claimed_lease_ids:[],
            start_qualified_gpu:value.start_qualified_gpu===true,
            claim_exclusive_gpu:value.claim_exclusive_gpu===true,
            probe_exclusive_gpu:value.probe_exclusive_gpu===true,
            armed_at:now(),last_tick_at:null,
            last_action:value.start_qualified_gpu===true
                ?'Armed for CPU jobs and explicitly qualified GPU claims; no job dispatched yet.'
                :value.claim_exclusive_gpu===true
                    ?'Armed for CPU jobs and opted-in GPU claims; GPU Starts require operator approval.'
                    :'Armed for qualified CPU-only jobs; no job dispatched yet.',
            last_error:null,last_transition_at:null,waiting_reason:null,
            last_claimed_project_id:null,last_started_project_id:null,
        };
        if(adopted.length)
            this.automation.last_action+=' Re-adopted '+adopted.length+
                ' freshly inspected native job(s) for terminal capacity release.';
        return this.automationStatus();
    }
    reconcileAutomationContinuity(observedAt) {
        for(const id of this.automation.machine_ids) {
            const report=this.agentReports.get(id);
            if(!report||observedAt<report.received_ms||
                observedAt-report.received_ms>=AGENT_HEARTBEAT_TTL_MS||
                report.instance_id!==this.automation.agent_instances[id]||
                report.native_preflight_passed!==true||report.local_executor!==true)
            {
                const message='Machine '+id+' lost qualified agent continuity.';
                if(this.state.automation_restart?.agent_restart===true) {
                    // Keep only the separately authorized CPU recovery intent.
                    // No dispatch resumes until the existing restoration path
                    // verifies fresh native ownership and matching leases.
                    this.automation.enabled=false;
                    this.automation.last_error=null;
                    this.automation.last_action='CPU automation paused for agent reconciliation.';
                    this.automation.waiting_reason=message+' Awaiting fresh native and lease proof.';
                }else this.disarmUnsafeAutomation(Error(message+' Operator must re-arm.'));
                return false;
            }
        }
        return true;
    }
    disarmUnsafeAutomation(error) {
        this.automation.enabled=false;
        this.automation.last_error=String(error.message||error).slice(0,240);
        this.automation.last_action='Automation disarmed; operator review required.';
        this.automation.waiting_reason=null;
        if(this.state.automation_restart) {
            const next=copy(this.state);
            delete next.automation_restart;
            this.commit(next);
        }
    }
    restoreOptedInAutomation() {
        const intent=this.state.automation_restart;
        if(!intent||this.automation.enabled)return;
        const reason=message=>{
            this.automation.waiting_reason=message;
            return;
        };
        // A restart provides no proof of current machine ownership.
        // Wait for every selected native agent and its lease receipts.
        const snapshot=this.snapshot(),nowMs=Date.now();
        for(const id of intent.machine_ids||[]) {
            const machine=snapshot.machines.find(item=>item.id===id);
            const report=this.agentReports.get(id);
            if(!report||!machine||machine.status!=='connected'||
                nowMs-report.received_ms>=AGENT_HEARTBEAT_TTL_MS)
                return reason('Restart restoration is awaiting a fresh authenticated agent on '+id+'.');
        }
        for(const job of snapshot.jobs) {
            if(!intent.machine_ids.includes(job.assigned_machine_id)||
                !job.automation_start_session_id||job.capacity_released_at)
                continue;
            if(['start_requested','launch_requested','stop_requested'].includes(job.status))
                return this.disarmUnsafeAutomation(Error('Prior automated job '+job.id+
                    ' has an uncertain in-flight Start or Stop after restart.'));
            if(['unverified','failed'].includes(job.status))
                return this.disarmUnsafeAutomation(Error('Prior automated job '+job.id+
                    ' needs operator review after restart.'));
            const observed=this.executionTelemetry.get(job.id),agent=this.agentReports.get(job.assigned_machine_id);
            const age=nowMs-Date.parse(observed?.at||'');
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id&&
                l.job_id===job.id&&l.status==='acknowledged'&&l.receipt_fresh===true);
            if(!observed||observed.dispatch_id!==job.dispatch_id||
                observed.agent_instance_id!==agent?.instance_id||
                observed.state!==job.status||
                !(age>=0&&age<AGENT_HEARTBEAT_TTL_MS)||!lease)
                return reason('Restart restoration is awaiting fresh native dispatch and lease proof for '+job.id+'.');
        }
        try {
            this.configureAutomation({
                enabled:true,machine_ids:intent.machine_ids,
                rebalance_queued:intent.rebalance_queued===true,
                adopt_reconciled_running:true,confirm_adopt_running:true,
                resume_after_restart:true,confirm_restart_rearm:true,
                confirm_agent_restart_rearm:intent.agent_restart===true,
                confirm_arm:true,confirm_untracked_load:true,
            },'restart');
            this.automation.last_action='Restored explicitly opted-in CPU automation after fresh native reconciliation.';
            this.automation.waiting_reason=null;
        }catch(error) {
            this.disarmUnsafeAutomation(error);
        }
    }
    // At most one durable transition per pass. Existing lifecycle methods
    // remain the only admission, start and release authorities. Stop/failed
    // recovery is deliberately manual; this never retries an uncertain job.
    tickAutomation() {
        if(!this.automation.enabled&&this.state.automation_restart) {
            this.restoreOptedInAutomation();
            // Recovery only arms the session. Never dispatch a new Start in
            // the same tick that first confirms restarted native ownership.
            return this.automationStatus();
        }
        const session=this.automation;
        if(!session.enabled)return this.automationStatus();
        session.last_tick_at=now();
        const transitioned=message=>{
            session.last_action=message;
            session.last_transition_at=now();
            session.waiting_reason=null;
            return this.automationStatus();
        };
        try {
            const snapshot=this.snapshot();
            const selected=snapshot.machines.filter(m=>
                session.machine_ids.includes(m.id));
            if(!this.reconcileAutomationContinuity(Date.now()))return this.automationStatus();
            const unsafe=snapshot.jobs.find(j=>
                session.machine_ids.includes(j.assigned_machine_id)&&
                !j.capacity_released_at&&
                ['unverified','failed'].includes(j.status));
            if(unsafe)throw Error('Job '+unsafe.id+' requires operator review before further automatic execution.');
            const complete=snapshot.jobs.find(j=>
                session.managed_job_ids.includes(j.id)&&
                session.machine_ids.includes(j.assigned_machine_id)&&
                !j.capacity_released_at&&j.execution_updated_at&&
                ['finished','stopped'].includes(j.status)&&
                snapshot.reservations.some(l=>l.id===j.lease_id&&
                    l.job_id===j.id&&l.project_id===j.project_id&&
                    l.machine_id===j.assigned_machine_id&&
                    l.issued_by==='experiment-hub'&&l.status==='acknowledged'));
            if(complete) {
                const blocked=snapshot.jobs.find(j=>
                    j.status==='waiting_predecessor'&&
                    (j.continuation_of_job_id===complete.id||
                     j.after_job_id===complete.id)&&
                    (!independentSequenceComplete(complete)||
                     (j.continuation_of_job_id===complete.id&&
                      !checkpointContinuationComplete(complete))));
                this.releaseLease({lease_id:complete.lease_id,confirm_release:true});
                session.managed_job_ids=session.managed_job_ids.filter(id=>id!==complete.id);
                if(blocked)throw Error('Job '+complete.id+
                    ' ended without an eligible stage handoff; stage '+
                    blocked.id+' is blocked after capacity release. Review the '+
                    (independentSequenceComplete(complete)
                        ?'resumable checkpoint proof.':'evaluation completion or manual Stop.'));
                return transitioned('Released verified capacity: '+complete.project_id+
                    ' / '+(complete.configuration_name||complete.id.slice(-8))+'.');
            }
            // A native terminal report can release capacity before the
            // scheduler observes it if release-on-terminal was enabled.
            const releasedParents=new Map(snapshot.jobs.filter(parent=>
                (parent.automation_start_session_id===session.session_id||
                 session.managed_job_ids.includes(parent.id))&&
                parent.capacity_released_at&&
                ['finished','stopped'].includes(parent.status))
                .map(parent=>[parent.id,parent]));
            const blocked=snapshot.jobs.find(j=>{
                if(j.status!=='waiting_predecessor')return false;
                const parent=releasedParents.get(
                    j.continuation_of_job_id||j.after_job_id);
                return Boolean(parent&&
                    (!independentSequenceComplete(parent)||
                     (j.continuation_of_job_id&&
                      !checkpointContinuationComplete(parent))));
            });
            if(blocked)throw Error('Job '+
                (blocked.continuation_of_job_id||blocked.after_job_id)+
                ' released capacity without an eligible stage handoff; stage '+
                blocked.id+' requires operator review.');
            // Fulfill at most one frozen candidate decision per pass.
            // Evaluation/checkpoint mismatches remain visible and cannot
            // silently promote a different native model.
            for(const plan of snapshot.candidate_selections||[]) {
                if(plan.status!=='waiting')continue;
                const candidates=plan.candidate_job_ids.map(id=>
                    snapshot.jobs.find(job=>job.id===id));
                const bad=candidates.some(j=>!j||
                    ['failed','unverified','cancelled'].includes(j.status));
                const completed=candidates.every(j=>j&&
                    ['finished','stopped'].includes(j.status)&&
                    j.capacity_released_at);
                if(!bad&&!completed)continue;
                if(!bad&&candidates.some(j=>missingFinalEvaluationRules(j).length)&&
                    Date.now()-Math.max(...candidates.map(j=>Date.parse(j.capacity_released_at)))<
                        FINAL_EVALUATION_GRACE_MS)
                    continue;
                try {
                    const decision=this.rankCandidates({
                        candidate_job_ids:plan.candidate_job_ids});
                    const result=this.queueBestCandidate({...plan,
                        selection_plan_id:plan.id,
                        expected_winner_job_id:decision.winner_job_id,
                        confirm_queue:true});
                    return transitioned('Promoted best evaluated '+plan.project_id+
                        ' candidate '+decision.winner_job_id+
                        '; queued exact-model continuation '+result.job.id+'.');
                }catch(error) {
                    const next=copy(this.state);
                    const current=(next.candidate_selections||[])
                        .find(item=>item.id===plan.id);
                    if(current&&current.status==='waiting') {
                        current.status='needs_review';
                        current.error=String(error.message||error).slice(0,200);
                        current.resolved_at=now();
                        this.commit(next);
                    }
                    throw Error('Candidate selection '+plan.id+
                        ' needs review: '+String(error.message||error));
                }
            }
            // Frozen group plans have their own all-member atomic Start
            // gate. Generic queue automation must neither issue an early
            // Start for a collector nor race the approved learner barrier.
            const reservedForGroupStarts=new Set([
                ...(this.state.bundle_start_plans||[])
                    .filter(plan=>['pending','needs_review'].includes(plan.status))
                    .flatMap(plan=>plan.member_job_ids),
                ...(this.state.distributed_group_plans||[])
                    .filter(plan=>['pending','needs_review'].includes(plan.status))
                    .flatMap(plan=>[plan.learner_job_id,...plan.collector_job_ids]),
                ...(this.state.distributed_launch_plans||[])
                    .filter(plan=>plan.status==='pending')
                    .map(plan=>plan.learner_job_id),
            ]);
            const pending=snapshot.jobs.filter(j=>
                !reservedForGroupStarts.has(j.id)&&
                j.adapter==='unconfigured'&&j.status==='waiting_adapter'&&
                !['bees','ants'].includes(j.project_id)&&
                !predecessorBlock(snapshot,j));
            // Claims and starts can become eligible at different times,
            // so fairness needs an independent cursor for each operation.
            const pendingStarts=fairPending(pending,session.last_started_project_id);
            const pendingClaims=fairPending(pending,session.last_claimed_project_id);
            const blockedMachines=new Set(snapshot.jobs.filter(j=>
                ['stop_requested','unverified','failed'].includes(j.status)&&
                !j.capacity_released_at).map(j=>j.assigned_machine_id));
            // A coordinator Start authorization is durable and can outlive
            // the next scheduler pass. Wait for its native acknowledgement
            // before authorizing another service launch on the same host.
            const startingMachines=new Set(snapshot.jobs.filter(j=>
                ['start_requested','launch_requested'].includes(j.status)&&
                !j.capacity_released_at).map(j=>j.assigned_machine_id));
            for(const job of pendingStarts) {
                if((job.resources.gpu_count!==0&&!session.start_qualified_gpu)||!job.lease_id||
                    !session.machine_ids.includes(job.assigned_machine_id)||
                    blockedMachines.has(job.assigned_machine_id)||
                    startingMachines.has(job.assigned_machine_id)||
                    job.native_start_readiness?.eligible!==true)continue;
                this.authorizeStart({job_id:job.id,confirm_start:true},'automation');
                session.managed_job_ids.push(job.id);
                session.last_started_project_id=job.project_id;
                return transitioned('Authorized '+(job.resources.gpu_count?job.resources.gpu_mode+' GPU':'CPU-only')+
                    ' Start: '+job.project_id+
                    ' / '+(job.configuration_name||job.id.slice(-8))+'.');
            }
            const capable=selected.filter(m=>!blockedMachines.has(m.id)&&
                ['linux','win32'].includes(m.agent_platform)&&
                (m.agent_platform!=='linux'||
                    (m.execution_capabilities?.cgroup_v2_detected===true&&
                     ['cpu','memory'].every(c=>
                        m.execution_capabilities?.cgroup_controllers.includes(c)))));
            const overcommitted=new Set(selected.filter(m=>m.capacity_overcommitted).map(m=>m.id));
            const queuedLeases=new Set(pending.map(job=>job.lease_id));
            session.claimed_lease_ids=session.claimed_lease_ids.filter(id=>queuedLeases.has(id));
            const owned=new Set(session.claimed_lease_ids);
            const canHostJob=(machine,job)=>
                machine.installed_projects?.includes(job.project_id)&&
                supportsEvaluation(machine.evaluation_suites,job)&&
                supportsTinyBrainsProfile(machine,job)&&
                (job.resources.gpu_count===0||
                    machine.gpu_projects?.includes(job.project_id)&&
                    (machine.agent_platform==='linux'
                        ?(job.resources.gpu_count===2
                            ?dualGpuApproved(job,machine)
                            :job.resources.gpu_count===1&&
                                (job.resources.gpu_mode==='exclusive'||
                                    job.resources.gpu_mode==='shared'&&
                                        machine.gpu_shared_projects?.includes(job.project_id)))&&
                            machine.observed_gpus?.length>=2
                        :machine.agent_platform==='win32'&&
                            machine.gpu_inventory_source==='nvidia-smi-v1'&&
                            machine.observed_gpus?.length>=1&&
                            (job.resources.gpu_mode!=='shared'||
                                machine.gpu_shared_projects?.includes(job.project_id))));
            const eligibleHosts=job=>capable.filter(machine=>
                canHostJob(machine,job)&&distributedRolloutHost(snapshot,job,machine));
            if(session.rebalance_queued) {
                for(const job of pendingClaims) {
                    if(!owned.has(job.lease_id)||job.dispatch_id||
                        job.continuation_machine_id||job.resource_bundle)
                        continue;
                    const gpu=job.resources.gpu_count!==0;
                    // Keep any already requested or observed probe with its claim.
                    if(gpu&&job.gpu_qualification?.lease_id===job.lease_id)continue;
                    const source=selected.find(m=>m.id===job.assigned_machine_id);
                    const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                    if(!source||!lease)continue;
                    const gpuUnavailable=gpu&&physicalGpuBindings(source,{
                        gpu_inventory_source:source.gpu_inventory_source,
                        gpu_devices:source.observed_gpus,
                    },lease.resources,lease.gpu_bindings)===null;
                    const currentBinding=lease.gpu_bindings?.[0];
                    const observedGpu=currentBinding?source.observed_gpus?.find(g=>
                        g.pci_address===currentBinding.pci_address):null;
                    const physicalBlock=startHeadroom(job.resources,
                        source.reported_available_ram_mb,observedGpu);
                    if(!overcommitted.has(source.id)&&!gpuUnavailable&&
                        !physicalBlock&&canHostJob(source,job))
                        continue;
                    const eligible=eligibleHosts(job).filter(m=>
                        m.id!==source.id&&!overcommitted.has(m.id));
                    const chosen=allocationChoice(eligible,snapshot.reservations,job,
                        null,[],undefined,snapshot);
                    if(!chosen||chosen.observed_headroom_block)continue;
                    const moved=this.moveJobClaim({job_id:job.id,expected_lease_id:job.lease_id,
                        machine_id:chosen.machine_id,confirm_move:true});
                    session.claimed_lease_ids=session.claimed_lease_ids
                        .filter(id=>id!==moved.released_lease_id).concat(moved.lease_id);
                    return transitioned('Moved queued '+(gpu?'GPU':'CPU')+' claim from '+job.assigned_machine_id+
                        ' to '+moved.machine_id+'; awaiting a new receipt.');
                }
            }
            if(session.probe_exclusive_gpu) {
                for(const job of pendingClaims) {
                    if(![1,2].includes(job.resources.gpu_count)||
                        !(job.resources.gpu_mode==='exclusive'||
                            job.resources.gpu_mode==='shared'&&
                                snapshot.machines.find(m=>m.id===job.assigned_machine_id)
                                    ?.gpu_shared_projects?.includes(job.project_id))||
                        !owned.has(job.lease_id)||
                        job.gpu_qualification?.lease_id===job.lease_id||
                        startingMachines.has(job.assigned_machine_id))
                        continue;
                    const machine=capable.find(m=>m.id===job.assigned_machine_id);
                    const lease=snapshot.reservations.find(l=>l.id===job.lease_id);
                    // Linux alone has a kernel device-denial probe.
                    // Windows CUDA uses a PCI/UUID-pinned cooperative lease
                    // and must never dispatch a Linux-only probe.
                    if(machine?.agent_platform!=='linux'||
                        !machine.gpu_projects?.includes(job.project_id)||
                        (machine.observed_gpus?.length||0)<2||!lease?.receipt_fresh)
                        continue;
                    this.requestGpuQualification({job_id:job.id,confirm_qualify:true},'automation');
                    return transitioned('Requested GPU access probe on '+machine.id+
                        ' for '+job.configuration_name+'.');
                }
            }
            const claimable=job=>{
                const r=job.resources;
                return !job.lease_id&&r.ram_mb>=128&&(
                    r.gpu_count===0&&!(r.gpus||[]).length||
                    session.claim_exclusive_gpu&&(
                        r.gpu_count===1&&['exclusive','shared'].includes(r.gpu_mode)||
                        r.gpu_count===2&&r.gpu_mode==='exclusive'&&
                            ['tinybrains','bees_native','bees_native_collect']
                                .includes(job.project_id)));
            };
            for(const [index,job] of pendingClaims.entries()) {
                if(!claimable(job))continue;
                // Keep the fair project order while preserving sole fitting
                // hosts for later CPU or exclusive GPU jobs.
                const following=pendingClaims.slice(index+1).filter(claimable);
                const chosen=allocationChoice(eligibleHosts(job),snapshot.reservations,
                    job,null,following,eligibleHosts,snapshot);
                if(!chosen)continue;
                const result=this.allocateJobs({
                    all_projects:true,job_ids:[job.id],
                    machine_id:chosen.machine_id,confirm_allocate:true,
                });
                if(result.results[0]?.status!=='claimed')
                    throw Error('Selected placement did not produce an atomic claim.');
                if(session.rebalance_queued||job.resources.gpu_count!==0&&session.probe_exclusive_gpu)
                    session.claimed_lease_ids.push(result.results[0].lease_id);
                session.last_claimed_project_id=job.project_id;
                return transitioned((job.resources.gpu_count
                    ?'Claimed '+job.resources.gpu_mode+' GPU: ':'Claimed CPU/RAM: ')+
                    job.project_id+' / '+
                    (job.configuration_name||job.id.slice(-8))+
                    ' on '+chosen.machine_id+'; awaiting agent receipt.');
            }
            const manualComplete=snapshot.jobs.some(j=>
                session.machine_ids.includes(j.assigned_machine_id)&&
                !session.managed_job_ids.includes(j.id)&&!j.capacity_released_at&&
                ['finished','stopped'].includes(j.status)&&j.lease_id);
            session.waiting_reason=manualComplete
                ?'Completed jobs outside this automation session retain capacity until manually released.'
                :overcommitted.size
                    ?'Outstanding claims exceed CPU/RAM capacity on '+[...overcommitted].join(', ')+
                        '; move idle claims or release verified capacity.'
                    :session.start_qualified_gpu||session.claim_exclusive_gpu
                        ?'Awaiting capacity, agent receipts or a platform-specific GPU authorization.'
                        :'No eligible CPU-only transition; awaiting agent receipts or available capacity.';
            return this.automationStatus();
        }catch(error) {
            // An ambiguous state must never provoke repeated automatic
            // dispatches. Native services and claims remain untouched.
            this.disarmUnsafeAutomation(error);
            return this.automationStatus();
        }
    }
    evaluationTargets(projectId,machineId,suiteId) {
        this.project(projectId);
        if(projectId==='ants')return [];
        if(!ID.test(machineId||'')||!ID.test(suiteId||'')||
            !this.state.machines.some(machine=>machine.id===machineId))
            throw Error('Select a registered machine and evaluation suite.');
        const report=this.agentReports.get(machineId);
        const age=Date.now()-(report?.received_ms??0);
        // A persisted "running" record is not proof the worker is still
        // present. This is a read-only evaluator worklist, not a native
        // service or checkpoint authority.
        if(!report?.local_executor||age<0||age>=AGENT_HEARTBEAT_TTL_MS||
            !report.installed_projects?.includes(projectId)||
            !report.evaluation_suites?.[projectId]?.includes(suiteId))
            return [];
        const observedAt=Date.now();
        const snapshot=this.snapshot();
        const original=snapshot.jobs.filter(job=>{
            // Native liveness is per dispatch, not inferred from a fresh
            // agent heartbeat. Use the same 45-second observation window as
            // Monitor so a busy but uninspected service cannot remain
            // indefinitely eligible for independent evaluation.
            const observed=this.executionTelemetry.get(job.id);
            const age=observedAt-Date.parse(observed?.at||'');
            return job.project_id===projectId&&job.assigned_machine_id===machineId&&
                evaluationRules(job.evaluation_stop).some(rule=>rule.suite_id===suiteId)&&
                UUID.test(job.dispatch_id||'')&&UUID.test(job.artifact_lineage_root_id||job.id)&&
                (finalEvaluationPending(snapshot,job,observedAt)&&snapshot.reservations.some(lease=>
                    lease.id===job.lease_id&&lease.job_id===job.id&&lease.machine_id===machineId&&
                    lease.status==='released'&&lease.released_at)||
                (job.status==='running'&&observed?.dispatch_id===job.dispatch_id&&
                observed?.agent_instance_id===report.instance_id&&
                observed?.state==='running'&&
                age>=0&&age<AGENT_HEARTBEAT_TTL_MS));
        }).map(job=>{
            const rule=evaluationRules(job.evaluation_stop)
                .find(candidate=>candidate.suite_id===suiteId);
            const frozenSuite=rule.suite_sha256||
                job.evaluation_suite_definitions?.[rule.suite_id];
            return {job_id:job.id,dispatch_id:job.dispatch_id,
                artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                ...(frozenSuite?{suite_sha256:frozenSuite}:{}),
                ...(job.capacity_released_at?{final_checkpoint_sha256:job.checkpoint_model_sha256}:{}),
                min_trials:rule.min_trials,
                min_success_rate:rule.min_success_rate,
                required_suite_ids:evaluationRules(job.evaluation_stop)
                    .map(candidate=>candidate.suite_id)};
        });
        if(projectId!=='bees_native')return original;
        const initial=snapshot.jobs.flatMap(job=>{
            if(job.assigned_machine_id!==machineId)return [];
            const plan=nativeBaselinePlan(snapshot,job,observedAt);
            if(!plan||plan.agent_instances?.[machineId]!==report.instance_id)
                return [];
            const rule=evaluationRules(plan.initial_evaluation_stop)
                .find(candidate=>candidate.suite_id===suiteId);
            if(!rule)return [];
            if((plan.initial_evaluation_receipts||[]).some(receipt=>{
                try {
                    const checked=evaluationEvidence(receipt,rule,plan.id);
                    return checked.checkpoint_sha256===job.checkpoint_model_sha256&&
                        SHA256.test(String(checked.suite_sha256||''))&&
                        checked.trials>=rule.min_trials;
                }catch{return false;}
            }))return [];
            return [{
                job_id:job.id,dispatch_id:plan.id,
                artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                final_checkpoint_sha256:job.checkpoint_model_sha256,
                ...(rule.suite_sha256?{suite_sha256:rule.suite_sha256}:{}),
                min_trials:rule.min_trials,min_success_rate:rule.min_success_rate,
                required_suite_ids:evaluationRules(plan.initial_evaluation_stop)
                    .map(candidate=>candidate.suite_id),
            }];
        });
        return original.concat(initial);
    }
    hasPendingFinalEvaluations() {
        const snapshot=this.snapshot(),observedAt=Date.now();
        return snapshot.jobs.some(job=>
            finalEvaluationPending(snapshot,job,observedAt)||
            nativeBaselinePlan(snapshot,job,observedAt));
    }
    progressComparison(projectId) {
        this.project(projectId);
        return this.progressHistory.snapshot(projectId,this.state.jobs);
    }
    // These are conservative coordinator-side claims, NOT operating system
    // isolation or an authorization to execute a workload.
    // Build an offer in a caller-owned registry copy. A batch of job claims
    // can then be durably committed together or not at all.
    offerIn(next, projectId, machineId, wanted, jobId = null,
        machine=this.snapshot().machines.find(m=>m.id===machineId)) {
        const project=this.project(projectId);
        if(project.id==='ants')
            throw Error('Ants-v2 Hub integration is retired; no new capacity claims.');
        if(!machine || machine.id!==machineId || machine.status!=='connected')
            throw Error('Machine must have a currently authenticated agent.');
        const place=resourcePlacements([machine],next.reservations,wanted)[0];
        if(!place.online || !place.capacity_fits)
            throw Error('Requested capacity is unavailable on this machine.');
        if(next.reservations.filter(r=>
            ['active','offered','acknowledged'].includes(r.status)).length>=100)
            throw Error('Maximum simultaneous coordination claims reached.');
        const lease={
            id:crypto.randomUUID(),issued_by:'experiment-hub',
            project_id:project.id,machine_id:machine.id,
            status:'offered',created_at:now(),acknowledged_at:null,
            released_at:null,
            resources:{...wanted,gpus:place.suggested_gpus},
            gpu_bindings:place.suggested_gpus.map(id=>{
                const observed=machine.verified_gpus?.find(g=>g.id===id);
                if(!observed||!PCI.test(observed.pci_address))
                    throw Error('Cannot freeze physical GPU identity at lease allocation.');
                return {id,pci_address:observed.pci_address,
                    vram_mb:observed.vram_mb,
                    ...(observed.cuda_uuid?{cuda_uuid:observed.cuda_uuid}:{})};
            }),
        };
        if(jobId!==null)lease.job_id=jobId;
        // Terminal jobs still need their original lease to prove native
        // ownership on continuation, dependency creation and report replay.
        const retainedJobLeases=new Set(next.jobs
            .filter(job=>job.capacity_released_at&&job.lease_id)
            .map(job=>job.lease_id));
        const standaloneHistory=next.reservations.filter(r=>
            r.status==='released'&&!retainedJobLeases.has(r.id)).slice(-100);
        next.reservations=next.reservations.filter(r=>
            r.status!=='released'||retainedJobLeases.has(r.id))
            .concat(standaloneHistory,lease);
        return lease;
    }
    requestLease(value) {
        if(!object(value)||value.confirm_request!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Select a machine and explicitly confirm the coordination lease.');
        const next=copy(this.state);
        const lease=this.offerIn(next,value.project_id,value.machine_id,
            resourceRequest(value.resources));
        this.commit(next);
        return {...copy(lease),receipt_fresh:false};
    }
    // Releasing a Hub-owned job claim also unassigns its unstarted job
    // in the same durable update, so job and lease can never disagree.
    releaseIn(next, lease) {
        if(!lease || lease.issued_by!=='experiment-hub' ||
            !['offered','acknowledged'].includes(lease.status))
            throw Error('Only an outstanding Hub coordination lease can be released.');
        if(lease.job_id) {
            const job=next.jobs.find(j=>j.id===lease.job_id);
            if(!job || job.lease_id!==lease.id||
                !['waiting_adapter','finished','failed','stopped'].includes(job.status))
                throw Error('Cannot release an unknown, active, or unverified managed job.');
            if(job.status==='waiting_adapter') {
                if(job.native_release_recovery)
                    throw Error('Cancel the prepared release recovery before abandoning its pinned machine claim.');
                if(job.dispatch_id)throw Error('Cannot release a claim after native dispatch.');
                if(gpuProbeHoldsClaim(job))
                    throw Error('Wait for the offered GPU probe and verified cleanup before releasing its claim.');
                if(job.gpu_qualification?.status==='requested') {
                    job.gpu_qualification.status='cancelled';
                    job.gpu_qualification.diagnostic=
                        'Lease released before GPU access proof completed.';
                }
                job.lease_id=null;
                job.assigned_machine_id=null;
                job.assigned_at=null;
                job.status_reason='No allocation or trusted execution adapter is installed.';
            }else {
                // Retain historical machine and lease IDs for provenance after
                // a native terminal report has been received and reviewed.
                job.capacity_released_at=now();
                // Independent successors advance after a verified finish or
                // successful evaluation Stop. A checkpoint continuation may
                // also advance after evaluation Stop, but only if the native
                // supervisor verifies a checkpoint-preserving shutdown.
                // Manual Stops do not automatically advance any stage.
                if(independentSequenceComplete(job)) {
                    const children=next.jobs.filter(j=>
                        j.status==='waiting_predecessor'&&
                        (j.after_job_id===job.id||
                         (checkpointContinuationComplete(job)&&
                          j.continuation_of_job_id===job.id)));
                    if(children.length>1)throw Error(
                        'Multiple successor jobs claim one predecessor; refusing release.');
                    const child=children[0];
                    if(child?.continuation_of_job_id) {
                        if(child.project_id!==job.project_id||
                            child.artifact_lineage_root_id!==
                                (job.artifact_lineage_root_id||job.id)||
                            (job.continuation_machine_id&&
                                job.continuation_machine_id!==job.assigned_machine_id)||
                            (child.continuation_machine_id&&
                                child.continuation_machine_id!==job.assigned_machine_id))
                            throw Error('Prequeued stage lineage changed; refusing capacity release.');
                        child.continuation_machine_id=job.assigned_machine_id;
                    }else if(child) {
                        if(child.after_job_id!==job.id||
                            child.continuation_of_job_id||
                            child.artifact_lineage_root_id!==child.id||
                            child.continuation_machine_id)
                            throw Error('Independent sequence identity changed; refusing capacity release.');
                    }
                    if(child) {
                        child.status='waiting_adapter';
                        child.status_reason=job.status==='stopped'
                            ?child.continuation_of_job_id
                                ?'Evaluation target met; checkpoint-preserving Stop and capacity release verified. The next stage can now be allocated and started.'
                                :'Independent evaluation target met; native Stop verified and capacity released. Separate allocation and Start remain required.'
                            :'Predecessor finished and released verified capacity; separate allocation and Start remain required.';
                        child.predecessor_ready_at=now();
                    }
                }
            }
        }
        lease.status='released';
        lease.released_at=now();
        return lease;
    }
    releaseLease(value) {
        if(!object(value)||value.confirm_release!==true||
            typeof value.lease_id!=='string')
            throw Error('Explicitly confirm which lease should be released.');
        const next=copy(this.state);
        const lease=next.reservations.find(x=>x.id===value.lease_id &&
            x.issued_by==='experiment-hub');
        if(!lease)throw Error('Hub-issued lease not found.');
        if(lease.status!=='released') {
            this.releaseIn(next,lease);
            this.commit(next);
        }
        this.leaseReceipts.delete(lease.id);
        return {...copy(lease),receipt_fresh:false};
    }
    // Queue placement is operator-triggered and advisory, never a process launch.
    // All selected jobs, claim records, and linked IDs commit atomically.
    allocateJobs(value) {
        if(!object(value)||value.confirm_allocate!==true)
            throw Error('Explicitly confirm the advisory allocation.');
        const allProjects=value.all_projects===true;
        if(allProjects && value.project_id!=null)
            throw Error('Specify either all projects or one project.');
        const project=allProjects?null:this.project(value.project_id);
        if(project?.adapter==='bees')
            throw Error('Bees uses its existing coordinator, not Hub allocation.');
        if(project?.id==='ants')
            throw Error('Ants-v2 Hub integration is retired.');
        const requested=value.job_ids;
        if(requested!==undefined && (!Array.isArray(requested) ||
            requested.length<1 || requested.length>100 ||
            new Set(requested).size!==requested.length ||
            requested.some(id=>typeof id!=='string')))
            throw Error('Select 1–100 distinct pending job IDs.');
        const next=copy(this.state);
        const candidates=next.jobs.filter(j=>
            (allProjects||j.project_id===project.id) &&
            j.project_id!=='ants'&&j.adapter==='unconfigured'&&
            j.status==='waiting_adapter'&&!predecessorBlock(next,j)&&
            (requested===undefined || requested.includes(j.id)));
        // Fair interleaving across project queues, preserving FIFO within
        // each project. This is only a claim planner, not job execution.
        const jobs=fairPending(candidates);
        if(requested && jobs.length!==requested.length)
            throw Error('One or more selected jobs are unavailable or no longer pending.');
        if(!jobs.length)throw Error('No pending jobs to allocate.');
        if(value.machine_id!=null &&
            (!ID.test(String(value.machine_id))||jobs.length!==1))
            throw Error('A preferred machine may only be selected for one job.');
        const machines=this.snapshot().machines.filter(m=>m.status==='connected');
        const results=[];
        for(const [index,job] of jobs.entries()) {
            if(job.lease_id) {
                const lease=next.reservations.find(l=>l.id===job.lease_id &&
                    l.job_id===job.id && l.project_id===job.project_id &&
                    ['offered','acknowledged'].includes(l.status));
                if(!lease)throw Error('Job allocation ledger inconsistent; no changes committed.');
                results.push({job_id:job.id,machine_id:lease.machine_id,
                    lease_id:lease.id,status:'already_claimed'});
                continue;
            }
            const chosen=allocationChoice(machines,next.reservations,job,
                value.machine_id||null,jobs.slice(index+1),()=>machines,
                {machines,jobs:next.jobs});
            if(!chosen) {
                results.push({job_id:job.id,machine_id:null,lease_id:null,
                    status:'insufficient_capacity',
                    reason:allocationBlockReason(machines,job,value.machine_id)});
                continue;
            }
            const lease=this.claimJobIn(next,job,chosen.machine_id);
            results.push({job_id:job.id,machine_id:lease.machine_id,
                lease_id:lease.id,status:'claimed'});
        }
        if(results.some(r=>r.status==='claimed'))this.commit(next);
        return {results,executable:false,
            note:'Coordination only: no process started or hardware limits enforced.'};
    }
    claimJobIn(next,job,machineId,machine) {
        const host=machine||this.snapshot().machines.find(m=>m.id===machineId);
        if(!distributedRolloutHost({jobs:next.jobs,machines:this.snapshot().machines},job,host))
            throw Error('Remote collector must be placed on its learner-approved endpoint and distinct host.');
        if(job.collector_replacement_of_job_id&&
            (machineId!==job.continuation_machine_id||
             collectorReplacementBlock(next,job)))
            throw Error('Replacement native collector cannot move from its original inactive owner.');
        const lease=this.offerIn(next,job.project_id,machineId,
            resourceRequest(job.resources),job.id,host);
        job.lease_id=lease.id;
        job.assigned_machine_id=lease.machine_id;
        job.assigned_at=now();
        job.status_reason='Hub coordination capacity claimed; execution still blocked.';
        return lease;
    }
    moveJobClaim(value) {
        const result=this.moveJobClaims({moves:[value],confirm_move:value?.confirm_move});
        return result.moves[0];
    }
    moveJobClaims(value) {
        const moves=value?.moves;
        if(!object(value)||value.confirm_move!==true||!Array.isArray(moves)||
            moves.length<1||moves.length>100||moves.some(move=>!object(move)||
                !UUID.test(String(move.job_id||''))||
                !UUID.test(String(move.expected_lease_id||''))||
                !ID.test(String(move.machine_id||'')))||
            new Set(moves.map(move=>move.job_id)).size!==moves.length)
            throw Error('Select distinct queued claims and explicitly confirm their destinations.');
        const next=copy(this.state);
        const machines=new Map(this.snapshot().machines.map(machine=>[machine.id,machine]));
        const transfers=moves.map(move=>{
            const job=next.jobs.find(j=>j.id===move.job_id);
            if(!job||job.adapter!=='unconfigured'||job.status!=='waiting_adapter'||job.dispatch_id)
                throw Error('Only an unstarted non-Bees job claim can move.');
            if(job.native_release_recovery||job.resource_bundle||job.distributed_rollout_managed===true||
                job.project_id==='tinybrains_rollout'&&next.jobs.some(parent=>
                    parent.id===job.frozen_parameters?.learner_job_id&&
                    parent.distributed_rollout_managed===true))
                throw Error('Distributed group and resource-bundle machines are pinned; abandon the group before changing placement.');
            if(job.lease_id!==move.expected_lease_id)
                throw Error('A job claim changed. Refresh Train before moving it.');
            const predecessor=predecessorBlock(next,job);
            if(predecessor)throw Error(predecessor);
            const source=next.reservations.find(l=>l.id===job.lease_id&&l.job_id===job.id&&
                l.project_id===job.project_id&&l.machine_id===job.assigned_machine_id);
            if(!source||source.machine_id===move.machine_id)
                throw Error('Select a different destination for the current job claim.');
            if(job.continuation_machine_id&&job.continuation_machine_id!==move.machine_id)
                throw Error('Checkpoint continuation must stay on the original machine.');
            const machine=machines.get(move.machine_id);
            if(machine?.status!=='connected'||!machine.installed_projects?.includes(job.project_id)||
                !supportsEvaluation(machine.evaluation_suites,job)||!supportsTinyBrainsProfile(machine,job)||
                !distributedRolloutHost({machines:[...machines.values()],jobs:next.jobs},job,machine))
                throw Error('Destination requires a connected adapter supporting the project, configuration and evaluation suite.');
            return {job,source,machine};
        });
        // Release all sources only in the draft ledger so full hosts can swap.
        for(const {source} of transfers)this.releaseIn(next,source);
        // Reserve exact device requests before flexible ones at each destination.
        const ordered=transfers.slice().sort((a,b)=>
            Number(Boolean(b.job.resources.gpus?.length))-Number(Boolean(a.job.resources.gpus?.length))||
            (b.job.resources.min_gpu_vram_mb||0)-(a.job.resources.min_gpu_vram_mb||0)||
            (b.job.resources.gpu_count||0)-(a.job.resources.gpu_count||0));
        const replacements=new Map();
        for(const {job,machine} of ordered) {
            try{replacements.set(job.id,this.claimJobIn(next,job,machine.id,machine));}
            catch(error){
                throw Error('Cannot move '+job.configuration_name+' to '+machine.id+': '+error.message);
            }
        }
        this.commit(next);
        return {moves:transfers.map(({job,source,machine})=>({
            job_id:job.id,machine_id:machine.id,
            released_lease_id:source.id,lease_id:replacements.get(job.id).id,
        }))};
    }
    // Explicitly request bounded device access and runtime PCI proof, not a trainer launch.
    requestGpuQualification(value,origin='operator') {
        if(!object(value)||value.confirm_qualify!==true||
            !UUID.test(String(value.job_id||'')))
            throw Error('Explicitly confirm the job whose GPU access will be probed.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        const lease=snapshot.reservations.find(l=>l.id===job?.lease_id);
        const machine=snapshot.machines.find(m=>m.id===job?.assigned_machine_id);
        const cleanupOnly=value.confirm_cleanup===true;
        if(cleanupOnly) {
            if(origin!=='operator'||job?.gpu_probe_cleanup_ready!==true)
                throw Error('Cleanup recheck requires a held probe and its connected qualified worker.');
        }else {
            const block=gpuProbeBlock(job,lease,machine);
            if(block)throw Error(block);
        }
        if(origin==='native_round_release'&&!this.nativeRoundReleaseAuthorized(snapshot,job))
            throw Error('Release GPU probe requires the current authorized native round.');
        const session=this.automation;
        if(origin==='automation'&&(!session.probe_exclusive_gpu||
            !session.claimed_lease_ids.includes(lease.id)||
            !this.automationSessionCurrent(session.session_id,machine.id)))
            throw Error('This session no longer authorizes an automatic GPU probe.');
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        if(job.gpu_qualification?.status==='requested') {
            if(origin!=='operator'||!job.gpu_qualification.automation_paused||
                job.gpu_qualification.lease_id!==lease.id)
                throw Error('GPU access probe is already requested for this job.');
            delete selected.gpu_qualification.automation_session_id;
        }else selected.gpu_qualification={
            request_id:crypto.randomUUID(),lease_id:lease.id,
            machine_id:machine.id,status:'requested',
            requested_at:now(),first_offered_at:null,
            ...(cleanupOnly?{cleanup_only:true}:{}),
            ...(job.gpu_qualification?.lease_id===lease.id&&job.gpu_qualification.cleanup_required
                ?{cleanup_required:true}:{}),
            ...(origin==='automation'?{automation_session_id:session.session_id}:{}),
            ...(origin==='native_round_release'?{native_round_release:true}:{}),
        };
        this.commit(next);
        return {job_id:job.id,lease_id:lease.id,
            request_id:selected.gpu_qualification.request_id,
            status:'requested',starts_training:false};
    }
    withdrawGpuQualification(value) {
        if(!object(value)||value.confirm_withdraw!==true||
            !UUID.test(String(value.expected_request_id||'')))
            throw Error('Explicitly confirm which GPU access probe to withdraw.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        const proof=job?.gpu_qualification;
        if(job?.status!=='waiting_adapter'||!job.lease_id||job.dispatch_id||
            proof?.lease_id!==job.lease_id||proof.request_id!==value.expected_request_id||
            proof.status!=='requested'||proof.first_offered_at!==null)
            throw Error('Only a matching GPU probe that has never been offered can be withdrawn.');
        proof.status='cancelled';
        if(proof.native_round_release&&job.native_release_recovery)
            delete job.native_release_recovery.round_plan_id;
        proof.diagnostic='Operator withdrew this undelivered GPU access probe.';
        this.commit(next);
        return {job_id:job.id,request_id:proof.request_id,status:proof.status};
    }
    // A GPU preflight report never changes the job's execution state.
    recordGpuQualifications(next,machine,reports,instanceId) {
        if(!Array.isArray(reports)||reports.length>4)
            throw Error('Invalid GPU qualification report count.');
        const seen=new Set();
        let changed=false;
        for(const report of reports) {
            if(!object(report)||!UUID.test(String(report.job_id||''))||
                !UUID.test(String(report.lease_id||''))||
                !UUID.test(String(report.dispatch_id||''))||
                !['verified','rejected'].includes(report.result)||
                (report.cleanup_verified!==undefined&&typeof report.cleanup_verified!=='boolean')||
                (report.result==='verified'&&report.cleanup_verified===false)||
                seen.has(report.job_id)||
                !Array.isArray(report.selected_pci_addresses)||
                report.selected_pci_addresses.length>2||
                report.selected_pci_addresses.some(p=>!PCI.test(String(p)))||
                (report.diagnostic!=null&&
                    (typeof report.diagnostic!=='string'||report.diagnostic.length>160)))
                throw Error('Invalid bounded GPU preflight report.');
            seen.add(report.job_id);
            const job=next.jobs.find(j=>j.id===report.job_id);
            const lease=next.reservations.find(l=>l.id===report.lease_id);
            const request=job?.gpu_qualification;
            if(!job||!lease||lease.issued_by!=='experiment-hub'||
                lease.job_id!==job.id||lease.machine_id!==machine.id||
                lease.project_id!==job.project_id)
                throw Error('GPU proof does not belong to this job and machine.');
            // An acknowledged report may be retried after the response is
            // lost, even if the operator has since replaced the request or
            // released the lease. A stale receipt has no authority.
            if(lease.status==='released')continue;
            if(lease.status!=='acknowledged'||job.lease_id!==lease.id||
                job.assigned_machine_id!==machine.id)
                throw Error('GPU proof does not belong to this job and machine.');
            if(request?.lease_id!==lease.id||request.machine_id!==machine.id||
                request.request_id!==report.dispatch_id)
                continue;
            if(request.status!=='requested')continue;
            if(request.first_offered_at===null)
                throw Error('GPU access report arrived before its probe was offered.');
            if(job.status!=='waiting_adapter')
                throw Error('GPU access probe no longer belongs to a queued job.');
            if(request.cleanup_only&&report.result!=='rejected')
                throw Error('A cleanup recheck cannot qualify GPU access.');
            const pinned=lease.gpu_bindings;
            const verified=report.result==='verified'&&
                [1,2].includes(pinned?.length)&&
                report.selected_pci_addresses.length===pinned.length&&
                pinned.every((bound,index)=>
                    report.selected_pci_addresses[index]===bound.pci_address);
            if(report.result==='verified'&&!verified)
                throw Error('GPU proof does not match the frozen physical PCI allocation.');
            request.status=report.result;
            request.cleanup_required=!verified&&report.cleanup_verified!==true;
            request.diagnostic=report.diagnostic||null;
            request.observed_at=now();
            request.agent_instance_id=instanceId;
            request.selected_pci_addresses=verified
                ?report.selected_pci_addresses.slice():[];
            changed=true;
        }
        return changed;
    }

    nativeRoundReleaseAuthorized(snapshot,job) {
        const plan=(this.state.native_round_plans||[]).find(item=>
            item.status==='awaiting_optimizer'&&
            item.optimizer_job_ids?.[0]===job?.id&&
            item.optimizer_bundle_id===job?.resource_bundle?.id);
        return Boolean(plan&&job?.native_release_recovery?.round_plan_id===plan.id&&
            Object.entries(plan.agent_instances).every(([id,instance])=>
                this.agentReports.get(id)?.instance_id===instance&&
                snapshot.machines.find(machine=>machine.id===id)?.status==='connected')&&
            this.state.bundle_start_plans?.some(launch=>
                launch.bundle_id===plan.optimizer_bundle_id&&launch.status==='authorized'));
    }

    authorizeStart(value,origin='operator') {
        if(!['operator','automation','distributed','native_round_release'].includes(origin))
            throw Error('Unknown native Start authorization source.');
        if(!object(value)||value.confirm_start!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly confirm which queued job to start.');
        if((this.state.bundle_start_plans||[]).some(plan=>
            ['pending','needs_review'].includes(plan.status)&&
            plan.member_job_ids.includes(value.job_id)))
            throw Error('Cancel the approved bundle launch before starting one of its members separately.');
        if((this.state.distributed_group_plans||[]).some(plan=>
            ['pending','needs_review'].includes(plan.status)&&
            (plan.learner_job_id===value.job_id||
             plan.collector_job_ids.includes(value.job_id))))
            throw Error('Cancel the frozen distributed group plan before individually starting one of its members.');
        const snapshot=this.snapshot();
        const job=snapshot.jobs.find(j=>j.id===value.job_id);
        if(job?.frozen_parameters?.optimizer_group===true&&!job.native_release_recovery)
            throw Error('Synchronized native optimizer nodes must start together through their exact approved resource bundle.');
        if(origin==='native_round_release') {
            if(!this.nativeRoundReleaseAuthorized(snapshot,job))
                throw Error('Release-only Start requires the current authorized native round and original agent sessions.');
        }else if(job?.native_release_recovery&&origin!=='operator')
            throw Error('A release-only recovery requires its authorized round or explicit owner Start.');
        const readiness=nativeStartReadiness(snapshot,job);
        if(!readiness.eligible)
            throw Error(readiness.reason);
        if(origin==='automation'&&(
            (job.resources.gpu_count!==0&&!this.automation.start_qualified_gpu)||!this.automation.enabled||
            !this.automation.machine_ids.includes(job.assigned_machine_id)||
            !UUID.test(String(this.automation.session_id||''))))
            throw Error('Native automation session no longer authorizes this Start.');
        const pendingPlan=origin==='distributed'
            ?(this.state.distributed_launch_plans||[]).find(plan=>
                plan.learner_job_id===job.id&&plan.status==='pending')
            :null;
        if(origin==='distributed'&&!pendingPlan)
            throw Error('No live, approved distributed learner Start plan was found.');
        const next=copy(this.state);
        const selected=next.jobs.find(j=>j.id===job.id);
        selected.status='start_requested';
        if(origin==='automation')
            selected.automation_start_session_id=this.automation.session_id;
        if(selected.native_release_recovery) {
            // A manually authorized Start is independent of the original
            // plan. Unattended Start must remain authorized until offered.
            selected.native_release_recovery.start_origin=origin;
        }
        selected.dispatch_id=crypto.randomUUID();
        selected.start_requested_at=now();
        selected.start_first_offered_at=null;
        if(origin==='operator'&&value.release_on_terminal===true)
            selected.release_on_terminal=true;
        selected.status_reason='Start authorized; waiting for machine-local OS resource enforcement verification.';
        if(origin==='distributed') {
            const resolved=next.distributed_launch_plans.find(plan=>plan.id===pendingPlan.id);
            resolved.status='authorized';
            resolved.reason='All remote native owners freshly verified; one learner Start durably authorized.';
            resolved.resolved_at=now();
        }
        this.commit(next);
        return {id:selected.id,status:selected.status,dispatch_id:selected.dispatch_id};
    }
    // One frozen operator approval bridges group reservation to complete
    // collector/learner authorization. No native owner is created until all
    // original machine claims have fresh authenticated readiness.
    scheduleDistributedGroup(value) {
        if(!object(value)||value.confirm_schedule!==true||
            !UUID.test(String(value.learner_job_id||''))||
            !Array.isArray(value.expected_collector_job_ids)||
            value.expected_collector_job_ids.length<1||
            value.expected_collector_job_ids.length>8||
            value.expected_collector_job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.expected_collector_job_ids).size!==
                value.expected_collector_job_ids.length||
            Object.keys(value).some(key=>![
                'learner_job_id','expected_collector_job_ids','confirm_schedule',
                'confirm_learner_start','resume_after_restart','confirm_restart_rearm',
            ].includes(key))||
            value.confirm_learner_start!==true||
            (value.resume_after_restart===true
                ?value.confirm_restart_rearm!==true
                :value.confirm_restart_rearm!==undefined))
            throw Error('Confirm the exact remote collectors and one future native GPU learner Start.');
        const snapshot=this.snapshot();
        const learner=snapshot.jobs.find(job=>job.id===value.learner_job_id);
        if(!learner||learner.project_id!=='tinybrains'||
            learner.distributed_rollout_managed!==true||
            learner.frozen_parameters?.remote_rollouts!==true||
            learner.status!=='waiting_adapter'||learner.dispatch_id||
            learner.distributed_abort_requested_at||
            !learner.lease_id||predecessorBlock(snapshot,learner))
            throw Error('Only an unstarted, checkpoint-eligible distributed group can schedule future launch.');
        if((this.state.distributed_group_plans||[]).some(plan=>
            plan.learner_job_id===learner.id&&
            ['pending','needs_review'].includes(plan.status))||
            (this.state.distributed_launch_plans||[]).some(plan=>
                plan.learner_job_id===learner.id&&plan.status==='pending'))
            throw Error('Cancel the existing distributed Start approval before scheduling another.');
        const host=snapshot.machines.find(machine=>
            machine.id===learner.assigned_machine_id);
        const endpoints=host?.tinybrains_remote_rollout_endpoints;
        if(!host||host.status!=='connected'||!Array.isArray(endpoints)||
            endpoints.length!==value.expected_collector_job_ids.length||
            !SHA256.test(String(host.tinybrains_remote_key_fingerprint||''))||
            !host.installed_projects?.includes('tinybrains'))
            throw Error('The original GPU learner must have approved pinned collector endpoints.');
        const workers=snapshot.jobs.filter(job=>
            job.project_id==='tinybrains_rollout'&&
            job.frozen_parameters?.learner_job_id===learner.id);
        if(workers.length!==endpoints.length||
            workers.some(job=>!value.expected_collector_job_ids.includes(job.id)||
                job.status!=='waiting_adapter'||job.dispatch_id))
            throw Error('The full original collector membership must remain reserved and unstarted.');
        const ordered=value.expected_collector_job_ids.map(id=>
            workers.find(job=>job.id===id));
        const seen=new Set([learner.assigned_machine_id]);
        const all=[learner,...ordered];
        const members=all.map((job,index)=>{
            const machine=snapshot.machines.find(host=>host.id===job.assigned_machine_id);
            const lease=snapshot.reservations.find(item=>
                item.id===job.lease_id&&item.job_id===job.id&&
                item.machine_id===job.assigned_machine_id&&
                item.project_id===job.project_id&&
                item.issued_by==='experiment-hub'&&
                ['offered','acknowledged'].includes(item.status));
            if(!machine||!lease||!job.assigned_machine_id)
                throw Error('An original group member or Hub resource lease changed.');
            if(index!==0) {
                if(seen.has(machine.id)||
                    machine.tinybrains_rollout_endpoint!==endpoints[index-1]||
                    machine.tinybrains_rollout_key_fingerprint!==
                        host.tinybrains_remote_key_fingerprint)
                    throw Error('Remote machine/HMAC endpoint identity does not match the learner slot.');
                seen.add(machine.id);
            }
            return {job_id:job.id,lease_id:lease.id,machine_id:machine.id,
                ...(index?{endpoint:machine.tinybrains_rollout_endpoint}:{})};
        });
        const plans=copy(this.state.distributed_group_plans||[]);
        if(plans.length>=64) {
            const index=plans.findIndex(plan=>
                !['pending','needs_review'].includes(plan.status));
            if(index<0)throw Error('Resolve older distributed group approvals before adding another.');
            plans.splice(index,1);
        }
        const at=Date.now(),intent={
            id:crypto.randomUUID(),learner_job_id:learner.id,
            collector_job_ids:ordered.map(job=>job.id),
            frozen_members:members,
            learner_endpoints:endpoints.slice(),
            key_fingerprint:host.tinybrains_remote_key_fingerprint,
            agent_instances:Object.fromEntries(members.map(member=>[
                member.machine_id,
                this.agentReports.get(member.machine_id)?.instance_id||null,
            ])),
            resume_after_restart:value.resume_after_restart===true,
            recovery_pending:false,status:'pending',
            reason:'Waiting for every original group member to acknowledge native capacity and GPU access.',
            approved_at:new Date(at).toISOString(),
            expires_at:new Date(at+DISTRIBUTED_LAUNCH_TTL_MS).toISOString(),
        };
        plans.push(intent);
        const next=copy(this.state);next.distributed_group_plans=plans;
        this.commit(next);
        this.advancePendingDistributedGroups();
        return copy(this.state.distributed_group_plans.find(plan=>plan.id===intent.id));
    }
    cancelScheduledDistributedGroup(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.plan_id||''))||
            Object.keys(value).some(key=>!['plan_id','confirm_cancel'].includes(key)))
            throw Error('Explicitly confirm which future distributed launch to cancel.');
        const next=copy(this.state);
        const plan=(next.distributed_group_plans||[])
            .find(item=>item.id===value.plan_id&&
                ['pending','needs_review'].includes(item.status));
        if(!plan)throw Error('No unresolved distributed group approval with this identity.');
        plan.status='cancelled';
        plan.reason='Operator revoked the future group Start; reserved jobs and claims remain unchanged.';
        plan.resolved_at=now();
        this.commit(next);
        return {plan_id:plan.id,learner_job_id:plan.learner_job_id,
            status:plan.status,starts_authorized:0};
    }
    // A native heartbeat is the only trigger for future group admission:
    // no polling timers or speculative retries on unknown workers. The
    // existing collector-Start + learner-plan transaction stays authoritative.
    advancePendingDistributedGroups() {
        const pending=(this.state.distributed_group_plans||[])
            .filter(plan=>plan.status==='pending').map(plan=>plan.id);
        for(const id of pending) {
            const plan=(this.state.distributed_group_plans||[]).find(item=>
                item.id===id&&item.status==='pending');
            if(!plan)continue;
            const halt=(status,reason)=>{
                const next=copy(this.state);
                const current=(next.distributed_group_plans||[])
                    .find(item=>item.id===id&&item.status==='pending');
                if(!current)return;
                current.status=status;
                current.reason=reason;
                current.resolved_at=now();
                this.commit(next);
            };
            if(Date.now()>=Date.parse(plan.expires_at)) {
                halt('expired','Future distributed group authorization expired; no native Starts were created.');
                continue;
            }
            const snapshot=this.snapshot();
            const members=plan.frozen_members.map(owner=>
                snapshot.jobs.find(job=>job.id===owner.job_id));
            if(members.some((job,index)=>!job||
                job.lease_id!==plan.frozen_members[index].lease_id||
                job.assigned_machine_id!==plan.frozen_members[index].machine_id||
                job.status!=='waiting_adapter'||job.dispatch_id||
                job.distributed_abort_requested_at)) {
                halt('needs_review','A group member, native status or original machine lease changed.');
                continue;
            }
            const learner=members[0];
            const other=snapshot.jobs.filter(job=>
                job.project_id==='tinybrains_rollout'&&
                job.frozen_parameters?.learner_job_id===learner.id);
            if(learner.project_id!=='tinybrains'||other.length!==members.length-1||
                members.slice(1).some(job=>job.project_id!=='tinybrains_rollout'||
                    job.frozen_parameters?.learner_job_id!==learner.id)) {
                halt('needs_review','Remote rollout membership changed; no Start can be authorized.');
                continue;
            }
            if(members.some((job,index)=>!snapshot.reservations.some(lease=>
                lease.id===plan.frozen_members[index].lease_id&&
                lease.job_id===job.id&&lease.project_id===job.project_id&&
                lease.machine_id===job.assigned_machine_id&&
                lease.issued_by==='experiment-hub'&&
                ['offered','acknowledged'].includes(lease.status)))) {
                halt('needs_review','The pinned native resource ownership no longer exists.');
                continue;
            }
            const learnerHost=snapshot.machines.find(host=>
                host.id===learner.assigned_machine_id);
            if(learnerHost?.tinybrains_remote_key_fingerprint!==plan.key_fingerprint||
                JSON.stringify(learnerHost?.tinybrains_remote_rollout_endpoints)!==
                    JSON.stringify(plan.learner_endpoints)||
                members.slice(1).some((worker,index)=>{
                    const host=snapshot.machines.find(item=>
                        item.id===worker.assigned_machine_id);
                    return host?.tinybrains_rollout_endpoint!==
                        plan.frozen_members[index+1].endpoint||
                        host?.tinybrains_rollout_key_fingerprint!==plan.key_fingerprint;
                })) {
                // Offline reports lack current HMAC identity. Only a live
                // mismatch can revoke identity; a disconnected host waits
                // for its fresh authenticated report before comparing.
                const offline=members.some(job=>
                    snapshot.machines.find(m=>m.id===job.assigned_machine_id)
                        ?.status!=='connected');
                if(offline)continue;
                halt('needs_review','Learner/collector HMAC fingerprint or ordered endpoint changed.');
                continue;
            }
            if(members.some(job=>snapshot.machines.find(host=>
                host.id===job.assigned_machine_id)?.status!=='connected'))
                continue;
            if(!plan.recovery_pending&&members.some(job=>
                this.agentReports.get(job.assigned_machine_id)?.instance_id!==
                    plan.agent_instances[job.assigned_machine_id])) {
                halt('disarmed','A group machine changed its native agent instance before Start.');
                continue;
            }
            if(!members.slice(1).every(job=>
                nativeStartReadiness(snapshot,job).eligible))
                continue;
            // The learner cannot meet the final nativeStartReadiness until
            // its collectors have started. The existing collector admission
            // checks its complete CPU/GPU lease, preflight, endpoint and key.
            try {
                this.authorizeDistributedCollectors({
                    learner_job_id:learner.id,
                    expected_collector_job_ids:plan.collector_job_ids,
                    confirm_start:true,start_learner_when_ready:true,
                    confirm_learner_start:true,
                    ...(plan.resume_after_restart?{
                        resume_after_restart:true,confirm_restart_rearm:true,
                    }:{}),
                },plan.id);
            }catch(error) {
                // Probe or fresh-lease readiness can change between two
                // authenticated heartbeats. Do not retry native processes;
                // none were authorized by a rejected atomic transaction.
                const message=String(error.message||error).slice(0,180);
                if(!['The learner must retain','Collector ','Coordinated launch needs']
                    .some(prefix=>message.startsWith(prefix)))
                    halt('needs_review','Group admission requires review: '+message);
            }
        }
    }
    // Authorize an entire already-reserved remote rollout tier in one durable
    // transition. No partial Starts are offered if any collector, endpoint, or
    // native owner has lost eligibility since the operator's last refresh.
    // Learner Start retains its independent running-collector safety barrier.
    authorizeDistributedCollectors(value,sourcePlanId=null) {
        if(sourcePlanId!==null&&
            !UUID.test(String(sourcePlanId)))
            throw Error('Invalid frozen distributed group approval.');
        const pending=sourcePlanId===null?null:(this.state.distributed_group_plans||[])
            .find(plan=>plan.id===sourcePlanId&&plan.status==='pending');
        if(sourcePlanId!==null&&!pending)
            throw Error('This future distributed group launch is no longer approved.');
        if(sourcePlanId===null&&(this.state.distributed_group_plans||[])
            .some(plan=>['pending','needs_review'].includes(plan.status)&&
                plan.learner_job_id===value?.learner_job_id))
            throw Error('Cancel the outstanding frozen group plan before manually launching its collectors.');
        if(!object(value)||value.confirm_start!==true||
            Object.keys(value).some(key=>![
                'learner_job_id','expected_collector_job_ids','confirm_start',
                'start_learner_when_ready','confirm_learner_start',
                'resume_after_restart','confirm_restart_rearm',
            ].includes(key))||
            (value.start_learner_when_ready!==undefined&&
                typeof value.start_learner_when_ready!=='boolean')||
            (value.start_learner_when_ready===true
                ?value.confirm_learner_start!==true
                :value.confirm_learner_start!==undefined)||
            (value.resume_after_restart!==undefined&&
                typeof value.resume_after_restart!=='boolean')||
            (value.resume_after_restart===true&&
                (value.start_learner_when_ready!==true||
                 value.confirm_restart_rearm!==true))||
            (value.confirm_restart_rearm!==undefined&&
                (value.resume_after_restart!==true||
                 value.confirm_restart_rearm!==true))||
            !UUID.test(String(value.learner_job_id||''))||
            !Array.isArray(value.expected_collector_job_ids)||
            value.expected_collector_job_ids.length<1||
            value.expected_collector_job_ids.length>8||
            value.expected_collector_job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.expected_collector_job_ids).size!==
                value.expected_collector_job_ids.length)
            throw Error('Confirm the learner and exact 1–8 remote collector jobs to start.');
        const snapshot=this.snapshot();
        const learner=snapshot.jobs.find(job=>job.id===value.learner_job_id);
        if(!learner||learner.project_id!=='tinybrains'||
            learner.distributed_rollout_managed!==true||
            learner.frozen_parameters?.remote_rollouts!==true||
            learner.status!=='waiting_adapter'||learner.dispatch_id||
            !learner.lease_id||predecessorBlock(snapshot,learner))
            throw Error('Only an unstarted, checkpoint-eligible distributed TinyBrains learner can authorize its collectors.');
        const learnerHost=snapshot.machines.find(host=>
            host.id===learner.assigned_machine_id);
        const endpoints=learnerHost?.tinybrains_remote_rollout_endpoints;
        const learnerLease=snapshot.reservations.find(lease=>
            lease.id===learner.lease_id&&lease.job_id===learner.id&&
            lease.project_id==='tinybrains'&&
            lease.machine_id===learner.assigned_machine_id&&
            lease.issued_by==='experiment-hub'&&
            lease.status==='acknowledged'&&lease.receipt_fresh===true);
        if(!learnerHost||learnerHost.status!=='connected'||
            learnerHost.local_executor_enabled!==true||
            learnerHost.native_preflight_passed!==true||
            !learnerHost.installed_projects?.includes('tinybrains')||
            !learnerLease||nativeResourceBlock(learner,learnerLease)||
            gpuStartBlock(learner,learnerLease,learnerHost)||
            !supportsTinyBrainsProfile(learnerHost,learner)||
            !Array.isArray(endpoints)||endpoints.length<1||
            endpoints.length>8||new Set(endpoints).size!==endpoints.length)
            throw Error('The learner must retain its fresh native resource claim, GPU access qualification and pinned remote endpoints.');
        const workers=snapshot.jobs.filter(job=>
            job.project_id==='tinybrains_rollout'&&
            job.frozen_parameters?.learner_job_id===learner.id&&
            job.status!=='cancelled');
        const expected=new Set(value.expected_collector_job_ids);
        if(workers.length!==endpoints.length||workers.length!==expected.size||
            workers.some(job=>!expected.has(job.id)))
            throw Error('Collector membership changed; refresh before authorizing any Start.');
        const seenEndpoints=new Set(),seenMachines=new Set([learnerHost.id]);
        for(const worker of workers) {
            if(worker.status!=='waiting_adapter'||worker.dispatch_id)
                throw Error('A collector already started or changed state; no group Starts authorized.');
            const readiness=nativeStartReadiness(snapshot,worker);
            if(!readiness.eligible)
                throw Error('Collector '+worker.id+' is not ready: '+readiness.reason);
            const host=snapshot.machines.find(item=>item.id===worker.assigned_machine_id);
            if(!host||seenMachines.has(host.id)||
                !endpoints.includes(host.tinybrains_rollout_endpoint)||
                seenEndpoints.has(host.tinybrains_rollout_endpoint)||
                host.tinybrains_rollout_key_fingerprint!==
                    learnerHost.tinybrains_remote_key_fingerprint)
                throw Error('Collector host or pinned HMAC endpoint identity changed; no Starts authorized.');
            seenMachines.add(host.id);
            seenEndpoints.add(host.tinybrains_rollout_endpoint);
        }
        const coordinated=value.start_learner_when_ready===true;
        const agents={};
        if(coordinated) {
            for(const id of seenMachines) {
                const report=this.agentReports.get(id);
                if(!UUID.test(String(report?.instance_id||'')))
                    throw Error('Coordinated launch needs a live, identified native agent on every group machine.');
                agents[id]=report.instance_id;
            }
        }
        if(pending&&(pending.learner_job_id!==learner.id||
            pending.resume_after_restart!==(value.resume_after_restart===true)||
            pending.collector_job_ids.length!==workers.length||
            pending.collector_job_ids.some(id=>!expected.has(id))))
            throw Error('Frozen distributed group approval changed before Start.');
        const next=copy(this.state),requestedAt=now();
        const collectorDispatches={};
        for(const worker of workers) {
            const selected=next.jobs.find(job=>job.id===worker.id);
            selected.status='start_requested';
            selected.dispatch_id=crypto.randomUUID();
            collectorDispatches[worker.id]=selected.dispatch_id;
            selected.start_requested_at=requestedAt;
            selected.start_first_offered_at=null;
            selected.status_reason='Remote collector Start authorized as part of the learner group; awaiting native OS enforcement.';
        }
        if(coordinated) {
            const plans=next.distributed_launch_plans||[];
            if(plans.length>=64) {
                const old=plans.findIndex(plan=>plan.status!=='pending');
                if(old<0)throw Error('Resolve earlier coordinated launch approvals before approving another.');
                plans.splice(old,1);
            }
            const approvedMs=Date.now();
            plans.push({
                id:crypto.randomUUID(),learner_job_id:learner.id,
                learner_machine_id:learner.assigned_machine_id,
                learner_lease_id:learner.lease_id,
                collector_dispatches:collectorDispatches,
                collector_owners:workers.map(worker=>{
                    const host=snapshot.machines.find(m=>m.id===worker.assigned_machine_id);
                    return {job_id:worker.id,dispatch_id:collectorDispatches[worker.id],
                        machine_id:worker.assigned_machine_id,lease_id:worker.lease_id,
                        endpoint:host.tinybrains_rollout_endpoint,
                        key_fingerprint:host.tinybrains_rollout_key_fingerprint};
                }),
                key_fingerprint:learnerHost.tinybrains_remote_key_fingerprint,
                learner_endpoints:endpoints.slice(),
                agent_instances:agents,
                resume_after_restart:value.resume_after_restart===true,
                recovery_pending:false,status:'pending',
                reason:'Waiting for verified running remote collector dispatches.',
                approved_at:new Date(approvedMs).toISOString(),
                expires_at:new Date(approvedMs+DISTRIBUTED_LAUNCH_TTL_MS).toISOString(),
            });
            next.distributed_launch_plans=plans;
        }
        if(pending) {
            const resolved=next.distributed_group_plans.find(item=>item.id===pending.id);
            resolved.status='authorized';
            resolved.reason='Every original native collector Start and the future learner gate were committed atomically.';
            resolved.resolved_at=requestedAt;
            // The unattended group also owns the learner's verified terminal
            // capacity release. The existing terminal report remains the
            // authority; this flag does not terminate a process.
            next.jobs.find(job=>job.id===learner.id).release_on_terminal=true;
        }
        // Collector Start authorizations and future learner permission form
        // one crash-atomic commit. Restart cannot observe only half of them.
        this.commit(next);
        return {learner_job_id:learner.id,
            collector_job_ids:workers.map(job=>job.id),
            starts_authorized:workers.length,
            learner_start_authorized:false,
            learner_start_pending:coordinated,
            resume_after_restart:coordinated&&value.resume_after_restart===true};
    }
    // An optimizer rank that fails or is stopped cannot leave other leased
    // torchrun nodes waiting indefinitely. Use the existing native Stop
    // ownership states; never assume the OS process is gone or free a lease
    // without the machine's terminal receipt.
    reconcileNativeOptimizerFailures(next) {
        const groups=new Map();
        for(const job of next.jobs) {
            const id=job.resource_bundle?.id;
            if(id && job.frozen_parameters?.optimizer_group===true) {
                if(!groups.has(id))groups.set(id,[]);
                groups.get(id).push(job);
            }
        }
        let changed=false;
        for(const members of groups.values()) {
            if(!members.some(job=>['failed','stopped'].includes(job.status)))
                continue;
            const at=now();
            for(const job of members) {
                if(job.status==='start_requested'&&job.start_first_offered_at===null) {
                    // The coordinator never offered this Start. Revoke only
                    // its undelivered dispatch; capacity stays reserved for
                    // explicit operator review of the failed group.
                    job.status='waiting_adapter';
                    delete job.dispatch_id;
                    delete job.start_requested_at;
                    delete job.start_first_offered_at;
                    delete job.automation_start_session_id;
                    job.status_reason='Native optimizer peer failed before this Start was delivered.';
                    changed=true;
                }else if(['start_requested','launch_requested','running','unverified',
                    'stop_requested'].includes(job.status)) {
                    const lease=next.reservations.find(item=>
                        item.id===job.lease_id&&item.job_id===job.id&&
                        item.project_id===job.project_id&&
                        item.machine_id===job.assigned_machine_id&&
                        item.issued_by==='experiment-hub'&&
                        item.status==='acknowledged');
                    if(!lease||!job.dispatch_id)continue;
                    if(job.status!=='stop_requested') {
                        job.status='stop_requested';
                        job.stop_requested_at=at;
                        job.status_reason='Synchronized native optimizer peer terminated; awaiting this machine\'s native Stop receipt.';
                        changed=true;
                    }
                    // An earlier operator Stop is already owned by this
                    // supervisor. Keep its original intent, but make sure
                    // the confirmed terminal receipt settles the GPU lease
                    // even when the synchronized group has failed.
                    if(job.release_on_terminal!==true) {
                        job.release_on_terminal=true;
                        changed=true;
                    }
                }
            }
        }
        return changed;
    }

    // A never-started learner may abandon its already-dispatched sidecars
    // without inventing a second process owner. Stops remain native orders;
    // no claim is released while a process/probe is active or unverified.
    reconcileDistributedAborts(next) {
        let changed=false;
        for(const learner of next.jobs.filter(job=>
            job.project_id==='tinybrains'&&
            job.distributed_rollout_managed===true&&
            job.distributed_abort_requested_at&&
            job.status==='waiting_adapter'&&!job.dispatch_id)) {
            const children=next.jobs.filter(job=>
                job.project_id==='tinybrains_rollout'&&
                job.frozen_parameters?.learner_job_id===learner.id);
            for(const child of children) {
                if(child.status==='start_requested'&&
                    child.start_first_offered_at===null) {
                    // No instruction left the coordinator. Forget this
                    // undelivered authority before releasing its CPU claim.
                    child.status='waiting_adapter';
                    delete child.dispatch_id;
                    delete child.start_requested_at;
                    delete child.start_first_offered_at;
                    delete child.automation_start_session_id;
                    changed=true;
                }
                if(child.status==='waiting_adapter'&&!child.dispatch_id) {
                    if(!gpuProbeHoldsClaim(child)) {
                        this.cancelUnstartedJob(next,child,now());
                        changed=true;
                    }
                    continue;
                }
                if(['start_requested','launch_requested','running','unverified']
                    .includes(child.status)) {
                    const lease=next.reservations.find(item=>
                        item.id===child.lease_id&&item.job_id===child.id&&
                        item.project_id==='tinybrains_rollout'&&
                        item.machine_id===child.assigned_machine_id&&
                        item.issued_by==='experiment-hub'&&
                        item.status==='acknowledged');
                    if(!lease||!child.dispatch_id)continue;
                    // First-offered native Starts cannot be safely withdrawn.
                    // Convert them to native Stop orders and require receipts.
                    child.status='stop_requested';
                    child.stop_requested_at=now();
                    child.status_reason='Distributed learner startup aborted; stopping this owned remote collector.';
                    changed=true;
                    continue;
                }
                const lease=next.reservations.find(item=>
                    item.id===child.lease_id&&item.job_id===child.id&&
                    item.machine_id===child.assigned_machine_id&&
                    item.project_id==='tinybrains_rollout'&&
                    item.issued_by==='experiment-hub');
                if(lease&&lease.status==='acknowledged'&&
                    ['finished','stopped','failed'].includes(child.status)&&
                    child.dispatch_id&&child.execution_updated_at&&
                    (child.status!=='failed'||
                        child.prelaunch_absence_verified===true)) {
                    // A verified terminal service has no active native
                    // owner; failed services without prelaunch absence proof
                    // deliberately retain the claim for operator review.
                    this.releaseIn(next,lease);
                    changed=true;
                }
            }
            const safe=children.length>0&&children.every(child=>{
                if(child.status==='cancelled')
                    return !child.lease_id||next.reservations.some(lease=>
                        lease.id===child.lease_id&&lease.status==='released');
                if(!['finished','stopped','failed'].includes(child.status)||
                    !child.capacity_released_at)return false;
                const lease=next.reservations.find(item=>
                    item.id===child.lease_id&&item.job_id===child.id&&
                    item.project_id==='tinybrains_rollout'&&
                    item.status==='released');
                return Boolean(lease);
            });
            if(safe&&!gpuProbeHoldsClaim(learner)) {
                this.cancelUnstartedJob(next,learner,now());
                learner.status_reason='Distributed startup abandoned: every collector was cancelled or released after verified native termination.';
                changed=true;
            }
        }
        return changed;
    }
    // Explicit abort is available only while the learner has never been
    // authorized to start. A GPU learner that was dispatched must use its
    // normal checkpoint-preserving native Stop and reconciliation.
    abortDistributedStartup(value) {
        if(!object(value)||value.confirm_abort!==true||
            !UUID.test(String(value.learner_job_id||''))||
            !Array.isArray(value.expected_collector_job_ids)||
            value.expected_collector_job_ids.length<1||
            value.expected_collector_job_ids.length>8||
            value.expected_collector_job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.expected_collector_job_ids).size!==
                value.expected_collector_job_ids.length||
            Object.keys(value).some(key=>![
                'learner_job_id','expected_collector_job_ids','confirm_abort',
            ].includes(key)))
            throw Error('Confirm the exact distributed group and its remote collector IDs to abort.');
        const next=copy(this.state);
        const learner=next.jobs.find(job=>job.id===value.learner_job_id);
        if(!learner||learner.project_id!=='tinybrains'||
            learner.distributed_rollout_managed!==true||
            learner.frozen_parameters?.remote_rollouts!==true||
            learner.status!=='waiting_adapter'||learner.dispatch_id)
            throw Error('Only a never-dispatched distributed learner can abort startup.');
        const children=next.jobs.filter(job=>
            job.project_id==='tinybrains_rollout'&&
            job.frozen_parameters?.learner_job_id===learner.id);
        const expected=new Set(value.expected_collector_job_ids);
        if(children.length!==expected.size||children.some(job=>!expected.has(job.id)))
            throw Error('Collector membership changed; refresh before abandoning the group.');
        const members=[learner,...children];
        if(next.jobs.some(job=>job.status!=='cancelled'&&
            members.some(parent=>job.after_job_id===parent.id||
                job.continuation_of_job_id===parent.id))||
            (next.candidate_selections||[]).some(plan=>
                ['waiting','needs_review'].includes(plan.status)&&
                members.some(job=>plan.candidate_job_ids?.includes(job.id))))
            throw Error('Cancel outstanding group dependencies and candidate selections before abandoning startup.');
        learner.distributed_abort_requested_at??=now();
        learner.status_reason='Distributed startup abort requested; stopping collectors and awaiting native ownership receipts.';
        this.reconcileDistributedAborts(next);
        const queued=(next.distributed_group_plans||[]).find(plan=>
            plan.learner_job_id===learner.id&&
            ['pending','needs_review'].includes(plan.status));
        if(queued) {
            queued.status='disarmed';
            queued.reason='Operator abandoned the unstarted distributed group.';
            queued.resolved_at=now();
        }
        const intent=(next.distributed_launch_plans||[]).find(plan=>
            plan.learner_job_id===learner.id&&plan.status==='pending');
        if(intent) {
            intent.status='disarmed';
            intent.reason='Distributed startup abandoned; future learner Start revoked.';
            intent.resolved_at=now();
        }
        this.commit(next);
        const member=next.jobs.find(job=>job.id===learner.id);
        return {learner_job_id:learner.id,
            learner_cancelled:member.status==='cancelled',
            collector_statuses:next.jobs.filter(job=>
                job.project_id==='tinybrains_rollout'&&
                job.frozen_parameters?.learner_job_id===learner.id)
                .map(job=>({job_id:job.id,status:job.status,
                    released:Boolean(job.capacity_released_at)})),
            note:'Native Stops are durable requests, not proof that a process exited. Claims remain held until verified absence or terminal receipts.'};
    }
    // Cancel only the future automatic learner authorization. Remote
    // processes keep their own durable native owners, Stops and reservations.
    cancelDistributedLaunch(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.learner_job_id||''))||
            Object.keys(value).some(key=>![
                'learner_job_id','confirm_cancel',
            ].includes(key)))
            throw Error('Explicitly confirm the coordinated learner launch to disarm.');
        const next=copy(this.state);
        const intent=(next.distributed_launch_plans||[]).find(plan=>
            plan.learner_job_id===value.learner_job_id&&plan.status==='pending');
        if(!intent)
            throw Error('No pending coordinated learner Start for this job.');
        intent.status='disarmed';
        intent.reason='Future learner Start cancelled by operator; existing collector processes are unchanged.';
        intent.resolved_at=now();
        this.commit(next);
        return {learner_job_id:intent.learner_job_id,
            learner_start_pending:false,collector_starts_withdrawn:0};
    }
    // A heartbeat first authenticates native ownership and refreshes
    // capacity receipts. Only then may one preapproved learner Start be
    // committed. Restart recovery requires the same durable worker dispatches,
    // lease owners, HMAC endpoints and a fresh live native running report;
    // an uncertain job is never restarted or treated as active proof.
    advanceDistributedLaunches() {
        const ids=(this.state.distributed_launch_plans||[])
            .filter(plan=>plan.status==='pending').map(plan=>plan.id);
        for(const id of ids) {
            const intent=(this.state.distributed_launch_plans||[]).find(plan=>
                plan.id===id&&plan.status==='pending');
            if(!intent)continue;
            const disarm=reason=>{
                const next=copy(this.state);
                const current=(next.distributed_launch_plans||[]).find(plan=>
                    plan.id===id&&plan.status==='pending');
                if(!current)return;
                current.status='disarmed';
                current.reason=reason;
                current.resolved_at=now();
                this.commit(next);
            };
            if(Date.now()>=Date.parse(intent.expires_at)) {
                disarm('Coordinated launch approval expired; native collector owners are unchanged.');
                continue;
            }
            const snapshot=this.snapshot();
            const learner=snapshot.jobs.find(job=>job.id===intent.learner_job_id);
            if(!learner||learner.status!=='waiting_adapter'||learner.dispatch_id||
                learner.lease_id!==intent.learner_lease_id||
                learner.assigned_machine_id!==intent.learner_machine_id) {
                disarm('Learner dispatch, host or original resource claim changed.');
                continue;
            }
            const workers=snapshot.jobs.filter(job=>
                job.project_id==='tinybrains_rollout'&&
                job.frozen_parameters?.learner_job_id===learner.id&&
                job.status!=='cancelled');
            const expectedIds=Object.keys(intent.collector_dispatches);
            if(workers.some(job=>['failed','finished','stopped'].includes(job.status))) {
                try {
                    this.abortDistributedStartup({
                        learner_job_id:learner.id,
                        expected_collector_job_ids:expectedIds,
                        confirm_abort:true,
                    });
                }catch(error) {
                    disarm('Collector exited before learner Start; manual abort review required: '+
                        String(error.message||error).slice(0,120));
                }
                continue;
            }
            if(workers.length!==expectedIds.length||
                workers.length!==intent.collector_owners.length||
                workers.some(job=>{
                    const frozen=intent.collector_owners.find(owner=>owner.job_id===job.id);
                    return !frozen||frozen.dispatch_id!==job.dispatch_id||
                        frozen.dispatch_id!==intent.collector_dispatches[job.id]||
                        frozen.lease_id!==job.lease_id||
                        frozen.machine_id!==job.assigned_machine_id||
                        !['start_requested','launch_requested','running'].includes(job.status);
                })) {
                disarm('Collector membership, native dispatch or original lease changed.');
                continue;
            }
            const machines=[learner.assigned_machine_id,
                ...workers.map(job=>job.assigned_machine_id)];
            if(machines.length!==new Set(machines).size) {
                disarm('Distinct native collector machine identities no longer match the approved group.');
                continue;
            }
            const offline=machines.some(id=>
                !snapshot.machines.some(host=>host.id===id&&host.status==='connected'));
            if(offline) {
                if(!intent.recovery_pending)
                    disarm('A group machine went offline before learner launch.');
                continue;
            }
            const changedAgent=machines.some(id=>
                this.agentReports.get(id)?.instance_id!==intent.agent_instances[id]);
            if(changedAgent&&!intent.recovery_pending) {
                disarm('A group machine changed its authenticated native agent identity.');
                continue;
            }
            // A new agent after coordinator restart must prove this exact
            // preexisting native dispatch running; status alone cannot pass
            // nativeStartReadiness's live_execution ownership barrier.
            const leases=[learner,...workers].map(job=>
                snapshot.reservations.find(lease=>lease.id===job.lease_id&&
                    lease.job_id===job.id&&
                    lease.machine_id===job.assigned_machine_id&&
                    lease.project_id===job.project_id&&
                    lease.issued_by==='experiment-hub'&&
                    lease.status==='acknowledged'));
            if(leases.some(lease=>!lease)) {
                disarm('An original Hub-owned machine lease changed or disappeared.');
                continue;
            }
            if(leases.some(lease=>!lease.receipt_fresh)) {
                if(!intent.recovery_pending)
                    disarm('A group resource receipt was lost or expired.');
                continue;
            }
            const learnerHost=snapshot.machines.find(host=>
                host.id===learner.assigned_machine_id);
            const endpoints=learnerHost?.tinybrains_remote_rollout_endpoints||[];
            if(learnerHost?.tinybrains_remote_key_fingerprint!==intent.key_fingerprint||
                JSON.stringify(endpoints)!==JSON.stringify(intent.learner_endpoints)||
                endpoints.length!==workers.length||
                new Set(endpoints).size!==workers.length||
                workers.some(worker=>{
                    const host=snapshot.machines.find(item=>
                        item.id===worker.assigned_machine_id);
                    const frozen=intent.collector_owners.find(owner=>owner.job_id===worker.id);
                    return !host||!frozen||
                        host.tinybrains_rollout_endpoint!==frozen.endpoint||
                        host.tinybrains_rollout_key_fingerprint!==frozen.key_fingerprint||
                        frozen.key_fingerprint!==intent.key_fingerprint||
                        !endpoints.includes(frozen.endpoint);
                })) {
                disarm('Pinned HMAC key or collector endpoint identity changed.');
                continue;
            }
            if(!nativeStartReadiness(snapshot,learner).eligible)
                continue;
            try {
                // The native learner Start and one-use plan resolution must
                // be committed together before any Start can be offered.
                this.authorizeStart({job_id:learner.id,confirm_start:true},
                    'distributed');
            }catch(error) {
                disarm('Learner Start failed; operator review required: '+
                    String(error.message||error).slice(0,150));
            }
        }
    }
    withdrawStart(value) {
        if(!object(value)||value.confirm_withdraw!==true||
            !UUID.test(String(value.expected_dispatch_id||'')))
            throw Error('Explicitly confirm which Start authorization to withdraw.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if(!job||job.status!=='start_requested'||job.dispatch_id!==value.expected_dispatch_id||
            job.start_first_offered_at!==null)
            throw Error('Only a matching Start that has never been offered to a worker can be withdrawn.');
        if(this.automation.enabled&&this.automation.machine_ids.includes(job.assigned_machine_id))
            throw Error('Disarm automation for this machine before withdrawing Start.');
        job.status='waiting_adapter';
        delete job.dispatch_id;
        delete job.start_requested_at;
        delete job.start_first_offered_at;
        // A distributed sidecar's terminal-release policy was frozen at
        // group queue time, not selected by this Start. Withdrawing an
        // undelivered offer must not turn its next verified terminal receipt
        // into a permanently held CPU/GPU reservation.
        const groupOwnedCollector=job.project_id==='tinybrains_rollout'&&
            next.jobs.some(parent=>parent.id===job.frozen_parameters?.learner_job_id&&
                parent.project_id==='tinybrains'&&
                parent.distributed_rollout_managed===true);
        if(!groupOwnedCollector&&!job.native_release_recovery)delete job.release_on_terminal;
        delete job.automation_start_session_id;
        if(job.native_release_recovery) {
            delete job.native_release_recovery.round_plan_id;
            delete job.native_release_recovery.start_origin;
        }
        job.status_reason='Undelivered Start withdrawn. The queued job retains its existing claim.';
        this.commit(next);
        return {id:job.id,status:job.status,lease_id:job.lease_id};
    }
    authorizeStop(value) {
        if(!object(value)||value.confirm_stop!==true||
            typeof value.job_id!=='string')
            throw Error('Explicitly confirm which managed job to stop.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if(!job||!job.dispatch_id||
            !['start_requested','launch_requested','running','unverified'].includes(job.status))
            throw Error('Only a dispatched, nonterminal job may request a native stop.');
        const lease=next.reservations.find(l=>l.id===job.lease_id &&
            l.job_id===job.id&&l.status==='acknowledged');
        if(job.status==='start_requested'&&job.start_first_offered_at===null)
            return this.withdrawStart({job_id:job.id,expected_dispatch_id:job.dispatch_id,
                confirm_withdraw:true});
        if(!lease)throw Error('Job/lease ownership cannot be verified.');
        job.status='stop_requested';
        job.stop_requested_at=now();
        job.status_reason='Stop requested; awaiting local service confirmation.';
        this.commit(next);
        return {id:job.id,status:job.status};
    }
    prepareNativeReleaseRecovery(value,roundPlanId=null) {
        if(!object(value)||value.confirm_recover!==true||
            !UUID.test(String(value.job_id||''))||
            Object.keys(value).some(key=>!['job_id','confirm_recover'].includes(key)))
            throw Error('Confirm the exact failed native generation release.');
        const next=copy(this.state);
        const job=next.jobs.find(item=>item.id===value.job_id);
        const proof=job?.native_release_pending;
        const lease=next.reservations.find(item=>item.id===job?.lease_id&&
            item.job_id===job.id&&item.machine_id===job.assigned_machine_id&&
            item.project_id==='bees_native'&&item.issued_by==='experiment-hub'&&
            ['acknowledged','released'].includes(item.status));
        if(!job||job.project_id!=='bees_native'||job.status!=='failed'||
            job.frozen_parameters?.continual_release!==true||!proof||!lease||
            (lease.status==='released')!==Boolean(job.capacity_released_at)||
            !job.dispatch_id||!job.start_first_offered_at||!job.execution_updated_at)
            throw Error('Release recovery requires the verified committed round and its original machine ownership.');
        if(job.frozen_parameters.optimizer_group===true) {
            const group=job.resource_bundle;
            const members=next.jobs.filter(item=>item.resource_bundle?.id===group?.id)
                .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
            if(group?.index!==1||members.length!==group.size||members[0]?.id!==job.id||
                members.slice(1).some((worker,index)=>
                    worker.project_id!=='bees_native_optimizer_worker'||
                    worker.resource_bundle.index!==index+2||
                    worker.resource_bundle.size!==group.size||
                    worker.status!=='finished'||!worker.capacity_released_at||
                    worker.optimizer_completion?.group_id!==group.id||
                    worker.optimizer_completion?.node_rank!==index+1||
                    worker.optimizer_completion?.input_sha256!==proof.input_sha256||
                    worker.optimizer_completion?.model_consensus_sha256!==proof.model_consensus_sha256))
                throw Error('Wait for every original optimizer worker to finish, release capacity and prove the same checkpoint consensus.');
        }
        const previous=job.native_release_recovery;
        const attempts=job.native_release_attempts||[];
        if(roundPlanId!==null) {
            const plan=(next.native_round_plans||[]).find(item=>
                item.id===roundPlanId&&item.status==='awaiting_optimizer'&&
                item.optimizer_job_ids?.[0]===job.id&&
                item.optimizer_bundle_id===job.resource_bundle?.id);
            if(!plan||attempts.length||
                !next.bundle_start_plans?.some(launch=>
                    launch.bundle_id===plan.optimizer_bundle_id&&launch.status==='authorized')||
                Object.entries(plan.agent_instances).some(([id,instance])=>
                    this.agentReports.get(id)?.instance_id!==instance))
                throw Error('Unattended release recovery requires an original approved round with no previous recovery attempt.');
        }
        if(attempts.length>=16)
            throw Error('Native generation recovery attempt history is full.');
        const attempt={attempt_id:crypto.randomUUID(),
            previous_attempt_id:previous?.attempt_id||job.id,
            previous_lease_id:lease.id,
            original_lease_id:attempts[0]?.original_lease_id||lease.id,
            checkpoint_sha256:proof.checkpoint_sha256,
            previous_dispatch_id:job.dispatch_id,
            previous_start_first_offered_at:job.start_first_offered_at,
            previous_resource_usage:job.final_resource_usage||null,
            ...(roundPlanId!==null?{round_plan_id:roundPlanId}:{}),
            prepared_at:now()};
        if(lease.status==='released') {
            // Recovery may need to reacquire capacity after an operator
            // released the failed owner. Pin the SAME machine and GPUs.
            const replacement=this.offerIn(next,job.project_id,job.assigned_machine_id,
                resourceRequest(lease.resources),job.id);
            if(JSON.stringify(replacement.gpu_bindings)!==JSON.stringify(lease.gpu_bindings)||
                !matchingLeaseBudget(job.resources,replacement.resources))
                throw Error('Release recovery cannot change the original physical GPU or resource budget.');
            job.lease_id=replacement.id;
            job.assigned_at=now();
            delete job.capacity_released_at;
            delete job.gpu_qualification;
        }
        job.native_release_attempts=[...attempts,attempt];
        job.native_release_recovery=attempt;
        job.status='waiting_adapter';
        job.status_reason=roundPlanId!==null
            ?'Committed training round retained; original native round authorizes one release-only recovery.'
            :'Committed training round retained; release-only recovery is queued for explicit Start.';
        delete job.dispatch_id;
        delete job.start_requested_at;
        delete job.start_first_offered_at;
        delete job.stop_requested_at;
        delete job.automation_start_session_id;
        delete job.prelaunch_absence_verified;
        job.release_on_terminal=true;
        // A separate physical attempt preserves the original logical
        // round, worker group and completed consensus receipts.
        this.commit(next);
        return {id:job.id,status:job.status,recovery_attempt_id:attempt.attempt_id};
    }
    requestCheckpointReinspection(value) {
        if(!object(value)||value.confirm_reinspect!==true||
            !UUID.test(String(value.job_id||'')))
            throw Error('Explicitly confirm the finished checkpoint job to reinspect.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        const lease=next.reservations.find(l=>l.id===job?.lease_id&&
            l.job_id===job.id&&l.machine_id===job.assigned_machine_id&&
            l.project_id===job.project_id&&
            (l.status==='acknowledged'||l.status==='released'));
        if(!job||job.project_id!=='tinybrains'||job.status!=='finished'||
            job.checkpoint_proof_required!==true||
            job.checkpoint_ready_verified===true||!job.dispatch_id||
            !job.start_first_offered_at||!lease)
            throw Error('Only a finished, authenticated TinyBrains job lacking checkpoint proof can be reinspected.');
        if(job.checkpoint_reinspection_requested_at)
            throw Error('Checkpoint reinspection is already pending.');
        job.checkpoint_reinspection_requested_at=now();
        this.commit(next);
        return {id:job.id,status:job.status,reinspection:'requested'};
    }
    // The report is authenticated by the machine-token heartbeat. A second
    // machine, retired dispatch ID or unrelated lease cannot update the job.
    recordExecution(next,machine,items,accepted=new Map(),evaluationSuites={},
        receivedMs=Date.now()) {
        if(!Array.isArray(items)||items.length>50)
            throw Error('Invalid execution reports.');
        const seen=new Set();
        const observedAt=new Date(receivedMs).toISOString();
        let changed=false;
        for(const report of items) {
            if(!object(report)||typeof report.job_id!=='string'||
                typeof report.lease_id!=='string'||
                typeof report.dispatch_id!=='string'||
                !['launch_requested','running','stop_requested','finished','failed','stopped','unverified']
                    .includes(report.state)||
                seen.has(report.job_id)||
                (report.prelaunch_absence_verified!=null&&
                    (report.prelaunch_absence_verified!==true||report.state!=='failed'))||
                (report.diagnostic!=null&&
                    (typeof report.diagnostic!=='string'||report.diagnostic.length>220))||
                (report.progress_issue!=null&&
                    (typeof report.progress_issue!=='string'||
                        report.progress_issue.length>160))||
                ['memory_current_mb','cpu_time_ns','peak_job_memory_bytes','process_tasks',
                 'cpu_throttle_events','cpu_quota_periods'].some(k=>
                    report[k]!=null&&
                    (!Number.isSafeInteger(report[k])||report[k]<0))||
                ['cpu_time_seconds','cpu_throttled_seconds'].some(k=>
                    report[k]!=null&&
                    (!Number.isFinite(report[k])||
                     report[k]<0||report[k]>1e12)))
                throw Error('Invalid native execution report.');
            seen.add(report.job_id);
            const job=next.jobs.find(j=>j.id===report.job_id);
            const lease=next.reservations.find(l=>l.id===report.lease_id);
            // A lost heartbeat response may replay the failed dispatch
            // after its logical job has acquired a new physical attempt.
            // Acknowledge only that exact retired terminal report; it must
            // never mutate the new lease, progress or execution state.
            if(job?.native_release_attempts?.some(attempt=>
                    attempt.previous_dispatch_id===report.dispatch_id&&
                    attempt.previous_lease_id===report.lease_id)&&
                lease?.job_id===job.id&&lease.machine_id===machine.id&&
                lease.project_id===job.project_id&&
                job.assigned_machine_id===machine.id&&report.state==='failed'&&
                job.dispatch_id!==report.dispatch_id)continue;
            if(!job||job.start_first_offered_at===null||!lease||
                job.dispatch_id!==report.dispatch_id||
                job.lease_id!==lease.id||lease.job_id!==job.id||
                lease.machine_id!==machine.id||job.assigned_machine_id!==machine.id||
                lease.project_id!==job.project_id)
                throw Error('Native execution report does not match its job and machine.');
            const releaseRecovery=verifiedReleaseRecovery(
                job,report.native_release_pending,report.state);
            if(releaseRecovery) {
                if(job.native_release_pending&&
                    JSON.stringify(job.native_release_pending)!==JSON.stringify(releaseRecovery))
                    throw Error('Committed native release recovery checkpoint changed.');
                if(!job.native_release_pending) {
                    job.native_release_pending=releaseRecovery;
                    changed=true;
                }
            }
            if(job.native_release_recovery&&report.checkpoint_model_sha256!=null&&
                report.checkpoint_model_sha256!==job.native_release_recovery.checkpoint_sha256)
                throw Error('Release recovery cannot publish a different optimizer checkpoint.');
            const optimizerCompletion=verifiedOptimizerCompletion(
                job,report.optimizer_completion,report.state);
            if(optimizerCompletion) {
                if(optimizerCompletion.continual_release&&
                    (report.checkpoint_ready_verified!==true||
                     optimizerCompletion.continual_release.checkpoint_sha256!==
                        report.checkpoint_model_sha256))
                    throw Error('Native generation release must identify the verified terminal model.');
                if(['failed','stopped','cancelled'].includes(job.status)||
                    job.optimizer_completion&&
                    JSON.stringify(job.optimizer_completion)!==
                        JSON.stringify(optimizerCompletion))
                    throw Error('Native optimizer completion changed after its original terminal proof.');
                if(!job.optimizer_completion) {
                    job.optimizer_completion=optimizerCompletion;
                    changed=true;
                }
            }
            // Released capacity stays released. A matching terminal Inspect
            // may add evidence for the exact final checkpoint only.
            if(lease.status==='released'&&lease.released_at&&
                job.capacity_released_at&&
                ['finished','failed','stopped'].includes(job.status)) {
                if(retainNativeBaselineEvaluation(next,job,report,evaluationSuites))
                    changed=true;
                if(finishCheckpointReinspection(job,report,observedAt))changed=true;
                if(report.state===job.status&&supportsEvaluation(evaluationSuites,job)&&
                    retainFinalEvaluations(job,report))changed=true;
                continue;
            }
            if(lease.status!=='acknowledged')
                throw Error('Native execution report requires an acknowledged lease.');
            // Metrics cannot grant authority or change status: they are
            // validated only after machine/job/dispatch ownership checks.
            const progress=report.progress==null||job.project_id==='ants'
                ?null:validateProgress(report.progress,job.project_id);
            if(['finished','failed','stopped'].includes(job.status)) {
                // An authenticated same-dispatch native Inspect may supply
                // an exact model identity missing from an older terminal
                // record. Never rewrite previously pinned bytes, native
                // status, or checkpoint ownership on a repeated report.
                const reinspected=finishCheckpointReinspection(job,report,observedAt);
                if(reinspected)changed=true;
                if(report.state===job.status) {
                    // Native Finish may precede verification of the
                    // resumable checkpoint. Retain a later authenticated
                    // same-dispatch proof before admitting its fingerprint.
                    if(job.project_id==='bees_native'&&job.status==='finished'&&
                        report.checkpoint_ready_verified===true&&
                        !job.checkpoint_ready_verified) {
                        job.checkpoint_ready_verified=true;
                        changed=true;
                    }
                    if(!reinspected&&report.checkpoint_model_sha256!=null) {
                        const verified=job.status==='finished'
                            ?job.checkpoint_ready_verified===true&&
                                report.checkpoint_ready_verified===true
                            :job.status==='stopped'&&
                                job.checkpoint_stop_verified===true&&
                                report.checkpoint_stop_verified===true;
                        if(!verified||
                            !SHA256.test(String(report.checkpoint_model_sha256))||
                            (job.checkpoint_model_sha256&&
                             job.checkpoint_model_sha256!==report.checkpoint_model_sha256))
                            throw Error('Terminal model fingerprint changed or lacks native proof.');
                        if(!job.checkpoint_model_sha256) {
                            job.checkpoint_model_sha256=report.checkpoint_model_sha256;
                            changed=true;
                        }
                    }
                    accepted.set(report.job_id,{progress:null,evaluation:null,
                        evaluationIssue:null,resources:null});
                    // A verified later Inspect supersedes an earlier
                    // checkpoint-unavailable diagnostic. Keep the original
                    // terminal process state while recording that its
                    // checkpoint became publishable under this same dispatch.
                    if(job.status==='finished'&&
                        job.frozen_parameters?.optimizer_group===true&&
                        nativeOptimizerTerminalProof(job)&&
                        (job.execution_diagnostic||null)!==(report.diagnostic||null)) {
                        job.execution_diagnostic=report.diagnostic||null;
                        job.execution_updated_at=observedAt;
                        changed=true;
                    }
                    if(job.release_on_terminal&&job.status==='finished'&&
                        job.frozen_parameters?.optimizer_group===true&&
                        nativeOptimizerTerminalProof(job)) {
                        this.releaseIn(next,lease);
                        changed=true;
                    }
                }
                continue;
            }
            if(!['start_requested','launch_requested','running',
                    'stop_requested','unverified'].includes(job.status))
                throw Error('Job is not awaiting a native execution report.');
            // Inspect an unverified service before retrying Stop. Once the
            // worker confirms it is running, restore the durable Stop intent.
            const state=report.state==='running'&&
                (job.status==='stop_requested'||job.stop_requested_at)
                ?'stop_requested':report.state;
            if(state==='launch_requested'&&job.status!=='start_requested')continue;
            if(job.status==='stop_requested'&&report.state==='launch_requested')continue;
            if(['courier','tinybrains','bees_native'].includes(job.project_id)) {
                if(state==='finished'&&report.checkpoint_ready_verified===true) {
                    job.checkpoint_ready_verified=true;
                    changed=true;
                }else if(state==='stopped'&&job.stop_requested_at&&
                    report.checkpoint_stop_verified===true) {
                    job.checkpoint_stop_verified=true;
                    changed=true;
                }
                if(report.checkpoint_model_sha256!=null) {
                    if(!SHA256.test(String(report.checkpoint_model_sha256))||
                        !(state==='finished'&&job.checkpoint_ready_verified===true||
                          state==='stopped'&&job.checkpoint_stop_verified===true))
                        throw Error('Model fingerprint requires verified terminal checkpoint ownership.');
                    job.checkpoint_model_sha256=report.checkpoint_model_sha256;
                    changed=true;
                }
            }
            // Only reports accepted into the lifecycle may update live
            // telemetry and comparison samples. A delayed older report
            // must not make a terminal job appear to be running again.
            let evaluation=null,evaluations=[],evaluationDefinitions=null,evaluationIssue=null;
            if(state==='running'&&job.project_id!=='ants'&&job.evaluation_stop&&
                supportsEvaluation(evaluationSuites,job)) {
                evaluationIssue=typeof report.evaluation_issue==='string'
                    ?report.evaluation_issue.slice(0,160):null;
                const rules=evaluationRules(job.evaluation_stop);
                if(job.evaluation_stop.all_of) {
                    if(report.evaluations!=null) {
                        try {
                            if(!Array.isArray(report.evaluations)||
                                report.evaluations.length>rules.length)
                                throw Error('Invalid multi-suite evaluation report count.');
                            // Unpinned suite IDs must not silently change
                            // meaning between checkpoints. Recover the initial
                            // fingerprints from earlier persisted receipts when
                            // upgrading a running experiment.
                            const definitions={...(job.evaluation_suite_definitions||{})};
                            if(!job.evaluation_suite_definitions) {
                                for(const cohort of job.evaluation_candidates||[])
                                    for(const receipt of Object.values(cohort.receipts||{})) {
                                        const prior=definitions[receipt.suite_id];
                                        if(!receipt.suite_sha256||
                                            Object.hasOwn(definitions,receipt.suite_id)&&
                                            prior!==receipt.suite_sha256)
                                            throw Error('Existing evaluation suite definitions conflict; review this run.');
                                        definitions[receipt.suite_id]=receipt.suite_sha256;
                                    }
                            }
                            const seen=new Set();
                            for(const raw of report.evaluations) {
                                const rule=rules.find(r=>r.suite_id===raw?.suite_id);
                                if(!rule||seen.has(rule.suite_id))
                                    throw Error('Unexpected or duplicate evaluation suite.');
                                seen.add(rule.suite_id);
                                const receipt=evaluationEvidence(raw,rule,job.dispatch_id);
                                if(!receipt.suite_sha256)
                                    throw Error('Multi-suite evaluations require suite SHA-256 fingerprints.');
                                if(Object.hasOwn(definitions,receipt.suite_id)&&
                                    definitions[receipt.suite_id]!==receipt.suite_sha256)
                                    throw Error('Evaluation suite '+receipt.suite_id+
                                        ' changed definition during training; use a new experiment.');
                                definitions[receipt.suite_id]=receipt.suite_sha256;
                                evaluations.push(receipt);
                            }
                            evaluationDefinitions=definitions;
                        }catch(error) {
                            evaluations=[];
                            evaluationIssue=error.message;
                        }
                    }
                }else if(report.evaluation!=null) {
                    try {
                        evaluation=evaluationEvidence(
                            report.evaluation,job.evaluation_stop,job.dispatch_id);
                        // A suite name alone cannot authorize a training Stop.
                        // Keep single-suite receipts as strictly identified as
                        // all-of batches before pinning or acting on results.
                        if(!evaluation.suite_sha256)
                            throw Error('Single-suite evaluations require suite SHA-256 fingerprints.');
                        const prior=job.evaluation_suite_definitions?.[evaluation.suite_id];
                        if(prior&&prior!==evaluation.suite_sha256)
                            throw Error('Evaluation suite '+evaluation.suite_id+
                                ' changed definition during training; use a new experiment.');
                        if(evaluation.suite_sha256)
                            evaluationDefinitions={...(job.evaluation_suite_definitions||{}),
                                [evaluation.suite_id]:evaluation.suite_sha256};
                    }catch(error) {
                        evaluation=null;
                        evaluationIssue=error.message;
                    }
                }
            }
            if(state==='failed'&&report.prelaunch_absence_verified===true&&
                job.prelaunch_absence_verified!==true) {
                job.prelaunch_absence_verified=true;
                changed=true;
            }
            const previous=this.executionTelemetry.get(job.id);
            const resources=summarizeResourceSample(
                previous?.dispatch_id===job.dispatch_id?previous.resource_usage:null,
                report,observedAt);
            accepted.set(report.job_id,{progress,evaluation,evaluationIssue,resources});
            if(job.status!==state||
                (job.execution_diagnostic||null)!==(report.diagnostic||null)) {
                job.status=state;
                job.status_reason=state==='unverified'
                    ?'Native service state requires inspection; no automatic retry.'
                    :state==='stop_requested'
                    ?'Stop requested; native process still reported running.'
                    :state==='running'
                    ?'Locally verified native service running.'
                    :'Native service state reported by its machine-local supervisor.';
                job.execution_diagnostic=report.diagnostic||null;
                job.execution_updated_at=now();
                changed=true;
            }
            if(evaluationDefinitions&&
                JSON.stringify(job.evaluation_suite_definitions||{})!==
                    JSON.stringify(evaluationDefinitions)) {
                job.evaluation_suite_definitions=evaluationDefinitions;
                changed=true;
            }
            if(evaluations.length) {
                // Results from different checkpoints must never be mixed
                // to authorize a benchmark-driven Stop.
                if(retainEvaluationCohorts(job,evaluations))changed=true;
                for(const cohort of job.evaluation_candidates||[]) {
                    const proof={all_of:evaluationRules(job.evaluation_stop)
                        .map(rule=>cohort.receipts[rule.suite_id])};
                    if(evaluationComplete(proof,job.evaluation_stop,job.dispatch_id)) {
                        job.status='stop_requested';
                        job.stop_requested_at=now();
                        job.status_reason='All independent evaluation targets met at one checkpoint; awaiting native Stop.';
                        job.evaluation_stop_evidence=proof;
                        changed=true;
                        break;
                    }
                }
            }else if(evaluation&&evaluationComplete(evaluation,
                job.evaluation_stop,job.dispatch_id)) {
                if(retainEvaluationCohorts(job,[evaluation]))changed=true;
                job.status='stop_requested';
                job.stop_requested_at=now();
                job.status_reason='Independent evaluation target met; awaiting native Stop.';
                job.evaluation_stop_evidence=evaluation;
                changed=true;
            }else if(evaluation) {
                if(retainEvaluationCohorts(job,[evaluation]))changed=true;
                if(JSON.stringify(evaluation)!==JSON.stringify(job.last_evaluation)) {
                    job.last_evaluation=evaluation;
                    changed=true;
                }
            }
            // Keep the last accepted source version if the terminal report
            // omitted progress or supplied an older/unrelated summary.
            // This observation never authorizes an execution transition.
            if(['finished','failed','stopped'].includes(job.status)) {
                if(resources)job.final_resource_usage=resources;
                const source='hub-metrics-v1';
                const latest=previous?.dispatch_id===job.dispatch_id&&
                    previous.progress?.source===source
                    ?previous.progress:null;
                const selected=latest&&(!progress||
                    progress.experiment_id!==latest.experiment_id||
                    Date.parse(progress.source_modified_at)<
                        Date.parse(latest.source_modified_at))
                    ?latest:progress;
                if(selected&&(!job.final_progress||
                    job.native_release_recovery&&Date.parse(selected.source_modified_at)>
                        Date.parse(job.final_progress.source_modified_at))) {
                    job.final_progress=selected;
                    changed=true;
                }
                if(job.release_on_terminal&&['finished','stopped'].includes(job.status)&&
                    nativeOptimizerTerminalProof(job))
                    this.releaseIn(next,lease);
            }
        }
        // Terminal learner receipts end the lifecycle of its exclusively
        // owned remote collection sidecars. Never launch or stop a different
        // learner's worker, and never infer a native process has disappeared.
        for(const learner of next.jobs.filter(j=>j.project_id==='tinybrains'&&
            j.distributed_rollout_managed===true&&
            ['finished','failed','stopped'].includes(j.status))) {
            for(const child of next.jobs.filter(j=>j.project_id==='tinybrains_rollout'&&
                j.frozen_parameters?.learner_job_id===learner.id)) {
                if(['running','launch_requested','unverified'].includes(child.status)) {
                    const owned=next.reservations.find(lease=>
                        lease.id===child.lease_id&&lease.job_id===child.id&&
                        lease.machine_id===child.assigned_machine_id&&
                        lease.project_id==='tinybrains_rollout'&&
                        lease.status==='acknowledged'&&
                        lease.issued_by==='experiment-hub');
                    if(!child.dispatch_id||!owned)continue;
                    child.status='stop_requested';
                    child.stop_requested_at=now();
                    child.status_reason='Parent learner is terminal; stopping its reserved remote collector.';
                    changed=true;
                }else if(child.status==='start_requested'&&
                    child.start_first_offered_at===null) {
                    // This Start was never delivered to the local owner.
                    // Withdraw the orphaned authority before cancellation.
                    child.status='waiting_adapter';
                    delete child.dispatch_id;
                    delete child.start_requested_at;
                    delete child.start_first_offered_at;
                    delete child.automation_start_session_id;
                    changed=true;
                }
                if(child.status==='waiting_adapter'&&!child.dispatch_id&&
                    !gpuProbeHoldsClaim(child)) {
                    const lease=next.reservations.find(l=>l.id===child.lease_id&&
                        l.job_id===child.id&&
                        ['offered','acknowledged'].includes(l.status));
                    if(lease||!child.lease_id) {
                        this.cancelUnstartedJob(next,child,now());
                        changed=true;
                    }
                }
            }
        }
        if(this.reconcileNativeOptimizerFailures(next))changed=true;
        if(this.reconcileDistributedAborts(next))changed=true;
        return changed;
    }
    executionOrders(machineId) {
        const snapshot=this.snapshot();
        const retiredParents=new Set(snapshot.jobs.filter(j=>
            j.project_id==='ants').map(j=>j.id));
        const machine=snapshot.machines.find(m=>m.id===machineId);
        if(!machine||machine.status!=='connected'||
            machine.local_executor_enabled!==true)return [];
        const observedAt=Date.now();
        const jobs=snapshot.jobs.filter(j=>j.assigned_machine_id===machineId &&
            (j.dispatch_id&&['start_requested','launch_requested','running',
                'stop_requested','unverified'].includes(j.status)||
             j.dispatch_id&&j.automation_start_session_id&&
                !j.capacity_released_at&&
                ['finished','stopped'].includes(j.status)||
             j.dispatch_id&&j.frozen_parameters?.optimizer_group===true&&
                j.status==='finished'&&!nativeOptimizerTerminalProof(j)&&
                !j.capacity_released_at||
             j.dispatch_id&&supportsEvaluation(machine.evaluation_suites,j)&&
                finalEvaluationPending(snapshot,j,observedAt)||
             j.dispatch_id&&nativeBaselinePlan(snapshot,j,observedAt)&&
                supportsEvaluation(machine.evaluation_suites,{
                    project_id:j.project_id,
                    evaluation_stop:nativeBaselinePlan(snapshot,j,observedAt).initial_evaluation_stop,
                })||
             j.dispatch_id&&j.status==='finished'&&
                Boolean(j.checkpoint_reinspection_requested_at)||
             j.status==='waiting_adapter'&&
                j.gpu_qualification?.status==='requested'));
        // A native Stop acknowledgement means the local supervisor has
        // persisted its stop intent. Further observations only need Inspect;
        // repeatedly dispatching Stop would starve unrelated running jobs.
        // A lost/stale acknowledgement keeps Stop urgent until confirmed.
        const actionFor=j=>j.status==='stop_requested'
            ?j.live_execution?.state==='stop_requested'?'inspect':'stop'
            :j.status==='start_requested'?'start'
            :j.gpu_qualification?.status==='requested'?'qualify_gpu':'inspect';
        const priority=j=>{
            const action=actionFor(j);
            if(action!=='inspect')return {stop:0,start:1,qualify_gpu:2}[action];
            if(j.checkpoint_reinspection_requested_at)return 3;
            // Starts and overdue native Inspects share one rotating tier.
            // Otherwise a sustained Start backlog can starve the inspection
            // that establishes which earlier jobs are still running. Stops
            // remain urgent and unexpired Inspects remain low priority.
            const age=observedAt-Date.parse(j.live_execution?.at||'');
            return !j.capacity_released_at&&!(age>=0&&age<AGENT_HEARTBEAT_TTL_MS/2)?1:4;
        };
        jobs.sort((a,b)=>priority(a)-priority(b));
        const ordered=[];
        for(const level of [0,1,2,3,4]) {
            const group=jobs.filter(j=>priority(j)===level);
            // Keep a separate cursor for each priority tier. An urgent
            // Stop must not reset fair rotation among pending Starts.
            const last=this.executionOrderCursor.get(machineId+':'+level);
            if(group.length>1) {
                const position=group.findIndex(j=>j.id===last);
                if(position>=0)
                    group.push(...group.splice(0,position+1));
            }
            ordered.push(...group);
        }
        for(const job of ordered) {
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id&&l.project_id===job.project_id&&
                l.machine_id===machineId&&(l.status==='acknowledged'||
                    (l.status==='released'&&l.released_at&&
                        (finalEvaluationPending(snapshot,job,observedAt)||
                         nativeBaselinePlan(snapshot,job,observedAt)||
                         Boolean(job.checkpoint_reinspection_requested_at)))));
            if(!lease)continue;
            const adapterInstalled=machine.installed_projects.includes(job.project_id);
            let action=actionFor(job);
            // Legacy Ants-v2 jobs and dependents must not initiate new
            // launches after retirement. Preserve Inspect and Stop for
            // native work that may already have been offered.
            const retired=job.project_id==='ants'||
                retiredParents.has(job.after_job_id);
            if(retired&&action==='start') {
                if(typeof job.start_first_offered_at!=='string')continue;
                action='inspect';
            }
            if(retired&&action==='qualify_gpu'&&
                !job.gpu_qualification?.first_offered_at)
                continue;
            if(action==='start') {
                const automationValid=!job.automation_start_session_id||
                    (this.automation.enabled&&
                     this.automation.session_id===job.automation_start_session_id&&
                     this.automation.agent_instances[machineId]===
                        this.agentReports.get(machineId)?.instance_id);
                const releaseValid=job.native_release_recovery?.start_origin!=='native_round_release'||
                    this.nativeRoundReleaseAuthorized(snapshot,job);
                if(!adapterInstalled||!automationValid||!releaseValid||!lease.receipt_fresh||
                    machine.native_preflight_passed!==true||
                    !supportsEvaluation(machine.evaluation_suites,job)||
                    !supportsTinyBrainsProfile(machine,job)||
                    !distributedRolloutHost(snapshot,job,machine)||
                    nativeModelSourceBlock(snapshot,job)||
                    nativeResourceBlock(job,lease)||
                    gpuStartBlock(job,lease,machine)) {
                    // A previously offered Start may already own a process.
                    // Recover observation without re-authorizing a launch.
                    if(typeof job.start_first_offered_at!=='string')continue;
                    action='inspect';
                }
            }
            let probeRecoveryOnly=false;
            if(action==='qualify_gpu') {
                const proof=job.gpu_qualification;
                if(proof.lease_id!==lease.id||proof.automation_paused||
                    !gpuProbeWorkerReady(job,lease,machine))
                    continue;
                const blocked=gpuProbeBlock(job,lease,machine);
                const releaseProbeBlocked=proof.native_round_release===true&&
                    !this.nativeRoundReleaseAuthorized(snapshot,job);
                if((blocked||releaseProbeBlocked)&&proof.first_offered_at===null&&!proof.cleanup_only)continue;
                // An offered probe can still owe a result when its lease
                // loses eligibility. The worker rejects an order without
                // a fresh receipt and reports whether cleanup is verified.
                probeRecoveryOnly=Boolean(blocked||releaseProbeBlocked||proof.cleanup_only);
            }
            if(action==='start'&&job.start_first_offered_at==null) {
                // Persist the point of no return before the order can leave
                // the coordinator, including when the HTTP reply is lost.
                const next=copy(this.state);
                next.jobs.find(j=>j.id===job.id).start_first_offered_at=now();
                this.commit(next);
            }
            if(action==='qualify_gpu'&&job.gpu_qualification.first_offered_at==null) {
                const next=copy(this.state);
                next.jobs.find(j=>j.id===job.id).gpu_qualification.first_offered_at=now();
                this.commit(next);
            }
            this.executionOrderCursor.set(machineId+':'+priority(job),job.id);
            return [{
                action,dispatch_id:action==='qualify_gpu'
                    ?job.gpu_qualification.request_id:job.dispatch_id,
                ...(action==='inspect'&&priority(job)<=3?{refresh_native_observation:true}:{}),
                job:{
                    id:job.id,project_id:job.project_id,adapter:job.adapter,
                    lease_id:job.lease_id,status:'waiting_adapter',
                    ...(job.native_release_recovery?{
                        native_release_recovery:copy(job.native_release_recovery),
                    }:{}),
                    ...(job.collector_replacement_of_job_id?{
                        collector_replacement_of_job_id:
                            job.collector_replacement_of_job_id,
                    }:{}),
                    ...(job.optimizer_restart_of_job_id?{
                        optimizer_restart_of_job_id:job.optimizer_restart_of_job_id,
                    }:{}),
                    ...(job.optimizer_restart_of_bundle_id?{
                        optimizer_restart_of_bundle_id:
                            job.optimizer_restart_of_bundle_id,
                    }:{}),
                    frozen_parameters:copy(job.frozen_parameters),
                    configuration_sha256:job.configuration_sha256,
                    continuation_of_job_id:job.continuation_of_job_id||null,
                    artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                    ...(job.resource_bundle?{resource_bundle:copy(job.resource_bundle)}:{}),
                    ...(job.model_source_job_id?{model_source_job_id:job.model_source_job_id}:{}),
                    resources:copy(job.resources),
                    ...(action==='inspect'&&job.checkpoint_reinspection_requested_at
                        ?{reverify_checkpoint:true}:{}),
                    ...(job.evaluation_stop?{evaluation_stop:copy(job.evaluation_stop)}:{}),
                    ...(action==='inspect'&&nativeBaselinePlan(snapshot,job,observedAt)
                        ?{native_baseline_evaluation:{
                            plan_id:nativeBaselinePlan(snapshot,job,observedAt).id,
                            policy:copy(nativeBaselinePlan(snapshot,job,observedAt)
                                .initial_evaluation_stop),
                            checkpoint_sha256:job.checkpoint_model_sha256,
                        }}:{}),
                    ...(lease.status==='released'?{final_checkpoint_sha256:job.checkpoint_model_sha256}:{}),
                    ...(job.expected_parent_model_sha256
                        ?{expected_parent_model_sha256:job.expected_parent_model_sha256}:{}),
                    ...(job.expected_parent_manifest_sha256?
                        {expected_parent_manifest_sha256:job.expected_parent_manifest_sha256}:{}),
                    ...(job.parent_collection_selection?
                        {parent_collection_selection:copy(job.parent_collection_selection)}:{}),
                },
                lease:{
                    id:lease.id,job_id:job.id,project_id:lease.project_id,
                    machine_id:lease.machine_id,issued_by:lease.issued_by,
                    status:lease.status,receipt_fresh:lease.receipt_fresh&&!probeRecoveryOnly,
                    resources:copy(lease.resources),
                    gpu_bindings:copy(lease.gpu_bindings||[]),
                },
            }];
        }
        return [];
    }
    // Withhold physical GPU admission capacity for existing Bees or other
    // independently owned jobs. These limits only constrain new Hub leases.
    // No native GPU quotas are created, and an issued lease is never resized.
    updateMachineGpuBudget(value) {
        if(!object(value)||value.confirm_budget!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Explicitly confirm the machine whose Hub GPU budget will change.');
        if(this.automation.enabled)
            throw Error('Disarm native queue automation before changing GPU budgets.');
        const machine=this.state.machines.find(m=>m.id===value.machine_id);
        if(!machine)throw Error('Machine not registered.');
        if(!Array.isArray(value.expected_gpus)||
            JSON.stringify(value.expected_gpus)!==JSON.stringify(machine.gpus))
            throw Error('Machine GPU configuration changed; refresh before saving its budget.');
        const before=configuredGpus(machine.gpus);
        if(!before.length)throw Error('Configure physical GPUs before setting their admission budgets.');
        const requested=configuredGpus(value.gpus);
        if(requested.length!==before.length||requested.some((g,i)=>
            g.id!==before[i].id||g.vram_mb!==before[i].vram_mb||
            g.pci_address!==before[i].pci_address))
            throw Error('GPU budgets cannot change device identity, PCI address or nominal VRAM.');
        if(this.state.reservations.some(lease=>{
            if(lease.machine_id!==machine.id||
                !['active','offered','acknowledged'].includes(lease.status))return false;
            try{return resourceRequest(lease.resources).gpu_count!==0;}
            catch{return true;} // Unreconciled leases may own the entire GPU.
        }))
            throw Error('Release or finish existing GPU-bearing leases before changing GPU budgets.');
        if(JSON.stringify(before)===JSON.stringify(requested))
            return {machine_id:machine.id,gpus:before,changed:false};
        const next=copy(this.state);
        next.machines.find(m=>m.id===machine.id).gpus=requested;
        this.commit(next);
        return {machine_id:machine.id,gpus:copy(requested),changed:true};
    }
    bindMachineGpus(value) {
        if(!object(value)||value.confirm_bind!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Explicitly confirm the machine whose GPU bindings will change.');
        const gpu=configuredGpus(value.gpus,true);
        const snapshot=this.snapshot();
        const machine=snapshot.machines.find(m=>m.id===value.machine_id);
        if(!machine||machine.status!=='connected'||
            !['amdgpu-sysfs-v1','nvidia-smi-v1'].includes(machine.gpu_inventory_source))
            throw Error('A fresh authenticated physical GPU inventory is required to bind GPUs.');
        if(gpu.some(g=>!machine.observed_gpus.some(actual=>
            actual.pci_address===g.pci_address)))
            throw Error('Configured PCI GPU does not match a currently observed physical device.');
        if(this.state.reservations.some(l=>{
            if(l.machine_id!==machine.id||
                !['active','offered','acknowledged'].includes(l.status))return false;
            try { return resourceRequest(l.resources).gpu_count!==0; }
            catch { return true; } // Unreconciled claims may own a GPU.
        }))
            throw Error('Release all GPU-bearing leases before rebinding machine GPU identities.');
        // Rebinding is a physical identity operation, not permission to
        // increase Hub GPU admission. Preserve configured budgets for the
        // same logical ID even when an older client omits the new fields.
        const retained=gpu.map(g=>{
            const previous=machine.gpus.find(old=>old.id===g.id);
            return previous?{...g,
                hub_vram_mb:Math.min(g.vram_mb,
                    previous.hub_vram_mb??previous.vram_mb),
                hub_compute_pct:previous.hub_compute_pct??100,
            }:g;
        });
        const next=copy(this.state);
        next.machines.find(m=>m.id===machine.id).gpus=retained;
        this.commit(next);
        return {machine_id:machine.id,gpus:copy(retained)};
    }
    enrollMachine(value) {
        if (!object(value) || !ID.test(String(value.machine_id || '')))
            throw Error('Invalid machine identity.');
        const next = copy(this.state);
        const machine = next.machines.find(x => x.id === value.machine_id);
        if (!machine) throw Error('Machine not registered.');
        if (machine.agent_key_sha256 && value.confirm_rotate !== true)
            throw Error('Explicitly confirm replacing this agent credential.');
        if (value.confirm_enroll !== true)
            throw Error('Explicitly confirm issuing a machine credential.');
        // 256-bit secrets are not stored in plaintext. This value is returned
        // only by this enrollment call; future snapshots never reveal it.
        const token = crypto.randomBytes(32).toString('hex');
        machine.agent_key_sha256 = fingerprint(machine.id, token);
        this.commit(next);
        this.invalidateAgentIdentity(machine.id,'Agent credential changed; review and re-arm native automation.');
        return {machine_id:machine.id, agent_token:token};
    }
    revokeMachine(value) {
        if (!object(value) || !ID.test(String(value.machine_id || '')) ||
            value.confirm_revoke !== true)
            throw Error('Explicitly confirm the machine credential revocation.');
        const next = copy(this.state);
        const machine = next.machines.find(x => x.id === value.machine_id);
        if (!machine) throw Error('Machine not registered.');
        delete machine.agent_key_sha256;
        this.commit(next);
        this.invalidateAgentIdentity(machine.id,'Agent credential revoked; review and re-arm native automation.');
        return {machine_id:machine.id, revoked:true};
    }
    invalidateAgentIdentity(machineId,reason) {
        this.agentReports.delete(machineId);
        for (const lease of this.state.reservations)
            if (lease.machine_id===machineId) this.leaseReceipts.delete(lease.id);
        for(const job of this.state.jobs)
            if(job.assigned_machine_id===machineId)
                this.executionTelemetry.delete(job.id);
        if((this.automation.enabled&&this.automation.machine_ids.includes(machineId))||
            this.state.automation_restart?.machine_ids?.includes(machineId))
            this.disarmUnsafeAutomation(Error(reason));
    }
    // The worker-only listener uses the same machine-scoped credential
    // for heartbeat and read-only evaluation scheduling. Never authorize
    // one enrolled worker to inspect another worker's dispatches.
    authenticateAgent(machineId,token) {
        const machine=ID.test(String(machineId||''))
            ?this.state.machines.find(x=>x.id===machineId):null;
        if(!machine||!machine.agent_key_sha256||
            typeof token!=='string'||!/^[0-9a-f]{64}$/.test(token)) {
            const error=Error('Agent authentication failed.');
            error.status=401;throw error;
        }
        const expected=Buffer.from(machine.agent_key_sha256,'hex');
        const supplied=Buffer.from(fingerprint(machine.id,token),'hex');
        if(expected.length!==supplied.length||
            !crypto.timingSafeEqual(expected,supplied)) {
            const error=Error('Agent authentication failed.');
            error.status=401;throw error;
        }
        return machine;
    }
    authenticatedEvaluationTargets(machineId,token,projectId,suiteId) {
        const machine=this.authenticateAgent(machineId,token);
        return this.evaluationTargets(projectId,machine.id,suiteId);
    }
    controlNativeEvaluator(value) {
        if(!object(value)||!ID.test(String(value.machine_id||''))||
            !['start','stop','restart','inspect'].includes(value.action)||
            value.confirm_control!==true||
            Object.keys(value).some(key=>![
                'machine_id','action','confirm_control',
            ].includes(key)))
            throw Error('Confirm a native Bees evaluator start, stop or status inspection.');
        const machine=this.snapshot().machines.find(item=>item.id===value.machine_id);
        const report=this.agentReports.get(value.machine_id);
        if(!machine||machine.status!=='connected'||
            !report||!UUID.test(String(report.instance_id||''))||
            !report.local_executor||report.platform!=='linux'&&report.platform!=='win32'||
            !report.installed_projects?.includes('bees_native')||
            !report.evaluation_suites?.bees_native?.length)
            throw Error('Native Bees evaluator requires a fresh enrolled execution agent and installed suite reader.');
        const next=copy(this.state);
        next.evaluator_services=next.evaluator_services||[];
        const previous=next.evaluator_services.find(item=>
            item.machine_id===value.machine_id);
        if(previous?.status==='pending'&&
            previous.agent_instance_id===report.instance_id)
            throw Error('A native evaluator control is already awaiting its machine report.');
        const command={
            command_id:crypto.randomUUID(),
            machine_id:value.machine_id,
            agent_instance_id:report.instance_id,
            action:value.action,status:'pending',
            requested_at:now(),
        };
        if(previous)Object.assign(previous,command);
        else next.evaluator_services.push(command);
        this.commit(next);
        return {command_id:command.command_id,
            machine_id:command.machine_id,action:command.action,
            status:command.status};
    }
    heartbeat(value, token) {
        if (!object(value) || !ID.test(String(value.machine_id || '')))
            throw Error('Invalid agent identity.');
        // A one-shot agent's final report can durably acknowledge native
        // results, but must not receive a new instruction it will not run.
        // In particular, constructing a Start order persists its first-offer
        // point of no return even if the final response is discarded.
        if(value.request_execution_orders!==undefined&&
            typeof value.request_execution_orders!=='boolean')
            throw Error('Invalid native instruction request preference.');
        if(value.close_agent_session!==undefined&&
            typeof value.close_agent_session!=='boolean')
            throw Error('Invalid agent session closure request.');
        if(value.close_agent_session===true&&value.request_execution_orders!==false)
            throw Error('Agent may sign off only with native instruction delivery disabled.');
        const machine=this.authenticateAgent(value.machine_id,token);
        const report = boundedReport(value);
        const received = Date.now();
        const instance=value.agent_instance_id;
        if(report.local_executor &&
            (typeof instance!=='string'||
             !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(instance)))
            throw Error('Native execution agent requires a process instance identity.');
        const previous=this.agentReports.get(machine.id);
        if(previous&&previous.local_executor&&
            (received<previous.received_ms||
                received-previous.received_ms<AGENT_HEARTBEAT_TTL_MS)&&
            instance!==previous.instance_id) {
            const error=Error('Another authenticated native agent instance is still active.');
            error.status=409;throw error;
        }
        const ids=value.observed_lease_ids==null?[]:value.observed_lease_ids;
        if (!Array.isArray(ids) || ids.length>100 ||
            ids.some(id=>typeof id!=='string'||!/^[0-9a-f-]{36}$/.test(id)) ||
            new Set(ids).size!==ids.length)
            throw Error('Invalid observed lease IDs.');
        // Only this authenticated machine may acknowledge its own offers.
        // No lease is evidence of OS resource enforcement.
        const next=copy(this.state);
        const evaluatorResult=value.evaluator_service_report;
        let newEvaluatorReport=false;
        if(evaluatorResult!==undefined&&evaluatorResult!==null) {
            const command=(next.evaluator_services||[]).find(item=>
                item.machine_id===machine.id);
            if(!object(evaluatorResult)||!command||
                !['pending','reported'].includes(command.status)||
                command.command_id!==evaluatorResult.command_id||
                command.action!==evaluatorResult.action||
                command.agent_instance_id!==instance||
                !['active','inactive','activating','deactivating','failed']
                    .includes(evaluatorResult.state)||
                evaluatorResult.diagnostic!=null&&(
                    typeof evaluatorResult.diagnostic!=='string'||
                    evaluatorResult.diagnostic.length>180)||
                Object.keys(evaluatorResult).some(key=>![
                    'command_id','action','state','diagnostic',
                ].includes(key)))
                throw Error('Native evaluator report does not match its one-use machine command.');
            if(command.status==='reported') {
                // The previous acknowledgment may have committed on the
                // server while its HTTP response was lost. The same signed
                // machine report is idempotent; conflicting bytes are not.
                if(command.state!==evaluatorResult.state||
                    command.diagnostic!==(evaluatorResult.diagnostic||null))
                    throw Error('Native evaluator completion changed after acceptance.');
            }else {
                command.status='reported';
                command.state=evaluatorResult.state;
                command.diagnostic=evaluatorResult.diagnostic||null;
                command.completed_at=now();
                newEvaluatorReport=true;
            }
        }
        // Receipt IDs are based on the agent's previous offer response,
        // while this heartbeat reports the current host capacity. Reject
        // carried-over IDs if *all* outstanding claims no longer fit.
        const withinCapacity=claimsFitCpuRam(next.reservations,machine,report.cpus,report.ram_mb);
        const acknowledgedIds=new Set();
        let changed=newEvaluatorReport;
        for (const id of ids) {
            if(!withinCapacity)break;
            const lease=next.reservations.find(x=>x.id===id &&
                x.machine_id===machine.id && x.issued_by==='experiment-hub');
            if (!lease || !['offered','acknowledged'].includes(lease.status)) continue;
            if(physicalGpuBindings(machine,report,lease.resources,lease.gpu_bindings)===null)
                continue;
            acknowledgedIds.add(id);
            if (lease.status==='offered') {
                lease.status='acknowledged';
                lease.acknowledged_at=new Date(received).toISOString();
                changed=true;
            }
        }
        const reports=value.execution_reports==null?[]:value.execution_reports;
        if(reports.length&&(!report.local_executor||
            !['linux','win32'].includes(report.platform)))
            throw Error('Native lifecycle reports require a locally enabled supported executor.');
        const acceptedReports=new Map();
        if(this.recordExecution(next,machine,reports,acceptedReports,report.evaluation_suites,received))
            changed=true;
        const gpuReports=value.gpu_qualification_reports==null
            ?[]:value.gpu_qualification_reports;
        if(gpuReports.length&&(!report.local_executor||
            report.platform!=='linux'||!report.native_preflight_passed))
            throw Error('GPU access reports require a qualified Linux executor.');
        if(this.recordGpuQualifications(next,machine,gpuReports,instance))
            changed=true;
        // A GPU that disappeared or changed after a successful access
        // proof may be reenumerated with different render-node identity.
        // Re-acknowledging the same PCI address must never revive that
        // old cgroup access proof. An explicit new probe is required.
        for(const job of next.jobs) {
            const proof=job.gpu_qualification;
            if(proof?.status!=='verified'||proof.machine_id!==machine.id)
                continue;
            const lease=next.reservations.find(l=>l.id===proof.lease_id);
            if(!lease||physicalGpuBindings(machine,report,
                lease.resources,lease.gpu_bindings)===null) {
                proof.status='stale';
                proof.diagnostic='Physical GPU identity changed or disappeared; request a fresh device access probe.';
                changed=true;
            }
        }
        if (changed) this.commit(next);
        // Do not mark receipt or liveness until the durable acknowledgement
        // succeeds. A disk failure leaves the previous lease available.
        // Check the previous heartbeat before replacing it. A reconnect
        // must not erase an expired gap before the scheduler observes it.
        if(this.automation.enabled) {
            this.reconcileAutomationContinuity(received);
        }
        // A disconnected or replaced agent must re-observe its native
        // services before evaluator worklists or resource-rate samples
        // can use their prior execution state. Clear only ephemeral data;
        // retained job ownership and native leases are unchanged.
        if(previous&&(received<previous.received_ms||
            received-previous.received_ms>=AGENT_HEARTBEAT_TTL_MS||
            previous.instance_id!==(instance||null)||
            previous.local_executor!==report.local_executor)) {
            for(const job of this.state.jobs)
                if(job.assigned_machine_id===machine.id)
                    this.executionTelemetry.delete(job.id);
        }
        this.agentReports.set(machine.id,{
            ...report,instance_id:instance||null,received_ms:received,
        });
        // A successful heartbeat supersedes the previous receipt set for
        // this machine. Missing IDs, reduced CPU/RAM capacity, or changed GPU
        // identity revoke *freshness* immediately, without freeing the
        // persisted capacity claims or touching native processes.
        for(const lease of this.state.reservations) {
            if(lease.machine_id!==machine.id)continue;
            if(!acknowledgedIds.has(lease.id))
                this.leaseReceipts.delete(lease.id);
            else if(lease.status==='acknowledged')
                this.leaseReceipts.set(lease.id,received);
        }
        let changedHistory=false,terminalHistory=false;
        for(const item of reports) {
            if(!acceptedReports.has(item.job_id))continue;
            const {progress,evaluation,evaluationIssue,resources}=acceptedReports.get(item.job_id);
            const previousTelemetry=this.executionTelemetry.get(item.job_id);
            const previousProgress=previousTelemetry?.progress||null;
            const job=this.state.jobs.find(j=>j.id===item.job_id);
            // Estimate CPU-core equivalents from authenticated server
            // arrival times, not a worker-supplied wall clock. Neither a
            // restarted agent nor a replaced native dispatch is continuous.
            const elapsedMs=received-Date.parse(previousTelemetry?.at||'');
            const cpuAfter=item.cpu_time_ns;
            const cpuBeforeSeconds=observedCpuSeconds(previousTelemetry);
            const cpuAfterSeconds=observedCpuSeconds(item);
            const continuous=previousTelemetry?.dispatch_id===item.dispatch_id&&
                previousTelemetry.agent_instance_id===instance&&
                ['running','stop_requested'].includes(previousTelemetry.state)&&
                ['running','stop_requested'].includes(item.state);
            const sampleWindow=continuous&&Number.isFinite(elapsedMs)&&
                elapsedMs>=1000&&elapsedMs<=120000;
            const cpuCores=sampleWindow&&
                cpuBeforeSeconds!==null&&cpuAfterSeconds!==null&&
                cpuAfterSeconds>=cpuBeforeSeconds
                ?1000*(cpuAfterSeconds-cpuBeforeSeconds)/elapsedMs:null;
            // These counters belong to the same verified cgroup. Compare
            // their deltas only across continuous authenticated samples;
            // counters can reset when a native owner is replaced.
            const beforePeriods=previousTelemetry?.cpu_quota_periods;
            const beforeThrottled=previousTelemetry?.cpu_throttle_events;
            const afterPeriods=item.cpu_quota_periods;
            const afterThrottled=item.cpu_throttle_events;
            const periodDelta=afterPeriods-beforePeriods;
            const throttledDelta=afterThrottled-beforeThrottled;
            const throttledPeriodPct=sampleWindow&&
                Number.isSafeInteger(beforePeriods)&&
                Number.isSafeInteger(beforeThrottled)&&
                Number.isSafeInteger(afterPeriods)&&
                Number.isSafeInteger(afterThrottled)&&
                Number.isSafeInteger(periodDelta)&&
                Number.isSafeInteger(throttledDelta)&&
                periodDelta>0&&throttledDelta>=0&&
                throttledDelta<=periodDelta
                ?100*throttledDelta/periodDelta:null;
            // Resource samples are authenticated machine/job observations.
            // GPU values are HOST-WIDE observations on the physically bound
            // device; they are not measured per-job GPU consumption.
            let chartProgress=progress;
            {
                const liveOwner=['running','stop_requested'].includes(item.state);
                const observed={};
                const add=(key,value,unit)=>{
                    if(Number.isFinite(value)&&value>=0&&value<=1e12)
                        observed[key]={value,unit};
                };
                add('job_cpu_cores',cpuCores,'scalar');
                // Process CPU-time deltas are already tied to the verified
                // native dispatch. Normalize against its frozen CPU budget,
                // not host-wide activity or an inferred worker count.
                const leasedCpus=job.resources?.cpus;
                if(Number.isSafeInteger(leasedCpus)&&leasedCpus>0&&cpuCores!==null)
                    add('job_cpu_lease_utilization_pct',
                        100*cpuCores/leasedCpus,'scalar');
                add('job_memory_mb',item.memory_current_mb,'mb');
                add('job_peak_memory_mb',Number.isFinite(item.peak_job_memory_bytes)
                    ?item.peak_job_memory_bytes/1048576:null,'mb');
                add('job_process_tasks',item.process_tasks,'count');
                // These are cumulative cgroup-v2 throttling counters, not
                // sampled durations for individual PPO stages.
                add('job_cpu_throttled_seconds',
                    item.cpu_throttled_seconds,'seconds');
                add('job_cpu_throttle_events',item.cpu_throttle_events,'count');
                add('job_cpu_quota_periods',item.cpu_quota_periods,'count');
                add('job_cpu_quota_throttled_periods_pct',
                    throttledPeriodPct,'scalar');
                const leasedRam=job.resources?.ram_mb;
                if(Number.isSafeInteger(leasedRam)&&leasedRam>0&&
                    Number.isSafeInteger(item.memory_current_mb)&&
                    item.memory_current_mb>=0)
                    add('job_memory_lease_utilization_pct',
                        100*item.memory_current_mb/leasedRam,'scalar');
                if(liveOwner)add('host_cpu_busy_pct',
                    report.host_cpu_busy_pct,'scalar');
                const lease=this.state.reservations.find(l=>
                    l.id===job.lease_id&&l.job_id===job.id);
                const bindings=lease?.gpu_bindings||[];
                for(const [i,binding] of (liveOwner?bindings:[]).entries()) {
                    const device=report.gpu_devices?.find(observedGpu=>
                        observedGpu.pci_address===binding.pci_address);
                    if(!device)continue;
                    add('host_gpu_'+i+'_busy_pct',device.gpu_busy_pct,'scalar');
                    add('host_gpu_'+i+'_used_vram_mb',device.used_vram_mb,'mb');
                    add('host_gpu_'+i+'_power_w',device.power_w,'watts');
                    add('host_gpu_'+i+'_temperature_c',device.temperature_c,'scalar');
                    add('host_gpu_'+i+'_hotspot_temperature_c',
                        device.hotspot_temperature_c,'scalar');
                    if(bindings.length===1) {
                        add('host_gpu_busy_pct',device.gpu_busy_pct,'scalar');
                        add('host_gpu_vram_mb',device.used_vram_mb,'mb');
                        add('host_gpu_power_w',device.power_w,'watts');
                        add('host_gpu_temperature_c',device.temperature_c,'scalar');
                        add('host_gpu_hotspot_temperature_c',
                            device.hotspot_temperature_c,'scalar');
                    }
                }
                // Archive validated source and host measurements before the
                // bounded chart selector/coalescer discards sampling detail.
                // This archive is observational and cannot gate training.
                const archived=progress?
                    this.observations.record(job,progress,observed,received):false;
                // When a source update is unchanged or absent, keep the
                // authenticated host observation with no invented trainer
                // step count or source timestamp. An archive failure stays
                // visible rather than being hidden by another write.
                // Terminal Inspect may legitimately report the host's busy
                // CPU/GPU after this job has exited. Do not append those
                // physical-host samples as if the finished process used them.
                if(liveOwner&&!archived&&
                    (!progress||!this.observations.trainerErrorFor(job)))
                    this.observations.recordResources(job,observed,received,
                        progress?'not_advanced':
                        item.progress_issue?'read_error':'absent');
                if(progress)
                    chartProgress={...progress,metrics:selectChartMetrics(
                        progress.metrics,observed,job.metric_selection,
                        progress.steps_completed)};
            }
            const progressAccepted=chartProgress&&this.progressHistory.record(
                job.id,job.project_id,chartProgress,received);
            if(progressAccepted)changedHistory=true;
            if(['finished','failed','stopped'].includes(job.status))
                terminalHistory=true;
            this.executionTelemetry.set(item.job_id,{
                at:new Date(received).toISOString(),state:item.state,
                dispatch_id:item.dispatch_id,agent_instance_id:instance||null,
                memory_current_mb:item.memory_current_mb??null,
                peak_job_memory_bytes:item.peak_job_memory_bytes??null,
                cpu_time_ns:cpuAfter??null,
                cpu_time_seconds:cpuAfterSeconds,
                cpu_throttled_seconds:item.cpu_throttled_seconds??null,
                cpu_throttle_events:item.cpu_throttle_events??null,
                cpu_quota_periods:item.cpu_quota_periods??null,
                cpu_cores_used:cpuCores,
                resource_usage:resources,
                process_tasks:item.process_tasks??null,
                progress:progressAccepted?chartProgress:previousProgress,
                progress_issue:item.progress_issue||null,
                evaluation:evaluation||previousTelemetry?.evaluation||null,
                evaluation_issue:evaluationIssue,
            });
        }
        if(changedHistory||terminalHistory)
            this.progressHistory.persist(received,terminalHistory);
        if(value.close_agent_session===true) {
            // The final report of a --once agent closes its native session.
            // It has no further instruction loop. Do not leave a fictitious
            // connected worker, a fresh execution lease receipt or an agent
            // instance lock until the heartbeat TTL expires. Persisted job
            // ownership and native service journals remain untouched.
            this.invalidateAgentIdentity(machine.id,
                'One-shot native agent signed off; review and re-arm automation with a continuously running agent.');
            return {machine_id:machine.id,status:'connected',
                heartbeat_ttl_seconds:Math.floor(AGENT_HEARTBEAT_TTL_MS/1000),
                lease_offers:[],execution_orders:[]};
        }
        // A --once agent closes its native session; never authorize a new
        // learner on the closing heartbeat when it cannot receive a Start.
        this.advancePendingDistributedGroups();
        this.advanceDistributedLaunches();
        this.advancePendingResourceBundles();
        this.advanceNativeBeesRounds();
        // An agent awaiting a slow native Start/Stop/probe can renew
        // authenticated liveness without receiving a second instruction.
        // This does not close the session or release native ownership.
        return {machine_id:machine.id,status:'connected',
            heartbeat_ttl_seconds:Math.floor(AGENT_HEARTBEAT_TTL_MS/1000),
            lease_offers:this.state.reservations.filter(x=>
                x.machine_id===machine.id && x.issued_by==='experiment-hub' &&
                ['offered','acknowledged'].includes(x.status)
            ).map(x=>({
                id:x.id,status:x.status,resources:copy(x.resources),
                // This is an expected PCI binding, not GPU isolation or
                // a process launch token. The agent checks it against sysfs.
                gpu_bindings:physicalGpuBindings(machine,report,x.resources,x.gpu_bindings)||[],
            })),
            execution_orders:value.request_execution_orders===false
                ?[]:this.executionOrders(machine.id),
            evaluator_service_command:value.request_execution_orders===false
                ?null:(()=>{
                    const command=(this.state.evaluator_services||[]).find(item=>
                        item.machine_id===machine.id&&item.status==='pending'&&
                        item.agent_instance_id===instance);
                    return command?{command_id:command.command_id,
                        action:command.action}:null;
                })()};
    }
    project(id) {
        const project = this.state.projects.find(p=>p.id===id);
        if (!project) throw Error('Unknown project.');
        return project;
    }
    addProject(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase project ID.');
        if(value.id==='ants')throw Error('Ants-v2 Hub integration is retired; use a distinct project ID.');
        if (this.state.projects.some(p=>p.id===value.id)) throw Error('Project already exists.');
        // New projects are definitions only until a trusted server-side adapter is installed.
        const project = {
            id:value.id, name:name(value.name,'Project name'), adapter:'unconfigured',
            description:String(value.description||'').slice(0,500), created_at:now(),
        };
        const next = copy(this.state);
        next.projects.push(project);
        this.commit(next);
        return copy(project);
    }
    saveConfiguration(value) {
        if (!object(value)) throw Error('Missing configuration.');
        const project = this.project(value.project_id);
        if(project.id==='ants')throw Error('Ants-v2 Hub integration is retired.');
        const params = config(value.parameters);
        const stop=evaluationPolicy(value.evaluation_stop);
        if(stop&&project.adapter==='bees')
            throw Error('Bees evaluation targets use the existing Bees experiment configuration.');
        const time = now();
        const next = copy(this.state);
        let item;
        if (value.id) {
            item = next.configurations.find(c=>c.id===value.id && c.project_id===project.id);
            if (!item) throw Error('Configuration not found.');
            if (item.revision !== value.revision) throw Error('Configuration changed; reopen it before saving.');
            item.name = name(value.name,'Configuration name');
            item.parameters = params;
            item.revision += 1;
            item.updated_at = time;
        } else {
            item = {
                id:crypto.randomUUID(), project_id:project.id,
                name:name(value.name,'Configuration name'), parameters:params,
                revision:1, created_at:time, updated_at:time,
            };
            next.configurations.push(item);
        }
        if(stop)item.evaluation_stop=stop;
        else if(Object.hasOwn(value,'evaluation_stop'))delete item.evaluation_stop;
        this.commit(next);
        return copy(item);
    }
    queueConfiguration(value) {
        const next=copy(this.state);
        const job=this.queueIn(next,value);
        if(job.project_id==='bees_native_optimizer_worker'||
            job.frozen_parameters?.optimizer_group===true)
            throw Error('Native distributed PPO ranks require one atomically reserved Hub resource bundle.');
        this.commit(next);
        return copy(job);
    }
    // Explicitly approved, bounded native rounds use the same collector,
    // optimizer and native Start owners as individually scheduled jobs. A
    // round creates NEW physical experience from the latest verified model,
    // then updates that same MA-POCA optimizer; no recorded experience is
    // silently replayed in a later round.
    scheduleNativeBeesRounds(value) {
        if(!object(value)||value.confirm_schedule!==true||
            value.confirm_gpu_start!==true||
            Object.keys(value).some(key=>![
                'source_job_id','collector_members','optimizer_members',
                'round_count','confirm_schedule','confirm_gpu_start',
            ].includes(key))||
            !UUID.test(String(value.source_job_id||''))||
            !Number.isSafeInteger(value.round_count)||
            value.round_count<1||value.round_count>8)
            throw Error('Select one verified native model and explicitly approve 1–8 complete GPU training rounds.');
        const source=this.state.jobs.find(job=>job.id===value.source_job_id);
        if(source?.project_id!=='bees_native'||source.status!=='finished'||
            !checkpointContinuationComplete(source)||!source.capacity_released_at||
            !SHA256.test(String(source.checkpoint_model_sha256||''))||
            nativeOptimizerGroupBlock(this.state,source))
            throw Error('Native training rounds require a fully released, verified checkpoint and synchronized optimizer group.');
        if(this.state.jobs.some(job=>
            (job.continuation_of_job_id===source.id||
             job.after_job_id===source.id)&&
            occupiesSuccessorSlot(job,this.state.jobs)))
            throw Error('The selected model already has an unresolved checkpoint successor; do not generate physical experience for a competing continuation.');
        if((this.state.native_round_plans||[]).some(plan=>
            plan.status!=='complete'&&plan.status!=='cancelled'&&
            plan.status!=='disarmed'&&
            (plan.source_job_id===source.id||
             plan.origin_source_job_id===source.id)))
            throw Error('This native model already has an unresolved training-round plan.');
        const validateMembers=(members,kind)=>{
            const lower=2,upper=kind==='collector'?16:8;
            if(!Array.isArray(members)||members.length<lower||members.length>upper)
                throw Error('Native round '+kind+' stage requires '+lower+'–'+upper+' fixed machine members.');
            const machines=new Set();
            const selected=[];
            const hosts=this.snapshot().machines;
            for(const [index,item] of members.entries()) {
                if(!object(item)||Object.keys(item).some(key=>![
                    'configuration_id','expected_revision','machine_id',
                    'resources','metric_selection',
                ].includes(key))||!ID.test(String(item.machine_id||''))||
                    machines.has(item.machine_id))
                    throw Error('Native round members require unique fixed machine identities and saved configurations.');
                machines.add(item.machine_id);
                const expected=kind==='collector'?'bees_native_collect':
                    index===0?'bees_native':'bees_native_optimizer_worker';
                const configuration=this.state.configurations.find(config=>
                    config.id===item.configuration_id&&
                    config.revision===item.expected_revision&&
                    config.project_id===expected);
                const parameters=configuration?.parameters;
                if(!configuration||
                    (parameters.continual_release!==undefined&&
                        (expected!=='bees_native'||parameters.continual_release!==true))||
                    (kind==='collector'&&(
                        parameters.optimizer_group===true||
                        parameters.collection_id!==undefined||
                        parameters.rollout_world_start!==undefined||
                        parameters.rollout_worlds!==undefined||
                        parameters.bundle_directory!==undefined||
                        parameters.source_run_contract_sha256!==undefined||
                        parameters.rollout_bundles!==undefined))||
                    (kind==='optimizer'&&(
                        parameters.optimizer_group!==true||
                        !Array.isArray(parameters.rollout_bundles)||
                        parameters.rollout_bundles.length!==0)))
                    throw Error('Native round '+kind+' configuration must be a saved physical collector or empty-bundle MA-POCA optimizer template.');
                const host=hosts.find(machine=>machine.id===item.machine_id);
                if(host?.status!=='connected'||host.local_executor_enabled!==true||
                    host.native_preflight_passed!==true||
                    !host.installed_projects?.includes(expected)||
                    !UUID.test(String(this.agentReports.get(item.machine_id)?.instance_id||'')))
                    throw Error('Native round members require fresh authenticated machines with their exact installed project.');
                const resources=resourceRequest(item.resources);
                if(kind==='optimizer'&&(
                    resources.gpu_mode!=='exclusive'||![1,2].includes(resources.gpu_count)||
                    resources.cpus<resources.gpu_count))
                    throw Error('Synchronized optimizer ranks require exclusive leased GPU and CPU budgets.');
                selected.push({
                    configuration_id:item.configuration_id,
                    expected_revision:item.expected_revision,
                    machine_id:item.machine_id,
                    resources:copy(resources),
                    ...(item.metric_selection?{metric_selection:copy(item.metric_selection)}:{}),
                });
            }
            return selected;
        };
        const collectors=validateMembers(value.collector_members,'collector');
        const optimizers=validateMembers(value.optimizer_members,'optimizer');
        // Every approved physical round has exactly one simulation
        // contract across its heterogeneous collector machines. This is
        // already required by the optimizer bundle verifier; reject an
        // incompatible curriculum before assigning any expensive GPU work.
        const selectedMachines=this.snapshot().machines;
        const collectorSources=Array.from({length:value.round_count},(_,index)=>{
            const hashes=collectors.map(member=>nativeBeesStageSource(
                selectedMachines.find(machine=>machine.id===member.machine_id),
                index+1,
            ));
            if(hashes.some(hash=>!SHA256.test(String(hash||''))))
                throw Error('All native collectors must advertise a pinned source for curriculum round '+(index+1)+'.');
            if(new Set(hashes).size!==1)
                throw Error('Native collectors have different installed physical Level sources for curriculum round '+(index+1)+'.');
            return hashes[0];
        });
        const configurations=new Map(this.state.configurations.map(item=>[item.id,item]));
        // Each approved round consumes fresh reproducible physical seeds.
        // Different collectors may start with different approved offsets:
        // the native training bundle already checks each seed cohort and
        // each machine's disjoint global World partition separately.
        // Freeze the original offsets with the exact machine order.
        const seeds=collectors.map(entry=>
            configurations.get(entry.configuration_id)?.parameters?.seed_offset);
        if(seeds.some(seed=>!Number.isSafeInteger(seed)||seed<0||
            seed>Number.MAX_SAFE_INTEGER-value.round_count+1))
            throw Error('Native training-round collector seed offsets must be safe across every approved round.');
        const ownerConfiguration=configurations.get(optimizers[0].configuration_id);
        const owner=ownerConfiguration?.parameters;
        const targetEvaluation=evaluationPolicy(ownerConfiguration?.evaluation_stop);
        const sourceEvaluation=evaluationPolicy(source.evaluation_stop);
        // The source's original evaluation policy is immutable. A new
        // holdout uses the existing evaluator on its original publisher
        // machine under a plan-scoped dispatch, with no new native Start.
        const initialEvaluation=targetEvaluation&&
            JSON.stringify(targetEvaluation)!==JSON.stringify(sourceEvaluation)
            ?targetEvaluation:null;
        if(initialEvaluation) {
            const host=selectedMachines.find(machine=>machine.id===source.assigned_machine_id);
            if(host?.status!=='connected'||
                evaluationRules(initialEvaluation).some(rule=>
                    !host.evaluation_suites?.bees_native?.includes(rule.suite_id)))
                throw Error('Starting native checkpoint machine has no qualified independent evaluator for the selected target suites.');
        }
        const ownerThreads=optimizers[0].resources.gpu_count;
        if(!SHA256.test(String(owner?.optimizer_inputs_sha256||''))||
            owner?.config_sha256!==source.frozen_parameters?.config_sha256||
            owner.seed_offset!==0||
            optimizers.some((entry,index)=>{
                const candidate=configurations.get(entry.configuration_id)?.parameters;
                return candidate.optimizer_inputs_sha256!==owner.optimizer_inputs_sha256||
                    candidate.seed_offset!==0||
                    (candidate.refresh_opponent_slot??null)!==
                        (owner.refresh_opponent_slot??null)||
                    entry.resources.gpu_count!==ownerThreads;
            }))
            throw Error('Native multi-round optimizer templates must share exact installed MA-POCA source identity, fixed GPU ranks and checkpoint lineage before collecting new physical experience.');
        if(optimizers[0].machine_id!==source.assigned_machine_id)
            throw Error('The new optimizer checkpoint publisher must remain on its verified predecessor machine.');
        const machines=[...new Set([...collectors,...optimizers].map(item=>item.machine_id))];
        const instances=Object.fromEntries(machines.map(id=>[
            id,this.agentReports.get(id).instance_id,
        ]));
        const next=copy(this.state),timestamp=now();
        const plan={
            id:crypto.randomUUID(),status:'pending_collectors',
            source_job_id:source.id,origin_source_job_id:source.id,
            source_model_sha256:source.checkpoint_model_sha256,
            collector_members:collectors,optimizer_members:optimizers,
            agent_instances:instances,
            round_count:value.round_count,completed_rounds:0,
            collector_seed_offsets:seeds,
            collector_source_contracts:collectorSources,round_history:[],
            ...(initialEvaluation?{initial_evaluation_stop:initialEvaluation}:{}),
            approved_at:timestamp,updated_at:timestamp,
            reason:'Approved for exact-source physical collection followed by one synchronized optimizer per round.',
        };
        next.native_round_plans=next.native_round_plans||[];
        if(next.native_round_plans.length>=64)
            throw Error('Resolve old native training-round plans before scheduling more.');
        next.native_round_plans.push(plan);
        this.commit(next);
        // An eligible stage may reserve and schedule immediately. On capacity
        // contention it stays pending; the existing agent heartbeat retries.
        this.advanceNativeBeesRounds();
        return copy((this.state.native_round_plans||[]).find(item=>item.id===plan.id));
    }
    // Explicit operator approval is required after either a coordinator
    // restart or a newly reserved replacement for a failed optimizer.
    // Keep the original frozen physical work and exact machine-owned
    // resource claims; this never relaunches a stopped rank.
    resumeNativeBeesRounds(value) {
        if(!object(value)||Object.keys(value).some(key=>![
            'plan_id','confirm_resume','confirm_gpu_start',
        ].includes(key))||
            !UUID.test(String(value.plan_id||''))||
            value.confirm_resume!==true||value.confirm_gpu_start!==true)
            throw Error('Explicitly reauthorize the exact native round plan and GPU Starts.');
        const plan=(this.state.native_round_plans||[])
            .find(item=>item.id===value.plan_id);
        const coordinatorRestart=plan?.status==='disarmed'&&
            plan.reason?.startsWith('Coordinator restarted;');
        const optimizerReplacement=plan?.status==='needs_review'&&
            plan.reason==='Failed synchronized PPO group replaced with new machine-owned GPU claims; reauthorize the native round before any Start.'&&
            plan.restart_phase==='awaiting_optimizer';
        if(!coordinatorRestart&&!optimizerReplacement)
            throw Error('Only a disarmed native round or an explicitly reviewed replacement optimizer bundle may resume.');
        // Older persisted plans did not retain the suspended stage. Their
        // durable bundle/job identities identify it without inventing work.
        let phase=plan.restart_phase;
        if(!phase) {
            if(plan.optimizer_bundle_id&&plan.optimizer_job_ids)
                phase='awaiting_optimizer';
            else if(plan.collectors_bundle_id&&plan.collectors_job_ids) {
                const jobs=plan.collectors_job_ids.map(id=>
                    this.state.jobs.find(job=>job.id===id));
                phase=jobs.every(job=>job?.status==='finished'&&
                    job.capacity_released_at)
                    ?'pending_optimizer':'awaiting_collectors';
            }else if(!plan.collectors_bundle_id&&!plan.optimizer_bundle_id)
                phase='pending_collectors';
        }
        if(!['pending_collectors','awaiting_collectors',
            'pending_optimizer','awaiting_optimizer'].includes(phase)||
            (optimizerReplacement&&phase!=='awaiting_optimizer'))
            throw Error('Original native training phase cannot be recovered safely.');
        const source=this.state.jobs.find(job=>job.id===plan.source_job_id);
        if(source?.status!=='finished'||!checkpointContinuationComplete(source)||
            !source.capacity_released_at||
            source.checkpoint_model_sha256!==plan.source_model_sha256||
            nativeOptimizerGroupBlock(this.state,source))
            throw Error('Original native PPO checkpoint is no longer a verified, released source.');
        const snapshot=this.snapshot(),instances={};
        for(const [machineId,originalInstance] of Object.entries(plan.agent_instances||{})) {
            const machine=snapshot.machines.find(item=>item.id===machineId);
            const report=this.agentReports.get(machineId);
            if(machine?.status!=='connected'||machine.local_executor_enabled!==true||
                machine.native_preflight_passed!==true||
                !UUID.test(String(report?.instance_id||'')))
                throw Error('Original native round machines must reconnect and qualify before reauthorization.');
            instances[machineId]=report.instance_id;
            if(!UUID.test(String(originalInstance||'')))
                throw Error('Original native agent identity is missing.');
        }
        if(Object.keys(instances).length!==
            new Set([...plan.collector_members,...plan.optimizer_members].map(
                item=>item.machine_id)).size)
            throw Error('Approved native round machine roster changed.');
        if(phase.endsWith('collectors')&&plan.collector_source_contracts) {
            const expected=plan.collector_source_contracts[plan.completed_rounds];
            if(!SHA256.test(String(expected||''))||
                plan.collector_members.some(member=>nativeBeesStageSource(
                    snapshot.machines.find(machine=>machine.id===member.machine_id),
                    plan.completed_rounds+1,
                )!==expected))
                throw Error('Original native curriculum source changed during coordinator recovery.');
        }
        if(phase.startsWith('awaiting_')) {
            const stage=phase.slice('awaiting_'.length);
            const ids=plan[stage+'_job_ids'],bundleId=plan[stage+'_bundle_id'];
            if(!UUID.test(String(bundleId||''))||
                !Array.isArray(ids)||ids.length<2)
                throw Error('Original native round has no reserved resource bundle.');
            const jobs=ids.map(id=>this.state.jobs.find(job=>job.id===id));
            if(jobs.some((job,index)=>!job||
                job.resource_bundle?.id!==bundleId||
                job.resource_bundle?.index!==index+1||
                job.resource_bundle?.size!==ids.length||
                job.assigned_machine_id!==plan[stage+'_members'][index].machine_id))
                throw Error('Original native round job and machine ownership changed.');
            if(optimizerReplacement) {
                // A reviewed replacement is a NEW whole synchronized
                // optimizer, never a revived rank of the failed group.
                // Reconcile each replacement with its immutable predecessor
                // and the released original claim before permitting Start.
                const original=jobs.map(job=>this.state.jobs.find(old=>
                    old.id===job.optimizer_restart_of_job_id));
                const previousBundle=original[0]?.resource_bundle?.id;
                if(!UUID.test(String(previousBundle||''))||
                    previousBundle===bundleId||
                    new Set(original.map(job=>job?.id)).size!==jobs.length||
                    original.some((old,index)=>
                        !old||old.resource_bundle?.id!==previousBundle||
                        old.resource_bundle?.index!==index+1||
                        old.resource_bundle?.size!==jobs.length||
                        old.project_id!==jobs[index].project_id||
                        jobs[index].optimizer_restart_of_bundle_id!==previousBundle||
                        old.assigned_machine_id!==jobs[index].assigned_machine_id||
                        JSON.stringify(old.resources)!==JSON.stringify(jobs[index].resources)||
                        JSON.stringify(old.frozen_parameters)!==
                            JSON.stringify(jobs[index].frozen_parameters)||
                        !['finished','failed','stopped'].includes(old.status)||
                        !old.capacity_released_at||
                        !this.state.reservations.some(lease=>
                            lease.id===old.lease_id&&
                            lease.job_id===old.id&&
                            lease.status==='released'&&lease.released_at)))
                    throw Error('Replacement native optimizer rank is not the exact released original owner.');
            }
            // A restored registry status is not evidence that its native
            // CPU/RAM/GPU claim is still present. Match every original
            // bundle member against its durable Hub-issued lease before
            // allowing any further Start or checkpoint transition.
            for(const job of jobs) {
                const lease=this.state.reservations.find(item=>
                    item.id===job.lease_id&&item.job_id===job.id&&
                    item.project_id===job.project_id&&
                    item.machine_id===job.assigned_machine_id&&
                    item.issued_by==='experiment-hub');
                const terminal=job.status==='finished'&&job.capacity_released_at;
                if(!lease||(terminal
                    ?lease.status!=='released'||!lease.released_at
                    :!['offered','acknowledged'].includes(lease.status)))
                    throw Error('Native round member has no matching original resource lease.');
            }
            const allWaiting=jobs.every(job=>
                job.status==='waiting_adapter'&&!job.dispatch_id);
            const allFinished=jobs.every(job=>
                job.status==='finished'&&job.capacity_released_at);
            // Distributed optimizer ranks can finish and release capacity
            // at different times. Such a mixed terminal/running group still
            // has one original authorized Start, and the Hub must preserve
            // all its independent machine-owned processes and leases.
            const owned=jobs.every(job=>
                job.status==='finished'&&job.capacity_released_at||
                stage==='optimizer'&&job.id===jobs[0].id&&
                    job.native_release_pending&&
                    ['failed','waiting_adapter'].includes(job.status)||
                ['start_requested','launch_requested','running','stop_requested']
                    .includes(job.status)&&UUID.test(String(job.dispatch_id||'')));
            if(!allWaiting&&!owned)
                throw Error('Mixed or uncertain native resource owners require manual review.');
            if(!allFinished&&jobs.some(job=>
                plan.agent_instances[job.assigned_machine_id]!==instances[job.assigned_machine_id]))
                throw Error('An original reserved native machine agent restarted; its outstanding lease requires manual review.');
            const plans=(this.state.bundle_start_plans||[])
                .filter(item=>item.bundle_id===bundleId);
            const latest=plans.at(-1);
            if(latest&&['needs_review','expired','cancelled'].includes(latest.status))
                throw Error('The original native bundle Start requires separate review.');
            if(owned&&!allFinished&&latest?.status!=='authorized')
                throw Error('Native jobs cannot be resumed without their original authorized Start.');
            if(allWaiting&&latest?.status==='authorized')
                throw Error('Authorized native Start has unresolved machine execution ownership.');
            if(allWaiting&&latest&&latest.status!=='disarmed'&&
                latest.status!=='pending')
                throw Error('Original native Start cannot be safely reauthorized.');
        }
        const next=copy(this.state);
        const resumed=next.native_round_plans.find(item=>item.id===plan.id);
        resumed.status=phase;
        resumed.agent_instances=instances;
        delete resumed.restart_phase;
        resumed.updated_at=now();
        resumed.reason=optimizerReplacement
            ?'Replacement synchronized optimizer group explicitly reauthorized; new GPU owners retain the original verified physical inputs.'
            :'Native training reauthorized after coordinator restart; original jobs and claims retained.';
        this.commit(next);
        // The normal heartbeat workflow owns all further resource decisions.
        // It will reissue a Start plan only for an entirely unstarted
        // reserved bundle; this approval never directly launches a process.
        this.advanceNativeBeesRounds();
        return copy(this.snapshot().native_round_plans.find(item=>item.id===plan.id));
    }

    cancelNativeBeesRounds(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.plan_id||''))||
            Object.keys(value).some(key=>!['plan_id','confirm_cancel'].includes(key)))
            throw Error('Confirm the exact native training-round plan to disarm.');
        const next=copy(this.state),plan=(next.native_round_plans||[])
            .find(item=>item.id===value.plan_id);
        if(!plan||!['pending_collectors','awaiting_collectors',
            'pending_optimizer','awaiting_optimizer','needs_review','disarmed']
            .includes(plan.status))
            throw Error('Only an unresolved native training-round plan can be disarmed.');
        const at=now(),bundles=[];
        for(const stage of ['collectors','optimizer']) {
            const bundleId=plan[stage+'_bundle_id'];
            const ids=plan[stage+'_job_ids'];
            if(bundleId==null&&ids==null)continue;
            if(!UUID.test(String(bundleId||''))||
                !Array.isArray(ids)||ids.length<2)
                throw Error('Native training round has an incomplete '+stage+' resource bundle.');
            const request={bundle_id:bundleId,expected_job_ids:ids,
                confirm_stop:true};
            const members=exactResourceBundle(next,request,'confirm_stop');
            if(members.some((job,index)=>
                job.assigned_machine_id!==plan[stage+'_members'][index]?.machine_id&&
                !(job.status==='cancelled'&&!job.assigned_machine_id&&!job.lease_id)))
                throw Error('Native training round '+stage+' machine ownership changed.');
            // All old and active bundle Stops, cancellations and lease
            // releases share this one registry transaction with the round
            // decision. A failure leaves the live registry unchanged.
            this.stopResourceBundleIn(next,request,at);
            bundles.push(bundleId);
        }
        plan.status='cancelled';
        plan.reason='Future native rounds disarmed; original resource bundles cancelled or sent Stop requests. Unverified and failed owners retain their leases for review.';
        plan.updated_at=at;
        this.commit(next);
        const members=next.jobs.filter(job=>bundles.includes(job.resource_bundle?.id));
        return {plan_id:plan.id,status:plan.status,active_bundles:bundles,
            stopped_pending:members.filter(job=>job.status==='stop_requested').map(job=>job.id),
            needs_review:members.filter(job=>['failed','unverified'].includes(job.status)||
                job.status==='finished'&&!job.capacity_released_at).map(job=>job.id),
            note:'No active or uncertain native owner is released without a verified terminal receipt.'};
    }
    // An authenticated agent heartbeat advances at most one stage per plan.
    // The queue transaction also commits its stage ID, so a crash between
    // reserving GPUs and the next heartbeat cannot create duplicate jobs.
    advanceNativeReleaseRecovery(plan,owner,members,snapshot) {
        // All recovery actions are part of the existing authorized native
        // round. Only the original rank zero may run, and only after the
        // other ranks have durably finished the same optimizer collective.
        if(Object.entries(plan.agent_instances).some(([id,instance])=>{
            const live=this.agentReports.get(id);
            return live?.instance_id&&live.instance_id!==instance;
        }))throw Error('A native round agent changed; release recovery requires renewed authorization.');
        if(Object.keys(plan.agent_instances).some(id=>
            snapshot.machines.find(machine=>machine.id===id)?.status!=='connected'))
            return 'Awaiting original authenticated machines before release recovery.';
        if(!this.state.bundle_start_plans?.some(launch=>
            launch.bundle_id===plan.optimizer_bundle_id&&launch.status==='authorized'))
            return 'Original optimizer Start authorization is unavailable; manual release recovery is required.';
        const proof=owner.native_release_pending;
        if(members.slice(1).some(worker=>worker.status!=='finished'||!worker.capacity_released_at))
            return 'Committed PPO checkpoint retained; awaiting terminal proof and released worker capacity.';
        if(members.slice(1).some(worker=>
            worker.optimizer_completion?.input_sha256!==proof.input_sha256||
            worker.optimizer_completion?.model_consensus_sha256!==proof.model_consensus_sha256))
            throw Error('Original optimizer workers disagree with the committed checkpoint; automatic release recovery is forbidden.');
        if(owner.status==='failed') {
            // A bounded retry handles interruption, not persistent release
            // errors. Further attempts remain an explicit operator decision.
            if(owner.native_release_attempts?.length)
                return 'Release-only recovery failed; committed PPO checkpoint retained for manual recovery.';
            try {
                this.prepareNativeReleaseRecovery({job_id:owner.id,confirm_recover:true},plan.id);
            }catch(error) {
                return 'Release recovery awaiting original ownership or capacity: '+String(error.message||error).slice(0,140);
            }
            return null;
        }
        if(owner.native_release_recovery?.round_plan_id!==plan.id)
            return 'Committed PPO checkpoint retained; awaiting manually prepared release recovery Start.';
        const lease=snapshot.reservations.find(item=>item.id===owner.lease_id);
        const host=snapshot.machines.find(item=>item.id===owner.assigned_machine_id);
        if(host?.agent_platform==='linux'&&owner.resources.gpu_count>0&&
            owner.gpu_qualification?.current!==true) {
            if(owner.gpu_qualification?.status==='requested')
                return 'Awaiting GPU access proof for the release-only recovery.';
            if(owner.gpu_qualification?.status==='rejected'||
                owner.gpu_qualification?.cleanup_required)
                return 'Release recovery GPU qualification requires operator review.';
            const block=gpuProbeBlock(owner,lease,host);
            if(block)return 'Release recovery: '+block;
            this.requestGpuQualification({job_id:owner.id,confirm_qualify:true},'native_round_release');
            return null;
        }
        const readiness=nativeStartReadiness(snapshot,owner);
        if(!readiness.eligible)return 'Release recovery: '+readiness.reason;
        this.authorizeStart({job_id:owner.id,confirm_start:true,release_on_terminal:true},
            'native_round_release');
        return null;
    }
    advanceNativeBeesRounds() {
        const ids=(this.state.native_round_plans||[])
            .filter(plan=>['pending_collectors','awaiting_collectors',
                'pending_optimizer','awaiting_optimizer'].includes(plan.status))
            .map(plan=>plan.id);
        for(const id of ids) {
            const plan=(this.state.native_round_plans||[]).find(item=>item.id===id);
            if(!plan||!['pending_collectors','awaiting_collectors',
                'pending_optimizer','awaiting_optimizer'].includes(plan.status))
                continue;
            const update=(status,reason,extra={})=>{
                const next=copy(this.state);
                const current=next.native_round_plans.find(item=>item.id===id);
                if(current?.status!==plan.status)return;
                Object.assign(current,extra,{status,reason,updated_at:now()});
                this.commit(next);
            };
            try {
                const source=this.state.jobs.find(job=>job.id===plan.source_job_id);
                if(source?.status!=='finished'||!checkpointContinuationComplete(source)||
                    !source.capacity_released_at||
                    source.checkpoint_model_sha256!==plan.source_model_sha256||
                    nativeOptimizerGroupBlock(this.state,source))
                    throw Error('Frozen native optimizer source or its original worker group changed.');
                if(plan.status==='pending_collectors'&&
                    plan.completed_rounds===0) {
                    // The standalone session evaluates its initial model
                    // before spending any PPO resources. Honor that same
                    // independent evidence when a Hub continuation uses the
                    // exact same saved evaluation policy and checkpoint.
                    // Different evaluation definitions cannot be inferred
                    // from the source job's unrelated prior holdout.
                    const selected=plan.optimizer_members[0];
                    const target=this.state.configurations.find(config=>
                        config.id===selected.configuration_id&&
                        config.revision===selected.expected_revision);
                    if(!target)
                        throw Error('Approved native optimizer evaluation configuration changed.');
                    const sourcePolicy=evaluationPolicy(source.evaluation_stop);
                    const targetPolicy=evaluationPolicy(target.evaluation_stop);
                    if(plan.initial_evaluation_stop) {
                        if(JSON.stringify(plan.initial_evaluation_stop)!==
                            JSON.stringify(targetPolicy))
                            throw Error('Approved initial native evaluation policy changed.');
                        const rules=evaluationRules(plan.initial_evaluation_stop);
                        const receipts=rules.map(rule=>
                            plan.initial_evaluation_receipts?.find(receipt=>{
                                if(receipt.suite_id!==rule.suite_id)return false;
                                try{
                                    const checked=evaluationEvidence(receipt,rule,plan.id);
                                    return checked.checkpoint_sha256===
                                        source.checkpoint_model_sha256&&
                                        SHA256.test(String(checked.suite_sha256||''))&&
                                        checked.trials>=rule.min_trials;
                                }catch{return false;}
                            })
                        );
                        if(receipts.some(receipt=>!receipt)) {
                            if(Date.now()>=Date.parse(plan.approved_at)+
                                FINAL_EVALUATION_GRACE_MS)
                                throw Error('Newly approved native baseline evaluation did not finish before the independent evaluation window closed.');
                            const reason='Awaiting independent evaluation of the starting checkpoint against the newly approved suite policy.';
                            if(plan.reason!==reason)update(plan.status,reason);
                            continue;
                        }
                        const passed=evaluationComplete(
                            plan.initial_evaluation_stop.all_of
                                ?{all_of:receipts}:receipts[0],
                            plan.initial_evaluation_stop,plan.id);
                        if(passed) {
                            update('complete',
                                'Starting checkpoint meets the newly approved independent evaluation target; no native collection or PPO Starts required.',
                                {
                                    initial_checkpoint_job_id:source.id,
                                    initial_checkpoint_sha256:source.checkpoint_model_sha256,
                                    initial_evaluation_receipts:receipts,
                                });
                            continue;
                        }
                    }else if(sourcePolicy&&targetPolicy&&
                        JSON.stringify(sourcePolicy)===JSON.stringify(targetPolicy)) {
                        const {missing,receipts,passed}=
                            nativeRoundFinalEvaluation(source);
                        if(missing.length) {
                            if(!finalEvaluationPending(this.snapshot(),source,Date.now()))
                                throw Error('Starting native checkpoint has no complete independent evaluation for the approved stopping policy.');
                            const reason='Awaiting independent baseline evaluation before the first native collection.';
                            if(plan.reason!==reason)update(plan.status,reason);
                            continue;
                        }
                        if(passed) {
                            update('complete',
                                'Starting checkpoint already meets the approved independent evaluation target; no new collection or PPO Starts required.',
                                {
                                    initial_checkpoint_job_id:source.id,
                                    initial_checkpoint_sha256:source.checkpoint_model_sha256,
                                    initial_evaluation_receipts:receipts.map(receipt=>({
                                        suite_id:receipt.suite_id,
                                        suite_sha256:receipt.suite_sha256,
                                        successes:receipt.successes,
                                        trials:receipt.trials,
                                        checkpoint_sha256:receipt.checkpoint_sha256,
                                        dispatch_id:receipt.dispatch_id,
                                    })),
                                });
                            continue;
                        }
                    }
                }
                if(plan.status==='pending_collectors') {
                    try {
                        verifiedNativeModelMirror(this,source);
                    }catch(error) {
                        // Artifact transport is asynchronous and remains
                        // owned by the original authenticated publisher.
                        // Never reserve a remote collector against an
                        // incomplete checkpoint, or invent model bytes.
                        if(error.status===409) {
                            const reason='Awaiting the complete authenticated model checkpoint, manifest and fixed-opponent mirror before leasing collector CPUs or GPUs.';
                            if(plan.reason!==reason)
                                update(plan.status,reason);
                            continue;
                        }
                        throw error;
                    }
                }
                const snapshot=this.snapshot();
                const phase=plan.status.endsWith('collectors')?'collectors':'optimizer';
                let members=null,bundleId=null;
                if(plan.status.startsWith('awaiting_')) {
                    members=(plan[phase+'_job_ids']||[]).map(jobId=>
                        snapshot.jobs.find(job=>job.id===jobId));
                    bundleId=plan[phase+'_bundle_id'];
                    if(!UUID.test(String(bundleId||''))||
                        members.length!==plan[phase+'_members'].length||
                        members.some((job,index)=>{
                            const approved=plan[phase+'_members'][index];
                            return !job||!approved||
                                job.resource_bundle?.id!==bundleId||
                                job.resource_bundle.index!==index+1||
                                job.resource_bundle.size!==members.length||
                                job.configuration_id!==approved.configuration_id||
                                job.configuration_revision!==approved.expected_revision||
                                job.assigned_machine_id!==approved.machine_id||
                                JSON.stringify(job.resources)!==
                                    JSON.stringify(approved.resources)||
                                job.model_source_job_id!==plan.source_job_id||
                                job.expected_parent_model_sha256!==
                                    plan.source_model_sha256||
                                (plan.source_manifest_sha256!=null&&
                                    job.expected_parent_manifest_sha256!==plan.source_manifest_sha256);
                        }))
                        throw Error('Frozen native round resource bundle, machine or model ownership changed.');
                    // A known terminal failure cannot be repaired by waiting
                    // for an unrelated offline machine. Settle surviving
                    // native Stop owners and surface manual review now.
                    if(phase==='optimizer'&&members[0].native_release_pending&&
                        ['failed','waiting_adapter'].includes(members[0].status)&&
                        members.slice(1).every(job=>!['failed','unverified','stopped','cancelled'].includes(job.status))) {
                        const owner=members[0];
                        const reason=this.advanceNativeReleaseRecovery(plan,owner,members,snapshot);
                        if(reason&&plan.reason!==reason)update(plan.status,reason);
                        continue;
                    }
                    if(members.some(job=>['failed','unverified','stopped','cancelled']
                        .includes(job.status))) {
                        // A failed GPU optimizer rank cannot finish a
                        // collective: remaining ranks may otherwise wait
                        // indefinitely while retaining live leases. Use the
                        // SAME durable, machine-owned group Stop as physical
                        // collection. The failed owner remains held for
                        // review; only verified terminal siblings release.
                        this.stopResourceBundle({
                            bundle_id:bundleId,
                            expected_job_ids:members.map(job=>job.id),
                            confirm_stop:true,
                        });
                        throw Error(phase==='collectors'
                            ?'A native collector failed; sibling Stop and terminal lease reconciliation are required.'
                            :'A synchronized native optimizer rank failed; sibling Stops and checkpoint consensus require review.');
                    }
                    // Job status is not a substitute for the Hub's actual
                    // original resource claim. In particular, do not accept
                    // a reported finished GPU rank until that exact lease
                    // has a verified terminal release. Otherwise a later
                    // round could overlap an apparently free old owner.
                    if(members.some(job=>{
                        const lease=snapshot.reservations.find(item=>
                            item.id===job.lease_id&&item.job_id===job.id&&
                            item.machine_id===job.assigned_machine_id&&
                            item.project_id===job.project_id&&
                            item.issued_by==='experiment-hub');
                        if(!lease)return true;
                        return job.status==='finished'&&job.capacity_released_at
                            ?lease.status!=='released'||!lease.released_at
                            :!['offered','acknowledged'].includes(lease.status);
                    }))
                        throw Error('Native round member has no matching active or verified released Hub resource lease.');
                }
                const machines=Object.entries(plan.agent_instances);
                if(machines.some(([machineId,instance])=>{
                    const live=this.agentReports.get(machineId);
                    return live?.instance_id&&live.instance_id!==instance;
                }))
                    throw Error('An approved native training-round machine changed its authenticated agent instance; fresh operator authorization is required.');
                if(machines.some(([machineId])=>
                    snapshot.machines.find(machine=>machine.id===machineId)
                        ?.status!=='connected')) {
                    // Offline members keep their original leases and jobs.
                    // No replacement worker is silently approved.
                    const reason='Awaiting the exact originally approved authenticated native machine sessions.';
                    if(plan.reason!==reason)
                        update(plan.status,reason);
                    continue;
                }
                if(phase==='collectors'&&plan.collector_source_contracts) {
                    const expected=plan.collector_source_contracts[plan.completed_rounds];
                    if(!SHA256.test(String(expected||''))||
                        plan.collector_members.some(member=>nativeBeesStageSource(
                            snapshot.machines.find(machine=>machine.id===member.machine_id),
                            plan.completed_rounds+1,
                        )!==expected))
                        throw Error('Native curriculum stage differs from the originally approved physical source.');
                }
                if(plan.status.startsWith('pending_')) {
                    const result=this.queueResourceBundle({
                        confirm_queue:true,
                        members:phase==='collectors'
                            ?plan.collector_members:plan.optimizer_members,
                        model_source_job_id:plan.source_job_id,
                        ...(phase==='optimizer'?{
                            collector_job_ids:plan.collectors_job_ids,
                        }:{}),
                    },{id,status:plan.status,phase});
                    if(!result?.bundle_id)
                        throw Error('Native round resource reservation did not publish a Hub bundle.');
                    continue;
                }
                const launch=(this.state.bundle_start_plans||[])
                    .filter(item=>item.bundle_id===bundleId).at(-1);
                if(!launch||launch.status==='disarmed'&&
                    members.every(job=>job.status==='waiting_adapter'&&!job.dispatch_id)) {
                    // The original pending Start was disarmed on restart.
                    // Only a separately reauthorized round can reach this
                    // branch, and it reuses the same reserved member IDs.
                    this.scheduleResourceBundleStart({
                        bundle_id:bundleId,expected_job_ids:members.map(job=>job.id),
                        confirm_schedule:true,confirm_gpu_start:true,
                    });
                    continue;
                }
                if(['needs_review','expired','cancelled','disarmed'].includes(launch.status))
                    throw Error('Native group Start is no longer authorized: '+launch.reason);
                if(!members.every(job=>job.status==='finished'&&
                    job.capacity_released_at))continue;
                if(phase==='collectors') {
                    // The optimizer consumes exactly these authenticated
                    // native physical partitions; freeze their measured
                    // World counts before starting the next GPU group.
                    const receipts=members.map(job=>
                        verifiedNativeCollectorPin(this,job.id));
                    const partitions=receipts.map((receipt,index)=>({
                        job_id:members[index].id,
                        world_start:receipt.partition.world_start,
                        world_count:receipt.partition.world_count,
                        seed_offset:receipt.seed_offset,
                        ...(members[index].frozen_parameters.native_round_number
                            ?{native_round_number:
                                members[index].frozen_parameters.native_round_number}
                            :{}),
                        source_run_contract_sha256:receipt.source_contract_sha256,
                        ...(members[index].frozen_parameters.collection_id
                            ?{collection_id:members[index].frozen_parameters.collection_id}
                            :{}),
                    }));
                    const work=receipts.map((receipt,index)=>({
                        job_id:members[index].id,
                        ...receipt.work,
                    }));
                    const totals={
                        scope:'sum_of_independent_native_collectors',
                        collector_count:work.length,
                        successful_world_steps:work.reduce((sum,row)=>
                            sum+row.successful_world_steps,0),
                        successful_policy_rows:work.reduce((sum,row)=>
                            sum+row.successful_policy_rows,0),
                        sum_collector_wall_seconds:work.reduce((sum,row)=>
                            sum+row.collector_wall_seconds,0),
                        verified_gpu_model_forwards:work.reduce((sum,row)=>
                            sum+(row.verified_gpu_model_forwards||0),0),
                        verified_gpu_agent_rows:work.reduce((sum,row)=>
                            sum+(row.verified_gpu_agent_rows||0),0),
                    };
                    update('pending_optimizer',
                        'Fresh completed physical experience ready for synchronized MA-POCA.',
                        {collector_world_partitions:partitions,
                            collector_work:work,collector_work_totals:totals});
                }else {
                    const owner=members[0];
                    if(!checkpointContinuationComplete(owner)||
                        nativeOptimizerGroupBlock(this.state,owner))
                        continue;
                    // A verified rank-zero receipt does not mean its model
                    // package is already available for the next round or
                    // eventual continuation. Do not declare a final native
                    // round complete until the original authenticated Hub
                    // mirror holds the checkpoint, manifest and opponents.
                    try {
                        verifiedNativeModelMirror(this,owner);
                    }catch(error) {
                        if(error.status===409) {
                            const reason='Awaiting the completed synchronized PPO checkpoint, manifest and frozen-opponent mirror before accepting this native round.';
                            if(plan.reason!==reason)update(plan.status,reason);
                            continue;
                        }
                        throw error;
                    }
                    // One already-registered native evaluator owns the
                    // fixed-reference holdout. Unlike training SPS, its
                    // checkpoint-bound receipts may decide whether further
                    // physical experience is necessary. Never advance an
                    // approved round before the frozen suites have reported.
                    const {rules,missing,receipts,passed}=
                        nativeRoundFinalEvaluation(owner);
                    if(missing.length) {
                        if(!finalEvaluationPending(snapshot,owner,Date.now()))
                            throw Error('Native optimizer completed but its independent final evaluations did not arrive before the approved evaluation window closed.');
                        const reason='Awaiting independent final evaluation of native checkpoint '+
                            owner.checkpoint_model_sha256.slice(0,12)+' before the next collection.';
                        if(plan.reason!==reason)update(plan.status,reason);
                        continue;
                    }
                    const nextCount=plan.completed_rounds+1;
                    // Every GPU rank has finished and released its own Hub
                    // lease. Retain the actual completed collective's
                    // consensus separately from reserved CPU/GPU capacity;
                    // a checkpoint hash alone does not prove remote learning.
                    const optimizerConsensus=owner.optimizer_completion;
                    const optimizerGroup={
                        scope:'verified_native_synchronized_optimizer_group',
                        group_id:bundleId,
                        nodes:members.length,
                        input_sha256:optimizerConsensus.input_sha256,
                        model_consensus_sha256:
                            optimizerConsensus.model_consensus_sha256,
                        // Rank-zero's verified learner measurements have
                        // one elapsed-time owner. Other GPU ranks overlap
                        // and their work cannot be added as fleet wall time.
                        ...(optimizerConsensus.learner_work?{
                            learner_work:optimizerConsensus.learner_work,
                        }:{}),
                        ...(optimizerConsensus.historical_opponent_usage?{
                            historical_opponent_usage:copy(optimizerConsensus.historical_opponent_usage),
                        }:{}),
                        ranks:members.map((member,index)=>({
                            job_id:member.id,
                            machine_id:member.assigned_machine_id,
                            node_rank:index,
                            leased_gpu_count:member.resources.gpu_count,
                            leased_cpus:member.resources.cpus,
                            completed_and_released:true,
                        })),
                        total_leased_gpu_slots:members.reduce((sum,member)=>
                            sum+member.resources.gpu_count,0),
                        total_leased_cpus:members.reduce((sum,member)=>
                            sum+member.resources.cpus,0),
                    };
                    // The source and output checkpoints were verified by
                    // the exact collector/optimizer group owners above.
                    // Retain their identities before advancing the same
                    // bounded plan; otherwise the original round's model,
                    // experience and resource provenance disappears from
                    // the plan after the next reservation is issued.
                    const history=[...(plan.round_history||[]),{
                        round:nextCount,
                        source_job_id:plan.source_job_id,
                        source_checkpoint_sha256:plan.source_model_sha256,
                        ...(plan.source_manifest_sha256?{
                            source_manifest_sha256:plan.source_manifest_sha256,
                        }:{}),
                        ...(plan.source_collection_selection?{
                            source_collection_selection:copy(plan.source_collection_selection),
                        }:{}),
                        collector_job_ids:plan.collectors_job_ids.slice(),
                        ...(plan.collector_source_contracts?{
                            collector_source_contract_sha256:
                                plan.collector_source_contracts[plan.completed_rounds],
                        }:{}),
                        ...(plan.collector_world_partitions?{
                            collector_world_partitions:plan.collector_world_partitions,
                        }:{}),
                        ...(plan.collector_work?{
                            collector_work:plan.collector_work,
                            collector_work_totals:plan.collector_work_totals,
                        }:{}),
                        optimizer_job_ids:members.map(job=>job.id),
                        collector_bundle_id:plan.collectors_bundle_id,
                        optimizer_bundle_id:bundleId,
                        optimizer_group:optimizerGroup,
                        ...(optimizerConsensus.continual_release?{
                            continual_release:copy(optimizerConsensus.continual_release),
                        }:{}),
                        checkpoint_job_id:owner.id,
                        checkpoint_sha256:owner.checkpoint_model_sha256,
                        ...(receipts.length?{
                            evaluation_receipts:receipts.map(receipt=>({
                                suite_id:receipt.suite_id,
                                suite_sha256:receipt.suite_sha256,
                                checkpoint_sha256:receipt.checkpoint_sha256,
                                dispatch_id:receipt.dispatch_id,
                                successes:receipt.successes,
                                trials:receipt.trials,
                            })),
                            evaluation_stop_satisfied:passed,
                        }:{}),
                        ...(plan.collector_seed_offset===undefined?{}:{
                            collector_seed_offset:
                                plan.collector_seed_offset+plan.completed_rounds,
                        }),
                    }];
                    update(passed||nextCount===plan.round_count?'complete':'pending_collectors',
                        passed
                            ?'Independent fixed-reference evaluation target met; further authorized native training rounds are stopped.'
                            :nextCount===plan.round_count
                                ?'All approved native collection and optimizer rounds completed.'
                                :rules.length
                                    ?'Independent evaluation did not meet its target; collecting fresh native experience from the latest verified checkpoint.'
                                    :'Starting fresh physical collection against the newly verified checkpoint.',
                        {
                            completed_rounds:nextCount,
                            round_history:history,
                            source_job_id:owner.id,
                            source_model_sha256:owner.checkpoint_model_sha256,
                            source_manifest_sha256:null,source_collection_selection:null,
                            collectors_bundle_id:null,collectors_job_ids:null,
                            collector_world_partitions:null,
                            collector_work:null,collector_work_totals:null,
                            optimizer_bundle_id:null,optimizer_job_ids:null,
                        });
                }
            }catch(error) {
                const reason=String(error.message||error).slice(0,240);
                // Capacity and fresh GPU qualification are not source or
                // checkpoint failures. Leave the exact authorized stage
                // pending rather than fabricating a substitute machine.
                if(plan.status.startsWith('pending_')&&
                    /capacity|headroom|budget|available|insufficient|claimed|overcommit/i.test(reason))
                    continue;
                update('needs_review',reason);
            }
        }
    }

    // Restart an incomplete synchronized PPO collective as a NEW complete
    // Hub group. No old torchrun process, GPU lease, checkpoint, artifact
    // directory or optimizer state is reused. The source model and accepted
    // physical gameplay remain pinned to the exact prior inputs.
    restartNativeOptimizerBundle(value) {
        if(!object(value)||value.confirm_restart!==true||
            !UUID.test(String(value.bundle_id||''))||
            !Array.isArray(value.expected_job_ids)||
            value.expected_job_ids.length<2||value.expected_job_ids.length>8||
            value.expected_job_ids.some(id=>!UUID.test(String(id)))||
            Object.keys(value).some(key=>![
                'bundle_id','expected_job_ids','confirm_restart',
            ].includes(key)))
            throw Error('Confirm the exact failed synchronized native optimizer group.');
        const snapshot=this.snapshot();
        const previous=snapshot.jobs.filter(job=>
            job.resource_bundle?.id===value.bundle_id)
            .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
        if(previous.length!==value.expected_job_ids.length||
            previous.some((job,index)=>job.id!==value.expected_job_ids[index]||
                job.resource_bundle?.index!==index+1||
                job.resource_bundle?.size!==previous.length||
                job.project_id!==(index===0?'bees_native':'bees_native_optimizer_worker')||
                job.frozen_parameters?.optimizer_group!==true||
                !['finished','failed','stopped'].includes(job.status)||
                !job.capacity_released_at||!job.dispatch_id||
                !job.start_first_offered_at||!job.execution_updated_at||
                !snapshot.reservations.some(lease=>
                    lease.id===job.lease_id&&lease.job_id===job.id&&
                    lease.project_id===job.project_id&&
                    lease.machine_id===job.assigned_machine_id&&
                    lease.issued_by==='experiment-hub'&&
                    lease.status==='released'&&Boolean(lease.released_at)))||
            !previous.some(job=>['failed','stopped'].includes(job.status))||
            !nativeOptimizerGroupBlock(snapshot,previous[0]))
            throw Error('A full synchronized optimizer restart requires every original rank to terminate, release its lease, and leave an incomplete group.');
        if(previous[0].native_release_pending)
            throw Error('The original owner already committed its PPO checkpoint; recover the continual release instead of restarting optimization.');
        if(snapshot.jobs.some(job=>
            job.optimizer_restart_of_bundle_id===value.bundle_id&&
            job.status!=='cancelled'))
            throw Error('This failed optimizer group already has an unresolved restarted owner group.');
        const original=previous[0];
        const expectedPins=original.frozen_parameters.rollout_bundles;
        if(!Array.isArray(expectedPins)||!expectedPins.length||
            !SHA256.test(String(original.frozen_parameters.optimizer_inputs_sha256||'')))
            throw Error('Failed optimizer group lacks exact source gameplay and optimizer pins.');
        // Exact pinned collector job IDs were already frozen in each
        // optimizer input. Reverify their authenticated artifact mirror
        // directly; do not scan unrelated jobs or choose a "similar" model.
        const collectorIds=expectedPins.map(pin=>{
            if(!object(pin)||!UUID.test(String(pin.job_id||''))||
                !SHA256.test(String(pin.sha256||''))||
                JSON.stringify(verifiedNativeCollectorPin(this,pin.job_id).pin)!==
                    JSON.stringify(pin))
                throw Error('Original native optimizer collector input no longer matches its issued physical artifact.');
            return pin.job_id;
        });
        if(new Set(collectorIds).size!==collectorIds.length)
            throw Error('Original distributed optimizer duplicates an issued collector artifact.');
        const members=previous.map(job=>({
            configuration_id:job.configuration_id,
            expected_revision:job.configuration_revision,
            machine_id:job.assigned_machine_id,
            resources:copy(job.resources),
            ...(job.metric_selection?{metric_selection:copy(job.metric_selection)}:{}),
        }));
        return this.queueResourceBundle({
            confirm_queue:true,members,
            ...(original.model_source_job_id?
                {model_source_job_id:original.model_source_job_id}:{}),
            collector_job_ids:collectorIds,
            restart_optimizer_bundle_id:value.bundle_id,
            confirm_optimizer_restart:true,
        });
    }

    // Atomically reserve fixed resources for independently executable jobs
    // across projects and machines. This is the common *control-plane*
    // foundation for distributed workloads, not a data-plane protocol or a
    // simultaneous-Start guarantee. Every member retains its own native
    // owner, checkpoint, Stop policy, and explicit Start authorization.
    queueResourceBundle(value,roundPlan=null) {
        if(!object(value)||value.confirm_queue!==true||
            Object.keys(value).some(key=>
                !['confirm_queue','members','model_source_job_id',
                    'collector_job_ids','restart_optimizer_bundle_id',
                    'confirm_optimizer_restart'].includes(key))||
            (value.restart_optimizer_bundle_id===undefined
                ?value.confirm_optimizer_restart!==undefined
                :value.confirm_optimizer_restart!==true||
                    !UUID.test(String(value.restart_optimizer_bundle_id)))||
            (value.model_source_job_id!==undefined&&
                !UUID.test(String(value.model_source_job_id)))||
            (value.collector_job_ids!==undefined&&(
                !Array.isArray(value.collector_job_ids)||
                !value.collector_job_ids.length||
                value.collector_job_ids.length>256||
                value.collector_job_ids.some(id=>!UUID.test(String(id)))||
                new Set(value.collector_job_ids).size!==
                    value.collector_job_ids.length))||
            !Array.isArray(value.members)||value.members.length<2||
            value.members.length>16)
            throw Error('Confirm 2–16 machine-pinned job allocations in one resource bundle.');
        // Resolve all physical inputs before creating a single reservation.
        // The completed collector's authenticated mirror, not the UI or a
        // mutable file path, owns the receipt SHA and the source model proof.
        const collectors=value.collector_job_ids===undefined?null:
            value.collector_job_ids.map(id=>verifiedNativeCollectorPin(this,id));
        if(collectors) {
            const first=collectors[0],parts=new Map();
            for(const item of collectors) {
                if(item.model_sha256!==first.model_sha256||
                    item.manifest_sha256!==first.manifest_sha256||
                    item.source_contract_sha256!==first.source_contract_sha256||
                    item.policy_worker_sha256!==first.policy_worker_sha256||
                    item.learner_slot!==first.learner_slot||
                    item.partition.source_run_id!==first.partition.source_run_id)
                    throw Error('Native PPO collector sources disagree on checkpoint, manifest, policy worker or physical simulation.');
                const bucket=String(item.seed_offset);
                if(!parts.has(bucket))parts.set(bucket,[]);
                const end=item.partition.world_start+item.partition.world_count;
                if(!Number.isSafeInteger(end)||end<=item.partition.world_start)
                    throw Error('Native collector World partition exceeds its supported range.');
                parts.get(bucket).push([item.partition.world_start,end]);
            }
            for(const intervals of parts.values()) {
                intervals.sort((a,b)=>a[0]-b[0]);
                if(intervals.some((range,index)=>index>0&&
                    range[0]<intervals[index-1][1]))
                    throw Error('Native PPO collectors overlap physical Worlds within a seed cohort.');
            }
            if(value.model_source_job_id!==undefined&&
                first.model_sha256!==this.state.jobs.find(job=>
                    job.id===value.model_source_job_id)?.checkpoint_model_sha256)
                throw Error('Fresh native rollout collectors did not use the selected predecessor checkpoint.');
        }
        const machines=this.snapshot().machines;
        const next=copy(this.state);
        const restartGroup=value.restart_optimizer_bundle_id===undefined?null:
            next.jobs.filter(job=>
                job.resource_bundle?.id===value.restart_optimizer_bundle_id)
                .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
        if(restartGroup&&(
            restartGroup.length!==value.members.length||
            restartGroup.some((old,index)=>old.resource_bundle?.index!==index+1||
                old.resource_bundle.size!==restartGroup.length||
                old.project_id!==(index===0?'bees_native':'bees_native_optimizer_worker')||
                old.frozen_parameters?.optimizer_group!==true||
                !['finished','failed','stopped'].includes(old.status)||
                !old.capacity_released_at||!old.dispatch_id||
                !old.start_first_offered_at||!old.execution_updated_at||
                !next.reservations.some(lease=>
                    lease.id===old.lease_id&&lease.job_id===old.id&&
                    lease.project_id===old.project_id&&
                    lease.machine_id===old.assigned_machine_id&&
                    lease.issued_by==='experiment-hub'&&
                    lease.status==='released'&&Boolean(lease.released_at)))||
            !restartGroup.some(old=>['failed','stopped'].includes(old.status))||
            Boolean(restartGroup[0]?.native_release_pending)||
            !nativeOptimizerGroupBlock(next,restartGroup[0])||
            next.jobs.some(job=>
                job.optimizer_restart_of_bundle_id===value.restart_optimizer_bundle_id&&
                job.status!=='cancelled')))
            throw Error('Original synchronized optimizer group is not terminal, released and available for one replacement.');
        const approvedRound=roundPlan
            ?(next.native_round_plans||[]).find(item=>
                item.id===roundPlan.id&&item.status===roundPlan.status)
            :null;
        if(roundPlan&&!approvedRound)
            throw Error('Approved native round changed before issuing a resource bundle.');
        // A native collector can expand at most 1,024 authentic source
        // Worlds. Reserve a separate global index domain for each machine,
        // without imposing an invented world count on its installed Level
        // recipe. The Hub freezes the start and collection identity; the
        // native collector reports the exact number of physical Worlds.
        const nativeCollectionId=approvedRound&&roundPlan.phase==='collectors'
            ?crypto.createHash('sha256').update(JSON.stringify([
                approvedRound.id,approvedRound.completed_rounds+1,
                approvedRound.source_job_id,approvedRound.source_model_sha256,
            ])).digest('hex')
            :null;
        const modelPin=value.model_source_job_id===undefined?null:
            verifiedNativeModelPin(this,next.jobs.find(
                job=>job.id===value.model_source_job_id));
        if(modelPin&&collectors&&collectors.some(item=>
            item.model_sha256!==modelPin.expected_parent_model_sha256||
            item.manifest_sha256!==modelPin.expected_parent_manifest_sha256))
            throw Error('Native optimizer inputs differ from the pinned collection manifest.');
        if(approvedRound?.source_manifest_sha256&&
            modelPin?.expected_parent_manifest_sha256!==approvedRound.source_manifest_sha256)
            throw Error('Native round source manifest changed after physical collection.');
        const bundleId=crypto.randomUUID(),jobs=[],leases=[];
        for(const [index,selection] of value.members.entries()) {
            if(!object(selection)||
                Object.keys(selection).some(key=>![
                    'configuration_id','expected_revision','machine_id',
                    'resources','metric_selection',
                ].includes(key))||
                !ID.test(String(selection.machine_id||'')))
                throw Error('Each bundle member requires a saved configuration, machine ID and fixed resource budget.');
            const machine=machines.find(host=>host.id===selection.machine_id);
            if(!machine||machine.status!=='connected'||
                machine.local_executor_enabled!==true||
                machine.native_preflight_passed!==true||
                !['win32','linux'].includes(machine.agent_platform)||
                (machine.agent_platform==='linux'&&
                    (machine.execution_capabilities?.cgroup_v2_detected!==true||
                     !['cpu','memory'].every(controller=>
                         machine.execution_capabilities?.cgroup_controllers?.includes(controller)))))
                throw Error('Bundle member '+(index+1)+' requires a connected, qualified native machine.');
            const modelSourceId=value.model_source_job_id;
            const job=this.queueIn(next,{
                ...selection,
                ...(modelSourceId?{
                    model_source_job_id:modelSourceId,
                    ...(index===0&&next.configurations.find(config=>
                        config.id===selection.configuration_id)?.project_id==='bees_native'
                        ?{continuation_of_job_id:modelSourceId}:{}),
                }:{}),
            },{modelPin,optimizerRestartFrom:restartGroup?.[index]??null});
            if(nativeCollectionId!==null) {
                // The native run configuration, not the Hub, knows how
                // many authored Levels must be preserved and how much
                // per-machine rollout concurrency was approved. Each
                // machine gets a distinct 1,024-world address range and
                // retains its local CPU/GPU/RAM lease unchanged.
                job.frozen_parameters.collection_id=nativeCollectionId;
                job.frozen_parameters.rollout_world_start=index*1024;
                // Select only the pre-pinned environment stage installed
                // on each original collector machine. The same MA-POCA
                // checkpoint and authorized GPU/CPU resources persist.
                job.frozen_parameters.native_round_number=
                    approvedRound.completed_rounds+1;
                // Carry the approved physical source directly into every
                // immutable job. Its installed adapter checks this SHA
                // before running; the Hub receipt mirror checks it again
                // before admitting collected experience to the optimizer.
                const pinnedSource=approvedRound.collector_source_contracts?.[
                    approvedRound.completed_rounds];
                if(pinnedSource!==undefined) {
                    if(!SHA256.test(String(pinnedSource)))
                        throw Error('Approved native curriculum stage has no valid source identity.');
                    job.frozen_parameters.source_run_contract_sha256=pinnedSource;
                }
                const originalSeed=approvedRound.collector_seed_offsets?.[index] ??
                    approvedRound.collector_seed_offset;
                if(originalSeed!==undefined)
                    job.frozen_parameters.seed_offset=
                        originalSeed+approvedRound.completed_rounds;
                job.configuration_sha256=crypto.createHash('sha256')
                    .update(JSON.stringify(job.frozen_parameters)).digest('hex');
            }
            if(collectors) {
                if(!['bees_native','bees_native_optimizer_worker']
                    .includes(job.project_id)||
                    job.frozen_parameters.optimizer_group!==true||
                    !Array.isArray(job.frozen_parameters.rollout_bundles)||
                    job.frozen_parameters.rollout_bundles.length!==0)
                    throw Error('Verified native collector IDs require an empty rollout-bundle optimizer template.');
                job.frozen_parameters.rollout_bundles=collectors.map(item=>item.pin);
                job.configuration_sha256=crypto.createHash('sha256')
                    .update(JSON.stringify(job.frozen_parameters)).digest('hex');
            }
            if(job.project_id==='tinybrains_rollout'||job.project_id==='bees'||
                job.project_id==='ants'||
                (job.project_id==='tinybrains'&&job.frozen_parameters?.remote_rollouts===true)||
                job.status!=='waiting_adapter'||
                (job.continuation_of_job_id&&
                    !(value.model_source_job_id&&index===0&&
                      job.continuation_of_job_id===value.model_source_job_id))||
                job.after_job_id||
                !machine.installed_projects?.includes(job.project_id)||
                !supportsEvaluation(machine.evaluation_suites,job)||
                !supportsTinyBrainsProfile(machine,job))
                throw Error('Bundle member '+(index+1)+' requires an approved independent project adapter and configuration.');
            if(job.resources.ram_mb<128||
                (job.resources.gpu_count>0&&
                    (!machine.gpu_projects?.includes(job.project_id)||
                     (job.resources.gpu_mode==='shared'&&
                         !machine.gpu_shared_projects?.includes(job.project_id))||
                     (job.resources.gpu_count===2&&
                         !dualGpuApproved(job,machine))||
                     job.resources.gpu_count>2)))
                throw Error('Bundle member '+(index+1)+' is not eligible for its requested native CPU/GPU mode.');
            // Use the actual lease allocator, not a separate capacity
            // estimate. Earlier members consume budget in the same draft;
            // any failure abandons the draft without queued jobs or leases.
            const lease=this.claimJobIn(next,job,machine.id,machine);
            job.resource_bundle={id:bundleId,index:index+1,size:value.members.length};
            if(restartGroup) {
                const predecessor=restartGroup[index];
                if(!predecessor||job.project_id!==predecessor.project_id||
                    job.assigned_machine_id!==predecessor.assigned_machine_id||
                    job.configuration_id!==predecessor.configuration_id||
                    job.configuration_revision!==predecessor.configuration_revision||
                    JSON.stringify(job.resources)!==JSON.stringify(predecessor.resources)||
                    JSON.stringify(job.frozen_parameters)!==
                        JSON.stringify(predecessor.frozen_parameters)||
                    job.configuration_sha256!==predecessor.configuration_sha256||
                    JSON.stringify(lease.gpu_bindings||[])!==JSON.stringify(
                        next.reservations.find(original=>
                            original.id===predecessor.lease_id&&
                            original.job_id===predecessor.id)?.gpu_bindings||[])||
                    (job.model_source_job_id||null)!==
                        (predecessor.model_source_job_id||null)||
                    (job.expected_parent_model_sha256||null)!==
                        (predecessor.expected_parent_model_sha256||null)||
                    (predecessor.expected_parent_manifest_sha256!==undefined&&
                        job.expected_parent_manifest_sha256!==
                            predecessor.expected_parent_manifest_sha256))
                    throw Error('Native restart changed the pinned optimizer source, machine, GPU budget or rank.');
                job.optimizer_restart_of_bundle_id=value.restart_optimizer_bundle_id;
                job.optimizer_restart_of_job_id=predecessor.id;
            }
            jobs.push(job);
            leases.push(lease);
        }
        // Synchronized Bees optimizer nodes are one distributed MA-POCA
        // update, not unrelated native jobs sharing a reservation. Validate
        // the complete immutable group before committing any of its leases.
        // The first member publishes the checkpoint; all remaining members
        // are worker-only and can never claim independent model ownership.
        if(jobs.some(job=>job.project_id==='bees_native_optimizer_worker'||
                job.frozen_parameters?.optimizer_group===true)) {
            const source=jobs[0]?.frozen_parameters;
            const pins=JSON.stringify(source?.rollout_bundles??null);
            const inputIdentity=source?.optimizer_inputs_sha256;
            if(jobs.length>8||jobs[0].project_id!=='bees_native'||
                source?.optimizer_group!==true||
                (value.model_source_job_id!==undefined&&
                    jobs[0].assigned_machine_id!==next.jobs.find(item=>
                        item.id===value.model_source_job_id)?.assigned_machine_id)||
                !SHA256.test(String(inputIdentity||''))||
                !Array.isArray(source.rollout_bundles)||
                source.rollout_bundles.length<1||
                new Set(jobs.map(job=>job.assigned_machine_id)).size!==jobs.length||
                jobs.some((job,index)=>
                    job.project_id!==(index===0?'bees_native':'bees_native_optimizer_worker')||
                    job.frozen_parameters?.optimizer_group!==true||
                    (job.frozen_parameters.continual_release!==undefined&&
                        (index!==0||job.frozen_parameters.continual_release!==true))||
                    job.frozen_parameters.optimizer_inputs_sha256!==inputIdentity||
                    JSON.stringify(job.frozen_parameters.rollout_bundles??null)!==pins||
                    job.frozen_parameters.seed_offset!==0||
                    (job.frozen_parameters.refresh_opponent_slot??null)!==
                        (source.refresh_opponent_slot??null)||
                    ![1,2].includes(job.resources.gpu_count)||
                    job.resources.gpu_mode!=='exclusive'||
                    job.resources.cpus<job.resources.gpu_count||
                    job.resources.gpu_count!==jobs[0].resources.gpu_count))
                throw Error('Native MA-POCA optimizer bundle requires one checkpoint owner and 1–7 exclusive GPU worker machines with identical verified model, trainer, backend and pinned rollout inputs.');
        }
        if(value.model_source_job_id!==undefined) {
            const optimizer=jobs[0]?.project_id==='bees_native'&&
                jobs[0].frozen_parameters?.optimizer_group===true;
            const collectorsOnly=jobs.every(job=>
                job.project_id==='bees_native_collect'&&
                job.model_source_job_id===value.model_source_job_id&&
                job.frozen_parameters?.optimizer_group!==true&&
                job.frozen_parameters?.seed_offset>=0);
            if(!optimizer&&!collectorsOnly)
                throw Error('Native checkpoint group must contain either a synchronized optimizer or physical rollout collectors pinned to the same predecessor.');
            if(collectorsOnly&&new Set(jobs.map(job=>job.assigned_machine_id)).size!==jobs.length)
                throw Error('Distributed native collectors require distinct leased machines.');
        }
        if(collectors&&!(jobs[0]?.project_id==='bees_native'&&
            jobs[0].frozen_parameters?.optimizer_group===true))
            throw Error('Physical native collector pins require an authorized synchronized Bees optimizer group.');
        if(restartGroup) {
            const oldId=value.restart_optimizer_bundle_id;
            const plan=(next.native_round_plans||[]).find(item=>
                item.optimizer_bundle_id===oldId);
            if(plan) {
                if(!['awaiting_optimizer','needs_review'].includes(plan.status)||
                    !Array.isArray(plan.optimizer_job_ids)||
                    JSON.stringify(plan.optimizer_job_ids)!==
                        JSON.stringify(restartGroup.map(job=>job.id))||
                    plan.status==='needs_review'&&
                        plan.restart_phase!=null&&
                        plan.restart_phase!=='awaiting_optimizer')
                    throw Error('Original native round must be reviewed before replacing its failed GPU owner group.');
                plan.optimizer_bundle_id=bundleId;
                plan.optimizer_job_ids=jobs.map(job=>job.id);
                // Reusing the existing native round reauthorization path
                // preserves operator approval and prevents an unsolicited
                // automatic Start from the next coordinator heartbeat.
                plan.status='needs_review';
                plan.restart_phase='awaiting_optimizer';
                plan.reason='Failed synchronized PPO group replaced with new machine-owned GPU claims; reauthorize the native round before any Start.';
                plan.updated_at=now();
            }
        }
        if(roundPlan) {
            const plan=(next.native_round_plans||[]).find(item=>
                item.id===roundPlan.id&&item.status===roundPlan.status);
            if(!plan||!['collectors','optimizer'].includes(roundPlan.phase)||
                roundPlan.status!=='pending_'+roundPlan.phase)
                throw Error('Native training round changed before atomically reserving its next stage.');
            plan.source_manifest_sha256=modelPin.expected_parent_manifest_sha256;
            plan.source_collection_selection=modelPin.parent_collection_selection??null;
            plan[roundPlan.phase+'_bundle_id']=bundleId;
            plan[roundPlan.phase+'_job_ids']=jobs.map(job=>job.id);
            plan.status='awaiting_'+roundPlan.phase;
            plan.updated_at=now();
        }
        this.commit(next);
        return {bundle_id:bundleId,members:jobs.map(copy),
            leases:leases.map(lease=>({...copy(lease),receipt_fresh:false})),
            starts_authorized:0,
            note:collectors
                ?'Every rank has the same completed, authenticated physical collector pins and a durable resource claim. No Start has been authorized; the existing Hub GPU group scheduler must approve and qualify all machines.'
                :'All members have durable resource claims. No Starts are authorized; the project adapter determines whether they form independent jobs or one synchronized learner.'};
    }
    // Grant independent machine-local native Starts for a reserved bundle
    // in one registry commit. Each Start is independently OS-enforced by its
    // machine; this grants no simultaneous-run or data-plane guarantee.
    authorizeResourceBundle(value) {
        if(!object(value)||Object.keys(value).some(key=>![
            'bundle_id','expected_job_ids','confirm_start','release_on_terminal',
        ].includes(key))||
            (value.release_on_terminal!==undefined&&
             typeof value.release_on_terminal!=='boolean'))
            throw Error('Select an exact bundle and its terminal-release policy.');
        const snapshot=this.snapshot();
        const members=exactResourceBundle(snapshot,value,'confirm_start');
        for(const job of members) {
            const readiness=nativeStartReadiness(snapshot,job);
            if(!readiness.eligible)
                throw Error('Bundle member '+job.resource_bundle.index+
                    ' / '+job.project_id+' cannot Start: '+readiness.reason);
            if(job.distributed_rollout_managed===true||
                job.project_id==='tinybrains_rollout')
                throw Error('Distributed rollout groups use their own verified learner barrier.');
        }
        const pending=(this.state.bundle_start_plans||[]).find(plan=>
            plan.bundle_id===value.bundle_id&&
            ['pending','needs_review'].includes(plan.status));
        if(pending?.status==='needs_review')
            throw Error('Cancel the review-required bundle launch plan before manually starting its members.');
        if(pending&&value.release_on_terminal!==true)
            throw Error('This preapproved bundle has frozen terminal release enabled; cancel the pending plan before changing the release policy.');
        const next=copy(this.state),startedAt=now(),dispatches=[];
        for(const job of members) {
            const selected=next.jobs.find(item=>item.id===job.id);
            selected.status='start_requested';
            selected.dispatch_id=crypto.randomUUID();
            selected.start_requested_at=startedAt;
            selected.start_first_offered_at=null;
            if(value.release_on_terminal===true)
                selected.release_on_terminal=true;
            selected.status_reason='Bundle Start approved; awaiting each machine-local native owner and resource enforcement.';
            dispatches.push({job_id:selected.id,dispatch_id:selected.dispatch_id,
                machine_id:selected.assigned_machine_id});
        }
        if(pending) {
            const resolved=next.bundle_start_plans.find(plan=>plan.id===pending.id);
            resolved.status='authorized';
            resolved.reason='All native member Starts authorized in one durable transaction.';
            resolved.resolved_at=startedAt;
        }
        this.commit(next);
        return {bundle_id:value.bundle_id,starts_authorized:dispatches.length,
            dispatches,simultaneous_start_guaranteed:false,
            release_on_terminal:value.release_on_terminal===true};
    }
    // The operator preapproves an exact frozen group's future native Starts,
    // not an open-ended machine scheduler. The group may wait for lease/GPU
    // readiness with no trainer running. CPU-only plans can explicitly opt
    // into fresh-reconciled coordinator restart; GPU approval cannot.
    scheduleResourceBundleStart(value) {
        if(!object(value)||Object.keys(value).some(key=>![
            'bundle_id','expected_job_ids','confirm_schedule',
            'confirm_gpu_start','resume_after_restart','confirm_restart_rearm',
        ].includes(key)))
            throw Error('Choose an exact group and its unattended Start policy.');
        const members=exactResourceBundle(this.state,value,'confirm_schedule');
        if(members.some(job=>job.status!=='waiting_adapter'||job.dispatch_id||
            !job.lease_id||!job.assigned_machine_id))
            throw Error('Only entirely unstarted, reserved resource bundles can schedule a future Start.');
        const gpu=members.some(job=>job.resources?.gpu_count>0);
        if(gpu&&value.confirm_gpu_start!==true)
            throw Error('Explicit GPU Start confirmation is required for this group.');
        if(value.resume_after_restart===true&&
            (gpu||value.confirm_restart_rearm!==true))
            throw Error('Unattended coordinator restart needs separate approval and CPU-only members.');
        if(value.confirm_restart_rearm===true&&value.resume_after_restart!==true)
            throw Error('Restart approval must accompany an explicit CPU-only resume choice.');
        const prior=(this.state.bundle_start_plans||[]).find(plan=>
            plan.bundle_id===value.bundle_id&&
            ['pending','needs_review'].includes(plan.status));
        if(prior)throw Error('Cancel the previous pending or review-required bundle launch first.');
        const snapshot=this.snapshot(),agentInstances={};
        const frozenMembers=members.map(job=>{
            const lease=this.state.reservations.find(lease=>
                lease.id===job.lease_id&&lease.job_id===job.id&&
                lease.machine_id===job.assigned_machine_id&&
                lease.project_id===job.project_id&&
                lease.issued_by==='experiment-hub'&&
                ['offered','acknowledged'].includes(lease.status));
            if(!lease)throw Error('Every scheduled member requires its unchanged Hub-owned resource claim.');
            if(gpu) {
                const host=snapshot.machines.find(machine=>
                    machine.id===job.assigned_machine_id);
                const instance=this.agentReports.get(job.assigned_machine_id)?.instance_id;
                if(host?.status!=='connected'||!UUID.test(String(instance||'')))
                    throw Error('GPU group preapproval requires each machine to have a current authenticated agent session.');
                agentInstances[job.assigned_machine_id]=instance;
            }
            return {job_id:job.id,machine_id:job.assigned_machine_id,
                lease_id:lease.id};
        });
        const next=copy(this.state),plans=next.bundle_start_plans||[];
        // Prune completed historical authorizations only; an unresolved
        // plan must never be erased to make room for a newer approval.
        if(plans.length>=64) {
            const old=plans.findIndex(p=>
                ['authorized','cancelled','disarmed','expired'].includes(p.status));
            if(old<0)throw Error('Resolve old pending bundle launch plans before scheduling another.');
            plans.splice(old,1);
        }
        const current=Date.now();
        const plan={id:crypto.randomUUID(),bundle_id:value.bundle_id,
            member_job_ids:members.map(job=>job.id),
            frozen_members:frozenMembers,
            agent_instances:agentInstances,
            gpu_approval:gpu,
            resume_after_restart:value.resume_after_restart===true,
            release_on_terminal:true,
            status:'pending',reason:'Waiting for every native member and lease to become eligible.',
            approved_at:new Date(current).toISOString(),
            expires_at:new Date(current+(gpu?20*60*1000:24*60*60*1000)).toISOString()};
        plans.push(plan);
        next.bundle_start_plans=plans;
        this.commit(next);
        // Authorize immediately if all workers were already ready. Otherwise
        // the next authenticated heartbeat can complete this exact plan.
        this.advancePendingResourceBundles();
        return copy(this.state.bundle_start_plans.find(item=>item.id===plan.id));
    }
    cancelScheduledResourceBundleStart(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.plan_id||''))||
            Object.keys(value).some(key=>!['plan_id','confirm_cancel'].includes(key)))
            throw Error('Explicitly confirm which unattended bundle launch to cancel.');
        const next=copy(this.state);
        const plan=(next.bundle_start_plans||[]).find(item=>item.id===value.plan_id);
        if(!plan||!['pending','needs_review'].includes(plan.status))
            throw Error('Only a pending or review-required bundle launch can be cancelled.');
        plan.status='cancelled';
        plan.reason='Future native group Start cancelled by operator; existing jobs and claims are unchanged.';
        plan.resolved_at=now();
        this.commit(next);
        return {plan_id:plan.id,bundle_id:plan.bundle_id,status:plan.status,
            native_starts_withdrawn:0};
    }
    // A restart or missed heartbeat cannot authorize a duplicate Start.
    // All jobs and the plan's terminal decision commit in the same write.
    // Fresh claims and machine-local native proof remain the sole authority.
    advancePendingResourceBundles() {
        const ids=(this.state.bundle_start_plans||[])
            .filter(plan=>plan.status==='pending').map(plan=>plan.id);
        for(const id of ids) {
            const plan=this.state.bundle_start_plans.find(item=>item.id===id);
            if(!plan||plan.status!=='pending')continue;
            const halt=(status,reason)=>{
                const next=copy(this.state),item=next.bundle_start_plans.find(p=>p.id===id);
                if(item?.status!=='pending')return;
                item.status=status;
                item.reason=reason;
                item.resolved_at=now();
                this.commit(next);
            };
            if(Date.now()>=Date.parse(plan.expires_at)) {
                halt('expired','The frozen launch window elapsed before all native members were ready.');
                continue;
            }
            const snapshot=this.snapshot();
            const members=snapshot.jobs.filter(job=>job.resource_bundle?.id===plan.bundle_id)
                .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
            if(members.length!==plan.frozen_members.length||
                members.some((job,i)=>job.id!==plan.frozen_members[i].job_id||
                    job.assigned_machine_id!==plan.frozen_members[i].machine_id||
                    job.lease_id!==plan.frozen_members[i].lease_id||
                    job.status!=='waiting_adapter'||job.dispatch_id)) {
                halt('needs_review','A bundle member, dispatch or reserved machine changed; no unattended Start was issued.');
                continue;
            }
            if(members.some(job=>{
                const expected=plan.frozen_members.find(item=>item.job_id===job.id);
                return !snapshot.reservations.some(lease=>
                    lease.id===expected.lease_id&&
                    lease.job_id===job.id&&
                    lease.project_id===job.project_id&&
                    lease.machine_id===expected.machine_id&&
                    lease.issued_by==='experiment-hub'&&
                    ['offered','acknowledged'].includes(lease.status));
            })) {
                halt('needs_review','Frozen native resource ownership changed; no unattended Start was issued.');
                continue;
            }
            if(plan.gpu_approval&&members.some(job=>
                this.agentReports.get(job.assigned_machine_id)?.instance_id!==
                    plan.agent_instances[job.assigned_machine_id])) {
                halt('disarmed','An authenticated GPU machine agent was replaced; fresh operator approval is required.');
                continue;
            }
            if(!members.every(job=>nativeStartReadiness(snapshot,job).eligible))
                continue;
            try {
                this.authorizeResourceBundle({
                    bundle_id:plan.bundle_id,expected_job_ids:plan.member_job_ids,
                    confirm_start:true,release_on_terminal:true,
                });
            }catch(error) {
                halt('needs_review','Native bundle admission changed: '+
                    String(error.message||error).slice(0,170));
            }
        }
    }
    // Stop a mixed-project bundle without releasing any unknown or active
    // native owner. The native Stop intent and terminal-release policy both
    // survive a coordinator restart. A failed/unverified member remains
    // visible for inspection and cannot silently free capacity.
    stopResourceBundleIn(next,value,at) {
        if(!object(value)||Object.keys(value).some(key=>![
            'bundle_id','expected_job_ids','confirm_stop',
        ].includes(key)))
            throw Error('Select the exact resource bundle to stop.');
        const members=exactResourceBundle(next,value,'confirm_stop');
        let changed=false;
        const planned=(next.bundle_start_plans||[]).find(plan=>
            plan.bundle_id===value.bundle_id&&
            ['pending','needs_review'].includes(plan.status));
        if(planned) {
            planned.status='disarmed';
            planned.reason='Operator requested group shutdown before auto-start.';
            planned.resolved_at=at;
            changed=true;
        }
        for(const member of members) {
            const job=next.jobs.find(item=>item.id===member.id);
            if(job.status==='cancelled'||job.capacity_released_at)continue;
            if(job.status==='waiting_adapter'&&!job.dispatch_id||
                job.status==='start_requested'&&job.start_first_offered_at===null) {
                if(job.status==='start_requested') {
                    // Coordinator never offered this Start to a worker.
                    // Withdraw it in the same draft, then release the claim.
                    job.status='waiting_adapter';
                    delete job.dispatch_id;
                    delete job.start_requested_at;
                    delete job.start_first_offered_at;
                    delete job.automation_start_session_id;
                }
                this.cancelUnstartedJob(next,job,at);
                changed=true;
                continue;
            }
            const lease=next.reservations.find(item=>
                item.id===job.lease_id&&item.job_id===job.id&&
                item.project_id===job.project_id&&
                item.machine_id===job.assigned_machine_id&&
                item.issued_by==='experiment-hub'&&
                item.status==='acknowledged');
            if(['start_requested','launch_requested','running',
                'stop_requested','unverified'].includes(job.status)) {
                if(!job.dispatch_id||!lease)
                    throw Error('Bundle member '+job.id+
                        ' has uncertain native ownership or resource claim; no Stops committed.');
                if(job.status!=='stop_requested') {
                    job.status='stop_requested';
                    job.stop_requested_at=at;
                    job.status_reason='Bundle Stop requested; waiting for native supervisor verification.';
                    changed=true;
                }
                if(job.release_on_terminal!==true) {
                    job.release_on_terminal=true;
                    changed=true;
                }
                continue;
            }
            if(['finished','stopped'].includes(job.status)) {
                if(!job.dispatch_id||!job.execution_updated_at||!lease)
                    throw Error('Bundle member '+job.id+
                        ' has no verified terminal native ownership; no leases released.');
                if(job.status==='finished'&&!nativeOptimizerTerminalProof(job)) {
                    // Rank zero can be terminal before its resumable model
                    // is verified. Keep the original lease and its native
                    // Inspect owner until the exact checkpoint is proven.
                    if(job.release_on_terminal!==true) {
                        job.release_on_terminal=true;
                        changed=true;
                    }
                    continue;
                }
                this.releaseIn(next,lease);
                changed=true;
                continue;
            }
            // Failed native services retain their claims until an operator
            // has reviewed the failure; they cannot be inferred absent.
            if(job.status!=='failed')
                throw Error('Bundle member '+job.id+' needs manual lifecycle review.');
        }
        return changed;
    }
    stopResourceBundle(value) {
        const next=copy(this.state),changed=this.stopResourceBundleIn(next,value,now());
        if(changed)this.commit(next);
        const current=this.state.jobs.filter(job=>job.resource_bundle?.id===value.bundle_id)
            .sort((a,b)=>a.resource_bundle.index-b.resource_bundle.index);
        return {bundle_id:value.bundle_id,
            stops_requested:current.filter(j=>j.status==='stop_requested').length,
            cancelled:current.filter(j=>j.status==='cancelled').length,
            released:current.filter(j=>Boolean(j.capacity_released_at)).length,
            needs_review:current.filter(j=>['failed','unverified'].includes(j.status)).map(j=>j.id),
            members:current.map(j=>({job_id:j.id,status:j.status,
                capacity_released:Boolean(j.capacity_released_at)})),
            note:'Stops are durable native requests. No active or uncertain service capacity is released without a native terminal receipt.'};
    }
    // Queue and reserve an entire TinyBrains rollout group as one durable
    // transaction. The operator supplies resource budgets; physical worker
    // endpoints and identities come only from authenticated machine agents.
    // An incomplete set never leaves behind a stranded learner or sidecar.
    queueDistributedTinyBrains(value) {
        if(!object(value)||value.confirm_queue!==true||
            Object.keys(value).some(key=>!['confirm_queue','learner','collectors'].includes(key))||
            !object(value.learner)||
            Object.keys(value.learner).some(key=>
                !['configuration_id','expected_revision','machine_id','resources',
                    'continuation_of_job_id','metric_selection'].includes(key))||
            !ID.test(String(value.learner.machine_id||''))||
            !Array.isArray(value.collectors))
            throw Error('Confirm the saved learner, its machine, and one resource budget for each approved remote collector.');
        const snapshot=this.snapshot();
        const learnerMachine=snapshot.machines.find(m=>m.id===value.learner.machine_id);
        const config=snapshot.configurations.find(c=>c.id===value.learner.configuration_id&&
            c.revision===value.learner.expected_revision&&c.project_id==='tinybrains');
        const endpoints=learnerMachine?.tinybrains_remote_rollout_endpoints;
        const ancestorId=value.learner.continuation_of_job_id??null;
        const ancestor=ancestorId===null?null:snapshot.jobs.find(j=>j.id===ancestorId);
        if(ancestorId!==null&&(!UUID.test(String(ancestorId))||
            !ancestor||ancestor.project_id!=='tinybrains'||
            !checkpointContinuationComplete(ancestor)||
            !ancestor.capacity_released_at||
            ancestor.assigned_machine_id!==value.learner.machine_id))
            throw Error('Distributed continuation requires a verified resumable learner checkpoint, released capacity and the same learner machine.');
        if(!config||config.parameters?.remote_rollouts!==true||
            config.parameters?.device==='cpu'||
            !learnerMachine||learnerMachine.status!=='connected'||
            learnerMachine.agent_platform!=='linux'||
            learnerMachine.local_executor_enabled!==true||
            learnerMachine.native_preflight_passed!==true||
            !learnerMachine.installed_projects?.includes('tinybrains')||
            !learnerMachine.gpu_projects?.includes('tinybrains')||
            !supportsTinyBrainsProfile(learnerMachine,{
                project_id:'tinybrains',frozen_parameters:config.parameters,
                resources:resourceRequest(value.learner.resources),
                continuation_of_job_id:ancestorId,
            })||
            !Array.isArray(endpoints)||!endpoints.length||endpoints.length>8||
            value.collectors.length!==endpoints.length||
            !SHA256.test(String(learnerMachine.tinybrains_remote_key_fingerprint||'')))
            throw Error('The selected Linux GPU learner must approve this profile and exactly the supplied remote collector count.');
        const rolloutProject=snapshot.projects.find(p=>p.id==='tinybrains_rollout');
        if(rolloutProject?.adapter!=='unconfigured')
            throw Error('Register the TinyBrains rollout project before creating a distributed group.');
        const next=copy(this.state),machines=snapshot.machines;
        const learner=this.queueIn(next,value.learner);
        if(learner.status!=='waiting_adapter')
            throw Error('Distributed group claims require an immediately eligible, released predecessor.');
        // Only groups queued as one transaction opt into automatic sidecar
        // teardown after the learner has a verified terminal owner report.
        learner.distributed_rollout_managed=true;
        const learnerLease=this.claimJobIn(next,learner,learnerMachine.id,learnerMachine);
        const workers=[],leases=[learnerLease],seen=new Set([learnerMachine.id]);
        for(const [index,endpoint] of endpoints.entries()) {
            const selection=value.collectors[index];
            if(!object(selection)||Object.keys(selection).some(key=>
                !['resources','threads'].includes(key)))
                throw Error('Each distributed collector supplies only a CPU/RAM and optional exclusive GPU resource budget.');
            const budget=resourceRequest(selection.resources);
            if(budget.gpu_count>1||
                (budget.gpu_count===1&&budget.gpu_mode!=='exclusive'))
                throw Error('A remote collector may use CPU only or one exclusively leased CUDA GPU.');
            const eligible=machines.filter(host=>
                host.status==='connected'&&host.tinybrains_rollout_endpoint===endpoint&&
                host.tinybrains_rollout_key_fingerprint===
                    learnerMachine.tinybrains_remote_key_fingerprint&&
                host.installed_projects?.includes('tinybrains_rollout')&&
                host.local_executor_enabled===true&&host.native_preflight_passed===true&&
                (host.agent_platform==='win32'||host.agent_platform==='linux'&&
                    host.execution_capabilities?.cgroup_v2_detected===true&&
                    ['cpu','memory'].every(c=>host.execution_capabilities?.cgroup_controllers.includes(c)))&&
                (!budget.gpu_count||host.agent_platform==='win32'&&
                    host.gpu_projects?.includes('tinybrains_rollout')&&
                    host.gpu_inventory_source==='nvidia-smi-v1'));
            if(eligible.length!==1||seen.has(eligible[0].id))
                throw Error('Every approved rollout endpoint must identify exactly one distinct connected, qualified worker.');
            const host=eligible[0];
            seen.add(host.id);
            const timestamp=now();
            const desiredThreads=selection.threads??Math.min(budget.cpus,
                host.tinybrains_rollout_thread_limit);
            if(!Number.isSafeInteger(desiredThreads)||desiredThreads<1||
                desiredThreads>budget.cpus||
                desiredThreads>host.tinybrains_rollout_thread_limit)
                throw Error('Remote worker threads must fit its leased CPUs and approved local thread ceiling.');
            const workerConfiguration={
                id:crypto.randomUUID(),project_id:'tinybrains_rollout',
                name:'Rollout '+(index+1)+' for '+learner.id.slice(-8),
                parameters:{learner_job_id:learner.id,threads:desiredThreads},revision:1,
                created_at:timestamp,updated_at:timestamp,
            };
            next.configurations.push(workerConfiguration);
            const worker=this.queueIn(next,{
                configuration_id:workerConfiguration.id,expected_revision:1,resources:budget,
            });
            worker.release_on_terminal=true;
            const lease=this.claimJobIn(next,worker,host.id,host);
            workers.push(worker);leases.push(lease);
        }
        this.commit(next);
        return {learner:copy(learner),collectors:workers.map(copy),
            leases:leases.map(lease=>({...copy(lease),receipt_fresh:false})),
            starts_authorized:0,
            note:'The entire distributed group has advisory machine claims. Each worker must acknowledge its lease and acquire separate native Start authorization before the learner can start.'};
    }
    // Independent jobs may be chained across non-Bees projects. These
    // dependencies do not imply checkpoint inheritance, machine affinity,
    // shared experiments, or evaluation success.
    queueSequence(value) {
        return this.queueIndependentJobs(value,true);
    }
    queueTrials(value) {
        if(!object(value)||Object.keys(value).some(key=>!['confirm_queue','stages'].includes(key)))
            throw Error('Independent trials accept saved stages and queue confirmation only.');
        return this.queueIndependentJobs(value,false);
    }
    queueIndependentJobs(value,ordered) {
        if(!object(value)||value.confirm_queue!==true||
            !Array.isArray(value.stages)||value.stages.length<2||
            value.stages.length>16||
            (value.after_job_id!=null&&(!ordered||!UUID.test(String(value.after_job_id)))))
            throw Error(ordered?'Explicitly confirm 2–16 independent sequential jobs.':
                'Explicitly confirm 2–16 independent trials without a predecessor.');
        const next=copy(this.state),jobs=[];
        let predecessor=value.after_job_id??null;
        const batchId=ordered?null:crypto.randomUUID();
        for(const stage of value.stages) {
            if(!object(stage)||Object.keys(stage).some(key=>
                !['configuration_id','expected_revision','resources'].includes(key)))
                throw Error('Each job must select a saved configuration and resources.');
            const job=this.queueIn(next,{
                ...stage,...(predecessor?{after_job_id:predecessor}:{})
            });
            if(batchId)job.trial_batch={id:batchId,index:jobs.length+1,size:value.stages.length};
            jobs.push(copy(job));
            if(ordered)predecessor=job.id;
        }
        this.commit(next);
        return {jobs,executable:false,...(batchId?{trial_batch_id:batchId}:{}),
            note:'Independent jobs frozen together; native Start needs explicit authorization or a qualified armed automation session.'};
    }
    // Rank independent completed candidates only on identical frozen
    // benchmark definitions and trial counts. Never use training metrics.
    rankCandidates(value) {
        const ids=value?.candidate_job_ids;
        if(!Array.isArray(ids)||ids.length<2||ids.length>16||
            ids.some(id=>typeof id!=='string'||!UUID.test(id))||
            new Set(ids).size!==ids.length)
            throw Error('Select 2–16 distinct candidate job IDs.');
        const jobs=ids.map(id=>this.state.jobs.find(job=>job.id===id));
        if(jobs.some(job=>!job))throw Error('Candidate job is missing.');
        const roots=jobs.map(job=>job.artifact_lineage_root_id||job.id);
        if(new Set(roots).size!==roots.length)
            throw Error('Compare independent checkpoint lineages, not repeated stages from one model.');
        const compared=jobs.map(job=>{
            const lease=this.state.reservations.find(l=>l.id===job.lease_id&&
                l.job_id===job.id&&l.machine_id===job.assigned_machine_id&&
                l.project_id===job.project_id&&l.issued_by==='experiment-hub'&&
                l.status==='released'&&l.released_at);
            if(!lease||!job.capacity_released_at)
                throw Error('Every candidate must have released its verified native capacity.');
            // The checkpoint publisher is not an independent candidate until
            // every original distributed rank has matched its consensus
            // proof and released its own machine-owned lease. Use the same
            // gate as native model continuation and next-round collection.
            const groupBlock=nativeOptimizerGroupBlock(this.state,job);
            if(groupBlock)throw Error(groupBlock);
            return comparableCandidate(job);
        });
        const first=compared[0];
        if(compared.some(c=>c.project_id!==first.project_id||
            c.policy_signature!==first.policy_signature||
            c.comparison_signature!==first.comparison_signature))
            throw Error('Candidate benchmarks must have identical suites, fingerprints, thresholds and trial counts.');
        const ranked=compared.sort((a,b)=>b.score-a.score||
            a.job_id.localeCompare(b.job_id)).map(({policy_signature,comparison_signature,...item})=>item);
        return {project_id:first.project_id,
            ranking_rule:'equal-weight mean success rate across matching independent suites',
            winner_job_id:ranked[0].job_id,ranked};
    }
    // Choose a trained candidate, but start nothing. The chosen model is
    // cryptographically pinned again at native launch on the original host.
    queueBestCandidate(value) {
        if(!object(value)||value.confirm_queue!==true||
            !UUID.test(String(value.expected_winner_job_id||'')))
            throw Error('Confirm the exact expected winner before queuing continuation.');
        const decision=this.rankCandidates(value);
        if(decision.winner_job_id!==value.expected_winner_job_id)
            throw Error('Candidate results changed; review the ranking before promotion.');
        const next=copy(this.state);
        const plan=value.selection_plan_id==null?null:
            (next.candidate_selections||[]).find(p=>p.id===value.selection_plan_id);
        if(value.selection_plan_id==null&&
            (next.candidate_selections||[]).some(p=>
                ['waiting','needs_review'].includes(p.status)&&
                p.candidate_job_ids.some(id=>value.candidate_job_ids.includes(id))))
            throw Error('Resolve or cancel the existing candidate selection before promoting the same candidate.');
        if(value.selection_plan_id!=null&&
            (!plan||(plan.status!=='waiting'&&
                !(plan.status==='needs_review'&&value.confirm_review===true))||
             JSON.stringify(plan.candidate_job_ids)!==
                JSON.stringify(value.candidate_job_ids)||
             plan.configuration_id!==value.configuration_id||
             plan.expected_revision!==value.expected_revision||
             JSON.stringify(plan.resources)!==JSON.stringify(value.resources)))
            throw Error('Candidate selection plan changed or was already fulfilled.');
        const parent=next.jobs.find(j=>j.id===decision.winner_job_id);
        const job=this.queueIn(next,{
            configuration_id:value.configuration_id,
            expected_revision:value.expected_revision,
            resources:value.resources,
            continuation_of_job_id:parent.id,
        });
        job.expected_parent_model_sha256=parent.checkpoint_model_sha256;
        job.candidate_selection={
            candidate_job_ids:value.candidate_job_ids.slice(),
            winner_job_id:parent.id,
            checkpoint_sha256:parent.checkpoint_model_sha256,
            score:decision.ranked[0].score,
            selected_at:now(),
            ...(plan?{selection_plan_id:plan.id}:{}),
        };
        if(plan) {
            plan.status='queued';
            delete plan.error;
            plan.winner_job_id=parent.id;
            plan.promoted_job_id=job.id;
            plan.resolved_at=job.candidate_selection.selected_at;
            plan.scores=decision.ranked.map(item=>({
                job_id:item.job_id,score:item.score,
            }));
        }
        this.commit(next);
        return {job:copy(job),selection:decision};
    }
    // A reusable winner selection is armed before experiments finish.
    // Its result and the selected continuation commit together, never
    // in two separate writes that could orphan the winner after restart.
    queueCandidateSelection(value) {
        if(!object(value)||value.confirm_queue!==true)
            throw Error('Explicitly confirm the candidate selection plan.');
        const ids=value.candidate_job_ids;
        if(!Array.isArray(ids)||ids.length<2||ids.length>16||
            ids.some(id=>!UUID.test(String(id||'')))||
            new Set(ids).size!==ids.length)
            throw Error('Select 2–16 distinct candidate jobs.');
        const jobs=ids.map(id=>this.state.jobs.find(j=>j.id===id));
        if(jobs.some(j=>!j)||!['courier','tinybrains','bees_native'].includes(jobs[0].project_id)||
            jobs.some(j=>j.project_id!==jobs[0].project_id||
                !j.evaluation_stop||
                !['waiting_adapter','waiting_predecessor','start_requested',
                    'launch_requested','running','stop_requested','finished','stopped'].includes(j.status)))
            throw Error('Candidate jobs must belong to one supported project and have independent evaluations.');
        const roots=jobs.map(j=>j.artifact_lineage_root_id||j.id);
        const thresholds=j=>JSON.stringify(evaluationRules(
            evaluationPolicy(j.evaluation_stop)).map(rule=>[
                rule.suite_id,rule.min_trials,rule.min_success_rate,
            ]).sort(([a],[b])=>a.localeCompare(b)));
        if(new Set(roots).size!==roots.length||
            new Set(jobs.map(thresholds)).size!==1)
            throw Error('Candidate models need independent lineages and identical evaluation thresholds.');
        const suiteDigests=new Map();
        for(const job of jobs)for(const rule of evaluationRules(job.evaluation_stop)) {
            const digest=rule.suite_sha256||job.evaluation_suite_definitions?.[rule.suite_id];
            if(!digest)continue;
            const previous=suiteDigests.get(rule.suite_id);
            if(previous&&previous!==digest)
                throw Error('Candidate jobs have different suite definitions for '+rule.suite_id+'.');
            suiteDigests.set(rule.suite_id,digest);
        }
        if(jobs.some(j=>this.state.jobs.some(child=>child.status!=='cancelled'&&
            (child.continuation_of_job_id===j.id||child.after_job_id===j.id))))
            throw Error('A candidate already has a queued successor.');
        const selected=this.state.configurations.find(c=>c.id===value.configuration_id&&
            c.revision===value.expected_revision&&c.project_id===jobs[0].project_id);
        if(!selected)throw Error('Select a current saved continuation configuration in the same project.');
        if(jobs[0].project_id==='tinybrains'&&
            (Object.hasOwn(selected.parameters,'import_checkpoint')||
                Object.hasOwn(selected.parameters,'import_checkpoint_sha256')))
            throw Error('Candidate winner continuation needs a TinyBrains configuration without an external checkpoint import.');
        const pin=jobs[0].project_id==='tinybrains'?'profile_sha256':'config_sha256';
        if(!SHA256.test(String(selected.parameters?.[pin]||''))||
            jobs.some(j=>j.frozen_parameters?.[pin]!==selected.parameters[pin]))
            throw Error('Every candidate must be compatible with the same approved continuation environment.');
        const resources=resourceRequest(value.resources);
        const next=copy(this.state),plans=next.candidate_selections||[];
        // The winner job retains its own decision and the contender jobs
        // retain their receipts. Prune only concluded plan metadata so a
        // long-running experiment queue never fills with old decisions.
        if(plans.length>=64) {
            const archived=plans.findIndex(p=>
                ['queued','cancelled'].includes(p.status));
            if(archived<0)
                throw Error('Too many unresolved candidate selection plans.');
            plans.splice(archived,1);
        }
        if(plans.some(p=>['waiting','needs_review'].includes(p.status)&&
            p.candidate_job_ids.some(id=>ids.includes(id))))
            throw Error('One candidate already belongs to another unresolved selection.');
        const plan={id:crypto.randomUUID(),project_id:jobs[0].project_id,
            candidate_job_ids:ids.slice(),
            configuration_id:selected.id,expected_revision:selected.revision,
            resources,status:'waiting',created_at:now()};
        plans.push(plan);
        next.candidate_selections=plans;
        this.commit(next);
        return copy(plan);
    }
    cancelCandidateSelection(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.selection_id||'')))
            throw Error('Explicitly confirm the candidate selection to cancel.');
        const next=copy(this.state);
        const plan=(next.candidate_selections||[])
            .find(item=>item.id===value.selection_id);
        if(!plan||!['waiting','needs_review'].includes(plan.status))
            throw Error('Only an unresolved candidate selection can be cancelled.');
        plan.status='cancelled';
        plan.cancelled_at=now();
        this.commit(next);
        return copy(plan);
    }
    // One durable write for the full linear checkpoint-continuation
    // pipeline: either all stages have validated lineage, or none do.
    queuePipeline(value) {
        if(!object(value)||value.confirm_queue!==true||
            !Array.isArray(value.stages)||value.stages.length<2||
            value.stages.length>16)
            throw Error('Explicitly confirm a 2–16-stage pipeline.');
        const next=copy(this.state),jobs=[];
        let predecessor=value.continuation_of_job_id??null;
        for(const stage of value.stages) {
            if(!object(stage)||Object.keys(stage).some(key=>
                !['configuration_id','expected_revision','resources'].includes(key)))
                throw Error('Each pipeline stage must select one saved configuration and resource request.');
            const job=this.queueIn(next,{
                ...stage,continuation_of_job_id:predecessor,
            });
            if(!['tinybrains','courier'].includes(job.project_id))
                throw Error('Checkpoint pipelines require the TinyBrains or Courier adapter.');
            jobs.push(copy(job));
            predecessor=job.id;
        }
        this.commit(next);
        return {jobs,executable:false,
            note:'Frozen pipeline only; native execution remains subject to separate authorization.'};
    }
    queueIn(next,value,{optimizerRestartFrom=null,modelPin=null}={}) {
        if (!object(value) || typeof value.configuration_id !== 'string')
            throw Error('Select a saved configuration to queue.');
        const selected = next.configurations.find(x=>x.id===value.configuration_id);
        if (!selected) throw Error('Saved configuration not found.');
        const project=next.projects.find(p=>p.id===selected.project_id);
        if(!project)throw Error('Unknown project.');
        if(project.id==='ants')
            throw Error('Ants-v2 Hub integration is retired.');
        if (project.adapter === 'bees')
            throw Error('Bees jobs use the existing validated Bees experiment queue.');
        if (project.adapter !== 'unconfigured')
            throw Error('Unknown execution adapter; refusing to schedule.');
        if (value.expected_revision !== selected.revision)
            throw Error('Draft changed since selection; reload before queueing.');
        // Freeze the exact input and resources. No executable, shell snippet,
        // directory, or process arguments can be supplied in the job request.
        const frozen = config(selected.parameters);
        const resources = resourceRequest(value.resources);
        // Telemetry selection is independent from optimizer configuration
        // fingerprints. It never influences a model or continuation lineage.
        const selectedMetrics=value.metric_selection??null;
        if(selectedMetrics!==null&&(!Array.isArray(selectedMetrics)||
            selectedMetrics.length>96||
            selectedMetrics.some(k=>typeof k!=='string'||
                !/^[a-z][a-z0-9_]{0,63}$/.test(k))||
            new Set(selectedMetrics).size!==selectedMetrics.length))
            throw Error('Select up to 96 distinct project metric names.');

        const ancestorId=value.continuation_of_job_id==null
            ?null:value.continuation_of_job_id;
        const afterId=value.after_job_id==null?null:value.after_job_id;
        const sourceId=value.model_source_job_id==null
            ?null:value.model_source_job_id;
        const replacementId=value.collector_replacement_of_job_id??null;
        if(replacementId!==null&&
            (project.id!=='bees_native_collect'||
             value.confirm_collector_replacement!==true||
             !UUID.test(String(replacementId))||
             ancestorId!==null||afterId!==null))
            throw Error('Explicitly approve a failed native collector replacement with no checkpoint or stage dependency.');
        if(replacementId===null&&value.confirm_collector_replacement!==undefined)
            throw Error('Collector replacement approval requires a selected failed native job.');
        if(sourceId!==null&&(
            !UUID.test(String(sourceId))||
            !['bees_native','bees_native_collect','bees_native_optimizer_worker']
                .includes(project.id)||
            afterId!==null||
            (ancestorId!==null&&ancestorId!==sourceId)||
            (project.id==='bees_native'&&ancestorId!==sourceId)))
            throw Error('Native model handoffs require an exact eligible Bees checkpoint owner.');
        if(afterId!==null&&ancestorId!==null)
            throw Error('A job cannot both depend on an independent predecessor and continue its checkpoint.');
        if(project.id==='tinybrains_rollout') {
            const learner=next.jobs.find(j=>j.id===frozen.learner_job_id);
            if(Object.keys(frozen).some(key=>!['learner_job_id','threads'].includes(key))||
                (frozen.threads!==undefined&&
                    (!Number.isSafeInteger(frozen.threads)||frozen.threads<1||
                        frozen.threads>resources.cpus||frozen.threads>256))||
                !UUID.test(String(frozen.learner_job_id||''))||
                !learner||learner.project_id!=='tinybrains'||
                learner.frozen_parameters?.remote_rollouts!==true||
                ['finished','stopped','failed','cancelled'].includes(learner.status)||
                (resources.gpu_count!==0&&
                    (resources.gpu_count!==1||resources.gpu_mode!=='exclusive'))||
                ancestorId!==null||afterId!==null)
                throw Error('Remote collector requires an active or queued distributed TinyBrains learner, CPU-only or one exclusive GPU, and no separate predecessor.');
            if(next.jobs.filter(j=>j.project_id==='tinybrains_rollout'&&
                j.status!=='cancelled'&&
                j.frozen_parameters?.learner_job_id===learner.id).length>=8)
                throw Error('Only eight separately leased remote collectors may join a TinyBrains learner.');
        }
        if(ancestorId!==null&&project.id==='tinybrains'&&
            (Object.hasOwn(frozen,'import_checkpoint')||
                Object.hasOwn(frozen,'import_checkpoint_sha256')))
            throw Error('TinyBrains checkpoint imports initialize a new lineage only. Select a continuation configuration without import_checkpoint.');
        let parent=null,dependency=null,waitingPredecessor=false;
        let dependencyReady=false;
        let failedCollector=null;
        if(replacementId!==null) {
            failedCollector=next.jobs.find(job=>job.id===replacementId);
            if(!failedCollector||
                failedCollector.project_id!=='bees_native_collect'||
                failedCollector.configuration_id!==selected.id||
                failedCollector.configuration_revision!==selected.revision||
                JSON.stringify(failedCollector.frozen_parameters)!==JSON.stringify(frozen)||
                (failedCollector.model_source_job_id||null)!==sourceId||
                failedCollector.status!=='failed'||
                !failedCollector.capacity_released_at||
                !failedCollector.assigned_machine_id||
                !failedCollector.dispatch_id||
                !failedCollector.execution_updated_at||
                resources.gpu_count!==failedCollector.resources.gpu_count||
                resources.gpu_mode!==failedCollector.resources.gpu_mode||
                next.jobs.some(job=>job.collector_replacement_of_job_id===replacementId&&
                    job.status!=='cancelled'))
                throw Error('Only a released, failed collector with the same immutable configuration can be replaced.');
        }
        if(ancestorId!==null) {
            if(!['tinybrains','courier','bees_native'].includes(project.id))
                throw Error('Checkpoint continuation requires a verified supported learner adapter.');
            if(typeof ancestorId!=='string'||!UUID.test(ancestorId))
                throw Error('Select a valid predecessor job for continuation.');
            parent=next.jobs.find(j=>j.id===ancestorId);
            if(parent?.distributed_abort_requested_at)
                throw Error('Cannot continue an abandoned distributed learner.');
            const pinnedRoot=parent?.artifact_lineage_root_id||parent?.id;
            const futureStage=['waiting_adapter','waiting_predecessor'].includes(parent?.status);
            if(!parent||parent.project_id!==project.id||
                !UUID.test(String(pinnedRoot||''))||
                !(parent.dispatch_id&&parent.assigned_machine_id||futureStage))
                throw Error('Continuation requires a dispatched predecessor or an eligible prequeued stage.');
            // Supported adapters can prequeue a bounded, linear sequence.
            // Future stages inherit the original host and artifact root
            // without claiming capacity or launching any process.
            waitingPredecessor=(
                ['start_requested','launch_requested','running'].includes(parent.status)||
                (parent.status==='finished'&&!parent.capacity_released_at)||
                futureStage);
            if(!waitingPredecessor&&
                (!checkpointContinuationComplete(parent)||!parent.capacity_released_at))
                throw Error('Continuation requires a verified resumable checkpoint and released capacity (or an eligible active or prequeued predecessor).');
            const successors=next.jobs.filter(j=>
                (j.continuation_of_job_id===parent.id||
                 j.after_job_id===parent.id)&&occupiesSuccessorSlot(j,next.jobs));
            if(successors.length) {
                // A synchronized restart does not consume another source
                // checkpoint or open a parallel continuation. It replaces
                // the failed optimizer group at the end of one exact, fully
                // released rank-zero lineage, retaining the original
                // model and collector receipts.
                const allowed=new Set();
                let previous=optimizerRestartFrom;
                while(previous&&!allowed.has(previous.id)) {
                    allowed.add(previous.id);
                    previous=next.jobs.find(job=>
                        job.id===previous.optimizer_restart_of_job_id);
                }
                if(!(optimizerRestartFrom&&
                    project.id==='bees_native'&&
                    optimizerRestartFrom.continuation_of_job_id===parent.id&&
                    successors.every(job=>allowed.has(job.id)&&
                        job.project_id==='bees_native'&&
                        job.frozen_parameters?.optimizer_group===true&&
                        job.capacity_released_at&&
                        ['finished','failed','stopped'].includes(job.status)&&
                        nativeOptimizerGroupBlock(next,job))))
                    throw Error('This predecessor already has an active or unresolved successor.');
            }
        }
        if(parent) {
            const digestField=project.id==='tinybrains'?'profile_sha256':'config_sha256';
            const digest=parent.frozen_parameters?.[digestField];
            // A physically trained native Bees checkpoint may enter its
            // first Hub-owned imported-bundle PPO optimizer. The direct
            // Level template and bundle-only template intentionally have
            // different configuration hashes; requiring equality here
            // makes this otherwise authenticated bootstrap impossible.
            // This is ONLY a same-project, explicitly selected checkpoint
            // source for a synchronized native optimizer. The resource
            // bundle still verifies every physical collector against the
            // source checkpoint, pins all GPU ranks to one learner input,
            // and keeps the original owner's linear continuation slot.
            const nativeBundleBootstrap=
                project.id==='bees_native'&&
                parent.project_id==='bees_native'&&
                parent.frozen_parameters?.optimizer_group!==true&&
                frozen.optimizer_group===true&&
                sourceId===parent.id&&
                optimizerRestartFrom===null;
            if(typeof digest!=='string'||
                digest!==frozen[digestField]&&!nativeBundleBootstrap)
                throw Error('Checkpoint continuation requires the same approved configuration digest as the predecessor.');
        }
        if(afterId!==null) {
            if(!UUID.test(String(afterId)))
                throw Error('Select a valid independent predecessor.');
            dependency=next.jobs.find(j=>j.id===afterId);
            if(dependency?.distributed_abort_requested_at)
                throw Error('An abandoned distributed group cannot accept a new dependent job.');
            if(dependency?.project_id==='tinybrains_rollout'||
                dependency?.distributed_rollout_managed===true)
                throw Error('Distributed group services cannot be independent-stage predecessors.');
            const completed=independentSequenceComplete(dependency);
            if(!dependency||['bees','ants'].includes(dependency.project_id)||
                !(['waiting_adapter','waiting_predecessor',
                    'start_requested','launch_requested','running'].includes(
                        dependency.status)||completed)||
                (completed&&
                    (!dependency.dispatch_id||!dependency.execution_updated_at||
                     !dependency.assigned_machine_id)))
                throw Error('Independent dependency needs an eligible non-Bees predecessor.');
            if(next.jobs.some(j=>
                (j.continuation_of_job_id===afterId||
                 j.after_job_id===afterId)&&occupiesSuccessorSlot(j,next.jobs)))
                throw Error('This predecessor already has an active or unresolved successor.');
            waitingPredecessor=!completed||!dependency.capacity_released_at;
            if(!waitingPredecessor) {
                // A dependency selected after its parent was released must
                // still prove terminal lease ownership before freezing an
                // immediately eligible job. Pruned old history fails closed.
                const released=next.reservations.find(l=>
                    l.id===dependency.lease_id&&
                    l.job_id===dependency.id&&
                    l.project_id===dependency.project_id&&
                    l.machine_id===dependency.assigned_machine_id&&
                    l.issued_by==='experiment-hub'&&
                    l.status==='released'&&Boolean(l.released_at));
                if(!released)
                    throw Error('Cannot queue against a completed dependency whose released native lease is no longer verifiable.');
                dependencyReady=true;
            }
        }
         if (next.jobs.filter(j=>['waiting_adapter',
            'waiting_predecessor'].includes(j.status)).length >= 100)
            throw Error('Too many pending jobs; cancel unused jobs first.');
        if (next.jobs.length >= 1000)
            throw Error('Job history limit reached. Export or archive older jobs before queueing more.');
        const jobId=crypto.randomUUID();
        const job = {
            id:jobId,
            project_id:project.id, adapter:project.adapter,
            continuation_of_job_id:parent?.id||null,
            after_job_id:dependency?.id||null,
            predecessor_ready_at:dependencyReady?now():null,
            artifact_lineage_root_id:parent
                ?(parent.artifact_lineage_root_id||parent.id)
                :failedCollector
                    ?(failedCollector.artifact_lineage_root_id||failedCollector.id)
                    :jobId,
            ...(failedCollector?{
                collector_replacement_of_job_id:failedCollector.id,
            }:{}),
            ...(sourceId?(modelPin||verifiedNativeModelPin(this,
                next.jobs.find(item=>item.id===sourceId))):{}),
            continuation_machine_id:parent
                ?(parent.continuation_machine_id||
                    (parent.dispatch_id?parent.assigned_machine_id:null))
                :failedCollector?failedCollector.assigned_machine_id:null,
            configuration_id:selected.id,
            configuration_revision:selected.revision,
            configuration_name:selected.name,
            configuration_sha256:crypto.createHash('sha256')
                .update(JSON.stringify(frozen)).digest('hex'),
            frozen_parameters:frozen, resources,
            ...(selectedMetrics?.length?{metric_selection:selectedMetrics.slice()}:{}),
            ...(['courier','tinybrains','bees_native'].includes(project.id)
                ?{checkpoint_proof_required:true}:{}),
            ...(selected.evaluation_stop
                ?{evaluation_stop:evaluationPolicy(selected.evaluation_stop)}:{}),
            status:waitingPredecessor?'waiting_predecessor':'waiting_adapter',
            status_reason:waitingPredecessor
                ?'Prequeued stage; awaiting successful predecessor completion and capacity release.'
                :failedCollector
                    ?'Failed collector replacement queued. Original-machine capacity and an explicit native Start are required; no process has been relaunched.'
                    :'Queued; a separate machine claim and explicit native start authorization are required.',
            created_at:now(), cancelled_at:null,
        };
        const modelSourceBlock=nativeModelSourceBlock(next,job);
        if(modelSourceBlock)throw Error(modelSourceBlock);
        const replacementBlock=collectorReplacementBlock(next,job);
        if(replacementBlock)throw Error(replacementBlock);
        next.jobs.push(job);
        return job;
    }
    // Cancel only future, never-dispatched jobs in one durable update.
    // The selected predecessor is untouched; its native supervisor remains
    // the sole execution owner.
    cancelFutureJobs(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.after_job_id||'')))
            throw Error('Explicitly confirm the predecessor whose queued successors should be cancelled.');
        const next=copy(this.state);
        let parent=next.jobs.find(j=>j.id===value.after_job_id);
        if(!parent)throw Error('Selected predecessor job not found.');
        const visited=new Set([parent.id]),future=[];
        while(true) {
            const children=next.jobs.filter(j=>j.status!=='cancelled'&&
                (j.after_job_id===parent.id||
                 j.continuation_of_job_id===parent.id));
            if(children.length>1)
                throw Error('Predecessor has conflicting successor branches; inspect registry.');
            if(!children.length)break;
            const child=children[0];
            if(visited.has(child.id)||future.length>=1000)
                throw Error('Invalid or cyclic queued dependency chain.');
            if(!['waiting_adapter','waiting_predecessor'].includes(
                    child.status)||child.dispatch_id||
                (child.status==='waiting_predecessor'&&child.lease_id))
                throw Error('A downstream job was dispatched or has uncertain state; cancel future jobs only after native review.');
            visited.add(child.id);
            future.push(child);
            parent=child;
        }
        if(!future.length)throw Error('No queued successor jobs to cancel.');
        const cancelledIds=future.map(job=>job.id);
        const expected=value.expected_job_ids;
        if(Object.hasOwn(value,'expected_job_ids')&&
            (!Array.isArray(expected)||expected.length!==cancelledIds.length||
             cancelledIds.some((id,index)=>id!==expected[index])))
            throw Error('The queued successors changed. Review the refreshed sequence before cancelling.');
        if(next.candidate_selections?.some(plan=>
            ['waiting','needs_review'].includes(plan.status)&&
            future.some(job=>plan.candidate_job_ids.includes(job.id))))
            throw Error('Cancel the unresolved candidate selection before cancelling its queued successor.');
        const cancellationTime=now();
        // Descendants first. Reuse existing unstarted lease release and
        // GPU-qualification cancellation, then commit every state together.
        for(const job of future.reverse()) {
            if(job.lease_id) {
                const lease=next.reservations.find(l=>l.id===job.lease_id&&
                    l.job_id===job.id&&
                    ['offered','acknowledged'].includes(l.status));
                if(!lease)
                    throw Error('Queued job lease ownership changed; no cancellation committed.');
                this.releaseIn(next,lease);
            }
            job.status='cancelled';
            job.status_reason='Future queued job cancelled by confirmed operator action.';
            job.cancelled_at=cancellationTime;
        }
        this.commit(next);
        return {cancelled_job_ids:cancelledIds,
            cancelled_count:cancelledIds.length,starts_stopped:0};
    }
    cancelJob(value) {
        if (!object(value) || typeof value.job_id!=='string' ||
            value.confirm_cancel!==true)
            throw Error('Select a queued job and explicitly confirm cancellation.');
        const next=copy(this.state);
        const job=next.jobs.find(j=>j.id===value.job_id);
        if (!job) throw Error('Job not found.');
        if((next.distributed_group_plans||[]).some(plan=>
            ['pending','needs_review'].includes(plan.status)&&
            (plan.learner_job_id===job.id||
             plan.collector_job_ids.includes(job.id))))
            throw Error('Cancel the pending distributed group approval before cancelling an individual native job.');
        if (job.status==='cancelled') return copy(job);
        const cancelledAt=now();
        if(job.project_id==='tinybrains'&&job.distributed_rollout_managed===true) {
            // The collector jobs are not checkpoint successors: they exist
            // solely for this learner. Cancel every unstarted child before
            // withdrawing the learner. Any uncertain/started sidecar blocks
            // the whole cancellation and requires explicit native Stop.
            for(const child of next.jobs.filter(candidate=>
                candidate.project_id==='tinybrains_rollout'&&
                candidate.frozen_parameters?.learner_job_id===job.id&&
                candidate.status!=='cancelled'))
                this.cancelUnstartedJob(next,child,cancelledAt);
        }
        this.cancelUnstartedJob(next,job,cancelledAt);
        this.commit(next);
        return copy(job);
    }
    // Undo a never-dispatched bundle as one durable transaction. If one
    // member has started or is uncertain, do not release any of the group's
    // claims: the local native owner must first be reconciled explicitly.
    cancelResourceBundle(value) {
        if(!object(value)||value.confirm_cancel!==true||
            !UUID.test(String(value.bundle_id||''))||
            !Array.isArray(value.expected_job_ids)||
            value.expected_job_ids.length<1||value.expected_job_ids.length>16||
            value.expected_job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.expected_job_ids).size!==value.expected_job_ids.length)
            throw Error('Confirm the exact resource bundle and outstanding member jobs.');
        const next=copy(this.state);
        const members=next.jobs.filter(job=>job.resource_bundle?.id===value.bundle_id);
        if(!members.length||members.length!==members[0].resource_bundle.size||
            members.some((job,index)=>job.resource_bundle.index!==index+1||
                job.resource_bundle.size!==members.length))
            throw Error('Resource bundle member ledger is incomplete or inconsistent.');
        const active=members.filter(job=>job.status!=='cancelled');
        const expected=new Set(value.expected_job_ids);
        if(active.length!==expected.size||
            active.some(job=>!expected.has(job.id)))
            throw Error('Bundle members have changed. Refresh before cancelling.');
        if(active.some(job=>job.dispatch_id||job.status!=='waiting_adapter'))
            throw Error('A bundle member was started or changed state. Reconcile native ownership before cancelling.');
        const at=now();
        for(const job of active)this.cancelUnstartedJob(next,job,at);
        const planned=(next.bundle_start_plans||[]).find(plan=>
            plan.bundle_id===value.bundle_id&&
            ['pending','needs_review'].includes(plan.status));
        if(planned) {
            planned.status='disarmed';
            planned.reason='Operator cancelled the unstarted reserved bundle.';
            planned.resolved_at=at;
        }
        this.commit(next);
        return {bundle_id:value.bundle_id,cancelled_job_ids:active.map(job=>job.id),
            cancelled_count:active.length,starts_stopped:0};
    }
    cancelQueuedTrials(value) {
        if(!object(value)||value.confirm_cancel!==true||!UUID.test(String(value.trial_batch_id||''))||
            !Array.isArray(value.expected_job_ids)||!value.expected_job_ids.length||
            value.expected_job_ids.length>16||value.expected_job_ids.some(id=>!UUID.test(String(id))))
            throw Error('Confirm the trial batch and exact queued jobs to cancel.');
        const next=copy(this.state);
        const jobs=next.jobs.filter(job=>job.trial_batch?.id===value.trial_batch_id&&
            job.status==='waiting_adapter'&&!job.dispatch_id);
        const ids=jobs.map(job=>job.id);
        const expected=new Set(value.expected_job_ids);
        if(ids.length!==expected.size||ids.some(id=>!expected.has(id))||
            expected.size!==value.expected_job_ids.length)
            throw Error('Queued trials changed. Review the batch before cancelling.');
        if(next.candidate_selections?.some(plan=>['waiting','needs_review'].includes(plan.status)&&
            plan.candidate_job_ids.some(id=>expected.has(id))))
            throw Error('Cancel the unresolved candidate selection before cancelling its trials.');
        const cancelledAt=now();
        for(const job of jobs)this.cancelUnstartedJob(next,job,cancelledAt);
        this.commit(next);
        return {cancelled_job_ids:ids,cancelled_count:ids.length,starts_stopped:0};
    }
    cancelUnstartedJob(next,job,cancelledAt) {
        if (job.dispatch_id||!['waiting_adapter','waiting_predecessor'].includes(job.status))
            throw Error('Only unstarted jobs can be cancelled without an execution adapter.');
        if(next.candidate_selections?.some(plan=>
            ['waiting','needs_review'].includes(plan.status)&&
            plan.candidate_job_ids.includes(job.id)))
            throw Error('Cancel the unresolved candidate selection before cancelling its candidate.');
        if(next.jobs.some(j=>
            (j.continuation_of_job_id===job.id||j.after_job_id===job.id)&&
            j.status!=='cancelled'))
            throw Error('Cancel the queued successor stage before cancelling its predecessor.');
        if(job.lease_id) {
            const lease=next.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id && ['offered','acknowledged'].includes(l.status));
            if(!lease)throw Error('Job allocation ledger inconsistent; refusing cancellation.');
            if(job.native_release_recovery) {
                if(gpuProbeHoldsClaim(job))
                    throw Error('Wait for native GPU probe cleanup before cancelling release recovery.');
                if(job.gpu_qualification?.status==='requested')
                    job.gpu_qualification.status='cancelled';
                // Only this unoffered recovery is cancelled. The original
                // failed training dispatch and artifact ownership survive.
                job.status='failed';
                job.dispatch_id=job.native_release_recovery.previous_dispatch_id;
                job.start_first_offered_at=
                    job.native_release_recovery.previous_start_first_offered_at;
                job.native_release_recovery.cancelled_at=cancelledAt;
            }
            this.releaseIn(next,lease);
        }
        job.status='cancelled';
        job.status_reason=job.native_release_recovery
            ?'Prepared generation release recovery cancelled; committed PPO artifacts retained.'
            :'Cancelled before any execution was permitted.';
        job.cancelled_at=cancelledAt;
    }
    plannedJobs() {
        const jobs=this.state.jobs.filter(j=>
            j.status==='waiting_adapter'&&j.project_id!=='ants'&&
            !predecessorBlock(this.state,j));
        const snapshot=this.snapshot();
        // Rehearse the same per-project FIFO interleave and best-fit
        // decisions as allocateJobs, but do not mint leases or mutate state.
        // The separate POST /api/hub/plan remains a general GPU-first
        // what-if optimizer and is not a prediction of the queue pass.
        // Claimed jobs still occupy their position in each project's
        // FIFO queue. Skipping them *before* interleaving would give
        // a project extra priority compared with allocateJobs.
        const ordered=fairPending(jobs);
        const reservations=copy(snapshot.reservations);
        const machines=snapshot.machines.filter(m=>m.status==='connected');
        const draftJobs=copy(snapshot.jobs);
        const proposed=[];
        for(const [index,job] of ordered.entries()) {
            if(job.lease_id)continue;
            const claimCount=reservations.filter(r=>
                ['active','offered','acknowledged'].includes(r.status)).length;
            const chosen=claimCount<100
                ?allocationChoice(machines,reservations,job,null,
                    ordered.slice(index+1),()=>machines,{machines: snapshot.machines,jobs:draftJobs}):null;
            if(chosen) {
                reservations.push({
                    machine_id:chosen.machine_id,status:'offered',
                    resources:{...resourceRequest(job.resources),
                        gpus:chosen.suggested_gpus},
                });
                const predicted=draftJobs.find(j=>j.id===job.id);
                if(predicted)predicted.assigned_machine_id=chosen.machine_id;
            }
            proposed.push({id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,
                status:job.status,claimed:false,executable:false,
                machine_id:chosen?.machine_id||null,
                machine_name:chosen?.machine_name||null,
                suggested_gpus:chosen?.suggested_gpus||[],
                capacity_fits:Boolean(chosen),online:Boolean(chosen?.online),
                reason:claimCount>=100
                    ?'Hub claim limit reached'
                    :chosen?'Would receive advisory capacity on this agent'
                    :allocationBlockReason(machines,job),
            });
        }
        const claimed=jobs.filter(j=>j.lease_id).map(job=>{
            const lease=snapshot.reservations.find(l=>l.id===job.lease_id &&
                l.job_id===job.id && ['offered','acknowledged'].includes(l.status));
            if(!lease)return {id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,claimed:true,
                status:'ledger_inconsistent',executable:false};
            const machine=snapshot.machines.find(m=>m.id===lease.machine_id);
            return {id:job.id,project_id:job.project_id,
                configuration_name:job.configuration_name,
                status:job.status,claimed:true,lease_id:lease.id,
                lease_status:lease.status,receipt_fresh:lease.receipt_fresh,
                machine_id:lease.machine_id,
                machine_name:machine?.name||lease.machine_id,
                online:machine?.status==='connected',capacity_fits:false,
                suggested_gpus:lease.resources.gpus,executable:false,
                reason:'Existing advisory lease, not a new capacity placement'};
        });
        const byId=new Map([...claimed,...proposed].map(j=>[j.id,j]));
        return {jobs:jobs.map(j=>byId.get(j.id)),note:
            'Read-only preview of the fair allocation pass, not a persistent claim, OS isolation or execution permission.'};
    }
    updateMachineBudget(value) {
        if(!object(value)||value.confirm_budget!==true||
            !ID.test(String(value.machine_id||'')))
            throw Error('Explicitly confirm the machine whose Hub budget will change.');
        const cpu=value.cpus,ram=value.ram_mb;
        if(!Number.isSafeInteger(cpu)||cpu<1||cpu>4096||
            !Number.isSafeInteger(ram)||ram<128||ram>100000000)
            throw Error('Hub CPU and RAM budgets must be positive bounded integers.');
        if(this.automation.enabled)
            throw Error('Disarm native queue automation before changing machine budgets.');
        const machine=this.state.machines.find(m=>m.id===value.machine_id);
        if(!machine)throw Error('Machine not registered.');
        if(value.expected_cpus!==machine.cpus||
            value.expected_ram_mb!==machine.ram_mb)
            throw Error('Machine budget changed; refresh the displayed values before saving.');
        const claimed=this.state.reservations.filter(l=>
            l.machine_id===machine.id&&
            ['active','offered','acknowledged'].includes(l.status));
        const claimedCpu=claimed.reduce((total,l)=>total+l.resources.cpus,0);
        const claimedRam=claimed.reduce((total,l)=>total+l.resources.ram_mb,0);
        // Never reduce admission below claims for processes that may already
        // exist. Budgets are bookkeeping, not OS controls and never Stop jobs.
        if(cpu<claimedCpu||ram<claimedRam)
            throw Error('Release or finish existing Hub claims before reducing capacity below their total.');
        if(cpu===machine.cpus&&ram===machine.ram_mb)
            return {machine_id:machine.id,cpus:cpu,ram_mb:ram,
                claimed_cpus:claimedCpu,claimed_ram_mb:claimedRam,changed:false};
        const next=copy(this.state);
        const updated=next.machines.find(m=>m.id===machine.id);
        updated.cpus=cpu;updated.ram_mb=ram;
        updated.budget_updated_at=now();
        this.commit(next);
        return {machine_id:machine.id,cpus:cpu,ram_mb:ram,
            claimed_cpus:claimedCpu,claimed_ram_mb:claimedRam,changed:true};
    }
    addMachine(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase machine ID.');
        if (this.state.machines.some(m=>m.id===value.id)) throw Error('Machine already registered.');
        const cpu = Number(value.cpus), ram = Number(value.ram_mb);
        if (!Number.isSafeInteger(cpu) || cpu < 1 || cpu > 4096 ||
            !Number.isSafeInteger(ram) || ram < 128 || ram > 100000000)
            throw Error('Machine CPU and RAM capacity must be positive integers.');
        const gpu=configuredGpus(value.gpus==null?[]:value.gpus);
        const machine = {
            id:value.id, name:name(value.name,'Machine name'),
            // All machines start inactive. An installed agent must prove health
            // before claims can be applied; a manually entered inventory is not authority.
            status:'unverified', cpus:cpu, ram_mb:ram, gpus:copy(gpu),
            created_at:now(), last_seen_at:null,
        };
        const next = copy(this.state);
        next.machines.push(machine);
        this.commit(next);
        return copy(machine);
    }
}
module.exports = {ExperimentHub, writeAtomic};
