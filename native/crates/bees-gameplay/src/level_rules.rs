//! One fixed-step implementation for both native graphical and headless
//! Levels: policy decisions -> canonical CombatWorld -> squad/HiveMind
//! lifecycle -> episode reward/terminal processing. No other combat rules
//! or training-only physics are reimplemented here.
//!
//! Simulation<NativeLevelState<...>, NativeLevelRules> owns the world's
//! episode revision, seeded RNG, aggregate measurements and failure state.
//! The caller must reset completed episodes before asking for a new step.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::num::NonZeroU32;

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatEvent, CombatWorld, CombatWorldError, NativeAutomaticWeaponContext, NativeAutomaticWeaponInputs};
use bees_sim::combat_perception_bridge::CombatPerceptionError;
use bees_sim::vision::{HiveMindObserver, VisionFirstSighting, VisionError};
use bees_sim::rl_discovery::DiscoveryKind as RewardDiscoveryKind;
use bees_sim::geometry::Vec2;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::navigation::NavigationError;
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
use crate::squads::{
    NativeSquadEntry, NativeSquadRoster, SquadRosterChange, SquadRosterError,
};
use crate::squad_geometry::{
    live_squad_bounds_from_catalog, live_squad_geometry_from_catalog,
    SquadMemberOffset,
};
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

    /// Install source-owned fleet/perception/vision data for real physical
    /// Beacon spawns before this tick's vision and reward consumers run.
    /// A Level without a spawn adapter must not acknowledge missing data.
    fn register_scout_beacons(
        &mut self, _world: &CombatWorld, events: &[CombatEvent],
    ) -> Result<(), CombatPerceptionError> {
        if events.iter().any(|event| matches!(event, CombatEvent::ScoutBeaconDropped { .. })) {
            return Err(CombatPerceptionError::InvalidAuthoredMetadata);
        }
        Ok(())
    }

    /// Collect real registered/enabled HiveMindVision observers. A full
    /// rendered Level may instead forward source OnTriggerEnter callbacks;
    /// only Levels with an authoritative observer adapter enable the
    /// native collider sweep. Reuse caller-owned output across steps.
    fn collect_hivemind_observers(
        &self, _world: &CombatWorld, output: &mut Vec<HiveMindObserver>,
    ) -> Result<(), CombatPerceptionError> {
        output.clear();
        Ok(())
    }

    /// Use the real *source* discovery denominator/category for a verified
    /// first-sighting identity. Physics never guesses health-based reward
    /// TSV from geometry or from the current model's visible slots.
    fn verified_discovery_reward(
        &self, _world: &CombatWorld, _sighting: VisionFirstSighting,
    ) -> Result<(RewardDiscoveryKind, i32), CombatPerceptionError> {
        Err(CombatPerceptionError::InvalidAuthoredMetadata)
    }

    /// After every successful canonical physics/reward tick, advance any
    /// world-owned observation memory (e.g. actual allied sight history).
    /// No inference-only clock may drift independently of world progress.
    fn update_observation_memory(
        &mut self, _world: &CombatWorld, _episode_progress: f32,
    ) -> Result<(), CombatPerceptionError> {
        Ok(())
    }
}

/// One authentic Unity OnTriggerEnter2D ship contact with the currently
/// selected enemy. Ordered contact callbacks, not a radius approximation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeBomberContact {
    pub bomber_id: i64,
    pub contacted_target_id: i64,
}

/// Instantaneous active source squad stances. These are sampled only
/// when the host reports status, never in the hot physics loop. Older
/// snapshots can have CeaseFire unknown at squad level even if per-ship
/// IsCeaseFire was imported; the known count makes that explicit.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NativePlayerControlGauges {
    pub active_player_squads: usize,
    pub matched_speed_squads: usize,
    pub chase_enabled_squads: usize,
    pub locked_on_squads: usize,
    pub cease_fire_known_squads: usize,
    pub cease_fire_enabled_squads: usize,
}

impl NativePlayerControlGauges {
    pub fn merge(&mut self, other: Self) {
        self.active_player_squads += other.active_player_squads;
        self.matched_speed_squads += other.matched_speed_squads;
        self.chase_enabled_squads += other.chase_enabled_squads;
        self.locked_on_squads += other.locked_on_squads;
        self.cease_fire_known_squads += other.cease_fire_known_squads;
        self.cease_fire_enabled_squads += other.cease_fire_enabled_squads;
    }
}

