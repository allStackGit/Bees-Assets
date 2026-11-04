'use strict';

// Pure resource accounting: admission is not execution or OS-level isolation.
function int(value) { return Number.isSafeInteger(value) && value >= 0; }
function request(input) {
    if (!input || typeof input !== 'object') throw Error('Invalid resource request.');
    const cpus = input.cpus, ram_mb = input.ram_mb;
    if (!int(cpus) || !int(ram_mb) || cpus < 1 || ram_mb < 1)
        throw Error('CPU and memory requests must be positive integers.');
    const gpu = Array.isArray(input.gpus) ? input.gpus : [];
    if (gpu.some(v=>typeof v !== 'string' || !/^[a-z][a-z0-9_-]{0,47}$/.test(v)) ||
        new Set(gpu).size !== gpu.length) throw Error('GPU IDs must be unique.');
    return {cpus, ram_mb, gpus:gpu};
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
        const ready = machine.status === 'online';
        const fits = ready && wanted.cpus<=available.cpus &&
            wanted.ram_mb<=available.ram_mb &&
            wanted.gpus.every(id=>available.gpus.includes(id));
        return {
            machine_id:machine.id, machine_name:machine.name, online:ready,
            available, fits,
            reason:!ready?'No authenticated healthy agent'
                :fits?'Resources available (reservation only; isolation not verified)'
                :'Insufficient unreserved capacity',
        };
    });
}
module.exports = {request, remaining, placements};
