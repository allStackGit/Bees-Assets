//! Source-authored metadata for real v26 policies and ordinary game weapons.
//! The SAME native CombatWorld performs physical gameplay; this only
//! supplies absent Unity ship/episode/observation values. No fake agent,
//! placeholder observations or inferred weapon statistics are allowed.
//!
//! Commands borrow the real roster and mounted weapons. Commands whose
//! physical weapon subclass is not implemented still fail explicitly.

mod commands;
mod scripted;
mod mission_cues;
pub use scripted::{NativeScriptedOrder, NativeScriptedQueuePlan, NativeScriptedActivation};
pub use mission_cues::NativeMissionCue;
pub(crate) use mission_cues::NativeMissionEvent;

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::sync::Arc;
use bees_gameplay::commands::{
    GameCommandError, NativeBombWeapon, NativeEnemyTargetStats,
};
use bees_gameplay::level_host::{
    NativeLevelObservationContext, NativeLevelActivationContext,
    NativeLevelStrategyInputs,
};
use bees_gameplay::level_rules::{
    NativeLevelRewardMetadata, NativeLevelCommandContext,
    NativePlayerChaseMeasurements, NativePlayerControlGauges,
    NativeScriptedQueueMeasurements, NativeNavigationWorkerMeasurements,
};
use bees_gameplay::perception_world::NativeLevelPerceptionInputs;
use bees_gameplay::squads::NativeSquadRoster;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::Aabb;
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, ControlledShipPerception,
};
use bees_sim::combat_world::{CombatWorld, CombatWorldError, CombatEvent};
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::navigation_scheduler::{NavigationScheduler, NavigationSchedulerError};
use bees_sim::perception::CapabilitySource;
use bees_sim::combat_perception_bridge::{
    update_exploration_from_world, CombatPerceptionError,
};
use bees_sim::team_exploration::{TeamExplorationGrid, Explorer};
use bees_sim::vision::{HiveMindObserver, VisionFirstSighting};
use bees_sim::level_discovery::DiscoveryKind as WorldDiscoveryKind;
use bees_sim::rl_discovery::DiscoveryKind as RewardDiscoveryKind;
use bees_sim::obstacle::ObstacleKind;
use bees_sim::perception_weapons::SelfWeapon;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_gameplay::inference::NativePolicyRoute;
use crate::fleet_inventory::{NativeFleetInventory, NativeFleetShipValue};
use crate::ship_specials::NativeBeaconTemplate;
use bees_gameplay::player_inputs::{NativeLevelPlayerContext, NativePlayerPermissions, NativePlayerAuthority};
use bees_gameplay::squad_geometry::SquadMemberOffset;
use bees_sim::{geometry::Vec2, squad_movement::SquadFormationOptions};

#[derive(Clone, Copy, Debug)]
pub struct NativeSourceFormation {
    pub offset: Vec2,
    pub clearance: i32,
}

