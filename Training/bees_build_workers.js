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
const {unityWorkerTreeSha256}=require('./experiment_hub_bees_worker_identity');
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
        '       [--install-root /absolute/worker/install/root] [--activate]',
        '       [--inventory /absolute/private/machines.json] [--machine ID]...',
        '       [--apply] [--activate]',
        '',
        'Builds one or both Unity RL simulation workers, packages ZIPs and writes',
        'an artifact manifest under ../Builds/OfflineWorkers/. No server, learner,',
        'experiment, checkpoint, remote worker or active release is modified.',
        'The Windows target requires Windows Build Support installed in Unity.',
        'Every build also packages the exact matching Bees Python learner/actor runtime.',
        'With --install-root, verifies and installs the host-platform player locally.',
        'Only an explicit --activate selects it for future Hub agent starts.',
        'With --inventory, builds then previews distribution to selected machines.',
        '--apply installs on remote machines; --apply --activate also selects the',
        'release after every selected remote installation has been verified.',
        'Local --install-root and remote --inventory are separate operations.',
    ].join('\n');
}

function options(argv) {
    const result = { platform: 'linux', editor: '', python: '', installRoot: '',
        inventory: '', machines: [], apply: false, activate: false };
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
        } else if (name === '--install-root' && value) {
            if (!path.isAbsolute(value)) throw Error('--install-root must be an absolute path.');
            result.installRoot = path.resolve(value);
        } else if (name === '--inventory' && value) {
            if (!path.isAbsolute(value)) throw Error('--inventory must be an absolute path.');
            result.inventory = path.resolve(value);
        } else if (name === '--machine' && value) {
            if (!/^[a-z][a-z0-9_-]{0,47}$/.test(value))
                throw Error('--machine requires an enrolled machine ID.');
            if (result.machines.includes(value))
                throw Error('--machine cannot be selected twice: '+value);
            result.machines.push(value);
        } else if (name === '--apply') {
            result.apply = true;
            continue;
        } else if (name === '--activate') {
            result.activate = true;
            continue;
        } else {
            throw Error('Unknown or incomplete argument: ' + name + '\n' + usage());
        }
        i++;
    }
    if (result.installRoot && result.inventory)
        throw Error('Choose local --install-root OR remote --inventory, not both.');
    if (result.machines.length && !result.inventory)
        throw Error('--machine requires --inventory.');
    if (result.apply && !result.inventory)
        throw Error('--apply requires --inventory.');
    if (result.activate && !result.installRoot && !(result.inventory && result.apply))
        throw Error('--activate requires --install-root or --inventory --apply.');
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

// Unity generates .meta for tracked, non-game Hub tooling and external .NET
// helper projects. Their auto-GUIDs are not source inputs for the game.
// Exclude only metadata paths that are themselves NOT tracked in Git;
// tracked Unity/game metadata and actual source always retain their source
// identity, even on subsequent builds after the first Editor import.
function initialToolMetadata() {
    const result=runSync('git',['ls-files','-z','--','Training'],{
        cwd:paths.assetsRoot,timeout:30000,maxBuffer:16*1024*1024,check:false,
    });
    if(result.status!==0)
        throw Error('Cannot inspect tracked tooling before Unity compilation.');
    const tracked=new Set(String(result.stdout||'').split('\0').filter(Boolean));
    const ignored=new Set();
    const externalDirs=['experiment_hub_windows_owner',
        'experiment_hub_windows_service','experiment_legacy_evaluator','systemd'];
    for(const name of tracked) {
        const standalone=externalDirs.some(dir=>name.startsWith('Training/'+dir+'/'));
        if(!standalone&&
            !/^Training\/[^/]+\.(?:js|py|md|svg|html|json|sh|cmd|ps1)$/i.test(name)&&
            name!=='Training/Dockerfile.tinybrains-rocm-node')
            continue;
        if(name.endsWith('.meta'))continue;
        const candidates=[name];
        if(standalone) {
            let dir=path.posix.dirname(name);
            while(dir!=='Training') {
                candidates.push(dir);
                dir=path.posix.dirname(dir);
            }
        }
        for(const candidate of candidates) {
            const meta=candidate+'.meta';
            if(!tracked.has(meta))ignored.add(meta);
        }
    }
    return ignored;
}
function dirtySource(ignoredMeta=new Set()) {
    const result = runSync('git', ['status', '--porcelain', '--untracked-files=normal','--',
        '.',...protectedExclusions()], {
        cwd: paths.assetsRoot,
        timeout: 30000,
        maxBuffer: 2 * 1024 * 1024,
        check: false,
    });
    if(result.status!==0)return null;
    return String(result.stdout||'').split(/\r?\n/).filter(Boolean)
        .some(line=>!line.startsWith('?? ')||
            !ignoredMeta.has(line.slice(3)));
}

