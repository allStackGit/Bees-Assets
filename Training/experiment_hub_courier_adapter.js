'use strict';

const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {validateProgress}=require('./experiment_hub_progress');
const {fixedCpuParallelism}=require('./experiment_hub_resources');
const {evaluationReader,modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const WINDOWS_ENTRY=[
    'import os, runpy, sys',
    "sys.stdout = open(os.devnull, 'w', encoding='utf-8')",
    "sys.stderr = open(sys.argv.pop(1), 'x', encoding='utf-8', buffering=1)",
    'script = sys.argv.pop(1)',
    'sys.path.insert(0, os.path.dirname(script))',
    "runpy.run_path(script, run_name='__main__')",
].join('; ');

function inside(parent,child) {
    const relative=path.relative(parent,child);
    return !path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep);
}

// Courier versions may publish additional scalar statistics beside the
// mandatory checkpoint fields. Preserve only actually reported measurements;
// a missing score or success-rate is not inferred from population lineage.
function courierMetrics(source) {
    const metrics={};
    const stats=source?.metrics;
    if(!stats||typeof stats!=='object'||Array.isArray(stats))return metrics;
    // The wire contract accepts 256 measurements. Reserve room for the
    // mandatory checkpoint fields; chart downselection happens separately.
    let retained=0;
    for(const [name,value] of Object.entries(stats)) {
        if(retained>=253)break;
        if(['generation','active_training_time','checkpoint_write_time'].includes(name)||
            !/^[a-z][a-z0-9_]{0,39}$/.test(name)||
            !Number.isFinite(value)||Math.abs(value)>1e12)continue;
        const unit=/(?:^|_)(?:seconds|duration|time_s)$/.test(name)
            ?'seconds':name.endsWith('_per_second')
                ?'rate':/(?:^|_)(?:rate|ratio|fraction)$/.test(name)&&
                value>=0&&value<=1?'ratio':'scalar';
        if((unit==='seconds'||unit==='rate')&&value<0)continue;
        metrics[name]={value,unit};
        retained++;
    }
    return metrics;
}

// A Courier selection evaluation is a paired policy-comparison signal,
// not an independent fixed benchmark. Read only completed records no later
// than the published checkpoint; never infer a missing survival score.
function latestSelectionMetrics(root,checkpointRound) {
    const file=path.join(root,'events.jsonl');
    let fd;
    try {
        if(fs.realpathSync(file)!==file)throw Error('Courier event path is redirected.');
        fd=fs.openSync(file,'r');
        const stat=fs.fstatSync(fd);
        if(!stat.isFile())throw Error('Invalid Courier event stream.');
        const length=Math.min(stat.size,2*1024*1024);
        if(!length)return {};
        const buffer=Buffer.alloc(length);
        if(fs.readSync(fd,buffer,0,length,stat.size-length)!==length)
            throw Error('Courier event stream changed during read.');
        const lines=buffer.toString('utf8').split('\n');
        lines.pop(); // A final record may still be in flight.
        if(length<stat.size)lines.shift(); // Discard the truncated first record.
        for(let i=lines.length-1;i>=0;i--) {
            if(!lines[i].includes('"selection_evaluation"'))continue;
            let row;
            try {row=JSON.parse(lines[i]);}
            catch {continue;}
            if(row.event!=='selection_evaluation'||
                !Number.isSafeInteger(row.round)||row.round<0||
                row.round>checkpointRound)continue;
            const metrics={
                selection_evaluation_round:{value:row.round,unit:'count'},
            };
            for(const [key,value] of [
                ['selection_candidate_survival_score',row.candidate_score],
                ['selection_incumbent_survival_score',row.incumbent_score],
            ])if(typeof value==='number'&&Number.isFinite(value)&&
                Math.abs(value)<=1e12)metrics[key]={value,unit:'scalar'};
            if(typeof row.champion_promoted==='boolean')
                metrics.selection_champion_promoted={
                    value:row.champion_promoted?1:0,unit:'count',
                };
            return metrics;
        }
        return {};
    }catch(error) {
        if(error.code==='ENOENT'&&fd===undefined)return {};
        throw error;
    }finally {
        if(fd!==undefined)fs.closeSync(fd);
    }
}

