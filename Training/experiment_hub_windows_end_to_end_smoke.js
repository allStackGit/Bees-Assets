#!/usr/bin/env node
'use strict';

// One isolated Windows-only Hub lifecycle over actual loopback HTTP and
// actual pinned Job Object owner. No Bees operator, persistent dashboard
// registry, external project adapter, GPU workload or automation is loaded.
const assert=require('node:assert/strict');
const crypto=require('node:crypto');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {ExperimentHub}=require('./experiment_hub_core');
const {createAgentServer}=require('./experiment_hub_agent_listener');
const {endpoint,observations,observedOffers,sendHeartbeat}=
    require('./experiment_hub_agent');
const {LocalExecution}=require('./experiment_hub_execution');
const {WindowsSupervisor}=require('./experiment_hub_windows_supervisor');
const {waitForSignedTerminal}=
    require('./experiment_hub_native_smoke_windows');

const terminal=state=>state==='finished'||state==='stopped';
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));

async function qualifyQueue({hub,executor,url,token,advertisement,firstJob,
    localRoot,localOptions}) {
    const project=hub.addProject({
        id:'smokeextra',name:'Second isolated Windows workload',
    });
    const secondConfig=hub.saveConfiguration({
        project_id:project.id,name:'Second fixed inert owner child',
        parameters:{kind:'inert'},
    });
    const thirdConfig=hub.saveConfiguration({
        project_id:'smoke',name:'Queued behind CPU/RAM capacity',
        parameters:{kind:'inert'},
    });
    const makeJob=(configuration,resources)=>hub.queueConfiguration({
        configuration_id:configuration.id,
        expected_revision:configuration.revision,resources,
    });
    const second=makeJob(secondConfig,{cpus:1,ram_mb:256,gpu_count:0});
    const third=makeJob(thirdConfig,{cpus:1,ram_mb:256,gpu_count:0});
    // GPU-capable hardware is deliberately not needed or exercised here.
    const gpu=makeJob(thirdConfig,{cpus:1,ram_mb:256,gpu_count:1});
    const cpuJobs=[firstJob,second,third];
    let receipts=[],pending=[],starts=0,stops=0,peakRunning=0;
    const poll=async()=>{
        const result=await sendHeartbeat(url,'smoke',token,receipts,
            pending,advertisement);
        pending=[];
        receipts=observedOffers(result.lease_offers,observations('smoke'));
        assert((result.execution_orders||[]).length<=1,
            'Agent received more than one native execution directive.');
        if(result.execution_orders?.length) {
            const order=result.execution_orders[0];
            assert.equal(order.job.project_id==='smoke'||
                order.job.project_id==='smokeextra',true);
            assert.equal(order.job.resources.gpu_count,0);
            if(order.action==='start')starts++;
            if(order.action==='stop')stops++;
            const report=await executor.handle(order);
            pending=[report];
        }
        const snapshot=hub.snapshot();
        peakRunning=Math.max(peakRunning,snapshot.jobs.filter(j=>
            ['running','stop_requested'].includes(j.status)).length);
        return snapshot;
    };
    await poll();
    const armed=hub.configureAutomation({
        enabled:true,machine_ids:['smoke'],
        confirm_arm:true,confirm_untracked_load:true,
    });
    assert.equal(armed.enabled,true);
    async function until(condition,reason) {
        for(let i=0;i<100;i++) {
            const automation=hub.tickAutomation();
            if(!automation.enabled)
                throw Error('Native automation disarmed: '+automation.last_error);
            const snapshot=await poll();
            if(condition(snapshot))return snapshot;
            await pause(80);
        }
        throw Error('Isolated automatic queue did not reach '+reason+'.');
    }
    // Two one-CPU jobs fit the deliberately registered two-CPU, 512 MiB
    // budget. The third and the GPU job must not be launched.
    const two=await until(s=>cpuJobs.slice(0,2).every(j=>
        s.jobs.find(x=>x.id===j.id)?.status==='running'),
        'two concurrent verified native services');
    assert.equal(two.jobs.find(j=>j.id===third.id).status,'waiting_adapter');
    assert.equal(two.jobs.find(j=>j.id===gpu.id).status,'waiting_adapter');
    assert.equal(two.reservations.filter(l=>l.status!=='released').length,2);
    assert.equal(peakRunning,2);
    console.log('PASS: two concurrent Windows Job Objects within a fixed');
    console.log('      2-CPU / 512-MiB claimed budget; third and GPU job held.');
    hub.authorizeStop({job_id:firstJob.id,confirm_stop:true});
    await until(s=>s.jobs.find(x=>x.id===third.id)?.status==='running',
        'capacity reuse and third qualified Start');
    assert.equal(hub.state.jobs.find(x=>x.id===firstJob.id).capacity_released_at!=null,true);
    console.log('PASS: first native Stop acknowledged; verified terminal');
    console.log('      capacity automatically released and third job started.');

    for(const job of [second,third]) {
        if(hub.state.jobs.find(j=>j.id===job.id).status==='running')
            hub.authorizeStop({job_id:job.id,confirm_stop:true});
    }
    await until(s=>cpuJobs.every(job=>
        s.jobs.find(x=>x.id===job.id)?.capacity_released_at!=null),
        'all native terminals and released capacity');
    for(const job of cpuJobs)
        await waitForSignedTerminal(localRoot,job.id,localOptions);
    const done=hub.snapshot();
    assert.equal(done.reservations.filter(l=>l.status!=='released').length,0);
    assert.equal(done.jobs.find(j=>j.id===gpu.id).status,'waiting_adapter');
    assert.equal(done.automation.managed_jobs,0);
    assert.equal(done.automation.enabled,true);
    hub.configureAutomation({enabled:false,confirm_disarm:true});
    const restored=new ExperimentHub(path.dirname(hub.file));
    assert.equal(restored.automationStatus().enabled,false);
    assert.equal(restored.state.jobs.filter(j=>
        cpuJobs.some(x=>x.id===j.id)&&j.capacity_released_at!=null).length,3);
    console.log('PASS: verified signed terminal receipts, no outstanding');
    console.log('      CPU claims, explicit disarm and restart-safe registry.');
    console.log('Queue observations: '+JSON.stringify({
        starts,stops,peak_running:peakRunning,
        cpu_jobs_released:3,gpu_jobs_started:0,
    }));
}


