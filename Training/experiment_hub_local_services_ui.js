'use strict';

// Dedicated controls for the loopback standalone Hub only. An OS service
// manager, not a browser session, owns process restarts.
(() => {
    const $=id=>document.getElementById(id);
    const panel=$('hub-local-service-controls');
    if(!panel)return;
    const message=$('hub-local-service-message');
    const coordinator=$('hub-local-restart-coordinator');
    const agent=$('hub-local-restart-agent');
    const refresh=$('hub-local-service-refresh');
    const evaluatorRows=$('hub-local-evaluator-rows');
    const evaluatorSetup=$('hub-local-evaluator-setup');
    const courierSetup=$('hub-local-courier-evaluator-setup');
    const evaluatorMessage=$('hub-local-evaluator-message');
    let state=null,busy=false;

    const label=(name,service)=>{
        if(!service)return name+': unknown';
        if(!service.managed)return name+': '+(service.reason||'not managed');
        return name+': '+(service.active?'running':'stopped')+
            (service.pid?' (PID '+service.pid+')':'');
    };
    function evaluatorButton(title,action,id,disabled,project) {
        const control=document.createElement('button');
        control.type='button';
        control.textContent=title;
        control.disabled=disabled;
        control.addEventListener('click',()=>manageEvaluator(action,id,project));
        return control;
    }
    function renderEvaluators(info) {
        if(!evaluatorRows||!evaluatorSetup)return;
        evaluatorRows.replaceChildren();
        evaluatorSetup.disabled=busy||!info?.tinybrains_available||
            info.entries?.filter(item=>item.project==='tinybrains')
                .every(item=>item.managed&&item.installed);
        // Setup also reapproves a new immutable suite when Courier's
        // environment or evaluator runtime changes; keep it available.
        if(courierSetup)courierSetup.disabled=busy||!info?.courier_available;
        if(!info?.available) {
            evaluatorMessage.textContent=[info?.tinybrains_reason,info?.courier_reason]
                .filter(Boolean).join('; ')||'Local evaluation services are unavailable.';
            return;
        }
        for(const item of info.entries||[]) {
            const line=document.createElement('div');
            line.className='hub-controls';
            const title=document.createElement('strong');
            title.textContent=item.project==='courier'?'Courier survival':
                item.id==='nano'?'TinyBrains Nano':'TinyBrains Micro';
            const status=document.createElement('span');
            status.textContent=!item.installed?'Not installed':
                !item.managed?'Not ready: '+(item.reason||'unmanaged service'):
                    (item.active?'Schedule active':'Schedule stopped')+
                    (item.running?' · evaluation running':'')+
                    (item.last_result&&item.last_result!=='success'
                        ?' · last result: '+item.last_result:'')+
                    (item.last_error?' · '+item.last_error:'');
            line.append(title,status);
            if(item.managed) {
                line.append(
                    evaluatorButton('Start','start',item.id,busy||item.active,item.project),
                    evaluatorButton('Restart','restart',item.id,busy,item.project),
                    evaluatorButton('Run now','run',item.id,busy||item.running,item.project),
                    evaluatorButton('Stop','stop',item.id,busy||!item.active,item.project)
                );
                if(item.project==='tinybrains')line.append(
                    evaluatorButton('Refresh suite','refresh',item.id,
                        busy||item.running,item.project));
            }
            evaluatorRows.append(line);
        }
    }
    async function manageEvaluator(action,id,project='tinybrains') {
        if(busy)return;
        const message=action==='setup'
            ?(project==='courier'
                ?'Set up the approved Courier survival evaluator using the installed local environment? The suite is pinned to the current environment file. A local agent restart is needed to advertise it.'
                :'Set up and start the two approved TinyBrains Nano/Micro evaluation timers? Each evaluator uses a fixed Docker image and its own CPU/RAM limits. This does not start or stop training.')
            :action==='refresh'
                ?'Create a new immutable '+id+' evaluation suite pinned to the installed runtime and replace its local schedule? Old suite files and receipts are preserved. Restart the local agent afterward.'
            :action==='stop'
                ?'Stop the '+id+' evaluation schedule? An evaluation already running may finish.'
                :action==='run'
                    ?'Request a '+id+' evaluation immediately if an eligible checkpoint exists?'
                    :action.charAt(0).toUpperCase()+action.slice(1)+
                        ' the '+id+' evaluation schedule?';
        if(!confirm(message))return;
        busy=true;
        if(evaluatorSetup)evaluatorSetup.disabled=true;
        if(courierSetup)courierSetup.disabled=true;
        try {
            const response=await fetch('/api/hub/local-services/evaluators',{
                method:'POST',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({
                    action,project,...(id?{id}:{}),confirm_action:true,
                }),
            });
            const result=await response.json();
            if(!response.ok)throw Error(result.error||'Evaluator action was rejected.');
            evaluatorMessage.textContent=result.message||'Evaluator action accepted.';
        }catch(error) {
            evaluatorMessage.textContent='Evaluator action failed: '+error.message;
        }finally {
            busy=false;
            await update();
        }
    }
    async function update() {
        try {
            const response=await fetch('/api/hub/local-services',{cache:'no-store'});
            const data=await response.json();
            if(!response.ok)throw Error(data.error||'Service status unavailable.');
            state=data;
            renderEvaluators(data.evaluators);
            const blocked=data.active_jobs>0;
            coordinator.disabled=busy||blocked||!data.coordinator.managed;
            agent.disabled=busy||blocked||!data.agent.managed;
            message.textContent=label('Coordinator',data.coordinator)+'; '+
                label('Local agent',data.agent)+'.'+
                (blocked?' Restart disabled while '+data.active_jobs+
                    ' native job(s) are active or unverified.':'');
        }catch(error){
            state=null;
            if(evaluatorSetup)evaluatorSetup.disabled=true;
        if(courierSetup)courierSetup.disabled=true;
            coordinator.disabled=true;
            agent.disabled=true;
            message.textContent='Could not reach coordinator: '+error.message;
        }
    }
    async function restart(service) {
        if(!state||busy||!state[service]?.managed)return;
        if(!confirm('Restart the '+(service==='coordinator'?'local coordinator':
            'local machine agent')+'? This temporarily interrupts its Hub connection. '+
            'Native jobs must be stopped or reconciled first.'))return;
        busy=true;
        coordinator.disabled=true;
        agent.disabled=true;
        try {
            const response=await fetch('/api/hub/local-services/restart',{
                method:'POST',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({service,confirm_restart:true}),
            });
            const data=await response.json();
            if(!response.ok)throw Error(data.error||'Restart request rejected.');
            message.textContent=data.message+
                ' Status may briefly be unavailable; refresh after reconnection.';
            setTimeout(update,3000);
            setTimeout(update,9000);
        }catch(error){
            message.textContent='Restart not requested: '+error.message;
        }finally{
            busy=false;
        }
    }
    if(evaluatorSetup)evaluatorSetup.addEventListener('click',()=>manageEvaluator('setup'));
    if(courierSetup)courierSetup.addEventListener('click',()=>manageEvaluator('setup',undefined,'courier'));
    refresh.addEventListener('click',update);
    coordinator.addEventListener('click',()=>restart('coordinator'));
    agent.addEventListener('click',()=>restart('agent'));
    update();
})();
