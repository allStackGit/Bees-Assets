'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const CONTROL_SCHEMA_VERSION = 1;
const DEFAULT_PORT = 7150;
const DEFAULT_HOST = '0.0.0.0';
const DEFAULT_LEASE_SECONDS = 20;
const VALID_ROLES = new Set(['dedicated', 'full-game']);

function sha256File(filePath) {
    const hash = crypto.createHash('sha256');
    const fd = fs.openSync(filePath, 'r');
    const buffer = Buffer.allocUnsafe(1024 * 1024);
    try {
        for (;;) {
            const bytes = fs.readSync(fd, buffer, 0, buffer.length, null);
            if (bytes <= 0) break;
            hash.update(buffer.subarray(0, bytes));
        }
    } finally {
        fs.closeSync(fd);
    }
    return hash.digest('hex');
}

function atomicWriteJson(filePath, value) {
    const resolved = path.resolve(filePath);
    fs.mkdirSync(path.dirname(resolved), { recursive: true });
    const temporary = resolved + '.tmp-' + process.pid + '-' + crypto.randomBytes(6).toString('hex');
    fs.writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', mode: 0o600 });
    fs.renameSync(temporary, resolved);
}

function readJsonBody(request, limitBytes = 1024 * 1024) {
    return new Promise((resolve, reject) => {
        const chunks = [];
        let total = 0;
        request.on('data', chunk => {
            total += chunk.length;
            if (total > limitBytes) {
                reject(Object.assign(new Error('request body exceeds limit'), { statusCode: 413 }));
                request.destroy();
                return;
            }
            chunks.push(chunk);
        });
        request.on('end', () => {
            if (total === 0) {
                resolve({});
                return;
            }
            try {
                resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
            } catch (error) {
                reject(Object.assign(new Error('request body is invalid JSON'), { statusCode: 400 }));
            }
        });
        request.on('error', reject);
    });
}

function sendJson(response, statusCode, value) {
    const body = Buffer.from(JSON.stringify(value) + '\n', 'utf8');
    response.writeHead(statusCode, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': body.length,
        'Cache-Control': 'no-store',
    });
    response.end(body);
}

function requireString(value, label, maximum = 256) {
    if (typeof value !== 'string' || !value.trim() || value.length > maximum) {
        throw Object.assign(new Error(label + ' must be a non-empty string'), { statusCode: 400 });
    }
    return value.trim();
}

function requireRole(value) {
    const role = requireString(value, 'role', 32);
    if (!VALID_ROLES.has(role)) {
        throw Object.assign(new Error('role must be dedicated or full-game'), { statusCode: 400 });
    }
    return role;
}

function normalizeEnvironmentArgs(value) {
    if (!Array.isArray(value) || value.some(item => typeof item !== 'string')) {
        throw Object.assign(new Error('environment_args must be an array of strings'), { statusCode: 400 });
    }
    return value.map(String);
}

function publicBuildDescriptor(record) {
    if (!record) return null;
    return {
        platform: record.platform,
        build_id: record.build_id,
        archive_sha256: record.archive_sha256,
        archive_size_bytes: record.archive_size_bytes,
        entrypoint: record.entrypoint,
        artifact_url: '/v1/artifact/' + encodeURIComponent(record.platform),
    };
}

class TrainingControlStore {
    constructor(options = {}) {
        this.statePath = path.resolve(
            options.statePath || path.join(__dirname, 'logs', 'training-control-state.json'));
        this.artifactRoot = path.resolve(
            options.artifactRoot || path.join(__dirname, 'training-artifacts'));
        this.leaseSeconds = Number(options.leaseSeconds || DEFAULT_LEASE_SECONDS);
        if (!Number.isFinite(this.leaseSeconds) || this.leaseSeconds <= 0) {
            throw new Error('training-control leaseSeconds must be positive');
        }
        this.now = typeof options.now === 'function' ? options.now : () => Date.now();
        this.trainers = new Map();
        this.state = this._loadState();
    }

