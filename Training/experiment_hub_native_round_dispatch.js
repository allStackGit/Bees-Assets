#!/usr/bin/env node
'use strict';

// One-shot control-plane bridge between an issued native MA-POCA collection
// round and Experiment Hub. Only Hub owns leases, GPU proof, durable Starts,
// process supervision and terminal resource release. This process is invoked
// by the learner *after* it publishes a signed work order and source archive.
//
// The loopback Hub operator API is the sole mutation interface. Names are
// derived from the immutable work-order digest, so an interrupted invocation
// can discover its existing configurations and exact bundle, not queue more
// simulations on the same training round.
const fs=require('node:fs');
const crypto=require('node:crypto');
const {parseArgs}=require('node:util');
const {request:normalizeResources}=require('./experiment_hub_resources');
const {PORTABLE_CONFIG_SHA256}=require('./experiment_hub_native_portable_adapter');

const SHA=/^[0-9a-f]{64}$/;
const ID=/^[A-Za-z0-9_-]{1,48}$/;
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const sha256=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const stable=value=>JSON.stringify(value);

function loadJson(path) {
    if(typeof path!=='string'||!path)return null;
    return JSON.parse(fs.readFileSync(path,'utf8'));
}
function checkPlan(plan,orders) {
    if(!plan||plan.schema_version!==1||
        plan.kind!=='native_hub_round_dispatch'||
        plan.schedule_start!==true||
        typeof plan.confirm_unattended_start!=='boolean'||
        plan.confirm_unattended_start!==true||
        typeof plan.confirm_gpu_start!=='boolean'||
        (plan.resume_after_restart!==undefined&&
         typeof plan.resume_after_restart!=='boolean')||
        (plan.confirm_restart_rearm!==undefined&&
         typeof plan.confirm_restart_rearm!=='boolean')||
        (plan.resume_after_restart===true&&
         (plan.confirm_restart_rearm!==true||
          plan.collectors?.some(item=>item.resources?.gpu_count>0)))||
        (!plan.resume_after_restart&&plan.confirm_restart_rearm===true)||
        !Array.isArray(plan.collectors)||
        plan.collectors.length!==orders.jobs.length||
        plan.collectors.length<2||plan.collectors.length>16)
        throw Error('Hub round dispatch requires 2–16 machine allocations and explicit unattended Start approval.');
    const jobs=new Map();
    for(const job of orders.jobs) {
        if(!ID.test(String(job?.id||''))||jobs.has(job.id)||
            !SHA.test(String(job.collection_id||''))||
            typeof job.bundle_directory!=='string'||
            !/^[A-Za-z0-9_-]{1,96}$/.test(job.bundle_directory)||
            !Number.isSafeInteger(job.world_start)||job.world_start<0||
            !Number.isSafeInteger(job.world_count)||job.world_count<1||
            !Number.isSafeInteger(job.rollout_workers)||job.rollout_workers<1)
            throw Error('Malformed issued native collector identity or simulation window.');
        jobs.set(job.id,job);
    }
    if(orders.kind!=='native_distributed_collection_orders'||
        orders.schema_version!==1||!SHA.test(String(orders.source_run_contract_sha256||''))||
        !Number.isSafeInteger(orders.seed_offset)||orders.seed_offset<0||
        !Number.isSafeInteger(orders.round)||orders.round<1)
        throw Error('Hub round dispatch requires a native v2 source contract work order.');
    const seen=new Set();
    for(const selected of plan.collectors) {
        if(!selected||!jobs.has(selected.id)||seen.has(selected.id)||
            typeof selected.machine_id!=='string'||!selected.machine_id||
            !/^[a-z][a-z0-9_-]{0,47}$/.test(selected.machine_id)||
            !selected.resources||typeof selected.resources!=='object'||
            Array.isArray(selected.resources))
            throw Error('Each Hub dispatch allocation must identify one issued collector and machine.');
        seen.add(selected.id);
        const allocation=selected.resources;
        const job=jobs.get(selected.id);
        const physics=job.physics_workers??1;
        if(!Number.isSafeInteger(physics)||physics<1||physics>128||
            job.rollout_workers>job.world_count||
            !Number.isSafeInteger(allocation.cpus)||
            allocation.cpus<job.rollout_workers*physics||
            !Number.isSafeInteger(allocation.ram_mb)||allocation.ram_mb<128||
            ![0,1,2].includes(allocation.gpu_count??0)||
            (allocation.gpu_count>0&&(
                allocation.gpu_mode!=='exclusive'||
                plan.confirm_gpu_start!==true||
                job.rollout_workers<allocation.gpu_count||
                job.world_count<allocation.gpu_count)))
            throw Error('Issued collector must fit the explicit Hub CPU/RAM/GPU lease and one actor per GPU.');
    }
    return jobs;
}
function parameters(orders,job) {
    return {
        config_sha256:PORTABLE_CONFIG_SHA256,
        collector_id:job.id,
        collection_id:job.collection_id,
        seed_offset:orders.seed_offset,
        rollout_workers:job.rollout_workers,
        physics_workers:job.physics_workers??1,
        rollout_world_start:job.world_start,
        rollout_worlds:job.world_count,
        bundle_directory:job.bundle_directory,
        source_run_contract_sha256:orders.source_run_contract_sha256,
    };
}
function sameJson(a,b) {return stable(a)===stable(b);}
async function api(port,method,resource,body) {
    const response=await fetch('http://127.0.0.1:'+port+'/api/hub/'+resource,{
        method,redirect:'error',signal:AbortSignal.timeout(30000),
        ...(body===undefined?{}:{headers:{'Content-Type':'application/json'},
            body:stable(body)}),
    });
    const result=await response.json();
    if(!response.ok)
        throw Error('Hub '+resource+' HTTP '+response.status+': '+String(result?.error||'unknown error'));
    return result;
}
async function dispatch(ordersFile,planFile,port) {
    const raw=fs.readFileSync(ordersFile);
    const orders=JSON.parse(raw);
    const plan=loadJson(planFile);
    const jobs=checkPlan(plan,orders);
    const digest=sha256(raw);
    const prefix='native-pc-'+digest.slice(0,24)+'-';
    let state=await api(port,'GET','state');
    const project=state.projects.find(p=>p.id==='bees_native_collect');
    if(project?.adapter!=='unconfigured')
        throw Error('Install bees_native_collect as an Experiment Hub project before dispatch.');
    const allocations=[];
    // Configuration creation is idempotent across crashes, restarts and
    // duplicate operator invocations. Never modify a saved revision that
    // could already be referenced by another queued job.
    for(const selection of plan.collectors) {
        const job=jobs.get(selection.id);
        const name=prefix+selection.id;
        if(name.length>100)throw Error('Collector identity too long for Hub configuration name.');
        const frozen=parameters(orders,job);
        const existing=state.configurations.filter(c=>
            c.project_id==='bees_native_collect'&&c.name===name);
        if(existing.length>1)
            throw Error('Conflicting duplicate Hub configurations for an issued native work order.');
        let configuration=existing[0];
        if(configuration) {
            if(!sameJson(configuration.parameters,frozen))
                throw Error('An existing Hub collector configuration has different frozen inputs.');
        } else {
            configuration=await api(port,'POST','configurations',{
                project_id:'bees_native_collect',name,parameters:frozen,
            });
            state=await api(port,'GET','state');
        }
        allocations.push({id:selection.id,machine_id:selection.machine_id,
            resources:selection.resources,configuration});
    }
    const discovered=allocations.map(row=>
        state.jobs.filter(job=>job.configuration_id===row.configuration.id&&
            job.configuration_revision===row.configuration.revision));
    if(discovered.some(matches=>matches.length>1))
        throw Error('Multiple Hub jobs already reference the same issued collector allocation.');
    const prior=discovered.filter(matches=>matches.length>0).length;
    let bundleId,memberIds;
    if(prior&&prior!==allocations.length)
        throw Error('Partial native Hub bundle already exists; inspect ownership before resubmitting.');
    if(prior) {
        const found=discovered.map(matches=>matches[0]);
        bundleId=found[0].resource_bundle?.id;
        if(!UUID.test(String(bundleId||''))||
            found.some((item,index)=>
                item.resource_bundle?.id!==bundleId||
                item.resource_bundle.index!==index+1||
                item.resource_bundle.size!==found.length||
                item.project_id!=='bees_native_collect'||
                item.assigned_machine_id!==allocations[index].machine_id||
                !sameJson(normalizeResources(item.resources),
                    normalizeResources(allocations[index].resources))||
                !sameJson(item.frozen_parameters,allocations[index].configuration.parameters)))
            throw Error('Existing native Hub bundle differs from issued immutable work.');
        memberIds=found.map(item=>item.id);
    } else {
        const queued=await api(port,'POST','resource-bundles',{
            confirm_queue:true,members:allocations.map(row=>({
                configuration_id:row.configuration.id,
                expected_revision:row.configuration.revision,
                machine_id:row.machine_id,
                resources:row.resources,
            })),
        });
        bundleId=queued.bundle_id;
        memberIds=queued.members.map(item=>item.id);
    }
    if(!UUID.test(String(bundleId||''))||
        memberIds.length!==allocations.length||
        memberIds.some(id=>!UUID.test(String(id||''))))
        throw Error('Hub did not return the exact reserved native worker identities.');
    state=await api(port,'GET','state');
    const existingPlans=(state.bundle_start_plans||[]).filter(p=>p.bundle_id===bundleId);
    const planCurrent=existingPlans.find(p=>
        ['pending','needs_review','authorized','disarmed','expired'].includes(p.status));
    if(planCurrent?.status==='needs_review'||planCurrent?.status==='disarmed'||
        planCurrent?.status==='expired')
        throw Error('Hub requires explicit operator review of an interrupted native Start plan.');
    let launch=planCurrent;
    const active=state.jobs.filter(j=>memberIds.includes(j.id));
    if(!launch&&active.every(j=>j.status==='waiting_adapter')) {
        launch=await api(port,'POST','resource-bundles/schedule-start',{
            bundle_id:bundleId,expected_job_ids:memberIds,
            confirm_schedule:true,
            confirm_gpu_start:plan.confirm_gpu_start,
            resume_after_restart:plan.resume_after_restart===true,
            confirm_restart_rearm:plan.confirm_restart_rearm===true,
        });
        if(!['pending','authorized'].includes(launch?.status))
            throw Error('Native bundle requires Hub operator review before unattended Start.');
    } else if(!launch&&active.some(j=>j.status==='waiting_adapter')) {
        throw Error('Native bundle has partially started; inspect exact Hub owners.');
    }
    const output={
        kind:'native_hub_collection_round_dispatched',
        round:orders.round,orders_sha256:digest,
        bundle_id:bundleId,member_job_ids:memberIds,
        start_status:launch?.status||'already_authorized',
        collectors:allocations.map((row,i)=>({
            collector_id:row.id,machine_id:row.machine_id,
            hub_job_id:memberIds[i],
        })),
    };
    process.stdout.write(stable(output)+'\n');
}
async function main() {
    const {values}=parseArgs({options:{
        orders:{type:'string'},plan:{type:'string'},
        port:{type:'string',default:'7148'},
    }});
    const port=Number(values.port);
    if(!values.orders||!values.plan||
        !/^[0-9]{1,5}$/.test(values.port)||port<1||port>65535)
        throw Error('Usage: native_round_dispatch --orders ISSUED.json --plan MACHINE.json [--port 7148]');
    await dispatch(values.orders,values.plan,port);
}
if(require.main===module)main().catch(error=>{
    process.stderr.write(String(error.message||error)+'\n');
    process.exitCode=1;
});
module.exports={dispatch};
