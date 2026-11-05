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

use crate::inference::{
    NativeInferenceError, NativeInferenceRunError, NativeLevelInferenceBatch,
    NativePolicyInference,
};
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
}

#[derive(Debug)]
pub enum NativeBatchedStepError<SourceError, BackendError> {
    MissingWorld { index: usize },
    Source { index: usize, error: SourceError },
    IncompleteObservations {
        index: usize, expected: usize, actual: usize,
    },
    WrongObservationWorld { index: usize },
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
    batch.clear();
    let mut counts = NativeBatchedStepCounts::default();
    for index in 0..simulation.world_count() {
        {
            let world = simulation.world_mut(index)
                .ok_or(NativeBatchedStepError::MissingWorld { index })?;
            if !world.is_failed() && !world.state.events.is_failed() &&
                !world.state.events.completed() {
                let tick = world.tick();
                let state = &mut world.state;
                state.decisions.synchronize(&state.combat, tick)
                    .map_err(|_| NativeBatchedStepError::Dispatch {
                        index, error: NativeInferenceError::WorldUnavailable,
                    })?;
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
        let start = batch.keys().len();
        build_observations(world, batch).map_err(|error|
            NativeBatchedStepError::Source { index, error }
        )?;
        let added = &batch.keys()[start..];
        let next_tick = world.tick().checked_add(1)
            .ok_or(NativeBatchedStepError::Dispatch {
                index, error: NativeInferenceError::WorldUnavailable,
            })?;
        let expected = world.state.decisions.due_count(next_tick);
        if added.len() != expected {
            return Err(NativeBatchedStepError::IncompleteObservations {
                index, expected, actual: added.len(),
            });
        }
        if added.iter().any(|key| !key.is_current_for(world)) {
            return Err(NativeBatchedStepError::WrongObservationWorld { index });
        }
        // Every pushed agent is checked against the real world's
        // action mask by push_agent(), which rejects unregistered
        // agents and duplicate keys. Equal counts therefore establish
        // a complete one-to-one roster for this world.
    }
    counts.policy_agents_inferred = batch.keys().len();
    if batch.keys().is_empty() {
        batch.finalize().map_err(|err|
            NativeBatchedStepError::Inference(
                NativeInferenceRunError::Boundary(err),
            )
        )?;
    } else {
        batch.infer(backend).map_err(NativeBatchedStepError::Inference)?;
    }
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
    Ok(counts)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelResetError {
    MissingWorld { index: usize },
    PendingNotifications { count: usize },
    PendingSessionRetirements { count: usize },
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
