//! Native gameplay command execution at the HiveMind / CombatWorld boundary.
//!
//! This is NOT a second simulation implementation. The command director
//! invokes the same shared physics world and formation navigation used by
//! user orders, the render client, and headless evaluation.
//!
//! Hold, MoveToRandom, Patrol, Retreat and ClosestFriendly have source-matched
//! movement/timer phases.
//! Every other server strategy is explicitly unsupported until its Unity
//! gameplay mechanics and reward effects are implemented. Caller-owned
//! targeting/shooting and command reward accumulation are not fabricated.

use std::collections::HashMap;

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
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{
    issue_squad_move, issue_squad_tracked_move, stop_squad_game_movement,
    SquadFormationMember,
    SquadFormationOptions, SquadOrderError,
};
use bees_sim::timer::ScaledTimer;

const COMMAND_FREQUENCY_SECONDS: f32 = 3.0;
const RANDOM_POSITION_ATTEMPTS: usize = 35;
const RANDOM_MIN_DISTANCE: f32 = 32.0;

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
    pub is_following_friend: bool,
}

impl NativeFriendlySquad {
    pub fn valid(self) -> bool {
        self.owner.valid() && self.position.is_finite()
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
        self.ai_squad_following_seconds > 0.0
    }
}

#[derive(Debug)]
pub enum GameCommandError {
    InvalidSquad,
    InvalidSettings,
    InvalidDelta,
    InvalidEnemy,
    InvalidFriendlies,
    Squad(SquadOrderError),
}

impl From<SquadOrderError> for GameCommandError {
    fn from(value: SquadOrderError) -> Self { Self::Squad(value) }
}

/// The only safe outcome of receiving a strategy for a dead/superseded
/// squad is explicit reservation settlement. The shared HiveMind session
/// has already checked the request hash and lifecycle identity.
#[derive(Debug)]
pub enum StrategyReceiptAction {
    Applied(Activation),
    DiscardReservations(Vec<i64>),
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
    Superseded,
    SquadRetired,
}

#[derive(Clone, Debug)]
pub struct FinishedNativeCommand {
    pub owner: SquadLifetime,
    pub selection: ResolvedHiveMindCommand,
    pub cause: CommandFinishCause,
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
                tsv: command_tsv,
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
    /// Unity ClosestFriendly.Execute cannot install a command without an
    /// eligible target. No server reservation has earned learning credit.
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
}

struct ActiveNativeCommand {
    selection: ResolvedHiveMindCommand,
    // Retreat has no time limit while moving; other commands may have
    // their own one-shot timeout or delayed finalization timer.
    timeout: Option<ScaledTimer>,
    command_poll: Option<ScaledTimer>,
    patrol: Option<PatrolRoute>,
    following: Option<FollowingFriendly>,
    /// Authoritative game-owned member snapshot at command issue.
    /// The Level must retire/reissue on squad composition changes.
    member_ids: Vec<i64>,
}

#[derive(Default)]
pub struct NativeSquadCommandDirector {
    active: HashMap<SquadLifetime, ActiveNativeCommand>,
}

impl NativeSquadCommandDirector {
    pub fn new() -> Self { Self::default() }

