#!/usr/bin/env node
'use strict';

// Native-only Experiment Hub coordinator. This entrypoint deliberately never
// imports the Bees operator, Unity build, Bees queue, or training control plane.
const fs=require('node:fs');
const crypto=require('node:crypto');
const http=require('node:http');
const path=require('node:path');
const {spawn,spawnSync}=require('node:child_process');
const {ExperimentHub}=require('./experiment_hub_core');
const {JobExporter}=require('./experiment_hub_job_export');
const localServices=require('./experiment_hub_local_services');
const {route:hubRoute}=require('./experiment_hub_admin');
const {ReleaseStore,workerChangedPaths,workerActivationImpact}=
    require('./experiment_hub_releases');
const {createAgentServer,AGENT_HOST,AGENT_PORT,activeTailnetAddress}=
    require('./experiment_hub_agent_listener');

const HOST='127.0.0.1';
const PORT=7148;
const BODY_LIMIT=65536;
const SHA=/^[a-f0-9]{40}$/;
const VERSION_FILE='hub-software-activation.json';

// Snapshot the running source at process start, rather than showing a newer
// checkout while the old coordinator generation is still serving requests.
function activeHubVersion(root,checkout=path.resolve(__dirname,'..')) {
    function gitValue(args) {
        const result=spawnSync('git',args,{
            cwd:checkout,encoding:'utf8',timeout:3000,windowsHide:true,
        });
        return result.status===0?result.stdout.trim():null;
    }
    const sourceCommit=gitValue(['rev-parse','HEAD']);
    const commit=SHA.test(sourceCommit||'')?sourceCommit:null;
    const sourceDate=commit?gitValue(['show','-s','--format=%cI','HEAD']):null;
    let updatedAt=sourceDate&&Number.isFinite(Date.parse(sourceDate))?sourceDate:null;
    let timestampKind=updatedAt?'commit':null;
    try {
        const marker=path.join(root,VERSION_FILE);
        const stat=fs.lstatSync(marker);
        if(stat.isFile()&&!stat.isSymbolicLink()&&stat.size>0&&stat.size<1024) {
            const item=JSON.parse(fs.readFileSync(marker,'utf8'));
            if(item.source_commit===commit&&
                typeof item.activated_at==='string'&&
                Number.isFinite(Date.parse(item.activated_at))) {
                updatedAt=item.activated_at;
                timestampKind='activated';
            }
        }
    }catch(error) {
        if(error.code!=='ENOENT')
            console.warn('[hub-updates] Ignoring invalid Hub activation receipt: '+error.message);
    }
    return {source_commit:commit,updated_at:updatedAt,
        timestamp_kind:timestampKind,generation:crypto.randomUUID()};
}

function recordHubActivation(root,sourceCommit) {
    if(!SHA.test(sourceCommit||''))throw Error('Invalid activated Hub revision.');
    const marker=path.join(root,VERSION_FILE);
    const temporary=marker+'.'+crypto.randomUUID()+'.tmp';
    try {
        fs.writeFileSync(temporary,JSON.stringify({
            source_commit:sourceCommit,activated_at:new Date().toISOString(),
        })+'\n',{flag:'wx',mode:0o600});
        fs.renameSync(temporary,marker);
    }finally {try{fs.unlinkSync(temporary);}catch{}}
}

function dataRoot(arg,fsApi=fs) {
    if(typeof arg!=='string'||!path.isAbsolute(arg)||arg==='/'||
        path.basename(arg)==='.'||path.basename(arg)==='..')
        throw Error('Specify a dedicated absolute --data-root outside the checkout.');
    fsApi.mkdirSync(arg,{recursive:true,mode:0o700});
    const stat=fsApi.lstatSync(arg);
    if(!stat.isDirectory()||stat.isSymbolicLink()||
        (typeof process.getuid==='function'&&stat.uid!==process.getuid())||
        (stat.mode&0o077)!==0)
        throw Error('Hub data root must be a private directory owned by this user (mode 0700).');
    return fsApi.realpathSync(arg);
}

