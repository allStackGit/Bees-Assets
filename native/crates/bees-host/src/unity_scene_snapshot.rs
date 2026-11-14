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
use bees_sim::combat_world::{NativeFirePattern, NativeTurretSpec, NativeTurretRangeSpec, NativeShipTargetGeometry, NativeLevelGeometry, NativeWeaponRankSource, GameStrikerCarrier, ScoutBeaconSpec};
use bees_sim::perception_entities::ObservedWeapon;
use bees_sim::control::PolicyFrame;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::combat::{CombatError, ShipVitals};
use bees_sim::geometry::Vec2;
use bees_sim::box_obstacle_assets::{live_box_shape, BoxObstacleCatalog};
use bees_sim::polygon_obstacle_assets::PolygonObstacleCatalog;
use bees_sim::physics::{ObstacleBody, ShipShape};
use bees_sim::map_object::CombatMapObject;
use bees_sim::obstacle::{CombatObstacle, ObstacleKind, ObstacleVitals};
use bees_sim::movement::ShipMotion;
use bees_sim::navigation_movement::GameMovementProfile;
use bees_sim::simulation::WorldId;
use bees_sim::ship_assets::{ShipAssetCatalog, AssetError, BEACON_TYPE};
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, ControlledShipPerception,
};
use bees_sim::perception::CapabilitySource;
use bees_sim::perception_weapons::SelfWeapon;
use bees_gameplay::inference::NativePolicyRoute;
use bees_gameplay::group_rewards::team_id_for_side;
use bees_sim::navigation_runtime::{NavigationMapFrame, NavigationRuntime};
use bees_sim::navigation::{NavigationGrid, NavigationError};
use bees_sim::team_exploration::TeamExplorationGrid;
use bees_gameplay::episode::{
    NativeEpisodeRewardCoordinator, NativeEpisodeRewardError,
};
use bees_gameplay::inference::NativePolicyInference;
use bees_gameplay::level_host::NativeLevelNotificationSink;
use serde::{Deserialize, Serialize};

use crate::fleet_inventory::{
    NativeFleetInventory, NativeFleetInventoryError, NativeFleetShipValue,
};
use crate::scene_builder::{
    NativeAuthoredEntity, NativeAuthoredShip, NativeAuthoredPolicy,
    NativeAuthoredTurret, NativeAuthoredSquad, NativeAuthoredLevel,
    NativeAuthoredGroups, NativeRuntimeBuildError,
    NativeAuthoredPolicyCapabilities, NativeHealingEligibility,
};
use crate::policy_only_metadata::{
    NativeSourceVision,
    AuthoredPolicyAgent, AuthoredPolicySide, NativePolicyOnlyMetadata,
    PolicyOnlyMetadataError,
    NativeSourcePlayerInputs, NativeSourceFormation,
};
use crate::scene_assets::NativeSceneAssets;
use crate::runtime::NativeHostRuntime;
use crate::ship_specials::{NativeAuthoredShipSpecial, NativeBeaconTemplate, NativeQueenTemplate};
use crate::durable_connection::DurableHiveMindConnection;

mod native_game_scene;

const SNAPSHOT_VERSION: u32 = 33;

#[derive(Deserialize, Clone, Copy)]
struct UnityVec2 { x: f32, y: f32 }

#[derive(Deserialize, Clone, Copy)]
struct UnityVec4 { x: f32, y: f32, z: f32, w: f32 }

#[derive(Deserialize, Clone, Copy)]
struct UnitySize { x: u32, y: u32 }

#[derive(Deserialize)]
struct UnitySupportCollider {
    kind: String,
    vertices: Vec<UnityVec2>,
    offset: UnityVec2,
    radius: f32,
}

impl UnitySupportCollider {
    fn into_shape(self, id: i64) -> Result<ShipShape, NativeSnapshotError> {
        let invalid = || NativeSnapshotError::InvalidShip(id);
        match self.kind.as_str() {
            "box" if self.vertices.len() == 4 => {
                let corners = [self.vertices[0].into_vec(), self.vertices[1].into_vec(),
                    self.vertices[2].into_vec(), self.vertices[3].into_vec()];
                live_box_shape(corners).map_err(|_| invalid())
            }
            "polygon" if (3..=8192).contains(&self.vertices.len()) => {
                let vertices: Vec<_> = self.vertices.into_iter().map(UnityVec2::into_vec).collect();
                if vertices.iter().any(|v| !v.is_finite()) { return Err(invalid()); }
                Ok(ShipShape::Polygon(Arc::from(vertices.into_boxed_slice())))
            }
            "circle" if self.vertices.is_empty() && self.offset.into_vec().is_finite() &&
                self.radius.is_finite() && self.radius > 0.0 =>
                Ok(ShipShape::Circle { offset: self.offset.into_vec(), radius: self.radius }),
            _ => Err(invalid()),
        }
    }
}

impl UnityVec2 {
    fn into_vec(self) -> Vec2 { Vec2::new(self.x, self.y) }
}

#[derive(Deserialize)]
struct UnitySnapshot {
    targeting_context: Option<UnityTargetingContext>,
    source_frame_count: Option<i32>,
    schema_version: u32,
    level_name: String,
    stage_name: String,
    fixed_dt_seconds: f32,
    shots_harmless: bool,
    activate_collision_asteroids: bool,
    activate_mining: Option<bool>,
    activate_fog_of_war: Option<bool>,
    camera_min_zoom: Option<i32>,
    camera_max_zoom: Option<i32>,
    camera_edge_scrolling: Option<bool>,
    activate_hivemind: Option<bool>,
    hivemind_initial_command_delay_seconds: Option<i32>,
    has_obstacles: bool,
    command_settings: UnityCommandSettings,
    bee_side: i32,
    human_side: i32,
    beehive_size_pixels: UnitySize,
    has_policy_episode: bool,
    policy_episode: Option<UnityPolicyEpisode>,
    min: UnityVec2,
    max: UnityVec2,
    starting_positions: Option<Vec<UnityVec2>>,
    original_squad_counts: Option<Vec<i32>>,
    navigation_map_width: i32,
    navigation_map_height: i32,
    navigation_origin_world: UnityVec2,
    navigation_x_axis_world: UnityVec2,
    navigation_y_axis_world: UnityVec2,
    ships: Vec<UnityShip>,
    obstacles: Vec<UnityObstacle>,
    map_objects: Vec<UnityMapObject>,
    squads: Vec<UnitySquad>,
}

#[derive(Deserialize)]
struct UnityPolicyEpisode {
    bee_side: i32,
    human_side: i32,
    bee_starting_tsv: i32,
    human_starting_tsv: i32,
    timeout_seconds: f32,
    discovery_baselines: Option<Vec<i32>>,
    #[serde(default)]
    episode_number: u32,
    #[serde(default)]
    team0_movement_disabled: Option<bool>,
    #[serde(default)]
    team1_movement_disabled: Option<bool>,
    #[serde(default)]
    group_participants: Vec<UnityGroupParticipant>,
}

#[derive(Deserialize)]
struct UnityGroupParticipant {
    agent_id: u64,
    ship_id: i64,
    side: i32,
    team_id: u8,
}

#[derive(Deserialize)]
struct UnityTargetingContext { has_player: bool, user_side: i32 }

#[derive(Deserialize)]
struct UnityCombatActivity {
    enabled: bool,
    window_seconds: f32,
    active: bool,
    elapsed_seconds: f32,
}
impl UnityCombatActivity {
    fn convert(self) -> Result<bees_sim::combat_world::NativeCombatActivity, NativeSnapshotError> {
        let source = bees_sim::combat_world::NativeCombatActivity {
            enabled: self.enabled, window_seconds: self.window_seconds,
            elapsed_seconds: self.active.then_some(self.elapsed_seconds),
        };
        if !source.valid() || !self.elapsed_seconds.is_finite() || self.elapsed_seconds < 0.0 {
            return Err(NativeSnapshotError::InvalidLevelBounds);
        }
        Ok(source)
    }
}

#[derive(Deserialize)]
struct UnityShip {
    combat_activity: Option<UnityCombatActivity>,
    special_weapon: Option<UnitySpecialWeapon>,
    weapon_rank_source: Option<UnityWeaponRankSource>,
    target_geometry: Option<UnityShipTargetGeometry>,
    entity_id: i64,
    ship_type: u8,
    side: i32,
    health: i32,
    max_health: i32,
    max_tsv: i32,
    current_tsv: i32,
    mined_cargo: i32,
    original_health: i32,
    size_class: f32,
    special: UnityShipSpecial,
    size_pixels: UnitySize,
    has_healing_zone: bool,
    healing_zone: Option<UnitySupportCollider>,
    has_warp_zone: bool,
    warp_zone: Option<UnitySupportCollider>,
    fleet_ship_id: i64,
    persistent_fleet: bool,
    position: UnityVec2,
    rotation_degrees: f32,
    rotation_speed: f32,
    base_speed: f32,
    current_speed: f32,
    body_velocity: UnityVec2,
    height: f32,
    turning_radius: f32,
    half_width: f32,
    half_height: f32,
    offset_from_center: UnityVec2,
    path_clearance: i32,
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
    has_bound_policy: bool,
    policy_team_id: i32,
    policy_quarter_turns: i32,
    policy_movement_disabled: bool,
    policy_cease_fire: bool,
    observed_weapons: Vec<UnityObservedWeapon>,
    source_sight: Option<i32>,
    effective_vision_range: i32,
    /// Legacy scene exports omit the initially created SpriteMask radius.
    fog_vision_radius: Option<f32>,
    hivemind_registered: bool,
    hivemind_vision_enabled: bool,
    hivemind_sight_range: i32,
    has_weapons: bool,
    has_turrets: bool,
    has_special_action: bool,
    special_ready: bool,
    special_resource_fraction: f32,
    special_cooldown_fraction: f32,
    special_phase: f32,
    mining_possible: bool,
    healing_possible: bool,
    warp_possible: bool,
    is_carrier_child: bool,
    has_living_parent_carrier: bool,
    parent_carrier_id: i64,
    team_retainable_mined_tsv: i32,
    side_starting_tsv: i32,
    self_weapons: Vec<UnitySelfWeapon>,
    // Required in v2, exported from the actual Unity Weapon/Piece instances.
    turrets: Vec<UnityTurret>,
}

#[derive(Deserialize)]
struct UnityShipSpecial {
    kind: String,
    power: i32,
    can_override_bounds: Option<bool>,
    carrier_id: i64,
    offset_from_center: UnityVec2,
    refill_distance: f32,
    scout_enabled: bool,
    beacon: Option<UnityBeaconTemplate>,
}

#[derive(Deserialize)]
struct UnityBeaconTemplate {
    combat_activity: Option<UnityCombatActivity>,
    targeting: Option<UnityBeaconTargetSource>,
    original_health: i32,
    health: i32,
    max_tsv: i32,
    size_class: f32,
    base_speed: f32,
    rotation_speed: f32,
    height: f32,
    longest_side: f32,
    effective_vision_range: i32,
    hivemind_registered: bool,
    hivemind_vision_enabled: bool,
    max_living_ships_per_side: i32,
}

#[derive(Deserialize)]
struct UnityBeaconTargetSource {
    front_offset: f32,
    ship_type_letter: u8,
    firepower: f32,
}

