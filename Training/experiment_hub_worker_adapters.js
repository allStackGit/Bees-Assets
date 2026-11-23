'use strict';

// Trusted worker adapter map for one enrolled Experiment Hub machine.
// The installer supplies only absolute local runtime/secret paths; the Hub
// never supplies executable paths, network listener ports, or HMAC identities.
// Both projects share the machine's native CPU/RAM ledger, not their secrets.
const fs=require('node:fs');
const path=require('node:path');
const {spawnSync}=require('node:child_process');
const {fileHash}=require('./experiment_hub_bees_immutable_runtime');
const crypto=require('node:crypto');
const {createTinyBrainsAdapter}=require('./experiment_hub_tinybrains_adapter');
const {createCourierAdapter}=require('./experiment_hub_courier_adapter');
const {createBeesNativeAdapter}=require('./experiment_hub_bees_native_adapter');
const {createBeesRolloutAdapter}=require('./experiment_hub_bees_rollout_adapter');
const {createTinyBrainsRemoteRolloutAdapter}=
    require('./experiment_hub_tinybrains_remote_adapter');
const {createCourierRemoteLineageAdapter}=
    require('./experiment_hub_courier_remote_adapter');

function required(name) {
    const value=process.env[name];
    if(typeof value!=='string'||!value||/[\r\n]/.test(value))
        throw Error('Configure '+name+' in the private worker environment.');
    return value;
}
function enabled(name) {
    const value=process.env.EXPERIMENT_HUB_WORKER_PROJECTS||'';
    return value.split(',').includes(name);
}
function port(name,defaultValue) {
    const raw=process.env[name];
    if(raw===undefined)return defaultValue;
    const value=Number(raw);
    if(!Number.isSafeInteger(value)||value<1024||value>65535)
        throw Error(name+' must be a fixed, private unprivileged port.');
    return value;
}
function listenerPorts(prefix,defaults) {
    const values=defaults.map((fallback,i)=>port(prefix+'_PORT_'+i,fallback));
    if(new Set(values).size!==values.length)
        throw Error(prefix+' listeners must use unique ports.');
    return Object.fromEntries(values.map((value,index)=>[index,value]));
}
const projects=(process.env.EXPERIMENT_HUB_WORKER_PROJECTS||'').split(',').filter(Boolean);
if(projects.length>4||
    projects.some(p=>!['tinybrains','courier','bees_native','bees_rollout'].includes(p))||
    new Set(projects).size!==projects.length)
    throw Error('Installed simulation selection must contain only TinyBrains, Courier or Bees native.');
