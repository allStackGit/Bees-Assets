#!/usr/bin/env node
'use strict';

// ROCm/CUDA learner qualification without installing packages, invoking Unity,
// changing an experiment, or starting any training/service process.
const path = require('node:path');
const {
    externalLearnerPython,
    preflightExternalLearner,
} = require('./operator/runtime');

function usage() {
    return [
        'Usage: BEES_LEARNER_PYTHON=/absolute/path/to/python \\',
        '       node Training/bees_learner_preflight.js [--device cuda|cpu]',
        '       node Training/bees_learner_preflight.js --python /absolute/path/to/python',
        '',
        'Performs read-only ML-Agents/PyTorch compatibility and GPU availability checks.',
        'For ROCm GPUs, PyTorch uses the cuda device API and reports backend=rocm.',
    ].join('\n');
}

function parseArgs(argv) {
    const result = { python: '', device: 'cuda' };
    for (let i = 0; i < argv.length; i++) {
        const arg = String(argv[i]);
        if (arg === '--help' || arg === '-h') return { help: true };
        if (arg !== '--python' && arg !== '--device') {
            throw Error('Unknown option ' + arg + '\n' + usage());
        }
        const value = argv[++i];
        if (!value) throw Error('Missing ' + arg + ' value.\n' + usage());
        if (arg === '--python') {
            if (!path.isAbsolute(value)) throw Error('--python requires an absolute path.');
            result.python = value;
        } else {
            if (!['cuda', 'cpu'].includes(value)) throw Error('--device must be cuda or cpu.');
            result.device = value;
        }
    }
    return result;
}

function main(argv = process.argv.slice(2)) {
    const options = parseArgs(argv);
    if (options.help) {
        console.log(usage());
        return;
    }
    const configured = { learnerPython: options.python, learnerDevice: options.device };
    const python = externalLearnerPython(configured);
    if (!python) throw Error('Set BEES_LEARNER_PYTHON or provide --python with an absolute executable path.');
    const report = preflightExternalLearner(python, configured);
    console.log(JSON.stringify(report, null, 2));
}

if (require.main === module) {
    try {
        main();
    } catch (error) {
        console.error(error.message || String(error));
        process.exitCode = 1;
    }
}

module.exports = { main, parseArgs };
