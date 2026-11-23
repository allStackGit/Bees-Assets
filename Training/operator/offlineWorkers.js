'use strict';

// A compiled Unity worker release can be transferred between machines without
// letting a remote file bypass Bees' canonical release, run and environment
// validation. The Windows Bees operator remains the release owner.
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { runChecked, paths, readJson, readText } = require('./common');

const DEFINITIONS = Object.freeze([
    { platform: 'WindowsPlayer', folder: 'Windows', archive: 'rl-windows.zip', entrypoint: 'Bees RL Training.exe' },
    { platform: 'LinuxPlayer', folder: 'Linux', archive: 'rl-linux.zip', entrypoint: 'Bees RL Training.x86_64' },
]);
const SHA256 = /^[0-9a-f]{64}$/i;

async function hashFile(filename) {
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(filename)) hash.update(chunk);
    return hash.digest('hex');
}

function cleanCheckout() {
    const result = require('node:child_process').spawnSync(
        'git', ['status', '--porcelain', '--untracked-files=normal'],
        { cwd: paths.assetsRoot, encoding: 'utf8', windowsHide: true, timeout: 30000, maxBuffer: 4 * 1024 * 1024 },
    );
    if (result.error || result.status !== 0 || String(result.stdout || '').trim()) {
        throw Error(
            'Offline worker import requires the central Bees-Assets checkout to match ' +
            'a committed revision with no local source modifications. ' +
            'Commit/sync the Unity changes before importing.'
        );
    }
}

function checkedPackagePath(root, filename) {
    const full = path.join(root, filename);
    const stat = fs.lstatSync(full);
    if (!stat.isFile() || stat.isSymbolicLink()) {
        throw Error('Offline worker archive must be a regular file: ' + full);
    }
    return full;
}

async function inspectOfflineWorkers(manifestPath, sourceSha) {
    if (process.platform !== 'win32') {
        throw Error(
            'The current Bees release/learner control is Windows-owned. ' +
            'Import offline workers on the Windows central Bees operator, ' +
            'not the Linux Experiment Hub.'
        );
    }
    if (!path.isAbsolute(String(manifestPath || ''))) {
        throw Error('Offline worker manifest path must be absolute.');
    }
    const filepath = path.resolve(manifestPath);
    const root = path.dirname(filepath);
    if (path.basename(filepath) !== 'worker-build-manifest.json') {
        throw Error('Expected worker-build-manifest.json from the Halifax builder.');
    }
    const manifest = readJson(filepath);
    if (manifest.schema_version !== 1 ||
        manifest.kind !== 'offline-unity-workers' ||
        manifest.deployment_status !== 'unpublished' ||
        !Array.isArray(manifest.artifacts) ||
        manifest.artifacts.length !== DEFINITIONS.length) {
        throw Error('Unsupported or incomplete offline Unity worker manifest.');
    }
    if (manifest.source_dirty !== false) {
        throw Error('Worker build was made with uncommitted/unknown source modifications; rebuild from a clean committed checkout.');
    }
    if (manifest.source_commit !== sourceSha) {
        throw Error(
            'Worker build source commit ' + manifest.source_commit +
            ' does not match central Bees-Assets checkout ' + sourceSha +
            '. Sync the same committed Unity source before importing.'
        );
    }
    const versionFile = path.join(paths.beesRoot, 'ProjectSettings', 'ProjectVersion.txt');
    const match = readText(versionFile).match(/^m_EditorVersion:\s*(\S+)/m);
    if (!match || manifest.unity_version !== match[1]) {
        throw Error('Offline worker Unity version differs from this Bees project.');
    }
    const packagesSha = await hashFile(path.join(paths.beesRoot, 'Packages', 'manifest.json'));
    if (!SHA256.test(String(manifest.packages_manifest_sha256 || '')) ||
        packagesSha !== String(manifest.packages_manifest_sha256).toLowerCase()) {
        throw Error('Offline worker Unity Packages/manifest.json does not match this project.');
    }
    cleanCheckout();

    const selected = [];
    for (const expected of DEFINITIONS) {
        const records = manifest.artifacts.filter(item =>
            item?.role === 'dedicated' && item.platform === expected.platform);
        if (records.length !== 1) {
            throw Error('Offline worker release must contain exactly one ' + expected.platform + ' binary.');
        }
        const item = records[0];
        if (item.folder !== expected.folder ||
            item.archive !== expected.archive ||
            item.entrypoint !== expected.entrypoint ||
            !SHA256.test(String(item.archive_sha256 || ''))) {
            throw Error('Invalid offline worker artifact identity for ' + expected.platform + '.');
        }
        const archive = checkedPackagePath(root, expected.archive);
        if (Number(item.archive_bytes) !== fs.statSync(archive).size ||
            (await hashFile(archive)) !== String(item.archive_sha256).toLowerCase()) {
            throw Error('Offline worker archive does not match the manifest: ' + expected.archive);
        }
        selected.push({
            ...expected,
            source: archive,
            archive_sha256: String(item.archive_sha256).toLowerCase(),
            archive_bytes: Number(item.archive_bytes),
        });
    }
    return { manifestPath: filepath, artifacts: selected };
}

