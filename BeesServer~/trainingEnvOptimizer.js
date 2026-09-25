'use strict';

const DEFAULT_MIN_ENVS = 1;
const DEFAULT_MAX_ENVS = 64;
const DEFAULT_WARMUP_MS = 20_000;
const DEFAULT_MEASUREMENT_MS = 60_000;
const DEFAULT_COOLDOWN_MS = 20_000;
const DEFAULT_RETEST_MS = 5 * 60_000;
const DEFAULT_MIN_IMPROVEMENT_RATIO = 0.03;
const DEFAULT_REGRESSION_RATIO = 0.05;

function finiteInteger(value) {
    return Number.isInteger(value) && !Number.isNaN(value);
}

function normalizeCapacity(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const auto = value.auto === true;
    const current = value.current_envs;
    const minimum = value.min_envs;
    const maximum = value.max_envs;
    if (!finiteInteger(current) || !finiteInteger(minimum) || !finiteInteger(maximum) ||
        minimum < DEFAULT_MIN_ENVS || maximum > DEFAULT_MAX_ENVS ||
        minimum > maximum || current < minimum || current > maximum) {
        return null;
    }
    return {
        auto,
        current_envs: current,
        min_envs: minimum,
        max_envs: maximum,
    };
}

function acceptedSteps(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const total = throughput.accepted_steps_total;
    if (!finiteInteger(total) || total < 0) return null;
    return total;
}

function initialStep(envs) {
    return Math.max(1, Math.min(4, Math.round(envs / 8)));
}

class TrainingEnvOptimizer {
    constructor(options = {}) {
        this.warmupMs = Number(options.warmupMs ?? DEFAULT_WARMUP_MS);
        this.measurementMs = Number(options.measurementMs ?? DEFAULT_MEASUREMENT_MS);
        this.cooldownMs = Number(options.cooldownMs ?? DEFAULT_COOLDOWN_MS);
        this.retestMs = Number(options.retestMs ?? DEFAULT_RETEST_MS);
        this.minImprovementRatio = Number(
            options.minImprovementRatio ?? DEFAULT_MIN_IMPROVEMENT_RATIO);
        this.regressionRatio = Number(options.regressionRatio ?? DEFAULT_REGRESSION_RATIO);
        for (const [label, value] of Object.entries({
            warmupMs: this.warmupMs,
            measurementMs: this.measurementMs,
            cooldownMs: this.cooldownMs,
            retestMs: this.retestMs,
        })) {
            if (!Number.isFinite(value) || value < 0) {
                throw new Error('training env optimizer ' + label + ' must be non-negative');
            }
        }
        if (!Number.isFinite(this.minImprovementRatio) || this.minImprovementRatio < 0 ||
            !Number.isFinite(this.regressionRatio) || this.regressionRatio < 0) {
            throw new Error('training env optimizer ratios must be non-negative');
        }
        this.states = new Map();
        this.activeProbeTrainerId = null;
    }

    _newState(trainerId, contextKey, capacity, now) {
        return {
            trainer_id: trainerId,
            context_key: contextKey,
            desired_envs: capacity.current_envs,
            min_envs: capacity.min_envs,
            max_envs: capacity.max_envs,
            baseline_envs: null,
            baseline_sps: null,
            direction: capacity.current_envs >= capacity.max_envs ? -1 : 1,
            step: initialStep(capacity.current_envs),
            blocked_up: false,
            blocked_down: false,
            phase: 'warmup',
            phase_started_ms: now,
            measurement_started_ms: null,
            measurement_start_steps: null,
            source_steps: null,
            last_sps: null,
            last_decision: 'collecting baseline',
            cooldown_until_ms: 0,
            retest_after_ms: 0,
            last_update_ms: now,
        };
    }

    _releaseProbe(trainerId) {
        if (this.activeProbeTrainerId === trainerId) {
            this.activeProbeTrainerId = null;
        }
    }

    _resetMeasurement(state, now, totalSteps, reason) {
        state.phase = 'warmup';
        state.phase_started_ms = now;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.source_steps = totalSteps;
        if (reason) state.last_decision = reason;
    }

    _target(state, capacity, direction) {
        const target = state.baseline_envs + direction * state.step;
        return Math.max(capacity.min_envs, Math.min(capacity.max_envs, target));
    }

    _directionBlocked(state, direction) {
        return direction > 0 ? state.blocked_up : state.blocked_down;
    }

    _blockDirection(state, direction) {
        if (direction > 0) state.blocked_up = true;
        else state.blocked_down = true;
    }

