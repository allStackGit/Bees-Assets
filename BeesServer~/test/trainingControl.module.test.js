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


function publishDedicatedBuild(store, root, buildId) {
    const archive = path.join(root, buildId + '.zip');
    fs.writeFileSync(archive, Buffer.from(buildId + '-build'));
    store.publishArtifact({
        role: 'dedicated',
        platform: 'WindowsPlayer',
        buildId,
        archivePath: archive,
        entrypoint: 'Bees.exe',
    });
    return store.artifact('dedicated', 'WindowsPlayer', buildId).archive_sha256;
}

function activateTestRelease(store, buildId, runId = 'run-' + buildId, key = 'a'.repeat(64)) {
    return store.stageRelease({
        buildId,
        runId,
        compatibilityKey: key,
        incompatible: false,
    });
}

function heartbeatDedicated(store, trainerId, buildId, buildSha256, options = {}) {
    return store.heartbeat({
        trainer_id: trainerId,
        role: 'dedicated',
        platform: 'WindowsPlayer',
        process_state: options.processState || 'running',
        build_id: buildId,
        build_sha256: buildSha256,
        prepared_build_id: options.preparedBuildId || '',
        preparation_error: options.preparationError || '',
        applied_revision: options.appliedRevision === undefined
            ? store.state.revision
            : options.appliedRevision,
        last_error: options.lastError || '',
    });
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
        assert.equal(status.body.desired.schema_version, 5);
    });
});

test('desired state is persisted and maps stop to inference for full games only', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const store = new TrainingControlStore({ statePath, artifactRoot: path.join(root, 'artifacts') });
        const archive = path.join(root, 'windows.zip');
        fs.writeFileSync(archive, Buffer.from('windows-build'));
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'build-1',
            archivePath: archive,
            entrypoint: 'Bees.exe',
        });

        assert.equal(store.stateFor({
            trainerId: 'dedicated-1', role: 'dedicated', platform: 'WindowsPlayer',
        }).desired_mode, 'stopped');
        assert.equal(store.stateFor({
            trainerId: 'game-1', role: 'full-game', platform: 'WindowsPlayer',
        }).desired_mode, 'inference');

        activateTestRelease(store, 'build-1');
        const updated = store.setDesiredState({
            training_enabled: true,
            environment_args: ['--rl-map-size', '64'],
        });
        assert.equal(updated.revision, 3);
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
        assert.equal(desired.revision, 3);
        assert.equal(desired.canonical_build_id, 'build-1');
    });
});

test('default training-control lease tolerates transient control outages', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const desired = store.stateFor({
            trainerId: 'remote-a', role: 'dedicated', platform: 'LinuxPlayer',
        });
        assert.equal(desired.lease_seconds, 60);
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


test('training refuses to start while an active platform lacks the canonical build', () => {
    withTempDir(root => {
        const windows = path.join(root, 'windows.zip');
        fs.writeFileSync(windows, Buffer.from('windows-build'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: windows,
            entrypoint: 'Bees.exe',
        });
        activateTestRelease(store, 'release-1');
        store.heartbeat({
            trainer_id: 'linux-1',
            role: 'dedicated',
            platform: 'LinuxPlayer',
            process_state: 'stopped',
            applied_revision: 1,
        });

        assert.throws(
            () => store.setDesiredState({ training_enabled: true }),
            /missing active dedicated role\/platform artifacts: dedicated:LinuxPlayer/);
        assert.equal(store.state.training_enabled, false);
    });
});

test('reloading control state rejects a tampered canonical artifact', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const source = path.join(root, 'windows.zip');
        fs.writeFileSync(source, Buffer.from('original-build'));
        const store = new TrainingControlStore({ statePath, artifactRoot });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: source,
            entrypoint: 'Bees.exe',
        });
        activateTestRelease(store, 'release-1');

        const owned = store.artifact('dedicated', 'WindowsPlayer', 'release-1');
        fs.writeFileSync(owned.archive_path, Buffer.from('tampered-build'));

        assert.throws(
            () => new TrainingControlStore({ statePath, artifactRoot }),
            /canonical artifact/);
    });
});

test('canonical build activation is release-owned and requires a published artifact', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        assert.throws(
            () => store.setDesiredState({ canonical_build_id: 'missing-build' }),
            error => error.statusCode === 409 && /release-owned/.test(error.message),
        );
        assert.throws(
            () => store.stageRelease({
                buildId: 'missing-build',
                runId: 'missing-run',
                compatibilityKey: 'a'.repeat(64),
                incompatible: false,
            }),
            /no published platform artifact/,
        );
        assert.equal(store.state.canonical_build_id, '');
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
            role: 'dedicated',
            platform: 'LinuxPlayer',
            buildId: 'build-123',
            archivePath: source,
            entrypoint: 'Bees.x86_64',
        });

        assert.equal(descriptor.archive_sha256, expectedSha);
        assert.equal(descriptor.archive_size_bytes, bytes.length);
        const owned = store.artifact('dedicated', 'LinuxPlayer', 'build-123');
        assert.notEqual(path.resolve(source), owned.archive_path);
        assert.equal(fs.readFileSync(owned.archive_path).toString('utf8'), bytes.toString('utf8'));
        assert.equal(store.state.revision, 0);

        activateTestRelease(store, 'build-123');
        const active = store.stateFor({
            trainerId: 'linux-1', role: 'dedicated', platform: 'LinuxPlayer',
        });
        assert.equal(active.build.build_id, 'build-123');
        assert.equal(active.canonical_build_id, 'build-123');
        assert.equal(store.state.revision, 2);
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
            role: 'dedicated',
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
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-42',
            archivePath: windows,
            entrypoint: 'Bees.exe',
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'LinuxPlayer',
            buildId: 'release-42',
            archivePath: linux,
            entrypoint: 'Bees.x86_64',
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-43',
            archivePath: windows,
            entrypoint: 'Bees.exe',
        });

        activateTestRelease(store, 'release-42');
        store.setDesiredState({ training_enabled: true });

        assert.equal(store.stateFor({
            trainerId: 'win', role: 'dedicated', platform: 'WindowsPlayer',
        }).build.build_id, 'release-42');
        assert.equal(store.stateFor({
            trainerId: 'linux', role: 'dedicated', platform: 'LinuxPlayer',
        }).build.build_id, 'release-42');
        const missingDedicated = store.stateFor({
            trainerId: 'mac', role: 'dedicated', platform: 'MacPlayer',
        });
        assert.equal(missingDedicated.build, null);
        assert.equal(missingDedicated.desired_mode, 'stopped');

        const missingFullGame = store.stateFor({
            trainerId: 'mac-game', role: 'full-game', platform: 'MacPlayer',
        });
        assert.equal(missingFullGame.build, null);
        assert.equal(missingFullGame.desired_mode, 'inference');
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
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: first,
            entrypoint: 'Bees.exe',
        });
        assert.throws(() => store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-1',
            archivePath: second,
            entrypoint: 'Bees.exe',
        }), /immutable/);
    });
});

test('role-specific Windows builds can share one canonical build id', () => {
    withTempDir(root => {
        const training = path.join(root, 'training.zip');
        const game = path.join(root, 'game.zip');
        fs.writeFileSync(training, Buffer.from('windows-training'));
        fs.writeFileSync(game, Buffer.from('windows-game'));

        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-role-aware',
            archivePath: training,
            entrypoint: 'Bees RL Training.exe',
        });
        store.publishArtifact({
            role: 'full-game',
            platform: 'WindowsPlayer',
            buildId: 'release-role-aware',
            archivePath: game,
            entrypoint: 'Bees.exe',
        });
        activateTestRelease(store, 'release-role-aware');
        store.setDesiredState({ training_enabled: true });

        const dedicated = store.stateFor({
            trainerId: 'trainer', role: 'dedicated', platform: 'WindowsPlayer',
        });
        const fullGame = store.stateFor({
            trainerId: 'game', role: 'full-game', platform: 'WindowsPlayer',
        });
        assert.equal(dedicated.build.role, 'dedicated');
        assert.equal(fullGame.build.role, 'full-game');
        assert.notEqual(dedicated.build.archive_sha256, fullGame.build.archive_sha256);
        assert.match(dedicated.build.artifact_url, /\/dedicated\/WindowsPlayer\//);
        assert.match(fullGame.build.artifact_url, /\/full-game\/WindowsPlayer\//);
    });
});