pub struct NativeSourcePlayerInputs {
    pub human_side: i32,
    pub has_obstacles: bool,
    pub commands: HashMap<SquadLifetime, bees_gameplay::player_inputs::NativePlayerCommandInputs>,
    pub squads: HashMap<SquadLifetime, NativePlayerPermissions>,
    /// Mutable source Squad.ShouldChase state; command director continues
    /// to own actual Aggressive command lifetimes.
    pub chasing: HashSet<SquadLifetime>,
    /// Source Squad.IsMatchingSpeed / CurrentSpeed: shared command and UI
    /// ownership; the physics ShipMotion owns the current per-ship speed.
    pub matching_speeds: HashMap<SquadLifetime, f32>,
    /// The real source Squad.CeaseFire, separate from individual authored
    /// Ship.IsCeaseFire. Older exports intentionally leave this unknown.
    pub cease_fire: HashMap<SquadLifetime, bool>,
    pub ships: HashMap<i64, NativeSourceFormation>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PolicyOnlyMetadataError {
    MissingAgent(i64),
    MissingSide(i32),
    InvalidSource,
    DuplicateAgent(i64),
    DuplicateShip(i64),
    DuplicateSide(i32),
}

/// One current source-owning policy controller. Its weapon slots are
/// only non-turret values; registered CombatWorld native turrets replace
/// those during observation assembly.
pub struct AuthoredPolicyAgent {
    pub ship_id: i64,
    pub side: i32,
    /// The actual Unity MA-POCA Agent instance controlling this ship.
    /// It is distinct from the physical ship ID; None for non-group
    /// gameplay and legacy scenes with no authenticated group record.
    pub source_group_agent_id: Option<u64>,
    pub route: NativePolicyRoute,
    pub controller: ControlledShipPerception,
    pub capability: CapabilitySource,
    pub parent_carrier_id: Option<i64>,
    pub own_weapons: [Option<SelfWeapon>; 5],
}

/// The authentic 16x16 team sight memory starts fresh for each native
/// episode, then advances on the same canonical fixed-step clock as combat.
/// Observer-local 21x21 navigation remains in the perception assembler.
pub struct AuthoredPolicySide {
    pub side: i32,
    pub exploration: TeamExplorationGrid,
}

/// Captured separately from an agent's effective v26 self sight range:
/// HiveMindVision's CircleCollider2D holds its original Create-time Range.
#[derive(Clone, Copy, Debug)]
pub struct NativeSourceVision {
    /// Actual Ship.Sight, distinct from HiveMind's zero-sight range fallback.
    pub source_sight: Option<i32>,
    pub effective_range: i32,
    pub trigger_range: i32,
    /// Fixed source FogOfWarVision.Range / 2. None is a legacy scene.
    pub fog_radius: Option<f32>,
    pub registered: bool,
    pub enabled: bool,
}

/// Source-authored native matches. The owner provides the real
/// NavigationRuntime (the same object used by gameplay pathfinding),
/// complete ship source observations, team environments and FleetShip
/// inventory; commands query those same owners rather than a copied roster.
pub struct NativePolicyOnlyMetadata {
    pub targeting_context: Option<bees_gameplay::squad_targeting::NativeTargetingContext>,
    /// Exact Level.ActivateMining from the source scene; unavailable for
    /// older physical exports, never inferred from present asteroids.
    pub activate_mining: Option<bool>,
    /// Exact source Level.ActivateFogOfWar for the displayed player.
    /// None means an older export that did not capture the option.
    pub activate_fog_of_war: Option<bool>,
    /// Source Stage command-authority gate, distinct from game physics
    /// and not inferred from whether a model controller is present.
    pub hivemind_active: Option<bool>,
    pub hivemind_initial_delay_seconds: Option<i32>,
    pub hivemind_starting_positions: Option<[Vec2; 2]>,
    /// Immutable per-squad source eligibility/opponent. The live roster
    /// and command director still own membership and active requests.
    pub hivemind_squads: HashMap<SquadLifetime, bees_gameplay::level_host::NativeHiveMindSquadSource>,
    pub bounds: Aabb,
    pub navigation: NavigationRuntime,
    /// Persistent background pathfinding for explicitly opted-in graphical
    /// games. Training and replay retain deterministic synchronous routes.
    navigation_scheduler: Option<NavigationScheduler>,
    navigation_worker_counts: NativeNavigationWorkerMeasurements,
    pub activate_collision_asteroids: bool,
    pub fleet: NativeFleetInventory,
    pub ship_catalog: Arc<ShipAssetCatalog>,
    authored_ships: HashMap<i64, AuthoredShipPerception>,
    /// Exact sight ranges for ALL original allied ships, including
    /// non-policy escorts that contribute to team coverage.
    effective_vision: HashMap<i64, NativeSourceVision>,
    /// Exact source OriginalHealth-based values of static obstacles.
    /// The dynamic collider and side knowledge stay in CombatWorld.
    source_static_discovery_values: HashMap<i64, i32>,
    explorers: Vec<Explorer>,
    agents: HashMap<i64, AuthoredPolicyAgent>,
    sides: HashMap<i32, AuthoredPolicySide>,
    beacon_templates: HashMap<i64, Arc<NativeBeaconTemplate>>,
    player: NativeSourcePlayerInputs,
    pub(crate) reinforcements: std::collections::VecDeque<crate::reinforcements::ReinforcementWave>,
    scripted_queues: Vec<scripted::NativeScriptedQueueState>,
    scripted_members: Vec<bees_sim::squad_movement::SquadFormationMember>,
    scripted_friendlies: Vec<bees_gameplay::commands::NativeFriendlySquad>,
    scripted_counts: NativeScriptedQueueMeasurements,
    /// One source-second chase clock shared by the player's starting
    /// squads, whose Unity timers are all armed at Squad.Setup.
    chase_elapsed_seconds: f32,
    pub chase_measurements: NativePlayerChaseMeasurements,
}

impl NativePolicyOnlyMetadata {
    /// Interactive presentation may dedicate long-lived CPU path workers to
    /// this actual Level. The same CombatWorld still owns each job, route
    /// installation and physics; model-driven headless runs leave this off.
    pub fn enable_navigation_workers(
        &mut self, count: usize,
    ) -> Result<(), NavigationSchedulerError> {
        let workers = NavigationScheduler::new(self.navigation.clearance().grid(), count)?;
        self.navigation_scheduler = Some(workers);
        self.navigation_worker_counts = NativeNavigationWorkerMeasurements::default();
        Ok(())
    }

    /// Initial controller routes for the current episode, independent of reward groups.
    pub fn authored_policy_slots_by_side(&self) -> BTreeMap<i32, BTreeSet<u32>> {
        let mut slots = BTreeMap::<i32, BTreeSet<u32>>::new();
        for agent in self.agents.values() {
            slots.entry(agent.side).or_default().insert(agent.route.model_slot);
        }
        slots
    }

