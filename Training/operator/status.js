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

function parseLearnerLogFiles(files) {
    let elo = null;
    let step = null;
    let reward = null;
    let averageStepsPerSecond = null;
    let liveStepsPerSecond = null;

    for (const file of files) {
        let firstStep = null;
        let firstElapsed = null;
        let previousStep = null;
        let previousElapsed = null;
        let fileAverage = null;
        let fileLive = null;

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
    }

    return {
        ELO: elo,
        Step: step,
        MeanReward: reward,
        AverageStepsPerSecond: averageStepsPerSecond,
        LiveStepsPerSecond: liveStepsPerSecond,
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
    const instabilityMs = Number(optimizer && optimizer.last_instability_ms);
    const instabilityReason = String(
        optimizer && (optimizer.last_instability_reason || optimizer.decision) || ''
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
    return historical.length ? historical[0].text : '';
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
            ' max_remote=' + config.maxRemoteActors +
            ' broker_port=' + config.brokerPort
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
                'LiveExp/s': liveExpRate,
                'OptExp/s': expRate,
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
                BAligned: episodes && metrics.bee_turret_aligned_pct != null ? number(metrics.bee_turret_aligned_pct, 1, '%') : '-',
                HAligned: episodes && metrics.human_turret_aligned_pct != null ? number(metrics.human_turret_aligned_pct, 1, '%') : '-',
                Error: statusError(record),
            };
        });

        if (rows.length) {
            lines.push(...table(rows, [
                'Trainer', 'Role', 'Platform', 'State', 'Envs', 'LiveExp/s', 'OptExp/s',
                'SentGiB', 'RecvGiB', 'MiB/s', 'Opt', 'Build', 'Rev', 'Age',
                'Timeout', 'BWin', 'HWin', 'Draw', 'Dur', 'BHit/Sh', 'HHit/Sh',
                'BAim', 'HAim', 'B<5', 'H<5', 'BAligned', 'HAligned', 'Error',
            ]));
        } else {
            lines.push('No managed trainers/gameplay builds have checked in.');
        }

        const expected = Array.isArray(config.expectedTrainers) ? config.expectedTrainers.map(String) : [];
        if (expected.length) {
            const present = new Set(trainers.map(record => String(record.trainer_id || '')));
            const missing = expected.filter(id => !present.has(id));
            if (missing.length) lines.push('WARNING: Expected trainers not connected: ' + missing.join(', '));
        }

        const learner = getLocalLearnerStats(String(desired.run_id || ''));
        lines.push('');
        lines.push(
            'Learner logs: Step=' + (learner.Step == null ? '-' : learner.Step) +
            '  ELO=' + (learner.ELO == null ? '-' : number(learner.ELO, 1)) +
            '  MeanReward=' + (learner.MeanReward == null ? '-' : number(learner.MeanReward, 3)) +
            '  LearnerAvgStep/s=' + (learner.AverageStepsPerSecond == null ? '-' : number(learner.AverageStepsPerSecond, 1)) +
            '  LearnerLiveStep/s=' + (learner.LiveStepsPerSecond == null ? '-' : number(learner.LiveStepsPerSecond, 1))
        );
        lines.push(
            'Rates: LiveExp/s is recent per-worker learner-consumed experience; OptExp/s is the optimizer measurement-window sample; learner Step/s is the global ML-Agents training-step rate.'
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
    getStatusFrameLines,
    rolloutBlockers,
    showStatus,
    statusError,
    table,
};
