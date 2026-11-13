'use strict';

// Trusted machine-local rollout service. This is a separate Hub job with
// its own CPU/RAM/GPU lease, not an unaccounted worker of the learner's lease.
// The experiment may choose only a learner job ID; the executable, binding,
// port, thread budget, cache and secret are installed by the host operator.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const CUDA=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const TAILNET=/^100\.\d{1,3}\.\d{1,3}\.\d{1,3}$/;

function directory(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value||
        !fs.statSync(value).isDirectory())
        throw Error('Remote rollout service requires an existing absolute non-symlink directory.');
    return value;
}

function keyFile(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value)
        throw Error('Use an existing absolute, non-symlink rollout key path.');
    const stat=fs.statSync(value);
    if(!stat.isFile()||stat.size<32||stat.size>4096||
        (process.platform!=='win32'&&(stat.mode&0o077)!==0))
        throw Error('Rollout HMAC key must be a private regular file containing 32–4096 bytes.');
    return value;
}

function tailnetIp(ip) {
    if(!TAILNET.test(String(ip||'')))
        throw Error('Listen only on an explicit Tailscale IPv4 address.');
    const parts=ip.split('.').map(Number);
    if(parts[1]<64||parts[1]>127||parts.slice(2).some(n=>n>255))
        throw Error('Rollout server must bind inside the Tailscale IPv4 network.');
    return ip;
}

