'use strict';

const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {validateProgress}=require('./experiment_hub_progress');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const WINDOWS_ENTRY=[
    'import os, runpy, sys',
    "sys.stdout = open(os.devnull, 'w', encoding='utf-8')",
    "sys.stderr = open(sys.argv.pop(1), 'x', encoding='utf-8', buffering=1)",
    'script = sys.argv.pop(1)',
    'sys.path.insert(0, os.path.dirname(script))',
    "runpy.run_path(script, run_name='__main__')",
].join('; ');

function inside(parent,child) {
    const relative=path.relative(parent,child);
    return !path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep);
}

function createCourierAdapter({projectRoot,pythonPath,artifactRoot,configPath}) {
    for(const target of [projectRoot,pythonPath,artifactRoot,configPath]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('Courier requires absolute local project, Python, artifact and configuration paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    const executable=fs.realpathSync(pythonPath);
    const script=path.join(project,'courier.py');
    if(!fs.statSync(artifacts).isDirectory()||!fs.statSync(executable).isFile()||
        !fs.statSync(script).isFile()||inside(project,artifacts)||inside(artifacts,project))
        throw Error('Courier needs an installed trainer and a dedicated artifact directory outside the project.');
    const config=fs.readFileSync(configPath);
    const parsed=JSON.parse(config.toString('utf8'));
    if(!parsed||typeof parsed!=='object'||Array.isArray(parsed))
        throw Error('Courier environment configuration must be a JSON object.');
    const configSha256=crypto.createHash('sha256').update(config).digest('hex');
    const windows=process.platform==='win32';

    function jobRoot(context) {
        if(context.project_id!=='courier'||!UUID.test(context.job_id)||
            context.continuation_of_job_id||context.artifact_lineage_root_id!==context.job_id||
            context.gpu_pci_address)
            throw Error('Courier currently supports fresh CPU jobs only.');
        return path.join(artifacts,context.job_id);
    }

    return {
        executable,cwd:project,stop_policy:windows?'terminate-group':'interrupt-main',
        args(parameters,context) {
            const {config_sha256,seed,workers,max_rounds,checkpoint_rounds}=parameters;
            const within=(value,min,max)=>Number.isSafeInteger(value)&&value>=min&&value<=max;
            if(Object.keys(parameters).some(key=>
                !['config_sha256','seed','workers','max_rounds','checkpoint_rounds'].includes(key))||
                config_sha256!==configSha256||!within(seed,0,4294967295)||
                !within(workers,1,64)||!within(max_rounds,0,10000000)||
                !within(checkpoint_rounds,1,10000000))
                throw Error('Courier requires the approved configuration digest and valid seed, worker and round counts.');
            const out=jobRoot(context);
            fs.mkdirSync(out,{mode:0o700});
            const configFile=path.join(out,'hub-environment.json');
            fs.writeFileSync(configFile,config,{flag:'wx',mode:0o600});
            const entry=windows?['-c',WINDOWS_ENTRY,path.join(out,'trainer.stderr.txt'),script]:[script];
            return ['-B',...entry,'--config',configFile,'--run-dir',out,'--port','0',
                '--seed',String(seed),'--workers',String(workers),
                '--max-rounds',String(max_rounds),'--checkpoint-rounds',String(checkpoint_rounds)];
        },
        readProgress(context) {
            const checkpoint=path.join(jobRoot(context),'checkpoint.json');
            let fd;
            try {
                if(fs.realpathSync(checkpoint)!==checkpoint)
                    throw Error('Courier checkpoint path is redirected.');
                fd=fs.openSync(checkpoint,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||stat.size<1||stat.size>256*1024)
                    throw Error('Invalid Courier checkpoint summary size.');
                const buffer=Buffer.alloc(stat.size);
                if(fs.readSync(fd,buffer,0,buffer.length,0)!==stat.size)
                    throw Error('Courier checkpoint summary changed while reading.');
                const summary=JSON.parse(buffer.toString('utf8'));
                return validateProgress({
                    schema_version:1,source:'hub-metrics-v1',experiment_id:context.job_id,
                    segment_id:'training',source_modified_at:stat.mtime.toISOString(),
                    steps_completed:summary.round,
                    metrics:{
                        generation:{value:summary.generation,unit:'count'},
                        active_training_time:{value:summary.active_seconds,unit:'seconds'},
                    },
                },context.project_id);
            }catch(error) {
                if(error.code==='ENOENT'&&fd===undefined)return null;
                throw error;
            }finally {
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}

module.exports={createCourierAdapter};
