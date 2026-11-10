#!/usr/bin/env node
'use strict';

// ExecStartPre runs in the same device cgroup as the trusted workload.
const {execFileSync}=require('node:child_process');
const {resolveRocmDevices,deviceIdentity}=require('./experiment_hub_linux_gpu_devices');
const {probe}=require('./experiment_hub_linux_gpu_probe');

function verify([id,pci,vramMb,expected,executable,...args]) {
    const binding={id,pci_address:pci,vram_mb:Number(vramMb)};
    const plan=resolveRocmDevices([binding]);
    if(deviceIdentity(plan)!==expected)
        throw Error('GPU topology changed before native workload startup.');
    const access=probe(plan.kfd.node,plan.selected[0].node,
        ...plan.unselected.map(device=>device.node));
    const result=JSON.parse(execFileSync(executable,args,{
        encoding:'utf8',timeout:12000,maxBuffer:4096,killSignal:'SIGKILL',
        stdio:['ignore','pipe','pipe'],
    }));
    if(!Array.isArray(result?.pci_addresses)||result.pci_addresses.length!==1||
        result.pci_addresses[0]!==pci)
        throw Error('Adapter runtime did not expose exactly the leased ROCm GPU.');
    // The exact same bound device-access and runtime verifier can be used
    // both for an inert qualification service and ExecStartPre. A valid
    // result contains proof only, never an executable training command.
    return {...access,pci_addresses:[pci]};
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
