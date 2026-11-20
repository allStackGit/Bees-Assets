'use strict';

// Restricted machine-agent listener. Standalone Hub may opt in to a
// separate Tailscale interface; administrator routes are never available here.
const http=require('node:http');
const net=require('node:net');
const os=require('node:os');
const {ArtifactMirror,MAX_CHUNK}=require('./experiment_hub_artifact_sync');
const AGENT_HOST='127.0.0.1';
const AGENT_PORT=7149;
// A heartbeat includes machine/lease reports beyond the separately
// validated 32-KiB trainer progress summary.
const MAX_BODY_BYTES=65536;

function isTailnetIPv4(value) {
    if(typeof value!=='string'||net.isIP(value)!==4)return false;
    const parts=value.split('.').map(Number);
    return parts[0]===100&&parts[1]>=64&&parts[1]<=127;
}
function activeTailnetAddress(value,interfaces=os.networkInterfaces()) {
    if(!isTailnetIPv4(value)||!Object.entries(interfaces).some(([name,addresses])=>
        /^tailscale/i.test(name)&&Array.isArray(addresses)&&
        addresses.some(info=>info.family==='IPv4'&&info.address===value&&
            info.internal!==true)))
        throw Error('Agent tailnet listener requires a local Tailscale interface address in 100.64.0.0/10.');
    return value;
}

function respond(res,status,value) {
    const payload=JSON.stringify(value);
    res.writeHead(status,{
        'Content-Type':'application/json; charset=utf-8',
        'Content-Length':Buffer.byteLength(payload),
        'Cache-Control':'no-store',
        'X-Content-Type-Options':'nosniff',
    });
    res.end(payload);
}

async function readBody(req) {
    if(!/^application\/json(?:\s*;\s*charset\s*=\s*(?:"utf-8"|utf-8))?\s*$/i
        .test(String(req.headers['content-type']||'')))
        throw Error('JSON requests only.');
    const chunks=[];
    let received=0;
    for await(const chunk of req) {
        received+=chunk.length;
        if(received>MAX_BODY_BYTES) {
            const error=Error('JSON request exceeds allowed byte limit.');
            error.status=413;
            throw error;
        }
        chunks.push(chunk);
    }
    return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(
        Buffer.concat(chunks,received))||'{}');
}

