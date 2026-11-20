'use strict';

// Optional local, authenticated SSH forwarding owned by the Hub agent
// process. No admin or agent HTTP port is exposed outside loopback.
// OS service recovery owns restarts; this module never retries a lost
// tunnel indefinitely or trusts keys supplied by queued experiments.
const fs=require('node:fs');
const net=require('node:net');
const path=require('node:path');
const {spawn}=require('node:child_process');

const TAILNET=/^([a-zA-Z_][a-zA-Z0-9_.-]{0,63})@(100\.\d{1,3}\.\d{1,3}\.\d{1,3})$/;
function realFile(input,label) {
    if(typeof input!=='string'||!path.isAbsolute(input))
        throw Error(label+' needs an absolute local file.');
    const stat=fs.lstatSync(input);
    if(!stat.isFile()||stat.isSymbolicLink()||!stat.size)
        throw Error(label+' needs an installed regular private file.');
    if(process.platform!=='win32'&&(stat.mode&0o077)!==0)
        throw Error(label+' must be unreadable to group and other users.');
    return input;
}
function approvedTarget(value) {
    const match=TAILNET.exec(value||'');
    if(!match)throw Error('SSH tunnel requires user@Tailscale-IPv4.');
    const parts=match[2].split('.').map(Number);
    if(parts[1]<64||parts[1]>127||parts.slice(2).some(n=>n>255))
        throw Error('SSH tunnel must use a private tailnet IPv4 peer.');
    return value;
}
function checkListening(port) {
    return new Promise(resolve=>{
        const connection=net.connect({host:'127.0.0.1',port});
        connection.setTimeout(500);
        connection.once('connect',()=>{connection.destroy();resolve(true);});
        connection.once('timeout',()=>{connection.destroy();resolve(false);});
        connection.once('error',()=>{connection.destroy();resolve(false);});
    });
}
async function withAgentTunnel(run,env=process.env) {
    if(!env.EXPERIMENT_HUB_SSH_TARGET)return run();
    const target=approvedTarget(env.EXPERIMENT_HUB_SSH_TARGET);
    const key=realFile(env.EXPERIMENT_HUB_SSH_KEY,'SSH private key');
    const hosts=realFile(env.EXPERIMENT_HUB_SSH_KNOWN_HOSTS,'SSH verified host keys');
    if(env.EXPERIMENT_HUB_URL!=='http://127.0.0.1:17149')
        throw Error('Tunnel-backed agent must use local-only HTTP port 17149.');
    const args=['-N','-T','-o','BatchMode=yes',
        '-o','IdentitiesOnly=yes','-o','ExitOnForwardFailure=yes',
        '-o','StrictHostKeyChecking=yes','-o','ServerAliveInterval=15',
        '-o','ServerAliveCountMax=3','-o','ConnectTimeout=10',
        '-o','UserKnownHostsFile='+hosts,'-i',key,
        '-L','127.0.0.1:17149:127.0.0.1:7149',target];
    const child=spawn('ssh',args,{stdio:'ignore',windowsHide:true});
    let ended=false,reason='SSH tunnel closed';
    child.once('error',error=>{ended=true;reason=error.message;});
    child.once('exit',(code,signal)=>{
        ended=true;reason='SSH tunnel exited ('+(signal||code)+')';
    });
    process.once('exit',()=>{if(!ended)child.kill();});
    // Wait only for the initial local listening socket. Losing the
    // transport thereafter is a service failure, never a second agent.
    let connected=false;
    for(let attempt=0;attempt<40&&!ended;attempt++) {
        if(await checkListening(17149)){connected=true;break;}
        await new Promise(resolve=>setTimeout(resolve,250));
    }
    if(!connected||ended) {
        if(!ended)child.kill();
        throw Error('Private Hub tunnel was not established: '+reason);
    }
    let shuttingDown=false;
    child.once('exit',()=>{
        if(shuttingDown)return;
        console.error('[experiment-hub-agent] Private SSH forward lost; service restart required.');
        process.exit(1);
    });
    try {
        return await run();
    }finally {
        // One-shot agents must release their port, too. No separate
        // unmanaged SSH daemon or logon task remains after termination.
        shuttingDown=true;
        if(!ended)child.kill();
    }
}
module.exports={withAgentTunnel};
