'use strict';

// Read-only AMDGPU PCI/VRAM discovery. This describes hardware presence,
// not GPU partitioning, kernel device isolation, ROCm readiness, or free VRAM.
const fs=require('node:fs');
const path=require('node:path');
const {spawnSync}=require('node:child_process');
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
// Passive sysfs observations only. AMD labels identify edge/core and
// junction/hotspot independently; do not guess a sensor's meaning by index.
function readAmdSensors(fsApi,pathApi,devicePath) {
    let temperatureC=null,hotspotC=null,powerW=null;
    let hwmons;
    try{hwmons=fsApi.readdirSync(pathApi.join(devicePath,'hwmon'),{withFileTypes:true});}
    catch{return {temperature_c:null,hotspot_temperature_c:null,power_w:null};}
    if(!Array.isArray(hwmons)||hwmons.length>16)
        return {temperature_c:null,hotspot_temperature_c:null,power_w:null};
    for(const hwmon of hwmons) {
        if(!/^hwmon[0-9]{1,3}$/.test(hwmon.name))continue;
        // Linux hwmon power attributes are in microwatts. The reported
        // physical-device sample is not attributable to a single Hub job.
        if(powerW===null)for(const attribute of ['power1_average','power1_input']) {
            try {
                const raw=boundedRead(fsApi,pathApi.join(
                    devicePath,'hwmon',hwmon.name,attribute));
                if(!/^[0-9]{1,10}$/.test(raw))continue;
                const watts=Number(raw)/1000000;
                if(Number.isFinite(watts)&&watts<=1500) {
                    powerW=watts;
                    break;
                }
            }catch {
                // This optional sensor is not available on every AMD GPU.
            }
        }
        for(let channel=1;channel<=4;channel++) {
            try {
                const base=pathApi.join(devicePath,'hwmon',hwmon.name,
                    'temp'+channel);
                const label=boundedRead(fsApi,base+'_label');
                if(!['edge','junction','hotspot'].includes(label))continue;
                const raw=boundedRead(fsApi,base+'_input');
                if(!/^[0-9]{1,6}$/.test(raw))continue;
                const value=Number(raw)/1000;
                if(!Number.isFinite(value)||value>150)continue;
                if(label==='edge'&&temperatureC===null)temperatureC=value;
                if((label==='junction'||label==='hotspot')&&hotspotC===null)
                    hotspotC=value;
            }catch{
                // Missing or unreadable optional hardware sensors are unknown.
            }
        }
    }
    return {temperature_c:temperatureC,hotspot_temperature_c:hotspotC,
        power_w:powerW};
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
            let busyPct=null;
            try {
                const busy=boundedRead(fsApi,pathApi.join(devicePath,'gpu_busy_percent'));
                if(/^(?:100|[0-9]{1,2})$/.test(busy))
                    busyPct=Number(busy);
            }catch{
                // Optional host-wide utilization is not a workload quota.
            }
            seen.add(pci);
            result.push({pci_address:pci,vram_mb:totalMb,
                used_vram_mb:usedMb,gpu_busy_pct:busyPct,
                ...readAmdSensors(fsApi,pathApi,devicePath)});
        }catch {
            // A missing driver, missing VRAM counter or disappearing DRM
            // device is not evidence of an available and usable GPU.
        }
    }
    return result;
}

const CUDA_UUID=/^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
// Read-only physical NVIDIA inventory. CUDA visibility is cooperative;
// GPU isolation and VRAM/compute quotas are not enforced by this probe.
function discoverNvidiaGpus(options={}) {
    const run=options.run||spawnSync;
    const runOptions={encoding:'utf8',windowsHide:true,timeout:5000,maxBuffer:8192};
    const common='pci.bus_id,memory.total,memory.used,utilization.gpu,uuid,temperature.gpu';
    const query=fields=>run('nvidia-smi',[
        '--query-gpu='+fields,'--format=csv,noheader,nounits',
    ],runOptions);
    let result=query(common+',power.draw'),hasPower=true;
    if(result.error?.code==='ENOENT')return [];
    // An optional power field must not remove otherwise valid CUDA devices
    // from machine inventory on drivers that cannot query power.draw.
    if(result.error||result.status!==0) {
        result=query(common);
        hasPower=false;
    }
    if(result.error||result.status!==0)
        throw Error('NVIDIA GPU inventory unavailable: '+
            String(result.error?.message||result.stderr||'nvidia-smi failed').trim().slice(0,160));
    const rows=String(result.stdout||'').trim().split(/\r?\n/).filter(Boolean);
    if(rows.length>MAX_DEVICES)throw Error('Too many NVIDIA GPUs for the Hub protocol.');
    const seenPci=new Set(),seenUuid=new Set(),devices=[];
    for(const row of rows) {
        const columns=row.split(',').map(v=>v.trim());
        if(columns.length!==(hasPower?7:6))
            throw Error('Invalid NVIDIA GPU inventory row.');
        let pci=columns[0].toLowerCase();
        if(/^0000[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$/.test(pci))
            pci=pci.slice(4);
        const memory=Number(columns[1]);
        const used=columns[2].toUpperCase()==='N/A'?null:Number(columns[2]);
        const busy=columns[3].toUpperCase()==='N/A'?null:Number(columns[3]);
        const uuid=columns[4];
        const temperature=columns[5].toUpperCase()==='N/A'
            ?null:Number(columns[5]);
        // nvidia-smi may report N/A or an unsupported optional power
        // sensor. Do not turn an absent value into a fabricated zero.
        const watts=hasPower&&/^[0-9]+(?:\.[0-9]+)?$/.test(columns[6])
            ?Number(columns[6]):null;
        const powerW=watts!==null&&watts<=1500?watts:null;
        if(!PCI.test(pci)||!Number.isSafeInteger(memory)||
            memory<1||memory>1000000||
            (used!==null&&(!Number.isSafeInteger(used)||used<0||used>memory))||
            (busy!==null&&(!Number.isSafeInteger(busy)||busy<0||busy>100))||
            (temperature!==null&&(!Number.isFinite(temperature)||
                temperature<0||temperature>150))||
            !CUDA_UUID.test(uuid)||
            seenPci.has(pci)||seenUuid.has(uuid.toLowerCase()))
            throw Error('Invalid or duplicate physical NVIDIA GPU identity.');
        seenPci.add(pci);seenUuid.add(uuid.toLowerCase());
        devices.push({pci_address:pci,vram_mb:memory,
            used_vram_mb:used,gpu_busy_pct:busy,cuda_uuid:uuid,
            temperature_c:temperature,hotspot_temperature_c:null,
            power_w:powerW});
    }
    return devices;
}
module.exports={discoverAmdGpus,discoverNvidiaGpus};

