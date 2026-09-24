'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const fsp = fs.promises;
const {
    RL_DEMO_POLICY,
    RlDemonstrationUploadError,
    RlDemonstrationUploadManager,
} = require('../rlDemonstrationUploads');

function sha256(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function captureManifest() {
    return {
        schemaVersion: RL_DEMO_POLICY.schemaVersion,
        behaviorName: RL_DEMO_POLICY.behaviorName,
        policyAbiVersion: RL_DEMO_POLICY.policyAbiVersion,
        policySignature: RL_DEMO_POLICY.policySignature,
        observationSize: RL_DEMO_POLICY.observationSize,
        continuousActionCount: RL_DEMO_POLICY.continuousActionCount,
        discreteBranchSizes: [...RL_DEMO_POLICY.discreteBranchSizes],
    };
}

async function fixture(t, options = {}) {
    const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'bees-rl-demo-upload-'));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    return {
        root,
        manager: new RlDemonstrationUploadManager(root, options),
        context: { userId: '76561198000000000', connectionId: '17' },
    };
}

function beginRequest(bytes, overrides = {}) {
    const manifestJson = overrides.ManifestJson || JSON.stringify(captureManifest(), null, 2);
    return {
        Type: 'rl-demo-begin',
        Source: 'Human',
        DemonstrationId: 'demo-1',
        GameBuildVersion: '2026.09.11',
        TotalBytes: bytes.length,
        DemoSha256: sha256(bytes),
        ManifestSha256: sha256(Buffer.from(manifestJson, 'utf8')),
        ManifestJson: manifestJson,
        ...overrides,
    };
}

function chunkRequest(uploadId, offset, bytes) {
    return {
        Type: 'rl-demo-chunk',
        UploadId: uploadId,
        Offset: offset,
        Data: bytes.toString('base64'),
    };
}

function completeRequest(uploadId) {
    return { Type: 'rl-demo-complete', UploadId: uploadId };
}

test('public upload policy matches current Unity ABI v18 capture contract', () => {
    assert.equal(RL_DEMO_POLICY.schemaVersion, 1);
    assert.equal(RL_DEMO_POLICY.behaviorName, 'BeesRL1v1');
    assert.equal(RL_DEMO_POLICY.policyAbiVersion, 18);
    assert.equal(RL_DEMO_POLICY.observationSize, 7342);
    assert.equal(RL_DEMO_POLICY.continuousActionCount, 16);
    assert.deepEqual(RL_DEMO_POLICY.discreteBranchSizes, [
        2, 2, 2, 2, 2,
        5,
    ]);
    assert.equal(
        RL_DEMO_POLICY.policySignature,
        'bees-rl-v18|behavior=BeesRL1v1|network=ff-128x3|normalize=true|obs=7342|tail=episode-progress+20-reserved|cont=16|disc=2x5,5|coord-frame=team-episode-distinct-quarter-turn|weapon-aim=slotwise-xy|weapon-fire=slotwise-cease-or-fire|weapon-ready=rl-latched-until-fire|shiptype=fixed-scrambled-scalar24|weapontype=fixed-scrambled-scalar10|mapbits=4|shipmap=v1-0..23|weaponmap=v1-0..9|allies=64|enemies=64|weapons=5|entity-weapons=5|enemy-mounts=0|mining=8|map-objects=64|moving-asteroids=48|self=25|ship-id=episode-permuted-scalar23|capability=12|parent-carrier=40|entity-core=14|entity=40|ally=44-with-private-comm4|communication=4-continuous-private-allied|self-weapon=15|observed-weapon=5|weapon-observation=split-self-vs-observed|mining-slot=7|map-slot=12|moving-asteroid-slot=11|objective=16|grid=13x13|exploration-grid=16x16-team-shared-sight-recency|entity-order=distance,type,fleet-id,runtime-id',
    );
});