/// Deferred authored squads not yet admitted to the canonical CombatWorld.
/// Derived at status-sampling time from the source wave queue, with no
/// gameplay-tick counters, extra roster, or second reinforcement scheduler.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NativeReinforcementGauges {
    pub pending_waves: usize,
    pub pending_squads: usize,
    pub pending_ships: usize,
}

impl NativeReinforcementGauges {
    pub fn merge(&mut self, other: Self) {
        self.pending_waves += other.pending_waves;
        self.pending_squads += other.pending_squads;
        self.pending_ships += other.pending_ships;
    }
}

/// Current episode's actual source Squad.CheckChase work. These counters
/// are incremented only at the one-second source timer boundary, not on
/// every fixed physics or inference step.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NativePlayerChaseMeasurements {
    pub timer_callbacks: u64,
    pub eligible_squads: u64,
    pub visible_targets: u64,
    pub sight_pairs_checked: u64,
    pub commands_started: u64,
}

impl NativePlayerChaseMeasurements {
    pub fn merge(&mut self, other: Self) {
        self.timer_callbacks += other.timer_callbacks;
        self.eligible_squads += other.eligible_squads;
        self.visible_targets += other.visible_targets;
        self.sight_pairs_checked += other.sight_pairs_checked;
        self.commands_started += other.commands_started;
    }
}

/// Source-facing gameplay inputs for timed Hive Mind commands.
    /// Must be refreshed after the combat/roster step from live Level
    /// geometry, preserving Level.State.Squads order. Command activation
    /// and timer callbacks use this same authoritative view.
pub trait NativeLevelCommandContext {
    /// Unity Command.SetFinalize releases user squads' IsLockedOn state.
    /// Level command completion is the one authoritative event source.
    fn player_command_finished(&mut self, _owner: SquadLifetime) {}

    /// Retire per-squad source controls at the authoritative Squad.Kill
    /// event, not by polling the roster each fixed step. Other metadata
    /// (damage/fleet history) survives as required for reward settlement.
    fn player_squad_retired(&mut self, _owner: SquadLifetime) {}

    /// Scout.CreateMinionSquad inherits its parent's chosen shooting and
    /// stance state but explicitly disables direct input after AddShip.
    /// The physical Beacon and exact minion lifetime are already created
    /// by CombatWorld + NativeSquadRoster; only source metadata is added.
    fn player_scout_minion_created(
        &mut self, _world: &mut CombatWorld,
        _mother: SquadLifetime, _child: SquadLifetime, _beacon_id: i64,
    ) -> Result<(), GameCommandError> { Ok(()) }

    /// The original Squad.CheckChase runs a 1-second source timer.
    /// Only a Level with actual source sight, movement and weapon inputs
    /// may trigger it. The game and headless learner use this same phase
    /// and the canonical command director; other adapters do no work.
    fn advance_player_chase(
        &mut self, _world: &mut CombatWorld,
        _events: &mut NativeLevelEventProcessor,
        _random: &mut bees_sim::simulation::RandomStream,
        _completed_tick: u64, _dt_seconds: f32,
    ) -> Result<u64, NativeLevelEventError> { Ok(0) }

    fn player_chase_measurements(&self) -> NativePlayerChaseMeasurements {
        NativePlayerChaseMeasurements::default()
    }
    fn player_control_gauges(&self) -> NativePlayerControlGauges {
        NativePlayerControlGauges::default()
    }

    fn reinforcement_gauges(&self) -> NativeReinforcementGauges {
        NativeReinforcementGauges::default()
    }

    /// Next authored admission boundary on this Level's single fixed-step
    /// clock. The frontend may show it, but must never execute the wave.
    fn next_reinforcement_tick(&self) -> Option<u64> { None }

    /// Admit scheduled source squads at the completed fixed-tick boundary.
    fn deploy_reinforcements(
        &mut self, _world: &mut CombatWorld, _events: &mut NativeLevelEventProcessor, _tick: u64,
    ) -> Result<(), NativeLevelStepError> { Ok(()) }