test('schema 2 build catalogs migrate to both roles without breaking an existing deployment', () => {
    withTempDir(root => {
        const artifactRoot = path.join(root, 'artifacts');
        const statePath = path.join(root, 'state.json');
        fs.mkdirSync(path.join(artifactRoot, 'WindowsPlayer'), { recursive: true });
        const artifact = path.join(artifactRoot, 'WindowsPlayer', 'legacy.zip');
        fs.writeFileSync(artifact, Buffer.from('legacy-build'));
        const sha = crypto.createHash('sha256').update(Buffer.from('legacy-build')).digest('hex');
        fs.writeFileSync(statePath, JSON.stringify({
            schema_version: 2,
            revision: 7,
            training_enabled: true,
            environment_args: ['--rl-map-size', '64'],
            canonical_build_id: 'legacy',
            builds: {
                WindowsPlayer: {
                    legacy: {
                        platform: 'WindowsPlayer',
                        build_id: 'legacy',
                        archive_path: artifact,
                        archive_sha256: sha,
                        archive_size_bytes: Buffer.byteLength('legacy-build'),
                        entrypoint: 'Bees.exe',
                    },
                },
            },
        }));

        const store = new TrainingControlStore({ statePath, artifactRoot });
        assert.equal(store.state.schema_version, 5);
        assert.equal(store.stateFor({
            trainerId: 'trainer', role: 'dedicated', platform: 'WindowsPlayer',
        }).build.role, 'dedicated');
        assert.equal(store.stateFor({
            trainerId: 'game', role: 'full-game', platform: 'WindowsPlayer',
        }).build.role, 'full-game');
    });
});

test('missing optional full-game artifact does not block dedicated training', () => {
    withTempDir(root => {
        const windows = path.join(root, 'windows.zip');
        fs.writeFileSync(windows, Buffer.from('windows-training'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'WindowsPlayer',
            buildId: 'release-optional-game',
            archivePath: windows,
            entrypoint: 'Bees RL Training.exe',
        });
        activateTestRelease(store, 'release-optional-game');
        store.heartbeat({
            trainer_id: 'trainer',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'stopped',
            applied_revision: 1,
        });
        store.heartbeat({
            trainer_id: 'game',
            role: 'full-game',
            platform: 'WindowsPlayer',
            process_state: 'running',
            applied_revision: 1,
        });

        store.setDesiredState({ training_enabled: true });
        assert.equal(store.stateFor({
            trainerId: 'trainer', role: 'dedicated', platform: 'WindowsPlayer',
        }).desired_mode, 'training');
        assert.equal(store.stateFor({
            trainerId: 'game', role: 'full-game', platform: 'WindowsPlayer',
        }).desired_mode, 'inference');
    });
});


test('compatible release prestages everywhere and rolls one dedicated trainer at a time', () => {
    withTempDir(root => {
        const artifactRoot = path.join(root, 'artifacts');
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot,
        });
        const oldArchive = path.join(root, 'old.zip');
        const newArchive = path.join(root, 'new.zip');
        fs.writeFileSync(oldArchive, Buffer.from('old-build'));
        fs.writeFileSync(newArchive, Buffer.from('new-build'));
        for (const [buildId, archive] of [['old', oldArchive], ['new', newArchive]]) {
            store.publishArtifact({
                role: 'dedicated',
                platform: 'WindowsPlayer',
                buildId,
                archivePath: archive,
                entrypoint: 'Bees.exe',
            });
        }
        const oldSha = store.artifact(
            'dedicated', 'WindowsPlayer', 'old').archive_sha256;
        const newSha = store.artifact(
            'dedicated', 'WindowsPlayer', 'new').archive_sha256;

        const key = 'a'.repeat(64);
        store.stageRelease({
            buildId: 'old',
            runId: 'run-a',
            compatibilityKey: key,
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });

        store.heartbeat({
            trainer_id: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: '',
            applied_revision: store.state.revision,
        });
        store.heartbeat({
            trainer_id: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: '',
            applied_revision: store.state.revision,
        });

        let staged = store.stageRelease({
            buildId: 'new',
            runId: 'run-a',
            compatibilityKey: key,
            incompatible: false,
        });
        assert.equal(staged.pending_release.phase, 'preparing');
        assert.equal(store.stateFor({
            trainerId: 'remote-a', role: 'dedicated', platform: 'WindowsPlayer',
        }).build.build_id, 'old');

        store.heartbeat({
            trainer_id: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.revision,
        });
        const centralPreparation = store.heartbeat({
            trainer_id: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.revision,
        });
        assert.equal(centralPreparation.pending_release.phase, 'rolling');

        const remoteTurn = store.stateFor({
            trainerId: 'remote-a', role: 'dedicated', platform: 'WindowsPlayer',
        });
        assert.equal(remoteTurn.desired_build_id, 'new');
        assert.equal(remoteTurn.build.build_id, 'new');

        const afterRemote = store.heartbeat({
            trainer_id: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'new',
            build_sha256: newSha,
            prepared_build_id: 'new',
            applied_revision: store.state.pending_release.phase_revision,
        });
        assert.equal(afterRemote.desired_build_id, 'new');
        assert.equal(store.state.canonical_build_id, 'old');

        const centralTurn = store.stateFor({
            trainerId: 'central-learner', role: 'dedicated', platform: 'WindowsPlayer',
        });
        assert.equal(centralTurn.desired_build_id, 'new');

        store.heartbeat({
            trainer_id: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'new',
            build_sha256: newSha,
            prepared_build_id: 'new',
            applied_revision: store.state.pending_release.phase_revision,
        });
        assert.equal(store.state.canonical_build_id, 'new');
        assert.equal(store.state.run_id, 'run-a');
        assert.equal(store.state.pending_release, null);
    });
});

test('compatible preparing drops a trainer after its dedicated lease expires', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'stale-prepare-old');
        publishDedicatedBuild(store, root, 'stale-prepare-new');
        store.stageRelease({
            buildId: 'stale-prepare-old',
            runId: 'stale-prepare-run',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stale-prepare-old', oldSha);
        }
        store.stageRelease({
            buildId: 'stale-prepare-new',
            runId: 'stale-prepare-run',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'stale-prepare-old',
            oldSha,
            { preparedBuildId: 'stale-prepare-new' },
        );

        now = 9000;
        heartbeatDedicated(
            store,
            'central-learner',
            'stale-prepare-old',
            oldSha,
            { preparedBuildId: 'stale-prepare-new' },
        );
        now = 11001;
        const desired = store.status().desired;

        assert.equal(desired.pending_release.phase, 'rolling');
        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['central-learner'],
        );
    });
});

test('compatible rolling skips an expired target and advances to the next live trainer', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'stale-roll-old');
        publishDedicatedBuild(store, root, 'stale-roll-new');
        store.stageRelease({
            buildId: 'stale-roll-old',
            runId: 'stale-roll-run',
            compatibilityKey: 'b'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stale-roll-old', oldSha);
        }
        store.stageRelease({
            buildId: 'stale-roll-new',
            runId: 'stale-roll-run',
            compatibilityKey: 'b'.repeat(64),
            incompatible: false,
        });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stale-roll-old',
                oldSha,
                { preparedBuildId: 'stale-roll-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'rolling');
        assert.equal(store._rollingTargetId(), 'remote-a');

        now = 9000;
        for (const trainerId of ['remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stale-roll-old',
                oldSha,
                { preparedBuildId: 'stale-roll-new' },
            );
        }
        now = 11001;
        const desired = store.status().desired;

        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-b', 'central-learner'],
        );
        assert.equal(store._rollingTargetId(), 'remote-b');
        assert.equal(
            store.stateFor({
                trainerId: 'remote-b',
                role: 'dedicated',
                platform: 'WindowsPlayer',
            }).desired_build_id,
            'stale-roll-new',
        );
    });
});

