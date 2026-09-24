'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
    ENABLE_ENV,
    WAN_ACTORS_ENV,
    WAN_ENVS_PER_ACTOR_ENV,
    WAN_MIN_ACTORS_ENV,
    WAN_BROKER_PORT_ENV,
    WAN_AUTH_TOKEN_FILE_ENV,
    WAN_MAX_QUEUED_BATCHES_ENV,
    WAN_ACTOR_LEASE_SECONDS_ENV,
    MAX_WAN_ACTORS,
    buildContinualLearningSpec,
} = require('../rlContinualLearningLauncher');

function fixture(t) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-continual-launcher-'));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const assets = path.join(root, 'Assets');
    const training = path.join(assets, 'Training');
    fs.mkdirSync(training, { recursive: true });
    fs.writeFileSync(path.join(training, 'bees_continual_service.py'), '# test\n');
    fs.writeFileSync(path.join(training, 'bees_continual_elastic_wan_service.py'), '# test\n');
    const env = {
        [ENABLE_ENV]: '1',
        BEES_RL_CONTINUAL_ROOT: path.join(root, 'continual'),
        BEES_RL_ASSETS_ROOT: assets,
        BEES_RL_TRAINING_ENV: path.join(root, 'Bees RL Training.exe'),
        BEES_RL_TELEMETRY_UPLOAD_DIR: path.join(root, 'telemetry'),
        BEES_RL_MODEL_DISTRIBUTION_DIR: path.join(root, 'models'),
        BEES_RL_GAME_BUILD_VERSION: '2026.09.14-test',
        BEES_RL_UNITY_EDITOR: path.join(root, 'Unity.exe'),
        BEES_RL_UNITY_PROJECT_ROOT: path.join(root, 'Project'),
    };
    return { root, assets, env };
}

function configureWan(root, env) {
    const tokenFile = path.join(root, 'wan-token.txt');
    fs.writeFileSync(tokenFile, '0123456789abcdef0123456789abcdef\n');
    env[WAN_ACTORS_ENV] = '12';
    env[WAN_MIN_ACTORS_ENV] = '0';
    env[WAN_AUTH_TOKEN_FILE_ENV] = tokenFile;
    return tokenFile;
}

test('continual learning autostart is opt-in at the server process boundary', () => {
    assert.equal(buildContinualLearningSpec({}), null);
    assert.equal(buildContinualLearningSpec({ [ENABLE_ENV]: '0' }), null);
});

test('enabled autostart fails closed when required machine paths are missing', t => {
    const { env } = fixture(t);
    delete env.BEES_RL_TRAINING_ENV;
    assert.throws(
        () => buildContinualLearningSpec(env),
        /BEES_RL_TRAINING_ENV/,
    );
});

test('enabled autostart launches the autonomous service with shared quarantine and distribution roots', t => {
    const { assets, env } = fixture(t);
    env.BEES_RL_PYTHON = 'python-test';
    env.BEES_RL_GENERATION_STEPS = '250000';
    env.BEES_RL_NUM_ENVS = '8';
    env.BEES_RL_CONTINUAL_RETRY_SECONDS = '7.5';
    env.BEES_RL_CONTINUAL_RUN_ID = 'bees-continuous-test';
    env.BEES_RL_PLATFORM = 'WindowsPlayer';
    env.BEES_RL_CLUSTER_ENV_ARGS_JSON = '["--rl-map-size=128","--rl-bee-ship-types=Wasp,Hornet"]';

    const spec = buildContinualLearningSpec(env);
    assert.equal(spec.executable, 'python-test');
    assert.equal(spec.cwd, path.resolve(assets));
    assert.equal(spec.args[0], path.join(path.resolve(assets), 'Training', 'bees_continual_service.py'));
    assert.ok(spec.args.includes(`--telemetry-quarantine=${path.resolve(env.BEES_RL_TELEMETRY_UPLOAD_DIR)}`));
    assert.ok(spec.args.includes(`--model-distribution-root=${path.resolve(env.BEES_RL_MODEL_DISTRIBUTION_DIR)}`));
    assert.ok(spec.args.includes('--generation-steps=250000'));
    assert.ok(spec.args.includes('--num-envs=8'));
    assert.ok(spec.args.includes('--retry-seconds=7.5'));
    assert.ok(spec.args.includes('--run-id=bees-continuous-test'));
    assert.ok(spec.args.includes('--platform=WindowsPlayer'));
    assert.ok(spec.args.includes('--environment-args-json=["--rl-map-size=128","--rl-bee-ship-types=Wasp,Hornet"]'));
});

test('ordinary non-WAN training still rejects zero local environments', t => {
    const { env } = fixture(t);
    env.BEES_RL_NUM_ENVS = '0';
    assert.throws(() => buildContinualLearningSpec(env), /BEES_RL_NUM_ENVS/);

    env.BEES_RL_NUM_ENVS = '4';
    env.BEES_RL_CONTINUAL_RETRY_SECONDS = 'not-a-number';
    assert.throws(() => buildContinualLearningSpec(env), /BEES_RL_CONTINUAL_RETRY_SECONDS/);
});

