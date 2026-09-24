'use strict';

const http = require('node:http');
const https = require('node:https');
const fs = require('node:fs');

function usage() {
    return [
        'Usage:',
        '  node trainingControlCli.js status',
        '  node trainingControlCli.js start [--env-arg VALUE ...]',
        '  node trainingControlCli.js stop',
        '  node trainingControlCli.js set-args [--env-arg VALUE ...]',
        '  node trainingControlCli.js publish-build --platform P --build-id ID --archive PATH --entrypoint RELATIVE_PATH',
        '',
        'Environment:',
        '  BEES_TRAINING_CONTROL_URL   default http://127.0.0.1:7150',
        '  BEES_TRAINING_CONTROL_ADMIN_TOKEN or BEES_TRAINING_CONTROL_ADMIN_TOKEN_FILE',
        '  (status also accepts the worker token variables as fallback)',
    ].join('\n');
}

function tokenFromEnvironment() {
    if (process.env.BEES_TRAINING_CONTROL_ADMIN_TOKEN) {
        return process.env.BEES_TRAINING_CONTROL_ADMIN_TOKEN;
    }
    if (process.env.BEES_TRAINING_CONTROL_ADMIN_TOKEN_FILE) {
        return fs.readFileSync(process.env.BEES_TRAINING_CONTROL_ADMIN_TOKEN_FILE, 'utf8').trim();
    }
    if (process.env.BEES_TRAINING_CONTROL_TOKEN) {
        return process.env.BEES_TRAINING_CONTROL_TOKEN;
    }
    if (process.env.BEES_TRAINING_CONTROL_TOKEN_FILE) {
        return fs.readFileSync(process.env.BEES_TRAINING_CONTROL_TOKEN_FILE, 'utf8').trim();
    }
    throw new Error('training-control admin token is required.');
}

function parseOptions(argv) {
    const command = argv[0];
    const values = { envArgs: [] };
    for (let index = 1; index < argv.length; index++) {
        const arg = argv[index];
        const next = argv[index + 1];
        if (arg === '--env-arg') {
            if (next === undefined) throw new Error('--env-arg requires a value');
            values.envArgs.push(next);
            index++;
        } else if (arg === '--platform' || arg === '--build-id' ||
                   arg === '--archive' || arg === '--entrypoint') {
            if (next === undefined) throw new Error(arg + ' requires a value');
            values[arg.slice(2).replace('-', '_')] = next;
            index++;
        } else {
            throw new Error('unknown option ' + arg);
        }
    }
    return { command, values };
}

function requestJson(baseUrl, token, method, path, payload = null) {
    const url = new URL(path, baseUrl.endsWith('/') ? baseUrl : baseUrl + '/');
    const transport = url.protocol === 'https:' ? https : http;
    const body = payload === null ? null : Buffer.from(JSON.stringify(payload) + '\n', 'utf8');
    return new Promise((resolve, reject) => {
        const request = transport.request(url, {
            method,
            headers: {
                Authorization: 'Bearer ' + token,
                Accept: 'application/json',
                ...(body ? {
                    'Content-Type': 'application/json',
                    'Content-Length': body.length,
                } : {}),
            },
        }, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(chunk));
            response.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                let value;
                try {
                    value = text ? JSON.parse(text) : {};
                } catch (error) {
                    reject(new Error('Control server returned invalid JSON: ' + text));
                    return;
                }
                if (response.statusCode < 200 || response.statusCode >= 300) {
                    reject(new Error('Control server HTTP ' + response.statusCode + ': ' + text));
                    return;
                }
                resolve(value);
            });
        });
        request.on('error', reject);
        if (body) request.write(body);
        request.end();
    });
}

async function main(argv = process.argv.slice(2)) {
    if (argv.length === 0 || argv[0] === '--help' || argv[0] === '-h') {
        console.log(usage());
        return 0;
    }
    const { command, values } = parseOptions(argv);
    const baseUrl = process.env.BEES_TRAINING_CONTROL_URL || 'http://127.0.0.1:7150';
    const token = tokenFromEnvironment();

    let result;
    if (command === 'status') {
        result = await requestJson(baseUrl, token, 'GET', '/v1/status');
    } else if (command === 'start') {
        const patch = { training_enabled: true };
        if (values.envArgs.length) patch.environment_args = values.envArgs;
        result = await requestJson(baseUrl, token, 'POST', '/v1/admin/state', patch);
    } else if (command === 'stop') {
        result = await requestJson(
            baseUrl, token, 'POST', '/v1/admin/state', { training_enabled: false });
    } else if (command === 'set-args') {
        result = await requestJson(
            baseUrl, token, 'POST', '/v1/admin/state', { environment_args: values.envArgs });
    } else if (command === 'publish-build') {
        for (const key of ['platform', 'build_id', 'archive', 'entrypoint']) {
            if (!values[key]) throw new Error('publish-build requires --' + key.replace('_', '-'));
        }
        result = await requestJson(baseUrl, token, 'POST', '/v1/admin/artifact', {
            platform: values.platform,
            build_id: values.build_id,
            archive_path: values.archive,
            entrypoint: values.entrypoint,
        });
    } else {
        throw new Error('unknown command ' + command + '\n' + usage());
    }

    console.log(JSON.stringify(result, null, 2));
    return 0;
}

if (require.main === module) {
    main().then(
        code => { process.exitCode = code; },
        error => {
            console.error(error.message);
            process.exitCode = 1;
        });
}

module.exports = { main, parseOptions, requestJson, tokenFromEnvironment, usage };
