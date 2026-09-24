'use strict';

const fs = require('node:fs');
const path = require('node:path');

const ENABLE_ENV = 'BEES_RL_CONTINUAL_AUTOSTART';
const WAN_ACTORS_ENV = 'BEES_RL_WAN_ACTORS';
const WAN_ENVS_PER_ACTOR_ENV = 'BEES_RL_WAN_ENVS_PER_ACTOR'; // obsolete fixed-topology setting
const WAN_MIN_ACTORS_ENV = 'BEES_RL_WAN_MIN_ACTORS';
const WAN_BROKER_PORT_ENV = 'BEES_RL_WAN_BROKER_PORT';
const WAN_AUTH_TOKEN_FILE_ENV = 'BEES_RL_WAN_AUTH_TOKEN_FILE';
const WAN_MAX_QUEUED_BATCHES_ENV = 'BEES_RL_WAN_MAX_QUEUED_BATCHES';
const WAN_ACTOR_LEASE_SECONDS_ENV = 'BEES_RL_WAN_ACTOR_LEASE_SECONDS';
const MAX_WAN_ACTORS = 12;
const WAN_ENV = Object.freeze([
    WAN_ACTORS_ENV,
    WAN_ENVS_PER_ACTOR_ENV,
    WAN_MIN_ACTORS_ENV,
    WAN_BROKER_PORT_ENV,
    WAN_AUTH_TOKEN_FILE_ENV,
    WAN_MAX_QUEUED_BATCHES_ENV,
    WAN_ACTOR_LEASE_SECONDS_ENV,
]);
const REQUIRED_ENV = Object.freeze([
    'BEES_RL_CONTINUAL_ROOT',
    'BEES_RL_ASSETS_ROOT',
    'BEES_RL_TRAINING_ENV',
    'BEES_RL_TELEMETRY_UPLOAD_DIR',
    'BEES_RL_MODEL_DISTRIBUTION_DIR',
    'BEES_RL_GAME_BUILD_VERSION',
    'BEES_RL_UNITY_EDITOR',
    'BEES_RL_UNITY_PROJECT_ROOT',
]);

function nonEmpty(value) {
    return typeof value === 'string' && value.trim().length > 0;
}

function optionalArgument(args, env, name, flag) {
    if (nonEmpty(env[name])) args.push(`${flag}=${env[name].trim()}`);
}

function integerValue(env, name, { minimum = 1, maximum = null } = {}) {
    if (!nonEmpty(env[name])) return null;
    const value = Number(env[name]);
    if (
        !Number.isSafeInteger(value) ||
        value < minimum ||
        (maximum !== null && value > maximum)
    ) {
        const range = maximum === null
            ? `at least ${minimum}`
            : `between ${minimum} and ${maximum}`;
        throw new Error(`${name} must be an integer ${range} when ${ENABLE_ENV}=1.`);
    }
    return value;
}

function positiveIntegerValue(env, name, maximum = null) {
    return integerValue(env, name, { minimum: 1, maximum });
}

function requirePositiveInteger(env, name, flag, maximum = null) {
    const value = positiveIntegerValue(env, name, maximum);
    return value === null ? null : `${flag}=${value}`;
}

function requirePositiveNumber(env, name, flag) {
    if (!nonEmpty(env[name])) return null;
    const value = Number(env[name]);
    if (!Number.isFinite(value) || value <= 0) {
        throw new Error(`${name} must be a positive number when ${ENABLE_ENV}=1.`);
    }
    return `${flag}=${value}`;
}

function wanConfiguration(env, assetsRoot) {
    const requested = WAN_ENV.filter(name => nonEmpty(env[name]));
    if (!requested.length) return null;
    if (!nonEmpty(env[WAN_ACTORS_ENV])) {
        throw new Error(`${WAN_ACTORS_ENV} is required when any other BEES_RL_WAN_* setting is used.`);
    }
    if (nonEmpty(env[WAN_ENVS_PER_ACTOR_ENV])) {
        throw new Error(
            `${WAN_ENVS_PER_ACTOR_ENV} is obsolete. Elastic WAN actors choose 1-64 environments ` +
            'independently with --envs on each remote machine.',
        );
    }

    // This is a slot ceiling, not the number of machines that must be online. Runtime may have 0..N.
    const actorCount = integerValue(env, WAN_ACTORS_ENV, { minimum: 1, maximum: MAX_WAN_ACTORS });
    if (!nonEmpty(env[WAN_AUTH_TOKEN_FILE_ENV])) {
        throw new Error(`${WAN_AUTH_TOKEN_FILE_ENV} is required when ${WAN_ACTORS_ENV} is configured.`);
    }
    const authTokenFile = path.resolve(env[WAN_AUTH_TOKEN_FILE_ENV].trim());
    if (!fs.existsSync(authTokenFile) || !fs.statSync(authTokenFile).isFile()) {
        throw new Error(`WAN actor authentication token file does not exist: ${authTokenFile}`);
    }
    const authToken = fs.readFileSync(authTokenFile, 'utf8').trim();
    if (authToken.length < 32 || authToken.length > 1024 || /\s/.test(authToken)) {
        throw new Error(
            'WAN actor authentication token must contain 32-1024 non-whitespace characters.',
        );
    }

    const minActors = integerValue(env, WAN_MIN_ACTORS_ENV, { minimum: 0, maximum: actorCount });
    const brokerPort = positiveIntegerValue(env, WAN_BROKER_PORT_ENV, 65535);
    const maxQueued = positiveIntegerValue(env, WAN_MAX_QUEUED_BATCHES_ENV);
    const actorLease = requirePositiveNumber(
        env,
        WAN_ACTOR_LEASE_SECONDS_ENV,
        '--bees-wan-actor-lease-seconds',
    );
    const script = path.join(assetsRoot, 'Training', 'bees_continual_elastic_wan_service.py');
    if (!fs.existsSync(script)) {
        throw new Error(`Elastic WAN continual-learning service script does not exist: ${script}`);
    }

    const args = [
        `--bees-wan-actors=${actorCount}`,
        `--bees-wan-auth-token-file=${authTokenFile}`,
    ];
    if (minActors !== null) args.push(`--bees-wan-min-actors=${minActors}`);
    if (brokerPort !== null) args.push(`--bees-wan-broker-port=${brokerPort}`);
    if (maxQueued !== null) args.push(`--bees-wan-max-queued-batches=${maxQueued}`);
    if (actorLease !== null) args.push(actorLease);
    return Object.freeze({ script, args: Object.freeze(args) });
}

