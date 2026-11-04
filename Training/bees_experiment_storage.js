'use strict';
const fs = require('node:fs');
const path = require('node:path');

const FILENAME = 'storage-location.json';
const defaultRoot = trainingRoot => path.resolve(trainingRoot, 'Experiments');
const locationFile = trainingRoot => path.join(defaultRoot(trainingRoot), FILENAME);
function selectedRoot(trainingRoot) {
    const file = locationFile(trainingRoot);
    if (!fs.existsSync(file)) return defaultRoot(trainingRoot);
    const value = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (value.version !== 1 || typeof value.root !== 'string' ||
        !path.isAbsolute(value.root)) {
        throw Error('Experiment storage-location.json is invalid; refusing to start.');
    }
    const selected = path.resolve(value.root);
    // Never silently create a fresh empty experiment registry if an external
    // drive or mounted data directory is unavailable at startup.
    if (!fs.existsSync(path.join(selected, 'experiments-v1.json'))) {
        throw Error('Configured experiment data directory is unavailable or incomplete: ' +
            selected + '. Restore the storage volume or location before starting.');
    }
    return selected;
}
function remapPaths(value, oldRoot, newRoot) {
    if (typeof value === 'string') {
        return value === oldRoot ? newRoot
            : value.startsWith(oldRoot + path.sep)
                ? newRoot + value.slice(oldRoot.length) : value;
    }
    if (Array.isArray(value)) return value.map(item => remapPaths(item, oldRoot, newRoot));
    if (value && typeof value === 'object')
        return Object.fromEntries(Object.entries(value)
            .map(([key, v]) => [key, remapPaths(v, oldRoot, newRoot)]));
    return value;
}
function isInside(a, b) {
    const relative = path.relative(a, b);
    return !relative || (!relative.startsWith('..' + path.sep) &&
        relative !== '..' && !path.isAbsolute(relative));
}
// Explicit idle-only copy. Keep the original directory untouched as a backup;
// switch only after the destination has complete, atomically saved metadata.
function copyAndSelect(trainingRoot, fromRoot, requestedRoot, state, writeJsonAtomic) {
    if (typeof requestedRoot !== 'string' || !path.isAbsolute(requestedRoot))
        throw Error('Enter an absolute path to the new experiment data directory.');
    const target = path.resolve(requestedRoot);
    if (isInside(fromRoot, target) || isInside(target, fromRoot)) {
        throw Error('New storage directory must not contain or be inside the current one.');
    }
    if (fs.existsSync(target) && fs.readdirSync(target).length)
        throw Error('Select an empty directory to avoid merging unrelated experiment data.');
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.cpSync(fromRoot, target, { recursive: true, errorOnExist: false });
    const copied = remapPaths(state, fromRoot, target);
    writeJsonAtomic(path.join(target, 'experiments-v1.json'), copied);
    for (const name of ['benchmark-settings.json', 'heldout-benchmark-settings.json']) {
        const filename = path.join(target, name);
        if (fs.existsSync(filename)) {
            writeJsonAtomic(filename,
                remapPaths(JSON.parse(fs.readFileSync(filename, 'utf8')), fromRoot, target));
        }
    }
    const locator = locationFile(trainingRoot);
    writeJsonAtomic(locator, {version: 1, root: target});
    return target;
}
module.exports = { selectedRoot, copyAndSelect };
