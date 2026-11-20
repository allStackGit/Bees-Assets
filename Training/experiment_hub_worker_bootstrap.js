'use strict';

// A signed-in worker's single bootstrap credential is its Tailscale node
// identity, verified by Halifax's local tailscaled daemon, not an IP header.
// Only nodes owned by Halifax's own Tailscale user may self-enroll.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const os=require('node:os');
const net=require('node:net');
const {spawnSync}=require('node:child_process');
const ID=/^[a-z][a-z0-9_-]{0,47}$/;

function tailscale(args) {
    const result=spawnSync('tailscale',args,{encoding:'utf8',timeout:8000,
        maxBuffer:1024*1024,windowsHide:true});
    if(result.error||result.status!==0)
        throw Error('Tailscale identity verification unavailable on Halifax.');
    try{return JSON.parse(result.stdout);}
    catch{throw Error('Tailscale returned an invalid identity report.');}
}
function authenticatedPeer(ip) {
    if(net.isIP(ip)!==4||!ip.startsWith('100.'))
        throw Error('Bootstrap requires a direct Tailscale IPv4 connection.');
    const self=tailscale(['status','--json']);
    const who=tailscale(['whois','--json',ip]);
    const owner=String(self.Self?.UserID??'');
    const peerOwner=String(who.UserProfile?.ID??'');
    const node=who.Node;
    if(!owner||owner==='0'||!peerOwner||owner!==peerOwner||
        !node||!Number.isSafeInteger(Number(node.ID))||
        Number(node.ID)<=0||!node.Name||!self.Self?.Online)
        throw Error('Worker must sign in to Tailscale as the Halifax owner.');
    const name=String(node.ComputedName||node.Name).split('.')[0].toLowerCase();
    const id=name.replace(/[^a-z0-9_-]/g,'-').slice(0,48);
    if(!ID.test(id))throw Error('Tailscale worker name is not a valid machine ID.');
    return {machineId:id,nodeId:String(node.ID)};
}
function keyFile(root,name) {
    const expected=name==='tinybrains'
        ?path.join(root,'private-rollouts','tinybrains','rollout.key')
        :path.join(root,'private-rollouts','courier.key');
    const selected=name==='tinybrains'
        ?process.env.EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY||
            (process.env.EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY
                ?path.join(process.env.EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY,'rollout.key'):expected)
        :process.env.EXPERIMENT_HUB_COURIER_REMOTE_KEY_PATH||expected;
    if(!path.isAbsolute(selected))throw Error('Invalid private rollout key location.');
    fs.mkdirSync(path.dirname(selected),{recursive:true,mode:0o700});
    // Do not silently replace any learner's existing secret. Generate once
    // only when no existing key is present at the configured canonical path.
    if(!fs.existsSync(selected)) {
        try {fs.writeFileSync(selected,crypto.randomBytes(32).toString('hex')+'\n',
            {flag:'wx',mode:0o600});}
        catch(error){if(error.code!=='EEXIST')throw error;}
    }
    const stat=fs.lstatSync(selected);
    if(!stat.isFile()||stat.isSymbolicLink()||(stat.mode&0o077)!==0)
        throw Error('Private rollout key permissions are unsafe.');
    const key=fs.readFileSync(selected,'utf8').trim();
    if(!/^[0-9a-f]{64}$/.test(key))
        throw Error('Existing learner rollout key is invalid.');
    return key;
}
function bootstrap(hub,root,ip,request) {
    if(!request||typeof request!=='object'||Array.isArray(request)||
        Object.keys(request).some(k=>!['projects','cpus','ram_mb'].includes(k)))
        throw Error('Invalid bootstrap request.');
    const projects=request.projects;
    if(!Array.isArray(projects)||projects.length>2||
        projects.some(p=>!['tinybrains','courier'].includes(p))||
        new Set(projects).size!==projects.length)
        throw Error('Unknown worker project selection.');
    const cpus=request.cpus,ram=request.ram_mb;
    if(!Number.isInteger(cpus)||cpus<1||cpus>4096||
        !Number.isInteger(ram)||ram<128||ram>100000000)
        throw Error('Invalid reported machine capacity.');
    const peer=authenticatedPeer(ip);
    const secrets=Object.fromEntries(projects.map(p=>[p,keyFile(root,p)]));
    let registered=hub.state.machines.find(m=>m.id===peer.machineId);
    if(registered) {
        // Re-running an interrupted first install is allowed only from the
        // SAME Tailscale node, before its first heartbeat or native job.
        // Never rotate an existing active or legacy machine's identity.
        if((registered.tailnet_node_id&&
             registered.tailnet_node_id!==peer.nodeId)||
            hub.agentReports.has(peer.machineId)||
            hub.state.jobs.some(j=>j.assigned_machine_id===peer.machineId)||
            hub.state.reservations.some(r=>r.machine_id===peer.machineId)||
            (registered.agent_key_sha256&&!registered.tailnet_node_id))
            throw Error('Machine already enrolled; use its existing installation.');
    } else {
        hub.addMachine({id:peer.machineId,name:peer.machineId,cpus,ram_mb:ram,gpus:[]});
    }
    // Bind this machine ID to its immutable tailnet node ID, avoiding a
    // second machine with the same hostname claiming the existing token.
    const next=JSON.parse(JSON.stringify(hub.state));
    registered=next.machines.find(m=>m.id===peer.machineId);
    registered.tailnet_node_id=peer.nodeId;
    registered.bootstrap_projects=projects;
    hub.commit(next);
    const enrolled=hub.enrollMachine({
        machine_id:peer.machineId,confirm_enroll:true,
        confirm_rotate:Boolean(registered.agent_key_sha256),
    });
    return {machine_id:peer.machineId,agent_token:enrolled.agent_token,
        rollout_keys:secrets};
}
module.exports={bootstrap,authenticatedPeer};
