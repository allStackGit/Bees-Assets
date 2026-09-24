'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const root = path.resolve(__dirname, '..');
const requiredFiles = [
    'AGENTS.md',
    'CLAUDE.md',
    'PROJECT_CONSTITUTION.md',
    'docs/DEVELOPMENT_MEMORY.md',
    'docs/DATABASE_MODEL.md',
    'docs/LIVE_INTEGRATION_TESTING.md',
    'docs/engineering/INVARIANTS.md',
    'docs/engineering/SYSTEM_MAP.md',
    'docs/engineering/VALIDATION_POLICY.md',
    'docs/engineering/REGRESSIONS.md',
    'docs/engineering/CONTEXT_INDEX.md',
    'docs/engineering/LEARNING_STATE.md',
    'QUALITY_LEDGER.md',
    '.agents/skills/repo-learning/SKILL.md',
    '.agents/skills/continuous-learning/SKILL.md',
    '.agents/skills/search-index/SKILL.md',
    '.agents/skills/code-quality/SKILL.md',
    '.agents/skills/test-health/SKILL.md',
    '.agents/skills/bug-finding/SKILL.md',
    '.agents/skills/performance-optimization/SKILL.md',
];

function repoPath(relativePath) {
    return path.join(root, ...relativePath.split('/'));
}

test('required engineering guardrail files remain present', () => {
    const missing = requiredFiles.filter(relativePath => !fs.existsSync(repoPath(relativePath)));
    assert.deepEqual(missing, [], `Missing mandatory repository guardrail files: ${missing.join(', ')}`);
});

test('permanent regression entries require root cause, protection, and verification', () => {
    const text = fs.readFileSync(repoPath('docs/engineering/REGRESSIONS.md'), 'utf8');
    const headings = [...text.matchAll(/^### REG-\d+\s+—.*$/gm)];

    for (let index = 0; index < headings.length; index += 1) {
        const heading = headings[index];
        const start = heading.index;
        const end = index + 1 < headings.length ? headings[index + 1].index : text.length;
        const entry = text.slice(start, end);
        assert.match(entry, /\*\*Root cause:\*\*/, `${heading[0]} is missing a root-cause field`);
        assert.match(entry, /\*\*Permanent protection:\*\*/, `${heading[0]} is missing permanent protection`);
        assert.match(entry, /\*\*Verification:\*\*/, `${heading[0]} is missing verification evidence`);
    }
});

test('self-learning workflow remains wired', () => {
    const agents = fs.readFileSync(repoPath('AGENTS.md'), 'utf8');
    const claude = fs.readFileSync(repoPath('CLAUDE.md'), 'utf8');
    const repoLearning = fs.readFileSync(repoPath('.agents/skills/repo-learning/SKILL.md'), 'utf8');
    const requiredLinks = [
        'docs/engineering/CONTEXT_INDEX.md',
        '.agents/skills/continuous-learning/SKILL.md',
        '.agents/skills/search-index/SKILL.md',
        '.agents/skills/code-quality/SKILL.md',
    ];

    for (const link of requiredLinks) {
        assert.ok(agents.includes(link), `AGENTS.md no longer routes to ${link}`);
        assert.ok(repoLearning.includes(link), `repo-learning no longer wires ${link}`);
    }
    assert.ok(claude.includes('docs/engineering/CONTEXT_INDEX.md'),
        'CLAUDE.md no longer routes through the compact context index');

    const learning = fs.readFileSync(repoPath('.agents/skills/continuous-learning/SKILL.md'), 'utf8');
    for (const disposition of ['promote', 'refresh', 'defer', 'reject']) {
        assert.ok(learning.includes(disposition), `continuous-learning lost ${disposition} disposition`);
    }
});

test('context loading remains task proportional', () => {
    const agents = fs.readFileSync(repoPath('AGENTS.md'), 'utf8');
    const claude = fs.readFileSync(repoPath('CLAUDE.md'), 'utf8');
    const repoLearning = fs.readFileSync(repoPath('.agents/skills/repo-learning/SKILL.md'), 'utf8');
    const searchIndex = fs.readFileSync(repoPath('.agents/skills/search-index/SKILL.md'), 'utf8');
    const continuousLearning = fs.readFileSync(repoPath('.agents/skills/continuous-learning/SKILL.md'), 'utf8');

    assert.ok(agents.includes('only unconditional repository read'),
        'AGENTS.md no longer protects the minimal bootstrap');
    assert.ok(agents.toLowerCase().includes('stop loading context'),
        'AGENTS.md no longer defines the retrieval stop rule');
    assert.ok(agents.includes('positive context ROI'),
        'AGENTS.md no longer requires accumulated knowledge to reduce future work');
    assert.ok(claude.toLowerCase().includes('do **not** independently preload'),
        'CLAUDE.md reintroduced broad startup loading');
    assert.ok(repoLearning.includes('Stop condition'),
        'repo-learning no longer bounds focused retrieval');
    assert.ok(searchIndex.includes('Stop rule'),
        'search-index no longer stops after sufficient evidence is found');
    assert.ok(continuousLearning.includes('context ROI'),
        'continuous-learning no longer rejects context-negative knowledge growth');
});

test('context index remains compact navigation', () => {
    const text = fs.readFileSync(repoPath('docs/engineering/CONTEXT_INDEX.md'), 'utf8');
    const nonEmptyLines = text.split(/\r?\n/).filter(line => line.trim().length > 0);
    assert.ok(nonEmptyLines.length <= 400, 'Context index has become too large; move detail to owner documents');
    assert.ok(text.toLowerCase().includes('navigation, not authority'));
});
