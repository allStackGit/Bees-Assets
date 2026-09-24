'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { performance } = require('node:perf_hooks');

const CACHE_WRITER_INSTALLED = Symbol('beesSafeCacheWriterInstalled');

function* cacheChunks(content, sliceSize) {
    const size = Number.isInteger(sliceSize) && sliceSize > 0 ? sliceSize : 1000;
    for (let offset = 0; offset < content.length; offset += size) {
        const string = JSON.stringify(content.slice(offset, offset + size)).slice(1, -1);
        if (string !== '' && string !== '[]') yield `${string},`;
    }
}

async function writeCacheSafely(server, cache, fsModule = fs) {
    const recentKey = `${cache}Recent`;
    const content = Array.isArray(server?.[recentKey]) ? server[recentKey] : [];
    server[recentKey] = [];
    if (content.length === 0) return true;

    const started = performance.now();
    server.writesCount = Number(server.writesCount || 0) + 1;
    const filename = path.join(server.cacheFolder || '.', `${cache}.json`);

    try {
        if (server.useFullDiskWrite) {
            await fsModule.promises.writeFile(filename, JSON.stringify(content), 'utf8');
        } else {
            const handle = await fsModule.promises.open(filename, 'a');
            try {
                for (const chunk of cacheChunks(content, server.cacheSliceSize)) {
                    await handle.writeFile(chunk, 'utf8');
                }
            } finally {
                await handle.close();
            }
        }

        const elapsed = performance.now() - started;
        server.totalWriteTime = Number(server.totalWriteTime || 0) + elapsed;
        return true;
    } catch (error) {
        // Preserve the unwritten snapshot ahead of entries that arrived while the write was
        // pending. Cache files are reconstructible, but silently dropping the delta makes the
        // next restart unnecessarily expensive and can hide repeated I/O failures.
        const newer = Array.isArray(server[recentKey]) ? server[recentKey] : [];
        server[recentKey] = content.concat(newer);
        if (typeof server?.common?.handleError === 'function') {
            server.common.handleError(error, `writeCacheToDisk.${cache}`);
        } else {
            console.error(`Cache file writing error for ${cache}.json:`, error);
        }
        return false;
    }
}

function installSafeCacheWriter(runtime, options = {}) {
    if (!runtime?.Server || runtime[CACHE_WRITER_INSTALLED]) return runtime;
    runtime[CACHE_WRITER_INSTALLED] = true;
    const BaseServer = runtime.Server;
    const fsModule = options.fsModule || fs;

    runtime.Server = class SafeCacheServer extends BaseServer {
        constructor(...args) {
            super(...args);
            this.common = runtime.common;
            this.writeCacheToDisk = cache => writeCacheSafely(this, cache, fsModule);
        }
    };
    return runtime;
}

module.exports = { cacheChunks, writeCacheSafely, installSafeCacheWriter };
