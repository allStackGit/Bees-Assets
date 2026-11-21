'use strict';

const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');

// Independent evaluation is a separate adapter contract from training progress.
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA256=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const trials=n=>Number.isSafeInteger(n)&&n>=1&&n<=1000000;
const record=value=>value&&typeof value==='object'&&!Array.isArray(value);

// Fingerprint the exact local model bytes used for evaluation/continuation.
function modelFileSha256(file) {
    if(typeof file!=='string'||!path.isAbsolute(file)||
        fs.realpathSync(file)!==file)
        throw Error('Evaluated model artifact must be a local, non-redirected file.');
    const fd=fs.openSync(file,'r');
    try {
        const before=fs.fstatSync(fd);
        if(!before.isFile()||before.size<1)
            throw Error('Evaluated model artifact must be a nonempty regular file.');
        const digest=crypto.createHash('sha256'),buffer=Buffer.allocUnsafe(1048576);
        let bytes=0;
        while(true) {
            const count=fs.readSync(fd,buffer,0,buffer.length,null);
            if(!count)break;
            digest.update(buffer.subarray(0,count));
            bytes+=count;
        }
        const after=fs.fstatSync(fd);
        if(bytes!==before.size||after.size!==before.size||
            after.mtimeMs!==before.mtimeMs)
            throw Error('Evaluated model artifact changed while being fingerprinted.');
        return digest.digest('hex');
    }finally {fs.closeSync(fd);}
}

function evaluationPolicy(value) {
    if(value==null)return null;
    if(record(value)&&Object.hasOwn(value,'all_of')) {
        if(Object.keys(value).length!==1||!Array.isArray(value.all_of)||
            value.all_of.length<2||value.all_of.length>8)
            throw Error('All-of evaluation requires 2–8 independent suite thresholds.');
        const rules=value.all_of.map(evaluationPolicy);
        if(rules.some(rule=>!rule||rule.all_of)||
            new Set(rules.map(rule=>rule.suite_id)).size!==rules.length)
            throw Error('All-of evaluation requires distinct, nonnested suite IDs.');
        return {all_of:rules};
    }
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
function evaluationRules(policy) {
    return policy?.all_of|| (policy?[policy]:[]);
}
// A promotion target is satisfied only by complete independent evidence
// for the exact same immutable checkpoint. Training progress is never proof.
function evaluationComplete(evidence,policy,dispatchId) {
    if(!policy||!evidence)return false;
    try {
        const rules=evaluationRules(evaluationPolicy(policy));
        const observed=policy.all_of?evidence.all_of:[evidence];
        if(!Array.isArray(observed)||observed.length!==rules.length)
            return false;
        let checkpoint=null;
        for(const [i,rule] of rules.entries()) {
            const receipt=evaluationEvidence(observed[i],rule,dispatchId);
            if(checkpoint!==null&&receipt.checkpoint_sha256!==checkpoint)
                return false;
            checkpoint=receipt.checkpoint_sha256;
            if(receipt.trials<rule.min_trials||
                receipt.successes/receipt.trials<rule.min_success_rate)
                return false;
        }
        return true;
    }catch {
        return false;
    }
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
         evaluationRules(job.evaluation_stop).every(rule=>
             suites[job.project_id].includes(rule.suite_id)));
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

module.exports={evaluationPolicy,evaluationEvidence,evaluationRules,evaluationComplete,
    suiteIds,supportsEvaluation,evaluationReader,modelFileSha256};
