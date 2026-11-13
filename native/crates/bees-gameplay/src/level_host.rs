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

use std::num::{NonZeroU32, NonZeroUsize};
use bees_hivemind::connection::NativeHiveMindConnection;
use bees_hivemind::outcomes::StoreAcknowledgement;
use bees_hivemind::wire::{
    decode_hivemind_response_frame, HiveMindServerResponse, HiveMindWireError,
};
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
use bees_sim::combat_world::{CombatEvent, CombatWorld};
use bees_sim::geometry::Vec2;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};
use bees_sim::simulation::World;
use bees_sim::full_perception::NativePerceptionWriter;
use bees_sim::policy::{Decision, OBSERVATION_FLOATS, write_inactive_observation};
use bees_sim::simulation::{Simulation, WorldId};
use bees_sim::telemetry::Measurements;

use crate::inference::{
    NativeInferenceMeasurements, NativeLevelInferenceBatch,
    NativePolicyInference, NativePolicyRoute,
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
    step_batched_levels, step_batched_levels_parallel,
    reset_level_after_delivery,
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

    /// Source MA-POCA Agent identity bound to this ship. A group Agent
    /// can survive multiple physical hulls, so this is NOT ship_id.
    /// Return None only for a Level without authenticated group membership.
    fn group_agent_for_ship(&self, ship_id: i64)
        -> Result<Option<u64>, Self::Error>;

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
/// Authored Squad.Setup inputs only. The current command, members, movement,
/// targeting and request state remain owned by the live Level and socket.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeHiveMindSquadSource {
    pub opponent_id: u64,
    pub hivemind_controlled: bool,
    pub immobile: bool,
}

impl NativeHiveMindSquadSource {
    pub fn can_request(self) -> bool {
        self.hivemind_controlled && !self.immobile
    }
}

pub trait NativeLevelActivationContext: NativeLevelCommandContext {
    fn targeting_context(&self) -> Option<crate::squad_targeting::NativeTargetingContext>;
    fn targeting_ship_catalog(&self) -> &bees_sim::ship_assets::ShipAssetCatalog;

    /// Source Stage.ActivateHiveMind; None means an older export with no
    /// authorization to autonomously issue command reservations.
    fn source_hivemind_active(&self) -> Option<bool> { None }

    /// Unity Stage.InitialCommandDelay. This only schedules external
    /// request pulses, never commands or simulated physics. Legacy scene
    /// metadata without the field fails closed for autonomous requests.
    fn source_hivemind_initial_delay_seconds(&self) -> Option<i32> { None }

    /// Level.StartingPositions[side-1], never guessed from the map center
    /// or a ship's current physical placement.
    fn source_hivemind_starting_position(&self, _side: i32) -> Option<Vec2> { None }

    /// Per-Squad source authority, not a second gameplay roster. Dynamic
    /// native-created minions without such source inputs are ineligible
    /// until the Level explicitly registers their command authority.
    fn source_hivemind_squad(
        &self, _owner: SquadLifetime,
    ) -> Option<NativeHiveMindSquadSource> { None }

    /// Request-bound source matchup over the same canonical CombatWorld.
    /// A renderer/headless adapter MUST provide authentic source weapon
    /// validity. The default is an error, never fabricated training data.
    fn source_strategy_matchup(
        &self, _world: &mut CombatWorld, _roster: &NativeSquadRoster,
        _owner: SquadLifetime, _selected_enemy: Option<SquadLifetime>,
    ) -> Result<Option<crate::strategy_matchup::NativeSourceStrategyMatchup>, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Actual exported Level.ActivateMining, needed for source strategy
    /// eligibility; older snapshots return None and cannot safely issue
    /// automatic native HiveMind strategy requests.
    fn source_mining_enabled(&self) -> Option<bool> { None }