test('compatible rollout does not re-add a pruned trainer that reconnects mid-rollout', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'rejoin-old');
        publishDedicatedBuild(store, root, 'rejoin-new');
        store.stageRelease({
            buildId: 'rejoin-old',
            runId: 'rejoin-run',
            compatibilityKey: '9'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'rejoin-old', oldSha);
        }
        store.stageRelease({
            buildId: 'rejoin-new',
            runId: 'rejoin-run',
            compatibilityKey: '9'.repeat(64),
            incompatible: false,
        });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'rejoin-old',
                oldSha,
                { preparedBuildId: 'rejoin-new' },
            );
        }

        now = 9000;
        for (const trainerId of ['remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'rejoin-old',
                oldSha,
                { preparedBuildId: 'rejoin-new' },
            );
        }
        now = 11001;
        store.status();
        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-b', 'central-learner'],
        );

        // The expired worker can reconnect and keep using the semantically compatible old
        // canonical release, but it must not make the in-flight barrier grow again.
        const rejoined = heartbeatDedicated(store, 'remote-a', 'rejoin-old', oldSha);
        assert.equal(rejoined.desired_build_id, 'rejoin-old');
        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-b', 'central-learner'],
        );
    });
});

test('late trainer does not expand a compatible rollout snapshot', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'late-compatible-old');
        publishDedicatedBuild(store, root, 'late-compatible-new');
        store.stageRelease({
            buildId: 'late-compatible-old',
            runId: 'late-compatible-run',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'central-learner', 'late-compatible-old', oldSha);
        store.stageRelease({
            buildId: 'late-compatible-new',
            runId: 'late-compatible-run',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });

        heartbeatDedicated(
            store,
            'remote-late',
            'late-compatible-old',
            oldSha,
            { preparedBuildId: 'late-compatible-new' },
        );

        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['central-learner'],
        );
    });
});

test('late trainer still joins an incompatible stop barrier', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'late-incompatible-old');
        publishDedicatedBuild(store, root, 'late-incompatible-new');
        store.stageRelease({
            buildId: 'late-incompatible-old',
            runId: 'late-incompatible-old-run',
            compatibilityKey: 'b'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'central-learner', 'late-incompatible-old', oldSha);
        store.stageRelease({
            buildId: 'late-incompatible-new',
            runId: 'late-incompatible-new-run',
            compatibilityKey: 'c'.repeat(64),
            incompatible: true,
        });

        heartbeatDedicated(
            store,
            'remote-late',
            'late-incompatible-old',
            oldSha,
            { preparedBuildId: 'late-incompatible-new' },
        );

        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-late', 'central-learner'],
        );
    });
});

test('compatible rollout converges across worker loss, server restart, and worker rejoin', () => {
    withTempDir(root => {
        let now = 1000;
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = {
            statePath,
            artifactRoot,
            leaseSeconds: 10,
            now: () => now,
        };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'resilience-old');
        const newSha = publishDedicatedBuild(store, root, 'resilience-new');

        store.stageRelease({
            buildId: 'resilience-old',
            runId: 'resilience-run',
            compatibilityKey: 'd'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'resilience-old', oldSha);
        }

        store.stageRelease({
            buildId: 'resilience-new',
            runId: 'resilience-run',
            compatibilityKey: 'd'.repeat(64),
            incompatible: false,
        });
        for (const trainerId of ['remote-a', 'remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'resilience-old',
                oldSha,
                { preparedBuildId: 'resilience-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'rolling');
        assert.equal(store._rollingTargetId(), 'remote-a');

        // remote-a disappears. The healthy members keep their leases while it expires.
        now = 9000;
        for (const trainerId of ['remote-b', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'resilience-old',
                oldSha,
                { preparedBuildId: 'resilience-new' },
            );
        }
        now = 11001;
        store.status();
        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-b', 'central-learner'],
        );
        assert.equal(store._rollingTargetId(), 'remote-b');

        heartbeatDedicated(
            store,
            'remote-b',
            'resilience-new',
            newSha,
            {
                preparedBuildId: 'resilience-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        assert.equal(store._rollingTargetId(), 'central-learner');

        // BeesServer dies/restarts after one remote has rolled. Persisted state must not promote
        // the release until the surviving trainers re-register healthy.
        now = 12000;
        store = new TrainingControlStore(options);
        assert.equal(store.state.canonical_build_id, 'resilience-old');
        assert.equal(store.state.pending_release.phase, 'rolling');

        heartbeatDedicated(
            store,
            'remote-b',
            'resilience-new',
            newSha,
            {
                preparedBuildId: 'resilience-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        let centralDesired = heartbeatDedicated(
            store,
            'central-learner',
            'resilience-old',
            oldSha,
            { preparedBuildId: 'resilience-new' },
        );
        assert.equal(centralDesired.desired_build_id, 'resilience-new');

        heartbeatDedicated(
            store,
            'central-learner',
            'resilience-new',
            newSha,
            {
                preparedBuildId: 'resilience-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'resilience-new');
        assert.equal(store.state.pending_release, null);

        // The machine that was absent during promotion rejoins on the old compatible build.
        // It must be told to converge to canonical without reopening the completed rollout.
        const rejoined = heartbeatDedicated(
            store,
            'remote-a',
            'resilience-old',
            oldSha,
        );
        assert.equal(rejoined.desired_build_id, 'resilience-new');
        assert.equal(rejoined.pending_release, null);
        heartbeatDedicated(store, 'remote-a', 'resilience-new', newSha);
        const remoteA = store.status().trainers.find(
            trainer => trainer.trainer_id === 'remote-a');
        assert.equal(remoteA.build_id, 'resilience-new');
        assert.equal(remoteA.stale, false);
    });
});

test('disabling training during compatible rolling promotes the fully prepared release', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'stop-compatible-old');
        const newSha = publishDedicatedBuild(store, root, 'stop-compatible-new');

        store.stageRelease({
            buildId: 'stop-compatible-old',
            runId: 'stop-compatible-run',
            compatibilityKey: 'e'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stop-compatible-old', oldSha);
        }

        store.stageRelease({
            buildId: 'stop-compatible-new',
            runId: 'stop-compatible-run',
            compatibilityKey: 'e'.repeat(64),
            incompatible: false,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stop-compatible-old',
                oldSha,
                { preparedBuildId: 'stop-compatible-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'rolling');

        const desired = store.setDesiredState({ training_enabled: false });

        assert.equal(desired.training_enabled, false);
        assert.equal(desired.canonical_build_id, 'stop-compatible-new');
        assert.equal(desired.run_id, 'stop-compatible-run');
        assert.equal(desired.pending_release, null);
        const remote = store.stateFor({
            trainerId: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        });
        assert.equal(remote.desired_mode, 'stopped');
        assert.equal(remote.desired_build_id, 'stop-compatible-new');
    });
});

test('disabling training never bypasses an incompatible stopping barrier', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'stop-incompatible-old');
        publishDedicatedBuild(store, root, 'stop-incompatible-new');

        store.stageRelease({
            buildId: 'stop-incompatible-old',
            runId: 'stop-incompatible-old-run',
            compatibilityKey: 'f'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stop-incompatible-old', oldSha);
        }
        store.stageRelease({
            buildId: 'stop-incompatible-new',
            runId: 'stop-incompatible-new-run',
            compatibilityKey: '0'.repeat(64),
            incompatible: true,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stop-incompatible-old',
                oldSha,
                { preparedBuildId: 'stop-incompatible-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');

        const desired = store.setDesiredState({ training_enabled: false });

        assert.equal(desired.training_enabled, false);
        assert.equal(desired.canonical_build_id, 'stop-incompatible-old');
        assert.equal(desired.run_id, 'stop-incompatible-old-run');
        assert.equal(desired.pending_release.phase, 'stopping');
    });
});

test('incompatible release promotes environment args atomically with run identity across restart', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = { statePath, artifactRoot, leaseSeconds: 20 };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'atomic-env-old');
        publishDedicatedBuild(store, root, 'atomic-env-new');

        store.stageRelease({
            buildId: 'atomic-env-old',
            runId: 'atomic-env-old-run',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({
            training_enabled: true,
            environment_args: ['--rl-map-size=32', '--rl-health-ratio=.25'],
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'atomic-env-old', oldSha);
        }

        const targetArgs = [
            '--rl-map-size-min=48',
            '--rl-map-size-max=64',
            '--rl-health-ratio=.05',
        ];
        store.stageRelease({
            buildId: 'atomic-env-new',
            runId: 'atomic-env-new-run',
            compatibilityKey: 'b'.repeat(64),
            incompatible: true,
            environmentArgs: targetArgs,
        });

        assert.deepEqual(
            store.state.pending_release.environment_args,
            targetArgs,
        );
        assert.deepEqual(
            store.state.environment_args,
            ['--rl-map-size=32', '--rl-health-ratio=.25'],
        );
        let central = store.stateFor({
            trainerId: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        });
        assert.equal(central.run_id, 'atomic-env-old-run');
        assert.deepEqual(
            central.environment_args,
            ['--rl-map-size=32', '--rl-health-ratio=.25'],
        );

        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'atomic-env-old',
                oldSha,
                { preparedBuildId: 'atomic-env-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');
        assert.equal(store.state.run_id, 'atomic-env-old-run');
        assert.deepEqual(
            store.state.environment_args,
            ['--rl-map-size=32', '--rl-health-ratio=.25'],
        );

        // The pending target survives a control-server restart without becoming current early.
        store = new TrainingControlStore(options);
        assert.equal(store.state.pending_release.phase, 'stopping');
        assert.deepEqual(store.state.pending_release.environment_args, targetArgs);
        assert.equal(store.state.run_id, 'atomic-env-old-run');
        assert.deepEqual(
            store.state.environment_args,
            ['--rl-map-size=32', '--rl-health-ratio=.25'],
        );

        const stoppingRevision = store.state.pending_release.phase_revision;
        heartbeatDedicated(
            store,
            'remote-a',
            'atomic-env-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'atomic-env-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.run_id, 'atomic-env-old-run');
        assert.deepEqual(
            store.state.environment_args,
            ['--rl-map-size=32', '--rl-health-ratio=.25'],
        );

        heartbeatDedicated(
            store,
            'central-learner',
            'atomic-env-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'atomic-env-new',
                appliedRevision: stoppingRevision,
            },
        );

        assert.equal(store.state.pending_release, null);
        assert.equal(store.state.canonical_build_id, 'atomic-env-new');
        assert.equal(store.state.run_id, 'atomic-env-new-run');
        assert.equal(store.state.compatibility_key, 'b'.repeat(64));
        assert.deepEqual(store.state.environment_args, targetArgs);

        central = store.stateFor({
            trainerId: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        });
        assert.equal(central.run_id, 'atomic-env-new-run');
        assert.deepEqual(central.environment_args, targetArgs);
    });
});

test('release retry rejects environment args drift for pending or canonical identity', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'env-drift-old');
        publishDedicatedBuild(store, root, 'env-drift-new');

        store.stageRelease({
            buildId: 'env-drift-old',
            runId: 'env-drift-old-run',
            compatibilityKey: 'c'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({
            training_enabled: true,
            environment_args: ['--rl-map-size=32'],
        });
        heartbeatDedicated(store, 'central-learner', 'env-drift-old', oldSha);

        const target = {
            buildId: 'env-drift-new',
            runId: 'env-drift-new-run',
            compatibilityKey: 'd'.repeat(64),
            incompatible: true,
            environmentArgs: ['--rl-map-size=48'],
        };
        store.stageRelease(target);

        assert.throws(
            () => store.stageRelease({
                ...target,
                environmentArgs: ['--rl-map-size=64'],
            }),
            error => error.statusCode === 409 &&
                /pending release environment_args differ/.test(error.message),
        );

        heartbeatDedicated(
            store,
            'central-learner',
            'env-drift-old',
            oldSha,
            { preparedBuildId: 'env-drift-new' },
        );
        const stoppingRevision = store.state.pending_release.phase_revision;
        heartbeatDedicated(
            store,
            'central-learner',
            'env-drift-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'env-drift-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.pending_release, null);
        assert.deepEqual(store.state.environment_args, ['--rl-map-size=48']);

        assert.throws(
            () => store.stageRelease({
                ...target,
                environmentArgs: ['--rl-map-size=64'],
            }),
            error => error.statusCode === 409 &&
                /canonical release environment_args differ/.test(error.message),
        );
        assert.deepEqual(store.state.environment_args, ['--rl-map-size=48']);
    });
});

