#!/usr/bin/env node
'use strict';

// Native-only Experiment Hub coordinator. This entrypoint deliberately never
// imports the Bees operator, Unity build, Bees queue, or training control plane.
const fs=require('node:fs');
const http=require('node:http');
const path=require('node:path');
const {spawn,spawnSync}=require('node:child_process');
const {ExperimentHub}=require('./experiment_hub_core');
const {JobExporter}=require('./experiment_hub_job_export');
const localServices=require('./experiment_hub_local_services');
const {route:hubRoute}=require('./experiment_hub_admin');
const {ReleaseStore}=require('./experiment_hub_releases');
const {createAgentServer,AGENT_HOST,AGENT_PORT,activeTailnetAddress}=
    require('./experiment_hub_agent_listener');

const HOST='127.0.0.1';
const PORT=7148;
const BODY_LIMIT=65536;

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

function renderPage(html) {
    // Reuse the production Hub markup/client, but never ship the embedded
    // legacy Bees browser controller or its Unity/Bees action buttons.
    const script='<script src="/experiment-hub-ui.js"></script>';
    const normalized=html.replace(/\r\n/g,'\n');
    const at=normalized.indexOf(script);
    if(at<0||normalized.indexOf(script,at+script.length)>=0||
        !normalized.slice(at+script.length).startsWith('\n<script>'))
        throw Error('Experiment Hub HTML script boundary changed; review before serving.');
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
        '<h4>Independent evaluation services</h4>'+
        '<p class="muted">Set up missing frozen Nano/Micro suites and manage their timers here. '+
        'Starting a timer schedules evaluations; Run now requests one immediate evaluation. '+
        'Stopping a timer does not stop training or interrupt a running evaluation.</p>'+
        '<div class="hub-controls">'+
        '<button type="button" id="hub-local-evaluator-setup" disabled>Set up TinyBrains Nano/Micro</button>'+
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
        .replace('<html lang="en">',
            '<html lang="en" data-hub-default-project="tinybrains">')
        .replace('</style>',
            '#bees-controls,[data-hub-tab][data-project="bees"],'+
            '#hub-copy-bees-form,#hub-save-bees-draft{display:none!important}\n</style>')
        .replace('<h2>Machine inventory and resource admission</h2>',
            '<h2>Machine inventory and resource admission</h2>'+panel)
        .replace('<body>','<body>\n<div class="hub-standalone" role="status">'+
            '<strong>NATIVE EXPERIMENT HUB</strong> — Bees/Unity training controls are unavailable. '+
            'Only an enrolled worker with a locally installed trusted adapter may execute a job.'+
            '</div>')
        +'\n<script src="/experiment-hub-services.js"></script>\n</body>\n</html>\n';
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

function createAdminServer(hub,{html,ui,dataRoot,releases=null}={}) {
    const exporter=new JobExporter(dataRoot,hub);
    const servicesUi=fs.readFileSync(path.join(__dirname,'experiment_hub_local_services_ui.js'),'utf8');
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
            if(req.method==='GET'&&route==='/')
                return sendFile(res,html,'text/html; charset=utf-8');
            if(req.method==='GET'&&route==='/experiment-hub-ui.js')
                return sendFile(res,ui,'text/javascript; charset=utf-8');
            if(req.method==='GET'&&route==='/experiment-hub-services.js')
                return sendFile(res,servicesUi,'text/javascript; charset=utf-8');
            if(req.method==='GET'&&route==='/api/hub/software/releases')
                return json(res,200,{
                    components:releases?.latest||{},
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
    const html=renderPage(fs.readFileSync(path.join(__dirname,'experiments.html'),'utf8'));
    const ui=fs.readFileSync(path.join(__dirname,'experiment_hub_ui.js'),'utf8');
    const releases=new ReleaseStore(root);
    const admin=createAdminServer(hub,{html,ui,dataRoot:root,releases});
    const worker=createAgentServer(()=>hub,AGENT_HOST,releases);
    const remoteWorker=tailnet?createAgentServer(()=>hub,tailnet,releases):null;
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
            if(!result.changed.length)return;
            console.log('[hub-updates] Local fast-forwarded software: '+
                result.changed.join(', '));
            // A service restart refreshes adapter catalogs and protocol
            // modules after safe local source activation. No native job is
            // stopped; this path runs only with no unresolved native jobs.
            const agent=spawnSync('systemctl',
                ['--user','is-active','--quiet','bees-experiment-hub-agent.service'],
                {timeout:5000,windowsHide:true});
            if(agent.status===0) {
                const restart=spawnSync('systemctl',
                    ['--user','restart','bees-experiment-hub-agent.service'],
                    {timeout:60000,windowsHide:true});
                if(restart.status!==0)
                    throw Error('Learner source activated but local agent restart failed.');
            }
            // Restart the coordinator from systemd; reloaded module versions
            // are necessary before remote release activation is authorized.
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
