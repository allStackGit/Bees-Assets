#!/usr/bin/env node
'use strict';

// ExecStartPre runs in the same device cgroup as the trusted workload.
const {execFileSync}=require('node:child_process');
const {resolveRocmDevices,deviceIdentity}=require('./experiment_hub_linux_gpu_devices');
const {probe,canOpen}=require('./experiment_hub_linux_gpu_probe');

function verify(argv) {
    let bindings,expected,executable,args;
    if(argv[0]==='--multi') {
        const count=Number(argv[1]);
        if(!Number.isSafeInteger(count)||count<2||count>8||
            argv.length<2+3*count+2)
            throw Error('Invalid approved multi-GPU start proof.');
        bindings=Array.from({length:count},(_,i)=>({
            id:argv[2+i*3],pci_address:argv[3+i*3],
            vram_mb:Number(argv[4+i*3]),
        }));
        [expected,executable,...args]=argv.slice(2+3*count);
    }else{
        const [id,pci,vramMb,hash,program,...rest]=argv;
        bindings=[{id,pci_address:pci,vram_mb:Number(vramMb)}];
        expected=hash;executable=program;args=rest;
    }
    const plan=resolveRocmDevices(bindings);
    if(deviceIdentity(plan)!==expected)
        throw Error('GPU topology changed before native workload startup.');
    let access;
    if(bindings.length===1) {
        // Preserve the strict single-GPU denied-device proof unchanged.
        access=probe(plan.kfd.node,plan.selected[0].node,
            ...plan.unselected.map(device=>device.node));
    }else{
        // Each member already passed its own separately restricted
        // allow/deny preflight. The combined service must open every
        // selected render node, and no non-leased node (when present).
        const allowed=plan.selected.map(g=>g.node);
        const denied=plan.unselected.map(g=>g.node);
        const kfdAccess=canOpen(plan.kfd.node);
        const allowedAccess=allowed.map(canOpen);
        const deniedAccess=denied.map(canOpen);
        if(!kfdAccess||allowedAccess.some(v=>v!==true)||
            deniedAccess.some(v=>v!==false))
            throw Error('Union GPU device filter did not match the leased physical set.');
        access={kfd:plan.kfd.node,allowed,denied,
            kfd_access:kfdAccess,allowed_access:allowedAccess,
            denied_access:deniedAccess};
    }
    const result=JSON.parse(execFileSync(executable,args,{
        encoding:'utf8',timeout:bindings.length>1?40000:12000,
        maxBuffer:4096,killSignal:'SIGKILL',
        stdio:['ignore','pipe','pipe'],
    }));
    if(!Array.isArray(result?.pci_addresses)||
        result.pci_addresses.length!==bindings.length||
        result.pci_addresses.some((pci,i)=>pci!==bindings[i].pci_address))
        throw Error('Adapter HIP runtime did not expose exactly the leased ROCm GPU set.');
    // Never accept device inventory alone as evidence of a working
    // runtime or kernel-filtered owner. Verifier also runs a tiny compute
    // sample on every physical GPU before training begins.
    return {...access,pci_addresses:bindings.map(b=>b.pci_address)};
}
if(require.main===module) {
    try{
        const proof=verify(process.argv.slice(2));
        process.stdout.write(JSON.stringify(proof)+'\n');
    }
    catch(error){
        console.error('GPU startup verification failed: '+String(error.message||error).slice(0,180));
        process.exitCode=1;
    }
}
module.exports={verify};
