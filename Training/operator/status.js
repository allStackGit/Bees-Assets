'use strict';

const fs = require('node:fs');
const path = require('node:path');

const {
    exists,
    getStateReferencedLivePid,
    paths,
    readJson,
    readTail,
    requestJson,
    sleep,
} = require('./common');
const { localActorSettings } = require('./localActor');

function listLogFiles(root, recursive, limit = Number.POSITIVE_INFINITY) {
    if (!fs.existsSync(root)) return [];
    const found = [];
    const queue = [root];
    while (queue.length && found.length < limit) {
        const current = queue.shift();
        let entries = [];
        try { entries = fs.readdirSync(current, { withFileTypes: true }); } catch (_) { continue; }
        for (const entry of entries) {
            const full = path.join(current, entry.name);
            if (entry.isDirectory() && recursive) queue.push(full);
            else if (entry.isFile() && entry.name.toLowerCase().endsWith('.log')) {
                try {
                    const stat = fs.statSync(full);
                    found.push({ full, mtimeMs: stat.mtimeMs });
                } catch (_) {}
            }
        }
    }
    return found;
}

// Search backwards for the latest checkpoint-resume marker, including when it has
// scrolled beyond readTail's bounded window. No historical training logs are opened.
function lastLearnerResumeStep(filename) {
    let fd;
    try {
        fd = fs.openSync(filename, 'r');
        const end = fs.fstatSync(fd).size;
        const chunkBytes = 65536;
        let nextPrefix = '';
        for (let pos = end; pos > 0;) {
            const count = Math.min(chunkBytes, pos);
            pos -= count;
            const chunk = Buffer.allocUnsafe(count);
            const bytes = fs.readSync(fd, chunk, 0, count, pos);
            const text = chunk.toString('utf8', 0, bytes) + nextPrefix;
            const matches = [...text.matchAll(/Resuming training from step\s+([\d,]+)/gi)];
            if (matches.length) {
                const step = Number(matches[matches.length - 1][1].replace(/,/g, ''));
                return Number.isSafeInteger(step) ? step : null;
            }
            nextPrefix = text.slice(0, 128);
        }
    } catch (_) {
        return null;
    } finally {
        if (fd !== undefined) fs.closeSync(fd);
    }
    return null;
}

function parseLearnerLogFiles(files) {
    let elo = null;
    let step = null;
    let reward = null;
    let averageStepsPerSecond = null;
    let liveStepsPerSecond = null;
    let restartAverageStepsPerSecond = null;
    let lastSummaryElapsedSeconds = null;

    for (const file of files) {
        let firstStep = null;
        let firstElapsed = null;
        let previousStep = null;
        let previousElapsed = null;
        let fileAverage = null;
        let fileLive = null;
        let lastSummary = null;

        for (const line of readTail(file.full, 1000, 2 * 1024 * 1024)) {
            // Only ML-Agents summary lines are authoritative learner progress. Other Bees logs
            // legitimately contain strings such as "registered candidate ... step=1144671" and
            // must never be mistaken for the learner's current step.
            const stepMatch = line.match(/\bStep\s*[:=]\s*([\d,]+)/i);
            const elapsedMatch = line.match(
                /Time Elapsed\s*[:=]\s*(\d+(?:\.\d+)?)\s*s/i
            );
            if (!stepMatch || !elapsedMatch) continue;

            const lineStep = Number(stepMatch[1].replace(/,/g, ''));
            const lineElapsed = Number(elapsedMatch[1]);
            if (!Number.isFinite(lineStep) || !Number.isFinite(lineElapsed)) continue;
            step = lineStep;
            lastSummary = { step: lineStep, elapsed: lineElapsed };
            lastSummaryElapsedSeconds = lineElapsed;

            let match;
            if ((match = line.match(/Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)/i))) {
                reward = Number(match[1]);
            }
            if ((match = line.match(/\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)/i))) {
                elo = Number(match[1]);
            }

            {
                if (
                    firstStep === null ||
                    previousStep === null ||
                    lineStep < previousStep ||
                    lineElapsed <= previousElapsed
                ) {
                    firstStep = lineStep;
                    firstElapsed = lineElapsed;
                    fileAverage = null;
                    fileLive = null;
                } else {
                    const elapsedDelta = lineElapsed - previousElapsed;
                    if (elapsedDelta > 0) fileLive = (lineStep - previousStep) / elapsedDelta;
                    const averageElapsed = lineElapsed - firstElapsed;
                    if (averageElapsed > 0) fileAverage = (lineStep - firstStep) / averageElapsed;
                }
                previousStep = lineStep;
                previousElapsed = lineElapsed;
            }
        }

        if (fileAverage !== null) averageStepsPerSecond = fileAverage;
        if (fileLive !== null) liveStepsPerSecond = fileLive;
        if (lastSummary && lastSummary.elapsed > 0) {
            const resumedStep = lastLearnerResumeStep(file.full);
            // A nonzero resumed checkpoint is the only authoritative baseline
            // when reading a tail of a long-running, repeatedly restarted log.
            // Do not invent an average when that marker is missing.
            if (resumedStep !== null && lastSummary.step >= resumedStep) {
                restartAverageStepsPerSecond =
                    (lastSummary.step - resumedStep) / lastSummary.elapsed;
            }
        }
    }

    return {
        ELO: elo,
        Step: step,
        MeanReward: reward,
        AverageStepsPerSecond: averageStepsPerSecond,
        LiveStepsPerSecond: liveStepsPerSecond,
        RestartAverageStepsPerSecond: restartAverageStepsPerSecond,
        LastSummaryElapsedSeconds: lastSummaryElapsedSeconds,
    };
}

