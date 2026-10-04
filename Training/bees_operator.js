#!/usr/bin/env node
'use strict';

const {
    invokeBuild,
    invokeQualify,
    invokeRuntime,
    invokeServer,
    invokeStart,
    invokeStatus,
    invokeStop,
} = require('./operator/commands');
const { invokeBundle } = require('./operator/diagnostics');
const { invokeObserve } = require('./operator/observe');

const COMMANDS = new Set([
    'build',
    'runtime',
    'server',
    'start',
    'stop',
    'status',
    'bundle',
    'observe',
    'qualify',
]);

function usage() {
    return [
        'Usage:',
        '  node Training/bees_operator.js build [--full-game] [--force] [--preserve-run]',
        '  node Training/bees_operator.js runtime [--threaded [--policy-lag N]] [--backpressure-queue N] [--local-training]',
        '  node Training/bees_operator.js server',
        '  node Training/bees_operator.js start [--new-run] [--env-arg VALUE ...]',
        '  node Training/bees_operator.js stop [--server]',
        '  node Training/bees_operator.js status [--once] [--refresh-seconds N]',
        '  node Training/bees_operator.js bundle [--run-id ID] [--log-percent PCT] [--evaluate]',
        '  node Training/bees_operator.js observe',
        '  node Training/bees_operator.js qualify',
        '',
        'The public PowerShell shim Assets\\bees.ps1 preserves the existing operator syntax.',
    ].join('\n');
}

function requireValue(argv, index, option) {
    if (index + 1 >= argv.length) {
        throw new Error(option + ' requires a value.');
    }
    return argv[index + 1];
}

function parseNumber(value, option, minimum, maximum, integer = false) {
    const number = Number(value);
    if (!Number.isFinite(number) ||
        number < minimum ||
        number > maximum ||
        (integer && !Number.isInteger(number))) {
        throw new Error(
            option + ' must be ' +
            (integer ? 'an integer ' : '') +
            'in ' + minimum + '-' + maximum + '.'
        );
    }
    return number;
}