impl UnityShipSpecial {
    fn into_native(self, id: i64, ship_type: u8, schema: u32, catalog: &ShipAssetCatalog)
        -> Result<Option<NativeAuthoredShipSpecial>, NativeSnapshotError>
    {
        let invalid = || NativeSnapshotError::InvalidShip(id);
        if self.power < 0 { return Err(invalid()); }
        let special = match (self.kind.as_str(), ship_type) {
            ("none", kind) if !matches!(kind, 0 | 10 | 18 | 19 | 22) => return Ok(None),
            ("barge", 0) if schema >= 30 && self.can_override_bounds.is_none() =>
                return Err(invalid()),
            ("barge", 0) => NativeAuthoredShipSpecial::Barge {
                power: self.power,
                can_override_bounds: self.can_override_bounds,
            },
            ("fire-barge", 10) => NativeAuthoredShipSpecial::FireBarge { power: self.power },
            ("yellow-jacket", 22) => NativeAuthoredShipSpecial::YellowJacket { power: self.power },
            ("striker", 19) if self.carrier_id >= 0 &&
                self.offset_from_center.into_vec().is_finite() &&
                self.refill_distance.is_finite() && self.refill_distance > 0.0 =>
                NativeAuthoredShipSpecial::Striker { power: self.power, carrier: GameStrikerCarrier {
                    carrier_id: (self.carrier_id != 0).then_some(self.carrier_id),
                    offset_from_center: self.offset_from_center.into_vec(),
                    refill_distance: self.refill_distance,
                } },
            ("scout", 18) => {
                let source = self.beacon.ok_or_else(invalid)?;
                let targeting = match source.targeting {
                    Some(value) if value.front_offset.is_finite() && value.ship_type_letter < 24 &&
                        value.firepower.is_finite() && value.firepower >= 0.0 =>
                        Some(bees_sim::combat_world::NativeBeaconTargetSource {
                            front_offset: value.front_offset, ship_type_letter: value.ship_type_letter,
                            firepower: value.firepower,
                        }),
                    None if schema < 23 => None,
                    _ => return Err(invalid()),
                };
                if source.original_health <= 0 || source.health <= 0 ||
                    source.health > source.original_health || source.max_tsv < 0 ||
                    !source.size_class.is_finite() || source.size_class < 0.0 ||
                    ![source.base_speed, source.rotation_speed, source.height, source.longest_side]
                        .iter().all(|v| v.is_finite() && *v >= 0.0) || source.height <= 0.0 ||
                    source.effective_vision_range < 0 || source.max_living_ships_per_side < 0 ||
                    (source.hivemind_vision_enabled && (!source.hivemind_registered || source.effective_vision_range == 0))
                { return Err(invalid()); }
                NativeAuthoredShipSpecial::Scout { enabled: self.scout_enabled, beacon: Arc::new(NativeBeaconTemplate {
                    physical: ScoutBeaconSpec {
                        combat_activity: source.combat_activity.map(UnityCombatActivity::convert).transpose()?,
                        beacon_asset: catalog.by_type(BEACON_TYPE).ok_or_else(invalid)?.clone(),
                        beacon_vitals: ShipVitals::new(source.health, source.health, source.max_tsv, 0)
                            .map_err(NativeSnapshotError::Combat)?,
                        beacon_base_speed: source.base_speed, beacon_rotation_speed: source.rotation_speed,
                        beacon_height: source.height, beacon_original_health: source.original_health,
                        beacon_size_class: source.size_class,
                        targeting,
                        max_living_ships_per_side: (source.max_living_ships_per_side > 0)
                            .then_some(source.max_living_ships_per_side as usize),
                    },
                    perception: AuthoredShipPerception {
                        longest_side: source.longest_side, max_weapon_range: 0.0,
                        firepower: targeting.map_or(0.0, |value| value.firepower),
                        is_bomber: false, is_carrier_ship: false, minerals_mined_this_level: 0,
                        observed_weapons: [None; 5], allied_communication: None,
                    },
                    vision: NativeSourceVision {
                        source_sight: Some(source.effective_vision_range),
                        effective_range: source.effective_vision_range, trigger_range: source.effective_vision_range,
                        fog_radius: None,
                        registered: source.hivemind_registered, enabled: source.hivemind_vision_enabled,
                    },
                }) }
            }
            _ => return Err(invalid()),
        };
        Ok(Some(special))
    }
}

