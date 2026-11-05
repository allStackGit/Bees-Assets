//! Read-only projection from authoritative CombatWorld ship state into v26
//! policy observations. A physical ship's pose, movement, health, team,
//! type, fleet identity and alive status come ONLY from the real combat world.
//!
//! Unity's RlCombatPerception also depends on fields the native combat
//! simulation has not yet modeled: sprite LongestSide, effective vision,
//! authored weapon stats, fleet MineralsMinedThisLevel, specials and allied
//! communications. These MUST be supplied by the Level/episode adapter.
//! Never fill them from defaults, hull guesses or a training-only simulator.

use crate::collision::Aabb;
use crate::combat_world::{CombatShip, CombatWorld};
use crate::episode_ship_identity::{EpisodeShipIdentities, ShipIdentityError};
use crate::geometry::Vec2;
use crate::local_navigation_observation::{LocalNavigationError, LocalNavigationOccupancy};
use crate::level_discovery::LevelDiscovery;
use crate::obstacle::ObstacleKind;
use crate::perception::{SelfSource, ship_type_scalar};
use crate::team_exploration::{Explorer, ExplorationError, TeamExplorationGrid};
use crate::perception_entities::{ObservedShip, ObservedWeapon};
use crate::physics::ShipBody;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatPerceptionError {
    MissingShip(i64),
    InactiveShip(i64),
    InvalidAuthoredMetadata,
    InvalidLevelBounds,
    MismatchedWorldStorage,
    Identity(ShipIdentityError),
    CountOverflow,
    InvalidObstacleStorage,
    MissingNavigationBounds(i64),
    MissingEffectiveVision(i64),
    MissingShipMetadata(i64),
    Navigation(LocalNavigationError),
    Exploration(ExplorationError),
}

impl From<LocalNavigationError> for CombatPerceptionError {
    fn from(value: LocalNavigationError) -> Self { Self::Navigation(value) }
}
impl From<ExplorationError> for CombatPerceptionError {
    fn from(value: ExplorationError) -> Self { Self::Exploration(value) }
}

impl From<ShipIdentityError> for CombatPerceptionError {
    fn from(value: ShipIdentityError) -> Self { Self::Identity(value) }
}

/// Values owned by the actual authored ship/fleet/weapon control layer that
/// do NOT currently exist in CombatWorld. In particular, mined value is
/// FleetShip.MineralsMinedThisLevel and must not be guessed from ShipVitals'
/// separately tracked current cargo.
#[derive(Clone, Copy, Debug)]
pub struct AuthoredShipPerception {
    pub longest_side: f32,
    pub max_weapon_range: f32,
    pub firepower: f32,
    pub is_bomber: bool,
    pub is_carrier_ship: bool,
    pub minerals_mined_this_level: i32,
    pub observed_weapons: [Option<ObservedWeapon>; 5],
    pub allied_communication: Option<[f32; 4]>,
}

impl AuthoredShipPerception {
    pub fn valid(&self) -> bool {
        self.longest_side.is_finite() && self.longest_side >= 0.0 &&
        self.max_weapon_range.is_finite() && self.max_weapon_range >= 0.0 &&
        self.firepower.is_finite() && self.firepower >= 0.0 &&
        self.minerals_mined_this_level >= 0 &&
        self.allied_communication.map_or(true, |values|
            values.iter().all(|v| v.is_finite())) &&
        self.observed_weapons.iter().flatten().all(|weapon| {
            weapon.weapon_type < 10 &&
            [weapon.range, weapon.power, weapon.rate_of_fire]
                .iter().all(|v| v.is_finite() && *v >= 0.0)
        })
    }
}

