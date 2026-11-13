'use strict';

const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {fixedCpuParallelism}=require('./experiment_hub_resources');
const {evaluationReader,modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const NVIDIA_UUID=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const COURIER_COUNT_FIELDS=new Set([
    'training_profile_round','training_lineage_workers',
    'training_slowest_worker','training_last_reply_worker',
    'training_simulation_slowest_worker',
    'training_simulated_steps','training_useful_steps',
    'training_total_useful_steps','training_rejected_workers',
    'training_concurrency_limit','training_concurrency_next_limit',
    'training_environment_version',
    'training_tuner_adjustments','training_tuner_accepted',
    'training_tuner_settled',
    'training_gpu_optimizer_lineages','training_gpu_optimizer_next_lineages',
    'training_gpu_optimizer_tuner_adjustments',
    'training_gpu_optimizer_tuner_accepted',
    'training_gpu_optimizer_tuner_settled',
    'training_gpu_metric_readbacks','training_gpu_metric_rows']);
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
    // The shared contract accepts 512 fields and a 32-KiB report. Courier
    // can report 64 independent recurrent policies; keep their actual
    // simulation/observation stages instead of truncating the second half
    // of the population at the old 253-field adapter limit. Reserve space
    // for the envelope, host data and optional derived diagnoses.
    let retained=0;
    for(const [name,value] of Object.entries(stats)) {
        if(retained>=480)break;
        if(['generation','active_training_time','checkpoint_write_time'].includes(name)||
            !/^[a-z][a-z0-9_]{0,39}$/.test(name)||
            !Number.isFinite(value)||Math.abs(value)>1e12)continue;
        const unit=COURIER_COUNT_FIELDS.has(name)?'count':
            name.endsWith('_mb')?'mb':
            /(?:^|_)(?:seconds|duration|time_s)$/.test(name)
                ?'seconds':name.endsWith('_per_second')
                    ?'rate':/(?:^|_)(?:rate|ratio|fraction)$/.test(name)&&
                    value>=0&&value<=1?'ratio':'scalar';
        if(((unit==='seconds'||unit==='rate')&&value<0)||
            (unit==='count'&&(!Number.isSafeInteger(value)||value<0)))continue;
        metrics[name]={value,unit};
        if(JSON.stringify(metrics).length>29*1024) {
            delete metrics[name];
            continue;
        }
        retained++;
    }
    return metrics;
}

