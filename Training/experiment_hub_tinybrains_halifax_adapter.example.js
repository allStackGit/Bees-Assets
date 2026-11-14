'use strict';

// Machine-local, opt-in example for Halifax. Copy to a private configuration
// path if changing defaults. Requires the exact, verified Exeter checkpoint.
const os=require('node:os');
const path=require('node:path');
const {createTinyBrainsPersistentDockerAdapter}=
    require('./experiment_hub_tinybrains_persistent_adapter');

const home=os.homedir();
const ai=path.join(home,'ai');
module.exports={
    tinybrains:createTinyBrainsPersistentDockerAdapter({
        projectRoot:path.join(ai,'projects/tinybrains/Ants-MAPOCA'),
        hubRoot:path.resolve(__dirname,'..'),
        artifactRoot:path.join(ai,'experiments/experiment-hub/tinybrains-jobs'),
        sourceRunRoot:path.join(ai,'experiments/tinybrains/mini-learning'),
        engineCacheRoot:path.join(ai,'experiments/tinybrains-cache'),
        // User verified that the Exeter and Halifax files have this same SHA.
        seedCheckpointSha256:'fae407ef8db3fab7620b1407b17380e2eea0f616c8d4d8816d3924cac6e005d7',
        seedPpoUpdates:7199,
        // Derive CPU threads once from the leased cores, without changing
        // the requested CPU allocation or retuning while the job runs.
        // Larger allocations must still be benchmarked for useful scaling.
        cpuParallelism:'allocation',
        // Diagnostic-only host-wall PPO stage timings. Does not change
        // checkpoint formats, optimizer settings or the frozen profile digest.
        profilePpo:true,
        // Dual and 3–8 GPU modes are offered automatically only when
        // the installed TinyBrains CLI/native collector files support them.
        // Native Start still verifies every selected R9700 PCI identity
        // against the fixed exclusive Hub lease. Set either to false to
        // disable a mode locally; no extra per-experiment tuning needed.
        // enableDualGpu:false, enableMultiGpu:false,
        profiles:{
            halifax_rocm:{
                device:'cuda',threads:12,live_matches:16,waves:16,
                rollout_actions:32768,
            },
        },
    }),
};
