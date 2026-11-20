'use strict';

// Machine-local execution of a one-use coordinator-approved evaluator action.
// It can operate only a service independently observed by this enrolled agent.
// No network address, shell, unit file, executable or command is accepted.
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {evaluatorHealth}=require('./experiment_hub_evaluator_health');
const run=promisify(execFile);
const UUID=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const MACHINE=/^[a-z][a-z0-9_-]{0,47}$/;

async function execute(machine,order) {
    if(!MACHINE.test(String(machine||''))||!order||
        typeof order!=='object'||Array.isArray(order)||
        Object.keys(order).sort().join(',')!=='action,id,unit'||
        !UUID.test(String(order.id||''))||
        typeof order.unit!=='string'||order.unit.length>160||
        !['start','stop','restart','run'].includes(order.action))
        throw Error('Invalid independent evaluator directive.');
    const receipt={id:order.id,unit:order.unit};
    try {
        const health=evaluatorHealth(machine);
        if(health.evaluator_service_issue)
            throw Error('Local evaluator service inventory unavailable: '+
                health.evaluator_service_issue);
        const service=health.evaluator_services.find(entry=>
            entry.unit===order.unit&&
            ['tinybrains','courier'].includes(entry.project));
        if(!service)throw Error('Evaluator is not an installed, observed service on this agent.');
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
