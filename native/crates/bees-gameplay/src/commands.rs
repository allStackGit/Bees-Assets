//! Native gameplay command execution at the HiveMind / CombatWorld boundary.
//!
//! This is NOT a second simulation implementation. The command director
//! invokes the same shared physics world and formation navigation used by
//! user orders, the render client, and headless evaluation.
//!
//! The authored movement and timer phases share one CombatWorld across
//! ordinary squad orders, resource commands and attacking strategies.
//! Striker BombingRun and Barge Charge use the shared combat world and
//! require authentic Level-supplied carrier, contact and weapon inputs.
//! Missing authored inputs fail closed; unsupported commands are not credited.
//! Fleet credits remain Level-owned, and source shooting/targeting rewards
//! are never fabricated by the command director.

use std::collections::{BTreeMap, HashMap};

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
use crate::squads::{NativeSquadEntry, NativeSquadRoster};
use crate::squad_geometry::SquadMemberOffset;

mod mining;
mod identity;
pub use identity::{NativeCommandSelection, NativeCommandOrigin,
    NativeLocalCommandIdentity, NativeLocalCommandSource, NativeLocalCommandAction};
mod heal;
mod scouting;
mod full_retreat;
mod circle;
mod swipe;
mod targeting;
mod in_and_out;
mod aggressive;
mod bombing;
mod charge;
pub use self::charge::NativeChargeTimerSnapshot;
use self::charge::NativeChargeRun;
pub use self::bombing::{
    NativeBombWeapon, NativeBombingActivation, NativeBombingTimerSnapshot,
    NativeBombingRun, assign_initial_bombing_run_targets,
};
use self::bombing::defenseless_carrier_strikers;
pub use self::aggressive::{
    NativeAggressiveCommandSnapshot, assemble_aggressive_snapshot_from_live_weapons,
};
use self::aggressive::AggressiveRun;
pub use self::targeting::{
    NativeCommandTargetQueue, NativeEnemyTargetStats,
    sort_native_command_targets, sort_native_game_bomb_targets,
    select_native_game_bomb_target,
};
pub use self::circle::{
    NativeCircleCommandSnapshot, assemble_circle_snapshot_from_live_weapons,
};
pub use self::swipe::{
    NativeSwipeCommandSnapshot, assemble_swipe_snapshot_from_live_weapons,
};
use self::swipe::SwipeRun;
use self::in_and_out::InAndOutRun;
use self::circle::CircleRun;
use self::full_retreat::FullRetreatRun;
use self::mining::MiningRun;
use self::heal::HealRun;
use self::scouting::ScoutingRun;

const COMMAND_FREQUENCY_SECONDS: f32 = 3.0;
const RANDOM_POSITION_ATTEMPTS: usize = 35;
const RANDOM_MIN_DISTANCE: f32 = 32.0;
const GUARD_PADDING: f32 = 4.0;
const MINING_RATE_SECONDS: f32 = 5.0;
const MINING_COMMAND_TIMEOUT_SECONDS: f32 = 600.0;
const GUARD_SPEED_MATCH_DISTANCE_SQUARED: f32 = 1.5 * 1.5;

#[derive(Clone, Copy, Debug)]
pub struct NativeSquadOrder<'a> {
    pub targeting: Option<crate::squad_targeting::NativeInstalledTargeting>,
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
    /// Required only for Scouting: real Squad.MaxSight and the source
    /// ConfigData.Configuration.HumanSide, supplied by the owning Level.
    pub scouting: Option<NativeScoutingContext>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeScoutingContext {
    pub max_sight: f32,
    pub human_side: i32,
}

