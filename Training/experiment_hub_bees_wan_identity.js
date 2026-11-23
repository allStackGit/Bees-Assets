'use strict';

// One immutable WAN identity shared by the Bees Hub learner and its remote
// rollout roles. The token remains in administrator-installed private files.
const fs=require('node:fs');
const crypto=require('node:crypto');
const path=require('node:path');
const SHA=/^[0-9a-f]{64}$/;
const PORT=/^[1-9][0-9]*$/;
const PREFIX='bees-native-wan-v1:';

function identity(workerSha,trainerSha,runtimeVersion,lineageRoot) {
    for(const [name,value] of Object.entries({
        worker_sha256:workerSha,trainer_sha256:trainerSha,
        runtime_version:runtimeVersion,
    }))if(!SHA.test(String(value||'')))throw Error('Invalid Bees WAN '+name+'.');
    if(typeof lineageRoot!=='string'||!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(lineageRoot))
        throw Error('Bees WAN needs the original immutable optimizer lineage.');
    return Object.freeze({
        build_id:workerSha,run_id:lineageRoot,
        compatibility_key:crypto.createHash('sha256')
            .update(PREFIX+workerSha+':'+trainerSha+':'+runtimeVersion,'ascii')
            .digest('hex'),
        environment_id:workerSha,
    });
}
function privateKey(file) {
    if(typeof file!=='string'||!path.isAbsolute(file)||
        fs.realpathSync(file)!==file)throw Error('Bees WAN secret must be a real absolute path.');
    const stat=fs.statSync(file);
    if(!stat.isFile()||stat.size<64||stat.size>128||
        (process.platform!=='win32'&&(stat.mode&0o077)!==0))
        throw Error('Bees WAN secret must be a private 32-byte hex key file.');
    const value=fs.readFileSync(file,'utf8').trim();
    if(!SHA.test(value))throw Error('Bees WAN secret must hold exactly 64 lowercase hex characters.');
    return {
        path:file,
        fingerprint:crypto.createHash('sha256').update(value,'ascii').digest('hex'),
    };
}
function brokerPort(value) {
    const raw=String(value||'');
    const number=Number(raw);
    if(!PORT.test(raw)||!Number.isSafeInteger(number)||number<1024||number>65535)
        throw Error('Bees WAN broker/forward port must be 1024–65535.');
    return number;
}
module.exports={identity,privateKey,brokerPort,PREFIX};