    pub fn active_count(&self) -> usize { self.active.len() }

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
            let Some(ship) = world.ships().iter()
                .find(|ship| ship.entity_id == member.ship_id) else {
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
            Ok(Activation::Unavailable(unavailable)) =>
                Ok(StrategyReceiptAction::DiscardReservations(vec![
                    unavailable.command_outcome_id,
                    unavailable.shooting_outcome_id,
                ])),
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
        let mut retreat_already_safe = false;
        let issued_move = match selection.local_command {
            LocalCommandKind::Hold => {
                stop_squad_game_movement(world, squad.members)?;
                true
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
                    let ship = world.ships().iter().find(
                        |ship| ship.entity_id == member.ship_id,
                    ).ok_or(GameCommandError::InvalidSquad)?;
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
                        !candidate.is_alive || candidate.is_following_friend {
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
                });
                // Unity ClosestFriendly.Execute schedules its first
                // MoveTracked callback after the first three-second delay.
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
            LocalCommandKind::Retreat if retreat_already_safe => Some(3.0),
            LocalCommandKind::Retreat => None,
            _ => Some(settings.max_command_seconds),
        };
        let active = ActiveNativeCommand {
            timeout: timeout_seconds.map(|seconds|
                ScaledTimer::new(seconds, false, false)),
            command_poll: if matches!(
                selection.local_command,
                LocalCommandKind::MoveToRandom | LocalCommandKind::Patrol |
                LocalCommandKind::ClosestFriendly
            ) || (selection.local_command == LocalCommandKind::Retreat &&
                !retreat_already_safe) {
                Some(ScaledTimer::new(
                    COMMAND_FREQUENCY_SECONDS, true,
                    selection.local_command != LocalCommandKind::ClosestFriendly,
                ))
            } else { None },
            patrol,
            following,
            member_ids: squad.members.iter().map(|m| m.ship_id).collect(),
            selection,
        };
        let superseded = self.active.insert(squad.owner, active).map(|old| {
            FinishedNativeCommand {
                owner: squad.owner, selection: old.selection,
                cause: CommandFinishCause::Superseded,
            }
        });
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
        if !dt.is_finite() || dt < 0.0 {
            return Err(GameCommandError::InvalidDelta);
        }
        let mut completed = Vec::new();
        // Stable order matters when several squads issue movement on the
        // same fixed frame. HashMap iteration is intentionally not used.
        let mut owners: Vec<_> = self.active.keys().copied().collect();
        owners.sort_unstable_by_key(|owner| (
            owner.level_id, owner.squad_id, owner.lifecycle_id,
        ));
        for owner in owners {
            let command = self.active.get_mut(&owner).expect("active owner");
            let mut finished_by_poll = false;
            // Unity registers the recurring command timer before its
            // timeout: on a shared due frame, command completion wins.
            if let Some(timer) = command.command_poll.as_mut() {
                if let Some(due) = timer.begin_update(dt) {
                    if let Some(follow) = command.following.as_ref() {
                        let mut matching = friendlies.iter()
                            .filter(|candidate| candidate.owner == follow.target);
                        let target = matching.next();
                        if matching.next().is_some() {
                            return Err(GameCommandError::InvalidFriendlies);
                        }
                        if let Some(friend) = target.filter(|candidate|
                            candidate.valid() && candidate.is_alive &&
                            candidate.side == follow.side
                        ) {
                            issue_squad_tracked_move(
                                world, navigation, &follow.members,
                                friend.position, follow.formation,
                            )?;
                        } else {
                            completed.push((owner, CommandFinishCause::FriendlyGone));
                            finished_by_poll = true;
                        }
                    } else {
                        // Unity Ship.HasReachedDestination means exactly
                        // !HasTargetCoordinates (not "velocity nearly zero").
                        let all_arrived = command.member_ids.iter().all(|id| {
                            world.physics().ships().iter()
                                .find(|ship| ship.id == *id)
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
                                completed.push((owner, cause));
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
                        timeout.finish_update(due);
                        let cause = if command.selection.local_command ==
                            LocalCommandKind::Retreat {
                            CommandFinishCause::AlreadySafe
                        } else {
                            CommandFinishCause::Timeout
                        };
                        completed.push((owner, cause));
                    }
                }
            }
        }
        completed.sort_unstable_by_key(|(owner, _)| (
            owner.level_id, owner.squad_id, owner.lifecycle_id,
        ));
        Ok(completed.into_iter().filter_map(|(owner, cause)| {
            self.active.remove(&owner).map(|active| FinishedNativeCommand {
                owner, selection: active.selection, cause,
            })
        }).collect())
    }

    /// Unity command finalization queues a fresh HiveMind request when the
    /// squad is still eligible; that request scheduling belongs to the
    /// caller, because it is bound to the live authenticated WebSocket.
    pub fn retire_squad(
        &mut self, owner: SquadLifetime,
    ) -> Option<FinishedNativeCommand> {
        self.active.remove(&owner).map(|active| FinishedNativeCommand {
            owner, selection: active.selection,
            cause: CommandFinishCause::SquadRetired,
        })
    }
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
