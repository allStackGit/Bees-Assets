'use strict';

// Identity of the complete Unity player directory, not merely its executable.
// Native loader files and Assets/StreamingAssets can change independently of
// Bees RL Training.x86_64 while the launcher executable stays byte-identical.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const MAX_FILES=20000;
const MAX_BYTES=32*1024**3;
const MAX_DEPTH=12;
function digestFile(file) {
    const hash=crypto.createHash('sha256'),fd=fs.openSync(file,'r');
    const buffer=Buffer.allocUnsafe(1024*1024);
    try{
        for(;;){
            const n=fs.readSync(fd,buffer,0,buffer.length,null);
            if(!n)break;
            hash.update(buffer.subarray(0,n));
        }
    }finally{fs.closeSync(fd);}
    return hash.digest('hex');
}
function unityWorkerTreeSha256(entrypoint) {
    const root=path.dirname(entrypoint);
    if(!path.isAbsolute(entrypoint)||fs.realpathSync(entrypoint)!==entrypoint||
        fs.realpathSync(root)!==root)
        throw Error('Unity player must be installed in a real absolute directory.');
    const files=[],queue=[{root,depth:0}];
    let bytes=0;
    while(queue.length) {
        const item=queue.shift();
        for(const entry of fs.readdirSync(item.root,{withFileTypes:true})) {
            if(!/^[\x20-\x7e]+$/.test(entry.name)||
                entry.name.includes('\\')||entry.name.includes('/'))
                throw Error('Unity player includes an unsupported file name.');
            const filename=path.join(item.root,entry.name);
            if(entry.isSymbolicLink()||fs.realpathSync(filename)!==filename)
                throw Error('Unity player has redirected or symlinked content.');
            if(entry.isDirectory()){
                if(item.depth>=MAX_DEPTH)
                    throw Error('Unity player directory depth exceeds a safe bound.');
                queue.push({root:filename,depth:item.depth+1});
            }else if(entry.isFile()) {
                const statBefore=fs.statSync(filename);
                bytes+=statBefore.size;
                if(!Number.isSafeInteger(bytes)||bytes>MAX_BYTES||files.length>=MAX_FILES)
                    throw Error('Unity player tree exceeds bounded content identity capacity.');
                const digest=digestFile(filename);
                const statAfter=fs.statSync(filename);
                if(statAfter.size!==statBefore.size||
                    statAfter.mtimeMs!==statBefore.mtimeMs)
                    throw Error('Unity player content changed during identity capture.');
                const rel=path.relative(root,filename).split(path.sep).join('/');
                files.push([rel,statBefore.size,digest]);
            }else throw Error('Unity player has an unsupported special file.');
        }
    }
    const entry=path.basename(entrypoint);
    if(!files.some(row=>row[0]===entry)||files.length<2)
        throw Error('Unity player must include executable and supporting build files.');
    files.sort((a,b)=>a[0]<b[0]?-1:a[0]>b[0]?1:0);
    return crypto.createHash('sha256')
        .update(JSON.stringify(files),'utf8').digest('hex');
}
module.exports={unityWorkerTreeSha256};