    _defaultState() {
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            revision: 0,
            training_enabled: false,
            environment_args: [],
            builds: {},
        };
    }

    _loadState() {
        if (!fs.existsSync(this.statePath)) return this._defaultState();
        const parsed = JSON.parse(fs.readFileSync(this.statePath, 'utf8'));
        if (!parsed || parsed.schema_version !== CONTROL_SCHEMA_VERSION) {
            throw new Error('training-control state schema is incompatible');
        }
        if (!Number.isInteger(parsed.revision) || parsed.revision < 0) {
            throw new Error('training-control state revision is invalid');
        }
        if (typeof parsed.training_enabled !== 'boolean') {
            throw new Error('training-control state training_enabled is invalid');
        }
        parsed.environment_args = normalizeEnvironmentArgs(parsed.environment_args || []);
        if (!parsed.builds || typeof parsed.builds !== 'object' || Array.isArray(parsed.builds)) {
            throw new Error('training-control state builds map is invalid');
        }
        return parsed;
    }

    _persist() {
        atomicWriteJson(this.statePath, this.state);
    }

    setDesiredState(patch) {
        if (!patch || typeof patch !== 'object' || Array.isArray(patch)) {
            throw Object.assign(new Error('desired-state patch must be an object'), { statusCode: 400 });
        }
        let changed = false;
        if (Object.prototype.hasOwnProperty.call(patch, 'training_enabled')) {
            if (typeof patch.training_enabled !== 'boolean') {
                throw Object.assign(new Error('training_enabled must be boolean'), { statusCode: 400 });
            }
            if (patch.training_enabled !== this.state.training_enabled) {
                this.state.training_enabled = patch.training_enabled;
                changed = true;
            }
        }
        if (Object.prototype.hasOwnProperty.call(patch, 'environment_args')) {
            const args = normalizeEnvironmentArgs(patch.environment_args);
            if (JSON.stringify(args) !== JSON.stringify(this.state.environment_args)) {
                this.state.environment_args = args;
                changed = true;
            }
        }
        if (changed) {
            this.state.revision++;
            this._persist();
        }
        return this.desiredState();
    }

    publishArtifact({ platform, buildId, archivePath, entrypoint }) {
        platform = requireString(platform, 'platform', 64);
        buildId = requireString(buildId, 'build_id', 128);
        entrypoint = requireString(entrypoint, 'entrypoint', 512);
        if (!/^[A-Za-z0-9._-]+$/.test(platform) || !/^[A-Za-z0-9._-]+$/.test(buildId)) {
            throw Object.assign(
                new Error('platform and build_id may contain only letters, digits, dot, underscore, and dash'),
                { statusCode: 400 });
        }
        const source = path.resolve(requireString(archivePath, 'archive_path', 4096));
        const stats = fs.statSync(source);
        if (!stats.isFile()) {
            throw Object.assign(new Error('archive_path must name a file'), { statusCode: 400 });
        }
        const archiveSha256 = sha256File(source);
        const platformRoot = path.join(this.artifactRoot, platform);
        fs.mkdirSync(platformRoot, { recursive: true });
        const destination = path.join(platformRoot, buildId + '-' + archiveSha256 + '.zip');
        if (!fs.existsSync(destination)) {
            const temporary = destination + '.tmp-' + process.pid + '-' + crypto.randomBytes(6).toString('hex');
            fs.copyFileSync(source, temporary);
            if (sha256File(temporary) !== archiveSha256) {
                fs.unlinkSync(temporary);
                throw new Error('canonical build copy failed SHA-256 verification');
            }
            fs.renameSync(temporary, destination);
        }
        const record = {
            platform,
            build_id: buildId,
            archive_path: destination,
            archive_sha256: archiveSha256,
            archive_size_bytes: stats.size,
            entrypoint,
        };
        const previous = this.state.builds[platform];
        if (!previous ||
            previous.build_id !== record.build_id ||
            previous.archive_sha256 !== record.archive_sha256 ||
            previous.entrypoint !== record.entrypoint ||
            previous.archive_path !== record.archive_path) {
            this.state.builds[platform] = record;
            this.state.revision++;
            this._persist();
        }
        return publicBuildDescriptor(this.state.builds[platform]);
    }

    desiredState() {
        const builds = {};
        for (const [platform, record] of Object.entries(this.state.builds)) {
            builds[platform] = publicBuildDescriptor(record);
        }
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            revision: this.state.revision,
            training_enabled: this.state.training_enabled,
            environment_args: [...this.state.environment_args],
            lease_seconds: this.leaseSeconds,
            builds,
        };
    }

    stateFor({ trainerId, role, platform }) {
        trainerId = requireString(trainerId, 'trainer_id', 128);
        role = requireRole(role);
        platform = requireString(platform, 'platform', 64);
        const desiredMode = this.state.training_enabled
            ? 'training'
            : role === 'full-game' ? 'inference' : 'stopped';
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            trainer_id: trainerId,
            role,
            platform,
            revision: this.state.revision,
            training_enabled: this.state.training_enabled,
            desired_mode: desiredMode,
            environment_args: [...this.state.environment_args],
            lease_seconds: this.leaseSeconds,
            build: publicBuildDescriptor(this.state.builds[platform]),
        };
    }

    heartbeat(payload) {
        if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
            throw Object.assign(new Error('heartbeat payload must be an object'), { statusCode: 400 });
        }
        const trainerId = requireString(payload.trainer_id, 'trainer_id', 128);
        const role = requireRole(payload.role);
        const platform = requireString(payload.platform, 'platform', 64);
        const now = this.now();
        const record = {
            trainer_id: trainerId,
            role,
            platform,
            hostname: typeof payload.hostname === 'string' ? payload.hostname.slice(0, 256) : '',
            pid: Number.isInteger(payload.pid) && payload.pid >= 0 ? payload.pid : null,
            process_state: typeof payload.process_state === 'string' ? payload.process_state.slice(0, 64) : '',
            build_id: typeof payload.build_id === 'string' ? payload.build_id.slice(0, 128) : '',
            build_sha256: typeof payload.build_sha256 === 'string' ? payload.build_sha256.slice(0, 64) : '',
            applied_revision: Number.isInteger(payload.applied_revision) ? payload.applied_revision : -1,
            last_error: typeof payload.last_error === 'string' ? payload.last_error.slice(0, 2048) : '',
            last_seen_ms: now,
        };
        this.trainers.set(trainerId, record);
        return this.stateFor({ trainerId, role, platform });
    }

    status() {
        const now = this.now();
        const staleAfter = this.leaseSeconds * 1000;
        const trainers = [...this.trainers.values()]
            .map(record => ({
                ...record,
                stale: now - record.last_seen_ms > staleAfter,
                age_seconds: Math.max(0, (now - record.last_seen_ms) / 1000),
            }))
            .sort((a, b) => a.trainer_id.localeCompare(b.trainer_id));
        return { desired: this.desiredState(), trainers };
    }

    artifact(platform) {
        platform = requireString(platform, 'platform', 64);
        return this.state.builds[platform] || null;
    }
}