// The Courier trainer already publishes real gameplay outcomes in its
// checkpoint-bound status.json. Keep this read paired with the same committed
// checkpoint round; older reports remain explicitly labeled by report round.
// Selection lives rotate seeds and are NOT a fixed benchmark.
function recordedCourierGameplay(root,checkpointRound) {
    const file=path.join(root,'status.json');
    let fd;
    try {
        if(fs.realpathSync(file)!==file)
            throw Error('Courier gameplay status path is redirected.');
        fd=fs.openSync(file,'r');
        const stat=fs.fstatSync(fd);
        if(!stat.isFile()||stat.size<1||stat.size>4*1024*1024)
            throw Error('Courier gameplay status exceeds its bounded read.');
        const buffer=Buffer.alloc(stat.size);
        if(fs.readSync(fd,buffer,0,buffer.length,0)!==stat.size)
            throw Error('Courier gameplay status changed during read.');
        const saved=JSON.parse(buffer.toString('utf8'));
        if(saved.round!==checkpointRound)return {};
        const metrics={};
        const add=(source,fields)=>{
            if(!source||typeof source!=='object'||Array.isArray(source))return;
            for(const [key,name,unit] of fields) {
                const value=source[key];
                if(typeof value!=='number'||!Number.isFinite(value)||
                    Math.abs(value)>1e12||
                    unit==='ratio'&&(value<0||value>1)||
                    unit==='count'&&(!Number.isSafeInteger(value)||value<0))
                    continue;
                metrics[name]={value,unit};
            }
        };
        add(saved.training_lifespan,[
            ['recent_mean_completed_lifespan','training_recent_mean_completed_lifespan','scalar'],
            ['recent_deaths','training_recent_deaths','count'],
            ['mean_active_age','training_mean_active_age','scalar'],
            ['oldest_active_age','training_oldest_active_age','scalar'],
            ['active_couriers','training_active_couriers','count'],
            ['mean_active_colony_age','training_mean_active_colony_age','scalar'],
            ['active_colonies','training_active_colonies','count'],
        ]);
        // The policy that won the just-completed selection comparison has
        // full gameplay metrics in the lineage's qualifying evaluation. It
        // must match this round's scored candidate, never a previous result.
        if(saved.selection?.qualified===true&&
            typeof saved.selection.candidate_score==='number'&&
            Array.isArray(saved.lineage)) {
            const qualified=saved.lineage.map(row=>row?.qualification_metrics)
                .filter(row=>row&&typeof row==='object'&&
                    Number.isFinite(row.mean_survival_score)&&
                    Number.isFinite(row.survival_rate)&&
                    Number.isFinite(row.mean_deliveries))
                .sort((a,b)=>b.mean_survival_score-a.mean_survival_score||
                    b.survival_rate-a.survival_rate||
                    b.mean_deliveries-a.mean_deliveries);
            const candidate=qualified[0];
            if(candidate&&
                candidate.mean_survival_score===saved.selection.candidate_score)
                add(candidate,[
                    ['survival_rate','selection_candidate_survival_rate','ratio'],
                    ['mean_lifespan','selection_candidate_mean_lifespan','scalar'],
                    ['mean_deliveries','selection_candidate_mean_deliveries','scalar'],
                    ['pickup_rate','selection_candidate_pickup_rate','ratio'],
                    ['mean_steps_per_delivery','selection_candidate_mean_steps_per_delivery','scalar'],
                    ['gross_energy_per_step','selection_candidate_gross_energy_per_step','scalar'],
                    ['mean_feedings','selection_candidate_mean_feedings','scalar'],
                    ['mean_births','selection_candidate_mean_births','scalar'],
                ]);
        }
        // Optional fixed report benchmarks are disabled by default. When
        // recorded, do not silently represent an older report as fresh.
        if(Number.isSafeInteger(saved.benchmark_round)&&
            saved.benchmark_round>=0&&saved.benchmark_round<=checkpointRound&&
            saved.benchmark) {
            metrics.benchmark_round={value:saved.benchmark_round,unit:'count'};
            add(saved.benchmark,[
                ['survival_rate','benchmark_survival_rate','ratio'],
                ['mean_lifespan','benchmark_mean_lifespan','scalar'],
                ['mean_deliveries','benchmark_mean_deliveries','scalar'],
                ['pickup_rate','benchmark_pickup_rate','ratio'],
                ['mean_steps_per_delivery','benchmark_mean_steps_per_delivery','scalar'],
                ['gross_energy_per_step','benchmark_gross_energy_per_step','scalar'],
                ['mean_feedings','benchmark_mean_feedings','scalar'],
                ['mean_births','benchmark_mean_births','scalar'],
            ]);
        }
        return metrics;
    }catch(error) {
        if(error.code==='ENOENT'&&fd===undefined)return {};
        throw error;
    }finally {
        if(fd!==undefined)fs.closeSync(fd);
    }
}

