'use strict';

// Fixed one-lineage Courier service. The Hub owns the CPU/RAM lease and
// service lifecycle; commands or paths from queue parameters never select
// executable code, the private key, listening address, or artifact root.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const TAILNET=/^100\.\d{1,3}\.\d{1,3}\.\d{1,3}$/;

function privateKey(filename) {
    if(typeof filename!=='string'||!path.isAbsolute(filename)||
        fs.realpathSync(filename)!==filename)throw Error('Courier remote key must be an installed absolute file.');
    const info=fs.statSync(filename);
    if(!info.isFile()||info.size<64||info.size>128||
        process.platform!=='win32'&&(info.mode&0o077)!==0)
        throw Error('Courier remote authentication key must be private.');
    const bytes=fs.readFileSync(filename,'utf8').trim();
    if(!/^[0-9a-fA-F]{64}$/.test(bytes))
        throw Error('Courier remote key must contain exactly 32 hexadecimal bytes.');
    return {filename,fingerprint:crypto.createHash('sha256')
        .update(Buffer.from(bytes,'hex')).digest('hex')};
}

function endpoint(ip,port) {
    if(!TAILNET.test(String(ip||''))||!Number.isSafeInteger(port)||
        port<1024||port>65535)throw Error('Courier remote service requires a Tailnet IPv4 and nonprivileged port.');
    const parts=ip.split('.').map(Number);
    if(parts[1]<64||parts[1]>127||parts.slice(2).some(n=>n>255))
        throw Error('Courier remote endpoint is outside the Tailscale IPv4 range.');
    return ip+':'+port;
}

function createCourierRemoteAdapter({
    projectRoot,pythonPath,artifactRoot,keyPath,listenIp,port,maxShards=64,
}) {
    for(const [name,source,isFile] of [
        ['Courier installation',projectRoot,false],['Courier Python',pythonPath,true],
        ['Courier artifacts',artifactRoot,false]]) {
        if(typeof source!=='string'||!path.isAbsolute(source)||
            fs.realpathSync(source)!==source||
            (isFile?!fs.statSync(source).isFile():!fs.statSync(source).isDirectory()))
            throw Error(name+' must be an installed, non-symlink absolute path.');
    }
    const workerScript=path.join(projectRoot,'courier_remote_lineage.py');
    if(!fs.statSync(workerScript).isFile()||fs.realpathSync(workerScript)!==workerScript||
        !Number.isSafeInteger(maxShards)||maxShards<1||maxShards>64)
        throw Error('Installed Courier remote backend or map limit is invalid.');
    const address=endpoint(listenIp,port),secret=privateKey(keyPath);
    const probe=spawnSync(pythonPath,['-B','-c','import courier_remote_lineage'],{
        cwd:projectRoot,timeout:15000,encoding:'utf8',windowsHide:true,
    });
    if(probe.status!==0)throw Error('Courier remote service backend import failed.');
    return {
        executable:pythonPath,cwd:projectRoot,stop_policy:'terminate-group',
        courier_remote_endpoint:address,
        courier_remote_key_fingerprint:secret.fingerprint,
        courier_remote_max_shards:maxShards,
        args(parameters,context) {
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(k=>!['learner_job_id','slot'].includes(k))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context.job_id||''))||
                parameters.learner_job_id===context.job_id||
                !Number.isSafeInteger(parameters.slot)||parameters.slot<1||
                parameters.slot>16||
                !Number.isSafeInteger(context.cpu_quota)||context.cpu_quota<1||
                context.cpu_quota>256||
                !Number.isSafeInteger(context.ram_quota_mb)||
                context.ram_quota_mb<128||context.ram_quota_mb>1000000||
                context.gpu_pci_address!==undefined||
                context.gpu_pci_addresses!==undefined)
                throw Error('Remote Courier PPO service requires one approved CPU/RAM-only lineage lease.');
            if(privateKey(keyPath).fingerprint!==secret.fingerprint)
                throw Error('Remote Courier identity changed after machine enrollment.');
            const folder=path.join(artifactRoot,context.job_id);
            fs.mkdirSync(folder,{mode:0o700,recursive:false});
            return ['-B',workerScript,'--host',listenIp,'--port',String(port),
                '--key-file',keyPath,'--job-id',parameters.learner_job_id,
                '--slot',String(parameters.slot),'--work-dir',folder,
                '--cpu-lease',String(context.cpu_quota),
                '--ram-lease-mb',String(context.ram_quota_mb),
                '--max-shards',String(maxShards)];
        },
    };
}
module.exports={createCourierRemoteAdapter};
