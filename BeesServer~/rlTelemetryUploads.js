'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const fsp = fs.promises;

const RL_TELEMETRY_REQUEST_TYPES = new Set([
    'rl-telemetry-begin',
    'rl-telemetry-chunk',
    'rl-telemetry-complete',
]);
const RL_TELEMETRY_POLICY = Object.freeze({
    schemaVersion: 1,
    behaviorName: 'BeesRL1v1',
    policyAbiVersion: 20,
    policySignature: 'bees-rl-v20|behavior=BeesRL1v1|network=ff-128x3|normalize=true|obs=7614|tail=episode-progress+20-reserved|cont=16|disc=2x5,5|coord-frame=team-episode-distinct-quarter-turn|weapon-aim=slotwise-xy|weapon-fire=slotwise-cease-or-fire|healing=weapon-exclusive|weapon-ready=rl-latched-until-fire|shiptype=fixed-scrambled-scalar24|weapontype=fixed-scrambled-scalar10|mapbits=4|shipmap=v1-0..23|weaponmap=v1-0..9|allies=64|enemies=64|weapons=5|entity-weapons=5|enemy-mounts=0|mining=8|map-objects=64|moving-asteroids=48|self=25|ship-id=episode-permuted-scalar23|capability=12|parent-carrier=40|entity-core=14|entity=40|ally=44-with-private-comm4|communication=4-continuous-private-allied|self-weapon=15|observed-weapon=5|weapon-observation=split-self-vs-observed|mining-slot=7|map-slot=12|moving-asteroid-slot=11|objective=16|grid=21x21-cell6|exploration-grid=16x16-team-shared-sight-recency|entity-order=distance,type,fleet-id,runtime-id',
    observationSchemaVersion: 11,
    actionSchemaVersion: 9,
    rewardSchemaVersion: 3,
    scenarioSchemaVersion: 1,
});

const DEFAULT_MAX_PAYLOAD_BYTES = 16 * 1024 * 1024;
const DEFAULT_MAX_CHUNK_BYTES = 512 * 1024;
const DEFAULT_MAX_ACTIVE_UPLOADS_PER_USER = 2;
const DEFAULT_MAX_ACTIVE_UPLOADS_GLOBAL = 64;
const DEFAULT_USER_BYTES_PER_WINDOW = 64 * 1024 * 1024;
const DEFAULT_RATE_WINDOW_MS = 60 * 60 * 1000;
const DEFAULT_UPLOAD_IDLE_TIMEOUT_MS = 5 * 60 * 1000;

class RlTelemetryUploadError extends Error {
    constructor(statusCode, code, message) {
        super(message);
        this.name = 'RlTelemetryUploadError';
        this.statusCode = statusCode;
        this.code = code;
    }
}

function sha256Bytes(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function requireString(value, label, maxLength = 256) {
    if (typeof value !== 'string' || !value.trim() || value.length > maxLength) {
        throw new RlTelemetryUploadError(
            400,
            'invalid-field',
            `${label} must be a non-empty string up to ${maxLength} characters.`,
        );
    }
    return value.trim();
}

function requireInteger(value, label, minimum, maximum) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
        throw new RlTelemetryUploadError(
            400,
            'invalid-field',
            `${label} must be an integer in [${minimum}, ${maximum}].`,
        );
    }
    return value;
}

function requireSha256(value, label) {
    const normalized = requireString(value, label, 64).toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(normalized)) {
        throw new RlTelemetryUploadError(
            400,
            'invalid-hash',
            `${label} must be a 64-character hexadecimal SHA-256.`,
        );
    }
    return normalized;
}

function requireDeploymentId(value) {
    const deploymentId = requireString(value, 'DeploymentId', 64);
    if (!/^deploy-[0-9a-f]{24}$/.test(deploymentId)) {
        throw new RlTelemetryUploadError(
            400,
            'invalid-deployment-id',
            'DeploymentId must be deploy- followed by 24 lowercase hexadecimal characters.',
        );
    }
    return deploymentId;
}

