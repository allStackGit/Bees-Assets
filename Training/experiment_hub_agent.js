#!/usr/bin/env node
'use strict';

// Cross-platform Experiment Hub presence and lease-receipt agent.
// The only agent output is machine observations and acknowledged offer IDs.
// It never runs commands, launches jobs, or enforces hardware quotas.
const http = require('node:http');
const os = require('node:os');

const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const TOKEN = /^[0-9a-f]{64}$/;
const INTERVAL_MS = 15000;

function endpoint(input) {
    const url = new URL(input || 'http://127.0.0.1:7148');
    if (url.protocol !== 'http:' ||
        !['127.0.0.1','localhost'].includes(url.hostname) ||
        url.username || url.password || url.search || url.hash ||
        !['','/'].includes(url.pathname)) {
        throw Error('Agent endpoint must be a local HTTP address; use a trusted SSH tunnel for remote machines.');
    }
    url.pathname='/api/hub/agent/heartbeat';
    return url;
}

function observations(machineId) {
    if (typeof machineId !== 'string' || !ID.test(machineId))
        throw Error('EXPERIMENT_HUB_MACHINE_ID must match a registered machine ID.');
    return {
        machine_id:machineId,
        cpus:Math.max(1,typeof os.availableParallelism==='function'
            ?os.availableParallelism():os.cpus().length),
        ram_mb:Math.max(128,Math.floor(os.totalmem()/1048576)),
        platform:os.platform(),
    };
}

function observedOffers(offers,observation) {
    if (!Array.isArray(offers)||offers.length>100)
        throw Error('Invalid coordination lease offer list.');
    const ids=new Set();
    let claimedCpus=0,claimedRam=0;
    for (const offer of offers) {
        const r=offer?.resources;
        if (!offer || typeof offer.id!=='string' ||
            !/^[0-9a-f-]{36}$/.test(offer.id) ||
            !['offered','acknowledged'].includes(offer.status) ||
            ids.has(offer.id) || !r ||
            !Number.isSafeInteger(r.cpus) || r.cpus<1 ||
            !Number.isSafeInteger(r.ram_mb) || r.ram_mb<1)
            throw Error('Invalid coordination lease offer.');
        ids.add(offer.id);
        claimedCpus+=r.cpus;
        claimedRam+=r.ram_mb;
    }
    // Do not claim to have seen a workload arrangement beyond observed
    // CPU/RAM capacity. Physical availability and GPU identity are unproven.
    if (claimedCpus>observation.cpus || claimedRam>observation.ram_mb)
        return [];
    return [...ids];
}

function sendHeartbeat(url,machineId,token,observedLeaseIds=[]) {
    if (typeof token !== 'string' || !TOKEN.test(token))
        return Promise.reject(Error('Missing/invalid EXPERIMENT_HUB_AGENT_TOKEN.'));
    if (!Array.isArray(observedLeaseIds) || observedLeaseIds.length>100 ||
        observedLeaseIds.some(id=>typeof id!=='string' ||
            !/^[0-9a-f-]{36}$/.test(id)))
        return Promise.reject(Error('Invalid lease acknowledgement IDs.'));
    const payload=JSON.stringify({
        ...observations(machineId),observed_lease_ids:observedLeaseIds,
    });
    return new Promise((resolve,reject)=>{
        const req=http.request(url,{
            method:'POST',timeout:10000,
            headers:{
                // The manager remains loopback-bound and expects its own
                // origin. This works with a local SSH forwarded port.
                Host:'127.0.0.1:7148',
                'Content-Type':'application/json',
                'Content-Length':Buffer.byteLength(payload),
                'X-Hub-Agent-Token':token,
            },
        },res=>{
            let text='';
            res.setEncoding('utf8');
            res.on('data',chunk=>{
                text+=chunk;
                if(text.length>65536)req.destroy(Error('Oversized hub reply.'));
            });
            res.on('end',()=>{
                let parsed;
                try{parsed=JSON.parse(text);}catch{reject(Error('Invalid hub response.'));return;}
                if(res.statusCode!==200||parsed?.status!=='connected') {
                    reject(Error(parsed?.error||'Agent heartbeat refused: HTTP '+res.statusCode));
                    return;
                }
                resolve(parsed);
            });
        });
        req.on('timeout',()=>req.destroy(Error('Agent heartbeat timed out.')));
        req.on('error',reject);
        req.end(payload);
    });
}

async function main() {
    const url=endpoint(process.env.EXPERIMENT_HUB_URL);
    const id=process.env.EXPERIMENT_HUB_MACHINE_ID;
    const token=process.env.EXPERIMENT_HUB_AGENT_TOKEN;
    observations(id);
    if(!TOKEN.test(token||''))throw Error('Set the one-time enrolled agent credential in EXPERIMENT_HUB_AGENT_TOKEN.');
    let active=false,last='initial',receipts=[];
    const poll=async()=>{
        if(active)return;
        active=true;
        try{
            const response=await sendHeartbeat(url,id,token,receipts);
            receipts=observedOffers(response.lease_offers||[],observations(id));
            if(last!=='connected')console.log('[experiment-hub-agent] connected ('+id+'); coordination only, no job execution.');
            last='connected';
        }catch(error){
            const message=String(error.message||error);
            if(last!==message)console.error('[experiment-hub-agent] '+message);
            last=message;
        }finally{active=false;}
    };
    await poll();
    if(process.argv.includes('--once')) {
        if(last!=='connected')process.exitCode=1;
        else if(receipts.length) {
            try { await sendHeartbeat(url,id,token,receipts); }
            catch(error) {
                console.error('[experiment-hub-agent] Lease receipt failed: '+error.message);
                process.exitCode=1;
            }
        }
        return;
    }
    setInterval(poll,INTERVAL_MS);
}

if(require.main===module)main().catch(error=>{
    console.error('[experiment-hub-agent] '+error.message);
    process.exitCode=1;
});
module.exports={endpoint,observations,observedOffers,sendHeartbeat};