test('compatible preparing drops a persistently failing remote after grace', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 60,
            compatibleFailureGraceSeconds: 5,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'prepare-fail-old');
        publishDedicatedBuild(store, root, 'prepare-fail-new');
        store.stageRelease({
            buildId: 'prepare-fail-old',
            runId: 'prepare-fail-run',
            compatibilityKey: '1'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-bad', 'remote-good', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'prepare-fail-old', oldSha);
        }
        store.stageRelease({
            buildId: 'prepare-fail-new',
            runId: 'prepare-fail-run',
            compatibilityKey: '1'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-bad',
            'prepare-fail-old',
            oldSha,
            {
                preparationError: 'download verification failed',
                lastError: 'download verification failed',
            },
        );
        for (const trainerId of ['remote-good', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'prepare-fail-old',
                oldSha,
                { preparedBuildId: 'prepare-fail-new' },
            );
        }

        let spec = store.state.pending_release.required_trainers.find(
            item => item.trainer_id === 'remote-bad');
        assert.equal(spec.failure_since_ms, 1000);
        assert.equal(store.state.pending_release.phase, 'preparing');

        now = 6001;
        heartbeatDedicated(
            store,
            'remote-bad',
            'prepare-fail-old',
            oldSha,
            {
                preparationError: 'download verification failed',
                lastError: 'download verification failed',
            },
        );

        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-good', 'central-learner'],
        );
        assert.equal(store.state.pending_release.phase, 'rolling');
    });
});

test('compatible preparation failure grace resets when the remote recovers', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 60,
            compatibleFailureGraceSeconds: 5,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'prepare-recover-old');
        publishDedicatedBuild(store, root, 'prepare-recover-new');
        store.stageRelease({
            buildId: 'prepare-recover-old',
            runId: 'prepare-recover-run',
            compatibilityKey: '2'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-a', 'prepare-recover-old', oldSha);
        heartbeatDedicated(store, 'central-learner', 'prepare-recover-old', oldSha);
        store.stageRelease({
            buildId: 'prepare-recover-new',
            runId: 'prepare-recover-run',
            compatibilityKey: '2'.repeat(64),
            incompatible: false,
        });

        heartbeatDedicated(
            store,
            'remote-a',
            'prepare-recover-old',
            oldSha,
            {
                preparationError: 'temporary download error',
                lastError: 'temporary download error',
            },
        );
        assert.ok(Number.isFinite(
            store.state.pending_release.required_trainers[0].failure_since_ms));

        now = 4000;
        heartbeatDedicated(
            store,
            'remote-a',
            'prepare-recover-old',
            oldSha,
            { preparedBuildId: 'prepare-recover-new' },
        );
        const spec = store.state.pending_release.required_trainers.find(
            item => item.trainer_id === 'remote-a');
        assert.equal(
            Object.prototype.hasOwnProperty.call(spec, 'failure_since_ms'),
            false,
        );
        assert.equal(store.state.pending_release.phase, 'preparing');
    });
});

test('compatible preparing never bypasses a failing central learner', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 60,
            compatibleFailureGraceSeconds: 5,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'central-prepare-old');
        publishDedicatedBuild(store, root, 'central-prepare-new');
        store.stageRelease({
            buildId: 'central-prepare-old',
            runId: 'central-prepare-run',
            compatibilityKey: '5'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-a', 'central-prepare-old', oldSha);
        heartbeatDedicated(store, 'central-learner', 'central-prepare-old', oldSha);
        store.stageRelease({
            buildId: 'central-prepare-new',
            runId: 'central-prepare-run',
            compatibilityKey: '5'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'central-prepare-old',
            oldSha,
            { preparedBuildId: 'central-prepare-new' },
        );
        heartbeatDedicated(
            store,
            'central-learner',
            'central-prepare-old',
            oldSha,
            {
                preparationError: 'central runtime staging failed',
                lastError: 'central runtime staging failed',
            },
        );

        now = 20000;
        heartbeatDedicated(
            store,
            'central-learner',
            'central-prepare-old',
            oldSha,
            {
                preparationError: 'central runtime staging failed',
                lastError: 'central runtime staging failed',
            },
        );

        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-a', 'central-learner'],
        );
        assert.equal(store.state.pending_release.phase, 'preparing');
        const centralSpec = store.state.pending_release.required_trainers.find(
            item => item.trainer_id === 'central-learner');
        assert.equal(
            Object.prototype.hasOwnProperty.call(centralSpec, 'failure_since_ms'),
            false,
        );
    });
});

