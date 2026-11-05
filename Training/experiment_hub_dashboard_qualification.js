#!/usr/bin/env node
'use strict';

// Deliberately separate from bees_experiments.js. Uses the real Hub API,
// existing dashboard markup/client, real worker-only HTTP listener and real
// Windows native owner with one fixed inert local adapter. No Bees operator,
// live registry, Unity build, Ants runner, or production listener is imported.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const http=require('node:http');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {ExperimentHub}=require('./experiment_hub_core');
const {route:hubRoute}=require('./experiment_hub_admin');
const {createAgentServer}=require('./experiment_hub_agent_listener');
const {endpoint,observations,observedOffers,sendHeartbeat}=
    require('./experiment_hub_agent');
const {LocalExecution}=require('./experiment_hub_execution');
const {WindowsSupervisor}=require('./experiment_hub_windows_supervisor');

const HOST='127.0.0.1';
const MAX_BODY_BYTES=65536;
const REPORT_INTERVAL_MS=1000;

function respond(res,status,data) {
    const payload=JSON.stringify(data);
    res.writeHead(status,{
        'Content-Type':'application/json; charset=utf-8',
        'Content-Length':Buffer.byteLength(payload),
        'Cache-Control':'no-store',
        'X-Content-Type-Options':'nosniff',
    });
    res.end(payload);
}
function renderQualificationPage() {
    const html=fs.readFileSync(path.join(__dirname,'experiments.html'),'utf8')
        .replace(/\r\n/g,'\n');
    // Omit the embedded legacy Bees browser script altogether; keep the
    // same Hub markup, CSS and production experiment_hub_ui.js controls.
    const boundary='<script src="/experiment-hub-ui.js"></script>\n<script>';
    const index=html.indexOf(boundary);
    if(index<0||html.indexOf(boundary,index+1)>=0)
        throw Error('Dashboard script boundary changed; review the isolation boundary before qualification.');
    return html.slice(0,index+boundary.indexOf('\n<script>'))
        .replace('<html lang="en">',
            '<html lang="en" data-hub-default-project="smoke">')
        .replace('<body>','<body>\n<div class="hub-standalone" role="status">'+
            '<strong>ISOLATED WINDOWS QUALIFICATION</strong> — '+
            'Only the fixed 30-second inert Windows helper is executable. '+
            'No Bees or Ants workloads, production experiments, or training controls are connected. '+
            'This session uses a temporary Hub registry and local agent.</div>')
        +'\n</body></html>\n';
}
async function readJson(req) {
    if(!String(req.headers['content-type']||'').startsWith('application/json'))
        throw Error('JSON requests only.');
    let length=0,raw='';
    for await (const chunk of req) {
        length+=chunk.length;
        if(length>MAX_BODY_BYTES)throw Error('Dashboard request too large.');
        raw+=chunk.toString('utf8');
    }
    return JSON.parse(raw||'{}');
}

