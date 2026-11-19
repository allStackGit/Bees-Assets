'use strict';

// Trusted Halifax-local adapter map for EXPERIMENT_HUB_LINUX_BACKEND=mixed.
// Install the private artifact directories before enabling execution.
// Courier runs as a native systemd CPU job; TinyBrains remains in ROCm Docker.
const os=require('node:os');
const fs=require('node:fs');
const path=require('node:path');
const {createCourierAdapter}=require('./experiment_hub_courier_adapter');
const tinybrains=require('./experiment_hub_tinybrains_halifax_adapter.example');

const ai=path.join(os.homedir(),'ai');
const suiteRoot=path.join(os.homedir(),'evaluation-audits/courier/suites');
const receiptRoot=path.join(ai,'experiments/experiment-hub/evaluator-receipts');
const snapshotRoot=path.join(ai,'experiments/experiment-hub/courier-evaluation-snapshots');
// The Hub UI installs and freezes approved survival suites. Only installed
// files are advertised; restarting the agent refreshes this inventory.
const suites=fs.existsSync(suiteRoot)
    ?fs.readdirSync(suiteRoot).filter(name=>
        /^courier_survival_[0-9a-f]{12}\.json$/.test(name)).map(name=>
        JSON.parse(fs.readFileSync(path.join(suiteRoot,name),'utf8')).suite_id)
    :[];
const evaluationReady=suites.length>0&&fs.existsSync(receiptRoot)&&
    fs.existsSync(snapshotRoot);
module.exports={
    courier:createCourierAdapter({
        projectRoot:path.join(ai,'projects/basic-training'),
        pythonPath:path.join(ai,'envs/cpu-training/bin/python'),
        artifactRoot:path.join(ai,'experiments/experiment-hub/courier-jobs'),
        configPath:path.join(ai,'projects/basic-training/environment.json'),
        cpuParallelism:'allocation',
        ...(evaluationReady?{
            evaluationRoot:receiptRoot,
            evaluationSuites:suites,
            snapshotRoot,
        }:{ }),
    }),
    tinybrains:tinybrains.tinybrains,
};
