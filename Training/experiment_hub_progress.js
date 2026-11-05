'use strict';

// Small shared wire contract. Progress is observational, not qualification
// evidence, a scheduler criterion or an authorization to release resources.
const FIELDS=new Set([
    'schema_version','source','experiment_id','segment_id',
    'source_modified_at','episodes_completed','policy_version',
    'curriculum_level','curriculum_window_episodes',
    'curriculum_window_success_rate','segment_success_rate',
    'total_environment_steps','total_optimization_steps',
    'simulation_steps_per_second','ppo_unique_samples_per_second',
]);
const safeInt=n=>Number.isSafeInteger(n)&&n>=0;
const boundedRate=n=>typeof n==='number'&&Number.isFinite(n)&&n>=0&&n<=1e12;
const ratio=n=>typeof n==='number'&&Number.isFinite(n)&&n>=0&&n<=1;
function validateProgress(value,projectId) {
    if(!value||typeof value!=='object'||Array.isArray(value)||
        projectId!=='ants'||value.schema_version!==1||
        value.source!=='ants-summary-v1'||
        Object.keys(value).some(k=>!FIELDS.has(k))||
        JSON.stringify(value).length>2048||
        typeof value.experiment_id!=='string'||
        !/^[A-Za-z0-9_-]{1,80}$/.test(value.experiment_id)||
        typeof value.segment_id!=='string'||
        !/^segment-[0-9]{4,12}$/.test(value.segment_id)||
        typeof value.source_modified_at!=='string'||
        !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(
            value.source_modified_at)||
        !Number.isFinite(Date.parse(value.source_modified_at))||
        !safeInt(value.episodes_completed)||
        !safeInt(value.policy_version)||!safeInt(value.curriculum_level))
        throw Error('Invalid project progress report.');
    const optional={
        curriculum_window_episodes:safeInt,
        curriculum_window_success_rate:ratio,
        segment_success_rate:ratio,
        total_environment_steps:safeInt,
        total_optimization_steps:safeInt,
        simulation_steps_per_second:boundedRate,
        ppo_unique_samples_per_second:boundedRate,
    };
    for(const [field,valid] of Object.entries(optional))
        if(value[field]!==null&&value[field]!==undefined&&!valid(value[field]))
            throw Error('Invalid project progress measurement: '+field);
    return {
        schema_version:1,source:'ants-summary-v1',
        experiment_id:value.experiment_id,
        segment_id:value.segment_id,
        source_modified_at:value.source_modified_at,
        episodes_completed:value.episodes_completed,
        policy_version:value.policy_version,
        curriculum_level:value.curriculum_level,
        ...Object.fromEntries(Object.keys(optional).map(k=>[
            k,value[k]??null,
        ])),
    };
}
module.exports={validateProgress};
