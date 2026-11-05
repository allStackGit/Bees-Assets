//! One source-ordered combat-event handoff for a native Level.
//!
//! Call once after the shared CombatWorld's fixed step, before its event
//! buffer is cleared by the next step. This is NOT a second combat engine,
//! scheduler, reward model or a substitute for the Unity/MA-POCA adapter.
//! All mutable scratch is reused per Level, never process-global.

use bees_hivemind::session::{HiveMindSession, SquadLifetime};
use bees_sim::combat_world::CombatWorld;
use bees_sim::rl_shaping::{DamageRewardMetadata, RewardEmission};

use crate::commands::{
    CommandMeasurements, FinishedNativeCommand, NativeSquadCommandDirector,
};
use crate::episode::{
    NativeEpisodeFinal, NativeEpisodeRewardCoordinator,
    NativeEpisodeRewardError, NativeEpisodeRewardMeasurements,
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
    next_tick: u64,
    failed: bool,
    changes: Vec<SquadRosterChange>,
    finished: Vec<FinishedNativeCommand>,
}

impl NativeLevelEventProcessor {
    pub fn new(episode: NativeEpisodeRewardCoordinator) -> Self {
        Self {
            roster: NativeSquadRoster::new(),
            commands: NativeSquadCommandDirector::new(),
            hive_mind: HiveMindSession::new(),
            episode,
            next_tick: 1,
            failed: false,
            changes: Vec::new(),
            finished: Vec::new(),
        }
    }

    /// Setup and live command activation use the same objects, not
    /// independently reconstructed per-tick registries.
    pub fn roster_mut(&mut self) -> &mut NativeSquadRoster {
        &mut self.roster
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

    pub fn is_failed(&self) -> bool {
        self.failed || self.roster.is_failed() || self.episode.is_failed()
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
        self.next_tick += 1;
        Ok(NativeLevelTick {
            squad_changes: &self.changes,
            finished_commands: &self.finished,
            reward_emissions: reward.emissions,
            finalized: reward.finalized,
        })
    }
}
