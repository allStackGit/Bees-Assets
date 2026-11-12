#!/usr/bin/env node
'use strict';

// Native-only Experiment Hub coordinator. This entrypoint deliberately never
// imports the Bees operator, Unity build, Bees queue, or training control plane.
const fs=require('node:fs');
const http=require('node:http');
const path=require('node:path');
const {ExperimentHub}=require('./experiment_hub_core');
const {JobExporter}=require('./experiment_hub_job_export');
const {route:hubRoute}=require('./experiment_hub_admin');
const {createAgentServer,AGENT_HOST,AGENT_PORT}=
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
    return normalized.slice(0,at+script.length)
        .replace('<html lang="en">',
            '<html lang="en" data-hub-default-project="tinybrains">')
        .replace('</style>',
            '#bees-controls,[data-hub-tab][data-project="bees"],'+
            '#hub-copy-bees-form,#hub-save-bees-draft{display:none!important}\n</style>')
        .replace('<body>','<body>\n<div class="hub-standalone" role="status">'+
            '<strong>NATIVE EXPERIMENT HUB</strong> — Bees/Unity training controls are unavailable. '+
            'Only an enrolled worker with a locally installed trusted adapter may execute a job.'+
            '</div>')
        +'\n</body>\n</html>\n';
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

function createAdminServer(hub,{html,ui,dataRoot}={}) {
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
            if(req.method==='GET'&&route==='/')
                return sendFile(res,html,'text/html; charset=utf-8');
            if(req.method==='GET'&&route==='/experiment-hub-ui.js')
                return sendFile(res,ui,'text/javascript; charset=utf-8');
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
    const admin=createAdminServer(hub,{html,ui,dataRoot:root});
    const worker=createAgentServer(()=>hub);
    try {
        await new Promise((resolve,reject)=>{
            admin.once('error',reject);
            admin.listen(PORT,HOST,resolve);
        });
        await new Promise((resolve,reject)=>{
            worker.once('error',reject);
            worker.listen(AGENT_PORT,AGENT_HOST,resolve);
        });
    }catch(error) {
        if(admin.listening)admin.close();
        if(worker.listening)worker.close();
        throw error;
    }
    const automationTimer=setInterval(()=>{
        try {
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
    let closing=false;
    function stop() {
        if(closing)return;
        closing=true;
        clearInterval(automationTimer);
        admin.close();
        worker.close();
        // Jobs and leases are intentionally retained; the coordinator
        // cannot infer whether native trainers remain alive.
    }
    process.once('SIGINT',stop);
    process.once('SIGTERM',stop);
    console.log('Native-only Experiment Hub: http://'+HOST+':'+PORT);
    console.log('Machine-agent listener: http://'+AGENT_HOST+':'+AGENT_PORT);
    console.log('Private registry: '+root);
    console.log('Automation disarmed by default; no Bees or Unity operator loaded.');
}
if(require.main===module)main().catch(error=>{
    console.error('[hub-standalone] '+String(error.message||error));
    process.exitCode=1;
});
module.exports={dataRoot,renderPage,createAdminServer,readJson,main};