function authenticated(request, token) {
    const header = request.headers && request.headers.authorization;
    if (typeof header !== 'string' || !header.startsWith('Bearer ')) return false;
    const supplied = Buffer.from(header.slice('Bearer '.length), 'utf8');
    const expected = Buffer.from(token, 'utf8');
    return supplied.length === expected.length && crypto.timingSafeEqual(supplied, expected);
}

function createTrainingControlHandler(store, token) {
    token = requireString(token, 'training-control token', 4096);
    return async (request, response) => {
        try {
            if (!authenticated(request, token)) {
                sendJson(response, 401, { error: 'unauthorized' });
                return;
            }
            const url = new URL(request.url, 'http://training-control.local');
            if (request.method === 'GET' && url.pathname === '/v1/state') {
                sendJson(response, 200, store.stateFor({
                    trainerId: url.searchParams.get('trainer_id'),
                    role: url.searchParams.get('role'),
                    platform: url.searchParams.get('platform'),
                }));
                return;
            }
            if (request.method === 'POST' && url.pathname === '/v1/heartbeat') {
                sendJson(response, 200, store.heartbeat(await readJsonBody(request)));
                return;
            }
            if (request.method === 'GET' && url.pathname === '/v1/status') {
                sendJson(response, 200, store.status());
                return;
            }
            if (request.method === 'POST' && url.pathname === '/v1/admin/state') {
                sendJson(response, 200, store.setDesiredState(await readJsonBody(request)));
                return;
            }
            if (request.method === 'POST' && url.pathname === '/v1/admin/artifact') {
                const body = await readJsonBody(request);
                sendJson(response, 200, store.publishArtifact({
                    platform: body.platform,
                    buildId: body.build_id,
                    archivePath: body.archive_path,
                    entrypoint: body.entrypoint,
                }));
                return;
            }
            const artifactMatch = request.method === 'GET' &&
                url.pathname.match(/^\/v1\/artifact\/([^/]+)$/);
            if (artifactMatch) {
                const platform = decodeURIComponent(artifactMatch[1]);
                const record = store.artifact(platform);
                if (!record || !fs.existsSync(record.archive_path)) {
                    sendJson(response, 404, { error: 'artifact-not-found' });
                    return;
                }
                response.writeHead(200, {
                    'Content-Type': 'application/zip',
                    'Content-Length': record.archive_size_bytes,
                    'X-Bees-Build-Id': record.build_id,
                    'X-Bees-Archive-Sha256': record.archive_sha256,
                    'Cache-Control': 'no-store',
                });
                fs.createReadStream(record.archive_path)
                    .on('error', error => response.destroy(error))
                    .pipe(response);
                return;
            }
            sendJson(response, 404, { error: 'not-found' });
        } catch (error) {
            const statusCode = Number.isInteger(error.statusCode) ? error.statusCode : 500;
            sendJson(response, statusCode, {
                error: statusCode >= 500 ? 'internal-error' : 'invalid-request',
                message: error.message,
            });
        }
    };
}

