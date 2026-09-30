'use strict';

const DEFAULT_MIN_ENVS = 1;
const DEFAULT_MAX_ENVS = 64;
const DEFAULT_WARMUP_MS = 20_000;
const DEFAULT_MEASUREMENT_MS = 60_000;
const DEFAULT_COOLDOWN_MS = 20_000;
const DEFAULT_RETEST_MS = 5 * 60_000;
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
    return Math.max(1, Math.min(4, Math.round(envs / 8)));
}

function cycleAwareStep(envs) {
    // Complete-cycle measurements are reliable enough to expand geometrically. Keep the old
    // conservative step for actors that have not yet rolled onto policy-epoch telemetry.
    return Math.max(1, Math.min(16, Math.floor(envs)));
}

class TrainingEnvOptimizer {
    constructor(options = {}) {
        this.warmupMs = Number(options.warmupMs ?? DEFAULT_WARMUP_MS);
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
        this.lastCompletedProbeTrainerId = null;
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
            cycle_aware: false,
            runtime_version: '',
            blocked_up: false,
            blocked_down: false,
            phase: 'warmup',
            phase_started_ms: now,
            warmup_policy_cycle: null,
            measurement_started_ms: null,
            measurement_start_steps: null,
            measurement_start_produced_steps: null,
            measurement_start_policy_cycle: null,
            source_steps: null,
            last_sps: null,
            last_decision: 'collecting baseline',
            cooldown_until_ms: 0,
            retest_after_ms: 0,
            instability_hold_until_ms: 0,
            last_instability_ms: null,
            last_instability_reason: '',
            last_session_failures_total: null,
            consecutive_baseline_session_failures: 0,
            last_update_ms: now,
            metrics_missing_since_ms: null,
        };
    }

    _releaseProbe(trainerId) {
        if (this.activeProbeTrainerId === trainerId) {
            this.activeProbeTrainerId = null;
        }
    }

    _completeProbe(trainerId) {
        if (this.activeProbeTrainerId === trainerId) {
            this.activeProbeTrainerId = null;
            this.lastCompletedProbeTrainerId = trainerId;
        }
    }

    _stateCanProbe(state, now) {
        if (!state || state.phase !== 'stable' || state.baseline_envs === null) return false;
        if (now < state.retest_after_ms || now < state.instability_hold_until_ms) return false;
        const canProbeUp =
            !state.blocked_up &&
            state.baseline_envs < state.max_envs;
        const canProbeDown =
            !state.blocked_down &&
            state.baseline_envs > state.min_envs;
        return canProbeUp || canProbeDown;
    }

    _hasReadyAlternateProbe(trainerId, now) {
        for (const candidate of this.states.values()) {
            if (candidate.trainer_id === trainerId) continue;
            if (this._stateCanProbe(candidate, now)) return true;
        }
        return false;
    }

    _resetMeasurement(state, now, totalSteps, reason) {
        state.phase = 'warmup';
        state.phase_started_ms = now;
        state.warmup_policy_cycle = null;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.measurement_start_policy_cycle = null;
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
        state.measurement_start_produced_steps = null;
        state.source_steps = null;
        state.last_decision = 'probing ' + state.baseline_envs + '->' + target;
        return true;
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
            state.desired_envs = state.baseline_envs;
            state.phase = 'stable';
            state.retest_after_ms = Math.max(state.retest_after_ms, now + this.cooldownMs);
            state.last_decision = 'waiting for another worker probe';
            return;
        }
        if (
            !this.activeProbeTrainerId &&
            this.lastCompletedProbeTrainerId === state.trainer_id &&
            this._hasReadyAlternateProbe(state.trainer_id, now)
        ) {
            state.desired_envs = state.baseline_envs;
            state.phase = 'stable';
            state.retest_after_ms = Math.max(state.retest_after_ms, now + this.cooldownMs);
            state.last_decision = 'yielding probe slot to another ready worker';
            return;
        }
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

    _abortProbe(state, capacity, now, reason) {
        if (state.baseline_envs === null || capacity.current_envs === state.baseline_envs) {
            this._releaseProbe(state.trainer_id);
            this._resetMeasurement(state, now, null, reason);
            return;
        }
        const direction = capacity.current_envs > state.baseline_envs ? 1 : -1;
        this._blockDirection(state, direction);
        state.direction = -direction;
        state.desired_envs = state.baseline_envs;
        state.phase = 'awaiting-restart';
        state.phase_started_ms = now;
        state.cooldown_until_ms = now + this.cooldownMs;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
        state.source_steps = null;
        state.metrics_missing_since_ms = null;
        state.last_decision = reason + '; backing off to ' + state.baseline_envs + ' envs';
        // Keep the cluster-wide probe lock until the worker reports the accepted baseline again.
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
        state.phase = 'awaiting-restart';
        state.phase_started_ms = now;
        state.cooldown_until_ms = now + this.cooldownMs;
        state.measurement_started_ms = null;
        state.measurement_start_steps = null;
        state.measurement_start_produced_steps = null;
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
            state.step = state.cycle_aware
                ? cycleAwareStep(capacity.current_envs)
                : initialStep(capacity.current_envs);
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
            state.step = state.cycle_aware
                ? cycleAwareStep(capacity.current_envs)
                : initialStep(capacity.current_envs);
            state.blocked_up = false;
            state.blocked_down = false;
            state.last_decision =
                'accepted ' + capacity.current_envs + ' envs at ' + sps.toFixed(1) + ' steps/s';
            this._completeProbe(state.trainer_id);
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
        state.measurement_start_produced_steps = null;
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
        const totalSteps = learnerConsumedSteps(record && record.metrics);
        const producedSteps = producerAcceptedSteps(record && record.metrics);
        const currentPolicyCycle = policyCycle(record && record.metrics);
        const currentRuntimeVersion = runtimeVersion(record && record.metrics);
        const sessionFailureAgeSeconds = recentSessionFailureAgeSeconds(
            record && record.metrics);
        const sessionFailuresTotal = sessionFailureCount(record && record.metrics);
        const lastSessionFailureType = sessionFailureType(record && record.metrics);
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
        state.last_update_ms = timestamp;
        if (currentPolicyCycle !== null) {
            state.cycle_aware = true;
        }
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

        if (
            resizeFailure &&
            capacity.current_envs !== state.desired_envs &&
            resizeFailure.target_envs === state.desired_envs
        ) {
            const direction = state.desired_envs > capacity.current_envs ? 1 : -1;
            this._blockDirection(state, direction);
            this._releaseProbe(state.trainer_id);
            if (state.baseline_envs === null) {
                state.baseline_envs = capacity.current_envs;
            }
            state.desired_envs = state.baseline_envs;
            state.phase = 'cooldown';
            state.phase_started_ms = timestamp;
            state.cooldown_until_ms = timestamp + this.cooldownMs;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.source_steps = totalSteps;
            state.metrics_missing_since_ms = null;
            state.last_decision =
                'live resize probe failed; retained ' + capacity.current_envs +
                ' envs: ' + resizeFailure.error;
            return this.snapshot(record.trainer_id);
        }

        if (capacity.current_envs !== state.desired_envs) {
            state.metrics_missing_since_ms = null;
            state.phase = 'awaiting-restart';
            state.phase_started_ms = timestamp;
            state.measurement_started_ms = null;
            state.measurement_start_steps = null;
            state.measurement_start_produced_steps = null;
            state.source_steps = totalSteps;
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
            state.last_instability_reason === 'WAN actor session failure'
        ) {
            state.instability_hold_until_ms = Math.min(
                state.instability_hold_until_ms,
                timestamp,
            );
        }

        // Reaching the requested env count does not mean the restarted worker is ready yet.
        // Keep a planned env-count transition in awaiting-restart while child health still
        // reports "starting"; begin warmup only after the process reports "running".
        if (
            state.phase === 'awaiting-restart' &&
            processState === 'starting' &&
            !optimizerError
        ) {
            state.last_decision = 'waiting for planned worker restart to become running';
            return this.snapshot(record.trainer_id);
        }
        const recentSessionFailure =
            sessionFailureAgeSeconds !== null &&
            sessionFailureAgeSeconds * 1000 < this.instabilityHoldMs;
        const expectedStarting =
            processState === 'starting' &&
            !optimizerError &&
            (
                state.phase === 'awaiting-restart' ||
                state.baseline_envs === null
            );
        const expectedTransition = expectedStarting || reconciliationTransition;
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
            const instabilityReason = optimizerError
                ? 'worker-reported error: ' + optimizerError
                : recentSessionFailure
                    ? 'WAN actor session failure'
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
                        'probe produced no learner-consumed-step metrics',
                    );
                    return this.snapshot(record.trainer_id);
                }
            }
            this._resetMeasurement(state, timestamp, null, 'waiting for learner-consumed-step metrics');
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
            state.blocked_up = false;
            state.blocked_down = false;
            state.step = state.cycle_aware
                ? cycleAwareStep(state.baseline_envs || capacity.current_envs)
                : initialStep(state.baseline_envs || capacity.current_envs);
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
                this._completeProbe(state.trainer_id);
            }
            this._resetMeasurement(state, timestamp, totalSteps, 'warming after env change');
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'warmup') {
            if (currentPolicyCycle !== null) {
                if (state.warmup_policy_cycle === null) {
                    state.warmup_policy_cycle = currentPolicyCycle;
                    state.last_decision =
                        'waiting for a complete learner policy cycle before measuring';
                    return this.snapshot(record.trainer_id);
                }
                if (currentPolicyCycle < state.warmup_policy_cycle) {
                    state.warmup_policy_cycle = currentPolicyCycle;
                    state.phase_started_ms = timestamp;
                    state.last_decision =
                        'learner policy cycle restarted; waiting for a fresh complete cycle';
                    return this.snapshot(record.trainer_id);
                }
                if (timestamp - state.phase_started_ms < this.warmupMs) {
                    if (currentPolicyCycle > state.warmup_policy_cycle) {
                        state.warmup_policy_cycle = currentPolicyCycle;
                    }
                    return this.snapshot(record.trainer_id);
                }
                if (currentPolicyCycle === state.warmup_policy_cycle) {
                    return this.snapshot(record.trainer_id);
                }
                state.phase = 'measuring';
                state.measurement_started_ms = timestamp;
                state.measurement_start_steps = totalSteps;
                state.measurement_start_produced_steps = producedSteps;
                state.measurement_start_policy_cycle = currentPolicyCycle;
                state.last_decision =
                    'measuring learner-consumed throughput across a complete policy cycle';
                return this.snapshot(record.trainer_id);
            }

            // Compatibility path for an older actor during a rolling runtime cutover.
            if (timestamp - state.phase_started_ms < this.warmupMs) {
                return this.snapshot(record.trainer_id);
            }
            state.phase = 'measuring';
            state.measurement_started_ms = timestamp;
            state.measurement_start_steps = totalSteps;
            state.measurement_start_produced_steps = producedSteps;
            state.measurement_start_policy_cycle = null;
            state.last_decision = 'measuring learner-consumed steps';
            return this.snapshot(record.trainer_id);
        }

        if (state.phase === 'measuring') {
            const elapsed = timestamp - state.measurement_started_ms;

            if (state.measurement_start_policy_cycle !== null) {
                if (currentPolicyCycle === null) {
                    state.last_decision =
                        'waiting for policy-epoch telemetry to complete measurement';
                    return this.snapshot(record.trainer_id);
                }
                if (currentPolicyCycle < state.measurement_start_policy_cycle) {
                    this._resetMeasurement(
                        state,
                        timestamp,
                        totalSteps,
                        'learner policy cycle restarted',
                    );
                    return this.snapshot(record.trainer_id);
                }
                if (currentPolicyCycle === state.measurement_start_policy_cycle) {
                    return this.snapshot(record.trainer_id);
                }
            } else if (elapsed < this.measurementMs) {
                return this.snapshot(record.trainer_id);
            }

            const delta = totalSteps - state.measurement_start_steps;
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
            if (delta === 0 && producedDelta !== null && producedDelta > 0) {
                this._resetMeasurement(
                    state,
                    timestamp,
                    totalSteps,
                    'worker produced rollouts but none were learner-consumed; retrying fair measurement',
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
    producerAcceptedSteps,
    runtimeVersion,
    policyCycle,
    recentSessionFailureAgeSeconds,
    sessionFailureCount,
    sessionFailureType,
    envResizeFailure,
    initialStep,
    cycleAwareStep,
};
