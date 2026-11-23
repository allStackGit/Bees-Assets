'use strict';

// The external Bees manager owns these actions. Do not infer successful starts
// from HTTP acceptance; only its later control-plane observations can do so.
(() => {
    const root=document.getElementById('hub-external-bees-controls');
    if(!root)return;
    const $=id=>document.getElementById(id);
    const status=$('hub-external-bees-status');
    const details=$('hub-external-bees-details');
    const message=$('hub-external-bees-operation');
    const workers=$('hub-external-bees-worker-summary');
    const buttons={
        stop:$('hub-external-bees-stop'),resume:$('hub-external-bees-resume'),
        fresh:$('hub-external-bees-new'),import:$('hub-external-bees-import'),
    };
    let current=null,connected=false,updating=false,acting=false;
    async function api(route,body) {
        const response=await fetch('/api/hub/bees/'+route,{
            method:body===undefined?'GET':'POST',
            ...(body===undefined?{}:{
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify(body),
            }),
            cache:'no-store',
        });
        let value={};
        try{value=await response.json();}catch{}
        if(!response.ok)throw Error(value.error||'Bees manager returned HTTP '+response.status);
        return value;
    }
    function allowActions() {
        const blocked=!connected||acting||!current||
            current.pending_release||current.operator_operation||
            current.experiment_owned||current.queue_armed;
        buttons.stop.disabled=blocked||!current.training_enabled;
        buttons.resume.disabled=blocked||current.training_enabled||
            current.initial_build_required;
        buttons.fresh.disabled=blocked||current.training_enabled||
            current.initial_build_required;
        buttons.import.disabled=blocked||current.training_enabled;
    }
    async function refresh() {
        if(updating||acting)return;
        updating=true;
        try {
            const bridge=await api('bridge');
            connected=bridge.connected===true;
            if(!bridge.configured) {
                current=null;
                status.textContent='External Bees manager not configured. Native Hub training is unaffected.';
                details.textContent='Configure EXPERIMENT_HUB_BEES_BRIDGE_PORT to a private SSH local forward.';
                workers.textContent='';
                return;
            }
            if(!connected) {
                current=null;
                status.textContent='Bees manager disconnected.';
                details.textContent=bridge.error||'No valid Bees manager response.';
                workers.textContent='Bees training state unknown. Do not assume the run stopped.';
                return;
            }
            const control=await api('control');
            current=control;
            const run=control.run_id||'none';
            const busy=control.pending_release?'release pending':
                control.operator_operation?'operator action in progress':
                control.training_enabled?'training running':'training stopped';
            status.textContent='Bees: '+busy+' · run '+run;
            details.textContent='Queued experiment owns training: '+
                (control.experiment_owned?'yes':'no')+
                ' · queue armed: '+(control.queue_armed?'yes':'no');
            try{
                const response=await api('workers');
                const members=Array.isArray(response.workers)?response.workers:[];
                const active=members.filter(w=>w.state==='running').length;
                const remote=members.filter(w=>w.trainer_id!=='central-learner').length;
                workers.textContent='Workers: '+members.length+' total ('+remote+
                    ' remote, '+active+' running). '+members.slice(0,12).map(w=>
                        String(w.trainer_id||'worker')+': '+
                        String(w.state||'unknown')+
                        (Number.isFinite(w.live_exp_s)?' · '+Math.round(w.live_exp_s)+' SPS':'')
                    ).join(' | ');
            }catch(error){
                workers.textContent='Worker status unavailable: '+error.message;
            }
        }catch(error) {
            connected=false;
            current=null;
            status.textContent='Bees manager status unavailable.';
            details.textContent=error.message;
            workers.textContent='Do not infer that training has stopped.';
        }finally{
            updating=false;
            allowActions();
        }
    }
    async function act(route,payload,confirmation) {
        if(!connected||acting||!current||!window.confirm(confirmation))return;
        acting=true;
        allowActions();
        try{
            const result=await api(route,payload);
            const operation=result.operation||{};
            message.textContent='Bees manager accepted '+route+
                (operation.id?' (operation '+operation.id+').':'.')+
                ' Refresh for authoritative results.';
        }catch(error){
            message.textContent='Bees action outcome may be uncertain: '+error.message+
                '. Check Bees manager state before retrying.';
        }finally{
            acting=false;
            allowActions();
            await refresh();
        }
    }
    $('hub-external-bees-refresh').addEventListener('click',()=>{void refresh();});
    buttons.stop.addEventListener('click',()=>void act('stop',
        {confirm_stop:true},'Gracefully stop the current Bees training run?'));
    buttons.resume.addEventListener('click',()=>void act('start',
        {mode:'resume',env_args:current.env_args||[]},
        'Resume the existing Bees run with its current environment arguments?'));
    buttons.fresh.addEventListener('click',()=>void act('start',
        {mode:'new',env_args:current.env_args||[],confirm_new_run:true},
        'Start a NEW Bees run and optimizer lineage? This does not resume the previous checkpoint.'));
    buttons.import.addEventListener('click',()=>{
        const manifest=$('hub-external-bees-import-manifest').value.trim();
        if(!/^(?:[A-Za-z]:\\|\\\\)/.test(manifest)||
            !manifest.toLowerCase().endsWith('worker-build-manifest.json')) {
            message.textContent='Enter the absolute Windows path to the incoming worker-build-manifest.json on the Bees manager.';
            return;
        }
        void act('import-workers',{confirm_import:true,manifest_path:manifest},
            'Import these Halifax-built worker archives, validate their source and stage a new Bees release?');
    });
    void refresh();
    setInterval(()=>{if(!document.hidden)void refresh();},12000);
})();
