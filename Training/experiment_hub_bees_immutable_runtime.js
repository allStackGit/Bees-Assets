'use strict';

// Reuse Bees' established content-hashed training runtime format. A Hub job
// runs from a verified, versioned Python installation, never from a moving
// Assets/Training checkout. The archive is prepared only at trusted agent
// enrollment; scheduling payloads cannot select its source or destination.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');

const SHA=/^[a-f0-9]{64}$/;
const GIT=/^[a-f0-9]{40}$/;

function fileHash(file) {
    const hash=crypto.createHash('sha256');
    const fd=fs.openSync(file,'r');
    const buffer=Buffer.allocUnsafe(1048576);
    try {
        while(true) {
            const count=fs.readSync(fd,buffer,0,buffer.length,null);
            if(!count)break;
            hash.update(buffer.subarray(0,count));
        }
    }finally {fs.closeSync(fd);}
    return hash.digest('hex');
}

function command(executable,argv,cwd) {
    const result=spawnSync(executable,argv,{
        cwd,encoding:'utf8',windowsHide:true,timeout:120000,
        maxBuffer:1024*1024,
    });
    if(result.error||result.status!==0)
        throw Error('Bees immutable runtime operation failed: '+
            String(result.error?.message||result.stderr||result.stdout||result.status).slice(0,600));
    return String(result.stdout||'').trim();
}

function pythonJson(executable,args,cwd) {
    const raw=command(executable,args,cwd);
    try {
        const value=JSON.parse(raw);
        if(!value||typeof value!=='object'||Array.isArray(value))
            throw Error('Missing Bees runtime JSON object.');
        return value;
    }catch(error){
        throw Error('Bees runtime tool returned invalid JSON: '+String(error.message||error));
    }
}

function prepare({project,python,artifactRoot,workerSha}) {
    if(process.platform!=='linux')
        throw Error('Bees native runtime pin requires Linux machine ownership.');
    if(!SHA.test(String(workerSha||'')))
        throw Error('Bees Unity worker must have a pinned SHA-256 before runtime packaging.');
    const resolved=fs.realpathSync(project),storage=fs.realpathSync(artifactRoot);
    const packager=path.join(resolved,'Training','bees_release_runtime.py');
    if(fs.realpathSync(packager)!==packager||!fs.statSync(packager).isFile())
        throw Error('Trusted Bees Python runtime packager is unavailable.');
    const packageHash=fileHash(packager);
    const source=command('git',['rev-parse','HEAD'],resolved);
    if(!GIT.test(source))
        throw Error('The Bees-Assets training checkout needs an exact Git revision.');
    const id='hub-native-'+workerSha.slice(0,24);
    const cache=path.join(storage,'_immutable_runtime');
    fs.mkdirSync(cache,{recursive:true,mode:0o700});
    if(fs.realpathSync(cache)!==cache||fs.statSync(cache).mode&0o077)
        throw Error('Immutable Bees runtime cache must be private and non-redirected.');
    const archive=path.join(cache,'runtime.zip');
    const packaged=pythonJson(python,[
        '-B',packager,'package','--assets-root',resolved,
        '--output',archive,'--build-id',id,'--source-commit',source,
    ],resolved);
    if(fileHash(packager)!==packageHash||
        packaged.build_id!==id||
        packaged.source_commit!==source||
        !SHA.test(String(packaged.runtime_version||''))||
        !SHA.test(String(packaged.archive_sha256||''))||
        packaged.archive!==archive||
        fileHash(archive)!==packaged.archive_sha256)
        throw Error('Bees immutable runtime packaging changed its inputs or identity.');
    const installed=pythonJson(python,[
        '-B',packager,'install','--archive',archive,
        '--destination-root',path.join(cache,'installed'),
        '--expected-sha256',packaged.archive_sha256,
        '--expected-version',packaged.runtime_version,
        '--expected-build-id',id,
    ],resolved);
    const root=fs.realpathSync(installed.installed_root);
    if(!root.startsWith(path.join(cache,'installed')+path.sep)||
        !fs.statSync(root).isDirectory()||
        installed.archive_sha256!==packaged.archive_sha256||
        installed.runtime_version!==packaged.runtime_version)
        throw Error('Installed Bees runtime does not match its pinned archive.');
    const file=name=>{
        const candidate=path.join(root,name);
        if(fs.realpathSync(candidate)!==candidate||!fs.statSync(candidate).isFile())
            throw Error('Immutable Bees runtime file missing: '+name);
        return candidate;
    };
    return {
        root,
        runtimeVersion:packaged.runtime_version,
        sourceCommit:source,
        archiveSha256:packaged.archive_sha256,
        training:file('bees_mlagents_learn.py'),
        trainer:file('rl_1v1_config.yaml'),
        runner:file('experiment_hub_bees_native_train.py'),
        evaluator:file('bees_experiment_benchmark.py'),
        evaluatorCore:file('bees_continual_evaluate.py'),
        opponentScript:file('bees_experiment_scripted.py'),
    };
}
module.exports={prepare,fileHash};