/// Additional authoritative data for the observing ship, which must come
/// from the actual ship controller and episode configuration. The policy
/// movement-enabled flag is independent of ship's base physical mobility.
#[derive(Clone, Copy, Debug)]
pub struct ControlledShipPerception {
    pub effective_vision_range: f32,
    pub movement_disabled_for_team: bool,
    pub has_weapons: bool,
    pub has_turrets: bool,
    pub has_special_action: bool,
    pub special_ready: bool,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LiveShipCounts {
    /// Includes the observing ship if alive, just like GetShips(side).
    pub same_side: u32,
    pub opponents: u32,
}

fn live_ship(
    world: &CombatWorld,
    id: i64,
) -> Result<(&CombatShip, &ShipBody), CombatPerceptionError> {
    let ship = world.ship(id).ok_or(CombatPerceptionError::MissingShip(id))?;
    let body = world.ship_body(id).ok_or(CombatPerceptionError::MismatchedWorldStorage)?;
    if ship.entity_id != body.id {
        return Err(CombatPerceptionError::MismatchedWorldStorage);
    }
    if !body.active || ship.vitals.is_dead() {
        return Err(CombatPerceptionError::InactiveShip(id));
    }
    if ship_type_scalar(ship.ship_type).is_err() ||
        !body.motion.position.is_finite() ||
        !body.motion.rotation_degrees.is_finite() ||
        !body.motion.body_velocity.is_finite() ||
        !body.motion.base_speed.is_finite() ||
        !body.motion.current_speed.is_finite() ||
        !body.motion.rotation_speed_degrees_per_second.is_finite() {
        return Err(CombatPerceptionError::InvalidAuthoredMetadata);
    }
    Ok((ship, body))
}

/// Use the same live ship inventory as game simulation, rather than a
/// duplicate training roster. Called once per side/decision boundary.
pub fn live_ship_counts(
    world: &CombatWorld,
    observer_side: i32,
) -> Result<LiveShipCounts, CombatPerceptionError> {
    if observer_side <= 0 || world.ships().len() != world.physics().ships().len() {
        return Err(CombatPerceptionError::MismatchedWorldStorage);
    }
    let mut counts = LiveShipCounts::default();
    for (ship, body) in world.ships().iter().zip(world.physics().ships()) {
        if ship.entity_id != body.id {
            return Err(CombatPerceptionError::MismatchedWorldStorage);
        }
        if !body.active || ship.vitals.is_dead() { continue; }
        let count = if ship.side == observer_side {
            &mut counts.same_side
        } else {
            &mut counts.opponents
        };
        *count = count.checked_add(1)
            .ok_or(CombatPerceptionError::CountOverflow)?;
    }
    Ok(counts)
}

/// Project one actual live world ship into an entity observation. Call this
/// for the observer FIRST, then parents, allies and ever-discovered enemies
/// in Unity source order, to preserve episode identity assignment order.
pub fn observed_ship_from_world(
    world: &CombatWorld,
    entity_id: i64,
    metadata: AuthoredShipPerception,
    identities: &mut EpisodeShipIdentities,
) -> Result<ObservedShip, CombatPerceptionError> {
    let (ship, body) = live_ship(world, entity_id)?;
    if !metadata.valid() {
        return Err(CombatPerceptionError::InvalidAuthoredMetadata);
    }
    let identity = identities.get_or_assign(entity_id)?;
    Ok(ObservedShip {
        entity_id,
        fleet_ship_id: world.ship_fleet_identity(entity_id),
        side: ship.side,
        ship_type: ship.ship_type,
        identity,
        position: body.motion.position,
        rotation_degrees: body.motion.rotation_degrees,
        health: ship.vitals.health(),
        max_health: ship.vitals.max_health(),
        speed: body.motion.base_speed,
        current_speed: body.motion.current_speed,
        linear_velocity: body.motion.body_velocity,
        longest_side: metadata.longest_side,
        max_range: metadata.max_weapon_range,
        firepower: metadata.firepower,
        is_mobile: body.motion.mobile,
        is_bomber: metadata.is_bomber,
        is_alive: true,
        mined_tsv: metadata.minerals_mined_this_level,
        weapons: metadata.observed_weapons,
        communication: metadata.allied_communication,
    })
}

/// Project authoritative physical state into Unity's 25 self features.
/// Inventory counts are sampled from the same world as the ship itself.
/// Source-specific role/capability and vision fields remain required inputs.
pub fn self_source_from_world(
    world: &CombatWorld,
    entity_id: i64,
    level_bounds: Aabb,
    metadata: AuthoredShipPerception,
    controller: ControlledShipPerception,
    identities: &mut EpisodeShipIdentities,
) -> Result<SelfSource, CombatPerceptionError> {
    let (ship, body) = live_ship(world, entity_id)?;
    if !metadata.valid() ||
        !controller.effective_vision_range.is_finite() ||
        controller.effective_vision_range < 0.0 ||
        (controller.has_turrets && !controller.has_weapons) {
        return Err(CombatPerceptionError::InvalidAuthoredMetadata);
    }
    if !level_bounds.min.is_finite() || !level_bounds.max.is_finite() ||
        level_bounds.max.x <= level_bounds.min.x ||
        level_bounds.max.y <= level_bounds.min.y {
        return Err(CombatPerceptionError::InvalidLevelBounds);
    }
    let counts = live_ship_counts(world, ship.side)?;
    let identity = identities.get_or_assign(entity_id)?;
    Ok(SelfSource {
        identity,
        ship_type: ship.ship_type,
        position: body.motion.position,
        level_bounds,
        rotation_degrees: body.motion.rotation_degrees,
        health: ship.vitals.health(),
        max_health: ship.vitals.max_health(),
        speed: body.motion.base_speed,
        current_speed: body.motion.current_speed,
        linear_velocity: body.motion.body_velocity,
        rotation_speed: body.motion.rotation_speed_degrees_per_second,
        longest_side: metadata.longest_side,
        effective_vision_range: controller.effective_vision_range,
        max_range: metadata.max_weapon_range,
        firepower: metadata.firepower,
        can_move: body.motion.mobile && !controller.movement_disabled_for_team,
        is_bomber: metadata.is_bomber,
        is_carrier: metadata.is_carrier_ship,
        has_weapons: controller.has_weapons,
        has_turrets: controller.has_turrets,
        has_special: controller.has_special_action,
        special_ready: controller.special_ready,
        live_allies_including_self: counts.same_side,
        live_enemies: counts.opponents,
    })
}

/// Extract a ship's true physical position without placing observation-only
/// stats into CombatWorld's authoritative physics/gameplay storage.
pub fn live_ship_position(
    world: &CombatWorld,
    entity_id: i64,
) -> Result<Vec2, CombatPerceptionError> {
    let (_, body) = live_ship(world, entity_id)?;
    Ok(body.motion.position)
}

/// Directly rasterize the actual Level's active, living, DISCOVERED
/// static collider bounds. The visibility predicate must read the faction's
/// authoritative HiveMind discovery cache. A physical obstacle that is not
/// discovered must not leak into RL observations.
pub fn navigation_from_world(
    world: &CombatWorld,
    bounds: Aabb,
    observer: Vec2,
    occupancy: &mut LocalNavigationOccupancy,
    mut is_visible_to_hivemind: impl FnMut(i64) -> bool,
) -> Result<usize, CombatPerceptionError> {
    if world.obstacles().len() != world.physics().obstacles().len() {
        return Err(CombatPerceptionError::InvalidObstacleStorage);
    }
    occupancy.reset(bounds, observer)?;
    let mut included = 0;
    for (obstacle, body) in world.obstacles().iter().zip(world.physics().obstacles()) {
        if obstacle.entity_id != body.id {
            return Err(CombatPerceptionError::InvalidObstacleStorage);
        }
        if obstacle.kind != ObstacleKind::StaticObstacle ||
            !body.active || !obstacle.live() ||
            !is_visible_to_hivemind(obstacle.entity_id) {
            continue;
        }
        let area = body.navigation_bounds()
            .ok_or(CombatPerceptionError::MissingNavigationBounds(obstacle.entity_id))?;
        // World/level coordinates are already identical for the native
        // gameplay arena. Unity's source takes the clearance collider's
        // world AABB and transforms it into Level-local coordinates here.
        let center = Vec2::new(
            (area.min.x + area.max.x) * 0.5,
            (area.min.y + area.max.y) * 0.5,
        );
        let extents = Vec2::new(
            (area.max.x - area.min.x).abs() * 0.5,
            (area.max.y - area.min.y).abs() * 0.5,
        );
        occupancy.mark_static_aabb(center, extents)?;
        included += 1;
    }
    Ok(included)
}

/// Prepare the actual alive allied ship inventory for the existing
/// TeamExplorationGrid::update. The caller supplies the effective vision
/// range from the SAME authored HiveMindVision rules used for player view.
/// Scratch is caller-owned and reused across steps; no per-ship allocation.
pub fn update_exploration_from_world(
    world: &CombatWorld,
    side: i32,
    bounds: Aabb,
    episode_progress: f32,
    grid: &mut TeamExplorationGrid,
    explorers: &mut Vec<Explorer>,
    mut effective_vision: impl FnMut(i64) -> Option<i32>,
) -> Result<usize, CombatPerceptionError> {
    if side <= 0 || world.ships().len() != world.physics().ships().len() {
        return Err(CombatPerceptionError::MismatchedWorldStorage);
    }
    explorers.clear();
    for (ship, body) in world.ships().iter().zip(world.physics().ships()) {
        if ship.entity_id != body.id {
            return Err(CombatPerceptionError::MismatchedWorldStorage);
        }
        if ship.side != side || !body.active || ship.vitals.is_dead() {
            continue;
        }
        let range = effective_vision(ship.entity_id)
            .ok_or(CombatPerceptionError::MissingEffectiveVision(ship.entity_id))?;
        if range < 0 {
            return Err(CombatPerceptionError::InvalidAuthoredMetadata);
        }
        explorers.push(Explorer {
            position: body.motion.position,
            effective_vision_range: range,
            alive: true,
        });
    }
    grid.update(bounds, explorers, episode_progress)?;
    Ok(explorers.len())
}

/// Read-only Level ship candidate staging. Sorting real live ships BEFORE
/// assigning episode identity scalars preserves Unity's source order:
/// squared distance, ship type, fleet ID, runtime ID. The buffer retains
/// capacity across policy decisions; no per-ship allocation is needed.
#[derive(Default)]
pub struct LiveShipCandidateCollector {
    ordered_ids: Vec<i64>,
}

impl LiveShipCandidateCollector {
    pub fn new() -> Self { Self::default() }

