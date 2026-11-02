'use strict';
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { invokeStart, invokeStop } = require('./operator/commands');
const { getStatus } = require('./operator/control');
const { getPendingForcedNewRunPlan } = require('./operator/runtime');
const { assertRlEnvironmentArgsValid } = require('./operator/validation');
const { getAuthoritativeLearnerStats, getLatestPpoUpdate, statusError } = require('./operator/status');
const { paths, readJson, writeJsonAtomic, ensureDir, loadConfig, resolvePython, requestJson } = require('./operator/common');

// The existing Bees operator owns safe run transitions. This module owns only
// an unattended queue, bounded metric summaries, and a loopback-only UI.
const HOST = '127.0.0.1';
// A fixed port also prevents two coordinators from writing the same state file.
const PORT = 7148;
const DB = path.join(paths.trainingRoot, 'Experiments', 'experiments-v1.json');
const HTML = path.join(__dirname, 'experiments.html');
const BENCHMARK_REPORTS = path.join(paths.trainingRoot, 'Experiments', 'Benchmarks');
const empty = () => ({ version: 1, armed: false, stop_requested: false, active_id: null, message: '', items: [] });
const state = fs.existsSync(DB) ? readJson(DB) : empty();
if (!state || state.version !== 1 || !Array.isArray(state.items) || typeof state.armed !== 'boolean') {
    throw Error('Invalid experiment data; no training started: ' + DB);
}
if (typeof state.stop_requested !== 'boolean') state.stop_requested = false;
// Poll-to-poll training time is observable only while this coordinator runs.
// After process restart, discard persisted interval baselines so downtime is
// never incorrectly charged as training or PPO idle time.
for (const item of state.items) delete item.performance_clock;
let busy = false;
const save = () => { ensureDir(path.dirname(DB)); writeJsonAtomic(DB, state); };
const active = () => state.items.find(x => x.id === state.active_id);
const matches = (a, b) => JSON.stringify(a || []) === JSON.stringify(b || []);
const now = () => new Date().toISOString();
const MAX_STEPS = 5000000000000;

function overridesForStage(raw, label) {
    if (!Array.isArray(raw) || raw.length > 64) throw Error(label + ': invalid environment arguments.');
    const overrides = raw.map(x => String(x).trim()).filter(Boolean);
    if (overrides.some(x => x.length > 512 || !/^--rl-[a-z0-9-]+(?:=[^\r\n]*)?$/.test(x))) {
        throw Error(label + ': only individual --rl-* arguments are accepted.');
    }
    if (new Set(overrides.map(x => x.split('=')[0])).size !== overrides.length) {
        throw Error(label + ': duplicate environment argument.');
    }
    return overrides;
}

function mergeEnvironmentArgs(previous, overrides) {
    const keys = new Set(overrides.map(x => x.split('=')[0]));
    const exactMap = '--rl-map-size';
    const rangedMap = ['--rl-map-size-min', '--rl-map-size-max'];
    if (keys.has(exactMap) && rangedMap.some(key => keys.has(key))) {
        throw Error('Choose either --rl-map-size or --rl-map-size-min/max, not both.');
    }
    const excluded = new Set(keys);
    if (keys.has(exactMap)) rangedMap.forEach(key => excluded.add(key));
    if (rangedMap.some(key => keys.has(key))) excluded.add(exactMap);
    return previous.filter(x => !excluded.has(x.split('=')[0])).concat(overrides);
}

function timeoutTarget(raw, label) {
    if (raw == null || raw === '') return null;
    const number = Number(raw);
    if (!Number.isFinite(number) || number < 0 || number > 100) {
        throw Error(label + ': timeout target must be 0–100% or blank.');
    }
    return number;
}

function stagesForDefinition(input, defaults, firstSteps) {
    const firstName = String(input.stage_name || 'Initial stage').trim();
    if (!firstName || firstName.length > 100) throw Error('Invalid initial stage name.');
    let args = mergeEnvironmentArgs(defaults, overridesForStage(input.env_args, firstName));
    const stages = [{
        name: firstName, end_step: firstSteps, env_args: args,
        timeout_target_pct: timeoutTarget(input.target_timeout_pct, firstName),
    }];
    const additions = input.stages == null ? [] : input.stages;
    if (!Array.isArray(additions) || additions.length > 11) {
        throw Error('At most 11 additional curriculum stages are supported.');
    }
    let end = firstSteps;
    for (const [index, stage] of additions.entries()) {
        if (!stage || typeof stage !== 'object' || Array.isArray(stage)) {
            throw Error('Invalid stage ' + (index + 2) + '.');
        }
        const name = String(stage.name || '').trim();
        const steps = Number(stage.steps);
        if (!name || name.length > 100 || !Number.isSafeInteger(steps) || steps < 1000 ||
            end + steps > MAX_STEPS) {
            throw Error('Each stage requires a name and a positive step budget within the run limit.');
        }
        args = mergeEnvironmentArgs(args, overridesForStage(stage.env_args || [], name));
        end += steps;
        stages.push({
            name, end_step: end, env_args: args,
            timeout_target_pct: timeoutTarget(stage.target_timeout_pct, name),
        });
    }
    return stages;
}

function stageFor(item) {
    return item.stages ? item.stages[item.stage_index || 0] : null;
}

function stageRecord(item, index = item.stage_index || 0) {
    item.stage_history = item.stage_history || [];
    if (!item.stage_history[index]) {
        item.stage_history[index] = {
            entered_at: now(), entered_step: index ? item.step : 0,
            consecutive_qualifying_samples: 0,
        };
    }
    return item.stage_history[index];
}

async function control() {
    const token = fs.readFileSync(paths.adminTokenPath, 'utf8').trim();
    if (!token) throw Error('Missing training control token.');
    return getStatus(loadConfig(), token);
}

// Read-only projection of the existing control-plane status. Nothing here
// changes learner/worker ownership or persists another telemetry copy.
function workerSnapshot(status) {
    const desired = status.desired || {};
    return {
        training_enabled: Boolean(desired.training_enabled),
        run_id: String(desired.run_id || ''),
        revision: Number(desired.revision || 0),
        pending_release: desired.pending_release
            ? String(desired.pending_release.phase || 'pending') : null,
        workers: (status.trainers || []).map(record => {
            const metrics = record.metrics || {};
            const throughput = metrics.throughput || {};
            const host = metrics.host || {};
            const capacity = record.worker_capacity || {};
            const optimizer = record.env_optimizer || {};
            return {
                trainer_id: String(record.trainer_id || ''),
                role: String(record.role || ''),
                platform: String(record.platform || ''),
                state: record.stale ? 'STALE' : String(record.process_state || 'unknown'),
                envs: capacity.current_envs ?? null,
                desired_envs: record.worker_env_override ?? optimizer.desired_envs ?? null,
                worker_env_override: record.worker_env_override ?? null,
                min_envs: capacity.min_envs ?? null,
                max_envs: capacity.max_envs ?? null,
                auto_envs: capacity.auto === true,
                can_override_envs: metrics.worker_env_override_supported === true,
                cpu_pct: host.cpu_percent ?? null,
                ram_pct: host.ram_percent ?? null,
                live_exp_s: throughput.learner_consumed_steps_per_sec ?? null,
                staged_s: throughput.staged_steps_per_sec ?? null,
                accepted_s: throughput.accepted_steps_per_sec ?? null,
                accepted_total: throughput.accepted_steps_total ?? null,
                consumed_total: throughput.learner_consumed_steps_total ?? null,
                upload_queue: throughput.upload_queue_depth ?? null,
                broker_queue: throughput.broker_queue_depth ?? null,
                broker_capacity: throughput.broker_queue_capacity ?? null,
                age_seconds: record.age_seconds ?? null,
                error: statusError(record),
            };
        }),
    };
}

// Compact samples of existing learner, PPO and broker telemetry. No new
// profiling or raw log storage: the PPO parser is shared with "bees status".
const finite = value => value == null || !Number.isFinite(Number(value))
    ? null : Number(value);

function livePipeline(status) {
    const desired = status.desired || {};
    const current = (status.trainers || []).filter(record =>
        !record.stale && record.process_state === 'running' &&
        Number(record.applied_revision) >= Number(desired.revision) &&
        record.build_id === desired.canonical_build_id && record.metrics);
    const central = current.find(record => record.trainer_id === 'central-learner');
    const broker = central?.metrics?.throughput || {};
    const sum = field => {
        const values = current.map(record =>
            finite(record.metrics?.throughput?.[field])).filter(x => x != null);
        return values.length ? values.reduce((a, b) => a + b, 0) : null;
    };
    return {
        workers: current.filter(record => record.trainer_id !== 'central-learner').length,
        staged_s: sum('staged_steps_per_sec'),
        accepted_s: sum('accepted_steps_per_sec'),
        consumed_s: sum('learner_consumed_steps_per_sec'),
        upload_q: sum('upload_queue_depth'),
        broker_q: finite(broker.broker_queue_depth),
        broker_capacity: finite(broker.broker_queue_capacity),
        optimizer_busy_total: finite(broker.optimizer_busy_seconds_total),
    };
}

