'use strict';

// Loaded only by the trusted Windows service launcher. Keep application
// diagnostics bounded without changing stdout ownership of training children.
const fs=require('node:fs');
const path=require('node:path');
const {Console}=require('node:console');
const {Writable}=require('node:stream');
const root=process.env.EXPERIMENT_HUB_SERVICE_LOG_ROOT;
const name=process.env.EXPERIMENT_HUB_SERVICE_NAME;
const LIMIT=1048576;
if(!path.isAbsolute(root||'')||!fs.statSync(root).isDirectory()||
    !/^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$/.test(name||''))
    throw Error('Service console requires its locally configured state directory and identity.');

function checkedFile(file) {
    if(!fs.existsSync(file))return;
    const info=fs.lstatSync(file);
    if(!info.isFile()||info.isSymbolicLink())
        throw Error('Service console destination must be a regular local file.');
    // This namespace belongs to the service. Bound a pre-existing oversized
    // log too, without loading its entire contents into memory.
    if(info.size>LIMIT)fs.truncateSync(file,0);
}
function channel(kind) {
    const file=path.join(root,name+'.'+kind+'.log'),previous=file+'.previous';
    checkedFile(file);checkedFile(previous);
    // Windows append-only handles cannot be truncated during rotation.
    const fd=fs.openSync(file,fs.existsSync(file)?'r+':'wx+',0o600);
    let size=fs.fstatSync(fd).size;
    const append=chunk=>{
        const stamp=Buffer.from(new Date().toISOString()+' ');
        const content=Buffer.isBuffer(chunk)?chunk:Buffer.from(chunk);
        const note=Buffer.from('[oversized console message truncated]\n');
        const data=stamp.length+content.length>LIMIT
            ?Buffer.concat([stamp,note,content.subarray(content.length-(LIMIT-stamp.length-note.length))])
            :Buffer.concat([stamp,content]);
        if(size+data.length>LIMIT) {
            // Copy/truncate keeps the active file handle stable for readers.
            fs.copyFileSync(file,previous);
            fs.ftruncateSync(fd,0);
            size=0;
        }
        let written=0;
        while(written<data.length) {
            const count=fs.writeSync(fd,data,written,data.length-written,size+written);
            if(!count)throw Error('Service console write made no progress.');
            written+=count;
        }
        size+=data.length;
    };
    const stream=new Writable({write(chunk,encoding,done){
        try{append(chunk);done();}catch(error){done(error);}
    }});
    process.once('exit',()=>fs.closeSync(fd));
    return {stream,append};
}
const output=channel('stdout'),errors=channel('stderr');
global.console=new Console({stdout:output.stream,stderr:errors.stream,
    colorMode:false,ignoreErrors:false});
// Observe fatal JavaScript failures without intercepting or recovering them.
process.on('uncaughtExceptionMonitor',(error,origin)=>{
    errors.append(origin+': '+(error?.stack||String(error))+'\n');
});