function getLocalLearnerStats(runId = '') {
    let authoritativeLiveLog = null;
    if (runId) {
        authoritativeLiveLog = path.join(
            paths.centralAgentInstallRoot,
            'logs',
            runId,
            'learner-live.log',
        );
        if (exists(authoritativeLiveLog)) {
            const authoritative = parseLearnerLogFiles([
                {
                    full: authoritativeLiveLog,
                    mtimeMs: fs.statSync(authoritativeLiveLog).mtimeMs,
                },
            ]);
            if (authoritative.Step !== null) return authoritative;
        }
    }

    const files = [];
    const operatorLogRoot = path.join(paths.logsRoot, 'Training');
    files.push(...listLogFiles(operatorLogRoot, false));

    if (runId) {
        const managedLearnerLogRoot = path.join(
            paths.centralAgentInstallRoot,
            'logs',
            runId,
        );
        files.push(
            ...listLogFiles(managedLearnerLogRoot, false)
                .filter(file => path.resolve(file.full) !== path.resolve(authoritativeLiveLog)),
        );
    }

    let trainerResultsRoot = path.join(paths.trainingRoot, 'trainer-results');
    if (runId) trainerResultsRoot = path.join(trainerResultsRoot, runId);
    files.push(...listLogFiles(trainerResultsRoot, true));

    const selected = files
        .sort((a, b) => b.mtimeMs - a.mtimeMs)
        .slice(0, 24)
        .sort((a, b) => a.mtimeMs - b.mtimeMs || a.full.localeCompare(b.full));

    return parseLearnerLogFiles(selected);
}


function restartWallClockRate(learner, runId, running) {
    if (!runId || !/^[a-zA-Z0-9_.-]+$/.test(runId)) {
        return learner.RestartAverageStepsPerSecond;
    }
    const state = readJson(path.join(
        paths.centralAgentInstallRoot, 'logs', runId, 'learner-restart.json'
    ), null);
    if (!state || state.run_id !== runId ||
        !Number.isSafeInteger(state.resumed_step) || state.resumed_step < 0 ||
        !Number.isFinite(state.started_unix_seconds) ||
        !Number.isFinite(learner.Step) || learner.Step < state.resumed_step) {
        return learner.RestartAverageStepsPerSecond;
    }

    const logElapsed = learner.LastSummaryElapsedSeconds;
    const liveElapsed = (Date.now() / 1000) - state.started_unix_seconds;
    if (running) {
        // Old summaries may still be present briefly after a new learner starts.
        // Do not attribute those historical steps to the new launch.
        if (!Number.isFinite(liveElapsed) || liveElapsed <= 0 ||
            (Number.isFinite(logElapsed) && logElapsed > liveElapsed + 30)) {
            return null;
        }
        return (learner.Step - state.resumed_step) / liveElapsed;
    }
    // When stopped, elapsed time must not continue to accumulate.
    return Number.isFinite(logElapsed) && logElapsed > 0 ?
        (learner.Step - state.resumed_step) / logElapsed :
        learner.RestartAverageStepsPerSecond;
}

function parsePpoFields(line, prefix) {
    const data = {};
    const offset = line.indexOf(prefix);
    if (offset < 0) return data;
    for (const token of line.slice(offset + prefix.length).trim().split(/\s+/)) {
        const equal = token.indexOf('=');
        if (equal > 0) data[token.slice(0, equal)] = token.slice(equal + 1);
    }
    return data;
}

function getLatestPpoUpdate(runId) {
    // Read only the active learner's bounded log tail; historical runs must not
    // masquerade as the current learner's hardware or PPO timing.
    if (!runId || !/^[a-zA-Z0-9_.-]+$/.test(runId)) return null;
    const file = path.join(
        paths.centralAgentInstallRoot, 'logs', runId, 'learner-live.log'
    );
    if (!exists(file)) return null;
    const endTag = '[Bees PPO timing] update end ';
    const hardwareTag = '[Bees PPO variability] hardware ';
    const variabilityTag = '[Bees PPO variability] timing ';
    let update = null;
    let hardware = null;
    let variability = null;
    for (const line of readTail(file, 1000, 2 * 1024 * 1024)) {
        if (line.includes(endTag)) {
            update = parsePpoFields(line, endTag);
            hardware = null;
            variability = null;
        } else if (update && line.includes(hardwareTag)) {
            hardware = parsePpoFields(line, hardwareTag);
        } else if (update && line.includes(variabilityTag)) {
            variability = parsePpoFields(line, variabilityTag);
        }
    }
    if (!update || !Number.isFinite(Number(update.seconds))) return null;
    return { update, hardware, variability };
}

