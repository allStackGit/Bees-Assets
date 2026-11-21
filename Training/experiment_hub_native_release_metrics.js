'use strict';

// Bounded projection of a registry-committed native physical evaluation.
// Timeouts are draws without score credit, not a fourth outcome class.
const SHA=/^[a-f0-9]{64}$/;
const MODEL=/^bees-rl-v[0-9]+-[0-9a-f]{24}$/;
const COUNTS=['champion_matches','champion_wins','champion_losses','champion_draws',
    'champion_timeouts','historical_opponents','historical_candidate_matches',
    'competency_cases','competency_cases_passed','candidate_inference_calls'];

function validateReleaseMeasurements(release) {
    const summary=release.evaluation_summary;
    if(summary===undefined)return null;
    const keys=['report_sha256','evaluated_champion_model_id',...COUNTS,
        'candidate_max_inference_milliseconds','latency_passed','champion_gate_short_circuited',
        ...(summary?.historical_min_score_delta!==undefined?['historical_min_score_delta']:[])];
    if(!summary||typeof summary!=='object'||Array.isArray(summary)||
        Object.keys(summary).sort().join(',')!==keys.sort().join(',')||
        !SHA.test(String(summary.report_sha256||''))||
        release.evaluation_report_id!=='eval-'+summary.report_sha256.slice(0,24)||
        !MODEL.test(String(summary.evaluated_champion_model_id||''))||
        COUNTS.some(key=>!Number.isSafeInteger(summary[key])||
            summary[key]<0||summary[key]>1e12)||
        summary.champion_matches<1||
        summary.champion_matches!==summary.champion_wins+summary.champion_losses+summary.champion_draws||
        summary.champion_timeouts>summary.champion_draws||
        summary.competency_cases_passed>summary.competency_cases||
        summary.historical_candidate_matches<summary.historical_opponents||
        (summary.historical_opponents>0)!==(summary.historical_min_score_delta!==undefined)||
        (summary.historical_min_score_delta!==undefined&&
            (!Number.isFinite(summary.historical_min_score_delta)||Math.abs(summary.historical_min_score_delta)>1))||
        !Number.isFinite(summary.candidate_max_inference_milliseconds)||
        summary.candidate_max_inference_milliseconds<0||
        summary.candidate_max_inference_milliseconds>1e12||
        typeof summary.latency_passed!=='boolean'||
        typeof summary.champion_gate_short_circuited!=='boolean'||
        summary.champion_gate_short_circuited&&
            (summary.historical_opponents!==0||summary.competency_cases!==0||release.passed)||
        release.passed&&!summary.latency_passed)
        throw Error('Native continual evaluation measurements disagree with the committed release.');
    return summary;
}

function championScore(summary) {
    return (summary.champion_wins+0.5*(summary.champion_draws-summary.champion_timeouts))/
        summary.champion_matches;
}

function releaseProgressMetrics(release) {
    const summary=validateReleaseMeasurements(release);
    if(!summary)return {};
    const metric=(value,unit)=>({value,unit});
    return {
        continual_champion_matches:metric(summary.champion_matches,'count'),
        continual_champion_score_rate:metric(championScore(summary),'ratio'),
        continual_champion_win_rate:metric(summary.champion_wins/summary.champion_matches,'ratio'),
        continual_champion_timeout_rate:metric(summary.champion_timeouts/summary.champion_matches,'ratio'),
        continual_evaluated_historical_opponents:metric(summary.historical_opponents,'count'),
        continual_historical_evaluation_matches:metric(summary.historical_candidate_matches,'count'),
        ...(summary.historical_min_score_delta===undefined?{}:{
            continual_historical_min_score_delta:metric(summary.historical_min_score_delta,'scalar'),
        }),
        continual_competency_cases:metric(summary.competency_cases,'count'),
        continual_competency_cases_passed:metric(summary.competency_cases_passed,'count'),
        continual_candidate_inference_calls:metric(summary.candidate_inference_calls,'count'),
        continual_candidate_max_inference_ms:metric(summary.candidate_max_inference_milliseconds,'milliseconds'),
        continual_latency_passed:metric(Number(summary.latency_passed),'count'),
        continual_champion_gate_short_circuited:metric(Number(summary.champion_gate_short_circuited),'count'),
    };
}

module.exports={validateReleaseMeasurements,championScore,releaseProgressMetrics};
