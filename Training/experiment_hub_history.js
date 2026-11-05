'use strict';

// Ephemeral visualization samples only. This is not training evidence,
// a checkpoint history or a durable replacement for project-owned metrics.
const MAX_JOBS=24;
const MAX_POINTS=96;
const MIN_SAMPLE_INTERVAL_MS=60000;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
function compact(progress,receivedMs) {
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
    constructor(){this.byJob=new Map();}
    record(jobId,projectId,progress,receivedMs=Date.now()) {
        if(projectId!=='ants'||!UUID.test(String(jobId))||!progress||
            progress.source!=='ants-summary-v1'||
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
        if(entry.experiment_id!==point.experiment_id)return false;
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
                point.total_environment_steps<previous.total_environment_steps)));
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
        return true;
    }
    snapshot(projectId,knownJobs) {
        const known=new Set(knownJobs.filter(j=>j.project_id===projectId)
            .map(j=>j.id));
        return {
            project_id:projectId,ephemeral:true,
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
