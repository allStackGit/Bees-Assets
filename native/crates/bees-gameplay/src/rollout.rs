//! One accelerator inference pass over multiple native gameplay worlds.
//!
//! The caller supplies the complete live v26 observation snapshots and a
//! real model backend. This orchestration never manufactures actions,
//! rewrites physics or takes ownership of a second game simulation.
//! World-local rewards, command completions and squad changes remain in
//! each Level's persistent tick-stamped notification outbox.

use bees_hivemind::session::{HiveMindSession, SquadLifetime};
use bees_sim::policy::Decision;
use bees_sim::simulation::{Simulation, StepError, World};
use std::time::Instant;

use crate::inference::{
    NativeInferenceError, NativeInferenceRunError, NativeLevelInferenceBatch,
    NativePolicyInference,
};
use crate::level_events::NativeLevelEventError;
use crate::level_rules::{
    NativeLevelCommandContext, NativeLevelRewardMetadata, NativeLevelRules,
    NativeLevelState, NativeLevelStepError,
};
use crate::squads::SquadRosterError;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeBatchedStepCounts {
    pub worlds_stepped: usize,
    pub worlds_complete_or_failed: usize,
    pub policy_agents_inferred: usize,
    /// Live worlds advanced without invoking a pointless observation
    /// builder because no policy controller is due on this physics tick.
    pub worlds_without_due_agents: usize,
    /// False when Simulation timing sampling is disabled or this call
    /// was not selected. The three durations are then exactly zero.
    pub phase_timing_sampled: bool,
    /// Sampled host wall time across each world's observation
    /// assembly, roster checks and snapshot validation.
    pub observation_build_nanos: u128,
    /// Sampled full inference boundary including model and action checks.
    /// For asynchronous accelerators this measures synchronization only
    /// when the backend waits for completion.
    pub inference_and_validation_nanos: u128,
    /// Sampled host wall time to dispatch actions and advance worlds.
    /// Shared Simulation already measures sampled individual world steps.
    pub world_dispatch_and_step_nanos: u128,
}

#[derive(Debug)]
pub enum NativeBatchedStepError<SourceError, BackendError> {
    MissingWorld { index: usize },
    Source { index: usize, error: SourceError },
    IncompleteObservations {
        index: usize, expected: usize, actual: usize,
    },
    WrongObservationWorld { index: usize },
    UnexpectedDecisionAgent { index: usize, agent: u64 },
    Inference(NativeInferenceRunError<BackendError>),
    Dispatch { index: usize, error: NativeInferenceError },
    /// Earlier indexed worlds may already have stepped. The caller
    /// must not replay their decisions or mix the partial window with
    /// successful complete PPO training batches.
    Step {
        index: usize,
        worlds_stepped: usize,
        error: StepError<NativeLevelStepError>,
    },
}

