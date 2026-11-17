'use strict';

// One authenticated Courier PPO lineage per independently leased Hub service.
// The host operator pins the private endpoint and key; the learner retains
// population/checkpoint ownership, while this native service owns only its
// remote CPU/RAM/GPU resources and a per-job scratch directory.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const CUDA=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;

function canonicalEndpoint(ip,port) {
    if(typeof ip!=='string'||!/^100\.(?:\d{1,3}\.){2}\d{1,3}$/.test(ip)||
        ip.split('.').some((part,i)=>String(Number(part))!==part||
            Number(part)>(i===0?100:255))||
        !Number.isSafeInteger(port)||port<1024||port>65535)
        throw Error('Courier remote service requires a canonical Tailscale IPv4 and nonprivileged port.');
    const second=Number(ip.split('.')[1]);
    if(second<64||second>127)
        throw Error('Courier remote service must use encrypted Tailnet IPv4.');
    return ip+':'+port;
}
function existingDirectory(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value||!fs.statSync(value).isDirectory())
        throw Error('Courier remote installation path must be an existing, real directory.');
    return value;
}
function secretFile(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value)
        throw Error('Courier remote secret must be a real absolute file.');
    const stat=fs.statSync(value);
    if(!stat.isFile()||stat.size<64||stat.size>128||
        (process.platform!=='win32'&&(stat.mode&0o077)!==0)||
        !/^[0-9a-fA-F]{64}$/.test(fs.readFileSync(value,'utf8').trim()))
        throw Error('Courier remote secret must contain 32 hex bytes in a private file.');
    return value;
}

function createCourierRemoteLineageAdapter({
    projectRoot,pythonPath,keyPath,cacheRoot,listenIp,port,
    maxShards=64,enableCuda=false,enableRocm=false,rocrGpuUuids={},
}) {
    if(typeof enableCuda!=='boolean'||typeof enableRocm!=='boolean'||
        (enableCuda&&enableRocm)||
        (enableCuda&&process.platform!=='win32')||
        (enableRocm&&process.platform!=='linux'))
        throw Error('Courier remote GPU acceleration requires an approved local CUDA/ROCm platform.');
    if(!rocrGpuUuids||typeof rocrGpuUuids!=='object'||
        Array.isArray(rocrGpuUuids)||Object.keys(rocrGpuUuids).length>32||
        (!enableRocm&&Object.keys(rocrGpuUuids).length)||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid))||
        new Set(Object.values(rocrGpuUuids)).size!==Object.keys(rocrGpuUuids).length)
        throw Error('Courier remote ROCm UUID map needs unique approved physical PCI devices.');
    if(!Number.isSafeInteger(maxShards)||maxShards<1||maxShards>64)
        throw Error('Courier remote simulation shard ceiling must be between 1 and 64.');
    const project=existingDirectory(projectRoot);
    const cache=existingDirectory(cacheRoot);
    const secret=secretFile(keyPath);
    const fingerprint=crypto.createHash('sha256').update(fs.readFileSync(secret)).digest('hex');
    const endpoint=canonicalEndpoint(listenIp,port);
    if(typeof pythonPath!=='string'||!path.isAbsolute(pythonPath)||
        fs.realpathSync(pythonPath)!==pythonPath||!fs.statSync(pythonPath).isFile())
        throw Error('Courier remote Python must be an installed executable file.');
    const probe=spawnSync(pythonPath,['-B','-c',
        'import courier_remote_lineage as r; assert hasattr(r, "serve")'],{
        cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    if(probe.status!==0)
        throw Error('Installed Courier lacks its authenticated remote-lineage protocol.');
    if(enableCuda||enableRocm) {
        const gpuProbe=spawnSync(pythonPath,['-B','-c',enableCuda
            ?'import torch; assert torch.version.cuda and torch.cuda.is_available()'
            :'import torch; assert torch.version.hip and torch.cuda.is_available()'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(gpuProbe.status!==0)
            throw Error('Installed Courier remote worker has no approved CUDA/ROCm runtime.');
    }
    const approved=Object.freeze({...rocrGpuUuids});
    return {
        executable:pythonPath,cwd:project,stop_policy:'terminate-group',
        courier_lineage_endpoint:endpoint,
        courier_lineage_key_fingerprint:fingerprint,
        courier_lineage_max_shards:maxShards,
        ...(enableCuda?{gpu_execution:'cuda'}:{}),
        ...(enableRocm?{
            rocr_gpu_uuids:approved,
            gpu_verify_args(_parameters,context) {
                if(!PCI.test(String(context?.gpu_pci_address||''))||
                    context.gpu_pci_addresses!==undefined)
                    throw Error('Courier remote GPU verification requires one physical lease.');
                return ['-B',path.join(__dirname,'experiment_hub_tinybrains_rocm_verify.py'),
                    context.gpu_pci_address];
            },
        }:{}),
        args(parameters,context) {
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(key=>!['learner_job_id','slot'].includes(key))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context?.job_id||''))||
                context.job_id===parameters.learner_job_id||
                !Number.isSafeInteger(parameters.slot)||parameters.slot<0||
                parameters.slot>63||
                !Number.isSafeInteger(context.cpu_quota)||context.cpu_quota<1||
                !Number.isSafeInteger(context.ram_quota_mb)||
                context.ram_quota_mb<128||context.ram_quota_mb>1000000)
                throw Error('Courier remote lineage needs frozen learner/slot IDs and its own CPU/RAM lease.');
            const physical=context.gpu_pci_address;
            const cuda=context.gpu_cuda_uuid;
            if(context.gpu_pci_addresses!==undefined||
                physical===undefined&&cuda!==undefined||
                physical!==undefined&&(
                    context.gpu_mode!=='exclusive'||!PCI.test(physical)||
                    (process.platform==='win32'
                        ?!enableCuda||!CUDA.test(String(cuda||''))
                        :!enableRocm||cuda!==undefined||
                            !Object.hasOwn(approved,physical))))
                throw Error('Courier remote GPU requires exactly one approved exclusive physical device.');
            if(crypto.createHash('sha256').update(fs.readFileSync(secret)).digest('hex')!==fingerprint)
                throw Error('Courier remote HMAC key was changed after local adapter approval.');
            const output=path.join(cache,context.job_id);
            fs.mkdirSync(output,{recursive:false,mode:0o700});
            if(fs.realpathSync(output)!==output||!fs.statSync(output).isDirectory())
                throw Error('Courier remote scratch directory is not a real directory.');
            return ['-B',path.join(project,'courier_remote_lineage.py'),
                '--host',listenIp,'--port',String(port),'--key-file',secret,
                '--job-id',parameters.learner_job_id,'--slot',String(parameters.slot),
                '--work-dir',output,'--cpu-lease',String(context.cpu_quota),
                '--ram-lease-mb',String(context.ram_quota_mb),
                '--max-shards',String(maxShards),
                ...(physical?[process.platform==='win32'?'--gpu-cuda-uuid':'--gpu-pci',
                    process.platform==='win32'?cuda:physical]:[])];
        },
    };
}
module.exports={createCourierRemoteLineageAdapter};