function createAgentServer(getHub,bindHost=AGENT_HOST,releases=null) {
    if(bindHost!==AGENT_HOST&&!isTailnetIPv4(bindHost))
        throw Error('Machine-agent server can bind only to loopback or a tailnet IPv4.');
    const hosts=new Set(bindHost===AGENT_HOST
        ?[AGENT_HOST+':'+AGENT_PORT,'localhost:'+AGENT_PORT]
        :[bindHost+':'+AGENT_PORT]);
    if(typeof getHub!=='function')
        throw Error('Agent endpoint requires a local Hub provider.');
    const mirror=new ArtifactMirror(getHub);
    return http.createServer(async(req,res)=>{
        const host=req.headers.host;
        if(!hosts.has(host)||req.headers.origin)
            return respond(res,404,{error:'Agent endpoint only.'});
        // Authenticated release distribution uses the existing machine token.
        // The administrator cannot reach this route from a worker port.
        if(releases&&req.url?.startsWith('/api/hub/agent/releases')) {
            try {
                if(req.url.length>512)return respond(res,404,{error:'Invalid release route.'});
                const url=new URL(req.url,'http://'+AGENT_HOST+':'+AGENT_PORT);
                const pathname=url.pathname,keys=[...url.searchParams.keys()];
                const identity=url.searchParams.get('machine_id');
                if(url.hash||!identity||
                    new Set(keys).size!==keys.length||
                    req.headers.origin)
                    return respond(res,404,{error:'Invalid release envelope.'});
                const hub=getHub();
                hub.authenticateAgent(identity,req.headers['x-hub-agent-token']);
                if(req.method==='GET'&&pathname==='/api/hub/agent/releases'&&
                    keys.length===1&&Number(req.headers['content-length']||0)===0) {
                    const live=hub.agentReports.get(identity);
                    return respond(res,200,{
                        components:releases.manifest(live?.installed_projects||[]),
                        mode:hub.machineUpdatePolicy(identity),
                        agent_instance_id:live?.instance_id||null,
                        agent_preflight_passed:live?.native_preflight_passed===true,
                        agent_report_age_ms:live?Date.now()-live.received_ms:null,
                        pending_native_jobs:hub.state.jobs.filter(job=>
                            job.assigned_machine_id===identity&&job.dispatch_id&&
                            !job.capacity_released_at&&
                            !['finished','failed','stopped','cancelled'].includes(job.status))
                            .map(job=>job.id),
                    });
                }
                if(req.method==='GET'&&pathname==='/api/hub/agent/releases/bundle'&&
                    keys.length===3&&keys.every(key=>
                        ['machine_id','component','sha256'].includes(key))&&
                    Number(req.headers['content-length']||0)===0) {
                    const component=url.searchParams.get('component');
                    const sha=url.searchParams.get('sha256');
                    if(component!=='worker'&&
                        !hub.agentReports.get(identity)?.installed_projects?.includes(component))
                        return respond(res,403,{error:'Project not enrolled on this worker.'});
                    const archive=releases.archive(component,sha);
                    res.writeHead(200,{
                        'Content-Type':'application/x-tar',
                        'Content-Length':archive.bytes,
                        'Cache-Control':'no-store',
                        'X-Content-Type-Options':'nosniff',
                    });
                    const stream=require('node:fs').createReadStream(archive.path);
                    stream.on('error',()=>res.destroy());
                    stream.pipe(res);
                    return;
                }
                if(req.method==='POST'&&pathname==='/api/hub/agent/releases/status'&&
                    keys.length===1) {
                    const bytes=Number(req.headers['content-length']||0);
                    if(bytes<1||bytes>MAX_BODY_BYTES)
                        return respond(res,413,{error:'Invalid release status envelope.'});
                    const report=await readBody(req);
                    if(report.machine_id!==identity)throw Error('Machine status identity changed.');
                    return respond(res,200,hub.reportMachineSoftware(report,
                        req.headers['x-hub-agent-token']));
                }
                return respond(res,404,{error:'Unknown release route.'});
            }catch(error) {
                return respond(res,[401,403,409,413].includes(error.status)
                    ?error.status:400,{error:String(error.message||error).slice(0,240)});
            }
        }
        // Separate bounded binary transfers from the 64-KiB heartbeat.
        // Worker credentials are machine-scoped; job ownership and each
        // relative telemetry path are verified before accepting bytes.
        if(['GET','PUT'].includes(req.method)&&
            req.url?.startsWith('/api/hub/agent/artifacts?')) {
            try {
                if(req.url.length>1024||req.headers.origin)
                    return respond(res,404,{error:'Agent endpoint only.'});
                const url=new URL(req.url,'http://'+AGENT_HOST+':'+AGENT_PORT);
                const params=url.searchParams,keys=[...params.keys()];
                const required=req.method==='PUT'
                    ?['machine_id','job_id','file','sha','size','offset']
                    :['machine_id','job_id','file','sha','size'];
                if(url.pathname!=='/api/hub/agent/artifacts'||url.hash||
                    keys.length!==required.length||
                    new Set(keys).size!==required.length||
                    required.some(key=>!params.has(key)))
                    return respond(res,404,{error:'Agent endpoint only.'});
                const machine=params.get('machine_id'),job=params.get('job_id'),
                    file=params.get('file'),sha=params.get('sha');
                const rawSize=params.get('size');
                if(!/^[1-9][0-9]{0,8}$/.test(rawSize||''))
                    throw Error('Invalid artifact snapshot size.');
                const size=Number(rawSize);
                mirror.authorize(machine,req.headers['x-hub-agent-token'],
                    job,file,sha,size);
                if(req.method==='GET') {
                    if(Number(req.headers['content-length']||0)!==0)
                        return respond(res,400,{error:'Artifact status must not have a body.'});
                    return respond(res,200,await mirror.status(machine,
                        req.headers['x-hub-agent-token'],job,file,sha,size));
                }
                const declared=req.headers['content-length'];
                const offset=params.get('offset');
                if(!/^(0|[1-9][0-9]{0,8})$/.test(offset||'')||
                    !/^[1-9][0-9]{0,6}$/.test(declared||'')||
                    Number(declared)>MAX_CHUNK||
                    req.headers['content-type']!=='application/octet-stream')
                    return respond(res,413,{error:'Invalid artifact chunk envelope.'});
                let received=0;const chunks=[];
                for await(const chunk of req) {
                    received+=chunk.length;
                    if(received>MAX_CHUNK)
                        return respond(res,413,{error:'Artifact chunk too large.'});
                    chunks.push(chunk);
                }
                if(received!==Number(declared))
                    return respond(res,400,{error:'Artifact chunk length changed.'});
                return respond(res,200,await mirror.append(machine,
                    req.headers['x-hub-agent-token'],job,file,sha,size,
                    Number(offset),Buffer.concat(chunks,received)));
            }catch(error) {
                return respond(res,[401,409,413].includes(error.status)
                    ?error.status:400,{error:String(error.message||error)});
            }
        }
        if(req.method==='GET') {
            // Only an enrolled machine may inspect its own evaluator
            // worklist. Never expose the privileged /api/hub dashboard
            // routes or accept a machine identity without its token.
            let url;
            try{url=new URL(req.url,'http://'+AGENT_HOST+':'+AGENT_PORT);}
            catch{return respond(res,404,{error:'Agent endpoint only.'});}
            const keys=[...url.searchParams.keys()];
            if(req.url.length>512||url.pathname!=='/api/hub/agent/evaluation/targets'||
                url.hash||keys.length!==3||
                new Set(keys).size!==3||
                !['machine_id','project_id','suite_id'].every(key=>
                    url.searchParams.has(key))||
                Number(req.headers['content-length']||0)!==0)
                return respond(res,404,{error:'Agent endpoint only.'});
            try {
                const targets=getHub().authenticatedEvaluationTargets(
                    url.searchParams.get('machine_id'),
                    req.headers['x-hub-agent-token'],
                    url.searchParams.get('project_id'),
                    url.searchParams.get('suite_id'));
                return respond(res,200,{targets});
            }catch(error) {
                return respond(res,[401,409].includes(error.status)?error.status:400,{
                    error:String(error.message||error),
                });
            }
        }
        if(req.method!=='POST'||req.url!=='/api/hub/agent/heartbeat')
            return respond(res,404,{error:'Agent endpoint only.'});
        try {
            const size=Number(req.headers['content-length']||0);
            if(!Number.isFinite(size)||size<0||size>MAX_BODY_BYTES)
                return respond(res,413,{error:'Agent request too large.'});
            const value=await readBody(req);
            return respond(res,200,getHub().heartbeat(value,
                req.headers['x-hub-agent-token']));
        }catch(error) {
            return respond(res,[401,409,413].includes(error.status)?error.status:400,{
                error:String(error.message||error),
            });
        }
    });
}
module.exports={createAgentServer,AGENT_HOST,AGENT_PORT,MAX_BODY_BYTES,isTailnetIPv4,activeTailnetAddress};
