'use strict';

// Machine-local, opt-in example for Halifax. Copy to a private configuration
// path if changing defaults. Requires the exact, verified Exeter checkpoint.
const os=require('node:os');
const fs=require('node:fs');
const path=require('node:path');
const {createTinyBrainsPersistentDockerAdapter}=
    require('./experiment_hub_tinybrains_persistent_adapter');

const home=os.homedir();
const ai=path.join(home,'ai');
// Advertise only installed fixed suites, and only after the evaluator receipt
// directory exists. The timer/poller must be installed separately.
const evaluationRoot=path.join(ai,'experiments/experiment-hub/evaluator-receipts');
const suiteRoot=path.join(home,'evaluation-audits/tinybrains/suites');
const suiteFiles=['tinybrains_nano_tiny_v3.json','tinybrains_micro_tiny_v3.json']
    .map(name=>path.join(suiteRoot,name));
const evaluationReady=fs.existsSync(evaluationRoot)&&
    suiteFiles.every(file=>fs.existsSync(file));
module.exports={
    tinybrains:createTinyBrainsPersistentDockerAdapter({
        projectRoot:path.join(ai,'projects/tinybrains/Ants-MAPOCA'),
        hubRoot:path.resolve(__dirname,'..'),
        artifactRoot:path.join(ai,'experiments/experiment-hub/tinybrains-jobs'),
        sourceRunRoot:path.join(ai,'experiments/tinybrains/mini-learning'),
        engineCacheRoot:path.join(ai,'experiments/tinybrains-cache'),
        // The service must write matching receipts into this private
        // directory. No evaluation is claimed until both frozen suite
        // definitions and the separate receipt directory are installed.
        evaluationRoot:evaluationReady?evaluationRoot:undefined,
        evaluationSuites:evaluationReady?suiteFiles.map(file=>
            JSON.parse(fs.readFileSync(file,'utf8')).suite_id):undefined,
        // User verified that the Exeter and Halifax files have this same SHA.
        seedCheckpointSha256:'fae407ef8db3fab7620b1407b17380e2eea0f616c8d4d8816d3924cac6e005d7',
        seedPpoUpdates:7199,
        // Independent PPO update-70 baseline chosen from the frozen Nano/Micro
        // comparison. Keep the original Exeter seed available alongside it.
        checkpointImports:{
            best70_halifax_rocm:{
                profile:'halifax_rocm',
                checkpoint:path.join(ai,
                    'projects/training-efficiency/TinyBrains-latest/Ants-MAPOCA/runs/mini-learning/self-play/update-000070.pt'),
                sha256:'f11e4d8e9c645379ce827afdec668e436e3ae0bf7778ad358f504b819d3e1780',
                update:70,
            },
        },
        // Derive CPU threads once from the leased cores, without changing
        // the requested CPU allocation or retuning while the job runs.
        // Larger allocations must still be benchmarked for useful scaling.
        cpuParallelism:'allocation',
        // Diagnostic-only host-wall PPO stage timings. Does not change
        // checkpoint formats, optimizer settings or the frozen profile digest.
        profilePpo:true,
        // Dual GPU is OFF by default. Enable only after installing the
        // matching TinyBrains two-device runtime and verifying both physical
        // R9700 PCI identities in the Docker ROCm qualification.
        // enableDualGpu:true,
        profiles:{
            halifax_rocm:{
                device:'cuda',threads:12,live_matches:16,waves:16,
                rollout_actions:32768,
            },
        },
    }),
};