function latestPpoMetrics(runId) {
    const record = getLatestPpoUpdate(runId);
    if (!record) return null;
    const update = record.update || {}, hardware = record.hardware || {};
    const minibatches = Number(String(update.minibatches || '').split('/')[0]);
    const seconds = finite(update.seconds);
    if (seconds == null || seconds <= 0) return null;
    return {
        update_s: seconds,
        minibatches: Number.isFinite(minibatches) && minibatches > 0 ? minibatches : null,
        minibatches_s: Number.isFinite(minibatches) && minibatches > 0
            ? minibatches / seconds : null,
        // Breakdown exists only when the operator's PPO diagnostics are enabled.
        materialize_s: finite(update.materialize),
        backward_s: finite(update.backward),
        optimizer_step_s: finite(update.optimizer_step),
        cpu_prepare_wait_s: finite(update.cpu_prepare_wait),
        cpu_pct: finite(hardware.process_cpu_avg),
        gpu_pct: finite(hardware.gpu_util_avg),
    };
}

function performanceFor(item, index = item.stage_index || 0) {
    const stage = stageRecord(item, index);
    if (!stage.performance) {
        stage.performance = { training_s: 0, steps: 0, optimizer_busy_s: 0,
            busy_observed_s: 0, totals: {}, counts: {}, pressure: {} };
    }
    return stage.performance;
}

function observeTrainingTime(item, status, step, atMs, pipeline) {
    const desired = status.desired || {};
    if (item.status !== 'running' || !desired.training_enabled ||
        desired.run_id !== item.run_id) {
        delete item.performance_clock;
        return;
    }
    const index = item.stage_index || 0;
    const previous = item.performance_clock;
    const perf = performanceFor(item, index);
    if (previous && previous.stage_index === index &&
        Number.isFinite(previous.at_ms) && atMs > previous.at_ms) {
        const seconds = (atMs - previous.at_ms) / 1000;
        perf.training_s += seconds;
        if (step != null && Number.isSafeInteger(previous.step) &&
            step >= previous.step) perf.steps += step - previous.step;
        const total = pipeline.optimizer_busy_total;
        if (total != null && previous.optimizer_busy_total != null &&
            total >= previous.optimizer_busy_total) {
            const delta = total - previous.optimizer_busy_total;
            // An optimizer update can span several control polls. Its busy
            // counter increments on completion, not once per polling window;
            // compare against the whole observed training interval, not this
            // single poll. Reset/stale counters are rebased, never subtracted.
            if (delta <= perf.training_s + 1) {
                perf.optimizer_busy_s += Math.min(delta,
                    Math.max(0, perf.training_s - perf.optimizer_busy_s));
                perf.busy_observed_s += seconds;
            }
        }
    }
    item.performance_clock = {
        stage_index: index, at_ms: atMs, step,
        optimizer_busy_total: pipeline.optimizer_busy_total,
    };
}

function performanceReading(item, pipeline, ppo) {
    const perf = performanceFor(item);
    const push = (key, value) => {
        if (value == null || !Number.isFinite(value)) return;
        perf.totals[key] = (perf.totals[key] || 0) + value;
        perf.counts[key] = (perf.counts[key] || 0) + 1;
    };
    for (const key of ['workers', 'staged_s', 'accepted_s', 'consumed_s', 'upload_q', 'broker_q']) {
        push(key, pipeline[key]);
    }
    const ratio = pipeline.broker_capacity > 0 && pipeline.broker_q != null
        ? pipeline.broker_q / pipeline.broker_capacity : null;
    push('broker_ratio', ratio);
    if (ratio != null) {
        perf.pressure.broker_samples = (perf.pressure.broker_samples || 0) + 1;
        if (ratio >= 0.75) perf.pressure.broker_high = (perf.pressure.broker_high || 0) + 1;
    }
    if (pipeline.upload_q != null) {
        perf.pressure.upload_samples = (perf.pressure.upload_samples || 0) + 1;
        if (pipeline.upload_q > 0) perf.pressure.upload_high = (perf.pressure.upload_high || 0) + 1;
    }
    if (ppo) {
        for (const [key, value] of Object.entries(ppo)) push('ppo_' + key, value);
    }
}

function trainingSeconds(item) {
    return (item.stage_history || []).reduce((total, record) =>
        total + (finite(record?.performance?.training_s) || 0), 0);
}

// Record applied worker environment counts rather than inferred requests.
// Auto-tuning and manual overrides take the same path; the first observation
// establishes a baseline instead of inventing a change.
function observeWorkerEnvironmentCounts(item, status, step, trainingS, atMs = Date.now()) {
    const desired = status.desired || {};
    if (item.status !== 'running' || !desired.training_enabled ||
        desired.run_id !== item.run_id) return false;
    const counts = item.worker_env_counts || {};
    let changed = false;
    for (const worker of status.trainers || []) {
        if (worker.role !== 'dedicated' || worker.stale ||
            worker.process_state !== 'running' ||
            worker.build_id !== desired.canonical_build_id ||
            Number(worker.applied_revision) < Number(desired.revision)) continue;
        const count = worker.worker_capacity?.current_envs;
        const id = String(worker.trainer_id || '');
        if (!id || !Number.isSafeInteger(count) || count < 1) continue;
        if (!item.env_observation_started_at) {
            item.env_observation_started_at = new Date(atMs).toISOString();
            changed = true;
        }
        if (!Object.prototype.hasOwnProperty.call(counts, id)) {
            counts[id] = count;
            changed = true;
        } else if (counts[id] !== count) {
            item.env_count_changes = item.env_count_changes || [];
            item.env_count_changes.push({
                at: new Date(atMs).toISOString(), trainer_id: id,
                from: counts[id], to: count,
                step: Number.isSafeInteger(step) ? step : null,
                training_s: Number.isFinite(trainingS) ? trainingS : null,
            });
            if (item.env_count_changes.length > 512) item.env_count_changes.shift();
            counts[id] = count;
            changed = true;
        }
    }
    if (changed) item.worker_env_counts = counts;
    return changed;
}

function measure(item, status, force = false) {
    const learner = getAuthoritativeLearnerStats(item.run_id) || {};
    // Only score workers that have applied the current run revision. Heartbeats
    // from the outgoing run may remain live briefly during a release cutover.
    const desired = status.desired || {};
    const workers = (status.trainers || []).filter(x =>
        !x.stale && x.process_state === 'running' &&
        Number(x.applied_revision) >= Number(desired.revision) &&
        x.build_id === desired.canonical_build_id &&
        x.metrics && Number(x.metrics.window_episodes) > 0);
    const mean = key => {
        let count = 0, total = 0;
        for (const w of workers) {
            const n = Number(w.metrics.window_episodes), v = Number(w.metrics[key]);
            if (n > 0 && w.metrics[key] != null && Number.isFinite(v)) {
                count += n; total += n * v;
            }
        }
        return count ? Math.round(total / count * 10000) / 10000 : null;
    };
    const measuredAt = Date.now();
    const pipeline = livePipeline(status);
    const step = Number.isSafeInteger(learner.Step) && learner.Step >= 0 ? learner.Step : null;
    if (step != null) {
        if (item.step != null && step < item.step) throw Error('Learner step regressed.');
        if (item.step == null || step > item.step) item.last_progress = now();
        item.step = step;
    }
    observeTrainingTime(item, status, step, measuredAt, pipeline);
    observeWorkerEnvironmentCounts(item, status, step, trainingSeconds(item), measuredAt);
    const samples = item.samples;
    if (force || !samples.length ||
        Date.now() - Date.parse(samples[samples.length - 1].at) >= 60000) {
        const prior = samples.length ? samples[samples.length - 1] : null;
        // Preserve the last observed live worker metrics at finalization, when
        // workers may already be stopped and their rolling windows unavailable.
        const last = key => mean(key) ?? (prior ? prior[key] : null);
        const running = item.status === 'running' && Boolean(status.desired?.training_enabled);
        const ppo = running ? latestPpoMetrics(item.run_id) : null;
        if (running) performanceReading(item, pipeline, ppo);
        const perf = performanceFor(item);
        const observation = {
            at: now(), step, stage_index: item.stage_index || 0,
            training_s: trainingSeconds(item),
            stage_training_s: perf.training_s,
            effective_steps_per_second: perf.training_s > 0
                ? perf.steps / perf.training_s : null,
            ppo_busy_pct: perf.busy_observed_s > 0
                ? 100 * perf.optimizer_busy_s / perf.busy_observed_s : null,
            reward: Number.isFinite(learner.MeanReward) ? learner.MeanReward : null,
            steps_per_second: Number.isFinite(learner.LiveStepsPerSecond) ?
                learner.LiveStepsPerSecond : null,
            timeout_pct: last('timeout_pct'), bee_win_pct: last('bee_win_pct'),
            human_win_pct: last('human_win_pct'),
            bee_hits_per_shot: last('bee_hits_per_shot'),
            human_hits_per_shot: last('human_hits_per_shot'),
            bee_aim_error_deg: last('bee_aim_error_deg'),
            human_aim_error_deg: last('human_aim_error_deg'),
            bee_near_miss_pct: last('bee_near_miss_pct'),
            human_near_miss_pct: last('human_near_miss_pct'),
            ...(running ? { pipeline, ppo } : {}),
        };
        samples.push(observation);
        const stage = stageFor(item);
        const windowTimeout = mean('timeout_pct');
        if (!force && stage && stage.timeout_target_pct != null &&
            step != null && windowTimeout != null) {
            const record = stageRecord(item);
            if (record.last_sample_step !== step) {
                record.consecutive_qualifying_samples = windowTimeout <= stage.timeout_target_pct
                    ? record.consecutive_qualifying_samples + 1 : 0;
                record.last_sample_step = step;
            }
            // Three sampled training windows are a provisional milestone, not
            // independent benchmark evidence or an automatic stage transition.
            if (record.consecutive_qualifying_samples >= 3 && record.first_target_step == null) {
                record.first_target_step = step;
                record.first_target_at = observation.at;
            }
        }
        if (samples.length > 500) item.samples = samples.filter((_, i) =>
            i === 0 || i === samples.length - 1 || i % 2 === 0);
    }
}

