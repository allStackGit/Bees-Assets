//! One source-ordered combat-event handoff for a native Level.
//!
//! Call once after the shared CombatWorld's fixed step, before its event
//! buffer is cleared by the next step. This is NOT a second combat engine,
//! scheduler, reward model or a substitute for the Unity/MA-POCA adapter.
//! All mutable scratch is reused per Level, never process-global.

use bees_hivemind::session::{HiveMindSession, SquadLifetime};
use bees_sim::combat_world::CombatWorld;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::rl_shaping::{DamageRewardMetadata, RewardEmission};

use crate::commands::{
    CommandMeasurements, FinishedNativeCommand, GameCommandError,
    NativeFriendlySquad, NativeSquadCommandDirector,
};
use crate::episode::{
    NativeEpisodeFinal, NativeEpisodeRewardCoordinator,
    NativeEpisodeRewardError, NativeEpisodeRewardMeasurements,
};
use crate::group_rewards::{
    GroupRewardError, GroupRewardEvent, NativeGroupRewardDelivery,
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
    FailedLevel,
}

pub struct NativeLevelTick<'a> {
    /// Processed in exact CombatWorld event order.
    pub squad_changes: &'a [SquadRosterChange],
    /// The authoritative game must settle real TSV and unmatched
    /// OutcomeIds; these command reservations are NOT silently dropped.
    pub finished_commands: &'a [FinishedNativeCommand],
    /// Source-order bounded shaping plus separate uncapped economics.
    /// Reward delivery to team/agent groups belongs to the host adapter.
    pub reward_emissions: &'a [RewardEmission],
    /// Optional source MA-POCA wire commands, emitted once per side.
    /// EloMarker is transport-only and must be stripped before PPO.
    pub group_events: Option<&'a [GroupRewardEvent]>,
    pub finalized: Option<NativeEpisodeFinal>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelEventMeasurements {
    pub squads: SquadRosterMeasurements,
    pub commands: CommandMeasurements,
    pub rewards: NativeEpisodeRewardMeasurements,
    pub ticks_integrated: u64,
}

/// Owns the authoritative native Level's squad/Hive Mind/episode state.
/// It does not own a second CombatWorld: the caller passes the same world
/// used by graphics and the Simulation fixed-step rules.
pub struct NativeLevelEventProcessor {
    roster: NativeSquadRoster,
    commands: NativeSquadCommandDirector,
    hive_mind: HiveMindSession,
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
            hive_mind: HiveMindSession::new(),
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

    pub fn session_mut(&mut self) -> &mut HiveMindSession {
        &mut self.hive_mind
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
            ticks_integrated: self.next_tick - 1,
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
            world, &mut self.commands, &mut self.hive_mind,
            allocate_minion, &mut self.changes, &mut self.finished,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Roster(error));
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
            reward_emissions: reward.emissions,
            group_events,
            finalized: reward.finalized,
        })
    }
}