function ppoStatusLines(ppo, signals) {
    if (!ppo) return [
        'PPO diagnostics: no completed update in the active learner log yet.',
        'Bottleneck: indeterminate (PPO timing not available).',
    ];
    const { update, hardware, variability } = ppo;
    const minibatches = Number(String(update.minibatches || '').split('/')[0]);
    const batches = Number.isFinite(minibatches) && minibatches > 0 ? minibatches : 0;
    const stage = [
        ['minibatch optimizer work', Number(update.optimizer_total) * batches],
        ['CPU preparation wait', Number(update.cpu_prepare_wait) * batches],
        ['batch materialization', Number(update.materialize)],
        ['diagnostic profiling', Number(update.diagnostic_profile)],
    ].filter(([, duration]) => Number.isFinite(duration) && duration >= 0);
    const seconds = Number(update.seconds);
    const formatStage = ([label, duration]) =>
        label + '=' + number(duration, 1) + 's';
    const detailed = update.diagnostics !== 'off';
    const lines = [
        'PPO latest completed update: ' + number(seconds, 1) + 's  ' +
        'minibatches=' + (update.minibatches || '-') +
        '  optimizer_updates/s=' + number(batches > 0 && seconds > 0 ? batches / seconds : null, 2) +
        '  cache=' + (update.cache_storage || '-') +
        '  avg_minibatch=' + (detailed ? number(update.avg_minibatch, 3) + 's' : 'disabled'),
        detailed
            ? 'PPO stage estimates: ' + stage.map(formatStage).join('  ') +
              '  (sampled durations; may overlap)'
            : 'PPO stage estimates: disabled (-PpoDiagnostics to enable)',
    ];
    if (hardware) {
        lines.push(
            'PPO hardware (sampled during update): CPU process=' +
            number(hardware.process_cpu_avg, 0) + '%  system=' +
            number(hardware.system_cpu_avg, 0) + '%  GPU=' +
            number(hardware.gpu_util_avg, 0) + '%  VRAM=' +
            number(hardware.vram_used_max_mib, 0) + 'MiB  RAM=' +
            number(hardware.rss_max_mib, 0) + 'MiB'
        );
    }
    if (variability) {
        lines.push(
            'PPO minibatch variation: p50=' + number(variability.minibatch_p50, 3) +
            's  p90=' + number(variability.minibatch_p90, 3) +
            's  max=' + number(variability.minibatch_max, 3) + 's'
        );
    }
    const leading = stage
        .filter(([label]) => label !== 'diagnostic profiling')
        .sort((a, b) => b[1] - a[1])[0];
    const evidence = leading ? leading[0] + ' is the largest sampled PPO stage' :
        'PPO stage totals unavailable';
    const broker = signals && signals.broker;
    const queueDepth = broker && broker.broker_queue_depth;
    const capacity = broker && broker.broker_queue_capacity;
    const pressure = broker && broker.broker_backpressure_total;
    const brokerRatio = capacity > 0 ? queueDepth / capacity : null;
    if (brokerRatio != null && brokerRatio >= 0.75) {
        lines.push('Bottleneck signal: CENTRAL QUEUE PRESSURE ' +
            queueDepth + '/' + capacity + '; ' + evidence +
            '. Learner consumption or network admission may be limiting.');
    } else if ((signals && signals.uploadBacklog) > 0) {
        lines.push('Bottleneck signal: worker upload queue=' + signals.uploadBacklog +
            ' batches; broker=' + (broker ? queueDepth + '/' + capacity : 'unknown') +
            '. Inspect network admission and learner throughput; ' + evidence + '.');
    } else if (broker && Number(broker.broker_learning_queue_depth) > 0) {
        lines.push('Bottleneck signal: downstream PPO input queue contains ' +
            broker.broker_learning_queue_depth +
            ' trajectories. Watch whether this grows over multiple samples; ' + evidence + '.');
    } else {
        lines.push('Bottleneck signal: no sustained queue-growth evidence available; ' +
            evidence + '. One snapshot cannot identify the limiting machine.');
    }
    if (broker) {
        lines.push('Broker cumulative: backpressure=' + pressure +
            '  stale accepted=' + broker.broker_accepted_stale_steps_total +
            '  stale consumed=' + broker.broker_consumed_stale_steps_total +
            '  stale discarded=' + broker.broker_discarded_stale_steps_total +
            ' experiences (since broker start).');
    }
    return lines;
}

function number(value, digits = 1, suffix = '') {
    if (value === null || value === undefined || !Number.isFinite(Number(value))) return '-';
    return Number(value).toFixed(digits) + suffix;
}

