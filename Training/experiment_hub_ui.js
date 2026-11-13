'use strict';

// Browser-only Hub navigation and draft tools. The existing Bees renderer and
// queue remain separate, including their existing live-run ownership rules.
function trainingFormPayload() {
    const $ = id => document.getElementById(id);
    return {
        name:$('name').value, group:$('group').value,
        max_steps:Number($('steps').value),
        scenario_seed:$('scenario-seed').value.trim()===''?null:Number($('scenario-seed').value),
        unlimited_steps:$('unlimited-steps').checked,
        max_minutes:$('minutes').value.trim()===''?null:Number($('minutes').value),
        stall_minutes:$('stall').value.trim()===''?null:Number($('stall').value),
        env_args:$('args').value.split('\n').map(s=>s.trim()).filter(Boolean),
        stage_name:$('stage-name').value,
        target_timeout_pct:$('stage-target').value.trim()||null,
        stages:[...$('stage-rows').children].map(row=>({
            name:row.querySelector('.stage-name').value,
            steps:Number(row.querySelector('.stage-steps').value),
            target_timeout_pct:row.querySelector('.stage-target').value.trim()||null,
            env_args:row.querySelector('.stage-args').value.split('\n').map(s=>s.trim()).filter(Boolean),
        })),
        evaluation_mode:$('evaluation-mode').value,
        benchmark_target:$('benchmark-target-enabled').checked?{
            min_win_rate_pct:Number($('benchmark-target-wins').value),
            max_timeout_rate_pct:Number($('benchmark-target-timeouts').value),
            min_matches:Number($('benchmark-target-matches').value),
            consecutive_passes:Number($('benchmark-target-passes').value),
        }:null,
        reserve_heldout:$('reserve-heldout').checked,
        metric_selection:[...$('bees-training-metrics').selectedOptions].map(o=>o.value),
        replicates:(typeof editingId === 'undefined' || !editingId) && $('replicate-enabled').checked
            ?Number($('replicate-count').value):1,
        trainer:{
            hyperparameters:Object.fromEntries(
                ['beta','lambd','learning_rate','batch_size','buffer_size','num_epoch']
                    .map(k=>[k,Number($('trainer-'+k).value)])),
            network_settings:{
                num_layers:Number($('trainer-num_layers').value),
                normalize:$('trainer-normalize').value==='true',
            },
            seed:Number($('trainer-seed').value),
        },
    };
}

// Compare exports remain observational, not benchmark or qualification
// evidence. Keep transformations independent of browser state.
function hubComparisonTerminal(job) {
    return ['finished','failed','stopped','completed','ended_early'].includes(job.status);
}
function hubComparisonEvaluation(job) {
    const final=Object.values(hubFinalEvaluationCohort(job)||{})[0];
    if(final)return final;
    if(job.evaluation_stop?.all_of)return job.evaluation_stop_evidence?.all_of?.[0]||
        Object.values(job.evaluation_candidates?.[0]?.receipts||{})[0]||null;
    return job.evaluation_stop_evidence||job.last_evaluation||null;
}
function hubFinalEvaluationCohort(job) {
    return job.checkpoint_model_sha256?job.evaluation_candidates?.find(cohort=>
        cohort.checkpoint_sha256===job.checkpoint_model_sha256)?.receipts:null;
}
function hubComparisonPoints(run) {
    return run.points.length?run.points:
        [run.job.live_execution?.progress||run.job.final_progress].filter(Boolean);
}
function hubJobLabel(job) {
    const configuration=job?.configuration_name||job?.configuration_id||'Unnamed run';
    return job?.run_name?job.run_name+' · '+configuration:configuration;
}
function hubGenericMetricCatalog(runs) {
    const types=new Map();
    for(const run of runs)for(const point of hubComparisonPoints(run))
        for(const [key,metric] of Object.entries(point.metrics||{})) {
            if(!types.has(key))types.set(key,new Set());
            types.get(key).add(metric.unit);
        }
    return [...types].sort(([a],[b])=>a.localeCompare(b))
        .map(([key,units])=>({key,units:[...units]}));
}

function hubMetricDescription(name,units=[]) {
    const known={
        training_score_rate:'Training score per completed match, counting wins as one and draws as one-half. Opponent changes can affect this rate.',
        training_win_rate:'Fraction of completed training matches won. It is not a fixed-opponent benchmark.',
        training_completed_matches:'Count of completed matches contributing training outcomes. Sample count is not learning quality.',
        training_mean_score_margin:'Average score advantage over the opponent in observed matches. Positive values favor the trained team.',
        training_wins:'Count of training matches won. Interpret alongside the number and difficulty of opponents.',
        training_losses:'Count of training matches lost. Changing opponents can change this count.',
        training_draws:'Count of drawn training matches. Draws contribute half a point to the training score rate.',
        training_mean_score:'Average score obtained in observed matches. Comparability depends on game rules and opponents.',
        food_gathered_exact:'Food gathered according to the simulation’s direct accounting. Consider game duration and available food.',
        combat_kills_exact:'Combat kills directly recorded by the simulation. This alone does not establish match success.',
        ant_decisions_per_second:'Ant policy decisions per second. This measures training throughput rather than model quality.',
        engine_steps_per_second:'Simulation engine steps per second. One step can include multiple ant decisions.',
        total_ppo_updates:'Completed PPO optimization updates. Update count is not evidence of improving policy strength.',
        update_wall_time:'Wall-clock duration of a PPO update. Lower sustained times indicate faster updates.',
        active_training_time:'Trainer-reported active training time, which can exclude pauses. It is not derived from Hub heartbeat time.',
        elapsed_training_seconds:'Trainer-reported elapsed training seconds. This is not inferred from Hub observation timestamps.',
        training_recent_mean_completed_lifespan:'Average lifespan of recently completed courier or colony lives. Still-living subjects do not yet contribute to this average.',
        training_recent_deaths:'Number of deaths or extinctions in the recent training lifespan window. This indicates the lifespan sample size.',
        training_mean_active_age:'Average age of currently living training couriers. A living episode may span several policy updates.',
        training_oldest_active_age:'Oldest currently living courier or colony age. Extreme lifespans do not represent the average.',
        training_active_couriers:'Number of currently active training couriers. Population alone is not a quality score.',
        training_mean_active_colony_age:'Mean age of living training colonies. Compare under similar task settings.',
        training_active_colonies:'Count of currently active colonies. It does not indicate how well they are performing.',
        selection_candidate_survival_score:'Survival-based qualification score for a screened candidate. Selection scenarios can differ across rounds.',
        selection_incumbent_survival_score:'Qualification score for the existing champion. Compare against candidates evaluated under matching conditions.',
        selection_champion_promoted:'Whether the newly evaluated candidate replaced the incumbent champion. Promotion is not an independent measure of generalization.',
        selection_evaluation_round:'Training round at which the candidate was evaluated. This locates the evaluation rather than measuring quality.',
        selection_candidate_mean_deliveries:'Average food deliveries per evaluated courier life or colony. More completed deliveries generally indicate better food-return behavior.',
        selection_candidate_pickup_rate:'Fraction of evaluated lives that pick up food. Picking up food does not guarantee delivery.',
        selection_candidate_mean_steps_per_delivery:'Evaluated steps divided by completed food deliveries. Lower values indicate more efficient delivery; undefined if there were no deliveries.',
        selection_candidate_survival_rate:'Fraction of evaluation lives still alive at the selection horizon. This is bounded survival, not indefinite survival.',
        selection_candidate_mean_lifespan:'Mean observed lifespan in selection evaluation. Lives surviving the horizon are right-censored.',
        selection_candidate_gross_energy_per_step:'Net delivery energy minus energy costs per evaluated step. Positive values suggest more sustainable behavior under the configured rules.',
        selection_candidate_mean_feedings:'Average feeding events per evaluated colony. Track with food collection, births, and survival.',
        selection_candidate_mean_births:'Average births per evaluated colony. Track alongside food supply and colony survival.',
        benchmark_round:'Training round of the last fixed reporting benchmark. These results may be older than the current training checkpoint.',
        benchmark_survival_rate:'Survival fraction in a fixed reporting evaluation. Compare results only under matching benchmark conditions.',
        benchmark_mean_lifespan:'Mean observed lifespan in a fixed reporting benchmark. Survivors at the horizon are right-censored.',
        benchmark_mean_deliveries:'Average completed food deliveries per fixed reporting evaluation. This better controls scenario variation than rotating selection evaluations.',
        benchmark_pickup_rate:'Fraction of benchmark lives that collected food. Food collection is separate from successful delivery.',
        benchmark_mean_steps_per_delivery:'Steps per completed food delivery in the reporting benchmark. Lower is generally more efficient.',
        benchmark_gross_energy_per_step:'Net energy per step in the reporting benchmark. Interpret using the benchmark’s energy rules.',
        benchmark_mean_feedings:'Average colony feedings in the reporting benchmark. This reflects food consumption at home.',
        benchmark_mean_births:'Average colony births in the reporting benchmark. Interpret with survival and food availability.',
        job_cpu_cores:'Observed CPU core-equivalents consumed by the managed job. This may be less than its leased CPU capacity.',
        job_cpu_lease_utilization_pct:'CPU utilization relative to the job’s allocated quota. High utilization alone does not establish useful training progress.',
        job_memory_mb:'Memory used by the supervised job, in MiB. This is not total machine memory.',
        job_memory_lease_utilization_pct:'Memory used as a percentage of the job’s configured limit. High values can indicate limited headroom.',
        job_cpu_throttled_seconds:'Accumulated CPU quota throttling time. Heavy throttling may limit training throughput.',
        job_cpu_throttle_events:'Count of CPU quota throttling events. Interpret together with CPU allocation and timing.',
        host_cpu_busy_pct:'Overall host CPU busy percentage, including other workloads. It is not isolated job utilization.',
        host_gpu_busy_pct:'Utilization of the physical GPU. This is host-wide, not isolated to a job.',
        host_gpu_vram_mb:'Used VRAM on the physical GPU, including other processes. This is not the job’s exclusive memory usage.',
        host_gpu_temperature_c:'GPU temperature reported by the hardware sensor. Temperature alone cannot establish throttling.',
        host_gpu_hotspot_temperature_c:'GPU hotspot or junction temperature. Interpretation requires device-specific limits.',
        host_gpu_power_w:'Power consumption of the physical GPU. It is not isolated job energy use.',
        collection_seconds:'Time spent collecting training experience. Compare with optimization time to identify bottlenecks.',
        optimization_seconds:'Time spent optimizing the model. A high share may indicate learner-bound throughput.',
        ppo_metric_readback_seconds:'Time spent reading PPO metrics back from the device. GPU synchronization may contribute.',
        loss:'Model training loss reported by the optimizer. A lower loss does not necessarily mean better gameplay.',
        policy_loss:'Policy optimization objective reported by the trainer. It is not a direct measure of game performance.',
        value_loss:'Value-function training loss. Interpret alongside reward and independent evaluations.',
        entropy:'Entropy of the policy’s action distribution. Lower entropy can indicate less exploration, not necessarily stronger play.',
        mean_reward:'Average training reward. The interpretation depends on the task and reward definition.',
    };
    if(Object.hasOwn(known,name))return known[name];
    const gpu=/^host_gpu_(\d+)_(busy_pct|used_vram_mb|temperature_c|hotspot_temperature_c|power_w)$/.exec(name);
    if(gpu)return 'Reports '+gpu[2].replace(/_/g,' ')+' of physical GPU '+gpu[1]+'. This is host-wide hardware telemetry, not isolated job usage.';
    const collector=/^collector_(\d+)_(.+)$/.exec(name);
    if(collector)return 'Reports '+collector[2].replace(/_/g,' ')+' for rollout collector '+collector[1]+'. Use this to inspect collector throughput or overhead, not policy strength.';
    const display=name.replace(/_/g,' ');
    const unit=units.length?' (units: '+units.join('/')+')':'';
    return 'Reports '+display+unit+' as published by the trainer or machine agent. This diagnostic value alone does not establish learning quality.';
}

function hubMetricValue(point,key) {
    return point.metrics?.[key.slice(7)]?.value;
}
// A chart axis is either the recorded project step counter or an explicit,
// correctly unit-tagged trainer measurement. Never infer elapsed training
// time from asynchronous Hub heartbeat arrival or host utilization.
function hubComparisonAxisValue(point,key) {
    if(key==='steps_completed')
        return Number.isSafeInteger(point.steps_completed)&&point.steps_completed>=0
            ?point.steps_completed:null;
    if(key==='elapsed_training_seconds'||key==='active_training_time') {
        const metric=point.metrics?.[key];
        return metric?.unit==='seconds'&&Number.isFinite(metric.value)&&
            metric.value>=0?metric.value:null;
    }
    return null;
}

// Observational scaling evidence only. Chart histories are bounded/decimated;
// never use these summaries to tune a running job, select a winning model or
// claim experimental equivalence. An allocation is not a measured CPU/GPU use.
function hubScalingEvidence(runs,key,allJobs=[],leases=[]) {
    const name=typeof key==='string'&&key.startsWith('metric:')?key.slice(7):null;
    if(!name)return [];
    const median=values=>{
        const sorted=values.slice().sort((a,b)=>a-b);
        const middle=Math.floor(sorted.length/2);
        return sorted.length%2?sorted[middle]:
            (sorted[middle-1]+sorted[middle])/2;
    };
    // The learner's reservation is only one member of an atomic distributed
    // group. Read the separately reserved remote jobs; never infer resources
    // from collector counts, measured rates or machine CPU capacity.
    const allocation=job=>{
        const distributed=job.distributed_rollout_managed===true;
        const workers=distributed?allJobs.filter(other=>
            other.project_id==='tinybrains_rollout'&&
            other.frozen_parameters?.learner_job_id===job.id):[];
        if(distributed&&(workers.length<1||workers.length>8))return null;
        const pinnedThreads=distributed&&workers.every(worker=>
            Number.isSafeInteger(worker.frozen_parameters?.threads)&&
            worker.frozen_parameters.threads>=1&&
            Number.isSafeInteger(worker.resources?.cpus)&&
            worker.frozen_parameters.threads<=worker.resources.cpus)
            ?{
                threads:workers.reduce((sum,worker)=>
                    sum+worker.frozen_parameters.threads,0),
                cpus:workers.reduce((sum,worker)=>
                    sum+worker.resources.cpus,0),
            }:null;
        const members=[job,...workers].map((member,index)=>{
            const gpuCount=member.resources?.gpu_count;
            const lease=leases.find(item=>item.id===member.lease_id&&
                item.job_id===member.id&&
                item.project_id===member.project_id&&
                item.machine_id===member.assigned_machine_id&&
                item.issued_by==='experiment-hub'&&
                ['acknowledged','released'].includes(item.status)&&
                item.resources?.cpus===member.resources?.cpus&&
                item.resources?.ram_mb===member.resources?.ram_mb&&
                item.resources?.gpu_count===gpuCount&&
                (item.resources?.gpu_mode||'exclusive')===
                    (member.resources?.gpu_mode||'exclusive')&&
                (item.resources?.gpu_vram_mb??0)===(member.resources?.gpu_vram_mb??0)&&
                (item.resources?.gpu_compute_pct??100)===(member.resources?.gpu_compute_pct??100)&&
                (item.resources?.min_gpu_vram_mb??0)===(member.resources?.min_gpu_vram_mb??0));
            const pinned=lease?.gpu_bindings;
            // A CPU-only job still requires its own genuine frozen lease.
            // Otherwise project metadata could be mistaken for a proven
            // physical allocation when drawing marginal CPU comparisons.
            const physical=lease&&Number.isSafeInteger(gpuCount)&&gpuCount===0
                ?[]:Array.isArray(pinned)&&pinned.length===gpuCount&&
                    pinned.every(item=>/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/.test(
                        item.pci_address||''))
                    ?pinned.map(item=>item.pci_address).sort():null;
            return {
                host:member.assigned_machine_id,
                role:index===0?'learner':'collector',
                cpus:member.resources?.cpus,
                ram_mb:member.resources?.ram_mb,
                gpus:gpuCount,
                mode:member.resources?.gpu_mode||'exclusive',
                gpu_vram_mb:member.resources?.gpu_vram_mb??0,
                gpu_compute_pct:member.resources?.gpu_compute_pct??100,
                min_gpu_vram_mb:member.resources?.min_gpu_vram_mb??0,
                gpu_ids:physical,
            };
        });
        if(members.some(member=>!member.host||
            !Number.isSafeInteger(member.cpus)||member.cpus<1||
            !Number.isSafeInteger(member.ram_mb)||member.ram_mb<1||
            !Number.isSafeInteger(member.gpus)||member.gpus<0)||
            new Set(members.map(member=>member.host)).size!==members.length)
            return null;
        members.sort((a,b)=>a.host.localeCompare(b.host));
        return {
            members,
            cpus:members.reduce((total,m)=>total+m.cpus,0),
            gpus:members.reduce((total,m)=>total+m.gpus,0),
            host:members.map(m=>m.host).join(' + '),
            topology:members.map(m=>m.host+':'+m.role).join('|'),
            gpu_mode:members.map(m=>m.mode).join('|'),
            claims_verified:members.every(m=>m.gpu_ids!==null),
            collector_threads:pinnedThreads?.threads??null,
            collector_cpus:pinnedThreads?.cpus??null,
        };
    };
    const rows=runs.map(run=>{
        const points=hubComparisonPoints(run);
        let lastBreak=0;
        for(let i=1;i<points.length;i++)
            if(points[i].break_before)lastBreak=i;
        const recent=points.slice(lastBreak);
        const observations=recent.map(point=>point.metrics?.[name])
            .filter(m=>m?.unit==='rate'&&
                Number.isFinite(m.value)&&m.value>=0)
            .map(m=>m.value);
        const resources=allocation(run.job);
        return {
            slot:run.slot,job_id:run.job.id,
            configuration_sha256:run.job.configuration_sha256,
            host:resources?.host||null,
            topology:resources?.topology||null,
            claims_verified:resources?.claims_verified===true,
            collector_threads:resources?.collector_threads??null,
            collector_cpus:resources?.collector_cpus??null,
            members:resources?.members||null,
            lineage:run.job.artifact_lineage_root_id||run.job.id,
            status:run.job.status,
            distributed:run.job.distributed_rollout_managed===true,
            cpus:resources?.cpus??null,
            gpus:resources?.gpus??null,
            gpu_mode:resources?.gpu_mode||null,
            sample_count:observations.length,
            median_rate:observations.length?median(observations):null,
            low_rate:observations.length?Math.min(...observations):null,
            high_rate:observations.length?Math.max(...observations):null,
            comparison:null,
        };
    });
    const samePerHost=(a,b,key)=>a.members.every((m,i)=>
        m[key]===b.members[i][key]);
    const samePhysicalGpus=(a,b)=>a.members.every((m,i)=>
        m.gpu_ids.length===b.members[i].gpu_ids.length&&
        m.gpu_ids.every((id,j)=>id===b.members[i].gpu_ids[j]));
    const retainsPhysicalGpus=(a,b)=>a.members.every((m,i)=>
        b.members[i].gpu_ids.every(id=>m.gpu_ids.includes(id)));
    const nondecreasing=(a,b,key)=>a.members.every((m,i)=>
        m[key]>=b.members[i][key]);
    for(const row of rows) {
        if(!row.members||!row.claims_verified||row.sample_count<5||
            !['finished','stopped','completed','ended_early'].includes(row.status)||
            !/^[0-9a-f]{64}$/.test(row.configuration_sha256||''))continue;
        const comparable=rows.filter(other=>other!==row&&
            other.members&&other.claims_verified&&other.sample_count>=5&&
            other.distributed===row.distributed&&
            ['finished','stopped','completed','ended_early'].includes(other.status)&&
            other.topology===row.topology&&
            other.configuration_sha256===row.configuration_sha256&&
            other.gpu_mode===row.gpu_mode&&
            ['ram_mb','gpu_vram_mb','gpu_compute_pct',
                'min_gpu_vram_mb'].every(key=>samePerHost(row,other,key))&&
            other.median_rate!==null);
        // A group is comparable only if all member hosts and GPU modes
        // match. No machine may lose CPU capacity in an "added CPU" trial,
        // or silently exchange GPU/CPU allocation with another member.
        const cpuBase=comparable.filter(other=>
            other.gpus===row.gpus&&samePerHost(row,other,'gpus')&&
            samePhysicalGpus(row,other)&&
            other.cpus<row.cpus&&nondecreasing(row,other,'cpus'))
            .sort((a,b)=>b.cpus-a.cpus)[0];
        const gpuBase=comparable.filter(other=>
            other.cpus===row.cpus&&samePerHost(row,other,'cpus')&&
            retainsPhysicalGpus(row,other)&&
            other.gpus<row.gpus&&nondecreasing(row,other,'gpus'))
            .sort((a,b)=>b.gpus-a.gpus)[0];
        const baseline=cpuBase||gpuBase;
        if(baseline) {
            const resource=cpuBase?'cpu':'gpu';
            const delta=row[resource+'s']-baseline[resource+'s'];
            row.comparison={
                resource,baseline_slot:baseline.slot,delta_resources:delta,
                median_delta:row.median_rate-baseline.median_rate,
                marginal_rate:(row.median_rate-baseline.median_rate)/delta,
                shared_lineage:row.lineage===baseline.lineage,
            };
        }
    }
    return rows;
}
// Generic, observational profile of recorded duration measurements.
// Units of seconds or milliseconds do not prove a per-update stage duration:
// cumulative clocks and overlapping work must not be added or compared as
// exclusive CPU/GPU time.
function hubMeasuredSeconds(run) {
    const points=hubComparisonPoints(run);
    let lastBreak=0;
    for(let i=1;i<points.length;i++)
        if(points[i].break_before)lastBreak=i;
    const recent=points.slice(lastBreak).slice(-64);
    const values=new Map();
    for(const point of recent)
        for(const [key,item] of Object.entries(point.metrics||{})) {
            // This explicitly cumulative run clock is already an X-axis
            // choice; ranking its median among phases hides useful timings.
            if(key==='elapsed_training_seconds'||key==='active_training_time'||
                key==='job_cpu_throttled_seconds')continue;
            const scale=item?.unit==='seconds'?1:
                item?.unit==='milliseconds'?0.001:null;
            if(scale===null||!Number.isFinite(item.value)||
                item.value<0||item.value>1e12)continue;
            if(!values.has(key))values.set(key,[]);
            values.get(key).push(item.value*scale);
        }
    const rows=[];
    for(const [key,samples] of values) {
        if(samples.length<5)continue;
        const sorted=samples.slice().sort((a,b)=>a-b);
        const mid=Math.floor(sorted.length/2);
        const median=sorted.length%2?sorted[mid]:
            (sorted[mid-1]+sorted[mid])/2;
        rows.push({key,samples:samples.length,median,
            minimum:sorted[0],maximum:sorted.at(-1)});
    }
    return rows.sort((a,b)=>b.median-a.median||
        a.key.localeCompare(b.key)).slice(0,12);
}
// Read-only, next-run guidance from actual paired per-update collector rates.
// Never modify a running job or infer unmeasured machine scaling.
function hubRolloutWeightRecommendation(run) {
    const job=run.job;
    if(job.project_id!=='tinybrains'||job.distributed_rollout_managed!==true||
        job.frozen_parameters?.remote_rollouts!==true||
        !['finished','stopped'].includes(job.status))return null;
    const points=hubComparisonPoints(run);
    let lastBreak=0;
    for(let i=1;i<points.length;i++)
        if(points[i].break_before)lastBreak=i;
    const recent=points.slice(lastBreak).slice(-64);
    let collectorCount=null;
    const complete=[];
    for(const point of recent) {
        const metrics=point.metrics||{};
        const count=metrics.collector_count;
        if(count?.unit!=='count'||!Number.isSafeInteger(count.value)||
            count.value<2||count.value>9)continue;
        if(collectorCount===null)collectorCount=count.value;
        if(count.value!==collectorCount)return null;
        const rates=[],targets=[];
        for(let i=0;i<collectorCount;i++) {
            const rate=metrics['collector_'+i+'_decisions_per_second'];
            const target=metrics['collector_'+i+'_action_target'];
            if(rate?.unit!=='rate'||!Number.isFinite(rate.value)||
                rate.value<=0||target?.unit!=='count'||
                !Number.isSafeInteger(target.value)||target.value<1)break;
            rates.push(rate.value);
            targets.push(target.value);
        }
        if(rates.length===collectorCount)complete.push({rates,targets});
    }
    if(complete.length<5)return null;
    // Shard work shares are frozen for this run. If the published targets
    // changed, these historical rate samples do not support one recommendation.
    const targets=complete[0].targets;
    if(complete.some(sample=>sample.targets.some((v,i)=>v!==targets[i])))
        return null;
    const original=job.frozen_parameters.rollout_action_weights;
    if(original!==undefined&&(!Array.isArray(original)||
        original.length!==collectorCount))return null;
    const latest=complete.slice(-32);
    const median=values=>{
        const sorted=values.slice().sort((a,b)=>a-b);
        const middle=Math.floor(sorted.length/2);
        return sorted.length%2?sorted[middle]:
            (sorted[middle-1]+sorted[middle])/2;
    };
    const rates=targets.map((_,i)=>median(latest.map(x=>x.rates[i])));
    const smallest=Math.min(...rates),largest=Math.max(...rates);
    if(!Number.isFinite(smallest)||smallest<=0||largest/smallest>1024)
        return null;
    // Approximate relative rates in small deterministic integer weights.
    // Bound every value to TinyBrains' frozen CLI range [1,1024].
    const scale=Math.min(16,1024/(largest/smallest));
    const raw=rates.map(rate=>Math.max(1,Math.round(rate/smallest*scale)));
    const gcd=(a,b)=>b?gcd(b,a%b):a;
    const divisor=raw.reduce(gcd);
    return {
        job_id:job.id,
        profile:job.frozen_parameters.profile,
        profile_sha256:job.frozen_parameters.profile_sha256,
        weights:raw.map(value=>value/divisor),
        current:original||Array(collectorCount).fill(1),
        rates,targets,samples:latest.length,
        // A new target can change throughput; this is a starting estimate.
    };
}

// Preserve the operator's explicit chart selection while adding balanced
// collector timing pairs when the bounded project-neutral chart has room.
// Full source telemetry remains in the separate observation archive.
function hubDistributedMetricSelection(selected,remoteCount) {
    if(!Array.isArray(selected)||!Number.isSafeInteger(remoteCount)||
        remoteCount<1||remoteCount>8)
        throw Error('Choose recorded metrics and 1–8 approved collectors.');
    const total=remoteCount+1;
    const required=['collector_count',...Array.from({length:total},(_,i)=>[
        'collector_'+i+'_decisions_per_second',
        'collector_'+i+'_action_target',
    ]).flat()];
    const names=[...new Set([...selected,...required])];
    if(names.length>96)
        throw Error('Distributed collector metrics plus selected Train metrics exceed 96. Deselect unused metrics before queueing.');
    const includePair=(first,second)=>{
        const missing=[first,second].filter(name=>!names.includes(name));
        if(names.length+missing.length<=96)names.push(...missing);
    };
    for(let i=0;i<total;i++)
        includePair('collector_'+i+'_rollout_engine_step_seconds',
            'collector_'+i+'_learner_inference_seconds');
    for(let i=0;i<total;i++) {
        const model='collector_'+i+'_model_prepare_seconds';
        if(!names.includes(model)&&names.length<96)names.push(model);
        if(i>0) {
            const roundtrip='collector_'+i+'_roundtrip_seconds';
            if(!names.includes(roundtrip)&&names.length<96)names.push(roundtrip);
        }
    }
    return names;
}
function hubCollectorStageProfile(run) {
    const job=run.job;
    if(job.project_id!=='tinybrains'||job.distributed_rollout_managed!==true||
        job.frozen_parameters?.remote_rollouts!==true)return null;
    const points=hubComparisonPoints(run);
    let lastBreak=0;
    for(let i=1;i<points.length;i++)
        if(points[i].break_before)lastBreak=i;
    const recent=points.slice(lastBreak).slice(-64);
    let count=null;
    for(const point of recent) {
        const item=point.metrics?.collector_count;
        if(item?.unit!=='count'||!Number.isSafeInteger(item.value)||
            item.value<2||item.value>9)continue;
        if(count!==null&&count!==item.value)return null;
        count=item.value;
    }
    if(count===null)return null;
    const value=(metrics,slot,name,unit)=>{
        const item=metrics['collector_'+slot+'_'+name];
        return item?.unit===unit&&Number.isFinite(item.value)&&
            item.value>=0?item.value:null;
    };
    const median=values=>{
        if(!values.length)return null;
        const sorted=values.slice().sort((a,b)=>a-b);
        const middle=Math.floor(sorted.length/2);
        return sorted.length%2?sorted[middle]:
            (sorted[middle-1]+sorted[middle])/2;
    };
    const slots=[];
    for(let slot=0;slot<count;slot++) {
        const samples=[];
        for(const point of recent) {
            const metrics=point.metrics||{};
            if(metrics.collector_count?.value!==count)continue;
            const engine=value(metrics,slot,'rollout_engine_step_seconds','seconds');
            const inference=value(metrics,slot,'learner_inference_seconds','seconds');
            const rate=value(metrics,slot,'decisions_per_second','rate');
            if(engine===null||inference===null||rate===null)continue;
            samples.push({
                engine,inference,rate,
                model:value(metrics,slot,'model_prepare_seconds','seconds'),
                roundtrip:value(metrics,slot,'roundtrip_seconds','seconds'),
            });
        }
        if(samples.length<5)continue;
        const latest=samples.slice(-32);
        const observed=key=>{
            const values=latest.map(sample=>sample[key]);
            return values.every(v=>v!==null)?median(values):null;
        };
        slots.push({
            slot,samples:latest.length,
            engine:observed('engine'),inference:observed('inference'),
            rate:observed('rate'),model:observed('model'),
            roundtrip:observed('roundtrip'),
        });
    }
    return slots.length?{job_id:job.id,collector_count:count,slots}:null;
}
function trialBatchLabel(job) {
    const batch=job.trial_batch;
    return batch?' · batch '+batch.id.slice(-8)+' · trial '+batch.index+'/'+batch.size:'';
}
function hubComparisonCsv(exported) {
    const fields=[
        'slot','job_id','project_id','configuration_name','run_name','configuration_sha256',
        'selected_x_axis','selected_metric','selected_metrics','assigned_machine_id',
        'artifact_lineage_root_id','continuation_of_job_id','status',
        'observed_at','source_modified_at','experiment_id','segment_id',
        'break_before','steps_completed','x_value',
        'trial_batch_id','trial_batch_index','trial_batch_size',
        'resource_bundle_id','resource_bundle_index','resource_bundle_size',
    ];
    const metrics=new Set(exported.metrics||[]);
    for(const key of [...metrics].sort())
        fields.push('metric_'+key,'unit_'+key);
    const encode=value=>{
        if(value==null)return '';
        let str=String(value);
        // Prevent spreadsheet software from interpreting user-supplied
        // configuration labels as formulas.
        if(typeof value==='string'&&str.trimStart().length&&
            '=+-@'.includes(str.trimStart()[0]))
            str="'"+str;
        const quote=str.includes(',')||str.includes('"')||
            str.includes(String.fromCharCode(10))||
            str.includes(String.fromCharCode(13));
        return quote?'"'+str.replaceAll('"','""')+'"':str;
    };
    const rows=exported.runs.flatMap(run=>run.points.map(point=>({
        slot:run.slot,job_id:run.job_id,project_id:exported.project_id,
        configuration_name:run.configuration_name,
        run_name:run.run_name||'',
        configuration_sha256:run.configuration_sha256,
        selected_x_axis:exported.x_axis,
        selected_metric:exported.metric,
        selected_metrics:(exported.metrics||[]).join('|'),
        assigned_machine_id:run.assigned_machine_id,
        artifact_lineage_root_id:run.artifact_lineage_root_id,
        continuation_of_job_id:run.continuation_of_job_id,
        trial_batch_id:run.trial_batch?.id,
        trial_batch_index:run.trial_batch?.index,
        trial_batch_size:run.trial_batch?.size,
        resource_bundle_id:run.resource_bundle?.id,
        resource_bundle_index:run.resource_bundle?.index,
        resource_bundle_size:run.resource_bundle?.size,
        status:run.status,...point,
        ...Object.fromEntries(Object.entries(point.metrics||{}).flatMap(
            ([key,metric])=>[
                ['metric_'+key,metric.value],
                ['unit_'+key,metric.unit],
            ])),
    })));
    const crlf=String.fromCharCode(13,10);
    return String.fromCharCode(0xFEFF)+fields.join(',')+crlf+
        rows.map(row=>fields.map(f=>encode(row[f])).join(','))
            .join(crlf)+crlf;
}

