'use strict';

// OS-native control of the ONE already-installed, independently budgeted
// native Bees evaluator. The Hub supplies only an action + one-use identity,
// never executable paths, arbitrary unit names or commands. Install/upgrade
// remains machine-local and outside the training resource planner.
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const os=require('node:os');
const run=promisify(execFile);
const ID=/^[a-z][a-z0-9_-]{0,47}$/;

function evaluatorServiceName(machineId,platform=os.platform()) {
    if(!ID.test(String(machineId||'')))throw Error('Invalid installed evaluator machine identity.');
    if(platform==='linux')
        return 'bees-experiment-hub-bees_native-'+machineId+'-evaluator.timer';
    if(platform==='win32')
        return 'Bees-ExperimentHub-bees_native-'+machineId+'-evaluator';
    throw Error('Native Bees evaluator service requires Linux or Windows.');
}

async function invoke(file,args) {
    try {
        return await run(file,args,{timeout:15000,maxBuffer:8192,
            windowsHide:true,encoding:'utf8'});
    }catch(error) {
        if(error.killed||error.code==='ETIMEDOUT')
            throw Error('Installed evaluator service manager timed out.');
        // Query commands return a nonzero exit for a stopped service.
        // Retain their bounded native output for explicit state parsing.
        if(typeof error.stdout==='string'||typeof error.stderr==='string')
            return {stdout:error.stdout||'',stderr:error.stderr||'',code:error.code};
        throw error;
    }
}

async function inspect(machineId,platform=os.platform()) {
    const name=evaluatorServiceName(machineId,platform);
    if(platform==='linux') {
        const loaded=await invoke('systemctl',['--user','show','-p','LoadState',
            '--value',name]);
        if(loaded.stdout.trim()!=='loaded')
            throw Error('Native Bees evaluator timer is not installed for this machine.');
        const status=await invoke('systemctl',['--user','is-active',name]);
        const state=status.stdout.trim();
        if(state==='active')return 'active';
        if(state==='inactive')return 'inactive';
        if(state==='activating')return 'activating';
        if(state==='deactivating')return 'deactivating';
        if(state==='failed')return 'failed';
        throw Error('Unrecognized installed native evaluator timer state.');
    }
    const status=await invoke('sc.exe',['query',name]);
    const match=/STATE\s*:\s*[0-9]+\s+(RUNNING|STOPPED|START_PENDING|STOP_PENDING|PAUSED)/i.exec(status.stdout);
    if(!match)throw Error('Native Bees evaluator Windows service is unavailable.');
    return {RUNNING:'active',STOPPED:'inactive',START_PENDING:'activating',
        STOP_PENDING:'deactivating',PAUSED:'failed'}[match[1].toUpperCase()];
}

async function control(command,machineId,platform=os.platform()) {
    if(!command||!['start','stop','inspect'].includes(command.action))
        throw Error('Invalid native evaluator service operation.');
    const name=evaluatorServiceName(machineId,platform);
    const before=await inspect(machineId,platform);
    if(command.action==='start'&&before==='inactive') {
        if(platform==='linux')await invoke('systemctl',['--user','start',name]);
        else await invoke('sc.exe',['start',name]);
    }else if(command.action==='stop'&&
        ['active','activating','deactivating'].includes(before)) {
        if(platform==='linux')await invoke('systemctl',['--user','stop',name]);
        else await invoke('sc.exe',['stop',name]);
    }
    return inspect(machineId,platform);
}
module.exports={control,evaluatorServiceName};
