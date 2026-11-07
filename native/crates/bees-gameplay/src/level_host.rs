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

use std::num::NonZeroU32;
use bees_hivemind::session::{
    HiveMindSession, HiveMindSessionError, SquadLifetime, StrategyReceipt,
    TargetingReceipt,
};
use bees_hivemind::protocol::{
    GetStrategyResponse, GetTargetingResponse,
};
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
    flush_all_squad_retirements, abort_failed_level, cancel_level,
    NativeBatchedStepCounts, NativeBatchedStepError, NativeLevelResetError,
    NativeFailedLevelAbortError, NativeLevelCancelError,
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

/// One authoritative registration/lifetime lookup for strategy and
/// targeting replies. A malformed active-world registry must not be
/// mistaken for a dead squad and must not consume its valid server reply.
#[derive(Debug)]
pub enum NativeLevelRoutingError {
    UnregisteredWorldCount,
    WorldIdentityChanged { index: usize },
    EpisodeRevisionChanged { index: usize },
    DuplicateOwnerAcrossWorlds { first: usize, second: usize },
}

#[derive(Debug)]
pub enum NativeLevelStrategyReceiveError {
    Session(HiveMindSessionError),
    Routing(NativeLevelRoutingError),
    Activation(StrategyReceiptFailure),
}

/// Targeting choices are independent server reservations. This delivery
/// routes the validated response to the correct live Level but leaves the
/// actual ship/weapon targeting installation and TSV attribution with
/// its authored Level adapter. Do not credit targeting until installed.
#[derive(Debug)]
pub struct NativeLevelTargetingDelivery {
    pub world_index: Option<usize>,
    pub receipt: TargetingReceipt,
}

#[derive(Debug)]
pub enum NativeLevelTargetingReceiveError {
    Session(HiveMindSessionError),
    Routing(NativeLevelRoutingError),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelDriverError {
    MissingWorld,
    IndexOutOfOrder,
    AlreadyRegistered,
    UnstartedWorldRequired,
    WorldIdentityChanged,
}

/// Summary of one explicit bounded native rollout window, using the
/// same simulation, source metadata, model backend and notification sink
/// as individual fixed steps. No per-step histories are retained.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelWindowCounts {
    pub rounds: u32,
    pub world_steps: usize,
    pub policy_rows: usize,
    pub retirement_notifications: usize,
    pub delivered_notifications: usize,
    pub stopped_with_no_live_worlds: bool,
}

#[derive(Debug)]
pub enum NativeLevelWindowError<SourceError, BackendError, SinkError> {
    Step {
        round: u32,
        error: NativeBatchedStepError<NativeLevelSourceError<SourceError>, BackendError>,
    },
    Delivery {
        round: u32,
        error: NativeNotificationDeliveryError<SinkError>,
    },
}

/// A normal operator stop and a failed-step abort have different source
/// contracts. Neither may manufacture terminal rewards or erase unearned
/// HiveMind OutcomeIds.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelStopKind {
    CancelLive,
    AbortFailed,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelStopDelivery {
    pub aborted_command_reservations: usize,
    pub retired_squad_lifetimes: usize,
    pub delivered_notifications: usize,
}

#[derive(Debug)]
pub enum NativeLevelStopError<E> {
    Driver(NativeLevelDriverError),
    Cancel(NativeLevelCancelError),
    Abort(NativeFailedLevelAbortError),
    Delivery(NativeNotificationDeliveryError<E>),
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