test('compatible rolling skips a persistently crashing remote but never central learner', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 60,
            compatibleFailureGraceSeconds: 5,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'roll-fail-old');
        const newSha = publishDedicatedBuild(store, root, 'roll-fail-new');
        store.stageRelease({
            buildId: 'roll-fail-old',
            runId: 'roll-fail-run',
            compatibilityKey: '3'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-bad', 'remote-good', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'roll-fail-old', oldSha);
        }
        store.stageRelease({
            buildId: 'roll-fail-new',
            runId: 'roll-fail-run',
            compatibilityKey: '3'.repeat(64),
            incompatible: false,
        });
        for (const trainerId of ['remote-bad', 'remote-good', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'roll-fail-old',
                oldSha,
                { preparedBuildId: 'roll-fail-new' },
            );
        }
        assert.equal(store._rollingTargetId(), 'remote-bad');

        heartbeatDedicated(
            store,
            'remote-bad',
            'roll-fail-new',
            newSha,
            {
                processState: 'stopped',
                preparedBuildId: 'roll-fail-new',
                lastError: 'managed process exited with code 2',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        now = 6001;
        heartbeatDedicated(
            store,
            'remote-bad',
            'roll-fail-new',
            newSha,
            {
                processState: 'stopped',
                preparedBuildId: 'roll-fail-new',
                lastError: 'managed process exited with code 2',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-good', 'central-learner'],
        );
        assert.equal(store._rollingTargetId(), 'remote-good');

        heartbeatDedicated(
            store,
            'remote-good',
            'roll-fail-new',
            newSha,
            {
                preparedBuildId: 'roll-fail-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        assert.equal(store._rollingTargetId(), 'central-learner');

        heartbeatDedicated(
            store,
            'central-learner',
            'roll-fail-new',
            newSha,
            {
                processState: 'stopped',
                preparedBuildId: 'roll-fail-new',
                lastError: 'central launch failed',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        now = 20000;
        heartbeatDedicated(
            store,
            'central-learner',
            'roll-fail-new',
            newSha,
            {
                processState: 'stopped',
                preparedBuildId: 'roll-fail-new',
                lastError: 'central launch failed',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );

        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-good', 'central-learner'],
        );
        assert.equal(store._rollingTargetId(), 'central-learner');
        assert.equal(store.state.canonical_build_id, 'roll-fail-old');
    });
});

test('compatible remote failure grace survives training-control restart', () => {
    withTempDir(root => {
        let now = 1000;
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = {
            statePath,
            artifactRoot,
            leaseSeconds: 60,
            compatibleFailureGraceSeconds: 5,
            now: () => now,
        };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'fail-restart-old');
        publishDedicatedBuild(store, root, 'fail-restart-new');
        store.stageRelease({
            buildId: 'fail-restart-old',
            runId: 'fail-restart-run',
            compatibilityKey: '4'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-bad', 'fail-restart-old', oldSha);
        heartbeatDedicated(store, 'central-learner', 'fail-restart-old', oldSha);
        store.stageRelease({
            buildId: 'fail-restart-new',
            runId: 'fail-restart-run',
            compatibilityKey: '4'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-bad',
            'fail-restart-old',
            oldSha,
            {
                preparationError: 'persistent staging failure',
                lastError: 'persistent staging failure',
            },
        );
        heartbeatDedicated(
            store,
            'central-learner',
            'fail-restart-old',
            oldSha,
            { preparedBuildId: 'fail-restart-new' },
        );
        const before = store.state.pending_release.required_trainers.find(
            item => item.trainer_id === 'remote-bad').failure_since_ms;
        assert.equal(before, 1000);

        store = new TrainingControlStore(options);
        now = 6001;
        heartbeatDedicated(
            store,
            'remote-bad',
            'fail-restart-old',
            oldSha,
            {
                preparationError: 'persistent staging failure',
                lastError: 'persistent staging failure',
            },
        );
        assert.deepEqual(
            store.state.pending_release.required_trainers.map(item => item.trainer_id),
            ['central-learner'],
        );
        assert.equal(store.state.pending_release.phase, 'preparing');

        // Server restart intentionally forgets live heartbeat records. The still-required
        // checkpoint owner must re-register before the barrier can advance.
        heartbeatDedicated(
            store,
            'central-learner',
            'fail-restart-old',
            oldSha,
            { preparedBuildId: 'fail-restart-new' },
        );
        assert.equal(store.state.pending_release.phase, 'rolling');
    });
});

test('incompatible rollout drops an expired remote after its fail-closed lease', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'stale-incompatible-old');
        publishDedicatedBuild(store, root, 'stale-incompatible-new');
        store.stageRelease({
            buildId: 'stale-incompatible-old',
            runId: 'stale-incompatible-old-run',
            compatibilityKey: 'c'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stale-incompatible-old', oldSha);
        }
        store.stageRelease({
            buildId: 'stale-incompatible-new',
            runId: 'stale-incompatible-new-run',
            compatibilityKey: 'd'.repeat(64),
            incompatible: true,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stale-incompatible-old',
                oldSha,
                { preparedBuildId: 'stale-incompatible-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');

        // The remote disappears. The central learner remains fresh and finishes its
        // authoritative checkpoint/stop acknowledgement before the remote lease expires.
        now = 9000;
        heartbeatDedicated(
            store,
            'central-learner',
            'stale-incompatible-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'stale-incompatible-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        now = 11001;
        const desired = store.status().desired;

        assert.equal(desired.pending_release, null);
        assert.equal(desired.canonical_build_id, 'stale-incompatible-new');
        assert.equal(desired.run_id, 'stale-incompatible-new-run');
    });
});

test('incompatible rollout never bypasses an expired central learner', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'stale-central-old');
        publishDedicatedBuild(store, root, 'stale-central-new');
        store.stageRelease({
            buildId: 'stale-central-old',
            runId: 'stale-central-old-run',
            compatibilityKey: 'e'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stale-central-old', oldSha);
        }
        store.stageRelease({
            buildId: 'stale-central-new',
            runId: 'stale-central-new-run',
            compatibilityKey: 'f'.repeat(64),
            incompatible: true,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stale-central-old',
                oldSha,
                { preparedBuildId: 'stale-central-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');

        now = 9000;
        heartbeatDedicated(
            store,
            'remote-a',
            'stale-central-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'stale-central-new',
                appliedRevision: store.state.pending_release.phase_revision,
            },
        );
        now = 11001;
        const desired = store.status().desired;

        assert.equal(desired.pending_release.phase, 'stopping');
        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-a', 'central-learner'],
        );
        assert.equal(desired.canonical_build_id, 'stale-central-old');
    });
});

test('expired incompatible remote that reconnects before promotion rejoins stop barrier', () => {
    withTempDir(root => {
        let now = 1000;
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'rejoin-incompatible-old');
        publishDedicatedBuild(store, root, 'rejoin-incompatible-new');
        store.stageRelease({
            buildId: 'rejoin-incompatible-old',
            runId: 'rejoin-incompatible-old-run',
            compatibilityKey: '1'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'rejoin-incompatible-old', oldSha);
        }
        store.stageRelease({
            buildId: 'rejoin-incompatible-new',
            runId: 'rejoin-incompatible-new-run',
            compatibilityKey: '2'.repeat(64),
            incompatible: true,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'rejoin-incompatible-old',
                oldSha,
                { preparedBuildId: 'rejoin-incompatible-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');

        now = 11001;
        let desired = store.status().desired;
        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['central-learner'],
        );

        now = 12000;
        heartbeatDedicated(
            store,
            'remote-a',
            'rejoin-incompatible-old',
            oldSha,
            {
                processState: 'running',
                preparedBuildId: 'rejoin-incompatible-new',
                appliedRevision: store.state.revision,
            },
        );
        desired = store.status().desired;
        assert.equal(desired.pending_release.phase, 'stopping');
        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-a', 'central-learner'],
        );
        assert.equal(desired.canonical_build_id, 'rejoin-incompatible-old');
    });
});

test('incompatible release waits for prestaging, stops all trainers, then switches run', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldArchive = path.join(root, 'old.zip');
        const newArchive = path.join(root, 'new.zip');
        fs.writeFileSync(oldArchive, Buffer.from('old-build'));
        fs.writeFileSync(newArchive, Buffer.from('new-build'));
        for (const [buildId, archive] of [['old', oldArchive], ['new', newArchive]]) {
            store.publishArtifact({
                role: 'dedicated',
                platform: 'WindowsPlayer',
                buildId,
                archivePath: archive,
                entrypoint: 'Bees.exe',
            });
        }
        const oldSha = store.artifact(
            'dedicated', 'WindowsPlayer', 'old').archive_sha256;

        store.stageRelease({
            buildId: 'old',
            runId: 'run-old',
            compatibilityKey: 'a'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            store.heartbeat({
                trainer_id: trainerId,
                role: 'dedicated',
                platform: 'WindowsPlayer',
                process_state: 'running',
                build_id: 'old',
                build_sha256: oldSha,
                prepared_build_id: '',
                applied_revision: store.state.revision,
            });
        }

        store.stageRelease({
            buildId: 'new',
            runId: 'run-new',
            compatibilityKey: 'b'.repeat(64),
            incompatible: true,
        });
        store.heartbeat({
            trainer_id: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.revision,
        });
        assert.equal(store.state.pending_release.phase, 'preparing');

        store.heartbeat({
            trainer_id: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'running',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.revision,
        });
        assert.equal(store.state.pending_release.phase, 'stopping');
        assert.equal(store.state.run_id, 'run-old');
        assert.equal(store.stateFor({
            trainerId: 'remote-a', role: 'dedicated', platform: 'WindowsPlayer',
        }).desired_mode, 'stopped');

        store.heartbeat({
            trainer_id: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'stopped',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.pending_release.phase_revision,
        });
        assert.equal(store.state.run_id, 'run-old');
        store.heartbeat({
            trainer_id: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
            process_state: 'stopped',
            build_id: 'old',
            build_sha256: oldSha,
            prepared_build_id: 'new',
            applied_revision: store.state.pending_release.phase_revision,
        });

        assert.equal(store.state.canonical_build_id, 'new');
        assert.equal(store.state.run_id, 'run-new');
        assert.equal(store.state.compatibility_key, 'b'.repeat(64));
        assert.equal(store.state.pending_release, null);
        const restarted = store.stateFor({
            trainerId: 'remote-a', role: 'dedicated', platform: 'WindowsPlayer',
        });
        assert.equal(restarted.desired_mode, 'training');
        assert.equal(restarted.build.build_id, 'new');
    });
});