const BENCHMARK_SETTINGS = path.join(paths.trainingRoot, 'Experiments', 'benchmark-settings.json');
const BENCHMARK_RUNNER = path.join(__dirname, 'bees_experiment_benchmark.py');
const SCRIPTED_OPPONENTS = path.join(__dirname, 'bees_experiment_scripted.py');
const SCRIPTED_NAMES = new Set(['stationary', 'strafe', 'pursuit']);
const hashFile = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

function benchmarkPlan() {
    const settings = readJson(BENCHMARK_SETTINGS);
    for (const key of ['python', 'environment', 'build_id']) {
        if (typeof settings[key] !== 'string' || !settings[key].trim()) {
            throw Error('Benchmark settings require ' + key + '.');
        }
    }
    if (!fs.statSync(path.resolve(settings.python)).isFile()) {
        throw Error('Benchmark Python executable is missing.');
    }
    const defined = settings.scenarios == null
        ? [{ id: 'default', recipe: settings.recipe, opponent: settings.opponent }]
        : settings.scenarios;
    if (!Array.isArray(defined) || defined.length < 1 || defined.length > 12) {
        throw Error('Benchmark suite must contain 1–12 scenarios.');
    }
    const ids = new Set();
    const files = {
        environment: path.resolve(settings.environment),
        evaluator: path.join(__dirname, 'bees_continual_evaluate.py'),
        runner: BENCHMARK_RUNNER,
    };
    const scenarios = defined.map((entry, index) => {
        if (!entry || typeof entry !== 'object' || Array.isArray(entry) ||
            typeof entry.id !== 'string' || !/^[a-z0-9][a-z0-9_-]{0,63}$/.test(entry.id) ||
            typeof entry.recipe !== 'string' || !entry.recipe.trim() || ids.has(entry.id)) {
            throw Error('Benchmark scenario ' + (index + 1) + ' requires a unique slug and recipe path.');
        }
        ids.add(entry.id);
        const opponentSpec = entry.opponent ?? settings.opponent;
        if (typeof opponentSpec !== 'string' || !opponentSpec.trim()) {
            throw Error('Benchmark scenario ' + entry.id + ' requires an opponent ONNX path or scripted controller.');
        }
        const recipe = path.resolve(entry.recipe);
        const recipeSpec = JSON.parse(fs.readFileSync(recipe, 'utf8'));
        if (!recipeSpec || recipeSpec.schema_version !== 1 ||
            !recipeSpec.id || !recipeSpec.matches) {
            throw Error('Benchmark scenario is missing versioned recipe metadata: ' + recipe);
        }
        files['recipe_' + index] = recipe;
        let opponent, opponent_kind;
        if (opponentSpec.startsWith('scripted:')) {
            const mode = opponentSpec.slice('scripted:'.length);
            if (!SCRIPTED_NAMES.has(mode)) {
                throw Error('Unknown scripted opponent ' + opponentSpec + '.');
            }
            opponent = opponentSpec;
            opponent_kind = 'scripted';
            files.scripted = SCRIPTED_OPPONENTS;
        } else {
            opponent = path.resolve(opponentSpec);
            opponent_kind = 'onnx';
            files['opponent_' + index] = opponent;
        }
        return {
            id: entry.id, recipe, benchmark_id: recipeSpec.id,
            benchmark_revision: recipeSpec.revision, opponent, opponent_kind,
        };
    });
    files.recipe = scenarios[0].recipe;
    if (scenarios[0].opponent_kind === 'onnx') files.opponent = scenarios[0].opponent;
    const hashes = Object.fromEntries(
        Object.entries(files).map(([key, file]) => [key, hashFile(file)]));
    for (const [index, scenario] of scenarios.entries()) {
        scenario.opponent_sha256 = scenario.opponent_kind === 'scripted'
            ? crypto.createHash('sha256')
                .update(scenario.opponent + ':' + hashes.scripted).digest('hex')
            : hashes['opponent_' + index];
    }
    return {
        python: path.resolve(settings.python), build_id: settings.build_id.trim(),
        files, hashes, scenarios,
    };
}

function verifyBenchmarkPlan(plan) {
    for (const [key, file] of Object.entries(plan.files)) {
        if (hashFile(file) !== plan.hashes[key]) throw Error('Benchmark ' + key + ' changed since it was selected.');
    }
}


function executionOptions(config) {
    return Object.fromEntries(['learnerDevice', 'threadedTraining', 'threadedPolicyLag',
        'generationSteps', 'numLocalEnvs', 'localOnly', 'minRemoteActors',
        'maxRemoteActors', 'learnerOptimizations', 'environmentArgs'].map(k => [k, config[k] ?? null]));
}

async function configTool(args) {
    const runtime = readJson(paths.centralRuntimePointerPath);
    return new Promise((resolve, reject) => {
        execFile(runtime.python_executable, [path.join(__dirname, 'bees_experiment_config.py'), ...args],
            { windowsHide: true, maxBuffer: 65536 }, (error, stdout, stderr) => {
                if (error) reject(Error(String(stderr || error.message).trim()));
                else { try { resolve(JSON.parse(stdout)); } catch (e) { reject(e); } }
            });
    });
}

async function trainerConfiguration(overrides, basePath = null) {
    const runtime = readJson(paths.centralRuntimePointerPath);
    const release = readJson(paths.latestReleasePath);
    if (runtime.runtime_version !== release.training_runtime.runtime_version) {
        throw Error('Prepared training runtime differs from the current release.');
    }
    const snapshot = await configTool(['prepare', '--base', basePath || path.join(runtime.runtime_root, 'rl_1v1_config.yaml'),
        '--overrides', JSON.stringify(overrides),
        '--output', path.join(paths.trainingRoot, 'Experiments', 'Configurations')]);
    return { ...snapshot, runtime_version: runtime.runtime_version };
}

function verifyTrainerSnapshot(item) {
    if (hashFile(item.trainer_config.path) !== item.trainer_config.sha256) {
        throw Error('Frozen experiment trainer configuration changed.');
    }
}

async function verifyEffectiveConfiguration(item) {
    verifyTrainerSnapshot(item);
    const release = readJson(paths.latestReleasePath);
    if (release.run_id !== item.run_id || release.experiment_config?.sha256 !== item.trainer_config.sha256) {
        throw Error('Active release does not own this experiment configuration.');
    }
    const root = path.join(paths.trainingRoot, 'metadata', 'continuous-service', item.run_id, 'trainer-configs');
    const files = fs.existsSync(root) ? fs.readdirSync(root).filter(f => /^generation-\d{8}[-a-z0-9]*\.yaml$/.test(f)) : [];
    item.applied_configurations = item.applied_configurations || [];
    for (const file of files) {
        const actual = path.join(root, file);
        if (item.applied_configurations.some(x => x.path === actual && x.sha256 === hashFile(actual))) continue;
        const result = await configTool(['verify', '--expected', item.trainer_config.path, '--actual', actual]);
        item.applied_configurations.push({ ...result, verified_at: now() });
    }
    if (item.step > 0 && !item.applied_configurations.length) {
        throw Error('Learner progressed without a verifiable generated experiment configuration.');
    }
}

