'use strict';

const DEFAULT_MIN_ENVS = 1;
const DEFAULT_MAX_ENVS = 64;
const DEFAULT_WARMUP_MS = 60_000;
const DEFAULT_MEASUREMENT_MS = 5 * 60_000;
const DEFAULT_COOLDOWN_MS = 0;
const DEFAULT_RETEST_MS = 30 * 60_000;
const DEFAULT_INSTABILITY_HOLD_MS = 15 * 60_000;
const DEFAULT_METRICS_TIMEOUT_MS = 3 * 60_000;
const DEFAULT_MIN_IMPROVEMENT_RATIO = 0.03;
const DEFAULT_REGRESSION_RATIO = 0.05;

function finiteInteger(value) {
    return Number.isSafeInteger(value);
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

function learnerConsumedSteps(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const total = throughput.learner_consumed_steps_total;
    if (!finiteInteger(total) || total < 0) return null;
    return total;
}

function learnerStep(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const total = throughput.learner_step_total;
    if (!finiteInteger(total) || total < 0) return null;
    return total;
}

function optimizationSteps(metrics) {
    const globalStep = learnerStep(metrics);
    return globalStep === null ? learnerConsumedSteps(metrics) : globalStep;
}

function producerAcceptedSteps(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const total = throughput.accepted_steps_total;
    if (!finiteInteger(total) || total < 0) return null;
    return total;
}

function runtimeVersion(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return '';
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return '';
    const value = typeof throughput.runtime_version === 'string'
        ? throughput.runtime_version.trim().toLowerCase()
        : '';
    return /^[0-9a-f]{64}$/.test(value) ? value : '';
}

function policyCycle(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const cycle = throughput.policy_cycle;
    if (!finiteInteger(cycle) || cycle < 0) return null;
    return cycle;
}

function sessionFailureCount(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const count = throughput.session_failures_total;
    if (!finiteInteger(count) || count < 0) return null;
    return count;
}

function sessionFailureType(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return '';
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return '';
    return typeof throughput.last_session_failure_type === 'string'
        ? throughput.last_session_failure_type.trim()
        : '';
}

function sessionFailureMessage(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return '';
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return '';
    return typeof throughput.last_session_failure_message === 'string'
        ? throughput.last_session_failure_message.trim()
        : '';
}

function envResizeFailure(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const target = throughput.env_resize_failed_target;
    const error = typeof throughput.env_resize_error === 'string'
        ? throughput.env_resize_error.trim()
        : '';
    if (!finiteInteger(target) || target < DEFAULT_MIN_ENVS || target > DEFAULT_MAX_ENVS || !error) {
        return null;
    }
    return { target_envs: target, error };
}

function recentSessionFailureAgeSeconds(metrics) {
    if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return null;
    const throughput = metrics.throughput;
    if (!throughput || typeof throughput !== 'object' || Array.isArray(throughput)) return null;
    const count = sessionFailureCount(metrics);
    const age = throughput.seconds_since_last_session_failure;
    if (count === null || count <= 0) return null;
    // BrokerStaleActor means the learner advanced policy/control state while an old request
    // was still in flight. Actors resynchronize from it; it is not an unstable worker session.
    if (sessionFailureType(metrics) === 'BrokerStaleActor') return null;
    if (typeof age !== 'number' || !Number.isFinite(age) || age < 0) return null;
    return age;
}

function initialStep(envs) {
    return Math.max(1, Math.floor(envs));
}

function cycleAwareStep(envs) {
    // Retained as a compatibility export. Throughput probes no longer wait for policy cycles;
    // both old and new telemetry use the same geometric capacity search.
    return initialStep(envs);
}

function downwardStep(envs) {
    return Math.max(1, Math.floor(envs / 2));
}

class TrainingEnvOptimizer {
    constructor(options = {}) {
        this.warmupMs = Number(options.settleMs ?? options.warmupMs ?? DEFAULT_WARMUP_MS);
        this.measurementMs = Number(options.measurementMs ?? DEFAULT_MEASUREMENT_MS);
        this.cooldownMs = Number(options.cooldownMs ?? DEFAULT_COOLDOWN_MS);
        this.retestMs = Number(options.retestMs ?? DEFAULT_RETEST_MS);
        this.instabilityHoldMs = Number(
            options.instabilityHoldMs ?? DEFAULT_INSTABILITY_HOLD_MS);
        this.metricsTimeoutMs = Number(options.metricsTimeoutMs ?? DEFAULT_METRICS_TIMEOUT_MS);
        this.minImprovementRatio = Number(
            options.minImprovementRatio ?? DEFAULT_MIN_IMPROVEMENT_RATIO);
        this.regressionRatio = Number(options.regressionRatio ?? DEFAULT_REGRESSION_RATIO);
        for (const [label, value] of Object.entries({
            warmupMs: this.warmupMs,
            measurementMs: this.measurementMs,
            cooldownMs: this.cooldownMs,
            retestMs: this.retestMs,
            instabilityHoldMs: this.instabilityHoldMs,
            metricsTimeoutMs: this.metricsTimeoutMs,
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
            step: capacity.current_envs >= capacity.max_envs
                ? downwardStep(capacity.current_envs)
                : initialStep(capacity.current_envs),
            moved_direction: 0,
            runtime_version: '',
            blocked_up: false,
            blocked_down: false,
            phase: 'settling',
            phase_started_ms: now,
            measurement_started_ms: null,
            measurement_start_steps: null,
            measurement_start_produced_steps: null,
            measurement_last_ms: null,
            measurement_last_steps: null,
            measurement_active_ms: 0,
            measurement_active_steps: 0,
            source_steps: null,
            last_sps: null,
            last_decision: 'settling before baseline measurement',
            cooldown_until_ms: 0,
            retest_after_ms: 0,
            instability_hold_until_ms: 0,
            last_instability_ms: null,
            last_instability_reason: '',
            last_session_failures_total: null,
            consecutive_baseline_session_failures: 0,
            last_update_ms: now,
            metrics_missing_since_ms: null,
            producer_sample_ms: null,
            producer_sample_steps: null,
            producer_sps: null,
            producer_efficiency: null,
        };
    }

    _releaseProbe(trainerId) {
        if (this.activeProbeTrainerId === trainerId) {
            this.activeProbeTrainerId = null;
        }
    }

    _completeProbe(trainerId) {
        this._releaseProbe(trainerId);
    }

    _resetMeasurement(state, now, totalSteps, reason) {
        state.phase = 'settling';
        state.phase_started_ms = now;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.measurement_last_ms = null;
        state.measurement_last_steps = null;
        state.measurement_active_ms = 0;
        state.measurement_active_steps = 0;
        state.source_steps = totalSteps;
        if (reason) state.last_decision = reason;
    }

    _stepForDirection(state, direction) {
        return direction > 0
            ? initialStep(state.baseline_envs)
            : downwardStep(state.baseline_envs);
    }

    _invalidateOtherMeasurements(trainerId) {
        for (const [candidateId, candidate] of this.states) {
            if (candidateId === trainerId) continue;
            if (!['stable', 'settling', 'measuring', 'waiting'].includes(candidate.phase)) continue;
            candidate.baseline_sps = null;
            candidate.last_sps = null;
            candidate.desired_envs = candidate.baseline_envs ?? candidate.desired_envs;
            candidate.phase = 'waiting';
            candidate.measurement_started_ms = null;
            candidate.measurement_start_steps = null;
            candidate.measurement_start_produced_steps = null;
            candidate.measurement_last_ms = null;
            candidate.measurement_last_steps = null;
            candidate.measurement_active_ms = 0;
            candidate.measurement_active_steps = 0;
            candidate.source_steps = null;
            candidate.last_decision =
                'cluster capacity changed; waiting to refresh global learner baseline';
        }
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
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'waiting';
            state.last_decision = 'waiting for another worker capacity search';
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
        state.phase = 'resizing';
        state.phase_started_ms = now;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.measurement_last_ms = null;
        state.measurement_last_steps = null;
        state.measurement_active_ms = 0;
        state.measurement_active_steps = 0;
        state.source_steps = null;
        state.last_decision = 'resizing for throughput probe ' +
            state.baseline_envs + '->' + target;
        return true;
    }

    _updateProducerRate(state, capacity, producedSteps, now) {
        if (producedSteps === null) return;
        if (
            state.producer_sample_ms !== null &&
            state.producer_sample_steps !== null &&
            producedSteps >= state.producer_sample_steps &&
            now > state.producer_sample_ms
        ) {
            const elapsedSeconds = (now - state.producer_sample_ms) / 1000;
            const rate = (producedSteps - state.producer_sample_steps) / elapsedSeconds;
            state.producer_sps = rate;
            state.producer_efficiency = rate / Math.max(1, capacity.current_envs);
        }
        state.producer_sample_ms = now;
        state.producer_sample_steps = producedSteps;
    }

    _producerRanks() {
        return [...this.states.values()]
            .filter(candidate =>
                candidate.producer_efficiency !== null &&
                Number.isFinite(candidate.producer_efficiency))
            .sort((left, right) =>
                right.producer_efficiency - left.producer_efficiency ||
                left.trainer_id.localeCompare(right.trainer_id));
    }

    _preferredDirection(state, capacity) {
        const ranked = this._producerRanks();
        if (ranked.length < 2 || state.producer_efficiency === null) {
            return state.direction;
        }

        const best = ranked[0];
        const worst = ranked[ranked.length - 1];
        if (
            state.trainer_id === worst.trainer_id &&
            capacity.current_envs > capacity.min_envs &&
            worst.producer_efficiency < best.producer_efficiency * 0.25
        ) {
            return -1;
        }
        return state.direction;
    }

    _shouldWaitForMoreProductiveWorker(state, direction) {
        if (direction <= 0 || state.producer_efficiency === null) return false;
        const ranked = this._producerRanks();
        return ranked.length >= 2 && ranked[0].trainer_id !== state.trainer_id;
    }

    _chooseProbe(state, capacity, now) {
        if (now < state.instability_hold_until_ms) {
            this._releaseProbe(state.trainer_id);
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'stability-hold';
            state.last_decision = 'holding env count after recent worker instability';
            return;
        }
        if (this.activeProbeTrainerId && this.activeProbeTrainerId !== state.trainer_id) {
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'waiting';
            state.last_decision = 'waiting for another worker capacity search';
            return;
        }

        const preferred = this._preferredDirection(state, capacity);
        if (this._shouldWaitForMoreProductiveWorker(state, preferred)) {
            this._releaseProbe(state.trainer_id);
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'waiting';
            state.last_decision =
                'waiting for more productive worker capacity search';
            return;
        }
        const alternate = -preferred;
        if (!this._directionBlocked(state, preferred)) {
            if (state.direction !== preferred || state.step < 1) {
                state.step = this._stepForDirection(state, preferred);
            }
            if (this._startProbe(state, capacity, now, preferred)) return;
        }
        if (!this._directionBlocked(state, alternate)) {
            state.direction = alternate;
            state.step = this._stepForDirection(state, alternate);
            if (this._startProbe(state, capacity, now, alternate)) return;
        }

        this._completeProbe(state.trainer_id);
        state.desired_envs = state.baseline_envs;
        state.phase = 'stable';
        state.retest_after_ms = now + this.retestMs;
        state.last_decision = 'stable near measured throughput optimum';
    }

    _abortProbe(state, capacity, now, reason) {
        if (state.baseline_envs === null) {
            this._releaseProbe(state.trainer_id);
            state.desired_envs = capacity.current_envs;
            this._resetMeasurement(state, now, null, reason);
            return;
        }

        const direction = capacity.current_envs === state.baseline_envs
            ? (state.desired_envs > state.baseline_envs ? 1 : -1)
            : (capacity.current_envs > state.baseline_envs ? 1 : -1);
        this._blockDirection(state, direction);
        if (state.moved_direction === direction) {
            this._blockDirection(state, -direction);
        } else if (!this._directionBlocked(state, -direction)) {
            state.direction = -direction;
            state.step = this._stepForDirection(state, -direction);
        }

        state.desired_envs = state.baseline_envs;
        if (capacity.current_envs === state.baseline_envs) {
            this._resetMeasurement(
                state,
                now,
                null,
                reason + '; settling again at ' + state.baseline_envs + ' envs',
            );
        } else {
            state.phase = 'resizing';
            state.phase_started_ms = now;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.measurement_last_ms = null;
            state.measurement_last_steps = null;
            state.measurement_active_ms = 0;
            state.measurement_active_steps = 0;
            state.source_steps = null;
            state.last_decision =
                reason + '; backing off to ' + state.baseline_envs + ' envs';
        }
        state.cooldown_until_ms = 0;
        state.metrics_missing_since_ms = null;
        // Keep the cluster-wide search lock until the accepted baseline is restored,
        // settled, and remeasured.
    }

    _backoffRepeatedSessionFailure(state, capacity, now, totalSteps) {
        const from = capacity.current_envs;
        if (from <= capacity.min_envs) return false;
        const target = Math.max(capacity.min_envs, from - 1);
        this._releaseProbe(state.trainer_id);
        state.baseline_envs = target;
        state.baseline_sps = null;
        state.last_sps = null;
        state.direction = -1;
        state.step = 1;
        state.blocked_up = true;
        state.blocked_down = false;
        state.desired_envs = target;
        state.phase = 'resizing';
        state.phase_started_ms = now;
        state.cooldown_until_ms = now + this.cooldownMs;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.measurement_last_ms = null;
        state.measurement_last_steps = null;
        state.measurement_active_ms = 0;
        state.measurement_active_steps = 0;
        state.source_steps = totalSteps;
        state.metrics_missing_since_ms = null;
        state.consecutive_baseline_session_failures = 0;
        state.last_decision =
            'repeated WAN actor session failures at ' + from +
            ' envs; backing off to ' + target + ' envs';
        return true;
    }

    _finishMeasurement(state, capacity, now, sps) {
        state.last_sps = sps;
        state.consecutive_baseline_session_failures = 0;

        if (state.baseline_envs === null || state.baseline_sps === null) {
            state.baseline_envs = capacity.current_envs;
            state.baseline_sps = sps;
            state.desired_envs = capacity.current_envs;
            state.direction = capacity.current_envs >= capacity.max_envs ? -1 : 1;
            state.step = this._stepForDirection(state, state.direction);
            state.moved_direction = 0;
            state.blocked_up = capacity.current_envs >= capacity.max_envs;
            state.blocked_down = capacity.current_envs <= capacity.min_envs;
            state.last_decision = 'baseline ' + sps.toFixed(1) + ' global learner steps/s';
            this._chooseProbe(state, capacity, now);
            return;
        }

        if (capacity.current_envs === state.baseline_envs) {
            state.baseline_sps = sps;
            state.desired_envs = state.baseline_envs;
            state.last_decision =
                'baseline refreshed at ' + sps.toFixed(1) + ' global learner steps/s';
            this._chooseProbe(state, capacity, now);
            return;
        }

        const baseline = Math.max(0.000001, state.baseline_sps);
        const ratio = (sps - baseline) / baseline;
        const direction = capacity.current_envs > state.baseline_envs ? 1 : -1;
        const previousBaseline = state.baseline_envs;

        if (ratio >= this.minImprovementRatio) {
            state.baseline_envs = capacity.current_envs;
            state.baseline_sps = sps;
            state.desired_envs = capacity.current_envs;
            state.direction = direction;
            state.step = this._stepForDirection(state, direction);
            state.moved_direction = direction;
            if (direction > 0) {
                state.blocked_up = capacity.current_envs >= capacity.max_envs;
                state.blocked_down = true;
            } else {
                state.blocked_down = capacity.current_envs <= capacity.min_envs;
                state.blocked_up = true;
            }
            state.last_decision =
                'accepted ' + capacity.current_envs + ' envs at ' +
                sps.toFixed(1) + ' global learner steps/s';
            this._invalidateOtherMeasurements(state.trainer_id);

            if (
                (direction > 0 && capacity.current_envs >= capacity.max_envs) ||
                (direction < 0 && capacity.current_envs <= capacity.min_envs)
            ) {
                this._blockDirection(state, direction);
            }
            this._chooseProbe(state, capacity, now);
            return;
        }

        const materiallyWorse = ratio <= -this.regressionRatio;
        const distance = Math.abs(capacity.current_envs - state.baseline_envs);
        if (materiallyWorse && distance > 1) {
            state.step = Math.max(1, Math.floor(distance / 2));
            state.direction = direction;
        } else {
            this._blockDirection(state, direction);
            if (state.moved_direction === direction) {
                // We already measured an improvement moving into the accepted baseline from
                // the opposite side, so once this side is exhausted the baseline is bracketed.
                this._blockDirection(state, -direction);
            } else if (!this._directionBlocked(state, -direction)) {
                state.direction = -direction;
                state.step = this._stepForDirection(state, -direction);
            }
        }

        state.desired_envs = state.baseline_envs;
        state.phase = 'resizing';
        state.phase_started_ms = now;
        state.cooldown_until_ms = 0;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.measurement_last_ms = null;
        state.measurement_last_steps = null;
        state.measurement_active_ms = 0;
        state.measurement_active_steps = 0;
        state.source_steps = null;
        state.last_decision =
            (materiallyWorse ? 'throughput regressed' : 'no material throughput gain') +
            ' at ' + capacity.current_envs + ' envs (' + sps.toFixed(1) + ' vs ' +
            state.baseline_sps.toFixed(1) + '); returning to ' +
            previousBaseline + ' envs';
        // Keep the cluster-wide search lock until the accepted baseline is restored,
        // settled, and remeasured. No other worker may contaminate this comparison.
    }

    update(record, context = {}) {
        const now = Number(context.now);
        const timestamp = Number.isFinite(now) ? now : Date.now();
        const capacity = normalizeCapacity(record && record.worker_capacity);
        const totalSteps = optimizationSteps(record && record.metrics);
        const producedSteps = producerAcceptedSteps(record && record.metrics);
        const currentRuntimeVersion = runtimeVersion(record && record.metrics);
        const sessionFailureAgeSeconds = recentSessionFailureAgeSeconds(
            record && record.metrics);
        const sessionFailuresTotal = sessionFailureCount(record && record.metrics);
        const lastSessionFailureType = sessionFailureType(record && record.metrics);
        const lastSessionFailureMessage = sessionFailureMessage(record && record.metrics);
        const resizeFailure = envResizeFailure(record && record.metrics);
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
            const trainerId = record && record.trainer_id;
            this._releaseProbe(trainerId);
            if (typeof trainerId === 'string' && trainerId) {
                this.states.delete(trainerId);
            }
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
        if (
            currentRuntimeVersion &&
            state.runtime_version &&
            currentRuntimeVersion !== state.runtime_version
        ) {
            this._releaseProbe(record.trainer_id);
            state = this._newState(record.trainer_id, contextKey, capacity, timestamp);
            state.runtime_version = currentRuntimeVersion;
            state.last_decision = 'worker runtime changed; collecting new baseline';
            this.states.set(record.trainer_id, state);
        } else if (currentRuntimeVersion && !state.runtime_version) {
            state.runtime_version = currentRuntimeVersion;
        }
        this._updateProducerRate(state, capacity, producedSteps, timestamp);
        state.last_update_ms = timestamp;
        let newSessionFailure = false;
        if (sessionFailuresTotal !== null) {
            if (
                state.last_session_failures_total === null ||
                sessionFailuresTotal < state.last_session_failures_total
            ) {
                state.last_session_failures_total = sessionFailuresTotal;
                state.consecutive_baseline_session_failures = 0;
            } else if (sessionFailuresTotal > state.last_session_failures_total) {
                newSessionFailure = true;
                state.last_session_failures_total = sessionFailuresTotal;
            }
        }

        if (!enabled) {
            this._releaseProbe(record.trainer_id);
            state.desired_envs = capacity.current_envs;
            const pausedPhase = capacity.auto ? 'paused' : 'manual';
            const pausedDecision = capacity.auto ? 'optimizer paused' : 'manual env count';
            this._resetMeasurement(state, timestamp, totalSteps, pausedDecision);
            state.phase = pausedPhase;
            return this.snapshot(record.trainer_id);
        }

        if (resizeFailure && capacity.current_envs !== state.desired_envs) {
            const failedTarget = state.desired_envs;
            const reason =
                'live resize failed while targeting ' + failedTarget +
                ' envs: ' + resizeFailure.error;

            if (
                state.baseline_envs !== null &&
                failedTarget === state.baseline_envs &&
                capacity.current_envs !== state.baseline_envs
            ) {
                // The probe already moved away from its baseline and the attempted rollback
                // failed too. The worker has explicitly rejected this target until BeesServer
                // requests a different count, so continuing to demand the old baseline creates
                // a permanent actual->desired mismatch. Reconcile to the capacity that is
                // actually alive, release the cluster-wide probe lock, and establish a fresh
                // baseline before trying another capacity change.
                this._releaseProbe(record.trainer_id);
                state.desired_envs = capacity.current_envs;
                state.baseline_envs = null;
                state.baseline_sps = null;
                state.last_sps = null;
                state.direction = capacity.current_envs >= capacity.max_envs ? -1 : 1;
                state.step = capacity.current_envs >= capacity.max_envs
                    ? downwardStep(capacity.current_envs)
                    : initialStep(capacity.current_envs);
                state.moved_direction = 0;
                state.blocked_up = false;
                state.blocked_down = false;
                state.cooldown_until_ms = 0;
                state.metrics_missing_since_ms = null;
                this._resetMeasurement(
                    state,
                    timestamp,
                    totalSteps,
                    reason + '; accepting live ' + capacity.current_envs +
                        ' envs and collecting a fresh baseline',
                );
                return this.snapshot(record.trainer_id);
            }

            this._abortProbe(
                state,
                capacity,
                timestamp,
                reason,
            );
            return this.snapshot(record.trainer_id);
        }

        const probingAwayFromBaseline =
            state.baseline_envs !== null &&
            state.desired_envs !== state.baseline_envs &&
            capacity.current_envs !== state.baseline_envs;
        const processState = typeof record.process_state === 'string'
            ? record.process_state.trim()
            : '';
        const reportedError = typeof record.last_error === 'string'
            ? record.last_error.trim()
            : '';

        const controlTransportError =
            reportedError.startsWith('ControlUnavailable:');
        const optimizerError = controlTransportError ? '' : reportedError;

        const reconciliation = record && record.metrics &&
            typeof record.metrics === 'object' &&
            !Array.isArray(record.metrics) &&
            record.metrics.reconciliation &&
            typeof record.metrics.reconciliation === 'object' &&
            !Array.isArray(record.metrics.reconciliation)
                ? record.metrics.reconciliation
                : null;
        const reconciliationActive = Boolean(
            reconciliation && String(reconciliation.phase || '').trim()
        );

        const reconciliationTransition =
            (processState === 'stopped' || processState === 'stopping') &&
            !optimizerError &&
            reconciliationActive;

        // A recovered control-plane transport interruption is not evidence that the Unity
        // worker itself is unstable. Older worker runtimes could echo ControlUnavailable back
        // through last_error on their recovery heartbeat, which otherwise creates a 15-minute
        // optimizer stability hold after connectivity has already recovered.
        if (
            processState === 'running' &&
            controlTransportError &&
            typeof state.last_instability_reason === 'string' &&
            state.last_instability_reason.startsWith(
                'worker-reported error: ControlUnavailable:')
        ) {
            state.instability_hold_until_ms = Math.min(
                state.instability_hold_until_ms,
                timestamp,
            );
        }

        if (
            processState === 'running' &&
            lastSessionFailureType === 'BrokerStaleActor' &&
            typeof state.last_instability_reason === 'string' &&
            state.last_instability_reason.startsWith('WAN actor session failure')
        ) {
            state.instability_hold_until_ms = Math.min(
                state.instability_hold_until_ms,
                timestamp,
            );
        }

        if (
            processState === 'running' &&
            !optimizerError &&
            typeof state.last_instability_reason === 'string' &&
            state.last_instability_reason.startsWith(
                'worker-reported error: managed child rollout has made no progress')
        ) {
            state.instability_hold_until_ms = Math.min(
                state.instability_hold_until_ms,
                timestamp,
            );
        }

        // Reaching the requested env count does not mean a newly launched worker is ready yet.
        // Live env-count changes do not restart the actor, but release/runtime cutovers can.
        // Start the settling clock only after the managed process reports "running".
        if (
            processState === 'starting' &&
            !optimizerError &&
            (
                state.phase === 'resizing' ||
                (state.phase === 'settling' && state.baseline_envs === null)
            )
        ) {
            state.phase_started_ms = timestamp;
            state.last_decision = 'waiting for managed worker to become running before settling';
            return this.snapshot(record.trainer_id);
        }
        const recentSessionFailure =
            sessionFailureAgeSeconds !== null &&
            sessionFailureAgeSeconds * 1000 < this.instabilityHoldMs;
        const expectedStarting =
            processState === 'starting' &&
            !optimizerError &&
            (
                state.phase === 'resizing' ||
                state.baseline_envs === null
            );
        const freshCutoverTransition =
            (processState === 'stopped' || processState === 'stopping') &&
            !optimizerError &&
            state.baseline_envs === null &&
            timestamp - state.phase_started_ms < this.warmupMs;
        const expectedTransition =
            expectedStarting || reconciliationTransition || freshCutoverTransition;
        const currentProcessFailure =
            (processState &&
                processState !== 'running' &&
                !expectedTransition) ||
            Boolean(optimizerError);
        const probeRollbackPending =
            this.activeProbeTrainerId === state.trainer_id &&
            state.baseline_envs !== null &&
            capacity.current_envs !== state.baseline_envs;
        const workerUnstable = currentProcessFailure || recentSessionFailure;
        if (workerUnstable) {
            if (recentSessionFailure && newSessionFailure && !probingAwayFromBaseline) {
                state.consecutive_baseline_session_failures += 1;
                if (
                    state.consecutive_baseline_session_failures >= 2 &&
                    this._backoffRepeatedSessionFailure(
                        state,
                        capacity,
                        timestamp,
                        totalSteps,
                    )
                ) {
                    return this.snapshot(record.trainer_id);
                }
            }
            const useSessionFailureTime = recentSessionFailure && !currentProcessFailure;
            const sessionFailureAgeMs = useSessionFailureTime
                ? sessionFailureAgeSeconds * 1000
                : 0;
            const instabilityTime = useSessionFailureTime
                ? timestamp - sessionFailureAgeMs
                : timestamp;
            const holdUntil = useSessionFailureTime
                ? timestamp + Math.max(0, this.instabilityHoldMs - sessionFailureAgeMs)
                : timestamp + this.instabilityHoldMs;
            const sessionFailureDetail = [
                lastSessionFailureType,
                lastSessionFailureMessage,
            ].filter(Boolean).join(': ');
            const instabilityReason = optimizerError
                ? 'worker-reported error: ' + optimizerError
                : recentSessionFailure
                    ? 'WAN actor session failure' +
                        (sessionFailureDetail ? ': ' + sessionFailureDetail : '')
                    : 'worker process state ' + (processState || 'unknown');
            if (
                state.last_instability_ms === null ||
                instabilityTime >= state.last_instability_ms
            ) {
                state.last_instability_reason = instabilityReason;
            }
            state.last_instability_ms = Math.max(
                state.last_instability_ms ?? Number.NEGATIVE_INFINITY,
                instabilityTime,
            );
            state.instability_hold_until_ms = Math.max(
                state.instability_hold_until_ms,
                holdUntil,
            );
            if (probingAwayFromBaseline || probeRollbackPending) {
                this._abortProbe(
                    state,
                    capacity,
                    timestamp,
                    optimizerError
                        ? 'probe worker reported an error'
                        : recentSessionFailure
                            ? 'probe WAN actor session failed'
                            : 'probe process is not running',
                );
                return this.snapshot(record.trainer_id);
            }
            this._releaseProbe(state.trainer_id);
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'stability-hold';
            state.phase_started_ms = timestamp;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.measurement_last_ms = null;
            state.measurement_last_steps = null;
            state.measurement_active_ms = 0;
            state.measurement_active_steps = 0;
            state.source_steps = totalSteps;
            state.last_decision = optimizerError
                ? 'holding env count after worker-reported error'
                : recentSessionFailure
                    ? 'holding env count after WAN actor session failure'
                    : 'holding env count after worker stopped unexpectedly';
            return this.snapshot(record.trainer_id);
        }

        if (timestamp < state.instability_hold_until_ms) {
            if (
                state.baseline_envs !== null &&
                capacity.current_envs === state.baseline_envs
            ) {
                this._releaseProbe(state.trainer_id);
            }
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'stability-hold';
            state.last_decision = 'holding env count after recent worker instability';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'stability-hold') {
            this._resetMeasurement(
                state,
                timestamp,
                totalSteps,
                'stability hold complete; collecting fresh baseline',
            );
            return this.snapshot(record.trainer_id);
        }

        if (capacity.current_envs !== state.desired_envs) {
            state.metrics_missing_since_ms = null;
            state.phase = 'resizing';
            state.phase_started_ms = timestamp;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.measurement_last_ms = null;
            state.measurement_last_steps = null;
            state.measurement_active_ms = 0;
            state.measurement_active_steps = 0;
            state.source_steps = totalSteps;
            state.last_decision =
                'resizing Unity environments ' + capacity.current_envs +
                '->' + state.desired_envs;
            return this.snapshot(record.trainer_id);
        }

        if (
            this.activeProbeTrainerId &&
            this.activeProbeTrainerId !== state.trainer_id
        ) {
            if (state.phase === 'stable' && state.baseline_sps !== null) {
                state.last_decision =
                    'holding stable env count while another worker capacity search runs';
                return this.snapshot(record.trainer_id);
            }
            state.desired_envs = state.baseline_envs ?? capacity.current_envs;
            state.phase = 'waiting';
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.measurement_last_ms = null;
            state.measurement_last_steps = null;
            state.measurement_active_ms = 0;
            state.measurement_active_steps = 0;
            state.source_steps = totalSteps;
            state.last_decision =
                'waiting for another worker capacity search before measuring global throughput';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'waiting') {
            this._resetMeasurement(
                state,
                timestamp,
                totalSteps,
                'settling before refreshing global learner baseline',
            );
            return this.snapshot(record.trainer_id);
        }

        if (totalSteps === null) {
            if (probingAwayFromBaseline) {
                if (state.metrics_missing_since_ms === null) {
                    state.metrics_missing_since_ms = timestamp;
                } else if (
                    timestamp - state.metrics_missing_since_ms >= this.metricsTimeoutMs
                ) {
                    this._abortProbe(
                        state,
                        capacity,
                        timestamp,
                        'probe produced no global learner-step metrics',
                    );
                    return this.snapshot(record.trainer_id);
                }
            }
            this._resetMeasurement(state, timestamp, null, 'waiting for global learner-step metrics');
            return this.snapshot(record.trainer_id);
        }
        state.metrics_missing_since_ms = null;

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
            const preferred = state.moved_direction < 0 ? -1 : 1;
            if (
                (preferred > 0 && state.baseline_envs >= capacity.max_envs) ||
                (preferred < 0 && state.baseline_envs <= capacity.min_envs)
            ) {
                state.retest_after_ms = timestamp + this.retestMs;
                state.last_decision = 'stable at worker capacity boundary';
                return this.snapshot(record.trainer_id);
            }
            state.direction = preferred;
            state.step = 1;
            if (preferred > 0) state.blocked_up = false;
            else state.blocked_down = false;
            this._chooseProbe(state, capacity, timestamp);
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'resizing' || state.phase === 'cooldown') {
            this._resetMeasurement(
                state,
                timestamp,
                totalSteps,
                'settling for ' + Math.round(this.warmupMs / 1000) +
                    ' seconds after env count reached ' + capacity.current_envs,
            );
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'settling') {
            if (timestamp - state.phase_started_ms < this.warmupMs) {
                return this.snapshot(record.trainer_id);
            }
            state.phase = 'measuring';
            state.measurement_started_ms = timestamp;
            state.measurement_start_steps = totalSteps;
            state.measurement_start_produced_steps = producedSteps;
            state.measurement_last_ms = timestamp;
            state.measurement_last_steps = totalSteps;
            state.measurement_active_ms = 0;
            state.measurement_active_steps = 0;
            state.last_decision =
                'measuring global learner throughput for ' +
                Math.round(this.measurementMs / 1000) + ' seconds';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'measuring') {
            const sampleElapsed = Math.max(
                0,
                timestamp - state.measurement_last_ms,
            );
            const sampleDelta = totalSteps - state.measurement_last_steps;
            if (sampleDelta < 0) {
                this._resetMeasurement(
                    state,
                    timestamp,
                    totalSteps,
                    'throughput counter restarted',
                );
                return this.snapshot(record.trainer_id);
            }

            // PPO intentionally stops learner-step progress while optimizer work runs.
            // Do not charge those stationary intervals against rollout-capacity probes.
            // Frequent worker heartbeats split normal rollout and PPO phases closely enough
            // that only the small boundary interval around a phase transition is ambiguous.
            if (sampleDelta > 0 && sampleElapsed > 0) {
                state.measurement_active_ms += sampleElapsed;
                state.measurement_active_steps += sampleDelta;
            }
            state.measurement_last_ms = timestamp;
            state.measurement_last_steps = totalSteps;

            const producedDelta =
                producedSteps !== null &&
                state.measurement_start_produced_steps !== null
                    ? producedSteps - state.measurement_start_produced_steps
                    : null;
            if (producedDelta !== null && producedDelta < 0) {
                this._resetMeasurement(
                    state,
                    timestamp,
                    totalSteps,
                    'producer throughput counter restarted',
                );
                return this.snapshot(record.trainer_id);
            }

            const elapsed = state.measurement_active_ms;
            if (elapsed < this.measurementMs) {
                return this.snapshot(record.trainer_id);
            }

            const delta = state.measurement_active_steps;
            if (delta === 0 && producedDelta !== null && producedDelta > 0) {
                this._resetMeasurement(
                    state,
                    timestamp,
                    totalSteps,
                    'worker produced rollouts but the global learner did not advance; retrying measurement',
                );
                return this.snapshot(record.trainer_id);
            }
            const sps = elapsed > 0 ? (delta * 1000) / elapsed : 0;
            this._finishMeasurement(state, capacity, timestamp, sps);
            return this.snapshot(record.trainer_id);
        }

        this._resetMeasurement(state, timestamp, totalSteps, 'resetting optimizer measurement');
        return this.snapshot(record.trainer_id);
    }

    remove(trainerId) {
        if (typeof trainerId !== 'string' || !trainerId) return false;
        const removed = this.states.delete(trainerId);
        if (this.activeProbeTrainerId === trainerId) {
            this.activeProbeTrainerId = null;
        }
        return removed;
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
            stability_hold_until_ms: state.instability_hold_until_ms,
            last_instability_ms: state.last_instability_ms,
            last_instability_reason: state.last_instability_reason,
            producer_sps: state.producer_sps === null
                ? null
                : Number(state.producer_sps.toFixed(2)),
            producer_efficiency: state.producer_efficiency === null
                ? null
                : Number(state.producer_efficiency.toFixed(3)),
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
    learnerConsumedSteps,
    learnerStep,
    optimizationSteps,
    producerAcceptedSteps,
    runtimeVersion,
    policyCycle,
    recentSessionFailureAgeSeconds,
    sessionFailureCount,
    sessionFailureType,
    sessionFailureMessage,
    envResizeFailure,
    initialStep,
    cycleAwareStep,
};