test('disabling training does not bypass an incompatible release stop barrier', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'stop-barrier-old');
        publishDedicatedBuild(store, root, 'stop-barrier-new');

        store.stageRelease({
            buildId: 'stop-barrier-old',
            runId: 'stop-barrier-old-run',
            compatibilityKey: 'c'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'stop-barrier-old', oldSha);
        }

        store.stageRelease({
            buildId: 'stop-barrier-new',
            runId: 'stop-barrier-new-run',
            compatibilityKey: 'd'.repeat(64),
            incompatible: true,
        });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store,
                trainerId,
                'stop-barrier-old',
                oldSha,
                { preparedBuildId: 'stop-barrier-new' },
            );
        }
        assert.equal(store.state.pending_release.phase, 'stopping');

        store.setDesiredState({ training_enabled: false });
        assert.equal(store.state.canonical_build_id, 'stop-barrier-old');
        assert.equal(store.state.run_id, 'stop-barrier-old-run');
        assert.equal(store.state.pending_release.phase, 'stopping');

        const stoppingRevision = store.state.pending_release.phase_revision;
        heartbeatDedicated(
            store,
            'remote-a',
            'stop-barrier-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'stop-barrier-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'stop-barrier-old');

        heartbeatDedicated(
            store,
            'central-learner',
            'stop-barrier-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'stop-barrier-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'stop-barrier-new');
        assert.equal(store.state.run_id, 'stop-barrier-new-run');
        assert.equal(store.state.pending_release, null);
        assert.equal(store.state.training_enabled, false);
    });
});

test('a different release cannot replace an active pending rollout', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'overlap-old');
        publishDedicatedBuild(store, root, 'overlap-first');
        publishDedicatedBuild(store, root, 'overlap-second');

        store.stageRelease({
            buildId: 'overlap-old',
            runId: 'overlap-run',
            compatibilityKey: 'e'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-a', 'overlap-old', oldSha);

        store.stageRelease({
            buildId: 'overlap-first',
            runId: 'overlap-run',
            compatibilityKey: 'e'.repeat(64),
            incompatible: false,
        });
        assert.equal(store.state.pending_release.build_id, 'overlap-first');
        assert.equal(store.state.pending_release.phase, 'preparing');

        assert.throws(
            () => store.stageRelease({
                buildId: 'overlap-second',
                runId: 'overlap-run',
                compatibilityKey: 'e'.repeat(64),
                incompatible: false,
            }),
            error => error.statusCode === 409 && /already pending/.test(error.message),
        );
        assert.equal(store.state.pending_release.build_id, 'overlap-first');
        assert.equal(store.state.canonical_build_id, 'overlap-old');
    });
});

test('preparing rollout barrier survives training-control server restart', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = { statePath, artifactRoot, leaseSeconds: 20 };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'restart-preparing-old');
        publishDedicatedBuild(store, root, 'restart-preparing-new');

        store.stageRelease({
            buildId: 'restart-preparing-old',
            runId: 'restart-preparing-run',
            compatibilityKey: '1'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(
                store, trainerId, 'restart-preparing-old', oldSha);
        }

        store.stageRelease({
            buildId: 'restart-preparing-new',
            runId: 'restart-preparing-run',
            compatibilityKey: '1'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'restart-preparing-old',
            oldSha,
            { preparedBuildId: 'restart-preparing-new' },
        );
        assert.equal(store.state.pending_release.phase, 'preparing');

        store = new TrainingControlStore(options);
        let desired = store.status().desired;
        assert.equal(desired.canonical_build_id, 'restart-preparing-old');
        assert.equal(desired.pending_release.phase, 'preparing');
        assert.deepEqual(
            desired.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-a', 'central-learner'],
        );

        heartbeatDedicated(
            store,
            'remote-a',
            'restart-preparing-old',
            oldSha,
            { preparedBuildId: 'restart-preparing-new' },
        );
        assert.equal(store.state.pending_release.phase, 'preparing');
        desired = heartbeatDedicated(
            store,
            'central-learner',
            'restart-preparing-old',
            oldSha,
            { preparedBuildId: 'restart-preparing-new' },
        );
        assert.equal(desired.pending_release.phase, 'rolling');
        assert.equal(store.state.canonical_build_id, 'restart-preparing-old');
    });
});

test('recent trainer registry seeds a release staged immediately after server restart', () => {
    withTempDir(root => {
        let now = 5000;
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = {
            statePath,
            artifactRoot,
            leaseSeconds: 20,
            now: () => now,
        };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'restart-stage-old');
        publishDedicatedBuild(store, root, 'restart-stage-new');

        store.stageRelease({
            buildId: 'restart-stage-old',
            runId: 'restart-stage-run',
            compatibilityKey: '7'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'restart-stage-old', oldSha);
        }

        store = new TrainingControlStore(options);
        const staged = store.stageRelease({
            buildId: 'restart-stage-new',
            runId: 'restart-stage-run',
            compatibilityKey: '7'.repeat(64),
            incompatible: false,
        });

        assert.equal(staged.canonical_build_id, 'restart-stage-old');
        assert.equal(staged.pending_release.phase, 'preparing');
        assert.deepEqual(
            staged.pending_release.required_trainers.map(item => item.trainer_id),
            ['remote-a', 'central-learner'],
        );
        assert.equal(staged.pending_release.collect_until_ms, 0);
    });
});

