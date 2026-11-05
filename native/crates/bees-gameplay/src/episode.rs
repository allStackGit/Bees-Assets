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
    EpisodeTerminalRewards, NativeEpisodeTerminal,
};
use bees_sim::rl_economy::{
    EconomicLedgerError, EconomicLedgerMeasurements, EpisodeEconomicLedger,
};
use bees_sim::rl_discovery::{
    DiscoveryError, DiscoveryKind, DiscoveryMeasurements, EpisodeDiscoveryLedger,
};
use bees_sim::rl_shaping::{
    DamageRewardMetadata, DamageShapingError, DamageShapingMeasurements,
    EpisodeShaping, RewardEmission,
};

use crate::squads::{
    observe_terminal_rewards_after_roster, SquadRosterChange,
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
    AlreadyComplete,
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
    bee_starting_tsv: i32,
    human_starting_tsv: i32,
    terminal: NativeEpisodeTerminal,
    attribution: EpisodeDeathAttribution,
    shaping: EpisodeShaping,
    economy: EpisodeEconomicLedger,
    discovery: Option<EpisodeDiscoveryLedger>,
    damage_emissions: Vec<RewardEmission>,
    economic_emissions: Vec<RewardEmission>,
    merged_emissions: Vec<RewardEmission>,
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
            bee_starting_tsv, human_starting_tsv,
            terminal, attribution, shaping, economy,
            discovery: None,
            damage_emissions: Vec::new(),
            economic_emissions: Vec::new(),
            merged_emissions: Vec::new(),
        })
    }

    pub fn measurements(&self) -> NativeEpisodeRewardMeasurements {
        NativeEpisodeRewardMeasurements {
            combat_damage: self.shaping.damage_measurements(),
            mining_economy: self.economy.measurements(),
            discovery: self.discovery.as_ref().map_or(
                DiscoveryMeasurements::default(),
                EpisodeDiscoveryLedger::measurements,
            ),
            termination: self.terminal.measurements(),
        }
    }

    pub fn result(&self) -> Option<EpisodeTerminalResult> {
        self.terminal.result()
    }

    /// Capture exact source initial discovery totals before any first
    /// sightings can receive reward. Category order is enemy ships,
    /// mining asteroids, static obstacles, map objects. Dynamic collision
    /// asteroids use the Unity convergent sequence, not a baseline.
    pub fn configure_discovery(
        &mut self, baselines: [[i32; 4]; 2],
    ) -> Result<(), NativeEpisodeRewardError> {
        if self.terminal.result().is_some() || self.discovery.is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        self.discovery = Some(EpisodeDiscoveryLedger::new(
            [self.bee_side, self.human_side], baselines,
        )?);
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
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        let ledger = self.discovery.as_mut()
            .ok_or(NativeEpisodeRewardError::DiscoveryNotConfigured)?;
        Ok(ledger.first_sighting(
            &mut self.shaping, side, kind, entity_id, authored_value,
        )?)
    }

    /// Source RecordResolvedProjectileMiss: exactly one completed root
    /// shot, after the physical near-miss classification is finalized.
    pub fn record_resolved_projectile_miss(
        &mut self, shooter_side: i32, near: bool,
    ) -> Result<Option<RewardEmission>, NativeEpisodeRewardError> {
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        Ok(self.shaping.record_resolved_projectile_miss(shooter_side, near))
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
        self.economy.update_mined_value(
            world, ship_id, fleet_ship_mined_tsv,
        )?;
        Ok(())
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
        if self.terminal.result().is_some() {
            return Err(NativeEpisodeRewardError::AlreadyComplete);
        }
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err(NativeEpisodeRewardError::Terminal(
                EpisodeTerminalError::InvalidStep,
            ));
        }
        // Both consumers inspect the same world-owned event store. Scratch
        // buffers survive across ticks to avoid allocations in quiet steps.
        self.shaping.consume_damage_events(
            world, world.events(), damage_metadata,
            &mut self.damage_emissions,
        )?;
        self.economy.consume_ship_deaths(
            world, world.events(), current_mined_tsv,
            &mut self.economic_emissions,
        )?;
        let completed = observe_terminal_rewards_after_roster(
            &mut self.terminal,
            &mut self.attribution,
            world, dt_seconds, squad_changes,
        )?;
        self.merged_emissions.clear();
        self.merged_emissions.extend_from_slice(&self.damage_emissions);
        self.merged_emissions.extend_from_slice(&self.economic_emissions);
        let finalized = if let Some(done) = completed {
            let economic = self.terminal.terminal_economic_rewards(
                self.bee_starting_tsv,
                self.human_starting_tsv,
                self.economy.retainable_mined_tsv(self.bee_side),
                self.economy.retainable_mined_tsv(self.human_side),
            )?.expect("terminal outcome just latched");
            Some(NativeEpisodeFinal {
                result: done.outcome,
                terminal_rewards: done.rewards,
                economic_rewards: economic,
            })
        } else {
            None
        };
        Ok(NativeTickReward {
            emissions: &self.merged_emissions,
            finalized,
        })
    }
}
