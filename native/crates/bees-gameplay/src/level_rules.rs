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
use bees_hivemind::dispatch::LocalCommandKind;
use bees_sim::combat_world::{CombatEvent, CombatWorld, CombatWorldError};
use bees_sim::geometry::Vec2;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::physics::CollisionMatrix;
use bees_sim::policy::Decision;
use bees_sim::rl_shaping::DamageRewardMetadata;
use bees_sim::simulation::{GameRules, World};
use bees_sim::telemetry::StepCounts;

use crate::commands::{
    GameCommandError, NativeAggressiveCommandSnapshot,
    NativeCircleCommandSnapshot, NativeBombingTimerSnapshot,
    NativeChargeTimerSnapshot,
    NativeFriendlySquad, NativeSwipeCommandSnapshot,
};
use crate::level_events::{
    NativeLevelEventError, NativeLevelEventProcessor,
};
use crate::squads::{NativeSquadEntry, NativeSquadRoster, SquadRosterError};
use crate::squad_geometry::live_squad_bounds_from_catalog;
use crate::level_inputs::collect_live_friendlies_from_catalog;

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

/// One authentic Unity OnTriggerEnter2D ship contact with the currently
/// selected enemy. Ordered contact callbacks, not a radius approximation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeBomberContact {
    pub bomber_id: i64,
    pub contacted_target_id: i64,
}

/// Source-facing gameplay inputs for timed Hive Mind commands.
    /// Must be refreshed after the combat/roster step from live Level
    /// geometry, preserving Level.State.Squads order. Command activation
    /// and timer callbacks use this same authoritative view.
pub trait NativeLevelCommandContext {
    /// Deliver source-order, real OnTriggerEnter2D contacts for active
    /// Striker and Yellow Jacket BombingRuns. Called only when a live,
    /// ready contact bomber has an active target, after the physics fixed
    /// step and before combat-event reconciliation. Do not scan every ship
    /// if no new contacts occurred. Without the authentic trigger adapter
    /// contact-bomber commands fail instead of silently never delivering.
    fn collect_bomber_contacts(
        &mut self, _combat: &CombatWorld, _roster: &NativeSquadRoster,
        _output: &mut Vec<NativeBomberContact>,
    ) -> Result<(), GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Source Level.State.Squads order, including authentic player and
    /// scripted Squad lifetimes. Command selection must not be based on
    /// BTreeMap lifecycle ordering (which may differ from source order).
    /// A Level not providing this authoritative order fails closed.
    fn current_squad_order(&self) -> Result<&[SquadLifetime], GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// The real current command of a live squad, including user/scripted
    /// Guard and ClosestFriendly commands and their protected squad.
    fn current_squad_command(
        &self, _owner: SquadLifetime,
    ) -> Result<(Option<LocalCommandKind>, Option<SquadLifetime>), GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Build one current source-order friendly snapshot from the SAME
    /// native roster, physical world and immutable sprite catalog. The
    /// Level caller clears this reused output buffer before every due
    /// timer; this default also clears it for direct external callers.
    /// A rendered Level may override when its actual scene values differ.
    fn collect_live_friendlies(
        &mut self,
        combat: &CombatWorld,
        roster: &NativeSquadRoster,
        output: &mut Vec<NativeFriendlySquad>,
    ) -> Result<(), GameCommandError> {
        let catalog = self.ship_asset_catalog()
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        let order = self.current_squad_order()?;
        collect_live_friendlies_from_catalog(
            combat, roster, catalog, order,
            |owner| self.current_squad_command(owner), output,
        )
    }

