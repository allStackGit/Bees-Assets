'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    TrainingEnvOptimizer,
    normalizeCapacity,
    learnerConsumedSteps,
    producerAcceptedSteps,
} = require('../trainingEnvOptimizer');

function record(
    trainerId,
    envs,
    consumed,
    {
        auto = true,
        min = 1,
        max = 64,
        processState = 'running',
        accepted = consumed,
        lastError = '',
    } = {},
) {
    return {
        trainer_id: trainerId,
        role: 'dedicated',
        process_state: processState,
        last_error: lastError,
        worker_capacity: {
            auto,
            current_envs: envs,
            min_envs: min,
            max_envs: max,
        },
        metrics: {
            throughput: {
                learner_consumed_steps_total: consumed,
                accepted_steps_total: accepted,
            },
        },
    };
}

function update(optimizer, trainerId, envs, consumed, now, options = {}) {
    return optimizer.update(
        record(trainerId, envs, consumed, options),
        { now, contextKey: 'run|build|args', enabled: true },
    );
}

test('capacity and learner-consumed-step metrics reject malformed values', () => {
    assert.equal(normalizeCapacity({ auto: true, current_envs: 0, min_envs: 1, max_envs: 64 }), null);
    assert.equal(normalizeCapacity({ auto: true, current_envs: 8, min_envs: 9, max_envs: 64 }), null);
    assert.deepEqual(
        normalizeCapacity({ auto: true, current_envs: 8, min_envs: 2, max_envs: 32 }),
        { auto: true, current_envs: 8, min_envs: 2, max_envs: 32 },
    );
    assert.equal(learnerConsumedSteps({ throughput: { learner_consumed_steps_total: -1 } }), null);
    assert.equal(
        learnerConsumedSteps({
            throughput: {
                learner_consumed_steps_total: 42,
                accepted_steps_total: 999999,
            },
        }),
        42,
    );
    assert.equal(
        learnerConsumedSteps({ throughput: { accepted_steps_total: 999999 } }),
        null,
    );
    assert.equal(
        producerAcceptedSteps({ throughput: { accepted_steps_total: 77 } }),
        77,
    );
    assert.equal(
        producerAcceptedSteps({ throughput: { accepted_steps_total: -1 } }),
        null,
    );
});

test('optimizer measures learner-consumed steps, increases envs, and keeps an improvement', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        retestMs: 60_000,
        minImprovementRatio: 0.03,
        regressionRatio: 0.05,
    });

    let state = update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    assert.equal(state.phase, 'measuring');

    state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.baseline_sps, 1000);
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);

    // The process restarts at the requested count, resetting its learner-consumed counter.
    state = update(optimizer, 'remote-a', 9, 0, 1010, { max: 16 });
    assert.equal(state.phase, 'warmup');
    state = update(optimizer, 'remote-a', 9, 0, 1011, { max: 16 });
    assert.equal(state.phase, 'measuring');

    state = update(optimizer, 'remote-a', 9, 1100, 2011, { max: 16 });
    assert.equal(state.baseline_envs, 9);
    assert.equal(state.baseline_sps, 1100);
    assert.equal(state.desired_envs, 10);
    assert.match(state.decision, /probing 9->10/);
});

test('optimizer retries a starved sample instead of treating producer activity as zero useful throughput', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        retestMs: 60_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16, accepted: 0 });
    let state = update(
        optimizer,
        'remote-a',
        8,
        0,
        1000,
        { max: 16, accepted: 900 },
    );

    assert.equal(state.baseline_envs, null);
    assert.equal(state.baseline_sps, null);
    assert.equal(state.desired_envs, 8);
    assert.equal(state.phase, 'warmup');
    assert.match(state.decision, /produced rollouts but none were learner-consumed/);

    state = update(
        optimizer,
        'remote-a',
        8,
        0,
        1001,
        { max: 16, accepted: 900 },
    );
    assert.equal(state.phase, 'measuring');

    state = update(
        optimizer,
        'remote-a',
        8,
        850,
        2001,
        { max: 16, accepted: 1750 },
    );
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.baseline_sps, 850);
    assert.equal(state.desired_envs, 9);
});

