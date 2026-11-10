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
function point(sample,previous,preferred=[]) {
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
            const unit=field==='pipeline'
                ?(['staged_s','accepted_s','consumed_s'].includes(key)
                    ?'rate':'scalar')
                :field==='ppo'
                    ?key==='minibatches_s'?'rate':
                        ['vram_used_max_mib','rss_max_mib'].includes(key)?'mb':
                        ['update_s','materialize_s','backward_s',
                        'optimizer_step_s','cpu_prepare_wait_s',
                        'avg_minibatch_s','optimizer_work_per_minibatch_s',
                        'optimizer_work_estimated_s','diagnostic_profile_s',
                        'minibatch_p50_s','minibatch_p90_s','minibatch_max_s'
                        ].includes(key)?'seconds':'scalar'
                    :'scalar';
            numeric(metrics,name,value,unit);
        }
    }
    numeric(metrics,'training_time_seconds',sample.training_s,'seconds');
    // When the wire limit is reached, keep recorded measurements explicitly
    // chosen by the experiment before filling remaining slots from the source.
    const names=[...new Set([
        ...preferred.filter(key=>Object.hasOwn(metrics,key)),
        ...Object.keys(metrics),
    ])].slice(0,96);
    const selected=Object.fromEntries(names.map(key=>[key,metrics[key]]));
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
            const next=point(source[i],points[points.length-1],
                Array.isArray(item.metric_selection)?item.metric_selection:[]);
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