#[derive(Deserialize)]
struct UnitySelfWeapon {
    slot: usize,
    weapon_type: u8,
    position: UnityVec2,
    range: f32,
    power: f32,
    rate_of_fire: f32,
    rotation_rate: f32,
    projectile_value: f32,
    projectile_speed: f64,
    has_projectile_speed: bool,
    has_target_ship: bool,
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
struct UnityWeaponRankSource {
    fleet_owner_ship_id: i64,
    ship_type_letter: u8,
    last_killed: i32,
    fleet_damage_done: i32,
    firepower: f32,
    max_range: i32,
    speed: f32,
}

#[derive(Deserialize)]
struct UnitySpecialTrigger {
    collider: UnitySupportCollider,
    layer: u8,
    enabled: bool,
}

impl UnitySpecialTrigger {
    fn into_native(self, id: i64) -> Result<NativeTurretRangeSpec, NativeSnapshotError> {
        let ShipShape::Circle { offset, radius } = self.collider.into_shape(id)? else {
            return Err(NativeSnapshotError::InvalidShip(id));
        };
        let spec = NativeTurretRangeSpec { offset, radius, layer: self.layer, enabled: self.enabled };
        if !spec.valid() { return Err(NativeSnapshotError::InvalidShip(id)); }
        Ok(spec)
    }
}

#[derive(Deserialize)]
struct UnitySpecialWeapon {
    weapon_id: i64,
    slot: usize,
    power: i32,
    mount_offset: UnityVec2,
    charge_range: Option<UnitySpecialTrigger>,
    proximity: Option<UnitySpecialTrigger>,
}

#[derive(Deserialize)]
struct UnityShipTargetGeometry {
    front_offset: f32,
    bounds_latched: bool,
}

#[derive(Deserialize)]
struct UnityTurret {
    aim_at_front_of_ship: Option<bool>,
    range_collider: Option<UnitySupportCollider>,
    range_layer: Option<u8>,
    range_enabled: Option<bool>,
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
struct UnityCommandSettings {
    max_command_seconds: f32,
    ai_random_max_distance: f32,
    ai_patrol_max_size: f32,
    ai_squad_patrol_seconds: f32,
    ai_squad_following_seconds: f32,
    ai_squad_guard_seconds: f32,
}

impl From<UnityCommandSettings> for bees_gameplay::commands::HiveMindCommandSettings {
    fn from(value: UnityCommandSettings) -> Self {
        Self {
            max_command_seconds: value.max_command_seconds,
            ai_random_max_distance: value.ai_random_max_distance,
            ai_patrol_max_size: value.ai_patrol_max_size,
            ai_squad_patrol_seconds: value.ai_squad_patrol_seconds,
            ai_squad_following_seconds: value.ai_squad_following_seconds,
            ai_squad_guard_seconds: value.ai_squad_guard_seconds,
        }
    }
}

#[derive(Deserialize)]
struct UnitySquad {
    banned_strategies: Option<Vec<String>>,
    is_carrier_squad: Option<bool>,
    squad_number: Option<i32>,
    is_minion_squad: Option<bool>,
    is_hivemind_controlled: Option<bool>,
    is_immobile: Option<bool>,
    opponent_id_decimal: Option<String>,
    item_id: i64,
    side: i32,
    member_ids: Vec<i64>,
    user_controlled: bool,
    can_accept_input: bool,
    locked_on: bool,
    should_chase: Option<bool>,
    is_matching_speed: Option<bool>,
    current_speed: Option<f32>,
    cease_fire: Option<bool>,
    shooting_strategy: String,
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
    collider_vertices: Vec<UnityVec2>,
    layer: u8,
}

/// A physically present MapObject with authentic Unity collider geometry.
#[derive(Deserialize)]
struct UnityMapObject {
    entity_id: i64,
    kind: String,
    layer: u8,
    health: i32,
    max_health: i32,
    position: UnityVec2,
    rotation_degrees: f32,
    linear_velocity: UnityVec2,
    angular_velocity_degrees: f32,
    collider_kind: String,
    collider_vertices: Vec<UnityVec2>,
    circle_offset: UnityVec2,
    circle_radius: f32,
    explosion_power: i32,
    linked_obstacle_id: i64,
}

#[derive(Debug)]
pub enum NativeSnapshotError {
    ShipCatalog(AssetError),
    Read { path: PathBuf, error: io::Error },
    Malformed(serde_json::Error),
    UnsupportedSchema(u32),
    InvalidLevelBounds,
    InvalidCameraZoom,
    MissingWeaponRankingClock,
    MissingTargetingSource,
    MissingHiveMindSource,
    InvalidCommandSettings,
    InvalidShip(i64),
    MismatchedShipTsv(i64),
    Combat(CombatError),
    UnsupportedObstacle(i64),
    InvalidObstacle(i64),
    /// Invalid native-authored map modification, rejected before scene mutation.
    InvalidNativeLayout(&'static str),
    InvalidMapObject(i64),
    DuplicateEntity(i64),
    InvalidSquad(i64),
    DuplicateSquad(i64),
    DuplicateSquadShip(i64),
    DuplicateTurretSlot { ship_id: i64, slot: usize },
    InvalidTurret { ship_id: i64, slot: usize },
    UnknownPolicyShip(i64),
    AlreadyBound(i64),
    InvalidPolicySource(i64),
    InvalidEpisodeSource,
    InvalidGroupSource,
    InvalidNavigationSource,
    Navigation(NavigationError),
    Episode(NativeEpisodeRewardError),
    MissingModelSlot(u8),
    Fleet(NativeFleetInventoryError),
}

/// Native-authored game Level geometry. Source snapshots supply verified
/// prefabs and gameplay capabilities, while operators can change static
/// obstacle layouts and translate complete source squads without re-exporting
/// from Unity. Policy/group reward scenes are excluded until their immutable
/// discovery denominators can be authored independently.
#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativeLevelLayout {
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub remove_static_ids: Vec<i64>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub add_static_boxes: Vec<NativePlacedStaticBox>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub add_static_polygons: Vec<NativePlacedStaticPolygon>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub translate_squads: Vec<NativeSquadTranslation>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub clone_squads: Vec<NativeSquadClone>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub remove_squads: Vec<i64>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativePlacedStaticBox {
    pub entity_id: i64,
    pub prefab_name: String,
    pub position: [f32; 2],
    pub rotation_degrees: f32,
    /// Optional exact root-local primary collider corners. A map creator
    /// may resize/reshape the source prefab; physics and rendering use the
    /// same validated geometry.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub local_corners: Option<[[f32; 2]; 4]>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativePlacedStaticPolygon {
    pub entity_id: i64,
    pub position: [f32; 2],
    pub rotation_degrees: f32,
    /// Omit to use the verified immutable source Polygon Obstacle outline;
    /// set explicitly for a resized/edited exact collider-local path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub local_vertices: Option<Vec<[f32; 2]>>,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativeSquadTranslation {
    pub squad_item_id: i64,
    pub by: [f32; 2],
}

/// Add a genuinely new native game squad from a verified source squad's
/// exact hulls, weapons, sight, fleet metadata and formation. The Level
/// allocates independent gameplay identities and may move the new squad
/// without altering the original template.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativeSquadClone {
    pub source_squad_item_id: i64,
    pub new_squad_item_id: i64,
    /// One new world entity ID for each source member, in original order.
    pub ship_ids: Vec<i64>,
    pub by: [f32; 2],
}

/// A source-authored INITIAL physical scene, ready to compose with actual
/// squad identities, full RL metadata, episode configuration and model slots.
/// Records are owned, but shared immutable prefab geometry stays in the
/// existing NativeSceneAssets Arc catalogs.
pub struct NativePhysicalScene {
    pub targeting_context: Option<bees_gameplay::squad_targeting::NativeTargetingContext>,
    bee_side: i32,
    pub human_side: i32,
    pub source_frame_count: Option<i32>,
    pub schema_version: u32,
    pub level_name: String,
    /// Source Stage identity for validating the loaded prefab palette.
    pub stage_name: String,
    pub bounds: Aabb,
    /// Exact Stage-owned home coordinates indexed by source Side-1.
    pub starting_positions: Option<[Vec2; 2]>,
    pub fixed_dt_seconds: f32,
    pub shots_harmless: bool,
    pub activate_collision_asteroids: bool,
    /// Optional only for legacy schema26 exports predating request bans.
    pub activate_mining: Option<bool>,
    /// Source Level.ActivateFogOfWar. Optional for historical exports.
    pub activate_fog_of_war: Option<bool>,
    /// Optional authored Map.MinZoom/MaxZoom orthographic half-heights.
    /// This is a graphical camera setting only and does not change the
    /// authoritative simulation broadphase or training observations.
    pub camera_zoom: Option<(f32, f32)>,
    /// Source Stage.UseMouseScrolling || UserProgressData.UseMouseScrolling.
    /// None is a legacy snapshot lacking the source user preference.
    pub camera_edge_scrolling: Option<bool>,
    /// Source Stage.ActivateHiveMind, required for autonomous native
    /// command requests. Legacy imports remain runnable but cannot
    /// silently assume gameplay AI eligibility.
    pub activate_hivemind: Option<bool>,
    pub hivemind_initial_command_delay_seconds: Option<i32>,
    pub has_obstacles: bool,
    pub command_settings: bees_gameplay::commands::HiveMindCommandSettings,
    pub entities: Vec<NativeAuthoredEntity>,
    source_episode: Option<NativeSourceEpisode>,
    /// Real participating Unity Agent instances and the active episode
    /// number. Schema 15 may legitimately have no group identities.
    pub source_policy_group: Option<NativeSourcePolicyGroup>,
    source_navigation: NativeSourceNavigation,
    pub fleet: NativeFleetInventory,
    /// Exact source ship, weapon and private communication metadata.
    pub authored_ship_perceptions: HashMap<i64, AuthoredShipPerception>,
    /// A physical Unity controller was active when captured. The caller
    /// must supply corresponding real NativeAuthoredPolicy for these IDs;
    /// this snapshot never fabricates a policy frame or decision period.
    pub active_policy_ship_ids: Vec<i64>,
    /// Source GameState.Squads order and exact members; no duplicate
    /// persistent fleet or second mutable native squad registry.
    pub source_squads: Vec<NativeSourceSquad>,
    /// Real active Unity agent/frame bindings only, never a random
    /// fabricated policy identity. Loaded model slots remain selected by
    /// the host's checkpoint schedule.
    pub source_policy_bindings: HashMap<i64, NativeSourcePolicyBinding>,
    source_policy_inputs: HashMap<i64, NativeSourcePolicyInputs>,
    source_vision: HashMap<i64, NativeSourceVision>,
    /// Original static collider health for source discovery TSV. Geometry
    /// and projectile collision remain owned by the canonical combat body.
    source_static_discovery_values: HashMap<i64, i32>,
    source_formation: HashMap<i64, NativeSourceFormation>,
}

#[derive(Clone, Copy, Debug)]
struct NativeSourceEpisode {
    bee_side: i32,
    human_side: i32,
    bee_starting_tsv: i32,
    human_starting_tsv: i32,
    timeout_seconds: f32,
    discovery_baselines: [[i32; 4]; 2],
}

#[derive(Clone, Copy, Debug)]
struct NativeSourceNavigation {
    map_width: f32,
    map_height: f32,
    frame: NavigationMapFrame,
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

/// Source-import launch settings: only genuine host-owned decisions stay
/// external. Policy frames/capabilities, navigation, and episode starting
/// reward baselines are taken from the verified Unity snapshot itself.
pub struct NativeExportedPolicyLevelSetup<A> {
    pub world_id: WorldId,
    pub level_id: i64,
    pub seed: u64,
    pub broadphase_cell_size: f32,
    pub decision_period: NonZeroU32,
    pub initial_full_asteroids: usize,
    pub groups: Option<NativeAuthoredGroups>,
    pub ship_catalog: Arc<ShipAssetCatalog>,
    /// Existing model slots selected by the learner/opponent scheduler,
    /// keyed by the Unity policy team (0 or 1), never inferred from side.
    pub model_slots: HashMap<u8, u32>,
    pub extraction: Option<bees_sim::combat_world::ExtractionZone>,
    pub survival: Option<bees_sim::episode_terminal::SurvivalObjective>,
    pub campaign_dialogue: Option<bees_gameplay::campaign_dialogue::CampaignDialogueSpec>,
    pub reinforcements: Vec<crate::reinforcements::NativeReinforcementPlan>,
    pub scripted_queues: Vec<crate::policy_only_metadata::NativeScriptedQueuePlan>,
    // Team-owned exploration starts fresh for the newly constructed
    // episode, and then follows the actual fixed-step ship movement.
    pub allocate_minion: A,
}

#[derive(Debug)]
pub enum NativePolicyOnlyLevelError {
    InvalidReinforcements(&'static str),
    InvalidScriptedCommands(&'static str),
    InvalidCampaignPlacement(&'static str),
    Snapshot(NativeSnapshotError),
    Metadata(PolicyOnlyMetadataError),
    InvalidControllerBinding(i64),
    InvalidSurvivalObjective,
    InvalidCampaignDialogue,
}

#[derive(Clone, Copy, Debug)]
pub struct NativeSourcePolicyBinding {
    pub team_id: u8,
    pub frame: PolicyFrame,
    pub movement_disabled: bool,
    pub cease_fire: bool,
}

/// The real Unity policy controller's static source fields. Dynamic
/// cooldowns, observed turret aiming and ship motion are instead read from
/// the same canonical CombatWorld during gameplay steps.
#[derive(Clone, Copy, Debug)]
struct NativeSourcePolicyInputs {
    controller: ControlledShipPerception,
    capability: CapabilitySource,
    parent_carrier_id: Option<i64>,
    own_weapons: [Option<SelfWeapon>; 5],
}

/// Runtime source Squad.ItemId is allocated once per Squad.Setup by the
/// Stage-wide monotonic Pool.ItemCount, even for reused pooled wrappers.
#[derive(Clone)]
pub struct NativeSourceSquad {
    pub command_bans: Option<Vec<bees_hivemind::dispatch::LocalCommandKind>>,
    pub is_carrier_squad: Option<bool>,
    /// Exact source shortcut index; generated minion squads are unnumbered.
    pub hotkey_number: Option<u32>,
    /// Unity GameState.OriginalSquadCounts[Side-1], never live count.
    pub original_squad_count: Option<u32>,
    pub hivemind: Option<bees_gameplay::level_host::NativeHiveMindSquadSource>,
    pub item_id: i64,
    pub side: i32,
    pub members: Vec<i64>,
    pub player_permissions: bees_gameplay::player_inputs::NativePlayerPermissions,
    /// Explicit source Squad.ShouldChase. Old schemas did not export it,
    /// so their behavior stays disabled rather than being fabricated.
    pub should_chase: bool,
    /// Unity Squad.CurrentSpeed is meaningful only while IsMatchingSpeed.
    /// Preserve the exact active setting rather than deriving it from
    /// the fastest surviving ship or the collision asset.
    pub matching_speed: Option<f32>,
    /// Source Squad.CeaseFire. None on older exports, which recorded
    /// per-ship cease-fire but not the squad action-box state.
    pub cease_fire: Option<bool>,
    pub shooting: bees_hivemind::protocol::StrategyIdentity,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeSourceGroupParticipant {
    pub agent_id: u64,
    pub ship_id: i64,
    pub side: i32,
    pub team_id: u8,
}

#[derive(Debug)]
pub struct NativeSourcePolicyGroup {
    pub episode_number: u32,
    pub movement_disabled_by_team: [bool; 2],
    pub participants: Vec<NativeSourceGroupParticipant>,
}

impl NativePhysicalScene {
    /// Apply an explicitly native game layout before any world is created.
    /// Retain the original source asset types, ship identities, formation
    /// offsets and Stage home positions. Each translated squad moves as one
    /// formation; gameplay still owns all subsequent motion and collisions.
    /// Validate the complete patch before mutating the imported template.
    pub fn apply_native_layout(
        &mut self, layout: &NativeLevelLayout,
        boxes: &BoxObstacleCatalog, polygons: &PolygonObstacleCatalog,
    ) -> Result<(), NativeSnapshotError> {
        use bees_sim::obstacle::ObstacleKind;
        let invalid = NativeSnapshotError::InvalidNativeLayout;
        if layout.remove_static_ids.is_empty() && layout.add_static_boxes.is_empty() &&
            layout.add_static_polygons.is_empty() && layout.translate_squads.is_empty() &&
            layout.clone_squads.is_empty() && layout.remove_squads.is_empty()
        {
            return Ok(());
        }
        // Imported training exports have already captured immutable source
        // reward denominators. Never apply any layout mutation to those.
        // A newly native-authored policy scene differs: static collider
        // placements must be installed BEFORE its explicitly authored
        // discovery baselines are checked and the group episode is frozen.
        // Its agent roster, positions and squad membership still cannot be
        // changed by the layout editor.
        if self.source_episode.is_some() || self.source_policy_group.is_some() {
            return Err(invalid(
                "native layout edits require a Level without frozen policy reward baselines",
            ));
        }
        if !self.source_policy_bindings.is_empty() && (
            !layout.translate_squads.is_empty() ||
            !layout.clone_squads.is_empty() ||
            !layout.remove_squads.is_empty()
        ) {
            return Err(invalid(
                "native policy layouts may only author initial static collider geometry",
            ));
        }
        let mut removed = HashSet::new();
        for &id in &layout.remove_static_ids {
            if id <= 0 || !removed.insert(id) {
                return Err(invalid("duplicate or invalid removed obstacle identity"));
            }
            let found = self.entities.iter().find(|entity| entity.entity_id() == id);
            let is_static = match found {
                Some(NativeAuthoredEntity::BoxObstacle { prefab_name, .. }) =>
                    boxes.by_name(prefab_name)
                        .is_some_and(|asset| asset.kind == ObstacleKind::StaticObstacle),
                Some(NativeAuthoredEntity::PolygonObstacle { .. }) => true,
                _ => false,
            };
            if !is_static {
                return Err(invalid("only present static obstacles may be removed"));
            }
        }

        let highest_original_id = self.entities.iter()
            .map(NativeAuthoredEntity::entity_id).max().unwrap_or(0);
        let mut new_ids = HashSet::new();
        for placement in &layout.add_static_boxes {
            if placement.entity_id <= highest_original_id ||
                placement.entity_id <= 0 || placement.entity_id == i64::MAX ||
                !new_ids.insert(placement.entity_id) {
                return Err(invalid("new obstacle identities must be unique and above the captured world"));
            }
            let Some(asset) = boxes.by_name(&placement.prefab_name) else {
                return Err(invalid("new obstacle requires a verified source box prefab"));
            };
            if asset.kind != ObstacleKind::StaticObstacle || asset.authored_health <= 0 {
                return Err(invalid("native box placement must be a real damage-free static obstacle"));
            }
            let position = Vec2::new(placement.position[0], placement.position[1]);
            let pose = Pose {
                position, rotation_degrees: placement.rotation_degrees,
            };
            // Like Unity-authored obstacles, a box may overhang the map
            // boundary. Only finite geometry is mandatory; collision and
            // navigation use the source Level's actual world bounds.
            if !position.is_finite() || !pose.rotation_degrees.is_finite() {
                return Err(invalid("new obstacle has nonfinite authored placement"));
            }
            let prepared = if let Some(corners) = placement.local_corners {
                asset.prepare_spawn_live_box(placement.entity_id, pose,
                    corners.map(|v| Vec2::new(v[0], v[1])))
            } else {
                asset.prepare_spawn(placement.entity_id, Some(pose))
            };
            if prepared.is_err() {
                return Err(invalid("native box collider corners are invalid"));
            }
        }
        for placement in &layout.add_static_polygons {
            if placement.entity_id <= highest_original_id ||
                placement.entity_id <= 0 || placement.entity_id == i64::MAX ||
                !new_ids.insert(placement.entity_id) {
                return Err(invalid("new polygon identities must be unique and above the captured world"));
            }
            let position = Vec2::new(placement.position[0], placement.position[1]);
            let pose = Pose { position, rotation_degrees: placement.rotation_degrees };
            if !position.is_finite() || !pose.rotation_degrees.is_finite() {
                return Err(invalid("new polygon has nonfinite authored placement"));
            }
            if placement.local_vertices.as_ref().is_some_and(|vertices|
                !(3..=8192).contains(&vertices.len())
            ) {
                return Err(invalid("native polygon path must contain 3 to 8192 vertices"));
            }
            let prepared = match &placement.local_vertices {
                Some(vertices) => polygons.prepare_spawn_live_polygon(
                    placement.entity_id, pose,
                    vertices.iter().map(|v| Vec2::new(v[0], v[1])).collect(),
                ),
                None => polygons.prepare_spawn(placement.entity_id, Some(pose)),
            };
            if prepared.is_err() {
                return Err(invalid("native polygon collider vertices are invalid"));
            }
        }

        let mut translated = HashSet::new();
        let mut placements = HashMap::new();
        for edit in &layout.translate_squads {
            let delta = Vec2::new(edit.by[0], edit.by[1]);
            if edit.squad_item_id <= 0 || !translated.insert(edit.squad_item_id) ||
                !delta.is_finite()
            {
                return Err(invalid("invalid or repeated native squad translation"));
            }
            let squad = self.source_squads.iter().find(|squad|
                squad.item_id == edit.squad_item_id
            ).ok_or(invalid("native squad translation references an unknown source squad"))?;
            for &id in &squad.members {
                let ship = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == id => Some(ship),
                    _ => None,
                }).ok_or(invalid("native squad translation requires a live authored ship"))?;
                // Move only the initial placement; never turn a captured
                // in-flight ship into a half-teleported physics/controller.
                if ship.motion.is_moving || ship.motion.has_target_coordinates ||
                    ship.motion.body_velocity.length_squared() != 0.0
                {
                    return Err(invalid("native placement requires stationary source ships"));
                }
                let destination = ship.motion.position + delta;
                if !destination.is_finite() ||
                    destination.x < self.bounds.min.x ||
                    destination.x > self.bounds.max.x ||
                    destination.y < self.bounds.min.y ||
                    destination.y > self.bounds.max.y ||
                    placements.insert(id, destination).is_some()
                {
                    return Err(invalid("native squad placement duplicates a ship or leaves the Level bounds"));
                }
            }
        }

        // Stage complete copies before changing the original Level. Unlike
        // a position-only duplicate, each native ship needs its own physical
        // ID, fleet value, current sight, command formation, and targeting
        // history. Pull the exact weapon mounts/capabilities from one verified
        // source template; never substitute hull-class guesses.
        let highest_original_squad_id = self.source_squads.iter()
            .map(|squad| squad.item_id).max().unwrap_or(0);
        let mut new_squad_ids = HashSet::new();
        let mut cloned_squads = Vec::new();
        for clone in &layout.clone_squads {
            let delta = Vec2::new(clone.by[0], clone.by[1]);
            if !delta.is_finite() || clone.source_squad_item_id <= 0 ||
                clone.new_squad_item_id <= highest_original_squad_id ||
                clone.new_squad_item_id == i64::MAX ||
                !new_squad_ids.insert(clone.new_squad_item_id)
            {
                return Err(invalid("native clone needs a unique new Stage squad identity and a finite offset"));
            }
            let template = self.source_squads.iter().find(|squad|
                squad.item_id == clone.source_squad_item_id
            ).ok_or(invalid("native clone references an unknown source squad"))?;
            if template.members.is_empty() || template.members.len() > 1024 ||
                template.members.len() != clone.ship_ids.len()
            {
                return Err(invalid("native clone must specify one new ship ID per original squad member"));
            }
            let mut ships = Vec::with_capacity(template.members.len());
            for (&source_id, &new_id) in template.members.iter().zip(&clone.ship_ids) {
                if new_id <= highest_original_id || new_id <= 0 ||
                    new_id == i64::MAX || !new_ids.insert(new_id) ||
                    self.fleet.get(new_id).is_some()
                {
                    return Err(invalid("native clone ship IDs must be unique and above all captured entities"));
                }
                let source = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == source_id => Some(ship),
                    _ => None,
                }).ok_or(invalid("native clone template ship is missing"))?;
                // These carry source-only relationship IDs and extra
                // lifecycle owners. Until fully native special/child fleet
                // authoring exists, never clone those relationships blindly.
                if source.policy.is_some() || source.motion.is_policy_controlled ||
                    source.special.is_some() || source.special_weapon.is_some() ||
                    source.drone_carrier_id.is_some() || source.vitals.is_dead() ||
                    source.motion.is_moving || source.motion.has_target_coordinates ||
                    source.motion.body_velocity.length_squared() != 0.0
                {
                    return Err(invalid("native squad clones require stationary ordinary game ships, not policy, special or carrier children"));
                }
                let position = source.motion.position + delta;
                if !position.is_finite() ||
                    position.x < self.bounds.min.x || position.x > self.bounds.max.x ||
                    position.y < self.bounds.min.y || position.y > self.bounds.max.y
                {
                    return Err(invalid("native cloned ship placement is outside the authored map"));
                }
                let mut new_ship = source.clone();
                new_ship.entity_id = new_id;
                new_ship.motion.position = position;
                new_ship.fleet_ship_id = None;
                // Fresh Ship.Setup clears the source combat/kill history.
                // The clone has never latched a valid target-bounds entry at
                // its new map location or recorded a target victim.
                if let Some(geometry) = &mut new_ship.target_geometry {
                    geometry.bounds_latched = false;
                }
                if let Some(activity) = &mut new_ship.combat_activity {
                    activity.elapsed_seconds = None;
                }
                if let Some(rank) = &mut new_ship.weapon_rank_source {
                    rank.stats.ship_id = new_id;
                    rank.stats.last_killed = 0;
                    rank.stats.fleet_damage_done = 0;
                    rank.fleet_owner_ship_id = new_id;
                }
                let mut perception = *self.authored_ship_perceptions.get(&source_id)
                    .ok_or(invalid("native clone is missing source weapon/perception metadata"))?;
                perception.minerals_mined_this_level = 0;
                if let Some(message) = &mut perception.allied_communication {
                    *message = [0.0; 4];
                }
                let vision = *self.source_vision.get(&source_id)
                    .ok_or(invalid("native clone is missing source vision registration"))?;
                let formation = *self.source_formation.get(&source_id)
                    .ok_or(invalid("native clone is missing source formation offsets"))?;
                ships.push((new_ship, perception, vision, formation));
            }
            let mut squad = template.clone();
            squad.item_id = clone.new_squad_item_id;
            squad.members = clone.ship_ids.clone();
            // The source's hotkey belongs to the original fleet. New native
            // squads remain selectable by actual roster identity instead
            // of masquerading as an old numbered player squad.
            squad.hotkey_number = None;
            squad.original_squad_count = None;
            cloned_squads.push((squad, ships));
        }

        // Whole-squad removal is a setup-time operation. Never leave a
        // source ship's independent perception/fleet/formation history
        // resident after its squad has disappeared. Reject missing source
        // ownership and any surviving carrier-child references up front.
        let mut omitted_squads = HashSet::new();
        let mut omitted_ship_ids = HashSet::new();
        for &id in &layout.remove_squads {
            if id <= 0 || !omitted_squads.insert(id) {
                return Err(invalid("duplicate or invalid native squad removal identity"));
            }
            let source = self.source_squads.iter().find(|squad|
                squad.item_id == id
            ).ok_or(invalid("native squad removal references an unknown source squad"))?;
            for &ship_id in &source.members {
                let ship = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == ship_id => Some(ship),
                    _ => None,
                }).ok_or(invalid("removed native squad has an unknown ship"))?;
                // A source-only capability may own a live child, emitter or
                // other relationship outside the actual squad. Keep the
                // initial editor contract small and safe, not an orphaned
                // half-deleted special ship.
                if ship.policy.is_some() || ship.special.is_some() ||
                    ship.special_weapon.is_some() || ship.drone_carrier_id.is_some()
                {
                    return Err(invalid("native squad removal supports ordinary game ships only"));
                }
                if !omitted_ship_ids.insert(ship_id) {
                    return Err(invalid("native squad removal shares a source ship"));
                }
            }
        }
        if layout.translate_squads.iter().any(|edit|
            omitted_squads.contains(&edit.squad_item_id)
        ) {
            return Err(invalid("cannot translate and remove the same native source squad"));
        }
        if omitted_squads.len() == self.source_squads.len() && cloned_squads.is_empty() {
            return Err(invalid("native layout would remove every playable squad"));
        }
        if self.entities.iter().any(|entity| match entity {
            NativeAuthoredEntity::Ship(ship) if !omitted_ship_ids.contains(&ship.entity_id) =>
                ship.drone_carrier_id.is_some_and(|id| omitted_ship_ids.contains(&id)) ||
                matches!(&ship.special, Some(NativeAuthoredShipSpecial::Striker { carrier, .. })
                    if carrier.carrier_id.is_some_and(|id| omitted_ship_ids.contains(&id))),
            _ => false,
        }) {
            return Err(invalid("native squad removal would orphan a surviving carrier child"));
        }

