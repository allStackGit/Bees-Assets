//! One source-ordered combat-event handoff for a native Level.
//!
//! Call once after the shared CombatWorld's fixed step, before its event
//! buffer is cleared by the next step. This is NOT a second combat engine,
//! scheduler, reward model or a substitute for the Unity/MA-POCA adapter.
//! All mutable scratch is reused per Level, never process-global.

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::rl_shaping::{DamageRewardMetadata, RewardEmission};
use bees_sim::rl_discovery::DiscoveryKind;

use crate::commands::{
    CommandMeasurements, FinishedNativeCommand, GameCommandError,
    NativeFriendlySquad, NativeSquadCommandDirector,
};
use crate::episode::{
    NativeEpisodeFinal, NativeEpisodeRewardCoordinator,
    NativeEpisodeRewardError, NativeEpisodeRewardMeasurements,
};
use crate::group_rewards::{
    GroupRewardError, GroupRewardEvent, GroupRewardMeasurements,
    NativeGroupRewardDelivery,
};
use crate::squads::{
    NativeSquadRoster, SquadEventBridgeError, SquadRosterChange,
    SquadRosterError, SquadRosterMeasurements,
};

#[derive(Debug)]
pub enum NativeLevelEventError {
    MissingOrRepeatedTick { expected: u64, got: u64 },
    Roster(SquadEventBridgeError),
    Rewards(NativeEpisodeRewardError),
    Commands(GameCommandError),
    Group(GroupRewardError),
    InvalidGroupConfiguration,
    InvalidRetirementAcknowledgement,
    FailedLevel,
}

pub struct NativeLevelTick<'a> {
    /// Processed in exact CombatWorld event order.
    pub squad_changes: &'a [SquadRosterChange],
    /// The authoritative game must settle real TSV and unmatched
    /// OutcomeIds; these command reservations are NOT silently dropped.
    pub finished_commands: &'a [FinishedNativeCommand],
    /// Pending instructions for the ONE connection-owned HiveMindSession.
    /// May include earlier ticks not yet acknowledged by the transport.
    pub pending_session_retirements: &'a [SquadLifetime],
    /// Source-order bounded shaping plus separate uncapped economics.
    /// Reward delivery to team/agent groups belongs to the host adapter.
    pub reward_emissions: &'a [RewardEmission],
    /// Optional source MA-POCA wire commands, emitted once per side.
    /// EloMarker is transport-only and must be stripped before PPO.
    pub group_events: Option<&'a [GroupRewardEvent]>,
    pub finalized: Option<NativeEpisodeFinal>,
}

/// Deliver this immediately through the host transport; it is not
/// replayed from the next fixed-step reward buffer.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeImmediateReward {
    pub emission: Option<RewardEmission>,
    pub group_event: Option<GroupRewardEvent>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelEventMeasurements {
    pub squads: SquadRosterMeasurements,
    pub commands: CommandMeasurements,
    pub rewards: NativeEpisodeRewardMeasurements,
    pub group_delivery: Option<GroupRewardMeasurements>,
    pub ticks_integrated: u64,
    pub pending_session_retirements: usize,
}

/// Owns the authoritative native Level's squad/command/episode state.
    /// HiveMindSession belongs to the WebSocket transport and may serve
    /// several Levels; this processor only publishes retirement messages.
/// It does not own a second CombatWorld: the caller passes the same world
/// used by graphics and the Simulation fixed-step rules.
pub struct NativeLevelEventProcessor {
    roster: NativeSquadRoster,
    commands: NativeSquadCommandDirector,
    /// Persistent until acknowledged by the connection-owned transport.
    /// Never discard squad retirement obligations at the next fixed tick.
    pending_session_retirements: Vec<SquadLifetime>,
    episode: NativeEpisodeRewardCoordinator,
    group_delivery: Option<NativeGroupRewardDelivery>,
    next_tick: u64,
    last_advanced_command_tick: Option<u64>,
    failed: bool,
    last_finalized: Option<NativeEpisodeFinal>,
    changes: Vec<SquadRosterChange>,
    finished: Vec<FinishedNativeCommand>,
    /// Reused for timer completions; death-retirement output remains intact.
    timed_finished: Vec<FinishedNativeCommand>,
}

impl NativeLevelEventProcessor {
    pub fn new(episode: NativeEpisodeRewardCoordinator) -> Self {
        Self {
            roster: NativeSquadRoster::new(),
            commands: NativeSquadCommandDirector::new(),
            pending_session_retirements: Vec::new(),
            episode,
            group_delivery: None,
            next_tick: 1,
            last_advanced_command_tick: None,
            failed: false,
            last_finalized: None,
            changes: Vec::new(),
            finished: Vec::new(),
            timed_finished: Vec::new(),
        }
    }

