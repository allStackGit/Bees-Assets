'use strict';

// Restricted, worker-only loopback listener shared by the Bees coordinator
// and isolated Hub transport qualification. The caller binds to loopback;
// neither project administration nor Bees training routes are available here.
const http=require('node:http');
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
    return http.createServer(async(req,res)=>{
        const host=req.headers.host;
        if((host!==AGENT_HOST+':'+AGENT_PORT&&
            host!=='localhost:'+AGENT_PORT)||req.headers.origin)
            return respond(res,404,{error:'Agent endpoint only.'});
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
