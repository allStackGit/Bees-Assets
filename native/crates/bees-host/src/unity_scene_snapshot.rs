//! Strict source-owned Unity physical scene snapshot importer.
//!
//! The Unity Editor captures active ships, physical obstacles and their real
//! source fields. This importer converts them to NativeAuthoredEntity and
//! NativeFleetInventory without guessing the remaining SquadLifetime,
//! episode policy frame, model weights, weapon mounts or command metadata.
//! Those must be supplied independently to NativeAuthoredLevel.
//!
//! This is INITIAL WORLD CONSTRUCTION, not a mid-episode recovery mechanism:
//! learned vision, missiles in flight, command timers, pending outcomes, PPO
//! trajectories and random stream position are not reconstructed here.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use bees_sim::asteroid_spawn::FullAsteroidPrototype;
use bees_sim::combat_world::{NativeFirePattern, NativeTurretSpec};
use bees_sim::control::PolicyFrame;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::combat::{CombatError, ShipVitals};
use bees_sim::geometry::Vec2;
use bees_sim::movement::ShipMotion;
use serde::Deserialize;

use crate::fleet_inventory::{
    NativeFleetInventory, NativeFleetInventoryError, NativeFleetShipValue,
};
use crate::scene_builder::{
    NativeAuthoredEntity, NativeAuthoredShip, NativeAuthoredPolicy, NativeAuthoredTurret,
};

const SNAPSHOT_VERSION: u32 = 2;

#[derive(Deserialize, Clone, Copy)]
struct UnityVec2 { x: f32, y: f32 }

impl UnityVec2 {
    fn into_vec(self) -> Vec2 { Vec2::new(self.x, self.y) }
}

#[derive(Deserialize)]
struct UnitySnapshot {
    schema_version: u32,
    level_name: String,
    fixed_dt_seconds: f32,
    min: UnityVec2,
    max: UnityVec2,
    ships: Vec<UnityShip>,
    obstacles: Vec<UnityObstacle>,
}

#[derive(Deserialize)]
struct UnityShip {
    entity_id: i64,
    ship_type: u8,
    side: i32,
    health: i32,
    max_health: i32,
    max_tsv: i32,
    current_tsv: i32,
    mined_cargo: i32,
    original_health: i32,
    fleet_ship_id: i64,
    persistent_fleet: bool,
    position: UnityVec2,
    rotation_degrees: f32,
    rotation_speed: f32,
    base_speed: f32,
    current_speed: f32,
    body_velocity: UnityVec2,
    height: f32,
    mobile: bool,
    cannot_change_orders: bool,
    user_controlled: bool,
    has_target_coordinates: bool,
    target_coordinates: UnityVec2,
    is_rl_policy_controlled: bool,
    is_moving: bool,
    movement_direction: u16,
    // Required in v2, exported from the actual Unity Weapon/Piece instances.
    turrets: Vec<UnityTurret>,
}

#[derive(Deserialize)]
struct UnityTurret {
    slot: usize,
    weapon_type: u8,
    projectile_type: u8,
    power: i32,
    mount_offset: UnityVec2,
    left_offset: UnityVec2,
    right_offset: UnityVec2,
    initial_rotation_degrees: f32,
    rotation_rate: f32,
    range: f32,
    rate_of_fire: f32,
    projectile_value: f32,
    projectile_speed: f64,
    fire_pattern: String,
}

#[derive(Deserialize)]
struct UnityObstacle {
    kind: String,
    entity_id: i64,
    prefab_name: String,
    prefab_number: u8,
    speed: i32,
    health: i32,
    effective_max_health: i32,
    is_shard: bool,
    position: UnityVec2,
    rotation_degrees: f32,
    velocity: UnityVec2,
    angular_velocity_degrees: f32,
}

