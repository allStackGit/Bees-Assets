'use strict';

// Local-only administrative actions. User systemd owns both processes; the
// coordinator may request a restart but must never be its own relauncher.
const path=require('node:path');
const {execFile,spawn}=require('node:child_process');
const {promisify}=require('node:util');
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
    const [coordinator,agent]=await Promise.all([
        unitStatus('coordinator'),unitStatus('agent'),
    ]);
    return {coordinator,agent,active_jobs:activeJobs(hub).length};
}
async function scheduleRestart(kind,hub,res) {
    if(!Object.hasOwn(UNITS,kind))reject('Unknown local service.',400);
    if(activeJobs(hub).length)
        reject('Restart refused: native jobs are active or unverified. Stop or reconcile them first.');
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
module.exports={status,scheduleRestart,unitStatus};
