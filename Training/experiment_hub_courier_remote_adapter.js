'use strict';

// One trusted Courier remote PPO lineage per separately leased Hub service.
// The endpoint, private key, executable and cache root are host-installed;
// an experiment supplies only a frozen learner ID and lineage position.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
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
}) {
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
    const backend=path.join(project,'courier_remote_lineage.py');
    if(strictFile(backend)!==backend)
        throw Error('Trusted Courier backend has no remote lineage service.');
    const probe=spawnSync(python,['-B','-c',
        'import courier_remote_lineage, courier'],{
        cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    if(probe.status!==0)
        throw Error('Installed Courier Python environment lacks remote lineage support.');
    return {
        executable:python,cwd:project,stop_policy:'terminate-group',
        courier_lineage_endpoint:listenIp+':'+port,
        courier_lineage_key_fingerprint:fingerprint,
        courier_lineage_max_shards:maxShards,
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
                context.gpu_pci_address!==undefined||
                context.gpu_pci_addresses!==undefined||
                context.gpu_cuda_uuid!==undefined)
                throw Error('Courier remote lineage must match a frozen CPU-only lease and immutable learner/slot.');
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
                '--max-shards',String(parameters.shards)];
        },
    };
}
module.exports={createCourierRemoteLineageAdapter};