async function main(argv=process.argv.slice(2),env=process.env) {
    if(argv.length!==1||!['--run-inert','--run-queue'].includes(argv[0]))
        throw Error('Specify --run-inert or --run-queue for isolated Windows qualification.');
    const queueMode=argv[0]==='--run-queue';
    if(os.platform()!=='win32')
        throw Error('This controlled end-to-end smoke requires Windows.');
    const helper=env.EXPERIMENT_HUB_WINDOWS_HELPER;
    const sha=env.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256;
    if(!path.win32.isAbsolute(helper||'')||
        !/^[a-f0-9]{64}$/.test(String(sha||'')))
        throw Error('Provide the reviewed absolute Windows helper path and pinned SHA-256.');

    const root=fs.mkdtempSync(path.join(os.tmpdir(),'hub-windows-e2e-'));
    const localRoot=path.join(root,'native');
    const localOptions={
        root:localRoot,machineId:'smoke',
        helperPath:helper,helperSha256:sha,
        adapters:Object.fromEntries((queueMode?['smoke','smokeextra']:['smoke'])
            .map(project=>[project,{
                executable:helper,stop_policy:'terminate-group',
                args:()=>['--smoke-child'],
            }])),
    };
    let server=null,clean=false,jobId=null;
    const began=Date.now(),durations={};
    try {
        const supervisor=new WindowsSupervisor(localOptions);
        const preflightBegan=Date.now();
        await supervisor.qualify();
        durations.preflight_ms=Date.now()-preflightBegan;
        assert(supervisor.qualifiedAt,'Windows resource preflight must pass.');
        const executor=new LocalExecution({
            machineId:'smoke',supervisor,
            projects:queueMode?['smoke','smokeextra']:['smoke'],
        });
        const hub=new ExperimentHub(path.join(root,'hub'));
        hub.addProject({id:'smoke',name:'Isolated Windows qualification'});
        const available=observations('smoke');
        if(queueMode&&(available.cpus<2||available.ram_mb<512))
            throw Error('Concurrent queue qualification requires at least two logical CPUs and 512 MiB RAM.');
        hub.addMachine({
            id:'smoke',name:'Temporary Windows worker',
            cpus:queueMode?2:Math.min(available.cpus,4096),
            ram_mb:queueMode?512:Math.min(available.ram_mb,100000000),
            gpus:[],
        });
        const token=hub.enrollMachine({
            machine_id:'smoke',confirm_enroll:true,
        }).agent_token;
        const configuration=hub.saveConfiguration({
            project_id:'smoke',name:'Fixed inert Windows owner child',
            parameters:{kind:'inert'},
        });
        const job=hub.queueConfiguration({
            configuration_id:configuration.id,
            expected_revision:configuration.revision,
            resources:{cpus:1,ram_mb:256,gpu_count:0},
        });
        jobId=job.id;
        const advertisement={
            ...executor.advertisement(),native_preflight_passed:true,
            instance_id:crypto.randomUUID(),
        };
        server=createAgentServer(()=>hub);
        await new Promise((resolve,reject)=>{
            server.once('error',reject);
            server.listen(0,'127.0.0.1',resolve);
        });
        const url=endpoint('http://127.0.0.1:'+server.address().port);
        const send=(ids=[],reports=[])=>sendHeartbeat(
            url,'smoke',token,ids,reports,advertisement);

        const connected=await send();
        assert.equal(connected.status,'connected');
        assert.equal(hub.snapshot().automation.enabled,false);
        if(queueMode) {
            await qualifyQueue({hub,executor,url,token,advertisement,
                firstJob:job,localRoot,localOptions});
            clean=true;
            console.log('No Bees or Ants training was started.');
            return;
        }
        const placement=hub.allocateJobs({
            all_projects:true,job_ids:[job.id],
            machine_id:'smoke',confirm_allocate:true,
        });
        assert.equal(placement.results[0].status,'claimed');
        const offered=await send();
        const receipts=observedOffers(
            offered.lease_offers,observations('smoke'));
        assert.equal(receipts.length,1);
        await send(receipts);
        assert.equal(hub.snapshot().reservations[0].receipt_fresh,true);
        hub.authorizeStart({job_id:job.id,confirm_start:true});
        const startResponse=await send(receipts);
        assert.equal(startResponse.execution_orders?.length,1);
        assert.equal(startResponse.execution_orders[0].action,'start');
        const launchBegan=Date.now();
        const runningReport=await executor.handle(
            startResponse.execution_orders[0]);
        durations.native_start_ms=Date.now()-launchBegan;
        assert(['running','finished'].includes(runningReport.state),
            'Only verified native running or completed work is accepted.');
        const runningAck=await send(receipts,[runningReport]);
        assert.equal(hub.snapshot().jobs[0].status,runningReport.state);
        console.log('PASS: authenticated worker accepted lease and dispatched');
        console.log('      exactly one locally verified native Windows Start.');

        const shutdownBegan=Date.now();
        if(runningReport.state==='running') {
            hub.authorizeStop({job_id:job.id,confirm_stop:true});
            const stopResponse=await send(receipts);
            assert.equal(stopResponse.execution_orders?.length,1);
            assert.equal(stopResponse.execution_orders[0].action,'stop');
            // One stop, never a retry after ambiguous owner response.
            let pending=[await executor.handle(
                stopResponse.execution_orders[0])];
            let ended=false;
            for(let attempt=0;attempt<30;attempt++) {
                const next=await send(receipts,pending);
                pending=[];
                const status=hub.snapshot().jobs[0].status;
                if(terminal(status)) {ended=true;break;}
                if(next.execution_orders?.length!==1||
                    next.execution_orders[0].action!=='inspect')
                    throw Error('Native shutdown is uncertain; inspect the local journal.');
                await pause(500);
                pending=[await executor.handle(next.execution_orders[0])];
            }
            if(!ended)
                throw Error('Windows job did not report a verified terminal state.');
        }
        await waitForSignedTerminal(localRoot,job.id,localOptions);
        durations.shutdown_receipt_ms=Date.now()-shutdownBegan;
        assert(terminal(hub.snapshot().jobs[0].status),
            'Hub did not persist the final authenticated native report.');
        const leaseId=hub.state.jobs[0].lease_id;
        hub.releaseLease({lease_id:leaseId,confirm_release:true});
        const restored=new ExperimentHub(path.join(root,'hub'));
        assert(terminal(restored.state.jobs[0].status));
        assert.equal(restored.state.jobs[0].capacity_released_at!=null,true);
        assert.equal(restored.state.reservations[0].status,'released');
        assert.equal(restored.automationStatus().enabled,false);
        clean=true;
        console.log('PASS: authenticated Stop/terminal receipt, explicit');
        console.log('      capacity release and coordinator restart.');
        durations.total_ms=Date.now()-began;
        console.log('Qualification timings: '+JSON.stringify(durations));
        console.log('No Bees or Ants training was started.');
    }catch(error) {
        console.error('Isolated Windows lifecycle not qualified: '+
            String(error.message||error));
        if(jobId)console.error('  native job: '+jobId);
        console.error('  inspect private qualification records: '+root);
        throw error;
    }finally {
        if(server?.listening)
            await new Promise((resolve,reject)=>
                server.close(e=>e?reject(e):resolve()));
        if(clean)fs.rmSync(root,{recursive:true,force:true});
        else console.error('Private state preserved; do not retry an uncertain native launch.');
    }
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-windows-e2e] '+String(error.stack||error));
    process.exitCode=1;
});
module.exports={main};