function renderPage(html,runtime={}) {
    // Reuse the production Hub markup/client, but never ship the embedded
    // legacy Bees browser controller or its Unity/Bees action buttons.
    const script='<script src="/experiment-hub-ui.js"></script>';
    const normalized=html.replace(/\r\n/g,'\n');
    const at=normalized.indexOf(script);
    if(at<0||normalized.indexOf(script,at+script.length)>=0||
        !normalized.slice(at+script.length).startsWith('\n<script>'))
        throw Error('Experiment Hub HTML script boundary changed; review before serving.');
    const versionAttributes=' data-generation="'+(runtime.generation||'')+
        '" data-commit="'+(runtime.source_commit||'')+
        '" data-frontend-revision="'+(runtime.frontend_revision||'')+'"';
    // Render the running version immediately. The browser poll enhances this
    // label but must not be required to make the build identity visible.
    const updated=runtime.updated_at&&Number.isFinite(Date.parse(runtime.updated_at))
        ?new Date(runtime.updated_at).toISOString().replace('T',' ').slice(0,16)+' UTC'
        :null;
    const versionLabel='Hub '+(runtime.source_commit?.slice(0,12)||'version unknown')+
        (updated
            ?runtime.timestamp_kind==='activated'
                ?' · Last updated: '+updated
                :' · Source committed: '+updated
            :' · Update time unavailable')+
        (runtime.frontend_source_commit&&
            runtime.frontend_source_commit!==runtime.source_commit
                ?' · UI '+runtime.frontend_source_commit.slice(0,12):'');
    const versionPanel='<div class="hub-version" id="hub-version-status"'+
        versionAttributes+' role="status">'+versionLabel+'</div>';
    const panel='<div class="hub-standalone" id="hub-local-service-controls">'+
        '<h3>Local service control</h3>'+
        '<p class="muted">Restart the coordinator or agent on this computer. '+
        'The controls require user systemd services; remote workers are not affected. '+
        'Coordinator restart requires all native jobs to be idle or reconciled. '+
        'Local agent restart checks only this machine when its installed enrollment identity is verified; '+
        'otherwise it conservatively checks all native jobs.</p>'+
        '<div class="hub-controls">'+
        '<button type="button" id="hub-local-restart-coordinator" disabled>Restart coordinator</button>'+
        '<button type="button" id="hub-local-restart-agent" disabled>Restart local agent</button>'+
        '<button type="button" id="hub-local-service-refresh">Refresh service status</button>'+
        '</div>'+
        '<p id="hub-local-service-message" class="hub-notice" role="status"></p>'+
        '<h4>TinyBrains distributed collectors</h4>'+
        '<p class="muted">Connect a fully installed, authenticated two-listener worker to the local TinyBrains learner. '+
        'Uses the canonical private rollout key; preserves all existing jobs and refuses to reconfigure an active learner.</p>'+
        '<div class="hub-controls">'+
        '<select id="hub-local-tinybrains-worker" aria-label="TinyBrains collector machine"></select>'+
        '<button type="button" id="hub-local-tinybrains-connect" disabled>Connect collector to Halifax learner</button>'+
        '</div>'+
        '<p id="hub-local-tinybrains-message" class="hub-notice" role="status"></p>'+
        '<h4>Independent evaluation services</h4>'+
        '<p class="muted">Set up missing frozen Nano/Micro suites and manage their timers here. '+
        'Starting a timer schedules evaluations; Run now requests one immediate evaluation. '+
        'Stopping a timer does not stop training or interrupt a running evaluation.</p>'+
        '<div class="hub-controls">'+
        '<button type="button" id="hub-local-evaluator-setup" disabled>Set up TinyBrains Nano/Micro</button>'+
        '<select id="hub-local-evaluator-mini-source" aria-label="Finished Batch 128 training run"></select>'+
        '<button type="button" id="hub-local-evaluator-mini-setup" disabled>Freeze Batch 128 Mini opponent</button>'+
        '<button type="button" id="hub-local-courier-evaluator-setup" disabled>Set up / refresh Courier evaluation</button>'+
        '</div>'+
        '<div id="hub-local-evaluator-rows" class="hub-standalone"></div>'+
        '<p id="hub-local-evaluator-message" class="hub-notice" role="status"></p>'+
        '<h4>Remote evaluator services</h4>'+
        '<p class="muted">Control only existing services verified by enrolled remote agents. '+
        'Operations are one-use; uncertain results require review, not automatic replay. '+
        'Host enrollment and initial service installation remain separate authorized steps.</p>'+
        '<div id="hub-remote-evaluator-rows" class="hub-standalone"></div>'+
        '<p id="hub-remote-evaluator-message" class="hub-notice" role="status"></p>'+
        '</div>';
    return normalized.slice(0,at+script.length)
        .replace(script,'<script src="/experiment-hub-ui.js?v='+
            runtime.frontend_revision+'"></script>')
        .replace('<html lang="en">',
            '<html lang="en" data-hub-default-project="tinybrains">')
        .replace('</style>',
            '#bees-controls,[data-hub-tab][data-project="bees"],'+
            '#hub-copy-bees-form,#hub-save-bees-draft{display:none!important}\n'+
            '.hub-version{font-size:12px;font-weight:700;line-height:1.5;'+
            'max-width:400px;overflow-wrap:anywhere;color:#333}\n</style>')
        .replace('</header>',versionPanel+'</header>')
        .replace('<h2>Machine inventory and resource admission</h2>',
            '<h2>Machine inventory and resource admission</h2>'+panel)
        .replace('<body>','<body>\n<div class="hub-standalone" role="status">'+
            '<strong>NATIVE EXPERIMENT HUB</strong> — Bees/Unity training controls are unavailable. '+
            'Only an enrolled worker with a locally installed trusted adapter may execute a job.'+
            '</div>')
        +'\n<script src="/experiment-hub-services.js?v='+
            runtime.frontend_revision+'"></script>\n</body>\n</html>\n';
}

