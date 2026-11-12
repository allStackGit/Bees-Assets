//! Native player commands operate on the same roster, command outbox and
//! CombatWorld as AI and headless gameplay. The window owns no game state.

use bees_hivemind::session::SquadLifetime;
use bees_sim::{geometry::Vec2, simulation::{World, WorldId},
    squad_movement::{SquadFormationMember, SquadFormationOptions, issue_squad_move}};
use crate::{commands::GameCommandError, level_events::NativeLevelEventError,
    level_host::NativeLevelActivationContext, level_rules::NativeLevelState};

const FLAGSHIP_SHIP_TYPE: u8 = 11;

#[derive(Clone, Copy, Debug)]
pub struct NativePlayerPermissions {
    pub user_controlled: bool,
    pub can_accept_input: bool,
    pub locked_on: bool,
}

/// Object-safe read-only authority, also borrowed by native presentation.
pub trait NativePlayerAuthority {
    /// Unity activates fog only for a Level with a user controller. Old
    /// snapshots lacking Level.ActivateFogOfWar remain unmasked.
    fn player_fog_of_war_enabled(&self) -> bool { false }
    /// Unity uses Range = 2 * (Sight or MaxRange) as a SpriteMask
    /// *diameter*, hence the visibility radius is Sight or MaxRange.
    /// This display-only value never replaces HiveMind perception.
    fn player_fog_vision_radius(&self, _ship_id: i64) -> Option<f32> { None }
    /// Actual Level.HasObstacles; None means range/line inspection is unavailable.
    fn weapon_obstacles_enabled(&self) -> Option<bool> { None }
    fn player_permissions(&self, owner: SquadLifetime) -> Option<NativePlayerPermissions>;
    /// Current mutable source Squad.GetShootingStrategy, not the initial
    /// imported snapshot and not the server's immutable action identity.
    fn player_shooting_strategy(&self, _owner: SquadLifetime)
        -> Option<bees_hivemind::protocol::StrategyIdentity> { None }
    fn player_should_chase(&self, _owner: SquadLifetime) -> bool { false }
    /// The source's currently chosen match-speed state. None means the
    /// squad is moving at its own ship speeds (not an unknown value).
    fn player_matched_speed(&self, _owner: SquadLifetime) -> Option<f32> { None }
    fn player_cease_fire(&self, _owner: SquadLifetime) -> Option<bool> { None }
}

pub trait NativeLevelPlayerContext: NativeLevelActivationContext + NativePlayerAuthority {
    fn player_formation_options(&self) -> SquadFormationOptions;
    fn set_player_shooting_strategy(&mut self, owner: SquadLifetime,
        shooting: bees_hivemind::protocol::StrategyIdentity)
        -> Result<(), GameCommandError>;
    /// SquadActionBox.LockOn is a live command-associated player choice.
    fn set_player_locked_on(&mut self, owner: SquadLifetime, locked: bool)
        -> Result<(), GameCommandError>;
    fn set_player_chasing(&mut self, owner: SquadLifetime, chasing: bool)
        -> Result<(), GameCommandError>;
    fn set_player_matched_speed(&mut self, owner: SquadLifetime, speed: Option<f32>)
        -> Result<(), GameCommandError>;
    fn set_player_cease_fire(&mut self, owner: SquadLifetime, enabled: bool)
        -> Result<(), GameCommandError>;
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
    pub is_carrier_squad: Option<bool>,
    pub shooting: bees_hivemind::protocol::StrategyIdentity,
    pub settings: crate::commands::HiveMindCommandSettings,
}

/// A chosen player command, scoped to the displayed world and episode.
#[derive(Clone, Copy, Debug)]
pub struct NativePlayerCommand<'a> {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub selected_ship: i64,
    pub action: crate::commands::NativeLocalCommandAction<'a>,
}

