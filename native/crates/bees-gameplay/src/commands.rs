//! Native gameplay command execution at the HiveMind / CombatWorld boundary.
//!
//! This is NOT a second simulation implementation. The command director
//! invokes the same shared physics world and formation navigation used by
//! user orders, the render client, and headless evaluation.
//!
//! Hold, MoveToRandom, Patrol, Retreat, ClosestFriendly and Mining have
//! source-defined movement/timer lifecycles. All mining extraction uses the
//! shared physical CombatWorld, with Level-owned persistent fleet credits.
//! Other strategies remain explicitly unsupported until their gameplay
//! mechanics and reward effects are implemented. Caller-owned
//! targeting/shooting and command reward accumulation are not fabricated.

use std::collections::BTreeMap;

use bees_hivemind::dispatch::{
    resolve_hivemind_command, LocalCommandKind, ResolvedHiveMindCommand,
    SquadCommandComposition,
};
use bees_hivemind::outcomes::{
    FinalizedCommand, OutcomeSettlement,
};
use bees_hivemind::protocol::{
    HiveMindProtocolError, ValidatedStrategySelection, ValidatedTargetingSelection,
};
use bees_hivemind::session::StrategyReceipt;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::Aabb;
use bees_sim::combat_world::{CombatWorld, CombatWorldError};
use bees_sim::geometry::Vec2;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{
    issue_squad_move, issue_squad_tracked_move, set_squad_game_speed,
    stop_squad_game_movement, SquadFormationMember,
    SquadFormationOptions, SquadOrderError,
};
use bees_sim::timer::ScaledTimer;

const COMMAND_FREQUENCY_SECONDS: f32 = 3.0;
const RANDOM_POSITION_ATTEMPTS: usize = 35;
const RANDOM_MIN_DISTANCE: f32 = 32.0;
const GUARD_PADDING: f32 = 4.0;
const MINING_RATE_SECONDS: f32 = 5.0;
const MINING_COMMAND_TIMEOUT_SECONDS: f32 = 600.0;
const GUARD_SPEED_MATCH_DISTANCE_SQUARED: f32 = 1.5 * 1.5;

#[derive(Clone, Copy, Debug)]
pub struct NativeSquadOrder<'a> {
    /// Captured exact squad lifecycle (distinct from numeric entity ID).
    pub owner: SquadLifetime,
    pub members: &'a [SquadFormationMember],
    /// Unity Squad.GetPosition: world-coordinate center of live bounds.
    /// Supply the actual bounding-box center; do not average ship centers.
    pub squad_center: Vec2,
    pub formation: SquadFormationOptions,
    /// Live, command-targeted enemy squad obtained from the same Level.
    /// The native core never invents an opponent from training data.
    pub enemy: Option<NativeEnemySquad>,
}

#[derive(Clone, Copy, Debug)]
pub struct NativeEnemySquad {
    pub squad_id: i64,
    pub position: Vec2,
    pub max_range: f32,
    pub is_alive: bool,
}

impl NativeEnemySquad {
    pub fn valid(self) -> bool {
        self.squad_id > 0 && self.position.is_finite() &&
            self.max_range.is_finite() && self.max_range >= 0.0
    }
}

/// Current Level.State.Squads entry. The owner contains the full lifecycle,
/// so a pooled squad reusing an ItemId is never the previous follow target.
/// Callers provide entries in actual Level.State.Squads iteration order.
#[derive(Clone, Copy, Debug)]
pub struct NativeFriendlySquad {
    pub owner: SquadLifetime,
    pub side: i32,
    pub position: Vec2,
    pub is_alive: bool,
    /// Current Unity command kind (including player/scripted commands),
    /// derived from the live Level registry, not this director alone.
    pub active_command: Option<LocalCommandKind>,
    /// For active Guards: the exact protected squad lifecycle.
    pub guarded_squad: Option<SquadLifetime>,
    /// Live Squad.GetWidth()/GetHeight(), not an inferred point radius.
    pub width: f32,
    pub height: f32,
    /// Unity Squad.GetSlowestShipSpeed() from authored ship Speed values.
    pub slowest_ship_speed: f32,
}

impl NativeFriendlySquad {
    pub fn valid(self) -> bool {
        self.owner.valid() && self.position.is_finite() &&
        self.width.is_finite() && self.width >= 0.0 &&
        self.height.is_finite() && self.height >= 0.0 &&
        self.slowest_ship_speed.is_finite() &&
        self.slowest_ship_speed >= 0.0 &&
        match (self.active_command, self.guarded_squad) {
            (Some(LocalCommandKind::Guard), Some(target)) =>
                target.valid() && target.level_id == self.owner.level_id,
            (Some(LocalCommandKind::Guard), None) => false,
            (_, None) => true,
            (_, Some(_)) => false,
        }
    }
}

