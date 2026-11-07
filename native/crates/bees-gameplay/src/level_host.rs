//! Complete native fixed-step/inference and delivery boundaries for both
//! headless workers and rendered Levels. This does not own a second game
//! world, a second command queue, a model implementation or a second
//! HiveMind reservation ledger.
//!
//! A NativeLevelBatchDriver owns only the per-world reusable v26 observation
//! assemblers, the accelerator batch and decision scratch. Both headless and
//! rendered hosts supply exact missing authored observation values through
//! NativeLevelObservationContext on their existing NativeLevelState.metadata.
//! Actual CPU/GPU inference is supplied by NativePolicyInference.
//!
//! Each world's model identity permutation is seeded from the same per-episode
//! RandomStream before its first tick, and must be refreshed by the guarded
//! episode-reset boundary. Stale world indices/revisions are rejected, not
//! silently allowed to corrupt PPO trajectories.

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_perception_bridge::AuthoredShipPerception;
use bees_sim::combat_world::CombatWorld;
use bees_sim::policy::Decision;
use bees_sim::simulation::{Simulation, WorldId};

use crate::inference::{
    NativeLevelInferenceBatch, NativePolicyInference, NativePolicyRoute,
};
use crate::level_events::{
    NativeLevelEventError, NativeLevelNotification,
};
use crate::level_rules::{
    NativeLevelCommandContext, NativeLevelRewardMetadata, NativeLevelRules,
    NativeLevelState,
};
use crate::perception_world::{
    NativeLevelPerceptionInputs, NativeWorldPerceptionAssembler,
    NativeWorldPerceptionError,
};
use crate::rollout::{
    step_batched_levels, reset_level_after_delivery, NativeBatchedStepCounts,
    NativeBatchedStepError, NativeLevelResetError,
};
use crate::squads::SquadRosterError;

/// Authored inputs not contained in CombatWorld. The concrete native Level
/// supplies actual current weapons, navigation grid, explored map, mining
/// targets, effective vision, FleetShip values and model route. This trait
/// intentionally has no default fake data or alternate training policy.
///
/// An implementation is per-Level (part of NativeLevelState.metadata).
/// Returning borrowed observation slices avoids copying world grids for
/// every policy agent in a densely populated arena.
pub trait NativeLevelObservationContext {
    type Error;

    fn route_for_agent(&self, ship_id: i64)
        -> Result<NativePolicyRoute, Self::Error>;

    fn inputs_for_agent<'a>(
        &'a self, world: &CombatWorld, ship_id: i64,
    ) -> Result<NativeLevelPerceptionInputs<'a>, Self::Error>;

    fn authored_ship_perception(&self, ship_id: i64)
        -> Option<AuthoredShipPerception>;
}

#[derive(Debug)]
pub enum NativeLevelSourceError<E> {
    MissingRegisteredWorld,
    WorldIdentityChanged,
    EpisodeRevisionChanged,
    Source(E),
    Perception(NativeWorldPerceptionError),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelDriverError {
    MissingWorld,
    IndexOutOfOrder,
    AlreadyRegistered,
    UnstartedWorldRequired,
    WorldIdentityChanged,
}

#[derive(Debug)]
pub enum NativeLevelDriverResetError {
    Driver(NativeLevelDriverError),
    Delivery(NativeLevelResetError),
}

struct NativeWorldAssembly {
    id: WorldId,
    revision: u64,
    perception: NativeWorldPerceptionAssembler,
}

/// Runs the REAL v26 observation writer, a genuine supplied model and the
/// single NativeLevelRules combat step. No per-tick roster copy, generated
/// action, training-only combat or hidden session state is introduced.
pub struct NativeLevelBatchDriver {
    worlds: Vec<NativeWorldAssembly>,
    inference: NativeLevelInferenceBatch,
    decisions: Vec<Decision>,
}

impl NativeLevelBatchDriver {
    /// Register the initial Level worlds before any simulation fixed step.
    /// Identity permutations consume their world-local RNG exactly once.
    pub fn from_simulation<M, A>(
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    ) -> Result<Self, NativeLevelDriverError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        for index in 0..simulation.world_count() {
            let world = simulation.world(index)
                .ok_or(NativeLevelDriverError::MissingWorld)?;
            if world.tick() != 0 || world.is_failed() ||
                world.state.events.is_failed() ||
                world.state.events.completed()
            {
                return Err(NativeLevelDriverError::UnstartedWorldRequired);
            }
        }
        let mut driver = Self {
            worlds: Vec::with_capacity(simulation.world_count()),
            inference: NativeLevelInferenceBatch::new(),
            decisions: Vec::new(),
        };
        for index in 0..simulation.world_count() {
            driver.register_added_world(simulation, index)?;
        }
        Ok(driver)
    }