function artifactDirectory(name) {
    const root=path.join(required('EXPERIMENT_HUB_LOCAL_ROOT'),'artifacts',name);
    fs.mkdirSync(root,{recursive:true,mode:0o700});
    return root;
}
function tinyBrainsCpuProfiles(project,python) {
    // Only reviewed, versioned CPU configurations are approved here.
    // Existing nonportable / historical run configurations are excluded.
    const files={mini:'mini.yaml',reference:'reference.yaml',smoke_mini:'smoke-mini.yaml'};
    const profiles={};
    for(const [name,file] of Object.entries(files)) {
        const source=path.join(project,'configs',file);
        if(!fs.statSync(source).isFile())throw Error('Missing pinned TinyBrains profile '+file);
        const script='import json,sys,yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1],encoding="utf-8"))))';
        const read=spawnSync(python,['-B','-c',script,source],{
            cwd:project,encoding:'utf8',timeout:15000,maxBuffer:1024*1024,windowsHide:true});
        if(read.status!==0||read.error)
            throw Error('Cannot read pinned TinyBrains profile '+file);
        const config=JSON.parse(read.stdout);
        if(config?.runtime?.device!=='cpu')
            throw Error('Distributed CPU worker may not approve a GPU profile.');
        profiles[name]=config;
    }
    return profiles;
}
// Activation is performed separately by the trusted local Unity installer.
// An agent reads the selected immutable release only on process startup;
// running jobs retain their original executable paths and source identities.
function installedUnityRelease() {
    const configured=process.env.EXPERIMENT_HUB_BEES_WORKER_INSTALL_ROOT;
    if(!configured)return null;
    const SHA=/^[0-9a-f]{64}$/;
    const root=path.resolve(configured);
    if(!path.isAbsolute(configured)||fs.realpathSync(root)!==root||
        !fs.statSync(root).isDirectory())
        throw Error('Hub Unity release root must be a real installed absolute directory.');
    const marker=path.join(root,'current.json');
    if(fs.realpathSync(marker)!==marker||fs.statSync(marker).size>4096)
        throw Error('Hub Unity release needs a locally activated current.json receipt.');
    const active=JSON.parse(fs.readFileSync(marker,'utf8'));
    const platform=process.platform==='win32'?'WindowsPlayer':'LinuxPlayer';
    const folder=process.platform==='win32'?'Windows':'Linux';
    const entry=process.platform==='win32'?'Bees RL Training.exe':'Bees RL Training.x86_64';
    if(!active||active.schema_version!==1||
        !SHA.test(String(active.release_sha256||''))||
        !SHA.test(String(active.player_tree_sha256||''))||
        active.platform!==platform)
        throw Error('Selected Bees Unity release has an invalid platform-specific activation receipt.');
    const release=path.join(root,'releases',active.release_sha256);
    const manifest=path.join(release,'worker-build-manifest.json');
    const binary=path.join(release,folder,entry);
    if(fs.realpathSync(release)!==release||
        fs.realpathSync(manifest)!==manifest||
        fs.realpathSync(binary)!==binary)
        throw Error('Selected Bees Unity worker release contains redirected files.');
    const bytes=fs.readFileSync(manifest);
    if(crypto.createHash('sha256').update(bytes).digest('hex')!==
        active.release_sha256)
        throw Error('Selected Bees Unity worker manifest is not its pinned release.');
    const data=JSON.parse(bytes.toString('utf8'));
    const record=data?.artifacts?.find(item=>item.platform===platform);
    if(data?.schema_version!==1||data.kind!=='offline-unity-workers'||
        data.build_id!==active.build_id||
        record?.player_tree_sha256!==active.player_tree_sha256||
        record?.entrypoint!==entry)
        throw Error('Selected Bees Unity worker release and activation receipt disagree.');
    const runtime=data.runtime;
    if(runtime!==undefined&&(
        !runtime||typeof runtime!=='object'||Array.isArray(runtime)||
        Object.keys(runtime).sort().join(',')!==
            ['archive','archive_bytes','archive_sha256','runtime_version'].join(',')||
        runtime.archive!=='bees-runtime.zip'||
        !SHA.test(String(runtime.archive_sha256||''))||
        !SHA.test(String(runtime.runtime_version||''))||
        !Number.isSafeInteger(runtime.archive_bytes)||
        runtime.archive_bytes<1||runtime.archive_bytes>512*1024*1024))
        throw Error('Selected Bees Unity release has an invalid immutable Python runtime receipt.');
    const runtimeArchive=runtime?path.join(release,runtime.archive):null;
    if(runtime&&(
        fs.realpathSync(runtimeArchive)!==runtimeArchive||
        fs.statSync(runtimeArchive).size!==runtime.archive_bytes||
        fileHash(runtimeArchive)!==runtime.archive_sha256))
        throw Error('Selected Bees Python learner/actor archive differs from the activated Unity release.');
    return Object.freeze({
        workerExecutable:binary,releaseManifestPath:manifest,
        runtimeArchive,
        runtimeSha256:runtime?.archive_sha256??null,
        runtimeVersion:runtime?.runtime_version??null,
        runtimeBuildId:runtime?data.build_id:null,
    });
}
const approvedBeesRelease=
    enabled('bees_native')||enabled('bees_rollout')?installedUnityRelease():null;
const beesWorker=()=>{
    if(approvedBeesRelease)return approvedBeesRelease.workerExecutable;
    return required(process.platform==='win32'
        ?'EXPERIMENT_HUB_BEES_WINDOWS_WORKER':'EXPERIMENT_HUB_BEES_LINUX_WORKER');
};
const beesManifest=()=>approvedBeesRelease?.releaseManifestPath||
    process.env.EXPERIMENT_HUB_BEES_WORKER_MANIFEST||null;
// The enrolled Hub worker software release already carries Training/ and
// its trusted runtime installer. A fully installed source cartridge therefore
// needs no duplicate project checkout on remote simulation machines.
const beesProject=()=>process.env.EXPERIMENT_HUB_BEES_ASSETS_ROOT?
    required('EXPERIMENT_HUB_BEES_ASSETS_ROOT'):
    approvedBeesRelease?.runtimeArchive?path.dirname(__dirname):
    required('EXPERIMENT_HUB_BEES_ASSETS_ROOT');
const beesTrainingArchive=()=>approvedBeesRelease&&approvedBeesRelease.runtimeArchive?
    {
        runtimeArchive:approvedBeesRelease.runtimeArchive,
        runtimeSha256:approvedBeesRelease.runtimeSha256,
        runtimeVersion:approvedBeesRelease.runtimeVersion,
        runtimeBuildId:approvedBeesRelease.runtimeBuildId,
    }:{};