async function evaluateItem(item) {
    try {
        const status = await control();
        if (!status.desired || status.desired.training_enabled || status.desired.pending_release) {
            throw Error('Benchmark requires idle training with no release in progress.');
        }
        verifyBenchmarkPlan(item.benchmark);
        item.benchmark_status = 'running';
        state.message = 'Evaluating checkpoint for ' + item.name;
        save();
        const plan = item.benchmark;
        const args = [BENCHMARK_RUNNER, '--benchmark', plan.files.recipe,
            '--candidate-run', item.run_id,
            '--registry', path.join(paths.trainingRoot, 'registry.sqlite3'),
            '--opponent', plan.scenarios?.[0]?.opponent || plan.files.opponent, '--env', plan.files.environment,
            '--build-id', plan.build_id, '--output-root', BENCHMARK_REPORTS];
        const output = await new Promise((resolve, reject) => {
            execFile(plan.python, args, { windowsHide: true, maxBuffer: 65536 },
                (error, stdout, stderr) => error
                    ? reject(Error(String(stderr || error.message).trim()))
                    : resolve(stdout));
        });
        const reportPath = String(output).trim().split(/\r?\n/).pop();
        if (path.dirname(path.resolve(reportPath)) !== path.resolve(BENCHMARK_REPORTS)) {
            throw Error('Evaluator returned an unexpected report path.');
        }
        const report = readJson(reportPath);
        if (report.run_id !== item.run_id || !report.metrics || !report.metrics.telemetry_validated) {
            throw Error('Evaluator did not return validated outcomes for this run.');
        }
        item.benchmark_report = path.basename(reportPath);
        item.benchmark_status = 'completed';
        delete item.benchmark_error;
        state.message = '';
    } catch (error) {
        item.benchmark_status = 'failed';
        item.benchmark_error = error.message;
        state.armed = false;
        state.message = 'Checkpoint evaluation failed; queue paused: ' + error.message;
    }
    state.active_id = null;
    state.stop_requested = false;
    save();
}

function execBenchmark(python, args) {
    return new Promise((resolve, reject) => {
        execFile(python, args, { windowsHide: true, maxBuffer: 65536 },
            (error, stdout, stderr) => error
                ? reject(Error(String(stderr || error.message).trim()))
                : resolve(String(stdout).trim()));
    });
}

function verifyStageCandidate(candidate, minimumStep) {
    if (!candidate || typeof candidate.path !== 'string' ||
        !/^[a-f0-9]{64}$/.test(String(candidate.sha256)) ||
        !Number.isSafeInteger(candidate.training_step) ||
        candidate.training_step < minimumStep ||
        !fs.statSync(candidate.path).isFile() ||
        hashFile(candidate.path) !== candidate.sha256) {
        throw Error('No verified ONNX export at or after this stage boundary; refusing stale-model evaluation.');
    }
}

function stageEvaluationKey(item, index, scenario, scenarioIndex, candidate, plan) {
    const identity = [
        item.id, item.run_id, index, scenario.id,
        plan.hashes['recipe_' + scenarioIndex],
        scenario.opponent_sha256 || plan.hashes.opponent,
        plan.hashes.environment, plan.hashes.evaluator, plan.hashes.runner,
        plan.build_id, candidate.sha256, candidate.training_step,
    ];
    return crypto.createHash('sha256').update(JSON.stringify(identity)).digest('hex');
}

function stageReportMatches(report, item, index, scenario, candidate, plan, evaluationId) {
    return report && report.schema_version === 1 &&
        report.run_id === item.run_id &&
        report.candidate_sha256 === candidate.sha256 &&
        report.opponent_sha256 === (scenario.opponent_sha256 || plan.hashes.opponent) &&
        (!scenario.opponent || report.opponent_path === scenario.opponent) &&
        (scenario.opponent_kind !== 'scripted' ||
            (report.opponent_kind === 'scripted' &&
                report.opponent_script_sha256 === plan.hashes.scripted)) &&
        report.environment_executable_sha256 === plan.hashes.environment &&
        report.evaluation_code_sha256 === plan.hashes.evaluator &&
        report.build_id === plan.build_id &&
        report.benchmark?.id === scenario.benchmark_id &&
        report.benchmark?.revision === scenario.benchmark_revision &&
        report.experiment?.id === item.id &&
        report.experiment?.stage_index === index &&
        report.experiment?.evaluation_id === evaluationId &&
        report.experiment?.candidate_training_step === candidate.training_step &&
        report.metrics?.telemetry_validated === true;
}

function findStageReport(item, index, scenario, candidate, plan, evaluationId, savedName) {
    // A process can exit after the evaluator atomically creates its report but
    // before the coordinator stores the filename. Re-link that immutable result
    // instead of launching the same benchmark a second time.
    const validFilename = name => /^\d{8}T\d{6}-[a-f0-9]{32}\.json$/.test(name);
    let names = [];
    if (savedName && validFilename(savedName)) names.push(savedName);
    if (fs.existsSync(BENCHMARK_REPORTS)) {
        names.push(...fs.readdirSync(BENCHMARK_REPORTS)
            .filter(validFilename).sort().reverse());
    }
    for (const name of new Set(names)) {
        const target = path.join(BENCHMARK_REPORTS, name);
        if (!fs.existsSync(target)) continue;
        const report = readJson(target);
        if (stageReportMatches(report, item, index, scenario, candidate, plan, evaluationId)) {
            return name;
        }
        if (name === savedName) {
            throw Error('Previously recorded stage benchmark no longer matches its frozen provenance.');
        }
    }
    return null;
}

async function evaluateStageSuite(item, index, status) {
    const plan = item.stage_benchmarks;
    if (!plan) return true;
    const record = stageRecord(item, index);
    try {
        if (!status.desired || status.desired.training_enabled ||
            status.desired.pending_release || status.desired.run_id !== item.run_id ||
            status.desired.canonical_build_id !== item.release_build) {
            throw Error('Stage benchmark requires a stopped, unchanged training run.');
        }
        verifyTrainerSnapshot(item);
        verifyBenchmarkPlan(plan);
        if (!record.evaluation) {
            const result = await execBenchmark(plan.python, [
                BENCHMARK_RUNNER, '--resolve-candidate-run', item.run_id,
                '--registry', path.join(paths.trainingRoot, 'registry.sqlite3'),
            ]);
            const candidate = JSON.parse(result);
            const boundary = Math.max(record.entered_step || 0, item.step || 0);
            verifyStageCandidate(candidate, boundary);
            record.evaluation = {
                candidate, boundary_step: item.step, started_at: now(),
                status: 'running', reports: {},
            };
            save();
        }
        const evaluation = record.evaluation;
        verifyStageCandidate(evaluation.candidate,
            Math.max(record.entered_step || 0, evaluation.boundary_step || 0));
        evaluation.reports = evaluation.reports || {};
        for (const [scenarioIndex, scenario] of plan.scenarios.entries()) {
            verifyBenchmarkPlan(plan);
            const evaluationId = stageEvaluationKey(
                item, index, scenario, scenarioIndex, evaluation.candidate, plan);
            let reportName = findStageReport(item, index, scenario, evaluation.candidate,
                plan, evaluationId, evaluation.reports[scenario.id]);
            if (!reportName) {
                state.message = 'Stage ' + (index + 1) + ' evaluation: ' + scenario.id +
                    ' for ' + item.name;
                save();
                const output = await execBenchmark(plan.python, [
                    BENCHMARK_RUNNER, '--benchmark', scenario.recipe,
                    '--candidate', evaluation.candidate.path,
                    '--candidate-sha256', evaluation.candidate.sha256,
                    '--candidate-training-step', String(evaluation.candidate.training_step),
                    '--run-id', item.run_id, '--experiment-id', item.id,
                    '--stage-index', String(index), '--stage-name', item.stages[index].name,
                    '--stage-step', String(evaluation.boundary_step),
                    '--evaluation-id', evaluationId,
                    '--opponent', scenario.opponent || plan.files.opponent, '--env', plan.files.environment,
                    '--build-id', plan.build_id, '--output-root', BENCHMARK_REPORTS,
                ]);
                reportName = path.basename(output.split(/\r?\n/).pop());
                if (!findStageReport(item, index, scenario, evaluation.candidate,
                    plan, evaluationId, reportName)) {
                    throw Error('Evaluator did not return a matching immutable stage report.');
                }
            }
            evaluation.reports[scenario.id] = reportName;
            save();
        }
        evaluation.status = 'completed';
        evaluation.finished_at = now();
        delete evaluation.error;
        delete item.stage_evaluation_error;
        save();
        return true;
    } catch (error) {
        if (record.evaluation) {
            record.evaluation.status = 'failed';
            record.evaluation.error = error.message;
        }
        item.stage_evaluation_error = error.message;
        state.armed = false;
        state.message = 'Stage evaluation failed; queue disarmed: ' + error.message;
        save();
        return false;
    }
}

function blocked(item, message) {
    if (item) { item.status = 'interrupted'; item.error = message; }
    state.armed = false;
    state.stop_requested = false;
    state.active_id = null;
    state.message = message;
    save();
}

