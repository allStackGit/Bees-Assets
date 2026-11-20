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
const net=require('node:net');
const isTailnetIPv4=value=>{
    if(net.isIP(value)!==4)return false;
    const octets=value.split('.').map(Number);
    return octets[0]===100&&octets[1]>=64&&octets[1]<=127;
};
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
    if(!fs.existsSync(file))return false; // No native job has ever been dispatched.
    const state=JSON.parse(fs.readFileSync(file,'utf8'));
    if(!Array.isArray(state.records))throw Error('Invalid native job journal.');
    const lock=path.join(path.dirname(file),process.platform==='win32'
        ?'windows-supervisor.lock':'supervisor.lock');
    if(fs.existsSync(lock))throw Error('Native ownership journal locked; update blocked.');
    return state.records.some(record=>!TERMINAL.has(record.state));
}
function validRelease(entry,name) {
    if(!entry||entry.component!==name||!SHA.test(entry.version||'')||
        typeof entry.activation_ready!=='boolean'||
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
        if(name==='tinybrains') {
            // Git archive deliberately omits the separately pinned official
            // engine, CLI, and upstream baselines installed during enrollment.
            // Reuse that approved local dependency without copying private
            // experiments, logs, training history, or Git metadata.
            const source=env.EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT;
            if(!source||!fs.existsSync(path.join(source,'upstream','ants','baselines')))
                throw Error('Approved original TinyBrains upstream baseline is absent.');
            const project=path.join(temporary,'Ants-MAPOCA');
            for(const relative of ['tools','upstream']) {
                const from=path.join(source,relative),to=path.join(project,relative);
                if(!fs.existsSync(from))throw Error('Pinned TinyBrains '+relative+' is missing.');
                fs.cpSync(from,to,{recursive:true,force:false,
                    filter:file=>!file.split(path.sep).some(part=>
                        ['.git','results','.results','Demonstrations','Logs',
                            'TrainingHistory~'].includes(part))});
            }
        }
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
function shouldRunService() {
    if(process.platform==='win32')
        return /START_TYPE\s*:\s*2\s+AUTO_START/.test(
            exec('sc.exe',['qc',SERVICE]));
    try{return exec('systemctl',['--user','is-enabled',SERVICE])==='enabled';}
    catch{return false;}
}
function awaitWindowsState(wanted) {
    // SCM stop/start requests are asynchronous, unlike systemctl.
    for(let count=0;count<60;count++) {
        const result=exec('sc.exe',['query',SERVICE]);
        if(result.includes(wanted))return;
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)),0,0,1000);
    }
    throw Error('Windows agent service failed to reach '+wanted);
}
function stopService() {
    if(process.platform==='win32') {
        exec('sc.exe',['stop',SERVICE]);
        awaitWindowsState('STOPPED');
    }else exec('systemctl',['--user','stop',SERVICE]);
}
function startService() {
    if(process.platform==='win32') {
        exec('sc.exe',['start',SERVICE]);
        awaitWindowsState('RUNNING');
    }else exec('systemctl',['--user','start',SERVICE]);
}
function configureService(root,training,serviceBinary=null) {
    if(process.platform==='win32') {
        const json=path.join(root,'Private','agent-service.json');
        const old=fs.readFileSync(json,'utf8'),parsed=JSON.parse(old);
        const previous=exec('sc.exe',['qc',SERVICE]);
        const match=/BINARY_PATH_NAME\s*:\s*(.+)/.exec(previous);
        if(!match)throw Error('Cannot inspect existing Windows agent service binary.');
        const originalCommand=match[1].trim();
        parsed.TrainingDirectory=training;
        save(json,JSON.stringify(parsed)+'\n');
        try {
            if(serviceBinary) {
                const command='"'+serviceBinary+'" --service "'+json+'"';
                exec('sc.exe',['config',SERVICE,'binPath=',command]);
            }
        }catch(error){save(json,old);throw error;}
        return ()=>{
            save(json,old);
            if(serviceBinary)
                exec('sc.exe',['config',SERVICE,'binPath=',originalCommand]);
        };
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
function preparePythonRuntimes(root,changes) {
    const env={...process.env};
    const windows=process.platform==='win32';
    let command=windows?'py.exe':'uv';
    let prefix=windows?['-3.11','-m','uv']:[];
    let pythonVersion='3.11.18';
    if(windows) {
        env.PYTHONPATH=path.join(root,'Tooling','uv');
        const managed=path.join(root,'Tooling','Python');
        const matches=fs.readdirSync(managed,{withFileTypes:true})
            .filter(item=>item.isDirectory()&&item.name.startsWith('cpython-3.11'))
            .map(item=>path.join(managed,item.name,'python.exe'))
            .filter(file=>fs.existsSync(file));
        if(matches.length!==1)throw Error('Pinned machine-local Python is missing.');
        pythonVersion=matches[0];
    }
    const pythonPaths={};
    for(const name of ['tinybrains','courier']) {
        if(!changes[name])continue;
        const version=path.basename(changes[name]);
        const venv=path.join(root,'Software','Venvs',name,version);
        const python=path.join(venv,windows?'Scripts/python.exe':'bin/python');
        if(!fs.existsSync(python)) {
            fs.mkdirSync(path.dirname(venv),{recursive:true,mode:0o700});
            exec(command,[...prefix,'venv','--python',pythonVersion,venv],{env});
        }
        if(name==='courier') {
            exec(command,[...prefix,'pip','install','--python',python,
                '-r',path.join(changes.courier,'requirements.txt')],{env});
            exec(python,['-B','-c','import courier_remote_lineage'],{
                cwd:changes.courier,env:{...env,PYTHONPATH:changes.courier}});
        }else {
            const project=path.join(changes.tinybrains,'Ants-MAPOCA');
            exec(command,[...prefix,'pip','install','--python',python,
                '--index-url','https://download.pytorch.org/whl/cpu','torch==2.6.0'],{env});
            const requirements=path.join(project,'requirements-lock.txt');
            if(fs.existsSync(requirements))
                exec(command,[...prefix,'pip','install','--python',python,
                    '-r',requirements],{env});
            exec(command,[...prefix,'pip','install','--python',python,
                '--no-deps','-e',path.join(project,'upstream','ants','baselines'),
                '-e',project],{env});
            exec(python,['-B','-c',
                'import ants_mapoca.remote_rollout; from ants_mapoca.env import Engine'],{
                    cwd:project,env:{...env,PYTHONPATH:project}});
        }
        pythonPaths[name]=python;
    }
    return pythonPaths;
}
async function confirmAgentReplacement(base,env,priorInstance) {
    let diagnostic='No authenticated replacement agent has reported yet.';
    for(let attempt=0;attempt<24;attempt++) {
        try {
            const observed=await request(base,env,'GET','');
            if(observed.agent_instance_id&&
                observed.agent_instance_id!==priorInstance&&
                observed.agent_preflight_passed===true&&
                Number.isFinite(observed.agent_report_age_ms)&&
                observed.agent_report_age_ms>=0&&observed.agent_report_age_ms<45000)
                return;
            diagnostic=observed.agent_instance_id===priorInstance
                ?'Previous agent instance is still reporting.'
                :'Replacement native agent has not passed authenticated preflight.';
        }catch(error){diagnostic=error.message;}
        await new Promise(resolve=>setTimeout(resolve,5000));
    }
    throw Error('Updated agent did not reconnect successfully: '+diagnostic);
}
async function apply(root,envFile,previous,changes,confirmReplacement=null) {
    const hasWorker=changes.worker;
    const entries={};
    let nativeServiceBinary=null;
    if(hasWorker&&process.platform==='win32') {
        const version=path.basename(changes.worker);
        const binaries=path.join(root,'Software','Binaries',version);
        const ownerDir=path.join(binaries,'Owner');
        const serviceDir=path.join(binaries,'Service');
        fs.mkdirSync(ownerDir,{recursive:true,mode:0o700});
        fs.mkdirSync(serviceDir,{recursive:true,mode:0o700});
        const training=path.join(changes.worker,'Training');
        const owner=path.join(ownerDir,'ExperimentHubWindowsOwner.exe');
        const service=path.join(serviceDir,'ExperimentHubWindowsService.exe');
        if(!fs.existsSync(owner)||!fs.existsSync(service)) {
            exec('dotnet.exe',['publish',path.join(training,'experiment_hub_windows_owner',
                'ExperimentHubWindowsOwner.csproj'),'-c','Release','-r','win-x64',
                '--self-contained','true','-o',ownerDir]);
            exec('dotnet.exe',['publish',path.join(training,'experiment_hub_windows_service',
                'ExperimentHubWindowsService.csproj'),'-c','Release','-r','win-x64',
                '--self-contained','true','-o',serviceDir]);
        }
        nativeServiceBinary=service;
        entries.EXPERIMENT_HUB_WINDOWS_HELPER=owner;
        entries.EXPERIMENT_HUB_WINDOWS_HELPER_SHA256=
            crypto.createHash('sha256').update(fs.readFileSync(owner)).digest('hex');
    }
    if(hasWorker)entries.EXPERIMENT_HUB_LOCAL_ADAPTERS=path.join(
        changes.worker,'Training','experiment_hub_worker_adapters.js');
    if(changes.tinybrains)
        entries.EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT=
            path.join(changes.tinybrains,'Ants-MAPOCA');
    if(changes.courier)
        entries.EXPERIMENT_HUB_COURIER_PROJECT_ROOT=changes.courier;
    if(changes.bees)entries.EXPERIMENT_HUB_BEES_PROJECT_ROOT=changes.bees;
    // Prepare a separate dependency environment before any service is stopped.
    // Previously running jobs keep their old Python executable and imports.
    const pythonPaths=preparePythonRuntimes(root,changes);
    if(pythonPaths.courier)entries.EXPERIMENT_HUB_COURIER_PYTHON=pythonPaths.courier;
    if(pythonPaths.tinybrains)entries.EXPERIMENT_HUB_TINYBRAINS_PYTHON=pythonPaths.tinybrains;
    let rollbackService=null,original=null,wasRunning=false,shouldStart=false;
    try {
        wasRunning=runningService();
        shouldStart=wasRunning||shouldRunService();
        if(wasRunning)stopService();
        if(hasWorker)rollbackService=configureService(root,
            path.join(changes.worker,'Training'),nativeServiceBinary);
        original=updateEnv(envFile,entries);
        if(shouldStart)startService();
        if(shouldStart&&!runningService())throw Error('Updated agent did not restart.');
        if(shouldStart&&confirmReplacement)await confirmReplacement();
    }catch(error) {
        try {
            // A newly launched but unqualified agent is not a successful
            // installation. Stop it before restoring the old service path.
            if(runningService())stopService();
            if(original!==null)save(envFile,original);
            if(rollbackService)rollbackService();
            if(shouldStart&&!runningService())startService();
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
    catch{
        // Crash residue is recoverable, but never break a live updater lock.
        try {
            const info=JSON.parse(fs.readFileSync(lock,'utf8'));
            const age=Date.now()-info.started_at;
            let alive=true;
            try{process.kill(info.pid,0);}catch(e){alive=e.code!=='ESRCH';}
            if(alive||!Number.isFinite(age)||age<8*3600000)
                throw Error('Updater lock still belongs to a possibly live process.');
            fs.unlinkSync(lock);
            fd=fs.openSync(lock,'wx',0o600);
        }catch(error){
            console.error('[worker-updater] Lock not recoverable: '+error.message);
            return;
        }
    }
    fs.writeFileSync(fd,JSON.stringify({pid:process.pid,started_at:Date.now()}));
    try {
        const activeFile=path.join(stateDir,'active.json');
        const active=fs.existsSync(activeFile)
            ?JSON.parse(fs.readFileSync(activeFile,'utf8')):{};
        const manifest=await request(base,env,'GET','');
        if(!manifest||!manifest.components||
            !Array.isArray(manifest.pending_native_jobs)||
            !['defer','override'].includes(manifest.mode))
            throw Error('Invalid authenticated Hub software manifest.');
        const installed=new Set((env.EXPERIMENT_HUB_WORKER_PROJECTS||'').split(','));
        const plan={};let dirty=false,baselineSaved=false;
        for(const [name,entry] of Object.entries(manifest.components)) {
            if(!['worker','tinybrains','courier','bees'].includes(name)||
                (name!=='worker'&&!installed.has(name)))continue;
            validRelease(entry,name);
            const baseline=active[name]?.version||initialVersion(root,name,env);
            if(baseline===entry.version) {
                if(active[name]&&active[name].sha256!==entry.sha256)
                    throw Error('Immutable software version changed its archive hash: '+name);
                if(!active[name]) {
                    active[name]={version:entry.version,sha256:entry.sha256,
                        activated_at:new Date().toISOString()};
                    baselineSaved=true;
                    await status(base,env,entry,'active','Initial installed version recognized.');
                }
                continue;
            }
            const directory=await stage(base,env,root,name,entry);
            dirty=true;
            await status(base,env,entry,'staged');
            if(!entry.activation_ready) {
                await status(base,env,entry,'waiting',
                    'Coordinator has not yet activated matching learner/worker source.');
                continue;
            }
            plan[name]={directory,entry};
        }
        if(baselineSaved)save(activeFile,JSON.stringify(active,null,2)+'\n');
        if(!dirty||!Object.keys(plan).length)return;
        let busy;
        try{busy=journalActive(root)||manifest.pending_native_jobs.length>0;}catch(error) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'waiting',error.message);
            return;
        }
        // Current Windows Job Objects can terminate native children when
        // their service owner restarts. Never risk a live Windows job.
        // On Linux, an override can refresh agent code while separately
        // systemd-owned workloads continue on their pinned existing code.
        const liveOverride=busy&&manifest.mode==='override'&&
            process.platform==='linux'&&Boolean(plan.worker);
        if(busy) {
            for(const [name,item] of Object.entries(plan)) {
                if(liveOverride&&name==='worker')continue;
                await status(base,env,item.entry,'waiting',
                    manifest.mode==='override'
                        ?'Live simulation restart needs compatible checkpoint recovery; no unsafe replacement performed.'
                        :'Active native experiment retains its pinned runtime.');
                delete plan[name];
            }
            if(!Object.keys(plan).length)return;
        }
        for(const item of Object.values(plan))
            await status(base,env,item.entry,'activating');
        // A new dispatch may have been authorized after the original GET.
        // Recheck before changing native service paths.
        const fresh=await request(base,env,'GET','');
        if((fresh.pending_native_jobs||[]).length>0&&!liveOverride) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'waiting','A native job was dispatched during staging.');
            return;
        }
        if(!liveOverride&&journalActive(root)) {
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'waiting','A native owner became active during staging.');
            return;
        }
        const changes=Object.fromEntries(Object.entries(plan)
            .map(([key,value])=>[key,value.directory]));
        try {
            await apply(root,envFile,active,changes,()=>{
                return confirmAgentReplacement(base,env,manifest.agent_instance_id||null);
            });
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
                fs.mkdirSync(path.join(root,'Update'),{recursive:true,mode:0o700});
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
