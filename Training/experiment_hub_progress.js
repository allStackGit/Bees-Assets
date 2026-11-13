'use strict';

// Small shared wire contract. Progress is observational, not qualification
// evidence, a scheduler criterion or an authorization to release resources.
const safeInt=n=>Number.isSafeInteger(n)&&n>=0;
// Optional project-neutral summaries from a locally trusted adapter.
// Values and units are explicit; neither metrics nor steps authorize
// training, benchmark decisions, lease release, or policy promotion.
const GENERIC_METRIC=/^[a-z][a-z0-9_]{0,39}$/;
const GENERIC_UNITS=new Set(['scalar','ratio','count','rate','seconds','mb','bytes','milliseconds','watts']);
// Keep a larger bounded source envelope than the 96-metric chart cache.
// Complete trainer metrics must reach the observation archive before display
// downselection; the transport's separate 32-KiB limit still applies.
// Up to 17 concurrent TinyBrains collectors can report independent stage metrics.
// Keep the bounded archive complete; the chart projection stays at 96 fields.
const MAX_SOURCE_METRICS=640;
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
        JSON.stringify(value).length>32768||
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
        Object.keys(metrics).length<1||Object.keys(metrics).length>MAX_SOURCE_METRICS)
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
            (['rate','seconds','mb','bytes','milliseconds','watts'].includes(item.unit)&&item.value<0))
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
// Display data is intentionally bounded independently of the authoritative
// project log. Host measurements are already validated by the coordinator,
// but they must not disappear when a trainer reports more than 96 metrics.
function selectChartMetrics(reported,observed,preferred,stepsCompleted) {
    const metrics={};
    let count=0;
    const retain=(name,value)=>{
        if(value&&!Object.hasOwn(metrics,name)&&count<96) {
            metrics[name]=value;
            count++;
        }
    };
    if(Array.isArray(preferred)&&preferred.length) {
        // An explicit operator preference always wins. Unavailable requested
        // metrics are not fabricated and do not consume the 96-field budget.
        for(const name of preferred)
            retain(name,Object.hasOwn(observed,name)?observed[name]:reported[name]);
        retain('total_ppo_updates',reported.total_ppo_updates);
        // Spare chart slots still show observed host resources. The
        // operator's requested metrics retain first priority; unrelated
        // trainer fields remain out of an explicitly selected view.
        for(const [name,value] of Object.entries(observed))retain(name,value);
    }else {
        // Without an explicit preference, retain observed host resources
        // before filling the remaining slots from the trusted trainer data.
        for(const [name,value] of Object.entries(observed))retain(name,value);
        retain('total_ppo_updates',reported.total_ppo_updates);
        for(const [name,value] of Object.entries(reported))retain(name,value);
    }
    if(!count)
        metrics.project_steps_completed={value:stepsCompleted,unit:'count'};
    return metrics;
}
function validateProgress(value,projectId) {
    return validateGenericProgress(value,projectId);
}
module.exports={validateProgress,selectChartMetrics};