    pub fn new(
        bounds: Aabb,
        navigation: NavigationRuntime,
        activate_collision_asteroids: bool,
        fleet: NativeFleetInventory,
        ship_catalog: Arc<ShipAssetCatalog>,
        ships: impl IntoIterator<Item=(i64, AuthoredShipPerception)>,
        vision: impl IntoIterator<Item=(i64, NativeSourceVision)>,
        source_static_discovery_values: HashMap<i64, i32>,
        agents: impl IntoIterator<Item=AuthoredPolicyAgent>,
        sides: impl IntoIterator<Item=AuthoredPolicySide>,
        beacon_templates: HashMap<i64, Arc<NativeBeaconTemplate>>,
        player: NativeSourcePlayerInputs,
    ) -> Result<Self, PolicyOnlyMetadataError> {
        if player.human_side <= 0 || !bounds.min.is_finite() || !bounds.max.is_finite() ||
            bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y {
            return Err(PolicyOnlyMetadataError::InvalidSource);
        }
        let mut authored_ships = HashMap::new();
        for (id, data) in ships {
            if id <= 0 || !data.valid() || fleet.get(id).is_none() {
                return Err(PolicyOnlyMetadataError::InvalidSource);
            }
            if authored_ships.insert(id, data).is_some() {
                return Err(PolicyOnlyMetadataError::DuplicateShip(id));
            }
        }
        let mut effective_vision = HashMap::new();
        for (id, source) in vision {
            if id <= 0 || source.source_sight.is_some_and(|sight| sight < 0) || source.effective_range < 0 ||
                source.trigger_range < 0 ||
                source.fog_radius.is_some_and(|radius| !radius.is_finite() || radius < 0.0) ||
                (source.enabled &&
                    (!source.registered || source.trigger_range == 0)) ||
                !authored_ships.contains_key(&id) ||
                effective_vision.insert(id, source).is_some() {
                return Err(PolicyOnlyMetadataError::InvalidSource);
            }
        }
        if effective_vision.len() != authored_ships.len() ||
            source_static_discovery_values.iter().any(|(&id, &value)|
                id <= 0 || value <= 0
            ) {
            return Err(PolicyOnlyMetadataError::InvalidSource);
        }
        let mut side_map = HashMap::new();
        for side in sides {
            if side.side <= 0 {
                return Err(PolicyOnlyMetadataError::InvalidSource);
            }
            let id = side.side;
            if side_map.insert(id, side).is_some() {
                return Err(PolicyOnlyMetadataError::DuplicateSide(id));
            }
        }
        let mut agent_map = HashMap::new();
        let mut seen_group_agents = std::collections::HashSet::new();
        for agent in agents {
            if agent.ship_id <= 0 || agent.side <= 0 ||
                !agent.controller.effective_vision_range.is_finite() ||
                agent.controller.effective_vision_range < 0.0 ||
                !agent.capability.special_resource_fraction.is_finite() ||
                !agent.capability.special_cooldown_fraction.is_finite() ||
                !agent.capability.special_phase.is_finite() ||
                agent.capability.ship_mined_tsv < 0 ||
                agent.capability.team_retainable_mined_tsv < 0 ||
                agent.capability.side_starting_tsv < 0 ||
                !authored_ships.contains_key(&agent.ship_id) ||
                !side_map.contains_key(&agent.side) || agent.route.team_id > 1 ||
                agent.parent_carrier_id.is_some_and(|id|
                    !authored_ships.contains_key(&id)
                ) ||
                agent.source_group_agent_id.is_some_and(|id|
                    id == 0 || !seen_group_agents.insert(id)
                ) {
                return Err(PolicyOnlyMetadataError::InvalidSource);
            }
            let id = agent.ship_id;
            if agent_map.insert(id, agent).is_some() {
                return Err(PolicyOnlyMetadataError::DuplicateAgent(id));
            }
        }
        if beacon_templates.iter().any(|(id, template)|
            !authored_ships.contains_key(id) || !template.perception.valid()
        ) { return Err(PolicyOnlyMetadataError::InvalidSource); }
        if authored_ships.keys().any(|id| !player.ships.get(id)
            .is_some_and(|data| data.offset.is_finite() && data.clearance >= 0)) ||
            // AI squads can carry the saved ShouldChase value even
            // though Unity arms CheckChase timers only on user squads.
            // Preserve their authored state; the timer checks authority.
            player.chasing.iter().any(|owner|
                !player.squads.contains_key(owner)) ||
            player.matching_speeds.iter().any(|(owner, &speed)|
                !speed.is_finite() || speed < 0.0 ||
                !player.squads.contains_key(owner)) ||
            player.cease_fire.keys().any(|owner|
                !player.squads.contains_key(owner)) {
            return Err(PolicyOnlyMetadataError::InvalidSource);
        }
        Ok(Self {
            targeting_context: None, activate_mining: None,
            activate_fog_of_war: None, hivemind_active: None, hivemind_initial_delay_seconds: None,
            hivemind_starting_positions: None,
            hivemind_squads: HashMap::new(),
            bounds, navigation, navigation_scheduler: None,
            navigation_worker_counts: NativeNavigationWorkerMeasurements::default(),
            activate_collision_asteroids, fleet, ship_catalog,
            authored_ships, effective_vision,
            source_static_discovery_values,
            explorers: Vec::new(),
            agents: agent_map, sides: side_map, beacon_templates, player,
            reinforcements: std::collections::VecDeque::new(),
            scripted_queues: Vec::new(),
            scripted_members: Vec::new(),
            scripted_friendlies: Vec::new(),
            scripted_counts: NativeScriptedQueueMeasurements::default(),
            chase_elapsed_seconds: 0.0,
            chase_measurements: NativePlayerChaseMeasurements::default(),
        })
    }

    /// A rendered Level may change externally authored ship/controller
    /// fields between synchronous inference windows. Exploration itself
    /// is fixed-step owned and must not be independently resampled here.
    pub fn side_mut(&mut self, side: i32) -> Option<&mut AuthoredPolicySide> {
        self.sides.get_mut(&side)
    }

    pub fn agent_mut(&mut self, ship_id: i64) -> Option<&mut AuthoredPolicyAgent> {
        self.agents.get_mut(&ship_id)
    }

    pub fn authored_ship_mut(&mut self, ship_id: i64)
        -> Option<&mut AuthoredShipPerception>
    {
        self.authored_ships.get_mut(&ship_id)
    }
}

impl NativeLevelRewardMetadata for NativePolicyOnlyMetadata {
    fn register_scout_beacons(
        &mut self, world: &CombatWorld, events: &[CombatEvent],
    ) -> Result<(), CombatPerceptionError> {
        for event in events {
            let CombatEvent::ScoutBeaconDropped { scout_id, beacon_id, parent_fleet_id } = event
                else { continue; };
            let invalid = || CombatPerceptionError::InvalidAuthoredMetadata;
            let template = self.beacon_templates.get(scout_id).ok_or_else(invalid)?;
            let ship = world.ship(*beacon_id).ok_or_else(invalid)?;
            if ship.ship_type != template.physical.beacon_asset.ship_type ||
                world.ship_fleet_identity(*scout_id) != Some(*parent_fleet_id) ||
                world.ship_fleet_identity(*beacon_id) != Some(*parent_fleet_id) ||
                self.authored_ships.contains_key(beacon_id) || self.effective_vision.contains_key(beacon_id)
            { return Err(invalid()); }
            let cargo = ship.vitals.mined_cargo();
            self.fleet.register_spawned_ship(NativeFleetShipValue {
                ship_id: *beacon_id, has_persistent_fleet_value: false,
                minerals_mined_this_level: cargo,
            }).map_err(|_| invalid())?;
            let mut perception = template.perception;
            perception.minerals_mined_this_level = cargo;
            self.authored_ships.insert(*beacon_id, perception);
            self.effective_vision.insert(*beacon_id, template.vision);
        }
        Ok(())
    }

