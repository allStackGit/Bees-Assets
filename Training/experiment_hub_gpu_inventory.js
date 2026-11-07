'use strict';

// Read-only AMDGPU PCI/VRAM discovery. This describes hardware presence,
// not GPU partitioning, kernel device isolation, ROCm readiness, or free VRAM.
const fs=require('node:fs');
const path=require('node:path');
const PCI=/^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/;
const CARD=/^card[0-9]{1,3}$/;
const MAX_DEVICES=32;
function boundedRead(fsApi,file) {
    const raw=fsApi.readFileSync(file,'utf8');
    if(typeof raw!=='string'||raw.length>128)
        throw Error('Invalid bounded AMDGPU sysfs attribute.');
    return raw.trim().toLowerCase();
}
function readBytes(fsApi,file) {
    const raw=boundedRead(fsApi,file);
    if(!/^[0-9]{1,16}$/.test(raw))throw Error('Invalid AMDGPU memory byte counter.');
    const bytes=Number(raw);
    if(!Number.isSafeInteger(bytes)||bytes<0)
        throw Error('Unsafe AMDGPU memory byte counter.');
    return bytes;
}
function discoverAmdGpus(options={}) {
    const fsApi=options.fsApi||fs;
    const pathApi=options.pathApi||path;
    const drmRoot=options.drmRoot||'/sys/class/drm';
    const devicesRoot=options.devicesRoot||'/sys/devices';
    let entries;
    try{entries=fsApi.readdirSync(drmRoot,{withFileTypes:true});}
    catch{return [];}
    if(!Array.isArray(entries)||entries.length>512)
        throw Error('Unbounded DRM device inventory.');
    const cards=entries.map(e=>e.name).filter(n=>CARD.test(n))
        .sort((a,b)=>Number(a.slice(4))-Number(b.slice(4)));
    if(cards.length>MAX_DEVICES)
        throw Error('More DRM devices than the Hub GPU protocol permits.');
    const seen=new Set(),result=[];
    for(const card of cards) {
        try {
            const devicePath=pathApi.join(drmRoot,card,'device');
            const real=fsApi.realpathSync(devicePath);
            const pci=pathApi.basename(real).toLowerCase();
            if(!PCI.test(pci)||!real.startsWith(devicesRoot+'/')||
                seen.has(pci))continue;
            const vendor=boundedRead(fsApi,pathApi.join(devicePath,'vendor'));
            if(vendor!=='0x1002')continue;
            const driver=pathApi.basename(fsApi.realpathSync(
                pathApi.join(devicePath,'driver')));
            if(driver!=='amdgpu')continue;
            const bytes=readBytes(fsApi,pathApi.join(devicePath,'mem_info_vram_total'));
            const totalMb=Math.floor(bytes/1048576);
            if(totalMb<1||totalMb>1000000)continue;
            let usedMb=null;
            try {
                const used=readBytes(fsApi,pathApi.join(devicePath,'mem_info_vram_used'));
                if(used<=bytes)usedMb=Math.floor(used/1048576);
            }catch{
                // Usage may be absent, inaccessible or momentarily inconsistent.
                // Never present an invented free-VRAM measurement.
            }
            seen.add(pci);
            result.push({pci_address:pci,vram_mb:totalMb,
                used_vram_mb:usedMb});
        }catch {
            // A missing driver, missing VRAM counter or disappearing DRM
            // device is not evidence of an available and usable GPU.
        }
    }
    return result;
}
module.exports={discoverAmdGpus};
