//! One v26 observation/action-batch bridge for native Levels.
//!
//! Source values are supplied by PolicyPerceptionSnapshot (the same
//! authoritative Unity-ported writer). This bridge binds action masks to
//! the *live* CombatWorld and inference keys to WorldId, episode revision
//! and completed tick, so a GPU batch cannot silently apply stale actions.
//! The model backend is external; no guessed action or network here.

use std::collections::HashMap;
use std::num::NonZeroU32;
use std::time::Instant;

use bees_sim::combat_world::CombatWorldError;
use bees_sim::combat_perception_bridge::live_ship_counts;
use bees_sim::full_perception::{
    NativePerceptionWriter, PerceptionWork, PolicyPerceptionSnapshot,
};
use bees_sim::policy::Decision;
use bees_sim::policy_batch::{
    PocaActivityBatchError, PolicyActionMask, PolicyAgentKey, PolicyBatch,
    PolicyBatchError, PolicyBatchFootprint,
};
use bees_sim::ppo_activity::PolicyDimensionActivity;
use bees_sim::simulation::{World, WorldId};

use crate::level_rules::NativeLevelState;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeInferenceError {
    InvalidAgent,
    RepeatedGroupAgent { world: WorldId, agent: u64 },
    SnapshotIdentityMismatch,
    SnapshotStateMismatch,
    UnmatchedTeamRoute,
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

/// The native host resolves this row's actual learner/opponent model
/// from the episode's team and checkpoint schedule. The numeric model
/// slot is opaque to the simulator, but must reference a real loaded
/// policy in the backend. Different rows can route to different models.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativePolicyRoute {
    pub team_id: u8,
    pub model_slot: u32,
}

/// Backend-independent execution of one exact v26 policy batch. A
/// CPU/ROCm/ONNX implementation may batch multiple native worlds into
/// one call without accessing or mutating the game simulation. Outputs
/// are row-major [agent][16 continuous] and [agent][6 discrete].
/// All keys, policy routes, and masks contain exactly one entry per
/// observation row; the backend uses the route to dispatch learner versus
/// opponent weights. It must apply masks BEFORE discrete sampling.
/// Final validation independently
/// rejects any masked action that slips through.
pub trait NativePolicyInference {
    type Error;

    fn forward(
        &mut self,
        keys: &[PolicyAgentKey],
        routes: &[NativePolicyRoute],
        observations: &[f32],
        action_masks: &[PolicyActionMask],
        continuous: &mut [f32],
        discrete: &mut [u8],
    ) -> Result<(), Self::Error>;
}

#[derive(Debug)]
pub enum NativePocaExportError {
    Boundary(NativeInferenceError),
    Activity(PocaActivityBatchError),
}

#[derive(Debug)]
pub enum NativeInferenceRunError<E> {
    Boundary(NativeInferenceError),
    Backend(E),
}

/// Cumulative, low-cardinality measurements: avoid tracing every action
/// or allocating a per-row timestamp. Model forward elapsed includes
/// accelerator launch/synchronization only when its backend synchronizes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeInferenceMeasurements {
    pub model_calls: u64,
    pub model_rows: u64,
    /// Validated action rows with a real MA-POCA participant identity,
    /// distinct from the live ship ID. A missing identity is never
    /// silently repaired by substituting the physical ship ID.
    pub grouped_actor_rows: u64,
    /// Wall time of calls selected for timing, not the sum of all calls.
    pub model_forward_nanos: u128,
    pub model_forward_timed_calls: u64,
    pub model_forward_timed_rows: u64,
    pub backend_failures: u64,
    pub invalid_action_batches: u64,
}

/// Reuse numeric observation and action buffers across inference requests.
/// A single instance may contain agents from multiple independent native
/// Levels, enabling CPU/GPU batching without a process-global world state.
#[derive(Default)]
pub struct NativeLevelInferenceBatch {
    batch: PolicyBatch,
    writer: NativePerceptionWriter,
    keyed_decisions: Vec<(PolicyAgentKey, Decision)>,
    /// Reused per-world row positions, avoiding rescanning every agent
    /// in the multi-world model batch at every dispatch.
    world_rows: HashMap<WorldId, Vec<usize>>,
    routes: Vec<NativePolicyRoute>,
    /// Source MA-POCA Agent identity per inference row, distinct from
    /// PolicyAgentKey.agent which is always the current physical ship.
    group_agent_ids: Vec<Option<u64>>,
    group_agents_seen: std::collections::HashSet<(WorldId, u64)>,
    /// Actual controller instance at the moment each observation row was
    /// encoded; an agent ID can outlive a controller rebind on the same tick.
    binding_revisions: Vec<u64>,
    finalized: bool,
    failed: bool,
    measurements: NativeInferenceMeasurements,
    /// Never reset by clear(): the sampling schedule spans batch reuse.
    rollout_calls: u64,
}