function extractWorkerArchive(python, archive, destination, entrypoint) {
    const code = [
        'import pathlib,stat,sys,zipfile',
        'source=pathlib.Path(sys.argv[1]); dest=pathlib.Path(sys.argv[2]); entry=sys.argv[3]',
        'with zipfile.ZipFile(source) as bundle:',
        '  names=set(); total=0; members=bundle.infolist()',
        '  if len(members)>100000: raise ValueError("too many Unity archive entries")',
        '  for item in members:',
        '    name=item.filename; parts=pathlib.PurePosixPath(name).parts',
        '    if not name or name.startswith("/") or "\\\\" in name or "\\x00" in name or ":" in name or ".." in parts or name in names:',
        '      raise ValueError("unsafe/duplicate worker archive path")',
        '    mode=item.external_attr>>16',
        '    if stat.S_ISLNK(mode): raise ValueError("symbolic link in worker archive")',
        '    total+=item.file_size',
        '    if total>16*1024**3: raise ValueError("worker archive exceeds 16GiB unpacked")',
        '    names.add(name)',
        '  if entry not in names: raise ValueError("missing Unity worker entrypoint")',
        '  bundle.extractall(dest)',
        'if not (dest/entry).is_file(): raise ValueError("Unity worker entrypoint not extracted")',
    ].join('\n');
    runChecked(python, ['-c', code, archive, destination, entrypoint], paths.assetsRoot);
}

async function installOfflineWorkers(checked, python, packageRoot) {
    // Keep a private copy under the versioned release root: later upgrades
    // must not depend on a removable share or a still-changing source folder.
    const result = [];
    for (const expected of checked.artifacts) {
        const archive = path.join(packageRoot, expected.archive);
        const staging = archive + '.part';
        const directory = path.join(packageRoot, expected.folder);
        fs.copyFileSync(expected.source, staging, fs.constants.COPYFILE_EXCL);
        if (fs.statSync(staging).size !== expected.archive_bytes ||
            await hashFile(staging) !== expected.archive_sha256) {
            fs.rmSync(staging, { force: true });
            throw Error('Offline Unity archive changed during transfer: ' + expected.archive);
        }
        fs.renameSync(staging, archive);
        fs.mkdirSync(directory, { recursive: false });
        extractWorkerArchive(python, archive, directory, expected.entrypoint);
        result.push({
            role: 'dedicated',
            platform: expected.platform,
            folder: directory,
            archive,
            entrypoint: expected.entrypoint,
        });
    }
    return result;
}

module.exports = { inspectOfflineWorkers, installOfflineWorkers };