function liveWindowsProgress(root) {
    // Reading the durable checkpoint can block Python's replacement on Windows.
    // The append-only event is emitted after all checkpoint files are published.
    const file=path.join(root,'events.jsonl');
    let fd;
    try {
        if(fs.realpathSync(file)!==file)throw Error('Courier event path is redirected.');
        fd=fs.openSync(file,'r');
        const stat=fs.fstatSync(fd);
        if(!stat.isFile())throw Error('Invalid Courier event stream.');
        const size=Math.min(stat.size,64*1024);
        const buffer=Buffer.alloc(size);
        const length=fs.readSync(fd,buffer,0,size,stat.size-size);
        const lines=buffer.toString('utf8',0,length).split('\n');
        lines.pop(); // The final line may still be in flight.
        if(size<stat.size)lines.shift();
        for(let i=lines.length-1;i>=0;i--) {
            if(!lines[i].includes('"checkpoint_saved"'))continue;
            const event=JSON.parse(lines[i]);
            if(event.event==='checkpoint_saved')return {
                steps_completed:event.round,
                source_modified_at:new Date(event.utc).toISOString(),
                metrics:{
                    checkpoint_write_time:{value:event.seconds,unit:'seconds'},
                    ...courierMetrics(event),
                },
            };
        }
        return null;
    }catch(error) {
        if(error.code==='ENOENT'&&fd===undefined)return null;
        throw error;
    }finally {
        if(fd!==undefined)fs.closeSync(fd);
    }
}

