'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { URL } = require('node:url');

const ENABLE_ENV = 'BEES_RL_TRAINER_CONTROL';
const TOKEN_FILE_ENV = 'BEES_RL_CONTROL_TOKEN_FILE';
const HOST_ENV = 'BEES_RL_CONTROL_HOST';
const PORT_ENV = 'BEES_RL_CONTROL_PORT';
const LEASE_SECONDS_ENV = 'BEES_RL_CONTROL_LEASE_SECONDS';
const TRAINING_ENABLED_ENV = 'BEES_RL_TRAINING_ENABLED';
const BUILD_DIR_ENV = 'BEES_RL_TRAINER_BUILD_DIR';
const BUILD_EXECUTABLE_ENV = 'BEES_RL_TRAINER_BUILD_EXECUTABLE';
const PLATFORM_ENV = 'BEES_RL_TRAINER_PLATFORM';
const LINUX_BUILD_DIR_ENV = 'BEES_RL_LINUX_TRAINER_BUILD_DIR';
const LINUX_EXECUTABLE_ENV = 'BEES_RL_LINUX_TRAINER_EXECUTABLE';
const REQUIRE_LINUX_ENV = 'BEES_RL_REQUIRE_LINUX_BUILD';
const CLUSTER_ENV_ARGS_JSON_ENV = 'BEES_RL_CLUSTER_ENV_ARGS_JSON';
const DEFAULT_PORT = 7148;
const DEFAULT_LEASE_SECONDS = 30;
const MAX_HEARTBEAT_BYTES = 64 * 1024;
const TRAINING_CONFIG_ENV = Object.freeze([
    'BEES_RL_GAME_BUILD_VERSION',
    'BEES_RL_GENERATION_STEPS',
    'BEES_RL_NUM_ENVS',
    'BEES_RL_WAN_ACTORS',
    'BEES_RL_WAN_MIN_ACTORS',
    'BEES_RL_WAN_BROKER_PORT',
    'BEES_RL_WAN_MAX_QUEUED_BATCHES',
    'BEES_RL_WAN_ACTOR_LEASE_SECONDS',
    'BEES_RL_TRAINER_CONFIG',
    'BEES_RL_CONTINUAL_CONFIG',
    'BEES_RL_COMPETENCY_SUITE',
    'BEES_RL_PLATFORM',
    'BEES_RL_CONTINUAL_RUN_ID',
]);

function nonEmpty(value) {
    return typeof value === 'string' && value.trim().length > 0;
}

