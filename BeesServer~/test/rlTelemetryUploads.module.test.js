'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const fsp = fs.promises;
const { RL_DEMO_POLICY } = require('../rlDemonstrationUploads');
const {
    RL_TELEMETRY_POLICY,
    RlTelemetryUploadError,
    RlTelemetryUploadManager,
} = require('../rlTelemetryUploads');

function sha256(value) {
    return crypto.createHash('sha256').update(value).digest('hex');
}

function telemetryPayload(overrides = {}) {
    return {
        schema_version: RL_TELEMETRY_POLICY.schemaVersion,
        match_id: 'match-public-1',
        game_build_version: '2026.09.12',
        mode: 'campaign',
        result: 'bee_win',
        model_id: `bees-rl-v${RL_TELEMETRY_POLICY.policyAbiVersion}-0123456789abcdef01234567`,
        model_sha256: 'a'.repeat(64),
        deployment_id: `deploy-${'b'.repeat(24)}`,
        policy_signature: RL_TELEMETRY_POLICY.policySignature,
        behavior_name: RL_TELEMETRY_POLICY.behaviorName,
        policy_abi_version: RL_TELEMETRY_POLICY.policyAbiVersion,
        observation_schema_version: RL_TELEMETRY_POLICY.observationSchemaVersion,
        action_schema_version: RL_TELEMETRY_POLICY.actionSchemaVersion,
        reward_schema_version: RL_TELEMETRY_POLICY.rewardSchemaVersion,
        scenario_schema_version: RL_TELEMETRY_POLICY.scenarioSchemaVersion,
        steps: [{ agent_key: 'side-0:ship-1', decision_index: 0, observation: [0], continuous_action: [0], discrete_action: [0] }],
        ...overrides,
    };
}

function payloadBytes(overrides = {}) {
    return Buffer.from(JSON.stringify(telemetryPayload(overrides)), 'utf8');
}

function beginRequest(bytes, overrides = {}) {
    const payload = JSON.parse(bytes.toString('utf8'));
    return {
        Type: 'rl-telemetry-begin',
        MatchId: payload.match_id,
        GameBuildVersion: payload.game_build_version,
        TotalBytes: bytes.length,
        PayloadSha256: sha256(bytes),
        ModelId: payload.model_id,
        ModelSha256: payload.model_sha256,
        DeploymentId: payload.deployment_id,
        PolicyAbiVersion: payload.policy_abi_version,
        PolicySignature: payload.policy_signature,
        ...overrides,
    };
}

function chunkRequest(uploadId, offset, bytes) {
    return { Type: 'rl-telemetry-chunk', UploadId: uploadId, Offset: offset, Data: bytes.toString('base64') };
}

async function fixture(t, options = {}) {
    const root = await fsp.mkdtemp(path.join(os.tmpdir(), 'bees-rl-telemetry-upload-'));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    return {
        root,
        manager: new RlTelemetryUploadManager(root, options),
        context: { userId: '76561198000000000', connectionId: '17' },
    };
}

async function complete(manager, context, bytes) {
    const begin = await manager.handle(beginRequest(bytes), context);
    await manager.handle(chunkRequest(begin.UploadId, 0, bytes), context);
    return manager.handle({ Type: 'rl-telemetry-complete', UploadId: begin.UploadId }, context);
}

test('telemetry quarantine policy stays pinned to the accepted Unity v19 policy', () => {
    assert.equal(RL_TELEMETRY_POLICY.schemaVersion, 1);
    assert.equal(RL_TELEMETRY_POLICY.behaviorName, RL_DEMO_POLICY.behaviorName);
    assert.equal(RL_TELEMETRY_POLICY.policyAbiVersion, RL_DEMO_POLICY.policyAbiVersion);
    assert.equal(RL_TELEMETRY_POLICY.policySignature, RL_DEMO_POLICY.policySignature);
    assert.equal(RL_TELEMETRY_POLICY.observationSchemaVersion, 11);
    assert.equal(RL_TELEMETRY_POLICY.actionSchemaVersion, 8);
    assert.equal(RL_TELEMETRY_POLICY.rewardSchemaVersion, 3);
    assert.equal(RL_TELEMETRY_POLICY.scenarioSchemaVersion, 1);
});