async function sha256(file) {
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
    return hash.digest('hex');
}

// The same manifest must never describe Linux and Windows players compiled
// from two different dirty checkout states. Git HEAD identifies committed
// sources; hash modified/untracked Assets plus the entire Unity package and
// project-settings trees before and after both target builds.
const PROTECTED=new Set(['results','.results','Demonstrations','Logs','TrainingHistory~']);
const protectedPath=name=>name.split(/[\\/]/).some(part=>PROTECTED.has(part));
const protectedExclusions=()=>[...PROTECTED].flatMap(name=>[
    ':(exclude)'+name+'/**',
    ':(exclude)**/'+name+'/**',
]);
async function sourceSnapshot(ignoredMeta=new Set()) {
    const names=new Set();
    for(const args of [
        ['diff','--name-only','-z','HEAD','--','.',...protectedExclusions()],
        ['ls-files','--others','--exclude-standard','-z','--',
            '.',...protectedExclusions()],
    ]) {
        const result=runSync('git',args,{
            cwd:paths.assetsRoot,timeout:60000,maxBuffer:16*1024*1024,
            check:false,
        });
        if(result.status!==0)
            throw Error('Cannot capture the exact modified Unity source inventory.');
        for(const name of String(result.stdout||'').split('\0')) {
            if(name&&!protectedPath(name)&&
                !(args[0]==='ls-files'&&ignoredMeta.has(name)))names.add(name);
        }
    }
    const hashes=[];
    const add=async (root,prefix,name)=>{
        const relative=name.split(path.sep).join('/');
        if(protectedPath(relative))return;
        const absolute=path.join(root,name);
        const st=fs.existsSync(absolute)?fs.lstatSync(absolute):null;
        if(st===null){hashes.push([prefix+relative,'deleted']);return;}
        if(!st.isFile()||st.isSymbolicLink())
            throw Error('Unity build source is not a regular file: '+prefix+relative);
        hashes.push([prefix+relative,st.size,await sha256(absolute)]);
    };
    for(const name of [...names].sort())await add(paths.assetsRoot,'Assets/',name);
    const projectRoot=paths.beesRoot;
    for(const folder of ['Packages','ProjectSettings']) {
        const base=path.join(projectRoot,folder);
        if(!fs.existsSync(base)||!fs.statSync(base).isDirectory())
            throw Error('Unity source snapshot is missing '+folder);
        const files=[];
        const pending=[''];
        while(pending.length) {
            const rel=pending.shift();
            for(const entry of fs.readdirSync(path.join(base,rel),{withFileTypes:true})) {
                const sub=path.join(rel,entry.name);
                if(protectedPath(sub))continue;
                if(entry.isSymbolicLink())
                    throw Error('Unity source snapshots cannot follow package/project symlinks.');
                if(entry.isDirectory())pending.push(sub);
                else if(entry.isFile())files.push(sub);
                else throw Error('Unity project source contains an unsupported filesystem entry.');
            }
        }
        for(const name of files.sort())await add(base,folder+'/',name);
    }
    hashes.sort((a,b)=>a[0]<b[0]?-1:a[0]>b[0]?1:0);
    return crypto.createHash('sha256')
        .update(JSON.stringify(hashes)).digest('hex');
}

