'use strict';

// Optional machine-authenticated, read-only trainer-log mirroring. This never
// accepts a source filesystem path or executable from the worker.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const SHA=/^[0-9a-f]{64}$/;
const MAX_CHUNK=1024*1024;
const MAX_FILE=500000000;
const MAX_FILES=256;
const MAX_JOB=5000000000;
const SUFFIXES=new Set(['.json','.jsonl','.ndjson','.csv','.tsv','.txt','.log']);
const DENIED=new Set(['.git','private','secrets','credentials','keys','checkpoints',
    'models','opponents','replays','__pycache__','build','node_modules']);
const DENIED_NAMES=['token','secret','password','credential','checkpoint','rollout.key',
    'api_key','api-key','api.key','apikey','access_key','access-key','accesskey',
    'private_key','private-key','privatekey','ssh_key','ssh-key','sshkey',
    'key_file','key-file','keyfile','id_rsa','id_ed25519',
    'service_account','service-account','connection_string',
    'connection-string','connectionstring','bearer','authorization','authentication'];
function safeLogPath(relative) {
    if(typeof relative!=='string'||relative.length>320||
        relative.includes('\\'))return false;
    const parts=relative.split('/');
    if(parts.length<1||parts.length>8||parts.some(s=>
        !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$/.test(s)||
        DENIED.has(s.toLowerCase())||
        DENIED_NAMES.some(name=>s.toLowerCase().includes(name))||
        s==='.'||s==='..'))return false;
    return SUFFIXES.has(path.posix.extname(relative).toLowerCase());
}
function fail(message,status=400) {
    const error=Error(message);
    error.status=status;
    throw error;
}
function privateDir(folder) {
    if(!fs.existsSync(folder))fs.mkdirSync(folder,{mode:0o700});
    const stat=fs.lstatSync(folder);
    if(!stat.isDirectory()||stat.isSymbolicLink()||
        fs.realpathSync(folder)!==folder||
        (typeof process.getuid==='function'&&stat.uid!==process.getuid())||
        process.platform!=='win32'&&(stat.mode&0o077)!==0)
        fail('Unsafe artifact storage directory.',409);
}
function privateTree(root,components) {
    let folder=root;
    privateDir(folder);
    for(const segment of components) {
        folder=path.join(folder,segment);
        privateDir(folder);
    }
    return folder;
}
function regular(file,allowMissing=false) {
    try {
        const st=fs.lstatSync(file);
        if(!st.isFile()||st.isSymbolicLink()||st.nlink!==1||
            (typeof process.getuid==='function'&&st.uid!==process.getuid()))
            fail('Artifact file was replaced or redirected.',409);
        return st;
    }catch(error) {
        if(allowMissing&&error.code==='ENOENT')return null;
        throw error;
    }
}
function durableJson(file,meta) {
    const temp=file+'.'+crypto.randomBytes(8).toString('hex')+'.tmp';
    try {
        fs.writeFileSync(temp,JSON.stringify(meta),{flag:'wx',mode:0o600});
        const fd=fs.openSync(temp,fs.constants.O_RDONLY|
            (fs.constants.O_NOFOLLOW||0));
        try{fs.fsyncSync(fd);}finally{fs.closeSync(fd);}
        fs.renameSync(temp,file);
    }finally {
        if(fs.existsSync(temp))fs.unlinkSync(temp);
    }
}
function shaFile(file) {
    const fd=fs.openSync(file,fs.constants.O_RDONLY|
        (fs.constants.O_NOFOLLOW||0));
    try {
        const st=fs.fstatSync(fd);
        if(!st.isFile()||st.nlink!==1)fail('Artifact source changed.',409);
        const hasher=crypto.createHash('sha256'),buf=Buffer.allocUnsafe(MAX_CHUNK);
        let offset=0;
        while(offset<st.size) {
            const n=fs.readSync(fd,buf,0,Math.min(buf.length,st.size-offset),offset);
            if(n<1)fail('Artifact shortened during verification.',409);
            hasher.update(buf.subarray(0,n));offset+=n;
        }
        return hasher.digest('hex');
    }finally{fs.closeSync(fd);}
}
class ArtifactMirror {
    constructor(getHub) {
        if(typeof getHub!=='function')throw Error('Artifact mirror requires a Hub.');
        this.getHub=getHub;
    }
    authorize(machineId,token,jobId,relative,sha,size) {
        const hub=this.getHub();
        hub.authenticateAgent(machineId,token);
        const job=UUID.test(String(jobId||''))&&hub.state.jobs.find(j=>j.id===jobId);
        if(!job||job.assigned_machine_id!==machineId||
            job.project_id==='bees'||!job.lease_id||
            job.start_first_offered_at==null)
            fail('Artifact job is not owned by this machine.',409);
        if(!safeLogPath(relative)||!SHA.test(String(sha||''))||
            !Number.isSafeInteger(size)||size<1||size>MAX_FILE)
            fail('Invalid artifact snapshot.',400);
        const root=path.dirname(hub.file);
        // The coordinator data root may have ordinary parent permissions.
        // Its mirroring subdirectories, not the whole Hub root, are private.
        const storage=privateTree(path.join(root,'hub-remote-artifacts'),[jobId]);
        const staging=privateTree(path.join(root,'hub-remote-artifact-staging'),[jobId]);
        const parts=relative.split('/');
        const destination=path.join(privateTree(storage,parts.slice(0,-1)),
            parts[parts.length-1]);
        const key=crypto.createHash('sha256').update(relative).digest('hex');
        return {storage,staging,destination,
            metaFile:path.join(staging,key+'.json'),
            partFile:path.join(staging,key+'.part'),relative,sha,size,jobId};
    }
    metadata(info) {
        const stat=regular(info.metaFile,true);
        if(!stat)return null;
        if(stat.size<10||stat.size>1024)fail('Invalid artifact staging metadata.',409);
        const meta=JSON.parse(fs.readFileSync(info.metaFile,'utf8'));
        if(meta?.relative!==info.relative||!SHA.test(meta.sha)||
            !Number.isSafeInteger(meta.size)||meta.size<1||meta.size>MAX_FILE||
            typeof meta.complete!=='boolean')fail('Artifact staging metadata mismatch.',409);
        return meta;
    }
    usage(info) {
        let size=0,count=0;
        for(const name of fs.readdirSync(info.staging)) {
            if(!/^[a-f0-9]{64}\.json$/.test(name))continue;
            const metaFile=path.join(info.staging,name);
            const st=regular(metaFile);
            if(st.size<10||st.size>1024)fail('Invalid artifact metadata.',409);
            const meta=JSON.parse(fs.readFileSync(metaFile,'utf8'));
            if(!Number.isSafeInteger(meta.size)||meta.size<1||meta.size>MAX_FILE)
                fail('Invalid artifact budget metadata.',409);
            if(name!==path.basename(info.metaFile)){size+=meta.size;count++;}
        }
        if(count>=MAX_FILES||size+info.size>MAX_JOB)
            fail('Job artifact storage limit reached.',413);
    }
    finalize(info) {
        if(shaFile(info.partFile)!==info.sha) {
            fs.unlinkSync(info.partFile);
            fail('Artifact snapshot checksum changed; retry a fresh snapshot.',409);
        }
        regular(info.destination,true);
        fs.renameSync(info.partFile,info.destination);
        durableJson(info.metaFile,{relative:info.relative,
            sha:info.sha,size:info.size,complete:true});
        return {offset:info.size,complete:true};
    }
    status(machineId,token,jobId,relative,sha,size) {
        const info=this.authorize(machineId,token,jobId,relative,sha,size);
        const meta=this.metadata(info);
        if(!meta||meta.sha!==sha||meta.size!==size)
            return {offset:0,complete:false};
        if(meta.complete) {
            const done=regular(info.destination,true);
            if(done&&done.size===size)return {offset:size,complete:true};
            return {offset:0,complete:false};
        }
        const pending=regular(info.partFile,true);
        if(!pending)return {offset:0,complete:false};
        if(pending.size>size)fail('Artifact staging file exceeds snapshot.',409);
        // A crash or a Windows file lock can interrupt publication after
        // the last chunk was synced. Complete that existing verified stage
        // instead of stranding its upload at offset == total size.
        if(pending.size===size)return this.finalize(info);
        return {offset:pending.size,complete:false};
    }
    append(machineId,token,jobId,relative,sha,size,offset,bytes) {
        const info=this.authorize(machineId,token,jobId,relative,sha,size);
        if(!Number.isSafeInteger(offset)||offset<0||offset>=size||
            !Buffer.isBuffer(bytes)||bytes.length<1||bytes.length>MAX_CHUNK||
            bytes.length>size-offset)
            fail('Invalid artifact transfer offset or chunk.',400);
        let meta=this.metadata(info);
        if(meta?.sha!==sha||meta?.size!==size||meta?.complete) {
            if(meta?.complete&&meta.sha===sha&&meta.size===size) {
                const done=regular(info.destination,true);
                if(done&&done.size===size)
                    return {offset:size,complete:true};
            }
            if(offset!==0)fail('Artifact snapshot changed; restart at offset zero.',409);
            this.usage(info);
            const old=regular(info.partFile,true);
            if(old)fs.unlinkSync(info.partFile);
            durableJson(info.metaFile,{relative,sha,size,complete:false});
            meta=this.metadata(info);
        }
        const current=regular(info.partFile,true);
        if((current?.size||0)!==offset)fail('Artifact offset changed; query status.',409);
        const fd=fs.openSync(info.partFile,
            fs.constants.O_RDWR|fs.constants.O_CREAT|
            (fs.constants.O_NOFOLLOW||0),0o600);
        try {
            const st=fs.fstatSync(fd);
            if(!st.isFile()||st.nlink!==1||st.size!==offset)
                fail('Artifact staging file changed.',409);
            let position=0;
            while(position<bytes.length) {
                const n=fs.writeSync(fd,bytes,position,bytes.length-position,
                    offset+position);
                if(n<1)fail('Artifact staging write incomplete.',409);
                position+=n;
            }
            fs.fsyncSync(fd);
        }finally{fs.closeSync(fd);}
        const next=offset+bytes.length;
        if(next===size)return this.finalize(info);
        return {offset:next,complete:false};
    }
}
module.exports={ArtifactMirror,safeLogPath,MAX_CHUNK,MAX_FILE};