// Browser assets have a separate, immutable revision from the native worker
// runtime. The coordinator serves one validated snapshot, swapped atomically
// after the authenticated release publisher fetches the Git commit. No source
// checkout update, service restart, or agent action is needed for this path.
const FRONTEND_ASSETS=Object.freeze({
    html:'experiments.html',
    ui:'experiment_hub_ui.js',
    servicesUi:'experiment_hub_local_services_ui.js',
    beakerIcon:'experiment_hub_beaker.svg',
});
function frontendSnapshot(runtime,commit=null,checkout=path.resolve(__dirname,'..')) {
    if(commit!==null&&!SHA.test(commit))throw Error('Invalid browser source commit.');
    const assets={};
    const hash=crypto.createHash('sha256');
    for(const [key,file] of Object.entries(FRONTEND_ASSETS)) {
        let value;
        if(commit===null)value=fs.readFileSync(path.join(__dirname,file),'utf8');
        else {
            const output=spawnSync('git',['show',commit+':Training/'+file],{
                cwd:checkout,encoding:'utf8',timeout:15000,maxBuffer:8*1024*1024,
                windowsHide:true,
            });
            if(output.error||output.status!==0)
                throw Error('Cannot load frontend '+file+' from fetched commit.');
            value=output.stdout;
        }
        if(!value||Buffer.byteLength(value)>8*1024*1024)
            throw Error('Frontend asset missing or unexpectedly large: '+file);
        assets[key]=value;
        hash.update(file+'\0').update(value);
    }
    const revision=hash.digest('hex');
    return {
        ...assets,
        html:renderPage(assets.html,{...runtime,frontend_revision:revision,
            frontend_source_commit:commit||runtime.source_commit}),
        revision,source_commit:commit||runtime.source_commit,
    };
}