    /// Exact Unity CircleSquad.Timer values from the current Level and
    /// Ship.IsShipWithinRange weapon-validity checks. Called only when a
    /// CircleSquad poll fires. Implementers must inspect true live weapons
    /// and squad membership; no geometric or fabricated fallback exists.
    /// The default fails closed for Levels without this command adapter.
    fn circle_command_snapshot(
        &self, _world: &CombatWorld, _owner: SquadLifetime,
        _enemy_squad_id: i64,
    ) -> Result<NativeCircleCommandSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Unity SwipeSquad.Timer and InAndOutTimer both require the original
    /// enemy lifetime, live squad bounds and weapon validity, and the same
    /// GetShips-ordered target metadata during approach. Called only at
    /// due Right/Left Swipe or InAndOut command callbacks. When
    /// need_target_candidates is false, the Level need not allocate or
    /// gather the candidate list; current enemy lifetime/range remains
    /// authoritative regardless.
    fn swipe_command_snapshot(
        &self, _world: &CombatWorld, _owner: SquadLifetime,
        _enemy_squad_id: i64, _need_target_candidates: bool,
    ) -> Result<NativeSwipeCommandSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Unity Aggressive.Timer checks a stronger all-targets-in-range
    /// predicate and enemy defenselessness after three in-range polls.
    /// The same adapter supplies fresh GetShips-ordered targeting metadata
    /// while the one-ship-per-frame coroutine is in flight, even when the
    /// 3s/0.25s command timer itself is not due. Missing authored weapon
    /// and ship composition data fails closed.
    fn aggressive_command_snapshot(
        &self, _world: &CombatWorld, _owner: SquadLifetime,
        _enemy_squad_id: i64, _need_all_target_range: bool,
    ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// The owning Level's original ProximityCollider.NearbyEnemyShips
    /// membership for current BombingRun target pairs. Called ONLY on
    /// a BombingRun timer callback. When need_retarget is true, also
    /// supply real current GetShips-ordered enemy metadata and live Bomb
    /// weapons for replacement target selection. There is no fabricated
    /// collider-center proximity or stale activation-time weapon power.
    fn bombing_command_snapshot(
        &self, _world: &CombatWorld, _owner: SquadLifetime,
        _enemy_squad_id: i64, _need_retarget: bool,
    ) -> Result<NativeBombingTimerSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Unity Charge.HasTargetsWithinChargingRange requires the selected
    /// Bomb target, actual rotation, unobstructed sight, and Charge
    /// weapon ShipsWithinRange membership. The Level supplies this exact
    /// predicate and optionally the live lost-target data.
    fn charge_command_snapshot(
        &self, _world: &CombatWorld, _owner: SquadLifetime,
        _enemy_squad_id: i64, _need_retarget: bool,
    ) -> Result<NativeChargeTimerSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Share the same immutable ship catalog used to spawn native
    /// collision bodies; it also contains Unity's complete sprite-size
    /// table. The Level never needs a duplicate ship-ID -> size registry.
    /// A Level without authentic assets must fail closed for Scouting.
    fn ship_asset_catalog(&self) -> Option<&ShipAssetCatalog> {
        None
    }

    /// Unity Squad.GetPosition on demand for Scouting's movement timer.
    /// The native roster supplies current member IDs, CombatWorld supplies
    /// positions and the real Unity ship catalog supplies sprite extents.
    fn scouting_command_center(
        &self, world: &CombatWorld, owner: SquadLifetime,
        squad: &NativeSquadEntry,
    ) -> Result<Vec2, GameCommandError> {
        if squad.owner != owner {
            return Err(GameCommandError::InvalidSquad);
        }
        let catalog = self.ship_asset_catalog()
            .ok_or(GameCommandError::MissingScoutingContext)?;
        live_squad_bounds_from_catalog(world, squad, catalog)
            .map(|geometry| geometry.center)
            .map_err(GameCommandError::SquadGeometry)
    }

    fn navigation(&self) -> &NavigationRuntime;
}

/// Unity Agent.FixedUpdate requests a decision after each controller's
/// own DecisionPeriod successful fixed steps, not every simulation tick.
/// One clock is enough: the owning World tick plus each ship's bind tick.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDecisionCadenceMeasurements {
    /// Fixed-step synchronization requests, including unchanged rosters.
    pub sync_calls: u64,
    /// Requests that needed to reconcile controller membership.
    pub roster_reconciliations: u64,
}

#[derive(Clone, Copy)]
struct PolicyBindingClock {
    tick: u64,
    revision: u64,
}

pub struct NativeDecisionCadence {
    period: NonZeroU32,
    bound_at_tick: BTreeMap<i64, PolicyBindingClock>,
    /// Live controller IDs and their original binding ticks grouped by
    /// decision phase, in stable ship-ID order. Rebuilt only on roster
    /// changes; observation iteration visits only the due phase.
    due_by_phase: BTreeMap<u64, Vec<(i64, u64)>>,
    latest_binding_tick: u64,
    observed_roster_revision: Option<u64>,
    measurements: NativeDecisionCadenceMeasurements,
}

