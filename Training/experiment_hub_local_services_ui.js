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
    let state=null,busy=false;

    const label=(name,service)=>{
        if(!service)return name+': unknown';
        if(!service.managed)return name+': '+(service.reason||'not managed');
        return name+': '+(service.active?'running':'stopped')+
            (service.pid?' (PID '+service.pid+')':'');
    };
    async function update() {
        try {
            const response=await fetch('/api/hub/local-services',{cache:'no-store'});
            const data=await response.json();
            if(!response.ok)throw Error(data.error||'Service status unavailable.');
            state=data;
            const blocked=data.active_jobs>0;
            coordinator.disabled=busy||blocked||!data.coordinator.managed;
            agent.disabled=busy||blocked||!data.agent.managed;
            message.textContent=label('Coordinator',data.coordinator)+'; '+
                label('Local agent',data.agent)+'.'+
                (blocked?' Restart disabled while '+data.active_jobs+
                    ' native job(s) are active or unverified.':'');
        }catch(error){
            state=null;
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
    refresh.addEventListener('click',update);
    coordinator.addEventListener('click',()=>restart('coordinator'));
    agent.addEventListener('click',()=>restart('agent'));
    update();
})();
