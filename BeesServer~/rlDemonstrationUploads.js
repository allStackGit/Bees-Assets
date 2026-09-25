'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const fsp = fs.promises;

const RL_DEMO_REQUEST_TYPES = new Set(['rl-demo-begin', 'rl-demo-chunk', 'rl-demo-complete']);
const RL_DEMO_POLICY = Object.freeze({
    schemaVersion: 1,
    behaviorName: 'BeesRL1v1',
    policyAbiVersion: 19,
    policySignature: 'bees-rl-v19|behavior=BeesRL1v1|network=ff-128x3|normalize=true|obs=7614|tail=episode-progress+20-reserved|cont=16|disc=2x5,5|coord-frame=team-episode-distinct-quarter-turn|weapon-aim=slotwise-xy|weapon-fire=slotwise-cease-or-fire|weapon-ready=rl-latched-until-fire|shiptype=fixed-scrambled-scalar24|weapontype=fixed-scrambled-scalar10|mapbits=4|shipmap=v1-0..23|weaponmap=v1-0..9|allies=64|enemies=64|weapons=5|entity-weapons=5|enemy-mounts=0|mining=8|map-objects=64|moving-asteroids=48|self=25|ship-id=episode-permuted-scalar23|capability=12|parent-carrier=40|entity-core=14|entity=40|ally=44-with-private-comm4|communication=4-continuous-private-allied|self-weapon=15|observed-weapon=5|weapon-observation=split-self-vs-observed|mining-slot=7|map-slot=12|moving-asteroid-slot=11|objective=16|grid=21x21-cell6|exploration-grid=16x16-team-shared-sight-recency|entity-order=distance,type,fleet-id,runtime-id',
    observationSize: 7614,
    continuousActionCount: 16,
    discreteBranchSizes: Object.freeze([
        2, 2, 2, 2, 2,
        5,
    ]),
});

const DEFAULT_MAX_DEMO_BYTES = 16 * 1024 * 1024;
const DEFAULT_MAX_CHUNK_BYTES = 512 * 1024;
const DEFAULT_MAX_MANIFEST_BYTES = 128 * 1024;
const DEFAULT_MAX_ACTIVE_UPLOADS_PER_USER = 2;
const DEFAULT_MAX_ACTIVE_UPLOADS_GLOBAL = 64;
const DEFAULT_USER_BYTES_PER_WINDOW = 64 * 1024 * 1024;
const DEFAULT_RATE_WINDOW_MS = 60 * 60 * 1000;
const DEFAULT_UPLOAD_IDLE_TIMEOUT_MS = 5 * 60 * 1000;
const COMPLETED_RESULT_TTL_MS = 10 * 60 * 1000;
const ARCHIVE_SCHEMA_VERSION = 1;

class RlDemonstrationUploadError extends Error {
    constructor(statusCode, code, message) {
        super(message);
        this.name = 'RlDemonstrationUploadError';
        this.statusCode = statusCode;
        this.code = code;
    }
}

function sha256Bytes(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function sha256File(filePath) {
    return new Promise((resolve, reject) => {
        const digest = crypto.createHash('sha256');
        const stream = fs.createReadStream(filePath);
        stream.on('data', chunk => digest.update(chunk));
        stream.on('error', reject);
        stream.on('end', () => resolve(digest.digest('hex')));
    });
}

function requireString(value, label, maxLength = 256) {
    if (typeof value !== 'string' || !value.trim() || value.length > maxLength) {
        throw new RlDemonstrationUploadError(
            400,
            'invalid-field',
            `${label} must be a non-empty string up to ${maxLength} characters.`,
        );
    }
    return value.trim();
}

function requireSha256(value, label) {
    const normalized = requireString(value, label, 64).toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(normalized)) {
        throw new RlDemonstrationUploadError(
            400,
            'invalid-hash',
            `${label} must be a 64-character hexadecimal SHA-256.`,
        );
    }
    return normalized;
}

function requireInteger(value, label, minimum, maximum) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
        throw new RlDemonstrationUploadError(
            400,
            'invalid-field',
            `${label} must be an integer in [${minimum}, ${maximum}].`,
        );
    }
    return value;
}

