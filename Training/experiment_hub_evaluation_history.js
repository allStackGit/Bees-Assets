'use strict';

// Durable, independent evaluation observations. This archive is never used
// for native Stop, candidate promotion, leases or checkpoint authorization.
const fs=require('node:fs');
const path=require('node:path');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROJECT=/^[a-z][a-z0-9_-]{0,47}$/;
const MAX_BYTES=64*1024*1024;
const MAX_UI_POINTS=1024;

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
    snapshot(projectId,jobs) {
        const samples=[];
        for(const job of jobs) {
            if(job.project_id!==projectId)continue;
            try {
                const records=this.load(job.id,job.project_id).records;
                if(!records.length)continue;
                // Keep first and latest checkpoints and evenly sample the
                // middle for interactive charting. All rows stay on disk.
                const points=records.length<=MAX_UI_POINTS?records:
                    records.filter((row,i)=>i===0||i===records.length-1||
                        i%Math.ceil(records.length/(MAX_UI_POINTS-2))===0)
                        .slice(-(MAX_UI_POINTS));
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