function statusError(record) {
    const current = String(record.last_error || record.preparation_error || '').trim();
    if (current) return current;

    const ageLabel = ageSeconds => {
        if (ageSeconds < 60) return Math.round(ageSeconds) + 's';
        if (ageSeconds < 3600) return (ageSeconds / 60).toFixed(1) + 'm';
        return (ageSeconds / 3600).toFixed(1) + 'h';
    };
    const historical = [];
    const recordAgeSeconds = Number(record.age_seconds);
    const snapshotLagSeconds = (
        Number.isFinite(recordAgeSeconds) && recordAgeSeconds >= 0
            ? recordAgeSeconds
            : 0
    );
    const metrics = record.metrics && typeof record.metrics === 'object'
        ? record.metrics
        : {};

    const reconciliation = metrics.reconciliation;
    if (reconciliation && typeof reconciliation === 'object') {
        const phase = String(reconciliation.phase || '').trim();
        const seconds = Number(reconciliation.seconds_in_phase);
        if (phase && Number.isFinite(seconds) && seconds >= 0) {
            return 'Reconcile, ' + ageLabel(seconds + snapshotLagSeconds) + ': ' + phase;
        }
    }

    const throughput = metrics.throughput;
    const staleActorResync = Boolean(
        throughput &&
        typeof throughput === 'object' &&
        String(throughput.last_session_failure_type || '').trim() === 'BrokerStaleActor'
    );
    if (throughput && typeof throughput === 'object') {
        const count = Number(throughput.session_failures_total);
        const ageSeconds = Number(throughput.seconds_since_last_session_failure);
        const failureType = String(throughput.last_session_failure_type || '').trim();
        const failureMessage = String(
            throughput.last_session_failure_message || ''
        ).trim();
        if (
            Number.isInteger(count) &&
            count > 0 &&
            Number.isFinite(ageSeconds) &&
            ageSeconds >= 0 &&
            failureType &&
            failureType !== 'BrokerStaleActor'
        ) {
            const effectiveAgeSeconds = ageSeconds + snapshotLagSeconds;
            historical.push({
                ageSeconds: effectiveAgeSeconds,
                text: 'WAN failures ' + count + ' total, last ' +
                    ageLabel(effectiveAgeSeconds) + ' ago: ' + failureType +
                    (failureMessage ? ': ' + failureMessage : ''),
            });
        }
    }

    const control = metrics.control;
    if (control && typeof control === 'object') {
        const count = Number(control.failures_total);
        const ageSeconds = Number(control.seconds_since_last_failure);
        const failureType = String(control.last_failure_type || '').trim();
        if (
            Number.isInteger(count) &&
            count > 0 &&
            Number.isFinite(ageSeconds) &&
            ageSeconds >= 0 &&
            failureType
        ) {
            const effectiveAgeSeconds = ageSeconds + snapshotLagSeconds;
            historical.push({
                ageSeconds: effectiveAgeSeconds,
                text: 'Control x' + count + ', ' + ageLabel(effectiveAgeSeconds) +
                    ' ago: ' + failureType,
            });
        }
    }

    const optimizer = record.env_optimizer;
    const rawInstabilityMs = optimizer && optimizer.last_instability_ms;
    const instabilityMs = (
        rawInstabilityMs !== null &&
        rawInstabilityMs !== undefined &&
        Number.isFinite(Number(rawInstabilityMs)) &&
        Number(rawInstabilityMs) >= 0
    ) ? Number(rawInstabilityMs) : null;
    const instabilityReason = String(
        optimizer && optimizer.last_instability_reason || ''
    ).trim();
    const optimizerDecision = String(
        optimizer && optimizer.decision || ''
    ).trim();
    const processState = String(record.process_state || '').trim();
    const recoveredProcessInstability =
        processState === 'running' &&
        (
            instabilityReason === 'worker process state stopped' ||
            instabilityReason === 'worker process state stopping'
        );
    if (
        Number.isFinite(instabilityMs) &&
        instabilityMs >= 0 &&
        instabilityReason &&
        !recoveredProcessInstability &&
        !(staleActorResync && instabilityReason === 'WAN actor session failure')
    ) {
        const ageSeconds = Math.max(0, (Date.now() - instabilityMs) / 1000);
        historical.push({
            ageSeconds,
            text: 'Optimizer, ' + ageLabel(ageSeconds) + ' ago: ' + instabilityReason,
        });
    }

    historical.sort((left, right) => left.ageSeconds - right.ageSeconds);
    if (historical.length) return historical[0].text;
    return optimizerDecision ? 'Optimizer: ' + optimizerDecision : '';
}

function table(rows, columns) {
    if (!rows.length) return [];
    const widths = {};
    for (const column of columns) {
        widths[column] = Math.max(
            column.length,
            ...rows.map(row => String(row[column] === undefined ? '' : row[column]).length),
        );
    }
    const render = row => columns.map(column =>
        String(row[column] === undefined ? '' : row[column]).padEnd(widths[column])
    ).join('  ').trimEnd();
    return [
        render(Object.fromEntries(columns.map(column => [column, column]))),
        columns.map(column => '-'.repeat(widths[column])).join('  '),
        ...rows.map(render),
    ];
}