function decodeCanonicalBase64(value, maxBytes) {
    if (typeof value !== 'string' || value.length === 0 ||
        value.length > Math.ceil(maxBytes / 3) * 4 + 4 ||
        value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) {
        throw new RlTelemetryUploadError(400, 'invalid-chunk', 'Chunk Data must be canonical base64.');
    }
    const decoded = Buffer.from(value, 'base64');
    if (decoded.length === 0 || decoded.length > maxBytes || decoded.toString('base64') !== value) {
        throw new RlTelemetryUploadError(
            413,
            'chunk-too-large',
            'Chunk exceeds the configured raw-byte limit or is not canonical base64.',
        );
    }
    return decoded;
}

async function safeUnlink(filePath) {
    try {
        await fsp.unlink(filePath);
    } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
    }
}

async function readJsonIfPresent(filePath) {
    try {
        return JSON.parse(await fsp.readFile(filePath, 'utf8'));
    } catch (error) {
        if (error?.code === 'ENOENT') return null;
        if (error instanceof SyntaxError) {
            throw new RlTelemetryUploadError(500, 'archive-corrupt', `Invalid JSON at ${filePath}.`);
        }
        throw error;
    }
}

async function verifyImmutablePayload(filePath, expectedSha256, expectedBytes) {
    let bytes;
    try {
        bytes = await fsp.readFile(filePath);
    } catch (error) {
        if (error?.code === 'ENOENT') {
            throw new RlTelemetryUploadError(
                500,
                'archive-corrupt',
                'Completed telemetry metadata points at a missing payload archive.',
            );
        }
        throw error;
    }
    if (bytes.length !== expectedBytes || sha256Bytes(bytes) !== expectedSha256) {
        throw new RlTelemetryUploadError(
            500,
            'archive-corrupt',
            'Completed telemetry payload archive does not match its immutable metadata.',
        );
    }
}

async function writeImmutableFile(source, destination, expectedSha256) {
    await fsp.mkdir(path.dirname(destination), { recursive: true });
    try {
        const existing = await fsp.readFile(destination);
        if (sha256Bytes(existing) === expectedSha256) return false;
        throw new RlTelemetryUploadError(500, 'archive-conflict', `Immutable archive conflict at ${destination}.`);
    } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
    }

    const bytes = await fsp.readFile(source);
    if (sha256Bytes(bytes) !== expectedSha256) {
        throw new RlTelemetryUploadError(422, 'payload-hash-mismatch', 'Telemetry changed while being quarantined.');
    }
    const temp = path.join(path.dirname(destination), `.${path.basename(destination)}.${process.pid}.${crypto.randomUUID()}.tmp`);
    try {
        const handle = await fsp.open(temp, 'wx');
        try {
            await handle.writeFile(bytes);
            await handle.sync();
        } finally {
            await handle.close();
        }
        try {
            await fsp.link(temp, destination);
            return true;
        } catch (error) {
            if (error?.code !== 'EEXIST') throw error;
            const winner = await fsp.readFile(destination);
            if (sha256Bytes(winner) !== expectedSha256) {
                throw new RlTelemetryUploadError(500, 'archive-conflict', `Immutable archive conflict at ${destination}.`);
            }
            return false;
        }
    } finally {
        await safeUnlink(temp);
    }
}

async function writeImmutableJson(destination, value) {
    const bytes = Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8');
    const expected = sha256Bytes(bytes);
    await fsp.mkdir(path.dirname(destination), { recursive: true });
    try {
        const existing = await fsp.readFile(destination);
        if (sha256Bytes(existing) === expected) return false;
        throw new RlTelemetryUploadError(500, 'archive-conflict', `Immutable archive conflict at ${destination}.`);
    } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
    }
    const temp = `${destination}.${process.pid}.${crypto.randomUUID()}.tmp`;
    try {
        await fsp.writeFile(temp, bytes, { flag: 'wx' });
        try {
            await fsp.link(temp, destination);
            return true;
        } catch (error) {
            if (error?.code !== 'EEXIST') throw error;
            const winner = await fsp.readFile(destination);
            if (sha256Bytes(winner) !== expected) {
                throw new RlTelemetryUploadError(500, 'archive-conflict', `Immutable archive conflict at ${destination}.`);
            }
            return false;
        }
    } finally {
        await safeUnlink(temp);
    }
}

