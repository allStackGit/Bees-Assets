'use strict';

// Durable, independent evaluation observations. This archive is never used
// for native Stop, candidate promotion, leases or checkpoint authorization.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {activeEvaluation,evaluationEvidence,evaluationFailure,evaluationRules}=
    require('./experiment_hub_evaluation');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROJECT=/^[a-z][a-z0-9_-]{0,47}$/;
const MAX_BYTES=64*1024*1024;
const MAX_UI_POINTS=1024;
const SHA256=/^[a-f0-9]{64}$/;
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
const HISTORICAL_SUFFIX='.historical.json';
const MAX_IMPORT_RECORDS=128;
const MAX_IMPORT_BYTES=65536;
// Receipt contradictions are already reported by the native lifecycle as
// non-scoring archive diagnostics. They must remain disqualifying after a
// Hub restart, not merely until the in-memory warning map is cleared.
const receiptConflict=issue=>typeof issue==='string'&&
    (issue.includes('Conflicting independent result for checkpoint ')||
     issue.startsWith('Conflicting independent evaluation receipt at checkpoint '));

// Read-only regression signals are recomputed from the complete immutable
// receipt archive. They never enter Stop evidence, candidate cohorts, or
// native checkpoint-continuation policy.
function wilson(successes,trials) {
    const z=1.96,positive=z*z,rate=successes/trials;
    const denominator=1+positive/trials;
    const center=(rate+positive/(2*trials))/denominator;
    const margin=z*Math.sqrt(rate*(1-rate)/trials+
        positive/(4*trials*trials))/denominator;
    return {lower:Math.max(0,center-margin),upper:Math.min(1,center+margin)};
}
function regressionSignals(job,records) {
    const policy=job?.evaluation_monitor;
    const alert=policy?.regression_alert;
    if(!alert)return [];
    const rules=policy.all_of||[policy];
    return rules.map(rule=>{
        const base={job_id:job.id,suite_id:rule.suite_id,
            window_checkpoints:alert.window_checkpoints,
            minimum_drop:alert.minimum_drop};
        const rows=records.filter(row=>row.suite_id===rule.suite_id);
        const state=(status,reason)=>({...base,status,reason});
        if(!rows.length)
            return state('unavailable','No accepted independent evaluation receipts for this suite.');
        const pinned=rule.suite_sha256||
            job.evaluation_suite_definitions?.[rule.suite_id]||null;
        const definitions=new Set(rows.map(row=>row.suite_sha256));
        const runtimes=new Set(rows.map(row=>row.runtime_sha256));
        const exactDefinition=[...definitions][0];
        if(definitions.size!==1||!SHA256.test(exactDefinition||'')||
            pinned&&exactDefinition!==pinned)
            return state('incompatible','Frozen evaluation suite definitions differ or do not match the job pin.');
        if(runtimes.size!==1||!SHA256.test([...runtimes][0]||''))
            return state('incompatible','Evaluation runtime fingerprint is missing or changed across checkpoints.');
        if(rows.some(row=>!Number.isSafeInteger(row.steps_completed)||
            row.steps_completed<0||!SHA256.test(String(row.checkpoint_sha256||''))||
            !Number.isSafeInteger(row.trials)||row.trials<rule.min_trials||
            !Number.isSafeInteger(row.successes)||row.successes<0||
            row.successes>row.trials))
            return state('incompatible','Checkpoint coordinates, trial counts or outcome identities are unavailable.');
        const ordered=rows.slice().sort((a,b)=>a.steps_completed-b.steps_completed);
        const seenUpdates=new Set(),seenCheckpoints=new Set();
        if(ordered.some(row=>{
            if(seenUpdates.has(row.steps_completed)||
                seenCheckpoints.has(row.checkpoint_sha256))return true;
            seenUpdates.add(row.steps_completed);
            seenCheckpoints.add(row.checkpoint_sha256);
            return false;
        }))
            return state('incompatible','Multiple observations identify the same update or checkpoint.');
        const n=alert.window_checkpoints;
        if(ordered.length<2*n)
            return {...state('insufficient','Need '+(2*n)+' distinct comparable checkpoint evaluations.'),recorded_checkpoints:ordered.length};
        const summarize=window=>{
            const trials=window.reduce((total,row)=>total+row.trials,0);
            const successes=window.reduce((total,row)=>total+row.successes,0);
            return {rate:successes/trials,successes,trials,
                start_update:window[0].steps_completed,
                end_update:window[window.length-1].steps_completed,
                ...wilson(successes,trials)};
        };
        const baseline=summarize(ordered.slice(0,n));
        const latest=summarize(ordered.slice(-n));
        const drop=baseline.rate-latest.rate;
        const reliable=drop>=alert.minimum_drop&&
            baseline.lower>latest.upper;
        return {...base,
            suite_sha256:[...definitions][0],runtime_sha256:[...runtimes][0],
            status:reliable?'regressed':
                drop>=alert.minimum_drop?'inconclusive':'no_regression',
            reason:reliable
                ?'Fixed-suite success rate declined beyond the configured threshold and conservative binomial bounds.'
                :drop>=alert.minimum_drop
                    ?'Observed decline exceeds the threshold, but evidence is not separated by conservative binomial bounds.'
                    :'No decline beyond the configured threshold.',
            baseline,latest,drop,recorded_checkpoints:ordered.length};
    });
}