function json(res,status,data) {
    const bytes=Buffer.from(JSON.stringify(data));
    res.writeHead(status,{
        'Content-Type':'application/json; charset=utf-8',
        'Content-Length':bytes.length,
        'Cache-Control':'no-store',
        'X-Content-Type-Options':'nosniff',
    });
    res.end(bytes);
}
function sendFile(res,source,contentType) {
    const bytes=Buffer.from(source);
    res.writeHead(200,{
        'Content-Type':contentType,'Content-Length':bytes.length,
        'Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
        'Content-Security-Policy':
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "+
            "img-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
    });
    res.end(bytes);
}
async function readJson(req) {
    if(!/^application\/json(?:\s*;|$)/i.test(String(req.headers['content-type']||'')))
        throw Error('JSON requests only.');
    let length=0;
    const chunks=[];
    for await(const chunk of req) {
        length+=chunk.length;
        if(length>BODY_LIMIT) {
            const error=Error('Request body exceeds 64 KiB.');
            error.status=413;
            throw error;
        }
        chunks.push(chunk);
    }
    // UTF-8 code points may cross TCP chunk boundaries. Decode after
    // bounded reassembly, rejecting malformed bytes rather than altering
    // an operator's frozen JSON configuration.
    const raw=new TextDecoder('utf-8',{fatal:true}).decode(
        Buffer.concat(chunks,length));
    return JSON.parse(raw||'{}');
}

function createAdminServer(hub,{frontend,dataRoot,releases=null,runtime=null}={}) {
    const exporter=new JobExporter(dataRoot,hub);
    return http.createServer(async(req,res)=>{
        const host=req.headers.host,origin=req.headers.origin;
        const expected=['127.0.0.1:'+PORT,'localhost:'+PORT];
        if(!expected.includes(host)||
            (origin&&!expected.some(x=>origin==='http://'+x))||
            (req.headers['sec-fetch-site']&&
                !['same-origin','none'].includes(req.headers['sec-fetch-site'])))
            return json(res,403,{error:'Local administrator dashboard only.'});
        try {
            const url=new URL(req.url,'http://localhost');
            const route=url.pathname;
            const requestedVersion=url.searchParams.get('v');
            const assets=requestedVersion&&frontend.snapshots.get(requestedVersion)||
                frontend.current;
            if(req.method==='GET'&&route==='/')
                return sendFile(res,frontend.current.html,'text/html; charset=utf-8');
            if(req.method==='GET'&&route==='/experiment-hub-beaker.svg')
                return sendFile(res,assets.beakerIcon,'image/svg+xml');
            if(req.method==='GET'&&route==='/experiment-hub-ui.js')
                return sendFile(res,assets.ui,'text/javascript; charset=utf-8');
            if(req.method==='GET'&&route==='/experiment-hub-services.js')
                return sendFile(res,assets.servicesUi,'text/javascript; charset=utf-8');
            if(req.method==='GET'&&route==='/api/hub/software/version') {
                const candidate=releases?.workerSourceCommit||
                    releases?.latest?.worker?.source_commit;
                return json(res,200,{
                    ...runtime,
                    frontend_revision:frontend.current.revision,
                    frontend_commit:frontend.current.source_commit,
                    pending_commit:frontend.backendPending&&SHA.test(candidate||'')&&
                        candidate!==runtime?.source_commit?candidate:null,
                    pending_agent_commit:frontend.agentPending&&SHA.test(candidate||'')&&
                        candidate!==runtime?.source_commit?candidate:null,
                });
            }
            if(req.method==='GET'&&route==='/api/hub/software/releases')
                return json(res,200,{
                    components:Object.fromEntries(Object.entries(releases?.latest||{})
                        .map(([name,entry])=>[name,{
                            ...entry,
                            activation_ready:entry.source_commit===releases.bootVersions[name],
                        }])),
                    errors:releases?.failures||{},
                    machine_update_policies:hub.state.machine_update_policies||{},
                });
            if(req.method==='GET'&&route==='/api/hub/local-services')
                return json(res,200,await localServices.status(hub));
            if(req.method==='POST'&&route==='/api/hub/local-services/restart') {
                const body=await readJson(req);
                if(body.confirm_restart!==true)
                    return json(res,400,{error:'Explicit restart confirmation required.'});
                const request=await localServices.scheduleRestart(body.service,hub,res);
                return json(res,202,request);
            }
            if(req.method==='POST'&&route==='/api/hub/local-services/tinybrains-collectors') {
                const result=await localServices.configureTinyBrainsCollectors(hub,await readJson(req));
                const requested=await localServices.scheduleRestart('agent',hub,res);
                return json(res,202,{...result,restart_requested:requested.requested});
            }
            if(req.method==='POST'&&route==='/api/hub/local-services/evaluators') {
                const body=await readJson(req);
                if(body.confirm_action!==true)
                    return json(res,400,{error:'Explicit evaluator confirmation required.'});
                return json(res,200,await localServices.evaluatorAction(hub,body));
            }
            if(req.method==='POST'&&route==='/api/hub/local-services/remote-evaluator') {
                return json(res,202,hub.queueEvaluatorAction(await readJson(req)));
            }
            if(req.method==='POST'&&route==='/api/hub/local-services/remote-evaluator/reconcile') {
                return json(res,200,hub.reconcileEvaluatorAction(await readJson(req)));
            }
            if(req.method==='POST'&&route==='/api/hub/exports')
                return json(res,202,exporter.start(await readJson(req)));
            const statusRoute=/^\/api\/hub\/exports\/([0-9a-f-]{36})$/.exec(route);
            if(req.method==='GET'&&statusRoute)
                return json(res,200,exporter.status(statusRoute[1]));
            const partRoute=/^\/api\/hub\/exports\/([0-9a-f-]{36})\/part\/([1-9][0-9]*)$/.exec(route);
            if(req.method==='GET'&&partRoute)
                return exporter.download(partRoute[1],Number(partRoute[2]),res);
            if(req.method==='GET'&&route==='/api/hub/bees/workers')
                return json(res,200,{workers:[],run_id:null});
            if(req.method==='GET'&&route==='/api/hub/bees/control')
                return json(res,200,{run_id:null,training_enabled:false,
                    env_args:[],pending_release:false,experiment_owned:false,
                    queue_armed:false,initial_build_required:true,
                    operator_operation:null});
            if(!route.startsWith('/api/hub/')||
                route.startsWith('/api/hub/bees/'))
                return json(res,404,{error:'Not available in native-only coordinator.'});
            const value=req.method==='POST'?await readJson(req):null;
            const result=hubRoute(hub,req.method,route,url.searchParams,value);
            return result
                ?json(res,result.status,result.data)
                :json(res,404,{error:'Unknown Hub API route.'});
        }catch(error) {
            return json(res,[401,409,413].includes(error.status)
                ?error.status:400,{error:String(error.message||error)});
        }
    });
}

