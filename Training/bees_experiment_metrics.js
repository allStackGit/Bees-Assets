'use strict';
const fs = require('node:fs');
const path = require('node:path');

function filename(root, item) {
    if (!/^[a-zA-Z0-9._-]{1,128}$/.test(item.id || '')) {
        throw Error('Invalid experiment ID for metrics archive.');
    }
    return path.join(root, 'Metrics', item.id + '.jsonl');
}
function append(root, item) {
    const target = filename(root, item);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    // Seed first archive with the samples that existed before this feature.
    // Historical samples already discarded by earlier releases are not
    // reconstructable and must not be represented as complete observations.
    if (!fs.existsSync(target)) {
        fs.writeFileSync(target, (item.samples || [])
            .map(sample => JSON.stringify(sample)).join('\n') +
            ((item.samples || []).length ? '\n' : ''), {flag:'wx'});
        return target;
    }
    const latest = (item.samples || []).at(-1);
    if (latest) fs.appendFileSync(target, JSON.stringify(latest) + '\n');
    return target;
}
function load(root, item) {
    const target = filename(root, item);
    if (!fs.existsSync(target)) return item.samples || [];
    return fs.readFileSync(target, 'utf8').split(/\r?\n/)
        .filter(Boolean).map(line => JSON.parse(line));
}
function reading(sample, metric) {
    if (metric.startsWith('ppo_') && metric !== 'ppo_busy_pct')
        return sample.ppo?.[metric.slice(4)] ?? null;
    if (metric.startsWith('pipeline_'))
        return sample.pipeline?.[metric.slice(9)] ?? null;
    return sample[metric] ?? null;
}
// Compute smoothing over the FULL archived sequence before retaining a bounded
// min/max plot subset. Downsampling the raw series before smoothing would
// change the values displayed and undermine the chart's meaning.
function plot(root, item, metric, axis, smooth = 1, max = 10000) {
    if (!/^[a-z][a-z0-9_]{0,63}$/.test(metric) ||
        !['steps','time'].includes(axis) ||
        ![1,5,15,30].includes(smooth)) throw Error('Unsupported chart settings.');
    const original = load(root, item);
    const recent = [];
    let sum = 0, running = 0, n = 0;
    const valid = [];
    for (const sample of original) {
        const raw = reading(sample, metric);
        const x = axis === 'time'
            ? (Number.isFinite(sample.training_s) ? sample.training_s / 3600 : null)
            : sample.step;
        if (!Number.isFinite(x) || raw == null || !Number.isFinite(Number(raw))) continue;
        const value = Number(raw);
        n++;
        running += value;
        sum += value;
        recent.push(value);
        if (recent.length > smooth) sum -= recent.shift();
        valid.push({
            ...sample, _chart_plotted: sum / recent.length,
            _chart_mean: running / n,
        });
    }
    if (valid.length <= max) {
        return {samples: valid, available: original.length, plotted: valid.length,
            downsampled: false};
    }
    const chosen = new Set([0, valid.length - 1]);
    const bucketCount = Math.max(1, Math.floor((max - 2) / 2));
    const middle = valid.length - 2;
    for (let i = 0; i < bucketCount; i++) {
        const begin = 1 + Math.floor(i * middle / bucketCount);
        const end = 1 + Math.floor((i + 1) * middle / bucketCount);
        if (end <= begin) continue;
        let lo = begin, hi = begin;
        for (let j = begin + 1; j < end; j++) {
            if (valid[j]._chart_plotted < valid[lo]._chart_plotted) lo = j;
            if (valid[j]._chart_plotted > valid[hi]._chart_plotted) hi = j;
        }
        chosen.add(lo); chosen.add(hi);
    }
    return {
        samples: [...chosen].sort((a,b)=>a-b).map(i=>valid[i]),
        available: original.length, plotted: valid.length, downsampled: true,
    };
}
module.exports = { append, load, plot, filename };
