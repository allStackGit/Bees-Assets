'use strict';

// Locally constructed, trusted adapter for Ants-v2's production CLI.
// The actual project checkout, Python environment, config and final
// qualification report are selected by the host administrator, never
// by a Hub experiment JSON document.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');

const SAFE_PATH=/^[A-Za-z0-9_./:@=+\-,]+$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HASH=/^[0-9a-f]{64}$/;
const MAX_EPISODES=100000000;

function verifiedFile(file,expectedSha256,fsApi=fs) {
    if(!HASH.test(String(expectedSha256||'')))
        throw Error('Pinned Ants config/report SHA-256 is required.');
    let current;
    try{current=fsApi.readFileSync(file);}catch{
        throw Error('Pinned Ants config/report is not readable on this host.');
    }
    const digest=crypto.createHash('sha256').update(current).digest('hex');
    if(digest!==expectedSha256)
        throw Error('Pinned Ants config/report content changed; revalidate qualification and adapter.');
}
function createAntsProductionAdapter(options={}) {
    const {python,projectRoot,configPath,qualificationReport,
        artifactRoot,configSha256,qualificationSha256}=options;
    for(const [label,value] of Object.entries({
        python,projectRoot,configPath,qualificationReport,artifactRoot,
    })) {
        if(typeof value!=='string'||!path.isAbsolute(value)||
            !SAFE_PATH.test(value)||value.length>512)
            throw Error('Ants '+label+' must be a safe absolute Linux-local path.');
    }
    if(!HASH.test(String(configSha256||''))||
        !HASH.test(String(qualificationSha256||'')))
        throw Error('Pinned Ants config and qualification report digests are required.');
    const sourceRoot=projectRoot.replace(/\/+$/,'');
    if(projectRoot==='/'||artifactRoot==='/'||
        artifactRoot===sourceRoot||artifactRoot.startsWith(sourceRoot+'/'))
        throw Error('Ants artifact parent must be outside the source checkout to preserve clean source identity.');
    const fsApi=options.fsApi||fs;
    const check=()=>{
        verifiedFile(configPath,configSha256,fsApi);
        verifiedFile(qualificationReport,qualificationSha256,fsApi);
    };
    // Fail during local opt-in, not after the dashboard has accepted a job.
    check();
    return Object.freeze({
        executable:python,
        cwd:projectRoot,
        stop_policy:'interrupt-main',
        args(frozen,context) {
            if(!frozen||typeof frozen!=='object'||Array.isArray(frozen)||
                Object.keys(frozen).some(key=>key!=='episodes'&&key!=='mode')||
                (frozen.mode!==undefined&&frozen.mode!=='new')||
                !Number.isSafeInteger(frozen.episodes)||
                frozen.episodes<1||frozen.episodes>MAX_EPISODES||
                !context||!UUID.test(String(context.job_id||''))||
                context.project_id!=='ants')
                throw Error('Expected a fresh, bounded production Ants job with an episode count.');
            check();
            // Prevent concurrent or resumed jobs from silently reusing an
            // existing experiment's checkpoint, metrics or rollback history.
            const root=path.join(artifactRoot,context.job_id);
            return ['-m','antsim','train',
                '--config',configPath,
                '--artifact-root',root,
                '--qualification-report',qualificationReport,
                '--checkpoint-on-exit',
                '--episodes',String(frozen.episodes)];
        },
    });
}
module.exports={createAntsProductionAdapter};
