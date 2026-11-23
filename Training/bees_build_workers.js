#!/usr/bin/env node
'use strict';

// Compile distributable Unity RL worker binaries without starting BeesServer,
// changing run state, or publishing an unqualified training release.
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
    exists,
    getGitShortSha,
    paths,
    readText,
    resolveCommand,
    resolveUnityEditor,
    runSync,
} = require('./operator/common');
const {
    assertUnityProjectAvailableForBatchBuild,
    invokeUnityBuild,
    packageBuild,
} = require('./operator/build');

const TARGETS = Object.freeze({
    linux: {
        method: 'BeesCommandLineBuild.BuildLinuxRl',
        entrypoint: 'Bees RL Training.x86_64',
        folder: 'Linux',
        archive: 'rl-linux.zip',
        platform: 'LinuxPlayer',
    },
    windows: {
        method: 'BeesCommandLineBuild.BuildWindowsRl',
        entrypoint: 'Bees RL Training.exe',
        folder: 'Windows',
        archive: 'rl-windows.zip',
        platform: 'WindowsPlayer',
    },
});

function usage() {
    return [
        'Usage: node Training/bees_build_workers.js [--platform linux|windows|both]',
        '       [--unity-editor /absolute/path/to/Editor/Unity] [--python EXECUTABLE]',
        '',
        'Builds one or both Unity RL simulation workers, packages ZIPs and writes',
        'an artifact manifest under ../Builds/OfflineWorkers/. No server, learner,',
        'experiment, checkpoint, remote worker or active release is modified.',
        'The Windows target requires Windows Build Support installed in Unity.',
    ].join('\n');
}

function options(argv) {
    const result = { platform: 'linux', editor: '', python: '' };
    for (let i = 0; i < argv.length; i++) {
        if (argv[i] === '-h' || argv[i] === '--help') return { help: true };
        const name = argv[i], value = argv[i + 1];
        if (name === '--platform' && value) {
            if (!['linux', 'windows', 'both'].includes(value)) {
                throw Error('Expected --platform linux, windows, or both.');
            }
            result.platform = value;
        } else if (name === '--unity-editor' && value) {
            if (!path.isAbsolute(value)) throw Error('--unity-editor must be an absolute path.');
            result.editor = value;
        } else if (name === '--python' && value) {
            result.python = value;
        } else {
            throw Error('Unknown or incomplete argument: ' + name + '\n' + usage());
        }
        i++;
    }
    return result;
}

function projectVersion() {
    const required = [
        path.join(paths.beesRoot, 'ProjectSettings', 'ProjectVersion.txt'),
        path.join(paths.beesRoot, 'Packages', 'manifest.json'),
        path.join(paths.assetsRoot, 'Editor', 'BeesCommandLineBuild.cs'),
    ];
    const missing = required.filter(file => !exists(file));
    if (missing.length) {
        throw Error(
            'The Unity project is incomplete. The repository checkout must be ' +
            'the Assets/ directory of a complete Unity project (with sibling ' +
            'Packages/ and ProjectSettings/). Missing:\n' + missing.join('\n')
        );
    }
    const text = readText(required[0]);
    const match = text.match(/^m_EditorVersion:\s*(\S+)/m);
    if (!match) throw Error('ProjectSettings/ProjectVersion.txt has no Unity Editor version.');
    return match[1];
}

function dirtySource() {
    const result = runSync('git', ['status', '--porcelain', '--untracked-files=normal'], {
        cwd: paths.assetsRoot,
        timeout: 30000,
        maxBuffer: 2 * 1024 * 1024,
        check: false,
    });
    return result.status === 0 ? Boolean(String(result.stdout || '').trim()) : null;
}

async function sha256(file) {
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
    return hash.digest('hex');
}

async function build(argv = process.argv.slice(2)) {
    const arg = options(argv);
    if (arg.help) {
        console.log(usage());
        return null;
    }
    const version = projectVersion();
    const editor = resolveUnityEditor({ unityEditor: arg.editor });
    const python = arg.python
        ? resolveCommand(arg.python)
        : resolveCommand(process.platform === 'win32' ? 'python' : 'python3');
    assertUnityProjectAvailableForBatchBuild();

    const targets = arg.platform === 'both'
        ? [TARGETS.linux, TARGETS.windows]
        : [TARGETS[arg.platform]];
    const commit = getGitShortSha();
    const sourceDirty = dirtySource();
    const packagesManifest = path.join(paths.beesRoot, 'Packages', 'manifest.json');
    const packagesSha = await sha256(packagesManifest);
    const timestamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
    const buildId = 'offline-unity-' + timestamp + '-' + commit + '-' +
        crypto.randomBytes(3).toString('hex');
    const root = path.join(paths.buildsRoot, 'OfflineWorkers', buildId);
    fs.mkdirSync(root, { recursive: true });
    console.log('Building Bees Unity ' + version + ' workers: ' +
        targets.map(target => target.platform).join(', '));
    console.log('Output: ' + root);
    const artifacts = [];

    for (const target of targets) {
        const folder = path.join(root, target.folder);
        await invokeUnityBuild(
            editor, target.method, folder, target.entrypoint,
            buildId + '-' + target.folder.toLowerCase() + '.log',
        );
        const archive = path.join(root, target.archive);
        packageBuild(python, folder, archive, target.entrypoint);
        const stat = fs.statSync(archive);
        artifacts.push({
            role: 'dedicated',
            platform: target.platform,
            folder: target.folder,
            archive: target.archive,
            entrypoint: target.entrypoint,
            archive_sha256: await sha256(archive),
            archive_bytes: stat.size,
        });
    }

    if (getGitShortSha() !== commit ||
        (await sha256(packagesManifest)) !== packagesSha ||
        dirtySource() !== sourceDirty) {
        throw Error('Unity source or project packages changed during the worker build; artifacts were not published.');
    }
    const manifest = {
        schema_version: 1,
        kind: 'offline-unity-workers',
        deployment_status: 'unpublished',
        build_id: buildId,
        created_utc: new Date().toISOString(),
        source_commit: commit,
        source_dirty: sourceDirty,
        unity_version: version,
        packages_manifest_sha256: packagesSha,
        artifacts,
    };
    const manifestPath = path.join(root, 'worker-build-manifest.json');
    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + os.EOL, {
        encoding: 'utf8',
        flag: 'wx',
    });
    console.log('Worker build completed. Manifest: ' + manifestPath);
    console.log('These artifacts have not been published to any training cluster.');
    return manifest;
}

if (require.main === module) {
    build().catch(error => {
        console.error(error && error.stack ? error.stack : String(error));
        process.exitCode = 1;
    });
}

module.exports = { build, options, projectVersion, TARGETS };