async function finishFinalStageEvaluation(item, status) {
    if (state.stop_requested) {
        state.stop_requested = false;
        state.armed = false;
        state.message = 'Final-stage evaluation paused; inspect partial reports, then rearm to finish.';
        save();
        return;
    }
    if (!state.armed) return;
    if (!(await evaluateStageSuite(item, item.stage_index || 0, status))) return;
    item.status = 'completed';
    item.finished_at = item.finished_at || now();
    state.active_id = null;
    state.stop_requested = false;
    state.message = '';
    save();
}

async function finalize(item, status) {
    if (!status.desired || status.desired.run_id !== item.run_id) {
        blocked(item, 'Control plane changed run ownership during stop.');
        return;
    }
    // A disabled desired flag alone does not prove remote trainers have
    // finalized their checkpoints. The operator must confirm the whole stop,
    // including when a prior attempt already disabled training but then failed.
    await invokeStop({});
    status = await control();
    if (!status.desired || status.desired.run_id !== item.run_id ||
        status.desired.training_enabled) {
        throw Error('Safe training stop is unconfirmed; refusing to advance the queue.');
    }
    measure(item, status, true);
    item.status = item.end_reason === 'budget' ? 'completed' :
        item.end_reason === 'manual' ? 'stopped' : 'ended_early';
    item.finished_at = now();
    if (stageFor(item)) {
        const record = stageRecord(item);
        if (!record.finished_at) {
            record.finished_at = item.finished_at;
            record.finished_step = item.step;
            record.exit_reason = item.end_reason;
        }
    }
    if (item.stage_benchmarks && item.status === 'completed') {
        item.status = 'evaluating_final_stage';
        save();
        await finishFinalStageEvaluation(item, status);
        return;
    }
    if (item.benchmark && item.status === 'completed') {
        item.benchmark_status = 'pending';
        save();
        await evaluateItem(item);
        return;
    }
    state.active_id = null;
    state.stop_requested = false;
    state.message = '';
    save();
}

function assertSameLineageCheckpoint(item) {
    const release = readJson(paths.latestReleasePath);
    if (!release || release.run_id !== item.run_id ||
        release.build_id !== item.release_build ||
        release.experiment_config?.sha256 !== item.trainer_config?.sha256) {
        throw Error('Run or frozen trainer configuration changed before curriculum transition.');
    }
    const behavior = String(release.contract?.behavior_name || '');
    if (!/^[A-Za-z0-9._-]+$/.test(behavior)) throw Error('Cannot resolve checkpoint behavior.');
    const checkpoint = path.join(paths.trainingRoot, 'trainer-results',
        item.run_id, behavior, 'checkpoint.pt');
    const info = fs.statSync(checkpoint);
    if (!info.isFile() || info.size === 0) {
        throw Error('Same-run transition requires a saved optimizer checkpoint: ' + checkpoint);
    }
    if (getPendingForcedNewRunPlan()) {
        throw Error('Another forced-new run plan is pending; refusing a same-run stage restart.');
    }
    // The resumed ML-Agents trainer verifies tensor compatibility. This check
    // prevents silently initializing from scratch when no checkpoint exists.
}


// Maintenance pause preserves the active experiment, run, checkpoint, and queue
// position. Unlike a manual stop, it never finalizes a stage or advances a run.
function assertMaintenanceOwnership(item, status, allowOffline = false) {
    const release = readJson(paths.latestReleasePath);
    verifyTrainerSnapshot(item);
    if (!release || release.run_id !== item.run_id ||
        release.build_id !== item.release_build ||
        release.experiment_config?.sha256 !== item.trainer_config?.sha256 ||
        (item.trainer_config?.runtime_version &&
            release.training_runtime?.runtime_version !== item.trainer_config.runtime_version)) {
        throw Error('Paused experiment does not match its frozen run, release, or trainer configuration.');
    }
    // Preserve the original execution contract even if operator defaults were
    // edited while the manager was stopped.
    const currentOptions = executionOptions(loadConfig());
    if (item.execution_options && Object.entries(item.execution_options).some(
        ([key, value]) => JSON.stringify(currentOptions[key]) !== JSON.stringify(value)
    )) {
        throw Error('Training execution options changed during maintenance. Restore the frozen settings before resuming.');
    }
    if (!status) {
        if (allowOffline) return; // Operator start can boot its own control server.
        throw Error('Training control is unavailable; cannot verify maintenance ownership.');
    }
    const d = status.desired || {};
    if (d.run_id !== item.run_id ||
        d.canonical_build_id !== item.release_build ||
        !matches(d.environment_args, item.env_args) || d.pending_release) {
        throw Error('Training control no longer owns the paused experiment run/build/environment.');
    }
}

async function pauseActiveRun(item, status) {
    assertMaintenanceOwnership(item, status);
    // Operator stop owns distributed shutdown and checkpoint finalization.
    await invokeStop({});
    const stopped = await control();
    assertMaintenanceOwnership(item, stopped);
    if (stopped.desired.training_enabled) {
        throw Error('Training remains enabled after maintenance stop; not marking experiment paused.');
    }
    assertSameLineageCheckpoint(item);
    measure(item, stopped, true);
    delete item.performance_clock;
    item.status = 'paused';
    item.paused_at = now();
    delete item.error;
    state.message = 'Paused ' + item.name +
        ' with a verified optimizer checkpoint. The manager can now be updated or restarted.';
    save();
}

function completeMaintenanceResume(item) {
    const pauseStart = Date.parse(item.pause_started_at);
    if (!Number.isFinite(pauseStart) || pauseStart > Date.now()) {
        throw Error('Paused experiment is missing a valid maintenance start timestamp.');
    }
    item.paused_ms = (item.paused_ms || 0) + Math.max(0, Date.now() - pauseStart);
    delete item.pause_started_at;
    delete item.paused_at;
    delete item.resume_attempted;
    delete item.performance_clock;
    delete item.error;
    item.status = 'running';
    item.last_progress = now();
    state.armed = true;
    state.message = 'Resumed ' + item.name + ' with the same run ID and optimizer checkpoint.';
    save();
}

async function resumeActiveRun(item, status) {
    assertMaintenanceOwnership(item, status, !item.resume_attempted);
    assertSameLineageCheckpoint(item);
    await verifyEffectiveConfiguration(item);
    if (item.resume_attempted) {
        // A coordinator restart during resume never blindly launches another
        // trainer. Reconcile the exact run or return to a deliberate pause.
        if (!status.desired.training_enabled) {
            item.status = 'paused';
            delete item.resume_attempted;
            state.armed = false;
            state.message = 'Resume was interrupted before training was enabled. Select Resume again.';
            save();
            return;
        }
        completeMaintenanceResume(item);
        return;
    }
    if (status?.desired?.training_enabled) {
        throw Error('Training was started outside the manager; refusing maintenance resume.');
    }
    item.resume_attempted = true;
    save();
    // Ordinary same-run start uses the existing immutable release and
    // ML-Agents resume path; it does not build or create a new experiment.
    await invokeStart({ envArgs: item.env_args });
    const running = await control();
    assertMaintenanceOwnership(item, running);
    if (!running.desired.training_enabled) {
        throw Error('Same-run resume did not enable training.');
    }
    completeMaintenanceResume(item);
}

