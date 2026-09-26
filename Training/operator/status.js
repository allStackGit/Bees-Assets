'use strict';

const fs = require('node:fs');
const path = require('node:path');

const {
    paths,
    readTail,
    requestJson,
    sleep,
} = require('./common');

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

function getLocalLearnerStats(runId = '') {
    const files = [];
    const operatorLogRoot = path.join(paths.logsRoot, 'Training');
    files.push(...listLogFiles(operatorLogRoot, false));

    let trainerResultsRoot = path.join(paths.trainingRoot, 'trainer-results');
    if (runId) trainerResultsRoot = path.join(trainerResultsRoot, runId);
    files.push(...listLogFiles(trainerResultsRoot, true));

    const selected = files
        .sort((a, b) => b.mtimeMs - a.mtimeMs)
        .slice(0, 24)
        .sort((a, b) => a.mtimeMs - b.mtimeMs || a.full.localeCompare(b.full));

    let elo = null;
    let step = null;
    let reward = null;
    let averageStepsPerSecond = null;
    let liveStepsPerSecond = null;

    for (const file of selected) {
        let firstStep = null;
        let firstElapsed = null;
        let previousStep = null;
        let previousElapsed = null;
        let fileAverage = null;
        let fileLive = null;

        for (const line of readTail(file.full, 1000, 2 * 1024 * 1024)) {
            let match;
            if ((match = line.match(/\bELO\b[^-0-9]*(-?\d+(?:\.\d+)?)/i))) {
                elo = Number(match[1]);
            }
            let lineStep = null;
            let lineElapsed = null;
            if ((match = line.match(/\bStep\s*[:=]\s*(\d+)/i))) {
                lineStep = Number(match[1]);
                step = lineStep;
            }
            if ((match = line.match(/Mean Reward\s*[:=]\s*(-?\d+(?:\.\d+)?)/i))) {
                reward = Number(match[1]);
            }
            if ((match = line.match(/Time Elapsed\s*[:=]\s*(\d+(?:\.\d+)?)\s*s/i))) {
                lineElapsed = Number(match[1]);
            }

            if (lineStep !== null && lineElapsed !== null) {
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

function number(value, digits = 1, suffix = '') {
    if (value === null || value === undefined || !Number.isFinite(Number(value))) return '-';
    return Number(value).toFixed(digits) + suffix;
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
        lines.push(
            'Cluster: local_envs=' + config.numLocalEnvs +
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
            const episodes = Number(metrics.window_episodes || 0);
            return {
                Trainer: String(record.trainer_id || '-'),
                Role: String(record.role || '-'),
                Platform: String(record.platform || '-'),
                State: record.stale ? 'STALE' : String(record.process_state || '-'),
                Envs: envDisplay,
                'OptExp/s': expRate,
                SentGiB: throughput.network_sent_bytes_total != null
                    ? number(Number(throughput.network_sent_bytes_total) / (1024 ** 3), 2)
                    : '-',
                RecvGiB: throughput.network_received_bytes_total != null
                    ? number(Number(throughput.network_received_bytes_total) / (1024 ** 3), 2)
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
                Error: String(record.last_error || ''),
            };
        });

        if (rows.length) {
            lines.push(...table(rows, [
                'Trainer', 'Role', 'Platform', 'State', 'Envs', 'OptExp/s',
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
            'Rates: OptExp/s is the last per-worker optimizer consumption sample; learner Step/s is the global ML-Agents training-step rate.'
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
    table,
};