#[derive(Debug)]
pub enum NativeSnapshotError {
    Read { path: PathBuf, error: io::Error },
    Malformed(serde_json::Error),
    UnsupportedSchema(u32),
    InvalidLevelBounds,
    InvalidShip(i64),
    MismatchedShipTsv(i64),
    Combat(CombatError),
    UnsupportedObstacle(i64),
    InvalidObstacle(i64),
    DuplicateEntity(i64),
    DuplicateTurretSlot { ship_id: i64, slot: usize },
    InvalidTurret { ship_id: i64, slot: usize },
    UnknownPolicyShip(i64),
    AlreadyBound(i64),
    Fleet(NativeFleetInventoryError),
}

/// A source-authored INITIAL physical scene, ready to compose with actual
/// squad identities, full RL metadata, episode configuration and model slots.
/// Records are owned, but shared immutable prefab geometry stays in the
/// existing NativeSceneAssets Arc catalogs.
pub struct NativePhysicalScene {
    pub level_name: String,
    pub bounds: Aabb,
    pub fixed_dt_seconds: f32,
    pub entities: Vec<NativeAuthoredEntity>,
    pub fleet: NativeFleetInventory,
    // Real authored physical mounts retained even for non-policy ships.
    // A Level must bind its own policy/route; the snapshot never invents one.
    pub turrets_by_ship: HashMap<i64, Vec<NativeAuthoredTurret>>,
    /// A physical Unity controller was active when captured. The caller
    /// must supply corresponding real NativeAuthoredPolicy for these IDs;
    /// this snapshot never fabricates a policy frame or decision period.
    pub active_policy_ship_ids: Vec<i64>,
}

impl NativePhysicalScene {
    /// Bind an actual source policy controller to one imported physical ship.
    /// The caller supplies its episode frame, cease-fire and mobility flags;
    /// imported native turrets retain exactly their authored mount and shot
    /// specifications. Never attach to a nonexistent/previously bound ship.
    pub fn bind_policy_ship(
        &mut self,
        ship_id: i64,
        frame: PolicyFrame,
        movement_disabled: bool,
        cease_fire: bool,
    ) -> Result<(), NativeSnapshotError> {
        let ship = self.entities.iter_mut().find_map(|entry| match entry {
            NativeAuthoredEntity::Ship(ship) if ship.entity_id == ship_id =>
                Some(ship),
            _ => None,
        }).ok_or(NativeSnapshotError::UnknownPolicyShip(ship_id))?;
        if ship.policy.is_some() {
            return Err(NativeSnapshotError::AlreadyBound(ship_id));
        }
        ship.policy = Some(NativeAuthoredPolicy {
            frame, movement_disabled, cease_fire,
            turrets: self.turrets_by_ship.remove(&ship_id).unwrap_or_default(),
        });
        Ok(())
    }

    pub fn read(path: &Path) -> Result<Self, NativeSnapshotError> {
        let source = fs::read_to_string(path).map_err(|error| NativeSnapshotError::Read {
            path: path.to_path_buf(), error,
        })?;
        Self::parse(&source)
    }