    /// Setup and live command activation use the same objects, not
    /// independently reconstructed per-tick registries.
    pub fn roster_mut(&mut self) -> &mut NativeSquadRoster {
        &mut self.roster
    }

    pub fn roster(&self) -> &NativeSquadRoster {
        &self.roster
    }

    pub fn has_active_commands(&self) -> bool {
        self.commands.active_count() > 0
    }

    pub fn commands_mut(&mut self) -> &mut NativeSquadCommandDirector {
        &mut self.commands
    }

    /// All pending retirements, including ones from earlier ticks. The
    /// shared connection must call HiveMindSession::retire_squad for
    /// each, then acknowledge the processed prefix only after success.
    pub fn pending_session_retirements(&self) -> &[SquadLifetime] {
        &self.pending_session_retirements
    }

    pub fn acknowledge_session_retirements(
        &mut self, count: usize,
    ) -> Result<(), NativeLevelEventError> {
        if count > self.pending_session_retirements.len() {
            return Err(NativeLevelEventError::InvalidRetirementAcknowledgement);
        }
        self.pending_session_retirements.drain(..count);
        Ok(())
    }

    pub fn episode_mut(&mut self) -> &mut NativeEpisodeRewardCoordinator {
        &mut self.episode
    }

    /// Opt in to native MA-POCA delivery before the first fixed tick.
    /// Graphical non-RL Levels may leave the adapter unconfigured.
    /// Agent participation is registered directly on this group object
    /// as controllers bind/unbind during the episode.
    pub fn configure_group_delivery(
        &mut self,
        delivery: NativeGroupRewardDelivery,
    ) -> Result<(), NativeLevelEventError> {
        if self.next_tick != 1 || self.group_delivery.is_some() ||
            delivery.sides() != self.episode.sides() {
            return Err(NativeLevelEventError::InvalidGroupConfiguration);
        }
        self.group_delivery = Some(delivery);
        Ok(())
    }

    pub fn group_delivery_mut(&mut self) -> Option<&mut NativeGroupRewardDelivery> {
        self.group_delivery.as_mut()
    }

