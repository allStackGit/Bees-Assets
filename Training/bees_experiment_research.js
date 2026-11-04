'use strict';
const crypto = require('node:crypto');

// Aggregate trained models, never correlated individual matches or shots,
// as independent observations. Comparability is checked before pooling.
const T95 = [null, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365,
    2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120,
    2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060,
    2.056, 2.052, 2.048, 2.045, 2.042];

function tCritical(df) {
    const z = 1.959963984540054;
    if (df <= 30) return T95[Math.max(1, Math.floor(df))];
    return z + (z ** 3 + z) / (4 * df) +
        (5 * z ** 5 + 16 * z ** 3 + 3 * z) / (96 * df ** 2);
}

function statistics(values) {
    const n = values.length;
    if (!n) return { n: 0, mean: null, sd: null, ci95: null };
    const mean = values.reduce((total, x) => total + x, 0) / n;
    if (n === 1) return { n, mean, sd: null, ci95: null };
    const sd = Math.sqrt(values.reduce((total, x) => total + (x - mean) ** 2, 0) / (n - 1));
    const t = tCritical(n - 1);
    const delta = t * sd / Math.sqrt(n);
    return { n, mean, sd, ci95: [mean - delta, mean + delta] };
}
function signature(item, omitBeta = false) {
    const resolved = item.trainer_config?.resolved || {};
    const shape = {
        trainer_type: resolved.trainer_type,
        hyperparameters: omitBeta
            ? Object.fromEntries(Object.entries(resolved.hyperparameters || {})
                .filter(([key]) => key !== 'beta'))
            : resolved.hyperparameters,
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
        const comparisonSignature = signature(item, true);
        for (const reference of references(item)) {
            const report = reportByName.get(reference.file);
            if (!report || report.run_id !== item.run_id ||
                !report.metrics?.telemetry_validated ||
                (report.experiment?.id && report.experiment.id !== item.id)) continue;
            const m = report.metrics;
            if (![m.win_rate, m.timeout_rate, m.score_rate]
                .every(value => typeof value === 'number' && Number.isFinite(value))) continue;
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
                comparability_id: comparisonSignature,
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

// Unpaired Welch comparisons use each trained network as one observation.
// Similar seeds alone do not establish paired trials when Unity scenarios and
// distributed worker scheduling are separately stochastic.
function differences(groups) {
    const buckets = new Map();
    for (const g of groups) {
        if (g.beta == null) continue;
        const key = JSON.stringify([g.comparison_group, g.comparability_id,
            g.benchmark_identity, g.target_step, g.purpose, g.scenario]);
        if (!buckets.has(key)) buckets.set(key, []);
        buckets.get(key).push(g);
    }
    const out = [];
    for (const bucket of buckets.values()) {
        for (let i = 0; i < bucket.length; i++) {
            for (let j = i + 1; j < bucket.length; j++) {
                const a = bucket[i], b = bucket[j];
                if (a.beta === b.beta) continue;
                const nA = a.win_rate.n, nB = b.win_rate.n;
                const difference = a.win_rate.mean - b.win_rate.mean;
                let ci95 = null, standardError = null;
                if (nA >= 2 && nB >= 2) {
                    const va = (a.win_rate.sd ** 2) / nA;
                    const vb = (b.win_rate.sd ** 2) / nB;
                    standardError = Math.sqrt(va + vb);
                    if (standardError === 0) ci95 = [difference,difference];
                    else {
                        const df = (va + vb) ** 2 /
                            (va ** 2 / (nA - 1) + vb ** 2 / (nB - 1));
                        const margin = tCritical(df) * standardError;
                        ci95 = [difference - margin, difference + margin];
                    }
                }
                out.push({
                    comparison_group: a.comparison_group, scenario: a.scenario,
                    purpose: a.purpose, target_step: a.target_step,
                    benchmark_identity: a.benchmark_identity,
                    comparability_id: a.comparability_id,
                    condition_a: a.condition_id, condition_b: b.condition_id,
                    study_a: a.study_label, study_b: b.study_label,
                    beta_a: a.beta, beta_b: b.beta,
                    independent_runs_a: nA, independent_runs_b: nB,
                    win_rate_difference: difference,
                    standard_error: standardError, win_difference_ci95: ci95,
                    interpretation: ci95 ? 'Approximate unpaired Welch 95% interval across trained networks'
                        : 'Descriptive only; at least two independent runs per condition required',
                });
            }
        }
    }
    return out;
}

// A threshold is observed at discrete evaluation boundaries. Below-threshold
// and unevaluated runs are explicitly censored, never assigned zero or infinity.
function sampleEfficiency(groups, threshold = 0.5) {
    if (typeof threshold !== 'number' || !Number.isFinite(threshold) ||
        threshold < 0 || threshold > 1) throw Error('Threshold must be 0–1.');
    const grouped = new Map();
    for (const group of groups) {
        if (group.purpose !== 'development' || !Number.isSafeInteger(group.target_step))
            continue;
        const key = JSON.stringify([group.comparison_group, group.study_id,
            group.condition_id, group.benchmark_identity, group.scenario]);
        if (!grouped.has(key)) grouped.set(key, { template: group, runs: new Map() });
        const bucket = grouped.get(key);
        for (const run of group.runs) {
            const earlier = bucket.runs.get(run.run_id);
            if (run.win_rate >= threshold &&
                (!earlier || group.target_step < earlier)) {
                bucket.runs.set(run.run_id, group.target_step);
            }
        }
    }
    return [...grouped.values()].map(({template, runs}) => {
        const reachedSteps = [...runs.values()].sort((a,b)=>a-b);
        const median = !reachedSteps.length ? null :
            reachedSteps.length % 2
                ? reachedSteps[(reachedSteps.length - 1) / 2]
                : (reachedSteps[reachedSteps.length / 2 - 1] +
                    reachedSteps[reachedSteps.length / 2]) / 2;
        return {
            study_id: template.study_id, study_label: template.study_label,
            comparison_group: template.comparison_group,
            condition_id: template.condition_id, beta: template.beta,
            scenario: template.scenario, benchmark_identity: template.benchmark_identity,
            threshold, expected_replicates: template.expected_replicates,
            reached_runs: reachedSteps.length,
            earliest_evaluated_steps: reachedSteps,
            median_first_observed_step: median,
            interpretation: 'First qualifying evaluated checkpoint; not an exact mastery step',
        };
    });
}
module.exports = { statistics, summarize, references, differences, sampleEfficiency };
