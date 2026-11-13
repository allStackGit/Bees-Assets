'use strict';

// Read actual cgroup v2 enforcement for a systemd-managed service. Checking
// unit properties alone cannot prove that CPU and memory controllers applied.
const fs=require('node:fs');
const path=require('node:path');
const CGROUP_ROOT='/sys/fs/cgroup';
function limitedRead(fsApi,file,max=4096) {
    const raw=fsApi.readFileSync(file,'utf8');
    if(typeof raw!=='string'||raw.length>max)throw Error('Invalid kernel control file.');
    return raw.trim();
}
function safeUint(value) {
    if(typeof value!=='string'||!(/^(0|[1-9][0-9]*)$/).test(value))
        return null;
    const number=Number(value);
    return Number.isSafeInteger(number)?number:null;
}
function verifyKernelLimits(report,cpus,ramMb,fsApi=fs) {
    if(!report||!Number.isSafeInteger(cpus)||cpus<1||
        !Number.isSafeInteger(ramMb)||ramMb<128||
        typeof report.ControlGroup!=='string'||
        !/^\/(?:[A-Za-z0-9_.@\\-]+\/)*[A-Za-z0-9_.@\\-]+$/.test(
            report.ControlGroup)||
        report.ControlGroup.split('/').some(x=>x==='.'||x==='..')||
        report.ControlGroup.length>900)
        throw Error('Untrusted systemd cgroup identity.');
    const pid=safeUint(String(report.MainPID||''));
    if(pid===null||pid<2||pid>4194304)
        throw Error('Active native service has no verifiable main process.');
    const group=path.resolve(CGROUP_ROOT,'.'+report.ControlGroup);
    const root=fsApi.realpathSync(CGROUP_ROOT);
    const actual=fsApi.realpathSync(group);
    if(actual===root||!actual.startsWith(root+path.sep))
        throw Error('Native service cgroup escaped the unified hierarchy.');
    const values=limitedRead(fsApi,path.join(actual,'cpu.max')).split(/\s+/);
    if(values.length!==2)
        throw Error('Kernel CPU quota is unavailable.');
    const quota=safeUint(values[0]),period=safeUint(values[1]);
    if(quota===null||period===null||period===0||
        // An integer CPUQuota of 100% per requested CPU must be active.
        BigInt(quota)!==BigInt(period)*BigInt(cpus))
        throw Error('Kernel CPU quota does not match the requested allocation.');
    const memory=safeUint(limitedRead(fsApi,path.join(actual,'memory.max')));
    if(memory===null||memory!==ramMb*1048576)
        throw Error('Kernel memory limit does not match the requested allocation.');
    const memberships=limitedRead(fsApi,'/proc/'+pid+'/cgroup',8192).split('\n');
    const unified=memberships.find(x=>x.startsWith('0::'));
    const location=unified?.slice(3);
    if(!location||
        (location!==report.ControlGroup&&
            !location.startsWith(report.ControlGroup+'/')))
        throw Error('Native service main process is not within its limited cgroup.');
    // Quota enforcement is mandatory, but diagnostic cpu.stat counters are
    // optional. A kernel without throttling fields still has a valid owner.
    let throttling={};
    try {
        const stat=limitedRead(fsApi,path.join(actual,'cpu.stat'),2048);
        const value=name=>{
            const match=new RegExp('^'+name+' ([0-9]+)$','m').exec(stat);
            return match?safeUint(match[1]):null;
        };
        const micros=value('throttled_usec'),events=value('nr_throttled'),
            periods=value('nr_periods');
        throttling={
            ...(micros===null?{}:{cpu_throttled_seconds:micros/1000000}),
            ...(events===null?{}:{cpu_throttle_events:events}),
            ...(periods===null?{}:{cpu_quota_periods:periods}),
        };
    }catch {
        // No invented zeros; missing optional counters cannot block Start.
    }
    return {verified:true,pid,cgroup:report.ControlGroup,
        kernel_cpu_quota_cores:cpus,kernel_memory_max_mb:ramMb,
        ...throttling};
}
module.exports={verifyKernelLimits};
