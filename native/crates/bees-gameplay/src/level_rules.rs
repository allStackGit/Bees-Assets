//! One fixed-step implementation for both native graphical and headless
//! Levels: policy decisions -> canonical CombatWorld -> squad/HiveMind
//! lifecycle -> episode reward/terminal processing. No other combat rules
//! or training-only physics are reimplemented here.
//!
//! Simulation<NativeLevelState<...>, NativeLevelRules> owns the world's
//! episode revision, seeded RNG, aggregate measurements and failure state.
//! The caller must reset completed episodes before asking for a new step.

use std::collections::{BTreeMap, HashMap};
use std::num::NonZeroU32;

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatEvent, CombatWorld, CombatWorldError};
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

    /// Add the exact native mining yield to the saved
    /// FleetShip.MineralsMinedThisLevel and return its new absolute value.
    /// This must equal CombatWorld's already-applied provisional total:
    /// divergent external mining changes require explicit pre-step sync,
    /// not an invisible post-damage adjustment to Ship.Tsv.
    /// No default implementation: a Level without persisted fleet state
    /// must not silently acknowledge an action and lose mined resources.
    fn credit_mined_minerals(&mut self, ship_id: i64, amount: i32) -> Option<i32>;
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

/// Unity Agent.FixedUpdate requests a decision after each controller's
/// own DecisionPeriod successful fixed steps, not every simulation tick.
/// One clock is enough: the owning World tick plus each ship's bind tick.
pub struct NativeDecisionCadence {
    period: NonZeroU32,
    bound_at_tick: BTreeMap<i64, u64>,
}

impl NativeDecisionCadence {
    pub fn new(period: NonZeroU32) -> Self {
        Self { period, bound_at_tick: BTreeMap::new() }
    }

    /// Reconcile the single authoritative policy roster at a decision
    /// boundary. New controllers start at 0; retiring a ship removes its
    /// counter. No independent timer advances on failed simulation steps.
    pub fn synchronize(
        &mut self, combat: &CombatWorld, completed_ticks: u64,
    ) {
        self.bound_at_tick.retain(|&id, _|
            combat.policy_frame(id).is_some(),
        );
        for ship_id in combat.bound_policy_agent_ids() {
            self.bound_at_tick.entry(ship_id).or_insert(completed_ticks);
        }
    }

    fn due_from_binding(&self, bound_at: u64, next_tick: u64) -> bool {
        next_tick > bound_at &&
            (next_tick - bound_at) % u64::from(self.period.get()) == 0
    }

    pub fn is_due(&self, ship_id: i64, next_tick: u64) -> bool {
        self.bound_at_tick.get(&ship_id).is_some_and(|&at|
            self.due_from_binding(at, next_tick),
        )
    }

    /// Sorted, allocation-free per-agent selection for the Level's
    /// v26 observation builder.
    pub fn due_agent_ids(&self, next_tick: u64) -> impl Iterator<Item=i64> + '_ {
        self.bound_at_tick.iter().filter_map(move |(&id, &at)|
            self.due_from_binding(at, next_tick).then_some(id),
        )
    }

    pub fn due_count(&self, next_tick: u64) -> usize {
        self.due_agent_ids(next_tick).count()
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
    /// Reused sparse per-ship mining credit transaction. Events may contain
    /// multiple extractions for the same ship during one combat phase.
    mining_credit_totals: HashMap<i64, i32>,
    mining_credit_order: Vec<i64>,
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
            mining_credit_totals: HashMap::new(),
            mining_credit_order: Vec::new(),
        }
    }
}

impl<M: NativeLevelRewardMetadata, A> NativeLevelState<M, A> {
    /// Reconcile one combat-event phase to authoritative FleetShip state.
    /// Multiple legitimate mining actions can credit the same ship before
    /// the Level callback, so compare against its final physical cargo
    /// once per ship—not against each intermediate event. Preserve first
    /// event order and reuse both buffers across quiet and busy ticks.
    fn credit_mining_since(&mut self, start: usize)
        -> Result<(), NativeLevelStepError>
    {
        self.mining_credit_totals.clear();
        self.mining_credit_order.clear();
        for event in &self.combat.events()[start..] {
            let CombatEvent::CapabilityMined {
                ship_id, amount_mined, ..
            } = event else { continue; };
            if *amount_mined <= 0 {
                return Err(NativeLevelStepError::InvalidMiningFleetValue(*ship_id));
            }
            if let Some(total) = self.mining_credit_totals.get_mut(ship_id) {
                *total = total.checked_add(*amount_mined).ok_or(
                    NativeLevelStepError::InvalidMiningFleetValue(*ship_id)
                )?;
            } else {
                self.mining_credit_order.push(*ship_id);
                self.mining_credit_totals.insert(*ship_id, *amount_mined);
            }
        }
        for &ship_id in &self.mining_credit_order {
            let amount = self.mining_credit_totals[&ship_id];
            let expected = self.combat.ship(ship_id)
                .ok_or(NativeLevelStepError::InvalidMiningFleetValue(ship_id))?
                .vitals.mined_cargo();
            let actual = self.metadata.credit_mined_minerals(ship_id, amount)
                .ok_or(NativeLevelStepError::MissingMiningFleetValue(ship_id))?;
            if expected != actual {
                return Err(NativeLevelStepError::InvalidMiningFleetValue(ship_id));
            }
            if self.combat.ship(ship_id)
                .is_some_and(|ship| !ship.vitals.is_dead()) {
                self.events.record_mined_value(&self.combat, ship_id, actual)?;
            }
        }
        Ok(())
    }
}

#[derive(Debug)]
pub enum NativeLevelStepError {
    Combat(CombatWorldError),
    Events(NativeLevelEventError),
    EpisodeComplete,
    TickExhausted,
    InvalidDecisionCadence,
    MissingMiningFleetValue(i64),
    InvalidMiningFleetValue(i64),
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
        let prior_tick = world.tick();
        let completed_tick = prior_tick.checked_add(1)
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
        state.decisions.synchronize(&state.combat, prior_tick);
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
        state.credit_mining_since(0)?;
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
            let previous_event_count = state.combat.events().len();
            state.events.advance_timed_commands(
                completed_tick, &mut state.combat,
                state.metadata.navigation(),
                &state.command_friendlies, dt_seconds,
            )?;
            if state.combat.events().len() > previous_event_count {
                // Timed Mining callbacks occur after the normal reward
                // pass. Persist before publishing any command completion.
                state.credit_mining_since(previous_event_count)?;
            }
            state.events.publish_timed_command_events(
                completed_tick,
                &state.combat.events()[previous_event_count..],
            )?;
        }
        Ok(counts)
    }
}