impl NativeScoutingContext {
    pub fn valid(self) -> bool {
        self.max_sight.is_finite() && self.max_sight >= 0.0 &&
            self.human_side > 0
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeEnemySquad {
    /// Exact enemy lifecycle captured at command activation, not the
    /// numeric pooled Squad ID alone. This prevents target substitution
    /// before the attack command's first source timer callback.
    pub owner: SquadLifetime,
    pub position: Vec2,
    pub max_range: f32,
    pub is_alive: bool,
}

impl NativeEnemySquad {
    pub fn valid(self) -> bool {
        self.owner.valid() && self.position.is_finite() &&
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
        self.formation.valid() && !self.members.is_empty() &&
            self.enemy.is_none_or(|enemy|
                enemy.valid() && enemy.owner.level_id == self.owner.level_id
            ) &&
            self.scouting.is_none_or(NativeScoutingContext::valid)
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
    InvalidShootingStrategy,
    InvalidCommandOrigin,
    LocalCommandIdExhausted,
    InvalidSquad,
    SquadGeometry(crate::squad_geometry::SquadGeometryError),
    InvalidSettings,
    InvalidDelta,
    InvalidEnemy,
    InvalidFriendlies,
    MissingCombatSnapshot,
    InvalidCombatSnapshot,
    InvalidMiningTarget,
    InvalidCapabilityAccounting,
    MissingOriginalShipHealth(i64),
    MissingScoutingContext,
    MissingGameScoutConfiguration(i64),
    Squad(SquadOrderError),
    World(CombatWorldError),
}

impl From<SquadOrderError> for GameCommandError {
    fn from(value: SquadOrderError) -> Self { Self::Squad(value) }
}

impl From<CombatWorldError> for GameCommandError {
    fn from(value: CombatWorldError) -> Self { Self::World(value) }
}

/// The only safe outcome of receiving a strategy for a dead/superseded
/// squad is explicit reservation settlement. The shared HiveMind session
/// has already checked the request hash and lifecycle identity.
#[derive(Debug)]
pub enum StrategyReceiptAction {
    Applied(Activation),
    DiscardReservations(Vec<i64>),
    /// Host applies each authored ban to its current Squad.BannedStrats
    /// and discards both unearned server reservations.
    BanAndDiscardReservations {
        reservation_ids: Vec<i64>,
        strategies: Vec<LocalCommandKind>,
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
    /// A completed predecessor cannot be safely replaced unless its
    /// settlement obligation fits in the owning Level's durable outbox.
    OutputBackpressure,
    Credit(crate::command_credit::CommandCreditError),
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
    EnemySquadGone,
    /// Unity BombingRun.EndBombingRun reports a completed run when its
    /// current enemy is gone; it is not a failed attack-target selection.
    BombingRunCompleted,
    ChargeCompleted,
    NoEnemyTargets,
    SwipeFinished,
    ReturnedToStart,
    LevelEnded,
    Superseded,
    SquadRetired,
    MiningAsteroidDestroyed,
    NoMiningShipsRemain,
    HealingBeehivesDestroyed,
    HealingCompleted,
    ScoutingFoundShips,
    WarpGateDestroyed,
    AllWarpParticipantsGone,
}

#[derive(Clone, Debug)]
pub struct FinishedNativeCommand {
    pub targeting: Option<ValidatedTargetingSelection>,
    pub targeting_targeted_enemy: bool,
    pub owner: SquadLifetime,
    pub selection: NativeCommandSelection,
    pub cause: CommandFinishCause,
    /// Unity GameState.StoreCommands sends shooting-command credit only
    /// when the issued command selected an enemy and uses that enemy for
    /// targeting (Retreat expressly excluded). Snapshot at activation,
    /// not from a replacement command at finalization.
    pub shooting_targeted_enemy: bool,
    /// Unity Mining.Mine and Heal.HealShips add their actual minerals or
    /// recovered TSV to Command.Tsv. Physical outcomes are not inferable
    /// from the final ship state at command settlement.
    pub capability_command_tsv: Option<i64>,
}

impl FinishedNativeCommand {
    /// The actual gameplay and damage-accounting layer decides whether
    /// shooting/targeting influenced the outcome. The supplied command_tsv
    /// excludes authored capability TSV (but includes real damage or
    /// other command TSV). Mining.Mine and Heal.HealShips add earned value. Inactive
    /// reservations are explicitly discarded, not given synthetic credit.
    /// Local completions retain their identity/value without inventing
    /// command/shooting reservations. An independently issued targeting
    /// reservation is preserved for either origin.
    pub fn settle(
        self,
        command_tsv: i64,
        shooting_tsv: Option<i64>,
        targeting: Option<(&ValidatedTargetingSelection, Option<i64>)>,
    ) -> NativeCommandSettlement {
        let shooting_tsv = if self.shooting_targeted_enemy {
            shooting_tsv
        } else { None };
        let targeting = targeting.map(|(selection, tsv)| match tsv {
            Some(tsv) => OutcomeSettlement::Credit { outcome_id: selection.outcome_id, tsv },
            None => OutcomeSettlement::Discard { outcome_id: selection.outcome_id },
        });
        let command_tsv = command_tsv.saturating_add(self.capability_command_tsv.unwrap_or(0));
        let selection = match self.selection.origin() {
            NativeCommandOrigin::HiveMind(selection) => selection,
            NativeCommandOrigin::Local(identity) => return NativeCommandSettlement::Local {
                identity: *identity, command_tsv, shooting_tsv, targeting,
            },
        };
        NativeCommandSettlement::HiveMind(FinalizedCommand {
            command: OutcomeSettlement::Credit {
                outcome_id: selection.command_outcome_id,
                tsv: command_tsv,
            },
            shooting: match shooting_tsv {
                Some(tsv) => OutcomeSettlement::Credit {
                    outcome_id: selection.shooting_outcome_id, tsv,
                },
                None => OutcomeSettlement::Discard {
                    outcome_id: selection.shooting_outcome_id,
                },
            },
            targeting,
        })
    }
}

#[derive(Debug)]
pub enum NativeCommandSettlement {
    HiveMind(FinalizedCommand),
    Local {
        identity: NativeLocalCommandIdentity,
        command_tsv: i64,
        shooting_tsv: Option<i64>,
        /// Targeting has its own server reservation and lifetime even when
        /// the enclosing movement/attack command originated locally.
        targeting: Option<OutcomeSettlement>,
    },
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
    /// No side effects. HiveMind origins must reconcile their real
    /// reservations; local origins have no server reservations.
    Unsupported(NativeCommandSelection),
    /// Unity ClosestFriendly/Guard cannot install without a valid target.
    /// No server reservation has earned learning credit.
    Unavailable(NativeCommandSelection),
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

/// One canonical command's source state. Physics and cargo belong
/// exclusively to CombatWorld, not to this timer/movement controller.
struct ActiveNativeCommand {
    targeting: Option<ValidatedTargetingSelection>,
    selected_enemy: Option<SquadLifetime>,
    preferred_weapon_enemy: Option<SquadLifetime>,
    selection: NativeCommandSelection,
    // Retreat has no time limit while moving; other commands may have
    // their own one-shot timeout or delayed finalization timer.
    timeout: Option<ScaledTimer>,
    command_poll: Option<ScaledTimer>,
    patrol: Option<PatrolRoute>,
    following: Option<FollowingFriendly>,
    circle: Option<CircleRun>,
    swipe: Option<SwipeRun>,
    in_and_out: Option<InAndOutRun>,
    aggressive: Option<AggressiveRun>,
    bombing: Option<NativeBombingRun>,
    charge: Option<NativeChargeRun>,
    mining: Option<MiningRun>,
    heal: Option<HealRun>,
    scouting: Option<ScoutingRun>,
    full_retreat: Option<FullRetreatRun>,
    /// Authoritative game-owned member snapshot at command issue.
    /// The Level must retire/reissue on squad composition changes.
    member_ids: Vec<i64>,
}

impl ActiveNativeCommand {
    fn targeting_eligible(&self) -> bool {
        self.selected_enemy.is_some() && matches!(self.selection.kind(),
            LocalCommandKind::Aggressive | LocalCommandKind::BombingRun | LocalCommandKind::Charge |
            LocalCommandKind::Retreat | LocalCommandKind::CircleSquad | LocalCommandKind::RightSwipe |
            LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut)
    }
}

/// Cheap command metrics for an experiment dashboard. Cumulative counts and
/// current active-command gauges never retain per-tick histories or timings.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CommandMeasurements {
    pub updates: u64,
    pub commands_examined: u64,
    pub command_timer_callbacks: u64,
    pub timeout_callbacks: u64,
    /// Authoritative Aggressive Level snapshots requested, including
    /// per-ship coroutine resumes. A reused due-timer snapshot counts once.
    pub aggressive_snapshot_requests: u64,
    /// Subset requiring Unity's all-own/all-enemy weapon range predicate.
    /// This is only requested for due position-hold timer callbacks.
    pub aggressive_all_target_snapshot_requests: u64,
    /// Successful authored Fire Barge proximity snapshots on due timers.
    pub bombing_snapshot_requests: u64,
    /// Total (ship,target) trigger pairs supplied by those snapshots.
    /// No raw per-tick contacts or additional histories are retained.
    pub bombing_proximity_pairs: u64,
    pub bombing_retarget_snapshots: u64,
    pub bombing_retarget_enemies_supplied: u64,
    pub bombing_retarget_weapons_supplied: u64,
    pub charge_snapshot_requests: u64,
    pub charge_eligible_barges_supplied: u64,
    pub charge_retarget_snapshots: u64,
    /// Sparse cumulative counters; no per-step logging or history.
    pub mining_extractions: u64,
    pub minerals_mined: u64,
    /// Actual per-ship healing actions (not elapsed timer callbacks).
    pub healing_actions: u64,
    pub healing_tsv: u64,
    pub scouting_beacons_dropped: u64,
    pub scouting_first_sightings: u64,
    pub full_retreat_warps: u64,
    /// Current lifecycle-owned commands requiring fresh friendly geometry.
    pub active_following: usize,
    /// Current lifecycle-owned FullRetreat commands needing warp settlement.
    pub active_full_retreat: usize,
    /// Server selections without an implemented native command executor.
    pub unsupported: u64,
    /// Supported commands with no eligible live target or resource.
    pub unavailable: u64,
    pub activated: u64,
    pub completed: u64,
    pub superseded: u64,
    pub retired: u64,
    /// Commands aborted after an irrecoverable world step. These may
    /// not be credited with a fabricated final TSV.
    pub aborted: u64,
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

    /// Observations use the true MiningAsteroid.SquadsMining cardinality:
    /// count unique live command-owning squads already enrolled by an
    /// actual collider contact. The active BTreeMap has one command per
    /// SquadLifetime, so no duplicate-ship count is possible. One
    /// ephemeral aggregate is updated per world/tick, not per agent.
    pub fn collect_mining_squad_counts(
        &self, output: &mut HashMap<i64, u32>,
    ) {
        output.clear();
        for active in self.active.values() {
            if let Some(asteroid_id) = active.mining.as_ref()
                .and_then(MiningRun::enrolled_target)
            {
                *output.entry(asteroid_id).or_insert(0) += 1;
            }
        }
    }

    /// Most commands do not require a source ship-to-ship trigger input.
    /// Poll the small active-command map instead of creating another
    /// independent roster or checking every game ship every fixed tick.
    pub fn needs_bomber_contacts(&self, world: &CombatWorld) -> bool {
        self.active.values().any(|command|
            command.bombing.as_ref().is_some_and(|bombing|
                bombing.needs_contact_events(world)
            )
        )
    }

    /// Snapshot measurements without resetting totals. Consumers can
    /// calculate experiment/window deltas by subtracting earlier snapshots.
    pub fn measurements(&self) -> CommandMeasurements {
        self.measurements
    }

    pub fn has_active(&self, owner: SquadLifetime) -> bool {
        self.active.contains_key(&owner)
    }

    /// Source Squad.StopChasing finalizes an Aggressive command only.
    /// Do not treat every active strategy as a chase or install a Hold
    /// command in its place.
    /// Update the effective shooting priority of an existing command
    /// without superseding/retiring it or changing the command identity.
    /// A squad without an active command uses its metadata's chosen
    /// strategy through automatic_weapon_inputs.
    pub fn set_player_shooting(
        &mut self, owner: SquadLifetime,
        shooting: bees_hivemind::protocol::StrategyIdentity,
    ) -> Result<(), GameCommandError> {
        NativeCommandSelection::validate_shooting(shooting)?;
        if let Some(command) = self.active.get_mut(&owner) {
            command.selection.set_shooting(shooting)?;
        }
        Ok(())
    }

    pub fn current_kind(&self, owner: SquadLifetime) -> Option<LocalCommandKind> {
        self.active.get(&owner).map(|active| active.selection.kind())
    }

    pub fn is_chasing_command(&self, owner: SquadLifetime) -> bool {
        self.current_kind(owner) == Some(LocalCommandKind::Aggressive)
    }

    pub fn automatic_weapon_input(&self, owner: SquadLifetime)
        -> Option<bees_sim::combat_world::NativeAutomaticWeaponInputs>
    {
        let selection = &self.active.get(&owner)?.selection;
        let key = match selection.origin() {
            NativeCommandOrigin::HiveMind(value) => (0, value.command_outcome_id as u64),
            NativeCommandOrigin::Local(value) => (match value.source {
                NativeLocalCommandSource::Player => 1, NativeLocalCommandSource::Scripted => 2,
            }, value.sequence),
        };
        Some(bees_sim::combat_world::NativeAutomaticWeaponInputs {
            strategy_id: selection.shooting().id, has_command: true, command_key: Some(key),
        })
    }

    pub fn preferred_weapon_enemy(&self, owner: SquadLifetime) -> Option<SquadLifetime> {
        self.active.get(&owner)?.preferred_weapon_enemy
    }

    pub fn selected_enemy(&self, owner: SquadLifetime) -> Option<SquadLifetime> {
        self.active.get(&owner)?.selected_enemy
    }

    pub fn first_targeted_enemy(
        &self, world: &CombatWorld, roster: &NativeSquadRoster, side: i32,
    ) -> Option<SquadLifetime> {
        for &owner in roster.source_order() {
            let Some(ally) = roster.get(owner) else { continue; };
            if ally.side != side { continue; }
            let Some(target) = self.active.get(&owner)
                .and_then(|command| command.selected_enemy) else {
                continue;
            };
            if roster.get(target).is_some_and(|enemy|
                enemy.side != side &&
                enemy.members.iter().any(|&id| world.ship(id).is_some_and(|ship|
                    !ship.vitals.is_dead()
                ))
            ) {
                return Some(target);
            }
        }
        None
    }

    pub fn current_server_outcome(&self, owner: SquadLifetime) -> Option<i64> {
        self.active.get(&owner)?.selection.server_outcome()
    }

    pub fn current_selection(&self, owner: SquadLifetime) -> Option<&NativeCommandSelection> {
        self.active.get(&owner).map(|command| &command.selection)
    }

    pub fn current_kind_and_guarded_target(&self, owner: SquadLifetime)
        -> (Option<LocalCommandKind>, Option<SquadLifetime>)
    {
        let Some(command) = self.active.get(&owner) else { return (None, None) };
        let guarded = command.following.as_ref().filter(|follow| follow.guard_slot.is_some())
            .map(|follow| follow.target);
        (Some(command.selection.kind()), guarded)
    }

    /// A real source hull-contact callback may deliver a commanded bomb
    /// only for the original, still-live BombingRun owning that member.
    /// Never accept a transport-supplied command OutcomeId for damage.
    pub fn bombing_contact_outcome(
        &self, owner: SquadLifetime, ship_id: i64,
    ) -> Option<Option<i64>> {
        let active = self.active.get(&owner)?;
        if active.member_ids.contains(&ship_id) &&
            active.bombing.as_ref().is_some_and(|run|
                run.may_deliver_contact(ship_id)
            ) {
            Some(active.selection.server_outcome())
        } else {
            None
        }
    }

    /// Consume a successful game Striker drop against the original
    /// active command after the world has created the real bomb.
    pub fn record_bombing_striker_drop(
        &mut self, owner: SquadLifetime, ship_id: i64,
    ) -> bool {
        self.active.get_mut(&owner)
            .and_then(|active| active.bombing.as_mut())
            .is_some_and(|run| run.record_striker_drop(ship_id))
    }

    /// Only ClosestFriendly and Guard timer callbacks consult the Level's
    /// refreshed friendly squad geometry. Other commands use their own
    /// world-owned state and must not force a full Level roster projection.
    pub fn needs_live_friendlies(&self) -> bool {
        self.measurements.active_following != 0
    }

    /// A friendly position/Guard bounds snapshot is only read on a
    /// ClosestFriendly or Guard command callback. Both use ScaledTimer's
    /// exact due condition, so quiet fixed steps can skip full Level
    /// geometry collection without delaying source command behavior.
    pub fn needs_live_friendlies_on_update(&self, dt: f32) -> bool {
        self.needs_live_friendlies() && self.active.values().any(|command|
            command.following.is_some() &&
            command.command_poll.as_ref().is_some_and(|timer|
                timer.will_fire(dt))
        )
    }

    /// Invoke before the Level's squad-death event pass. FullRetreat's
    /// source Striker/Drone TSV adjustment is earned only when the physical
    /// warp actually occurs, not when a timer queues the attempt.
    /// The Level's existing ship-to-squad index is still authoritative
    /// until death events retire its owners. Route directly to that command
    /// instead of scanning every active squad for every warped ship.
    pub fn record_warp_events(
        &mut self,
        world: &CombatWorld,
        mut owner_of_ship: impl FnMut(i64) -> Option<SquadLifetime>,
    ) {
        // Most training worlds have no FullRetreat command. Avoid
        // rescanning potentially large combat event buffers in that case.
        if self.measurements.active_full_retreat == 0 { return; }
        for event in world.events() {
            let bees_sim::combat_world::CombatEvent::ShipWarped {
                ship_id, ..
            } = event else { continue; };
            let Some(owner) = owner_of_ship(*ship_id) else { continue; };
            let Some(retreat) = self.active.get_mut(&owner)
                .and_then(|active| active.full_retreat.as_mut()) else {
                    continue;
                };
            if retreat.record_warp_event(event, world) {
                self.measurements.full_retreat_warps += 1;
            }
        }
    }

    /// Source GameState.RemoveShip releases an unavailable Heal member's
    /// command queue entry immediately, then offers the newly freed
    /// Beehive slot to the next damaged member. Use the existing world
    /// reservation table; no second healing-slot owner is introduced.
    /// The roster emits MemberLost only while the squad still survives.
    pub fn healing_member_lost(
        &mut self, world: &mut CombatWorld, owner: SquadLifetime,
    ) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
        let Some(active) = self.active.get_mut(&owner) else {
            return Ok(None);
        };
        let Some(heal) = active.heal.as_mut() else {
            return Ok(None);
        };
        if !heal.ship_became_unavailable(world)? {
            return Ok(None);
        }
        let mut active = self.active.remove(&owner)
            .expect("existing completed Heal command");
        let earned_tsv = active.heal.as_ref()
            .expect("completed Heal command").earned_tsv;
        active.heal.as_mut().expect("completed Heal command")
            .release_all(world);
        self.measurements.completed += 1;
        Ok(Some(FinishedNativeCommand {
            targeting: active.targeting,
            targeting_targeted_enemy: active.targeting_eligible(),
            owner, selection: active.selection,
            cause: CommandFinishCause::HealingCompleted,
            shooting_targeted_enemy: active.preferred_weapon_enemy.is_some(),
            capability_command_tsv: Some(earned_tsv),
        }))
    }


    /// Unity Ship.Kill -> Squad.RemoveShip -> Squad.SetOffsets recomputes
    /// formation offsets for each surviving ship, even when the current
    /// HiveMind command remains active. Retain the strategy's timer and
    /// targeting state, but update every surviving cached movement order.
    /// Drop retired members so subsequent polls cannot order dead ships.
    pub fn refresh_survivor_formation(
        &mut self, owner: SquadLifetime,
        source_offsets: &[SquadMemberOffset],
    ) {
        let Some(active) = self.active.get_mut(&owner) else { return };
        active.member_ids.retain(|id|
            source_offsets.iter().any(|entry| entry.ship_id == *id),
        );
        if let Some(run) = active.patrol.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.following.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.circle.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.swipe.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.in_and_out.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.aggressive.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.bombing.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.charge.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.mining.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.scouting.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        if let Some(run) = active.full_retreat.as_mut() {
            refresh_command_formation_members(&mut run.members, source_offsets);
        }
        // Heal retains per-ship reservation and clearance metadata rather
        // than issuing formation moves. Its dead healing member is already
        // removed by the authoritative squad death pass.
    }

    /// Source HivemindVision.RecordShipSighting only calls
    /// Scouting.FoundNewShips on the first side-wide enemy sighting,
    /// credited to its real squad (or a Beacon's MotherSquad).
    /// The Level adapter verifies that fact; this method does not
    /// mistake every temporary line-of-sight contact for discovery.
    pub fn record_first_ship_sighting(
        &mut self, owner: SquadLifetime, side: i32,
    ) -> bool {
        let Some(command) = self.active.get_mut(&owner) else {
            return false;
        };
        let Some(scouting) = command.scouting.as_mut() else {
            return false;
        };
        if !scouting.found_first_ships(side) { return false; }
        command.timeout = None;
        self.measurements.scouting_first_sightings += 1;
        true
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
        self.apply_strategy_receipt_with_bombing(
            world, navigation, random, squad, friendlies, settings, receipt,
            None,
        )
    }

    /// Use when the actual Level has authored Bomb weapon identities,
    /// target metadata and the exact source squad roster at receipt time.
    /// Without those inputs BombingRun stays explicitly unsupported.
    pub fn apply_strategy_receipt_with_bombing(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        receipt: StrategyReceipt,
        bombing: Option<NativeBombingActivation<'_>>,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        let (owner, enemy_owner, selection) = match receipt {
            StrategyReceipt::Retired { reservation_ids, .. } =>
                return Ok(StrategyReceiptAction::DiscardReservations(
                    reservation_ids,
                )),
            StrategyReceipt::Accepted { owner, enemy_owner, selection } =>
                (owner, enemy_owner, selection),
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
        // The connection-owned request stores the ORIGINAL enemy
        // lifecycle, not just the numeric pooled Squad ID. Reject delayed
        // target-strategy replies after that lifetime disappears, even if
        // another squad has already reused the same numeric ID. Attack
        // timers independently validate the same captured lifetime.
        if matches!(resolved.local_command,
            LocalCommandKind::Retreat | LocalCommandKind::Aggressive |
            LocalCommandKind::CircleSquad | LocalCommandKind::RightSwipe |
            LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut |
            LocalCommandKind::BombingRun | LocalCommandKind::Charge
        ) && (enemy_owner.is_none() ||
            squad.enemy.map(|enemy| enemy.owner) != enemy_owner)
        {
            return Ok(StrategyReceiptAction::DiscardReservations(vec![
                selection.command_outcome_id,
                selection.shooting_outcome_id,
            ]));
        }
        // Unity checks a CarrierSquad's defenseless Strikers before
        // Command.Execute. Returning the source five bans here preserves
        // unused outcome identities without starting an ineffective run.
        if resolved.local_command == LocalCommandKind::BombingRun &&
            bombing.is_some_and(|input|
                input.weapons.len() == squad.members.len() &&
                input.roster.get(squad.owner).is_some_and(|entry|
                    entry.members.len() == squad.members.len() &&
                    entry.members.iter().zip(squad.members)
                        .all(|(&id, member)| id == member.ship_id)
                ) &&
                defenseless_carrier_strikers(
                    world, squad, input.is_carrier_squad,
                )
            ) {
            return Ok(StrategyReceiptAction::BanAndDiscardReservations {
                reservation_ids: vec![
                    resolved.command_outcome_id,
                    resolved.shooting_outcome_id,
                ],
                strategies: vec![
                    LocalCommandKind::Aggressive,
                    LocalCommandKind::CircleSquad,
                    LocalCommandKind::RightSwipe,
                    LocalCommandKind::LeftSwipe,
                    LocalCommandKind::InAndOut,
                ],
            });
        }
        match self.activate_hivemind_with_bombing(
            world, navigation, random, squad, friendlies, settings, resolved,
            bombing,
        ) {
            Ok(Activation::Unavailable(unavailable)) => {
                let reservation_ids = vec![
                    selection.command_outcome_id,
                    selection.shooting_outcome_id,
                ];
                if matches!(unavailable.local_command,
                    LocalCommandKind::Guard | LocalCommandKind::Mining) {
                    Ok(StrategyReceiptAction::BanAndDiscardReservations {
                        reservation_ids,
                        strategies: vec![unavailable.local_command],
                    })
                } else {
                    Ok(StrategyReceiptAction::DiscardReservations(reservation_ids))
                }
            },
            Ok(Activation::Unsupported(_)) =>
                Ok(StrategyReceiptAction::DiscardReservations(vec![
                    selection.command_outcome_id,
                    selection.shooting_outcome_id,
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
        self.activate_hivemind_with_bombing(
            world, navigation, random, squad, friendlies, settings,
            selection, None,
        )
    }

    /// One canonical activation, including BombingRun when the Level
    /// supplies the real owned Bomb weapons and original enemy metadata.
    /// Other strategies use the same path and never pay for Bomb input.
    pub fn activate_hivemind_with_bombing(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        selection: ResolvedHiveMindCommand,
        bombing_input: Option<NativeBombingActivation<'_>>,
    ) -> Result<Activation, GameCommandError> {
        self.activate_selection(world, navigation, random, squad, friendlies,
            settings, selection.into(), None, bombing_input)
    }

    /// Execute player/scripted commands through the same timers, target
    /// reservations, formations and capability controllers as HiveMind.
    pub fn activate_local_selection(
        &mut self, world: &mut CombatWorld, navigation: &NavigationRuntime,
        random: &mut RandomStream, squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad], settings: HiveMindCommandSettings,
        selection: NativeCommandSelection,
        action: NativeLocalCommandAction<'_>,
        bombing_input: Option<NativeBombingActivation<'_>>,
    ) -> Result<Activation, GameCommandError> {
        let NativeCommandOrigin::Local(identity) = selection.origin() else {
            return Err(GameCommandError::InvalidCommandOrigin);
        };
        if selection.local_command != action.kind() {
            return Err(GameCommandError::InvalidCommandOrigin);
        }
        if let NativeLocalCommandAction::TargetEnemy { target } = action {
            if squad.enemy.map(|enemy| enemy.owner) != Some(target) {
                return Err(GameCommandError::InvalidEnemy);
            }
        }
        if selection.local_command == LocalCommandKind::Hold &&
            identity.source != NativeLocalCommandSource::Scripted {
            self.measurements.unsupported += 1;
            return Ok(Activation::Unsupported(selection));
        }
        self.activate_selection(world, navigation, random, squad, friendlies,
            settings, selection, Some(action), bombing_input)
    }

    fn activate_selection(
        &mut self, world: &mut CombatWorld, navigation: &NavigationRuntime,
        random: &mut RandomStream, squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad], settings: HiveMindCommandSettings,
        selection: NativeCommandSelection,
        local: Option<NativeLocalCommandAction<'_>>,
        bombing_input: Option<NativeBombingActivation<'_>>,
    ) -> Result<Activation, GameCommandError> {
        if !squad.valid() {
            return Err(GameCommandError::InvalidSquad);
        }
        if !settings.valid() {
            return Err(GameCommandError::InvalidSettings);
        }

        let mut patrol = None;
        let mut following = None;
        let mut circle = None;
        let mut swipe = None;
        let mut in_and_out = None;
        let mut aggressive = None;
        let mut bombing = None;
        let mut charge = None;
        let mut mining = None;
        let mut heal = None;
        let mut scouting = None;
        let mut full_retreat = None;
        let mut retreat_already_safe = false;
        let issued_move = match selection.local_command {
            LocalCommandKind::Hold => {
                stop_squad_game_movement(world, squad.members)?;
                true
            }
            LocalCommandKind::Mining => {
                let chosen = match local {
                    Some(NativeLocalCommandAction::Mining { asteroid_id }) => Some(asteroid_id),
                    _ => None,
                };
                let Some(run) = MiningRun::start(world, random, squad, chosen)?
                else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                mining = Some(run);
                false
            }
            LocalCommandKind::Heal => {
                let chosen = match local {
                    Some(NativeLocalCommandAction::Heal { beehive_ids }) => Some(beehive_ids),
                    _ => None,
                };
                let Some(run) = HealRun::start(world, squad, chosen)?
                else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                heal = Some(run);
                true
            }
            LocalCommandKind::Scouting => {
                scouting = Some(ScoutingRun::start(
                    world, navigation, random, squad, settings,
                )?);
                true
            }
            LocalCommandKind::FullRetreat => {
                let previous_gate = self.active.get(&squad.owner)
                    .and_then(|prior| prior.full_retreat.as_ref())
                    .map(FullRetreatRun::gate_id);
                let Some(run) = FullRetreatRun::start(
                    world, random, squad, previous_gate,
                    match local {
                        Some(NativeLocalCommandAction::FullRetreat { warp_gate_id }) => Some(warp_gate_id),
                        _ => None,
                    },
                )? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                full_retreat = Some(run);
                true
            }
            LocalCommandKind::CircleSquad => {
                let Some(run) = CircleRun::start(world, squad)? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                circle = Some(run);
                // Unity Execute schedules its first 3s timer immediately;
                // the first formation move occurs during that callback.
                false
            }
            LocalCommandKind::RightSwipe | LocalCommandKind::LeftSwipe => {
                let Some(run) = SwipeRun::start(
                    world, squad, selection.local_command,
                )? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                swipe = Some(run);
                // SwipeSquad.Execute schedules a 3s immediate timer.
                false
            }
            LocalCommandKind::InAndOut => {
                let Some(run) = InAndOutRun::start(world, random, squad)? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                in_and_out = Some(run);
                // Execute picks a return point; timer owns the first move.
                false
            }
            LocalCommandKind::Aggressive => {
                let Some(run) = AggressiveRun::start(world, squad)? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                aggressive = Some(run);
                // Source begins its first coroutine on the immediate timer.
                false
            }
            LocalCommandKind::BombingRun => {
                let Some(input) = bombing_input else {
                    self.measurements.unsupported += 1;
                    return Ok(Activation::Unsupported(selection));
                };
                // All three bomber classes use one movement/command
                // owner. A Striker must already have its live Carrier
                // configuration installed in CombatWorld before issue.
                for member in squad.members {
                    let ship = world.ship(member.ship_id)
                        .ok_or(GameCommandError::InvalidSquad)?;
                    if !matches!(ship.ship_type, 10 | 19 | 22) {
                        self.measurements.unsupported += 1;
                        return Ok(Activation::Unsupported(selection));
                    }
                }
                let Some(run) = NativeBombingRun::start(
                    world, input.roster, random, squad, input.enemy_owner,
                    &selection, input.weapons, input.enemies,
                    input.is_carrier_squad,
                )? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                bombing = Some(run);
                // Unity Execute assigns Bomb targets now but schedules its
                // first tracked pursuit during the immediate command timer.
                false
            }
            LocalCommandKind::Charge => {
                let Some(input) = bombing_input else {
                    self.measurements.unsupported += 1;
                    return Ok(Activation::Unsupported(selection));
                };
                let Some(run) = NativeChargeRun::start(
                    world, input.roster, random, squad, input.enemy_owner,
                    &selection, input.weapons, input.enemies,
                )? else {
                    self.measurements.unavailable += 1;
                    return Ok(Activation::Unavailable(selection));
                };
                charge = Some(run);
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
                let (top_left, bottom_right) = if let Some(NativeLocalCommandAction::Patrol { top_left, bottom_right }) = local {
                    if !top_left.is_finite() || !bottom_right.is_finite() {
                        return Err(GameCommandError::InvalidCombatSnapshot);
                    }
                    (top_left, bottom_right)
                } else {
                    let top_left = choose_random_coordinate(
                        random, squad.squad_center, squad.formation.map_bounds,
                        settings.ai_patrol_max_size, 10.0,
                    );
                    let bottom_right = choose_random_coordinate(
                        random, squad.squad_center, squad.formation.map_bounds,
                        settings.ai_patrol_max_size, 10.0,
                    );
                    (top_left, bottom_right)
                };
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
                    self.measurements.unavailable += 1;
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
                let chosen = match local {
                    Some(NativeLocalCommandAction::Guard { target }) => Some(target),
                    _ => None,
                };
                // Guard uses the same Squad.GetPosition as ClosestFriendly.
                // A target tied on distance retains Level registry order.
                let mut nearest = None;
                let mut best_distance = f32::MAX;
                for &candidate in friendlies {
                    if !candidate.valid() {
                        return Err(GameCommandError::InvalidFriendlies);
                    }
                    if candidate.owner.level_id != squad.owner.level_id ||
                        (chosen.is_none() && candidate.owner == squad.owner) ||
                        candidate.side != side ||
                        !candidate.is_alive ||
                        (chosen.is_none() && candidate.active_command == Some(LocalCommandKind::Guard)) ||
                        chosen.is_some_and(|target| candidate.owner != target) {
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
                    self.measurements.unavailable += 1;
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
            _ => {
                self.measurements.unsupported += 1;
                return Ok(Activation::Unsupported(selection));
            },
        };

        // Native world action above has succeeded. Only now can this
        // command supersede an earlier one; a failed order leaves the
        // existing HiveMind command and its reserved IDs intact.
        let timeout_seconds = match selection.local_command {
            LocalCommandKind::Guard | LocalCommandKind::Patrol |
            LocalCommandKind::Mining | LocalCommandKind::Heal if local.is_some() => None,
            LocalCommandKind::Patrol => Some(settings.ai_squad_patrol_seconds),
            LocalCommandKind::ClosestFriendly =>
                Some(settings.ai_squad_following_seconds),
            LocalCommandKind::Guard => Some(settings.ai_squad_guard_seconds),
            LocalCommandKind::Retreat if retreat_already_safe => Some(3.0),
            LocalCommandKind::Retreat => None,
            LocalCommandKind::Mining => Some(MINING_COMMAND_TIMEOUT_SECONDS),
            LocalCommandKind::Scouting => Some(300.0),
            _ => Some(settings.max_command_seconds),
        };
        let active = ActiveNativeCommand {
            targeting: squad.targeting.map(|choice| choice.selection),
            selected_enemy: squad.enemy.map(|enemy| enemy.owner),
            preferred_weapon_enemy: if matches!(selection.kind(), LocalCommandKind::Aggressive |
                LocalCommandKind::BombingRun | LocalCommandKind::Charge | LocalCommandKind::CircleSquad |
                LocalCommandKind::RightSwipe | LocalCommandKind::LeftSwipe | LocalCommandKind::InAndOut) {
                squad.enemy.map(|enemy| enemy.owner)
            } else { None },
            timeout: timeout_seconds.map(|seconds|
                ScaledTimer::new(seconds, false, false)),
            command_poll: if matches!(
                selection.local_command,
                LocalCommandKind::MoveToRandom | LocalCommandKind::Patrol |
                LocalCommandKind::Mining | LocalCommandKind::Heal |
                LocalCommandKind::Scouting | LocalCommandKind::FullRetreat |
                LocalCommandKind::ClosestFriendly |
                LocalCommandKind::Guard | LocalCommandKind::CircleSquad |
                LocalCommandKind::RightSwipe | LocalCommandKind::LeftSwipe |
                LocalCommandKind::InAndOut | LocalCommandKind::Aggressive |
                LocalCommandKind::BombingRun | LocalCommandKind::Charge
            ) || (selection.local_command == LocalCommandKind::Retreat &&
                !retreat_already_safe) {
                Some(ScaledTimer::new(
                    if selection.local_command == LocalCommandKind::Scouting {
                        scouting::SCOUT_MOVE_SECONDS
                    } else if selection.local_command == LocalCommandKind::BombingRun {
                        2.0
                    } else { COMMAND_FREQUENCY_SECONDS },
                    true,
                    !matches!(selection.local_command,
                        LocalCommandKind::ClosestFriendly |
                        LocalCommandKind::FullRetreat | LocalCommandKind::Charge
                    ),
                ))
            } else { None },
            patrol,
            following,
            circle,
            swipe,
            in_and_out,
            aggressive,
            bombing,
            charge,
            mining,
            heal,
            scouting,
            full_retreat,
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
        // Unity SetFinalize releases old Heal reservations on command
        // replacement. Preserve shared slots already claimed by a new
        // Heal command for the same ship and same Beehive.
        if let Some(previous) = self.active.get_mut(&squad.owner)
            .and_then(|old| old.heal.as_mut()) {
            previous.release_superseded(world, active.heal.as_ref());
        }
        if let Some(previous) = self.active.get(&squad.owner)
            .and_then(|prior| prior.full_retreat.as_ref()) {
            previous.release_all(world, active.full_retreat.as_ref());
        }
        // Both special commands select via Bomb weapons. Retain only
        // members still owning a Bomb command after atomic replacement.
        let retained_bomb_members: &[i64] =
            if active.bombing.is_some() || active.charge.is_some() {
                &active.member_ids
            } else { &[] };
        if let Some(previous) = self.active.get(&squad.owner)
            .and_then(|prior| prior.bombing.as_ref()) {
            previous.release_superseded(world, retained_bomb_members);
        }
        if let Some(previous) = self.active.get(&squad.owner)
            .and_then(|prior| prior.charge.as_ref()) {
            previous.release_superseded(world, retained_bomb_members);
        }
        let new_following = usize::from(active.following.is_some());
        let new_full_retreat = usize::from(active.full_retreat.is_some());
        let replaced = self.active.insert(squad.owner, active);
        // Active gauges change at the same ownership boundary as the map.
        // A replacement exchanges its old type for the new one atomically.
        self.measurements.active_following += new_following;
        self.measurements.active_full_retreat += new_full_retreat;
        if let Some(previous) = replaced.as_ref() {
            self.measurements.active_following -=
                usize::from(previous.following.is_some());
            self.measurements.active_full_retreat -=
                usize::from(previous.full_retreat.is_some());
        }
        let superseded = replaced.map(|old| {
            FinishedNativeCommand {
                targeting: old.targeting,
                targeting_targeted_enemy: old.targeting_eligible(),
                owner: squad.owner, selection: old.selection,
                cause: CommandFinishCause::Superseded,
                shooting_targeted_enemy: old.preferred_weapon_enemy.is_some(),
                capability_command_tsv: old.mining.as_ref()
                    .map(|mining| mining.mined_tsv)
                    .or_else(|| old.heal.as_ref().map(|heal| heal.earned_tsv))
                    .or_else(|| old.full_retreat.as_ref()
                        .map(|retreat| retreat.earned_tsv)),
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
        random: &mut RandomStream,
        dt: f32,
    ) -> Result<Vec<FinishedNativeCommand>, GameCommandError> {
        let mut out = Vec::new();
        self.advance_into(world, navigation, friendlies, random, dt, &mut out)?;
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
        random: &mut RandomStream,
        dt: f32,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), GameCommandError> {
        self.advance_into_with_combat(
            world, navigation, None, friendlies, random, dt,
            |_, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            finished,
        )
    }

    /// Shared fast Level path for all movement-attack timers. The same
    /// authoritative roster and physics world drive target selection,
    /// pathfinding, and combat; no alternate training-only implementation.
    pub fn advance_into_with_combat(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        roster: Option<&NativeSquadRoster>,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        mut circle_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        mut swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), GameCommandError> {
        self.advance_into_with_bombing(
            world, navigation, roster, friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            finished,
        )
    }

    /// Shared Level path: all due combat timers, including Fire Barge
    /// bombing. The Level computes proximity from its real trigger cache;
    /// the command director never guesses range from ship centers.
    pub fn advance_into_with_bombing(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        roster: Option<&NativeSquadRoster>,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        mut circle_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        mut swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        mut aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        mut bombing_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, bool,
        ) -> Result<NativeBombingTimerSnapshot, GameCommandError>,
        mut charge_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, bool,
        ) -> Result<NativeChargeTimerSnapshot, GameCommandError>,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), GameCommandError> {
        self.advance_into_with_scouting_center(
            world, navigation, roster, friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            bombing_snapshot, charge_snapshot,
            |_, _, _| Err(GameCommandError::MissingScoutingContext),
            finished,
        )
    }

    /// A Level can provide fresh source Squad.GetPosition for Scouting's
    /// destination timer without rebuilding the entire friendly roster.
    /// Existing non-Level callers remain fail-closed for this input.
    pub fn advance_into_with_scouting_center(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        roster: Option<&NativeSquadRoster>,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        mut circle_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        mut swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        mut aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        mut bombing_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, bool,
        ) -> Result<NativeBombingTimerSnapshot, GameCommandError>,
        mut charge_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, bool,
        ) -> Result<NativeChargeTimerSnapshot, GameCommandError>,
        mut scouting_center: impl FnMut(
            SquadLifetime, &CombatWorld, &NativeSquadEntry,
        ) -> Result<Vec2, GameCommandError>,
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
            if let Some(retreat) = command.full_retreat.as_mut() {
                let (mut finished, callbacks) = retreat.advance(
                    world, random, &mut command.command_poll, dt,
                )?;
                self.measurements.command_timer_callbacks += callbacks;
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
            if let Some(scouting) = command.scouting.as_mut() {
                let previous_events = world.events().len();
                let mut source_center = |owner: SquadLifetime,
                    combat: &CombatWorld| {
                    let entry = roster.and_then(|registry| registry.get(owner))
                        .ok_or(GameCommandError::MissingScoutingContext)?;
                    scouting_center(owner, combat, entry)
                };
                let (mut finished, callbacks) = scouting.advance(
                    world, navigation, random, &mut command.command_poll, dt,
                    owner, &mut source_center,
                )?;
                self.measurements.command_timer_callbacks += callbacks;
                self.measurements.scouting_beacons_dropped +=
                    world.events()[previous_events..].iter()
                        .filter(|event| matches!(event,
                            bees_sim::combat_world::CombatEvent::ScoutBeaconDropped { .. }
                        )).count() as u64;
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
            if let Some(heal) = command.heal.as_mut() {
                let before_tsv = heal.earned_tsv;
                let before_events = world.events().len();
                let (mut finished, callbacks) = heal.advance(
                    world, &mut command.command_poll, dt,
                )?;
                self.measurements.command_timer_callbacks += callbacks;
                let healed = heal.earned_tsv.saturating_sub(before_tsv);
                self.measurements.healing_tsv += healed as u64;
                self.measurements.healing_actions += world.events()[before_events..]
                    .iter().filter(|event| matches!(
                        event, bees_sim::combat_world::CombatEvent::CapabilityHealed { .. }
                    )).count() as u64;
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
            if let Some(swipe) = command.swipe.as_mut() {
                let mut completed = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let roster = roster.ok_or(GameCommandError::MissingCombatSnapshot)?;
                        let snapshot = swipe_snapshot(
                            owner, swipe.enemy_owner(), world,
                            swipe.needs_target_candidates(),
                        )?;
                        completed = swipe.on_poll(
                            world, navigation, roster, random, owner,
                            &command.selection, snapshot,
                        )?;
                        timer.finish_update(due);
                    }
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            if let Some(in_and_out) = command.in_and_out.as_mut() {
                let mut completed = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let roster = roster.ok_or(GameCommandError::MissingCombatSnapshot)?;
                        let snapshot = swipe_snapshot(
                            owner, in_and_out.enemy_owner(), world,
                            in_and_out.needs_target_candidates(),
                        )?;
                        completed = in_and_out.on_poll(
                            world, navigation, roster, random, owner,
                            &command.selection, snapshot,
                        )?;
                        timer.finish_update(due);
                    }
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            if let Some(aggressive) = command.aggressive.as_mut() {
                let mut completed = None;
                // When StartCoroutine begins during a timer callback the
                // first ship order executes before yielding on this frame.
                let mut due_snapshot = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let need_all = aggressive.needs_all_target_range();
                        self.measurements.aggressive_snapshot_requests += 1;
                        self.measurements.aggressive_all_target_snapshot_requests +=
                            u64::from(need_all);
                        let snapshot = aggressive_snapshot(
                            owner, aggressive.enemy_owner(), world, need_all,
                        )?;
                        completed = aggressive.on_poll(
                            world, navigation, timer, owner, &snapshot,
                        )?;
                        timer.finish_update(due);
                        due_snapshot = Some(snapshot);
                    }
                }
                if completed.is_none() && aggressive.has_pending_coroutine() {
                    let roster = roster.ok_or(GameCommandError::MissingCombatSnapshot)?;
                    let snapshot = if aggressive.coroutine_needs_target() {
                        Some(match due_snapshot {
                            Some(snapshot) => snapshot,
                            None => {
                                self.measurements.aggressive_snapshot_requests += 1;
                                aggressive_snapshot(
                                    owner, aggressive.enemy_owner(), world, false,
                                )?
                            }
                        })
                    } else {
                        None
                    };
                    completed = aggressive.resume_coroutine_one_ship(
                        world, roster, random, owner,
                        &command.selection, snapshot.as_ref(),
                    )?;
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            if let Some(charge) = command.charge.as_mut() {
                let mut completed = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let need_retarget = charge.needs_retarget(world);
                        let mut snapshot = charge_snapshot(
                            owner, charge.enemy_owner(), world,
                            need_retarget,
                        )?;
                        self.measurements.charge_snapshot_requests += 1;
                        self.measurements.charge_eligible_barges_supplied +=
                            snapshot.eligible_barges.len() as u64;
                        self.measurements.charge_retarget_snapshots +=
                            u64::from(need_retarget);
                        snapshot.eligible_barges.sort_unstable();
                        snapshot.eligible_barges.dedup();
                        completed = charge.on_poll(
                            world, owner, &snapshot, &command.selection,
                            roster.ok_or(GameCommandError::MissingCombatSnapshot)?,
                            random,
                        )?;
                        timer.finish_update(due);
                    }
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            if let Some(bombing) = command.bombing.as_mut() {
                let mut completed = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let need_retarget = bombing.needs_retarget(world);
                        let mut snapshot = bombing_snapshot(
                            owner, bombing.enemy_owner(), world,
                            need_retarget,
                        )?;
                        self.measurements.bombing_snapshot_requests += 1;
                        self.measurements.bombing_proximity_pairs +=
                            snapshot.nearby_targets.len() as u64;
                        if need_retarget {
                            self.measurements.bombing_retarget_snapshots += 1;
                            self.measurements.bombing_retarget_enemies_supplied +=
                                snapshot.retarget_enemies.len() as u64;
                            self.measurements.bombing_retarget_weapons_supplied +=
                                snapshot.retarget_weapons.len() as u64;
                        }
                        // The Level's proximity input is ephemeral. Sort
                        // its one-time per-callback projection in place,
                        // then check every barge in logarithmic time rather
                        // than scanning all other barges' contacts.
                        snapshot.nearby_targets.sort_unstable();
                        snapshot.nearby_targets.dedup();
                        completed = bombing.on_poll(
                            world, timer, owner, snapshot.enemy_owner,
                            snapshot.enemy_alive,
                            &command.selection, roster.ok_or(
                                GameCommandError::MissingCombatSnapshot,
                            )?, random,
                            &snapshot.retarget_enemies,
                            &snapshot.retarget_weapons,
                            |ship_id, target_id| Ok(snapshot.nearby_targets
                                .binary_search(&(ship_id, target_id)).is_ok()),
                        )?;
                        timer.finish_update(due);
                    }
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
                    self.completed_scratch.push((owner, cause));
                }
                continue;
            }
            if let Some(circle) = command.circle.as_mut() {
                let mut completed = None;
                if let Some(timer) = command.command_poll.as_mut() {
                    if let Some(due) = timer.begin_update(dt) {
                        self.measurements.command_timer_callbacks += 1;
                        let snapshot = circle_snapshot(
                            owner, circle.enemy_owner(), world,
                        )?;
                        completed = circle.on_poll(
                            world, navigation, timer, owner, snapshot,
                        )?;
                        // A 3s -> 0.25s timer reuse is generation-guarded.
                        timer.finish_update(due);
                    }
                }
                if completed.is_none() {
                    if let Some(timeout) = command.timeout.as_mut() {
                        if let Some(due) = timeout.begin_update(dt) {
                            self.measurements.timeout_callbacks += 1;
                            timeout.finish_update(due);
                            completed = Some(CommandFinishCause::Timeout);
                        }
                    }
                }
                if let Some(cause) = completed {
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
        // Iteration above already follows SquadLifetime's derived Ord,
        // i.e. (level_id, squad_id, lifecycle_id). Each owner is pushed
        // at most once, so completions remain ordered without a second sort.
        for (owner, cause) in self.completed_scratch.drain(..) {
            // Source Guard.SetFinalize must release its speed override,
            // regardless of whether it ended normally or its target died.
            if let Some(guard) = self.active.get(&owner)
                .and_then(|active| active.following.as_ref())
                .filter(|follow| follow.guard_slot.is_some()) {
                restore_guard_speed(world, guard)?;
            }
            // A navigation cleanup failure must not orphan the original
            // command OutcomeIds. Keep it active until all fallible
            // scripted Charge cleanup has succeeded.
            if let Some(charge) = self.active.get(&owner)
                .and_then(|active| active.charge.as_ref()) {
                charge.release_all(world)?;
            }
            if let Some(mut active) = self.active.remove(&owner) {
                self.measurements.active_following -=
                    usize::from(active.following.is_some());
                self.measurements.active_full_retreat -=
                    usize::from(active.full_retreat.is_some());
                if let Some(heal) = active.heal.as_mut() {
                    heal.release_all(world);
                }
                if let Some(bombing) = active.bombing.as_ref() {
                    bombing.release_all(world);
                }
                if let Some(retreat) = active.full_retreat.as_ref() {
                    retreat.release_all(world, None);
                }
                finished.push(FinishedNativeCommand {
                    targeting: active.targeting,
                    targeting_targeted_enemy: active.targeting_eligible(),
                    owner, selection: active.selection, cause,
                    shooting_targeted_enemy: active.preferred_weapon_enemy.is_some(),
                    capability_command_tsv: active.mining.as_ref()
                        .map(|mining| mining.mined_tsv)
                        .or_else(|| active.heal.as_ref().map(|heal| heal.earned_tsv))
                        .or_else(|| active.full_retreat.as_ref()
                            .map(|retreat| retreat.earned_tsv)),
                });
            }
        }
        self.measurements.completed += finished.len() as u64;
        Ok(())
    }

    /// A failed world cannot safely run more callbacks or claim its TSV.
    /// Drain every origin without touching partially updated physics. Only
    /// HiveMind origins carry server discard/reconciliation obligations.
    pub(crate) fn take_failed_commands(
        &mut self,
    ) -> impl Iterator<Item = (SquadLifetime, NativeCommandSelection)> {
        let active = std::mem::take(&mut self.active);
        self.measurements.aborted += active.len() as u64;
        self.measurements.active_following = 0;
        self.measurements.active_full_retreat = 0;
        active.into_iter().map(|(owner, command)| (owner, command.selection))
    }

    /// Unity command finalization queues a fresh HiveMind request when the
    /// squad is still eligible; that request scheduling belongs to the
    /// caller, because it is bound to the live authenticated WebSocket.
    pub fn retire_squad(
        &mut self,
        world: &mut CombatWorld,
        owner: SquadLifetime,
    ) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
        self.finish_squad(world, owner, CommandFinishCause::SquadRetired)
    }

    pub fn supersede_for_local_command(
        &mut self, world: &mut CombatWorld, owner: SquadLifetime,
    ) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
        self.finish_squad(world, owner, CommandFinishCause::Superseded)
    }

    fn finish_squad(
        &mut self, world: &mut CombatWorld, owner: SquadLifetime,
        cause: CommandFinishCause,
    ) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
        // On squad death, missing/dead members are skipped, exactly as
        // Unity Guard.SetFinalize does not reset a dead squad's speed.
        if let Some(guard) = self.active.get(&owner)
            .and_then(|active| active.following.as_ref())
            .filter(|follow| follow.guard_slot.is_some()) {
            restore_guard_speed(world, guard)?;
        }
        if let Some(charge) = self.active.get(&owner)
            .and_then(|active| active.charge.as_ref()) {
            charge.release_all(world)?;
        }
        let retired = self.active.remove(&owner).map(|mut active| {
            self.measurements.active_following -=
                usize::from(active.following.is_some());
            self.measurements.active_full_retreat -=
                usize::from(active.full_retreat.is_some());
            if let Some(heal) = active.heal.as_mut() {
                heal.release_all(world);
            }
            if let Some(bombing) = active.bombing.as_ref() {
                bombing.release_all(world);
            }
            if let Some(retreat) = active.full_retreat.as_ref() {
                retreat.release_all(world, None);
            }
            FinishedNativeCommand {
                targeting: active.targeting,
                targeting_targeted_enemy: active.targeting_eligible(),
                owner, selection: active.selection,
                cause,
                shooting_targeted_enemy: active.preferred_weapon_enemy.is_some(),
                capability_command_tsv: active.mining.as_ref()
                    .map(|mining| mining.mined_tsv)
                    .or_else(|| active.heal.as_ref().map(|heal| heal.earned_tsv))
                    .or_else(|| active.full_retreat.as_ref()
                        .map(|retreat| retreat.earned_tsv)),
            }
        });
        if cause == CommandFinishCause::Superseded {
            self.measurements.superseded += u64::from(retired.is_some());
        } else {
            self.measurements.retired += u64::from(retired.is_some());
        }
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

/// Rebase one command's surviving member offsets from the same source
/// sprite-extent geometry used by the Level, preserving original member
/// iteration order, mobility and authored Pathfinder clearance. This
/// only executes when squad membership changes, not each fixed step.
fn refresh_command_formation_members(
    members: &mut Vec<SquadFormationMember>,
    source_offsets: &[SquadMemberOffset],
) {
    members.retain_mut(|member| {
        let Some(source) = source_offsets.iter().find(|entry|
            entry.ship_id == member.ship_id,
        ) else { return false };
        member.offset_from_center = source.offset;
        true
    });
}