function buffersEqual(left, right) {
    return left.length === right.length && crypto.timingSafeEqual(left, right);
}

async function safeUnlink(filePath) {
    try {
        await fsp.unlink(filePath);
    } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
    }
}

async function readExistingBytes(filePath) {
    try {
        return await fsp.readFile(filePath);
    } catch (error) {
        if (error?.code === 'ENOENT') return null;
        throw error;
    }
}

async function writeImmutableBytes(filePath, bytes) {
    await fsp.mkdir(path.dirname(filePath), { recursive: true });
    const payload = Buffer.from(bytes);
    const existing = await readExistingBytes(filePath);
    if (existing !== null) {
        if (!buffersEqual(existing, payload)) {
            throw new RlDemonstrationUploadError(
                500,
                'archive-conflict',
                `Immutable archive conflict at ${filePath}.`,
            );
        }
        return false;
    }

    const temp = path.join(
        path.dirname(filePath),
        `.${path.basename(filePath)}.${process.pid}.${crypto.randomUUID()}.tmp`,
    );
    try {
        const handle = await fsp.open(temp, 'wx');
        try {
            await handle.writeFile(payload);
            await handle.sync();
        } finally {
            await handle.close();
        }
        try {
            await fsp.link(temp, filePath);
            return true;
        } catch (error) {
            if (error?.code !== 'EEXIST') throw error;
            const winner = await fsp.readFile(filePath);
            if (!buffersEqual(winner, payload)) {
                throw new RlDemonstrationUploadError(
                    500,
                    'archive-conflict',
                    `Immutable archive conflict at ${filePath}.`,
                );
            }
            return false;
        }
    } finally {
        await safeUnlink(temp);
    }
}

async function copyImmutableFile(source, destination, expectedSha256) {
    await fsp.mkdir(path.dirname(destination), { recursive: true });
    try {
        if (await sha256File(destination) === expectedSha256) return false;
        throw new RlDemonstrationUploadError(
            500,
            'archive-conflict',
            `Immutable archive hash conflict at ${destination}.`,
        );
    } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
    }

    const temp = path.join(
        path.dirname(destination),
        `.${path.basename(destination)}.${process.pid}.${crypto.randomUUID()}.tmp`,
    );
    try {
        await fsp.copyFile(source, temp, fs.constants.COPYFILE_EXCL);
        if (await sha256File(temp) !== expectedSha256) {
            throw new RlDemonstrationUploadError(
                422,
                'demo-hash-mismatch',
                'Demonstration changed while being quarantined.',
            );
        }
        try {
            await fsp.link(temp, destination);
            return true;
        } catch (error) {
            if (error?.code !== 'EEXIST') throw error;
            if (await sha256File(destination) !== expectedSha256) {
                throw new RlDemonstrationUploadError(
                    500,
                    'archive-conflict',
                    `Immutable archive hash conflict at ${destination}.`,
                );
            }
            return false;
        }
    } finally {
        await safeUnlink(temp);
    }
}

function validatePolicyManifest(manifest) {
    if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) {
        throw new RlDemonstrationUploadError(
            400,
            'invalid-manifest',
            'Capture manifest must be a JSON object.',
        );
    }
    const scalarChecks = [
        ['schemaVersion', RL_DEMO_POLICY.schemaVersion],
        ['behaviorName', RL_DEMO_POLICY.behaviorName],
        ['policyAbiVersion', RL_DEMO_POLICY.policyAbiVersion],
        ['policySignature', RL_DEMO_POLICY.policySignature],
        ['observationSize', RL_DEMO_POLICY.observationSize],
        ['continuousActionCount', RL_DEMO_POLICY.continuousActionCount],
    ];
    for (const [key, expected] of scalarChecks) {
        if (manifest[key] !== expected) {
            throw new RlDemonstrationUploadError(
                409,
                'incompatible-policy',
                `Capture manifest ${key}=${JSON.stringify(manifest[key])} does not match the accepted policy contract.`,
            );
        }
    }
    if (!Array.isArray(manifest.discreteBranchSizes) ||
        manifest.discreteBranchSizes.length !== RL_DEMO_POLICY.discreteBranchSizes.length ||
        manifest.discreteBranchSizes.some(
            (value, index) => value !== RL_DEMO_POLICY.discreteBranchSizes[index],
        )) {
        throw new RlDemonstrationUploadError(
            409,
            'incompatible-policy',
            'Capture manifest discreteBranchSizes do not match the accepted policy contract.',
        );
    }
}