async function transitionStage(item, status) {
    const index = item.stage_index || 0;
    const previous = item.stages[index];
    const next = item.stages[index + 1];
    if (!previous || !next) throw Error('Invalid saved curriculum stage index.');
    const d = status.desired || {};
    if (d.run_id !== item.run_id || d.pending_release ||
        String(d.canonical_build_id || '') !== item.release_build) {
        blocked(item, 'Curriculum transition lost control-plane run/build ownership.');
        return;
    }
    if (!matches(d.environment_args, previous.env_args) &&
        !matches(d.environment_args, next.env_args)) {
        blocked(item, 'Another operator changed environment arguments during curriculum transition.');
        return;
    }
    if (!state.armed && !state.stop_requested) return; // error pause: do not restart
    if (state.stop_requested || !state.armed) {
        item.end_reason = 'manual';
        item.status = 'stopping';
        save();
        await finalize(item, status);
        return;
    }
    verifyTrainerSnapshot(item);
    if (item.stage_stop_confirmed && d.training_enabled &&
        matches(d.environment_args, previous.env_args)) {
        blocked(item, 'Previous curriculum stage restarted after its verified stop; refusing to evaluate or overwrite the pinned boundary.');
        return;
    }
    if (matches(previous.env_args, next.env_args) && !item.stage_benchmarks) {
        if (!d.training_enabled) {
            blocked(item, 'Training was disabled outside the manager at a metric-only stage boundary.');
            return;
        }
        // A metric-only stage boundary needs no optimizer restart.
        const finished = stageRecord(item, index);
        if (!finished.finished_at) {
            finished.finished_at = now();
            finished.finished_step = item.step;
        }
        item.stage_index = index + 1;
        stageRecord(item, item.stage_index);
        item.status = 'running';
        item.stage_transition_at = null;
        save();
        return;
    }
    if (matches(d.environment_args, previous.env_args) && d.training_enabled) {
        item.stage_stop_confirmed = false;
        save();
        await invokeStop({});
        status = await control();
        if (status.desired?.run_id !== item.run_id ||
            status.desired.training_enabled ||
            !matches(status.desired.environment_args, previous.env_args)) {
            throw Error('Safe curriculum stop not confirmed; refusing to switch environments.');
        }
        item.stage_stop_confirmed = true;
        const finished = stageRecord(item, index);
        if (!finished.finished_at) {
            finished.finished_at = now();
            finished.finished_step = item.step;
        }
        save();
    }
    if (state.stop_requested || !state.armed) {
        item.end_reason = 'manual';
        item.status = 'stopping';
        save();
        await finalize(item, status);
        return;
    }
    if (!item.stage_stop_confirmed) {
        blocked(item, 'Curriculum stop ownership was not durably confirmed; inspect the stopped run before manual recovery.');
        return;
    }
    if (status.desired.training_enabled && item.stage_benchmarks &&
        stageRecord(item, index).evaluation?.status !== 'completed' &&
        matches(status.desired.environment_args, next.env_args)) {
        blocked(item, 'Next curriculum stage was started without verified stage evaluations.');
        return;
    }
    if (!status.desired.training_enabled) {
        assertSameLineageCheckpoint(item);
        if (!(await evaluateStageSuite(item, index, status))) return;
        if (state.stop_requested || !state.armed) {
            item.end_reason = 'manual';
            item.status = 'stopping';
            save();
            await finalize(item, status);
            return;
        }
        assertSameLineageCheckpoint(item);
        await invokeStart({ envArgs: next.env_args });
        status = await control();
    }
    if (state.stop_requested || !state.armed) {
        item.end_reason = 'manual';
        item.status = 'stopping';
        save();
        await finalize(item, status);
        return;
    }
    if (!status.desired || !status.desired.training_enabled ||
        status.desired.run_id !== item.run_id || status.desired.pending_release ||
        !matches(status.desired.environment_args, next.env_args)) {
        throw Error('Same-run curriculum restart is not confirmed.');
    }
    const finished = stageRecord(item, index);
    if (!finished.finished_at) {
        finished.finished_at = now();
        finished.finished_step = item.step;
    }
    item.stage_index = index + 1;
    item.env_args = next.env_args;
    stageRecord(item, item.stage_index);
    item.last_progress = now();
    item.stage_transition_at = null;
    delete item.stage_stop_confirmed;
    item.status = 'running';
    state.message = 'Continued ' + item.name + ' on ' + next.name + ' (same run and optimizer).';
    save();
}

async function monitor() {
    const item = active();
    if (!item) { blocked(null, 'Active experiment record is missing.'); return; }
    if (item.status === 'paused') return;
    if (['pending', 'running'].includes(item.benchmark_status)) {
        await evaluateItem(item);
        return;
    }
    let status;
    try {
        status = await control();
    } catch (error) {
        // A fully stopped host can have no BeesServer process. The ordinary
        // same-run operator start brings it online, but only on a fresh,
        // explicitly requested resume and only for connection refusal.
        if (!(item.status === 'resuming' && !item.resume_attempted &&
            /ECONNREFUSED/.test(String(error.code || error.message)))) throw error;
        status = null;
    }
    const d = status?.desired || {};
    if (item.status === 'pausing') {
        await pauseActiveRun(item, status);
        return;
    }
    if (item.status === 'resuming') {
        await resumeActiveRun(item, status);
        return;
    }
    if (item.status === 'transitioning') {
        await transitionStage(item, status);
        return;
    }
    if (item.status === 'evaluating_final_stage') {
        await finishFinalStageEvaluation(item, status);
        return;
    }
    if (item.status === 'starting') {
        // Recovery is intentionally conservative: never assume another live run is ours.
        if (!(d.training_enabled && d.run_id && d.run_id !== item.previous_run_id &&
            matches(d.environment_args, item.env_args) &&
            (!item.trainer_config || readJson(paths.latestReleasePath).experiment_config?.sha256 === item.trainer_config.sha256))) {
            blocked(item, 'Interrupted startup could not be matched to a new run; inspect training.');
            return;
        }
        item.run_id = String(d.run_id);
        item.build_id = String(d.canonical_build_id || '');
        item.started_at = item.started_at || now();
        item.last_progress = item.started_at;
        item.status = 'running';
        if (item.stages) stageRecord(item, 0);
        save();
    }
    if (item.status === 'stopping') {
        if (d.run_id !== item.run_id) {
            blocked(item, 'Another training run owns the control plane.');
            return;
        }
        await finalize(item, status);
        return;
    }
    if (!d.training_enabled || d.run_id !== item.run_id ||
        !matches(d.environment_args, item.env_args)) {
        blocked(item, 'Training was changed outside the experiment manager; queue paused.');
        return;
    }
    measure(item, status);
    let configurationError = '';
    if (item.trainer_config) {
        try { await verifyEffectiveConfiguration(item); }
        catch (error) { configurationError = error.message; item.error = error.message; state.armed = false; }
    }
    const elapsed = Date.now() - Date.parse(item.started_at) - (item.paused_ms || 0);
    const stagnant = Date.now() - Date.parse(item.last_progress);
    const reason = configurationError ? 'configuration_mismatch' : state.stop_requested ? 'manual' :
        item.step != null && item.step >= item.max_steps ? 'budget' :
        elapsed >= item.max_minutes * 60000 ? 'time_limit' :
        stagnant >= item.stall_minutes * 60000 ? 'no_progress' : '';
    if (reason) {
        item.status = 'stopping';
        item.end_reason = reason;
        state.message = 'Safely stopping ' + item.name + ' (' + reason + ')';
        save();
        await finalize(item, status);
    } else if (item.stages && item.stage_index + 1 < item.stages.length &&
        item.step != null && item.step >= item.stages[item.stage_index].end_step) {
        item.status = 'transitioning';
        item.stage_transition_at = now();
        state.message = 'Switching ' + item.name + ' to ' + item.stages[item.stage_index + 1].name;
        save();
        await transitionStage(item, status);
    } else save();
}

async function launch() {
    const item = state.items.find(x => x.status === 'queued');
    if (!item) { state.armed = false; save(); return; }
    const status = await control();
    // A Stop click may arrive while the control request is in flight, before
    // this experiment has an active_id. Never start after the queue is disarmed.
    if (!state.armed || state.stop_requested) return;
    if (!status.desired || status.desired.training_enabled || status.desired.pending_release) {
        state.armed = false;
        state.message = 'Another training run or release is active. Queue not started.';
        save(); return;
    }
    const latest = readJson(paths.latestReleasePath);
    if (String(latest.build_id || '') !== item.release_build) {
        state.armed = false;
        state.message = 'Release changed since experiment was queued. Review before starting.';
        save(); return;
    }
    if (item.benchmark) verifyBenchmarkPlan(item.benchmark);
    if (item.stage_benchmarks) verifyBenchmarkPlan(item.stage_benchmarks);
    if (item.trainer_config) {
        verifyTrainerSnapshot(item);
        if (item.trainer_config.runtime_version && item.trainer_config.runtime_version !== latest.training_runtime.runtime_version) {
            throw Error('Training runtime changed since this experiment was queued.');
        }
        // Older queued items did not freeze every later-added execution field.
        // Compare the exact set they recorded; new items include the complete set.
        const currentOptions = executionOptions(loadConfig());
        if (!item.execution_options || Object.entries(item.execution_options).some(
            ([key, value]) => JSON.stringify(currentOptions[key]) !== JSON.stringify(value)
        )) {
            throw Error('Training execution settings changed since this experiment was queued.');
        }
    }
    // Validate every queued environment against the actual Unity release
    // before spending training steps. These preflights are cached by the
    // operator's existing release/argument validation identity.
    const config = loadConfig();
    const python = resolvePython(config);
    for (const stage of item.stages || [{ env_args: item.env_args }]) {
        await assertRlEnvironmentArgsValid(config, latest, stage.env_args, python);
    }
    if (!state.armed || state.stop_requested) return;
    const afterValidation = await control();
    if (!afterValidation.desired ||
        afterValidation.desired.training_enabled ||
        afterValidation.desired.pending_release ||
        afterValidation.desired.run_id !== status.desired.run_id ||
        readJson(paths.latestReleasePath).build_id !== item.release_build) {
        state.armed = false;
        state.message = 'Training or release ownership changed during curriculum validation.';
        save();
        return;
    }
    item.previous_run_id = String(afterValidation.desired.run_id || '');
    item.status = 'starting';
    item.started_at = now();
    state.active_id = item.id;
    state.message = 'Starting ' + item.name;
    save();
    await invokeStart({ newRun: true, envArgs: item.env_args, experimentConfig: item.trainer_config });
    await monitor();
}

