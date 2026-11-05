'use strict';

// Bounded visualization samples only. The optional chart archive is not
// training evidence, checkpoint history, or project-owned metrics.
const fs=require('node:fs');
const crypto=require('node:crypto');
const {validateProgress}=require('./experiment_hub_progress');
const MAX_FILE_BYTES=4*1024*1024;
const MAX_JOBS=24;
const MAX_POINTS=96;
const PROJECT=/^[a-z][a-z0-9_-]{0,47}$/;
const MIN_SAMPLE_INTERVAL_MS=60000;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
function compact(progress,receivedMs) {
    if(progress.source==='hub-metrics-v1') {
        return {
            observed_at:new Date(receivedMs).toISOString(),
            source_modified_at:progress.source_modified_at,
            experiment_id:progress.experiment_id,
            segment_id:progress.segment_id,
            steps_completed:progress.steps_completed,
            metrics:progress.metrics,
        };
    }
    return {
        observed_at:new Date(receivedMs).toISOString(),
        source_modified_at:progress.source_modified_at,
        experiment_id:progress.experiment_id,
        segment_id:progress.segment_id,
        episodes_completed:progress.episodes_completed,
        total_environment_steps:progress.total_environment_steps,
        policy_version:progress.policy_version,
        curriculum_level:progress.curriculum_level,
        curriculum_window_success_rate:progress.curriculum_window_success_rate,
        curriculum_window_episodes:progress.curriculum_window_episodes,
        segment_success_rate:progress.segment_success_rate,
        simulation_steps_per_second:progress.simulation_steps_per_second,
        ppo_unique_samples_per_second:progress.ppo_unique_samples_per_second,
        latest_episode_policy_updated:progress.latest_episode_policy_updated,
    };
}
class ProgressHistory {
    constructor(file=null) {
        this.byJob=new Map();
        this.file=file;
        this.dirty=false;
        this.persistError=null;
        this.storageBlocked=false;
        this.last_saved_ms=0;
        if(!file||!fs.existsSync(file))return;
        try {
            if(fs.statSync(file).size>MAX_FILE_BYTES)
                throw Error('Chart archive is too large.');
            const saved=JSON.parse(fs.readFileSync(file,'utf8'));
            if(saved.version!==1||!Array.isArray(saved.samples)||
                saved.samples.length>MAX_JOBS)
                throw Error('Invalid chart archive format.');
            const seen=new Set();
            for(const entry of saved.samples) {
                if(!entry||!UUID.test(String(entry.job_id))||
                    !PROJECT.test(String(entry.project_id))||
                    entry.project_id==='bees'||seen.has(entry.job_id)||
                    !Array.isArray(entry.points)||!entry.points.length||
                    entry.points.length>MAX_POINTS||
                    !Number.isSafeInteger(entry.last_received_ms)||
                    !Number.isSafeInteger(entry.bucket_started_ms)||
                    entry.bucket_started_ms>entry.last_received_ms)
                    throw Error('Invalid chart archive entry.');
                seen.add(entry.job_id);
                const points=entry.points.map((point,i)=>{
                    if(!point||typeof point.observed_at!=='string'||
                        !Number.isFinite(Date.parse(point.observed_at))||
                        new Date(point.observed_at).toISOString()!==point.observed_at||
                        typeof point.break_before!=='boolean'||
                        (i===0&&point.break_before))
                        throw Error('Invalid chart archive sample.');
                    const {observed_at,break_before,...values}=point;
                    const verified=validateProgress({
                        ...values,schema_version:1,
                        source:entry.project_id==='ants'
                            ?'ants-summary-v1':'hub-metrics-v1',
                    },entry.project_id);
                    if(verified.experiment_id!==entry.experiment_id||
                        (i>0&&Date.parse(verified.source_modified_at)<=
                            Date.parse(entry.points[i-1].source_modified_at))||
                        (i>0&&Date.parse(observed_at)<
                            Date.parse(entry.points[i-1].observed_at)))
                        throw Error('Chart archive lineage or order invalid.');
                    return {...compact(verified,Date.parse(observed_at)),
                        break_before};
                });
                if(Date.parse(points[points.length-1].observed_at)>
                    entry.last_received_ms)
                    throw Error('Chart archive receipt order invalid.');
                this.byJob.set(entry.job_id,{
                    project_id:entry.project_id,
                    experiment_id:entry.experiment_id,
                    points,last_received_ms:entry.last_received_ms,
                    bucket_started_ms:entry.bucket_started_ms,
                });
            }
            this.last_saved_ms=Date.now();
        }catch(error) {
            // A damaged visualization archive must not block native work or
            // be overwritten without operator inspection.
            this.byJob.clear();
            this.storageBlocked=true;
            this.persistError='Stored comparison history is invalid; file retained for inspection.';
        }
    }
    record(jobId,projectId,progress,receivedMs=Date.now()) {
        if(!PROJECT.test(String(projectId))||projectId==='bees'||
            !UUID.test(String(jobId))||!progress||
            progress.source!==(projectId==='ants'
                ?'ants-summary-v1':'hub-metrics-v1')||
            !Number.isFinite(receivedMs))
            return false;
        const point=compact(progress,receivedMs);
        let entry=this.byJob.get(jobId);
        if(!entry) {
            // Old histories are discarded entirely; this is bounded memory,
            // never an archive or a source for training comparisons.
            if(this.byJob.size>=MAX_JOBS) {
                let oldestId=null,oldestTime=Infinity;
                for(const [id,item] of this.byJob) {
                    if(item.last_received_ms<oldestTime) {
                        oldestId=id;oldestTime=item.last_received_ms;
                    }
                }
                this.byJob.delete(oldestId);
            }
            entry={project_id:projectId,experiment_id:point.experiment_id,
                points:[],last_received_ms:receivedMs};
            this.byJob.set(jobId,entry);
        }
        if(entry.project_id!==projectId||
            entry.experiment_id!==point.experiment_id)return false;
        const previous=entry.points[entry.points.length-1];
        // The source file is written atomically. Repeated reads of the same
        // exact source version must never manufacture extra chart points.
        if(previous&&
            Date.parse(point.source_modified_at)<=
                Date.parse(previous.source_modified_at))return false;
        point.break_before=Boolean(previous&&(
            point.segment_id!==previous.segment_id||
            point.episodes_completed<previous.episodes_completed||
            point.policy_version<previous.policy_version||
            (Number.isSafeInteger(point.total_environment_steps)&&
                Number.isSafeInteger(previous.total_environment_steps)&&
                point.total_environment_steps<previous.total_environment_steps)||
            (Number.isSafeInteger(point.steps_completed)&&
                Number.isSafeInteger(previous.steps_completed)&&
                point.steps_completed<previous.steps_completed)||
            Object.entries(point.metrics||{}).some(([key,metric])=>
                previous.metrics?.[key]&&
                previous.metrics[key].unit!==metric.unit)));
        if(previous&&entry.points.length>1&&!point.break_before&&
            receivedMs-entry.bucket_started_ms<MIN_SAMPLE_INTERVAL_MS) {
            // Coalesce a recent sample without losing the discontinuity
            // carried by the point being replaced. A segment transition or
            // rollback must remain visually distinct after later updates.
            point.break_before=point.break_before||previous.break_before;
            entry.points[entry.points.length-1]=point;
        }else {
            entry.points.push(point);
            entry.bucket_started_ms=receivedMs;
        }
        if(entry.points.length>MAX_POINTS) {
            // Keep the first reference point and decimate the older samples
            // without erasing discontinuities or the latest observation.
            const old=entry.points;
            const retained=[old[0]];
            for(let i=2;i<old.length-1;i+=2) {
                retained.push({...old[i],
                    break_before:old[i].break_before||old[i-1].break_before});
            }
            retained.push({...old[old.length-1],
                break_before:old[old.length-1].break_before||
                    old[old.length-2].break_before});
            entry.points=retained;
        }
        entry.last_received_ms=receivedMs;
        this.dirty=true;
        return true;
    }
    persist(receivedMs=Date.now(),force=false) {
        if(!this.file||!this.dirty||this.storageBlocked)return;
        if(!force&&this.last_saved_ms&&
            receivedMs-this.last_saved_ms<MIN_SAMPLE_INTERVAL_MS)return;
        this.last_saved_ms=receivedMs;
        const pending=this.file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
        try {
            const saved={version:1,samples:[...this.byJob].map(([jobId,e])=>({
                job_id:jobId,project_id:e.project_id,
                experiment_id:e.experiment_id,
                last_received_ms:e.last_received_ms,
                bucket_started_ms:e.bucket_started_ms,
                points:e.points,
            }))};
            const body=JSON.stringify(saved)+String.fromCharCode(10);
            if(Buffer.byteLength(body,'utf8')>MAX_FILE_BYTES)
                throw Error('Chart archive exceeds size cap.');
            fs.writeFileSync(pending,body,{flag:'wx',mode:0o600});
            fs.renameSync(pending,this.file);
            this.dirty=false;
            this.persistError=null;
        }catch(error) {
            // The chart archive is non-authoritative. A storage failure
            // cannot fail an acknowledged native heartbeat or restart work.
            this.persistError='Comparison history was not saved: '+
                String(error.message||error).slice(0,120);
        }finally {
            try {
                if(fs.existsSync(pending))fs.unlinkSync(pending);
            }catch(error) {
                this.persistError='Chart-history temporary file cleanup failed: '+
                    String(error.message||error).slice(0,100);
            }
        }
    }
    snapshot(projectId,knownJobs) {
        const known=new Set(knownJobs.filter(j=>j.project_id===projectId)
            .map(j=>j.id));
        return {
            project_id:projectId,
            ephemeral:!this.file||Boolean(this.persistError),
            storage_warning:this.persistError,
            max_tracked_jobs:MAX_JOBS,max_samples_per_job:MAX_POINTS,
            samples:[...this.byJob].filter(([id,e])=>
                e.project_id===projectId&&known.has(id))
                .map(([jobId,e])=>({
                    job_id:jobId,points:e.points.map(p=>({...p})),
                })),
        };
    }
}
module.exports={ProgressHistory};
