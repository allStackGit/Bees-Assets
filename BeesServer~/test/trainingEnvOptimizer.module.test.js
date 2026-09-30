'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    TrainingEnvOptimizer,
    normalizeCapacity,
    learnerConsumedSteps,
    learnerStep,
    optimizationSteps,
    producerAcceptedSteps,
    runtimeVersion,
    policyCycle,
    sessionFailureMessage,
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
        sessionFailures = 0,
        failureAgeSeconds = null,
        failureType = '',
        failureMessage = '',
        resizeFailedTarget = null,
        resizeError = '',
        policyCycleValue = null,
        runtimeVersionValue = '',
        learnerStepValue = consumed,
        reconciliationPhase = '',
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
                learner_step_total: learnerStepValue,
                accepted_steps_total: accepted,
                policy_cycle: policyCycleValue,
                runtime_version: runtimeVersionValue,
                session_failures_total: sessionFailures,
                seconds_since_last_session_failure: failureAgeSeconds,
                last_session_failure_type: failureType,
                last_session_failure_message: failureMessage,
                env_resize_failed_target: resizeFailedTarget,
                env_resize_error: resizeError,
            },
            reconciliation: reconciliationPhase
                ? { phase: reconciliationPhase, seconds_in_phase: 1 }
                : undefined,
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
    assert.equal(learnerStep({ throughput: { learner_step_total: 21 } }), 21);
    assert.equal(learnerStep({ throughput: { learner_step_total: -1 } }), null);
    assert.equal(
        optimizationSteps({
            throughput: {
                learner_step_total: 21,
                learner_consumed_steps_total: 420,
            },
        }),
        21,
    );
    assert.equal(
        producerAcceptedSteps({ throughput: { accepted_steps_total: 77 } }),
        77,
    );
    assert.equal(
        producerAcceptedSteps({ throughput: { accepted_steps_total: -1 } }),
        null,
    );
    assert.equal(policyCycle({ throughput: { policy_cycle: 7 } }), 7);
    assert.equal(policyCycle({ throughput: { policy_cycle: -1 } }), null);
    assert.equal(
        sessionFailureMessage({
            throughput: { last_session_failure_message: ' background watcher failed ' },
        }),
        'background watcher failed',
    );
    assert.equal(runtimeVersion({ throughput: { runtime_version: 'a'.repeat(64) } }), 'a'.repeat(64));
    assert.equal(runtimeVersion({ throughput: { runtime_version: 'not-a-hash' } }), '');
});

test('optimizer defaults to 60 seconds settling and five minutes measuring', () => {
    const optimizer = new TrainingEnvOptimizer();

    let state = update(optimizer, 'remote-a', 4, 0, 0, { max: 48 });
    assert.equal(state.phase, 'settling');

    state = update(optimizer, 'remote-a', 4, 1000, 59_999, { max: 48 });
    assert.equal(state.phase, 'settling');

    state = update(optimizer, 'remote-a', 4, 1200, 60_000, { max: 48 });
    assert.equal(state.phase, 'measuring');

    state = update(optimizer, 'remote-a', 4, 7199, 359_999, { max: 48 });
    assert.equal(state.phase, 'measuring');
    assert.equal(state.baseline_envs, null);

    state = update(optimizer, 'remote-a', 4, 7200, 360_000, { max: 48 });
    assert.equal(state.baseline_envs, 4);
    assert.equal(state.baseline_sps, 20);
    assert.equal(state.desired_envs, 8);
    assert.equal(state.phase, 'resizing');
});

