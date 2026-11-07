'use strict';

// Opt-in development-benchmark stopping rule for Bees. This module does not
// read training telemetry or authorize execution; callers must first verify
// the frozen evaluator/candidate/recipe identity of every result.
function normalizeTarget(raw,mode) {
    if(raw==null)return null;
    if(!raw||typeof raw!=='object'||Array.isArray(raw)||
        Object.keys(raw).some(k=>![
            'min_win_rate_pct','max_timeout_rate_pct',
            'min_matches','consecutive_passes',
        ].includes(k)))
        throw Error('Invalid development benchmark success target.');
    if(mode!=='periodic')
        throw Error('Automatic benchmark completion requires periodic evaluation.');
    const win=raw.min_win_rate_pct,timeout=raw.max_timeout_rate_pct;
    if(typeof win!=='number'||!Number.isFinite(win)||win<0||win>100||
        typeof timeout!=='number'||!Number.isFinite(timeout)||
        timeout<0||timeout>100||
        !Number.isSafeInteger(raw.min_matches)||raw.min_matches<30||
        raw.min_matches>10000||
        !Number.isSafeInteger(raw.consecutive_passes)||
        raw.consecutive_passes<1||raw.consecutive_passes>4)
        throw Error('Benchmark target requires win/timeout limits of 0–100%, at least 30 matches, and 1–4 consecutive checkpoint passes.');
    return {min_win_rate_pct:win,max_timeout_rate_pct:timeout,
        min_matches:raw.min_matches,consecutive_passes:raw.consecutive_passes};
}

function scoreTarget(target,scenarios) {
    if(!target||!Array.isArray(scenarios)||scenarios.length<1||
        scenarios.length>12)
        throw Error('Missing frozen development benchmark target or scenarios.');
    const seen=new Set();
    const results=scenarios.map(item=>{
        if(!item||typeof item.id!=='string'||seen.has(item.id)||
            !Number.isSafeInteger(item.expected_matches)||
            item.expected_matches<target.min_matches||
            !item.report||item.report.evaluation_purpose!=='development')
            throw Error('Invalid development benchmark identity, match count or evaluation purpose.');
        seen.add(item.id);
        const m=item.report.metrics;
        const nonnegativeInt=x=>Number.isSafeInteger(x)&&x>=0;
        if(!m||m.telemetry_validated!==true||
            !['matches','wins','losses','draws','timeouts'].every(k=>
                nonnegativeInt(m[k]))||
            m.matches!==item.expected_matches||
            m.wins+m.losses+m.draws!==m.matches||
            m.timeouts>m.draws)
            throw Error('Development benchmark match totals are inconsistent.');
        // Compute ratios from the validated integer totals, not rounded or
        // independently supplied rate fields.
        const winPct=100*m.wins/m.matches;
        const timeoutPct=100*m.timeouts/m.matches;
        const passed=winPct>=target.min_win_rate_pct&&
            timeoutPct<=target.max_timeout_rate_pct;
        return {id:item.id,matches:m.matches,wins:m.wins,
            timeouts:m.timeouts,win_rate_pct:winPct,
            timeout_rate_pct:timeoutPct,passed};
    });
    return {passed:results.every(r=>r.passed),scenarios:results};
}
module.exports={normalizeTarget,scoreTarget};
