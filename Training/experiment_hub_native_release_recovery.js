'use strict';

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA=/^[0-9a-f]{64}$/;

// A recovery retains the authenticated logical job, but its native service
// has its own durable OS identity. Existing ownership journals are immutable.
function physicalExecution(job,lease) {
    const recovery=job.native_release_recovery;
    if(!recovery)return {job,lease};
    if(job.project_id!=='bees_native'||job.frozen_parameters?.continual_release!==true||
        !UUID.test(recovery.attempt_id)||recovery.attempt_id===job.id||
        !UUID.test(recovery.previous_attempt_id)||
        !UUID.test(recovery.previous_lease_id)||!UUID.test(recovery.original_lease_id)||
        recovery.previous_attempt_id===recovery.attempt_id||
        !SHA.test(recovery.checkpoint_sha256))
        throw Error('Invalid authenticated native release recovery identity.');
    return {job:{...job,id:recovery.attempt_id,
        native_release_recovery_of_job_id:job.id,
        native_release_previous_job_id:recovery.previous_attempt_id,
        native_release_previous_lease_id:recovery.previous_lease_id,
        native_release_original_lease_id:recovery.original_lease_id,
        native_release_checkpoint_sha256:recovery.checkpoint_sha256},
        lease:{...lease,job_id:recovery.attempt_id}};
}

function logicalJobId(job) {
    const logical=job.native_release_recovery_of_job_id;
    if(logical===undefined)return job.id;
    if(job.project_id!=='bees_native'||job.frozen_parameters?.continual_release!==true||
        !UUID.test(logical)||logical===job.id||
        !UUID.test(job.native_release_previous_job_id)||
        !UUID.test(job.native_release_previous_lease_id)||
        !UUID.test(job.native_release_original_lease_id)||
        job.native_release_previous_job_id===job.id||
        !SHA.test(job.native_release_checkpoint_sha256))
        throw Error('Invalid local release-only execution ancestry.');
    return logical;
}

function recoveryPredecessor(records,job) {
    const id=logicalJobId(job);
    if(id===job.id)return null;
    const original=records.find(row=>row.job_id===id);
    const prior=records.find(row=>row.job_id===job.native_release_previous_job_id);
    const related=records.filter(row=>row.job_id===id||
        row.native_release_recovery_of_job_id===id);
    if(!original||!prior||!related.includes(prior)||
        original.project_id!==job.project_id||prior.project_id!==job.project_id||
        original.lease_id!==job.native_release_original_lease_id||
        prior.lease_id!==job.native_release_previous_lease_id||
        prior.state!=='failed'||related.some(row=>row.state!=='failed')||
        (original.artifact_lineage_root_id||original.job_id)!==
            (job.artifact_lineage_root_id||id)||
        (prior.continuation_of_job_id||null)!==(job.continuation_of_job_id||null))
        throw Error('Release recovery lacks its original inactive failed native owner.');
    return prior;
}

function completedPredecessor(records,id) {
    const original=records.find(row=>row.job_id===id);
    if(!original||original.state==='finished')return original;
    const recovered=records.filter(row=>row.native_release_recovery_of_job_id===id&&
        row.state==='finished'&&row.project_id===original.project_id&&
        row.artifact_lineage_root_id===original.artifact_lineage_root_id);
    if(recovered.length>1)throw Error('Conflicting completed native release attempts.');
    return recovered[0]||original;
}

module.exports={physicalExecution,logicalJobId,recoveryPredecessor,completedPredecessor};
