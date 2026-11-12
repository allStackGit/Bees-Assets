//! One executable lifecycle for a rendered native Level or headless worker.
//! Both run the SAME simulation, batched policy model, HiveMind correlation
//! session and durable notification sink. No placeholder actions, alternate
//! physics, or shadow command queues live here.

use std::num::NonZeroU32;
use bees_gameplay::inference::NativePolicyInference;
use bees_gameplay::level_host::{
    NativeHiveMindFrameDelivery, NativeLevelActivationContext,
    NativeLevelBatchDriver, NativeLevelDriverError, NativeLevelDriverResetError,
    NativeLevelNotificationSink, NativeLevelObservationContext,
    NativeLevelRoutingError, NativeLevelStopDelivery, NativeLevelStopError,
    NativeLevelStopKind, NativeLevelWindowCounts, NativeLevelWindowError,
    NativeRuntimeStatus,
};
use bees_gameplay::level_rules::{
    NativeLevelRewardMetadata, NativeLevelRules, NativeLevelState,
};
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::outcomes::OutcomeBatch;
use bees_hivemind::protocol::{
    GetStrategyRequest, GetTargetingRequest, StoreCommandsRequest,
};
use bees_hivemind::session::{SquadLifetime, TargetingReceipt};
use bees_sim::simulation::{RandomStream, Simulation, World};
use crate::durable_connection::{DurableConnectionError, DurableHiveMindConnection};
use crate::status::{NativeHostStatusInterval, NativeStatusSampler};
use crate::presentation::NativeRenderWorldView;
use bees_gameplay::player_inputs::{NativeLevelPlayerContext, NativePlayerMove,
    NativePlayerMovePlan, NativePlayerInputError, NativePlayerAuthority,
    NativePlayerSquadMoves, NativePlayerMoveCounts};

#[derive(Debug)]
pub enum HostPlayerInputError {
    Gameplay(NativePlayerInputError),
    Connection(DurableConnectionError),
    Driver(NativeLevelDriverError),
}

#[derive(Debug)]
pub enum HostInitError {
    Driver(NativeLevelDriverError),
    Status(NativeLevelRoutingError),
}

#[derive(Debug)]
pub enum HostWindowError<Source, Model, Sink> {
    Connection(DurableConnectionError),
    Window(NativeLevelWindowError<Source, Model, Sink>),
    WindowAndConnection {
        window: NativeLevelWindowError<Source, Model, Sink>,
        connection: DurableConnectionError,
    },
}

#[derive(Debug)]
pub enum HostStopError<E> {
    Connection(DurableConnectionError),
    Stop(NativeLevelStopError<E>),
    StopAndConnection { stop: NativeLevelStopError<E>, connection: DurableConnectionError },
}

#[derive(Debug)]
pub enum HostRegistrationError {
    Simulation(&'static str),
    Driver(NativeLevelDriverError),
}

#[derive(Debug)]
pub enum HostResetError {
    Connection(DurableConnectionError),
    Reset(NativeLevelDriverResetError),
}

/// Every recognized non-setup frame has already reached the authoritative
/// Level/connection. Targeting and ban application still return to their
/// gameplay owner; automatic stale-reservation discard is already durable.
#[derive(Debug)]
pub enum NativeHostSocketEvent {
    SetupAccepted { request_hash: i64, server_game_id: i64 },
    ServerFrame {
        request_hash: i64,
        delivery: Option<NativeHiveMindFrameDelivery>,
        /// Recognized replies are already routed. Other authenticated
        /// features retain their exact bytes for the caller's real owner.
        unhandled_frame: Option<Vec<u8>>,
    },
}

#[derive(Debug)]
pub enum HostNetworkError {
    Connection(DurableConnectionError),
    Transport {
        error: crate::socket_worker::NativeSocketWorkerError,
        unresolved_requests: Vec<i64>,
        retirement_error: Option<DurableConnectionError>,
    },
}

/// Source Hive Mind's 0.25s queue-timer handoff. Counters are aggregated
/// only on timer pulses, not during per-ship physics or inference steps.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSourceCommandPulse {
    pub eligible: usize,
    pub issued: usize,
    pub out_of_bounds: usize,
    pub home_moves: usize,
    pub home_unreachable: usize,
    pub unauthored_minions: usize,
}

#[derive(Debug)]
pub enum HostSourceCommandError {
    MissingStageAuthorization { index: usize },
    MissingSquadAuthorization(SquadLifetime),
    InvalidSquadSide(SquadLifetime),
    Geometry {
        owner: SquadLifetime,
        error: bees_sim::combat_world::CombatWorldError,
    },
    SourceOrder {
        owner: SquadLifetime,
        error: bees_gameplay::commands::GameCommandError,
    },
    Connection(DurableConnectionError),
    Driver(NativeLevelRoutingError),
}

/// Accepts actual authored Level metadata and a loaded, real inference
/// backend. Its one notification sink must commit accepted notifications
/// durably before returning Ok to the shared Level driver.
pub struct NativeHostRuntime<M, A, B, S> {
    simulation: Simulation<NativeLevelState<M, A>, NativeLevelRules>,
    driver: NativeLevelBatchDriver,
    model: B,
    connection: DurableHiveMindConnection,
    sink: S,
    sampler: NativeStatusSampler,
}

