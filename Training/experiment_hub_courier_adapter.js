'use strict';

const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {validateProgress}=require('./experiment_hub_progress');
const {fixedCpuParallelism,approvedEndpointSubset}=require('./experiment_hub_resources');
const {evaluationReader,modelFileSha256}=require('./experiment_hub_evaluation');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA256=/^[a-f0-9]{64}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const NVIDIA_UUID=/^GPU-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const ROCM_UUID=/^GPU-(?:[0-9a-f]{16}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;
const COURIER_COUNT_FIELDS=new Set([
    'training_profile_round','training_lineage_workers','training_collector_workers',
    'training_collector_probe_count','training_collector_probe_moved_maps',
    'training_collector_start_parallel',
    'training_collector_probe_layout_changed',
    'training_collector_resume_restored',
    'training_resume_map_moves','training_resume_proof_ready',
    'training_resume_proof_rounds',
    'training_ingestion_shards','training_ingestion_peak_parallel_copies',
    'training_rollout_compressed_collectors',
    'training_shared_last_reply_worker','training_shared_last_copy_worker',
    'training_shared_last_wire_worker',
    'training_slowest_worker','training_last_reply_worker',
    'training_simulation_slowest_worker',
    'training_simulated_steps','training_useful_steps','training_collected_useful_steps',
    'training_total_useful_steps','training_rejected_workers',
    'training_concurrency_limit','training_concurrency_next_limit',
    'training_environment_version','training_sim_early_dispatches',
    'training_sim_prestaged_actions','training_sim_queue_peak_shards',
    'training_sim_multiplexed_shards','training_sim_child_os_threads',
    'training_sim_topology_trial_lineages','training_sim_topology_accepted',
    'training_sim_migration_workers','training_collector_sim_trial_workers',
    'training_sim_process_migrations','training_sim_process_migration_denials',
    'training_sim_topology_rejected','training_sim_topology_unavailable',
    'training_sim_topology_unverified','training_sim_topology_migrations',
    'training_tuner_adjustments','training_tuner_accepted',
    'training_tuner_settled',
    'training_gpu_optimizer_lineages','training_gpu_optimizer_next_lineages',
    'training_gpu_optimizer_tuner_adjustments',
    'training_gpu_optimizer_tuner_accepted',
    'training_gpu_optimizer_tuner_settled',
    'training_gpu_placement_trial','training_gpu_placement_settled',
    'training_gpu_placement_adjustments','training_gpu_placement_accepted',
    'training_gpu_placement_rejected','training_gpu_placement_ram_denials',
    'training_gpu_placement_changed_next','training_gpu_placement_devices',
    'training_gpu_placement_moved_lineage',
    'training_gpu_metric_readbacks','training_gpu_metric_rows',
    'training_gpu_guard_readbacks',
    'training_gpu_snapshot_reused_lineages','training_gpu_snapshot_owner',
    'training_remote_progress_seen','training_remote_progress_sent',
    'training_remote_progress_skipped','training_remote_progress_owner',
    'training_gpu_h2d_async_batches',
    'training_gpu_h2d_packed_batches',
    'training_gpu_copy_stream_batches',
    'training_gpu_copy_eligible_batches',
    'training_gpu_copy_timing_samples',
    'training_gpu_copy_stream_next','training_gpu_copy_stream_settled',
    'training_gpu_lstm_boundary_batches',
    'training_gpu_vector_pad_batches','training_cpu_vector_pad_batches',
    'training_cpu_lstm_boundary_batches','training_cpu_rollout_lstm_calls',
    'training_cpu_optimizer_threads_max','training_cpu_optimizer_threads_next',
    'training_cpu_optimizer_threads_settled','training_remote_cpu_optimizer_trials',
    'training_gpu_rollout_lineages','training_split_gpu_rollout_lineages',
    'training_gpu_forward_stream_workers',
    'training_gpu_input_slots_reused','training_gpu_state_slots_reused',
    'training_gpu_output_staged_batches','training_gpu_output_slots_reused',
    'training_gpu_input_device_copy_batches',
    'training_gpu_input_device_reused_slots',
    'training_gpu_input_device_new_slots',
    'training_gpu_output_slots_allocated',
    'training_gpu_state_pack_reuse_batches',
    'training_gpu_policy_moves_skipped',
    'training_shared_gpu_forward_workers',
    'training_shared_gpu_input_reused','training_shared_gpu_state_reused',
    'training_shared_gpu_output_staged','training_shared_gpu_output_reused',
    'training_shared_gpu_input_device_copies',
    'training_shared_gpu_input_device_reused',
    'training_shared_gpu_state_pack_reused',
    'training_shared_gpu_moves_skipped',
    'training_local_gpu_rollout_devices','training_gpu_rollout_next_devices',
    'training_local_gpu_rollout_lineages','training_gpu_rollout_next_lineages',
    'training_gpu_rollout_trial',
    'training_gpu_rollout_spare_candidates',
    'training_gpu_rollout_batches','training_gpu_rollout_states',
    'training_gpu_rollout_lstm_steps','training_gpu_rollout_policy_reused',
    'training_gpu_policy_retained','training_gpu_policy_reused',
    'training_gpu_rollout_state_copy_batches',
    'training_gpu_rollout_input_packs',
    'training_remote_gpu_rollout_trials','training_remote_gpu_rollout_tuned',
    'training_gpu_prefetched_batches',
    'training_gpu_prefetch_active_lineages',
    'training_gpu_prefetch_validated_batches',
    'training_gpu_prefetch_skipped_batches',
    'training_gpu_devices_assigned','training_gpu_devices_active']);
const WINDOWS_ENTRY=[
    'import os, runpy, sys',
    "sys.stdout = open(os.devnull, 'w', encoding='utf-8')",
    "sys.stderr = open(sys.argv.pop(1), 'x', encoding='utf-8', buffering=1)",
    'script = sys.argv.pop(1)',
    'sys.path.insert(0, os.path.dirname(script))',
    "runpy.run_path(script, run_name='__main__')",
].join('; ');

function approvedCourierLineages(approved,selected) {
    if(selected===undefined)return approved;
    if(!Array.isArray(selected)||selected.some(item=>!item||
        typeof item!=='object'||Array.isArray(item)||
        Object.keys(item).length!==2||typeof item.endpoint!=='string'||
        !Number.isSafeInteger(item.maxShards)))return null;
    const endpoints=approvedEndpointSubset(approved?.map(item=>item.endpoint),
        selected.map(item=>item.endpoint));
    if(!endpoints)return null;
    const plan=approved.filter(item=>endpoints.includes(item.endpoint));
    return plan.every((item,index)=>item.maxShards===selected[index].maxShards)?plan:null;
}

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
    // A 64-lineage run can exceed the neutral 512-field/32-KiB report.
    // Preserve the handful of whole-run GPU placement decisions before
    // optional per-lineage details. The append-only Courier event still
    // contains every original measurement for full native history.
    const primary=name=>name.startsWith('training_gpu_placement_')||
        name.startsWith('training_gpu_output_')||
        name.startsWith('training_gpu_input_device_')||
        name.startsWith('training_shared_gpu_input_device_')||
        name.startsWith('training_shared_gpu_output_')||
        name==='training_gpu_state_pack_reuse_batches'||
        name==='training_shared_gpu_state_pack_reused'||
        name.startsWith('training_collector_probe_')||
        /^worker_[0-9]{1,2}_probe_maps_(?:before|after)$/.test(name)||
        name.startsWith('training_collector_start_')||
        name.startsWith('training_resume_')||
        name.startsWith('training_sim_migration_')||
        name.startsWith('training_sim_process_migration_')||
        name==='training_collector_sim_trial_workers'||
        /^worker_[0-9]{1,2}_sim_(?:trial|next_processes)$/.test(name)||
        /^worker_[0-9]{1,2}_resume_maps_(?:before|after)$/.test(name)||
        name==='training_collector_resume_restored'||
        ['training_info_pack_max_seconds',
            'training_info_pack_work_seconds',
            'training_sim_pack_max_ratio',
            'training_sim_pack_ratio_owner'].includes(name)||
        /^training_(?:gpu_forward_stream_|shared_gpu_forward_|gpu_input_slots_reused$|gpu_state_slots_reused$|gpu_policy_moves_skipped$|shared_gpu_input_reused$|shared_gpu_state_reused$)/.test(name)||
        /^training_gpu_device_[0-7]_next_lineages$/.test(name)||
        /^training_sim_(?:multiplexed_shards|child_os_threads)$/.test(name)||
        /^training_gpu_rollout_input_(?:packs|seconds|owner)$/.test(name)||
        /^training_remote_progress_(?:seen|sent|skipped|bytes|max_seconds|encode_seconds|owner)$/.test(name)||
        /^training_(?:local_gpu_rollout_lineages|gpu_rollout_(?:next_lineages|trial))$/.test(name)||
        /^training_gpu_snapshot_(?:max_seconds|stream_max_seconds|reused_lineages|owner|work_share)$/.test(name);
    const entries=Object.entries(stats).sort(
        ([a],[b])=>Number(primary(b))-Number(primary(a)));
    for(const [name,value] of entries) {
        if(retained>=480)break;
        if(['generation','active_training_time','checkpoint_write_time'].includes(name)||
            !/^[a-z][a-z0-9_]{0,39}$/.test(name)||
            !Number.isFinite(value)||Math.abs(value)>1e12)continue;
        const unit=(COURIER_COUNT_FIELDS.has(name)||
            name.endsWith('_owner')||name.endsWith('_train_seq')||
            /^worker_[0-9]{1,2}_(?:probe|resume)_maps_(?:before|after)$/.test(name)||
            /^worker_[0-9]{1,2}_sim_(?:trial|next_processes)$/.test(name)||
            /^worker_[0-9]{1,2}_(?:sim_early_dispatches|sim_multiplexed_shards|sim_os_threads|gpu_output_batches|gpu_output_reused|gpu_input_device_copies|gpu_snapshot_reused|gpu_prefetch_skipped_batches|gpu_policy_retained|gpu_policy_reused|gpu_device|rollout_gpu_device|rollout_compressed|is_remote)$/.test(name)||
            /^training_gpu_device_[0-7]_(?:lineages|next_lineages)$/.test(name))?'count':
            name.endsWith('_bytes')?'bytes':
            /_probe_map_steps_per_second$/.test(name)?'rate':
            name.endsWith('_mb')?'mb':
            name==='training_gpu_snapshot_work_share'&&value>=0&&value<=1?'ratio':
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
    cpuParallelism='allocation',enableGpu=false,rocrGpuUuids={},remoteLineages=null}) {
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
    const sharedScript=path.join(project,'courier_shared_training.py');
    if(!fs.statSync(artifacts).isDirectory()||!fs.statSync(executable).isFile()||
        !fs.statSync(script).isFile()||inside(project,artifacts)||inside(artifacts,project))
        throw Error('Courier needs an installed trainer and a dedicated artifact directory outside the project.');
    // Old Courier installations do not know --active-learners. The explicit,
    // bounded backend capability manifest allows the same Hub adapter to
    // launch both old and new trainers without interpreting source text.
    const capabilitiesFile=path.join(project,'hub-capabilities.json');
    let sharedPolicy=false,sharedCapacityProbe=false;
    let boundedLearners=false,autoLearners=false,optimizerGpu=false,gpuPciPin=false,gpuMulti=false;
    let gpuSpareRollout=false;
    let persistentWorlds=false,maxSimulationShards=1;
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
             typeof capabilities.courier_gpu_optimizer!=='boolean')||
            (capabilities.courier_gpu_pci_pin!==undefined&&
             typeof capabilities.courier_gpu_pci_pin!=='boolean')||
            (capabilities.courier_gpu_multi_optimizer!==undefined&&
             typeof capabilities.courier_gpu_multi_optimizer!=='boolean')||
            (capabilities.courier_gpu_pci_pin===true&&
             capabilities.courier_gpu_optimizer!==true)||
            (capabilities.courier_gpu_multi_optimizer===true&&
             (capabilities.courier_gpu_optimizer!==true||
              capabilities.courier_gpu_pci_pin!==true))||
            (capabilities.courier_spare_rollout_gpu!==undefined&&
             typeof capabilities.courier_spare_rollout_gpu!=='boolean')||
            (capabilities.courier_spare_rollout_gpu===true&&
             (capabilities.courier_gpu_multi_optimizer!==true||
              capabilities.courier_auto_active_learners!==true))||
            (capabilities.courier_parallel_worlds!==undefined&&
                typeof capabilities.courier_parallel_worlds!=='boolean')||
            (capabilities.courier_episode_safe_process_groups!==undefined&&
                typeof capabilities.courier_episode_safe_process_groups!=='boolean')||
            (capabilities.courier_max_simulation_shards!==undefined&&
                (!Number.isSafeInteger(capabilities.courier_max_simulation_shards)||
                    capabilities.courier_max_simulation_shards<1||
                    capabilities.courier_max_simulation_shards>64))||
            (capabilities.courier_parallel_worlds===true&&
                (capabilities.courier_episode_safe_process_groups!==true||
                    !Number.isSafeInteger(capabilities.courier_max_simulation_shards)))||
            (capabilities.courier_shared_policy!==undefined&&
                typeof capabilities.courier_shared_policy!=='boolean')||
            (capabilities.courier_shared_capacity_probe!==undefined&&
                typeof capabilities.courier_shared_capacity_probe!=='boolean')||
            (capabilities.courier_shared_capacity_probe===true&&
                capabilities.courier_shared_policy!==true)||
            (capabilities.courier_remote_lineage_transport!==undefined&&
                typeof capabilities.courier_remote_lineage_transport!=='boolean'))
            throw Error('Unsupported Courier backend capabilities manifest.');
        sharedPolicy=capabilities.courier_shared_policy===true;
        sharedCapacityProbe=sharedPolicy&&capabilities.courier_shared_capacity_probe===true;
        if(sharedPolicy&&(fs.realpathSync(sharedScript)!==sharedScript||
            !fs.statSync(sharedScript).isFile()))
            throw Error('Courier shared-policy learner entry point is missing.');
        boundedLearners=capabilities.courier_active_learners;
        autoLearners=boundedLearners&&capabilities.courier_auto_active_learners===true;
        optimizerGpu=capabilities.courier_gpu_optimizer===true;
        gpuPciPin=optimizerGpu&&capabilities.courier_gpu_pci_pin===true;
        gpuMulti=gpuPciPin&&capabilities.courier_gpu_multi_optimizer===true;
        gpuSpareRollout=gpuMulti&&capabilities.courier_spare_rollout_gpu===true;
        persistentWorlds=autoLearners&&
            capabilities.courier_parallel_worlds===true&&
            capabilities.courier_episode_safe_process_groups===true;
        maxSimulationShards=persistentWorlds
            ?capabilities.courier_max_simulation_shards:1;
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
    const sharedMaps=parsed.colony===true?4:parsed.competitive===true?32:64;
    const configSha256=crypto.createHash('sha256').update(config).digest('hex');
    const windows=process.platform==='win32';
    // Endpoint order determines the last N immutable PPO lineage slots.
    // Only the machine operator can install these private peer identities;
    // experiment parameters never supply network addresses or secrets.
    let remote=null;
    if(remoteLineages!==null) {
        if(!remoteLineages||typeof remoteLineages!=='object'||
            Array.isArray(remoteLineages)||
            Object.keys(remoteLineages).sort().join(',')!=='endpoints,keyPath'||
            !Array.isArray(remoteLineages.endpoints)||
            !remoteLineages.endpoints.length||remoteLineages.endpoints.length>16)
            throw Error('Courier remote execution requires 1–16 locally pinned lineage endpoints.');
        const keyFile=remoteLineages.keyPath;
        if(typeof keyFile!=='string'||!path.isAbsolute(keyFile)||
            fs.realpathSync(keyFile)!==keyFile)
            throw Error('Courier remote lineage key must be an installed absolute file.');
        const stat=fs.statSync(keyFile);
        if(!stat.isFile()||stat.size<64||stat.size>128||
            (!windows&&(stat.mode&0o077)!==0)||
            !/^[0-9a-fA-F]{64}$/.test(fs.readFileSync(keyFile,'utf8').trim()))
            throw Error('Courier remote lineage requires a private 32-byte hexadecimal key.');
        const endpoints=remoteLineages.endpoints.map(worker=>{
            if(!worker||typeof worker!=='object'||Array.isArray(worker)||
                Object.keys(worker).sort().join(',')!=='endpoint,maxShards'||
                typeof worker.endpoint!=='string'||
                !/^100\.(?:\d{1,3}\.){2}\d{1,3}:\d{1,5}$/.test(worker.endpoint)||
                !Number.isSafeInteger(worker.maxShards)||worker.maxShards<1||
                worker.maxShards>64)
                throw Error('Courier remote worker requires a pinned Tailnet endpoint and 1–64 process ceiling.');
            const [ip,portText]=worker.endpoint.split(':');
            const octets=ip.split('.');
            if(octets.some(v=>String(Number(v))!==v||Number(v)>255)||
                Number(octets[1])<64||Number(octets[1])>127||
                Number(portText)<1024||Number(portText)>65535)
                throw Error('Courier remote endpoint must be a canonical Tailscale IPv4 listener.');
            return Object.freeze({endpoint:worker.endpoint,maxShards:worker.maxShards});
        });
        if(new Set(endpoints.map(item=>item.endpoint)).size!==endpoints.length)
            throw Error('Each Courier lineage requires a distinct remote endpoint.');
        const probe=spawnSync(executable,['-B','-c',
            'import courier_remote_lineage as r; assert hasattr(r, "remote_proxy")'],{
            cwd:project,timeout:15000,encoding:'utf8',windowsHide:true,
        });
        if(probe.status!==0)
            throw Error('Installed Courier learner has no authenticated remote lineage transport.');
        remote={keyFile,endpoints,
            fingerprint:crypto.createHash('sha256').update(fs.readFileSync(keyFile)).digest('hex')};
    }
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
            (context.gpu_pci_addresses!==undefined&&(
                context.gpu_pci_address!==undefined||context.gpu_cuda_uuid!==undefined||
                !enableGpu||windows||!gpuMulti||context.gpu_mode!=='exclusive'||
                !Array.isArray(context.gpu_pci_addresses)||
                ![2,3,4,5,6,7,8].includes(context.gpu_pci_addresses.length)||
                context.gpu_pci_addresses.some(pci=>
                    !PCI.test(String(pci))||!Object.hasOwn(approvedRocr,pci))||
                new Set(context.gpu_pci_addresses).size!==context.gpu_pci_addresses.length)))
            throw Error('Invalid Courier job, physical GPU lease or checkpoint lineage.');
        return path.join(artifacts,context.job_id);
    }

    const within=(value,min,max)=>Number.isSafeInteger(value)&&value>=min&&value<=max;
    function completedCheckpoint(source,maxRounds,shared=false) {
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
        if((checkpoint.training_mode==='shared-policy')!==shared||
            (shared&&(checkpoint.workers!==1||checkpoint.lineage.length!==1||
                checkpoint.lineage[0]!==0||!SHA256.test(String(checkpoint.model_sha256||''))||
                modelFileSha256(localFile('lineage-0.zip'))!==checkpoint.model_sha256)))
            throw Error('Courier checkpoint mode or shared-policy model digest differs.');
        const names=new Set(fs.readdirSync(source));
        const required=['checkpoint.json',...checkpoint.lineage.map((_,i)=>'lineage-'+i+'.zip')];
        const optional=['champion.zip','milestone.json','milestone-policy.zip',
            ...checkpoint.lineage.map((_,i)=>'lineage-'+i+'-traits.json')];
        return {checkpoint,files:required.concat(optional.filter(name=>names.has(name))).map(localFile)};
    }

    return {
        executable,cwd:project,stop_policy:windows?'terminate-group':'interrupt-main',
        ...(remote?{
            courier_remote_lineage_endpoints:remote.endpoints.map(item=>item.endpoint),
            courier_lineage_subset:true,
            courier_shared_policy_maps:sharedPolicy?sharedMaps:0,
            courier_remote_key_fingerprint:remote.fingerprint,
            courier_remote_max_shards:remote.endpoints.map(item=>item.maxShards),
        }:{}),
        ...(enableGpu&&windows?{gpu_execution:'cuda'}:{}),
        ...(enableGpu&&!windows?{
            rocr_gpu_uuids:approvedRocr,
            ...(gpuMulti&&Object.keys(approvedRocr).length>=2
                ?{gpu_multi_execution:'independent-ppo'}:{}),
            gpu_verify_args(_parameters,context) {
                const ids=context?.gpu_pci_addresses??[context?.gpu_pci_address];
                if(!Array.isArray(ids)||ids.length<1||ids.length>8||
                    (ids.length>1&&(!gpuMulti||Object.keys(approvedRocr).length<2))||
                    ids.some(pci=>!PCI.test(String(pci))||
                        (ids.length>1&&!Object.hasOwn(approvedRocr,pci)))||
                    new Set(ids).size!==ids.length)
                    throw Error('Courier GPU qualification requires frozen AMD PCI identities.');
                return ['-B',path.join(__dirname,
                    'experiment_hub_tinybrains_rocm_verify.py'),...ids];
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
            completedCheckpoint(jobRoot(context),10000000,context.frozen_parameters?.shared_policy===true);
            return true;
        },
        model_checkpoint_sha256(context) {
            const saved=completedCheckpoint(jobRoot(context),10000000,context.frozen_parameters?.shared_policy===true);
            const model=saved.files.find(file=>path.basename(file)==='lineage-0.zip');
            if(!model)throw Error('Courier evaluated policy is not available for promotion.');
            return modelFileSha256(model);
        },
        checkpoint_stop_file:context=>path.join(jobRoot(context),'hub-stop.request'),
        ...evaluator,
        args(parameters,context) {
            const {config_sha256,seed,workers,max_rounds,checkpoint_rounds,remote_lineage_plan}=parameters;
            const remotePlan=approvedCourierLineages(remote?.endpoints,remote_lineage_plan);
            const shared=parameters.shared_policy===true;
            const collectorCpus=parameters.shared_policy_cpus;
            if(Object.keys(parameters).some(key=>
                !['config_sha256','seed','workers','max_rounds','checkpoint_rounds',
                    'remote_lineages','remote_lineage_plan','shared_policy','shared_policy_cpus'].includes(key))||
                (parameters.shared_policy!==undefined&&typeof parameters.shared_policy!=='boolean')||
                (!shared&&collectorCpus!==undefined)||
                (shared&&(!sharedPolicy||parameters.remote_lineages!==true||
                    !Array.isArray(remote_lineage_plan)||!Array.isArray(collectorCpus)||
                    !remotePlan||remotePlan.length>sharedMaps||remotePlan.length!==collectorCpus.length||
                    collectorCpus.some(cpu=>!within(cpu,1,256))||
                    (workers!==undefined&&workers!==1)||context.gpu_pci_addresses!==undefined||
                    !within(context.cpu_quota,1,256)||!within(context.ram_quota_mb,128,1000000)))||
                config_sha256!==configSha256||!within(seed,0,4294967295)||
                !(workers===undefined&&(shared||cpuParallelism==='allocation'))&&
                    !within(workers,1,64)||
                !within(max_rounds,0,10000000)||
                !within(checkpoint_rounds,1,10000000)||
                (parameters.remote_lineages!==undefined&&
                    typeof parameters.remote_lineages!=='boolean')||
                (parameters.remote_lineages===true&&!remotePlan)||
                (remote_lineage_plan!==undefined&&(parameters.remote_lineages!==true||!remotePlan)))
                throw Error('Courier requires a pinned environment, valid worker settings and the locally approved remote-lineage mode.');
            // Courier workers own separate PPO policies and population
            // lineages, not parallel simulators for one shared policy.
            // Keep population size independent of CPU lease changes.
            let chosenWorkers=workers===undefined?12:workers;
            const useRemote=parameters.remote_lineages===true;
            if(useRemote&&!shared&&remotePlan.length>=chosenWorkers)
                throw Error('Courier requires at least one local PPO lineage beside its remote workers.');
            const out=jobRoot(context);
            if(useRemote&&crypto.createHash('sha256')
                .update(fs.readFileSync(remote.keyFile)).digest('hex')!==remote.fingerprint)
                throw Error('Courier remote lineage key differs from its approved identity.');
            const publish=Boolean(snapshots&&context.evaluation_suite_id);
            if(publish&&(!UUID.test(String(context.dispatch_id||''))||
                !evaluator.evaluation_suites.includes(context.evaluation_suite_id)))
                throw Error('Courier snapshot publishing requires a matching dispatch and suite.');
            const predecessor=context.continuation_of_job_id;
            let limit=max_rounds, files=[];
            if(predecessor) {
                const source=path.join(artifacts,predecessor);
                const saved=completedCheckpoint(source,max_rounds,shared);
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
            if(!gpuSpareRollout&&context.gpu_pci_addresses&&chosenWorkers<context.gpu_pci_addresses.length)
                throw Error('This Courier installation requires at least one lineage per leased GPU.');
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
            // The Hub owns the CPU/RAM lease and supplies the installed
            // backend's declared shard ceiling. Courier alone derives
            // whole-map capacity from the CPU share and tunes physical
            // processes using completed useful-SPS measurements. It never
            // changes the recurrent PPO observation/batch contract.
            // The wrapper provides the same checkpoint-boundary Stop request
            // on both platforms; systemd remains the sole Linux service owner.
            const launch=[path.join(__dirname,'experiment_hub_courier_train.py'),
                shared?sharedScript:script,publish?snapshots:'-',publish?context.dispatch_id:'-',
                path.join(out,'hub-stop.request')];
            const entry=windows?['-c',WINDOWS_ENTRY,path.join(out,'trainer.stderr.txt'),...launch]:launch;
            if(shared)return ['-B',...entry,'--config',configFile,'--run-dir',out,
                '--seed',String(seed),'--cpu-lease',String(context.cpu_quota),
                '--ram-lease-mb',String(context.ram_quota_mb),
                '--shared-policy-plan',remotePlan.map((item,index)=>
                    item.endpoint+'@'+collectorCpus[index]).join(','),
                '--remote-lineage-key-file',remote.keyFile,'--remote-job-id',context.job_id,
                ...(context.gpu_pci_address!==undefined?[
                    '--optimizer-device','cuda',
                    ...(windows?[]:['--optimizer-gpu-pci',context.gpu_pci_address])]:[]),
                '--max-rounds',String(limit),'--checkpoint-rounds',String(checkpoint_rounds),
                ...(sharedCapacityProbe&&!predecessor?['--probe-collectors']:[]),
                ...(predecessor?['--resume']:[])];
            return ['-B',...entry,'--config',configFile,'--run-dir',out,'--port','0',
                '--seed',String(seed),'--workers',String(chosenWorkers),
                ...(boundedLearners?['--active-learners',String(Math.min(
                    chosenWorkers,fixedCpuParallelism(context.cpu_quota,64)))]:[]),
                ...(auto?['--auto-active-learners']:[]),
                ...(auto&&persistentWorlds?[
                    '--cpu-lease',String(context.cpu_quota),
                    ...(maxSimulationShards>1
                        ?['--simulation-shards',String(maxSimulationShards)]:[]),
                ]:[]),
                ...(auto||context.gpu_pci_address!==undefined||
                    context.gpu_pci_addresses!==undefined
                    ?['--ram-lease-mb',String(context.ram_quota_mb)]:[]),
                ...(context.gpu_pci_address!==undefined||
                    context.gpu_pci_addresses!==undefined
                    ?['--optimizer-device','cuda',
                       ...(context.gpu_pci_addresses
                           ?context.gpu_pci_addresses.flatMap(pci=>
                               ['--optimizer-gpu-pci',pci])
                           :gpuPciPin&&!windows
                               ?['--optimizer-gpu-pci',context.gpu_pci_address]:[])]:[]),
                ...(useRemote?[
                    '--remote-lineage-plan',remotePlan.map(item=>
                        item.endpoint+'@'+item.maxShards).join(','),
                    '--remote-lineage-key-file',remote.keyFile,
                    '--remote-job-id',context.job_id,
                ]:[]),
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

module.exports={createCourierAdapter,approvedCourierLineages};