impl NativeSquadOrder<'_> {
    pub fn valid(self) -> bool {
        self.owner.valid() && self.squad_center.is_finite() &&
        self.formation.valid() && !self.members.is_empty()
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HiveMindCommandSettings {
    /// From server-loaded ConfigData.StandardMaxCommandTime.
    pub max_command_seconds: f32,
    /// From ConfigData.Configuration.AIRandomMovementMaxDistance.
    pub ai_random_max_distance: f32,
    /// From ConfigData.Configuration.AIPatrolMaxSize.
    pub ai_patrol_max_size: f32,
    /// From ConfigData.Configuration.AISquadPatrolTime.
    pub ai_squad_patrol_seconds: f32,
    /// From ConfigData.Configuration.AISquadFollowingTime.
    pub ai_squad_following_seconds: f32,
    /// From ConfigData.Configuration.AISquadGuardTime.
    pub ai_squad_guard_seconds: f32,
}

impl HiveMindCommandSettings {
    pub fn valid(self) -> bool {
        self.max_command_seconds.is_finite() &&
        self.max_command_seconds > 0.0 &&
        self.ai_random_max_distance.is_finite() &&
        self.ai_random_max_distance >= 0.0 &&
        self.ai_patrol_max_size.is_finite() &&
        self.ai_patrol_max_size >= 0.0 &&
        self.ai_squad_patrol_seconds.is_finite() &&
        self.ai_squad_patrol_seconds > 0.0 &&
        self.ai_squad_following_seconds.is_finite() &&
        self.ai_squad_following_seconds > 0.0 &&
        self.ai_squad_guard_seconds.is_finite() &&
        self.ai_squad_guard_seconds > 0.0
    }
}

#[derive(Debug)]
pub enum GameCommandError {
    InvalidSquad,
    InvalidSettings,
    InvalidDelta,
    InvalidEnemy,
    InvalidFriendlies,
    InvalidMiningTarget,
    InvalidMiningAccounting,
    Squad(SquadOrderError),
    Mining(CombatWorldError),
}

impl From<SquadOrderError> for GameCommandError {
    fn from(value: SquadOrderError) -> Self { Self::Squad(value) }
}

impl From<CombatWorldError> for GameCommandError {
    fn from(value: CombatWorldError) -> Self { Self::Mining(value) }
}

/// The only safe outcome of receiving a strategy for a dead/superseded
/// squad is explicit reservation settlement. The shared HiveMind session
/// has already checked the request hash and lifecycle identity.
#[derive(Debug)]
pub enum StrategyReceiptAction {
    Applied(Activation),
    DiscardReservations(Vec<i64>),
    /// Unity Guard bans the strategy until the next eligibility reset when
    /// there is no squad to protect. The host must update live banned strats.
    BanAndDiscardReservations {
        reservation_ids: Vec<i64>,
        command: LocalCommandKind,
    },
}

#[derive(Debug)]
pub struct StrategyReceiptFailure {
    /// Transport/client must retain the original server reservations if
    /// gameplay execution could not be installed. Do not silently lose
    /// these OutcomeIds on an error path.
    pub selection: ValidatedStrategySelection,
    pub cause: StrategyReceiptFailureCause,
}

#[derive(Debug)]
pub enum StrategyReceiptFailureCause {
    InvalidSquad(GameCommandError),
    InvalidStrategy(HiveMindProtocolError),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommandFinishCause {
    Timeout,
    ReachedRandomDestination,
    ReachedRetreatDestination,
    AlreadySafe,
    FriendlyGone,
    GuardTargetGone,
    GuardFinished,
    Superseded,
    SquadRetired,
    MiningAsteroidDestroyed,
    NoMiningShipsRemain,
}

#[derive(Clone, Debug)]
pub struct FinishedNativeCommand {
    pub owner: SquadLifetime,
    pub selection: ResolvedHiveMindCommand,
    pub cause: CommandFinishCause,
    /// Unity Mining.Mine accumulates the actual extraction as Command.Tsv.
    /// This value cannot be reconstructed from ship health or final cargo.
    pub mined_command_tsv: Option<i64>,
}

impl FinishedNativeCommand {
    /// The actual gameplay and damage-accounting layer decides whether
    /// shooting/targeting influenced the outcome. Inactive reservations
    /// are explicitly discarded; they must not gain synthetic zero TSV.
    pub fn settle(
        self,
        command_tsv: i64,
        shooting_tsv: Option<i64>,
        targeting: Option<(&ValidatedTargetingSelection, Option<i64>)>,
    ) -> FinalizedCommand {
        FinalizedCommand {
            command: OutcomeSettlement::Credit {
                outcome_id: self.selection.command_outcome_id,
                tsv: self.mined_command_tsv.unwrap_or(command_tsv),
            },
            shooting: match shooting_tsv {
                Some(tsv) => OutcomeSettlement::Credit {
                    outcome_id: self.selection.shooting_outcome_id, tsv,
                },
                None => OutcomeSettlement::Discard {
                    outcome_id: self.selection.shooting_outcome_id,
                },
            },
            targeting: targeting.map(|(selection, tsv)| match tsv {
                Some(tsv) => OutcomeSettlement::Credit {
                    outcome_id: selection.outcome_id, tsv,
                },
                None => OutcomeSettlement::Discard {
                    outcome_id: selection.outcome_id,
                },
            }),
        }
    }
}

#[derive(Debug)]
pub enum Activation {
    /// All supported movement effects applied; the caller still owns the
    /// separate shooting/targeting setup and learning-data attribution.
    Applied {
        command: LocalCommandKind,
        issued_move: bool,
        superseded: Option<FinishedNativeCommand>,
    },
    /// No side effects. The native caller MUST discard or otherwise
    /// reconcile this validated server selection's reservations.
    Unsupported(ResolvedHiveMindCommand),
    /// Unity ClosestFriendly/Guard cannot install without a valid target.
    /// No server reservation has earned learning credit.
    Unavailable(ResolvedHiveMindCommand),
}

struct PatrolRoute {
    /// Unity's destination queue starts with top-left. On each timer tick,
    /// an arrived squad rotates the head to the tail and moves to the new
    /// head, so the first immediate tick chooses top-right.
    corners: [Vec2; 4],
    current: usize,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
}

struct FollowingFriendly {
    target: SquadLifetime,
    side: i32,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    /// None = ClosestFriendly; Some(slot) = Guard. Guard's slot is assigned
    /// only once, by the number of existing guards of this specific target.
    guard_slot: Option<usize>,
}

/// One server Mining command's source state. Physics and cargo belong
/// exclusively to CombatWorld, not to this timer/movement controller.
struct MiningRun {
    target_id: i64,
    destination_offset: Vec2,
    formation: SquadFormationOptions,
    members: Vec<SquadFormationMember>,
    eligible: Vec<i64>,
    /// Encounter order, not ID order: Unity assigns mining remainders to
    /// ShipsCurrentlyMining in the order FoundAsteroid registered them.
    currently_mining: Vec<i64>,
    mining_timer: Option<ScaledTimer>,
    stop_moving_timer: Option<ScaledTimer>,
    has_found_all: bool,
    mined_tsv: i64,
}

impl MiningRun {
    fn advance(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        command_poll: &mut Option<ScaledTimer>,
        dt: f32,
    ) -> Result<(Option<CommandFinishCause>, u64), GameCommandError> {
        if world.mining_asteroid_position(self.target_id).is_none() {
            return Ok((Some(CommandFinishCause::MiningAsteroidDestroyed), 0));
        }

        // Contact is checked on the actual freshly stepped physical hull,
        // not an extra "close enough" radius or guessed collision grid.
        if !self.has_found_all {
            for &id in &self.eligible {
                if self.currently_mining.contains(&id) {
                    continue;
                }
                if world.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) &&
                    world.ship_touches_mining_asteroid(id, self.target_id) {
                    self.currently_mining.push(id);
                    if self.mining_timer.is_none() {
                        // Source FoundAsteroid starts the five-second
                        // recurring Mine timer on first actual contact.
                        self.mining_timer = Some(ScaledTimer::new(
                            MINING_RATE_SECONDS, true, true,
                        ));
                    }
                }
            }
            // Dead ships no longer block the source "all miners reached"
            // transition. It cancels movement polling after five seconds,
            // not the ongoing mining timer.
            if self.eligible.iter().all(|id|
                !world.ship(*id).is_some_and(|ship| !ship.vitals.is_dead()) ||
                self.currently_mining.contains(id)
            ) {
                self.has_found_all = true;
                self.stop_moving_timer = Some(ScaledTimer::new(
                    MINING_RATE_SECONDS, false, false,
                ));
            }
        }

        if let Some(timer) = self.mining_timer.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                // Source Mine drops dead miners but keeps miners that
                // drift outside the asteroid after first touch.
                self.currently_mining.retain(|id|
                    world.ship(*id).is_some_and(|ship|
                        !ship.vitals.is_dead() &&
                        matches!(ship.ship_type, 4 | 9)
                    )
                );
                if self.currently_mining.is_empty() {
                    return Ok((Some(CommandFinishCause::NoMiningShipsRemain), 0));
                }
                let amount = world.mine_asteroid_for_ships(
                    self.target_id, &self.currently_mining,
                )?;
                self.mined_tsv = self.mined_tsv.checked_add(i64::from(amount))
                    .ok_or(GameCommandError::InvalidMiningAccounting)?;
                timer.finish_update(due);
                if world.mining_asteroid_position(self.target_id).is_none() {
                    return Ok((Some(CommandFinishCause::MiningAsteroidDestroyed), 0));
                }
            }
        }

        let mut callbacks = 0;
        if let Some(timer) = command_poll.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                let position = world.mining_asteroid_position(self.target_id)
                    .ok_or(GameCommandError::InvalidMiningTarget)?;
                issue_squad_move(
                    world, navigation, &self.members,
                    position + self.destination_offset, self.formation,
                )?;
                timer.finish_update(due);
                callbacks += 1;
            }
        }
        if let Some(timer) = self.stop_moving_timer.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                timer.finish_update(due);
                *command_poll = None;
                self.stop_moving_timer = None;
            }
        }
        Ok((None, callbacks))
    }
}

