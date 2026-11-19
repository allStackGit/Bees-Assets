'use strict';

// Local administrator control for the approved Halifax TinyBrains evaluators.
// The browser can choose only fixed Nano/Micro actions. It cannot provide
// unit names, executables, host mounts, Docker arguments or suite definitions.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {evaluatorDefinition,installEvaluatorUnits}=require('./experiment_hub_systemd');

const run=promisify(execFile);
const SYSTEMCTL='/usr/bin/systemctl';
const SUITES=Object.freeze({
    nano:'tinybrains_nano_tiny_v3',
    micro:'tinybrains_micro_tiny_v3',
});
function suiteNames(options) {
    const marker=path.join(options.suites,'hub-approved-latest.json');
    if(!fs.existsSync(marker))return SUITES;
    const stat=fs.lstatSync(marker);
    if(!stat.isFile()||stat.isSymbolicLink()||
        fs.realpathSync(marker)!==marker||
        stat.uid!==process.getuid()||stat.mode&0o077||stat.size>1024)
        throw Error('TinyBrains suite approval must be a private regular file.');
    const approved=JSON.parse(fs.readFileSync(marker,'utf8'));
    if(!approved||approved.version!==1||
        Object.keys(approved).sort().join(',')!=='micro,nano,version'||
        ['nano','micro'].some(id=>
            typeof approved[id]!=='string'||
            !new RegExp('^tinybrains_'+id+'_tiny_v[3-9][0-9]*$').test(approved[id])))
        throw Error('Invalid approved TinyBrains suite version map.');
    return {nano:approved.nano,micro:approved.micro};
}
const SAFE_PATH=/^\/[A-Za-z0-9._/@:+-]+$/;
const MACHINE=/^[a-z][a-z0-9_-]{0,47}$/;

