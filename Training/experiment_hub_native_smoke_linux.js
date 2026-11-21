#!/usr/bin/env node
'use strict';

// Isolated, local native qualification. Never imports the Bees operator,
// contacts a coordinator, loads a production adapter or runs training.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {LinuxSupervisor,unitFor}=
    require('./experiment_hub_linux_supervisor');
const adapters=require('./experiment_hub_smoke_adapter_linux');

const terminal=state=>['finished','stopped'].includes(state);
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));

async function waitForTerminal(supervisor,jobId) {
    for(let attempt=0;attempt<30;attempt++) {
        const result=await supervisor.reconcile(jobId);
        if(terminal(result.state))return result;
        if(result.state==='unverified'||result.state==='failed')
            throw Error('Native service became '+result.state+
                '; manual inspection is required.');
        await pause(500);
    }
    throw Error('Native stop did not reach a verified terminal state.');
}

async function main(argv=process.argv.slice(2)) {
    if(argv.includes('--help')) {
        console.log('Usage: node Training/experiment_hub_native_smoke_linux.js --preflight');
        console.log('   or: node Training/experiment_hub_native_smoke_linux.js --run-inert');
        console.log('The first checks an inert systemd CPU/RAM preflight; the second');
        console.log('also starts and stops one isolated 30-second sleep service.');
        return;
    }
    if(argv.length!==1||!['--preflight','--run-inert'].includes(argv[0]))
        throw Error('Specify --preflight or --run-inert explicitly.');
    if(os.platform()!=='linux')
        throw Error('Native systemd qualification requires Linux.');
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'hub-native-smoke-'));
    const supervisor=new LinuxSupervisor({
        root,machineId:'smoke',adapters,
    });
    let clean=true,jobId=null;
    try {
        await supervisor.qualify();
        assert(supervisor.qualifiedAt,
            'Native preflight must produce a process-local qualification.');
        console.log('PASS: user systemd and kernel CPU/RAM preflight.');
        if(argv[0]==='--preflight')return;
        // No agent, manager or executable path is supplied by JSON.
        // The trusted smoke adapter is fixed to /usr/bin/sleep.
        const parameters={kind:'inert'};
        jobId=crypto.randomUUID();
        const leaseId=crypto.randomUUID();
        const resources={
            cpus:1,ram_mb:256,gpu_count:0,gpus:[],
            gpu_mode:'exclusive',gpu_vram_mb:0,
            gpu_compute_pct:100,min_gpu_vram_mb:0,
        };
        const job={
            id:jobId,project_id:'smoke',adapter:'unconfigured',
            status:'waiting_adapter',lease_id:leaseId,
            artifact_lineage_root_id:jobId,
            frozen_parameters:parameters,
            configuration_sha256:crypto.createHash('sha256')
                .update(JSON.stringify(parameters)).digest('hex'),
            resources,
        };
        const lease={
            id:leaseId,job_id:jobId,project_id:'smoke',
            machine_id:'smoke',issued_by:'experiment-hub',
            status:'acknowledged',receipt_fresh:true,resources,
        };
        const launched=await supervisor.start(job,lease);
        assert(launched.state==='running'||launched.state==='finished',
            'Native launch did not return verified process ownership.');
        console.log('PASS: bounded service '+unitFor(jobId)+
            ' launched with verified kernel CPU/RAM controls.');
        if(launched.state==='running')await supervisor.stop(jobId);
        const result=await waitForTerminal(supervisor,jobId);
        assert(terminal(result.state),'A verified terminal service is required.');
        console.log('PASS: native service terminated and ownership reconciled.');
    }catch(error) {
        // A local ledger may describe an already-launched or ambiguous unit.
        // Preserve it. Never discard ownership records or blindly retry.
        clean=false;
        console.error('Native qualification stopped: '+String(error.message||error));
        console.error('Inspect the exact systemd unit and local journal:');
        if(jobId)console.error('  unit: '+unitFor(jobId));
        console.error('  private journal root: '+root);
        throw error;
    }finally {
        if(clean) {
            fs.rmSync(root,{recursive:true,force:true});
        }else {
            console.error('Private journal preserved; no unverified capacity was released.');
        }
    }
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-native-smoke] '+String(error.message||error));
    process.exitCode=1;
});
module.exports={main};