struct ActiveNativeCommand {
    selection: ResolvedHiveMindCommand,
    // Retreat has no time limit while moving; other commands may have
    // their own one-shot timeout or delayed finalization timer.
    timeout: Option<ScaledTimer>,
    command_poll: Option<ScaledTimer>,
    patrol: Option<PatrolRoute>,
    following: Option<FollowingFriendly>,
    mining: Option<MiningRun>,
    /// Authoritative game-owned member snapshot at command issue.
    /// The Level must retire/reissue on squad composition changes.
    member_ids: Vec<i64>,
}

/// Cheap, cumulative command metrics for an experiment dashboard. Counts
/// never contain per-tick histories, allocations, or wall-clock reads.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CommandMeasurements {
    pub updates: u64,
    pub commands_examined: u64,
    pub command_timer_callbacks: u64,
    pub timeout_callbacks: u64,
    /// Sparse cumulative counters; no per-step logging or history.
    pub mining_extractions: u64,
    pub minerals_mined: u64,
    pub activated: u64,
    pub completed: u64,
    pub superseded: u64,
    pub retired: u64,
    pub peak_active: usize,
}

#[derive(Default)]
pub struct NativeSquadCommandDirector {
    /// Sorted by (level, squad, lifecycle), matching the deterministic
    /// native ordering while avoiding a per-frame key sort.
    active: BTreeMap<SquadLifetime, ActiveNativeCommand>,
    measurements: CommandMeasurements,
    /// Reused callback completions for timer-driven command updates.
    completed_scratch: Vec<(SquadLifetime, CommandFinishCause)>,
}

