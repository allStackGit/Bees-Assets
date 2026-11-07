'use strict';
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { invokeStart, invokeStop, invokeBuild, invokeWorkerRuntime } = require('./operator/commands');
const { invokeMaintenanceUnityRebuild } = require('./operator/build');
const { summarize: summarizeResearch, differences: compareResearch,
    sampleEfficiency: researchEfficiency } = require('./bees_experiment_research');
const { freezePlan: freezeEvaluator, supportsPurpose: evaluatorSupportsPurpose } =
    require('./bees_experiment_evaluator_versions');
const { selectedRoot, copyAndSelect } = require('./bees_experiment_storage');
const { normalizeTarget, scoreTarget } = require('./bees_experiment_benchmark_target');
const { ExperimentHub } = require('./experiment_hub_core');
const { createAgentServer, AGENT_PORT, AGENT_HOST } =
    require('./experiment_hub_agent_listener');
const { route: hubAdminRoute } = require('./experiment_hub_admin');
const { append: appendMetricSample, plot: archivedMetricsChart,
    filename: metricArchivePath } = require('./bees_experiment_metrics');
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
const DATA_ROOT = selectedRoot(paths.trainingRoot);
const DB = path.join(DATA_ROOT, 'experiments-v1.json');
const HTML = path.join(__dirname, 'experiments.html');
const LOG_EXPORTER = path.join(__dirname, 'bees_experiment_logs.py');
const RESEARCH_EXPORTER = path.join(__dirname, 'bees_experiment_research_export.py');
const BENCHMARK_REPORTS = path.join(DATA_ROOT, 'Benchmarks');
const empty = () => ({ version: 1, armed: false, stop_requested: false, active_id: null, message: '', items: [] });
const state = fs.existsSync(DB) ? readJson(DB) : empty();
// The Hub's own registry must never prevent the existing Bees coordinator from
// recovering an active experiment if its optional registry becomes invalid.
let hub = null, hubInitializationError = null;
try { hub = new ExperimentHub(DATA_ROOT); }
catch (error) {
    hubInitializationError = error.message;
    console.error('[hub] separate project registry unavailable:', error.message);
}
function requireHub() {
    if (!hub) throw Error('Experiment Hub unavailable: ' + hubInitializationError);
    return hub;
}

if (!state || state.version !== 1 || !Array.isArray(state.items) || typeof state.armed !== 'boolean') {
    throw Error('Invalid experiment data; no training started: ' + DB);
}
if (typeof state.stop_requested !== 'boolean') state.stop_requested = false;
// Poll-to-poll training time is observable only while this coordinator runs.
// After process restart, discard persisted interval baselines so downtime is
// never incorrectly charged as training or PPO idle time.
for (const item of state.items) delete item.performance_clock;
let busy = false;
let rebuildProgress = null;
let logExportBusy = false;
let storageMigrationPending = false;
let hubOperatorOperation = null;
function runHubOperatorOperation(kind, execute) {
    if (hubOperatorOperation?.status === 'running' || busy)
        throw Error('Another training operation is in progress.');
    const op = {kind, status:'running', started_at:now(), message:'Starting ' + kind};
    hubOperatorOperation = op;
    busy = true;
    setImmediate(async () => {
        try {
            await execute();
            op.status = 'completed';
            op.message = kind + ' completed. Check the live training status.';
        } catch (error) {
            op.status = 'failed';
            op.message = error.message;
            console.error('[hub operator]', kind, error.stack || String(error));
        } finally {
            op.finished_at = now();
            busy = false;
        }
    });
    return {...op};
}

