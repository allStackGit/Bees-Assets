#!/usr/bin/env node
'use strict';

// Loopback client for the same operator API used by the dashboard.
const fs=require('node:fs');
const {Readable,Transform}=require('node:stream');
const {pipeline}=require('node:stream/promises');
const {parseArgs}=require('node:util');
const {isHubAdminPath}=require('./experiment_hub_admin');
const BEES_GET=new Set(['workers','control']);
const BEES_POST=new Set(['start','stop','build','update-workers']);
const EXPORT_ID='[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}';
const EXPORT_STATUS=new RegExp('^exports/'+EXPORT_ID+'$');
const EXPORT_PART=new RegExp('^exports/'+EXPORT_ID+'/part/[1-9][0-9]*$');
const MAX_ZIP_BYTES=500000000;
// Experiment-owned actions use the same coordinator's existing JSON API.
// Never let the CLI forward arbitrary paths or change the process owner.
const MANAGER_GET=new Set([
    'state','storage','curriculum-default','configuration','workers',
    'benchmarks','metric-history','research-summary','queued',
]);
const MANAGER_POST=new Set([
    'storage','worker-envs','curriculum-default','queue','edit-queued',
    'research-heldout','evaluate','pause','rebuild-unity',
    'update-workers','resume','start','stop',
]);
const HELP=[
    'Usage: node Training/experiment_hub_cli.js get <resource[?query]> [--port 7148]',
    '       node Training/experiment_hub_cli.js post <resource> --data <file.json> [--port 7148]',
    'Hub resources: state, jobs/plan, jobs/start, bees/control (relative to /api/hub/).',
    'Experiment manager JSON resources: manager/state, manager/queue, manager/pause, manager/resume,',
    '  manager/metric-history?id=..., manager/research-summary and other existing JSON routes.',
    'POST uses the supplied JSON unchanged. Include any confirmation required by the dashboard API.',
    'Export: post exports --data request.json; get exports/<id>;',
    '        get exports/<id>/part/1 --output diagnostic-part-1.zip (repeat for each part).',
].join('\n');

async function main(argv=process.argv.slice(2)) {
    const {values,positionals}=parseArgs({args:argv,allowPositionals:true,options:{
        port:{type:'string',default:'7148'},data:{type:'string'},
        output:{type:'string'},help:{type:'boolean'},
    }});
    if(values.help){console.log(HELP);return;}
    const [verb,resource]=positionals;
    if(positionals.length!==2||!['get','post'].includes(verb)||
        !/^[a-z-]+(?:\/[a-z0-9-]+)*(?:\?[^#\r\n]*)?$/.test(resource))
        throw Error(HELP);
    const port=Number(values.port);
    if(!/^[0-9]{1,5}$/.test(values.port)||port<1||port>65535)
        throw Error('Port must be between 1 and 65535.');
    const method=verb.toUpperCase();
    const manager=resource.startsWith('manager/');
    const relative=manager?resource.slice('manager/'.length):resource;
    const url=new URL('http://127.0.0.1:'+port+
        (manager?'/api/':'/api/hub/')+relative);
    const beesAction=!manager&&url.pathname.startsWith('/api/hub/bees/')
        &&url.pathname.slice('/api/hub/bees/'.length);
    const beesPaths=method==='GET'?BEES_GET:BEES_POST;
    const managerPaths=method==='GET'?MANAGER_GET:MANAGER_POST;
    const exportStatus=!manager&&EXPORT_STATUS.test(relative);
    const exportPart=!manager&&EXPORT_PART.test(relative);
    const exportStart=!manager&&relative==='exports';
    if(!(manager?managerPaths.has(relative.split('?')[0]):
        isHubAdminPath(method,url.pathname)||beesPaths.has(beesAction)||
        method==='POST'&&exportStart||
        method==='GET'&&(exportStatus||exportPart)))
        throw Error('Unknown or unsupported operator resource for '+method+'.');
    if(exportPart) {
        if(!values.output||values.data!==undefined||url.search)
            throw Error('ZIP part downloads require --output and no --data or query.');
    }else if(values.output!==undefined)
        throw Error('--output is supported only for a ZIP part download.');
    let body;
    if(method==='POST') {
        if(!values.data||url.search)throw Error('POST requires --data and a resource without a query.');
        body=fs.readFileSync(values.data,'utf8');
        JSON.parse(body);
    }else if(values.data!==undefined)throw Error('GET does not accept --data.');
    const response=await fetch(url,{method,redirect:'error',
        signal:AbortSignal.timeout(exportPart?300000:30000),
        ...(body!==undefined?{body,headers:{'Content-Type':'application/json'}}:{})});
    if(exportPart) {
        if(!response.ok) {
            const error=await response.json();
            throw Error('HTTP '+response.status+': '+(error?.error||response.statusText));
        }
        const bytes=Number(response.headers.get('content-length'));
        if(response.headers.get('content-type')?.split(';')[0].toLowerCase()!=='application/zip'||
            !Number.isSafeInteger(bytes)||bytes<1||bytes>MAX_ZIP_BYTES||
            !response.body)
            throw Error('Export part has an invalid ZIP response or size.');
        // Open exclusively. Failed transfers only remove the inode we
        // created; never overwrite or delete an operator's existing file.
        const fd=fs.openSync(values.output,'wx',0o600);
        const created=fs.fstatSync(fd);
        let handedOff=false,received=0;
        try {
            const destination=fs.createWriteStream(values.output,{fd,autoClose:true});
            handedOff=true;
            await pipeline(Readable.fromWeb(response.body),
                new Transform({transform(chunk,encoding,callback){
                    received+=chunk.length;
                    if(received>bytes||received>MAX_ZIP_BYTES)
                        callback(Error('Export part exceeded its declared size.'));
                    else callback(null,chunk);
                }}),destination);
            if(received!==bytes)throw Error('Export part was truncated.');
        }catch(error) {
            if(!handedOff)fs.closeSync(fd);
            try {
                const current=fs.lstatSync(values.output);
                if(current.isFile()&&!current.isSymbolicLink()&&
                    current.ino===created.ino&&current.dev===created.dev)
                    fs.unlinkSync(values.output);
            }catch { /* A removed/changed output is not ours to delete. */ }
            throw error;
        }
        console.log('Saved '+values.output+' ('+received+' bytes)');
        return;
    }
    const result=await response.json();
    if(!response.ok)throw Error('HTTP '+response.status+': '+(result?.error||response.statusText));
    console.log(JSON.stringify(result,null,2));
}

if(require.main===module)main().catch(error=>{
    console.error(error.message);process.exitCode=1;
});
module.exports={main};