    /// Current source squad strategy and native command identity. A Level with
    /// authored automatic weapons must provide these genuine values.
    fn automatic_weapon_inputs(&self, _ship_id: i64, _roster: &NativeSquadRoster,
        _commands: &crate::commands::NativeSquadCommandDirector,
    ) -> Result<Option<NativeAutomaticWeaponInputs>, CombatWorldError> { Ok(None) }

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

    /// Build one current source-order friendly snapshot from the SAME
    /// native roster, physical world and immutable sprite catalog. The
    /// Level caller clears this reused output buffer before every due
    /// timer; this default also clears it for direct external callers.
    /// A rendered Level may override when its actual scene values differ.
    fn collect_live_friendlies(
        &mut self,
        combat: &CombatWorld,
        roster: &NativeSquadRoster,
        commands: &crate::commands::NativeSquadCommandDirector,
        output: &mut Vec<NativeFriendlySquad>,
    ) -> Result<(), GameCommandError> {
        let catalog = self.ship_asset_catalog()
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        collect_live_friendlies_from_catalog(
            combat, roster, catalog, roster.source_order(),
            |owner| Ok(commands.current_kind_and_guarded_target(owner)), output,
        )
    }

    /// Exact Unity CircleSquad.Timer values from the current Level and
    /// Ship.IsShipWithinRange weapon-validity checks. Called only when a
    /// CircleSquad poll fires. Implementers must inspect true live weapons
    /// and squad membership; no geometric or fabricated fallback exists.
    /// The default fails closed for Levels without this command adapter.
    fn circle_command_snapshot(
        &self, _world: &mut CombatWorld, _roster: &NativeSquadRoster, _owner: SquadLifetime,
        _enemy_owner: SquadLifetime,
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
        &self, _world: &mut CombatWorld, _roster: &NativeSquadRoster, _owner: SquadLifetime,
        _enemy_owner: SquadLifetime, _need_target_candidates: bool,
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
        &self, _world: &mut CombatWorld, _roster: &NativeSquadRoster, _owner: SquadLifetime,
        _enemy_owner: SquadLifetime, _need_all_target_range: bool,
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
        &self, _world: &CombatWorld, _roster: &NativeSquadRoster, _owner: SquadLifetime,
        _enemy_owner: SquadLifetime, _need_retarget: bool,
    ) -> Result<NativeBombingTimerSnapshot, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    /// Unity Charge.HasTargetsWithinChargingRange requires the selected
    /// Bomb target, actual rotation, unobstructed sight, and Charge
    /// weapon ShipsWithinRange membership. The Level supplies this exact
    /// predicate and optionally the live lost-target data.
    fn charge_command_snapshot(
        &self, _world: &CombatWorld, _roster: &NativeSquadRoster, _owner: SquadLifetime,
        _enemy_owner: SquadLifetime, _need_retarget: bool,
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

    /// Unity Ship.Kill removes a member and immediately calls
    /// Squad.SetOffsets for the surviving squad, before its next movement
    /// command. Update the Level's single stored Ship.OffsetFromCenter
    /// values and refresh composition-dependent command bans. The source
    /// offsets have already been calculated from the one physical world
    /// and its authored sprite catalog; there is no second game roster.
    /// Without this Level-owned update, a later command would silently
    /// apply formation offsets from before the casualty.
    fn update_survivor_formation(
        &mut self, _owner: SquadLifetime,
        _offsets: &[SquadMemberOffset],
    ) -> Result<(), GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
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
    fn navigation_mut(&mut self) -> &mut NavigationRuntime;

    /// Exact Level.ActivateCollisionAsteroids, independent of asteroid count.
    fn collision_asteroids_enabled(&self) -> bool;

    /// Shared fixed-step navigation pump. Refresh actual geometry only when
    /// work is queued, then consume lifecycle-checked results before movement.
    /// Rebuilding static clearance here also covers destroyed/edited obstacles
    /// without maintaining another mutable entity registry or a stale cache.
    fn advance_navigation(&mut self, combat: &mut CombatWorld)
        -> Result<u64, CombatWorldError>
    {
        if !combat.has_waiting_path_jobs() { return Ok(0); }
        let asteroids = self.collision_asteroids_enabled();
        let navigation = self.navigation_mut();
        combat.refresh_world_navigation(navigation, true, asteroids)?;
        let mut jobs = 0;
        while combat.solve_next_world_navigation_job(navigation)?.is_some() {
            jobs += 1;
        }
        Ok(jobs)
    }

    /// Once the single CombatWorld exists, initialize this Level's real
    /// static navigation clearance from its actual active colliders.
    /// Rich game adapters may have their own fully authored navigation
    /// lifecycle; policy-only scenes use the same canonical rasterizer.
    fn initialize_navigation_from_world(
        &mut self, _combat: &CombatWorld,
    ) -> Result<(), NavigationError> {
        Ok(())
    }
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

#[derive(Debug)]
pub enum NativeLevelVisionError {
    Source(CombatPerceptionError),
    Geometry(VisionError),
    Reward(NativeLevelEventError),
}

impl From<CombatPerceptionError> for NativeLevelVisionError {
    fn from(error: CombatPerceptionError) -> Self { Self::Source(error) }
}
impl From<VisionError> for NativeLevelVisionError {
    fn from(error: VisionError) -> Self { Self::Geometry(error) }
}
impl From<NativeLevelEventError> for NativeLevelVisionError {
    fn from(error: NativeLevelEventError) -> Self { Self::Reward(error) }
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
    pub campaign_dialogue: Option<crate::campaign_dialogue::CampaignDialogue>,
    pub(crate) held_fire_ships: Vec<i64>,
    /// Reused source Level.State.Squads snapshot; no per-frame allocation.
    command_friendlies: Vec<NativeFriendlySquad>,
    command_bomber_contacts: Vec<NativeBomberContact>,
    /// One source-validated observer roster and its exact new sightings
    /// per tick. Neither is a second learned cache or a model-side view.
    hivemind_observers: Vec<HiveMindObserver>,
    first_sightings: Vec<VisionFirstSighting>,
    /// Rare source-order survivor-formation updates after ship deaths.
    /// Both buffers are reused; quiet frames do no formation work.
    formation_changed_squads: Vec<SquadLifetime>,
    /// Scratch membership only; source-order changes stay in the Vec.
    /// Retain capacity when mass casualties occur across multiple ticks.
    formation_changed_seen: HashSet<SquadLifetime>,
    survivor_offsets: Vec<SquadMemberOffset>,
    command_input_measurements: NativeCommandInputMeasurements,
    navigation_jobs_completed: u64,
    /// Reused sparse per-ship mining credit transaction. Events may contain
    /// multiple extractions for the same ship during one combat phase.
    mining_credit_totals: HashMap<i64, i32>,
    mining_credit_order: Vec<i64>,
}

impl<M, A> NativeLevelState<M, A> {
    /// Cumulative path jobs delivered in this episode, including genuine
    /// path-not-found completions. No histories or per-ship timers are stored.
    pub fn navigation_jobs_completed(&self) -> u64 {
        self.navigation_jobs_completed
    }

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
            campaign_dialogue: None,
            held_fire_ships: Vec::new(),
            command_friendlies: Vec::new(),
            command_bomber_contacts: Vec::new(),
            hivemind_observers: Vec::new(),
            first_sightings: Vec::new(),
            formation_changed_squads: Vec::new(),
            formation_changed_seen: HashSet::new(),
            survivor_offsets: Vec::new(),
            command_input_measurements: NativeCommandInputMeasurements::default(),
            navigation_jobs_completed: 0,
            mining_credit_totals: HashMap::new(),
            mining_credit_order: Vec::new(),
        }
    }

