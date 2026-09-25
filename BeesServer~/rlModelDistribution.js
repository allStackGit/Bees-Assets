'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { RL_DEMO_POLICY: RL_POLICY } = require('./rlDemonstrationUploads');

const fsp = fs.promises;

const RL_MODEL_REQUEST_TYPES = new Set(['rl-model-current', 'rl-model-chunk']);
const DISTRIBUTION_POINTER_SCHEMA_VERSION = 1;
const DEFAULT_MAX_CHUNK_BYTES = 512 * 1024;
const DEFAULT_MAX_BUNDLE_BYTES = 512 * 1024 * 1024;
const DEFAULT_USER_BYTES_PER_WINDOW = 1024 * 1024 * 1024;
const DEFAULT_RATE_WINDOW_MS = 60 * 60 * 1000;
const PLATFORM_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;
const DEPLOYMENT_ID_PATTERN = /^deploy-[0-9a-f]{24}$/;
const MODEL_ID_PATTERN = /^bees-rl-v\d+-[0-9a-f]{24}$/;

class RlModelDistributionError extends Error {
    constructor(statusCode, code, message) {
        super(message);
        this.name = 'RlModelDistributionError';
        this.statusCode = statusCode;
        this.code = code;
    }
}

function requiredString(value, label, maxLength = 256) {
    if (typeof value !== 'string' || !value.trim() || value.length > maxLength) {
        throw new RlModelDistributionError(
            400,
            'invalid-field',
            `${label} must be a non-empty string up to ${maxLength} characters.`,
        );
    }
    return value.trim();
}

function optionalString(value, label, maxLength = 256) {
    if (value === undefined || value === null || value === '') return null;
    return requiredString(value, label, maxLength);
}

function requiredInteger(value, label, minimum, maximum) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
        throw new RlModelDistributionError(
            400,
            'invalid-field',
            `${label} must be an integer in [${minimum}, ${maximum}].`,
        );
    }
    return value;
}

function requiredSha256(value, label) {
    const normalized = requiredString(value, label, 64).toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(normalized)) {
        throw new RlModelDistributionError(
            400,
            'invalid-hash',
            `${label} must be a 64-character hexadecimal SHA-256.`,
        );
    }
    return normalized;
}