test('optimizer uses wall-clock settling and measurement instead of policy-cycle gating', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 1000,
        measurementMs: 5000,
        retestMs: 60_000,
    });

    let state = update(
        optimizer,
        'remote-a',
        4,
        0,
        0,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'settling');

    state = update(
        optimizer,
        'remote-a',
        4,
        50,
        999,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'settling');

    state = update(
        optimizer,
        'remote-a',
        4,
        100,
        1000,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'measuring');

    state = update(
        optimizer,
        'remote-a',
        4,
        1099,
        5999,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'measuring');
    assert.equal(state.baseline_envs, null);

    state = update(
        optimizer,
        'remote-a',
        4,
        1100,
        6000,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.baseline_envs, 4);
    assert.equal(state.baseline_sps, 200);
    assert.equal(state.desired_envs, 8);
    assert.equal(state.phase, 'resizing');
    assert.equal(state.probing, true);

    state = update(
        optimizer,
        'remote-a',
        8,
        1200,
        6010,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'settling');

    state = update(
        optimizer,
        'remote-a',
        8,
        1400,
        7010,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.phase, 'measuring');

    state = update(
        optimizer,
        'remote-a',
        8,
        2600,
        12010,
        { max: 48, policyCycleValue: 10 },
    );
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.desired_envs, 16);
    assert.equal(state.probing, true);
});

test('optimizer doubles capacity while global learner throughput improves', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        retestMs: 60_000,
        minImprovementRatio: 0.03,
        regressionRatio: 0.05,
    });

    let state = update(optimizer, 'remote-a', 8, 0, 0, { max: 32 });
    assert.equal(state.phase, 'measuring');

    state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 32 });
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.baseline_sps, 1000);
    assert.equal(state.desired_envs, 16);
    assert.equal(state.phase, 'resizing');

    state = update(optimizer, 'remote-a', 16, 1010, 1010, { max: 32 });
    assert.equal(state.phase, 'settling');
    state = update(optimizer, 'remote-a', 16, 1011, 1011, { max: 32 });
    assert.equal(state.phase, 'measuring');

    state = update(optimizer, 'remote-a', 16, 2111, 2011, { max: 32 });
    assert.equal(state.baseline_envs, 16);
    assert.equal(state.baseline_sps, 1100);
    assert.equal(state.desired_envs, 32);
    assert.match(state.decision, /32/);
});

test('optimizer retries a starved sample instead of treating producer activity as zero useful throughput', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
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
    assert.equal(state.phase, 'settling');
    assert.match(state.decision, /global learner did not advance/);

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
    assert.equal(state.desired_envs, 16);
});

test('one worker owns the capacity search while other workers wait', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        retestMs: 60_000,
        minImprovementRatio: 0.03,
    });

    update(optimizer, 'remote-a', 1, 0, 0, { max: 16 });
    update(optimizer, 'remote-b', 1, 0, 0, { max: 16 });

    let stateA = update(optimizer, 'remote-a', 1, 1000, 1000, { max: 16 });
    assert.equal(stateA.desired_envs, 2);
    assert.equal(stateA.probing, true);

    let stateB = update(optimizer, 'remote-b', 1, 1000, 1000, { max: 16 });
    assert.equal(stateB.desired_envs, 1);
    assert.equal(stateB.phase, 'waiting');
    assert.equal(stateB.probing, false);

    update(optimizer, 'remote-a', 2, 1010, 1010, { max: 16 });
    update(optimizer, 'remote-a', 2, 1011, 1011, { max: 16 });
    stateA = update(optimizer, 'remote-a', 2, 2211, 2011, { max: 16 });
    assert.equal(stateA.baseline_envs, 2);
    assert.equal(stateA.desired_envs, 4);
    assert.equal(stateA.probing, true);

    stateB = update(optimizer, 'remote-b', 1, 2200, 2012, { max: 16 });
    assert.equal(stateB.phase, 'waiting');
    assert.equal(stateB.desired_envs, 1);
});

