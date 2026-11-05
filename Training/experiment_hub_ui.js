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

document.addEventListener('DOMContentLoaded', () => {
    const $ = id => document.getElementById(id);
    let project = 'bees', tab = 'monitor', state = null, editing = null;
    let comparisonData = null,comparisonProject = null;
    let comparisonRequest = 0,selectionProject = null;
    let envArgsTouched = false;
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
            renderComparison();
        }catch(error){
            if(serial!==comparisonRequest||requested!==project)return;
            comparisonData=null;
            comparisonProject=requested;
            feedback('hub-compare-notice',error.message);
            renderComparison();
        }
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
        selectionProject=project;
        const selected=new Set();
        for(let i=0;i<compareSlots.length;i++) {
            const select=$('hub-compare-'+compareSlots[i]);
            select.replaceChildren(new Option('No run',''),
                ...chartable.map(j=>new Option(
                    j.configuration_name+
                    (j.continuation_of_job_id?' · continuation':' · initial run')+
                    ' · '+j.status+
                    ' · '+new Date(j.created_at).toLocaleDateString(),j.id)));
            const desired=!changedProject?previous[i]:
                (i<2?chartable[i]?.id:null);
            if(desired&&chartable.some(j=>j.id===desired)&&
                !selected.has(desired)){
                select.value=desired;
                selected.add(desired);
            }
        }
        const runs=compareSlots.map((slot,i)=>{
            const id=$('hub-compare-'+slot).value;
            const job=jobs.find(j=>j.id===id);
            return job?{job,color:chartPalette[i],slot:slot.toUpperCase(),
                points:history.get(id)||[]}:null;
        }).filter(Boolean);
        const note=project!=='ants'
            ?'This project has no supported learning-curve adapter yet. Native job state and terminal records are still available.'
            :comparisonProject!==project
                ?'Loading bounded, in-memory progress history...'
                :comparisonData?.ephemeral
                    ?'Live chart samples are held in memory only (up to 96 per job). Coordinator restarts clear curves; final job summaries remain available.'
                    :'Only current/final snapshots are available.';
        feedback('hub-compare-notice',note);
        const knownRoots=new Set(runs.map(r=>
            r.job.artifact_lineage_root_id||r.job.id));
        const related=knownRoots.size<runs.length;
        feedback('hub-compare-chart-note',
            (related?'Some selected jobs share a checkpoint/artifact lineage; they are not independent replicates. ':'')+
            'Comparison uses cumulative episodes, not wall-clock time. '+
            'Source timestamps show when Ants last published a summary. No training decisions use chart data.');
        const table=$('hub-compare-rows');
        table.replaceChildren();
        for(const run of runs) {
            const j=run.job,p=j.live_execution?.progress||j.final_progress||
                run.points[run.points.length-1]||null;
            const tr=document.createElement('tr');
            cell(tr,run.slot+' · '+j.configuration_name+
                (j.continuation_of_job_id?' (continuation)':''));
            cell(tr,j.status+' · '+(j.assigned_machine_id||'unassigned'));
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
        drawComparisonChart(runs,$('hub-compare-metric').value);
    }
    function drawComparisonChart(runs,key) {
        const svg=$('hub-compare-svg'),metric=chartMetrics[key];
        svg.replaceChildren();
        if(!metric)return;
        const ns='http://www.w3.org/2000/svg';
        const node=(tag,attrs={},content=null)=>{
            const item=document.createElementNS(ns,tag);
            for(const [name,value] of Object.entries(attrs))
                item.setAttribute(name,String(value));
            if(content!=null)item.textContent=String(content);
            svg.append(item);
            return item;
        };
        svg.setAttribute('aria-label',metric.label+
            ' by cumulative Ants experiment episodes for selected runs');
        const series=runs.map(run=>{
            const points=run.points.length?run.points:
                [run.job.live_execution?.progress||run.job.final_progress]
                    .filter(Boolean);
            return {...run,valid:points.filter(p=>
                Number.isFinite(p.episodes_completed))};
        });
        const values=series.flatMap(r=>r.valid
            .filter(p=>Number.isFinite(p[key]))
            .map(p=>({x:p.episodes_completed,y:p[key]})));
        if(!values.length) {
            node('text',{x:450,y:160,'text-anchor':'middle'},
                'No recorded samples for the selected runs and metric');
            return;
        }
        const bounds={left:85,right:865,top:64,bottom:265};
        const xmin=Math.min(...values.map(v=>v.x)),
            xmax=Math.max(...values.map(v=>v.x));
        const ymax=metric.rate?1:Math.max(1,...values.map(v=>v.y))*1.05;
        const x=v=>bounds.left+(v-xmin)/Math.max(1,xmax-xmin)*
            (bounds.right-bounds.left);
        const y=v=>bounds.bottom-(v/ymax)*(bounds.bottom-bounds.top);
        for(let i=0;i<=4;i++) {
            const value=ymax*i/4,yp=y(value);
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
            'Cumulative training episodes');
        for(const run of series) {
            const legendIndex=runs.indexOf(run);
            // run objects are cloned into series; color/slot remain stable.
            const i=runs.findIndex(r=>r.job.id===run.job.id);
            node('line',{x1:100+i*195,y1:29,x2:128+i*195,y2:29,
                stroke:run.color,'stroke-width':4});
            node('text',{x:133+i*195,y:33},
                run.slot+' · '+run.job.configuration_name.slice(0,18));
            let part=[];
            const flush=()=>{
                if(part.length>1)
                    node('path',{d:part.map((p,i)=>(i?'L':'M')+
                        x(p.episodes_completed).toFixed(1)+' '+
                        y(p[key]).toFixed(1)).join(' '),
                        stroke:run.color,class:'plot-line'});
                part=[];
            };
            for(const point of run.valid) {
                if(point.break_before||!Number.isFinite(point[key]))flush();
                if(!Number.isFinite(point[key]))continue;
                part.push(point);
                node('circle',{cx:x(point.episodes_completed),cy:y(point[key]),
                    r:3.5,fill:run.color,class:'plot-point'});
            }
            flush();
        }
    }
    function resetEditor() {
        editing=null;
        $('hub-config-name').value='';
        $('hub-config-json').value='{}';
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
        if(live?.progress_issue)
            return 'Local progress unavailable: '+live.progress_issue;
        return job.project_id==='ants'&&job.dispatch_id
            ?'Awaiting production summary':'—';
    }
    function display() {
        if(!state)return;
        const current=project;
        $('hub-project').replaceChildren(...state.projects.map(p=>new Option(p.name,p.id)));
        project=state.projects.some(p=>p.id===current)?current:'bees';
        $('hub-project').value=project;
        $('hub-copy-bees-form').hidden=project!=='bees';
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
                $('hub-config-save').textContent='Save revision';
                feedback('hub-config-message','Editing revision '+c.revision);
            }));
            actions.append(button('Duplicate',()=>{
                resetEditor();
                $('hub-config-name').value=(c.name+' copy').slice(0,100);
                $('hub-config-json').value=JSON.stringify(c.parameters,null,2);
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
            .filter(j=>j.continuation_of_job_id&&j.status!=='cancelled')
            .map(j=>j.continuation_of_job_id));
        const availableParents=(state.jobs||[]).filter(j=>
            j.project_id===project&&['finished','stopped'].includes(j.status)&&
            j.capacity_released_at&&j.assigned_machine_id&&
            !successors.has(j.id));
        predecessorSelect.replaceChildren(
            new Option('New experiment (no predecessor)',''),
            ...availableParents.map(j=>new Option(
                j.configuration_name+' · '+j.status+' on '+
                (state.machines.find(m=>m.id===j.assigned_machine_id)?.name||
                    j.assigned_machine_id)+' · '+j.id.slice(-8),j.id)));
        if(availableParents.some(j=>j.id===previousParent))
            predecessorSelect.value=previousParent;
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
                stop_requested:'Stop requested',finished:'Finished',
                failed:'Failed',stopped:'Stopped',unverified:'Needs inspection',
                cancelled:'Cancelled',
            };
            const live=j.live_execution;
            const usage=live && j.status==='running' ?
                ' · '+(live.memory_current_mb==null?'RAM ?':live.memory_current_mb+' MiB RAM')+
                (live.cpu_time_ns==null?'':' · '+Math.round(live.cpu_time_ns/1e9)+' CPU seconds')
                :'';
            const lineage=j.continuation_of_job_id
                ?' · Resumes '+(state.jobs.find(p=>
                    p.id===j.continuation_of_job_id)?.configuration_name||
                    'prior experiment')+' on original machine':'';
            const status=j.status==='waiting_adapter'
                ?linked?('Hub budget claim on '+linked.machine_id+' · '+
                    (linked.receipt_fresh?'Receipt fresh':linked.status)+
                    ' · Not started')
                    :'Waiting for trusted adapter · No claim'
                :(labels[j.status]||j.status)+usage+
                    (j.capacity_released_at?' · Capacity released':'')+
                    (machine?.status==='offline'?' · Agent offline':'');
            const detail=j.execution_diagnostic?' · '+j.execution_diagnostic:'';
            const statusCell=cell(tr,status+lineage);
            statusCell.title=detail||j.status_reason||status;
            cell(tr,j.resources.cpus+' CPU / '+j.resources.ram_mb+' MiB / '+
                j.resources.gpu_count+' GPUs');
            cell(tr,learningSummary(j));
            const actions=cell(tr,'');
            if(j.status==='waiting_adapter'&&!j.lease_id)
                actions.append(button('Coordinate capacity',()=>allocateJob([j.id])));
            if(j.status==='waiting_adapter'&&linked?.receipt_fresh&&
                machine?.status==='connected'&&
                machine.local_executor_enabled===true&&
                machine.installed_projects?.includes(j.project_id)&&
                j.resources.gpu_count===0)
                actions.append(button('Start on Linux',async()=>{
                    if(!confirm('Authorize native CPU/RAM-limited execution of '+
                        j.configuration_name+' on '+machine.name+
                        '? Only the installed local adapter selects executable code. This starts a real process; verify host capacity and experimental validity.'))return;
                    try{
                        await action('hub/jobs/start',{
                            job_id:j.id,confirm_start:true,
                        });
                        feedback('hub-job-message',
                            'Native start authorized. Awaiting local systemd verification.');
                        await reload();
                    }catch(e){feedback('hub-job-message',e.message);}
                }));
            if(['start_requested','launch_requested','running','unverified']
                .includes(j.status))
                actions.append(button('Stop managed job',async()=>{
                    if(!confirm('Request native stop for '+j.configuration_name+
                        '? The agent must authenticate and verify the local systemd unit. An offline agent may leave stop pending.'))return;
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
            if(j.status==='waiting_adapter'&&j.lease_id)
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
            if(j.status==='waiting_adapter')actions.append(button('Cancel',async()=>{
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
        const cancelled=records.filter(j=>j.status==='cancelled').length;
        const allocated=records.filter(j=>j.status==='waiting_adapter'&&j.lease_id).length;
        const running=records.filter(j=>j.status==='running').length;
        const pendingNative=records.filter(j=>[
            'start_requested','launch_requested','stop_requested'
        ].includes(j.status)).length;
        const uncertain=records.filter(j=>j.status==='unverified').length;
        feedback('hub-other-monitor',
            waiting+' queued · '+allocated+' with advisory claims · '+
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
            cell(tr,j.status+
                (j.capacity_released_at?' · capacity released':''));
            cell(tr,learningSummary(j));
            cell(tr,progress?.source_modified_at
                ?new Date(progress.source_modified_at).toLocaleString():'—');
            cell(tr,live?.memory_current_mb==null?'—':
                live.memory_current_mb.toLocaleString()+' MiB RAM'+
                (live.cpu_time_ns==null?'':' · '+Math.round(
                    live.cpu_time_ns/1e9).toLocaleString()+' CPU seconds'));
            $('hub-monitor-rows').append(tr);
        }
        if(!records.length) {
            const tr=document.createElement('tr');
            cell(tr,'No project jobs yet.').colSpan=5;
            $('hub-monitor-rows').append(tr);
        }
        $('hub-machine-rows').replaceChildren();
        for(const m of state.machines) {
            const tr=document.createElement('tr');
            cell(tr,m.name+' ('+m.id+')');cell(tr,m.status);
            cell(tr,m.cpus+(m.reported_cpus!=null?' / agent '+m.reported_cpus:''));
            cell(tr,m.ram_mb+(m.reported_ram_mb!=null?' / agent '+m.reported_ram_mb:''));
            cell(tr,(m.gpus||[]).map(g=>g.id+' ('+g.vram_mb+' MiB)').join(', ')||'None');
            const capability=m.execution_capabilities;
            const control=cell(tr,m.status==='connected'
                ?m.local_executor_enabled
                    ?'Local Linux CPU/RAM adapter opted in; verify per started service'
                    :'Not installed'+(capability?.cgroup_v2_detected?' · Linux cgroup v2 detected':'')
                :'Not verified');
            control.title=capability?.reason||'No authenticated native capability report.';
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
            const blocked=result.results.filter(x=>x.status==='insufficient_capacity').length;
            feedback('hub-global-allocate-result',
                allocated+' new advisory claims across projects · '+blocked+
                ' without available Hub budget · No processes started.');
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
                'For Ants, resume mode needs a completed predecessor, and new mode must not select one.');
            return;
        }
        if(!confirm('Freeze '+selected.name+' revision '+selected.revision+
            (predecessor?' as a continuation of the selected completed job':
                ' as a new job')+
            '? This queues work; execution still needs separate authorization.'))return;
        try{
            const job=await action('hub/jobs',{
                configuration_id:selected.id,expected_revision:selected.revision,
                resources:JSON.parse($('hub-job-resources').value),
                ...(predecessor?{continuation_of_job_id:predecessor}:{}),
            });
            feedback('hub-job-message',
                'Queued job '+job.id+'. Coordinate capacity before explicitly authorizing native execution.');
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
            const blocked=result.results.filter(x=>x.status==='insufficient_capacity').length;
            feedback('hub-job-allocate-message',allocated+' coordination claims created · '+
                blocked+' jobs without capacity · No execution.');
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