    fn damage_metadata(&self, ship_id: i64)
        -> Option<bees_sim::rl_shaping::DamageRewardMetadata>
    {
        self.fleet.damage_metadata(ship_id)
    }

    fn current_mined_tsv(&self, ship_id: i64) -> Option<i32> {
        self.fleet.current_mined_tsv(ship_id)
    }

    fn credit_mined_minerals(&mut self, ship_id: i64, amount: i32) -> Option<i32> {
        self.fleet.credit_mined_minerals(ship_id, amount)
    }

    fn collect_hivemind_observers(
        &self, world: &CombatWorld, output: &mut Vec<HiveMindObserver>,
    ) -> Result<(), CombatPerceptionError> {
        if world.ships().len() != world.physics().ships().len() {
            return Err(CombatPerceptionError::MismatchedWorldStorage);
        }
        output.clear();
        for (ship, body) in world.ships().iter()
            .zip(world.physics().ships())
        {
            if ship.entity_id != body.id {
                return Err(CombatPerceptionError::MismatchedWorldStorage);
            }
            if !body.active || ship.vitals.is_dead() ||
                !self.sides.contains_key(&ship.side) {
                continue;
            }
            let source = self.effective_vision.get(&ship.entity_id)
                .ok_or(CombatPerceptionError::MissingEffectiveVision(
                    ship.entity_id
                ))?;
            if source.registered && source.enabled {
                output.push(HiveMindObserver {
                    ship_id: ship.entity_id,
                    effective_range: source.trigger_range,
                    registered: true, enabled: true,
                });
            }
        }
        Ok(())
    }

    fn verified_discovery_reward(
        &self, world: &CombatWorld, sighting: VisionFirstSighting,
    ) -> Result<(RewardDiscoveryKind, i32), CombatPerceptionError> {
        match sighting.kind {
            WorldDiscoveryKind::Ship => {
                let ship = world.ship(sighting.target_id).ok_or(
                    CombatPerceptionError::MissingShip(sighting.target_id)
                )?;
                Ok((RewardDiscoveryKind::EnemyShip, ship.vitals.tsv().max(1)))
            }
            WorldDiscoveryKind::MapObject => {
                let object = world.map_object(sighting.target_id).ok_or(
                    CombatPerceptionError::InvalidAuthoredMetadata
                )?;
                Ok((RewardDiscoveryKind::MapObject, object.max_health.max(1)))
            }
            kind => {
                // A discovered obstacle is rare; linear inspection here
                // avoids retaining a duplicate mutable obstacle ledger
                // or doing hash work per observer candidate.
                let obstacle = world.obstacles().iter()
                    .find(|item| item.entity_id == sighting.target_id)
                    .ok_or(CombatPerceptionError::InvalidAuthoredMetadata)?;
                match (kind, obstacle.kind) {
                    (WorldDiscoveryKind::MiningAsteroid,
                     ObstacleKind::MiningAsteroid) => {
                        let vitals = obstacle.vitals.ok_or(
                            CombatPerceptionError::MissingAsteroidVitals(
                                sighting.target_id,
                            )
                        )?;
                        Ok((RewardDiscoveryKind::MiningAsteroid,
                            vitals.original_health.max(1)))
                    }
                    (WorldDiscoveryKind::Obstacle,
                     ObstacleKind::CollisionAsteroid) =>
                        Ok((RewardDiscoveryKind::CollisionAsteroid,
                            i32::from(obstacle.size_class))),
                    (WorldDiscoveryKind::Obstacle,
                     ObstacleKind::StaticObstacle | ObstacleKind::MapBorder) => {
                        let value = *self.source_static_discovery_values
                            .get(&sighting.target_id)
                            .ok_or(CombatPerceptionError::InvalidAuthoredMetadata)?;
                        Ok((RewardDiscoveryKind::StaticObstacle, value))
                    }
                    _ => Err(CombatPerceptionError::InvalidAuthoredMetadata),
                }
            }
        }
    }

    fn update_observation_memory(
        &mut self, world: &CombatWorld, progress: f32,
    ) -> Result<(), CombatPerceptionError> {
        // No per-tick allocation: reuse one scratch explorer array for the
        // two sides. Every current living ship contributes its real source
        // HiveMindVision range, regardless of policy ownership.
        for side in self.sides.values_mut() {
            update_exploration_from_world(
                world, side.side, self.bounds, progress,
                &mut side.exploration, &mut self.explorers,
                |ship_id| self.effective_vision.get(&ship_id)
                    .map(|vision| vision.effective_range),
            )?;
        }
        Ok(())
    }
}

impl NativeLevelCommandContext for NativePolicyOnlyMetadata {
    fn scripted_queue_owns(&self, owner: SquadLifetime) -> bool {
        self.scripted_queues.iter().any(|queue|
            queue.owner == owner && queue.claimed
        )
    }

    fn player_chase_measurements(&self) -> NativePlayerChaseMeasurements {
        self.chase_measurements
    }

    fn advance_scripted_commands(
        &mut self, world: &mut CombatWorld,
        events: &mut bees_gameplay::level_events::NativeLevelEventProcessor,
        random: &mut bees_sim::simulation::RandomStream,
        completed_tick: u64,
    ) -> Result<u64, bees_gameplay::level_events::NativeLevelEventError> {
        self.advance_source_scripted(world, events, random, completed_tick)
    }

    fn scripted_queue_measurements(
        &self, tick: u64, events: &bees_gameplay::level_events::NativeLevelEventProcessor,
    ) -> NativeScriptedQueueMeasurements {
        self.current_scripted_queue_measurements(tick, events)
    }

    fn next_reinforcement_tick(&self) -> Option<u64> {
        self.reinforcements.iter().filter_map(|wave| wave.scheduled_tick()).min()
    }

    fn pending_reinforcement_waves(&self) -> usize {
        self.reinforcements.len()
    }

