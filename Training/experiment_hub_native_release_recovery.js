'use strict';

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA=/^[0-9a-f]{64}$/;

function nativeRecovery(job) {
    return job?.native_optimizer_recovery||job?.native_release_recovery||job?.native_collector_recovery||null;
}

function nativeRecoveryAttempts(job) {
    return job?.native_optimizer_attempts||job?.native_release_attempts||job?.native_collector_attempts||[];
}

function recoveryRecordJobId(record) {
    return record.native_recovery_of_job_id||record.native_release_recovery_of_job_id||record.job_id;
}

// Both recovery types retain the logical work order and its artifact root,
// but receive a distinct, durably journaled native service identity.
function physicalExecution(job,lease) {
    const recovery=nativeRecovery(job);
    if(!recovery)return {job,lease};
    const collector=job.project_id==='bees_native_collect';
    const optimizer=job.native_optimizer_recovery;
    if((optimizer
        ?!['bees_native','bees_native_optimizer_worker'].includes(job.project_id)||
            job.frozen_parameters?.optimizer_group!==true||!optimizer.finalizer||
            !SHA.test(optimizer.finalizer.optimizer_attempt_sha256)||
            !SHA.test(optimizer.finalizer.finalizer_sha256)
        :collector
        ?!job.native_collector_recovery||!SHA.test(recovery.collection_id)||
            recovery.collection_id!==job.frozen_parameters?.collection_id
        :job.project_id!=='bees_native'||job.frozen_parameters?.continual_release!==true||
            !job.native_release_recovery||!SHA.test(recovery.checkpoint_sha256))||
        !UUID.test(recovery.attempt_id)||recovery.attempt_id===job.id||
        !UUID.test(recovery.previous_attempt_id)||
        !UUID.test(recovery.previous_lease_id)||!UUID.test(recovery.original_lease_id)||
        recovery.previous_attempt_id===recovery.attempt_id)
        throw Error('Invalid authenticated native recovery identity.');
    return {job:{...job,id:recovery.attempt_id,
        native_recovery_of_job_id:job.id,
        native_recovery_previous_job_id:recovery.previous_attempt_id,
        native_recovery_previous_lease_id:recovery.previous_lease_id,
        native_recovery_original_lease_id:recovery.original_lease_id,
        ...(optimizer?{native_optimizer_finalize:optimizer.finalizer}:
            collector?{native_collector_resume:true}:
            {native_release_checkpoint_sha256:recovery.checkpoint_sha256})},
        lease:{...lease,job_id:recovery.attempt_id}};
}

function logicalJobId(job) {
    const logical=job.native_recovery_of_job_id;
    if(logical===undefined)return job.id;
    if((job.native_optimizer_finalize!==undefined
        ?!['bees_native','bees_native_optimizer_worker'].includes(job.project_id)||
            job.frozen_parameters?.optimizer_group!==true||
            !SHA.test(job.native_optimizer_finalize?.optimizer_attempt_sha256)||
            !SHA.test(job.native_optimizer_finalize?.finalizer_sha256)
        :job.project_id==='bees_native_collect'
        ?job.native_collector_resume!==true||
            !SHA.test(job.frozen_parameters?.collection_id)
        :job.project_id!=='bees_native'||job.frozen_parameters?.continual_release!==true||
            !SHA.test(job.native_release_checkpoint_sha256))||
        !UUID.test(logical)||logical===job.id||
        !UUID.test(job.native_recovery_previous_job_id)||
        !UUID.test(job.native_recovery_previous_lease_id)||
        !UUID.test(job.native_recovery_original_lease_id)||
        job.native_recovery_previous_job_id===job.id)
        throw Error('Invalid local native recovery execution ancestry.');
    return logical;
}

function recoveryPredecessor(records,job) {
    const id=logicalJobId(job);
    if(id===job.id)return null;
    const original=records.find(row=>row.job_id===id);
    const prior=records.find(row=>row.job_id===job.native_recovery_previous_job_id);
    const related=records.filter(row=>recoveryRecordJobId(row)===id);
    if(!original||!prior||!related.includes(prior)||
        original.project_id!==job.project_id||prior.project_id!==job.project_id||
        original.lease_id!==job.native_recovery_original_lease_id||
        prior.lease_id!==job.native_recovery_previous_lease_id||
        prior.state!=='failed'||related.some(row=>row.state!=='failed')||
        (original.artifact_lineage_root_id||original.job_id)!==
            (job.artifact_lineage_root_id||id)||
        (prior.continuation_of_job_id||null)!==(job.continuation_of_job_id||null))
        throw Error('Native recovery lacks its original inactive failed native owner.');
    return prior;
}

function completedPredecessor(records,id) {
    const original=records.find(row=>row.job_id===id);
    if(!original||original.state==='finished')return original;
    const recovered=records.filter(row=>row.job_id!==id&&recoveryRecordJobId(row)===id&&
        row.state==='finished'&&row.project_id===original.project_id&&
        row.artifact_lineage_root_id===original.artifact_lineage_root_id);
    if(recovered.length>1)throw Error('Conflicting completed native recovery attempts.');
    return recovered[0]||original;
}

module.exports={nativeRecovery,nativeRecoveryAttempts,recoveryRecordJobId,
    physicalExecution,logicalJobId,recoveryPredecessor,completedPredecessor};