async function build(argv = process.argv.slice(2)) {
    const arg = options(argv);
    if (arg.help) {
        console.log(usage());
        return null;
    }
    if (arg.inventory && process.platform !== 'linux')
        throw Error('Remote Unity distribution from the build command requires a Linux SSH operator host.');
    const version = projectVersion();
    const editor = resolveUnityEditor({ unityEditor: arg.editor });
    const python = arg.python
        ? resolveCommand(arg.python)
        : resolveCommand(process.platform === 'win32' ? 'python' : 'python3');
    assertUnityProjectAvailableForBatchBuild();

    const targets = arg.platform === 'both'
        ? [TARGETS.linux, TARGETS.windows]
        : [TARGETS[arg.platform]];
    const hostPlatform = process.platform === 'linux' ? 'linux'
        : process.platform === 'win32' ? 'windows' : null;
    if (arg.installRoot) {
        if (!hostPlatform || !targets.includes(TARGETS[hostPlatform]))
            throw Error('--install-root requires building the worker for this host OS.');
        const prefix = paths.beesRoot.endsWith(path.sep) ? paths.beesRoot
            : paths.beesRoot + path.sep;
        if (arg.installRoot === paths.beesRoot || arg.installRoot.startsWith(prefix))
            throw Error('--install-root must be outside the Unity project tree.');
    }
    const commit = getGitShortSha();
    const ignoredMeta=initialToolMetadata();
    const sourceDirty = dirtySource(ignoredMeta);
    if(sourceDirty===null)
        throw Error('Cannot establish the Unity source status before building workers.');
    const sourceFingerprint=await sourceSnapshot(ignoredMeta);
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
            player_tree_sha256: unityWorkerTreeSha256(path.join(folder,target.entrypoint)),
        });
    }

    // Freeze the actual Python learner/actor code from the same Assets
    // snapshot as both Unity players. The WAN runtime is a content-addressed
    // source release, never whatever an actor's checkout happens to contain.
    const runtimeArchive=path.join(root,'bees-runtime.zip');
    const runtimeTool=path.join(paths.assetsRoot,'Training','bees_release_runtime.py');
    const packaged=runSync(python,['-B',runtimeTool,'package',
        '--assets-root',paths.assetsRoot,'--output',runtimeArchive,
        '--build-id',buildId,'--source-commit',commit],{
        cwd:paths.assetsRoot,timeout:120000,maxBuffer:1024*1024,
    });
    const runtime=JSON.parse(String(packaged.stdout||''));
    if(runtime.archive!==runtimeArchive||runtime.build_id!==buildId||
        runtime.source_commit!==commit||
        !/^[0-9a-f]{64}$/.test(String(runtime.archive_sha256||''))||
        !/^[0-9a-f]{64}$/.test(String(runtime.runtime_version||''))||
        runtime.archive_sha256!==(await sha256(runtimeArchive))||
        runtime.archive_size_bytes!==fs.statSync(runtimeArchive).size)
        throw Error('Compiled Bees runtime ZIP failed immutable source provenance verification.');

    if (getGitShortSha() !== commit ||
        (await sha256(packagesManifest)) !== packagesSha ||
        dirtySource(ignoredMeta) !== sourceDirty||
        (await sourceSnapshot(ignoredMeta))!==sourceFingerprint) {
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
        source_snapshot_sha256: sourceFingerprint,
        unity_version: version,
        packages_manifest_sha256: packagesSha,
        artifacts,
        runtime:{
            archive:'bees-runtime.zip',
            archive_sha256:runtime.archive_sha256,
            archive_bytes:runtime.archive_size_bytes,
            runtime_version:runtime.runtime_version,
        },
    };
    const manifestPath = path.join(root, 'worker-build-manifest.json');
    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + os.EOL, {
        encoding: 'utf8',
        flag: 'wx',
    });
    console.log('Worker build completed. Manifest: ' + manifestPath);
    console.log('These artifacts have not been published to any training cluster.');
    if (arg.installRoot) {
        const installer = path.join(paths.assetsRoot, 'Training',
            'bees_hub_worker_install.py');
        const command = [
            '-B', installer, 'install',
            '--root', arg.installRoot,
            '--manifest', manifestPath,
            '--archive', path.join(root, TARGETS[hostPlatform].archive),
            ...(arg.activate ? ['--activate'] : []),
        ];
        const receipt = JSON.parse(String(runSync(python, command, {
            cwd: paths.assetsRoot, timeout: 180000, maxBuffer: 1024 * 1024,
        }).stdout || '').trim());
        const matching = artifacts.find(artifact =>
            artifact.platform === TARGETS[hostPlatform].platform);
        if (receipt.release_sha256 !== await sha256(manifestPath) ||
            receipt.platform !== matching.platform ||
            receipt.player_tree_sha256 !== matching.player_tree_sha256 ||
            (arg.activate && receipt.activated !== true))
            throw Error('Local Unity worker installer did not return the expected verified release receipt.');
        console.log('Host worker release installed: ' + receipt.release_sha256);
        if (arg.activate)
            console.log('Host worker release activated for future idle Hub agent starts.');
    }
    if (arg.inventory) {
        // Do not repackage or infer a release from an older build. The trusted
        // distributor verifies this invocation's manifest and both archive
        // identities, then stages ALL selected hosts before any activation.
        const distributor=path.join(paths.assetsRoot,'Training',
            'bees_hub_worker_distribute.py');
        console.log('Distributing this verified Unity build to the approved machine inventory.');
        runSync(python,['-B',distributor,
            '--manifest',manifestPath,'--inventory',arg.inventory,
            ...arg.machines.flatMap(machine=>['--machine',machine]),
            ...(arg.apply?['--apply']:[]),
            ...(arg.activate?['--activate']:[])],{
            cwd:paths.assetsRoot,stdio:'inherit',
        });
        console.log(arg.apply
            ?'Remote Unity release installed'+(arg.activate?' and activated.':'.')
            :'Remote Unity distribution plan completed; no machine changed.');
    }
    return manifest;
}

if (require.main === module) {
    build().catch(error => {
        console.error(error && error.stack ? error.stack : String(error));
        process.exitCode = 1;
    });
}

module.exports = { build, options, projectVersion, TARGETS };
