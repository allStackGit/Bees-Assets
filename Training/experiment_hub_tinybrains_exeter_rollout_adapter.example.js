'use strict';

// Exeter: two separate CPU-only native rollout jobs, one per Halifax learner.
// This is an opt-in local adapter map, not a public listener or Hub command.
// Install TinyBrains' authenticated remote_rollout runtime, private shared
// rollout.key, and a native Experiment Hub agent before selecting this map.
// All variables below belong to the trusted Exeter service environment.
const path=require('node:path');
const {createTinyBrainsRemoteRolloutAdapter}=
    require('./experiment_hub_tinybrains_remote_adapter');

function required(name) {
    const value=process.env[name];
    if(!value)throw Error('Set the locally approved '+name+' before enrolling Exeter.');
    return value;
}

module.exports={
    tinybrains_rollout:createTinyBrainsRemoteRolloutAdapter({
        projectRoot:required('EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT'),
        pythonPath:required('EXPERIMENT_HUB_TINYBRAINS_PYTHON'),
        keyPath:required('EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY'),
        cacheRoot:required('EXPERIMENT_HUB_TINYBRAINS_CACHE'),
        listenIp:required('EXPERIMENT_HUB_TINYBRAINS_TAILNET_IP'),
        port:12134,
        slotPorts:{0:12134,1:12135},
        // A per-job upper bound, not a reservation. Each run will receive
        // its own 6-CPU lease, enforced cumulatively against Exeter's budget.
        threads:12,
        enableCuda:false,
    }),
};
