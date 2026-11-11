'use strict';

// Append-only, project-neutral observations accepted from trusted native agents.
// This is diagnostic history, not a training, checkpoint or evaluation authority.
// Original trainer histories remain the source of truth for training updates.
const fs=require('node:fs');
const path=require('node:path');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const MAX_RECORD_BYTES=65536;
const dirName='hub-observations-v1';

class ObservationArchive {
    constructor(root) {
        this.directory=path.join(root,dirName);
        this.lastByJob=new Map();
        this.error=null;
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
            const key=hostOnly?job.id+':host':job.id;
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
                    this.error=null;
                    return false;
                }
                const observation={
                    schema_version:1,job_id:job.id,project_id:job.project_id,
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
                if(fs.writeSync(fd,body,0,body.length)!==body.length)
                    throw Error('Incomplete observation archive append.');
                this.lastByJob.set(key,{project_id:job.project_id,at,
                    size:stat.size+body.length,dev:stat.dev,ino:stat.ino});
                this.error=null;
                return true;
            }finally{fs.closeSync(fd);}
        }catch(error) {
            // Optional diagnostics must never fail an authenticated heartbeat
            // or alter any training lease or process lifecycle.
            this.error='Hub observation archive unavailable: '+
                String(error.message||error).slice(0,180);
            return false;
        }
    }
}
module.exports={ObservationArchive};