function decodeCanonicalBase64(value, maxBytes) {
    if (typeof value !== 'string' || value.length === 0 ||
        value.length > Math.ceil(maxBytes / 3) * 4 + 4 ||
        value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) {
        throw new RlDemonstrationUploadError(
            400,
            'invalid-chunk',
            'Chunk Data must be canonical base64.',
        );
    }
    const decoded = Buffer.from(value, 'base64');
    if (decoded.length === 0 || decoded.length > maxBytes || decoded.toString('base64') !== value) {
        throw new RlDemonstrationUploadError(
            413,
            'chunk-too-large',
            'Chunk exceeds the configured raw-byte limit or is not canonical base64.',
        );
    }
    return decoded;
}

class RlDemonstrationUploadManager {
    constructor(root, options = {}) {
        if (typeof root !== 'string' || !root.trim()) {
            throw new TypeError('RL demonstration upload root must be a non-empty path.');
        }
        this.root = path.resolve(root);
        this.partialDir = path.join(this.root, 'partial');
        this.incomingDir = path.join(this.root, 'incoming');
        this.maxDemoBytes = options.maxDemoBytes || DEFAULT_MAX_DEMO_BYTES;
        this.maxBundleBytes = options.maxBundleBytes || this.maxDemoBytes;
        this.maxChunkBytes = options.maxChunkBytes || DEFAULT_MAX_CHUNK_BYTES;
        this.maxManifestBytes = options.maxManifestBytes || DEFAULT_MAX_MANIFEST_BYTES;
        this.maxActiveUploadsPerUser = options.maxActiveUploadsPerUser || DEFAULT_MAX_ACTIVE_UPLOADS_PER_USER;
        this.maxActiveUploadsGlobal = options.maxActiveUploadsGlobal || DEFAULT_MAX_ACTIVE_UPLOADS_GLOBAL;
        this.userBytesPerWindow = options.userBytesPerWindow || DEFAULT_USER_BYTES_PER_WINDOW;
        this.rateWindowMs = options.rateWindowMs || DEFAULT_RATE_WINDOW_MS;
        this.uploadIdleTimeoutMs = options.uploadIdleTimeoutMs || DEFAULT_UPLOAD_IDLE_TIMEOUT_MS;
        this.now = options.now || (() => Date.now());
        this.randomUUID = options.randomUUID || (() => crypto.randomUUID());
        this.sessions = new Map();
        this.activeLogicalUploads = new Map();
        this.activeBatchUploads = new Map();
        this.completedResults = new Map();
        this.userQuotas = new Map();
        this.initialization = null;
        this.beginTail = Promise.resolve();
    }

    async handle(params, context) {
        if (!params || typeof params !== 'object') {
            throw new RlDemonstrationUploadError(
                400,
                'invalid-request',
                'Upload request must be an object.',
            );
        }
        const userId = requireString(context?.userId, 'authenticated user ID');
        const connectionId = requireString(String(context?.connectionId ?? ''), 'connection ID');
        await this._ensureInitialized();
        await this.cleanupExpired();
        switch (params.Type) {
            case 'rl-demo-begin':
                return this._serializeBegin(() => this._beginUnlocked(params, userId, connectionId));
            case 'rl-demo-chunk':
                return this.chunk(params, userId, connectionId);
            case 'rl-demo-complete':
                return this.complete(params, userId, connectionId);
            default:
                throw new RlDemonstrationUploadError(
                    400,
                    'invalid-request',
                    `Unsupported RL demonstration request type ${params.Type}.`,
                );
        }
    }

