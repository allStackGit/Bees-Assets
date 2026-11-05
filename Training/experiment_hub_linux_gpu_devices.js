'use strict';

// Resolve Hub-lease PCI identities into LOCAL Linux ROCm character devices.
// This does not confer GPU access or prove cgroup eBPF enforcement.
const fs=require('node:fs');
const path=require('node:path');
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const RENDER=/^renderD(12[8-9]|1[3-9][0-9]|[2-9][0-9]{2}|[1-3][0-9]{3})$/;
const GPU_ID=/^[a-z][a-z0-9_-]{0,47}$/;
function majorMinor(dev) {
    if(!Number.isSafeInteger(dev)||dev<0)
        throw Error('Unsafe kernel character device identity.');
    const n=BigInt(dev);
    return {major:Number((n>>8n&0xfffn)|(n>>32n&0xfffff000n)),
        minor:Number((n&0xffn)|(n>>12n&0xffffff00n))};
}
function characterDevice(fsApi,filename) {
    const stat=fsApi.statSync(filename);
    if(!stat.isCharacterDevice()||fsApi.realpathSync(filename)!==filename)
        throw Error('GPU device node is not an ordinary character device.');
    return majorMinor(stat.rdev);
}
function readBounded(fsApi,filename) {
    const value=fsApi.readFileSync(filename,'utf8');
    if(typeof value!=='string'||value.length>128)
        throw Error('Invalid local DRM sysfs property.');
    return value.trim().toLowerCase();
}
function resolveRocmDevices(bindings,options={}) {
    const fsApi=options.fsApi||fs;
    const pathApi=options.pathApi||path;
    const drm=options.drmRoot||'/sys/class/drm';
    const dev=options.deviceRoot||'/dev/dri';
    const kfd=options.kfdNode||'/dev/kfd';
    if(!Array.isArray(bindings)||bindings.length<1||bindings.length>32||
        bindings.some(b=>!b||!GPU_ID.test(String(b.id||''))||
            !PCI.test(String(b.pci_address||''))||
            !Number.isSafeInteger(b.vram_mb)||b.vram_mb<1)||
        new Set(bindings.map(b=>b.id)).size!==bindings.length||
        new Set(bindings.map(b=>b.pci_address)).size!==bindings.length)
        throw Error('GPU lease requires unique, frozen PCI identity bindings.');
    if(kfd!=='/dev/kfd'||drm!=='/sys/class/drm'||dev!=='/dev/dri')
        throw Error('GPU device paths must be the kernel-known host paths.');
    const kfdDevice=characterDevice(fsApi,kfd);
    const entries=fsApi.readdirSync(drm,{withFileTypes:true});
    if(!Array.isArray(entries)||entries.length>512)
        throw Error('Unbounded local DRM inventory.');
    const renderNames=entries.map(e=>e.name).filter(n=>RENDER.test(n));
    if(renderNames.length>32)
        throw Error('More DRM render devices than the Hub can bind.');
    const matches=new Map();
    for(const name of renderNames) {
        const classPath=pathApi.join(drm,name),renderPath=pathApi.join(dev,name);
        let sysfs,driver,pci,device,claim;
        try {
            sysfs=fsApi.realpathSync(pathApi.join(classPath,'device'));
            if(!sysfs.startsWith('/sys/devices/'))continue;
            pci=pathApi.basename(sysfs).toLowerCase();
            if(!PCI.test(pci)||readBounded(fsApi,
                pathApi.join(classPath,'device','vendor'))!=='0x1002')
                continue;
            driver=pathApi.basename(fsApi.realpathSync(
                pathApi.join(classPath,'device','driver')));
            if(driver!=='amdgpu')continue;
            device=characterDevice(fsApi,renderPath);
            const reported=readBounded(fsApi,pathApi.join(classPath,'dev'));
            if(reported!==device.major+':'+device.minor||
                device.major!==226)
                continue;
            const memoryBytes=readBounded(fsApi,pathApi.join(
                classPath,'device','mem_info_vram_total'));
            if(!/^[0-9]{1,16}$/.test(memoryBytes)||
                !Number.isSafeInteger(Number(memoryBytes)))
                continue;
            const actualVramMb=Math.floor(Number(memoryBytes)/1048576);
            if(actualVramMb<1||actualVramMb>1000000)continue;
            claim={pci_address:pci,node:renderPath,major:device.major,
                minor:device.minor,observed_vram_mb:actualVramMb};
        }catch{continue;}
        if(matches.has(pci))
            throw Error('Ambiguous multiple AMD render nodes for one PCI GPU.');
        matches.set(pci,claim);
    }
    const selected=[];
    for(const b of bindings) {
        const observed=matches.get(b.pci_address);
        if(!observed||observed.observed_vram_mb<b.vram_mb)
            throw Error('Leased PCI GPU has no matching local render node and sufficient physical VRAM.');
        selected.push({id:b.id,pci_address:b.pci_address,
            vram_mb:b.vram_mb,...observed});
    }
    const chosen=new Set(bindings.map(b=>b.pci_address));
    const others=[...matches.values()].filter(g=>!chosen.has(g.pci_address));
    return {kfd:{node:kfd,...kfdDevice},selected,
        unselected:others,
        // This is a proposed systemd access profile only. Never assume a
        // setting was applied just because these strings were generated.
        device_properties:[
            'DevicePolicy=closed','DeviceAllow=/dev/kfd rw',
            ...selected.map(g=>'DeviceAllow='+g.node+' rw'),
        ],
    };
}
module.exports={resolveRocmDevices,majorMinor};
