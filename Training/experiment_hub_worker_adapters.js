'use strict';

// Trusted worker adapter map for one enrolled Experiment Hub machine.
// The installer supplies only absolute local runtime/secret paths; the Hub
// never supplies executable paths, network listener ports, or HMAC identities.
// Both projects share the machine's native CPU/RAM ledger, not their secrets.
const path=require('node:path');
const {createTinyBrainsRemoteRolloutAdapter}=
    require('./experiment_hub_tinybrains_remote_adapter');
const {createCourierRemoteLineageAdapter}=
    require('./experiment_hub_courier_remote_adapter');

function required(name) {
    const value=process.env[name];
    if(typeof value!=='string'||!value||/[\r\n]/.test(value))
        throw Error('Configure '+name+' in the private worker environment.');
    return value;
}
function enabled(name) {
    const value=process.env.EXPERIMENT_HUB_WORKER_PROJECTS||'';
    return value.split(',').includes(name);
}
function port(name,defaultValue) {
    const raw=process.env[name];
    if(raw===undefined)return defaultValue;
    const value=Number(raw);
    if(!Number.isSafeInteger(value)||value<1024||value>65535)
        throw Error(name+' must be a fixed, private unprivileged port.');
    return value;
}
function listenerPorts(prefix,defaults) {
    const values=defaults.map((fallback,i)=>port(prefix+'_PORT_'+i,fallback));
    if(new Set(values).size!==values.length)
        throw Error(prefix+' listeners must use unique ports.');
    return Object.fromEntries(values.map((value,index)=>[index,value]));
}
const projects=(process.env.EXPERIMENT_HUB_WORKER_PROJECTS||'').split(',').filter(Boolean);
if(projects.length>2||
    projects.some(p=>!['tinybrains','courier'].includes(p))||
    new Set(projects).size!==projects.length)
    throw Error('Installed simulation selection must contain only TinyBrains or Courier.');
const adapters={};
if(enabled('tinybrains')) {
    const slots=listenerPorts('EXPERIMENT_HUB_TINYBRAINS',[12134,12135]);
    adapters.tinybrains_rollout=createTinyBrainsRemoteRolloutAdapter({
        projectRoot:required('EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT'),
        pythonPath:required('EXPERIMENT_HUB_TINYBRAINS_PYTHON'),
        keyPath:required('EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY'),
        cacheRoot:required('EXPERIMENT_HUB_TINYBRAINS_CACHE'),
        listenIp:required('EXPERIMENT_HUB_TAILNET_IP'),
        port:slots[0],slotPorts:slots,
        threads:Number(required('EXPERIMENT_HUB_TINYBRAINS_THREAD_LIMIT')),
        enableCuda:false,enableRocm:false,
    });
}
if(enabled('courier')) {
    const slots=listenerPorts('EXPERIMENT_HUB_COURIER',[13134,13135]);
    adapters.courier_lineage=createCourierRemoteLineageAdapter({
        projectRoot:required('EXPERIMENT_HUB_COURIER_PROJECT_ROOT'),
        pythonPath:required('EXPERIMENT_HUB_COURIER_PYTHON'),
        keyPath:required('EXPERIMENT_HUB_COURIER_ROLLOUT_KEY'),
        cacheRoot:required('EXPERIMENT_HUB_COURIER_CACHE'),
        listenIp:required('EXPERIMENT_HUB_TAILNET_IP'),
        port:slots[0],slotPorts:slots,
        enableCuda:false,enableRocm:false,
    });
}
module.exports=adapters;