    fn strategy_inputs(
        &self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, original_enemy: Option<SquadLifetime>,
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
    targeting: crate::squad_targeting::NativeSquadTargetingScratch,
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
        world.state.events.roster().get(owner).is_none() ||
        world.state.metadata.scripted_queue_owns(owner)
    {
        // A scripted mission command supersedes the request's original
        // authority. Do not install a late server command over the queue;
        // the two returned OutcomeIds still require durable discard.
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
    // A strategy reply may arrive AFTER its original target died or its
    // Squad wrapper was recycled. Validate against the live roster before
    // asking the authored Level for command-specific snapshots, so absent
    // source metadata cannot turn a genuinely stale reply into an
    // unowned activation failure.
    let targeted = matches!(resolved.local_command,
        LocalCommandKind::Retreat | LocalCommandKind::Aggressive |
        LocalCommandKind::CircleSquad | LocalCommandKind::RightSwipe |
        LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut |
        LocalCommandKind::BombingRun | LocalCommandKind::Charge);
    if targeted {
        let Some(target_owner) = original_enemy else {
            return Ok(discard());
        };
        let Some(enemy_squad) =
            world.state.events.roster().get(target_owner) else {
            return Ok(discard());
        };
        if enemy_squad.side == squad.side ||
            !enemy_squad.members.iter().any(|&id|
                world.state.combat.ship(id).is_some_and(|ship|
                    !ship.vitals.is_dead()
                )
            )
        {
            return Ok(discard());
        }
    }
    if matches!(resolved.local_command, LocalCommandKind::Aggressive | LocalCommandKind::BombingRun |
        LocalCommandKind::Charge | LocalCommandKind::Retreat | LocalCommandKind::CircleSquad |
        LocalCommandKind::RightSwipe | LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut) &&
        squad.targeting.is_some_and(|choice| choice.enemy != original_enemy) {
        return Ok(discard());
    }
    let metadata = &mut world.state.metadata;
    let details = metadata.strategy_inputs(&world.state.combat, world.state.events.roster(), owner, original_enemy)
        .map_err(&fail)?;
    if !details.formation.valid() || !details.settings.valid() {
        return Err(fail(GameCommandError::InvalidSettings));
    }
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
            &world.state.combat, world.state.events.roster(), world.state.events.commands(),
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

#[derive(Clone, Debug)]
pub struct NativeSourceStrategyRequestInputs {
    pub matchup: crate::strategy_matchup::NativeSourceStrategyMatchup,
    pub activate_mining: bool,
}

#[derive(Debug)]
pub enum NativeStrategyRequestError {
    Routing(NativeLevelRoutingError),
    MissingOwner,
    MissingSourceBans,
    UnmatchedTargeting,
    Source(GameCommandError),
}

#[derive(Debug)]
pub enum NativeTargetingRequestSourceError {
    Routing(NativeLevelRoutingError),
    MissingOwner,
    Source(crate::squad_targeting::NativeTargetingSourceError),
}

#[derive(Debug)]
pub enum NativeLevelSourceError<E> {
    TerminalPerception(bees_sim::perception::PerceptionError),
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
/// installs the validated matchup against the owning Level's live world.
/// The selected enemy and reservation are captured by subsequent commands;
/// episode finalization settles earned TSV or discards unused selections.
#[derive(Debug)]
pub struct NativeLevelTargetingDelivery {
    pub world_index: Option<usize>,
    pub selected_enemy: Option<SquadLifetime>,
    /// Copied from the ORIGINAL targeting request before correlation
    /// consumes its hash. GetStrategy must retain that exact opponent.
    pub opponent_id: u64,
    pub receipt: TargetingReceipt,
}

#[derive(Debug)]
pub enum NativeTargetingApplicationError {
    Ranking(crate::squad_targeting::NativeTargetingError),
    Events(crate::level_events::NativeLevelEventError),
}

#[derive(Debug)]
pub enum NativeLevelTargetingReceiveError {
    Application { selection: bees_hivemind::protocol::ValidatedTargetingSelection, cause: NativeTargetingApplicationError },
    Session(HiveMindSessionError),
    Routing(NativeLevelRoutingError),
}

/// One complete recognized server reply, already decoded through the
/// source-sized wire codec and routed to the correct owner. StoreCommands
/// acknowledgement changes the connection's existing durable retry ledger;
/// the transport must persist its next checkpoint before considering that
/// change crash-safe.
#[derive(Debug)]
pub enum NativeHiveMindFrameDelivery {
    Strategy(NativeLevelStrategyDelivery),
    Targeting(NativeLevelTargetingDelivery),
    StoreCommands(StoreAcknowledgement),
}

#[derive(Debug)]
pub enum NativeHiveMindFrameError {
    Wire(HiveMindWireError),
    Strategy(NativeLevelStrategyReceiveError),
    Targeting(NativeLevelTargetingReceiveError),
    StoreCommands(bees_hivemind::protocol::HiveMindProtocolError),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLevelDriverError {
    MissingWorld,
    IndexOutOfOrder,
    AlreadyRegistered,
    UnstartedWorldRequired,
    WorldIdentityChanged,
}

/// On-demand status derived exclusively from the authoritative per-world
/// simulation, the already-owned inference sampler, and ONE HiveMind
/// connection. No background clock, independent health state, log scanning,
/// per-step history, or game/training fork is introduced.
#[derive(Clone, Copy, Debug)]
pub struct NativeRuntimeStatus {
    pub simulation: Measurements,
    pub inference: NativeInferenceMeasurements,
    /// Episode-local authored scripted mission queue work and remaining
    /// pending orders. Collected only when a status snapshot is requested.
    pub scripted_queues: crate::level_rules::NativeScriptedQueueMeasurements,
    /// Episode-local live-roster selection work. These counts reset with
    /// policy identity on world recycle; never treat them as host lifetime rates.
    pub observation_candidates: bees_sim::combat_perception_bridge::LiveShipCandidateMeasurements,
    pub total_worlds: usize,
    pub running_worlds: usize,
    pub completed_worlds: usize,
    pub aborted_worlds: usize,
    pub failed_worlds: usize,
    pub living_ships: usize,
    pub active_commands: usize,
    /// Current sum of occupied grid cells and retained bucket pools across
    /// all worlds; one shared physical grid per world, never per agent.
    pub spatial_grid_active_buckets: usize,
    pub spatial_grid_retained_buckets: usize,
    /// These are episode-local physical effects from ALL controllers,
    /// not only player buttons. They reset on episode recycling and must
    /// never be interpreted as host-lifetime counter deltas.
    pub special_scout_beacons_spawned: u64,
    pub special_fire_barge_self_detonations: u64,
    pub special_game_barge_charges_started: u64,
    pub special_game_barge_charge_hits: u64,
    pub special_game_barge_charge_completions: u64,
    /// Actual one-second player chase timer/visibility/activation work,
    /// summed over current episodes and never interpreted as a lifetime
    /// rate without accounting for world resets.
    pub player_chase: crate::level_rules::NativePlayerChaseMeasurements,
    pub player_controls: crate::level_rules::NativePlayerControlGauges,
    pub reinforcements: crate::level_rules::NativeReinforcementGauges,
    pub reinforcement_arrivals: crate::level_events::NativeReinforcementArrivals,
    /// Sum of the current episodes' authoritative vision measurements.
    /// These reset on episode turnover, so they are gauges, not host-wide
    /// monotonic counters for deriving cross-episode rates.
    pub vision: bees_sim::vision::VisionSweepCounts,
    /// Sum of current episodes' lifecycle-checked navigation completions.
    /// Resets with worlds; this is not a host-lifetime rate counter.
    pub navigation_jobs_completed: u64,
    pub navigation_jobs_waiting: usize,
    pub navigation_queue_slots: usize,
    pub queued_notifications: usize,
    pub queued_squad_retirements: usize,
    pub pending_hivemind_requests: usize,
    pub pending_store_commands: usize,
    pub pending_store_reconciliations: usize,
    /// Current episode's ordinary asteroid pool, shared between game and RL.
    /// These are gauges/cumulative episode counts, NOT global lifetime rates.
    pub full_asteroids_active: usize,
    pub full_asteroid_profiles_cached: usize,
    pub full_asteroid_profiles_created: u64,
    pub full_asteroid_profiles_reused: u64,
    pub full_asteroid_destination_draws: u64,
    pub full_asteroid_destination_exhaustions: u64,
}

/// Summary of one explicit bounded native rollout window, using the
/// same simulation, source metadata, model backend and notification sink
/// as individual fixed steps. No per-step histories are retained.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelWindowCounts {
    pub rounds: u32,
    pub world_steps: usize,
    pub policy_rows: usize,
    /// Phase measurements cover only sampled successful rounds, never
    /// all rounds or a fabricated estimate of total host wall time.
    pub sampled_rounds: u32,
    pub sampled_policy_rows: usize,
    pub sampled_world_steps: usize,
    pub sampled_observation_build_nanos: u128,
    pub sampled_inference_and_validation_nanos: u128,
    pub sampled_world_dispatch_and_step_nanos: u128,
    /// Headless model-batched rounds that used multiple physical workers.
    pub parallel_physics_rounds: u32,
    /// Rounds intentionally serialized for deterministic Beacon lifetimes.
    pub parallel_stage_id_serialized_rounds: u32,
    pub parallel_physics_max_workers: usize,
    /// Sum of real worker elapsed time, not a speedup estimate.
    pub parallel_physics_worker_nanos: u128,
    pub retirement_notifications: usize,
    pub delivered_notifications: usize,
    pub stopped_with_no_live_worlds: bool,
}

#[derive(Debug)]
pub enum NativeLevelWindowError<SourceError, BackendError, SinkError> {
    RetirementObservation { round: u32, error: NativeLevelSourceError<SourceError> },
    Step {
        round: u32,
        error: NativeBatchedStepError<NativeLevelSourceError<SourceError>, BackendError>,
    },
    Delivery {
        round: u32,
        error: NativeNotificationDeliveryError<SinkError>,
    },
    PolicyDelivery { round: u32, error: SinkError },
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

pub struct NativeTerminalObservation {
    pub ship_id: i64,
    pub group_agent: u64,
    pub binding_revision: u64,
    pub features: Vec<f32>,
}

/// Runs the REAL v26 observation writer, a genuine supplied model and the
/// single NativeLevelRules combat step. No per-tick roster copy, generated
/// action, training-only combat or hidden session state is introduced.
pub struct NativeLevelBatchDriver {
    worlds: Vec<NativeWorldAssembly>,
    inference: NativeLevelInferenceBatch,
    decisions: Vec<Decision>,
    /// Reused per-world action rows only for opted-in parallel physics.
    parallel_actions: Vec<Option<Vec<Decision>>>,
    /// Sorted indices whose source Stage identities must be minted on the
    /// owning thread, reused across model rounds.
    parallel_serial_worlds: Vec<usize>,
}

impl NativeLevelBatchDriver {
    /// Capture group observations at a controller retirement or episode end.
    pub fn policy_boundary_observations<M: NativeLevelObservationContext, A>(
        &self, index: usize, world: &World<NativeLevelState<M, A>>,
    ) -> Result<Vec<NativeTerminalObservation>, NativeLevelSourceError<M::Error>> {
        let assembly = self.worlds.get(index)
            .ok_or(NativeLevelSourceError::MissingRegisteredWorld)?;
        if assembly.id != world.id {
            return Err(NativeLevelSourceError::WorldIdentityChanged);
        }
        if assembly.revision != world.episode_revision() {
            return Err(NativeLevelSourceError::EpisodeRevisionChanged);
        }
        let metadata = &world.state.metadata;
        let combat = &world.state.combat;
        let mut perception = assembly.perception.for_policy_boundary();
        let mut writer = NativePerceptionWriter::new();
        let mut rows = Vec::new();
        for (ship_id, binding_revision) in combat.bound_policy_agent_bindings() {
            let Some(group_agent) = metadata.group_agent_for_ship(ship_id)
                .map_err(NativeLevelSourceError::Source)? else { continue };
            let inputs = metadata.inputs_for_agent(combat, ship_id)
                .map_err(NativeLevelSourceError::Source)?;
            let snapshot = perception.assemble(
                world, ship_id, inputs, |id| metadata.authored_ship_perception(id),
            ).map_err(NativeLevelSourceError::Perception)?;
            let frame = combat.policy_frame(ship_id).expect("live policy binding");
            let mut features = vec![f32::NAN; OBSERVATION_FLOATS];
            writer.write(&mut features, &snapshot, frame)
                .map_err(NativeLevelSourceError::TerminalPerception)?;
            rows.push(NativeTerminalObservation { ship_id, group_agent, binding_revision, features });
        }
        let result = world.state.events.latest_finalized_episode().map(|finalized| finalized.result);
        let [bee_side, human_side] = world.state.events.sides();
        for event in combat.events() {
            let CombatEvent::ShipDied {
                victim_id: ship_id, policy_binding_revision: Some(binding_revision), ..
            } = *event else { continue };
            let Some(ship) = combat.ship(ship_id) else { continue };
            let faction = match ship.side {
                side if side == bee_side && result.is_none_or(|r| r.bee_side_eliminated) => 1.0,
                side if side == human_side && result.is_none_or(|r| r.human_side_eliminated) => -1.0,
                _ => continue,
            };
            let Some(group_agent) = metadata.group_agent_for_ship(ship_id)
                .map_err(NativeLevelSourceError::Source)? else { continue };
            // The source keeps the eliminated side registered through the
            // final reward and supplies its inactive faction observation.
            let mut features = vec![0.0; OBSERVATION_FLOATS];
            write_inactive_observation(&mut features, faction)
                .expect("fixed-size observation and authored faction");
            rows.push(NativeTerminalObservation { ship_id, group_agent, binding_revision, features });
        }
        Ok(rows)
    }
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
            parallel_actions: Vec::new(),
            parallel_serial_worlds: Vec::new(),
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

    /// Add the owning squad's authored/failure bans at request creation only.
    /// Retransmission always uses the original connection-owned request.
    pub fn strategy_request_bans<M, A>(
        &self, simulation: &Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        owner: SquadLifetime, existing: impl IntoIterator<Item=String>,
    ) -> Result<Vec<String>, NativeStrategyRequestError>
    where M: NativeLevelRewardMetadata + NativeLevelCommandContext,
          A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let index = self.locate_live_owner(simulation, owner)
            .map_err(NativeStrategyRequestError::Routing)?
            .ok_or(NativeStrategyRequestError::MissingOwner)?;
        let events = &simulation.world(index).expect("located world").state.events;
        let roster = events.roster();
        let acting = roster.get(owner).expect("located squad");
        let bans = acting.command_bans.as_ref()
            .ok_or(NativeStrategyRequestError::MissingSourceBans)?;
        let mut names = crate::squad_composition::banned_strategy_names(bans, existing);
        // Source MakeMatchupAndGetCommand forbids ClosestFriendly if no
        // viable other squad is available. A squad already executing that
        // order counts among the source's follower total. Read the one
        // current command director and source roster, not a shadow registry.
        let mut friendlies = 0usize;
        let mut followers = 0usize;
        for &candidate in roster.source_order() {
            let other = roster.get(candidate)
                .ok_or(NativeStrategyRequestError::MissingOwner)?;
            if other.side == acting.side {
                friendlies += 1;
                followers += usize::from(
                    events.commands().current_kind(candidate) ==
                        Some(LocalCommandKind::ClosestFriendly)
                );
            }
        }
        if friendlies.saturating_sub(1) <= followers {
            names = crate::squad_composition::banned_strategy_names(
                &[LocalCommandKind::ClosestFriendly], names,
            );
        }
        let combat = &simulation.world(index).expect("located world").state.combat;
        let [bee_side, human_side] = events.sides();
        let mut dynamic = Vec::with_capacity(3);
        use bees_sim::combat_world::{
            BEEHIVE_TYPE, WARP_GATE_TYPE, is_mining_ship_type,
        };
        let has_gate = combat.ships().iter().any(|ship|
            ship.ship_type == WARP_GATE_TYPE && !ship.vitals.is_dead()
        );
        if acting.side != human_side || !has_gate ||
            acting.members.iter().all(|&id| combat.ship(id)
                .is_some_and(|ship| ship.ship_type == WARP_GATE_TYPE))
        {
            dynamic.push(LocalCommandKind::FullRetreat);
        }
        if acting.side != bee_side ||
            acting.members.iter().all(|&id| combat.ship(id)
                .is_some_and(|ship| ship.ship_type == BEEHIVE_TYPE)) ||
            !combat.has_available_beehive_healing_slot(bee_side)
        {
            dynamic.push(LocalCommandKind::Heal);
        }
        let owns_miner = acting.members.iter().any(|&id| combat.ship(id)
            .is_some_and(|ship| is_mining_ship_type(ship.ship_type)));
        let has_mining_target = combat.obstacles().iter().any(|obstacle|
            obstacle.kind == bees_sim::obstacle::ObstacleKind::MiningAsteroid &&
            obstacle.live()
        );
        if !owns_miner || !has_mining_target {
            dynamic.push(LocalCommandKind::Mining);
        }
        Ok(crate::squad_composition::banned_strategy_names(&dynamic, names))
    }

    /// Prepare the actual Unity Squad.MakeMatchupStrat request from its live
    /// World, roster, authored ShipTypeLetter and user-progress roster.
    /// Request creation remains outside the physics step and outside this
    /// driver: the host's durable connection owns the one hash namespace.
    /// The caller supplies the configured enemy side; the full Type A-X
    /// ban universe is fixed in source UserProgressData.SetShipTypes.
    pub fn targeting_request_inputs<M, A>(
        &self,
        simulation: &Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        owner: SquadLifetime,
        enemy_side: i32,
    ) -> Result<crate::squad_targeting::NativeTargetingRequestInputs, NativeTargetingRequestSourceError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let index = self.locate_live_owner(simulation, owner)
            .map_err(NativeTargetingRequestSourceError::Routing)?
            .ok_or(NativeTargetingRequestSourceError::MissingOwner)?;
        let world = simulation.world(index).expect("located live world");
        crate::squad_targeting::build_source_targeting_request(
            &world.state.combat, world.state.events.roster(),
            owner, enemy_side,
        ).map_err(NativeTargetingRequestSourceError::Source)
    }

    /// Prepare the actual Unity Squad.MakeMatchupAndGetCommand identity
    /// from the accepted source targeting reservation, not from caller
    /// strings. A selected enemy with no visible potential ship yields None
    /// and must be requeued without requesting a command.
    ///
    /// This only builds the two learning keys; dynamic command bans still
    /// belong to their source gameplay owner and must be supplied separately.
    pub fn source_strategy_matchup_inputs<M, A>(
        &self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        owner: SquadLifetime,
    ) -> Result<Option<NativeSourceStrategyRequestInputs>, NativeStrategyRequestError>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let index = self.locate_live_owner(simulation, owner)
            .map_err(NativeStrategyRequestError::Routing)?
            .ok_or(NativeStrategyRequestError::MissingOwner)?;
        let world = simulation.world_mut(index).expect("located live world");
        let state = &mut world.state;
        let selection = state.events.roster().get(owner)
            .and_then(|entry| entry.targeting)
            .ok_or(NativeStrategyRequestError::UnmatchedTargeting)?;
        if selection.enemy.is_some_and(|enemy|
            state.events.roster().get(enemy).is_none_or(|target|
                target.side == state.events.roster().get(owner)
                    .expect("verified owner").side || enemy.level_id != owner.level_id
            )
        ) {
            return Err(NativeStrategyRequestError::UnmatchedTargeting);
        }
        // Must know the actual Level.ActivateMining value before
        // emitting any server request, even when no miners are present.
        let activate_mining = state.metadata.source_mining_enabled()
            .ok_or(NativeStrategyRequestError::MissingSourceBans)?;
        let matchup = state.metadata.source_strategy_matchup(
            &mut state.combat, state.events.roster(), owner,
            selection.enemy,
        ).map_err(NativeStrategyRequestError::Source)?;
        Ok(matchup.map(|matchup| NativeSourceStrategyRequestInputs {
            matchup, activate_mining,
        }))
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
        let scripted = found.is_some_and(|index|
            simulation.world(index).is_some_and(|world|
                world.state.metadata.scripted_queue_owns(owner)
            )
        );
        if scripted {
            // Mark all existing hashes retired in the connection owner.
            // The response still validates against its original request
            // and returns the exact reservation IDs for StoreCommands.
            connection.retire_squad(owner);
        }
        let receipt = connection.receive_strategy(
            response, found.filter(|_| !scripted).map(|_| owner),
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
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let owner = connection.requested_targeting_owner(response.hash)
            .ok_or(NativeLevelTargetingReceiveError::Session(
                HiveMindSessionError::UnknownRequest,
            ))?;
        let opponent_id = connection.targeting_request_opponent(response.hash)
            .ok_or(NativeLevelTargetingReceiveError::Session(
                HiveMindSessionError::UnknownRequest,
            ))?;
        let found = self.locate_live_owner(simulation, owner)
            .map_err(NativeLevelTargetingReceiveError::Routing)?;
        flush_all_squad_retirements(simulation, connection);
        let scripted = found.is_some_and(|index|
            simulation.world(index).is_some_and(|world|
                world.state.metadata.scripted_queue_owns(owner)
            )
        );
        if scripted {
            connection.retire_squad(owner);
        }
        let receipt = connection.receive_targeting(
            response, found.filter(|_| !scripted).map(|_| owner),
        ).map_err(NativeLevelTargetingReceiveError::Session)?;
        let world_index = match receipt {
            TargetingReceipt::Retired { .. } => None,
            TargetingReceipt::Accepted { .. } =>
                Some(found.expect("validated accepted live owner")),
        };
        let mut selected_enemy = None;
        if let TargetingReceipt::Accepted { owner, selection } = &receipt {
            let selection = *selection;
            let failed = |cause| NativeLevelTargetingReceiveError::Application { selection, cause };
            let world = simulation.world_mut(world_index.expect("live accepted owner")).expect("registered world");
            let context = world.state.metadata.targeting_context().ok_or_else(|| failed(
                NativeTargetingApplicationError::Ranking(crate::squad_targeting::NativeTargetingError::MissingSourceContext)))?;
            selected_enemy = crate::squad_targeting::select_enemy_squad(
                &world.state.combat, world.state.events.roster(), world.state.events.commands(),
                world.state.metadata.targeting_ship_catalog(), context, *owner, selection,
                &mut world.random, &mut self.worlds[world_index.expect("live owner")].activation.targeting,
            ).map_err(|error| failed(NativeTargetingApplicationError::Ranking(error)))?;
            let tick = world.tick();
            world.state.events.install_targeting(tick, *owner, selection, selected_enemy)
                .map_err(|error| failed(NativeTargetingApplicationError::Events(error)))?;
            connection.retire_squad(*owner);
        }
        Ok(NativeLevelTargetingDelivery {
            world_index, selected_enemy, opponent_id, receipt,
        })
    }

    /// Complete authenticated-WebSocket JSON ingress for all supported
    /// HiveMind response types. The host socket authenticates first, then
    /// passes the exact raw frame here. No retry/ack or strategy attribution
    /// is fabricated on an unrecognized setup/settings message: the caller
    /// receives None and dispatches that frame to its separate owner.
    ///
    /// Successful StoreCommands acknowledgements must be persisted via
    /// NativeHiveMindConnection::checkpoint. Actual network I/O, auth and
    /// durable storage stay outside this shared game/worker boundary.
    pub fn receive_hivemind_frame<M, A>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        connection: &mut NativeHiveMindConnection,
        frame: &[u8],
    ) -> Result<Option<NativeHiveMindFrameDelivery>, NativeHiveMindFrameError>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        let Some(response) = decode_hivemind_response_frame(frame)
            .map_err(NativeHiveMindFrameError::Wire)? else {
            return Ok(None);
        };
        let delivery = match response {
            HiveMindServerResponse::Strategy(response) =>
                NativeHiveMindFrameDelivery::Strategy(
                    self.receive_strategy_response(
                        simulation, connection.session_mut(), &response,
                    ).map_err(NativeHiveMindFrameError::Strategy)?,
                ),
            HiveMindServerResponse::Targeting(response) =>
                NativeHiveMindFrameDelivery::Targeting(
                    self.receive_targeting_response(
                        simulation, connection.session_mut(), &response,
                    ).map_err(NativeHiveMindFrameError::Targeting)?,
                ),
            HiveMindServerResponse::StoreCommands(response) =>
                NativeHiveMindFrameDelivery::StoreCommands(
                    connection.acknowledge_store(&response)
                        .map_err(NativeHiveMindFrameError::StoreCommands)?,
                ),
        };
        Ok(Some(delivery))
    }

    /// Read a single consistent local snapshot between rollout windows.
    /// Runs in O(number of worlds), independent of fleet size; all ship,
    /// command and notification counts are already maintained by their
    /// canonical owners. Sampled durations and model counters retain their
    /// original denominators, so dashboard consumers do not fabricate
    /// average inference throughput from missing timing samples.
    pub fn runtime_status<M, A>(
        &self,
        simulation: &Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        connection: &NativeHiveMindConnection,
    ) -> Result<NativeRuntimeStatus, NativeLevelRoutingError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        if self.worlds.len() != simulation.world_count() {
            return Err(NativeLevelRoutingError::UnregisteredWorldCount);
        }
        let mut status = NativeRuntimeStatus {
            simulation: simulation.measurements(),
            inference: self.inference.measurements(),
            scripted_queues: Default::default(),
            observation_candidates: Default::default(),
            total_worlds: simulation.world_count(),
            running_worlds: 0,
            completed_worlds: 0,
            aborted_worlds: 0,
            failed_worlds: 0,
            living_ships: 0,
            active_commands: 0,
            spatial_grid_active_buckets: 0,
            spatial_grid_retained_buckets: 0,
            special_scout_beacons_spawned: 0,
            special_fire_barge_self_detonations: 0,
            special_game_barge_charges_started: 0,
            special_game_barge_charge_hits: 0,
            special_game_barge_charge_completions: 0,
            player_chase: crate::level_rules::NativePlayerChaseMeasurements::default(),
            player_controls: crate::level_rules::NativePlayerControlGauges::default(),
            reinforcements: crate::level_rules::NativeReinforcementGauges::default(),
            reinforcement_arrivals: crate::level_events::NativeReinforcementArrivals::default(),
            vision: bees_sim::vision::VisionSweepCounts::default(),
            navigation_jobs_completed: 0,
            navigation_jobs_waiting: 0,
            navigation_queue_slots: 0,
            queued_notifications: 0,
            queued_squad_retirements: 0,
            pending_hivemind_requests: connection.pending_request_count(),
            pending_store_commands: connection.pending_store_count(),
            pending_store_reconciliations:
                connection.pending_reconciliation_count(),
            full_asteroids_active: 0,
            full_asteroid_profiles_cached: 0,
            full_asteroid_profiles_created: 0,
            full_asteroid_profiles_reused: 0,
            full_asteroid_destination_draws: 0,
            full_asteroid_destination_exhaustions: 0,
        };
        for (index, slot) in self.worlds.iter().enumerate() {
            let world = simulation.world(index)
                .expect("validated synchronous world count");
            if world.id != slot.id {
                return Err(NativeLevelRoutingError::WorldIdentityChanged {
                    index,
                });
            }
            if world.episode_revision() != slot.revision {
                return Err(NativeLevelRoutingError::EpisodeRevisionChanged {
                    index,
                });
            }
            let events = &world.state.events;
            status.observation_candidates.merge(
                slot.perception.candidate_measurements(),
            );
            status.scripted_queues.merge(
                world.state.metadata.scripted_queue_measurements(
                    world.tick(), events,
                ),
            );
            status.navigation_jobs_completed += world.state.navigation_jobs_completed();
            let (waiting, queue_slots) = world.state.combat.navigation_queue_gauges();
            status.navigation_jobs_waiting += waiting;
            status.navigation_queue_slots += queue_slots;
            if events.was_aborted() {
                status.aborted_worlds += 1;
            } else if world.is_failed() || events.is_failed() {
                status.failed_worlds += 1;
            } else if events.completed() {
                status.completed_worlds += 1;
            } else {
                status.running_worlds += 1;
            }
            status.living_ships += world.state.combat.total_living_ships();
            let (occupied, retained) = world.state.combat.physics()
                .spatial_grid_bucket_usage();
            status.spatial_grid_active_buckets += occupied;
            status.spatial_grid_retained_buckets += retained;
            let policy_specials = world.state.combat.policy_special_measurements();
            let game_barges = world.state.combat.game_barge_measurements();
            status.special_scout_beacons_spawned += policy_specials.scout_beacons_spawned;
            status.special_fire_barge_self_detonations += policy_specials.fire_barge_detonations;
            status.special_game_barge_charges_started += game_barges.starts;
            status.special_game_barge_charge_hits += game_barges.hits;
            status.special_game_barge_charge_completions += game_barges.completions;
            status.player_chase.merge(
                world.state.metadata.player_chase_measurements(),
            );
            status.player_controls.merge(
                world.state.metadata.player_control_gauges(events.roster()),
            );
            status.reinforcements.merge(
                world.state.metadata.reinforcement_gauges(),
            );
            status.reinforcement_arrivals.merge(events.reinforcement_arrivals());
            status.vision.merge(&world.state.combat.vision_measurements());
            let asteroids = world.state.combat.full_asteroid_pool_measurements();
            status.full_asteroids_active += asteroids.active;
            status.full_asteroid_profiles_cached += asteroids.inactive;
            status.full_asteroid_profiles_created += asteroids.created_profiles;
            status.full_asteroid_profiles_reused += asteroids.reused_profiles;
            status.full_asteroid_destination_draws += asteroids.total_destination_draws;
            status.full_asteroid_destination_exhaustions +=
                asteroids.exhausted_destination_searches;
            status.active_commands += events.active_command_count();
            status.queued_notifications +=
                events.pending_notifications().len();
            status.queued_squad_retirements +=
                events.pending_session_retirements().len();
        }
        Ok(status)
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

    /// Same model/roster/world identity preflight for rendered and headless
    /// execution; only the final physical dispatch strategy differs.
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
        self.step_with_dispatch(simulation, backend,
            |simulation, batch, backend, mut build, decisions, _parallel_actions, _serialized_worlds| {
                step_batched_levels(simulation, batch, backend,
                    &mut build, decisions)
            })
    }

