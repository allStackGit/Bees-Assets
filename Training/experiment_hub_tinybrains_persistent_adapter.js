'use strict';

// Locally trusted TinyBrains persistent-PPO adapter for the opt-in Docker owner.
// A Hub job copies only its seed checkpoint, frozen run spec and gate proof;
// frozen opponents remain referenced from an immutable, read-only source run.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {fixedCpuParallelism}=require('./experiment_hub_resources');
const {modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROFILE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA=/^[0-9a-f]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const TAIL=256*1024;
const MAX_PROGRESS_ROW_TAIL=8*1024*1024;
const PROJECT_IN='/workspace/projects/tinybrains/Ants-MAPOCA';
const HUB_IN='/workspace/projects/experiment-hub/Assets';
const JOBS_IN='/workspace/experiments/experiment-hub/tinybrains-jobs';
const SOURCE_IN='/workspace/experiments/tinybrains/mini-learning';
const CACHE_IN='/workspace/cache/tinybrains';
const REMOTE_KEY_IN='/workspace/private/tinybrains-rollouts';

// Large completed-update rows may fit in the final 8 MiB scan without
// including the *preceding* full row. Read that predecessor separately so
// end-to-end actions/s remains available when match evidence is extensive.
// This is optional telemetry: an oversized predecessor yields no rate.
function previousHistoryRow(fd,endExclusive) {
    let bytes=Math.min(endExclusive,TAIL);
    while(bytes>0) {
        const offset=endExclusive-bytes;
        const buffer=Buffer.alloc(bytes);
        if(fs.readSync(fd,buffer,0,bytes,offset)!==bytes)
            throw Error('TinyBrains training history changed during read.');
        const text=buffer.toString('utf8');
        const lineStart=text.lastIndexOf('\n');
        if(lineStart>=0||offset===0)
            return JSON.parse(text.slice(lineStart+1));
        if(bytes>=MAX_PROGRESS_ROW_TAIL)return null;
        bytes=Math.min(endExclusive,MAX_PROGRESS_ROW_TAIL,bytes*2);
    }
    return null;
}

// Derive bounded, project-neutral chart measurements from the trainer's
// committed update, not from GPU utilization guesses or incomplete rollouts.
// Keep exact game events distinct from observation-based food/combat proxies.
function recordedTinyBrainsMetrics(row,updateWall) {
    const metrics={};
    function add(name,value,unit) {
        if(!Number.isFinite(value)||Math.abs(value)>1e12||
            (unit==='count'&&(!Number.isSafeInteger(value)||value<0))||
            (['rate','seconds','mb'].includes(unit)&&value<0)||
            (unit==='ratio'&&(value<0||value>1)))return;
        metrics[name]={value,unit};
    }
    add('total_ppo_updates',row.update,'count');
    add('engine_steps',row.engine_steps,'count');
    add('simulation_match_steps',row.simulation_match_steps,'count');
    if(updateWall!==null&&updateWall>0) {
        add('update_wall_time',updateWall,'seconds');
        add('engine_steps_per_second',row.engine_steps/updateWall,'rate');
        add('simulation_match_steps_per_second',
            row.simulation_match_steps/updateWall,'rate');
        add('ant_decisions_per_second',row.ant_decisions/updateWall,'rate');
        add('group_states_per_second',row.group_states/updateWall,'rate');
    }
    for(const name of ['collection_seconds','rollout_model_refresh_seconds',
        'rollout_engine_step_seconds','rollout_observation_prepare_seconds',
        'rollout_learner_inference_seconds','rollout_learner_gpu_stream_seconds',
        'rollout_opponent_processing_seconds',
        'simulation_engine_roundtrip_seconds','simulation_dispatch_queue_seconds',
        'simulation_batch_wall_seconds','simulation_last_child_gap_seconds',
        'simulation_prefetch_overlap_seconds',
        'optimization_seconds','checkpoint_seconds',
        'remote_collector_join_wait_seconds','collection_merge_seconds',
        'overlap_seconds','remote_policy_snapshot_seconds',
        'remote_opponent_snapshot_seconds',
        'ppo_profile_host_seconds','ppo_advantages_seconds',
        'ppo_ordering_seconds','ppo_zero_grad_seconds',
        'ppo_grouping_seconds','ppo_data_preparation_seconds',
        'ppo_forward_loss_seconds','ppo_loss_finite_check_seconds',
        'ppo_backward_seconds','ppo_metric_readback_seconds',
        'ppo_diagnostic_aggregation_seconds',
        'ppo_gradient_processing_seconds','ppo_optimizer_step_seconds',
        'ppo_parameter_finite_check_seconds','ppo_gpu_stream_sync_seconds',
        'ppo_gpu_stream_interval_seconds'])
        add(name,row[name],'seconds');
    for(const name of ['simulation_active_matches','simulation_target_matches',
        'simulation_concurrency_adjustments','simulation_tuner_accepted',
        'simulation_tuner_settled','simulation_target_restored',
        'simulation_proven_target',
        'ant_decisions','group_states','births','deaths',
        'episodes_ended','multi_ant_states','terminal_rewards',
        'carried_matches','open_matches','peak_live_matches',
        'truncated_groups','terminal_groups','collector_count',
        'simulation_prefetch_engine_steps',
        'ppo_minibatches','ppo_microbatches',
        'ppo_diagnostic_readbacks','ppo_diagnostic_rows',
        'ppo_gpu_pixel_budget',
        'ppo_gpu_batch_target_pixels','ppo_gpu_batch_next_target',
        'ppo_gpu_batch_adjustments','ppo_gpu_batch_accepted',
        'ppo_gpu_batch_settled','ppo_gpu_batch_restored',
        'ppo_gpu_batch_changed_next','ppo_gpu_batch_proven_target',
        'ppo_gpu_batch_vram_capped',
        'rollout_gpu_batch_target_pixels','rollout_gpu_batch_next_target',
        'rollout_gpu_batch_adjustments','rollout_gpu_batch_accepted',
        'rollout_gpu_batch_settled','rollout_gpu_batch_restored',
        'rollout_gpu_batch_changed_next','rollout_gpu_batch_proven_target',
        'rollout_gpu_batch_vram_capped',
        'rollout_inference_pixel_samples',
        'ppo_prestaged_board_states','ppo_reused_board_states',
        'rollout_inference_shape_groups','rollout_inference_readbacks',
        'rollout_inference_forward_chunks','rollout_opponent_shape_groups','rollout_opponent_readbacks',
        'collector_auto_balance_adjustments',
        'collector_auto_balance_accepted','collector_auto_balance_settled',
        'collector_balance_changed_next_update',
        'ppo_board_pixel_samples','ppo_max_board_cells',
        'ppo_unique_board_shapes','policy_lag','action_target'])
        add(name,row[name],'count');
    // These optimizer observations can be negative; they are scalar values,
    // never performance or strength certification.
    for(const name of ['loss','policy_loss','value_loss','baseline_loss',
        'entropy','actor_grad_norm','group_grad_norm',
        'baseline_grad_norm','actor_max_weight_change',
        'critic_max_weight_change','learning_rate','population_mean',
        'states_per_microbatch'])add(name,row[name],'scalar');
    add('gae_lambda',row.gae_lambda,'ratio');
    add('rss_mb',row.rss_mb,'mb');
    add('simulation_engine_memory_mb',row.simulation_engine_memory_mb,'mb');
    add('ppo_prestaged_board_bytes',row.ppo_prestaged_board_bytes,'bytes');
    if(Number.isFinite(row.elapsed_seconds))
        add('elapsed_training_seconds',row.elapsed_seconds,'seconds');

    const outcomes=row.outcomes||{};
    const wins=outcomes.win,losses=outcomes.loss,draws=outcomes.draw;
    add('training_wins',wins,'count');
    add('training_losses',losses,'count');
    add('training_draws',draws,'count');
    if([wins,losses,draws].every(n=>Number.isSafeInteger(n)&&n>=0)) {
        const played=wins+losses+draws;
        add('training_completed_matches',played,'count');
        if(played) {
            add('training_win_rate',wins/played,'ratio');
            add('training_score_rate',(wins+0.5*draws)/played,'ratio');
        }
    }
    const games=Array.isArray(row.match_results)?row.match_results:[];
    let scoreSum=0,marginSum=0,turnsSum=0,scored=0,margins=0,turns=0;
    let exactFood=0,exactKills=0,foodGames=0,killGames=0;
    for(const game of games) {
        if(Array.isArray(game.scores)&&
            Number.isSafeInteger(game.learner_seat)&&
            game.learner_seat>=0&&game.learner_seat<game.scores.length&&
            game.scores.every(Number.isFinite)) {
            const own=game.scores[game.learner_seat];
            const others=game.scores.filter((_,i)=>i!==game.learner_seat);
            scoreSum+=own;
            if(others.length) {
                marginSum+=own-others.reduce((a,b)=>a+b,0)/others.length;
                margins++;
            }
            scored++;
        }
        if(Number.isFinite(game.turns)&&game.turns>=0) {
            turnsSum+=game.turns;turns++;
        }
        const exact=game.authoritative_gameplay;
        if(exact&&typeof exact==='object') {
            if(Number.isSafeInteger(exact.food_gathered)&&exact.food_gathered>=0) {
                exactFood+=exact.food_gathered;foodGames++;
            }
            if(Number.isSafeInteger(exact.combat_kills)&&exact.combat_kills>=0) {
                exactKills+=exact.combat_kills;killGames++;
            }
        }
    }
    if(scored) {
        add('training_mean_score',scoreSum/scored,'scalar');
        add('training_scored_matches',scored,'count');
    }
    // A solo match has a score but no opponent-relative margin.
    if(margins) {
        add('training_mean_score_margin',marginSum/margins,'scalar');
        add('training_opponent_margin_matches',margins,'count');
    }
    if(turns)add('training_mean_match_turns',turnsSum/turns,'scalar');
    if(foodGames) {
        add('food_gathered_exact',exactFood,'count');
        add('food_gathered_verified_matches',foodGames,'count');
    }
    if(killGames) {
        add('combat_kills_exact',exactKills,'count');
        add('combat_kills_verified_matches',killGames,'count');
    }

    const gameplay=row.gameplay||{};
    for(const name of ['births_observed','deaths_observed',
        'food_disappearances_near_own_ant','combat_exposed_ant_turns',
        'transitions_without_final_observation','population_peak'])
        add(name,gameplay[name],'count');
    if(Number.isFinite(gameplay.visible_food_sum)&&
        Number.isFinite(gameplay.food_samples)&&gameplay.food_samples>0)
        add('visible_food_mean',gameplay.visible_food_sum/gameplay.food_samples,'scalar');
    if(Number.isFinite(gameplay.combat_exposed_ant_turns)&&
        Number.isFinite(gameplay.population_sum)&&gameplay.population_sum>0)
        add('combat_exposure_fraction',
            gameplay.combat_exposed_ant_turns/gameplay.population_sum,'ratio');

    // Each collector is a separately identified contributor; remote transfer
    // roundtrip includes transmission and worker compute, not network-only time.
    for(const [i,worker] of (Array.isArray(row.collector_samples)
        ?row.collector_samples:[]).slice(0,9).entries()) {
        add('collector_'+i+'_ant_decisions',worker.ant_decisions,'count');
        add('collector_'+i+'_engine_steps',worker.engine_steps,'count');
        add('collector_'+i+'_simulation_match_steps',
            worker.simulation_match_steps,'count');
        for(const [suffix,name] of [
            ['sim_matches','simulation_active_matches'],
            ['sim_target','simulation_target_matches'],
            ['sim_changes','simulation_concurrency_adjustments'],
            ['sim_accepted','simulation_tuner_accepted'],
            ['sim_settled','simulation_tuner_settled'],
            ['sim_restored','simulation_target_restored'],
            ['sim_proven','simulation_proven_target']])
            add('collector_'+i+'_'+suffix,worker[name],'count');
        add('collector_'+i+'_sim_rss_mb',
            worker.simulation_engine_memory_mb,'mb');
        add('collector_'+i+'_sim_prefetch_engine_steps',
            worker.simulation_prefetch_engine_steps,'count');
        for(const [suffix,name] of [
            ['sim_engine_roundtrip_seconds','simulation_engine_roundtrip_seconds'],
            ['sim_dispatch_queue_seconds','simulation_dispatch_queue_seconds'],
            ['sim_batch_wall_seconds','simulation_batch_wall_seconds'],
            ['sim_last_child_gap_seconds','simulation_last_child_gap_seconds'],
            ['sim_prefetch_overlap_seconds','simulation_prefetch_overlap_seconds']])
            add('collector_'+i+'_'+suffix,worker[name],'seconds');
        add('collector_'+i+'_inference_shape_groups',
            worker.rollout_inference_shape_groups,'count');
        add('collector_'+i+'_inference_readbacks',
            worker.rollout_inference_readbacks,'count');
        add('collector_'+i+'_inference_forward_chunks',
            worker.rollout_inference_forward_chunks,'count');
        add('collector_'+i+'_opponent_shape_groups',
            worker.rollout_opponent_shape_groups,'count');
        add('collector_'+i+'_opponent_readbacks',
            worker.rollout_opponent_readbacks,'count');
        add('collector_'+i+'_action_target',worker.action_target,'count');
        add('collector_'+i+'_roundtrip_seconds',
            worker.transport_roundtrip_seconds,'seconds');
        add('collector_'+i+'_elapsed_seconds',worker.elapsed_seconds,'seconds');
        add('collector_'+i+'_service_seconds',worker.collector_service_seconds,'seconds');
        add('collector_'+i+'_record_encode_seconds',
            worker.collector_record_encode_seconds,'seconds');
        // Separately timed remote/learner stages are observational; never
        // treat socket send/receive blocking as measured network bandwidth.
        for(const [suffix,name] of [
            ['serialize_seconds','collector_client_serialize_seconds'],
            ['compress_seconds','collector_client_compress_seconds'],
            ['send_seconds','collector_client_send_seconds'],
            ['receive_seconds','collector_client_receive_seconds'],
            ['decode_seconds','collector_client_decode_seconds'],
            ['ingestion_seconds','collector_learner_ingestion_seconds'],
            ['worker_receive_seconds','collector_worker_receive_seconds'],
            ['worker_decode_seconds','collector_worker_decode_seconds'],
            ['client_auth_seconds','collector_client_authenticate_seconds'],
            ['reply_auth_seconds','collector_client_authenticate_reply_seconds'],
            ['reply_decompress_seconds','collector_client_decompress_seconds'],
            ['reply_deserialize_seconds','collector_client_deserialize_seconds'],
            ['worker_auth_seconds','collector_worker_authenticate_seconds'],
            ['worker_decompress_seconds','collector_worker_decompress_seconds'],
            ['worker_deserialize_seconds','collector_worker_deserialize_seconds'],
            ['prev_reply_serial_seconds','collector_worker_previous_reply_serialize_seconds'],
            ['prev_reply_compress_seconds','collector_worker_previous_reply_compress_seconds'],
            ['prev_reply_auth_seconds','collector_worker_previous_reply_authenticate_seconds'],
            ['prev_reply_send_seconds','collector_worker_previous_reply_send_seconds'],
            ['prev_reply_service_seconds','collector_worker_previous_reply_service_seconds']])
            add('collector_'+i+'_'+suffix,worker[name],'seconds');
        // Reply timing from the worker necessarily arrives one PPO update
        // later; never label these durations as current-update service time.
        add('collector_'+i+'_prev_reply_update',
            worker.collector_worker_previous_reply_update,'count');
        // Preserve two compact summary fields for each previous worker
        // reply even when the 32-KiB source envelope cannot hold every
        // optional timing breakdown from many independent collectors.
        const priorStages=[
            worker.collector_worker_previous_reply_serialize_seconds,
            worker.collector_worker_previous_reply_compress_seconds,
            worker.collector_worker_previous_reply_authenticate_seconds];
        if(priorStages.every(v=>Number.isFinite(v)&&v>=0))
            add('collector_'+i+'_prev_reply_prepare_seconds',
                priorStages.reduce((sum,v)=>sum+v,0),'seconds');
        add('collector_'+i+'_request_wire_bytes',
            worker.collector_request_wire_bytes,'bytes');
        add('collector_'+i+'_response_wire_bytes',
            worker.collector_response_wire_bytes,'bytes');
        add('collector_'+i+'_rollout_engine_step_seconds',
            worker.rollout_engine_step_seconds,'seconds');
        add('collector_'+i+'_observation_prepare_seconds',
            worker.rollout_observation_prepare_seconds,'seconds');
        add('collector_'+i+'_learner_inference_seconds',
            worker.rollout_learner_inference_seconds,'seconds');
        add('collector_'+i+'_learner_gpu_stream_seconds',
            worker.rollout_learner_gpu_stream_seconds,'seconds');
        add('collector_'+i+'_inference_pixel_samples',
            worker.rollout_inference_pixel_samples,'count');
        add('collector_'+i+'_gpu_batch_used_pixels',
            worker.rollout_gpu_batch_used_pixels,'count');
        add('collector_'+i+'_gpu_batch_vram_capped',
            worker.rollout_gpu_batch_vram_capped,'count');
        for(const [suffix,name] of [
            ['gpu_batch_target_pixels','rollout_gpu_batch_target_pixels'],
            ['gpu_batch_next_target','rollout_gpu_batch_next_target'],
            ['gpu_batch_adjustments','rollout_gpu_batch_adjustments'],
            ['gpu_batch_accepted','rollout_gpu_batch_accepted'],
            ['gpu_batch_settled','rollout_gpu_batch_settled'],
            ['gpu_batch_restored','rollout_gpu_batch_restored'],
            ['gpu_batch_changed_next','rollout_gpu_batch_changed_next'],
            ['gpu_batch_proven_target','rollout_gpu_batch_proven_target']])
            add('collector_'+i+'_'+suffix,worker[name],'count');
        add('collector_'+i+'_opponent_processing_seconds',
            worker.rollout_opponent_processing_seconds,'seconds');
        add('collector_'+i+'_model_prepare_seconds',
            worker.model_prepare_seconds,'seconds');
        add('collector_'+i+'_snapshot_cache_files',
            worker.snapshot_cache_files,'count');
        add('collector_'+i+'_snapshot_cache_bytes',
            worker.snapshot_cache_bytes,'bytes');
        // This includes input transfer, remote compute and returned data;
        // unlike GPU busy time, it measures useful delivered experience.
        const elapsed=Number.isFinite(worker.elapsed_seconds)
            ?worker.elapsed_seconds:worker.transport_roundtrip_seconds;
        if(Number.isFinite(elapsed)&&elapsed>0) {
            add('collector_'+i+'_decisions_per_second',
                worker.ant_decisions/elapsed,'rate');
            add('collector_'+i+'_engine_steps_per_second',
                worker.engine_steps/elapsed,'rate');
            add('collector_'+i+'_match_steps_per_second',
                worker.simulation_match_steps/elapsed,'rate');
        }
    }
    // This is only a source-report envelope: the TinyBrains committed row
    // retains the full transport profile. Never reject a valid many-worker
    // update merely because optional detailed timings overflow the Hub's
    // fixed project-neutral 512-field / 32-KiB progress contract.
    // Keep the compact previous-reply summary for every worker as long as
    // possible, dropping finer breakdowns across workers before summaries.
    const optionalSuffixes=[
        'client_auth_seconds','reply_auth_seconds',
        'reply_decompress_seconds','reply_deserialize_seconds',
        'worker_auth_seconds','worker_decompress_seconds',
        'worker_deserialize_seconds',
        'prev_reply_serial_seconds','prev_reply_compress_seconds',
        'prev_reply_auth_seconds'];
    const collectors=Math.min(9,Array.isArray(row.collector_samples)
        ?row.collector_samples.length:0);
    const fits=()=>Object.keys(metrics).length<=500&&
        JSON.stringify(metrics).length<=31000;
    if(!fits()) {
        for(const suffix of optionalSuffixes) {
            // Delete a detail for all workers before deleting a more
            // valuable stage from just one worker.
            for(let i=collectors-1;i>=0;i--) {
                delete metrics['collector_'+i+'_'+suffix];
                if(fits())break;
            }
            if(fits())break;
        }
        // Previous-reply diagnosis needs all three fields from the same
        // update. Remove complete per-worker groups rather than publishing
        // a partial summary that can no longer be interpreted accurately.
        if(!fits())for(let i=collectors-1;i>=0;i--) {
            for(const suffix of ['prev_reply_prepare_seconds',
                                 'prev_reply_send_seconds',
                                 'prev_reply_service_seconds',
                                 'prev_reply_update'])
                delete metrics['collector_'+i+'_'+suffix];
            if(fits())break;
        }
    }
    return metrics;
}


function approvedTailnetEndpoint(value) {
    if(typeof value!=='string'||!/^100\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{1,5}$/.test(value))
        return false;
    const [ip,rawPort]=value.split(':');
    const octets=ip.split('.').map(Number),port=Number(rawPort);
    return octets[1]>=64&&octets[1]<=127&&
        octets.slice(2).every(n=>n>=0&&n<=255)&&port>=1&&port<=65535;
}

function fullDir(name) {
    if(typeof name!=='string'||!path.isAbsolute(name)||
        fs.realpathSync(name)!==name||!fs.statSync(name).isDirectory())
        throw Error('Local TinyBrains path must be an existing non-symlink directory.');
    if(!/^[A-Za-z0-9_./-]+$/.test(name))
        throw Error('Local TinyBrains paths cannot contain whitespace or shell syntax.');
    return name;
}
function runRelative(folder,filename) {
    if(typeof filename!=='string')throw Error('Invalid frozen run relative path.');
    const segments=filename.replace(/\\/g,'/').split('/');
    if(segments.some(part=>!part||part==='.'||part==='..'||part.includes(':')))
        throw Error('Unsafe saved gate checkpoint path.');
    return path.join(folder,...segments);
}
function stageFile(source,target) {
    fs.mkdirSync(path.dirname(target),{recursive:true,mode:0o700});
    fs.copyFileSync(source,target,fs.constants.COPYFILE_EXCL);
}
function createTinyBrainsPersistentDockerAdapter({
    projectRoot,hubRoot,artifactRoot,sourceRunRoot,engineCacheRoot,
    seedCheckpointSha256,seedPpoUpdates,profiles,
    image='tinybrains-rocm:halifax-node',sourceWindowsRoot='B:/TinyBrains/Ants-MAPOCA',
    enableDualGpu=false,remoteCollectors=null,profilePpo=false,
    cpuParallelism='allocation',
}) {
    if(!['profile','allocation'].includes(cpuParallelism))
        throw Error('TinyBrains CPU concurrency must be pinned to its profile or allocated CPU budget.');
    if(typeof profilePpo!=='boolean')
        throw Error('PPO profiling requires an explicit local boolean opt-in.');
    if(typeof enableDualGpu!=='boolean')
        throw Error('Dual GPU enablement requires an explicit local boolean opt-in.');
    let remote=null;
    if(remoteCollectors!==null) {
        if(!remoteCollectors||typeof remoteCollectors!=='object'||
            Array.isArray(remoteCollectors)||
            Object.keys(remoteCollectors).sort().join(',')!=='endpoints,keyDirectory'||
            !Array.isArray(remoteCollectors.endpoints)||
            !remoteCollectors.endpoints.length||remoteCollectors.endpoints.length>8||
            remoteCollectors.endpoints.some(endpoint=>!approvedTailnetEndpoint(endpoint))||
            new Set(remoteCollectors.endpoints).size!==remoteCollectors.endpoints.length)
            throw Error('Remote TinyBrains collectors require unique, locally pinned tailnet IPv4 endpoints.');
        const keyDirectory=fullDir(remoteCollectors.keyDirectory);
        const keyFile=path.join(keyDirectory,'rollout.key');
        const stat=fs.lstatSync(keyFile);
        if(!stat.isFile()||fs.realpathSync(keyFile)!==keyFile||
            (stat.mode&0o077)!==0||stat.size<32||stat.size>4096)
            throw Error('Remote collector rollout.key must be an existing private regular file.');
        remote={keyDirectory,endpoints:remoteCollectors.endpoints.slice(),
            keyFingerprint:crypto.createHash('sha256').update(fs.readFileSync(keyFile)).digest('hex')};
    }
    if(process.platform!=='linux'||!SHA.test(String(seedCheckpointSha256||''))||
        !Number.isSafeInteger(seedPpoUpdates)||seedPpoUpdates<1)
        throw Error('Persistent TinyBrains Docker import requires Linux and a pinned checkpoint hash.');
    const project=fullDir(projectRoot),hub=fullDir(hubRoot),
        artifacts=fullDir(artifactRoot),source=fullDir(sourceRunRoot),
        cache=fullDir(engineCacheRoot);
    if(project===artifacts||project===source||artifacts===source||
        source.startsWith(artifacts+'/')||artifacts.startsWith(source+'/'))
        throw Error('Hub job artifacts and immutable TinyBrains import must not overlap.');
    if(remote&&[project,hub,artifacts,source,cache].some(folder=>
        remote.keyDirectory===folder||
        remote.keyDirectory.startsWith(folder+path.sep)||
        folder.startsWith(remote.keyDirectory+path.sep)))
        throw Error('Private rollout key directory must not overlap a project, checkpoint, Hub or writable Docker mount.');
    const engine=path.join(project,'tools/tinybrains-0.5.0/tinybrains');
    const result=spawnSync(engine,['--version'],{
        timeout:5000,encoding:'utf8',windowsHide:true,
    });
    if(result.status!==0||result.stdout.trim()!=='tinybrains 0.5.0')
        throw Error('The Linux TinyBrains 0.5.0 engine is unavailable.');
    const original=path.join(source,'self-play/latest.pt');
    if(modelFileSha256(original)!==seedCheckpointSha256)
        throw Error('Imported Exeter checkpoint differs from operator-pinned SHA-256.');
    for(const name of ['run-spec.json','gate-passed.json']) {
        if(!fs.statSync(path.join(source,name)).isFile())
            throw Error('The saved TinyBrains run is missing '+name);
    }
    const gate=JSON.parse(fs.readFileSync(path.join(source,'gate-passed.json'),'utf8'));
    const candidate=runRelative(source,gate.checkpoint);
    if(modelFileSha256(candidate)!==gate.checkpoint_sha256)
        throw Error('Original gate evidence is missing or has changed.');
    const frozen=fs.readFileSync(path.join(source,'run-spec.json'));
    const evidence=fs.readFileSync(path.join(source,'gate-passed.json'));
    if(!profiles||typeof profiles!=='object'||Array.isArray(profiles)||
        !Object.keys(profiles).length||Object.keys(profiles).length>16)
        throw Error('Specify approved local persistent PPO profiles.');
    const approved=new Map(Object.entries(profiles).map(([id,p])=>{
        if(!PROFILE.test(id)||p?.device!=='cuda'||
            !['threads','live_matches','waves','rollout_actions','device'].every(k=>Object.hasOwn(p,k))||
            Object.keys(p).length!==5||
            [p.threads,p.live_matches,p.waves,p.rollout_actions].some(n=>!Number.isSafeInteger(n)||n<1)||
            p.live_matches%p.waves||p.threads>256||
            p.live_matches>512||p.rollout_actions>1048576)
            throw Error('Invalid approved TinyBrains persistent profile.');
        const text=JSON.stringify(p,null,2)+'\n';
        return [id,{profile:p,text,sha256:crypto.createHash('sha256').update(text).digest('hex')}];
    }));
    if(typeof sourceWindowsRoot!=='string'||
        !/^[A-Za-z]:\/[A-Za-z0-9_./-]+$/.test(sourceWindowsRoot))
        throw Error('Invalid original Windows project path mapping.');
    const originalSpec=JSON.parse(frozen);
    if(originalSpec?.model_config?.training?.algorithm!=='colony_ppo'||
        !Number.isSafeInteger(originalSpec.self_play_updates))
        throw Error('Original run is not the approved persistent colony-PPO lineage.');

    function frozenRemoteKeyIntact() {
        if(!remote)return true;
        const file=path.join(remote.keyDirectory,'rollout.key');
        const stat=fs.lstatSync(file);
        return stat.isFile()&&fs.realpathSync(file)===file&&
            (stat.mode&0o077)===0&&stat.size>=32&&stat.size<=4096&&
            crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')===remote.keyFingerprint;
    }

    function targetFor(context) {
        if(context.project_id!=='tinybrains'||!UUID.test(context.job_id)||
            !UUID.test(context.artifact_lineage_root_id)||
            (context.continuation_of_job_id&&!UUID.test(context.continuation_of_job_id)))
            throw Error('Invalid Hub TinyBrains lineage identity.');
        return path.join(artifacts,context.job_id);
    }
    function frozenEvidenceIntact(folder) {
        return fs.readFileSync(path.join(folder,'run-spec.json')).equals(frozen)&&
            fs.readFileSync(path.join(folder,'gate-passed.json')).equals(evidence)&&
            modelFileSha256(runRelative(folder,gate.checkpoint))===gate.checkpoint_sha256;
    }
    function checkpointReady(out,text) {
        const profile=path.join(out,'hub-profile.json');
        const latest=path.join(out,'self-play/latest.pt');
        const stat=fs.statSync(latest);
        if(!stat.isFile()||stat.size<1||
            fs.readFileSync(profile,'utf8')!==text||
            !frozenEvidenceIntact(out))
            throw Error('Finished TinyBrains job does not have its original frozen lineage and checkpoint.');
        const status=JSON.parse(fs.readFileSync(path.join(out,'status.json'),'utf8'));
        const start=JSON.parse(fs.readFileSync(path.join(out,'hub-start-update.json'),'utf8'));
        if(!Number.isSafeInteger(start.update)||start.update<1||
            !['stopped','self_play_paused','self_play_completed'].includes(status.phase)||
            !Number.isSafeInteger(status.ppo_updates)||status.ppo_updates<start.update)
            throw Error('TinyBrains job has no valid checkpoint-boundary status.');
        return latest;
    }
    const docker={
        image,workdir:PROJECT_IN,uid:process.getuid(),gid:process.getgid(),
        remote_host_network:Boolean(remote),
        mounts:[
            {host:project,target:PROJECT_IN,readonly:true},
            {host:hub,target:HUB_IN,readonly:true},
            {host:artifacts,target:JOBS_IN,readonly:false},
            {host:source,target:SOURCE_IN,readonly:true},
            {host:cache,target:CACHE_IN,readonly:false},
            ...(remote?[{host:remote.keyDirectory,target:REMOTE_KEY_IN,readonly:true}]:[]),
        ],
        env:{
            HOME:'/tmp',PYTHONDONTWRITEBYTECODE:'1',
            // On Halifax's R9700, MIOpen's default solver selection made
            // dilated-convolution backward passes 7-10x slower in isolation.
            // FAST improved all three observed dilations without model changes.
            MIOPEN_FIND_MODE:'FAST',
            TINYBRAINS_SOURCE_ROOT:sourceWindowsRoot,
            TINYBRAINS_RUNS_ROOT:'/workspace/experiments/tinybrains',
            TINYBRAINS_HOME:CACHE_IN,
            TINYBRAINS_REGISTRY:PROJECT_IN+'/games.toml',
            TINYBRAINS:PROJECT_IN+'/tools/tinybrains-0.5.0/tinybrains',
            PYTHONPATH:PROJECT_IN+'/src:'+PROJECT_IN+'/upstream/ants/baselines/src:'+HUB_IN+'/Training',
            ...(profilePpo?{TINYBRAINS_PPO_PROFILE:'1'}:{}),
        },
    };
    const executable='/opt/tinybrains-venv/bin/python';
    const runner=HUB_IN+'/Training/experiment_hub_tinybrains_persistent.py';
    return {
        executable,cwd:PROJECT_IN,docker,stop_policy:'interrupt-main',
        artifact_export_dir(jobId) {
            if(!UUID.test(String(jobId||'')))throw Error('Invalid TinyBrains job identity.');
            return path.join(artifacts,jobId);
        },
        gpu_execution:'cuda',
        gpu_sharing:'cooperative-pytorch',
        tinybrains_profiles:Object.fromEntries([...approved].map(([id,p])=>[id,p.sha256])),
        tinybrains_cuda_profiles:[...approved.keys()],
        tinybrains_thread_overrides:[...approved.keys()],
        tinybrains_cpu_fallback_profiles:[...approved.keys()],
        tinybrains_required_import_profiles:[...approved.keys()],
        tinybrains_dual_gpu_profiles:enableDualGpu?[...approved.keys()]:[],
        tinybrains_remote_rollout_profiles:remote?[...approved.keys()]:[],
        tinybrains_remote_rollout_count:remote?remote.endpoints.length:0,
        tinybrains_remote_rollout_endpoints:remote?remote.endpoints.slice():[],
        tinybrains_remote_key_fingerprint:remote?remote.keyFingerprint:null,
        ...(enableDualGpu?{gpu_dual_execution:'split-ppo'}:{}),
        tinybrains_imports:Object.fromEntries([...approved.keys()].map(id=>[
            'exeter_'+id,{profile:id,sha256:seedCheckpointSha256},
        ])),
        checkpoint_stop_file:ctx=>path.join(targetFor(ctx),'STOP'),
        gpu_verify_args(parameters,context){
            const item=approved.get(parameters?.profile);
            const dual=parameters?.device==='dual';
            const ids=dual?context.gpu_pci_addresses:[context.gpu_pci_address];
            if(!item||item.sha256!==parameters.profile_sha256||
                (parameters?.device!==undefined&&parameters.device!=='dual')||
                !Array.isArray(ids)||ids.length!==(dual?2:1)||
                ids.some(pci=>!PCI.test(String(pci||'')))||
                new Set(ids).size!==ids.length)
                throw Error('Invalid approved TinyBrains ROCm proof.');
            return ['-B',HUB_IN+'/Training/experiment_hub_tinybrains_rocm_verify.py',...ids];
        },
        args(parameters,context) {
            const {profile,profile_sha256,updates,threads,device,remote_rollouts,
                rollout_action_weights,import_checkpoint,import_checkpoint_sha256}=parameters;
            const item=approved.get(profile);
            if(remote_rollouts===true&&!frozenRemoteKeyIntact())
                throw Error('Remote rollout key was rotated or changed after local adapter approval.');
            if(!item||item.sha256!==profile_sha256||
                Object.keys(parameters).some(k=>!['profile','profile_sha256','updates',
                    'threads','device','remote_rollouts','rollout_action_weights',
                    'import_checkpoint','import_checkpoint_sha256'].includes(k))||
                (remote_rollouts!==undefined&&
                    (remote_rollouts!==true||!remote||device==='cpu'))||
                (rollout_action_weights!==undefined&&
                    (remote_rollouts!==true||!Array.isArray(rollout_action_weights)||
                     rollout_action_weights.length!==remote.endpoints.length+1||
                     rollout_action_weights.some(w=>
                         !Number.isSafeInteger(w)||w<1||w>1024)))||
                (device!==undefined&&!['cpu','dual'].includes(device))||
                (threads!==undefined&&(!Number.isSafeInteger(threads)||threads<1||threads>256))||
                !Number.isSafeInteger(updates)||updates<1||updates>10000000||
                (device==='cpu'?Boolean(context.gpu_pci_address||
                    context.gpu_pci_addresses||context.gpu_cuda_uuid):
                    device==='dual'
                        ?context.gpu_mode!=='exclusive'||
                            !Array.isArray(context.gpu_pci_addresses)||
                            context.gpu_pci_addresses.length!==2||
                            context.gpu_pci_addresses.some(pci=>!PCI.test(String(pci||'')))||
                            new Set(context.gpu_pci_addresses).size!==2
                        :!PCI.test(String(context.gpu_pci_address||''))||
                            context.gpu_pci_addresses!==undefined)||
                (device!=='cpu'&&context.gpu_mode==='shared'&&
                    (!Number.isSafeInteger(context.gpu_vram_mb)||
                        context.gpu_vram_mb<1||
                        !Number.isSafeInteger(context.gpu_total_vram_mb)||
                        context.gpu_vram_mb>context.gpu_total_vram_mb||
                        !Number.isSafeInteger(context.gpu_compute_pct)||
                        context.gpu_compute_pct<1||context.gpu_compute_pct>100)))
                throw Error('Invalid immutable TinyBrains Hub training request.');
            const parent=context.continuation_of_job_id;
            if(parent?
                import_checkpoint!==undefined||import_checkpoint_sha256!==undefined:
                import_checkpoint!=='exeter_'+profile||
                    import_checkpoint_sha256!==seedCheckpointSha256)
                throw Error('A new run must import the pinned Exeter lineage or explicitly continue its parent.');
            let sourceCheckpoint=original;
            let startingUpdate=seedPpoUpdates;
            if(parent) {
                const predecessor=path.join(artifacts,parent);
                sourceCheckpoint=checkpointReady(predecessor,item.text);
                startingUpdate=JSON.parse(fs.readFileSync(
                    path.join(predecessor,'status.json'),'utf8')).ppo_updates;
                if(context.expected_parent_model_sha256&&
                    modelFileSha256(sourceCheckpoint)!==context.expected_parent_model_sha256)
                    throw Error('Continuation checkpoint identity changed.');
            }
            const sourceHash=modelFileSha256(sourceCheckpoint);
            if(!frozenEvidenceIntact(source))
                throw Error('Original TinyBrains frozen run evidence changed after adapter installation.');
            const out=targetFor(context);
            if(fs.existsSync(out)) {
                if(fs.existsSync(path.join(out,'status.json'))||
                    fs.readFileSync(path.join(out,'hub-profile.json'),'utf8')!==item.text||
                    modelFileSha256(path.join(out,'self-play/latest.pt'))!==sourceHash||
                    JSON.parse(fs.readFileSync(path.join(out,'hub-start-update.json'),'utf8')).update!==startingUpdate||
                    !frozenEvidenceIntact(out))
                    throw Error('Previously launched or changed Hub training output cannot be re-used.');
            }else{
                fs.mkdirSync(out,{mode:0o700});
                stageFile(sourceCheckpoint,path.join(out,'self-play/latest.pt'));
                stageFile(path.join(source,'run-spec.json'),path.join(out,'run-spec.json'));
                stageFile(path.join(source,'gate-passed.json'),path.join(out,'gate-passed.json'));
                stageFile(candidate,runRelative(out,gate.checkpoint));
                fs.writeFileSync(path.join(out,'hub-profile.json'),item.text,{flag:'wx',mode:0o600});
                fs.writeFileSync(path.join(out,'hub-start-update.json'),
                    JSON.stringify({update:startingUpdate})+'\n',{flag:'wx',mode:0o600});
            }
            // A machine-local choice, resolved once at launch from the frozen
            // CPU lease. Explicit per-job overrides keep their old meaning.
            const chosenThreads=threads??(cpuParallelism==='allocation'
                ?fixedCpuParallelism(context.cpu_quota,256)
                :item.profile.threads);
            if(!Number.isSafeInteger(context.ram_quota_mb)||
                context.ram_quota_mb<128||context.ram_quota_mb>1000000)
                throw Error('TinyBrains automatic simulation requires a frozen RAM lease.');
            return ['-B',runner,'--run',JOBS_IN+'/'+context.job_id,
                '--updates',String(updates),'--threads',String(chosenThreads),
                '--ram-budget-mb',String(context.ram_quota_mb),
                '--live-matches',String(item.profile.live_matches),
                '--waves',String(item.profile.waves),
                '--actions',String(item.profile.rollout_actions),
                ...(remote_rollouts===true?[...remote.endpoints.flatMap(endpoint=>
                    ['--remote-rollout-endpoint',endpoint]),
                    '--remote-rollout-key-file',REMOTE_KEY_IN+'/rollout.key']:[]),
                ...(rollout_action_weights?rollout_action_weights.flatMap(w=>
                    ['--remote-rollout-weight',String(w)]):[]),
                ...(device==='cpu'?['--device','cpu']:
                    device==='dual'
                        ?['--device','dual',...context.gpu_pci_addresses.flatMap(pci=>
                            ['--rocm-pci',pci])]
                        :['--rocm-pci',context.gpu_pci_address,
                            ...(context.gpu_mode==='shared'?
                                ['--gpu-vram-mb',String(context.gpu_vram_mb)]:[])])];
        },
        checkpoint_ready(context){
            const item=approved.get(context.frozen_parameters?.profile);
            if(!item||item.sha256!==context.frozen_parameters?.profile_sha256)
                throw Error('Unknown approved checkpoint profile.');
            checkpointReady(targetFor(context),item.text);
            return true;
        },
        model_checkpoint_sha256(context){
            const item=approved.get(context.frozen_parameters?.profile);
            if(!item||item.sha256!==context.frozen_parameters?.profile_sha256)
                throw Error('Unknown approved checkpoint profile.');
            return modelFileSha256(checkpointReady(targetFor(context),item.text));
        },
        readProgress(context){
            const file=path.join(targetFor(context),'self-play/history.jsonl');
            let fd;
            try{
                fd=fs.openSync(file,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||!stat.size)return null;
                // Completed training rows contain per-match evidence. They
                // can exceed the old 256 KiB tail limit and otherwise make
                // progress disappear just when many games complete. Expand
                // only as needed, with a hard bounded scan and diagnostic.
                let bytes=Math.min(stat.size,TAIL);
                let offset=0,str='',end=-1,lineStart=-1;
                while(true) {
                    offset=stat.size-bytes;
                    const buffer=Buffer.alloc(bytes);
                    if(fs.readSync(fd,buffer,0,buffer.length,offset)!==buffer.length)
                        throw Error('TinyBrains training history changed during read.');
                    str=buffer.toString('utf8');
                    end=str.lastIndexOf('\n');
                    lineStart=end<0?-1:str.lastIndexOf('\n',end-1);
                    if(end>=0&&(lineStart>=0||offset===0))break;
                    if(offset===0)return null;
                    if(bytes>=MAX_PROGRESS_ROW_TAIL)
                        throw Error('TinyBrains completed-update history row exceeds 8 MiB; export the original history and review telemetry volume.');
                    bytes=Math.min(stat.size,MAX_PROGRESS_ROW_TAIL,bytes*2);
                }
                const row=JSON.parse(str.slice(lineStart+1,end));
                if(!Number.isSafeInteger(row.update)||row.update<1||
                    !Number.isFinite(row.wall_seconds)||row.wall_seconds<=0||
                    !Number.isSafeInteger(row.engine_steps)||row.engine_steps<0)
                    throw Error('Invalid TinyBrains completed-update telemetry.');
                const start=JSON.parse(fs.readFileSync(
                    path.join(targetFor(context),'hub-start-update.json'),'utf8')).update;
                if(!Number.isSafeInteger(start)||row.update<start)
                    throw Error('Progress precedes the pinned imported PPO checkpoint.');
                // TinyBrains row.wall_seconds is collection + optimization,
                // which double-counts overlap between the two stages.
                // Cumulative elapsed_seconds differences are the measured
                // per-update wall time for throughput comparisons.
                let updateWall=null;
                if(Number.isFinite(row.elapsed_seconds)&&row.elapsed_seconds>0) {
                    if(row.update===start+1)
                        updateWall=row.elapsed_seconds;
                    else {
                        const priorStart=str.lastIndexOf('\n',lineStart-1);
                        const prior=(priorStart>=0||offset===0)
                            ?JSON.parse(str.slice(priorStart+1,lineStart))
                            :previousHistoryRow(fd,offset+lineStart);
                        if(prior?.update===row.update-1&&
                            Number.isFinite(prior.elapsed_seconds)&&
                            prior.elapsed_seconds>=0&&
                            row.elapsed_seconds>prior.elapsed_seconds)
                            updateWall=row.elapsed_seconds-prior.elapsed_seconds;
                    }
                }
                const metrics=recordedTinyBrainsMetrics(row,updateWall);
                return validateProgress({
                    schema_version:1,source:'hub-metrics-v1',experiment_id:context.job_id,
                    segment_id:'training',source_modified_at:stat.mtime.toISOString(),
                    steps_completed:row.update-start,metrics,
                },context.project_id);
            }catch(error){
                if(error.code==='ENOENT')return null;
                throw error;
            }finally{
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}
module.exports={createTinyBrainsPersistentDockerAdapter};