function createTinyBrainsRemoteRolloutAdapter({
    projectRoot,pythonPath,keyPath,cacheRoot,listenIp,port,threads,
    enableCuda=false,enableRocm=false,rocrGpuUuids={},
}) {
    if(typeof enableCuda!=='boolean'||typeof enableRocm!=='boolean'||
        (enableCuda&&enableRocm)||
        (enableCuda&&process.platform!=='win32')||
        (enableRocm&&process.platform!=='linux'))
        throw Error('Remote GPU rollout needs exactly one locally opted-in Windows CUDA or Linux ROCm runtime.');
    if(!rocrGpuUuids||typeof rocrGpuUuids!=='object'||
        Array.isArray(rocrGpuUuids)||
        Object.keys(rocrGpuUuids).length>32||
        (!enableRocm&&Object.keys(rocrGpuUuids).length)||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid))||
        new Set(Object.values(rocrGpuUuids)).size!==Object.keys(rocrGpuUuids).length)
        throw Error('Remote ROCm selector must bind distinct locally approved GPU UUIDs to canonical PCI addresses.');
    const approvedRocr=Object.freeze({...rocrGpuUuids});
    const project=directory(projectRoot);
    const cache=directory(cacheRoot);
    const secret=keyFile(keyPath);
    const fingerprint=crypto.createHash('sha256').update(fs.readFileSync(secret)).digest('hex');
    const host=tailnetIp(listenIp);
    if(!Number.isSafeInteger(port)||port<1024||port>65535||
        !Number.isSafeInteger(threads)||threads<1||threads>256)
        throw Error('Remote rollout listener needs an approved non-privileged port and CPU thread count.');
    if(typeof pythonPath!=='string'||!path.isAbsolute(pythonPath)||
        !fs.statSync(pythonPath).isFile()||fs.realpathSync(pythonPath)!==pythonPath)
        throw Error('Remote rollout Python must be an installed regular file.');
    const probe=spawnSync(pythonPath,['-B','-c',
        'import ants_mapoca.remote_rollout'],{
        cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    if(probe.status!==0)
        throw Error('Installed TinyBrains worker lacks authenticated remote rollout support.');
    // Older installed workers remain runnable with their established fixed
    // native engine concurrency until they provide the new implementation.
    const poolProbe=spawnSync(pythonPath,['-B','-c',
        'import ants_mapoca.engine_pool'],{
        cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    const supportsAutoPool=poolProbe.status===0;
    if(enableCuda||enableRocm) {
        const check=spawnSync(pythonPath,['-B','-c',enableCuda
            ?'import torch; assert torch.version.cuda and torch.cuda.is_available()'
            :'import torch, ants_mapoca.remote_rollout as r; assert torch.version.hip and torch.cuda.is_available() and hasattr(r, "linux_rocm_pci_identity")'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(check.status!==0)
            throw Error('Remote GPU collection requires the installed verified CUDA/ROCm TinyBrains backend.');
    }
    return {
        executable:pythonPath,cwd:project,stop_policy:'terminate-group',
        tinybrains_rollout_endpoint:host+':'+port,
        tinybrains_rollout_key_fingerprint:fingerprint,
        tinybrains_rollout_thread_limit:threads,
        ...(enableCuda?{gpu_execution:'cuda'}:{}),
        ...(enableRocm?{
            rocr_gpu_uuids:approvedRocr,
            // Executed only through the Hub's qualified, device-filtered
            // Linux preflight (and again as ExecStartPre). This verifies
            // the physical HIP PCI identity without starting a collector.
            gpu_verify_args(_parameters,context) {
                if(!PCI.test(String(context?.gpu_pci_address||''))||
                    context.gpu_pci_addresses!==undefined)
                    throw Error('Remote ROCm verification requires one frozen physical GPU.');
                return ['-B',path.join(__dirname,
                    'experiment_hub_tinybrains_rocm_verify.py'),
                    context.gpu_pci_address];
            },
        }:{}),
        args(parameters,context) {
            const desired=parameters?.threads??Math.min(threads,context.cpu_quota);
            if(supportsAutoPool&&(!Number.isSafeInteger(context.ram_quota_mb)||
                context.ram_quota_mb<128||context.ram_quota_mb>1000000))
                throw Error('Automatic remote simulation requires its original RAM lease.');
            if(!Number.isSafeInteger(context.cpu_quota)||context.cpu_quota<1||
                !Number.isSafeInteger(desired)||desired<1||
                desired>threads||desired>context.cpu_quota)
                throw Error('Remote collector threads must fit both its CPU lease and the local approved thread limit.');
            if(crypto.createHash('sha256').update(fs.readFileSync(secret)).digest('hex')!==fingerprint)
                throw Error('Installed rollout HMAC key differs from its advertised physical worker identity.');
            const hasGpu=Boolean(context.gpu_pci_address);
            const cudaGpu=context.gpu_cuda_uuid!==undefined;
            const rocmGpu=hasGpu&&!cudaGpu;
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(key=>!['learner_job_id','threads'].includes(key))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context.job_id||''))||
                parameters.learner_job_id===context.job_id||
                (cudaGpu&&(!enableCuda||!CUDA.test(String(context.gpu_cuda_uuid))||
                    !hasGpu||context.gpu_mode!=='exclusive'))||
                (rocmGpu&&(!enableRocm||!PCI.test(context.gpu_pci_address)||
                    context.gpu_mode!=='exclusive'))||
                (!hasGpu&&(cudaGpu||context.gpu_pci_addresses!==undefined))||
                (hasGpu&&context.gpu_pci_addresses!==undefined))
                throw Error('Remote rollout needs a distinct learner ID and an approved CPU or single exclusive GPU lease.');
            return ['-B','-m','ants_mapoca.remote_rollout',
                '--listen',host,'--port',String(port),
                '--job-id',parameters.learner_job_id,
                '--threads',String(desired),'--key-file',secret,
                '--cache-dir',cache,
                ...(supportsAutoPool?['--auto-env-pool',
                    '--ram-budget-mb',String(context.ram_quota_mb)]:[]),
                ...(cudaGpu
                    ?['--device','cuda','--cuda-uuid',context.gpu_cuda_uuid]
                    :rocmGpu
                        ?['--device','cuda','--rocm-pci',context.gpu_pci_address]
                        :['--device','cpu'])];
        },
    };
}

module.exports={createTinyBrainsRemoteRolloutAdapter};