function buildContinualLearningSpec(env = process.env) {
    if (String(env[ENABLE_ENV] || '').trim() !== '1') return null;

    const missing = REQUIRED_ENV.filter(name => !nonEmpty(env[name]));
    if (missing.length) {
        throw new Error(
            `${ENABLE_ENV}=1 requires: ${missing.join(', ')}. ` +
            'The server will not start a partially configured continual-learning system.',
        );
    }

    const assetsRoot = path.resolve(env.BEES_RL_ASSETS_ROOT.trim());
    const wan = wanConfiguration(env, assetsRoot);
    const script = wan
        ? wan.script
        : path.join(assetsRoot, 'Training', 'bees_continual_service.py');
    if (!fs.existsSync(script)) {
        throw new Error(`Continual-learning service script does not exist: ${script}`);
    }

    const python = nonEmpty(env.BEES_RL_PYTHON) ? env.BEES_RL_PYTHON.trim() : 'python';
    const args = [
        script,
        `--root=${path.resolve(env.BEES_RL_CONTINUAL_ROOT.trim())}`,
        `--assets-root=${assetsRoot}`,
        `--training-env=${path.resolve(env.BEES_RL_TRAINING_ENV.trim())}`,
        `--telemetry-quarantine=${path.resolve(env.BEES_RL_TELEMETRY_UPLOAD_DIR.trim())}`,
        `--model-distribution-root=${path.resolve(env.BEES_RL_MODEL_DISTRIBUTION_DIR.trim())}`,
        `--game-build-version=${env.BEES_RL_GAME_BUILD_VERSION.trim()}`,
        `--unity-editor=${path.resolve(env.BEES_RL_UNITY_EDITOR.trim())}`,
        `--unity-project-root=${path.resolve(env.BEES_RL_UNITY_PROJECT_ROOT.trim())}`,
    ];

    optionalArgument(args, env, 'BEES_RL_TRAINER_CONFIG', '--trainer-config');
    optionalArgument(args, env, 'BEES_RL_CONTINUAL_CONFIG', '--continual-config');
    optionalArgument(args, env, 'BEES_RL_COMPETENCY_SUITE', '--competency-suite');
    optionalArgument(args, env, 'BEES_RL_CONTINUAL_RUN_ID', '--run-id');
    optionalArgument(args, env, 'BEES_RL_PLATFORM', '--platform');

    const generationSteps = requirePositiveInteger(
        env,
        'BEES_RL_GENERATION_STEPS',
        '--generation-steps',
    );
    if (generationSteps) args.push(generationSteps);

    if (wan) {
        // WAN mode defaults Exeter to a pure learner. Set BEES_RL_NUM_ENVS=1+ to opt back into
        // hybrid local simulation. Explicit zero is valid only on this elastic WAN path.
        const configuredLocalEnvs = integerValue(env, 'BEES_RL_NUM_ENVS', { minimum: 0 });
        const localEnvs = configuredLocalEnvs === null ? 0 : configuredLocalEnvs;
        args.push(`--num-envs=${localEnvs}`);
    } else {
        const numEnvs = requirePositiveInteger(env, 'BEES_RL_NUM_ENVS', '--num-envs');
        if (numEnvs) args.push(numEnvs);
    }

    const retrySeconds = requirePositiveNumber(
        env,
        'BEES_RL_CONTINUAL_RETRY_SECONDS',
        '--retry-seconds',
    );
    if (retrySeconds) args.push(retrySeconds);
    if (wan) args.push(...wan.args);

    return Object.freeze({
        executable: python,
        args: Object.freeze(args),
        cwd: assetsRoot,
        script,
    });
}

module.exports = {
    ENABLE_ENV,
    REQUIRED_ENV,
    WAN_ACTORS_ENV,
    WAN_ENVS_PER_ACTOR_ENV,
    WAN_MIN_ACTORS_ENV,
    WAN_BROKER_PORT_ENV,
    WAN_AUTH_TOKEN_FILE_ENV,
    WAN_MAX_QUEUED_BATCHES_ENV,
    WAN_ACTOR_LEASE_SECONDS_ENV,
    MAX_WAN_ACTORS,
    buildContinualLearningSpec,
};