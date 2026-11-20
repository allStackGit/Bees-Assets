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
        this.pending=null;
        fs.mkdirSync(this.root,{recursive:true,mode:0o700});
    }
    async publish(name,source) {
        const repo=source.path;
        await git(['fetch','--quiet','--no-tags','origin',source.branch],repo);
        const sha=await git(['rev-parse','FETCH_HEAD'],repo);
        if(!SHA.test(sha))throw Error('Invalid Git commit from '+name);
        const previous=this.latest[name];
        if(previous?.version===sha)return;
        const filename=path.join(this.root,name+'-'+sha+'.tar');
        if(!fs.existsSync(filename)) {
            const temp=filename+'.'+process.pid+'.tmp';
            try {
                // Git archives only tracked source at the resolved commit;
                // no local secrets, logs, untracked files, or Git credentials.
                await git(['archive','--format=tar','--output='+temp,sha,
                    ...(name==='worker'?['Training']:[])],repo,240000);
                const info=await fileHash(temp);
                if(info.bytes===0)throw Error('Empty release archive.');
                fs.renameSync(temp,filename);
                fs.chmodSync(filename,0o600);
            }finally {try{fs.unlinkSync(temp);}catch{}}
        }
        const info=await fileHash(filename);
        const record={component:name,version:sha,sha256:info.sha256,bytes:info.bytes};
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
            if(name==='worker'||projects.has(name))components[name]=entry;
        return components;
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