async function cycle() {
    if (busy) return;
    busy = true;
    try {
        if (state.active_id) {
            await monitor();
            // The outgoing run has completed a verified stop. Move to the next
            // queued run now rather than leave remote workers idle for a poll.
            if (!state.active_id && state.armed && !state.stop_requested) await launch();
        } else if (state.armed && !state.stop_requested) await launch();
    } catch (err) {
        state.armed = false; // fail closed; do not start another experiment
        // A failed preflight must return to an explicit Paused state. Do not
        // silently resume later just because the release/control issue clears.
        if (active()?.status === 'resuming' && !active().resume_attempted) {
            active().status = 'paused';
            delete active().resume_attempted;
        }
        if (active()?.status === 'transitioning') state.stop_requested = true;
        state.message = 'Operator attention required: ' + err.message;
        if (active()) active().error = err.message;
        save();
        console.error('[experiments]', err.stack || String(err));
    } finally {
        if (!state.active_id && state.stop_requested) {
            state.stop_requested = false;
            save();
        }
        busy = false;
    }
}

async function definition(input, original = null) {
    const name = String(input.name || '').trim(), group = String(input.group || 'General').trim();
    const max_steps = Number(input.max_steps), max_minutes = Number(input.max_minutes);
    const stall_minutes = Number(input.stall_minutes);
    if (!name || name.length > 100 || !group || group.length > 80) throw Error('Invalid name or group.');
    if (!Number.isSafeInteger(max_steps) || max_steps < 1000 || max_steps > 5000000000000)
        throw Error('Invalid step budget.');
    if (!Number.isInteger(max_minutes) || max_minutes < 10 || max_minutes > 43200)
        throw Error('Time limit must be 10-43200 minutes.');
    if (!Number.isInteger(stall_minutes) || stall_minutes < 10 || stall_minutes > max_minutes)
        throw Error('No-progress limit must be between 10 minutes and the time limit.');
    const config = loadConfig();
    // Edits retain the exact environment baseline frozen when the item was queued.
    // Re-reading mutable operator defaults could silently change unrelated stages.
    const defaults = original?.queue_defaults ||
        (Array.isArray(config.environmentArgs) ? config.environmentArgs.map(String) : []);
    const stages = stagesForDefinition(input, defaults, max_steps);
    if (input.benchmark === true && input.evaluate_stages === true) {
        throw Error('Choose stage-suite evaluations or final-only evaluation, not both.');
    }
    const env_args = stages[0].env_args;
    const release = readJson(paths.latestReleasePath);
    if (!release || !release.build_id) throw Error('Build a training release before queuing experiments.');
    if (original) {
        verifyTrainerSnapshot(original);
        if (original.release_build !== String(release.build_id) ||
            original.trainer_config?.runtime_version !== release.training_runtime?.runtime_version) {
            throw Error('Queued release or training runtime changed; cannot safely edit this definition.');
        }
    }
    const pinnedBenchmarks = original?.stage_benchmarks || original?.benchmark || null;
    return {
        id: crypto.randomUUID(), name, group, status: 'queued', created_at: now(),
        max_steps: stages[stages.length - 1].end_step,
        max_minutes, stall_minutes, env_args, stages, stage_index: 0, stage_history: [],
        release_build: String(release.build_id), samples: [], step: null, run_id: null,
        benchmark: input.benchmark === true ? (pinnedBenchmarks || benchmarkPlan()) : null,
        stage_benchmarks: input.evaluate_stages === true ? (pinnedBenchmarks || benchmarkPlan()) : null,
        trainer_config: await trainerConfiguration(input.trainer || {}, original?.trainer_config?.path),
        execution_options: original?.execution_options || executionOptions(config),
        queue_defaults: defaults,
    };
}

// Editable definitions are reconstructed from frozen effective stages, not from
// today's operator defaults. Legacy queued items can be edited only if their
// effective stage arguments can be round-tripped without changing any settings.
function savedStageOverrides(previous, effective) {
    if (!Array.isArray(previous) || !Array.isArray(effective)) {
        throw Error('Queued experiment has invalid frozen environment arguments.');
    }
    for (let prefix = effective.length; prefix >= 0; prefix--) {
        const overrides = effective.slice(prefix);
        if (overrides.length > 64) continue;
        try {
            if (matches(mergeEnvironmentArgs(previous, overrides), effective)) {
                return overrides;
            }
        } catch (_) { /* A suffix containing incompatible map options cannot be an override. */ }
    }
    throw Error('Cannot reconstruct the queued environment overrides without changing the frozen stages.');
}

function canEditQueued(item) {
    return Boolean(item && item.status === 'queued' &&
        !item.run_id && !item.started_at && item.id !== state.active_id);
}

function queuedDefaultsHash(item) {
    const defaults = item.queue_defaults ||
        (Array.isArray(loadConfig().environmentArgs) ? loadConfig().environmentArgs.map(String) : []);
    return crypto.createHash('sha256').update(JSON.stringify(defaults)).digest('hex');
}

function editableDefinition(item) {
    if (!canEditQueued(item) || !item.trainer_config?.resolved ||
        !Array.isArray(item.stages) || !item.stages.length) {
        throw Error('Only unstarted queued experiments with frozen trainer settings can be edited.');
    }
    const defaults = item.queue_defaults ||
        (Array.isArray(loadConfig().environmentArgs) ? loadConfig().environmentArgs.map(String) : []);
    const stages = item.stages;
    const trainer = item.trainer_config.resolved;
    const input = {
        name: item.name, group: item.group,
        max_steps: stages[0].end_step, max_minutes: item.max_minutes,
        stall_minutes: item.stall_minutes,
        env_args: savedStageOverrides(defaults, stages[0].env_args),
        stage_name: stages[0].name,
        target_timeout_pct: stages[0].timeout_target_pct,
        stages: stages.slice(1).map((stage, index) => ({
            name: stage.name,
            steps: stage.end_step - stages[index].end_step,
            target_timeout_pct: stage.timeout_target_pct,
            env_args: savedStageOverrides(stages[index].env_args, stage.env_args),
        })),
        benchmark: Boolean(item.benchmark),
        evaluate_stages: Boolean(item.stage_benchmarks),
        trainer: {
            hyperparameters: Object.fromEntries(
                ['beta', 'lambd', 'learning_rate', 'batch_size', 'buffer_size', 'num_epoch']
                    .map(key => [key, trainer.hyperparameters[key]])),
            network_settings: {
                num_layers: trainer.network_settings.num_layers,
                normalize: trainer.network_settings.normalize,
            },
            seed: trainer.seed,
        },
    };
    // Refuse legacy edits if the currently available defaults differ from the
    // original implicit baseline. A no-op form submission must be a no-op.
    if (!matches(stagesForDefinition(input, defaults, input.max_steps), stages)) {
        throw Error('Queued stages cannot be reconstructed exactly; editing would change their settings.');
    }
    return input;
}

function benchmarkReports() {
    if (!fs.existsSync(BENCHMARK_REPORTS)) return [];
    // Reports are small immutable JSON summaries. Never enumerate raw training
    // data, checkpoints or unrelated files to populate the UI.
    return fs.readdirSync(BENCHMARK_REPORTS, { withFileTypes: true })
        .filter(entry => entry.isFile() &&
            /^\d{8}T\d{6}-[a-f0-9]{32}\.json$/.test(entry.name))
        .map(entry => entry.name)
        .sort().reverse().slice(0, 1000)
        .map(name => readJson(path.join(BENCHMARK_REPORTS, name)))
        .filter(report => report && report.schema_version === 1 && report.benchmark &&
            report.metrics);
}

function json(res, status, obj) {
    const body = JSON.stringify(obj);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff' });
    res.end(body);
}
async function body(req) {
    if (!String(req.headers['content-type'] || '').startsWith('application/json'))
        throw Error('JSON requests only.');
    let raw = '';
    for await (const chunk of req) {
        raw += chunk.toString('utf8');
        if (raw.length > 32768) throw Error('Request exceeds 32 KB.');
    }
    return JSON.parse(raw || '{}');
}

