'use strict';

// Local-only administrative actions. User systemd owns both processes; the
// coordinator may request a restart but must never be its own relauncher.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const net=require('node:net');
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
// The coordinator and learner share a canonical private rollout key. The
// operator selects only a machine ID; neither arbitrary URLs nor key paths
// come from the browser.
function approvedTinyBrainsWorkers(hub) {
    const root=path.dirname(hub.file);
    const keyDirectory=path.join(root,'private-rollouts','tinybrains');
    const key=path.join(keyDirectory,'rollout.key');
    let fingerprint=null;
    try {
        const stat=fs.lstatSync(key);
        if(stat.isFile()&&!stat.isSymbolicLink()&&
            stat.uid===process.getuid()&&(stat.mode&0o077)===0)
            fingerprint=crypto.createHash('sha256')
                .update(fs.readFileSync(key)).digest('hex');
    }catch {}
    const snapshot=hub.snapshot();
    const workers=snapshot.machines.filter(machine=>machine.status==='connected'&&
        machine.id!=='halifax'&&machine.installed_projects?.includes('tinybrains_rollout'))
        .map(machine=>{
            const slots=machine.tinybrains_rollout_slots||[];
            const named=slots.filter(entry=>Number.isSafeInteger(entry.slot)&&
                entry.slot>=0&&entry.slot<=1&&typeof entry.endpoint==='string')
                .sort((a,b)=>a.slot-b.slot);
            const valid=named.length===2&&named[0].slot===0&&named[1].slot===1&&
                [12134,12135].every((port,i)=>{
                    const endpoint=named[i].endpoint;
                    const match=/^(100\.\d{1,3}\.\d{1,3}\.\d{1,3}):(\d{4,5})$/.exec(endpoint);
                    return match&&net.isIP(match[1])===4&&
                        Number(match[2])===port;
                });
            const trusted=machine.native_preflight_passed===true&&
                fingerprint!==null&&machine.tinybrains_rollout_key_fingerprint===fingerprint;
            return {machine_id:machine.id,name:machine.name,
                endpoints:valid?named.map(entry=>entry.endpoint):[],
                ready:Boolean(valid&&trusted),
                reason:!valid?'Worker must advertise its two native TinyBrains slots.'
                    :!fingerprint?'Coordinator has no approved private TinyBrains rollout key.'
                    :!trusted?'Native preflight or HMAC fingerprint does not match Halifax.'
                    :null};
        });
    return {workers,keyDirectory};
}
async function configureTinyBrainsCollectors(hub,value) {
    if(!value||value.confirm_connect!==true||
        !MACHINE_ID.test(String(value.machine_id||''))||
        Object.keys(value).some(k=>!['confirm_connect','machine_id'].includes(k)))
        reject('Confirm one enrolled TinyBrains collector machine.',400);
    const localId=await enrolledLocalAgentMachine(hub);
    const unit=await unitStatus('agent');
    if(!localId||!unit.managed)reject('Halifax native agent service is not configured for managed local deployment.');
    const activity=await restartActivity(hub,'agent');
    if(activity.count)reject('Reconfigure remote collectors only when Halifax native jobs are idle.');
    if(hub.state.jobs.some(job=>job.project_id==='tinybrains'&&
        !job.capacity_released_at&&job.lease_id&&
        job.assigned_machine_id===localId&&
        job.frozen_parameters?.remote_rollouts===true&&
        !['cancelled','finished','failed','stopped'].includes(job.status)))
        reject('Existing Halifax TinyBrains distributed reservations must be resolved before changing endpoints.');
    const {workers,keyDirectory}=approvedTinyBrainsWorkers(hub);
    const chosen=workers.find(worker=>worker.machine_id===value.machine_id&&worker.ready);
    if(!chosen)reject('Selected machine has not reported two trusted TinyBrains collector ports and native preflight.');
    const file=path.join(os.homedir(),'.config/experiment-hub/agent.env');
    const st=fs.lstatSync(file);
    if(!st.isFile()||st.isSymbolicLink()||
        st.uid!==process.getuid()||(st.mode&0o077)!==0)
        reject('Halifax agent environment permissions are not approved.');
    const existing=fs.readFileSync(file,'utf8');
    if(Buffer.byteLength(existing)>65536||existing.includes('\0'))
        reject('Halifax agent environment is invalid.');
    const variables={
        EXPERIMENT_HUB_TINYBRAINS_REMOTE_ENDPOINTS:chosen.endpoints.join(','),
        EXPERIMENT_HUB_TINYBRAINS_KEY_DIRECTORY:keyDirectory,
    };
    const rows=existing.split(/\r?\n/).filter(row=>
        !Object.keys(variables).some(name=>row.startsWith(name+'=')));
    const updated=rows.filter((row,index)=>index<rows.length-1||row!=='')
        .concat(Object.entries(variables).map(([name,v])=>name+'='+v)).join('\n')+'\n';
    if(updated===existing)
        return {configured:true,unchanged:true,endpoints:chosen.endpoints,
            message:'Halifax already approves these two collector endpoints.'};
    const temporary=file+'.'+crypto.randomBytes(6).toString('hex')+'.tmp';
    try {
        fs.writeFileSync(temporary,updated,{flag:'wx',mode:0o600});
        fs.renameSync(temporary,file);
    }finally {try{fs.unlinkSync(temporary);}catch{}}
    return {configured:true,unchanged:false,endpoints:chosen.endpoints,
        message:'Approved private learner endpoints saved; restart local agent to advertise both collectors.'};
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
    const snapshot=hub.snapshot();
    const localId=await enrolledLocalAgentMachine(hub);
    const remoteMachines=snapshot.machines.filter(machine=>
        machine.id!==localId&&
        (machine.evaluator_services?.length||machine.evaluator_service_issue||
            machine.status==='connected'&&machine.agent_platform==='linux'&&
            machine.installed_projects?.some(project=>['tinybrains','courier'].includes(project)))).map(machine=>({
        id:machine.id,name:machine.name,status:machine.status,
        platform:machine.agent_platform||null,
        native_restart_blocked:snapshot.jobs.some(job=>
            (!job.assigned_machine_id||job.assigned_machine_id===machine.id)&&
            BUSY.has(job.status)),
        installed_projects:(machine.installed_projects||[]).filter(project=>
            ['tinybrains','courier'].includes(project)),
        issue:machine.evaluator_service_issue||null,
        services:(machine.evaluator_services||[]).map(entry=>({...entry})),
    }));
    const history=snapshot.evaluator_actions||[];
    const remoteActions=history.filter(action=>
        ['pending','dispatched','awaiting_restart','unverified'].includes(action.status)
        ||history.indexOf(action)>=history.length-48).map(action=>({
        id:action.id,machine_id:action.machine_id,unit:action.unit,
        project:action.project,action:action.action,status:action.status,
        created_at:action.created_at,dispatched_at:action.dispatched_at||null,
        finished_at:action.finished_at||null,reason:action.reason||null,
    }));
    return {coordinator,agent,active_jobs:activeJobs(hub).length,
        tinybrains_collectors:approvedTinyBrainsWorkers(hub).workers,
        remote_evaluators:{machines:remoteMachines,actions:remoteActions},
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
const activeEvaluatorActions=new Set();
async function evaluatorAction(hub,value) {
    // All suites within a project share an approval manifest. Serialize only
    // that project's local UI operations so overlapping requests cannot
    // interleave timer replacement and immutable suite publication.
    const project=value?.project??'tinybrains';
    if(!['courier','tinybrains'].includes(project))
        reject('Unknown local evaluator project.',400);
    if(activeEvaluatorActions.has(project))
        reject('An evaluator operation for this project is already in progress.',409);
    activeEvaluatorActions.add(project);
    try {
        // Evaluator actions never stop or restart a native trainer or its owner.
        return await (project==='courier'
            ?courierEvaluators.action(hub,value)
            :localEvaluators.action(hub,value));
    }finally {
        activeEvaluatorActions.delete(project);
    }
}
module.exports={status,scheduleRestart,unitStatus,evaluatorAction,configureTinyBrainsCollectors};
