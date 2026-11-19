'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {spawnSync, execFile} = require('node:child_process');
const {promisify} = require('node:util');
const execFileAsync = promisify(execFile);
const {validateProgress} = require('./experiment_hub_progress');
const {evaluationReader} = require('./experiment_hub_evaluation');

const PROJECT = 'bees_native';
const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PCI = /^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const ROCM_UUID = /^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const CUDA_UUID = /^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const sha256 = data => crypto.createHash('sha256').update(data).digest('hex');

function trustedFile(file) {
    if (typeof file !== 'string' || !path.isAbsolute(file))
        throw Error('Native Bees requires absolute machine-local file paths.');
    const resolved = fs.realpathSync(file);
    if (!fs.statSync(resolved).isFile()) throw Error('Native Bees input is not a file.');
    return resolved;
}

// The Hub job config owns the whole executable training task, not just the
// top-level JSON. The scene, source collision assets and model worker must
// remain byte-identical between separate jobs and checkpoint continuations.
// A bundle-only learner has no local scene, by design; its collector bundle
// receipts prove their own physical input identity.
function nativeSourceDependencies(runFile, config) {
    const references = new Map();
    const pin = (value, key) => {
        if (typeof value !== 'string' || !value)
            throw Error('Native Bees requires an existing ' + key + ' file.');
        const file = trustedFile(path.resolve(path.dirname(runFile), value));
        references.set(file, sha256(fs.readFileSync(file)));
    };
    if (!Object.hasOwn(config, 'rollout_bundles')) {
        if (!Array.isArray(config.levels) || !config.levels.length)
            throw Error('Native Bees source run must contain physical Levels.');
        for (const level of config.levels)
            pin(level?.scene, 'Level scene');
    }
    for (const key of ['policy_worker', 'collision_matrix', 'stage_palette']) {
        if (config[key] != null) pin(config[key], key);
    }
    return references;
}


// The manifest is the complete runnable model set, not just the learner
// checkpoint. Resolve every slot against its declared digest before leasing
// compute or advertising a checkpoint as resumable. Reuse already-verified
// learner digests to avoid hashing the same large file twice.
function verifyModelDependencies(manifest, checked, checkpoint, learnerSlot) {
    const document = JSON.parse(fs.readFileSync(manifest, 'utf8'));
    if (document?.format !== 'bees-native-model-v1' ||
        document.abi_version !== 26 ||
        typeof document.policy_signature !== 'string' ||
        !Buffer.byteLength(document.policy_signature, 'utf8') ||
        Buffer.byteLength(document.policy_signature, 'utf8') > 8192 ||
        !Array.isArray(document.models) || !document.models.length)
        throw Error('Invalid native Bees v26 model manifest.');
    const slots = new Set();
    let learnerMatches = 0;
    let requiresGpuInference = false;
    let supportsGpuInference = false;
    for (const model of document.models) {
        if (!model || !Number.isSafeInteger(model.slot) || model.slot < 0 ||
            model.slot > 0xffffffff || slots.has(model.slot) ||
            !['mlagents', 'onnx', 'torchscript'].includes(model.backend) ||
            typeof model.path !== 'string' || !model.path ||
            !/^[a-f0-9]{64}$/.test(model.sha256))
            throw Error('Invalid native Bees model slot.');
        slots.add(model.slot);
        if (model.backend === 'onnx') {
            const providers = model.providers ?? ['CPUExecutionProvider'];
            if (!Array.isArray(providers) || !providers.length ||
                providers.some(provider => typeof provider !== 'string' || !provider))
                throw Error('Invalid native Bees ONNX inference providers.');
            // ONNX uses its highest-priority available provider. A CPU
            // primary with an optional GPU fallback is not a GPU allocation.
            const gpuProvider = providers[0] !== 'CPUExecutionProvider';
            requiresGpuInference ||= gpuProvider;
            supportsGpuInference ||= gpuProvider;
        } else {
            // Each native policy child sees at most one GPU (cuda:0),
            // including when the supervisor leases two physical GPUs.
            const device = model.device ?? 'cpu';
            if (!['cpu', 'cuda', 'cuda:0'].includes(device))
                throw Error('Native Bees inference device exceeds the supported single-GPU lease.');
            requiresGpuInference ||= device !== 'cpu';
            supportsGpuInference = true; // PyTorch can move the pinned weights to a leased GPU.
        }
        const artifact = path.resolve(path.dirname(manifest), model.path);
        let digest = checked.get(artifact);
        if (digest === undefined) {
            digest = sha256(fs.readFileSync(trustedFile(artifact)));
            checked.set(artifact, digest);
        }
        if (digest !== model.sha256)
            throw Error('Native Bees model artifact differs from its manifest.');
        if (learnerSlot === undefined || model.slot === learnerSlot) {
            if (model.backend === 'mlagents' && artifact === checkpoint)
                learnerMatches++;
            else if (learnerSlot === undefined)
                throw Error('Mixed native Bees models require an explicit learner slot.');
        }
    }
    if (learnerMatches !== (learnerSlot === undefined ? document.models.length : 1))
        throw Error('Native Bees learner slot does not reference the pinned ML-Agents checkpoint.');
    return {requiresGpuInference, supportsGpuInference};
}

