'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    TrainingEnvOptimizer,
    normalizeCapacity,
    acceptedSteps,
} = require('../trainingEnvOptimizer');

function record(
    trainerId,
    envs,
    accepted,
    { auto = true, min = 1, max = 64, processState = 'running' } = {},
) {
    return {
        trainer_id: trainerId,
        role: 'dedicated',
        process_state: processState,
        worker_capacity: {
            auto,
            current_envs: envs,
            min_envs: min,
            max_envs: max,
        },
        metrics: {
            throughput: {
                accepted_steps_total: accepted,
            },
        },
    };
}

function update(optimizer, trainerId, envs, accepted, now, options = {}) {
    return optimizer.update(
        record(trainerId, envs, accepted, options),
        { now, contextKey: 'run|build|args', enabled: true },
    );
}

test('capacity and accepted-step metrics reject malformed values', () => {
    assert.equal(normalizeCapacity({ auto: true, current_envs: 0, min_envs: 1, max_envs: 64 }), null);
    assert.equal(normalizeCapacity({ auto: true, current_envs: 8, min_envs: 9, max_envs: 64 }), null);
    assert.deepEqual(
        normalizeCapacity({ auto: true, current_envs: 8, min_envs: 2, max_envs: 32 }),
        { auto: true, current_envs: 8, min_envs: 2, max_envs: 32 },
    );
    assert.equal(acceptedSteps({ throughput: { accepted_steps_total: -1 } }), null);
    assert.equal(acceptedSteps({ throughput: { accepted_steps_total: 42 } }), 42);
});

test('optimizer measures accepted steps, increases envs, and keeps an improvement', () => {
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

    // The process restarts at the requested count, resetting its accepted-step counter.
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
