#!/usr/bin/env node
'use strict';

// Isolated Windows owner/helper qualification. No Hub manager, Bees operator,
// training adapter, training data or agent transport is loaded.
const assert=require('node:assert/strict');
const crypto=require('node:crypto');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {WindowsSupervisor}=require('./experiment_hub_windows_supervisor');

const terminal=state=>state==='finished'||state==='stopped';
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));

async function waitForSignedTerminal(root,jobId,options) {
    // The independent owner can close its status pipe before writing the
    // HMAC-signed terminal receipt. Do not treat a missing pipe as success,
    // and never issue a second Stop. Reopening the local supervisor forces
    // durable receipt verification instead of trusting transient state.
    const receipt=path.join(root,'job-'+jobId+'.receipt.json');
    for(let attempt=0;attempt<30;attempt++) {
        if(fs.existsSync(receipt)) {
            const reopened=new WindowsSupervisor(options);
            const status=await reopened.reconcile(jobId);
            if(!terminal(status.state))
                throw Error('Windows terminal receipt is invalid or unverified: '+
                    (status.diagnostic||status.state));
            return status;
        }
        await pause(500);
    }
    throw Error('Windows owner did not write a signed terminal receipt.');
}

async function main(argv=process.argv.slice(2),env=process.env) {
    if(argv.includes('--help')) {
        console.log('Usage: node Training/experiment_hub_native_smoke_windows.js --preflight');
        console.log('   or: node Training/experiment_hub_native_smoke_windows.js --run-inert');
        console.log('Requires a locally built, SHA-pinned Windows owner helper.');
        console.log('The second mode starts/stops only the helper\'s fixed inert child.');
        return;
    }
    if(argv.length!==1||
        !['--preflight','--run-inert'].includes(argv[0]))
        throw Error('Specify --preflight or --run-inert explicitly.');
    if(os.platform()!=='win32')
        throw Error('Windows native owner qualification requires Windows.');
    const helper=env.EXPERIMENT_HUB_WINDOWS_HELPER;
    const sha=env.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256;
    if(!path.win32.isAbsolute(helper||'')||
        !/^[a-f0-9]{64}$/.test(String(sha||'')))
        throw Error('Supply a reviewed absolute helper path and pinned SHA-256.');
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'hub-windows-smoke-'));
    const options={
        root,machineId:'smoke',helperPath:helper,helperSha256:sha,
        adapters:{
            // No configurable executable or arguments: the reviewed helper
            // runs its own inert 30-second mode under a separate Job Object.
            smoke:{
                executable:helper,stop_policy:'terminate-group',
                args:()=>['--smoke-child'],
            },
        },
    };
    let clean=false,jobId=null;
    try {
        const supervisor=new WindowsSupervisor(options);
        await supervisor.qualify();
        assert(supervisor.qualifiedAt,
            'Windows native owner preflight did not qualify.');
        console.log('PASS: SHA-pinned Windows helper verifies native CPU/RAM');
        console.log('      limits and suspended process assignment.');
        if(argv[0]==='--run-inert') {
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
            if(!['running','finished'].includes(launched.state))
                throw Error('Native Job Object did not verify its bounded workload.');
            console.log('PASS: isolated native Job Object Start was verified.');
            if(launched.state==='running') {
                // Send one bounded terminate-group request. An unknown
                // response requires inspecting the durable owner receipt;
                // sending another termination request would be unsafe.
                await supervisor.stop(jobId);
            }
            const terminalState=await waitForSignedTerminal(
                root,jobId,options);
            assert(terminal(terminalState.state),
                'Windows owner termination was not verified.');
            console.log('PASS: signed terminal receipt and journal recovery.');
        }
        clean=true;
        console.log('No Bees or project training workload was executed.');
    }catch(error) {
        console.error('Qualification failed: '+String(error.message||error));
        if(jobId)
            console.error('  native job: '+jobId);
        console.error('  inspect private journal: '+root);
        throw error;
    }finally {
        if(clean)fs.rmSync(root,{recursive:true,force:true});
        else console.error('Private journal preserved; do not retry an ambiguous launch.');
    }
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-windows-smoke] '+String(error.stack||error));
    process.exitCode=1;
});
module.exports={main,waitForSignedTerminal};
