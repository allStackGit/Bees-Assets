'use strict';

// Read-only machine-local evaluator scheduler observations. These are not
// training ownership, evaluation receipts, or authorization to run jobs.
const fs=require('node:fs');
const path=require('node:path');
const {spawnSync}=require('node:child_process');

const MACHINE=/^[a-z][a-z0-9_-]{0,47}$/;
const SERVICE=/^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$/;
const MAX_SERVICES=16;
const SUITE=/^[a-z][a-z0-9_-]{0,47}$/;
function suiteIds(value) {
    if(!Array.isArray(value)||value.length<1||value.length>8||
        value.some(id=>typeof id!=='string'||!SUITE.test(id))||
        new Set(value).size!==value.length)
        return null;
    return value.slice();
}

function command(file,args,timeout=1500) {
    const result=spawnSync(file,args,{
        shell:false,windowsHide:true,encoding:'utf8',
        timeout,maxBuffer:32768,
    });
    if(result.error||result.status!==0||typeof result.stdout!=='string')
        throw Error(file+' service status unavailable.');
    return result.stdout;
}
function instant(value) {
    const at=Date.parse(value||'');
    return Number.isFinite(at)?new Date(at).toISOString():null;
}
function cpuQuota(value) {
    if(typeof value!=='string'||value==='infinity')return null;
    const parsed=/^([0-9]+(?:\.[0-9]+)?)(us|ms|s|min)$/.exec(value);
    const seconds=parsed?Number(parsed[1])*({
        us:0.000001,ms:0.001,s:1,min:60,
    })[parsed[2]]:/^[0-9]+$/.test(value)?Number(value)/1000000:null;
    return Number.isSafeInteger(seconds)&&seconds>=1&&seconds<=64
        ?seconds:null;
}
function memoryQuota(value) {
    if(typeof value!=='string'||!/^[0-9]+$/.test(value))return null;
    const memory=Number(value)/1048576;
    return Number.isSafeInteger(memory)&&memory>=512&&memory<=1048576
        ?memory:null;
}
function linuxHealth(machine) {
    // Query the systemd user manager; unit files need not be running to
    // appear. Do not read user unit files or execute their commands.
    const list=command('systemctl',[
        '--user','list-unit-files','--type=timer','--no-legend',
        '--no-pager','bees-experiment-hub-*-evaluator*.timer',
    ]);
    const regex=new RegExp('^bees-experiment-hub-(tinybrains|courier)-'+
        machine+'-evaluator(?:-[a-z][a-z0-9_-]{0,31})?\\.timer$');
    const files=list.split(/\r?\n/).map(line=>line.trim().split(/\s+/)[0])
        .filter(name=>regex.test(name)).sort();
    if(files.length>MAX_SERVICES)
        throw Error('More than 16 evaluator timers; service health is incomplete.');
    if(!files.length)return [];
    const units=files.flatMap(timer=>[timer,timer.slice(0,-6)+'.service']);
    const shown=command('systemctl',[
        '--user','show','--no-pager',
        '--property=Id,Description,ActiveState,Result,ExecMainStatus,ExecMainExitTimestamp,CPUQuotaPerSecUSec,MemoryMax',
        ...units,
    ]);
    const details=new Map();
    for(const block of shown.trim().split(/\r?\n\r?\n/)) {
        const fields={};
        for(const line of block.split(/\r?\n/)) {
            const i=line.indexOf('=');
            if(i>0)fields[line.slice(0,i)]=line.slice(i+1);
        }
        if(fields.Id)details.set(fields.Id,fields);
    }
    // A slow journal must not block the machine agent's 15-second native
    // heartbeat. Capture at most one detailed failure per health sample;
    // every failed unit still reports its systemd result and exit code.
    let detailedJournalRemaining=1;
    return files.map(timer=>{
        const serviceName=timer.slice(0,-6)+'.service';
        const service=details.get(serviceName)||{};
        const scheduler=details.get(timer)||{};
        const exit=Number(service.ExecMainStatus);
        const finished=instant(service.ExecMainExitTimestamp);
        const failed=Boolean(service.Result&&service.Result!=='success');
        const definition=/ \[suites:([a-z0-9_,-]+)\]$/.exec(service.Description||'');
        const configuredSuites=definition?suiteIds(definition[1].split(',')):null;
        let diagnostic=failed?('systemd evaluator result: '+service.Result):null;
        if(failed&&detailedJournalRemaining-->0) {
            // The dedicated evaluator writes bounded human-readable failures
            // to its own unit's journal; no training log or protected
            // repository path is read. Treat it as a diagnostic only.
            try {
                const lines=command('journalctl',[
                    '--user','-u',serviceName,'-n','24',
                    '--no-pager','-o','cat',
                ],1000).split(/\r?\n/);
                const failure=lines.reverse().find(line=>
                    /^Scheduled (TinyBrains|Courier) evaluation failed:/.test(line));
                if(failure)diagnostic=failure;
            }catch { /* The systemd result remains useful without journal access. */ }
        }
        return {
            project:regex.exec(timer)[1],
            unit:serviceName,
            executor:'systemd-user',
            service_state:service.ActiveState==='active'||
                service.ActiveState==='activating'?'running':
                service.ActiveState==='failed'?'failed':
                service.ActiveState==='inactive'?'stopped':'unknown',
            scheduler_state:scheduler.ActiveState==='active'?'active':
                scheduler.ActiveState==='inactive'||
                scheduler.ActiveState==='failed'?'inactive':'unknown',
            last_result:failed?'failed':finished?'completed':'unknown',
            last_exit_code:Number.isSafeInteger(exit)&&exit>=0&&exit<=255?exit:null,
            last_finished_at:finished,
            evaluator_cpus:cpuQuota(service.CPUQuotaPerSecUSec),
            evaluator_ram_mb:memoryQuota(service.MemoryMax),
            suite_ids:configuredSuites,
            diagnostic:diagnostic?diagnostic.replace(/[\x00-\x1f]/g,' ').slice(0,160):null,
        };
    });
}
function windowsHealth(machine,env) {
    // Windows evaluator services already publish a private, atomic status
    // file. The SCM provides independent evidence that each service is
    // presently running; an old "running" status file alone does not.
    const supplied=env.EXPERIMENT_HUB_EVALUATOR_STATUS_ROOT;
    if(!supplied)return [];
    if(!path.isAbsolute(supplied)||supplied.startsWith('\\\\'))
        throw Error('Evaluator status root must be an absolute local directory.');
    if(fs.lstatSync(supplied).isSymbolicLink())
        throw Error('Evaluator status root must not be redirected.');
    const root=fs.realpathSync(supplied);
    const stat=fs.lstatSync(root);
    if(!stat.isDirectory()||stat.isSymbolicLink()||root.startsWith('\\\\'))
        throw Error('Evaluator status root must be a local regular directory.');
    const files=fs.readdirSync(root).filter(name=>
        name.endsWith('.evaluation.json')&&
        SERVICE.test(name.slice(0,-'.evaluation.json'.length))).sort();
    if(files.length>MAX_SERVICES)
        throw Error('More than 16 Windows evaluator status files; reporting is incomplete.');
    const services=[];
    // Query SCM state with a machine-wide deadline. Beyond it we retain
    // useful persisted status files but report unknown live ownership.
    // Sixteen wedged SCM queries must not prevent native Stop/Inspect polls.
    const scmDeadline=Date.now()+3000;
    for(const name of files) {
        const serviceName=name.slice(0,-'.evaluation.json'.length);
        const file=path.join(root,name),entry=fs.lstatSync(file);
        if(!entry.isFile()||entry.isSymbolicLink()||entry.nlink!==1||
            entry.size>8192||entry.size<1)
            throw Error('Windows evaluator status file is unsafe or oversized.');
        const record=JSON.parse(fs.readFileSync(file,'utf8'));
        if(record.schema_version!==1||record.machine!==machine||
            !['tinybrains','courier'].includes(record.project)||
            !['running','completed','failed'].includes(record.state))
            continue;
        let running=null;
        const remaining=scmDeadline-Date.now();
        if(remaining>100)try {
            const output=command('sc.exe',['query',serviceName],
                Math.min(750,remaining));
            const match=/STATE\s*:\s*([0-9]+)/i.exec(output);
            running=match?Number(match[1])===4:null;
        }catch { /* Missing SCM proof must not imply a running evaluator. */ }
        const completed=instant(record.finished_at);
        const diagnostic=typeof record.diagnostic==='string'
            ?record.diagnostic.slice(0,160):null;
        services.push({
            project:record.project,unit:serviceName,executor:'windows-service',
            service_state:running===true?'running':
                running===false?'stopped':'unknown',
            scheduler_state:running===true?'active':
                running===false?'inactive':'unknown',
            last_result:record.state==='failed'?'failed':
                record.state==='completed'?'completed':'unknown',
            last_exit_code:Number.isSafeInteger(record.python_exit_code)&&
                record.python_exit_code>=0&&record.python_exit_code<=255
                ?record.python_exit_code:null,
            last_finished_at:completed,
            evaluator_cpus:Number.isSafeInteger(record.cpus)&&record.cpus>=1&&record.cpus<=64
                ?record.cpus:null,
            evaluator_ram_mb:Number.isSafeInteger(record.ram_mb)&&
                record.ram_mb>=512&&record.ram_mb<=1048576?record.ram_mb:null,
            suite_ids:suiteIds(record.suite_ids),
            diagnostic,
        });
    }
    return services;
}
function evaluatorHealth(machine,env=process.env,platform=process.platform) {
    const checked=new Date().toISOString();
    const report=(services,issue)=>({evaluator_services:services,
        evaluator_service_issue:issue,evaluator_service_checked_at:checked});
    if(!MACHINE.test(machine))
        return report([],'Invalid evaluator machine identity.');
    try {
        if(platform==='linux')return report(linuxHealth(machine),null);
        if(platform==='win32')return env.EXPERIMENT_HUB_EVALUATOR_STATUS_ROOT
            ?report(windowsHealth(machine,env),null)
            :report([],'Set EXPERIMENT_HUB_EVALUATOR_STATUS_ROOT to the evaluator private service-state directory.');
        return report([],'Evaluator service status unsupported on this platform.');
    }catch(error) {
        return report([],String(error.message||error).slice(0,160));
    }
}
module.exports={evaluatorHealth};