/// Owned UI intent; targets remain identities in the canonical world. Heal
/// owns its ordered ID list so input can cross the window's borrowed frame.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub enum NativePlayerSupportAction {
    /// Source Hold button: stop an active Aggressive chase, not a new order.
    Hold,
    Attack { target: SquadLifetime },
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
            Self::Hold => Action::Hold,
            Self::Attack { target } => Action::TargetEnemy { target: *target },
            Self::Guard { target } => Action::Guard { target: *target },
            Self::Patrol { top_left, bottom_right } => Action::Patrol {
                top_left: *top_left, bottom_right: *bottom_right },
            Self::Mining { asteroid_id } => Action::Mining { asteroid_id: *asteroid_id },
            Self::Heal { beehive_ids } => Action::Heal { beehive_ids },
            Self::FullRetreat { warp_gate_id } => Action::FullRetreat { warp_gate_id: *warp_gate_id },
        }
    }
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadCommands {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub action: NativePlayerSupportAction,
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadFire {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    /// Manual point-fire, or None to release it. Does not replace movement or
    /// the squad's current command and never manufactures a policy decision.
    pub target: Option<Vec2>,
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerHeldFire {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub action: NativeHeldFireAction,
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub enum NativeHeldFireAction {
    Begin { squads: Vec<SquadLifetime>, target: Vec2 },
    Aim { target: Vec2 },
    End,
}

/// Player Cease Fire / Attack on Sight changes only the same native weapon
/// runtime used by headless simulation. It does not replace or settle an
/// existing movement/strategy command.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadCeaseFire {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub cease_fire: bool,
}

/// Unity SquadActionBox.Chase changes a persistent user preference:
/// the one-second source timer starts an Aggressive command only when a
/// visible nearest enemy exists. Hold disables this preference but never
/// fabricates a Hold command for an otherwise idle squad.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadChase {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub chasing: bool,
}

pub struct NativePlayerChasePlan {
    tick: u64,
    owner: SquadLifetime,
    world_id: WorldId,
    episode_revision: u64,
    selected_ship: i64,
    chasing: bool,
}

impl NativePlayerChasePlan {
    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, world_id: WorldId,
        episode_revision: u64, selected_ship: i64, chasing: bool,
    ) -> Result<Self, NativePlayerInputError> {
        let (owner, _) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        Ok(Self { tick: world.tick(), owner, world_id,
            episode_revision, selected_ship, chasing })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<(), NativePlayerInputError> {
        let current = Self::prepare(world, self.world_id,
            self.episode_revision, self.selected_ship, self.chasing)?;
        if current.tick != self.tick || current.owner != self.owner {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        if let Err(error) = state.metadata.set_player_chasing(self.owner, self.chasing) {
            state.events.fail_player_input();
            return Err(NativePlayerInputError::Source(error));
        }
        Ok(())
    }
}

/// Unity SquadActionBox.LockOn toggles only a squad with an enemy
/// command. Locked squads remain selectable and can receive new commands;
/// LevelInputManager excludes them from ordinary ground movement only.
/// Store the chosen boolean in the recorded input, not an inferred toggle.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadLockOn {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub locked: bool,
}

pub struct NativePlayerLockOnPlan {
    tick: u64,
    owner: SquadLifetime,
    world_id: WorldId,
    episode_revision: u64,
    selected_ship: i64,
    locked: bool,
}

impl NativePlayerLockOnPlan {
    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, world_id: WorldId,
        episode_revision: u64, selected_ship: i64, locked: bool,
    ) -> Result<Self, NativePlayerInputError> {
        let (owner, _) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        let permission = world.state.metadata.player_permissions(owner)
            .ok_or(NativePlayerInputError::NotControllable)?;
        if !world.state.events.commands().selected_enemy(owner).is_some() ||
            permission.locked_on == locked
        {
            return Err(NativePlayerInputError::InvalidAction);
        }
        Ok(Self { tick: world.tick(), owner, world_id,
            episode_revision, selected_ship, locked })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<(), NativePlayerInputError> {
        let current = Self::prepare(world, self.world_id,
            self.episode_revision, self.selected_ship, self.locked)?;
        if current.tick != self.tick || current.owner != self.owner {
            return Err(NativePlayerInputError::StaleWorld);
        }
        world.state.metadata.set_player_locked_on(self.owner, self.locked)
            .map_err(|error| {
                world.state.events.fail_player_input();
                NativePlayerInputError::Source(error)
            })
    }
}

/// Change the chosen shooting target priority of an existing source squad.
    /// Its weapon target ranking and any active Command share one strategy,
    /// and the player trace records the explicit ID for deterministic replay.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadShooting {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    /// 1-40 in the shared source SHOOTING_STRATEGIES registry.
    pub strategy_id: u8,
}

