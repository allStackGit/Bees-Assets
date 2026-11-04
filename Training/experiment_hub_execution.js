'use strict';

// Machine-local bridge from authenticated, machine-bound job directives to
// a separately installed trusted adapter. This module has no HTTP listener.
const os=require('node:os');
const path=require('node:path');
const {LinuxSupervisor}=require('./experiment_hub_linux_supervisor');
const {inspectHost}=require('./experiment_hub_host');
const ID=/^[a-z][a-z0-9_-]{0,47}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

class LocalExecution {
    constructor({machineId,supervisor,projects}) {
        if(!ID.test(String(machineId||''))||!supervisor||
            !Array.isArray(projects)||!projects.length||
            projects.some(p=>!ID.test(p)||p==='bees')||
            new Set(projects).size!==projects.length)
            throw Error('Invalid machine-local execution adapter configuration.');
        this.machineId=machineId;
        this.supervisor=supervisor;
        this.projects=projects.slice();
    }
    advertisement(){
        return {local_executor:true,installed_projects:this.projects.slice()};
    }
    async handle(order) {
        if(!order||!['start','stop','inspect'].includes(order.action)||
            !UUID.test(String(order.dispatch_id||''))||
            !order.job||!order.lease||
            !UUID.test(String(order.job.id||''))||
            !UUID.test(String(order.lease.id||''))||
            order.job.lease_id!==order.lease.id||
            order.lease.job_id!==order.job.id||
            order.lease.machine_id!==this.machineId||
            order.lease.project_id!==order.job.project_id||
            !this.projects.includes(order.job.project_id))
            throw Error('Rejected invalid or unauthorized local dispatch instruction.');
        const {job,lease}=order;
        let observed,diagnostic=null;
        try {
            const record=this.supervisor.snapshot().find(r=>r.job_id===job.id);
            if(record&&(record.lease_id!==lease.id||
                record.project_id!==job.project_id))
                throw Error('Existing locally supervised service has different lease ownership.');
            if(order.action==='start') {
                // An ambiguous, previously issued launch is reconciled, never
                // launched again, including after agent/coordinator restarts.
                observed=record
                    ?await this.supervisor.reconcile(job.id)
                    :await this.supervisor.start(job,lease);
            }else if(!record) {
                // No journal is not proof that no process exists. The
                // operator must inspect the deterministic native service.
                observed={state:'unverified'};
                diagnostic='Missing local ownership record; inspect systemd before recovery.';
            }else if(order.action==='stop') {
                observed=await this.supervisor.stop(job.id);
            }else {
                observed=await this.supervisor.reconcile(job.id);
            }
        }catch(error) {
            diagnostic=String(error.message||error).slice(0,220);
            const record=this.supervisor.snapshot().find(r=>r.job_id===job.id);
            if(record&&record.lease_id===lease.id) {
                try{observed=await this.supervisor.reconcile(job.id);}
                catch{observed=null;}
            }
            if(!observed)observed={state:'unverified'};
        }
        const state=['launch_requested','running','stop_requested','finished','failed','stopped']
            .includes(observed.state)?observed.state:'unverified';
        return {
            job_id:job.id,lease_id:lease.id,
            dispatch_id:order.dispatch_id,state,
            ...(diagnostic?{diagnostic}:{}),
            ...(Number.isSafeInteger(observed.memory_current_mb)&&
                observed.memory_current_mb>=0
                ?{memory_current_mb:observed.memory_current_mb}:{}),
            ...(Number.isSafeInteger(observed.cpu_time_ns)&&
                observed.cpu_time_ns>=0
                ?{cpu_time_ns:observed.cpu_time_ns}:{}),
            ...(Number.isSafeInteger(observed.process_tasks)&&
                observed.process_tasks>=0
                ?{process_tasks:observed.process_tasks}:{}),
        };
    }
}
function fromEnvironment(env=process.env,options={}) {
    if(env.EXPERIMENT_HUB_ENABLE_EXECUTION!=='1')return null;
    const hostOs=options.hostOs||os;
    if(hostOs.platform()!=='linux')
        throw Error('Local native execution is supported only on Linux.');
    const adapterFile=env.EXPERIMENT_HUB_LOCAL_ADAPTERS;
    const root=env.EXPERIMENT_HUB_LOCAL_ROOT;
    if(typeof adapterFile!=='string'||!path.isAbsolute(adapterFile)||
        typeof root!=='string'||!path.isAbsolute(root))
        throw Error('Specify absolute local adapter module and supervisor state directory.');
    // This module path is chosen by the machine administrator in local
    // environment configuration, never by the Hub, a job or a heartbeat.
    const adapters=(options.loadAdapters||require)(adapterFile);
    if(!adapters||typeof adapters!=='object'||Array.isArray(adapters)||
        !Object.keys(adapters).length||
        Object.entries(adapters).some(([project,adapter])=>
            !ID.test(project)||project==='bees'||
            !adapter||typeof adapter!=='object'||
            typeof adapter.executable!=='string'||
            !path.isAbsolute(adapter.executable)||
            typeof adapter.args!=='function'||
            (adapter.cwd!=null&&
                (typeof adapter.cwd!=='string'||!path.isAbsolute(adapter.cwd)))))
        throw Error('Install one or more valid locally trusted, non-Bees project adapters.');
    const machineId=env.EXPERIMENT_HUB_MACHINE_ID;
    if(!ID.test(String(machineId||'')))throw Error('Invalid local machine identity.');
    const capabilities=(options.inspectHost||inspectHost)();
    if(!capabilities.cgroup_v2_detected||
        !['cpu','memory'].every(c=>capabilities.cgroup_controllers.includes(c)))
        throw Error('Required Linux CPU and memory controllers are not available.');
    const supervisor=options.createSupervisor
        ?options.createSupervisor({root,machineId,adapters})
        :new LinuxSupervisor({root,machineId,adapters});
    return new LocalExecution({machineId,supervisor,
        projects:Object.keys(adapters)});
}
module.exports={LocalExecution,fromEnvironment};
