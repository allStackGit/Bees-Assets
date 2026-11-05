//! One fixed-step implementation for both native graphical and headless
//! Levels: policy decisions -> canonical CombatWorld -> squad/HiveMind
//! lifecycle -> episode reward/terminal processing. No other combat rules
//! or training-only physics are reimplemented here.
//!
//! Simulation<NativeLevelState<...>, NativeLevelRules> owns the world's
//! episode revision, seeded RNG, aggregate measurements and failure state.
//! The caller must reset completed episodes before asking for a new step.

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatWorld, CombatWorldError};
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::physics::CollisionMatrix;
use bees_sim::policy::Decision;
use bees_sim::rl_shaping::DamageRewardMetadata;
use bees_sim::simulation::{GameRules, World};
use bees_sim::telemetry::StepCounts;

use crate::commands::{GameCommandError, NativeFriendlySquad};
use crate::level_events::{
    NativeLevelEventError, NativeLevelEventProcessor,
};
use crate::squads::{NativeSquadRoster, SquadRosterError};

/// The native game Level's authoritative FleetShip metadata. This is NOT
/// computed from collider size or estimated from training observations.
pub trait NativeLevelRewardMetadata {
    fn damage_metadata(&self, ship_id: i64) -> Option<DamageRewardMetadata>;
    fn current_mined_tsv(&self, ship_id: i64) -> Option<i32>;
}

/// Source-facing gameplay inputs for timed Hive Mind commands.
    /// Must be refreshed after the combat/roster step from live Level
    /// geometry, preserving Level.State.Squads order. Command activation
    /// and timer callbacks use this same authoritative view.
pub trait NativeLevelCommandContext {
    fn collect_live_friendlies(
        &mut self,
        combat: &CombatWorld,
        roster: &NativeSquadRoster,
        output: &mut Vec<NativeFriendlySquad>,
    ) -> Result<(), GameCommandError>;

    fn navigation(&self) -> &NavigationRuntime;
}

pub struct NativeLevelState<M, A> {
    pub combat: CombatWorld,
    pub events: NativeLevelEventProcessor,
    pub metadata: M,
    /// Level-owned transient minion SquadLifetime allocation.
    /// Keeping it separate from immutable reward metadata permits safe
    /// borrowing of both providers during the one event-processing pass.
    pub allocate_minion: A,
    /// Reused source Level.State.Squads snapshot; no per-frame allocation.
    command_friendlies: Vec<NativeFriendlySquad>,
}

impl<M, A> NativeLevelState<M, A> {
    pub fn new(
        combat: CombatWorld,
        events: NativeLevelEventProcessor,
        metadata: M,
        allocate_minion: A,
    ) -> Self {
        Self {
            combat, events, metadata, allocate_minion,
            command_friendlies: Vec::new(),
        }
    }
}

#[derive(Debug)]
pub enum NativeLevelStepError {
    Combat(CombatWorldError),
    Events(NativeLevelEventError),
    EpisodeComplete,
    TickExhausted,
}

impl From<CombatWorldError> for NativeLevelStepError {
    fn from(error: CombatWorldError) -> Self {
        Self::Combat(error)
    }
}
impl From<NativeLevelEventError> for NativeLevelStepError {
    fn from(error: NativeLevelEventError) -> Self {
        Self::Events(error)
    }
}

/// Stateless immutable collision settings; world scratch, game state,
/// HiveMind session and rewards are isolated per World<NativeLevelState>.
pub struct NativeLevelRules {
    pub collision_matrix: CollisionMatrix,
}

impl<M, A> GameRules<NativeLevelState<M, A>> for NativeLevelRules
where
    M: NativeLevelRewardMetadata + NativeLevelCommandContext,
    A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
{
    type Error = NativeLevelStepError;

    fn is_complete(
        &self, world: &World<NativeLevelState<M, A>>,
    ) -> bool {
        world.state.events.completed()
    }

    fn step(
        &self,
        world: &mut World<NativeLevelState<M, A>>,
        decisions: &[Decision],
        dt_seconds: f32,
    ) -> Result<StepCounts, Self::Error> {
        let completed_tick = world.tick().checked_add(1)
            .ok_or(NativeLevelStepError::TickExhausted)?;
        let state = &mut world.state;
        if state.events.completed() {
            return Err(NativeLevelStepError::EpisodeComplete);
        }
        if state.events.is_failed() {
            return Err(NativeLevelStepError::Events(
                NativeLevelEventError::FailedLevel,
            ));
        }
        // No independent policy/physics rules in gameplay or training:
        // both adapters invoke CombatWorld's one canonical transaction.
        let counts = state.combat.step_policy_combat_with_random(
            decisions, dt_seconds, &self.collision_matrix,
            &mut world.random,
        )?;
        let metadata = &state.metadata;
        let allocate = &mut state.allocate_minion;
        state.events.process_completed_tick(
            &mut state.combat, completed_tick, dt_seconds,
            |mother, beacon| allocate(mother, beacon),
            |id| metadata.damage_metadata(id),
            |id| metadata.current_mined_tsv(id),
        )?;
        if state.events.has_active_commands() && !state.events.completed() {
            // Snapshot from the actual current ship/squad positions after
            // casualty removal; no stale formation or Guard target.
            state.metadata.collect_live_friendlies(
                &state.combat, state.events.roster(),
                &mut state.command_friendlies,
            ).map_err(|err|
                NativeLevelStepError::Events(
                    NativeLevelEventError::Commands(err)
                )
            )?;
            state.events.advance_timed_commands(
                completed_tick, &mut state.combat,
                state.metadata.navigation(),
                &state.command_friendlies, dt_seconds,
            )?;
        }
        Ok(counts)
    }
}
