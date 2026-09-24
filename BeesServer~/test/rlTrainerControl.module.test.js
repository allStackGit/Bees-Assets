'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
    buildManifest,
    buildTrainerControlConfig,
    installTrainerControl,
    trainingConfigIdentity,
} = require('./rlTrainerControl');

function tempBuild(t, name = 'Bees.x86_64') {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-control-'));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const executable = path.join(root, name);
    fs.writeFileSync(executable, 'trainer-binary');
    fs.chmodSync(executable, 0o755);
    fs.mkdirSync(path.join(root, 'Bees_Data'));
    fs.writeFileSync(path.join(root, 'Bees_Data', 'globalgamemanagers'), 'data');
    return { root, executable };
}

function tokenFile(t) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-control-token-'));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const file = path.join(root, 'token.txt');
    fs.writeFileSync(file, 'abcdefghijklmnopqrstuvwxyz0123456789TOKEN');
    return file;
}

function request(port, token, method, requestPath, body = null) {
    return new Promise((resolve, reject) => {
        const payload = body === null ? null : Buffer.from(JSON.stringify(body));
        const req = http.request({
            host: '127.0.0.1',
            port,
            method,
            path: requestPath,
            headers: {
                authorization: `Bearer ${token}`,
                ...(payload ? { 'content-type': 'application/json', 'content-length': payload.length } : {}),
            },
        }, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(chunk));
            response.on('end', () => resolve({
                status: response.statusCode,
                headers: response.headers,
                body: Buffer.concat(chunks),
            }));
        });
        req.on('error', reject);
        if (payload) req.write(payload);
        req.end();
    });
}

test('trainer build manifest is content-addressed and includes the complete directory', t => {
    const build = tempBuild(t);
    const first = buildManifest('linux-x64', build.root, null, 'v1');
    assert.equal(first.manifest.file_count, 2);
    assert.equal(first.manifest.entrypoint, 'Bees.x86_64');
    assert.match(first.manifest.build_id, /^build-[0-9a-f]{24}$/);

    const second = buildManifest('linux-x64', build.root, null, 'v1');
    assert.equal(second.manifest.build_id, first.manifest.build_id);
    fs.writeFileSync(path.join(build.root, 'Bees_Data', 'globalgamemanagers'), 'changed');
    const changed = buildManifest('linux-x64', build.root, null, 'v1');
    assert.notEqual(changed.manifest.build_id, first.manifest.build_id);
});

test('control configuration requires a Linux equivalent when the central trainer is Windows', t => {
    const build = tempBuild(t, 'Bees.exe');
    const token = tokenFile(t);
    assert.throws(() => buildTrainerControlConfig({
        BEES_RL_TRAINER_CONTROL: '1',
        BEES_RL_CONTROL_TOKEN_FILE: token,
        BEES_RL_TRAINING_ENV: build.executable,
        BEES_RL_TRAINER_PLATFORM: 'windows-x64',
    }), /BEES_RL_LINUX_TRAINER_BUILD_DIR/);
});

test('training config revision changes with centrally controlled environment arguments', () => {
    const first = trainingConfigIdentity({
        BEES_RL_NUM_ENVS: '0',
        BEES_RL_CLUSTER_ENV_ARGS_JSON: '["--rl-map-size=64"]',
    });
    const second = trainingConfigIdentity({
        BEES_RL_NUM_ENVS: '0',
        BEES_RL_CLUSTER_ENV_ARGS_JSON: '["--rl-map-size=128"]',
    });
    assert.notEqual(first.revision, second.revision);
    assert.deepEqual(second.environmentArgs, ['--rl-map-size=128']);
});

test('trainer control heartbeats return desired state and serve the exact build', async t => {
    const build = tempBuild(t);
    const tokenPath = tokenFile(t);
    const token = fs.readFileSync(tokenPath, 'utf8').trim();
    const config = buildTrainerControlConfig({
        BEES_RL_TRAINER_CONTROL: '1',
        BEES_RL_CONTROL_TOKEN_FILE: tokenPath,
        BEES_RL_CONTROL_PORT: '0',
        BEES_RL_CONTROL_LEASE_SECONDS: '15',
        BEES_RL_TRAINING_ENV: build.executable,
        BEES_RL_TRAINER_PLATFORM: 'linux-x64',
        BEES_RL_GAME_BUILD_VERSION: 'test-build',
        BEES_RL_CLUSTER_ENV_ARGS_JSON: '["--rl-map-size=64"]',
    }, { allowPortZero: true });
    const host = {};
    const control = installTrainerControl(host, { config, serverEpoch: 'epoch-test' });
    t.after(() => control.stop());
    if (!control.server.listening) await new Promise(resolve => control.server.once('listening', resolve));
    const port = control.server.address().port;

    const heartbeat = await request(port, token, 'POST', '/v1/trainers/heartbeat', {
        node_id: 'trainer-1',
        role: 'rollout',
        platform: 'linux-x64',
        state: 'idle',
        actor_id: 2,
        env_count: 32,
    });
    assert.equal(heartbeat.status, 200);
    const desired = JSON.parse(heartbeat.body.toString('utf8'));
    assert.equal(desired.server_epoch, 'epoch-test');
    assert.equal(desired.lease_seconds, 15);
    assert.equal(desired.desired.training_enabled, true);
    assert.deepEqual(desired.desired.environment_args, ['--rl-map-size=64']);
    assert.equal(desired.desired.build.build_id, config.builds.get('linux-x64').manifest.build_id);

    const manifestResponse = await request(port, token, 'GET', '/v1/trainers/build/manifest?platform=linux-x64');
    assert.equal(manifestResponse.status, 200);
    const manifest = JSON.parse(manifestResponse.body.toString('utf8'));
    const file = manifest.files.find(item => item.path === 'Bees.x86_64');
    assert.ok(file);

    const fileResponse = await request(
        port,
        token,
        'GET',
        `/v1/trainers/build/file?platform=linux-x64&path=${encodeURIComponent(file.path)}`,
    );
    assert.equal(fileResponse.status, 200);
    assert.equal(fileResponse.body.toString('utf8'), 'trainer-binary');
    assert.equal(fileResponse.headers['x-bees-sha256'], file.sha256);

    const status = await request(port, token, 'GET', '/v1/trainers/status');
    const statusBody = JSON.parse(status.body.toString('utf8'));
    assert.equal(statusBody.trainers.length, 1);
    assert.equal(statusBody.trainers[0].nodeId, 'trainer-1');
});