impl NativeDecisionCadence {
    pub fn new(period: NonZeroU32) -> Self {
        Self {
            period,
            bound_at_tick: BTreeMap::new(),
            due_by_phase: BTreeMap::new(),
            latest_binding_tick: 0,
            observed_roster_revision: None,
            measurements: NativeDecisionCadenceMeasurements::default(),
        }
    }

    pub fn measurements(&self) -> NativeDecisionCadenceMeasurements {
        self.measurements
    }

    /// Reconcile the single authoritative policy roster at a decision
    /// boundary. New controllers start at 0; retiring a ship removes its
    /// counter. No independent timer advances on failed simulation steps.
    pub fn synchronize(
        &mut self, combat: &CombatWorld, completed_ticks: u64,
    ) {
        self.measurements.sync_calls += 1;
        let revision = combat.policy_roster_revision();
        if self.observed_roster_revision == Some(revision) {
            return;
        }
        // A stable controller roster changes no decision clock. Reconcile
        // only after authoritative bind/death transitions.
        self.bound_at_tick.retain(|&id, _|
            combat.policy_frame(id).is_some(),
        );
        for (ship_id, bind_revision) in combat.bound_policy_agent_bindings() {
            let clock = self.bound_at_tick.entry(ship_id).or_insert(
                PolicyBindingClock {
                    tick: completed_ticks,
                    revision: bind_revision,
                }
            );
            // The ship might still be alive while its policy controller
            // was replaced. A new controller begins its own decision
            // period at this completed simulation tick.
            if clock.revision != bind_revision {
                clock.tick = completed_ticks;
                clock.revision = bind_revision;
            }
        }
        // For any future tick beyond the newest binding, the strict
        // next_tick > bound_at condition is automatically satisfied.
        // Equal modulo-period phases are therefore exactly the due group.
        self.due_by_phase.clear();
        self.latest_binding_tick = 0;
        let period = u64::from(self.period.get());
        for (&ship_id, &clock) in &self.bound_at_tick {
            self.due_by_phase.entry(clock.tick % period)
                .or_default().push((ship_id, clock.tick));
            self.latest_binding_tick = self.latest_binding_tick.max(clock.tick);
        }
        self.observed_roster_revision = Some(revision);
        self.measurements.roster_reconciliations += 1;
    }

    fn due_from_binding(&self, bound_at: u64, next_tick: u64) -> bool {
        next_tick > bound_at &&
            (next_tick - bound_at) % u64::from(self.period.get()) == 0
    }

    pub fn is_due(&self, ship_id: i64, next_tick: u64) -> bool {
        self.bound_at_tick.get(&ship_id).is_some_and(|clock|
            self.due_from_binding(clock.tick, next_tick),
        )
    }

    /// Sorted, allocation-free per-agent selection for the Level's
    /// v26 observation builder. Phase vectors are populated in BTreeMap
    /// ship-ID order; even historical tick queries retain strict bind
    /// gating without traversing unrelated controllers.
    pub fn due_agent_ids(&self, next_tick: u64) -> impl Iterator<Item=i64> + '_ {
        let fully_bound = next_tick > self.latest_binding_tick;
        let phase = next_tick % u64::from(self.period.get());
        self.due_by_phase.get(&phase).into_iter()
            .flat_map(|members| members.iter())
            .filter_map(move |&(id, bound_at)| {
                (fully_bound || self.due_from_binding(bound_at, next_tick))
                    .then_some(id)
            })
    }

    pub fn due_count(&self, next_tick: u64) -> usize {
        if next_tick > self.latest_binding_tick {
            return self.due_by_phase.get(
                &(next_tick % u64::from(self.period.get()))
            ).map_or(0, Vec::len);
        }
        self.due_agent_ids(next_tick).count()
    }
}

/// Actual Level-provided friendly command input work. Counts remain cheap
/// even when timing samples are disabled for maximum training throughput.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeCommandInputMeasurements {
    /// Successful, timer-gated live friendly squad snapshots.
    pub friendly_snapshots: u64,
    /// Number of friendly squad entries returned in those snapshots.
    pub friendly_entries_supplied: u64,
    /// Authored contact projections are requested only when an active
    /// BombingRun has a live, ready Striker or Yellow Jacket with a target.
    pub bomber_contact_snapshots: u64,
    pub bomber_contacts_supplied: u64,
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
    command_bomber_contacts: Vec<NativeBomberContact>,
    command_input_measurements: NativeCommandInputMeasurements,
    /// Reused sparse per-ship mining credit transaction. Events may contain
    /// multiple extractions for the same ship during one combat phase.
    mining_credit_totals: HashMap<i64, i32>,
    mining_credit_order: Vec<i64>,
}