    fn reinforcement_gauges(&self) -> bees_gameplay::level_rules::NativeReinforcementGauges {
        // Source owns the queue. Sample it on status requests only;
        // never walk deferred squads during an ordinary physics tick.
        bees_gameplay::level_rules::NativeReinforcementGauges {
            pending_waves: self.reinforcements.len(),
            pending_squads: self.reinforcements.iter()
                .map(|wave| wave.squad_count()).sum(),
            pending_ships: self.reinforcements.iter()
                .map(|wave| wave.ship_count()).sum(),
            waiting_for_campaign_cue: self.reinforcements.iter()
                .filter(|wave| wave.is_campaign_triggered()).count(),
            waiting_for_ship_death: self.reinforcements.iter()
                .filter(|wave| wave.is_ship_death_triggered()).count(),
            timed_waves: self.reinforcements.iter()
                .filter(|wave| wave.scheduled_tick().is_some()).count(),
        }
    }

    fn player_control_gauges(&self, roster: &NativeSquadRoster) -> NativePlayerControlGauges {
        // The source metadata includes authored squads reserved for later
        // reinforcement waves. Only the canonical live roster determines
        // whether a player squad has actually entered the battlefield.
        let player = &self.player;
        let eligible = |owner: &SquadLifetime| roster.get(*owner).is_some() &&
            player.squads.get(owner).is_some_and(|p| p.user_controlled);
        NativePlayerControlGauges {
            active_player_squads: roster.source_order().iter()
                .filter(|&&owner| eligible(&owner)).count(),
            matched_speed_squads: player.matching_speeds.keys()
                .filter(|owner| eligible(owner)).count(),
            chase_enabled_squads: player.chasing.iter()
                .filter(|owner| eligible(owner)).count(),
            locked_on_squads: roster.source_order().iter()
                .filter(|&&owner| player.squads.get(&owner)
                    .is_some_and(|p| p.user_controlled && p.locked_on)).count(),
            cease_fire_known_squads: player.cease_fire.keys()
                .filter(|owner| eligible(owner)).count(),
            cease_fire_enabled_squads: player.cease_fire.iter()
                .filter(|entry| *entry.1 && eligible(entry.0)).count(),
        }
    }

    fn advance_player_chase(
        &mut self, world: &mut CombatWorld,
        events: &mut bees_gameplay::level_events::NativeLevelEventProcessor,
        random: &mut bees_sim::simulation::RandomStream,
        completed_tick: u64, dt_seconds: f32,
    ) -> Result<u64, bees_gameplay::level_events::NativeLevelEventError> {
        self.advance_source_chase(world, events, random, completed_tick, dt_seconds)
    }

    fn player_scout_minion_created(
        &mut self, world: &mut CombatWorld,
        mother: SquadLifetime, child: SquadLifetime, beacon_id: i64,
    ) -> Result<(), GameCommandError> {
        let parent = self.player.squads.get(&mother).copied()
            .ok_or(GameCommandError::InvalidSquad)?;
        let source = *self.player.commands.get(&mother)
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        if self.player.squads.contains_key(&child) ||
            self.player.commands.contains_key(&child) ||
            self.player.ships.contains_key(&beacon_id) ||
            world.ship(beacon_id).is_none() {
            return Err(GameCommandError::InvalidSquad);
        }

        // Scout.CreateMinionSquad copies these exact source fields.
        // Scout.DropBeacon then sets CanAcceptUserInput=false. Inherit
        // read-only state but never grant direct command authority.
        let mut inherited = source;
        inherited.is_carrier_squad = Some(false);
        self.player.squads.insert(child, NativePlayerPermissions {
            user_controlled: parent.user_controlled,
            can_accept_input: false, locked_on: false,
        });
        self.player.commands.insert(child, inherited);
        self.player.ships.insert(beacon_id, NativeSourceFormation {
            offset: Vec2::ZERO, clearance: 0,
        });
        if self.player.chasing.contains(&mother) {
            self.player.chasing.insert(child);
        }
        if self.player.matching_speeds.contains_key(&mother) {
            // Beacon is immobile. Unity's new minion Squad.CurrentSpeed
            // begins at zero even if its inherited IsMatchingSpeed is true.
            self.player.matching_speeds.insert(child, 0.0);
        }
        if let Some(&enabled) = self.player.cease_fire.get(&mother) {
            self.player.cease_fire.insert(child, enabled);
            if world.ship(beacon_id).is_some_and(|ship| !ship.vitals.is_dead()) {
                world.set_ship_cease_fire(beacon_id, enabled)
                    .map_err(GameCommandError::World)?;
            }
        }
        Ok(())
    }

    fn player_squad_retired(&mut self, owner: SquadLifetime) {
        let before = self.scripted_queues.len();
        self.scripted_queues.retain(|queue| queue.owner != owner);
        self.scripted_counts.queues_cancelled +=
            (before - self.scripted_queues.len()) as u64;
        // A dead source squad cannot reappear under the same lifetime.
        // Drop mutable UI/command preferences without touching original
        // fleet, reward, sight or discovery history.
        self.player.chasing.remove(&owner);
        self.player.matching_speeds.remove(&owner);
        self.player.cease_fire.remove(&owner);
        self.player.squads.remove(&owner);
        self.player.commands.remove(&owner);
    }

    fn player_command_finished(&mut self, owner: SquadLifetime) {
        if let Some(permission) = self.player.squads.get_mut(&owner) {
            permission.locked_on = false;
        }
    }

    fn deploy_reinforcements(
        &mut self, world: &mut CombatWorld, events: &mut bees_gameplay::level_events::NativeLevelEventProcessor, tick: u64,
    ) -> Result<(), bees_gameplay::level_rules::NativeLevelStepError> {
        self.activate_reinforcements(world, events, tick)
    }