async function main(argv=process.argv.slice(2)) {
    if(argv.length===1&&argv[0]==='--help') {
        console.log('Usage: node Training/experiment_hub_standalone.js --data-root /absolute/private/hub-data');
        return;
    }
    if(argv.length!==2||argv[0]!=='--data-root')
        throw Error('Specify --data-root /absolute/private/hub-data.');
    process.umask(0o077);
    // The optional private-network interface is never inferred or wildcard-bound.
    const tailnet=process.env.EXPERIMENT_HUB_AGENT_TAILNET_IP
        ?activeTailnetAddress(process.env.EXPERIMENT_HUB_AGENT_TAILNET_IP):null;
    const root=dataRoot(argv[1]);
    const registry=path.join(root,'hub-v1.json');
    const existed=fs.existsSync(registry);
    const hub=new ExperimentHub(root);
    // Fresh native-only deployments start with supported native project
    // definitions. These do not install runners or authorize execution.
    if(!existed) {
        hub.addProject({id:'tinybrains',name:'TinyBrains Ants-MAPOCA'});
        hub.addProject({id:'courier',name:'Courier Ants'});
    }
    const runtime=activeHubVersion(root);
    const initialFrontend=frontendSnapshot(runtime);
    const frontend={current:initialFrontend,checkedSource:runtime.source_commit,
        backendPending:false,agentPending:false,snapshots:new Map([
            [initialFrontend.revision,initialFrontend]])};
    const releases=new ReleaseStore(root);
    const admin=createAdminServer(hub,{frontend,dataRoot:root,releases,runtime});
    const worker=createAgentServer(()=>hub,AGENT_HOST,releases);
    const remoteWorker=tailnet?createAgentServer(()=>hub,tailnet,releases,root):null;
    async function listen(server,port,host) {
        await new Promise((resolve,reject)=>{
            server.once('error',reject);
            server.listen(port,host,resolve);
        });
    }
    try {
        await listen(admin,PORT,HOST);
        await listen(worker,AGENT_PORT,AGENT_HOST);
        if(remoteWorker)await listen(remoteWorker,AGENT_PORT,tailnet);
    }catch(error) {
        if(admin.listening)admin.close();
        if(worker.listening)worker.close();
        if(remoteWorker?.listening)remoteWorker.close();
        throw error;
    }
    // Private GitHub credentials stay on Halifax. Remote workers stage new
    // commits immediately, but their manifest's activation_ready flag remains
    // false until the learner checkout and running coordinator generation
    // are both updated at an idle native-job boundary.
    let closing=false,releaseBusy=false;
    async function releaseCycle() {
        if(closing||releaseBusy)return;
        releaseBusy=true;
        try {
            await releases.refresh();
            // Update browser assets independently even when all native jobs
            // are running. Git show reads the fetched object without changing
            // the checkout used by the coordinator or native adapters.
            const newest=releases.frontendSourceCommit;
            if(newest&&newest!==frontend.checkedSource) {
                try {
                    const next=frontendSnapshot(runtime,newest,releases.sources.worker.path);
                    frontend.checkedSource=newest;
                    if(next.revision!==frontend.current.revision) {
                        frontend.snapshots.set(next.revision,next);
                        // Keep old HTML and its JavaScript paired while a
                        // browser loads across a frontend-version boundary.
                        while(frontend.snapshots.size>8)
                            frontend.snapshots.delete(frontend.snapshots.keys().next().value);
                        frontend.current=next;
                        console.log('[hub-updates] Browser UI activated at '+newest.slice(0,12));
                    }
                }catch(error) {
                    console.error('[hub-updates] Frontend activation: '+
                        String(error.message||error));
                }
            }
            const candidate=releases.workerSourceCommit||
                releases.latest.worker?.source_commit;
            const publishedImpact=workerActivationImpact(candidate&&
                candidate!==runtime.source_commit
                ?await workerChangedPaths(releases.sources.worker.path,
                    runtime.source_commit,candidate):[]);
            frontend.backendPending=publishedImpact.coordinator;
            const agentBase=releases.bootVersions.worker||runtime.source_commit;
            frontend.agentPending=workerActivationImpact(candidate&&
                candidate!==agentBase
                ?await workerChangedPaths(releases.sources.worker.path,agentBase,candidate)
                :[]).agent;
            const active=hub.state.jobs.some(job=>job.dispatch_id&&
                !job.capacity_released_at&&
                !['finished','failed','stopped','cancelled'].includes(job.status));
            const override=active&&Object.values(
                hub.state.machine_update_policies||{}).includes('override');
            if(active&&!override)return;
            // An ordinary idle update still honors the separate CPU queue
            // restart re-arm authorization. An explicit mid-job software
            // override may pause automation, never stop native workloads.
            if(!override&&hub.automation.enabled&&!hub.state.automation_restart)return;
            hub.softwareMaintenance=true;
            const result=await releases.applyLocalWhenIdle(hub,override);
            // Compare with the running coordinator's commit, not merely
            // against the previous release. If an earlier restart failed,
            // the same unloaded code still needs activation.
            const installedWorker=releases.localVersions.worker;
            const impact=workerActivationImpact(installedWorker&&
                installedWorker!==runtime.source_commit
                ?await workerChangedPaths(releases.sources.worker.path,
                    runtime.source_commit,installedWorker):[]);
            const pendingSimulations=Object.entries(releases.latest)
                .filter(([name,entry])=>name!=='worker'&&
                    entry.source_commit!==releases.bootVersions[name]&&
                    releases.localVersions[name]===entry.source_commit)
                .map(([name])=>name);
            const agentNeedsRestart=impact.agent||pendingSimulations.length>0;
            const coordinatorNeedsRestart=impact.coordinator;
            if(!agentNeedsRestart&&!coordinatorNeedsRestart) {
                // Frontend assets were activated separately; no native
                // process needs to be touched.
                if(installedWorker)releases.bootVersions.worker=installedWorker;
                return;
            }
            if(active&&agentNeedsRestart) {
                console.log('[hub-updates] Agent runtime activation deferred until jobs are idle.');
                return;
            }
            if(result.changed.length)
                console.log('[hub-updates] Local fast-forwarded software: '+
                    result.changed.join(', '));
            if(agentNeedsRestart) {
                const service='bees-experiment-hub-agent.service';
                // A failed agent may have exhausted systemd's StartLimitBurst.
                // An installed, enabled agent must recover on the next good
                // idle release even if it is no longer active.
                const enabled=spawnSync('systemctl',
                    ['--user','is-enabled','--quiet',service],
                    {timeout:5000,windowsHide:true});
                if(enabled.status===0) {
                    const reset=spawnSync('systemctl',
                        ['--user','reset-failed',service],
                        {timeout:5000,windowsHide:true});
                    if(reset.status!==0)
                        throw Error('Learner source activated but agent failure state could not be reset.');
                    const restart=spawnSync('systemctl',
                        ['--user','restart',service],
                        {timeout:60000,windowsHide:true});
                    if(restart.status!==0)
                        throw Error('Learner source activated but local agent restart failed.');
                }else {
                    console.log('[hub-updates] Agent activation skipped: local service is not enabled.');
                }
            }
            for(const name of pendingSimulations)
                releases.bootVersions[name]=releases.localVersions[name];
            if(!coordinatorNeedsRestart) {
                // No coordinator modules changed. The authenticated release
                // can now be offered to remote workers without forcing a
                // pointless restart of the dashboard or its API.
                if(installedWorker)releases.bootVersions.worker=installedWorker;
                return;
            }
            try {recordHubActivation(root,installedWorker);}
            catch(error) {
                console.error('[hub-updates] Activation timestamp: '+error.message);
            }
            // Backend code alone requires a coordinator generation change.
            // Unchanged agents and all separately owned training jobs remain.
            const proc=spawn('systemctl',
                ['--user','restart','bees-experiment-hub-standalone.service'],
                {detached:true,stdio:'ignore',windowsHide:true});
            proc.unref();
        }catch(error) {
            console.error('[hub-updates] Local activation: '+String(error.message||error));
        }finally {
            hub.softwareMaintenance=false;
            releaseBusy=false;
        }
    }
    releaseCycle();
    const releaseTimer=setInterval(releaseCycle,120000);
    const automationTimer=setInterval(()=>{
        try {
            // A lost worker heartbeat is a durable observation, never a
            // native Stop or a reason to release its reserved capacity.
            hub.noteExpiredAgents();
            const current=hub.automationStatus();
            if(!current.enabled&&!current.restart_rearm_pending)return;
            const status=hub.tickAutomation();
            if(status.last_error)
                console.error('[hub] Native automation disarmed: '+status.last_error);
        }catch(error) {
            hub.disarmUnsafeAutomation(error);
            console.error('[hub] Native automation disarmed: '+String(error.message||error));
        }
    },10000);
    function stop() {
        if(closing)return;
        closing=true;
        clearInterval(automationTimer);
        clearInterval(releaseTimer);
        admin.close();
        worker.close();
        if(remoteWorker)remoteWorker.close();
        // Jobs and leases are intentionally retained; the coordinator
        // cannot infer whether native trainers remain alive.
    }
    process.once('SIGINT',stop);
    process.once('SIGTERM',stop);
    console.log('Native-only Experiment Hub: http://'+HOST+':'+PORT);
    console.log('Machine-agent listener: http://'+AGENT_HOST+':'+AGENT_PORT);
    if(tailnet)console.log('Tailnet machine-agent listener: http://'+tailnet+':'+AGENT_PORT);
    console.log('Private registry: '+root);
    console.log('Automation disarmed by default; no Bees or Unity operator loaded.');
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-standalone] '+String(error.message||error));
    process.exitCode=1;
});
module.exports={dataRoot,renderPage,createAdminServer,readJson,main};
