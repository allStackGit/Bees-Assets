'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const { TrainingEnvOptimizer, normalizeCapacity } = require('./trainingEnvOptimizer');

const CONTROL_SCHEMA_VERSION = 5;
const DEFAULT_PORT = 7150;
const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_LEASE_SECONDS = 60;
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

function readRawBody(request, limitBytes = 1024 * 1024) {
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
        request.on('end', () => resolve(Buffer.concat(chunks)));
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
        role: record.role,
        platform: record.platform,
        build_id: record.build_id,
        archive_sha256: record.archive_sha256,
        archive_size_bytes: record.archive_size_bytes,
        entrypoint: record.entrypoint,
        artifact_url: '/v1/artifact/' + encodeURIComponent(record.role) + '/' +
            encodeURIComponent(record.platform) + '/' + encodeURIComponent(record.build_id),
    };
}

class TrainingControlStore {
    constructor(options = {}) {
        this.statePath = path.resolve(
            options.statePath || path.join(__dirname, 'logs', 'training-control-state.json'));
        this.artifactRoot = path.resolve(
            options.artifactRoot || path.join(__dirname, 'training-artifacts'));
        this.logRoot = path.resolve(
            options.logRoot || path.join(__dirname, 'training-logs'));
        this.leaseSeconds = Number(options.leaseSeconds || DEFAULT_LEASE_SECONDS);
        if (!Number.isFinite(this.leaseSeconds) || this.leaseSeconds <= 0) {
            throw new Error('training-control leaseSeconds must be positive');
        }
        this.now = typeof options.now === 'function' ? options.now : () => Date.now();
        this.envOptimizer = new TrainingEnvOptimizer(options.envOptimizer || {});
        this.trainers = new Map();
        this.state = this._loadState();
    }