    fn collect_bomber_contacts(&mut self, world: &CombatWorld, roster: &NativeSquadRoster,
        output: &mut Vec<bees_gameplay::level_rules::NativeBomberContact>,
    ) -> Result<(), GameCommandError> {
        output.clear();
        for &(bomber_id, contacted_target_id) in world.bomber_body_entries() {
            if roster.owning_squad(bomber_id).is_some() && roster.owning_squad(contacted_target_id).is_some() {
                output.push(bees_gameplay::level_rules::NativeBomberContact { bomber_id, contacted_target_id });
            }
        }
        Ok(())
    }

    fn bombing_command_snapshot(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_retarget: bool,
    ) -> Result<bees_gameplay::commands::NativeBombingTimerSnapshot, GameCommandError> {
        self.live_bombing_snapshot(world, roster, owner, enemy, need_retarget)
    }

    fn charge_command_snapshot(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_retarget: bool,
    ) -> Result<bees_gameplay::commands::NativeChargeTimerSnapshot, GameCommandError> {
        self.live_charge_snapshot(world, roster, owner, enemy, need_retarget)
    }

    fn circle_command_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime,
    ) -> Result<bees_gameplay::commands::NativeCircleCommandSnapshot, GameCommandError> {
        self.live_circle_snapshot(world, roster, owner, enemy)
    }

    fn swipe_command_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_candidates: bool,
    ) -> Result<bees_gameplay::commands::NativeSwipeCommandSnapshot, GameCommandError> {
        self.live_swipe_snapshot(world, roster, owner, enemy, need_candidates)
    }

    fn aggressive_command_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_all: bool,
    ) -> Result<bees_gameplay::commands::NativeAggressiveCommandSnapshot, GameCommandError> {
        self.live_aggressive_snapshot(world, roster, owner, enemy, need_all)
    }

    fn automatic_weapon_inputs(&self, ship_id: i64,
        roster: &bees_gameplay::squads::NativeSquadRoster,
        commands: &bees_gameplay::commands::NativeSquadCommandDirector,
    ) -> Result<Option<bees_sim::combat_world::NativeAutomaticWeaponInputs>, CombatWorldError> {
        let owner = roster.owning_squad(ship_id).ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
        if let Some(input) = commands.automatic_weapon_input(owner) { return Ok(Some(input)); }
        let source = self.player.commands.get(&owner).ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
        Ok(Some(bees_sim::combat_world::NativeAutomaticWeaponInputs {
            strategy_id: source.shooting.id, has_command: false, command_key: None,
        }))
    }

    fn update_survivor_formation(
        &mut self, _owner: SquadLifetime, offsets: &[SquadMemberOffset],
    ) -> Result<(), GameCommandError> {
        if offsets.iter().any(|entry| !entry.offset.is_finite() ||
            !self.player.ships.contains_key(&entry.ship_id)) {
            return Err(GameCommandError::MissingCombatSnapshot);
        }
        for entry in offsets {
            self.player.ships.get_mut(&entry.ship_id).expect("validated").offset = entry.offset;
        }
        Ok(())
    }
    fn ship_asset_catalog(&self) -> Option<&ShipAssetCatalog> {
        Some(&self.ship_catalog)
    }

    fn navigation_worker_measurements(&self) -> NativeNavigationWorkerMeasurements {
        self.navigation_worker_counts
    }

    fn advance_navigation_for_tick(
        &mut self, world: &mut CombatWorld, completed_tick: u64,
    ) -> Result<u64, CombatWorldError> {
        let Some(scheduler) = self.navigation_scheduler.as_mut() else {
            // Deterministic headless simulation executes the same canonical
            // path requests synchronously with its Level-owned workspace.
            return self.advance_navigation(world);
        };
        // Physics owns all StaticObstacle admissions and integrates any
        // animated static poses. A source geometry revision (or real motion)
        // invalidates its snapshot, not merely the presence of static walls.
        let revision = world.static_navigation_revision();
        let static_moves = world.has_moving_static_navigation_obstacles();
        let step = scheduler.pump(
            world, &mut self.navigation, completed_tick, revision, static_moves,
            self.activate_collision_asteroids,
        ).map_err(|error| match error {
            NavigationSchedulerError::InvalidMap(source) =>
                CombatWorldError::NavigationMap(source),
            NavigationSchedulerError::World(source) => source,
            NavigationSchedulerError::WorkerPanicked =>
                CombatWorldError::NavigationWorkerPanicked,
            NavigationSchedulerError::WorkerProtocol =>
                CombatWorldError::NavigationWorkerProtocol,
            NavigationSchedulerError::WorkerUnavailable |
            NavigationSchedulerError::InvalidWorkerCount |
            NavigationSchedulerError::Spawn(_) =>
                CombatWorldError::NavigationWorkerUnavailable,
        })?;
        let counts = &mut self.navigation_worker_counts;
        counts.launched += step.launched as u64;
        counts.completed += step.completed as u64;
        counts.requeued += step.requeued as u64;
        counts.discarded += step.discarded as u64;
        counts.clearance_refreshes += u64::from(step.clearance_refreshed);
        counts.waiting_for_static_rebuild_frames +=
            u64::from(step.waiting_for_static_rebuild);
        counts.busy_workers = step.busy_workers;
        // Match the synchronous Level counter: every completed path job
        // counts, including lifecycle-safe requeues and discarded results.
        // The accepted/requeued/discarded split remains separately measured.
        Ok(step.completed as u64)
    }

    fn navigation(&self) -> &NavigationRuntime {
        &self.navigation
    }

    fn navigation_mut(&mut self) -> &mut NavigationRuntime {
        &mut self.navigation
    }

    fn collision_asteroids_enabled(&self) -> bool {
        self.activate_collision_asteroids
    }

    fn initialize_navigation_from_world(
        &mut self, combat: &CombatWorld,
    ) -> Result<(), bees_sim::navigation::NavigationError> {
        self.navigation.refresh_world_static(
            combat.static_navigation_revision(), combat.static_navigation_obstacles(),
        )?;
        Ok(())
    }
}