    async _ensureInitialized() {
        if (!this.initialization) {
            this.initialization = (async () => {
                await fsp.mkdir(this.partialDir, { recursive: true });
                await fsp.mkdir(this.incomingDir, { recursive: true });
                const entries = await fsp.readdir(this.partialDir, { withFileTypes: true });
                await Promise.all(entries
                    .filter(entry => entry.isFile() && entry.name.endsWith('.demo.partial'))
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

    _logicalKey(userId, connectionId, demonstrationId) {
        return `${userId}\u0000${connectionId}\u0000${demonstrationId}`;
    }

    _batchId(userId, gameBuildVersion, demoSha256, manifestSha256) {
        const identity = `${userId}\n${gameBuildVersion}\n${demoSha256}\n${manifestSha256}\n`;
        return `rl-demo-${sha256Bytes(Buffer.from(identity, 'utf8')).slice(0, 32)}`;
    }

    _pathsForBatch(batchId) {
        return {
            demo: path.join(this.incomingDir, `${batchId}.demo`),
            manifest: path.join(this.incomingDir, `${batchId}.capture-manifest.json`),
            metadata: path.join(this.incomingDir, `${batchId}.json`),
        };
    }

    _validateManifestJson(manifestJson, manifestSha256) {
        if (typeof manifestJson !== 'string' ||
            Buffer.byteLength(manifestJson, 'utf8') > this.maxManifestBytes) {
            throw new RlDemonstrationUploadError(
                413,
                'manifest-too-large',
                'Capture manifest exceeds the configured size limit.',
            );
        }
        const manifestBytes = Buffer.from(manifestJson, 'utf8');
        if (sha256Bytes(manifestBytes) !== manifestSha256) {
            throw new RlDemonstrationUploadError(
                400,
                'manifest-hash-mismatch',
                'Capture manifest SHA-256 does not match ManifestSha256.',
            );
        }
        let manifest;
        try {
            manifest = JSON.parse(manifestJson);
        } catch (error) {
            throw new RlDemonstrationUploadError(
                400,
                'invalid-manifest',
                `Capture manifest is invalid JSON: ${error.message}`,
            );
        }
        validatePolicyManifest(manifest);
        return { manifest, manifestBytes };
    }

    _activeCountForUser(userId) {
        let count = 0;
        for (const session of this.sessions.values()) {
            if (session.userId === userId) count++;
        }
        return count;
    }

    _reserveQuota(userId, bytes, now) {
        let quota = this.userQuotas.get(userId);
        if (!quota || now - quota.windowStart >= this.rateWindowMs) {
            quota = { windowStart: now, bytes: 0 };
        }
        if (quota.bytes + bytes > this.userBytesPerWindow) {
            throw new RlDemonstrationUploadError(
                429,
                'upload-rate-limit',
                'Authenticated user exceeded the RL demonstration upload byte quota for the current window.',
            );
        }
        quota.bytes += bytes;
        this.userQuotas.set(userId, quota);
    }

    async _completedBatch(batchId, expected) {
        const paths = this._pathsForBatch(batchId);
        let metadata;
        try {
            metadata = JSON.parse(await fsp.readFile(paths.metadata, 'utf8'));
        } catch (error) {
            if (error?.code === 'ENOENT') return null;
            if (error instanceof SyntaxError) {
                throw new RlDemonstrationUploadError(
                    500,
                    'archive-conflict',
                    `Existing RL demonstration metadata is malformed for ${batchId}.`,
                );
            }
            throw error;
        }
        if (metadata.batchId !== batchId || metadata.demoSha256 !== expected.demoSha256 ||
            metadata.manifestSha256 !== expected.manifestSha256 ||
            metadata.uploaderUserId !== expected.userId ||
            metadata.gameBuildVersion !== expected.gameBuildVersion ||
            metadata.demoBytes !== expected.totalBytes) {
            throw new RlDemonstrationUploadError(
                500,
                'archive-conflict',
                `Existing RL demonstration metadata conflicts for ${batchId}.`,
            );
        }
        try {
            if (await sha256File(paths.demo) !== expected.demoSha256 ||
                await sha256File(paths.manifest) !== expected.manifestSha256) {
                throw new RlDemonstrationUploadError(
                    500,
                    'archive-conflict',
                    `Existing RL demonstration archive failed integrity verification for ${batchId}.`,
                );
            }
        } catch (error) {
            if (error?.code === 'ENOENT') {
                throw new RlDemonstrationUploadError(
                    500,
                    'archive-conflict',
                    `Completed RL demonstration metadata references missing artifacts for ${batchId}.`,
                );
            }
            throw error;
        }
        return {
            UploadId: null,
            BatchId: batchId,
            Completed: true,
            Duplicate: true,
            NextOffset: expected.totalBytes,
            ChunkBytes: this.maxChunkBytes,
        };
    }

    async _beginUnlocked(params, userId, connectionId) {
        if (params.Source !== 'Human') {
            throw new RlDemonstrationUploadError(
                403,
                'unsupported-source',
                'Only authenticated Human demonstrations may be uploaded through the public client path.',
            );
        }
        const demonstrationId = requireString(params.DemonstrationId, 'DemonstrationId', 128);
        if (!/^[A-Za-z0-9._:-]+$/.test(demonstrationId)) {
            throw new RlDemonstrationUploadError(
                400,
                'invalid-field',
                'DemonstrationId contains unsupported characters.',
            );
        }
        const gameBuildVersion = requireString(params.GameBuildVersion, 'GameBuildVersion', 128);
        const totalBytes = requireInteger(params.TotalBytes, 'TotalBytes', 1, this.maxDemoBytes);
        const demoSha256 = requireSha256(params.DemoSha256, 'DemoSha256');
        const manifestSha256 = requireSha256(params.ManifestSha256, 'ManifestSha256');
        const { manifest, manifestBytes } = this._validateManifestJson(
            params.ManifestJson,
            manifestSha256,
        );
        const bundleBytes = totalBytes + manifestBytes.length;
        if (bundleBytes > this.maxBundleBytes) {
            throw new RlDemonstrationUploadError(
                413,
                'upload-too-large',
                'Native demonstration plus capture manifest exceeds the configured bundle limit.',
            );
        }

        const logicalKey = this._logicalKey(userId, connectionId, demonstrationId);
        const fingerprint = sha256Bytes(Buffer.from(
            `${gameBuildVersion}\n${totalBytes}\n${demoSha256}\n${manifestSha256}`,
            'utf8',
        ));
        const existingUploadId = this.activeLogicalUploads.get(logicalKey);
        if (existingUploadId) {
            const existing = this.sessions.get(existingUploadId);
            if (existing && existing.fingerprint === fingerprint) {
                existing.lastActivityAt = this.now();
                return this._activeResult(existing);
            }
            throw new RlDemonstrationUploadError(
                409,
                'demonstration-id-conflict',
                'An active upload already uses this DemonstrationId with different metadata.',
            );
        }

        const batchId = this._batchId(userId, gameBuildVersion, demoSha256, manifestSha256);
        const completed = await this._completedBatch(batchId, {
            userId,
            gameBuildVersion,
            totalBytes,
            demoSha256,
            manifestSha256,
        });
        if (completed) return completed;

        const activeBatchUploadId = this.activeBatchUploads.get(batchId);
        if (activeBatchUploadId) {
            const active = this.sessions.get(activeBatchUploadId);
            if (!active) {
                this.activeBatchUploads.delete(batchId);
            } else if (active.userId === userId && active.connectionId === connectionId) {
                active.lastActivityAt = this.now();
                return this._activeResult(active);
            } else {
                throw new RlDemonstrationUploadError(
                    409,
                    'duplicate-upload-active',
                    'The same demonstration content is already being uploaded on another authenticated connection.',
                );
            }
        }

        if (this.sessions.size >= this.maxActiveUploadsGlobal) {
            throw new RlDemonstrationUploadError(
                503,
                'upload-capacity',
                'Server RL demonstration upload capacity is temporarily full.',
            );
        }
        if (this._activeCountForUser(userId) >= this.maxActiveUploadsPerUser) {
            throw new RlDemonstrationUploadError(
                429,
                'too-many-active-uploads',
                'Authenticated user has too many active RL demonstration uploads.',
            );
        }

        const now = this.now();
        this._reserveQuota(userId, bundleBytes, now);
        const uploadId = this.randomUUID();
        const partialPath = path.join(this.partialDir, `${uploadId}.demo.partial`);
        const handle = await fsp.open(partialPath, 'wx');
        await handle.close();
        const session = {
            uploadId,
            batchId,
            logicalKey,
            fingerprint,
            userId,
            connectionId,
            demonstrationId,
            gameBuildVersion,
            totalBytes,
            demoSha256,
            manifestSha256,
            manifest,
            manifestBytes,
            partialPath,
            bytesWritten: 0,
            createdAt: now,
            receivedAt: new Date(now).toISOString(),
            lastActivityAt: now,
            operationTail: Promise.resolve(),
        };
        this.sessions.set(uploadId, session);
        this.activeLogicalUploads.set(logicalKey, uploadId);
        this.activeBatchUploads.set(batchId, uploadId);
        return this._activeResult(session);
    }

    _activeResult(session) {
        return {
            UploadId: session.uploadId,
            Completed: false,
            Duplicate: false,
            NextOffset: session.bytesWritten,
            ChunkBytes: this.maxChunkBytes,
        };
    }

    _ownedSession(params, userId, connectionId) {
        const uploadId = requireString(params.UploadId, 'UploadId', 128);
        const session = this.sessions.get(uploadId);
        if (!session) {
            const completed = this.completedResults.get(uploadId);
            if (completed && completed.userId === userId &&
                completed.connectionId === connectionId) {
                return { completed: completed.result };
            }
            throw new RlDemonstrationUploadError(
                404,
                'upload-not-found',
                'RL demonstration upload session was not found or expired.',
            );
        }
        if (session.userId !== userId || session.connectionId !== connectionId) {
            throw new RlDemonstrationUploadError(
                403,
                'upload-owner-mismatch',
                'RL demonstration upload session belongs to a different authenticated connection.',
            );
        }
        session.lastActivityAt = this.now();
        return { session };
    }

    _serializeSession(session, operation) {
        const result = session.operationTail.catch(() => undefined).then(async () => {
            if (this.sessions.get(session.uploadId) !== session) {
                const completed = this.completedResults.get(session.uploadId);
                if (completed) return completed.result;
                throw new RlDemonstrationUploadError(
                    404,
                    'upload-not-found',
                    'RL demonstration upload session is no longer active.',
                );
            }
            return operation();
        });
        session.operationTail = result.then(() => undefined, () => undefined);
        return result;
    }

    async chunk(params, userId, connectionId) {
        const owned = this._ownedSession(params, userId, connectionId);
        if (owned.completed) return owned.completed;
        return this._serializeSession(
            owned.session,
            () => this._chunkUnlocked(params, owned.session),
        );
    }

    async _chunkUnlocked(params, session) {
        const offset = requireInteger(params.Offset, 'Offset', 0, session.totalBytes);
        const chunk = decodeCanonicalBase64(params.Data, this.maxChunkBytes);
        if (offset + chunk.length > session.totalBytes) {
            throw new RlDemonstrationUploadError(
                413,
                'upload-too-large',
                'Chunk would exceed the declared RL demonstration length.',
            );
        }
        if (offset > session.bytesWritten) {
            throw new RlDemonstrationUploadError(
                409,
                'unexpected-offset',
                `Chunk offset ${offset} is ahead of server offset ${session.bytesWritten}.`,
            );
        }
        if (offset < session.bytesWritten) {
            if (offset + chunk.length > session.bytesWritten) {
                throw new RlDemonstrationUploadError(
                    409,
                    'overlapping-chunk',
                    'Retried chunk overlaps bytes that have not yet been committed.',
                );
            }
            const handle = await fsp.open(session.partialPath, 'r');
            const existing = Buffer.alloc(chunk.length);
            try {
                const { bytesRead } = await handle.read(
                    existing,
                    0,
                    existing.length,
                    offset,
                );
                if (bytesRead !== chunk.length || !buffersEqual(existing, chunk)) {
                    throw new RlDemonstrationUploadError(
                        409,
                        'chunk-conflict',
                        'Retried chunk does not match already stored bytes.',
                    );
                }
            } finally {
                await handle.close();
            }
            return {
                UploadId: session.uploadId,
                NextOffset: session.bytesWritten,
                Duplicate: true,
            };
        }

        const handle = await fsp.open(session.partialPath, 'r+');
        try {
            const { bytesWritten } = await handle.write(
                chunk,
                0,
                chunk.length,
                offset,
            );
            if (bytesWritten !== chunk.length) {
                throw new RlDemonstrationUploadError(
                    500,
                    'short-write',
                    'Server could not persist the complete RL demonstration chunk.',
                );
            }
            await handle.sync();
        } finally {
            await handle.close();
        }
        session.bytesWritten += chunk.length;
        session.lastActivityAt = this.now();
        return {
            UploadId: session.uploadId,
            NextOffset: session.bytesWritten,
            Duplicate: false,
        };
    }

    async complete(params, userId, connectionId) {
        const owned = this._ownedSession(params, userId, connectionId);
        if (owned.completed) return owned.completed;
        return this._serializeSession(
            owned.session,
            () => this._completeUnlocked(owned.session),
        );
    }

    async _completeUnlocked(session) {
        if (session.bytesWritten !== session.totalBytes) {
            throw new RlDemonstrationUploadError(
                409,
                'upload-incomplete',
                `Upload has ${session.bytesWritten} of ${session.totalBytes} bytes.`,
            );
        }
        const actualHash = await sha256File(session.partialPath);
        if (actualHash !== session.demoSha256) {
            await this._discardSession(session);
            throw new RlDemonstrationUploadError(
                422,
                'demo-hash-mismatch',
                'Uploaded RL demonstration SHA-256 does not match the declared hash.',
            );
        }

        const paths = this._pathsForBatch(session.batchId);
        await copyImmutableFile(session.partialPath, paths.demo, session.demoSha256);
        await writeImmutableBytes(paths.manifest, session.manifestBytes);
        const metadata = {
            schemaVersion: ARCHIVE_SCHEMA_VERSION,
            batchId: session.batchId,
            demonstrationId: session.demonstrationId,
            uploaderUserId: session.userId,
            gameBuildVersion: session.gameBuildVersion,
            source: 'Human',
            trust: 'authenticated-quarantine',
            readyForTraining: false,
            receivedAt: session.receivedAt,
            demoBytes: session.totalBytes,
            demoSha256: session.demoSha256,
            manifestSha256: session.manifestSha256,
            manifest: session.manifest,
        };
        await writeImmutableBytes(
            paths.metadata,
            Buffer.from(`${JSON.stringify(metadata, null, 2)}\n`, 'utf8'),
        );

        await safeUnlink(session.partialPath);
        this._removeSession(session);
        const result = {
            UploadId: session.uploadId,
            BatchId: session.batchId,
            Completed: true,
            Duplicate: false,
            NextOffset: session.totalBytes,
        };
        this.completedResults.set(session.uploadId, {
            userId: session.userId,
            connectionId: session.connectionId,
            completedAt: this.now(),
            result,
        });
        return result;
    }

    _removeSession(session) {
        this.sessions.delete(session.uploadId);
        if (this.activeLogicalUploads.get(session.logicalKey) === session.uploadId) {
            this.activeLogicalUploads.delete(session.logicalKey);
        }
        if (this.activeBatchUploads.get(session.batchId) === session.uploadId) {
            this.activeBatchUploads.delete(session.batchId);
        }
    }

    async _discardSession(session) {
        this._removeSession(session);
        await safeUnlink(session.partialPath);
    }

    async cleanupExpired() {
        const now = this.now();
        const expired = [];
        for (const session of this.sessions.values()) {
            if (now - session.lastActivityAt >= this.uploadIdleTimeoutMs) expired.push(session);
        }
        for (const session of expired) await this._discardSession(session);
        for (const [uploadId, completed] of this.completedResults.entries()) {
            if (now - completed.completedAt >= COMPLETED_RESULT_TTL_MS) {
                this.completedResults.delete(uploadId);
            }
        }
        for (const [userId, quota] of this.userQuotas.entries()) {
            if (now - quota.windowStart >= this.rateWindowMs) this.userQuotas.delete(userId);
        }
    }
}

module.exports = {
    RL_DEMO_REQUEST_TYPES,
    RL_DEMO_POLICY,
    DEFAULT_MAX_DEMO_BYTES,
    DEFAULT_MAX_CHUNK_BYTES,
    DEFAULT_USER_BYTES_PER_WINDOW,
    RlDemonstrationUploadError,
    RlDemonstrationUploadManager,
    validatePolicyManifest,
};