function validateCompletedPayload(payload, session) {
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
        throw new RlTelemetryUploadError(422, 'invalid-payload', 'Telemetry payload must be a JSON object.');
    }
    const exact = [
        ['schema_version', RL_TELEMETRY_POLICY.schemaVersion],
        ['match_id', session.matchId],
        ['game_build_version', session.gameBuildVersion],
        ['model_id', session.modelId],
        ['model_sha256', session.modelSha256],
        ['deployment_id', session.deploymentId],
        ['policy_signature', RL_TELEMETRY_POLICY.policySignature],
        ['behavior_name', RL_TELEMETRY_POLICY.behaviorName],
        ['policy_abi_version', RL_TELEMETRY_POLICY.policyAbiVersion],
        ['observation_schema_version', RL_TELEMETRY_POLICY.observationSchemaVersion],
        ['action_schema_version', RL_TELEMETRY_POLICY.actionSchemaVersion],
        ['reward_schema_version', RL_TELEMETRY_POLICY.rewardSchemaVersion],
        ['scenario_schema_version', RL_TELEMETRY_POLICY.scenarioSchemaVersion],
    ];
    for (const [key, expected] of exact) {
        if (payload[key] !== expected) {
            throw new RlTelemetryUploadError(
                409,
                'payload-identity-mismatch',
                `Telemetry ${key} does not match the authenticated upload declaration.`,
            );
        }
    }
}

class RlTelemetryUploadManager {
    constructor(root, options = {}) {
        if (typeof root !== 'string' || !root.trim()) {
            throw new TypeError('RL telemetry upload root must be a non-empty path.');
        }
        this.root = path.resolve(root);
        this.partialDir = path.join(this.root, 'partial');
        this.incomingDir = path.join(this.root, 'incoming');
        this.maxPayloadBytes = options.maxPayloadBytes || DEFAULT_MAX_PAYLOAD_BYTES;
        this.maxChunkBytes = options.maxChunkBytes || DEFAULT_MAX_CHUNK_BYTES;
        this.maxActiveUploadsPerUser = options.maxActiveUploadsPerUser || DEFAULT_MAX_ACTIVE_UPLOADS_PER_USER;
        this.maxActiveUploadsGlobal = options.maxActiveUploadsGlobal || DEFAULT_MAX_ACTIVE_UPLOADS_GLOBAL;
        this.userBytesPerWindow = options.userBytesPerWindow || DEFAULT_USER_BYTES_PER_WINDOW;
        this.rateWindowMs = options.rateWindowMs || DEFAULT_RATE_WINDOW_MS;
        this.uploadIdleTimeoutMs = options.uploadIdleTimeoutMs || DEFAULT_UPLOAD_IDLE_TIMEOUT_MS;
        this.now = options.now || (() => Date.now());
        this.randomUUID = options.randomUUID || (() => crypto.randomUUID());
        this.sessions = new Map();
        this.logicalUploads = new Map();
        this.userQuotas = new Map();
        this.initialization = null;
        this.beginTail = Promise.resolve();
    }

    async handle(params, context) {
        if (!params || typeof params !== 'object') {
            throw new RlTelemetryUploadError(400, 'invalid-request', 'Telemetry upload request must be an object.');
        }
        const userId = requireString(context?.userId, 'authenticated user ID');
        const connectionId = requireString(String(context?.connectionId ?? ''), 'connection ID');
        await this._ensureInitialized();
        await this.cleanupExpired();
        switch (params.Type) {
            case 'rl-telemetry-begin':
                return this._serializeBegin(() => this._beginUnlocked(params, userId, connectionId));
            case 'rl-telemetry-chunk':
                return this._withSession(params, userId, connectionId, session => this._chunkUnlocked(params, session));
            case 'rl-telemetry-complete':
                return this._withSession(params, userId, connectionId, session => this._completeUnlocked(session));
            default:
                throw new RlTelemetryUploadError(400, 'invalid-request', `Unsupported RL telemetry request type ${params.Type}.`);
        }
    }

