'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
    RL_DEMO_POLICY,
    RlDemonstrationUploadError,
    RlDemonstrationUploadManager,
} = require('../rlDemonstrationUploads');

const fsp = fs.promises;

function sha256(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function manifestJson() {
    return JSON.stringify({
        schemaVersion: RL_DEMO_POLICY.schemaVersion,
        behaviorName: RL_DEMO_POLICY.behaviorName,
        policyAbiVersion: RL_DEMO_POLICY.policyAbiVersion,
        policySignature: RL_DEMO_POLICY.policySignature,
        observationSize: RL_DEMO_POLICY.observationSize,
        continuousActionCount: RL_DEMO_POLICY.continuousActionCount,
        discreteBranchSizes: [...RL_DEMO_POLICY.discreteBranchSizes],
    });
}

test('combined demo plus manifest must fit the central ingestion byte limit', async t => {
    const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'bees-rl-demo-bundle-'));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    const manifest = manifestJson();
    const demo = Buffer.from('demo');
    const manager = new RlDemonstrationUploadManager(root, {
        maxDemoBytes: 1024,
        maxBundleBytes: Buffer.byteLength(manifest, 'utf8') + demo.length - 1,
    });

    await assert.rejects(
        manager.handle({
            Type: 'rl-demo-begin',
            Source: 'Human',
            DemonstrationId: 'bundle-limit',
            GameBuildVersion: 'test-build',
            TotalBytes: demo.length,
            DemoSha256: sha256(demo),
            ManifestSha256: sha256(Buffer.from(manifest, 'utf8')),
            ManifestJson: manifest,
        }, {
            userId: '76561198000000000',
            connectionId: '17',
        }),
        error => error instanceof RlDemonstrationUploadError &&
            error.statusCode === 413 && error.code === 'upload-too-large',
    );
});