    _defaultState() {
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            revision: 0,
            training_enabled: false,
            environment_args: [],
            canonical_build_id: "",
            run_id: "",
            compatibility_key: "",
            pending_release: null,
            known_dedicated_trainers: [],
            builds: {},
            full_game_builds: {},
        };
    }

    _loadState() {
        if (!fs.existsSync(this.statePath)) return this._defaultState();
        let parsed = JSON.parse(fs.readFileSync(this.statePath, 'utf8'));
        if (!parsed || !Number.isInteger(parsed.schema_version)) {
            throw new Error('training-control state schema is incompatible');
        }
        let migrated = false;
        if (parsed.schema_version === 2) {
            const fullGameBuilds = {};
            for (const [platform, versions] of Object.entries(parsed.builds || {})) {
                fullGameBuilds[platform] = {};
                for (const [buildId, record] of Object.entries(versions || {})) {
                    fullGameBuilds[platform][buildId] = { ...record, role: 'full-game' };
                    versions[buildId] = { ...record, role: 'dedicated' };
                }
            }
            parsed = { ...parsed, schema_version: 3, full_game_builds: fullGameBuilds };
            migrated = true;
        }
        if (parsed.schema_version === 3) {
            parsed = {
                ...parsed,
                schema_version: 4,
                run_id: "",
                compatibility_key: "",
                pending_release: null,
            };
            migrated = true;
        }
        if (parsed.schema_version === 4) {
            const pending = parsed.pending_release && typeof parsed.pending_release === 'object' &&
                !Array.isArray(parsed.pending_release)
                ? {
                    ...parsed.pending_release,
                    required_trainers: [],
                    phase_revision: parsed.revision,
                    collect_until_ms: this.now() + this.leaseSeconds * 1000,
                }
                : parsed.pending_release;
            parsed = {
                ...parsed,
                schema_version: CONTROL_SCHEMA_VERSION,
                pending_release: pending,
                known_dedicated_trainers: [],
            };
            migrated = true;
        }
        if (parsed.schema_version !== CONTROL_SCHEMA_VERSION) {
            throw new Error('training-control state schema is incompatible');
        }
        if (migrated) atomicWriteJson(this.statePath, parsed);
        if (!Number.isInteger(parsed.revision) || parsed.revision < 0) {
            throw new Error('training-control state revision is invalid');
        }
        if (typeof parsed.training_enabled !== 'boolean') {
            throw new Error('training-control state training_enabled is invalid');
        }
        parsed.environment_args = normalizeEnvironmentArgs(parsed.environment_args || []);
        if (typeof parsed.run_id !== 'string' ||
            (parsed.run_id && !/^[A-Za-z0-9._-]+$/.test(parsed.run_id))) {
            throw new Error('training-control state run_id is invalid');
        }
        if (typeof parsed.compatibility_key !== 'string' ||
            (parsed.compatibility_key && !/^[0-9a-f]{64}$/.test(parsed.compatibility_key))) {
            throw new Error('training-control state compatibility_key is invalid');
        }
        if (parsed.pending_release !== null) {
            const pending = parsed.pending_release;
            if (!pending || typeof pending !== 'object' || Array.isArray(pending) ||
                typeof pending.build_id !== 'string' || !/^[A-Za-z0-9._-]+$/.test(pending.build_id) ||
                typeof pending.run_id !== 'string' || !/^[A-Za-z0-9._-]+$/.test(pending.run_id) ||
                typeof pending.compatibility_key !== 'string' || !/^[0-9a-f]{64}$/.test(pending.compatibility_key) ||
                typeof pending.incompatible !== 'boolean' ||
                !['preparing', 'rolling', 'stopping'].includes(pending.phase) ||
                !Array.isArray(pending.required_trainers) ||
                !Number.isInteger(pending.phase_revision) || pending.phase_revision < 0 ||
                pending.phase_revision > parsed.revision ||
                !Number.isFinite(pending.collect_until_ms) || pending.collect_until_ms < 0) {
                throw new Error('training-control pending release is invalid');
            }
            const trainerIds = new Set();
            for (const trainer of pending.required_trainers) {
                if (!trainer || typeof trainer !== 'object' || Array.isArray(trainer) ||
                    typeof trainer.trainer_id !== 'string' ||
                    !/^[A-Za-z0-9._-]+$/.test(trainer.trainer_id) ||
                    typeof trainer.platform !== 'string' ||
                    !/^[A-Za-z0-9._-]+$/.test(trainer.platform) ||
                    trainerIds.has(trainer.trainer_id)) {
                    throw new Error('training-control pending release trainer barrier is invalid');
                }
                trainerIds.add(trainer.trainer_id);
            }
        }
        if (!Array.isArray(parsed.known_dedicated_trainers)) {
            throw new Error('training-control known trainer registry is invalid');
        }
        const knownTrainerIds = new Set();
        for (const trainer of parsed.known_dedicated_trainers) {
            if (!trainer || typeof trainer !== 'object' || Array.isArray(trainer) ||
                typeof trainer.trainer_id !== 'string' ||
                !/^[A-Za-z0-9._-]+$/.test(trainer.trainer_id) ||
                typeof trainer.platform !== 'string' ||
                !/^[A-Za-z0-9._-]+$/.test(trainer.platform) ||
                !Number.isFinite(trainer.last_seen_ms) || trainer.last_seen_ms < 0 ||
                knownTrainerIds.has(trainer.trainer_id)) {
                throw new Error('training-control known trainer registry is invalid');
            }
            knownTrainerIds.add(trainer.trainer_id);
        }
        if (typeof parsed.canonical_build_id !== 'string' ||
            (parsed.canonical_build_id && !/^[A-Za-z0-9._-]+$/.test(parsed.canonical_build_id))) {
            throw new Error('training-control state canonical_build_id is invalid');
        }
        if (!parsed.builds || typeof parsed.builds !== 'object' || Array.isArray(parsed.builds) ||
            !parsed.full_game_builds || typeof parsed.full_game_builds !== 'object' ||
            Array.isArray(parsed.full_game_builds)) {
            throw new Error('training-control state build catalogs are invalid');
        }
        this._validateStoredBuilds(parsed);
        if (parsed.training_enabled && !parsed.canonical_build_id) {
            throw new Error('training-control persisted training state has no canonical build');
        }
        return parsed;
    }

    _catalogForRole(role) {
        return role === 'full-game' ? this.state.full_game_builds : this.state.builds;
    }

    _validateStoredBuilds(state) {
        const safeIdentity = /^[A-Za-z0-9._-]+$/;
        const catalogs = [
            ['dedicated', state.builds],
            ['full-game', state.full_game_builds],
        ];
        for (const [role, catalog] of catalogs) {
            for (const [platform, versions] of Object.entries(catalog)) {
                if (!safeIdentity.test(platform) ||
                    !versions || typeof versions !== 'object' || Array.isArray(versions)) {
                    throw new Error(
                        'training-control persisted build catalog is invalid for ' +
                        role + '/' + platform);
                }
                for (const [buildId, record] of Object.entries(versions)) {
                    if (!safeIdentity.test(buildId) ||
                        !record || typeof record !== 'object' || Array.isArray(record) ||
                        record.role !== role ||
                        record.platform !== platform ||
                        record.build_id !== buildId ||
                        typeof record.archive_sha256 !== 'string' ||
                        !/^[0-9a-f]{64}$/.test(record.archive_sha256) ||
                        !Number.isInteger(record.archive_size_bytes) ||
                        record.archive_size_bytes <= 0 ||
                        typeof record.archive_path !== 'string' || !record.archive_path ||
                        typeof record.entrypoint !== 'string' || !record.entrypoint) {
                        throw new Error(
                            'training-control persisted build descriptor is invalid for ' +
                            role + '/' + platform + '/' + buildId);
                    }
                    record.archive_path = path.resolve(record.archive_path);
                }
            }
        }

        if (!state.canonical_build_id) return;
        let canonicalArtifacts = 0;
        const verifiedPaths = new Set();
        for (const [role, catalog] of catalogs) {
            for (const [platform, versions] of Object.entries(catalog)) {
                const record = versions[state.canonical_build_id];
                if (!record) continue;
                canonicalArtifacts++;
                if (verifiedPaths.has(record.archive_path)) continue;
                verifiedPaths.add(record.archive_path);
                const stats = fs.statSync(record.archive_path);
                if (!stats.isFile() || stats.size !== record.archive_size_bytes) {
                    throw new Error(
                        'training-control canonical artifact size is invalid for ' +
                        role + '/' + platform);
                }
                if (sha256File(record.archive_path) !== record.archive_sha256) {
                    throw new Error(
                        'training-control canonical artifact hash is invalid for ' +
                        role + '/' + platform);
                }
            }
        }
        if (canonicalArtifacts === 0) {
            throw new Error('training-control canonical build has no published role/platform artifact');
        }
    }

    _hasBuild(buildId) {
        if (!buildId) return false;
        return [this.state.builds, this.state.full_game_builds].some(
            catalog => Object.values(catalog).some(
                versions => versions && versions[buildId]));
    }

    _missingActiveTargets(buildId) {
        const activeTargets = new Map();
        for (const record of this._releaseBarrierTrainers()) {
            activeTargets.set('dedicated|' + record.platform, {
                role: 'dedicated',
                platform: record.platform,
            });
        }
        return [...activeTargets.values()]
            .filter(target => !this._catalogForRole(target.role)[target.platform]?.[buildId])
            .map(target => target.role + ':' + target.platform)
            .sort();
    }

    _dedicatedBarrierSort(left, right) {
        const leftCentral = left.trainer_id === 'central-learner' ? 1 : 0;
        const rightCentral = right.trainer_id === 'central-learner' ? 1 : 0;
        return leftCentral - rightCentral ||
            left.trainer_id.localeCompare(right.trainer_id);
    }

    _activeDedicatedTrainers() {
        const cutoff = this.now() - this.leaseSeconds * 1000;
        return [...this.trainers.values()]
            .filter(record => record.role === 'dedicated' && record.last_seen_ms >= cutoff)
            .sort((left, right) => this._dedicatedBarrierSort(left, right));
    }

    _releaseBarrierTrainers() {
        const cutoff = this.now() - this.leaseSeconds * 1000;
        const trainers = new Map();
        for (const record of this.state.known_dedicated_trainers) {
            if (record.last_seen_ms < cutoff) continue;
            trainers.set(record.trainer_id, {
                trainer_id: record.trainer_id,
                platform: record.platform,
            });
        }
        for (const record of this._activeDedicatedTrainers()) {
            trainers.set(record.trainer_id, {
                trainer_id: record.trainer_id,
                platform: record.platform,
            });
        }
        return [...trainers.values()]
            .sort((left, right) => this._dedicatedBarrierSort(left, right));
    }

    _rememberDedicatedTrainer(record) {
        if (record.role !== 'dedicated') return false;
        const refreshAfterMs = Math.max(1000, this.leaseSeconds * 500);
        const existing = this.state.known_dedicated_trainers.find(
            item => item.trainer_id === record.trainer_id);
        if (!existing) {
            this.state.known_dedicated_trainers.push({
                trainer_id: record.trainer_id,
                platform: record.platform,
                last_seen_ms: record.last_seen_ms,
            });
            this.state.known_dedicated_trainers.sort(
                (left, right) => this._dedicatedBarrierSort(left, right));
            return true;
        }
        if (existing.platform !== record.platform) {
            existing.platform = record.platform;
            existing.last_seen_ms = record.last_seen_ms;
            return true;
        }
        if (record.last_seen_ms - existing.last_seen_ms >= refreshAfterMs) {
            existing.last_seen_ms = record.last_seen_ms;
            return true;
        }
        return false;
    }

    _ensurePendingTrainer(record) {
        const pending = this.state.pending_release;
        if (!pending || record.role !== 'dedicated') return false;
        const existing = pending.required_trainers.find(
            item => item.trainer_id === record.trainer_id);
        if (existing) {
            if (existing.platform !== record.platform) {
                throw Object.assign(
                    new Error(
                        'trainer ' + record.trainer_id +
                        ' changed platform during an active release barrier'),
                    { statusCode: 409 });
            }
            return false;
        }

        // Compatible rollout membership is a shrinking snapshot. Trainers that appear after
        // staging (or return after their lease expired and they were pruned) can continue on the
        // compatible canonical release and reconcile after promotion; they must not re-expand and
        // deadlock the in-flight barrier. The only compatible exception is the explicit
        // recollection window used when a restart/migration staged with no known trainers.
        if (!pending.incompatible && pending.collect_until_ms <= this.now()) {
            return false;
        }

        // Incompatible run cutovers remain strict: any dedicated trainer that appears before
        // promotion must join the stop barrier so old-run training cannot survive the cutover.
        pending.required_trainers.push({
            trainer_id: record.trainer_id,
            platform: record.platform,
        });
        pending.required_trainers.sort(
            (left, right) => this._dedicatedBarrierSort(left, right));
        return true;
    }

    _requiredTrainerRecord(spec) {
        const record = this.trainers.get(spec.trainer_id);
        if (!record ||
            record.role !== 'dedicated' ||
            record.platform !== spec.platform ||
            record.last_seen_ms < this.now() - this.leaseSeconds * 1000) {
            return null;
        }
        return record;
    }

    _pendingRecordFor(role, platform) {
        const pending = this.state.pending_release;
        if (!pending) return null;
        return this._catalogForRole(role)[platform]?.[pending.build_id] || null;
    }

    _allDedicatedPrepared(pending) {
        return pending.required_trainers.every(spec => {
            const record = this._requiredTrainerRecord(spec);
            return Boolean(record) && (
                record.build_id === pending.build_id ||
                record.prepared_build_id === pending.build_id);
        });
    }

    _pruneExpiredCompatibleBarrierTrainers(pending) {
        if (!pending || pending.incompatible) return false;
        const cutoff = this.now() - this.leaseSeconds * 1000;
        const kept = [];
        let changed = false;
        for (const spec of pending.required_trainers) {
            const current = this.trainers.get(spec.trainer_id);
            let lastSeen = (
                current &&
                current.role === 'dedicated' &&
                current.platform === spec.platform
            ) ? current.last_seen_ms : null;
            if (lastSeen === null) {
                const known = this.state.known_dedicated_trainers.find(
                    item => item.trainer_id === spec.trainer_id &&
                        item.platform === spec.platform);
                if (known) lastSeen = known.last_seen_ms;
            }
            if (lastSeen !== null && lastSeen < cutoff) {
                changed = true;
                continue;
            }
            kept.push(spec);
        }
        if (!changed) return false;
        pending.required_trainers = kept;
        this.state.revision++;
        this._persist();
        return true;
    }

    _trainerHealthyOnPending(spec, pending) {
        const record = this._requiredTrainerRecord(spec);
        if (!record) return false;
        const artifact = this._catalogForRole('dedicated')[spec.platform]?.[pending.build_id];
        return Boolean(artifact) &&
            record.process_state === 'running' &&
            !record.last_error &&
            record.build_id === pending.build_id &&
            record.build_sha256 === artifact.archive_sha256 &&
            record.applied_revision >= pending.phase_revision;
    }

    _trainerStoppedForPending(spec, pending) {
        const record = this._requiredTrainerRecord(spec);
        return Boolean(record) &&
            record.process_state === 'stopped' &&
            record.applied_revision >= pending.phase_revision;
    }

    _rollingTargetId() {
        const pending = this.state.pending_release;
        if (!pending || pending.phase !== 'rolling') return null;
        const remaining = pending.required_trainers
            .filter(spec => !this._trainerHealthyOnPending(spec, pending));
        return remaining.length ? remaining[0].trainer_id : null;
    }

    _promotePendingRelease() {
        const pending = this.state.pending_release;
        if (!pending) return false;
        this.state.canonical_build_id = pending.build_id;
        this.state.run_id = pending.run_id;
        this.state.compatibility_key = pending.compatibility_key;
        this.state.pending_release = null;
        this.state.revision++;
        this._persist();
        return true;
    }

    _advanceRollout() {
        const pending = this.state.pending_release;
        if (!pending) return false;
        if (pending.collect_until_ms > this.now()) return false;

        this._pruneExpiredCompatibleBarrierTrainers(pending);

        if (pending.phase === 'preparing') {
            if (!this._allDedicatedPrepared(pending)) return false;
            if (pending.required_trainers.length === 0 ||
                (!this.state.training_enabled && !pending.incompatible)) {
                return this._promotePendingRelease();
            }
            pending.phase = pending.incompatible ? 'stopping' : 'rolling';
            this.state.revision++;
            pending.phase_revision = this.state.revision;
            this._persist();
            return true;
        }

        if (pending.phase === 'rolling') {
            if (pending.required_trainers.every(
                spec => this._trainerHealthyOnPending(spec, pending))) {
                return this._promotePendingRelease();
            }
            return false;
        }

        if (pending.phase === 'stopping') {
            if (pending.required_trainers.every(
                spec => this._trainerStoppedForPending(spec, pending))) {
                return this._promotePendingRelease();
            }
        }
        return false;
    }

    stageRelease({ buildId, runId, compatibilityKey, incompatible = false }) {
        buildId = requireString(buildId, 'build_id', 128);
        runId = requireString(runId, 'run_id', 128);
        compatibilityKey = requireString(compatibilityKey, 'compatibility_key', 64).toLowerCase();
        if (!/^[A-Za-z0-9._-]+$/.test(buildId) ||
            !/^[A-Za-z0-9._-]+$/.test(runId) ||
            !/^[0-9a-f]{64}$/.test(compatibilityKey)) {
            throw Object.assign(new Error('release identity is malformed'), { statusCode: 400 });
        }
        if (typeof incompatible !== 'boolean') {
            throw Object.assign(new Error('incompatible must be boolean'), { statusCode: 400 });
        }
        if (!this._hasBuild(buildId)) {
            throw Object.assign(
                new Error('release build has no published platform artifact'),
                { statusCode: 409 });
        }
        const missingTargets = this._missingActiveTargets(buildId);
        if (missingTargets.length > 0) {
            throw Object.assign(
                new Error(
                    'release is missing active dedicated role/platform artifacts: ' +
                    missingTargets.join(', ')),
                { statusCode: 409 });
        }
        if (this.state.canonical_build_id === buildId &&
            this.state.run_id === runId &&
            this.state.compatibility_key === compatibilityKey &&
            this.state.pending_release === null) {
            return this.desiredState();
        }
        const existingPending = this.state.pending_release;
        if (existingPending &&
            existingPending.build_id === buildId &&
            existingPending.run_id === runId &&
            existingPending.compatibility_key === compatibilityKey &&
            existingPending.incompatible === incompatible) {
            this._advanceRollout();
            return this.desiredState();
        }
        if (existingPending) {
            throw Object.assign(
                new Error(
                    'another release rollout is already pending: ' +
                    existingPending.build_id + ' (' + existingPending.phase + ')'),
                { statusCode: 409 });
        }

        const requiredTrainers = this._releaseBarrierTrainers();
        this.state.pending_release = {
            build_id: buildId,
            run_id: runId,
            compatibility_key: compatibilityKey,
            incompatible,
            phase: 'preparing',
            required_trainers: requiredTrainers,
            phase_revision: this.state.revision + 1,
            collect_until_ms: (
                this.state.training_enabled && requiredTrainers.length === 0
                    ? this.now() + this.leaseSeconds * 1000
                    : 0
            ),
        };
        this.state.revision++;
        this._persist();
        this._advanceRollout();
        return this.desiredState();
    }

    _persist() {
        atomicWriteJson(this.statePath, this.state);
    }

    setDesiredState(patch) {
        if (!patch || typeof patch !== 'object' || Array.isArray(patch)) {
            throw Object.assign(new Error('desired-state patch must be an object'), { statusCode: 400 });
        }

        if (Object.prototype.hasOwnProperty.call(patch, 'canonical_build_id')) {
            throw Object.assign(
                new Error(
                    'canonical_build_id is release-owned; activate builds through the staged release endpoint'),
                { statusCode: 409 });
        }

        const requestedBuildId = this.state.canonical_build_id;
        const requestedTraining = Object.prototype.hasOwnProperty.call(patch, 'training_enabled')
            ? patch.training_enabled
            : this.state.training_enabled;
        if (typeof requestedTraining !== 'boolean') {
            throw Object.assign(new Error('training_enabled must be boolean'), { statusCode: 400 });
        }
        if (requestedTraining && !requestedBuildId) {
            throw Object.assign(
                new Error('training cannot start without a canonical_build_id'),
                { statusCode: 409 });
        }
        if (requestedTraining) {
            const missingTargets = this._missingActiveTargets(requestedBuildId);
            if (missingTargets.length > 0) {
                throw Object.assign(
                    new Error(
                        'canonical build is missing active dedicated role/platform artifacts: ' +
                        missingTargets.join(', ')),
                    { statusCode: 409 });
            }
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

    publishArtifact({ role, platform, buildId, archivePath, entrypoint }) {
        role = role == null ? 'dedicated' : requireRole(role);
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
        const platformRoot = path.join(this.artifactRoot, role, platform);
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
            role,
            platform,
            build_id: buildId,
            archive_path: destination,
            archive_sha256: archiveSha256,
            archive_size_bytes: stats.size,
            entrypoint,
        };
        const catalog = this._catalogForRole(role);
        if (!catalog[platform] ||
            typeof catalog[platform] !== 'object' ||
            Array.isArray(catalog[platform])) {
            catalog[platform] = {};
        }
        const previous = catalog[platform][buildId];
        if (previous) {
            if (previous.archive_sha256 !== record.archive_sha256 ||
                previous.entrypoint !== record.entrypoint ||
                previous.archive_size_bytes !== record.archive_size_bytes) {
                throw Object.assign(
                    new Error('published role/platform/build identity is immutable; use a new build_id'),
                    { statusCode: 409 });
            }
            return publicBuildDescriptor(previous);
        }
        catalog[platform][buildId] = record;
        if (buildId === this.state.canonical_build_id) {
            this.state.revision++;
        }
        this._persist();
        return publicBuildDescriptor(record);
    }

    desiredState() {
        const builds = { dedicated: {}, 'full-game': {} };
        if (this.state.canonical_build_id) {
            for (const role of VALID_ROLES) {
                const catalog = this._catalogForRole(role);
                for (const [platform, versions] of Object.entries(catalog)) {
                    const record = versions && versions[this.state.canonical_build_id];
                    if (record) builds[role][platform] = publicBuildDescriptor(record);
                }
            }
        }
        const pending = this.state.pending_release
            ? {
                ...this.state.pending_release,
                required_trainers: this.state.pending_release.required_trainers.map(
                    trainer => ({ ...trainer })),
            }
            : null;
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            revision: this.state.revision,
            training_enabled: this.state.training_enabled,
            environment_args: [...this.state.environment_args],
            canonical_build_id: this.state.canonical_build_id,
            run_id: this.state.run_id,
            compatibility_key: this.state.compatibility_key,
            pending_release: pending,
            lease_seconds: this.leaseSeconds,
            builds,
        };
    }

    stateFor({ trainerId, role, platform }) {
        trainerId = requireString(trainerId, 'trainer_id', 128);
        role = requireRole(role);
        platform = requireString(platform, 'platform', 64);
        this._advanceRollout();

        const catalog = this._catalogForRole(role);
        let desiredBuildId = this.state.canonical_build_id;
        let forcedStop = false;
        const pending = this.state.pending_release;
        if (role === 'dedicated' && pending) {
            if (pending.phase === 'rolling') {
                const current = this.trainers.get(trainerId);
                const alreadyRolled = current && current.build_id === pending.build_id;
                const recollecting = pending.collect_until_ms > this.now();
                if (alreadyRolled ||
                    (!recollecting && this._rollingTargetId() === trainerId)) {
                    desiredBuildId = pending.build_id;
                }
            } else if (pending.phase === 'stopping') {
                forcedStop = true;
            }
        }

        const buildRecord = desiredBuildId &&
            catalog[platform] &&
            catalog[platform][desiredBuildId];
        const canTrain = this.state.training_enabled && Boolean(buildRecord) && !forcedStop;
        const desiredMode = canTrain
            ? 'training'
            : role === 'full-game' ? 'inference' : 'stopped';
        const prepareRecord = pending
            ? this._pendingRecordFor(role, platform)
            : null;
        const envOptimizer = role === 'dedicated'
            ? this.envOptimizer.snapshot(trainerId)
            : null;
        const workerEnvCount = envOptimizer && Number.isInteger(envOptimizer.desired_envs)
            ? envOptimizer.desired_envs
            : null;
        return {
            schema_version: CONTROL_SCHEMA_VERSION,
            trainer_id: trainerId,
            role,
            platform,
            revision: this.state.revision,
            training_enabled: this.state.training_enabled,
            desired_mode: desiredMode,
            environment_args: [...this.state.environment_args],
            worker_env_count: workerEnvCount,
            env_optimizer: envOptimizer,
            canonical_build_id: this.state.canonical_build_id,
            desired_build_id: desiredBuildId,
            run_id: this.state.run_id,
            compatibility_key: this.state.compatibility_key,
            pending_release: pending
                ? {
                    ...pending,
                    required_trainers: pending.required_trainers.map(trainer => ({ ...trainer })),
                }
                : null,
            lease_seconds: this.leaseSeconds,
            build: publicBuildDescriptor(buildRecord),
            prepare_build: publicBuildDescriptor(prepareRecord),
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
            prepared_build_id: typeof payload.prepared_build_id === 'string'
                ? payload.prepared_build_id.slice(0, 128)
                : '',
            applied_revision: Number.isInteger(payload.applied_revision) ? payload.applied_revision : -1,
            last_error: typeof payload.last_error === 'string' ? payload.last_error.slice(0, 2048) : '',
            metrics: payload.metrics && typeof payload.metrics === 'object' && !Array.isArray(payload.metrics)
                ? payload.metrics
                : {},
            worker_capacity: normalizeCapacity(payload.worker_capacity),
            last_seen_ms: now,
        };
        let persistentHeartbeatStateChanged = false;
        if (role === 'dedicated') {
            persistentHeartbeatStateChanged = this._ensurePendingTrainer(record) ||
                persistentHeartbeatStateChanged;
            persistentHeartbeatStateChanged = this._rememberDedicatedTrainer(record) ||
                persistentHeartbeatStateChanged;
        }
        const canonicalBuild = this._catalogForRole(role)[platform]?.[this.state.canonical_build_id];
        const optimizerContextKey = [
            this.state.run_id,
            this.state.canonical_build_id,
            JSON.stringify(this.state.environment_args),
        ].join('|');
        record.env_optimizer = this.envOptimizer.update(record, {
            now,
            contextKey: optimizerContextKey,
            enabled: this.state.training_enabled &&
                !this.state.pending_release &&
                Boolean(canonicalBuild),
        });
        this.trainers.set(trainerId, record);
        if (persistentHeartbeatStateChanged) this._persist();
        this._advanceRollout();
        return this.stateFor({ trainerId, role, platform });
    }

    status() {
        this._advanceRollout();
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

    artifact(role, platform, buildId) {
        role = requireRole(role);
        platform = requireString(platform, 'platform', 64);
        buildId = requireString(buildId, 'build_id', 128);
        return this._catalogForRole(role)[platform]?.[buildId] || null;
    }

    appendTrainerLog({ trainerId, runId, relativePath, offset, reset, data }) {
        trainerId = requireString(trainerId, 'trainer_id', 128);
        runId = requireString(runId, 'run_id', 128);
        relativePath = requireString(relativePath, 'path', 1024).replace(/\\/g, '/');
        if (!/^[A-Za-z0-9._-]+$/.test(trainerId) ||
            !/^[A-Za-z0-9._-]+$/.test(runId) ||
            relativePath.startsWith('/') ||
            relativePath.split('/').some(part => !part || part === '.' || part === '..')) {
            throw Object.assign(new Error('trainer log identity/path is unsafe'), { statusCode: 400 });
        }
        if (!Number.isInteger(offset) || offset < 0) {
            throw Object.assign(new Error('log offset must be a non-negative integer'), { statusCode: 400 });
        }
        if (!Buffer.isBuffer(data) || data.length > 1024 * 1024) {
            throw Object.assign(new Error('log chunk must be at most 1 MiB'), { statusCode: 413 });
        }
        const root = path.join(this.logRoot, runId, trainerId);
        const destination = path.resolve(root, relativePath);
        const resolvedRoot = path.resolve(root) + path.sep;
        if (!destination.startsWith(resolvedRoot)) {
            throw Object.assign(new Error('trainer log path escapes its run root'), { statusCode: 400 });
        }
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        let current = fs.existsSync(destination) ? fs.statSync(destination).size : 0;
        if (reset) {
            if (offset !== 0) {
                const error = Object.assign(
                    new Error('trainer log reset requires offset 0'),
                    { statusCode: 409 });
                error.expectedOffset = current;
                throw error;
            }
            fs.writeFileSync(destination, Buffer.alloc(0), { mode: 0o600 });
            current = 0;
        } else if (current !== offset) {
            const error = Object.assign(new Error('trainer log offset mismatch'), { statusCode: 409 });
            error.expectedOffset = current;
            throw error;
        }
        if (data.length > 0) {
            fs.appendFileSync(destination, data, { mode: 0o600 });
        }
        return { next_offset: current + data.length };
    }

}