impl NativeSquadCommandDirector {
    pub fn new() -> Self { Self::default() }

    pub fn active_count(&self) -> usize { self.active.len() }

    /// Snapshot measurements without resetting totals. Consumers can
    /// calculate experiment/window deltas by subtracting earlier snapshots.
    pub fn measurements(&self) -> CommandMeasurements {
        self.measurements
    }

    pub fn has_active(&self, owner: SquadLifetime) -> bool {
        self.active.contains_key(&owner)
    }

    /// Connect a validated HiveMindSession response directly to the
    /// game's squad executor, retaining exact outcome identities on every
    /// successful, stale and failed path. The server-selected strategy
    /// remains distinct from its Unity composition-specific command kind.
    pub fn apply_strategy_receipt(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        receipt: StrategyReceipt,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        let (owner, enemy_squad_id, selection) = match receipt {
            StrategyReceipt::Retired { reservation_ids, .. } =>
                return Ok(StrategyReceiptAction::DiscardReservations(
                    reservation_ids,
                )),
            StrategyReceipt::Accepted { owner, enemy_squad_id, selection } =>
                (owner, enemy_squad_id, selection),
        };
        if owner != squad.owner || !squad.valid() {
            return Ok(StrategyReceiptAction::DiscardReservations(vec![
                selection.command_outcome_id,
                selection.shooting_outcome_id,
            ]));
        }
        let mut ship_types = Vec::with_capacity(squad.members.len());
        for member in squad.members {
            let Some(ship) = world.ship(member.ship_id) else {
                return Err(StrategyReceiptFailure {
                    selection,
                    cause: StrategyReceiptFailureCause::InvalidSquad(
                        GameCommandError::Squad(SquadOrderError::MissingShip),
                    ),
                });
            };
            ship_types.push(i32::from(ship.ship_type));
        }
        let composition = SquadCommandComposition::from_unity_ship_types(ship_types);
        let resolved = resolve_hivemind_command(&selection, composition)
            .map_err(|cause| StrategyReceiptFailure {
                selection: selection.clone(),
                cause: StrategyReceiptFailureCause::InvalidStrategy(cause),
            })?;
        // A strategy response may arrive after the originally targeted
        // enemy was replaced. Retreat cannot silently use a new opponent.
        if resolved.local_command == LocalCommandKind::Retreat &&
            (enemy_squad_id.is_none() ||
             squad.enemy.map(|enemy| enemy.squad_id) != enemy_squad_id) {
            return Ok(StrategyReceiptAction::DiscardReservations(vec![
                selection.command_outcome_id,
                selection.shooting_outcome_id,
            ]));
        }
        match self.activate_hivemind(
            world, navigation, random, squad, friendlies, settings, resolved,
        ) {
            Ok(Activation::Unavailable(unavailable)) => {
                let reservation_ids = vec![
                    unavailable.command_outcome_id,
                    unavailable.shooting_outcome_id,
                ];
                if matches!(unavailable.local_command,
                    LocalCommandKind::Guard | LocalCommandKind::Mining) {
                    Ok(StrategyReceiptAction::BanAndDiscardReservations {
                        reservation_ids, command: unavailable.local_command,
                    })
                } else {
                    Ok(StrategyReceiptAction::DiscardReservations(reservation_ids))
                }
            },
            Ok(activation) => Ok(StrategyReceiptAction::Applied(activation)),
            Err(cause) => Err(StrategyReceiptFailure {
                selection,
                cause: StrategyReceiptFailureCause::InvalidSquad(cause),
            }),
        }
    }