function rolloutBlockers(status) {
    const desired = status.desired || {};
    const pending = desired.pending_release;
    if (!pending) return [];
    const trainers = Array.isArray(status.trainers) ? status.trainers : [];
    const required = Array.isArray(pending.required_trainers) ? pending.required_trainers : [];
    const blockers = [];

    for (const requirement of required) {
        const id = String(requirement.trainer_id || '');
        const platform = String(requirement.platform || '');
        const record = trainers.find(item => String(item.trainer_id || '') === id);
        if (!record) {
            blockers.push(id + '[' + platform + ']: missing/no heartbeat');
            continue;
        }
        const stale = Boolean(record.stale);
        const state = String(record.process_state || '');
        const build = String(record.build_id || '');
        const prepared = String(record.prepared_build_id || '');
        const revision = record.applied_revision;
        const error = String(record.last_error || '');
        const phaseRevision = pending.phase_revision;
        const age = number(record.age_seconds, 1, 's');
        const errorText = error ? ' error=' + error : '';

        if (pending.phase === 'preparing' &&
            (stale || (build !== String(pending.build_id) && prepared !== String(pending.build_id)))) {
            blockers.push(
                id + '[' + platform + ']: ' + (stale ? 'STALE' : 'not prepared') +
                ' state=' + state + ' age=' + age +
                ' build=' + (build || '-') + ' prepared=' + (prepared || '-') +
                ' rev=' + (revision == null ? '-' : revision) + errorText
            );
        } else if (pending.phase === 'rolling' &&
            (stale ||
             build !== String(pending.build_id) ||
             state !== 'running' ||
             error ||
             (phaseRevision != null && Number(revision) < Number(phaseRevision)))) {
            blockers.push(
                id + '[' + platform + ']: rollout state=' + state + ' age=' + age +
                ' build=' + (build || '-') + ' prepared=' + (prepared || '-') +
                ' rev=' + (revision == null ? '-' : revision) + errorText
            );
        } else if (pending.phase === 'stopping' &&
            (stale ||
             state !== 'stopped' ||
             (phaseRevision != null && Number(revision) < Number(phaseRevision)))) {
            blockers.push(
                id + '[' + platform + ']: stop state=' + state + ' age=' + age +
                ' build=' + (build || '-') + ' rev=' +
                (revision == null ? '-' : revision) + errorText
            );
        }
    }
    return blockers;
}