function learnerMeasurements(metrics) {
    const report = {};
    const add = (name, unit, value = metrics[name]) => {
        if (value != null) report[name] = {value, unit};
    };
    add('capture_read_and_validation_wall_seconds', 'seconds');
    add('capture_read_and_validation_cpu_seconds', 'seconds');
    add('trajectory_assembly_and_ingest_seconds', 'seconds', metrics.trajectory_assembly_and_ingest_wall_seconds);
    add('trajectory_ingest_cpu_seconds', 'seconds');
    add('trajectory_processing_cpu_seconds', 'seconds');
    add('optimizer_update_cpu_seconds', 'seconds');
    add('total_wall_seconds', 'seconds');
    add('total_cpu_seconds', 'seconds');
    for (const name of ['capture_peak_inflight_decisions', 'capture_peak_inflight_episodes',
        'empty_policy_terminal_boundaries',
        'capture_compact_opponent_decisions', 'capture_compact_opponent_boundaries',
        'episodes_without_policy_decisions', 'episodes_without_learner_decisions',
        'dynamic_policy_episodes', 'dynamic_policy_admissions',
        'dynamic_policy_admissions_without_decisions',
        'dynamic_policy_decision_phases_max',
        'dynamic_policy_decision_rows', 'dynamic_policy_learner_decision_rows',
        'dynamic_policy_peak_live_agents', 'reward_journal_events',
        'optimizer_torch_intraop_threads', 'optimizer_torch_interop_threads'])
        add(name, 'count');
    add('reward_journal_payload_bytes', 'bytes');
    add('optimizer_steps_per_update_second', 'rate');
    add('torch_cuda_process_peak_allocated_bytes', 'bytes');
    add('torch_cuda_process_peak_reserved_bytes', 'bytes');
    if (metrics.optimizer_gpu_workers != null) {
        const count = metrics.optimizer_gpu_workers;
        if (![0,1,2].includes(count) ||
            (count === 2 && (
                !Number.isSafeInteger(metrics.optimizer_synchronized_minibatches) ||
                metrics.optimizer_synchronized_minibatches < 1 ||
                metrics.optimizer_masked_loss_normalizations !==
                    metrics.optimizer_synchronized_minibatches ||
                typeof metrics.optimizer_gpu_model_consensus_sha256 !== 'string' ||
                !SHA256.test(metrics.optimizer_gpu_model_consensus_sha256) ||
                !Array.isArray(metrics.optimizer_gpu_per_device) ||
                metrics.optimizer_gpu_per_device.length !== 2)))
            throw Error('Invalid native optimizer GPU worker measurements.');
        add('optimizer_gpu_workers', 'count');
        if (count === 2) {
            add('optimizer_synchronized_minibatches', 'count');
            add('optimizer_masked_loss_normalizations', 'count');
            add('optimizer_gradient_submission_wall_seconds_sum', 'seconds');
            add('optimizer_gpu_update_wall_seconds_max', 'seconds');
            metrics.optimizer_gpu_per_device.forEach((worker,index) => {
                if (worker.rank !== index ||
                    worker.gradient_syncs !== metrics.optimizer_synchronized_minibatches ||
                    worker.masked_loss_syncs !== metrics.optimizer_masked_loss_normalizations ||
                    !Number.isFinite(worker.update_wall_seconds) ||
                    worker.update_wall_seconds < 0)
                    throw Error('Invalid native synchronized optimizer worker evidence.');
                report['optimizer_gpu_'+index+'_update_wall_seconds'] = {
                    value:worker.update_wall_seconds,unit:'seconds',
                };
                if (!Number.isFinite(worker.gradient_submission_wall_seconds) ||
                    worker.gradient_submission_wall_seconds < 0)
                    throw Error('Invalid native GPU gradient synchronization time.');
                report['optimizer_gpu_'+index+'_gradient_submission_seconds'] = {
                    value:worker.gradient_submission_wall_seconds,unit:'seconds',
                };
                for (const [source,target] of [
                    ['torch_cuda_process_peak_allocated_bytes','peak_allocated_bytes'],
                    ['torch_cuda_process_peak_reserved_bytes','peak_reserved_bytes'],
                ]) {
                    const value=worker[source];
                    if(!Number.isSafeInteger(value)||value<0)
                        throw Error('Invalid native GPU optimizer device memory evidence.');
                    report['optimizer_gpu_'+index+'_'+target]={value,unit:'bytes'};
                }
            });
        }
    }
    const missionDecisions = metrics.native_mission_learner_decisions;
    const verifiedDecisions = metrics.native_mission_learner_decision_rows_validated;
    const missionMix = metrics.native_mission_learner_decisions_by_kind;
    if (missionDecisions != null || verifiedDecisions != null || missionMix != null) {
        if (!Number.isSafeInteger(missionDecisions) || missionDecisions < 0 ||
            !Number.isSafeInteger(verifiedDecisions) ||
            verifiedDecisions !== missionDecisions ||
            !missionMix || typeof missionMix !== 'object' || Array.isArray(missionMix) ||
            Object.keys(missionMix).sort().join(',') !==
                ['demolition','escort','extraction','survival'].join(',') ||
            Object.values(missionMix).some(n => !Number.isSafeInteger(n) || n < 0) ||
            Object.values(missionMix).reduce((a,b)=>a+b,0) !== missionDecisions)
            throw Error('Native Bees objective-bearing learner decisions disagree with real optimizer inputs.');
        add('native_mission_learner_decisions', 'count');
        add('native_mission_learner_decision_rows_validated', 'count');
        for (const kind of ['survival','demolition','escort','extraction'])
            report['native_mission_learner_decisions_' + kind] = {
                value: missionMix[kind], unit: 'count',
            };
    }
    if (metrics.refreshed_opponent_slot != null) {
        report.self_play_opponent_promotion = {value: 1, unit: 'count'};
        report.self_play_opponent_slot = {value: metrics.refreshed_opponent_slot, unit: 'count'};
    }

    const outcomes = metrics.training_outcomes;
    if (!outcomes || !outcomes.episodes) return report;
    const outcome = (name, unit) => {
        if (outcomes[name] != null) report['training_' + name] = {value: outcomes[name], unit};
    };
    for (const name of ['head_to_head_episodes', 'self_play_episodes', 'timed_out_episodes',
        'bee_wins', 'human_wins', 'draws'])
        outcome(name, 'count');
    outcome('mean_episode_seconds', 'seconds');
    const attempts = outcomes.mission_attempts_by_kind;
    const completions = outcomes.mission_completions_by_kind;
    if (attempts != null || completions != null) {
        if (!attempts || !completions || typeof attempts !== 'object' ||
            typeof completions !== 'object' || Array.isArray(attempts) ||
            Array.isArray(completions) ||
            [...Object.keys(attempts), ...Object.keys(completions)]
                .some(kind => !['survival','demolition','escort','extraction'].includes(kind)))
            throw Error('Invalid native Bees physical training mission categories.');
        for (const kind of ['survival','demolition','escort','extraction']) {
            const total = attempts[kind] ?? 0;
            const successful = completions[kind] ?? 0;
            if (!Number.isSafeInteger(total) || !Number.isSafeInteger(successful) ||
                total < 0 || successful < 0 || successful > total)
                throw Error('Invalid native Bees physical training mission outcome totals.');
            report['training_mission_attempts_' + kind] = {value: total,unit:'count'};
            report['training_mission_completions_' + kind] = {value: successful,unit:'count'};
            if (total > 0) report['training_mission_completion_rate_' + kind] = {
                value:successful / total,unit:'ratio',
            };
        }
        for (const [attemptsKey,successKey] of [
            ['learner_objective_attempts','learner_objective_completions'],
            ['learner_interception_attempts','learner_interceptions'],
        ]) {
            const attempts = outcomes[attemptsKey];
            const successes = outcomes[successKey];
            if (!Number.isSafeInteger(attempts) || attempts < 0 ||
                !Number.isSafeInteger(successes) ||
                successes < 0 || successes > attempts)
                throw Error('Invalid native Bees physical mission learner result.');
            outcome(attemptsKey, 'count');
            outcome(successKey, 'count');
        }
        if (outcomes.learner_objective_attempts > 0)
            report.training_learner_objective_completion_rate = {
                value:outcomes.learner_objective_completions /
                    outcomes.learner_objective_attempts,unit:'ratio',
            };
        if (outcomes.learner_interception_attempts > 0)
            report.training_learner_interception_success_rate = {
                value:outcomes.learner_interceptions /
                    outcomes.learner_interception_attempts,unit:'ratio',
            };
    }
    const owners = ['bee', 'human'];
    if (outcomes.head_to_head_episodes > 0) {
        for (const name of ['learner_wins', 'learner_losses', 'learner_draws',
            'learner_as_bee_episodes', 'learner_as_human_episodes'])
            outcome(name, 'count');
        outcome('head_to_head_win_rate', 'ratio');
        owners.push('learner', 'opponent');
    }
    for (const owner of owners) {
        for (const name of ['enemy_hits', 'enemy_damage', 'enemy_tsv_loss', 'enemy_kills',
            'friendly_hits', 'friendly_damage', 'friendly_tsv_loss', 'friendly_kills'])
            outcome(owner + '_' + name, 'count');
        for (const name of ['retained_mined_tsv', 'retained_reward', 'ppo_reward_sum'])
            outcome(owner + '_' + name, 'scalar');
    }
    return report;
}

// Expose only actual simulator receipts. Older runners may omit some
// parallel-physics counters; missing measurements are never fabricated.
function rolloutGpuMeasurements(rollout) {
    const proof = rollout?.gpu_inference;
    if (proof == null) return {};
    const names = ['verified_workers', 'model_forwards', 'agent_rows'];
    if (names.some(name => !Number.isSafeInteger(proof[name]) ||
            proof[name] < 1) ||
        proof.model_forwards > proof.agent_rows ||
        proof.agent_rows > rollout.successful_policy_rows)
        throw Error('Invalid native Bees measured GPU inference receipt.');
    const metrics = {
        rollout_gpu_verified_workers: {value: proof.verified_workers, unit: 'count'},
        rollout_gpu_model_forwards: {value: proof.model_forwards, unit: 'count'},
        rollout_gpu_agent_rows: {value: proof.agent_rows, unit: 'count'},
    };
    if (proof.per_device !== undefined) {
        if (!Array.isArray(proof.per_device) ||
            ![1, 2].includes(proof.per_device.length) ||
            !Array.isArray(proof.gpu_pci_addresses) ||
            proof.per_device.length !== proof.gpu_pci_addresses.length ||
            proof.per_device.some((entry, index) =>
                !PCI.test(String(entry?.gpu_pci_address)) ||
                entry.gpu_pci_address !== proof.gpu_pci_addresses[index] ||
                ['rollout_processes','model_forwards','gpu_agent_rows',
                    'successful_world_steps','successful_policy_rows'].some(key =>
                    !Number.isSafeInteger(entry[key]) || entry[key] < 1
                ))
        ) throw Error('Invalid physical GPU attribution in native Bees rollout.');
        for (const [index, entry] of proof.per_device.entries()) {
            for (const [source, target] of [
                ['rollout_processes', 'workers'],
                ['model_forwards', 'model_forwards'],
                ['gpu_agent_rows', 'agent_rows'],
                ['successful_world_steps', 'world_steps'],
                ['successful_policy_rows', 'policy_rows'],
            ]) {
                metrics[`rollout_gpu_${index}_${target}`] = {
                    value: entry[source], unit: 'count',
                };
            }
        }
    }
    return metrics;
}