function parseArgs(argv = process.argv.slice(2)) {
    if (!argv.length || argv[0] === '-h' || argv[0] === '--help') {
        return { help: true };
    }

    const command = String(argv[0]).toLowerCase();
    if (!COMMANDS.has(command)) {
        throw new Error('Unknown Bees command: ' + argv[0] + '\n' + usage());
    }

    const options = {
        fullGame: false,
        force: false,
        preserveRun: false,
        newRun: false,
        threaded: false,
        policyLag: null,
        explicitPolicyLag: false,
        backpressureQueue: null,
        explicitBackpressureQueue: false,
        localTraining: false,
        envArgs: [],
        once: false,
        refreshSeconds: 2,
        server: false,
        logPercent: 10,
        runId: '',
        explicitLogPercent: false,
        explicitRunId: false,
        evaluate: false,
    };

    for (let index = 1; index < argv.length; index++) {
        const arg = String(argv[index]);
        if (arg === '--full-game') {
            options.fullGame = true;
        } else if (arg === '--force') {
            options.force = true;
        } else if (arg === '--preserve-run') {
            options.preserveRun = true;
        } else if (arg === '--new-run') {
            options.newRun = true;
        } else if (arg === '--threaded') {
            options.threaded = true;
        } else if (arg === '--policy-lag') {
            options.policyLag = parseNumber(
                requireValue(argv, index, arg),
                arg,
                0,
                2147483647,
                true,
            );
            options.explicitPolicyLag = true;
            index++;
        } else if (arg.startsWith('--policy-lag=')) {
            options.policyLag = parseNumber(
                arg.slice('--policy-lag='.length),
                '--policy-lag',
                0,
                2147483647,
                true,
            );
            options.explicitPolicyLag = true;
        } else if (arg === '--backpressure-queue') {
            options.backpressureQueue = parseNumber(
                requireValue(argv, index, arg),
                arg,
                1,
                2147483647,
                true,
            );
            options.explicitBackpressureQueue = true;
            index++;
        } else if (arg.startsWith('--backpressure-queue=')) {
            options.backpressureQueue = parseNumber(
                arg.slice('--backpressure-queue='.length),
                '--backpressure-queue',
                1,
                2147483647,
                true,
            );
            options.explicitBackpressureQueue = true;
        } else if (arg === '--local-training') {
            options.localTraining = true;
        } else if (arg === '--env-arg') {
            options.envArgs.push(String(requireValue(argv, index, arg)));
            index++;
        } else if (arg.startsWith('--env-arg=')) {
            options.envArgs.push(arg.slice('--env-arg='.length));
        } else if (arg === '--once') {
            options.once = true;
        } else if (arg === '--refresh-seconds') {
            options.refreshSeconds = parseNumber(
                requireValue(argv, index, arg),
                arg,
                1,
                60,
                true,
            );
            index++;
        } else if (arg === '--server') {
            options.server = true;
        } else if (arg === '--log-percent') {
            options.logPercent = parseNumber(
                requireValue(argv, index, arg),
                arg,
                0.1,
                100,
                false,
            );
            options.explicitLogPercent = true;
            index++;
        } else if (arg === '--run-id') {
            options.runId = String(requireValue(argv, index, arg));
            options.explicitRunId = true;
            index++;
        } else if (arg === '--evaluate') {
            options.evaluate = true;
        } else if (arg === '-h' || arg === '--help') {
            options.help = true;
        } else {
            throw new Error('Unknown option: ' + arg + '\n' + usage());
        }
    }

    if (options.preserveRun && command !== 'build') {
        throw new Error('--preserve-run is only valid with the build command.');
    }
    if (options.newRun && command !== 'start') {
        throw new Error('--new-run is only valid with the start command.');
    }
    if (options.threaded && command !== 'runtime') {
        throw new Error('--threaded is only valid with the runtime command.');
    }
    if (options.localTraining && command !== 'runtime') {
        throw new Error('--local-training is only valid with the runtime command.');
    }
    if (options.explicitPolicyLag && command !== 'runtime') {
        throw new Error('--policy-lag is only valid with the runtime command.');
    }
    if (options.explicitPolicyLag && !options.threaded) {
        throw new Error('--policy-lag requires --threaded.');
    }
    if (options.explicitBackpressureQueue && command !== 'runtime') {
        throw new Error('--backpressure-queue is only valid with the runtime command.');
    }
    if (options.explicitLogPercent && command !== 'bundle') {
        throw new Error('--log-percent is only valid with the bundle command.');
    }
    if (options.explicitRunId && command !== 'bundle') {
        throw new Error('--run-id is only valid with the bundle command.');
    }
    if (options.evaluate && command !== 'bundle') {
        throw new Error('--evaluate is only valid with the bundle command.');
    }

    return { command, options };
}

async function dispatch(parsed) {
    if (parsed.help) {
        console.log(usage());
        return 0;
    }
    const { command, options } = parsed;
    if (command === 'build') await invokeBuild(options);
    else if (command === 'runtime') await invokeRuntime(options);
    else if (command === 'server') await invokeServer(options);
    else if (command === 'start') await invokeStart(options);
    else if (command === 'stop') await invokeStop(options);
    else if (command === 'status') await invokeStatus(options);
    else if (command === 'bundle') await invokeBundle(options);
    else if (command === 'observe') await invokeObserve(options);
    else if (command === 'qualify') await invokeQualify(options);
    return 0;
}

async function main(argv = process.argv.slice(2)) {
    return dispatch(parseArgs(argv));
}

if (require.main === module) {
    main().then(
        code => {
            process.exitCode = Number(code || 0);
        },
        error => {
            console.error(error && error.stack ? error.stack : String(error));
            process.exitCode = 1;
        },
    );
}

module.exports = {
    COMMANDS,
    dispatch,
    main,
    parseArgs,
    usage,
};