    /// Attach a world added to Simulation after driver construction.
    /// Registration is append-only and follows Simulation::add_world order.
    /// An existing registered world may only be replaced through
    /// reset_world_after_delivery.
    pub fn register_added_world<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        index: usize,
    ) -> Result<(), NativeLevelDriverError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        if index < self.worlds.len() {
            return Err(NativeLevelDriverError::AlreadyRegistered);
        }
        if index != self.worlds.len() {
            return Err(NativeLevelDriverError::IndexOutOfOrder);
        }
        let world = simulation.world_mut(index)
            .ok_or(NativeLevelDriverError::MissingWorld)?;
        if world.tick() != 0 || world.is_failed() ||
            world.state.events.is_failed() ||
            world.state.events.completed()
        {
            return Err(NativeLevelDriverError::UnstartedWorldRequired);
        }
        self.worlds.push(NativeWorldAssembly {
            id: world.id,
            revision: world.episode_revision(),
            perception: NativeWorldPerceptionAssembler::from_random(
                &mut world.random,
            ),
        });
        Ok(())
    }

    pub fn registered_world_count(&self) -> usize {
        self.worlds.len()
    }

    pub fn inference(&self) -> &NativeLevelInferenceBatch {
        &self.inference
    }

    /// The caller may invalidate a world's learned-environment projections
    /// after source-side discovery changes between successful fixed steps.
    pub fn invalidate_environment(&mut self, index: usize)
        -> Result<(), NativeLevelDriverError>
    {
        let world = self.worlds.get_mut(index)
            .ok_or(NativeLevelDriverError::MissingWorld)?;
        world.perception.invalidate_environment();
        Ok(())
    }

    /// One complete model-inference and fixed-physics step across all live
    /// Levels. The model backend is injected, not fabricated. Observation
    /// completeness, world/episode/tick keys, policy masks and actions are
    /// validated by step_batched_levels before any world mutates.
    ///
    /// When a world fails during dispatch, step_batched_levels reports how
    /// many preceding worlds advanced. The owner must not reuse the partial
    /// PPO trajectory as if the batch succeeded.
    pub fn step<M, A, B>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
    ) -> Result<NativeBatchedStepCounts,
        NativeBatchedStepError<
            NativeLevelSourceError<<M as NativeLevelObservationContext>::Error>,
            B::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
    {
        let worlds = &mut self.worlds;
        step_batched_levels(
            simulation, &mut self.inference, backend,
            &mut |index, world, batch| {
                let state = worlds.get_mut(index)
                    .ok_or(NativeLevelSourceError::MissingRegisteredWorld)?;
                if state.id != world.id {
                    return Err(NativeLevelSourceError::WorldIdentityChanged);
                }
                if state.revision != world.episode_revision() {
                    return Err(NativeLevelSourceError::EpisodeRevisionChanged);
                }
                let metadata = &world.state.metadata;
                let next_tick = world.tick() + 1;
                for ship_id in world.state.decisions.due_agent_ids(next_tick) {
                    let route = metadata.route_for_agent(ship_id)
                        .map_err(NativeLevelSourceError::Source)?;
                    let inputs = metadata.inputs_for_agent(
                        &world.state.combat, ship_id,
                    ).map_err(NativeLevelSourceError::Source)?;
                    state.perception.push_agent(
                        batch, world, ship_id, route, inputs,
                        |id| metadata.authored_ship_perception(id),
                    ).map_err(NativeLevelSourceError::Perception)?;
                }
                Ok(())
            },
            &mut self.decisions,
        )
    }

    /// Atomic with respect to host-visible episode identity: old Level
    /// notifications and session lifetimes must be consumed before reset.
    /// Only after the authoritative World revision is advanced does the
    /// model assembler draw the next episode's identity permutation.
    pub fn reset_world_after_delivery<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        index: usize,
        seed: u64,
        replacement: NativeLevelState<M, A>,
    ) -> Result<(), NativeLevelDriverResetError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let slot = self.worlds.get_mut(index).ok_or(
            NativeLevelDriverResetError::Driver(
                NativeLevelDriverError::MissingWorld,
            )
        )?;
        let current = simulation.world(index).ok_or(
            NativeLevelDriverResetError::Driver(
                NativeLevelDriverError::MissingWorld,
            )
        )?;
        if slot.id != current.id ||
            slot.revision != current.episode_revision()
        {
            return Err(NativeLevelDriverResetError::Driver(
                NativeLevelDriverError::WorldIdentityChanged,
            ));
        }
        reset_level_after_delivery(simulation, index, seed, replacement)
            .map_err(NativeLevelDriverResetError::Delivery)?;
        let world = simulation.world_mut(index)
            .expect("validated existing native Level index");
        slot.perception.reset_episode(&mut world.random);
        slot.revision = world.episode_revision();
        Ok(())
    }
}