    fn sort_and_project(
        &mut self,
        world: &CombatWorld,
        observer_position: Vec2,
        identities: &mut EpisodeShipIdentities,
        mut metadata_for: impl FnMut(i64) -> Option<AuthoredShipPerception>,
        output: &mut Vec<ObservedShip>,
    ) -> Result<usize, CombatPerceptionError> {
        self.ordered_ids.sort_unstable_by(|left_id, right_id| {
            let left = world.ship(*left_id).expect("validated live ship");
            let right = world.ship(*right_id).expect("validated live ship");
            let left_body = world.ship_body(*left_id).expect("validated ship body");
            let right_body = world.ship_body(*right_id).expect("validated ship body");
            (left_body.motion.position - observer_position).length_squared()
                .total_cmp(&(right_body.motion.position - observer_position).length_squared())
                .then_with(|| left.ship_type.cmp(&right.ship_type))
                .then_with(|| world.ship_fleet_identity(*left_id).unwrap_or(i64::MAX)
                    .cmp(&world.ship_fleet_identity(*right_id).unwrap_or(i64::MAX)))
                .then_with(|| left_id.cmp(right_id))
        });

        output.clear();
        // Unity writes at most 64 allies and 64 enemies. Assigning identities
        // for farther unused candidates would change first-use ordinals.
        for &id in self.ordered_ids.iter().take(64) {
            let metadata = metadata_for(id)
                .ok_or(CombatPerceptionError::MissingShipMetadata(id))?;
            output.push(observed_ship_from_world(world, id, metadata, identities)?);
        }
        Ok(output.len())
    }

