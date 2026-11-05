//! One v26 observation/action-batch bridge for native Levels.
//!
//! Source values are supplied by PolicyPerceptionSnapshot (the same
//! authoritative Unity-ported writer). This bridge binds action masks to
//! the *live* CombatWorld and inference keys to WorldId, episode revision
//! and completed tick, so a GPU batch cannot silently apply stale actions.
//! The model backend is external; no guessed action or network here.

use bees_sim::combat_world::CombatWorldError;
use bees_sim::control::PolicyFrame;
use bees_sim::full_perception::{
    NativePerceptionWriter, PerceptionWork, PolicyPerceptionSnapshot,
};
use bees_sim::policy::Decision;
use bees_sim::policy_batch::{
    PolicyAgentKey, PolicyBatch, PolicyBatchError, PolicyBatchFootprint,
};
use bees_sim::simulation::World;
use bees_sim::telemetry::Measurements;

use crate::level_rules::NativeLevelState;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeInferenceError {
    InvalidAgent,
    SnapshotIdentityMismatch,
    WorldUnavailable,
    AlreadyFinalized,
    NotFinalized,
    Combat(CombatWorldError),
    Batch(PolicyBatchError),
    StaleAction(PolicyAgentKey),
}

impl From<CombatWorldError> for NativeInferenceError {
    fn from(value: CombatWorldError) -> Self { Self::Combat(value) }
}
impl From<PolicyBatchError> for NativeInferenceError {
    fn from(value: PolicyBatchError) -> Self { Self::Batch(value) }
}

/// Reuse numeric observation and action buffers across inference requests.
/// A single instance may contain agents from multiple independent native
/// Levels, enabling CPU/GPU batching without a process-global world state.
#[derive(Default)]
pub struct NativeLevelInferenceBatch {
    batch: PolicyBatch,
    writer: NativePerceptionWriter,
    keyed_decisions: Vec<(PolicyAgentKey, Decision)>,
    finalized: bool,
}

impl NativeLevelInferenceBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) {
        self.batch.clear();
        self.keyed_decisions.clear();
        self.finalized = false;
    }

    pub fn reserve_agents(&mut self, count: usize)
        -> Result<(), NativeInferenceError>
    {
        self.batch.reserve_agents(count)?;
        self.keyed_decisions.reserve(count.saturating_sub(
            self.keyed_decisions.capacity(),
        ));
        Ok(())
    }

    pub fn perceptions(&self) -> PerceptionWork {
        self.writer.measurements()
    }

    pub fn footprint(&self) -> PolicyBatchFootprint {
        self.batch.footprint()
    }

    pub fn observations(&self) -> &[f32] {
        self.batch.observations()
    }

    pub fn keys(&self) -> &[PolicyAgentKey] {
        self.batch.keys()
    }

    /// An inference library fills BOTH buffers: 16 continuous channels
    /// and 6 discrete branches per agent. The underlying PolicyBatch
    /// performs complete finite/range/mask validation at finalization.
    pub fn output_buffers_mut(&mut self) -> (&mut [f32], &mut [u8]) {
        self.batch.output_buffers_mut()
    }

    /// Authoritative input only: the snapshot's exact living ship/side
    /// must match the live native Level, and action masks always come
    /// from the real turret/special state, never an optimistic model.
    pub fn push_agent<M, A>(
        &mut self,
        world: &World<NativeLevelState<M, A>>,
        ship_id: i64,
        snapshot: &PolicyPerceptionSnapshot<'_>,
        frame: PolicyFrame,
    ) -> Result<(), NativeInferenceError> {
        if self.finalized { return Err(NativeInferenceError::AlreadyFinalized); }
        if world.is_failed() || world.state.events.is_failed() ||
            world.state.events.completed() ||
            world.state.combat.frozen_after_elimination {
            return Err(NativeInferenceError::WorldUnavailable);
        }
        let agent = u64::try_from(ship_id)
            .map_err(|_| NativeInferenceError::InvalidAgent)?;
        if agent == 0 { return Err(NativeInferenceError::InvalidAgent); }
        let ship = world.state.combat.ship(ship_id)
            .ok_or(NativeInferenceError::InvalidAgent)?;
        if snapshot.self_entity_id != ship_id ||
            snapshot.side != ship.side {
            return Err(NativeInferenceError::SnapshotIdentityMismatch);
        }
        let mask = world.state.combat.policy_action_mask(ship_id)?;
        let key = PolicyAgentKey::from_world(world, agent);
        self.writer.push_to_batch(&mut self.batch, key, mask, snapshot, frame)?;
        Ok(())
    }

    /// Finish exactly once after model inference. Invalid/missing outputs
    /// fail the entire batch, rather than silently substituting zeros.
    pub fn finalize(&mut self)
        -> Result<&[(PolicyAgentKey, Decision)], NativeInferenceError>
    {
        if self.finalized { return Err(NativeInferenceError::AlreadyFinalized); }
        self.batch.collect_decisions(&mut self.keyed_decisions)?;
        self.finalized = true;
        Ok(&self.keyed_decisions)
    }

    /// Extract only the keys for this specific world. Check ALL matching
    /// keys against its current episode/tick before mutating `output`;
    /// mixed-world batches remain supported without stale-action leaks.
    /// Caller then feeds decisions to the one NativeLevelRules.step.
    pub fn decisions_for_world<M, A>(
        &self,
        world: &World<NativeLevelState<M, A>>,
        output: &mut Vec<Decision>,
    ) -> Result<(), NativeInferenceError> {
        if !self.finalized { return Err(NativeInferenceError::NotFinalized); }
        if world.is_failed() || world.state.events.is_failed() ||
            world.state.events.completed() ||
            world.state.combat.frozen_after_elimination {
            return Err(NativeInferenceError::WorldUnavailable);
        }
        for (key, _) in &self.keyed_decisions {
            if key.world == world.id && !key.is_current_for(world) {
                return Err(NativeInferenceError::StaleAction(*key));
            }
        }
        output.clear();
        for (key, decision) in &self.keyed_decisions {
            if key.world == world.id {
                output.push(*decision);
            }
        }
        Ok(())
    }
}