async function main(argv=process.argv.slice(2),env=process.env) {
    if(argv.length!==1||argv[0]!=='--run-inert-ui')
        throw Error('Specify --run-inert-ui for the isolated Windows browser qualification.');
    if(os.platform()!=='win32')
        throw Error('Native browser qualification requires Windows.');
    const helper=env.EXPERIMENT_HUB_WINDOWS_HELPER;
    const sha=env.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256;
    if(!path.win32.isAbsolute(helper||'')||!/^[a-f0-9]{64}$/.test(String(sha||'')))
        throw Error('Set the locally built absolute Windows helper path and SHA-256 pin first.');
    const page=renderQualificationPage();
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'hub-dashboard-qualification-'));
    let admin=null,worker=null,timer=null,closing=false,active=false;
    let receipts=[],reports=[],lastError=null,token;
    const nativeRoot=path.join(root,'native');
    try {
        const supervisor=new WindowsSupervisor({
            root:nativeRoot,machineId:'smoke',
            helperPath:helper,helperSha256:sha,
            adapters:{smoke:{
                executable:helper,stop_policy:'terminate-group',
                args:()=>['--smoke-child'],
            }},
        });
        await supervisor.qualify();
        assert(supervisor.qualifiedAt,'Windows native preflight did not pass.');
        const executor=new LocalExecution({
            machineId:'smoke',supervisor,projects:['smoke'],
        });
        const hub=new ExperimentHub(path.join(root,'hub'));
        hub.addProject({id:'smoke',name:'Windows qualification',
            description:'Only a fixed 30-second inert Windows child may execute.'});
        const available=observations('smoke');
        if(available.cpus<2||available.ram_mb<512)
            throw Error('Windows qualification requires two logical CPUs and 512 MiB RAM.');
        hub.addMachine({
            id:'smoke',name:'Isolated local Windows agent',
            cpus:2,ram_mb:512,gpus:[],
        });
        token=hub.enrollMachine({machine_id:'smoke',confirm_enroll:true}).agent_token;
        const advertisement={...executor.advertisement(),
            native_preflight_passed:true,instance_id:crypto.randomUUID()};
        worker=createAgentServer(()=>hub);
        await new Promise((resolve,reject)=>{
            worker.once('error',reject);
            worker.listen(0,HOST,resolve);
        });
        const workerUrl=endpoint('http://'+HOST+':'+worker.address().port);
        const poll=async()=>{
            if(active||closing)return;
            active=true;
            try {
                const reply=await sendHeartbeat(workerUrl,'smoke',token,
                    receipts,reports,advertisement);
                reports=[];
                receipts=observedOffers(reply.lease_offers,observations('smoke'));
                if((reply.execution_orders||[]).length>1)
                    throw Error('Agent received multiple native execution orders.');
                if(reply.execution_orders?.length)
                    reports=[await executor.handle(reply.execution_orders[0])];
                if(hub.automation.enabled) {
                    const status=hub.tickAutomation();
                    if(!status.enabled)
                        console.error('[qualification automation] Disarmed: '+
                            status.last_error);
                }
                if(lastError)console.log('[qualification agent] reconnected');
                lastError=null;
            }catch(error) {
                const message=String(error.message||error);
                if(message!==lastError)
                    console.error('[qualification agent] '+message);
                lastError=message;
            }finally{active=false;}
        };

        admin=http.createServer(async(req,res)=>{
            const port=admin.address().port;
            const host=req.headers.host,origin=req.headers.origin;
            if(![HOST+':'+port,'localhost:'+port].includes(host)||
                (origin&&!['http://'+HOST+':'+port,
                    'http://localhost:'+port].includes(origin)))
                return respond(res,403,{error:'Local dashboard only.'});
            try {
                const url=new URL(req.url,'http://localhost');
                const route=url.pathname;
                if(req.method==='GET'&&route==='/') {
                    res.writeHead(200,{
                        'Content-Type':'text/html; charset=utf-8',
                        'Cache-Control':'no-store',
                        'X-Content-Type-Options':'nosniff',
                        'Content-Security-Policy':
                            "default-src 'self'; script-src 'self' 'unsafe-inline'; "+
                            "style-src 'self' 'unsafe-inline'; img-src 'self' blob:; "+
                            "object-src 'none'; base-uri 'none'",
                    });
                    return res.end(page);
                }
                if(req.method==='GET'&&route==='/experiment-hub-ui.js') {
                    const source=fs.readFileSync(
                        path.join(__dirname,'experiment_hub_ui.js'));
                    res.writeHead(200,{
                        'Content-Type':'text/javascript; charset=utf-8',
                        'Content-Length':source.length,'Cache-Control':'no-store',
                        'X-Content-Type-Options':'nosniff',
                    });
                    return res.end(source);
                }
                // Safe read-only response to the Machines tab's existing
                // live-Bees status widget. Never call a Bees control plane.
                if(req.method==='GET'&&route==='/api/hub/bees/workers')
                    return respond(res,200,{workers:[],run_id:null});
                if(req.method==='GET'&&route==='/api/hub/bees/control')
                    return respond(res,200,{
                        run_id:null,training_enabled:false,env_args:[],
                        pending_release:false,experiment_owned:false,
                        queue_armed:false,initial_build_required:true,
                        operator_operation:null,
                    });
                if(!route.startsWith('/api/hub/')||
                    route.startsWith('/api/hub/bees/'))
                    return respond(res,404,{error:'Unavailable in isolated qualification.'});
                const value=req.method==='POST'?await readJson(req):null;
                // The live fixed worker token belongs to this disposable
                // session. Rotating/revoking it mid-flight would strand the
                // agent and turn this qualification into a recovery test.
                if(req.method==='POST'&&
                    ['/api/hub/machines/enroll','/api/hub/machines/revoke']
                        .includes(route)&&value?.machine_id==='smoke')
                    return respond(res,409,{
                        error:'Qualification worker enrollment is managed by this isolated session.',
                    });
                const result=hubRoute(hub,req.method,route,url.searchParams,value);
                if(!result)return respond(res,404,{error:'Unknown Hub route.'});
                return respond(res,result.status,result.data);
            }catch(error) {
                return respond(res,error.status===401?401:400,{
                    error:String(error.message||error),
                });
            }
        });
        admin.on('error',error=>{
            console.error('[qualification dashboard] '+error.message);
        });
        await new Promise((resolve,reject)=>{
            admin.once('error',reject);
            admin.listen(0,HOST,resolve);
        });
        await poll();
        if(lastError)throw Error('Initial isolated agent heartbeat failed: '+lastError);
        timer=setInterval(poll,REPORT_INTERVAL_MS);
        const url='http://'+HOST+':'+admin.address().port+'/';
        console.log('ISOLATED Experiment Hub browser qualification: '+url);
        console.log('Project: Windows qualification (smoke); machine: smoke');
        console.log('Private qualification registry: '+root);
        console.log('The normal Bees dashboard on port 7148 is not used.');
        console.log('Create a draft, queue one CPU-only job, claim it, then Start/Stop');
        console.log('using the browser. Stop/release all claims before Ctrl+C.');
        console.log('Native execution is disarmed by default; browser arming is explicit.');
        const stop=()=>{
            if(closing)return;
            closing=true;
            clearInterval(timer);
            console.log('Qualification listeners stopping. Private registry preserved: '+root);
            const snapshot=hub.snapshot();
            if(snapshot.reservations.some(r=>
                ['active','offered','acknowledged'].includes(r.status))||
                supervisor.snapshot().some(record=>
                    !['finished','failed','stopped'].includes(record.state)))
                console.error('Outstanding/uncertain native ownership remains; inspect the private journal. No processes were force-killed.');
            admin.close();worker.close();
        };
        process.once('SIGINT',stop);
        process.once('SIGTERM',stop);
        return {url,root};
    }catch(error) {
        if(timer)clearInterval(timer);
        if(admin?.listening)admin.close();
        if(worker?.listening)worker.close();
        console.error('Qualification failed; private state preserved: '+root);
        throw error;
    }
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-dashboard-qualification] '+String(error.stack||error));
    process.exitCode=1;
});
module.exports={main,renderQualificationPage};
