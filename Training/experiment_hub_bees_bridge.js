'use strict';

// Optional same-host, loopback-only bridge to the existing Bees Experiment
// Manager. The manager remains the sole Bees run/build/worker authority.
// A separate SSH local forward can connect Halifax to Exeter's loopback UI.
const http=require('node:http');

const ACTIONS=new Set(['start','stop','build','update-workers','import-workers']);
const READS=new Set(['control','workers']);
const BRIDGE_TIMEOUT_MS=3500;
const MAX_RESPONSE_BYTES=16*1024*1024;
const POLL_MS=10000;

class BeesBridge {
    constructor(port) {
        if(port===null||port===undefined||port==='') {
            this.port=null;
        }else{
            const text=String(port);
            const value=Number(text);
            if(!/^[1-9][0-9]*$/.test(text)||!Number.isSafeInteger(value)||
                value<1024||value>65535||[7148,7149].includes(value))
                throw Error('EXPERIMENT_HUB_BEES_BRIDGE_PORT must be a distinct, non-Hub loopback TCP port (1024–65535).');
            this.port=value;
        }
        this.lastSuccess=null;
        this.error=this.port?'Bees manager has not been reached.':'External Bees manager is not configured.';
        this.bees=null;
        this.lastAttempt=0;
        this.pending=null;
    }

    async request(method,route,payload=null) {
        if(!this.port) {
            const error=Error('External Bees manager is not configured.');
            error.status=503;
            throw error;
        }
        const routeValid=(method==='GET'&&(
            route==='/api/hub/bees/summary'||
            route==='/api/hub/progress?project_id=bees'||
            [...READS].some(name=>route==='/api/hub/bees/'+name)))||
            (method==='POST'&&[...ACTIONS].some(name=>route==='/api/hub/bees/'+name));
        if(!routeValid)throw Error('Unsupported Bees bridge request.');
        const body=payload===null?null:Buffer.from(JSON.stringify(payload),'utf8');
        if(body&&body.length>65536)throw Error('Bees bridge request exceeds 64 KiB.');
        return new Promise((resolve,reject)=>{
            const req=http.request({
                hostname:'127.0.0.1',
                port:this.port,
                method,
                path:route,
                // The Bees manager is bound to 127.0.0.1:7148 on Exeter.
                // SSH forwarding leaves Host unchanged unless set here.
                headers:{
                    Host:'127.0.0.1:7148',
                    ...(body?{
                        'Content-Type':'application/json; charset=utf-8',
                        'Content-Length':body.length,
                    }:{}),
                },
            },res=>{
                const chunks=[];
                let size=0;
                res.on('data',chunk=>{
                    size+=chunk.length;
                    if(size>MAX_RESPONSE_BYTES){
                        req.destroy(Error('Bees manager response exceeds 16 MiB.'));
                        return;
                    }
                    chunks.push(chunk);
                });
                res.once('end',()=>{
                    try{
                        if(res.statusCode<200||res.statusCode>=300) {
                            let upstream=null;
                            try{upstream=JSON.parse(Buffer.concat(chunks).toString('utf8'));}
                            catch{}
                            const error=Error(String(upstream?.error||
                                'Bees manager returned HTTP '+res.statusCode));
                            error.status=[400,401,403,404,409,413].includes(res.statusCode)
                                ?res.statusCode:502;
                            return reject(error);
                        }
                        const value=JSON.parse(
                            new TextDecoder('utf-8',{fatal:true})
                                .decode(Buffer.concat(chunks,size)));
                        resolve(value);
                    }catch(error){reject(error);}
                });
            });
            req.setTimeout(BRIDGE_TIMEOUT_MS,()=>{
                req.destroy(Error('Bees manager did not respond before timeout; operation outcome may be unknown.'));
            });
            req.once('error',reject);
            if(body)req.end(body);else req.end();
        });
    }

    async refresh(force=false) {
        if(!this.port)return;
        if(this.pending)return this.pending;
        if(!force&&Date.now()-this.lastAttempt<POLL_MS)return;
        this.lastAttempt=Date.now();
        this.pending=(async()=>{
            try{
                const data=await this.request('GET','/api/hub/bees/summary');
                if(!data||!Array.isArray(data.runs))
                    throw Error('Bees manager returned an invalid experiment queue snapshot.');
                this.bees={
                    active_id:data.active_id||null,
                    queue_armed:Boolean(data.queue_armed),
                    runs:data.runs,
                };
                this.lastSuccess=new Date().toISOString();
                this.error=null;
            }catch(error){
                this.error=String(error.message||error);
            }finally{
                this.pending=null;
            }
        })();
        return this.pending;
    }

    snapshot() {
        if(this.port&&!this.pending&&Date.now()-this.lastAttempt>=POLL_MS)
            void this.refresh();
        return this.bees?{
            ...this.bees,
            bridge_connected:!this.error,
            bridge_last_success:this.lastSuccess,
            bridge_error:this.error,
        }:{
            active_id:null,queue_armed:false,runs:[],
            bridge_connected:false,
            bridge_last_success:this.lastSuccess,
            bridge_error:this.error,
        };
    }

    status() {
        const snapshot=this.snapshot();
        return {
            configured:!!this.port,
            connected:snapshot.bridge_connected,
            last_success:snapshot.bridge_last_success,
            error:snapshot.bridge_error,
            active_id:snapshot.active_id,
            queue_armed:snapshot.queue_armed,
            run_count:snapshot.runs.length,
            owner:'external-bees-experiment-manager',
        };
    }

    async forward(method,route,payload=null) {
        if(method==='POST') {
            const action=route.slice('/api/hub/bees/'.length);
            const confirmation={
                start:payload?.mode==='new'?payload?.confirm_new_run===true:
                    payload?.mode==='resume',
                stop:payload?.confirm_stop===true,
                build:payload?.confirm_build===true,
                'update-workers':payload?.confirm_update===true,
                'import-workers':payload?.confirm_import===true,
            };
            if(!Object.prototype.hasOwnProperty.call(confirmation,action)||
                !confirmation[action]) {
                const error=Error('Explicit Bees operator action confirmation required.');
                error.status=409;
                throw error;
            }
            // Avoid automatic replay after a connection failure: the Bees
            // operator may already have accepted the command.
            const value=await this.request(method,route,payload);
            void this.refresh(true);
            return value;
        }
        return this.request(method,route);
    }
}

module.exports={BeesBridge};
