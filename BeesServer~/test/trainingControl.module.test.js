'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { TrainingControlStore, createTrainingControlHandler } = require('../trainingControl');

function withTempDir(work) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-training-control-'));
    let result;
    try {
        result = work(root);
    } catch (error) {
        fs.rmSync(root, { recursive: true, force: true });
        throw error;
    }
    if (result && typeof result.then === 'function') {
        return result.finally(() => fs.rmSync(root, { recursive: true, force: true }));
    }
    fs.rmSync(root, { recursive: true, force: true });
    return result;
}


function invokeGet(handler, url, token) {
    return new Promise(resolve => {
        const request = {
            method: 'GET',
            url,
            headers: { authorization: 'Bearer ' + token },
        };
        const response = {
            statusCode: null,
            headers: null,
            writeHead(statusCode, headers) {
                this.statusCode = statusCode;
                this.headers = headers;
            },
            end(body) {
                resolve({
                    statusCode: this.statusCode,
                    body: body ? JSON.parse(Buffer.from(body).toString('utf8')) : null,
                });
            },
        };
        handler(request, response);
    });
}

test('worker token cannot invoke admin endpoints and admin token can inspect status', async () => {
    await withTempDir(async root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const handler = createTrainingControlHandler(store, 'worker-secret', 'admin-secret');

        const rejected = await invokeGet(handler, '/v1/admin/state', 'worker-secret');
        assert.equal(rejected.statusCode, 401);

        const status = await invokeGet(handler, '/v1/status', 'admin-secret');
        assert.equal(status.statusCode, 200);
        assert.equal(status.body.desired.schema_version, 1);
    });
});

test('desired state is persisted and maps stop to inference for full games only', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const store = new TrainingControlStore({ statePath, artifactRoot: path.join(root, 'artifacts') });

        assert.equal(store.stateFor({
            trainerId: 'dedicated-1', role: 'dedicated', platform: 'WindowsPlayer',
        }).desired_mode, 'stopped');
        assert.equal(store.stateFor({
            trainerId: 'game-1', role: 'full-game', platform: 'WindowsPlayer',
        }).desired_mode, 'inference');

        const updated = store.setDesiredState({
            training_enabled: true,
            environment_args: ['--rl-map-size', '64'],
            canonical_build_id: 'build-1',
        });
        assert.equal(updated.revision, 1);
        assert.equal(updated.training_enabled, true);
        assert.equal(updated.canonical_build_id, 'build-1');

        const reloaded = new TrainingControlStore({
            statePath,
            artifactRoot: path.join(root, 'artifacts'),
        });
        const desired = reloaded.stateFor({
            trainerId: 'dedicated-1', role: 'dedicated', platform: 'WindowsPlayer',
        });
        assert.equal(desired.desired_mode, 'training');
        assert.deepEqual(desired.environment_args, ['--rl-map-size', '64']);
        assert.equal(desired.revision, 1);
        assert.equal(desired.canonical_build_id, 'build-1');
    });
});

test('heartbeats retain machine-readable lease status', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        store.heartbeat({
            trainer_id: 'trainer-a',
            role: 'dedicated',
            platform: 'LinuxPlayer',
            process_state: 'running',
            applied_revision: 4,
            build_id: 'build-4',
            build_sha256: 'a'.repeat(64),
        });
        assert.equal(store.status().trainers[0].stale, false);
        now += 10001;
        assert.equal(store.status().trainers[0].stale, true);
    });
});

test('publishing a build copies and hashes a server-owned canonical artifact', () => {
    withTempDir(root => {
        const source = path.join(root, 'source.zip');
        const bytes = Buffer.from('canonical-bees-build');
        fs.writeFileSync(source, bytes);
        const expectedSha = crypto.createHash('sha256').update(bytes).digest('hex');

        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const descriptor = store.publishArtifact({
            platform: 'LinuxPlayer',
            buildId: 'build-123',
            archivePath: source,
            entrypoint: 'Bees.x86_64',
        });

        assert.equal(descriptor.archive_sha256, expectedSha);
        assert.equal(descriptor.archive_size_bytes, bytes.length);
        const owned = store.artifact('LinuxPlayer', 'build-123');
        assert.notEqual(path.resolve(source), owned.archive_path);
        assert.equal(fs.readFileSync(owned.archive_path).toString('utf8'), bytes.toString('utf8'));
        assert.equal(store.state.revision, 0);

        store.setDesiredState({ canonical_build_id: 'build-123' });
        const active = store.stateFor({
            trainerId: 'linux-1', role: 'dedicated', platform: 'LinuxPlayer',
        });
        assert.equal(active.build.build_id, 'build-123');
        assert.equal(active.canonical_build_id, 'build-123');
        assert.equal(store.state.revision, 1);
    });
});

test('publishing an identical canonical build is idempotent', () => {
    withTempDir(root => {
        const source = path.join(root, 'source.zip');
        fs.writeFileSync(source, Buffer.from('same-build'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const input = {
            platform: 'WindowsPlayer',
            buildId: 'build-1',
            archivePath: source,
            entrypoint: 'Bees.exe',
        };
        store.publishArtifact(input);
        const revision = store.state.revision;
        store.publishArtifact(input);
        assert.equal(store.state.revision, revision);
    });
});


test('one canonical build id selects equivalent platform artifacts and hides mismatches', () => {
    withTempDir(root => {
        const windows = path.join(root, 'windows.zip');
        const linux = path.join(root, 'linux.zip');
        fs.writeFileSync(windows, Buffer.from('windows-build'));
        fs.writeFileSync(linux, Buffer.from('linux-build'));

        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        store.publishArtifact({
            platform: 'WindowsPlayer',
            buildId: 'release-42',
            archivePath: windows,
            entrypoint: 'Bees.exe',
        });
        store.publishArtifact({
            platform: 'LinuxPlayer',
            buildId: 'release-42',
            archivePath: linux,
            entrypoint: 'Bees.x86_64',
        });
        store.publishArtifact({
            platform: 'WindowsPlayer',
            buildId: 'release-43',
            archivePath: windows,
            entrypoint: 'Bees.exe',
        });

        store.setDesiredState({ canonical_build_id: 'release-42', training_enabled: true });

        assert.equal(store.stateFor({
            trainerId: 'win', role: 'dedicated', platform: 'WindowsPlayer',
        }).build.build_id, 'release-42');
        assert.equal(store.stateFor({
            trainerId: 'linux', role: 'dedicated', platform: 'LinuxPlayer',
        }).build.build_id, 'release-42');
        assert.equal(store.stateFor({
            trainerId: 'mac', role: 'dedicated', platform: 'MacPlayer',
        }).build, null);
    });
});

test('a platform/build identity cannot be silently replaced with different bytes', () => {
    withTempDir(root => {
        const first = path.join(root, 'first.zip');
        const second = path.join(root, 'second.zip');
        fs.writeFileSync(first, Buffer.from('first'));
        fs.writeFileSync(second, Buffer.from('second'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        store.publishArtifact({
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: first,
            entrypoint: 'Bees.exe',
        });
        assert.throws(() => store.publishArtifact({
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: second,
            entrypoint: 'Bees.exe',
        }), /immutable/);
    });
});