pub struct NativePlayerShootingPlan {
    tick: u64,
    owner: SquadLifetime,
    selected_ship: i64,
    world_id: WorldId,
    episode_revision: u64,
    shooting: bees_hivemind::protocol::StrategyIdentity,
}

impl NativePlayerShootingPlan {
    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, world_id: WorldId,
        episode_revision: u64, selected_ship: i64, strategy_id: u8,
    ) -> Result<Self, NativePlayerInputError> {
        let (owner, _) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        let Some(&name) = strategy_id.checked_sub(1).and_then(|index|
            bees_hivemind::protocol::SHOOTING_STRATEGIES.get(index as usize)
        ) else { return Err(NativePlayerInputError::InvalidAction); };
        let shooting = bees_hivemind::protocol::StrategyIdentity {
            id: strategy_id, name,
        };
        // The metadata must own a real mutable source squad preference.
        // Never create a UI-only value that disagrees with weapons.
        world.state.metadata.player_command_inputs(owner)
            .map_err(|error| NativePlayerInputError::Source(error))?;
        Ok(Self {
            tick: world.tick(), owner, selected_ship, world_id,
            episode_revision, shooting,
        })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<(), NativePlayerInputError> {
        let current = Self::prepare(world, self.world_id,
            self.episode_revision, self.selected_ship, self.shooting.id)?;
        if current.tick != self.tick || current.owner != self.owner {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        // Source first changes the squad/ships' chosen strategy, then
        // refreshes the current Command's live shooting preference.
        if let Err(error) = state.metadata.set_player_shooting_strategy(
            self.owner, self.shooting,
        ) {
            state.events.fail_player_input();
            return Err(NativePlayerInputError::Source(error));
        }
        if let Err(error) = state.events.commands_mut().set_player_shooting(
            self.owner, self.shooting,
        ) {
            state.events.fail_player_input();
            return Err(NativePlayerInputError::Source(error));
        }
        Ok(())
    }
}

/// Unity Squad.MatchSpeed / UnmatchSpeed. The host calculates the one
/// minimum authored speed over all selected, eligible squads; replaying
/// the same selection reproduces that speed without cached UI state.
/// The user chooses an explicit mode, so playback does not guess UI
/// toggle state after a squad loses a ship.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadMatchSpeed {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub match_speed: bool,
}

pub struct NativePlayerMatchSpeedPlan {
    tick: u64,
    owner: SquadLifetime,
    selected_ship: i64,
    world_id: WorldId,
    episode_revision: u64,
    match_speed: bool,
    /// Live source squad GetShips order; revalidated before mutation.
    members: Vec<i64>,
    speed: f32,
}

impl NativePlayerMatchSpeedPlan {
    /// The source action box chooses one minimum over ALL selected squads.
    /// Do not silently calculate a different speed for each squad.
    pub fn slowest_authored_speed(&self) -> f32 { self.speed }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>, world_id: WorldId,
        episode_revision: u64, selected_ship: i64, match_speed: bool,
    ) -> Result<Self, NativePlayerInputError> {
        let (owner, squad) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        let mut members = Vec::with_capacity(squad.members.len());
        let mut minimum_speed = f32::INFINITY;
        for &id in &squad.members {
            // Speed setting is not a movement order: stationary ships
            // do not need a pathfinder or steering profile.
            world.state.combat.validate_ship_game_speed(id)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            let body = world.state.combat.ship_body(id)
                .ok_or(NativePlayerInputError::MissingSquad)?;
            if !body.motion.base_speed.is_finite() || body.motion.base_speed < 0.0 {
                return Err(NativePlayerInputError::InvalidAction);
            }
            minimum_speed = minimum_speed.min(body.motion.base_speed);
            members.push(id);
        }
        if members.is_empty() { return Err(NativePlayerInputError::MissingSquad); }
        Ok(Self {
            tick: world.tick(), owner, selected_ship, world_id,
            episode_revision, match_speed, members,
            speed: if match_speed { minimum_speed } else { 0.0 },
        })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
        shared_slowest_speed: f32,
    ) -> Result<usize, NativePlayerInputError> {
        let current = Self::prepare(world, self.world_id,
            self.episode_revision, self.selected_ship, self.match_speed)?;
        if current.tick != self.tick || current.owner != self.owner ||
            current.members != self.members || current.speed != self.speed {
            return Err(NativePlayerInputError::StaleWorld);
        }
        if self.match_speed && (!shared_slowest_speed.is_finite() ||
            shared_slowest_speed < 0.0 || shared_slowest_speed > self.speed) {
            return Err(NativePlayerInputError::InvalidAction);
        }
        for id in &self.members {
            let requested_speed = if self.match_speed {
                shared_slowest_speed
            } else {
                world.state.combat.ship_body(*id)
                    .ok_or(NativePlayerInputError::MissingSquad)?
                    .motion.base_speed
            };
            if let Err(error) = world.state.combat
                .set_ship_game_speed(*id, requested_speed)
            {
                world.state.events.fail_player_input();
                return Err(NativePlayerInputError::Source(error.into()));
            }
        }
        // Source Squad.MatchSpeed and UnmatchSpeed persist as squad state;
        // the gameplay ShipMotion remains the only owner of per-ship speeds.
        let matched = self.match_speed.then_some(shared_slowest_speed);
        if let Err(error) = world.state.metadata
            .set_player_matched_speed(self.owner, matched)
        {
            world.state.events.fail_player_input();
            return Err(NativePlayerInputError::Source(error));
        }
        Ok(self.members.len())
    }
}