const adapters={};
if(enabled('tinybrains')) {
    const project=required('EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT');
    const python=required('EXPERIMENT_HUB_TINYBRAINS_PYTHON');
    const engine=path.join(project,'tools','tinybrains-0.5.0',
        process.platform==='win32'?'tinybrains.exe':'tinybrains');
    adapters.tinybrains=createTinyBrainsAdapter({
        projectRoot:project,pythonPath:python,enginePath:engine,
        artifactRoot:artifactDirectory('tinybrains'),
        profiles:tinyBrainsCpuProfiles(project,python),
    });
    const slots=listenerPorts('EXPERIMENT_HUB_TINYBRAINS',[12134,12135]);
    adapters.tinybrains_rollout=createTinyBrainsRemoteRolloutAdapter({
        projectRoot:required('EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT'),
        pythonPath:required('EXPERIMENT_HUB_TINYBRAINS_PYTHON'),
        keyPath:required('EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY'),
        cacheRoot:required('EXPERIMENT_HUB_TINYBRAINS_CACHE'),
        listenIp:required('EXPERIMENT_HUB_TAILNET_IP'),
        port:slots[0],slotPorts:slots,
        threads:Number(required('EXPERIMENT_HUB_TINYBRAINS_THREAD_LIMIT')),
        enableCuda:process.platform==='win32'&&
            process.env.EXPERIMENT_HUB_TINYBRAINS_CUDA==='1',
        enableRocm:false,
    });
}
if(enabled('courier')) {
    const project=required('EXPERIMENT_HUB_COURIER_PROJECT_ROOT');
    adapters.courier=createCourierAdapter({
        projectRoot:project,
        pythonPath:required('EXPERIMENT_HUB_COURIER_PYTHON'),
        artifactRoot:artifactDirectory('courier'),
        configPath:path.join(project,'environment.json'),
        cpuParallelism:'allocation',
    });
    const slots=listenerPorts('EXPERIMENT_HUB_COURIER',[13134,13135]);
    adapters.courier_lineage=createCourierRemoteLineageAdapter({
        projectRoot:required('EXPERIMENT_HUB_COURIER_PROJECT_ROOT'),
        pythonPath:required('EXPERIMENT_HUB_COURIER_PYTHON'),
        keyPath:required('EXPERIMENT_HUB_COURIER_ROLLOUT_KEY'),
        cacheRoot:required('EXPERIMENT_HUB_COURIER_CACHE'),
        listenIp:required('EXPERIMENT_HUB_TAILNET_IP'),
        port:slots[0],slotPorts:slots,
        enableCuda:false,enableRocm:false,
    });
}
if(enabled('bees_native')) {
    adapters.bees_native=createBeesNativeAdapter({
        projectRoot:beesProject(),
        pythonPath:required('EXPERIMENT_HUB_BEES_PYTHON'),
        workerExecutable:beesWorker(),
        artifactRoot:artifactDirectory('bees_native'),
        wanKeyPath:process.env.EXPERIMENT_HUB_BEES_WAN_KEY||null,
        wanBrokerPort:Number(process.env.EXPERIMENT_HUB_BEES_WAN_BROKER_PORT||55051),
        releaseManifestPath:beesManifest(),
        ...beesTrainingArchive(),
    });
}
if(enabled('bees_rollout')) {
    const sshKeys=['EXPERIMENT_HUB_BEES_WAN_SSH_USER',
        'EXPERIMENT_HUB_BEES_WAN_SSH_TAILNET_IP',
        'EXPERIMENT_HUB_BEES_WAN_SSH_IDENTITY_FILE',
        'EXPERIMENT_HUB_BEES_WAN_SSH_KNOWN_HOSTS_FILE',
        'EXPERIMENT_HUB_BEES_WAN_LEARNER_MACHINE'];
    const sshEnabled=sshKeys.some(key=>process.env[key]!==undefined);
    if(sshEnabled&&sshKeys.some(key=>!process.env[key]))
        throw Error('Bees managed WAN SSH forwarding needs all five locally enrolled learner and credential settings.');
    const sshPortRaw=process.env.EXPERIMENT_HUB_BEES_WAN_SSH_PORT??'22';
    const sshPort=Number(sshPortRaw);
    if(!/^[1-9][0-9]*$/.test(sshPortRaw)||
        !Number.isSafeInteger(sshPort)||sshPort>65535)
        throw Error('Bees WAN SSH listener needs a locally approved port 1–65535.');
    adapters.bees_rollout=createBeesRolloutAdapter({
        projectRoot:beesProject(),
        pythonPath:required('EXPERIMENT_HUB_BEES_PYTHON'),
        workerExecutable:beesWorker(),
        artifactRoot:artifactDirectory('bees_rollout'),
        keyPath:required('EXPERIMENT_HUB_BEES_WAN_KEY'),
        forwardPort:Number(required('EXPERIMENT_HUB_BEES_WAN_FORWARD_PORT')),
        releaseManifestPath:beesManifest(),
        ...beesTrainingArchive(),
        sshForward:sshEnabled?{
            learnerMachineId:required('EXPERIMENT_HUB_BEES_WAN_LEARNER_MACHINE'),
            tailnetIp:required('EXPERIMENT_HUB_BEES_WAN_SSH_TAILNET_IP'),
            sshUser:required('EXPERIMENT_HUB_BEES_WAN_SSH_USER'),
            identityFile:required('EXPERIMENT_HUB_BEES_WAN_SSH_IDENTITY_FILE'),
            knownHostsFile:required('EXPERIMENT_HUB_BEES_WAN_SSH_KNOWN_HOSTS_FILE'),
            sshPort,
            ...(process.env.EXPERIMENT_HUB_BEES_WAN_SSH_EXECUTABLE?{
                sshExecutable:required('EXPERIMENT_HUB_BEES_WAN_SSH_EXECUTABLE'),
            }:{}),
            brokerPort:port('EXPERIMENT_HUB_BEES_WAN_BROKER_PORT',55051),
        }:null,
    });
}
module.exports=adapters;