impl NativeLevelActivationContext for NativePolicyOnlyMetadata {
    fn targeting_context(&self) -> Option<bees_gameplay::squad_targeting::NativeTargetingContext> { self.targeting_context }
    fn targeting_ship_catalog(&self) -> &bees_sim::ship_assets::ShipAssetCatalog { &self.ship_catalog }

    fn source_mining_enabled(&self) -> Option<bool> { self.activate_mining }

    fn source_hivemind_active(&self) -> Option<bool> {
        self.hivemind_active
    }

    fn source_hivemind_initial_delay_seconds(&self) -> Option<i32> {
        self.hivemind_initial_delay_seconds
    }

    fn source_hivemind_starting_position(&self, side: i32) -> Option<Vec2> {
        let index = usize::try_from(side.checked_sub(1)?).ok()?;
        self.hivemind_starting_positions?.get(index).copied()
    }

    fn source_hivemind_squad(
        &self, owner: SquadLifetime,
    ) -> Option<bees_gameplay::level_host::NativeHiveMindSquadSource> {
        self.hivemind_squads.get(&owner).copied().map(|mut source| {
            if self.scripted_queues.iter().any(|queue|
                queue.owner == owner && queue.claimed
            ) {
                source.hivemind_controlled = false;
            }
            source
        })
    }

    fn source_strategy_matchup(
        &self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: Option<SquadLifetime>,
    ) -> Result<Option<bees_gameplay::strategy_matchup::NativeSourceStrategyMatchup>, GameCommandError> {
        bees_gameplay::strategy_matchup::build_source_strategy_matchup(
            world, roster, &self.ship_catalog, owner, enemy,
            |world, own, target| self.source_ship_valid_target(world, own, target),
        )
    }

    fn strategy_inputs(
        &self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: Option<SquadLifetime>,
    ) -> Result<NativeLevelStrategyInputs, GameCommandError> {
        let source = self.player.commands.get(&owner).ok_or(GameCommandError::MissingCombatSnapshot)?;
        let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        let max_sight = own.members.iter().try_fold(0, |value, id|
            self.effective_vision.get(id)?.source_sight.map(|sight| value.max(sight)));
        Ok(NativeLevelStrategyInputs {
            formation: self.player_formation_options(), settings: source.settings,
            enemy: enemy.map(|target| self.live_enemy(world, roster, owner, target)).transpose()?,
            scouting: max_sight.map(|sight| bees_gameplay::commands::NativeScoutingContext {
                max_sight: sight as f32, human_side: self.player.human_side,
            }),
        })
    }

    fn stored_formation_offset(
        &self, ship_id: i64,
    ) -> Option<bees_sim::geometry::Vec2> {
        self.player.ships.get(&ship_id).map(|data| data.offset)
    }

    fn current_path_clearance(&self, ship_id: i64) -> Option<i32> {
        self.player.ships.get(&ship_id).map(|data| data.clearance)
    }

    fn collect_bomb_activation(
        &self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime,
        weapons: &mut Vec<NativeBombWeapon>,
        enemies: &mut Vec<NativeEnemyTargetStats>,
    ) -> Result<bool, GameCommandError> {
        weapons.clear(); enemies.clear();
        self.live_enemy(world, roster, owner, enemy)?;
        let carrier = self.player.commands.get(&owner).and_then(|source| source.is_carrier_squad)
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        for &id in &roster.get(owner).ok_or(GameCommandError::InvalidSquad)?.members {
            let spec = world.special_weapon_spec(id).ok_or(GameCommandError::MissingCombatSnapshot)?;
            weapons.push(NativeBombWeapon { ship_id: id, weapon_id: spec.weapon_id, power: spec.power });
        }
        for &id in &roster.get(enemy).ok_or(GameCommandError::InvalidEnemy)?.members {
            enemies.push(world.weapon_target_rank_stats(id)?);
        }
        Ok(carrier)
    }
}

impl NativePlayerAuthority for NativePolicyOnlyMetadata {
    fn player_fog_of_war_enabled(&self) -> bool {
        self.activate_fog_of_war == Some(true)
    }

    fn player_viewer_side(&self) -> Option<i32> {
        self.targeting_context.filter(|source| source.has_player && source.user_side > 0)
            .map(|source| source.user_side)
    }

    fn player_fog_vision_radius(&self, ship_id: i64) -> Option<f32> {
        // New source snapshots capture the Create-time mask radius
        // directly. Older snapshots have only the static effective sight,
        // which is the best available approximation. Beacons acquire
        // source vision through the same authoritative registration.
        self.effective_vision.get(&ship_id)
            .map(|vision| vision.fog_radius.unwrap_or(vision.effective_range as f32))
    }

    fn weapon_obstacles_enabled(&self) -> Option<bool> { Some(self.player.has_obstacles) }
    fn player_permissions(&self, owner: SquadLifetime) -> Option<NativePlayerPermissions> {
        self.player.squads.get(&owner).copied()
    }
    fn player_carrier_squad(&self, owner: SquadLifetime) -> Option<bool> {
        self.player.commands.get(&owner).and_then(|source| source.is_carrier_squad)
    }
    fn player_shooting_strategy(&self, owner: SquadLifetime)
        -> Option<bees_hivemind::protocol::StrategyIdentity>
    {
        self.player.commands.get(&owner).map(|command| command.shooting)
    }
    fn player_should_chase(&self, owner: SquadLifetime) -> bool {
        self.player.chasing.contains(&owner)
    }
    fn player_matched_speed(&self, owner: SquadLifetime) -> Option<f32> {
        self.player.matching_speeds.get(&owner).copied()
    }
    fn player_cease_fire(&self, owner: SquadLifetime) -> Option<bool> {
        self.player.cease_fire.get(&owner).copied()
    }
}

impl NativeLevelPlayerContext for NativePolicyOnlyMetadata {
    fn set_player_cease_fire(&mut self, owner: SquadLifetime,
        enabled: bool,
    ) -> Result<(), GameCommandError> {
        if !self.player.squads.get(&owner)
            .is_some_and(|permission|
                permission.user_controlled && permission.can_accept_input) {
            return Err(GameCommandError::InvalidSquad);
        }
        self.player.cease_fire.insert(owner, enabled);
        Ok(())
    }