async function getStatusFrameLines(config, adminToken) {
    const now = new Date();
    const lines = [
        'Bees distributed learning status  ' + now.toLocaleString(),
        '='.repeat(78),
    ];

    let status;
    try {
        status = await requestJson(config.controlUrl, adminToken, 'GET', '/v1/status');
    } catch (error) {
        lines.push('Server: OFFLINE/UNREACHABLE - ' + error.message);
        if (exists(paths.serverStatePath)) {
            try {
                const serverState = readJson(paths.serverStatePath);
                const persistedPid = Number(serverState.pid || 0);
                const livePid = getStateReferencedLivePid(serverState);
                lines.push(
                    'Server supervisor state: pid=' +
                    (persistedPid > 0 ? persistedPid : '-') +
                    ' pid_alive=' + Boolean(livePid) +
                    ' state=' + String(serverState.status || '-') +
                    ' runtime=' + String(serverState.source_hash || '').slice(0, 12)
                );
            } catch (stateError) {
                lines.push(
                    'Server supervisor state: unreadable - ' +
                    stateError.name + ': ' + stateError.message
                );
            }
        } else {
            lines.push('Server supervisor state: missing');
        }
        return lines;
    }

    try {
        const desired = status.desired;
        if (!desired) throw new Error('status payload has no desired state object');
        const trainers = Array.isArray(status.trainers) ? status.trainers : [];

        lines.push(
            'Server: ONLINE   Training: ' + Boolean(desired.training_enabled) +
            '   Revision: ' + desired.revision
        );
        lines.push(
            'Build:  ' + String(desired.canonical_build_id || '') +
            '   Run: ' + String(desired.run_id || '')
        );
        const localActor = localActorSettings(config);
        lines.push(
            'Cluster: local_envs=' + config.numLocalEnvs +
            ' local_actor=' + (
                localActor.enabled
                    ? localActor.initialEnvs + (
                        localActor.autoTune
                            ? '(auto ' + localActor.minEnvs + '-' + localActor.maxEnvs + ')'
                            : '(fixed)'
                    )
                    : 'off'
            ) +
            ' max_remote=' + (config.localOnly ? 0 : config.maxRemoteActors) +
            ' local_only=' + (config.localOnly ? 'on' : 'off') +
            ' learner_device=' + (config.learnerDevice || 'trainer-config') +
            ' broker_port=' + config.brokerPort +
            ' threaded=' + (config.threadedTraining ? 'on' : 'off') +
            ' policy_lag=' + Number(config.threadedPolicyLag)
        );
        const learnerOptimizations = config.learnerOptimizations || {};
        lines.push(
            'Learner opt: sync=' + (
                learnerOptimizations.syncCleanup ? 'on' : 'off'
            ) +
            ' shards=' + Number(learnerOptimizations.streamShards || 1) +
            ' prefetch=' + (
                learnerOptimizations.minibatchPrefetch ? 'on' : 'off'
            ) +
            ' overlap=' + (
                learnerOptimizations.criticBaselineOverlap ? 'on' : 'off'
            ) +
            ' graphs=' + (
                learnerOptimizations.cudaGraphs ? 'on' : 'off'
            ) +
            ' profile=' + (
                learnerOptimizations.profileOnce ? 'on' : 'off'
            ) +
            ' entropy_beta=' + (
                config.entropyBeta == null ? 'base' : Number(config.entropyBeta)
            )
        );

        if (desired.pending_release) {
            const pending = desired.pending_release;
            lines.push(
                'Pending release: build=' + pending.build_id +
                ' phase=' + pending.phase +
                ' incompatible=' + Boolean(pending.incompatible)
            );
            const blockers = rolloutBlockers(status);
            if (blockers.length) {
                lines.push('Rollout blockers:');
                for (const blocker of blockers) lines.push('  ' + blocker);
            } else {
                lines.push('Rollout blockers: none visible; waiting for the control state machine to advance.');
            }
        }

        const environmentArgs = Array.isArray(desired.environment_args) ? desired.environment_args : [];
        lines.push('Env:    ' + (environmentArgs.length ? environmentArgs.join(' ') : '(none)'));
        lines.push('');

        const rows = trainers.map(record => {
            const metrics = record.metrics || {};
            const throughput = metrics.throughput || {};
            const host = metrics.host || {};
            const capacity = record.worker_capacity || {};
            const optimizer = record.env_optimizer || {};
            const currentEnvs = capacity.current_envs;
            const desiredEnvs = optimizer.desired_envs;
            let envDisplay = '-';
            if (currentEnvs != null) {
                envDisplay = String(currentEnvs);
                if (desiredEnvs != null && Number(desiredEnvs) !== Number(currentEnvs)) {
                    envDisplay += '->' + desiredEnvs;
                }
            }
            const expRate = optimizer.measured_sps != null
                ? number(optimizer.measured_sps, 0)
                : optimizer.baseline_sps != null
                    ? number(optimizer.baseline_sps, 0)
                    : '-';
            const liveExpRate = throughput.learner_consumed_steps_per_sec != null
                ? number(throughput.learner_consumed_steps_per_sec, 0)
                : '-';
            const centralWithoutLocalEnvs =
                String(record.trainer_id || '') === 'central-learner' &&
                Number(config.numLocalEnvs) === 0;
            const episodes = centralWithoutLocalEnvs
                ? 0
                : Number(metrics.window_episodes || 0);
            return {
                Trainer: String(record.trainer_id || '-'),
                Role: String(record.role || '-'),
                Platform: String(record.platform || '-'),
                State: record.stale ? 'STALE' : String(record.process_state || '-'),
                Envs: envDisplay,
                vCPU: host.logical_cpus == null ? '-' : String(host.logical_cpus),
                'CPU%': number(host.cpu_percent, 0, '%'),
                'RAM%': number(host.ram_percent, 0, '%'),
                'LiveExp/s': liveExpRate,
                'Staged/s': throughput.staged_steps_per_sec != null
                    ? number(throughput.staged_steps_per_sec, 1)
                    : '-',
                'Accept/s': throughput.accepted_steps_per_sec != null
                    ? number(throughput.accepted_steps_per_sec, 1)
                    : '-',
                'OptStep/s': expRate,
                Accepted: throughput.accepted_steps_total == null ? '-' : String(throughput.accepted_steps_total),
                Consumed: throughput.learner_consumed_steps_total == null ? '-' : String(throughput.learner_consumed_steps_total),
                UploadQ: throughput.upload_queue_depth == null ? '-' : String(throughput.upload_queue_depth),
                BrokerQ: throughput.broker_queue_depth == null ? '-' :
                    String(throughput.broker_queue_depth) + '/' + String(throughput.broker_queue_capacity ?? '?'),
                SentGiB: throughput.network_sent_bytes_total != null
                    ? number(Number(throughput.network_sent_bytes_total) / (1024 ** 3), 3)
                    : '-',
                RecvGiB: throughput.network_received_bytes_total != null
                    ? number(Number(throughput.network_received_bytes_total) / (1024 ** 3), 3)
                    : '-',
                'MiB/s': throughput.network_mib_per_s != null
                    ? number(throughput.network_mib_per_s, 2)
                    : '-',
                Opt: String(optimizer.phase || '-'),
                Build: String(record.build_id || '-'),
                Rev: record.applied_revision == null ? '-' : String(record.applied_revision),
                Age: number(record.age_seconds, 1, 's'),
                Timeout: episodes && metrics.timeout_pct != null ? number(metrics.timeout_pct, 1, '%') : '-',
                BWin: episodes && metrics.bee_win_pct != null ? number(metrics.bee_win_pct, 1, '%') : '-',
                HWin: episodes && metrics.human_win_pct != null ? number(metrics.human_win_pct, 1, '%') : '-',
                Draw: episodes && metrics.draw_pct != null ? number(metrics.draw_pct, 1, '%') : '-',
                Dur: episodes && metrics.avg_duration_s != null ? number(metrics.avg_duration_s, 1, 's') : '-',
                'BHit/Sh': episodes && metrics.bee_hits_per_shot != null ? number(metrics.bee_hits_per_shot, 3) : '-',
                'HHit/Sh': episodes && metrics.human_hits_per_shot != null ? number(metrics.human_hits_per_shot, 3) : '-',
                BAim: episodes && metrics.bee_aim_error_deg != null ? number(metrics.bee_aim_error_deg, 1, 'deg') : '-',
                HAim: episodes && metrics.human_aim_error_deg != null ? number(metrics.human_aim_error_deg, 1, 'deg') : '-',
                'B<5': episodes && metrics.bee_aim_within_5_pct != null ? number(metrics.bee_aim_within_5_pct, 1, '%') : '-',
                'H<5': episodes && metrics.human_aim_within_5_pct != null ? number(metrics.human_aim_within_5_pct, 1, '%') : '-',
                'BMiss%': episodes && metrics.bee_missed_pct != null ? number(metrics.bee_missed_pct, 1, '%') : '-',
                'HMiss%': episodes && metrics.human_missed_pct != null ? number(metrics.human_missed_pct, 1, '%') : '-',
                'BNear%': episodes && metrics.bee_near_miss_pct != null ? number(metrics.bee_near_miss_pct, 1, '%') : '-',
                'HNear%': episodes && metrics.human_near_miss_pct != null ? number(metrics.human_near_miss_pct, 1, '%') : '-',
                Error: statusError(record),
            };
        });

        if (rows.length) {
            lines.push(...table(rows, [
                'Trainer', 'Role', 'Platform', 'State', 'Envs', 'vCPU', 'CPU%', 'RAM%', 'LiveExp/s', 'Staged/s', 'Accept/s', 'OptStep/s',
                'Accepted', 'Consumed', 'UploadQ', 'BrokerQ', 'SentGiB', 'RecvGiB', 'MiB/s', 'Opt', 'Build', 'Rev', 'Age',
                'Timeout', 'BWin', 'HWin', 'Draw', 'Dur', 'BHit/Sh', 'HHit/Sh',
                'BAim', 'HAim', 'B<5', 'H<5', 'BMiss%', 'HMiss%', 'BNear%', 'HNear%', 'Error',
            ]));
        } else {
            lines.push('No managed trainers/gameplay builds have checked in.');
        }

        const profiled = trainers.filter(record =>
            !record.stale &&
            record.metrics && record.metrics.throughput &&
            record.metrics.throughput.rollout_profile
        );
        for (const record of profiled) {
            const p = record.metrics.throughput.rollout_profile;
            const seconds = name => number(p[name + '_seconds'], 2, 's');
            lines.push(
                'Rollout profile ' + String(record.trainer_id) +
                ' (' + number(p.window_seconds, 1, 's') + ' window, wall time; inclusive stages overlap): ' +
                'Unity get_steps=' + seconds('unity_get_steps') +
                '  Unity first-result wait=' + seconds('unity_wait_first') +
                '  policy inference=' + seconds('policy_inference') +
                '  trajectory processing=' + seconds('trajectory_processing') +
                '  collect=' + seconds('trajectory_collection')
            );
            lines.push(
                'Rollout upload ' + String(record.trainer_id) + ': ' +
                'queue put/wait=' + seconds('upload_queue_wait') +
                '  serialize+compress=' + seconds('upload_encode') +
                '  HTTP+ack=' + seconds('upload_http') +
                '  HTTP requests=' + String(p.upload_http_count ?? '-') +
                '  HTTP connections created=' + String(p.upload_connection_created_count ?? '-')
            );
        }

        const expected = Array.isArray(config.expectedTrainers) ? config.expectedTrainers.map(String) : [];
        if (expected.length) {
            const present = new Set(trainers.map(record => String(record.trainer_id || '')));
            const missing = expected.filter(id => !present.has(id));
            if (missing.length) lines.push('WARNING: Expected trainers not connected: ' + missing.join(', '));
        }

        const runId = String(desired.run_id || '');
        const learner = getLocalLearnerStats(runId);
        const centralRunning = trainers.some(record =>
            String(record.trainer_id || '') === 'central-learner' &&
            String(record.process_state || '') === 'running' && !record.stale
        );
        const restartRate = restartWallClockRate(learner, runId, centralRunning);
        lines.push('');
        lines.push(
            'Learner logs: Step=' + (learner.Step == null ? '-' : learner.Step) +
            '  ELO=' + (learner.ELO == null ? '-' : number(learner.ELO, 1)) +
            '  MeanReward=' + (learner.MeanReward == null ? '-' : number(learner.MeanReward, 3)) +
            '  LearnerRestartAvgStep/s=' + (restartRate == null ? '-' : number(restartRate, 1)) +
            '  LearnerTailAvgStep/s=' + (learner.AverageStepsPerSecond == null ? '-' : number(learner.AverageStepsPerSecond, 1)) +
            '  LearnerLastIntervalStep/s=' + (learner.LiveStepsPerSecond == null ? '-' : number(learner.LiveStepsPerSecond, 1))
        );
        const reportingWorkers = trainers.filter(record =>
            !record.stale &&
            String(record.process_state || '') === 'running' &&
            String(record.trainer_id || '') !== 'central-learner'
        );
        const currentMetric = (record, field) => {
            const throughput = record.metrics && record.metrics.throughput;
            const raw = throughput && throughput[field];
            if (raw === undefined || raw === null) return null;
            const value = Number(raw);
            return Number.isFinite(value) && value >= 0 ? value : null;
        };
        const sumReporting = field => {
            const available = reportingWorkers
                .map(record => currentMetric(record, field))
                .filter(value => value !== null);
            return available.length ? number(available.reduce((a, b) => a + b, 0), 1) : '-';
        };
        const uploadBacklog = reportingWorkers.reduce(
            (total, record) => total + (currentMetric(record, 'upload_queue_depth') || 0), 0
        );
        const brokerReporter = reportingWorkers
            .filter(record => currentMetric(record, 'broker_queue_depth') !== null)
            .sort((a, b) => Number(a.age_seconds ?? Infinity) - Number(b.age_seconds ?? Infinity))[0];
        const broker = brokerReporter && brokerReporter.metrics.throughput;
        if (broker && broker.broker_pending_remote_experience_limit != null) {
            lines.push(
                'WAN experience memory budget (broker + ML-Agents queues): ' +
                String(broker.broker_pending_remote_experiences ?? '-') + '/' +
                String(broker.broker_pending_remote_experience_limit) +
                ' pending remote experiences; excess uploads receive retriable backpressure.'
            );
        }
        const brokerLabel = broker ? (
            String(broker.broker_queue_depth) + '/' +
            String(broker.broker_queue_capacity ?? '?')
        ) : '-';
        lines.push(
            'Pipeline (active remote workers): staged=' + sumReporting('staged_steps_per_sec') +
            ' exp/s  accepted=' + sumReporting('accepted_steps_per_sec') +
            ' exp/s  consumed=' + sumReporting('learner_consumed_steps_per_sec') +
            ' exp/s  uploads=' + uploadBacklog + '  broker=' + brokerLabel +
            '  global learner(last interval)=' + number(learner.LiveStepsPerSecond, 1) +
            ' exp/s  learner since restart=' + (restartRate == null ? '-' : number(restartRate, 1)) + ' exp/s'
        );
        if (broker && broker.broker_accepted_team_0_steps_total != null) {
            const count = field => broker[field] == null ? '-' : String(broker[field]);
            lines.push(
                'Self-play experience totals (since learner start): ' +
                'broker accepted team0=' + count('broker_accepted_team_0_steps_total') +
                '  team1=' + count('broker_accepted_team_1_steps_total') +
                '  other=' + count('broker_accepted_other_steps_total') +
                '  remote delivered to PPO=' + count('broker_ppo_delivered_steps_total') +
                '  ghost discarded (local + remote)=' + count('broker_ghost_discarded_steps_total') +
                '. Team IDs change learning/ghost roles during self-play; ' +
                'PPO delivery requires threaded version-tagged trajectories.'
            );
            lines.push(
                'Self-play flow (sampled): useful PPO remote=' +
                number(broker.broker_ppo_delivered_steps_per_sec, 1) +
                ' exp/s  ghost disposed (local + remote)=' +
                number(broker.broker_ghost_discarded_steps_per_sec, 1) + ' exp/s.'
            );
            lines.push(
                'Learner queues (trajectory counts): ghost input=' +
                count('broker_ghost_input_queue_depth') +
                '  PPO input=' + count('broker_learning_queue_depth') +
                '  PPO staged experiences=' + count('broker_poca_staged_experiences') +
                '  PPO update buffer experiences=' + count('broker_poca_update_buffer_experiences') +
                '  updates in flight=' + count('broker_ppo_updates_in_flight') + '.'
            );
        }
        lines.push(...ppoStatusLines(
            getLatestPpoUpdate(String(desired.run_id || '')),
            { uploadBacklog, broker }
        ));
        lines.push(
            'Rates: Staged/s counts queued complete trajectories, Accept/s broker-accepted experience, ' +
            'LiveExp/s counts dequeued experience from BOTH self-play teams before ghost filtering. ' +
            'OptStep/s is an environment auto-tuner probe, NOT gradient steps/s. ' +
            'Rates use different sampling windows; queues are snapshots, not trends.'
        );
        lines.push(
            'Network: SentGiB/RecvGiB are cumulative per-run WAN payload bytes; MiB/s is the current payload rate when a live actor session is available.'
        );
    } catch (error) {
        lines.push('');
        lines.push('Dashboard: RENDER ERROR - ' + error.name + ': ' + error.message);
        lines.push(
            'Control endpoint: RESPONDED. The server is reachable; only this status snapshot failed to render completely.'
        );
    }

    return lines;
}

async function showStatus(config, adminToken, single, refreshSeconds = 2) {
    const render = async () => {
        const lines = await getStatusFrameLines(config, adminToken);
        process.stdout.write(lines.join('\n') + '\n');
    };
    if (single || !process.stdout.isTTY) {
        await render();
        return;
    }

    while (true) {
        const lines = await getStatusFrameLines(config, adminToken);
        process.stdout.write('\x1b[2J\x1b[H');
        process.stdout.write(lines.join('\n') + '\n\n');
        process.stdout.write('Refreshing every ' + refreshSeconds + ' s. Ctrl+C to stop.\n');
        await sleep(refreshSeconds * 1000);
    }
}

module.exports = {
    getLocalLearnerStats,
    getLatestPpoUpdate,
    getStatusFrameLines,
    rolloutBlockers,
    showStatus,
    statusError,
    table,
};
