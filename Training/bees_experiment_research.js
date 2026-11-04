'use strict';
const crypto = require('node:crypto');

// Aggregate trained models, never correlated individual matches or shots,
// as independent observations. Comparability is checked before pooling.
const T95 = [null, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365,
    2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120,
    2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060,
    2.056, 2.052, 2.048, 2.045, 2.042];

function statistics(values) {
    const n = values.length;
    if (!n) return { n: 0, mean: null, sd: null, ci95: null };
    const mean = values.reduce((total, x) => total + x, 0) / n;
    if (n === 1) return { n, mean, sd: null, ci95: null };
    const sd = Math.sqrt(values.reduce((total, x) => total + (x - mean) ** 2, 0) / (n - 1));
    const t = T95[n - 1] || 1.96;
    const delta = t * sd / Math.sqrt(n);
    return { n, mean, sd, ci95: [mean - delta, mean + delta] };
}
function signature(item) {
    const resolved = item.trainer_config?.resolved || {};
    const shape = {
        trainer_type: resolved.trainer_type,
        hyperparameters: resolved.hyperparameters,
        network_settings: resolved.network_settings,
        stages: item.stages,
        release_build: item.release_build,
        evaluation_mode: item.evaluation_mode,
        execution_options: item.execution_options,
    };
    return crypto.createHash('sha256').update(JSON.stringify(shape))
        .digest('hex').slice(0, 16);
}
function references(item) {
    const references = [];
    for (const checkpoint of item.checkpoint_history || []) {
        if (checkpoint.evaluation?.status !== 'completed') continue;
        for (const [scenario, file] of Object.entries(checkpoint.evaluation.reports || {})) {
            references.push({ scenario, file, step: checkpoint.target_step,
                label: 'Periodic ' + checkpoint.ordinal, purpose: 'development' });
        }
    }
    for (const [index, stage] of (item.stage_history || []).entries()) {
        if (stage?.evaluation?.status !== 'completed') continue;
        for (const [scenario, file] of Object.entries(stage.evaluation.reports || {})) {
            references.push({ scenario, file, step: item.stages?.[index]?.end_step ?? stage.finished_step,
                label: 'Stage ' + (index + 1), purpose: 'development' });
        }
    }
    if (item.benchmark_report && item.benchmark_status === 'completed')
        references.push({ scenario: null, file: item.benchmark_report, step: item.max_steps,
            label: 'Final', purpose: 'development' });
    if (item.heldout_validation?.status === 'completed') {
        for (const [scenario, file] of Object.entries(item.heldout_validation.evaluation?.reports || {})) {
            references.push({ scenario, file, step: 'heldout-final',
                label: 'Held-out final', purpose: 'heldout' });
        }
    }
    return references;
}
function summarize(items, reports) {
    const reportByName = new Map(reports.map(report => [report._file, report]));
    const groups = new Map();
    for (const item of items) {
        if (!item.run_id || !item.trainer_config) continue;
        const condition = signature(item);
        for (const reference of references(item)) {
            const report = reportByName.get(reference.file);
            if (!report || report.run_id !== item.run_id ||
                !report.metrics?.telemetry_validated ||
                (report.experiment?.id && report.experiment.id !== item.id)) continue;
            const m = report.metrics;
            const benchmark = report.benchmark || {};
            const identity = [
                report.build_id, report.environment_executable_sha256,
                report.evaluation_code_sha256, benchmark.sha256,
                report.opponent_sha256, report.opponent_script_sha256 || '',
            ].join(':');
            const groupId = item.study_id || item.id;
            const key = JSON.stringify([groupId, condition, reference.step,
                reference.purpose, identity]);
            if (!groups.has(key)) groups.set(key, {
                study_id: item.study_id || null,
                study_label: item.study_label || item.name,
                expected_replicates: item.replicate_count || 1,
                comparison_group: item.group,
                condition_id: condition,
                beta: item.trainer_config.resolved?.hyperparameters?.beta ?? null,
                scenario: benchmark.id || reference.scenario,
                checkpoint: reference.label,
                target_step: reference.step,
                purpose: reference.purpose,
                benchmark_identity: identity,
                observations: new Map(),
            });
            const group = groups.get(key);
            if (group.observations.has(item.id)) continue;
            group.observations.set(item.id, {
                experiment_id: item.id, run_id: item.run_id,
                seed: item.trainer_config.resolved?.seed ?? null,
                actual_step: report.experiment?.candidate_training_step ?? null,
                win_rate: Number(m.win_rate),
                timeout_rate: Number(m.timeout_rate),
                score_rate: Number(m.score_rate),
                matches: m.matches,
            });
        }
    }
    return [...groups.values()].map(group => {
        const observations = [...group.observations.values()];
        return {
            ...Object.fromEntries(Object.entries(group).filter(([key]) => key !== 'observations')),
            runs: observations,
            win_rate: statistics(observations.map(x => x.win_rate)),
            timeout_rate: statistics(observations.map(x => x.timeout_rate)),
            score_rate: statistics(observations.map(x => x.score_rate)),
            inference: 'Student-t intervals across independent training runs only; descriptive, not a causal test',
        };
    }).sort((a,b) => a.comparison_group.localeCompare(b.comparison_group) ||
        a.study_label.localeCompare(b.study_label) ||
        String(a.target_step).localeCompare(String(b.target_step)) ||
        String(a.scenario).localeCompare(String(b.scenario)));
}
module.exports = { statistics, summarize, references };
