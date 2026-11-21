'use strict';

// Opt-in job-log synchronization independent of trainer/process control.
// Only the installed adapter chooses a job directory; Hub never sends a path.
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const crypto=require('node:crypto');
const {safeLogPath,safeNativeCollectorPath,safeNativeModelPath,MAX_CHUNK,MAX_FILE}=
    require('./experiment_hub_artifact_sync');
const {recoveryRecordJobId}=require('./experiment_hub_native_release_recovery');
const TERMINAL=new Set(['finished','failed','stopped']);
const MAX_CYCLE=32*1024*1024;
function filesForJob(folder,projectId,nativeTrainingOnly=false) {
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
            const allowed=name=>nativeTrainingOnly
                ?projectId==='bees_native'&&
                    (safeNativeModelPath(name)||[
                        'round-result.json', 'optimizer-attempt.json',
                        'learner/metrics.json','continual-release.json',
                    ].includes(name))||
                    projectId==='bees_native_collect'&&
                    (name==='rollout-bundle.json'||
                     /(?:^|\/)(?:decisions\.bin|events\.jsonl)$/.test(name)&&
                        safeNativeCollectorPath(name))
                :projectId==='bees_native_collect'
                    ?safeNativeCollectorPath(name)
                    :safeLogPath(name)||
                        projectId==='bees_native'&&safeNativeModelPath(name);
            if(item.isDirectory()&&next.depth<7) {
                const modelFolder=projectId==='bees_native'&&
                    ['learner','learner/opponents'].includes(relative);
                const rolloutFolder=projectId==='bees_native_collect'&&
                    safeNativeCollectorPath(relative+'/decisions.bin');
                if((allowed(relative+'/ignored.log')||modelFolder||rolloutFolder)&&
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
async function snapshotFile(root,relative,allowHardlink=false) {
    const source=path.join(root,...relative.split('/'));
    const real=fs.realpathSync(source);
    if(real!==source||!real.startsWith(path.resolve(root)+path.sep))
        throw Error('Job log escaped its approved artifact directory.');
    const fd=fs.openSync(source,fs.constants.O_RDONLY|
        (fs.constants.O_NOFOLLOW||0));
    try {
        const before=fs.fstatSync(fd);
        if(!before.isFile()||(!allowHardlink&&before.nlink!==1)||
            before.nlink<1||before.size<1||
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
            headers:{'X-Hub-Agent-Token':token,
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
async function syncArtifacts(executor,endpoint,machineId,token,cache=new Map(),
    nativeTrainingOnly=false) {
    if(!executor?.supervisor?.snapshot)return {bytes:0,files:0};
    let sent=0,completed=0;
    // Native ownership is local and durable. The coordinator cannot give
    // the agent a filename or a new file root through a heartbeat.
    const records=executor.supervisor.snapshot();
    // The latest physical attempt owns its logical output. Never mirror a
    // running capture through an older, historical failed owner.
    const latest=new Map(records.map(record=>[recoveryRecordJobId(record),record]));
    for(const record of latest.values()) {
        if(!TERMINAL.has(record.state))continue;
        const project=record.project_id,adapter=executor.supervisor.adapters?.[project];
        // Failed physical collectors may own recoverable journals and
        // captures. Never mirror those mutable files as optimizer inputs.
        // Only the terminal successful collector publishes its bundle.
        if(nativeTrainingOnly&&project==='bees_native_collect'&&record.state!=='finished')
            continue;
        if(nativeTrainingOnly&&
            (!['bees_native','bees_native_collect'].includes(project)||
             // Only a finished, checkpoint-verified publisher can mirror
             // transferable model files. Failed synchronized groups can
             // leave partial weights; do not let their rejected uploads
             // block later valid checkpoint owners on this agent.
             project==='bees_native'&&record.state!=='finished'))continue;
        // Collectors carry binary training experience rather than only
        // diagnostics. Raise their bounded per-cycle delivery allowance
        // without changing general telemetry's smaller background budget.
        const cycleBudget=['bees_native_collect','bees_native'].includes(project)
            ?128*1024*1024:MAX_CYCLE;
        if(typeof adapter?.artifact_export_dir!=='function')continue;
        if(!executor.projects.includes(project))continue;
        const jobId=recoveryRecordJobId(record);
        const root=adapter.artifact_export_dir(jobId);
        if(typeof root!=='string'||!path.isAbsolute(root))continue;
        if(!fs.existsSync(root))continue;
        for(const relative of filesForJob(root,project,nativeTrainingOnly)) {
            // Normal diagnostic mirroring still includes failed-job logs.
            // Only the explicit model-package paths are completion-only.
            if(project==='bees_native'&&record.state!=='finished'&&
                safeNativeModelPath(relative))continue;
            if(project==='bees_native_collect'&&record.state!=='finished'&&
                (relative==='rollout-bundle.json'||
                 /(?:^|\/)(?:decisions\.bin|events\.jsonl)$/.test(relative)))continue;
            const file=path.join(root,...relative.split('/'));
            let st;
            try{st=fs.lstatSync(file);}catch{continue;}
            const key=jobId+':'+relative;
            const cached=cache.get(key);
            const unchanged=cached&&st.isFile()&&st.dev===cached.dev&&
                st.ino===cached.ino&&st.size===cached.size&&
                st.mtimeMs===cached.mtimeMs;
            if(unchanged&&cached.complete)continue;
            const proof=unchanged?cached:await snapshotFile(
                root,relative,project==='bees_native'&&safeNativeModelPath(relative));
            if(!proof)continue;
            const snapshot={size:proof.size,sha:proof.sha};
            cache.set(key,{...proof,mtimeMs:st.mtimeMs,complete:false});
            if(cache.size>10000)cache.clear();
            let response=await remoteCall(endpoint,machineId,token,
                jobId,relative,snapshot,0,null);
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
                // The native learner intentionally hard-links immutable
                // frozen opponents into its published model package. The
                // initial SHA snapshot allows these exact model paths;
                // the streaming phase must enforce the SAME rule or the
                // checkpoint handoff silently stalls after discovery.
                const modelArtifact=project==='bees_native'&&
                    safeNativeModelPath(relative);
                if(!opened.isFile()||opened.nlink<1||
                    (!modelArtifact&&opened.nlink!==1)||
                    opened.dev!==proof.dev||opened.ino!==proof.ino||
                    opened.size<proof.size)continue;
                while(response.offset<snapshot.size&&sent<cycleBudget) {
                    const length=Math.min(MAX_CHUNK,snapshot.size-response.offset,
                        cycleBudget-sent);
                    const block=Buffer.allocUnsafe(length);
                    if(fs.readSync(fd,block,0,length,response.offset)!==length)
                        throw Error('Job log shortened during transfer.');
                    response=await remoteCall(endpoint,machineId,token,
                        jobId,relative,snapshot,response.offset,block);
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
