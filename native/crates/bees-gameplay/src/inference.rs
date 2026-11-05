//! One v26 observation/action-batch bridge for native Levels.
//!
//! Source values are supplied by PolicyPerceptionSnapshot (the same
//! authoritative Unity-ported writer). This bridge binds action masks to
//! the *live* CombatWorld and inference keys to WorldId, episode revision
//! and completed tick, so a GPU batch cannot silently apply stale actions.
//! The model backend is external; no guessed action or network here.

use bees_sim::combat_world::CombatWorldError;
use bees_sim::combat_perception_bridge::live_ship_counts;
use bees_sim::full_perception::{
    NativePerceptionWriter, PerceptionWork, PolicyPerceptionSnapshot,
};
use bees_sim::policy::Decision;
use bees_sim::policy_batch::{
    PolicyActionMask, PolicyAgentKey, PolicyBatch, PolicyBatchError,
    PolicyBatchFootprint,
};
use bees_sim::simulation::World;

use crate::level_rules::NativeLevelState;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeInferenceError {
    InvalidAgent,
    SnapshotIdentityMismatch,
    SnapshotStateMismatch,
    WorldUnavailable,
    AlreadyFinalized,
    NotFinalized,
    FailedBatch,
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

/// Backend-independent execution of one exact v26 policy batch. A
/// CPU/ROCm/ONNX implementation may batch multiple native worlds into
/// one call without accessing or mutating the game simulation. Outputs
/// are row-major [agent][16 continuous] and [agent][6 discrete].
/// The number of rows equals action_masks.len(); the backend must apply
/// these masks BEFORE discrete sampling. Final validation independently
/// rejects any masked action that slips through.
pub trait NativePolicyInference {
    type Error;

    fn forward(
        &mut self,
        observations: &[f32],
        action_masks: &[PolicyActionMask],
        continuous: &mut [f32],
        discrete: &mut [u8],
    ) -> Result<(), Self::Error>;
}

#[derive(Debug)]
pub enum NativeInferenceRunError<E> {
    Boundary(NativeInferenceError),
    Backend(E),
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
    failed: bool,
}

impl NativeLevelInferenceBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) {
        self.batch.clear();
        self.keyed_decisions.clear();
        self.finalized = false;
        self.failed = false;
    }

    pub fn reserve_agents(&mut self, count: usize)
        -> Result<(), NativeInferenceError>
    {
        self.batch.reserve_agents(count)?;
        self.keyed_decisions.try_reserve(
            count.saturating_sub(self.keyed_decisions.len()),
        ).map_err(|_| NativeInferenceError::Batch(
            PolicyBatchError::AllocationFailed,
        ))?;
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
    pub fn output_buffers_mut(
        &mut self,
    ) -> Result<(&mut [f32], &mut [u8]), NativeInferenceError> {
        if self.finalized {
            return Err(NativeInferenceError::AlreadyFinalized);
        }
        if self.failed { return Err(NativeInferenceError::FailedBatch); }
        Ok(self.batch.output_buffers_mut())
    }

    /// Authoritative input only: the snapshot's exact living ship/side
    /// must match the live native Level, and action masks always come
    /// from the real turret/special state, never an optimistic model.
    pub fn push_agent<M, A>(
        &mut self,
        world: &World<NativeLevelState<M, A>>,
        ship_id: i64,
        snapshot: &PolicyPerceptionSnapshot<'_>,
    ) -> Result<(), NativeInferenceError> {
        if self.finalized { return Err(NativeInferenceError::AlreadyFinalized); }
        if self.failed { return Err(NativeInferenceError::FailedBatch); }
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
        // The snapshot's identity/order-dependent observations remain the
        // authored Level's responsibility. But physical self-features,
        // live side counts and bound special readiness MUST be projected
        // from THIS world at THIS decision boundary, not cached from a
        // previous tick or an externally fabricated training roster.
        let combat = &world.state.combat;
        let body = combat.ship_body(ship_id)
            .ok_or(NativeInferenceError::SnapshotStateMismatch)?;
        let counts = live_ship_counts(combat, ship.side)
            .map_err(|_| NativeInferenceError::SnapshotStateMismatch)?;
        let special = combat.policy_special_observations(ship_id)
            .ok_or(NativeInferenceError::SnapshotStateMismatch)?;
        let obs = snapshot.ship;
        if !body.active || ship.vitals.is_dead() ||
            obs.ship_type != ship.ship_type ||
            obs.position != body.motion.position ||
            obs.rotation_degrees != body.motion.rotation_degrees ||
            obs.health != ship.vitals.health() ||
            obs.max_health != ship.vitals.max_health() ||
            obs.speed != body.motion.base_speed ||
            obs.current_speed != body.motion.current_speed ||
            obs.rotation_speed != body.motion.rotation_speed_degrees_per_second ||
            obs.linear_velocity != body.motion.body_velocity ||
            obs.live_allies_including_self != counts.same_side ||
            obs.live_enemies != counts.opponents ||
            obs.has_special != special.has_special ||
            obs.special_ready != special.ready ||
            snapshot.capability.special_resource_fraction != special.resource_fraction ||
            snapshot.capability.special_cooldown_fraction != special.cooldown_fraction ||
            snapshot.capability.special_phase != special.phase {
            return Err(NativeInferenceError::SnapshotStateMismatch);
        }
        let mask = combat.policy_action_mask(ship_id)?;
        let frame = combat.policy_frame(ship_id)
            .ok_or(NativeInferenceError::InvalidAgent)?;
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
        if self.failed { return Err(NativeInferenceError::FailedBatch); }
        if let Err(error) = self.batch.collect_decisions(
            &mut self.keyed_decisions,
        ) {
            self.failed = true;
            return Err(NativeInferenceError::Batch(error));
        }
        self.finalized = true;
        Ok(&self.keyed_decisions)
    }

    /// Execute one model inference against the contiguous v26 buffers.
    /// A backend failure invalidates the whole batch, including any
    /// partially written actions; only clear() permits another attempt.
    /// The same final validation as the manual-output path applies.
    pub fn infer<B: NativePolicyInference>(
        &mut self,
        backend: &mut B,
    ) -> Result<&[(PolicyAgentKey, Decision)], NativeInferenceRunError<B::Error>> {
        if self.finalized {
            return Err(NativeInferenceRunError::Boundary(
                NativeInferenceError::AlreadyFinalized,
            ));
        }
        if self.failed {
            return Err(NativeInferenceRunError::Boundary(
                NativeInferenceError::FailedBatch,
            ));
        }
        let (observations, action_masks, continuous, discrete) =
            self.batch.inference_buffers_mut();
        if let Err(error) = backend.forward(
            observations, action_masks, continuous, discrete,
        ) {
            self.failed = true;
            return Err(NativeInferenceRunError::Backend(error));
        }
        self.finalize().map_err(NativeInferenceRunError::Boundary)
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
        if self.failed { return Err(NativeInferenceError::FailedBatch); }
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