    pub fn configure_campaign_dialogue(
        &mut self, spec: crate::campaign_dialogue::CampaignDialogueSpec,
    ) -> Result<(), NativeLevelEventError> {
        let dialogue = crate::campaign_dialogue::CampaignDialogue::new(spec);
        self.events.queue_campaign_dialogue(0, dialogue.current().1)?;
        self.campaign_dialogue = Some(dialogue);
        Ok(())
    }
}

impl<M: NativeLevelRewardMetadata, A> NativeLevelState<M, A> {
    /// The sole physical sighting -> faction discovery -> reward path for
    /// worlds whose Level metadata provides real HiveMind observers.
    /// At startup tick=0; during a fixed step this is its completed tick
    /// BEFORE episode termination. Both cases preserve source ordering,
    /// and duplicate observers never award second discovery reward.
    pub fn refresh_vision_and_rewards(
        &mut self, tick: u64,
    ) -> Result<bees_sim::vision::VisionSweepCounts, NativeLevelVisionError> {
        self.hivemind_observers.clear();
        self.first_sightings.clear();
        self.metadata.collect_hivemind_observers(
            &self.combat, &mut self.hivemind_observers,
        )?;
        let vision = self.combat.refresh_hivemind_vision_with_sightings(
            &self.hivemind_observers, false, &mut self.first_sightings,
        )?;
        for &sighting in &self.first_sightings {
            let (kind, value) = self.metadata.verified_discovery_reward(
                &self.combat, sighting,
            )?;
            let roster = self.events.roster();
            let reward_squad = roster.owning_squad(sighting.observer_id).and_then(|owner| {
                // Source Beacon.MotherSquad survives its parent Scout's death
                // when the original squad still has other living members.
                if self.combat.ship(sighting.observer_id).is_some_and(|ship|
                    ship.ship_type == bees_sim::ship_assets::BEACON_TYPE) {
                    roster.get(owner).and_then(|squad| squad.mother_squad).or(Some(owner))
                } else { Some(owner) }
            }).filter(|owner| roster.get(*owner).is_some_and(|squad|
                // Vision runs before this tick's casualty/roster pass.
                // A still-registered MotherSquad may already be physically dead.
                squad.members.iter().any(|&id| self.combat.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) &&
                    self.combat.ship_body(id).is_some_and(|body| body.active))));
            let target_ship_tsv = if sighting.kind == bees_sim::level_discovery::DiscoveryKind::Ship {
                Some(self.combat.ship(sighting.target_id)
                    .ok_or(CombatPerceptionError::MissingShip(sighting.target_id))?.vitals.tsv())
            } else { None };
            self.events.record_verified_vision_sighting(
                tick, sighting, kind, value, reward_squad, target_ship_tsv,
            )?;
        }
        Ok(vision)
    }


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
    Reinforcements(String),
    Combat(CombatWorldError),
    Events(NativeLevelEventError),
    EpisodeComplete,
    TickExhausted,
    InvalidDecisionCadence,
    MissingMiningFleetValue(i64),
    InvalidMiningFleetValue(i64),
    Perception(CombatPerceptionError),
    Vision(NativeLevelVisionError),
}

