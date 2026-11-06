'use strict';

// Windows CPU adapter for the TinyBrains Ants-MAPOCA trainer.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const {validateProgress}=require('./experiment_hub_progress');

const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const PROFILE=/^[a-z][a-z0-9_-]{0,47}$/;
const TAIL_BYTES=256*1024;

function isWithin(parent,child) {
    const relative=path.relative(parent,child);
    return !path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep);
}

function createTinyBrainsAdapter({projectRoot,pythonPath,artifactRoot,profiles}) {
    for(const target of [projectRoot,pythonPath,artifactRoot]) {
        if(typeof target!=='string'||!path.isAbsolute(target))
            throw Error('TinyBrains requires absolute local project, Python and artifact paths.');
    }
    const project=fs.realpathSync(projectRoot);
    const artifacts=fs.realpathSync(artifactRoot);
    const executable=fs.realpathSync(pythonPath);
    if(!fs.statSync(project).isDirectory()||!fs.statSync(artifacts).isDirectory()||
        !fs.statSync(executable).isFile()||
        !fs.statSync(path.join(project,'tools','tinybrains-0.5.0','tinybrains.exe')).isFile()||
        isWithin(project,artifacts)||isWithin(artifacts,project))
        throw Error('TinyBrains needs the Windows engine and a dedicated artifact directory outside the project.');
    if(!profiles||typeof profiles!=='object'||Array.isArray(profiles)||!Object.keys(profiles).length)
        throw Error('Configure at least one locally approved TinyBrains CPU profile.');
    const configs=new Map(Object.entries(profiles).map(([id,config])=>{
        if(!PROFILE.test(id)||config?.runtime?.device!=='cpu'||
            !Number.isSafeInteger(config.runtime.threads)||config.runtime.threads<1)
            throw Error('TinyBrains profiles require a valid ID and explicit CPU runtime.');
        const content=JSON.stringify(config,null,2)+'\n';
        const sha256=crypto.createHash('sha256').update(content).digest('hex');
        return [id,{content,sha256}];
    }));

    function jobRoot(context) {
        if(context.project_id!=='tinybrains'||!UUID.test(context.job_id)||
            context.continuation_of_job_id||context.artifact_lineage_root_id!==context.job_id||
            context.gpu_pci_address)
            throw Error('TinyBrains adapter supports fresh CPU jobs only.');
        return path.join(artifacts,context.job_id);
    }

    return {
        executable,cwd:project,stop_policy:'terminate-group',
        args(parameters,context) {
            const {profile,profile_sha256,updates}=parameters;
            const approved=configs.get(profile);
            if(Object.keys(parameters).some(key=>!['profile','profile_sha256','updates'].includes(key))||
                !approved||approved.sha256!==profile_sha256||
                !Number.isSafeInteger(updates)||updates<1||updates>10000000)
                throw Error('TinyBrains jobs require an approved profile, its SHA-256 and 1–10,000,000 updates.');
            const out=jobRoot(context);
            // An existing directory may belong to a prior launch. Never reuse it.
            fs.mkdirSync(out,{mode:0o700});
            const config=path.join(out,'hub-profile.json');
            fs.writeFileSync(config,approved.content,{flag:'wx',mode:0o600});
            return ['-B','-m','ants_mapoca.cli','train','--config',config,'--out',out,
                '--updates',String(updates)];
        },
        readProgress(context) {
            const out=jobRoot(context);
            const history=path.join(out,'history.jsonl');
            let fd;
            try {
                if(fs.realpathSync(history)!==history)
                    throw Error('TinyBrains progress path is redirected.');
                fd=fs.openSync(history,'r');
                const stat=fs.fstatSync(fd);
                if(!stat.isFile()||!stat.size)return null;
                const tail=Buffer.alloc(Math.min(stat.size,TAIL_BYTES));
                const offset=stat.size-tail.length;
                const bytes=fs.readSync(fd,tail,0,tail.length,offset);
                const text=tail.toString('utf8',0,bytes);
                const end=text.lastIndexOf('\n');
                if(end<0)return null;
                const start=text.lastIndexOf('\n',end-1);
                if(start<0&&offset>0)return null;
                const row=JSON.parse(text.slice(start+1,end));
                if(!Number.isSafeInteger(row.update)||row.update<1||
                    !Number.isSafeInteger(row.engine_steps)||row.engine_steps<0||
                    !Number.isFinite(row.wall_seconds)||row.wall_seconds<=0)
                    throw Error('Invalid completed TinyBrains training update.');
                return validateProgress({
                    schema_version:1,source:'hub-metrics-v1',experiment_id:context.job_id,
                    segment_id:'training',source_modified_at:stat.mtime.toISOString(),
                    steps_completed:row.update,
                    metrics:{
                        update_wall_time:{value:row.wall_seconds,unit:'seconds'},
                        engine_steps_per_second:{value:row.engine_steps/row.wall_seconds,unit:'rate'},
                    },
                },context.project_id);
            }catch(error) {
                if(error.code==='ENOENT')return null;
                throw error;
            }finally{
                if(fd!==undefined)fs.closeSync(fd);
            }
        },
    };
}
module.exports={createTinyBrainsAdapter};