class EvaluationHistory {
    constructor(root) {
        this.directory=path.join(root,'hub-evaluations-v1');
        this.loaded=new Map();
        this.issues=new Map();
    }
    get error() {
        return this.issues.values().next().value||null;
    }
    directoryReady() {
        if(!fs.existsSync(this.directory))
            fs.mkdirSync(this.directory,{mode:0o700});
        const dir=fs.lstatSync(this.directory);
        if(!dir.isDirectory()||dir.isSymbolicLink()||
            (typeof process.getuid==='function'&&
             (dir.uid!==process.getuid()||(dir.mode&0o077)!==0)))
            throw Error('Independent evaluation archive directory is unsafe.');
    }
    // Archive segmentation is internal to this existing history: the original
    // job.jsonl is segment zero and remains readable unchanged. Subsequent
    // immutable byte prefixes are job.000001.jsonl, job.000002.jsonl, etc.
    // A missing segment fails closed rather than silently shortening evidence.
    file(jobId,segment=0) {
        if(!UUID.test(String(jobId))||
            !Number.isSafeInteger(segment)||segment<0||segment>999999)
            throw Error('Invalid evaluation archive segment identity.');
        return path.join(this.directory,jobId+
            (segment?'.'+String(segment).padStart(6,'0'):'')+'.jsonl');
    }
    segmentFiles(jobId) {
        const primary=this.file(jobId);
        this.directoryReady();
        const prefix=jobId+'.';
        const segments=fs.readdirSync(this.directory)
            .filter(name=>name.startsWith(prefix)&&
                /^[0-9]{6}\.jsonl$/.test(name.slice(prefix.length)))
            .map(name=>Number(name.slice(prefix.length,-6)))
            .sort((a,b)=>a-b);
        if(segments.some((number,index)=>number!==index+1)||
            (segments.length&&!fs.existsSync(primary)))
            throw Error('Independent evaluation archive segment sequence is incomplete.');
        return fs.existsSync(primary)
            ?[primary,...segments.map(number=>this.file(jobId,number))]:[];
    }
    load(jobId,projectId) {
        const existing=this.loaded.get(jobId);
        if(existing) {
            if(existing.project_id!==projectId)
                throw Error('Independent evaluation job ID belongs to another project.');
            return existing;
        }
        const files=this.segmentFiles(jobId);
        const records=[],diagnostics=[],seen=new Map(),issueKeys=new Set(),
            failureAttempts=new Map();
        let lastEntry=null,conflicted=false,stat=null;
        for(const [index,file] of files.entries()) {
            stat=fs.lstatSync(file);
            // Every closed segment must have been genuinely full enough to
            // require rotation by a bounded (<=8 KiB) next receipt. Reject
            // forged, missing or prematurely truncated archive generations.
            if(index<files.length-1&&stat.size<=MAX_BYTES-8192)
                throw Error('Independent evaluation archive segment rotated before capacity.');
            if(!stat.isFile()||stat.isSymbolicLink()||stat.nlink!==1||
                stat.size>MAX_BYTES||
                (typeof process.getuid==='function'&&
                 (stat.uid!==process.getuid()||(stat.mode&0o077)!==0)))
                throw Error('Independent evaluation history file is unsafe or too large.');
            const raw=fs.readFileSync(file,'utf8');
            if(raw&&!raw.endsWith('\n'))
                throw Error('Independent evaluation history has an incomplete record.');
            for(const line of raw.split('\n')) {
                if(!line)continue;
                const row=JSON.parse(line);
                if(row.schema_version!==1||row.job_id!==jobId||
                    row.project_id!==projectId||
                    !UUID.test(String(row.dispatch_id)))
                    throw Error('Invalid independent evaluation archive identity.');
                if(row.evaluation_status==='failed') {
                    // A failed evaluation has exact checkpoint provenance,
                    // but never a measured success/trial count or score.
                    const {observed_at,...attempt}=row;
                    const checked=evaluationFailure(attempt,{
                        project_id:projectId,job_id:jobId,
                        dispatch_id:row.dispatch_id,suite_id:row.suite_id,
                    });
                    if(typeof observed_at!=='string'||
                        !Number.isFinite(Date.parse(observed_at))||
                        new Date(observed_at).toISOString()!==observed_at||
                        checked.checkpoint_sha256!==row.checkpoint_sha256)
                        throw Error('Invalid archived failed evaluation attempt.');
                    // Two failed attempts against the same immutable model
                    // remain distinct if they happened at different times.
                    // Replayed native heartbeats retain the original attempt
                    // timestamp and must not append the same attempt again.
                    const key=JSON.stringify([row.dispatch_id,row.suite_id,
                        row.checkpoint_sha256,row.attempted_at]);
                    if(failureAttempts.has(key))
                        throw Error('Duplicate failed evaluation attempt in archive.');
                    failureAttempts.set(key,JSON.stringify(row));
                    diagnostics.push(row);
                    lastEntry=row;
                    continue;
                }
                if(row.evaluation_status==='unavailable') {
                    // A native evaluator reported an unavailable or invalid
                    // receipt. No checkpoint or measured score is asserted.
                    if(row.suite_id!==null&&!SUITE.test(String(row.suite_id))||
                        typeof row.issue!=='string'||!row.issue||
                        row.issue.length>160||
                        typeof row.observed_at!=='string'||
                        !Number.isFinite(Date.parse(row.observed_at))||
                        new Date(row.observed_at).toISOString()!==row.observed_at||
                        ['checkpoint_sha256','trials','successes','metrics']
                            .some(key=>Object.hasOwn(row,key)))
                        throw Error('Invalid independent evaluation diagnostic.');
                    diagnostics.push(row);
                    issueKeys.add(JSON.stringify([row.dispatch_id,row.suite_id,row.issue]));
                    if(receiptConflict(row.issue))conflicted=true;
                    lastEntry=row;
                    continue;
                }
                if(row.evaluation_status!=null&&row.evaluation_status!=='complete'||
                    !SUITE.test(String(row.suite_id||''))||
                    !SHA256.test(String(row.suite_sha256||''))||
                    typeof row.observed_at!=='string'||
                    !Number.isFinite(Date.parse(row.observed_at))||
                    new Date(row.observed_at).toISOString()!==row.observed_at)
                    throw Error('Invalid independent evaluation archive record.');
                // A previously persisted receipt must satisfy the same
                // project-neutral evidence/measurement contract as a live
                // receipt. Otherwise malformed historical scores could
                // reappear after a restart without passing native checks.
                // Older accepted receipts may lack an update coordinate;
                // preserve them without fabricating a comparison point.
                evaluationEvidence(row,{
                    suite_id:row.suite_id,suite_sha256:row.suite_sha256,
                },row.dispatch_id);
                const key=row.dispatch_id+':'+row.suite_id+':'+row.checkpoint_sha256;
                if(seen.has(key))throw Error('Duplicate evaluation history record.');
                seen.set(key,JSON.stringify(row));
                records.push(row);
                lastEntry=row;
            }
        }
        const loaded={project_id:projectId,records,diagnostics,seen,issueKeys,
            failureAttempts,conflicted,lastEntry,
            segment:Math.max(0,files.length-1),
            size:stat?.size||0,dev:stat?.dev??null,ino:stat?.ino??null};
        this.loaded.set(jobId,loaded);
        return loaded;
    }
    // Both completed receipts and non-authoritative diagnostic events use
    // one protected, fsynced append path. Never overwrite prior evidence.
    appendRow(jobId,data,row) {
        const body=Buffer.from(JSON.stringify(row)+'\n');
        if(body.length>8192)
            throw Error('Independent evaluation history record exceeds 8 KiB.');
        const rotate=data.size+body.length>MAX_BYTES;
        const segment=data.segment+(rotate?1:0);
        const file=this.file(jobId,segment);
        if(rotate) {
            // Recheck the opened predecessor and its directory entry before
            // rolling over. Neither a symlink replacement nor an inode swap
            // may turn a tampered archive into an apparently new segment.
            const previous=this.file(jobId,data.segment);
            const previousFd=fs.openSync(previous,fs.constants.O_RDONLY|
                (fs.constants.O_NOFOLLOW||0));
            try {
                const preceding=fs.fstatSync(previousFd);
                const named=fs.lstatSync(previous);
                if(!preceding.isFile()||!named.isFile()||
                    named.isSymbolicLink()||preceding.nlink!==1||
                    preceding.size!==data.size||
                    preceding.ino!==data.ino||preceding.dev!==data.dev||
                    named.ino!==preceding.ino||named.dev!==preceding.dev||
                    (typeof process.getuid==='function'&&
                     (preceding.uid!==process.getuid()||
                      (preceding.mode&0o077)!==0)))
                    throw Error('Independent evaluation archive changed before rotation.');
            }finally {fs.closeSync(previousFd);}
        }
        const newFile=!fs.existsSync(file);
        const fd=fs.openSync(file,fs.constants.O_CREAT|fs.constants.O_RDWR|
            fs.constants.O_APPEND|(fs.constants.O_NOFOLLOW||0),0o600);
        try {
            const st=fs.fstatSync(fd),current=fs.lstatSync(file);
            const expectedSize=rotate?0:data.size;
            if(!st.isFile()||st.nlink!==1||!current.isFile()||
                current.isSymbolicLink()||st.ino!==current.ino||
                st.dev!==current.dev||st.size!==expectedSize||
                (!rotate&&data.ino!==null&&
                 (data.ino!==st.ino||data.dev!==st.dev))||
                (typeof process.getuid==='function'&&
                 (st.uid!==process.getuid()||(st.mode&0o077)!==0)))
                throw Error('Independent evaluation archive was replaced or modified.');
            try {
                for(let offset=0;offset<body.length;) {
                    const n=fs.writeSync(fd,body,offset,body.length-offset);
                    if(!Number.isSafeInteger(n)||n<=0)
                        throw Error('Incomplete independent evaluation archive append.');
                    offset+=n;
                }
                fs.fsyncSync(fd);
                // On POSIX, ensure a newly created file's directory
                // entry is durable along with its fsynced contents, including
                // the original first segment. Windows retains file fsync.
                if(newFile&&process.platform!=='win32') {
                    const directoryFd=fs.openSync(this.directory,'r');
                    try {fs.fsyncSync(directoryFd);}
                    finally {fs.closeSync(directoryFd);}
                }
            }catch(error) {
                try {
                    fs.ftruncateSync(fd,st.size);
                    fs.fsyncSync(fd);
                }catch(rollbackError) {
                    throw Error('Independent evaluation append rollback failed: '+
                        String(rollbackError.message||rollbackError));
                }
                throw error;
            }
            data.segment=segment;
            data.size=st.size+body.length;
            data.dev=st.dev;data.ino=st.ino;
            data.lastEntry=row;
        }finally{fs.closeSync(fd);}
    }
    record(job,receipt) {
        const id=job?.id;
        try {
            if(!UUID.test(String(id))||!PROJECT.test(String(job.project_id))||
                !UUID.test(String(receipt?.dispatch_id))||
                receipt.dispatch_id!==job.dispatch_id||
                receipt.schema_version!==1||
                typeof receipt.suite_sha256!=='string'||
                !/^[a-f0-9]{64}$/.test(receipt.suite_sha256))
                throw Error('Invalid independent evaluation identity.');
            const data=this.load(id,job.project_id);
            const key=receipt.dispatch_id+':'+receipt.suite_id+':'+receipt.checkpoint_sha256;
            if(data.seen.has(key)) {
                // An idempotent retry is harmless; a second contradictory
                // report for the same immutable checkpoint is not. Retain
                // the first durable observation and surface the conflict
                // without granting any training or promotion authority.
                const previous=JSON.parse(data.seen.get(key));
                const fields=['schema_version','suite_id','dispatch_id',
                    'checkpoint_sha256','suite_sha256','runtime_sha256',
                    'steps_completed','evaluated_at','trials','successes','metrics'];
                const canonical=value=>JSON.stringify(value===undefined?null:
                    value&&typeof value==='object'&&!Array.isArray(value)
                        ?Object.fromEntries(Object.entries(value).sort(([a],[b])=>
                            a.localeCompare(b)).map(([k,v])=>
                            [k,v&&typeof v==='object'&&!Array.isArray(v)
                                ?Object.fromEntries(Object.entries(v).sort(([a],[b])=>
                                    a.localeCompare(b))):v]))
                        :value);
                if(fields.some(field=>canonical(previous[field])!==canonical(receipt[field]))) {
                    this.recordIssue(job,
                        'Conflicting independent evaluation receipt at checkpoint '+
                        receipt.checkpoint_sha256+'; original evidence retained.',
                        receipt.suite_id);
                    this.issues.set('conflict:'+id,
                        'Conflicting evaluation receipt for an already archived checkpoint on job '+
                        id+'; original independent evidence retained.');
                }
                return false;
            }
            const row={...receipt,schema_version:1,evaluation_status:'complete',
                job_id:id,project_id:job.project_id,
                observed_at:new Date().toISOString()};
            this.appendRow(id,data,row);
            data.records.push(row);
            data.seen.set(key,JSON.stringify(row));
            this.issues.delete(id);
            return true;
        }catch(error) {
            this.issues.set(id,'Independent evaluation history unavailable: '+
                String(error.message||error).slice(0,160));
            return false;
        }
    }
    // A verified native owner can report an unreadable or invalid receipt.
    // Preserve its availability diagnostic without inventing a checkpoint,
    // trial, success count or failed game outcome. Repeated heartbeats with
    // the same ongoing issue do not fill the archive.
    recordIssue(job,issue,suiteId=null) {
        const id=job?.id;
        try {
            if(!UUID.test(String(id))||!PROJECT.test(String(job?.project_id))||
                !UUID.test(String(job?.dispatch_id))||
                typeof issue!=='string'||issue.length<1||issue.length>160||
                (suiteId!==null&&!SUITE.test(String(suiteId))))
                throw Error('Invalid independent evaluation diagnostic identity.');
            const data=this.load(id,job.project_id);
            // Native heartbeat reports have no distinct failed-attempt ID.
            // Store one durable event per distinct dispatch/suite/reason,
            // not a fabricated new failure on every heartbeat or after restart.
            const issueKey=JSON.stringify([job.dispatch_id,suiteId,issue]);
            if(data.issueKeys.has(issueKey))return false;
            const row={schema_version:1,evaluation_status:'unavailable',
                job_id:id,project_id:job.project_id,dispatch_id:job.dispatch_id,
                suite_id:suiteId,issue,observed_at:new Date().toISOString()};
            this.appendRow(id,data,row);
            data.issueKeys.add(issueKey);
            data.diagnostics.push(row);
            if(receiptConflict(issue))data.conflicted=true;
            this.issues.delete('diagnostic:'+id);
            return true;
        }catch(error) {
            this.issues.set('diagnostic:'+id,
                'Independent evaluation diagnostic archive unavailable: '+
                String(error.message||error).slice(0,160));
            return false;
        }
    }
    recordFailure(job,failure) {
        const id=job?.id;
        try {
            if(!UUID.test(String(id))||!PROJECT.test(String(job?.project_id))||
                !UUID.test(String(job?.dispatch_id)))
                throw Error('Invalid native evaluator failure owner.');
            const policy=activeEvaluation(job);
            const rule=evaluationRules(policy).find(rule=>
                rule.suite_id===failure?.suite_id);
            if(!rule)
                throw Error('Failed attempt does not belong to a configured evaluation suite.');
            const checked=evaluationFailure(failure,{
                project_id:job.project_id,job_id:id,
                dispatch_id:job.dispatch_id,suite_id:rule.suite_id,
                suite_sha256:job.evaluation_suite_definitions?.[rule.suite_id]||
                    rule.suite_sha256||undefined,
            });
            const data=this.load(id,job.project_id);
            const key=JSON.stringify([checked.dispatch_id,checked.suite_id,
                checked.checkpoint_sha256,checked.attempted_at]);
            if(data.failureAttempts.has(key)) {
                const {observed_at,...prior}=JSON.parse(
                    data.failureAttempts.get(key));
                if(JSON.stringify(prior)!==JSON.stringify(checked))
                    this.issues.set('failure-conflict:'+id,
                        'Conflicting independent evaluator failure report for an already archived attempt on job '+
                        id+'; original evidence retained.');
                return false;
            }
            const row={...checked,observed_at:new Date().toISOString()};
            this.appendRow(id,data,row);
            data.failureAttempts.set(key,JSON.stringify(row));
            data.diagnostics.push(row);
            this.issues.delete('failure:'+id);
            return true;
        }catch(error) {
            this.issues.set('failure:'+id,
                'Independent failed-attempt archive unavailable: '+
                String(error.message||error).slice(0,160));
            return false;
        }
    }
    // The independent poller must not publish its next checkpoint over the
    // only local receipt until this exact previous receipt is durable here.
    // Native candidate caches are bounded, so they cannot acknowledge it.
    latestAccepted(job,suiteId) {
        if(!UUID.test(String(job?.id))||
            !PROJECT.test(String(job?.project_id))||
            !UUID.test(String(job?.dispatch_id))||
            !SUITE.test(String(suiteId)))
            throw Error('Invalid evaluation archive acknowledgement identity.');
        try {
            const records=this.load(job.id,job.project_id).records;
            for(let i=records.length-1;i>=0;i--) {
                const row=records[i];
                if(row.suite_id===suiteId&&row.dispatch_id===job.dispatch_id)
                    return evaluationEvidence(row,{suite_id:suiteId},
                        job.dispatch_id);
            }
            return null;
        }catch(error) {
            this.issues.set(job.id,'Independent evaluation archive acknowledgement unavailable: '+
                String(error.message||error).slice(0,160));
            throw error;
        }
    }
    hasReceiptConflict(job) {
        const id=job?.id;
        if(!UUID.test(String(id))||!PROJECT.test(String(job?.project_id)))
            throw Error('Invalid independent evaluation candidate identity.');
        // A malformed or unavailable archive fails closed rather than
        // reviving promotion authority from the bounded candidate cache.
        return this.issues.has('conflict:'+id)||
            this.load(id,job.project_id).conflicted===true;
    }
    // Offline checkpoint audits are kept in the existing independent
    // evaluation archive, but never added to the native job registry.
    // These read-only sources cannot be dispatched, stopped or promoted.
    historicalJobs() {
        this.directoryReady();
        const jobs=[];
        for(const name of fs.readdirSync(this.directory)) {
            if(!name.endsWith(HISTORICAL_SUFFIX))continue;
            const id=name.slice(0,-HISTORICAL_SUFFIX.length);
            if(!UUID.test(id))continue;
            try {
                const metaFile=path.join(this.directory,name);
                const stat=fs.lstatSync(metaFile);
                if(!stat.isFile()||stat.isSymbolicLink()||stat.nlink!==1||
                    stat.size<1||stat.size>32768||
                    (typeof process.getuid==='function'&&
                     (stat.uid!==process.getuid()||(stat.mode&0o077)!==0)))
                    throw Error('Historical evaluation metadata is unsafe.');
                const meta=JSON.parse(fs.readFileSync(metaFile,'utf8'));
                if(meta.schema_version!==1||meta.id!==id||
                    meta.project_id!=='tinybrains'||
                    typeof meta.run_name!=='string'||meta.run_name.length<1||
                    meta.run_name.length>100||
                    !SHA256.test(String(meta.source_report_sha256||''))||
                    !Number.isFinite(Date.parse(meta.created_at))||
                    !Array.isArray(meta.suites)||!meta.suites.length||
                    meta.suites.length>8)
                    throw Error('Invalid historical evaluation source metadata.');
                const suites=new Map();
                for(const pinned of meta.suites) {
                    if(!pinned||!SUITE.test(String(pinned.suite_id||''))||
                        !SHA256.test(String(pinned.suite_sha256||''))||
                        typeof pinned.definition!=='string'||
                        Buffer.byteLength(pinned.definition,'utf8')>8192||
                        crypto.createHash('sha256').update(pinned.definition)
                            .digest('hex')!==pinned.suite_sha256||
                        suites.has(pinned.suite_id))
                        throw Error('Historical frozen suite metadata is inconsistent.');
                    const definition=JSON.parse(pinned.definition);
                    if(!definition||definition.suite_id!==pinned.suite_id||
                        !SHA256.test(String(definition.runtime_sha256||'')))
                        throw Error('Historical frozen suite runtime is unavailable.');
                    suites.set(pinned.suite_id,{
                        fingerprint:pinned.suite_sha256,
                        runtime:definition.runtime_sha256,
                    });
                }
                const records=this.load(id,meta.project_id).records;
                const seen=new Set();
                if(!records.length||records.length>MAX_IMPORT_RECORDS||
                    records.some(row=>{
                        const suite=suites.get(row.suite_id);
                        const key=row.suite_id+':'+row.checkpoint_sha256;
                        if(seen.has(key))return true;
                        seen.add(key);
                        const metrics=row.metrics;
                        return row.source_kind!=='historical_checkpoint_audit'||
                            row.source_report_sha256!==meta.source_report_sha256||
                            row.evaluation_status!=='complete'||
                            row.dispatch_id!==id||row.observed_at!==meta.created_at||
                            !suite||row.suite_sha256!==suite.fingerprint||
                            row.runtime_sha256!==suite.runtime||
                            !Number.isSafeInteger(row.steps_completed)||
                            row.steps_completed<0||
                            !/^update-[0-9]{6,8}\.pt$/.test(
                                String(row.source_checkpoint_name||''))||
                            Number(row.source_checkpoint_name.slice(7,-3))!==
                                row.steps_completed||
                            !row.evaluated_at||
                            !metrics||
                            !['win_count','loss_count','draw_count','score_rate']
                                .every(name=>Object.hasOwn(metrics,name))||
                            metrics.win_count.unit!=='count'||
                            metrics.loss_count.unit!=='count'||
                            metrics.draw_count.unit!=='count'||
                            metrics.score_rate.unit!=='ratio'||
                            metrics.win_count.value!==row.successes||
                            metrics.win_count.value+metrics.loss_count.value+
                                metrics.draw_count.value!==row.trials||
                            Math.abs(metrics.score_rate.value-
                                (row.successes+metrics.draw_count.value/2)/
                                row.trials)>1e-10;
                    }))
                    throw Error('Historical evaluation evidence lineage is incomplete.');
                jobs.push({
                    id,project_id:meta.project_id,
                    configuration_name:meta.run_name,run_name:meta.run_name,
                    status:'historical_evaluation',evaluation_history_only:true,
                    created_at:meta.created_at,source_report_sha256:meta.source_report_sha256,
                    source_suites:meta.suites,
                });
                this.issues.delete('historical:'+id);
            }catch(error) {
                this.issues.set('historical:'+id,
                    'Historical evaluation '+id+' unavailable: '+
                    String(error.message||error).slice(0,150));
            }
        }
        return jobs;
    }
    importHistorical(value,existingIds=[]) {
        if(!value||typeof value!=='object'||Array.isArray(value)||
            value.project_id!=='tinybrains'||value.complete!==true||
            typeof value.run_name!=='string'||!value.run_name.trim()||
            value.run_name.length>100||
            !SHA256.test(String(value.source_report_sha256||''))||
            !Array.isArray(value.results)||!value.results.length||
            value.results.length>MAX_IMPORT_RECORDS)
            throw Error('Expected a complete, bounded independent TinyBrains checkpoint audit.');
        if(Buffer.byteLength(JSON.stringify(value),'utf8')>MAX_IMPORT_BYTES)
            throw Error('Historical evaluation import exceeds the request limit.');
        const importDigest=crypto.createHash('sha256').update(
            JSON.stringify(value.results)).digest('hex');
        const seed=crypto.createHash('sha256').update(
            value.project_id+':'+value.source_report_sha256).digest('hex');
        // Deterministic identity makes resubmission idempotent and prevents
        // the same immutable report from creating multiple comparison runs.
        const id=seed.slice(0,8)+'-'+seed.slice(8,12)+'-5'+seed.slice(13,16)+
            '-a'+seed.slice(17,20)+'-'+seed.slice(20,32);
        if(existingIds.includes(id))
            throw Error('Historical report identity collides with a native job.');
        this.directoryReady();
        const metaFile=path.join(this.directory,id+HISTORICAL_SUFFIX);
        const file=this.file(id);
        if(fs.existsSync(metaFile)) {
            const existing=this.historicalJobs().find(job=>job.id===id);
            if(existing&&existing.source_report_sha256===value.source_report_sha256) {
                const meta=JSON.parse(fs.readFileSync(metaFile,'utf8'));
                if(meta.import_sha256!==importDigest)
                    throw Error('Historical report identity was reused with conflicting results.');
                return {job_id:id,imported:false,record_count:this.load(id,'tinybrains').records.length};
            }
            throw Error('An existing historical report with this identity is invalid.');
        }
        let observed=new Date().toISOString();
        if(fs.existsSync(file)) {
            // Recover only a previously completed append. Preserve the
            // original receipt time so a retry has identical bytes.
            const raw=fs.readFileSync(file,'utf8');
            if(!raw.endsWith('\n'))
                throw Error('Interrupted historical archive is incomplete; retained for inspection.');
            const first=JSON.parse(raw.split('\n')[0]);
            if(first.source_report_sha256!==value.source_report_sha256||
                typeof first.observed_at!=='string'||
                !Number.isFinite(Date.parse(first.observed_at))||
                new Date(first.observed_at).toISOString()!==first.observed_at)
                throw Error('Previous historical import identity is inconsistent.');
            observed=first.observed_at;
        }
        const seen=new Set(),suites=new Map(),rows=[];
        for(const raw of value.results) {
            if(!raw||typeof raw!=='object'||Array.isArray(raw)||
                typeof raw.suite_definition_canonical!=='string'||
                Buffer.byteLength(raw.suite_definition_canonical)>8192||
                !SHA256.test(String(raw.suite_sha256||''))||
                crypto.createHash('sha256').update(raw.suite_definition_canonical)
                    .digest('hex')!==raw.suite_sha256||
                !SHA256.test(String(raw.runtime_sha256||''))||
                !Number.isSafeInteger(raw.steps_completed)||raw.steps_completed<0||
                typeof raw.checkpoint_name!=='string'||
                !/^update-[0-9]{6,8}\.pt$/.test(raw.checkpoint_name)||
                Number(raw.checkpoint_name.slice(7,-3))!==raw.steps_completed)
                throw Error('Historical evaluation checkpoint or suite provenance is invalid.');
            let suite;
            try{suite=JSON.parse(raw.suite_definition_canonical);}
            catch {throw Error('Historical evaluation suite definition is not JSON.');}
            if(!suite||typeof suite!=='object'||Array.isArray(suite)||
                !SUITE.test(String(suite.suite_id||''))||
                suite.suite_id!==raw.suite_id||
                suite.runtime_sha256!==raw.runtime_sha256||
                !Array.isArray(suite.maps)||!Array.isArray(suite.seeds)||
                !['nano','micro'].includes(suite.opponent||'nano')||
                (suite.opponent==='micro'&&
                    (!SHA256.test(String(suite.opponent_model_sha256||''))||
                     !SHA256.test(String(suite.opponent_manifest_sha256||'')))))
                throw Error('Historical evaluation suite differs from the claimed pinned runtime/opponent.');
            const prior=suites.get(raw.suite_id);
            if(prior&&prior.suite_sha256!==raw.suite_sha256)
                throw Error('One historical suite ID has conflicting definitions.');
            suites.set(raw.suite_id,{suite_id:raw.suite_id,
                suite_sha256:raw.suite_sha256,
                definition:raw.suite_definition_canonical});
            if(suites.size>8)throw Error('Historical comparison supports at most eight suites.');
            const evidence=evaluationEvidence({
                schema_version:1,suite_id:raw.suite_id,dispatch_id:id,
                checkpoint_sha256:raw.checkpoint_sha256,
                suite_sha256:raw.suite_sha256,
                runtime_sha256:raw.runtime_sha256,
                steps_completed:raw.steps_completed,
                evaluated_at:raw.evaluated_at,
                trials:raw.trials,successes:raw.successes,metrics:raw.metrics,
            },{suite_id:raw.suite_id,suite_sha256:raw.suite_sha256},id);
            if(!evidence.evaluated_at||
                !evidence.metrics||
                !['win_count','loss_count','draw_count','score_rate'].every(
                    key=>Object.hasOwn(evidence.metrics,key))||
                evidence.metrics.win_count.value!==evidence.successes||
                evidence.metrics.win_count.unit!=='count'||
                evidence.metrics.loss_count.unit!=='count'||
                evidence.metrics.draw_count.unit!=='count'||
                evidence.metrics.score_rate.unit!=='ratio'||
                evidence.metrics.win_count.value+evidence.metrics.loss_count.value+
                    evidence.metrics.draw_count.value!==evidence.trials||
                Math.abs(evidence.metrics.score_rate.value-
                    (evidence.successes+evidence.metrics.draw_count.value/2)/
                    evidence.trials)>1e-10)
                throw Error('Historical match outcomes do not reconcile with success evidence.');
            const unique=evidence.checkpoint_sha256+':'+evidence.suite_id;
            if(seen.has(unique))throw Error('Repeated historical checkpoint/suite result.');
            seen.add(unique);
            rows.push({
                ...evidence,job_id:id,project_id:value.project_id,
                observed_at:observed,
                source_kind:'historical_checkpoint_audit',
                source_report_sha256:value.source_report_sha256,
                source_checkpoint_name:raw.checkpoint_name,
                evaluation_status:'complete',
            });
        }
        rows.sort((a,b)=>a.steps_completed-b.steps_completed||
            a.suite_id.localeCompare(b.suite_id));
        const records=Buffer.from(rows.map(row=>JSON.stringify(row)).join('\n')+'\n');
        if(records.length>MAX_BYTES||rows.some(row=>
            Buffer.byteLength(JSON.stringify(row))>8192))
            throw Error('Historical evaluation archive exceeds safe bounds.');
        const meta={
            schema_version:1,id,project_id:value.project_id,
            run_name:value.run_name.trim(),created_at:observed,
            source_report_sha256:value.source_report_sha256,
            import_sha256:importDigest,
            suites:[...suites.values()],
        };
        const metadata=Buffer.from(JSON.stringify(meta)+'\n');
        if(metadata.length>32768)
            throw Error('Historical suite definitions exceed metadata limit.');
        const writeNew=(location,body)=>{
            const fd=fs.openSync(location,'wx',0o600);
            try {
                for(let offset=0;offset<body.length;) {
                    const count=fs.writeSync(fd,body,offset,body.length-offset);
                    if(!Number.isSafeInteger(count)||count<=0)
                        throw Error('Incomplete historical archive write.');
                    offset+=count;
                }
                fs.fsyncSync(fd);
            }finally{fs.closeSync(fd);}
        };
        // An interrupted import may leave a fully fsynced history file
        // without its discoverable metadata. Recover only if every byte
        // matches the now-validated import; never truncate or replace it.
        let recovered=false;
        if(fs.existsSync(file)) {
            const stat=fs.lstatSync(file);
            if(!stat.isFile()||stat.isSymbolicLink()||stat.nlink!==1||
                stat.size!==records.length||
                (typeof process.getuid==='function'&&
                 (stat.uid!==process.getuid()||(stat.mode&0o077)!==0))||
                !fs.readFileSync(file).equals(records))
                throw Error('Previous historical import differs; retained for inspection.');
            recovered=true;
        }else writeNew(file,records);
        // History first, discoverable metadata last. Neither operation
        // modifies the native registry or any existing evaluation file.
        writeNew(metaFile,metadata);
        return {job_id:id,imported:true,recovered,record_count:rows.length};
    }
    // Regression stopping is optional and separate from chart rendering,
    // success-based Stop evidence and candidate-promotion authority. Decide
    // only from the complete fsynced archive, with the newest receipt still
    // matching the authenticated, lifecycle-accepted native observation.
    regressionStopDecision(job,receipt) {
        if(job?.status!=='running'||job?.stop_requested_at||
            job?.evaluation_history_only===true||
            job?.evaluation_monitor?.regression_alert?.stop_on_regression!==true||
            !UUID.test(String(job.id||''))||
            !UUID.test(String(job.dispatch_id||''))||
            !SHA256.test(String(receipt?.suite_sha256||''))||
            !SHA256.test(String(receipt?.runtime_sha256||''))||
            !SHA256.test(String(receipt?.checkpoint_sha256||''))||
            typeof receipt.evaluated_at!=='string'||
            !Number.isFinite(Date.parse(receipt.evaluated_at))||
            new Date(receipt.evaluated_at).toISOString()!==receipt.evaluated_at||
            receipt.dispatch_id!==job.dispatch_id||
            !Number.isSafeInteger(receipt.steps_completed))
            return null;
        try {
            if(this.hasReceiptConflict(job))return null;
            const data=this.load(job.id,job.project_id);
            const records=data.records;
            const cacheKey=receipt.checkpoint_sha256+':'+records.length;
            // Replayed native inspections can report the same successful
            // receipt every few seconds. A negative result cannot change
            // until a new immutable receipt is durably appended. Avoid
            // re-sorting the complete archive on every heartbeat.
            const negativeChecks=data.regressionNegativeChecks||=
                new Map();
            if(negativeChecks.get(receipt.suite_id)===cacheKey)
                return null;
            const source=records.find(row=>
                row.dispatch_id===job.dispatch_id&&
                row.suite_id===receipt.suite_id&&
                row.checkpoint_sha256===receipt.checkpoint_sha256&&
                row.suite_sha256===receipt.suite_sha256&&
                row.runtime_sha256===receipt.runtime_sha256&&
                row.steps_completed===receipt.steps_completed&&
                row.trials===receipt.trials&&
                row.successes===receipt.successes&&
                row.evaluated_at===receipt.evaluated_at);
            if(!source) {
                negativeChecks.set(receipt.suite_id,cacheKey);
                return null;
            }
            const signal=regressionSignals(job,records).find(row=>
                row.suite_id===receipt.suite_id&&
                row.status==='regressed');
            if(!signal||signal.latest?.end_update!==receipt.steps_completed||
                signal.suite_sha256!==receipt.suite_sha256||
                signal.runtime_sha256!==receipt.runtime_sha256) {
                negativeChecks.set(receipt.suite_id,cacheKey);
                return null;
            }
            return {schema_version:1,dispatch_id:job.dispatch_id,
                suite_id:receipt.suite_id,
                suite_sha256:receipt.suite_sha256,
                runtime_sha256:receipt.runtime_sha256,
                checkpoint_sha256:receipt.checkpoint_sha256,
                steps_completed:receipt.steps_completed,
                evaluated_at:receipt.evaluated_at,
                window_checkpoints:signal.window_checkpoints,
                minimum_drop:signal.minimum_drop,drop:signal.drop,
                baseline:signal.baseline,latest:signal.latest,
                confirmed_at:new Date().toISOString()};
        }catch(error) {
            this.issues.set('regression:'+job.id,
                'Independent regression Stop evidence unavailable: '+
                String(error.message||error).slice(0,160));
            return null;
        }
    }
    snapshot(projectId,jobs) {
        const samples=[],regressions=[],diagnostics=[];
        for(const job of jobs) {
            if(job.project_id!==projectId)continue;
            try {
                const stored=this.load(job.id,job.project_id);
                const records=stored.records;
                if(stored.diagnostics.length)diagnostics.push({
                    job_id:job.id,events:stored.diagnostics.slice(-32),
                });
                regressions.push(...regressionSignals(job,records));
                if(!records.length)continue;
                // Bound only interactive chart payloads. Sampling the
                // combined append order at a fixed stride can erase an
                // entire suite when multiple suites alternate checkpoints.
                // Allocate observations fairly by exact suite AND runtime
                // identity; preserve each cohort's first and latest point.
                // Every original receipt remains unchanged in the archive.
                let points=records;
                if(records.length>MAX_UI_POINTS) {
                    const groups=new Map();
                    for(const [index,row] of records.entries()) {
                        const key=JSON.stringify([row.suite_id,
                            row.suite_sha256||null,row.runtime_sha256||null]);
                        if(!groups.has(key))groups.set(key,[]);
                        groups.get(key).push(index);
                    }
                    const cohorts=[...groups.values()].sort((a,b)=>
                        b[b.length-1]-a[a.length-1]);
                    const allocation=new Map();
                    let budget=MAX_UI_POINTS,remaining=cohorts;
                    while(budget>0&&remaining.length) {
                        if(remaining.length>budget) {
                            // Pathological histories with more identities
                            // than points retain the most recent cohorts.
                            for(const cohort of remaining.slice(0,budget))
                                allocation.set(cohort,1);
                            break;
                        }
                        const share=Math.max(1,Math.floor(budget/remaining.length));
                        const complete=remaining.filter(cohort=>cohort.length<=share);
                        if(!complete.length) {
                            const extra=budget%remaining.length;
                            remaining.forEach((cohort,i)=>
                                allocation.set(cohort,share+(i<extra?1:0)));
                            break;
                        }
                        for(const cohort of complete) {
                            allocation.set(cohort,cohort.length);
                            budget-=cohort.length;
                        }
                        remaining=remaining.filter(cohort=>cohort.length>share);
                    }
                    const indices=[];
                    for(const cohort of cohorts) {
                        const count=allocation.get(cohort)||0;
                        if(count===1) {
                            indices.push(cohort[cohort.length-1]);
                        }else if(count>1) {
                            for(let i=0;i<count;i++)
                                indices.push(cohort[Math.round(
                                    i*(cohort.length-1)/(count-1))]);
                        }
                    }
                    indices.sort((a,b)=>a-b);
                    points=indices.map(index=>records[index]);
                }
                // Supersession is a comparison annotation, never a rewrite
                // of the immutable receipt. Only a strictly later training
                // coordinate under the exact same frozen suite AND runtime
                // can supersede an older observation. All points remain
                // inspectable, chartable and available for full export.
                const newest=new Map();
                const comparableKey=row=>
                    SHA256.test(String(row.suite_sha256||''))&&
                    SHA256.test(String(row.runtime_sha256||''))&&
                    Number.isSafeInteger(row.steps_completed)
                        ?row.suite_id+':'+row.suite_sha256+':'+row.runtime_sha256
                        :null;
                for(const row of records) {
                    const key=comparableKey(row);
                    if(key&&(!newest.has(key)||
                        row.steps_completed>newest.get(key).steps_completed))
                        newest.set(key,row);
                }
                // Keep one complete frozen definition per exact suite and
                // runtime in the bounded chart response. All original JSONL
                // rows remain intact; duplicating a 2 KiB manifest for every
                // checkpoint would inflate frequent polling and comparisons.
                const definitionsLast=new Map();
                const definitionKey=row=>JSON.stringify([
                    row.suite_id,row.suite_sha256,row.runtime_sha256||null,
                ]);
                points.forEach((row,index)=>{
                    if(typeof row.suite_definition_canonical==='string')
                        definitionsLast.set(definitionKey(row),index);
                });
                points=points.map((row,index)=>{
                    const latest=newest.get(comparableKey(row));
                    const superseded=latest&&
                        latest.steps_completed>row.steps_completed;
                    const compact=definitionsLast.get(definitionKey(row))===index
                        ?row:(({
                            suite_definition_canonical,...receipt
                        })=>receipt)(row);
                    return {...compact,comparison_status:superseded
                        ?'superseded':'complete',
                        ...(superseded?{
                            superseded_by_checkpoint_sha256:latest.checkpoint_sha256,
                            superseded_by_steps_completed:latest.steps_completed,
                        }:{})};
                });
                samples.push({job_id:job.id,points,
                    total_records:records.length});
                this.issues.delete(job.id);
            }catch(error) {
                this.issues.set(job.id,'Independent evaluation history unavailable: '+
                    String(error.message||error).slice(0,160));
            }
        }
        return {samples,regressions,diagnostics,storage_warning:this.error};
    }
}
module.exports={EvaluationHistory,regressionSignals};