test('elastic WAN defaults Exeter to learner-only zero-local mode', t => {
    const { root, assets, env } = fixture(t);
    const tokenFile = configureWan(root, env);
    env[WAN_BROKER_PORT_ENV] = '56051';
    env[WAN_MAX_QUEUED_BATCHES_ENV] = '48';
    env[WAN_ACTOR_LEASE_SECONDS_ENV] = '90';

    const spec = buildContinualLearningSpec(env);
    assert.equal(
        spec.args[0],
        path.join(path.resolve(assets), 'Training', 'bees_continual_elastic_wan_service.py'),
    );
    assert.ok(spec.args.includes('--bees-wan-actors=12'));
    assert.ok(spec.args.includes('--bees-wan-min-actors=0'));
    assert.ok(spec.args.includes('--bees-wan-broker-port=56051'));
    assert.ok(spec.args.includes(`--bees-wan-auth-token-file=${path.resolve(tokenFile)}`));
    assert.ok(spec.args.includes('--bees-wan-max-queued-batches=48'));
    assert.ok(spec.args.includes('--bees-wan-actor-lease-seconds=90'));
    assert.ok(spec.args.includes('--num-envs=0'));
    assert.ok(!spec.args.some(arg => arg.startsWith('--bees-wan-envs-per-actor=')));
});

test('elastic WAN accepts explicit zero local envs and positive values opt Exeter into hybrid simulation', t => {
    const { root, env } = fixture(t);
    configureWan(root, env);

    env.BEES_RL_NUM_ENVS = '0';
    let spec = buildContinualLearningSpec(env);
    assert.ok(spec.args.includes('--num-envs=0'));

    env.BEES_RL_NUM_ENVS = '32';
    spec = buildContinualLearningSpec(env);
    assert.ok(spec.args.includes('--num-envs=32'));
    assert.ok(!spec.args.includes('--num-envs=0'));
});

test('WAN authentication remains mandatory but per-actor env count is no longer central configuration', t => {
    const { root, env } = fixture(t);
    env[WAN_ACTORS_ENV] = '12';
    assert.throws(() => buildContinualLearningSpec(env), new RegExp(WAN_AUTH_TOKEN_FILE_ENV));

    env[WAN_AUTH_TOKEN_FILE_ENV] = path.join(root, 'missing-token.txt');
    assert.throws(() => buildContinualLearningSpec(env), /authentication token file does not exist/);
});

test('elastic WAN slot ceiling, minimum actors, and broker port are bounded before startup', t => {
    const { root, env } = fixture(t);
    const tokenFile = path.join(root, 'wan-token.txt');
    fs.writeFileSync(tokenFile, '0123456789abcdef0123456789abcdef\n');
    env[WAN_AUTH_TOKEN_FILE_ENV] = tokenFile;

    env[WAN_ACTORS_ENV] = String(MAX_WAN_ACTORS + 1);
    assert.throws(() => buildContinualLearningSpec(env), new RegExp(WAN_ACTORS_ENV));

    env[WAN_ACTORS_ENV] = '4';
    env[WAN_MIN_ACTORS_ENV] = '5';
    assert.throws(() => buildContinualLearningSpec(env), new RegExp(WAN_MIN_ACTORS_ENV));

    env[WAN_MIN_ACTORS_ENV] = '0';
    env[WAN_BROKER_PORT_ENV] = '70000';
    assert.throws(() => buildContinualLearningSpec(env), new RegExp(WAN_BROKER_PORT_ENV));
});

test('obsolete fixed envs-per-actor setting is rejected with migration guidance', t => {
    const { root, env } = fixture(t);
    const tokenFile = path.join(root, 'wan-token.txt');
    fs.writeFileSync(tokenFile, '0123456789abcdef0123456789abcdef\n');
    env[WAN_ACTORS_ENV] = '12';
    env[WAN_AUTH_TOKEN_FILE_ENV] = tokenFile;
    env[WAN_ENVS_PER_ACTOR_ENV] = '32';
    assert.throws(() => buildContinualLearningSpec(env), /obsolete/);
});

test('stray WAN settings are rejected rather than silently ignored', t => {
    const { env } = fixture(t);
    env[WAN_BROKER_PORT_ENV] = '56051';
    assert.throws(() => buildContinualLearningSpec(env), new RegExp(WAN_ACTORS_ENV));
});
test('invalid central environment-argument JSON fails before trainer startup', t => {
    const { env } = fixture(t);
    env.BEES_RL_CLUSTER_ENV_ARGS_JSON = '{"not":"a-list"}';
    assert.throws(() => buildContinualLearningSpec(env), /JSON array of strings/);

    env.BEES_RL_CLUSTER_ENV_ARGS_JSON = 'not-json';
    assert.throws(() => buildContinualLearningSpec(env), /valid JSON/);
});
