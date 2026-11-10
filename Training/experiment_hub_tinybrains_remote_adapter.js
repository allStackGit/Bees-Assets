'use strict';

// Trusted machine-local CPU rollout service. This is a separate Hub job with
// its own CPU/RAM lease, not an unaccounted worker of the learner's lease.
// The experiment may choose only a learner job ID; the executable, binding,
// port, thread budget, cache and secret are installed by the host operator.
const fs=require('node:fs');
const path=require('node:path');
const {spawnSync}=require('node:child_process');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
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
}) {
    const project=directory(projectRoot);
    const cache=directory(cacheRoot);
    const secret=keyFile(keyPath);
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
    return {
        executable:pythonPath,cwd:project,stop_policy:'terminate-group',
        args(parameters,context) {
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).length!==1||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(context.job_id||''))||
                parameters.learner_job_id===context.job_id)
                throw Error('Remote CPU worker requires one immutable, different TinyBrains learner job ID.');
            return ['-B','-m','ants_mapoca.remote_rollout',
                '--listen',host,'--port',String(port),
                '--job-id',parameters.learner_job_id,
                '--threads',String(threads),'--key-file',secret,
                '--cache-dir',cache];
        },
    };
}

module.exports={createTinyBrainsRemoteRolloutAdapter};