const server = http.createServer(async (req, res) => {
    const host = req.headers.host, origin = req.headers.origin;
    if (!((host === HOST + ':' + PORT || host === 'localhost:' + PORT) &&
        (!origin || origin === 'http://' + HOST + ':' + PORT ||
         origin === 'http://localhost:' + PORT))) {
        return json(res, 403, { error: 'Loopback-only web interface.' });
    }
    try {
        const url = new URL(req.url, 'http://localhost');
        const route = url.pathname;
        if (req.method === 'GET' && route === '/') {
            const page = fs.readFileSync(HTML);
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8',
                'Content-Length': page.length, 'Cache-Control': 'no-store',
                'X-Content-Type-Options': 'nosniff',
                'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'" });
            return res.end(page);
        }
        if (req.method === 'GET' && route === '/api/state') {
            return json(res, 200, { ...state, busy, benchmark_configured: fs.existsSync(BENCHMARK_SETTINGS) });
        }
        if (req.method === 'GET' && route === '/api/configuration') {
            return json(res, 200, await trainerConfiguration({}));
        }
        if (req.method === 'GET' && route === '/api/workers') {
            const status = await control();
            const item = active();
            if (item?.status === 'running') {
                const measuredAt = Date.now();
                const clock = item.performance_clock;
                const trainingS = trainingSeconds(item) +
                    (clock && Number.isFinite(clock.at_ms) && measuredAt >= clock.at_ms
                        ? (measuredAt - clock.at_ms) / 1000 : 0);
                const events = item.env_count_changes || [];
                const previousEvent = events[events.length - 1];
                if (observeWorkerEnvironmentCounts(item, status, item.step, trainingS, measuredAt)) {
                    // Worker-panel refreshes are frequent; read the learner log
                    // for a precise step only when an applied count actually changed.
                    const changes = item.env_count_changes || [];
                    if (changes[changes.length - 1] !== previousEvent) {
                        const learner = getAuthoritativeLearnerStats(item.run_id) || {};
                        if (Number.isSafeInteger(learner.Step)) {
                            const at = new Date(measuredAt).toISOString();
                            for (let i = changes.length - 1; i >= 0 && changes[i].at === at; i--) {
                                changes[i].step = learner.Step;
                            }
                        }
                    }
                    save();
                }
            }
            return json(res, 200, workerSnapshot(status));
        }
        if (req.method === 'GET' && route === '/api/benchmarks') {
            return json(res, 200, { reports: benchmarkReports() });
        }
        if (req.method === 'GET' && route === '/api/queued') {
            const item = state.items.find(x => x.id === url.searchParams.get('id'));
            if (!canEditQueued(item)) {
                return json(res, 409, { error: 'This experiment is no longer queued and editable.' });
            }
            return json(res, 200, {
                id: item.id, revision: item.edit_revision || 0,
                defaults_sha256: queuedDefaultsHash(item),
                definition: editableDefinition(item),
            });
        }
        if (req.method !== 'POST') return json(res, 404, { error: 'Not found.' });
        const value = await body(req);
        if (route === '/api/worker-envs') {
            const target = value.envs;
            const trainerId = String(value.trainer_id || '');
            if (!/^[A-Za-z0-9._-]{1,128}$/.test(trainerId) ||
                trainerId === 'central-learner' ||
                (target !== null && (!Number.isInteger(target) || target < 1 || target > 64))) {
                return json(res, 400, { error: 'Select a remote worker and a valid environment count.' });
            }
            const before = await control();
            const desired = before.desired || {};
            if (value.expected_revision !== desired.revision ||
                value.expected_run_id !== desired.run_id) {
                return json(res, 409, { error: 'Training control changed. Refresh worker status first.' });
            }
            if (desired.training_enabled && value.confirm_active !== true) {
                return json(res, 409, { error: 'Confirm the performance impact before resizing active workers.' });
            }
            const record = (before.trainers || []).find(w =>
                w.trainer_id === trainerId && w.role === 'dedicated' && !w.stale);
            if (!record || desired.pending_release) {
                return json(res, 409, { error: 'Remote worker is unavailable or a release is pending.' });
            }
            const token = fs.readFileSync(paths.adminTokenPath, 'utf8').trim();
            const response = await requestJson(loadConfig().controlUrl, token,
                'POST', '/v1/admin/worker-envs', {
                    trainer_id: trainerId, envs: target,
                    expected_revision: desired.revision,
                    expected_run_id: String(desired.run_id || ''),
                    allow_during_training: value.confirm_active === true,
                });
            if (desired.training_enabled && state.active_id) {
                const item = active();
                if (item && item.run_id === desired.run_id) {
                    item.capacity_changes = item.capacity_changes || [];
                    item.capacity_changes.push({
                        at: now(), trainer_id: trainerId, envs: target,
                        stage_index: item.stage_index || 0,
                    });
                    if (item.capacity_changes.length > 128) item.capacity_changes.shift();
                    save();
                }
            }
            return json(res, 200, response);
        }
        if (route === '/api/queue') {
            const item = await definition(value);
            state.items.push(item); save();
            return json(res, 201, item);
        }
        if (route === '/api/edit-queued') {
            if (busy) return json(res, 409, { error: 'Queue manager is busy; retry after refresh.' });
            const original = state.items.find(x => x.id === value.id);
            if (!canEditQueued(original)) {
                return json(res, 409, { error: 'Only experiments that have not started can be edited.' });
            }
            if (value.revision !== (original.edit_revision || 0)) {
                return json(res, 409, { error: 'This queued definition changed. Reopen it before saving.' });
            }
            if (value.defaults_sha256 !== queuedDefaultsHash(original)) {
                return json(res, 409, { error: 'Operator environment defaults changed. Reopen this queued experiment before saving.' });
            }
            busy = true; // Excludes the asynchronous launch/preflight cycle while we freeze new settings.
            try {
                const prepared = await definition(value.definition || {}, original);
                if (!canEditQueued(original)) throw Error('The selected experiment started during editing.');
                const index = state.items.indexOf(original);
                if (index < 0) throw Error('Queued experiment changed during editing.');
                const revised = {
                    ...prepared, id: original.id, created_at: original.created_at,
                    edit_revision: (original.edit_revision || 0) + 1,
                    edited_at: now(),
                };
                state.items[index] = revised; // Preserve queue position and identity.
                try { save(); }
                catch (error) { state.items[index] = original; throw error; }
                return json(res, 200, { id: revised.id, revision: revised.edit_revision });
            } finally {
                busy = false;
                if (state.armed && !state.active_id && !state.stop_requested) setImmediate(cycle);
            }
        }
        if (route === '/api/evaluate') {
            if (busy || state.active_id || state.armed) return json(res, 409, { error: 'Wait for the queue to be idle.' });
            const item = state.items.find(x => x.id === value.id);
            if (!item || !item.run_id || !['completed', 'stopped', 'ended_early'].includes(item.status)) {
                return json(res, 400, { error: 'Select a finished training run.' });
            }
            if (item.benchmark_status === 'completed') return json(res, 409, { error: 'This run already has a saved evaluation.' });
            item.benchmark = benchmarkPlan();
            item.benchmark_status = 'pending';
            delete item.benchmark_error;
            state.active_id = item.id;
            save();
            setImmediate(cycle);
            return json(res, 202, { ok: true });
        }
        if (route === '/api/pause') {
            const item = active();
            if (busy || !item || item.status !== 'running' || !state.armed || state.stop_requested) {
                return json(res, 409, { error: 'Only a stable running experiment can be paused.' });
            }
            item.pause_started_at = now();
            item.status = 'pausing';
            state.armed = false; // Never launch the next queued run during maintenance.
            state.message = 'Gracefully stopping ' + item.name + ' for maintenance…';
            save();
            setImmediate(cycle);
            return json(res, 202, { ok: true });
        }
        if (route === '/api/resume') {
            const item = active();
            if (busy || !item || item.status !== 'paused' || state.stop_requested ||
                state.armed) {
                return json(res, 409, { error: 'Only a confirmed paused experiment can be resumed.' });
            }
            item.status = 'resuming';
            item.resume_attempted = false;
            state.message = 'Verifying saved run and resuming ' + item.name + '…';
            save();
            setImmediate(cycle);
            return json(res, 202, { ok: true });
        }
        if (route === '/api/start') {
            if (['paused', 'pausing', 'resuming'].includes(active()?.status)) {
                return json(res, 409, { error: 'Maintenance pause owns the queue. Use Resume.' });
            }
            if (!state.active_id) {
                const snapshot = await control();
                if (!snapshot.desired || snapshot.desired.training_enabled ||
                    snapshot.desired.pending_release) {
                    return json(res, 409, { error: 'Existing training or release is active.' });
                }
            }
            if (!state.items.some(x => x.status === 'queued') &&
                !['transitioning', 'evaluating_final_stage'].includes(active()?.status)) {
                return json(res, 409, { error: 'Queue is empty.' });
            }
            state.armed = true;
            state.stop_requested = false;
            state.message = '';
            save();
            setImmediate(cycle);
            return json(res, 200, { ok: true });
        }
        if (route === '/api/stop') {
            if (['paused', 'pausing', 'resuming'].includes(active()?.status)) {
                return json(res, 409, { error: 'Finish maintenance resume before ending this experiment.' });
            }
            state.armed = false;
            state.stop_requested = Boolean(state.active_id);
            state.message = state.active_id ? 'Graceful stop requested.' : 'Queue disarmed.';
            save(); setImmediate(cycle);
            return json(res, 200, { ok: true });
        }
        return json(res, 404, { error: 'Not found.' });
    } catch (err) {
        return json(res, 400, { error: err.message });
    }
});
if (require.main === module) {
    server.listen(PORT, HOST, () => {
        console.log('Bees experiments: http://' + HOST + ':' + PORT);
        console.log('Records: ' + DB);
        setImmediate(cycle);
        setInterval(cycle, 30000);
    });
}
module.exports = { server, definition, measure, workerSnapshot, benchmarkReports, benchmarkPlan, verifyBenchmarkPlan };