function resolveInsideRoot(root, relativePath) {
    if (typeof relativePath !== 'string' || !relativePath || path.isAbsolute(relativePath)) return null;
    const normalizedRoot = path.resolve(root);
    const resolved = path.resolve(normalizedRoot, relativePath);
    if (resolved === normalizedRoot || !resolved.startsWith(`${normalizedRoot}${path.sep}`)) return null;
    return resolved;
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

class RlModelDistributionManager {
    constructor(root, options = {}) {
        if (typeof root !== 'string' || !root.trim()) {
            throw new TypeError('RL model distribution root must be a non-empty path.');
        }
        this.root = path.resolve(root);
        this.maxChunkBytes = options.maxChunkBytes || DEFAULT_MAX_CHUNK_BYTES;
        this.maxBundleBytes = options.maxBundleBytes || DEFAULT_MAX_BUNDLE_BYTES;
        this.userBytesPerWindow = options.userBytesPerWindow || DEFAULT_USER_BYTES_PER_WINDOW;
        this.rateWindowMs = options.rateWindowMs || DEFAULT_RATE_WINDOW_MS;
        this.now = options.now || (() => Date.now());
        this.userQuotas = new Map();
        this.pointerCache = new Map();
    }

    async handle(params, context) {
        if (!params || typeof params !== 'object') {
            throw new RlModelDistributionError(400, 'invalid-request', 'Model distribution request must be an object.');
        }
        const userId = requiredString(context?.userId, 'authenticated user ID');
        this._cleanupQuotas();
        switch (params.Type) {
            case 'rl-model-current':
                return this.current(params, userId);
            case 'rl-model-chunk':
                return this.chunk(params, userId);
            default:
                throw new RlModelDistributionError(
                    400,
                    'invalid-request',
                    `Unsupported RL model request type ${params.Type}.`,
                );
        }
    }

    _validateClientPolicy(params) {
        if (params.PolicyAbiVersion !== RL_POLICY.policyAbiVersion ||
            params.PolicySignature !== RL_POLICY.policySignature) {
            throw new RlModelDistributionError(
                409,
                'incompatible-client-policy',
                'Client policy ABI/signature does not match the server distribution contract.',
            );
        }
    }

    _platform(params) {
        const platform = requiredString(params.Platform, 'Platform', 64);
        if (!PLATFORM_PATTERN.test(platform)) {
            throw new RlModelDistributionError(400, 'invalid-platform', 'Platform contains unsupported characters.');
        }
        return platform;
    }

    async current(params, _userId) {
        this._validateClientPolicy(params);
        const platform = this._platform(params);
        const currentDeploymentId = optionalString(params.CurrentDeploymentId, 'CurrentDeploymentId', 64);
        if (currentDeploymentId && !DEPLOYMENT_ID_PATTERN.test(currentDeploymentId)) {
            throw new RlModelDistributionError(400, 'invalid-deployment-id', 'CurrentDeploymentId is malformed.');
        }
        const record = await this._loadCurrent(platform);
        const upToDate = currentDeploymentId === record.deploymentId;
        return {
            Platform: platform,
            UpToDate: upToDate,
            DeploymentId: record.deploymentId,
            ModelId: record.modelId,
            BundleSha256: record.bundleSha256,
            BundleSizeBytes: record.bundleSizeBytes,
            ChunkBytes: this.maxChunkBytes,
            PolicyAbiVersion: RL_POLICY.policyAbiVersion,
            PolicySignature: RL_POLICY.policySignature,
        };
    }

    async chunk(params, userId) {
        this._validateClientPolicy(params);
        const platform = this._platform(params);
        const deploymentId = requiredString(params.DeploymentId, 'DeploymentId', 64);
        if (!DEPLOYMENT_ID_PATTERN.test(deploymentId)) {
            throw new RlModelDistributionError(400, 'invalid-deployment-id', 'DeploymentId is malformed.');
        }
        const expectedBundleSha256 = requiredSha256(params.BundleSha256, 'BundleSha256');
        const record = await this._loadCurrent(platform);
        if (deploymentId !== record.deploymentId || expectedBundleSha256 !== record.bundleSha256) {
            throw new RlModelDistributionError(
                409,
                'deployment-changed',
                'The current RL deployment changed while the client was downloading it.',
            );
        }

        const offset = requiredInteger(params.Offset, 'Offset', 0, record.bundleSizeBytes - 1);
        const requestedLength = requiredInteger(params.Length, 'Length', 1, this.maxChunkBytes);
        const length = Math.min(requestedLength, record.bundleSizeBytes - offset);
        this._reserveQuota(userId, length);

        const handle = await fsp.open(record.bundlePath, 'r');
        const buffer = Buffer.alloc(length);
        let bytesRead;
        try {
            ({ bytesRead } = await handle.read(buffer, 0, length, offset));
        } finally {
            await handle.close();
        }
        if (bytesRead !== length) {
            this.pointerCache.delete(platform);
            throw new RlModelDistributionError(
                503,
                'distribution-read-failed',
                'RL model bundle changed or became unreadable during download.',
            );
        }
        const nextOffset = offset + bytesRead;
        return {
            Platform: platform,
            DeploymentId: record.deploymentId,
            BundleSha256: record.bundleSha256,
            Offset: offset,
            NextOffset: nextOffset,
            Complete: nextOffset === record.bundleSizeBytes,
            Data: buffer.toString('base64'),
        };
    }

    _reserveQuota(userId, bytes) {
        const now = this.now();
        let quota = this.userQuotas.get(userId);
        if (!quota || now - quota.windowStart >= this.rateWindowMs) {
            quota = { windowStart: now, bytes: 0 };
        }
        if (quota.bytes + bytes > this.userBytesPerWindow) {
            throw new RlModelDistributionError(
                429,
                'download-rate-limit',
                'Authenticated user exceeded the RL model download byte quota for the current window.',
            );
        }
        quota.bytes += bytes;
        this.userQuotas.set(userId, quota);
    }

    _cleanupQuotas() {
        const now = this.now();
        for (const [userId, quota] of this.userQuotas.entries()) {
            if (now - quota.windowStart >= this.rateWindowMs) this.userQuotas.delete(userId);
        }
    }

    async _loadCurrent(platform) {
        const pointerPath = path.join(this.root, `current-${platform}.json`);
        let pointerStats;
        try {
            pointerStats = await fsp.stat(pointerPath);
        } catch (error) {
            if (error?.code === 'ENOENT') {
                throw new RlModelDistributionError(
                    404,
                    'model-not-published',
                    `No RL model is currently published for ${platform}.`,
                );
            }
            throw error;
        }
        if (!pointerStats.isFile()) {
            throw new RlModelDistributionError(500, 'distribution-corrupt', 'RL model pointer is not a file.');
        }

        const cached = this.pointerCache.get(platform);
        if (cached && cached.pointerMtimeMs === pointerStats.mtimeMs && cached.pointerSize === pointerStats.size) {
            let bundleStats;
            try {
                bundleStats = await fsp.stat(cached.bundlePath);
            } catch (error) {
                this.pointerCache.delete(platform);
                if (error?.code === 'ENOENT') {
                    throw new RlModelDistributionError(500, 'distribution-corrupt', 'Published RL model bundle is missing.');
                }
                throw error;
            }
            if (bundleStats.isFile() && bundleStats.size === cached.bundleSizeBytes &&
                bundleStats.mtimeMs === cached.bundleMtimeMs) {
                return cached;
            }
            this.pointerCache.delete(platform);
        }

        let pointer;
        try {
            pointer = JSON.parse(await fsp.readFile(pointerPath, 'utf8'));
        } catch (error) {
            if (error instanceof SyntaxError) {
                throw new RlModelDistributionError(500, 'distribution-corrupt', 'RL model pointer contains invalid JSON.');
            }
            throw error;
        }
        const identity = pointer?.identity;
        if (pointer?.schema_version !== DISTRIBUTION_POINTER_SCHEMA_VERSION ||
            !identity || typeof identity !== 'object' || Array.isArray(identity) ||
            identity.schema_version !== DISTRIBUTION_POINTER_SCHEMA_VERSION ||
            identity.platform !== platform ||
            !DEPLOYMENT_ID_PATTERN.test(identity.deployment_id || '') ||
            !MODEL_ID_PATTERN.test(identity.model_id || '') ||
            !/^[0-9a-f]{64}$/.test(identity.bundle_sha256 || '') ||
            !/^[0-9a-f]{64}$/.test(identity.manifest_sha256 || '') ||
            !/^[0-9a-f]{64}$/.test(identity.model_sha256 || '') ||
            identity.policy_abi_version !== RL_POLICY.policyAbiVersion ||
            identity.policy_signature !== RL_POLICY.policySignature ||
            !Number.isSafeInteger(identity.bundle_size_bytes) ||
            identity.bundle_size_bytes <= 0 || identity.bundle_size_bytes > this.maxBundleBytes) {
            throw new RlModelDistributionError(500, 'distribution-corrupt', 'RL model pointer identity is malformed or incompatible.');
        }
        const bundlePath = resolveInsideRoot(this.root, identity.bundle_path);
        if (!bundlePath) {
            throw new RlModelDistributionError(500, 'distribution-corrupt', 'RL model pointer bundle path escapes the distribution root.');
        }
        let bundleStats;
        try {
            bundleStats = await fsp.stat(bundlePath);
        } catch (error) {
            if (error?.code === 'ENOENT') {
                throw new RlModelDistributionError(500, 'distribution-corrupt', 'Published RL model bundle is missing.');
            }
            throw error;
        }
        if (!bundleStats.isFile() || bundleStats.size !== identity.bundle_size_bytes) {
            throw new RlModelDistributionError(500, 'distribution-corrupt', 'Published RL model bundle size does not match its pointer.');
        }
        const actualHash = await sha256File(bundlePath);
        if (actualHash !== identity.bundle_sha256) {
            throw new RlModelDistributionError(500, 'distribution-corrupt', 'Published RL model bundle failed SHA-256 verification.');
        }
        const record = {
            platform,
            deploymentId: identity.deployment_id,
            modelId: identity.model_id,
            bundlePath,
            bundleSha256: identity.bundle_sha256,
            bundleSizeBytes: identity.bundle_size_bytes,
            pointerMtimeMs: pointerStats.mtimeMs,
            pointerSize: pointerStats.size,
            bundleMtimeMs: bundleStats.mtimeMs,
        };
        this.pointerCache.set(platform, record);
        return record;
    }
}

module.exports = {
    RL_MODEL_REQUEST_TYPES,
    DISTRIBUTION_POINTER_SCHEMA_VERSION,
    DEFAULT_MAX_CHUNK_BYTES,
    DEFAULT_MAX_BUNDLE_BYTES,
    DEFAULT_USER_BYTES_PER_WINDOW,
    RlModelDistributionError,
    RlModelDistributionManager,
    resolveInsideRoot,
};
