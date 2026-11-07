#!/usr/bin/env node
'use strict';

// Generate an OS-owned user service, never a second training supervisor.
// Printing is the default. --install writes only an absent unit file and
// deliberately neither enables nor starts it.
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {parseArgs}=require('node:util');

const NAMES={
    coordinator:'bees-experiment-hub.service',
    agent:'bees-experiment-hub-agent.service',
};
// Reject systemd specifiers, shell expansion, whitespace and escapes rather
// than pretending a path containing them is safely quoted in a unit file.
const UNIT_PATH=/^\/[A-Za-z0-9._/@:+-]+$/;
function localPath(input,kind) {
    if(typeof input!=='string'||!path.isAbsolute(input))
        throw Error(kind+' must be an absolute path.');
    const canonical=fs.realpathSync(input);
    if(!UNIT_PATH.test(canonical)||canonical.split('/').includes('..'))
        throw Error(kind+' contains characters that are unsafe in systemd units.');
    const stat=fs.statSync(canonical);
    if(kind==='Training directory'? !stat.isDirectory():!stat.isFile())
        throw Error(kind+' is not a '+(kind==='Training directory'?'directory.':'regular file.'));
    return canonical;
}
function serviceDefinition(kind,{trainingDir=__dirname,nodePath=process.execPath,
    environmentFile=null}={}) {
    if(process.platform!=='linux')
        throw Error('User systemd service generation is supported on Linux only.');
    if(!Object.hasOwn(NAMES,kind))
        throw Error('Select coordinator or agent.');
    const directory=localPath(trainingDir,'Training directory');
    const node=localPath(nodePath,'Node executable');
    const entry=localPath(path.join(directory,kind==='coordinator'
        ?'bees_experiments.js':'experiment_hub_agent.js'),'Service entrypoint');
    let envLine='';
    if(kind==='agent'&&!environmentFile)
        throw Error('Agent service needs --env-file containing its machine ID and enrollment token.');
    if(environmentFile) {
        // The machine administrator owns this file, not the checkout.
        if(fs.lstatSync(environmentFile).isSymbolicLink())
            throw Error('Service environment file must not be a symbolic link.');
        const file=localPath(environmentFile,'Service environment file');
        const stat=fs.statSync(file);
        if(stat.uid!==process.getuid()||(stat.mode&0o077)!==0)
            throw Error('Service environment file must be owned by this user and inaccessible to group/others.');
        envLine='EnvironmentFile='+file+'\n';
    }
    const title=kind==='coordinator'
        ?'Bees experiment coordinator':'Experiment Hub native machine agent';
    // The Bees operator owns its training children, not the coordinator
    // service. Stop/restart must not kill an independently running learner.
    const killMode=kind==='coordinator'?'process':'control-group';
    return [
        '[Unit]',
        'Description='+title,
        'After=network.target',
        'StartLimitIntervalSec=300',
        'StartLimitBurst=5',
        '',
        '[Service]',
        'Type=exec',
        'WorkingDirectory='+directory,
        'ExecStart='+node+' '+entry,
        ...(envLine?[envLine.trimEnd()]:[]),
        'UMask=0077',
        'Restart=on-failure',
        'RestartSec=15s',
        'TimeoutStopSec=30s',
        'KillMode='+killMode,
        '',
        '[Install]',
        'WantedBy=default.target',
        '',
    ].join('\n');
}
function installUnit(kind,content,home=os.homedir()) {
    const dir=path.join(home,'.config','systemd','user');
    fs.mkdirSync(dir,{recursive:true,mode:0o700});
    const file=path.join(dir,NAMES[kind]);
    fs.writeFileSync(file,content,{encoding:'utf8',flag:'wx',mode:0o600});
    return file;
}
// A periodic evaluator is a separately budgeted, one-shot systemd service.
// The timer never overlaps a still-running invocation of the same unit.
function positiveInt(input,min,max,label) {
    if(typeof input!=='string'||!/^[0-9]+$/.test(input))throw Error(label+' must be an integer.');
    const value=Number(input);
    if(!Number.isSafeInteger(value)||value<min||value>max)
        throw Error(label+' must be between '+min+' and '+max+'.');
    return value;
}
function evaluatorDefinition({
    project,machine,trainingDir=__dirname,pythonPath,port='7148',suites,
    projectRoot,artifactRoot,snapshotRoot,scratch,receiptRoot,enginePath,
    cpus,ramMb,interval='180',timeout='7200',
}={}) {
    if(process.platform!=='linux')throw Error('Evaluator timers are Linux-only.');
    if(!['courier','tinybrains'].includes(project)||!/^[a-z][a-z0-9_-]{0,47}$/.test(machine||''))
        throw Error('Select a supported evaluator project and registered machine ID.');
    if(!Array.isArray(suites)||suites.length<1||suites.length>8||
        new Set(suites).size!==suites.length)
        throw Error('Supply 1–8 distinct --suite files for a coordinated benchmark batch.');
    const directory=localPath(trainingDir,'Training directory');
    const python=localPath(pythonPath,'Python executable');
    const script=localPath(path.join(directory,
        'experiment_hub_'+project+'_eval_poll.py'),'Evaluator entrypoint');
    const args=['-B',script,'--machine',machine,'--port',
        String(positiveInt(port,1,65535,'Port'))];
    for(const suite of suites)
        args.push('--suite',localPath(suite,'Benchmark suite'));
    if(project==='courier') {
        args.push('--project-root',localPath(projectRoot,'Courier project'));
        args.push('--snapshot-root',localPath(snapshotRoot,'Courier snapshots'));
    }else {
        args.push('--engine-path',localPath(enginePath,'TinyBrains engine'));
    }
    args.push('--artifact-root',localPath(artifactRoot,'Training artifacts'),
        '--scratch',localPath(scratch,'Evaluator scratch'),
        '--receipt-root',localPath(receiptRoot,'Evaluator receipts'));
    const cpuBudget=positiveInt(cpus,1,64,'Evaluator CPU quota');
    const memoryBudget=positiveInt(ramMb,512,1048576,'Evaluator RAM MiB');
    const period=positiveInt(interval,60,86400,'Evaluator interval seconds');
    const timeoutSeconds=positiveInt(timeout,60,86400,'Evaluator timeout seconds');
    const name='bees-experiment-hub-'+project+'-'+machine+'-evaluator';
    const service=[
        '[Unit]','Description=Independent '+project+' evaluation for '+machine,
        'After=network.target','',
        '[Service]','Type=oneshot','WorkingDirectory='+directory,
        'ExecStart='+[python,...args].join(' '),
        'CPUQuota='+(cpuBudget*100)+'%',
        'MemoryMax='+memoryBudget+'M',
        'Nice=10','UMask=0077',
        'TimeoutStartSec='+timeoutSeconds+'s',
        '',
    ].join('\n');
    const timer=[
        '[Unit]','Description=Schedule '+project+' evaluation for '+machine,'',
        '[Timer]','OnStartupSec=120s','OnUnitInactiveSec='+period+'s',
        'AccuracySec=15s','Unit='+name+'.service','',
        '[Install]','WantedBy=timers.target','',
    ].join('\n');
    return {name,service,timer};
}
function installEvaluatorUnits(result,home=os.homedir()) {
    const dir=path.join(home,'.config','systemd','user');
    fs.mkdirSync(dir,{recursive:true,mode:0o700});
    const service=path.join(dir,result.name+'.service');
    const timer=path.join(dir,result.name+'.timer');
    if(fs.existsSync(service)||fs.existsSync(timer))
        throw Error('Evaluator unit already exists; review and reconcile it manually.');
    fs.writeFileSync(service,result.service,{encoding:'utf8',flag:'wx',mode:0o600});
    try {
        fs.writeFileSync(timer,result.timer,{encoding:'utf8',flag:'wx',mode:0o600});
    }catch(error) {
        fs.unlinkSync(service);
        throw error;
    }
    return {service,timer};
}
function main(argv=process.argv.slice(2)) {
    const {values,positionals}=parseArgs({args:argv,allowPositionals:true,
        options:{
            'training-dir':{type:'string'},
            'node':{type:'string'},
            'env-file':{type:'string'},
            'install':{type:'boolean'},
            'project':{type:'string'},
            'machine':{type:'string'},
            'python':{type:'string'},
            'port':{type:'string'},
            'suite':{type:'string',multiple:true},
            'project-root':{type:'string'},
            'artifact-root':{type:'string'},
            'snapshot-root':{type:'string'},
            'scratch':{type:'string'},
            'receipt-root':{type:'string'},
            'engine-path':{type:'string'},
            'cpus':{type:'string'},
            'ram-mb':{type:'string'},
            'interval':{type:'string'},
            'timeout':{type:'string'},
        }});
    if(positionals.length!==1||
        !(Object.hasOwn(NAMES,positionals[0])||positionals[0]==='evaluator'))
        throw Error('Usage: node Training/experiment_hub_systemd.js coordinator|agent|evaluator [options] [--install]');
    const kind=positionals[0];
    if(kind==='evaluator') {
        const result=evaluatorDefinition({
            project:values.project,machine:values.machine,
            trainingDir:values['training-dir']||__dirname,
            pythonPath:values.python,port:values.port||'7148',
            suites:values.suite,projectRoot:values['project-root'],
            artifactRoot:values['artifact-root'],snapshotRoot:values['snapshot-root'],
            scratch:values.scratch,receiptRoot:values['receipt-root'],
            enginePath:values['engine-path'],
            cpus:values.cpus,ramMb:values['ram-mb'],
            interval:values.interval||'180',timeout:values.timeout||'7200',
        });
        if(!values.install) {
            process.stdout.write('# '+result.name+'.service\n'+result.service+
                '# '+result.name+'.timer\n'+result.timer);
            return;
        }
        const installed=installEvaluatorUnits(result);
        console.log('Installed (not enabled): '+installed.service+' and '+installed.timer);
        console.log('Review the units, then run: systemctl --user daemon-reload && systemctl --user enable --now '+result.name+'.timer');
        return;
    }
    const unit=serviceDefinition(kind,{
        trainingDir:values['training-dir']||__dirname,
        nodePath:values.node||process.execPath,
        environmentFile:values['env-file']||null,
    });
    if(!values.install) {
        process.stdout.write(unit);
        return;
    }
    const filename=installUnit(kind,unit);
    console.log('Installed (not started): '+filename);
    console.log('Review the unit, then run: systemctl --user daemon-reload && systemctl --user enable --now '+NAMES[kind]);
}
if(require.main===module) {
    try {main();}
    catch(error){console.error(error.message);process.exitCode=1;}
}
module.exports={serviceDefinition,installUnit,evaluatorDefinition,installEvaluatorUnits,main};
