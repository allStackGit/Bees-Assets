//! Native gameplay command execution at the HiveMind / CombatWorld boundary.
//!
//! This is NOT a second simulation implementation. The command director
//! invokes the same shared physics world and formation navigation used by
//! user orders, the render client, and headless evaluation.
//!
//! Hold, MoveToRandom and Patrol have their movement/timer phases ported here.
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
    issue_squad_move, stop_squad_game_movement, SquadFormationMember,
    SquadFormationOptions, SquadOrderError,
};
use bees_sim::timer::ScaledTimer;

const RANDOM_COMMAND_POLL_SECONDS: f32 = 3.0;
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
        self.ai_squad_patrol_seconds > 0.0
    }
}

#[derive(Debug)]
pub enum GameCommandError {
    InvalidSquad,
    InvalidSettings,
    InvalidDelta,
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

struct ActiveNativeCommand {
    selection: ResolvedHiveMindCommand,
    timeout: ScaledTimer,
    command_poll: Option<ScaledTimer>,
    patrol: Option<PatrolRoute>,
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
        settings: HiveMindCommandSettings,
        receipt: StrategyReceipt,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        let (owner, selection) = match receipt {
            StrategyReceipt::Retired { reservation_ids, .. } =>
                return Ok(StrategyReceiptAction::DiscardReservations(
                    reservation_ids,
                )),
            StrategyReceipt::Accepted { owner, selection, .. } =>
                (owner, selection),
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
        self.activate_hivemind(
            world, navigation, random, squad, settings, resolved,
        ).map(StrategyReceiptAction::Applied)
            .map_err(|cause| StrategyReceiptFailure {
                selection,
                cause: StrategyReceiptFailureCause::InvalidSquad(cause),
            })
    }

    pub fn activate_hivemind(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
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
            _ => return Ok(Activation::Unsupported(selection)),
        };

        // Native world action above has succeeded. Only now can this
        // command supersede an earlier one; a failed order leaves the
        // existing HiveMind command and its reserved IDs intact.
        let timeout_seconds = if selection.local_command == LocalCommandKind::Patrol {
            settings.ai_squad_patrol_seconds
        } else {
            settings.max_command_seconds
        };
        let active = ActiveNativeCommand {
            timeout: ScaledTimer::new(timeout_seconds, false, false),
            command_poll: if matches!(
                selection.local_command,
                LocalCommandKind::MoveToRandom | LocalCommandKind::Patrol
            ) {
                Some(ScaledTimer::new(RANDOM_COMMAND_POLL_SECONDS, true, true))
            } else { None },
            patrol,
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
        dt: f32,
    ) -> Result<Vec<FinishedNativeCommand>, GameCommandError> {
        if !dt.is_finite() || dt < 0.0 {
            return Err(GameCommandError::InvalidDelta);
        }
        let mut completed = Vec::new();
        for (&owner, command) in &mut self.active {
            if let Some(due) = command.timeout.begin_update(dt) {
                command.timeout.finish_update(due);
                completed.push((owner, CommandFinishCause::Timeout));
                continue;
            }
            if let Some(timer) = command.command_poll.as_mut() {
                if let Some(due) = timer.begin_update(dt) {
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
                            // Patrol rotates the queue before calling Move;
                            // a blocked move still advances the queue.
                            let next = (route.current + 1) % route.corners.len();
                            issue_squad_move(
                                world, navigation, &route.members,
                                route.corners[next], route.formation,
                            )?;
                            route.current = next;
                        } else {
                            completed.push((
                                owner, CommandFinishCause::ReachedRandomDestination,
                            ));
                        }
                    }
                    timer.finish_update(due);
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

/// Utilities.RandomCoordinate(Level, Squad.GetPosition(), range, 32):
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