/// Synchronous GPU/CPU model batching across all current worlds.
///
/// `build_observations` is a source-authored Level adapter. It must
/// enumerate the participating policy controllers and call
/// `batch.push_agent(world, id, &snapshot, frame)` with genuine live
/// observation data. Missing observations and unsupported actions
/// remain host errors; no all-zero fallback is supplied.
///
/// Model outputs are validated for the WHOLE batch before any world
/// mutates. Each world then advances exactly once via NativeLevelRules.
/// Level notifications are kept in its persistent outbox, so repeated
/// calls cannot silently overwrite earlier rewards even if the host
/// flushes them only at the end of a rollout window.
pub fn step_batched_levels<M, A, B, F, SourceError>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    batch: &mut NativeLevelInferenceBatch,
    backend: &mut B,
    build_observations: &mut F,
    decisions: &mut Vec<Decision>,
) -> Result<NativeBatchedStepCounts,
    NativeBatchedStepError<SourceError, B::Error>>
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    B: NativePolicyInference,
    F: FnMut(
        &World<NativeLevelState<M, A>>, &mut NativeLevelInferenceBatch,
    ) -> Result<(), SourceError>,
{
    let sample_phases =
        batch.sample_rollout_phase_timing(simulation.timing_sample_period());
    batch.clear();
    let mut counts = NativeBatchedStepCounts {
        phase_timing_sampled: sample_phases,
        ..NativeBatchedStepCounts::default()
    };
    let observations_started = sample_phases.then(Instant::now);
    for index in 0..simulation.world_count() {
        {
            let world = simulation.world_mut(index)
                .ok_or(NativeBatchedStepError::MissingWorld { index })?;
            if !world.is_failed() && !world.state.events.is_failed() &&
                !world.state.events.completed() {
                let tick = world.tick();
                let state = &mut world.state;
                state.decisions.synchronize(&state.combat, tick);
            }
        }
        let world = simulation.world(index)
            .ok_or(NativeBatchedStepError::MissingWorld { index })?;
        if world.is_failed() || world.state.events.completed() {
            counts.worlds_complete_or_failed += 1;
            continue;
        }
        if world.state.events.is_failed() {
            return Err(NativeBatchedStepError::Dispatch {
                index, error: NativeInferenceError::WorldUnavailable,
            });
        }
        let next_tick = world.tick().checked_add(1)
            .ok_or(NativeBatchedStepError::Dispatch {
                index, error: NativeInferenceError::WorldUnavailable,
            })?;
        let expected = world.state.decisions.due_count(next_tick);
        let start = batch.keys().len();
        if expected == 0 {
            // The Level still advances physics, rewards and command timers
            // below; only policy perception has no consumer on this tick.
            counts.worlds_without_due_agents += 1;
        } else {
            build_observations(world, batch).map_err(|error|
                NativeBatchedStepError::Source { index, error }
            )?;
        }
        let added = &batch.keys()[start..];
        if added.len() != expected {
            return Err(NativeBatchedStepError::IncompleteObservations {
                index, expected, actual: added.len(),
            });
        }
        if (start..batch.keys().len()).any(|row|
            !batch.is_row_current_for(world, row)
        ) {
            return Err(NativeBatchedStepError::WrongObservationWorld { index });
        }
        for key in added {
            let Ok(agent_id) = i64::try_from(key.agent) else {
                return Err(NativeBatchedStepError::UnexpectedDecisionAgent {
                    index, agent: key.agent,
                });
            };
            if !world.state.decisions.is_due(agent_id, next_tick) {
                return Err(NativeBatchedStepError::UnexpectedDecisionAgent {
                    index, agent: key.agent,
                });
            }
        }
        // Every pushed agent is checked against the real world's
        // action mask by push_agent(), which rejects unregistered
        // agents and duplicate keys. Equal counts therefore establish
        // a complete one-to-one roster for this world.
    }
    counts.policy_agents_inferred = batch.keys().len();
    if let Some(started) = observations_started {
        counts.observation_build_nanos = started.elapsed().as_nanos();
    }
    let inference_started = sample_phases.then(Instant::now);
    if batch.keys().is_empty() {
        batch.finalize().map_err(|err|
            NativeBatchedStepError::Inference(
                NativeInferenceRunError::Boundary(err),
            )
        )?;
    } else {
        batch.infer_with_timing(backend, sample_phases)
            .map_err(NativeBatchedStepError::Inference)?;
    }
    if let Some(started) = inference_started {
        counts.inference_and_validation_nanos = started.elapsed().as_nanos();
    }
    let dispatch_started = sample_phases.then(Instant::now);
    for index in 0..simulation.world_count() {
        let world = simulation.world(index)
            .ok_or(NativeBatchedStepError::MissingWorld { index })?;
        if world.is_failed() || world.state.events.completed() {
            continue;
        }
        batch.decisions_for_world(world, decisions)
            .map_err(|error|
                NativeBatchedStepError::Dispatch { index, error }
            )?;
        simulation.step_world(index, decisions).map_err(|error|
            NativeBatchedStepError::Step {
                index, worlds_stepped: counts.worlds_stepped, error,
            }
        )?;
        counts.worlds_stepped += 1;
    }
    if let Some(started) = dispatch_started {
        counts.world_dispatch_and_step_nanos = started.elapsed().as_nanos();
    }
    Ok(counts)
}

#[derive(Debug)]
pub enum NativeFailedLevelAbortError {
    MissingWorld { index: usize },
    NotFailed { index: usize },
    AlreadyComplete { index: usize },
    Events(NativeLevelEventError),
}