// A read-only cross-project view. No dependency record is ever mutated
// by rendering, and observed native completion is not benchmark success.
function sequenceRowsForJobs(jobs) {
    const successor=new Map();
    for(const job of jobs) {
        const parent=job.after_job_id||job.continuation_of_job_id;
        if(!parent||job.status==='cancelled')continue;
        if(!successor.has(parent))successor.set(parent,[]);
        successor.get(parent).push(job);
    }
    return jobs.filter(job=>!job.after_job_id&&!job.continuation_of_job_id&&
        successor.has(job.id))
        .sort((a,b)=>String(b.created_at||'').localeCompare(
            String(a.created_at||'')))
        .slice(0,20).map(root=>{
            const chain=[root],visited=new Set([root.id]);
            let conflict=false;
            while(true) {
                const candidates=successor.get(chain[chain.length-1].id)||[];
                if(candidates.length>1){conflict=true;break;}
                const child=candidates[0];
                if(!child)break;
                if(visited.has(child.id)){conflict=true;break;}
                visited.add(child.id);
                chain.push(child);
            }
            // A finished or evaluation-stopped stage is only complete
            // when it can actually hand off its queued successor. Read
            // coordinator-owned decisions; never infer evaluation or
            // checkpoint authority from a terminal state in the browser.
            const completed=(job,index)=>Boolean(job.capacity_released_at&&
                job.independent_sequence_complete===true&&
                (chain[index+1]?.continuation_of_job_id!==job.id||
                    job.checkpoint_continuation_complete===true));
            const current=chain.find((job,index)=>!completed(job,index))||
                chain[chain.length-1];
            const blocked=chain.find(job=>job.status==='waiting_predecessor');
            return {root,chain,current,canCancel:!conflict,
                completed:chain.filter(completed).length,
                reason:conflict?'Conflicting or cyclic successor records; inspect the registry.'
                    :blocked?.predecessor_wait?.reason||
                        current?.status_reason||'—'};
        });
}

// Older live services and unreleased claims remain operable even when
// the registry contains far more historical jobs than a table can show.
function hubVisibleJobs(jobs,limit) {
    const priority=job=>{
        if(['running','start_requested','launch_requested','stop_requested']
            .includes(job.status))return 4;
        if(!job.capacity_released_at&&
            ['failed','unverified'].includes(job.status))return 3;
        if(!job.capacity_released_at&&job.lease_id&&(
            ['finished','stopped','waiting_adapter'].includes(job.status)))
            return 2;
        if(job.status==='waiting_predecessor'&&
            ['needs_review','broken_lineage'].includes(job.predecessor_wait?.status))
            return 3;
        return 0;
    };
    const ordered=jobs.map((job,index)=>({job,index,priority:priority(job)}))
        .sort((a,b)=>b.priority-a.priority||b.index-a.index);
    // Do not discard live or capacity-holding jobs just because a machine
    // has accumulated more records than the historical display limit.
    const actionable=ordered.filter(item=>item.priority>=2).length;
    return ordered.slice(0,Math.max(limit,actionable)).map(item=>item.job);
}

function approvedTinyBrainsProfiles(machines) {
    const profiles=new Map();
    for(const machine of machines) {
        if(machine.status!=='connected'||!['win32','linux'].includes(machine.agent_platform))continue;
        for(const [profile,digest] of Object.entries(machine.tinybrains_profiles||{})) {
            const key=profile+':'+digest;
            if(!profiles.has(key))
                profiles.set(key,{profile,digest,workers:0,cudaWorkers:0});
            profiles.get(key).workers++;
            if(machine.tinybrains_cuda_profiles?.includes(profile))
                profiles.get(key).cudaWorkers++;
        }
    }
    return [...profiles.entries()].sort(([a],[b])=>a.localeCompare(b));
}

