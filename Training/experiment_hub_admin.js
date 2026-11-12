'use strict';

// Shared project-neutral dashboard API used by the production Bees manager
// and the isolated native qualification dashboard. Bees training actions stay
// with the existing operator; this module cannot invoke them or bind a listener.
const {placements,plan}=require('./experiment_hub_resources');

const POST_ACTIONS={
    '/api/hub/projects':[201,'addProject'],
    '/api/hub/configurations':[200,'saveConfiguration'],
    '/api/hub/machines':[201,'addMachine'],
    '/api/hub/machines/budget':[200,'updateMachineBudget'],
    '/api/hub/machines/gpu-budget':[200,'updateMachineGpuBudget'],
    '/api/hub/machines/gpus':[200,'bindMachineGpus'],
    '/api/hub/machines/enroll':[201,'enrollMachine'],
    '/api/hub/machines/revoke':[200,'revokeMachine'],
    '/api/hub/leases':[201,'requestLease'],
    '/api/hub/leases/release':[200,'releaseLease'],
    '/api/hub/jobs':[201,'queueConfiguration'],
    '/api/hub/pipelines':[201,'queuePipeline'],
    '/api/hub/distributed/tinybrains':[201,'queueDistributedTinyBrains'],
    '/api/hub/distributed/tinybrains/start-collectors':[200,'authorizeDistributedCollectors'],
    '/api/hub/distributed/tinybrains/cancel-launch':[200,'cancelDistributedLaunch'],
    '/api/hub/candidates/rank':[200,'rankCandidates'],
    '/api/hub/candidates/schedule':[201,'queueCandidateSelection'],
    '/api/hub/candidates/cancel':[200,'cancelCandidateSelection'],
    '/api/hub/candidates/promote':[201,'queueBestCandidate'],
    '/api/hub/sequences':[201,'queueSequence'],
    '/api/hub/trials':[201,'queueTrials'],
    '/api/hub/resource-bundles':[201,'queueResourceBundle'],
    '/api/hub/resource-bundles/cancel':[200,'cancelResourceBundle'],
    '/api/hub/trials/cancel-queued':[200,'cancelQueuedTrials'],
    '/api/hub/jobs/cancel':[200,'cancelJob'],
    '/api/hub/jobs/cancel-future':[200,'cancelFutureJobs'],
    '/api/hub/jobs/allocate':[200,'allocateJobs'],
    '/api/hub/jobs/move-claim':[200,'moveJobClaim'],
    '/api/hub/jobs/move-claims':[200,'moveJobClaims'],
    '/api/hub/automation':[200,'configureAutomation'],
    '/api/hub/jobs/qualify-gpu':[200,'requestGpuQualification'],
    '/api/hub/jobs/withdraw-gpu-probe':[200,'withdrawGpuQualification'],
    '/api/hub/jobs/start':[200,'authorizeStart'],
    '/api/hub/jobs/withdraw-start':[200,'withdrawStart'],
    '/api/hub/jobs/stop':[200,'authorizeStop'],
    '/api/hub/jobs/reinspect-checkpoint':[200,'requestCheckpointReinspection'],
};
const GET_PATHS=new Set([
    '/api/hub/jobs/plan','/api/hub/automation','/api/hub/progress',
    '/api/hub/state','/api/hub/placements','/api/hub/evaluation/targets',
]);
function isHubAdminPath(method,pathname) {
    return method==='GET'?GET_PATHS.has(pathname):
        method==='POST'&&(pathname==='/api/hub/plan'||Object.hasOwn(POST_ACTIONS,pathname));
}

function route(hub,method,pathname,query,value,bees) {
    if(!isHubAdminPath(method,pathname))return null;
    if(method==='GET') {
        if(pathname==='/api/hub/jobs/plan')
            return {status:200,data:hub.plannedJobs()};
        if(pathname==='/api/hub/automation')
            return {status:200,data:hub.automationStatus()};
        if(pathname==='/api/hub/evaluation/targets')
            return {status:200,data:{targets:hub.evaluationTargets(
                query.get('project_id'),query.get('machine_id'),query.get('suite_id'))}};
        if(pathname==='/api/hub/progress')
            return {status:200,data:hub.progressComparison(query.get('project_id'))};
        if(pathname==='/api/hub/state')
            return {status:200,data:{...hub.snapshot(),bees:bees||{
                active_id:null,queue_armed:false,runs:[],
            }}};
        if(pathname==='/api/hub/placements') {
            const snapshot=hub.snapshot();
            const gpus=(query.get('gpus')||'').split(',').filter(Boolean);
            return {status:200,data:{
                placements:placements(snapshot.machines,snapshot.reservations,{
                    cpus:Number(query.get('cpus')),
                    ram_mb:Number(query.get('ram_mb')),gpus,
                    gpu_count:query.has('gpu_count')?Number(query.get('gpu_count')):undefined,
                    min_gpu_vram_mb:query.has('min_gpu_vram_mb')
                        ?Number(query.get('min_gpu_vram_mb')):undefined,
                    gpu_mode:query.get('gpu_mode')||'exclusive',
                    gpu_vram_mb:query.has('gpu_vram_mb')
                        ?Number(query.get('gpu_vram_mb')):undefined,
                    gpu_compute_pct:query.has('gpu_compute_pct')
                        ?Number(query.get('gpu_compute_pct')):undefined,
                }),
                note:'Inventory and reservations are advisory until authenticated agents enforce isolation.',
            }};
        }
        return null;
    }
    if(pathname==='/api/hub/plan') {
        const snapshot=hub.snapshot();
        return {status:200,data:{
            placements:plan(snapshot.machines,snapshot.reservations,value.jobs),
            note:'Advisory only: neither scheduling nor GPU compute isolation is enforced.',
        }};
    }
    const action=POST_ACTIONS[pathname];
    return action?{status:action[0],data:hub[action[1]](value)}:null;
}
module.exports={route,isHubAdminPath};