    /// Source-verified HiveMind sighting between fixed ticks. This is the
    /// same bounded shaping budget as projectile hits and misses, but
    /// must be sent immediately (not silently merged into the next
    /// physics-tick event buffer). Only one group reward is generated
    /// regardless of how many agents are currently participating.
    pub fn record_verified_discovery(
        &mut self,
        side: i32,
        kind: DiscoveryKind,
        entity_id: i64,
        authored_value: i32,
    ) -> Result<NativeImmediateReward, NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        let emission = match self.episode.record_verified_discovery(
            side, kind, entity_id, authored_value,
        ) {
            Ok(value) => value,
            Err(error) => {
                self.failed = true;
                return Err(NativeLevelEventError::Rewards(error));
            }
        };
        let group_event = if let (Some(event), Some(group)) = (
            emission, self.group_delivery.as_mut(),
        ) {
            match group.immediate_event(event) {
                Ok(value) => value,
                Err(error) => {
                    self.failed = true;
                    return Err(NativeLevelEventError::Group(error));
                }
            }
        } else {
            None
        };
        Ok(NativeImmediateReward { emission, group_event })
    }

    pub fn is_failed(&self) -> bool {
        self.failed || self.roster.is_failed() || self.episode.is_failed()
    }

    pub fn completed(&self) -> bool {
        self.episode.result().is_some()
    }

    /// Immutable view after Simulation::step_world; no per-frame copies.
    /// Must be consumed before the next successful world step.
    pub fn latest_squad_changes(&self) -> &[SquadRosterChange] {
        &self.changes
    }

    pub fn latest_finished_commands(&self) -> &[FinishedNativeCommand] {
        &self.finished
    }

    pub fn latest_reward_emissions(&self) -> &[RewardEmission] {
        self.episode.latest_emissions()
    }

    pub fn latest_finalized_episode(&self) -> Option<NativeEpisodeFinal> {
        self.last_finalized
    }

    /// The multi-world Simulation.step_world returns StepCounts, so
    /// adapters retrieve the current completed tick's group commands
    /// here before allowing the next world step to clear scratch.
    pub fn latest_group_events(&self) -> Option<&[GroupRewardEvent]> {
        self.group_delivery.as_ref().map(
            NativeGroupRewardDelivery::events,
        )
    }

    /// Unity scaled timer callbacks are advanced on every Level tick,
    /// not only when a policy batch requests a new action. The caller
    /// supplies the freshly refreshed Level.State.Squads projection and
    /// the real authored NavigationRuntime. The command director may
    /// issue new waypoint/Guard speed orders, but never simulate physics.
    ///
    /// This must run only after the current combat events were reconciled.
    /// A terminal match does not execute further gameplay commands.
    pub fn advance_timed_commands(
        &mut self,
        completed_tick: u64,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        dt: f32,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        // This method is a separate Level callback boundary; enforce
        // once-per-step ordering here too, not only in the caller's
        // Simulation/GameRules integration.
        if completed_tick.checked_add(1) != Some(self.next_tick) ||
            self.last_advanced_command_tick == Some(completed_tick) {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick.saturating_sub(1),
                got: completed_tick,
            });
        }
        if self.completed() || self.commands.active_count() == 0 {
            return Ok(());
        }
        if let Err(error) = self.commands.advance_into(
            world, navigation, friendlies, dt, &mut self.timed_finished,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Commands(error));
        }
        self.finished.append(&mut self.timed_finished);
        self.last_advanced_command_tick = Some(completed_tick);
        Ok(())
    }

    pub fn measurements(&self) -> NativeLevelEventMeasurements {
        NativeLevelEventMeasurements {
            squads: self.roster.measurements(),
            commands: self.commands.measurements(),
            rewards: self.episode.measurements(),
            group_delivery: self.group_delivery.as_ref().map(
                NativeGroupRewardDelivery::measurements,
            ),
            ticks_integrated: self.next_tick - 1,
            pending_session_retirements: self.pending_session_retirements.len(),
        }
    }

    /// `completed_tick` must equal the owning world's completed fixed
    /// step count: 1, 2, 3... per episode. This rejects replay of the
    /// same events, lost steps and accidental cross-episode reuse.
    ///
    /// The minion allocator supplies a real Level-owned SquadLifetime,
    /// rather than a training-only guessed squad identity. Both metadata
    /// providers read the Level's actual persistent FleetShip values.
    /// A callback failure invalidates this Level's remaining episode,
    /// because a partial combat-event batch cannot be replayed safely.
    pub fn process_completed_tick(
        &mut self,
        world: &mut CombatWorld,
        completed_tick: u64,
        dt_seconds: f32,
        allocate_minion: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
        damage_metadata: impl FnMut(i64) -> Option<DamageRewardMetadata>,
        mined_tsv: impl FnMut(i64) -> Option<i32>,
    ) -> Result<NativeLevelTick<'_>, NativeLevelEventError> {
        if self.is_failed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if completed_tick != self.next_tick {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick, got: completed_tick,
            });
        }
        // The roster may create/destroy minions and retire completed
        // commands; the episode consumer sees the same authoritative
        // physical world and the source-order immutable event buffer.
        if let Err(error) = self.roster.reconcile_combat_tick(
            world, &mut self.commands,
            allocate_minion, &mut self.changes, &mut self.finished,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Roster(error));
        }
        // Preserve retirements across multi-step parallel rollout
        // windows. The shared WebSocket connection may not run until
        // after an entire world window completes. Late replies are
        // rejected by the connection's live-owner validation meanwhile.
        for &change in &self.changes {
            if let SquadRosterChange::SquadRetired { squad } = change {
                self.pending_session_retirements.push(squad);
            }
        }
        let reward = match self.episode.consume_combat_tick(
            world, dt_seconds, &self.changes, damage_metadata, mined_tsv,
        ) {
            Ok(result) => result,
            Err(error) => {
                self.failed = true;
                return Err(NativeLevelEventError::Rewards(error));
            }
        };
        // The group's side/episode membership is registered by the
        // actual policy controllers. This single event boundary delivers
        // immediate combat/economic shaping and terminal rewards without
        // duplicating them for each participating agent.
        let group_events = if let Some(group) = self.group_delivery.as_mut() {
            match group.consume_tick(
                completed_tick, reward.emissions, reward.finalized,
            ) {
                Ok(events) => Some(events),
                Err(error) => {
                    self.failed = true;
                    return Err(NativeLevelEventError::Group(error));
                }
            }
        } else {
            None
        };
        self.next_tick = self.next_tick.checked_add(1)
            .expect("monotonic native Level tick exhausted");
        self.last_finalized = reward.finalized;
        Ok(NativeLevelTick {
            squad_changes: &self.changes,
            finished_commands: &self.finished,
            pending_session_retirements: &self.pending_session_retirements,
            reward_emissions: reward.emissions,
            group_events,
            finalized: reward.finalized,
        })
    }
}
