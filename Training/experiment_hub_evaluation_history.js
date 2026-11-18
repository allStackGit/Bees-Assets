'use strict';

// Durable, independent evaluation observations. This archive is never used
// for native Stop, candidate promotion, leases or checkpoint authorization.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {evaluationEvidence}=require('./experiment_hub_evaluation');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROJECT=/^[a-z][a-z0-9_-]{0,47}$/;
const MAX_BYTES=64*1024*1024;
const MAX_UI_POINTS=1024;
const SHA256=/^[a-f0-9]{64}$/;
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
const HISTORICAL_SUFFIX='.historical.json';
const MAX_IMPORT_RECORDS=128;
const MAX_IMPORT_BYTES=65536;

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
    file(jobId) {
        if(!UUID.test(String(jobId)))throw Error('Invalid evaluation job ID.');
        return path.join(this.directory,jobId+'.jsonl');
    }
    load(jobId,projectId) {
        const existing=this.loaded.get(jobId);
        if(existing)return existing;
        this.directoryReady();
        const file=this.file(jobId);
        const records=[],seen=new Map();
        let stat=null;
        if(fs.existsSync(file)) {
            stat=fs.lstatSync(file);
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
                    !UUID.test(String(row.dispatch_id))||
                    typeof row.suite_id!=='string'||
                    !/^[a-z][a-z0-9_-]{0,47}$/.test(row.suite_id)||
                    typeof row.checkpoint_sha256!=='string'||
                    !/^[a-f0-9]{64}$/.test(row.checkpoint_sha256)||
                    !Number.isSafeInteger(row.trials)||row.trials<1||
                    !Number.isSafeInteger(row.successes)||row.successes<0||
                    row.successes>row.trials)
                    throw Error('Invalid independent evaluation archive record.');
                const key=row.dispatch_id+':'+row.suite_id+':'+row.checkpoint_sha256;
                if(seen.has(key))throw Error('Duplicate evaluation history record.');
                seen.set(key,JSON.stringify(row));
                records.push(row);
            }
        }
        const loaded={project_id:projectId,records,seen,
            size:stat?.size||0,dev:stat?.dev??null,ino:stat?.ino??null};
        this.loaded.set(jobId,loaded);
        return loaded;
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
            if(data.seen.has(key))return false;
            const row={...receipt,schema_version:1,job_id:id,
                project_id:job.project_id,observed_at:new Date().toISOString()};
            const body=Buffer.from(JSON.stringify(row)+'\n');
            if(body.length>8192||data.size+body.length>MAX_BYTES)
                throw Error('Independent evaluation history exceeds its size limit.');
            const file=this.file(id);
            const fd=fs.openSync(file,fs.constants.O_CREAT|fs.constants.O_RDWR|
                fs.constants.O_APPEND|(fs.constants.O_NOFOLLOW||0),0o600);
            try {
                const st=fs.fstatSync(fd),current=fs.lstatSync(file);
                if(!st.isFile()||st.nlink!==1||!current.isFile()||
                    current.isSymbolicLink()||st.ino!==current.ino||
                    st.dev!==current.dev||st.size!==data.size||
                    (data.ino!==null&&(data.ino!==st.ino||data.dev!==st.dev))||
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
                }catch(error) {
                    fs.ftruncateSync(fd,st.size);
                    throw error;
                }
                data.size+=body.length;
                data.dev=st.dev;data.ino=st.ino;
            }finally{fs.closeSync(fd);}
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
                const records=this.load(id,meta.project_id).records;
                if(!records.length||records.length>MAX_IMPORT_RECORDS||
                    records.some(row=>row.source_kind!=='historical_checkpoint_audit'||
                        row.source_report_sha256!==meta.source_report_sha256||
                        row.evaluation_status!=='complete'))
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
    snapshot(projectId,jobs) {
        const samples=[];
        for(const job of jobs) {
            if(job.project_id!==projectId)continue;
            try {
                const records=this.load(job.id,job.project_id).records;
                if(!records.length)continue;
                // Keep first and latest checkpoints and evenly sample the
                // middle for interactive charting. All rows stay on disk.
                let points=records;
                if(records.length>MAX_UI_POINTS) {
                    const middle=[];
                    const stride=Math.ceil((records.length-2)/(MAX_UI_POINTS-2));
                    for(let i=1;i<records.length-1;i+=stride)
                        middle.push(records[i]);
                    points=[records[0],...middle.slice(0,MAX_UI_POINTS-2),
                        records[records.length-1]];
                }
                samples.push({job_id:job.id,points});
                this.issues.delete(job.id);
            }catch(error) {
                this.issues.set(job.id,'Independent evaluation history unavailable: '+
                    String(error.message||error).slice(0,160));
            }
        }
        return {samples,storage_warning:this.error};
    }
}
module.exports={EvaluationHistory};
