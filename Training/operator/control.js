'use strict';

const {
    paths,
    requestJson,
    sleep,
} = require('./common');

function getObject(value, key, fallback = null) {
    if (!value || typeof value !== 'object') return fallback;
    return Object.prototype.hasOwnProperty.call(value, key) ? value[key] : fallback;
}

async function getStatus(config, adminToken) {
    return requestJson(config.controlUrl, adminToken, 'GET', '/v1/status');
}

async function setDesiredState(config, adminToken, payload) {
    return requestJson(config.controlUrl, adminToken, 'POST', '/v1/admin/state', payload);
}

async function publishRelease(config, adminToken, release) {
    for (const artifact of release.artifacts || []) {
        const fs = require('node:fs');
        if (!fs.existsSync(String(artifact.archive || ''))) {
            throw new Error('Release artifact is missing: ' + String(artifact.archive || ''));
        }
        await requestJson(config.controlUrl, adminToken, 'POST', '/v1/admin/artifact', {
            role: String(artifact.role),
            platform: String(artifact.platform),
            build_id: String(release.build_id),
            archive_path: String(artifact.archive),
            entrypoint: String(artifact.entrypoint),
        });
    }
}

async function stageRelease(
    config,
    adminToken,
    release,
    environmentArgs,
    environmentValidationKey = '',
) {
    const body = {
        build_id: String(release.build_id),
        run_id: String(release.run_id),
        compatibility_key: String(release.compatibility_key),
        incompatible: Boolean(release.incompatible),
    };
    if (environmentArgs !== undefined) body.environment_args = [...environmentArgs].map(String);
    if (environmentValidationKey) body.environment_validation_key = String(environmentValidationKey);
    return requestJson(config.controlUrl, adminToken, 'POST', '/v1/admin/release', body);
}

function rolloutTrainerRecord(records, trainerId) {
    return records.find(record => String(record.trainer_id || '') === trainerId) || null;
}

function rolloutRequirementSatisfied(phase, phaseRevision, requiredTrainer, record, buildId) {
    if (!record) return false;
    const stale = Boolean(record.stale);
    const state = String(record.process_state || '');
    const build = String(record.build_id || '');
    const prepared = String(record.prepared_build_id || '');
    const revision = Number(record.applied_revision);
    const error = String(record.last_error || '');
    if (phase === 'preparing') {
        return !stale && (build === buildId || prepared === buildId);
    }
    if (phase === 'rolling') {
        return !stale &&
            state === 'running' &&
            build === buildId &&
            !error &&
            (phaseRevision == null || revision >= Number(phaseRevision));
    }
    if (phase === 'stopping') {
        return !stale &&
            state === 'stopped' &&
            (phaseRevision == null || revision >= Number(phaseRevision));
    }
    return false;
}

function rolloutRequirementDetail(requiredTrainer, record) {
    const trainerId = String(requiredTrainer.trainer_id || '');
    if (!record) return trainerId + ':missing';
    let detail = trainerId + ':' + String(record.process_state || '');
    if (record.stale) detail += '(STALE)';
    detail += ' build=' + (record.build_id || '-');
    if (record.prepared_build_id) detail += ' prepared=' + record.prepared_build_id;
    if (record.applied_revision != null) detail += ' rev=' + record.applied_revision;
    if (record.last_error) detail += ' error=' + record.last_error;
    return detail;
}

async function waitReleaseRollout(
    config,
    adminToken,
    buildId,
    runId,
    compatibilityKey,
    timeoutSeconds = 600,
) {
    const deadline = Date.now() + timeoutSeconds * 1000;
    let lastProgress = '';
    let lastProgressAt = 0;

    while (Date.now() < deadline) {
        const status = await getStatus(config, adminToken);
        const desired = status.desired || {};
        const pending = desired.pending_release;

        if (!pending &&
            String(desired.canonical_build_id || '') === buildId &&
            String(desired.run_id || '') === runId &&
            String(desired.compatibility_key || '') === compatibilityKey) {
            console.log('Release rollout complete: build=' + buildId + ' run=' + runId + '.');
            return status;
        }

        if (!pending) {
            throw new Error(
                'Release rollout ended without activating the expected identity. expected build=' +
                buildId + ' run=' + runId + '; active build=' +
                String(desired.canonical_build_id || '') + ' run=' + String(desired.run_id || '') + '.'
            );
        }

        const pendingBuild = String(pending.build_id || '').trim();
        const pendingRun = String(pending.run_id || '').trim();
        const pendingKey = String(pending.compatibility_key || '').trim().toLowerCase();
        if (pendingBuild !== buildId || pendingRun !== runId || pendingKey !== compatibilityKey) {
            throw new Error(
                'A different release became pending while waiting. expected build=' + buildId +
                ' run=' + runId + '; pending build=' + pendingBuild + ' run=' + pendingRun + '.'
            );
        }

        const phase = String(pending.phase || '');
        const phaseRevision = pending.phase_revision;
        const required = Array.isArray(pending.required_trainers) ? pending.required_trainers : [];
        const trainerRecords = Array.isArray(status.trainers) ? status.trainers : [];
        const waiting = [];
        for (const requiredTrainer of required) {
            const trainerId = String(requiredTrainer.trainer_id || '');
            const record = rolloutTrainerRecord(trainerRecords, trainerId);
            if (!rolloutRequirementSatisfied(phase, phaseRevision, requiredTrainer, record, buildId)) {
                waiting.push(rolloutRequirementDetail(requiredTrainer, record));
            }
        }

        const centralFailure = trainerRecords.find(record =>
            String(record.trainer_id || '') === 'central-learner' &&
            String(record.process_state || '') === 'stopped' &&
            String(record.last_error || '')
        );
        if (centralFailure) {
            const centralError = String(centralFailure.last_error || '');
            if (/^managed process exited with code /.test(centralError)) {
                throw new Error(
                    'Central learner failed while rolling release ' + buildId + ': ' + centralError +
                    '. See ' + paths.logsRoot + '\\Training\\central-agent.err.log and central-agent.out.log.'
                );
            }
        }

        const progress =
            'Waiting for release rollout: phase=' + phase + ' remaining=' +
            (waiting.length ? waiting.join('; ') : 'control state advancing');
        const now = Date.now();
        if (progress !== lastProgress || now - lastProgressAt >= 10000) {
            console.log(progress);
            lastProgress = progress;
            lastProgressAt = now;
        }
        await sleep(1000);
    }

    throw new Error('Timed out waiting for release ' + buildId + ' run=' + runId + ' to finish coordinated rollout.');
}

module.exports = {
    getObject,
    getStatus,
    publishRelease,
    setDesiredState,
    stageRelease,
    waitReleaseRollout,
};
