#!/usr/bin/env node
'use strict';

// Isolated Windows owner/helper qualification. No Hub manager, Bees operator,
// project execution adapter, training data or agent transport is loaded.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {WindowsSupervisor}=require('./experiment_hub_windows_supervisor');

async function main(argv=process.argv.slice(2),env=process.env) {
    if(argv.includes('--help')) {
        console.log('Usage: node Training/experiment_hub_native_smoke_windows.js --preflight');
        console.log('Requires EXPERIMENT_HUB_WINDOWS_HELPER (absolute installed .exe)');
        console.log('and EXPERIMENT_HUB_WINDOWS_HELPER_SHA256 (pinned lowercase hash).');
        console.log('Runs only the installed helper\'s inert Job Object --probe.');
        return;
    }
    if(argv.length!==1||argv[0]!=='--preflight')
        throw Error('Specify --preflight explicitly.');
    if(os.platform()!=='win32')
        throw Error('Windows native owner preflight requires Windows.');
    const helper=env.EXPERIMENT_HUB_WINDOWS_HELPER;
    const sha=env.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256;
    if(!path.win32.isAbsolute(helper||'')||
        !/^[a-f0-9]{64}$/.test(String(sha||'')))
        throw Error('Supply a reviewed absolute helper path and pinned SHA-256.');
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'hub-windows-smoke-'));
    let clean=false;
    try {
        const supervisor=new WindowsSupervisor({
            root,machineId:'smoke',adapters:{},
            helperPath:helper,helperSha256:sha,
        });
        await supervisor.qualify();
        assert(supervisor.qualifiedAt,
            'Windows native owner preflight did not qualify.');
        clean=true;
        console.log('PASS: SHA-pinned Windows helper verified native CPU and');
        console.log('      memory limits and suspended process assignment.');
        console.log('No project workload, training, Hub agent or dashboard used.');
    }finally {
        if(clean)
            fs.rmSync(root,{recursive:true,force:true});
        else
            console.error('Preflight failed; private state preserved for review: '+root);
    }
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-windows-smoke] '+String(error.stack||error));
    process.exitCode=1;
});
module.exports={main};
