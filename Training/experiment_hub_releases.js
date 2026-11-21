'use strict';

// Coordinator-owned release publisher. Only this host needs private GitHub
// credentials; enrolled workers fetch authenticated, hashed immutable archives.
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const {pipeline}=require('node:stream/promises');
const COMPONENTS=['worker','bees','tinybrains','courier'];
const SHA=/^[0-9a-f]{40}$/;
const DIGEST=/^[0-9a-f]{64}$/;
const MAX_BYTES=512*1024*1024;
const BRANCH=/^[a-zA-Z0-9._/-]{1,128}$/;
const TINYBRAINS_UPSTREAM=Object.freeze({
    'ants-starter':'a9610c693c0559286e4f5a96efec511b024bd573',
    ants:'54edfab12469b432041658ec80bcdf5931ae23e4',
});
const TINYBRAINS_CLI='v0.5.0';
const CLI_ASSETS=[
    'tinybrains-x86_64-unknown-linux-gnu.tar.gz',
    'tinybrains-x86_64-pc-windows-msvc.zip',
];
// Runtime provisioning changed too: force an immutable new release/venv
// even for workers that already installed this exact TinyBrains source.
const TINYBRAINS_PACKAGE_FORMAT='pinned-upstream-cli-cartridge-windows-cuda-v3';