function rolloutPhysicsMeasurements(rollout) {
    const result = {};
    for (const field of [
        'requested_physics_worker_slots',
        'parallel_physics_rounds',
        'parallel_physics_direct_rounds',
        'parallel_physics_queued_rounds',
        'parallel_stage_id_serialized_rounds',
        'parallel_stage_id_serialized_arenas',
        'peak_parallel_physics_workers',
    ]) {
        if (rollout?.[field] != null)
            result['rollout_' + field] = {value: rollout[field], unit: 'count'};
    }
    if (rollout?.parallel_worker_cpu_wall_seconds_sum != null)
        result.rollout_parallel_worker_cpu_wall_seconds_sum = {
            value: rollout.parallel_worker_cpu_wall_seconds_sum, unit: 'seconds',
        };
    if (rollout?.parallel_physics_rounds > 0 &&
        Number.isSafeInteger(rollout.parallel_physics_direct_rounds) &&
        rollout.parallel_physics_direct_rounds >= 0 &&
        rollout.parallel_physics_direct_rounds <= rollout.parallel_physics_rounds)
        result.rollout_parallel_physics_direct_ratio = {
            value: rollout.parallel_physics_direct_rounds / rollout.parallel_physics_rounds,
            unit: 'ratio',
        };
    return result;
}

// Report only authenticated completed native rollout admissions. Future
// authored ships do not count as present policy agents before deployment.
function rolloutReinforcementMeasurements(rollout) {
    const admissions = rollout?.reinforcement_arrivals;
    if (admissions == null) return {};
    const result = {};
    for (const key of ['waves', 'squads', 'ships', 'policy_agents']) {
        const value = admissions[key];
        if (!Number.isSafeInteger(value) || value < 0)
            throw Error('Invalid native Bees reinforcement admission receipt.');
        result['rollout_reinforcement_' + key] = {value, unit: 'count'};
    }
    if (admissions.policy_agents > admissions.ships)
        throw Error('Policy admissions exceed physical reinforcement ships.');
    return result;
}

function rolloutQueenControllerMeasurements(rollout) {
    const counts = rollout?.queen_minion_controllers;
    if (counts == null) return {};
    const fields = [
        'policy_minions_bound', 'ungrouped_policy_minions_bound',
        'ordinary_minions_preserved',
    ];
    const result = {};
    for (const key of fields) {
        const value = counts[key];
        if (!Number.isSafeInteger(value) || value < 0)
            throw Error('Invalid native Bees dynamic minion ownership receipt.');
        result['rollout_queen_' + key] = {value, unit: 'count'};
    }
    if (counts.ungrouped_policy_minions_bound > counts.policy_minions_bound)
        throw Error('Native ungrouped Queen minions exceed all neural minion bindings.');
    if (counts.retired_policy_routes_reclaimed != null) {
        if (!Number.isSafeInteger(counts.retired_policy_routes_reclaimed) ||
            counts.retired_policy_routes_reclaimed < 0 ||
            counts.retired_policy_routes_reclaimed > counts.policy_minions_bound)
            throw Error('Invalid native reclaimed Queen policy routes.');
        result.rollout_queen_retired_policy_routes_reclaimed = {
            value: counts.retired_policy_routes_reclaimed, unit: 'count',
        };
    }
    return result;
}

// This is physical scheduler work across every completed arena, not an
// estimate of a machine's available CPU or the number of pending waves.
function rolloutReinforcementWakeupMeasurements(rollout) {
    const work = rollout?.reinforcement_wakeup_work;
    if (work == null) return {};
    const result = {};
    for (const key of ['idle_ticks_skipped', 'wakeup_scans']) {
        if (!Number.isSafeInteger(work[key]) || work[key] < 0)
            throw Error('Invalid native Bees reinforcement wakeup measurement.');
        result['rollout_reinforcement_' + key] = {value: work[key], unit: 'count'};
    }
    if (work.idle_ticks_skipped + work.wakeup_scans >
        rollout.successful_world_steps)
        throw Error('Native reinforcement checks exceed executed world steps.');
    return result;
}

// Only exact native episode-complete physical border counts are comparable.
// Missing fields in historical rollouts stay absent rather than fabricated.
// Direct counters from completed canonical physics ticks. They count
// real traversals avoided, not an assumed speedup or CPU utilization.
function rolloutLiveShipMeasurements(rollout) {
    const slots = rollout?.ship_slot_work;
    if (slots == null) return {};
    const metrics = {};
    const required = ['integrated_bodies', 'inactive_ship_slots_skipped',
        'inactive_ship_grid_slots_skipped'];
    const obstacle = ['inactive_obstacle_slots_skipped',
        'inactive_obstacle_grid_slots_skipped'];
    const present = obstacle.filter(key => slots[key] !== undefined);
    if (present.length !== 0 && present.length !== obstacle.length)
        throw Error('Incomplete native Bees pooled obstacle work receipt.');
    const projectile = slots.inactive_projectile_slots_skipped === undefined
        ? [] : ['inactive_projectile_slots_skipped'];
    for (const key of [...required, ...present, ...projectile]) {
        const value = slots[key];
        if (!Number.isSafeInteger(value) || value < 0)
            throw Error('Invalid native Bees active body-slot work receipt.');
        metrics['rollout_' + key] = {value, unit: 'count'};
    }
    return metrics;
}

function rolloutBorderMeasurements(rollout) {
    const border = rollout?.map_border_ships;
    if (border == null) return {};
    const fields = [
        'entries', 'movement_stops', 'override_skips',
        'lethal_hits', 'lethal_kills',
        'confinement_position_corrections', 'confinement_velocity_clamps',
        'confinement_unfittable_hulls', 'confinement_missing_profiles',
        'completed_episodes',
    ];
    const metrics = {};
    for (const field of fields) {
        const value = border[field];
        if (!Number.isSafeInteger(value) || value < 0)
            throw Error('Invalid native Bees physical MapBorder metrics.');
        metrics['rollout_map_border_' + field] = {value, unit: 'count'};
    }
    if (border.lethal_kills > border.lethal_hits ||
        ['movement_stops', 'override_skips', 'lethal_hits', 'lethal_kills']
            .some(field => border[field] > border.entries) ||
        border.completed_episodes !== rollout.episodes)
        throw Error('Native Bees MapBorder metrics do not match the completed rollout.');
    return metrics;
}

// These are actual startup/restart costs reported by the native scene
// source cache and verified by the training receipt. Across distributed
// collectors they are sums of PROCESS-LOCAL work, not a claim that all
// machines shared a single parse or that every avoided parse saved the
// same CPU time. Historical runs without cache measurements stay absent.
function rolloutSceneCacheMeasurements(rollout) {
    const cache = rollout?.source_scene_template_cache;
    if (cache == null) return {};
    if (cache.scope != null &&
        cache.scope !== 'sum_of_process_local_source_work')
        throw Error('Invalid native Bees scene template measurement scope.');
    const countFields = [
        'distinct_source_documents', 'distinct_parsed_layouts',
        'worlds_initialized', 'episodes_recycled', 'source_parse_count',
        'recycle_source_parses_avoided',
    ];
    const timeFields = [
        'source_parse_wall_seconds', 'initial_template_clone_wall_seconds',
        'recycled_template_clone_wall_seconds',
    ];
    if (countFields.some(name => !Number.isSafeInteger(cache[name]) ||
            cache[name] < 0) ||
        timeFields.some(name => !Number.isFinite(cache[name]) ||
            cache[name] < 0) ||
        cache.worlds_initialized !== rollout.worlds ||
        cache.episodes_recycled !== rollout.episodes - rollout.worlds ||
        cache.recycle_source_parses_avoided !== cache.episodes_recycled ||
        cache.source_parse_count !== cache.distinct_parsed_layouts ||
        cache.distinct_source_documents < 1 ||
        cache.distinct_source_documents > cache.distinct_parsed_layouts ||
        cache.distinct_parsed_layouts > cache.worlds_initialized)
        throw Error('Native Bees scene cache data contradicts completed training Worlds.');
    const result = {};
    for (const name of countFields)
        result['rollout_scene_' + name] = {value: cache[name], unit: 'count'};
    for (const name of timeFields)
        result['rollout_scene_' + name] = {value: cache[name], unit: 'seconds'};
    return result;
}

