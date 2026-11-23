'use strict';

// Standalone Design controls for immutable mixed evaluation definitions.
// Never mutate a running job or one of its previously evaluated suite files.
(() => {
    const $=id=>document.getElementById(id);
    const existing=$('hub-config-evaluation');
    if(!existing)return;
    const panel=document.createElement('fieldset');
    panel.className='wide';
    panel.id='hub-saved-mixed-evaluations';
    panel.innerHTML=[
        '<legend>Saved mixed-opponent evaluations</legend>',
        '<p class="muted">Select a saved training environment, then choose independently verified frozen opponents. Editing publishes a new immutable suite revision; old runs and receipts stay unchanged.</p>',
        '<div class="formgrid">',
        '<label>Evaluation name<input id="hub-mixed-name" maxlength="80" placeholder="All-map mixed opponents"></label>',
        '<label>Match training configuration<select id="hub-mixed-configuration"></select></label>',
        '<label>Map selection<select id="hub-mixed-maps"><option value="configuration">Use training configuration maps</option><option value="all_77">All 77 maps</option><option value="season_76">76 competitive maps</option><option value="custom">Choose individual maps</option></select></label>',
        '<label id="hub-mixed-custom-maps-label" hidden>Exact evaluation maps (Ctrl/Cmd-click to select multiple)<select id="hub-mixed-custom-maps" multiple size="8"></select></label>',
        '<label>Maximum turns<input id="hub-mixed-turns" type="number" min="1" max="1000" placeholder="Inherit from configuration"></label>',
        '<label>Seeds (comma-separated)<input id="hub-mixed-seeds" value="813101"></label>',
        '<label>Rotations per candidate seat<input id="hub-mixed-rotations" type="number" min="1" max="8" value="1"></label>',
        '</div><p id="hub-mixed-config-summary" class="muted"></p>',
        '<fieldset><legend>Opponent pool</legend><div id="hub-mixed-opponents" class="formgrid"></div>',
        '<p class="muted">Each map tests every candidate starting seat. More rotations provide more balanced opposing seat assignments; a suite is capped at 2,048 games.</p></fieldset>',
        '<div class="hub-controls"><button id="hub-mixed-save" type="button">Save evaluation</button>',
        '<button id="hub-mixed-new" type="button">New evaluation</button></div>',
        '<p id="hub-mixed-status" class="hub-notice" role="status"></p>',
        '<h4>Saved evaluations</h4><div id="hub-mixed-saved"></div>',
        '<h4>Add a frozen network as an opponent</h4>',
        '<div class="formgrid"><label>Verified finished checkpoint<select id="hub-mixed-checkpoint"></select></label>',
        '<label>Opponent name<input id="hub-mixed-opponent-name" maxlength="80" placeholder="Batch 128 — 9570 updates"></label></div>',
        '<button id="hub-mixed-freeze" type="button">Freeze and add named opponent</button>',
        '<p class="muted">Nano and Micro are already available. Freezing a finished checkpoint creates a new immutable SHA-pinned model; no training is started.</p>'
    ].join('');
    existing.parentElement.insertBefore(panel,existing.nextSibling);
    let library=null,editing=null,busy=false,chosen=new Set();
    let templateSignature=null,opponentSignature=null,configSignature=null,checkpointSignature=null,mapSignature=null;
    const notice=value=>{$('hub-mixed-status').textContent=value;};
    function fill(id,items,placeholder) {
        const field=$(id),previous=field.value;
        field.replaceChildren(new Option(placeholder,''),...items.map(row=>new Option(row[1],row[0])));
        if(items.some(row=>row[0]===previous))field.value=previous;
    }
    function summary() {
        const value=$('hub-mixed-configuration').value;
        const c=library?.configurations?.find(x=>x.id+':'+x.revision===value);
        $('hub-mixed-config-summary').textContent=c
            ?'Source Design map stage: '+c.stage+'. The evaluator uses maps and maximum turns from the approved environment, with independent evaluation seeds. PPO/reward hyperparameters do not change native match rules.'
            :'Select an existing TinyBrains Design configuration.';
    }
    function draft(record,copy=false) {
        editing=copy?null:{id:record.id,revision:record.revision};
        $('hub-mixed-name').value=copy?record.name+' copy':record.name;
        $('hub-mixed-configuration').value=record.configuration_id+':'+record.configuration_revision;
        $('hub-mixed-maps').value=record.map_mode;
        $('hub-mixed-custom-maps-label').hidden=record.map_mode!=='custom';
        const selection=new Set(record.map_ids||[]);
        for(const option of $('hub-mixed-custom-maps').options)
            option.selected=selection.has(option.value);
        $('hub-mixed-turns').value=record.max_turns;
        $('hub-mixed-seeds').value=record.seeds.join(',');
        $('hub-mixed-rotations').value=record.rotations;
        chosen=new Set(record.opponent_ids);
        panel.querySelectorAll('[data-mixed-opponent]').forEach(c=>c.checked=chosen.has(c.value));
        $('hub-mixed-save').textContent=copy?'Save duplicate':'Save new revision';
        summary();
        notice(copy?'This creates a separate evaluation.':
            'Editing revision '+record.revision+'; the old suite stays frozen.');
    }
    function clear() {
        editing=null;
        $('hub-mixed-name').value='';
        $('hub-mixed-maps').value='configuration';
        $('hub-mixed-custom-maps-label').hidden=true;
        for(const option of $('hub-mixed-custom-maps').options)option.selected=false;
        $('hub-mixed-turns').value='';
        $('hub-mixed-seeds').value='813101';
        $('hub-mixed-rotations').value=1;
        chosen.clear();
        panel.querySelectorAll('[data-mixed-opponent]').forEach(c=>c.checked=false);
        $('hub-mixed-save').textContent='Save evaluation';
    }
    function render(data) {
        if(!data)return;
        library=data;
        const configs=JSON.stringify(data.configurations);
        if(configs!==configSignature) {
            configSignature=configs;
            fill('hub-mixed-configuration',(data.configurations||[]).map(c=>[
                c.id+':'+c.revision,c.name+' · revision '+c.revision+' · '+c.stage
            ]),'Select a saved TinyBrains Design');
            summary();
        }
        const opponents=JSON.stringify(data.opponents);
        if(opponents!==opponentSignature) {
            opponentSignature=opponents;
            const root=$('hub-mixed-opponents');
            root.replaceChildren();
            for(const item of data.opponents||[]) {
                const row=document.createElement('label');
                row.className='hub-toggle-row';
                const input=document.createElement('input');
                input.type='checkbox';
                input.dataset.mixedOpponent='1';
                input.value=item.id;
                input.checked=chosen.has(item.id);
                input.addEventListener('change',()=>{
                    if(input.checked)chosen.add(input.value);
                    else chosen.delete(input.value);
                });
                row.append(input,document.createTextNode(' '+item.label+
                    (item.checkpoint_sha256?' · '+item.checkpoint_sha256.slice(0,12):'')));
                root.append(row);
            }
        }
        const templates=JSON.stringify(data.templates);
        if(templates!==templateSignature) {
            templateSignature=templates;
            const root=$('hub-mixed-saved');
            root.replaceChildren();
            for(const record of [...(data.templates||[])].reverse()) {
                const row=document.createElement('div');
                row.className='hub-controls';
                const name=document.createElement('span');
                name.textContent=record.name+' · revision '+record.revision+
                    ' · '+record.games+' games · '+record.suite_id;
                row.append(name);
                for(const [title,action] of [
                    ['Edit',()=>draft(record)],
                    ['Duplicate',()=>draft(record,true)],
                    ['Delete',()=>remove(record)]
                ]) {
                    const b=document.createElement('button');
                    b.type='button';b.textContent=title;
                    b.addEventListener('click',action);
                    row.append(b);
                }
                root.append(row);
            }
            if(!data.templates?.length)root.textContent='No saved mixed evaluations.';
        }
        const maps=JSON.stringify(data.maps);
        if(maps!==mapSignature) {
            mapSignature=maps;
            const select=$('hub-mixed-custom-maps');
            const previous=new Set([...select.selectedOptions].map(o=>o.value));
            select.replaceChildren(...(data.maps||[]).map(id=>new Option(id,id)));
            for(const option of select.options)option.selected=previous.has(option.value);
        }
        const candidates=JSON.stringify(data.checkpoint_candidates);
        if(candidates!==checkpointSignature) {
            checkpointSignature=candidates;
            fill('hub-mixed-checkpoint',(data.checkpoint_candidates||[]).map(c=>[
                c.id,c.updates+' PPO updates · '+c.id.slice(0,12)+
                    (c.batch_size?' · batch '+c.batch_size:'')
            ]),'Choose a verified finished checkpoint');
        }
        panel.hidden=$('hub-project')?.value!=='tinybrains';
    }
    async function send(request) {
        if(busy)return null;
        busy=true;
        panel.querySelectorAll('button').forEach(button=>button.disabled=true);
        try {
            const response=await fetch('/api/hub/local-services/evaluators',{
                method:'POST',headers:{'Content-Type':'application/json'},
                body:JSON.stringify({...request,confirm_action:true})
            });
            const answer=await response.json();
            if(!response.ok)throw Error(answer.error||'Evaluation operation failed.');
            notice(answer.message||'Saved.');
            $('hub-local-service-refresh')?.click();
            return answer;
        }catch(e){notice(e.message);return null;}
        finally{
            busy=false;
            panel.querySelectorAll('button').forEach(button=>button.disabled=false);
        }
    }
    async function save() {
        const [id,revision]=$('hub-mixed-configuration').value.split(':');
        const seeds=$('hub-mixed-seeds').value.split(',').map(x=>x.trim());
        if(seeds.some(x=>!/^[0-9]+$/.test(x)))
            return notice('Seeds must be nonnegative whole numbers separated by commas.');
        const turns=$('hub-mixed-turns').value.trim();
        const result=await send({
            action:'save-custom',name:$('hub-mixed-name').value,
            ...(editing?{id:editing.id,expected_revision:editing.revision}:{}),
            configuration_id:id,configuration_revision:Number(revision),
            map_mode:$('hub-mixed-maps').value,
            ...($('hub-mixed-maps').value==='custom'
                ?{map_ids:[...$('hub-mixed-custom-maps').selectedOptions].map(o=>o.value)}:{}),
            ...(turns?{max_turns:Number(turns)}:{}),
            seeds:seeds.map(Number),
            rotations:Number($('hub-mixed-rotations').value),
            opponent_ids:[...chosen]
        });
        if(result){clear();notice(result.message);}
    }
    async function remove(record) {
        if(!confirm('Delete saved evaluation "'+record.name+
            '"? Its active timer will stop, but immutable suites, jobs and receipts remain intact.'))return;
        const result=await send({action:'delete-custom',id:record.id,
            expected_revision:record.revision});
        if(result&&editing?.id===record.id)clear();
    }
    async function freeze() {
        const id=$('hub-mixed-checkpoint').value;
        const name=$('hub-mixed-opponent-name').value;
        if(!id||!name.trim())return notice('Select a checkpoint and enter a name.');
        if(!confirm('Freeze the selected checkpoint as "'+name+
            '"? This publishes one immutable, fingerprinted opponent.'))return;
        const result=await send({action:'freeze-history',source_job_id:id,
            opponent_name:name});
        if(result)$('hub-mixed-opponent-name').value='';
    }
    $('hub-mixed-save').addEventListener('click',save);
    $('hub-mixed-new').addEventListener('click',clear);
    $('hub-mixed-freeze').addEventListener('click',freeze);
    $('hub-mixed-configuration').addEventListener('change',summary);
    $('hub-mixed-maps').addEventListener('change',()=>{
        $('hub-mixed-custom-maps-label').hidden=$('hub-mixed-maps').value!=='custom';
    });
    $('hub-project')?.addEventListener('change',()=>{
        panel.hidden=$('hub-project').value!=='tinybrains';
    });
    document.addEventListener('hub-local-services-status',event=>{
        const info=event.detail?.evaluators;
        if(info?.custom_library)render({...info.custom_library,
            checkpoint_candidates:info.historical_candidates||[]});
    });
    panel.hidden=$('hub-project')?.value!=='tinybrains';
})();