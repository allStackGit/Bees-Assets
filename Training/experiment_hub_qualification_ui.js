'use strict';

// Isolated dashboard qualification controls only. No production dashboard
// includes this script; no credential or native launch command is exposed.
(() => {
    const $=id=>document.getElementById(id);
    let updating=false,requesting=false;
    const fmt=n=>n==null?'not measured':String(n);
    function row(values) {
        const tr=document.createElement('tr');
        for(const value of values) {
            const td=document.createElement('td');
            td.textContent=String(value);
            tr.append(td);
        }
        return tr;
    }
    async function refresh() {
        if(updating)return;
        updating=true;
        try {
            const response=await fetch('/api/hub/qualification/status',
                {cache:'no-store'});
            const state=await response.json();
            if(!response.ok)throw Error(state.error||'Status unavailable');
            $('hub-qual-pause').disabled=state.heartbeats_paused||requesting;
            $('hub-qual-resume').disabled=!state.heartbeats_paused||requesting;
            const last=state.last_heartbeat_at
                ?new Date(state.last_heartbeat_at).toLocaleTimeString():'never';
            $('hub-qual-status').textContent=
                'Worker: '+(state.heartbeats_paused?'HEARTBEATS PAUSED':'polling')+
                ' · Coordinator reports '+state.agent_status+
                ' · Last heartbeat '+last+
                ' · Scheduler '+(state.automation.enabled?'armed':'disarmed')+
                (state.automation.last_error
                    ?' · Disarmed reason: '+state.automation.last_error:'');
            $('hub-qual-resources').textContent=
                'Hub claims: '+state.claimed_cpus+'/'+state.cpu_budget+' CPUs, '+
                state.claimed_ram_mb+'/'+state.ram_budget_mb+' MiB RAM · '+
                state.active_claims+' active leases · '+
                state.running_jobs+' running jobs · '+
                state.pending_jobs+' queued or pending · '+
                state.terminal_unreleased+' terminal awaiting release · '+
                state.unverified_jobs+' unverified · '+
                state.active_native_owners+' native journal entries not yet terminal';
            const records=(state.jobs||[]).map(job=>row([
                job.name,job.status+(job.machine_online?'':' · agent offline'),
                job.cpu_time_ns==null?'not measured':
                    (job.cpu_time_ns/1e9).toFixed(2)+' CPU seconds',
                fmt(job.process_tasks),
                job.memory_current_mb==null?'not measured':
                    job.memory_current_mb+' MiB',
                job.capacity_released?'released':'held or unclaimed',
            ]));
            $('hub-qual-job-rows').replaceChildren(
                ...(records.length?records:[
                    row(['No isolated jobs','—','—','—','—','—']),
                ]));
        }catch(error) {
            $('hub-qual-status').textContent='Qualification status: '+error.message;
        }finally{updating=false;}
    }
    async function setPaused(paused) {
        if(requesting)return;
        const question=paused
            ?'Pause this isolated agent’s heartbeats? After 45 seconds, the coordinator should mark it offline and disarm automatic scheduling. Existing native processes continue until they exit; capacity claims remain reserved.'
            :'Resume this isolated agent’s heartbeats? This reconnects the same local worker but does not re-arm automation or release native claims.';
        if(!confirm(question))return;
        requesting=true;
        $('hub-qual-pause').disabled=true;
        $('hub-qual-resume').disabled=true;
        try {
            const response=await fetch('/api/hub/qualification/heartbeats',{
                method:'POST',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({paused,confirm_change:true}),
            });
            const result=await response.json();
            if(!response.ok)throw Error(result.error||'Cannot change heartbeat state');
            await refresh();
        }catch(error) {
            $('hub-qual-status').textContent='Heartbeat control: '+error.message;
        }finally{requesting=false;}
    }
    $('hub-qual-pause').addEventListener('click',()=>setPaused(true));
    $('hub-qual-resume').addEventListener('click',()=>setPaused(false));
    refresh();
    setInterval(refresh,1000);
})();
