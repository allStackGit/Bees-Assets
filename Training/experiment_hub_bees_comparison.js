'use strict';

// Read-only bridge from the authoritative Bees experiment manager's existing
// sampled telemetry into the same project-neutral Compare presentation used by
// native projects. It does not create a second Bees scheduler or trainer.
const FIELD=/^[a-z][a-z0-9_]{0,39}$/;
const LIMIT=512;
function summary(job) {
    return {
        id:job.id,project_id:'bees',configuration_name:job.name||job.id,
        status:job.status,assigned_machine_id:'bees-operator',
        created_at:job.created_at,
        metric_selection:job.metric_selection||[],
        configuration_sha256:job.configuration_sha256||null,
        artifact_lineage_root_id:job.id,
        continuation_of_job_id:null,
        capacity_released_at:['completed','stopped','ended_early'].includes(job.status)
            ?job.finished_at||job.stopped_at||null:null,
        native_bees_run_id:job.run_id||null,
    };
}
function numeric(metrics,key,value,unit='scalar') {
    if(!FIELD.test(key)||typeof value!=='number'||
        !Number.isFinite(value)||Math.abs(value)>1e12)return;
    if(unit==='rate'&&value<0)return;
    metrics[key]={value,unit};
}
function point(sample,previous) {
    if(!Number.isSafeInteger(sample.step)||sample.step<0||
        !Number.isFinite(Date.parse(sample.at)))return null;
    const metrics={};
    // Original names preserve established dashboards and research notebooks.
    for(const [key,value] of Object.entries(sample)) {
        if(key==='step'||key==='stage_index'||key==='training_s')continue;
        numeric(metrics,key,value,key.endsWith('steps_per_second')?'rate':'scalar');
    }
    for(const field of ['ppo','pipeline']) {
        for(const [key,value] of Object.entries(sample[field]||{})) {
            const name=field+'_'+key;
            numeric(metrics,name,value,Number.isFinite(value)&&value>=0&&
                (name.endsWith('_sps')||name.endsWith('_per_second'))?'rate':'scalar');
        }
    }
    numeric(metrics,'training_time_seconds',sample.training_s,'seconds');
    const selected=Object.fromEntries(Object.entries(metrics).slice(0,96));
    if(!Object.keys(selected).length)return null;
    const stage=Number.isSafeInteger(sample.stage_index)?sample.stage_index:0;
    const at=new Date(sample.at).toISOString();
    return {
        observed_at:at,source_modified_at:at,
        experiment_id:sample.experiment_id||'bees_run',
        segment_id:'stage_'+stage,
        steps_completed:sample.step,metrics:selected,
        break_before:Boolean(previous&&(
            previous.segment_id!=='stage_'+stage||
            sample.step<previous.steps_completed)),
    };
}
function comparison(items) {
    const samples=[];
    for(const item of items) {
        const source=(item.samples||[]).filter(sample=>
            sample&&Number.isSafeInteger(sample.step)&&
            Number.isFinite(Date.parse(sample.at)));
        if(!source.length)continue;
        // Retain first/last and a uniformly spread historical series.
        // Full-resolution legacy samples remain in the Bees Metrics archive.
        const indices=new Set([0,source.length-1]);
        if(source.length<=LIMIT)for(let i=0;i<source.length;i++)indices.add(i);
        else for(let i=1;i<LIMIT-1;i++)
            indices.add(Math.floor(i*(source.length-1)/(LIMIT-1)));
        const points=[];
        for(const i of [...indices].sort((a,b)=>a-b)) {
            const next=point(source[i],points[points.length-1]);
            if(next)points.push(next);
        }
        if(points.length)samples.push({job_id:item.id,points});
    }
    return {
        project_id:'bees',source:'legacy-bees-metrics-archive',
        max_tracked_jobs:items.length,max_samples_per_job:LIMIT,
        ephemeral:false,storage_warning:null,samples,
    };
}
module.exports={comparison,summary};
