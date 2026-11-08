//! Native player commands operate on the same roster, command outbox and
//! CombatWorld as AI and headless gameplay. The window owns no game state.

use bees_hivemind::session::SquadLifetime;
use bees_sim::{geometry::Vec2, simulation::{World, WorldId},
    squad_movement::{SquadFormationMember, SquadFormationOptions,
        issue_squad_move, stop_squad_game_movement}};
use crate::{commands::GameCommandError, level_events::NativeLevelEventError,
    level_host::NativeLevelActivationContext, level_rules::NativeLevelState};

#[derive(Clone, Copy, Debug)]
pub struct NativePlayerPermissions {
    pub user_controlled: bool,
    pub can_accept_input: bool,
    pub locked_on: bool,
}

/// Object-safe read-only authority, also borrowed by native presentation.
pub trait NativePlayerAuthority {
    fn player_permissions(&self, owner: SquadLifetime) -> Option<NativePlayerPermissions>;
}

pub trait NativeLevelPlayerContext: NativeLevelActivationContext + NativePlayerAuthority {
    fn player_formation_options(&self) -> SquadFormationOptions;
    /// Current source Squad.GetShootingStrategy and loaded command settings.
    fn player_command_inputs(&self, _owner: SquadLifetime)
        -> Result<NativePlayerCommandInputs, GameCommandError>
    { Err(GameCommandError::MissingCombatSnapshot) }
    /// Full game adapters cancel their real scripted queue here. Fresh
    /// policy-scene adapters reject queued source history at import.
    fn cancel_player_scripted_queue(&mut self, owner: SquadLifetime)
        -> Result<(), GameCommandError>;
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativePlayerCommandInputs {
    pub shooting: bees_hivemind::protocol::StrategyIdentity,
    pub settings: crate::commands::HiveMindCommandSettings,
}

/// A chosen support command, scoped to the displayed world and episode.
/// Attack commands require additional enemy/weapon authority and are rejected.
#[derive(Clone, Copy, Debug)]
pub struct NativePlayerCommand<'a> {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub selected_ship: i64,
    pub action: crate::commands::NativeLocalCommandAction<'a>,
}

/// Owned UI intent; targets remain identities in the canonical world. Heal
/// owns its ordered ID list so input can cross the window's borrowed frame.
#[derive(Clone, Debug, PartialEq)]
pub enum NativePlayerSupportAction {
    Guard { target: SquadLifetime },
    Patrol { top_left: Vec2, bottom_right: Vec2 },
    Mining { asteroid_id: i64 },
    Heal { beehive_ids: Vec<i64> },
    FullRetreat { warp_gate_id: i64 },
}

