'use strict';

// Local administrator control for the approved Halifax TinyBrains evaluators.
// The browser can approve only trusted Nano/Micro or a finished Batch-128
// frozen Mini source. It cannot supply paths, executables or suite definitions.
const fs=require('node:fs');
const crypto=require('node:crypto');
const os=require('node:os');
const path=require('node:path');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {evaluatorDefinition,installEvaluatorUnits}=require('./experiment_hub_systemd');
const custom=require('./experiment_hub_tinybrains_eval_library');

const run=promisify(execFile);
const SYSTEMCTL='/usr/bin/systemctl';
const SUITES=Object.freeze({
    nano:'tinybrains_nano_tiny_v3',
    micro:'tinybrains_micro_tiny_v3',
});
// Single-suite timers serve independent cadences; the paired timer serves
// same-checkpoint Nano+Micro monitoring/stop policies.
const BATCH_ID='nano-micro';
const MINI_ID='mini';
const MINI_SUITE='tinybrains_mini_batch128_v1';
const SCHEDULED_IDS=[...Object.keys(SUITES),BATCH_ID];
const HISTORICAL_SUITE=/^tinybrains_mini_([a-f0-9]{20})$/;
function historicalSuites(options) {
    if(!fs.existsSync(options.suites))return {};
    const names=fs.readdirSync(options.suites)
        .filter(file=>file.endsWith('.json')&&
            HISTORICAL_SUITE.test(file.slice(0,-5)));
    if(names.length>32)
        throw Error('The approved TinyBrains historical suite population exceeds 32.');
    return Object.fromEntries(names.map(file=>{
        const name=file.slice(0,-5);
        return ['history-'+HISTORICAL_SUITE.exec(name)[1],name];
    }));
}
// Full-season suites preserve the fast two-seat benchmarks. All 76 enabled
// season boards are evaluated with each board's actual 2/4/6 player count.
const SEASON_SUITES=Object.freeze({
    'nano-season':'tinybrains_nano_season_v1',
    'micro-season':'tinybrains_micro_season_v1',
    'mini-season':'tinybrains_mini_batch128_season_v1',
});
const UUID=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const SHA256=/^[a-f0-9]{64}$/;
function suiteNames(options) {
    const marker=path.join(options.suites,'hub-approved-latest.json');
    if(!fs.existsSync(marker))return {
        ...SUITES,
        ...(fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'))
            ?{mini:MINI_SUITE}:{}),
        ...Object.fromEntries(Object.entries(SEASON_SUITES).filter(([,name])=>
            fs.existsSync(path.join(options.suites,name+'.json')))),
        ...historicalSuites(options),...custom.customSuites(options),
    };
    const stat=fs.lstatSync(marker);
    if(!stat.isFile()||stat.isSymbolicLink()||
        fs.realpathSync(marker)!==marker||
        stat.uid!==process.getuid()||stat.mode&0o077||stat.size>1024)
        throw Error('TinyBrains suite approval must be a private regular file.');
    const approved=JSON.parse(fs.readFileSync(marker,'utf8'));
    const keys=approved&&Object.keys(approved).sort().join(',');
    // A prior Mini installation accidentally persisted "mini" in this
    // Nano/Micro-only marker. Accept that exact historic shape only when
    // its immutable suite exists; never trust arbitrary marker additions.
    const legacyMini=keys==='micro,mini,nano,version'&&
        approved.mini===MINI_SUITE&&
        fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'));
    if(!approved||approved.version!==1||
        (keys!=='micro,nano,version'&&!legacyMini)||
        ['nano','micro'].some(id=>
            typeof approved[id]!=='string'||
            !new RegExp('^tinybrains_'+id+'_tiny_v(?:[3-9]|[1-9][0-9]{1,2})$').test(approved[id])))
        throw Error('Invalid approved TinyBrains suite version map.');
    return {
        nano:approved.nano,micro:approved.micro,
        ...(fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'))
            ?{mini:MINI_SUITE}:{}),
        ...Object.fromEntries(Object.entries(SEASON_SUITES).filter(([,name])=>
            fs.existsSync(path.join(options.suites,name+'.json')))),
        ...historicalSuites(options),...custom.customSuites(options),
    };
}
function scheduledIds(options) {
    return [...SCHEDULED_IDS,
        ...(fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'))?[MINI_ID]:[]),
        'nano-season','micro-season',
        ...(fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'))
            ?['mini-season']:[]),
        ...Object.keys(historicalSuites(options)),
        ...Object.keys(custom.customSuites(options))];
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
function localOptions(hub,{observationOnly=false}={}) {
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
    if(!host)
        throw Error('The enrolled local agent identity is not registered in the Hub.');
    if(!observationOnly&&host.status!=='connected')
        throw Error('Waiting for a fresh authenticated local agent heartbeat; evaluator readiness will refresh automatically.');
    if(!observationOnly&&!host.installed_projects?.includes('tinybrains')) {
        const projects=Array.isArray(host.installed_projects)
            ?host.installed_projects.join(', '):'';
        throw Error(host.local_executor_enabled!==true
            ?'The connected local agent is observation-only. Check its private agent.env execution setting and approved adapter.'
            :'The local agent advertises '+(projects||'no project adapters')+
                ', not TinyBrains. Check its approved EXPERIMENT_HUB_LOCAL_ADAPTERS file.');
    }

    const ai=path.join(home,'ai');
    const project=path.join(ai,'projects/tinybrains/Ants-MAPOCA');
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
        if(stat.size<1||stat.size>(id.startsWith('mix-')?32768:8192))
            throw Error('Frozen evaluator suite has an invalid size: '+name);
        const definition=JSON.parse(fs.readFileSync(file,'utf8'));
        if(definition.suite_id!==name)
            throw Error('Frozen suite ID differs from its approved filename: '+name);
        return [id,file];
    }));
}
function validateInstall(options) {
    presentDirectory(options.project);
    presentDirectory(options.hubRoot);
    presentFile(options.engine);
    presentDirectory(options.artifacts);
    presentDirectory(options.cache);
    presentDirectory(options.suites);
    const suites=suiteFiles(options);
    privateDirectory(options.scratch);
    privateDirectory(options.receipts);
    return suites;
}
// Setup owns fixed directories and never downloads or changes game assets.
function prepareInstall(o) {
    presentDirectory(o.project);
    presentDirectory(o.hubRoot);
    presentFile(o.engine);
    for(const dir of [o.artifacts,o.cache]) {
        checkedPath(dir);
        fs.mkdirSync(dir,{recursive:true,mode:0o700});
        presentDirectory(dir);
    }
    for(const dir of [o.suites,o.scratch,o.receipts,o.bin])
        privateDirectory(dir,true);
}
function seasonBoards(o) {
    const boards=presentDirectory(path.join(o.project,'maps/hive-minds/boards'));
    const filenames=fs.readdirSync(boards).filter(name=>name.endsWith('.json')).sort();
    if(filenames.length!==76)
        throw Error('The pinned Hive Minds season must contain exactly 76 approved board files.');
    const ids=[],counts={2:0,4:0,6:0};
    for(const name of filenames) {
        const id=name.slice(0,-5);
        if(!/^[a-z0-9_-]{1,80}$/.test(id))
            throw Error('Invalid trusted TinyBrains season board filename.');
        const file=presentFile(path.join(boards,name));
        const stat=fs.statSync(file);
        if(stat.size<1||stat.size>1048576)
            throw Error('Trusted TinyBrains season board exceeds its size bound.');
        const board=JSON.parse(fs.readFileSync(file,'utf8'));
        if(board.id!==id||![2,4,6].includes(board.players))
            throw Error('Trusted TinyBrains season board has invalid identity or player count: '+id);
        counts[board.players]++;
        ids.push(id);
    }
    if(counts[2]!==30||counts[4]!==34||counts[6]!==12)
        throw Error('The trusted season map distribution has changed; an explicitly versioned suite is required.');
    return ids;
}
async function createSeasonSuites(o) {
    const missing=Object.entries(SEASON_SUITES).filter(([id,name])=>
        (id!=='mini-season'||fs.existsSync(path.join(o.suites,MINI_SUITE+'.json')))&&
        !fs.existsSync(path.join(o.suites,name+'.json')));
    if(!missing.length)return;
    const ids=seasonBoards(o);
    const {stdout}=await run(presentFile(o.launcher),[
        '-B',path.join(o.hubRoot,'Training/experiment_hub_evaluation_runtime.py'),
        '--project','tinybrains','--engine-path',o.engine,
    ],{timeout:90000,maxBuffer:1024*1024});
    const digest=JSON.parse(stdout).runtime_sha256;
    if(!SHA256.test(String(digest||'')))
        throw Error('The TinyBrains evaluation runtime fingerprint is missing.');
    const names=suiteNames(o);
    for(const [id,name] of missing) {
        const opponent=id.split('-')[0];
        const base=JSON.parse(fs.readFileSync(
            presentFile(path.join(o.suites,
                (opponent==='mini'?MINI_SUITE:names[opponent])+'.json')),'utf8'));
        if(base.opponent!==opponent||
            !SHA256.test(String(base.opponent_model_sha256||''))||
            !SHA256.test(String(base.opponent_manifest_sha256||'')))
            throw Error('Season suite requires a verified frozen baseline opponent.');
        const definition={
            suite_id:name,maps:ids,seeds:[813101],max_turns:1000,
            opponent,
            opponent_model_sha256:base.opponent_model_sha256,
            opponent_manifest_sha256:base.opponent_manifest_sha256,
            runtime_sha256:digest,
            ...(opponent==='mini'?{
                opponent_dir:base.opponent_dir,
                opponent_source_job:base.opponent_source_job,
                opponent_checkpoint_sha256:base.opponent_checkpoint_sha256,
            }:{}),
        };
        fs.writeFileSync(path.join(o.suites,name+'.json'),
            JSON.stringify(definition,null,2)+'\n',{flag:'wx',mode:0o600});
    }
}
async function createMissingBaselineSuites(o) {
    // A previously approved version is immutable. Do not regenerate a
    // missing approved suite against potentially changed opponents.
    if(fs.existsSync(path.join(o.suites,'hub-approved-latest.json')))
        return;
    if(Object.values(SUITES).every(name=>
        fs.existsSync(path.join(o.suites,name+'.json'))))
        return;
    // Generate frozen suites via the existing trusted recipe. Docker's suite
    // mount stays read-only; only the local Hub exclusively writes definitions.
    const {stdout}=await run(presentFile(o.launcher),[
        '-B',path.join(o.hubRoot,'Training/experiment_hub_tinybrains_baseline_suites.py'),
        '--suite-version','3','--stdout-json',
    ],{timeout:90000,maxBuffer:16384});
    const generated=JSON.parse(stdout);
    if(!generated||!Array.isArray(generated.suites)||
        generated.suites.length!==2||
        new Set(generated.suites.map(s=>s?.suite_id)).size!==2)
        throw Error('TinyBrains generator did not return the two approved suites.');
    for(const [id,name] of Object.entries(SUITES)) {
        const suite=generated.suites.find(s=>s?.suite_id===name);
        if(!suite||suite.opponent!==id||
            suite.maps?.length!==1||suite.maps[0]!=='basic-tiny-2p'||
            suite.seeds?.length!==50||suite.seeds[0]!==813001||
            suite.seeds[49]!==813050||suite.max_turns!==300||
            [suite.runtime_sha256,suite.opponent_model_sha256,
                suite.opponent_manifest_sha256].some(hash=>
                    typeof hash!=='string'||!/^[0-9a-f]{64}$/.test(hash)))
            throw Error('TinyBrains generator differs from the fixed approved evaluation recipe.');
    }
    for(const suite of generated.suites) {
        const file=path.join(o.suites,suite.suite_id+'.json');
        if(!fs.existsSync(file))
            fs.writeFileSync(file,JSON.stringify(suite,null,2)+'\n',
                {flag:'wx',mode:0o600});
    }
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
function definitions(options,overrides={}) {
    const files={...validateInstall(options),...overrides};
    const schedules=[...Object.entries(files).map(([id,file])=>[id,[file]]),
        [BATCH_ID,[files.nano,files.micro]]];
    return Object.fromEntries(schedules.map(([id,suites])=>[
        id,evaluatorDefinition({
            project:'tinybrains',machine:options.machine,
            trainingDir:path.join(options.hubRoot,'Training'),
            pythonPath:presentFile(options.launcher),
            suites,artifactRoot:options.artifacts,
            scratch:options.scratch,receiptRoot:options.receipts,
            enginePath:options.engine,cpus:'2',ramMb:'4096',
            // Season-wide sweeps are expensive; keep their cadence separate
            // from short basic-map evaluations, with a bounded six-hour run.
            // The sweep contains 268 sequential games, each individually
            // bounded to 180 seconds. The timer waits until the service
            // finishes; permit the full bounded sweep instead of truncating
            // it at the next six-hour cadence.
            interval:(Object.hasOwn(SEASON_SUITES,id)||id.startsWith('history-')||id.startsWith('mix-'))
                ?'21600':'180',
            timeout:(Object.hasOwn(SEASON_SUITES,id)||id.startsWith('history-')||id.startsWith('mix-'))
                ?'57600':'7200',
            unitId:id,
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
function expensiveEvaluation(id) {
    return Object.hasOwn(SEASON_SUITES,id)||id.startsWith('history-')||id.startsWith('mix-');
}
function evaluationCapacity(hub,options) {
    const snapshot=hub.snapshot();
    const machine=snapshot.machines.find(item=>item.id===options.machine);
    if(!machine||machine.status!=='connected'||
        !Number.isSafeInteger(machine.cpus)||!Number.isSafeInteger(machine.ram_mb))
        return {ready:false,reason:'A fresh authenticated capacity report is required.'};
    const leases=(snapshot.reservations||[]).filter(lease=>
        lease.machine_id===options.machine&&
        ['active','offered','acknowledged'].includes(lease.status));
    if(leases.some(lease=>!Number.isSafeInteger(lease.resources?.cpus)||
        !Number.isSafeInteger(lease.resources?.ram_mb)))
        return {ready:false,reason:'Unknown active Hub CPU/RAM reservations.'};
    const running=(machine.evaluator_services||[]).filter(service=>
        service.service_state==='running');
    // Independent evaluators are outside Hub job leases. Include their
    // authenticated running-service quotas as additional committed capacity.
    if(running.some(service=>!Number.isSafeInteger(service.evaluator_cpus)||
        !Number.isSafeInteger(service.evaluator_ram_mb)))
        return {ready:false,reason:'Unknown independent evaluator resource usage.'};
    const cpuReserved=leases.reduce((n,lease)=>n+lease.resources.cpus,0)+
        running.reduce((n,service)=>n+service.evaluator_cpus,0);
    const ramReserved=leases.reduce((n,lease)=>n+lease.resources.ram_mb,0)+
        running.reduce((n,service)=>n+service.evaluator_ram_mb,0);
    const cpus=machine.cpus-cpuReserved,ramMb=machine.ram_mb-ramReserved;
    const available=machine.available_ram_mb;
    if(cpus<2||ramMb<4096||
        (Number.isSafeInteger(available)&&available<5120))
        return {ready:false,cpus,ram_mb:ramMb,
            reason:'At least two unreserved CPUs and 4 GiB RAM (plus 1 GiB live headroom) are required before starting a full-season evaluator.'};
    return {ready:true,cpus,ram_mb:ramMb,reason:null};
}
async function status(hub) {
    let options;
    try { options=localOptions(hub,{observationOnly:true}); }
    catch(error) {
        return {available:false,reason:String(error.message||error),entries:[]};
    }
    const ids=scheduledIds(options),entries=[];
    // One managed batch of systemd observations is enough for all evaluators.
    // Status UI polling must not launch two processes per archived opponent.
    const installed=ids.map(id=>({id,...unitFiles(options,id)}))
        .filter(item=>fs.existsSync(item.serviceFile)&&fs.existsSync(item.timerFile));
    let propertiesByUnit=new Map(),statusError=null;
    if(installed.length) {
        try {
            const units=installed.flatMap(item=>[item.timer,item.service]);
            const result=await systemctl(['show','--no-pager',
                '--property=Id,LoadState,ActiveState,Result,ExecMainStatus,ExecMainExitTimestamp',
                ...units]);
            for(const block of result.trim().split(/\r?\n\r?\n/)) {
                const fields=Object.fromEntries(block.split(/\r?\n/).filter(line=>
                    line.includes('=')).map(line=>
                    [line.slice(0,line.indexOf('=')),line.slice(line.indexOf('=')+1)]));
                if(fields.Id)propertiesByUnit.set(fields.Id,fields);
            }
        }catch(error){statusError=error;}
    }
    let approvedDefinitions=null,definitionError=null;
    if(installed.length)try {approvedDefinitions=definitions(options);}
    catch(error){definitionError=error;}
    for(const id of ids) {
        const names=unitFiles(options,id);
        const serviceExists=fs.existsSync(names.serviceFile);
        const timerExists=fs.existsSync(names.timerFile);
        const installed=serviceExists&&timerExists;
        let managed=false,reason=serviceExists!==timerExists
            ?'Incomplete installation; use Set up to finish the approved unit pair.':null;
        let active=false,running=false,lastResult=null,lastError=null;
        if(installed) {
            try {
                if(definitionError)throw definitionError;
                if(statusError)throw statusError;
                approvedUnit(options,id,approvedDefinitions[id]);
                const timer=propertiesByUnit.get(names.timer);
                const service=propertiesByUnit.get(names.service);
                if(!timer||!service)throw Error('Evaluator systemd status is incomplete.');
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
        entries.push({id,suite_id:id===BATCH_ID?null:suiteNames(options)[id],installed,managed,
            active,running,last_result:lastResult,last_error:lastError,reason});
    }
    // Saved-library diagnostics are independent of scheduler health. A
    // damaged optional catalog cannot hide otherwise healthy evaluators.
    let library=null,library_issue=null;
    try {library=custom.library(options,hub);}
    catch(error){library_issue=String(error.message||error).slice(0,180);}
    const localMachine=hub.snapshot().machines.find(m=>m.id===options.machine);
    const ready=localMachine?.status==='connected'&&
        localMachine?.installed_projects?.includes('tinybrains')===true;
    return {available:ready,machine:options.machine,entries,
        ...(!ready?{reason:'Local evaluator definitions are readable, but the native agent is not ready.'}:{}),
        evaluation_capacity:evaluationCapacity(hub,options),
        mini_candidates:miniCandidates(hub,options).map(({id,updates})=>({id,updates})),
        historical_candidates:historicalCandidates(hub,options)
            .map(({id,updates,batch_size})=>({id,updates,batch_size})),
        custom_library:library,custom_library_issue:library_issue};
}
async function setup(hub) {
    const o=localOptions(hub);
    prepareInstall(o);
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
    await createMissingBaselineSuites(o);
    await createSeasonSuites(o);
    const defs=definitions(o);
    // Never overwrite an installed service, even one sharing our fixed name.
    for(const [id,definition] of Object.entries(defs)) {
        const unit=unitFiles(o,id);
        if(fs.existsSync(unit.serviceFile)&&fs.existsSync(unit.timerFile))
            approvedUnit(o,id,definition);
        else installEvaluatorUnits(definition,o.home,{repairPartial:true});
    }
    await systemctl(['daemon-reload']);
    for(const id of scheduledIds(o)) {
        // Full-season sweeps are expensive. Install their units without
        // taking CPU from an ongoing learner unless the operator explicitly
        // starts each schedule or invokes Run now.
        if(!Object.hasOwn(SEASON_SUITES,id)&&!id.startsWith('history-')&&!id.startsWith('mix-'))
            await systemctl(['start',unitFiles(o,id).timer]);
    }
    return {message:'Fast TinyBrains evaluation timers started. Full-season 2/4/6-player suites installed; use Start or Run now for each expensive sweep.'};
}
async function refreshSuite(hub,id) {
    const o=localOptions(hub);
    const approved=definitions(o);
    const unit=approvedUnit(o,id,approved[id]);
    const pairNames=unitFiles(o,BATCH_ID);
    const pairFiles=[pairNames.serviceFile,pairNames.timerFile]
        .map(file=>fs.existsSync(file));
    if(pairFiles[0]!==pairFiles[1])
        throw Error('Incomplete Nano/Micro evaluator pair; use Set up to repair it.');
    const pair=pairFiles[0]?approvedUnit(o,BATCH_ID,approved[BATCH_ID]):null;
    const units=pair?[unit,pair]:[unit];
    for(const current of units) {
        const service=await properties(current.service);
        if(['active','activating'].includes(service.ActiveState))
            throw Error('Finish the active evaluation before approving a new suite.');
    }
    const {stdout}=await run(presentFile(o.launcher),[
        '-B',path.join(o.hubRoot,'Training/experiment_hub_evaluation_runtime.py'),
        '--project','tinybrains','--engine-path',o.engine,
    ],{timeout:90000,maxBuffer:1024*1024});
    const report=JSON.parse(stdout),digest=report.runtime_sha256;
    const modelSha=report.runtime?.files?.[id+'/model.onnx'];
    const manifestSha=report.runtime?.files?.[id+'/manifest.json'];
    if([digest,modelSha,manifestSha].some(value=>
        typeof value!=='string'||!/^[a-f0-9]{64}$/.test(value)))
        throw Error('The installed TinyBrains runtime returned incomplete opponent fingerprints.');
    const oldSuite=JSON.parse(fs.readFileSync(suiteFiles(o)[id],'utf8'));
    if(oldSuite.opponent!==id)
        throw Error('Frozen TinyBrains evaluator suite has a different opponent.');
    if(oldSuite.runtime_sha256===digest&&
        oldSuite.opponent_model_sha256===modelSha&&
        oldSuite.opponent_manifest_sha256===manifestSha)
        return {message:id+' suite is already pinned to this runtime and opponent.'};
    const pattern=new RegExp('^tinybrains_'+id+'_tiny_v((?:[3-9]|[1-9][0-9]{1,2}))\\.json$');
    const versions=fs.readdirSync(o.suites).map(name=>pattern.exec(name))
        .filter(Boolean).map(match=>Number(match[1]));
    const version=Math.max(3,...versions)+1;
    if(version>999)throw Error('TinyBrains suite version limit exceeded.');
    const name='tinybrains_'+id+'_tiny_v'+version;
    const suiteFile=path.join(o.suites,name+'.json');
    if(fs.existsSync(suiteFile))
        throw Error('New frozen TinyBrains suite already exists.');
    const marker=path.join(o.suites,'hub-approved-latest.json');
    const beforeMarker=fs.existsSync(marker)?fs.readFileSync(marker,'utf8'):null;
    const saved=units.map(current=>({
        unit:current,
        service:fs.readFileSync(current.serviceFile,'utf8'),
        timer:fs.readFileSync(current.timerFile,'utf8'),
    }));
    let installed=false;
    try {
        for(const current of units)
            await systemctl(['stop',current.timer]);
        // Frozen suite files remain immutable; the approved single-suite
        // schedule and any installed same-checkpoint batch change together.
        fs.writeFileSync(suiteFile,JSON.stringify({
            ...oldSuite,suite_id:name,runtime_sha256:digest,
            opponent_model_sha256:modelSha,
            opponent_manifest_sha256:manifestSha,
        },null,2)+'\n',{flag:'wx',mode:0o600});
        const next=definitions(o,{[id]:suiteFile});
        for(const current of units) {
            const key=current===unit?id:BATCH_ID;
            fs.writeFileSync(current.serviceFile,next[key].service,{mode:0o600});
            fs.writeFileSync(current.timerFile,next[key].timer,{mode:0o600});
        }
        const names=suiteNames(o);
        // Only Nano and Micro belong in the versioned approval marker.
        // Mini's separately SHA-pinned suite is discovered from its file.
        fs.writeFileSync(marker,JSON.stringify({
            version:1,
            nano:id==='nano'?name:names.nano,
            micro:id==='micro'?name:names.micro,
        },null,2)+'\n',{mode:0o600});
        await systemctl(['daemon-reload']);
        for(const current of units)
            await systemctl(['start',current.timer]);
        installed=true;
    }finally {
        if(!installed) {
            for(const current of units) {
                try {await systemctl(['stop',current.timer]);}
                catch { /* Best effort before restoring the approved units. */ }
            }
            for(const previous of saved) {
                fs.writeFileSync(previous.unit.serviceFile,previous.service,{mode:0o600});
                fs.writeFileSync(previous.unit.timerFile,previous.timer,{mode:0o600});
            }
            if(beforeMarker===null) {
                if(fs.existsSync(marker))fs.unlinkSync(marker);
            }else fs.writeFileSync(marker,beforeMarker,{mode:0o600});
            try {
                await systemctl(['daemon-reload']);
                for(const current of units)
                    await systemctl(['start',current.timer]);
            }catch { /* Leave the initial service failure visible. */ }
        }
    }
    return {message:name+' approved and scheduled. Restart the local agent from this panel to advertise the new suite.'};
}
function historicalCandidates(hub,o) {
    const candidates=[];
    for(const job of hub.snapshot().jobs||[]) {
        if(job.project_id!=='tinybrains'||job.status!=='finished'||
            job.checkpoint_ready_verified!==true||
            !job.capacity_released_at||
            !UUID.test(job.id)||!SHA256.test(job.checkpoint_model_sha256||''))
            continue;
        const dir=path.join(o.artifacts,job.id);
        try {
            const overrideFile=path.join(dir,'hub-design-overrides.json');
            const overrides=fs.existsSync(overrideFile)
                ?JSON.parse(fs.readFileSync(overrideFile,'utf8')):{};
            const status=JSON.parse(fs.readFileSync(path.join(dir,'status.json'),'utf8'));
            const checkpoint=path.join(dir,'self-play/latest.pt');
            const stat=fs.lstatSync(checkpoint);
            if(!stat.isFile()||stat.isSymbolicLink()||
                !Number.isSafeInteger(status.ppo_updates)||status.ppo_updates<1)
                continue;
            candidates.push({id:job.id,updates:status.ppo_updates,
                batch_size:overrides['training.colony_batch_size']??null,
                checkpoint,sha256:job.checkpoint_model_sha256,
                modified:stat.mtimeMs});
        }catch { /* A missing/partial job is not a frozen source. */ }
    }
    candidates.sort((a,b)=>b.updates-a.updates||b.modified-a.modified);
    return candidates;
}
function miniCandidates(hub,o) {
    return historicalCandidates(hub,o).filter(source=>source.batch_size===128);
}

function verifyFrozenMini(options,source,suite,name=MINI_SUITE) {
    const directory=path.join(options.suites,'opponents',name);
    if(suite.suite_id!==name||suite.opponent!=='mini'||
        suite.opponent_dir!==directory||
        suite.opponent_source_job!==source.id||
        suite.opponent_checkpoint_sha256!==source.sha256||
        !SHA256.test(String(suite.opponent_model_sha256||''))||
        !SHA256.test(String(suite.opponent_manifest_sha256||'')))
        throw Error('Existing frozen Mini suite does not match the selected completed training job.');
    privateDirectory(directory);
    for(const [name,expected] of [
        ['model.onnx',suite.opponent_model_sha256],
        ['manifest.json',suite.opponent_manifest_sha256],
    ]) {
        const file=presentFile(path.join(directory,name));
        const stat=fs.lstatSync(file);
        if(stat.uid!==process.getuid()||(stat.mode&0o077))
            throw Error('Frozen Mini opponent file is not private and user-owned: '+name);
        const actual=crypto.createHash('sha256')
            .update(fs.readFileSync(file)).digest('hex');
        if(actual!==expected)
            throw Error('Frozen Mini opponent differs from its original approved fingerprint: '+name);
    }
}

async function setupMini(hub,sourceJobId,{historical=false}={}) {
    const o=localOptions(hub);
    prepareInstall(o);
    const candidates=historical?historicalCandidates(hub,o):miniCandidates(hub,o);
    const source=sourceJobId===undefined?candidates[0]:
        candidates.find(item=>item.id===sourceJobId);
    if(!source)
        throw Error('No eligible finished TinyBrains run with a pinned checkpoint was found.');
    const name=historical
        ?'tinybrains_mini_'+source.id.replace(/-/g,'').slice(0,20)
        :MINI_SUITE;
    const destination=path.join(o.suites,name+'.json');
    if(fs.existsSync(destination)) {
        const file=presentFile(destination);
        const stat=fs.lstatSync(file);
        if(stat.uid!==process.getuid()||(stat.mode&0o077))
            throw Error('Frozen Mini suite file is not private and user-owned.');
        const suite=JSON.parse(fs.readFileSync(file,'utf8'));
        verifyFrozenMini(o,source,suite,name);
        // An interrupted setup may have published the immutable suite before
        // systemd installed its one-shot service and timer. Finish that
        // installation instead of falsely reporting success.
        await setup(hub);
        return {message:'Previously approved frozen opponent '+name+
            ' verified and evaluator schedule reconciled. Restart the local agent when idle to advertise it.'};
    }
    // The trusted launcher is installed only by local setup; it never gets
    // an arbitrary checkout, path, container or command from the browser.
    await setup(hub);
    const stage=fs.mkdtempSync(path.join(o.scratch,historical?'history-freeze-':'mini-freeze-'));
    const output=path.join(stage,'out');
    fs.mkdirSync(output,{mode:0o700});
    const opponents=privateDirectory(path.join(o.suites,'opponents'),true);
    const frozenPath=path.join(opponents,name);
    let prepared=null;
    try {
        const {stdout}=await run(presentFile(o.launcher),[
            '-B',path.join(o.hubRoot,'Training/experiment_hub_tinybrains_freeze.py'),
            '--checkpoint',source.checkpoint,'--output',output,
            '--expected-sha256',source.sha256,'--engine-path',o.engine,
        ],{timeout:360000,maxBuffer:1024*1024});
        const info=JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
        if(info.checkpoint_sha256!==source.sha256||
            [info.model_sha256,info.manifest_sha256,info.runtime_sha256]
                .some(value=>!SHA256.test(value))||
            !Number.isSafeInteger(info.model_size_metric_bytes)||
            info.model_size_metric_bytes<1||info.model_size_metric_bytes>1048576)
            throw Error('Frozen Mini export lacks trusted checkpoint/model/runtime fingerprints.');
        const exported=path.join(output,'export');
        const files=[['model.onnx',info.model_sha256],
            ['manifest.json',info.manifest_sha256]];
        for(const [name,hash] of files) {
            const file=presentFile(path.join(exported,name));
            const actual=crypto.createHash('sha256')
                .update(fs.readFileSync(file)).digest('hex');
            if(actual!==hash)throw Error('Frozen Mini export changed after verification.');
        }
        if(fs.existsSync(destination))
            throw Error('Frozen Mini suite was approved by a concurrent operation; existing definition is immutable.');
        if(fs.existsSync(frozenPath)) {
            // The last coordinator might have restarted between installing
            // the frozen model and writing the suite. Reuse it only if both
            // bytes match this newly verified checkpoint export exactly.
            privateDirectory(frozenPath);
            for(const [name,expected] of files) {
                const file=presentFile(path.join(frozenPath,name));
                const stat=fs.lstatSync(file);
                if(stat.uid!==process.getuid()||(stat.mode&0o077)||
                    crypto.createHash('sha256')
                        .update(fs.readFileSync(file)).digest('hex')!==expected)
                    throw Error('Orphaned frozen Mini opponent differs from the selected checkpoint; no files were replaced.');
            }
        }else{
            prepared=fs.mkdtempSync(path.join(opponents,'.mini-stage-'));
            for(const [name] of files) {
                const file=path.join(prepared,name);
                fs.copyFileSync(path.join(exported,name),file,fs.constants.COPYFILE_EXCL);
                fs.chmodSync(file,0o600);
            }
            fs.renameSync(prepared,frozenPath);
            prepared=null;
        }
        const suite={
            suite_id:name,
            maps:historical?seasonBoards(o):['basic-tiny-2p'],
            seeds:historical?[813101]:Array.from({length:50},(_,i)=>813001+i),
            max_turns:historical?1000:300,
            opponent:'mini',opponent_dir:frozenPath,
            opponent_source_job:source.id,
            opponent_checkpoint_sha256:source.sha256,
            opponent_model_sha256:info.model_sha256,
            opponent_manifest_sha256:info.manifest_sha256,
            runtime_sha256:info.runtime_sha256,
        };
        fs.writeFileSync(destination,JSON.stringify(suite,null,2)+'\n',
            {flag:'wx',mode:0o600});
        // Reconcile the entire approved evaluator set after publishing Mini,
        // including the newly eligible Mini season suite. The common setup
        // path preserves existing units and leaves expensive season timers
        // stopped until explicitly selected by the operator.
        await setup(hub);
        if(historical)
            return {message:'Historical TinyBrains checkpoint '+source.id+
                ' frozen as '+name+' (update '+source.updates+
                '). Full-season evaluation installed but stopped. Start it explicitly and restart the agent when idle.'};
        // Adding a new trusted opponent changes the evaluator method hash.
        // Reapprove existing Nano/Micro suites at their same seeds without
        // rewriting their historic definitions or interrupting training.
        const pending=[];
        for(const id of ['nano','micro']) {
            try {await refreshSuite(hub,id);}
            catch(error){pending.push(id+': '+String(error.message||error).slice(0,100));}
        }
        return {message:'Frozen Batch-128 Mini opponent approved from job '+
            source.id+' (update '+source.updates+'); 100 balanced-seat games per checkpoint. '+
            (pending.length?'Refresh remaining baseline suites when idle: '+
                pending.join('; ')+'. ':'Nano/Micro runtime approvals refreshed. ')+
            'Restart the local agent to advertise the new suite.'};
    }finally {
        if(prepared)fs.rmSync(prepared,{recursive:true,force:true});
        fs.rmSync(stage,{recursive:true,force:true});
    }
}
function mapReport(hub,id) {
    const o=localOptions(hub),approved=suiteNames(o);
    if(typeof id!=='string'||!/^[a-z][a-z0-9_-]{0,31}$/.test(id)||
        !Object.hasOwn(approved,id))
        throw Error('Unknown approved TinyBrains evaluation suite.');
    const suiteName=approved[id];
    const suite=JSON.parse(fs.readFileSync(
        presentFile(path.join(o.suites,suiteName+'.json')),'utf8'));
    if(suite.suite_id!==suiteName||!Array.isArray(suite.maps)||
        suite.maps.length>128)
        throw Error('Invalid frozen TinyBrains map inventory.');
    privateDirectory(o.receipts);
    const suffix='.'+suiteName+'.maps.json';
    const reports=fs.readdirSync(o.receipts).filter(name=>name.endsWith(suffix))
        .map(name=>{
            const dispatch=name.slice(0,-suffix.length);
            if(!UUID.test(dispatch))return null;
            const file=path.join(o.receipts,name);
            try {
                const stat=fs.lstatSync(file);
                return stat.isFile()&&!stat.isSymbolicLink()&&
                    fs.realpathSync(file)===file&&stat.uid===process.getuid()&&
                    !(stat.mode&0o077)&&stat.size>0&&stat.size<=32768
                    ?{file,dispatch,at:stat.mtimeMs}:null;
            }catch {return null;}
        }).filter(Boolean).sort((a,b)=>b.at-a.at);
    for(const item of reports.slice(0,128)) {
        try {
            const details=JSON.parse(fs.readFileSync(item.file,'utf8'));
            const file=presentFile(path.join(o.receipts,
                item.dispatch+'.'+suiteName+'.json'));
            const stat=fs.lstatSync(file);
            if(stat.uid!==process.getuid()||stat.mode&0o077||
                stat.size<1||stat.size>4096)continue;
            const receipt=JSON.parse(fs.readFileSync(file,'utf8'));
            if(details.schema_version!==1||details.dispatch_id!==item.dispatch||
                details.suite_id!==suiteName||
                !UUID.test(String(details.job_id||''))||
                !SHA256.test(String(details.suite_sha256||''))||
                !SHA256.test(String(details.checkpoint_sha256||''))||
                receipt.dispatch_id!==details.dispatch_id||
                receipt.suite_id!==details.suite_id||
                receipt.suite_sha256!==details.suite_sha256||
                receipt.checkpoint_sha256!==details.checkpoint_sha256||
                receipt.trials!==details.trials||
                receipt.successes!==details.successes||
                !Array.isArray(details.maps)||
                details.maps.length!==suite.maps.length)continue;
            let trials=0,wins=0,valid=true;
            for(let i=0;i<details.maps.length;i++) {
                const row=details.maps[i];
                if(!row||row.map!==suite.maps[i]||
                    ![2,4,6].includes(row.players)||
                    !['win','loss','draw','top_half','placement_sum','trials']
                        .every(key=>Number.isSafeInteger(row[key])&&row[key]>=0)||
                    row.win+row.loss+row.draw!==row.trials||
                    row.top_half>row.trials||
                    row.placement_sum<row.trials||
                    row.placement_sum>row.trials*row.players) {valid=false;break;}
                trials+=row.trials;wins+=row.win;
            }
            if(!valid||trials!==details.trials||wins!==details.successes)
                continue;
            return {available:true,suite_id:suiteName,
                dispatch_id:details.dispatch_id,job_id:details.job,
                checkpoint_sha256:details.checkpoint_sha256,
                trials,successes:wins,maps:details.maps,
                opponents:Array.isArray(details.opponents)?details.opponents:[],
                recorded_at:new Date(item.at).toISOString()};
        }catch { /* Stale or incomplete reports do not replace a receipt. */ }
    }
    return {available:false,suite_id:suiteName,
        reason:'No current checkpoint receipt with a matching per-map report is available.'};
}
// Move unused immutable suite recipes out of the active timer namespace.
// Never erase checkpoint imports or evaluator receipts. Archives are
// deliberately not advertised as runnable suites until deliberately restored.
async function archiveUnusedEvaluations(hub) {
    const o=localOptions(hub);
    const unused=custom.unusedVersions(o,hub);
    if(!unused.length)return {message:'No unreferenced mixed evaluation versions need archiving.',
        archived:0,blocked:[]};
    const archive=privateDirectory(path.join(o.suites,'archived'),true);
    let archived=0,unitFilesChanged=false;
    const blocked=[];
    for(const suiteId of unused) {
        const match=/^tinybrains_mix_([a-f0-9]{20})_v([1-9][0-9]*)$/.exec(suiteId);
        if(!match)continue;
        const origin=path.join(o.suites,suiteId+'.json');
        const destination=path.join(archive,suiteId+'.json');
        const unit=unitFiles(o,'mix-'+match[1]+'-v'+match[2]);
        try {
            if(fs.existsSync(destination))
                throw Error('Archived immutable version already exists; no overwrite was attempted.');
            const file=presentFile(origin),stat=fs.lstatSync(file);
            if(stat.uid!==process.getuid()||(stat.mode&0o077)!==0)
                throw Error('Frozen evaluation definition is not a private user-owned file.');
            const serviceExists=fs.existsSync(unit.serviceFile);
            const timerExists=fs.existsSync(unit.timerFile);
            if(serviceExists!==timerExists)
                throw Error('Evaluation service installation is partial; repair it before archiving.');
            if(serviceExists) {
                const approved=definitions(o);
                approvedUnit(o,'mix-'+match[1]+'-v'+match[2],
                    approved['mix-'+match[1]+'-v'+match[2]]);
                const running=await properties(unit.service);
                if(['active','activating','deactivating'].includes(running.ActiveState))
                    throw Error('The evaluation service is still running.');
                await systemctl(['stop',unit.timer]);
                const afterward=await properties(unit.service);
                if(['active','activating','deactivating'].includes(afterward.ActiveState))
                    throw Error('The evaluation service started while retiring its timer.');
                await systemctl(['disable',unit.timer]);
            }
            // Recheck live Hub references immediately before removing the
            // approved suite from the discovery directory.
            if(!custom.unusedVersions(o,hub).includes(suiteId))
                throw Error('The evaluation became referenced by a saved job/configuration.');
            if(serviceExists) {
                // Retire unit files first while the immutable recipe is
                // still in its original location. If cleanup is interrupted,
                // Set up can reinstall the same frozen service safely.
                fs.unlinkSync(unit.timerFile);
                unitFilesChanged=true;
                fs.unlinkSync(unit.serviceFile);
            }
            fs.renameSync(origin,destination);
            archived++;
        }catch(error) {
            blocked.push({suite_id:suiteId,reason:String(error.message||error).slice(0,180)});
        }
    }
    if(archived||unitFilesChanged)await systemctl(['daemon-reload']);
    return {archived,blocked,message:'Archived '+archived+
        ' unused mixed evaluation version(s); frozen recipes and prior receipts remain intact.'+
        (blocked.length?' '+blocked.length+' version(s) need attention; see archive details.':'')};
}
async function action(hub,value) {
    if(!value||value.confirm_action!==true)
        throw Error('Explicit evaluator action confirmation is required.');
    if(value.action==='setup'&&value.id===undefined)
        return setup(hub);
    if(value.action==='setup-mini'&&value.id===undefined&&
        (value.source_job_id===undefined||UUID.test(value.source_job_id)))
        return setupMini(hub,value.source_job_id);
    if(value.action==='archive-unused-custom'&&value.id===undefined)
        return archiveUnusedEvaluations(hub);
    if(value.action==='save-custom') {
        const o=localOptions(hub);
        const previous=value.id==null?null:
            custom.read(o).templates.find(t=>t.id===value.id);
        const saved=custom.saveEvaluation(o,hub,value);
        // The immutable definition is already committed; a failed service
        // install must not masquerade as a failed save and prompt another
        // revision. Set up can reconcile the original installation later.
        try {
            await setup(hub);
        }catch(error) {
            return {...saved,installed:false,message:saved.message+
                ' The evaluation is saved but its service is not installed: '+
                String(error.message||error).slice(0,180)+
                '. Use Set up TinyBrains evaluations to reconcile it.'};
        }
        // The previous revision may still be selected by another saved
        // Design or a running job. Leave its schedule alone in that case.
        // A failed optional timer stop must not turn a committed Save into
        // an apparent failure that causes the user to duplicate a revision.
        if(previous&&!custom.referencedSuites(hub).has(previous.suite_id)) {
            const old=unitFiles(o,'mix-'+previous.id+'-v'+previous.revision);
            if(fs.existsSync(old.timerFile))try {
                await systemctl(['stop',old.timer]);
            }catch(error) {
                return {...saved,installed:true,message:saved.message+
                    ' The prior revision was saved but its timer could not be stopped: '+
                    String(error.message||error).slice(0,170)};
            }
        }
        return saved;
    }
    if(value.action==='delete-custom') {
        const o=localOptions(hub);
        const current=custom.read(o).templates.find(row=>row.id===value.id);
        if(!current||current.revision!==value.expected_revision)
            throw Error('Saved evaluation revision changed; reload before deletion.');
        const referenced=custom.referencedSuites(hub).has(current.suite_id);
        // A prequeued/running/terminal job may still require its pinned final
        // suite. Never disarm its scheduler merely because a saved template
        // was deleted. Old immutable service files and receipts are retained.
        if(!referenced) {
            const unit=unitFiles(o,'mix-'+current.id+'-v'+current.revision);
            if(fs.existsSync(unit.timerFile))
                await systemctl(['stop',unit.timer]);
        }
        const removed=custom.deleteEvaluation(o,value);
        return {...removed,message:removed.message+(referenced
            ?' A job still references its frozen suite; its evaluator schedule was preserved.'
            :' Its evaluator timer was stopped.')};
    }
    if(value.action==='freeze-history'&&value.id===undefined&&
        UUID.test(String(value.source_job_id||''))) {
        const result=await setupMini(hub,value.source_job_id,{historical:true});
        if(value.opponent_name)
            return custom.nameOpponent(localOptions(hub),
                value.source_job_id,value.opponent_name);
        return result;
    }
    const options=localOptions(hub);
    if(!scheduledIds(options).includes(value.id)||
        !['start','restart','stop','run','refresh'].includes(value.action)||
        (![...Object.keys(SUITES)].includes(value.id)&&value.action==='refresh'))
        throw Error('Unknown approved local evaluator action.');
    if(value.action==='refresh')return refreshSuite(hub,value.id);
    const o=localOptions(hub);
    const defs=definitions(o);
    const unit=approvedUnit(o,value.id,defs[value.id]);
    if(['start','restart','run'].includes(value.action)&&
        expensiveEvaluation(value.id)) {
        const capacity=evaluationCapacity(hub,o);
        if(!capacity.ready)
            throw Error('Full-season evaluation admission deferred: '+capacity.reason);
    }
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
module.exports={status,action,mapReport};
