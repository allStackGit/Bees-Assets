'use strict';

// One trusted Courier remote PPO lineage per separately leased Hub service.
// The endpoint, private key, executable and cache root are host-installed;
// an experiment supplies only a frozen learner ID and lineage position.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\\.[0-7]$/;
const NVIDIA=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const TAILNET=/^100\.(?:[6-9][0-9]|1[01][0-9]|12[0-7])\.(?:[0-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])\.(?:[0-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])$/;
function strictFile(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value||!fs.statSync(value).isFile())
        throw Error('Courier remote service needs an installed absolute regular file without redirects.');
    return value;
}
function strictDirectory(value) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        fs.realpathSync(value)!==value||!fs.statSync(value).isDirectory())
        throw Error('Courier remote service needs an installed absolute directory without redirects.');
    return value;
}
function createCourierRemoteLineageAdapter({
    projectRoot,pythonPath,keyPath,cacheRoot,listenIp,port,maxShards=64,
    enableCuda=false,enableRocm=false,rocrGpuUuids={},
}) {
    if(typeof enableCuda!=='boolean'||typeof enableRocm!=='boolean'||
        enableCuda&&enableRocm||
        enableCuda&&process.platform!=='win32'||
        enableRocm&&process.platform!=='linux'||
        !rocrGpuUuids||typeof rocrGpuUuids!=='object'||
        Array.isArray(rocrGpuUuids)||
        Object.keys(rocrGpuUuids).length>32||
        !enableRocm&&Object.keys(rocrGpuUuids).length||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid))||
        new Set(Object.values(rocrGpuUuids)).size!==Object.keys(rocrGpuUuids).length)
        throw Error('Remote Courier GPU PPO needs one approved Windows CUDA or Linux ROCm runtime and unique physical GPU identities.');
    const approvedRocr=Object.freeze({...rocrGpuUuids});
    const project=strictDirectory(projectRoot);
    const python=strictFile(pythonPath);
    const secret=strictFile(keyPath);
    const cache=strictDirectory(cacheRoot);
    if(!TAILNET.test(listenIp)||!Number.isSafeInteger(port)||
        port<1024||port>65535||!Number.isSafeInteger(maxShards)||
        maxShards<1||maxShards>64||cache===project||
        cache.startsWith(project+path.sep)||project.startsWith(cache+path.sep))
        throw Error('Courier remote lineage needs a fixed private Tailnet endpoint and separate cache.');
    const stat=fs.statSync(secret);
    if(stat.size<64||stat.size>128||
        process.platform!=='win32'&&(stat.mode&0o077)!==0)
        throw Error('Courier remote service key must be private and contain 32 hexadecimal bytes.');
    const keyText=fs.readFileSync(secret,'ascii').trim();
    if(!/^[0-9a-fA-F]{64}$/.test(keyText))
        throw Error('Courier remote service key has invalid encoding.');
    const fingerprint=crypto.createHash('sha256')
        .update(Buffer.from(keyText,'hex')).digest('hex');
    const capabilitiesFile=path.join(project,'hub-capabilities.json');
    if(fs.realpathSync(capabilitiesFile)!==capabilitiesFile||
        fs.statSync(capabilitiesFile).size>4096)
        throw Error('Remote Courier backend capabilities are missing or redirected.');
    const capabilities=JSON.parse(fs.readFileSync(capabilitiesFile,'utf8'));
    if(capabilities?.schema_version!==1||
        capabilities.courier_remote_lineage_transport!==true||
        ((enableCuda||enableRocm)&&
            capabilities.courier_remote_gpu_optimizer!==true))
        throw Error('Installed remote Courier backend does not advertise CPU/GPU PPO lineage execution.');
    const backend=path.join(project,'courier_remote_lineage.py');
    if(strictFile(backend)!==backend)
        throw Error('Trusted Courier backend has no remote lineage service.');
    const probe=spawnSync(python,['-B','-c',
        'import courier_remote_lineage, courier'],{
        cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    if(probe.status!==0)
        throw Error('Installed Courier Python environment lacks remote lineage support.');
    if(enableCuda||enableRocm) {
        const gpuProbe=spawnSync(python,['-B','-c',enableCuda
            ?'import torch; assert torch.version.cuda and torch.cuda.is_available()'
            :'import torch; assert torch.version.hip and torch.cuda.is_available()'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(gpuProbe.status!==0)
            throw Error('Installed remote Courier GPU PPO requires a working CUDA/ROCm PyTorch runtime.');
    }
    return {
        executable:python,cwd:project,stop_policy:'terminate-group',
        courier_lineage_endpoint:listenIp+':'+port,
        courier_lineage_key_fingerprint:fingerprint,
        courier_lineage_max_shards:maxShards,
        ...(enableCuda?{gpu_execution:'cuda'}:{}),
        ...(enableRocm?{
            rocr_gpu_uuids:approvedRocr,
            gpu_verify_args(_parameters,context) {
                if(!PCI.test(String(context?.gpu_pci_address||''))||
                    context.gpu_pci_addresses!==undefined)
                    throw Error('Remote Courier GPU qualification requires one pinned ROCm device.');
                return ['-B',path.join(__dirname,
                    'experiment_hub_tinybrains_rocm_verify.py'),
                    context.gpu_pci_address];
            },
        }:{}),
        args(parameters,context) {
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(key=>!['learner_job_id','slot','shards'].includes(key))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context.job_id||''))||
                parameters.learner_job_id===context.job_id||
                !Number.isSafeInteger(parameters.slot)||
                parameters.slot<0||parameters.slot>=64||
                !Number.isSafeInteger(parameters.shards)||
                parameters.shards<1||parameters.shards>maxShards||
                !Number.isSafeInteger(context.cpu_quota)||
                context.cpu_quota<1||context.cpu_quota>256||
                !Number.isSafeInteger(context.ram_quota_mb)||
                context.ram_quota_mb<128||context.ram_quota_mb>1000000||
                context.gpu_pci_addresses!==undefined||
                (context.gpu_pci_address!==undefined&&(
                    !PCI.test(context.gpu_pci_address)||
                    context.gpu_mode!=='exclusive'||!(enableCuda||enableRocm)))||
                (context.gpu_cuda_uuid!==undefined&&
                    (!enableCuda||!NVIDIA.test(context.gpu_cuda_uuid)))||
                (context.gpu_pci_address===undefined&&
                    context.gpu_cuda_uuid!==undefined))
                throw Error('Courier remote lineage must match a frozen CPU/RAM and optional exclusive GPU lease.');
            const current=crypto.createHash('sha256')
                .update(Buffer.from(fs.readFileSync(secret,'ascii').trim(),'hex')).digest('hex');
            if(current!==fingerprint)
                throw Error('Installed Courier remote identity changed since local preflight.');
            const directory=path.join(cache,context.job_id);
            if(fs.existsSync(directory))
                throw Error('Courier remote service work directory already exists; reconcile native owner first.');
            return ['-B',backend,'--host',listenIp,'--port',String(port),
                '--key-file',secret,'--job-id',parameters.learner_job_id,
                '--slot',String(parameters.slot),'--work-dir',directory,
                '--cpu-lease',String(context.cpu_quota),
                '--ram-lease-mb',String(context.ram_quota_mb),
                '--max-shards',String(parameters.shards),
                ...(context.gpu_pci_address!==undefined
                    ?enableCuda
                        ?['--gpu-cuda-uuid',context.gpu_cuda_uuid]
                        :['--gpu-pci',context.gpu_pci_address]
                    :[])];
        },
    };
}
module.exports={createCourierRemoteLineageAdapter};
