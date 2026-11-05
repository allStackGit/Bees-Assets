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
use crate::obstacle::ObstacleKind;
use crate::perception::{SelfSource, ship_type_scalar};
use crate::perception_environment::{CollisionAsteroid, MapObject as MapObjectObservation};
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
    MissingAsteroidVitals(i64),
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
    // Bound turret mounts belong to live gameplay. Their measured weapon
    // stats override stale external snapshots, while unrelated non-turret
    // slots remain sourced by the authored Level adapter.
    let mut observed_weapons = metadata.observed_weapons;
    if let Some(registered) = world.policy_observed_weapons(entity_id) {
        for slot in 0..observed_weapons.len() {
            if let Some(weapon) = registered[slot] {
                observed_weapons[slot] = Some(weapon);
            }
        }
    }
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
        weapons: observed_weapons,
        // A bound RL ship's actual private communication lives on the
        // canonical world controller. External authored controllers continue
        // to provide the fallback when no native policy is bound.
        communication: world.policy_communication(entity_id)
            .or(metadata.allied_communication),
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

/// One sortable record computed once at collection time. Comparing these
/// fields avoids four repeated CombatWorld hash lookups for every comparison
/// made by the nearest-first sort in large battles.
#[derive(Clone, Copy)]
struct ShipCandidateKey {
    id: i64,
    distance_squared: f32,
    ship_type: u8,
    fleet_id: i64,
}

/// Gather only discovered, alive collision asteroids from the actual
/// native obstacle registry. Unity's RlCombatPerception uses the active
/// clearance-mapping collider AABB (or primary collider), the body's
/// level-local velocity, and Health / EffectiveMaxHealth.
///
/// This does no candidate sorting: EnvironmentSlotEncoder already owns
/// reusable scratch and sorts by distance to the collider bounds, exactly
/// as Unity does. Filtering here makes it impossible for undiscovered
/// obstacles to leak into observations.
pub fn collect_learned_collision_asteroids(
    world: &CombatWorld,
    observing_side: i32,
    output: &mut Vec<CollisionAsteroid>,
) -> Result<usize, CombatPerceptionError> {
    if observing_side <= 0 ||
        world.obstacles().len() != world.physics().obstacles().len() {
        return Err(CombatPerceptionError::InvalidObstacleStorage);
    }
    output.clear();
    for (obstacle, body) in world.obstacles().iter()
        .zip(world.physics().obstacles()) {
        if obstacle.entity_id != body.id {
            output.clear();
            return Err(CombatPerceptionError::InvalidObstacleStorage);
        }
        if obstacle.kind != ObstacleKind::CollisionAsteroid ||
            !body.active || !obstacle.live() ||
            !world.discovery().has(
                observing_side,
                crate::level_discovery::DiscoveryKind::Obstacle,
                obstacle.entity_id,
            ) {
            continue;
        }
        let Some(vitals) = obstacle.vitals else {
            output.clear();
            return Err(CombatPerceptionError::MissingAsteroidVitals(
                obstacle.entity_id,
            ));
        };
        if vitals.effective_max_health <= 0 {
            output.clear();
            return Err(CombatPerceptionError::MissingAsteroidVitals(
                obstacle.entity_id,
            ));
        }
        let Some(bounds) = body.navigation_bounds() else {
            output.clear();
            return Err(CombatPerceptionError::MissingNavigationBounds(
                obstacle.entity_id,
            ));
        };
        let position = Vec2::new(
            (bounds.min.x + bounds.max.x) * 0.5,
            (bounds.min.y + bounds.max.y) * 0.5,
        );
        let half_extents = Vec2::new(
            (bounds.max.x - bounds.min.x).abs() * 0.5,
            (bounds.max.y - bounds.min.y).abs() * 0.5,
        );
        if !position.is_finite() || !half_extents.is_finite() ||
            !body.linear_velocity.is_finite() ||
            !body.pose.rotation_degrees.is_finite() {
            output.clear();
            return Err(CombatPerceptionError::InvalidAuthoredMetadata);
        }
        output.push(CollisionAsteroid {
            id: obstacle.entity_id,
            position,
            half_extents,
            rotation_degrees: body.pose.rotation_degrees,
            level_velocity: body.linear_velocity,
            health_fraction: (vitals.health as f32 /
                vitals.effective_max_health as f32).clamp(0.0, 1.0),
            alive: true,
        });
    }
    Ok(output.len())
}

