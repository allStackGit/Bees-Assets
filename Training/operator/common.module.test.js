'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { rotateManagedLog } = require('./common');

test('rotateManagedLog isolates the previous supervisor generation', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bees-operator-log-'));
    try {
        const log = path.join(root, 'agent.err.log');
        fs.writeFileSync(log + '.previous', 'older', 'utf8');
        fs.writeFileSync(log, 'current-generation', 'utf8');

        rotateManagedLog(log);

        assert.equal(fs.existsSync(log), false);
        assert.equal(fs.readFileSync(log + '.previous', 'utf8'), 'current-generation');

        fs.writeFileSync(log, 'next-generation', 'utf8');
        rotateManagedLog(log);

        assert.equal(fs.existsSync(log), false);
        assert.equal(fs.readFileSync(log + '.previous', 'utf8'), 'next-generation');
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
