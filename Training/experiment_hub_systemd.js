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
function main(argv=process.argv.slice(2)) {
    const {values,positionals}=parseArgs({args:argv,allowPositionals:true,
        options:{
            'training-dir':{type:'string'},
            'node':{type:'string'},
            'env-file':{type:'string'},
            'install':{type:'boolean'},
        }});
    if(positionals.length!==1||!Object.hasOwn(NAMES,positionals[0]))
        throw Error('Usage: node Training/experiment_hub_systemd.js coordinator|agent [--training-dir /absolute/path] [--node /absolute/node] [--env-file /private/owner-only.env] [--install]');
    const kind=positionals[0];
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
module.exports={serviceDefinition,installUnit,main};
