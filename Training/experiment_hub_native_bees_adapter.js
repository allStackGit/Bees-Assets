'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {spawnSync} = require('node:child_process');
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
            requiresGpuInference ||= providers.some(provider => provider !== 'CPUExecutionProvider');
        } else {
            // The Hub leases at most one GPU to the Bees optimizer. A
            // second CUDA ordinal cannot be reached through that lease.
            const device = model.device ?? 'cpu';
            if (!['cpu', 'cuda', 'cuda:0'].includes(device))
                throw Error('Native Bees inference device exceeds the supported single-GPU lease.');
            requiresGpuInference ||= device !== 'cpu';
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
    return requiresGpuInference;
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
        'capture_compact_opponent_decisions', 'capture_compact_opponent_boundaries',
        'reward_journal_events',
        'optimizer_torch_intraop_threads', 'optimizer_torch_interop_threads'])
        add(name, 'count');
    add('reward_journal_payload_bytes', 'bytes');
    add('optimizer_steps_per_update_second', 'rate');
    add('torch_cuda_process_peak_allocated_bytes', 'bytes');
    add('torch_cuda_process_peak_reserved_bytes', 'bytes');
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
function rolloutPhysicsMeasurements(rollout) {
    const result = {};
    for (const field of [
        'requested_physics_worker_slots',
        'parallel_physics_rounds',
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
    return result;
}

function createNativeBeesAdapter({
    pythonPath, rolloutBinary, runConfig, trainerConfig, checkpoint, artifactRoot,
    learnerSlot, collectOnly = false, bundleInbox, rocrGpuUuids = {},
    evaluationRoot, evaluationSuites,
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
    const contractScript = trustedFile(path.resolve(__dirname,
        '../native/tools/native_training_artifacts.py'));
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
        ...[runner, contractScript, ...(binary ? [binary] : [])]
            .map(file => [file, sha256(fs.readFileSync(file))]),
    ]);
    const initialGpuInference = verifyModelDependencies(
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
        const requiresGpuInference = verifyModelDependencies(
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
            manifestDigest: receipt.next_manifest_sha256, requiresGpuInference};
    }

    function roundInputs(context) {
        if (!context.continuation_of_job_id)
            return {checkpoint: input, manifest, checkpointDigest: pins.get(input),
                manifestDigest: pins.get(manifest), requiresGpuInference: initialGpuInference};
        if (collectOnly) throw Error('Native Bees collection requires an independent job.');
        if (!UUID.test(context.continuation_of_job_id) || context.continuation_of_job_id === context.job_id)
            throw Error('Invalid native Bees predecessor.');
        const parent = path.join(artifacts, context.continuation_of_job_id);
        return verifiedOutput(completedRound(parent), parent);
    }

    function jobRound(context) {
        const output = jobRoot(context);
        const round = completedRound(output);
        if (!round) return null;
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
        rocr_gpu_uuids,
        // Collection and optimization both run the same native model
        // inference backend. A collector may require GPU inference even
        // though it does not own an optimizer or the learner checkpoint.
        // Verify the exact PCI/HIP ordinal before either workload starts.
        gpu_verify_args(_parameters, context) {
            if (!PCI.test(String(context?.gpu_pci_address || '')) ||
                context?.gpu_pci_addresses !== undefined)
                throw Error('Native Bees ROCm verification requires one leased physical GPU.');
            return ['-B', path.join(__dirname, 'experiment_hub_tinybrains_rocm_verify.py'),
                context.gpu_pci_address];
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
            const activeWorlds = rollout_worlds ?? config.rollout_worlds ?? config.levels?.length;
            const activeWorkers = rollout_workers ?? config.rollout_workers ?? 1;
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
            const gpu = pci !== undefined;
            if (context.gpu_pci_addresses !== undefined ||
                (gpu && (!PCI.test(String(pci)) || context.gpu_mode !== 'exclusive' ||
                    (cudaUuid !== undefined && !CUDA_UUID.test(String(cudaUuid))))) ||
                (!gpu && cudaUuid !== undefined))
                throw Error('Native Bees execution requires an exclusive single-GPU lease or CPU-only placement.');
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
            if (collectOnly && gpu && !inputs.requiresGpuInference)
                throw Error('Native Bees GPU collection requires a GPU-pinned inference model.');
            if (context.continuation_of_job_id &&
                context.expected_parent_model_sha256 !== inputs.checkpointDigest)
                throw Error('Native Bees predecessor differs from the verified Hub checkpoint.');
            return ['-B', runner, run, inputs.checkpoint, trainer, output,
                '--model-manifest', inputs.manifest, ...(binary ? ['--rollout-binary', binary] : []),
                '--stage-diagnostics',
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
                ...(optimizer_threads === undefined ? [] :
                    ['--torch-threads', String(optimizer_threads)]),
                ...(rollout_workers === undefined ? [] :
                    ['--rollout-workers', String(rollout_workers)]),
                ...(!collectOnly && rollout_worlds !== undefined ?
                    ['--rollout-worlds', String(rollout_worlds)] : []),
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
            checkpoint_ready(context) {
                return Boolean(verifiedOutput(jobRound(context), jobRoot(context)));
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
