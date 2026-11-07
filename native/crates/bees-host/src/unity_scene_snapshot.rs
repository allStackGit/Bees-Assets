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
use std::num::NonZeroU32;
use std::sync::Arc;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use bees_sim::asteroid_spawn::FullAsteroidPrototype;
use bees_sim::combat_world::{NativeFirePattern, NativeTurretSpec};
use bees_sim::perception_entities::ObservedWeapon;
use bees_sim::control::PolicyFrame;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::combat::{CombatError, ShipVitals};
use bees_sim::geometry::Vec2;
use bees_sim::movement::ShipMotion;
use bees_sim::simulation::WorldId;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::combat_perception_bridge::AuthoredShipPerception;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_gameplay::episode::NativeEpisodeRewardCoordinator;
use bees_gameplay::group_rewards::NativeGroupRewardDelivery;
use bees_gameplay::level_rules::NativeLevelState;
use serde::Deserialize;

use crate::fleet_inventory::{
    NativeFleetInventory, NativeFleetInventoryError, NativeFleetShipValue,
};
use crate::scene_builder::{
    NativeAuthoredEntity, NativeAuthoredShip, NativeAuthoredPolicy,
    NativeAuthoredTurret, NativeAuthoredSquad, NativeAuthoredLevel,
    NativeAuthoredGroups,
};
use crate::policy_only_metadata::{
    AuthoredPolicyAgent, AuthoredPolicySide, NativePolicyOnlyMetadata,
    PolicyOnlyMetadataError,
};

const SNAPSHOT_VERSION: u32 = 4;

#[derive(Deserialize, Clone, Copy)]
struct UnityVec2 { x: f32, y: f32 }

#[derive(Deserialize, Clone, Copy)]
struct UnityVec4 { x: f32, y: f32, z: f32, w: f32 }

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
    squads: Vec<UnitySquad>,
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
    longest_side: f32,
    max_weapon_range: f32,
    firepower: f32,
    is_bomber: bool,
    is_carrier_ship: bool,
    has_communication: bool,
    communication: UnityVec4,
    observed_weapons: Vec<UnityObservedWeapon>,
    // Required in v2, exported from the actual Unity Weapon/Piece instances.
    turrets: Vec<UnityTurret>,
}

#[derive(Deserialize)]
struct UnityObservedWeapon {
    slot: usize,
    weapon_type: u8,
    range: f32,
    power: f32,
    rate_of_fire: f32,
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
struct UnitySquad {
    item_id: i64,
    side: i32,
    member_ids: Vec<i64>,
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
    InvalidSquad(i64),
    DuplicateSquad(i64),
    DuplicateSquadShip(i64),
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
    /// Exact source ship, weapon and private communication metadata.
    pub authored_ship_perceptions: HashMap<i64, AuthoredShipPerception>,
    /// A physical Unity controller was active when captured. The caller
    /// must supply corresponding real NativeAuthoredPolicy for these IDs;
    /// this snapshot never fabricates a policy frame or decision period.
    pub active_policy_ship_ids: Vec<i64>,
    /// Source GameState.Squads order and exact members; no duplicate
    /// persistent fleet or second mutable native squad registry.
    pub source_squads: Vec<NativeSourceSquad>,
}

/// All values not represented in Unity's physical snapshot remain required
/// authenticated/authoritative inputs. The caller binds real controller
/// frames to imported ships before composing the policy Level, and provides
/// the complete v26 observation/NavigationRuntime environment here.
pub struct NativePolicyOnlyLevelSetup<A> {
    pub world_id: WorldId,
    pub level_id: i64,
    pub seed: u64,
    pub broadphase_cell_size: f32,
    pub decision_period: NonZeroU32,
    pub initial_full_asteroids: usize,
    pub episode: NativeEpisodeRewardCoordinator,
    pub groups: Option<NativeAuthoredGroups>,
    pub navigation: NavigationRuntime,
    pub ship_catalog: Arc<ShipAssetCatalog>,
    pub policy_agents: Vec<AuthoredPolicyAgent>,
    pub sides: Vec<AuthoredPolicySide>,
    pub allocate_minion: A,
}

#[derive(Debug)]
pub enum NativePolicyOnlyLevelError {
    Snapshot(NativeSnapshotError),
    Metadata(PolicyOnlyMetadataError),
    InvalidControllerBinding(i64),
}

/// Runtime source Squad.ItemId is allocated once per Squad.Setup by the
/// Stage-wide monotonic Pool.ItemCount, even for reused pooled wrappers.
pub struct NativeSourceSquad {
    pub item_id: i64,
    pub side: i32,
    pub members: Vec<i64>,
}

impl NativePhysicalScene {
    /// Finish ONE actual imported Level without creating a separate combat
    /// backend or estimated ship/weapon/squad values. Every supplied policy
    /// agent must match one previously bound native ship on the same side;
    /// likewise no bound policy ship may lack its source observation route.
    pub fn into_policy_only_level<A>(
        self,
        setup: NativePolicyOnlyLevelSetup<A>,
    ) -> Result<NativeAuthoredLevel<NativePolicyOnlyMetadata, A>,
        NativePolicyOnlyLevelError>
    {
        let mut bound_sides = HashMap::new();
        for entity in &self.entities {
            if let NativeAuthoredEntity::Ship(ship) = entity {
                if ship.policy.is_some() {
                    bound_sides.insert(ship.entity_id, ship.side);
                }
            }
        }
        for agent in &setup.policy_agents {
            if bound_sides.remove(&agent.ship_id) != Some(agent.side) {
                return Err(NativePolicyOnlyLevelError::InvalidControllerBinding(
                    agent.ship_id,
                ));
            }
        }
        if let Some((&ship_id, _)) = bound_sides.iter().next() {
            return Err(NativePolicyOnlyLevelError::InvalidControllerBinding(
                ship_id,
            ));
        }

        let squads = self.squads_for_level(setup.level_id)
            .map_err(NativePolicyOnlyLevelError::Snapshot)?;
        let metadata = NativePolicyOnlyMetadata::new(
            self.bounds,
            setup.navigation,
            self.fleet,
            setup.ship_catalog,
            self.authored_ship_perceptions,
            setup.policy_agents,
            setup.sides,
        ).map_err(NativePolicyOnlyLevelError::Metadata)?;
        Ok(NativeAuthoredLevel {
            world_id: setup.world_id,
            seed: setup.seed,
            broadphase_bounds: self.bounds,
            broadphase_cell_size: setup.broadphase_cell_size,
            decision_period: setup.decision_period,
            entities: self.entities,
            squads,
            initial_full_asteroids: setup.initial_full_asteroids,
            episode: setup.episode,
            groups: setup.groups,
            metadata,
            allocate_minion: setup.allocate_minion,
        })
    }