impl NativePlayerSupportAction {
    pub fn as_local_action(&self) -> crate::commands::NativeLocalCommandAction<'_> {
        use crate::commands::NativeLocalCommandAction as Action;
        match self {
            Self::Guard { target } => Action::Guard { target: *target },
            Self::Patrol { top_left, bottom_right } => Action::Patrol {
                top_left: *top_left, bottom_right: *bottom_right },
            Self::Mining { asteroid_id } => Action::Mining { asteroid_id: *asteroid_id },
            Self::Heal { beehive_ids } => Action::Heal { beehive_ids },
            Self::FullRetreat { warp_gate_id } => Action::FullRetreat { warp_gate_id: *warp_gate_id },
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct NativePlayerSquadCommands {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub action: NativePlayerSupportAction,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePlayerCommandCounts {
    pub accepted_squads: usize,
    pub unavailable_squads: usize,
    pub rejected_squads: usize,
}

#[derive(Debug)]
pub enum NativePlayerCommandOutcome {
    Command(crate::commands::Activation),
    Move { reachable: bool },
}

pub struct NativePlayerCommandPlan<'a> {
    execution: NativePlayerCommandExecution<'a>,
}

enum NativePlayerCommandExecution<'a> {
    Command(NativePlayerSupportCommandPlan<'a>),
    Move { plan: NativePlayerMovePlan, asteroid_id: i64 },
}

impl<'a> NativePlayerCommandPlan<'a> {
    pub fn owner(&self) -> SquadLifetime {
        match &self.execution {
            NativePlayerCommandExecution::Command(plan) => plan.owner(),
            NativePlayerCommandExecution::Move { plan, .. } => plan.owner(),
        }
    }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &mut World<NativeLevelState<M, A>>, input: NativePlayerCommand<'a>,
    ) -> Result<Self, NativePlayerInputError> {
        use crate::commands::NativeLocalCommandAction as Action;
        let authority = NativePlayerSquadPlan::prepare(world, input.world_id,
            input.episode_revision, input.selected_ship)?;
        if let Action::Mining { asteroid_id } = input.action {
            let has_miner = authority.members.iter().any(|member|
                world.state.combat.ship(member.ship_id).is_some_and(|ship|
                    bees_sim::combat_world::is_mining_ship_type(ship.ship_type)));
            // Source UserMining moves non-mining squads to the chosen live
            // asteroid; it does not install a persistent Mining command.
            if !has_miner {
                if let Some(destination) = world.state.combat.mining_asteroid_position(asteroid_id) {
                    return Ok(Self { execution: NativePlayerCommandExecution::Move {
                        plan: NativePlayerMovePlan { authority, input: NativePlayerMove {
                            world_id: input.world_id, episode_revision: input.episode_revision,
                            selected_ship: input.selected_ship, destination,
                        } }, asteroid_id } });
                }
            }
        }
        if matches!(input.action, Action::FullRetreat { .. }) && authority.members.iter().all(|member|
            world.state.combat.ship(member.ship_id).is_some_and(|ship|
                ship.ship_type == bees_sim::combat_world::WARP_GATE_TYPE)) {
            return Err(NativePlayerInputError::NotControllable);
        }
        Ok(Self { execution: NativePlayerCommandExecution::Command(
            NativePlayerSupportCommandPlan::prepare(world, input)?) })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(self,
        world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<NativePlayerCommandOutcome, NativePlayerInputError> {
        match self.execution {
            NativePlayerCommandExecution::Command(plan) =>
                plan.execute(world).map(NativePlayerCommandOutcome::Command),
            NativePlayerCommandExecution::Move { plan, asteroid_id } => {
                if world.state.combat.mining_asteroid_position(asteroid_id) != Some(plan.input.destination) {
                    return Err(NativePlayerInputError::StaleWorld);
                }
                plan.execute(world).map(|reachable| NativePlayerCommandOutcome::Move { reachable })
            },
        }
    }
}

struct NativePlayerSupportCommandPlan<'a> {
    input: NativePlayerCommand<'a>,
    authority: NativePlayerSquadPlan,
    source: NativePlayerCommandInputs,
    center: Vec2,
    friendlies: Vec<crate::commands::NativeFriendlySquad>,
}

impl<'a> NativePlayerSupportCommandPlan<'a> {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &mut World<NativeLevelState<M, A>>, input: NativePlayerCommand<'a>,
    ) -> Result<Self, NativePlayerInputError> {
        use crate::commands::NativeLocalCommandAction as Action;
        match input.action {
            Action::Guard { .. } | Action::Mining { .. } | Action::Heal { .. } |
            Action::FullRetreat { .. } => (),
            Action::Patrol { top_left, bottom_right } if top_left.is_finite() &&
                bottom_right.is_finite() && top_left.x <= bottom_right.x &&
                top_left.y >= bottom_right.y => (),
            _ => return Err(NativePlayerInputError::InvalidAction),
        }
        let authority = NativePlayerSquadPlan::prepare(world, input.world_id,
            input.episode_revision, input.selected_ship)?;
        let source = world.state.metadata.player_command_inputs(authority.owner)
            .map_err(NativePlayerInputError::Source)?;
        if !source.settings.valid() {
            return Err(NativePlayerInputError::Source(GameCommandError::InvalidSettings));
        }
        // Validate source identity before any reservation retirement or mutation.
        crate::commands::NativeCommandSelection::local(
            crate::commands::NativeLocalCommandIdentity {
                source: crate::commands::NativeLocalCommandSource::Player, sequence: 0,
            }, input.action.kind(), source.shooting)
            .map_err(NativePlayerInputError::Source)?;
        let state = &mut world.state;
        let center = crate::squad_geometry::live_squad_bounds_from_catalog(
            &state.combat, state.events.roster().get(authority.owner)
                .ok_or(NativePlayerInputError::MissingSquad)?,
            state.metadata.ship_asset_catalog()
                .ok_or(NativePlayerInputError::Source(GameCommandError::MissingCombatSnapshot))?,
        ).map_err(|error| NativePlayerInputError::Source(GameCommandError::SquadGeometry(error)))?.center;
        let mut friendlies = Vec::new();
        if matches!(input.action, Action::Guard { .. }) {
            state.metadata.collect_live_friendlies(&state.combat, state.events.roster(),
                state.events.commands(), &mut friendlies)
                .map_err(NativePlayerInputError::Source)?;
        }
        Ok(Self { input, authority, source, center, friendlies })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(self,
        world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<crate::commands::Activation, NativePlayerInputError> {
        let current = Self::prepare(world, self.input)?;
        if current.authority != self.authority || current.source != self.source ||
            current.center != self.center {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let owner = self.owner();
        let tick = world.tick();
        let state = &mut world.state;
        let result = (|| {
            let squad = crate::commands::NativeSquadOrder {
                owner, members: &self.authority.members, squad_center: self.center,
                formation: self.authority.options, enemy: None, scouting: None,
            };
            state.metadata.cancel_player_scripted_queue(owner)
                .map_err(NativePlayerInputError::Source)?;
            let asteroids = state.metadata.collision_asteroids_enabled();
            let navigation = state.metadata.navigation_mut();
            state.combat.refresh_world_navigation(navigation, true, asteroids)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            state.events.activate_local_command(tick, &mut state.combat, navigation,
                &mut world.random, squad, &current.friendlies, self.source.settings,
                crate::commands::NativeLocalCommandSource::Player, self.input.action,
                self.source.shooting, None).map_err(NativePlayerInputError::Finalization)
        })();
        if result.is_err() { state.events.fail_player_input(); }
        result
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativePlayerMove {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub selected_ship: i64,
    pub destination: Vec2,
}

#[derive(Clone, Debug, PartialEq)]
pub struct NativePlayerSquadMoves {
    pub world_id: WorldId,
    pub episode_revision: u64,
    /// Source selection order, with complete pooled-squad lifetimes.
    pub squads: Vec<SquadLifetime>,
    pub destination: Vec2,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePlayerMoveCounts {
    pub accepted_squads: usize,
    pub unreachable_squads: usize,
    pub rejected_squads: usize,
}

#[derive(Debug)]
pub enum NativePlayerInputError {
    StaleWorld,
    InactiveWorld,
    MissingSquad,
    NotControllable,
    InvalidDestination,
    InvalidAction,
    Source(GameCommandError),
    Finalization(NativeLevelEventError),
}

impl NativePlayerInputError {
    /// Expected user rejection is read-only; execution/source failures must
    /// terminate the caller's window and follow normal abort settlement.
    pub fn is_rejection(&self) -> bool {
        matches!(self, Self::StaleWorld | Self::InactiveWorld | Self::MissingSquad |
            Self::NotControllable | Self::InvalidDestination | Self::InvalidAction)
    }
}

/// Shared world, lifetime, permissions and formation preflight for every
/// player order. The host retires pending AI authority only after preparation.
#[derive(PartialEq)]
struct NativePlayerSquadPlan {
    tick: u64,
    owner: SquadLifetime,
    members: Vec<SquadFormationMember>,
    options: SquadFormationOptions,
}

impl NativePlayerSquadPlan {
    fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, world_id: WorldId,
        episode_revision: u64, selected_ship: i64,
    ) -> Result<Self, NativePlayerInputError> {
        if world.id != world_id || world.episode_revision() != episode_revision {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &world.state;
        if world.is_failed() || state.events.is_failed() || state.events.completed() {
            return Err(NativePlayerInputError::InactiveWorld);
        }
        let owner = state.events.roster().owning_squad(selected_ship)
            .ok_or(NativePlayerInputError::MissingSquad)?;
        let permission = state.metadata.player_permissions(owner)
            .ok_or(NativePlayerInputError::NotControllable)?;
        if !permission.user_controlled || !permission.can_accept_input || permission.locked_on {
            return Err(NativePlayerInputError::NotControllable);
        }
        let squad = state.events.roster().get(owner)
            .ok_or(NativePlayerInputError::MissingSquad)?;
        let options = state.metadata.player_formation_options();
        if !options.valid() { return Err(NativePlayerInputError::InvalidDestination); }
        let mut members = Vec::with_capacity(squad.members.len());
        for &ship_id in &squad.members {
            let body = state.combat.ship_body(ship_id)
                .ok_or(NativePlayerInputError::MissingSquad)?;
            let ship = state.combat.ship(ship_id)
                .ok_or(NativePlayerInputError::MissingSquad)?;
            if !body.active || ship.vitals.is_dead() {
                return Err(NativePlayerInputError::MissingSquad);
            }
            if body.motion.is_policy_controlled && !body.motion.user_controlled {
                return Err(NativePlayerInputError::NotControllable);
            }
            if body.motion.mobile {
                state.combat.validate_ship_game_order(ship_id)
                    .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            }
            let offset = state.metadata.stored_formation_offset(ship_id)
                .ok_or(NativePlayerInputError::Source(GameCommandError::MissingCombatSnapshot))?;
            let clearance = state.metadata.current_path_clearance(ship_id)
                .ok_or(NativePlayerInputError::Source(GameCommandError::MissingCombatSnapshot))?;
            if !offset.is_finite() || clearance < 0 {
                return Err(NativePlayerInputError::Source(GameCommandError::InvalidCombatSnapshot));
            }
            members.push(SquadFormationMember {
                ship_id, offset_from_center: offset, clearance, mobile: body.motion.mobile,
            });
        }
        Ok(Self { tick: world.tick(), owner, members, options })
    }

}

pub struct NativePlayerMovePlan {
    input: NativePlayerMove,
    authority: NativePlayerSquadPlan,
}

impl NativePlayerMovePlan {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, input: NativePlayerMove,
    ) -> Result<Self, NativePlayerInputError> {
        if !input.destination.is_finite() {
            return Err(NativePlayerInputError::InvalidDestination);
        }
        let authority = NativePlayerSquadPlan::prepare(world, input.world_id,
            input.episode_revision, input.selected_ship)?;
        Ok(Self { input, authority })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<bool, NativePlayerInputError> {
        // Reject tokens retained across ticks, resets, membership/permission
        // edits or pooled identities before any gameplay mutation.
        let current = Self::prepare(world, self.input)?;
        if current.authority != self.authority {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        let result = (|| {
            state.metadata.cancel_player_scripted_queue(self.authority.owner)
                .map_err(NativePlayerInputError::Source)?;
            state.events.finalize_local_command(self.authority.tick, &mut state.combat, self.authority.owner)
                .map_err(NativePlayerInputError::Finalization)?;
            // Formation search uses the current colliders, including edits
            // made between fixed steps. All ship orders remain in bees-sim.
            let asteroids = state.metadata.collision_asteroids_enabled();
            let navigation = state.metadata.navigation_mut();
            state.combat.refresh_world_navigation(navigation, true, asteroids)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            issue_squad_move(&mut state.combat, navigation, &self.authority.members,
                self.input.destination, self.authority.options)
                .map(|result| result.is_some())
                .map_err(|error| NativePlayerInputError::Source(error.into()))
        })();
        if result.is_err() { state.events.fail_player_input(); }
        result
    }
}


/// One explicit player StopMoving intent. Stopping is not the AI Hold
/// command, and never fabricates a learned shooting strategy or reservation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativePlayerStop {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub selected_ship: i64,
}

/// Selected source-lifetime squads, retaining the player's selection order.
/// The host resolves each owner to live members and deduplicates identities.
#[derive(Clone, Debug, PartialEq)]
pub struct NativePlayerSquadStops {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePlayerStopCounts {
    pub accepted_squads: usize,
    pub rejected_squads: usize,
}

/// Preflight before retiring outstanding AI requests. Stop uses the same
/// authenticated world/episode/squad roster as Move and support commands.
pub struct NativePlayerStopPlan {
    input: NativePlayerStop,
    authority: NativePlayerSquadPlan,
}

impl NativePlayerStopPlan {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, input: NativePlayerStop,
    ) -> Result<Self, NativePlayerInputError> {
        let authority = NativePlayerSquadPlan::prepare(
            world, input.world_id, input.episode_revision, input.selected_ship,
        )?;
        Ok(Self { input, authority })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<(), NativePlayerInputError> {
        let current = Self::prepare(world, self.input)?;
        if current.authority != self.authority {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        let result = (|| {
            state.metadata.cancel_player_scripted_queue(self.authority.owner)
                .map_err(NativePlayerInputError::Source)?;
            // One canonical command finalization, including prior server
            // reservations and actual timed command completion.
            state.events.finalize_local_command(
                self.authority.tick, &mut state.combat, self.authority.owner,
            ).map_err(NativePlayerInputError::Finalization)?;
            // This clears actual tracked waypoints/A* steering on every
            // mobile member. A zero-distance Move would not do that.
            stop_squad_game_movement(&mut state.combat, &self.authority.members)
                .map(|_| ())
                .map_err(|error| NativePlayerInputError::Source(error.into()))
        })();
        if result.is_err() { state.events.fail_player_input(); }
        result
    }
}


/// Persistent manual point-fire for real user-controlled turret ships.
/// Source Turret.IsFiringManually targets this world point while enabled;
/// the canonical turret cooldown and projectile physics own every shot.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativePlayerManualFire {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub selected_ship: i64,
    pub target: Option<Vec2>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct NativePlayerSquadManualFire {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    /// Some(point) begins or retargets manual fire; None clears it.
    pub target: Option<Vec2>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePlayerManualFireCounts {
    pub accepted_squads: usize,
    pub rejected_squads: usize,
}

/// Weapon input neither replaces movement commands nor retires server
/// strategies. It is a parallel, authorized input into the SAME physical
/// ship turrets already used by gameplay and policy projectile collisions.
pub struct NativePlayerManualFirePlan {
    input: NativePlayerManualFire,
    authority: NativePlayerSquadPlan,
    armed_ships: Vec<i64>,
}

impl NativePlayerManualFirePlan {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, input: NativePlayerManualFire,
    ) -> Result<Self, NativePlayerInputError> {
        if input.target.is_some_and(|point| !point.is_finite()) {
            return Err(NativePlayerInputError::InvalidDestination);
        }
        let authority = NativePlayerSquadPlan::prepare(
            world, input.world_id, input.episode_revision, input.selected_ship,
        )?;
        let armed_ships: Vec<_> = authority.members.iter()
            .filter(|member| world.state.combat
                .game_ship_has_manual_weapon(member.ship_id))
            .map(|member| member.ship_id).collect();
        if armed_ships.is_empty() {
            return Err(NativePlayerInputError::NotControllable);
        }
        Ok(Self { input, authority, armed_ships })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<(), NativePlayerInputError> {
        let current = Self::prepare(world, self.input)?;
        if current.authority != self.authority ||
            current.armed_ships != self.armed_ships
        {
            return Err(NativePlayerInputError::StaleWorld);
        }
        for ship_id in self.armed_ships {
            if let Err(error) = world.state.combat
                .set_game_ship_manual_target(ship_id, self.input.target)
            {
                world.state.events.fail_player_input();
                return Err(NativePlayerInputError::Source(error.into()));
            }
        }
        Ok(())
    }
}