    async _ensureInitialized() {
        if (!this.initialization) {
            this.initialization = (async () => {
                await fsp.mkdir(this.partialDir, { recursive: true });
                await fsp.mkdir(this.incomingDir, { recursive: true });
                const entries = await fsp.readdir(this.partialDir, { withFileTypes: true });
                await Promise.all(entries
                    .filter(entry => entry.isFile() && entry.name.endsWith('.json.partial'))
                    .map(entry => safeUnlink(path.join(this.partialDir, entry.name))));
            })();
        }
        return this.initialization;
    }

    _serializeBegin(operation) {
        const result = this.beginTail.catch(() => undefined).then(operation);
        this.beginTail = result.then(() => undefined, () => undefined);
        return result;
    }

    _logicalKey(userId, matchId) {
        return `${userId}\u0000${matchId}`;
    }

    _batchId(userId, matchId) {
        return `rl-telemetry-${sha256Bytes(Buffer.from(`${userId}\n${matchId}\n`, 'utf8')).slice(0, 32)}`;
    }

    _paths(batchId) {
        return {
            payload: path.join(this.incomingDir, `${batchId}.json`),
            metadata: path.join(this.incomingDir, `${batchId}.metadata.json`),
        };
    }

    _reserveQuota(userId, bytes, now) {
        let quota = this.userQuotas.get(userId);
        if (!quota || now - quota.windowStart >= this.rateWindowMs) {
            quota = { windowStart: now, bytes: 0 };
        }
        if (quota.bytes + bytes > this.userBytesPerWindow) {
            throw new RlTelemetryUploadError(429, 'upload-rate-limit', 'Telemetry upload byte quota exceeded.');
        }
        quota.bytes += bytes;
        this.userQuotas.set(userId, quota);
    }

    _activeForUser(userId) {
        let count = 0;
        for (const session of this.sessions.values()) if (session.userId === userId) count++;
        return count;
    }