/// MapObject.Collider and MapObject.Health belong to the native gameplay
/// world, not to an observation-only list. Collect only living MapObjects
/// that Hive Mind discovery has actually learned for the observing side.
/// Fire Tanks cannot be registered until their real explosion is ported.
pub fn collect_learned_map_objects(
    world: &CombatWorld,
    observing_side: i32,
    output: &mut Vec<MapObjectObservation>,
) -> Result<usize, CombatPerceptionError> {
    if observing_side <= 0 ||
        world.obstacles().len() != world.physics().obstacles().len() {
        return Err(CombatPerceptionError::InvalidObstacleStorage);
    }
    output.clear();
    for (obstacle, body) in world.obstacles().iter()
        .zip(world.physics().obstacles()) {
        if obstacle.entity_id != body.id {
            output.clear();
            return Err(CombatPerceptionError::InvalidObstacleStorage);
        }
        if obstacle.kind != ObstacleKind::MapObject || !body.active ||
            !world.discovery().has(
                observing_side,
                crate::level_discovery::DiscoveryKind::MapObject,
                obstacle.entity_id,
            ) {
            continue;
        }
        let Some(object) = world.map_object(obstacle.entity_id) else {
            output.clear();
            return Err(CombatPerceptionError::InvalidObstacleStorage);
        };
        let Some(bounds) = body.primary_bounds() else {
            output.clear();
            return Err(CombatPerceptionError::MissingNavigationBounds(
                obstacle.entity_id,
            ));
        };
        let half_extents = Vec2::new(
            (bounds.max.x - bounds.min.x).abs() * 0.5,
            (bounds.max.y - bounds.min.y).abs() * 0.5,
        );
        if !body.pose.position.is_finite() || !half_extents.is_finite() {
            output.clear();
            return Err(CombatPerceptionError::InvalidAuthoredMetadata);
        }
        output.push(MapObjectObservation {
            id: obstacle.entity_id,
            kind: object.kind,
            position: body.pose.position,
            half_extents,
            health_fraction: object.health_fraction(),
            alive: object.alive(),
        });
    }
    Ok(output.len())
}

/// Read-only Level ship candidate staging. Sorting real live ships BEFORE
/// assigning episode identity scalars preserves Unity's source order:
/// squared distance, ship type, fleet ID, runtime ID. The buffer retains
/// capacity across policy decisions; no per-ship allocation is needed.
#[derive(Default)]
pub struct LiveShipCandidateCollector {
    candidates: Vec<ShipCandidateKey>,
}

impl LiveShipCandidateCollector {
    pub fn new() -> Self { Self::default() }

    fn add_candidate(
        &mut self,
        world: &CombatWorld,
        ship: &CombatShip,
        body: &ShipBody,
        origin: Vec2,
    ) -> Result<(), CombatPerceptionError> {
        if ship.entity_id != body.id {
            return Err(CombatPerceptionError::MismatchedWorldStorage);
        }
        let distance_squared = (body.motion.position - origin).length_squared();
        if !distance_squared.is_finite() {
            return Err(CombatPerceptionError::InvalidAuthoredMetadata);
        }
        self.candidates.push(ShipCandidateKey {
            id: ship.entity_id,
            distance_squared,
            ship_type: ship.ship_type,
            fleet_id: world.ship_fleet_identity(ship.entity_id)
                .unwrap_or(i64::MAX),
        });
        Ok(())
    }

