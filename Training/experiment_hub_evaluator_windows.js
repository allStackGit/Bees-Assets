#!/usr/bin/env node
'use strict';

// Independent local evaluation inside the Windows service launcher's
// verified CPU/RAM Job Object. This never starts or stops training jobs.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawn,execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {parseArgs}=require('node:util');
const {setTimeout:delay}=require('node:timers/promises');
const run=promisify(execFile);
const ID=/^[a-z][a-z0-9_-]{0,47}$/;
const NAME=/^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$/;
const FIELDS=new Set(['schema_version','project','machine','python','port','suites',
    'project_root','artifact_root','snapshot_root','scratch','receipt_root',
    'engine_path','interval_seconds','timeout_seconds']);

function local(value,label,directory=false) {
    if(typeof value!=='string'||!path.isAbsolute(value)||
        value.startsWith('\\\\')||/[\0\r\n]/.test(value)||value.length>1024)
        throw Error(label+' needs an absolute local path.');
    const result=fs.realpathSync(value),stat=fs.statSync(result);
    if(result.startsWith('\\\\'))throw Error(label+' must resolve to a local path.');
    if(directory?!stat.isDirectory():!stat.isFile())
        throw Error(label+' has the wrong file type.');
    return result;
}
function integer(value,min,max,label) {
    if(!Number.isSafeInteger(value)||value<min||value>max)
        throw Error(label+' must be an integer from '+min+' to '+max+'.');
    return value;
}
function readConfiguration(file) {
    const fd=fs.openSync(local(file,'Evaluator configuration'),'r');
    let bytes;
    try {
        const buffer=Buffer.alloc(16385);
        const count=fs.readSync(fd,buffer,0,buffer.length,0);
        if(count>16384)throw Error('Evaluator configuration exceeds 16 KiB.');
        bytes=buffer.subarray(0,count);
    }finally {fs.closeSync(fd);}
    const value=JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/,''));
    if(!value||typeof value!=='object'||Array.isArray(value)||
        Object.keys(value).some(key=>!FIELDS.has(key))||value.schema_version!==1||
        !['tinybrains','courier'].includes(value.project)||
        typeof value.machine!=='string'||!ID.test(value.machine))
        throw Error('Select a supported evaluator project, machine and schema version.');
    if(!Array.isArray(value.suites)||value.suites.length<1||value.suites.length>8)
        throw Error('Supply 1–8 suite files for one coordinated evaluation poll.');
    const suites=value.suites.map(file=>local(file,'Evaluation suite'));
    if(new Set(suites.map(file=>file.toLowerCase())).size!==suites.length)
        throw Error('Evaluation suites must be distinct.');
    const python=local(value.python,'Python executable');
    const args=['-B',local(path.join(__dirname,
        'experiment_hub_'+value.project+'_eval_poll.py'),'Evaluator entrypoint'),
        '--machine',value.machine,'--port',String(integer(value.port??7148,1,65535,'Port'))];
    for(const suite of suites)args.push('--suite',suite);
    const directories=[];
    for(const key of ['artifact_root','scratch','receipt_root',
        ...(value.project==='courier'?['project_root','snapshot_root']:[])]) {
        const directory=local(value[key],key,true);
        for(const other of directories) {
            const within=(parent,child)=>{
                const relative=path.relative(parent,child);
                return !relative||relative!=='..'&&!relative.startsWith('..'+path.sep)&&!path.isAbsolute(relative);
            };
            if(within(other,directory)||within(directory,other))
                throw Error('Project, artifact and evaluator directories must be separate.');
        }
        directories.push(directory);
        args.push('--'+key.replaceAll('_','-'),directory);
    }
    if(value.project==='tinybrains'&&value.engine_path)
        args.push('--engine-path',local(value.engine_path,'TinyBrains engine'));
    if(value.project==='courier'&&value.engine_path||
        value.project==='tinybrains'&&(value.project_root||value.snapshot_root))
        throw Error('Configuration contains paths for a different evaluator project.');
    return {project:value.project,machine:value.machine,python,args,
        interval:integer(value.interval_seconds??180,60,86400,'Interval seconds'),
        timeout:integer(value.timeout_seconds??7200,60,86400,'Timeout seconds'),
        sha256:crypto.createHash('sha256').update(bytes).digest('hex')};
}
async function verifyBudget(env=process.env) {
    if(process.platform!=='win32'||!NAME.test(env.EXPERIMENT_HUB_SERVICE_NAME||''))
        throw Error('Use the Windows service launcher evaluator role for native resource containment.');
    const executable=local(env.EXPERIMENT_HUB_SERVICE_EXECUTABLE,'Service launcher');
    const cpus=integer(Number(env.EXPERIMENT_HUB_EVALUATOR_CPUS),1,64,'Native CPU budget');
    const ram=integer(Number(env.EXPERIMENT_HUB_EVALUATOR_RAM_MB),512,1048576,'Native RAM budget');
    const result=await run(executable,['--probe-evaluator',env.EXPERIMENT_HUB_SERVICE_NAME,String(process.pid)],
        {windowsHide:true,timeout:20000,maxBuffer:8192});
    const proof=JSON.parse(result.stdout);
    if(proof.verified!==true||!Number.isSafeInteger(proof.host_logical_processors)||
        proof.host_logical_processors<cpus||proof.ram_limit_mb!==ram||
        proof.cpu_rate_units!==Math.max(1,Math.floor(10000*cpus/proof.host_logical_processors))||
        proof.application_process_tasks!==2)
        throw Error('Evaluator CPU/RAM ownership or empty process tree not verified ('+
            proof.application_process_tasks+' application processes).');
    return {...proof,cpus,ram_mb:ram};
}
function saveStatus(file,value) {
    const pending=file+'.pending';
    fs.writeFileSync(pending,JSON.stringify({schema_version:1,...value})+'\n',{mode:0o600});
    fs.renameSync(pending,file);
}
async function poll(config) {
    return await new Promise((resolve,reject)=>{
        const child=spawn(config.python,config.args,
            {cwd:__dirname,windowsHide:true,stdio:['ignore','pipe','pipe']});
        const timeout=setTimeout(()=>{
            child.kill();
            child.stdout.destroy();child.stderr.destroy();
            reject(Error('Evaluation exceeded '+config.timeout+' seconds; stopping its native process tree.'));
        },config.timeout*1000);
        child.stdout.on('data',data=>console.log(data.toString('utf8').trimEnd()));
        child.stderr.on('data',data=>console.error(data.toString('utf8').trimEnd()));
        child.once('error',error=>{clearTimeout(timeout);reject(error);});
        child.once('exit',(code,signal)=>{clearTimeout(timeout);resolve({code,signal});});
    });
}
async function main(argv=process.argv.slice(2),env=process.env) {
    const {values,positionals}=parseArgs({args:argv,options:{once:{type:'boolean'},help:{type:'boolean'}}});
    if(values.help) {
        console.log('Run through experiment_hub_windows_service with Role=evaluator, Cpus, RamMb and a private environment file.');
        console.log('Set EXPERIMENT_HUB_EVALUATOR_CONFIG to its local JSON configuration. --run-once performs one bounded poll.');
        return;
    }
    if(positionals.length)throw Error('Unexpected evaluator arguments.');
    const initial=await verifyBudget(env);
    const config=readConfiguration(env.EXPERIMENT_HUB_EVALUATOR_CONFIG);
    const root=local(env.EXPERIMENT_HUB_SERVICE_LOG_ROOT,'Service state directory',true);
    const status=path.join(root,env.EXPERIMENT_HUB_SERVICE_NAME+'.evaluation.json');
    const context={project:config.project,machine:config.machine,configuration_sha256:config.sha256,
        cpus:initial.cpus,ram_mb:initial.ram_mb};
    console.log('Independent '+config.project+' evaluation active with verified CPU/RAM limits.');
    do {
        let startedAt;
        try {
            const start=performance.now();
            startedAt=new Date().toISOString();
            const before=await verifyBudget(env);
            saveStatus(status,{...context,state:'running',started_at:startedAt});
            const result=await poll(config);
            // A finished Python process does not prove its descendants exited.
            // Never begin another poll while any native child remains active.
            const after=await verifyBudget(env);
            const record={...context,state:result.code===0?'completed':'failed',
                started_at:startedAt,finished_at:new Date().toISOString(),
                elapsed_ms:Math.round(performance.now()-start),python_exit_code:result.code,
                signal:result.signal,
                service_job_cpu_time_ns:Number.isSafeInteger(after.cpu_time_ns)&&
                    Number.isSafeInteger(before.cpu_time_ns)&&after.cpu_time_ns>=before.cpu_time_ns
                    ?after.cpu_time_ns-before.cpu_time_ns:null,
                service_job_peak_memory_bytes:after.peak_job_memory_bytes};
            saveStatus(status,record);
            if(result.code!==0)console.error('Evaluation poll failed with exit code '+result.code+'.');
            if(values.once&&result.code!==0)process.exitCode=1;
        }catch(error) {
            saveStatus(status,{...context,state:'failed',started_at:startedAt||null,
                finished_at:new Date().toISOString(),diagnostic:error.message});
            throw error;
        }
        if(values.once)return;
        await delay(config.interval*1000);
    }while(true);
}
if(require.main===module)main().catch(error=>{
    console.error('Independent evaluator stopped: '+error.message);
    // Return promptly even if an orphan still holds a pipe. The launcher
    // closes its native job and terminates this evaluator's entire tree.
    process.exit(1);
});
module.exports={main,readConfiguration,verifyBudget};