test('native demonstration is chunked to disk and finalized only into quarantine', async t => {
    const { root, manager, context } = await fixture(t, { maxChunkBytes: 8 });
    const bytes = Buffer.from('native-demo-bytes');
    const begin = await manager.handle(beginRequest(bytes), context);
    assert.equal(begin.Completed, false);
    assert.equal(begin.NextOffset, 0);
    assert.equal(begin.ChunkBytes, 8);

    const first = bytes.subarray(0, 8);
    const second = bytes.subarray(8, 16);
    const third = bytes.subarray(16);
    const firstResult = await manager.handle(chunkRequest(begin.UploadId, 0, first), context);
    assert.equal(firstResult.NextOffset, 8);
    const retry = await manager.handle(chunkRequest(begin.UploadId, 0, first), context);
    assert.equal(retry.Duplicate, true);
    assert.equal(retry.NextOffset, 8);
    await manager.handle(chunkRequest(begin.UploadId, 8, second), context);
    await manager.handle(chunkRequest(begin.UploadId, 16, third), context);

    const completed = await manager.handle(completeRequest(begin.UploadId), context);
    assert.equal(completed.Completed, true);
    assert.equal(completed.Duplicate, false);

    const incoming = path.join(root, 'incoming');
    const demoPath = path.join(incoming, `${completed.BatchId}.demo`);
    const manifestPath = path.join(incoming, `${completed.BatchId}.capture-manifest.json`);
    const metadataPath = path.join(incoming, `${completed.BatchId}.json`);
    assert.deepEqual(await fsp.readFile(demoPath), bytes);
    assert.equal(sha256(await fsp.readFile(manifestPath)), beginRequest(bytes).ManifestSha256);

    const metadata = JSON.parse(await fsp.readFile(metadataPath, 'utf8'));
    assert.equal(metadata.uploaderUserId, context.userId);
    assert.equal(metadata.gameBuildVersion, '2026.09.11');
    assert.equal(metadata.trust, 'authenticated-quarantine');
    assert.equal(metadata.readyForTraining, false);
    assert.equal(metadata.demoSha256, sha256(bytes));
    assert.equal(metadata.manifest.policySignature, RL_DEMO_POLICY.policySignature);
});

test('concurrent chunks for one upload are serialized in request order', async t => {
    const { manager, context } = await fixture(t, { maxChunkBytes: 4 });
    const bytes = Buffer.from('abcdefgh');
    const begin = await manager.handle(beginRequest(bytes), context);

    const [first, second] = await Promise.all([
        manager.handle(chunkRequest(begin.UploadId, 0, bytes.subarray(0, 4)), context),
        manager.handle(chunkRequest(begin.UploadId, 4, bytes.subarray(4, 8)), context),
    ]);

    assert.equal(first.NextOffset, 4);
    assert.equal(second.NextOffset, 8);
    const completed = await manager.handle(completeRequest(begin.UploadId), context);
    assert.equal(completed.Completed, true);
});

test('same content has one active finalization path before completion', async t => {
    const { manager, context } = await fixture(t);
    const bytes = Buffer.from('same-active-native-demo');
    const first = await manager.handle(beginRequest(bytes), context);
    const duplicate = await manager.handle(
        beginRequest(bytes, { DemonstrationId: 'same-content-other-id' }),
        context,
    );

    assert.equal(duplicate.UploadId, first.UploadId);
    assert.equal(manager.sessions.size, 1);
});

test('same authenticated content deduplicates even when demonstration ID changes', async t => {
    const { manager, context } = await fixture(t);
    const bytes = Buffer.from('same-native-demo');
    const first = await manager.handle(beginRequest(bytes), context);
    await manager.handle(chunkRequest(first.UploadId, 0, bytes), context);
    const completed = await manager.handle(completeRequest(first.UploadId), context);

    const duplicate = await manager.handle(beginRequest(bytes, { DemonstrationId: 'renamed-demo' }), context);
    assert.equal(duplicate.Completed, true);
    assert.equal(duplicate.Duplicate, true);
    assert.equal(duplicate.BatchId, completed.BatchId);
});

test('active demonstration ID cannot be reused for different content', async t => {
    const { manager, context } = await fixture(t);
    const firstBytes = Buffer.from('first');
    await manager.handle(beginRequest(firstBytes), context);

    await assert.rejects(
        manager.handle(beginRequest(Buffer.from('second')), context),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 409 &&
            error.code === 'demonstration-id-conflict',
    );
});

