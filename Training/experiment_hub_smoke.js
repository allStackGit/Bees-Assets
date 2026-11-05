#!/usr/bin/env node
'use strict';

// Isolated real HTTP qualification; no Bees operator or training imported.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const http=require('node:http');
const os=require('node:os');
const path=require('node:path');
const {ExperimentHub}=require('./experiment_hub_core');
const {endpoint,observations,observedOffers,sendHeartbeat}=
    require('./experiment_hub_agent');

async function run() {
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'experiment-hub-transport-'));
    let server;
    try {
        const hub=new ExperimentHub(root);
        hub.addProject({id:'smoke',name:'Isolated smoke project'});
        hub.addMachine({id:'smoke',name:'Isolated smoke agent',
            cpus:1,ram_mb:256,gpus:[]});
        const token=hub.enrollMachine({
            machine_id:'smoke',confirm_enroll:true,
        }).agent_token;
        server=http.createServer(async(req,res)=>{
            const send=(status,data)=>{
                res.writeHead(status,{'Content-Type':'application/json',
                    'Cache-Control':'no-store'});
                res.end(JSON.stringify(data));
            };
            if(req.method!=='POST'||
                req.url!=='/api/hub/agent/heartbeat'||
                req.headers.host!=='127.0.0.1:7149'||req.headers.origin)
                return send(404,{error:'Agent endpoint only.'});
            try {
                let size=0;
                const chunks=[];
                for await(const chunk of req) {
                    size+=chunk.length;
                    if(size>32768)throw Error('Agent request too large.');
                    chunks.push(chunk);
                }
                return send(200,hub.heartbeat(
                    JSON.parse(Buffer.concat(chunks).toString('utf8')),
                    req.headers['x-hub-agent-token']));
            }catch(error) {
                return send(error.status===401?401:400,
                    {error:String(error.message||error)});
            }
        });
        await new Promise((resolve,reject)=>{
            server.once('error',reject);
            server.listen(0,'127.0.0.1',resolve);
        });
        const url=endpoint('http://127.0.0.1:'+server.address().port);
        await assert.rejects(
            sendHeartbeat(url,'smoke','0'.repeat(64)),
            /Agent authentication failed/);
        const first=await sendHeartbeat(url,'smoke',token);
        assert.equal(first.status,'connected');
        assert.equal(hub.snapshot().machines[0].status,'connected');
        const lease=hub.requestLease({
            project_id:'smoke',machine_id:'smoke',
            resources:{cpus:1,ram_mb:128},confirm_request:true,
        });
        const response=await sendHeartbeat(url,'smoke',token);
        assert(response.lease_offers.some(o=>o.id===lease.id));
        const receipts=observedOffers(
            response.lease_offers,observations('smoke'));
        assert.deepEqual(receipts,[lease.id]);
        await sendHeartbeat(url,'smoke',token,receipts);
        assert.equal(hub.snapshot().reservations[0].receipt_fresh,true);
        assert.throws(()=>hub.configureAutomation({
            enabled:true,machine_ids:['smoke'],confirm_arm:true,
            confirm_untracked_load:true,
        }),/native agent/);
        hub.releaseLease({lease_id:lease.id,confirm_release:true});
        const restarted=new ExperimentHub(root);
        assert.equal(restarted.state.reservations[0].status,'released');
        assert.equal(restarted.snapshot().machines[0].status,'offline');
        assert.equal(restarted.automationStatus().enabled,false);
        console.log('PASS: isolated real loopback transport, authentication, '+
            'receipts, release, persistence and inert automation.');
        console.log('No native process or production registry was touched.');
    }finally {
        if(server?.listening)await new Promise((resolve,reject)=>
            server.close(error=>error?reject(error):resolve()));
        fs.rmSync(root,{recursive:true,force:true});
    }
}
if(require.main===module)run().catch(error=>{
    console.error('[experiment-hub-smoke] '+String(error.stack||error));
    process.exitCode=1;
});
module.exports={run};
