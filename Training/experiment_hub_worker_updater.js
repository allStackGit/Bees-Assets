#!/usr/bin/env node
'use strict';

// Dedicated machine-local periodic updater. It is not an experiment owner.
// Signed-in tailnet + Hub machine credential authorize versioned source
// artifacts; SHA-256 verifies every downloaded byte before extraction.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {isTailnetIPv4}=require('./experiment_hub_agent_listener');
const ID=/^[a-z][a-z0-9_-]{0,47}$/;
const SHA=/^[0-9a-f]{40}$/;
const HASH=/^[0-9a-f]{64}$/;
const TERMINAL=new Set(['finished','failed','stopped']);
const MAX=512*1024*1024;
const SERVICE=process.platform==='win32'?'Bees-ExperimentHub-agent':
    'bees-experiment-hub-agent.service';
function exec(program,args,options={}) {
    const call=spawnSync(program,args,{encoding:'utf8',timeout:240000,
        maxBuffer:1024*1024,windowsHide:true,...options});
    if(call.error||call.status!==0)
        throw Error(program+' failed: '+String(call.error?.message||
            call.stderr||call.stdout||call.status).slice(0,250));
    return call.stdout.trim();
}
function save(file,content) {
    const temp=file+'.'+process.pid+'.tmp';
    try {
        fs.writeFileSync(temp,content,{flag:'wx',mode:0o600});
        fs.renameSync(temp,file);
    }finally{try{fs.unlinkSync(temp);}catch{}}
}
function config(file) {
    if(!fs.statSync(file).isFile()||fs.lstatSync(file).isSymbolicLink())
        throw Error('Private worker environment file must be a regular file.');
    const lines=fs.readFileSync(file,'utf8').split(/\r?\n/);
    const values={};
    for(const line of lines) {
        if(!line||line.startsWith('#'))continue;
        const index=line.indexOf('=');
        if(index<1)throw Error('Invalid environment file');
        const name=line.slice(0,index),value=line.slice(index+1);
        if(!/^EXPERIMENT_HUB_[A-Z0-9_]+$/.test(name)||Object.hasOwn(values,name)||
            /[\r\n\x00]/.test(value))throw Error('Invalid private environment field.');
        values[name]=value;
    }
    return values;
}
function sourcePath(env,root) {
    const url=new URL(env.EXPERIMENT_HUB_URL||'');
    if(url.protocol!=='http:'||url.port!=='7149'||
        (!isTailnetIPv4(url.hostname)&&!['127.0.0.1','localhost'].includes(url.hostname))||
        url.username||url.password||url.search||url.hash||!['/',''].includes(url.pathname))
        throw Error('Updater requires the restricted Hub Tailscale endpoint.');
    if(!ID.test(env.EXPERIMENT_HUB_MACHINE_ID||'')||
        !HASH.test(env.EXPERIMENT_HUB_AGENT_TOKEN||''))
        throw Error('Updater has no valid enrolled machine identity.');
    if(!path.isAbsolute(root)||!fs.statSync(root).isDirectory())
        throw Error('Updater requires an installed absolute local root.');
    return url;
}
function request(base,env,method,route,query=null,body=null,max=65536) {
    return new Promise((resolve,reject)=>{
        const url=new URL('/api/hub/agent/releases'+route,base);
        url.searchParams.set('machine_id',env.EXPERIMENT_HUB_MACHINE_ID);
        for(const [key,value] of Object.entries(query||{}))url.searchParams.set(key,value);
        const payload=body===null?null:Buffer.from(JSON.stringify(body),'utf8');
        const req=http.request(url,{method,timeout:30000,
            headers:{'x-hub-agent-token':env.EXPERIMENT_HUB_AGENT_TOKEN,
                ...(payload?{'content-type':'application/json','content-length':payload.length}:{})}},
        res=>{
            if(res.statusCode!==200){res.resume();return reject(Error('Hub release HTTP '+res.statusCode));}
            const data=[];let length=0;
            res.on('data',b=>{length+=b.length;if(length>max)req.destroy(Error('Release response too large.'));
                else data.push(b);});
            res.on('end',()=>{try{resolve(JSON.parse(Buffer.concat(data).toString('utf8')));}
                catch(e){reject(e);}});
            res.on('error',reject);
        });
        req.on('timeout',()=>req.destroy(Error('Hub release request timed out.')));
        req.on('error',reject);
        req.end(payload);
    });
}
function download(base,env,entry,destination) {
    return new Promise((resolve,reject)=>{
        const url=new URL('/api/hub/agent/releases/bundle',base);
        for(const [key,val] of Object.entries({
            machine_id:env.EXPERIMENT_HUB_MACHINE_ID,
            component:entry.component,sha256:entry.sha256
        }))url.searchParams.set(key,val);
        const hash=crypto.createHash('sha256');
        let bytes=0,done=false;
        const fail=error=>{
            if(done)return;done=true;
            try{fs.unlinkSync(destination);}catch{}
            reject(error);
        };
        const req=http.get(url,{timeout:120000,headers:{
            'x-hub-agent-token':env.EXPERIMENT_HUB_AGENT_TOKEN}},res=>{
            if(res.statusCode!==200)return fail(Error('Release bundle HTTP '+res.statusCode));
            const stream=fs.createWriteStream(destination,{flags:'wx',mode:0o600});
            stream.on('error',fail);
            res.on('data',part=>{
                bytes+=part.length;
                if(bytes>MAX||bytes>entry.bytes){req.destroy();return fail(Error('Release size exceeds manifest.'));}
                hash.update(part);
            });
            res.pipe(stream);
            stream.on('finish',()=>{
                if(done)return;
                if(bytes!==entry.bytes||hash.digest('hex')!==entry.sha256)
                    return fail(Error('Release archive SHA-256/length mismatch.'));
                done=true;resolve();
            });
            res.on('error',fail);
        });
        req.on('timeout',()=>req.destroy(Error('Release download timed out.')));
        req.on('error',fail);
    });
}
async function status(base,env,entry,state,message='') {
    try {
        await request(base,env,'POST','/status',null,{
            machine_id:env.EXPERIMENT_HUB_MACHINE_ID,
            component:entry.component,version:entry.version,
            sha256:entry.sha256,status:state,message:String(message).slice(0,250)
        });
    }catch(error){console.error('[worker-updater] Report: '+error.message);}
}
function journalActive(root) {
    const file=path.join(root,process.platform==='win32'
        ?'NativeState/windows-supervisor-v1.json':'native-state/supervisor-v1.json');
    if(!fs.existsSync(file))throw Error('Native owner journal missing; update blocked.');
    const state=JSON.parse(fs.readFileSync(file,'utf8'));
    if(!Array.isArray(state.records))throw Error('Invalid native job journal.');
    const lock=path.join(path.dirname(file),process.platform==='win32'
        ?'windows-supervisor.lock':'supervisor.lock');
    if(fs.existsSync(lock))throw Error('Native ownership journal locked; update blocked.');
    return state.records.some(record=>!TERMINAL.has(record.state));
}
function validRelease(entry,name) {
    if(!entry||entry.component!==name||!SHA.test(entry.version||'')||
        !HASH.test(entry.sha256||'')||!Number.isSafeInteger(entry.bytes)||
        entry.bytes<1||entry.bytes>MAX)
        throw Error('Invalid approved release version for '+name);
}
function escapeTar(destination,archive) {
    const tar=process.platform==='win32'?'tar.exe':'tar';
    const names=exec(tar,['-tf',archive],{maxBuffer:4*1024*1024}).split(/\r?\n/);
    if(names.length>100000||names.some(name=>!name||name.startsWith('/')||
        name.startsWith('\\')||name.includes('\\')||
        name.split('/').includes('..')||name.includes(':')||
        name.includes('\x00')))
        throw Error('Release archive includes an invalid path.');
    fs.mkdirSync(destination,{recursive:true,mode:0o700});
    exec(tar,['-xf',archive,'-C',destination]);
}
function initialVersion(root,name,env) {
    const locations={
        worker:path.join(root,process.platform==='win32'?'Sources':'sources','Bees-Assets'),
        tinybrains:path.join(root,process.platform==='win32'?'Sources':'sources','TinyBrains'),
        courier:path.join(root,process.platform==='win32'?'Sources':'sources','Basic-Training'),
    };
    const folder=locations[name];
    if(!folder||!fs.existsSync(path.join(folder,'.git')))return null;
    try {
        const sha=exec('git',['-C',folder,'rev-parse','HEAD']);
        return SHA.test(sha)?sha:null;
    }catch{return null;}
}
async function stage(base,env,root,name,entry) {
    const directory=path.join(root,'Software','Releases',name,entry.version);
    const verified=path.join(directory,'.verified-'+entry.sha256);
    if(fs.existsSync(verified))return directory;
    const parent=path.dirname(directory);
    fs.mkdirSync(parent,{recursive:true,mode:0o700});
    const archive=path.join(parent,entry.version+'.tar');
    if(!fs.existsSync(archive))await download(base,env,entry,archive);
    else {
        const info=await (async()=>{
            let bytes=0;const hash=crypto.createHash('sha256');
            for await(const block of fs.createReadStream(archive)){bytes+=block.length;hash.update(block);}
            return {bytes,digest:hash.digest('hex')};
        })();
        if(info.bytes!==entry.bytes||info.digest!==entry.sha256) {
            fs.unlinkSync(archive);await download(base,env,entry,archive);
        }
    }
    const temporary=directory+'.'+process.pid+'.staging';
    try{
        escapeTar(temporary,archive);
        // The worker entrypoint must exist before the old service is touched.
        if(name==='worker'&&
            !fs.existsSync(path.join(temporary,'Training','experiment_hub_agent.js')))
            throw Error('Worker release is missing its native entrypoint.');
        if(name==='courier'&&
            !fs.existsSync(path.join(temporary,'requirements.txt')))
            throw Error('Courier release has no dependency contract.');
        if(name==='tinybrains'&&
            !fs.existsSync(path.join(temporary,'Ants-MAPOCA','pyproject.toml')))
            throw Error('TinyBrains release has no simulation package.');
        fs.writeFileSync(path.join(temporary,'.verified-'+entry.sha256),'\n',{mode:0o600});
        fs.renameSync(temporary,directory);
    }finally {if(fs.existsSync(temporary))fs.rmSync(temporary,{recursive:true,force:true});}
    return directory;
}
function updateEnv(file,entries) {
    const original=fs.readFileSync(file,'utf8');
    const values=new Map(Object.entries(entries));
    const lines=original.split(/\r?\n/).filter(Boolean).map(line=>{
        const i=line.indexOf('='),key=line.slice(0,i);
        if(!values.has(key))return line;
        const value=values.get(key);values.delete(key);
        return key+'='+value;
    });
    for(const [key,val] of values)lines.push(key+'='+val);
    save(file,lines.join('\n')+'\n');
    return original;
}
function runningService() {
    if(process.platform==='win32') {
        const state=exec('sc.exe',['query',SERVICE]);
        return /STATE\s*:\s*4\s+RUNNING/.test(state);
    }
    try{return exec('systemctl',['--user','is-active',SERVICE])==='active';}
    catch{return false;}
}
function stopService() {
    if(process.platform==='win32')exec('sc.exe',['stop',SERVICE]);
    else exec('systemctl',['--user','stop',SERVICE]);
}
function startService() {
    if(process.platform==='win32')exec('sc.exe',['start',SERVICE]);
    else exec('systemctl',['--user','start',SERVICE]);
}
function configureService(root,training) {
    if(process.platform==='win32') {
        const json=path.join(root,'Private','agent-service.json');
        const old=fs.readFileSync(json,'utf8'),parsed=JSON.parse(old);
        parsed.TrainingDirectory=training;
        save(json,JSON.stringify(parsed)+'\n');
        return ()=>save(json,old);
    }
    const dir=path.join(require('node:os').homedir(),'.config','systemd','user',
        SERVICE+'.d');
    fs.mkdirSync(dir,{recursive:true,mode:0o700});
    const file=path.join(dir,'software-release.conf');
    const old=fs.existsSync(file)?fs.readFileSync(file,'utf8'):null;
    const node=process.execPath;
    if(/[\s%]/.test(training)||/[\s%]/.test(node))
        throw Error('Systemd release paths may not contain spaces or specifiers.');
    save(file,'[Service]\nWorkingDirectory='+training+
        '\nExecStart=\nExecStart='+node+' '+
        path.join(training,'experiment_hub_agent.js')+'\n');
    exec('systemctl',['--user','daemon-reload']);
    return ()=>{
        if(old===null)fs.unlinkSync(file);else save(file,old);
        exec('systemctl',['--user','daemon-reload']);
    };
}
async function apply(root,envFile,previous,changes) {
    const hasWorker=changes.worker;
    const entries={};
    if(hasWorker)entries.EXPERIMENT_HUB_LOCAL_ADAPTERS=path.join(
        changes.worker,'Training','experiment_hub_worker_adapters.js');
    if(changes.tinybrains)
        entries.EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT=
            path.join(changes.tinybrains,'Ants-MAPOCA');
    if(changes.courier)
        entries.EXPERIMENT_HUB_COURIER_PROJECT_ROOT=changes.courier;
    if(changes.bees)entries.EXPERIMENT_HUB_BEES_PROJECT_ROOT=changes.bees;
    let rollbackService=null,original=null,wasRunning=false;
    try {
        wasRunning=runningService();
        if(wasRunning)stopService();
        if(hasWorker)rollbackService=configureService(root,
            path.join(changes.worker,'Training'));
        original=updateEnv(envFile,entries);
        // Existing venvs are immutable during active native jobs. Rebind
        // editable simulation modules only after the native journal is idle.
        const venvs=path.join(root,process.platform==='win32'?'Venvs':'venvs');
        if(changes.courier||changes.tinybrains) {
            const uv=process.platform==='win32'?'py.exe':'uv';
            const prefix=process.platform==='win32'?['-3.11','-m','uv']:[];
            const env={...process.env};
            if(process.platform==='win32')
                env.PYTHONPATH=path.join(root,'Tooling','uv');
            if(changes.courier) {
                const python=path.join(venvs,'courier',process.platform==='win32'
                    ?'Scripts/python.exe':'bin/python');
                exec(uv,[...prefix,'pip','install','--python',python,
                    '-r',path.join(changes.courier,'requirements.txt')],{env});
                exec(python,['-B','-c','import courier_remote_lineage'],{
                    env:{...env,PYTHONPATH:changes.courier}});
            }
            if(changes.tinybrains) {
                const python=path.join(venvs,'tinybrains',process.platform==='win32'
                    ?'Scripts/python.exe':'bin/python');
                const project=path.join(changes.tinybrains,'Ants-MAPOCA');
                exec(uv,[...prefix,'pip','install','--python',python,'--no-deps','-e',project],{env});
                exec(python,['-B','-c','import ants_mapoca.remote_rollout'],{
                    env:{...env,PYTHONPATH:project}});
            }
        }
        if(wasRunning)startService();
        if(wasRunning&&!runningService())throw Error('Updated agent did not restart.');
    }catch(error) {
        try {
            if(original!==null)save(envFile,original);
            if(rollbackService)rollbackService();
            if(wasRunning&&!runningService())startService();
        }catch(rollbackError) {
            throw Error('Activation failed: '+error.message+
                '; rollback requires inspection: '+rollbackError.message);
        }
        throw error;
    }
}
async function main(argv=process.argv.slice(2)) {
    if(argv.length!==2||argv[0]!=='--root'||!path.isAbsolute(argv[1]))
        throw Error('Usage: node experiment_hub_worker_updater.js --root /installed/worker');
    const root=path.resolve(argv[1]);
    const privateDir=path.join(root,process.platform==='win32'?'Private':'private');
    const envFile=path.join(privateDir,'worker.env'),env=config(envFile);
    const base=sourcePath(env,root);
    const stateDir=path.join(root,'Software');
    fs.mkdirSync(stateDir,{recursive:true,mode:0o700});
    const lock=path.join(stateDir,'updater.lock');
    let fd;
    try{fd=fs.openSync(lock,'wx',0o600);}
    catch{console.log('[worker-updater] Another update process owns the lock.');return;}
    try {
        const activeFile=path.join(stateDir,'active.json');
        const active=fs.existsSync(activeFile)
            ?JSON.parse(fs.readFileSync(activeFile,'utf8')):{};
        const manifest=await request(base,env,'GET','');
        if(!manifest||!manifest.components||
            !['defer','override'].includes(manifest.mode))
            throw Error('Invalid authenticated Hub software manifest.');
        const installed=new Set((env.EXPERIMENT_HUB_WORKER_PROJECTS||'').split(','));
        const plan={};let dirty=false;
        for(const [name,entry] of Object.entries(manifest.components)) {
            if(!['worker','tinybrains','courier','bees'].includes(name)||
                (name!=='worker'&&!installed.has(name)))continue;
            validRelease(entry,name);
            const baseline=active[name]?.version||initialVersion(root,name,env);
            if(baseline===entry.version)continue;
            const directory=await stage(base,env,root,name,entry);
            plan[name]={directory,entry};
            dirty=true;
            await status(base,env,entry,'staged');
        }
        if(!dirty)return;
        let busy;
        try{busy=journalActive(root);}catch(error) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'waiting',error.message);
            return;
        }
        // Current Windows Job Objects can terminate native children when
        // their service owner restarts. Never risk a live Windows job.
        // On Linux, an override can refresh agent code while separately
        // systemd-owned workloads continue on their pinned existing code.
        const liveOverride=busy&&manifest.mode==='override'&&
            process.platform==='linux'&&Object.keys(plan).every(name=>name==='worker');
        if(busy&&!liveOverride) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'waiting',
                    manifest.mode==='override'
                        ?'Override awaits a native checkpoint/compatible restart; live process replacement is unsafe.'
                        :'Active native experiment retains its pinned runtime.');
            return;
        }
        for(const item of Object.values(plan))
            await status(base,env,item.entry,'activating');
        const changes=Object.fromEntries(Object.entries(plan)
            .map(([key,value])=>[key,value.directory]));
        try {
            await apply(root,envFile,active,changes);
            for(const [name,item] of Object.entries(plan)) {
                active[name]={version:item.entry.version,
                    sha256:item.entry.sha256,activated_at:new Date().toISOString()};
            }
            save(activeFile,JSON.stringify(active,null,2)+'\n');
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'active');
            // Self-updater is copied only from an authenticated verified worker
            // release after the running invocation has completed its work.
            if(changes.worker) {
                const replacement=path.join(changes.worker,'Training',
                    'experiment_hub_worker_updater.js');
                if(fs.existsSync(replacement))
                    save(path.join(root,'Update','experiment_hub_worker_updater.js'),
                        fs.readFileSync(replacement));
            }
        }catch(error) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'failed',error.message);
            throw error;
        }
    }finally{fs.closeSync(fd);fs.unlinkSync(lock);}
}
if(require.main===module)main().catch(error=>{
    console.error('[worker-updater] '+String(error.message||error));process.exitCode=1;
});
module.exports={main,config,journalActive,validRelease};