    fn set_player_matched_speed(&mut self, owner: SquadLifetime,
        speed: Option<f32>,
    ) -> Result<(), GameCommandError> {
        if !self.player.squads.get(&owner)
            .is_some_and(|permission|
                permission.user_controlled && permission.can_accept_input)
            || speed.is_some_and(|s| !s.is_finite() || s < 0.0) {
            return Err(GameCommandError::InvalidSquad);
        }
        match speed {
            Some(speed) => { self.player.matching_speeds.insert(owner, speed); }
            None => { self.player.matching_speeds.remove(&owner); }
        }
        Ok(())
    }

    fn set_player_chasing(&mut self, owner: SquadLifetime, chasing: bool)
        -> Result<(), GameCommandError> {
        if !self.player.squads.get(&owner)
            .is_some_and(|permission| permission.user_controlled &&
                permission.can_accept_input) {
            return Err(GameCommandError::InvalidSquad);
        }
        if chasing { self.player.chasing.insert(owner); }
        else { self.player.chasing.remove(&owner); }
        Ok(())
    }

    fn set_player_locked_on(&mut self, owner: SquadLifetime, locked: bool)
        -> Result<(), GameCommandError> {
        let permission = self.player.squads.get_mut(&owner)
            .ok_or(GameCommandError::InvalidSquad)?;
        if !permission.user_controlled || !permission.can_accept_input {
            return Err(GameCommandError::InvalidSquad);
        }
        permission.locked_on = locked;
        Ok(())
    }

    fn set_player_shooting_strategy(&mut self, owner: SquadLifetime,
        shooting: bees_hivemind::protocol::StrategyIdentity,
    ) -> Result<(), GameCommandError> {
        bees_gameplay::commands::NativeCommandSelection::validate_shooting(shooting)?;
        self.player.commands.get_mut(&owner)
            .ok_or(GameCommandError::MissingCombatSnapshot)?.shooting = shooting;
        Ok(())
    }

    fn player_command_inputs(&self, owner: SquadLifetime)
        -> Result<bees_gameplay::player_inputs::NativePlayerCommandInputs, GameCommandError>
    {
        self.player.commands.get(&owner).copied()
            .ok_or(GameCommandError::MissingCombatSnapshot)
    }

    fn player_formation_options(&self) -> SquadFormationOptions {
        SquadFormationOptions { map_bounds: self.bounds,
            has_obstacles: self.player.has_obstacles, can_override_bounds: false }
    }

    fn cancel_player_scripted_queue(&mut self, owner: SquadLifetime)
        -> Result<(), GameCommandError>
    {
        // Unity FinalizeUserCommand clears all pending scripted commands
        // before replacing the current command. The shared event director
        // handles finalizing the current scripted command exactly once.
        let before = self.scripted_queues.len();
        self.scripted_queues.retain(|queue| queue.owner != owner);
        self.scripted_counts.queues_cancelled +=
            (before - self.scripted_queues.len()) as u64;
        Ok(())
    }
}

impl NativeLevelObservationContext for NativePolicyOnlyMetadata {
    type Error = PolicyOnlyMetadataError;

    fn group_agent_for_ship(&self, ship_id: i64)
        -> Result<Option<u64>, Self::Error>
    {
        Ok(self.agents.get(&ship_id)
            .ok_or(PolicyOnlyMetadataError::MissingAgent(ship_id))?
            .source_group_agent_id)
    }

    fn route_for_agent(&self, ship_id: i64)
        -> Result<NativePolicyRoute, Self::Error>
    {
        Ok(self.agents.get(&ship_id)
            .ok_or(PolicyOnlyMetadataError::MissingAgent(ship_id))?.route)
    }

    fn inputs_for_agent<'a>(
        &'a self, _world: &'a CombatWorld, ship_id: i64,
    ) -> Result<NativeLevelPerceptionInputs<'a>, Self::Error> {
        let agent = self.agents.get(&ship_id)
            .ok_or(PolicyOnlyMetadataError::MissingAgent(ship_id))?;
        // The live physical owner, not a stale metadata slot, determines
        // the current policy roster and side. A removed/rebound agent must
        // never receive a trajectory under its previous imported identity.
        let real_ship = _world.ship(ship_id)
            .ok_or(PolicyOnlyMetadataError::InvalidSource)?;
        if real_ship.side != agent.side ||
            real_ship.vitals.is_dead() ||
            _world.policy_frame(ship_id).is_none()
        {
            return Err(PolicyOnlyMetadataError::InvalidSource);
        }
        let side = self.sides.get(&agent.side)
            .ok_or(PolicyOnlyMetadataError::MissingSide(agent.side))?;
        let mut capability = agent.capability;
        let parent_carrier_id = _world.policy_striker_carrier(ship_id)
            .or_else(|| _world.drone_carrier(ship_id))
            .unwrap_or(agent.parent_carrier_id);
        capability.has_living_parent_carrier = parent_carrier_id.is_some();
        capability.ship_mined_tsv = self.fleet.get(ship_id)
            .ok_or(PolicyOnlyMetadataError::InvalidSource)?
            .minerals_mined_this_level;
        Ok(NativeLevelPerceptionInputs {
            level_bounds: self.bounds,
            controller: agent.controller,
            capability,
            parent_carrier_id,
            own_weapons: &agent.own_weapons,
            exploration_last_seen: Some(side.exploration.last_seen()),
        })
    }

    fn authored_ship_perception(
        &self, ship_id: i64,
    ) -> Option<AuthoredShipPerception> {
        let mut ship = *self.authored_ships.get(&ship_id)?;
        ship.minerals_mined_this_level =
            self.fleet.get(ship_id)?.minerals_mined_this_level;
        Some(ship)
    }
}
