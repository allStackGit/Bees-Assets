'use strict';

// Independent evaluation is a separate adapter contract from training progress.
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA256=/^[a-f0-9]{64}$/;
const trials=n=>Number.isSafeInteger(n)&&n>=1&&n<=1000000;
const record=value=>value&&typeof value==='object'&&!Array.isArray(value);

function evaluationPolicy(value) {
    if(value==null)return null;
    if(!record(value)||Object.keys(value).some(key=>
        !['suite_id','min_trials','min_success_rate'].includes(key))||
        typeof value.suite_id!=='string'||!SUITE.test(value.suite_id)||
        !trials(value.min_trials)||typeof value.min_success_rate!=='number'||
        !(value.min_success_rate>0&&value.min_success_rate<=1))
        throw Error('Evaluation stop needs a suite ID, minimum trials and a success rate in (0, 1].');
    return {suite_id:value.suite_id,min_trials:value.min_trials,
        min_success_rate:value.min_success_rate};
}

function evaluationEvidence(value,policy,dispatchId) {
    if(!record(value)||value.schema_version!==1||
        value.suite_id!==policy.suite_id||value.dispatch_id!==dispatchId||
        typeof value.checkpoint_sha256!=='string'||!SHA256.test(value.checkpoint_sha256)||
        !trials(value.trials)||!Number.isSafeInteger(value.successes)||
        value.successes<0||value.successes>value.trials)
        throw Error('Invalid independent evaluation evidence for this suite and dispatch.');
    return {schema_version:1,suite_id:value.suite_id,dispatch_id:dispatchId,
        checkpoint_sha256:value.checkpoint_sha256,trials:value.trials,
        successes:value.successes};
}
function suiteIds(value) {
    if(!Array.isArray(value)||value.length<1||value.length>8||
        new Set(value).size!==value.length||
        value.some(id=>typeof id!=='string'||!SUITE.test(id)))
        throw Error('Evaluation capability needs 1–8 distinct valid suite IDs.');
    return value.slice();
}
function supportsEvaluation(suites,job) {
    return !job.evaluation_stop||
        (suites&&Object.hasOwn(suites,job.project_id)&&
         suites[job.project_id].includes(job.evaluation_stop.suite_id));
}
module.exports={evaluationPolicy,evaluationEvidence,suiteIds,supportsEvaluation};
