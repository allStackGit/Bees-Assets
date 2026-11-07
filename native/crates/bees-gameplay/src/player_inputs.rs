//! Native player commands operate on the same roster, command outbox and
//! CombatWorld as AI and headless gameplay. The window owns no game state.

use bees_hivemind::session::SquadLifetime;
use bees_sim::{geometry::Vec2, simulation::{World, WorldId},
    squad_movement::{SquadFormationMember, SquadFormationOptions, issue_squad_move}};
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
    /// Full game adapters cancel their real scripted queue here. Fresh
    /// policy-scene adapters reject queued source history at import.
    fn cancel_player_scripted_queue(&mut self, owner: SquadLifetime)
        -> Result<(), GameCommandError>;
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
    Source(GameCommandError),
    Finalization(NativeLevelEventError),
}

impl NativePlayerInputError {
    /// Expected user rejection is read-only; execution/source failures must
    /// terminate the caller's window and follow normal abort settlement.
    pub fn is_rejection(&self) -> bool {
        matches!(self, Self::StaleWorld | Self::InactiveWorld | Self::MissingSquad |
            Self::NotControllable | Self::InvalidDestination)
    }
}

/// Immutable preflight token. Host retires pending network authority after
/// preparation, before consuming this token synchronously. No fake command
/// selection, outcome IDs or persistent MoveToPoint command are allocated.
pub struct NativePlayerMovePlan {
    input: NativePlayerMove,
    tick: u64,
    owner: SquadLifetime,
    members: Vec<SquadFormationMember>,
    options: SquadFormationOptions,
}

impl NativePlayerMovePlan {
    pub fn owner(&self) -> SquadLifetime { self.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, input: NativePlayerMove,
    ) -> Result<Self, NativePlayerInputError> {
        if world.id != input.world_id || world.episode_revision() != input.episode_revision {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &world.state;
        if world.is_failed() || state.events.is_failed() || state.events.completed() {
            return Err(NativePlayerInputError::InactiveWorld);
        }
        if !input.destination.is_finite() {
            return Err(NativePlayerInputError::InvalidDestination);
        }
        let owner = state.events.roster().owning_squad(input.selected_ship)
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
        Ok(Self { input, tick: world.tick(), owner, members, options })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<bool, NativePlayerInputError> {
        // Reject tokens retained across ticks, resets, membership/permission
        // edits or pooled identities before any gameplay mutation.
        let current = Self::prepare(world, self.input)?;
        if world.tick() != self.tick || current.owner != self.owner ||
            current.members != self.members || current.options != self.options {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        let result = (|| {
            state.metadata.cancel_player_scripted_queue(self.owner)
                .map_err(NativePlayerInputError::Source)?;
            state.events.finalize_player_command(self.tick, &mut state.combat, self.owner)
                .map_err(NativePlayerInputError::Finalization)?;
            // Formation search uses the current colliders, including edits
            // made between fixed steps. All ship orders remain in bees-sim.
            let asteroids = state.metadata.collision_asteroids_enabled();
            let navigation = state.metadata.navigation_mut();
            state.combat.refresh_world_navigation(navigation, true, asteroids)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            issue_squad_move(&mut state.combat, navigation, &self.members,
                self.input.destination, self.options)
                .map(|result| result.is_some())
                .map_err(|error| NativePlayerInputError::Source(error.into()))
        })();
        if result.is_err() { state.events.fail_player_input(); }
        result
    }
}
