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
    if(!folder)return null;
    // Initial one-file bootstrap installs verified immutable release archives
    // without a private Git clone. Recognize their signed-off version marker
    // instead of downloading and duplicating the same software on the first
    // update timer tick.
    const marker=path.join(folder,'.hub-release-version');
    if(fs.existsSync(marker)) {
        const version=fs.readFileSync(marker,'utf8').trim();
        if(!SHA.test(version))throw Error('Invalid initial software version marker: '+name);
        return version;
    }
    if(!fs.existsSync(path.join(folder,'.git')))return null;
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
            // The coordinator packages both exact upstream source commits and
            // the SHA-256-checked official 0.5.0 CLI for both platforms. No
            // worker-side Git clone, GitHub account or simulator install step.
            const project=path.join(temporary,'Ants-MAPOCA');
            if(!fs.existsSync(path.join(project,'upstream','ants','baselines'))||
                !fs.existsSync(path.join(project,'upstream','ants-starter'))||
                !fs.existsSync(path.join(project,'downloads','SHA256SUMS')))
                throw Error('Published TinyBrains bundle has no pinned upstream engines.');
            const archiveName=process.platform==='win32'
                ?'tinybrains-x86_64-pc-windows-msvc.zip'
                :'tinybrains-x86_64-unknown-linux-gnu.tar.gz';
            const archiveFile=path.join(project,'downloads',archiveName);
            const sums=fs.readFileSync(path.join(project,'downloads','SHA256SUMS'),'utf8');
            const lines=sums.split(/\r?\n/).filter(line=>line.trim().endsWith(' '+archiveName));
            if(lines.length!==1||!HASH.test(lines[0].trim().split(/\s+/)[0]))
                throw Error('Pinned official TinyBrains CLI checksum missing.');
            const wanted=lines[0].trim().split(/\s+/)[0];
            const actual=crypto.createHash('sha256')
                .update(fs.readFileSync(archiveFile)).digest('hex');
            if(actual!==wanted)throw Error('Pinned TinyBrains engine checksum mismatch.');
            const engineDir=path.join(project,'tools','tinybrains-0.5.0');
            fs.mkdirSync(engineDir,{recursive:true,mode:0o700});
            const tar=process.platform==='win32'?'tar.exe':'tar';
            exec(tar,[process.platform==='win32'?'-xf':'-xzf',
                archiveFile,'-C',engineDir]);
            const engine=path.join(engineDir,process.platform==='win32'
                ?'tinybrains.exe':'tinybrains');
            if(!fs.existsSync(engine))
                throw Error('Pinned TinyBrains CLI does not contain the approved executable.');
            // The trainer's runtime.configure uses this version's project
            // cache by default. Populate the exact CLI cache layout before
            // the release is marked verified, so simultaneous collectors
            // cannot race the first download or require outbound Internet.
            const downloads=path.join(project,'downloads');
            const pin=JSON.parse(fs.readFileSync(path.join(downloads,'ants-cartridge.json'),'utf8'));
            if(!HASH.test(pin.sha256||'')||!HASH.test(pin.engine_sha256||''))
                throw Error('Invalid pinned TinyBrains cartridge metadata.');
            const cartridgeArchive=path.join(downloads,'ants-artifacts.tar.gz');
            if(crypto.createHash('sha256').update(fs.readFileSync(cartridgeArchive)).digest('hex')!==pin.sha256)
                throw Error('Pinned TinyBrains cartridge archive checksum mismatch.');
            const cartridge=path.join(project,'cache','cartridges',pin.sha256);
            escapeTar(cartridge,cartridgeArchive);
            if(!fs.statSync(path.join(cartridge,'cartridge.json')).isFile()||
                crypto.createHash('sha256').update(fs.readFileSync(path.join(cartridge,'tb-ants.wasm'))).digest('hex')!==pin.engine_sha256)
                throw Error('Pinned TinyBrains cartridge engine checksum mismatch.');
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
// uv is a self-contained executable: project-specific Python provisioning
// must not depend on the Windows py launcher or a machine-wide interpreter.
const UV_WINDOWS_VERSION='0.13.6';
const UV_WINDOWS_SHA256='a2a8820d5eea32f08ef467ef3b7f66883a9b076d5737a2fc2fbb5f458d1bb50f';
function verifyWindowsUvVersion(executable) {
    // Official uv may append its build identifier and build date. Verify the
    // pinned semantic version rather than requiring the whole banner to equal
    // "uv X.Y.Z". The downloaded ZIP is separately SHA-256 verified.
    const reported=exec(executable,['--version']);
    const match=/^uv ([0-9]+\.[0-9]+\.[0-9]+)(?: \([^\r\n]{1,128}\))?$/.exec(reported);
    if(!match||match[1]!==UV_WINDOWS_VERSION)
        throw Error('Pinned Windows uv runtime version mismatch: '+reported.slice(0,120));
}
function windowsUv(root) {
    const tools=path.join(root,'Tooling');
    const directory=path.join(tools,'uv-'+UV_WINDOWS_VERSION);
    const uv=path.join(directory,'uv.exe');
    if(fs.existsSync(uv)) {
        verifyWindowsUvVersion(uv);
        return uv;
    }
    fs.mkdirSync(tools,{recursive:true,mode:0o700});
    const archive=path.join(tools,'uv-'+UV_WINDOWS_VERSION+'.'+process.pid+'.zip');
    const temporary=directory+'.'+process.pid+'.staging';
    try {
        const url='https://github.com/astral-sh/uv/releases/download/'+
            UV_WINDOWS_VERSION+'/uv-x86_64-pc-windows-msvc.zip';
        exec('curl.exe',['--fail','--location','--silent','--show-error',
            '--max-time','200','--output',archive,url]);
        if(fs.statSync(archive).size>48*1024*1024||
            crypto.createHash('sha256').update(fs.readFileSync(archive))
                .digest('hex')!==UV_WINDOWS_SHA256)
            throw Error('Pinned Windows uv archive checksum mismatch.');
        const names=exec('tar.exe',['-tf',archive]).split(/\r?\n/).filter(Boolean);
        if(!names.includes('uv.exe')||names.length>12||
            names.some(name=>name.includes('/')||name.includes('\\')||
                name==='.'||name==='..'||name.includes(':')))
            throw Error('Pinned Windows uv archive contains unexpected paths.');
        fs.mkdirSync(temporary,{recursive:true,mode:0o700});
        exec('tar.exe',['-xf',archive,'-C',temporary]);
        verifyWindowsUvVersion(path.join(temporary,'uv.exe'));
        fs.renameSync(temporary,directory);
        return uv;
    }finally{
        try{fs.unlinkSync(archive);}catch{}
        if(fs.existsSync(temporary))
            fs.rmSync(temporary,{recursive:true,force:true});
    }
}
function linuxUv(root) {
    if(process.arch!=='x64')
        throw Error('Pinned Linux uv provisioning currently needs x86_64.');
    const target=path.join(root,'Tooling','uv-'+UV_WINDOWS_VERSION);
    const binary=path.join(target,'uv');
    if(fs.existsSync(binary)) {
        verifyWindowsUvVersion(binary);
        return binary;
    }
    fs.mkdirSync(path.dirname(target),{recursive:true,mode:0o700});
    const filename='uv-x86_64-unknown-linux-gnu.tar.gz';
    const base='https://github.com/astral-sh/uv/releases/download/'+
        UV_WINDOWS_VERSION+'/'+filename;
    const archive=path.join(path.dirname(target),'.uv-'+process.pid+'.tar.gz');
    const checksum=archive+'.sha256';
    const temporary=target+'.'+process.pid+'.stage';
    try {
        exec('curl',['--fail','--location','--silent','--show-error',
            '--max-time','180','--output',checksum,base+'.sha256']);
        const expected=fs.readFileSync(checksum,'utf8').trim().split(/\s+/)[0];
        if(!HASH.test(expected)||fs.statSync(checksum).size>4096)
            throw Error('The pinned official Linux uv checksum is invalid.');
        exec('curl',['--fail','--location','--silent','--show-error',
            '--max-time','180','--output',archive,base]);
        if(fs.statSync(archive).size>48*1024*1024||
            crypto.createHash('sha256').update(fs.readFileSync(archive))
                .digest('hex')!==expected)
            throw Error('Official Linux uv archive checksum mismatch.');
        const names=exec('tar',['-tzf',archive]).split(/\r?\n/).filter(Boolean);
        if(!names.includes('uv-x86_64-unknown-linux-gnu/uv')||
            names.length>24||names.some(name=>name.startsWith('/')||
                name.includes('\\')||name.includes(':')||
                name.split('/').includes('..'))||
            exec('tar',['-tvzf',archive]).split(/\r?\n/)
                .some(line=>/^[lh]/.test(line)))
            throw Error('Pinned official Linux uv archive has unexpected paths.');
        fs.mkdirSync(temporary,{recursive:true,mode:0o700});
        exec('tar',['-xzf',archive,'-C',temporary]);
        const unpacked=path.join(temporary,'uv-x86_64-unknown-linux-gnu','uv');
        verifyWindowsUvVersion(unpacked);
        fs.chmodSync(unpacked,0o700);
        // Publish the entire validated runtime atomically. A power loss
        // never leaves a half-written executable at the installed path.
        fs.renameSync(path.dirname(unpacked),target);
        return binary;
    }finally {
        for(const file of [archive,checksum]) {
            try{fs.unlinkSync(file);}catch{}
        }
        if(fs.existsSync(temporary))
            fs.rmSync(temporary,{recursive:true,force:true});
    }
}
function preparePythonRuntimes(root,changes) {
    // The permanent Hub worker has no Python requirement. Only simulation
    // releases create or update Python environments.
    if(!changes.tinybrains&&!changes.courier)return {};
    const env={...process.env};
    const windows=process.platform==='win32';
    // Both platforms use a project-local pinned uv executable only when
    // installing a simulation. No system-wide Python or uv prerequisite.
    let command=windows?null:linuxUv(root);
    const prefix=[];
    let pythonVersion='3.11.18';
    if(windows) {
        command=windowsUv(root);
        const managed=path.join(root,'Tooling','Python');
        fs.mkdirSync(managed,{recursive:true,mode:0o700});
        env.UV_PYTHON_INSTALL_DIR=managed;
        env.UV_CACHE_DIR=path.join(root,'Tooling','Cache');
        env.UV_NO_PROGRESS='1';
        env.UV_NO_CONFIG='1';
        fs.mkdirSync(env.UV_CACHE_DIR,{recursive:true,mode:0o700});
        // Use an immutable machine-local interpreter, not "py.exe -3.11".
        // The latter may be installed even with no Python runtime available.
        exec(command,['python','install','3.11.18',
            '--install-dir',managed,'--no-bin'],{env});
        const matches=fs.readdirSync(managed,{withFileTypes:true})
            .filter(item=>item.isDirectory()&&item.name.startsWith('cpython-3.11.18-'))
            .map(item=>path.join(managed,item.name,'python.exe'))
            .filter(file=>fs.existsSync(file));
        if(matches.length!==1)
            throw Error('Pinned machine-local Python 3.11.18 installation failed.');
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
            // Exercise the same runtime and engine handshake that a remote
            // collector needs, before replacing the active service. The
            // pinned cartridge is already local and needs no runtime fetch.
            exec(python,['-B','-c',[
                'from ants_mapoca.runtime import configure, ENGINE',
                'from ants_mapoca.env import Engine',
                'import ants_mapoca.remote_rollout',
                'configure(threads=1)',
                'engine = Engine(waves=1, matches_per_wave=1, max_turns=2, maps=["basic-tiny-2p"])',
                'try:',
                '    assert engine.engine_digest == ENGINE, "Unexpected Ants engine digest"',
                '    engine.reset()',
                'finally:',
                '    engine.close()',
            ].join('\n')],{
                cwd:project,env:{...env,PYTHONPATH:project,
                    TINYBRAINS_HOME:path.join(project,'cache'),
                    TINYBRAINS_REGISTRY:path.join(project,'games.toml'),
                    TINYBRAINS:path.join(project,'tools','tinybrains-0.5.0',
                        windows?'tinybrains.exe':'tinybrains')},timeout:120000});
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
                (observed.agent_preflight_passed===true||
                    // A brand-new base-only worker has no simulation to
                    // qualify yet; keep its authenticated recovery agent.
                    Array.isArray(observed.agent_installed_projects)&&
                    observed.agent_installed_projects.length===0)&&
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
async function apply(root,envFile,previous,changes,confirmReplacement=null,rolloutKeys={}) {
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
    // Install only the coordinator-requested simulations, retaining all
    // previously installed components. Private keys are never stored in
    // published source archives or replaced silently.
    const localEnv=config(envFile);
    const selected=new Set((localEnv.EXPERIMENT_HUB_WORKER_PROJECTS||'')
        .split(',').filter(Boolean));
    const privateDir=path.join(root,process.platform==='win32'?'Private':'private');
    for(const name of ['tinybrains','courier']) {
        if(!changes[name])continue;
        const key=rolloutKeys[name];
        if(!HASH.test(key||''))throw Error('Missing machine-authorized '+name+' rollout key.');
        const target=path.join(privateDir,name+'-rollout.key');
        if(fs.existsSync(target)) {
            if(fs.readFileSync(target,'utf8').trim()!==key)
                throw Error('Existing '+name+' rollout key differs from coordinator; refusing rotation.');
        }else save(target,key+'\n');
        selected.add(name);
        entries['EXPERIMENT_HUB_'+name.toUpperCase()+'_ROLLOUT_KEY']=target;
        const cache=path.join(root,process.platform==='win32'?'Cache':'cache',name);
        fs.mkdirSync(cache,{recursive:true,mode:0o700});
        entries['EXPERIMENT_HUB_'+name.toUpperCase()+'_CACHE']=cache;
        if(name==='tinybrains')
            entries.EXPERIMENT_HUB_TINYBRAINS_THREAD_LIMIT=
                String(require('node:os').availableParallelism());
    }
    if(changes.tinybrains||changes.courier)
        entries.EXPERIMENT_HUB_WORKER_PROJECTS=[...selected].sort().join(',');
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
    const component=argv.length===4&&argv[2]==='--component'
        ?argv[3]:argv.length===2?'all':null;
    if(argv[0]!=='--root'||!path.isAbsolute(argv[1]||'')||
        !['all','worker','tinybrains','courier','bees'].includes(component))
        throw Error('Usage: node experiment_hub_worker_updater.js --root ABSOLUTE_PATH [--component worker|tinybrains|courier|bees]');
    // The persistent update launcher invokes worker infrastructure first,
    // then each requested simulation separately. A failed simulation update
    // cannot block worker update or another simulation's installation.
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
        // The authenticated coordinator manifest is an allowlist of
        // machine-requested components, not a frozen enrollment selection.
        const plan={};let dirty=false,baselineSaved=false;
        for(const [name,entry] of Object.entries(manifest.components)) {
            if(!['worker','tinybrains','courier','bees'].includes(name)||
                component!=='all'&&component!==name)continue;
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
            const rolloutKeys={};
            for(const name of ['tinybrains','courier']) {
                if(!changes[name])continue;
                const credential=await request(base,env,'GET','/rollout-key',{component:name});
                if(credential?.component!==name||!HASH.test(credential.key||''))
                    throw Error('Coordinator refused '+name+' rollout credential.');
                rolloutKeys[name]=credential.key;
            }
            await apply(root,envFile,active,changes,()=>{
                return confirmAgentReplacement(base,env,manifest.agent_instance_id||null);
            },rolloutKeys);
            for(const [name,item] of Object.entries(plan)) {
                active[name]={version:item.entry.version,
                    sha256:item.entry.sha256,activated_at:new Date().toISOString()};
            }
            save(activeFile,JSON.stringify(active,null,2)+'\n');
            for(const item of Object.values(plan))
                await status(base,env,item.entry,'active');
            // The stable scheduler bootstrap retains verified, versioned
            // updater engines and their last-good pointer. Never overwrite the
            // fallback updater in-place from a replaceable updater process.
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
