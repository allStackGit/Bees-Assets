'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const fsp = fs.promises;
const {
    RlModelDistributionError,
    RlModelDistributionManager,
    resolveInsideRoot,
} = require('../rlModelDistribution');
const { RL_DEMO_POLICY: RL_POLICY } = require('../rlDemonstrationUploads');

function sha256(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

async function fixture(t, options = {}) {
    const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'bees-rl-model-distribution-'));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    const platform = 'StandaloneWindows64';
    const deploymentId = `deploy-${'a'.repeat(24)}`;
    const modelId = `bees-rl-v${RL_POLICY.policyAbiVersion}-${'b'.repeat(24)}`;
    const bundle = Buffer.from('immutable-unity-asset-bundle');
    const bundleSha256 = sha256(bundle);
    const relativeBundle = `packages/${deploymentId}/${platform}/champion.bundle`;
    const bundlePath = path.join(root, relativeBundle);
    await fsp.mkdir(path.dirname(bundlePath), { recursive: true });
    await fsp.writeFile(bundlePath, bundle);
    const pointer = {
        schema_version: 1,
        identity: {
            schema_version: 1,
            platform,
            deployment_id: deploymentId,
            model_id: modelId,
            bundle_path: relativeBundle,
            bundle_sha256: bundleSha256,
            bundle_size_bytes: bundle.length,
            manifest_sha256: 'c'.repeat(64),
            model_sha256: 'd'.repeat(64),
            policy_abi_version: RL_POLICY.policyAbiVersion,
            policy_signature: RL_POLICY.policySignature,
        },
    };
    const pointerPath = path.join(root, `current-${platform}.json`);
    await fsp.writeFile(pointerPath, `${JSON.stringify(pointer, null, 2)}\n`);
    return {
        root,
        platform,
        deploymentId,
        modelId,
        bundle,
        bundleSha256,
        bundlePath,
        pointer,
        pointerPath,
        manager: new RlModelDistributionManager(root, options),
        context: { userId: '76561198000000000', connectionId: '17' },
    };
}

function currentRequest(platform, overrides = {}) {
    return {
        Type: 'rl-model-current',
        Platform: platform,
        PolicyAbiVersion: RL_POLICY.policyAbiVersion,
        PolicySignature: RL_POLICY.policySignature,
        ...overrides,
    };
}

function chunkRequest(f, offset, length, overrides = {}) {
    return {
        Type: 'rl-model-chunk',
        Platform: f.platform,
        DeploymentId: f.deploymentId,
        BundleSha256: f.bundleSha256,
        Offset: offset,
        Length: length,
        PolicyAbiVersion: RL_POLICY.policyAbiVersion,
        PolicySignature: RL_POLICY.policySignature,
        ...overrides,
    };
}

test('current pointer returns immutable compatible bundle metadata and up-to-date state', async t => {
    const f = await fixture(t);
    const available = await f.manager.handle(currentRequest(f.platform), f.context);
    assert.equal(available.UpToDate, false);
    assert.equal(available.DeploymentId, f.deploymentId);
    assert.equal(available.ModelId, f.modelId);
    assert.equal(available.BundleSha256, f.bundleSha256);
    assert.equal(available.BundleSizeBytes, f.bundle.length);
    assert.equal(available.PolicyAbiVersion, RL_POLICY.policyAbiVersion);

    const current = await f.manager.handle(
        currentRequest(f.platform, { CurrentDeploymentId: f.deploymentId }),
        f.context,
    );
    assert.equal(current.UpToDate, true);
});

test('chunk returns bounded canonical bytes and terminal offset', async t => {
    const f = await fixture(t, { maxChunkBytes: 8 });
    const first = await f.manager.handle(chunkRequest(f, 0, 8), f.context);
    assert.equal(first.Offset, 0);
    assert.equal(first.NextOffset, 8);
    assert.equal(first.Complete, false);
    assert.deepEqual(Buffer.from(first.Data, 'base64'), f.bundle.subarray(0, 8));

    const tailOffset = f.bundle.length - 3;
    const tail = await f.manager.handle(chunkRequest(f, tailOffset, 8), f.context);
    assert.equal(tail.NextOffset, f.bundle.length);
    assert.equal(tail.Complete, true);
    assert.deepEqual(Buffer.from(tail.Data, 'base64'), f.bundle.subarray(tailOffset));
});

test('client policy mismatch fails closed before distribution metadata is returned', async t => {
    const f = await fixture(t);
    await assert.rejects(
        f.manager.handle(currentRequest(f.platform, { PolicyAbiVersion: RL_POLICY.policyAbiVersion - 1 }), f.context),
        error => error instanceof RlModelDistributionError &&
            error.statusCode === 409 && error.code === 'incompatible-client-policy',
    );
});

test('rollback or promotion during download invalidates the old deployment request', async t => {
    const f = await fixture(t);
    const replacementId = `deploy-${'e'.repeat(24)}`;
    const replacementBundle = Buffer.from('replacement-bundle');
    const replacementSha = sha256(replacementBundle);
    const replacementRelative = `packages/${replacementId}/${f.platform}/champion.bundle`;
    await fsp.mkdir(path.join(f.root, 'packages', replacementId, f.platform), { recursive: true });
    await fsp.writeFile(path.join(f.root, replacementRelative), replacementBundle);
    f.pointer.identity.deployment_id = replacementId;
    f.pointer.identity.model_id = `bees-rl-v${RL_POLICY.policyAbiVersion}-${'f'.repeat(24)}`;
    f.pointer.identity.bundle_path = replacementRelative;
    f.pointer.identity.bundle_sha256 = replacementSha;
    f.pointer.identity.bundle_size_bytes = replacementBundle.length;
    await fsp.writeFile(f.pointerPath, `${JSON.stringify(f.pointer, null, 2)}\n`);

    await assert.rejects(
        f.manager.handle(chunkRequest(f, 0, 4), f.context),
        error => error instanceof RlModelDistributionError &&
            error.statusCode === 409 && error.code === 'deployment-changed',
    );
});

test('published pointer cannot escape the configured distribution root', async t => {
    const f = await fixture(t);
    f.pointer.identity.bundle_path = '../outside.bundle';
    await fsp.writeFile(f.pointerPath, `${JSON.stringify(f.pointer, null, 2)}\n`);
    await assert.rejects(
        f.manager.handle(currentRequest(f.platform), f.context),
        error => error instanceof RlModelDistributionError &&
            error.statusCode === 500 && error.code === 'distribution-corrupt',
    );
    assert.equal(resolveInsideRoot(f.root, '../outside.bundle'), null);
});

test('bundle hash mismatch fails closed instead of serving changed bytes', async t => {
    const f = await fixture(t);
    await fsp.writeFile(f.bundlePath, Buffer.alloc(f.bundle.length, 7));
    await assert.rejects(
        f.manager.handle(currentRequest(f.platform), f.context),
        error => error instanceof RlModelDistributionError &&
            error.statusCode === 500 && error.code === 'distribution-corrupt',
    );
});

test('per-user download quota bounds repeated bundle reads', async t => {
    const f = await fixture(t, { userBytesPerWindow: 5, maxChunkBytes: 4 });
    await f.manager.handle(chunkRequest(f, 0, 4), f.context);
    await assert.rejects(
        f.manager.handle(chunkRequest(f, 4, 4), f.context),
        error => error instanceof RlModelDistributionError &&
            error.statusCode === 429 && error.code === 'download-rate-limit',
    );
});
