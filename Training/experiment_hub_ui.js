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
        $('hub-machine-rows').replaceChildren();
        for(const m of state.machines) {
            const tr=document.createElement('tr');
            cell(tr,m.name+' ('+m.id+')');cell(tr,m.status);
            cell(tr,m.cpus);cell(tr,m.ram_mb);
            cell(tr,(m.gpus||[]).map(g=>g.id+' ('+g.vram_mb+' MiB)').join(', ')||'None');
            $('hub-machine-rows').append(tr);
        }
        if(!state.machines.length) {
            const tr=document.createElement('tr');
            cell(tr,'No registered machines.').colSpan=5;
            $('hub-machine-rows').append(tr);
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
                p.machine_name+': '+(p.fits?'Eligible':p.capacity_fits?'Capacity fits (unverified)':'Unavailable')+
                (p.suggested_gpus?.length?' · GPUs '+p.suggested_gpus.join(', '):'')+' — '+p.reason
            ).join('\n')||'No registered machines.');
        }catch(e){feedback('hub-placements',e.message);}
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
