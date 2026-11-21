#!/usr/bin/env node
'use strict';

// Called only by systemd ExecStopPost on the machine that owns the unit.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;

function writeReceipt(file,jobId,leaseId,env) {
    if(!UUID.test(jobId)||!UUID.test(leaseId)||!path.isAbsolute(file)||
        path.basename(file)!=='job-'+jobId+'.linux-receipt.json')
        throw Error('Invalid local Linux terminal receipt identity.');
    const {SERVICE_RESULT:result,EXIT_CODE:exitCode,EXIT_STATUS:exitStatus}=env;
    // Failed ExecStartPre can reach ExecStopPost without a main process.
    if(!/^[a-z][a-z-]{0,31}$/.test(result||'')||
        !['exited','killed','dumped'].includes(exitCode)||
        !/^[0-9A-Z]{1,32}$/.test(exitStatus||''))return;
    const pending=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    try {
        fs.writeFileSync(pending,JSON.stringify({version:1,job_id:jobId,
            lease_id:leaseId,result,exit_code:exitCode,exit_status:exitStatus})+'\n',
        {flag:'wx',mode:0o600});
        fs.linkSync(pending,file);
    }finally{
        if(fs.existsSync(pending))fs.unlinkSync(pending);
    }
}
if(require.main===module) {
    try{writeReceipt(...process.argv.slice(2),process.env);}
    catch(error){console.error(error.message);process.exitCode=1;}
}
