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

use bees_hivemind::session::{
    HiveMindSession, HiveMindSessionError, SquadLifetime, StrategyReceipt,
};
use bees_hivemind::protocol::GetStrategyResponse;
use bees_hivemind::dispatch::{
    LocalCommandKind, SquadCommandComposition, resolve_hivemind_command,
};
use bees_sim::combat_perception_bridge::AuthoredShipPerception;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};
use bees_sim::simulation::World;
use bees_sim::policy::Decision;
use bees_sim::simulation::{Simulation, WorldId};

use crate::inference::{
    NativeLevelInferenceBatch, NativePolicyInference, NativePolicyRoute,
};
use crate::commands::{
    GameCommandError, HiveMindCommandSettings, NativeBombWeapon,
    NativeEnemySquad, NativeEnemyTargetStats, NativeFriendlySquad,
    NativeScoutingContext, StrategyReceiptAction, StrategyReceiptFailure,
    StrategyReceiptFailureCause,
};
use crate::level_inputs::native_squad_order_from_catalog;
use crate::squads::NativeSquadRoster;
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
    step_batched_levels, reset_level_after_delivery,
    flush_all_squad_retirements, NativeBatchedStepCounts,
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
        &'a self, world: &'a CombatWorld, ship_id: i64,
    ) -> Result<NativeLevelPerceptionInputs<'a>, Self::Error>;

    fn authored_ship_perception(&self, ship_id: i64)
        -> Option<AuthoredShipPerception>;
}


/// All nonphysical values required to create one real Unity command.
/// The original enemy lifecycle comes from the connection's accepted
/// request, never a lookup by the server's reused numeric Squad ID.
#[derive(Clone, Copy, Debug)]
pub struct NativeLevelStrategyInputs {
    pub formation: SquadFormationOptions,
    pub enemy: Option<NativeEnemySquad>,
    pub scouting: Option<NativeScoutingContext>,
    pub settings: HiveMindCommandSettings,
}

/// Source-owned command activation values absent from CombatWorld.
/// These are supplied by the real Level's current Squad/FleetShip/weapon
/// registry; no guessed formation offsets, path clearance, Bomb weapons
/// or target rankings are introduced by the host.
pub trait NativeLevelActivationContext: NativeLevelCommandContext {
    fn strategy_inputs(
        &self, owner: SquadLifetime, original_enemy: Option<SquadLifetime>,
    ) -> Result<NativeLevelStrategyInputs, GameCommandError>;

    fn stored_formation_offset(&self, ship_id: i64) -> Option<Vec2>;

    fn current_path_clearance(&self, ship_id: i64) -> Option<i32>;

    /// Called ONLY for a composition-resolved BombingRun or Charge.
    /// Write current Unity GetShips-ordered enemy targeting stats and live
    /// bomb weapon identities into the caller's reused output buffers.
    /// Return the source Squad.IsCarrierSquad value, never a ship-type guess.
    fn collect_bomb_activation(
        &self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy_owner: SquadLifetime,
        weapons: &mut Vec<NativeBombWeapon>,
        enemies: &mut Vec<NativeEnemyTargetStats>,
    ) -> Result<bool, GameCommandError>;
}

/// Allocation-reusing activation scratch owned by a Level host; it is not
/// another gameplay registry. All work is preflighted before a command
/// can replace its predecessor or consume a server reservation.
#[derive(Default)]
pub struct NativeLevelActivationScratch {
    members: Vec<SquadFormationMember>,
    friendlies: Vec<NativeFriendlySquad>,
    bomb_weapons: Vec<NativeBombWeapon>,
    target_stats: Vec<NativeEnemyTargetStats>,
    ship_types: Vec<i32>,
}

impl NativeLevelActivationScratch {
    pub fn new() -> Self { Self::default() }
}

