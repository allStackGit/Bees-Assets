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
use bees_sim::combat_world::CombatWorld;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::perception::{CapabilitySource, EXPLORATION_LEN};
use bees_sim::perception_environment::MiningAsteroid;
use bees_sim::perception_weapons::SelfWeapon;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_gameplay::inference::NativePolicyRoute;
use crate::fleet_inventory::NativeFleetInventory;

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

/// Only genuinely team-shared discovery/observation inputs live here.
/// The 21x21 navigation grid is NOT side-wide: the gameplay assembler
/// recomputes it around each ship using shared cached static bounds.
/// Dynamic scenes must refresh mining/exploration inputs at the Level's
/// authoritative discovery boundary.
pub struct AuthoredPolicySide {
    pub side: i32,
    pub exploration_last_seen: Option<[f32; EXPLORATION_LEN]>,
    pub visible_mining: Vec<MiningAsteroid>,
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
    agents: HashMap<i64, AuthoredPolicyAgent>,
    sides: HashMap<i32, AuthoredPolicySide>,
}

impl NativePolicyOnlyMetadata {
    pub fn new(
        bounds: Aabb,
        navigation: NavigationRuntime,
        fleet: NativeFleetInventory,
        ship_catalog: Arc<ShipAssetCatalog>,
        ships: impl IntoIterator<Item=(i64, AuthoredShipPerception)>,
        agents: impl IntoIterator<Item=AuthoredPolicyAgent>,
        sides: impl IntoIterator<Item=AuthoredPolicySide>,
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
        let mut side_map = HashMap::new();
        for side in sides {
            if side.side <= 0 ||
                side.exploration_last_seen.as_ref().is_some_and(
                    |values| values.iter().any(|value| !value.is_finite()),
                ) {
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
        Ok(Self {
            bounds, navigation, fleet, ship_catalog,
            authored_ships, agents: agent_map, sides: side_map,
        })
    }

    /// A rendered Level that changes dynamic navigation/mining/exploration
    /// values updates this exact owned side state between windows.
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
}

impl NativeLevelCommandContext for NativePolicyOnlyMetadata {
    fn ship_asset_catalog(&self) -> Option<&ShipAssetCatalog> {
        Some(&self.ship_catalog)
    }

    fn navigation(&self) -> &NavigationRuntime {
        &self.navigation
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
        capability.ship_mined_tsv = self.fleet.get(ship_id)
            .ok_or(PolicyOnlyMetadataError::InvalidSource)?
            .minerals_mined_this_level;
        Ok(NativeLevelPerceptionInputs {
            level_bounds: self.bounds,
            controller: agent.controller,
            capability,
            parent_carrier_id: agent.parent_carrier_id,
            own_weapons: &agent.own_weapons,
            visible_mining: &side.visible_mining,
            exploration_last_seen: side.exploration_last_seen.as_ref(),
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
