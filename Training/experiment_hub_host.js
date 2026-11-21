'use strict';

const fs=require('node:fs');
const os=require('node:os');

// Read-only platform discovery, not an authority to start jobs.
// Detecting Linux cgroup v2 does NOT establish a delegated, writable cgroup,
// exclusive host ownership, or CPU/GPU isolation.
function inspectHost(osApi=os,fsApi=fs) {
    const platform=osApi.platform();
    let cgroupV2=false,controllers=[];
    if(platform==='linux') {
        try {
            const raw=fsApi.readFileSync('/sys/fs/cgroup/cgroup.controllers','utf8');
            if(typeof raw==='string'&&raw.length<=4096) {
                controllers=[...new Set(raw.trim().split(/\s+/)
                    .filter(name=>['cpu','memory','io','pids','cpuset'].includes(name)))];
                cgroupV2=true;
            }
        } catch {
            // Missing or inaccessible controller metadata is not proof of
            // isolation: treat it as unavailable.
        }
    }
    let reason;
    if(platform==='linux'&&cgroupV2)
        reason='Linux cgroup v2 detected, but delegation and process supervision are not installed.';
    else if(platform==='linux')
        reason='A usable Linux cgroup v2 hierarchy has not been identified.';
    else if(platform==='win32')
        reason='Windows Job Objects supervision backend is not installed.';
    else
        reason='No native process supervisor is installed for this platform.';
    return {
        protocol_version:1,platform,
        backend:'none',process_supervision:false,
        cpu_quota_enforced:false,memory_limit_enforced:false,
        gpu_isolation_enforced:false,
        cgroup_v2_detected:cgroupV2,
        cgroup_controllers:controllers,
        reason,
    };
}
// Report memory that can be supplied to a new process without relying on
// configured machine capacity. Linux MemAvailable includes reclaimable cache;
// os.freemem() alone excludes it. These are observations, not reservations.
function availableMemoryMb(osApi=os,fsApi=fs) {
    const total=typeof osApi.totalmem==='function'?osApi.totalmem():null;
    if(!Number.isSafeInteger(total)||total<=0)return null;
    let available=null;
    if(osApi.platform()==='linux') {
        try {
            const data=fsApi.readFileSync('/proc/meminfo','utf8');
            if(typeof data==='string'&&data.length<=16384) {
                const match=data.match(/^MemAvailable:\s+([0-9]+)\s+kB\s*$/m);
                if(match)available=Number(match[1])*1024;
            }
        }catch {
            // An inaccessible or malformed /proc value is not an estimate.
        }
    }
    if(available===null&&typeof osApi.freemem==='function')
        available=osApi.freemem();
    if(!Number.isSafeInteger(available)||available<0)return null;
    return Math.floor(Math.min(total,available)/1048576);
}
module.exports={inspectHost,availableMemoryMb};
