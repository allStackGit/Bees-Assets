//! One fixed-step implementation for both native graphical and headless
//! Levels: policy decisions -> canonical CombatWorld -> squad/HiveMind
//! lifecycle -> episode reward/terminal processing. No other combat rules
//! or training-only physics are reimplemented here.
//!
//! Simulation<NativeLevelState<...>, NativeLevelRules> owns the world's
//! episode revision, seeded RNG, aggregate measurements and failure state.
//! The caller must reset completed episodes before asking for a new step.

use std::collections::BTreeMap;
use std::num::NonZeroU32;

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

/// Unity Agent.FixedUpdate increments each participating agent's own
/// decision counter and requests a new action only after DecisionPeriod
/// fixed steps. New agents begin at zero, not the global frame modulo.
pub struct NativeDecisionCadence {
    period: NonZeroU32,
    next_due_by_ship: BTreeMap<i64, u64>,
}

impl NativeDecisionCadence {
    pub fn new(period: NonZeroU32) -> Self {
        Self { period, next_due_by_ship: BTreeMap::new() }
    }

    /// Reconcile live policy registration at the fixed-step boundary.
    /// Adding a new controller starts its first decision countdown
    /// without restarting any existing controller's schedule.
    pub fn synchronize(
        &mut self, combat: &CombatWorld, completed_ticks: u64,
    ) -> Result<(), NativeLevelStepError> {
        let first_due = completed_ticks.checked_add(
            u64::from(self.period.get()),
        ).ok_or(NativeLevelStepError::TickExhausted)?;
        self.next_due_by_ship.retain(|&id, _|
            combat.policy_frame(id).is_some(),
        );
        for ship_id in combat.bound_policy_agent_ids() {
            self.next_due_by_ship.entry(ship_id).or_insert(first_due);
        }
        Ok(())
    }

    pub fn is_due(&self, ship_id: i64, next_tick: u64) -> bool {
        self.next_due_by_ship.get(&ship_id).copied() == Some(next_tick)
    }

    pub fn due_count(&self, next_tick: u64) -> usize {
        self.next_due_by_ship.values()
            .filter(|&&due| due == next_tick).count()
    }

    /// Advance due clocks only AFTER the shared game step and Level
    /// event handoff succeeded; a failed physics tick cannot consume
    /// neural action decisions.
    pub fn complete_step(
        &mut self, completed_tick: u64,
    ) -> Result<(), NativeLevelStepError> {
        let next = completed_tick.checked_add(
            u64::from(self.period.get()),
        ).ok_or(NativeLevelStepError::TickExhausted)?;
        for due in self.next_due_by_ship.values_mut() {
            if *due == completed_tick { *due = next; }
        }
        Ok(())
    }
}

pub struct NativeLevelState<M, A> {
    pub combat: CombatWorld,
    pub events: NativeLevelEventProcessor,
    pub metadata: M,
    /// Level-owned transient minion SquadLifetime allocation.
    /// Keeping it separate from immutable reward metadata permits safe
    /// borrowing of both providers during the one event-processing pass.
    pub allocate_minion: A,
    /// Individual agent clocks follow the active native policy registry.
    /// The source's default decision period is 5 physics steps.
    pub decisions: NativeDecisionCadence,
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
        // Source RlOneVsOneTrainingOptions.DefaultDecisionPeriod = 5.
        Self::with_decision_period(
            combat, events, metadata, allocate_minion,
            NonZeroU32::new(5).expect("source decision period"),
        )
    }

    pub fn with_decision_period(
        combat: CombatWorld,
        events: NativeLevelEventProcessor,
        metadata: M,
        allocate_minion: A,
        period: NonZeroU32,
    ) -> Self {
        Self {
            combat, events, metadata, allocate_minion,
            decisions: NativeDecisionCadence::new(period),
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
    InvalidDecisionCadence,
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
        state.decisions.synchronize(&state.combat, world.tick())?;
        let due = state.decisions.due_count(completed_tick);
        if due != decisions.len() || decisions.iter().any(|decision| {
            let Ok(id) = i64::try_from(decision.agent_id) else {
                return true;
            };
            !state.decisions.is_due(id, completed_tick)
        }) {
            return Err(NativeLevelStepError::InvalidDecisionCadence);
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
        state.decisions.complete_step(completed_tick)?;
        Ok(counts)
    }
}