    _startProbe(state, capacity, now, direction) {
        if (this.activeProbeTrainerId && this.activeProbeTrainerId !== state.trainer_id) {
            state.phase = 'stable';
            state.retest_after_ms = Math.max(state.retest_after_ms, now + this.cooldownMs);
            state.last_decision = 'waiting for another worker probe';
            return false;
        }
        const target = this._target(state, capacity, direction);
        if (target === state.baseline_envs) {
            this._blockDirection(state, direction);
            return false;
        }
        this.activeProbeTrainerId = state.trainer_id;
        state.direction = direction;
        state.desired_envs = target;
        state.phase = 'awaiting-restart';
        state.phase_started_ms = now;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.source_steps = null;
        state.last_decision = 'probing ' + state.baseline_envs + '->' + target;
        return true;
    }

    _chooseProbe(state, capacity, now) {
        const preferred = state.direction;
        const alternate = -preferred;
        if (!this._directionBlocked(state, preferred) &&
            this._startProbe(state, capacity, now, preferred)) {
            return;
        }
        if (!this._directionBlocked(state, alternate) &&
            this._startProbe(state, capacity, now, alternate)) {
            return;
        }
        this._releaseProbe(state.trainer_id);
        state.desired_envs = state.baseline_envs;
        state.phase = 'stable';
        state.retest_after_ms = now + this.retestMs;
        state.last_decision = 'stable near measured optimum';
    }

    _finishMeasurement(state, capacity, now, sps) {
        state.last_sps = sps;
        if (state.baseline_envs === null || state.baseline_sps === null) {
            state.baseline_envs = capacity.current_envs;
            state.baseline_sps = sps;
            state.desired_envs = capacity.current_envs;
            state.step = initialStep(capacity.current_envs);
            state.blocked_up = false;
            state.blocked_down = false;
            state.last_decision = 'baseline ' + sps.toFixed(1) + ' steps/s';
            this._chooseProbe(state, capacity, now);
            return;
        }

        if (capacity.current_envs === state.baseline_envs) {
            state.baseline_sps = sps;
            state.desired_envs = state.baseline_envs;
            state.last_decision = 'baseline refreshed at ' + sps.toFixed(1) + ' steps/s';
            this._chooseProbe(state, capacity, now);
            return;
        }

        const baseline = Math.max(0.000001, state.baseline_sps);
        const ratio = (sps - baseline) / baseline;
        const direction = capacity.current_envs > state.baseline_envs ? 1 : -1;
        if (ratio >= this.minImprovementRatio) {
            state.baseline_envs = capacity.current_envs;
            state.baseline_sps = sps;
            state.desired_envs = capacity.current_envs;
            state.direction = direction;
            state.step = Math.max(1, state.step);
            state.blocked_up = false;
            state.blocked_down = false;
            state.last_decision =
                'accepted ' + capacity.current_envs + ' envs at ' + sps.toFixed(1) + ' steps/s';
            this._releaseProbe(state.trainer_id);
            this._chooseProbe(state, capacity, now);
            return;
        }

        const materiallyWorse = ratio <= -this.regressionRatio;
        if (state.step > 1) {
            state.step = Math.max(1, Math.floor(state.step / 2));
        } else {
            this._blockDirection(state, direction);
        }
        state.desired_envs = state.baseline_envs;
        state.direction = -direction;
        state.phase = 'awaiting-restart';
        state.phase_started_ms = now;
        state.cooldown_until_ms = now + this.cooldownMs;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.source_steps = null;
        state.last_decision = (materiallyWorse ? 'backing off' : 'no material gain') +
            ' from ' + capacity.current_envs + ' envs (' + sps.toFixed(1) + ' vs ' +
            state.baseline_sps.toFixed(1) + ' steps/s)';
        // Keep the cluster-wide probe lock until this worker has actually returned to the
        // accepted baseline. Otherwise another worker could begin a probe during restart/backoff.
    }