        if !removed.is_empty() {
            self.entities.retain(|entity| !removed.contains(&entity.entity_id()));
            self.source_static_discovery_values.retain(|id, _| !removed.contains(id));
        }
        for entity in &mut self.entities {
            if let NativeAuthoredEntity::Ship(ship) = entity {
                if let Some(&position) = placements.get(&ship.entity_id) {
                    ship.motion.position = position;
                }
            }
        }
        for placement in &layout.add_static_boxes {
            let asset = boxes.by_name(&placement.prefab_name)
                .expect("validated immutable box prefab");
            self.source_static_discovery_values
                .insert(placement.entity_id, asset.authored_health);
            self.entities.push(NativeAuthoredEntity::BoxObstacle {
                entity_id: placement.entity_id,
                prefab_name: placement.prefab_name.clone(),
                pose: Some(Pose {
                    position: Vec2::new(placement.position[0], placement.position[1]),
                    rotation_degrees: placement.rotation_degrees,
                }),
                live_corners: placement.local_corners.map(|corners|
                    corners.map(|v| Vec2::new(v[0], v[1]))),
            });
        }
        for placement in &layout.add_static_polygons {
            self.source_static_discovery_values
                .insert(placement.entity_id, polygons.authored_health);
            self.entities.push(NativeAuthoredEntity::PolygonObstacle {
                entity_id: placement.entity_id,
                pose: Some(Pose {
                    position: Vec2::new(placement.position[0], placement.position[1]),
                    rotation_degrees: placement.rotation_degrees,
                }),
                live_vertices: placement.local_vertices.as_ref().map(|vertices|
                    vertices.iter().map(|v| Vec2::new(v[0], v[1])).collect()),
            });
        }
        if !layout.add_static_boxes.is_empty() || !layout.add_static_polygons.is_empty() {
            self.has_obstacles = true;
        }
        if !omitted_ship_ids.is_empty() {
            self.entities.retain(|entity|
                !omitted_ship_ids.contains(&entity.entity_id()));
            self.source_squads.retain(|squad|
                !omitted_squads.contains(&squad.item_id));
            for id in omitted_ship_ids {
                self.authored_ship_perceptions.remove(&id);
                self.source_vision.remove(&id);
                self.source_formation.remove(&id);
                self.fleet.omit_initial_ship(id);
            }
        }
        for (squad, ships) in cloned_squads {
            for (ship, perception, vision, formation) in ships {
                let id = ship.entity_id;
                self.fleet.register_spawned_ship(NativeFleetShipValue {
                    ship_id: id,
                    has_persistent_fleet_value: false,
                    minerals_mined_this_level: 0,
                }).expect("native clone IDs and fresh fleet values prevalidated");
                self.authored_ship_perceptions.insert(id, perception);
                self.source_vision.insert(id, vision);
                self.source_formation.insert(id, formation);
                self.entities.push(NativeAuthoredEntity::Ship(ship));
            }
            self.source_squads.push(squad);
        }
        Ok(())
    }

    /// Construct the same Pathfinder.Scale=4 navigation grid and complete
    /// map affine basis used by this Unity Level. The canonical game world
    /// owns the live obstacle colliders; refresh static/dynamic clearance
    /// against that world before command-driven path requests.
    pub fn exported_navigation_runtime(
        &self,
    ) -> Result<NavigationRuntime, NativeSnapshotError> {
        let nav = self.source_navigation;
        let grid = NavigationGrid::new(nav.map_width, nav.map_height)
            .map_err(NativeSnapshotError::Navigation)?;
        NavigationRuntime::new(grid, nav.frame)
            .map_err(NativeSnapshotError::Navigation)
    }

    pub fn gameplay_sides(&self) -> [i32; 2] {
        [self.bee_side, self.human_side]
    }

    /// Fresh native coordinator with the exact source *starting* TSV and
    /// all four source-defined discovery denominators per faction. This
    /// starts a NEW native episode; it does not replay Unity's consumed
    /// rewards, action trajectories or elapsed time.
    pub fn exported_episode(
        &self,
    ) -> Result<NativeEpisodeRewardCoordinator, NativeSnapshotError> {
        let source = self.source_episode
            .ok_or(NativeSnapshotError::InvalidEpisodeSource)?;
        let mut episode = NativeEpisodeRewardCoordinator::new(
            source.bee_side, source.human_side,
            source.bee_starting_tsv, source.human_starting_tsv,
            source.timeout_seconds,
        ).map_err(NativeSnapshotError::Episode)?;
        episode.configure_discovery(source.discovery_baselines)
            .map_err(NativeSnapshotError::Episode)?;
        Ok(episode)
    }