impl<M, A> NativeLevelState<M, A> {
    pub fn command_input_measurements(&self) -> NativeCommandInputMeasurements {
        self.command_input_measurements
    }

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
            command_bomber_contacts: Vec::new(),
            command_input_measurements: NativeCommandInputMeasurements::default(),
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
        // Unity's YellowJacket.OnTriggerEnter2D is independent of the
        // command timer. Consume these source-ordered contacts INSIDE the
        // owning completed tick, before the sole reward/roster event pass.
        if state.events.needs_bomber_contacts(&state.combat) {
            state.command_bomber_contacts.clear();
            state.metadata.collect_bomber_contacts(
                &state.combat, state.events.roster(),
                &mut state.command_bomber_contacts,
            ).map_err(|err| NativeLevelStepError::Events(
                NativeLevelEventError::Commands(err),
            ))?;
            state.command_input_measurements.bomber_contact_snapshots += 1;
            state.command_input_measurements.bomber_contacts_supplied +=
                state.command_bomber_contacts.len() as u64;
            for &contact in &state.command_bomber_contacts {
                state.events.deliver_commanded_bomber_contact(
                    completed_tick, &mut state.combat,
                    contact.bomber_id, contact.contacted_target_id,
                    &self.collision_matrix,
                )?;
            }
        }
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
            // Only tracked-friendly commands read this geometry; collecting
            // every squad on each fixed step for Mining, Heal, Scouting,
            // FullRetreat or idle Hold would waste large-battle work.
            // The Level owns buffer lifetime: a projecting adapter
            // receives an empty Vec every time, even if it only appends.
            // clear() retains capacity across large-battle callbacks.
            state.command_friendlies.clear();
            if state.events.needs_live_command_friendlies_on_update(dt_seconds) {
                state.metadata.collect_live_friendlies(
                    &state.combat, state.events.roster(),
                    &mut state.command_friendlies,
                ).map_err(|err|
                    NativeLevelStepError::Events(
                        NativeLevelEventError::Commands(err)
                    )
                )?;
                state.command_input_measurements.friendly_snapshots += 1;
                state.command_input_measurements.friendly_entries_supplied +=
                    state.command_friendlies.len() as u64;
            }
            let previous_event_count = state.combat.events().len();
            let metadata = &state.metadata;
            state.events.advance_timed_commands_with_scouting_center(
                completed_tick, &mut state.combat,
                metadata.navigation(),
                &state.command_friendlies, &mut world.random, dt_seconds,
                |owner, enemy_id, combat| {
                    metadata.circle_command_snapshot(combat, owner, enemy_id)
                },
                |owner, enemy_id, combat, need_candidates| {
                    metadata.swipe_command_snapshot(
                        combat, owner, enemy_id, need_candidates,
                    )
                },
                |owner, enemy_id, combat, need_all| {
                    metadata.aggressive_command_snapshot(
                        combat, owner, enemy_id, need_all,
                    )
                },
                |owner, enemy_id, combat, need_retarget| {
                    metadata.bombing_command_snapshot(
                        combat, owner, enemy_id, need_retarget,
                    )
                },
                |owner, enemy_id, combat, need_retarget| {
                    metadata.charge_command_snapshot(
                        combat, owner, enemy_id, need_retarget,
                    )
                },
                |owner, combat, squad| {
                    metadata.scouting_command_center(combat, owner, squad)
                },
            )?;
            if state.combat.events().len() > previous_event_count {
                // Timed Mining callbacks occur after the normal reward
                // pass. Persist before publishing any command completion.
                state.credit_mining_since(previous_event_count)?;
            }
            let allocate_late_minion = &mut state.allocate_minion;
            state.events.publish_timed_command_events(
                completed_tick, &state.combat,
                &state.combat.events()[previous_event_count..],
                |mother, beacon| allocate_late_minion(mother, beacon),
            )?;
        }
        Ok(counts)
    }
}