    pub fn parse(json: &str) -> Result<Self, NativeSnapshotError> {
        let raw: UnitySnapshot = serde_json::from_str(json)
            .map_err(NativeSnapshotError::Malformed)?;
        if raw.schema_version != SNAPSHOT_VERSION {
            return Err(NativeSnapshotError::UnsupportedSchema(raw.schema_version));
        }
        let min = raw.min.into_vec();
        let max = raw.max.into_vec();
        if raw.level_name.trim().is_empty() ||
            !raw.fixed_dt_seconds.is_finite() || raw.fixed_dt_seconds <= 0.0 ||
            !min.is_finite() || !max.is_finite() ||
            min.x >= max.x || min.y >= max.y
        {
            return Err(NativeSnapshotError::InvalidLevelBounds);
        }

        let mut entities = Vec::with_capacity(
            raw.ships.len().saturating_add(raw.obstacles.len())
        );
        let mut fleet_values = Vec::with_capacity(raw.ships.len());
        let mut turrets_by_ship = HashMap::with_capacity(raw.ships.len());
        let mut active_policy_ship_ids = Vec::new();
        let mut seen = HashSet::with_capacity(entities.capacity());
        for ship in raw.ships {
            if ship.entity_id <= 0 || ship.side <= 0 ||
                ship.original_health <= 0 ||
                ship.base_speed < 0.0 || ship.current_speed < 0.0 ||
                !ship.base_speed.is_finite() || !ship.current_speed.is_finite() ||
                !ship.rotation_speed.is_finite() || ship.rotation_speed < 0.0 ||
                !ship.height.is_finite() || ship.height <= 0.0 ||
                ship.movement_direction > 360 ||
                ship.mobile != (ship.base_speed > 0.0) ||
                (ship.fleet_ship_id <= 0 && ship.persistent_fleet)
            {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            if !seen.insert(ship.entity_id) {
                return Err(NativeSnapshotError::DuplicateEntity(ship.entity_id));
            }
            let vitals = ShipVitals::new(
                ship.health, ship.max_health, ship.max_tsv, ship.mined_cargo,
            ).map_err(NativeSnapshotError::Combat)?;
            if vitals.is_dead() || vitals.tsv() != ship.current_tsv {
                return Err(NativeSnapshotError::MismatchedShipTsv(ship.entity_id));
            }
            let pos = ship.position.into_vec();
            let vel = ship.body_velocity.into_vec();
            let target = ship.target_coordinates.into_vec();
            if !pos.is_finite() || !vel.is_finite() || !target.is_finite() ||
                !ship.rotation_degrees.is_finite()
            {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            let mut motion = ShipMotion::new(
                pos, ship.rotation_degrees, ship.rotation_speed,
                ship.base_speed, ship.height,
            );
            motion.current_speed = ship.current_speed;
            motion.body_velocity = vel;
            motion.cannot_change_movement_orders = ship.cannot_change_orders;
            motion.user_controlled = ship.user_controlled;
            motion.has_target_coordinates = ship.has_target_coordinates;
            motion.target_coordinates = target;
            motion.is_policy_controlled = ship.is_rl_policy_controlled;
            motion.is_moving = ship.is_moving;
            motion.movement_direction = ship.movement_direction;

            let mut authored_turrets = Vec::with_capacity(ship.turrets.len());
            let mut occupied = [false; 5];
            for weapon in ship.turrets {
                let invalid = |slot| NativeSnapshotError::InvalidTurret {
                    ship_id: ship.entity_id, slot,
                };
                if weapon.slot >= occupied.len() {
                    return Err(invalid(weapon.slot));
                }
                if occupied[weapon.slot] {
                    return Err(NativeSnapshotError::DuplicateTurretSlot {
                        ship_id: ship.entity_id, slot: weapon.slot,
                    });
                }
                occupied[weapon.slot] = true;
                let mount = weapon.mount_offset.into_vec();
                let left = weapon.left_offset.into_vec();
                let right = weapon.right_offset.into_vec();
                if !mount.is_finite() || !left.is_finite() || !right.is_finite() ||
                    !weapon.initial_rotation_degrees.is_finite() ||
                    !weapon.rotation_rate.is_finite() ||
                    !weapon.range.is_finite() ||
                    !weapon.rate_of_fire.is_finite() ||
                    !weapon.projectile_value.is_finite() ||
                    !weapon.projectile_speed.is_finite()
                {
                    return Err(invalid(weapon.slot));
                }
                let fire_pattern = match weapon.fire_pattern.as_str() {
                    "single" => NativeFirePattern::Single,
                    "dual" => NativeFirePattern::DualCannon {
                        left_offset: left, right_offset: right,
                    },
                    "laser-builder" => NativeFirePattern::LaserBuilder,
                    "full-ship" => NativeFirePattern::FullShipTurret,
                    "beam" => NativeFirePattern::BeamCannon,
                    _ => return Err(invalid(weapon.slot)),
                };
                authored_turrets.push(NativeAuthoredTurret {
                    slot: weapon.slot,
                    spec: NativeTurretSpec {
                        weapon_type: weapon.weapon_type,
                        projectile_type: weapon.projectile_type,
                        mount_offset: mount,
                        initial_rotation_degrees: weapon.initial_rotation_degrees,
                        rotation_rate: weapon.rotation_rate,
                        range: weapon.range,
                        power: weapon.power,
                        rate_of_fire: weapon.rate_of_fire,
                        projectile_value: weapon.projectile_value,
                        projectile_speed: weapon.projectile_speed,
                        fire_pattern,
                    },
                });
            }
            if !authored_turrets.is_empty() {
                turrets_by_ship.insert(ship.entity_id, authored_turrets);
            }
            if ship.is_rl_policy_controlled {
                active_policy_ship_ids.push(ship.entity_id);
            }
            fleet_values.push(NativeFleetShipValue {
                ship_id: ship.entity_id,
                has_persistent_fleet_value: ship.persistent_fleet,
                minerals_mined_this_level: ship.mined_cargo,
            });
            entities.push(NativeAuthoredEntity::Ship(NativeAuthoredShip {
                entity_id: ship.entity_id, ship_type: ship.ship_type,
                side: ship.side, vitals, motion,
                original_health: ship.original_health,
                fleet_ship_id: (ship.fleet_ship_id > 0)
                    .then_some(ship.fleet_ship_id),
                policy: None,
            }));
        }
        if fleet_values.is_empty() {
            return Err(NativeSnapshotError::InvalidLevelBounds);
        }
        for obstacle in raw.obstacles {
            if obstacle.entity_id <= 0 || !seen.insert(obstacle.entity_id) {
                return Err(NativeSnapshotError::DuplicateEntity(obstacle.entity_id));
            }
            let pos = obstacle.position.into_vec();
            let velocity = obstacle.velocity.into_vec();
            let pose = Pose {
                position: pos,
                rotation_degrees: obstacle.rotation_degrees,
            };
            if !pos.is_finite() || !velocity.is_finite() ||
                !pose.rotation_degrees.is_finite() ||
                !obstacle.angular_velocity_degrees.is_finite()
            {
                return Err(NativeSnapshotError::InvalidObstacle(obstacle.entity_id));
            }
            let entity = match obstacle.kind.as_str() {
                "box" => NativeAuthoredEntity::BoxObstacle {
                    entity_id: obstacle.entity_id,
                    prefab_name: obstacle.prefab_name,
                    pose: Some(pose),
                },
                "polygon" => NativeAuthoredEntity::PolygonObstacle {
                    entity_id: obstacle.entity_id,
                    pose: Some(pose),
                },
                "full-asteroid" => {
                    // Partial asteroid durability, active shard-family
                    // collisions, and delayed-kill state cannot be inferred
                    // from appearance. Do not treat those as fresh spawns.
                    if obstacle.is_shard || obstacle.health <= 0 ||
                        obstacle.health != obstacle.effective_max_health ||
                        obstacle.prefab_number == 0 || obstacle.speed < 2 {
                        return Err(NativeSnapshotError::InvalidObstacle(obstacle.entity_id));
                    }
                    NativeAuthoredEntity::FullCollisionAsteroid {
                        entity_id: obstacle.entity_id,
                        profile: FullAsteroidPrototype {
                            prefab_number: obstacle.prefab_number,
                            speed: obstacle.speed,
                        },
                        pose,
                        velocity,
                        angular_velocity_degrees: obstacle.angular_velocity_degrees,
                        effective_max_health: obstacle.effective_max_health,
                    }
                },
                _ => return Err(NativeSnapshotError::UnsupportedObstacle(obstacle.entity_id)),
            };
            entities.push(entity);
        }
        // Unity GameState.GetId allocates Stage-wide ascending entity IDs.
        // Ships and obstacles have separate source collections; merging
        // them by exact allocated ID restores the real creation order and
        // satisfies CombatWorld's monotonically increasing entity ledger.
        entities.sort_unstable_by_key(NativeAuthoredEntity::entity_id);
        let fleet = NativeFleetInventory::from_source(fleet_values)
            .map_err(NativeSnapshotError::Fleet)?;
        Ok(Self {
            level_name: raw.level_name,
            bounds: Aabb { min, max },
            fixed_dt_seconds: raw.fixed_dt_seconds,
            entities,
            fleet,
            turrets_by_ship,
            active_policy_ship_ids,
        })
    }
}