const logExportSessions = new Map();
let lastRoutineSaveMs = 0;
const save = () => { ensureDir(path.dirname(DB)); writeJsonAtomic(DB, state); };
const active = () => state.items.find(x => x.id === state.active_id);
const matches = (a, b) => JSON.stringify(a || []) === JSON.stringify(b || []);
const now = () => new Date().toISOString();
// Step counts are JSON numbers; reject unsafe integers instead of silently
// rounding learner-step boundaries. No arbitrary experiment-duration cap.
const CURRICULUM_BUILTIN = path.join(__dirname, 'experiment-curriculum.json');
const CURRICULUM_SAVED = path.join(DATA_ROOT, 'curriculum-default.json');

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
    const firstOverrides = overridesForStage(input.env_args, firstName);
    if (input.scenario_seed !== undefined && input.scenario_seed !== null &&
        input.scenario_seed !== '') {
        const seed = Number(input.scenario_seed);
        if (!Number.isSafeInteger(seed) || seed < 0 || seed > 2147483647) {
            throw Error('Scenario root seed must be an integer from 0 to 2147483647.');
        }
        const flag = '--rl-scenario-seed=';
        const previous = firstOverrides.find(arg => arg.startsWith(flag));
        if (previous && previous !== flag + seed) {
            throw Error('Conflicting --rl-scenario-seed in environment arguments.');
        }
        if (!previous) firstOverrides.push(flag + seed);
    }
    let args = mergeEnvironmentArgs(defaults, firstOverrides);
    const stages = [{
        name: firstName, end_step: firstSteps, env_args: args,
        timeout_target_pct: timeoutTarget(input.target_timeout_pct, firstName),
    }];
    const additions = input.stages == null ? [] : input.stages;
    if (!Array.isArray(additions)) {
        throw Error('Curriculum stages must be an array.');
    }
    let end = firstSteps;
    for (const [index, stage] of additions.entries()) {
        if (!stage || typeof stage !== 'object' || Array.isArray(stage)) {
            throw Error('Invalid stage ' + (index + 2) + '.');
        }
        const name = String(stage.name || '').trim();
        const steps = Number(stage.steps);
        if (!name || name.length > 100 || !Number.isSafeInteger(steps) || steps < 1000 ||
            !Number.isSafeInteger(end + steps)) {
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

function curriculumDefault() {
    const template = readJson(fs.existsSync(CURRICULUM_SAVED)
        ? CURRICULUM_SAVED : CURRICULUM_BUILTIN);
    validateCurriculumTemplate(template);
    return template;
}

function validateCurriculumTemplate(template) {
    if (!template || template.schema_version !== 1 ||
        typeof template.name !== 'string' || !template.name.trim() ||
        template.name.length > 100 ||
        !Number.isSafeInteger(template.first_steps) || template.first_steps < 1000) {
        throw Error('Curriculum default is invalid.');
    }
    stagesForDefinition({
        stage_name: template.stage_name, env_args: template.env_args,
        target_timeout_pct: template.target_timeout_pct,
        stages: template.stages,
    }, [], template.first_steps);
}

// Four equal quarter-run checkpoints or every 100M steps, whichever is denser.
// Unlimited runs have no percentage denominator and use 100M steps.
function periodicEvaluationTarget(item, ordinal) {
    if (item.evaluation_mode !== 'periodic' || !item.stage_benchmarks ||
        !Number.isSafeInteger(ordinal) || ordinal < 1) return null;
    const total = item.max_steps;
    if (total != null && total <= 400_000_000) {
        return ordinal <= 4 ? Math.ceil(total / 4 * ordinal) : null;
    }
    const milestone = ordinal * 100_000_000;
    if (!Number.isSafeInteger(milestone)) return null;
    if (total == null || milestone < total) return milestone;
    return ordinal === Math.ceil(total / 100_000_000) ? total : null;
}
function periodicEvaluationDue(item) {
    const step = periodicEvaluationTarget(item, item.checkpoint_next_index || 1);
    return step != null && Number.isSafeInteger(item.step) && item.step >= step;
}
function stageSuiteAtBoundary(item) {
    return Boolean(item.stage_benchmarks && item.evaluation_mode !== 'periodic');
}
function periodicCheckpointRecord(item) {
    const ordinal = item.checkpoint_next_index || 1;
    const target = periodicEvaluationTarget(item, ordinal);
    if (target == null || item.step == null || item.step < target) return null;
    item.checkpoint_history = item.checkpoint_history || [];
    let record = item.checkpoint_history.find(entry => entry.ordinal === ordinal);
    if (!record) {
        record = {
            ordinal, target_step: target, stage_index: item.stage_index || 0,
            started_at: now(),
        };
        item.checkpoint_history.push(record);
        save();
    } else if (record.target_step !== target) {
        throw Error('Saved checkpoint schedule differs from this experiment.');
    }
    return record;
}
async function evaluateDuePeriodic(item, status) {
    const record = periodicCheckpointRecord(item);
    if (!record) return true;
    if (!(await evaluateStageSuite(item, record.stage_index, status, record))) return false;
    if (item.benchmark_target) evaluatePeriodicTarget(item, record);
    record.finished_at = record.finished_at || now();
    record.actual_step = record.evaluation?.boundary_step ?? item.step;
    record.training_s = trainingSeconds(item);
    item.checkpoint_next_index = record.ordinal + 1;
    // Avoid running multiple evaluations of the same network if a learner
    // status update overshot several intended checkpoint thresholds.
    while (periodicEvaluationTarget(item, item.checkpoint_next_index) != null &&
        item.step >= periodicEvaluationTarget(item, item.checkpoint_next_index)) {
        item.checkpoint_next_index++;
    }
    save();
    return true;
}

// The pinned evaluator already verified each report before persisting it.
// Recheck frozen result identity and current bytes whenever a development
// checkpoint contributes to an automatic stopping decision.
function targetCheckpointEvidence(item,checkpoint,target,plan) {
    const evaluation=checkpoint.evaluation;
    if(!evaluation||evaluation.status!=='completed')
        throw Error('Benchmark target needs a completed frozen checkpoint evaluation.');
    const candidate=evaluation.candidate;
    const reportHashes={};
    const reports=plan.scenarios.map((scenario,i)=>{
        const name=evaluation.reports?.[scenario.id];
        if(typeof name!=='string'||
            !/^\d{8}T\d{6}-[a-f0-9]{32}\.json$/.test(name))
            throw Error('Benchmark target is missing a frozen scenario report.');
        const identity=stageEvaluationKey(item,checkpoint.stage_index,
            scenario,i,candidate,plan,checkpoint.target_step,'development');
        const file=path.join(BENCHMARK_REPORTS,name);
        // Score and SHA-256 must describe the exact same immutable bytes.
        // One read also avoids hashing each report twice per verification.
        const bytes=fs.readFileSync(file);
        const report=JSON.parse(bytes.toString('utf8'));
        reportHashes[scenario.id]=crypto.createHash('sha256')
            .update(bytes).digest('hex');
        if(!stageReportMatches(report,item,checkpoint.stage_index,
            scenario,candidate,plan,identity,'development'))
            throw Error('Benchmark target report provenance changed.');
        const recipe=readJson(scenario.recipe);
        if(report.benchmark?.matches!==recipe.matches||
            report.benchmark?.seed!==recipe.seed||
            report.benchmark?.behavior_name!==recipe.behavior_name||
            JSON.stringify(report.benchmark?.env_args)!==
                JSON.stringify(recipe.env_args)||
            (report.benchmark?.time_scale??null)!==
                (recipe.time_scale??null))
            throw Error('Benchmark target report settings differ from frozen recipe.');
        return {id:scenario.id,expected_matches:recipe.matches,report};
    });
    return {...scoreTarget(target,reports),report_sha256:reportHashes};
}

function priorCheckpoint(item,ordinal) {
    // Skipped scheduler milestones are not performed evaluations.
    return (item.checkpoint_history||[])
        .filter(record=>record.ordinal<ordinal)
        .reduce((latest,record)=>!latest||record.ordinal>latest.ordinal
            ?record:latest,null);
}

function verifyPriorTargetPasses(item,checkpoint,target,plan) {
    let cursor=priorCheckpoint(item,checkpoint.ordinal);
    const priorCount=cursor?.benchmark_target_result?.consecutive_passes||0;
    if(!Number.isSafeInteger(priorCount)||priorCount<0||
        priorCount>target.consecutive_passes)
        throw Error('Saved benchmark target pass count is invalid.');
    // At most three prior evaluations (the configured target is 1–4).
    // Verify contributing immutable reports, not just a saved pass flag.
    const required=Math.min(priorCount,target.consecutive_passes-1);
    for(let offset=0;offset<required;offset++) {
        const saved=cursor?.benchmark_target_result;
        if(!saved?.passed||saved.consecutive_passes!==priorCount-offset)
            throw Error('Consecutive benchmark evidence history is inconsistent.');
        const evidence=targetCheckpointEvidence(item,cursor,target,plan);
        const {report_sha256,...scores}=evidence;
        const expected={...scores,consecutive_passes:saved.consecutive_passes,
            report_sha256};
        if(JSON.stringify(saved)!==JSON.stringify(expected))
            throw Error('Earlier benchmark target evidence changed.');
        cursor=priorCheckpoint(item,cursor.ordinal);
    }
    return priorCount;
}

function evaluatePeriodicTarget(item, checkpoint) {
    const target=item.benchmark_target;
    if(!target)throw Error('Missing frozen benchmark target.');
    const plan=item.stage_benchmarks;
    verifyBenchmarkPlan(plan);
    // The current candidate must still be present and match its original
    // pinned hash. Historic already-evaluated candidates need not remain
    // available, but their immutable verified reports must.
    verifyStageCandidate(checkpoint.evaluation?.candidate,
        Math.max(0,checkpoint.evaluation?.boundary_step||0));
    const evidence=targetCheckpointEvidence(item,checkpoint,target,plan);
    const priorCount=evidence.passed
        ?verifyPriorTargetPasses(item,checkpoint,target,plan):0;
    const {report_sha256,...scores}=evidence;
    const consecutive=evidence.passed?priorCount+1:0;
    const decision={...scores,consecutive_passes:consecutive,
        report_sha256};
    if(checkpoint.benchmark_target_result) {
        if(JSON.stringify(checkpoint.benchmark_target_result)!==
            JSON.stringify(decision))
            throw Error('Previously saved benchmark target decision or evidence changed.');
    }else checkpoint.benchmark_target_result=decision;
    if(consecutive>=target.consecutive_passes&&!item.benchmark_target_met)
        item.benchmark_target_met={
            checkpoint_ordinal:checkpoint.ordinal,at:now(),
            actual_step:checkpoint.evaluation.boundary_step,
        };
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

function measure(item, status, force = false, boundary = '') {
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
    if (boundary && samples.length) {
        const latest = samples[samples.length - 1];
        // The regular minute sample can occur on this exact same control poll.
        // Label it instead of duplicating a reward in rolling averages.
        if (latest.step === step && step != null &&
            Date.now() - Date.parse(latest.at) < 1000) {
            latest.sample_context = boundary;
            return;
        }
    }
    // A resumed learner can take time to emit its first fresh ML-Agents
    // summary. Never store a null/stale point that consumes the one-minute
    // sample slot just as the stage resumes.
    const resumeBoundary = item.awaiting_resume_sample_step;
    const resumed = resumeBoundary == null ||
        (step != null && step > resumeBoundary);
    const due = force || !samples.length ||
        Date.now() - Date.parse(samples[samples.length - 1].at) >= 60000 ||
        (resumeBoundary != null && resumed);
    if (due && step != null && resumed) {
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
            ...(boundary || resumeBoundary != null
                ? { sample_context: boundary ||
                    (item.stage_benchmarks ? 'post-evaluation' : 'post-transition') } : {}),
            training_s: trainingSeconds(item),
            stage_training_s: perf.training_s,
            effective_steps_per_second: perf.training_s > 0
                ? perf.steps / perf.training_s : null,
            ppo_busy_pct: perf.busy_observed_s > 0
                ? 100 * perf.optimizer_busy_s / perf.busy_observed_s : null,
            reward: Number.isFinite(learner.MeanReward) ? learner.MeanReward : null,
            group_reward: Number.isFinite(learner.MeanGroupReward) ? learner.MeanGroupReward : null,
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
        try {
            appendMetricSample(DATA_ROOT, item);
            delete item.metric_archive_error;
        } catch (error) {
            // A measurement-storage failure must be visible but must not
            // terminate an otherwise healthy distributed training run.
            item.metric_archive_error = error.message;
        }
        delete item.awaiting_resume_sample_step;
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
        // Retain original minute-resolution samples. Halving all samples
        // every time the 500-entry threshold was crossed repeatedly thinned
        // early training history into multi-million-step gaps.
        // This much larger fixed cap keeps weeks of data per experiment
        // without exponential loss of historical resolution.
        if (samples.length > 20000) samples.shift();
    }
}

const BENCHMARK_SETTINGS = path.join(DATA_ROOT, 'benchmark-settings.json');
const HELDOUT_BENCHMARK_SETTINGS = path.join(DATA_ROOT, 'heldout-benchmark-settings.json');
const BENCHMARK_RUNNER = path.join(__dirname, 'bees_experiment_benchmark.py');
const SCRIPTED_OPPONENTS = path.join(__dirname, 'bees_experiment_scripted.py');
const SCRIPTED_NAMES = new Set(['stationary', 'strafe', 'pursuit']);
const hashFile = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

function benchmarkPlan(settingsPath = BENCHMARK_SETTINGS) {
    const settings = readJson(settingsPath);
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
            benchmark_revision: recipeSpec.revision, seed: recipeSpec.seed,
            opponent, opponent_kind,
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
    return freezeEvaluator({
        python: path.resolve(settings.python), build_id: settings.build_id.trim(),
        files, hashes, scenarios,
    }, path.join(DATA_ROOT, 'EvaluatorVersions'));
}

function verifyBenchmarkPlan(plan) {
    freezeEvaluator(plan, path.join(DATA_ROOT, 'EvaluatorVersions'));
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
        '--output', path.join(DATA_ROOT, 'Configurations')]);
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
        const args = [plan.files.runner, '--benchmark', plan.files.recipe,
            '--candidate-run', item.run_id,
            '--registry', path.join(paths.trainingRoot, 'registry.sqlite3'),
            '--opponent', plan.scenarios?.[0]?.opponent || plan.files.opponent, '--env', plan.files.environment,
            '--build-id', plan.build_id, '--output-root', BENCHMARK_REPORTS];
        const output = await new Promise((resolve, reject) => {
            execFile(plan.python, args, { windowsHide: true, maxBuffer: 65536,
                env: { ...process.env, PYTHONPATH: [__dirname,
                    process.env.PYTHONPATH || ''].filter(Boolean).join(path.delimiter) } },
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
        execFile(python, args, { windowsHide: true, maxBuffer: 65536,
            env: { ...process.env, PYTHONPATH: [__dirname,
                process.env.PYTHONPATH || ''].filter(Boolean).join(path.delimiter) } },
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

function stageEvaluationKey(item, index, scenario, scenarioIndex, candidate, plan,
    checkpointTarget = null, purpose = 'development') {
    const identity = [
        item.id, item.run_id, index, scenario.id,
        plan.hashes['recipe_' + scenarioIndex],
        scenario.opponent_sha256 || plan.hashes.opponent,
        plan.hashes.environment, plan.hashes.evaluator, plan.hashes.runner,
        plan.build_id, candidate.sha256, candidate.training_step,
    ];
    if (checkpointTarget != null) identity.push(checkpointTarget);
    if (purpose !== 'development') identity.push(purpose);
    return crypto.createHash('sha256').update(JSON.stringify(identity)).digest('hex');
}

function stageReportMatches(report, item, index, scenario, candidate, plan,
    evaluationId, purpose = 'development') {
    return report && report.schema_version === 1 &&
        (report.evaluation_purpose || 'development') === purpose &&
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

function findStageReport(item, index, scenario, candidate, plan, evaluationId, savedName,
    purpose = 'development') {
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
        if (stageReportMatches(report, item, index, scenario, candidate, plan, evaluationId,
            purpose)) {
            return name;
        }
        if (name === savedName) {
            throw Error('Previously recorded stage benchmark no longer matches its frozen provenance.');
        }
    }
    return null;
}

async function evaluateStageSuite(item, index, status, checkpointRecord = null,
    planOverride = null, purpose = 'development') {
    const plan = planOverride || item.stage_benchmarks;
    if (!plan) return true;
    const record = checkpointRecord || stageRecord(item, index);
    try {
        if (!status.desired || status.desired.training_enabled ||
            status.desired.pending_release ||
            (purpose !== 'heldout' &&
                (status.desired.run_id !== item.run_id ||
                 status.desired.canonical_build_id !== item.release_build))) {
            throw Error('Stage benchmark requires a stopped, unchanged training run.');
        }
        verifyTrainerSnapshot(item);
        verifyBenchmarkPlan(plan);
        if (!record.evaluation) {
            const result = await execBenchmark(plan.python, [
                plan.files.runner, '--resolve-candidate-run', item.run_id,
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
                item, index, scenario, scenarioIndex, evaluation.candidate, plan,
                checkpointRecord?.target_step ?? null, purpose);
            let reportName = findStageReport(item, index, scenario, evaluation.candidate,
                plan, evaluationId, evaluation.reports[scenario.id], purpose);
            if (!reportName) {
                const label = purpose === 'heldout'
                    ? 'Held-out validation'
                    : checkpointRecord
                        ? 'Checkpoint ' + checkpointRecord.ordinal + ' (' +
                            checkpointRecord.target_step.toLocaleString() + ' steps)'
                        : 'Stage ' + (index + 1);
                state.message = label + ' evaluation: ' + scenario.id +
                    ' for ' + item.name;
                save();
                const output = await execBenchmark(plan.python, [
                    plan.files.runner, '--benchmark', scenario.recipe,
                    '--candidate', evaluation.candidate.path,
                    '--candidate-sha256', evaluation.candidate.sha256,
                    '--candidate-training-step', String(evaluation.candidate.training_step),
                    '--run-id', item.run_id, '--experiment-id', item.id,
                    '--stage-index', String(index), '--stage-name', item.stages[index].name,
                    '--stage-step', String(evaluation.boundary_step),
                    '--evaluation-id', evaluationId,
                    ...(evaluatorSupportsPurpose(plan) ? ['--evaluation-purpose', purpose] : []),
                    '--opponent', scenario.opponent || plan.files.opponent, '--env', plan.files.environment,
                    '--build-id', plan.build_id, '--output-root', BENCHMARK_REPORTS,
                ]);
                reportName = path.basename(output.split(/\r?\n/).pop());
                if (!findStageReport(item, index, scenario, evaluation.candidate,
                    plan, evaluationId, reportName, purpose)) {
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
        state.message = (checkpointRecord ? 'Checkpoint' : 'Stage') +
            ' evaluation failed; queue disarmed: ' + error.message;
        save();
        return false;
    }
}

// Opt-in final validation never runs during training or while the queue is
// armed. Reports remain immutable and use a separately frozen benchmark plan.
// An interrupted run reuses its exact pinned candidate and finished reports.
async function evaluateHeldout(item) {
    const record = item.heldout_validation;
    if (!record || !['pending', 'running'].includes(record.status)) return;
    try {
        const status = await control();
        if (!status.desired || status.desired.training_enabled ||
            status.desired.pending_release || state.armed) {
            throw Error('Held-out evaluation requires idle training with no pending release.');
        }
        verifyBenchmarkPlan(item.heldout_benchmarks);
        record.status = 'running';
        save();
        const done = await evaluateStageSuite(item, record.stage_index, status,
            record, item.heldout_benchmarks, 'heldout');
        if (done) {
            record.status = 'completed';
            record.finished_at = now();
            delete record.error;
            state.message = 'Held-out validation completed for ' + item.name;
        } else {
            record.status = 'failed';
            record.error = item.stage_evaluation_error || 'Held-out evaluation failed.';
        }
    } catch (error) {
        record.status = 'failed';
        record.error = error.message;
        state.message = 'Held-out validation failed: ' + error.message;
    } finally {
        state.active_id = null;
        state.stop_requested = false;
        save();
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
    if (!(item.evaluation_mode === 'periodic'
        ? await evaluateDuePeriodic(item, status)
        : await evaluateStageSuite(item, item.stage_index || 0, status))) return;
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


function recordUnityMaintenanceBuild(item, release, status) {
    const attempt = item.unity_rebuild_attempt;
    if (!attempt) return false;
    if (!release || !release.unity_maintenance ||
        release.unity_maintenance.previous_build_id !== attempt.old_build ||
        release.run_id !== item.run_id ||
        release.compatibility_key !== attempt.compatibility_key ||
        release.training_runtime?.runtime_version !== item.trainer_config.runtime_version ||
        release.experiment_config?.sha256 !== item.trainer_config.sha256 ||
        status?.desired?.training_enabled || status?.desired?.pending_release ||
        status?.desired?.canonical_build_id !== release.build_id ||
        status?.desired?.run_id !== item.run_id) return false;
    item.unity_rebuilds = item.unity_rebuilds || [];
    if (!item.unity_rebuilds.some(record => record.build_id === release.build_id)) {
        item.unity_rebuilds.push({
            at: now(), step: item.step, training_s: trainingSeconds(item),
            old_build_id: attempt.old_build, build_id: release.build_id,
            compatibility_key: release.compatibility_key,
            warning_acknowledged: true,
        });
    }
    item.release_build = release.build_id;
    item.build_id = release.build_id;
    delete item.unity_rebuild_attempt;
    save();
    return true;
}

// If compilation failed before the new release was staged, the old release
// remains canonical. Allow an operator retry or same-run resume; never clear
// an ambiguous attempt after partial promotion.
function clearUnpublishedUnityRebuild(item, release, status) {
    const attempt = item.unity_rebuild_attempt;
    if (!attempt || attempt.requires_manual_repair || !release || !status?.desired) return false;
    if (release.build_id !== attempt.old_build ||
        release.run_id !== item.run_id ||
        release.training_runtime?.runtime_version !== item.trainer_config?.runtime_version ||
        release.experiment_config?.sha256 !== item.trainer_config?.sha256 ||
        status.desired.training_enabled || status.desired.pending_release ||
        status.desired.run_id !== item.run_id ||
        status.desired.canonical_build_id !== attempt.old_build) return false;
    delete item.unity_rebuild_attempt;
    save();
    return true;
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
    if (item.unity_rebuild_attempt) {
        const candidate = readJson(paths.latestReleasePath);
        if (!recordUnityMaintenanceBuild(item, candidate, status) &&
            !clearUnpublishedUnityRebuild(item, candidate, status)) {
            throw Error('An interrupted Unity maintenance rebuild has not reached a verified canonical release. Remain paused and inspect the rebuild.');
        }
    }
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

// Pause the learner at a fixed periodic checkpoint without advancing the
// curriculum. The durable status, pinned ONNX candidate and immutable reports
// allow a coordinator restart to resume the same operation safely.
// Both a periodic pause and a curriculum boundary may evaluate the same
// frozen checkpoint. Never let the stage-transition path resume a model
// after a verified opt-in completion target has already been reached.
function completeBenchmarkTarget(item) {
    if(!item.benchmark_target_met)return false;
    const winning=(item.checkpoint_history||[]).find(record=>
        record.ordinal===item.benchmark_target_met.checkpoint_ordinal);
    // The scheduler may skip multiple milestones after evaluating one
    // overshot checkpoint. Its next ordinal is not the evaluation's ID.
    // Require the latest completed evaluation on this curriculum stage,
    // rather than inferring an ID from the next scheduled checkpoint.
    const latestCompleted=(item.checkpoint_history||[])
        .filter(record=>record.stage_index===(item.stage_index||0)&&
            record.evaluation?.status==='completed')
        .reduce((latest,record)=>!latest||record.ordinal>latest.ordinal
            ?record:latest,null);
    const expected=item.status==='checkpointing'
        ?item.checkpoint_active_ordinal
        :item.status==='transitioning'?latestCompleted?.ordinal:null;
    if(!winning||winning.ordinal!==expected||
        winning.stage_index!==(item.stage_index||0))
        throw Error('Benchmark completion checkpoint lineage changed.');
    // A saved success flag cannot authorize completion after restart:
    // reverify exact report bytes and candidate/recipe provenance.
    evaluatePeriodicTarget(item,winning);
    if(!winning.benchmark_target_result?.passed||
        winning.benchmark_target_result.consecutive_passes<
            item.benchmark_target.consecutive_passes)
        throw Error('Frozen benchmark evidence no longer supports completion.');
    assertSameLineageCheckpoint(item);
    const stage=stageRecord(item);
    stage.finished_at=stage.finished_at||now();
    stage.finished_step=item.step;
    item.end_reason='benchmark_target';
    item.status='completed';
    item.finished_at=now();
    state.active_id=null;
    state.stop_requested=false;
    delete item.checkpoint_active_ordinal;
    delete item.checkpoint_resume_pending;
    state.message='Completed '+item.name+
        ' after meeting its frozen development benchmark target.';
    save();
    return true;
}

async function checkpointPeriodic(item, status) {
    const d = status?.desired || {};
    if (d.run_id !== item.run_id || d.pending_release ||
        String(d.canonical_build_id || '') !== item.release_build ||
        !matches(d.environment_args, item.env_args)) {
        blocked(item, 'Periodic checkpoint lost run, build or environment ownership.');
        return;
    }
    if (!state.armed && !state.stop_requested) return;
    if (state.stop_requested || !state.armed) {
        item.end_reason = 'manual';
        item.status = 'stopping';
        save();
        await finalize(item, status);
        return;
    }
    verifyTrainerSnapshot(item);
    const activeOrdinal = item.checkpoint_active_ordinal;
    const record = (item.checkpoint_history || []).find(
        entry => entry.ordinal === activeOrdinal);
    const completed = record?.evaluation?.status === 'completed' &&
        activeOrdinal < (item.checkpoint_next_index || 1);
    if (d.training_enabled && !item.checkpoint_resume_pending) {
        if (completed) {
            blocked(item, 'Training restarted after checkpoint evaluation without a verified manager resume.');
            return;
        }
        await invokeStop({});
        status = await control();
        if (status.desired?.training_enabled || status.desired?.run_id !== item.run_id ||
            status.desired?.pending_release ||
            !matches(status.desired?.environment_args, item.env_args)) {
            throw Error('Safe periodic checkpoint stop was not confirmed.');
        }
        measure(item, status, true, 'pre-evaluation');
        save();
    }
    if (!status.desired || status.desired.run_id !== item.run_id ||
        status.desired.pending_release ||
        String(status.desired.canonical_build_id || '') !== item.release_build ||
        !matches(status.desired.environment_args, item.env_args)) {
        throw Error('Periodic checkpoint control ownership changed.');
    }
    if (!status.desired.training_enabled) {
        assertSameLineageCheckpoint(item);
        if (!completed && !(await evaluateDuePeriodic(item, status))) return;
        if (state.stop_requested || !state.armed) {
            item.end_reason = 'manual';
            item.status = 'stopping';
            save();
            await finalize(item, status);
            return;
        }
        if(completeBenchmarkTarget(item))return;
        assertSameLineageCheckpoint(item);
        item.awaiting_resume_sample_step = item.step ?? 0;
        item.checkpoint_resume_pending = true;
        save();
        await invokeStart({ envArgs: item.env_args });
        status = await control();
    } else if (!item.checkpoint_resume_pending || !completed) {
        blocked(item, 'Periodic checkpoint resumed before its evaluation was verified.');
        return;
    }
    if (!status.desired || !status.desired.training_enabled ||
        status.desired.run_id !== item.run_id ||
        status.desired.pending_release ||
        String(status.desired.canonical_build_id || '') !== item.release_build ||
        !matches(status.desired.environment_args, item.env_args)) {
        throw Error('Same-run resume after periodic checkpoint was not confirmed.');
    }
    delete item.checkpoint_resume_pending;
    delete item.checkpoint_active_ordinal;
    item.status = 'running';
    item.last_progress = now();
    state.message = 'Completed periodic checkpoint and resumed ' + item.name +
        ' on the same curriculum stage and optimizer.';
    save();
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
    if (item.benchmark_target_met && d.training_enabled) {
        // Recovery may observe an external restart into the next stage
        // after a verified benchmark target was met but before completion
        // was recorded. Do not silently accept that restart as a stage
        // transition or take ownership of the external process.
        blocked(item, 'Benchmark completion target was met, but training restarted before completion. Queue disarmed; inspect and safely stop the active run before recovery.');
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
    if (matches(previous.env_args, next.env_args) &&
        !stageSuiteAtBoundary(item) && !periodicEvaluationDue(item)) {
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
        // The learner may emit one final summary during graceful shutdown.
        // Keep it if it proves real progress beyond the pre-stop snapshot.
        const finalized = getAuthoritativeLearnerStats(item.run_id);
        if (Number.isSafeInteger(finalized?.Step) &&
            (item.step == null || finalized.Step > item.step)) {
            measure(item, status, true, stageSuiteAtBoundary(item) || periodicEvaluationDue(item)
                ? 'pre-evaluation' : 'pre-transition');
        }
        item.stage_stop_confirmed = true;
        const finished = stageRecord(item, index);
        if (!finished.finished_at) {
            finished.finished_at = now();
            finished.finished_step = item.step;
            finished.finished_training_s = trainingSeconds(item);
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
    if (status.desired.training_enabled &&
        matches(status.desired.environment_args, next.env_args) &&
        ((stageSuiteAtBoundary(item) &&
            stageRecord(item, index).evaluation?.status !== 'completed') ||
            periodicEvaluationDue(item))) {
        blocked(item, 'Next curriculum stage was started before its required checkpoint evaluation.');
        return;
    }
    if (!status.desired.training_enabled) {
        assertSameLineageCheckpoint(item);
        if (stageSuiteAtBoundary(item) && !(await evaluateStageSuite(item, index, status))) return;
        if (periodicEvaluationDue(item) && !(await evaluateDuePeriodic(item, status))) return;
        if (state.stop_requested || !state.armed) {
            item.end_reason = 'manual';
            item.status = 'stopping';
            save();
            await finalize(item, status);
            return;
        }
        if(completeBenchmarkTarget(item))return;
        assertSameLineageCheckpoint(item);
        // After evaluation/restart the previous live log can still contain
        // the outgoing learner's last summary. Require genuine new progress
        // before adding the first point of the resumed stage.
        item.awaiting_resume_sample_step = item.step ?? 0;
        save();
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
    if (['pending', 'running'].includes(item.heldout_validation?.status)) {
        await evaluateHeldout(item);
        return;
    }
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
    if (item.status === 'checkpointing') {
        await checkpointPeriodic(item, status);
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
        item.max_steps != null && item.step != null && item.step >= item.max_steps ? 'budget' :
        item.max_minutes != null && elapsed >= item.max_minutes * 60000 ? 'time_limit' :
        item.stall_minutes != null && stagnant >= item.stall_minutes * 60000 ? 'no_progress' : '';
    if (reason) {
        item.status = 'stopping';
        item.end_reason = reason;
        state.message = 'Safely stopping ' + item.name + ' (' + reason + ')';
        save();
        await finalize(item, status);
    } else if (item.stages && item.stage_index + 1 < item.stages.length &&
        item.step != null && item.step >= item.stages[item.stage_index].end_step) {
        // The ordinary minute cadence often misses the last training window
        // when a long benchmark evaluation begins. Capture it while the
        // learner is still running, before changing the item to transitioning.
        measure(item, status, true, stageSuiteAtBoundary(item) || periodicEvaluationDue(item)
            ? 'pre-evaluation' : 'pre-transition');
        item.status = 'transitioning';
        item.stage_transition_at = now();
        state.message = 'Switching ' + item.name + ' to ' + item.stages[item.stage_index + 1].name;
        save();
        await transitionStage(item, status);
    } else if (periodicEvaluationDue(item)) {
        measure(item, status, true, 'pre-evaluation');
        item.status = 'checkpointing';
        item.checkpoint_active_ordinal = item.checkpoint_next_index || 1;
        state.message = 'Evaluating ' + item.name + ' at ' +
            periodicEvaluationTarget(item, item.checkpoint_next_index || 1).toLocaleString() +
            ' learner steps, without changing the curriculum stage.';
        save();
        await checkpointPeriodic(item, status);
    } else if (Date.now() - lastRoutineSaveMs >= 30000) {
        // A 10-second threshold observation should not triple full-history
        // JSON rewrites. Retain the previous ~30-second durable snapshot cadence.
        save();
        lastRoutineSaveMs = Date.now();
    }
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
    if (item.heldout_benchmarks) verifyBenchmarkPlan(item.heldout_benchmarks);
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
    if (busy || storageMigrationPending) return;
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
    const firstSteps = Number(input.max_steps);
    const maxMinutes = input.max_minutes == null || input.max_minutes === ''
        ? null : Number(input.max_minutes);
    const stallMinutes = input.stall_minutes == null || input.stall_minutes === ''
        ? null : Number(input.stall_minutes);
    if (!Number.isSafeInteger(firstSteps) || firstSteps < 1000)
        throw Error('First-stage steps must be a positive safe integer.');
    if (maxMinutes !== null && (!Number.isSafeInteger(maxMinutes) || maxMinutes < 1))
        throw Error('Time limit must be a positive integer in minutes or blank for unlimited.');
    if (stallMinutes !== null && (!Number.isSafeInteger(stallMinutes) || stallMinutes < 1))
        throw Error('No-progress limit must be a positive integer in minutes or blank.');
    const legacyMode = input.evaluate_stages === true ? 'each_stage'
        : input.benchmark === true ? 'final' : 'none';
    const evaluationMode = input.evaluation_mode || legacyMode;
    if (!['periodic', 'each_stage', 'final', 'none'].includes(evaluationMode))
        throw Error('Unknown evaluation schedule.');
    const benchmarkTarget=normalizeTarget(input.benchmark_target,evaluationMode);
    const config = loadConfig();
    // Edits retain the exact environment baseline frozen when the item was queued.
    // Re-reading mutable operator defaults could silently change unrelated stages.
    const defaults = original?.queue_defaults ||
        (Array.isArray(config.environmentArgs) ? config.environmentArgs.map(String) : []);
    const stages = stagesForDefinition(input, defaults, firstSteps);

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
    const benchmark = evaluationMode === 'none'
        ? null : (pinnedBenchmarks || benchmarkPlan());
    const heldoutPlan = input.reserve_heldout === true
        ? (original?.heldout_benchmarks || benchmarkPlan(HELDOUT_BENCHMARK_SETTINGS))
        : null;
    if(benchmarkTarget) {
        for(const scenario of benchmark.scenarios) {
            const recipe=readJson(scenario.recipe);
            if(!Number.isSafeInteger(recipe.matches)||
                recipe.matches<benchmarkTarget.min_matches)
                throw Error('Benchmark target requires at least '+
                    benchmarkTarget.min_matches+' frozen matches in scenario '+
                    scenario.id+'.');
        }
    }
    const developmentRecipes = new Set((benchmark?.scenarios || []).map((scenario, i) =>
        benchmark.hashes['recipe_' + i]));
    const developmentSeeds = new Set((benchmark?.scenarios || [])
        .map(scenario => scenario.seed).filter(Number.isSafeInteger));
    if (heldoutPlan && heldoutPlan.scenarios.some((scenario, i) =>
        developmentRecipes.has(heldoutPlan.hashes['recipe_' + i]) ||
        developmentSeeds.has(scenario.seed))) {
        throw Error('Held-out benchmarks must use different recipes and evaluation seeds from development.');
    }
    return {
        id: crypto.randomUUID(), name, group, status: 'queued', created_at: now(),
        max_steps: input.unlimited_steps === true ? null : stages[stages.length - 1].end_step,
        first_stage_steps: firstSteps, unlimited_steps: input.unlimited_steps === true,
        max_minutes: maxMinutes, stall_minutes: stallMinutes,
        evaluation_mode: evaluationMode,
        benchmark_target:benchmarkTarget,
        checkpoint_history: [], checkpoint_next_index: 1,
        env_args, stages, stage_index: 0, stage_history: [],
        scenario_seed: (() => {
            const arg = env_args.find(value => value.startsWith('--rl-scenario-seed='));
            return arg ? Number(arg.split('=')[1]) : null;
        })(),
        release_build: String(release.build_id), samples: [], step: null, run_id: null,
        benchmark: evaluationMode === 'final' ? benchmark : null,
        stage_benchmarks: ['periodic', 'each_stage'].includes(evaluationMode) ? benchmark : null,
        heldout_benchmarks: heldoutPlan,
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
        max_steps: item.first_stage_steps ?? stages[0].end_step,
        unlimited_steps: item.unlimited_steps === true,
        max_minutes: item.max_minutes, stall_minutes: item.stall_minutes,
        env_args: savedStageOverrides(defaults, stages[0].env_args)
            .filter(arg => !arg.startsWith('--rl-scenario-seed=')),
        scenario_seed: item.scenario_seed ?? null,
        stage_name: stages[0].name,
        target_timeout_pct: stages[0].timeout_target_pct,
        stages: stages.slice(1).map((stage, index) => ({
            name: stage.name,
            steps: stage.end_step - stages[index].end_step,
            target_timeout_pct: stage.timeout_target_pct,
            env_args: savedStageOverrides(stages[index].env_args, stage.env_args),
        })),
        evaluation_mode: item.evaluation_mode ||
            (item.stage_benchmarks ? 'each_stage' : item.benchmark ? 'final' : 'none'),
        benchmark_target:item.benchmark_target||null,
        reserve_heldout: Boolean(item.heldout_benchmarks),
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

function benchmarkReports(limit = 1000) {
    if (!fs.existsSync(BENCHMARK_REPORTS)) return [];
    // Reports are small immutable JSON summaries. Never enumerate raw training
    // data, checkpoints or unrelated files to populate the UI.
    return fs.readdirSync(BENCHMARK_REPORTS, { withFileTypes: true })
        .filter(entry => entry.isFile() &&
            /^\d{8}T\d{6}-[a-f0-9]{32}\.json$/.test(entry.name))
        .map(entry => entry.name)
        .sort().reverse().slice(0, limit)
        .map(name => {
            const report = readJson(path.join(BENCHMARK_REPORTS, name));
            return report ? { ...report, _file: name } : null;
        })
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
async function body(req, limitBytes = 5_000_000) {
    if (!/^application\/json(?:\s*;\s*charset\s*=\s*(?:"utf-8"|utf-8))?\s*$/i
        .test(String(req.headers['content-type'] || '')))
        throw Error('JSON requests only.');
    const chunks=[];
    let received=0;
    for await (const chunk of req) {
        received += chunk.length;
        if (received > limitBytes) {
            const error=Error('JSON request exceeds allowed byte limit.');
            error.status=413;
            throw error;
        }
        chunks.push(chunk);
    }
    return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(
        Buffer.concat(chunks,received))||'{}');
}


// On-demand, compact scientific evidence export. It is intentionally read-only
// and does not stop training, duplicate models, or modify frozen studies.
async function exportResearchBundle(res, item, studyId = null) {
    const directory = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'bees-study-'));
    const target = path.join(directory, 'research.zip');
    try {
        const python = resolvePython(loadConfig());
        await new Promise((resolve, reject) => {
            const args = [RESEARCH_EXPORTER, '--db', DB, '--benchmarks', BENCHMARK_REPORTS,
                '--output', target, studyId ? '--study-id' : '--experiment-id',
                studyId || item.id];
            const child = execFile(python, args, { windowsHide: true, maxBuffer: 65536 },
                (error, stdout, stderr) => error
                    ? reject(Error(String(stderr || error.message).trim()))
                    : resolve(stdout));
            res.once('close', () => child.kill());
        });
        if (res.destroyed) return;
        const stats = await fs.promises.stat(target);
        if (!stats.isFile() || !stats.size) throw Error('No research bundle was produced.');
        const name = 'bees-research-' +
            String(studyId || item.run_id).replace(/[^A-Za-z0-9._-]/g, '_') + '.zip';
        res.writeHead(200, {
            'Content-Type': 'application/zip',
            'Content-Disposition': 'attachment; filename="' + name + '"',
            'Content-Length': stats.size,
            'Cache-Control': 'no-store',
            'X-Content-Type-Options': 'nosniff',
        });
        const stream = fs.createReadStream(target);
        stream.on('error', () => res.destroy());
        stream.pipe(res);
        await new Promise(resolve => res.once('close', resolve));
    } finally {
        await fs.promises.rm(directory, { recursive: true, force: true });
    }
}

// ZIP parts are prepared off the event loop and served independently. A
// temporary download session retains them for one hour so browser download
// restrictions cannot cause silent data loss or require recompression.
const LOG_PART_SESSION_MS = 60 * 60 * 1000;
function registerLogExport(folder, parts, item, percent) {
    const id = crypto.randomBytes(16).toString('hex');
    const session = { folder, parts, runId: item.run_id, percent };
    logExportSessions.set(id, session);
    const timer = setTimeout(() => {
        if (logExportSessions.get(id) !== session) return;
        logExportSessions.delete(id);
        fs.promises.rm(folder, { recursive: true, force: true }).catch(() => {});
    }, LOG_PART_SESSION_MS);
    timer.unref();
    return id;
}

function sendLogExportPart(res, id, number) {
    const session = /^[a-f0-9]{32}$/.test(id) && logExportSessions.get(id);
    const index = Number(number);
    if (!session || !Number.isSafeInteger(index) || index < 1 ||
        index > session.parts.length) {
        return json(res, 404, { error: 'Log export expired or part not found. Export logs again.' });
    }
    const part = session.parts[index - 1];
    const archive = path.join(session.folder, part.file);
    const size = fs.statSync(archive).size;
    if (size > 500_000_000) throw Error('Log archive exceeds the 500 MB limit.');
    const filename = 'bees-' + session.runId + '-logs-last-' +
        session.percent + 'pct-part-' + String(index).padStart(4, '0') +
        '-of-' + String(session.parts.length).padStart(4, '0') + '.zip';
    res.writeHead(200, {
        'Content-Type': 'application/zip',
        'Content-Disposition': 'attachment; filename="' + filename + '"',
        'Content-Length': size,
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff',
    });
    const stream = fs.createReadStream(archive);
    stream.on('error', () => res.destroy());
    stream.pipe(res);
}

function sendLogExportIndex(res, sessionId, parts) {
    const links = parts.map((part, i) => {
        const number = i + 1;
        const href = '/api/experiment-log-part?id=' + sessionId + '&part=' + number;
        return '<li><a href="' + href + '" download>Part ' + number +
            ' of ' + parts.length + ' (' + Math.ceil(part.bytes / 1_000_000) +
            ' MB)</a></li>';
    }).join('\n');
    const page = '<!doctype html><html lang="en"><meta charset="utf-8">' +
        '<title>Bees log export</title><style>body{font:16px Arial,sans-serif;' +
        'max-width:720px;margin:40px auto;padding:20px;color:#000;background:#fff}' +
        'a{color:#000}li{padding:8px}h1{border-bottom:5px solid #000}</style>' +
        '<h1>Bees log export</h1><p>Export split into ' + parts.length +
        ' independent ZIP files, each no larger than 500 MB. Downloads will ' +
        'start automatically if your browser permits multiple downloads. ' +
        'Otherwise use the links below. Links expire in one hour. ' +
        'Each ZIP extracts separately; when a large source log has numbered ' +
        '.part00001 segments, concatenate those segments in numeric order to ' +
        'recover the complete selected log excerpt.</p><ol>' +
        links + '</ol><script>window.addEventListener("load",()=>{' +
        'document.querySelectorAll("a[download]").forEach((a,i)=>' +
        'setTimeout(()=>a.click(),i*1000));});</script></html>';
    res.writeHead(200, {
        'Content-Type': 'text/html; charset=utf-8',
        'Content-Length': Buffer.byteLength(page),
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff',
        'Content-Security-Policy': "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; base-uri 'none'",
    });
    res.end(page);
}

// One export at a time. All parts remain standalone ZIPs, not multi-volume ZIP
// pieces; files spanning ZIPs have numbered, lossless line-aligned segments.
async function exportExperimentLogs(item, res, percent) {
    if (logExportBusy) return json(res, 409, {
        error: 'Another experiment log archive is being prepared or downloaded.',
    });
    logExportBusy = true;
    let folder = null;
    try {
        folder = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'bees-experiment-logs-'));
        const archive = path.join(folder, 'logs.zip');
        const python = resolvePython(loadConfig());
        const generated = await new Promise((resolve, reject) => {
            const child = execFile(python, [
                LOG_EXPORTER, '--bees-root', paths.beesRoot,
                '--assets-root', paths.assetsRoot,
                '--run-id', item.run_id, '--output', archive,
                '--log-percent', String(percent),
            ], { windowsHide: true, maxBuffer: 262144 }, (error, stdout, stderr) => {
                if (error) return reject(Error(String(stderr || error.message).trim()));
                try { resolve(JSON.parse(stdout)); }
                catch (_) { reject(Error('Log packager did not return a valid parts index.')); }
            });
            res.once('close', () => child.kill());
        });
        if (res.destroyed) return;
        if (!Array.isArray(generated.parts) || !generated.parts.length ||
            generated.parts.some(part =>
                !/^logs(?:-part-\d{4})?\.zip$/.test(part.file) ||
                !Number.isSafeInteger(part.bytes) || part.bytes <= 0 ||
                part.bytes > 500_000_000 ||
                fs.statSync(path.join(folder, part.file)).size !== part.bytes)) {
            throw Error('Log packager returned invalid or oversized ZIP parts.');
        }
        if (generated.parts.length > 1) {
            const id = registerLogExport(folder, generated.parts, item, percent);
            folder = null; // Session owns temporary files until expiry.
            return sendLogExportIndex(res, id, generated.parts);
        }
        const single = path.join(folder, generated.parts[0].file);
        const size = generated.parts[0].bytes;
        res.writeHead(200, {
            'Content-Type': 'application/zip',
            'Content-Disposition': 'attachment; filename="bees-' +
                item.run_id + '-logs-last-' + percent + 'pct.zip"',
            'Content-Length': size,
            'Cache-Control': 'no-store',
            'X-Content-Type-Options': 'nosniff',
        });
        const stream = fs.createReadStream(single);
        stream.on('error', () => res.destroy());
        stream.pipe(res);
        await new Promise(resolve => res.once('close', resolve));
    } finally {
        if (folder) await fs.promises.rm(folder, { recursive: true, force: true });
        logExportBusy = false;
    }
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
        if (req.method === 'GET' && route === '/experiment-hub-ui.js') {
            const script = fs.readFileSync(path.join(__dirname, 'experiment_hub_ui.js'));
            res.writeHead(200, {
                'Content-Type':'text/javascript; charset=utf-8',
                'Content-Length':script.length,
                'Cache-Control':'no-store',
                'X-Content-Type-Options':'nosniff',
            });
            return res.end(script);
        }
        if (req.method === 'GET' && route === '/') {
            const page = fs.readFileSync(HTML);
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8',
                'Content-Length': page.length, 'Cache-Control': 'no-store',
                'X-Content-Type-Options': 'nosniff',
                'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' blob:; object-src 'none'; base-uri 'none'" });
            return res.end(page);
        }
        if (req.method === 'GET' && route === '/api/experiment-log-part') {
            return sendLogExportPart(res, url.searchParams.get('id'), url.searchParams.get('part'));
        }
        if (req.method === 'GET' && route === '/api/research-bundle') {
            const studyId = url.searchParams.get('study_id');
            const experimentId = url.searchParams.get('experiment_id');
            if (Boolean(studyId) === Boolean(experimentId)) {
                return json(res, 400, { error: 'Select one research study or experiment.' });
            }
            const item = state.items.find(entry => studyId
                ? entry.study_id === studyId && entry.run_id
                : entry.id === experimentId && entry.run_id);
            if (!item) return json(res, 404, { error: 'Selected research run is unavailable.' });
            return await exportResearchBundle(res, item, studyId);
        }
        if (req.method === 'GET' && route === '/api/experiment-logs') {
            const id = url.searchParams.get('experiment_id');
            const item = state.items.find(entry => entry.id === id);
            if (!item || !item.run_id ||
                !/^[A-Za-z0-9._-]{1,128}$/.test(item.run_id)) {
                return json(res, 404, { error: 'A started experiment with a valid run ID is required.' });
            }
            const percent = Number(url.searchParams.get('percent') || 5);
            if (![1, 5, 10, 25, 50, 100].includes(percent)) {
                return json(res, 400, { error: 'Select 1, 5, 10, 25, 50 or 100 percent.' });
            }
            return await exportExperimentLogs(item, res, percent);
        }
        if(req.method==='GET'&&route.startsWith('/api/hub/')&&
            !route.startsWith('/api/hub/bees/')) {
            // Only the full state endpoint needs Bees history. Keep planning
            // and monitor polling independent of its potentially large queue.
            const bees=route==='/api/hub/state'?{
                active_id:state.active_id,
                queue_armed:state.armed,
                runs:state.items.map(x=>({
                    id:x.id,name:x.name,status:x.status,
                    run_id:x.run_id,step:x.step,group:x.group,
                })),
            }:null;
            const result=hubAdminRoute(requireHub(),'GET',route,
                url.searchParams,null,bees);
            if(result)return json(res,result.status,result.data);
        }
        if (req.method === 'GET' && route === '/api/hub/bees/workers') {
            // This is a view of Bees' authoritative control plane, not a second
            // machine registry. Trainer IDs are not assumed to identify hosts.
            return json(res, 200, workerSnapshot(await control()));
        }
        if (req.method === 'GET' && route === '/api/hub/bees/control') {
            // The first RL build precedes the existence of a release/control
            // server. Do not make the browser dependent on CLI bootstrap.
            if (!fs.existsSync(paths.latestReleasePath)) {
                return json(res, 200, {
                    run_id:null, training_enabled:false, pending_release:false,
                    env_args:[], experiment_owned:Boolean(state.active_id),
                    queue_armed:state.armed,
                    operator_operation:hubOperatorOperation,
                    initial_build_required:true,
                });
            }
            const status = await control();
            const d = status.desired || {};
            return json(res, 200, {
                run_id:d.run_id || null,
                training_enabled:Boolean(d.training_enabled),
                pending_release:Boolean(d.pending_release),
                env_args:Array.isArray(d.environment_args) ? d.environment_args : [],
                experiment_owned:Boolean(state.active_id),
                queue_armed:state.armed,
                operator_operation:hubOperatorOperation,
                initial_build_required:false,
                // The CLI and UI share the same Bees control plane.
            });
        }
        if (req.method === 'GET' && route === '/api/state') {
            return json(res, 200, { ...state, busy, rebuild_progress: rebuildProgress,
                benchmark_configured: fs.existsSync(BENCHMARK_SETTINGS),
                heldout_configured: fs.existsSync(HELDOUT_BENCHMARK_SETTINGS),
                experiment_data_root: DATA_ROOT, storage_restart_required: storageMigrationPending });
        }
        if (req.method === 'GET' && route === '/api/storage') {
            return json(res, 200, { root: DATA_ROOT,
                restart_required: storageMigrationPending });
        }
        if (req.method === 'GET' && route === '/api/curriculum-default') {
            return json(res, 200, { curriculum: curriculumDefault() });
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
        if (req.method === 'GET' && route === '/api/metric-history') {
            const item = state.items.find(entry => entry.id === url.searchParams.get('id'));
            if (!item) return json(res, 404, { error: 'Experiment not found.' });
            return json(res, 200, archivedMetricsChart(DATA_ROOT, item,
                url.searchParams.get('metric') || 'group_reward',
                url.searchParams.get('axis') || 'steps',
                Number(url.searchParams.get('smoothing') || 1)));
        }
        if (req.method === 'GET' && route === '/api/metric-archive') {
            const item = state.items.find(entry => entry.id === url.searchParams.get('id'));
            if (!item) return json(res, 404, { error: 'Experiment not found.' });
            const source = metricArchivePath(DATA_ROOT, item);
            if (!fs.existsSync(source)) return json(res, 404, {
                error: 'No full-resolution metric archive exists yet for this experiment.' });
            const stats = fs.statSync(source);
            res.writeHead(200, {
                'Content-Type': 'application/x-ndjson; charset=utf-8',
                'Content-Disposition': 'attachment; filename="bees-' + item.id + '-metrics.jsonl"',
                'Content-Length': stats.size, 'Cache-Control': 'no-store',
                'X-Content-Type-Options': 'nosniff',
            });
            fs.createReadStream(source).pipe(res);
            return;
        }
        if (req.method === 'GET' && route === '/api/research-summary') {
            const threshold = Number(url.searchParams.get('threshold') || 0.5);
            if (!Number.isFinite(threshold) || threshold < 0 || threshold > 1)
                return json(res, 400, { error: 'Win-rate threshold must be 0–100%.' });
            const groups = summarizeResearch(state.items, benchmarkReports(Infinity));
            return json(res, 200, {
                groups, comparisons: compareResearch(groups),
                efficiency: researchEfficiency(groups, threshold),
            });
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
        if (storageMigrationPending) {
            return json(res, 409, { error: 'Data-location switch prepared. Restart this manager before making other changes.' });
        }
        if(route.startsWith('/api/hub/')&&
            !route.startsWith('/api/hub/bees/')) {
            const result=hubAdminRoute(requireHub(),'POST',route,
                url.searchParams,value);
            if(result)return json(res,result.status,result.data);
        }

        if (route === '/api/hub/bees/start' ||
            route === '/api/hub/bees/stop' ||
            route === '/api/hub/bees/build' ||
            route === '/api/hub/bees/update-workers') {
            if (state.active_id || state.armed || state.stop_requested || busy) {
                return json(res, 409, {
                    error:'An experiment or another operator action owns training. Use experiment controls.',
                });
            }
            const action = route.slice('/api/hub/bees/'.length);
            // Initial build must be possible before any Bees release or control
            // server exists. The operator itself owns bootstrap validation.
            if (action === 'build' && !fs.existsSync(paths.latestReleasePath)) {
                if (value.confirm_build !== true)
                    return json(res, 409, {error:'Confirm the initial training release build.'});
                return json(res, 202, {
                    operation:runHubOperatorOperation('Initial Bees RL build', async()=>invokeBuild({})),
                });
            }
            const before = await control();
            if (!before.desired || before.desired.pending_release) {
                return json(res, 409, {error:'Training control unavailable or a release pending.'});
            }
            if (action === 'stop') {
                if (!before.desired.training_enabled)
                    return json(res, 409, {error:'Bees training is already stopped.'});
                if (value.confirm_stop !== true)
                    return json(res, 409, {error:'Explicitly confirm graceful stop.'});
                return json(res, 202, {
                    operation:runHubOperatorOperation('Graceful Bees stop', async()=>invokeStop({})),
                });
            }
            if (before.desired.training_enabled) {
                return json(res, 409, {error:'Stop the current training run safely first.'});
            }
            if (action === 'build') {
                if (value.confirm_build !== true)
                    return json(res, 409, {error:'Confirm the training release build.'});
                return json(res, 202, {
                    operation:runHubOperatorOperation('Bees RL build', async()=>invokeBuild({})),
                });
            }
            if (action === 'update-workers') {
                if (value.confirm_update !== true)
                    return json(res, 409, {error:'Confirm publishing the worker-only runtime.'});
                return json(res, 202, {
                    operation:runHubOperatorOperation('Bees worker software update',
                        async()=>invokeWorkerRuntime({expectedRunId:before.desired.run_id})),
                });
            }
            if (!['resume','new'].includes(value.mode))
                return json(res, 400, {error:'Use resume or new run mode.'});
            if (value.mode === 'new' && value.confirm_new_run !== true)
                return json(res, 409, {error:'Confirm starting a fresh optimizer and run identity.'});
            const currentArgs = Array.isArray(before.desired.environment_args)
                ? before.desired.environment_args : [];
            // Validation of the actual Unity release arguments remains in
            // invokeStart -> assertRlEnvironmentArgsValid. Never execute input.
            const args = value.env_args === undefined ? currentArgs : value.env_args;
            if (!Array.isArray(args) || args.length > 64 ||
                args.some(arg=>typeof arg !== 'string' || arg.length > 512 ||
                    !/^--rl-[a-z0-9-]+(?:=.*)?$/i.test(arg))) {
                return json(res, 400, {error:'Enter up to 64 valid --rl-... environment arguments.'});
            }
            if (value.mode === 'resume' && !matches(args, currentArgs) &&
                value.confirm_environment_change !== true) {
                return json(res, 409, {
                    error:'Confirm changing the environment for this existing run; training comparisons may be affected.',
                });
            }
            return json(res, 202, {
                operation:runHubOperatorOperation(
                    value.mode === 'new' ? 'New Bees run' : 'Resume Bees run',
                    async()=>invokeStart({newRun:value.mode==='new',envArgs:args}),
                ),
            });
        }
        if (route === '/api/storage') {
            if (busy || state.active_id || state.armed || state.stop_requested ||
                hub?.automation?.enabled) {
                return json(res, 409, { error: 'Disarm native automation and stop active experiments before relocating data.' });
            }
            if (value.root === DATA_ROOT)
                return json(res, 200, { root: DATA_ROOT, restart_required: false });
            const root = copyAndSelect(paths.trainingRoot, DATA_ROOT,
                value.root, state, writeJsonAtomic);
            storageMigrationPending = true;
            return json(res, 200, { root, restart_required: true,
                message: 'Data copied and configured. Stop and restart the Experiment Manager. The old directory remains as a backup.' });
        }
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
        if (route === '/api/curriculum-default') {
            const template = { schema_version: 1,
                name: String(value.name || 'Custom experiment curriculum').trim(),
                first_steps: value.first_steps, stage_name: value.stage_name,
                env_args: value.env_args, target_timeout_pct: value.target_timeout_pct,
                stages: value.stages };
            validateCurriculumTemplate(template);
            ensureDir(path.dirname(CURRICULUM_SAVED));
            writeJsonAtomic(CURRICULUM_SAVED, template);
            return json(res, 200, { ok: true, curriculum: template });
        }
        if (route === '/api/queue') {
            // Ordinary requests still queue exactly one experiment. Replicates
            // are an explicit opt-in and run sequentially through the existing
            // queue, each with fresh policy/optimizer initialization.
            const count = value.replicates == null ? 1 : Number(value.replicates);
            if (!Number.isSafeInteger(count) || count < 1 || count > 32) {
                throw Error('Replicate count must be 1–32.');
            }
            const baseSeed = Number(value.trainer?.seed);
            if (count > 1 && (!Number.isInteger(baseSeed) || baseSeed < 0 ||
                baseSeed + count - 1 >= 2 ** 31)) {
                throw Error('Replicates require a valid starting initialization seed with room for all seeds.');
            }
            const studyId = count > 1 ? crypto.randomUUID() : null;
            const prepared = [];
            for (let i = 0; i < count; i++) {
                const seed = baseSeed + i;
                const suffix = count > 1 ? ' · seed ' + seed : '';
                const input = {
                    ...value,
                    name: count > 1 ? String(value.name || '').slice(0, 100 - suffix.length) + suffix : value.name,
                    trainer: count > 1 ? { ...value.trainer, seed } : (value.trainer || {}),
                };
                const item = await definition(input);
                if (studyId) {
                    item.study_id = studyId;
                    item.study_label = String(value.name || '');
                    item.replicate_number = i + 1;
                    item.replicate_count = count;
                    item.replicate_seed = seed;
                }
                prepared.push(item);
            }
            state.items.push(...prepared);
            try { save(); }
            catch (error) { state.items.splice(-prepared.length, prepared.length); throw error; }
            return json(res, 201, { items: prepared, study_id: studyId });
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
                const seed = prepared.trainer_config?.resolved?.seed;
                if (original.study_id && state.items.some(peer =>
                    peer.id !== original.id && peer.study_id === original.study_id &&
                    peer.trainer_config?.resolved?.seed === seed)) {
                    throw Error('A replicate in this study already uses the selected initialization seed.');
                }
                const revised = {
                    ...prepared, id: original.id, created_at: original.created_at,
                    edit_revision: (original.edit_revision || 0) + 1,
                    edited_at: now(),
                    ...(original.study_id ? {
                        study_id: original.study_id,
                        study_label: original.study_label,
                        replicate_number: original.replicate_number,
                        replicate_count: original.replicate_count,
                        replicate_seed: seed,
                    } : {}),
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
        if (route === '/api/research-heldout') {
            if (busy || state.active_id || state.armed || state.stop_requested) {
                return json(res, 409, { error: 'Wait until the experiment queue and trainer are idle.' });
            }
            const item = state.items.find(entry => entry.id === value.id);
            if (!item || !item.heldout_benchmarks ||
                !item.run_id || !['completed', 'stopped', 'ended_early'].includes(item.status)) {
                return json(res, 409, { error: 'Choose a finished run with a frozen held-out suite.' });
            }
            if (item.heldout_validation?.status === 'completed') {
                return json(res, 409, { error: 'Held-out validation is already complete. Its results cannot be rerun or replaced.' });
            }
            const desired = await control();
            if (!desired.desired || desired.desired.training_enabled ||
                desired.desired.pending_release) {
                return json(res, 409, { error: 'Training and pending releases must be stopped.' });
            }
            verifyBenchmarkPlan(item.heldout_benchmarks);
            const record = item.heldout_validation || {
                stage_index: item.stage_index || 0,
                target_step: item.step,
                started_at: now(),
            };
            record.status = 'pending';
            item.heldout_validation = record;
            state.active_id = item.id;
            state.message = 'Starting held-out validation for ' + item.name;
            save();
            setImmediate(cycle);
            return json(res, 202, { ok: true });
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
        if (route === '/api/rebuild-unity') {
            const item = active();
            if (busy || !item || item.status !== 'paused' ||
                state.armed || state.stop_requested) {
                return json(res, 409, { error: 'Pause the active experiment before rebuilding Unity.' });
            }
            const before = await control();
            if (item.unity_rebuild_attempt) {
                const release = readJson(paths.latestReleasePath);
                if (!recordUnityMaintenanceBuild(item, release, before) &&
                    !clearUnpublishedUnityRebuild(item, release, before)) {
                    return json(res, 409, {
                        error: 'An earlier Unity rebuild has not been reconciled. Do not overwrite it.',
                    });
                }
            }
            assertMaintenanceOwnership(item, before);
            if (before.desired.training_enabled) {
                return json(res, 409, { error: 'Training is still enabled; Unity rebuild refused.' });
            }
            assertSameLineageCheckpoint(item);
            const release = readJson(paths.latestReleasePath);
            busy = true;
            const startedAt = now();
            const reportRebuildProgress = message => {
                rebuildProgress = { started_at: startedAt, updated_at: now(), message };
            };
            item.unity_rebuild_attempt = {
                started_at: startedAt, old_build: item.release_build,
                compatibility_key: release.compatibility_key,
            };
            state.message = 'Preparing Unity rebuild; experiment remains paused and its learner is unchanged.';
            reportRebuildProgress('Checking paused experiment before Unity compilation');
            save();
            try {
                const rebuilt = await invokeMaintenanceUnityRebuild({
                    expectedRunId: item.run_id,
                    expectedBuildId: item.unity_rebuild_attempt.old_build,
                    expectedConfigSha: item.trainer_config.sha256,
                    onProgress: reportRebuildProgress,
                });
                const updatedRelease = readJson(paths.latestReleasePath);
                const after = await control();
                if (!recordUnityMaintenanceBuild(item, updatedRelease, after) ||
                    rebuilt.build_id !== item.release_build) {
                    throw Error('Rebuilt release did not become canonical with the expected training identity.');
                }
                state.message = 'Unity rebuilt and activated on the same run. ' +
                    'WARNING: the simulation may have changed; results before and after this ' +
                    'build should be compared separately. Select Resume training.';
                save();
                return json(res, 200, { ok: true, ...rebuilt });
            } catch (error) {
                reportRebuildProgress('Build failed; reconciling the original release before reporting recovery');
                let reconciled = false, cleanOldRelease = false;
                if (/bootstrap rollback failed/i.test(error.message) &&
                    item.unity_rebuild_attempt) {
                    item.unity_rebuild_attempt.requires_manual_repair = true;
                }
                try {
                    const current = readJson(paths.latestReleasePath);
                    const controlState = await control();
                    reconciled = recordUnityMaintenanceBuild(item, current, controlState);
                    if (!reconciled) cleanOldRelease =
                        clearUnpublishedUnityRebuild(item, current, controlState);
                } catch (_) {}
                state.message = 'Unity rebuild failed: ' + error.message +
                    (reconciled
                        ? ' A compatible new build became canonical; experiment remains paused.'
                        : cleanOldRelease
                            ? ' Original build remains canonical; experiment is safe to resume.'
                            : ' Experiment remains paused; inspect the incomplete release before resuming.');
                save();
                return json(res, 409, { error: state.message });
            } finally {
                busy = false;
                rebuildProgress = null;
            }
        }
        if (route === '/api/update-workers') {
            const item = active();
            if (busy || !item || item.status !== 'paused' ||
                state.armed || state.stop_requested) {
                return json(res, 409, { error: 'Pause the active experiment before updating its workers.' });
            }
            const status = await control();
            assertMaintenanceOwnership(item, status);
            if (status.desired.training_enabled) {
                return json(res, 409, { error: 'Training is not stopped. Worker update refused.' });
            }
            assertSameLineageCheckpoint(item);
            busy = true;
            state.message = 'Preparing worker-only software update. Experiment remains paused.';
            save();
            try {
                const published = await invokeWorkerRuntime({ expectedRunId: item.run_id });
                const after = await control();
                if (after.desired?.training_enabled ||
                    after.desired?.run_id !== item.run_id ||
                    after.desired?.canonical_build_id !== item.release_build) {
                    throw Error('Worker runtime published, but training control ownership changed. Resume blocked.');
                }
                item.worker_software_updates = item.worker_software_updates || [];
                item.worker_software_updates.push({
                    at: now(), runtime_version: published.worker_runtime_version,
                    learner_runtime_version: published.learner_runtime_version,
                });
                state.message = 'Worker-only runtime published for ' + item.name +
                    '. Remote supervisors automatically adopt it. Resume the same experiment ' +
                    'and check Worker Status for live-resize support.';
                save();
                return json(res, 200, { ok: true, ...published });
            } catch (error) {
                state.message = 'Worker software update failed: ' + error.message +
                    '. The experiment remains paused; inspect the error before resuming.';
                save();
                return json(res, 409, { error: state.message });
            } finally {
                busy = false;
            }
        }
        if (route === '/api/resume') {
            const item = active();
            if (busy || !item || item.status !== 'paused' || state.stop_requested ||
                state.armed) {
                return json(res, 409, { error: 'Only a confirmed paused experiment can be resumed.' });
            }
            if (item.unity_rebuild_attempt) {
                const current = readJson(paths.latestReleasePath);
                const status = await control();
                if (!recordUnityMaintenanceBuild(item, current, status) &&
                    !clearUnpublishedUnityRebuild(item, current, status)) {
                    return json(res, 409, { error: 'Unity rebuild is incomplete or unreconciled. Remain paused.' });
                }
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
                !['transitioning', 'checkpointing', 'evaluating_final_stage'].includes(active()?.status)) {
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
        return json(res, [401,413].includes(err.status) ? err.status : 400, { error: err.message });
    }
});
// The dedicated worker-only HTTP boundary is shared with the isolated
// transport smoke. It cannot serve Hub administration or Bees training.
const agentServer=createAgentServer(requireHub);
if (require.main === module) {
    server.listen(PORT, HOST, () => {
        console.log('Bees experiments: http://' + HOST + ':' + PORT);
        console.log('Records: ' + DB);
        setImmediate(cycle);
        setInterval(cycle, 10000);
        // The separate Hub scheduler is inert until an operator explicitly
        // arms named native-capable machines. Native service state and Bees
        // training ownership remain separate; faults disarm Hub automation.
        setInterval(() => {
            const before=hub?.automationStatus();
            if(!before?.enabled&&!before?.restart_rearm_pending)return;
            const status=hub.tickAutomation();
            if(status.last_error)
                console.error('[hub] native automation disarmed: '+status.last_error);
        }, 10000);
    });
    // Optional Hub agent service must not prevent the existing Bees
    // coordinator from recovering training if this secondary port is busy.
    agentServer.on('error', error => {
        console.error('[hub] agent heartbeat listener unavailable:', error.message);
    });
    agentServer.listen(AGENT_PORT, AGENT_HOST, () => {
        console.log('Experiment Hub machine heartbeat port: '+
            AGENT_HOST+':'+AGENT_PORT);
    });
}
module.exports = { server, agentServer, definition, measure, workerSnapshot, benchmarkReports, benchmarkPlan, verifyBenchmarkPlan };
