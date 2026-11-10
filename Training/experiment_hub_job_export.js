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
const METRIC=/^[a-z][a-z0-9_]{0,39}$/;
const KEEP_MS=30*60*1000;
const JOB_FIELDS=[
    'id','project_id','configuration_name','configuration_sha256',
    'frozen_parameters','metric_selection','resources','status',
    'assigned_machine_id','continuation_of_job_id','artifact_lineage_root_id',
    'checkpoint_model_sha256','checkpoint_ready_verified',
    'checkpoint_stop_verified','created_at','capacity_released_at',
    'evaluation_stop','evaluation_stop_evidence','evaluation_candidates',
    'last_evaluation','final_progress','final_resource_usage',
    'execution_diagnostic','native_bees_run_id',
];
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
    const roots={tinybrains:path.join(dataRoot,'tinybrains-jobs')};
    // Additional projects have separately configured, operator-owned artifact
    // roots. The browser/API can never provide an arbitrary filesystem path.
    const config=path.join(dataRoot,'job-export-roots.json');
    if(fs.existsSync(config)) {
        const stat=fs.lstatSync(config);
        if(!stat.isFile()||stat.isSymbolicLink()||
            (typeof process.getuid==='function'&&stat.uid!==process.getuid())||
            (stat.mode&0o077)!==0)
            throw Error('Project export root configuration must be a private regular file.');
        const entries=JSON.parse(fs.readFileSync(config,'utf8'));
        if(!entries||typeof entries!=='object'||Array.isArray(entries)||
            Object.keys(entries).length>32||
            Object.entries(entries).some(([id,dir])=>!PROJECT.test(id)||
                !trustedRoot(dir)))
            throw Error('Project export roots must be approved absolute local directories.');
        Object.assign(roots,entries);
    }
    return roots;
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
                'project_id','job_ids','metrics','include_replays'].includes(key))||
            !PROJECT.test(String(value.project_id||''))||
            !Array.isArray(value.job_ids)||value.job_ids.length<1||
            value.job_ids.length>1000||
            value.job_ids.some(id=>!UUID.test(String(id)))||
            new Set(value.job_ids).size!==value.job_ids.length||
            !Array.isArray(value.metrics)||value.metrics.length>96||
            value.metrics.some(m=>!METRIC.test(String(m)))||
            new Set(value.metrics).size!==value.metrics.length||
            (value.include_replays!==undefined&&typeof value.include_replays!=='boolean'))
            throw Error('Choose distinct managed jobs and up to 96 metric names.');
        if([...this.sessions.values()].some(item=>item.status==='preparing'))
            throw Error('Another complete job export is already in progress.');
        const project=value.project_id;
        const legacy=project==='bees'&&this.legacyBees;
        const root=trustedRoot(legacy?this.dataRoot:loadRoots(this.dataRoot)[project]);
        if(!root)
            throw Error('No approved local artifact directory for this project; configure job-export-roots.json in the private Hub data directory.');
        const snapshot=legacy?{jobs:this.legacyBees.jobs()}:this.hub.snapshot();
        const available=new Map(snapshot.jobs.filter(job=>job.project_id===project)
            .map(job=>[job.id,job]));
        const jobs=value.job_ids.map(id=>{
            const job=available.get(id);
            if(!job)throw Error('Selected job is not managed by this project.');
            return Object.fromEntries(JOB_FIELDS.filter(key=>Object.hasOwn(job,key))
                .map(key=>[key,job[key]]));
        });
        const progress=legacy?this.legacyBees.progress():
            this.hub.progressComparison(project);
        progress.samples=progress.samples.filter(sample=>value.job_ids.includes(sample.job_id));
        const dir=fs.mkdtempSync(path.join(this.dataRoot,'job-export-'));
        fs.chmodSync(dir,0o700);
        const spec=path.join(dir,'request.json');
        fs.writeFileSync(spec,JSON.stringify({
            project_id:project,jobs,metrics:value.metrics,
            include_replays:value.include_replays===true,
            artifact_root:root,progress,
            ...(legacy?{additional_sources:this.legacyBees.sources(jobs)}:{}),
        }),{flag:'wx',mode:0o600});
        const id=crypto.randomUUID();
        const session={id,dir,project,status:'preparing',parts:[],error:null};
        this.sessions.set(id,session);
        const script=path.join(__dirname,'experiment_hub_job_export.py');
        const python=typeof this.pythonExecutable==='function'
            ?this.pythonExecutable():this.pythonExecutable||
                (process.platform==='win32'?'python':'python3');
        const child=spawn(python,
            [script,'--spec',spec,'--output',path.join(dir,project+'-analysis.zip')],
            {shell:false,windowsHide:true,stdio:['ignore','pipe','pipe']});
        let stdout='',stderr='',done=false;
        function cleanupLater() {
            const cleanup=setTimeout(()=>{
                fs.rm(dir,{recursive:true,force:true},()=>{});
                // The session ID remains visible as an expired entry until
                // this coordinator restarts; it can no longer serve archives.
                session.status='expired';
                session.parts=[];
            },KEEP_MS);
            cleanup.unref?.();
        }
        const fail=error=>{
            if(done)return;
            done=true;session.status='failed';
            session.error=String(error).slice(0,500);
            cleanupLater();
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
                session.status='ready';
                done=true;
                cleanupLater();
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
        res.writeHead(200,{
            'Content-Type':'application/zip',
            'Content-Length':stat.size,
            'Content-Disposition':'attachment; filename="'+record.name+'"',
            'Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
        });
        fs.createReadStream(target).pipe(res);
    }
}
module.exports={JobExporter};
