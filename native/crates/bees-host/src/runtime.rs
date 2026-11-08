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
use bees_hivemind::session::SquadLifetime;
use bees_sim::simulation::{RandomStream, Simulation, World};
use crate::durable_connection::{DurableConnectionError, DurableHiveMindConnection};
use crate::status::{NativeHostStatusInterval, NativeStatusSampler};
use crate::presentation::NativeRenderWorldView;
use bees_gameplay::player_inputs::{NativeLevelPlayerContext, NativePlayerMove,
    NativePlayerMovePlan, NativePlayerInputError, NativePlayerAuthority,
    NativePlayerSquadMoves, NativePlayerMoveCounts, NativePlayerStop,
    NativePlayerSquadStops, NativePlayerStopPlan, NativePlayerStopCounts,
    NativePlayerManualFire, NativePlayerSquadManualFire,
    NativePlayerManualFirePlan, NativePlayerManualFireCounts};

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
}

#[derive(Debug)]
pub enum HostStopError<E> {
    Connection(DurableConnectionError),
    Stop(NativeLevelStopError<E>),
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
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        self.connection.retire_player_requests(plan.owner())
            .map_err(HostPlayerInputError::Connection)?;
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
        self.driver.invalidate_environment(index).map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            self.connection.retire_player_requests(plan.owner())
                .map_err(HostPlayerInputError::Connection)?;
            match plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)? {
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::Command(
                    bees_gameplay::commands::Activation::Applied { .. }) |
                bees_gameplay::player_inputs::NativePlayerCommandOutcome::Move { reachable: true } =>
                    counts.accepted_squads += 1,
                _ => counts.unavailable_squads += 1,
            }
        }
        Ok(counts)
    }

    /// Stop each selected, authorized source squad in selection order.
    /// The source's StopMoving is distinct from the scripted AI Hold
    /// command. No second movement controller or shadow command is created.
    pub fn player_stop_squads(
        &mut self, input: NativePlayerSquadStops,
    ) -> Result<NativePlayerStopCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerStopCounts::default();
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        let mut plans = Vec::with_capacity(input.squads.len());
        for owner in input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerStopPlan::prepare(world, NativePlayerStop {
                world_id: input.world_id,
                episode_revision: input.episode_revision,
                selected_ship,
            }) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index)
            .map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            self.connection.retire_player_requests(plan.owner())
                .map_err(HostPlayerInputError::Connection)?;
            plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            counts.accepted_squads += 1;
        }
        Ok(counts)
    }

    /// Send point-fire input to real per-ship turret controllers, not
    /// to an alternate client-side weapon simulation. Unlike Move/Stop,
    /// manual fire does not replace movement or HiveMind command ownership.
    pub fn player_manual_fire_squads(
        &mut self, input: NativePlayerSquadManualFire,
    ) -> Result<NativePlayerManualFireCounts, HostPlayerInputError>
    where M: NativeLevelPlayerContext
    {
        let index = (0..self.simulation.world_count()).find(|&index|
            self.simulation.world(index).is_some_and(|world| world.id == input.world_id))
            .ok_or(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld))?;
        let world = self.simulation.world(index).expect("located world");
        if world.episode_revision() != input.episode_revision {
            return Err(HostPlayerInputError::Gameplay(NativePlayerInputError::StaleWorld));
        }
        let mut counts = NativePlayerManualFireCounts::default();
        let mut seen = std::collections::HashSet::with_capacity(input.squads.len());
        let mut plans = Vec::with_capacity(input.squads.len());
        for owner in input.squads {
            if !seen.insert(owner) { continue; }
            let Some(selected_ship) = world.state.events.roster().get(owner)
                .and_then(|squad| squad.members.first()).copied() else {
                counts.rejected_squads += 1;
                continue;
            };
            match NativePlayerManualFirePlan::prepare(world, NativePlayerManualFire {
                world_id: input.world_id,
                episode_revision: input.episode_revision,
                selected_ship, target: input.target,
            }) {
                Ok(plan) => plans.push(plan),
                Err(error) if error.is_rejection() => counts.rejected_squads += 1,
                Err(error) => return Err(HostPlayerInputError::Gameplay(error)),
            }
        }
        if plans.is_empty() { return Ok(counts); }
        self.driver.invalidate_environment(index)
            .map_err(HostPlayerInputError::Driver)?;
        for plan in plans {
            plan.execute(self.simulation.world_mut(index).expect("located world"))
                .map_err(HostPlayerInputError::Gameplay)?;
            counts.accepted_squads += 1;
        }
        Ok(counts)
    }

    pub fn set_timing_sample_period(&mut self, every: Option<NonZeroU32>) {
        self.simulation.set_timing_sample_period(every);
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
        let counts = result.map_err(HostWindowError::Window)?;
        self.sampler.record_window(&counts);
        Ok(counts)
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
        result.map_err(HostStopError::Stop)
    }

    /// Caller must settle earlier notifications and all Squad lifetimes.
    pub fn reset_world(
        &mut self, index: usize, seed: u64, replacement: NativeLevelState<M, A>,
    ) -> Result<(), HostResetError> {
        if !self.connection.is_available() {
            return Err(HostResetError::Connection(DurableConnectionError::Unavailable));
        }
        self.driver.reset_world_after_delivery(
            &mut self.simulation, index, seed, replacement,
        ).map_err(HostResetError::Reset)
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
        self.connection.request_strategy(
            owner, enemy, matchup, shooting, opponent, banned,
        )
    }

    pub fn request_targeting(
        &mut self, owner: SquadLifetime, ships: String, opponent: u64,
        banned: impl IntoIterator<Item=String>,
    ) -> Result<GetTargetingRequest, DurableConnectionError> {
        self.connection.request_targeting(owner, ships, opponent, banned)
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
            combat: &world.state.combat,
            roster: world.state.events.roster(),
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