function liveCourierProgress(root,allowCheckpointEvent) {
    // The append-only event stream already exists on both operating systems.
    // On Windows, prefer checkpoint events to reading open files that can
    // interfere with atomic replacement. Completed training events are
    // observational and do not authorize model promotion/checkpoint release.
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
            if(!lines[i].includes('"checkpoint_saved"')&&
                !lines[i].includes('"batch_finished"'))continue;
            const event=JSON.parse(lines[i]);
            if(event.event==='batch_finished'&&event.operation==='train'&&
                Number.isSafeInteger(event.completed_round)&&event.completed_round>=0&&
                event.metrics?.training_profile_round===event.completed_round&&
                typeof event.completed_at_utc==='string')return {
                steps_completed:event.completed_round,
                // Immutable event-specific timestamp; file mtime can move
                // when later unrelated diagnostics are appended.
                source_modified_at:new Date(event.completed_at_utc).toISOString(),
                metrics:courierMetrics(event),
            };
            if(allowCheckpointEvent&&event.event==='checkpoint_saved')return {
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
    cpuParallelism='allocation',enableGpu=false,rocrGpuUuids={}}) {
    if(typeof enableGpu!=='boolean'||enableGpu&&
        !['linux','win32'].includes(process.platform))
        throw Error('Courier GPU optimization requires an approved Windows CUDA or Linux ROCm owner.');
    if(!rocrGpuUuids||typeof rocrGpuUuids!=='object'||
        Array.isArray(rocrGpuUuids)||Object.keys(rocrGpuUuids).length>32||
        (!enableGpu||process.platform!=='linux')&&Object.keys(rocrGpuUuids).length||
        Object.entries(rocrGpuUuids).some(([pci,uuid])=>
            !PCI.test(pci)||typeof uuid!=='string'||!ROCM_UUID.test(uuid))||
        new Set(Object.values(rocrGpuUuids)).size!==Object.keys(rocrGpuUuids).length)
        throw Error('Courier GPU selectors must map unique, approved ROCr UUIDs to canonical PCI identities.');
    const approvedRocr=Object.freeze({...rocrGpuUuids});
    if(!['profile','allocation'].includes(cpuParallelism))
        throw Error('Courier CPU concurrency must be explicitly profile-based or follow allocated CPU cores.');
    for(const target of [projectRoot,pythonPath,artifactRoot,configPath]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('Courier requires absolute local project, Python, artifact and configuration paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    const executable=fs.realpathSync(pythonPath);
    const script=path.join(project,'courier.py');
    if(!fs.statSync(artifacts).isDirectory()||!fs.statSync(executable).isFile()||
        !fs.statSync(script).isFile()||inside(project,artifacts)||inside(artifacts,project))
        throw Error('Courier needs an installed trainer and a dedicated artifact directory outside the project.');
    // Old Courier installations do not know --active-learners. The explicit,
    // bounded backend capability manifest allows the same Hub adapter to
    // launch both old and new trainers without interpreting source text.
    const capabilitiesFile=path.join(project,'hub-capabilities.json');
    let boundedLearners=false,autoLearners=false,optimizerGpu=false;
    if(fs.existsSync(capabilitiesFile)) {
        if(fs.realpathSync(capabilitiesFile)!==capabilitiesFile||
            !fs.statSync(capabilitiesFile).isFile()||
            fs.statSync(capabilitiesFile).size>4096)
            throw Error('Invalid Courier backend capabilities manifest.');
        const capabilities=JSON.parse(fs.readFileSync(capabilitiesFile,'utf8'));
        if(!capabilities||typeof capabilities!=='object'||
            Array.isArray(capabilities)||capabilities.schema_version!==1||
            typeof capabilities.courier_active_learners!=='boolean'||
            (capabilities.courier_auto_active_learners!==undefined&&
             typeof capabilities.courier_auto_active_learners!=='boolean')||
            (capabilities.courier_gpu_optimizer!==undefined&&
             typeof capabilities.courier_gpu_optimizer!=='boolean'))
            throw Error('Unsupported Courier backend capabilities manifest.');
        boundedLearners=capabilities.courier_active_learners;
        autoLearners=boundedLearners&&capabilities.courier_auto_active_learners===true;
        optimizerGpu=capabilities.courier_gpu_optimizer===true;
    }
    if(enableGpu) {
        if(!optimizerGpu)
            throw Error('The installed Courier backend does not advertise safe optimizer-only GPU migration.');
        const check=spawnSync(executable,['-B','-c',process.platform==='win32'
            ?'import torch; assert torch.version.cuda and torch.cuda.is_available()'
            :'import torch; assert torch.version.hip and torch.cuda.is_available()'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(check.status!==0)
            throw Error('Courier GPU optimizer requires the locally installed CUDA/ROCm PyTorch wheel, not its CPU-only requirements.');
    }
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
            (context.gpu_pci_address!==undefined&&(
                !enableGpu||!PCI.test(String(context.gpu_pci_address))||
                context.gpu_mode!=='exclusive'||
                (windows&&!NVIDIA_UUID.test(String(context.gpu_cuda_uuid||'')))||
                (!windows&&context.gpu_cuda_uuid!==undefined)))||
            (context.gpu_pci_address===undefined&&context.gpu_cuda_uuid!==undefined)||
            context.gpu_pci_addresses!==undefined)
            throw Error('Invalid Courier job, physical GPU lease or checkpoint lineage.');
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
        ...(enableGpu&&windows?{gpu_execution:'cuda'}:{}),
        ...(enableGpu&&!windows?{
            rocr_gpu_uuids:approvedRocr,
            gpu_verify_args(_parameters,context) {
                if(!PCI.test(String(context?.gpu_pci_address||''))||
                    context.gpu_pci_addresses!==undefined)
                    throw Error('Courier GPU qualification requires one pinned AMD PCI identity.');
                return ['-B',path.join(__dirname,
                    'experiment_hub_tinybrains_rocm_verify.py'),
                    context.gpu_pci_address];
            },
        }:{}),
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
            // Courier workers own separate PPO policies and population
            // lineages, not parallel simulators for one shared policy.
            // Keep population size independent of CPU lease changes.
            let chosenWorkers=workers===undefined?12:workers;
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
                if(workers===undefined) {
                    // A continuation retains its original active population.
                    // Extra CPU resources must not create new PPO lineages.
                    if(!within(saved.checkpoint.workers,1,64)||
                        saved.checkpoint.workers>saved.checkpoint.lineage.length)
                        throw Error('Courier predecessor has an invalid saved population.');
                    chosenWorkers=saved.checkpoint.workers;
                }
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
            // Only trusted, capability-declared backends may tune within
            // the Hub's fixed CPU/RAM lease. Legacy/manual profiles retain
            // their original fixed-policy execution behavior.
            const auto=autoLearners&&cpuParallelism==='allocation';
            if(auto&&(!within(context.ram_quota_mb,128,1000000)))
                throw Error('Courier automatic execution requires a fixed RAM lease.');
            // The wrapper provides the same checkpoint-boundary Stop request
            // on both platforms; systemd remains the sole Linux service owner.
            const launch=[path.join(__dirname,'experiment_hub_courier_train.py'),
                script,publish?snapshots:'-',publish?context.dispatch_id:'-',
                path.join(out,'hub-stop.request')];
            const entry=windows?['-c',WINDOWS_ENTRY,path.join(out,'trainer.stderr.txt'),...launch]:launch;
            return ['-B',...entry,'--config',configFile,'--run-dir',out,'--port','0',
                '--seed',String(seed),'--workers',String(chosenWorkers),
                ...(boundedLearners?['--active-learners',String(Math.min(
                    chosenWorkers,fixedCpuParallelism(context.cpu_quota,64)))]:[]),
                ...(auto?['--auto-active-learners']:[]),
                ...(auto||context.gpu_pci_address!==undefined
                    ?['--ram-lease-mb',String(context.ram_quota_mb)]:[]),
                ...(context.gpu_pci_address!==undefined
                    ?['--optimizer-device','cuda']:[]),
                '--max-rounds',String(limit),'--checkpoint-rounds',String(checkpoint_rounds),
                ...(predecessor?['--resume']:[])];
        },
        readProgress(context) {
            const root=jobRoot(context);
            const identity={schema_version:1,source:'hub-metrics-v1',
                experiment_id:context.job_id,segment_id:'training'};
            if(['running','stop_requested'].includes(context.state)) {
                // Courier emits checkpoint events only after its policy
                // population and checkpoint summary have been published.
                // Read the committed event on both operating systems so
                // ZIP serialization and publication stalls remain visible.
                const progress=liveCourierProgress(root,true);
                if(progress)return validateProgress(
                    {...identity,...progress},context.project_id);
                if(windows)return null;
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
