'use strict';

// Local-only administrative actions. User systemd owns both processes; the
// coordinator may request a restart but must never be its own relauncher.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {execFile,spawn}=require('node:child_process');
const {promisify}=require('node:util');
const localEvaluators=require('./experiment_hub_local_evaluators');
const courierEvaluators=require('./experiment_hub_local_courier_evaluators');
const run=promisify(execFile);

const SYSTEMCTL='/usr/bin/systemctl';
const UNITS=Object.freeze({
    coordinator:'bees-experiment-hub-standalone.service',
    agent:'bees-experiment-hub-agent.service',
});
const BUSY=new Set([
    'start_requested','launch_requested','running','stop_requested','unverified',
]);

function reject(message,status=409) {
    const error=Error(message);
    error.status=status;
    throw error;
}
function activeJobs(hub) {
    return hub.snapshot().jobs.filter(job=>BUSY.has(job.status));
}
const MACHINE_ID=/^[a-z][a-z0-9_-]{0,47}$/;
// Only narrow the restart barrier to local jobs when the installed agent's
// actual EnvironmentFile matches its private enrollment. Unknown or unusual
// service configurations retain the conservative all-machine barrier.
async function enrolledLocalAgentMachine(hub) {
    if(process.platform!=='linux')return null;
    const file=path.join(os.homedir(),'.config/experiment-hub/agent.env');
    try {
        const {stdout}=await run(SYSTEMCTL,['--user','show',UNITS.agent,
            '--property=EnvironmentFiles'],{timeout:5000,maxBuffer:4096});
        if(stdout.trim()!=='EnvironmentFiles='+file+' (ignore_errors=no)')
            return null;
        const stat=fs.lstatSync(file);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||(stat.mode&0o077)!==0)
            return null;
        const ids=fs.readFileSync(file,'utf8').split(/\r?\n/)
            .filter(line=>line.startsWith('EXPERIMENT_HUB_MACHINE_ID='));
        if(ids.length!==1)return null;
        const id=ids[0].slice('EXPERIMENT_HUB_MACHINE_ID='.length)
            .trim().replace(/^['"]|['"]$/g,'');
        if(!MACHINE_ID.test(id)||
            !hub.snapshot().machines.some(machine=>machine.id===id))
            return null;
        return id;
    }catch {return null;}
}
async function restartActivity(hub,kind) {
    const jobs=activeJobs(hub);
    if(kind!=='agent')return {count:jobs.length,scope:'all'};
    const machine=await enrolledLocalAgentMachine(hub);
    return machine
        ?{count:jobs.filter(job=>!job.assigned_machine_id||
            job.assigned_machine_id===machine).length,
            scope:'local'}
        :{count:jobs.length,scope:'all'};
}
async function unitStatus(kind,{runCommand=run,platform=process.platform,
    pid=process.pid}={}) {
    if(!Object.hasOwn(UNITS,kind))reject('Unknown local service.',400);
    const unit=UNITS[kind];
    const unavailable=reason=>({
        unit,installed:false,managed:false,active:false,pid:null,reason,
    });
    if(platform!=='linux')
        return unavailable('Local restart controls require Linux user systemd.');
    let stdout;
    try {
        ({stdout}=await runCommand(SYSTEMCTL,[
            '--user','show',unit,'--property=LoadState,ActiveState,MainPID,ExecStart',
        ],{timeout:5000,maxBuffer:16384}));
    }catch {
        return unavailable('Unable to inspect the local user systemd service.');
    }
    const fields=Object.fromEntries(stdout.split('\n').filter(Boolean)
        .map(line=>[line.slice(0,line.indexOf('=')),line.slice(line.indexOf('=')+1)]));
    if(fields.LoadState!=='loaded')
        return unavailable('User systemd unit not installed.');
    const entry=path.join(__dirname,kind==='coordinator'
        ?'experiment_hub_standalone.js':'experiment_hub_agent.js');
    // Do not let a button restart an unrelated or stale checkout's service.
    if(!String(fields.ExecStart||'').includes(' '+entry+' '))
        return unavailable('Installed service targets a different checkout; review its unit.');
    const mainPid=Number(fields.MainPID)||null;
    const active=fields.ActiveState==='active';
    const managed=kind==='coordinator'
        ?active&&mainPid===pid
        :true;
    return {
        unit,installed:true,managed,active,pid:mainPid,
        reason:managed?null:kind==='coordinator'
            ?'Dashboard is not running under its installed systemd unit.'
            :'Service is not managed.',
    };
}
async function status(hub) {
    const [tinybrains,courier]=await Promise.all([
        localEvaluators.status(hub),
        courierEvaluators.status(hub),
    ]);
    const [coordinator,agent]=await Promise.all([
        unitStatus('coordinator'),unitStatus('agent'),
    ]);
    const agentActivity=await restartActivity(hub,'agent');
    return {coordinator,agent,active_jobs:activeJobs(hub).length,
        agent_active_jobs:agentActivity.count,
        agent_restart_scope:agentActivity.scope,
        evaluators:{available:tinybrains.available||courier.available,
            tinybrains_available:tinybrains.available,
            tinybrains_reason:tinybrains.reason||null,
            courier_available:courier.available,
            courier_reason:courier.reason||null,
            entries:[...tinybrains.entries.map(row=>({project:'tinybrains',...row})),
                ...courier.entries]}};
}
async function scheduleRestart(kind,hub,res) {
    if(!Object.hasOwn(UNITS,kind))reject('Unknown local service.',400);
    const activity=await restartActivity(hub,kind);
    if(activity.count)
        reject(kind==='agent'&&activity.scope==='local'
            ?'Restart refused: local native jobs are active or unverified. Stop or reconcile them first.'
            :'Restart refused: native jobs are active or unverified. Stop or reconcile them first.');
    const service=await unitStatus(kind);
    if(!service.managed)reject(service.reason||'Service is not managed by user systemd.');
    // Deliver the acknowledgement before stopping the HTTP server itself.
    // The caller is never trusted to provide an executable, PID or unit name.
    res.once('finish',()=>{
        const child=spawn(SYSTEMCTL,['--user','restart',UNITS[kind]],{
            detached:true,stdio:'ignore',
        });
        child.once('error',error=>
            console.error('[hub] Local '+kind+' restart dispatch failed: '+error.message));
        child.unref();
    });
    return {requested:true,service:kind,
        message:'Restart requested from the local user systemd manager.'};
}
async function evaluatorAction(hub,value) {
    // Evaluator actions never stop or restart a native trainer or its owner.
    if(value?.project==='courier')return courierEvaluators.action(hub,value);
    if(value?.project===undefined||value.project==='tinybrains')
        return localEvaluators.action(hub,value);
    throw Error('Unknown local evaluator project.');
}
module.exports={status,scheduleRestart,unitStatus,evaluatorAction};