/// Authoritative player ship-special input. Like cease-fire, this affects
/// physical abilities without replacing a squad strategy or retiring its
/// pending HiveMind command. The queued effects execute inside the next
/// fixed step, where normal combat events become visible to every consumer.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum NativePlayerShipSpecialKind {
    DropBeacon,
    Detonate,
    Charge,
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NativePlayerSquadSpecial {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub squads: Vec<SquadLifetime>,
    pub kind: NativePlayerShipSpecialKind,
}

pub struct NativePlayerSpecialPlan {
    tick: u64,
    owner: SquadLifetime,
    world_id: WorldId,
    episode_revision: u64,
    selected_ship: i64,
    kind: NativePlayerShipSpecialKind,
    eligible_ships: Vec<i64>,
}

impl NativePlayerSpecialPlan {
    pub fn owner(&self) -> SquadLifetime { self.owner }
    pub fn is_available(&self) -> bool { !self.eligible_ships.is_empty() }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>,
        world_id: WorldId, episode_revision: u64, selected_ship: i64,
        kind: NativePlayerShipSpecialKind,
    ) -> Result<Self, NativePlayerInputError> {
        // Physical specials are legal even when movement is locked by an
        // active strategy. Reuse world/lifetime/player authority checks,
        // but never require an unrelated formation or path-clearance plan.
        let (owner, squad) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        let eligible_ships = squad.members.iter().filter_map(|&ship_id| {
            let ship = world.state.combat.ship(ship_id)?;
            let body = world.state.combat.ship_body(ship_id)?;
            if !body.active || ship.vitals.is_dead() ||
                body.motion.is_policy_controlled ||
                world.state.combat.policy_frame(ship_id).is_some() {
                return None;
            }
            let eligible = match kind {
                NativePlayerShipSpecialKind::DropBeacon =>
                    ship.ship_type == 18 &&
                    world.state.combat.player_scout_beacon_ready(ship_id),
                NativePlayerShipSpecialKind::Detonate =>
                    ship.ship_type == 10 &&
                    world.state.combat.player_fire_barge_detonation_ready(ship_id),
                NativePlayerShipSpecialKind::Charge =>
                    ship.ship_type == 0 &&
                    world.state.combat.player_game_barge_charge_ready(ship_id),
            };
            eligible.then_some(ship_id)
        }).collect();
        Ok(Self {
            tick: world.tick(), owner, world_id, episode_revision,
            selected_ship, kind, eligible_ships,
        })
    }

    /// Recheck immutable ownership and live readiness before queueing.
    /// No projectile/death/spawn event is emitted from the UI thread.
    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<usize, NativePlayerInputError> {
        let current = Self::prepare(world, self.world_id,
            self.episode_revision, self.selected_ship, self.kind)?;
        if current.tick != self.tick || current.owner != self.owner ||
            current.eligible_ships != self.eligible_ships {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let mut queued = 0usize;
        for ship_id in self.eligible_ships {
            let result = match self.kind {
                NativePlayerShipSpecialKind::DropBeacon =>
                    world.state.combat.queue_player_scout_beacon(ship_id),
                NativePlayerShipSpecialKind::Detonate =>
                    world.state.combat.queue_player_fire_barge_detonation(ship_id),
                NativePlayerShipSpecialKind::Charge =>
                    world.state.combat.queue_player_barge_charge(ship_id),
            };
            match result {
                Ok(true) => queued += 1,
                Ok(false) => {},
                Err(error) => {
                    world.state.events.fail_player_input();
                    return Err(NativePlayerInputError::Source(error.into()));
                }
            }
        }
        Ok(queued)
    }
}

