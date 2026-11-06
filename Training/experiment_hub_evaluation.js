'use strict';

const fs=require('node:fs');
const path=require('node:path');

// Independent evaluation is a separate adapter contract from training progress.
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA256=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const trials=n=>Number.isSafeInteger(n)&&n>=1&&n<=1000000;
const record=value=>value&&typeof value==='object'&&!Array.isArray(value);

function evaluationPolicy(value) {
    if(value==null)return null;
    if(!record(value)||Object.keys(value).some(key=>
        !['suite_id','suite_sha256','min_trials','min_success_rate'].includes(key))||
        typeof value.suite_id!=='string'||!SUITE.test(value.suite_id)||
        (Object.hasOwn(value,'suite_sha256')&&
            (typeof value.suite_sha256!=='string'||!SHA256.test(value.suite_sha256)))||
        !trials(value.min_trials)||typeof value.min_success_rate!=='number'||
        !(value.min_success_rate>0&&value.min_success_rate<=1))
        throw Error('Evaluation stop needs a suite ID, minimum trials and a success rate in (0, 1].');
    return {suite_id:value.suite_id,min_trials:value.min_trials,
        min_success_rate:value.min_success_rate,
        ...(value.suite_sha256?{suite_sha256:value.suite_sha256}:{})};
}

function evaluationEvidence(value,policy,dispatchId) {
    if(!record(value)||value.schema_version!==1||
        value.suite_id!==policy.suite_id||value.dispatch_id!==dispatchId||
        (Object.hasOwn(value,'suite_sha256')&&
            (typeof value.suite_sha256!=='string'||!SHA256.test(value.suite_sha256)))||
        (policy.suite_sha256&&value.suite_sha256!==policy.suite_sha256)||
        typeof value.checkpoint_sha256!=='string'||!SHA256.test(value.checkpoint_sha256)||
        !trials(value.trials)||!Number.isSafeInteger(value.successes)||
        value.successes<0||value.successes>value.trials)
        throw Error('Invalid independent evaluation evidence for this suite and dispatch.');
    return {schema_version:1,suite_id:value.suite_id,dispatch_id:dispatchId,
        checkpoint_sha256:value.checkpoint_sha256,trials:value.trials,
        successes:value.successes,
        ...(value.suite_sha256?{suite_sha256:value.suite_sha256}:{})};
}
function suiteIds(value) {
    if(!Array.isArray(value)||value.length<1||value.length>8||
        new Set(value).size!==value.length||
        value.some(id=>typeof id!=='string'||!SUITE.test(id)))
        throw Error('Evaluation capability needs 1–8 distinct valid suite IDs.');
    return value.slice();
}
function supportsEvaluation(suites,job) {
    return !job.evaluation_stop||
        (suites&&Object.hasOwn(suites,job.project_id)&&
         suites[job.project_id].includes(job.evaluation_stop.suite_id));
}
function isWithin(parent,child) {
    const relative=path.relative(parent,child);
    return !path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep);
}

function evaluationReader(projectId,evaluationRoot,evaluationSuites,protectedRoots) {
    if(evaluationRoot==null&&evaluationSuites==null)return {};
    if(typeof evaluationRoot!=='string'||!path.isAbsolute(evaluationRoot))
        throw Error('Specify an absolute independent evaluation directory.');
    const root=fs.realpathSync(evaluationRoot);
    if(!fs.statSync(root).isDirectory()||protectedRoots.some(dir=>
        isWithin(dir,root)||isWithin(root,dir)))
        throw Error('Evaluation receipts must be separate from source and training artifacts.');
    const suites=Object.freeze(suiteIds(evaluationSuites));
    return {
        evaluation_suites:suites,
        readEvaluation(context) {
            if(context?.project_id!==projectId||!UUID.test(String(context.job_id||''))||
                !UUID.test(String(context.dispatch_id||''))||!suites.includes(context.suite_id))
                throw Error('Invalid independent evaluation identity.');
            const file=path.join(root,context.dispatch_id+'.'+context.suite_id+'.json');
            if(fs.realpathSync(root)!==root)
                throw Error('Evaluation directory identity changed.');
            let fd;
            try {
                if(!fs.lstatSync(file).isFile()||fs.realpathSync(file)!==file)
                    throw Error('Evaluation receipt must be a regular local file.');
                fd=fs.openSync(file,'r');
                const buffer=Buffer.alloc(4097);
                const bytes=fs.readSync(fd,buffer,0,buffer.length,0);
                if(bytes<1||bytes>4096)
                    throw Error('Invalid evaluation receipt size.');
                return evaluationEvidence(JSON.parse(buffer.toString('utf8',0,bytes)),
                    {suite_id:context.suite_id},context.dispatch_id);
            }catch(error) {
                if(error.code==='ENOENT'&&fd===undefined)return null;
                throw error;
            }finally {
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}

module.exports={evaluationPolicy,evaluationEvidence,suiteIds,supportsEvaluation,evaluationReader};