function rolloutPolicyBoundaryMeasurements(rollout) {
    const capture = rollout?.policy_boundary_capture;
    if (capture == null) return {};
    const report = {};
    for (const name of ['admission_rows', 'admission_frames',
        'retirement_rows', 'retirement_frames']) {
        const value = capture[name];
        if (!Number.isSafeInteger(value) || value < 0)
            throw Error('Invalid native Bees policy boundary receipt.');
        report['rollout_policy_boundary_' + name] = {value, unit: 'count'};
    }
    const seconds = capture.observation_build_wall_seconds;
    if (!Number.isFinite(seconds) || seconds < 0)
        throw Error('Invalid native Bees policy observation timing.');
    report.rollout_policy_boundary_observation_build_wall_seconds = {
        value: seconds, unit: 'seconds',
    };
    return report;
}

// These are actual feature rows produced by the unified simulation,
// not a count of authored missions or an estimate from win rates. Keep
// mission samples and target-discovery coverage comparable across workers
// and the separate Hub collector/learner process boundaries.
function rolloutPolicyObservationMeasurements(rollout) {
    const work = rollout?.policy_observations;
    if (work == null) return {};
    const report = {};
    const counters = ['rows_encoded','native_mission_rows',
        'allies_encoded','enemies_encoded','map_objects_encoded'];
    if (typeof work !== 'object' || Array.isArray(work) ||
        counters.some(key => !Number.isSafeInteger(work[key]) || work[key] < 0) ||
        work.native_mission_rows > work.rows_encoded)
        throw Error('Invalid native Bees policy observation work counters.');
    for (const key of counters)
        report['rollout_policy_' + key] = {value:work[key], unit:'count'};
    if (work.rows_encoded > 0)
        report.rollout_mission_observation_fraction = {
            value:work.native_mission_rows / work.rows_encoded,unit:'ratio',
        };
    const kinds = work.native_mission_rows_by_kind;
    const targetRows = work.native_mission_visible_target_rows;
    const destinationRows = work.native_mission_destination_rows;
    const available = [kinds, targetRows, destinationRows].filter(x=>x!==undefined);
    if (available.length > 0) {
        if (available.length !== 3 || !kinds || typeof kinds !== 'object' ||
            Array.isArray(kinds) || Object.keys(kinds).sort().join(',') !==
                ['demolition','escort','extraction','survival'].join(',') ||
            Object.values(kinds).some(x=>!Number.isSafeInteger(x)||x<0) ||
            Object.values(kinds).reduce((a,b)=>a+b,0) !== work.native_mission_rows ||
            !Number.isSafeInteger(targetRows) || targetRows < 0 ||
            targetRows > work.native_mission_rows ||
            !Number.isSafeInteger(destinationRows) || destinationRows < 0 ||
            destinationRows > work.native_mission_rows)
            throw Error('Native Bees source mission coverage counters are incomplete.');
        for (const kind of ['survival','demolition','escort','extraction'])
            report['rollout_mission_rows_' + kind] = {value:kinds[kind],unit:'count'};
        report.rollout_mission_visible_target_rows = {
            value:targetRows,unit:'count',
        };
        report.rollout_mission_destination_rows = {
            value:destinationRows,unit:'count',
        };
        if (work.native_mission_rows > 0) {
            report.rollout_mission_target_visibility_rate = {
                value: targetRows / work.native_mission_rows, unit:'ratio',
            };
            report.rollout_mission_destination_available_rate = {
                value: destinationRows / work.native_mission_rows, unit:'ratio',
            };
        }
    }
    return report;
}

