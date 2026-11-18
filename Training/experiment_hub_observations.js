'use strict';

// Append-only, project-neutral observations accepted from trusted native agents.
// This is diagnostic history, not a training, checkpoint or evaluation authority.
// Original trainer histories remain the source of truth for training updates.
const fs=require('node:fs');
const path=require('node:path');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const MAX_RECORD_BYTES=65536;
const THROUGHPUT_TAIL_BYTES=16*MAX_RECORD_BYTES;
const dirName='hub-observations-v1';

function stableThroughput(lines,job,workMetric) {
    let previous=null, seconds=0, work=0;
    const rates=[];
    for(let i=lines.length-1;i>=0&&rates.length<6;i--) {
        const row=JSON.parse(lines[i]);
        const count=row.metrics?.[workMetric],elapsed=row.metrics?.training_tracked_active_seconds;
        if(row.schema_version!==1||row.job_id!==job.id||row.project_id!==job.project_id||
            row.dispatch_id!==job.dispatch_id||row.experiment_id!==job.id||
            typeof row.segment_id!=='string'||!Number.isSafeInteger(row.steps_completed)||
            count?.unit!=='count'||!Number.isSafeInteger(count.value)||count.value<0||
            elapsed?.unit!=='seconds'||!Number.isFinite(elapsed.value)||elapsed.value<0)break;
        const point={work:count.value,seconds:elapsed.value,
            segment:row.segment_id,step:row.steps_completed};
        if(!previous) {
            previous=point;
            continue;
        }
        const duration=previous.seconds-point.seconds, completed=previous.work-point.work;
        if(point.segment!==previous.segment||point.step>previous.step||
            duration<0||completed<0)break;
        if(point.step===previous.step) {
            if(completed!==0)break;
            continue; // The same completed update may appear in a save receipt.
        }
        if(duration<45)continue;
        work+=completed;
        seconds+=duration;
        rates.push(completed/duration);
        previous=point;
    }
    const rate=work/seconds;
    return rates.length>=3&&seconds>=180&&rate>0&&
        rates.every(sample=>Math.abs(sample-rate)<=rate*.25)?rate:null;
}