    pub fn activate_hivemind(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        selection: ResolvedHiveMindCommand,
    ) -> Result<Activation, GameCommandError> {
        if !squad.valid() {
            return Err(GameCommandError::InvalidSquad);
        }
        if !settings.valid() {
            return Err(GameCommandError::InvalidSettings);
        }

        let mut patrol = None;
        let mut following = None;
        let mut mining = None;
        let mut retreat_already_safe = false;
        let issued_move = match selection.local_command {
            LocalCommandKind::Hold => {
                stop_squad_game_movement(world, squad.members)?;
                true
            }
            LocalCommandKind::Mining => {
                // Unity Squad.GetNearestMiningAsteroid selects by distance
                // from the current live squad bounds center. An absent
                // target or no mining ships bans Mining for this strategy
                // response rather than installing a nonfunctional command.
                let Some((target_id, _)) =
                    world.nearest_mining_asteroid(squad.squad_center)
                else { return Ok(Activation::Unavailable(selection)); };
                let spread = world.mining_asteroid_spread_radius(target_id)
                    .ok_or(GameCommandError::InvalidMiningTarget)?;
                let mut eligible = Vec::new();
                for member in squad.members {
                    let ship = world.ship(member.ship_id)
                        .ok_or(GameCommandError::InvalidSquad)?;
                    if !ship.vitals.is_dead() &&
                        matches!(ship.ship_type, 4 | 9) {
                        if world.ship_fleet_identity(member.ship_id).is_none() {
                            return Err(GameCommandError::InvalidSquad);
                        }
                        eligible.push(member.ship_id);
                    }
                }
                if eligible.is_empty() {
                    return Ok(Activation::Unavailable(selection));
                }
                // Unity Random.insideUnitCircle uses a uniform disk. Use
                // world-local seeded RNG for comparable statistics, not
                // a per-process global random stream.
                let angle = next_unit(random) * std::f32::consts::TAU;
                let radius = next_unit(random).sqrt() * spread;
                mining = Some(MiningRun {
                    target_id,
                    destination_offset: Vec2::new(
                        angle.cos() * radius, angle.sin() * radius,
                    ),
                    formation: squad.formation,
                    members: squad.members.to_vec(),
                    eligible,
                    currently_mining: Vec::new(),
                    mining_timer: None,
                    stop_moving_timer: None,
                    has_found_all: false,
                    mined_tsv: 0,
                });
                // The source begins with an immediate 3-second command
                // timer callback, not an Execute-time movement command.
                false
            }
            LocalCommandKind::MoveToRandom => {
                let requested = choose_random_coordinate(
                    random, squad.squad_center, squad.formation.map_bounds,
                    settings.ai_random_max_distance, RANDOM_MIN_DISTANCE,
                );
                issue_squad_move(
                    world, navigation, squad.members,
                    requested, squad.formation,
                )?.is_some()
            }
            LocalCommandKind::Patrol => {
                let top_left = choose_random_coordinate(
                    random, squad.squad_center, squad.formation.map_bounds,
                    settings.ai_patrol_max_size, 10.0,
                );
                let bottom_right = choose_random_coordinate(
                    random, squad.squad_center, squad.formation.map_bounds,
                    settings.ai_patrol_max_size, 10.0,
                );
                patrol = Some(PatrolRoute {
                    corners: [
                        top_left,
                        Vec2::new(bottom_right.x, top_left.y),
                        bottom_right,
                        Vec2::new(top_left.x, bottom_right.y),
                    ],
                    current: 0,
                    members: squad.members.to_vec(),
                    formation: squad.formation,
                });
                // Patrol.Execute installs a timer; the first movement occurs
                // on its immediate callback, not during Execute itself.
                false
            }
            LocalCommandKind::ClosestFriendly => {
                let mut side = None;
                for member in squad.members {
                    // This command does not move until its first timer tick;
                    // preflight the current squad before reserving it.
                    world.validate_ship_game_order(member.ship_id)
                        .map_err(|err| GameCommandError::Squad(
                            SquadOrderError::Ship(err),
                        ))?;
                    let ship = world.ship(member.ship_id)
                        .ok_or(GameCommandError::InvalidSquad)?;
                    if side.is_some_and(|existing| existing != ship.side) {
                        return Err(GameCommandError::InvalidSquad);
                    }
                    side = Some(ship.side);
                }
                let side = side.ok_or(GameCommandError::InvalidSquad)?;
                let mut nearest = None;
                let mut best_distance = f32::MAX;
                for &candidate in friendlies {
                    if !candidate.valid() {
                        return Err(GameCommandError::InvalidFriendlies);
                    }
                    if candidate.owner.level_id != squad.owner.level_id ||
                        candidate.owner == squad.owner || candidate.side != side ||
                        !candidate.is_alive ||
                        candidate.active_command == Some(LocalCommandKind::ClosestFriendly) {
                        continue;
                    }
                    let distance = (candidate.position - squad.squad_center)
                        .length_squared().sqrt();
                    if distance < best_distance {
                        best_distance = distance;
                        nearest = Some(candidate);
                    }
                }
                let Some(nearest) = nearest else {
                    return Ok(Activation::Unavailable(selection));
                };
                following = Some(FollowingFriendly {
                    target: nearest.owner, side,
                    members: squad.members.to_vec(),
                    formation: squad.formation,
                    guard_slot: None,
                });
                // Unity ClosestFriendly.Execute schedules its first
                // MoveTracked callback after the first three-second delay.
                false
            }
            LocalCommandKind::Guard => {
                // The Level supplies all current squads in its registry
                // order. Guard excludes only squads already guarding another
                // squad, not squads running ClosestFriendly.
                let mut side = None;
                for member in squad.members {
                    world.validate_ship_game_order(member.ship_id)
                        .map_err(|err| GameCommandError::Squad(
                            SquadOrderError::Ship(err),
                        ))?;
                    let ship = world.ship(member.ship_id)
                        .ok_or(GameCommandError::InvalidSquad)?;
                    if side.is_some_and(|existing| existing != ship.side) {
                        return Err(GameCommandError::InvalidSquad);
                    }
                    side = Some(ship.side);
                }
                let side = side.ok_or(GameCommandError::InvalidSquad)?;
                let self_entry = lookup_friendly(friendlies, squad.owner)?
                    .filter(|entry| entry.is_alive && entry.side == side)
                    .ok_or(GameCommandError::InvalidFriendlies)?;
                // Guard uses the same Squad.GetPosition as ClosestFriendly.
                // A target tied on distance retains Level registry order.
                let mut nearest = None;
                let mut best_distance = f32::MAX;
                for &candidate in friendlies {
                    if !candidate.valid() {
                        return Err(GameCommandError::InvalidFriendlies);
                    }
                    if candidate.owner.level_id != squad.owner.level_id ||
                        candidate.owner == squad.owner || candidate.side != side ||
                        !candidate.is_alive ||
                        candidate.active_command == Some(LocalCommandKind::Guard) {
                        continue;
                    }
                    let distance = (candidate.position - squad.squad_center)
                        .length_squared().sqrt();
                    if nearest.is_none() || distance < best_distance {
                        nearest = Some(candidate);
                        best_distance = distance;
                    }
                }
                let Some(nearest) = nearest else {
                    return Ok(Activation::Unavailable(selection));
                };
                if !self_entry.valid() {
                    return Err(GameCommandError::InvalidFriendlies);
                }
                // Unity Guard.Execute links each existing Guard of the
                // chosen target, then assigns OtherGuardSquads.Count % 4.
                // Active Level registry entries already own those links:
                // no secondary mutable registry is necessary.
                let other_guards = friendlies.iter().filter(|candidate|
                    candidate.owner.level_id == squad.owner.level_id &&
                    candidate.owner != squad.owner &&
                    candidate.side == side && candidate.is_alive &&
                    candidate.active_command == Some(LocalCommandKind::Guard) &&
                    candidate.guarded_squad == Some(nearest.owner)
                ).count();
                following = Some(FollowingFriendly {
                    target: nearest.owner, side,
                    members: squad.members.to_vec(),
                    formation: squad.formation,
                    guard_slot: Some(other_guards % 4),
                });
                // Guard's first movement is its immediate scaled callback,
                // not Execute itself.
                false
            }
            LocalCommandKind::Retreat => {
                let enemy = squad.enemy.filter(|enemy| enemy.valid() && enemy.is_alive)
                    .ok_or(GameCommandError::InvalidEnemy)?;
                let delta = squad.squad_center - enemy.position;
                let distance = delta.length_squared().sqrt();
                let ideal_distance = enemy.max_range * 2.0;
                if !distance.is_finite() || !ideal_distance.is_finite() {
                    return Err(GameCommandError::InvalidEnemy);
                }
                if distance < ideal_distance {
                    // Squad.AngleToPoint uses atan2(dx,dy), so the retreat
                    // ray is measured FROM enemy toward the squad.
                    let angle = delta.x.atan2(delta.y);
                    let retreat_point = squad.squad_center +
                        Vec2::new(angle.sin(), angle.cos()) *
                            (ideal_distance - distance);
                    issue_squad_move(
                        world, navigation, squad.members,
                        retreat_point, squad.formation,
                    )?.is_some()
                } else {
                    // Retreat already has enough separation: stop now, then
                    // finalize through its 3-second delayed timer.
                    stop_squad_game_movement(world, squad.members)?;
                    retreat_already_safe = true;
                    true
                }
            }
            _ => return Ok(Activation::Unsupported(selection)),
        };

        // Native world action above has succeeded. Only now can this
        // command supersede an earlier one; a failed order leaves the
        // existing HiveMind command and its reserved IDs intact.
        let timeout_seconds = match selection.local_command {
            LocalCommandKind::Patrol => Some(settings.ai_squad_patrol_seconds),
            LocalCommandKind::ClosestFriendly =>
                Some(settings.ai_squad_following_seconds),
            LocalCommandKind::Guard => Some(settings.ai_squad_guard_seconds),
            LocalCommandKind::Retreat if retreat_already_safe => Some(3.0),
            LocalCommandKind::Retreat => None,
            LocalCommandKind::Mining => Some(MINING_COMMAND_TIMEOUT_SECONDS),
            _ => Some(settings.max_command_seconds),
        };
        let active = ActiveNativeCommand {
            timeout: timeout_seconds.map(|seconds|
                ScaledTimer::new(seconds, false, false)),
            command_poll: if matches!(
                selection.local_command,
                LocalCommandKind::MoveToRandom | LocalCommandKind::Patrol |
                LocalCommandKind::Mining | LocalCommandKind::ClosestFriendly |
                LocalCommandKind::Guard
            ) || (selection.local_command == LocalCommandKind::Retreat &&
                !retreat_already_safe) {
                Some(ScaledTimer::new(
                    COMMAND_FREQUENCY_SECONDS, true,
                    selection.local_command != LocalCommandKind::ClosestFriendly,
                ))
            } else { None },
            patrol,
            following,
            mining,
            member_ids: squad.members.iter().map(|m| m.ship_id).collect(),
            selection,
        };
        // Guard's SetFinalize resets every live ship to its authored speed.
        // Do this after successful activation, before replacing the prior
        // owner, so a failed new order retains its previous reservations.
        if let Some(previous_guard) = self.active.get(&squad.owner)
            .and_then(|prior| prior.following.as_ref())
            .filter(|follow| follow.guard_slot.is_some()) {
            restore_guard_speed(world, previous_guard)?;
        }
        let superseded = self.active.insert(squad.owner, active).map(|old| {
            FinishedNativeCommand {
                owner: squad.owner, selection: old.selection,
                cause: CommandFinishCause::Superseded,
                mined_command_tsv: old.mining.as_ref().map(|mining| mining.mined_tsv),
            }
        });
        self.measurements.activated += 1;
        self.measurements.superseded += u64::from(superseded.is_some());
        self.measurements.peak_active =
            self.measurements.peak_active.max(self.active.len());
        Ok(Activation::Applied {
            command: self.active[&squad.owner].selection.local_command,
            issued_move, superseded,
        })
    }

