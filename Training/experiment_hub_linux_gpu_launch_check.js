#!/usr/bin/env node
'use strict';

// ExecStartPre runs in the same device cgroup as the trusted workload.
const {execFileSync}=require('node:child_process');
const {resolveRocmDevices,deviceIdentity}=require('./experiment_hub_linux_gpu_devices');
const {probe,probeMany}=require('./experiment_hub_linux_gpu_probe');

function verify(argv) {
    // Only the machine-local supervisor constructs --dual/--multi. Preserve
    // Bees' exact two-card proof and the original project's 2–8-card ordered
    // HIP runtime mapping without accepting job-supplied physical ordinals.
    let pairs, expected, executable, args;
    const orderedMulti=argv[0]==='--multi';
    if(orderedMulti) {
        const count=Number(argv[1]);
        if(!Number.isSafeInteger(count)||count<2||count>8||
            argv.length<2+3*count+2)
            throw Error('Invalid approved multi-GPU start proof.');
        pairs=Array.from({length:count},(_,i)=>
            [argv[2+i*3],argv[3+i*3],argv[4+i*3]]);
        [expected,executable,...args]=argv.slice(2+3*count);
    }else if(argv[0]==='--dual') {
        if(argv.length<9)throw Error('Incomplete two-GPU verifier invocation.');
        pairs=[[argv[1],argv[2],argv[3]],[argv[4],argv[5],argv[6]]];
        [expected,executable,...args]=argv.slice(7);
    }else{
        if(argv.length<5)throw Error('Incomplete one-GPU verifier invocation.');
        pairs=[[argv[0],argv[1],argv[2]]];
        [expected,executable,...args]=argv.slice(3);
    }
    const bindings=pairs.map(([id,pci,vram])=>({
        id,pci_address:pci,vram_mb:Number(vram),
    }));
    const plan=resolveRocmDevices(bindings);
    if(deviceIdentity(plan)!==expected)
        throw Error('GPU topology changed before native workload startup.');
    const access=bindings.length===1
        ?probe(plan.kfd.node,plan.selected[0].node,
            ...plan.unselected.map(device=>device.node))
        :probeMany(plan.kfd.node,plan.selected.map(device=>device.node),
            plan.unselected.map(device=>device.node));
    const result=JSON.parse(execFileSync(executable,args,{
        encoding:'utf8',timeout:bindings.length>1?40000:12000,
        maxBuffer:4096,killSignal:'SIGKILL',
        stdio:['ignore','pipe','pipe'],
    }));
    const identities=bindings.map(binding=>binding.pci_address);
    if(!Array.isArray(result?.pci_addresses)||
        result.pci_addresses.length!==identities.length||
        new Set(result.pci_addresses).size!==identities.length||
        (orderedMulti
            ?result.pci_addresses.some((pci,i)=>pci!==identities[i])
            :result.pci_addresses.some(pci=>!identities.includes(pci))))
        throw Error('Adapter HIP runtime did not expose exactly the leased ROCm GPUs.');
    // Return a measured identity set, never a claim from the requested
    // resource count. The same trusted verifier runs at preflight and start.
    return {...access,pci_addresses:identities};
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
