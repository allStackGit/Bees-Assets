#!/usr/bin/env node
'use strict';

// Local systemd eBPF device-denial probe. Only run with the machine-local
// supervisor's fixed command construction, never from Hub-supplied code.
const fs=require('node:fs');
const RENDER=/^\/dev\/dri\/renderD[0-9]{3,4}$/;
function canOpen(device) {
    let fd;
    try{fd=fs.openSync(device,fs.constants.O_RDWR|fs.constants.O_CLOEXEC);return true;}
    catch(error){
        if(['EACCES','EPERM'].includes(error.code))return false;
        throw error;
    }finally{if(fd!==undefined)fs.closeSync(fd);}
}
function probe(kfd,allowed,...denied) {
    if(kfd!=='/dev/kfd'||!RENDER.test(allowed)||denied.length<1||
        denied.length>31||new Set(denied).size!==denied.length||
        denied.some(node=>!RENDER.test(node)||node===allowed))
        throw Error('Invalid trusted Linux GPU probe devices.');
    const result={kfd,allowed,denied,
        kfd_access:canOpen(kfd),
        allowed_access:canOpen(allowed),
        denied_access:denied.map(canOpen)};
    if(!result.kfd_access||!result.allowed_access||result.denied_access.some(Boolean))
        throw Error('Device-policy probe did not establish allowed/blocked access.');
    return result;
}
// Multi-GPU native rollouts use one OS service with two exclusively leased
// render devices. Prove that BOTH selected nodes are accessible, and that
// every excluded GPU is blocked. A fully allocated two-GPU host legitimately
// has no excluded device; the separately attested HIP PCI set must then
// equal the complete discovered AMD render inventory.
function probeMany(kfd,allowed,denied) {
    if(kfd!=='/dev/kfd'||!Array.isArray(allowed)||!Array.isArray(denied)||
        ![1,2].includes(allowed.length)||denied.length>31||
        !allowed.every(node=>RENDER.test(node))||
        !denied.every(node=>RENDER.test(node))||
        new Set([...allowed,...denied]).size!==allowed.length+denied.length||
        (allowed.length===1&&denied.length===0))
        throw Error('Invalid trusted multi-GPU device probe inventory.');
    const result={
        kfd,allowed,denied,
        kfd_access:canOpen(kfd),
        allowed_access:allowed.map(canOpen),
        denied_access:denied.map(canOpen),
    };
    if(!result.kfd_access||result.allowed_access.some(access=>!access)||
        result.denied_access.some(Boolean))
        throw Error('Multi-GPU device-policy probe did not establish allowed/blocked access.');
    return result;
}
if(require.main===module) {
    try{
        process.stdout.write(JSON.stringify(probe(...process.argv.slice(2)))+'\n');
    }catch(error){
        console.error('GPU device access preflight failed: '+
            String(error.message||error).slice(0,150));
        process.exitCode=1;
    }
}
module.exports={probe,probeMany};
