'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const packageJson = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8'));

test('fixed modular runtime remains the package main behind the launch wrapper', () => {
    assert.equal(packageJson.main, 'server.js');
    assert.equal(packageJson.scripts?.start, 'node start-server.js');
});

for (const dependency of ['mysql2', 'websocket']) {
    test(`production dependency ${dependency} is declared`, () => {
        assert.equal(typeof packageJson.dependencies?.[dependency], 'string',
            `${dependency} is required by the production server and must be a package.json runtime dependency.`);
    });
}
