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
const MAX_SOURCE_METRICS=256;
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
// Existing hub-metrics-v1 fields remain the sole public telemetry path.
// Shares compare measured stage work; they are *not* hardware utilization or
// independent capacity benchmarks. Unreported timing cannot be guessed.
class BottleneckEvidence {
    constructor() { this.byJob=new Map(); }
    enrich(job,progress) {
        if(!progress||!job?.id)return progress;
        const metrics=progress.metrics||{};
        const derived={};
        const seconds=name=>metrics[name]?.unit==='seconds'&&
            Number.isFinite(metrics[name].value)&&metrics[name].value>=0
                ?metrics[name].value:null;
        const add=(name,value,unit='ratio')=>{
            if(Number.isFinite(value)&&value>=0&&value<=1e12&&
                (unit!=='ratio'||value<=1))
                derived[name]={value,unit};
        };
        const collection=seconds('collection_seconds');
        const optimize=seconds('optimization_seconds');
        if(collection!==null&&optimize!==null&&collection+optimize>0) {
            const fraction=collection/(collection+optimize);
            add('pipeline_collection_work_share',fraction);
            add('pipeline_optimization_work_share',1-fraction);
            const identity=String(job.dispatch_id||'')+':'+progress.segment_id;
            const at=Date.parse(progress.source_modified_at);
            let prior=this.byJob.get(job.id);
            if(!prior||prior.identity!==identity||at<prior.lastAt) {
                prior={identity,lastAt:-Infinity,samples:[]};
                // Bounded, observational evidence is never persisted as
                // training state or used to change allocations.
                if(this.byJob.size>=128&&!this.byJob.has(job.id))
                    this.byJob.delete(this.byJob.keys().next().value);
                this.byJob.set(job.id,prior);
            }
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,fraction});
                if(prior.samples.length>8)prior.samples.shift();
                prior.lastAt=at;
            }
            const recent=prior.samples.slice(-6);
            add('pipeline_timing_sample_count',recent.length,'count');
            if(recent.length>=5&&
                recent.at(-1).at-recent[0].at>=120000) {
                // A five-of-six majority plus a large difference is far
                // less sensitive to one noisy update than instantaneous SPS.
                if(recent.filter(x=>x.fraction>=0.7).length>=5)
                    add('pipeline_collection_dominant',1,'count');
                else if(recent.filter(x=>x.fraction<=0.3).length>=5)
                    add('pipeline_optimization_dominant',1,'count');
            }
        } else {
            // Don't carry a prior timing conclusion into a source version
            // which no longer provides comparable completed stage timings.
            this.byJob.delete(job.id);
        }
        const engine=seconds('rollout_engine_step_seconds');
        const inference=seconds('rollout_learner_inference_seconds');
        if(engine!==null&&inference!==null&&engine+inference>0) {
            add('rollout_engine_measured_work_share',engine/(engine+inference));
            add('rollout_inference_measured_work_share',inference/(engine+inference));
        }
        // Round-trip overhead includes transport, queueing and coordinator
        // work. It is NOT a measurement of network bandwidth alone.
        for(let i=0;i<9;i++) {
            const roundtrip=seconds('collector_'+i+'_roundtrip_seconds');
            const elapsed=seconds('collector_'+i+'_elapsed_seconds');
            if(roundtrip!==null&&elapsed!==null&&roundtrip>0&&elapsed<=roundtrip)
                add('collector_'+i+'_turnaround_overhead_share',
                    (roundtrip-elapsed)/roundtrip);
        }
        // Optional derived measures must not crowd out project-owned source
        // metrics or violate the existing bounded wire contract.
        const available=Math.max(0,256-Object.keys(metrics).length);
        const extras=Object.fromEntries(Object.entries(derived).slice(0,available));
        const enriched={...progress,metrics:{...metrics,...extras}};
        return JSON.stringify(enriched).length<=32768?enriched:progress;
    }
}
function validateProgress(value,projectId) {
    return validateGenericProgress(value,projectId);
}
module.exports={validateProgress,selectChartMetrics,BottleneckEvidence};
