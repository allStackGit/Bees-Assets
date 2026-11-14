'use strict';

// Opt-in job-log synchronization independent of trainer/process control.
// Only the installed adapter chooses a job directory; Hub never sends a path.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const {safeLogPath,safeNativeCollectorPath,MAX_CHUNK,MAX_FILE}=
    require('./experiment_hub_artifact_sync');
const TERMINAL=new Set(['finished','failed','stopped']);
const MAX_CYCLE=32*1024*1024;
function filesForJob(folder,projectId) {
    const root=path.resolve(folder);
    const stat=fs.lstatSync(root);
    if(!stat.isDirectory()||stat.isSymbolicLink()||fs.realpathSync(root)!==root)
        throw Error('Trusted job artifact directory was redirected.');
    const names=[],queue=[{folder:root,prefix:'',depth:0}];
    let entries=0;
    while(queue.length) {
        const next=queue.shift();
        const found=fs.readdirSync(next.folder,{withFileTypes:true})
            .sort((a,b)=>a.name.localeCompare(b.name));
        entries+=found.length;
        if(entries>2000)throw Error('Job artifact scan exceeded its entry limit.');
        for(const item of found) {
            const relative=next.prefix+item.name;
            if(item.isSymbolicLink())continue;
            const allowed=name=>projectId==='bees_native_collect'
                ?safeNativeCollectorPath(name):safeLogPath(name);
            if(item.isDirectory()&&next.depth<7) {
                if(allowed(relative+'/ignored.log')&&
                    fs.realpathSync(path.join(next.folder,item.name))===
                        path.join(next.folder,item.name))
                    queue.push({folder:path.join(next.folder,item.name),
                        prefix:relative+'/',depth:next.depth+1});
            }else if(item.isFile()&&allowed(relative))names.push(relative);
            const limit=projectId==='bees_native_collect'?1024:256;
            if(names.length>limit)throw Error('Job has too many eligible artifact files.');
        }
    }
    return names.sort();
}
async function snapshotFile(root,relative) {
    const source=path.join(root,...relative.split('/'));
    const real=fs.realpathSync(source);
    if(real!==source||!real.startsWith(path.resolve(root)+path.sep))
        throw Error('Job log escaped its approved artifact directory.');
    const fd=fs.openSync(source,fs.constants.O_RDONLY|
        (fs.constants.O_NOFOLLOW||0));
    try {
        const before=fs.fstatSync(fd);
        if(!before.isFile()||before.nlink!==1||before.size<1||
            before.size>MAX_FILE)return null;
        const digest=crypto.createHash('sha256'),buffer=Buffer.allocUnsafe(MAX_CHUNK);
        let offset=0;
        while(offset<before.size) {
            const size=fs.readSync(fd,buffer,0,
                Math.min(MAX_CHUNK,before.size-offset),offset);
            if(size<1)throw Error('Job log shortened during hashing.');
            digest.update(buffer.subarray(0,size));offset+=size;
            // Native heartbeats and urgent Stop/Inspect instructions share
            // this Node process. Yield between bounded reads, particularly
            // for large diagnostics on slower or removable media.
            if(offset<before.size)
                await new Promise(resolve=>setImmediate(resolve));
        }
        const after=fs.fstatSync(fd);
        // Appended bytes beyond the captured size do not change the prefix.
        // A rewrite/shrink cannot be published as a coherent snapshot.
        if(after.dev!==before.dev||after.ino!==before.ino||
            after.size<before.size||
            after.size===before.size&&after.mtimeMs!==before.mtimeMs)
            return null;
        return {source,size:before.size,sha:digest.digest('hex'),
            dev:before.dev,ino:before.ino};
    }finally{fs.closeSync(fd);}
}
function remoteCall(endpoint,machineId,token,jobId,file,snapshot,offset,bytes) {
    const url=new URL(endpoint);
    url.pathname='/api/hub/agent/artifacts';
    url.search='';url.searchParams.set('machine_id',machineId);
    url.searchParams.set('job_id',jobId);url.searchParams.set('file',file);
    url.searchParams.set('sha',snapshot.sha);
    url.searchParams.set('size',String(snapshot.size));
    if(bytes)url.searchParams.set('offset',String(offset));
    return new Promise((resolve,reject)=>{
        const request=http.request(url,{
            method:bytes?'PUT':'GET',timeout:30000,
            headers:{Host:'127.0.0.1:7149',
                'X-Hub-Agent-Token':token,
                ...(bytes?{'Content-Type':'application/octet-stream',
                    'Content-Length':bytes.length}:{})},
        },response=>{
            const chunks=[];let total=0;
            response.on('data',part=>{
                total+=part.length;
                if(total>8192)request.destroy(Error('Artifact response too large.'));
                else chunks.push(part);
            });
            response.on('end',()=>{
                let reply;
                try{reply=JSON.parse(Buffer.concat(chunks).toString('utf8'));}
                catch{return reject(Error('Invalid artifact mirror response.'));}
                if(response.statusCode!==200)
                    return reject(Error('Artifact mirror HTTP '+response.statusCode+
                        ': '+String(reply?.error||'rejected').slice(0,120)));
                if(!Number.isSafeInteger(reply.offset)||reply.offset<0||
                    reply.offset>snapshot.size||typeof reply.complete!=='boolean')
                    return reject(Error('Invalid artifact mirror offset.'));
                resolve(reply);
            });
        });
        request.on('timeout',()=>request.destroy(Error('Artifact transfer timed out.')));
        request.on('error',reject);
        request.end(bytes);
    });
}
async function syncArtifacts(executor,endpoint,machineId,token,cache=new Map()) {
    if(!executor?.supervisor?.snapshot)return {bytes:0,files:0};
    let sent=0,completed=0;
    // Native ownership is local and durable. The coordinator cannot give
    // the agent a filename or a new file root through a heartbeat.
    for(const record of executor.supervisor.snapshot()) {
        if(!TERMINAL.has(record.state))continue;
        const project=record.project_id,adapter=executor.supervisor.adapters?.[project];
        // Collectors carry binary training experience rather than only
        // diagnostics. Raise their bounded per-cycle delivery allowance
        // without changing general telemetry's smaller background budget.
        const cycleBudget=project==='bees_native_collect'
            ?128*1024*1024:MAX_CYCLE;
        if(typeof adapter?.artifact_export_dir!=='function')continue;
        if(!executor.projects.includes(project))continue;
        const root=adapter.artifact_export_dir(record.job_id);
        if(typeof root!=='string'||!path.isAbsolute(root))continue;
        if(!fs.existsSync(root))continue;
        for(const relative of filesForJob(root,project)) {
            const file=path.join(root,...relative.split('/'));
            let st;
            try{st=fs.lstatSync(file);}catch{continue;}
            const key=record.job_id+':'+relative;
            const cached=cache.get(key);
            const unchanged=cached&&st.isFile()&&st.dev===cached.dev&&
                st.ino===cached.ino&&st.size===cached.size&&
                st.mtimeMs===cached.mtimeMs;
            if(unchanged&&cached.complete)continue;
            const proof=unchanged?cached:await snapshotFile(root,relative);
            if(!proof)continue;
            const snapshot={size:proof.size,sha:proof.sha};
            cache.set(key,{...proof,mtimeMs:st.mtimeMs,complete:false});
            if(cache.size>10000)cache.clear();
            let response=await remoteCall(endpoint,machineId,token,
                record.job_id,relative,snapshot,0,null);
            if(response.complete){
                cache.set(key,{...proof,mtimeMs:st.mtimeMs,complete:true});
                completed++;continue;
            }
            if(sent>=cycleBudget)return {bytes:sent,files:completed};
            // Each chunk is bounded, resumable by its exact destination
            // offset and tied to a complete source snapshot SHA-256.
            const fd=fs.openSync(file,fs.constants.O_RDONLY|
                (fs.constants.O_NOFOLLOW||0));
            try {
                const opened=fs.fstatSync(fd);
                if(!opened.isFile()||opened.nlink!==1||
                    opened.dev!==proof.dev||opened.ino!==proof.ino||
                    opened.size<proof.size)continue;
                while(response.offset<snapshot.size&&sent<cycleBudget) {
                    const length=Math.min(MAX_CHUNK,snapshot.size-response.offset,
                        cycleBudget-sent);
                    const block=Buffer.allocUnsafe(length);
                    if(fs.readSync(fd,block,0,length,response.offset)!==length)
                        throw Error('Job log shortened during transfer.');
                    response=await remoteCall(endpoint,machineId,token,
                        record.job_id,relative,snapshot,response.offset,block);
                    sent+=length;
                }
                if(response.complete) {
                    cache.set(key,{...proof,mtimeMs:st.mtimeMs,complete:true});
                    completed++;
                }
            }finally{fs.closeSync(fd);}
        }
    }
    return {bytes:sent,files:completed};
}
module.exports={syncArtifacts,filesForJob,snapshotFile,remoteCall};
