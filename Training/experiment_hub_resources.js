'use strict';

// Project-neutral resource admission. Never mistake capacity planning for
// OS-level process isolation or ownership of a live physical device.
function int(value) { return Number.isSafeInteger(value) && value >= 0; }

function request(input) {
    if (!input || typeof input !== 'object' || Array.isArray(input))
        throw Error('Invalid resource request.');
    const {cpus, ram_mb} = input;
    if (!int(cpus) || !int(ram_mb) || cpus < 1 || ram_mb < 1)
        throw Error('CPU and memory requests must be positive integers.');
    const gpus = input.gpus == null ? [] : input.gpus;
    if (!Array.isArray(gpus) || gpus.some(v=>typeof v !== 'string' ||
        !/^[a-z][a-z0-9_-]{0,47}$/.test(v)) ||
        new Set(gpus).size !== gpus.length)
        throw Error('GPU IDs must be unique valid device names.');
    const gpu_count = input.gpu_count == null ? gpus.length : input.gpu_count;
    const min_gpu_vram_mb = input.min_gpu_vram_mb == null ? 0 : input.min_gpu_vram_mb;
    if (!int(gpu_count) || gpu_count > 32 ||
        (gpus.length && gpu_count !== gpus.length) ||
        !int(min_gpu_vram_mb) || min_gpu_vram_mb > 1000000 ||
        (!gpu_count && min_gpu_vram_mb))
        throw Error('GPU count and minimum VRAM must be valid; exact device IDs must match the requested count.');
    return {cpus, ram_mb, gpus, gpu_count, min_gpu_vram_mb};
}

function remaining(machine, reservations) {
    const claimed = reservations.filter(r=>r.machine_id===machine.id && r.status==='active');
    const usedCpus = claimed.reduce((sum,r)=>sum+r.resources.cpus,0);
    const usedRam = claimed.reduce((sum,r)=>sum+r.resources.ram_mb,0);
    const usedGpu = new Set(claimed.flatMap(r=>r.resources.gpus));
    return {
        cpus:Math.max(0,machine.cpus-usedCpus),
        ram_mb:Math.max(0,machine.ram_mb-usedRam),
        gpus:machine.gpus.filter(g=>!usedGpu.has(g.id)).map(g=>g.id),
    };
}

function placements(machines, reservations, input) {
    const wanted = request(input);
    return machines.map(machine=>{
        const available = remaining(machine,reservations);
        const eligibleGpus = machine.gpus.filter(g=>available.gpus.includes(g.id) &&
            g.vram_mb>=wanted.min_gpu_vram_mb &&
            (!wanted.gpus.length || wanted.gpus.includes(g.id)))
            .sort((a,b)=>a.vram_mb-b.vram_mb);
        const gpuFits = eligibleGpus.length >= wanted.gpu_count &&
            (!wanted.gpus.length || wanted.gpus.every(id=>
                eligibleGpus.some(g=>g.id===id)));
        const capacityFits = wanted.cpus<=available.cpus &&
            wanted.ram_mb<=available.ram_mb && gpuFits;
        const online = machine.status === 'online';
        const fits = online && capacityFits;
        return {
            machine_id:machine.id, machine_name:machine.name, online,
            available, fits, capacity_fits:capacityFits,
            suggested_gpus:capacityFits?eligibleGpus.slice(0,wanted.gpu_count).map(g=>g.id):[],
            reason:!online?'No authenticated healthy agent; capacity preview only'
                :fits?'Resources available (isolation must still be enforced)'
                :'Insufficient unreserved capacity or GPU VRAM',
        };
    });
}
module.exports = {request,remaining,placements};