function git(args,cwd,timeout=120000) {
    return new Promise((resolve,reject)=>{
        const child=spawn('git',args,{cwd,stdio:['ignore','pipe','pipe'],windowsHide:true});
        let out='',err='',complete=false;
        const alarm=setTimeout(()=>child.kill(),timeout);
        child.stdout.on('data',part=>{out+=part; if(out.length>8192)child.kill();});
        child.stderr.on('data',part=>{err+=part; if(err.length>8192)child.kill();});
        child.once('error',fail);
        child.once('close',code=>code===0?finish(out.trim()):fail(Error(err.slice(0,300)||'Git exited '+code)));
        function finish(value){if(complete)return;complete=true;clearTimeout(alarm);resolve(value);}
        function fail(error){if(complete)return;complete=true;clearTimeout(alarm);reject(error);}
    });
}
function command(program,args,cwd,timeout=120000) {
    return new Promise((resolve,reject)=>{
        const child=spawn(program,args,{cwd,stdio:['ignore','pipe','pipe'],windowsHide:true});
        let stderr='',complete=false;
        const alarm=setTimeout(()=>child.kill(),timeout);
        child.stdout.resume();
        child.stderr.on('data',data=>{stderr+=data;if(stderr.length>4096)child.kill();});
        child.once('error',fail);
        child.once('close',code=>code===0?finish():fail(Error(
            program+' failed: '+stderr.slice(0,250)||'Exit '+code)));
        function finish(){if(complete)return;complete=true;clearTimeout(alarm);resolve();}
        function fail(error){if(complete)return;complete=true;clearTimeout(alarm);reject(error);}
    });
}
async function pinTinyBrainsUpstream(root,archive,registry) {
    const vendorRoot=path.join(root,'pinned-upstream');
    fs.mkdirSync(vendorRoot,{recursive:true,mode:0o700});
    for(const [name,revision] of Object.entries(TINYBRAINS_UPSTREAM)) {
        const checkout=path.join(vendorRoot,name);
        if(!fs.existsSync(path.join(checkout,'.git')))
            await git(['clone','--quiet','--no-checkout','--filter=blob:none',
                'https://github.com/Tiny-Brains/'+name+'.git',checkout],vendorRoot,120000);
        await git(['fetch','--quiet','--no-tags','--depth=1','origin',revision],checkout);
        const resolved=await git(['rev-parse','FETCH_HEAD'],checkout);
        if(resolved!==revision)throw Error('Pinned TinyBrains upstream revision changed: '+name);
        const temporary=archive+'.'+name+'.tar';
        try {
            await git(['archive','--format=tar',
                '--prefix=Ants-MAPOCA/upstream/'+name+'/',
                '--output='+temporary,revision],checkout,120000);
            await command('tar',['-Af',archive,temporary],root);
        }finally{try{fs.unlinkSync(temporary);}catch{}}
    }
    const downloads=path.join(root,'pinned-cli');
    fs.mkdirSync(downloads,{recursive:true,mode:0o700});
    const source='https://github.com/Tiny-Brains/cli/releases/download/'+TINYBRAINS_CLI+'/';
    const sums=path.join(downloads,'SHA256SUMS');
    await command('curl',['--fail','--location','--silent','--show-error',
        '--output',sums,source+'SHA256SUMS'],root);
    const entries=fs.readFileSync(sums,'utf8').split(/\r?\n/);
    for(const name of CLI_ASSETS) {
        const matches=entries.filter(line=>line.trim().endsWith(' '+name));
        if(matches.length!==1)throw Error('Missing TinyBrains checksum: '+name);
        const expected=matches[0].trim().split(/\s+/)[0];
        if(!DIGEST.test(expected))throw Error('Invalid TinyBrains checksum for '+name);
        const asset=path.join(downloads,name);
        if(!fs.existsSync(asset)||(await fileHash(asset)).sha256!==expected)
            await command('curl',['--fail','--location','--silent','--show-error',
                '--output',asset,source+name],root,240000);
        if((await fileHash(asset)).sha256!==expected)
            throw Error('TinyBrains CLI official release checksum mismatch: '+name);
    }
    await command('tar',['-rf',archive,'-C',downloads,
        '--transform=s|^|Ants-MAPOCA/downloads/|','SHA256SUMS',...CLI_ASSETS],root,120000);
    // Package the game itself as well as its CLI. The exact archived
    // registry is the source of truth, not a moving upstream release.
    const section=registry.split(/^\[games\.ants\]\s*$/m)[1]?.split(/^\[/m)[0]||'';
    const sourceRepo=section.match(/^repo\s*=\s*"Tiny-Brains\/ants"\s*$/m);
    const release=section.match(/^release\s*=\s*"(engine-[a-f0-9]{12})"\s*$/m)?.[1];
    const digest=section.match(/^artifacts\s*=\s*\{\s*file\s*=\s*"ants-artifacts\.tar\.gz"\s*,\s*sha256\s*=\s*"sha256:([a-f0-9]{64})"\s*\}\s*$/m)?.[1];
    const engine=section.match(/^engine\s*=\s*"sha256:([a-f0-9]{64})"\s*$/m)?.[1];
    if(!sourceRepo||!release||!digest||!engine)
        throw Error('TinyBrains registry does not identify a supported pinned Ants cartridge.');
    const cartridgeDir=path.join(root,'pinned-cartridges',digest);
    fs.mkdirSync(cartridgeDir,{recursive:true,mode:0o700});
    const assetName='ants-artifacts.tar.gz';
    const asset=path.join(cartridgeDir,assetName);
    if(!fs.existsSync(asset)||(await fileHash(asset)).sha256!==digest)
        await command('curl',['--fail','--location','--silent','--show-error',
            '--output',asset,'https://github.com/Tiny-Brains/ants/releases/download/'+release+'/'+assetName],root,240000);
    if((await fileHash(asset)).sha256!==digest)
        throw Error('Pinned TinyBrains Ants cartridge checksum mismatch.');
    fs.writeFileSync(path.join(cartridgeDir,'ants-cartridge.json'),
        JSON.stringify({release,sha256:digest,engine_sha256:engine})+'\n',{mode:0o600});
    await command('tar',['-rf',archive,'-C',cartridgeDir,
        '--transform=s|^|Ants-MAPOCA/downloads/|',assetName,'ants-cartridge.json'],root,120000);
}
async function fileHash(file) {
    const hash=crypto.createHash('sha256');let bytes=0;
    for await(const block of fs.createReadStream(file)) {
        bytes+=block.length;
        if(bytes>MAX_BYTES)throw Error('Software release exceeds 512 MiB.');
        hash.update(block);
    }
    return {sha256:hash.digest('hex'),bytes};
}
function configuration(input,workerRoot) {
    if(!input)return {worker:{path:workerRoot,branch:'feat/hub-resource-experiments-20261110'}};
    const parsed=JSON.parse(input);
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed)||
        Object.keys(parsed).some(key=>!COMPONENTS.includes(key)))
        throw Error('EXPERIMENT_HUB_RELEASE_SOURCES must map approved component names.');
    if(!parsed.worker)parsed.worker={path:workerRoot,branch:'feat/hub-resource-experiments-20261110'};
    for(const [key,value] of Object.entries(parsed)) {
        if(!value||typeof value.path!=='string'||!path.isAbsolute(value.path)||
            value.path.includes('\\0')||!fs.existsSync(path.join(value.path,'.git'))||
            !BRANCH.test(value.branch||'')||value.branch.includes('..'))
            throw Error('Invalid local Git release source for '+key);
    }
    return parsed;
}
class ReleaseStore {
    constructor(root,sources=configuration(process.env.EXPERIMENT_HUB_RELEASE_SOURCES,
        path.resolve(__dirname,'..'))) {
        this.root=path.join(root,'software-releases');
        this.sources=sources;
        this.latest={};
        this.files=new Map();
        this.failures={};
        this.localVersions={};
        this.bootVersions={};
        this.pending=null;
        fs.mkdirSync(this.root,{recursive:true,mode:0o700});
    }
    async publish(name,source) {
        const repo=source.path;
        const localVersion=await git(['rev-parse','HEAD'],repo);
        this.localVersions[name]=localVersion;
        if(!this.bootVersions[name])this.bootVersions[name]=localVersion;
        await git(['fetch','--quiet','--no-tags','origin',source.branch],repo);
        const sha=await git(['rev-parse','FETCH_HEAD'],repo);
        if(!SHA.test(sha))throw Error('Invalid Git commit from '+name);
        // A Git commit and its vendored dependencies together identify a
        // TinyBrains runtime release; never change bytes under the old SHA.
        const version=name==='tinybrains'
            ?crypto.createHash('sha1').update(JSON.stringify({
                source:sha,upstream:TINYBRAINS_UPSTREAM,cli:TINYBRAINS_CLI,
                format:TINYBRAINS_PACKAGE_FORMAT,
            })).digest('hex'):sha;
        const previous=this.latest[name];
        if(previous?.version===version)return;
        const filename=path.join(this.root,name+'-'+version+'.tar');
        if(!fs.existsSync(filename)) {
            const temp=filename+'.'+process.pid+'.tmp';
            try {
                // Git archives only tracked source at the resolved commit;
                // no local secrets, logs, untracked files, or Git credentials.
                // TinyBrains' tracked repository contains multi-gigabyte
                // learning runs, checkpoints and evidence. These are not
                // executable worker software and must never be shipped to
                // enrolled machines. Include only the runtime's source,
                // configuration, maps and tools; the pinned official engine
                // and upstream baseline are installed separately on workers.
                const archivePaths=name==='worker'?['Training']:
                    name==='tinybrains'?[
                        'Ants-MAPOCA/pyproject.toml',
                        'Ants-MAPOCA/requirements-lock.txt',
                        'Ants-MAPOCA/src',
                        'Ants-MAPOCA/configs',
                        'Ants-MAPOCA/maps',
                        'Ants-MAPOCA/scripts',
                        'Ants-MAPOCA/tools',
                        'Ants-MAPOCA/games.toml',
                    ]:[];
                await git(['archive','--format=tar','--output='+temp,sha,
                    ...archivePaths],repo,240000);
                if(name==='tinybrains') {
                    const registry=await git(['show',sha+':Ants-MAPOCA/games.toml'],repo);
                    await pinTinyBrainsUpstream(this.root,temp,registry);
                }
                const info=await fileHash(temp);
                if(info.bytes===0)throw Error('Empty release archive.');
                fs.renameSync(temp,filename);
                fs.chmodSync(filename,0o600);
            }finally {try{fs.unlinkSync(temp);}catch{}}
        }
        const info=await fileHash(filename);
        const record={component:name,version,source_commit:sha,
            sha256:info.sha256,bytes:info.bytes};
        this.files.set(name+':'+info.sha256,filename);
        this.latest[name]=record;
    }
    async refresh() {
        if(this.pending)return this.pending;
        const run=async()=>{
            for(const [name,source] of Object.entries(this.sources)) {
                try {await this.publish(name,source);delete this.failures[name];}
                catch(error) {
                    this.failures[name]=String(error.message||error).slice(0,250);
                    console.error('[hub-updates] '+name+': '+this.failures[name]);
                }
            }
        };
        this.pending=run().finally(()=>{this.pending=null;});
        return this.pending;
    }
    manifest(installedProjects) {
        const projects=new Set(installedProjects||[]);
        const components={};
        for(const [name,entry] of Object.entries(this.latest))
            if(name==='worker'||projects.has(name))components[name]={
                ...entry,
                // Remote activation waits until both the local checkout and
                // coordinator generation were refreshed after a safe restart.
                activation_ready:entry.source_commit===this.bootVersions[name],
            };
        return components;
    }
    async applyLocalWhenIdle(hub,allowWorkerOverride=false) {
        // Ordinary learner/simulation revisions require an idle native-job
        // boundary. Explicit operator override may refresh coordinator and
        // Linux agents while separately OS-owned native workloads continue.
        const active=hub.state.jobs.some(job=>job.dispatch_id&&
            !job.capacity_released_at&&
            !['finished','failed','stopped','cancelled'].includes(job.status));
        if(active&&!allowWorkerOverride)return {deferred:true,changed:[]};
        const changed=[];
        for(const [name,source] of Object.entries(this.sources)) {
            if(active&&name!=='worker')continue;
            try {
                const local=await git(['rev-parse','HEAD'],source.path);
                const target=this.latest[name]?.source_commit;
                if(!target||local===target)continue;
                const branch=await git(['branch','--show-current'],source.path);
                if(branch!==source.branch)
                    throw Error('Local checkout is not on the approved release branch.');
                const dirty=await git(['status','--porcelain','--untracked-files=no'],source.path);
                if(dirty)throw Error('Local checkout has modifications; release source remains staged.');
                await git(['merge','--ff-only',target],source.path);
                this.localVersions[name]=target;
                changed.push(name);
            }catch(error) {
                this.failures[name]='Local activation: '+String(error.message||error).slice(0,220);
            }
        }
        return {deferred:false,changed};
    }
    archive(component,sha) {
        if(!COMPONENTS.includes(component)||!DIGEST.test(sha))
            throw Error('Invalid release archive request.');
        const file=this.files.get(component+':'+sha);
        if(!file||!fs.existsSync(file))throw Error('Unknown release archive.');
        return {path:file,bytes:fs.statSync(file).size};
    }
}
module.exports={ReleaseStore,configuration,COMPONENTS};
