'use strict';

// Restricted, worker-only loopback listener shared by the Bees coordinator
// and isolated Hub transport qualification. The caller binds to loopback;
// neither project administration nor Bees training routes are available here.
const http=require('node:http');
const AGENT_HOST='127.0.0.1';
const AGENT_PORT=7149;
const MAX_BODY_BYTES=32768;

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
    return JSON.parse(Buffer.concat(chunks,received).toString('utf8')||'{}');
}

function createAgentServer(getHub) {
    if(typeof getHub!=='function')
        throw Error('Agent endpoint requires a local Hub provider.');
    return http.createServer(async(req,res)=>{
        const host=req.headers.host;
        if(req.method!=='POST'||
            req.url!=='/api/hub/agent/heartbeat'||
            (host!==AGENT_HOST+':'+AGENT_PORT&&
                host!=='localhost:'+AGENT_PORT)||
            req.headers.origin)
            return respond(res,404,{error:'Agent endpoint only.'});
        try {
            const size=Number(req.headers['content-length']||0);
            if(!Number.isFinite(size)||size<0||size>MAX_BODY_BYTES)
                return respond(res,413,{error:'Agent request too large.'});
            const value=await readBody(req);
            return respond(res,200,getHub().heartbeat(value,
                req.headers['x-hub-agent-token']));
        }catch(error) {
            return respond(res,[401,413].includes(error.status)?error.status:400,{
                error:String(error.message||error),
            });
        }
    });
}
module.exports={createAgentServer,AGENT_HOST,AGENT_PORT};