impl From<CombatPerceptionError> for NativeLevelStepError {
    fn from(error: CombatPerceptionError) -> Self {
        Self::Perception(error)
    }
}
impl From<GameCommandError> for NativeLevelStepError {
    fn from(error: GameCommandError) -> Self {
        Self::Events(NativeLevelEventError::Commands(error))
    }
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

/// Borrow the canonical Level owners for physical automatic weapons.
struct LevelAutomaticWeapons<'a, M> {
    metadata: &'a M,
    roster: &'a NativeSquadRoster,
    commands: &'a crate::commands::NativeSquadCommandDirector,
}

impl<M: NativeLevelCommandContext> NativeAutomaticWeaponContext for LevelAutomaticWeapons<'_, M> {
    fn inputs(&self, ship_id: i64) -> Result<NativeAutomaticWeaponInputs, CombatWorldError> {
        self.metadata.automatic_weapon_inputs(ship_id, self.roster, self.commands)?
            .ok_or(CombatWorldError::MissingAutomaticTargetingInputs)
    }
    fn prefers(&self, ship_id: i64, target_id: i64) -> bool {
        self.roster.owning_squad(ship_id).and_then(|owner| self.commands.preferred_weapon_enemy(owner))
            .is_some_and(|enemy| self.roster.owning_squad(target_id) == Some(enemy))
    }
}