impl<M, A, B, S> NativeHostRuntime<M, A, B, S>
where
    M: NativeLevelRewardMetadata + NativeLevelActivationContext +
       NativeLevelObservationContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    B: NativePolicyInference,
    S: NativeLevelNotificationSink,
{
    /// Build once after registering all initial Level worlds with Simulation.
    /// The driver seeds per-episode model identity from each world RNG.
    pub fn new(
        mut simulation: Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        model: B,
        connection: DurableHiveMindConnection,
        sink: S,
    ) -> Result<Self, HostInitError> {
        let driver = NativeLevelBatchDriver::from_simulation(&mut simulation)
            .map_err(HostInitError::Driver)?;
        let baseline = driver.runtime_status(&simulation, connection.connection())
            .map_err(HostInitError::Status)?;
        Ok(Self {
            simulation, driver, model, connection, sink,
            sampler: NativeStatusSampler::new(baseline),
        })
    }

    /// Add an independently seeded authored Level after runtime startup.
    /// Preflight before modifying Simulation so a rejected Level cannot
    /// leave the model driver and world indices out of sync.
    pub fn add_world(
        &mut self,
        world: World<NativeLevelState<M, A>>,
    ) -> Result<usize, HostRegistrationError> {
        if self.driver.registered_world_count() != self.simulation.world_count() {
            return Err(HostRegistrationError::Driver(
                NativeLevelDriverError::IndexOutOfOrder,
            ));
        }
        if world.tick() != 0 || world.is_failed() ||
            world.state.events.is_failed() || world.state.events.completed()
        {
            return Err(HostRegistrationError::Driver(
                NativeLevelDriverError::UnstartedWorldRequired,
            ));
        }
        let index = self.simulation.add_world(world)
            .map_err(HostRegistrationError::Simulation)?;
        self.driver.register_added_world(&mut self.simulation, index)
            .map_err(HostRegistrationError::Driver)?;
        Ok(index)
    }

    /// Game/editor/scenario input may change the true Level state between
    /// synchronous inference/physics windows. Borrow only state and its
    /// world-owned RandomStream, not the World identity or reset methods.
    /// Invalidate observation-side environment projections on every edit;
    /// the caller must still preserve gameplay/asset invariants.
    pub fn edit_level<R>(
        &mut self,
        index: usize,
        edit: impl FnOnce(&mut NativeLevelState<M, A>, &mut RandomStream) -> R,
    ) -> Result<R, NativeLevelDriverError> {
        self.driver.invalidate_environment(index)?;
        let world = self.simulation.world_mut(index)
            .ok_or(NativeLevelDriverError::MissingWorld)?;
        Ok(edit(&mut world.state, &mut world.random))
    }

    pub fn player_move(&mut self, input: NativePlayerMove)
        -> Result<bool, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        let plan = NativePlayerMovePlan::prepare(world, input)
            .map_err(HostPlayerInputError::Gameplay)?;
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        self.connection.retire_player_requests(plan.owner())
            .map_err(HostPlayerInputError::Connection)?;
        plan.execute(self.simulation.world_mut(index).expect("located world"))
            .map_err(HostPlayerInputError::Gameplay)
    }

    /// Chosen player support actions use the same authority, command director,
    /// request retirement and durable settlement path as graphical movement.
    pub fn player_command(&mut self,
        input: bees_gameplay::player_inputs::NativePlayerCommand<'_>,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandOutcome, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let plan = bees_gameplay::player_inputs::NativePlayerCommandPlan::prepare(
            self.simulation.world_mut(index).expect("located world"), input)
            .map_err(HostPlayerInputError::Gameplay)?;
        if plan.retires_pending_authority() {
            self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
            self.connection.retire_player_requests(plan.owner())
                .map_err(HostPlayerInputError::Connection)?;
        }
        plan.execute(self.simulation.world_mut(index).expect("located world"))
            .map_err(HostPlayerInputError::Gameplay)
    }

    /// Source MoveSquads selection order; duplicate lifetimes execute once.
    /// Unavailable/locked squads are skipped without commandeering AI ships.
    /// Prepare every eligible squad before retiring or issuing its first order.
    pub fn player_move_squads(&mut self, input: NativePlayerSquadMoves)
        -> Result<NativePlayerMoveCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        if !input.destination.is_finite() {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::InvalidDestination));
        }
        let mut counts = NativePlayerMoveCounts::default();
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        let mut plans = Vec::with_capacity(input.squads.len());
        for owner in input.squads {
            if !seen.insert(owner) { continue; }
            let Some(ship_id) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerMovePlan::prepare(world, NativePlayerMove {
                world_id: input.world_id, episode_revision: input.episode_revision,
                selected_ship: ship_id, destination: input.destination,
            }) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            self.connection.retire_player_requests(plan.owner())
                .map_err(HostPlayerInputError::Connection)?;
            if plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)? {
                counts.accepted_squads += 1;
            } else { counts.unreachable_squads += 1; }
        }
        Ok(counts)
    }

    /// Preflight the selected lifetimes in selection order before issuing the
    /// first support action. Repeated identities execute once; each actual
    /// activation re-reads shared resource/target state after its predecessor.
    pub fn player_command_squads(&mut self,
        input: &bees_gameplay::player_inputs::NativePlayerSquadCommands,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{NativePlayerCommand, NativePlayerCommandPlan,
            NativePlayerCommandCounts};
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world_mut(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        let mut plans = Vec::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerCommandPlan::prepare(world, NativePlayerCommand {
                world_id: input.world_id, episode_revision: input.episode_revision,
                selected_ship, action: input.action.as_local_action(),
            }) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        if input.action == bees_gameplay::player_inputs::NativePlayerSupportAction::Hold ||
            plans.iter().any(|plan| plan.retires_pending_authority())
        {
            // Hold changes persistent ShouldChase even when no Aggressive
            // command needs retirement; invalidate stale environment views
            // before the first metadata mutation.
            self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        }
        for plan in plans {
            let owner = plan.owner();
            let replacing_command = plan.retires_pending_authority();
            if replacing_command {
                self.connection.retire_player_requests(owner)
                    .map_err(HostPlayerInputError::Connection)?;
            }
            let outcome = plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            let stopped_chase = matches!(
                outcome,
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::StopChasing { stopped: true },
            );
            match outcome {
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::Command(
                    bees_gameplay::commands::Activation::Applied { .. }) |
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::Move { reachable: true } =>
                    counts.accepted_squads += 1,
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::StopChasing { stopped } => {
                    counts.accepted_squads += 1;
                    counts.stopped_chases += usize::from(stopped);
                },
                _ => counts.unavailable_squads += 1,
            }
            // A replaced command finalizes immediately even when the
            // game is paused. Timed completions are handled by Level.step.
            if replacing_command || stopped_chase {
                self.simulation.world_mut(index).expect("located world")
                    .state.metadata.set_player_locked_on(owner, false)
                    .map_err(|error| HostPlayerInputError::Gameplay(
                        NativePlayerInputError::Source(error)
                    ))?;
            }
            if input.action == bees_gameplay::player_inputs::NativePlayerSupportAction::Hold {
                // Unity StopChasing disables the preference even if no
                // Aggressive command happened to be active at this instant.
                self.simulation.world_mut(index).expect("located world")
                    .state.metadata.set_player_chasing(owner, false)
                    .map_err(|error| HostPlayerInputError::Gameplay(
                        NativePlayerInputError::Source(error)
                    ))?;
            }
        }
        Ok(counts)
    }

    pub fn set_timing_sample_period(&mut self, every: Option<NonZeroU32>) {
        self.simulation.set_timing_sample_period(every);
    }

    /// Manual aim changes weapon input only. Existing squad commands and
    /// pending strategic requests retain their own source lifecycle.
    pub fn player_fire_squads(&mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadFire)
        -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        self.apply_player_fire(input, false)
    }

    pub fn player_held_fire(&mut self, input: bees_gameplay::player_inputs::NativePlayerHeldFire)
        -> Result<&'static str, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{NativeHeldFireAction, NativePlayerSquadFire};
        let target = match input.action {
            NativeHeldFireAction::Begin { squads, target } => {
                let counts = self.apply_player_fire(&NativePlayerSquadFire {
                    world_id: input.world_id, episode_revision: input.episode_revision, squads,
                    target: Some(target),
                }, true)?;
                return Ok(if counts.accepted_squads == 0 { "No selected weapons accepted held fire" }
                    else { "Manual fire active" });
            }
            NativeHeldFireAction::Aim { target } if target.is_finite() => Some(target),
            NativeHeldFireAction::Aim { .. } => return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::InvalidAction)),
            NativeHeldFireAction::End => None,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id &&
                world.episode_revision() == input.episode_revision))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.is_failed() || world.state.events.is_failed() || world.state.events.completed() {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::InactiveWorld));
        }
        if !world.state.has_held_fire() {
            return Ok(if target.is_some() { "No selected weapons accepted held fire" } else { "Manual fire released" });
        }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        self.simulation.world_mut(index).expect("located world").state.update_held_fire(target)
            .map_err(HostPlayerInputError::Gameplay)?;
        Ok(if target.is_some() { "Manual fire active" } else { "Manual fire released" })
    }

    fn apply_player_fire(&mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadFire, held: bool)
        -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{NativePlayerFirePlan, NativePlayerCommandCounts};
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        if input.target.is_some_and(|point| !point.is_finite()) {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::InvalidAction));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerFirePlan::prepare(world, input.world_id, input.episode_revision,
                selected_ship, input.target) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() && (!held || !world.state.has_held_fire()) { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        let world = self.simulation.world_mut(index).expect("located world");
        if held { world.state.update_held_fire(None).map_err(HostPlayerInputError::Gameplay)?; }
        for plan in plans {
            let result = if held { plan.begin_held_fire(world) } else { plan.execute(world) };
            if result.map_err(HostPlayerInputError::Gameplay)? > 0 {
                counts.accepted_squads += 1;
            } else { counts.unavailable_squads += 1; }
        }
        Ok(counts)
    }

    /// Player Cease Fire / Attack on Sight sets the SAME ship weapon flag
    /// consumed by the canonical projectile scheduler, without replacing
    /// an active squad command or touching HiveMind reservation authority.
    pub fn player_cease_fire_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadCeaseFire,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerCeaseFirePlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerCeaseFirePlan::prepare(
                world, input.world_id, input.episode_revision,
                selected_ship, input.cease_fire,
            ) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            if plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)? > 0 {
                counts.accepted_squads += 1;
            } else {
                counts.unavailable_squads += 1;
            }
        }
        Ok(counts)
    }

    /// Enable or disable the original one-second source CheckChase timer
    /// behavior for user squads without replacing an active command.
    pub fn player_chase_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadChase,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerChasePlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerChasePlan::prepare(world, input.world_id,
                input.episode_revision, selected_ship, input.chasing) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            counts.accepted_squads += 1;
        }
        Ok(counts)
    }

    /// Unity SquadActionBox.LockOn: selected squads with a live enemy
    /// command can lock/unlock independently of movement. Preflight the
    /// whole selection before applying; no socket request is retired.
    pub fn player_lock_on_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadLockOn,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerLockOnPlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerLockOnPlan::prepare(world, input.world_id,
                input.episode_revision, selected_ship, input.locked) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            counts.accepted_squads += 1;
        }
        Ok(counts)
    }

    /// Change the original squad's chosen shooting priority, including
    /// the currently active command's effective weapon ranking. This
    /// requires neither a HiveMind reservation nor command supersession.
    /// The host preflights all participants before invalidating the
    /// driver's borrowed environment view and executing any mutation.
    pub fn player_shooting_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadShooting,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerShootingPlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerShootingPlan::prepare(
                world, input.world_id, input.episode_revision,
                selected_ship, input.strategy_id,
            ) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            counts.accepted_squads += 1;
        }
        Ok(counts)
    }

    /// Source MatchSpeed / UnmatchSpeed changes the already-authored
    /// current speed on each live ship, not its immutable base speed or
    /// the squad's active AI/strategic command. Preflight all squads before
    /// mutating the first; an actual execution failure marks the world failed.
    pub fn player_match_speed_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadMatchSpeed,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerMatchSpeedPlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerMatchSpeedPlan::prepare(
                world, input.world_id, input.episode_revision,
                selected_ship, input.match_speed,
            ) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        let shared_slowest_speed = plans.iter()
            .map(NativePlayerMatchSpeedPlan::slowest_authored_speed)
            .fold(f32::INFINITY, f32::min);
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            if plan.execute(self.simulation.world_mut(index).expect("located world"),
                shared_slowest_speed)
                .map_err(HostPlayerInputError::Gameplay)? > 0 {
                counts.accepted_squads += 1;
            } else { counts.unavailable_squads += 1; }
        }
        Ok(counts)
    }

    /// Physical Scout/Fire Barge player specials do not replace any active
    /// command. The shared combat simulation defers their effects to its
    /// next fixed-step event boundary so roster, reward and death consumers
    /// see exactly the same transaction as AI and policy actions.
    pub fn player_special_squads(
        &mut self, input: &bees_gameplay::player_inputs::NativePlayerSquadSpecial,
    ) -> Result<bees_gameplay::player_inputs::NativePlayerCommandCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        use bees_gameplay::player_inputs::{
            NativePlayerSpecialPlan, NativePlayerCommandCounts,
        };
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerCommandCounts::default();
        let mut plans = Vec::with_capacity(input.squads.len());
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        for &owner in &input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerSpecialPlan::prepare(world, input.world_id,
                input.episode_revision, selected_ship, input.kind) {
                Ok(plan) if plan.is_available() => plans.push(plan),
                Ok(_) => counts.unavailable_squads += 1,
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        // No HiveMind request retirement or squad command finalization:
        // pressing DropBeacon/Detonate is orthogonal to that strategy.
        for plan in plans {
            if plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)? > 0 {
                counts.accepted_squads += 1;
            } else {
                counts.unavailable_squads += 1;
            }
        }
        Ok(counts)
    }

    /// Schema1 compatibility: only runs registered in the original
    /// connection checkpoint may be replayed. Normal journals use schema2.
    pub fn recover_prior_outcome_journal(
        &mut self, path: &std::path::Path, expected_run_id: &str,
    ) -> Result<crate::journal_recovery::JournalRecoveryCounts,
        crate::journal_recovery::JournalRecoveryError<DurableConnectionError>> {
        crate::journal_recovery::replay_command_outcomes(path, expected_run_id, |recorded|
            self.connection.queue_journal_outcomes(recorded).map(|request| request.is_some()))
    }

    /// A notification sink exposes only batches already journaled to stable
    /// storage. Checkpoint each original batch into the connection's one
    /// StoreCommands retry ledger BEFORE the sink releases it. The socket
    /// uses pending_store_hashes/retry_payload to transmit those exact
    /// hashes; this method never equates queueing with server commit.
    fn checkpoint_prepared_outcomes(&mut self) -> Result<(), DurableConnectionError> {
        while let Some(batch) = self.sink.first_prepared_outcomes() {
            self.connection.queue_prepared_outcomes(batch)?;
            self.sink.prepared_outcomes_queued();
        }
        Ok(())
    }

    /// Actual multi-Level model inference, fixed-step combat and ordered
    /// durable notification delivery. A partial-step error is returned,
    /// never silently retried as a valid PPO trajectory.
    pub fn run_window(
        &mut self,
        rounds: NonZeroU32,
        delivery_every: NonZeroU32,
    ) -> Result<NativeLevelWindowCounts,
        HostWindowError<
            <M as NativeLevelObservationContext>::Error, B::Error, S::Error,
        >>
    {
        let result = self.connection.with_level_lifecycle(|connection|
            self.driver.run_window(
                &mut self.simulation, &mut self.model, connection,
                &mut self.sink, rounds, delivery_every,
            )
        ).map_err(HostWindowError::Connection)?;
        // Even when a later world step failed, earlier completed episodes
        // may already have durably published prepared credits in this window.
        // Checkpoint those before returning either the window result or its
        // failure to the operator.
        let queued = self.checkpoint_prepared_outcomes();
        match (result, queued) {
            (Ok(counts), Ok(())) => {
                self.sampler.record_window(&counts);
                Ok(counts)
            }
            (Err(window), Ok(())) => Err(HostWindowError::Window(window)),
            (Ok(_), Err(connection)) => Err(HostWindowError::Connection(connection)),
            (Err(window), Err(connection)) =>
                Err(HostWindowError::WindowAndConnection { window, connection }),
        }
    }

    /// Cancel or abort an episode and durably deliver all source-owned
    /// reservation/PPO discard obligations before replacement is allowed.
    pub fn stop_world(
        &mut self, index: usize, kind: NativeLevelStopKind,
    ) -> Result<NativeLevelStopDelivery, HostStopError<S::Error>> {
        let result = self.connection.with_level_lifecycle(|connection|
            self.driver.stop_world_and_deliver(
                &mut self.simulation, index, kind, connection, &mut self.sink,
            )
        ).map_err(HostStopError::Connection)?;
        let queued = self.checkpoint_prepared_outcomes();
        match (result, queued) {
            (Ok(delivery), Ok(())) => Ok(delivery),
            (Err(stop), Ok(())) => Err(HostStopError::Stop(stop)),
            (Ok(_), Err(connection)) => Err(HostStopError::Connection(connection)),
            (Err(stop), Err(connection)) =>
                Err(HostStopError::StopAndConnection { stop, connection }),
        }
    }

    /// Caller must settle earlier notifications and all Squad lifetimes.
    pub fn reset_world(
        &mut self, index: usize, seed: u64, replacement: NativeLevelState<M, A>,
    ) -> Result<(), HostResetError> {
        if !self.connection.is_available() {
            return Err(HostResetError::Connection(DurableConnectionError::Unavailable));
        }
        self.checkpoint_prepared_outcomes()
            .map_err(HostResetError::Connection)?;
        self.driver.reset_world_after_delivery(
            &mut self.simulation, index, seed, replacement,
        ).map_err(HostResetError::Reset)
    }

    /// Invoke at the owning Stage's SOURCE 0.25-second HiveMind timer
    /// pulse, after its InitialCommandDelay. The caller supplies the
    /// real per-Level (IsLevelSetupOnServer && !State.IsPaused) condition.
    /// A shared socket setup does NOT imply every native Level is setup.
    ///
    /// This inspects ONLY the canonical roster, command director, imported
    /// source authorization and connection's in-flight session. It does not
    /// allocate a second gameplay queue or rerun selection during physics.
    /// Newly issued requests are already durably persisted; use
    /// poll_network_and_continue_source_commands to transmit them and
    /// chain their exact matching strategy response.
    ///
    /// Source out-of-bounds squads are not sent to the Hive Mind. Their
    /// original move-to-start-position behavior still needs the actual
    /// Stage starting-position/path adapter; this method does not invent
    /// positions or silently count them as successful orders.
    pub fn issue_due_source_targeting(
        &mut self,
        worker: &crate::socket_worker::NativeSocketWorker,
        max_new_requests: usize,
        mut level_is_ready: impl FnMut(i64) -> bool,
    ) -> Result<NativeSourceCommandPulse, HostSourceCommandError> {
        let mut counts = NativeSourceCommandPulse::default();
        if !worker.ready() || max_new_requests == 0 {
            return Ok(counts);
        }
        if self.driver.registered_world_count() != self.simulation.world_count() {
            return Err(HostSourceCommandError::Driver(
                NativeLevelRoutingError::UnregisteredWorldCount,
            ));
        }
        // Only (lifetime, opponent, enemy side) crosses the preflight:
        // commands, combat positions and source authorization stay in the
        // owning Level; requests use their ORIGINAL connection allocator.
        let mut issue = Vec::with_capacity(max_new_requests.min(64));
        let mut home = Vec::new();
        for index in 0..self.simulation.world_count() {
            let world = self.simulation.world_mut(index)
                .expect("validated number of registered native worlds");
            if world.is_failed() || world.state.events.is_failed() ||
                world.state.events.completed() || world.state.events.was_aborted()
            {
                continue;
            }
            let stage_enabled = world.state.metadata.source_hivemind_active()
                .ok_or(HostSourceCommandError::MissingStageAuthorization { index })?;
            if !stage_enabled { continue; }
            let state = &mut world.state;
            let [bee_side, human_side] = state.events.sides();
            let roster = state.events.roster();
            for &owner in roster.source_order() {
                let squad = roster.get(owner)
                    .ok_or(HostSourceCommandError::MissingSquadAuthorization(owner))?;
                let Some(source) = state.metadata.source_hivemind_squad(owner) else {
                    if squad.mother_squad.is_some() {
                        // Dynamic Beacon children are not source-authored
                        // movable command squads; do not infer their orders.
                        counts.unauthored_minions += 1;
                        continue;
                    }
                    return Err(HostSourceCommandError::MissingSquadAuthorization(owner));
                };
                if !source.can_request() || !level_is_ready(owner.level_id) ||
                    state.events.commands().has_active(owner) ||
                    self.connection.connection().session().has_live_request_for(owner)
                {
                    continue;
                }
                counts.eligible += 1;
                // Squad.IsInBounds calls each real Ship.IsInBounds, which
                // performs lazy persistent bounds latching in CombatWorld.
                let mut in_bounds = true;
                for &ship_id in &squad.members {
                    if !state.combat.ship_is_in_bounds(ship_id)
                        .map_err(|error| HostSourceCommandError::Geometry {
                            owner, error,
                        })? {
                        in_bounds = false;
                        break;
                    }
                }
                if !in_bounds {
                    counts.out_of_bounds += 1;
                    // Source GetHiveMindCommands invokes Squad.Move(home)
                    // only when no squad member still has target coordinates.
                    if !squad.members.iter().any(|&ship_id|
                        state.combat.ship_body(ship_id)
                            .is_some_and(|body| body.motion.has_target_coordinates)
                    ) {
                        home.push((index, owner));
                    }
                    continue;
                }
                // Network admission is bounded, but the source timer must
                // still inspect later squads for local return-home orders.
                // Validate enemy routing only for requests admitted this
                // pulse; exhausted-budget squads have no wire destination.
                if issue.len() < max_new_requests {
                    let enemy_side = if squad.side == bee_side { human_side }
                        else if squad.side == human_side { bee_side }
                        else { return Err(HostSourceCommandError::InvalidSquadSide(owner)); };
                    issue.push((owner, source.opponent_id, enemy_side));
                }
            }
        }
        // Reuse one formation buffer across all home moves on this pulse.
        // A large battle should not allocate one Vec per squad requeue.
        let mut home_members = Vec::new();
        for (index, owner) in home {
            if self.issue_source_return_home(index, owner, &mut home_members)? {
                counts.home_moves += 1;
            } else {
                counts.home_unreachable += 1;
            }
        }
        for (owner, opponent_id, enemy_side) in issue {
            self.request_source_targeting(owner, opponent_id, enemy_side)
                .map_err(HostSourceCommandError::Connection)?;
            counts.issued += 1;
        }
        Ok(counts)
    }

    /// The actual source Squad.Move(startingPosition), using the same
    /// reusable authored formations, native navigation and CombatWorld as
    /// gameplay commands and rendered player movement. Never teleport
    /// or substitute a synthetic arena-center destination.
    fn issue_source_return_home(
        &mut self, index: usize, owner: SquadLifetime,
        members: &mut Vec<bees_sim::squad_movement::SquadFormationMember>,
    ) -> Result<bool, HostSourceCommandError> {
        let world = self.simulation.world_mut(index)
            .expect("preflighted live world index");
        let state = &mut world.state;
        let squad = state.events.roster().get(owner)
            .ok_or(HostSourceCommandError::MissingSquadAuthorization(owner))?;
        let home = state.metadata.source_hivemind_starting_position(squad.side)
            .ok_or(HostSourceCommandError::MissingSquadAuthorization(owner))?;
        let fail = |error| HostSourceCommandError::SourceOrder { owner, error };
        let inputs = state.metadata.strategy_inputs(
            &state.combat, state.events.roster(), owner, None,
        ).map_err(fail)?;
        let order = bees_gameplay::level_inputs::native_squad_order_from_catalog(
            &state.combat, squad, state.metadata.targeting_ship_catalog(),
            inputs.formation, None, None,
            |id| state.metadata.stored_formation_offset(id),
            |id| state.metadata.current_path_clearance(id),
            members,
        ).map_err(fail)?;
        let asteroids = state.metadata.collision_asteroids_enabled();
        let navigation = state.metadata.navigation_mut();
        state.combat.refresh_world_navigation(navigation, true, asteroids)
            .map_err(|error| fail(error.into()))?;
        bees_sim::squad_movement::issue_squad_move(
            &mut state.combat, navigation, order.members, home,
            inputs.formation,
        ).map(|result| result.is_some())
            .map_err(|error| fail(error.into()))
    }

    /// Nonblocking socket pump for BOTH graphical and headless callers.
    /// Call between shared simulation windows, with a caller-bounded event
    /// budget. Network I/O stays on the worker; outcome ownership stays here.
    /// Returning one event at a time preserves targeting/ban obligations even
    /// if the next frame or output operation fails.
    pub fn poll_network(
        &mut self, worker: &mut crate::socket_worker::NativeSocketWorker,
    ) -> Result<Option<NativeHostSocketEvent>, HostNetworkError> {
        self.checkpoint_prepared_outcomes().map_err(HostNetworkError::Connection)?;
        let transport = (|| {
            worker.queue_pending_stores(&self.connection)?;
            worker.poll_event()
        })();
        let event = match transport {
            Ok(event) => event,
            Err(error) => return Err(self.retire_transport_after_error(error)),
        };
        match event {
            None => Ok(None),
            Some(crate::socket::HiveMindSocketEvent::SetupAccepted { request_hash, server_game_id }) =>
                Ok(Some(NativeHostSocketEvent::SetupAccepted { request_hash, server_game_id })),
            Some(crate::socket::HiveMindSocketEvent::ServerFrame { request_hash, frame }) => {
                let delivery = self.receive_frame(&frame).map_err(HostNetworkError::Connection)?;
                let unhandled_frame = delivery.is_none().then_some(frame);
                Ok(Some(NativeHostSocketEvent::ServerFrame { request_hash, delivery, unhandled_frame }))
            }
        }
    }

    fn retire_transport_after_error(
        &mut self, error: crate::socket_worker::NativeSocketWorkerError,
    ) -> HostNetworkError {
        let (unresolved_requests, retirement_error) =
            match self.connection.retire_socket() {
                Ok(hashes) => (hashes, None),
                Err(error) => (Vec::new(), Some(error)),
            };
        HostNetworkError::Transport {
            error, unresolved_requests, retirement_error,
        }
    }

    /// Native gameplay network pump. An accepted GetMatchupStrategy reply
    /// continues to source-built GetStrategy with the ORIGINAL opponent
    /// identity. The one connection still owns its hashes and reservations;
    /// the socket worker holds only bounded send flow-control.
    ///
    /// Call between simulation windows in lieu of poll_network when using
    /// source-imported ordinary gameplay. The legacy poll_network remains
    /// a lower-level receive-only adapter for explicit host-controlled
    /// request sequences and independent protocol qualification.
    ///
    /// Pending requests that cannot enter a full send channel remain
    /// correlation-owned by the original session; subsequent pumps retry
    /// without minting new hashes. A source-validation failure is surfaced
    /// and must be handled by the operator, not treated as a successful
    /// strategy selection or fabricated StoreCommands credit.
    pub fn poll_network_and_continue_source_commands(
        &mut self,
        worker: &mut crate::socket_worker::NativeSocketWorker,
    ) -> Result<Option<NativeHostSocketEvent>, HostNetworkError> {
        let event = self.poll_network(worker)?;
        if let Some(NativeHostSocketEvent::ServerFrame {
            delivery: Some(NativeHiveMindFrameDelivery::Targeting(targeting)), ..
        }) = event.as_ref() {
            if let TargetingReceipt::Accepted { owner, .. } = &targeting.receipt {
                self.request_live_strategy(*owner, targeting.opponent_id)
                    .map_err(HostNetworkError::Connection)?;
            }
        }
        worker.queue_pending_hivemind_requests(&self.connection)
            .map_err(|error| self.retire_transport_after_error(error))?;
        Ok(event)
    }

    /// Pass only authenticated WebSocket frames. StoreCommands 200/409
    /// changes are persisted before the acknowledgement is returned.
    pub fn receive_frame(
        &mut self, frame: &[u8],
    ) -> Result<Option<NativeHiveMindFrameDelivery>, DurableConnectionError> {
        self.connection.receive_frame(
            &mut self.driver, &mut self.simulation, frame,
        )
    }

    pub fn request_strategy(
        &mut self, owner: SquadLifetime, enemy: Option<SquadLifetime>,
        matchup: String, shooting: String, opponent: u64,
        banned: impl IntoIterator<Item=String>,
    ) -> Result<GetStrategyRequest, DurableConnectionError> {
        let banned = self.driver.strategy_request_bans(&self.simulation, owner, banned)
            .map_err(DurableConnectionError::GameplayRequest)?;
        self.connection.request_strategy(
            owner, enemy, matchup, shooting, opponent, banned,
        )
    }

    /// Continue an accepted GetMatchupStrategy reply into a real
    /// GetStrategy request. Both request-local learning keys and the
    /// enemy lifecycle come from the canonical live Level, never a
    /// host-supplied string or recycled Squad.ItemId.
    ///
    /// The driver supplies all source bans from canonical Level state:
    /// composition, ClosestFriendly occupancy, live warp-gates, shared
    /// Beehive healing slots, miner/asteroid availability, and the original
    /// exported Level.ActivateMining flag. The no-target bans are included.
    /// Legacy scene exports without that flag fail closed before allocating
    /// a request hash rather than silently changing server strategy choices.
    /// The exact returned durable request goes to send_strategy on the
    /// existing socket worker; retries must use its original Hash.
    ///
    /// None means the selected enemy had no visible potential ships,
    /// matching Unity's requeue-without-command behavior.
    pub fn request_live_strategy(
        &mut self, owner: SquadLifetime, opponent: u64,
    ) -> Result<Option<GetStrategyRequest>, DurableConnectionError> {
        let Some(source) = self.driver.source_strategy_matchup_inputs(
            &mut self.simulation, owner,
        ).map_err(DurableConnectionError::GameplayRequest)? else {
            return Ok(None);
        };
        let matchup = source.matchup;
        let mut bans = self.driver.strategy_request_bans(
            &self.simulation, owner, std::iter::empty::<String>(),
        ).map_err(DurableConnectionError::GameplayRequest)?;
        if !source.activate_mining {
            bans = bees_gameplay::squad_composition::banned_strategy_names(
                &[bees_hivemind::dispatch::LocalCommandKind::Mining], bans,
            );
        }
        if matchup.enemy.is_none() {
            use bees_hivemind::dispatch::LocalCommandKind as Kind;
            bans = bees_gameplay::squad_composition::banned_strategy_names(
                &[Kind::Aggressive, Kind::Retreat, Kind::CircleSquad,
                    Kind::RightSwipe, Kind::LeftSwipe, Kind::InAndOut,
                    Kind::FullRetreat, Kind::Hold],
                bans,
            );
        }
        self.connection.request_strategy(
            owner, matchup.enemy, matchup.matchup,
            matchup.shooting_matchup, opponent, bans,
        ).map(Some)
    }

    pub fn request_targeting(
        &mut self, owner: SquadLifetime, ships: String, opponent: u64,
        banned: impl IntoIterator<Item=String>,
    ) -> Result<GetTargetingRequest, DurableConnectionError> {
        self.connection.request_targeting(owner, ships, opponent, banned)
    }

    /// Source Squad.MakeMatchupStrat request: never trust a caller-built
    /// Ships/Banned pair for ordinary gameplay. The Level has already
    /// determined that this squad is eligible and in bounds; preflight
    /// confirms its live lifecycle, complete roster, authored type letters
    /// and current enemy-side type bans BEFORE allocating/persisting a hash.
    /// The opposite faction side comes from the actual Level configuration;
    /// the banned type universe is the fixed source Type A-X registry.
    ///
    /// Send the returned persisted request using NativeSocketWorker::send_targeting.
    /// Transport retries must reuse the original request/hash.
    pub fn request_source_targeting(
        &mut self, owner: SquadLifetime, opponent: u64,
        enemy_side: i32,
    ) -> Result<GetTargetingRequest, DurableConnectionError> {
        let inputs = self.driver.targeting_request_inputs(
            &self.simulation, owner, enemy_side,
        ).map_err(DurableConnectionError::GameplayTargetingRequest)?;
        self.connection.request_targeting(
            owner, inputs.ships, opponent, inputs.banned,
        )
    }

    pub fn queue_outcomes(
        &mut self, batch: OutcomeBatch,
    ) -> Result<StoreCommandsRequest, DurableConnectionError> {
        self.connection.queue_outcomes(batch)
    }

    /// Borrow one immutable, live World for rendering. Only the actual
    /// active combat/physics bodies are exposed, with their exact original
    /// IDs/poses. The borrow prevents stepping or resetting this world
    /// during the render pass, without allocating a second snapshot.
    pub fn render_world(
        &self, index: usize,
    ) -> Option<NativeRenderWorldView<'_>>
    where M: NativePlayerAuthority
    {
        let world = self.simulation.world(index)?;
        Some(NativeRenderWorldView {
            world_id: world.id,
            episode_revision: world.episode_revision(),
            tick: world.tick(),
            fixed_step_seconds: self.simulation.world_fixed_step_seconds(index)?,
            terminal_result: world.state.events.latest_finalized_episode().map(|episode| episode.result),
            campaign_dialogue: world.state.campaign_dialogue.as_ref().map(|dialogue| dialogue.current()),
            survival: world.state.events.survival_status(),
            reinforcement_waves_pending:
                world.state.metadata.reinforcement_gauges().pending_waves,
            next_reinforcement_tick:
                world.state.metadata.next_reinforcement_tick(),
            combat: &world.state.combat,
            collision_matrix: &self.simulation.rules().collision_matrix,
            roster: world.state.events.roster(),
            commands: world.state.events.commands(),
            player_authority: &world.state.metadata,
            pending_notifications:
                world.state.events.pending_notifications(),
        })
    }

    /// Read-only world access; mutation always uses the canonical driver.
    pub fn simulation(&self)
        -> &Simulation<NativeLevelState<M, A>, NativeLevelRules>
    {
        &self.simulation
    }

    /// Model weights and result sink can be swapped/consumed between
    /// synchronous windows, never while an inference batch is in flight.
    pub fn model_mut(&mut self) -> &mut B { &mut self.model }
    pub fn sink_mut(&mut self) -> &mut S { &mut self.sink }

    pub fn terminal_observations(
        &mut self, index: usize,
    ) -> Result<Vec<bees_gameplay::level_host::NativeTerminalObservation>,
        bees_gameplay::level_host::NativeLevelSourceError<<M as NativeLevelObservationContext>::Error>>
    {
        let world = self.simulation.world(index).ok_or(
            bees_gameplay::level_host::NativeLevelSourceError::MissingRegisteredWorld,
        )?;
        self.driver.policy_boundary_observations(index, world)
    }

    /// Expose only the fail-closed persistent connection API, not its raw
    /// hash allocator or mutable session.
    pub fn connection_mut(&mut self) -> &mut DurableHiveMindConnection {
        &mut self.connection
    }

    pub fn runtime_status(&self) -> Result<NativeRuntimeStatus, NativeLevelRoutingError> {
        self.driver.runtime_status(&self.simulation, self.connection.connection())
    }

    /// True host wall seconds, world steps/s, inferred rows/s, sampled
    /// physics/model timings and live backlog gauges without step histories.
    pub fn sample_status(
        &mut self,
    ) -> Result<Option<NativeHostStatusInterval>, NativeLevelRoutingError> {
        let status = self.runtime_status()?;
        Ok(self.sampler.sample(status))
    }
}