    async _beginUnlocked(params, userId, connectionId) {
        const matchId = requireString(params.MatchId, 'MatchId');
        const gameBuildVersion = requireString(params.GameBuildVersion, 'GameBuildVersion');
        const totalBytes = requireInteger(params.TotalBytes, 'TotalBytes', 1, this.maxPayloadBytes);
        const payloadSha256 = requireSha256(params.PayloadSha256, 'PayloadSha256');
        const modelId = requireString(params.ModelId, 'ModelId');
        const modelSha256 = requireSha256(params.ModelSha256, 'ModelSha256');
        const deploymentId = requireDeploymentId(params.DeploymentId);
        if (params.PolicyAbiVersion !== RL_TELEMETRY_POLICY.policyAbiVersion ||
            params.PolicySignature !== RL_TELEMETRY_POLICY.policySignature) {
            throw new RlTelemetryUploadError(409, 'incompatible-policy', 'Telemetry upload policy identity is incompatible.');
        }

        const logicalKey = this._logicalKey(userId, matchId);
        const activeId = this.logicalUploads.get(logicalKey);
        if (activeId) {
            const active = this.sessions.get(activeId);
            if (!active) {
                this.logicalUploads.delete(logicalKey);
            } else if (active.connectionId !== connectionId) {
                throw new RlTelemetryUploadError(
                    409,
                    'match-id-conflict',
                    'Active MatchId is already owned by another authenticated connection.',
                );
            } else if (
                active.payloadSha256 === payloadSha256 &&
                active.totalBytes === totalBytes &&
                active.gameBuildVersion === gameBuildVersion &&
                active.modelId === modelId &&
                active.modelSha256 === modelSha256 &&
                active.deploymentId === deploymentId
            ) {
                return this._progress(active, false);
            } else {
                throw new RlTelemetryUploadError(409, 'match-id-conflict', 'Active MatchId is already bound to different telemetry content.');
            }
        }

        const batchId = this._batchId(userId, matchId);
        const paths = this._paths(batchId);
        const completed = await readJsonIfPresent(paths.metadata);
        if (completed) {
            const expected = {
                batchId,
                matchId,
                uploaderUserId: userId,
                gameBuildVersion,
                payloadSha256,
                payloadBytes: totalBytes,
                modelId,
                modelSha256,
                deploymentId,
                policyAbiVersion: RL_TELEMETRY_POLICY.policyAbiVersion,
                policySignature: RL_TELEMETRY_POLICY.policySignature,
                trust: 'authenticated-quarantine',
                readyForIngestion: false,
            };
            for (const [key, value] of Object.entries(expected)) {
                if (completed[key] !== value) {
                    throw new RlTelemetryUploadError(
                        409,
                        'archive-conflict',
                        `Completed telemetry ${key} conflicts with this upload.`,
                    );
                }
            }
            await verifyImmutablePayload(paths.payload, payloadSha256, totalBytes);
            return {
                Completed: true,
                Duplicate: true,
                BatchId: batchId,
                PayloadSha256: payloadSha256,
                TotalBytes: totalBytes,
            };
        }

        if (this.sessions.size >= this.maxActiveUploadsGlobal || this._activeForUser(userId) >= this.maxActiveUploadsPerUser) {
            throw new RlTelemetryUploadError(429, 'too-many-active-uploads', 'Too many active telemetry uploads.');
        }
        const now = this.now();
        this._reserveQuota(userId, totalBytes, now);
        const uploadId = `rl-telemetry-upload-${this.randomUUID()}`;
        const partialPath = path.join(this.partialDir, `${uploadId}.json.partial`);
        await fsp.writeFile(partialPath, Buffer.alloc(0), { flag: 'wx' });
        const session = {
            uploadId,
            batchId,
            logicalKey,
            userId,
            connectionId,
            matchId,
            gameBuildVersion,
            totalBytes,
            payloadSha256,
            modelId,
            modelSha256,
            deploymentId,
            partialPath,
            nextOffset: 0,
            updatedAt: now,
            tail: Promise.resolve(),
        };
        this.sessions.set(uploadId, session);
        this.logicalUploads.set(logicalKey, uploadId);
        return this._progress(session, false);
    }

    _progress(session, duplicate) {
        return {
            Completed: false,
            Duplicate: Boolean(duplicate),
            UploadId: session.uploadId,
            BatchId: session.batchId,
            NextOffset: session.nextOffset,
            ChunkBytes: this.maxChunkBytes,
            TotalBytes: session.totalBytes,
        };
    }

    async _withSession(params, userId, connectionId, operation) {
        const uploadId = requireString(params.UploadId, 'UploadId');
        const session = this.sessions.get(uploadId);
        if (!session) throw new RlTelemetryUploadError(404, 'unknown-upload', 'Telemetry upload session was not found.');
        if (session.userId !== userId || session.connectionId !== connectionId) {
            throw new RlTelemetryUploadError(403, 'upload-owner-mismatch', 'Telemetry upload belongs to another authenticated connection.');
        }
        const result = session.tail.catch(() => undefined).then(() => operation(session));
        session.tail = result.then(() => undefined, () => undefined);
        return result;
    }