/// Activate the real server-selected command on one existing native world.
/// The connection owns the request/response ledger; this function consumes
/// the exact StrategyReceipt, uses the already-registered squad, and returns
/// the existing Applied/Discard/Ban obligations for durable settlement.
///
/// A source metadata failure returns StrategyReceiptFailure with original
/// selection and both reservations intact. A retired owner or stale enemy
/// identity returns explicit DiscardReservations without gameplay mutation.
pub fn apply_native_level_strategy_receipt<M, A>(
    world: &mut World<NativeLevelState<M, A>>,
    receipt: StrategyReceipt,
    scratch: &mut NativeLevelActivationScratch,
) -> Result<StrategyReceiptAction, StrategyReceiptFailure>
where
    M: NativeLevelRewardMetadata + NativeLevelActivationContext,
{
    let (owner, original_enemy, selection) = match &receipt {
        StrategyReceipt::Retired { reservation_ids, .. } =>
            return Ok(StrategyReceiptAction::DiscardReservations(
                reservation_ids.clone(),
            )),
        StrategyReceipt::Accepted { owner, enemy_owner, selection } =>
            (*owner, *enemy_owner, selection.clone()),
    };
    let discard = || StrategyReceiptAction::DiscardReservations(vec![
        selection.command_outcome_id, selection.shooting_outcome_id,
    ]);
    if world.is_failed() || world.state.events.is_failed() ||
        world.state.events.completed() ||
        world.state.events.roster().get(owner).is_none()
    {
        return Ok(discard());
    }
    let fail = |error: GameCommandError| StrategyReceiptFailure {
        selection: selection.clone(),
        cause: StrategyReceiptFailureCause::InvalidSquad(error),
    };

    // Resolve the same original HiveMind dispatch rule before collecting
    // command-specific authored inputs. The actual director still resolves
    // and validates the selection at mutation time.
    scratch.ship_types.clear();
    let squad = world.state.events.roster().get(owner)
        .expect("prevalidated live source squad");
    for &id in &squad.members {
        let ship = world.state.combat.ship(id)
            .ok_or_else(|| fail(GameCommandError::InvalidSquad))?;
        if ship.vitals.is_dead() {
            return Err(fail(GameCommandError::InvalidSquad));
        }
        scratch.ship_types.push(i32::from(ship.ship_type));
    }
    let composition = SquadCommandComposition::from_unity_ship_types(
        scratch.ship_types.iter().copied(),
    );
    let resolved = resolve_hivemind_command(&selection, composition)
        .map_err(|error| StrategyReceiptFailure {
            selection: selection.clone(),
            cause: StrategyReceiptFailureCause::InvalidStrategy(error),
        })?;
    let metadata = &mut world.state.metadata;
    let details = metadata.strategy_inputs(owner, original_enemy)
        .map_err(&fail)?;
    if !details.formation.valid() || !details.settings.valid() {
        return Err(fail(GameCommandError::InvalidSettings));
    }
    let targeted = matches!(resolved.local_command,
        LocalCommandKind::Retreat | LocalCommandKind::Aggressive |
        LocalCommandKind::CircleSquad | LocalCommandKind::RightSwipe |
        LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut |
        LocalCommandKind::BombingRun | LocalCommandKind::Charge);
    if targeted && (original_enemy.is_none() ||
        details.enemy.map(|enemy| enemy.owner) != original_enemy)
    {
        return Ok(discard());
    }

    scratch.friendlies.clear();
    if matches!(resolved.local_command,
        LocalCommandKind::ClosestFriendly | LocalCommandKind::Guard)
    {
        metadata.collect_live_friendlies(
            &world.state.combat, world.state.events.roster(),
            &mut scratch.friendlies,
        ).map_err(&fail)?;
    }
    scratch.bomb_weapons.clear();
    scratch.target_stats.clear();
    let needs_bomb = matches!(resolved.local_command,
        LocalCommandKind::BombingRun | LocalCommandKind::Charge);
    let carrier_squad = if needs_bomb {
        let enemy = original_enemy.ok_or_else(||
            fail(GameCommandError::InvalidEnemy)
        )?;
        metadata.collect_bomb_activation(
            &world.state.combat, world.state.events.roster(), owner, enemy,
            &mut scratch.bomb_weapons, &mut scratch.target_stats,
        ).map_err(&fail)?
    } else {
        false
    };

    let catalog = metadata.ship_asset_catalog()
        .ok_or_else(|| fail(GameCommandError::MissingScoutingContext))?;
    let order = native_squad_order_from_catalog(
        &world.state.combat,
        world.state.events.roster().get(owner)
            .expect("prevalidated squad before command activation"),
        catalog, details.formation, details.enemy, details.scouting,
        |id| metadata.stored_formation_offset(id),
        |id| metadata.current_path_clearance(id),
        &mut scratch.members,
    ).map_err(&fail)?;
    let navigation = metadata.navigation();
    if needs_bomb {
        world.state.events.apply_strategy_receipt_with_bombing(
            &mut world.state.combat, navigation, &mut world.random,
            order, &scratch.friendlies, details.settings, receipt,
            original_enemy.expect("validated targeted strategy"),
            &scratch.bomb_weapons, &scratch.target_stats, carrier_squad,
        )
    } else {
        world.state.events.apply_strategy_receipt(
            &mut world.state.combat, navigation, &mut world.random,
            order, &scratch.friendlies, details.settings, receipt,
        )
    }
}

