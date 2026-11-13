'use strict';

// Small shared wire contract. Progress is observational, not qualification
// evidence, a scheduler criterion or an authorization to release resources.
const safeInt=n=>Number.isSafeInteger(n)&&n>=0;
// Optional project-neutral summaries from a locally trusted adapter.
// Values and units are explicit; neither metrics nor steps authorize
// training, benchmark decisions, lease release, or policy promotion.
const GENERIC_METRIC=/^[a-z][a-z0-9_]{0,39}$/;
const GENERIC_UNITS=new Set(['scalar','ratio','count','rate','seconds','mb','bytes','milliseconds','watts']);
// Keep the bounded source envelope larger than the 96-metric chart cache.
// The bounded source projection preserves core measurements for up to 17
// collectors and reserves room for sustained coordinator-side diagnosis.
// Complete training history and full-job exports remain authoritative.
// Complete trainer metrics reach the archive before chart downselection.
const MAX_SOURCE_METRICS=512;
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
        retain('ant_decisions_per_second',reported.ant_decisions_per_second);
        retain('simulation_match_steps_per_second',
            reported.simulation_match_steps_per_second);
        for(const name of ['ppo_gpu_batch_target_pixels',
            'ppo_gpu_batch_next_target','ppo_gpu_batch_settled',
            'rollout_gpu_batch_target_pixels',
            'rollout_gpu_batch_next_target','rollout_gpu_batch_settled',
            'rollout_gpu_batch_proven_target',
            'ppo_gpu_batch_proven_target',
            'ppo_gpu_stream_work_share',
            'remote_policy_shared_collectors',
            'remote_policy_shared_encode_seconds',
            'remote_policy_shared_bytes',
            'local_lease_ram_charged_mb','local_lease_ram_headroom_mb',
            'local_gpu_collectors_pending_at_local_end',
            'collector_join_wait_seconds','local_gpu_join_wait_seconds',
            'rollout_onnx_batch_share','rollout_onnx_calls',
            'rollout_onnx_batches','rollout_onnx_states',
            'rollout_opponent_forward_chunks'])
            retain(name,reported[name]);
        for(const name of ['training_end_to_end_steps_per_second',
            'training_useful_steps_per_second',
            'training_concurrency_limit','training_concurrency_next_limit',
            'training_tuner_adjustments','training_tuner_accepted',
            'training_tuner_settled',
            'training_gpu_optimizer_lineages',
            'training_gpu_optimizer_next_lineages',
            'training_gpu_devices_assigned',
            'training_gpu_devices_active',
            'training_gpu_device_0_lineages',
            'training_gpu_device_1_lineages',
            'training_gpu_device_0_peak_allocated_mb',
            'training_gpu_device_1_peak_allocated_mb',
            'training_gpu_stream_max_seconds',
            'training_gpu_state_reused_lineages',
            'training_gpu_state_cache_mb',
            'training_inference_max_seconds','training_ingestion_max_seconds',
            'training_inference_slowest_worker',
            'training_ppo_data_slowest_worker',
            'training_ppo_data_max_seconds','training_padding_sync_max_seconds',
            'training_gpu_metric_readback_max_seconds',
            'training_gpu_optimizer_peak_allocated_mb',
            'training_cpu_parallelism','training_queue_max_seconds',
            'training_checkpoint_snapshot_seconds',
            'training_checkpoint_max_worker_seconds',
            'training_reply_gap_seconds','training_last_reply_worker'])
            retain(name,reported[name]);
        // Show measured stage pressure before unrelated high-cardinality
        // project metrics consume the default 96-chart-field cache.
        for(const name of ['pipeline_cpu_lease_pressure',
            'pipeline_cpu_throttle_pressure','pipeline_ram_lease_pressure',
            'pipeline_gpu_host_busy_pressure',
            'pipeline_gpu_mixed_host_evidence','pipeline_gpu_host_busy_device_share',
            'pipeline_cpu_underuse_evidence','pipeline_gpu_idle_evidence',
            'pipeline_remote_wait_dominant',
            'pipeline_local_gpu_wait_dominant',
            'pipeline_local_gpu_wait_work_share',
            'training_queue_pressure','training_final_join_pressure',
            'training_parallelism_underuse_evidence',
            'training_queue_work_share','training_reply_gap_work_share',
            'training_timing_sample_count',
            'pipeline_merge_dominant','pipeline_collection_dominant',
            'pipeline_optimization_dominant','pipeline_remote_wait_work_share',
            'pipeline_merge_work_share','pipeline_collection_work_share',
            'pipeline_optimization_work_share','pipeline_timing_sample_count',
            'rollout_engine_measured_work_share',
            'rollout_inference_measured_work_share'])
            retain(name,reported[name]);
        // At most one sustained stage can dominate each fully instrumented
        // collector. Give confirmed per-worker evidence a default chart slot.
        for(let i=0;i<17;i++) {
            for(const suffix of ['join_delay_pressure',
                'sim_straggler_pressure','sim_dispatch_pressure',
                'record_encode_pressure','prev_reply_prep_pressure'])
                retain('collector_'+i+'_'+suffix,reported['collector_'+i+'_'+suffix]);
            for(const stage of ['engine','observation','inference','opponent'])
                retain('collector_'+i+'_stage_'+stage+'_dominant',
                    reported['collector_'+i+'_stage_'+stage+'_dominant']);
        }
        // Show per-collector batching coverage without treating a static
        // ONNX model or many narrow boards as a hardware bottleneck.
        for(let i=0;i<17;i++) {
            retain('collector_'+i+'_is_local_gpu',
                reported['collector_'+i+'_is_local_gpu']);
            retain('collector_'+i+'_gpu_ordinal',
                reported['collector_'+i+'_gpu_ordinal']);
        }
        for(let i=0;i<17;i++)
            retain('collector_'+i+'_onnx_batch_share',
                reported['collector_'+i+'_onnx_batch_share']);
        // With many independent recurrent PPO lineages, retain sustained
        // limiting stages rather than the first dozens of raw counters.
        for(let i=0;i<64;i++)
            for(const stage of ['collection','optimization','ppo_data',
                'simulation','observation','inference','ingestion',
                'collection_other'])
                retain('worker_'+i+'_'+stage+'_dominant',
                    reported['worker_'+i+'_'+stage+'_dominant']);
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
    enrich(job,progress,observed={}) {
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
        const history=()=>{
            const version=metrics.training_environment_version;
            const environment=(version?.unit==='count'&&
                Number.isSafeInteger(version.value))
                ?':environment-'+version.value:'';
            const identity=String(job.dispatch_id||'')+':'+progress.segment_id+
                environment;
            const at=Date.parse(progress.source_modified_at);
            let prior=this.byJob.get(job.id);
            if(!prior||prior.identity!==identity||at<prior.lastAt) {
                prior={identity,lastAt:-Infinity,samples:[]};
                if(this.byJob.size>=128&&!this.byJob.has(job.id))
                    this.byJob.delete(this.byJob.keys().next().value);
                this.byJob.set(job.id,prior);
            }
            return {at,prior};
        };
        // These are trusted, authenticated native-job/cgroup observations.
        // GPU occupancy remains HOST-wide even with an exclusive Hub lease.
        // It cannot certify useful per-job GPU work or exclude outside load.
        const percent=name=>{
            const item=observed?.[name];
            return item?.unit==='scalar'&&Number.isFinite(item.value)&&
                item.value>=0&&item.value<=1000?item.value:null;
        };
        const cpu=percent('job_cpu_lease_utilization_pct');
        const throttle=percent('job_cpu_quota_throttled_periods_pct');
        const ram=percent('job_memory_lease_utilization_pct');
        const gpuCount=job.resources?.gpu_count;
        const eligibleGpu=job.resources?.gpu_mode==='exclusive'&&
            Number.isSafeInteger(gpuCount)&&gpuCount>0&&gpuCount<=32;
        const gpuBusy=eligibleGpu
            ?Array.from({length:gpuCount},(_,i)=>percent(
                'host_gpu_'+i+'_busy_pct')):[];
        const allGpuKnown=gpuBusy.length>0&&gpuBusy.every(x=>x!==null);
        if(allGpuKnown)
            // Share of *assigned physical devices* observed at high host
            // occupancy; not a per-job GPU utilization measurement.
            add('pipeline_gpu_host_busy_device_share',
                gpuBusy.filter(x=>x>=90).length/gpuBusy.length);
        const resourceHints={
            cpuBusy:cpu!==null&&cpu>=90,
            cpuIdle:cpu!==null&&cpu<=40,
            throttled:throttle!==null&&throttle>=10,
            ramNear:ram!==null&&ram>=90,
            gpuBusy:allGpuKnown&&gpuBusy.every(x=>x>=90),
            gpuIdle:allGpuKnown&&gpuBusy.every(x=>x<=30),
            gpuMixed:allGpuKnown&&gpuBusy.length>1&&
                gpuBusy.some(x=>x>=90)&&gpuBusy.some(x=>x<=30)
        };
        const collection=seconds('collection_seconds');
        const optimize=seconds('optimization_seconds');
        if(collection!==null&&optimize!==null&&collection+optimize>0) {
            const fraction=collection/(collection+optimize);
            add('pipeline_collection_work_share',fraction);
            add('pipeline_optimization_work_share',1-fraction);
            // Worker straggler waits and learner-side merge are distinct
            // measured subsets of collection, not new hardware diagnoses.
            const joinWait=seconds('remote_collector_join_wait_seconds');
            const localGpuWait=seconds('local_gpu_join_wait_seconds');
            const merge=seconds('collection_merge_seconds');
            const waitShare=collection>0&&joinWait!==null&&joinWait<=collection
                ?joinWait/collection:null;
            const mergeShare=collection>0&&merge!==null&&merge<=collection
                ?merge/collection:null;
            const localShare=collection>0&&localGpuWait!==null&&
                localGpuWait<=collection?localGpuWait/collection:null;
            if(waitShare!==null)add('pipeline_remote_wait_work_share',waitShare);
            if(localShare!==null)
                add('pipeline_local_gpu_wait_work_share',localShare);
            if(mergeShare!==null)add('pipeline_merge_work_share',mergeShare);
            // Bounded observational state is neither a scheduler input
            // nor evidence that any particular device is saturated.
            const {at,prior}=history();
            // Per-collector stage times are independent *wall intervals*.
            // Compare only a complete set from one collector. A dominant
            // measured stage is not evidence that a specific CPU/GPU is full.
            const stageNames=['engine','observation','inference','opponent'];
            const sourceNames=['rollout_engine_step_seconds',
                'observation_prepare_seconds','learner_inference_seconds',
                'opponent_processing_seconds'];
            const stageHints=[];
            const simHints=[];
            const joinHints=[];
            for(let i=0;i<17;i++) {
                const batch=seconds('collector_'+i+'_sim_batch_wall_seconds');
                const tail=seconds('collector_'+i+'_sim_last_child_gap_seconds');
                const queue=seconds('collector_'+i+'_sim_dispatch_queue_seconds');
                const worker=seconds('collector_'+i+'_sim_engine_roundtrip_seconds');
                const tailShare=(batch>0&&tail!==null&&tail<=batch)
                    ?tail/batch:null;
                const exposed=seconds('collector_'+i+'_rollout_engine_step_seconds');
                const early=metrics['collector_'+i+'_sim_prefetch_engine_steps'];
                const prefetched=early?.unit==='count'&&early.value>0;
                const exposedShare=(batch>0&&exposed!==null&&exposed<=batch)
                    ?exposed/batch:null;
                const queueShare=(worker!==null&&queue!==null&&worker+queue>0)
                    ?queue/(worker+queue):null;
                if(tailShare!==null)
                    add('collector_'+i+'_sim_last_child_gap_share',tailShare);
                if(exposedShare!==null)
                    add('collector_'+i+'_sim_exposed_wait_share',exposedShare);
                if(queueShare!==null)
                    add('collector_'+i+'_sim_dispatch_queue_share',queueShare);
                if(batch>0&&worker!==null)
                    // Sum of parallel engine request durations divided by
                    // batch wall: in-flight request overlap, not GPU/CPU busy.
                    add('collector_'+i+'_sim_inflight_factor',
                        worker/batch,'scalar');
                const packing=seconds('collector_'+i+'_record_encode_seconds');
                const service=seconds('collector_'+i+'_service_seconds');
                const packingShare=(service>0&&packing!==null&&packing<=service)
                    ?packing/service:null;
                if(packingShare!==null)
                    add('collector_'+i+'_record_encode_work_share',packingShare);
                // A reply cannot contain its own serialization timing, so
                // these measurements are authenticated on the NEXT update.
                // Compare only stages from the same previous reply; do not
                // subtract them from the current update's service interval.
                const priorUpdate=metrics['collector_'+i+'_prev_reply_update'];
                const priorPrep=seconds('collector_'+i+'_prev_reply_prepare_seconds');
                const priorSend=seconds('collector_'+i+'_prev_reply_send_seconds');
                const priorService=seconds('collector_'+i+'_prev_reply_service_seconds');
                const priorValid=priorUpdate?.unit==='count'&&
                    Number.isSafeInteger(priorUpdate.value)&&
                    priorPrep!==null&&priorSend!==null&&priorService!==null;
                const priorTotal=priorValid?priorPrep+priorSend:0;
                const prepShare=priorTotal>0?priorPrep/priorTotal:null;
                // Compare reply encoding to the SERVICE interval of the SAME
                // previous update, not to the next update's processing time.
                const prepWorkShare=priorTotal+priorService>0&&priorValid
                    ?priorPrep/(priorTotal+priorService):null;
                if(prepShare!==null)
                    add('collector_'+i+'_prev_reply_prep_share',prepShare);
                if(prepWorkShare!==null)
                    add('collector_'+i+'_prev_reply_prep_work_share',prepWorkShare);
                const joinExposed=seconds('collector_'+i+'_join_exposed_seconds');
                const joinShare=(collection>0&&joinExposed!==null&&
                    joinExposed<=collection)?joinExposed/collection:null;
                if(joinShare!==null)
                    add('collector_'+i+'_join_exposed_share',joinShare);
                // An individual remote collector is a sustained barrier
                // only when its authenticated, learner-clock completion is
                // late relative to local collection, not just because the
                // coordinator read its future in a particular order.
                joinHints[i]=joinExposed>=0.5&&joinShare>=0.20;
                simHints[i]={
                    // With early dispatch, the slowest-child completion
                    // tail may have been hidden under opponent preparation.
                    // Diagnose an actual collection stall only if the
                    // remaining join wait also occupies substantial time.
                    tail:tailShare!==null&&tailShare>=0.25&&
                        (!prefetched||(exposedShare!==null&&exposedShare>=0.25)),
                    queue:queueShare!==null&&queueShare>=0.20,
                    packing:packingShare!==null&&packingShare>=0.30,
                    // A sustained expensive worker reply preparation stage
                    // suggests codec/serialization work, not network or GPU
                    // saturation. Require both substantial reply time AND
                    // a meaningful fraction of the same worker update.
                    replyPrep:priorTotal>=0.5&&prepShare>=0.70&&
                        prepWorkShare>=0.25
                };
                const inferenceHost=seconds('collector_'+i+'_learner_inference_seconds');
                const inferenceStream=seconds('collector_'+i+'_learner_gpu_stream_seconds');
                // Stream events bracket GPU dispatch, sampling and kernel
                // gaps; host wall also includes copies and record creation.
                // The difference is not proof of GPU or network saturation.
                if(inferenceHost>0&&inferenceStream!==null&&
                    inferenceStream<=inferenceHost)
                    add('collector_'+i+'_inference_gpu_stream_share',
                        inferenceStream/inferenceHost);
                const durations=sourceNames.map(name=>
                    seconds('collector_'+i+'_'+name));
                if(durations.some(x=>x===null))continue;
                const total=durations.reduce((sum,x)=>sum+x,0);
                if(total<=0)continue;
                const maximum=Math.max(...durations);
                const stage=durations.indexOf(maximum);
                add('collector_'+i+'_max_stage_work_share',maximum/total);
                if(maximum/total>=0.65)
                    stageHints[i]=stage;
            }
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,fraction,waitShare,localShare,mergeShare,stageHints,simHints,
                    joinHints,resourceHints});
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
                if(recent.filter(x=>x.waitShare!==null&&x.waitShare>=0.5).length>=5)
                    add('pipeline_remote_wait_dominant',1,'count');
                if(recent.filter(x=>x.localShare!==null&&x.localShare>=0.5).length>=5)
                    add('pipeline_local_gpu_wait_dominant',1,'count');
                if(recent.filter(x=>x.mergeShare!==null&&x.mergeShare>=0.3).length>=5)
                    add('pipeline_merge_dominant',1,'count');
                // Cross-stage capacity evidence requires *sustained* host
                // data. Observations are independent and can overlap:
                // saturation, throttling and inefficient software may coexist.
                const sustained=name=>recent.filter(
                    x=>x.resourceHints?.[name]).length>=5;
                if(sustained('cpuBusy'))
                    add('pipeline_cpu_lease_pressure',1,'count');
                if(sustained('throttled'))
                    add('pipeline_cpu_throttle_pressure',1,'count');
                if(sustained('ramNear'))
                    add('pipeline_ram_lease_pressure',1,'count');
                if(sustained('gpuBusy'))
                    add('pipeline_gpu_host_busy_pressure',1,'count');
                if(sustained('gpuMixed'))
                    add('pipeline_gpu_mixed_host_evidence',1,'count');
                if(sustained('cpuIdle')&&
                        recent.filter(x=>x.fraction>=0.7).length>=5)
                    add('pipeline_cpu_underuse_evidence',1,'count');
                if(sustained('gpuIdle')&&
                        recent.filter(x=>x.fraction<=0.3).length>=5)
                    add('pipeline_gpu_idle_evidence',1,'count');
                for(let i=0;i<17;i++) {
                    // The reported collector ID is a stable position in
                    // the frozen remote fanout, not an inferred host label.
                    if(recent.filter(x=>x.joinHints?.[i]).length>=5)
                        add('collector_'+i+'_join_delay_pressure',1,'count');
                    // Stable straggler tails or dispatch queueing suggest
                    // software parallelism pressure, not CPU saturation.
                    if(recent.filter(x=>x.simHints?.[i]?.tail).length>=5)
                        add('collector_'+i+'_sim_straggler_pressure',1,'count');
                    if(recent.filter(x=>x.simHints?.[i]?.queue).length>=5)
                        add('collector_'+i+'_sim_dispatch_pressure',1,'count');
                    if(recent.filter(x=>x.simHints?.[i]?.packing).length>=5)
                        add('collector_'+i+'_record_encode_pressure',1,'count');
                    if(recent.filter(x=>x.simHints?.[i]?.replyPrep).length>=5)
                        add('collector_'+i+'_prev_reply_prep_pressure',1,'count');
                    for(let stage=0;stage<stageNames.length;stage++) {
                        if(recent.filter(x=>x.stageHints?.[i]===stage).length>=5)
                            add('collector_'+i+'_stage_'+stageNames[stage]+
                                '_dominant',1,'count');
                    }
                }
            }
        } else if(seconds('training_batch_wall_seconds')>0&&
            metrics.training_lineage_workers?.unit==='count'&&
            Number.isSafeInteger(metrics.training_lineage_workers.value)&&
            metrics.training_lineage_workers.value>=1&&
            metrics.training_lineage_workers.value<=64) {
            // Courier trains independent recurrent-PPO policy lineages.
            // Wall intervals overlap: compare each worker's own stages and
            // use the coordinator's actual finished-at timings for waits.
            const count=metrics.training_lineage_workers.value;
            const wall=seconds('training_batch_wall_seconds');
            const queue=seconds('training_queue_max_seconds');
            const gap=seconds('training_reply_gap_seconds');
            const queueShare=queue!==null&&queue<=wall?queue/wall:null;
            const gapShare=gap!==null&&gap<=wall?gap/wall:null;
            if(queueShare!==null)add('training_queue_work_share',queueShare);
            if(gapShare!==null)add('training_reply_gap_work_share',gapShare);
            const stageHints=[];
            // The backend reports its measured worst recurrent-minibatch
            // preparation alongside the *owning* lineage index. Comparing
            // unrelated maxima from different workers is not causal evidence.
            const dataOwner=metrics.training_ppo_data_slowest_worker;
            const dataOwnerIndex=(dataOwner?.unit==='count'&&
                Number.isSafeInteger(dataOwner.value)&&
                dataOwner.value>=0&&dataOwner.value<count)
                ?dataOwner.value:null;
            const dataWorst=seconds('training_ppo_data_max_seconds');
            for(let i=0;i<count;i++) {
                const collect=seconds('worker_'+i+'_collection_seconds');
                const optimize=seconds('worker_'+i+'_optimization_seconds');
                if(collect===null||optimize===null||collect+optimize<=0)
                    continue;
                // Simulation is nested in rollout collection; observation
                // preparation is nested in simulation. These wall intervals
                // must never be summed as independent elapsed stages.
                const simulation=seconds('worker_'+i+'_simulation_seconds');
                const observation=seconds('worker_'+i+'_observation_seconds');
                const inference=seconds('worker_'+i+'_inference_seconds');
                const ingestion=seconds('worker_'+i+'_ingestion_seconds');
                const fraction=collect/(collect+optimize);
                const dataShare=(i===dataOwnerIndex&&dataWorst!==null&&
                    optimize>0&&dataWorst<=optimize*1.02)
                    ?dataWorst/optimize:null;
                const simShare=(collect>0&&simulation!==null&&
                    simulation<=collect)?simulation/collect:null;
                const obsShare=(simulation>0&&observation!==null&&
                    observation<=simulation)?observation/simulation:null;
                if(simShare!==null)
                    add('worker_'+i+'_simulation_work_share',simShare);
                if(obsShare!==null)
                    add('worker_'+i+'_observation_work_share',obsShare);
                // The new CPU inference, environment and buffer-ingestion
                // intervals are siblings inside collection, NOT children
                // of one another. Observation is a child of simulation.
                // Require complete and non-overlapping accounting before
                // attributing the residual to Python/recurrent coordination.
                // A 40/40 simulation/inference split is genuinely mixed;
                // report broad collection pressure, not a guessed cause.
                let cause=null;
                if(fraction>=0.70&&simulation!==null&&
                    inference!==null&&ingestion!==null&&collect>0&&
                    simulation+inference+ingestion<=collect*1.02) {
                    const sources=[['simulation',simulation/collect],
                        ['inference',inference/collect],
                        ['ingestion',ingestion/collect]];
                    sources.sort((a,b)=>b[1]-a[1]);
                    if(sources[0][1]>=0.50) {
                        cause=sources[0][0];
                        if(cause==='simulation'&&obsShare!==null&&
                            obsShare>=0.60)cause='observation';
                    } else if((collect-simulation-inference-ingestion)/collect>=0.50) {
                        cause='collection_other';
                    }
                } else if(fraction>=0.70&&inference===null&&
                    ingestion===null&&simShare!==null) {
                    // Historical Courier sources lacked separate inference
                    // and ingestion intervals. Keep only their supported
                    // older simulation/observation evidence.
                    if(simShare>=0.65)
                        cause=obsShare!==null&&obsShare>=0.60
                            ?'observation':'simulation';
                    else if(simShare<=0.35)cause='collection_other';
                }
                stageHints[i]={
                    collection:fraction>=0.70,
                    optimization:fraction<=0.30,
                    ppo_data:fraction<=0.30&&dataShare!==null&&dataShare>=0.50,
                    simulation:cause==='simulation',
                    observation:cause==='observation',
                    inference:cause==='inference',
                    ingestion:cause==='ingestion',
                    collection_other:cause==='collection_other'
                };
            }
            const {at,prior}=history();
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,stageHints,resourceHints,
                    courierQueue:queueShare!==null&&queueShare>=0.20,
                    // With queued lineages, the last completion may be a
                    // normal admission wave rather than a slow worker.
                    courierTail:gapShare!==null&&gapShare>=0.25&&
                        (queueShare===null||queueShare<=0.10)});
                if(prior.samples.length>8)prior.samples.shift();
                prior.lastAt=at;
            }
            const recent=prior.samples.slice(-6);
            add('training_timing_sample_count',recent.length,'count');
            if(recent.length>=5&&
                recent.at(-1).at-recent[0].at>=120000) {
                const sustained=fn=>recent.filter(fn).length>=5;
                if(sustained(x=>x.courierQueue))
                    add('training_queue_pressure',1,'count');
                if(sustained(x=>x.courierTail))
                    add('training_final_join_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.cpuBusy))
                    add('pipeline_cpu_lease_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.throttled))
                    add('pipeline_cpu_throttle_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.ramNear))
                    add('pipeline_ram_lease_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.cpuIdle)&&
                    sustained(x=>x.courierQueue))
                    add('training_parallelism_underuse_evidence',1,'count');
                for(let i=0;i<count;i++)
                    for(const stage of ['collection','optimization','ppo_data',
                        'simulation','observation','inference','ingestion',
                        'collection_other'])
                        if(sustained(x=>x.stageHints?.[i]?.[stage]))
                            add('worker_'+i+'_'+stage+'_dominant',1,'count');
            }
        } else {
            // No comparable completed stage timing: do not retain a stale
            // diagnosis across project changes or instrumentation versions.
            this.byJob.delete(job.id);
        }
        const streamPpo=seconds('ppo_gpu_stream_interval_seconds');
        const hostPpo=seconds('optimization_seconds');
        if(hostPpo>0&&streamPpo!==null&&streamPpo<=hostPpo)
            add('ppo_gpu_stream_work_share',streamPpo/hostPpo);
        const engine=seconds('rollout_engine_step_seconds');
        const inference=seconds('rollout_learner_inference_seconds');
        if(engine!==null&&inference!==null&&engine+inference>0) {
            add('rollout_engine_measured_work_share',engine/(engine+inference));
            add('rollout_inference_measured_work_share',inference/(engine+inference));
        }
        // Only an independently timed remote service interval can separate
        // roundtrip overhead. Remote 'elapsed_seconds' equals roundtrip in
        // the existing TinyBrains source and must never be subtracted from it.
        // Residual includes transport, framing, serialization and queueing,
        // NOT a measurement of network bandwidth alone.
        for(let i=0;i<17;i++) {
            const roundtrip=seconds('collector_'+i+'_roundtrip_seconds');
            const service=seconds('collector_'+i+'_service_seconds');
            if(roundtrip!==null&&service!==null&&roundtrip>0&&service<=roundtrip)
                add('collector_'+i+'_turnaround_overhead_share',
                    (roundtrip-service)/roundtrip);
        }
        // Optional derived measures must not crowd out project-owned source
        // metrics or violate the existing bounded wire contract.
        const available=Math.max(0,MAX_SOURCE_METRICS-Object.keys(metrics).length);
        // Confirmed, sustained stage limitations matter more than optional
        // continuous ratios when many collectors approach the wire limit. Never evict a project-owned source metric for a diagnosis.
        const priority=name=>
            name.startsWith('pipeline_')&&(
                name.endsWith('_pressure')||name.endsWith('_evidence'))?0:
            name.startsWith('pipeline_')&&name.endsWith('_dominant')?0:
            // Keep evidence from every machine before filling the report
            // with a second or third warning from the first collector.
            name.endsWith('_join_delay_pressure')?1:
            name.endsWith('_sim_straggler_pressure')?2:
            name.endsWith('_sim_dispatch_pressure')?3:
            name.endsWith('_record_encode_pressure')?4:
            name.endsWith('_pressure')?5:
            name.endsWith('_dominant')?6:
            name==='pipeline_timing_sample_count'?7:8;
        const additions=Object.entries(derived).sort(
            ([a],[b])=>priority(a)-priority(b)).slice(0,available);
        // A heavily instrumented job can approach the 32-KiB report limit.
        // Retain confirmed bottlenecks first and then as many of the
        // remaining measurements as fit, without widening the envelope.
        while(additions.length) {
            const enriched={...progress,metrics:{
                ...metrics,...Object.fromEntries(additions)}};
            if(JSON.stringify(enriched).length<=32768)return enriched;
            additions.pop();
        }
        return progress;
    }
}
function validateProgress(value,projectId) {
    return validateGenericProgress(value,projectId);
}
module.exports={validateProgress,selectChartMetrics,BottleneckEvidence};
