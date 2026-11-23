#!/usr/bin/env node
'use strict';

// Compile Unity policy AssetBundles on Halifax without needing a live Bees
// learner, deployment store or Windows Unity Editor. This is NOT promotion:
// the current champion publisher verifies the returned sidecars separately.
const crypto=require('node:crypto');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {spawn,spawnSync}=require('node:child_process');
const {
    paths,exists,resolveUnityEditor,getGitShortSha,
} =require('./operator/common');
const {assertUnityProjectAvailableForBatchBuild}=require('./operator/build');

const PLATFORMS=Object.freeze({
    linux:{platform:'LinuxPlayer',target:'StandaloneLinux64',folder:'Linux'},
    windows:{platform:'WindowsPlayer',target:'StandaloneWindows64',folder:'Windows'},
});
const DEPLOYMENT=path.join(paths.assetsRoot,'Resources','RlPolicy',
    'BeesRL1v1Deployment.json');
const MODEL=path.join(paths.assetsRoot,'Resources','RlPolicy','BeesRL1v1.onnx');
const SHA=/^[a-f0-9]{64}$/;
const IDENT=/^[a-zA-Z0-9._-]{1,128}$/;

function usage() {
    return 'Usage: node Training/bees_build_hot_bundles.js '+
        '[--platform linux|windows|both] [--unity-editor /absolute/path/to/Unity]\n'+
        'Requires an approved BeesRL1v1.onnx and matching BeesRL1v1Deployment.json '+
        'already staged in Assets/Resources/RlPolicy. Produces offline sidecars only.\n'+
        'Does not change deployment, running Bees jobs, checkpoint or remote worker state.';
}
function options(args) {
    const result={platform:'both',editor:''};
    for(let i=0;i<args.length;i++) {
        const option=args[i];
        if(option==='-h'||option==='--help')return {help:true};
        if(option==='--platform'&&args[i+1]) {
            const value=args[++i];
            if(!['linux','windows','both'].includes(value))
                throw Error('Unsupported hot-bundle platform: '+value);
            result.platform=value;
        }else if(option==='--unity-editor'&&args[i+1]) {
            const value=args[++i];
            if(!path.isAbsolute(value))
                throw Error('Unity executable must have an absolute path.');
            result.editor=value;
        }else throw Error('Invalid hot-bundle build argument: '+option);
    }
    return result;
}
async function sha256(file) {
    const digest=crypto.createHash('sha256');
    for await(const data of fs.createReadStream(file))digest.update(data);
    return digest.digest('hex');
}
function existingFile(file,maxBytes) {
    const stat=fs.lstatSync(file);
    if(!stat.isFile()||stat.isSymbolicLink()||
        fs.realpathSync(file)!==file||stat.size<1||stat.size>maxBytes)
        throw Error('Invalid staged Unity bundle input: '+file);
    return file;
}
function dirtySource() {
    const status=spawnSync('git',['status','--porcelain','--untracked-files=normal'],
        {cwd:paths.assetsRoot,encoding:'utf8',timeout:30000,maxBuffer:2*1024*1024});
    if(status.error||status.status!==0)
        throw Error('Cannot identify Unity source state before offline bundle build.');
    return Boolean(status.stdout.trim());
}
function validateIdentity(manifest,modelSha,modelSize) {
    const identity=manifest?.identity;
    if(manifest?.schema_version!==1||
        !IDENT.test(String(manifest.deployment_id||''))||
        !SHA.test(String(manifest.identity_sha256||''))||
        manifest.model_file!=='model.onnx'||
        identity?.schema_version!==1||
        identity.behavior_name!=='BeesRL1v1'||
        !IDENT.test(String(identity.model_id||''))||
        identity.model_sha256!==modelSha||
        identity.model_size_bytes!==modelSize||
        !Number.isSafeInteger(identity.compatibility?.policy_abi_version)||
        identity.compatibility.policy_abi_version<1||
        typeof identity.policy_signature!=='string'||
        !identity.policy_signature.startsWith('bees-rl-v')||
        identity.policy_signature.length>12000)
        throw Error('Staged Unity policy is missing its matching authorized deployment identity.');
    return identity;
}
function runUnity(editor,platform,output,log) {
    const argv=[
        '-batchmode','-quit','-nographics',
        '-projectPath',paths.beesRoot,
        '-buildTarget',platform.target,
        '-executeMethod','RlLivePolicyHotBundleBuilder.BuildFromCommandLine',
        '-logFile',log,
        '--bees-rl-hot-bundle-output='+output,
    ];
    return new Promise((resolve,reject)=>{
        const child=spawn(editor,argv,{
            cwd:paths.beesRoot,stdio:'ignore',windowsHide:true,
        });
        child.once('error',reject);
        child.once('exit',(code,signal)=>{
            if(code===0&&signal===null)resolve();
            else reject(Error('Unity '+platform.platform+
                ' hot-bundle build failed (exit='+code+', signal='+signal+
                '). Inspect '+log));
        });
    });
}
async function build(args=process.argv.slice(2)) {
    const opts=options(args);
    if(opts.help){console.log(usage());return null;}
    const versionFile=existingFile(path.join(paths.beesRoot,'ProjectSettings',
        'ProjectVersion.txt'),4096);
    const packages=existingFile(path.join(paths.beesRoot,'Packages','manifest.json'),
        1024*1024);
    existingFile(path.join(paths.assetsRoot,'Editor',
        'RlLivePolicyHotBundleBuilder.cs'),1024*1024);
    existingFile(MODEL,1024*1024*1024);
    existingFile(DEPLOYMENT,32768);
    assertUnityProjectAvailableForBatchBuild();

    const versionText=fs.readFileSync(versionFile,'utf8');
    const version=/^m_EditorVersion:\s*(\S+)/m.exec(versionText)?.[1];
    if(!version)throw Error('Missing authoritative Unity project version.');
    const sourceCommit=getGitShortSha();
    const initialDirty=dirtySource();
    const modelSha=await sha256(MODEL);
    const modelSize=fs.statSync(MODEL).size;
    const deploymentSha=await sha256(DEPLOYMENT);
    const packageSha=await sha256(packages);
    const deployment=JSON.parse(fs.readFileSync(DEPLOYMENT,'utf8'));
    const identity=validateIdentity(deployment,modelSha,modelSize);
    const editor=resolveUnityEditor({unityEditor:opts.editor});
    const desired=opts.platform==='both'
        ?[PLATFORMS.linux,PLATFORMS.windows]
        :[PLATFORMS[opts.platform]];
    const buildId='offline-hot-'+new Date().toISOString()
        .replace(/[-:]/g,'').replace(/\.\d{3}Z$/,'Z')+'-'+
        sourceCommit+'-'+crypto.randomBytes(3).toString('hex');
    const root=path.join(paths.buildsRoot,'OfflineHotBundles',buildId);
    const logs=path.join(paths.logsRoot,'Build');
    fs.mkdirSync(root,{recursive:true,mode:0o700});
    fs.mkdirSync(logs,{recursive:true});

    const built=[];
    for(const platform of desired) {
        const output=path.join(root,platform.folder);
        fs.mkdirSync(output,{mode:0o700});
        const log=path.join(logs,buildId+'-'+platform.folder.toLowerCase()+'.log');
        console.log('Unity offline hot bundle: '+platform.platform);
        await runUnity(editor,platform,output,log);
        const bundle=existingFile(path.join(output,'bees-rl-policy.bundle'),
            1024*1024*1024);
        const metadataFile=existingFile(path.join(output,
            'bees-rl-policy.metadata.json'),32768);
        const metadata=JSON.parse(fs.readFileSync(metadataFile,'utf8'));
        const bundleSha=await sha256(bundle);
        const bundleSize=fs.statSync(bundle).size;
        if(metadata.schema_version!==1||
            metadata.platform!==platform.platform||
            metadata.build_target!==platform.target||
            metadata.unity_version!==version||
            metadata.deployment_id!==deployment.deployment_id||
            metadata.model_id!==identity.model_id||
            metadata.model_sha256!==modelSha||
            metadata.manifest_sha256!==deploymentSha||
            metadata.policy_abi_version!==identity.compatibility.policy_abi_version||
            metadata.policy_signature!==identity.policy_signature||
            metadata.bundle_file!=='bees-rl-policy.bundle'||
            metadata.bundle_sha256!==bundleSha||
            metadata.bundle_size_bytes!==bundleSize||
            metadata.model_address!=='BeesRL1v1'||
            metadata.manifest_address!=='BeesRL1v1Deployment')
            throw Error('Unity hot-bundle metadata does not match the staged policy: '+
                platform.platform);
        built.push({
            platform:platform.platform,build_target:platform.target,
            folder:platform.folder,bundle:'bees-rl-policy.bundle',
            metadata:'bees-rl-policy.metadata.json',
            bundle_sha256:bundleSha,bundle_bytes:bundleSize,
            metadata_sha256:await sha256(metadataFile),
        });
    }

    if(getGitShortSha()!==sourceCommit||dirtySource()!==initialDirty||
        await sha256(MODEL)!==modelSha||
        await sha256(DEPLOYMENT)!==deploymentSha||
        await sha256(packages)!==packageSha)
        throw Error('Unity source, model, deployment manifest or packages changed during compilation.');
    const manifest={
        schema_version:1,kind:'offline-unity-policy-hot-bundles',
        deployment_status:'unpublished',
        build_id:buildId,source_commit:sourceCommit,source_dirty:initialDirty,
        unity_version:version,created_utc:new Date().toISOString(),
        deployment_id:deployment.deployment_id,
        model_id:identity.model_id,model_sha256:modelSha,
        deployment_manifest_sha256:deploymentSha,
        packages_manifest_sha256:packageSha,
        artifacts:built,
    };
    const outputFile=path.join(root,'hot-bundle-build-manifest.json');
    fs.writeFileSync(outputFile,JSON.stringify(manifest,null,2)+os.EOL,
        {encoding:'utf8',flag:'wx',mode:0o600});
    console.log('Offline hot bundles built: '+outputFile);
    console.log('No live deployment or policy promotion was performed.');
    return manifest;
}
if(require.main===module) {
    build().catch(error=>{
        console.error(error?.stack||String(error));
        process.exitCode=1;
    });
}
module.exports={build,options,validateIdentity,PLATFORMS};