function lowerHexSha256(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function canonicalJson(value) {
    if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
    if (value && typeof value === 'object') {
        const keys = Object.keys(value).sort();
        return `{${keys.map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
    }
    return JSON.stringify(value);
}

function hashFileSync(filePath) {
    const hash = crypto.createHash('sha256');
    const fd = fs.openSync(filePath, 'r');
    const buffer = Buffer.allocUnsafe(1024 * 1024);
    try {
        for (;;) {
            const read = fs.readSync(fd, buffer, 0, buffer.length, null);
            if (!read) break;
            hash.update(buffer.subarray(0, read));
        }
    } finally {
        fs.closeSync(fd);
    }
    return hash.digest('hex');
}

function platformName(value = process.platform) {
    const normalized = String(value || '').trim().toLowerCase();
    if (['linux', 'linux-x64', 'linux64'].includes(normalized)) return 'linux-x64';
    if (['win32', 'windows', 'windows-x64', 'win-x64'].includes(normalized)) return 'windows-x64';
    if (['darwin', 'mac', 'macos', 'macos-x64'].includes(normalized)) return 'macos-x64';
    throw new Error(`Unsupported trainer platform ${JSON.stringify(value)}.`);
}

function parseInteger(env, name, defaultValue, minimum, maximum) {
    if (!nonEmpty(env[name])) return defaultValue;
    const value = Number(env[name]);
    if (!Number.isSafeInteger(value) || value < minimum || (maximum !== undefined && value > maximum)) {
        throw new Error(`${name} must be an integer between ${minimum} and ${maximum ?? 'infinity'}.`);
    }
    return value;
}

function parsePositiveNumber(env, name, defaultValue) {
    if (!nonEmpty(env[name])) return defaultValue;
    const value = Number(env[name]);
    if (!Number.isFinite(value) || value <= 0) throw new Error(`${name} must be a positive number.`);
    return value;
}

function isPathInside(root, candidate) {
    const relative = path.relative(root, candidate);
    return relative === '' || (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative));
}

function walkBuildFiles(root) {
    const result = [];
    const visit = current => {
        const entries = fs.readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
        for (const entry of entries) {
            const absolute = path.join(current, entry.name);
            if (entry.isSymbolicLink()) {
                throw new Error(`Trainer build may not contain symbolic links: ${absolute}`);
            }
            if (entry.isDirectory()) {
                visit(absolute);
                continue;
            }
            if (!entry.isFile()) continue;
            const stat = fs.statSync(absolute);
            result.push({
                path: path.relative(root, absolute).split(path.sep).join('/'),
                size: stat.size,
                sha256: hashFileSync(absolute),
                mode: stat.mode & 0o777,
            });
        }
    };
    visit(root);
    return result;
}

function resolveEntrypoint(root, configured, fallbackAbsolute, platform) {
    if (nonEmpty(configured)) {
        const candidate = path.isAbsolute(configured.trim())
            ? path.resolve(configured.trim())
            : path.resolve(root, configured.trim());
        if (!isPathInside(root, candidate) || !fs.existsSync(candidate) || !fs.statSync(candidate).isFile()) {
            throw new Error(`Trainer executable must be a file inside build root ${root}: ${candidate}`);
        }
        return candidate;
    }
    if (fallbackAbsolute) {
        const candidate = path.resolve(fallbackAbsolute);
        if (!isPathInside(root, candidate) || !fs.existsSync(candidate) || !fs.statSync(candidate).isFile()) {
            throw new Error(`BEES_RL_TRAINING_ENV must be a file inside trainer build root ${root}: ${candidate}`);
        }
        return candidate;
    }
    if (platform === 'linux-x64') {
        const candidates = [];
        const visit = current => {
            for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
                const absolute = path.join(current, entry.name);
                if (entry.isDirectory()) visit(absolute);
                else if (entry.isFile() && entry.name.endsWith('.x86_64')) candidates.push(absolute);
            }
        };
        visit(root);
        if (candidates.length === 1) return candidates[0];
        throw new Error(
            `${LINUX_EXECUTABLE_ENV} is required because ${root} contains ${candidates.length} *.x86_64 candidates.`,
        );
    }
    throw new Error(`${BUILD_EXECUTABLE_ENV} is required when the trainer executable cannot be inferred.`);
}

function buildManifest(platform, rootPath, executable, gameBuildVersion, options = {}) {
    const root = path.resolve(rootPath);
    if (!fs.existsSync(root) || !fs.statSync(root).isDirectory()) {
        throw new Error(`Trainer build directory does not exist: ${root}`);
    }
    const entrypointAbsolute = resolveEntrypoint(root, executable, options.fallbackExecutable, platform);
    if (options.scanBuilds === false) {
        return Object.freeze({
            platform,
            root,
            entrypointAbsolute,
            entrypoint: path.relative(root, entrypointAbsolute).split(path.sep).join('/'),
            gameBuildVersion,
            manifest: null,
            fileMap: null,
        });
    }
    const files = walkBuildFiles(root);
    const entrypoint = path.relative(root, entrypointAbsolute).split(path.sep).join('/');
    if (!files.some(file => file.path === entrypoint)) {
        throw new Error(`Trainer entrypoint is not present in build manifest: ${entrypoint}`);
    }
    const identity = {
        schema_version: 1,
        platform,
        game_build_version: gameBuildVersion,
        entrypoint,
        files,
    };
    const manifestSha256 = lowerHexSha256(canonicalJson(identity));
    const buildId = `build-${manifestSha256.slice(0, 24)}`;
    const totalSizeBytes = files.reduce((sum, file) => sum + file.size, 0);
    const manifest = Object.freeze({
        ...identity,
        build_id: buildId,
        manifest_sha256: manifestSha256,
        file_count: files.length,
        total_size_bytes: totalSizeBytes,
    });
    return Object.freeze({
        platform,
        root,
        entrypointAbsolute,
        entrypoint,
        gameBuildVersion,
        manifest,
        fileMap: new Map(files.map(file => [file.path, Object.freeze({ ...file, absolute: path.resolve(root, ...file.path.split('/')) })])),
    });
}

function parseClusterEnvArgs(env) {
    if (!nonEmpty(env[CLUSTER_ENV_ARGS_JSON_ENV])) return [];
    let parsed;
    try {
        parsed = JSON.parse(env[CLUSTER_ENV_ARGS_JSON_ENV]);
    } catch (error) {
        throw new Error(`${CLUSTER_ENV_ARGS_JSON_ENV} must be valid JSON: ${error.message}`);
    }
    if (!Array.isArray(parsed) || parsed.some(value => typeof value !== 'string')) {
        throw new Error(`${CLUSTER_ENV_ARGS_JSON_ENV} must be a JSON array of strings.`);
    }
    return parsed;
}

function trainingConfigIdentity(env) {
    const values = {};
    for (const name of TRAINING_CONFIG_ENV) {
        if (nonEmpty(env[name])) values[name] = env[name].trim();
    }
    const environmentArgs = parseClusterEnvArgs(env);
    const identity = { values, environment_args: environmentArgs };
    return Object.freeze({
        revision: lowerHexSha256(canonicalJson(identity)),
        environmentArgs: Object.freeze([...environmentArgs]),
        values: Object.freeze(values),
    });
}

function readToken(filePath) {
    const resolved = path.resolve(filePath);
    if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
        throw new Error(`Trainer-control authentication token file does not exist: ${resolved}`);
    }
    const token = fs.readFileSync(resolved, 'utf8').trim();
    if (token.length < 32 || token.length > 1024 || /\s/.test(token)) {
        throw new Error('Trainer-control token must contain 32-1024 non-whitespace characters.');
    }
    return Object.freeze({ path: resolved, value: token });
}

function buildTrainerControlConfig(env = process.env, options = {}) {
    if (String(env[ENABLE_ENV] || '').trim() !== '1') return null;
    const tokenFile = nonEmpty(env[TOKEN_FILE_ENV])
        ? env[TOKEN_FILE_ENV].trim()
        : nonEmpty(env.BEES_RL_WAN_AUTH_TOKEN_FILE)
            ? env.BEES_RL_WAN_AUTH_TOKEN_FILE.trim()
            : null;
    if (!tokenFile) throw new Error(`${TOKEN_FILE_ENV} or BEES_RL_WAN_AUTH_TOKEN_FILE is required when ${ENABLE_ENV}=1.`);
    const token = readToken(tokenFile);
    const host = nonEmpty(env[HOST_ENV]) ? env[HOST_ENV].trim() : '127.0.0.1';
    if (!['127.0.0.1', '::1', 'localhost'].includes(host) && String(env.BEES_RL_CONTROL_ALLOW_PLAINTEXT_REMOTE || '').trim() !== '1') {
        throw new Error(
            `${HOST_ENV} defaults to loopback because trainer control uses bearer authentication over HTTP. ` +
            'Use SSH port forwarding, or explicitly set BEES_RL_CONTROL_ALLOW_PLAINTEXT_REMOTE=1.',
        );
    }
    const port = parseInteger(env, PORT_ENV, DEFAULT_PORT, options.allowPortZero ? 0 : 1, 65535);
    const leaseSeconds = parsePositiveNumber(env, LEASE_SECONDS_ENV, DEFAULT_LEASE_SECONDS);
    const trainingEnabled = String(env[TRAINING_ENABLED_ENV] ?? '1').trim() !== '0';
    const gameBuildVersion = nonEmpty(env.BEES_RL_GAME_BUILD_VERSION)
        ? env.BEES_RL_GAME_BUILD_VERSION.trim()
        : 'unspecified';
    if (!nonEmpty(env.BEES_RL_TRAINING_ENV)) {
        throw new Error(`BEES_RL_TRAINING_ENV is required when ${ENABLE_ENV}=1.`);
    }
    const currentExecutable = path.resolve(env.BEES_RL_TRAINING_ENV.trim());
    const currentRoot = nonEmpty(env[BUILD_DIR_ENV])
        ? path.resolve(env[BUILD_DIR_ENV].trim())
        : path.dirname(currentExecutable);
    const currentPlatform = platformName(nonEmpty(env[PLATFORM_ENV]) ? env[PLATFORM_ENV] : process.platform);
    const builds = new Map();
    builds.set(currentPlatform, buildManifest(
        currentPlatform,
        currentRoot,
        nonEmpty(env[BUILD_EXECUTABLE_ENV]) ? env[BUILD_EXECUTABLE_ENV].trim() : null,
        gameBuildVersion,
        { fallbackExecutable: currentExecutable, scanBuilds: options.scanBuilds !== false },
    ));

    const requireLinux = String(env[REQUIRE_LINUX_ENV] ?? '1').trim() !== '0';
    if (currentPlatform !== 'linux-x64') {
        if (!nonEmpty(env[LINUX_BUILD_DIR_ENV])) {
            if (requireLinux) {
                throw new Error(
                    `${LINUX_BUILD_DIR_ENV} is required when the central trainer is not Linux so remote Linux trainers receive the equivalent build.`,
                );
            }
        } else {
            const linuxRoot = path.resolve(env[LINUX_BUILD_DIR_ENV].trim());
            builds.set('linux-x64', buildManifest(
                'linux-x64',
                linuxRoot,
                nonEmpty(env[LINUX_EXECUTABLE_ENV]) ? env[LINUX_EXECUTABLE_ENV].trim() : null,
                gameBuildVersion,
                { scanBuilds: options.scanBuilds !== false },
            ));
        }
    }

    const trainingConfig = trainingConfigIdentity(env);
    return Object.freeze({
        enabled: true,
        host,
        port,
        leaseSeconds,
        trainingEnabled,
        token,
        gameBuildVersion,
        trainingConfig,
        builds,
    });
}

function authorized(request, token) {
    const header = request.headers.authorization;
    if (!header || !header.startsWith('Bearer ')) return false;
    const supplied = Buffer.from(header.slice(7), 'utf8');
    const expected = Buffer.from(token, 'utf8');
    return supplied.length === expected.length && crypto.timingSafeEqual(supplied, expected);
}

function sendJson(response, status, value) {
    const payload = Buffer.from(JSON.stringify(value));
    response.writeHead(status, {
        'content-type': 'application/json; charset=utf-8',
        'content-length': String(payload.length),
        'cache-control': 'no-store',
    });
    response.end(payload);
}

function readJsonBody(request, maximum = MAX_HEARTBEAT_BYTES) {
    return new Promise((resolve, reject) => {
        let size = 0;
        const chunks = [];
        request.on('data', chunk => {
            size += chunk.length;
            if (size > maximum) {
                reject(new Error('request-too-large'));
                request.destroy();
                return;
            }
            chunks.push(chunk);
        });
        request.on('end', () => {
            try {
                const parsed = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                resolve(parsed);
            } catch (error) {
                reject(error);
            }
        });
        request.on('error', reject);
    });
}

function validateHeartbeat(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('heartbeat must be an object');
    const nodeId = String(value.node_id || '').trim();
    if (!/^[A-Za-z0-9._:-]{1,128}$/.test(nodeId)) throw new Error('invalid node_id');
    const role = String(value.role || 'rollout').trim().toLowerCase();
    if (!['rollout', 'game'].includes(role)) throw new Error('role must be rollout or game');
    const platform = platformName(value.platform || 'linux-x64');
    const state = String(value.state || 'unknown').trim().slice(0, 64);
    const buildId = value.build_id == null ? null : String(value.build_id).trim().slice(0, 128);
    const configRevision = value.config_revision == null ? null : String(value.config_revision).trim().slice(0, 128);
    const actorId = Number.isSafeInteger(value.actor_id) ? value.actor_id : null;
    const envCount = Number.isSafeInteger(value.env_count) ? value.env_count : null;
    return { nodeId, role, platform, state, buildId, configRevision, actorId, envCount };
}

function installTrainerControl(hostObject, options = {}) {
    if (!hostObject || Object.prototype.hasOwnProperty.call(hostObject, '__beesTrainerControl')) {
        return hostObject?.__beesTrainerControl || null;
    }
    const config = options.config === undefined
        ? buildTrainerControlConfig(options.env || process.env, { allowPortZero: options.allowPortZero })
        : options.config;
    if (!config) {
        Object.defineProperty(hostObject, '__beesTrainerControl', { value: null, enumerable: false });
        return null;
    }
    for (const build of config.builds.values()) {
        if (!build.manifest || !build.fileMap) {
            throw new Error('Trainer-control runtime requires scanned build manifests.');
        }
    }

    const serverEpoch = options.serverEpoch || crypto.randomUUID();
    const now = options.now || (() => Date.now());
    const trainers = new Map();
    let stopped = false;
    let httpServer = null;

    const desiredFor = platform => {
        const build = config.builds.get(platform) || null;
        return {
            schema_version: 1,
            server_epoch: serverEpoch,
            lease_seconds: config.leaseSeconds,
            desired: {
                training_enabled: config.trainingEnabled,
                config_revision: config.trainingConfig.revision,
                environment_args: config.trainingConfig.environmentArgs,
                environment_args_authority: 'central-wan-session',
                game_build_version: config.gameBuildVersion,
                build: build ? {
                    platform: build.platform,
                    build_id: build.manifest.build_id,
                    manifest_sha256: build.manifest.manifest_sha256,
                    entrypoint: build.manifest.entrypoint,
                    file_count: build.manifest.file_count,
                    total_size_bytes: build.manifest.total_size_bytes,
                    manifest_url: `/v1/trainers/build/manifest?platform=${encodeURIComponent(build.platform)}`,
                } : null,
                available_platforms: [...config.builds.keys()].sort(),
            },
        };
    };

    const handleRequest = async (request, response) => {
        if (!authorized(request, config.token.value)) {
            sendJson(response, 401, { error: 'unauthorized' });
            return;
        }
        const url = new URL(request.url, `http://${request.headers.host || 'localhost'}`);
        try {
            if (request.method === 'POST' && url.pathname === '/v1/trainers/heartbeat') {
                const heartbeat = validateHeartbeat(await readJsonBody(request));
                const previous = trainers.get(heartbeat.nodeId);
                const record = Object.freeze({
                    ...heartbeat,
                    last_seen_ms: now(),
                    server_epoch: serverEpoch,
                });
                trainers.set(heartbeat.nodeId, record);
                if (!previous || previous.state !== record.state || previous.buildId !== record.buildId ||
                    previous.configRevision !== record.configRevision) {
                    console.log(
                        `[Bees trainer control] ${record.nodeId} role=${record.role} state=${record.state} ` +
                        `platform=${record.platform} build=${record.buildId || 'none'} envs=${record.envCount ?? 'n/a'}.`,
                    );
                }
                sendJson(response, 200, desiredFor(heartbeat.platform));
                return;
            }

            if (request.method === 'GET' && url.pathname === '/v1/trainers/build/manifest') {
                const platform = platformName(url.searchParams.get('platform') || '');
                const build = config.builds.get(platform);
                if (!build) {
                    sendJson(response, 404, { error: 'platform-build-unavailable', available_platforms: [...config.builds.keys()].sort() });
                    return;
                }
                sendJson(response, 200, build.manifest);
                return;
            }

            if (request.method === 'GET' && url.pathname === '/v1/trainers/build/file') {
                const platform = platformName(url.searchParams.get('platform') || '');
                const relative = url.searchParams.get('path') || '';
                const build = config.builds.get(platform);
                const file = build?.fileMap.get(relative);
                if (!build || !file || !isPathInside(build.root, file.absolute)) {
                    sendJson(response, 404, { error: 'build-file-not-found' });
                    return;
                }
                response.writeHead(200, {
                    'content-type': 'application/octet-stream',
                    'content-length': String(file.size),
                    'x-bees-sha256': file.sha256,
                    'cache-control': 'private, immutable',
                });
                const stream = fs.createReadStream(file.absolute);
                stream.on('error', error => {
                    console.error(`Trainer build read failed for ${file.absolute}: ${error.message}`);
                    response.destroy(error);
                });
                stream.pipe(response);
                return;
            }

            if (request.method === 'GET' && url.pathname === '/v1/trainers/status') {
                const cutoff = now() - config.leaseSeconds * 3000;
                for (const [nodeId, record] of trainers) {
                    if (record.last_seen_ms < cutoff) trainers.delete(nodeId);
                }
                sendJson(response, 200, {
                    schema_version: 1,
                    server_epoch: serverEpoch,
                    training_enabled: config.trainingEnabled,
                    config_revision: config.trainingConfig.revision,
                    trainers: [...trainers.values()].sort((a, b) => a.nodeId.localeCompare(b.nodeId)),
                });
                return;
            }

            sendJson(response, 404, { error: 'not-found' });
        } catch (error) {
            const status = error.message === 'request-too-large' ? 413 : 400;
            sendJson(response, status, { error: 'invalid-request', message: error.message });
        }
    };

    const start = () => {
        if (stopped || httpServer) return httpServer;
        httpServer = http.createServer((request, response) => {
            Promise.resolve(handleRequest(request, response)).catch(error => {
                console.error(`Trainer-control request failed: ${error.stack || error}`);
                if (!response.headersSent) sendJson(response, 500, { error: 'internal-error' });
                else response.destroy();
            });
        });
        httpServer.on('error', error => {
            console.error(`Bees trainer-control server error: ${error.message}`);
            if (options.onFatalError) {
                options.onFatalError(error);
                return;
            }
            process.exitCode = 1;
            process.nextTick(() => process.exit(1));
        });
        httpServer.listen(config.port, config.host, () => {
            const address = httpServer.address();
            const actualPort = typeof address === 'object' && address ? address.port : config.port;
            console.log(
                `Bees trainer control listening on ${config.host}:${actualPort}; ` +
                `epoch=${serverEpoch}; training=${config.trainingEnabled ? 'enabled' : 'stopped'}.`,
            );
        });
        return httpServer;
    };

    const stop = () => {
        if (stopped) return;
        stopped = true;
        const current = httpServer;
        httpServer = null;
        if (current) {
            try { current.close(); } catch { /* process shutdown is best effort */ }
        }
    };

    const control = Object.freeze({
        start,
        stop,
        get server() { return httpServer; },
        get serverEpoch() { return serverEpoch; },
        get trainers() { return trainers; },
        config,
    });
    Object.defineProperty(hostObject, '__beesTrainerControl', {
        configurable: false,
        enumerable: false,
        writable: false,
        value: control,
    });
    if (options.start !== false) start();
    process.once('exit', stop);
    return control;
}

module.exports = {
    ENABLE_ENV,
    TOKEN_FILE_ENV,
    HOST_ENV,
    PORT_ENV,
    LEASE_SECONDS_ENV,
    TRAINING_ENABLED_ENV,
    BUILD_DIR_ENV,
    BUILD_EXECUTABLE_ENV,
    PLATFORM_ENV,
    LINUX_BUILD_DIR_ENV,
    LINUX_EXECUTABLE_ENV,
    REQUIRE_LINUX_ENV,
    CLUSTER_ENV_ARGS_JSON_ENV,
    DEFAULT_PORT,
    DEFAULT_LEASE_SECONDS,
    platformName,
    canonicalJson,
    hashFileSync,
    buildManifest,
    trainingConfigIdentity,
    buildTrainerControlConfig,
    installTrainerControl,
};