    /// The single supported source-scene launch path for a policy-only
    /// native Level. All fallible source/model/side validation is performed
    /// before binding any physical controller. Scripted/unbound RL action
    /// producers must use a full gameplay adapter, not silently stop moving.
    pub fn into_exported_policy_only_level<A>(
        mut self,
        setup: NativeExportedPolicyLevelSetup<A>,
    ) -> Result<NativeAuthoredLevel<NativePolicyOnlyMetadata, A>,
        NativePolicyOnlyLevelError>
    {
        for &ship_id in &self.active_policy_ship_ids {
            if !self.source_policy_bindings.contains_key(&ship_id) {
                return Err(NativePolicyOnlyLevelError::InvalidControllerBinding(
                    ship_id,
                ));
            }
        }
        let [bee_side, human_side] = self.gameplay_sides();
        let mut episode = if self.source_episode.is_some() {
            self.exported_episode()
        } else if setup.groups.is_some() {
            Err(NativeSnapshotError::InvalidEpisodeSource)
        } else {
            NativeEpisodeRewardCoordinator::new_game(bee_side, human_side)
                .map_err(NativeSnapshotError::Episode)
        }.map_err(NativePolicyOnlyLevelError::Snapshot)?;
        use bees_gameplay::campaign_dialogue::CampaignDialogueSpec;
        match setup.campaign_dialogue {
            Some(CampaignDialogueSpec::TitaniaBeenoculars { player_side, .. })
                if !setup.survival.is_some_and(|objective| objective.defending_side == player_side) =>
                return Err(NativePolicyOnlyLevelError::InvalidCampaignDialogue),
            Some(CampaignDialogueSpec::TitaniaMinesweeper { .. }) if setup.survival.is_some() =>
                return Err(NativePolicyOnlyLevelError::InvalidCampaignDialogue),
            _ => {}
        }
        if let Some(objective) = setup.survival {
            let target_is_live_ally = self.entities.iter().any(|entity| matches!(
                entity,
                NativeAuthoredEntity::Ship(ship) if ship.entity_id == objective.protected_ship_id &&
                    ship.side == objective.defending_side && !ship.vitals.is_dead()
            ));
            if setup.extraction.is_some() || !target_is_live_ally {
                return Err(NativePolicyOnlyLevelError::InvalidSurvivalObjective);
            }
            episode.arm_survival(objective, self.fixed_dt_seconds)
                .map_err(NativeSnapshotError::Episode)
                .map_err(NativePolicyOnlyLevelError::Snapshot)?;
            if matches!(setup.campaign_dialogue,
                Some(CampaignDialogueSpec::TitaniaBeenoculars { .. }))
            {
                // Titania2EnhancementGuard / StartTitania2Enhancements:
                // the target is a real HumanTarget, made fully healthy at
                // 10,000 HP when the briefing ends. Native play holds
                // physics during the briefing, so applying that authored
                // change once during construction is equivalent. Generic
                // survival objectives keep their imported ship health.
                let target = self.entities.iter_mut().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship)
                        if ship.entity_id == objective.protected_ship_id => Some(ship),
                    _ => None,
                }).ok_or(NativePolicyOnlyLevelError::InvalidCampaignDialogue)?;
                if target.ship_type != bees_sim::ship_assets::HUMAN_TARGET_TYPE {
                    return Err(NativePolicyOnlyLevelError::InvalidCampaignDialogue);
                }
                target.vitals.set_full_health_capacity(10_000)
                    .map_err(|_| NativePolicyOnlyLevelError::InvalidCampaignDialogue)?;
                target.original_health = 10_000;
                // Unity CreateHumanTarget(Titania2Center) runs when the
                // briefing ends. The native briefing holds physics, so
                // placing this same real hull during Level construction
                // preserves the gameplay start without a second spawn.
                target.motion.position = bees_gameplay::campaign_dialogue::TITANIA_II_CENTER;
            }
        }
        let agents = self.exported_policy_agents(&setup.model_slots)
            .map_err(NativePolicyOnlyLevelError::Snapshot)?;
        if agents.iter().any(|agent|
            agent.side != bee_side && agent.side != human_side
        ) {
            return Err(NativePolicyOnlyLevelError::Snapshot(
                NativeSnapshotError::InvalidEpisodeSource,
            ));
        }
        let sides = vec![
            AuthoredPolicySide {
                side: bee_side,
                exploration: TeamExplorationGrid::new(),
            },
            AuthoredPolicySide {
                side: human_side,
                exploration: TeamExplorationGrid::new(),
            },
        ];
        let navigation = self.exported_navigation_runtime()
            .map_err(NativePolicyOnlyLevelError::Snapshot)?;
        self.bind_exported_policy_ships()
            .map_err(NativePolicyOnlyLevelError::Snapshot)?;
        let dt = self.fixed_dt_seconds;
        let mut authored = self.into_policy_only_level(NativePolicyOnlyLevelSetup {
            world_id: setup.world_id,
            level_id: setup.level_id,
            seed: setup.seed,
            broadphase_cell_size: setup.broadphase_cell_size,
            decision_period: setup.decision_period,
            // The snapshot already imports every existing full asteroid.
            // Spawning extra randomized ones would duplicate the authored
            // initial scene and invalidate the recorded discovery totals.
            initial_full_asteroids: 0,
            episode,
            groups: setup.groups,
            navigation,
            ship_catalog: setup.ship_catalog,
            policy_agents: agents,
            sides,
            allocate_minion: setup.allocate_minion,
        })?;
        authored.extraction = setup.extraction;
        authored.campaign_dialogue = setup.campaign_dialogue;
        // Validate scripted ownership against the complete source roster
        // before wave extraction removes deferred squads from the initial
        // scene. Deferred orders activate only after real deployment.
        let deferred_squads = setup.reinforcements.iter()
            .flat_map(|wave| wave.squad_item_ids.iter().copied())
            .collect::<std::collections::HashSet<_>>();
        let entry_squads = setup.reinforcements.iter()
            .filter(|wave| wave.entry.is_some_and(|position|
                !position.unity_equal(Vec2::ZERO)))
            .flat_map(|wave| wave.squad_item_ids.iter().copied())
            .collect::<std::collections::HashSet<_>>();
        let known_ships = authored.entities.iter().filter_map(|entity| match entity {
            NativeAuthoredEntity::Ship(ship) => Some(ship.entity_id),
            _ => None,
        }).collect::<std::collections::HashSet<_>>();
        authored.metadata.stage_scripted_queues(
            &authored.squads, setup.scripted_queues, &deferred_squads,
            &entry_squads, &known_ships, dt, authored.campaign_dialogue,
        ).map_err(NativePolicyOnlyLevelError::InvalidScriptedCommands)?;
        authored.stage_reinforcements(setup.reinforcements, dt)
            .map_err(NativePolicyOnlyLevelError::InvalidReinforcements)?;
        if let Some(CampaignDialogueSpec::TitaniaBeenoculars { player_side, .. }) =
            authored.campaign_dialogue
        {
            // Source moves the original player fleet before briefing and
            // before new enemy squads are added. Deferred wave ships were
            // removed above, so only the actual starting roster is staged.
            authored.stage_titania2_player_fleet(player_side)
                .map_err(NativePolicyOnlyLevelError::InvalidCampaignPlacement)?;
        }
        Ok(authored)
    }

    /// Compose observation-capable policy agents exclusively from verified
    /// source-bound Unity controllers. Only *model slots* remain external:
    /// the host must resolve the requested checkpoint/learner matchup
    /// before invoking this function. An unknown team is an error, not
    /// a default policy or substituted model.
    pub fn exported_policy_agents(
        &self, model_slots: &HashMap<u8, u32>,
    ) -> Result<Vec<AuthoredPolicyAgent>, NativeSnapshotError> {
        let mut ids: Vec<_> = self.source_policy_bindings.keys()
            .copied().collect();
        ids.sort_unstable();
        let mut agents = Vec::with_capacity(ids.len());
        for ship_id in ids {
            let binding = self.source_policy_bindings.get(&ship_id)
                .expect("enumerated source-bound ship");
            let input = self.source_policy_inputs.get(&ship_id)
                .ok_or(NativeSnapshotError::InvalidPolicySource(ship_id))?;
            let model_slot = *model_slots.get(&binding.team_id)
                .ok_or(NativeSnapshotError::MissingModelSlot(binding.team_id))?;
            let side = self.entities.iter().find_map(|entity| match entity {
                NativeAuthoredEntity::Ship(ship)
                    if ship.entity_id == ship_id => Some(ship.side),
                _ => None,
            }).ok_or(NativeSnapshotError::UnknownPolicyShip(ship_id))?;
            let source_group_agent_id = self.source_policy_group.as_ref()
                .and_then(|group| group.participants.iter().find(|member|
                    member.ship_id == ship_id
                )).map(|member| member.agent_id);
            agents.push(AuthoredPolicyAgent {
                ship_id,
                side,
                source_group_agent_id,
                route: NativePolicyRoute {
                    team_id: binding.team_id, model_slot,
                },
                // Team policies change across self-play episodes. Read the
                // current binding, not the immutable first-episode source
                // controller's movement flag, so observations and physical
                // policy commands agree after every team permutation.
                controller: ControlledShipPerception {
                    movement_disabled_for_team: binding.movement_disabled,
                    ..input.controller
                },
                capability: input.capability,
                parent_carrier_id: input.parent_carrier_id,
                own_weapons: input.own_weapons,
            });
        }
        Ok(agents)
    }

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
            if self.source_policy_bindings.get(&agent.ship_id)
                .is_some_and(|source| source.team_id != agent.route.team_id) ||
                bound_sides.remove(&agent.ship_id) != Some(agent.side) {
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
        for &ship_id in self.source_policy_bindings.keys() {
            if !self.entities.iter().any(|entity| match entity {
                NativeAuthoredEntity::Ship(ship) =>
                    ship.entity_id == ship_id && ship.policy.is_some(),
                _ => false,
            }) {
                return Err(NativePolicyOnlyLevelError::InvalidControllerBinding(
                    ship_id,
                ));
            }
        }

        let squads = self.squads_for_level(setup.level_id)
            .map_err(NativePolicyOnlyLevelError::Snapshot)?;
        let queen_templates = self.entities.iter().filter_map(|entity| match entity {
            NativeAuthoredEntity::Ship(ship) => match &ship.special {
                Some(NativeAuthoredShipSpecial::Queen { minions }) =>
                    Some((ship.entity_id, Arc::clone(minions))),
                _ => None,
            },
            _ => None,
        }).collect();
        let beacon_templates = self.entities.iter().filter_map(|entity| match entity {
            NativeAuthoredEntity::Ship(ship) => match &ship.special {
                Some(NativeAuthoredShipSpecial::Scout { beacon, .. }) =>
                    Some((ship.entity_id, Arc::clone(beacon))),
                _ => None,
            },
            _ => None,
        }).collect();
        let mut metadata = NativePolicyOnlyMetadata::new(
            self.bounds,
            setup.navigation,
            self.activate_collision_asteroids,
            self.fleet,
            setup.ship_catalog,
            self.authored_ship_perceptions,
            self.source_vision,
            self.source_static_discovery_values,
            setup.policy_agents,
            setup.sides,
            beacon_templates,
            NativeSourcePlayerInputs {
                human_side: self.human_side,
                has_obstacles: self.has_obstacles,
                ships: self.source_formation,
                commands: self.source_squads.iter().zip(&squads).map(|(source, squad)|
                    (squad.owner, bees_gameplay::player_inputs::NativePlayerCommandInputs {
                        is_carrier_squad: source.is_carrier_squad,
                        shooting: source.shooting, settings: self.command_settings,
                    })).collect(),
                squads: self.source_squads.iter().zip(&squads)
                    .map(|(source, squad)| (squad.owner, source.player_permissions)).collect(),
                chasing: self.source_squads.iter().zip(&squads)
                    .filter_map(|(source, squad)| source.should_chase.then_some(squad.owner))
                    .collect(),
                matching_speeds: self.source_squads.iter().zip(&squads)
                    .filter_map(|(source, squad)| source.matching_speed
                        .map(|speed| (squad.owner, speed)))
                    .collect(),
                cease_fire: self.source_squads.iter().zip(&squads)
                    .filter_map(|(source, squad)| source.cease_fire
                        .map(|enabled| (squad.owner, enabled)))
                    .collect(),
            },
        ).map_err(NativePolicyOnlyLevelError::Metadata)?;
        metadata.install_queen_templates(queen_templates)
            .map_err(NativePolicyOnlyLevelError::Metadata)?;
        metadata.targeting_context = self.targeting_context;
        metadata.activate_mining = self.activate_mining;
        metadata.activate_fog_of_war = self.activate_fog_of_war;
        metadata.hivemind_active = self.activate_hivemind;
        metadata.hivemind_initial_delay_seconds = self.hivemind_initial_command_delay_seconds;
        metadata.hivemind_starting_positions = self.starting_positions;
        metadata.hivemind_squads = self.source_squads.iter().zip(&squads)
            .filter_map(|(source, squad)| source.hivemind.map(|inputs| (squad.owner, inputs)))
            .collect();
        Ok(NativeAuthoredLevel {
            reserved_ship_ids: Vec::new(),
            extraction: None,
            campaign_dialogue: None,
            weapon_rank_frame: if self.schema_version >= 23 { self.source_frame_count } else { None },
            level_geometry: (self.schema_version >= 22).then_some(NativeLevelGeometry {
                map_bounds: self.bounds, has_obstacles: self.has_obstacles,
            }),
            world_id: setup.world_id,
            seed: setup.seed,
            broadphase_bounds: self.bounds,
            broadphase_cell_size: setup.broadphase_cell_size,
            decision_period: setup.decision_period,
            shots_harmless: self.shots_harmless,
            entities: self.entities,
            squads,
            initial_full_asteroids: setup.initial_full_asteroids,
            spawn_map_geometry: Some((
                self.bounds,
                Vec2::new(
                    (self.source_navigation.map_width / 2.0).floor(),
                    (self.source_navigation.map_height / 2.0).floor(),
                ),
            )),
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
                command_bans: source.command_bans.clone(),
                hotkey_number: source.hotkey_number,
                original_squad_count: source.original_squad_count,
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
    /// Attach every *exported* real Unity controller and its actual
    /// randomized episode frame. Return the ship IDs and source team IDs
    /// needed to select genuine loaded model slots, never placeholder
    /// policy outputs. Preflight avoids partial mutation on ordinary
    /// duplicate-bind failures; a failed snapshot must be discarded.
    pub fn bind_exported_policy_ships(
        &mut self,
    ) -> Result<Vec<(i64, u8)>, NativeSnapshotError> {
        let mut source: Vec<_> = self.source_policy_bindings.iter()
            .map(|(&id, &binding)| (id, binding))
            .collect();
        source.sort_unstable_by_key(|entry| entry.0);
        for &(ship_id, _) in &source {
            let ship = self.entities.iter().find_map(|entity| match entity {
                NativeAuthoredEntity::Ship(ship)
                    if ship.entity_id == ship_id => Some(ship),
                _ => None,
            }).ok_or(NativeSnapshotError::UnknownPolicyShip(ship_id))?;
            if ship.policy.is_some() {
                return Err(NativeSnapshotError::AlreadyBound(ship_id));
            }
        }
        for &(ship_id, binding) in &source {
            self.bind_policy_ship(
                ship_id, binding.frame, binding.movement_disabled,
                binding.cease_fire,
            )?;
        }
        Ok(source.into_iter()
            .map(|(ship_id, binding)| (ship_id, binding.team_id))
            .collect())
    }

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
        ship.cease_fire = cease_fire;
        ship.policy = Some(NativeAuthoredPolicy {
            frame, movement_disabled,
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
        let catalog = ShipAssetCatalog::embedded().map_err(NativeSnapshotError::ShipCatalog)?;
        Self::parse_with_ship_catalog(json, &catalog)
    }

    /// One native scene authoring boundary for the game and headless runner.
    /// Legacy Unity snapshots retain their original strict importer while
    /// explicit native_game_schema files construct the same owned world
    /// without a Unity scene, editor, GameObject or source-only defaults.
    pub fn parse_scene_with_assets(
        json: &str, assets: &NativeSceneAssets,
    ) -> Result<Self, NativeSnapshotError> {
        #[derive(Deserialize)]
        struct Format { #[serde(default)] native_game_schema: Option<u32> }
        let format: Format = serde_json::from_str(json)
            .map_err(NativeSnapshotError::Malformed)?;
        if format.native_game_schema.is_some() {
            Self::parse_native_game_scene(
                json, assets.ship_catalog(), assets.box_catalog(),
                assets.polygon_catalog(),
            )
        } else {
            Self::parse_with_ship_catalog(json, assets.ship_catalog())
        }
    }

    /// Reuse the Stage's immutable hull catalog when many arenas share a source.
    pub fn parse_with_ship_catalog(json: &str, catalog: &ShipAssetCatalog) -> Result<Self, NativeSnapshotError> {
        let raw: UnitySnapshot = serde_json::from_str(json)
            .map_err(NativeSnapshotError::Malformed)?;
        if !(20..=SNAPSHOT_VERSION).contains(&raw.schema_version) {
            return Err(NativeSnapshotError::UnsupportedSchema(raw.schema_version));
        }
        if raw.schema_version >= 25 && (raw.targeting_context.is_none() || raw.ships.iter().any(|ship|
            ship.combat_activity.is_none() || ship.special.beacon.as_ref().is_some_and(|beacon| beacon.combat_activity.is_none()))) {
            return Err(NativeSnapshotError::MissingTargetingSource);
        }
        let targeting_context = raw.targeting_context.map(|context|
            bees_gameplay::squad_targeting::NativeTargetingContext { has_player: context.has_player, user_side: context.user_side });
        if targeting_context.is_some_and(|context| context.has_player && context.user_side <= 0) {
            return Err(NativeSnapshotError::MissingTargetingSource);
        }
        if raw.schema_version >= 27 &&
            (raw.activate_hivemind.is_none() || raw.activate_mining.is_none() ||
             raw.hivemind_initial_command_delay_seconds.is_none() ||
             raw.starting_positions.is_none()) {
            return Err(NativeSnapshotError::MissingHiveMindSource);
        }
        let original_squad_counts = if raw.schema_version >= 29 {
            match raw.original_squad_counts.as_deref() {
                Some([first, second]) if *first >= 0 && *second >= 0 =>
                    Some([*first as u32, *second as u32]),
                _ => return Err(NativeSnapshotError::InvalidSquad(0)),
            }
        } else { None };
        let starting_positions = match raw.starting_positions.as_deref() {
            Some([first, second]) => {
                let pair = [first.into_vec(), second.into_vec()];
                if pair.iter().any(|position| !position.is_finite()) {
                    return Err(NativeSnapshotError::MissingHiveMindSource);
                }
                Some(pair)
            },
            None if raw.schema_version < 27 => None,
            _ => return Err(NativeSnapshotError::MissingHiveMindSource),
        };
        if raw.schema_version >= 23 && raw.source_frame_count.is_none() {
            return Err(NativeSnapshotError::MissingWeaponRankingClock);
        }
        let command_settings: bees_gameplay::commands::HiveMindCommandSettings = raw.command_settings.into();
        if !command_settings.valid() { return Err(NativeSnapshotError::InvalidCommandSettings); }
        if raw.bee_side <= 0 || raw.human_side <= 0 || raw.bee_side == raw.human_side ||
            raw.beehive_size_pixels.x == 0 || raw.beehive_size_pixels.y == 0 {
            return Err(NativeSnapshotError::InvalidLevelBounds);
        }
        let (source_episode, source_policy_group) = if raw.has_policy_episode {
            let raw_episode = raw.policy_episode
                .ok_or(NativeSnapshotError::InvalidEpisodeSource)?;
            let baselines = raw_episode.discovery_baselines
                .ok_or(NativeSnapshotError::InvalidEpisodeSource)?;
            if baselines.len() != 8 ||
                raw_episode.bee_side != raw.bee_side ||
                raw_episode.human_side != raw.human_side ||
                raw_episode.bee_side == raw_episode.human_side ||
                raw_episode.bee_starting_tsv <= 0 ||
                raw_episode.human_starting_tsv <= 0 ||
                !raw_episode.timeout_seconds.is_finite() ||
                raw_episode.timeout_seconds <= 0.0 ||
                baselines.iter().any(|value| *value < 0)
            {
                return Err(NativeSnapshotError::InvalidEpisodeSource);
            }
            let source_group = if raw.schema_version >= 16 {
                if raw_episode.episode_number == 0 {
                    return Err(NativeSnapshotError::InvalidGroupSource);
                }
                let movement_disabled_by_team = match (
                    raw_episode.team0_movement_disabled,
                    raw_episode.team1_movement_disabled,
                ) {
                    (Some(team0), Some(team1)) => [team0, team1],
                    _ => return Err(NativeSnapshotError::InvalidGroupSource),
                };
                let mut ids = HashSet::new();
                let mut participants = Vec::with_capacity(
                    raw_episode.group_participants.len()
                );
                for member in raw_episode.group_participants {
                    let expected_team = team_id_for_side(
                        raw_episode.bee_side, raw_episode.human_side,
                        raw_episode.episode_number, member.side,
                    ).map_err(|_| NativeSnapshotError::InvalidGroupSource)?;
                    if member.agent_id == 0 || member.ship_id < 0 ||
                        member.team_id != expected_team ||
                        !ids.insert(member.agent_id)
                    {
                        return Err(NativeSnapshotError::InvalidGroupSource);
                    }
                    participants.push(NativeSourceGroupParticipant {
                        agent_id: member.agent_id,
                        ship_id: member.ship_id,
                        side: member.side,
                        team_id: member.team_id,
                    });
                }
                if participants.is_empty() { None } else {
                    Some(NativeSourcePolicyGroup {
                        episode_number: raw_episode.episode_number,
                        movement_disabled_by_team,
                        participants,
                    })
                }
            } else {
                None
            };
            (Some(NativeSourceEpisode {
                bee_side: raw_episode.bee_side,
                human_side: raw_episode.human_side,
                bee_starting_tsv: raw_episode.bee_starting_tsv,
                human_starting_tsv: raw_episode.human_starting_tsv,
                timeout_seconds: raw_episode.timeout_seconds,
                discovery_baselines: [
                    [baselines[0], baselines[1], baselines[2], baselines[3]],
                    [baselines[4], baselines[5], baselines[6], baselines[7]],
                ],
            }), source_group)
        } else {
            (None, None)
        };
        let frame = NavigationMapFrame {
            origin_world: raw.navigation_origin_world.into_vec(),
            x_axis_world: raw.navigation_x_axis_world.into_vec(),
            y_axis_world: raw.navigation_y_axis_world.into_vec(),
        };
        if raw.navigation_map_width <= 0 ||
            raw.navigation_map_height <= 0 || !frame.valid()
        {
            return Err(NativeSnapshotError::InvalidNavigationSource);
        }
        let source_navigation = NativeSourceNavigation {
            map_width: raw.navigation_map_width as f32,
            map_height: raw.navigation_map_height as f32,
            frame,
        };
        let min = raw.min.into_vec();
        let max = raw.max.into_vec();
        if raw.level_name.trim().is_empty() ||
            raw.stage_name.trim().is_empty() ||
            !raw.fixed_dt_seconds.is_finite() || raw.fixed_dt_seconds <= 0.0 ||
            !min.is_finite() || !max.is_finite() ||
            min.x >= max.x || min.y >= max.y
        {
            return Err(NativeSnapshotError::InvalidLevelBounds);
        }

        let camera_zoom = match (raw.camera_min_zoom, raw.camera_max_zoom) {
            (None, None) | (Some(0), Some(0)) => None,
            (Some(min), Some(max)) if min > 0 && max >= min =>
                Some((min as f32, max as f32)),
            _ => return Err(NativeSnapshotError::InvalidCameraZoom),
        };
        let mut entities = Vec::with_capacity(
            raw.ships.len().saturating_add(raw.obstacles.len())
                .saturating_add(raw.map_objects.len())
        );
        let mut fleet_values = Vec::with_capacity(raw.ships.len());
        let mut authored_ship_perceptions = HashMap::with_capacity(raw.ships.len());
        let mut active_policy_ship_ids = Vec::new();
        let mut source_policy_bindings = HashMap::new();
        let mut source_policy_inputs = HashMap::new();
        let mut source_vision = HashMap::new();
        let mut source_static_discovery_values = HashMap::new();
        let mut source_formation = HashMap::new();
        let mut seen = HashSet::with_capacity(entities.capacity());
        for ship in raw.ships {
            if ship.entity_id <= 0 || ship.side <= 0 ||
                ship.original_health <= 0 ||
                !ship.size_class.is_finite() || ship.size_class < 0.0 ||
                ship.size_pixels.x == 0 || ship.size_pixels.y == 0 ||
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
            if ship.source_sight.is_some_and(|sight| sight < 0) || ship.effective_vision_range < 0 ||
                ship.fog_vision_radius.is_some_and(|radius| !radius.is_finite() || radius < 0.0) ||
                ship.hivemind_sight_range < 0 ||
                (ship.hivemind_vision_enabled &&
                    (!ship.hivemind_registered ||
                     ship.hivemind_sight_range <= 0))
            {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            source_vision.insert(ship.entity_id, NativeSourceVision {
                source_sight: ship.source_sight,
                effective_range: ship.effective_vision_range,
                trigger_range: ship.hivemind_sight_range,
                fog_radius: ship.fog_vision_radius,
                registered: ship.hivemind_registered,
                enabled: ship.hivemind_vision_enabled,
            });
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
            let game_movement = GameMovementProfile {
                turning_radius: ship.turning_radius,
                half_width: ship.half_width,
                half_height: ship.half_height,
            };
            if !game_movement.valid() {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            let offset = ship.offset_from_center.into_vec();
            if !offset.is_finite() || ship.path_clearance < 0 {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            source_formation.insert(ship.entity_id, NativeSourceFormation {
                offset, clearance: ship.path_clearance,
            });
            // This format constructs fresh worlds. A target alone cannot
            // reconstruct a running game's waypoint/retry/command history.
            if (!ship.is_rl_policy_controlled || ship.user_controlled) &&
                (ship.has_target_coordinates || ship.is_moving) {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }

            let healing_zone = if ship.has_healing_zone {
                if ship.ship_type != 2 { return Err(NativeSnapshotError::InvalidShip(ship.entity_id)); }
                Some(ship.healing_zone.ok_or(NativeSnapshotError::InvalidShip(ship.entity_id))?
                    .into_shape(ship.entity_id)?)
            } else { None };
            let warp_zone = if ship.has_warp_zone {
                if ship.ship_type != 20 { return Err(NativeSnapshotError::InvalidShip(ship.entity_id)); }
                Some(ship.warp_zone.ok_or(NativeSnapshotError::InvalidShip(ship.entity_id))?
                    .into_shape(ship.entity_id)?)
            } else { None };
            let can_mine = matches!(ship.ship_type, 4 | 9);
            let can_heal = ship.side == raw.bee_side &&
                ship.size_pixels.x < raw.beehive_size_pixels.x &&
                ship.size_pixels.y < raw.beehive_size_pixels.y;
            let can_warp = ship.side == raw.human_side && ship.ship_type != 20;
            if ship.has_bound_policy && (ship.mining_possible != can_mine ||
                ship.healing_possible != can_heal || ship.warp_possible != can_warp) {
                return Err(NativeSnapshotError::InvalidPolicySource(ship.entity_id));
            }
            let policy_capabilities = NativeAuthoredPolicyCapabilities {
                mining: can_mine,
                healing: can_heal.then_some(NativeHealingEligibility {
                    bee_side: raw.bee_side,
                    ship_size_pixels: [ship.size_pixels.x, ship.size_pixels.y],
                    beehive_size_pixels: [raw.beehive_size_pixels.x, raw.beehive_size_pixels.y],
                }),
                warp_human_side: can_warp.then_some(raw.human_side),
            };
            let source_special_power = ship.special.power;
            let special = ship.special.into_native(ship.entity_id, ship.ship_type, raw.schema_version, catalog)?;
            if ship.has_bound_policy && ship.has_special_action != special.is_some() {
                return Err(NativeSnapshotError::InvalidPolicySource(ship.entity_id));
            }
            // Schema28 captures ordinary Striker CarrierShip.Carrier as
            // well as the Bomb special's Carrier reference. Both fields
            // originate from the same Unity object and must agree; a
            // mismatch cannot safely enter game or policy simulation.
            if ship.has_bound_policy || raw.schema_version >= 28 {
                if let Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) = &special {
                    if carrier.carrier_id != (ship.parent_carrier_id > 0).then_some(ship.parent_carrier_id) {
                        return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
                    }
                }
            }

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
                let range_collider = match (weapon.range_collider, weapon.range_layer, weapon.range_enabled) {
                    (None, None, None) if raw.schema_version == 20 => None,
                    (Some(collider), Some(layer), Some(enabled)) => {
                        let ShipShape::Circle { offset, radius } = collider.into_shape(ship.entity_id)
                            .map_err(|_| invalid(weapon.slot))? else { return Err(invalid(weapon.slot)) };
                        let range = NativeTurretRangeSpec { offset, radius, layer, enabled };
                        if !range.valid() { return Err(invalid(weapon.slot)); }
                        Some(range)
                    },
                    _ => return Err(invalid(weapon.slot)),
                };
                if raw.schema_version >= 22 && weapon.aim_at_front_of_ship.is_none() {
                    return Err(invalid(weapon.slot));
                }
                authored_turrets.push(NativeAuthoredTurret {
                    aim_at_front_of_ship: weapon.aim_at_front_of_ship,
                    range_collider,
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
            // Schema28 captures CarrierShip.Carrier for ordinary game
            // children too; older exports only included this for policies.
            if raw.schema_version >= 28 &&
                (ship.parent_carrier_id < 0 ||
                ship.has_living_parent_carrier != (ship.parent_carrier_id > 0) ||
                (ship.has_living_parent_carrier && !ship.is_carrier_child) ||
                (!ship.is_carrier_child && ship.parent_carrier_id != 0) ||
                (ship.is_carrier_child && !matches!(ship.ship_type, 8 | 19)))
            {
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            if ship.has_bound_policy {
                if !ship.is_rl_policy_controlled ||
                    ship.effective_vision_range < 0 ||
                    (ship.has_turrets && !ship.has_weapons) ||
                    ship.side_starting_tsv <= 0 ||
                    ship.team_retainable_mined_tsv < 0 ||
                    (ship.has_living_parent_carrier !=
                        (ship.parent_carrier_id > 0)) ||
                    (!ship.is_carrier_child &&
                        ship.parent_carrier_id != 0) ||
                    [ship.special_resource_fraction,
                     ship.special_cooldown_fraction, ship.special_phase]
                        .iter().any(|value| !value.is_finite() ||
                            *value < 0.0 || *value > 1.0)
                {
                    return Err(NativeSnapshotError::InvalidPolicySource(
                        ship.entity_id,
                    ));
                }
                let controller = ControlledShipPerception {
                    effective_vision_range: ship.effective_vision_range as f32,
                    movement_disabled_for_team: ship.policy_movement_disabled,
                    has_weapons: ship.has_weapons,
                    has_turrets: ship.has_turrets,
                    has_special_action: ship.has_special_action,
                    special_ready: ship.special_ready,
                };
                let capability = CapabilitySource {
                    special_resource_fraction: ship.special_resource_fraction,
                    special_cooldown_fraction: ship.special_cooldown_fraction,
                    special_phase: ship.special_phase,
                    mining_possible: ship.mining_possible,
                    healing_possible: ship.healing_possible,
                    warp_possible: ship.warp_possible,
                    is_carrier_child: ship.is_carrier_child,
                    has_living_parent_carrier: ship.has_living_parent_carrier,
                    ship_mined_tsv: ship.mined_cargo,
                    team_retainable_mined_tsv: ship.team_retainable_mined_tsv,
                    side_starting_tsv: ship.side_starting_tsv,
                };
                let mut own_weapons = [None; 5];
                for weapon in ship.self_weapons {
                    if weapon.slot >= own_weapons.len() ||
                        own_weapons[weapon.slot].is_some() ||
                        occupied[weapon.slot] ||
                        !weapon.position.into_vec().is_finite() ||
                        ![weapon.range, weapon.power, weapon.rate_of_fire,
                          weapon.rotation_rate, weapon.projectile_value]
                            .iter().all(|value| value.is_finite()) ||
                        (weapon.has_projectile_speed &&
                            !weapon.projectile_speed.is_finite())
                    {
                        return Err(NativeSnapshotError::InvalidPolicySource(
                            ship.entity_id,
                        ));
                    }
                    let index = weapon.slot;
                    own_weapons[index] = Some(SelfWeapon {
                        weapon_type: weapon.weapon_type,
                        position: weapon.position.into_vec(),
                        range: weapon.range,
                        power: weapon.power,
                        rate_of_fire: weapon.rate_of_fire,
                        rotation_rate: weapon.rotation_rate,
                        projectile_value: weapon.projectile_value,
                        projectile_speed: weapon.has_projectile_speed
                            .then_some(weapon.projectile_speed),
                        turret: None,
                        has_target_ship: weapon.has_target_ship,
                    });
                }
                source_policy_inputs.insert(ship.entity_id,
                    NativeSourcePolicyInputs {
                        controller, capability,
                        parent_carrier_id: (ship.parent_carrier_id > 0)
                            .then_some(ship.parent_carrier_id),
                        own_weapons,
                    }
                );
                if !(0..=1).contains(&ship.policy_team_id) ||
                    !(0..=3).contains(&ship.policy_quarter_turns) {
                    return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
                }
                source_policy_bindings.insert(
                    ship.entity_id,
                    NativeSourcePolicyBinding {
                        team_id: ship.policy_team_id as u8,
                        frame: PolicyFrame::new(ship.policy_quarter_turns as u8),
                        movement_disabled: ship.policy_movement_disabled,
                        cease_fire: ship.policy_cease_fire,
                    },
                );
            } else if ship.policy_team_id != -1 || ship.policy_quarter_turns != -1 {
                // A serialized non-controller must not masquerade as
                // having an unverified, newly sampled coordinate frame.
                return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
            }
            if ship.is_rl_policy_controlled {
                active_policy_ship_ids.push(ship.entity_id);
            }
            fleet_values.push(NativeFleetShipValue {
                ship_id: ship.entity_id,
                has_persistent_fleet_value: ship.persistent_fleet,
                minerals_mined_this_level: ship.mined_cargo,
            });
            let weapon_rank_source = match ship.weapon_rank_source {
                Some(source) => {
                    let stats = bees_sim::targeting::NativeEnemyTargetStats {
                        ship_id: ship.entity_id, ship_type_letter: source.ship_type_letter,
                        last_killed: source.last_killed, fleet_damage_done: i64::from(source.fleet_damage_done),
                        firepower: source.firepower, max_range: source.max_range, speed: source.speed,
                    };
                    if !stats.valid() || source.fleet_owner_ship_id <= 0 {
                        return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
                    }
                    Some(NativeWeaponRankSource { stats, fleet_owner_ship_id: source.fleet_owner_ship_id })
                },
                None if raw.schema_version < 23 => None,
                _ => return Err(NativeSnapshotError::InvalidShip(ship.entity_id)),
            };
            let target_geometry = match ship.target_geometry {
                Some(source) if source.front_offset.is_finite() => Some(NativeShipTargetGeometry {
                    front_offset: source.front_offset, bounds_latched: source.bounds_latched,
                }),
                None if raw.schema_version < 22 => None,
                _ => return Err(NativeSnapshotError::InvalidShip(ship.entity_id)),
            };
            let special_weapon = match ship.special_weapon {
                Some(source) if matches!(ship.ship_type, 0 | 10 | 19 | 22) &&
                    source.weapon_id > 0 && source.slot < 5 && source.power == source_special_power &&
                    source.power >= 0 && source.mount_offset.into_vec().is_finite() => {
                    let charge_range = source.charge_range.map(|value| value.into_native(ship.entity_id)).transpose()?;
                    let proximity = source.proximity.map(|value| value.into_native(ship.entity_id)).transpose()?;
                    if (ship.ship_type == 0 && charge_range.is_none()) || (ship.ship_type != 0 && proximity.is_none()) {
                        return Err(NativeSnapshotError::InvalidShip(ship.entity_id));
                    }
                    Some(bees_sim::combat_world::NativeSpecialWeaponSpec {
                        weapon_id: source.weapon_id, slot: source.slot, power: source.power, mount_offset: source.mount_offset.into_vec(),
                        charge_range, proximity,
                    })
                },
                None if raw.schema_version < 24 || !matches!(ship.ship_type, 0 | 10 | 19 | 22) => None,
                _ => return Err(NativeSnapshotError::InvalidShip(ship.entity_id)),
            };
            entities.push(NativeAuthoredEntity::Ship(NativeAuthoredShip {
                combat_activity: ship.combat_activity.map(UnityCombatActivity::convert).transpose()?,
                special_weapon,
                target_geometry, weapon_rank_source,
                entity_id: ship.entity_id, ship_type: ship.ship_type,
                cease_fire: ship.policy_cease_fire, turrets: authored_turrets,
                side: ship.side, vitals, motion, game_movement,
                original_health: ship.original_health,
                size_class: ship.size_class,
                healing_zone, warp_zone, policy_capabilities,
                fleet_ship_id: (ship.fleet_ship_id != 0)
                    .then_some(ship.fleet_ship_id),
                policy: None,
                drone_carrier_id: (ship.ship_type == 8 &&
                    (raw.schema_version >= 28 || ship.has_bound_policy) &&
                    ship.is_carrier_child && ship.parent_carrier_id > 0)
                    .then_some(ship.parent_carrier_id),
                special,
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
            // Source RlOneVsOneEpisodeCoordinator uses OriginalHealth
            // when positive, then current Health, clamped to at least one.
            // These are reward-only immutable inputs, not duplicated
            // mutable obstacle health or a second collision registry.
            if matches!(obstacle.kind.as_str(), "box" | "polygon") {
                let value = if obstacle.effective_max_health > 0 {
                    obstacle.effective_max_health
                } else {
                    obstacle.health
                }.max(1);
                source_static_discovery_values.insert(obstacle.entity_id, value);
            }
            let entity = match obstacle.kind.as_str() {
                "box" => {
                    if obstacle.collider_vertices.len() != 4 {
                        return Err(NativeSnapshotError::InvalidObstacle(obstacle.entity_id));
                    }
                    let mut corners = [Vec2::ZERO; 4];
                    for (target, source) in corners.iter_mut()
                        .zip(obstacle.collider_vertices.iter())
                    {
                        *target = source.into_vec();
                    }
                    NativeAuthoredEntity::BoxObstacle {
                        entity_id: obstacle.entity_id,
                        prefab_name: obstacle.prefab_name,
                        pose: Some(pose),
                        live_corners: Some(corners),
                    }
                },
                "polygon" => {
                    let vertices: Vec<_> = obstacle.collider_vertices.iter()
                        .map(|point| point.into_vec()).collect();
                    if vertices.len() < 3 || vertices.len() > 8192 ||
                        vertices.iter().any(|v| !v.is_finite()) {
                        return Err(NativeSnapshotError::InvalidObstacle(
                            obstacle.entity_id,
                        ));
                    }
                    NativeAuthoredEntity::PolygonObstacle {
                        entity_id: obstacle.entity_id,
                        pose: Some(pose),
                        live_vertices: Some(vertices),
                    }
                },
                "mining" => {
                    let verts: Vec<Vec2> = obstacle.collider_vertices.iter()
                        .map(|p| p.into_vec()).collect();
                    if verts.len() < 3 || verts.len() > 8192 ||
                        verts.iter().any(|p| !p.is_finite()) ||
                        obstacle.health <= 0 ||
                        obstacle.health != obstacle.effective_max_health ||
                        obstacle.layer >= 32
                    {
                        return Err(NativeSnapshotError::InvalidObstacle(obstacle.entity_id));
                    }
                    let vitals = ObstacleVitals::with_original_health(
                        obstacle.health, obstacle.effective_max_health,
                        obstacle.effective_max_health,
                    ).ok_or(NativeSnapshotError::InvalidObstacle(obstacle.entity_id))?;
                    let body = ObstacleBody::new(
                        obstacle.entity_id, pose, obstacle.layer,
                        ShipShape::Polygon(Arc::from(verts.into_boxed_slice())),
                        velocity, obstacle.angular_velocity_degrees,
                    ).map_err(|_| NativeSnapshotError::InvalidObstacle(obstacle.entity_id))?;
                    NativeAuthoredEntity::MiningAsteroid {
                        obstacle: CombatObstacle {
                            entity_id: obstacle.entity_id,
                            kind: ObstacleKind::MiningAsteroid,
                            vitals: Some(vitals),
                            size_class: 0,
                        },
                        body,
                    }
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
        // MapObjects are independent Unity components, NOT Obstacle
        // subclasses: they must retain their source entity IDs, health,
        // current live collider and optional exact Fire Tank relationship.
        // Initial snapshots cannot represent ongoing damage/animation.
        for entry in raw.map_objects {
            let id = entry.entity_id;
            if id <= 0 || !seen.insert(id) || entry.max_health <= 0 ||
                entry.health != entry.max_health || entry.layer >= 32 ||
                !entry.position.into_vec().is_finite() ||
                !entry.linear_velocity.into_vec().is_finite() ||
                !entry.rotation_degrees.is_finite() ||
                !entry.angular_velocity_degrees.is_finite()
            {
                return Err(NativeSnapshotError::InvalidMapObject(id));
            }
            let object = match entry.kind.as_str() {
                "generic" if entry.explosion_power == 0 &&
                    entry.linked_obstacle_id == 0 =>
                {
                    CombatMapObject::new(
                        id, bees_sim::map_object::MapObjectKind::Generic,
                        entry.max_health,
                    )
                }
                "fire-tank" => CombatMapObject::new_fire_tank(
                    id, entry.max_health, entry.explosion_power,
                    (entry.linked_obstacle_id > 0)
                        .then_some(entry.linked_obstacle_id),
                ),
                _ => return Err(NativeSnapshotError::InvalidMapObject(id)),
            }.map_err(|_| NativeSnapshotError::InvalidMapObject(id))?;

            let shape = match entry.collider_kind.as_str() {
                "box" if entry.collider_vertices.len() == 4 => {
                    let mut vertices = [Vec2::ZERO; 4];
                    for (dst, src) in vertices.iter_mut()
                        .zip(&entry.collider_vertices)
                    {
                        *dst = src.into_vec();
                    }
                    live_box_shape(vertices)
                        .map_err(|_| NativeSnapshotError::InvalidMapObject(id))?
                }
                "polygon" if entry.collider_vertices.len() >= 3 &&
                    entry.collider_vertices.len() <= 8192 =>
                {
                    let vertices: Vec<Vec2> = entry.collider_vertices
                        .iter().map(|v| v.into_vec()).collect();
                    if vertices.iter().any(|v| !v.is_finite()) {
                        return Err(NativeSnapshotError::InvalidMapObject(id));
                    }
                    ShipShape::Polygon(Arc::from(vertices.into_boxed_slice()))
                }
                "circle" if entry.collider_vertices.is_empty() => {
                    ShipShape::Circle {
                        offset: entry.circle_offset.into_vec(),
                        radius: entry.circle_radius,
                    }
                }
                _ => return Err(NativeSnapshotError::InvalidMapObject(id)),
            };
            let body = ObstacleBody::new(
                id,
                Pose {
                    position: entry.position.into_vec(),
                    rotation_degrees: entry.rotation_degrees,
                },
                entry.layer,
                shape,
                entry.linear_velocity.into_vec(),
                entry.angular_velocity_degrees,
            ).map_err(|_| NativeSnapshotError::InvalidMapObject(id))?;
            entities.push(NativeAuthoredEntity::MapObject { object, body });
        }
        // Fire Tanks may break only an actual imported static Obstacle.
        // Never quietly point a blast at a nonexistent/other-kind entity.
        let static_obstacle_ids: HashSet<i64> = entities.iter()
            .filter_map(|entity| match entity {
                NativeAuthoredEntity::BoxObstacle { entity_id, .. } |
                NativeAuthoredEntity::PolygonObstacle { entity_id, .. } =>
                    Some(*entity_id),
                _ => None,
            }).collect();
        for entry in &entities {
            if let NativeAuthoredEntity::MapObject { object, .. } = entry {
                if object.fire_tank.and_then(|tank| tank.target_obstacle_id)
                    .is_some_and(|id| !static_obstacle_ids.contains(&id))
                {
                    return Err(NativeSnapshotError::InvalidMapObject(
                        object.entity_id,
                    ));
                }
            }
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
        // Unity CarrierSquad.SetupCarrierSquad creates homogeneous Drone
        // or Striker groups for ONE parent Carrier. This import-only map
        // validates the authoritative ship links before commands can use
        // the source IsCarrierSquad flag. It is not kept in the live world.
        let carrier_children: HashMap<i64, (u8, Option<i64>)> =
            if raw.schema_version >= 28 {
                entities.iter().filter_map(|entity| {
                    let NativeAuthoredEntity::Ship(ship) = entity else {
                        return None;
                    };
                    let parent = match ship.ship_type {
                        8 => ship.drone_carrier_id,
                        19 => match &ship.special {
                            Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) =>
                                carrier.carrier_id,
                            _ => return None,
                        },
                        _ => return None,
                    };
                    Some((ship.entity_id, (ship.ship_type, parent)))
                }).collect()
            } else { HashMap::new() };
        let mut source_squads = Vec::with_capacity(raw.squads.len());
        let mut squad_ids = HashSet::with_capacity(raw.squads.len());
        let mut registered_ships = HashSet::with_capacity(live_ships.len());
        let mut used_numbers = HashSet::with_capacity(raw.squads.len());
        for squad in raw.squads {
            if squad.item_id <= 0 || squad.side <= 0 || squad.member_ids.is_empty() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            if !squad_ids.insert(squad.item_id) {
                return Err(NativeSnapshotError::DuplicateSquad(squad.item_id));
            }
            let (hotkey_number, original_squad_count) =
                if let Some(counts) = original_squad_counts {
                    let side = usize::try_from(squad.side - 1)
                        .ok().filter(|&side| side < 2)
                        .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                    let number = squad.squad_number
                        .and_then(|number| u32::try_from(number).ok())
                        .filter(|&number| number > 0)
                        .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                    let minion = squad.is_minion_squad
                        .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                    if !used_numbers.insert((squad.side, number)) ||
                        (!minion && number > counts[side]) {
                        return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                    }
                    ((!minion).then_some(number), Some(counts[side]))
                } else { (None, None) };
            for &ship_id in &squad.member_ids {
                if live_ships.get(&ship_id) != Some(&squad.side) {
                    return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                }
                if !registered_ships.insert(ship_id) {
                    return Err(NativeSnapshotError::DuplicateSquadShip(ship_id));
                }
            }
            let shooting_index = bees_hivemind::protocol::SHOOTING_STRATEGIES.iter()
                .position(|&name| name == squad.shooting_strategy)
                .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
            if raw.schema_version >= 31 && squad.should_chase.is_none() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            if raw.schema_version >= 32 &&
                (squad.is_matching_speed.is_none() || squad.current_speed.is_none())
            {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            if raw.schema_version >= 33 && squad.cease_fire.is_none() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            let matching_speed = if squad.is_matching_speed == Some(true) {
                let speed = squad.current_speed
                    .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                if !speed.is_finite() || speed < 0.0 {
                    return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                }
                Some(speed)
            } else { None };
            if raw.schema_version >= 24 && squad.is_carrier_squad.is_none() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            if raw.schema_version >= 28 && squad.is_carrier_squad == Some(true) {
                let first = carrier_children.get(&squad.member_ids[0])
                    .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                // No guessed Carrier Squad or cross-parent ship transfer.
                // An orphaned squad has None as its shared parent.
                if squad.member_ids.iter().any(|id|
                    carrier_children.get(id) != Some(first)
                ) {
                    return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                }
            }
            if raw.schema_version >= 26 && squad.banned_strategies.is_none() {
                return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
            }
            let command_bans = squad.banned_strategies.map(|names| {
                let mut bans = Vec::with_capacity(names.len());
                for name in names {
                    let index = bees_hivemind::protocol::COMMAND_STRATEGIES.iter()
                        .position(|&candidate| candidate == name)
                        .ok_or(NativeSnapshotError::InvalidSquad(squad.item_id))?;
                    let kind = bees_hivemind::dispatch::LocalCommandKind::from_server_strategy_id((index + 1) as u8)
                        .expect("canonical command table");
                    if bans.contains(&kind) { return Err(NativeSnapshotError::InvalidSquad(squad.item_id)); }
                    bans.push(kind);
                }
                Ok(bans)
            }).transpose()?;
            let hivemind = match (
                squad.is_hivemind_controlled, squad.is_immobile,
                squad.opponent_id_decimal,
            ) {
                (Some(hivemind_controlled), Some(immobile), Some(id)) => {
                    let opponent_id = id.parse::<u64>()
                        .map_err(|_| NativeSnapshotError::InvalidSquad(squad.item_id))?;
                    // Mixed-production Levels legitimately have neural
                    // squads controlled by neither the user nor HiveMind.
                    // Only simultaneous competing authorities are invalid;
                    // an idle/source-scripted squad is also not HiveMind.
                    if opponent_id.to_string() != id ||
                        (hivemind_controlled && squad.user_controlled) {
                        return Err(NativeSnapshotError::InvalidSquad(squad.item_id));
                    }
                    Some(bees_gameplay::level_host::NativeHiveMindSquadSource {
                        opponent_id, hivemind_controlled, immobile,
                    })
                },
                (None, None, None) if raw.schema_version < 27 => None,
                _ => return Err(NativeSnapshotError::MissingHiveMindSource),
            };
            source_squads.push(NativeSourceSquad {
                command_bans,
                hivemind,
                is_carrier_squad: squad.is_carrier_squad,
                should_chase: squad.should_chase.unwrap_or(false),
                matching_speed,
                cease_fire: squad.cease_fire,
                hotkey_number,
                original_squad_count,
                shooting: bees_hivemind::protocol::StrategyIdentity {
                    id: (shooting_index + 1) as u8,
                    name: bees_hivemind::protocol::SHOOTING_STRATEGIES[shooting_index],
                },
                item_id: squad.item_id, side: squad.side,
                members: squad.member_ids,
                player_permissions: bees_gameplay::player_inputs::NativePlayerPermissions {
                    user_controlled: squad.user_controlled,
                    can_accept_input: squad.can_accept_input, locked_on: squad.locked_on,
                },
            });
        }
        if registered_ships.len() != live_ships.len() {
            return Err(NativeSnapshotError::InvalidSquad(0));
        }
        // A carrier-child policy observes the original living parent as
        // a special entity BEFORE ordinary allies. Reject dangling links
        // rather than changing the order of the learned identity scalars.
        for (&ship_id, input) in &source_policy_inputs {
            if let Some(parent_id) = input.parent_carrier_id {
                if ship_id == parent_id ||
                    live_ships.get(&parent_id) != live_ships.get(&ship_id)
                {
                    return Err(NativeSnapshotError::InvalidPolicySource(
                        ship_id,
                    ));
                }
            }
        }
        if !source_policy_bindings.is_empty() && source_episode.is_none() {
            return Err(NativeSnapshotError::InvalidEpisodeSource);
        }
        // A schema-16 export MUST include every source-controlled
        // participant: it must not silently degrade to guessed or manual
        // MA-POCA ownership when its authenticated group is absent.
        if raw.schema_version >= 16 &&
            !source_policy_bindings.is_empty() &&
            source_policy_group.is_none()
        {
            return Err(NativeSnapshotError::InvalidGroupSource);
        }
        if let Some(group) = &source_policy_group {
            let mut bound_ship_ids = HashSet::new();
            for member in &group.participants {
                if member.ship_id == 0 {
                    continue;
                }
                let Some(binding) = source_policy_bindings.get(&member.ship_id)
                else {
                    return Err(NativeSnapshotError::InvalidGroupSource);
                };
                if binding.team_id != member.team_id ||
                    binding.movement_disabled !=
                        group.movement_disabled_by_team[usize::from(member.team_id)] ||
                    live_ships.get(&member.ship_id) != Some(&member.side) ||
                    !bound_ship_ids.insert(member.ship_id)
                {
                    return Err(NativeSnapshotError::InvalidGroupSource);
                }
            }
            // Every exported policy-controlled ship must belong to its
            // actual participating Unity Agent; otherwise group rewards
            // cannot be matched to the physical action trajectories.
            if source_policy_bindings.keys().any(|id|
                !bound_ship_ids.contains(id)
            ) {
                return Err(NativeSnapshotError::InvalidGroupSource);
            }
        }
        let fleet = NativeFleetInventory::from_source(fleet_values)
            .map_err(NativeSnapshotError::Fleet)?;
        Ok(Self {
            bee_side: raw.bee_side,
            human_side: raw.human_side,
            source_frame_count: raw.source_frame_count,
            targeting_context,
            schema_version: raw.schema_version,
            level_name: raw.level_name,
            stage_name: raw.stage_name,
            bounds: Aabb { min, max },
            starting_positions,
            fixed_dt_seconds: raw.fixed_dt_seconds,
            shots_harmless: raw.shots_harmless,
            activate_collision_asteroids: raw.activate_collision_asteroids,
            activate_mining: raw.activate_mining,
            activate_fog_of_war: raw.activate_fog_of_war,
            camera_zoom,
            camera_edge_scrolling: raw.camera_edge_scrolling,
            activate_hivemind: raw.activate_hivemind,
            hivemind_initial_command_delay_seconds: raw.hivemind_initial_command_delay_seconds,
            has_obstacles: raw.has_obstacles,
            command_settings,
            entities,
            source_episode,
            source_policy_group,
            source_navigation,
            fleet,
            authored_ship_perceptions,
            active_policy_ship_ids,
            source_squads,
            source_policy_bindings,
            source_policy_inputs,
            source_vision,
            source_static_discovery_values, source_formation,
        })
    }
}


impl NativeSceneAssets {
    /// Launch a complete native, policy-only batched host from multiple
    /// independently exported Unity Levels. Gameplay, model inference,
    /// source-derived rewards and observations, presentation and
    /// notifications all use the same shared runtime. No alternate
    /// training-only physics world or placeholder policy is constructed.
    ///
    /// Every Level chooses its real model-slot mappings and squad allocator
    /// in NativeExportedPolicyLevelSetup. One common Stage fixed delta is
    /// mandatory for a synchronous batched Simulation. Check that before
    /// constructing any world so an invalid mixed-stage set cannot leave
    /// an accidentally partially started host.
    pub fn build_imported_policy_runtime<A, B, S>(
        &self,
        imported: impl IntoIterator<Item=(
            NativePhysicalScene, NativeExportedPolicyLevelSetup<A>,
        )>,
        model: B,
        connection: DurableHiveMindConnection,
        sink: S,
    ) -> Result<
        NativeHostRuntime<NativePolicyOnlyMetadata, A, B, S>,
        NativeRuntimeBuildError,
    >
    where
        A: FnMut(SquadLifetime, i64) -> Result<
            SquadLifetime, bees_gameplay::squads::SquadRosterError,
        >,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        let mut levels = Vec::new();
        for (scene, setup) in imported {
            if scene.stage_name != self.stage_name() {
                return Err(NativeRuntimeBuildError::MixedSourceStage {
                    expected: self.stage_name().to_owned(),
                    actual: scene.stage_name,
                });
            }
            let dt = scene.fixed_dt_seconds;
            levels.push((dt, scene.into_exported_policy_only_level(setup)
                .map_err(NativeRuntimeBuildError::Imported)?));
        }
        let default_step = levels.first().ok_or(NativeRuntimeBuildError::EmptyLevels)?.0;
        self.build_runtime_with_steps(default_step, levels, model, connection, sink)
    }
}