    /// Source: GameState.GetShips(side), minus the observer and dead ships.
    /// The writer's zero padding is the only representation of unused slots.
    pub fn collect_allies(
        &mut self,
        world: &CombatWorld,
        observer_id: i64,
        identities: &mut EpisodeShipIdentities,
        metadata_for: impl FnMut(i64) -> Option<AuthoredShipPerception>,
        output: &mut Vec<ObservedShip>,
    ) -> Result<usize, CombatPerceptionError> {
        let (observer, observer_body) = live_ship(world, observer_id)?;
        let origin = observer_body.motion.position;
        self.ordered_ids.clear();
        for candidate in world.ships() {
            if candidate.side != observer.side || candidate.entity_id == observer_id {
                continue;
            }
            match live_ship(world, candidate.entity_id) {
                Ok(_) => self.ordered_ids.push(candidate.entity_id),
                Err(CombatPerceptionError::InactiveShip(_)) => continue,
                Err(error) => return Err(error),
            }
        }
        self.sort_and_project(world, origin, identities, metadata_for, output)
    }

    /// Source: GameState.VisionCache[observer.Side - 1]. Previously learned
    /// enemy ships remain eligible even when outside current sight.
    /// Dead/removed entries are ignored as Unity CollectVisibleEnemies does.
    pub fn collect_learned_enemies(
        &mut self,
        world: &CombatWorld,
        discoveries: &LevelDiscovery,
        observer_id: i64,
        identities: &mut EpisodeShipIdentities,
        metadata_for: impl FnMut(i64) -> Option<AuthoredShipPerception>,
        output: &mut Vec<ObservedShip>,
    ) -> Result<usize, CombatPerceptionError> {
        let (observer, observer_body) = live_ship(world, observer_id)?;
        let origin = observer_body.motion.position;
        self.ordered_ids.clear();
        if let Some(known) = discoveries.ship_ids(observer.side) {
            for &id in known {
                let Some(candidate) = world.ship(id) else {
                    continue; // a target retired before cache cleanup
                };
                if candidate.side == observer.side {
                    continue;
                }
                match live_ship(world, id) {
                    Ok(_) => self.ordered_ids.push(id),
                    Err(CombatPerceptionError::InactiveShip(_)) => continue,
                    Err(error) => return Err(error),
                }
            }
        }
        self.sort_and_project(world, origin, identities, metadata_for, output)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    use crate::combat::ShipVitals;
    use crate::movement::ShipMotion;
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::ship_assets::ShipAssetCatalog;

    #[test]
    fn projection_uses_live_world_vitals_motion_and_fleet_identity() {
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let bounds = Aabb {
            min: Vec2::new(-50.0, -50.0),
            max: Vec2::new(50.0, 50.0),
        };
        let mut world = CombatWorld::new(bounds, 8.0, projectiles).unwrap();
        let mut motion = ShipMotion::new(
            Vec2::new(4.0, -2.0), 90.0, 120.0, 10.0, 3.0,
        );
        motion.body_velocity = Vec2::new(3.0, 0.0);
        world.spawn_ship(
            hulls.by_type(21).unwrap(), 1, 1,
            ShipVitals::new(80, 100, 1000, 30).unwrap(),
            motion,
        ).unwrap();
        world.set_ship_fleet_identity(1, 87).unwrap();
        world.spawn_ship(
            hulls.by_type(13).unwrap(), 2, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(15.0, 0.0), 0.0,
                100.0, 8.0, 3.0),
        ).unwrap();
        let metadata = AuthoredShipPerception {
            longest_side: 7.0, max_weapon_range: 80.0,
            firepower: 45.0, is_bomber: false,
            is_carrier_ship: false,
            minerals_mined_this_level: 75,
            observed_weapons: [None; 5],
            allied_communication: None,
        };
        let mut identities = EpisodeShipIdentities::new(5, 100).unwrap();
        let own = self_source_from_world(
            &world, 1, bounds, metadata,
            ControlledShipPerception {
                effective_vision_range: 50.0,
                movement_disabled_for_team: false,
                has_weapons: false, has_turrets: false,
                has_special_action: false, special_ready: false,
            },
            &mut identities,
        ).unwrap();
        let observed = observed_ship_from_world(
            &world, 1, metadata, &mut identities,
        ).unwrap();
        assert_eq!(own.identity, observed.identity);
        assert_eq!(own.health, 80);
        assert_eq!(own.max_health, 100);
        assert_eq!(own.rotation_degrees, 90.0);
        assert_eq!(own.position, Vec2::new(4.0, -2.0));
        assert_eq!(own.current_speed, 10.0);
        assert_eq!(own.live_allies_including_self, 1);
        assert_eq!(own.live_enemies, 1);
        assert_eq!(observed.fleet_ship_id, Some(87));
        assert_eq!(observed.mined_tsv, 75);
        assert_eq!(observed.linear_velocity, Vec2::new(3.0, 0.0));
        assert_eq!(live_ship_position(&world, 1).unwrap(),
            own.position);
    }
}
