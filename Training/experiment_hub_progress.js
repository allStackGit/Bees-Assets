'use strict';

// Small shared wire contract. Progress is observational, not qualification
// evidence, a scheduler criterion or an authorization to release resources.
const safeInt=n=>Number.isSafeInteger(n)&&n>=0;
// Optional project-neutral summaries from a locally trusted adapter.
// Values and units are explicit; neither metrics nor steps authorize
// training, benchmark decisions, lease release, or policy promotion.
const GENERIC_METRIC=/^[a-z][a-z0-9_]{0,39}$/;
const GENERIC_UNITS=new Set(['scalar','ratio','count','rate','seconds']);
function validateGenericProgress(value,projectId) {
    const metrics=value?.metrics;
    if(!/^[a-z][a-z0-9_-]{0,47}$/.test(String(projectId||''))||
        ['ants','bees'].includes(projectId)||
        !value||typeof value!=='object'||Array.isArray(value)||
        value.schema_version!==1||value.source!=='hub-metrics-v1'||
        Object.keys(value).some(key=>![
            'schema_version','source','experiment_id','segment_id',
            'source_modified_at','steps_completed','metrics',
        ].includes(key))||
        JSON.stringify(value).length>4096||
        typeof value.experiment_id!=='string'||
        !/^[A-Za-z0-9_-]{1,80}$/.test(value.experiment_id)||
        typeof value.segment_id!=='string'||
        !/^[A-Za-z][A-Za-z0-9_-]{0,63}$/.test(value.segment_id)||
        typeof value.source_modified_at!=='string'||
        !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(
            value.source_modified_at)||
        !Number.isFinite(Date.parse(value.source_modified_at))||
        new Date(value.source_modified_at).toISOString()!==value.source_modified_at||
        !safeInt(value.steps_completed)||
        !metrics||typeof metrics!=='object'||Array.isArray(metrics)||
        Object.keys(metrics).length<1||Object.keys(metrics).length>12)
        throw Error('Invalid project-neutral progress report.');
    const normalized={};
    for(const [key,item] of Object.entries(metrics).sort(([a],[b])=>
        a.localeCompare(b))) {
        if(!GENERIC_METRIC.test(key)||!item||typeof item!=='object'||
            Array.isArray(item)||Object.keys(item).length!==2||
            !Object.hasOwn(item,'value')||!Object.hasOwn(item,'unit')||
            !GENERIC_UNITS.has(item.unit)||typeof item.value!=='number'||
            !Number.isFinite(item.value)||Math.abs(item.value)>1e12||
            (item.unit==='ratio'&&(item.value<0||item.value>1))||
            (item.unit==='count'&&!safeInt(item.value))||
            (['rate','seconds'].includes(item.unit)&&item.value<0))
            throw Error('Invalid project-neutral progress measurement: '+key);
        normalized[key]={value:item.value,unit:item.unit};
    }
    return {
        schema_version:1,source:'hub-metrics-v1',
        experiment_id:value.experiment_id,segment_id:value.segment_id,
        source_modified_at:value.source_modified_at,
        steps_completed:value.steps_completed,metrics:normalized,
    };
}
function validateProgress(value,projectId) {
    return validateGenericProgress(value,projectId);
}
module.exports={validateProgress};