    /// Called in the owning Level's scaled update (Unity timers do NOT run
    /// only at PPO decision times). The caller subsequently settles each
    /// FinishedNativeCommand with real TSV and explicit unused reserves.
    pub fn advance(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        dt: f32,
    ) -> Result<Vec<FinishedNativeCommand>, GameCommandError> {
        let mut out = Vec::new();
        self.advance_into(world, navigation, friendlies, dt, &mut out)?;
        Ok(out)
    }

    /// Preferred high-throughput Level path. Reuses both the timer
    /// completion workspace and the caller's finished-command buffer.
    /// Every command still executes against CombatWorld's real movement.
    pub fn advance_into(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        dt: f32,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), GameCommandError> {
        if !dt.is_finite() || dt < 0.0 {
            return Err(GameCommandError::InvalidDelta);
        }
        finished.clear();
        self.completed_scratch.clear();
        self.measurements.updates += 1;
        if self.active.is_empty() {
            return Ok(());
        }
        // BTreeMap stores exact lifecycle order. Iteration is linear
        // and stable; completion storage is retained between ticks.
        self.measurements.commands_examined += self.active.len() as u64;
        for (&owner, command) in self.active.iter_mut() {
            if let Some(mining) = command.mining.as_mut() {
                let before_mined = mining.mined_tsv;
                let (mut finished, polls) = mining.advance(
                    world, navigation, &mut command.command_poll, dt,
                )?;
                self.measurements.command_timer_callbacks += polls;
                let extracted = mining.mined_tsv.saturating_sub(before_mined);
                if extracted > 0 {
                    self.measurements.mining_extractions += 1;
                    self.measurements.minerals_mined += extracted as u64;
                }
                if finished.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            finished = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = finished {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            let mut finished_by_poll = false;
            // Unity registers the recurring command timer before its
            // timeout: on a shared due frame, command completion wins.
            if let Some(timer) = command.command_poll.as_mut() {
                if let Some(due) = timer.begin_update(dt) {
                    self.measurements.command_timer_callbacks += 1;
                    if let Some(follow) = command.following.as_ref() {
                        let target = lookup_friendly(friendlies, follow.target)?;
                        if let Some(friend) = target.filter(|candidate|
                            candidate.is_alive && candidate.side == follow.side
                        ) {
                            if let Some(slot) = follow.guard_slot {
                                // Guard.Timer follows the protected squad's
                                // live bounds center plus the guarding squad's
                                // *current* width or height and 4 units.
                                let own = lookup_friendly(friendlies, owner)?
                                    .filter(|entry| entry.is_alive &&
                                        entry.side == follow.side)
                                    .ok_or(GameCommandError::InvalidFriendlies)?;
                                let mut point = friend.position;
                                match slot {
                                    0 => point.y += own.height + GUARD_PADDING,
                                    1 => point.x -= own.width + GUARD_PADDING,
                                    2 => point.x += own.width + GUARD_PADDING,
                                    3 => point.y -= own.height + GUARD_PADDING,
                                    _ => return Err(GameCommandError::InvalidFriendlies),
                                }
                                issue_squad_tracked_move(
                                    world, navigation, &follow.members,
                                    point, follow.formation,
                                )?;
                                if (point - own.position).length_squared() <
                                    GUARD_SPEED_MATCH_DISTANCE_SQUARED {
                                    // Squad.SetSquadSpeed is clamped per ship
                                    // to immutable Ship.Speed by CombatWorld.
                                    set_squad_game_speed(
                                        world, &follow.members,
                                        friend.slowest_ship_speed,
                                    )?;
                                }
                            } else {
                                issue_squad_tracked_move(
                                    world, navigation, &follow.members,
                                    friend.position, follow.formation,
                                )?;
                            }
                        } else {
                            let cause = if follow.guard_slot.is_some() {
                                CommandFinishCause::GuardTargetGone
                            } else {
                                CommandFinishCause::FriendlyGone
                            };
                            self.completed_scratch.push((owner, cause));
                            finished_by_poll = true;
                        }
                    } else {
                        // Unity Ship.HasReachedDestination means exactly
                        // !HasTargetCoordinates (not "velocity nearly zero").
                        let all_arrived = command.member_ids.iter().all(|id| {
                            world.ship_body(*id)
                                .is_some_and(|ship| ship.active &&
                                    !ship.motion.has_target_coordinates)
                        });
                        if all_arrived {
                            if let Some(route) = command.patrol.as_mut() {
                                // Rotate before moving, matching Unity Patrol.
                                // A blocked formation still advances the route.
                                let next = (route.current + 1) % route.corners.len();
                                issue_squad_move(
                                    world, navigation, &route.members,
                                    route.corners[next], route.formation,
                                )?;
                                route.current = next;
                            } else {
                                let cause = if command.selection.local_command ==
                                    LocalCommandKind::Retreat {
                                    CommandFinishCause::ReachedRetreatDestination
                                } else {
                                    CommandFinishCause::ReachedRandomDestination
                                };
                                self.completed_scratch.push((owner, cause));
                                finished_by_poll = true;
                            }
                        }
                    }
                    timer.finish_update(due);
                }
            }
            if !finished_by_poll {
                if let Some(timeout) = command.timeout.as_mut() {
                    if let Some(due) = timeout.begin_update(dt) {
                        self.measurements.timeout_callbacks += 1;
                        timeout.finish_update(due);
                        let cause = match command.selection.local_command {
                            LocalCommandKind::Retreat => CommandFinishCause::AlreadySafe,
                            LocalCommandKind::Guard => CommandFinishCause::GuardFinished,
                            _ => CommandFinishCause::Timeout,
                        };
                        self.completed_scratch.push((owner, cause));
                    }
                }
            }
        }
        self.completed_scratch.sort_unstable_by_key(|(owner, _)| (
            owner.level_id, owner.squad_id, owner.lifecycle_id,
        ));
        for (owner, cause) in self.completed_scratch.drain(..) {
            // Source Guard.SetFinalize must release its speed override,
            // regardless of whether it ended normally or its target died.
            if let Some(guard) = self.active.get(&owner)
                .and_then(|active| active.following.as_ref())
                .filter(|follow| follow.guard_slot.is_some()) {
                restore_guard_speed(world, guard)?;
            }
            if let Some(active) = self.active.remove(&owner) {
                finished.push(FinishedNativeCommand {
                    owner, selection: active.selection, cause,
                    mined_command_tsv: active.mining.as_ref()
                        .map(|mining| mining.mined_tsv),
                });
            }
        }
        self.measurements.completed += finished.len() as u64;
        Ok(())
    }

    /// Unity command finalization queues a fresh HiveMind request when the
    /// squad is still eligible; that request scheduling belongs to the
    /// caller, because it is bound to the live authenticated WebSocket.
    pub fn retire_squad(
        &mut self,
        world: &mut CombatWorld,
        owner: SquadLifetime,
    ) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
        // On squad death, missing/dead members are skipped, exactly as
        // Unity Guard.SetFinalize does not reset a dead squad's speed.
        if let Some(guard) = self.active.get(&owner)
            .and_then(|active| active.following.as_ref())
            .filter(|follow| follow.guard_slot.is_some()) {
            restore_guard_speed(world, guard)?;
        }
        let retired = self.active.remove(&owner).map(|active| FinishedNativeCommand {
            owner, selection: active.selection,
            cause: CommandFinishCause::SquadRetired,
            mined_command_tsv: active.mining.as_ref()
                .map(|mining| mining.mined_tsv),
        });
        self.measurements.retired += u64::from(retired.is_some());
        Ok(retired)
    }
}

/// Unity Guard.SetFinalize resets surviving members to Squad.MaxSpeed,
/// which is the highest *authored* Speed among the current live members.
fn restore_guard_speed(
    world: &mut CombatWorld,
    guard: &FollowingFriendly,
) -> Result<(), GameCommandError> {
    let mut live = Vec::new();
    let mut fastest = 0.0_f32;
    for &member in &guard.members {
        let alive = world.ship(member.ship_id)
            .is_some_and(|ship| !ship.vitals.is_dead());
        let body = world.ship_body(member.ship_id);
        if alive {
            if let Some(body) = body.filter(|body| body.active) {
                if !body.motion.base_speed.is_finite() ||
                    body.motion.base_speed < 0.0 {
                    return Err(GameCommandError::InvalidSquad);
                }
                fastest = fastest.max(body.motion.base_speed);
                live.push(member);
            }
        }
    }
    if !live.is_empty() {
        set_squad_game_speed(world, &live, fastest)?;
    }
    Ok(())
}

/// A missing or duplicated current squad must not be treated as a new
/// squad with the same numeric ID. All accesses use exact lifecycle owners.
fn lookup_friendly(
    friendlies: &[NativeFriendlySquad],
    owner: SquadLifetime,
) -> Result<Option<NativeFriendlySquad>, GameCommandError> {
    let mut matches = friendlies.iter()
        .filter(|candidate| candidate.owner == owner);
    let result = matches.next().copied();
    if matches.next().is_some() ||
        result.is_some_and(|candidate| !candidate.valid()) {
        return Err(GameCommandError::InvalidFriendlies);
    }
    Ok(result)
}

/// Utilities.RandomCoordinate(Level, Squad.GetPosition(), range, minimum):
/// up to 35 attempts; a final out-of-bounds sample is returned as-is and
/// Squad.Move will apply Level.ForceBounds. Random draws deliberately use
/// the world-local RandomStream instead of an unrelated global RNG.
/// This preserves gameplay sampling distribution and sequence ownership,
/// but does not claim bit-exact System.Random state compatibility.
fn choose_random_coordinate(
    random: &mut RandomStream,
    position: Vec2,
    bounds: Aabb,
    max_distance: f32,
    min_distance: f32,
) -> Vec2 {
    let mut location = Vec2::ZERO;
    for _ in 0..RANDOM_POSITION_ATTEMPTS {
        let x = position.x +
            (next_unit(random) * max_distance + min_distance) *
                next_sign(random);
        let y = position.y +
            (next_unit(random) * max_distance + min_distance) *
                next_sign(random);
        location = Vec2::new(x, y);
        if location != Vec2::ZERO &&
            x > bounds.min.x && x < bounds.max.x &&
            y > bounds.min.y && y < bounds.max.y {
            break;
        }
    }
    location
}

fn next_unit(random: &mut RandomStream) -> f32 {
    let high = random.next_u64() >> 11;
    (high as f64 / ((1_u64 << 53) as f64)) as f32
}

fn next_sign(random: &mut RandomStream) -> f32 {
    if random.next_below(2) != 0 { 1.0 } else { -1.0 }
}