test('optimizer rolls back a slower probe before another worker may measure', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        retestMs: 60_000,
        minImprovementRatio: 0.03,
        regressionRatio: 0.05,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 9 });
    let stateA = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 9 });
    assert.equal(stateA.desired_envs, 9);

    update(optimizer, 'remote-b', 6, 0, 1001, { max: 16 });
    const stateB = update(optimizer, 'remote-b', 6, 800, 2001, { max: 16 });
    assert.equal(stateB.desired_envs, 6);
    assert.equal(stateB.phase, 'waiting');

    update(optimizer, 'remote-a', 9, 1010, 2010, { max: 9 });
    update(optimizer, 'remote-a', 9, 1011, 2011, { max: 9 });
    stateA = update(optimizer, 'remote-a', 9, 1911, 3011, { max: 9 });
    assert.equal(stateA.desired_envs, 8);
    assert.equal(stateA.probing, true);
    assert.match(stateA.decision, /throughput regressed/);

    const waitingB = update(optimizer, 'remote-b', 6, 1600, 3012, { max: 16 });
    assert.equal(waitingB.phase, 'waiting');
    assert.equal(waitingB.probing, false);

    stateA = update(optimizer, 'remote-a', 8, 1920, 3020, { max: 9 });
    assert.equal(stateA.phase, 'settling');
    assert.equal(stateA.probing, true);
});

test('optimizer backs off immediately when a probed worker process stops', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 9 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 9 });
    assert.equal(state.desired_envs, 9);

    state = update(
        optimizer,
        'remote-a',
        9,
        1010,
        1010,
        { max: 9, processState: 'stopped' },
    );
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, true);
    assert.match(state.decision, /not running/);

    state = update(optimizer, 'remote-a', 8, 1020, 1020, { max: 9 });
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.probing, false);
});

test('optimizer backs off a probe that never produces global learner-step metrics', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        metricsTimeoutMs: 100,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 9 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 9 });
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);

    state = update(
        optimizer,
        'remote-a',
        9,
        null,
        1010,
        { max: 9, learnerStepValue: null },
    );
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);

    state = update(
        optimizer,
        'remote-a',
        9,
        null,
        1111,
        { max: 9, learnerStepValue: null },
    );
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, true);
    assert.match(state.decision, /no global learner-step metrics/);

    state = update(optimizer, 'remote-a', 8, 1000, 1120, { max: 9 });
    assert.equal(state.phase, 'settling');
    assert.equal(state.probing, true);
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
        { max: 9, lastError: 'Unity communicator stopped' },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, false);
    assert.equal(state.stability_hold_until_ms, 10_000);
    assert.match(state.last_instability_reason, /Unity communicator stopped/);

    state = update(optimizer, 'remote-a', 8, 500, 5_000, { max: 9 });
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.desired_envs, 8);
    assert.equal(state.probing, false);

    state = update(optimizer, 'remote-a', 8, 1000, 10_001, { max: 9 });
    assert.equal(state.phase, 'settling');
    assert.match(state.decision, /collecting fresh baseline/);

    state = update(optimizer, 'remote-a', 8, 1000, 10_002, { max: 9 });
    assert.equal(state.phase, 'measuring');
    state = update(optimizer, 'remote-a', 8, 2000, 11_002, { max: 9 });
    assert.equal(state.baseline_envs, 8);
    assert.equal(state.desired_envs, 9);
    assert.equal(state.probing, true);
});

test('unexplained stopped heartbeat is instability even when brief', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1_000_000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    const state = update(optimizer, 'remote-a', 8, 100, 1000, {
        max: 16,
        processState: 'stopped',
    });
    assert.equal(state.phase, 'stability-hold');
    assert.match(state.last_instability_reason, /worker process state stopped/);
});

test('planned stopping heartbeat during reconciliation is not instability', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1_000_000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    const state = update(optimizer, 'remote-a', 8, 100, 1000, {
        max: 16,
        processState: 'stopping',
        reconciliationPhase: 'launching managed actor',
    });

    assert.notEqual(state.phase, 'stability-hold');
    assert.equal(state.stability_hold_until_ms, 0);
});

