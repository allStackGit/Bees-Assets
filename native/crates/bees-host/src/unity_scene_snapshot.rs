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
use bees_sim::box_obstacle_assets::live_box_shape;
use bees_sim::physics::{ObstacleBody, ShipShape};
use bees_sim::map_object::CombatMapObject;
use bees_sim::obstacle::{CombatObstacle, ObstacleKind, ObstacleVitals};
use bees_sim::movement::ShipMotion;
use bees_sim::simulation::WorldId;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, ControlledShipPerception,
};
use bees_sim::perception::CapabilitySource;
use bees_sim::perception_weapons::SelfWeapon;
use bees_gameplay::inference::NativePolicyRoute;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_gameplay::episode::{
    NativeEpisodeRewardCoordinator, NativeEpisodeRewardError,
};
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

const SNAPSHOT_VERSION: u32 = 12;

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
    shots_harmless: bool,
    has_policy_episode: bool,
    policy_episode: Option<UnityPolicyEpisode>,
    min: UnityVec2,
    max: UnityVec2,
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
    has_bound_policy: bool,
    policy_team_id: i32,
    policy_quarter_turns: i32,
    policy_movement_disabled: bool,
    policy_cease_fire: bool,
    observed_weapons: Vec<UnityObservedWeapon>,
    effective_vision_range: f32,
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
    Read { path: PathBuf, error: io::Error },
    Malformed(serde_json::Error),
    UnsupportedSchema(u32),
    InvalidLevelBounds,
    InvalidShip(i64),
    MismatchedShipTsv(i64),
    Combat(CombatError),
    UnsupportedObstacle(i64),
    InvalidObstacle(i64),
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
    Episode(NativeEpisodeRewardError),
    MissingModelSlot(u8),
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
    pub shots_harmless: bool,
    pub entities: Vec<NativeAuthoredEntity>,
    source_episode: Option<NativeSourceEpisode>,
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
    /// Real active Unity agent/frame bindings only, never a random
    /// fabricated policy identity. Loaded model slots remain selected by
    /// the host's checkpoint schedule.
    pub source_policy_bindings: HashMap<i64, NativeSourcePolicyBinding>,
    source_policy_inputs: HashMap<i64, NativeSourcePolicyInputs>,
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
pub struct NativeSourceSquad {
    pub item_id: i64,
    pub side: i32,
    pub members: Vec<i64>,
}

impl NativePhysicalScene {
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
            agents.push(AuthoredPolicyAgent {
                ship_id,
                side,
                route: NativePolicyRoute {
                    team_id: binding.team_id, model_slot,
                },
                controller: input.controller,
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
            shots_harmless: self.shots_harmless,
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
        let source_episode = if raw.has_policy_episode {
            let raw_episode = raw.policy_episode
                .ok_or(NativeSnapshotError::InvalidEpisodeSource)?;
            let baselines = raw_episode.discovery_baselines
                .ok_or(NativeSnapshotError::InvalidEpisodeSource)?;
            if baselines.len() != 8 ||
                raw_episode.bee_side <= 0 ||
                raw_episode.human_side <= 0 ||
                raw_episode.bee_side == raw_episode.human_side ||
                raw_episode.bee_starting_tsv <= 0 ||
                raw_episode.human_starting_tsv <= 0 ||
                !raw_episode.timeout_seconds.is_finite() ||
                raw_episode.timeout_seconds <= 0.0 ||
                baselines.iter().any(|value| *value < 0)
            {
                return Err(NativeSnapshotError::InvalidEpisodeSource);
            }
            Some(NativeSourceEpisode {
                bee_side: raw_episode.bee_side,
                human_side: raw_episode.human_side,
                bee_starting_tsv: raw_episode.bee_starting_tsv,
                human_starting_tsv: raw_episode.human_starting_tsv,
                timeout_seconds: raw_episode.timeout_seconds,
                discovery_baselines: [
                    [baselines[0], baselines[1], baselines[2], baselines[3]],
                    [baselines[4], baselines[5], baselines[6], baselines[7]],
                ],
            })
        } else {
            None
        };
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
                .saturating_add(raw.map_objects.len())
        );
        let mut fleet_values = Vec::with_capacity(raw.ships.len());
        let mut turrets_by_ship = HashMap::with_capacity(raw.ships.len());
        let mut authored_ship_perceptions = HashMap::with_capacity(raw.ships.len());
        let mut active_policy_ship_ids = Vec::new();
        let mut source_policy_bindings = HashMap::new();
        let mut source_policy_inputs = HashMap::new();
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
            if ship.has_bound_policy {
                if !ship.is_rl_policy_controlled ||
                    !ship.effective_vision_range.is_finite() ||
                    ship.effective_vision_range < 0.0 ||
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
                    effective_vision_range: ship.effective_vision_range,
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
        let fleet = NativeFleetInventory::from_source(fleet_values)
            .map_err(NativeSnapshotError::Fleet)?;
        Ok(Self {
            level_name: raw.level_name,
            bounds: Aabb { min, max },
            fixed_dt_seconds: raw.fixed_dt_seconds,
            shots_harmless: raw.shots_harmless,
            entities,
            source_episode,
            fleet,
            turrets_by_ship,
            authored_ship_perceptions,
            active_policy_ship_ids,
            source_squads,
            source_policy_bindings,
            source_policy_inputs,
        })
    }
}
