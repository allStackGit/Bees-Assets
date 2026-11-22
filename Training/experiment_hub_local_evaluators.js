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
const UUID=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const SHA256=/^[a-f0-9]{64}$/;
function suiteNames(options) {
    const marker=path.join(options.suites,'hub-approved-latest.json');
    if(!fs.existsSync(marker))return {
        ...SUITES,
        ...(fs.existsSync(path.join(options.suites,MINI_SUITE+'.json'))
            ?{mini:MINI_SUITE}:{}),
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
    };
}
function scheduledIds(options) {
    return [...SCHEDULED_IDS,...(suiteNames(options).mini?[MINI_ID]:[])];
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
    if(!host)
        throw Error('The enrolled local agent identity is not registered in the Hub.');
    if(host.status!=='connected')
        throw Error('Waiting for a fresh authenticated local agent heartbeat; evaluator readiness will refresh automatically.');
    if(!host.installed_projects?.includes('tinybrains')) {
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
        if(stat.size<1||stat.size>8192)
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
    for(const id of scheduledIds(options)) {
        const names=unitFiles(options,id);
        const serviceExists=fs.existsSync(names.serviceFile);
        const timerExists=fs.existsSync(names.timerFile);
        const installed=serviceExists&&timerExists;
        let managed=false,reason=serviceExists!==timerExists
            ?'Incomplete installation; use Set up to finish the approved unit pair.':null;
        let active=false,running=false,lastResult=null,lastError=null;
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
        entries.push({id,suite_id:id===BATCH_ID?null:suiteNames(options)[id],installed,managed,
            active,running,last_result:lastResult,last_error:lastError,reason});
    }
    return {available:true,machine:options.machine,entries,
        mini_candidates:miniCandidates(hub,options).map(({id,updates})=>({id,updates}))};
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
    const defs=definitions(o);
    // Never overwrite an installed service, even one sharing our fixed name.
    for(const [id,definition] of Object.entries(defs)) {
        const unit=unitFiles(o,id);
        if(fs.existsSync(unit.serviceFile)&&fs.existsSync(unit.timerFile))
            approvedUnit(o,id,definition);
        else installEvaluatorUnits(definition,o.home,{repairPartial:true});
    }
    await systemctl(['daemon-reload']);
    for(const id of scheduledIds(o))
        await systemctl(['start',unitFiles(o,id).timer]);
    return {message:'Approved TinyBrains evaluation timers installed and started.'};
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
function miniCandidates(hub,o) {
    const candidates=[];
    for(const job of hub.snapshot().jobs||[]) {
        if(job.project_id!=='tinybrains'||job.status!=='finished'||
            !UUID.test(job.id)||!SHA256.test(job.checkpoint_model_sha256||''))
            continue;
        const dir=path.join(o.artifacts,job.id);
        try {
            const overrides=JSON.parse(fs.readFileSync(
                path.join(dir,'hub-design-overrides.json'),'utf8'));
            if(overrides['training.colony_batch_size']!==128)continue;
            const status=JSON.parse(fs.readFileSync(path.join(dir,'status.json'),'utf8'));
            const checkpoint=path.join(dir,'self-play/latest.pt');
            const stat=fs.lstatSync(checkpoint);
            if(!stat.isFile()||stat.isSymbolicLink()||
                !Number.isSafeInteger(status.ppo_updates)||status.ppo_updates<1)
                continue;
            candidates.push({id:job.id,updates:status.ppo_updates,
                checkpoint,sha256:job.checkpoint_model_sha256,
                modified:stat.mtimeMs});
        }catch { /* A missing/partial job is not a frozen source. */ }
    }
    candidates.sort((a,b)=>b.updates-a.updates||b.modified-a.modified);
    return candidates;
}

function verifyFrozenMini(options,source,suite) {
    const directory=path.join(options.suites,'opponents',MINI_SUITE);
    if(suite.suite_id!==MINI_SUITE||suite.opponent!=='mini'||
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

async function setupMini(hub,sourceJobId) {
    const o=localOptions(hub);
    prepareInstall(o);
    const candidates=miniCandidates(hub,o);
    const source=sourceJobId===undefined?candidates[0]:
        candidates.find(item=>item.id===sourceJobId);
    if(!source)
        throw Error('No finished Batch-128 TinyBrains run with a pinned checkpoint was found.');
    const destination=path.join(o.suites,MINI_SUITE+'.json');
    if(fs.existsSync(destination)) {
        const file=presentFile(destination);
        const stat=fs.lstatSync(file);
        if(stat.uid!==process.getuid()||(stat.mode&0o077))
            throw Error('Frozen Mini suite file is not private and user-owned.');
        const suite=JSON.parse(fs.readFileSync(file,'utf8'));
        verifyFrozenMini(o,source,suite);
        // An interrupted setup may have published the immutable suite before
        // systemd installed its one-shot service and timer. Finish that
        // installation instead of falsely reporting success.
        await setup(hub);
        return {message:'Previously approved frozen Mini opponent verified and evaluator schedule reconciled. '+
            'Restart the local agent when idle to advertise the suite.'};
    }
    // The trusted launcher is installed only by local setup; it never gets
    // an arbitrary checkout, path, container or command from the browser.
    await setup(hub);
    const stage=fs.mkdtempSync(path.join(o.scratch,'mini-freeze-'));
    const output=path.join(stage,'out');
    fs.mkdirSync(output,{mode:0o700});
    const opponents=privateDirectory(path.join(o.suites,'opponents'),true);
    const frozenPath=path.join(opponents,MINI_SUITE);
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
            suite_id:MINI_SUITE,maps:['basic-tiny-2p'],
            seeds:Array.from({length:50},(_,i)=>813001+i),max_turns:300,
            opponent:'mini',opponent_dir:frozenPath,
            opponent_source_job:source.id,
            opponent_checkpoint_sha256:source.sha256,
            opponent_model_sha256:info.model_sha256,
            opponent_manifest_sha256:info.manifest_sha256,
            runtime_sha256:info.runtime_sha256,
        };
        fs.writeFileSync(destination,JSON.stringify(suite,null,2)+'\n',
            {flag:'wx',mode:0o600});
        const defs=definitions(o);
        const unit=unitFiles(o,MINI_ID);
        installEvaluatorUnits(defs[MINI_ID],o.home,{repairPartial:true});
        await systemctl(['daemon-reload']);
        await systemctl(['start',unit.timer]);
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
async function action(hub,value) {
    if(!value||value.confirm_action!==true)
        throw Error('Explicit evaluator action confirmation is required.');
    if(value.action==='setup'&&value.id===undefined)
        return setup(hub);
    if(value.action==='setup-mini'&&value.id===undefined&&
        (value.source_job_id===undefined||UUID.test(value.source_job_id)))
        return setupMini(hub,value.source_job_id);
    const options=localOptions(hub);
    if(!scheduledIds(options).includes(value.id)||
        !['start','restart','stop','run','refresh'].includes(value.action)||
        (![...Object.keys(SUITES)].includes(value.id)&&value.action==='refresh'))
        throw Error('Unknown approved local evaluator action.');
    if(value.action==='refresh')return refreshSuite(hub,value.id);
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