function createCourierAdapter({projectRoot,pythonPath,artifactRoot,configPath,evaluationRoot,evaluationSuites,snapshotRoot,
    cpuParallelism='profile'}) {
    if(!['profile','allocation'].includes(cpuParallelism))
        throw Error('Courier CPU concurrency must be explicitly profile-based or follow allocated CPU cores.');
    for(const target of [projectRoot,pythonPath,artifactRoot,configPath]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('Courier requires absolute local project, Python, artifact and configuration paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    // Python locates pyvenv.cfg from the invoked executable path. Resolving
    // bin/python to /usr/bin/python discards the approved venv site-packages.
    // Keep the trusted configured path; statSync below still follows and validates it.
    const executable=pythonPath;
    const script=path.join(project,'courier.py');
    if(!fs.statSync(artifacts).isDirectory()||!fs.statSync(executable).isFile()||
        !fs.statSync(script).isFile()||inside(project,artifacts)||inside(artifacts,project))
        throw Error('Courier needs an installed trainer and a dedicated artifact directory outside the project.');
    const config=fs.readFileSync(configPath);
    const parsed=JSON.parse(config.toString('utf8'));
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed))
        throw Error('Courier environment configuration must be a JSON object.');
    const configSha256=crypto.createHash('sha256').update(config).digest('hex');
    const windows=process.platform==='win32';
    const evaluator=evaluationReader('courier',evaluationRoot,evaluationSuites,[project,artifacts]);
    let snapshots=null;
    if(snapshotRoot!=null) {
        if(!evaluator.readEvaluation||typeof snapshotRoot!=='string'||!path.isAbsolute(snapshotRoot))
            throw Error('Courier snapshots require approved evaluation suites and an absolute snapshot directory.');
        snapshots=fs.realpathSync(snapshotRoot);
        if(!fs.statSync(snapshots).isDirectory()||[project,artifacts,fs.realpathSync(evaluationRoot)]
            .some(root=>inside(root,snapshots)||inside(snapshots,root)))
            throw Error('Courier snapshot directory must be separate from source, training and receipts.');
    }

    function jobRoot(context) {
        if(context.project_id!=='courier'||!UUID.test(context.job_id)||
            !UUID.test(context.artifact_lineage_root_id)||
            (context.continuation_of_job_id
                ?!UUID.test(context.continuation_of_job_id)||
                    context.continuation_of_job_id===context.job_id||
                    context.artifact_lineage_root_id===context.job_id
                :context.artifact_lineage_root_id!==context.job_id)||
            context.gpu_pci_address)
            throw Error('Invalid Courier CPU job or checkpoint lineage.');
        return path.join(artifacts,context.job_id);
    }

    const within=(value,min,max)=>Number.isSafeInteger(value)&&value>=min&&value<=max;
    function completedCheckpoint(source,maxRounds) {
        const localFile=name=>{
            const file=path.join(source,name);
            if(fs.realpathSync(file)!==file)throw Error('Courier checkpoint file is redirected: '+name);
            const stat=fs.statSync(file);
            if(!stat.isFile()||stat.size<1)
                throw Error('Courier checkpoint file is missing or empty: '+name);
            return file;
        };
        const summaryFile=localFile('checkpoint.json');
        if(fs.statSync(summaryFile).size>256*1024)
            throw Error('Courier checkpoint summary exceeds 256 KiB.');
        const checkpoint=JSON.parse(fs.readFileSync(summaryFile,'utf8'));
        if(!fs.readFileSync(localFile('hub-environment.json')).equals(config)||
            !within(checkpoint.round,1,Number.MAX_SAFE_INTEGER-maxRounds)||
            !Array.isArray(checkpoint.lineage)||!within(checkpoint.lineage.length,1,64))
            throw Error('Courier checkpoint environment or population is invalid.');
        const names=new Set(fs.readdirSync(source));
        const required=['checkpoint.json',...checkpoint.lineage.map((_,i)=>'lineage-'+i+'.zip')];
        const optional=['champion.zip','milestone.json','milestone-policy.zip',
            ...checkpoint.lineage.map((_,i)=>'lineage-'+i+'-traits.json')];
        return {checkpoint,files:required.concat(optional.filter(name=>names.has(name))).map(localFile)};
    }

    return {
        executable,cwd:project,stop_policy:windows?'terminate-group':'interrupt-main',
        // Private machine-local export root, never an operator HTTP path.
        artifact_export_dir(jobId) {
            if(!UUID.test(String(jobId||'')))throw Error('Invalid Courier job identity.');
            return path.join(artifacts,jobId);
        },
        checkpoint_ready(context) {
            if(context.frozen_parameters?.config_sha256!==configSha256)
                throw Error('Courier checkpoint environment pin changed.');
            completedCheckpoint(jobRoot(context),10000000);
            return true;
        },
        model_checkpoint_sha256(context) {
            const saved=completedCheckpoint(jobRoot(context),10000000);
            const model=saved.files.find(file=>path.basename(file)==='lineage-0.zip');
            if(!model)throw Error('Courier evaluated policy is not available for promotion.');
            return modelFileSha256(model);
        },
        checkpoint_stop_file:context=>path.join(jobRoot(context),'hub-stop.request'),
        ...evaluator,
        args(parameters,context) {
            const {config_sha256,seed,workers,max_rounds,checkpoint_rounds}=parameters;
            if(Object.keys(parameters).some(key=>
                !['config_sha256','seed','workers','max_rounds','checkpoint_rounds'].includes(key))||
                config_sha256!==configSha256||!within(seed,0,4294967295)||
                !(workers===undefined&&cpuParallelism==='allocation')&&
                    !within(workers,1,64)||
                !within(max_rounds,0,10000000)||
                !within(checkpoint_rounds,1,10000000))
                throw Error('Courier requires the approved configuration digest and valid seed, worker and round counts.');
            // The approved adapter may treat an omitted worker count as the
            // immutable CPU lease size. Explicit worker counts retain their
            // previous semantics for existing frozen configurations.
            const chosenWorkers=workers===undefined
                ?fixedCpuParallelism(context.cpu_quota,64):workers;
            const out=jobRoot(context);
            const publish=Boolean(snapshots&&context.evaluation_suite_id);
            if(publish&&(!UUID.test(String(context.dispatch_id||''))||
                !evaluator.evaluation_suites.includes(context.evaluation_suite_id)))
                throw Error('Courier snapshot publishing requires a matching dispatch and suite.');
            const predecessor=context.continuation_of_job_id;
            let limit=max_rounds, files=[];
            if(predecessor) {
                const source=path.join(artifacts,predecessor);
                const saved=completedCheckpoint(source,max_rounds);
                files=saved.files;
                if(context.expected_parent_model_sha256&&
                    modelFileSha256(saved.files.find(file=>path.basename(file)==='lineage-0.zip'))!==
                        context.expected_parent_model_sha256)
                    throw Error('Courier promoted checkpoint no longer matches its evaluated model.');
                if(max_rounds!==0)limit=saved.checkpoint.round+max_rounds;
            }
            fs.mkdirSync(out,{mode:0o700});
            for(const file of files)
                fs.copyFileSync(file,path.join(out,path.basename(file)),fs.constants.COPYFILE_EXCL);
            const configFile=path.join(out,'hub-environment.json');
            fs.writeFileSync(configFile,config,{flag:'wx',mode:0o600});
            // The wrapper provides the same checkpoint-boundary Stop request
            // on both platforms; systemd remains the sole Linux service owner.
            const launch=[path.join(__dirname,'experiment_hub_courier_train.py'),
                script,publish?snapshots:'-',publish?context.dispatch_id:'-',
                path.join(out,'hub-stop.request')];
            const entry=windows?['-c',WINDOWS_ENTRY,path.join(out,'trainer.stderr.txt'),...launch]:launch;
            return ['-B',...entry,'--config',configFile,'--run-dir',out,'--port','0',
                '--seed',String(seed),'--workers',String(chosenWorkers),
                '--max-rounds',String(limit),'--checkpoint-rounds',String(checkpoint_rounds),
                ...(predecessor?['--resume']:[])];
        },
        readProgress(context) {
            const root=jobRoot(context);
            const identity={schema_version:1,source:'hub-metrics-v1',
                experiment_id:context.job_id,segment_id:'training'};
            if(windows&&['running','stop_requested'].includes(context.state)) {
                const progress=liveWindowsProgress(root);
                return progress&&validateProgress({...identity,...progress,
                    metrics:{...progress.metrics,
                        ...latestSelectionMetrics(root,progress.steps_completed),
                        ...recordedCourierGameplay(root,progress.steps_completed)},
                },context.project_id);
            }
            const checkpoint=path.join(root,'checkpoint.json');
            let fd;
            try {
                if(fs.realpathSync(checkpoint)!==checkpoint)
                    throw Error('Courier checkpoint path is redirected.');
                fd=fs.openSync(checkpoint,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||stat.size<1||stat.size>256*1024)
                    throw Error('Invalid Courier checkpoint summary size.');
                const buffer=Buffer.alloc(stat.size);
                if(fs.readSync(fd,buffer,0,buffer.length,0)!==stat.size)
                    throw Error('Courier checkpoint summary changed while reading.');
                const summary=JSON.parse(buffer.toString('utf8'));
                return validateProgress({
                    ...identity,source_modified_at:stat.mtime.toISOString(),
                    steps_completed:summary.round,
                    metrics:{
                        generation:{value:summary.generation,unit:'count'},
                        active_training_time:{value:summary.active_seconds,unit:'seconds'},
                        ...courierMetrics(summary),
                        ...latestSelectionMetrics(root,summary.round),
                        ...recordedCourierGameplay(root,summary.round),
                    },
                },context.project_id);
            }catch(error) {
                if(error.code==='ENOENT'&&fd===undefined)return null;
                throw error;
            }finally {
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}

module.exports={createCourierAdapter};
