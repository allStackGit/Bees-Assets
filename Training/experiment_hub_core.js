'use strict';

// Project-neutral definitions and job records. This never starts a Bees run.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const ID = /^[a-z][a-z0-9_-]{0,47}$/;
const now = () => new Date().toISOString();
function object(value) {
    return value && typeof value === 'object' && !Array.isArray(value);
}
function copy(value) { return JSON.parse(JSON.stringify(value)); }
function name(value, label) {
    if (typeof value !== 'string' || !value.trim() || value.length > 100)
        throw Error(label + ' must contain 1–100 characters.');
    return value.trim();
}
function config(value) {
    if (!object(value)) throw Error('Configuration parameters must be a JSON object.');
    const serialized = JSON.stringify(value);
    if (serialized.length > 65536) throw Error('Configuration exceeds 64 KiB.');
    return JSON.parse(serialized);
}
function writeAtomic(file, value) {
    fs.mkdirSync(path.dirname(file), {recursive:true});
    const pending = file + '.' + process.pid + '.' + crypto.randomBytes(6).toString('hex') + '.tmp';
    try {
        fs.writeFileSync(pending, JSON.stringify(value, null, 2) + '\n', {flag:'wx'});
        fs.renameSync(pending, file);
    } finally {
        if (fs.existsSync(pending)) fs.unlinkSync(pending);
    }
}
class ExperimentHub {
    constructor(root) {
        this.file = path.join(root, 'hub-v1.json');
        this.state = fs.existsSync(this.file) ? JSON.parse(fs.readFileSync(this.file, 'utf8')) : {
            version:1,
            projects:[{id:'bees', name:'Bees', adapter:'bees', description:'Distributed Unity reinforcement learning'}],
            configurations:[],
            jobs:[],
            machines:[],
            reservations:[],
        };
        const s = this.state;
        if (!object(s) || s.version !== 1 ||
            !['projects','configurations','jobs','machines','reservations'].every(key=>Array.isArray(s[key])))
            throw Error('Experiment Hub registry invalid; refusing to start.');
        if (!s.projects.some(p=>p.id==='bees' && p.adapter==='bees'))
            throw Error('Experiment Hub Bees project identity changed; refusing to start.');
    }
    // Publish in-memory changes only after the complete registry is durably
    // written. A full disk must not leave the API believing a draft was saved.
    commit(next) {
        writeAtomic(this.file, next);
        this.state = next;
    }
    snapshot() { return copy(this.state); }
    project(id) {
        const project = this.state.projects.find(p=>p.id===id);
        if (!project) throw Error('Unknown project.');
        return project;
    }
    addProject(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase project ID.');
        if (this.state.projects.some(p=>p.id===value.id)) throw Error('Project already exists.');
        // New projects are definitions only until a trusted server-side adapter is installed.
        const project = {
            id:value.id, name:name(value.name,'Project name'), adapter:'unconfigured',
            description:String(value.description||'').slice(0,500), created_at:now(),
        };
        const next = copy(this.state);
        next.projects.push(project);
        this.commit(next);
        return copy(project);
    }
    saveConfiguration(value) {
        if (!object(value)) throw Error('Missing configuration.');
        const project = this.project(value.project_id);
        const params = config(value.parameters);
        const time = now();
        const next = copy(this.state);
        let item;
        if (value.id) {
            item = next.configurations.find(c=>c.id===value.id && c.project_id===project.id);
            if (!item) throw Error('Configuration not found.');
            if (item.revision !== value.revision) throw Error('Configuration changed; reopen it before saving.');
            item.name = name(value.name,'Configuration name');
            item.parameters = params;
            item.revision += 1;
            item.updated_at = time;
        } else {
            item = {
                id:crypto.randomUUID(), project_id:project.id,
                name:name(value.name,'Configuration name'), parameters:params,
                revision:1, created_at:time, updated_at:time,
            };
            next.configurations.push(item);
        }
        this.commit(next);
        return copy(item);
    }
    queueConfiguration(value) {
        if (!object(value)) throw Error('Invalid job request.');
        const selected = this.state.configurations.find(x=>x.id===value.configuration_id);
        if (!selected) throw Error('Saved configuration not found.');
        const project = this.project(selected.project_id);
        if (project.adapter === 'unconfigured')
            throw Error('This project has no installed execution adapter. It can be designed but not scheduled.');
        if (project.adapter === 'bees')
            throw Error('Bees jobs still use the verified Bees queue. Queue this definition through the Bees training form.');
        // Explicit adapter registration is required before enabling generic execution.
        throw Error('No trusted execution adapter has been registered for this project.');
    }
    addMachine(value) {
        if (!object(value) || !ID.test(String(value.id||''))) throw Error('Use a lowercase machine ID.');
        if (this.state.machines.some(m=>m.id===value.id)) throw Error('Machine already registered.');
        const cpu = Number(value.cpus), ram = Number(value.ram_mb);
        if (!Number.isSafeInteger(cpu) || cpu < 1 || cpu > 4096 ||
            !Number.isSafeInteger(ram) || ram < 128 || ram > 100000000)
            throw Error('Machine CPU and RAM capacity must be positive integers.');
        const gpu = Array.isArray(value.gpus) ? value.gpus : [];
        if (gpu.length > 32 || gpu.some(g=>!object(g) || !ID.test(String(g.id||'')) ||
            !Number.isSafeInteger(g.vram_mb) || g.vram_mb < 1) ||
            new Set(gpu.map(g=>g.id)).size!==gpu.length)
            throw Error('Invalid GPU inventory.');
        const machine = {
            id:value.id, name:name(value.name,'Machine name'),
            // All machines start inactive. An installed agent must prove health
            // before claims can be applied; a manually entered inventory is not authority.
            status:'unverified', cpus:cpu, ram_mb:ram, gpus:copy(gpu),
            created_at:now(), last_seen_at:null,
        };
        const next = copy(this.state);
        next.machines.push(machine);
        this.commit(next);
        return copy(machine);
    }
}
module.exports = {ExperimentHub, writeAtomic};
