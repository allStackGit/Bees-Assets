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
    regular(config);regular(interpreter);
    directory(project);
    const raw=fs.readFileSync(config);
    if(raw.length<2||raw.length>262144)throw Error('Courier environment JSON is invalid.');
    const parsed=JSON.parse(raw.toString('utf8'));
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed))
        throw Error('Courier environment must be a JSON object.');
    const hash=crypto.createHash('sha256').update(raw).digest('hex');
    const suiteId='courier_survival_'+hash.slice(0,12);
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
        unitId:'survival_'+hash.slice(0,8),
    };
}
function frozenSuite(o,create=false) {
    if(create)privateDirectory(o.suiteRoot,true);
    const data={
        suite_id:o.suiteId,config_sha256:o.hash,
        seed:618437, trials:100,horizon:1024,
    };
    const serialized=JSON.stringify(data,null,2)+'\n';
    if(create&&!fs.existsSync(o.suiteFile))
        fs.writeFileSync(o.suiteFile,serialized,{flag:'wx',mode:0o600});
    const file=regular(o.suiteFile);
    if(fs.readFileSync(file,'utf8')!==serialized)
        throw Error('An existing Courier frozen suite differs from its approved definition.');
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
    frozenSuite(o,true);
    // One explicit operator approval. At the next agent restart, even a
    // private preexisting Courier adapter can recognize these exact,
    // immutable local evaluation paths without editing its source.
    const approval=JSON.stringify({schema_version:1,
        config_sha256:o.hash,suite_id:o.suiteId},null,2)+'\n';
    if(fs.existsSync(o.approvalFile)) {
        const stat=fs.lstatSync(o.approvalFile);
        if(!stat.isFile()||stat.isSymbolicLink()||
            stat.uid!==process.getuid()||stat.mode&0o077)
            throw Error('Existing Courier evaluation approval is not a private regular file.');
        const current=JSON.parse(fs.readFileSync(o.approvalFile,'utf8'));
        if(current.schema_version!==1)
            throw Error('Unsupported Courier evaluator approval version.');
        if(!SHA.test(String(current.config_sha256||''))||
            current.suite_id!=='courier_survival_'+current.config_sha256.slice(0,12))
            throw Error('Existing Courier evaluator approval is malformed.');
        if(current.config_sha256!==o.hash) {
            // A new environment needs a new immutable suite. Stop the old
            // schedule without interrupting a running evaluation or training.
            const oldTimer='bees-experiment-hub-courier-'+o.machine+
                '-evaluator-survival_'+current.config_sha256.slice(0,8)+'.timer';
            await systemctl(['stop',oldTimer]);
            const temporary=o.approvalFile+'.new';
            fs.writeFileSync(temporary,approval,{flag:'wx',mode:0o600});
            fs.renameSync(temporary,o.approvalFile);
        }else if(current.suite_id!==o.suiteId)
            throw Error('An existing Courier suite approval differs from this environment.');
    }else fs.writeFileSync(o.approvalFile,approval,{flag:'wx',mode:0o600});
    const def=definition(o),unit=unitPaths(o);
    if(fs.existsSync(unit.serviceFile)||fs.existsSync(unit.timerFile))
        approvedUnits(o,def);
    else installEvaluatorUnits(def,o.home);
    await systemctl(['daemon-reload']);
    await systemctl(['start',unit.timer]);
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
