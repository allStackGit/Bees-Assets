'use strict';

// Machine-owned Bees WAN actors participate in Hub CPU/GPU reservations but
// never own PPO updates, the optimizer, policy promotion or checkpoints.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const {prepare: prepareImmutableRuntime,fileHash}=
    require('./experiment_hub_bees_immutable_runtime');
const {privateKey,brokerPort,identity}=require('./experiment_hub_bees_wan_identity');
const {unityWorkerTreeSha256}=require('./experiment_hub_bees_worker_identity');

const UUID=/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/;
const SHA=/^[0-9a-f]{64}$/;
const TAILNET=/^100\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.(?:0|[1-9][0-9]{0,2})\.(?:0|[1-9][0-9]{0,2})$/;
const SSH_USER=/^[a-z_][a-z0-9_-]{0,31}$/;
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const SAFE=/^[a-zA-Z0-9_./:@=+\-,]+$/;

function trustedFile(raw,label,executable=false) {
    if(typeof raw!=='string'||!path.isAbsolute(raw)||!SAFE.test(raw))
        throw Error(label+' must be an approved absolute Linux path.');
    const real=fs.realpathSync(raw);
    if(real!==raw||!SAFE.test(real))
        throw Error(label+' may not be redirected.');
    const st=fs.statSync(raw);
    if(!st.isFile()||executable&&(st.mode&0o111)===0)
        throw Error(label+' is not a usable installed file.');
    return real;
}
function createBeesRolloutAdapter({
    projectRoot,pythonPath,workerExecutable,artifactRoot,
    keyPath,forwardPort,sshForward=null,
}) {
    if(process.platform!=='linux')
        throw Error('This Bees Hub remote actor adapter uses the Linux systemd owner.');
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    if(!SAFE.test(project)||!SAFE.test(artifacts)||
        !fs.statSync(project).isDirectory()||
        !fs.statSync(artifacts).isDirectory()||
        project===artifacts||artifacts.startsWith(project+path.sep))
        throw Error('Bees remote actor artifacts must use a separate real local directory.');
    const python=trustedFile(pythonPath,'Bees remote Python',true);
    const worker=trustedFile(workerExecutable,'Bees Linux Unity worker',true);
    if(path.basename(worker)!=='Bees RL Training.x86_64')
        throw Error('Bees WAN remote rollout requires a Linux dedicated Unity worker.');
    const secret=privateKey(keyPath);
    const tunnelPort=brokerPort(forwardPort);
    // Machine operator owns SSH enrollment; no Hub job can select remote
    // peers, credentials, transport flags, broker ports, or executable.
    let approvedForward=null;
    if(sshForward!==null) {
        if(!sshForward||typeof sshForward!=='object'||Array.isArray(sshForward)||
            Object.keys(sshForward).some(k=>![
                'learnerMachineId','tailnetIp','sshUser','identityFile',
                'knownHostsFile','sshPort','brokerPort',
            ].includes(k))||
            !/^[a-z][a-z0-9_-]{0,47}$/.test(String(sshForward.learnerMachineId||''))||
            !TAILNET.test(String(sshForward.tailnetIp||''))||
            sshForward.tailnetIp.split('.').some(x=>Number(x)>255)||
            !SSH_USER.test(String(sshForward.sshUser||''))||
            !Number.isSafeInteger(sshForward.sshPort)||
            sshForward.sshPort<1||sshForward.sshPort>65535)
            throw Error('Bees WAN SSH forward must target a pinned Tailnet learner and approved login identity.');
        const ssh=trustedFile('/usr/bin/ssh','System OpenSSH',true);
        const keyFile=trustedFile(sshForward.identityFile,'Private SSH identity');
        const hostFile=trustedFile(sshForward.knownHostsFile,'Pinned SSH known_hosts');
        const privateStat=fs.statSync(keyFile);
        const hostStat=fs.statSync(hostFile);
        if(!fs.statSync(keyFile).isFile()||privateStat.size<16||
            privateStat.size>32768||(privateStat.mode&0o077)!==0||
            typeof process.getuid==='function'&&privateStat.uid!==process.getuid()||
            hostStat.size<40||hostStat.size>1048576)
            throw Error('Bees WAN SSH credentials must be local pinned files; private key must be owned and mode 0600.');
        approvedForward=Object.freeze({
            learner_machine_id:sshForward.learnerMachineId,
            tailnet_ip:sshForward.tailnetIp,
            ssh_user:sshForward.sshUser,
            ssh_identity_file:keyFile,
            ssh_known_hosts_file:hostFile,
            ssh_port:sshForward.sshPort,
            remote_broker_port:brokerPort(sshForward.brokerPort),
            ssh_executable:ssh,
        });
    }
    const pinned=prepareImmutableRuntime({
        project,python,artifactRoot:artifacts,workerSha:fileHash(worker),
    });
    const source=name=>trustedFile(path.join(pinned.root,name),'Frozen Bees '+name);
    const approved={
        worker_payload_sha256:unityWorkerTreeSha256(worker),
        worker_sha256:fileHash(worker),
        trainer_sha256:fileHash(source('rl_1v1_config.yaml')),
        runtime_version:pinned.runtimeVersion,
        wan_key_fingerprint:secret.fingerprint,
        actor_sha256:fileHash(source('bees_elastic_wan_actor_worker.py')),
        runner_sha256:fileHash(source('experiment_hub_bees_rollout_train.py')),
    };
    const py=spawnSync(python,['-B','-c',
        'import sys,mlagents,torch,yaml,tensorboard;'+
        'assert sys.version_info[:2]==(3,10);'+
        'assert __import__("importlib.metadata",fromlist=["version"]).version("mlagents")=="1.1.0"'],{
        cwd:project,encoding:'utf8',timeout:20000,maxBuffer:16384,
    });
    if(py.error||py.status!==0)
        throw Error('Bees remote actor requires Python 3.10 with ML-Agents 1.1.0, torch, and TensorBoard.');
    const runtime=Object.freeze({
        training:source('bees_mlagents_learn.py'),
        trainer:source('rl_1v1_config.yaml'),
        evaluator:source('bees_experiment_benchmark.py'),
        evaluatorCore:source('bees_continual_evaluate.py'),
        opponents:source('bees_experiment_scripted.py'),
        actor:source('bees_elastic_wan_actor_worker.py'),
        runner:source('experiment_hub_bees_rollout_train.py'),
    });
    return {
        executable:python,cwd:project,stop_policy:'terminate-group',
        design_catalog:{bees_rollout:{
            version:1,fields:[],
            worker_sha256:approved.worker_sha256,
            worker_payload_sha256:approved.worker_payload_sha256,
            trainer_sha256:approved.trainer_sha256,
            runtime_version:approved.runtime_version,
            wan_key_fingerprint:approved.wan_key_fingerprint,
            wan_forward_managed:Boolean(approvedForward),
            ...(approvedForward?{
                wan_target_machine_id:approvedForward.learner_machine_id,
            }:{}),
        }},
        bees_rollout_versions:{...approved},
        gpu_verify_args(parameters,context) {
            if(parameters?.device!=='cuda'||!PCI.test(String(context?.gpu_pci_address||'')))
                throw Error('Bees remote ROCm actor requires one exclusive physical GPU lease.');
            return ['-B',path.join(__dirname,'experiment_hub_tinybrains_rocm_verify.py'),
                context.gpu_pci_address];
        },
        args(parameters,context) {
            if(!parameters||typeof parameters!=='object'||Array.isArray(parameters)||
                Object.keys(parameters).some(key=>![
                    'learner_job_id','lineage_root_id','worker_sha256',
                    'trainer_sha256','runtime_version','wan_key_fingerprint',
                    'worker_payload_sha256','envs','device','slot',
                ].includes(key))||
                !UUID.test(String(parameters.learner_job_id||''))||
                !UUID.test(String(parameters.lineage_root_id||''))||
                parameters.learner_job_id===context.job_id||
                !Number.isSafeInteger(parameters.slot)||
                parameters.slot<0||parameters.slot>7||
                !UUID.test(String(context.job_id||''))||
                context.project_id!=='bees_rollout'||
                !Number.isSafeInteger(parameters.envs)||parameters.envs<1||
                parameters.envs>64||
                !Number.isSafeInteger(context.cpu_quota)||context.cpu_quota<1||
                parameters.envs>context.cpu_quota||
                !['cpu','cuda'].includes(parameters.device)||
                (parameters.device==='cuda'
                    ?!PCI.test(String(context.gpu_pci_address||''))||
                        context.gpu_mode!=='exclusive'||context.gpu_pci_addresses!==undefined
                    :context.gpu_pci_address!==undefined||
                        context.gpu_pci_addresses!==undefined))
                throw Error('Bees WAN rollout requires one frozen learner, exact CPU or exclusive GPU lease, and 1–64 local environments.');
            for(const key of ['worker_sha256','worker_payload_sha256',
                'trainer_sha256','runtime_version','wan_key_fingerprint'])
                if(parameters[key]!==approved[key])
                    throw Error('Bees remote rollout source or private session key does not match its approved learner.');
            if(privateKey(secret.path).fingerprint!==secret.fingerprint||
                fileHash(worker)!==approved.worker_sha256||
                unityWorkerTreeSha256(worker)!==approved.worker_payload_sha256||
                fileHash(runtime.trainer)!==approved.trainer_sha256||
                fileHash(runtime.actor)!==approved.actor_sha256||
                fileHash(runtime.runner)!==approved.runner_sha256)
                throw Error('The approved Bees WAN actor installation changed after enrollment.');
            // This identity must equal the learner's, not the actor's Hub
            // lineage ID. Both sides use only SHA-pinned source identities.
            identity(approved.worker_payload_sha256,approved.trainer_sha256,
                approved.runtime_version,parameters.lineage_root_id);
            const root=path.join(artifacts,context.job_id);
            fs.mkdirSync(root,{mode:0o700});
            if(fs.realpathSync(root)!==root||
                fs.statSync(root).mode&0o077)
                throw Error('Bees remote job artifact owner must be private and unredirected.');
            const job={
                schema_version:approvedForward?2:1,job_id:context.job_id,
                learner_job_id:parameters.learner_job_id,
                lineage_root_id:parameters.lineage_root_id,
                slot:parameters.slot,
                output_root:root,
                game_binary:worker,game_sha256:approved.worker_sha256,
                game_payload_sha256:approved.worker_payload_sha256,
                training_script:runtime.training,
                training_sha256:fileHash(runtime.training),
                trainer_source:runtime.trainer,
                trainer_sha256:approved.trainer_sha256,
                runtime_version:approved.runtime_version,
                evaluator_script:runtime.evaluator,
                evaluator_core:runtime.evaluatorCore,
                opponent_script:runtime.opponents,
                actor_script:runtime.actor,actor_sha256:approved.actor_sha256,
                envs:parameters.envs,
                torch_threads:Math.min(context.cpu_quota,parameters.envs),
                device:parameters.device,
                expected_gpu_pci:context.gpu_pci_address??null,
                auth_token_file:secret.path,
                auth_token_fingerprint:secret.fingerprint,
                broker_port:tunnelPort,
                ...(approvedForward?{ssh_forward:approvedForward}:{}),
            };
            const frozen=path.join(root,'job.json');
            fs.writeFileSync(frozen,JSON.stringify(job,null,2)+'\n',{
                flag:'wx',mode:0o600,
            });
            return ['-B',runtime.runner,'--job',frozen];
        },
        artifact_export_dir(jobId) {
            if(!UUID.test(String(jobId||'')))throw Error('Invalid Bees remote job ID.');
            return path.join(artifacts,jobId);
        },
    };
}
module.exports={createBeesRolloutAdapter};