    fn sort_and_project(
        &mut self,
        world: &CombatWorld,
        identities: &mut EpisodeShipIdentities,
        mut metadata_for: impl FnMut(i64) -> Option<AuthoredShipPerception>,
        output: &mut Vec<ObservedShip>,
    ) -> Result<usize, CombatPerceptionError> {
        self.candidates.sort_unstable_by(|left, right| {
            left.distance_squared.total_cmp(&right.distance_squared)
                .then_with(|| left.ship_type.cmp(&right.ship_type))
                .then_with(|| left.fleet_id.cmp(&right.fleet_id))
                .then_with(|| left.id.cmp(&right.id))
        });
        output.clear();
        // Unity writes at most 64 allies and 64 enemies. Assigning identities
        // for farther unused candidates would change first-use ordinals.
        for candidate in self.candidates.iter().take(64) {
            let id = candidate.id;
            let metadata = match metadata_for(id) {
                Some(value) => value,
                None => {
                    output.clear();
                    return Err(CombatPerceptionError::MissingShipMetadata(id));
                }
            };
            match observed_ship_from_world(world, id, metadata, identities) {
                Ok(ship) => output.push(ship),
                Err(error) => {
                    output.clear();
                    return Err(error);
                }
            }
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
        let side = observer.side;
        let origin = observer_body.motion.position;
        self.candidates.clear();
        if world.ships().len() != world.physics().ships().len() {
            return Err(CombatPerceptionError::MismatchedWorldStorage);
        }
        for (candidate, body) in world.ships().iter().zip(world.physics().ships()) {
            if candidate.entity_id != body.id {
                return Err(CombatPerceptionError::MismatchedWorldStorage);
            }
            if candidate.side != side || candidate.entity_id == observer_id ||
                !body.active || candidate.vitals.is_dead() {
                continue;
            }
            self.add_candidate(world, candidate, body, origin)?;
        }
        self.sort_and_project(world, identities, metadata_for, output)
    }

    /// Source: GameState.VisionCache[observer.Side - 1]. Previously learned
    /// enemy ships remain eligible even when outside current sight.
    /// Dead/removed entries are ignored as Unity CollectVisibleEnemies does.
    pub fn collect_learned_enemies(
        &mut self,
        world: &CombatWorld,
        observer_id: i64,
        identities: &mut EpisodeShipIdentities,
        metadata_for: impl FnMut(i64) -> Option<AuthoredShipPerception>,
        output: &mut Vec<ObservedShip>,
    ) -> Result<usize, CombatPerceptionError> {
        let (observer, observer_body) = live_ship(world, observer_id)?;
        let side = observer.side;
        let origin = observer_body.motion.position;
        self.candidates.clear();
        if let Some(known) = world.discovery().ship_ids(side) {
            for &id in known {
                let Some(candidate) = world.ship(id) else {
                    continue; // a target retired before cache cleanup
                };
                if candidate.side == side {
                    continue;
                }
                match live_ship(world, id) {
                    Ok((ship, body)) => self.add_candidate(world, ship, body, origin)?,
                    Err(CombatPerceptionError::InactiveShip(_)) => continue,
                    Err(error) => return Err(error),
                }
            }
        }
        self.sort_and_project(world, identities, metadata_for, output)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    use crate::combat::ShipVitals;
    use crate::collision::Pose;
    use crate::movement::ShipMotion;
    use crate::obstacle::{CombatObstacle, ObstacleVitals};
    use crate::physics::{ObstacleBody, ShipShape};
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
        world.spawn_ship(
            hulls.by_type(21).unwrap(), 3, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(0.0, 0.0), 0.0,
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
        assert_eq!(own.live_enemies, 2);
        assert_eq!(observed.fleet_ship_id, Some(87));
        assert_eq!(observed.mined_tsv, 75);
        assert_eq!(observed.linear_velocity, Vec2::new(3.0, 0.0));
        assert_eq!(live_ship_position(&world, 1).unwrap(),
            own.position);

        assert!(!world.record_hivemind_ship_sighting(1, 2, false, false));
        assert!(!world.record_hivemind_ship_sighting(1, 2, true, true));
        assert!(world.record_hivemind_ship_sighting(1, 2, true, false));
        assert!(!world.record_hivemind_ship_sighting(1, 2, true, false));
        assert!(world.record_hivemind_ship_sighting(1, 3, true, false));
        let mut collector = LiveShipCandidateCollector::new();
        let mut known_enemies = Vec::new();
        let count = collector.collect_learned_enemies(
            &world, 1, &mut identities,
            |_| Some(metadata), &mut known_enemies,
        ).unwrap();
        assert_eq!(count, 2);
        assert_eq!(known_enemies[0].entity_id, 3);
        assert_eq!(known_enemies[1].entity_id, 2);
        assert_eq!(known_enemies[0].side, 2);

        let mut nav = LocalNavigationOccupancy::new();
        assert_eq!(navigation_from_world(
            &world, bounds, own.position, &mut nav,
            |id| world.discovery().has(1, crate::level_discovery::DiscoveryKind::Obstacle, id),
        ).unwrap(), 0);
        assert_eq!(nav.occupancy().len(), 21 * 21);

        let mut exploration = TeamExplorationGrid::new();
        let mut explorers = Vec::new();
        assert_eq!(update_exploration_from_world(
            &world, 1, bounds, 0.5,
            &mut exploration, &mut explorers,
            |_| Some(50),
        ).unwrap(), 1);
        assert!(exploration.last_seen().iter().any(|&seen| seen == 0.5));

        // A discovered native CollisionAsteroid exposes its actual
        // clearance/physics bounds, health and body velocity to perception.
        world.add_obstacle(
            CombatObstacle {
                entity_id: 4,
                kind: ObstacleKind::CollisionAsteroid,
                vitals: Some(ObstacleVitals::new(50, 100).unwrap()),
                size_class: 2,
            },
            ObstacleBody::new(
                4,
                Pose { position: Vec2::new(10.0, 5.0),
                    rotation_degrees: 0.0 },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(5.0, 2.0),
                },
                Vec2::new(-2.0, 1.0),
                0.0,
            ).unwrap(),
        ).unwrap();
        assert!(world.discovery_mut().record(
            1, crate::level_discovery::DiscoveryKind::Obstacle, 4,
        ).unwrap());
        let mut asteroids = Vec::new();
        assert_eq!(collect_learned_collision_asteroids(
            &world, 1, &mut asteroids,
        ).unwrap(), 1);
        assert_eq!(asteroids[0].id, 4);
        assert_eq!(asteroids[0].position, Vec2::new(10.0, 5.0));
        assert_eq!(asteroids[0].half_extents, Vec2::new(5.0, 2.0));
        assert_eq!(asteroids[0].level_velocity, Vec2::new(-2.0, 1.0));
        assert_eq!(asteroids[0].health_fraction, 0.5);
        assert!(world.kill_collision_asteroid(4, true).unwrap());
        assert_eq!(collect_learned_collision_asteroids(
            &world, 1, &mut asteroids,
        ).unwrap(), 0);
        assert!(!world.discovery().has(
            1, crate::level_discovery::DiscoveryKind::Obstacle, 4,
        ));
    }
}
