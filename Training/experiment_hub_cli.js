#!/usr/bin/env node
'use strict';

// Loopback client for the same operator API used by the dashboard.
const fs=require('node:fs');
const {parseArgs}=require('node:util');
const {isHubAdminPath}=require('./experiment_hub_admin');
const BEES_GET=new Set(['workers','control']);
const BEES_POST=new Set(['start','stop','build','update-workers']);
const HELP=[
    'Usage: node Training/experiment_hub_cli.js get <resource[?query]> [--port 7148]',
    '       node Training/experiment_hub_cli.js post <resource> --data <file.json> [--port 7148]',
    'Resources are relative to /api/hub/, such as state, jobs/plan, jobs/start, or bees/control.',
    'POST uses the supplied JSON unchanged. Include any confirmation required by the dashboard API.',
].join('\n');

async function main(argv=process.argv.slice(2)) {
    const {values,positionals}=parseArgs({args:argv,allowPositionals:true,options:{
        port:{type:'string',default:'7148'},data:{type:'string'},help:{type:'boolean'},
    }});
    if(values.help){console.log(HELP);return;}
    const [verb,resource]=positionals;
    if(positionals.length!==2||!['get','post'].includes(verb)||
        !/^[a-z-]+(?:\/[a-z-]+)*(?:\?[^#\r\n]*)?$/.test(resource))
        throw Error(HELP);
    const port=Number(values.port);
    if(!/^[0-9]{1,5}$/.test(values.port)||port<1||port>65535)
        throw Error('Port must be between 1 and 65535.');
    const method=verb.toUpperCase();
    const url=new URL('http://127.0.0.1:'+port+'/api/hub/'+resource);
    const beesAction=url.pathname.startsWith('/api/hub/bees/')
        &&url.pathname.slice('/api/hub/bees/'.length);
    const beesPaths=method==='GET'?BEES_GET:BEES_POST;
    if(!isHubAdminPath(method,url.pathname)&&!beesPaths.has(beesAction))
        throw Error('Unknown Hub operator resource for '+method+'.');
    let body;
    if(method==='POST') {
        if(!values.data||url.search)throw Error('POST requires --data and a resource without a query.');
        body=fs.readFileSync(values.data,'utf8');
        JSON.parse(body);
    }else if(values.data!==undefined)throw Error('GET does not accept --data.');
    const response=await fetch(url,{method,redirect:'error',
        signal:AbortSignal.timeout(30000),
        ...(body!==undefined?{body,headers:{'Content-Type':'application/json'}}:{})});
    const result=await response.json();
    if(!response.ok)throw Error('HTTP '+response.status+': '+(result?.error||response.statusText));
    console.log(JSON.stringify(result,null,2));
}

if(require.main===module)main().catch(error=>{
    console.error(error.message);process.exitCode=1;
});
module.exports={main};