document.addEventListener('DOMContentLoaded', () => {
    const $ = id => document.getElementById(id);
    let project = document.documentElement.dataset.hubDefaultProject || 'bees';
    let tab = 'monitor', state = null, editing = null;
    let comparisonData = null,comparisonProject = null,comparisonError = null;
    let comparisonRequest = 0,selectionProject = null,previewCandidateKey = null;
    let comparisonDefaultsApplied = false;
    let pipelineKindProject = null;
    let pipelineStageProject = null;
    let pipelineStageKind = null;
    let envArgsTouched = false;
    let lastGpuBindingMachine = null, budgetDraft = null, gpuBudgetDraft = null;
    const stagedMoves=new Map();
    let applyingMoves=false;
    const action = async (route, payload) => {
        const r = await fetch('/api/'+route,{
            method:'POST',
            headers:{'Content-Type':'application/json'},
            body:JSON.stringify(payload),
        });
        const data=await r.json();
        if(!r.ok)throw Error(data.error || r.statusText);
        return data;
    };
    const feedback=(id,message)=>{ $(id).textContent=message; };
    function placementObservedLoad(place) {
        if(!place.capacity_fits)return '';
        if(place.suggested_gpus?.length===1) {
            const vram=Number.isSafeInteger(place.observed_gpu_free_vram_mb)
                ?' · '+place.observed_gpu_free_vram_mb+' MiB physical GPU free':'';
            const activity=Number.isFinite(place.observed_gpu_busy_pct)
                ?' · '+place.observed_gpu_busy_pct+'% GPU busy':'';
            return vram+activity;
        }
        if(place.suggested_gpus?.length>1&&
            Array.isArray(place.observed_gpu_devices)) {
            return place.observed_gpu_devices.map(g=>(
                ' · '+g.id+
                (Number.isFinite(g.busy_pct)?' '+g.busy_pct+'% busy':'')+
                (Number.isSafeInteger(g.physical_free_vram_mb)
                    ?' '+g.physical_free_vram_mb+' MiB physical free':'')
            )).join('');
        }
        return Number.isFinite(place.observed_host_cpu_busy_pct)
            ?' · '+place.observed_host_cpu_busy_pct.toFixed(1)+'% host CPU busy':'';
    }
    function allocationBlocks(results) {
        const blocked=results.filter(item=>item.status==='insufficient_capacity');
        const counts=new Map();
        for(const item of blocked) {
            const reason=item.reason||'Insufficient capacity.';
            counts.set(reason,(counts.get(reason)||0)+1);
        }
        return [...counts].map(([reason,count])=>count+' blocked: '+reason)
            .join(' · ');
    }
    function cell(tr, value) {
        const td=document.createElement('td');
        td.textContent=String(value);
        tr.append(td);
        return td;
    }
    function button(label, onClick) {
        const b=document.createElement('button');
        b.type='button';b.textContent=label;b.addEventListener('click',onClick);
        return b;
    }
    function selectedMoveDestination(job) {
        const destination=state.machines.find(m=>m.id===$('hub-job-machine').value);
        if(destination&&destination.id!==job.assigned_machine_id)return destination;
        feedback('hub-job-allocate-message','Choose a different preferred machine above.');
        return null;
    }
    function renderStagedMoves() {
        const list=$('hub-claim-moves');
        list.replaceChildren();
        for(const [id,move] of stagedMoves) {
            const row=document.createElement('li');
            row.append(document.createTextNode(move.label+' '),
                button('Remove',()=>{stagedMoves.delete(id);renderStagedMoves();}));
            list.append(row);
        }
        $('hub-apply-claim-moves').disabled=applyingMoves||!stagedMoves.size;
    }
    // The visual pipeline editor only selects saved configurations. It
    // cannot supply executable paths or authorize a native process.
    function addPipelineStage() {
        const container=$('hub-pipeline-rows');
        if(container.children.length>=16)return;
        const row=document.createElement('div');
        row.className='hub-pipeline-stage';
        const configLabel=document.createElement('label');
        const select=document.createElement('select');
        select.className='hub-pipeline-config';
        configLabel.append(select);
        const resourceLabel=document.createElement('label');
        resourceLabel.textContent='Requested CPU/RAM/GPU resources (JSON)';
        const resources=document.createElement('input');
        resources.type='text';
        resources.className='hub-pipeline-resources';
        try {
            resources.value=JSON.stringify(JSON.parse($('hub-job-resources').value));
        }catch(error){
            resources.value='{"cpus":2,"ram_mb":2048,"gpu_count":0}';
        }
        resourceLabel.append(resources);
        const remove=button('Remove',()=>{
            if(container.children.length<=2)return;
            row.remove();
            renderPipelineStages();
        });
        row.append(configLabel,resourceLabel,remove);
        container.append(row);
    }
    function renderPipelineStages() {
        const rows=[...$('hub-pipeline-rows').children];
        const checkpointPipeline=['tinybrains','courier'].includes(project)&&
            $('hub-pipeline-kind').value==='continuation';
        const configs=(state?.configurations||[]).filter(c=>
            checkpointPipeline?c.project_id===project:
                !['bees','ants'].includes(c.project_id));
        const relationship=$('hub-pipeline-kind').value;
        const sameSelection=pipelineStageProject===project&&
            pipelineStageKind===relationship;
        for(const [i,row] of rows.entries()) {
            const label=row.firstElementChild;
             const select=row.querySelector('select');
            label.textContent=(relationship==='batch'?'Trial ':'Stage ')+(i+1)+' saved configuration';
            label.append(select);
            const old=select.value;
            select.replaceChildren(...configs.map(c=>new Option(
                (checkpointPipeline?'':c.project_id+' · ')+
                    c.name+' · revision '+c.revision,c.id)));
            const suggested=configs.find(c=>c.project_id===project);
            if(sameSelection&&configs.some(c=>c.id===old))
                select.value=old;
            else if(suggested)select.value=suggested.id;
            row.querySelector('button').disabled=rows.length<=2;
        }
        pipelineStageProject=project;
        pipelineStageKind=relationship;
        $('hub-pipeline-add').disabled=['bees','ants'].includes(project)||rows.length>=16;
        $('hub-pipeline-queue').disabled=['bees','ants'].includes(project)||
            rows.length<2||!configs.length;
    }
    // Build capacity bundles from the same saved project configurations and
    // authenticated machine choices used by normal job Start. A bundle never
    // transmits arbitrary executable paths or worker network endpoints.
    function addResourceBundleMember() {
        const container=$('hub-resource-bundle-rows');
        if(container.children.length>=16)return;
        const row=document.createElement('div');
        row.className='hub-pipeline-stage';
        const configLabel=document.createElement('label');
        const configCaption=document.createElement('span');
        configCaption.className='hub-bundle-caption';
        const config=document.createElement('select');
        config.className='hub-bundle-config';
        config.addEventListener('change',renderResourceBundleMembers);
        configLabel.append(configCaption,config);
        const machineLabel=document.createElement('label');
        const machineCaption=document.createElement('span');
        machineCaption.className='hub-bundle-caption';
        const machine=document.createElement('select');
        machine.className='hub-bundle-machine';
        machineLabel.append(machineCaption,machine);
        const resourceLabel=document.createElement('label');
        resourceLabel.textContent='Fixed CPU/RAM/GPU budget (JSON)';
        const resources=document.createElement('input');
        resources.type='text';
        resources.className='hub-bundle-resources';
        try{
            resources.value=JSON.stringify(JSON.parse($('hub-job-resources').value));
        }catch(error){
            resources.value='{"cpus":2,"ram_mb":2048,"gpu_count":0}';
        }
        resourceLabel.append(resources);
        const remove=button('Remove',()=>{
            if(container.children.length<=2)return;
            row.remove();
            renderResourceBundleMembers();
        });
        row.append(configLabel,machineLabel,resourceLabel,remove);
        container.append(row);
    }
    function renderResourceBundleMembers() {
        const rows=[...$('hub-resource-bundle-rows').children];
        const configs=(state?.configurations||[]).filter(c=>
            !['bees','ants','tinybrains_rollout'].includes(c.project_id));
        const machines=state?.machines||[];
        for(const [i,row] of rows.entries()) {
            const captions=row.querySelectorAll('.hub-bundle-caption');
            const config=row.querySelector('.hub-bundle-config');
            const machine=row.querySelector('.hub-bundle-machine');
            const oldConfig=config.value,oldMachine=machine.value;
            captions[0].textContent='Member '+(i+1)+' saved configuration';
            captions[1].textContent='Machine';
            config.replaceChildren(...configs.map(c=>new Option(
                c.project_id+' · '+c.name+' · revision '+c.revision,c.id)));
            if(configs.some(c=>c.id===oldConfig))config.value=oldConfig;
            else {
                const own=configs.find(c=>c.project_id===project);
                if(own)config.value=own.id;
            }
            const selected=configs.find(c=>c.id===config.value);
            const eligible=machines.filter(m=>
                m.status==='connected'&&m.local_executor_enabled===true&&
                m.native_preflight_passed===true&&
                m.installed_projects?.includes(selected?.project_id));
            machine.replaceChildren(...eligible.map(m=>
                new Option(m.name+' ('+m.id+')',m.id)));
            if(eligible.some(m=>m.id===oldMachine))machine.value=oldMachine;
            row.querySelector('button').disabled=rows.length<=2;
        }
        $('hub-resource-bundle-add').disabled=rows.length>=16;
        $('hub-resource-bundle-queue').disabled=rows.length<2||
            !rows.every(row=>row.querySelector('.hub-bundle-machine').value);
        const container=$('hub-resource-bundle-existing');
        container.replaceChildren();
        const groups=new Map();
        for(const job of state?.jobs||[]) {
            const id=job.resource_bundle?.id;
            if(!id)continue;
            if(!groups.has(id))groups.set(id,[]);
            groups.get(id).push(job);
        }
        // Keep unresolved bundles visible even after many completed runs.
        // Historical rows remain bounded; the complete job history is in Train.
        const newest=[...groups].reverse();
        const pending=newest.filter(([,members])=>members.some(job=>
            job.status!=='cancelled'&&!job.capacity_released_at));
        const completed=newest.filter(([,members])=>members.every(job=>
            job.status==='cancelled'||job.capacity_released_at));
        for(const [id,members] of [...pending,...completed].slice(0,32)) {
            const row=document.createElement('div');
            row.className='hub-pipeline-stage';
            const launchPlan=(state.bundle_start_plans||[])
                .filter(plan=>plan.bundle_id===id).at(-1);
            const unresolvedLaunch=launchPlan&&
                ['pending','needs_review'].includes(launchPlan.status);
            const active=members.filter(j=>j.status!=='cancelled');
            const claims=active.filter(j=>
                (state.reservations||[]).some(lease=>lease.id===j.lease_id&&
                    ['offered','acknowledged'].includes(lease.status)));
            const details=document.createElement('span');
            details.textContent='Bundle '+id.slice(-8)+' · '+
                members.length+' jobs · '+claims.length+' outstanding claims · '+
                members.map(j=>j.project_id+' on '+
                    (j.assigned_machine_id||'unassigned')+': '+j.status).join('; ')+
                (launchPlan?' · Scheduled launch '+launchPlan.status+
                    ' (expires '+new Date(launchPlan.expires_at).toLocaleString()+')'+
                    (launchPlan.status==='pending'&&launchPlan.waiting_reason
                        ?': '+launchPlan.waiting_reason
                        :launchPlan.reason?': '+launchPlan.reason:''):'');
            row.append(details);
            const complete=members.length===members[0].resource_bundle.size&&
                members.every((job,index)=>job.resource_bundle.index===index+1);
            const ids=members.map(job=>job.id);
            const canStart=complete&&!unresolvedLaunch&&members.every(job=>
                job.status==='waiting_adapter'&&!job.dispatch_id&&
                job.native_start_readiness?.eligible===true);
            const reserved=complete&&members.every(job=>
                job.status==='waiting_adapter'&&!job.dispatch_id&&
                Boolean(job.lease_id)&&Boolean(job.assigned_machine_id));
            if(reserved&&!unresolvedLaunch) {
                const usesGpu=members.some(job=>job.resources?.gpu_count>0);
                let keepAcrossRestart=false;
                if(!usesGpu) {
                    const label=document.createElement('label');
                    const check=document.createElement('input');
                    check.type='checkbox';
                    check.checked=false;
                    label.append(check,document.createTextNode(
                        ' Keep CPU-only approval across coordinator restart'));
                    label.title='Requires fresh authenticated agents, native preflight and the original matching resource claims. No ambiguous process is retried.';
                    row.append(label);
                    label.dataset.bundleRestart='1';
                    row.append(button('Schedule auto-start when ready',async()=>{
                        keepAcrossRestart=check.checked;
                        await scheduleLaunch(keepAcrossRestart,false);
                    }));
                }else {
                    row.append(button('Schedule GPU auto-start when ready',async()=>{
                        await scheduleLaunch(false,true);
                    }));
                }
                async function scheduleLaunch(restart,gpu) {
                    if(!confirm('Approve one native Start for each of the '+members.length+
                        ' reserved jobs as soon as ALL native lease receipts and Start checks pass? '+
                        'No work starts until every machine is qualified. '+
                        'Every finished/stopped member will release its own claim after a verified native report. '+
                        (gpu?'This includes GPU execution, expires after 20 minutes and NEVER survives a coordinator restart. ':
                            'This CPU-only approval expires after 24 hours. '+
                            (restart?'You explicitly authorize recovery after coordinator restart, only after fresh machine and lease reconciliation. ':
                                'It is disarmed on coordinator restart. '))+
                        'No failed, unverified or previously dispatched job is ever retried.'))return;
                    try{
                        const plan=await action('hub/resource-bundles/schedule-start',{
                            bundle_id:id,expected_job_ids:ids,confirm_schedule:true,
                            ...(gpu?{confirm_gpu_start:true}:{}),
                            ...(restart?{resume_after_restart:true,
                                confirm_restart_rearm:true}:{}),
                        });
                        feedback('hub-resource-bundle-message',
                            plan.status==='authorized'
                                ?'All '+members.length+' bundle Starts authorized after verifying native readiness.'
                                :'Unattended bundle launch '+plan.status+'; waiting for fresh machine-local proof.');
                        await reload();
                    }catch(error){
                        feedback('hub-resource-bundle-message',String(error.message||error));
                    }
                }
            }
            if(unresolvedLaunch)
                row.append(button('Cancel scheduled launch',async()=>{
                    if(!confirm('Cancel this unattended group Start approval? The reserved jobs and native claims remain unchanged.'))return;
                    try{
                        await action('hub/resource-bundles/cancel-start-plan',{
                            plan_id:launchPlan.id,confirm_cancel:true,
                        });
                        feedback('hub-resource-bundle-message',
                            'Unattended Start permission revoked; reserved jobs remain unchanged.');
                        await reload();
                    }catch(error){
                        feedback('hub-resource-bundle-message',String(error.message||error));
                    }
                }));
            if(canStart)row.append(button('Start entire bundle',async()=>{
                const autoRelease=$('hub-native-release-on-terminal').checked;
                if(!confirm('Authorize '+members.length+
                    ' independent native Starts across the selected Windows/Linux machines in one durable operation? '+
                    'All members must retain their original leases, fresh native readiness and any GPU access qualification. '+
                    'The machines enforce each Start independently; this does not guarantee simultaneous execution or link the jobs into distributed training. '+
                    'CUDA/ROCm GPU compute sharing is not an enforced quota. '+
                    (autoRelease
                        ?'Verified native finished/stopped reports will release each member’s claimed capacity. '
                        :'Members keep their capacity claims after completion until released separately. ')))return;
                try{
                    const result=await action('hub/resource-bundles/start',{
                        bundle_id:id,expected_job_ids:ids,
                        confirm_start:true,release_on_terminal:autoRelease,
                    });
                    $('hub-native-release-on-terminal').checked=false;
                    feedback('hub-resource-bundle-message',
                        'Authorized '+result.starts_authorized+
                        ' native Starts together. Each worker must still verify its own process.');
                    await reload();
                }catch(error){
                    feedback('hub-resource-bundle-message',String(error.message||error));
                }
            }));
            const canStop=complete&&members.some(job=>job.dispatch_id)&&
                members.some(job=>job.status!=='cancelled'&&!job.capacity_released_at);
            if(canStop)row.append(button('Stop / close bundle',async()=>{
                if(!confirm('Request native Stop for all active bundle members and cancel undelivered Starts? '+
                    'Finished/stopped members with verified native terminal receipts release their reservations. '+
                    'Running or offline members retain their resources until native Stop is verified. '+
                    'Failed or unverified owners may require manual reconciliation. '+
                    'Existing checkpoints, records and other experiments are untouched.'))return;
                try{
                    const result=await action('hub/resource-bundles/stop',{
                        bundle_id:id,expected_job_ids:ids,confirm_stop:true,
                    });
                    feedback('hub-resource-bundle-message',
                        'Bundle shutdown: '+result.stops_requested+
                        ' Stop requests pending, '+result.cancelled+
                        ' unstarted members cancelled, '+result.released+
                        ' capacity claims released'+
                        (result.needs_review.length
                            ?'; '+result.needs_review.length+' members need review.':'.'));
                    await reload();
                }catch(error){
                    feedback('hub-resource-bundle-message',String(error.message||error));
                }
            }));
            const canCancel=members.length===members[0].resource_bundle.size&&
                active.length>0&&active.every(j=>
                    j.status==='waiting_adapter'&&!j.dispatch_id);
            if(canCancel)row.append(button('Cancel all unstarted members',async()=>{
                if(!confirm('Cancel '+active.length+' unstarted bundle members and release their Hub claims? No native process will be stopped.'))return;
                try{
                    const result=await action('hub/resource-bundles/cancel',{
                        bundle_id:id,expected_job_ids:active.map(j=>j.id),
                        confirm_cancel:true,
                    });
                    feedback('hub-resource-bundle-message',
                        'Cancelled '+result.cancelled_count+' unstarted members in bundle '+id.slice(-8)+'.');
                    await reload();
                }catch(error){
                    feedback('hub-resource-bundle-message',String(error.message||error));
                }
            }));
            container.append(row);
        }
    }
    // Only a fresh, authenticated native report can distinguish a Stop
    // still awaiting delivery from a service already shutting down.
    function nativeJobLabel(job, fallback) {
        if(job.status==='start_requested'&&job.start_first_offered_at===null)
            return 'Start authorized; awaiting first worker delivery';
        if(job.status==='start_requested'&&job.automation_start_paused)
            return 'Automatic Start suspended; inspect or request Stop';
        const source=job.evaluation_stop_evidence?'Evaluation':
            job.stop_requested_at?'Operator':null;
        if(job.status==='stop_requested') {
            const prefix=source?source+' Stop':'Stop';
            return job.live_execution?.state==='stop_requested'
                ?prefix+' acknowledged; awaiting exit'
                :prefix+' requested; awaiting worker confirmation';
        }
        if(job.status==='unverified'&&source)
            return source+' Stop pending; native service state unverified';
        if(source&&['finished','failed','stopped'].includes(job.status))
            return fallback+' · '+source+' Stop was requested';
        return fallback;
    }
    function selectTab(next) {
        if (!['design','train','monitor','compare','machines'].includes(next)) return;
        tab=next;
        for(const section of document.querySelectorAll('section[data-hub-tab]')) {
            const scope=section.dataset.project;
            const shouldHide=section.dataset.hubTab!==tab ||
                (scope==='bees' && project!=='bees') ||
                (scope==='other' && project==='bees');
            if(section.hidden!==shouldHide)section.hidden=shouldHide;
        }
        for(const b of document.querySelectorAll('[data-hub-go]')) {
            b.setAttribute('aria-selected',String(b.dataset.hubGo===tab));
        }
        $('bees-controls').hidden=project!=='bees' || !['train','monitor'].includes(tab);
        if(tab==='train'&&project==='bees') loadBeesControl();
        if(tab==='monitor'&&project==='bees') loadMonitorBees();
        if(tab==='compare')loadComparison();
        if(tab==='machines') loadLiveBeesWorkers();
    }
    const compareSlots=[];
    const rolloutRecommendations=document.createElement('div');
    rolloutRecommendations.id='hub-compare-rollout-recommendations';
    $('hub-compare-scaling-note').insertAdjacentElement('afterend',
        rolloutRecommendations);
    const collectorStageProfiles=document.createElement('details');
    collectorStageProfiles.id='hub-compare-collector-stages';
    rolloutRecommendations.insertAdjacentElement('afterend',collectorStageProfiles);
    const secondsMetricsDetails=document.createElement('details');
    secondsMetricsDetails.id='hub-compare-recorded-seconds';
    collectorStageProfiles.insertAdjacentElement('afterend',secondsMetricsDetails);
    const comparisonMetricOverrides=new Map();
    const comparisonChartAverages=new Map();
    const comparisonChartRolling=new Map();
    let lastComparisonChart=null,lastComparisonChartWidth=0;
    let compareSerial=0;
    const compareColor=i=>'hsl('+((i*137.508+211)%360).toFixed(2)+' 65% 38%)';
    function addComparisonSlot() {
        const slot='run'+(++compareSerial);
        compareSlots.push(slot);
        const row=document.createElement('div');
        row.className='hub-compare-run';
        const label=document.createElement('label');
        const select=document.createElement('select');
        select.id='hub-compare-'+slot;
        label.htmlFor=select.id;
        label.textContent='Run '+compareSerial;
        select.addEventListener('change',renderComparison);
        const remove=button('Remove',()=>{
            compareSlots.splice(compareSlots.indexOf(slot),1);
            row.remove();
            renderComparison();
        });
        remove.title='Remove from comparison (does not modify the job)';
        const controls=document.createElement('div');
        controls.className='hub-compare-run-controls';
        controls.append(select,remove);
        row.append(label,controls);
        $('hub-compare-run-list').append(row);
        return slot;
    }
    // Keep the native multi-select as the single source of selected IDs.
    // This searchable panel changes only its options, not the job/export API.
    function renderMetricPicker(selectId,pickerId) {
        const source=$(selectId),panel=$(pickerId);
        if(!source||!panel)return;
        // Frozen Hub jobs accept up to 96 preferred chart metrics.
        // Compare charts are intentionally not subject to this cap.
        const selectionLimit=selectId==='hub-job-metrics'?96:null;
        if(!panel._metricView) {
            const toolbar=document.createElement('div');
            toolbar.className='hub-metric-toolbar';
            const search=document.createElement('input');
            search.type='search';
            search.placeholder='Search metric names or units';
            search.setAttribute('aria-label','Search metrics');
            const defaultsButton=selectId==='hub-job-metrics'
                ?document.createElement('button'):null;
            if(defaultsButton) {
                defaultsButton.type='button';
                defaultsButton.textContent='Use project defaults';
                defaultsButton.addEventListener('click',()=>{
                    jobMetricChoicesByProject.delete(project);
                    renderJobMetricSelection();
                });
            }
            const shownButton=document.createElement('button');
            shownButton.type='button';
            shownButton.textContent='Select shown';
            const clearButton=document.createElement('button');
            clearButton.type='button';
            clearButton.textContent='Clear selection';
            const count=document.createElement('span');
            count.className='hub-metric-count';
            count.setAttribute('aria-live','polite');
            const selectedSummary=document.createElement('div');
            selectedSummary.className='hub-metric-selection-summary';
            selectedSummary.setAttribute('aria-label','Selected metrics');
            const selectedHeading=document.createElement('strong');
            selectedHeading.className='hub-metric-selected-heading';
            const selectedItems=document.createElement('div');
            selectedItems.className='hub-metric-selected-items';
            selectedSummary.append(selectedHeading,selectedItems);
            const rows=document.createElement('div');
            rows.className='hub-metric-options';
            rows.setAttribute('role','group');
            rows.setAttribute('aria-label','Available metrics');
            toolbar.append(search,...(defaultsButton?[defaultsButton]:[]),
                shownButton,clearButton,count);
            panel.append(toolbar,selectedSummary,rows);
            const matches=()=>[...source.options].filter(option=>
                (option.value+' '+option.textContent).toLowerCase()
                    .includes(search.value.trim().toLowerCase()));
            function draw(focusKey=null) {
                const scroll=rows.scrollTop;
                const visible=matches();
                const groups=new Map();
                for(const option of visible) {
                    const group=option.dataset.category||'Other metrics';
                    if(!groups.has(group))groups.set(group,[]);
                    groups.get(group).push(option);
                }
                const nodes=[];
                // Learning outcomes should not be buried below hundreds of
                // hardware/performance counters in the metric picker.
                const priority={'Learning outcomes':0,'Performance and resources':1};
                for(const [group,options] of [...groups].sort(([a],[b])=>
                    (priority[a]??2)-(priority[b]??2)||a.localeCompare(b))) {
                    const heading=document.createElement('div');
                    heading.className='hub-metric-group-title';
                    heading.textContent=group+' · '+options.length;
                    nodes.push(heading);
                    for(const option of options) {
                        const label=document.createElement('label');
                        label.className='hub-metric-choice';
                        const check=document.createElement('input');
                        check.type='checkbox';
                        check.checked=option.selected;
                        check.disabled=!check.checked&&selectionLimit!==null&&
                            source.selectedOptions.length>=selectionLimit;
                        check.dataset.metricValue=option.value;
                        check.title=option.dataset.description||'';
                        label.title=check.title;
                        const key=option.value.replace(/^metric:/,'');
                        const name=document.createElement('span');
                        name.textContent=key;
                        const note=document.createElement('small');
                        note.textContent=option.textContent.slice(key.length).trim();
                        check.addEventListener('change',()=>{
                            if(check.checked&&selectionLimit!==null&&
                                source.selectedOptions.length>=selectionLimit) {
                                check.checked=false;
                                draw(option.value);
                                return;
                            }
                            option.selected=check.checked;
                            source.dispatchEvent(new Event('change',{bubbles:true}));
                            draw(option.value);
                        });
                        label.append(check,name,note);
                        nodes.push(label);
                    }
                }
                if(!visible.length) {
                    const empty=document.createElement('div');
                    empty.className='hub-metric-empty';
                    empty.textContent=source.options.length
                        ?'No metrics match this search.'
                        :'No metrics have been recorded for this project yet.';
                    nodes.push(empty);
                }
                rows.replaceChildren(...nodes);
                rows.scrollTop=scroll;
                const chosen=[...source.selectedOptions];
                selectedHeading.textContent='Selected metrics ('+chosen.length+')';
                selectedItems.replaceChildren(...(chosen.length?chosen.map(option=>{
                    const metric=option.value.replace(/^metric:/,'');
                    const remove=document.createElement('button');
                    remove.type='button';
                    remove.className='hub-metric-selected-chip';
                    remove.textContent=metric+' ×';
                    remove.setAttribute('aria-label','Remove '+metric+' from selected metrics');
                    remove.title=(option.dataset.description||'')+
                        '\nClick to remove '+metric;
                    remove.addEventListener('click',()=>{
                        option.selected=false;
                        source.dispatchEvent(new Event('change',{bubbles:true}));
                        draw();
                    });
                    return remove;
                }):[document.createTextNode('None selected')]));
                count.textContent=source.selectedOptions.length+
                    (selectionLimit===null?'':' / '+selectionLimit)+
                    ' selected · '+visible.length+' of '+
                    source.options.length+' shown';
                if(focusKey!==null)
                    for(const check of rows.querySelectorAll('input[type="checkbox"]'))
                        if(check.dataset.metricValue===focusKey) {
                            check.focus({preventScroll:true});
                            break;
                        }
            }
            function toggle(checked,visibleOnly) {
                let available=selectionLimit===null?Infinity:
                    Math.max(0,selectionLimit-source.selectedOptions.length);
                for(const option of visibleOnly?matches():[...source.options]) {
                    if(!checked)option.selected=false;
                    else if(!option.selected&&available>0) {
                        option.selected=true;
                        available--;
                    }
                }
                source.dispatchEvent(new Event('change',{bubbles:true}));
                draw();
            }
            search.addEventListener('input',()=>{rows.scrollTop=0;draw();});
            shownButton.addEventListener('click',()=>toggle(true,true));
            clearButton.addEventListener('click',()=>toggle(false,false));
            panel._metricView={search,draw};
        }
        if(panel.dataset.metricProject!==project) {
            panel.dataset.metricProject=project;
            panel._metricView.search.value='';
        }
        panel._metricView.draw();
        source.classList.add('is-enhanced');
    }
    function selectedComparisonMetrics() {
        return [...$('hub-compare-metric').selectedOptions].map(option=>option.value);
    }
    const chartAxes={
        steps_completed:{label:'Cumulative project steps'},
        elapsed_training_seconds:{label:'Reported elapsed training seconds'},
        active_training_time:{label:'Reported active training seconds'},
    };
    async function loadComparison() {
        if(!state)return;
        const requested=project,serial=++comparisonRequest;
        try {
            const response=await fetch('/api/hub/progress?project_id='+
                encodeURIComponent(requested),{cache:'no-store'});
            const body=await response.json();
            if(!response.ok)throw Error(body.error||'Progress series unavailable');
            if(serial!==comparisonRequest||requested!==project)return;
            comparisonData=body;
            comparisonProject=requested;
            comparisonError=null;
            refreshComparisonKeepingViewport();
        }catch(error){
            if(serial!==comparisonRequest||requested!==project)return;
            comparisonData=null;
            comparisonProject=requested;
            comparisonError=String(error.message||error);
            refreshComparisonKeepingViewport();
        }
    }
    // Automatic comparison updates replace several panels and charts. Keep
    // the currently visible metric at the same viewport position rather than
    // allowing browser scroll anchoring to jump back to the first chart.
    function withCompareViewport(update) {
        const plots=$('hub-compare-plots');
        const sections=[...plots.querySelectorAll('.hub-progress-plot')];
        const visible=sections.find(section=>{
            const rect=section.getBoundingClientRect();
            return rect.bottom>0&&rect.top<window.innerHeight;
        });
        const anchorKey=visible?.dataset.metric;
        const anchorTop=visible?.getBoundingClientRect().top;
        const beforeScroll=window.scrollY;
        update();
        const replacement=anchorKey?[...plots.querySelectorAll('.hub-progress-plot')]
            .find(section=>section.dataset.metric===anchorKey):null;
        if(replacement&&Number.isFinite(anchorTop)) {
            window.scrollBy(0,replacement.getBoundingClientRect().top-anchorTop);
        }else if(window.scrollY!==beforeScroll)window.scrollTo(0,beforeScroll);
    }
    function refreshComparisonKeepingViewport() {
        withCompareViewport(renderComparison);
    }
    function comparisonAxisLabel(key) {
        return key==='steps_completed'&&project==='tinybrains'
            ?'Cumulative TinyBrains updates':chartAxes[key]?.label;
    }
    function renderComparison() {
        if(!state)return;
        const jobs=project==='bees'?(state.bees?.runs||[]):
            state.jobs.filter(j=>j.project_id===project);
        const samples=comparisonProject===project&&comparisonData?.samples||[];
        $('hub-compare-selection-panel').hidden=project==='bees';
        const history=new Map(samples.map(s=>[s.job_id,s.points||[]]));
        const hasSamples=j=>j.live_execution?.progress||j.final_progress||history.get(j.id)?.length;
        const selectable=jobs.filter(j=>hasSamples(j)||hubComparisonEvaluation(j)||
            hubComparisonTerminal(j)||['waiting_adapter','waiting_predecessor',
                'start_requested','launch_requested','running','stop_requested']
                .includes(j.status)).reverse();
        const chartable=selectable.filter(hasSamples);
        const defaults=[...chartable,...selectable.filter(j=>!hasSamples(j))];
        const previous=compareSlots.map(slot=>$('hub-compare-'+slot).value);
        const changedProject=selectionProject!==project;
        if(changedProject)comparisonDefaultsApplied=false;
        selectionProject=project;
        const chooseDefaults=!comparisonDefaultsApplied&&defaults.length>0;
        const selected=new Set();
        for(let i=0;i<compareSlots.length;i++) {
            const select=$('hub-compare-'+compareSlots[i]);
            select.replaceChildren(new Option('No run',''),
                ...selectable.map(j=>new Option(
                    hubJobLabel(j)+
                    trialBatchLabel(j)+
                    ' · job '+(jobs.indexOf(j)+1)+
                    (j.continuation_of_job_id?' · continuation':' · initial run')+
                    ' · '+j.status+
                    ' · '+new Date(j.created_at).toLocaleDateString(),j.id)));
            const desired=chooseDefaults&&i<2
                ?defaults[i]?.id:(!changedProject?previous[i]:null);
            if(desired&&selectable.some(j=>j.id===desired)&&
                !selected.has(desired)){
                select.value=desired;
                selected.add(desired);
            }
        }
        if(chooseDefaults)comparisonDefaultsApplied=true;
        const configs=(state.configurations||[]).filter(c=>c.project_id===project);
        const configSelect=$('hub-compare-selection-config'),previousConfig=configSelect.value;
        configSelect.replaceChildren(...configs.map(c=>
            new Option(c.name+' · revision '+c.revision,c.id)));
        if(configs.some(c=>c.id===previousConfig))
            configSelect.value=previousConfig;
        const chosenIds=compareSlots.map(slot=>$('hub-compare-'+slot).value)
            .filter(Boolean);
        const candidateKey=JSON.stringify(chosenIds);
        if(candidateKey!==previewCandidateKey) {
            feedback('hub-compare-selection-ranking','');
            previewCandidateKey=candidateKey;
        }
        const readyForRanking=chosenIds.length>=2&&chosenIds.every(id=>{
            const candidate=jobs.find(j=>j.id===id);
            return candidate&&['finished','stopped'].includes(candidate.status)&&
                candidate.capacity_released_at;
        });
        $('hub-compare-selection-preview').disabled=!readyForRanking||
            new Set(chosenIds).size!==chosenIds.length||
            !['courier','tinybrains'].includes(project);
        $('hub-compare-selection-queue').textContent=readyForRanking
            ?'Queue best evaluated continuation':'Schedule candidate selection';
        $('hub-compare-selection-queue').disabled=chosenIds.length<2||
            new Set(chosenIds).size!==chosenIds.length||!configs.length;
        const allPlans=(state.candidate_selections||[])
            .filter(p=>p.project_id===project);
        const plans=allPlans.slice(-5).reverse();
        const cancelSelect=$('hub-compare-selection-cancel-id');
        const oldPlan=cancelSelect.value;
        const cancellable=allPlans.filter(p=>
            ['waiting','needs_review'].includes(p.status)).reverse();
        cancelSelect.replaceChildren(...cancellable.map(p=>
            new Option(p.status+' · '+p.id.slice(-8),p.id)));
        if(cancellable.some(p=>p.id===oldPlan))
            cancelSelect.value=oldPlan;
        $('hub-compare-selection-cancel').disabled=!cancellable.length;
        $('hub-compare-selection-review').disabled=!cancellable.some(p=>
            p.id===cancelSelect.value&&p.status==='needs_review');
        feedback('hub-compare-selection-status',plans.length
            ?plans.map(p=>(p.status==='queued'
                    ?'Queued winner '+p.winner_job_id?.slice(-8)+
                        ' · next job '+p.promoted_job_id?.slice(-8)
                    :p.status==='needs_review'
                        ?'Needs review: '+(p.error||'unresolved model identity')
                        :'Awaiting candidate completion or final checkpoint receipts: '+p.candidate_job_ids.length+
                            ' runs')+
                    ' · plan '+p.id.slice(-8)).join(' | ')
            :'No candidate selection scheduled.');
        const runs=compareSlots.map((slot,i)=>{
            const id=$('hub-compare-'+slot).value;
            const job=jobs.find(j=>j.id===id);
            return job?{job,color:compareColor(i),slot:String(i+1),
                points:history.get(id)||[]}:null;
        }).filter(Boolean);
        const metricSelect=$('hub-compare-metric');
        const oldMetrics=metricSelect.dataset.metricProject===project
            ?selectedComparisonMetrics():[];
        const metricOptions=hubGenericMetricCatalog(chartable.map(job=>({
            job,points:history.get(job.id)||[],
        }))).map(metric=>({
            key:'metric:'+metric.key,
            label:metric.key+' ('+metric.units.join('/')+')',
            units:metric.units,
        }));
        metricSelect.replaceChildren(...metricOptions.map(m=>{
            const option=new Option(m.label,m.key);
            const key=m.key.slice(7);
            option.dataset.description=hubMetricDescription(key,m.units);
            option.title=option.dataset.description;
            option.dataset.category=knownLearningMetrics.includes(key)
                ?'Learning outcomes':knownPerformanceMetrics.includes(key)
                    ?'Performance and resources':'Other recorded metrics';
            return option;
        }));
        const keep=oldMetrics.filter(key=>metricOptions.some(m=>m.key===key));
        const preferred=[...new Set(runs.flatMap(run=>run.job.metric_selection||[]))]
            .map(key=>'metric:'+key)
            .filter(key=>metricOptions.some(m=>m.key===key));
        // On a new project view, show actual learning outcomes before the
        // alphabetically first resource counter. Explicit user choices win.
        const learningDefaults=['selection_candidate_survival_score',
            'selection_incumbent_survival_score','training_win_rate','training_score_rate',
            'training_mean_score','loss','entropy',
            'ant_decisions_per_second'].map(name=>'metric:'+name)
            .filter(key=>metricOptions.some(option=>option.key===key));
        const metricChoices=comparisonMetricOverrides.get(project) ??
            (keep.length?keep:preferred.length?preferred:
                learningDefaults.length?learningDefaults.slice(0,5):
                    [metricOptions[0]?.key]);
        for(const option of metricSelect.options)
            option.selected=metricChoices.includes(option.value);
        metricSelect.dataset.metricProject=project;
        renderMetricPicker('hub-compare-metric','hub-compare-metric-picker');
        const selectedMetrics=selectedComparisonMetrics();
        const axisSelect=$('hub-compare-x-axis'),oldAxis=axisSelect.value;
        const timeAxes=['elapsed_training_seconds','active_training_time'];
        const availableTimeAxes=timeAxes.filter(key=>chartable.some(job=>
            hubComparisonPoints({job,points:history.get(job.id)||[]})
                .some(point=>hubComparisonAxisValue(point,key)!==null)));
        axisSelect.replaceChildren(
            new Option(comparisonAxisLabel('steps_completed'),'steps_completed'),
            ...availableTimeAxes.map(key=>new Option(comparisonAxisLabel(key),key)));
        axisSelect.value=[...axisSelect.options].some(option=>
            option.value===oldAxis)?oldAxis:'steps_completed';
        const hasExportData=runs.some(run=>hubComparisonPoints(run).length);
        const legacyBeesCoordinator=project==='bees'&&
            !document.documentElement.dataset.hubDefaultProject;
        $('hub-compare-export-full').disabled=!runs.length||
            (project==='bees'&&!legacyBeesCoordinator);
        $('hub-compare-export-csv').disabled=!hasExportData||!selectedMetrics.length;
        $('hub-compare-export-json').disabled=!hasExportData&&!runs.some(run=>
            hubComparisonTerminal(run.job)||hubComparisonEvaluation(run.job));
        const note=comparisonError&&comparisonProject===project
            ?'Unable to load comparison history: '+comparisonError
            :comparisonProject!==project
                ?'Loading bounded, in-memory progress history...'
                :comparisonData?.storage_warning
                    ?comparisonData.storage_warning+' Live chart samples remain available; saved history may be incomplete.'
                    :comparisonData?.ephemeral
                        ?'Live chart samples are held in memory only. Final summaries remain available.'
                        :'Bounded chart history is saved separately from the training registry. For detailed investigation export the complete project-owned job history.';
        feedback('hub-compare-notice',
            (state?.hub_observation_archive_issue
                ?state.hub_observation_archive_issue+' '
                :'')+note);
        const knownRoots=new Set(runs.map(r=>
            r.job.artifact_lineage_root_id||r.job.id));
        const warnings=[];
        if(knownRoots.size<runs.length)
            warnings.push('Some selected jobs share checkpoint/artifact lineage, so they are not independent replicates.');
        if(runs.length>1) {
            const fingerprints=runs.map(r=>r.job.configuration_sha256);
            if(fingerprints.some(h=>typeof h!=='string'||h.length!==64))
                warnings.push('Frozen configuration fingerprints are unavailable for one or more runs.');
            else if(new Set(fingerprints).size>1) {
                // Show field names rather than JSON parameter values: enough
                // to explain a discrepancy without filling the chart with
                // long training configurations or experimental secrets.
                const configurations=runs.map(r=>r.job.frozen_parameters||{});
                const keys=[...new Set(configurations.flatMap(Object.keys))].sort();
                const different=keys.filter(key=>{
                    const first=JSON.stringify(configurations[0][key]);
                    return configurations.slice(1).some(c=>
                        JSON.stringify(c[key])!==first);
                });
                warnings.push('Different frozen configurations'+
                    (different.length?'; changed top-level fields: '+
                        different.slice(0,6).join(', ')+
                        (different.length>6?' and '+(different.length-6)+' more':''):'')+
                    '. Do not treat these as controlled replicates.');
            }else warnings.push('Frozen configurations match; this alone does not establish comparable evaluation cohorts or seeds.');
            const hostIds=runs.map(r=>r.job.assigned_machine_id).filter(Boolean);
            if(new Set(hostIds).size>1)
                warnings.push('Runs used different assigned machines; performance measurements may not be comparable.');
        }
        feedback('hub-compare-chart-note',
            warnings.join(' ')+(warnings.length?' ':'')+
            'Horizontal axis: '+(comparisonAxisLabel($('hub-compare-x-axis').value)||
                chartAxes.steps_completed.label)+'. '+
            ($('hub-compare-x-axis').value==='elapsed_training_seconds'
                ?'Elapsed time comes from recorded trainer seconds, not the Hub heartbeat clock. Runs without this measurement are omitted from the time plots. '
                :$('hub-compare-x-axis').value==='active_training_time'
                    ?'Active training time comes from recorded trainer seconds, which may exclude pauses; it is not the Hub heartbeat clock. Runs without this measurement are omitted from these plots. '
                    :'Project step counts are not wall-clock time. ')+
            'Source timestamps show when a trusted adapter published a summary. No training decisions use chart data.');
        const table=$('hub-compare-rows');
        table.replaceChildren();
        const headings=[...table.closest('table').querySelectorAll('thead th')];
        const labels=['Run','Status / host','Project steps','Selected metrics',
            'Reported metrics','Segment','Latest summary'];
        labels.forEach((label,i)=>{if(headings[i])headings[i].textContent=label;});
        for(const run of runs) {
            const j=run.job,p=j.live_execution?.progress||j.final_progress||
                run.points[run.points.length-1]||null;
            const tr=document.createElement('tr');
            cell(tr,run.slot+' · '+hubJobLabel(j)+
                trialBatchLabel(j)+
                (j.continuation_of_job_id?' (continuation)':''));
            cell(tr,j.status+' · '+(j.assigned_machine_id||'unassigned')+
                (j.execution_diagnostic?' · '+j.execution_diagnostic:''));
            cell(tr,p?.steps_completed?.toLocaleString()||'—');
            cell(tr,selectedMetrics.map(key=>{
                const metric=p?.metrics?.[key.slice(7)];
                return metric?key.slice(7)+': '+metric.value.toLocaleString()+' '+metric.unit:null;
            }).filter(Boolean).join(' · ')||'—');
            cell(tr,Object.keys(p?.metrics||{}).join(', ')||'—');
            cell(tr,p?.segment_id||'—');
            cell(tr,p?.source_modified_at
                ?new Date(p.source_modified_at).toLocaleString():'—');
            table.append(tr);
        }
        if(!runs.length) {
            const tr=document.createElement('tr');
            cell(tr,selectable.length?'Select a run above to compare or export.':
                'No training samples, evaluation receipts or terminal jobs for this project.').colSpan=7;
            table.append(tr);
        }
        renderComparisonEvaluations(runs);
        renderScalingEvidence(runs,selectedMetrics);
        renderRolloutRecommendations(runs);
        renderCollectorStageProfiles(runs);
        renderMeasuredSeconds(runs);
        drawComparisonCharts(runs,selectedMetrics,
            $('hub-compare-x-axis').value);
    }
    function renderScalingEvidence(runs,keys) {
        const table=$('hub-compare-scaling-rows');
        table.replaceChildren();
        // Rates are the only sensible common unit for marginal throughput.
        // Time metrics, CPU/GPU busy percentages and losses are not throughput.
        const available=[...new Set(runs.flatMap(run=>
            hubComparisonPoints(run).flatMap(point=>
                Object.entries(point.metrics||{})
                    .filter(([,value])=>value.unit==='rate')
                    .map(([name])=>'metric:'+name))))];
        const key=keys.find(value=>available.includes(value))||null;
        const rows=hubScalingEvidence(runs,key,state?.jobs||[],
            state?.reservations||[]);
        feedback('hub-compare-scaling-note',key
            ?'Showing '+key.slice(7)+'. Medians use bounded chart samples from each run’s latest segment, not complete histories. '+
              'CPU/GPU values are allocations, not measured utilization. Distributed TinyBrains groups sum only their explicitly reserved learner and remote worker resources; the remote threads/CPU column shows frozen collector concurrency, not actual CPU occupancy. Marginal comparisons require five samples per job, completed jobs, verified matching resource leases, identical per-host RAM and shared GPU budgets, fixed machine group/configuration, physical GPU identity, and one increased CPU or GPU dimension. '+
              'They remain observational, not guaranteed improvements or independent learning evaluations.'
            :'Select a recorded throughput metric (unit: rate) above to inspect resource scaling. No CPU/GPU efficiency can be inferred from utilization alone.');
        for(const row of rows) {
            const tr=document.createElement('tr');
            cell(tr,row.slot+' · '+row.job_id.slice(-8));
            cell(tr,row.cpus===null?'Unknown':row.cpus.toLocaleString()+
                (row.distributed?' (group allocation)':''));
            cell(tr,row.gpus===null?'Unknown':row.gpus.toLocaleString());
            cell(tr,row.host||'Unknown');
            cell(tr,!row.distributed?'—':
                row.collector_threads===null?'Unknown':
                    row.collector_threads+' / '+row.collector_cpus);
            cell(tr,String(row.sample_count));
            cell(tr,row.median_rate===null?'—':
                row.median_rate.toLocaleString(undefined,{maximumFractionDigits:2}));
            cell(tr,row.cpus&&row.median_rate!==null
                ?(row.median_rate/row.cpus).toLocaleString(undefined,{
                    maximumFractionDigits:2}):'—');
            const diff=row.comparison;
            cell(tr,diff
                ?'Compared with '+diff.baseline_slot+': '+
                    (diff.marginal_rate>=0?'+':'')+
                    diff.marginal_rate.toLocaleString(undefined,{
                        maximumFractionDigits:2})+
                    ' rate units per added '+diff.resource+
                    (diff.shared_lineage?' (same checkpoint lineage)':'')
                :'Not comparable');
            table.append(tr);
        }
        if(!rows.length) {
            const tr=document.createElement('tr');
            cell(tr,runs.length
                ?'No recorded rate samples for the selected runs and metric.'
                :'Select one or more runs to review observed scaling.').colSpan=9;
            table.append(tr);
        }
    }

    function renderCollectorStageProfiles(runs) {
        collectorStageProfiles.replaceChildren();
        const profiles=project==='tinybrains'
            ?runs.map(run=>({run,profile:hubCollectorStageProfile(run)}))
                .filter(item=>item.profile):[];
        collectorStageProfiles.hidden=!profiles.length;
        if(!profiles.length)return;
        const summary=document.createElement('summary');
        summary.textContent='Per-collector rollout stage timings ('+
            profiles.length+' run'+(profiles.length===1?'':'s')+')';
        collectorStageProfiles.append(summary);
        const format=value=>value===null?'—':
            value.toLocaleString(undefined,{maximumFractionDigits:2});
        for(const {run,profile} of profiles) {
            const heading=document.createElement('p');
            heading.textContent='Run '+run.slot+' · '+profile.job_id.slice(-8)+
                ' · '+profile.slots.length+'/'+profile.collector_count+
                ' collectors with at least five complete recent samples';
            collectorStageProfiles.append(heading);
            const table=document.createElement('table');
            const headers=['Collector','Samples','Decisions/s',
                'Engine step (s)','Inference (s)','Model prepare (s)',
                'Roundtrip (s)'];
            const tr=document.createElement('tr');
            for(const title of headers) {
                const th=document.createElement('th');
                th.textContent=title;
                tr.append(th);
            }
            const thead=document.createElement('thead');
            thead.append(tr);
            const tbody=document.createElement('tbody');
            for(const slot of profile.slots) {
                const row=document.createElement('tr');
                for(const value of [
                    slot.slot===0?'0 · learner':' '+slot.slot+' · remote',
                    String(slot.samples),format(slot.rate),format(slot.engine),
                    format(slot.inference),format(slot.model),
                    slot.slot===0?'—':format(slot.roundtrip),
                ])cell(row,value);
                tbody.append(row);
            }
            table.append(thead,tbody);
            collectorStageProfiles.append(table);
        }
        const note=document.createElement('p');
        note.textContent='Medians use up to 32 paired observations from the latest chart segment. Engine and inference timings are host-wall stage observations, not exclusive CPU or GPU time; stages may overlap. Remote roundtrip includes transfer and worker computation, not network latency alone. Missing columns mean the recorded samples did not contain the metric consistently; no values are inferred.';
        collectorStageProfiles.append(note);
    }
    function renderMeasuredSeconds(runs) {
        secondsMetricsDetails.replaceChildren();
        const profiles=runs.map(run=>({run,metrics:hubMeasuredSeconds(run)}))
            .filter(item=>item.metrics.length);
        secondsMetricsDetails.hidden=!profiles.length;
        if(!profiles.length)return;
        const heading=document.createElement('summary');
        heading.textContent='Recorded time metrics ('+profiles.length+
            ' run'+(profiles.length===1?'':'s')+', all projects)';
        secondsMetricsDetails.append(heading);
        const format=value=>value.toLocaleString(undefined,{
            maximumFractionDigits:4});
        for(const {run,metrics} of profiles) {
            const caption=document.createElement('p');
            caption.textContent='Run '+run.slot+' · '+run.job.id.slice(-8)+
                ' · top '+metrics.length+' metrics by recorded median';
            secondsMetricsDetails.append(caption);
            const table=document.createElement('table');
            const header=document.createElement('tr');
            for(const title of ['Metric','Samples','Median (s)',
                'Minimum (s)','Maximum (s)']) {
                const th=document.createElement('th');
                th.textContent=title;
                header.append(th);
            }
            const thead=document.createElement('thead');
            thead.append(header);
            const tbody=document.createElement('tbody');
            for(const metric of metrics) {
                const tr=document.createElement('tr');
                for(const item of [metric.key,String(metric.samples),
                    format(metric.median),format(metric.minimum),
                    format(metric.maximum)])cell(tr,item);
                tbody.append(tr);
            }
            table.append(thead,tbody);
            secondsMetricsDetails.append(table);
        }
        const note=document.createElement('p');
        note.textContent='Read-only medians from up to 64 observations in each run’s latest contiguous chart segment, with at least five samples per metric. Second- and millisecond-valued measurements are normalized to seconds. Known cumulative elapsed_training_seconds and active_training_time clocks are omitted from this ranking but remain chartable and exportable. Other duration metrics may be cumulative; overlapping stages cannot be added. These chart samples are not full-resolution profiling evidence. Export the histories for definitive bottleneck analysis.';
        secondsMetricsDetails.append(note);
    }
    function renderRolloutRecommendations(runs) {
        rolloutRecommendations.replaceChildren();
        rolloutRecommendations.hidden=project!=='tinybrains';
        if(project!=='tinybrains')return;
        const heading=document.createElement('strong');
        heading.textContent='Next-run distributed rollout action weights';
        rolloutRecommendations.append(heading);
        const suggestions=runs.map(run=>({
            run,recommendation:hubRolloutWeightRecommendation(run),
        })).filter(item=>item.recommendation);
        if(!suggestions.length) {
            const note=document.createElement('p');
            note.textContent='Select a finished or stopped distributed TinyBrains learner with at least five complete paired collector throughput and action-target observations in its latest segment. No weights are inferred from utilization or missing samples.';
            rolloutRecommendations.append(note);
            return;
        }
        for(const {run,recommendation:r} of suggestions) {
            const entry=document.createElement('p');
            const format=value=>value.toLocaleString(undefined,{maximumFractionDigits:1});
            entry.textContent='Run '+run.slot+' · '+r.job_id.slice(-8)+
                ' · '+r.samples+' paired updates · observed decisions/s ['+
                r.rates.map(format).join(', ')+'] · current weights ['+
                r.current.join(', ')+'] · proposed ['+r.weights.join(', ')+']. ';
            const action=button('Use proposed weights in Design',()=>{
                const key=r.profile+':'+r.profile_sha256;
                if(![...tinyBrainsSelect.options].some(option=>option.value===key)) {
                    feedback('hub-compare-scaling-note',
                        'The original learner profile is not available in Design. Review the printed weights and the current approved machines manually.');
                    return;
                }
                tinyBrainsSelect.value=key;
                tinyBrainsRemote.checked=true;
                tinyBrainsWeights.value=r.weights.join(',');
                selectTab('design');
                feedback('hub-config-message',
                    'Proposed weights copied to the Design input only. Click Use profile and update count, review the entire draft (including approved remote count and checkpoint lineage), and Save. No job or reservation was changed.');
            });
            entry.append(action);
            rolloutRecommendations.append(entry);
        }
        const disclaimer=document.createElement('p');
        disclaimer.textContent='Advisory starting estimates only: recent delivered rates depend on the previous work shares, network and workload. The suggested weights do not change CPU/GPU leases, optimize the live job, or certify higher PPO steps/s. Validate with a new fixed-allocation run.';
        rolloutRecommendations.append(disclaimer);
    }
    function renderComparisonEvaluations(runs) {
        const table=$('hub-compare-evaluation-rows');
        table.replaceChildren();
        const suites=new Set(),digestBySuite=new Map(),trialsBySuite=new Map();
        let missingDigest=false,differentDigests=false,differentTrialCounts=false;
        for(const {job,slot} of runs) {
            const rules=job.evaluation_stop?.all_of||[job.evaluation_stop].filter(Boolean);
            const passed=job.evaluation_stop_evidence?.all_of||null;
            const cohort=job.evaluation_candidates?.[0]?.receipts||{};
            const finalCohort=hubFinalEvaluationCohort(job);
            const entries=rules.length?rules:[null];
            for(const [index,rule] of entries.entries()) {
                const finalReceipt=rule&&finalCohort?.[rule.suite_id];
                const evidence=finalReceipt||(passed?passed[index]:
                    rule?cohort[rule.suite_id]||
                        (!job.evaluation_stop?.all_of?job.evaluation_stop_evidence:null)||
                        job.last_evaluation:
                        job.evaluation_stop_evidence||job.last_evaluation);
                const row=document.createElement('tr');
                cell(row,slot+' · '+job.configuration_name);
                cell(row,evidence?.suite_id||rule?.suite_id||'—');
                cell(row,evidence?.suite_sha256||rule?.suite_sha256||'—')
                    .classList.add('hub-checkpoint-digest');
                cell(row,evidence?evidence.successes.toLocaleString()+' / '+
                    evidence.trials.toLocaleString():'—');
                cell(row,evidence?(evidence.successes/evidence.trials)
                    .toLocaleString(undefined,{style:'percent',maximumFractionDigits:2}):'—');
                cell(row,evidence?.checkpoint_sha256||'—')
                    .classList.add('hub-checkpoint-digest');
                const meets=Boolean(evidence&&rule&&
                    evidence.trials>=rule.min_trials&&
                    evidence.successes/evidence.trials>=rule.min_success_rate);
                const final=Boolean(evidence&&evidence.checkpoint_sha256===job.checkpoint_model_sha256);
                const outcome=final?(meets?'Final checkpoint · suite target met':'Final checkpoint · below target'):
                    job.evaluation_stop_evidence?'All stop targets met':
                    evidence?(meets?'This suite passed; awaiting remaining gates':
                        'Target not met') :
                    rule?'Awaiting receipt':'No evaluation configured';
                cell(row,!final&&evidence&&job.checkpoint_model_sha256?'Earlier checkpoint · '+outcome:outcome);
                table.append(row);
                if(evidence) {
                    suites.add(evidence.suite_id);
                    if(trialsBySuite.has(evidence.suite_id)&&
                        trialsBySuite.get(evidence.suite_id)!==evidence.trials)
                        differentTrialCounts=true;
                    trialsBySuite.set(evidence.suite_id,evidence.trials);
                    if(evidence.suite_sha256) {
                        if(digestBySuite.has(evidence.suite_id)&&
                            digestBySuite.get(evidence.suite_id)!==evidence.suite_sha256)
                            differentDigests=true;
                        digestBySuite.set(evidence.suite_id,evidence.suite_sha256);
                    }else missingDigest=true;
                }
            }
        }
        if(!runs.length) {
            const row=document.createElement('tr');
            cell(row,'Select runs to inspect their evaluation receipts.').colSpan=7;
            table.append(row);
        }
        feedback('hub-compare-evaluation-note',[
            suites.size>1?'Selected receipts use different suites. Compare rates within the same suite.':'',
            differentDigests?'Selected receipts for the same suite use different definitions.':'',
            differentTrialCounts?'Selected receipts for the same suite have different trial counts.':'',
            missingDigest?'Some receipts lack a suite fingerprint. A shared suite ID cannot verify matching scenarios.':'',
        ].filter(Boolean).join(' '));
    }
    function drawComparisonCharts(runs,keys,xKey) {
        const container=$('hub-compare-plots');
        lastComparisonChart={runs,keys,xKey};
        lastComparisonChartWidth=container.clientWidth;
        container.replaceChildren();
        if(!runs.length||!keys.length) {
            const notice=document.createElement('p');
            notice.className='hub-notice';
            notice.textContent=!runs.length
                ?'Select a run above to display recorded metric charts.'
                :$('hub-compare-metric').options.length
                    ?'No chart metrics are selected. Check one or more metrics above to display a graph.'
                    :'No recorded chart metrics are available for this project yet.';
            container.append(notice);
            return;
        }
        for(const key of keys) {
            const section=document.createElement('div');
            section.className='hub-progress-plot';
            section.dataset.metric=key;
            const title=document.createElement('h3');
            title.textContent=key.slice(7);
            const header=document.createElement('div');
            header.className='hub-plot-header';
            const toggles=document.createElement('div');
            toggles.className='hub-plot-toggles';
            const averageLabel=document.createElement('label');
            averageLabel.className='hub-plot-average-toggle';
            averageLabel.title='Arithmetic mean of retained chart samples for each run, not weighted by elapsed time or steps.';
            const average=document.createElement('input');
            average.type='checkbox';
            const averageId=project+'|'+key;
            average.checked=comparisonChartAverages.get(averageId)===true;
            averageLabel.append(average,document.createTextNode(' Show sample mean'));
            const rollingLabel=document.createElement('label');
            rollingLabel.className='hub-plot-average-toggle';
            rollingLabel.title='Mean of available retained chart samples within the preceding 50 project updates/rounds. Chart downsampling means this is not a mean of every raw update.';
            const rolling=document.createElement('input');
            rolling.type='checkbox';
            const rollingId=project+'|'+key;
            rolling.checked=comparisonChartRolling.get(rollingId)===true;
            rollingLabel.append(rolling,document.createTextNode(
                project==='tinybrains'?' Rolling 50-update mean':
                project==='courier'?' Rolling 50-round mean':' Rolling 50-step mean'));
            toggles.append(averageLabel,rollingLabel);
            header.append(title,toggles);
            const description=document.createElement('p');
            description.className='hub-plot-description';
            description.textContent=hubMetricDescription(key.slice(7));
            const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
            svg.setAttribute('role','img');
            svg.setAttribute('preserveAspectRatio','xMinYMin meet');
            svg.style.width='100%';
            svg.style.height='330px';
            const legend=document.createElement('div');
            legend.className='hub-plot-legend';
            const tooltip=document.createElement('div');
            tooltip.className='hub-plot-tooltip';
            tooltip.hidden=true;
            section.append(header,description,svg,tooltip,legend);
            container.append(section);
            const redraw=()=>drawComparisonChart(runs,key,xKey,svg,
                legend,tooltip,average.checked,rolling.checked);
            average.addEventListener('change',()=>{
                comparisonChartAverages.set(averageId,average.checked);
                redraw();
            });
            rolling.addEventListener('change',()=>{
                comparisonChartRolling.set(rollingId,rolling.checked);
                redraw();
            });
            redraw();
        }
    }
    function drawComparisonChart(runs,key,xKey,svg,legend,tooltip,showAverage,showRolling) {
        const width=Math.max(320,Math.round(svg.getBoundingClientRect().width||
            svg.clientWidth||svg.parentElement?.clientWidth||900));
        svg.setAttribute('viewBox','0 0 '+width+' 330');
        legend.replaceChildren();
        tooltip.hidden=true;
        svg.onpointermove=null;
        svg.onpointerleave=null;
        const axisLabel=comparisonAxisLabel(xKey);
        const genericKey=key.startsWith('metric:')?key.slice(7):null;
        const units=genericKey?[...new Set(runs.flatMap(run=>
            hubComparisonPoints(run).map(point=>
                point.metrics?.[genericKey]?.unit).filter(Boolean)))]:[];
        const metric=genericKey?{
            label:genericKey+' ('+(units.join('/')||'unknown')+')',
            rate:units.length===1&&units[0]==='ratio',
        }:null;
        svg.replaceChildren();
        if(!metric||!axisLabel)return;
        const ns='http://www.w3.org/2000/svg';
        const node=(tag,attrs={},content=null)=>{
            const item=document.createElementNS(ns,tag);
            for(const [name,value] of Object.entries(attrs))
                item.setAttribute(name,String(value));
            if(content!=null)item.textContent=String(content);
            svg.append(item);
            return item;
        };
        if(units.length>1) {
            node('text',{x:width/2,y:160,'text-anchor':'middle'},
                'Selected metric uses incompatible units across runs');
            return;
        }
        svg.setAttribute('aria-label',metric.label+
            ' by '+axisLabel.toLowerCase()+' for selected runs');
        const usable=p=>hubComparisonAxisValue(p,xKey)!==null&&
            Number.isFinite(hubMetricValue(p,key));
        const series=runs.map(run=>({
            ...run,valid:hubComparisonPoints(run),
        }));
        const values=series.flatMap(r=>r.valid
            .filter(usable)
            .map(p=>({x:hubComparisonAxisValue(p,xKey),y:hubMetricValue(p,key)})));
        if(!values.length) {
            node('text',{x:width/2,y:160,'text-anchor':'middle'},
                'No recorded samples for the selected runs and metric');
            return;
        }
        const bounds={left:85,right:width-24,top:20,bottom:270};
        let xmin=Infinity,xmax=-Infinity,minY=0,maxY=0.001;
        for(const point of values) {
            xmin=Math.min(xmin,point.x);xmax=Math.max(xmax,point.x);
            minY=Math.min(minY,point.y);maxY=Math.max(maxY,point.y);
        }
        const ymin=metric.rate?0:minY*1.05;
        const ymax=metric.rate?1:maxY*1.05;
        const x=v=>bounds.left+(v-xmin)/Math.max(1,xmax-xmin)*
            (bounds.right-bounds.left);
        const y=v=>bounds.bottom-(v-ymin)/Math.max(1e-9,ymax-ymin)*
            (bounds.bottom-bounds.top);
        for(let i=0;i<=4;i++) {
            const value=ymin+(ymax-ymin)*i/4,yp=y(value);
            node('line',{x1:bounds.left,y1:yp,x2:bounds.right,y2:yp,
                class:'plot-grid'});
            node('text',{x:75,y:yp+4,'text-anchor':'end'},
                metric.rate?(value*100).toFixed(0)+'%':
                    Number(value.toPrecision(3)).toLocaleString());
        }
        for(let i=0;i<=4;i++) {
            const value=xmin+(xmax-xmin)*i/4,xp=x(value);
            node('text',{x:xp,y:295,'text-anchor':'middle'},
                Math.round(value).toLocaleString());
        }
        node('line',{x1:bounds.left,y1:bounds.bottom,
            x2:bounds.right,y2:bounds.bottom,class:'plot-axis'});
        node('line',{x1:bounds.left,y1:bounds.top,
            x2:bounds.left,y2:bounds.bottom,class:'plot-axis'});
        node('text',{x:width/2,y:320,'text-anchor':'middle'},
            axisLabel);
        const showPoints=series.reduce((n,run)=>n+run.valid.length,0)<=300;
        for(const run of series) {
            const sampled=run.valid.filter(usable).map(p=>hubMetricValue(p,key));
            const mean=sampled.length
                ?sampled.reduce((sum,value)=>sum+value,0)/sampled.length:null;
            if(showAverage&&mean!==null)
                node('line',{x1:bounds.left,y1:y(mean),x2:bounds.right,y2:y(mean),
                    stroke:run.color,'stroke-dasharray':'7 5',class:'plot-average'});
            if(showRolling) {
                let window=[],part=[],lastStep=null,lastAxis=null;
                const flush=()=>{
                    if(part.length>1)
                        node('path',{d:part.map((sample,i)=>
                            (i?'L':'M')+x(sample.axis).toFixed(1)+' '+
                            y(sample.mean).toFixed(1)).join(' '),
                            stroke:run.color,class:'plot-rolling'});
                    else if(part.length===1)
                        node('circle',{cx:x(part[0].axis),cy:y(part[0].mean),
                            r:3,fill:run.color,class:'plot-rolling-point'});
                    part=[];
                };
                for(const point of run.valid) {
                    const step=point.steps_completed;
                    const axis=hubComparisonAxisValue(point,xKey);
                    if(point.break_before||!usable(point)||
                        !Number.isSafeInteger(step)||step<0||
                        lastStep!==null&&step<lastStep||
                        lastAxis!==null&&axis<lastAxis) {
                        flush();window=[];lastStep=null;lastAxis=null;
                    }
                    if(!usable(point)||!Number.isSafeInteger(step)||step<0)
                        continue;
                    window=window.filter(item=>
                        item.step>step-50&&item.step!==step);
                    window.push({step,value:hubMetricValue(point,key)});
                    // A single retained point is not a rolling average.
                    // Do not invent an average when downsampling has left
                    // fewer than two distinct updates in this window.
                    if(window.length>=2)
                        part.push({axis,mean:window.reduce((sum,item)=>
                            sum+item.value,0)/window.length});
                    else flush();
                    lastStep=step;lastAxis=axis;
                }
                flush();
            }
            const legendItem=document.createElement('span');
            legendItem.className='hub-plot-legend-entry';
            const swatch=document.createElement('span');
            swatch.className='hub-plot-legend-swatch';
            swatch.style.borderColor=run.color;
            const unit=units[0]||'';
            const formatted=value=>unit==='ratio'
                ?(value*100).toFixed(2)+'%'
                :Number(value.toPrecision(6)).toLocaleString()+
                    (unit==='seconds'?' s':unit==='rate'?' /s':
                        unit==='mb'?' MiB':unit==='watts'?' W':
                        unit==='scalar'||unit==='count'?'':' '+unit);
            legendItem.append(swatch,document.createTextNode(
                run.slot+' · '+hubJobLabel(run.job)+
                (showAverage&&mean!==null?' · mean '+formatted(mean):'')+
                (showRolling?' · rolling 50-'+(project==='tinybrains'?'update':project==='courier'?'round':'step')+' sample mean':'')));
            legend.append(legendItem);
            let part=[],previousX=null;
            const flush=()=>{
                if(part.length>1)
                    node('path',{d:part.map((p,i)=>(i?'L':'M')+
                        x(hubComparisonAxisValue(p,xKey)).toFixed(1)+' '+
                        y(hubMetricValue(p,key)).toFixed(1)).join(' '),
                        stroke:run.color,class:'plot-line'});
                else if(part.length===1&&!showPoints)
                    node('circle',{cx:x(hubComparisonAxisValue(part[0],xKey)),
                        cy:y(hubMetricValue(part[0],key)),r:3.5,
                        fill:run.color,class:'plot-point'});
                part=[];
            };
            for(const point of run.valid) {
                const currentX=hubComparisonAxisValue(point,xKey);
                if(point.break_before||!usable(point)||
                    previousX!==null&&currentX!==null&&currentX<previousX)
                    flush();
                if(!usable(point)) {
                    previousX=null;
                    continue;
                }
                previousX=currentX;
                part.push(point);
                if(showPoints)
                    node('circle',{cx:x(hubComparisonAxisValue(point,xKey)),
                        cy:y(hubMetricValue(point,key)),
                        r:3.5,fill:run.color,class:'plot-point'});
            }
            flush();
        }
        const perRun=series.map(run=>({
            run,
            points:run.valid.filter(usable).map(point=>({
                point,cx:x(hubComparisonAxisValue(point,xKey)),
                cy:y(hubMetricValue(point,key)),
                value:hubMetricValue(point,key),
                axis:hubComparisonAxisValue(point,xKey),
            })),
        }));
        // Whole plot is hoverable; select a separate nearest x-axis sample
        // for every run instead of showing only one run under the pointer.
        node('rect',{x:bounds.left,y:bounds.top,width:bounds.right-bounds.left,
            height:bounds.bottom-bounds.top,fill:'transparent',
            class:'plot-hover-area'});
        const guide=node('line',{x1:bounds.left,x2:bounds.left,
            y1:bounds.top,y2:bounds.bottom,class:'plot-crosshair',
            visibility:'hidden','pointer-events':'none'});
        const markers=perRun.map(({run})=>node('circle',{
            cx:bounds.left,cy:bounds.bottom,r:5,fill:'white',
            stroke:run.color,'stroke-width':2,
            visibility:'hidden','pointer-events':'none',
        }));
        const hide=()=>{
            tooltip.hidden=true;
            guide.setAttribute('visibility','hidden');
            for(const marker of markers)marker.setAttribute('visibility','hidden');
        };
        svg.onpointerleave=hide;
        svg.onpointermove=event=>{
            const rect=svg.getBoundingClientRect();
            const mouseX=(event.clientX-rect.left)*width/rect.width;
            const mouseY=(event.clientY-rect.top)*330/rect.height;
            if(mouseX<bounds.left||mouseX>bounds.right||
                mouseY<bounds.top||mouseY>bounds.bottom)return hide();
            const chosen=perRun.map(({run,points})=>{
                let closest=null,distance=Infinity;
                for(const point of points) {
                    const delta=Math.abs(mouseX-point.cx);
                    if(delta<distance){distance=delta;closest=point;}
                }
                return {run,closest};
            });
            if(!chosen.some(item=>item.closest))return hide();
            guide.setAttribute('x1',mouseX);
            guide.setAttribute('x2',mouseX);
            guide.setAttribute('visibility','visible');
            for(let i=0;i<chosen.length;i++) {
                const point=chosen[i].closest;
                if(point) {
                    markers[i].setAttribute('cx',point.cx);
                    markers[i].setAttribute('cy',point.cy);
                }
                markers[i].setAttribute('visibility',point?'visible':'hidden');
            }
            const format=value=>Number(value).toLocaleString(undefined,{
                maximumSignificantDigits:15,
            });
            const heading=document.createElement('div');
            heading.className='hub-plot-tooltip-heading';
            heading.textContent='Nearest recorded sample for each run';
            const rows=chosen.map(({run,closest})=>{
                const item=document.createElement('div');
                item.className='hub-plot-tooltip-run';
                const title=document.createElement('div');
                title.className='hub-plot-tooltip-run-title';
                const swatch=document.createElement('span');
                swatch.className='hub-plot-legend-swatch';
                swatch.style.borderColor=run.color;
                title.append(swatch,document.createTextNode(
                    'Run '+run.slot+' · '+hubJobLabel(run.job)));
                item.append(title);
                const line=text=>{
                    const detail=document.createElement('div');
                    detail.className='hub-plot-tooltip-detail';
                    detail.textContent=text;
                    item.append(detail);
                };
                if(!closest){line('No recorded metric samples');return item;}
                const unit=units[0]||'';
                const value=unit==='ratio'
                    ?format(closest.value*100)+'% ('+format(closest.value)+')'
                    :format(closest.value)+(unit==='seconds'?' s':
                        unit==='rate'?' /s':unit==='mb'?' MiB':
                        unit==='watts'?' W':unit==='scalar'||unit==='count'?'':' '+unit);
                line(genericKey+': '+value);
                line(axisLabel+': '+format(closest.axis));
                const timestamp=closest.point.source_modified_at;
                line('Published: '+(timestamp&&!Number.isNaN(Date.parse(timestamp))
                    ?new Date(timestamp).toLocaleString():'unknown'));
                line('Segment: '+(closest.point.segment_id||'unknown'));
                return item;
            });
            tooltip.replaceChildren(heading,...rows);
            tooltip.hidden=false;
            const parent=svg.parentElement;
            const box=parent.getBoundingClientRect();
            const tipWidth=tooltip.offsetWidth||460;
            const tipHeight=tooltip.offsetHeight||150;
            tooltip.style.left=Math.max(8,Math.min(
                parent.clientWidth-tipWidth-8,event.clientX-box.left+14))+'px';
            tooltip.style.top=Math.max(8,Math.min(
                parent.clientHeight-tipHeight-8,event.clientY-box.top+14))+'px';
        };
    }
    function exportComparison(format) {
        if(!state)
            throw Error('Select an available project comparison.');
        const metrics=selectedComparisonMetrics().map(key=>key.slice(7));
        if(format==='csv'&&!metrics.length)
            throw Error('Select at least one recorded metric for CSV export.');
        const chosen=new Set(metrics);
        const jobs=project==='bees'?(state.bees?.runs||[]):
            state.jobs.filter(job=>job.project_id===project);
        const samples=comparisonProject===project&&comparisonData?.samples||[];
        const byJob=new Map(samples.map(sample=>
            [sample.job_id,sample.points||[]]));
        const runs=compareSlots.map(slot=>{
            const id=$('hub-compare-'+slot).value;
            const job=jobs.find(item=>item.id===id);
            if(!job)return null;
            return {
                slot:slot.toUpperCase(),job_id:job.id,
                configuration_name:job.configuration_name,
                run_name:job.run_name||null,
                configuration_sha256:job.configuration_sha256||null,
                assigned_machine_id:job.assigned_machine_id||null,
                artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                continuation_of_job_id:job.continuation_of_job_id||null,
                trial_batch:job.trial_batch||null,
                resource_bundle:job.resource_bundle||null,
                status:job.status,
                execution_diagnostic:job.execution_diagnostic||null,
                ...(project==='tinybrains'?{profile_sha256:job.frozen_parameters?.profile_sha256||null}:{}),
                final_resource_usage:job.final_resource_usage||null,
                evaluation_stop:job.evaluation_stop||null,
                evaluation_suite_definitions:job.evaluation_suite_definitions||null,
                checkpoint_model_sha256:job.checkpoint_model_sha256||null,
                expected_parent_model_sha256:job.expected_parent_model_sha256||null,
                candidate_selection:job.candidate_selection||null,
                last_evaluation:job.last_evaluation||null,
                evaluation_stop_evidence:job.evaluation_stop_evidence||null,
                evaluation_candidates:job.evaluation_candidates||null,
                stop_requested_at:job.stop_requested_at||null,
                points:hubComparisonPoints({job,points:byJob.get(id)||[]})
                    .map(point=>({...point,
                        x_value:hubComparisonAxisValue(
                            point,$('hub-compare-x-axis').value),
                        metrics:Object.fromEntries(
                        Object.entries(point.metrics||{})
                            .filter(([name])=>chosen.has(name)))})),
            };
        }).filter(Boolean);
        if(!runs.some(run=>run.points.length||(format==='json'&&
            (hubComparisonTerminal(run)||hubComparisonEvaluation(run)))))
            throw Error(format==='csv'?'No recorded samples for the selected runs.':
                'Select a job with recorded samples, an evaluation receipt or a terminal outcome.');
        const exported={
            schema_version:1,project_id:project,
            exported_at:new Date().toISOString(),
            x_axis:$('hub-compare-x-axis').value,
            metric:selectedComparisonMetrics()[0]||null,metrics,
            source:'Experiment Hub validated project metrics',
            interpretation:'Training curves are observational. Their selected horizontal axis is a recorded counter or trainer time measurement, never Hub heartbeat time. Compare independent evaluation receipts only across matched suite definitions and scenarios.',
            runs,
            candidate_selection_plans:(state.candidate_selections||[]).filter(p=>
                p.candidate_job_ids?.some(id=>runs.some(r=>r.job_id===id))),
        };
        const content=format==='csv'?hubComparisonCsv(exported):
            JSON.stringify(exported,null,2)+String.fromCharCode(10);
        const blob=new Blob([content],{type:format==='csv'
            ?'text/csv;charset=utf-8':'application/json;charset=utf-8'});
        const url=URL.createObjectURL(blob);
        const link=document.createElement('a');
        link.href=url;
        link.download=project+'-comparison-'+
            exported.exported_at.replace(/[:.]/g,'-')+'.'+format;
        document.body.append(link);
        try{link.click();}finally{
            link.remove();
            setTimeout(()=>URL.revokeObjectURL(url),1000);
        }
        feedback('hub-compare-export-message','Exported '+runs.length+
            ' selected runs as '+format.toUpperCase()+
            '. This is observational progress, not independent evaluation.');
    }
    // The coordinator invokes only an installed trusted exporter. No browser
    // request can choose a filesystem path, executable or checkpoint filename.
    async function exportCompleteComparison() {
        const ids=compareSlots.map(slot=>$('hub-compare-'+slot).value).filter(Boolean);
        if(!ids.length||new Set(ids).size!==ids.length)
            throw Error('Choose at least one distinct managed job.');
        const button=$('hub-compare-export-full');
        button.disabled=true;
        const message=$('hub-compare-export-message');
        try {
            const includeReplays=$('hub-compare-export-replays').checked;
            feedback('hub-compare-export-message','Preparing complete job archives'+
                (includeReplays?' with available raw replays':' without raw replays')+
                '; no training files will be modified.');
            const requested=await action('hub/exports',{
                project_id:project,job_ids:ids,
                metrics:exportAllMetricsToggle.checked?[]:
                    selectedComparisonMetrics().map(key=>key.slice(7)),
                all_recorded_metrics:exportAllMetricsToggle.checked,
                include_replays:includeReplays,
            });
            // An export is read-only and asynchronous. Poll its status rather
            // than attempting a single unbounded HTTP download response.
            for(let attempt=0;attempt<900;attempt++) {
                await new Promise(resolve=>setTimeout(resolve,2000));
                const response=await fetch('/api/hub/exports/'+requested.id,
                    {cache:'no-store'});
                const result=await response.json();
                if(!response.ok)throw Error(result.error||response.statusText);
                if(result.status==='failed')
                    throw Error(result.error||'Job archive generation failed.');
                if(result.status==='ready') {
                    message.replaceChildren(document.createTextNode(
                        result.parts.length+' independently downloadable ZIP part(s), each at most 500 MB. '+
                        (includeReplays
                            ?Number.isSafeInteger(result.replay_files_included)
                                ?result.replay_files_included+
                                    ' locally available raw replay file(s) included. '
                                :'Replay inclusion requested; check the ZIP manifest for availability. '
                            :'Raw replay files excluded. ')+
                        (result.missing_artifact_job_ids?.length
                            ?'Some jobs have no local artifacts: '+
                              result.missing_artifact_job_ids.join(', ')+'. ':'')+
                        (result.missing_sources?.length
                            ?'Unavailable log sources: '+result.missing_sources
                                .slice(0,8).map(item=>item.job_id.slice(-8)+
                                    '/'+item.label+' ('+item.reason+')').join(', ')+
                                ((result.missing_source_count||result.missing_sources.length)>8
                                    ?' and '+((result.missing_source_count||result.missing_sources.length)-8)+' more':'')+
                                '. See the ZIP manifest for details. ':'')+
                        (result.files_extended_during_copy_count
                            ?result.files_extended_during_copy_count+
                                ' log file(s) grew during export; only their captured byte prefixes are included. '+
                                'See export-manifest.json for file details. ':'')+
                        'Select each part: '));
                    for(const [i,part] of result.parts.entries()) {
                        const link=document.createElement('a');
                        link.href='/api/hub/exports/'+requested.id+'/part/'+(i+1);
                        link.textContent=part.name+' ('+(part.bytes/1e6).toFixed(1)+' MB)';
                        link.rel='noopener';
                        link.style.marginRight='12px';
                        message.append(link);
                    }
                    return;
                }
                if(attempt%15===0)
                    feedback('hub-compare-export-message','Preparing ZIP archives; project-owned histories remain untouched.');
            }
            throw Error('Export is still preparing. Check the export status before starting another.');
        }finally { button.disabled=false; }
    }
    const tinyBrainsPicker=document.createElement('fieldset');
    tinyBrainsPicker.hidden=true;
    const tinyBrainsLegend=document.createElement('legend');
    tinyBrainsLegend.textContent='TinyBrains CPU / GPU job';
    const tinyBrainsSelect=document.createElement('select');
    const tinyBrainsImport=document.createElement('select');
    const tinyBrainsUpdates=document.createElement('input');
    const tinyBrainsThreads=document.createElement('input');
    const tinyBrainsDevice=document.createElement('select');
    const tinyBrainsRemote=document.createElement('input');
    const tinyBrainsWeights=document.createElement('input');
    tinyBrainsRemote.type='checkbox';
    tinyBrainsWeights.type='text';
    tinyBrainsWeights.placeholder='Optional: 2,8,4 (local, then each remote)';
    tinyBrainsDevice.append(new Option('Profile default',''),
        new Option('CPU-only fallback (no GPU)','cpu'),
        new Option('Dedicated two-GPU split PPO (ROCm)','dual'));
    tinyBrainsThreads.type='number';
    tinyBrainsThreads.min='1';
    tinyBrainsThreads.max='256';
    tinyBrainsThreads.step='1';
    tinyBrainsThreads.placeholder='Profile default';
    tinyBrainsUpdates.type='number';
    tinyBrainsUpdates.min='1';
    tinyBrainsUpdates.max='10000000';
    tinyBrainsUpdates.step='1';
    tinyBrainsUpdates.value='1';
    const tinyBrainsUse=button('Use profile and update count',()=>{
        const [profile,digest]=tinyBrainsSelect.value.split(':');
        const updates=tinyBrainsUpdates.valueAsNumber;
        const hasThreads=tinyBrainsThreads.value.trim()!=='';
        const threads=hasThreads?tinyBrainsThreads.valueAsNumber:null;
        const cpuFallback=tinyBrainsDevice.value==='cpu';
        const dualGpu=tinyBrainsDevice.value==='dual';
        const remoteRollouts=tinyBrainsRemote.checked;
        const weightText=tinyBrainsWeights.value.trim();
        const weights=weightText?weightText.split(',').map(s=>Number(s.trim())):[];
        if(weightText&&(!remoteRollouts||weights.length<2||weights.length>9||
            weights.some(w=>!Number.isSafeInteger(w)||w<1||w>1024)||
            !(state?.machines||[]).some(machine=>
                machine.status==='connected'&&
                machine.tinybrains_profiles?.[profile]===digest&&
                machine.tinybrains_remote_rollout_endpoints?.length===weights.length-1))) {
            feedback('hub-config-message',
                'Fixed action weights require one integer from 1–1024 for the learner and each approved remote endpoint.');
            return;
        }
        if(hasThreads&&(!Number.isSafeInteger(threads)||threads<1||threads>256)) {
            feedback('hub-config-message','CPU threads must be a whole number from 1 to 256.');
            return;
        }
        if(hasThreads&&!(state?.machines||[]).some(machine=>
            machine.status==='connected'&&machine.tinybrains_profiles?.[profile]===digest&&
            machine.tinybrains_thread_overrides?.includes(profile))) {
            feedback('hub-config-message','No connected worker approves CPU thread overrides for this profile.');
            return;
        }
        if(cpuFallback&&!(state?.machines||[]).some(machine=>
            machine.status==='connected'&&machine.tinybrains_profiles?.[profile]===digest&&
            machine.tinybrains_cpu_fallback_profiles?.includes(profile))) {
            feedback('hub-config-message','No connected worker approves CPU-only fallback for this profile.');
            return;
        }
        if(dualGpu&&!(state?.machines||[]).some(machine=>
            machine.status==='connected'&&machine.tinybrains_profiles?.[profile]===digest&&
            machine.tinybrains_dual_gpu_profiles?.includes(profile))) {
            feedback('hub-config-message','No connected ROCm worker approves two-GPU split PPO for this profile.');
            return;
        }
        if(remoteRollouts&&(cpuFallback||!(state?.machines||[]).some(machine=>
            machine.status==='connected'&&machine.agent_platform==='linux'&&
            machine.tinybrains_profiles?.[profile]===digest&&
            machine.tinybrains_remote_rollout_profiles?.includes(profile)))) {
            feedback('hub-config-message','Remote CPU rollouts require an approved Linux GPU learner and a locally installed tailnet worker configuration.');
            return;
        }
        if(!profile||!digest||!Number.isSafeInteger(updates)||updates<1||updates>10000000) {
            feedback('hub-config-message','Select an approved profile and enter 1–10,000,000 updates.');
            return;
        }
        const [sourceId,sourceDigest]=tinyBrainsImport.value.split(':');
        $('hub-config-json').value=JSON.stringify({
            profile,profile_sha256:digest,updates,
            ...(hasThreads?{threads}:{}),
            ...(cpuFallback?{device:'cpu'}:dualGpu?{device:'dual'}:{}),
            ...(remoteRollouts?{remote_rollouts:true}:{}),
            ...(weights.length?{rollout_action_weights:weights}:{}),
            ...(sourceId&&sourceDigest?{
                import_checkpoint:sourceId,
                import_checkpoint_sha256:sourceDigest,
            }:{}),
        },null,2);
        const selected=approvedTinyBrainsProfiles(state?.machines||[])
            .find(([key])=>key===tinyBrainsSelect.value)?.[1];
        feedback('hub-config-message','Profile and update count copied to the draft. '+
            (cpuFallback||!selected?.cudaWorkers
                ?'When queueing, request CPU/RAM without GPUs. '
                :dualGpu
                    ?'When queueing, request two EXCLUSIVE GPUs and sufficient CPU/RAM; a single PPO optimizer runs on one GPU while collection runs on the other. '
                    :'When queueing, request one GPU (exclusive, or an approved cooperative share) and sufficient CPU/RAM. ')+
            (sourceId
                ?'The source checkpoint SHA-256 is frozen; this draft can initialize a new lineage, not a continuation stage. '
                :'Use a configuration without an import for checkpoint-continuation stages. ')+
            (hasThreads?'The CPU thread override does not change the job CPU quota. ':'')+
            (remoteRollouts?'In Train, use Queue and reserve entire group to atomically assign learner and approved remote collector CPU/GPU budgets before any Start. ':'')+
            (weights.length?'Fixed action weights change rollout work shares, not hardware reservations. ':'')+
            'Save the draft to retain these settings.');
    });
    for(const [caption,control] of [['Approved profile ',tinyBrainsSelect],
        ['Updates ',tinyBrainsUpdates],['CPU threads (optional) ',tinyBrainsThreads],
        ['Execution device ',tinyBrainsDevice],
        ['Use approved remote CPU rollout collectors ',tinyBrainsRemote],
        ['Fixed rollout action weights (optional; local then remotes) ',tinyBrainsWeights],
        ['Approved checkpoint import (optional) ',tinyBrainsImport]]) {
        const label=document.createElement('label');
        label.append(caption,control);
        tinyBrainsPicker.append(label);
    }
    tinyBrainsPicker.prepend(tinyBrainsLegend);
    tinyBrainsPicker.append(tinyBrainsUse);
    $('hub-config-form').prepend(tinyBrainsPicker);
    function displayTinyBrainsImports() {
        const [profile,digest]=tinyBrainsSelect.value.split(':');
        const previous=tinyBrainsImport.value;
        const options=new Map();
        for(const machine of state?.machines||[]) {
            if(machine.status!=='connected'||machine.tinybrains_profiles?.[profile]!==digest)
                continue;
            for(const [id,source] of Object.entries(machine.tinybrains_imports||{})) {
                if(source.profile!==profile||!source.sha256)continue;
                const key=id+':'+source.sha256;
                options.set(key,id+' · '+source.sha256.slice(0,12));
            }
        }
        tinyBrainsImport.replaceChildren(new Option('Fresh initialization (no import)',''),
            ...[...options].sort(([a],[b])=>a.localeCompare(b))
                .map(([key,label])=>new Option(label,key)));
        if(options.has(previous))tinyBrainsImport.value=previous;
    }
    tinyBrainsSelect.addEventListener('change',displayTinyBrainsImports);
    function displayTinyBrainsProfiles() {
        tinyBrainsPicker.hidden=project!=='tinybrains';
        if(tinyBrainsPicker.hidden)return;
        const prior=tinyBrainsSelect.value;
        const options=approvedTinyBrainsProfiles(state.machines);
        tinyBrainsSelect.replaceChildren(...(options.length
            ?options.map(([key,{profile,digest,workers,cudaWorkers}])=>new Option(
                profile+' · '+(cudaWorkers?'GPU (CUDA/ROCm)':'CPU')+' · '+digest.slice(0,12)+
                ' · '+workers+(workers===1?' worker':' workers'),key))
            :[new Option('No connected worker advertises a profile','')]));
        if(options.some(([key])=>key===prior))tinyBrainsSelect.value=prior;
        tinyBrainsSelect.disabled=tinyBrainsUse.disabled=!options.length;
        displayTinyBrainsImports();
    }
    function resetEditor() {
        editing=null;
        $('hub-config-name').value='';
        $('hub-config-json').value='{}';
        tinyBrainsUpdates.value='1';
        tinyBrainsThreads.value='';
        tinyBrainsDevice.value='';
        setEvaluationEditor();
        $('hub-config-save').textContent='Save draft';
        feedback('hub-config-message','');
    }
    function learningSummary(job) {
        const live=job.live_execution;
        const p=live?.progress||job.final_progress;
        if(p?.source==='hub-metrics-v1') {
            // Compact Monitor summaries should show the trainer's recorded
            // learning outcomes as well as steps; timing-only summaries
            // can misleadingly look like missing learning telemetry.
            const preferred=['selection_candidate_survival_score',
                'selection_incumbent_survival_score','selection_champion_promoted',
                'training_completed_matches','training_win_rate',
                'training_score_rate','training_mean_score_margin',
                'training_mean_score','loss','policy_loss','value_loss',
                'entropy','total_ppo_updates','ant_decisions_per_second',
                'update_wall_time','collection_seconds','optimization_seconds'];
            const priority=preferred.filter(key=>Object.hasOwn(p.metrics,key));
            const entries=[...priority.map(key=>[key,p.metrics[key]]),
                ...Object.entries(p.metrics).filter(([key])=>!priority.includes(key))];
            const values=entries.slice(0,5).map(
                ([key,measurement])=>key+' '+
                    (measurement.unit==='ratio'
                        ?(measurement.value*100).toFixed(1)+'%'
                        :measurement.value.toLocaleString()+' '+measurement.unit));
            const remaining=Math.max(0,Object.keys(p.metrics).length-values.length);
            return 'Steps '+p.steps_completed.toLocaleString()+
                ' · '+values.join(' · ')+
                (remaining?' · +'+remaining+' more':'')+
                ' · Summary written '+new Date(p.source_modified_at).toLocaleString();
        }
        if(live?.progress_issue)
            return 'Local progress unavailable: '+live.progress_issue;
        return job.dispatch_id?'Awaiting trusted project summary':'—';
    }
    function setEvaluationEditor(policy) {
        const multi=policy?.all_of||null;
        $('hub-evaluation-suite').value=multi?'':policy?.suite_id??'';
        $('hub-evaluation-digest').value=multi?'':policy?.suite_sha256??'';
        $('hub-evaluation-trials').value=multi?'':policy?.min_trials??'';
        $('hub-evaluation-rate').value=multi?'':policy?.min_success_rate??'';
        $('hub-evaluation-all-of').value=multi?JSON.stringify(multi,null,2):'';
    }
    function evaluationFromEditor() {
        const suite=$('hub-evaluation-suite').value.trim();
        const digest=$('hub-evaluation-digest').value.trim();
        const trials=$('hub-evaluation-trials').value;
        const rate=$('hub-evaluation-rate').value;
        const allOf=$('hub-evaluation-all-of').value.trim();
        if(allOf) {
            if(suite||digest||trials||rate)
                throw Error('Choose either the single-suite fields or all-required-suites JSON.');
            return {all_of:JSON.parse(allOf)};
        }
        if(!suite&&!digest&&!trials&&!rate)return null;
        if(!suite||!trials||!rate||Number(rate)<=0)
            throw Error('Enter an approved suite ID, minimum trials, and a success rate greater than zero.');
        return {suite_id:suite,min_trials:Number(trials),min_success_rate:Number(rate),
            ...(digest?{suite_sha256:digest}:{})};
    }
    // New jobs inherit a project-specific measurement priority, not a claim
    // that every candidate metric is available in every worker version.
    // Individual job choices remain editable without changing the project
    // defaults or previously frozen jobs.
    const defaultJobMetrics={
        tinybrains:[
            'training_score_rate','training_win_rate',
            'training_mean_score_margin','training_completed_matches',
            'food_gathered_exact','ant_decisions_per_second',
            'total_ppo_updates','update_wall_time','job_cpu_cores',
            'host_gpu_busy_pct',
        ],
        courier:[
            'selection_candidate_mean_deliveries',
            'selection_candidate_pickup_rate',
            'selection_candidate_mean_steps_per_delivery',
            'selection_candidate_survival_rate',
            'selection_candidate_mean_lifespan',
            'selection_candidate_gross_energy_per_step',
            'training_recent_mean_completed_lifespan',
            'active_training_time','job_cpu_cores','host_cpu_busy_pct',
        ],
        tinybrains_rollout:[
            'ant_decisions_per_second','engine_steps_per_second',
            'job_cpu_cores','host_cpu_busy_pct',
        ],
        other:['active_training_time','job_cpu_cores','job_memory_mb',
            'host_cpu_busy_pct'],
    };
    const jobMetricChoicesByProject=new Map();
    // Availability is adapter-driven. These are discoverable requests, not a
    // promise that every project provides every physical/game measurement.
    const knownLearningMetrics=[
        'training_wins','training_losses','training_draws',
        'training_completed_matches','training_win_rate','training_score_rate',
        'training_mean_score','training_mean_score_margin',
        'training_scored_matches','training_opponent_margin_matches',
        'training_mean_match_turns','food_gathered_exact',
        'food_gathered_verified_matches',
        'food_disappearances_near_own_ant','visible_food_mean',
        'combat_kills_exact','combat_kills_verified_matches',
        'births_observed','deaths_observed',
        'population_mean','population_peak','combat_exposure_fraction',
        'selection_candidate_survival_score','selection_incumbent_survival_score',
        'selection_champion_promoted','selection_evaluation_round',
        'training_recent_mean_completed_lifespan','training_recent_deaths',
        'training_mean_active_age','training_oldest_active_age',
        'training_active_couriers','training_mean_active_colony_age',
        'training_active_colonies',
        'selection_candidate_survival_rate','selection_candidate_mean_lifespan',
        'selection_candidate_mean_deliveries','selection_candidate_pickup_rate',
        'selection_candidate_mean_steps_per_delivery',
        'selection_candidate_gross_energy_per_step',
        'selection_candidate_mean_feedings','selection_candidate_mean_births',
        'benchmark_round','benchmark_survival_rate','benchmark_mean_lifespan',
        'benchmark_mean_deliveries','benchmark_pickup_rate',
        'benchmark_mean_steps_per_delivery','benchmark_gross_energy_per_step',
        'benchmark_mean_feedings','benchmark_mean_births',
        'terminal_rewards','episodes_ended','loss','policy_loss',
        'value_loss','baseline_loss','entropy','local_elo','mean_reward'];
    const knownPerformanceMetrics=[
        'total_ppo_updates','update_wall_time','elapsed_training_seconds',
        'active_training_time',
        'engine_steps','engine_steps_per_second','ant_decisions',
        'ant_decisions_per_second','group_states','group_states_per_second',
        'collection_seconds','rollout_model_refresh_seconds',
        'rollout_engine_step_seconds','rollout_learner_inference_seconds',
        'optimization_seconds','overlap_seconds',
        'remote_policy_snapshot_seconds',
        'remote_opponent_snapshot_seconds','ppo_microbatches','states_per_microbatch','actor_grad_norm',
        'group_grad_norm','baseline_grad_norm','policy_lag',
        'rss_mb','job_memory_mb','job_peak_memory_mb','job_cpu_cores',
        'job_cpu_lease_utilization_pct','job_process_tasks',
        'job_cpu_throttled_seconds','job_cpu_throttle_events',
        'job_cpu_quota_periods','job_cpu_quota_throttled_periods_pct',
        'job_memory_lease_utilization_pct',
        'host_cpu_busy_pct','host_gpu_busy_pct','host_gpu_vram_mb',
        'host_gpu_temperature_c','host_gpu_hotspot_temperature_c',
        'host_gpu_power_w',
        // The wire inventory supports up to 32 physical devices. Operators
        // can prioritize metrics for any pinned GPU before its first run.
        ...Array.from({length:32},(_,i)=>[
            'host_gpu_'+i+'_busy_pct',
            'host_gpu_'+i+'_used_vram_mb',
            'host_gpu_'+i+'_temperature_c',
            'host_gpu_'+i+'_hotspot_temperature_c',
            'host_gpu_'+i+'_power_w',
        ]).flat(),
        'data_preparation_seconds','inference_seconds','forward_seconds',
        'backward_seconds','optimizer_step_seconds','cpu_wait_seconds',
        'gpu_transfer_seconds','network_transfer_seconds',
        'broker_queue_seconds','serialization_seconds','checkpoint_seconds',
        'ppo_profile_host_seconds','ppo_advantages_seconds',
        'ppo_ordering_seconds','ppo_zero_grad_seconds',
        'ppo_grouping_seconds','ppo_data_preparation_seconds',
        'ppo_forward_loss_seconds','ppo_loss_finite_check_seconds',
        'ppo_backward_seconds','ppo_metric_readback_seconds',
        'ppo_gradient_processing_seconds','ppo_optimizer_step_seconds',
        'ppo_parameter_finite_check_seconds','ppo_gpu_stream_sync_seconds',
        'ppo_minibatches','ppo_board_pixel_samples',
        'ppo_max_board_cells','ppo_unique_board_shapes',
        'evaluation_seconds','collector_count',
        // The TinyBrains learner contributes slot 0; up to eight remote
        // workers contribute slots 1–8. Let the operator select every slot.
        ...Array.from({length:9},(_,i)=>[
            'collector_'+i+'_ant_decisions',
            'collector_'+i+'_engine_steps',
            'collector_'+i+'_action_target',
            'collector_'+i+'_elapsed_seconds',
            'collector_'+i+'_rollout_engine_step_seconds',
            'collector_'+i+'_learner_inference_seconds',
            'collector_'+i+'_roundtrip_seconds',
            'collector_'+i+'_model_prepare_seconds',
            'collector_'+i+'_snapshot_cache_files',
            'collector_'+i+'_snapshot_cache_bytes',
            'collector_'+i+'_decisions_per_second',
            'collector_'+i+'_engine_steps_per_second',
        ]).flat()];
    function renderJobMetricSelection() {
        const select=$('hub-job-metrics');
        if(!select)return;
        const prior=jobMetricChoicesByProject.get(project) ??
            new Set(defaultJobMetrics[project]||defaultJobMetrics.other);
        const observed=new Set();
        for(const job of state?.jobs||[]) {
            if(job.project_id!==project)continue;
            for(const name of Object.keys(job.live_execution?.progress?.metrics||
                job.final_progress?.metrics||{}))observed.add(name);
            for(const name of job.metric_selection||[])observed.add(name);
        }
        if(comparisonProject===project)
            for(const item of comparisonData?.samples||[])
                for(const point of item.points||[])
                    for(const name of Object.keys(point.metrics||{}))observed.add(name);
        const all=[...new Set([...knownLearningMetrics,...knownPerformanceMetrics,
            ...observed])].filter(key=>/^[a-z][a-z0-9_]{0,63}$/.test(key))
            .sort();
        select.replaceChildren(...all.map(key=>{
            const option=new Option(key+(observed.has(key)
                ?' · recorded':' · not yet observed'),key);
            option.dataset.description=hubMetricDescription(key);
            option.title=option.dataset.description+'\n'+(observed.has(key)
                ?'This metric has been published by a job in this project.'
                :'Selecting a metric does not install missing project instrumentation.');
            option.selected=prior.has(key);
            option.dataset.category=knownLearningMetrics.includes(key)
                ?'Learning outcomes':knownPerformanceMetrics.includes(key)
                    ?'Performance and resources':'Other observed metrics';
            return option;
        }));
        renderMetricPicker('hub-job-metrics','hub-job-metric-picker');
    }
    function selectedJobMetrics() {
        return [...$('hub-job-metrics').selectedOptions].map(o=>o.value);
    }
    $('hub-job-metrics').addEventListener('change',()=>{
        jobMetricChoicesByProject.set(project,new Set(selectedJobMetrics()));
    });
    function display() {
        if(!state)return;
        const current=project;
        $('hub-project').replaceChildren(...state.projects.map(p=>new Option(p.name,p.id)));
        project=state.projects.some(p=>p.id===current)?current:'bees';
        $('hub-project').value=project;
        renderJobMetricSelection();
        $('hub-copy-bees-form').hidden=project!=='bees';
        $('hub-config-evaluation').hidden=project==='bees';
        displayTinyBrainsProfiles();
        $('hub-config-rows').replaceChildren();
        const configs=state.configurations.filter(c=>c.project_id===project);
        for(const c of configs.slice().reverse()) {
            const tr=document.createElement('tr');
            cell(tr,c.name);cell(tr,project);cell(tr,c.revision);
            cell(tr,c.updated_at?.replace('T',' ').slice(0,19)||'');
            const actions=cell(tr,'');
            $('hub-config-rows').append(tr);
            actions.append(button('Edit draft',()=>{
                editing={id:c.id,revision:c.revision};
                $('hub-config-name').value=c.name;
                $('hub-config-json').value=JSON.stringify(c.parameters,null,2);
                tinyBrainsThreads.value=c.parameters.threads??'';
                tinyBrainsDevice.value=['cpu','dual'].includes(c.parameters.device)
                    ?c.parameters.device:'';
                setEvaluationEditor(c.evaluation_stop);
                $('hub-config-save').textContent='Save revision';
                feedback('hub-config-message','Editing revision '+c.revision);
            }));
            actions.append(button('Duplicate',()=>{
                resetEditor();
                $('hub-config-name').value=(c.name+' copy').slice(0,100);
                $('hub-config-json').value=JSON.stringify(c.parameters,null,2);
                tinyBrainsThreads.value=c.parameters.threads??'';
                tinyBrainsDevice.value=['cpu','dual'].includes(c.parameters.device)
                    ?c.parameters.device:'';
                setEvaluationEditor(c.evaluation_stop);
                feedback('hub-config-message','Duplicated into a new unsaved draft. Select Save draft to retain it.');
            }));
            if(project==='bees') {
                actions.append(button('Queue for Bees',async()=>{
                    if(!confirm('Freeze and queue "'+c.name+'"? Training will not start until the queue is armed.'))return;
                    try {
                        await action('queue',c.parameters);
                        feedback('hub-config-message','Queued '+c.name+' via the existing Bees manager.');
                        selectTab('train');
                        if(typeof refresh==='function')await refresh();
                    }catch(e){feedback('hub-config-message',e.message);}
                }));
                actions.append(button('Load into training form',()=>{
                    try { populateBeesForm(c.parameters);selectTab('train');}
                    catch(e){feedback('hub-config-message',e.message);}
                }));
            }
        }
        if(!configs.length) {
            const tr=document.createElement('tr');
            cell(tr,'No saved configurations.').colSpan=5;
            $('hub-config-rows').append(tr);
        }
        const jobSelect=$('hub-job-configuration');
        const former=jobSelect.value;
        jobSelect.replaceChildren(...configs.map(c=>new Option(
            c.name+' · revision '+c.revision,c.id)));
        if(configs.some(c=>c.id===former)) jobSelect.value=former;
        const predecessorSelect=$('hub-job-predecessor');
        const previousParent=predecessorSelect.value;
        const successors=new Set((state.jobs||[])
            .filter(j=>{
                if(j.status==='cancelled')return false;
                if(j.status==='failed'&&j.prelaunch_absence_verified===true&&
                    j.capacity_released_at&&
                    !(state.jobs||[]).some(child=>child.status!=='cancelled'&&
                        (child.continuation_of_job_id===j.id||child.after_job_id===j.id)))
                    return false;
                return Boolean(j.continuation_of_job_id||j.after_job_id);
            })
            .map(j=>j.continuation_of_job_id||j.after_job_id));
        const availableParents=(state.jobs||[]).filter(j=>
            ['tinybrains','courier'].includes(project)&&j.project_id===project&&!successors.has(j.id)&&(
                j.assigned_machine_id&&j.dispatch_id&&(
                    (j.status==='finished'&&
                        (j.checkpoint_ready_verified===true||
                         j.checkpoint_proof_required!==true))||
                    (j.status==='stopped'&&
                        j.checkpoint_stop_verified===true&&j.capacity_released_at)||
                    ['start_requested','launch_requested','running'].includes(j.status))||
                ['waiting_adapter','waiting_predecessor'].includes(j.status)));
        predecessorSelect.replaceChildren(
            new Option('New experiment (no predecessor)',''),
            ...availableParents.map(j=>new Option(
                j.configuration_name+' · '+j.status+
                (j.checkpoint_ready_verified===true||
                    j.checkpoint_stop_verified===true?' · checkpoint verified':
                    j.status==='finished'&&j.checkpoint_proof_required!==true
                        ?' · legacy checkpoint verified at Start':'')+
                (!j.capacity_released_at?' · stage will wait':'')+' on '+
                (state.machines.find(m=>m.id===(j.assigned_machine_id||
                    j.continuation_machine_id))?.name||
                    j.assigned_machine_id||j.continuation_machine_id||
                    'machine not assigned yet')+
                ' · '+j.id.slice(-8),j.id)));
        if(availableParents.some(j=>j.id===previousParent))
            predecessorSelect.value=previousParent;
        const sequenceRows=sequenceRowsForJobs(state.jobs||[]);
        $('hub-sequence-overview-panel').hidden=project==='bees'||
            !sequenceRows.length;
        $('hub-sequence-message').hidden=project==='bees';
        const sequenceTable=$('hub-sequence-overview-rows');
        sequenceTable.replaceChildren();
        for(const summary of sequenceRows) {
            const tr=document.createElement('tr');
            cell(tr,summary.root.project_id+' · '+
                summary.root.configuration_name+' · '+
                summary.root.id.slice(-8));
            cell(tr,summary.chain.map(job=>
                (job.continuation_of_job_id?'Resume ':job.after_job_id?'Fresh ':'')+
                job.project_id+' / '+job.configuration_name.slice(0,28))
                .join(' → '));
            const position=summary.chain.indexOf(summary.current)+1;
            cell(tr,position+' of '+summary.chain.length+
                ' · '+summary.current.project_id+
                ' · '+summary.current.status);
            cell(tr,summary.completed+' of '+summary.chain.length+
                ' stages completed and capacity released');
            cell(tr,summary.reason);
            const actions=cell(tr,'');
            const future=summary.chain.slice(position);
            const futureProbeHold=future.some(job=>job.gpu_qualification?.holds_claim);
            if(summary.canCancel&&future.length&&!futureProbeHold&&future.every(job=>
                !job.dispatch_id&&(job.status==='waiting_adapter'||
                    job.status==='waiting_predecessor'&&!job.lease_id))) {
                actions.append(button('Cancel future queued jobs',async()=>{
                    if(!confirm('Cancel '+future.length+' unstarted future jobs across '+
                        [...new Set(future.map(job=>job.project_id))].join(', ')+
                        '? The current job '+summary.current.configuration_name+
                        ' will not be stopped. Claims for cancelled unstarted jobs will be released.'))return;
                    try {
                        const result=await action('hub/jobs/cancel-future',{
                            after_job_id:summary.current.id,confirm_cancel:true,
                            expected_job_ids:future.map(job=>job.id),
                        });
                        feedback('hub-sequence-message',
                            'Cancelled '+result.cancelled_count+
                            ' future queued jobs. Current native jobs were not stopped.');
                        await reload();
                    }catch(error){
                        await reload();
                        feedback('hub-sequence-message',String(error.message||error));
                    }
                }));
            }else if(future.length&&summary.canCancel) {
                actions.textContent=futureProbeHold
                    ?'A downstream GPU probe still holds its claim.'
                    :'A downstream job has started or needs inspection.';
            }
            sequenceTable.append(tr);
        }
        const pipeline=$('hub-pipeline-panel');
        pipeline.hidden=['bees','ants'].includes(project);
        const kind=$('hub-pipeline-kind'),oldKind=kind.value;
        kind.replaceChildren(
            new Option('Independent fresh jobs (no shared checkpoint)','independent'),
            new Option('Independent trials (no dependency)','batch'),
            ...(['tinybrains','courier'].includes(project)
                ?[new Option('Checkpoint-continuation stages','continuation')]
                :[]));
        if(pipelineKindProject===project&&
            ['independent','batch','continuation'].includes(oldKind)&&
            (oldKind!=='continuation'||['tinybrains','courier'].includes(project)))
            kind.value=oldKind;
        else kind.value=['tinybrains','courier'].includes(project)?'continuation':'independent';
        pipelineKindProject=project;
        const checkpointPipeline=['tinybrains','courier'].includes(project)&&kind.value==='continuation';
        const trialBatch=kind.value==='batch';
        $('hub-pipeline-add').textContent=trialBatch?'Add trial':'Add stage';
        $('hub-pipeline-queue').textContent=trialBatch?'Queue all trials':'Queue entire pipeline';
        $('hub-pipeline-title').textContent=checkpointPipeline
            ?'Prequeue checkpoint-continuation stages'
            :trialBatch?'Queue independent trials together':'Queue independent project jobs in sequence';
        $('hub-pipeline-description').textContent=checkpointPipeline
            ?'Two to sixteen stages with the same approved configuration digest, on the original worker. Natural completion advances only with a verified saved checkpoint; evaluation-triggered Stop additionally requires native checkpoint-preserving shutdown. Manual Stops and failures hold later stages for review.'
            :trialBatch?'Two to sixteen fresh jobs with separate artifact lineages. Select saved configurations for each seed or parameter variant. The jobs are eligible independently as qualified capacity becomes available.':
                'Two to sixteen fresh, independent jobs from any non-Bees project. Each job gets its own artifact lineage and may use a different machine. No checkpoint, optimizer or qualification result is inherited. A verified finish or an evaluation-driven Stop can advance the next job after capacity release. Manual Stops and failures do not advance. Queueing never starts training.';
        $('hub-pipeline-predecessor-label').textContent=checkpointPipeline
            ?'Continue an existing job (optional)'
            :'Run after an existing native job (optional)';
        const pipelinePredecessor=$('hub-pipeline-predecessor');
        pipelinePredecessor.closest('label').hidden=trialBatch;
        const formerPipelineParent=pipelinePredecessor.value;
        const retiredParents=new Set((state.jobs||[]).filter(j=>
            j.project_id==='ants').map(j=>j.id));
        const sequenceParents=trialBatch?[]:checkpointPipeline?availableParents:
            (state.jobs||[]).filter(j=>{
                if(['bees','ants'].includes(j.project_id)||
                    retiredParents.has(j.after_job_id)||successors.has(j.id))
                    return false;
                const queued=['waiting_adapter','waiting_predecessor',
                    'start_requested','launch_requested','running'].includes(j.status);
                const completed=j.independent_sequence_complete===true&&
                    Boolean(j.dispatch_id&&j.assigned_machine_id&&j.execution_updated_at);
                return queued||completed;
            });
        pipelinePredecessor.replaceChildren(
            new Option(checkpointPipeline?'New experiment (no predecessor)':
                'New independent sequence (no predecessor)',''),
            ...sequenceParents.map(j=>new Option(
                (checkpointPipeline?'':j.project_id+' · ')+
                    j.configuration_name+' · '+j.status+' · '+j.id.slice(-8),
                j.id)));
        if(sequenceParents.some(j=>j.id===formerPipelineParent))
            pipelinePredecessor.value=formerPipelineParent;
        if(project!=='bees') {
            if(!$('hub-pipeline-rows').children.length) {
                addPipelineStage();
                addPipelineStage();
            }
            renderPipelineStages();
            if(!$('hub-resource-bundle-rows').children.length) {
                addResourceBundleMember();
                addResourceBundleMember();
            }
            renderResourceBundleMembers();
        }
        $('hub-job-queue').disabled=['bees','ants'].includes(project)||!configs.length;
        const previousMachine=$('hub-job-machine').value;
        $('hub-job-machine').replaceChildren(new Option('Automatic best fit',''),
            ...state.machines.filter(m=>m.status==='connected')
                .map(m=>new Option(m.name+' ('+m.id+')',m.id)));
        if(state.machines.some(m=>m.id===previousMachine&&m.status==='connected'))
            $('hub-job-machine').value=previousMachine;
        renderDistributedGroup();
        const records=(state.jobs||[]).filter(j=>j.project_id===project);
        $('hub-job-allocate-all').disabled=['bees','ants'].includes(project)||
            !records.some(j=>j.status==='waiting_adapter'&&!j.lease_id);
        $('hub-global-allocate').disabled=!(state.jobs||[]).some(j=>
            j.adapter==='unconfigured'&&j.status==='waiting_adapter'&&!j.lease_id);
        const automation=state.automation||{enabled:false,machine_ids:[]};
        const automationOptions=$('hub-automation-machines');
        const selectedAutomation=new Set([
            ...automationOptions.querySelectorAll('input:checked')
        ].map(input=>input.value));
        if(automation.enabled)
            for(const id of automation.machine_ids)selectedAutomation.add(id);
        const automationCandidates=state.machines.filter(m=>
            m.status==='connected'&&m.local_executor_enabled&&
            m.native_preflight_passed&&m.installed_projects?.length&&
            (m.agent_platform==='win32'||m.agent_platform==='linux'&&
                m.execution_capabilities?.cgroup_v2_detected===true&&
                ['cpu','memory'].every(c=>
                    m.execution_capabilities?.cgroup_controllers.includes(c))));
        automationOptions.replaceChildren();
        for(const machine of automationCandidates) {
            const label=document.createElement('label');
            const input=document.createElement('input');
            input.type='checkbox';input.value=machine.id;
            input.checked=selectedAutomation.has(machine.id);
            label.append(input,document.createTextNode(' '+machine.name+
                ' ('+machine.id+') · '+machine.agent_platform+
                ' · adapters: '+machine.installed_projects.join(', ')));
            automationOptions.append(label);
        }
        if(!automationCandidates.length)
            automationOptions.textContent='No connected native-capable machines with a passing preflight.';
        const pendingRecovery=automation.restart_rearm_pending===true;
        $('hub-automation-arm').disabled=automation.enabled||pendingRecovery||
            !automationCandidates.length;
        $('hub-automation-disarm').disabled=!automation.enabled&&!pendingRecovery;
        for(const id of ['hub-automation-adopt-running',
            'hub-automation-resume-after-restart','hub-automation-rebalance',
            'hub-automation-gpu','hub-automation-gpu-claim',
            'hub-automation-gpu-probe'])
            $(id).disabled=automation.enabled||pendingRecovery;
        if(automation.enabled) {
            $('hub-automation-resume-after-restart').checked=
                automation.resume_after_restart===true&&automation.resume_after_agent_restart===true;
            $('hub-automation-rebalance').checked=automation.rebalance_queued===true;
            $('hub-automation-gpu').checked=automation.start_qualified_gpu===true;
            $('hub-automation-gpu-claim').checked=automation.claim_exclusive_gpu===true;
            $('hub-automation-gpu-probe').checked=automation.probe_exclusive_gpu===true;
        }
        feedback('hub-automation-status',
            (automation.enabled?'Armed for '+automation.machine_ids.join(', '):
                pendingRecovery?'Awaiting restart reconciliation':'Disarmed')+
            (automation.resume_after_restart&&!automation.resume_after_agent_restart
                ?' · Saved permission covers coordinator restarts only; disarm and re-arm to include agent recovery.':'')+
            (automation.last_action?' · '+automation.last_action:'')+
            (automation.last_transition_at?' · Last change '+
                new Date(automation.last_transition_at).toLocaleString():'')+
            (automation.waiting_reason?' · '+automation.waiting_reason:'')+
            (automation.last_error?' · REQUIRES REVIEW: '+automation.last_error:''));
        $('hub-job-rows').replaceChildren();
        const displayedTrialBatches=new Set();
        for(const j of hubVisibleJobs(records,50)) {
            const tr=document.createElement('tr');
            cell(tr,hubJobLabel(j)+trialBatchLabel(j));
            cell(tr,j.configuration_revision);
            const provenanceLease=state.reservations.find(l=>l.id===j.lease_id);
            const linked=provenanceLease&&
                ['offered','acknowledged'].includes(provenanceLease.status)
                ?provenanceLease:null;
            const machine=state.machines.find(m=>m.id===j.assigned_machine_id);
            const labels={
                start_requested:'Start requested',launch_requested:'Starting',
                running:'Running (native verified)',
                waiting_predecessor:'Waiting for predecessor',
                stop_requested:'Stop requested',finished:'Finished',
                failed:'Failed',stopped:'Stopped',unverified:'Needs inspection',
                cancelled:'Cancelled',
            };
            const live=j.live_execution;
            const usage=live && j.status==='running' ?
                ' · '+(live.memory_current_mb==null?'RAM ?':live.memory_current_mb+' MiB RAM')+
                (live.cpu_time_ns==null?'':' · '+Math.round(live.cpu_time_ns/1e9)+' CPU seconds')+
                (Number.isFinite(live.cpu_cores_used)
                    ?' · '+live.cpu_cores_used.toFixed(2)+' average CPU cores':'')
                :'';
            const lineage=j.continuation_of_job_id
                ?' · Resumes '+(state.jobs.find(p=>
                    p.id===j.continuation_of_job_id)?.configuration_name||
                    'prior experiment')+' on original machine'
                :j.after_job_id
                    ?' · Runs independently after '+(state.jobs.find(p=>
                        p.id===j.after_job_id)?.configuration_name||'prior job')
                    :'';
            const parent=state.jobs.find(p=>
                p.id===(j.continuation_of_job_id||j.after_job_id));
            const waitingOnParent=j.status==='waiting_predecessor'
                ?'Prequeued · No capacity claim · '+
                    (j.predecessor_wait?.reason||
                        'Awaiting predecessor '+(parent?.status||'unknown'))
                :null;
            const status=waitingOnParent||(
                j.status==='waiting_adapter'
                    ?linked?('Hub budget claim on '+linked.machine_id+' · '+
                        (linked.receipt_fresh?'Receipt fresh':linked.status)+
                        ' · Not started')
                        :'Waiting for trusted adapter · No claim'
                    :nativeJobLabel(j,labels[j.status]||j.status)+usage)+
                    (j.capacity_released_at?' · Capacity released':'')+
                    (machine?.status==='offline'?' · Agent offline':'');
            const proof=j.gpu_qualification;
            const probeHoldsClaim=proof?.holds_claim===true;
            const gpuProof=proof
                ?proof.cleanup_required?' · GPU probe cleanup needs verification'
                :proof.status==='requested'
                    ?proof.automation_paused?' · Automatic GPU access probe suspended'
                        :' · GPU access probe requested (no training)'
                    :proof.status==='verified'
                        ?proof.current
                            ?' · GPU device access probe passed this agent session'
                            :' · GPU device probe previously passed, now stale'
                        :proof.status==='cancelled'
                            ?' · GPU device probe cancelled'
                            :proof.status==='stale'
                                ?' · GPU device access proof invalidated by hardware change'
                                :proof.cleanup_only?' · GPU probe cleanup verified'
                                    :' · GPU device access probe rejected'
                :'';
            const detail=j.execution_diagnostic?' · '+j.execution_diagnostic:'';
            const readiness=j.native_start_readiness;
            const readinessText=readiness
                ?readiness.eligible
                    ?' · '+readiness.reason
                    :' · Start blocked: '+readiness.reason
                :'';
            const hasSuccessor=(state.jobs||[]).some(x=>
                (x.continuation_of_job_id===j.id||x.after_job_id===j.id)&&
                x.status!=='cancelled');
            const promoted=j.candidate_selection
                ?' · Selected winner '+j.candidate_selection.winner_job_id.slice(-8)+
                    ' · pinned evaluated model '+j.candidate_selection.checkpoint_sha256.slice(0,12)
                :'';
            const checkpointRecovery=j.checkpoint_reinspected_at
                ?' · Resumable checkpoint reverified'
                :j.checkpoint_reinspection_requested_at?' · Checkpoint recheck pending':'';
            const noTrainer=j.prelaunch_absence_verified===true
                ?' · No trainer started':'';
            const groupWorkers=j.project_id==='tinybrains'&&
                j.distributed_rollout_managed===true
                ?state.jobs.filter(worker=>worker.project_id==='tinybrains_rollout'&&
                    worker.frozen_parameters?.learner_job_id===j.id):[];
            const scheduledGroup=(state.distributed_group_plans||[])
                .filter(plan=>plan.learner_job_id===j.id).at(-1);
            const unresolvedScheduledGroup=scheduledGroup&&
                ['pending','needs_review'].includes(scheduledGroup.status);
            const distributedLaunch=(state.distributed_launches||[]).find(launch=>
                launch.learner_job_id===j.id);
            const launchText=distributedLaunch
                ?' · Coordinated Start '+distributedLaunch.status+
                    (distributedLaunch.recovery_pending?' (coordinator restart recovery)':'')+
                    ': '+distributedLaunch.reason
                :'';
            const scheduleText=scheduledGroup
                ?' · Group scheduled Start '+scheduledGroup.status+
                    (scheduledGroup.recovery_pending?' (restart reconciliation)':'')+
                    ': '+scheduledGroup.reason
                :'';
            const abortText=j.distributed_abort_requested_at
                ?' · Startup abort requested: native collector shutdown and verified claim release pending'
                :'';
            const distributedSummary=groupWorkers.length
                ?' · Distributed collectors: '+groupWorkers.map(worker=>
                    (worker.assigned_machine_id||'unassigned')+' '+worker.status+
                    (worker.capacity_released_at?' (released)':'')).join('; ')
                :'';
            const bundleLabel=j.resource_bundle
                ?' · Resource bundle '+j.resource_bundle.id.slice(-8)+
                    ' ('+j.resource_bundle.index+'/'+j.resource_bundle.size+')'
                :'';
            const statusCell=cell(tr,status+lineage+promoted+gpuProof+readinessText+
                checkpointRecovery+noTrainer+distributedSummary+launchText+scheduleText+abortText+bundleLabel+
                (hasSuccessor&&['waiting_adapter','waiting_predecessor'].includes(j.status)
                    ?' · Cancel downstream stage first':''));

            statusCell.title=readiness?.reason||
                proof?.diagnostic||detail||j.status_reason||status;
            const pinnedGpuDevices=(provenanceLease?.gpu_bindings||[])
                .map(binding=>{
                    const physical=(machine?.observed_gpus||[])
                        .find(device=>device.pci_address===binding.pci_address);
                    const description=binding.id+' @ '+binding.pci_address;
                    const observed=physical?.used_vram_mb;
                    return description+(observed==null?''
                        :' · '+observed+' MiB host GPU used')+
                        (physical?.gpu_busy_pct==null?''
                            :' · '+physical.gpu_busy_pct+'% host GPU busy')+
                        (physical?.power_w==null?''
                            :' · '+physical.power_w+' W host GPU')+
                        (physical?.temperature_c==null?''
                            :' · '+physical.temperature_c+'°C GPU')+
                        (physical?.hotspot_temperature_c==null?''
                            :' · '+physical.hotspot_temperature_c+'°C hotspot');
                });
            const resourceText=j.resources.cpus+' CPU / '+j.resources.ram_mb+
                ' MiB / '+j.resources.gpu_count+' GPUs'+
                (j.resources.gpu_count
                    ?' ('+j.resources.gpu_mode+
                        (j.resources.gpu_mode==='shared'
                            ?' · '+j.resources.gpu_vram_mb+' MiB PyTorch budget / '+
                                j.resources.gpu_compute_pct+'% advisory compute':'')+
                        (pinnedGpuDevices.length?' · '+pinnedGpuDevices.join(', '):' · awaiting binding')+')'
                    :'');
            const resourceCell=cell(tr,resourceText);
            if(j.resources.gpu_count)
                resourceCell.title='GPU budget reservations and observed host telemetry are separate. Shared Windows PyTorch limits do not enforce GPU compute time or non-PyTorch memory.';
            const policy=j.evaluation_stop;
            const multi=policy?.all_of||null;
            const evidence=j.evaluation_stop_evidence||j.last_evaluation;
            const cohort=j.evaluation_candidates?.[0];
            const gateSummary=multi?multi.map((rule,index)=>{
                const receipt=j.evaluation_stop_evidence?.all_of?.[index]||
                    cohort?.receipts?.[rule.suite_id];
                const met=receipt&&receipt.trials>=rule.min_trials&&
                    receipt.successes/receipt.trials>=rule.min_success_rate;
                return rule.suite_id+': '+(receipt
                    ?receipt.successes+'/'+receipt.trials+(met?' passed':' below target')
                    :'pending');
            }).join(' · '):null;
            const evaluation=policy
                ?multi
                    ?' · Benchmark gates ('+multi.length+' required, same checkpoint): '+
                        gateSummary+(j.evaluation_stop_evidence?' · Stop authorized':'')+
                        (j.live_execution?.evaluation_issue?' · '+j.live_execution.evaluation_issue:'')
                    :' · Evaluation '+policy.suite_id+': '+(evidence
                        ?evidence.successes+'/'+evidence.trials+' successes'+
                            (j.evaluation_stop_evidence?' · stop target met':' · target not met')
                        :'awaiting evidence')+
                        (j.live_execution?.evaluation_issue?' · '+j.live_execution.evaluation_issue:'')
                :'';
            cell(tr,learningSummary(j)+evaluation);
            const actions=cell(tr,'');
            actions.append(button('Rename run',async()=>{
                const entered=prompt('Run name (optional; blank restores the configuration name):',
                    j.run_name||'');
                if(entered===null||entered.trim()===(j.run_name||''))return;
                try {
                    await action('hub/jobs/rename',{
                        job_id:j.id,run_name:entered.trim(),
                        expected_run_name:j.run_name||null,
                    });
                    feedback('hub-job-message','Run label updated. Trainer and job configuration are unchanged.');
                    await reload();
                }catch(error){feedback('hub-job-message',String(error.message||error));}
            }));
            if(j.status==='waiting_adapter'&&!j.lease_id)
                actions.append(button('Coordinate capacity',()=>allocateJob([j.id])));
            if(j.gpu_probe_block===null&&
                (proof?.status!=='requested'||proof.automation_paused))
                actions.append(button(proof?.automation_paused?'Resume GPU access probe':
                    proof?.status==='verified'?'Recheck GPU device access':'Check GPU device access',async()=>{
                    if(!confirm('Run an inert Linux GPU access probe for '+
                        j.configuration_name+' on '+machine.name+
                        '? It opens the selected and excluded render devices, '+
                        'and starts a short isolated systemd probe. '+
                        'It does NOT train, reserve VRAM, authorize GPU execution, '+
                        'or guarantee isolation from other host processes.'))return;
                    try{
                        await action('hub/jobs/qualify-gpu',{
                            job_id:j.id,confirm_qualify:true,
                        });
                        feedback('hub-job-message',
                            'Inert GPU device probe requested. No training started.');
                        await reload();
                    }catch(e){feedback('hub-job-message',e.message);}
                }));
            if(j.gpu_probe_block&&machine?.agent_platform==='linux'&&
                (proof?.status!=='requested'||proof.first_offered_at===null||proof.automation_paused))
                actions.append(document.createTextNode(j.gpu_probe_block+' '));
            if(j.gpu_probe_cleanup_ready)
                actions.append(button('Recheck GPU probe cleanup',async()=>{
                    if(!confirm('Ask the worker to verify GPU probe cleanup for '+j.configuration_name+'?'))return;
                    try {
                        await action('hub/jobs/qualify-gpu',{job_id:j.id,confirm_qualify:true,confirm_cleanup:true});
                        feedback('hub-job-message','GPU probe cleanup recheck requested.');
                        await reload();
                    }catch(error){feedback('hub-job-message',error.message);}
                }));
            if(j.status==='waiting_adapter'&&proof?.status==='requested'&&proof.first_offered_at===null)
                actions.append(button('Withdraw GPU probe',async()=>{
                    if(!confirm('Withdraw the undelivered GPU access probe for '+j.configuration_name+
                        '? The job keeps its current claim.'))return;
                    try{
                        await action('hub/jobs/withdraw-gpu-probe',{job_id:j.id,
                            expected_request_id:proof.request_id,confirm_withdraw:true});
                        feedback('hub-job-message','Undelivered GPU access probe withdrawn.');
                    }catch(error){
                        feedback('hub-job-message',error.message);
                    }
                    await reload();
                }));
            // The operator may start only the collectors, or preapprove one
            // bounded learner Start that waits for authenticated running
            // receipts. Neither control changes the group's fixed leases.
            if(j.status==='waiting_adapter'&&!unresolvedScheduledGroup&&
                j.distributed_rollout_managed===true&&groupWorkers.length>0&&
                groupWorkers.every(worker=>worker.status==='waiting_adapter'&&
                    !worker.dispatch_id&&worker.native_start_readiness?.eligible===true)) {
                const startDistributed=async (coordinated,recovery=false)=>{
                    const hosts=groupWorkers.map(worker=>worker.assigned_machine_id);
                    const gpus=groupWorkers.reduce((sum,worker)=>
                        sum+worker.resources.gpu_count,0);
                    const cpus=groupWorkers.reduce((sum,worker)=>
                        sum+worker.resources.cpus,0);
                    if(!confirm('Authorize '+groupWorkers.length+
                        ' pinned remote collectors across '+hosts.join(', ')+
                        ' with '+cpus+' leased CPUs'+
                        (gpus?' and '+gpus+' exclusive CUDA GPUs':'')+'? '+
                        'These are real native processes; CUDA access on Windows is cooperative. '+
                        'If any host, lease or native Start proof is invalid, no collector is authorized. '+
                        (coordinated
                            ?'Also authorize ONE learner GPU Start after all collectors have fresh verified running receipts. '+
                                'This permission expires after 20 minutes. '+
                                (recovery
                                    ?'You separately approve coordinator restart recovery, but ONLY after all ORIGINAL remote dispatches are freshly verified running and the GPU learner passes its native admission and HMAC endpoint checks again. The learner and remote workers are NEVER retried. '
                                    :'Coordinator restart disarms learner Start. ')+
                                'Changed collector dispatches, lease owners, HMAC identities or unsafe machine states disarm launch. '+
                                'If any collector exits before learner Start, remaining sidecars receive native Stop/cleanup requests. '
                            :'The GPU learner must be started separately after verification. ')+
                        'All resource allocations stay fixed.'))return;
                    try {
                        const result=await action('hub/distributed/tinybrains/start-collectors',{
                            learner_job_id:j.id,
                            expected_collector_job_ids:groupWorkers.map(worker=>worker.id),
                            confirm_start:true,
                            ...(coordinated?{
                                start_learner_when_ready:true,confirm_learner_start:true,
                                ...(recovery?{
                                    resume_after_restart:true,confirm_restart_rearm:true,
                                }:{}),
                            }:{}),
                        });
                        feedback('hub-job-message',result.starts_authorized+
                            ' remote collector Starts authorized atomically. '+
                            (coordinated
                                ?'Learner Start preapproved'+
                                    (recovery?' for bounded restart-reconciled recovery':' for this coordinator session')+
                                    '; awaiting verified remote native owners.'
                                :'Start the learner separately after the collectors are verified.'));
                        await reload();
                    }catch(error){
                        feedback('hub-job-message',error.message);
                        await reload();
                    }
                };
                actions.append(button('Start collectors only',()=>startDistributed(false)));
                actions.append(button('Start coordinated group',()=>startDistributed(true)));
                actions.append(button('Start recoverable coordinated group',()=>startDistributed(true,true)));
            }
            // An early approval may be granted immediately after the
            // atomic reservation, before the worker lease receipts or GPU
            // probe have finished. All future Starts use the existing
            // qualified distributed collector/learner admission path.
            if(j.distributed_rollout_managed===true&&
                j.status==='waiting_adapter'&&!j.dispatch_id&&
                !j.distributed_abort_requested_at&&groupWorkers.length>0&&
                !unresolvedScheduledGroup&&!distributedLaunch?.status?.includes('pending')&&
                j.lease_id&&j.assigned_machine_id&&
                groupWorkers.every(worker=>worker.status==='waiting_adapter'&&
                    !worker.dispatch_id&&worker.lease_id&&worker.assigned_machine_id)) {
                const schedule=async recovery=>{
                    if(!confirm('Approve the complete distributed TinyBrains group for one future native Start when ALL pinned collector and learner machine claims are ready? '+
                        'This grants real remote collector Starts and a subsequent one-use GPU learner Start only after verified original native collector ownership. '+
                        'The approval expires after 20 minutes. '+
                        (recovery
                            ?'You separately permit the pending approval to survive a coordinator restart, after fresh machine, lease, HMAC and GPU proof. '
                            :'A coordinator restart disarms the approval. ')+
                        'No dispatched worker or learner is retried, and all resources remain fixed. '+
                        'Verified native terminal reports release capacity.'))return;
                    try {
                        const plan=await action('hub/distributed/tinybrains/schedule-start',{
                            learner_job_id:j.id,
                            expected_collector_job_ids:groupWorkers.map(worker=>worker.id),
                            confirm_schedule:true,confirm_learner_start:true,
                            ...(recovery?{resume_after_restart:true,
                                confirm_restart_rearm:true}:{}),
                        });
                        feedback('hub-job-message',
                            'Distributed group '+plan.status+
                            '. The Hub will authorize all original remote collectors atomically when their leases and GPU admission are ready.');
                    }catch(error){feedback('hub-job-message',error.message);}
                    await reload();
                };
                actions.append(button('Schedule full group when ready',()=>schedule(false)));
                actions.append(button('Schedule recoverable group',()=>schedule(true)));
            }
            if(unresolvedScheduledGroup)
                actions.append(button('Cancel scheduled group',async()=>{
                    if(!confirm('Revoke future collector and GPU learner Starts for this group? The queued native jobs and their resource claims stay unchanged.'))return;
                    try {
                        await action('hub/distributed/tinybrains/cancel-start-plan',{
                            plan_id:scheduledGroup.id,confirm_cancel:true,
                        });
                        feedback('hub-job-message',
                            'Scheduled distributed group Start revoked; no native process was created by cancellation.');
                    }catch(error){feedback('hub-job-message',error.message);}
                    await reload();
                }));
            if(distributedLaunch?.status==='pending')
                actions.append(button('Cancel future learner Start',async()=>{
                    if(!confirm('Disarm this pending coordinated learner Start? '+
                        'The existing remote collector processes and all resource claims remain unchanged.'))return;
                    try{
                        await action('hub/distributed/tinybrains/cancel-launch',{
                            learner_job_id:j.id,confirm_cancel:true,
                        });
                        feedback('hub-job-message',
                            'Future learner Start disarmed. Native collectors and reservations remain unchanged.');
                    }catch(error){feedback('hub-job-message',error.message);}
                    await reload();
                }));
            if(j.distributed_rollout_managed===true&&
                j.status==='waiting_adapter'&&!j.dispatch_id&&
                !j.distributed_abort_requested_at&&groupWorkers.length>0)
                actions.append(button('Abort distributed startup',async()=>{
                    if(!confirm('Abandon this unstarted TinyBrains learner and its '+
                        groupWorkers.length+' remote collectors? Undelivered Starts will be cancelled; '+
                        'already dispatched collectors receive native Stop requests. '+
                        'No running or uncertain process claim will be released without verified termination. '+
                        'The learner GPU claim is released only after every sidecar is proven stopped '+
                        'or absent. Native stops can remain pending on offline machines. '+
                        'Existing checkpoints and runs are never deleted.'))return;
                    try{
                        const result=await action('hub/distributed/tinybrains/abort-startup',{
                            learner_job_id:j.id,
                            expected_collector_job_ids:groupWorkers.map(worker=>worker.id),
                            confirm_abort:true,
                        });
                        feedback('hub-job-message',result.learner_cancelled
                            ?'Distributed startup cancelled; all native collector claims safely released.'
                            :'Distributed startup abort requested; awaiting sidecar Stop/absence receipts.');
                    }catch(error){feedback('hub-job-message',error.message);}
                    await reload();
                }));
            if(j.status==='waiting_adapter'&&readiness?.eligible===true)
                actions.append(button('Start native job',async()=>{
                    const windows=machine.agent_platform==='win32';
                    const gpu=j.resources.gpu_count>0;
                    const autoRelease=$('hub-native-release-on-terminal').checked;
                    if(!confirm((gpu?'Authorize native '+(j.resources.gpu_mode==='shared'
                            ?'cooperative shared':'exclusive')+' GPU execution for ':
                        'Authorize native CPU/RAM-limited execution of ')+
                        j.configuration_name+' on '+machine.name+
                        '? Only locally installed adapter code selects the executable. '+
                        (gpu?(windows
                            ?'Windows CUDA selects the pinned UUID cooperatively; other programs are not isolated. '+
                                (j.resources.gpu_mode==='shared'
                                    ?'TinyBrains limits PyTorch allocator memory to '+j.resources.gpu_vram_mb+
                                        ' MiB; its '+j.resources.gpu_compute_pct+
                                        '% GPU compute budget is advisory, not enforced. '
                                    :'No GPU share or VRAM cap is enforced. ')
                            :'The Linux service verifies device access and ROCm visibility before training. '+
                                (j.resources.gpu_mode==='shared'
                                    ?'TinyBrains limits PyTorch allocator memory to '+j.resources.gpu_vram_mb+
                                        ' MiB; its '+j.resources.gpu_compute_pct+
                                        '% GPU compute budget is advisory. No hard GPU VRAM isolation is provided. '
                                    :'')):'')+
                        (windows?'Windows Stop requests checkpoint-preserving shutdown when the local adapter supports it; otherwise it force-terminates the Job Object process tree. ':
                            'Linux uses the locally selected service stop policy. ')+
                        (autoRelease?'The Hub will release this claim after verified finish or Stop. ':'')+
                        'This starts a real process; verify host capacity and experimental validity.'))return;
                    try{
                        await action('hub/jobs/start',{
                            job_id:j.id,confirm_start:true,release_on_terminal:autoRelease,
                        });
                        $('hub-native-release-on-terminal').checked=false;
                        feedback('hub-job-message',
                            'Native start authorized. Awaiting local OS enforcement verification.');
                        await reload();
                    }catch(e){feedback('hub-job-message',e.message);}
                }));
            if(j.status==='start_requested'&&j.start_first_offered_at===null)
                actions.append(button('Withdraw Start',async()=>{
                    if(!confirm('Withdraw the undelivered Start for '+j.configuration_name+
                        '? The job returns to the queue and keeps its claim.'))return;
                    try{
                        await action('hub/jobs/withdraw-start',{job_id:j.id,
                            expected_dispatch_id:j.dispatch_id,confirm_withdraw:true});
                        feedback('hub-job-message','Start withdrawn. The job is queued with its existing claim.');
                    }catch(error){
                        feedback('hub-job-message',error.message);
                    }
                    await reload();
                }));
            else if(['start_requested','launch_requested','running','unverified']
                .includes(j.status))
                actions.append(button('Stop managed job',async()=>{
                    const windows=machine?.agent_platform==='win32';
                    if(!confirm('Request native stop for '+j.configuration_name+
                        '? '+(windows
                            ?'Windows will request checkpoint-preserving shutdown when supported by the local adapter; otherwise it will force-terminate the owned Job Object process tree. '
                            :'Linux will use the locally installed systemd stop policy. ')+
                        'An offline agent may leave the stop request pending.'))return;
                    try{
                        await action('hub/jobs/stop',{
                            job_id:j.id,confirm_stop:true,
                        });
                        feedback('hub-job-message',
                            'Stop recorded. Awaiting machine-local service confirmation.');
                        await reload();
                    }catch(e){feedback('hub-job-message',e.message);}
                }));
            if(j.project_id==='tinybrains'&&j.status==='finished'&&
                j.checkpoint_proof_required===true&&j.checkpoint_ready_verified!==true) {
                if(j.checkpoint_reinspection_requested_at) {
                    actions.append(document.createTextNode('Checkpoint recheck pending'));
                }else {
                    actions.append(button('Recheck finished checkpoint',async()=>{
                        if(!confirm('Ask the original authenticated Halifax agent to inspect '+
                            'the existing finished TinyBrains container and verify its '+
                            'resumable checkpoint? This does not restart training or '+
                            'modify the checkpoint. Restore frozen lineage bytes first.'))return;
                        try{
                            await action('hub/jobs/reinspect-checkpoint',{
                                job_id:j.id,confirm_reinspect:true,
                            });
                            feedback('hub-job-message',
                                'Checkpoint reinspection requested from the original worker.');
                            await reload();
                        }catch(e){feedback('hub-job-message',e.message);}
                    }));
                }
            }
            if(['finished','failed','stopped'].includes(j.status)&&linked)
                actions.append(button('Release completed capacity',async()=>{
                    if(!confirm('Release the native job’s Hub capacity after its terminal service report? The job history is preserved. This does not stop a process.'))return;
                    try{
                        await action('hub/leases/release',{
                            lease_id:j.lease_id,confirm_release:true,
                        });
                        feedback('hub-job-message','Completed job capacity released; history preserved.');
                        await reload();
                    }catch(e){feedback('hub-job-message',e.message);}
                }));
            if(j.status==='waiting_adapter'&&j.lease_id&&!j.dispatch_id&&
                !j.continuation_machine_id&&!j.resource_bundle&&
                !probeHoldsClaim) {
                actions.append(button('Move claim',async()=>{
                    const destination=selectedMoveDestination(j);
                    if(!destination)return;
                    if(!confirm('Move the queued claim for '+j.configuration_name+' to '+
                        destination.name+'? The new worker must acknowledge the claim. '+
                        'An armed scheduler can then start an eligible CPU job.'))return;
                    try {
                        const moved=await action('hub/jobs/move-claim',{job_id:j.id,
                            expected_lease_id:j.lease_id,machine_id:destination.id,
                            confirm_move:true});
                        feedback('hub-job-allocate-message','Claim moved to '+moved.machine_id+
                            ' and awaits a fresh worker receipt.');
                    }catch(error){
                        feedback('hub-job-allocate-message',error.message);
                    }
                    await reload();
                }));
                actions.append(button('Stage move',()=>{
                    const destination=selectedMoveDestination(j);
                    if(!destination)return;
                    stagedMoves.set(j.id,{job_id:j.id,expected_lease_id:j.lease_id,
                        machine_id:destination.id,label:j.project_id+' / '+j.configuration_name+
                            ' · '+j.assigned_machine_id+' → '+destination.id});
                    renderStagedMoves();
                }));
            }
            if(j.status==='waiting_adapter'&&j.lease_id&&!j.dispatch_id&&!probeHoldsClaim)
                actions.append(button('Release claim',async()=>{
                    if(!confirm('Release this advisory budget claim? No process is stopped.'))return;
                    try{
                        await action('hub/leases/release',{
                            lease_id:j.lease_id,confirm_release:true,
                        });
                        feedback('hub-job-allocate-message','Claim released; job remains queued.');
                        await reload();
                    }catch(e){feedback('hub-job-allocate-message',e.message);}
                }));
            if(['waiting_adapter','waiting_predecessor'].includes(j.status)&&
                !j.dispatch_id&&!hasSuccessor&&!probeHoldsClaim)actions.append(button('Cancel',async()=>{
                if(!confirm('Cancel this unstarted '+j.project_id+' job? No process is running.'))return;
                try{
                    await action('hub/jobs/cancel',{job_id:j.id,confirm_cancel:true});
                    feedback('hub-job-message','Cancelled job before execution.');
                    await reload();
                }catch(e){feedback('hub-job-message',e.message);}
            }));
            if(j.trial_batch&&!displayedTrialBatches.has(j.trial_batch.id)) {
                displayedTrialBatches.add(j.trial_batch.id);
                const queued=state.jobs.filter(job=>job.trial_batch?.id===j.trial_batch.id&&
                    job.status==='waiting_adapter'&&!job.dispatch_id);
                if(queued.length)actions.append(button('Cancel queued batch trials',async()=>{
                    if(!confirm('Cancel '+queued.length+' queued trials from batch '+j.trial_batch.id.slice(-8)+
                        ' across all projects? Trials already dispatched keep running.'))return;
                    try {
                        const result=await action('hub/trials/cancel-queued',{
                            trial_batch_id:j.trial_batch.id,
                            expected_job_ids:queued.map(job=>job.id),confirm_cancel:true,
                        });
                        feedback('hub-job-message','Cancelled '+result.cancelled_count+' queued trials.');
                        await reload();
                    }catch(error){feedback('hub-job-message',error.message);}
                }));
            }
            $('hub-job-rows').append(tr);
        }
        if(!records.length) {
            const tr=document.createElement('tr');
            cell(tr,'No queued jobs for this project.').colSpan=6;
            $('hub-job-rows').append(tr);
        }
        const waiting=records.filter(j=>j.status==='waiting_adapter').length;
        const blocked=records.filter(j=>j.status==='waiting_predecessor').length;
        const cancelled=records.filter(j=>j.status==='cancelled').length;
        const allocated=records.filter(j=>j.status==='waiting_adapter'&&j.lease_id).length;
        const running=records.filter(j=>j.status==='running').length;
        const pendingNative=records.filter(j=>[
            'start_requested','launch_requested','stop_requested'
        ].includes(j.status)).length;
        const uncertain=records.filter(j=>j.status==='unverified').length;
        feedback('hub-other-monitor',
            waiting+' ready to coordinate · '+blocked+' prequeued stages waiting on predecessors · '+
            allocated+' with advisory claims · '+
            running+' native services reported running · '+pendingNative+
            ' start/stop operations pending · '+uncertain+' requiring inspection · '+
            cancelled+' cancelled. Remote status is heartbeat-based.');
        $('hub-monitor-rows').replaceChildren();
        for(const j of hubVisibleJobs(records,30)) {
            const tr=document.createElement('tr');
            const live=j.live_execution;
            const progress=live?.progress||j.final_progress;
            cell(tr,hubJobLabel(j)+
                trialBatchLabel(j)+
                (j.continuation_of_job_id?' · continuation':''));
            cell(tr,(j.status==='waiting_predecessor'
                    ?j.predecessor_wait?.reason||'Waiting for predecessor'
                    :nativeJobLabel(j,j.status))+
                (j.capacity_released_at?' · capacity released':'')+
                (['finished','stopped','failed','unverified'].includes(j.status)&&
                    j.execution_diagnostic?' · '+j.execution_diagnostic:''));
            cell(tr,learningSummary(j));
            cell(tr,progress?.source_modified_at
                ?new Date(progress.source_modified_at).toLocaleString():'—');
            const final=j.final_resource_usage;
            const peakJobMemory=final?.peak_job_memory_bytes??live?.peak_job_memory_bytes;
            const nativePeak=peakJobMemory==null?null:
                (peakJobMemory/1048576).toFixed(2)+' MiB peak Job Object memory';
            const measured=final?[
                nativePeak,
                final.peak_observed_memory_mb==null?null:
                    final.peak_observed_memory_mb.toLocaleString()+' MiB highest sampled RAM',
                final.cpu_time_seconds_high_water!=null
                    ?final.cpu_time_seconds_high_water.toFixed(2)+' highest reported CPU seconds'
                    :final.cpu_time_ns_high_water==null?null:
                        (final.cpu_time_ns_high_water/1e9).toFixed(2)+' highest reported CPU seconds',
            ].filter(Boolean).join(' · '):live?[
                nativePeak,
                live.memory_current_mb==null?null:
                    live.memory_current_mb.toLocaleString()+' MiB RAM',
                live.cpu_time_seconds!=null
                    ?Math.round(live.cpu_time_seconds).toLocaleString()+' CPU seconds'
                    :live.cpu_time_ns==null?null:
                        Math.round(live.cpu_time_ns/1e9).toLocaleString()+' CPU seconds',
                Number.isFinite(live.cpu_cores_used)
                    ?live.cpu_cores_used.toFixed(2)+' average CPU cores':null,
            ].filter(Boolean).join(' · '):'';
            const resourceCell=cell(tr,measured||'—');
            if(final)resourceCell.title='Retained samples from '+final.first_observed_at+
                ' to '+final.last_observed_at+'. Earlier samples may have been lost on coordinator restart.';
            $('hub-monitor-rows').append(tr);
        }
        if(!records.length) {
            const tr=document.createElement('tr');
            cell(tr,'No project jobs yet.').colSpan=5;
            $('hub-monitor-rows').append(tr);
        }
        const physical=$('hub-gpu-bind-machine');
        const previousGpuMachine=physical.value;
        const candidates=state.machines.filter(m=>
            m.status==='connected'&&
            (m.agent_platform==='linux'&&m.gpu_inventory_source==='amdgpu-sysfs-v1'||
             m.agent_platform==='win32'&&m.gpu_inventory_source==='nvidia-smi-v1'));
        physical.replaceChildren(new Option('Select machine with physical GPUs',''),
            ...candidates.map(m=>new Option(m.name+' ('+m.id+')',m.id)));
        if(candidates.some(m=>m.id===previousGpuMachine))
            physical.value=previousGpuMachine;
        else if(candidates.length===1)physical.value=candidates[0].id;
        if(lastGpuBindingMachine!==physical.value) {
            const selected=candidates.find(m=>m.id===physical.value);
            const inventory=selected?.gpus?.length
                ?selected.gpus.map(g=>({
                    id:g.id,vram_mb:g.vram_mb,
                    pci_address:g.pci_address||'',
                }))
                :(selected?.observed_gpus||[]).map((g,index)=>({
                    id:'gpu'+index,vram_mb:g.vram_mb,
                    pci_address:g.pci_address,
                }));
            $('hub-gpu-bind-json').value=JSON.stringify(inventory,null,2);
            lastGpuBindingMachine=physical.value;
        }
        const budgetMachine=$('hub-budget-machine');
        const selectedBudgetId=budgetMachine.value;
        budgetMachine.replaceChildren(new Option('Select machine',''),
            ...state.machines.map(m=>new Option(m.name+' ('+m.id+')',m.id)));
        const preferred=state.machines.some(m=>m.id===selectedBudgetId)
            ?selectedBudgetId:state.machines[0]?.id||'';
        budgetMachine.value=preferred;
        const budgetSelected=state.machines.find(m=>m.id===preferred);
        if(!budgetDraft||budgetDraft.machine_id!==preferred) {
            budgetDraft=budgetSelected?{
                machine_id:preferred,cpus:budgetSelected.cpus,
                ram_mb:budgetSelected.ram_mb,
            }:null;
            $('hub-budget-cpus').value=budgetSelected?.cpus??'';
            $('hub-budget-ram').value=budgetSelected?.ram_mb??'';
        }
        $('hub-budget-save').disabled=!budgetSelected;
        const gpuBudgetMachine=$('hub-gpu-budget-machine');
        const priorGpuBudgetId=gpuBudgetMachine.value;
        const budgetCandidates=state.machines.filter(m=>(m.gpus||[]).length>0);
        gpuBudgetMachine.replaceChildren(new Option('Select GPU machine',''),
            ...budgetCandidates.map(m=>new Option(m.name+' ('+m.id+')',m.id)));
        const chosenGpuBudget=budgetCandidates.some(m=>m.id===priorGpuBudgetId)
            ?priorGpuBudgetId:budgetCandidates[0]?.id||'';
        gpuBudgetMachine.value=chosenGpuBudget;
        const selectedGpuBudget=budgetCandidates.find(m=>m.id===chosenGpuBudget);
        if(!gpuBudgetDraft||gpuBudgetDraft.machine_id!==chosenGpuBudget) {
            gpuBudgetDraft=selectedGpuBudget?{
                machine_id:chosenGpuBudget,gpus:structuredClone(selectedGpuBudget.gpus),
            }:null;
            $('hub-gpu-budget-json').value=JSON.stringify(
                (selectedGpuBudget?.gpus||[]).map(g=>({
                    id:g.id,vram_mb:g.vram_mb,
                    ...(g.pci_address?{pci_address:g.pci_address}:{}),
                    hub_vram_mb:g.hub_vram_mb??g.vram_mb,
                    hub_compute_pct:g.hub_compute_pct??100,
                })),null,2);
        }
        $('hub-gpu-budget-save').disabled=!selectedGpuBudget;
        $('hub-machine-rows').replaceChildren();
        for(const m of state.machines) {
            const tr=document.createElement('tr');
            // These are Hub ledger reservations, not observed CPU/RAM usage.
            const claims=state.reservations.filter(l=>l.machine_id===m.id&&
                ['active','offered','acknowledged'].includes(l.status));
            const claimedCpus=claims.reduce((sum,l)=>sum+(l.resources?.cpus||0),0);
            const claimedRam=claims.reduce((sum,l)=>sum+(l.resources?.ram_mb||0),0);
            // These are measurements from reporting Hub-owned native jobs
            // only. Bees workers, OS services and other software are excluded.
            const activeJobs=state.jobs.filter(j=>j.assigned_machine_id===m.id&&
                ['running','stop_requested'].includes(j.status));
            const sampledCpu=activeJobs.filter(j=>
                Number.isFinite(j.live_execution?.cpu_cores_used));
            const sampledRam=activeJobs.filter(j=>
                Number.isFinite(j.live_execution?.memory_current_mb));
            const observedCpu=sampledCpu.reduce((sum,j)=>
                sum+j.live_execution.cpu_cores_used,0);
            const observedRam=sampledRam.reduce((sum,j)=>
                sum+j.live_execution.memory_current_mb,0);
            const overClaimed=m.capacity_overcommitted===true;
            cell(tr,m.name+' ('+m.id+')');
            const status=cell(tr,m.status+
                (overClaimed?' · Claims exceed reported capacity':''));
            if(overClaimed)
                status.title='Hub claims exceed fresh agent CPU/RAM capacity; new starts require renewed receipts. Existing processes are not stopped.';
            const cpuCell=cell(tr,m.cpus+' configured'+
                (m.reported_cpus!=null?' / '+m.reported_cpus+' reported':'')+
                ' / '+claimedCpus+' claimed'+
                (m.status==='connected'&&m.host_cpu_busy_pct!=null
                    ?' · '+m.host_cpu_busy_pct.toFixed(1)+'% host busy (all processes)':'')+
                (sampledCpu.length?' · '+observedCpu.toFixed(2)+
                    ' average cores from '+sampledCpu.length+'/'+activeJobs.length+
                    ' Hub native jobs':''));
            cpuCell.title='Host busy percentage includes Bees, external training and the OS; Hub native job samples are a subset and must not be added to it.';
            cell(tr,m.ram_mb+' MiB configured'+
                (m.reported_ram_mb!=null?' / '+m.reported_ram_mb+' MiB reported':'')+
                ' / '+claimedRam+' MiB claimed'+
                (m.status==='connected'&&m.reported_available_ram_mb!=null
                    ?' · '+m.reported_available_ram_mb+' MiB currently available (all processes)':
                    m.status==='connected'&&m.reported_free_ram_mb!=null
                        ?' · '+m.reported_free_ram_mb+' MiB host free (all processes)':'')+
                (sampledRam.length?' · '+Math.round(observedRam).toLocaleString()+
                    ' MiB observed from '+sampledRam.length+'/'+activeJobs.length+
                    ' Hub native jobs':''));
            const verified=new Map((m.verified_gpus||[]).map(g=>[g.id,g]));
            const configured=(m.gpus||[]).map(g=>{
                const matched=verified.get(g.id);
                return g.id+' ('+g.vram_mb+' MiB configured; Hub budget '+
                    (g.hub_vram_mb??g.vram_mb)+' MiB / '+
                    (g.hub_compute_pct??100)+'% compute) · '+
                    (matched?'PCI '+matched.pci_address+' verified, '+
                        matched.vram_mb+' MiB verified device capacity'
                        :g.pci_address?'PCI '+g.pci_address+' not observed':
                            'No PCI binding');
            });
            const discovered=(m.observed_gpus||[]).map(g=>
                g.pci_address+' ('+g.vram_mb+' MiB'+
                (g.used_vram_mb==null?'':', '+g.used_vram_mb+' MiB used')+
                (g.gpu_busy_pct==null?'':', '+g.gpu_busy_pct+'% host busy')+
                (g.power_w==null?'':', '+g.power_w+' W host GPU')+
                (g.temperature_c==null?'':', '+g.temperature_c+'°C GPU')+
                (g.hotspot_temperature_c==null?'':', '+g.hotspot_temperature_c+
                    '°C hotspot')+')');
            cell(tr,(configured.join(' / ')||'No configured GPUs')+
                (discovered.length?' · Detected '+(m.agent_platform==='win32'?'NVIDIA':'AMD')+' GPUs: '+
                    discovered.join(', '):''));
            const capability=m.execution_capabilities;
            const recoveryOnly=m.local_executor_enabled&&!m.installed_projects?.length;
            const control=cell(tr,m.status==='connected'
                ?recoveryOnly?'Native recovery only · no installed adapters'
                :m.local_executor_enabled
                    ?m.native_preflight_passed
                        ?(m.agent_platform==='win32'
                            ?'Windows Job Object CPU/RAM preflight passed · GPU not isolated'
                            :'Linux kernel cgroup CPU/RAM preflight passed · GPU not isolated')
                        :'Native start blocked · preflight incomplete or failed'
                    :'Observation only'+(capability?.cgroup_v2_detected
                        ?' · Linux cgroup v2 detected':'')
                :'Offline or not verified');
            control.title=recoveryOnly
                ?'The native journal can inspect and stop owned jobs.'
                :m.native_preflight_passed
                ?'Authenticated local native preflight passed; verify limits for each launched job. GPU not isolated.'
                :m.native_preflight_reason||
                    capability?.reason||'No authenticated native capability report.';
            cell(tr,m.last_seen_at?.replace('T',' ').slice(0,19)||'—');
            const actions=cell(tr,'');
            actions.append(button(m.agent_enrolled?'Rotate':'Enroll',async()=>{
                const rotate=m.agent_enrolled;
                if(!confirm((rotate?'Rotate':'Enroll')+' machine credential for '+m.id+
                    '? Rotation invalidates the old credential.'))return;
                try{
                    const result=await action('hub/machines/enroll',{
                        machine_id:m.id,confirm_enroll:true,confirm_rotate:rotate,
                    });
                    $('hub-enrollment-token').value=result.agent_token;
                    feedback('hub-enrollment-message',
                        'Credential issued for '+m.id+'. Copy it now; it cannot be retrieved later.');
                    await reload();
                }catch(e){feedback('hub-enrollment-message',e.message);}
            }));
            if(m.agent_enrolled)actions.append(button('Revoke',async()=>{
                if(!confirm('Disable machine agent access for '+m.id+'?'))return;
                try{
                    await action('hub/machines/revoke',{machine_id:m.id,confirm_revoke:true});
                    $('hub-enrollment-token').value='';
                    feedback('hub-enrollment-message','Revoked machine credential for '+m.id);
                    await reload();
                }catch(e){feedback('hub-enrollment-message',e.message);}
            }));
            $('hub-machine-rows').append(tr);
        }
        if(!state.machines.length) {
            const tr=document.createElement('tr');
            cell(tr,'No registered machines.').colSpan=8;
            $('hub-machine-rows').append(tr);
        }
        const selectedProject=$('hub-lease-project').value;
        $('hub-lease-project').replaceChildren(...state.projects.map(p=>
            new Option(p.name,p.id)));
        $('hub-lease-project').value=state.projects.some(p=>p.id===selectedProject)
            ?selectedProject:project;
        const selectedMachine=$('hub-lease-machine').value;
        const machines=state.machines.filter(m=>m.status==='connected');
        $('hub-lease-machine').replaceChildren(...machines.map(m=>
            new Option(m.name+' ('+m.id+')',m.id)));
        if(machines.some(m=>m.id===selectedMachine))
            $('hub-lease-machine').value=selectedMachine;
        $('hub-lease-request').disabled=!machines.length;
        $('hub-lease-rows').replaceChildren();
        const leases=state.reservations.filter(l=>
            l.issued_by==='experiment-hub' && l.status!=='released');
        for(const lease of leases.slice().reverse()) {
            const tr=document.createElement('tr');
            cell(tr,lease.project_id);
            cell(tr,lease.machine_id);
            cell(tr,lease.resources.cpus+' / '+lease.resources.ram_mb+' MiB');
            cell(tr,(lease.resources.gpus||[]).join(', ')||'None');
            cell(tr,lease.status==='offered'?'Awaiting agent receipt'
                :lease.receipt_fresh?'Agent receipt fresh'
                :'Acknowledged previously; receipt not fresh');
            const actions=cell(tr,'');
            const owner=(state.jobs||[]).find(j=>j.id===lease.job_id);
            if(owner&&!['waiting_adapter','finished','failed','stopped']
                .includes(owner.status)) {
                actions.textContent='Stop or verify managed job before release';
            }else actions.append(button('Release',async()=>{
                if(!confirm('Release the coordination lease for '+lease.project_id+
                    ' on '+lease.machine_id+
                    '? This only frees Hub scheduling capacity; it does not stop any process.'))return;
                try{
                    await action('hub/leases/release',{
                        lease_id:lease.id,confirm_release:true,
                    });
                    feedback('hub-lease-message',
                        'Lease released. Existing processes were not affected.');
                    await reload();
                }catch(e){feedback('hub-lease-message',e.message);}
            }));
            $('hub-lease-rows').append(tr);
        }
        if(!leases.length){
            const tr=document.createElement('tr');
            cell(tr,'No outstanding coordination leases.').colSpan=6;
            $('hub-lease-rows').append(tr);
        }
    }
    async function reload() {
        try {
            const r=await fetch('/api/hub/state',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Hub state unavailable');
            const apply=()=>{state=data;display();selectTab(tab);};
            if(tab==='compare')withCompareViewport(apply);
            else apply();
        }catch(e){feedback('hub-config-message',e.message);}
    }
    function populateBeesForm(p) {
        if(!p || typeof p!=='object' || !p.trainer)throw Error('Not a Bees training definition.');
        if(typeof closeQueuedEditor==='function')closeQueuedEditor();
        $('name').value=p.name||'';
        $('group').value=p.group||'';
        $('steps').value=p.max_steps||20000000;
        $('scenario-seed').value=p.scenario_seed??'';
        $('unlimited-steps').checked=p.unlimited_steps===true;
        $('minutes').value=p.max_minutes??'';
        $('stall').value=p.stall_minutes??'';
        $('args').value=(p.env_args||[]).join('\n');
        $('stage-name').value=p.stage_name||'Initial combat';
        $('stage-target').value=p.target_timeout_pct??'';
        $('stage-rows').replaceChildren();
        for(const st of p.stages||[])addCurriculumStage(st);
        if(typeof updateStageLabels==='function')updateStageLabels();
        $('evaluation-mode').value=p.evaluation_mode||'none';
        $('benchmark-target-enabled').checked=Boolean(p.benchmark_target);
        $('benchmark-target-wins').value=p.benchmark_target?.min_win_rate_pct??70;
        $('benchmark-target-timeouts').value=p.benchmark_target?.max_timeout_rate_pct??5;
        $('benchmark-target-matches').value=p.benchmark_target?.min_matches??100;
        $('benchmark-target-passes').value=p.benchmark_target?.consecutive_passes??2;
        $('reserve-heldout').checked=p.reserve_heldout===true;
        for(const option of $('bees-training-metrics').options)
            option.selected=(p.metric_selection||[]).includes(option.value);
        $('replicate-enabled').checked=Number(p.replicates||1)>1;
        $('replicate-count').value=p.replicates||3;
        for(const [key,val] of Object.entries(p.trainer.hyperparameters||{})) {
            if($('trainer-'+key))$('trainer-'+key).value=String(val);
        }
        $('trainer-num_layers').value=p.trainer.network_settings?.num_layers??3;
        $('trainer-normalize').value=String(p.trainer.network_settings?.normalize??false);
        $('trainer-seed').value=p.trainer.seed??0;
        feedback('message','Draft loaded. Review the training form before queuing.');
    }

    for(const b of document.querySelectorAll('[data-hub-go]'))
        b.addEventListener('click',()=>selectTab(b.dataset.hubGo));
    addComparisonSlot();
    addComparisonSlot();
    if(typeof ResizeObserver==='function') {
        const plotResize=new ResizeObserver(entries=>{
            const width=Math.round(entries[0].contentRect.width);
            if(width>0&&width!==lastComparisonChartWidth&&
                lastComparisonChart&&tab==='compare')
                drawComparisonCharts(lastComparisonChart.runs,
                    lastComparisonChart.keys,lastComparisonChart.xKey);
        });
        plotResize.observe($('hub-compare-plots'));
    }
    $('hub-compare-add-run').addEventListener('click',()=>{
        addComparisonSlot();
        renderComparison();
    });
    $('hub-compare-metric').addEventListener('change',()=>{
        comparisonMetricOverrides.set(project,selectedComparisonMetrics());
        renderComparison();
    });
    $('hub-compare-x-axis').addEventListener('change',renderComparison);
    for(const format of ['csv','json'])
        $('hub-compare-export-'+format).addEventListener('click',()=>{
            try{exportComparison(format);}
            catch(error){
                feedback('hub-compare-export-message',String(error.message||error));
            }
        });
    const replayChoice=$('hub-compare-export-replays');
    replayChoice.addEventListener('change',()=>{
        feedback('hub-compare-replays-note',replayChoice.checked
            ?'Replay inclusion requested for the next complete diagnostic ZIP export. Click Export complete job diagnostics to create it; only available replay files can be included.'
            :'Raw replay files are excluded. This option only applies to Export complete job diagnostics.');
    });
    const exportAllMetricsLabel=document.createElement('label');
    const exportAllMetricsToggle=document.createElement('input');
    exportAllMetricsToggle.type='checkbox';
    exportAllMetricsToggle.id='hub-export-all-recorded-metrics';
    exportAllMetricsLabel.append(exportAllMetricsToggle,
        document.createTextNode(' Export every recorded metric, including those not retained in charts'));
    $('hub-compare-export-full').insertAdjacentElement(
        'beforebegin',exportAllMetricsLabel);
    $('hub-compare-export-full').addEventListener('click',async()=>{
        try{await exportCompleteComparison();}
        catch(error){
            feedback('hub-compare-export-message',String(error.message||error));
        }
    });
    function candidateRankingText(decision) {
        if(!Array.isArray(decision.ranked)||!decision.ranked.length)
            throw Error('No comparable independently evaluated candidates.');
        return 'Equal-weight mean across identical frozen suites; differences are not proof of superiority.\n'+
            decision.ranked.map((candidate,i)=>
                (i+1)+'. '+candidate.job_id+' · '+
                (candidate.score*100).toFixed(2)+'% mean\n   '+
                candidate.suites.map(suite=>
                    suite.suite_id+': '+suite.successes+'/'+suite.trials+' ('+
                    (100*suite.successes/suite.trials).toFixed(2)+'%)').join(' | ')
            ).join('\n');
    }
    $('hub-compare-selection-preview').addEventListener('click',async()=>{
        try {
            const ids=compareSlots.map(slot=>$('hub-compare-'+slot).value).filter(Boolean);
            const decision=await action('hub/candidates/rank',{candidate_job_ids:ids});
            feedback('hub-compare-selection-ranking',candidateRankingText(decision));
        }catch(error) {
            feedback('hub-compare-selection-ranking',String(error.message||error));
        }
    });
    $('hub-compare-selection-queue').addEventListener('click',async()=>{
        try {
            if(!['courier','tinybrains'].includes(project))
                throw Error('Model promotion needs the Courier or TinyBrains adapter.');
            const candidateIds=compareSlots.map(slot=>$('hub-compare-'+slot).value)
                .filter(Boolean);
            if(candidateIds.length<2||new Set(candidateIds).size!==candidateIds.length)
                throw Error('Choose at least two distinct candidate runs.');
            const configuration=(state.configurations||[])
                .find(c=>c.project_id===project&&
                    c.id===$('hub-compare-selection-config').value);
            if(!configuration)throw Error('Select a saved configuration for the next stage.');
            const resources=JSON.parse($('hub-compare-selection-resources').value);
            const payload={
                candidate_job_ids:candidateIds,
                configuration_id:configuration.id,
                expected_revision:configuration.revision,resources,
            };
            const jobs=candidateIds.map(id=>state.jobs.find(j=>j.id===id));
            const complete=jobs.every(j=>j&&
                ['finished','stopped'].includes(j.status)&&j.capacity_released_at);
            if(complete) {
                const decision=await action('hub/candidates/rank',payload);
                const winner=decision.ranked[0];
                if(!confirm('Queue the best independently evaluated '+project+
                    ' candidate?\n'+candidateRankingText(decision)+
                    '\nThe next stage resumes only when the native model bytes still match the evaluated checkpoint.'))return;
                const result=await action('hub/candidates/promote',{
                    ...payload,expected_winner_job_id:winner.job_id,
                    confirm_queue:true,
                });
                feedback('hub-compare-selection-message',
                    'Queued exact-model continuation '+result.job.id+
                    ' from winning candidate '+winner.job_id+'.');
            }else {
                if(!confirm('Schedule '+candidateIds.length+' '+project+
                    ' candidates for automatic best-model selection when all complete? The coordinator will require identical suites, verified final model bytes and released capacity; invalid results require review.'))return;
                const plan=await action('hub/candidates/schedule',{
                    ...payload,confirm_queue:true,
                });
                feedback('hub-compare-selection-message',
                    'Scheduled selection '+plan.id+'. Automatic queueing requires an armed native scheduler.');
            }
            await reload();
        }catch(error){
            feedback('hub-compare-selection-message',String(error.message||error));
        }
    });
    $('hub-compare-selection-cancel-id').addEventListener('change',()=>{
        const id=$('hub-compare-selection-cancel-id').value;
        $('hub-compare-selection-review').disabled=!(state.candidate_selections||[])
            .some(p=>p.id===id&&p.status==='needs_review');
    });
    $('hub-compare-selection-review').addEventListener('click',async()=>{
        const id=$('hub-compare-selection-cancel-id').value;
        try {
            const plan=(state.candidate_selections||[]).find(p=>p.id===id);
            if(!plan||plan.status!=='needs_review')
                throw Error('Select a review-required candidate decision.');
            // A fresh ranking validates every frozen suite, model byte digest,
            // native completion and capacity release before operator consent.
            const decision=await action('hub/candidates/rank',{
                candidate_job_ids:plan.candidate_job_ids,
            });
            if(decision.project_id!==plan.project_id)
                throw Error('The candidate project changed; cancel this plan.');
            const winner=decision.ranked[0];
            if(!confirm('Resolve reviewed selection '+plan.id+' and queue the winning '+
                plan.project_id+' checkpoint?\n'+candidateRankingText(decision)+
                '\nThe original frozen next-stage configuration and resources will be used.'))return;
            const result=await action('hub/candidates/promote',{
                selection_plan_id:plan.id,
                candidate_job_ids:plan.candidate_job_ids,
                configuration_id:plan.configuration_id,
                expected_revision:plan.expected_revision,
                resources:plan.resources,
                expected_winner_job_id:winner.job_id,
                confirm_review:true,confirm_queue:true,
            });
            feedback('hub-compare-selection-message',
                'Reviewed selection '+plan.id+' queued pinned continuation '+result.job.id+'.');
            await reload();
        }catch(error){
            feedback('hub-compare-selection-message',String(error.message||error));
        }
    });
    $('hub-compare-selection-cancel').addEventListener('click',async()=>{
        const selectionId=$('hub-compare-selection-cancel-id').value;
        if(!selectionId)return;
        if(!confirm('Cancel pending candidate selection '+selectionId+
            '? Existing training jobs are unaffected.'))return;
        try {
            await action('hub/candidates/cancel',{
                selection_id:selectionId,confirm_cancel:true,
            });
            feedback('hub-compare-selection-message',
                'Candidate selection cancelled; existing jobs remain unchanged.');
            await reload();
        }catch(error){
            feedback('hub-compare-selection-message',String(error.message||error));
        }
    });
    $('hub-project').addEventListener('change',()=>{
        project=$('hub-project').value;resetEditor();display();selectTab(tab);
    });
    $('hub-config-cancel').addEventListener('click',resetEditor);
    $('hub-copy-bees-form').addEventListener('click',()=>{
        if(project!=='bees')return;
        try {
            const payload=trainingFormPayload();
            resetEditor();
            $('hub-config-name').value=payload.name||'Bees draft';
            $('hub-config-json').value=JSON.stringify(payload,null,2);
            feedback('hub-config-message','Copied current Bees training form. Save draft to retain it.');
        }catch(e){feedback('hub-config-message',e.message);}
    });
    $('hub-config-form').addEventListener('submit',async e=>{
        e.preventDefault();
        try {
            const saved=await action('hub/configurations',{
                ...(editing||{}),
                project_id:project,
                name:$('hub-config-name').value,
                parameters:JSON.parse($('hub-config-json').value),
                ...(project!=='bees'?{evaluation_stop:evaluationFromEditor()}:{}),
            });
            editing={id:saved.id,revision:saved.revision};
            feedback('hub-config-message','Saved revision '+saved.revision+'. No training scheduled.');
            await reload();
        }catch(err){feedback('hub-config-message',err.message);}
    });
    $('hub-save-bees-draft').addEventListener('click',async()=>{
        if (typeof editingId!=='undefined' && editingId) {
            feedback('message','Finish editing the queued experiment before saving a new draft.');
            return;
        }
        try {
            const params=trainingFormPayload();
            const title=params.name?.trim();
            if(!title)throw Error('Enter a training name before saving a draft.');
            const result=await action('hub/configurations',{
                project_id:'bees',name:title,parameters:params,
            });
            feedback('message','Saved '+result.name+' as an editable draft in Design. No job queued.');
            await reload();
        }catch(err){feedback('message',err.message);}
    });
    $('hub-project-form').addEventListener('submit',async e=>{
        e.preventDefault();
        try {
            const saved=await action('hub/projects',{
                id:$('hub-new-project-id').value.trim(),
                name:$('hub-new-project-name').value,
                description:$('hub-new-project-description').value,
            });
            project=saved.id;resetEditor();await reload();
        }catch(err){feedback('hub-config-message',err.message);}
    });
    $('hub-enrollment-clear').addEventListener('click',()=>{
        $('hub-enrollment-token').value='';
        feedback('hub-enrollment-message','Token display cleared. Rotate it if the token was lost.');
    });
    $('hub-machine-form').addEventListener('submit',async e=>{
        e.preventDefault();
        try {
            await action('hub/machines',{
                id:$('hub-machine-id').value.trim(),
                name:$('hub-machine-name').value,
                cpus:Number($('hub-machine-cpus').value),
                ram_mb:Number($('hub-machine-ram').value),
                gpus:JSON.parse($('hub-machine-gpus').value),
            });
            feedback('hub-placements','Machine registered as unverified. An agent must establish resource ownership before execution.');
            await reload();
        }catch(err){feedback('hub-placements',err.message);}
    });
    $('hub-budget-machine').addEventListener('change',()=>{
        budgetDraft=null;
        display();
    });
    $('hub-budget-form').addEventListener('submit',async e=>{
        e.preventDefault();
        const machine=state?.machines.find(m=>
            m.id===$('hub-budget-machine').value);
        if(!machine||!budgetDraft||machine.id!==budgetDraft.machine_id) {
            feedback('hub-budget-message','Select a registered machine.');
            return;
        }
        const cpus=Number($('hub-budget-cpus').value);
        const ram=Number($('hub-budget-ram').value);
        if(!confirm('Set the Hub scheduling budget for '+machine.name+
            ' to '+cpus+' CPU slots and '+ram+' MiB RAM? '+
            'This does not measure or reserve capacity for existing Bees, Ants, or other workloads. '+
            'Disarm automation first; active native jobs are not stopped or resized.'))return;
        try{
            const result=await action('hub/machines/budget',{
                machine_id:machine.id,cpus,ram_mb:ram,
                expected_cpus:budgetDraft.cpus,
                expected_ram_mb:budgetDraft.ram_mb,
                confirm_budget:true,
            });
            budgetDraft={machine_id:machine.id,cpus:result.cpus,
                ram_mb:result.ram_mb};
            feedback('hub-budget-message',
                'Hub budget '+result.cpus+' CPUs / '+result.ram_mb+
                ' MiB · Existing claims '+result.claimed_cpus+' CPUs / '+
                result.claimed_ram_mb+' MiB. No processes changed.');
            await reload();
        }catch(error){feedback('hub-budget-message',error.message);}
    });
    $('hub-gpu-budget-machine').addEventListener('change',()=>{
        gpuBudgetDraft=null;
        display();
    });
    $('hub-gpu-budget-form').addEventListener('submit',async event=>{
        event.preventDefault();
        const machine=state?.machines.find(m=>
            m.id===$('hub-gpu-budget-machine').value);
        if(!machine||!gpuBudgetDraft||gpuBudgetDraft.machine_id!==machine.id) {
            feedback('hub-gpu-budget-message','Select a registered GPU machine.');
            return;
        }
        let requested;
        try{requested=JSON.parse($('hub-gpu-budget-json').value);}
        catch(error){feedback('hub-gpu-budget-message',error.message);return;}
        if(!confirm('Change Hub GPU scheduling budgets on '+machine.name+
            '? This leaves capacity for unmanaged training only in the Hub ledger; '+
            'it does not enforce GPU memory or compute quotas. Disarm automation '+
            'and release outstanding GPU-bearing leases first.'))return;
        try {
            const result=await action('hub/machines/gpu-budget',{
                machine_id:machine.id,expected_gpus:gpuBudgetDraft.gpus,
                gpus:requested,confirm_budget:true,
            });
            gpuBudgetDraft=null;
            lastGpuBindingMachine=null;
            feedback('hub-gpu-budget-message',
                result.changed?'GPU admission budgets saved. No existing processes changed.':
                    'GPU admission budgets unchanged.');
            await reload();
        }catch(error){
            feedback('hub-gpu-budget-message',error.message);
        }
    });
    $('hub-gpu-bind-machine').addEventListener('change',()=>{
        lastGpuBindingMachine=null;
        display();
    });
    $('hub-gpu-bind-form').addEventListener('submit',async e=>{
        e.preventDefault();
        const machineId=$('hub-gpu-bind-machine').value;
        const machine=state?.machines.find(m=>m.id===machineId);
        if(!machine){
            feedback('hub-gpu-bind-message',
                'Select a connected machine with a fresh AMDGPU or NVIDIA report.');
            return;
        }
        let desired;
        try{desired=JSON.parse($('hub-gpu-bind-json').value);}
        catch(error){feedback('hub-gpu-bind-message',error.message);return;}
        if(!confirm('Replace logical GPU bindings on '+machine.name+
            '? Each PCI address must match the connected agent. '+
            'Any outstanding GPU leases block this change. '+
            'This does not enable GPU execution or hardware isolation.'))return;
        try{
            await action('hub/machines/gpus',{
                machine_id:machineId,gpus:desired,confirm_bind:true,
            });
            feedback('hub-gpu-bind-message',
                'Physical GPU bindings saved. This is identity-aware advisory scheduling only.');
            await reload();
        }catch(error){feedback('hub-gpu-bind-message',error.message);}
    });
    const shareMode=$('hub-request-gpu-mode');
    shareMode.addEventListener('change',()=>{
        const shared=shareMode.value==='shared';
        $('hub-request-share-vram').disabled=!shared;
        $('hub-request-share-compute').disabled=!shared;
    });
    function selectedResourceRequest(){
        const mode=shareMode.value;
        return {
            cpus:Number($('hub-request-cpus').value),
            ram_mb:Number($('hub-request-ram').value),
            gpus:$('hub-request-gpus').value.split(',').map(s=>s.trim()).filter(Boolean),
            gpu_count:Number($('hub-request-gpu-count').value),
            min_gpu_vram_mb:Number($('hub-request-gpu-vram').value),
            gpu_mode:mode,
            gpu_vram_mb:mode==='shared'
                ?Number($('hub-request-share-vram').value):0,
            gpu_compute_pct:mode==='shared'
                ?Number($('hub-request-share-compute').value):100,
        };
    }
    $('hub-lease-request').addEventListener('click',async()=>{
        const projectId=$('hub-lease-project').value;
        const machineId=$('hub-lease-machine').value;
        if(!machineId){
            feedback('hub-lease-message','Connect an authenticated machine first.');
            return;
        }
        if(!confirm('Create a persistent, advisory coordination lease for '+
            projectId+' on '+machineId+
            '? The agent will acknowledge receipt, but no hardware isolation or training execution is provided.'))return;
        try{
            const created=await action('hub/leases',{
                project_id:projectId,machine_id:machineId,
                resources:selectedResourceRequest(),confirm_request:true,
            });
            feedback('hub-lease-message',
                'Coordination lease issued: '+created.id+'. Awaiting agent receipt.');
            await reload();
        }catch(e){feedback('hub-lease-message',e.message);}
    });
    $('hub-preview').addEventListener('click',async()=>{
        try {
            const query=new URLSearchParams({
                cpus:$('hub-request-cpus').value,
                ram_mb:$('hub-request-ram').value,
                gpus:$('hub-request-gpus').value.trim(),
                gpu_count:$('hub-request-gpu-count').value,
                min_gpu_vram_mb:$('hub-request-gpu-vram').value,
                gpu_mode:shareMode.value,
                gpu_vram_mb:shareMode.value==='shared'
                    ?$('hub-request-share-vram').value:'0',
                gpu_compute_pct:shareMode.value==='shared'
                    ?$('hub-request-share-compute').value:'100',
            });
            const r=await fetch('/api/hub/placements?'+query,{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'No placement result.');
            feedback('hub-placements',(data.placements||[]).map(p=>
                p.machine_name+': '+(p.capacity_fits?'Advisory capacity fits':'Unavailable')+
                (p.suggested_gpus?.length?' · GPUs '+p.suggested_gpus.join(', '):'')+
                placementObservedLoad(p)+
                (p.observed_headroom_block?' · Current Start blocked: '+p.observed_headroom_block:'')+
                ' — '+p.reason
            ).join('\n')||'No registered machines.');
        }catch(e){feedback('hub-placements',e.message);}
    });

    $('hub-automation-arm').addEventListener('click',async()=>{
        const ids=[...$('hub-automation-machines').querySelectorAll('input:checked')]
            .map(input=>input.value);
        if(!ids.length) {
            feedback('hub-automation-status','Select at least one native-capable machine.');
            return;
        }
        const adopt=$('hub-automation-adopt-running').checked;
        const resume=$('hub-automation-resume-after-restart').checked;
        const rebalance=$('hub-automation-rebalance').checked;
        const gpu=$('hub-automation-gpu').checked;
        const gpuClaim=$('hub-automation-gpu-claim').checked;
        const gpuProbe=$('hub-automation-gpu-probe').checked;
        if(gpuProbe&&!gpuClaim) {
            feedback('hub-automation-status','Enable GPU claims to automate Linux device-access probes.');
            return;
        }
        if(resume&&(gpu||gpuClaim||gpuProbe)) {
            feedback('hub-automation-status','Unattended restart restoration currently supports CPU-only automation.');
            return;
        }
        if(!confirm('Arm native queue automation on '+ids.join(', ')+
            '? This will start eligible CPU jobs and release verified completed capacity. You confirm that unmanaged host workloads have been accounted for. Restart disarms automation by default.'+
            (resume?' You explicitly authorize CPU-only scheduling recovery after coordinator or agent restart, after fresh native and lease proof. This does not enable machine boot startup. Uncertain jobs or changed credentials cancel the permission.':'')+
            (adopt?' Previously automated running or completed jobs will be re-adopted ONLY after fresh, same-dispatch native inspection and verified lease ownership. Uncertain jobs block arming; existing native processes will not be relaunched.':'')+
            (gpu?' This session may also start eligible Linux or Windows GPU jobs with separately verified sharing support.':'')+
            (gpuClaim?' This session may claim exclusive GPU or locally approved cooperative CUDA/ROCm shared capacity.':'')+
            (gpuProbe?' It may run inert Linux device-denial probes for qualified exclusive or shared GPU claims.':'')+
            (rebalance?' This session may move its own unstarted claims between armed hosts after a capacity drop.':'')))return;
        try {
            await action('hub/automation',{
                enabled:true,machine_ids:ids,rebalance_queued:rebalance,
                adopt_reconciled_running:adopt,confirm_adopt_running:adopt,
                resume_after_restart:resume,confirm_restart_rearm:resume,
                confirm_agent_restart_rearm:resume,
                start_qualified_gpu:gpu,claim_exclusive_gpu:gpuClaim,probe_exclusive_gpu:gpuProbe,
                confirm_arm:true,confirm_untracked_load:true,
            });
            await reload();
        }catch(error){feedback('hub-automation-status',error.message);}
    });
    $('hub-automation-disarm').addEventListener('click',async()=>{
        if(!confirm('Disarm future automatic allocations, Start directives, and any pending restart permission? Already delivered Starts may still be executing; existing processes are NOT stopped and capacity claims remain held. Use manual Stop and native inspection to resolve suspended Start requests.'))return;
        try {
            await action('hub/automation',{enabled:false,confirm_disarm:true});
            await reload();
        }catch(error){feedback('hub-automation-status',error.message);}
    });
    $('hub-apply-claim-moves').addEventListener('click',async()=>{
        const moves=[...stagedMoves.values()];
        if(applyingMoves||!moves.length)return;
        if(moves.some(move=>state?.jobs?.find(job=>job.id===move.job_id)
            ?.gpu_qualification?.holds_claim)) {
            feedback('hub-job-allocate-message',
                'A staged claim is held by a GPU probe. Wait for verified cleanup or remove that move.');
            return;
        }
        if(!confirm('Apply these '+moves.length+' queued claim moves together?\n'+
            moves.map(move=>move.label).join('\n')+
            '\nNew claims need fresh worker receipts. An armed scheduler can then start eligible CPU jobs.'))return;
        applyingMoves=true;
        renderStagedMoves();
        try {
            const result=await action('hub/jobs/move-claims',{
                moves:moves.map(({job_id,expected_lease_id,machine_id})=>
                    ({job_id,expected_lease_id,machine_id})),confirm_move:true,
            });
            for(const move of moves)
                if(stagedMoves.get(move.job_id)===move)stagedMoves.delete(move.job_id);
            feedback('hub-job-allocate-message','Moved '+result.moves.length+' claims together. Awaiting fresh worker receipts.');
        }catch(error){
            feedback('hub-job-allocate-message',error.message);
        }finally{
            applyingMoves=false;
            renderStagedMoves();
            await reload();
        }
    });
    $('hub-global-allocate').addEventListener('click',async()=>{
        const projects=new Set((state.jobs||[]).filter(j=>
            j.adapter==='unconfigured'&&j.status==='waiting_adapter'&&!j.lease_id)
            .map(j=>j.project_id));
        if(!projects.size){
            feedback('hub-global-allocate-result','No unclaimed pending jobs.');
            return;
        }
        if(!confirm('Coordinate remaining Hub capacity across '+projects.size+
            ' non-Bees project(s)? This makes durable bookkeeping claims only; no workloads launch and no OS resource isolation is provided.'))return;
        try{
            const result=await action('hub/jobs/allocate',{
                all_projects:true,confirm_allocate:true,
            });
            const allocated=result.results.filter(x=>x.status==='claimed').length;
            const blocked=allocationBlocks(result.results);
            feedback('hub-global-allocate-result',
                allocated+' new advisory claims across projects'+
                (blocked?' · '+blocked:'')+' · No processes started.');
            await reload();
        }catch(e){feedback('hub-global-allocate-result',e.message);}
    });
    $('hub-plan').addEventListener('click',async()=>{
        try {
            const jobs=JSON.parse($('hub-plan-json').value);
            const result=await action('hub/plan',{jobs});
            feedback('hub-plan-result',result.placements.map(p=>
                p.id+': '+(p.capacity_fits
                    ?p.machine_name+(p.suggested_gpus.length
                        ?' · '+p.suggested_gpus.join(', '):'')
                    :'No fit')+
                placementObservedLoad(p)+
                (p.observed_headroom_block?' · Current Start blocked: '+p.observed_headroom_block:'')+
                ' — '+p.reason
            ).join('\n')||'No workloads entered. No changes made.');
        }catch(e){feedback('hub-plan-result',e.message);}
    });

    $('hub-resource-bundle-add').addEventListener('click',()=>{
        addResourceBundleMember();
        renderResourceBundleMembers();
    });
    $('hub-resource-bundle-queue').addEventListener('click',async()=>{
        if(!state||project==='bees')return;
        try{
            const rows=[...$('hub-resource-bundle-rows').children];
            if(rows.length<2||rows.length>16)
                throw Error('Select 2–16 members.');
            const members=rows.map(row=>{
                const cfg=state.configurations.find(c=>c.id===
                    row.querySelector('.hub-bundle-config').value);
                const machine=row.querySelector('.hub-bundle-machine').value;
                if(!cfg||!machine)throw Error('Select a saved configuration and qualified machine for every member.');
                return {configuration_id:cfg.id,expected_revision:cfg.revision,
                    machine_id:machine,
                    resources:JSON.parse(row.querySelector('.hub-bundle-resources').value)};
            });
            if(!confirm('Atomically reserve fixed resources for '+members.length+
                ' independent jobs on the selected machines? Each job requires its own native Start authorization. '+
                'This does not enable distributed training, automatic checkpoint sharing or simultaneous worker starts.'))return;
            const result=await action('hub/resource-bundles',{
                confirm_queue:true,members,
            });
            feedback('hub-resource-bundle-message',
                'Reserved all '+result.members.length+' jobs · bundle '+
                result.bundle_id.slice(-8)+'. No Starts authorized.');
            await reload();
        }catch(error){
            feedback('hub-resource-bundle-message',String(error.message||error));
        }
    });
    $('hub-pipeline-add').addEventListener('click',()=>{
        addPipelineStage();
        renderPipelineStages();
    });
    $('hub-pipeline-predecessor').addEventListener('change',renderPipelineStages);
    $('hub-pipeline-kind').addEventListener('change',display);
    $('hub-pipeline-queue').addEventListener('click',async()=>{
        if(project==='bees'||!state)return;
        try {
            const checkpointPipeline=['tinybrains','courier'].includes(project)&&
                $('hub-pipeline-kind').value==='continuation';
            const trialBatch=$('hub-pipeline-kind').value==='batch';
            const configs=state.configurations.filter(c=>checkpointPipeline
                ?c.project_id===project:!['bees','ants'].includes(c.project_id));
            const rows=[...$('hub-pipeline-rows').children];
            if(rows.length<2||rows.length>16)
                throw Error('Choose 2–16 jobs for this sequence.');
            const stages=rows.map(row=>{
                const selected=configs.find(c=>
                    c.id===row.querySelector('select').value);
                if(!selected)throw Error('Select a saved configuration for every stage.');
                return {configuration_id:selected.id,
                    expected_revision:selected.revision,
                    resources:JSON.parse(row.querySelector('input').value)};
            });
            const predecessor=trialBatch?'':$('hub-pipeline-predecessor').value;
            const question=checkpointPipeline
                ?'Queue '+stages.length+' frozen checkpoint-continuation stages? They share verified checkpoint lineage and original machine.'
                :trialBatch?'Queue '+stages.length+' frozen independent trials? Each is eligible as capacity becomes available.':
                    'Queue '+stages.length+' independent jobs as a frozen sequence? No job inherits any previous checkpoint, machine affinity, optimizer state or evaluation evidence.';
            if(!confirm(question+
                (checkpointPipeline
                    ?' Each stage waits for its predecessor to finish and release capacity.'
                    :trialBatch?'':' Each stage waits for a verified finish or evaluation-driven Stop and capacity release.')+
                ' No work starts without the separate native authorization gates.'))return;
            const result=await action(checkpointPipeline?'hub/pipelines':trialBatch?'hub/trials':'hub/sequences',{
                confirm_queue:true,stages,
                ...(predecessor?(checkpointPipeline
                    ?{continuation_of_job_id:predecessor}
                    :{after_job_id:predecessor}):{}),
            });
            feedback('hub-pipeline-message',
                'Queued '+result.jobs.length+' jobs atomically; no work started. '+
                (trialBatch?'Batch '+result.trial_batch_id.slice(-8)+'. Every trial is independently eligible for capacity.':
                    'Only the first eligible job may request capacity.'));
            await reload();
        }catch(error){
            feedback('hub-pipeline-message',String(error.message||error));
        }
    });
    // Optional resource builder: author only the existing frozen resource
    // request. No lease or native execution is implied by filling the JSON.
    const builderMode=$('hub-build-gpu-mode');
    const updateBuilder=()=>{
        const shared=builderMode.value==='shared';
        $('hub-build-share-vram').disabled=!shared;
        $('hub-build-share-compute').disabled=!shared;
    };
    builderMode.addEventListener('change',updateBuilder);
    $('hub-build-resources').addEventListener('click',()=>{
        try {
            const integer=(id,min,max)=>{
                const value=Number($(id).value);
                if(!Number.isSafeInteger(value)||value<min||value>max)
                    throw Error('Enter a whole number between '+min+' and '+max+' for '+id.replace('hub-build-','')+'.');
                return value;
            };
            const cpus=integer('hub-build-cpus',1,4096);
            const ram_mb=integer('hub-build-ram',128,100000000);
            const gpu_count=integer('hub-build-gpu-count',0,2);
            const mode=builderMode.value;
            if(gpu_count===2&&mode==='shared')
                throw Error('Two-GPU TinyBrains training requires exclusive GPUs; shared mode supports one GPU.');
            const min_gpu_vram_mb=integer('hub-build-gpu-min-vram',0,1000000);
            const raw=$('hub-build-gpu-ids').value.trim();
            const gpus=raw?raw.split(',').map(s=>s.trim()):[];
            if(gpus.some(id=>!/^[a-z][a-z0-9_-]{0,47}$/.test(id))||
                new Set(gpus).size!==gpus.length||
                (gpus.length&&gpus.length!==gpu_count))
                throw Error('GPU IDs must be unique registered identifiers matching the requested GPU count.');
            if(!gpu_count&&(mode==='shared'||min_gpu_vram_mb||gpus.length))
                throw Error('Zero-GPU jobs cannot request GPU memory, IDs or shared allocation.');
            const resources={cpus,ram_mb,gpu_count};
            if(gpu_count){
                resources.gpu_mode=mode;
                if(min_gpu_vram_mb)resources.min_gpu_vram_mb=min_gpu_vram_mb;
                if(gpus.length)resources.gpus=gpus;
                if(mode==='shared') {
                    resources.gpu_vram_mb=integer('hub-build-share-vram',1,1000000);
                    resources.gpu_compute_pct=integer('hub-build-share-compute',1,100);
                }
            }
            $('hub-job-resources').value=JSON.stringify(resources,null,2);
            feedback('hub-build-message','Resource JSON filled. Review it before queueing; no work was started.');
        }catch(error){
            feedback('hub-build-message',String(error.message||error));
        }
    });
    // Distributed groups are frozen and claimed atomically. The operator
    // chooses the CPU/RAM/GPU budgets; endpoint-to-machine identity is never
    // entered by the browser and comes from connected native agents instead.
    const distributedPanel=document.createElement('fieldset');
    const distributedLegend=document.createElement('legend');
    distributedLegend.textContent='Distributed TinyBrains learner + remote collectors';
    const distributedMachine=document.createElement('select');
    const distributedBudgets=document.createElement('textarea');
    distributedBudgets.rows=5;
    distributedBudgets.placeholder='Worker resource budgets, in endpoint order';
    const distributedDetails=document.createElement('div');
    const distributedFeedback=document.createElement('div');
    const distributedMachineLabel=document.createElement('label');
    distributedMachineLabel.append('Learner machine ',distributedMachine);
    const distributedBudgetsLabel=document.createElement('label');
    distributedBudgetsLabel.append('Remote worker budgets (JSON array) ',distributedBudgets);
    const distributedQueue=button('Queue and reserve entire group',async()=>{
        const selected=state?.configurations.find(c=>
            c.project_id==='tinybrains'&&c.id===$('hub-job-configuration').value);
        if(!selected||selected.parameters?.remote_rollouts!==true||
            !distributedMachine.value)return;
        try {
            const predecessor=$('hub-job-predecessor').value;
            if(predecessor) {
                const parent=state.jobs.find(j=>j.id===predecessor);
                if(!parent||!parent.capacity_released_at||
                    parent.assigned_machine_id!==distributedMachine.value||
                    !(parent.status==='finished'&&parent.checkpoint_ready_verified===true||
                      parent.status==='stopped'&&parent.checkpoint_stop_verified===true))
                    throw Error('Distributed continuation requires a released, checkpoint-verified learner on this same machine.');
                if(selected.parameters.import_checkpoint!==undefined)
                    throw Error('Save a continuation configuration without the original checkpoint import.');
            }
            const budgets=JSON.parse(distributedBudgets.value);
            const selectedMachine=state.machines.find(m=>m.id===distributedMachine.value);
            if(!Array.isArray(budgets)||budgets.length!==
                selectedMachine?.tinybrains_remote_rollout_endpoints?.length)
                throw Error('Supply one resource budget for each physically approved remote collector.');
            const resources=JSON.parse($('hub-job-resources').value);
            // The distributed queue must preserve the same Train metrics as
            // ordinary queueing. Prioritize paired collector evidence for
            // a subsequent *advisory* allocation recommendation.
            const metricSelection=hubDistributedMetricSelection(
                selectedJobMetrics(),budgets.length);
            if(!confirm('Queue '+(predecessor?'checkpoint continuation':'new TinyBrains learner')+
                ' on '+distributedMachine.value+
                ' and '+budgets.length+' remotely pinned collectors? All CPU, RAM and GPU'+
                ' reservations will be created together, but no process will start.'))return;
            const result=await action('hub/distributed/tinybrains',{
                confirm_queue:true,
                learner:{configuration_id:selected.id,expected_revision:selected.revision,
                    machine_id:distributedMachine.value,resources,
                    metric_selection:metricSelection,
                    ...(predecessor?{continuation_of_job_id:predecessor}:{})},
                collectors:budgets.map(item=>{
                    if(!item||typeof item!=='object'||Array.isArray(item))
                        throw Error('Each collector budget must be a JSON object.');
                    const {threads,...resources}=item;
                    return {resources,...(threads===undefined?{}:{threads})};
                }),
            });
            distributedFeedback.textContent='Queued learner '+result.learner.id+
                ' and '+result.collectors.length+' separate collector jobs. All '+
                result.leases.length+' claims await native receipts and explicit Starts.';
            await reload();
        }catch(error){distributedFeedback.textContent=String(error.message||error);}
    });
    distributedPanel.append(distributedLegend,distributedMachineLabel,
        distributedBudgetsLabel,distributedDetails,distributedQueue,distributedFeedback);
    $('hub-job-queue').insertAdjacentElement('afterend',distributedPanel);
    let distributedBudgetHost=null;
    function renderDistributedGroup() {
        if(!state)return;
        const selected=state.configurations.find(c=>
            c.project_id==='tinybrains'&&c.id===$('hub-job-configuration').value);
        distributedPanel.hidden=project!=='tinybrains'||
            selected?.parameters?.remote_rollouts!==true;
        if(distributedPanel.hidden)return;
        const prior=distributedMachine.value;
        const machines=state.machines.filter(m=>m.status==='connected'&&
            m.agent_platform==='linux'&&m.local_executor_enabled&&
            m.native_preflight_passed&&m.tinybrains_profiles?.[selected.parameters.profile]===
                selected.parameters.profile_sha256&&
            m.tinybrains_remote_rollout_profiles?.includes(selected.parameters.profile)&&
            m.tinybrains_remote_rollout_endpoints?.length);
        distributedMachine.replaceChildren(...machines.map(m=>new Option(
            (m.name||m.id)+' ('+m.id+') · '+
                m.tinybrains_remote_rollout_endpoints.length+' collectors',m.id)));
        if(machines.some(m=>m.id===prior))distributedMachine.value=prior;
        const machine=machines.find(m=>m.id===distributedMachine.value);
        if(distributedBudgetHost!==machine?.id) {
            distributedBudgetHost=machine?.id||null;
            distributedBudgets.value=JSON.stringify(
                (machine?.tinybrains_remote_rollout_endpoints||[]).map(()=>
                    ({cpus:2,ram_mb:4096,gpu_count:0,threads:2})),null,2);
        }
        const endpoints=machine?.tinybrains_remote_rollout_endpoints||[];
        const workers=endpoints.map(endpoint=>state.machines.filter(m=>
            m.status==='connected'&&m.id!==machine.id&&
            m.tinybrains_rollout_endpoint===endpoint&&
            m.tinybrains_rollout_key_fingerprint===machine.tinybrains_remote_key_fingerprint&&
            m.installed_projects?.includes('tinybrains_rollout')));
        const ready=Boolean(machine&&
            state.projects.some(p=>p.id==='tinybrains_rollout')&&
            workers.every(m=>m.length===1)&&
            new Set(workers.map(m=>m[0]?.id)).size===workers.length);
        distributedQueue.disabled=!ready;
        distributedDetails.textContent=ready
            ?'Approved collectors: '+workers.map((items,i)=>
                endpoints[i]+' on '+items[0].id).join('; ')+
                '. Modify each CPU/RAM budget, its independent threads setting, or request an exclusive CUDA GPU.'
            :'Register the tinybrains_rollout project and connect one distinct, matching worker for every approved endpoint.';
    }
    $('hub-job-configuration').addEventListener('change',renderDistributedGroup);
    distributedMachine.addEventListener('change',renderDistributedGroup);
    $('hub-job-queue').addEventListener('click',async()=>{
        const selected=state?.configurations.find(c=>
            c.project_id===project&&c.id===$('hub-job-configuration').value);
        if(!selected){
            feedback('hub-job-message','Save a configuration in Design first.');
            return;
        }
        const predecessor=$('hub-job-predecessor').value;
        const parent=state.jobs.find(j=>j.id===predecessor);
        const prequeue=Boolean(parent&&!parent.capacity_released_at);
        if(project==='tinybrains') {
            const parameters=selected.parameters;
            if(predecessor&&parameters.import_checkpoint!==undefined) {
                feedback('hub-job-message',
                    'A TinyBrains checkpoint continuation cannot import a separate seed. '+
                    'Select a saved configuration without import_checkpoint.');
                return;
            }
            if(!predecessor&&parameters.import_checkpoint===undefined) {
                const matching=state.machines.filter(machine=>
                    machine.status==='connected'&&
                    machine.tinybrains_profiles?.[parameters.profile]===
                        parameters.profile_sha256);
                if(matching.length&&matching.every(machine=>
                    machine.tinybrains_required_import_profiles?.includes(parameters.profile))) {
                    feedback('hub-job-message',
                        'This TinyBrains profile needs an approved seed checkpoint for a new run. '+
                        'Select a verified predecessor or save a configuration with an approved import.');
                    return;
                }
            }
        }
        if(!confirm('Freeze '+selected.name+' revision '+selected.revision+
            ($('hub-job-run-name').value.trim()
                ?' as run "'+$('hub-job-run-name').value.trim()+'"':'')+
            (predecessor?' as a continuation of the selected predecessor':
                ' as a new job')+
            '? '+(prequeue
                ?'This stage remains blocked until its predecessor FINISHES and capacity is released. Stops and failures do not auto-advance it. '
                :'')+
            'Queueing alone does not authorize execution.'))return;
        try{
            const metrics=selectedJobMetrics();
            const job=await action('hub/jobs',{
                configuration_id:selected.id,expected_revision:selected.revision,
                resources:JSON.parse($('hub-job-resources').value),
                ...($('hub-job-run-name').value.trim()
                    ?{run_name:$('hub-job-run-name').value.trim()}:{}),
                ...(metrics.length?{metric_selection:metrics}:{}),
                ...(predecessor?{continuation_of_job_id:predecessor}:{}),
            });
            $('hub-job-run-name').value='';
            feedback('hub-job-message',job.status==='waiting_predecessor'
                ?'Prequeued stage '+job.id+'. It cannot claim capacity or start until the predecessor finishes and releases capacity.'
                :'Queued job '+job.id+'. Coordinate capacity before explicitly authorizing native execution.');
            await reload();
        }catch(e){feedback('hub-job-message',e.message);}
    });
    async function allocateJob(jobIds=null) {
        const count=jobIds?.length||state.jobs.filter(j=>
            j.project_id===project&&j.status==='waiting_adapter'&&!j.lease_id).length;
        if(!count){
            feedback('hub-job-allocate-message','No unclaimed queued jobs.');
            return;
        }
        if(!confirm('Coordinate Hub capacity for '+count+' queued '+project+
            ' job(s)? This will create durable advisory claims, NOT enforce resource limits or start work.'))return;
        try{
            const chosen=jobIds?.length===1 ?$('hub-job-machine').value:'';
            const result=await action('hub/jobs/allocate',{
                project_id:project,
                ...(jobIds?{job_ids:jobIds}:{}),
                ...(chosen?{machine_id:chosen}:{}),
                confirm_allocate:true,
            });
            const allocated=result.results.filter(x=>x.status==='claimed').length;
            const blocked=allocationBlocks(result.results);
            feedback('hub-job-allocate-message',allocated+' coordination claims created'+
                (blocked?' · '+blocked:'')+' · No execution.');
            await reload();
        }catch(e){feedback('hub-job-allocate-message',e.message);}
    }
    $('hub-job-allocate-all').addEventListener('click',()=>allocateJob());
    $('hub-job-preview').addEventListener('click',async()=>{
        try{
            const r=await fetch('/api/hub/jobs/plan',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Job preview unavailable.');
            const selected=(data.jobs||[]).filter(j=>j.project_id===project);
            feedback('hub-job-plan',selected.map(j=>
                j.configuration_name+': '+
                (j.claimed
                    ?('Hub claim on '+(j.machine_name||j.machine_id)+
                        ' · '+(j.receipt_fresh?'agent receipt fresh':'receipt unverified'))
                    :j.capacity_fits?(j.machine_name+' (hypothetical placement)'):'No capacity fit')+
                ' · Waiting for trusted adapter; no execution'
            ).join('\n')||'No pending jobs for this project.');
        }catch(e){feedback('hub-job-plan',e.message);}
    });

    async function loadMonitorBees() {
        try {
            const r=await fetch('/api/hub/bees/control',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Bees control unavailable.');
            const kind=data.experiment_owned?'Experiment-managed':
                data.training_enabled?'Ordinary Bees training':'No training active';
            feedback('hub-monitor-training',kind+
                ' · Run '+(data.run_id||'not yet built')+
                (data.pending_release?' · Release pending':'')+
                (data.operator_operation?.status==='running'
                    ?' · '+data.operator_operation.kind+' in progress':''));
        }catch(err){feedback('hub-monitor-training',err.message);}
    }
    async function loadLiveBeesWorkers() {
        try {
            const r=await fetch('/api/hub/bees/workers',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Bees workers unavailable.');
            $('hub-live-bees-rows').replaceChildren();
            for(const w of data.workers||[]) {
                const tr=document.createElement('tr');
                cell(tr,w.trainer_id);cell(tr,w.role);cell(tr,w.platform);
                cell(tr,w.state);cell(tr,w.envs??'—');
                cell(tr,w.cpu_pct??'—');cell(tr,w.ram_pct??'—');
                cell(tr,w.accepted_s??'—');cell(tr,w.error||'');
                $('hub-live-bees-rows').append(tr);
            }
            feedback('hub-live-bees-note',data.workers?.length
                ?'Live Bees worker reports for run '+(data.run_id||'unknown')
                :'No Bees trainers currently registered.');
        }catch(err){feedback('hub-live-bees-note',err.message);}
    }
    $('hub-live-bees-refresh').addEventListener('click',loadLiveBeesWorkers);
    $('hub-bees-env-args').addEventListener('input',()=>{envArgsTouched=true;});

    async function loadBeesControl(){
        if(project!=='bees'||tab!=='train')return;
        try {
            const r=await fetch('/api/hub/bees/control',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Bees control unavailable.');
            const op=data.operator_operation;
            if(!envArgsTouched) $('hub-bees-env-args').value=
                (data.env_args||[]).join('\n');
            feedback('hub-bees-control-status','Run: '+(data.run_id||'None')+' · '+
                (data.training_enabled?'Training active':'Training stopped')+
                (data.experiment_owned?' · Experiment manager owns this run':''));
            feedback('hub-operator-message',op?op.kind+' · '+op.status+' · '+op.message:'');
            const blocked=data.experiment_owned||data.queue_armed||
                data.pending_release||op?.status==='running';
            $('hub-bees-resume').disabled=blocked||data.training_enabled||
                data.initial_build_required;
            $('hub-bees-new').disabled=blocked||data.training_enabled||
                data.initial_build_required;
            $('hub-bees-build').disabled=blocked||data.training_enabled;
            $('hub-bees-update-workers').disabled=blocked||data.training_enabled||
                !data.run_id;
            $('hub-bees-stop').disabled=blocked||!data.training_enabled;
            $('hub-bees-env-args').disabled=blocked||data.training_enabled;
        }catch(err){feedback('hub-bees-control-status',err.message);}
    }
    async function beesCommand(kind,params,question){
        if(!confirm(question))return;
        try {
            await action('hub/bees/'+kind,params);
            feedback('hub-operator-message','Operation accepted by Bees operator.');
            await loadBeesControl();
        }catch(e){feedback('hub-operator-message',e.message);}
    }
    function currentEnvArgs() {
        return $('hub-bees-env-args').value.split('\n').map(s=>s.trim()).filter(Boolean);
    }
    $('hub-bees-resume').addEventListener('click',()=>beesCommand('start',{
        mode:'resume',env_args:currentEnvArgs(),confirm_environment_change:true,
    },'Start or resume the current Bees run? Changing these environment arguments can affect its scientific comparability; the optimizer lineage will be preserved.'));
    $('hub-bees-new').addEventListener('click',()=>beesCommand('start',{
        mode:'new',confirm_new_run:true,env_args:currentEnvArgs(),
    },'Create a NEW Bees run and optimizer using the entered Unity environment arguments? Previous checkpoints are retained.'));
    $('hub-bees-build').addEventListener('click',()=>beesCommand('build',{confirm_build:true},
        'Compile new Windows and Linux Unity RL builds? Compatibility changes may require a new run.'));
    $('hub-bees-stop').addEventListener('click',()=>beesCommand('stop',{confirm_stop:true},
        'Gracefully stop ordinary Bees training and preserve its checkpoints?'));
    $('hub-bees-update-workers').addEventListener('click',()=>beesCommand(
        'update-workers',{confirm_update:true},
        'Publish updated remote worker software for this stopped Bees run? The worker release and run identity are verified by the existing operator.'));

    selectTab('monitor');
    reload();
    setInterval(()=>{
        reload();
        if(tab==='train')loadBeesControl();
        if(tab==='monitor'&&project==='bees')loadMonitorBees();
        if(tab==='machines')loadLiveBeesWorkers();
    },10000);
});
