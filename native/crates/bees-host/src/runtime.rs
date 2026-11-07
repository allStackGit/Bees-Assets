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
use bees_sim::simulation::Simulation;
use crate::durable_connection::{DurableConnectionError, DurableHiveMindConnection};
use crate::status::{NativeHostStatusInterval, NativeStatusSampler};

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
        result.map_err(HostWindowError::Window)
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
