//! One accelerator inference pass over multiple native gameplay worlds.
//!
//! The caller supplies the complete live v26 observation snapshots and a
//! real model backend. This orchestration never manufactures actions,
//! rewrites physics or takes ownership of a second game simulation.
//! World-local rewards, command completions and squad changes remain in
//! each Level's persistent tick-stamped notification outbox.

use bees_hivemind::session::SquadLifetime;
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
        build_observations(world, batch).map_err(|error|
            NativeBatchedStepError::Source { index, error }
        )?;
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
