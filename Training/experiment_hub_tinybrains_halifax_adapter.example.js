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
        engineCacheRoot:path.join(ai,'projects/tinybrains/Ants-MAPOCA/cache'),
        // User verified that the Exeter and Halifax files have this same SHA.
        seedCheckpointSha256:'fae407ef8db3fab7620b1407b17380e2eea0f616c8d4d8816d3924cac6e005d7',
        profiles:{
            halifax_rocm:{
                device:'cuda',threads:12,live_matches:16,waves:16,
                rollout_actions:32768,
            },
        },
    }),
};