function createNativeBeesAdapter({
    pythonPath, rolloutBinary, runConfig, trainerConfig, checkpoint, artifactRoot,
    learnerSlot, collectOnly = false, bundleInbox, rocrGpuUuids = {},
    evaluationRoot, evaluationSuites, gameLibrarySaveRoot,
}) {
    if (typeof collectOnly !== 'boolean') throw Error('Invalid native Bees collection mode.');
    // Local operator-owned PCI to ROCr UUID mappings are never job parameters.
    if (!rocrGpuUuids || typeof rocrGpuUuids !== 'object' || Array.isArray(rocrGpuUuids) ||
        Object.entries(rocrGpuUuids).some(([pci, uuid]) =>
            !PCI.test(pci) || typeof uuid !== 'string' || !ROCM_UUID.test(uuid)))
        throw Error('Invalid locally approved native Bees ROCm GPU UUID bindings.');
    const rocr_gpu_uuids = Object.freeze({...rocrGpuUuids});
    const project = collectOnly ? 'bees_native_collect' : PROJECT;
    if (collectOnly && (evaluationRoot != null || evaluationSuites != null))
        throw Error('Native Bees collection-only jobs cannot own learner evaluations.');
    const executable = trustedFile(pythonPath);
    const run = trustedFile(runConfig);
    const trainer = trustedFile(trainerConfig);
    const input = trustedFile(checkpoint);
    const runner = trustedFile(path.resolve(__dirname, '../native/tools/train_policy_round.py'));
    const gpuAllocationScript = trustedFile(path.resolve(__dirname,
        '../native/tools/native_gpu_allocation.py'));
    const contractScript = trustedFile(path.resolve(__dirname,
        '../native/tools/native_training_artifacts.py'));
    // These native Python modules are invoked by the already-pinned round
    // launcher and its PPO subprocess. They define the physical objective
    // ABI, durable capture parsing, and distributed collection acceptance.
    // Pin their installed bytes as one learner implementation; otherwise a
    // long-lived Hub job could execute different code after assignment.
    const trainingSources = [
        'native_mission_contract.py',
        'native_distributed_collection.py',
        'train_policy_capture.py',
        'native_training_outcomes.py',
        'read_policy_capture.py',
        'policy_worker.py',
    ].map(name => trustedFile(path.resolve(__dirname,
        '../native/tools', name))).concat([
        'bees_mlagents_ppo_compat.py',
        'bees_mlagents_structured_policy.py',
        'bees_learner_stall_diagnostics.py',
    ].map(name => trustedFile(path.resolve(__dirname, name))));
    if (typeof artifactRoot !== 'string' || !path.isAbsolute(artifactRoot))
        throw Error('Native Bees requires an absolute artifact root.');
    const artifacts = fs.realpathSync(artifactRoot);
    if (!fs.statSync(artifacts).isDirectory()) throw Error('Native Bees artifact root is not a directory.');
    const evaluator = collectOnly ? {} : evaluationReader(
        PROJECT, evaluationRoot, evaluationSuites,
        [path.resolve(__dirname, '..'), artifacts],
    );
    let inbox;
    if (bundleInbox !== undefined) {
        if (typeof bundleInbox !== 'string' || !path.isAbsolute(bundleInbox))
            throw Error('Native Bees bundle inbox requires an absolute path.');
        inbox = fs.realpathSync(bundleInbox);
        if (!fs.statSync(inbox).isDirectory() || inbox === artifacts)
            throw Error('Native Bees bundle inbox must be a separate existing directory.');
    }
    if (learnerSlot !== undefined &&
        (!Number.isSafeInteger(learnerSlot) || learnerSlot < 0 || learnerSlot > 0xffffffff))
        throw Error('Invalid native Bees learner slot.');

    const runBytes = fs.readFileSync(run);
    const config = JSON.parse(runBytes);
    const bundleRound = Object.hasOwn(config, 'rollout_bundles');
    const inboxLearner = !collectOnly && Boolean(inbox);
    if (inboxLearner && (!Array.isArray(config.rollout_bundles) || config.rollout_bundles.length))
        throw Error('Native Bees inbox learners require an empty rollout_bundles template.');
    if (collectOnly && bundleRound) throw Error('Native Bees collection requires live Levels.');
    // Optional operator-owned native-game gallery. This is not a job
    // parameter, Hub server path, or new training artifact store. The same
    // verified learner is playable using the existing game library.
    let gallery = null;
    if (gameLibrarySaveRoot !== undefined) {
        if (collectOnly || inboxLearner || bundleRound ||
            learnerSlot === undefined)
            throw Error('Native Bees Hub gallery needs a physical-Level learner with an explicit slot.');
        if (typeof gameLibrarySaveRoot !== 'string' ||
            !path.isAbsolute(gameLibrarySaveRoot))
            throw Error('Native Bees game library root must be an absolute machine-local directory.');
        gallery = fs.realpathSync(gameLibrarySaveRoot);
        if (!fs.statSync(gallery).isDirectory() || gallery === artifacts ||
            gallery.startsWith(artifacts + path.sep))
            throw Error('Native Bees player saves must be separate from Hub job artifacts.');
    }
    const publisher = gallery ? trustedFile(path.resolve(__dirname,
        '../native/tools/publish_hub_playable.py')) : null;
    const gallerySources = gallery ? [
        publisher,
        ...['native_game_registration.py', 'native_session_selection.py', 'native_curriculum.py']
            .map(name => trustedFile(path.resolve(__dirname, '../native/tools', name))),
    ] : [];
    const binary = bundleRound ? null : trustedFile(rolloutBinary);
    if (config.hivemind != null || config.recover_notifications?.length ||
        config.recover_outcome_journals?.length || config.model_updates?.length ||
        !path.isAbsolute(config.python || '') || trustedFile(config.python) !== executable ||
        typeof config.model_manifest !== 'string' || !config.model_manifest)
        throw Error('Native Bees Hub runs require a model-only template and the installed interpreter.');
    const manifest = trustedFile(path.resolve(path.dirname(run), config.model_manifest));
    const basePins = new Map([
        [run, sha256(runBytes)],
        ...[trainer, input, manifest].map(file => [file, sha256(fs.readFileSync(file))]),
    ]);
    const sourcePins = nativeSourceDependencies(run, config);
    let sourceContract = null;
    if (collectOnly) {
        // The exact shared Python implementation also signs the learner's
        // work orders. One process at adapter setup, never in the hot path.
        const probe = spawnSync(executable, ['-B', contractScript, run], {
            encoding: 'utf8', timeout: 15000, maxBuffer: 4096,
        });
        if (probe.error || probe.status !== 0 ||
            !/^[a-f0-9]{64}$/.test((probe.stdout || '').trim()))
            throw Error('Native Bees physical source identity could not be verified by the installed Python runtime.');
        sourceContract = probe.stdout.trim();
    }
    const pins = new Map([
        ...basePins,
        ...sourcePins,
        ...[runner, contractScript, gpuAllocationScript,
            ...trainingSources, ...(binary ? [binary] : []), ...gallerySources]
            .map(file => [file, sha256(fs.readFileSync(file))]),
    ]);
    const initialGpuSupport = verifyModelDependencies(
        manifest, new Map([[input, pins.get(input)]]), input, learnerSlot,
    );
    // Config identity is independent of machine-local absolute paths and
    // platform-specific Rust binary bytes. Those are still pinned locally
    // for execution, but a Windows/Linux rollout cannot share a single
    // ELF/PE digest. Scene/Stage/worker content is part of the experiment.
    const configIdentity = [...basePins.values(), learnerSlot ?? null,
        ...sourcePins.values()];
    if (collectOnly) configIdentity.push(project);
    if (inbox) configIdentity.push(inbox);
    const configSha256 = sha256(JSON.stringify(configIdentity));

    function jobRoot(context) {
        if (context.project_id !== project || !UUID.test(context.job_id))
            throw Error('Invalid native Bees job identity.');
        return path.join(artifacts, context.job_id);
    }

    function bundleArguments(bundles) {
        if (!Array.isArray(bundles) || !bundles.length)
            throw Error('Native Bees inbox learners require approved rollout bundles.');
        const seen = new Set();
        return bundles.flatMap(bundle => {
            const {job_id, sha256: digest} = bundle ?? {};
            if (typeof job_id !== 'string' || !UUID.test(job_id) || seen.has(job_id) ||
                typeof digest !== 'string' || !/^[a-f0-9]{64}$/.test(digest))
                throw Error('Invalid or duplicate native Bees rollout bundle identity.');
            seen.add(job_id);
            const receipt = path.join(inbox, job_id, 'rollout-bundle.json');
            if (trustedFile(receipt) !== receipt)
                throw Error('Native Bees rollout receipt must be inside its inbox job directory.');
            return ['--pinned-rollout-bundle', receipt, digest];
        });
    }

    function completionReceipt(output, name) {
        const file = path.join(output, name);
        let stat;
        try {
            stat = fs.lstatSync(file);
        } catch (error) {
            if (error.code === 'ENOENT') return null;
            throw error;
        }
        if (!stat.isFile() || stat.size < 1 || stat.size > 64 * 1024 || fs.realpathSync(file) !== file)
            throw Error('Invalid native Bees completion receipt file.');
        return {receipt: JSON.parse(fs.readFileSync(file, 'utf8')), stat};
    }

    function completedRound(output) {
        const result = completionReceipt(output, 'round-result.json');
        if (!result) return null;
        const {receipt, stat} = result;
        const metrics = receipt.learner_metrics;
        if (receipt.schema_version !== 1 || receipt.kind !== 'native_training_round_complete' ||
            metrics?.schema_version !== 1 || metrics.source !== 'bees-native-ma-poca-v1' ||
            metrics.learner_slot !== (learnerSlot ?? null) ||
            metrics.input_checkpoint_sha256 !== receipt.input_checkpoint_sha256 ||
            metrics.input_manifest_sha256 !== receipt.input_manifest_sha256 ||
            (metrics.refreshed_opponent_slot != null && (
                !Number.isSafeInteger(metrics.refreshed_opponent_slot) ||
                metrics.refreshed_opponent_slot < 0 ||
                metrics.refreshed_opponent_slot > 0xffffffff ||
                metrics.refreshed_opponent_slot === learnerSlot ||
                metrics.refreshed_opponent_from_checkpoint_sha256 !== receipt.next_checkpoint_sha256
            )) ||
            !Number.isSafeInteger(metrics.optimizer_input_steps) || metrics.optimizer_input_steps < 1)
            throw Error('Invalid native Bees completion receipt.');
        return {receipt, metrics, stat};
    }

    function verifiedOutput(round, output) {
        if (!round) throw Error('Native Bees continuation needs a completed round.');
        const {receipt, metrics} = round;
        const checkpoint = path.join(output, 'learner', 'checkpoint.pt');
        const manifest = path.join(output, 'learner', 'manifest.json');
        for (const [file, digest, metricDigest] of [
            [checkpoint, receipt.next_checkpoint_sha256, metrics.next_checkpoint_sha256],
            [manifest, receipt.next_manifest_sha256, metrics.next_manifest_sha256],
        ]) {
            if (trustedFile(file) !== file || digest !== metricDigest ||
                sha256(fs.readFileSync(file)) !== digest)
                throw Error('Native Bees output differs from its completion receipt.');
        }
        // A manifest may retain opponent artifacts outside the round output.
        // The complete runnable model set, not just checkpoint.pt, is the
        // unit of continuation readiness.
        const gpuSupport = verifyModelDependencies(
            manifest, new Map([[checkpoint, receipt.next_checkpoint_sha256]]),
            checkpoint, learnerSlot,
        );
        if (metrics.refreshed_opponent_slot != null) {
            const models = JSON.parse(fs.readFileSync(manifest, 'utf8')).models;
            const promoted = models.filter(m => m.slot === metrics.refreshed_opponent_slot);
            if (promoted.length !== 1 || promoted[0].backend !== 'mlagents' ||
                promoted[0].sha256 !== receipt.next_checkpoint_sha256 ||
                path.resolve(path.dirname(manifest), promoted[0].path) !== checkpoint)
                throw Error('Native Bees promoted opponent does not reference its committed checkpoint.');
        }
        return {checkpoint, manifest, checkpointDigest: receipt.next_checkpoint_sha256,
            manifestDigest: receipt.next_manifest_sha256, ...gpuSupport};
    }

    function roundInputs(context) {
        if (!context.continuation_of_job_id)
            return {checkpoint: input, manifest, checkpointDigest: pins.get(input),
                manifestDigest: pins.get(manifest), ...initialGpuSupport};
        if (collectOnly) throw Error('Native Bees collection requires an independent job.');
        if (!UUID.test(context.continuation_of_job_id) || context.continuation_of_job_id === context.job_id)
            throw Error('Invalid native Bees predecessor.');
        const parent = path.join(artifacts, context.continuation_of_job_id);
        return verifiedOutput(completedRound(parent), parent);
    }

    function gpuLeaseCount(context) {
        return Array.isArray(context.gpu_pci_addresses)
            ?context.gpu_pci_addresses.length
            :context.gpu_pci_address?1:0;
    }

    function verifyRoundGpuLease(context, receipt) {
        const count=gpuLeaseCount(context);
        // Inbox-only learners legitimately consume verified bundles with
        // CPU-side collection performed on other workers. Direct rounds and
        // collectors must prove exact device placement and real forwards.
        if(inboxLearner||bundleRound)return;
        const proof=receipt.rollout_metrics?.gpu_inference;
        if(count>0?(
            receipt.leased_gpu_inference!==true||
            (receipt.leased_gpu_count??1)!==count||
            receipt.rollout_processes<count||
            proof?.verified_workers!==receipt.rollout_processes||
            (count===2&&(
                !Array.isArray(proof?.gpu_pci_addresses)||
                proof.gpu_pci_addresses.length!==2||
                new Set(proof.gpu_pci_addresses).size!==2||
                proof.gpu_pci_addresses.some(pci=>
                    !context.gpu_pci_addresses.includes(pci))
            ))||
            !Number.isSafeInteger(proof?.model_forwards)||
            proof.model_forwards<receipt.rollout_processes
        ):(
            receipt.leased_gpu_inference!==undefined||
            receipt.leased_gpu_count!==undefined
        ))
            throw Error('Native Bees round evidence disagrees with the exact Hub GPU lease.');
    }

    function jobRound(context) {
        const output = jobRoot(context);
        const round = completedRound(output);
        if (!round) return null;
        verifyRoundGpuLease(context, round.receipt);
        if (!collectOnly && gpuLeaseCount(context) === 2) {
            const proof = round.metrics;
            if (proof.optimizer_gpu_workers !== 2 ||
                !Number.isSafeInteger(proof.optimizer_synchronized_minibatches) ||
                proof.optimizer_synchronized_minibatches < 1 ||
                proof.optimizer_masked_loss_normalizations !==
                    proof.optimizer_synchronized_minibatches ||
                typeof proof.optimizer_gpu_model_consensus_sha256 !== 'string' ||
                !SHA256.test(proof.optimizer_gpu_model_consensus_sha256) ||
                !Array.isArray(proof.optimizer_gpu_per_device) ||
                proof.optimizer_gpu_per_device.length !== 2 ||
                proof.optimizer_gpu_per_device.some((row, index) =>
                    row?.rank !== index || row.gradient_syncs !==
                        proof.optimizer_synchronized_minibatches ||
                    row.masked_loss_syncs !==
                        proof.optimizer_masked_loss_normalizations))
                throw Error('Native learner claimed two GPUs without verified synchronized policy updates.');
        }
        const inputs = roundInputs(context);
        if (round.receipt.input_checkpoint_sha256 !== inputs.checkpointDigest ||
            round.receipt.input_manifest_sha256 !== inputs.manifestDigest)
            throw Error('Native Bees completion receipt disagrees with the approved inputs.');
        if (context.frozen_parameters) {
            const expected = context.frozen_parameters.refresh_opponent_slot ?? null;
            if ((round.metrics.refreshed_opponent_slot ?? null) !== expected)
                throw Error('Native Bees completed self-play promotion differs from the frozen job parameters.');
        }
        return round;
    }

    return {
        ...evaluator,
        executable,
        cwd: path.dirname(runner),
        stop_policy: 'terminate-group',
        config_sha256: configSha256,
        gpu_execution: 'cuda',
        // The coordinator's native dual-GPU capability tag covers the
        // existing rollout-worker placement and the same MA-POCA learner.
        // Bundle-only jobs need no physical simulation here: both leased
        // devices perform synchronized native policy updates instead.
        gpu_dual_execution: 'native-rollouts',
        rocr_gpu_uuids,
        // Collection and optimization both run the same native model
        // inference backend. A collector may require GPU inference even
        // though it does not own an optimizer or the learner checkpoint.
        // Verify the exact PCI/HIP ordinal before either workload starts.
        gpu_verify_args(_parameters, context) {
            const ids=context?.gpu_pci_addresses ?? [context?.gpu_pci_address];
            if(!Array.isArray(ids)||![1,2].includes(ids.length)||
                ids.some(pci=>!PCI.test(String(pci)))||
                new Set(ids).size!==ids.length)
                throw Error('Native Bees ROCm verification requires one or two unique leased PCI GPUs.');
            return ['-B', path.join(__dirname, 'experiment_hub_tinybrains_rocm_verify.py'),
                ...ids];
        },
        args(parameters, context) {
            const output = jobRoot(context);
            const {config_sha256, seed_offset, rollout_bundles, optimizer_threads, rollout_workers,
                physics_workers, collection_id, rollout_world_start, rollout_worlds,
                bundle_directory, refresh_opponent_slot,
                source_run_contract_sha256} = parameters;
            const allowed = inboxLearner
                ? ['config_sha256', 'seed_offset', 'rollout_bundles', 'optimizer_threads',
                    'refresh_opponent_slot']
                : bundleRound
                    ? ['config_sha256', 'seed_offset', 'optimizer_threads',
                        'refresh_opponent_slot']
                    : collectOnly
                        ? ['config_sha256', 'seed_offset', 'rollout_workers', 'physics_workers',
                            'collection_id', 'rollout_world_start', 'rollout_worlds',
                            'bundle_directory', 'source_run_contract_sha256']
                        : ['config_sha256', 'seed_offset', 'optimizer_threads', 'rollout_workers',
                            'physics_workers', 'rollout_worlds', 'refresh_opponent_slot'];
            if (Object.keys(parameters).some(key => !allowed.includes(key)) ||
                config_sha256 !== configSha256 || !Number.isSafeInteger(seed_offset) || seed_offset < 0 ||
                (bundleRound && seed_offset !== 0))
                throw Error('Native Bees requires the approved configuration digest and a seed offset.');
            if (optimizer_threads !== undefined &&
                (collectOnly || !Number.isSafeInteger(optimizer_threads) ||
                    optimizer_threads < 1 || optimizer_threads > 256 ||
                    !Number.isSafeInteger(context.cpu_quota) ||
                    optimizer_threads > context.cpu_quota))
                throw Error('Optimizer threads must be positive and within the fixed Hub CPU quota.');
            // The coordinator owns machine/CPU/RAM leases. A native
            // collector job may additionally receive an immutable World
            // window and SHA-256 work-order identity; these select
            // simulation work *within* its existing Hub resource lease.
            if (collectOnly && (
                (collection_id !== undefined &&
                    (!/^[0-9a-f]{64}$/.test(String(collection_id)) ||
                        !Number.isSafeInteger(rollout_world_start) ||
                        rollout_world_start < 0 ||
                        !Number.isSafeInteger(rollout_worlds) ||
                        rollout_worlds < 1 || rollout_worlds > 1024)) ||
                (collection_id === undefined &&
                    (rollout_world_start !== undefined || rollout_worlds !== undefined ||
                        bundle_directory !== undefined)) ||
                (bundle_directory !== undefined &&
                    (typeof bundle_directory !== 'string' ||
                        !/^[A-Za-z0-9_-]{1,96}$/.test(bundle_directory) || !inbox)) ||
                (source_run_contract_sha256 !== undefined &&
                    (collection_id === undefined ||
                        !/^[a-f0-9]{64}$/.test(String(source_run_contract_sha256)) ||
                        source_run_contract_sha256 !== sourceContract))
            ))
                throw Error('Native distributed work order needs an exact ID, bounded World window and safe inbox name.');
            const dualGpu=Array.isArray(context.gpu_pci_addresses);
            // Each exclusive physical GPU requires its own persistent
            // model worker; expand the default native World pool rather
            // than forcing users to tune counts for every project.
            const activeWorlds = rollout_worlds ??
                Math.max(config.rollout_worlds ?? config.levels?.length ?? 1,
                    dualGpu ? 2 : 1);
            const activeWorkers = rollout_workers ??
                Math.max(config.rollout_workers ?? 1, dualGpu ? 2 : 1);
            // Rollout processes each retain a model worker and run their
            // own shared physical CPU pool. Both dimensions consume the
            // same fixed machine lease; never advertise more runnable
            // physics threads than the coordinator allocated.
            // An opt-in allocation mode chooses the largest useful physics
            // pool that fits both the current CPU lease and the independent
            // Worlds assigned to each rollout process. It makes no claim of
            // measured optimality; an explicit count remains available.
            // Existing jobs without this option retain their old default.
            const activePhysicsWorkers = physics_workers === 'allocation'
                ? Math.min(128, Math.floor(context.cpu_quota / activeWorkers),
                    Math.ceil(activeWorlds / activeWorkers))
                : physics_workers ?? config.physics_workers ?? 1;
            if (!bundleRound && !inboxLearner &&
                (!Number.isSafeInteger(activeWorkers) ||
                    activeWorkers < 1 || activeWorkers > 256 ||
                    !Number.isSafeInteger(context.cpu_quota) ||
                    activeWorkers > context.cpu_quota ||
                    !Number.isSafeInteger(activePhysicsWorkers) ||
                    activePhysicsWorkers < 1 || activePhysicsWorkers > 128 ||
                    activeWorkers * activePhysicsWorkers > context.cpu_quota ||
                    !Number.isSafeInteger(activeWorlds) ||
                    activeWorlds < 1 || activeWorlds > 1024 ||
                    activeWorkers > activeWorlds))
                throw Error('Effective rollout processes and per-process physics threads must fit the fixed Hub CPU quota and allocated World count.');
            for (const [file, digest] of pins) {
                if (sha256(fs.readFileSync(file)) !== digest)
                    throw Error('Native Bees pinned input changed after adapter installation.');
            }
            verifyModelDependencies(manifest, new Map([[input, pins.get(input)]]), input, learnerSlot);
            const pci = context.gpu_pci_address;
            const cudaUuid = context.gpu_cuda_uuid;
            const gpu = pci !== undefined || dualGpu;
            if (dualGpu && (
                !['bees_native','bees_native_collect'].includes(context.project_id)||
                context.gpu_mode !== 'exclusive' ||
                context.gpu_pci_addresses.length !== 2 ||
                new Set(context.gpu_pci_addresses).size !== 2 ||
                context.gpu_pci_addresses.some(pci=>!PCI.test(String(pci))) ||
                cudaUuid !== undefined ||
                context.cpu_quota < 2
            ))
                throw Error('Two native GPU leases require exclusive GPUs and at least two CPUs.');
            if ((pci !== undefined && (
                    dualGpu || !PCI.test(String(pci)) ||
                    context.gpu_mode !== 'exclusive' ||
                    (cudaUuid !== undefined && !CUDA_UUID.test(String(cudaUuid))))) ||
                (!gpu && cudaUuid !== undefined))
                throw Error('Native Bees execution requires an exclusive one- or two-GPU lease or CPU-only placement.');
            const inputs = roundInputs(context);
            if (refresh_opponent_slot !== undefined) {
                if (learnerSlot === undefined || !Number.isSafeInteger(refresh_opponent_slot) ||
                    refresh_opponent_slot < 0 || refresh_opponent_slot > 0xffffffff ||
                    refresh_opponent_slot === learnerSlot)
                    throw Error('Native Bees self-play requires a distinct existing uint32 opponent and explicit learner slot.');
                const current = JSON.parse(fs.readFileSync(inputs.manifest, 'utf8'));
                const opponents = current.models.filter(model => model.slot === refresh_opponent_slot);
                if (opponents.length !== 1 || opponents[0].backend !== 'mlagents')
                    throw Error('Native Bees self-play promotion requires an existing MA-POCA opponent slot.');
                if (!config.levels?.some(level =>
                    [learnerSlot, refresh_opponent_slot].every(slot =>
                        Object.values(level.model_slots || {}).includes(slot))) && !inboxLearner)
                    throw Error('Native Bees training Levels must route the learner and selected opponent.');
            }
            if (!gpu && inputs.requiresGpuInference)
                throw Error('Native Bees GPU inference requires an exclusive GPU lease.');
            // Collect-only jobs run no optimizer. A GPU lease cannot help a
            // manifest whose every inference model is CPU-bound, so refuse
            // that placement rather than silently wasting the assigned GPU.
            if (collectOnly && gpu && !inputs.supportsGpuInference)
                throw Error('Native Bees GPU collection needs an accelerator-capable model backend.');
            if (context.continuation_of_job_id &&
                context.expected_parent_model_sha256 !== inputs.checkpointDigest)
                throw Error('Native Bees predecessor differs from the verified Hub checkpoint.');
            return ['-B', runner, run, inputs.checkpoint, trainer, output,
                '--model-manifest', inputs.manifest, ...(binary ? ['--rollout-binary', binary] : []),
                '--stage-diagnostics',
                // Supervisor-owned exclusive GPU selection is already verified.
                // The round supplies cuda:0 to every resident PyTorch/MA-POCA
                // worker, without changing the immutable checkpoint manifest.
                ...(gpu && !inboxLearner && !bundleRound
                    ? ['--leased-gpu-inference',
                        ...(dualGpu?['--leased-gpu-count','2']:[])] : []),
                ...(collectOnly ? ['--collect-only'] : []),
                ...(collectOnly && inbox ? ['--bundle-inbox', inbox] : []),
                ...(collection_id === undefined ? [] : [
                    '--collection-id', collection_id,
                    '--rollout-world-start', String(rollout_world_start),
                    '--rollout-worlds', String(rollout_worlds),
                ]),
                ...(bundle_directory === undefined ? [] : [
                    '--bundle-delivery-name', bundle_directory,
                ]),
                ...(inboxLearner ? bundleArguments(rollout_bundles) : []),
                '--device', gpu ? 'cuda' : 'cpu',
                ...(!collectOnly && dualGpu ? ['--learner-gpus', '2'] : []),
                // Two optimizer ranks are OS processes. Bound the sum
                // of their intra-op threads by the reserved Hub CPUs,
                // including when no manual thread selection was made.
                ...(optimizer_threads === undefined && !dualGpu ? [] :
                    ['--torch-threads', String(optimizer_threads ??
                        Math.min(256, Math.floor(context.cpu_quota)))]),
                ...((rollout_workers === undefined && !dualGpu) ||
                    inboxLearner || bundleRound ? [] :
                    ['--rollout-workers', String(activeWorkers)]),
                ...((!inboxLearner && !bundleRound) &&
                    (rollout_worlds !== undefined || dualGpu) ?
                    ['--rollout-worlds', String(activeWorlds)] : []),
                ...(physics_workers === undefined ? [] :
                    ['--physics-workers', String(activePhysicsWorkers)]),
                '--seed-offset', String(seed_offset),
                ...(learnerSlot === undefined ? [] : ['--learner-slot', String(learnerSlot)]),
                ...(refresh_opponent_slot === undefined ? [] :
                    ['--refresh-opponent-slot', String(refresh_opponent_slot)])];
        },
        ...(collectOnly ? {
            // Transport is machine-owned. Neither the Hub nor a frozen job
            // supplies filesystem roots or executable paths.
            artifact_export_dir(jobId) {
                if (!UUID.test(String(jobId))) throw Error('Invalid collector job identity.');
                return path.join(artifacts, jobId);
            },
        } : {
            ...(inbox ? {bundle_inbox_dir() { return inbox; }} : {}),
            async checkpoint_ready(context) {
                const output = jobRoot(context);
                const verified = verifiedOutput(jobRound(context), output);
                if (gallery) {
                    // The verified learner checkpoint is already resumable.
                    // A player-save permission error must never make the
                    // coordinator misreport a valid learner as lacking its
                    // checkpoint. Publication is an independent opt-in game
                    // handoff; repeated Inspect requests retry it safely.
                    // The async child leaves the agent heartbeat responsive.
                    try {
                        for (const file of gallerySources) {
                            if (sha256(fs.readFileSync(file)) !== pins.get(file))
                                throw Error('Native Bees gallery publisher changed after adapter installation.');
                        }
                        const gameId = 'hub-' + context.job_id;
                        const {stdout} = await execFileAsync(executable, [
                            '-B', publisher, run, output, gallery, gameId,
                            String(learnerSlot), pins.get(run),
                        ], {
                            cwd: path.dirname(publisher),
                            timeout: 120000,
                            maxBuffer: 8192,
                        });
                        const published = JSON.parse(stdout);
                        if (published.kind !== 'native_hub_game_registered' ||
                            published.game_id !== gameId ||
                            published.checkpoint_sha256 !== verified.checkpointDigest)
                            throw Error('Native Bees gallery publication lacks verified Hub model identity.');
                    } catch (error) {
                        console.error('[experiment-hub-bees] Optional game-gallery publication failed for ' +
                            context.job_id + ': ' + String(error.message || error));
                    }
                }
                return true;
            },
            model_checkpoint_sha256(context) {
                return verifiedOutput(jobRound(context), jobRoot(context)).checkpointDigest;
            },
        }),
        readProgress(context) {
            if (collectOnly) {
                const bundle = completionReceipt(jobRoot(context), 'rollout-bundle.json');
                if (!bundle) return null;
                const {receipt, stat} = bundle;
                const rollout = receipt.rollout_metrics;
                const inputs = roundInputs(context);
                if (receipt.schema_version !== 1 || receipt.kind !== 'native_rollout_bundle' ||
                    receipt.input_checkpoint_sha256 !== inputs.checkpointDigest ||
                    receipt.input_manifest_sha256 !== inputs.manifestDigest ||
                    receipt.learner_slot !== (learnerSlot ?? null) ||
                    !Array.isArray(receipt.captures) || !receipt.captures.length ||
                    !Number.isSafeInteger(rollout?.successful_policy_rows) || rollout.successful_policy_rows < 1 ||
                    !Number.isSafeInteger(rollout.successful_world_steps) || rollout.successful_world_steps < 1 ||
                    !Number.isFinite(rollout.wall_seconds) || rollout.wall_seconds <= 0)
                    throw Error('Invalid native Bees rollout bundle receipt.');
                verifyRoundGpuLease(context, receipt);
                return validateProgress({
                    schema_version: 1, source: 'hub-metrics-v1',
                    experiment_id: context.job_id, segment_id: 'collection',
                    source_modified_at: stat.mtime.toISOString(),
                    steps_completed: rollout.successful_policy_rows,
                    metrics: {
                        rollout_world_steps: {value: rollout.successful_world_steps, unit: 'count'},
                        rollout_policy_rows: {value: rollout.successful_policy_rows, unit: 'count'},
                        rollout_wall_seconds: {value: rollout.wall_seconds, unit: 'seconds'},
                        rollout_world_steps_per_second: {
                            value: rollout.successful_world_steps / rollout.wall_seconds, unit: 'rate',
                        },
                        rollout_policy_rows_per_second: {
                            value: rollout.successful_policy_rows / rollout.wall_seconds, unit: 'rate',
                        },
                        rollout_workers: {value: receipt.captures.length, unit: 'count'},
                        ...rolloutPhysicsMeasurements(rollout),
                        ...rolloutGpuMeasurements(rollout),
                        ...rolloutReinforcementMeasurements(rollout),
                        ...rolloutReinforcementWakeupMeasurements(rollout),
                        ...rolloutQueenControllerMeasurements(rollout),
                        ...rolloutBorderMeasurements(rollout),
                        ...rolloutLiveShipMeasurements(rollout),
                        ...rolloutSceneCacheMeasurements(rollout),
                        ...rolloutPolicyBoundaryMeasurements(rollout),
                        ...rolloutPolicyObservationMeasurements(rollout),
                        ...(rollout.sampled_model_timing ? {
                            rollout_sampled_model_calls: {
                                value: rollout.sampled_model_timing.calls, unit: 'count',
                            },
                            rollout_sampled_model_rows: {
                                value: rollout.sampled_model_timing.rows, unit: 'count',
                            },
                            rollout_sampled_model_wall_seconds: {
                                value: rollout.sampled_model_timing.wall_seconds, unit: 'seconds',
                            },
                        } : {}),
                        ...(rollout.worker_execution_min_seconds !== undefined ? {
                            rollout_worker_execution_min_seconds: {
                                value: rollout.worker_execution_min_seconds, unit: 'seconds',
                            },
                            rollout_worker_execution_max_seconds: {
                                value: rollout.worker_execution_max_seconds, unit: 'seconds',
                            },
                            rollout_worker_world_steps_min: {
                                value: rollout.worker_world_steps_min, unit: 'count',
                            },
                            rollout_worker_world_steps_max: {
                                value: rollout.worker_world_steps_max, unit: 'count',
                            },
                        } : {}),
                        ...(receipt.delivery ? {
                            rollout_delivery_bytes: {value: receipt.delivery.bytes, unit: 'bytes'},
                            rollout_delivery_seconds: {value: receipt.delivery.copy_wall_seconds, unit: 'seconds'},
                        } : {}),
                    },
                }, project);
            }
            const round = jobRound(context);
            if (!round) return null;
            const {receipt, metrics, stat} = round;
            const rollout = receipt.rollout_metrics;
            const modelTiming = rollout?.sampled_model_timing;
            return validateProgress({
                schema_version: 1, source: 'hub-metrics-v1',
                experiment_id: context.job_id, segment_id: 'training',
                source_modified_at: stat.mtime.toISOString(),
                steps_completed: metrics.optimizer_input_steps,
                metrics: {
                    ...(rollout ? {
                        ...rolloutPhysicsMeasurements(rollout),
                        ...rolloutGpuMeasurements(rollout),
                        ...rolloutReinforcementMeasurements(rollout),
                        ...rolloutReinforcementWakeupMeasurements(rollout),
                        ...rolloutQueenControllerMeasurements(rollout),
                        ...rolloutBorderMeasurements(rollout),
                        ...rolloutLiveShipMeasurements(rollout),
                        ...rolloutSceneCacheMeasurements(rollout),
                        ...rolloutPolicyBoundaryMeasurements(rollout),
                        ...rolloutPolicyObservationMeasurements(rollout),
                        rollout_world_steps: {value: rollout.successful_world_steps, unit: 'count'},
                        rollout_policy_rows: {value: rollout.successful_policy_rows, unit: 'count'},
                        rollout_wall_seconds: {value: rollout.wall_seconds, unit: 'seconds'},
                        ...(rollout.workers ? {
                            rollout_workers: {value: rollout.workers, unit: 'count'},
                            ...(rollout.worker_execution_min_seconds !== undefined ? {
                                rollout_worker_execution_min_seconds: {
                                    value: rollout.worker_execution_min_seconds, unit: 'seconds',
                                },
                                rollout_worker_execution_max_seconds: {
                                    value: rollout.worker_execution_max_seconds, unit: 'seconds',
                                },
                                rollout_worker_world_steps_min: {
                                    value: rollout.worker_world_steps_min, unit: 'count',
                                },
                                rollout_worker_world_steps_max: {
                                    value: rollout.worker_world_steps_max, unit: 'count',
                                },
                            } : {}),
                        } : {
                            rollout_startup_seconds: {value: rollout.startup_wall_seconds, unit: 'seconds'},
                            rollout_execution_seconds: {value: rollout.execution_wall_seconds, unit: 'seconds'},
                            rollout_finalization_seconds: {value: rollout.finalization_wall_seconds, unit: 'seconds'},
                        }),
                        rollout_policy_rows_per_second: {
                            value: rollout.successful_policy_rows / rollout.wall_seconds, unit: 'rate',
                        },
                        rollout_world_steps_per_second: {
                            value: rollout.successful_world_steps / rollout.wall_seconds, unit: 'rate',
                        },
                    } : {}),
                    ...(receipt.rollout_bundles ? {
                        imported_rollout_bundles: {value: receipt.rollout_bundles.length, unit: 'count'},
                    } : {}),
                    ...(modelTiming ? {
                        rollout_sampled_model_calls: {value: modelTiming.calls, unit: 'count'},
                        rollout_sampled_model_rows: {value: modelTiming.rows, unit: 'count'},
                        rollout_sampled_model_wall_seconds: {value: modelTiming.wall_seconds, unit: 'seconds'},
                    } : {}),
                    episodes: {value: metrics.episodes, unit: 'count'},
                    opponent_steps: {value: metrics.opponent_steps, unit: 'count'},
                    terminal_steps: {value: metrics.terminal_steps, unit: 'count'},
                    optimizer_update_wall_seconds: {value: metrics.optimizer_update_wall_seconds, unit: 'seconds'},
                    trajectory_processing_wall_seconds: {value: metrics.trajectory_processing_wall_seconds, unit: 'seconds'},
                    ...(rollout?.wall_seconds > 0 && metrics.total_wall_seconds > 0 ? {
                        training_round_input_steps_per_second: {
                            value: metrics.optimizer_input_steps /
                                (rollout.wall_seconds + metrics.total_wall_seconds),
                            unit: 'rate',
                        },
                        training_round_world_steps_per_second: {
                            value: rollout.successful_world_steps /
                                (rollout.wall_seconds + metrics.total_wall_seconds),
                            unit: 'rate',
                        },
                    } : {}),
                    ...learnerMeasurements(metrics),
                },
            }, PROJECT);
        },
    };
}

module.exports = {createNativeBeesAdapter};
