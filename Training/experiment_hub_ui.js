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
        replicates:!window.editingId && $('replicate-enabled').checked
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
    $('hub-preview').addEventListener('click',async()=>{
        try {
            const query=new URLSearchParams({
                cpus:$('hub-request-cpus').value,
                ram_mb:$('hub-request-ram').value,
                gpus:$('hub-request-gpus').value.trim(),
            });
            const r=await fetch('/api/hub/placements?'+query,{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'No placement result.');
            feedback('hub-placements',(data.placements||[]).map(p=>
                p.machine_name+': '+(p.fits?'Available':'Unavailable')+' — '+p.reason
            ).join('\n')||'No registered machines.');
        }catch(e){feedback('hub-placements',e.message);}
    });

    async function loadBeesControl(){
        if(project!=='bees'||tab!=='train')return;
        try {
            const r=await fetch('/api/hub/bees/control',{cache:'no-store'});
            const data=await r.json();
            if(!r.ok)throw Error(data.error||'Bees control unavailable.');
            const op=data.operator_operation;
            feedback('hub-bees-control-status','Run: '+(data.run_id||'None')+' · '+
                (data.training_enabled?'Training active':'Training stopped')+
                (data.experiment_owned?' · Experiment manager owns this run':''));
            feedback('hub-operator-message',op?op.kind+' · '+op.status+' · '+op.message:'');
            const blocked=data.experiment_owned||data.pending_release||op?.status==='running';
            $('hub-bees-resume').disabled=blocked||data.training_enabled;
            $('hub-bees-new').disabled=blocked||data.training_enabled;
            $('hub-bees-build').disabled=blocked||data.training_enabled;
            $('hub-bees-stop').disabled=blocked||!data.training_enabled;
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
    $('hub-bees-resume').addEventListener('click',()=>beesCommand('start',{mode:'resume'},
        'Start or resume the current Bees run, preserving its optimizer and run identity?'));
    $('hub-bees-new').addEventListener('click',()=>beesCommand('start',{mode:'new',confirm_new_run:true},
        'Create a NEW Bees run and optimizer lineage? Previous checkpoints are retained.'));
    $('hub-bees-build').addEventListener('click',()=>beesCommand('build',{confirm_build:true},
        'Compile new Windows and Linux Unity RL builds? Compatibility changes may require a new run.'));
    $('hub-bees-stop').addEventListener('click',()=>beesCommand('stop',{confirm_stop:true},
        'Gracefully stop ordinary Bees training and preserve its checkpoints?'));

    selectTab('monitor');
    reload();
    setInterval(()=>{reload();if(tab==='train')loadBeesControl();},10000);
});