function startTrainingControl(options = {}) {
    const token = options.token || process.env.BEES_TRAINING_CONTROL_TOKEN;
    if (!token) throw new Error('BEES_TRAINING_CONTROL_TOKEN is required for training control.');
    const port = Number(options.port || process.env.BEES_TRAINING_CONTROL_PORT || DEFAULT_PORT);
    const host = options.host || process.env.BEES_TRAINING_CONTROL_HOST || DEFAULT_HOST;
    if (!Number.isInteger(port) || port <= 0 || port > 65535) {
        throw new Error('training-control port must be in 1-65535');
    }
    const store = options.store || new TrainingControlStore({
        statePath: options.statePath || process.env.BEES_TRAINING_CONTROL_STATE,
        artifactRoot: options.artifactRoot || process.env.BEES_TRAINING_ARTIFACT_ROOT,
        leaseSeconds: options.leaseSeconds || process.env.BEES_TRAINING_CONTROL_LEASE_SECONDS,
    });
    const httpModule = options.httpModule || http;
    const server = httpModule.createServer(createTrainingControlHandler(store, token));
    server.listen(port, host);
    console.log(
        '[Bees training control] listening on ' + host + ':' + port +
        ' revision=' + store.state.revision +
        ' training=' + store.state.training_enabled);
    return { server, store, port, host };
}

function startTrainingControlFromEnvironment(options = {}) {
    const token = options.token || process.env.BEES_TRAINING_CONTROL_TOKEN;
    const explicitlyEnabled = String(
        options.enabled !== undefined ? options.enabled : process.env.BEES_TRAINING_CONTROL_ENABLED || ''
    ).toLowerCase();
    const enabled = explicitlyEnabled === '1' || explicitlyEnabled === 'true' || Boolean(token);
    if (!enabled) return null;
    if (!token) {
        throw new Error(
            'Training control is enabled but BEES_TRAINING_CONTROL_TOKEN is not configured.');
    }
    return startTrainingControl({ ...options, token });
}

module.exports = {
    CONTROL_SCHEMA_VERSION,
    DEFAULT_PORT,
    DEFAULT_HOST,
    DEFAULT_LEASE_SECONDS,
    TrainingControlStore,
    createTrainingControlHandler,
    startTrainingControl,
    startTrainingControlFromEnvironment,
    sha256File,
};