test('telemetry is chunked to disk and finalized only into authenticated quarantine', async t => {
    const { root, manager, context } = await fixture(t, { maxChunkBytes: 32 });
    const bytes = payloadBytes();
    const begin = await manager.handle(beginRequest(bytes), context);
    for (let offset = 0; offset < bytes.length; offset += begin.ChunkBytes) {
        const chunk = bytes.subarray(offset, Math.min(bytes.length, offset + begin.ChunkBytes));
        const progress = await manager.handle(chunkRequest(begin.UploadId, offset, chunk), context);
        assert.equal(progress.NextOffset, offset + chunk.length);
    }
    const completed = await manager.handle({ Type: 'rl-telemetry-complete', UploadId: begin.UploadId }, context);
    assert.equal(completed.Completed, true);
    assert.equal(completed.Duplicate, false);

    const incoming = path.join(root, 'incoming');
    const archived = await fsp.readFile(path.join(incoming, `${completed.BatchId}.json`));
    assert.deepEqual(archived, bytes);
    const metadata = JSON.parse(await fsp.readFile(path.join(incoming, `${completed.BatchId}.metadata.json`), 'utf8'));
    assert.equal(metadata.uploaderUserId, context.userId);
    assert.equal(metadata.matchId, 'match-public-1');
    assert.equal(metadata.payloadSha256, sha256(bytes));
    assert.equal(metadata.trust, 'authenticated-quarantine');
    assert.equal(metadata.readyForIngestion, false);
});

test('exact chunk retries are idempotent but changed retry bytes fail closed', async t => {
    const { manager, context } = await fixture(t, { maxChunkBytes: 64 });
    const bytes = payloadBytes();
    const begin = await manager.handle(beginRequest(bytes), context);
    const first = bytes.subarray(0, Math.min(64, bytes.length));
    await manager.handle(chunkRequest(begin.UploadId, 0, first), context);
    const retry = await manager.handle(chunkRequest(begin.UploadId, 0, first), context);
    assert.equal(retry.Duplicate, true);

    const changed = Buffer.from(first);
    changed[0] = changed[0] === 123 ? 91 : 123;
    await assert.rejects(
        manager.handle(chunkRequest(begin.UploadId, 0, changed), context),
        error => error instanceof RlTelemetryUploadError && error.code === 'chunk-content-conflict',
    );
});

test('upload ownership is bound to authenticated user and connection', async t => {
    const { manager, context } = await fixture(t);
    const bytes = payloadBytes();
    const begin = await manager.handle(beginRequest(bytes), context);
    await assert.rejects(
        manager.handle(chunkRequest(begin.UploadId, 0, bytes), { userId: context.userId, connectionId: '18' }),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 403 && error.code === 'upload-owner-mismatch',
    );
});

test('one user cannot fork an active match identity across reconnects', async t => {
    const { manager, context } = await fixture(t);
    const first = payloadBytes();
    await manager.handle(beginRequest(first), context);

    const conflicting = payloadBytes({ result: 'draw' });
    await assert.rejects(
        manager.handle(beginRequest(conflicting), { userId: context.userId, connectionId: '18' }),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 409 && error.code === 'match-id-conflict',
    );
});

test('incompatible policy is rejected before a partial file is allocated', async t => {
    const { root, manager, context } = await fixture(t);
    const bytes = payloadBytes();
    await assert.rejects(
        manager.handle(beginRequest(bytes, { PolicySignature: 'stale-policy' }), context),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 409 && error.code === 'incompatible-policy',
    );
    assert.deepEqual(await fsp.readdir(path.join(root, 'partial')), []);
});

