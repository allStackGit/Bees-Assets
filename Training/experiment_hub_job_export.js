'use strict';

// Project-neutral, read-only export orchestration. The browser chooses IDs and
// metric names, never executable paths or filesystem roots. The coordinator
// owns only locally mounted artifacts; remote-only files are reported missing.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROJECT=/^[a-z][a-z0-9_-]{0,47}$/;
const METRIC=/^[a-z][a-z0-9_]{0,63}$/;
const KEEP_MS=2*60*60*1000;
const JOB_FIELDS=[
    'id','project_id','configuration_name','configuration_sha256',
    'frozen_parameters','metric_selection','resources','resource_bundle',
    'trial_batch','distributed_rollout_managed','status',
    'assigned_machine_id','continuation_of_job_id','artifact_lineage_root_id',
    'checkpoint_model_sha256','checkpoint_ready_verified',
    'checkpoint_stop_verified','created_at','capacity_released_at',
    'evaluation_stop','evaluation_stop_evidence','evaluation_candidates',
    'last_evaluation','final_progress','final_resource_usage',
    'execution_diagnostic','native_bees_run_id',
];
// Exported job metadata is operator-readable but can leave the machine when
// downloaded. Never copy credential-like configuration fields verbatim.
function isCredentialField(key) {
    // JSON configuration keys can be camelCase, PascalCase, snake_case,
    // kebab-case, or condensed acronyms (APIKEY, SSHKEY, CLIENTSECRET).
    const words=key.replace(/([A-Z]+)([A-Z][a-z])/g,'$1_$2')
        .replace(/([a-z0-9])([A-Z])/g,'$1_$2')
        .replace(/[^a-zA-Z0-9]+/g,'_').toLowerCase();
    if(/(?:^|_)(?:token|tokens|password|passwords|passwd|secret|secrets|credential|credentials|api_key|private_key|key_file|authorization|access_key|ssh_key|signing_key|encryption_key|connection_string|dsn)(?:_|$)/.test(words))
        return true;
    // Word-boundary splitting cannot infer boundaries within all-caps or
    // all-lowercase identifiers. Check only established credential compounds,
    // not bare 'key', so ordinary fields stay visible.
    const collapsed=words.replace(/_/g,'');
    return /(?:apikey|privatekey|keyfile|accesskey|sshkey|signingkey|encryptionkey|clientsecret|authtoken|accesstoken|refreshtoken|idtoken|bearertoken|connectionstring)/.test(collapsed);
}
function redactMetadata(value,depth=0) {
    if(depth>20)return '[nested configuration omitted]';
    if(Array.isArray(value))
        return value.map(item=>redactMetadata(item,depth+1));
    if(value&&typeof value==='object')
        return Object.fromEntries(Object.entries(value).map(([key,item])=>[
            key,isCredentialField(key)
                ?'[redacted]':redactMetadata(item,depth+1),
        ]));
    return value;
}
function trustedRoot(file) {
    if(typeof file!=='string'||!path.isAbsolute(file))return null;
    try {
        const stat=fs.lstatSync(file);
        if(!stat.isDirectory()||stat.isSymbolicLink()||
            (typeof process.getuid==='function'&&stat.uid!==process.getuid())||
            fs.realpathSync(file)!==file)return null;
        return file;
    }catch { return null; }
}
function loadRoots(dataRoot) {
    const roots=Object.assign(Object.create(null),{
        tinybrains:path.join(dataRoot,'tinybrains-jobs'),
    });
    const machineRoots=Object.create(null);
    // The coordinator selects all export directories from this private
    // operator-owned config. An optional machine mount is never an authority
    // to change native job ownership or execute a remote command.
    const config=path.join(dataRoot,'job-export-roots.json');
    if(fs.existsSync(config)) {
        const stat=fs.lstatSync(config);
        if(!stat.isFile()||stat.isSymbolicLink()||
            (typeof process.getuid==='function'&&stat.uid!==process.getuid())||
            (stat.mode&0o077)!==0)
            throw Error('Project export root configuration must be a private regular file.');
        const entries=JSON.parse(fs.readFileSync(config,'utf8'));
        if(!entries||typeof entries!=='object'||Array.isArray(entries))
            throw Error('Invalid project export root configuration.');
        const mounts=Object.hasOwn(entries,'@machine_roots')
            ?entries['@machine_roots']:{};

        delete entries['@machine_roots'];
        if(Object.keys(entries).length>32||
            Object.entries(entries).some(([id,dir])=>!PROJECT.test(id)||
                typeof dir!=='string'||!path.isAbsolute(dir))||
            !mounts||typeof mounts!=='object'||Array.isArray(mounts)||
            Object.keys(mounts).length>32||
            Object.entries(mounts).some(([machine,projects])=>
                !PROJECT.test(machine)||!projects||typeof projects!=='object'||
                Array.isArray(projects)||Object.keys(projects).length>32||
                Object.entries(projects).some(([project,dir])=>
                    !PROJECT.test(project)||typeof dir!=='string'||
                    !path.isAbsolute(dir))))
            throw Error('Project/machine export root configuration requires absolute directories.');
        Object.assign(roots,entries);
        Object.assign(machineRoots,mounts);
    }
    return {roots,machineRoots};
}
// The reservation ledger is historical admission evidence, not proof that
// the OS used a given number of cores or that a GPU delivered useful work.
// Keep immutable physical GPU bindings available for offline comparisons.
function resourceProvenance(snapshot,selectedJobs) {
    const registry=Array.isArray(snapshot.jobs)?snapshot.jobs:[];
    const reservations=Array.isArray(snapshot.reservations)?snapshot.reservations:[];
    const leaseFor=job=>{
        const lease=reservations.find(item=>item.id===job.lease_id&&
            item.job_id===job.id&&item.project_id===job.project_id&&
            item.machine_id===job.assigned_machine_id&&
            item.issued_by==='experiment-hub');
        return lease?redactMetadata(Object.fromEntries([
            'id','project_id','machine_id','status','resources',
            'gpu_bindings','created_at','acknowledged_at','released_at',
        ].filter(field=>Object.hasOwn(lease,field))
            .map(field=>[field,lease[field]]))):null;
    };
    return {
        schema_version:1,
        scope:'selected jobs and their explicitly managed collector sidecars',
        resource_claims_are_not_utilization:true,
        jobs:selectedJobs.map(selected=>{
            const job=registry.find(item=>item.id===selected.id&&
                item.project_id===selected.project_id);
            const collectors=job?.project_id==='tinybrains'&&
                job.distributed_rollout_managed===true
                ?registry.filter(other=>other.project_id==='tinybrains_rollout'&&
                    other.frozen_parameters?.learner_job_id===job.id)
                :[];
            return {
                job_id:selected.id,project_id:selected.project_id,
                assigned_machine_id:job?.assigned_machine_id??null,
                dispatch_id:job?.dispatch_id??null,
                lease:job?leaseFor(job):null,
                collectors:collectors.map(child=>({
                    job_id:child.id,project_id:child.project_id,
                    assigned_machine_id:child.assigned_machine_id??null,
                    status:child.status,dispatch_id:child.dispatch_id??null,
                    frozen_threads:child.frozen_parameters?.threads??null,
                    lease:leaseFor(child),
                })),
            };
        }),
    };
}
class JobExporter {
    constructor(dataRoot,hub,legacyBees=null,pythonExecutable=null) {
        this.dataRoot=dataRoot;
        this.hub=hub;
        this.legacyBees=legacyBees;
        this.pythonExecutable=pythonExecutable;
        this.sessions=new Map();
    }
    start(value) {
        if(!value||typeof value!=='object'||Array.isArray(value)||
            Object.keys(value).some(key=>![
                'project_id','job_ids','metrics','all_recorded_metrics',
                'include_replays'].includes(key))||
            !PROJECT.test(String(value.project_id||''))||
            !Array.isArray(value.job_ids)||value.job_ids.length<1||
            value.job_ids.length>1000||
            value.job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.job_ids).size!==value.job_ids.length||
            !Array.isArray(value.metrics)||value.metrics.length>256||
            value.metrics.some(m=>!METRIC.test(String(m)))||
            new Set(value.metrics).size!==value.metrics.length||
            (value.all_recorded_metrics!==undefined&&
                typeof value.all_recorded_metrics!=='boolean')||
            (value.all_recorded_metrics===true&&value.metrics.length>0)||
            (value.include_replays!==undefined&&typeof value.include_replays!=='boolean'))
            throw Error('Choose distinct managed jobs and up to 256 metric names.');
        if([...this.sessions.values()].some(item=>item.status==='preparing'))
            throw Error('Another complete job export is already in progress.');
        const project=value.project_id;
        const legacy=project==='bees'&&this.legacyBees;
        const configured=legacy?null:loadRoots(this.dataRoot);
        const configuredRoot=legacy?this.dataRoot:
            Object.hasOwn(configured.roots,project)
                ?configured.roots[project]:undefined;
        const approvedRoot=trustedRoot(configuredRoot);
        // The Hub's received observation archive is useful even if the
        // project's training artifacts live only on a remote machine.
        // Never silently substitute for an existing but unsafe local path.
        const missingLocalRoot=!legacy&&!approvedRoot&&(
            configuredRoot===undefined||
            typeof configuredRoot==='string'&&!fs.existsSync(configuredRoot));
        const root=approvedRoot||(
            missingLocalRoot?trustedRoot(this.dataRoot):null);
        if(!root)
            throw Error('The approved local artifact directory is unsafe or unavailable. Remote-only exports require a trusted Hub data root.');
        const python=typeof this.pythonExecutable==='function'
            ?this.pythonExecutable():this.pythonExecutable||
                (process.platform==='win32'?'python':'python3');
        if(typeof python!=='string'||!python.trim())
            throw Error('A trusted Python interpreter is required for ZIP export.');
        const snapshot=legacy?{jobs:this.legacyBees.jobs()}:this.hub.snapshot();
        const available=new Map(snapshot.jobs.filter(job=>job.project_id===project)
            .map(job=>[job.id,job]));
        const jobs=value.job_ids.map(id=>{
            const job=available.get(id);
            if(!job)throw Error('Selected job is not managed by this project.');
            return redactMetadata(Object.fromEntries(
                JOB_FIELDS.filter(key=>Object.hasOwn(job,key))
                    .map(key=>[key,job[key]])));
        });
        const progress=legacy?this.legacyBees.progress():
            this.hub.progressComparison(project);
        progress.samples=progress.samples.filter(sample=>value.job_ids.includes(sample.job_id));
        // Each mounted root is keyed by an already assigned machine and a
        // trusted project ID. Job IDs are UUIDs from the Hub registry, never
        // arbitrary caller path components.
        const mounted=legacy?[]:jobs.flatMap(job=>{
            const machine=Object.hasOwn(configured.machineRoots,
                job.assigned_machine_id)
                ?configured.machineRoots[job.assigned_machine_id]:null;
            const root=machine&&Object.hasOwn(machine,project)
                ?machine[project]:null;
            if(!root)return [];
            // Other machines may be offline or have a temporarily unsafe
            // mount. Never let their paths block an unrelated selected job,
            // but fail closed if this selected job's mount is redirected.
            if(fs.existsSync(root)&&!trustedRoot(root))
                throw Error('Selected machine artifact mount is redirected or unowned.');
            return [{job_id:job.id,label:'mounted-artifacts',
                path:path.join(root,job.id)}];
        });
        // A verified completed upload is an alternate, locally stored
        // source for machine-owned text histories. Do not mistake mere
        // Hub heartbeat observations for project-owned artifacts.
        const mirrored=legacy?[]:jobs.filter(job=>
            !mounted.some(item=>item.job_id===job.id)).flatMap(job=>{
            const folder=path.join(this.dataRoot,'hub-remote-artifacts',job.id);
            if(!fs.existsSync(folder))return [];
            if(!trustedRoot(folder))
                throw Error('Received job artifacts were redirected or unowned.');
            return [{job_id:job.id,label:'agent-artifacts',path:folder}];
        });
        // A mirror is additional evidence, not a reason to suppress an
        // independently available local original job folder. Only substitute
        // its root when the original trainer files are genuinely absent.
        const mirrorFallbackIds=new Set(mirrored.filter(item=>{
            if(!approvedRoot)return true;
            const local=path.join(approvedRoot,item.job_id);
            if(!fs.existsSync(local))return true;
            if(!trustedRoot(local))
                throw Error('Original job artifacts were redirected or unowned.');
            return false;
        }).map(item=>item.job_id));
        // A managed TinyBrains learner's collector is a separate Hub job.
        // Include its coordinator-received observations in the learner export,
        // even when no worker-local log directory is mounted. All IDs are
        // frozen Hub UUIDs, not browser-supplied filesystem paths.
        const linkedCollectorSources=legacy?[]:jobs.flatMap(selected=>{
            const learner=snapshot.jobs.find(job=>
                job.id===selected.id&&job.project_id==='tinybrains'&&
                job.distributed_rollout_managed===true);
            if(!learner)return [];
            const children=snapshot.jobs.filter(job=>
                job.project_id==='tinybrains_rollout'&&UUID.test(job.id)&&
                job.frozen_parameters?.learner_job_id===learner.id);
            if(children.length>8)
                throw Error('Distributed learner has more than eight collector jobs.');
            const archive=path.join(this.dataRoot,'hub-observations-v1');
            return children.flatMap(child=>{
                const labelId=child.id.replace(/-/g,'');
                const source=path.join(archive,child.id+'.jsonl');
                // A child has its own project ID and physical owner. Its
                // original log files may be available through a separately
                // approved machine mount, never through the learner's root.
                const machine=Object.hasOwn(configured.machineRoots,
                    child.assigned_machine_id)
                    ?configured.machineRoots[child.assigned_machine_id]:null;
                const root=machine&&Object.hasOwn(machine,'tinybrains_rollout')
                    ?machine.tinybrains_rollout:null;
                if(root&&fs.existsSync(root)&&!trustedRoot(root))
                    throw Error('Selected collector mount is redirected or unowned.');
                const synced=path.join(this.dataRoot,'hub-remote-artifacts',child.id);
                if(fs.existsSync(synced)&&!trustedRoot(synced))
                    throw Error('Received collector artifacts were redirected or unowned.');
                const collectorRoot=root||(fs.existsSync(synced)?synced:null);
                return [
                    {
                        job_id:learner.id,label:'collector_'+labelId+'_src',
                        path:source,
                    },
                    {job_id:learner.id,label:'collector_'+labelId+'_host',
                        path:path.join(archive,child.id+'-resources.jsonl')},
                    ...(collectorRoot?[{
                        job_id:learner.id,label:'collector_'+labelId+'_logs',
                        path:root?path.join(root,child.id):collectorRoot,
                    }]:[]),
                ];
            });
        });
        const dir=fs.mkdtempSync(path.join(this.dataRoot,'job-export-'));
        fs.chmodSync(dir,0o700);
        const spec=path.join(dir,'request.json');
        fs.writeFileSync(spec,JSON.stringify({
            project_id:project,jobs,metrics:value.metrics,
            all_recorded_metrics:value.all_recorded_metrics===true,
            include_replays:value.include_replays===true,
            artifact_root:root,local_artifact_root_missing:missingLocalRoot,
            mounted_artifact_job_ids:[...mounted.map(item=>item.job_id),
                ...mirrorFallbackIds],
            resource_provenance:legacy?null:resourceProvenance(snapshot,jobs),
            progress,
            additional_sources:legacy?this.legacyBees.sources(jobs):
                [...jobs.flatMap(job=>{
                    const base=path.join(this.dataRoot,'hub-observations-v1');
                    const hostFile=path.join(base,job.id+'-resources.jsonl');
                    return [{
                        job_id:job.id,label:'hub-observations',
                        path:path.join(base,job.id+'.jsonl'),
                    },{
                        job_id:job.id,label:'hub-host-observations',
                        path:hostFile,
                    }];
                }),...linkedCollectorSources,...mounted,...mirrored],
        }),{flag:'wx',mode:0o600});
        const id=crypto.randomUUID();
        const session={id,dir,project,status:'preparing',parts:[],error:null};
        this.sessions.set(id,session);
        const script=path.join(__dirname,'experiment_hub_job_export.py');
        const child=spawn(python,
            [script,'--spec',spec,'--output',path.join(dir,project+'-analysis.zip')],
            {shell:false,windowsHide:true,stdio:['ignore','pipe','pipe']});
        let stdout='',stderr='',done=false;
        function cleanupLater() {
            const cleanup=setTimeout(()=>{
                this.sessions.delete(id);
                fs.rm(dir,{recursive:true,force:true},()=>{});
            },KEEP_MS);
            cleanup.unref?.();
        }
        const fail=error=>{
            if(done)return;
            done=true;session.status='failed';
            session.error=String(error).slice(0,500);
            child.kill();
            cleanupLater.call(this);
        };
        child.stdout.on('data',chunk=>{
            stdout+=chunk.toString('utf8');
            if(stdout.length>65536)fail('Exporter output exceeded its limit.');
        });
        child.stderr.on('data',chunk=>{
            stderr=(stderr+chunk.toString('utf8')).slice(-3000);
        });
        child.on('error',fail);
        child.on('close',code=>{
            if(done)return;
            if(code!==0)return fail(stderr||'Exporter exited with code '+code);
            try {
                const report=JSON.parse(stdout);
                if(!Array.isArray(report.parts)||!report.parts.length||
                    report.parts.some(part=>typeof part.name!=='string'||
                        !/^[-a-z0-9_]+-analysis-part-[0-9]{4}\.zip$/.test(part.name)||
                        !Number.isSafeInteger(part.bytes)||part.bytes>500000000||
                        fs.statSync(path.join(dir,part.name)).size!==part.bytes))
                    throw Error('Export part validation failed.');
                session.parts=report.parts;
                session.missing_artifact_job_ids=report.missing_artifact_job_ids||[];
                session.missing_sources=report.missing_sources||[];
                session.missing_source_count=report.missing_source_count||0;
                session.files_extended_during_copy_count=report.files_extended_during_copy_count||0;
                session.status='ready';
                done=true;
                cleanupLater.call(this);
            }catch(error){fail(error);}
        });
        // No training processes are managed or modified by the exporter.
        return {id,status:session.status};
    }
    status(id) {
        if(!UUID.test(id)||!this.sessions.has(id))throw Error('Unknown export session.');
        const session=this.sessions.get(id);
        return {
            id,project_id:session.project,status:session.status,
            parts:session.parts,error:session.error,
            missing_artifact_job_ids:session.missing_artifact_job_ids||[],
            missing_sources:session.missing_sources||[],
            missing_source_count:session.missing_source_count||0,
            files_extended_during_copy_count:session.files_extended_during_copy_count||0,
        };
    }
    download(id,part,res) {
        const session=this.sessions.get(id);
        if(!UUID.test(id)||!session||session.status!=='ready'||
            !Number.isSafeInteger(part)||part<1||part>session.parts.length)
            throw Error('Export part unavailable.');
        const record=session.parts[part-1];
        const target=path.join(session.dir,record.name);
        const stat=fs.lstatSync(target);
        if(!stat.isFile()||stat.isSymbolicLink()||stat.size!==record.bytes)
            throw Error('Export part changed or disappeared.');
        // Validate and stream the *same* opened inode. A local file swap
        // between lstat and createReadStream must not serve other bytes.
        const fd=fs.openSync(target,fs.constants.O_RDONLY|
            (fs.constants.O_NOFOLLOW||0));
        let stream=null;
        try {
            const opened=fs.fstatSync(fd);
            if(!opened.isFile()||opened.nlink!==1||
                opened.size!==record.bytes||opened.dev!==stat.dev||
                opened.ino!==stat.ino)
                throw Error('Export part changed or disappeared.');
            stream=fs.createReadStream(target,{fd,autoClose:true});
            // Optional downloads must not crash the training coordinator.
            stream.on('error',()=>res.destroy());
            res.on('close',()=>stream.destroy());
            res.writeHead(200,{
                'Content-Type':'application/zip',
                'Content-Length':opened.size,
                'Content-Disposition':'attachment; filename="'+record.name+'"',
                'Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
            });
            stream.pipe(res);
        }catch(error) {
            if(stream)stream.destroy();
            else fs.closeSync(fd);
            throw error;
        }
    }
}
module.exports={JobExporter};
