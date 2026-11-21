'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const names = Object.freeze({
    runner: 'bees_experiment_benchmark.py',
    evaluator: 'bees_continual_evaluate.py',
    scripted: 'bees_experiment_scripted.py',
});
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

// Fixes an important provenance bug: hashing shared sources is not enough;
// the frozen plan must EXECUTE exactly the bytes originally hashed.
// Legacy sources are bundled for experiments queued before snapshots existed.
function freezePlan(plan, snapshotRoot) {
    const sources = path.resolve(__dirname);
    const legacy = path.join(sources, 'experiment_legacy_evaluator');
    const lookup = (label, expected) => {
        for (const file of [plan.files[label], path.join(sources, names[label]),
                path.join(legacy, names[label])].filter(Boolean)) {
            if (fs.existsSync(file) && hash(file) === expected) return file;
        }
        throw Error('Pinned evaluator source unavailable or altered (' + label +
            ', SHA-256 ' + expected + '). Preserving experiment without evaluation.');
    };
    if (!plan.hashes.scripted) {
        const scripted = path.join(sources, names.scripted);
        plan.hashes.scripted = hash(scripted);
        plan.files.scripted = scripted;
    }
    const digest = crypto.createHash('sha256')
        .update(JSON.stringify(Object.keys(names).map(x => plan.hashes[x])))
        .digest('hex');
    const dir = path.join(snapshotRoot, digest);
    for (const [label, filename] of Object.entries(names)) {
        const expected = plan.hashes[label];
        if (!/^[0-9a-f]{64}$/.test(expected || '')) {
            throw Error('Frozen evaluator hash missing for ' + label);
        }
        const target = path.join(dir, filename);
        if (!fs.existsSync(target)) {
            const source = lookup(label, expected);
            fs.mkdirSync(dir, { recursive: true });
            try { fs.copyFileSync(source, target, fs.constants.COPYFILE_EXCL); }
            catch (error) { if (error.code !== 'EEXIST') throw error; }
        }
        if (hash(target) !== expected) {
            throw Error('Content-addressed evaluator source changed: ' + target);
        }
        plan.files[label] = target;
    }
    plan.evaluator_snapshot = digest;
    return plan;
}
function supportsPurpose(plan) {
    return fs.readFileSync(plan.files.runner, 'utf8')
        .includes('add_argument("--evaluation-purpose"');
}
module.exports = { freezePlan, supportsPurpose };