test('rolling rollout barrier survives server restart and re-requires healthy trainers', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = { statePath, artifactRoot, leaseSeconds: 20 };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'restart-rolling-old');
        const newSha = publishDedicatedBuild(store, root, 'restart-rolling-new');

        store.stageRelease({
            buildId: 'restart-rolling-old',
            runId: 'restart-rolling-run',
            compatibilityKey: '2'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'restart-rolling-old', oldSha);
        }
        store.stageRelease({
            buildId: 'restart-rolling-new',
            runId: 'restart-rolling-run',
            compatibilityKey: '2'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'restart-rolling-old',
            oldSha,
            { preparedBuildId: 'restart-rolling-new' },
        );
        heartbeatDedicated(
            store,
            'central-learner',
            'restart-rolling-old',
            oldSha,
            { preparedBuildId: 'restart-rolling-new' },
        );
        assert.equal(store.state.pending_release.phase, 'rolling');

        store = new TrainingControlStore(options);
        assert.equal(store.status().desired.pending_release.phase, 'rolling');
        assert.equal(store.state.canonical_build_id, 'restart-rolling-old');
        assert.equal(store.stateFor({
            trainerId: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        }).desired_build_id, 'restart-rolling-new');

        const rolloutRevision = store.state.pending_release.phase_revision;
        heartbeatDedicated(
            store,
            'remote-a',
            'restart-rolling-new',
            newSha,
            {
                preparedBuildId: 'restart-rolling-new',
                appliedRevision: rolloutRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'restart-rolling-old');
        assert.equal(store.stateFor({
            trainerId: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        }).desired_build_id, 'restart-rolling-new');

        heartbeatDedicated(
            store,
            'central-learner',
            'restart-rolling-new',
            newSha,
            {
                preparedBuildId: 'restart-rolling-new',
                appliedRevision: rolloutRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'restart-rolling-new');
        assert.equal(store.state.pending_release, null);
    });
});

