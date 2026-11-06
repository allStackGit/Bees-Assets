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
function hubComparisonPoints(run,level='') {
    const points=run.points.length?run.points:
        [run.job.live_execution?.progress||run.job.final_progress]
            .filter(Boolean);
    if(level==='')return points;
    const wanted=Number(level);
    let last=-1;
    return points.flatMap((point,i)=>{
        if(point.curriculum_level!==wanted)return [];
        const item={...point,
            break_before:Boolean(point.break_before||(last>=0&&i>last+1))};
        last=i;
        return [item];
    });
}
// Descriptive screening only. Bounded, decimated observations cannot
// establish the true first crossing or a held-out benchmark outcome.
function hubObservedThreshold(run,level,threshold,minWindow) {
    if(level===''||!Number.isSafeInteger(Number(level))||
        !Number.isFinite(threshold)||threshold<0||threshold>1||
        !Number.isSafeInteger(minWindow)||minWindow<1)
        return null;
    const points=hubComparisonPoints(run,level).filter(p=>
        Number.isFinite(p.curriculum_window_success_rate)&&
        p.curriculum_window_success_rate>=0&&
        p.curriculum_window_success_rate<=1&&
        Number.isSafeInteger(p.curriculum_window_episodes)&&
        p.curriculum_window_episodes>=minWindow&&
        Number.isSafeInteger(p.episodes_completed)&&p.episodes_completed>=0);
    return {observations:points.length,
        first:points.find(p=>p.curriculum_window_success_rate>=threshold)||null,
        last:points[points.length-1]||null};
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
function hubMetricValue(point,key) {
    return key.startsWith('metric:')
        ?point.metrics?.[key.slice(7)]?.value
        :point[key];
}
function hubComparisonCsv(exported) {
    const fields=[
        'slot','job_id','configuration_name','configuration_sha256',
        'selected_x_axis','selected_metric','selected_curriculum_level',
        'assigned_machine_id','artifact_lineage_root_id',
        'continuation_of_job_id','status','observed_at','source_modified_at',
        'experiment_id','segment_id','break_before','episodes_completed',
        'total_environment_steps','policy_version','curriculum_level',
        'curriculum_window_episodes','curriculum_window_success_rate',
        'segment_success_rate','simulation_steps_per_second',
        'ppo_unique_samples_per_second','latest_episode_policy_updated',
    ];
    if(exported.project_id!=='ants') {
        fields.push('project_id','steps_completed');
        const metrics=new Set(exported.runs.flatMap(run=>
            run.points.flatMap(point=>Object.keys(point.metrics||{}))));
        for(const key of [...metrics].sort()) {
            fields.push('metric_'+key,'unit_'+key);
        }
    }
    const encode=value=>{
        if(value==null)return '';
        let str=String(value);
        // Prevent spreadsheet software from interpreting user-supplied
        // configuration labels as formulas.
        if(str.trimStart().length&&'=+-@'.includes(str.trimStart()[0]))
            str="'"+str;
        const quote=str.includes(',')||str.includes('"')||
            str.includes(String.fromCharCode(10))||
            str.includes(String.fromCharCode(13));
        return quote?'"'+str.replaceAll('"','""')+'"':str;
    };
    const rows=exported.runs.flatMap(run=>run.points.map(point=>({
        slot:run.slot,job_id:run.job_id,project_id:exported.project_id,
        configuration_name:run.configuration_name,
        configuration_sha256:run.configuration_sha256,
        selected_x_axis:exported.x_axis,
        selected_metric:exported.metric,
        selected_curriculum_level:exported.curriculum_level,
        assigned_machine_id:run.assigned_machine_id,
        artifact_lineage_root_id:run.artifact_lineage_root_id,
        continuation_of_job_id:run.continuation_of_job_id,
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
            const current=chain.find(job=>job.status!=='finished'||
                !job.capacity_released_at)||chain[chain.length-1];
            const blocked=chain.find(job=>job.status==='waiting_predecessor');
            return {root,chain,current,canCancel:!conflict,
                completed:chain.filter(job=>job.status==='finished'&&
                    job.capacity_released_at).length,
                reason:conflict?'Conflicting or cyclic successor records; inspect the registry.'
                    :blocked?.predecessor_wait?.reason||
                        current?.status_reason||'—'};
        });
}

function approvedTinyBrainsProfiles(machines) {
    const profiles=new Map();
    for(const machine of machines) {
        if(machine.status!=='connected'||machine.agent_platform!=='win32')continue;
        for(const [profile,digest] of Object.entries(machine.tinybrains_profiles||{})) {
            const key=profile+':'+digest;
            if(!profiles.has(key))
                profiles.set(key,{profile,digest,workers:0});
            profiles.get(key).workers++;
        }
    }
    return [...profiles.entries()].sort(([a],[b])=>a.localeCompare(b));
}

document.addEventListener('DOMContentLoaded', () => {
    const $ = id => document.getElementById(id);
    let project = document.documentElement.dataset.hubDefaultProject || 'bees';
    let tab = 'monitor', state = null, editing = null;
    let comparisonData = null,comparisonProject = null,comparisonError = null;
    let comparisonRequest = 0,selectionProject = null;
    let comparisonDefaultsApplied = false;
    let pipelineKindProject = null;
    let pipelineStageProject = null;
    let pipelineStageKind = null;
    let envArgsTouched = false;
    let lastGpuBindingMachine = null, budgetDraft = null;
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
        const checkpointPipeline=['ants','tinybrains'].includes(project)&&
            $('hub-pipeline-kind').value==='continuation';
        const configs=(state?.configurations||[]).filter(c=>
            checkpointPipeline?c.project_id===project:
                c.project_id!=='bees'&&
                (c.project_id!=='ants'||c.parameters?.mode==='new'));
        const hasPredecessor=Boolean($('hub-pipeline-predecessor').value);
        const relationship=checkpointPipeline?'continuation':'independent';
        const sameSelection=pipelineStageProject===project&&
            pipelineStageKind===relationship;
        for(const [i,row] of rows.entries()) {
            const label=row.firstElementChild;
             const select=row.querySelector('select');
            label.textContent='Stage '+(i+1)+' saved configuration';
            label.append(select);
            const old=select.value;
            select.replaceChildren(...configs.map(c=>new Option(
                (checkpointPipeline?'':c.project_id+' · ')+
                    c.name+' · revision '+c.revision,c.id)));
            const expectedResume=i>0||hasPredecessor;
            const suggested=configs.find(c=>checkpointPipeline&&project==='ants'
                ?(c.parameters?.mode==='resume')===expectedResume
                :c.project_id===project);
            if(sameSelection&&configs.some(c=>c.id===old))
                select.value=old;
            else if(suggested)select.value=suggested.id;
            row.querySelector('button').disabled=rows.length<=2;
        }
        pipelineStageProject=project;
        pipelineStageKind=relationship;
        $('hub-pipeline-add').disabled=project==='bees'||rows.length>=16;
        $('hub-pipeline-queue').disabled=project==='bees'||
            rows.length<2||!configs.length;
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
            section.hidden=section.dataset.hubTab!==tab ||
                (scope==='bees' && project!=='bees') ||
                (scope==='other' && project==='bees');
        }
        for(const b of document.querySelectorAll('[data-hub-go]')) {
            b.setAttribute('aria-selected',String(b.dataset.hubGo===tab));
        }
        $('bees-controls').hidden=project!=='bees' || !['train','monitor'].includes(tab);
        if(tab==='train'&&project==='bees') loadBeesControl();
        if(tab==='monitor'&&project==='bees') loadMonitorBees();
        if(tab==='compare'&&project!=='bees')loadComparison();
        if(tab==='machines') loadLiveBeesWorkers();
    }
    const compareSlots=['a','b','c','d'];
    const chartPalette=['#1769aa','#ce5a18','#23834b','#8647a8'];
    const chartMetrics={
        curriculum_window_success_rate:{label:'Current curriculum-window success',rate:true},
        segment_success_rate:{label:'Segment success rate',rate:true},
        curriculum_level:{label:'Curriculum level',rate:false},
        policy_version:{label:'Policy version',rate:false},
        simulation_steps_per_second:{label:'Last-episode simulation steps/s',rate:false},
        ppo_unique_samples_per_second:{label:'Last-episode PPO unique samples/s',rate:false},
    };
    const chartAxes={
        episodes_completed:{label:'Cumulative training episodes'},
        total_environment_steps:{label:'Cumulative environment steps'},
        steps_completed:{label:'Cumulative project steps'},
    };
    async function loadComparison() {
        if(!state||project==='bees')return;
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
            renderComparison();
        }catch(error){
            if(serial!==comparisonRequest||requested!==project)return;
            comparisonData=null;
            comparisonProject=requested;
            comparisonError=String(error.message||error);
            renderComparison();
        }
    }
    function comparisonAxisLabel(key) {
        return key==='steps_completed'&&project==='tinybrains'
            ?'Cumulative TinyBrains updates':chartAxes[key]?.label;
    }
    function renderComparison() {
        if(!state||project==='bees')return;
        const jobs=state.jobs.filter(j=>j.project_id===project);
        const samples=comparisonProject===project&&comparisonData?.samples||[];
        const history=new Map(samples.map(s=>[s.job_id,s.points||[]]));
        const chartable=jobs.filter(j=>j.live_execution?.progress||
            j.final_progress||history.get(j.id)?.length).slice(-80).reverse();
        const previous=compareSlots.map(slot=>$('hub-compare-'+slot).value);
        const changedProject=selectionProject!==project;
        if(changedProject)comparisonDefaultsApplied=false;
        selectionProject=project;
        const chooseDefaults=!comparisonDefaultsApplied&&chartable.length>0;
        const selected=new Set();
        for(let i=0;i<compareSlots.length;i++) {
            const select=$('hub-compare-'+compareSlots[i]);
            select.replaceChildren(new Option('No run',''),
                ...chartable.map(j=>new Option(
                    j.configuration_name+
                    ' · job '+(jobs.indexOf(j)+1)+
                    (j.continuation_of_job_id?' · continuation':' · initial run')+
                    ' · '+j.status+
                    ' · '+new Date(j.created_at).toLocaleDateString(),j.id)));
            const desired=chooseDefaults&&i<2
                ?chartable[i]?.id:(!changedProject?previous[i]:null);
            if(desired&&chartable.some(j=>j.id===desired)&&
                !selected.has(desired)){
                select.value=desired;
                selected.add(desired);
            }
        }
        if(chooseDefaults)comparisonDefaultsApplied=true;
        const runs=compareSlots.map((slot,i)=>{
            const id=$('hub-compare-'+slot).value;
            const job=jobs.find(j=>j.id===id);
            return job?{job,color:chartPalette[i],slot:slot.toUpperCase(),
                points:history.get(id)||[]}:null;
        }).filter(Boolean);
        const metricSelect=$('hub-compare-metric'),oldMetric=metricSelect.value;
        const metricOptions=project==='ants'
            ?Object.entries(chartMetrics).map(([key,meta])=>({
                key,label:meta.label,
            }))
            :hubGenericMetricCatalog(chartable.map(job=>({
                job,points:history.get(job.id)||[],
            }))).map(metric=>({
                key:'metric:'+metric.key,
                label:metric.key+' ('+metric.units.join('/')+')',
            }));
        metricSelect.replaceChildren(...metricOptions.map(m=>
            new Option(m.label,m.key)));
        if(metricOptions.some(m=>m.key===oldMetric))metricSelect.value=oldMetric;
        const axisSelect=$('hub-compare-x-axis'),oldAxis=axisSelect.value;
        const axes=project==='ants'
            ?['episodes_completed','total_environment_steps']
            :['steps_completed'];
        axisSelect.replaceChildren(...axes.map(key=>
            new Option(comparisonAxisLabel(key),key)));
        if(axes.includes(oldAxis))axisSelect.value=oldAxis;
        const levelSelect=$('hub-compare-level');
        const oldLevel=levelSelect.value;
        const levels=[...new Set(runs.flatMap(run=>
            hubComparisonPoints(run).map(p=>p.curriculum_level)
                .filter(Number.isSafeInteger)))].sort((a,b)=>a-b);
        levelSelect.replaceChildren(
            new Option(project==='ants'?'All curriculum levels':'All phases',''),
            ...levels.map(level=>new Option('Level '+level,String(level))));
        if(levels.some(level=>String(level)===oldLevel))
            levelSelect.value=oldLevel;
        const level=levelSelect.value;
        const hasExportData=runs.some(run=>hubComparisonPoints(run,level).length);
        $('hub-compare-export-csv').disabled=!hasExportData;
        $('hub-compare-export-json').disabled=!hasExportData;
        const note=comparisonError&&comparisonProject===project
            ?'Unable to load comparison history: '+comparisonError
            :comparisonProject!==project
                ?'Loading bounded, in-memory progress history...'
                :comparisonData?.storage_warning
                    ?comparisonData.storage_warning+' Live chart samples remain available; saved history may be incomplete.'
                    :comparisonData?.ephemeral
                        ?'Live chart samples are held in memory only. Final summaries remain available.'
                        :'Bounded chart history is saved separately from the training registry (up to 96 points per job and 24 jobs). Recent unsaved samples may be lost on abrupt shutdown.';
        feedback('hub-compare-notice',note);
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
            const levels=runs.map(r=>{
                const p=r.job.live_execution?.progress||r.job.final_progress||
                    r.points[r.points.length-1];
                return p?.curriculum_level;
            }).filter(Number.isSafeInteger);
            if(new Set(levels).size>1)
                warnings.push('Runs are at different curriculum levels; success rates describe different training conditions.');
        }
        feedback('hub-compare-chart-note',
            warnings.join(' ')+(warnings.length?' ':'')+
            'Horizontal axis: '+(comparisonAxisLabel($('hub-compare-x-axis').value)||
                chartAxes.episodes_completed.label)+'. Not wall-clock time. '+
            (level!==''?'Showing curriculum level '+level+
                ' only; axes remain cumulative, not stage-relative. ':'')+
            'Source timestamps show when a trusted adapter published a summary. No training decisions use chart data.');
        const table=$('hub-compare-rows');
        table.replaceChildren();
        const headings=[...table.closest('table').querySelectorAll('thead th')];
        const labels=project==='ants'
            ?['Run','Status / host','Episodes / level','Window success',
                'Segment success','Policy','Latest summary']
            :['Run','Status / host','Project steps','Selected metric',
                'Reported metrics','Segment','Latest summary'];
        labels.forEach((label,i)=>{if(headings[i])headings[i].textContent=label;});
        for(const run of runs) {
            const j=run.job,p=j.live_execution?.progress||j.final_progress||
                run.points[run.points.length-1]||null;
            const tr=document.createElement('tr');
            cell(tr,run.slot+' · '+j.configuration_name+
                (j.continuation_of_job_id?' (continuation)':''));
            cell(tr,j.status+' · '+(j.assigned_machine_id||'unassigned'));
            if(project!=='ants') {
                const metric=p?.metrics?.[metricSelect.value.slice(7)];
                cell(tr,p?.steps_completed?.toLocaleString()||'—');
                cell(tr,metric?metric.value.toLocaleString()+' '+metric.unit:'—');
                cell(tr,Object.keys(p?.metrics||{}).join(', ')||'—');
                cell(tr,p?.segment_id||'—');
                cell(tr,p?.source_modified_at
                    ?new Date(p.source_modified_at).toLocaleString():'—');
                table.append(tr);
                continue;
            }
            cell(tr,p?p.episodes_completed.toLocaleString()+
                ' / level '+p.curriculum_level:'—');
            cell(tr,p?.curriculum_window_success_rate==null?'—':
                (p.curriculum_window_success_rate*100).toFixed(1)+'%'+
                (p.curriculum_window_episodes==null?'':' ('+
                    p.curriculum_window_episodes+' episodes)'));
            cell(tr,p?.segment_success_rate==null?'—':
                (p.segment_success_rate*100).toFixed(1)+'%');
            cell(tr,p?.policy_version?.toLocaleString()||'—');
            cell(tr,p?.source_modified_at?
                new Date(p.source_modified_at).toLocaleString():'—');
            table.append(tr);
        }
        if(!runs.length) {
            const tr=document.createElement('tr');
            cell(tr,chartable.length?'Select a run above to compare.':
                'No reported learning progress for this project.').colSpan=7;
            table.append(tr);
        }
        $('hub-compare-threshold-panel').hidden=project!=='ants';
        const thresholdRows=$('hub-compare-threshold-rows');
        thresholdRows.replaceChildren();
        const rateInput=$('hub-compare-threshold-rate').value.trim();
        const windowInput=$('hub-compare-threshold-window').value.trim();
        const targetPct=Number(rateInput),minWindow=Number(windowInput);
        const validTarget=rateInput!==''&&windowInput!==''&&
            Number.isFinite(targetPct)&&targetPct>=0&&
            targetPct<=100&&Number.isSafeInteger(minWindow)&&
            minWindow>=1&&minWindow<=1000000;
        feedback('hub-compare-threshold-note',project!=='ants'
            ?'Observed Ants rolling-window screening is unavailable for this project.'
            :level===''
                ?'Select one curriculum level to compare the same training phase. All-level success rates are not comparable.'
                :!validTarget
                    ?'Enter a 0–100% target and a minimum observed rolling-window size between 1 and 1,000,000.'
                    :'First observed qualifying sample only, not the true first crossing. This bounded history may omit earlier or intermediate measurements. Not observed means censored, never failure. No evaluation or training decisions use this table.');
        if(project==='ants'&&level!==''&&validTarget) {
            for(const run of runs) {
                const result=hubObservedThreshold(run,level,targetPct/100,minWindow);
                const tr=document.createElement('tr');
                cell(tr,run.slot+' · '+run.job.configuration_name+
                    (run.job.continuation_of_job_id?' (continuation)':''));
                cell(tr,result?.observations??0);
                const first=result?.first;
                cell(tr,first
                    ?'Observed at '+(first.curriculum_window_success_rate*100).toFixed(1)+'%'
                    :result?.observations?'Not observed (censored)':'No qualifying window samples');
                cell(tr,first?first.episodes_completed.toLocaleString():'—');
                cell(tr,first?.total_environment_steps==null?'—':
                    first.total_environment_steps.toLocaleString());
                cell(tr,result?.last?.episodes_completed==null?'—':
                    result.last.episodes_completed.toLocaleString());
                thresholdRows.append(tr);
            }
        }
        drawComparisonChart(runs,$('hub-compare-metric').value,
            $('hub-compare-x-axis').value);
    }
    function drawComparisonChart(runs,key,xKey) {
        const svg=$('hub-compare-svg'),axisLabel=comparisonAxisLabel(xKey);
        const genericKey=key.startsWith('metric:')?key.slice(7):null;
        const units=project==='ants'||!genericKey?[]:
            [...new Set(runs.flatMap(run=>
                hubComparisonPoints(run).map(point=>
                    point.metrics?.[genericKey]?.unit).filter(Boolean)))];
        const metric=project==='ants'?chartMetrics[key]:
            genericKey?{label:genericKey+' ('+(units.join('/')||'unknown')+')',
                rate:units.length===1&&units[0]==='ratio'}:null;
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
            node('text',{x:450,y:160,'text-anchor':'middle'},
                'Selected metric uses incompatible units across runs');
            return;
        }
        svg.setAttribute('aria-label',metric.label+
            ' by '+axisLabel.toLowerCase()+' for selected runs');
        const usable=p=>Number.isSafeInteger(p[xKey])&&p[xKey]>=0&&
            Number.isFinite(hubMetricValue(p,key))&&
            !(key==='ppo_unique_samples_per_second'&&
                p.latest_episode_policy_updated===false);
        const series=runs.map(run=>({
            ...run,valid:hubComparisonPoints(run,$('hub-compare-level').value),
        }));
        const values=series.flatMap(r=>r.valid
            .filter(usable)
            .map(p=>({x:p[xKey],y:hubMetricValue(p,key)})));
        if(!values.length) {
            node('text',{x:450,y:160,'text-anchor':'middle'},
                'No recorded samples for the selected runs and metric');
            return;
        }
        const bounds={left:85,right:865,top:64,bottom:265};
        const xmin=Math.min(...values.map(v=>v.x)),
            xmax=Math.max(...values.map(v=>v.x));
        const ymin=project==='ants'||metric.rate?0:
            Math.min(0,...values.map(v=>v.y))*1.05;
        const ymax=metric.rate?1:project==='ants'
            ?Math.max(1,...values.map(v=>v.y))*1.05
            :Math.max(0.001,...values.map(v=>v.y))*1.05;
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
            node('text',{x:xp,y:288,'text-anchor':'middle'},
                Math.round(value).toLocaleString());
        }
        node('line',{x1:bounds.left,y1:bounds.bottom,
            x2:bounds.right,y2:bounds.bottom,class:'plot-axis'});
        node('line',{x1:bounds.left,y1:bounds.top,
            x2:bounds.left,y2:bounds.bottom,class:'plot-axis'});
        node('text',{x:450,y:313,'text-anchor':'middle'},
            axisLabel);
        for(const run of series) {
            // Run slots provide stable colors even as selections change.
            const i=runs.findIndex(r=>r.job.id===run.job.id);
            node('line',{x1:100+i*195,y1:29,x2:128+i*195,y2:29,
                stroke:run.color,'stroke-width':4});
            node('text',{x:133+i*195,y:33},
                run.slot+' · '+run.job.configuration_name.slice(0,18));
            let part=[];
            const flush=()=>{
                if(part.length>1)
                    node('path',{d:part.map((p,i)=>(i?'L':'M')+
                        x(p[xKey]).toFixed(1)+' '+
                        y(hubMetricValue(p,key)).toFixed(1)).join(' '),
                        stroke:run.color,class:'plot-line'});
                part=[];
            };
            for(const point of run.valid) {
                if(point.break_before||!usable(point))flush();
                if(!usable(point))continue;
                part.push(point);
                node('circle',{cx:x(point[xKey]),cy:y(hubMetricValue(point,key)),
                    r:3.5,fill:run.color,class:'plot-point'});
            }
            flush();
        }
    }
    function exportComparison(format) {
        if(!state||project==='bees')
            throw Error('Select an available project comparison.');
        const jobs=state.jobs.filter(job=>job.project_id===project);
        const samples=comparisonProject===project&&comparisonData?.samples||[];
        const byJob=new Map(samples.map(sample=>
            [sample.job_id,sample.points||[]]));
        const level=$('hub-compare-level').value;
        const runs=compareSlots.map(slot=>{
            const id=$('hub-compare-'+slot).value;
            const job=jobs.find(item=>item.id===id);
            if(!job)return null;
            return {
                slot:slot.toUpperCase(),job_id:job.id,
                configuration_name:job.configuration_name,
                configuration_sha256:job.configuration_sha256||null,
                assigned_machine_id:job.assigned_machine_id||null,
                artifact_lineage_root_id:job.artifact_lineage_root_id||job.id,
                continuation_of_job_id:job.continuation_of_job_id||null,
                status:job.status,
                execution_diagnostic:job.execution_diagnostic||null,
                ...(project==='tinybrains'?{profile_sha256:job.frozen_parameters?.profile_sha256||null}:{}),
                final_resource_usage:job.final_resource_usage||null,
                evaluation_stop:job.evaluation_stop||null,
                last_evaluation:job.last_evaluation||null,
                evaluation_stop_evidence:job.evaluation_stop_evidence||null,
                stop_requested_at:job.stop_requested_at||null,
                points:hubComparisonPoints({job,points:byJob.get(id)||[]},level),
            };
        }).filter(Boolean);
        if(!runs.some(run=>run.points.length))
            throw Error('No recorded samples for the selected runs and level.');
        const exported={
            schema_version:1,project_id:project,
            exported_at:new Date().toISOString(),
            x_axis:$('hub-compare-x-axis').value,
            metric:$('hub-compare-metric').value,
            curriculum_level:level===''?null:Number(level),
            source:project==='ants'?'Experiment Hub validated Ants training summaries':
                'Experiment Hub validated project metrics',
            interpretation:'Observational curves, not benchmark or qualification evidence; cumulative X axes are not stage-normalized.',
            runs,
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
    const tinyBrainsPicker=document.createElement('fieldset');
    tinyBrainsPicker.hidden=true;
    const tinyBrainsLegend=document.createElement('legend');
    tinyBrainsLegend.textContent='TinyBrains Windows CPU job';
    const tinyBrainsSelect=document.createElement('select');
    const tinyBrainsUpdates=document.createElement('input');
    tinyBrainsUpdates.type='number';
    tinyBrainsUpdates.min='1';
    tinyBrainsUpdates.max='10000000';
    tinyBrainsUpdates.step='1';
    tinyBrainsUpdates.value='1';
    const tinyBrainsUse=button('Use profile and update count',()=>{
        const [profile,digest]=tinyBrainsSelect.value.split(':');
        const updates=tinyBrainsUpdates.valueAsNumber;
        if(!profile||!digest||!Number.isSafeInteger(updates)||updates<1||updates>10000000) {
            feedback('hub-config-message','Select an approved profile and enter 1–10,000,000 updates.');
            return;
        }
        $('hub-config-json').value=JSON.stringify({profile,profile_sha256:digest,updates},null,2);
        feedback('hub-config-message','Profile and update count copied to the draft. Save to retain them.');
    });
    for(const [caption,control] of [['Approved profile ',tinyBrainsSelect],
        ['Updates ',tinyBrainsUpdates]]) {
        const label=document.createElement('label');
        label.append(caption,control);
        tinyBrainsPicker.append(label);
    }
    tinyBrainsPicker.prepend(tinyBrainsLegend);
    tinyBrainsPicker.append(tinyBrainsUse);
    $('hub-config-form').prepend(tinyBrainsPicker);
    function displayTinyBrainsProfiles() {
        tinyBrainsPicker.hidden=project!=='tinybrains';
        if(tinyBrainsPicker.hidden)return;
        const prior=tinyBrainsSelect.value;
        const options=approvedTinyBrainsProfiles(state.machines);
        tinyBrainsSelect.replaceChildren(...(options.length
            ?options.map(([key,{profile,digest,workers}])=>new Option(
                profile+' · '+digest.slice(0,12)+' · '+workers+(workers===1?' worker':' workers'),key))
            :[new Option('No connected worker advertises a profile','')]));
        if(options.some(([key])=>key===prior))tinyBrainsSelect.value=prior;
        tinyBrainsSelect.disabled=tinyBrainsUse.disabled=!options.length;
    }
    function resetEditor() {
        editing=null;
        $('hub-config-name').value='';
        $('hub-config-json').value='{}';
        tinyBrainsUpdates.value='1';
        $('hub-config-evaluation').value='';
        $('hub-config-save').textContent='Save draft';
        feedback('hub-config-message','');
    }
    function learningSummary(job) {
        const live=job.live_execution;
        const p=live?.progress||job.final_progress;
        if(p?.source==='ants-summary-v1') {
            const pct=x=>x==null?'—':(x*100).toFixed(1)+'%';
            return 'Episodes '+p.episodes_completed.toLocaleString()+
                ' · Policy '+p.policy_version.toLocaleString()+
                ' · Level '+p.curriculum_level+
                ' · Current curriculum window '+pct(p.curriculum_window_success_rate)+
                (p.curriculum_window_episodes==null?''
                    :' ('+p.curriculum_window_episodes+' episodes)')+
                ' · Segment success '+pct(p.segment_success_rate)+
                (p.simulation_steps_per_second==null?''
                    :' · Last-episode simulation '+Math.round(
                        p.simulation_steps_per_second).toLocaleString()+' steps/s')+
                (p.total_environment_steps==null?''
                    :' · Env steps '+p.total_environment_steps.toLocaleString())+
                (p.total_optimization_steps==null?''
                    :' · Optimizer steps '+p.total_optimization_steps.toLocaleString())+
                (p.latest_episode_policy_updated===false
                    ?' · No PPO update in last episode'
                    :p.ppo_unique_samples_per_second==null?''
                    :' · Last-episode PPO '+Math.round(
                        p.ppo_unique_samples_per_second).toLocaleString()+
                        ' unique samples/s')+
                ' · Summary written '+new Date(p.source_modified_at).toLocaleString();
        }
        if(p?.source==='hub-metrics-v1') {
            const values=Object.entries(p.metrics).slice(0,5).map(
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
        return job.dispatch_id
            ?job.project_id==='ants'?'Awaiting production summary'
                :'Awaiting trusted project summary':'—';
    }
    function display() {
        if(!state)return;
        const current=project;
        $('hub-project').replaceChildren(...state.projects.map(p=>new Option(p.name,p.id)));
        project=state.projects.some(p=>p.id===current)?current:'bees';
        $('hub-project').value=project;
        $('hub-copy-bees-form').hidden=project!=='bees';
        $('hub-config-evaluation-label').hidden=project==='bees';
        displayTinyBrainsProfiles();
        $('hub-config-rows').replaceChildren();
        const configs=state.configurations.filter(c=>c.project_id===project);
        for(const c of configs.slice().reverse()) {
            const tr=document.createElement('tr');
            cell(tr,c.name);cell(tr,project);cell(tr,c.revision);
            cell(tr,c.updated_at?.replace('T',' ').slice(0,19)||'');
            const actions=cell(tr,'');
            actions.append(button('Edit draft',()=>{
                editing={id:c.id,revision:c.revision};
                $('hub-config-name').value=c.name;
                $('hub-config-json').value=JSON.stringify(c.parameters,null,2);
                $('hub-config-evaluation').value=c.evaluation_stop
                    ?JSON.stringify(c.evaluation_stop,null,2):'';
                $('hub-config-save').textContent='Save revision';
                feedback('hub-config-message','Editing revision '+c.revision);
            }));
            actions.append(button('Duplicate',()=>{
                resetEditor();
                $('hub-config-name').value=(c.name+' copy').slice(0,100);
                $('hub-config-json').value=JSON.stringify(c.parameters,null,2);
                $('hub-config-evaluation').value=c.evaluation_stop
                    ?JSON.stringify(c.evaluation_stop,null,2):'';
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
            .filter(j=>j.status!=='cancelled'&&
                (j.continuation_of_job_id||j.after_job_id))
            .map(j=>j.continuation_of_job_id||j.after_job_id));
        const availableParents=(state.jobs||[]).filter(j=>
            j.project_id===project&&!successors.has(j.id)&&(
                j.assigned_machine_id&&j.dispatch_id&&(
                    (j.status==='finished'||j.status==='stopped'&&project!=='tinybrains')&&
                        j.capacity_released_at||
                    ['ants','tinybrains'].includes(project)&&(
                        ['start_requested','launch_requested','running'].includes(j.status)||
                        j.status==='finished'&&!j.capacity_released_at))||
                ['ants','tinybrains'].includes(project)&&
                    ['waiting_adapter','waiting_predecessor'].includes(j.status)));
        predecessorSelect.replaceChildren(
            new Option('New experiment (no predecessor)',''),
            ...availableParents.map(j=>new Option(
                j.configuration_name+' · '+j.status+
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
                ' jobs finished and capacity released');
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
        pipeline.hidden=project==='bees';
        const kind=$('hub-pipeline-kind'),oldKind=kind.value;
        kind.replaceChildren(
            new Option('Independent fresh jobs (no shared checkpoint)','independent'),
            ...(['ants','tinybrains'].includes(project)
                ?[new Option('Checkpoint-continuation stages','continuation')]
                :[]));
        if(pipelineKindProject===project&&
            ['independent','continuation'].includes(oldKind)&&
            (oldKind!=='continuation'||['ants','tinybrains'].includes(project)))
            kind.value=oldKind;
        else kind.value=project==='ants'?'continuation':'independent';
        pipelineKindProject=project;
        const checkpointPipeline=['ants','tinybrains'].includes(project)&&kind.value==='continuation';
        $('hub-pipeline-title').textContent=checkpointPipeline
            ?'Prequeue checkpoint-continuation stages'
            :'Queue independent project jobs in sequence';
        $('hub-pipeline-description').textContent=checkpointPipeline
            ?project==='ants'
                ?'Two to sixteen saved Ants configurations. Use a new first stage and resume-only successors. Each stage waits for its predecessor to finish and release capacity.'
                :'Two to sixteen TinyBrains stages with the same approved profile digest. Each stage waits for its predecessor to finish and release capacity, then stays on the original worker.'
            :'Two to sixteen fresh, independent jobs from any non-Bees project. Each job gets its own artifact lineage and may use a different machine. No checkpoint, optimizer or qualification result is inherited. A verified finished predecessor and capacity release unlock the next job. Stopped and failed jobs do not advance. Queueing never starts training.';
        $('hub-pipeline-predecessor-label').textContent=checkpointPipeline
            ?'Continue an existing job (optional)'
            :'Run after an existing native job (optional)';
        const pipelinePredecessor=$('hub-pipeline-predecessor');
        const formerPipelineParent=pipelinePredecessor.value;
        const sequenceParents=checkpointPipeline?availableParents:
            (state.jobs||[]).filter(j=>j.project_id!=='bees'&&
                !successors.has(j.id)&&
                ['waiting_adapter','waiting_predecessor','start_requested',
                    'launch_requested','running','finished'].includes(j.status)&&
                (j.status!=='finished'||Boolean(j.dispatch_id&&
                    j.assigned_machine_id&&j.execution_updated_at)));
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
        }
        $('hub-job-queue').disabled=project==='bees'||!configs.length;
        const previousMachine=$('hub-job-machine').value;
        $('hub-job-machine').replaceChildren(new Option('Automatic best fit',''),
            ...state.machines.filter(m=>m.status==='connected')
                .map(m=>new Option(m.name+' ('+m.id+')',m.id)));
        if(state.machines.some(m=>m.id===previousMachine&&m.status==='connected'))
            $('hub-job-machine').value=previousMachine;
        const records=(state.jobs||[]).filter(j=>j.project_id===project);
        $('hub-job-allocate-all').disabled=project==='bees'||
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
        $('hub-automation-arm').disabled=automation.enabled||!automationCandidates.length;
        $('hub-automation-disarm').disabled=!automation.enabled;
        $('hub-automation-rebalance').disabled=automation.enabled;
        $('hub-automation-gpu').disabled=automation.enabled;
        $('hub-automation-gpu-claim').disabled=automation.enabled;
        $('hub-automation-gpu-probe').disabled=automation.enabled;
        if(automation.enabled) {
            $('hub-automation-rebalance').checked=automation.rebalance_queued===true;
            $('hub-automation-gpu').checked=automation.start_qualified_gpu===true;
            $('hub-automation-gpu-claim').checked=automation.claim_exclusive_gpu===true;
            $('hub-automation-gpu-probe').checked=automation.probe_exclusive_gpu===true;
        }
        feedback('hub-automation-status',
            (automation.enabled?'Armed for '+automation.machine_ids.join(', ')
                :'Disarmed')+
            (automation.last_action?' · '+automation.last_action:'')+
            (automation.last_transition_at?' · Last change '+
                new Date(automation.last_transition_at).toLocaleString():'')+
            (automation.waiting_reason?' · '+automation.waiting_reason:'')+
            (automation.last_error?' · REQUIRES REVIEW: '+automation.last_error:''));
        $('hub-job-rows').replaceChildren();
        for(const j of records.slice(-50).reverse()) {
            const tr=document.createElement('tr');
            cell(tr,j.configuration_name||j.configuration_id);
            cell(tr,j.configuration_revision);
            const linked=state.reservations.find(l=>l.id===j.lease_id &&
                ['offered','acknowledged'].includes(l.status));
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
            const statusCell=cell(tr,status+lineage+gpuProof+readinessText+
                (hasSuccessor&&['waiting_adapter','waiting_predecessor'].includes(j.status)
                    ?' · Cancel downstream stage first':''));

            statusCell.title=readiness?.reason||
                proof?.diagnostic||detail||j.status_reason||status;
            cell(tr,j.resources.cpus+' CPU / '+j.resources.ram_mb+' MiB / '+
                j.resources.gpu_count+' GPUs');
            const policy=j.evaluation_stop;
            const evidence=j.evaluation_stop_evidence||j.last_evaluation;
            const evaluation=policy
                ?' · Evaluation '+policy.suite_id+': '+(evidence
                    ?evidence.successes+'/'+evidence.trials+' successes'+
                        (j.evaluation_stop_evidence?' · stop target met':' · target not met')
                    :'awaiting evidence')+
                    (j.live_execution?.evaluation_issue?' · '+j.live_execution.evaluation_issue:'')
                :'';
            cell(tr,learningSummary(j)+evaluation);
            const actions=cell(tr,'');
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
            if(j.gpu_probe_block&&
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
            if(j.status==='waiting_adapter'&&readiness?.eligible===true)
                actions.append(button('Start native job',async()=>{
                    const windows=machine.agent_platform==='win32';
                    const gpu=j.resources.gpu_count===1;
                    const autoRelease=$('hub-native-release-on-terminal').checked;
                    if(!confirm((gpu?'Authorize native execution on the exclusively leased GPU for ':
                        'Authorize native CPU/RAM-limited execution of ')+
                        j.configuration_name+' on '+machine.name+
                        '? Only locally installed adapter code selects the executable. '+
                        (gpu?'The service will verify device access and ROCm visibility before training. ':'')+
                        (windows?'Windows Stop FORCE-TERMINATES the entire process tree without checkpoint-aware interruption. ':
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
                            ?'Windows will FORCE-TERMINATE the owned Job Object process tree, without a checkpoint-aware interrupt. '
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
                !j.continuation_machine_id&&!probeHoldsClaim) {
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
            if(j.status==='waiting_predecessor'&&j.project_id==='ants'&&
                parent?.status==='stopped'&&parent.capacity_released_at&&
                parent.execution_updated_at)
                actions.append(button('Approve stopped predecessor',async()=>{
                    if(!confirm('Explicitly allow continuation after the predecessor STOPPED, '+
                        'rather than finished? The latest durable Ants checkpoint may predate the stop, '+
                        'so unsaved progress may be lost. This only unblocks resource scheduling; '+
                        'the trusted adapter must still validate checkpoint lineage and qualification. '+
                        'If native queue automation is armed, it may subsequently start the approved stage.'))return;
                    try{
                        await action('hub/jobs/approve-stopped',{
                            job_id:j.id,confirm_stopped_checkpoint_risk:true,
                        });
                        feedback('hub-job-message',
                            'Stopped predecessor approved. The stage is eligible for ordinary capacity coordination; no process was started by this action.');
                        await reload();
                    }catch(error){
                        feedback('hub-job-message',String(error.message||error));
                    }
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
        for(const j of records.slice(-30).reverse()) {
            const tr=document.createElement('tr');
            const live=j.live_execution;
            const progress=live?.progress||j.final_progress;
            cell(tr,j.configuration_name+
                (j.continuation_of_job_id?' · continuation':''));
            cell(tr,(j.status==='waiting_predecessor'
                    ?j.predecessor_wait?.reason||'Waiting for predecessor'
                    :nativeJobLabel(j,j.status))+
                (j.capacity_released_at?' · capacity released':'')+
                (['failed','unverified'].includes(j.status)&&j.execution_diagnostic
                    ?' · '+j.execution_diagnostic:''));
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
                final.cpu_time_ns_high_water==null?null:
                    (final.cpu_time_ns_high_water/1e9).toFixed(2)+' highest reported CPU seconds',
            ].filter(Boolean).join(' · '):live?[
                nativePeak,
                live.memory_current_mb==null?null:
                    live.memory_current_mb.toLocaleString()+' MiB RAM',
                live.cpu_time_ns==null?null:
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
            m.status==='connected'&&m.agent_platform==='linux'&&
            m.gpu_inventory_source==='amdgpu-sysfs-v1');
        physical.replaceChildren(new Option('Select Linux machine',''),
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
            cell(tr,m.cpus+' configured'+
                (m.reported_cpus!=null?' / '+m.reported_cpus+' reported':'')+
                ' / '+claimedCpus+' claimed'+
                (sampledCpu.length?' · '+observedCpu.toFixed(2)+
                    ' average cores from '+sampledCpu.length+'/'+activeJobs.length+
                    ' Hub native jobs':''));
            cell(tr,m.ram_mb+' MiB configured'+
                (m.reported_ram_mb!=null?' / '+m.reported_ram_mb+' MiB reported':'')+
                ' / '+claimedRam+' MiB claimed'+
                (sampledRam.length?' · '+Math.round(observedRam).toLocaleString()+
                    ' MiB observed from '+sampledRam.length+'/'+activeJobs.length+
                    ' Hub native jobs':''));
            const verified=new Map((m.verified_gpus||[]).map(g=>[g.id,g]));
            const configured=(m.gpus||[]).map(g=>{
                const matched=verified.get(g.id);
                return g.id+' ('+g.vram_mb+' MiB configured) · '+
                    (matched?'PCI '+matched.pci_address+' verified, '+
                        matched.vram_mb+' MiB available for advisory claims'
                        :g.pci_address?'PCI '+g.pci_address+' not observed':
                            'No PCI binding');
            });
            const discovered=(m.observed_gpus||[]).map(g=>
                g.pci_address+' ('+g.vram_mb+' MiB'+
                (g.used_vram_mb==null?'':', '+g.used_vram_mb+' MiB used')+')');
            cell(tr,(configured.join(' / ')||'No configured GPUs')+
                (discovered.length?' · Detected AMD GPUs: '+
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
            state=data;display();selectTab(tab);
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
    for(const slot of compareSlots)
        $('hub-compare-'+slot).addEventListener('change',renderComparison);
    $('hub-compare-metric').addEventListener('change',renderComparison);
    $('hub-compare-x-axis').addEventListener('change',renderComparison);
    $('hub-compare-level').addEventListener('change',renderComparison);
    $('hub-compare-threshold-rate').addEventListener('change',renderComparison);
    $('hub-compare-threshold-window').addEventListener('change',renderComparison);
    for(const format of ['csv','json'])
        $('hub-compare-export-'+format).addEventListener('click',()=>{
            try{exportComparison(format);}
            catch(error){
                feedback('hub-compare-export-message',String(error.message||error));
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
            const stop=$('hub-config-evaluation').value.trim();
            const saved=await action('hub/configurations',{
                ...(editing||{}),
                project_id:project,
                name:$('hub-config-name').value,
                parameters:JSON.parse($('hub-config-json').value),
                ...(project!=='bees'?{evaluation_stop:stop?JSON.parse(stop):null}:{}),
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
                'Select a connected Linux machine with a fresh physical AMDGPU report.');
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
                (p.suggested_gpus?.length?' · GPUs '+p.suggested_gpus.join(', '):'')+' — '+p.reason
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
        const rebalance=$('hub-automation-rebalance').checked;
        const gpu=$('hub-automation-gpu').checked;
        const gpuClaim=$('hub-automation-gpu-claim').checked;
        const gpuProbe=$('hub-automation-gpu-probe').checked;
        if(gpuProbe&&!gpuClaim) {
            feedback('hub-automation-status','Enable exclusive GPU claims to automate their access probes.');
            return;
        }
        if(!confirm('Arm native queue automation on '+ids.join(', ')+
            '? This will start eligible CPU jobs and release verified completed capacity. You confirm that unmanaged host workloads have been accounted for. Restart disarms automation.'+
            (gpu?' This session will also start jobs with a currently qualified exclusive GPU.':'')+
            (gpuClaim?' This session may claim exclusive GPU capacity on armed Linux hosts.':'')+
            (gpuProbe?' It may run one inert access probe for each GPU claim it creates.':'')+
            (rebalance?' This session may move its own unstarted claims between armed hosts after a capacity drop.':'')))return;
        try {
            await action('hub/automation',{
                enabled:true,machine_ids:ids,rebalance_queued:rebalance,
                start_qualified_gpu:gpu,claim_exclusive_gpu:gpuClaim,probe_exclusive_gpu:gpuProbe,
                confirm_arm:true,confirm_untracked_load:true,
            });
            await reload();
        }catch(error){feedback('hub-automation-status',error.message);}
    });
    $('hub-automation-disarm').addEventListener('click',async()=>{
        if(!confirm('Disarm future automatic allocations and Start directives? Already delivered Starts may still be executing; existing processes are NOT stopped and capacity claims remain held. Use manual Stop and native inspection to resolve suspended Start requests.'))return;
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
                    :'No fit')+' — '+p.reason
            ).join('\n')||'No workloads entered. No changes made.');
        }catch(e){feedback('hub-plan-result',e.message);}
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
            const checkpointPipeline=['ants','tinybrains'].includes(project)&&
                $('hub-pipeline-kind').value==='continuation';
            const configs=state.configurations.filter(c=>checkpointPipeline
                ?c.project_id===project
                :c.project_id!=='bees'&&
                    (c.project_id!=='ants'||c.parameters?.mode==='new'));
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
            const predecessor=$('hub-pipeline-predecessor').value;
            if(checkpointPipeline&&project==='ants')for(let i=0;i<stages.length;i++) {
                const selected=configs.find(c=>c.id===stages[i].configuration_id);
                const requiresResume=i>0||Boolean(predecessor);
                if((selected.parameters?.mode==='resume')!==requiresResume)
                    throw Error('Stage '+(i+1)+' needs '+
                        (requiresResume?'a resume':'a new-run')+
                        ' Ants configuration.');
            }
            const question=checkpointPipeline
                ?'Queue '+stages.length+' frozen checkpoint-continuation stages? They share verified checkpoint lineage and original machine.'
                :'Queue '+stages.length+' independent jobs as a frozen sequence? No job inherits any previous checkpoint, machine affinity, optimizer state or evaluation evidence.';
            if(!confirm(question+
                ' Each successor remains blocked until its predecessor FINISHES and releases capacity. No work starts without the separate native authorization gates.'))return;
            const result=await action(checkpointPipeline?'hub/pipelines':'hub/sequences',{
                confirm_queue:true,stages,
                ...(predecessor?(checkpointPipeline
                    ?{continuation_of_job_id:predecessor}
                    :{after_job_id:predecessor}):{}),
            });
            feedback('hub-pipeline-message',
                'Queued '+result.jobs.length+' jobs atomically; no work started. '+
                'Only the first eligible job may request capacity.');
            await reload();
        }catch(error){
            feedback('hub-pipeline-message',String(error.message||error));
        }
    });
    $('hub-job-queue').addEventListener('click',async()=>{
        const selected=state?.configurations.find(c=>
            c.project_id===project&&c.id===$('hub-job-configuration').value);
        if(!selected){
            feedback('hub-job-message','Save a configuration in Design first.');
            return;
        }
        const predecessor=$('hub-job-predecessor').value;
        if(project==='ants'&&
            (selected.parameters?.mode==='resume')!==Boolean(predecessor)){
            feedback('hub-job-message',
                'For Ants, resume mode needs an eligible predecessor (active or completed), and new mode must not select one.');
            return;
        }
        const parent=state.jobs.find(j=>j.id===predecessor);
        const prequeue=Boolean(parent&&!parent.capacity_released_at);
        if(!confirm('Freeze '+selected.name+' revision '+selected.revision+
            (predecessor?' as a continuation of the selected predecessor':
                ' as a new job')+
            '? '+(prequeue
                ?'This stage remains blocked until its predecessor FINISHES and capacity is released. Stops and failures do not auto-advance it. '
                :'')+
            'Queueing alone does not authorize execution.'))return;
        try{
            const job=await action('hub/jobs',{
                configuration_id:selected.id,expected_revision:selected.revision,
                resources:JSON.parse($('hub-job-resources').value),
                ...(predecessor?{continuation_of_job_id:predecessor}:{}),
            });
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
