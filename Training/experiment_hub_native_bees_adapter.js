'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {validateProgress} = require('./experiment_hub_progress');

const PROJECT = 'bees_native';
const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const sha256 = data => crypto.createHash('sha256').update(data).digest('hex');

function trustedFile(file) {
    if (typeof file !== 'string' || !path.isAbsolute(file))
        throw Error('Native Bees requires absolute machine-local file paths.');
    const resolved = fs.realpathSync(file);
    if (!fs.statSync(resolved).isFile()) throw Error('Native Bees input is not a file.');
    return resolved;
}

function createNativeBeesAdapter({
    pythonPath, rolloutBinary, runConfig, trainerConfig, checkpoint, artifactRoot,
    learnerSlot,
}) {
    const executable = trustedFile(pythonPath);
    const binary = trustedFile(rolloutBinary);
    const run = trustedFile(runConfig);
    const trainer = trustedFile(trainerConfig);
    const input = trustedFile(checkpoint);
    const runner = trustedFile(path.resolve(__dirname, '../native/tools/train_policy_round.py'));
    if (typeof artifactRoot !== 'string' || !path.isAbsolute(artifactRoot))
        throw Error('Native Bees requires an absolute artifact root.');
    const artifacts = fs.realpathSync(artifactRoot);
    if (!fs.statSync(artifacts).isDirectory()) throw Error('Native Bees artifact root is not a directory.');
    if (learnerSlot !== undefined &&
        (!Number.isSafeInteger(learnerSlot) || learnerSlot < 0 || learnerSlot > 0xffffffff))
        throw Error('Invalid native Bees learner slot.');

    const runBytes = fs.readFileSync(run);
    const config = JSON.parse(runBytes);
    if (config.hivemind != null || config.recover_notifications?.length ||
        config.recover_outcome_journals?.length || config.model_updates?.length ||
        !path.isAbsolute(config.python || '') || trustedFile(config.python) !== executable ||
        typeof config.model_manifest !== 'string' || !config.model_manifest)
        throw Error('Native Bees Hub runs require a model-only template and the installed interpreter.');
    const manifest = trustedFile(path.resolve(path.dirname(run), config.model_manifest));
    const pins = new Map([
        [run, sha256(runBytes)],
        ...[trainer, input, manifest].map(file => [file, sha256(fs.readFileSync(file))]),
    ]);
    const configSha256 = sha256(JSON.stringify([...pins.values(), learnerSlot ?? null]));

    function jobRoot(context) {
        if (context.project_id !== PROJECT || !UUID.test(context.job_id))
            throw Error('Invalid native Bees job identity.');
        return path.join(artifacts, context.job_id);
    }

    function completedRound(output) {
        const file = path.join(output, 'round-result.json');
        let stat;
        try {
            stat = fs.lstatSync(file);
        } catch (error) {
            if (error.code === 'ENOENT') return null;
            throw error;
        }
        if (!stat.isFile() || stat.size < 1 || stat.size > 64 * 1024 || fs.realpathSync(file) !== file)
            throw Error('Invalid native Bees completion receipt file.');
        const receipt = JSON.parse(fs.readFileSync(file, 'utf8'));
        const metrics = receipt.learner_metrics;
        if (receipt.schema_version !== 1 || receipt.kind !== 'native_training_round_complete' ||
            metrics?.schema_version !== 1 || metrics.source !== 'bees-native-ma-poca-v1' ||
            metrics.learner_slot !== (learnerSlot ?? null) ||
            metrics.input_checkpoint_sha256 !== receipt.input_checkpoint_sha256 ||
            metrics.input_manifest_sha256 !== receipt.input_manifest_sha256 ||
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
        return {checkpoint, manifest, checkpointDigest: receipt.next_checkpoint_sha256,
            manifestDigest: receipt.next_manifest_sha256};
    }

    function roundInputs(context) {
        if (!context.continuation_of_job_id)
            return {checkpoint: input, manifest, checkpointDigest: pins.get(input),
                manifestDigest: pins.get(manifest)};
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
        return round;
    }

    return {
        executable,
        cwd: path.dirname(runner),
        stop_policy: 'terminate-group',
        config_sha256: configSha256,
        args(parameters, context) {
            const output = jobRoot(context);
            const {config_sha256, seed_offset} = parameters;
            if (Object.keys(parameters).some(key => !['config_sha256', 'seed_offset'].includes(key)) ||
                config_sha256 !== configSha256 || !Number.isSafeInteger(seed_offset) || seed_offset < 0)
                throw Error('Native Bees requires the approved configuration digest and a seed offset.');
            for (const [file, digest] of pins) {
                if (sha256(fs.readFileSync(file)) !== digest)
                    throw Error('Native Bees pinned input changed after adapter installation.');
            }
            const inputs = roundInputs(context);
            if (context.continuation_of_job_id &&
                context.expected_parent_model_sha256 !== inputs.checkpointDigest)
                throw Error('Native Bees predecessor differs from the verified Hub checkpoint.');
            return ['-B', runner, run, inputs.checkpoint, trainer, output,
                '--model-manifest', inputs.manifest, '--rollout-binary', binary,
                '--stage-diagnostics',
                '--seed-offset', String(seed_offset),
                ...(learnerSlot === undefined ? [] : ['--learner-slot', String(learnerSlot)])];
        },
        checkpoint_ready(context) {
            return Boolean(verifiedOutput(jobRound(context), jobRoot(context)));
        },
        model_checkpoint_sha256(context) {
            return verifiedOutput(jobRound(context), jobRoot(context)).checkpointDigest;
        },
        readProgress(context) {
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
                        rollout_world_steps: {value: rollout.successful_world_steps, unit: 'count'},
                        rollout_policy_rows: {value: rollout.successful_policy_rows, unit: 'count'},
                        rollout_wall_seconds: {value: rollout.wall_seconds, unit: 'seconds'},
                        rollout_startup_seconds: {value: rollout.startup_wall_seconds, unit: 'seconds'},
                        rollout_execution_seconds: {value: rollout.execution_wall_seconds, unit: 'seconds'},
                        rollout_finalization_seconds: {value: rollout.finalization_wall_seconds, unit: 'seconds'},
                        rollout_policy_rows_per_second: {
                            value: rollout.successful_policy_rows / rollout.wall_seconds, unit: 'rate',
                        },
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
                },
            }, PROJECT);
        },
    };
}

module.exports = {createNativeBeesAdapter};
