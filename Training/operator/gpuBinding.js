'use strict';

// Bind the central PPO learner and optional co-located rollout actor to
// independently selected physical GPUs. This is process-wide, so the bound
// environment belongs to the long-lived supervisor and every child it owns;
// never change visibility inside an active PyTorch process.
const os=require('node:os');

const MAX_INDEX=31;
function gpuIndex(value,description) {
    if(value==null||value==='')return null;
    if((typeof value!=='number'&&typeof value!=='string')||
        !/^(0|[1-9]\d*)$/.test(String(value)))
        throw Error(description+' must be an integer GPU index between 0 and '+MAX_INDEX+'.');
    const index=Number(value);
    if(!Number.isSafeInteger(index)||index>MAX_INDEX)
        throw Error(description+' is outside the supported GPU index range 0–'+MAX_INDEX+'.');
    return index;
}

function binding(config,role,env=process.env,platform=process.platform) {
    if(!['learner','local-actor'].includes(role))
        throw Error('Unknown Bees GPU owner.');
    if(!['linux','win32'].includes(platform))
        throw Error('Managed GPU isolation is supported only on Linux and Windows.');
    const external=role==='learner'?'BEES_LEARNER_GPU_INDEX':'BEES_LOCAL_ACTOR_GPU_INDEX';
    const fallback=role==='learner'?config?.learnerGpuIndex:config?.localActor?.gpuIndex;
    const index=gpuIndex(env[external]===undefined?fallback:env[external],external);
    if(index===null)return {index:null,environment:{...env},visibility:null};

    const device=role==='learner'?String(config?.learnerDevice||'cuda'):
        String(env.BEES_LOCAL_ACTOR_TORCH_DEVICE||config?.localActor?.torchDevice||'cpu');
    if(device!=='cuda'&&device!=='cuda:0')
        throw Error(external+' requires the matching Bees '+role+' to use CUDA/ROCm device "cuda" or "cuda:0".');

    const backend=platform==='win32'?'cuda':
        String(env.BEES_GPU_BACKEND||'rocm').toLowerCase();
    if(!['rocm','cuda'].includes(backend)||
        platform==='win32'&&backend!=='cuda')
        throw Error('BEES_GPU_BACKEND must be rocm or cuda (CUDA only on Windows).');
    const visibility=backend==='rocm'?'ROCR_VISIBLE_DEVICES':'CUDA_VISIBLE_DEVICES';
    const conflicts=backend==='rocm'
        ?['HIP_VISIBLE_DEVICES','CUDA_VISIBLE_DEVICES']
        :['HIP_VISIBLE_DEVICES','ROCR_VISIBLE_DEVICES'];
    if(conflicts.some(key=>env[key]!=null&&env[key]!=='')) {
        throw Error(
            'GPU visibility is already constrained by another variable. '+
            'Unset the inherited '+conflicts.filter(key=>env[key]!=null&&env[key]!=='').join(', ')+
            ' before assigning '+external+'; nested physical GPU indices are ambiguous.'
        );
    }
    const existing=env[visibility];
    if(existing!=null&&existing!==''&&existing!==String(index))
        throw Error('Conflicting inherited '+visibility+'='+existing+' for '+external+'='+index+'.');

    return {
        index,
        backend,
        visibility,
        environment:{...env,[visibility]:String(index)},
        fingerprint:role+':'+backend+':'+index,
    };
}
module.exports={gpuIndex,binding};