test('unexplained stopping heartbeat remains immediate instability', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1_000_000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    const state = update(optimizer, 'remote-a', 8, 100, 1000, {
        max: 16,
        processState: 'stopping',
    });

    assert.equal(state.phase, 'stability-hold');
    assert.match(state.last_instability_reason, /worker process state stopping/);
});

test('active reconciliation keeps planned stopped worker out of stability hold', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1_000_000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    let state = update(optimizer, 'remote-a', 8, 100, 1000, {
        max: 16,
        processState: 'stopped',
        reconciliationPhase: 'ensuring canonical build',
    });
    assert.notEqual(state.phase, 'stability-hold');

    state = update(optimizer, 'remote-a', 8, 100, 120_000, {
        max: 16,
        processState: 'stopped',
        reconciliationPhase: 'ensuring canonical build',
    });
    assert.notEqual(state.phase, 'stability-hold');
});

test('BrokerStaleActor freshness races do not create optimizer instability', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    const state = update(
        optimizer,
        'remote-a',
        8,
        100,
        1000,
        {
            max: 16,
            sessionFailures: 48,
            failureAgeSeconds: 0,
            failureType: 'BrokerStaleActor',
        },
    );

    assert.notEqual(state.phase, 'stability-hold');
    assert.equal(state.stability_hold_until_ms, 0);
});

test('healthy worker clears legacy hold caused by BrokerStaleActor resyncs', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    const internal = optimizer.states.get('remote-a');
    internal.phase = 'stability-hold';
    internal.instability_hold_until_ms = 20_000;
    internal.last_instability_reason = 'WAN actor session failure';
    internal.last_instability_ms = 1_000;

    const state = update(
        optimizer,
        'remote-a',
        8,
        100,
        5_000,
        {
            max: 16,
            sessionFailures: 48,
            failureAgeSeconds: 0,
            failureType: 'BrokerStaleActor',
        },
    );

    assert.equal(state.phase, 'settling');
    assert.equal(state.stability_hold_until_ms, 5_000);
    assert.match(state.decision, /collecting fresh baseline/);
});

test('control transport recovery does not create optimizer instability', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    let state = update(
        optimizer,
        'remote-a',
        8,
        100,
        1000,
        {
            max: 16,
            lastError: 'ControlUnavailable: POST /v1/heartbeat: timed out',
        },
    );
    assert.notEqual(state.phase, 'stability-hold');
    assert.equal(state.stability_hold_until_ms, 0);

    state = update(
        optimizer,
        'remote-a',
        8,
        100,
        2000,
        {
            max: 16,
            processState: 'stopped',
            lastError: 'ControlUnavailable: POST /v1/heartbeat: timed out',
        },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.match(state.last_instability_reason, /worker process state stopped/);
});

test('healthy worker clears legacy stability hold caused only by ControlUnavailable', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    const internal = optimizer.states.get('remote-a');
    internal.phase = 'stability-hold';
    internal.instability_hold_until_ms = 20_000;
    internal.last_instability_reason =
        'worker-reported error: ControlUnavailable: POST /v1/heartbeat: timed out';
    internal.last_instability_ms = 1_000;

    const state = update(
        optimizer,
        'remote-a',
        8,
        100,
        5_000,
        {
            max: 16,
            lastError: 'ControlUnavailable: POST /v1/heartbeat: timed out',
        },
    );

    assert.equal(state.phase, 'settling');
    assert.equal(state.stability_hold_until_ms, 5_000);
    assert.match(state.decision, /collecting fresh baseline/);
});

test('healthy worker clears stability hold caused by a resolved rollout stall', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    const internal = optimizer.states.get('remote-a');
    internal.phase = 'stability-hold';
    internal.instability_hold_until_ms = 20_000;
    internal.last_instability_reason =
        'worker-reported error: managed child rollout has made no progress for 1902.6 seconds';
    internal.last_instability_ms = 1_000;

    const state = update(
        optimizer,
        'remote-a',
        8,
        100,
        5_000,
        {
            max: 16,
            processState: 'running',
            lastError: '',
        },
    );

    assert.equal(state.phase, 'settling');
    assert.equal(state.stability_hold_until_ms, 5_000);
    assert.match(state.decision, /collecting fresh baseline/);
});