test('completed JSON identity must match the authenticated begin declaration', async t => {
    const { root, manager, context } = await fixture(t);
    const bytes = payloadBytes({ deployment_id: `deploy-${'c'.repeat(24)}` });
    const begin = await manager.handle(beginRequest(bytes, { DeploymentId: `deploy-${'b'.repeat(24)}` }), context);
    await manager.handle(chunkRequest(begin.UploadId, 0, bytes), context);
    await assert.rejects(
        manager.handle({ Type: 'rl-telemetry-complete', UploadId: begin.UploadId }, context),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 409 && error.code === 'payload-identity-mismatch',
    );
    assert.deepEqual(await fsp.readdir(path.join(root, 'incoming')), []);
});

test('bad final hash and malformed JSON never enter quarantine', async t => {
    const { root, manager, context } = await fixture(t);
    const bytes = payloadBytes();
    const badHash = await manager.handle(beginRequest(bytes, { PayloadSha256: '0'.repeat(64) }), context);
    await manager.handle(chunkRequest(badHash.UploadId, 0, bytes), context);
    await assert.rejects(
        manager.handle({ Type: 'rl-telemetry-complete', UploadId: badHash.UploadId }, context),
        error => error instanceof RlTelemetryUploadError && error.code === 'payload-hash-mismatch',
    );

    const malformed = Buffer.from('{bad-json', 'utf8');
    const malformedBegin = await manager.handle(beginRequest(payloadBytes(), {
        MatchId: 'malformed-match',
        TotalBytes: malformed.length,
        PayloadSha256: sha256(malformed),
    }), context);
    await manager.handle(chunkRequest(malformedBegin.UploadId, 0, malformed), context);
    await assert.rejects(
        manager.handle({ Type: 'rl-telemetry-complete', UploadId: malformedBegin.UploadId }, context),
        error => error instanceof RlTelemetryUploadError && error.code === 'invalid-payload',
    );
    assert.deepEqual(await fsp.readdir(path.join(root, 'incoming')), []);
});

test('declared byte quota and active-session limits are enforced before upload growth', async t => {
    const { manager, context } = await fixture(t, { userBytesPerWindow: 1024 * 1024, maxActiveUploadsPerUser: 1 });
    const bytes = payloadBytes();
    await manager.handle(beginRequest(bytes), context);
    await assert.rejects(
        manager.handle(beginRequest(payloadBytes({ match_id: 'match-public-2' })), context),
        error => error instanceof RlTelemetryUploadError && error.code === 'too-many-active-uploads',
    );

    const { manager: quotaManager, context: quotaContext } = await fixture(t, { userBytesPerWindow: bytes.length - 1 });
    await assert.rejects(
        quotaManager.handle(beginRequest(bytes), quotaContext),
        error => error instanceof RlTelemetryUploadError && error.code === 'upload-rate-limit',
    );
});

test('completed exact upload deduplicates on a later begin', async t => {
    const { manager, context } = await fixture(t);
    const bytes = payloadBytes();
    const completed = await complete(manager, context, bytes);
    const retry = await manager.handle(beginRequest(bytes), context);
    assert.equal(retry.Completed, true);
    assert.equal(retry.Duplicate, true);
    assert.equal(retry.BatchId, completed.BatchId);
});

test('completed match identity cannot be reused for different content', async t => {
    const { manager, context } = await fixture(t);
    await complete(manager, context, payloadBytes());
    const conflicting = payloadBytes({ result: 'draw' });
    await assert.rejects(
        manager.handle(beginRequest(conflicting), context),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 409 && error.code === 'archive-conflict',
    );
});

test('completed dedupe revalidates immutable payload bytes before reporting success', async t => {
    const { root, manager, context } = await fixture(t);
    const bytes = payloadBytes();
    const completed = await complete(manager, context, bytes);
    const payloadPath = path.join(root, 'incoming', `${completed.BatchId}.json`);
    await fsp.writeFile(payloadPath, 'corrupt');

    await assert.rejects(
        manager.handle(beginRequest(bytes), context),
        error => error instanceof RlTelemetryUploadError && error.statusCode === 500 && error.code === 'archive-corrupt',
    );
});