test('optimizer backs off a slower probe before another worker may probe', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        retestMs: 60_000,
        minImprovementRatio: 0.03,
        regressionRatio: 0.05,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let stateA = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(stateA.desired_envs, 9);

    // A second worker can establish a baseline, but cannot begin a simultaneous cluster probe.
    update(optimizer, 'remote-b', 6, 0, 1001, { max: 16 });
    const stateB = update(optimizer, 'remote-b', 6, 800, 2001, { max: 16 });
    assert.equal(stateB.desired_envs, 6);
    assert.equal(stateB.probing, false);

    update(optimizer, 'remote-a', 9, 0, 2010, { max: 16 });
    update(optimizer, 'remote-a', 9, 0, 2011, { max: 16 });
    stateA = update(optimizer, 'remote-a', 9, 900, 3011, { max: 16 });
    assert.equal(stateA.desired_envs, 8);
    assert.equal(stateA.probing, true);
    assert.match(stateA.decision, /backing off/);

    // The probe lock remains with A until its process has actually returned to the baseline.
    const waitingB = update(optimizer, 'remote-b', 6, 1600, 3012, { max: 16 });
    assert.equal(waitingB.desired_envs, 6);
    assert.equal(waitingB.probing, false);

    stateA = update(optimizer, 'remote-a', 8, 0, 3020, { max: 16 });
    assert.equal(stateA.probing, false);
});

test('optimizer backs off immediately when a probed worker process stops', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.desired_envs, 9);

    state = update(
        optimizer,
        'remote-a',
        9,
        0,
        1010,
        { max: 16, processState: 'stopped' },
    );
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, true);
    assert.match(state.decision, /not running/);

    state = update(optimizer, 'remote-a', 8, 0, 1020, { max: 16 });
    assert.equal(state.probing, false);
});

test('optimizer backs off a probe that never produces learner-consumed-step metrics', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        metricsTimeoutMs: 100,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);

    state = update(optimizer, 'remote-a', 9, null, 1010, { max: 16 });
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);

    state = update(optimizer, 'remote-a', 9, null, 1111, { max: 16 });
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, true);
    assert.match(state.decision, /no learner-consumed-step metrics/);

    state = update(optimizer, 'remote-a', 8, 0, 1120, { max: 16 });
    assert.equal(state.probing, false);
});

test('optimizer holds a recovered worker before probing again after a reported failure', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        retestMs: 1000,
        instabilityHoldMs: 10_000,
    });

    let state = update(
        optimizer,
        'remote-a',
        8,
        0,
        0,
        { max: 16, lastError: 'Unity communicator stopped' },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, false);
    assert.equal(state.stability_hold_until_ms, 10_000);

    state = update(optimizer, 'remote-a', 8, 500, 5_000, { max: 16 });
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, false);

    state = update(optimizer, 'remote-a', 8, 1000, 10_001, { max: 16 });
    assert.equal(state.phase, 'warmup');
    assert.match(state.decision, /collecting fresh baseline/);

    state = update(optimizer, 'remote-a', 8, 1000, 10_002, { max: 16 });
    assert.equal(state.phase, 'measuring');
    state = update(optimizer, 'remote-a', 8, 2000, 11_002, { max: 16 });
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);
});

test('planned env-count transition does not create an instability hold', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.desired_envs, 9);

    state = update(
        optimizer,
        'remote-a',
        8,
        1000,
        1001,
        { max: 16, processState: 'stopped' },
    );
    assert.equal(state.phase, 'awaiting-restart');
    assert.equal(state.stability_hold_until_ms, 0);

    state = update(optimizer, 'remote-a', 9, 0, 1010, { max: 16 });
    assert.equal(state.phase, 'warmup');
    assert.equal(state.stability_hold_until_ms, 0);
});

test('optimizer resets safely when a worker advertises new env bounds', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.desired_envs, 9);

    state = update(optimizer, 'remote-a', 6, 0, 1010, { max: 6 });
    assert.equal(state.desired_envs, 6);
    assert.equal(state.baseline_envs, null);
    assert.equal(state.phase, 'measuring');
});

test('manual env counts and central learner are never auto-tuned', () => {
    const optimizer = new TrainingEnvOptimizer({ warmupMs: 0, measurementMs: 1 });

    const manual = optimizer.update(
        record('remote-manual', 12, 100, { auto: false, min: 12, max: 12 }),
        { now: 0, contextKey: 'x', enabled: true },
    );
    assert.equal(manual.enabled, false);
    assert.equal(manual.phase, 'manual');
    assert.equal(manual.desired_envs, 12);

    const centralRecord = record('central-learner', 8, 100);
    const central = optimizer.update(
        centralRecord,
        { now: 0, contextKey: 'x', enabled: true },
    );
    assert.equal(central.enabled, false);
    assert.equal(central.phase, 'paused');
    assert.equal(central.desired_envs, 8);
});
