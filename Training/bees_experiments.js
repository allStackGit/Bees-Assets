'use strict';
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const crypto = require('node:crypto');
const { invokeStart, invokeStop } = require('./operator/commands');
const { getStatus } = require('./operator/control');
const { getAuthoritativeLearnerStats } = require('./operator/status');
const { paths, readJson, writeJsonAtomic, ensureDir, loadConfig } = require('./operator/common');

// The existing Bees operator owns safe run transitions. This module owns only
// an unattended queue, bounded metric summaries, and a loopback-only UI.
const HOST = '127.0.0.1';
// A fixed port also prevents two coordinators from writing the same state file.
const PORT = 7148;
const DB = path.join(paths.trainingRoot, 'Experiments', 'experiments-v1.json');
const HTML = path.join(__dirname, 'experiments.html');
const empty = () => ({ version: 1, armed: false, active_id: null, message: '', items: [] });
const state = fs.existsSync(DB) ? readJson(DB) : empty();
if (!state || state.version !== 1 || !Array.isArray(state.items) || typeof state.armed !== 'boolean') {
    throw Error('Invalid experiment data; no training started: ' + DB);
}
let busy = false;
let haltRequested = false;
const save = () => { ensureDir(path.dirname(DB)); writeJsonAtomic(DB, state); };
const active = () => state.items.find(x => x.id === state.active_id);
const matches = (a, b) => JSON.stringify(a || []) === JSON.stringify(b || []);
const now = () => new Date().toISOString();

async function control() {
    const token = fs.readFileSync(paths.adminTokenPath, 'utf8').trim();
    if (!token) throw Error('Missing training control token.');
    return getStatus(loadConfig(), token);
}

function measure(item, status, force = false) {
    const learner = getAuthoritativeLearnerStats(item.run_id) || {};
    const workers = (status.trainers || []).filter(x =>
        !x.stale && x.metrics && Number(x.metrics.window_episodes) > 0);
    const mean = key => {
        let count = 0, total = 0;
        for (const w of workers) {
            const n = Number(w.metrics.window_episodes), v = Number(w.metrics[key]);
            if (n > 0 && w.metrics[key] != null && Number.isFinite(v)) {
                count += n; total += n * v;
            }
        }
        return count ? Math.round(total / count * 10000) / 10000 : null;
    };
    const step = Number.isSafeInteger(learner.Step) && learner.Step >= 0 ? learner.Step : null;
    if (step != null) {
        if (item.step != null && step < item.step) throw Error('Learner step regressed.');
        if (item.step == null || step > item.step) item.last_progress = now();
        item.step = step;
    }
    const samples = item.samples;
    if (force || !samples.length ||
        Date.now() - Date.parse(samples[samples.length - 1].at) >= 60000) {
        const prior = samples.length ? samples[samples.length - 1] : null;
        // Preserve the last observed live worker metrics at finalization, when
        // workers may already be stopped and their rolling windows unavailable.
        const last = key => mean(key) ?? (prior ? prior[key] : null);
        samples.push({
            at: now(), step, reward: Number.isFinite(learner.MeanReward) ? learner.MeanReward : null,
            steps_per_second: Number.isFinite(learner.LiveStepsPerSecond) ?
                learner.LiveStepsPerSecond : null,
            timeout_pct: last('timeout_pct'), bee_win_pct: last('bee_win_pct'),
            bee_hits_per_shot: last('bee_hits_per_shot'),
        });
        if (samples.length > 500) item.samples = samples.filter((_, i) =>
            i === 0 || i === samples.length - 1 || i % 2 === 0);
    }
}

function blocked(item, message) {
    if (item) { item.status = 'interrupted'; item.error = message; }
    state.armed = false;
    state.active_id = null;
    state.message = message;
    save();
}

async function finalize(item, status) {
    if (status.desired && status.desired.training_enabled) {
        if (status.desired.run_id !== item.run_id) {
            blocked(item, 'Control plane switched runs during stop.');
            return;
        }
        await invokeStop({}); // checkpoints and distributed workers stop through the existing operator
        status = await control();
    }
    if (!status.desired || status.desired.training_enabled) {
        throw Error('Stop is not confirmed; refusing to advance the queue.');
    }
    measure(item, status, true);
    item.status = item.end_reason === 'budget' ? 'completed' :
        item.end_reason === 'manual' ? 'stopped' : 'ended_early';
    item.finished_at = now();
    state.active_id = null;
    state.message = '';
    save();
}