    /// Derive the *current* lifetime token from the source's unique ItemId,
    /// not from a pooled GameObject pointer, reusable squad number or SavedSquad.
    /// The caller supplies the actual distinct Level identity used by HiveMind.
    pub fn squads_for_level(
        &self, level_id: i64,
    ) -> Result<Vec<NativeAuthoredSquad>, NativeSnapshotError> {
        if level_id <= 0 {
            return Err(NativeSnapshotError::InvalidSquad(level_id));
        }
        Ok(self.source_squads.iter().map(|source|
            NativeAuthoredSquad {
                owner: SquadLifetime {
                    level_id, squad_id: source.item_id,
                    lifecycle_id: source.item_id as u64,
                },
                side: source.side,
                members: source.members.clone(),
            }
        ).collect())
    }


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
        let mut authored_ship_perceptions = HashMap::with_capacity(raw.ships.len());
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

            let mut observed_weapons = [None; 5];
            for weapon in ship.observed_weapons {
                if weapon.slot >= observed_weapons.len() ||
                    observed_weapons[weapon.slot].is_some() {
                    return Err(NativeSnapshotError::InvalidTurret {
                        ship_id: ship.entity_id, slot: weapon.slot,
                    });
                }
                observed_weapons[weapon.slot] = Some(ObservedWeapon {
                    weapon_type: weapon.weapon_type,
                    range: weapon.range,
                    power: weapon.power,
                    rate_of_fire: weapon.rate_of_fire,
                });
            }
            let comm = ship.communication;
            let metadata = AuthoredShipPerception {
                longest_side: ship.longest_side,
                max_weapon_range: ship.max_weapon_range,
                firepower: ship.firepower,
                is_bomber: ship.is_bomber,
                is_carrier_ship: ship.is_carrier_ship,
                minerals_mined_this_level: ship.mined_cargo,
                observed_weapons,
                allied_communication: ship.has_communication.then_some([
                    comm.x, comm.y, comm.z, comm.w,
                ]),
            };
            if !metadata.valid() {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            authored_ship_perceptions.insert(ship.entity_id, metadata);

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
        let live_ships: HashMap<i64, i32> = entities.iter().filter_map(|entity|
            match entity {
                NativeAuthoredEntity::Ship(ship) =>
                    Some((ship.entity_id, ship.side)),
                _ => None,
            }
        ).collect();
        let mut source_squads = Vec::with_capacity(raw.squads.len());
        let mut squad_ids = HashSet::with_capacity(raw.squads.len());
        let mut registered_ships = HashSet::with_capacity(live_ships.len());
        for squad in raw.squads {
            if squad.item_id <= 0 || squad.side <= 0 || squad.member_ids.is_empty() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            if !squad_ids.insert(squad.item_id) {
                return Err(NativeSnapshotError::DuplicateSquad(squad.item_id));
            }
            for &ship_id in &squad.member_ids {
                if live_ships.get(&ship_id) != Some(&squad.side) {
                    return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                }
                if !registered_ships.insert(ship_id) {
                    return Err(NativeSnapshotError::DuplicateSquadShip(ship_id));
                }
            }
            source_squads.push(NativeSourceSquad {
                item_id: squad.item_id, side: squad.side,
                members: squad.member_ids,
            });
        }
        if registered_ships.len() != live_ships.len() {
            return Err(NativeSnapshotError::InvalidSquad(0));
        }
        let fleet = NativeFleetInventory::from_source(fleet_values)
            .map_err(NativeSnapshotError::Fleet)?;
        Ok(Self {
            level_name: raw.level_name,
            bounds: Aabb { min, max },
            fixed_dt_seconds: raw.fixed_dt_seconds,
            entities,
            fleet,
            turrets_by_ship,
            authored_ship_perceptions,
            active_policy_ship_ids,
            source_squads,
        })
    }
}
