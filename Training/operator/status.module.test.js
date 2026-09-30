'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { statusError } = require('./status');

test('optimizer decisions without an instability timestamp do not render epoch age', () => {
    const text = statusError({
        process_state: 'running',
        env_optimizer: {
            last_instability_ms: null,
            last_instability_reason: '',
            decision: 'measuring global learner throughput for 300 seconds',
        },
        metrics: {},
    });

    assert.equal(
        text,
        'Optimizer: measuring global learner throughput for 300 seconds',
    );
    assert.doesNotMatch(text, /ago:/);
});

test('real optimizer instability timestamps still render an age', () => {
    const originalNow = Date.now;
    Date.now = () => 100_000;
    try {
        assert.equal(
            statusError({
                process_state: 'running',
                env_optimizer: {
                    last_instability_ms: 95_000,
                    last_instability_reason: 'WAN actor session failure',
                    decision: 'holding env count after recent worker instability',
                },
                metrics: {},
            }),
            'Optimizer, 5s ago: WAN actor session failure',
        );
    } finally {
        Date.now = originalNow;
    }
});