test('recent internal WAN actor failure holds probes without extending the hold each heartbeat', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    let state = update(
        optimizer,
        'remote-a',
        8,
        100,
        20_000,
        {
            max: 16,
            sessionFailures: 2,
            failureAgeSeconds: 4,
            failureType: 'RuntimeError',
            failureMessage: 'WAN actor background task failed: ValueError: bad state',
        },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.stability_hold_until_ms, 26_000);
    assert.match(state.decision, /WAN actor session failure/);
    assert.equal(
        state.last_instability_reason,
        'WAN actor session failure: RuntimeError: ' +
            'WAN actor background task failed: ValueError: bad state',
    );

    state = update(
        optimizer,
        'remote-a',
        8,
        200,
        22_000,
        {
            max: 16,
            sessionFailures: 2,
            failureAgeSeconds: 6,
        },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.stability_hold_until_ms, 26_000);

    state = update(
        optimizer,
        'remote-a',
        8,
        300,
        26_001,
        {
            max: 16,
            sessionFailures: 2,
            failureAgeSeconds: 10.001,
        },
    );
    assert.equal(state.phase, 'settling');
    assert.match(state.decision, /collecting fresh baseline/);
});

test('repeated WAN failures at an accepted baseline back off the environment count', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 0,
        measurementMs: 1000,
        cooldownMs: 0,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 5, 0, 0, { max: 8 });
    const internal = optimizer.states.get('remote-a');
    Object.assign(internal, {
        baseline_envs: 5,
        baseline_sps: 200,
        desired_envs: 5,
        phase: 'stable',
        last_session_failures_total: 0,
        consecutive_baseline_session_failures: 0,
    });

    let state = update(
        optimizer,
        'remote-a',
        5,
        100,
        1000,
        {
            max: 8,
            sessionFailures: 1,
            failureAgeSeconds: 0,
        },
    );
    assert.equal(state.phase, 'stability-hold');
    assert.equal(state.desired_envs, 5);
    assert.equal(internal.consecutive_baseline_session_failures, 1);

    state = update(
        optimizer,
        'remote-a',
        5,
        100,
        2000,
        {
            max: 8,
            sessionFailures: 2,
            failureAgeSeconds: 0,
        },
    );
    assert.equal(state.phase, 'resizing');
    assert.equal(state.desired_envs, 4);
    assert.equal(state.baseline_envs, 4);
    assert.match(state.decision, /repeated WAN actor session failures at 5 envs/);

    // Repeated heartbeats for the same recorded failure must not ratchet the count downward.
    state = update(
        optimizer,
        'remote-a',
        5,
        100,
        2100,
        {
            max: 8,
            sessionFailures: 2,
            failureAgeSeconds: 0.1,
        },
    );
    assert.equal(state.desired_envs, 4);

    state = update(
        optimizer,
        'remote-a',
        4,
        0,
        2200,
        {
            max: 8,
            processState: 'starting',
            sessionFailures: 2,
            failureAgeSeconds: 0.2,
        },
    );
    assert.equal(state.desired_envs, 4);
});

test('failed live resize probe keeps the running baseline and backs off without restart', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 3, 0, 0, { max: 4 });
    let state = update(optimizer, 'remote-a', 3, 300, 1000, { max: 4 });
    assert.equal(state.baseline_envs, 3);
    assert.equal(state.desired_envs, 4);
    assert.equal(state.probing, true);

    state = update(
        optimizer,
        'remote-a',
        3,
        320,
        1100,
        {
            max: 4,
            processState: 'running',
            resizeFailedTarget: 4,
            resizeError: 'RuntimeError: candidate spec mismatch',
        },
    );

    assert.equal(state.desired_envs, 3);
    assert.equal(state.phase, 'settling');
    assert.equal(state.probing, true);
    assert.equal(state.stability_hold_until_ms, 0);
    assert.match(state.decision, /live resize failed/);
    assert.equal(optimizer.states.get('remote-a').blocked_up, true);
});

