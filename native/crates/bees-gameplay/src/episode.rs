//! One game-owned RL reward/terminal adapter for a native Level tick.
//!
//! All sources come from bees-sim's authoritative CombatWorld events.
//! It never simulates combat, invents mining/fleet metadata, guesses
//! deaths from killer IDs, or silently emits training examples.
//! MA-POCA delivery, RL agent groups and persistent TSV are external.

use bees_sim::combat_world::CombatWorld;
use bees_sim::episode_terminal::{
    EpisodeDeathAttribution, EpisodeEconomicError, EpisodeEconomicRewards,
    EpisodeTerminalError, EpisodeTerminalMeasurements, EpisodeTerminalResult,
    EpisodeTerminalRewards, NativeEpisodeTerminal, SurvivalObjective, SurvivalStatus,
};
use bees_sim::rl_economy::{
    EconomicLedgerError, EconomicLedgerMeasurements, EpisodeEconomicLedger,
};
use bees_sim::rl_discovery::{
    DiscoveryError, DiscoveryKind, DiscoveryMeasurements, DiscoverySightings, EpisodeDiscoveryLedger,
};
use bees_sim::rl_shaping::{
    DamageRewardMetadata, DamageShapingError, DamageShapingMeasurements,
    EpisodeShaping, RewardEmission,
};

use crate::squads::{
    observe_terminal_after_roster, observe_terminal_rewards_after_roster, SquadRosterChange,
};