    /// Opt-in shared-model batched inference with independent physical
    /// Levels executed on CPU workers. Each Level still uses the *same*
    /// authoritative NativeLevelRules and durable notification outbox.
    pub fn step_parallel<M, A, B>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
        workers: NonZeroUsize,
    ) -> Result<NativeBatchedStepCounts,
        NativeBatchedStepError<
            NativeLevelSourceError<<M as NativeLevelObservationContext>::Error>,
            B::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext + Send,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError> + Send,
        B: NativePolicyInference,
    {
        self.step_with_dispatch(simulation, backend,
            |simulation, batch, backend, mut build, decisions, parallel_actions, serialized_worlds| {
                step_batched_levels_parallel(simulation, batch, backend,
                    &mut build, decisions, parallel_actions, serialized_worlds, workers)
            })
    }

    fn step_with_dispatch<M, A, B, D>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
        dispatch: D,
    ) -> Result<NativeBatchedStepCounts,
        NativeBatchedStepError<
            NativeLevelSourceError<<M as NativeLevelObservationContext>::Error>,
            B::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        D: FnOnce(
            &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
            &mut NativeLevelInferenceBatch, &mut B,
            &mut dyn FnMut(usize, &World<NativeLevelState<M, A>>,
                &mut NativeLevelInferenceBatch)
                -> Result<(), NativeLevelSourceError<M::Error>>,
            &mut Vec<Decision>, &mut Vec<Option<Vec<Decision>>>, &mut Vec<usize>,
        ) -> Result<NativeBatchedStepCounts,
            NativeBatchedStepError<NativeLevelSourceError<M::Error>, B::Error>>,
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
        dispatch(
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
                    let group_agent = metadata.group_agent_for_ship(ship_id)
                        .map_err(NativeLevelSourceError::Source)?;
                    let inputs = metadata.inputs_for_agent(
                        &world.state.combat, ship_id,
                    ).map_err(NativeLevelSourceError::Source)?;
                    state.perception.push_agent(
                        batch, world, ship_id, route, group_agent,
                        inputs, |id| metadata.authored_ship_perception(id),
                    ).map_err(NativeLevelSourceError::Perception)?;
                }
                Ok(())
            },
            &mut self.decisions, &mut self.parallel_actions,
            &mut self.parallel_serial_worlds,
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
        connection: &mut NativeHiveMindConnection,
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
        self.run_window_with_step(
            simulation, backend, connection, sink, max_rounds, delivery_every,
            |driver, simulation, backend| driver.step(simulation, backend),
        )
    }

    /// Model inference is common across worlds. With multiple CPU workers,
    /// only the independent authoritative physics/Level updates fan out.
    /// Notifications, PPO capture and HiveMind outcome checkpoint ordering
    /// remain single-owner and identical to the serial window.
    pub fn run_window_parallel<M, A, B, S>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
        connection: &mut NativeHiveMindConnection,
        sink: &mut S,
        max_rounds: NonZeroU32,
        delivery_every: NonZeroU32,
        workers: NonZeroUsize,
    ) -> Result<NativeLevelWindowCounts,
        NativeLevelWindowError<
            <M as NativeLevelObservationContext>::Error, B::Error, S::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext + Send,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError> + Send,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        self.run_window_with_step(
            simulation, backend, connection, sink, max_rounds, delivery_every,
            |driver, simulation, backend|
                driver.step_parallel(simulation, backend, workers),
        )
    }

    fn run_window_with_step<M, A, B, S, D>(
        &mut self,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        backend: &mut B,
        connection: &mut NativeHiveMindConnection,
        sink: &mut S,
        max_rounds: NonZeroU32,
        delivery_every: NonZeroU32,
        mut step: D,
    ) -> Result<NativeLevelWindowCounts,
        NativeLevelWindowError<
            <M as NativeLevelObservationContext>::Error, B::Error, S::Error>>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
        D: FnMut(
            &mut NativeLevelBatchDriver,
            &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
            &mut B,
        ) -> Result<NativeBatchedStepCounts,
            NativeBatchedStepError<NativeLevelSourceError<M::Error>, B::Error>>,
    {
        let mut counts = NativeLevelWindowCounts::default();
        // An earlier window may have deferred its last notification
        // delivery. Never start another inference batch over a backlog
        // that the sink has not yet durably accepted.
        counts.retirement_notifications +=
            flush_all_squad_retirements(simulation, connection.session_mut());
        counts.delivered_notifications +=
            deliver_all_level_notifications(simulation, sink)
                .map_err(|error| NativeLevelWindowError::Delivery {
                    round: 0, error,
                })?.notifications_accepted;
        for round in 1..=max_rounds.get() {
            let step = step(self, simulation, backend)
                .map_err(|error| NativeLevelWindowError::Step {
                    round, error,
                })?;
            if step.policy_agents_inferred != 0 {
                sink.accept_applied_policy_batch(&self.inference)
                    .map_err(|error| NativeLevelWindowError::PolicyDelivery { round, error })?;
            }
            if sink.captures_policy_boundaries() && step.worlds_stepped != 0 {
                for index in 0..simulation.world_count() {
                    let world = simulation.world(index).expect("registered world");
                    if world.is_failed() || world.state.events.completed() ||
                        world.state.events.was_aborted() ||
                        !world.state.combat.events().iter().any(|event| matches!(event,
                            CombatEvent::ShipDied { policy_binding_revision: Some(_), .. }))
                    {
                        continue;
                    }
                    let rows = self.policy_boundary_observations(index, world)
                        .map_err(|error| NativeLevelWindowError::RetirementObservation { round, error })?;
                    sink.accept_policy_boundary(world.id, world.episode_revision(), world.tick(), &rows)
                        .map_err(|error| NativeLevelWindowError::PolicyDelivery { round, error })?;
                }
            }
            counts.rounds += 1;
            counts.world_steps += step.worlds_stepped;
            counts.policy_rows += step.policy_agents_inferred;
            counts.parallel_stage_id_serialized_rounds +=
                u32::from(step.serialized_for_stage_ids);
            if step.parallel_physics_workers > 1 {
                counts.parallel_physics_rounds += 1;
                counts.parallel_physics_max_workers = counts.parallel_physics_max_workers
                    .max(step.parallel_physics_workers);
                counts.parallel_physics_worker_nanos +=
                    step.parallel_physics_worker_nanos;
            }
            if step.phase_timing_sampled {
                counts.sampled_rounds += 1;
                counts.sampled_policy_rows += step.policy_agents_inferred;
                counts.sampled_world_steps += step.worlds_stepped;
                counts.sampled_observation_build_nanos +=
                    step.observation_build_nanos;
                counts.sampled_inference_and_validation_nanos +=
                    step.inference_and_validation_nanos;
                counts.sampled_world_dispatch_and_step_nanos +=
                    step.world_dispatch_and_step_nanos;
            }
            if round % delivery_every.get() == 0 ||
                step.worlds_stepped == 0 ||
                round == max_rounds.get()
            {
                counts.retirement_notifications +=
                    flush_all_squad_retirements(simulation, connection.session_mut());
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
        connection: &mut NativeHiveMindConnection,
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
            simulation, connection.session_mut(),
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

    fn captures_policy_boundaries(&self) -> bool { false }

    fn accept_policy_boundary(
        &mut self, _world: WorldId, _revision: u64, _tick: u64,
        _rows: &[NativeTerminalObservation],
    ) -> Result<(), Self::Error> {
        Ok(())
    }

    /// Called only after every world in the policy batch stepped successfully,
    /// before delivering rewards and terminal records produced by that step.
    fn accept_applied_policy_batch(
        &mut self, _batch: &NativeLevelInferenceBatch,
    ) -> Result<(), Self::Error> {
        Ok(())
    }

    /// Durable command-credit sinks expose only batches that they have
    /// already committed to their own journal. The host then checkpoints
    /// each batch in the one connection-owned StoreCommands retry ledger.
    /// A sink that deliberately does not store outcomes must make that
    /// choice explicit rather than silently accepting valid credit.
    fn first_prepared_outcomes(&self) -> Option<&bees_hivemind::outcomes::PreparedOutcomeBatch>;

    /// Called only AFTER the corresponding StoreCommands payload was
    /// persisted in the connection. It must not release an unqueued batch.
    fn prepared_outcomes_queued(&mut self);

    fn accept(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        event_sequence: u64,
        notification: &NativeLevelNotification,
    ) -> Result<(), Self::Error>;

    /// Deliver a contiguous source-order prefix. The default preserves
    /// existing per-notification sink semantics. A durable batch sink may
    /// commit a group with one disk sync, but MUST return the exact number
    /// of events known committed before an error. Uncommitted events stay
    /// in the authoritative Level outbox for explicit reconciliation.
    fn accept_batch(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        first_sequence: u64,
        notifications: &[NativeLevelNotification],
    ) -> Result<(), (usize, Self::Error)> {
        for (index, notification) in notifications.iter().enumerate() {
            let sequence = first_sequence + index as u64;
            self.accept(world, episode_revision, sequence, notification)
                .map_err(|error| (index, error))?;
        }
        Ok(())
    }
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
        let (accepted, failure) = match sink.accept_batch(
            world.id, world.episode_revision(), first_sequence,
            world.state.events.pending_notifications(),
        ) {
            Ok(()) => (count, None),
            Err((committed, error)) => (committed, Some(error)),
        };
        // A sink reporting more committed events than it was offered has
        // violated its contract; the existing outbox will refuse the ACK.

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