async function monitor() {
    const item = active();
    if (!item) { blocked(null, 'Active experiment record is missing.'); return; }
    const status = await control(), d = status.desired || {};
    if (item.status === 'starting') {
        // Recovery is intentionally conservative: never assume another live run is ours.
        if (!(d.training_enabled && d.run_id && d.run_id !== item.previous_run_id &&
            matches(d.environment_args, item.env_args))) {
            blocked(item, 'Interrupted startup could not be matched to a new run; inspect training.');
            return;
        }
        item.run_id = String(d.run_id);
        item.build_id = String(d.canonical_build_id || '');
        item.started_at = item.started_at || now();
        item.last_progress = item.started_at;
        item.status = 'running';
        save();
    }
    if (item.status === 'stopping') {
        if (d.training_enabled && d.run_id !== item.run_id) {
            blocked(item, 'Another training run owns the control plane.');
            return;
        }
        await finalize(item, status);
        return;
    }
    if (!d.training_enabled || d.run_id !== item.run_id ||
        !matches(d.environment_args, item.env_args)) {
        blocked(item, 'Training was changed outside the experiment manager; queue paused.');
        return;
    }
    measure(item, status);
    const elapsed = Date.now() - Date.parse(item.started_at);
    const stagnant = Date.now() - Date.parse(item.last_progress);
    const reason = haltRequested ? 'manual' :
        item.step != null && item.step >= item.max_steps ? 'budget' :
        elapsed >= item.max_minutes * 60000 ? 'time_limit' :
        stagnant >= item.stall_minutes * 60000 ? 'no_progress' : '';
    if (reason) {
        item.status = 'stopping';
        item.end_reason = reason;
        state.message = 'Safely stopping ' + item.name + ' (' + reason + ')';
        save();
        await finalize(item, status);
    } else save();
}

async function launch() {
    const item = state.items.find(x => x.status === 'queued');
    if (!item) { state.armed = false; save(); return; }
    const status = await control();
    if (!status.desired || status.desired.training_enabled || status.desired.pending_release) {
        state.armed = false;
        state.message = 'Another training run or release is active. Queue not started.';
        save(); return;
    }
    const latest = readJson(paths.latestReleasePath);
    if (String(latest.build_id || '') !== item.release_build) {
        state.armed = false;
        state.message = 'Release changed since experiment was queued. Review before starting.';
        save(); return;
    }
    item.previous_run_id = String(status.desired.run_id || '');
    item.status = 'starting';
    item.started_at = now();
    state.active_id = item.id;
    state.message = 'Starting ' + item.name;
    save();
    await invokeStart({ newRun: true, envArgs: item.env_args });
    await monitor();
}

async function cycle() {
    if (busy) return;
    busy = true;
    try {
        if (state.active_id) await monitor();
        else if (state.armed && !haltRequested) await launch();
    } catch (err) {
        state.armed = false; // fail closed; do not start another experiment
        state.message = 'Operator attention required: ' + err.message;
        if (active()) active().error = err.message;
        save();
        console.error('[experiments]', err.stack || String(err));
    } finally {
        if (!state.active_id) haltRequested = false;
        busy = false;
    }
}

