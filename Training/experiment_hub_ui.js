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
        if(tab==='machines') loadLiveBeesWorkers();
    }
    function resetEditor() {
        editing=null;
        $('hub-config-name').value='';
        $('hub-config-json').value='{}';
        $('hub-config-save').textContent='Save draft';
        feedback('hub-config-message','');
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
            cell(tr,'No queued jobs for this project.').colSpan=5;
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
                'Saved immutable job '+job.id+'. Waiting for a trusted execution adapter.');
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