    async _chunkUnlocked(params, session) {
        const offset = requireInteger(params.Offset, 'Offset', 0, session.totalBytes);
        const bytes = decodeCanonicalBase64(params.Data, this.maxChunkBytes);
        if (offset + bytes.length > session.totalBytes) {
            throw new RlTelemetryUploadError(413, 'payload-too-large', 'Chunk exceeds declared telemetry length.');
        }
        if (offset < session.nextOffset) {
            if (offset + bytes.length > session.nextOffset) {
                throw new RlTelemetryUploadError(409, 'chunk-offset-conflict', 'Retry chunk overlaps unwritten telemetry bytes.');
            }
            const handle = await fsp.open(session.partialPath, 'r');
            try {
                const existing = Buffer.alloc(bytes.length);
                const { bytesRead } = await handle.read(existing, 0, bytes.length, offset);
                if (bytesRead !== bytes.length || !crypto.timingSafeEqual(existing, bytes)) {
                    throw new RlTelemetryUploadError(409, 'chunk-content-conflict', 'Retry chunk differs from stored telemetry bytes.');
                }
            } finally {
                await handle.close();
            }
            session.updatedAt = this.now();
            return this._progress(session, true);
        }
        if (offset !== session.nextOffset) {
            throw new RlTelemetryUploadError(409, 'unexpected-offset', `Expected telemetry offset ${session.nextOffset}.`);
        }
        const handle = await fsp.open(session.partialPath, 'r+');
        try {
            const { bytesWritten } = await handle.write(bytes, 0, bytes.length, offset);
            if (bytesWritten !== bytes.length) throw new Error('Short telemetry file write.');
            await handle.sync();
        } finally {
            await handle.close();
        }
        session.nextOffset += bytes.length;
        session.updatedAt = this.now();
        return this._progress(session, false);
    }

    async _completeUnlocked(session) {
        if (session.nextOffset !== session.totalBytes) {
            throw new RlTelemetryUploadError(409, 'upload-incomplete', `Telemetry upload is incomplete at ${session.nextOffset}/${session.totalBytes} bytes.`);
        }
        try {
            const bytes = await fsp.readFile(session.partialPath);
            if (bytes.length !== session.totalBytes || sha256Bytes(bytes) !== session.payloadSha256) {
                throw new RlTelemetryUploadError(422, 'payload-hash-mismatch', 'Telemetry payload SHA-256 does not match the declared digest.');
            }
            let payload;
            try {
                payload = JSON.parse(bytes.toString('utf8'));
            } catch (error) {
                throw new RlTelemetryUploadError(422, 'invalid-payload', `Telemetry payload is invalid JSON: ${error.message}`);
            }
            validateCompletedPayload(payload, session);

            const paths = this._paths(session.batchId);
            await writeImmutableFile(session.partialPath, paths.payload, session.payloadSha256);
            const metadata = {
                schemaVersion: 1,
                batchId: session.batchId,
                matchId: session.matchId,
                uploaderUserId: session.userId,
                gameBuildVersion: session.gameBuildVersion,
                payloadSha256: session.payloadSha256,
                payloadBytes: session.totalBytes,
                modelId: session.modelId,
                modelSha256: session.modelSha256,
                deploymentId: session.deploymentId,
                policyAbiVersion: RL_TELEMETRY_POLICY.policyAbiVersion,
                policySignature: RL_TELEMETRY_POLICY.policySignature,
                trust: 'authenticated-quarantine',
                readyForIngestion: false,
            };
            await writeImmutableJson(paths.metadata, metadata);
            return {
                Completed: true,
                Duplicate: false,
                BatchId: session.batchId,
                PayloadSha256: session.payloadSha256,
                TotalBytes: session.totalBytes,
            };
        } finally {
            this.sessions.delete(session.uploadId);
            if (this.logicalUploads.get(session.logicalKey) === session.uploadId) {
                this.logicalUploads.delete(session.logicalKey);
            }
            await safeUnlink(session.partialPath);
        }
    }

    async cleanupExpired() {
        const cutoff = this.now() - this.uploadIdleTimeoutMs;
        const expired = [...this.sessions.values()].filter(session => session.updatedAt < cutoff);
        await Promise.all(expired.map(async session => {
            this.sessions.delete(session.uploadId);
            if (this.logicalUploads.get(session.logicalKey) === session.uploadId) this.logicalUploads.delete(session.logicalKey);
            await safeUnlink(session.partialPath);
        }));
        return expired.length;
    }
}

module.exports = {
    RL_TELEMETRY_REQUEST_TYPES,
    RL_TELEMETRY_POLICY,
    RlTelemetryUploadError,
    RlTelemetryUploadManager,
};