    /// Source-correct, allocation-free routing shared by the two HiveMind
    /// request types. The connection already owns the only pending-hash map;
    /// this scans native Level registries only when a server reply arrives.
    fn locate_live_owner<M, A>(
        &self,
        simulation: &Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        owner: SquadLifetime,
    ) -> Result<Option<usize>, NativeLevelRoutingError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        if self.worlds.len() != simulation.world_count() {
            return Err(NativeLevelRoutingError::UnregisteredWorldCount);
        }
        let mut found = None;
        for (index, slot) in self.worlds.iter().enumerate() {
            let world = simulation.world(index)
                .expect("validated synchronous world count");
            if slot.id != world.id {
                return Err(NativeLevelRoutingError::WorldIdentityChanged { index });
            }
            if slot.revision != world.episode_revision() {
                return Err(NativeLevelRoutingError::EpisodeRevisionChanged { index });
            }
            if world.is_failed() || world.state.events.is_failed() ||
                world.state.events.completed() ||
                world.state.events.was_aborted() ||
                world.state.events.roster().get(owner).is_none()
            {
                continue;
            }
            if let Some(first) = found {
                return Err(NativeLevelRoutingError::DuplicateOwnerAcrossWorlds {
                    first, second: index,
                });
            }
            found = Some(index);
        }
        Ok(found)
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
        // The connection owns request correlation for ALL Levels. Do not
        // consume a reply if world registrations or live owner identities
        // are ambiguous; the request then remains retrievable by Hash.
        let owner = connection.requested_strategy_owner(response.hash)
            .ok_or(NativeLevelStrategyReceiveError::Session(
                HiveMindSessionError::UnknownRequest,
            ))?;
        let found = self.locate_live_owner(simulation, owner)
            .map_err(NativeLevelStrategyReceiveError::Routing)?;
        // Death/terminal retirement instructions persist across rollout
        // windows. Apply them before validating this server response.
        flush_all_squad_retirements(simulation, connection);
        let receipt = connection.receive_strategy(
            response, found.map(|_| owner),
        ).map_err(NativeLevelStrategyReceiveError::Session)?;
        match receipt {
            StrategyReceipt::Retired { reservation_ids, .. } =>
                Ok(NativeLevelStrategyDelivery {
                    world_index: None,
                    action: StrategyReceiptAction::DiscardReservations(
                        reservation_ids,
                    ),
                }),
            accepted @ StrategyReceipt::Accepted { .. } => {
                let index = found.expect("validated accepted live owner");
                let action = self.apply_strategy_receipt(
                    simulation, index, accepted,
                ).map_err(NativeLevelStrategyReceiveError::Activation)?;
                Ok(NativeLevelStrategyDelivery {
                    world_index: Some(index), action,
                })
            }
        }
    }

    /// Receive a separate GetTargetingResponse from the SAME shared
    /// connection and route it by exact source squad lifetime. Accepted
    /// targeting must still be applied by the destination Level's real
    /// shooting/targeting owner, which also retains the outcome reservation
    /// for later StoreCommands settlement. This method never silently
    /// converts a valid targeting reservation into command TSV.
    pub fn receive_targeting_response<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        connection: &mut HiveMindSession,
        response: &GetTargetingResponse,
    ) -> Result<NativeLevelTargetingDelivery, NativeLevelTargetingReceiveError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let owner = connection.requested_targeting_owner(response.hash)
            .ok_or(NativeLevelTargetingReceiveError::Session(
                HiveMindSessionError::UnknownRequest,
            ))?;
        let found = self.locate_live_owner(simulation, owner)
            .map_err(NativeLevelTargetingReceiveError::Routing)?;
        flush_all_squad_retirements(simulation, connection);
        let receipt = connection.receive_targeting(
            response, found.map(|_| owner),
        ).map_err(NativeLevelTargetingReceiveError::Session)?;
        let world_index = match receipt {
            TargetingReceipt::Retired { .. } => None,
            TargetingReceipt::Accepted { .. } =>
                Some(found.expect("validated accepted live owner")),
        };
        Ok(NativeLevelTargetingDelivery {
            world_index, receipt,
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

    /// Drive a bounded synchronous rollout window across all registered
    /// Levels with the REAL v26 model, shared physics and one durable
    /// notification sink. A rendered client uses delivery_every = 1 to
    /// publish immediately; headless workers may choose larger explicit
    /// intervals to amortize transport overhead while respecting the
    /// bounded Level notification outbox.
    ///
    /// On ANY failed model, gameplay or delivery step, return immediately.
    /// Never retry a partially stepped world or quietly replace invalid
    /// actions. Already accepted notifications are acknowledged; later
    /// notifications remain pending. The owner must abort failed episodes
    /// and discard their partial PPO trajectories through the existing
    /// failure boundary before reset.
    pub fn run_window<M, A, B, S>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
        connection: &mut HiveMindSession,
        sink: &mut S,
        max_rounds: NonZeroU32,
        delivery_every: NonZeroU32,
    ) -> Result<NativeLevelWindowCounts,
        NativeLevelWindowError<
            <M as NativeLevelObservationContext>::Error, B::Error, S::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        let mut counts = NativeLevelWindowCounts::default();
        // An earlier window may have deferred its last notification
        // delivery. Never start another inference batch over a backlog
        // that the sink has not yet durably accepted.
        counts.retirement_notifications +=
            flush_all_squad_retirements(simulation, connection);
        counts.delivered_notifications +=
            deliver_all_level_notifications(simulation, sink)
                .map_err(|error| NativeLevelWindowError::Delivery {
                    round: 0, error,
                })?.notifications_accepted;
        for round in 1..=max_rounds.get() {
            let step = self.step(simulation, backend)
                .map_err(|error| NativeLevelWindowError::Step {
                    round, error,
                })?;
            counts.rounds += 1;
            counts.world_steps += step.worlds_stepped;
            counts.policy_rows += step.policy_agents_inferred;
            if round % delivery_every.get() == 0 ||
                step.worlds_stepped == 0 ||
                round == max_rounds.get()
            {
                counts.retirement_notifications +=
                    flush_all_squad_retirements(simulation, connection);
                counts.delivered_notifications +=
                    deliver_all_level_notifications(simulation, sink)
                        .map_err(|error| NativeLevelWindowError::Delivery {
                            round, error,
                        })?.notifications_accepted;
            }
            if step.worlds_stepped == 0 {
                counts.stopped_with_no_live_worlds = true;
                break;
            }
        }
        Ok(counts)
    }

    /// One explicit stop/abort handoff for a long-running experiment.
    /// The existing game Level owns its command/episode cleanup, the
    /// connection owns squad request retirement, and the sink owns durable
    /// StoreCommands/MA-POCA/presentation delivery. No retry or silent
    /// recovery occurs: any failed sink acknowledgement leaves the
    /// unaccepted notifications in the existing persistent outbox.
    ///
    /// A successful return permits a later reset_world_after_delivery;
    /// that reset independently rejects any unresolved world obligations.
    pub fn stop_world_and_deliver<M, A, S>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        index: usize,
        kind: NativeLevelStopKind,
        connection: &mut HiveMindSession,
        sink: &mut S,
    ) -> Result<NativeLevelStopDelivery, NativeLevelStopError<S::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        S: NativeLevelNotificationSink,
    {
        let slot = self.worlds.get(index).ok_or(
            NativeLevelStopError::Driver(NativeLevelDriverError::MissingWorld),
        )?;
        let world = simulation.world(index).ok_or(
            NativeLevelStopError::Driver(NativeLevelDriverError::MissingWorld),
        )?;
        if slot.id != world.id ||
            slot.revision != world.episode_revision()
        {
            return Err(NativeLevelStopError::Driver(
                NativeLevelDriverError::WorldIdentityChanged,
            ));
        }
        let aborted = match kind {
            NativeLevelStopKind::CancelLive =>
                cancel_level(simulation, index)
                    .map_err(NativeLevelStopError::Cancel)?,
            NativeLevelStopKind::AbortFailed =>
                abort_failed_level(simulation, index)
                    .map_err(NativeLevelStopError::Abort)?,
        };
        let retired = flush_all_squad_retirements(
            simulation, connection,
        );
        let delivered = deliver_all_level_notifications(
            simulation, sink,
        ).map_err(NativeLevelStopError::Delivery)?.notifications_accepted;
        Ok(NativeLevelStopDelivery {
            aborted_command_reservations: aborted,
            retired_squad_lifetimes: retired,
            delivered_notifications: delivered,
        })
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
/// For idempotent external storage, use the host's run incarnation plus
/// WorldId, episode_revision and event_sequence as the stable event key.
/// These identifiers cannot be substituted for server OutcomeIds.
pub trait NativeLevelNotificationSink {
    type Error;
    fn accept(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        event_sequence: u64,
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
    NotificationSequenceExhausted { index: usize },
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
        let first_sequence =
            world.state.events.pending_notification_sequence();
        let count = world.state.events.pending_notifications().len();
        // Preflight the entire contiguous range before any side effects,
        // so an exhausted sequence can never partially reach a sink.
        let upper = u64::try_from(count).ok()
            .and_then(|len| first_sequence.checked_add(len))
            .ok_or(
                NativeNotificationDeliveryError::NotificationSequenceExhausted {
                    index,
                }
            )?;
        debug_assert!(upper >= first_sequence);
        let (accepted, failure) = {
            let notifications = world.state.events.pending_notifications();
            let mut accepted = 0usize;
            let mut failure = None;
            for (offset, notification) in notifications.iter().enumerate() {
                // Range checked above, and each offset is strictly smaller
                // than the pending queue length.
                let event_sequence = first_sequence + offset as u64;
                match sink.accept(
                    world.id, world.episode_revision(),
                    event_sequence, notification,
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
