'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { cleanupStaleValidationCandidates } = require('./validation');

test('stale RL validation candidates are removed without touching fresh candidates', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-validation-cleanup-'));
    try {
        const stale = path.join(root, 'candidate-stale');
        const fresh = path.join(root, 'candidate-fresh');
        const unrelated = path.join(root, 'keep-me');
        fs.mkdirSync(stale);
        fs.mkdirSync(fresh);
        fs.mkdirSync(unrelated);
        fs.writeFileSync(path.join(stale, 'file.txt'), 'stale');
        fs.writeFileSync(path.join(fresh, 'file.txt'), 'fresh');

        const now = Date.now();
        const old = new Date(now - 11 * 60 * 1000);
        fs.utimesSync(stale, old, old);

        const removed = cleanupStaleValidationCandidates(root, now);

        assert.equal(removed, 1);
        assert.equal(fs.existsSync(stale), false);
        assert.equal(fs.existsSync(fresh), true);
        assert.equal(fs.existsSync(unrelated), true);
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
