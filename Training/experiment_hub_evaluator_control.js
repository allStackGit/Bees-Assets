'use strict';

// OS-native control of the ONE already-installed, independently budgeted
// native Bees evaluator. The Hub supplies only an action + one-use identity,
// never executable paths, arbitrary unit names or commands. Install/upgrade
// remains machine-local and outside the training resource planner.
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const os=require('node:os');
const crypto=require('node:crypto');
const run=promisify(execFile);
const ID=/^[a-z][a-z0-9_-]{0,47}$/;

function evaluatorServiceName(machineId,platform=os.platform()) {
    if(!ID.test(String(machineId||'')))throw Error('Invalid installed evaluator machine identity.');
    if(platform==='linux')
        return 'bees-experiment-hub-bees_native-'+machineId+'-evaluator.timer';
    if(platform==='win32') {
        // The installed Windows launcher only permits ASCII alphanumeric
        // names and hyphens. Prefix + bounded slug + machine fingerprint
        // also keeps distinct machine IDs with '-' and '_' unambiguous.
        const slug=machineId.replaceAll('_','-').slice(0,24);
        const digest=crypto.createHash('sha256').update(machineId)
            .digest('hex').slice(0,8);
        return 'Bees-ExperimentHub-native-'+slug+'-'+digest;
    }
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
    if(!command||!['start','stop','restart','inspect'].includes(command.action))
        throw Error('Invalid native evaluator service operation.');
    const name=evaluatorServiceName(machineId,platform);
    const before=await inspect(machineId,platform);
    if(command.action==='restart') {
        if(platform==='linux') {
            await invoke('systemctl',['--user','stop',name,
                name.replace(/\.timer$/,'.service')]);
            if(await inspect(machineId,platform)!=='inactive')
                throw Error('Native evaluator timer did not stop before restart.');
            await invoke('systemctl',['--user','start',name]);
        }else {
            if(['active','activating','deactivating'].includes(before))
                await invoke('sc.exe',['stop',name]);
            // Windows SCM Stop is asynchronous. One bounded wait observes
            // the actual service state; it never launches a second copy
            // while the original evaluator Job Object is still closing.
            let stopped=false;
            for(let attempt=0;attempt<30;attempt++) {
                if(await inspect(machineId,platform)==='inactive') {
                    stopped=true;break;
                }
                await new Promise(resolve=>setTimeout(resolve,500));
            }
            if(!stopped)
                throw Error('Evaluator Stop still pending; inspect the original Windows service.');
            await invoke('sc.exe',['start',name]);
        }
    }else if(command.action==='start'&&before==='inactive') {
        if(platform==='linux')await invoke('systemctl',['--user','start',name]);
        else await invoke('sc.exe',['start',name]);
    }else if(command.action==='stop') {
        if(platform==='linux') {
            // The timer can be inactive while its previously launched
            // oneshot still runs. Always stop both installed OS units.
            await invoke('systemctl',['--user','stop',name,
                name.replace(/\.timer$/,'.service')]);
        }else if(['active','activating','deactivating'].includes(before))
            await invoke('sc.exe',['stop',name]);
    }
    const observed=await inspect(machineId,platform);
    if(['start','restart'].includes(command.action)&&
        !['active','activating'].includes(observed))
        throw Error('Native evaluator OS service did not accept Start.');
    if(command.action==='stop'&&
        !['inactive','deactivating'].includes(observed))
        throw Error('Native evaluator OS service did not accept Stop.');
    return observed;
}
module.exports={control,evaluatorServiceName};
