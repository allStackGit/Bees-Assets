'use strict';

// Bounded read-only projection of Ants-v2's production summary.json.
// Never read training CSV/JSONL histories or infer qualification outcomes.
const fs=require('node:fs');
const path=require('node:path');
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const EXPERIMENT=/^[A-Za-z0-9_-]{1,80}$/;
const MAX_SUMMARY_BYTES=512*1024;
const nonnegative=x=>Number.isSafeInteger(x)&&x>=0;
const ratio=x=>typeof x==='number'&&Number.isFinite(x)&&x>=0&&x<=1;
const rate=x=>typeof x==='number'&&Number.isFinite(x)&&x>=0&&x<=1e12;
function numberOrNull(x,test){return test(x)?x:null;}

// A fresh job root contains one Ants experiment. Full-state continuation
// preserves its experiment identity and metric directory. More than one
// directory is ambiguous; do not silently choose an unrelated experiment.
function readAntsProgress(artifactRoot,rootId,fsApi=fs) {
    if(typeof artifactRoot!=='string'||!path.isAbsolute(artifactRoot)||
        !UUID.test(String(rootId||'')))
        throw Error('Invalid trusted Ants metric lineage.');
    const experiments=path.join(artifactRoot,rootId,'experiments');
    let dirs;
    try {
        dirs=fsApi.readdirSync(experiments,{withFileTypes:true});
    }catch(error) {
        if(error.code==='ENOENT')return null;
        throw Error('Cannot inspect Ants production experiment metrics.');
    }
    if(dirs.length>32)throw Error('Ambiguous Ants metrics directory population.');
    const candidates=dirs.filter(d=>d.isDirectory()&&EXPERIMENT.test(d.name));
    if(candidates.length===0)return null;
    if(candidates.length!==1||candidates.length!==dirs.length)
        throw Error('Multiple or unrecognized Ants production metric directories.');
    const dir=path.join(experiments,candidates[0].name);
    const summary=path.join(dir,'summary.json');
    let stat;
    try{stat=fsApi.statSync(summary);}
    catch(error){
        if(error.code==='ENOENT')return null;
        throw Error('Cannot inspect Ants production summary.');
    }
    if(!stat.isFile()||!Number.isSafeInteger(stat.size)||stat.size<2||
        stat.size>MAX_SUMMARY_BYTES)
        throw Error('Ants production summary is not a bounded regular file.');
    let data;
    try{
        const lineageRoot=fsApi.realpathSync(path.join(artifactRoot,rootId));
        const canonicalRoot=fsApi.realpathSync(experiments);
        const canonical=fsApi.realpathSync(summary);
        if(!canonicalRoot.startsWith(lineageRoot+path.sep)||
            !canonical.startsWith(canonicalRoot+path.sep))
            throw Error('Ants metrics escaped the approved artifact lineage.');
        data=JSON.parse(fsApi.readFileSync(summary,'utf8'));
    }catch{
        throw Error('Ants production summary is unreadable, incomplete or outside the approved root.');
    }
    if(!data||typeof data!=='object'||Array.isArray(data)||
        data.experiment_id!==candidates[0].name||
        typeof data.segment_id!=='string'||
        !/^segment-[0-9]{4,12}$/.test(data.segment_id)||
        !nonnegative(data.episodes_completed)||
        !nonnegative(data.latest_policy_version)||
        !nonnegative(data.highest_curriculum_level))
        throw Error('Ants production summary does not match its experiment lineage.');
    // Ants summary() success_rate is cumulative within the metrics
    // segment. curriculum.success_rate is the CURRENT rolling window.
    const curriculum=data.curriculum&&typeof data.curriculum==='object'?
        data.curriculum:{};
    const performance=data.performance&&typeof data.performance==='object'?
        data.performance:{};
    return {
        schema_version:1,source:'ants-summary-v1',
        experiment_id:data.experiment_id,segment_id:data.segment_id,
        source_modified_at:new Date(stat.mtimeMs).toISOString(),
        episodes_completed:data.episodes_completed,
        policy_version:data.latest_policy_version,
        curriculum_level:data.highest_curriculum_level,
        curriculum_window_episodes:
            numberOrNull(curriculum.completed_in_window,nonnegative),
        curriculum_window_success_rate:
            numberOrNull(curriculum.success_rate,ratio),
        segment_success_rate:numberOrNull(data.success_rate,ratio),
        total_environment_steps:
            numberOrNull(data.total_environment_steps,nonnegative),
        total_optimization_steps:
            numberOrNull(data.total_optimization_steps,nonnegative),
        simulation_steps_per_second:
            numberOrNull(performance.simulation_steps_per_second,rate),
        ppo_unique_samples_per_second:
            numberOrNull(performance.ppo_unique_samples_per_second,rate),
    };
}
module.exports={readAntsProgress};