test('incompatible stopping barrier survives server restart until every trainer re-registers stopped', () => {
    withTempDir(root => {
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const options = { statePath, artifactRoot, leaseSeconds: 20 };
        let store = new TrainingControlStore(options);
        const oldSha = publishDedicatedBuild(store, root, 'restart-stopping-old');
        publishDedicatedBuild(store, root, 'restart-stopping-new');

        store.stageRelease({
            buildId: 'restart-stopping-old',
            runId: 'restart-stopping-old-run',
            compatibilityKey: '3'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'restart-stopping-old', oldSha);
        }
        store.stageRelease({
            buildId: 'restart-stopping-new',
            runId: 'restart-stopping-new-run',
            compatibilityKey: '4'.repeat(64),
            incompatible: true,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'restart-stopping-old',
            oldSha,
            { preparedBuildId: 'restart-stopping-new' },
        );
        heartbeatDedicated(
            store,
            'central-learner',
            'restart-stopping-old',
            oldSha,
            { preparedBuildId: 'restart-stopping-new' },
        );
        assert.equal(store.state.pending_release.phase, 'stopping');

        store = new TrainingControlStore(options);
        assert.equal(store.status().desired.pending_release.phase, 'stopping');
        assert.equal(store.state.run_id, 'restart-stopping-old-run');
        assert.equal(store.state.canonical_build_id, 'restart-stopping-old');

        const stoppingRevision = store.state.pending_release.phase_revision;
        heartbeatDedicated(
            store,
            'remote-a',
            'restart-stopping-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'restart-stopping-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.run_id, 'restart-stopping-old-run');
        assert.equal(store.state.pending_release.phase, 'stopping');

        heartbeatDedicated(
            store,
            'central-learner',
            'restart-stopping-old',
            oldSha,
            {
                processState: 'stopped',
                preparedBuildId: 'restart-stopping-new',
                appliedRevision: stoppingRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'restart-stopping-new');
        assert.equal(store.state.run_id, 'restart-stopping-new-run');
        assert.equal(store.state.pending_release, null);
    });
});

test('compatible rollout does not count an installed but crashed build as healthy', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const oldSha = publishDedicatedBuild(store, root, 'health-old');
        const newSha = publishDedicatedBuild(store, root, 'health-new');

        store.stageRelease({
            buildId: 'health-old',
            runId: 'health-run',
            compatibilityKey: '5'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-a', 'health-old', oldSha);

        store.stageRelease({
            buildId: 'health-new',
            runId: 'health-run',
            compatibilityKey: '5'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'health-old',
            oldSha,
            { preparedBuildId: 'health-new' },
        );
        assert.equal(store.state.pending_release.phase, 'rolling');
        const rolloutRevision = store.state.pending_release.phase_revision;

        heartbeatDedicated(
            store,
            'remote-a',
            'health-new',
            newSha,
            {
                processState: 'stopped',
                preparedBuildId: 'health-new',
                appliedRevision: rolloutRevision,
                lastError: 'managed process exited with code 1',
            },
        );
        assert.equal(store.state.canonical_build_id, 'health-old');
        assert.equal(store.state.pending_release.phase, 'rolling');

        heartbeatDedicated(
            store,
            'remote-a',
            'health-new',
            newSha,
            {
                preparedBuildId: 'health-new',
                appliedRevision: rolloutRevision - 1,
            },
        );
        assert.equal(store.state.canonical_build_id, 'health-old');

        heartbeatDedicated(
            store,
            'remote-a',
            'health-new',
            '0'.repeat(64),
            {
                preparedBuildId: 'health-new',
                appliedRevision: rolloutRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'health-old');

        heartbeatDedicated(
            store,
            'remote-a',
            'health-new',
            newSha,
            {
                preparedBuildId: 'health-new',
                appliedRevision: rolloutRevision,
                lastError: 'startup health error',
            },
        );
        assert.equal(store.state.canonical_build_id, 'health-old');

        heartbeatDedicated(
            store,
            'remote-a',
            'health-new',
            newSha,
            {
                preparedBuildId: 'health-new',
                appliedRevision: rolloutRevision,
            },
        );
        assert.equal(store.state.canonical_build_id, 'health-new');
        assert.equal(store.state.pending_release, null);
    });
});

test('schema 4 pending rollout migration waits for trainer recollection instead of promoting empty barrier', () => {
    withTempDir(root => {
        let now = 1000;
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        const store = new TrainingControlStore({
            statePath,
            artifactRoot,
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'migration-old');
        publishDedicatedBuild(store, root, 'migration-new');
        store.stageRelease({
            buildId: 'migration-old',
            runId: 'migration-run',
            compatibilityKey: '6'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        heartbeatDedicated(store, 'remote-a', 'migration-old', oldSha);
        store.stageRelease({
            buildId: 'migration-new',
            runId: 'migration-run',
            compatibilityKey: '6'.repeat(64),
            incompatible: false,
        });

        const legacy = JSON.parse(fs.readFileSync(statePath, 'utf8'));
        legacy.schema_version = 4;
        delete legacy.known_dedicated_trainers;
        delete legacy.pending_release.required_trainers;
        delete legacy.pending_release.phase_revision;
        delete legacy.pending_release.collect_until_ms;
        fs.writeFileSync(statePath, JSON.stringify(legacy));

        const migrated = new TrainingControlStore({
            statePath,
            artifactRoot,
            leaseSeconds: 10,
            now: () => now,
        });
        const desired = migrated.status().desired;
        assert.equal(desired.schema_version, 5);
        assert.equal(desired.canonical_build_id, 'migration-old');
        assert.equal(desired.pending_release.build_id, 'migration-new');
        assert.equal(desired.pending_release.phase, 'preparing');
        assert.deepEqual(desired.pending_release.required_trainers, []);
        assert.equal(desired.pending_release.collect_until_ms, 11000);
    });
});

test('schema 4 rolling migration recollects remotes before assigning a new rolling target', () => {
    withTempDir(root => {
        let now = 1000;
        const statePath = path.join(root, 'state.json');
        const artifactRoot = path.join(root, 'artifacts');
        let store = new TrainingControlStore({
            statePath,
            artifactRoot,
            leaseSeconds: 10,
            now: () => now,
        });
        const oldSha = publishDedicatedBuild(store, root, 'migration-rolling-old');
        publishDedicatedBuild(store, root, 'migration-rolling-new');

        store.stageRelease({
            buildId: 'migration-rolling-old',
            runId: 'migration-rolling-run',
            compatibilityKey: '8'.repeat(64),
            incompatible: false,
        });
        store.setDesiredState({ training_enabled: true });
        for (const trainerId of ['remote-a', 'central-learner']) {
            heartbeatDedicated(store, trainerId, 'migration-rolling-old', oldSha);
        }
        store.stageRelease({
            buildId: 'migration-rolling-new',
            runId: 'migration-rolling-run',
            compatibilityKey: '8'.repeat(64),
            incompatible: false,
        });
        heartbeatDedicated(
            store,
            'remote-a',
            'migration-rolling-old',
            oldSha,
            { preparedBuildId: 'migration-rolling-new' },
        );
        heartbeatDedicated(
            store,
            'central-learner',
            'migration-rolling-old',
            oldSha,
            { preparedBuildId: 'migration-rolling-new' },
        );
        assert.equal(store.state.pending_release.phase, 'rolling');

        const legacy = JSON.parse(fs.readFileSync(statePath, 'utf8'));
        legacy.schema_version = 4;
        delete legacy.known_dedicated_trainers;
        delete legacy.pending_release.required_trainers;
        delete legacy.pending_release.phase_revision;
        delete legacy.pending_release.collect_until_ms;
        fs.writeFileSync(statePath, JSON.stringify(legacy));

        store = new TrainingControlStore({
            statePath,
            artifactRoot,
            leaseSeconds: 10,
            now: () => now,
        });
        const centralDuringCollection = heartbeatDedicated(
            store,
            'central-learner',
            'migration-rolling-old',
            oldSha,
            { preparedBuildId: 'migration-rolling-new' },
        );
        assert.equal(
            centralDuringCollection.desired_build_id,
            'migration-rolling-old',
        );

        const remoteDuringCollection = heartbeatDedicated(
            store,
            'remote-a',
            'migration-rolling-old',
            oldSha,
            { preparedBuildId: 'migration-rolling-new' },
        );
        assert.equal(
            remoteDuringCollection.desired_build_id,
            'migration-rolling-old',
        );

        now = 11000;
        assert.equal(store.stateFor({
            trainerId: 'remote-a',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        }).desired_build_id, 'migration-rolling-new');
        assert.equal(store.stateFor({
            trainerId: 'central-learner',
            role: 'dedicated',
            platform: 'WindowsPlayer',
        }).desired_build_id, 'migration-rolling-old');
    });
});

test('trainer logs append by verified offset under their run and trainer namespace', () => {
    withTempDir(root => {
        const logRoot = path.join(root, 'logs');
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            logRoot,
        });
        let result = store.appendTrainerLog({
            trainerId: 'trainer-a',
            runId: 'run-a',
            relativePath: 'Player-0.log',
            offset: 0,
            reset: false,
            data: Buffer.from('abc'),
        });
        assert.equal(result.next_offset, 3);
        result = store.appendTrainerLog({
            trainerId: 'trainer-a',
            runId: 'run-a',
            relativePath: 'Player-0.log',
            offset: 3,
            reset: false,
            data: Buffer.from('def'),
        });
        assert.equal(result.next_offset, 6);
        assert.equal(
            fs.readFileSync(path.join(logRoot, 'run-a', 'trainer-a', 'Player-0.log'), 'utf8'),
            'abcdef',
        );

        assert.throws(
            () => store.appendTrainerLog({
                trainerId: 'trainer-a',
                runId: 'run-a',
                relativePath: 'Player-0.log',
                offset: 3,
                reset: true,
                data: Buffer.alloc(0),
            }),
            error => error.statusCode === 409 && error.expectedOffset === 6,
        );
        assert.equal(
            fs.readFileSync(path.join(logRoot, 'run-a', 'trainer-a', 'Player-0.log'), 'utf8'),
            'abcdef',
        );

        result = store.appendTrainerLog({
            trainerId: 'trainer-a',
            runId: 'run-a',
            relativePath: 'Player-0.log',
            offset: 0,
            reset: true,
            data: Buffer.from('xy'),
        });
        assert.equal(result.next_offset, 2);
        assert.equal(
            fs.readFileSync(path.join(logRoot, 'run-a', 'trainer-a', 'Player-0.log'), 'utf8'),
            'xy',
        );

        assert.throws(() => store.appendTrainerLog({
            trainerId: 'trainer-a',
            runId: 'run-a',
            relativePath: '../escape.log',
            offset: 0,
            reset: false,
            data: Buffer.from('bad'),
        }), /unsafe/);
    });
});


test('training control returns per-worker env targets from learner-consumed optimization', () => {
    withTempDir(root => {
        let now = 0;
        const archive = path.join(root, 'linux.zip');
        fs.writeFileSync(archive, Buffer.from('linux-build'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            now: () => now,
            envOptimizer: {
                warmupMs: 0,
                measurementMs: 1000,
                cooldownMs: 0,
                retestMs: 60000,
            },
        });
        store.publishArtifact({
            role: 'dedicated',
            platform: 'LinuxPlayer',
            buildId: 'release-opt',
            archivePath: archive,
            entrypoint: 'Bees.x86_64',
        });
        activateTestRelease(store, 'release-opt');
        store.setDesiredState({ training_enabled: true });

        const heartbeat = consumedSteps => store.heartbeat({
            trainer_id: 'remote-linux',
            role: 'dedicated',
            platform: 'LinuxPlayer',
            process_state: 'running',
            applied_revision: store.state.revision,
            build_id: 'release-opt',
            build_sha256: store.artifact(
                'dedicated', 'LinuxPlayer', 'release-opt').archive_sha256,
            worker_capacity: {
                auto: true,
                current_envs: 8,
                min_envs: 1,
                max_envs: 16,
            },
            metrics: {
                throughput: {
                    learner_consumed_steps_total: consumedSteps,
                },
            },
        });

        let desired = heartbeat(0);
        assert.equal(desired.worker_env_count, 8);
        assert.equal(desired.env_optimizer.phase, 'measuring');

        now = 1000;
        desired = heartbeat(1000);
        assert.equal(desired.worker_env_count, 9);
        assert.equal(desired.env_optimizer.baseline_sps, 1000);
        assert.equal(desired.env_optimizer.probing, true);

        const trainer = store.status().trainers.find(
            record => record.trainer_id === 'remote-linux');
        assert.equal(trainer.worker_capacity.current_envs, 8);
        assert.equal(trainer.env_optimizer.desired_envs, 9);
    });
});

test('training control pauses env optimization during a release cutover', () => {
    withTempDir(root => {
        let now = 0;
        const firstArchive = path.join(root, 'linux-1.zip');
        const secondArchive = path.join(root, 'linux-2.zip');
        fs.writeFileSync(firstArchive, Buffer.from('linux-build-1'));
        fs.writeFileSync(secondArchive, Buffer.from('linux-build-2'));
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
            now: () => now,
            envOptimizer: {
                warmupMs: 0,
                measurementMs: 1000,
                cooldownMs: 0,
            },
        });
        for (const [buildId, archivePath] of [
            ['release-1', firstArchive],
            ['release-2', secondArchive],
        ]) {
            store.publishArtifact({
                role: 'dedicated',
                platform: 'LinuxPlayer',
                buildId,
                archivePath,
                entrypoint: 'Bees.x86_64',
            });
        }
        activateTestRelease(store, 'release-1');
        store.setDesiredState({ training_enabled: true });

        const heartbeat = consumedSteps => store.heartbeat({
            trainer_id: 'remote-linux',
            role: 'dedicated',
            platform: 'LinuxPlayer',
            process_state: 'running',
            applied_revision: store.state.revision,
            build_id: 'release-1',
            build_sha256: store.artifact(
                'dedicated', 'LinuxPlayer', 'release-1').archive_sha256,
            worker_capacity: {
                auto: true,
                current_envs: 8,
                min_envs: 1,
                max_envs: 16,
            },
            metrics: {
                throughput: {
                    learner_consumed_steps_total: consumedSteps,
                },
            },
        });

        heartbeat(0);
        now = 1000;
        let desired = heartbeat(1000);
        assert.equal(desired.worker_env_count, 9);
        assert.equal(desired.env_optimizer.probing, true);

        store.stageRelease({
            buildId: 'release-2',
            runId: 'run-2',
            compatibilityKey: 'b'.repeat(64),
            incompatible: false,
        });

        now = 1010;
        desired = heartbeat(1010);
        assert.equal(desired.worker_env_count, 8);
        assert.equal(desired.env_optimizer.phase, 'paused');
        assert.equal(desired.env_optimizer.enabled, false);
    });
});

test('training control leaves explicit fixed worker env counts unchanged', () => {
    withTempDir(root => {
        const store = new TrainingControlStore({
            statePath: path.join(root, 'state.json'),
            artifactRoot: path.join(root, 'artifacts'),
        });
        const state = store.heartbeat({
            trainer_id: 'remote-fixed',
            role: 'dedicated',
            platform: 'LinuxPlayer',
            process_state: 'stopped',
            applied_revision: 0,
            worker_capacity: {
                auto: false,
                current_envs: 12,
                min_envs: 12,
                max_envs: 12,
            },
        });
        assert.equal(state.worker_env_count, 12);
        assert.equal(state.env_optimizer.phase, 'manual');
        assert.equal(state.env_optimizer.enabled, false);
    });
});
