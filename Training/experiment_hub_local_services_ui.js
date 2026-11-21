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
    const tinyWorker=$('hub-local-tinybrains-worker');
    const tinyConnect=$('hub-local-tinybrains-connect');
    const tinyMessage=$('hub-local-tinybrains-message');
    const evaluatorRows=$('hub-local-evaluator-rows');
    const evaluatorSetup=$('hub-local-evaluator-setup');
    const courierSetup=$('hub-local-courier-evaluator-setup');
    const evaluatorMessage=$('hub-local-evaluator-message');
    const remoteRows=$('hub-remote-evaluator-rows');
    const remoteMessage=$('hub-remote-evaluator-message');
    let state=null,busy=false;
    const versionStatus=$('hub-version-status');
    // These attributes describe the HTML generation actually loaded by the
    // browser. The endpoint can already be serving newer JS after a restart.
    const servedGeneration=versionStatus?.dataset.generation||null;
    const servedCommit=versionStatus?.dataset.commit||null;

    async function checkHubVersion() {
        if(!versionStatus)return;
        try {
            const response=await fetch('/api/hub/software/version',{cache:'no-store'});
            if(!response.ok)return; // Coordinator may be restarting.
            const data=await response.json();
            if(data.generation!==servedGeneration||
                (data.source_commit||null)!==servedCommit) {
                window.location.reload();
                return;
            }
            let label='Hub '+(data.source_commit?.slice(0,12)||'version unknown');
            if(data.updated_at&&Number.isFinite(Date.parse(data.updated_at))) {
                const updated=new Date(data.updated_at).toLocaleString();
                label+=data.timestamp_kind==='activated'
                    ?' · Last updated: '+updated
                    :' · Source committed: '+updated;
            }else label+=' · Update time unavailable';
            if(data.pending_commit)
                label+=' · New version waiting to activate ('+
                    data.pending_commit.slice(0,12)+')';
            versionStatus.textContent=label;
        }catch {
            // A deployment temporarily interrupts the loopback server.
            // Retain the visible version until the next successful poll.
        }
    }

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
            status.textContent=!item.installed?(item.reason||'Not installed'):
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
    function renderRemoteEvaluators(info) {
        if(!remoteRows)return;
        remoteRows.replaceChildren();
        const machines=info?.machines||[];
        const actions=info?.actions||[];
        if(!machines.length) {
            const empty=document.createElement('p');
            empty.textContent='No remote evaluator service has been reported by an enrolled agent.';
            remoteRows.append(empty);
            return;
        }
        for(const machine of machines) {
            const heading=document.createElement('p');
            const title=document.createElement('strong');
            title.textContent=(machine.name||machine.id)+' ('+machine.id+')';
            heading.append(title,document.createTextNode(' · '+machine.status+
                (machine.issue?' · '+machine.issue:'')));
            remoteRows.append(heading);
            if(machine.platform==='linux'&&machine.status==='connected') {
                const last=actions.filter(item=>
                    item.machine_id===machine.id&&item.unit==='@agent').at(-1);
                const unresolved=last&&
                    ['pending','dispatched','awaiting_restart','unverified'].includes(last.status);
                const controls=document.createElement('div');
                controls.className='hub-controls';
                const detail=document.createElement('span');
                detail.textContent='Machine agent'+(machine.native_restart_blocked
                    ?' · native jobs need reconciliation first':'')+
                    (last?' · '+last.status+(last.reason?' ('+last.reason+')':''):'');
                controls.append(detail,evaluatorButtonRemote('Restart remote agent',
                    'restart-agent',machine.id,'@agent',
                    busy||machine.native_restart_blocked||Boolean(unresolved)));
                if(unresolved) {
                    const review=document.createElement('button');
                    review.type='button';
                    review.textContent=last.status==='pending'?'Cancel pending':'Mark reviewed';
                    review.disabled=busy||
                        (last.status!=='pending'&&
                            Date.now()-Date.parse(last.dispatched_at||'')<120000);
                    review.addEventListener('click',()=>reconcileRemote(last));
                    controls.append(review);
                }
                remoteRows.append(controls);
                for(const project of machine.installed_projects||[]) {
                    const previous=actions.filter(item=>
                        item.machine_id===machine.id&&
                        item.unit==='@setup:'+project).at(-1);
                    const unresolved=actions.some(item=>
                        item.machine_id===machine.id&&item.project===project&&
                        ['pending','dispatched','unverified'].includes(item.status));
                    const line=document.createElement('div');
                    line.className='hub-controls';
                    const title=document.createElement('span');
                    title.textContent=project+' setup'+(previous
                        ?' · '+previous.status+(previous.reason
                            ?' ('+previous.reason+')':''):'');
                    line.append(title,evaluatorButtonRemote(
                        project==='courier'?'Set up / reapprove Courier':'Set up TinyBrains',
                        'setup',machine.id,'@setup:'+project,busy||unresolved));
                    if(previous&&['pending','dispatched','unverified'].includes(previous.status)) {
                        const review=document.createElement('button');
                        review.type='button';
                        review.textContent=previous.status==='pending'
                            ?'Cancel pending':'Mark reviewed';
                        review.disabled=busy||
                            (previous.status==='dispatched'&&
                                Date.now()-Date.parse(previous.dispatched_at||'')<120000);
                        review.addEventListener('click',()=>reconcileRemote(previous));
                        line.append(review);
                    }
                    remoteRows.append(line);
                }
            }
            for(const service of machine.services||[]) {
                const line=document.createElement('div');
                line.className='hub-controls';
                const prior=actions.filter(item=>
                    item.machine_id===machine.id&&item.unit===service.unit).at(-1);
                const unresolved=actions.some(item=>
                    item.machine_id===machine.id&&item.project===service.project&&
                    ['pending','dispatched','unverified'].includes(item.status));
                const detail=document.createElement('span');
                detail.textContent=service.project+' · '+service.unit+' · '+
                    (service.executor==='systemd-user'
                        ?'schedule '+service.scheduler_state
                        :'service '+service.service_state)+
                    (service.last_result==='failed'?' · previous evaluation failed':'')+
                    (prior?' · '+prior.action+' '+prior.status+
                        (prior.reason?' ('+prior.reason+')':''):'');
                line.append(detail);
                const allowed=machine.status==='connected'&&!busy&&!unresolved;
                if(service.executor==='systemd-user') {
                    line.append(
                        evaluatorButtonRemote('Start','start',machine.id,service.unit,
                            !allowed||service.scheduler_state==='active'),
                        evaluatorButtonRemote('Stop','stop',machine.id,service.unit,
                            !allowed||service.scheduler_state!=='active'),
                        evaluatorButtonRemote('Restart','restart',machine.id,service.unit,!allowed),
                        evaluatorButtonRemote('Run now','run',machine.id,service.unit,
                            !allowed||service.service_state==='running')
                    );
                    if(service.project==='tinybrains'&&
                        /-evaluator-(nano|micro)\.service$/.test(service.unit))
                        line.append(evaluatorButtonRemote('Refresh suite','refresh',
                            machine.id,service.unit,
                            !allowed||service.service_state==='running'));
                }else if(service.executor==='windows-service') {
                    line.append(
                        evaluatorButtonRemote('Start','start',machine.id,service.unit,
                            !allowed||service.service_state==='running'),
                        evaluatorButtonRemote('Stop','stop',machine.id,service.unit,
                            !allowed||service.service_state!=='running')
                    );
                }
                if(prior&&['pending','dispatched','unverified'].includes(prior.status)) {
                    const button=document.createElement('button');
                    button.type='button';
                    button.textContent=prior.status==='pending'?'Cancel pending action':'Mark reviewed';
                    const eligible=prior.status!=='dispatched'||
                        Date.now()-Date.parse(prior.dispatched_at||'')>=120000;
                    button.disabled=busy||!eligible||
                        (prior.status!=='pending'&&machine.status!=='connected');
                    button.addEventListener('click',()=>reconcileRemote(prior));
                    line.append(button);
                }
                remoteRows.append(line);
            }
        }
    }
    function evaluatorButtonRemote(title,action,machine,unit,disabled) {
        const button=document.createElement('button');
        button.type='button';
        button.textContent=title;
        button.disabled=disabled;
        button.addEventListener('click',()=>manageRemoteEvaluator(machine,unit,action));
        return button;
    }
    async function manageRemoteEvaluator(machine_id,unit,action) {
        const warning=action==='restart-agent'
            ?'Restart the enrolled remote agent only after local native jobs are idle? '+
                'Its own user-systemd service must be confirmed, and the restart is '+
                'requested only after the Hub accepts the one-use preflight receipt.'
            :'Request '+action+' for evaluator '+unit+' on '+machine_id+
                '? The enrolled agent verifies fixed local paths and approvals. '+
                'A safe agent restart is required after suite setup or refresh.';
        if(busy||!confirm(warning+' No action is automatically replayed.'))return;
        busy=true;
        try {
            const result=await fetch('/api/hub/local-services/remote-evaluator',{
                method:'POST',headers:{'Content-Type':'application/json'},
                body:JSON.stringify({machine_id,unit,action,confirm_action:true}),
            });
            const data=await result.json();
            if(!result.ok)throw Error(data.error||'Remote evaluator action refused.');
            remoteMessage.textContent='Queued '+action+' for '+machine_id+
                ' (operation '+data.id+'). Refresh to see its confirmed outcome.';
        }catch(error){remoteMessage.textContent='Remote evaluator: '+error.message;}
        finally {busy=false;await update();}
    }
    async function reconcileRemote(prior) {
        if(busy||!confirm(prior.status==='pending'
            ?'Cancel this evaluator action before it is dispatched?'
            :'I have inspected the current remote evaluator service state. Mark this operation reviewed WITHOUT replaying it?'))return;
        busy=true;
        try {
            const result=await fetch('/api/hub/local-services/remote-evaluator/reconcile',{
                method:'POST',headers:{'Content-Type':'application/json'},
                body:JSON.stringify({id:prior.id,confirm_reconcile:true}),
            });
            const data=await result.json();
            if(!result.ok)throw Error(data.error||'Evaluator reconciliation refused.');
            remoteMessage.textContent='Operation '+data.id+' marked '+data.status+
                '; no remote command was replayed.';
        }catch(error){remoteMessage.textContent='Reconciliation: '+error.message;}
        finally {busy=false;await update();}
    }
    async function manageEvaluator(action,id,project='tinybrains') {
        if(busy)return;
        const message=action==='setup'
            ?(project==='courier'
                ?'Set up the approved Courier survival evaluator using the installed local environment? The suite is pinned to the current environment file. A local agent restart is needed to advertise it.'
                :'Set up TinyBrains Nano/Micro evaluators? The Hub creates missing frozen initial suites from the trusted installed runtime, then starts separate fixed-budget evaluation timers. Existing suites remain unchanged. This does not start or stop training.')
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
            renderRemoteEvaluators(data.remote_evaluators);
            const workerOptions=(data.tinybrains_collectors||[]);
            if(tinyWorker) {
                const former=tinyWorker.value;
                tinyWorker.replaceChildren(...workerOptions.map(worker=>
                    new Option(worker.name+' ('+worker.machine_id+')'+
                        (worker.ready?' · 2 listeners ready':' · '+worker.reason),
                        worker.machine_id)));
                if(workerOptions.some(worker=>worker.machine_id===former))
                    tinyWorker.value=former;
            }
            const selectedWorker=workerOptions.find(worker=>
                worker.machine_id===tinyWorker?.value);
            if(tinyConnect)tinyConnect.disabled=busy||
                !selectedWorker?.ready||!data.agent.managed||
                (data.agent_active_jobs??data.active_jobs)>0;
            const blocked=data.active_jobs>0;
            const localBlocked=(data.agent_active_jobs??data.active_jobs)>0;
            coordinator.disabled=busy||blocked||!data.coordinator.managed;
            agent.disabled=busy||localBlocked||!data.agent.managed;
            const unavailable=[
                !data.evaluators?.tinybrains_available
                    ?'TinyBrains: '+(data.evaluators?.tinybrains_reason||'unavailable'):null,
                !data.evaluators?.courier_available
                    ?'Courier: '+(data.evaluators?.courier_reason||'unavailable'):null,
            ].filter(Boolean);
            message.textContent=label('Coordinator',data.coordinator)+'; '+
                label('Local agent',data.agent)+'.'+
                (blocked?' Coordinator restart disabled while '+data.active_jobs+
                    ' native job(s) are active or unverified.':'')+
                (data.agent_restart_scope==='local'
                    ?(localBlocked?' Local agent restart disabled while '+
                        data.agent_active_jobs+' local job(s) are active or unverified.':
                        ' Local agent restart is available independently of remote jobs.')
                    :(blocked?' Local agent restart also requires all jobs to be idle because its machine identity was not verified.':''))+
                (unavailable.length?' Evaluator readiness — '+unavailable.join('; ')+'.':'');
        }catch(error){
            state=null;
            if(evaluatorSetup)evaluatorSetup.disabled=true;
            if(courierSetup)courierSetup.disabled=true;
            coordinator.disabled=true;
            agent.disabled=true;
            if(tinyConnect)tinyConnect.disabled=true;
            message.textContent='Could not reach coordinator: '+error.message;
        }
    }
    async function restart(service) {
        if(!state||busy||!state[service]?.managed)return;
        if(!confirm('Restart the '+(service==='coordinator'?'local coordinator':
            'local machine agent')+'? This temporarily interrupts its Hub connection. '+
            (service==='agent'&&state.agent_restart_scope==='local'
                ?' Native jobs on this machine must be stopped or reconciled first.'
                :' Native jobs must be stopped or reconciled first.')))return;
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
    async function connectTinyBrains() {
        if(!state||busy||!tinyWorker?.value||!tinyConnect||
            tinyConnect.disabled)return;
        const selected=(state.tinybrains_collectors||[])
            .find(item=>item.machine_id===tinyWorker.value&&item.ready);
        if(!selected||!confirm('Approve the two authenticated TinyBrains listeners on '+
            selected.machine_id+' for Halifax learning, update its private agent configuration and restart ONLY the local agent? '+
            'Native jobs on Halifax must be idle; this does not start training.'))return;
        busy=true;tinyConnect.disabled=true;
        try{
            const response=await fetch('/api/hub/local-services/tinybrains-collectors',{
                method:'POST',headers:{'Content-Type':'application/json'},
                body:JSON.stringify({machine_id:selected.machine_id,confirm_connect:true}),
            });
            const data=await response.json();
            if(!response.ok)throw Error(data.error||'Collector configuration was rejected.');
            tinyMessage.textContent=data.message+
                (data.restart_requested?' Local agent restart requested.':'');
            setTimeout(update,3000);setTimeout(update,9000);
        }catch(error){tinyMessage.textContent='Collector setup: '+error.message;}
        finally{busy=false;await update();}
    }
    if(tinyConnect)tinyConnect.addEventListener('click',connectTinyBrains);
    if(evaluatorSetup)evaluatorSetup.addEventListener('click',()=>manageEvaluator('setup'));
    if(courierSetup)courierSetup.addEventListener('click',()=>manageEvaluator('setup',undefined,'courier'));
    refresh.addEventListener('click',update);
    coordinator.addEventListener('click',()=>restart('coordinator'));
    agent.addEventListener('click',()=>restart('agent'));
    update();
    checkHubVersion();
    setInterval(checkHubVersion,10000);
})();