function checkedPath(file) {
    if(typeof file!=='string'||!SAFE_PATH.test(file))
        throw Error('Evaluator paths must be safe absolute local paths.');
    return file;
}
function presentFile(file) {
    checkedPath(file);
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink()||fs.realpathSync(file)!==file)
        throw Error('Expected a regular approved evaluator file: '+file);
    return file;
}
function presentDirectory(dir) {
    checkedPath(dir);
    const stat=fs.lstatSync(dir);
    if(!stat.isDirectory()||stat.isSymbolicLink()||fs.realpathSync(dir)!==dir)
        throw Error('Expected an approved evaluator directory: '+dir);
    return dir;
}
function privateDirectory(dir,create=false) {
    checkedPath(dir);
    if(create)fs.mkdirSync(dir,{recursive:true,mode:0o700});
    let stat=fs.lstatSync(dir);
    if(create&&stat.isDirectory()&&!stat.isSymbolicLink()&&
        stat.uid===process.getuid()&&(stat.mode&0o077)!==0) {
        // Existing user-owned evaluation scratch folders may predate the Hub
        // UI. Tighten permissions rather than requiring a terminal command.
        fs.chmodSync(dir,0o700);
        stat=fs.lstatSync(dir);
    }
    if(!stat.isDirectory()||stat.isSymbolicLink()||
        stat.uid!==process.getuid()||(stat.mode&0o077)!==0)
        throw Error('Evaluator writable directories must be private user-owned directories: '+dir);
    return dir;
}
function localOptions(hub) {
    if(process.platform!=='linux')
        throw Error('Local evaluator control requires Linux user systemd.');
    const home=checkedPath(os.homedir());
    const agentEnv=presentFile(path.join(home,'.config/experiment-hub/agent.env'));
    const stat=fs.statSync(agentEnv);
    if(stat.uid!==process.getuid()||(stat.mode&0o077)!==0)
        throw Error('Local machine enrollment file must be private and user-owned.');
    const lines=fs.readFileSync(agentEnv,'utf8').split(/\r?\n/);
    const machineValue=lines.filter(line=>line.startsWith('EXPERIMENT_HUB_MACHINE_ID='));
    if(machineValue.length!==1)throw Error('Expected one local enrolled machine identity.');
    const machine=machineValue[0].slice('EXPERIMENT_HUB_MACHINE_ID='.length)
        .trim().replace(/^['"]|['"]$/g,'');
    if(!MACHINE.test(machine))
        throw Error('Invalid enrolled evaluator machine identity.');
    const host=hub.snapshot().machines.find(entry=>entry.id===machine);
    if(!host||!host.installed_projects?.includes('tinybrains'))
        throw Error('The local enrolled machine does not advertise TinyBrains.');

    const ai=path.join(home,'ai');
    const project=path.join(ai,'projects/training-efficiency/TinyBrains-latest/Ants-MAPOCA');
    const hubRoot=path.resolve(__dirname,'..');
    const suites=path.join(home,'evaluation-audits/tinybrains/suites');
    return {
        home,ai,machine,project,hubRoot,suites,
        engine:path.join(project,'tools/tinybrains-0.5.0/tinybrains'),
        cache:path.join(ai,'experiments/tinybrains-cache'),
        artifacts:path.join(ai,'experiments/experiment-hub/tinybrains-jobs'),
        scratch:path.join(home,'evaluation-audits/tinybrains/live-evaluation-scratch'),
        receipts:path.join(ai,'experiments/experiment-hub/evaluator-receipts'),
        bin:path.join(home,'.local/share/experiment-hub'),
        launcher:path.join(home,'.local/share/experiment-hub/tinybrains-evaluator-python'),
        unitDir:path.join(home,'.config/systemd/user'),
    };
}
function suiteFiles(options) {
    return Object.fromEntries(Object.entries(suiteNames(options)).map(([id,name])=>{
        const file=presentFile(path.join(options.suites,name+'.json'));
        const stat=fs.statSync(file);
        if(stat.size<1||stat.size>8192)
            throw Error('Frozen evaluator suite has an invalid size: '+name);
        const definition=JSON.parse(fs.readFileSync(file,'utf8'));
        if(definition.suite_id!==name)
            throw Error('Frozen suite ID differs from its approved filename: '+name);
        return [id,file];
    }));
}
function validateInstall(options,{create=false}={}) {
    presentDirectory(options.project);
    presentDirectory(options.hubRoot);
    presentFile(options.engine);
    presentDirectory(options.artifacts);
    presentDirectory(options.cache);
    presentDirectory(options.suites);
    const suites=suiteFiles(options);
    privateDirectory(options.scratch,create);
    privateDirectory(options.receipts,create);
    if(create)privateDirectory(options.bin,true);
    return suites;
}
function launcherText(o) {
    // Only fixed, server-derived paths are interpolated. No request-controlled
    // Docker arguments, writable source mounts or shell commands.
    for(const value of [o.home,o.project,o.hubRoot,o.artifacts,o.cache,
        o.suites,o.scratch,o.receipts])checkedPath(value);
    const bind=(dir,mode)=>'-v '+dir+':'+dir+':'+mode;
    return [
        '#!/bin/sh',
        'set -eu',
        'exec /usr/bin/sudo -n /usr/bin/docker run --rm --pull=never --network host \\',
        '  --read-only --cap-drop ALL --security-opt no-new-privileges \\',
        '  --tmpfs /tmp:rw,nosuid,nodev,size=1g \\',
        '  --cpus 2 --memory 4g --user '+process.getuid()+':'+process.getgid()+' \\',
        '  '+bind(o.project,'ro')+' \\',
        '  '+bind(o.hubRoot,'ro')+' \\',
        '  '+bind(o.artifacts,'ro')+' \\',
        '  '+bind(o.cache,'rw')+' \\',
        '  '+bind(o.suites,'ro')+' \\',
        '  '+bind(o.scratch,'rw')+' \\',
        '  '+bind(o.receipts,'rw')+' \\',
        '  -w '+o.hubRoot+'/Training \\',
        '  -e HOME=/tmp -e PYTHONDONTWRITEBYTECODE=1 \\',
        '  -e TINYBRAINS_SOURCE_ROOT=B:/TinyBrains/Ants-MAPOCA \\',
        '  -e TINYBRAINS_HOME='+o.cache+' \\',
        '  -e TINYBRAINS_REGISTRY='+o.project+'/games.toml \\',
        '  -e TINYBRAINS='+o.engine+' \\',
        '  -e PYTHONPATH='+o.project+'/src:'+o.project+
            '/upstream/ants/baselines/src:'+o.hubRoot+'/Training \\',
        '  --entrypoint /opt/tinybrains-venv/bin/python \\',
        '  tinybrains-rocm:halifax-node "$@"',
        '',
    ].join('\n');
}
function definitions(options) {
    const files=validateInstall(options);
    return Object.fromEntries(Object.entries(files).map(([id,suite])=>[
        id,evaluatorDefinition({
            project:'tinybrains',machine:options.machine,
            trainingDir:path.join(options.hubRoot,'Training'),
            pythonPath:presentFile(options.launcher),
            suites:[suite],artifactRoot:options.artifacts,
            scratch:options.scratch,receiptRoot:options.receipts,
            enginePath:options.engine,cpus:'2',ramMb:'4096',
            interval:'180',timeout:'7200',unitId:id,
        }),
    ]));
}
function unitFiles(options,id) {
    const name='bees-experiment-hub-tinybrains-'+options.machine+
        '-evaluator-'+id;
    return {
        service:name+'.service',timer:name+'.timer',
        serviceFile:path.join(options.unitDir,name+'.service'),
        timerFile:path.join(options.unitDir,name+'.timer'),
    };
}
function approvedUnit(options,id,definition) {
    const names=unitFiles(options,id);
    for(const [file,expected] of [
        [names.serviceFile,definition.service],
        [names.timerFile,definition.timer],
    ]) {
        const stat=fs.lstatSync(file);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||
            fs.readFileSync(file,'utf8')!==expected)
            throw Error('Existing evaluator systemd unit differs from its approved definition: '+file);
    }
    return names;
}
async function systemctl(args) {
    const result=await run(SYSTEMCTL,['--user',...args],{
        timeout:15000,maxBuffer:32768,
    });
    return result.stdout;
}
async function properties(unit) {
    const output=await systemctl(['show',unit,
        '--property=LoadState,ActiveState,Result,ExecMainStatus,ExecMainExitTimestamp']);
    return Object.fromEntries(output.trim().split(/\r?\n/)
        .map(line=>[line.slice(0,line.indexOf('=')),line.slice(line.indexOf('=')+1)]));
}
async function status(hub) {
    let options;
    try { options=localOptions(hub); }
    catch(error) {
        return {available:false,reason:String(error.message||error),entries:[]};
    }
    const entries=[];
    for(const id of Object.keys(SUITES)) {
        const names=unitFiles(options,id);
        const installed=fs.existsSync(names.serviceFile)&&fs.existsSync(names.timerFile);
        let managed=false,reason=null,active=false,running=false,lastResult=null,lastError=null;
        if(installed) {
            try {
                const defs=definitions(options);
                approvedUnit(options,id,defs[id]);
                const [timer,service]=await Promise.all([
                    properties(names.timer),properties(names.service),
                ]);
                managed=timer.LoadState==='loaded'&&service.LoadState==='loaded';
                active=timer.ActiveState==='active';
                running=service.ActiveState==='activating'||service.ActiveState==='active';
                lastResult=service.Result||null;
                if(lastResult&&lastResult!=='success') {
                    try {
                        const {stdout}=await run('/usr/bin/journalctl',[
                            '--user','-u',names.service,'-n','24','--no-pager','-o','cat',
                        ],{timeout:3000,maxBuffer:16384});
                        lastError=stdout.split(/\r?\n/).reverse().find(line=>
                            /^Scheduled TinyBrains evaluation failed:/.test(line))||null;
                        if(lastError)lastError=lastError.slice(0,220);
                    }catch { /* Leave the systemd failure status visible. */ }
                }
                if(!managed)reason='Installed evaluator requires systemd reload.';
            }catch(error){reason=String(error.message||error);}
        }
        entries.push({id,suite_id:suiteNames(options)[id],installed,managed,
            active,running,last_result:lastResult,last_error:lastError,reason});
    }
    return {available:true,machine:options.machine,entries};
}
async function setup(hub) {
    const o=localOptions(hub);
    validateInstall(o,{create:true});
    const expected=launcherText(o);
    if(fs.existsSync(o.launcher)) {
        const stat=fs.lstatSync(o.launcher);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||
            fs.readFileSync(o.launcher,'utf8')!==expected)
            throw Error('An existing TinyBrains evaluator launcher is not the approved version.');
    }else {
        fs.writeFileSync(o.launcher,expected,{flag:'wx',mode:0o700});
    }
    fs.chmodSync(o.launcher,0o700);
    const defs=definitions(o);
    // Never overwrite an installed service, even one sharing our fixed name.
    for(const [id,definition] of Object.entries(defs)) {
        const unit=unitFiles(o,id);
        if(fs.existsSync(unit.serviceFile)||fs.existsSync(unit.timerFile))
            approvedUnit(o,id,definition);
        else installEvaluatorUnits(definition,o.home);
    }
    await systemctl(['daemon-reload']);
    for(const id of Object.keys(SUITES))
        await systemctl(['start',unitFiles(o,id).timer]);
    return {message:'Approved Nano and Micro evaluation timers installed and started.'};
}
async function action(hub,value) {
    if(!value||value.confirm_action!==true)
        throw Error('Explicit evaluator action confirmation is required.');
    if(value.action==='setup'&&value.id===undefined)
        return setup(hub);
    if(!Object.hasOwn(SUITES,value.id)||
        !['start','restart','stop','run'].includes(value.action))
        throw Error('Unknown approved local evaluator action.');
    const o=localOptions(hub);
    const defs=definitions(o);
    const unit=approvedUnit(o,value.id,defs[value.id]);
    if(value.action==='run') {
        const current=await properties(unit.service);
        if(current.ActiveState==='active'||current.ActiveState==='activating')
            throw Error('Evaluation already running; it will not be interrupted.');
        await systemctl(['start','--no-block',unit.service]);
        return {message:'Independent '+value.id+' evaluation requested immediately.'};
    }
    await systemctl([value.action,unit.timer]);
    return {message:value.id+' evaluator '+(value.action==='stop'
        ?'schedule stopped; any current evaluation may finish.'
        :value.action==='restart'?'schedule restarted.':'schedule started.')};
}
module.exports={status,action};
