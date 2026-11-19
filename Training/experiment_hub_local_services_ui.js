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
    const evaluatorMessage=$('hub-local-evaluator-message');
    let state=null,busy=false;

    const label=(name,service)=>{
        if(!service)return name+': unknown';
        if(!service.managed)return name+': '+(service.reason||'not managed');
        return name+': '+(service.active?'running':'stopped')+
            (service.pid?' (PID '+service.pid+')':'');
    };
    function evaluatorButton(title,action,id,disabled) {
        const control=document.createElement('button');
        control.type='button';
        control.textContent=title;
        control.disabled=disabled;
        control.addEventListener('click',()=>manageEvaluator(action,id));
        return control;
    }
    function renderEvaluators(info) {
        if(!evaluatorRows||!evaluatorSetup)return;
        evaluatorRows.replaceChildren();
        evaluatorSetup.disabled=busy||!info?.available||
            info.entries?.every(item=>item.managed&&item.installed);
        if(!info?.available) {
            evaluatorMessage.textContent=info?.reason||
                'Local evaluation services are unavailable.';
            return;
        }
        for(const item of info.entries||[]) {
            const line=document.createElement('div');
            line.className='hub-controls';
            const title=document.createElement('strong');
            title.textContent=item.id==='nano'?'Nano':'Micro';
            const status=document.createElement('span');
            status.textContent=!item.installed?'Not installed':
                !item.managed?'Not ready: '+(item.reason||'unmanaged service'):
                    (item.active?'Schedule active':'Schedule stopped')+
                    (item.running?' · evaluation running':'')+
                    (item.last_result&&item.last_result!=='success'
                        ?' · last result: '+item.last_result:'');
            line.append(title,status);
            if(item.managed) {
                line.append(
                    evaluatorButton('Start','start',item.id,busy||item.active),
                    evaluatorButton('Restart','restart',item.id,busy),
                    evaluatorButton('Run now','run',item.id,busy||item.running),
                    evaluatorButton('Stop','stop',item.id,busy||!item.active)
                );
            }
            evaluatorRows.append(line);
        }
    }
    async function manageEvaluator(action,id) {
        if(busy)return;
        const message=action==='setup'
            ?'Set up and start the two approved TinyBrains Nano/Micro evaluation timers? '+
                'Each evaluator uses a fixed Docker image and its own CPU/RAM limits. '+
                'This does not start or stop training.'
            :action==='stop'
                ?'Stop the '+id+' evaluation schedule? An evaluation already running may finish.'
                :action==='run'
                    ?'Request a '+id+' evaluation immediately if an eligible checkpoint exists?'
                    :action.charAt(0).toUpperCase()+action.slice(1)+
                        ' the '+id+' evaluation schedule?';
        if(!confirm(message))return;
        busy=true;
        if(evaluatorSetup)evaluatorSetup.disabled=true;
        try {
            const response=await fetch('/api/hub/local-services/evaluators',{
                method:'POST',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({
                    action,...(id?{id}:{}),confirm_action:true,
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
    refresh.addEventListener('click',update);
    coordinator.addEventListener('click',()=>restart('coordinator'));
    agent.addEventListener('click',()=>restart('agent'));
    update();
})();