    update(record, context = {}) {
        const now = Number(context.now);
        const timestamp = Number.isFinite(now) ? now : Date.now();
        const capacity = normalizeCapacity(record && record.worker_capacity);
        const totalSteps = acceptedSteps(record && record.metrics);
        const contextKey = String(context.contextKey || '');
        if (this.activeProbeTrainerId && this.activeProbeTrainerId !== record?.trainer_id) {
            const active = this.states.get(this.activeProbeTrainerId);
            const staleAfter = this.warmupMs + this.measurementMs + this.cooldownMs + 60_000;
            if (!active || timestamp - active.last_update_ms > staleAfter) {
                this.activeProbeTrainerId = null;
            }
        }
        const enabled = context.enabled === true &&
            record && record.role === 'dedicated' &&
            record.trainer_id !== 'central-learner' &&
            capacity && capacity.auto;

        if (!capacity) {
            this._releaseProbe(record && record.trainer_id);
            return null;
        }

        let state = this.states.get(record.trainer_id);
        const capacityChanged = state && (
            state.min_envs !== capacity.min_envs ||
            state.max_envs !== capacity.max_envs
        );
        if (!state || state.context_key !== contextKey || capacityChanged) {
            this._releaseProbe(record.trainer_id);
            state = this._newState(record.trainer_id, contextKey, capacity, timestamp);
            if (capacityChanged) {
                state.last_decision = 'worker capacity changed; collecting new baseline';
            }
            this.states.set(record.trainer_id, state);
        }
        state.last_update_ms = timestamp;

        if (!enabled) {
            this._releaseProbe(record.trainer_id);
            state.desired_envs = capacity.current_envs;
            const pausedPhase = capacity.auto ? 'paused' : 'manual';
            const pausedDecision = capacity.auto ? 'optimizer paused' : 'manual env count';
            this._resetMeasurement(state, timestamp, totalSteps, pausedDecision);
            state.phase = pausedPhase;
            return this.snapshot(record.trainer_id);
        }

        if (capacity.current_envs !== state.desired_envs) {
            state.phase = 'awaiting-restart';
            state.phase_started_ms = timestamp;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.source_steps = totalSteps;
            return this.snapshot(record.trainer_id);
        }

        if (totalSteps === null) {
            this._resetMeasurement(state, timestamp, null, 'waiting for accepted-step metrics');
            return this.snapshot(record.trainer_id);
        }

        if (state.source_steps !== null && totalSteps < state.source_steps) {
            this._resetMeasurement(state, timestamp, totalSteps, 'throughput counter restarted');
            return this.snapshot(record.trainer_id);
        }
        state.source_steps = totalSteps;

        if (timestamp < state.cooldown_until_ms) {
            state.phase = 'cooldown';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'stable') {
            if (timestamp < state.retest_after_ms) return this.snapshot(record.trainer_id);
            state.blocked_up = false;
            state.blocked_down = false;
            state.step = initialStep(state.baseline_envs || capacity.current_envs);
            state.direction = capacity.current_envs >= capacity.max_envs ? -1 : 1;
            this._chooseProbe(state, capacity, timestamp);
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'awaiting-restart' || state.phase === 'cooldown') {
            if (
                state.baseline_envs !== null &&
                state.desired_envs === state.baseline_envs &&
                capacity.current_envs === state.baseline_envs
            ) {
                this._releaseProbe(state.trainer_id);
            }
            this._resetMeasurement(state, timestamp, totalSteps, 'warming after env change');
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'warmup') {
            if (timestamp - state.phase_started_ms < this.warmupMs) {
                return this.snapshot(record.trainer_id);
            }
            state.phase = 'measuring';
            state.measurement_started_ms = timestamp;
            state.measurement_start_steps = totalSteps;
            state.last_decision = 'measuring accepted steps';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'measuring') {
            const elapsed = timestamp - state.measurement_started_ms;
            if (elapsed < this.measurementMs) return this.snapshot(record.trainer_id);
            const delta = totalSteps - state.measurement_start_steps;
            const sps = elapsed > 0 ? (delta * 1000) / elapsed : 0;
            this._finishMeasurement(state, capacity, timestamp, sps);
            return this.snapshot(record.trainer_id);
        }

        this._resetMeasurement(state, timestamp, totalSteps, 'resetting optimizer measurement');
        return this.snapshot(record.trainer_id);
    }

    snapshot(trainerId) {
        const state = this.states.get(trainerId);
        if (!state) return null;
        return {
            enabled: !['manual', 'paused'].includes(state.phase),
            phase: state.phase,
            desired_envs: state.desired_envs,
            baseline_envs: state.baseline_envs,
            baseline_sps: state.baseline_sps === null ? null : Number(state.baseline_sps.toFixed(2)),
            measured_sps: state.last_sps === null ? null : Number(state.last_sps.toFixed(2)),
            direction: state.direction,
            step: state.step,
            decision: state.last_decision,
            probing: this.activeProbeTrainerId === trainerId,
        };
    }

    desiredEnvCount(trainerId) {
        const state = this.states.get(trainerId);
        return state && Number.isInteger(state.desired_envs) ? state.desired_envs : null;
    }
}

module.exports = {
    TrainingEnvOptimizer,
    normalizeCapacity,
    acceptedSteps,
    initialStep,
};