/// Explicitly convert an irrecoverable native episode's remaining HiveMind
/// command selections into discard obligations. No gameplay callbacks or
/// reward credit may run after a failed step. The caller must persist and
/// acknowledge the emitted notifications and flush surviving lifetimes to
/// its shared HiveMindSession before calling reset_level_after_delivery.
pub fn abort_failed_level<M, A>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    index: usize,
) -> Result<usize, NativeFailedLevelAbortError>
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
{
    let world = simulation.world_mut(index)
        .ok_or(NativeFailedLevelAbortError::MissingWorld { index })?;
    if !world.is_failed() && !world.state.events.is_failed() {
        return Err(NativeFailedLevelAbortError::NotFailed { index });
    }
    if world.state.events.terminal_was_published() {
        return Err(NativeFailedLevelAbortError::AlreadyComplete { index });
    }
    world.state.events.drain_failed_level_reservations()
        .map_err(NativeFailedLevelAbortError::Events)
}

#[derive(Debug)]
pub enum NativeLevelCancelError {
    MissingWorld { index: usize },
    AlreadyComplete { index: usize },
    Events(NativeLevelEventError),
}

/// Stop a live or already-failed native Level without manufacturing an
/// episode result. Every living HiveMind squad lifetime is queued for
/// shared-session retirement, including squads with no active command.
/// Aborted command reservations become durable discard obligations.
pub fn cancel_level<M, A>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    index: usize,
) -> Result<usize, NativeLevelCancelError>
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
{
    let world = simulation.world_mut(index)
        .ok_or(NativeLevelCancelError::MissingWorld { index })?;
    if world.state.events.terminal_was_published() {
        return Err(NativeLevelCancelError::AlreadyComplete { index });
    }
    world.state.events.cancel_live_level()
        .map_err(NativeLevelCancelError::Events)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelResetError {
    MissingWorld { index: usize },
    PendingNotifications { count: usize },
    PendingSessionRetirements { count: usize },
    /// A failed episode must still explicitly settle any live commands;
    /// replacing the world would silently lose their OutcomeIds.
    ActiveCommands { count: usize },
    /// A started episode needs a successfully published terminal result
    /// or a deliberate abort. This holds even when every squad died.
    UnresolvedEpisodeLifecycle,
}

/// A successful or failed episode must not be discarded while group
/// rewards, command completions or shared-session retirement instructions
/// remain unacknowledged. Callers explicitly deliver/acknowledge the
/// pending notifications and apply HiveMindSession retirements first.
/// World::reset_episode then increments the revision, invalidating late
/// inference results for this reused WorldId.
pub fn reset_level_after_delivery<M, A>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    index: usize,
    seed: u64,
    replacement: NativeLevelState<M, A>,
) -> Result<(), NativeLevelResetError>
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
{
    let world = simulation.world(index)
        .ok_or(NativeLevelResetError::MissingWorld { index })?;
    let remaining = world.state.events.pending_notifications().len();
    if remaining > 0 {
        return Err(NativeLevelResetError::PendingNotifications {
            count: remaining,
        });
    }
    let retirements =
        world.state.events.pending_session_retirements().len();
    if retirements > 0 {
        return Err(NativeLevelResetError::PendingSessionRetirements {
            count: retirements,
        });
    }
    let active = world.state.events.active_command_count();
    if active > 0 {
        return Err(NativeLevelResetError::ActiveCommands {
            count: active,
        });
    }
    if world.state.events.has_unresolved_episode_lifecycle() {
        return Err(NativeLevelResetError::UnresolvedEpisodeLifecycle);
    }
    // Indices are validated above, and this call preserves the
    // Simulation's existing episode-reset and measurement semantics.
    simulation.reset_world(index, seed, replacement)
        .map_err(|_| NativeLevelResetError::MissingWorld { index })
}

/// The network connection owns exactly one HiveMindSession shared by
/// many Levels. Apply all pending retirement instructions between
/// rollout windows, before handling delayed strategy responses.
/// No RPCs or OutcomeId settlements are performed here.
pub fn flush_all_squad_retirements<M, A>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    connection: &mut HiveMindSession,
) -> usize
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
{
    let mut total = 0;
    for index in 0..simulation.world_count() {
        if let Some(world) = simulation.world_mut(index) {
            total += world.state.events
                .flush_retirements_to_connection(connection);
        }
    }
    total
}