impl NativeLevelInferenceBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) {
        self.batch.clear();
        self.keyed_decisions.clear();
        for rows in self.world_rows.values_mut() {
            rows.clear();
        }
        self.routes.clear();
        self.group_agent_ids.clear();
        self.group_agents_seen.clear();
        self.binding_revisions.clear();
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
        self.routes.try_reserve(
            count.saturating_sub(self.routes.len()),
        ).map_err(|_| NativeInferenceError::Batch(
            PolicyBatchError::AllocationFailed,
        ))?;
        self.group_agent_ids.try_reserve(
            count.saturating_sub(self.group_agent_ids.len()),
        ).map_err(|_| NativeInferenceError::Batch(
            PolicyBatchError::AllocationFailed,
        ))?;
        self.group_agents_seen.try_reserve(
            count.saturating_sub(self.group_agents_seen.len()),
        ).map_err(|_| NativeInferenceError::Batch(
            PolicyBatchError::AllocationFailed,
        ))?;
        self.binding_revisions.try_reserve(
            count.saturating_sub(self.binding_revisions.len()),
        ).map_err(|_| NativeInferenceError::Batch(
            PolicyBatchError::AllocationFailed,
        ))?;
        Ok(())
    }

    pub fn measurements(&self) -> NativeInferenceMeasurements {
        self.measurements
    }

    /// Use Simulation's existing optional timing sample period for
    /// complete rollout phases, not a separate always-on profiler.
    /// One counter increment per call when timing is disabled; the
    /// clock is queried only on selected calls.
    pub(crate) fn sample_rollout_phase_timing(
        &mut self,
        every: Option<NonZeroU32>,
    ) -> bool {
        let sequence = self.rollout_calls;
        self.rollout_calls = self.rollout_calls.wrapping_add(1);
        every.is_some_and(|period|
            sequence % u64::from(period.get()) == 0
        )
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

    pub fn routes(&self) -> &[NativePolicyRoute] {
        &self.routes
    }

    /// Row-aligned with keys()/observations()/routes()/actions().
    /// None is reserved for the non-group game and older source scenes.
    /// The owner must not fabricate an MA-POCA Agent from its ship ID.
    pub fn group_agent_ids(&self) -> &[Option<u64>] {
        &self.group_agent_ids
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
        route: NativePolicyRoute,
        group_agent_id: Option<u64>,
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
        if group_agent_id == Some(0) {
            return Err(NativeInferenceError::InvalidAgent);
        }
        if group_agent_id.is_some_and(|group_agent|
            self.group_agents_seen.contains(&(world.id, group_agent))
        ) {
            return Err(NativeInferenceError::RepeatedGroupAgent {
                world: world.id,
                agent: group_agent_id.expect("validated group agent"),
            });
        }
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
        if !world.state.events.sides().contains(&ship.side) {
            return Err(NativeInferenceError::SnapshotStateMismatch);
        }
        if world.state.events.policy_team_id(ship.side)
            .is_some_and(|team| team != route.team_id) {
            return Err(NativeInferenceError::UnmatchedTeamRoute);
        }
        let mask = combat.policy_action_mask(ship_id)?;
        let frame = combat.policy_frame(ship_id)
            .ok_or(NativeInferenceError::InvalidAgent)?;
        let bind_revision = combat.policy_binding_revision(ship_id)
            .ok_or(NativeInferenceError::InvalidAgent)?;
        let key = PolicyAgentKey::from_world(world, agent);
        self.writer.push_to_batch(&mut self.batch, key, mask, snapshot, frame)?;
        let row = self.batch.keys().len() - 1;
        self.world_rows.entry(world.id).or_default().push(row);
        self.routes.push(route);
        self.group_agent_ids.push(group_agent_id);
        if let Some(group_agent) = group_agent_id {
            self.group_agents_seen.insert((world.id, group_agent));
        }
        self.binding_revisions.push(bind_revision);
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
            self.measurements.invalid_action_batches += 1;
            return Err(NativeInferenceError::Batch(error));
        }
        self.measurements.grouped_actor_rows = self.measurements
            .grouped_actor_rows
            .saturating_add(self.group_agents_seen.len() as u64);
        self.finalized = true;
        Ok(&self.keyed_decisions)
    }

    /// Standalone inference records model forward elapsed time as before.
    /// A multi-world rollout can instead use its shared timing sampling
    /// policy via infer_with_timing to avoid a clock read on every call.
    pub fn infer<B: NativePolicyInference>(
        &mut self,
        backend: &mut B,
    ) -> Result<&[(PolicyAgentKey, Decision)], NativeInferenceRunError<B::Error>> {
        self.infer_with_timing(backend, true)
    }

    /// Execute one complete model inference against the v26 buffers.
    /// Failure invalidates the whole batch, including partially written
    /// actions. All calls count model rows; only selected calls contribute
    /// wall time, with their own sample denominators.
    pub(crate) fn infer_with_timing<B: NativePolicyInference>(
        &mut self,
        backend: &mut B,
        sample_timing: bool,
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
        let (keys, observations, action_masks, continuous, discrete) =
            self.batch.inference_buffers_mut();
        debug_assert_eq!(keys.len(), self.routes.len());
        let started = sample_timing.then(Instant::now);
        let result = backend.forward(
            keys, &self.routes, observations, action_masks,
            continuous, discrete,
        );
        self.measurements.model_calls += 1;
        self.measurements.model_rows += keys.len() as u64;
        if let Some(started) = started {
            self.measurements.model_forward_timed_calls += 1;
            self.measurements.model_forward_timed_rows += keys.len() as u64;
            self.measurements.model_forward_nanos =
                self.measurements.model_forward_nanos
                    .saturating_add(started.elapsed().as_nanos());
        }
        if let Err(error) = result {
            self.failed = true;
            self.measurements.backend_failures += 1;
            return Err(NativeInferenceRunError::Backend(error));
        }
        self.finalize().map_err(NativeInferenceRunError::Boundary)
    }

    /// Export the complete source-defined PPO action activity weights
    /// after one inference batch has been validated. The caller supplies
    /// the actual POCA live-groupmate count for each action row, including
    /// zero for a truly isolated agent. No implicit group or fabricated
    /// critic estimate is created by this method.
    pub fn poca_dimension_activity(
        &self,
        groupmate_counts: &[u32],
        output: &mut Vec<PolicyDimensionActivity>,
    ) -> Result<(), NativePocaExportError> {
        if self.failed {
            return Err(NativePocaExportError::Boundary(
                NativeInferenceError::FailedBatch,
            ));
        }
        if !self.finalized {
            return Err(NativePocaExportError::Boundary(
                NativeInferenceError::NotFinalized,
            ));
        }
        self.batch.poca_dimension_activity(groupmate_counts, output)
            .map_err(NativePocaExportError::Activity)
    }

    /// World and controller instance must both still match the row's
    /// original observation. Rebinding can occur without advancing a tick.
    pub fn is_row_current_for<M, A>(
        &self,
        world: &World<NativeLevelState<M, A>>,
        row: usize,
    ) -> bool {
        let (Some(key), Some(&expected)) = (
            self.batch.keys().get(row),
            self.binding_revisions.get(row),
        ) else {
            return false;
        };
        key.is_current_for(world) &&
            i64::try_from(key.agent).ok().and_then(|ship_id|
                world.state.combat.policy_binding_revision(ship_id)
            ) == Some(expected)
    }

    /// Dispatch only this world's indexed rows. Check all keys against
    /// current episode/tick BEFORE changing the caller's output.
    /// Interleaved world rows retain their insertion order.
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
        let rows = self.world_rows.get(&world.id);
        if let Some(rows) = rows {
            for &row in rows {
                let key = self.keyed_decisions[row].0;
                if !self.is_row_current_for(world, row) {
                    return Err(NativeInferenceError::StaleAction(key));
                }
            }
        }
        output.clear();
        if let Some(rows) = rows {
            output.reserve(rows.len());
            for &row in rows {
                output.push(self.keyed_decisions[row].1);
            }
        }
        Ok(())
    }
}