class ObservationArchive {
    constructor(root) {
        this.directory=path.join(root,dirName);
        this.lastByJob=new Map();
        // A healthy job must never hide a different job's archive failure.
        // Track trainer and host-only streams independently.
        this.issues=new Map();
    }
    get error() {
        return this.issues.values().next().value||null;
    }
    trainerErrorFor(job) {
        // Only a trainer-stream failure prevents the normal host-only
        // fallback. A host-stream failure must be allowed to retry.
        return this.issues.has(job.id);
    }
    // Read only the exact dispatch's recent cumulative trainer receipts.
    // Missing, incompatible or unreadable evidence is an ordinary fallback.
    sustainedThroughput(job,workMetric) {
        if(!UUID.test(String(job?.id))||!UUID.test(String(job?.dispatch_id)))return null;
        let fd;
        try {
            const directory=fs.lstatSync(this.directory);
            if(!directory.isDirectory()||directory.isSymbolicLink()||
                (typeof process.getuid==='function'&&
                    (directory.uid!==process.getuid()||(directory.mode&0o077)!==0)))return null;
            const file=path.join(this.directory,job.id+'.jsonl');
            fd=fs.openSync(file,fs.constants.O_RDONLY|(fs.constants.O_NOFOLLOW||0)|
                (fs.constants.O_NONBLOCK||0));
            const stat=fs.fstatSync(fd),current=fs.lstatSync(file);
            if(!stat.isFile()||stat.nlink!==1||!current.isFile()||current.isSymbolicLink()||
                stat.dev!==current.dev||stat.ino!==current.ino||
                (typeof process.getuid==='function'&&
                    (stat.uid!==process.getuid()||(stat.mode&0o077)!==0)))return null;
            const length=Math.min(stat.size,THROUGHPUT_TAIL_BYTES);
            if(!length)return null;
            const buffer=Buffer.alloc(length);
            if(fs.readSync(fd,buffer,0,length,stat.size-length)!==length||
                buffer[length-1]!==10)return null;
            const lines=buffer.toString('utf8').split('\n');
            lines.pop();
            if(length<stat.size)lines.shift();
            return stableThroughput(lines,job,workMetric);
        }catch {
            return null;
        }finally {
            if(fd!==undefined)fs.closeSync(fd);
        }
    }
    record(job,progress,resourceMetrics,receivedMs) {
        if(!progress)return false;
        return this.append(job,progress,resourceMetrics,receivedMs,false,null);
    }
    // Host-only samples are distinct from trainer updates. They retain
    // observed CPU/GPU/memory behavior when the trainer's source timestamp
    // stalls (for example, inside a long PPO update).
    recordResources(job,resourceMetrics,receivedMs,sourceStatus) {
        if(!resourceMetrics||!Object.keys(resourceMetrics).length)return false;
        if(!['absent','not_advanced','read_error'].includes(sourceStatus))
            throw Error('Host observation needs an explicit trainer source status.');
        return this.append(job,null,resourceMetrics,receivedMs,true,sourceStatus);
    }
    append(job,progress,resourceMetrics,receivedMs,hostOnly,sourceStatus) {
        const key=hostOnly?String(job?.id)+':host':String(job?.id);
        try {
            if(!UUID.test(String(job?.id))||
                !/^[a-z][a-z0-9_-]{0,47}$/.test(String(job?.project_id))||
                !Number.isSafeInteger(receivedMs))
                throw Error('Invalid observation identity or receipt time.');
            if(!fs.existsSync(this.directory))
                fs.mkdirSync(this.directory,{mode:0o700});
            const directory=fs.lstatSync(this.directory);
            if(!directory.isDirectory()||directory.isSymbolicLink()||
                (typeof process.getuid==='function'&&(
                    directory.uid!==process.getuid()||(directory.mode&0o077)!==0)))
                throw Error('Observation archive directory is redirected or unowned.');
            const file=path.join(this.directory,job.id+
                (hostOnly?'-resources':'')+'.jsonl');
            const flags=fs.constants.O_CREAT|fs.constants.O_RDWR|
                fs.constants.O_APPEND|(fs.constants.O_NOFOLLOW||0);
            const fd=fs.openSync(file,flags,0o600);
            try {
                const stat=fs.fstatSync(fd),current=fs.lstatSync(file);
                if(!stat.isFile()||stat.nlink!==1||!current.isFile()||
                    current.isSymbolicLink()||
                    stat.dev!==current.dev||stat.ino!==current.ino||
                    (typeof process.getuid==='function'&&(
                        stat.uid!==process.getuid()||(stat.mode&0o077)!==0)))
                    throw Error('Observation archive file is unsafe or redirected.');
                let last=this.lastByJob.get(key);
                if(last===undefined) {
                    last=null;
                    if(stat.size) {
                        const length=Math.min(stat.size,MAX_RECORD_BYTES);
                        const buffer=Buffer.alloc(length);
                        if(fs.readSync(fd,buffer,0,length,stat.size-length)!==length||
                            buffer[length-1]!==10)
                            throw Error('Observation archive has an incomplete final record.');
                        const row=JSON.parse(buffer.toString('utf8').trimEnd().split('\n').at(-1));
                        const priorAt=Date.parse(hostOnly?
                            row.observed_at:row.source_modified_at);
                        if(row.job_id!==job.id||row.project_id!==job.project_id||
                            (hostOnly?row.observation_kind!=='host_only':
                                row.observation_kind==='host_only')||
                            !Number.isFinite(priorAt))
                            throw Error('Observation archive lineage is invalid.');
                        last={project_id:row.project_id,at:priorAt,
                            size:stat.size,dev:stat.dev,ino:stat.ino};
                    }
                    this.lastByJob.set(key,last);
                }
                if(last&&(last.project_id!==job.project_id||
                    last.size!==stat.size||last.dev!==stat.dev||last.ino!==stat.ino))
                    throw Error('Observation archive was replaced or truncated.');
                const at=hostOnly?receivedMs:
                    Date.parse(progress.source_modified_at);
                if(!Number.isFinite(at))
                    throw Error('Observation source timestamp is invalid.');
                if(last&&at<=last.at) {
                    this.issues.delete(key);
                    return false;
                }
                const observation={
                    schema_version:1,job_id:job.id,project_id:job.project_id,
                    ...(UUID.test(String(job.dispatch_id))?{dispatch_id:job.dispatch_id}:{}),
                    observed_at:new Date(receivedMs).toISOString(),
                    source_modified_at:hostOnly?null:progress.source_modified_at,
                    experiment_id:hostOnly?null:progress.experiment_id,
                    segment_id:hostOnly?null:progress.segment_id,
                    steps_completed:hostOnly?null:progress.steps_completed,
                    metrics:hostOnly?{}:progress.metrics,
                    resource_metrics:resourceMetrics,
                    ...(hostOnly?{
                        observation_kind:'host_only',
                        trainer_source_status:sourceStatus,
                    }:{}),
                };
                const body=Buffer.from(JSON.stringify(observation)+'\n');
                if(body.length>MAX_RECORD_BYTES)
                    throw Error('Observation record exceeds its size limit.');
                // Filesystems may accept fewer bytes than requested.
                // Complete the *same* bounded JSONL record before advancing
                // the durable size/timestamp cursor.
                try {
                    for(let offset=0;offset<body.length;) {
                        const n=fs.writeSync(fd,body,offset,body.length-offset);
                        if(!Number.isSafeInteger(n)||n<=0)
                            throw Error('Incomplete observation archive append.');
                        offset+=n;
                    }
                }catch(writeError) {
                    // A failed append must not leave a truncated JSON record
                    // blocking every later observation for this job. Preserve
                    // the previously validated complete archive prefix.
                    try{fs.ftruncateSync(fd,stat.size);}
                    catch(rollbackError) {
                        throw Error('Observation append failed and its previous size could not be restored: '+
                            String(rollbackError.message||rollbackError));
                    }
                    throw writeError;
                }
                this.lastByJob.set(key,{project_id:job.project_id,at,
                    size:stat.size+body.length,dev:stat.dev,ino:stat.ino});
                this.issues.delete(key);
                return true;
            }finally{fs.closeSync(fd);}
        }catch(error) {
            // Optional diagnostics must never fail an authenticated heartbeat
            // or alter any training lease or process lifecycle.
            this.issues.set(key,'Hub observation archive unavailable: '+
                String(error.message||error).slice(0,180));
            return false;
        }
    }
}
module.exports={ObservationArchive};
