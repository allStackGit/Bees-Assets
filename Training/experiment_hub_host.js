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
module.exports={inspectHost};