test('upload session ownership is bound to authenticated user and connection', async t => {
    const { manager, context } = await fixture(t);
    const bytes = Buffer.from('ownership');
    const begin = await manager.handle(beginRequest(bytes), context);

    await assert.rejects(
        manager.handle(chunkRequest(begin.UploadId, 0, bytes), {
            userId: '76561198000000001',
            connectionId: context.connectionId,
        }),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 403 &&
            error.code === 'upload-owner-mismatch',
    );
    await assert.rejects(
        manager.handle(chunkRequest(begin.UploadId, 0, bytes), {
            userId: context.userId,
            connectionId: 'other-connection',
        }),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 403,
    );
});

test('public upload rejects non-human and incompatible policy manifests', async t => {
    const { manager, context } = await fixture(t);
    const bytes = Buffer.from('manifest');

    await assert.rejects(
        manager.handle(beginRequest(bytes, { Source: 'HiveMind' }), context),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 403,
    );

    const stale = captureManifest();
    stale.policySignature = 'stale-policy';
    const staleJson = JSON.stringify(stale);
    await assert.rejects(
        manager.handle(beginRequest(bytes, {
            DemonstrationId: 'stale-demo',
            ManifestJson: staleJson,
            ManifestSha256: sha256(Buffer.from(staleJson, 'utf8')),
        }), context),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 409 &&
            error.code === 'incompatible-policy',
    );
});

test('combined demonstration and manifest bundle limit is enforced before allocation', async t => {
    const bytes = Buffer.from('1234');
    const manifestJson = JSON.stringify(captureManifest(), null, 2);
    const bundleBytes = bytes.length + Buffer.byteLength(manifestJson, 'utf8');
    const { root, manager, context } = await fixture(t, {
        maxDemoBytes: 1024 * 1024,
        maxBundleBytes: bundleBytes - 1,
    });

    await assert.rejects(
        manager.handle(beginRequest(bytes, { ManifestJson: manifestJson }), context),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 413 &&
            error.code === 'upload-too-large',
    );
    assert.equal(manager.sessions.size, 0);
    assert.deepEqual(await fsp.readdir(path.join(root, 'partial')), []);
});

test('declared upload quota and active-session limits fail closed', async t => {
    const { manager, context } = await fixture(t, {
        userBytesPerWindow: 8,
        maxActiveUploadsPerUser: 1,
    });
    const firstBytes = Buffer.from('1234');
    await manager.handle(beginRequest(firstBytes), context);

    await assert.rejects(
        manager.handle(beginRequest(Buffer.from('12'), { DemonstrationId: 'second-active' }), context),
        error => error instanceof RlDemonstrationUploadError && error.code === 'too-many-active-uploads',
    );

    const otherContext = { userId: '76561198000000001', connectionId: '18' };
    await assert.rejects(
        manager.handle(beginRequest(Buffer.from('123456789'), { DemonstrationId: 'quota' }), otherContext),
        error => error instanceof RlDemonstrationUploadError && error.code === 'upload-rate-limit',
    );
});

test('bad final SHA discards partial session instead of quarantining bytes', async t => {
    const { root, manager, context } = await fixture(t);
    const bytes = Buffer.from('actual-bytes');
    const beginParams = beginRequest(bytes, { DemoSha256: '0'.repeat(64) });
    const begin = await manager.handle(beginParams, context);
    await manager.handle(chunkRequest(begin.UploadId, 0, bytes), context);

    await assert.rejects(
        manager.handle(completeRequest(begin.UploadId), context),
        error => error instanceof RlDemonstrationUploadError && error.statusCode === 422 &&
            error.code === 'demo-hash-mismatch',
    );
    assert.equal(manager.sessions.has(begin.UploadId), false);
    const partials = await fsp.readdir(path.join(root, 'partial'));
    assert.deepEqual(partials, []);
    const incoming = await fsp.readdir(path.join(root, 'incoming'));
    assert.deepEqual(incoming, []);
});

test('manager removes unresumable partial files on first use after restart', async t => {
    const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'bees-rl-demo-restart-'));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    const partialDir = path.join(root, 'partial');
    await fsp.mkdir(partialDir, { recursive: true });
    const orphan = path.join(partialDir, 'old.demo.partial');
    await fsp.writeFile(orphan, 'orphan');

    const manager = new RlDemonstrationUploadManager(root);
    const bytes = Buffer.from('new-demo');
    await manager.handle(beginRequest(bytes), {
        userId: '76561198000000000',
        connectionId: '17',
    });
    assert.equal(fs.existsSync(orphan), false);
});
