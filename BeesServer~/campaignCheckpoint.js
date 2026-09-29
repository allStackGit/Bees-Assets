'use strict';

const { installRlTelemetryUploadSecurity } = require('./rlTelemetryUploadSecurity');
const { installRlModelDistributionSecurity } = require('./rlModelDistributionSecurity');
const { installRuntimeSecurity } = require('./security');
const { installSafeCacheWriter } = require('./cachePersistence');

const CHECKPOINT_FILE = '__campaign_checkpoint__';
const REQUIRED_FILES = [
    'user_progress',
    'saved_squads_data',
    'fleet_data',
    'campaign_saved_squads_data',
    'campaign_fleet_data',
    'challenge_saved_squads_data',
    'challenge_fleet_data',
];
const CREDENTIAL_GUARD = Symbol('beesProductionCredentialGuard');

function parseCheckpoint(contents) {
    const checkpoint = JSON.parse(contents);
    if (!checkpoint || typeof checkpoint !== 'object' || Array.isArray(checkpoint)) throw new TypeError('Profile checkpoint must be a JSON object.');
    for (const filename of REQUIRED_FILES) {
        if (typeof checkpoint[filename] !== 'string') throw new TypeError(`Profile checkpoint is missing string payload '${filename}'.`);
    }
    return checkpoint;
}

async function storeCampaignCheckpoint(user, contents) {
    if (!user?.db?.transaction) throw new TypeError('Profile checkpoint storage requires transactional database access.');
    const checkpoint = parseCheckpoint(contents);
    await user.db.transaction(async query => {
        const fileOwners = REQUIRED_FILES.map(filename => ({ filename, owner: user.determineUserId(filename) }));
        const owners = [...new Set(fileOwners.map(item => item.owner))];
        const existing = await query(
            'SELECT userId, filename FROM stored_user_data WHERE userId IN (?) AND filename IN (?)',
            [owners, REQUIRED_FILES]);
        const existingFiles = new Set((existing || []).map(row => `${String(row.userId)}\u0000${row.filename}`));

        for (const { filename, owner } of fileOwners) {
            if (owner != 2 && existingFiles.has(`${String(owner)}\u0000${filename}`)) {
                await query('UPDATE stored_user_data SET contents = ? WHERE userId = ? AND filename = ?', [checkpoint[filename], owner, filename]);
            } else {
                await query('INSERT INTO stored_user_data (userId, filename, contents) VALUES ?', [[[owner, filename, checkpoint[filename]]]]);
            }
        }
    });
    return true;
}

function installProductionCredentialGuard(runtime) {
    if (!runtime?.Server || runtime[CREDENTIAL_GUARD]) return;
    runtime[CREDENTIAL_GUARD] = true;
    const SecuredServer = runtime.Server;
    runtime.Server = class CredentialGuardedServer extends SecuredServer {
        constructor(test, ...args) {
            if (!test && (!process.env.BEES_DB_USER || !process.env.BEES_DB_PASSWORD)) {
                throw new Error('Production BeesServer requires BEES_DB_USER and BEES_DB_PASSWORD.');
            }
            super(test, ...args);
        }
    };
}

function installCampaignCheckpoint(runtime) {
    if (!runtime?.User?.prototype) throw new TypeError('Legacy runtime User class is unavailable.');
    const prototype = runtime.User.prototype;
    if (!prototype.__beesCampaignCheckpointInstalled) {
        const originalStoreData = prototype.storeData;
        prototype.storeData = async function storeDataWithCampaignCheckpoint(filename, contents) {
            if (filename === CHECKPOINT_FILE) return storeCampaignCheckpoint(this, contents);
            return originalStoreData.call(this, filename, contents);
        };
        Object.defineProperty(prototype, '__beesCampaignCheckpointInstalled', { value: true });
    }

    // Install auxiliary RL routes below the existing security layer. Security remains outermost so
    // Steam authentication/claimed-user checks run before telemetry is accepted or model bytes served.
    installRlTelemetryUploadSecurity(runtime);
    installRlModelDistributionSecurity(runtime);
    installRuntimeSecurity(runtime);
    installSafeCacheWriter(runtime);
    installProductionCredentialGuard(runtime);
}

module.exports = { CHECKPOINT_FILE, REQUIRED_FILES, parseCheckpoint, storeCampaignCheckpoint, installCampaignCheckpoint, installProductionCredentialGuard };
