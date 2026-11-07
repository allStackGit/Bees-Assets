//! Source-authored metadata for Levels driven solely by a real v26 policy.
//! The SAME native CombatWorld performs physical gameplay; this only
//! supplies absent Unity ship/episode/observation values. No fake agent,
//! placeholder observations or inferred weapon statistics are allowed.
//!
//! This mode intentionally has NO HiveMind command activation. If an
//! operator sends a strategy to it, the Level fails closed instead of
//! pretending an invented order is valid. Full game Levels supply a
//! richer metadata implementation of the same shared traits.

use std::collections::HashMap;
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
};
use bees_gameplay::perception_world::NativeLevelPerceptionInputs;
use bees_gameplay::squads::NativeSquadRoster;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::Aabb;
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, ControlledShipPerception,
};
use bees_sim::combat_world::{CombatWorld, CombatEvent};
use bees_sim::navigation_runtime::NavigationRuntime;
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
    pub effective_range: i32,
    pub trigger_range: i32,
    pub registered: bool,
    pub enabled: bool,
}

/// For policy-only native matches. The owner provides the real
/// NavigationRuntime (the same object used by gameplay pathfinding),
/// complete ship source observations, team environments and FleetShip
/// inventory. Do not use for command-capable HiveMind game Levels.
pub struct NativePolicyOnlyMetadata {
    pub bounds: Aabb,
    pub navigation: NavigationRuntime,
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
}

impl NativePolicyOnlyMetadata {
    pub fn new(
        bounds: Aabb,
        navigation: NavigationRuntime,
        fleet: NativeFleetInventory,
        ship_catalog: Arc<ShipAssetCatalog>,
        ships: impl IntoIterator<Item=(i64, AuthoredShipPerception)>,
        vision: impl IntoIterator<Item=(i64, NativeSourceVision)>,
        source_static_discovery_values: HashMap<i64, i32>,
        agents: impl IntoIterator<Item=AuthoredPolicyAgent>,
        sides: impl IntoIterator<Item=AuthoredPolicySide>,
        beacon_templates: HashMap<i64, Arc<NativeBeaconTemplate>>,
    ) -> Result<Self, PolicyOnlyMetadataError> {
        if !bounds.min.is_finite() || !bounds.max.is_finite() ||
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
            if id <= 0 || source.effective_range < 0 ||
                source.trigger_range < 0 ||
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
        Ok(Self {
            bounds, navigation, fleet, ship_catalog,
            authored_ships, effective_vision,
            source_static_discovery_values,
            explorers: Vec::new(),
            agents: agent_map, sides: side_map, beacon_templates,
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
    fn ship_asset_catalog(&self) -> Option<&ShipAssetCatalog> {
        Some(&self.ship_catalog)
    }

    fn navigation(&self) -> &NavigationRuntime {
        &self.navigation
    }

    fn initialize_navigation_from_world(
        &mut self, combat: &CombatWorld,
    ) -> Result<(), bees_sim::navigation::NavigationError> {
        self.navigation.refresh_static(combat.static_navigation_obstacles())?;
        Ok(())
    }
}

impl NativeLevelActivationContext for NativePolicyOnlyMetadata {
    fn strategy_inputs(
        &self, _owner: SquadLifetime, _enemy: Option<SquadLifetime>,
    ) -> Result<NativeLevelStrategyInputs, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }

    fn stored_formation_offset(
        &self, _ship_id: i64,
    ) -> Option<bees_sim::geometry::Vec2> {
        None
    }

    fn current_path_clearance(&self, _ship_id: i64) -> Option<i32> {
        None
    }

    fn collect_bomb_activation(
        &self, _world: &CombatWorld, _roster: &NativeSquadRoster,
        _owner: SquadLifetime, _enemy: SquadLifetime,
        _weapons: &mut Vec<NativeBombWeapon>,
        _enemies: &mut Vec<NativeEnemyTargetStats>,
    ) -> Result<bool, GameCommandError> {
        Err(GameCommandError::MissingCombatSnapshot)
    }
}

impl NativeLevelObservationContext for NativePolicyOnlyMetadata {
    type Error = PolicyOnlyMetadataError;

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