function authenticated(request, tokens) {
    const header = request.headers && request.headers.authorization;
    if (typeof header !== 'string' || !header.startsWith('Bearer ')) return false;
    const supplied = Buffer.from(header.slice('Bearer '.length), 'utf8');
    return tokens.some(token => {
        if (!token) return false;
        const expected = Buffer.from(token, 'utf8');
        return supplied.length === expected.length && crypto.timingSafeEqual(supplied, expected);
    });
}

function createTrainingControlHandler(store, token, adminToken = null) {
    token = requireString(token, 'training-control token', 4096);
    if (adminToken !== null && adminToken !== undefined) {
        adminToken = requireString(adminToken, 'training-control admin token', 4096);
    }
    return async (request, response) => {
        try {
            const url = new URL(request.url, 'http://training-control.local');
            const adminPath = url.pathname.startsWith('/v1/admin/');
            if (adminPath && !adminToken) {
                sendJson(response, 503, { error: 'admin-token-not-configured' });
                return;
            }
            const allowedTokens = adminPath ? [adminToken] : [token, adminToken];
            if (!authenticated(request, allowedTokens)) {
                sendJson(response, 401, { error: 'unauthorized' });
                return;
            }
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
            if (request.method === 'POST' && url.pathname === '/v1/admin/release') {
                const body = await readJsonBody(request);
                sendJson(response, 200, store.stageRelease({
                    buildId: body.build_id,
                    runId: body.run_id,
                    compatibilityKey: body.compatibility_key,
                    incompatible: body.incompatible,
                }));
                return;
            }
            if (request.method === 'POST' && url.pathname === '/v1/admin/artifact') {
                const body = await readJsonBody(request);
                sendJson(response, 200, store.publishArtifact({
                    role: body.role,
                    platform: body.platform,
                    buildId: body.build_id,
                    archivePath: body.archive_path,
                    entrypoint: body.entrypoint,
                }));
                return;
            }
            if (request.method === 'POST' && url.pathname === '/v1/log') {
                const offset = Number(url.searchParams.get('offset'));
                const result = store.appendTrainerLog({
                    trainerId: url.searchParams.get('trainer_id'),
                    runId: url.searchParams.get('run_id'),
                    relativePath: url.searchParams.get('path'),
                    offset,
                    reset: url.searchParams.get('reset') === '1',
                    data: await readRawBody(request),
                });
                sendJson(response, 200, result);
                return;
            }
            const artifactMatch = request.method === 'GET' &&
                url.pathname.match(/^\/v1\/artifact\/([^/]+)\/([^/]+)\/([^/]+)$/);
            if (artifactMatch) {
                const role = decodeURIComponent(artifactMatch[1]);
                const platform = decodeURIComponent(artifactMatch[2]);
                const buildId = decodeURIComponent(artifactMatch[3]);
                const record = store.artifact(role, platform, buildId);
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
            const body = {
                error: statusCode >= 500 ? 'internal-error' : 'invalid-request',
                message: error.message,
            };
            if (Number.isInteger(error.expectedOffset)) body.expected_offset = error.expectedOffset;
            sendJson(response, statusCode, body);
        }
    };
}

function startTrainingControl(options = {}) {
    const token = options.token || process.env.BEES_TRAINING_CONTROL_TOKEN;
    const adminToken = options.adminToken || process.env.BEES_TRAINING_CONTROL_ADMIN_TOKEN || null;
    if (!token) throw new Error('BEES_TRAINING_CONTROL_TOKEN is required for training control.');
    const port = Number(options.port || process.env.BEES_TRAINING_CONTROL_PORT || DEFAULT_PORT);
    const host = options.host || process.env.BEES_TRAINING_CONTROL_HOST || DEFAULT_HOST;
    if (!Number.isInteger(port) || port <= 0 || port > 65535) {
        throw new Error('training-control port must be in 1-65535');
    }
    const store = options.store || new TrainingControlStore({
        statePath: options.statePath || process.env.BEES_TRAINING_CONTROL_STATE,
        artifactRoot: options.artifactRoot || process.env.BEES_TRAINING_ARTIFACT_ROOT,
        logRoot: options.logRoot || process.env.BEES_TRAINING_LOG_ROOT,
        leaseSeconds: options.leaseSeconds || process.env.BEES_TRAINING_CONTROL_LEASE_SECONDS,
    });
    const httpModule = options.httpModule || http;
    const server = httpModule.createServer(createTrainingControlHandler(store, token, adminToken));
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
