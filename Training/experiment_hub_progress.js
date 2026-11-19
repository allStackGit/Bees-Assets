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
// Nine fully instrumented collectors can publish almost 400 stage,
// resource and outcome measurements. The unchanged 32-KiB serialized
// envelope still bounds payload size, separately from this field limit.
// Complete trainer metrics reach the archive before chart downselection.
const MAX_SOURCE_METRICS=512;
const MAX_TINYBRAINS_COLLECTORS=17;
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
            // Report exposed CPU-prep wait, not concurrent background work
            // as a separate portion of whole-update wall.
            'ppo_prep_wait_seconds','ppo_prep_cpu_seconds',
            'ppo_prep_background_chunks',
            'ppo_prep_background_cpu_seconds',
            'ppo_prep_background_wait_seconds',
            'ppo_prep_wait_work_share',
            'pipeline_ppo_prep_wait_pressure',
            'ppo_prep_async_h2d_bytes',
            'rollout_gpu_batch_target_pixels',
            'rollout_gpu_batch_next_target','rollout_gpu_batch_settled',
            'rollout_gpu_batch_proven_target',
            'ppo_gpu_batch_proven_target',
            'rollout_opponent_gpu_stream_seconds',
            'rollout_fused_gpu_stream_seconds',
            'rollout_opponent_joined_learner_readbacks',
            'rollout_opponent_readback_slot_reused',
            'rollout_opponent_readback_slot_allocated',
            'rollout_opponent_readback_cached_indices',
            'rollout_opponent_grouped_calls',
            'rollout_opponent_grouped_models',
            'rollout_opponent_grouped_states',
            'rollout_opponent_grouped_input_bytes',
            'rollout_learner_grouped_forwards_saved',
            'rollout_learner_grouped_states',
            'rollout_opponent_grouped_padding',
            'rollout_opponent_grouped_fallbacks',
            'rollout_opponent_grouped_weight_reuses',
            'rollout_opponent_batch_budget_pixels',
            'rollout_opponent_wide_forward_chunks',
            'ppo_gpu_stream_work_share',
            'remote_policy_shared_collectors',
            'remote_policy_shared_encode_seconds',
            'remote_policy_source_pack_reused',
            'remote_policy_shared_bytes',
            'local_gpu_collector_count',
            'local_gpu_direct_model_updates',
            'local_gpu_direct_model_tensor_bytes',
            'local_gpu_policy_source_pack_groups',
            'local_gpu_policy_packed_consumers',
            'local_gpu_join_wait_seconds',
            'local_gpu_pending_at_local_end',
            'local_lease_native_engines_active',
            'local_lease_admission_sample_delta',
            'local_lease_admission_spawn_delta',
            'local_lease_engines_admitted_delta',
            'local_lease_admission_denials_delta',
            'local_lease_simulation_trial_active',
            'pipeline_local_gpu_wait_work_share',
            'pipeline_local_gpu_wait_dominant',
            'pipeline_local_gpu_wait_owner',
            'pipeline_slowest_collector_id',
            'pipeline_collection_exposure_fraction',
            'collector_wait_seconds','collector_critical_path_seconds',
            'collector_dispatch_queue_peak','collector_dispatch_queue_max_seconds',
            'collector_dispatch_tail_seconds','collector_dispatch_tail_owner',
            'pipeline_collector_dispatch_pressure','pipeline_collector_dispatch_owner',
            'pipeline_local_gpu_lease_index',
            'pipeline_local_gpu_fwd_pressure',
            'pipeline_local_gpu_fwd_owner',
            'pipeline_remote_gpu_fwd_pressure',
            'pipeline_remote_gpu_fwd_owner',
            'collector_0_sim_draining'])
            retain(name,reported[name]);
        for(const name of ['training_end_to_end_steps_per_second',
            'training_useful_steps_per_second',
            // Exposed Python simulation packing is a cross-project CPU
            // throughput clue, not an independent hardware saturation claim.
            'training_info_pack_max_seconds',
            // A proved live map move changes the physical collector barrier,
            // not the fixed PPO batch or the operator's machine lease.
            'training_live_map_moves','training_live_map_pred_gain_ratio',
            'training_live_move_parallel_workers',
            'training_live_move_seconds','training_live_move_wait_seconds',
            'training_live_move_service_seconds',
            'training_live_move_wire_bytes',
            'training_live_result_ready','training_live_result_accepted',
            'training_live_sps_change_pct','training_live_rollbacks',
            'training_collector_span_ratio',
            'training_collector_probe_moved_maps',
            'training_resume_map_moves',
            'training_resume_gain_ratio',
            'training_resume_proof_ready',
            'training_resume_proof_rounds',
            'training_resume_proof_seconds',
            'training_collector_sim_trial_workers',
            'training_collector_sim_eligible_workers',
            'training_sim_migration_workers',
            'training_sim_process_migrations',
            'training_sim_process_migration_denials',
            'training_sim_process_migration_seconds',
            'training_sim_migration_work_seconds',
            'training_collector_start_parallel',
            'training_collector_start_wall_seconds',
            'training_sim_pack_max_ratio','training_sim_pack_ratio_owner',
            'training_concurrency_limit','training_concurrency_next_limit',
            'training_tuner_adjustments','training_tuner_accepted',
            'training_tuner_settled',
            'training_gpu_optimizer_lineages',
            'training_gpu_optimizer_next_lineages',
            'training_gpu_memory_peer_contexts',
            'training_gpu_memory_peak_share',
            'training_gpu_memory_adjustments','training_gpu_memory_trial',
            'training_gpu_memory_settled','training_gpu_memory_accepted',
            'training_gpu_memory_rejected',
            'training_gpu_memory_sps_change_pct',
            'training_gpu_devices_assigned',
            'training_gpu_devices_active',
            'training_gpu_rollout_lineages',
            'training_local_gpu_rollout_lineages',
            'training_gpu_forward_stream_max_seconds',
            'training_gpu_forward_stream_workers',
            'training_gpu_input_slots_reused',
            'training_gpu_state_slots_reused',
            'training_gpu_output_staged_batches',
            'training_gpu_input_device_copy_batches',
            'training_gpu_input_device_reused_slots',
            'training_gpu_input_device_new_slots',
            'training_gpu_output_slots_reused',
            'training_gpu_output_slots_allocated',
            'training_gpu_state_pack_reuse_batches',
            'training_gpu_policy_moves_skipped',
            'training_shared_gpu_forward_max_seconds',
            'training_shared_gpu_forward_workers',
            'training_shared_gpu_input_reused',
            'training_shared_gpu_state_reused',
            'training_shared_gpu_output_staged',
            'training_shared_gpu_output_reused',
            'training_shared_gpu_input_device_copies',
            'training_shared_gpu_input_device_reused',
            'training_shared_gpu_state_pack_reused',
            'training_gpu_forward_slowest_share',
            'training_shared_gpu_forward_share',
            'training_gpu_forward_pressure','training_gpu_forward_owner',
            'training_shared_gpu_fwd_pressure',
            'training_shared_gpu_fwd_owner',
            'training_gpu_rollout_next_devices',
            'training_gpu_rollout_next_lineages','training_gpu_rollout_trial',
            'training_gpu_rollout_spare_candidates',
            'training_gpu_rollout_input_packs','training_gpu_rollout_input_seconds',
            'training_gpu_rollout_input_owner',
            'training_gpu_device_0_lineages',
            'training_gpu_device_1_lineages',
            'training_gpu_device_0_peak_allocated_mb',
            'training_gpu_device_1_peak_allocated_mb',
            'training_gpu_stream_max_seconds',
            'training_gpu_metric_readback_max_seconds',
            'training_gpu_guard_readback_max_seconds',
            'training_gpu_guard_readbacks',
            'training_gpu_snapshot_max_seconds',
            'training_gpu_snapshot_stream_max_seconds',
            'training_gpu_snapshot_reused_lineages',
            'training_gpu_snapshot_reuse_fraction',
            'training_gpu_snapshot_work_share',
            'training_gpu_h2d_enqueue_max_seconds',
            'training_gpu_h2d_bytes',
            'training_gpu_h2d_async_batches',
            'training_gpu_precheck_max_seconds',
            'training_gpu_prefetch_skipped_batches',
            'training_gpu_prefetch_validated_batches',
            'training_gpu_optimizer_peak_allocated_mb',
            'training_cpu_parallelism','training_queue_max_seconds',
            'training_sim_processes',
            'training_sim_multiplexed_shards',
            'training_sim_child_os_threads',
            'training_sim_queue_max_ratio','training_sim_queue_ratio_owner',
            'training_info_pack_max_seconds',
            'training_info_pack_work_seconds',
            'training_sim_pack_max_ratio',
            'training_sim_pack_ratio_owner',
            'training_sim_queue_max_seconds','training_sim_queue_peak_shards',
            'training_sim_idle_tail_max_ratio',
            'training_sim_idle_tail_ratio_owner',
            'training_sim_owner_tail_max_seconds',
            'training_sim_prestaged_actions',
            'training_sim_prestage_max_seconds',
            'training_sim_dispatch_copy_max_seconds',
            'training_sim_early_dispatches',
            'training_sim_topology_trial_lineages','training_sim_topology_accepted',
            'training_sim_topology_rejected','training_sim_topology_unavailable',
            'training_sim_topology_unverified','training_sim_topology_migrations',
            'training_topology_max_migration_seconds',
            'training_topology_max_pred_gain_ratio','training_topology_migration_owner',
            'training_topology_sps_change_pct','training_topology_result_owner',
            'training_collector_probe_count',
            'training_collector_probe_layout_changed',
            'training_collector_probe_gain_ratio',
            'training_collector_probe_wall_seconds',
            'training_collector_resume_restored',
            'training_collector_span_ratio',
            'training_collector_imbalance_pressure',
            'training_rollout_raw_bytes',
            'training_rollout_wire_bytes',
            'training_rollout_saved_bytes',
            'training_rollout_saved_share',
            'training_rollout_compressed_collectors',
            'training_rollout_decompress_seconds',
            'training_shared_last_wire_worker',
            'training_shared_wire_gap_seconds',
            'training_shared_postwire_tail_seconds',
            'training_shared_wire_gap_work_share',
            'training_shared_postwire_work_share',
            'training_shared_postwire_pressure',
            'training_prev_rollout_compress_seconds',
            'training_prev_rollout_send_seconds',
            'training_ingestion_exposed_seconds',
            'training_ingestion_copy_wall_seconds',
            'training_ingestion_claim_wait_seconds',
            'training_ingestion_peak_parallel_copies',
            'training_shared_reply_gap_seconds',
            'training_shared_ingestion_work_share',
            'training_shared_reply_gap_work_share',
            'training_shared_ingestion_pressure',
            'training_shared_collector_wait_pressure',
            'training_shared_wait_owner',
            'training_shared_sim_headroom_candidate',
            'training_shared_sim_headroom_owner',
            'training_shared_collection_dominant',
            'training_shared_optimization_dominant',
            'training_checkpoint_snapshot_seconds',
            'training_checkpoint_max_worker_seconds',
            'training_reply_gap_seconds','training_last_reply_worker',
            'training_remote_roundtrip_max_seconds',
            'training_remote_service_max_seconds',
            'training_remote_encode_max_seconds',
            'training_remote_decode_max_seconds',
            'training_remote_artifact_max_seconds',
            'training_remote_progress_seen',
            'training_remote_progress_sent',
            'training_remote_progress_skipped',
            'training_remote_progress_bytes',
            'training_remote_progress_max_seconds',
            'training_remote_progress_encode_seconds',
            'training_remote_critical_gap_ratio',
            'training_remote_prev_encode_max_seconds',
            'training_remote_prev_send_max_seconds',
            'training_remote_prev_prep_max_ratio'])
            retain(name,reported[name]);
        // Show measured stage pressure before unrelated high-cardinality
        // project metrics consume the default 96-chart-field cache.
        for(const name of ['pipeline_cpu_lease_pressure',
            'pipeline_cpu_throttle_pressure','pipeline_ram_lease_pressure',
            'pipeline_gpu_host_busy_pressure',
            'pipeline_gpu_mixed_host_evidence','pipeline_gpu_host_busy_device_share',
            'pipeline_cpu_underuse_evidence','pipeline_gpu_idle_evidence',
            'pipeline_remote_wait_dominant',
            'pipeline_remote_wait_owner','pipeline_remote_wait_competing_collectors',
            'pipeline_remote_exposed_work_share',
            'remote_collectors_pending_after_local',
            'remote_collector_co_limiting_count',
            'remote_collector_exposed_wait_seconds',
            'remote_collector_exposed_wait_owner',
            'remote_transport_new_connections',
            'remote_transport_reused_collectors',
            'remote_transport_connect_max_seconds',
            'remote_transport_connect_owner',
            'local_lease_ram_charged_mb',
        'local_lease_ram_headroom_mb',
        'remote_snapshot_cache_hits',
            'remote_snapshot_staged_bytes',
            'remote_snapshot_unstaged_bytes',
            'remote_snapshot_unstaged_ratio',
            'pipeline_remote_connection_reuse_ratio',
            'pipeline_remote_connect_pressure',
            'training_queue_pressure','training_final_join_pressure',
            'training_parallelism_underuse_evidence',
            'training_remote_gap_pressure','training_remote_codec_pressure',
            'training_remote_progress_pressure',
            'training_gpu_guard_wait_pressure',
            'training_gpu_policy_copy_pressure',
            'training_gpu_policy_reuse_fraction',
            'training_gpu_policy_copy_work_share',
            'training_gpu_guard_readback_work_share',
            'training_queue_work_share','training_reply_gap_work_share',
            'training_sim_queue_work_share','training_sim_queue_pressure',
            'training_sim_cpu_underuse_evidence',
            'training_sim_idle_tail_work_share',
            'training_sim_tail_skew_pressure',
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
        for(let i=0;i<MAX_TINYBRAINS_COLLECTORS;i++) {
            for(const suffix of ['sim_straggler_pressure','sim_dispatch_pressure',
                'record_encode_pressure','prev_reply_prep_pressure'])
                retain('collector_'+i+'_'+suffix,reported['collector_'+i+'_'+suffix]);
            for(const stage of ['engine','observation','inference','opponent','result','bootstrap','returns','model_prepare'])
                retain('collector_'+i+'_stage_'+stage+'_dominant',
                    reported['collector_'+i+'_stage_'+stage+'_dominant']);
        }
        // With many independent recurrent PPO lineages, retain sustained
        // limiting stages rather than the first dozens of raw counters.
        for(let i=0;i<64;i++)
            for(const stage of ['collection','optimization',
                'simulation','observation','collection_other'])
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
        const prepWait=seconds('ppo_prep_wait_seconds');
        const prepShare=(optimize>0&&prepWait!==null&&prepWait<=optimize)
            ?prepWait/optimize:null;
        if(prepShare!==null)add('ppo_prep_wait_work_share',prepShare);
        if(collection!==null&&optimize!==null&&collection+optimize>0) {
            const workShare=collection/(collection+optimize);
            add('pipeline_collection_work_share',workShare);
            add('pipeline_optimization_work_share',1-workShare);
            const overlap=seconds('overlap_seconds');
            const sourceExposure=metrics.pipeline_collection_exposure_fraction;
            const exposure=collection-Math.min(collection,overlap??0);
            const fraction=sourceExposure?.unit==='ratio'?sourceExposure.value:
                exposure+optimize>0?exposure/(exposure+optimize):workShare;
            // Worker straggler waits and learner-side merge are distinct
            // measured subsets of collection, not new hardware diagnoses.
            const joinWait=seconds('remote_collector_join_wait_seconds');
            const merge=seconds('collection_merge_seconds');
            const waitShare=collection>0&&joinWait!==null&&joinWait<=collection
                ?joinWait/collection:null;
            const mergeShare=collection>0&&merge!==null&&merge<=collection
                ?merge/collection:null;
            if(waitShare!==null)add('pipeline_remote_wait_work_share',waitShare);
            const remoteExposed=seconds('remote_collector_exposed_wait_seconds');
            const remotePending=metrics.remote_collectors_pending_after_local;
            const remoteOwnerMetric=metrics.remote_collector_exposed_wait_owner;
            const remoteCoLimit=metrics.remote_collector_co_limiting_count;
            const remoteCount=metrics.collector_count;
            const exposedShare=(collection>0&&remoteExposed!==null&&
                remoteExposed<=collection)?remoteExposed/collection:null;
            if(exposedShare!==null)
                add('pipeline_remote_exposed_work_share',exposedShare);
            // The owner is the last learner-visible collector completion,
            // not a worker's isolated service time or physical GPU usage.
            // Verify the matching per-worker receipt before attribution.
            const candidate=remoteOwnerMetric?.value;
            const ownerWait=Number.isSafeInteger(candidate)&&candidate>0&&
                candidate<MAX_TINYBRAINS_COLLECTORS
                ?seconds('collector_'+candidate+'_wait_after_local_seconds'):null;
            const connected=metrics.remote_transport_new_connections;
            const reused=metrics.remote_transport_reused_collectors;
            const validCount=value=>value?.unit==='count'&&
                Number.isSafeInteger(value.value)&&value.value>=0;
            const remoteUses=validCount(connected)&&validCount(reused)
                ?connected.value+reused.value:null;
            if(remoteUses>0)
                add('pipeline_remote_connection_reuse_ratio',
                    reused.value/remoteUses);
            const remoteCompeting=(remoteCoLimit?.unit==='count'&&
                Number.isSafeInteger(remoteCoLimit.value)&&
                remoteCoLimit.value>=2&&remotePending?.unit==='count'&&
                Number.isSafeInteger(remotePending.value)&&
                remotePending.value>=remoteCoLimit.value&&
                exposedShare!==null&&exposedShare>=0.25);
            const remoteOwner=(remoteCount?.unit==='count'&&
                remotePending?.unit==='count'&&
                Number.isSafeInteger(remoteCount.value)&&
                Number.isSafeInteger(remotePending.value)&&
                candidate>0&&candidate<remoteCount.value&&
                remotePending.value>=1&&
                remotePending.value<remoteCount.value&&
                exposedShare!==null&&exposedShare>=0.25&&
                remoteExposed>=0.25&&ownerWait!==null&&
                Math.abs(ownerWait-remoteExposed)<=
                    Math.max(1e-6,remoteExposed*0.001))
                    ?candidate:null;
            // The learner measures the limiting collector's TCP setup
            // interval after all local collectors finish.
            const connectSeconds=seconds('remote_connect_exposed_seconds');
            const remoteConnectCritical=remoteOwner!==null&&
                validCount(connected)&&connected.value>=1&&
                connectSeconds!==null&&connectSeconds<=remoteExposed&&
                connectSeconds>=0.15&&
                connectSeconds>=0.15*collection&&
                connectSeconds>=0.20*remoteExposed;
            const launchDelay=seconds('remote_network_launch_delay_seconds');
            const prepExposed=seconds('remote_network_prep_exposed_seconds');
            const remotePrepCritical=launchDelay!==null&&launchDelay<=collection&&
                prepExposed!==null&&prepExposed<=launchDelay&&
                prepExposed>=0.25&&prepExposed>=0.15*collection;
            const dispatchTail=seconds('collector_dispatch_tail_seconds');
            const dispatchMax=seconds('collector_dispatch_queue_max_seconds');
            const tailOwner=metrics.collector_dispatch_tail_owner;
            // Subtract all measured collection/optimization overlap from
            // the queue tail to get a lower bound on exposed PPO delay.
            const dispatchExposed=(dispatchTail!==null&&dispatchMax!==null&&
                dispatchTail<=dispatchMax&&dispatchMax<=collection&&overlap!==null)
                ?Math.max(0,dispatchTail-overlap):0;
            const dispatchOwner=(validCount(tailOwner)&&tailOwner.value>0&&
                validCount(remoteCount)&&tailOwner.value<remoteCount.value&&
                dispatchExposed>=0.15&&dispatchExposed>=0.15*exposure)
                ?tailOwner.value:null;
            if(mergeShare!==null)add('pipeline_merge_work_share',mergeShare);
            const localGpuWait=seconds('local_gpu_wait_after_local_max_seconds');
            const localGpuShare=(collection>0&&localGpuWait!==null&&
                localGpuWait<=collection)?localGpuWait/collection:null;
            if(localGpuShare!==null)
                add('pipeline_local_gpu_wait_work_share',localGpuShare);
            // Physical attribution requires the COMPLETE collection of
            // local GPU receipts in this update, not whichever metric happened
            // to fit the bounded source envelope. Inconclusive/tied results
            // leave owner unknown rather than inventing a slow GPU.
            const localGpuCount=metrics.local_gpu_collector_count?.value;
            const gpuSamples=[];
            for(let i=0;i<MAX_TINYBRAINS_COLLECTORS;i++) {
                if(metrics['collector_'+i+'_is_local_gpu']?.value!==1)
                    continue;
                const waited=seconds('collector_'+i+'_wait_after_local_seconds');
                const leaseIndex=metrics['collector_'+i+'_gpu_lease_index']?.value;
                if(waited!==null)
                    gpuSamples.push({index:i,waited,
                        leaseIndex:Number.isSafeInteger(leaseIndex)&&
                            leaseIndex>=0&&leaseIndex<gpuCount?leaseIndex:null});
            }
            let localGpuOwner=null,localGpuLease=null;
            if(localGpuShare!==null&&localGpuShare>=0.25&&
                localGpuWait>=0.25&&
                Number.isSafeInteger(localGpuCount)&&localGpuCount>0&&
                gpuSamples.length===localGpuCount) {
                gpuSamples.sort((a,b)=>b.waited-a.waited||a.index-b.index);
                const first=gpuSamples[0],second=gpuSamples[1];
                if(first.waited>=0.95*localGpuWait&&
                    (!second||(first.waited>=second.waited*1.15&&
                        first.waited-second.waited>=0.05))) {
                    localGpuOwner=first.index;
                    localGpuLease=first.leaseIndex;
                }
            }
            // These timed forward-stream intervals may include launch gaps.
            // Count them as a potential limiter only for a uniquely exposed
            // learner-side collector and only when they materially occupy
            // its OWN inference interval and the full collection wall.
            const forwardLimiting=index=>{
                if(!Number.isSafeInteger(index)||index<0||
                    index>=MAX_TINYBRAINS_COLLECTORS)return false;
                const host=seconds('collector_'+index+'_learner_inference_seconds');
                const gpu=seconds('collector_'+index+'_learner_gpu_stream_seconds');
                return host>0&&gpu!==null&&gpu<=host&&
                    gpu/host>=0.50&&gpu>=0.25&&gpu>=0.20*collection;
            };
            const localGpuForward=(localGpuOwner!==null&&
                localGpuShare>=0.25&&forwardLimiting(localGpuOwner))
                ?localGpuOwner:null;
            const remoteGpuForward=(remoteOwner!==null&&
                exposedShare>=0.25&&forwardLimiting(remoteOwner))
                ?remoteOwner:null;
            // Bounded observational state is neither a scheduler input
            // nor evidence that any particular device is saturated.
            const {at,prior}=history();
            // Per-collector stage times are independent *wall intervals*.
            // Compare only a complete set from one collector. A dominant
            // measured stage is not evidence that a specific CPU/GPU is full.
            const stageNames=['engine','observation','inference','opponent','result','bootstrap','returns','model_prepare'];
            const sourceNames=['rollout_engine_step_seconds',
                'observation_prepare_seconds','learner_inference_seconds',
                'opponent_processing_seconds','result_processing_seconds',
                'bootstrap_seconds','returns_seconds','model_prepare_seconds'];
            const stageHints=[];
            const simHints=[];
            for(let i=0;i<MAX_TINYBRAINS_COLLECTORS;i++) {
                const batch=seconds('collector_'+i+'_sim_batch_wall_seconds');
                const tail=seconds('collector_'+i+'_sim_last_child_gap_seconds');
                const queue=seconds('collector_'+i+'_sim_dispatch_queue_seconds');
                const worker=seconds('collector_'+i+'_sim_engine_roundtrip_seconds')??
                    seconds('collector_'+i+'_sim_engine_reply_seconds');
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
                const opponentHost=seconds('collector_'+i+'_opponent_processing_seconds');
                const opponentStream=seconds('collector_'+i+'_opp_gpu_stream_seconds');
                if(opponentHost>0&&opponentStream!==null&&opponentStream<=opponentHost)
                    // Stream time includes kernel launch gaps; this ratio
                    // compares one collector's completed opponent stage,
                    // not physical accelerator utilization or saturation.
                    add('collector_'+i+'_opponent_stream_share',
                        opponentStream/opponentHost);
                const durations=sourceNames.map(name=>
                    seconds('collector_'+i+'_'+name));
                if(durations.slice(0,4).some(x=>x===null))continue;
                const total=durations.reduce((sum,x)=>sum+(x??0),0);
                if(total<=0)continue;
                const maximum=Math.max(...durations);
                const stage=durations.indexOf(maximum);
                add('collector_'+i+'_max_stage_work_share',maximum/total);
                if(maximum/total>=0.65)
                    stageHints[i]=stage;
            }
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,fraction,waitShare,mergeShare,
                    prepShare,
                    localGpuShare,localGpuOwner,localGpuLease,remoteOwner,remoteCompeting,
                    localGpuForward,remoteGpuForward,
                    remoteConnectCritical,remotePrepCritical,dispatchOwner,
                    stageHints,simHints,resourceHints});
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
                // A foreground CPU-preparation stall matters for end-to-end
                // PPO only when optimizing is already exposure-dominant.
                // Producer CPU time can overlap GPU kernels and cannot be
                // added to the wall as if it were serial critical work.
                if(recent.filter(x=>x.fraction<=0.5&&
                    x.prepShare!==null&&x.prepShare>=0.30).length>=5)
                    add('pipeline_ppo_prep_wait_pressure',1,'count');
                if(recent.filter(x=>x.waitShare!==null&&x.waitShare>=0.5).length>=5) {
                    add('pipeline_remote_wait_dominant',1,'count');
                    // Require five of six completed measurements to name
                    // the same uniquely exposed remote collector.
                    const owner=recent.at(-1).remoteOwner;
                    if(owner!==null&&recent.filter(x=>
                        x.remoteOwner===owner&&x.waitShare!==null&&
                        x.waitShare>=0.5).length>=5)
                        add('pipeline_remote_wait_owner',owner,'count');
                    if(recent.filter(x=>x.remoteCompeting&&x.waitShare!==null&&
                        x.waitShare>=0.5).length>=5)
                        add('pipeline_remote_wait_competing_collectors',1,'count');
                }
                if(recent.filter(x=>x.localGpuShare!==null&&
                    x.localGpuShare>=0.5).length>=5) {
                    add('pipeline_local_gpu_wait_dominant',1,'count');
                    // A physical owner is meaningful only when the same
                    // collector dominates five of six sustained samples.
                    const owner=recent.at(-1).localGpuOwner;
                    const lease=recent.at(-1).localGpuLease;
                    if(owner!==null&&recent.filter(x=>
                        x.localGpuOwner===owner&&x.localGpuShare>=0.5).length>=5) {
                        add('pipeline_local_gpu_wait_owner',owner,'count');
                        if(lease!==null&&recent.filter(x=>
                            x.localGpuOwner===owner&&
                            x.localGpuLease===lease&&x.localGpuShare>=0.5).length>=5)
                            add('pipeline_local_gpu_lease_index',lease,'count');
                    }
                }
                const localForward=recent.at(-1).localGpuForward;
                if(localForward!==null&&recent.filter(x=>
                    x.localGpuForward===localForward).length>=5) {
                    add('pipeline_local_gpu_fwd_pressure',1,'count');
                    add('pipeline_local_gpu_fwd_owner',localForward,'count');
                }
                const remoteForward=recent.at(-1).remoteGpuForward;
                if(remoteForward!==null&&recent.filter(x=>
                    x.remoteGpuForward===remoteForward).length>=5) {
                    add('pipeline_remote_gpu_fwd_pressure',1,'count');
                    add('pipeline_remote_gpu_fwd_owner',remoteForward,'count');
                }
                const connect=recent.at(-1);
                if(connect.remoteConnectCritical&&recent.filter(x=>
                    x.remoteConnectCritical&&x.remoteOwner===connect.remoteOwner).length>=5) {
                    add('pipeline_remote_connect_pressure',1,'count');
                    add('pipeline_remote_connect_owner',connect.remoteOwner,'count');
                }
                if(recent.filter(x=>x.remotePrepCritical).length>=5)
                    add('pipeline_remote_prep_pressure',1,'count');
                const dispatchOwner=recent.at(-1).dispatchOwner;
                if(Number.isSafeInteger(dispatchOwner)&&recent.filter(x=>
                    x.dispatchOwner===dispatchOwner).length>=5) {
                    add('pipeline_collector_dispatch_pressure',1,'count');
                    add('pipeline_collector_dispatch_owner',dispatchOwner,'count');
                }
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
                for(let i=0;i<MAX_TINYBRAINS_COLLECTORS;i++) {
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
            // Only the slowest REMOTE lineage can demonstrate a potential
            // distributed critical-path delay. A high residual from an
            // early finishing worker does not limit the completed batch.
            const remoteWall=seconds('training_remote_roundtrip_max_seconds');
            const remoteGapMetric=metrics.training_remote_critical_gap_ratio;
            const remoteGap=remoteGapMetric?.unit==='ratio'&&
                Number.isFinite(remoteGapMetric.value)
                ?remoteGapMetric.value:null;
            const critical=metrics.training_remote_roundtrip_owner?.value;
            const remoteImportant=remoteWall!==null&&remoteWall>=0.3*wall&&
                remoteWall>=0.5&&Number.isSafeInteger(critical);
            const remoteGapHint=remoteImportant&&remoteGap!==null&&
                remoteGap>=0.35;
            // Encoding/decoding time is exclusive measured client work.
            // In contrast, socket receive wall contains worker execution,
            // remote scheduling, and blocking; it is not link utilization.
            const remoteCodecHint=remoteImportant&&[
                ['encode','training_remote_encode_max_seconds'],
                ['decode','training_remote_decode_max_seconds'],
            ].some(([stage,field])=>
                metrics['training_remote_'+stage+'_owner']?.value===critical&&
                seconds(field)!==null&&seconds(field)/remoteWall>=0.25);
            // Progress is observational, but each forwarded frame blocks
            // the remote service on TCP send. Attribute a measured stall
            // only when that service finishes the entire round last.
            // Socket blocking may be OS backpressure, not link saturation.
            const progressSend=seconds('training_remote_progress_max_seconds');
            const progressOwner=metrics.training_remote_progress_owner?.value;
            const progressShare=(remoteImportant&&progressSend!==null&&
                progressOwner===critical&&progressSend<=remoteWall)
                ?progressSend/remoteWall:null;
            if(progressShare!==null)
                add('training_remote_progress_work_share',progressShare);
            const progressHint=progressShare!==null&&progressShare>=0.20&&
                progressSend>=0.25&&
                metrics.training_last_reply_worker?.value===critical&&
                (queueShare===null||queueShare<=0.10);
            // Only the actual slowest PPO lineage can identify whether
            // measured host blocking at the fused KL/finite GPU decision
            // contributes to the experiment's critical path. This wait
            // INCLUDES pending GPU work, so it is not a GPU busy fraction.
            const slowest=metrics.training_slowest_worker?.value;
            const simQueue=metrics.training_sim_queue_max_ratio;
            const simQueueShare=simQueue?.unit==='ratio'&&
                Number.isFinite(simQueue.value)&&simQueue.value>=0&&
                simQueue.value<=1?simQueue.value:null;
            if(simQueueShare!==null)
                add('training_sim_queue_work_share',simQueueShare);
            const simQueueOwner=metrics.training_sim_queue_ratio_owner?.value;
            const slowestSimulation=Number.isSafeInteger(slowest)
                ?seconds('worker_'+slowest+'_simulation_seconds'):null;
            const slowestCollection=Number.isSafeInteger(slowest)
                ?seconds('worker_'+slowest+'_collection_seconds'):null;
            const forward=Number.isSafeInteger(slowest)
                ?seconds('worker_'+slowest+'_gpu_fwd_stream_seconds'):null;
            if(slowestCollection>0&&forward!==null&&forward<=slowestCollection)
                // The longest worker is not necessarily the last reply.
                // Report measured work share without asserting GPU saturation.
                add('training_gpu_forward_slowest_share',
                    forward/slowestCollection);
            // Stream elapsed time includes dispatch gaps and is not kernel
            // occupancy. Only an actually last-completing PPO lineage with
            // a material coordinator reply gap can limit useful SPS.
            const gpuForwardHint=Number.isSafeInteger(slowest)&&
                slowest>=0&&slowest<count&&
                metrics.training_last_reply_worker?.value===slowest&&
                slowestCollection>0&&forward!==null&&
                forward<=slowestCollection&&
                forward/slowestCollection>=0.50&&forward>=0.25&&
                forward>=0.20*wall&&slowestCollection>=0.30*wall&&
                gapShare!==null&&gapShare>=0.20&&
                (queueShare===null||queueShare<=0.10);
            const simDominant=slowestSimulation!==null&&slowestCollection>0&&
                slowestSimulation>=0.45*slowestCollection&&
                slowestSimulation>=0.20*wall;
            // Queued shards show pending work, not host CPU saturation.
            // Only attribute a bottleneck to the lineage that extends
            // the completed PPO round and is dominated by simulation.
            const simQueueCritical=simQueueShare!==null&&
                simQueueShare>=0.25&&simDominant&&
                Number.isSafeInteger(simQueueOwner)&&simQueueOwner===slowest;
            const idleTail=metrics.training_sim_idle_tail_max_ratio;
            const idleTailShare=idleTail?.unit==='ratio'&&
                Number.isFinite(idleTail.value)&&idleTail.value>=0&&
                idleTail.value<=1?idleTail.value:null;
            if(idleTailShare!==null)
                add('training_sim_idle_tail_work_share',idleTailShare);
            const idleTailOwner=metrics.training_sim_idle_tail_ratio_owner?.value;
            // Observed last reply-to-barrier time for idle physical
            // processes supports a delayed-final-reply (tail-skew) diagnosis,
            // not a measurement of actual OS/device idle percentage.
            const simImbalanceCritical=idleTailShare!==null&&
                idleTailShare>=0.20&&simDominant&&
                Number.isSafeInteger(idleTailOwner)&&idleTailOwner===slowest;
            const guard=(Number.isSafeInteger(slowest)&&
                slowest>=0&&slowest<count)
                ?seconds('worker_'+slowest+'_gpu_guard_readback_seconds'):null;
            const slowestOptimize=(Number.isSafeInteger(slowest)&&
                slowest>=0&&slowest<count)
                ?seconds('worker_'+slowest+'_optimization_seconds'):null;
            const guardShare=slowestOptimize>0&&guard!==null&&
                guard<=slowestOptimize
                ?guard/slowestOptimize:null;
            if(guardShare!==null)
                add('training_gpu_guard_readback_work_share',guardShare);
            // An optimizer backup is a measured host+GPU copy interval.
            // Only the last completed lineage can expose it as a training
            // bottleneck; a costly early-finishing lineage does not.
            const snapshot=(Number.isSafeInteger(slowest)&&
                slowest>=0&&slowest<count)
                ?seconds('worker_'+slowest+'_gpu_snapshot_seconds'):null;
            const snapshotShare=slowestOptimize>0&&snapshot!==null&&
                snapshot<=slowestOptimize?snapshot/slowestOptimize:null;
            if(snapshotShare!==null)
                add('training_gpu_snapshot_critical_share',snapshotShare);
            // CUDA/ROCm events bracket backup copies on the optimizer
            // stream. This interval includes host dispatch gaps; neither
            // measure is an exclusive GPU-kernel busy percentage.
            const snapshotStream=(Number.isSafeInteger(slowest)&&
                slowest>=0&&slowest<count)
                ?seconds('worker_'+slowest+'_gpu_snapshot_stream_seconds'):null;
            const streamShare=slowestOptimize>0&&snapshotStream!==null&&
                snapshotStream<=slowestOptimize
                ?snapshotStream/slowestOptimize:null;
            if(streamShare!==null)
                add('training_gpu_snapshot_stream_work_share',streamShare);
            const snapshotHint=(
                (snapshotShare!==null&&snapshotShare>=0.25&&snapshot>=0.20)||
                (streamShare!==null&&streamShare>=0.25&&snapshotStream>=0.20)
            )&&slowestOptimize>=0.15*wall&&
                metrics.training_last_reply_worker?.value===slowest&&
                (queueShare===null||queueShare<=0.10);
            const guardHint=guardShare!==null&&guardShare>=0.3&&
                guard>=0.25&&slowestOptimize>=0.15*wall;
            // Policy GPU<->CPU host transition costs from the SAME
            // slowest completed lineage. These include host dispatch and
            // blocking; they are not measured PCIe link utilization.
            const copyRow=metrics.training_gpu_policy_copy_slowest_ratio;
            const copyShare=copyRow?.unit==='ratio'&&
                Number.isFinite(copyRow.value)&&copyRow.value>=0&&
                copyRow.value<=1?copyRow.value:null;
            const copySeconds=seconds('training_gpu_policy_copy_slowest_seconds');
            if(copyShare!==null)
                add('training_gpu_policy_copy_work_share',copyShare);
            const gpuCopyHint=copyShare!==null&&copyShare>=0.25&&
                copySeconds!==null&&copySeconds>=0.25&&
                Number.isSafeInteger(slowest)&&
                // A worker can have the longest local training interval yet
                // finish before a separately queued or remote lineage.
                // Attribute an exposed pipeline stall only when verified
                // coordinator reply arrival identifies the SAME owner.
                metrics.training_last_reply_worker?.value===slowest&&
                metrics.training_gpu_policy_copy_owner?.value===slowest&&
                (queueShare===null||queueShare<=0.10);
            // In each completed update, only eligible GPU PPO lineages can
            // retain an unchanged policy for another on-policy collection.
            const gpuLineages=metrics.training_gpu_optimizer_lineages?.value;
            const reused=metrics.training_gpu_policy_reused?.value;
            if(Number.isSafeInteger(gpuLineages)&&gpuLineages>0&&
                Number.isSafeInteger(reused)&&reused>=0&&reused<=gpuLineages)
                add('training_gpu_policy_reuse_fraction',reused/gpuLineages,'ratio');
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
                const fraction=collect/(collect+optimize);
                const simShare=(collect>0&&simulation!==null&&
                    simulation<=collect)?simulation/collect:null;
                const obsShare=(simulation>0&&observation!==null&&
                    observation<=simulation)?observation/simulation:null;
                if(simShare!==null)
                    add('worker_'+i+'_simulation_work_share',simShare);
                if(obsShare!==null)
                    add('worker_'+i+'_observation_work_share',obsShare);
                // Report the broad collection/optimization split and at
                // most one deeper collection cause per worker. Otherwise
                // 64 lineages can displace later workers' evidence at the
                // 512-field report limit simply through nested duplicates.
                const observationHeavy=simShare!==null&&simShare>=0.50&&
                    obsShare!==null&&obsShare>=0.60;
                stageHints[i]={
                    collection:fraction>=0.70,
                    optimization:fraction<=0.30,
                    simulation:fraction>=0.70&&!observationHeavy&&
                        simShare!==null&&simShare>=0.65,
                    observation:fraction>=0.70&&observationHeavy,
                    // Unmeasured work also includes actor inference,
                    // recurrent state handling and Python coordination.
                    collection_other:fraction>=0.70&&simShare!==null&&
                        simShare<=0.35
                };
            }
            const {at,prior}=history();
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,stageHints,resourceHints,
                    courierQueue:queueShare!==null&&queueShare>=0.20,
                    courierSimQueue:simQueueCritical,
                    courierSimImbalance:simImbalanceCritical,
                    // With queued lineages, the last completion may be a
                    // normal admission wave rather than a slow worker.
                    courierTail:gapShare!==null&&gapShare>=0.25&&
                        (queueShare===null||queueShare<=0.10),
                    courierRemoteGap:remoteGapHint,
                    courierRemoteCodec:remoteCodecHint,
                    courierRemoteProgress:progressHint,
                    courierGpuGuard:guardHint,
                    courierGpuSnapshot:snapshotHint,
                    courierGpuCopy:gpuCopyHint,
                    courierGpuForward:gpuForwardHint?slowest:null});
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
                if(sustained(x=>x.courierSimQueue))
                    add('training_sim_queue_pressure',1,'count');
                if(sustained(x=>x.courierSimImbalance))
                    add('training_sim_tail_skew_pressure',1,'count');
                if(sustained(x=>x.courierSimQueue)&&
                    sustained(x=>x.resourceHints?.cpuIdle))
                    add('training_sim_cpu_underuse_evidence',1,'count');
                if(sustained(x=>x.courierTail))
                    add('training_final_join_pressure',1,'count');
                if(sustained(x=>x.courierRemoteGap))
                    add('training_remote_gap_pressure',1,'count');
                if(sustained(x=>x.courierRemoteCodec))
                    add('training_remote_codec_pressure',1,'count');
                if(sustained(x=>x.courierRemoteProgress))
                    add('training_remote_progress_pressure',1,'count');
                if(sustained(x=>x.courierGpuGuard))
                    add('training_gpu_guard_wait_pressure',1,'count');
                if(sustained(x=>x.courierGpuSnapshot))
                    add('training_gpu_snapshot_pressure',1,'count');
                if(sustained(x=>x.courierGpuCopy))
                    add('training_gpu_policy_copy_pressure',1,'count');
                // Do not combine evidence from different policy lineages.
                if(Number.isSafeInteger(slowest)&&slowest>=0&&slowest<count&&
                    recent.filter(x=>x.courierGpuForward===slowest).length>=5) {
                    add('training_gpu_forward_pressure',1,'count');
                    add('training_gpu_forward_owner',slowest,'count');
                }
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
                    for(const stage of ['collection','optimization',
                        'simulation','observation','collection_other'])
                        if(sustained(x=>x.stageHints?.[i]?.[stage]))
                            add('worker_'+i+'_'+stage+'_dominant',1,'count');
            }
        } else if(seconds('training_batch_wall_seconds')>0&&
            metrics.training_collector_workers?.unit==='count'&&
            Number.isSafeInteger(metrics.training_collector_workers.value)&&
            metrics.training_collector_workers.value>=1&&
            metrics.training_collector_workers.value<=16) {
            // Shared-policy Courier owns ONE recurrent PPO update across
            // remote shards, unlike independently optimized lineages above.
            // All times are learner-clock intervals. Shard-copy work can
            // overlap other copies and remote simulation; only the tail
            // after the last complete reply is exposed ingestion latency.
            const wall=seconds('training_batch_wall_seconds');
            const raw=metrics.training_rollout_raw_bytes;
            const wire=metrics.training_rollout_wire_bytes;
            if(raw?.unit==='bytes'&&wire?.unit==='bytes'&&
                Number.isSafeInteger(raw.value)&&raw.value>0&&
                Number.isSafeInteger(wire.value)&&
                wire.value>=0&&wire.value<=raw.value)
                add('training_rollout_saved_share',
                    (raw.value-wire.value)/raw.value);
            const collection=seconds('training_collect_max_seconds');
            const optimization=seconds('training_optimize_max_seconds');
            const gpuForwardOwner=metrics.training_shared_last_wire_worker?.value;
            const replyOwner=metrics.training_shared_last_reply_worker?.value;
            let sharedForwardShare=null,sharedForwardSeconds=null;
            if(Number.isSafeInteger(gpuForwardOwner)&&gpuForwardOwner===replyOwner&&
                gpuForwardOwner>=0&&gpuForwardOwner<metrics.training_collector_workers.value) {
                const workerCollection=seconds('worker_'+gpuForwardOwner+'_collection_seconds');
                const stream=seconds('worker_'+gpuForwardOwner+'_gpu_fwd_stream_seconds');
                if(workerCollection>0&&stream!==null&&stream<=workerCollection) {
                    sharedForwardShare=stream/workerCollection;
                    sharedForwardSeconds=stream;
                    add('training_shared_gpu_forward_share',sharedForwardShare);
                }
            }
            const exposed=seconds('training_ingestion_exposed_seconds');
            const copied=seconds('training_ingestion_copy_wall_seconds');
            const gap=seconds('training_shared_reply_gap_seconds');
            const wireGap=seconds('training_shared_wire_gap_seconds');
            const postwire=seconds('training_shared_postwire_tail_seconds');
            const wireOwner=metrics.training_shared_last_wire_worker?.value;
            const decodedOwner=metrics.training_shared_last_reply_worker?.value;
            const count=metrics.training_ingestion_peak_parallel_copies;
            const measured=(collection!==null&&optimization!==null&&
                collection<=wall*1.02&&optimization<=wall*1.02&&
                exposed!==null&&exposed<=collection&&
                copied!==null&&count?.unit==='count'&&
                Number.isSafeInteger(count.value)&&count.value>=1&&
                count.value<=metrics.training_collector_workers.value);
            const exposedShare=measured?exposed/wall:null;
            const gapShare=gap!==null&&gap<=wall?gap/wall:null;
            const wireShare=wireGap!==null&&wireGap<=wall?wireGap/wall:null;
            const postwireShare=postwire!==null&&postwire<=collection
                ?postwire/wall:null;
            // A collector's GPU stream is a candidate only when that SAME
            // collector last delivered both bytes and decoded experience,
            // and its distinct wire-arrival gap extends the PPO round.
            const sharedForwardHint=sharedForwardShare!==null&&
                sharedForwardShare>=0.50&&sharedForwardSeconds>=0.25&&
                sharedForwardSeconds>=0.20*wall&&collection!==null&&
                collection>=0.50*wall&&wireShare!==null&&
                wireShare>=0.20&&wireGap>=0.25&&
                gpuForwardOwner===wireOwner&&wireOwner===decodedOwner;
            if(wireShare!==null)
                add('training_shared_wire_gap_work_share',wireShare);
            if(postwireShare!==null)
                add('training_shared_postwire_work_share',postwireShare);
            if(exposedShare!==null)
                add('training_shared_ingestion_work_share',exposedShare);
            if(gapShare!==null)
                add('training_shared_reply_gap_work_share',gapShare);
            const ingestionDelay=exposedShare!==null&&
                exposedShare>=0.20&&exposed>=0.25;
            // A late DECODED packet can be late because the learner is
            // busy unpickling, even if bytes arrived first. Only confirmed
            // learner-clock last-wire gaps support a remote-completion tail.
            const slowReply=wireShare!==null&&wireShare>=0.25&&
                wireGap>=0.25&&wireOwner===decodedOwner&&
                (exposedShare===null||exposedShare<=0.10);
            // A stable last-wire owner identifies a limiting collector,
            // not a saturated CPU/GPU. A process ceiling above its current
            // process count only indicates room to *test* parallelism.
            // Do not attribute a worker while its own topology is changing.
            const validOwner=Number.isSafeInteger(wireOwner)&&wireOwner>=0&&
                wireOwner<metrics.training_collector_workers.value;
            const ownerTrial=validOwner
                ?metrics['worker_'+wireOwner+'_sim_trial']?.value:null;
            const sharedWaitOwner=slowReply&&validOwner&&ownerTrial!==1
                ?wireOwner:null;
            const processCount=validOwner
                ?metrics['worker_'+wireOwner+'_sim_physical_processes']:null;
            const processCeiling=validOwner
                ?metrics['worker_'+wireOwner+'_sim_process_ceiling']:null;
            const headroomCandidate=sharedWaitOwner!==null&&
                processCount?.unit==='count'&&
                processCeiling?.unit==='count'&&
                Number.isSafeInteger(processCount.value)&&
                Number.isSafeInteger(processCeiling.value)&&
                processCeiling.value>processCount.value;
            const postwirePressure=postwireShare!==null&&
                postwireShare>=0.20&&postwire>=0.25;
            const span=metrics.training_collector_span_ratio;
            const spread=(span?.unit==='ratio'&&span.value>=0&&
                span.value<=1)?span.value:null;
            // Unequal collector completion walls are a real scheduling
            // imbalance, not evidence of a saturated CPU or slow network.
            // Ignore it if collection is mostly hidden by PPO optimization.
            const imbalance=spread!==null&&spread>=0.25&&
                collection>=0.50*wall&&collection>=0.50;
            const collectionDominant=measured&&collection>=0.70*wall&&
                optimization<=0.30*wall;
            const optimizationDominant=measured&&optimization>=0.70*wall&&
                collection<=0.30*wall;
            const {at,prior}=history();
            if(Number.isFinite(at)&&at>prior.lastAt) {
                prior.samples.push({at,resourceHints,
                    sharedIngestion:ingestionDelay,
                    sharedReply:slowReply,
                    sharedWaitOwner,
                    sharedHeadroom:headroomCandidate?sharedWaitOwner:null,
                    sharedPostwire:postwirePressure,
                    sharedImbalance:imbalance,
                    sharedCollection:collectionDominant,
                    sharedOptimization:optimizationDominant,
                    sharedGpuForward:sharedForwardHint?gpuForwardOwner:null});
                if(prior.samples.length>8)prior.samples.shift();
                prior.lastAt=at;
            }
            const recent=prior.samples.slice(-6);
            add('training_timing_sample_count',recent.length,'count');
            if(recent.length>=5&&
                recent.at(-1).at-recent[0].at>=120000) {
                const sustained=fn=>recent.filter(fn).length>=5;
                if(sustained(x=>x.sharedIngestion))
                    add('training_shared_ingestion_pressure',1,'count');
                if(sustained(x=>x.sharedReply))
                    add('training_shared_collector_wait_pressure',1,'count');
                // Require the SAME authenticated collector to own five
                // of the six last complete wire arrivals. Avoid labeling
                // transient fleet skew or a mixed group of slower hosts as
                // one machine's hardware saturation.
                const owners=recent.map(x=>x.sharedWaitOwner).filter(
                    Number.isSafeInteger);
                if(owners.length>=5) {
                    const owner=owners.find(slot=>
                        owners.filter(value=>value===slot).length>=5);
                    if(owner!==undefined) {
                        add('training_shared_wait_owner',owner,'count');
                        if(recent.filter(x=>x.sharedHeadroom===owner).length>=5) {
                            add('training_shared_sim_headroom_candidate',1,'count');
                            add('training_shared_sim_headroom_owner',owner,'count');
                        }
                    }
                }
                if(sustained(x=>x.sharedPostwire))
                    add('training_shared_postwire_pressure',1,'count');
                if(Number.isSafeInteger(gpuForwardOwner)&&
                    recent.filter(x=>x.sharedGpuForward===gpuForwardOwner).length>=5) {
                    add('training_shared_gpu_fwd_pressure',1,'count');
                    add('training_shared_gpu_fwd_owner',gpuForwardOwner,'count');
                }
                if(sustained(x=>x.sharedImbalance))
                    add('training_collector_imbalance_pressure',1,'count');
                if(sustained(x=>x.sharedCollection))
                    add('training_shared_collection_dominant',1,'count');
                if(sustained(x=>x.sharedOptimization))
                    add('training_shared_optimization_dominant',1,'count');
                if(sustained(x=>x.resourceHints?.cpuBusy))
                    add('pipeline_cpu_lease_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.throttled))
                    add('pipeline_cpu_throttle_pressure',1,'count');
                if(sustained(x=>x.resourceHints?.ramNear))
                    add('pipeline_ram_lease_pressure',1,'count');
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
        for(let i=0;i<MAX_TINYBRAINS_COLLECTORS;i++) {
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
        // continuous ratios when nine profiled collectors approach the wire
        // limit. Never evict a project-owned source metric for a diagnosis.
        const priority=name=>
            (name==='training_gpu_forward_pressure'||
             name==='training_gpu_forward_owner'||
             name==='training_shared_gpu_fwd_pressure'||
             name==='training_shared_gpu_fwd_owner'||
             name==='training_shared_wait_owner'||
             name==='training_shared_sim_headroom_owner'||
             name==='pipeline_local_gpu_fwd_owner'||
             name==='pipeline_remote_gpu_fwd_owner'||
             name==='pipeline_local_gpu_wait_owner'||
             name==='pipeline_local_gpu_lease_index'||
             name==='pipeline_remote_connect_owner'||
             name==='pipeline_collector_dispatch_owner'||
             name==='pipeline_remote_wait_owner')?0:
            name.startsWith('pipeline_')&&(
                name.endsWith('_pressure')||name.endsWith('_evidence'))?0:
            name.startsWith('pipeline_')&&name.endsWith('_dominant')?0:
            // Keep evidence from every machine before filling the report
            // with a second or third warning from the first collector.
            name.endsWith('_sim_straggler_pressure')?1:
            name.endsWith('_sim_dispatch_pressure')?2:
            name.endsWith('_record_encode_pressure')?3:
            name.endsWith('_pressure')?4:
            name.endsWith('_dominant')?5:
            name==='pipeline_timing_sample_count'?6:7;
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
module.exports={validateProgress,selectChartMetrics,BottleneckEvidence,MAX_TINYBRAINS_COLLECTORS};
