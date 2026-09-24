'use strict';

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const mime = require('mime-types');

const hostname = process.env.BEES_MEDIA_HOST || '192.168.36.3';
const port = Number.parseInt(process.env.BEES_MEDIA_PORT || '8010', 10);
const serverName = 'Seagrams Media Server';
const defaultMediaRoot = path.join(__dirname, 'v');
let totalData = 0;

function escapeHtml(value) {
    return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}
function sendText(res, statusCode, text) {
    if (res.headersSent) { res.destroy(); return; }
    const body = Buffer.from(String(text), 'utf8');
    res.writeHead(statusCode, { 'Content-Type': 'text/plain; charset=utf-8', 'Content-Length': body.length, Server: serverName });
    res.end(body);
}
function resolveMediaPath(mediaRoot, pathname) {
    const normalizedRoot = path.resolve(mediaRoot);
    const relative = pathname.replace(/^\/v\/?/, '').replace(/^\/+/, '');
    const resolved = path.resolve(normalizedRoot, relative);
    if (resolved !== normalizedRoot && !resolved.startsWith(`${normalizedRoot}${path.sep}`)) return null;
    return resolved;
}
function parseRange(rangeHeader, size) {
    if (!rangeHeader) return null;
    const match = /^bytes=(\d*)-(\d*)$/.exec(String(rangeHeader).trim());
    if (!match || (match[1] === '' && match[2] === '')) return false;
    let start;
    let end;
    if (match[1] === '') {
        const suffixLength = Number.parseInt(match[2], 10);
        if (!Number.isSafeInteger(suffixLength) || suffixLength <= 0) return false;
        start = Math.max(0, size - suffixLength);
        end = size - 1;
    } else {
        start = Number.parseInt(match[1], 10);
        end = match[2] === '' ? size - 1 : Number.parseInt(match[2], 10);
    }
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end < start || start >= size) return false;
    return { start, end: Math.min(end, size - 1) };
}
async function readDir(directoryPath, requestPath, res) {
    let files;
    try { files = await fs.promises.readdir(directoryPath, { withFileTypes: true }); }
    catch (error) {
        console.error(`Unable to scan directory ${directoryPath}:`, error);
        sendText(res, error.code === 'ENOENT' ? 404 : 500, error.code === 'ENOENT' ? 'Directory not found' : 'Unable to read directory');
        return;
    }
    const baseHref = requestPath.endsWith('/') ? requestPath : `${requestPath}/`;
    const items = files.map(entry => {
        const label = `${entry.name}${entry.isDirectory() ? '/' : ''}`;
        return `<li><a href="${baseHref}${encodeURIComponent(entry.name)}${entry.isDirectory() ? '/' : ''}">${escapeHtml(label)}</a></li>`;
    }).join('');
    const body = Buffer.from(`<h3>Files</h3><ul>${items}</ul>`, 'utf8');
    totalData += body.length;
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Content-Length': body.length, Server: serverName });
    res.end(body);
}
function createMediaServer(options = {}) {
    const mediaRoot = path.resolve(options.mediaRoot || process.env.BEES_MEDIA_ROOT || defaultMediaRoot);
    return http.createServer(async (req, res) => {
        let pathname;
        try { pathname = decodeURIComponent(new URL(req.url || '/', 'http://localhost').pathname); }
        catch { sendText(res, 400, 'Invalid request path'); return; }
        if (pathname === '/') { sendText(res, 200, 'Hello World\n'); return; }
        if (pathname !== '/v' && !pathname.startsWith('/v/')) { sendText(res, 404, 'Not found'); return; }
        const fullFilename = resolveMediaPath(mediaRoot, pathname);
        if (!fullFilename) { sendText(res, 403, 'Forbidden'); return; }
        let stats;
        try { stats = await fs.promises.stat(fullFilename); }
        catch (error) { sendText(res, error.code === 'ENOENT' ? 404 : 500, error.code === 'ENOENT' ? 'File not found' : 'Unable to read file'); return; }
        if (stats.isDirectory()) { await readDir(fullFilename, pathname, res); return; }
        if (!stats.isFile()) { sendText(res, 404, 'Not found'); return; }

        const requestedRange = parseRange(req.headers.range, stats.size);
        if (requestedRange === false) {
            res.writeHead(416, { 'Content-Range': `bytes */${stats.size}`, Server: serverName });
            res.end();
            return;
        }
        const contentType = mime.lookup(fullFilename) || 'application/octet-stream';
        if (stats.size === 0) {
            res.writeHead(200, { 'Content-Type': contentType, 'Content-Length': 0, 'Accept-Ranges': 'bytes', Server: serverName });
            res.end();
            return;
        }
        const start = requestedRange ? requestedRange.start : 0;
        const end = requestedRange ? requestedRange.end : stats.size - 1;
        const contentLength = end - start + 1;
        const headers = { 'Content-Type': contentType, 'Content-Length': contentLength, 'Accept-Ranges': 'bytes', Server: serverName };
        if (requestedRange) headers['Content-Range'] = `bytes ${start}-${end}/${stats.size}`;
        const stream = fs.createReadStream(fullFilename, { start, end });
        stream.once('open', () => { res.writeHead(requestedRange ? 206 : 200, headers); stream.pipe(res); });
        stream.once('error', error => {
            console.error(`Unable to stream ${fullFilename}:`, error);
            if (!res.headersSent) sendText(res, error.code === 'ENOENT' ? 404 : 500, error.code === 'ENOENT' ? 'File not found' : 'Unable to read file');
            else res.destroy(error);
        });
        res.once('finish', () => {
            totalData += contentLength;
            console.log(`Server has sent ${(totalData / 1024).toFixed(2)}KB`);
        });
    });
}
if (require.main === module) {
    const server = createMediaServer();
    server.listen(port, hostname, () => console.log(`Media server running on ${hostname}:${port}`));
}
module.exports = { createMediaServer, resolveMediaPath, parseRange };