function definition(input) {
    const name = String(input.name || '').trim(), group = String(input.group || 'General').trim();
    const max_steps = Number(input.max_steps), max_minutes = Number(input.max_minutes);
    const stall_minutes = Number(input.stall_minutes);
    if (!name || name.length > 100 || !group || group.length > 80) throw Error('Invalid name or group.');
    if (!Number.isSafeInteger(max_steps) || max_steps < 1000 || max_steps > 5000000000000)
        throw Error('Invalid step budget.');
    if (!Number.isInteger(max_minutes) || max_minutes < 10 || max_minutes > 43200)
        throw Error('Time limit must be 10-43200 minutes.');
    if (!Number.isInteger(stall_minutes) || stall_minutes < 10 || stall_minutes > max_minutes)
        throw Error('No-progress limit must be between 10 minutes and the time limit.');
    if (!Array.isArray(input.env_args) || input.env_args.length > 64) throw Error('Invalid arguments.');
    const overrides = input.env_args.map(x => String(x).trim()).filter(Boolean);
    if (overrides.some(s => s.length > 512 || !/^--rl-[a-z0-9-]+(?:=[^\r\n]*)?$/.test(s)))
        throw Error('Only individual --rl-* environment arguments are accepted.');
    const config = loadConfig();
    const defaults = Array.isArray(config.environmentArgs) ? config.environmentArgs.map(String) : [];
    const keys = new Set(overrides.map(x => x.split('=')[0]));
    const env_args = defaults.filter(x => !keys.has(x.split('=')[0])).concat(overrides);
    const release = readJson(paths.latestReleasePath);
    if (!release || !release.build_id) throw Error('Build a training release before queuing experiments.');
    return {
        id: crypto.randomUUID(), name, group, status: 'queued', created_at: now(),
        max_steps, max_minutes, stall_minutes, env_args,
        release_build: String(release.build_id), samples: [], step: null, run_id: null,
    };
}

function json(res, status, obj) {
    const body = JSON.stringify(obj);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff' });
    res.end(body);
}
async function body(req) {
    if (!String(req.headers['content-type'] || '').startsWith('application/json'))
        throw Error('JSON requests only.');
    let raw = '';
    for await (const chunk of req) {
        raw += chunk.toString('utf8');
        if (raw.length > 32768) throw Error('Request exceeds 32 KB.');
    }
    return JSON.parse(raw || '{}');
}

const server = http.createServer(async (req, res) => {
    const host = req.headers.host, origin = req.headers.origin;
    if (!((host === HOST + ':' + PORT || host === 'localhost:' + PORT) &&
        (!origin || origin === 'http://' + HOST + ':' + PORT ||
         origin === 'http://localhost:' + PORT))) {
        return json(res, 403, { error: 'Loopback-only web interface.' });
    }
    try {
        const route = new URL(req.url, 'http://localhost').pathname;
        if (req.method === 'GET' && route === '/') {
            const page = fs.readFileSync(HTML);
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8',
                'Content-Length': page.length, 'Cache-Control': 'no-store',
                'X-Content-Type-Options': 'nosniff',
                'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'" });
            return res.end(page);
        }
        if (req.method === 'GET' && route === '/api/state') {
            return json(res, 200, { ...state, busy, halt_requested: haltRequested });
        }
        if (req.method !== 'POST') return json(res, 404, { error: 'Not found.' });
        const value = await body(req);
        if (route === '/api/queue') {
            const item = definition(value);
            state.items.push(item); save();
            return json(res, 201, item);
        }
        if (route === '/api/start') {
            if (!state.active_id) {
                const snapshot = await control();
                if (!snapshot.desired || snapshot.desired.training_enabled ||
                    snapshot.desired.pending_release) {
                    return json(res, 409, { error: 'Existing training or release is active.' });
                }
            }
            if (!state.items.some(x => x.status === 'queued'))
                return json(res, 409, { error: 'Queue is empty.' });
            state.armed = true;
            state.message = '';
            save();
            setImmediate(cycle);
            return json(res, 200, { ok: true });
        }
        if (route === '/api/stop') {
            state.armed = false;
            haltRequested = Boolean(state.active_id);
            state.message = state.active_id ? 'Graceful stop requested.' : 'Queue disarmed.';
            save(); setImmediate(cycle);
            return json(res, 200, { ok: true });
        }
        return json(res, 404, { error: 'Not found.' });
    } catch (err) {
        return json(res, 400, { error: err.message });
    }
});
if (require.main === module) {
    server.listen(PORT, HOST, () => {
        console.log('Bees experiments: http://' + HOST + ':' + PORT);
        console.log('Records: ' + DB);
        setImmediate(cycle);
        setInterval(cycle, 30000);
    });
}
module.exports = { server, definition, measure };
