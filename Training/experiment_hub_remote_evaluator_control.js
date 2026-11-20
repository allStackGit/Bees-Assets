'use strict';

// Machine-local execution of a one-use coordinator-approved evaluator action.
// It can operate only a service independently observed by this enrolled agent.
// No network address, shell, unit file, executable or command is accepted.
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {evaluatorHealth}=require('./experiment_hub_evaluator_health');
const tinybrains=require('./experiment_hub_local_evaluators');
const courier=require('./experiment_hub_local_courier_evaluators');
const run=promisify(execFile);
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const MACHINE=/^[a-z][a-z0-9_-]{0,47}$/;

async function execute(machine,order,installedProjects=[]) {
    if(!MACHINE.test(String(machine||''))||!order||
        typeof order!=='object'||Array.isArray(order)||
        Object.keys(order).sort().join(',')!=='action,id,unit'||
        !UUID.test(String(order.id||''))||
        typeof order.unit!=='string'||order.unit.length>160||
        !['start','stop','restart','run','setup','refresh'].includes(order.action))
        throw Error('Invalid independent evaluator directive.');
    const receipt={id:order.id,unit:order.unit};
    try {
        if(order.action==='setup') {
            const project=order.unit.slice('@setup:'.length);
            if(process.platform!=='linux'||!['tinybrains','courier'].includes(project)||
                order.unit!=='@setup:'+project||
                !installedProjects.includes(project))
                throw Error('This enrolled host lacks the approved Linux evaluator setup capability.');
            const hub={snapshot:()=>({machines:[{
                id:machine,installed_projects:installedProjects.slice(),
            }]})};
            await (project==='tinybrains'?tinybrains:courier).action(hub,{
                action:'setup',confirm_action:true,
            });
            return {...receipt,status:'completed',
                diagnostic:'Evaluator installed; restart this machine agent after native jobs are safely stopped to advertise new suites.'};
        }
        const health=evaluatorHealth(machine);
        if(health.evaluator_service_issue)
            throw Error('Local evaluator service inventory unavailable: '+
                health.evaluator_service_issue);
        const service=health.evaluator_services.find(entry=>
            entry.unit===order.unit&&
            ['tinybrains','courier'].includes(entry.project));
        if(!service)throw Error('Evaluator is not an installed, observed service on this agent.');
        if(order.action==='refresh') {
            const matched=new RegExp('^bees-experiment-hub-tinybrains-'+machine+
                '-evaluator-(nano|micro)\\.service$').exec(order.unit);
            if(process.platform!=='linux'||service.project!=='tinybrains'||
                !matched||!installedProjects.includes('tinybrains'))
                throw Error('Only the approved Linux TinyBrains suites may be refreshed.');
            const hub={snapshot:()=>({machines:[{
                id:machine,installed_projects:installedProjects.slice(),
            }]})};
            await tinybrains.action(hub,{
                action:'refresh',id:matched[1],confirm_action:true,
            });
            return {...receipt,status:'completed',
                diagnostic:'Suite refreshed; restart this machine agent when native jobs are safely stopped to advertise it.'};
        }
        if(service.executor==='systemd-user'&&process.platform==='linux') {
            const unit=order.unit;
            if(!unit.endsWith('.service')||
                !unit.startsWith('bees-experiment-hub-'+service.project+'-'+machine+'-evaluator'))
                throw Error('Evaluator service identity differs from its machine.');
            const timer=unit.slice(0,-'.service'.length)+'.timer';
            if(order.action==='run'&&service.service_state==='running')
                throw Error('Evaluator is already running; a second evaluation cannot start.');
            await run('/usr/bin/systemctl',[
                '--user',
                ...(order.action==='run'
                    ?['start','--no-block',unit]
                    :[order.action,timer]),
            ],{timeout:15000,maxBuffer:16384,windowsHide:true});
        }else if(service.executor==='windows-service'&&process.platform==='win32') {
            if(!/^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$/.test(order.unit)||
                !['start','stop'].includes(order.action))
                throw Error('Windows evaluators support Start and Stop; restart requires a confirmed stopped service.');
            await run('sc.exe',[order.action,order.unit],
                {timeout:15000,maxBuffer:16384,windowsHide:true});
        }else throw Error('Evaluator is not supported by this machine service manager.');
        return {...receipt,status:'completed'};
    }catch(error) {
        return {...receipt,status:'failed',
            diagnostic:String(error.message||error).replace(/[\r\n\t]+/g,' ').slice(0,200)};
    }
}
module.exports={execute};