/// Stateless collision settings; mutable state remains isolated per world.
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
        state.navigation_jobs_completed +=
            state.metadata.advance_navigation(&mut state.combat)?;
        // No independent policy/physics rules in gameplay or training:
        // both adapters invoke CombatWorld's one canonical transaction.
        let events = &state.events;
        let mut counts = state.combat.step_combat_with_weapon_targeting(
            decisions, dt_seconds, &self.collision_matrix,
            &mut world.random, &|ship_id| events.roster().owning_squad(ship_id)
                .and_then(|owner| events.commands().current_server_outcome(owner)),
            &LevelAutomaticWeapons { metadata: &state.metadata, roster: events.roster(), commands: events.commands() },
        )?;
        // CombatWorld returns its counts before Level-owned vision,
        // reinforcement orders and timed command callbacks run. Those
        // phases also use the same shared spatial grid; retain a baseline
        // so their actual rebuilds enter this tick's telemetry exactly once.
        let grid_at_combat_end = state.combat.physics().counts();
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
        state.combat.step_extraction(&self.collision_matrix)?;
        state.combat.consume_weapon_rank_events()?;
        state.metadata.register_scout_beacons(&state.combat, state.combat.events())?;
        counts.vision = state.refresh_vision_and_rewards(completed_tick)
            .map_err(NativeLevelStepError::Vision)?;
        state.credit_mining_since(0)?;
        let metadata = &state.metadata;
        let allocate = &mut state.allocate_minion;
        state.events.process_completed_tick(
            &mut state.combat, completed_tick, dt_seconds,
            |mother, beacon| allocate(mother, beacon),
            |id| metadata.damage_metadata(id),
            |id| metadata.current_mined_tsv(id),
        )?;
        if let Some(dialogue) = &mut state.campaign_dialogue {
            if let Some(cue) = dialogue.observe_combat(&state.combat) {
                dialogue.set_cue(cue);
                state.events.queue_campaign_dialogue(completed_tick, cue)?;
            }
            if let Some(finalized) = state.events.latest_finalized_episode() {
                let cue = dialogue.outcome(finalized.result.winner_side);
                dialogue.set_cue(cue);
                state.events.queue_campaign_dialogue(completed_tick, cue)?;
            } else if let Some(survival) = state.events.survival_status() {
                while let Some(cue) = dialogue.take_upload_cue(survival) {
                    dialogue.set_cue(cue);
                    state.events.queue_campaign_dialogue(completed_tick, cue)?;
                }
            }
        }
        // Deferred reinforcements are added after the core combat step.
        // Their arrival is a Level notification, not a CombatEvent, so the
        // ordinary event-count refresh below would otherwise leave the
        // reported active-ship total at its pre-arrival value on quiet ticks.
        // An O(1) storage-length comparison avoids rescanning large battles
        // unless a real deployment changed the physical ship population.
        let ships_before_deployment = state.combat.physics().ships().len();
        if !state.events.completed() {
            state.metadata.deploy_reinforcements(&mut state.combat, &mut state.events, completed_tick)?;
        }
        let ships_deployed = state.combat.physics().ships().len() != ships_before_deployment;
        // Unity Ship.Kill removes the casualty, then calls Squad.SetOffsets
        // on its survivors in the SAME kill transaction. The roster's
        // MemberLost changes are the authoritative signal. Other commands
        // retain their running strategies, but must use fresh live offsets
        // on subsequent movement polls. When all members died this tick,
        // the roster retired their squad instead; no rebasing is needed.
        state.formation_changed_squads.clear();
        state.formation_changed_seen.clear();
        for &change in state.events.latest_squad_changes() {
            match change {
                SquadRosterChange::MemberLost { squad, .. } => {
                    if state.events.roster().get(squad).is_some() &&
                        state.formation_changed_seen.insert(squad)
                    {
                        state.formation_changed_squads.push(squad);
                    }
                }
                SquadRosterChange::SquadRetired { squad } =>
                    state.metadata.player_squad_retired(squad),
                SquadRosterChange::MinionCreated {
                    squad, mother_squad, beacon_id, ..
                } => state.metadata.player_scout_minion_created(
                    &mut state.combat, mother_squad, squad, beacon_id,
                )?,
                _ => {}
            }
        }
        for &owner in &state.formation_changed_squads {
            let squad = state.events.roster().get(owner)
                .ok_or(GameCommandError::InvalidSquad)?;
            let catalog = state.metadata.ship_asset_catalog()
                .ok_or(GameCommandError::MissingCombatSnapshot)?;
            live_squad_geometry_from_catalog(
                &state.combat, squad, catalog, &mut state.survivor_offsets,
            ).map_err(GameCommandError::SquadGeometry)?;
            if squad.command_bans.is_some() {
                let bans = crate::squad_composition::composition_command_bans(
                    &state.combat, squad, |id| state.combat.weapon_target_rank_stats(id).ok().map(|rank| rank.firepower),
                ).map_err(|_| GameCommandError::MissingCombatSnapshot)?;
                crate::squad_composition::refresh_composition_bans(
                    state.events.roster_mut().command_bans_mut(owner).expect("captured bans"), bans);
            }
            // Store the same authored formation and composition bans that
            // Unity's actual Level uses for later strategy activations.
            state.metadata.update_survivor_formation(
                owner, &state.survivor_offsets,
            )?;
            // Also synchronize in-flight command copies without resetting
            // their timers, strategy reservations or selection identities.
            state.events.commands_mut().refresh_survivor_formation(
                owner, &state.survivor_offsets,
            );
        }
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
                    state.events.commands(),
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
                |owner, enemy_id, combat, roster| {
                    metadata.circle_command_snapshot(combat, roster, owner, enemy_id)
                },
                |owner, enemy_id, combat, roster, need_candidates| {
                    metadata.swipe_command_snapshot(
                        combat, roster, owner, enemy_id, need_candidates,
                    )
                },
                |owner, enemy_id, combat, roster, need_all| {
                    metadata.aggressive_command_snapshot(
                        combat, roster, owner, enemy_id, need_all,
                    )
                },
                |owner, enemy_id, combat, roster, need_retarget| {
                    metadata.bombing_command_snapshot(
                        combat, roster, owner, enemy_id, need_retarget,
                    )
                },
                |owner, enemy_id, combat, roster, need_retarget| {
                    metadata.charge_command_snapshot(
                        combat, roster, owner, enemy_id, need_retarget,
                    )
                },
                |owner, combat, squad| {
                    metadata.scouting_command_center(combat, owner, squad)
                },
            )?;
            if state.combat.events().len() > previous_event_count {
                state.metadata.register_scout_beacons(
                    &state.combat, &state.combat.events()[previous_event_count..],
                )?;
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
        // Use the Level's finished-command list, not a second per-frame
        // scan of all player squads, to release source Lock On state.
        for finished in state.events.latest_finished_commands() {
            state.metadata.player_command_finished(finished.owner);
        }
        // Source Squad.CheckChase fires independently of the current
        // command's timer. Install only authenticated player commands,
        // after the prior event/timer finalizations and before the next
        // physics tick consumes their movement instructions.
        if !state.events.completed() {
            state.metadata.advance_player_chase(
                &mut state.combat, &mut state.events, &mut world.random,
                completed_tick, dt_seconds,
            )?;
        }
        // The observation history is part of the same World lifetime:
        // sample after physical movement and combat events, never via
        // an unrelated UI or inference tick. Fixed-step failures are
        // reported to the caller and the world is not reused as valid PPO.
        state.combat.consume_weapon_rank_events()?;
        state.metadata.update_observation_memory(
            &state.combat, state.events.normalized_episode_progress(),
        )?;
        // Count final physical outcomes once after scripted contact/timer
        // callbacks, using the same combat-world definition as the standalone
        // game/physics driver. No separate Level hit ledger is kept here.
        CombatWorld::count_step_outcomes(state.combat.events(), &mut counts);
        // Contact/timer callbacks can detonate a ship or spawn a Beacon after
        // the core physical transaction; reinforcement deployment can add
        // hulls without producing any CombatEvent. Report the final same-tick
        // state in both cases. Quiet ticks reuse the core counts.
        if ships_deployed || counts.events != state.combat.events().len() as u64 {
            counts.events = state.combat.events().len() as u64;
            counts.active_ships = state.combat.total_living_ships() as u32;
            counts.active_projectiles = state.combat.total_active_projectiles() as u32;
        }
        // No second physics.step runs during the Level's late phases.
        // Include only the additional rebuilds since CombatWorld's
        // snapshot: the core collision/weapon rebuilds are already counted.
        let grid_at_level_end = state.combat.physics().counts();
        counts.ship_grid_rebuilds +=
            grid_at_level_end.ship_grid_rebuilds - grid_at_combat_end.ship_grid_rebuilds;
        counts.obstacle_grid_rebuilds +=
            grid_at_level_end.obstacle_grid_rebuilds - grid_at_combat_end.obstacle_grid_rebuilds;
        counts.ship_grid_cells_indexed +=
            grid_at_level_end.ship_grid_cells_indexed - grid_at_combat_end.ship_grid_cells_indexed;
        counts.obstacle_grid_cells_indexed +=
            grid_at_level_end.obstacle_grid_cells_indexed - grid_at_combat_end.obstacle_grid_cells_indexed;
        Ok(counts)
    }
}