#[derive(Debug)]
pub enum NativeEpisodeRewardError {
    InvalidShaping(&'static str),
    Terminal(EpisodeTerminalError),
    Economic(EconomicLedgerError),
    TerminalEconomic(EpisodeEconomicError),
    Damage(DamageShapingError),
    Discovery(DiscoveryError),
    DiscoveryNotConfigured,
    InvalidCapabilitySide(i32),
    AlreadyComplete,
    /// An earlier source or metadata error may have partially consumed a
    /// tick's reward events. Only a fresh episode can resume safely.
    FailedEpisode,
}

impl From<EpisodeTerminalError> for NativeEpisodeRewardError {
    fn from(error: EpisodeTerminalError) -> Self {
        Self::Terminal(error)
    }
}
impl From<EconomicLedgerError> for NativeEpisodeRewardError {
    fn from(error: EconomicLedgerError) -> Self {
        Self::Economic(error)
    }
}
impl From<EpisodeEconomicError> for NativeEpisodeRewardError {
    fn from(error: EpisodeEconomicError) -> Self {
        Self::TerminalEconomic(error)
    }
}
impl From<DamageShapingError> for NativeEpisodeRewardError {
    fn from(error: DamageShapingError) -> Self {
        Self::Damage(error)
    }
}
impl From<DiscoveryError> for NativeEpisodeRewardError {
    fn from(error: DiscoveryError) -> Self {
        Self::Discovery(error)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeEpisodeFinal {
    pub result: EpisodeTerminalResult,
    pub terminal_rewards: EpisodeTerminalRewards,
    pub economic_rewards: EpisodeEconomicRewards,
}

/// Returned slices refer to the adapter's reused scratch memory. Consume
/// before the next tick or copy into the ML-Agents group-reward delivery
/// buffer; they are NOT automatically delivered to an agent.
pub struct NativeTickReward<'a> {
    pub emissions: &'a [RewardEmission],
    pub finalized: Option<NativeEpisodeFinal>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeEpisodeRewardMeasurements {
    pub combat_damage: DamageShapingMeasurements,
    pub mining_economy: EconomicLedgerMeasurements,
    pub discovery: DiscoveryMeasurements,
    pub termination: EpisodeTerminalMeasurements,
}

pub struct NativeEpisodeRewardCoordinator {
    bee_side: i32,
    human_side: i32,
    terminal: NativeEpisodeTerminal,
    mode: EpisodeMode,
    failed: bool,
    merged_emissions: Vec<RewardEmission>,
}

enum EpisodeMode {
    Game(DiscoverySightings),
    Training(Box<TrainingRewards>),
}

struct TrainingRewards {
    bee_starting_tsv: i32,
    human_starting_tsv: i32,
    attribution: EpisodeDeathAttribution,
    shaping: EpisodeShaping,
    economy: EpisodeEconomicLedger,
    discovery: Option<EpisodeDiscoveryLedger>,
    damage_emissions: Vec<RewardEmission>,
    economic_emissions: Vec<RewardEmission>,
}

impl NativeEpisodeRewardCoordinator {
    pub fn new(
        bee_side: i32,
        human_side: i32,
        bee_starting_tsv: i32,
        human_starting_tsv: i32,
        timeout_seconds: f32,
    ) -> Result<Self, NativeEpisodeRewardError> {
        let terminal = NativeEpisodeTerminal::new(
            bee_side, human_side, timeout_seconds,
        )?;
        let attribution = EpisodeDeathAttribution::new(
            bee_side, human_side,
        )?;
        let economy = EpisodeEconomicLedger::new(
            bee_side, human_side,
            bee_starting_tsv, human_starting_tsv,
        )?;
        let combined_tsv = bee_starting_tsv.saturating_add(human_starting_tsv);
        let shaping = EpisodeShaping::new(bee_side, human_side, combined_tsv)
            .map_err(NativeEpisodeRewardError::InvalidShaping)?;
        Ok(Self {
            bee_side, human_side,
            terminal,
            mode: EpisodeMode::Training(Box::new(TrainingRewards {
                bee_starting_tsv, human_starting_tsv,
                attribution, shaping, economy,
                discovery: None,
                damage_emissions: Vec::new(),
                economic_emissions: Vec::new(),
            })),
            failed: false,
            merged_emissions: Vec::new(),
        })
    }

    pub fn new_game(bee_side: i32, human_side: i32) -> Result<Self, NativeEpisodeRewardError> {
        Ok(Self {
            bee_side, human_side,
            terminal: NativeEpisodeTerminal::new_game(bee_side, human_side)?,
            mode: EpisodeMode::Game(DiscoverySightings::default()),
            failed: false,
            merged_emissions: Vec::new(),
        })
    }

    pub fn arm_survival(
        &mut self, objective: SurvivalObjective, fixed_dt_seconds: f32,
    ) -> Result<(), NativeEpisodeRewardError> {
        self.terminal.arm_survival(objective, fixed_dt_seconds)?;
        Ok(())
    }

    pub fn measurements(&self) -> NativeEpisodeRewardMeasurements {
        let termination = self.terminal.measurements();
        match &self.mode {
            EpisodeMode::Game(sightings) => NativeEpisodeRewardMeasurements {
                discovery: sightings.measurements(),
                termination,
                ..NativeEpisodeRewardMeasurements::default()
            },
            EpisodeMode::Training(training) => NativeEpisodeRewardMeasurements {
                combat_damage: training.shaping.damage_measurements(),
                mining_economy: training.economy.measurements(),
                discovery: training.discovery.as_ref().map_or(
                    DiscoveryMeasurements::default(),
                    EpisodeDiscoveryLedger::measurements,
                ),
                termination,
            },
        }
    }

    pub fn result(&self) -> Option<EpisodeTerminalResult> {
        self.terminal.result()
    }

    pub fn sides(&self) -> [i32; 2] {
        [self.bee_side, self.human_side]
    }

    pub fn survival_status(&self) -> Option<SurvivalStatus> {
        self.terminal.survival_status()
    }

    pub fn normalized_episode_progress(&self) -> f32 {
        self.terminal.normalized_episode_progress()
    }

    /// Current retainable mineral TSV uses the same economic ledger as the
    /// eventual winner's terminal reward. Unlike a scene snapshot, this
    /// changes when mining, death or safe WarpGate returns are processed.
    /// None means this is an ordinary non-training game episode.
    pub fn retainable_mined_tsv(&self, side: i32) -> Option<i32> {
        if side != self.bee_side && side != self.human_side {
            return None;
        }
        match &self.mode {
            EpisodeMode::Game(_) => None,
            EpisodeMode::Training(training) =>
                Some(training.economy.retainable_mined_tsv(side)),
        }
    }

    pub fn latest_emissions(&self) -> &[RewardEmission] {
        &self.merged_emissions
    }

    pub fn is_failed(&self) -> bool {
        self.failed
    }

    /// Capture exact source initial discovery totals before any first
    /// sightings can receive reward. Category order is enemy ships,
    /// mining asteroids, static obstacles, map objects. Dynamic collision
    /// asteroids use the Unity convergent sequence, not a baseline.
    pub fn configure_discovery(
        &mut self, baselines: [[i32; 4]; 2],
    ) -> Result<(), NativeEpisodeRewardError> {
        if self.failed {
            return Err(NativeEpisodeRewardError::FailedEpisode);
        }
        let sides = self.sides();
        let EpisodeMode::Training(training) = &mut self.mode else {
            return Err(NativeEpisodeRewardError::DiscoveryNotConfigured);
        };
        if self.terminal.result().is_some() || training.discovery.is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        training.discovery = Some(EpisodeDiscoveryLedger::new(sides, baselines)?);
        Ok(())
    }

    /// Call only from source-verified learned sightings, never ordinary
    /// nearby geometry. Native encounters, misses and weapon hits all
    /// consume the SAME bounded positive/negative shaping budget.
    /// Return the immediate emission for MA-POCA delivery by the host.
    pub fn record_verified_discovery(
        &mut self, side: i32, kind: DiscoveryKind,
        entity_id: i64, authored_value: i32,
    ) -> Result<Option<RewardEmission>, NativeEpisodeRewardError> {
        self.record_verified_discovery_with_freshness(
            side, kind, entity_id, authored_value,
        ).map(|(_, reward)| reward)
    }

    /// Keep first-contact causality distinct from shaping reward amount:
    /// the reward may be None when the tactical budget is exhausted.
    pub fn record_verified_discovery_with_freshness(
        &mut self, side: i32, kind: DiscoveryKind,
        entity_id: i64, authored_value: i32,
    ) -> Result<(bool, Option<RewardEmission>), NativeEpisodeRewardError> {
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        if self.failed {
            return Err(NativeEpisodeRewardError::FailedEpisode);
        }
        let sides = self.sides();
        let sighting = match &mut self.mode {
            EpisodeMode::Game(sightings) => sightings.record(sides, side, kind, entity_id, authored_value)
                .map(|index| (index.is_some(), None)),
            EpisodeMode::Training(training) => {
                let Some(ledger) = training.discovery.as_mut() else {
                    self.failed = true;
                    return Err(NativeEpisodeRewardError::DiscoveryNotConfigured);
                };
                ledger.first_sighting_with_freshness(
                    &mut training.shaping, side, kind, entity_id, authored_value,
                )
            }
        };
        match sighting {
            Ok(emission) => Ok(emission),
            Err(err) => {
                self.failed = true;
                Err(err.into())
            }
        }
    }

    /// Source RecordSuccessfulCapabilityOutcome. Only the actual native
    /// gameplay action may report its audited TSV gain or preserved
    /// exit value. This never fabricates healing/mining/warp behavior.
    pub fn record_successful_capability_outcome(
        &mut self, side: i32, tsv_value: i32,
    ) -> Result<Option<RewardEmission>, NativeEpisodeRewardError> {
        if self.failed {
            return Err(NativeEpisodeRewardError::FailedEpisode);
        }
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        if !self.sides().contains(&side) {
            self.failed = true;
            return Err(NativeEpisodeRewardError::InvalidCapabilitySide(side));
        }
        Ok(match &mut self.mode {
            EpisodeMode::Game(_) => None,
            EpisodeMode::Training(training) => training.shaping
                .record_successful_capability_outcome(side, tsv_value),
        })
    }

    /// Called by the Level's real mining/campaign integration whenever
    /// FleetShip.MineralsMinedThisLevel is updated. This data never
    /// comes from ShipVitals.mined_cargo or a policy observation guess.
    pub fn update_mined_value(
        &mut self,
        world: &CombatWorld,
        ship_id: i64,
        fleet_ship_mined_tsv: i32,
    ) -> Result<(), NativeEpisodeRewardError> {
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        if self.failed {
            return Err(NativeEpisodeRewardError::FailedEpisode);
        }
        let EpisodeMode::Training(training) = &mut self.mode else {
            return Ok(());
        };
        match training.economy.update_mined_value(
            world, ship_id, fleet_ship_mined_tsv,
        ) {
            Ok(()) => Ok(()),
            Err(error) => {
                self.failed = true;
                Err(error.into())
            }
        }
    }

    /// Called after the native Level processes the current CombatWorld
    /// events through NativeSquadRoster.reconcile_combat_tick. Only one
    /// reward coordinator may consume a given world's tick.
    ///
    /// Metadata closures must read the Level's actual persistent fleet
    /// accounting. Missing values cause an error; the owning world/episode
    /// must be marked failed rather than silently continuing with zero
    /// damage rewards, fabricated economics or invalid rollout samples.
    pub fn consume_combat_tick(
        &mut self,
        world: &mut CombatWorld,
        dt_seconds: f32,
        squad_changes: &[SquadRosterChange],
        damage_metadata: impl FnMut(i64) -> Option<DamageRewardMetadata>,
        current_mined_tsv: impl FnMut(i64) -> Option<i32>,
    ) -> Result<NativeTickReward<'_>, NativeEpisodeRewardError> {
        if self.failed {
            return Err(NativeEpisodeRewardError::FailedEpisode);
        }
        let finalized = match self.consume_combat_tick_inner(
            world, dt_seconds, squad_changes,
            damage_metadata, current_mined_tsv,
        ) {
            Ok(finalized) => finalized,
            Err(error) => {
                self.failed = true;
                return Err(error);
            }
        };
        Ok(NativeTickReward {
            emissions: &self.merged_emissions,
            finalized,
        })
    }

    fn consume_combat_tick_inner(
        &mut self,
        world: &mut CombatWorld,
        dt_seconds: f32,
        squad_changes: &[SquadRosterChange],
        damage_metadata: impl FnMut(i64) -> Option<DamageRewardMetadata>,
        current_mined_tsv: impl FnMut(i64) -> Option<i32>,
    ) -> Result<Option<NativeEpisodeFinal>, NativeEpisodeRewardError> {
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err(NativeEpisodeRewardError::Terminal(
                EpisodeTerminalError::InvalidStep,
            ));
        }
        let EpisodeMode::Training(training) = &mut self.mode else {
            return Ok(observe_terminal_after_roster(
                &mut self.terminal, world, dt_seconds, squad_changes,
            )?.map(|result| NativeEpisodeFinal {
                result,
                terminal_rewards: EpisodeTerminalRewards::default(),
                economic_rewards: EpisodeEconomicRewards::default(),
            }));
        };
        // Both consumers inspect the same world-owned event store. Scratch
        // buffers survive across ticks to avoid allocations in quiet steps.
        training.shaping.consume_combat_shaping_events(
            world, world.events(), damage_metadata,
            &mut training.damage_emissions,
        )?;
        training.economy.consume_ship_deaths(
            world, world.events(), current_mined_tsv,
            &mut training.economic_emissions,
        )?;
        let completed = observe_terminal_rewards_after_roster(
            &mut self.terminal,
            &mut training.attribution,
            world, dt_seconds, squad_changes,
        )?;
        self.merged_emissions.clear();
        self.merged_emissions.extend_from_slice(&training.damage_emissions);
        self.merged_emissions.extend_from_slice(&training.economic_emissions);
        let finalized = if let Some(done) = completed {
            let economic = self.terminal.terminal_economic_rewards(
                training.bee_starting_tsv,
                training.human_starting_tsv,
                training.economy.retainable_mined_tsv(self.bee_side),
                training.economy.retainable_mined_tsv(self.human_side),
            )?.expect("terminal outcome just latched");
            Some(NativeEpisodeFinal {
                result: done.outcome,
                terminal_rewards: done.rewards,
                economic_rewards: economic,
            })
        } else {
            None
        };
        Ok(finalized)
    }
}
