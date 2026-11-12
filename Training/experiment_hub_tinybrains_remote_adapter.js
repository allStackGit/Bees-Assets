'use strict';

// Trusted machine-local CPU rollout service. This is a separate Hub job with
// its own CPU/RAM lease, not an unaccounted worker of the learner's lease.
// The experiment may choose only a learner job ID; the executable, binding,
// port, thread budget, cache and secret are installed by the host operator.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const CUDA=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
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
    enableCuda=false,
}) {
    if(typeof enableCuda!=='boolean'||enableCuda&&process.platform!=='win32')
        throw Error('Remote GPU rollout requires explicit Windows CUDA opt-in.');
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
    if(enableCuda) {
        const check=spawnSync(pythonPath,['-B','-c',
            'import torch; assert torch.version.cuda and torch.cuda.is_available()'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(check.status!==0)
            throw Error('Remote CUDA collection requires a locally working NVIDIA PyTorch environment.');
    }
    return {
        executable:pythonPath,cwd:project,stop_policy:'terminate-group',
        tinybrains_rollout_endpoint:host+':'+port,
        tinybrains_rollout_key_fingerprint:fingerprint,
        tinybrains_rollout_thread_limit:threads,
        ...(enableCuda?{gpu_execution:'cuda'}:{}),
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
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(key=>!['learner_job_id','threads'].includes(key))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context.job_id||''))||
                parameters.learner_job_id===context.job_id||
                (context.gpu_cuda_uuid!==undefined&&
                    (!enableCuda||!CUDA.test(String(context.gpu_cuda_uuid))||
                    context.gpu_mode!=='exclusive'||!context.gpu_pci_address))||
                (context.gpu_cuda_uuid===undefined&&Boolean(context.gpu_pci_address)))
                throw Error('Remote CPU worker requires one immutable, different TinyBrains learner job ID.');
            return ['-B','-m','ants_mapoca.remote_rollout',
                '--listen',host,'--port',String(port),
                '--job-id',parameters.learner_job_id,
                '--threads',String(desired),'--key-file',secret,
                '--cache-dir',cache,
                ...(supportsAutoPool?['--auto-env-pool',
                    '--ram-budget-mb',String(context.ram_quota_mb)]:[]),
                ...(context.gpu_cuda_uuid
                    ?['--device','cuda','--cuda-uuid',context.gpu_cuda_uuid]
                    :['--device','cpu'])];
        },
    };
}

module.exports={createTinyBrainsRemoteRolloutAdapter};
