'use strict';

// Restricted, worker-only loopback listener shared by the Bees coordinator
// and isolated Hub transport qualification. The caller binds to loopback;
// neither project administration nor Bees training routes are available here.
const http=require('node:http');
const fs=require('node:fs');
const {ArtifactMirror,MAX_CHUNK}=require('./experiment_hub_artifact_sync');
const {authorizedBundleFile}=require('./experiment_hub_native_bundle_transfer');
const AGENT_HOST='127.0.0.1';
const AGENT_PORT=7149;
// A heartbeat includes machine/lease reports beyond the separately
// validated 32-KiB trainer progress summary.
const MAX_BODY_BYTES=65536;

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

function createAgentServer(getHub) {
    if(typeof getHub!=='function')
        throw Error('Agent endpoint requires a local Hub provider.');
    const mirror=new ArtifactMirror(getHub);
    return http.createServer(async(req,res)=>{
        const host=req.headers.host;
        if((host!==AGENT_HOST+':'+AGENT_PORT&&
            host!=='localhost:'+AGENT_PORT)||req.headers.origin)
            return respond(res,404,{error:'Agent endpoint only.'});
        // A learner may retrieve only a completed collector bundle pinned
        // in its own frozen job. Authentication and relative capture paths
        // are checked against the durable Hub and mirrored receipt.
        if(req.method==='GET'&&
            req.url?.startsWith('/api/hub/agent/native-bundle-file?')) {
            try {
                if(req.url.length>1024||
                    Number(req.headers['content-length']||0)!==0)
                    return respond(res,404,{error:'Agent endpoint only.'});
                const url=new URL(req.url,'http://'+AGENT_HOST+':'+AGENT_PORT);
                const keys=[...url.searchParams.keys()];
                const required=['machine_id','learner_job_id','collector_job_id','file'];
                if(url.pathname!=='/api/hub/agent/native-bundle-file'||
                    url.hash||keys.length!==4||new Set(keys).size!==4||
                    required.some(key=>!url.searchParams.has(key)))
                    return respond(res,404,{error:'Agent endpoint only.'});
                const source=authorizedBundleFile(getHub(),
                    url.searchParams.get('machine_id'),
                    req.headers['x-hub-agent-token'],
                    url.searchParams.get('learner_job_id'),
                    url.searchParams.get('collector_job_id'),
                    url.searchParams.get('file'));
                const stream=fs.createReadStream(source.file,{highWaterMark:MAX_CHUNK});
                stream.on('error',()=>res.destroy());
                res.writeHead(200,{
                    'Content-Type':'application/octet-stream',
                    'Content-Length':source.size,
                    'X-Native-Bundle-SHA256':source.sha,
                    'Cache-Control':'no-store',
                    'X-Content-Type-Options':'nosniff',
                });
                stream.pipe(res);
                return;
            }catch(error) {
                return respond(res,[401,409,413].includes(error.status)
                    ?error.status:400,{error:String(error.message||error)});
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
module.exports={createAgentServer,AGENT_HOST,AGENT_PORT,MAX_BODY_BYTES};
