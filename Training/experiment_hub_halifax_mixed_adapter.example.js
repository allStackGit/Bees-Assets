'use strict';

// Trusted Halifax-local adapter map for EXPERIMENT_HUB_LINUX_BACKEND=mixed.
// Install the private artifact directories before enabling execution.
// Courier runs as a native systemd CPU job; TinyBrains remains in ROCm Docker.
const os=require('node:os');
const path=require('node:path');
const {createCourierAdapter}=require('./experiment_hub_courier_adapter');
const tinybrains=require('./experiment_hub_tinybrains_halifax_adapter.example');

const ai=path.join(os.homedir(),'ai');
module.exports={
    courier:createCourierAdapter({
        projectRoot:path.join(ai,'projects/basic-training'),
        pythonPath:path.join(ai,'envs/cpu-training/bin/python'),
        artifactRoot:path.join(ai,'experiments/experiment-hub/courier-jobs'),
        configPath:path.join(ai,'projects/basic-training/environment.json'),
        cpuParallelism:'allocation',
    }),
    tinybrains:tinybrains.tinybrains,
};
