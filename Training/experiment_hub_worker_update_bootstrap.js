#!/usr/bin/env node
'use strict';

// Permanent, dependency-free update entrypoint. The OS scheduler invokes
// this file, never the replaceable updater. Simulation Python, uv, .NET,
// adapter loading and service restarts are deliberately absent here.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const net=require('node:net');
const {spawnSync}=require('node:child_process');

const SHA=/^[0-9a-f]{40}$/, HASH=/^[0-9a-f]{64}$/;
const MAX_BYTES=512*1024*1024;
const MAX_JSON=128*1024;
function fatal(message){throw Error('[update-bootstrap] '+message);}
function command(program,args,timeout=180000) {
    const result=spawnSync(program,args,{encoding:'utf8',timeout,
        maxBuffer:8*1024*1024,windowsHide:true});
    if(result.error||result.status!==0)
        fatal(program+': '+String(result.error?.message||
            result.stderr||result.stdout||result.status).slice(0,240));
    return result.stdout.trim();
}
function atomic(file,content) {
    const temp=file+'.'+process.pid+'.tmp';
    try {
        fs.writeFileSync(temp,content,{flag:'wx',mode:0o600});
        fs.renameSync(temp,file);
    }finally {try{fs.unlinkSync(temp);}catch{}}
}
function identity(root) {
    const file=path.join(root,process.platform==='win32'?'Private':'private','worker.env');
    if(!fs.statSync(file).isFile()||fs.lstatSync(file).isSymbolicLink())
        fatal('Missing private machine enrollment.');
    const entries={};
    for(const row of fs.readFileSync(file,'utf8').split(/\r?\n/)) {
        if(!row||row.startsWith('#'))continue;
        const i=row.indexOf('=');
        if(i<=0||Object.hasOwn(entries,row.slice(0,i)))fatal('Invalid worker enrollment.');
        entries[row.slice(0,i)]=row.slice(i+1);
    }
    const url=new URL(entries.EXPERIMENT_HUB_URL||'');
    const host=url.hostname;
    const octets=host.split('.').map(Number);
    if(url.protocol!=='http:'||url.port!=='7149'||
        url.username||url.password||url.search||url.hash||
        !['','/'].includes(url.pathname)||
        !(host==='127.0.0.1'||host==='localhost'||
            net.isIP(host)===4&&octets[0]===100&&
            octets[1]>=64&&octets[1]<=127)||
        !/^[a-z][a-z0-9_-]{0,47}$/.test(entries.EXPERIMENT_HUB_MACHINE_ID||'')||
        !HASH.test(entries.EXPERIMENT_HUB_AGENT_TOKEN||''))
        fatal('Invalid authenticated Hub endpoint or machine token.');
    return {base:url,id:entries.EXPERIMENT_HUB_MACHINE_ID,
        token:entries.EXPERIMENT_HUB_AGENT_TOKEN};
}
function getJson(peer,route) {
    return new Promise((resolve,reject)=>{
        const url=new URL('/api/hub/agent/releases'+route,peer.base);
        url.searchParams.set('machine_id',peer.id);
        const req=http.get(url,{timeout:30000,
            headers:{'x-hub-agent-token':peer.token}},res=>{
            if(res.statusCode!==200) {
                res.resume();
                return reject(Error('Hub release HTTP '+res.statusCode));
            }
            const parts=[];let bytes=0;
            res.on('data',chunk=>{
                bytes+=chunk.length;
                if(bytes>MAX_JSON)req.destroy(Error('Release manifest too large.'));
                else parts.push(chunk);
            });
            res.on('end',()=>{
                try{resolve(JSON.parse(Buffer.concat(parts).toString('utf8')));}
                catch(error){reject(error);}
            });
            res.on('error',reject);
        });
        req.on('timeout',()=>req.destroy(Error('Hub release request timed out.')));
        req.on('error',reject);
    });
}
function reportWorkerFailure(peer,entry,reason) {
    if(!entry||!SHA.test(entry.version||'')||!HASH.test(entry.sha256||''))
        return Promise.resolve();
    const url=new URL('/api/hub/agent/releases/status',peer.base);
    url.searchParams.set('machine_id',peer.id);
    const payload=Buffer.from(JSON.stringify({
        machine_id:peer.id,component:'worker',version:entry.version,
        sha256:entry.sha256,status:'failed',message:String(reason).slice(0,240),
    }));
    return new Promise(resolve=>{
        const req=http.request(url,{method:'POST',timeout:10000,
            headers:{'x-hub-agent-token':peer.token,'content-type':'application/json',
                'content-length':payload.length}},res=>{
            res.resume();res.on('end',resolve);
        });
        req.on('timeout',()=>req.destroy());
        req.on('error',()=>resolve());
        req.end(payload);
    });
}
function download(peer,entry,destination) {
    return new Promise((resolve,reject)=>{
        const url=new URL('/api/hub/agent/releases/bundle',peer.base);
        url.searchParams.set('machine_id',peer.id);
        url.searchParams.set('component','worker');
        url.searchParams.set('sha256',entry.sha256);
        const hash=crypto.createHash('sha256');
        let received=0,done=false;
        const fail=error=>{
            if(done)return;
            done=true;
            try{fs.unlinkSync(destination);}catch{}
            reject(error);
        };
        const req=http.get(url,{timeout:240000,
            headers:{'x-hub-agent-token':peer.token}},res=>{
            if(res.statusCode!==200) {
                res.resume();
                return fail(Error('Worker bundle HTTP '+res.statusCode));
            }
            const stream=fs.createWriteStream(destination,{flags:'wx',mode:0o600});
            res.on('data',chunk=>{
                received+=chunk.length;
                if(received>entry.bytes||received>MAX_BYTES) {
                    req.destroy(Error('Worker bundle exceeds approved size.'));
                    return;
                }
                hash.update(chunk);
            });
            res.on('error',fail);
            stream.on('error',fail);
            res.pipe(stream);
            stream.on('finish',()=>{
                if(done)return;
                if(received!==entry.bytes||hash.digest('hex')!==entry.sha256)
                    return fail(Error('Worker release checksum or size mismatch.'));
                done=true;
                resolve();
            });
        });
        req.on('timeout',()=>req.destroy(Error('Worker download timed out.')));
        req.on('error',fail);
    });
}
function checkedUpdater(file) {
    if(!fs.existsSync(file))fatal('Updater script not installed: '+file);
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink())fatal('Unsafe updater script.');
    command(process.execPath,['--check',file],30000);
    return file;
}
async function releaseEngine(root,peer,entry) {
    if(!entry||entry.component!=='worker'||!SHA.test(entry.version||'')||
        !HASH.test(entry.sha256||'')||
        !Number.isSafeInteger(entry.bytes)||entry.bytes<1||
        entry.bytes>MAX_BYTES||entry.activation_ready!==true)
        fatal('Worker release manifest does not advertise an active immutable version.');
    const base=path.join(root,'Software','UpdateEngines');
    const target=path.join(base,entry.version);
    const main=path.join(target,'Training','experiment_hub_worker_updater.js');
    const verified=path.join(target,'.verified-'+entry.sha256);
    if(fs.existsSync(verified))
        return checkedUpdater(main);
    if(fs.existsSync(target))
        fatal('Refusing to overwrite incomplete or unverified update engine.');
    fs.mkdirSync(base,{recursive:true,mode:0o700});
    const archive=path.join(base,'.worker-'+process.pid+'.tar');
    const staging=path.join(base,'.engine-'+process.pid+'.stage');
    if(fs.existsSync(archive)||fs.existsSync(staging))
        fatal('Another updater extraction may be active.');
    try {
        await download(peer,entry,archive);
        const tar=process.platform==='win32'?'tar.exe':'tar';
        const listing=command(tar,['-tf',archive]).split(/\r?\n/).filter(Boolean);
        if(!listing.length||listing.length>100000||
            listing.some(name=>name.startsWith('/')||name.startsWith('\\')||
                name.includes('\\')||name.includes(':')||
                name.split('/').includes('..')||name.includes('\0')))
            fatal('Unsafe worker release archive paths.');
        const details=command(tar,['-tvf',archive]);
        if(details.split(/\r?\n/).some(line=>/^[lh]/.test(line)))
            fatal('Worker release contains a forbidden link.');
        fs.mkdirSync(staging,{mode:0o700});
        command(tar,['-xf',archive,'-C',staging],240000);
        checkedUpdater(path.join(staging,'Training','experiment_hub_worker_updater.js'));
        fs.writeFileSync(path.join(staging,'.verified-'+entry.sha256),
            entry.version+'\n',{mode:0o600});
        fs.renameSync(staging,target);
        return checkedUpdater(main);
    }finally {
        try{fs.unlinkSync(archive);}catch{}
        if(fs.existsSync(staging))
            fs.rmSync(staging,{recursive:true,force:true});
    }
}
function lastGood(root) {
    const file=path.join(root,'Software','update-engine.json');
    if(!fs.existsSync(file))return null;
    let saved;
    try{saved=JSON.parse(fs.readFileSync(file,'utf8'));}catch{return null;}
    if(!SHA.test(saved.version||'')||!HASH.test(saved.sha256||''))return null;
    const stage=path.join(root,'Software','UpdateEngines',saved.version);
    if(!fs.existsSync(path.join(stage,'.verified-'+saved.sha256)))return null;
    try{return {entry:saved,
        script:checkedUpdater(path.join(stage,'Training','experiment_hub_worker_updater.js'))};}
    catch{return null;}
}
function invoke(script,root,component) {
    const result=spawnSync(process.execPath,[script,'--root',root,'--component',component],{
        stdio:'inherit',timeout:3*60*60*1000,windowsHide:true,
    });
    if(result.error||result.status!==0) {
        console.error('[update-bootstrap] '+component+' via '+script+
            ' failed: '+String(result.error?.message||result.signal||result.status));
        return false;
    }
    return true;
}
async function main(argv=process.argv.slice(2)) {
    if(argv.length!==2||argv[0]!=='--root'||!path.isAbsolute(argv[1]))
        fatal('Usage: node experiment_hub_worker_update_bootstrap.js --root ABSOLUTE_PATH');
    const root=path.resolve(argv[1]);
    if(!fs.statSync(root).isDirectory())fatal('Worker root is missing.');
    const peer=identity(root);
    const manifest=await getJson(peer,'');
    if(!manifest||!manifest.components||!Array.isArray(manifest.pending_native_jobs))
        fatal('Invalid authenticated software manifest.');
    const worker=manifest.components.worker;
    let good=lastGood(root);
    let selected=null;
    let newCandidate=null;
    if(worker?.activation_ready===true) {
        try{newCandidate=await releaseEngine(root,peer,worker);}
        catch(error){
            console.error('[update-bootstrap] Worker download or verification failed: '+error.message);
            await reportWorkerFailure(peer,worker,error.message);
        }
    }
    if(newCandidate) {
        if(invoke(newCandidate,root,'worker')) {
            // Promote only after the infrastructure phase completes. A
            // simulation failure later cannot evict this known-good engine.
            atomic(path.join(root,'Software','update-engine.json'),
                JSON.stringify({version:worker.version,sha256:worker.sha256})+'\n');
            good={script:newCandidate,entry:worker};
            selected=newCandidate;
        }else{
            await reportWorkerFailure(peer,worker,
                'Candidate infrastructure updater did not complete; retaining last-good engine.');
        }
    }
    if(!selected) {
        selected=good?.script||path.join(root,'Update','experiment_hub_worker_updater.js');
        if(!newCandidate||selected!==newCandidate) {
            checkedUpdater(selected);
            if(!invoke(selected,root,'worker'))
                fatal('Current infrastructure updater failed; previous working engine retained.');
        }else fatal('Worker infrastructure update failed without a recovery engine.');
    }
    // Each simulation is an independent invocation. Its Python/uv failure
    // does not change update-engine.json or prevent other project updates.
    let failed=false;
    for(const component of ['tinybrains','courier','bees']) {
        if(!manifest.components[component])continue;
        if(!invoke(selected,root,component))failed=true;
    }
    if(failed)process.exitCode=1;
}
if(require.main===module)main().catch(error=>{
    console.error(String(error.message||error));process.exitCode=1;
});
module.exports={main};
