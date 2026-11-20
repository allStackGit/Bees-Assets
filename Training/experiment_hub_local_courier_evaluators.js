'use strict';

// Coordinator-local, operator-approved Courier evaluator lifecycle. No HTTP
// argument can select an executable, unit name, source checkout or arbitrary
// file path. New suite identities are immutable and tied to the local config.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {evaluatorDefinition,installEvaluatorUnits}=require('./experiment_hub_systemd');

const run=promisify(execFile);
const SHA=/^[0-9a-f]{64}$/;
const MACHINE=/^[a-z][a-z0-9_-]{0,47}$/;
function regular(file) {
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink()||
        fs.realpathSync(file)!==file)
        throw Error('Courier evaluator needs a non-redirected local file: '+file);
    return file;
}
function directory(file,create=false) {
    if(create)fs.mkdirSync(file,{recursive:true,mode:0o700});
    const stat=fs.lstatSync(file);
    if(!stat.isDirectory()||stat.isSymbolicLink()||
        fs.realpathSync(file)!==file)
        throw Error('Courier evaluator needs a non-redirected directory: '+file);
    return file;
}
function privateDirectory(file,create=false) {
    directory(file,create);
    const stat=fs.lstatSync(file);
    if(stat.uid!==process.getuid())
        throw Error('Courier evaluator writable directory must be user-owned: '+file);
    if(create&&stat.mode&0o077)fs.chmodSync(file,0o700);
    if(fs.statSync(file).mode&0o077)
        throw Error('Courier evaluator writable directory must be private: '+file);
    return file;
}
function configuration(hub) {
    if(process.platform!=='linux')
        throw Error('Courier evaluator local service controls require Linux user systemd.');
    const home=os.homedir();
    const env=regular(path.join(home,'.config/experiment-hub/agent.env'));
    const stat=fs.statSync(env);
    if(stat.uid!==process.getuid()||stat.mode&0o077)
        throw Error('Courier evaluator requires the enrolled private machine identity.');
    const ids=fs.readFileSync(env,'utf8').split(/\r?\n/)
        .filter(line=>line.startsWith('EXPERIMENT_HUB_MACHINE_ID='));
    if(ids.length!==1)throw Error('Expected one local enrolled machine identity.');
    const machine=ids[0].split('=',2)[1].trim().replace(/^['"]|['"]$/g,'');
    if(!MACHINE.test(machine))throw Error('Invalid enrolled machine ID.');
    const host=hub.snapshot().machines.find(row=>row.id===machine);
    if(!host||!host.installed_projects?.includes('courier'))
        throw Error('This machine has no locally approved Courier training adapter.');
    const ai=path.join(home,'ai');
    const project=path.join(ai,'projects/basic-training');
    const config=path.join(project,'environment.json');
    const interpreter=path.join(ai,'envs/cpu-training/bin/python');
    regular(config);
    // Preserve this approved Python virtualenv invocation path. bin/python
    // may be a symlink; resolving it loses the environment's site-packages.
    if(path.resolve(interpreter)!==interpreter||
        !fs.statSync(interpreter).isFile())
        throw Error('Approved Courier Python virtual environment is unavailable.');
    directory(project);
    const raw=fs.readFileSync(config);
    if(raw.length<2||raw.length>262144)throw Error('Courier environment JSON is invalid.');
    const parsed=JSON.parse(raw.toString('utf8'));
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed))
        throw Error('Courier environment must be a JSON object.');
    const hash=crypto.createHash('sha256').update(raw).digest('hex');
    const approvalFile=path.join(home,'evaluation-audits/courier/hub-approved-evaluation.json');
    let suiteId='courier_survival_'+hash.slice(0,12);
    if(fs.existsSync(approvalFile)) {
        const stat=fs.lstatSync(approvalFile);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||stat.mode&0o077)
            throw Error('Courier evaluator approval must be a private regular file.');
        const approved=JSON.parse(fs.readFileSync(approvalFile,'utf8'));
        if(approved.schema_version!==1||
            !SHA.test(String(approved.config_sha256||''))||
            !/^courier_survival_(?:[a-f0-9]{12}|[a-f0-9]{8}_[a-f0-9]{8})$/.test(approved.suite_id))
            throw Error('Courier evaluation approval manifest is invalid.');
        if(approved.config_sha256===hash)suiteId=approved.suite_id;
    }
    return {
        home,ai,machine,project,interpreter,hash,suiteId,
        suiteRoot:path.join(home,'evaluation-audits/courier/suites'),
        approvalFile:path.join(home,'evaluation-audits/courier/hub-approved-evaluation.json'),
        suiteFile:path.join(home,'evaluation-audits/courier/suites',suiteId+'.json'),
        scratch:path.join(home,'evaluation-audits/courier/live-evaluation-scratch'),
        artifacts:path.join(ai,'experiments/experiment-hub/courier-jobs'),
        snapshots:path.join(ai,'experiments/experiment-hub/courier-evaluation-snapshots'),
        receipts:path.join(ai,'experiments/experiment-hub/evaluator-receipts'),
        unitDir:path.join(home,'.config/systemd/user'),
        unitId:'survival_'+suiteId.slice('courier_survival_'.length),
    };
}
function frozenSuite(o,create=false) {
    if(create)privateDirectory(o.suiteRoot,true);
    const data={
        suite_id:o.suiteId,config_sha256:o.hash,
        seed:618437, trials:100,horizon:1024,
    };
    if(create&&!fs.existsSync(o.suiteFile)) {
        if(!SHA.test(String(o.runtimeSha||'')))
            throw Error('Courier evaluator runtime fingerprint is missing.');
        fs.writeFileSync(o.suiteFile,JSON.stringify({
            ...data,runtime_sha256:o.runtimeSha,
        },null,2)+'\n',{flag:'wx',mode:0o600});
    }
    const file=regular(o.suiteFile);
    const saved=JSON.parse(fs.readFileSync(file,'utf8'));
    if(Object.keys(saved).sort().join(',')!==
            'config_sha256,horizon,runtime_sha256,seed,suite_id,trials'||
        Object.entries(data).some(([key,value])=>saved[key]!==value)||
        !SHA.test(String(saved.runtime_sha256||''))||
        create&&saved.runtime_sha256!==o.runtimeSha)
        throw Error('An existing Courier frozen suite differs from its approved runtime and configuration.');
    return file;
}
function definition(o) {
    directory(o.artifacts);
    directory(o.snapshots);
    privateDirectory(o.scratch);
    privateDirectory(o.receipts);
    const suite=frozenSuite(o);
    return evaluatorDefinition({
        project:'courier',machine:o.machine,
        trainingDir:path.join(__dirname),
        pythonPath:o.interpreter,projectRoot:o.project,
        artifactRoot:o.artifacts,snapshotRoot:o.snapshots,
        scratch:o.scratch,receiptRoot:o.receipts,suites:[suite],
        cpus:'2',ramMb:'4096',interval:'180',timeout:'7200',unitId:o.unitId,
    });
}
function unitPaths(o) {
    const name='bees-experiment-hub-courier-'+o.machine+'-evaluator-'+o.unitId;
    return {service:name+'.service',timer:name+'.timer',
        serviceFile:path.join(o.unitDir,name+'.service'),
        timerFile:path.join(o.unitDir,name+'.timer')};
}
function approvedUnits(o,def) {
    const units=unitPaths(o);
    for(const [file,expected] of [
        [units.serviceFile,def.service],
        [units.timerFile,def.timer],
    ]) {
        const stat=fs.lstatSync(file);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||
            fs.readFileSync(file,'utf8')!==expected)
            throw Error('Courier evaluator service differs from its approved definition.');
    }
    return units;
}
async function systemctl(args) {
    return (await run('/usr/bin/systemctl',['--user',...args],{
        timeout:15000,maxBuffer:16384,
    })).stdout;
}
async function state(unit) {
    const lines=(await systemctl(['show',unit,
        '--property=LoadState,ActiveState,Result'])).trim().split(/\r?\n/);
    return Object.fromEntries(lines.map(line=>{
        const at=line.indexOf('=');
        return [line.slice(0,at),line.slice(at+1)];
    }));
}
async function status(hub) {
    try {
        const o=configuration(hub),u=unitPaths(o);
        const installed=fs.existsSync(u.serviceFile)&&fs.existsSync(u.timerFile);
        if(!installed)return {available:true,entries:[{
            project:'courier',id:o.unitId,suite_id:o.suiteId,
            installed:false,managed:false,active:false,running:false,
        }]};
        const approved=approvedUnits(o,definition(o));
        const [timer,service]=await Promise.all([
            state(approved.timer),state(approved.service),
        ]);
        const managed=timer.LoadState==='loaded'&&service.LoadState==='loaded';
        return {available:true,entries:[{
            project:'courier',id:o.unitId,suite_id:o.suiteId,
            installed:true,managed,active:timer.ActiveState==='active',
            running:['active','activating'].includes(service.ActiveState),
            last_result:service.Result||null,
            reason:managed?null:'Installed Courier evaluator needs systemd reload.',
        }]};
    }catch(error){
        return {available:false,reason:String(error.message||error),entries:[]};
    }
}
async function setup(hub) {
    const o=configuration(hub);
    privateDirectory(o.artifacts,true);
    privateDirectory(o.snapshots,true);
    privateDirectory(o.scratch,true);
    privateDirectory(o.receipts,true);
    const {stdout}=await run(o.interpreter,[
        '-B',path.join(__dirname,'experiment_hub_evaluation_runtime.py'),
        '--project','courier','--project-root',o.project,
    ],{timeout:90000,maxBuffer:1024*1024});
    o.runtimeSha=JSON.parse(stdout).runtime_sha256;
    if(!SHA.test(String(o.runtimeSha||'')))
        throw Error('Courier evaluator runtime did not return a valid digest.');
    const version=o.hash.slice(0,8)+'_'+o.runtimeSha.slice(0,8);
    o.suiteId='courier_survival_'+version;
    o.unitId='survival_'+version;
    o.suiteFile=path.join(o.suiteRoot,o.suiteId+'.json');
    frozenSuite(o,true);

    const approval=JSON.stringify({schema_version:1,
        config_sha256:o.hash,suite_id:o.suiteId},null,2)+'\n';
    let oldTimer=null,approvalChanged=true;
    if(fs.existsSync(o.approvalFile)) {
        const stat=fs.lstatSync(o.approvalFile);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||stat.mode&0o077)
            throw Error('Existing Courier evaluation approval is not a private regular file.');
        const current=JSON.parse(fs.readFileSync(o.approvalFile,'utf8'));
        if(current.schema_version!==1)
            throw Error('Unsupported Courier evaluator approval version.');
        if(!SHA.test(String(current.config_sha256||''))||
            !/^courier_survival_(?:[a-f0-9]{12}|[a-f0-9]{8}_[a-f0-9]{8})$/.test(current.suite_id))
            throw Error('Existing Courier evaluator approval is malformed.');
        approvalChanged=current.suite_id!==o.suiteId;
        if(!approvalChanged&&current.config_sha256!==o.hash)
            throw Error('Existing Courier evaluator suite identity has a different environment.');
        if(approvalChanged) {
            const previous='bees-experiment-hub-courier-'+o.machine+
                '-evaluator-survival_'+
                current.suite_id.slice('courier_survival_'.length)+'.timer';
            if(fs.existsSync(path.join(o.unitDir,previous)))
                oldTimer=previous;
        }
    }

    // Validate or install the replacement before touching the old schedule or
    // its approval. Failed preflight leaves the previous evaluator intact.
    const def=definition(o),unit=unitPaths(o);
    if(fs.existsSync(unit.serviceFile)&&fs.existsSync(unit.timerFile))
        approvedUnits(o,def);
    else installEvaluatorUnits(def,o.home,{repairPartial:true});
    await systemctl(['daemon-reload']);

    let oldRestoreNeeded=false,newStartAttempted=false,temporary=null;
    try {
        if(oldTimer) {
            const previous=await state(oldTimer);
            if(previous.ActiveState==='active') {
                oldRestoreNeeded=true;
                await systemctl(['stop',oldTimer]);
            }
        }
        newStartAttempted=true;
        await systemctl(['start',unit.timer]);

        // Publish the new immutable suite only after its schedule is running.
        // The agent advertises this private marker on its next safe restart.
        if(approvalChanged) {
            temporary=o.approvalFile+'.'+crypto.randomUUID()+'.new';
            fs.writeFileSync(temporary,approval,{flag:'wx',mode:0o600});
            fs.renameSync(temporary,o.approvalFile);
            temporary=null;
        }
    }catch(error) {
        const failures=[];
        if(newStartAttempted&&approvalChanged) {
            try { await systemctl(['stop',unit.timer]); }
            catch(rollback) { failures.push('new timer: '+rollback.message); }
        }
        if(oldRestoreNeeded) {
            try { await systemctl(['start',oldTimer]); }
            catch(rollback) { failures.push('old timer: '+rollback.message); }
        }
        if(failures.length)
            throw Error('Courier evaluator setup failed: '+error.message+
                '; schedule rollback needs operator review ('+
                failures.join('; ')+').');
        throw error;
    }finally {
        if(temporary) {
            try { fs.unlinkSync(temporary); }
            catch { /* A failed temporary cleanup cannot hide the setup error. */ }
        }
    }
    return {message:'Courier fixed-survival evaluation schedule installed and started. Restart the local agent from this panel so it advertises the new suite.'};
}
async function action(hub,value) {
    if(value?.confirm_action!==true)
        throw Error('Explicit evaluator confirmation required.');
    if(value.action==='setup'&&value.id===undefined)return setup(hub);
    const o=configuration(hub);
    if(value.id!==o.unitId||
        !['start','restart','stop','run'].includes(value.action))
        throw Error('Unknown approved Courier evaluator action.');
    const units=approvedUnits(o,definition(o));
    if(value.action==='run') {
        const current=await state(units.service);
        if(['active','activating'].includes(current.ActiveState))
            throw Error('Courier evaluation already running.');
        await systemctl(['start','--no-block',units.service]);
    }else await systemctl([value.action,units.timer]);
    return {message:'Courier evaluation '+value.action+' accepted; training is unaffected.'};
}
module.exports={status,action};