pub struct NativePlayerCeaseFirePlan {
    tick: u64,
    owner: SquadLifetime,
    members: Vec<i64>,
    world_id: WorldId,
    episode_revision: u64,
    selected_ship: i64,
    cease_fire: bool,
}

impl NativePlayerCeaseFirePlan {
    pub fn owner(&self) -> SquadLifetime { self.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &World<NativeLevelState<M, A>>,
        world_id: WorldId, episode_revision: u64, selected_ship: i64,
        cease_fire: bool,
    ) -> Result<Self, NativePlayerInputError> {
        // CeaseFire changes weapons without moving a squad. The old
        // formation preflight rejected stationary ships and squads missing
        // pathfinder clearance even though Unity accepts their fire mode.
        let (owner, squad) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        // Source SquadActionBox.AttackOnSight skips Heal squads rather than
        // implicitly canceling their healing command or firing during Heal.
        if !cease_fire &&
            world.state.events.commands().current_kind(owner) ==
                Some(bees_hivemind::dispatch::LocalCommandKind::Heal)
        {
            return Err(NativePlayerInputError::InvalidAction);
        }
        let mut members = Vec::with_capacity(squad.members.len());
        for &ship_id in &squad.members {
            if !world.state.combat.ship(ship_id)
                .is_some_and(|ship| !ship.vitals.is_dead()) ||
                !world.state.combat.ship_body(ship_id)
                    .is_some_and(|body| body.active) {
                return Err(NativePlayerInputError::MissingSquad);
            }
            members.push(ship_id);
        }
        if members.is_empty() { return Err(NativePlayerInputError::MissingSquad); }
        Ok(Self {
            tick: world.tick(), owner, members, world_id,
            episode_revision, selected_ship, cease_fire,
        })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<usize, NativePlayerInputError> {
        let current = Self::prepare(
            world, self.world_id, self.episode_revision, self.selected_ship,
            self.cease_fire,
        )?;
        if current.tick != self.tick || current.owner != self.owner ||
            current.members != self.members {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let state = &mut world.state;
        for &ship_id in &self.members {
            let result = if state.combat.policy_frame(ship_id).is_some() {
                state.combat.set_policy_ship_cease_fire(ship_id, self.cease_fire)
            } else {
                state.combat.set_ship_cease_fire(ship_id, self.cease_fire)
            };
            if let Err(error) = result {
                state.events.fail_player_input();
                return Err(NativePlayerInputError::Source(error.into()));
            }
        }
        if let Err(error) = state.metadata
            .set_player_cease_fire(self.owner, self.cease_fire)
        {
            state.events.fail_player_input();
            return Err(NativePlayerInputError::Source(error));
        }
        Ok(self.members.len())
    }
}

pub struct NativePlayerFirePlan {
    authority: NativePlayerSquadPlan,
    selected_ship: i64,
    world_id: WorldId,
    episode_revision: u64,
    target: Option<Vec2>,
}

impl NativePlayerFirePlan {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(world: &World<NativeLevelState<M, A>>,
        world_id: WorldId, episode_revision: u64, selected_ship: i64, target: Option<Vec2>,
    ) -> Result<Self, NativePlayerInputError> {
        if target.is_some_and(|point| !point.is_finite()) {
            return Err(NativePlayerInputError::InvalidAction);
        }
        let authority = NativePlayerSquadPlan::prepare(world, world_id, episode_revision, selected_ship)?;
        for member in &authority.members {
            if world.state.combat.policy_frame(member.ship_id).is_some() {
                return Err(NativePlayerInputError::NotControllable);
            }
            world.state.combat.validate_game_manual_fire(member.ship_id, target)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
        }
        Ok(Self { authority, world_id, episode_revision, selected_ship, target })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(self, world: &mut World<NativeLevelState<M, A>>)
        -> Result<usize, NativePlayerInputError>
    {
        self.apply(world, false)
    }

    pub fn begin_held_fire<M: NativeLevelPlayerContext, A>(self, world: &mut World<NativeLevelState<M, A>>)
        -> Result<usize, NativePlayerInputError>
    {
        self.apply(world, true)
    }

    fn apply<M: NativeLevelPlayerContext, A>(
        self, world: &mut World<NativeLevelState<M, A>>, held: bool,
    ) -> Result<usize, NativePlayerInputError>
    {
        let current = Self::prepare(world, self.world_id, self.episode_revision,
            self.selected_ship, self.target)?;
        if current.authority != self.authority { return Err(NativePlayerInputError::StaleWorld); }
        let mut weapons = 0;
        for member in &self.authority.members {
            let state = &mut world.state;
            let result = (|| {
                // The source manual-fire hotkey stops a selected Flagship on
                // key-down so its main cannon can turn the hull.
                if held && member.mobile && state.combat.ship(member.ship_id)
                    .is_some_and(|ship| ship.ship_type == FLAGSHIP_SHIP_TYPE)
                {
                    state.combat.stop_ship_game_movement(member.ship_id)?;
                }
                state.combat.set_game_manual_fire(member.ship_id, self.target)
            })();
            match result {
                Ok(count) => {
                    weapons += count;
                    if held && count > 0 { state.held_fire_ships.push(member.ship_id); }
                },
                Err(error) => {
                    world.state.events.fail_player_input();
                    return Err(NativePlayerInputError::Source(error.into()));
                },
            }
        }
        Ok(weapons)
    }
}

impl<M, A> NativeLevelState<M, A> {
    pub fn has_held_fire(&self) -> bool { !self.held_fire_ships.is_empty() }

    /// Follow or release the original live ships even if selection or the
    /// squad roster changed after key-down. Episode turnover owns the reset.
    pub fn update_held_fire(&mut self, target: Option<Vec2>) -> Result<(), NativePlayerInputError> {
        self.held_fire_ships.retain(|&id| self.combat.ship_body(id).is_some_and(|body| body.active) &&
            self.combat.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) && self.combat.policy_frame(id).is_none());
        for &id in &self.held_fire_ships {
            if let Err(error) = self.combat.set_game_manual_fire(id, target) {
                self.events.fail_player_input();
                return Err(NativePlayerInputError::Source(error.into()));
            }
        }
        if target.is_none() { self.held_fire_ships.clear(); }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePlayerCommandCounts {
    pub accepted_squads: usize,
    pub unavailable_squads: usize,
    pub rejected_squads: usize,
    /// Source Hold finalized this many actual Aggressive commands.
    /// A harmless click on another command is still an accepted input.
    pub stopped_chases: usize,
}

#[derive(Debug)]
pub enum NativePlayerCommandOutcome {
    Command(crate::commands::Activation),
    Move { reachable: bool },
    StopChasing { stopped: bool },
}

pub struct NativePlayerCommandPlan<'a> {
    execution: NativePlayerCommandExecution<'a>,
}

enum NativePlayerCommandExecution<'a> {
    /// Hold changes command/chase state, never formation movement.
    /// It must not require a pathfinder or mutable ShipMotion profile.
    Hold { owner: SquadLifetime, tick: u64,
        input: NativePlayerCommand<'a>, was_chasing: bool },
    Command(NativePlayerSupportCommandPlan<'a>),
    Move { plan: NativePlayerMovePlan, asteroid_id: i64 },
}

impl<'a> NativePlayerCommandPlan<'a> {
    pub fn owner(&self) -> SquadLifetime {
        match &self.execution {
            NativePlayerCommandExecution::Hold { owner, .. } => *owner,
            NativePlayerCommandExecution::Command(plan) => plan.owner(),
            NativePlayerCommandExecution::Move { plan, .. } => plan.owner(),
        }
    }

    /// A Hold click on a non-Aggressive squad is source-defined as a
    /// no-op. It must not retire an unrelated pending HiveMind request or
    /// invalidate the policy environment.
    pub fn retires_pending_authority(&self) -> bool {
        match &self.execution {
            NativePlayerCommandExecution::Hold { was_chasing, .. } => *was_chasing,
            _ => true,
        }
    }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &mut World<NativeLevelState<M, A>>, input: NativePlayerCommand<'a>,
    ) -> Result<Self, NativePlayerInputError> {
        use crate::commands::NativeLocalCommandAction as Action;
        if matches!(input.action, Action::Hold) {
            let (owner, _) = authorized_player_squad(
                world, input.world_id, input.episode_revision, input.selected_ship,
            )?;
            let was_chasing = world.state.events.commands().is_chasing_command(owner);
            return Ok(Self { execution: NativePlayerCommandExecution::Hold {
                owner, tick: world.tick(), input, was_chasing,
            } });
        }
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
            NativePlayerCommandExecution::Hold { owner, tick, input, was_chasing } => {
                let (current, _) = authorized_player_squad(
                    world, input.world_id, input.episode_revision, input.selected_ship,
                )?;
                if current != owner || world.tick() != tick ||
                    world.state.events.commands().is_chasing_command(owner) != was_chasing {
                    return Err(NativePlayerInputError::StaleWorld);
                }
                let state = &mut world.state;
                let result = state.events.stop_player_chasing(
                    tick, &mut state.combat, owner,
                ).map_err(NativePlayerInputError::Finalization);
                if result.is_err() { state.events.fail_player_input(); }
                result.map(|stopped| NativePlayerCommandOutcome::StopChasing { stopped })
            }
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
    enemy: Option<crate::commands::NativeEnemySquad>,
    action: crate::commands::NativeLocalCommandAction<'a>,
    bombing: Option<(bool, Vec<crate::commands::NativeBombWeapon>, Vec<crate::commands::NativeEnemyTargetStats>)>,
    friendlies: Vec<crate::commands::NativeFriendlySquad>,
}

impl<'a> NativePlayerSupportCommandPlan<'a> {
    pub fn owner(&self) -> SquadLifetime { self.authority.owner }

    pub fn prepare<M: NativeLevelPlayerContext, A>(
        world: &mut World<NativeLevelState<M, A>>, input: NativePlayerCommand<'a>,
    ) -> Result<Self, NativePlayerInputError> {
        use crate::commands::NativeLocalCommandAction as Action;
        match input.action {
            Action::TargetEnemy { .. } => (),
            Action::Guard { .. } | Action::Mining { .. } | Action::Heal { .. } |
            Action::FullRetreat { .. } => (),
            Action::Patrol { top_left, bottom_right } if top_left.is_finite() &&
                bottom_right.is_finite() && top_left.x <= bottom_right.x &&
                top_left.y >= bottom_right.y => (),
            _ => return Err(NativePlayerInputError::InvalidAction),
        }
        let authority = NativePlayerSquadPlan::prepare(world, input.world_id,
            input.episode_revision, input.selected_ship)?;
        let mut action = input.action;
        let mut bombing = None;
        let enemy = if let Action::TargetEnemy { target } = input.action {
            let roster = world.state.events.roster();
            let own = roster.get(authority.owner).ok_or(NativePlayerInputError::MissingSquad)?;
            let target_squad = roster.get(target).ok_or(NativePlayerInputError::InvalidAction)?;
            if target.level_id != authority.owner.level_id || own.side == target_squad.side {
                return Err(NativePlayerInputError::InvalidAction);
            }
            let composition = bees_hivemind::dispatch::SquadCommandComposition::from_unity_ship_types(
                own.members.iter().filter_map(|&id| world.state.combat.ship(id).map(|ship| i32::from(ship.ship_type))));
            let details = world.state.metadata.strategy_inputs(&world.state.combat,
                roster, authority.owner, Some(target)).map_err(NativePlayerInputError::Source)?;
            let enemy = details.enemy.filter(|enemy| enemy.owner == target && enemy.is_alive)
                .ok_or(NativePlayerInputError::InvalidAction)?;
            if composition.only_barges || composition.only_bombers {
                action = if composition.only_barges { Action::Charge } else { Action::BombingRun };
                let mut weapons = Vec::new(); let mut enemies = Vec::new();
                let carrier = world.state.metadata.collect_bomb_activation(&world.state.combat, roster,
                    authority.owner, target, &mut weapons, &mut enemies).map_err(NativePlayerInputError::Source)?;
                bombing = Some((carrier, weapons, enemies));
            } else {
                world.state.metadata.aggressive_command_snapshot(&mut world.state.combat, roster,
                    authority.owner, target, false).map_err(NativePlayerInputError::Source)?;
            }
            Some(enemy)
        } else { None };
        let source = world.state.metadata.player_command_inputs(authority.owner)
            .map_err(NativePlayerInputError::Source)?;
        if !source.settings.valid() {
            return Err(NativePlayerInputError::Source(GameCommandError::InvalidSettings));
        }
        // Validate source identity before any reservation retirement or mutation.
        crate::commands::NativeCommandSelection::local(
            crate::commands::NativeLocalCommandIdentity {
                source: crate::commands::NativeLocalCommandSource::Player, sequence: 0,
            }, action.kind(), source.shooting)
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
        Ok(Self { input, authority, source, center, enemy, action, bombing, friendlies })
    }

    pub fn execute<M: NativeLevelPlayerContext, A>(self,
        world: &mut World<NativeLevelState<M, A>>,
    ) -> Result<crate::commands::Activation, NativePlayerInputError> {
        let current = Self::prepare(world, self.input)?;
        if current.authority != self.authority || current.source != self.source ||
            current.center != self.center || current.enemy != self.enemy {
            return Err(NativePlayerInputError::StaleWorld);
        }
        let owner = self.owner();
        let tick = world.tick();
        let state = &mut world.state;
        let result = (|| {
            let squad = crate::commands::NativeSquadOrder {
                targeting: state.events.roster().get(owner).and_then(|squad| squad.targeting),
                owner, members: &self.authority.members, squad_center: self.center,
                formation: self.authority.options, enemy: self.enemy, scouting: None,
            };
            state.metadata.cancel_player_scripted_queue(owner)
                .map_err(NativePlayerInputError::Source)?;
            let asteroids = state.metadata.collision_asteroids_enabled();
            let navigation = state.metadata.navigation_mut();
            state.combat.refresh_world_navigation(navigation, true, asteroids)
                .map_err(|error| NativePlayerInputError::Source(error.into()))?;
            state.events.activate_local_command(tick, &mut state.combat, navigation,
                &mut world.random, squad, &current.friendlies, self.source.settings,
                crate::commands::NativeLocalCommandSource::Player, current.action,
                self.source.shooting, current.bombing.as_ref().map(|(carrier, weapons, enemies)|
                    crate::level_events::NativeLocalBombingTargets {
                        enemy_owner: self.enemy.expect("targeted special command").owner,
                        weapons, enemies, is_carrier_squad: *carrier,
                    })).map_err(NativePlayerInputError::Finalization)
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

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
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

/// Shared authoritative player/world/lifetime checks for all command
/// classes. Physical specials deliberately do not depend on movement
/// eligibility, but they obey the exact same player ownership contract.
fn authorized_player_squad<'a, M: NativeLevelPlayerContext, A>(
    world: &'a World<NativeLevelState<M, A>>, world_id: WorldId,
    episode_revision: u64, selected_ship: i64,
) -> Result<(SquadLifetime, &'a crate::squads::NativeSquadEntry), NativePlayerInputError> {
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
    // Unity permits tactical commands and action-box changes while
    // locked; only LevelInputManager.MoveSquads excludes a locked squad.
    if !permission.user_controlled || !permission.can_accept_input {
        return Err(NativePlayerInputError::NotControllable);
    }
    let squad = state.events.roster().get(owner)
        .ok_or(NativePlayerInputError::MissingSquad)?;
    Ok((owner, squad))
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
        let (owner, squad) = authorized_player_squad(
            world, world_id, episode_revision, selected_ship,
        )?;
        let state = &world.state;
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
        if world.state.metadata.player_permissions(authority.owner)
            .is_some_and(|permission| permission.locked_on)
        {
            return Err(NativePlayerInputError::NotControllable);
        }
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