#[derive(Debug)]
pub enum NativeLevelSourceError<E> {
    MissingRegisteredWorld,
    UnregisteredWorldCount,
    WorldIdentityChanged,
    EpisodeRevisionChanged,
    Source(E),
    Perception(NativeWorldPerceptionError),
}


/// One decoded, connection-correlated strategy result. A retired reply
/// belongs to no live world and its two server OutcomeIds must be explicitly
/// discarded by the existing durable StoreCommands pipeline.
#[derive(Debug)]
pub struct NativeLevelStrategyDelivery {
    pub world_index: Option<usize>,
    pub action: StrategyReceiptAction,
}

#[derive(Debug)]
pub enum NativeLevelStrategyReceiveError {
    Session(HiveMindSessionError),
    Driver(NativeLevelDriverError),
    /// A reused Level/squad/lifecycle identity across live worlds is
    /// ambiguous, so do not consume a response and guess its recipient.
    DuplicateOwnerAcrossWorlds { first: usize, second: usize },
    Activation(StrategyReceiptFailure),
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
    activation: NativeLevelActivationScratch,
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
            activation: NativeLevelActivationScratch::new(),
        });
        Ok(())
    }

    /// Apply a connection-correlated strategy reply to a live native Level.
    /// Same-world action and persistent reservations are returned through
    /// the existing command director, not through a training-only path.
    pub fn apply_strategy_receipt<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        index: usize,
        receipt: StrategyReceipt,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let selection = match &receipt {
            StrategyReceipt::Retired { reservation_ids, .. } =>
                return Ok(StrategyReceiptAction::DiscardReservations(
                    reservation_ids.clone(),
                )),
            StrategyReceipt::Accepted { selection, .. } =>
                selection.clone(),
        };
        let fail = |error| StrategyReceiptFailure {
            selection: selection.clone(),
            cause: StrategyReceiptFailureCause::InvalidSquad(error),
        };
        let slot = self.worlds.get_mut(index)
            .ok_or_else(|| fail(GameCommandError::InvalidSquad))?;
        let world = simulation.world_mut(index)
            .ok_or_else(|| fail(GameCommandError::InvalidSquad))?;
        if slot.id != world.id ||
            slot.revision != world.episode_revision()
        {
            return Err(fail(GameCommandError::InvalidSquad));
        }
        apply_native_level_strategy_receipt(
            world, receipt, &mut slot.activation,
        )
    }

    /// Correlate one real BeesServer GetStrategyResponse to the connection's
    /// original request, flush pending squad retirements, and route to the
    /// owning live Level. This is the complete synchronous transport/Level
    /// handoff after the WebSocket adapter has validated its connection.
    ///
    /// Invalid wire replies leave their pending request intact. A reply
    /// for an already-dead squad becomes a durable discard obligation, not
    /// an action in a newer world. The server and StoreCommands persistence
    /// are still owned by the external transport.
    pub fn receive_strategy_response<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        connection: &mut HiveMindSession,
        response: &GetStrategyResponse,
    ) -> Result<NativeLevelStrategyDelivery, NativeLevelStrategyReceiveError>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        // Do not consume an authenticated server decision against a stale
        // registration. This includes quiet worlds with no due policy rows.
        if self.worlds.len() != simulation.world_count() {
            return Err(NativeLevelStrategyReceiveError::Driver(
                NativeLevelDriverError::MissingWorld,
            ));
        }
        for (index, slot) in self.worlds.iter().enumerate() {
            let world = simulation.world(index)
                .expect("validated fixed Simulation world count");
            if slot.id != world.id ||
                slot.revision != world.episode_revision()
            {
                return Err(NativeLevelStrategyReceiveError::Driver(
                    NativeLevelDriverError::WorldIdentityChanged,
                ));
            }
        }
        // The connection owns request correlation for ALL Levels. Apply
        // death/terminal retirement instructions before interpreting the
        // newest response, even when its numeric Squad ID was recycled.
        flush_all_squad_retirements(simulation, connection);
        let owner = connection.requested_strategy_owner(response.hash)
            .ok_or(NativeLevelStrategyReceiveError::Session(
                HiveMindSessionError::UnknownRequest,
            ))?;
        let mut found = None;
        for index in 0..simulation.world_count() {
            let world = simulation.world(index)
                .expect("validated fixed Simulation world count");
            if world.is_failed() || world.state.events.is_failed() ||
                world.state.events.completed() ||
                world.state.events.was_aborted() ||
                world.state.events.roster().get(owner).is_none()
            {
                continue;
            }
            if let Some(first) = found {
                return Err(
                    NativeLevelStrategyReceiveError::DuplicateOwnerAcrossWorlds {
                        first, second: index,
                    },
                );
            }
            found = Some(index);
        }
        let receipt = connection.receive_strategy(
            response, found.map(|_| owner),
        ).map_err(NativeLevelStrategyReceiveError::Session)?;
        let Some(index) = found else {
            let StrategyReceipt::Retired { reservation_ids, .. } = receipt
            else { unreachable!("a missing live owner cannot be accepted") };
            return Ok(NativeLevelStrategyDelivery {
                world_index: None,
                action: StrategyReceiptAction::DiscardReservations(
                    reservation_ids,
                ),
            });
        };
        let action = self.apply_strategy_receipt(
            simulation, index, receipt,
        ).map_err(NativeLevelStrategyReceiveError::Activation)?;
        Ok(NativeLevelStrategyDelivery {
            world_index: Some(index), action,
        })
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
        // Preflight ALL slots, including complete worlds and worlds whose
        // policy period is not due. The lower-level observation callback is
        // intentionally skipped for those worlds; relying on it alone
        // would let a secretly reset arena advance several physics ticks
        // using the previous episode's identity permutation.
        if self.worlds.len() != simulation.world_count() {
            return Err(NativeBatchedStepError::Source {
                index: self.worlds.len().min(simulation.world_count()),
                error: NativeLevelSourceError::UnregisteredWorldCount,
            });
        }
        for (index, slot) in self.worlds.iter().enumerate() {
            let world = simulation.world(index)
                .expect("world count is stable during synchronous preflight");
            if slot.id != world.id {
                return Err(NativeBatchedStepError::Source {
                    index, error: NativeLevelSourceError::WorldIdentityChanged,
                });
            }
            if slot.revision != world.episode_revision() {
                return Err(NativeBatchedStepError::Source {
                    index, error: NativeLevelSourceError::EpisodeRevisionChanged,
                });
            }
        }
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
                let next_tick = world.tick().checked_add(1)
                    .ok_or(NativeLevelSourceError::EpisodeRevisionChanged)?;
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
