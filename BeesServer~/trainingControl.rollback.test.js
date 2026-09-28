'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { TrainingControlStore } = require('./trainingControl');

test('compatible barrier pruning restores all fields when persistence fails', t => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-rollout-rollback-'));
    t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));

    const store = new TrainingControlStore({
        statePath: path.join(tempRoot, 'state.json'),
        artifactRoot: path.join(tempRoot, 'artifacts'),
        logRoot: path.join(tempRoot, 'logs'),
        leaseSeconds: 10,
        now: () => 100000,
    });
    store.state.known_dedicated_trainers = [{
        trainer_id: 'remote-linux',
        platform: 'linux',
        last_seen_ms: 80000,
    }];
    store.state.pending_release = {
        build_id: 'next-build',
        incompatible: false,
        phase: 'preparing',
        collect_until_ms: 0,
        required_trainers: [{ trainer_id: 'remote-linux', platform: 'linux' }],
        rolled_trainers: [],
        required_remote_platforms: ['linux'],
        healthy_remote_platforms: [],
        quarantined_trainers: [],
    };

    const before = JSON.stringify(store.state);
    store._persist = () => {
        throw new Error('simulated persistence failure');
    };

    assert.throws(
        () => store._pruneExpiredCompatibleBarrierTrainers(store.state.pending_release),
        /simulated persistence failure/,
    );
    assert.equal(JSON.stringify(store.state), before);
});