/// Every returned Ok from this sink means the notification was ACCEPTED and
/// durably handled (presentation applied, PPO group delivered, or command
/// reservation persisted). Its effect cannot depend on later notifications
/// being accepted; the host acknowledges the successful prefix on failure.
pub trait NativeLevelNotificationSink {
    type Error;
    fn accept(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        notification: &NativeLevelNotification,
    ) -> Result<(), Self::Error>;
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeNotificationDelivery {
    pub worlds_visited: usize,
    pub notifications_accepted: usize,
}

#[derive(Debug)]
pub enum NativeNotificationDeliveryError<E> {
    Sink {
        index: usize,
        accepted_before_failure: usize,
        error: E,
    },
    Acknowledgement {
        index: usize,
        error: NativeLevelEventError,
    },
}

/// Drain each world's ONE persistent, source-ordered gameplay/reward/command
/// notification outbox after a fixed-step window. On a delivery error, only
/// the accepted prefix is acknowledged. Every unaccepted record stays
/// pending for the caller to resolve. This also works on failed worlds:
/// EpisodeAborted and aborted OutcomeIds must reach the same sink before
/// the Level can reset.
///
/// Connection-owned HiveMindSession retirements are a different, already
/// implemented outbox: call rollout::flush_all_squad_retirements before
/// processing delayed server responses. Do not infer retirement from
/// notification kinds.
pub fn deliver_all_level_notifications<M, A, S>(
    simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    sink: &mut S,
) -> Result<NativeNotificationDelivery,
    NativeNotificationDeliveryError<S::Error>>
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    S: NativeLevelNotificationSink,
{
    let mut delivery = NativeNotificationDelivery::default();
    for index in 0..simulation.world_count() {
        let world = simulation.world_mut(index)
            .expect("indexed native world count cannot change during delivery");
        delivery.worlds_visited += 1;
        let (accepted, failure) = {
            let notifications = world.state.events.pending_notifications();
            let mut accepted = 0usize;
            let mut failure = None;
            for notification in notifications {
                match sink.accept(
                    world.id, world.episode_revision(), notification,
                ) {
                    Ok(()) => accepted += 1,
                    Err(error) => {
                        failure = Some(error);
                        break;
                    }
                }
            }
            (accepted, failure)
        };
        world.state.events.acknowledge_notifications(accepted)
            .map_err(|error|
                NativeNotificationDeliveryError::Acknowledgement {
                    index, error,
                }
            )?;
        delivery.notifications_accepted += accepted;
        if let Some(error) = failure {
            return Err(NativeNotificationDeliveryError::Sink {
                index, accepted_before_failure: accepted, error,
            });
        }
    }
    Ok(delivery)
}