test('runtime cutover clears old optimizer instability and baseline state', () => {
    const optimizer = new TrainingEnvOptimizer({
        warmupMs: 1000,
        measurementMs: 1000,
        instabilityHoldMs: 60_000,
    });
    const oldRuntime = 'a'.repeat(64);
    const newRuntime = 'b'.repeat(64);

    update(
        optimizer,
        'remote-a',
        4,
        100,
        0,
        { max: 48, runtimeVersionValue: oldRuntime },
    );
    const internal = optimizer.states.get('remote-a');
    Object.assign(internal, {
        baseline_envs: 4,
        baseline_sps: 100,
        desired_envs: 4,
        phase: 'stability-hold',
        instability_hold_until_ms: 60_000,
        last_instability_ms: 0,
        last_instability_reason: 'WAN actor session failure',
    });

    const state = update(
        optimizer,
        'remote-a',
        4,
        100,
        1000,
        { max: 48, runtimeVersionValue: newRuntime },
    );

    assert.equal(state.phase, 'settling');
    assert.equal(state.baseline_envs, null);
    assert.equal(state.desired_envs, 4);
    assert.equal(state.stability_hold_until_ms, 0);
    assert.match(state.decision, /worker runtime changed/);
});

test('recent WAN failure aborts a probe with the correct reason', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 9 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 9 });
    assert.equal(state.desired_envs, 9);

    state = update(
        optimizer,
        'remote-a',
        9,
        1010,
        1010,
        {
            max: 9,
            processState: 'running',
            sessionFailures: 1,
            failureAgeSeconds: 1,
        },
    );

    assert.equal(state.desired_envs, 8);
    assert.match(state.decision, /WAN actor session failed/);
});

test('fresh worker startup does not create an instability hold before a baseline exists', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 1000,
        measurementMs: 1000,
        instabilityHoldMs: 10_000,
    });

    const state = update(
        optimizer,
        'remote-a',
        1,
        0,
        0,
        { max: 16, processState: 'starting' },
    );

    assert.equal(state.phase, 'settling');
    assert.equal(state.stability_hold_until_ms, 0);
    assert.equal(state.last_instability_ms, null);
    assert.match(state.decision, /waiting for managed worker/);
});

test('live env-count transition stays in resizing until the requested count is reached', () => {
    const optimizer = new TrainingEnvOptimizer({
        settleMs: 0,
        measurementMs: 1000,
        instabilityHoldMs: 10_000,
    });

    update(optimizer, 'remote-a', 8, 0, 0, { max: 16 });
    let state = update(optimizer, 'remote-a', 8, 1000, 1000, { max: 16 });
    assert.equal(state.desired_envs, 16);

    state = update(
        optimizer,
        'remote-a',
        9,
        1050,
        1001,
        { max: 16, processState: 'running' },
    );
    assert.equal(state.phase, 'resizing');
    assert.equal(state.desired_envs, 16);
    assert.equal(state.stability_hold_until_ms, 0);

    state = update(
        optimizer,
        'remote-a',
        15,
        1100,
        1002,
        { max: 16, processState: 'running' },
    );
    assert.equal(state.phase, 'resizing');
    assert.equal(state.desired_envs, 16);
    assert.equal(state.stability_hold_until_ms, 0);

    state = update(
        optimizer,
        'remote-a',
        16,
        1150,
        1010,
        { max: 16, processState: 'running' },
    );
    assert.equal(state.phase, 'settling');
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
    assert.equal(state.desired_envs, 16);

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
