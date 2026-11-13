//! World-local impact resolution atop the single native physics core.
//!
//! This is the shared game implementation, not a separate training simulator.
//! The core resolves contacts and health only where Unity rules are ported.
//! Command/squad storage, episode rewards, terminal-state decisions, and the
//! remaining weapon specials must be wired into this same world before the
//! world becomes usable for training or user-visible gameplay.
//!
//! Unity's OnTriggerEnter2D enqueues contact; Projectile.FixedUpdate consumes
//! one ship contact per ordinary projectile per tick, while RocketExplosion
//! drains all. We keep those queues at the world boundary. Within a tick we
//! use stable entity IDs for ordering; Unity callback ordering is not
//! guaranteed to match until checked with a live reference build.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::Arc;

use crate::collision::{Aabb, Pose};
use crate::asteroid_assets::CollisionAsteroidCatalog;
use crate::asteroid_spawn::{
    FullAsteroidSpawnError, FullAsteroidMotion, FullAsteroidPrototype,
    FullAsteroidStage, create_full_asteroid_prototype,
    sample_full_asteroid_motion,
};
use crate::asteroid_fragments::{
    AsteroidFragmentPalette, AsteroidPieceState, FragmentPrototype,
    FragmentSpawnSettings, new_piece_prototype, new_shard_prototype,
    spawn_fragment_motion,
};
use crate::box_obstacle_assets::BoxObstacleCatalog;
use crate::polygon_obstacle_assets::PolygonObstacleCatalog;
use crate::explosion_lifecycle::{ExplosionAction, ExplosionEvent, ExplosionTimeline};
use crate::bomb::{BombAction, StrikerBombFuse};
use crate::combat::{CombatError, DamageSource, HitEvent, ShipVitals};
use crate::damage::can_basic_projectile_damage;
use crate::damage_reservations::{DamageReservations, DamageStatus, ReservationError};
use crate::physics::{
    CollisionMatrix, KinematicPhysics, MovementObstacleHit, ObstacleBody,
    ObstaclePairContact, ObstacleTriggerEnter, PhysicsError, ProjectileBody,
    ShipBody, ShipHullOverlap, ShipShape, TriggerEnter,
};
use crate::policy::{ActionError, Decision};
use crate::control::{PolicyControl, PolicyFrame};
use crate::geometry::Vec2;
use crate::level_discovery::{DiscoveryKind, LevelDiscovery};
use crate::map_object::{CombatMapObject, MapObjectDamage, MapObjectError};
use crate::movement::ShipMotion;
use crate::navigation_clearance::NavigationClearance;
use crate::navigation_runtime::NavigationRuntime;
use crate::navigation::NavigationRasterCounts;
use crate::navigation_movement::{GameMovementProfile, GameShipSteering};
use crate::movement::MotionUpdate;
use crate::navigation_search::NavigationSearch;
use crate::navigation_requests::{
    NavigationCompletion, NavigationRequest, NavigationRequestDisposition,
    NavigationRequestError, NavigationRequests,
};
use crate::obstacle::{
    resolve_asteroid_pair_damage, resolve_asteroid_ship_damage,
    AsteroidBreakup, AsteroidDamage,
    CombatObstacle, ObstacleKind,
};
use crate::projectile::ProjectileMotion;
use crate::ship_assets::ShipAsset;
use crate::projectile_assets::ProjectileAsset;
use crate::projectile_rules::{rocket_accelerated_velocity, split_fragments, ExplosionContacts, PiercingShot, PiercingType};
use crate::projectile_assets::ProjectileAssetCatalog;
use crate::timer::ScaledTimer;
use crate::simulation::{GameRules, RandomStream, World};
use crate::telemetry::StepCounts;
use crate::vision::{HiveMindObserver, VisionError, VisionSweepCounts, VisionFirstSighting};

// Child module owns the complete live collider-vision refresh and keeps
// the 140K+ combat gameplay implementation focused on authoritative state.
mod vision;
mod map_object;
mod extraction;
pub use extraction::ExtractionZone;
mod policy;
mod weapons;
mod weapon_ranges;
mod special_weapon_contacts;
pub use special_weapon_contacts::{NativeSpecialWeaponSpec, SpecialWeaponMeasurements};
mod weapon_target_geometry;
mod weapon_rank_history;
mod combat_activity;
pub use self::combat_activity::NativeCombatActivity;
mod automatic_weapons;
pub use self::automatic_weapons::{NativeAutomaticWeaponInputs, NativeAutomaticWeaponContext, NativeAutomaticTurretStatus, AutomaticWeaponMeasurements};
pub use self::weapon_rank_history::NativeWeaponRankSource;
pub use self::weapon_target_geometry::{NativeLevelGeometry, NativeShipTargetGeometry};
pub use self::weapon_ranges::NativeWeaponRangeContacts;
mod striker;
mod carrier;
mod fire_barge;
mod yellow_jacket;
mod barge;
mod scout;
mod healing;
mod mining;
mod warp;
mod shot_outcomes;
use self::warp::GameWarpGateSession;
use self::barge::GameBargeCharge;
use self::striker::GameStrikerRuntime;
pub use self::striker::GameStrikerCarrier;
pub use self::warp::WARP_GATE_OPENING_SECONDS;
pub use self::mining::{is_mining_ship_type, MINING_FACTORY_TYPE, MINING_CARPENTER_BEE_TYPE};
pub use self::shot_outcomes::{ProjectileRootOutcome, ProjectileOutcomeMeasurements};
use self::shot_outcomes::{ProjectileOutcomeTracker, NEAR_MISS_DISTANCE};
pub use self::scout::{NativeBeaconTargetSource, ScoutBeaconSpec, ScoutBeaconStatus, SCOUT_TYPE};
use self::scout::PolicyScoutRuntime;
use self::policy::{PolicyShipRuntime, PolicyShipSpecial};
pub use self::policy::PolicySpecialObservations;
pub use self::policy::WARP_GATE_TYPE;
pub use self::healing::BEEHIVE_TYPE;
use self::weapons::{PendingTurretShot, ShipWeapons};
pub use self::weapons::{NativeFirePattern, NativeTurretSpec, NativeWeaponCharge};
pub use self::weapons::{NativeTurretRangeSpec, NativeWeaponRange, NativeWeaponTargetGeometry};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatWorldError {
    InvalidCommandAttribution,
    Physics(PhysicsError),
    Combat(CombatError),
    Reservation(ReservationError),
    Navigation(NavigationRequestError),
    NavigationMap(crate::navigation::NavigationError),
    /// A background navigation worker failed, not a valid path-not-found.
    NavigationWorkerUnavailable,
    NavigationWorkerPanicked,
    NavigationWorkerProtocol,
    MissingGameMovementProfile,
    InvalidGameMovementProfile,
    InvalidGameMovementOrder,
    InvalidGameSpeed,
    UnknownProjectile,
    UnknownShip,
    UnknownObstacle,
    InvalidObstacle,
    InvalidExtractionZone,
    MissingShipCollisionProfile,
    InvalidShipCollisionProfile,
    InvalidFragmentationConfiguration,
    FragmentRandomRequired,
    EntityIdExhausted,
    MissingRocketStats,
    InvalidFleetIdentity,
    InvalidBombTarget,
    DuplicateEntity,
    InvalidSide,
    InvalidLiveShipSpawn,
    InvalidLiveProjectileSpawn,
    InvalidProjectilePower,
    InvalidProjectileType(u8),
    /// Never fabricate simplified behavior for unported weapons.
    UnportedProjectileType(u8),
    UnsupportedPolicyActions,
    InvalidPolicyAction(ActionError),
    UnknownPolicyAgent(u64),
    DuplicatePolicyAgent(u64),
    InvalidPolicyTurret,
    InvalidWeaponRangeGeometry,
    MissingWeaponRangeGeometry,
    MissingLevelGeometry,
    InvalidLevelGeometry,
    MissingWeaponTargetingGeometry,
    InvalidWeaponTargetingGeometry,
    InvalidAutomaticTargeting,
    MissingAutomaticTargetingInputs,
    MissingShipTargetGeometry,
    InvalidPolicyStriker,
    InvalidDroneCarrier,
    InvalidGameStriker,
    InvalidPolicyFireBarge,
    InvalidPolicyYellowJacket,
    InvalidPolicyBarge,
    InvalidPolicyScout,
    InvalidPolicyHealing,
    InvalidPolicyMining,
    InvalidPolicyWarp,
    UnsupportedPolicyTurret { weapon_type: u8, projectile_type: u8 },
    DuplicatePolicyTurret { ship_id: i64, slot: usize },
    MapObject(MapObjectError),
}

impl From<MapObjectError> for CombatWorldError {
    fn from(value: MapObjectError) -> Self { Self::MapObject(value) }
}
impl From<PhysicsError> for CombatWorldError {
    fn from(value: PhysicsError) -> Self { Self::Physics(value) }
}
impl From<CombatError> for CombatWorldError {
    fn from(value: CombatError) -> Self { Self::Combat(value) }
}
impl From<ReservationError> for CombatWorldError {
    fn from(value: ReservationError) -> Self { Self::Reservation(value) }
}
impl From<NavigationRequestError> for CombatWorldError {
    fn from(value: NavigationRequestError) -> Self { Self::Navigation(value) }
}
impl From<crate::navigation::NavigationError> for CombatWorldError {
    fn from(value: crate::navigation::NavigationError) -> Self { Self::NavigationMap(value) }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CombatShip {
    pub entity_id: i64,
    pub side: i32,
    pub ship_type: u8,
    pub vitals: ShipVitals,
}

/// The original Unity ship SizeClass is a float (unlike asteroid SizeClass).
/// OriginalHealth comes from ConfigData.GetShipInfo and survives reductions
/// of MaxHealth in training; it cannot be inferred from current ShipVitals.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ShipCollisionProfile {
    pub size_class: f32,
    pub original_health: i32,
}

impl ShipCollisionProfile {
    pub fn valid(self) -> bool {
        self.size_class.is_finite() && self.size_class >= 0.0 &&
            self.original_health > 0
    }
}

/// The Level issues one gameplay point order. Bounds and obstacle activation
/// are supplied explicitly from the Level, rather than inferred from the
/// broadphase grid or the RL curriculum. Coordinates must already share
/// the native physics world's map frame.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GamePointOrder {
    pub destination: Vec2,
    pub ship_clearance: i32,
    pub map_bounds: Aabb,
    pub has_obstacles: bool,
    pub can_override_bounds: bool,
    /// CollisionAsteroid proximity enter forces a fresh route even when
    /// the endpoint is unchanged.
    pub newly_found_obstacle: bool,
}

impl GamePointOrder {
    pub fn valid(self) -> bool {
        self.destination.is_finite() && self.ship_clearance >= 0 &&
        self.map_bounds.min.is_finite() && self.map_bounds.max.is_finite() &&
        self.map_bounds.min.x <= self.map_bounds.max.x &&
        self.map_bounds.min.y <= self.map_bounds.max.y
    }

    pub fn bounded_destination(self) -> Vec2 {
        if self.can_override_bounds { return self.destination; }
        Vec2::new(
            self.destination.x.clamp(self.map_bounds.min.x, self.map_bounds.max.x),
            self.destination.y.clamp(self.map_bounds.min.y, self.map_bounds.max.y),
        )
    }

    fn position_in_bounds(self, position: Vec2) -> bool {
        position.x >= self.map_bounds.min.x &&
        position.x <= self.map_bounds.max.x &&
        position.y >= self.map_bounds.min.y &&
        position.y <= self.map_bounds.max.y
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GamePointOrderResult {
    Unchanged,
    DeferredByMovementLock,
    Direct,
    Searching(NavigationRequestDisposition),
}

/// Per-projectile immutable owner information follows the shot after its
/// shooter's death, even if a pooled ship wrapper is reused for another unit.
/// Attribution IDs are carried from Projectile.Setup/Inheritance.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ShotOwner {
    pub shooter_id: i64,
    pub shooter_side: i32,
    pub shooter_is_fire_barge: bool,
    pub self_detonation: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub enum CombatEvent {
    Hit(HitEvent),
    UnattributedDamage { target_id: i64, delta: crate::combat::DamageDelta },
    BombDetonated { projectile_id: i64, target_id: i64 },
    /// Striker.TryToDropBombs created the real delayed StrikerBomb fuse.
    StrikerBombDropped {
        ship_id: i64, target_id: i64, projectile_id: i64,
    },
    StrikerBombReloaded { ship_id: i64, carrier_id: i64 },
    /// FireBarge.Kill creates type-14 explosion on self or combat death.
    /// ShotOwner.self_detonation differentiates the two reward sources.
    FireBargeDetonated { ship_id: i64, projectile_id: i64 },
    /// Yellow Jacket's direct reciprocal Bomb hits have no projectile.
    YellowJacketDetonated { ship_id: i64, target_id: i64 },
    BargeChargeStarted { ship_id: i64 },
    BargeChargeHit { ship_id: i64, target_id: i64 },
    BargeChargeStopped { ship_id: i64 },
    /// An authored Beacon ship joined the active roster and inherits
    /// the parent's persistent FleetShip identity (not its runtime ID).
    ScoutBeaconDropped {
        scout_id: i64, beacon_id: i64, parent_fleet_id: i64,
    },
    /// A real health restoration at an authored Beehive.HealCollider.
    /// This is NOT a damage hit and never changes kill attribution.
    CapabilityHealed {
        ship_id: i64, beehive_id: i64, side: i32,
        healed_health: i32, tsv_gain: i32,
    },
    /// A real Factory/CarpenterBee mined this amount from a physical
    /// MiningAsteroid. The owning Level commits the delta to its persistent
    /// FleetShip before reconciling economic rewards or observations.
    CapabilityMined {
        ship_id: i64, obstacle_id: i64, side: i32, amount_mined: i32,
    },
    /// MiningAsteroid.Kill(false) retires this obstacle and releases its
    /// command/renderer ownership. No collision-asteroid fragmentation.
    MiningAsteroidDepleted { obstacle_id: i64 },
    /// A safe EndKill via actual WarpGate child collider. Must precede
    /// ShipDied to preserve mined TSV at the economic event consumer.
    ShipWarped {
        ship_id: i64, gate_id: i64, side: i32,
        preserved_tsv: i32,
    },
    /// Emitted for projectile impacts against static geometry; the
    /// dedicated RL coordinator determines the corresponding reward.
    StaticObstacleImpact { projectile_id: i64, obstacle_id: i64 },
    ShipExtracted { ship_id: i64, side: i32, preserved_tsv: i32 },
    MapObjectDamaged {
        projectile_id: i64,
        object_id: i64,
        damage: MapObjectDamage,
    },
    MapObjectDestroyed { object_id: i64, projectile_id: i64 },
    /// CanisterBomb.Kill spawns a separate type-15 RocketExplosion,
    /// attributed to the incoming projectile's original shooter/shot.
    FireTankExploded {
        object_id: i64,
        triggering_projectile_id: i64,
        explosion_projectile_id: i64,
    },
    /// The linked obstacle is removed from gameplay; the render client
    /// may spawn the authored cosmetic debris without affecting RL RNG.
    FireTankObstacleBrokenApart {
        object_id: i64,
        obstacle_id: i64,
        origin: Vec2,
        bounds: Aabb,
    },
    AsteroidDamaged {
        projectile_id: i64,
        obstacle_id: i64,
        damage: AsteroidDamage,
    },
    /// CollisionAsteroid.Kill(false) must also remove pathfinder/registry
    /// ownership and generate shards according to its original SizeClass.
    AsteroidDestroyed {
        obstacle_id: i64,
        size_class: u8,
        breakup: AsteroidBreakup,
    },
    /// MapBorder.OnTriggerExit2D calls Kill(true) on a previously entered
    /// asteroid, without generating shards.
    AsteroidExitedMap { obstacle_id: i64 },
    /// An asteroid/asteroid hull collision applies damage immediately but
    /// leaves any newly zero-health bodies active for their delayed kill.
    AsteroidsCollided {
        first_obstacle_id: i64,
        second_obstacle_id: i64,
        first_damage: AsteroidDamage,
        second_damage: AsteroidDamage,
    },
    AsteroidHitShip {
        obstacle_id: i64,
        ship_id: i64,
        asteroid_damage: AsteroidDamage,
    },
    AsteroidShardSpawned {
        parent_id: i64,
        shard_id: i64,
        prefab_number: u8,
    },
    AsteroidPieceSpawned {
        parent_id: i64,
        piece_id: i64,
        prefab_number: u8,
    },
    AsteroidPieceEnded { piece_id: i64 },
    ShipDied {
        victim_id: i64,
        killer_id: i64,
        command_outcome_id: i64,
        policy_binding_revision: Option<u64>,
    },
    ProjectileEnded {
        projectile_id: i64,
    },
    /// One final root outcome, after the last live split/explosion child
    /// ends. Only policy-controlled turret shots enter this episode tracker.
    ProjectileRootCompleted(ProjectileRootOutcome),
    ProjectileContinued {
        parent_id: i64,
        continuation_id: i64,
    },
    /// An authored rotating turret launched a real physical shot through
    /// the same projectile spawn/impact path as rendered gameplay.
    PolicyWeaponFired {
        ship_id: i64, slot: u8, projectile_id: i64,
    },
    /// Ordinary manually controlled turret; no policy trajectory attribution.
    GameWeaponFired { ship_id: i64, slot: u8, projectile_id: i64 },
}

enum ImpactBehavior {
    Direct,
    Splitter { split_count: u8 },
    StrikerBomb(StrikerBombFuse),
    Rocket { acceleration_timer: ScaledTimer, max_speed: f32 },
    Penetrating(PiercingShot),
    /// LaserBeam is a persistent, growing collider driven by its real
    /// BeamCannon mount. Its piercing ledger survives every fixed step.
    Beam { piercing: PiercingShot, length_scale: f32, just_spawned: bool },
    Explosion { contacts: ExplosionContacts, timeline: ExplosionTimeline },
}

impl ImpactBehavior {
    fn for_type(
        projectile_type: u8,
        power: i32,
        rocket_max_speed: Option<f32>,
        explosion_events: Arc<[ExplosionEvent]>,
        split_count: Option<u8>,
    ) -> Result<Self, CombatWorldError> {
        match projectile_type {
            1 | 2 | 3 | 6 | 7 | 10 | 11 => Ok(Self::Direct),
            4 => Ok(Self::Penetrating(PiercingShot::new(PiercingType::PowerShot, power))),
            8 => Ok(Self::Beam {
                piercing: PiercingShot::new(PiercingType::LaserBeam, power),
                length_scale: 1.0,
                just_spawned: true,
            }),
            13..=15 => Ok(Self::Explosion {
                contacts: ExplosionContacts::new(),
                timeline: ExplosionTimeline::new(explosion_events),
            }),
            5 => Ok(Self::Rocket {
                acceleration_timer: ScaledTimer::new(0.1, true, true),
                max_speed: rocket_max_speed.ok_or(CombatWorldError::MissingRocketStats)?,
            }),
            12 => Err(CombatWorldError::InvalidBombTarget),
            9 => Ok(Self::Splitter {
                split_count: split_count.ok_or(CombatWorldError::InvalidProjectileType(9))?,
            }),
            _ => Err(CombatWorldError::InvalidProjectileType(projectile_type)),
        }
    }

    fn drains_all_contacts(&self) -> bool {
        matches!(self, Self::Explosion { .. })
    }
}

pub struct CombatProjectile {
    pub entity_id: i64,
    pub projectile_type: u8,
    pub owner: ShotOwner,
    pub power: i32,
    /// Exact shooter's shot-root and Hive Mind command identity live in the
    /// paired ProjectileBody.motion.attribution.
    pub ignored_ships: HashSet<i64>,
    /// Source Weapon reference, scoped by ShotOwner.shooter_id and mount slot.
    /// Inherited by continuations; never inferred from projectile type or roots.
    source_turret_slot: Option<u8>,
    within_source_range: bool,
    behavior: ImpactBehavior,
}

impl CombatProjectile {
    pub fn new(
        entity_id: i64,
        projectile_type: u8,
        owner: ShotOwner,
        power: i32,
        rocket_max_speed: Option<f32>,
        explosion_events: Arc<[ExplosionEvent]>,
        split_count: Option<u8>,
    ) -> Result<Self, CombatWorldError> {
        if power < 0 { return Err(CombatWorldError::InvalidProjectilePower); }
        let behavior = ImpactBehavior::for_type(
            projectile_type, power, rocket_max_speed, explosion_events, split_count,
        )?;
        Ok(Self {
            entity_id, projectile_type, owner, power,
            ignored_ships: HashSet::new(), source_turret_slot: None, within_source_range: false, behavior,
        })
    }

    pub fn new_striker_bomb(
        entity_id: i64,
        owner: ShotOwner,
        power: i32,
        contacted_ship_id: i64,
        shooter_fleet_id: i64,
    ) -> Result<Self, CombatWorldError> {
        if power < 0 {
            return Err(CombatWorldError::InvalidProjectilePower);
        }
        if shooter_fleet_id == 0 {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        Ok(Self {
            entity_id,
            projectile_type: 12,
            owner,
            power,
            ignored_ships: HashSet::new(),
            source_turret_slot: None, within_source_range: false,
            behavior: ImpactBehavior::StrikerBomb(
                StrikerBombFuse::new(contacted_ship_id, shooter_fleet_id),
            ),
        })
    }

    pub fn set_explosion_harmless(&mut self, harmless: bool) {
        if let ImpactBehavior::Explosion { contacts: explosion, .. } = &mut self.behavior {
            explosion.harmless = harmless;
        }
    }

    fn is_explosion(&self) -> bool {
        self.behavior.drains_all_contacts()
    }

    fn advance_explosion_timeline(&mut self, dt: f32) {
        if let ImpactBehavior::Explosion { timeline, .. } = &mut self.behavior {
            timeline.advance(dt);
        }
    }

    fn next_explosion_action(&mut self) -> Option<ExplosionAction> {
        if let ImpactBehavior::Explosion { timeline, .. } = &mut self.behavior {
            timeline.take_due()
        } else {
            None
        }
    }
}

/// Cumulative, allocation-free tracked-order workload counters. A renderer
/// or training dashboard can compare successive snapshots to measure actual
/// A* suppression rather than infer it from total simulation throughput.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TrackedOrderMeasurements {
    pub requests: u64,
    pub movement_lock_deferrals: u64,
    pub active_path_suppressed: u64,
    pub failed_path_retry_suppressed: u64,
    pub small_target_change_suppressed: u64,
    pub one_second_gate_suppressed: u64,
    /// Reached the canonical MoveToPoint branch (not necessarily pathfound).
    pub forwarded_orders: u64,
}

/// Per-world, allocation-free special-action workload counters. Snapshot
/// and subtract values to measure experiment windows without per-step logs.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PolicySpecialMeasurements {
    pub requests: u64,
    pub striker_hull_checks: u64,
    pub striker_distance_rejections: u64,
    pub striker_exact_overlap_tests: u64,
    pub striker_contact_misses: u64,
    pub striker_bombs_dropped: u64,
    pub striker_reloads: u64,
    /// Shared exact-contact acceleration for large Striker/Yellow Jacket
    /// battles. Small worlds use the direct scan and incur no grid rebuild.
    pub special_contact_grid_queries: u64,
    pub special_contact_grid_rebuilds: u64,
    pub special_contact_grid_candidates: u64,
    pub fire_barge_detonations: u64,
    pub fire_barge_death_explosions: u64,
    pub yellow_jacket_contact_misses: u64,
    pub yellow_jacket_hull_checks: u64,
    pub yellow_jacket_distance_rejections: u64,
    pub yellow_jacket_exact_overlap_tests: u64,
    pub yellow_jacket_detonations: u64,
    pub barge_charges_started: u64,
    pub barge_broadphase_candidates: u64,
    pub barge_ship_grid_rebuilds: u64,
    pub barge_contact_checks: u64,
    pub barge_contact_hits: u64,
    pub barge_charges_stopped: u64,
    pub scout_beacons_spawned: u64,
    pub scout_spawns_blocked_by_cap: u64,
    pub mining_requests: u64,
    pub mining_actions_completed: u64,
    pub mining_obstacles_depleted: u64,
    pub healing_requests: u64,
    pub healing_touch_checks: u64,
    pub healing_actions_completed: u64,
    pub warp_requests: u64,
    pub warp_actions_completed: u64,
}

/// Cumulative work and delivered bomb count for authored game commands.
/// This is separate from policy special-action request measurements.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GameBombMeasurements {
    pub striker_contact_checks: u64,
    pub striker_contact_misses: u64,
    pub striker_bombs_dropped: u64,
    pub striker_reloads: u64,
    pub yellow_jacket_contact_checks: u64,
    pub yellow_jacket_contact_misses: u64,
    pub yellow_jacket_detonations: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GameBargeMeasurements {
    pub starts: u64,
    pub stops: u64,
    /// Source MapBorder.OnTriggerEnter2D stopped an active game Charge.
    pub border_stops: u64,
    pub completions: u64,
    pub grid_rebuilds: u64,
    pub candidates: u64,
    pub contact_checks: u64,
    pub hits: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ProjectileStorageMetrics {
    pub allocated_slots: usize,
    pub active_projectiles: usize,
    pub slots_reused: u64,
}

/// Monitors whether asteroid churn grows memory or reuses inactive bodies.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObstacleStorageMetrics {
    pub allocated_slots: usize,
    pub active_obstacles: usize,
    pub slots_reused: u64,
}

/// Stage-owned ordinary asteroid prefab pool and setup workload. The
/// canonical CombatWorld owns active instances and source-derived events;
/// these are compact counters, never per-asteroid telemetry records.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FullAsteroidPoolMeasurements {
    pub active: usize,
    pub inactive: usize,
    pub created_profiles: u64,
    pub reused_profiles: u64,
    pub total_destination_draws: u64,
    pub exhausted_destination_searches: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AsteroidBorderState {
    pub has_touched_map_border: bool,
    pub has_entered_map: bool,
}

/// Fragment settings are explicitly owned by the game Level/Stage, while
/// the prefab/geometry catalog is immutable and shared across worlds.
#[derive(Clone, Debug)]
pub struct AsteroidFragmentationConfig {
    pub catalog: Arc<CollisionAsteroidCatalog>,
    pub palette: AsteroidFragmentPalette,
    pub settings: FragmentSpawnSettings,
}

impl AsteroidFragmentationConfig {
    fn effective_shard_health(
        &self,
        prefab_number: u8,
    ) -> Result<Option<i32>, CombatWorldError> {
        let Some(ratio) = self.settings.training_health_ratio else {
            return Ok(None);
        };
        let asset = self.catalog.by_number(prefab_number)
            .ok_or(CombatWorldError::InvalidFragmentationConfiguration)?;
        let original_health = i32::from(asset.size_class)
            .checked_mul(self.catalog.health_increment())
            .ok_or(CombatWorldError::InvalidFragmentationConfiguration)?;
        let result = (original_health as f32 * ratio).ceil();
        if !result.is_finite() || result > i32::MAX as f32 {
            return Err(CombatWorldError::InvalidFragmentationConfiguration);
        }
        Ok(Some(result.max(1.0) as i32))
    }
}

#[derive(Clone, Copy, Debug)]
struct PendingAsteroidBreakup {
    parent_id: i64,
    size_class: u8,
    position: Vec2,
    angular_velocity_degrees: f32,
}

/// State and scratch are owned per arena. No process globals, no cross-world
/// static dictionaries. Dense arrays use identical indices to physics bodies;
/// ID maps are only used when routing collision results.
#[derive(Clone, Copy, Debug, Default)]
struct EnemyShipContact {
    target_id: Option<i64>,
    candidates: u64,
    distance_rejections: u64,
    exact_tests: u64,
}

pub struct CombatWorld {
    physics: KinematicPhysics,
    projectile_assets: Arc<ProjectileAssetCatalog>,
    next_entity_id: i64,
    reserved_ship_ids: HashSet<i64>,
    ships: Vec<CombatShip>,
    /// Actual live MapObjects: paired with ObstacleBody collider slots in
    /// the same KinematicPhysics world, not perception-only replicas.
    map_objects: HashMap<i64, CombatMapObject>,
    range_visible_map_objects: HashSet<i64>,
    extraction: Option<extraction::ExtractionState>,
    /// Same Level-owned learned visibility in gameplay, evaluation and
    /// training. Replaced with the world on episode reset.
    discovery: LevelDiscovery,
    /// Reused across each native vision refresh so validating the live
    /// observer roster does not allocate for every training decision.
    vision_observer_ids: HashSet<i64>,
    /// Reused broadphase ship indices for faction vision. The physics grid
    /// remains the sole spatial index; sorting preserves Unity ship order.
    vision_ship_candidates: Vec<usize>,
    /// One reused broadphase buffer, not a per-observer candidate list.
    /// Obstacle vision runs for all observers before ship-grid queries.
    vision_obstacle_candidates: Vec<usize>,
    /// Only real first sightings need buffering to restore the exact Unity
    /// observer-major ship-before-obstacle event order after grid batching.
    vision_obstacle_sightings: Vec<VisionFirstSighting>,
    vision_obstacle_offsets: Vec<usize>,
    /// Transient remaining-unlearned count per side during one refresh.
    vision_unlearned_obstacles: HashMap<i32, u64>,
    /// Source GameState.HiveMindLiveEnemyVisible: recalculated on every
    /// vision refresh, never retained like the faction discovery cache.
    vision_live_enemy_sides: HashSet<i32>,
    vision_measurements: VisionSweepCounts,
    projectiles: Vec<CombatProjectile>,
    obstacles: Vec<CombatObstacle>,
    free_projectile_slots: Vec<usize>,
    projectile_slot_reuses: u64,
    free_obstacle_slots: Vec<usize>,
    obstacle_slot_reuses: u64,
    ship_by_id: HashMap<i64, usize>,
    /// Updated only at canonical spawn/death boundaries. Scout RL action
    /// masks can enforce a team cap in O(1), even in large battles.
    live_ships_by_side: HashMap<i32, usize>,
    /// Updated at the exact same registration/retirement boundary.
    /// Eliminates another full roster scan per agent self-observation.
    live_ship_total: usize,
    /// Unity GameState.IsSideKilled checks any living IsMobile ship.
    /// Barge charging/cooldown changes CurrentSpeed, NOT IsMobile;
    /// this count tracks the immutable authored base mobility flag.
    live_mobile_ships_by_side: HashMap<i32, usize>,
    /// Each temporary Beacon has its own runtime ID and records the source
    /// Scout. The physical squad formation remains a Level adapter concern.
    beacon_parent_by_id: HashMap<i64, i64>,
    /// Prefab-derived Beacon health, motion and collider, supplied at setup.
    /// An RL Scout never invents missing authored prefab data at runtime.
    scout_beacon_specs: HashMap<i64, ScoutBeaconSpec>,
    /// Non-policy gameplay Scouts retain charges/cooldown across HiveMind
    /// command replacement, rather than each Scouting command resetting it.
    game_scout_runtime: HashMap<i64, PolicyScoutRuntime>,
    /// Player-initiated specials execute after the next fixed-step event
    /// buffer reset. Do not spawn physical ships from an idle UI frame.
    pending_player_scout_beacons: Vec<i64>,
    navigation_requests: NavigationRequests,
    game_ship_steering: HashMap<i64, GameShipSteering>,
    /// The Level-owned policy controller stores actual ongoing action
    /// state for each bound ship; no secondary training-only movement world.
    // Stable ship-ID iteration is necessary for reproducible shot IDs.
    policy_ship_runtime: BTreeMap<i64, PolicyShipRuntime>,
    ship_weapons: BTreeMap<i64, ShipWeapons>,
    automatic_weapon_measurements: AutomaticWeaponMeasurements,
    automatic_rank_candidates: Vec<crate::targeting::NativeEnemyTargetStats>,
    automatic_distance_keys: HashMap<i64, f32>,
    weapon_rank_history: weapon_rank_history::WeaponRankHistory,
    combat_activity: HashMap<i64, NativeCombatActivity>,
    level_geometry: Option<NativeLevelGeometry>,
    ship_target_front_offsets: HashMap<i64, f32>,
    pending_weapon_range_exits: u64,
    weapon_range_candidates: Vec<usize>,
    weapon_range_entering_ids: Vec<i64>,
    special_weapons: BTreeMap<i64, special_weapon_contacts::SpecialWeaponContacts>,
    special_body_entries: Vec<(i64, i64)>,
    special_weapon_measurements: SpecialWeaponMeasurements,
    /// Updated only when a policy controller is bound or retired.
    /// Decision cadence checks this revision instead of rescanning the
    /// entire controller roster on every fixed physics step.
    policy_roster_revision: u64,
    policy_decision_seen: HashSet<u64>,
    pending_turret_shots: Vec<PendingTurretShot>,
    /// Validated ship specials use one queue across different authored ships.
    /// Commit after event clearing and before physics, in stable ship-ID order.
    policy_pending_ship_specials: Vec<i64>,
    /// v26 SpecialAction.Healing (branch choice 3) is distinct from
    /// ship-class specials (choice 1); both remain in the one world.
    policy_pending_mining: Vec<i64>,
    policy_pending_healing: Vec<i64>,
    policy_pending_warp: Vec<i64>,
    /// Sorted IDs of living ships with imported child support colliders.
    /// Indexed by (authored ship type, side); the ship and physics arrays
    /// remain authoritative. Used by WarpGate and Beehive action masks.
    support_zone_ids: HashMap<(u8, i32), Vec<i64>>,
    /// Unity Beehive.ShipsHealingHere is a shared four-ship resource across
    /// every squad's Heal command, not one reservation set per command.
    healing_hive_by_ship: HashMap<i64, i64>,
    healing_slots_used_by_hive: HashMap<i64, u8>,
    /// Shared FullRetreat/live-game WarpGate opening sessions. Inactive
    /// gates have no timer or per-step cost.
    game_warp_gate_sessions: HashMap<i64, GameWarpGateSession>,
    game_warp_gate_by_ship: HashMap<i64, i64>,
    /// WarpGate callback completions execute at the next normal fixed
    /// combat event boundary, never after that tick's reward pass.
    pending_game_warps: Vec<(i64, i64)>,
    /// Unity Ship.OriginalHealth remains fixed when training overrides
    /// MaxHealth; the gameplay Heal priority must use the original value.
    authored_original_ship_health: HashMap<i64, i32>,
    /// Monotonic IDs of living authored mining obstacles; no full scan
    /// of static geometry for each eligible policy agent.
    mining_obstacle_ids: Vec<i64>,
    /// Reused extraction validation set: large mining squads must not
    /// pay a quadratic cost to detect repeated ship references.
    mining_seen_ship_ids: HashSet<i64>,
    policy_special_measurements: PolicySpecialMeasurements,
    game_bomb_measurements: GameBombMeasurements,
    /// Persistent game Striker readiness is ship-owned, not reset when
    /// a HiveMind BombingRun command is superseded or timed out.
    game_strikers: BTreeMap<i64, GameStrikerRuntime>,
    /// Authored Drone.Carrier for game and neural-controlled children.
    /// Strikers retain their separate live relation in ship-special state.
    drone_carriers: HashMap<i64, Option<i64>>,
    /// Scripted Barge coroutine phase lives in the shared combat world;
    /// the HiveMind command owns only its target choice and finalization.
    game_barge_charges: BTreeMap<i64, GameBargeCharge>,
    /// Authored Charge.Power plus per-Barge bounds capability. Both are
    /// copied from the same source special used by policy and AI.
    game_barge_player_config: HashMap<i64, (i32, bool)>,
    /// Manual ChargeForward starts inside fixed_step, never in the UI
    /// between event-reset and roster/reward reconciliation.
    pending_player_barge_charges: Vec<i64>,
    game_barge_measurements: GameBargeMeasurements,
    tracked_order_measurements: TrackedOrderMeasurements,
    /// One hit per victim for each active charge; cleared when the
    /// owning Barge starts another charge or is removed from the world.
    barge_charge_hits: HashSet<(i64, i64)>,
    /// Reused current-grid candidate buffer. No per-charge per-tick Vec
    /// allocation even in battles with many active Barges.
    barge_contact_candidates: Vec<usize>,
    /// Reused broadphase scratch for scripted and policy bomb contacts.
    special_contact_candidates: Vec<usize>,
    pending_game_point_orders: HashMap<i64, GamePointOrder>,
    latest_game_point_orders: HashMap<i64, GamePointOrder>,
    ship_collision_profiles: HashMap<i64, ShipCollisionProfile>,
    /// Collision-asteroid proximity contacts in entry order for each ship.
    nearby_asteroids_by_ship: HashMap<i64, Vec<i64>>,
    /// Unity NearbyAsteroidDoubleCheck runs at one-second intervals while
    /// the ship has nearby collision asteroids. One timer per ship avoids
    /// repeated route invalidation for multiple overlapping sensors.
    asteroid_navigation_refresh: HashMap<i64, ScaledTimer>,
    obstacle_by_id: HashMap<i64, usize>,
    /// Source of truth for static pathfinding geometry invalidation. Only
    /// actual StaticObstacle admission increments this revision. Animation
    /// is separately recorded because its pose advances in physics.step.
    static_navigation_revision: u64,
    moving_static_navigation_obstacles: bool,
    asteroid_border_states: HashMap<i64, AsteroidBorderState>,
    /// Directional child-proximity contacts; a primary collider entry only
    /// becomes a collision after the owning asteroid detects its neighbor.
    nearby_asteroid_pairs: HashSet<(i64, i64)>,
    /// Every shard spawned by one asteroid destruction shares the parent's
    /// immutable entity ID as its family key. Non-shard asteroids are absent.
    asteroid_shard_family: HashMap<i64, i64>,
    fragmentation: Option<AsteroidFragmentationConfig>,
    full_asteroid_stage: Option<FullAsteroidStage>,
    active_full_asteroid_profiles: HashMap<i64, FullAsteroidPrototype>,
    pooled_full_asteroid_profiles: Vec<FullAsteroidPrototype>,
    full_asteroid_pool_work: FullAsteroidPoolMeasurements,
    pending_asteroid_breakups: Vec<PendingAsteroidBreakup>,
    active_shard_prototypes: HashMap<i64, FragmentPrototype>,
    pooled_shard_prototypes: Vec<FragmentPrototype>,
    pieces: Vec<AsteroidPieceState>,
    free_piece_slots: Vec<usize>,
    pooled_piece_prototypes: Vec<FragmentPrototype>,
    asteroid_delayed_kills: HashMap<i64, ScaledTimer>,
    expired_asteroid_ids: Vec<i64>,
    fleet_id_by_ship: HashMap<i64, i64>,
    /// Authoritative per-ship Bomb.Power, also for non-policy Fire Barges.
    /// Consumed once at Kill; no second explosion can be spawned.
    fire_barge_bomb_powers: HashMap<i64, i32>,
    /// Actual authored Bomb.Weapon.Id per game-controlled bomber. Power
    /// and target damage remain live authoritative data, not cached copies.
    /// This identity is retained solely for death-time lease release.
    game_bomb_weapon_by_ship: HashMap<i64, i64>,
    /// Command-timer self-detonations are settled inside the NEXT normal
    /// physics step so roster, rewards, deaths and outbox see them once.
    pending_game_fire_barges: Vec<(i64, Option<i64>)>,
    projectile_by_id: HashMap<i64, usize>,
    /// Per-world RL root-shot lifetime, not per-projectile hit telemetry.
    shot_outcomes: ProjectileOutcomeTracker,
    /// Reused near-pass candidates from the shared ship spatial grid.
    projectile_near_candidates: Vec<usize>,
    reservations: DamageReservations,
    pending_contacts: Vec<TriggerEnter>,
    processing_contacts: Vec<TriggerEnter>,
    pending_obstacle_contacts: Vec<ObstacleTriggerEnter>,
    processing_obstacle_contacts: Vec<ObstacleTriggerEnter>,
    processed_this_tick: HashSet<i64>,
    processed_obstacles_this_tick: HashSet<i64>,
    events: Vec<CombatEvent>,
    pub shots_harmless: bool,
    /// Dedicated RL episodes cannot apply further hits after game over.
    pub frozen_after_elimination: bool,
}

impl CombatWorld {
    pub fn new(
        area: Aabb,
        cell_size: f32,
        projectile_assets: Arc<ProjectileAssetCatalog>,
    ) -> Result<Self, CombatWorldError> {
        Ok(Self {
            physics: KinematicPhysics::new(area, cell_size)?,
            projectile_assets,
            next_entity_id: 1,
            reserved_ship_ids: HashSet::new(),
            ships: Vec::new(),
            map_objects: HashMap::new(),
            range_visible_map_objects: HashSet::new(),
            extraction: None,
            discovery: LevelDiscovery::new(),
            vision_observer_ids: HashSet::new(),
            vision_ship_candidates: Vec::new(),
            vision_obstacle_candidates: Vec::new(),
            vision_obstacle_sightings: Vec::new(),
            vision_obstacle_offsets: Vec::new(),
            vision_unlearned_obstacles: HashMap::new(),
            vision_live_enemy_sides: HashSet::new(),
            vision_measurements: VisionSweepCounts::default(),
            projectiles: Vec::new(),
            obstacles: Vec::new(),
            free_projectile_slots: Vec::new(),
            projectile_slot_reuses: 0,
            free_obstacle_slots: Vec::new(),
            obstacle_slot_reuses: 0,
            ship_by_id: HashMap::new(),
            live_ships_by_side: HashMap::new(),
            live_ship_total: 0,
            live_mobile_ships_by_side: HashMap::new(),
            beacon_parent_by_id: HashMap::new(),
            scout_beacon_specs: HashMap::new(),
            game_scout_runtime: HashMap::new(),
            pending_player_scout_beacons: Vec::new(),
            navigation_requests: NavigationRequests::new(),
            game_ship_steering: HashMap::new(),
            policy_ship_runtime: BTreeMap::new(),
            ship_weapons: BTreeMap::new(),
            automatic_weapon_measurements: AutomaticWeaponMeasurements::default(),
            automatic_rank_candidates: Vec::new(), automatic_distance_keys: HashMap::new(),
            weapon_rank_history: weapon_rank_history::WeaponRankHistory::default(),
            combat_activity: HashMap::new(),
            level_geometry: None, ship_target_front_offsets: HashMap::new(),
            pending_weapon_range_exits: 0,
            weapon_range_candidates: Vec::new(), weapon_range_entering_ids: Vec::new(),
            special_weapons: BTreeMap::new(), special_body_entries: Vec::new(),
            special_weapon_measurements: SpecialWeaponMeasurements::default(),
            policy_roster_revision: 0,
            policy_decision_seen: HashSet::new(),
            pending_turret_shots: Vec::new(),
            policy_pending_ship_specials: Vec::new(),
            policy_pending_mining: Vec::new(),
            policy_pending_healing: Vec::new(),
            policy_pending_warp: Vec::new(),
            support_zone_ids: HashMap::new(),
            healing_hive_by_ship: HashMap::new(),
            healing_slots_used_by_hive: HashMap::new(),
            game_warp_gate_sessions: HashMap::new(),
            game_warp_gate_by_ship: HashMap::new(),
            pending_game_warps: Vec::new(),
            authored_original_ship_health: HashMap::new(),
            mining_obstacle_ids: Vec::new(),
            mining_seen_ship_ids: HashSet::new(),
            policy_special_measurements: PolicySpecialMeasurements::default(),
            game_bomb_measurements: GameBombMeasurements::default(),
            game_strikers: BTreeMap::new(),
            drone_carriers: HashMap::new(),
            game_barge_charges: BTreeMap::new(),
            game_barge_player_config: HashMap::new(),
            pending_player_barge_charges: Vec::new(),
            game_barge_measurements: GameBargeMeasurements::default(),
            tracked_order_measurements: TrackedOrderMeasurements::default(),
            barge_charge_hits: HashSet::new(),
            barge_contact_candidates: Vec::new(),
            special_contact_candidates: Vec::new(),
            pending_game_point_orders: HashMap::new(),
            latest_game_point_orders: HashMap::new(),
            ship_collision_profiles: HashMap::new(),
            nearby_asteroids_by_ship: HashMap::new(),
            asteroid_navigation_refresh: HashMap::new(),
            obstacle_by_id: HashMap::new(),
            static_navigation_revision: 0,
            moving_static_navigation_obstacles: false,
            asteroid_border_states: HashMap::new(),
            nearby_asteroid_pairs: HashSet::new(),
            asteroid_shard_family: HashMap::new(),
            fragmentation: None,
            full_asteroid_stage: None,
            active_full_asteroid_profiles: HashMap::new(),
            pooled_full_asteroid_profiles: Vec::new(),
            full_asteroid_pool_work: FullAsteroidPoolMeasurements::default(),
            pending_asteroid_breakups: Vec::new(),
            active_shard_prototypes: HashMap::new(),
            pooled_shard_prototypes: Vec::new(),
            pieces: Vec::new(),
            free_piece_slots: Vec::new(),
            pooled_piece_prototypes: Vec::new(),
            asteroid_delayed_kills: HashMap::new(),
            expired_asteroid_ids: Vec::new(),
            fleet_id_by_ship: HashMap::new(),
            fire_barge_bomb_powers: HashMap::new(),
            game_bomb_weapon_by_ship: HashMap::new(),
            pending_game_fire_barges: Vec::new(),
            projectile_by_id: HashMap::new(),
            shot_outcomes: ProjectileOutcomeTracker::default(),
            projectile_near_candidates: Vec::new(),
            reservations: DamageReservations::new(),
            pending_contacts: Vec::new(),
            processing_contacts: Vec::new(),
            pending_obstacle_contacts: Vec::new(),
            processing_obstacle_contacts: Vec::new(),
            processed_this_tick: HashSet::new(),
            processed_obstacles_this_tick: HashSet::new(),
            events: Vec::new(),
            shots_harmless: false,
            frozen_after_elimination: false,
        })
    }

    /// One source-defined contact query for both Striker and Yellow Jacket.
    /// Small battles keep the direct scan; large battles reuse the same
    /// physical ship grid as projectiles and Barge charges. The actual
    /// contact predicate always uses authored hulls and collision layers.
    /// Lowest living enemy ID breaks otherwise unspecified contact ties.
    fn find_touching_enemy_ship(
        &mut self, ship_index: usize, collisions: &CollisionMatrix,
    ) -> EnemyShipContact {
        let side = self.ships[ship_index].side;
        let mut result = EnemyShipContact::default();
        // The direct loop avoids rebuilding an entire grid for 1v1 and
        // small-squad encounters. Dead slots are still cheap to skip.
        if self.ships.len() <= 32 {
            for index in 0..self.ships.len() {
                self.check_touching_enemy_candidate(
                    ship_index, index, side, collisions, &mut result,
                );
            }
        } else {
            let rebuilt = self.physics.current_ship_contact_candidates(
                ship_index, &mut self.special_contact_candidates,
            );
            self.policy_special_measurements.special_contact_grid_queries += 1;
            self.policy_special_measurements.special_contact_grid_rebuilds +=
                u64::from(rebuilt);
            self.policy_special_measurements.special_contact_grid_candidates +=
                self.special_contact_candidates.len() as u64;
            for i in 0..self.special_contact_candidates.len() {
                let index = self.special_contact_candidates[i];
                self.check_touching_enemy_candidate(
                    ship_index, index, side, collisions, &mut result,
                );
            }
        }
        result
    }

    #[inline]
    fn check_touching_enemy_candidate(
        &mut self, source: usize, index: usize, side: i32,
        collisions: &CollisionMatrix, result: &mut EnemyShipContact,
    ) {
        if index == source || self.ships[index].side == side ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return;
        }
        result.candidates += 1;
        match self.physics.ship_hulls_overlap_detailed(
            source, index, collisions,
        ) {
            ShipHullOverlap::Ineligible => {}
            ShipHullOverlap::RejectedByDistance =>
                result.distance_rejections += 1,
            ShipHullOverlap::ExactMiss => result.exact_tests += 1,
            ShipHullOverlap::Contact => {
                result.exact_tests += 1;
                let id = self.ships[index].entity_id;
                if result.target_id.map_or(true, |previous| id < previous) {
                    result.target_id = Some(id);
                }
            }
        }
    }

    /// Add one successful authored child-collider registration to the
    /// derived support index. Duplicate registrations update the same
    /// collider but do not add duplicate IDs to the index.
    fn register_support_zone(&mut self, ship_type: u8, side: i32, id: i64) {
        let registered = self.support_zone_ids.entry((ship_type, side))
            .or_default();
        if let Err(at) = registered.binary_search(&id) {
            registered.insert(at, id);
        }
    }

    /// The same canonical ship-retirement path removes all authored zone
    /// memberships. Empty keys are released, so no-zone action-mask checks
    /// do not scan the complete combat roster.
    fn retire_support_zone(&mut self, ship_type: u8, side: i32, id: i64) {
        let key = (ship_type, side);
        let empty = if let Some(registered) = self.support_zone_ids.get_mut(&key) {
            if let Ok(at) = registered.binary_search(&id) {
                registered.remove(at);
            }
            registered.is_empty()
        } else {
            false
        };
        if empty {
            self.support_zone_ids.remove(&key);
        }
    }

    /// All direct and projectile deaths share one world-owned retirement
    /// boundary: physics, sight, policy, path and collision-sensor membership
    /// cannot retain a dead ship. This does not impersonate Level's squad,
    /// economy or presentation cleanup, which belongs to the game adapter.
    fn retire_dead_ship(
        &mut self, index: usize, killer_id: i64, command_outcome_id: i64,
    ) -> Result<(), CombatWorldError> {
        self.retire_dead_ship_with_mode(index, killer_id, command_outcome_id, false)
    }

    /// EndKill skips combat death/explosion accounting but still removes
    /// the same physical ship, controller, vision and squad memberships.
    fn retire_dead_ship_with_mode(
        &mut self, index: usize, killer_id: i64, command_outcome_id: i64,
        end_kill: bool,
    ) -> Result<(), CombatWorldError> {
        debug_assert!(self.ships[index].vitals.is_dead());
        if !self.physics.ships()[index].active {
            return Ok(());
        }
        // Unity FireBarge.Kill creates the same explosion whether the
        // death came from its special, projectile fire or a collision.
        // Explicit self-detonations consume their bomb first, so this is
        // strictly once even when both paths reach this retirement.
        if !end_kill && self.ships[index].ship_type == 10 {
            self.spawn_fire_barge_explosion(index, false, 0)?;
        }
        let victim_id = self.ships[index].entity_id;
        // Unity Carrier.Kill reassigns all surviving CarrierShip children
        // BEFORE the dying Carrier is retired from Level.State.Ships.
        // Striker and policy Drone parent links share this death boundary.
        self.reassign_carrier_children_after_death(index);
        self.game_strikers.remove(&victim_id);
        self.drone_carriers.remove(&victim_id);
        self.game_barge_charges.remove(&victim_id);
        self.game_barge_player_config.remove(&victim_id);
        // Bomb.ReleaseTargetReservation is part of every authored bomber
        // Kill, not only successful BombingRun command finalization.
        if let Some(weapon_id) =
            self.game_bomb_weapon_by_ship.remove(&victim_id)
        {
            self.reservations.release(weapon_id);
        }
        let side = self.ships[index].side;
        let was_mobile = self.physics.ships()[index].motion.mobile;
        self.physics.ships_mut()[index].active = false;
        if was_mobile {
            if let Some(live) = self.live_mobile_ships_by_side.get_mut(&side) {
                debug_assert!(*live > 0, "mobile roster decrement underflow");
                *live = live.saturating_sub(1);
            }
        }
        if let Some(live) = self.live_ships_by_side.get_mut(&side) {
            debug_assert!(*live > 0, "side roster decrement underflow");
            *live = live.saturating_sub(1);
        }
        debug_assert!(self.live_ship_total > 0);
        self.live_ship_total = self.live_ship_total.saturating_sub(1);
        self.beacon_parent_by_id.remove(&victim_id);
        self.scout_beacon_specs.remove(&victim_id);
        self.game_scout_runtime.remove(&victim_id);
        self.discovery.retire(DiscoveryKind::Ship, victim_id);
        self.ship_weapons.remove(&victim_id);
        // Source Kill removes the victim from every weapon cache BEFORE later
        // targeting callbacks, not only on the next physical trigger sweep.
        for weapons in self.ship_weapons.values_mut() {
            for contacts in &mut weapons.range_contacts {
                self.pending_weapon_range_exits += u64::from(contacts.remove(victim_id));
            }
        }
        self.ship_target_front_offsets.remove(&victim_id);
        let policy_binding_revision = self.policy_ship_runtime.remove(&victim_id)
            .map(|binding| binding.bind_revision);
        if policy_binding_revision.is_some() {
            self.bump_policy_roster_revision();
        }
        self.retire_support_zone(self.ships[index].ship_type, side, victim_id);
        self.release_beehive_healing_slot(victim_id);
        self.release_game_warp_participant(victim_id);
        self.authored_original_ship_health.remove(&victim_id);
        if self.ships[index].ship_type == policy::WARP_GATE_TYPE {
            self.retire_game_warp_gate(victim_id);
        }
        if self.ships[index].ship_type == 2 {
            self.release_beehive_healing_slots_for_hive(victim_id);
        }
        self.barge_charge_hits.retain(|&(attacker, victim)|
            attacker != victim_id && victim != victim_id);
        self.navigation_requests.retire_ship(victim_id);
        self.game_ship_steering.remove(&victim_id);
        self.pending_game_point_orders.remove(&victim_id);
        self.latest_game_point_orders.remove(&victim_id);
        self.asteroid_navigation_refresh.remove(&victim_id);
        self.nearby_asteroids_by_ship.remove(&victim_id);
        self.events.push(CombatEvent::ShipDied {
            victim_id, killer_id, command_outcome_id, policy_binding_revision,
        });
        Ok(())
    }

    pub fn policy_special_measurements(&self) -> PolicySpecialMeasurements {
        self.policy_special_measurements
    }

    pub fn game_bomb_measurements(&self) -> GameBombMeasurements {
        self.game_bomb_measurements
    }

    pub fn game_barge_measurements(&self) -> GameBargeMeasurements {
        self.game_barge_measurements
    }

    pub fn tracked_order_measurements(&self) -> TrackedOrderMeasurements {
        self.tracked_order_measurements
    }

    pub fn physics(&self) -> &KinematicPhysics { &self.physics }
    pub fn events(&self) -> &[CombatEvent] { &self.events }
    pub fn ships(&self) -> &[CombatShip] { &self.ships }
    pub fn map_object(&self, entity_id: i64) -> Option<&CombatMapObject> {
        self.map_objects.get(&entity_id)
    }
    pub fn map_object_count(&self) -> usize { self.map_objects.len() }

    /// Live MapObjects in at least one active authored weapon trigger.
    pub fn range_visible_map_object_ids(&self) -> &HashSet<i64> {
        &self.range_visible_map_objects
    }
    /// Learned faction vision belongs to this Level's game state. Sight
    /// events must be validated by the authoritative vision detector before
    /// insertion, and removed entities retired via Level teardown.
    pub fn discovery(&self) -> &LevelDiscovery { &self.discovery }
    pub fn discovery_mut(&mut self) -> &mut LevelDiscovery { &mut self.discovery }

    /// Cumulative world-local vision cost since spawn/reset. Read it at the
    /// existing dashboard reporting cadence; the hot sweep only increments
    /// integer counters and retains observer-validation scratch memory.
    pub fn vision_measurements(&self) -> VisionSweepCounts {
        self.vision_measurements
    }

    /// Source GameState.HasLiveEnemyVisibleToHiveMind. Unlike learned enemy
    /// sightings, this transient flag is reset on each successful refresh.
    pub fn has_live_enemy_visible_to_hivemind(&self, side: i32) -> bool {
        self.vision_live_enemy_sides.contains(&side)
    }

    /// Unity's GameState.RecordHiveMindSighting is the sole transition from
    /// a verified observer->target contact into faction-wide learned ship
    /// memory. Call ONLY after the authoritative vision collider/geometric
    /// detector establishes that sighting. The caller supplies whether the
    /// observer is currently registered as a Hive Mind ship and whether
    /// RL ship setup suppresses learning.
    ///
    /// Returns true only for the first side-wide discovery; repeated
    /// observations from this or any other observer are not new rewards.
    /// Exiting range or an observer dying MUST NOT clear this memory.
    pub fn record_hivemind_ship_sighting(
        &mut self,
        observer_id: i64,
        spotted_id: i64,
        observer_registered: bool,
        setup_suppressed: bool,
    ) -> bool {
        if !observer_registered || setup_suppressed || observer_id == spotted_id {
            return false;
        }
        let Some(observer) = self.ship(observer_id) else { return false; };
        let Some(spotted) = self.ship(spotted_id) else { return false; };
        let side = observer.side;
        if side <= 0 || side == spotted.side ||
            observer.vitals.is_dead() || spotted.vitals.is_dead() {
            return false;
        }
        if !self.ship_body(observer_id).is_some_and(|body| body.active) ||
            !self.ship_body(spotted_id).is_some_and(|body| body.active) {
            return false;
        }
        self.discovery.record(side, DiscoveryKind::Ship, spotted_id)
            .unwrap_or(false)
    }

    /// O(1) live ship lookup through the world's canonical entity index.
    /// Squad commands must not scan all world ships for every formation
    /// member on each scaled tick.
    pub fn ship(&self, entity_id: i64) -> Option<&CombatShip> {
        let &index = self.ship_by_id.get(&entity_id)?;
        self.ships.get(index)
    }

    /// The Level supplies source Ship.OriginalHealth at setup. This is
    /// deliberately not inferred from training-adjusted Ship.MaxHealth.
    pub fn configure_game_ship_original_health(
        &mut self, ship_id: i64, original_health: i32,
    ) -> Result<(), CombatWorldError> {
        if original_health <= 0 || !self.ship(ship_id)
            .is_some_and(|ship| !ship.vitals.is_dead()) ||
            self.authored_original_ship_health.get(&ship_id)
                .is_some_and(|&existing| existing != original_health) {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        self.authored_original_ship_health.insert(ship_id, original_health);
        Ok(())
    }

    pub fn game_ship_original_health(&self, ship_id: i64) -> Option<i32> {
        self.authored_original_ship_health.get(&ship_id).copied()
    }

    /// Physics bodies and gameplay ships share the identical dense slot.
    pub fn ship_body(&self, entity_id: i64) -> Option<&ShipBody> {
        let &index = self.ship_by_id.get(&entity_id)?;
        self.physics.ships().get(index)
    }

    /// Living members include temporary/minion ships, as Unity's RL
    /// training-team cap requires; dead wrappers never occupy a slot.
    pub fn living_ships_on_side(&self, side: i32) -> usize {
        self.live_ships_by_side.get(&side).copied().unwrap_or(0)
    }

    pub fn total_living_ships(&self) -> usize {
        self.live_ship_total
    }

    /// One free slot is recorded when a projectile ends, exactly once, and
    /// consumed when that slot is reused. All other slots are live, so this
    /// avoids scanning the historical projectile pool on every fixed tick.
    /// This is derived from the existing canonical slot lifecycle: no extra
    /// counter or projectile registry is maintained.
    pub fn total_active_projectiles(&self) -> usize {
        self.projectiles.len() - self.free_projectile_slots.len()
    }

    /// Source GameState.IsSideKilled: immobile living targets/minions do
    /// not preserve a side's ability to continue fighting.
    pub fn living_mobile_ships_on_side(&self, side: i32) -> usize {
        self.live_mobile_ships_by_side.get(&side).copied().unwrap_or(0)
    }

    pub fn scout_beacon_parent(&self, beacon_id: i64) -> Option<i64> {
        self.beacon_parent_by_id.get(&beacon_id).copied()
    }

    /// Canonical gameplay fleet identity, if this runtime ship belongs to
    /// an authored fleet ship. Used for source-matched RL entity ordering;
    /// never infer it from the runtime ID or dense storage index.
    pub fn ship_fleet_identity(&self, entity_id: i64) -> Option<i64> {
        self.fleet_id_by_ship.get(&entity_id).copied()
    }

    /// Live rendered ships borrow the EXACT same dense physical bodies and
    /// canonical combat state used by headless simulation. No scene copy,
    /// temporary entity list or display-side collision engine is created.
    pub fn render_ships(&self)
        -> impl Iterator<Item = (&CombatShip, &ShipBody)> + '_
    {
        self.ships.iter().zip(self.physics.ships()).filter(
            |(ship, body)| body.active && !ship.vitals.is_dead(),
        )
    }

    pub fn render_projectiles(&self)
        -> impl Iterator<Item = (&CombatProjectile, &ProjectileBody)> + '_
    {
        self.projectiles.iter().zip(self.physics.projectiles()).filter(
            |(_, body)| body.active,
        )
    }

    pub fn render_obstacles(&self)
        -> impl Iterator<Item = (&CombatObstacle, &ObstacleBody)> + '_
    {
        self.obstacles.iter().zip(self.physics.obstacles()).filter(
            |(_, body)| body.active,
        )
    }

    pub fn render_asteroid_pieces(&self)
        -> impl Iterator<Item = &AsteroidPieceState> + '_
    {
        self.pieces.iter().filter(|piece| piece.active)
    }

    pub fn projectiles(&self) -> &[CombatProjectile] { &self.projectiles }
    pub fn reservations(&self) -> &DamageReservations { &self.reservations }
    pub fn obstacles(&self) -> &[CombatObstacle] { &self.obstacles }
    pub fn asteroid_pieces(&self) -> &[AsteroidPieceState] { &self.pieces }

    /// Unity Pathfinder.UpdateDynamicObstacleLayer enumerates live
    /// CollisionAsteroids from Level.State.Obstacles every fixed update.
    /// Deriving this view directly from world ownership avoids retaining
    /// dead asteroid coordinates in a second pathfinder registry. It keeps
    /// zero-health collision asteroids until Kill(false) actually removes
    /// them after the authored 0.35-second delay.
    pub fn dynamic_navigation_asteroids(
        &self,
    ) -> impl Iterator<Item = (i64, &ObstacleBody)> + '_ {
        self.obstacles.iter().zip(self.physics.obstacles()).filter_map(
            |(obstacle, body)| {
                if body.active && obstacle.kind == ObstacleKind::CollisionAsteroid &&
                    obstacle.live() {
                    Some((obstacle.entity_id, body))
                } else {
                    None
                }
            },
        )
    }

    /// Revisions track actual additions to the world-owned static collider
    /// registry. The physics core has no external mutable obstacle access;
    /// animated static obstacles must still refresh each completed tick.
    pub fn static_navigation_revision(&self) -> u64 {
        self.static_navigation_revision
    }

    pub fn has_moving_static_navigation_obstacles(&self) -> bool {
        self.moving_static_navigation_obstacles
    }

    /// Only StaticObstacle participates in Unity's baked static clearance.
    /// MapBorder is excluded: pathfinder grid edges supply the border
    /// clearance cap. Geometry uses the same live ObstacleBody storage as
    /// physics, never a second persistent navigation obstacle list.
    pub fn static_navigation_obstacles(
        &self,
    ) -> impl Iterator<Item = (i64, &ObstacleBody)> + '_ {
        self.obstacles.iter().zip(self.physics.obstacles()).filter_map(
            |(obstacle, body)| {
                if body.active && obstacle.kind == ObstacleKind::StaticObstacle {
                    Some((obstacle.entity_id, body))
                } else {
                    None
                }
            },
        )
    }

    /// A game stage enables authored breakup behavior once, before its
    /// simulation starts. Explicit configuration avoids accidentally using
    /// the RL scene's shard selection for unrelated game modes.
    pub fn configure_asteroid_fragments(
        &mut self,
        config: AsteroidFragmentationConfig,
    ) -> Result<(), CombatWorldError> {
        if !config.settings.valid() ||
            self.fragmentation.is_some() ||
            config.palette.shard_prefabs().iter().any(|&n|
                config.effective_shard_health(n).is_err()) {
            return Err(CombatWorldError::InvalidFragmentationConfiguration);
        }
        self.fragmentation = Some(config);
        Ok(())
    }
    /// Configure the source Stage's actual serialized CollisionAsteroid
    /// prefab palette once. Both the game and training use these same
    /// authentic body shapes; the pool retains each Created speed/profile
    /// until its object is acquired again.
    pub fn configure_full_asteroid_stage(
        &mut self, stage: FullAsteroidStage,
    ) -> Result<(), CombatWorldError> {
        if self.full_asteroid_stage.is_some() || !stage.valid() ||
            stage.prefabs.iter().any(|&id| stage.effective_health(id).is_err()) {
            return Err(CombatWorldError::InvalidFragmentationConfiguration);
        }
        self.full_asteroid_stage = Some(stage);
        Ok(())
    }

    /// Source Stage.Pool.GetCollisionAsteroidFromPool() followed by Setup.
    /// Recycled profiles keep their original prefab and Create-time speed;
    /// newly created profiles sample from the source Stage's OWN palette.
    /// The physical entity ID is always fresh, independent of reused slots.
    pub fn spawn_next_full_collision_asteroid(
        &mut self, random: &mut RandomStream,
    ) -> Result<(i64, FullAsteroidMotion), CombatWorldError> {
        let stage = self.full_asteroid_stage.as_ref()
            .ok_or(CombatWorldError::InvalidFragmentationConfiguration)?;
        // Preflight ID exhaustion before RNG, pool ownership or collider
        // mutation. A failure during setup is returned, not recorded as
        // a fabricated successful asteroid in episode measurements.
        let id = self.next_entity_id;
        id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        let reused = self.pooled_full_asteroid_profiles.last().copied();
        let profile = if let Some(profile) = reused {
            profile
        } else {
            let selected = stage.prefabs[
                random.next_below(stage.prefabs.len() as u64) as usize
            ];
            create_full_asteroid_prototype(
                &stage.catalog, selected, stage.settings, random,
            ).map_err(|_| CombatWorldError::InvalidObstacle)?
        };
        let effective = stage.effective_health(profile.prefab_number)
            .map_err(|_| CombatWorldError::InvalidObstacle)?;
        let motion = sample_full_asteroid_motion(
            stage.settings, profile, random,
        ).map_err(|_| CombatWorldError::InvalidObstacle)?;
        let catalog = Arc::clone(&stage.catalog);
        self.spawn_collision_asteroid(
            &catalog, profile.prefab_number, id, motion.pose,
            motion.velocity, motion.angular_velocity_degrees, effective,
        )?;
        if reused.is_some() {
            self.pooled_full_asteroid_profiles.pop();
            self.full_asteroid_pool_work.reused_profiles += 1;
        } else {
            self.full_asteroid_pool_work.created_profiles += 1;
        }
        self.active_full_asteroid_profiles.insert(id, profile);
        self.full_asteroid_pool_work.total_destination_draws +=
            u64::from(motion.destination_attempts);
        if !motion.destination_accepted {
            self.full_asteroid_pool_work.exhausted_destination_searches += 1;
        }
        Ok((id, motion))
    }

    pub fn full_asteroid_pool_measurements(&self) -> FullAsteroidPoolMeasurements {
        FullAsteroidPoolMeasurements {
            active: self.active_full_asteroid_profiles.len(),
            inactive: self.pooled_full_asteroid_profiles.len(),
            ..self.full_asteroid_pool_work
        }
    }

    pub fn asteroid_border_state(&self, entity_id: i64) -> Option<AsteroidBorderState> {
        self.asteroid_border_states.get(&entity_id).copied()
    }

    /// Includes both dormant slots and live shots. This is measured per
    /// world, so stress runs can distinguish peak concurrency from lifetime
    /// shot count without allocating additional instrumentation buffers.
    pub fn projectile_storage_metrics(&self) -> ProjectileStorageMetrics {
        ProjectileStorageMetrics {
            allocated_slots: self.projectiles.len(),
            active_projectiles: self.total_active_projectiles(),
            slots_reused: self.projectile_slot_reuses,
        }
    }

    pub fn obstacle_storage_metrics(&self) -> ObstacleStorageMetrics {
        ObstacleStorageMetrics {
            allocated_slots: self.obstacles.len(),
            active_obstacles: self.physics.obstacles().iter()
                .filter(|body| body.active).count(),
            slots_reused: self.obstacle_slot_reuses,
        }
    }

    /// The canonical spawn path for game and training. Asset collider
    /// geometry is shared, not reconstructed per world or per spawn.
    pub fn spawn_ship(
        &mut self,
        asset: &ShipAsset,
        entity_id: i64,
        side: i32,
        vitals: ShipVitals,
        motion: ShipMotion,
    ) -> Result<(), CombatWorldError> {
        let body = asset.spawn_physics_body(entity_id, motion)?;
        self.add_ship(CombatShip {
            entity_id, side, ship_type: asset.ship_type, vitals,
        }, body)
    }

    /// Reserve an authored future hull in the same source-ordered stream
    /// that assigns initial entities. Dynamic shots and minions skip it.
    pub fn reserve_future_ship_id(&mut self, id: i64) -> Result<(), CombatWorldError> {
        if id < self.next_entity_id { return Err(CombatWorldError::DuplicateEntity); }
        self.next_entity_id = id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        self.reserved_ship_ids.insert(id);
        Ok(())
    }

    pub fn spawn_reserved_ship(
        &mut self, asset: &ShipAsset, entity_id: i64, side: i32, vitals: ShipVitals, motion: ShipMotion,
    ) -> Result<(), CombatWorldError> {
        if !self.reserved_ship_ids.contains(&entity_id) { return Err(CombatWorldError::UnknownShip); }
        let body = asset.spawn_physics_body(entity_id, motion)?;
        self.add_ship_inner(CombatShip { entity_id, side, ship_type: asset.ship_type, vitals }, body, true)?;
        self.reserved_ship_ids.remove(&entity_id);
        Ok(())
    }

    /// Projectile kind and shape are supplied by the SAME authored prefab
    /// catalog that the rendered client uses for spawning.
    pub fn spawn_projectile(
        &mut self,
        asset: &ProjectileAsset,
        entity_id: i64,
        owner: ShotOwner,
        power: i32,
        motion: ProjectileMotion,
        rotation_degrees: f32,
    ) -> Result<(), CombatWorldError> {
        let metadata = CombatProjectile::new(
            entity_id, asset.projectile_type, owner, power, asset.rocket_max_speed,
            asset.explosion_events.clone(), asset.split_count,
        )?;
        let body = asset.spawn_physics_body(entity_id, motion, rotation_degrees)?;
        self.add_projectile(metadata, body)
    }

    /// Read the installed final route/direct-order endpoint for UI and
    /// diagnostics; a pending search has not installed its new endpoint yet.
    pub fn ship_game_destination(&self, ship_id: i64) -> Option<Vec2> {
        self.game_ship_steering.get(&ship_id)?.final_destination
    }

    /// Normal gameplay ship movement needs the live configuration's
    /// ShipTurningRadius and the ship sprite's half-extents. They are
    /// deliberately not guessed from the collision hull or RL observation.
    pub fn configure_ship_game_movement(
        &mut self,
        ship_id: i64,
        profile: GameMovementProfile,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active || self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let controller = GameShipSteering::new(profile)
            .ok_or(CombatWorldError::InvalidGameMovementProfile)?;
        self.game_ship_steering.insert(ship_id, controller);
        Ok(())
    }

    /// Preflight a game/squad point order without mutating any controller.
    /// A whole squad must be checked before issuing its first ship command,
    /// otherwise a missing asset profile or policy-owned ship could leave
    /// a half-issued formation.
    pub fn validate_ship_game_order(&self, ship_id: i64) -> Result<(), CombatWorldError> {
        self.validate_ship_game_speed(ship_id)?;
        if !self.game_ship_steering.contains_key(&ship_id) {
            return Err(CombatWorldError::MissingGameMovementProfile);
        }
        Ok(())
    }

    /// Unity Ship.SetCurrentSpeed is not a pathfinding command. It is legal
    /// for stationary user-controlled ships without GameShipSteering, even
    /// though issuing a destination to those ships is unavailable.
    pub fn validate_ship_game_speed(&self, ship_id: i64) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active || self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let motion = &self.physics.ships()[index].motion;
        if motion.is_policy_controlled && !motion.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        if !motion.base_speed.is_finite() || motion.base_speed < 0.0 {
            return Err(CombatWorldError::InvalidGameSpeed);
        }
        Ok(())
    }

    /// Unity Ship.SetCurrentSpeed(speed) applies the squad's requested speed
    /// but caps it at the ship's immutable authored Speed. Only the live
    /// game-controlled world may change ship movement speed; the RL policy
    /// and ordinary pathfinding continue to use the same ShipMotion.
    pub fn set_ship_game_speed(
        &mut self,
        ship_id: i64,
        requested_speed: f32,
    ) -> Result<(), CombatWorldError> {
        if !requested_speed.is_finite() || requested_speed < 0.0 {
            return Err(CombatWorldError::InvalidGameSpeed);
        }
        self.validate_ship_game_speed(ship_id)?;
        let index = self.ship_by_id[&ship_id];
        let motion = &mut self.physics.ships_mut()[index].motion;
        motion.current_speed = requested_speed.min(motion.base_speed);
        Ok(())
    }

    /// Unity Ship.Geometry.GetObstacleInPath:
    /// BoxCastNonAlloc(origin, GetSize(), GetDegreesTowardsPoint(destination),
    /// -DirectionToPoint(destination), DistanceToPoint(destination), layer 19).
    /// Excludes dead obstacles and map borders using ShouldAvoidObstacle.
    /// Geometry is queried from the real current physics bodies (never
    /// approximated using pathfinder clearance or an obstacle radius).
    ///
    /// Source Unity converts level-local ship position to world by adding
    /// Level.GetPosition(). The native caller must provide positions and
    /// destination in the world/physics frame used by this CombatWorld.
    pub fn ship_movement_obstacle(
        &self,
        ship_id: i64,
        destination: Vec2,
    ) -> Result<Option<MovementObstacleHit>, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::UnknownShip);
        }
        let profile = self.game_ship_steering.get(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?.profile;
        let origin = self.physics.ships()[index].motion.position;
        let angle = crate::geometry::heading_towards(destination - origin);
        let size = Vec2::new(profile.half_width * 2.0, profile.half_height * 2.0);
        Ok(self.physics.cast_ship_movement_box(
            origin, size, angle, destination,
            |body| self.obstacle_by_id.get(&body.id).is_some_and(|&obstacle_index| {
                let obstacle = &self.obstacles[obstacle_index];
                obstacle.live() && matches!(obstacle.kind,
                    ObstacleKind::StaticObstacle | ObstacleKind::CollisionAsteroid |
                    ObstacleKind::AsteroidPiece)
            }),
        )?)
    }

    /// Unity Ship.MoveToPoint's order-selection boundary. The authored
    /// BoxCast selects the current closest live obstacle; an obstructed
    /// destination uses the shared A* queue, otherwise it moves directly.
    /// Recurring same-destination commands do not restart an active search.
    /// Movement locks retain only the latest complete order, so an unlocked
    /// ship rechecks obstacles rather than blindly using a deferred straight
    /// line (which could cross an asteroid that moved in the meantime).
    pub fn move_ship_to_point(
        &mut self,
        ship_id: i64,
        order: GamePointOrder,
    ) -> Result<GamePointOrderResult, CombatWorldError> {
        if !order.valid() || self.level_geometry.is_some_and(|geometry|
            geometry.map_bounds != order.map_bounds || geometry.has_obstacles != order.has_obstacles) {
            return Err(CombatWorldError::InvalidGameMovementOrder);
        }
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::UnknownShip);
        }
        if !self.game_ship_steering.contains_key(&ship_id) {
            return Err(CombatWorldError::MissingGameMovementProfile);
        }
        let motion = &self.physics.ships()[index].motion;
        if motion.is_policy_controlled && !motion.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        if motion.cannot_change_movement_orders {
            self.pending_game_point_orders.insert(ship_id, order);
            return Ok(GamePointOrderResult::DeferredByMovementLock);
        }
        // Ordinary point orders replace the tracked-replan interval.
        // A tracked caller arms its own interval after dispatch.
        self.game_ship_steering.get_mut(&ship_id)
            .expect("validated steering").clear_tracked_replan();
        self.pending_game_point_orders.remove(&ship_id);
        let destination = order.bounded_destination();
        if !self.physics.ships()[index].motion.mobile {
            return Ok(GamePointOrderResult::Unchanged);
        }
        if self.navigation_requests.is_pathfinding(ship_id) {
            // Unity does not raycast while an active worker owns the
            // request. Changed destinations are deferred until it settles.
            let disposition = self.request_ship_path(
                ship_id, destination, order.ship_clearance,
                order.newly_found_obstacle,
            )?;
            self.latest_game_point_orders.insert(ship_id, order);
            return Ok(GamePointOrderResult::Searching(disposition));
        }

        let origin = self.physics.ships()[index].motion.position;
        let in_bounds = if order.has_obstacles {
            if self.level_geometry.is_some() {
                self.ship_is_in_bounds(ship_id)?
            } else { order.position_in_bounds(origin) }
        } else { false };
        if in_bounds {
            let target = self.physics.ships()[index].motion.target_coordinates;
            if !order.newly_found_obstacle &&
                (destination - target).length_squared() < 1.5 * 1.5 {
                return Ok(GamePointOrderResult::Unchanged);
            }
            if order.newly_found_obstacle ||
                self.ship_movement_obstacle(ship_id, destination)?.is_some() {
                let disposition = self.request_ship_path(
                    ship_id, destination, order.ship_clearance,
                    order.newly_found_obstacle,
                )?;
                self.latest_game_point_orders.insert(ship_id, order);
                return Ok(GamePointOrderResult::Searching(disposition));
            }
        }

        if self.move_ship_direct(ship_id, destination)? {
            self.latest_game_point_orders.insert(ship_id, order);
            Ok(GamePointOrderResult::Direct)
        } else {
            Ok(GamePointOrderResult::Unchanged)
        }
    }

    /// Unity Ship.MoveToTrackedPoint for following another moving entity.
    /// A moving target does not invalidate an active A* worker or a failed
    /// path's two-second retry lease. CircleSquad can poll every 0.25s:
    /// Unity additionally rate-limits significant changes to an active
    /// A* path to one replan per second. Direct movement remains ungated.
    /// Ordinary MoveToPoint can still replace a superseded search.
    pub fn move_ship_to_tracked_point(
        &mut self,
        ship_id: i64,
        order: GamePointOrder,
    ) -> Result<GamePointOrderResult, CombatWorldError> {
        if !order.valid() || self.level_geometry.is_some_and(|geometry|
            geometry.map_bounds != order.map_bounds || geometry.has_obstacles != order.has_obstacles) {
            return Err(CombatWorldError::InvalidGameMovementOrder);
        }
        self.validate_ship_game_order(ship_id)?;
        self.tracked_order_measurements.requests += 1;
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let motion = &self.physics.ships()[index].motion;
        if motion.cannot_change_movement_orders {
            self.tracked_order_measurements.movement_lock_deferrals += 1;
            // Match Unity: movement locks preserve the latest requested point.
            return self.move_ship_to_point(ship_id, order);
        }
        if self.navigation_requests.is_pathfinding(ship_id) {
            self.tracked_order_measurements.active_path_suppressed += 1;
            return Ok(GamePointOrderResult::Unchanged);
        }
        let steering = self.game_ship_steering.get(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?;
        if steering.has_pending_path_retry() {
            self.tracked_order_measurements.failed_path_retry_suppressed += 1;
            return Ok(GamePointOrderResult::Unchanged);
        }
        let destination = order.bounded_destination();
        // Pathfinder.Scale * 2 = 8 world units in Unity. A recurring
        // tracked order ignores lesser endpoint drift, preserving the current
        // useful path or direct target instead of restarting navigation.
        const TRACKED_REPLAN_DISTANCE_SQUARED: f32 = 8.0 * 8.0;
        let endpoint = steering.final_destination
            .unwrap_or(motion.target_coordinates);
        if (steering.is_following_path || motion.has_target_coordinates) &&
            (destination - endpoint).length_squared() <
                TRACKED_REPLAN_DISTANCE_SQUARED {
            self.tracked_order_measurements.small_target_change_suppressed += 1;
            return Ok(GamePointOrderResult::Unchanged);
        }
        if steering.is_following_path && !steering.tracked_replan_ready() {
            self.tracked_order_measurements.one_second_gate_suppressed += 1;
            return Ok(GamePointOrderResult::Unchanged);
        }
        let was_following_path = steering.is_following_path;
        let result = self.move_ship_to_point(ship_id, order)?;
        self.tracked_order_measurements.forwarded_orders += 1;
        if was_following_path {
            self.game_ship_steering.get_mut(&ship_id)
                .expect("validated steering").arm_tracked_replan();
        }
        Ok(result)
    }

    /// Tick source ScaledTimer path retries before the ship's ordinary
    /// fixed-step steering. A scheduled failure retry is never allowed to
    /// restart a still-running or superseded request.
    fn advance_failed_game_paths(&mut self, dt: f32) -> Result<(), CombatWorldError> {
        let mut ready = Vec::new();
        for (&ship_id, steering) in &mut self.game_ship_steering {
            if steering.advance_failed_path_retry(dt) {
                if let Some(&order) = self.latest_game_point_orders.get(&ship_id) {
                    ready.push((ship_id, order));
                }
            }
        }
        ready.sort_unstable_by_key(|(ship_id, _)| *ship_id);
        for (ship_id, mut order) in ready {
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            if self.ships[index].vitals.is_dead() ||
                !self.physics.ships()[index].active ||
                self.navigation_requests.is_pathfinding(ship_id) {
                continue;
            }
            let policy_owned = {
                let motion = &self.physics.ships()[index].motion;
                motion.is_policy_controlled && !motion.user_controlled
            };
            if policy_owned {
                if let Some(steering) = self.game_ship_steering.get_mut(&ship_id) {
                    steering.await_path(&mut self.physics.ships_mut()[index].motion);
                }
                self.latest_game_point_orders.remove(&ship_id);
                self.pending_game_point_orders.remove(&ship_id);
                continue;
            }
            // Retry from the current position, checking current obstacles.
            // Unity's TryToFindPathAgain invokes MoveToPoint without the
            // FoundNearbyAsteroid override.
            order.newly_found_obstacle = false;
            self.move_ship_to_point(ship_id, order)?;
        }
        Ok(())
    }

    fn remove_nearby_asteroid(&mut self, ship_id: i64, asteroid_id: i64) {
        if let std::collections::hash_map::Entry::Occupied(mut nearby) =
            self.nearby_asteroids_by_ship.entry(ship_id)
        {
            nearby.get_mut().retain(|&id| id != asteroid_id);
            if nearby.get().is_empty() {
                nearby.remove();
            }
        }
    }

    /// Ship.NearbyAsteroidDoubleCheck, one shared recurring timer per ship.
    /// An active A* worker must not be invalidated by each one-second sensor
    /// tick. When it completes, the next refresh can reassess the route from
    /// live asteroid positions and the last Level-issued destination.
    fn advance_asteroid_navigation_refresh(
        &mut self, dt: f32,
    ) -> Result<(), CombatWorldError> {
        let nearby = &self.nearby_asteroids_by_ship;
        self.asteroid_navigation_refresh.retain(|ship_id, _| nearby.contains_key(ship_id));
        let mut ready = Vec::new();
        for (&ship_id, timer) in &mut self.asteroid_navigation_refresh {
            if let Some(due) = timer.begin_update(dt) {
                timer.finish_update(due);
                ready.push(ship_id);
            }
        }
        ready.sort_unstable();
        for ship_id in ready {
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            let body = &self.physics.ships()[index];
            if !body.active || !body.motion.mobile ||
                self.ships[index].ship_type == 17 ||
                (body.motion.is_policy_controlled && !body.motion.user_controlled) ||
                self.navigation_requests.is_pathfinding(ship_id) {
                continue;
            }
            let Some(mut order) = self.latest_game_point_orders.get(&ship_id).copied()
                else { continue };
            order.newly_found_obstacle = false;
            self.move_ship_to_point(ship_id, order)?;
        }
        Ok(())
    }

    /// The equivalent of Unity's WaitingTargetCoordinates release after a
    /// movement lock. Resolve at most one (latest) command per unlocked ship.
    /// Sorting by immutable ship ID keeps re-issued orders deterministic.
    fn apply_unlocked_game_point_orders(&mut self) -> Result<(), CombatWorldError> {
        let mut ready = Vec::new();
        for (&ship_id, &order) in &self.pending_game_point_orders {
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            let body = &self.physics.ships()[index];
            if body.active && !body.motion.cannot_change_movement_orders {
                ready.push((ship_id, order));
            }
        }
        ready.sort_unstable_by_key(|(ship_id, _)| *ship_id);
        for (ship_id, order) in ready {
            let &index = self.ship_by_id.get(&ship_id)
                .ok_or(CombatWorldError::UnknownShip)?;
            let motion = &self.physics.ships()[index].motion;
            if motion.is_policy_controlled && !motion.user_controlled {
                self.pending_game_point_orders.remove(&ship_id);
                self.latest_game_point_orders.remove(&ship_id);
                continue;
            }
            self.move_ship_to_point(ship_id, order)?;
        }
        Ok(())
    }

    pub fn move_ship_direct(
        &mut self,
        ship_id: i64,
        destination: Vec2,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active || self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let controller = self.game_ship_steering.get_mut(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?;
        let motion = &mut self.physics.ships_mut()[index].motion;
        let deferred = motion.mobile && motion.cannot_change_movement_orders &&
            !(motion.is_policy_controlled && !motion.user_controlled) &&
            destination.is_finite();
        let accepted = controller.set_direct_destination(motion, destination);
        // A movement lock stores the latest order until it can be applied.
        // The old A* result must not overwrite that deferred order.
        if accepted || deferred {
            self.pending_game_point_orders.remove(&ship_id);
            self.latest_game_point_orders.remove(&ship_id);
            self.navigation_requests.invalidate(ship_id)?;
        }
        Ok(accepted)
    }

    pub fn move_ship_in_direction(
        &mut self,
        ship_id: i64,
        heading_degrees: f32,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active || self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let controller = self.game_ship_steering.get_mut(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?;
        let accepted = controller.set_direction(
            &mut self.physics.ships_mut()[index].motion, heading_degrees,
        );
        if accepted {
            self.pending_game_point_orders.remove(&ship_id);
            self.latest_game_point_orders.remove(&ship_id);
            self.navigation_requests.invalidate(ship_id)?;
        }
        Ok(accepted)
    }

    pub fn stop_ship_game_movement(
        &mut self, ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active || self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let motion = &self.physics.ships()[index].motion;
        if motion.is_policy_controlled && !motion.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        let controller = self.game_ship_steering.get_mut(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?;
        self.navigation_requests.invalidate(ship_id)?;
        self.pending_game_point_orders.remove(&ship_id);
        self.latest_game_point_orders.remove(&ship_id);
        controller.stop(&mut self.physics.ships_mut()[index].motion);
        Ok(())
    }

    /// World-owned source of truth for an active ship's pathfinding job.
    /// The caller decides whether direct movement is obstructed; this does
    /// not infer a substitute Unity GetObstacleInPath raycast.
    pub fn request_ship_path(
        &mut self,
        ship_id: i64,
        destination: Vec2,
        ship_clearance: i32,
        newly_found_obstacle: bool,
    ) -> Result<NavigationRequestDisposition, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::UnknownShip);
        }
        let body = &self.physics.ships()[index].motion;
        if body.is_policy_controlled && !body.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        let mut position = body.position;
        if self.physics.ships()[index].has_latched_target_bounds {
            if let Some(geometry) = self.level_geometry.filter(|geometry| geometry.has_obstacles) {
                // Source MoveToPoint uses ForceBounds(_startPosition) even
                // after a previously entered ship has left the map again.
                position = geometry.force_bounds(position);
            }
        }
        let disposition = self.navigation_requests.request(
            ship_id, position, destination, ship_clearance,
            newly_found_obstacle,
        )?;
        if matches!(disposition,
            NavigationRequestDisposition::Queued |
            NavigationRequestDisposition::UpdatedQueued
        ) {
            if let Some(steering) = self.game_ship_steering.get_mut(&ship_id) {
                steering.await_path(&mut self.physics.ships_mut()[index].motion);
            }
        }
        Ok(disposition)
    }

    /// Explicit StopMoving or command replacement invalidates any queued
    /// or running job. It does not cancel the worker's CPU work: its later
    /// completion will simply be discarded.
    pub fn invalidate_ship_path(
        &mut self, ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        if !self.ship_by_id.contains_key(&ship_id) {
            return Err(CombatWorldError::UnknownShip);
        }
        Ok(self.navigation_requests.invalidate(ship_id)?)
    }

    pub fn ship_is_pathfinding(&self, ship_id: i64) -> bool {
        self.navigation_requests.is_pathfinding(ship_id)
    }

    /// One authoritative arrival predicate for ordinary game orders.
    /// A ship awaiting an A* job has no target coordinates yet, and a
    /// failed request may be in its source-style retry window. Treat
    /// either state as pending movement so mission scripts cannot
    /// prematurely overwrite a reinforcement's real entry order.
    pub fn ship_has_pending_game_movement(&self, ship_id: i64) -> bool {
        self.navigation_requests.is_pathfinding(ship_id) ||
            self.game_ship_steering.get(&ship_id)
                .is_some_and(|steering| steering.has_game_order()) ||
            self.ship_body(ship_id)
                .is_some_and(|body| body.motion.has_target_coordinates)
    }

    pub fn claim_next_path_job(&mut self) -> Option<NavigationRequest> {
        self.navigation_requests.next()
    }

    pub fn has_waiting_path_jobs(&self) -> bool {
        self.navigation_requests.has_waiting_jobs()
    }

    /// A constant-time sampler for the exact source-owned navigation queue.
    pub fn navigation_queue_gauges(&self) -> (usize, usize) {
        self.navigation_requests.queue_gauges()
    }

    /// A failed native worker is not a gameplay path-not-found result.
    pub(crate) fn abandon_path_job(&mut self, job: NavigationRequest) {
        self.navigation_requests.abandon_failed_job(job);
    }

    /// A path job's result is delivered only to the same live ship and
    /// newest request owner. A superseding order is requeued from the
    /// ship's current physics position, rather than a stale snapshot.
    pub fn complete_path_job(
        &mut self,
        job: NavigationRequest,
        path: Option<crate::navigation_search::NavigationPath>,
    ) -> Result<NavigationCompletion, CombatWorldError> {
        let Some(&index) = self.ship_by_id.get(&job.ship_id) else {
            self.navigation_requests.retire_ship(job.ship_id);
            return Ok(NavigationCompletion::Discarded);
        };
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            self.navigation_requests.retire_ship(job.ship_id);
            return Ok(NavigationCompletion::Discarded);
        }
        let motion = &self.physics.ships()[index].motion;
        let policy_owned = motion.is_policy_controlled && !motion.user_controlled;
        let position = motion.position;
        if policy_owned {
            // A mode switch can arrive between two fixed steps. Do not
            // report a path as accepted when the RL controller owns motion.
            self.navigation_requests.invalidate(job.ship_id)?;
            if let Some(steering) = self.game_ship_steering.get_mut(&job.ship_id) {
                if steering.has_game_order() {
                    steering.await_path(&mut self.physics.ships_mut()[index].motion);
                }
            }
            return Ok(NavigationCompletion::Discarded);
        }
        let completion = self.navigation_requests.complete(job, path, position)?;
        if let NavigationCompletion::Accepted { path, .. } = &completion {
            if let Some(steering) = self.game_ship_steering.get_mut(&job.ship_id) {
                let motion = &mut self.physics.ships_mut()[index].motion;
                if !(motion.is_policy_controlled && !motion.user_controlled) &&
                    !path.as_ref().is_some_and(|route| steering.install_path(motion, route)) {
                    // Unity MergePathfindingPaths: empty/failed route stops
                    // motion and schedules a two-second retry. Only a
                    // remembered Level order has enough bounds/clearance
                    // context to safely re-enter MoveToPoint.
                    let may_retry = self.latest_game_point_orders.get(&job.ship_id)
                        .is_some_and(|order| order.bounded_destination() == job.destination);
                    if may_retry {
                        steering.failed_path(motion);
                    } else {
                        steering.stop(motion);
                    }
                }
            }
        }
        Ok(completion)
    }

    /// Synchronous world-local worker entrypoint. A future native scheduler
    /// can dispatch claim_next_path_job on worker threads and deliver the
    /// same tokens via complete_path_job, without copying gameplay rules.
    /// Processing a job here does not by itself issue a new movement order.
    pub fn solve_next_path_job(
        &mut self,
        layers: &NavigationClearance,
        search: &mut NavigationSearch,
    ) -> Result<Option<NavigationCompletion>, CombatWorldError> {
        let Some(job) = self.claim_next_path_job() else { return Ok(None); };
        let path = search.find_level_path(
            layers, job.start, job.destination, job.ship_clearance,
        )?;
        Ok(Some(self.complete_path_job(job, path)?))
    }

    /// The reusable, Level-owned navigation workspace reads actual live
    /// obstacles rather than keeping an independent entity list. Call with
    /// static_changed=true after static obstacles are added, moved or
    /// removed. The Level decides whether collision asteroids are active.
    /// Unity updates dynamic clearance once per fixed frame when requested;
    /// the caller makes this call once at the appropriate frame boundary.
    pub fn refresh_world_navigation(
        &self,
        runtime: &mut NavigationRuntime,
        static_changed: bool,
        collision_asteroids_enabled: bool,
    ) -> Result<(Option<NavigationRasterCounts>, NavigationRasterCounts), CombatWorldError> {
        let static_counts = if static_changed {
            Some(runtime.refresh_world_static(
                self.static_navigation_revision, self.static_navigation_obstacles(),
            )?)
        } else {
            None
        };
        let dynamic_counts = runtime.refresh_dynamic(
            collision_asteroids_enabled,
            self.dynamic_navigation_asteroids(),
        )?;
        Ok((static_counts, dynamic_counts))
    }

    /// Consumes one queued request using the Level's persistent immutable
    /// clearance snapshot and reusable search arrays. The runtime converts
    /// world coordinates to the Map transform's local grid and converts
    /// waypoints back to world before updating shared ship movement.
    /// Caller controls worker count and update timing; no path search
    /// is launched implicitly by PPO decision boundaries.
    pub fn solve_next_world_navigation_job(
        &mut self,
        runtime: &mut NavigationRuntime,
    ) -> Result<Option<NavigationCompletion>, CombatWorldError> {
        let Some(job) = self.claim_next_path_job() else { return Ok(None) };
        let path = match runtime.search(job) {
            Ok(path) => path,
            Err(error) => {
                self.navigation_requests.abandon_failed_job(job);
                return Err(error.into());
            }
        };
        Ok(Some(self.complete_path_job(job, path)?))
    }

    /// Set the two authored Ship.Create values needed by
    /// CollisionAsteroid.ShipCollision. The normal user and training spawn
    /// paths must both provide these; training-adjusted MaxHealth is not a
    /// substitute for OriginalHealth.
    pub fn set_ship_collision_profile(
        &mut self,
        ship_id: i64,
        profile: ShipCollisionProfile,
    ) -> Result<(), CombatWorldError> {
        if !profile.valid() {
            return Err(CombatWorldError::InvalidShipCollisionProfile);
        }
        if !self.ship_by_id.contains_key(&ship_id) {
            return Err(CombatWorldError::UnknownShip);
        }
        self.ship_collision_profiles.insert(ship_id, profile);
        Ok(())
    }

    /// Preserve the signed FleetShip identity independently of pooled
    /// combat wrappers. Required before bomb launch to permit future damage
    /// attribution even if the shooter has since been killed.
    pub fn set_ship_fleet_identity(
        &mut self, ship_id: i64, fleet_ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        // Unity assigns negative IDs to training, campaign and carrier
        // temporaries. Sign expresses persistence, not identity validity.
        if fleet_ship_id == 0 {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        if !self.ship_by_id.contains_key(&ship_id) {
            return Err(CombatWorldError::UnknownShip);
        }
        self.fleet_id_by_ship.insert(ship_id, fleet_ship_id);
        Ok(())
    }

    /// A Striker bomb is attached to a particular runtime ship, never to a
    /// reusable Unity-style pooled wrapper. The world owns both fuse stages
    /// and retains the bomb's slot until delayed damage has resolved.
    pub fn spawn_striker_bomb(
        &mut self,
        entity_id: i64,
        owner: ShotOwner,
        target_id: i64,
        shooter_fleet_id: i64,
        power: i32,
        motion: ProjectileMotion,
    ) -> Result<(), CombatWorldError> {
        if shooter_fleet_id == 0 {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        let target_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[target_index].vitals.is_dead() ||
            !self.physics.ships()[target_index].active ||
            self.ships[target_index].side == owner.shooter_side {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        // A Striker must have a live fleet identity when the bomb is
        // released. Only a later identity mismatch makes damage unattributed.
        if self.fleet_id_by_ship.get(&owner.shooter_id) != Some(&shooter_fleet_id) {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        let asset = self.projectile_assets.by_type(12)
            .ok_or(CombatWorldError::InvalidProjectileType(12))?;
        let body = asset.spawn_physics_body(entity_id, motion, 0.0)?;
        let mut shot = CombatProjectile::new_striker_bomb(
            entity_id, owner, power, target_id, shooter_fleet_id,
        )?;
        let target_pose = &self.physics.ships()[target_index].motion;
        let displacement = body.motion.position - target_pose.position;
        let (sin, cos) = target_pose.rotation_degrees.to_radians().sin_cos();
        let local_offset = crate::geometry::Vec2::new(
            displacement.x * cos + displacement.y * sin,
            -displacement.x * sin + displacement.y * cos,
        );
        if let ImpactBehavior::StrikerBomb(fuse) = &mut shot.behavior {
            fuse.set_attachment_offset(local_offset);
        }
        self.add_projectile(shot, body)
    }

    pub fn add_ship(
        &mut self,
        ship: CombatShip,
        body: ShipBody,
    ) -> Result<(), CombatWorldError> {
        self.add_ship_inner(ship, body, false)
    }

    fn add_ship_inner(&mut self, ship: CombatShip, body: ShipBody, reserved: bool)
        -> Result<(), CombatWorldError>
    {
        if ship.side <= 0 { return Err(CombatWorldError::InvalidSide); }
        // Ship.Setup spawns living active hulls. Dead/despawned wrappers
        // may be represented by ShipVitals but cannot join the live world:
        // otherwise cached live counts, RL caps and side elimination are
        // incorrect from their first frame.
        if ship.vitals.is_dead() || !body.active {
            return Err(CombatWorldError::InvalidLiveShipSpawn);
        }
        if body.id != ship.entity_id || (!reserved && body.id < self.next_entity_id) ||
            self.ship_by_id.contains_key(&ship.entity_id) ||
            self.projectile_by_id.contains_key(&ship.entity_id) ||
            self.obstacle_by_id.contains_key(&ship.entity_id)
        {
            return Err(CombatWorldError::DuplicateEntity);
        }
        let next_id = body.id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        // IDs are validated against every canonical entity registry above.
        // Physics only appends a dense body; scanning historical ship IDs a
        // second time would make initial fleet setup quadratic.
        // Each new entity gets its own lifecycle token, so results from a
        // dead or pooled ship cannot write into a new movement order.
        self.navigation_requests.register_ship(ship.entity_id)?;
        let index = self.physics.insert_prevalidated_ship(body);
        self.next_entity_id = self.next_entity_id.max(next_id);
        debug_assert_eq!(index, self.ships.len());
        self.ship_by_id.insert(ship.entity_id, index);
        *self.live_ships_by_side.entry(ship.side).or_insert(0) += 1;
        if self.physics.ships()[index].motion.mobile {
            *self.live_mobile_ships_by_side.entry(ship.side).or_insert(0) += 1;
        }
        self.live_ship_total += 1;
        self.ships.push(ship);
        Ok(())
    }

    /// An existing ordinary source asteroid arrives with exact live motion
    /// and its pool-owned Create-time speed. Unlike a newly sampled spawn,
    /// loading it consumes NO world RNG and retains its actual profile for
    /// reuse when this asteroid is destroyed.
    pub fn spawn_authored_full_collision_asteroid(
        &mut self,
        entity_id: i64,
        profile: FullAsteroidPrototype,
        pose: Pose,
        velocity: Vec2,
        angular_velocity_degrees: f32,
        effective_max_health: i32,
    ) -> Result<(), CombatWorldError> {
        let stage = self.full_asteroid_stage.as_ref()
            .ok_or(CombatWorldError::InvalidFragmentationConfiguration)?;
        if !stage.prefabs.contains(&profile.prefab_number) ||
            profile.speed < 2 ||
            profile.speed as u32 > 2 + stage.settings.asteroid_max_speed_exclusive
                .saturating_sub(1)
        {
            return Err(CombatWorldError::InvalidObstacle);
        }
        let asset = stage.catalog.by_number(profile.prefab_number)
            .ok_or(CombatWorldError::InvalidObstacle)?;
        let original = i32::from(asset.size_class)
            .checked_mul(stage.catalog.health_increment())
            .ok_or(CombatWorldError::InvalidObstacle)?;
        let expected = stage.effective_health(profile.prefab_number)
            .map_err(|_| CombatWorldError::InvalidObstacle)?
            .unwrap_or(original);
        if effective_max_health != expected {
            return Err(CombatWorldError::InvalidObstacle);
        }
        let catalog = Arc::clone(&stage.catalog);
        self.spawn_collision_asteroid(
            &catalog, profile.prefab_number, entity_id, pose, velocity,
            angular_velocity_degrees, Some(effective_max_health),
        )?;
        self.active_full_asteroid_profiles.insert(entity_id, profile);
        Ok(())
    }

    /// Spawns a concrete authored CollisionAsteroid prefab into the same
    /// physics/combat world as ships and projectiles. Never approximates an
    /// asteroid's hull using a sprite bounding rectangle.
    ///
    /// Training durability follows the caller's established guard; None
    /// restores the normal ConfigData.SizeClass * health increment.
    pub fn spawn_collision_asteroid(
        &mut self,
        catalog: &CollisionAsteroidCatalog,
        prefab_number: u8,
        entity_id: i64,
        pose: Pose,
        linear_velocity: Vec2,
        angular_velocity_degrees: f32,
        effective_max_health: Option<i32>,
    ) -> Result<(), CombatWorldError> {
        let (obstacle, body) = catalog.prepare_spawn(
            prefab_number, entity_id, pose, linear_velocity,
            angular_velocity_degrees, effective_max_health,
        ).map_err(|_| CombatWorldError::InvalidObstacle)?;
        self.add_obstacle(obstacle, body)
    }

    /// Spawn a normal pooled CollisionAsteroid with authentic Stage/Level
    /// edge placement and velocity, rather than requiring callers to pass
    /// arbitrary training-only motion. A Level creates the prototype only
    /// once with create_full_asteroid_prototype and retains it across
    /// pooled acquisition; a shard uses the separate fragment pipeline.
    ///
    /// Scene construction calls this through its World.RandomStream so
    /// independent arenas do not affect each other's placement or training
    /// reproducibility. Destination draw count is returned for inexpensive
    /// per-episode setup diagnostics.
    pub fn spawn_full_collision_asteroid(
        &mut self,
        catalog: &CollisionAsteroidCatalog,
        prototype: FullAsteroidPrototype,
        entity_id: i64,
        settings: FragmentSpawnSettings,
        effective_max_health: Option<i32>,
        random: &mut RandomStream,
    ) -> Result<FullAsteroidMotion, CombatWorldError> {
        if catalog.by_number(prototype.prefab_number).is_none() {
            return Err(CombatWorldError::InvalidObstacle);
        }
        let motion = sample_full_asteroid_motion(settings, prototype, random)
            .map_err(|_error: FullAsteroidSpawnError| CombatWorldError::InvalidObstacle)?;
        self.spawn_collision_asteroid(
            catalog, prototype.prefab_number, entity_id, motion.pose,
            motion.velocity, motion.angular_velocity_degrees,
            effective_max_health,
        )?;
        Ok(motion)
    }

    /// Assign the parent-destruction ID to a newly spawned shard. Both
    /// siblings must have the same family ID to ignore their collisions.
    /// Other asteroids, ships, and shots still collide with shards normally.
    pub fn register_asteroid_shard(
        &mut self,
        shard_id: i64,
        parent_id: i64,
    ) -> Result<(), CombatWorldError> {
        let &index = self.obstacle_by_id.get(&shard_id)
            .ok_or(CombatWorldError::UnknownObstacle)?;
        if parent_id <= 0 ||
            parent_id == shard_id ||
            self.obstacles[index].kind != ObstacleKind::CollisionAsteroid {
            return Err(CombatWorldError::InvalidObstacle);
        }
        self.asteroid_shard_family.insert(shard_id, parent_id);
        Ok(())
    }

    /// Add a static obstacle or map border from its verified prefab shape.
    /// Placement may be supplied by the owning map; None uses the actual
    /// authored prefab transform. The shape is shared, never reconstructed
    /// from a visual bounding rectangle.
    pub fn spawn_box_obstacle(
        &mut self,
        catalog: &BoxObstacleCatalog,
        name: &str,
        entity_id: i64,
        override_pose: Option<Pose>,
    ) -> Result<(), CombatWorldError> {
        let asset = catalog.by_name(name)
            .ok_or(CombatWorldError::InvalidObstacle)?;
        let (obstacle, body) = asset.prepare_spawn(entity_id, override_pose)?;
        self.add_obstacle(obstacle, body)
    }

    /// Live authored instance of a verified primary BoxCollider2D. Unity
    /// may resize/rotate a nested collider after prefab load; copy its real
    /// four corners once, with no alternate collision or pathfinding core.
    pub fn spawn_authored_box_obstacle(
        &mut self, catalog: &BoxObstacleCatalog, prefab_name: &str,
        entity_id: i64, pose: Pose, corners: [Vec2; 4],
    ) -> Result<(), CombatWorldError> {
        let asset = catalog.by_name(prefab_name)
            .ok_or(CombatWorldError::InvalidObstacle)?;
        let (obstacle, body) = asset.prepare_spawn_live_box(
            entity_id, pose, corners,
        )?;
        self.add_obstacle(obstacle, body)
    }

    /// Spawn the standalone authored concave Polygon Obstacle without
    /// treating its proximity sensor as a second damage collider.
    pub fn spawn_polygon_obstacle(
        &mut self,
        catalog: &PolygonObstacleCatalog,
        entity_id: i64,
        override_pose: Option<Pose>,
    ) -> Result<(), CombatWorldError> {
        let (obstacle, body) = catalog.prepare_spawn(entity_id, override_pose)
            .map_err(|_| CombatWorldError::InvalidObstacle)?;
        self.add_obstacle(obstacle, body)
    }

    /// Authored runtime polygon outline from the live primary collider.
    /// Both the renderer and headless simulator reference the same
    /// collision vertices and original prefab type, with no other
    /// obstacle implementation or gameplay-dependent conversion.
    pub fn spawn_authored_polygon_obstacle(
        &mut self, catalog: &PolygonObstacleCatalog,
        entity_id: i64, pose: Pose, vertices: Vec<Vec2>,
    ) -> Result<(), CombatWorldError> {
        let (obstacle, body) = catalog.prepare_spawn_live_polygon(
            entity_id, pose, vertices,
        ).map_err(|_| CombatWorldError::InvalidObstacle)?;
        self.add_obstacle(obstacle, body)
    }

    /// Obstacles use the same world-local geometry and collision matrix
    /// as ships and projectiles. Callers MUST supply the actual obstacle
    /// collider: this path never substitutes a bounding-circle heuristic.
    pub fn add_obstacle(
        &mut self,
        obstacle: CombatObstacle,
        mut body: ObstacleBody,
    ) -> Result<(), CombatWorldError> {
        if body.id != obstacle.entity_id || body.id < self.next_entity_id ||
            self.ship_by_id.contains_key(&body.id) ||
            self.projectile_by_id.contains_key(&body.id) ||
            self.obstacle_by_id.contains_key(&body.id)
        {
            return Err(CombatWorldError::DuplicateEntity);
        }
        // The canonical obstacle registry represents physical participants
        // that are active at spawn. Accepting a disabled collider here would
        // leave a live gameplay obstacle that targeting and mining can see
        // while physics excludes it from all collisions.
        if !body.active || (matches!(obstacle.kind,
            ObstacleKind::CollisionAsteroid | ObstacleKind::MiningAsteroid) &&
            !obstacle.vitals.is_some_and(|v| !v.is_dead && v.health > 0))
        {
            return Err(CombatWorldError::InvalidObstacle);
        }
        let next_id = body.id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        let is_asteroid = obstacle.kind == ObstacleKind::CollisionAsteroid;
        let obstacle_kind = obstacle.kind;
        let moving_static = obstacle_kind == ObstacleKind::StaticObstacle &&
            (body.linear_velocity.length_squared() > 0.0 ||
             body.angular_velocity_degrees != 0.0);
        let entity_id = obstacle.entity_id;
        body.tracks_body_contacts = is_asteroid;
        // MapBorder.OnTriggerEnter2D needs only ship entries. Keep the
        // old asteroid-only obstacle-pair and proximity search cost.
        body.tracks_ship_contacts =
            is_asteroid || obstacle_kind == ObstacleKind::MapBorder;
        if let Some(&index) = self.free_obstacle_slots.last() {
            self.physics.replace_ended_obstacle(index, body)?;
            self.free_obstacle_slots.pop();
            self.obstacle_slot_reuses += 1;
            debug_assert!(!self.obstacle_by_id.contains_key(&self.obstacles[index].entity_id));
            self.obstacle_by_id.insert(obstacle.entity_id, index);
            self.obstacles[index] = obstacle;
        } else {
            let index = self.physics.insert_prevalidated_obstacle(body);
            debug_assert_eq!(index, self.obstacles.len());
            self.obstacle_by_id.insert(obstacle.entity_id, index);
            self.obstacles.push(obstacle);
        }
        self.next_entity_id = next_id;
        if obstacle_kind == ObstacleKind::StaticObstacle {
            self.static_navigation_revision =
                self.static_navigation_revision.wrapping_add(1);
            self.moving_static_navigation_obstacles |= moving_static;
        }
        if is_asteroid {
            self.asteroid_border_states.insert(entity_id, AsteroidBorderState::default());
        }
        if obstacle_kind == ObstacleKind::MiningAsteroid {
            // Entity IDs are monotonic in the shared world, even with
            // recycled dense obstacle slots.
            self.mining_obstacle_ids.push(entity_id);
        }
        Ok(())
    }

    pub fn add_projectile(
        &mut self,
        projectile: CombatProjectile,
        body: ProjectileBody,
    ) -> Result<(), CombatWorldError> {
        // This world only creates active shots. Every ended shot is recorded
        // once in free_projectile_slots and every reused slot is removed
        // there; accepting an initially inactive shot would break that
        // invariant and the O(1) live-projectile count.
        if !body.active {
            return Err(CombatWorldError::InvalidLiveProjectileSpawn);
        }
        if body.id != projectile.entity_id || body.id < self.next_entity_id ||
            self.ship_by_id.contains_key(&projectile.entity_id) ||
            self.projectile_by_id.contains_key(&projectile.entity_id) ||
            self.obstacle_by_id.contains_key(&projectile.entity_id)
        {
            return Err(CombatWorldError::DuplicateEntity);
        }
        let next_id = body.id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        let body_root = body.motion.attribution.root_shot_id;
        let projectile_id = projectile.entity_id;
        // The longest combat run must use memory proportional to maximum
        // concurrent shots, not to every shot ever fired. World IDs remain
        // monotonic even when a dormant physical/metadata slot is recycled.
        if let Some(&index) = self.free_projectile_slots.last() {
            self.physics.replace_ended_projectile(index, body)?;
            self.free_projectile_slots.pop();
            self.projectile_slot_reuses += 1;
            self.projectile_by_id.remove(&self.projectiles[index].entity_id);
            self.projectile_by_id.insert(projectile.entity_id, index);
            self.projectiles[index] = projectile;
        } else {
            let index = self.physics.insert_prevalidated_projectile(body);
            debug_assert_eq!(index, self.projectiles.len());
            self.projectile_by_id.insert(projectile.entity_id, index);
            self.projectiles.push(projectile);
        }
        self.next_entity_id = next_id;
        // Continuations join an existing tracked policy turret root before
        // the parent ends. An ordinary game shot creates no RL root.
        if body_root != projectile_id {
            self.shot_outcomes.continued(body_root);
        }
        Ok(())
    }

    /// Only an actual policy-controlled turret launch enables an RL
    /// projectile root; explosions and other ordinary game shots are not
    /// implicitly rewarded as misses. The projectile is already created.
    fn track_policy_projectile_root(&mut self, projectile_id: i64)
        -> Result<(), CombatWorldError>
    {
        let index = *self.projectile_by_id.get(&projectile_id)
            .ok_or(CombatWorldError::UnknownProjectile)?;
        let body = &self.physics.projectiles()[index];
        if !body.active ||
            body.motion.attribution.root_shot_id != projectile_id {
            return Err(CombatWorldError::UnknownProjectile);
        }
        let owner = self.projectiles[index].owner;
        self.shot_outcomes.launched(
            projectile_id, owner.shooter_id, owner.shooter_side,
        );
        Ok(())
    }

    /// Root metrics are updated at actual launch/continuation/end.
    pub fn projectile_outcome_measurements(&self) -> ProjectileOutcomeMeasurements {
        self.shot_outcomes.measurements()
    }

    /// Source RlProjectileNearMissTelemetry.Travel, called only for real
    /// active policy projectile roots that have neither contacted an enemy
    /// nor already marked a near miss. It shares the physical ship grid
    /// with Barge contact work; there is no per-shot full-ship scan.
    fn update_policy_projectile_near_passes(&mut self, dt: f32) {
        if self.shot_outcomes.measurements().in_flight_roots == 0 {
            return;
        }
        for index in 0..self.projectiles.len() {
            if !self.physics.projectiles()[index].active { continue; }
            let root = self.physics.projectiles()[index]
                .motion.attribution.root_shot_id;
            if !self.shot_outcomes.needs_nearness(root) { continue; }
            let shooter_side = self.projectiles[index].owner.shooter_side;
            if self.physics.projectile_near_candidates(
                index, dt, NEAR_MISS_DISTANCE, &mut self.projectile_near_candidates,
            ) {
                self.shot_outcomes.record_ship_grid_rebuild();
            }
            self.shot_outcomes.record_near_candidates(
                self.projectile_near_candidates.len(),
            );
            for &candidate in &self.projectile_near_candidates {
                let target = &self.ships[candidate];
                if target.side == shooter_side || target.vitals.is_dead() {
                    continue;
                }
                self.shot_outcomes.record_near_hull_check();
                if self.physics.projectile_passed_near_ship(
                    index, candidate, dt, NEAR_MISS_DISTANCE,
                ) {
                    self.shot_outcomes.near_enemy(root);
                    break;
                }
            }
        }
    }

    /// Bind the existing weapon's outgoing-damage reservation exactly once.
    /// Projectile.Setup only captures this handle; it never creates a second
    /// reservation. The final weapon port will call this when firing.
    pub fn reserve_projectile_damage(
        &mut self,
        projectile_id: i64,
        target_id: i64,
    ) -> Result<DamageStatus, CombatWorldError> {
        let shot_index = *self.projectile_by_id.get(&projectile_id)
            .ok_or(CombatWorldError::UnknownProjectile)?;
        let target_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.projectiles()[shot_index].active ||
            !self.physics.ships()[target_index].active {
            return Err(CombatWorldError::Reservation(ReservationError::TargetUnavailable));
        }
        let shot = &self.projectiles[shot_index];
        let health = self.ships[target_index].vitals.health();
        Ok(self.reservations.reserve(
            projectile_id, shot.owner.shooter_side, target_id, health, shot.power,
        )?)
    }

    /// The target-selection command can consult the same reserved-damage
    /// counter as GameState.GetShipDamageStatus without copying the ledger.
    pub fn target_damage_status(&mut self, side: i32, target_id: i64)
        -> Result<DamageStatus, CombatWorldError>
    {
        let ship_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        Ok(self.reservations.status(
            side, target_id, self.ships[ship_index].vitals.health(),
        ))
    }

    /// Source BombingRun.Execute and Charge.Execute both select targets
    /// through their real Bomb weapons. A squad assigns every target before
    /// activation succeeds. If a later target fails, the old HiveMind
    /// command is still active and MUST retain its original reservations.
    /// Snapshot only at this rare command-activation boundary, not on
    /// per-frame physics/weapon updates. Both the shared damage totals and
    /// weapon-owner bindings commit or roll back together.
    pub fn with_atomic_game_bomb_assignments<T, E>(
        &mut self,
        operation: impl FnOnce(&mut Self) -> Result<Option<T>, E>,
    ) -> Result<Option<T>, E> {
        let original_reservations = self.reservations.clone();
        let original_bindings = self.game_bomb_weapon_by_ship.clone();
        let result = operation(self);
        if !matches!(result.as_ref(), Ok(Some(_))) {
            self.reservations = original_reservations;
            self.game_bomb_weapon_by_ship = original_bindings;
        }
        result
    }

    /// Unity Bomb.SetTargetShip reserves its actual weapon's Bomb.Power
    /// immediately. The Level picks the target using the already-exposed
    /// target_damage_status; even the source's over-reserved random
    /// fallback is legal here. The weapon ID is the stable, unique authored
    /// Level weapon identity, not a substitute ship or projectile ID.
    /// A retarget releases the previous weapon lease exactly once.
    pub fn assign_game_bomb_target(
        &mut self, bomber_id: i64, bomb_weapon_id: i64,
        target_id: i64, power: i32,
    ) -> Result<DamageStatus, CombatWorldError> {
        let &bomber_index = self.ship_by_id.get(&bomber_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let &target_index = self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let bomber = &self.ships[bomber_index];
        let target = &self.ships[target_index];
        if bomb_weapon_id <= 0 || power < 0 ||
            !matches!(bomber.ship_type, 0 | 10 | 19 | 22) ||
            bomber.vitals.is_dead() || target.vitals.is_dead() ||
            bomber.side == target.side ||
            !self.physics.ships()[bomber_index].active ||
            !self.physics.ships()[target_index].active {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        // The authored Weapon.Id identifies the owner of its lease.
        // The live weapon Power can change independently and must not
        // be frozen by this bookkeeping. Distinct bombers never reuse
        // one authored Level weapon identity.
        match self.game_bomb_weapon_by_ship.get(&bomber_id) {
            Some(&id) if id != bomb_weapon_id =>
                return Err(CombatWorldError::InvalidBombTarget),
            None if self.game_bomb_weapon_by_ship.values()
                .any(|&id| id == bomb_weapon_id) =>
                return Err(CombatWorldError::InvalidBombTarget),
            _ => {}
        }
        let side = bomber.side;
        let health = target.vitals.health();
        // Bomb.SetTargetShip is a single world-owned reservation update.
        // Validate the new total before retiring the existing weapon lease,
        // so a refused retarget cannot leave the old command unreserved.
        let status = self.reservations.retarget(
            bomb_weapon_id, side, target_id, health, power,
        )?;
        self.game_bomb_weapon_by_ship
            .entry(bomber_id).or_insert(bomb_weapon_id);
        Ok(status)
    }

    /// Unity Bomb.ReleaseTargetReservation. Command cancellation,
    /// retargeting and a consumed self-detonation all release this
    /// weapon-owned lease. An already transferred/released lease is inert.
    pub fn release_game_bomb_target(&mut self, bomb_weapon_id: i64) -> bool {
        self.reservations.release(bomb_weapon_id).is_some()
    }

    /// Unity BombingRun.SetFinalize releases each current squad member's
    /// Bomb weapon reservation. The weapon's stable identity is already
    /// owned by this world, so the Level does not need to copy or retain
    /// weapon IDs merely to finalize or supersede a command.
    pub fn release_game_bomb_target_for_ship(&mut self, bomber_id: i64) -> bool {
        let Some(&weapon_id) = self.game_bomb_weapon_by_ship.get(&bomber_id) else {
            return false;
        };
        self.reservations.release(weapon_id).is_some()
    }

    /// Read the canonical Bomb.TargetShip while the weapon itself owns
    /// its reservation. No second target cache is maintained in gameplay.
    /// A transferred Striker bomb's independent projectile reservation is
    /// intentionally NOT a live Bomb.TargetShip; the source weapon clears
    /// its target at transfer. The ship's separate TargetEnemyShipToFollow
    /// lifecycle belongs to the forthcoming BombingRun command adapter.
    pub fn game_bomb_current_target(&self, bomber_id: i64) -> Option<i64> {
        let weapon_id = *self.game_bomb_weapon_by_ship.get(&bomber_id)?;
        self.reservations.held_by(weapon_id).map(|held| held.target_id)
    }

    /// Unity Weapon.DistanceTo uses Collider2D.ClosestPoint, not distance
    /// to the target ship center. The existing read-only exact-hull query
    /// uses the current pose without evicting the shared spatial grid.
    /// BombingRun target ranking must not force a global grid rebuild for
    /// each candidate it inspects.
    pub fn ship_hull_closest_point(
        &self, target_id: i64, origin: Vec2,
    ) -> Option<Vec2> {
        if !origin.is_finite() { return None; }
        let &index = self.ship_by_id.get(&target_id)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return None;
        }
        Some(self.physics.ships()[index].primary_closest_point_readonly(origin))
    }

    /// Bomb.SetTargetShip reserves at weapon selection time. When its
    /// delivery object is created, transfer that existing lease, not a new
    /// reservation. Cancellation of an unused weapon target calls release
    /// on the original weapon owner instead.
    pub fn transfer_damage_reservation(
        &mut self,
        from_owner_id: i64,
        projectile_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let index = *self.projectile_by_id.get(&projectile_id)
            .ok_or(CombatWorldError::UnknownProjectile)?;
        if !self.physics.projectiles()[index].active {
            return Err(CombatWorldError::UnknownProjectile);
        }
        Ok(self.reservations.transfer(from_owner_id, projectile_id)?)
    }

    /// Return a shot to the dormant state once, release only its OWN
    /// reservation, and notify other gameplay systems in Unity's order.
    /// Rockets transfer their lease to the explosion before calling this.
    fn end_projectile(&mut self, index: usize) {
        // LaserBeam.Kill clears BeamCannon.IsFiringLaserBeam. Centralizing
        // this in the ordinary projectile end path also covers range expiry,
        // shooter death, collider contact, and pooled-slot replacement.
        if matches!(&self.projectiles[index].behavior, ImpactBehavior::Beam { .. }) {
            let shooter_id = self.projectiles[index].owner.shooter_id;
            let id = self.projectiles[index].entity_id;
            if let Some(runtime) = self.ship_weapons.get_mut(&shooter_id) {
                for mount in runtime.turrets.iter_mut().flatten() {
                    if mount.active_beam_projectile_id == Some(id) {
                        mount.active_beam_projectile_id = None;
                    }
                }
            }
        }
        let projectile = &mut self.physics.projectiles_mut()[index];
        if !projectile.active {
            return;
        }
        projectile.active = false;
        let id = projectile.id;
        self.reservations.release(id);
        self.free_projectile_slots.push(index);
        self.events.push(CombatEvent::ProjectileEnded { projectile_id: id });
        let root = projectile.motion.attribution.root_shot_id;
        if let Some(result) = self.shot_outcomes.ended(root) {
            self.events.push(CombatEvent::ProjectileRootCompleted(result));
        }
    }

    /// A rocket's explosion has a distinct native entity identity but the
    /// original root-shot ID, command outcome and shooter/fleet ownership.
    /// Its damage reservation transfers without ever being counted twice.
    fn spawn_rocket_explosion(&mut self, rocket_index: usize)
        -> Result<(), CombatWorldError>
    {
        let rocket = &self.projectiles[rocket_index];
        let parent_id = rocket.entity_id;
        let parent_body = &self.physics.projectiles()[rocket_index];
        let origin = parent_body.motion.position;
        let attribution = parent_body.motion.attribution;
        let owner = rocket.owner;
        let source_turret_slot = rocket.source_turret_slot;
        let power = rocket.power;
        let continuation_id = self.next_entity_id;
        if continuation_id == i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        let asset = self.projectile_assets.by_type(13)
            .ok_or(CombatWorldError::InvalidProjectileType(13))?.clone();
        // RocketExplosion.Setup is untargeted and stationary: its 0 range
        // is valid because RocketExplosion.FixedUpdate skips range expiry.
        let motion = ProjectileMotion::new(
            origin, 0.0, 0.0, 0.0,
            attribution.root_shot_id, attribution.command_outcome_id,
        );
        self.spawn_projectile(&asset, continuation_id, owner, power, motion, 0.0)?;
        self.projectiles[self.projectile_by_id[&continuation_id]].source_turret_slot = source_turret_slot;
        self.reservations.transfer(parent_id, continuation_id)?;
        self.events.push(CombatEvent::ProjectileContinued {
            parent_id, continuation_id,
        });
        Ok(())
    }

    /// SplitterShot.KillSequence spawns BeeSmall ballistic children before
    /// ending the parent. Each new child keeps the parent root-shot/command
    /// attribution and shooter, but has NO target damage reservation.
    /// The hit ship is ignored by children spawned from a ship impact.
    fn spawn_split_children(
        &mut self,
        parent_index: usize,
        contacted_ship_id: Option<i64>,
    ) -> Result<(), CombatWorldError> {
        let count = match &self.projectiles[parent_index].behavior {
            ImpactBehavior::Splitter { split_count } => usize::from(*split_count),
            _ => return Err(CombatWorldError::InvalidProjectileType(
                self.projectiles[parent_index].projectile_type,
            )),
        };
        let parent = &self.physics.projectiles()[parent_index].motion;
        let origin = parent.position;
        let distance = (origin - parent.starting_position).length_squared().sqrt();
        let angle = parent.angle_radians;
        let range = parent.range;
        let attribution = parent.attribution;
        let owner = self.projectiles[parent_index].owner;
        let power = self.projectiles[parent_index].power;
        let parent_id = self.projectiles[parent_index].entity_id;
        let source_turret_slot = self.projectiles[parent_index].source_turret_slot;
        let child_asset = self.projectile_assets.by_type(1)
            .ok_or(CombatWorldError::InvalidProjectileType(1))?.clone();
        let speed = child_asset.shot_speed
            .ok_or(CombatWorldError::InvalidProjectileType(1))?;
        for fragment in split_fragments(
            count, angle, distance, range, power, contacted_ship_id,
        ) {
            let child_id = self.next_entity_id;
            let motion = ProjectileMotion::new(
                origin, speed, fragment.angle_radians, range,
                attribution.root_shot_id, attribution.command_outcome_id,
            );
            self.spawn_projectile(
                &child_asset, child_id, owner, fragment.power, motion,
                -fragment.angle_radians.to_degrees(),
            )?;
            self.projectiles[self.projectile_by_id[&child_id]].source_turret_slot = source_turret_slot;
            if let Some(ignored_ship) = fragment.ignore_ship_id {
                if let Some(&index) = self.projectile_by_id.get(&child_id) {
                    self.projectiles[index].ignored_ships.insert(ignored_ship);
                }
            }
            self.events.push(CombatEvent::ProjectileContinued {
                parent_id, continuation_id: child_id,
            });
        }
        Ok(())
    }

    /// Animation/fuse completion and game cleanup use this same end path.
    /// In particular, a rocket explosion releases the reservation it
    /// inherited, even though its projectile Target reference is null.
    pub fn end_projectile_by_id(&mut self, id: i64)
        -> Result<(), CombatWorldError>
    {
        let index = *self.projectile_by_id.get(&id)
            .ok_or(CombatWorldError::UnknownProjectile)?;
        self.end_projectile(index);
        Ok(())
    }

    /// Rocket.Setup registers a repeating 0.1-second ScaledTimer with an
    /// immediate first callback. Timer advancement is kept separate from
    /// projectile collision and only operates on active rocket bodies.
    fn update_rocket_timers(&mut self, dt: f32) {
        for (index, shot) in self.projectiles.iter_mut().enumerate() {
            if !self.physics.projectiles()[index].active {
                continue;
            }
            let ImpactBehavior::Rocket { acceleration_timer, max_speed } =
                &mut shot.behavior else { continue };
            if let Some(due) = acceleration_timer.begin_update(dt) {
                let body = &mut self.physics.projectiles_mut()[index];
                body.motion.velocity =
                    rocket_accelerated_velocity(body.motion.velocity, *max_speed);
                acceleration_timer.finish_update(due);
            }
        }
    }

    /// Gameplay AnimationClip events operate on the same world-owned
    /// collision shapes used by graphical gameplay and headless training.
    /// All due events are consumed in clip order; callbacks can affect
    /// broadphase contacts during the physics step immediately below.
    fn update_explosion_timelines(&mut self, dt: f32) -> Result<(), CombatWorldError> {
        for index in 0..self.projectiles.len() {
            if !self.physics.projectiles()[index].active ||
                !self.projectiles[index].is_explosion() {
                continue;
            }
            self.projectiles[index].advance_explosion_timeline(dt);
            while let Some(action) = self.projectiles[index].next_explosion_action() {
                match action {
                    ExplosionAction::SetRadius(radius) => {
                        let offset = match self.physics.projectiles()[index].shape() {
                            Some(ShipShape::Circle { offset, .. }) => *offset,
                            _ => return Err(CombatWorldError::Physics(
                                PhysicsError::InvalidCollider,
                            )),
                        };
                        self.physics.projectiles_mut()[index].replace_shape(
                            Some(ShipShape::Circle { offset, radius }),
                        )?;
                    }
                    ExplosionAction::SetHarmless => {
                        self.projectiles[index].set_explosion_harmless(true);
                    }
                    ExplosionAction::Kill => {
                        // This is the same idempotent cleanup used for an
                        // explicit end, including transferred reservations
                        // and pooled physical/metadata storage.
                        self.end_projectile(index);
                        break;
                    }
                }
            }
        }
        Ok(())
    }

    /// Advance script-driven bombs. Their physics bodies have no collider
    /// or velocity; the bomb remains allocated through both callback stages.
    fn update_striker_bombs(&mut self, dt: f32) -> Result<(), CombatWorldError> {
        for index in 0..self.projectiles.len() {
            if !self.physics.projectiles()[index].active { continue; }
            let (target_id, shooter_fleet_id, attachment_offset) = {
                let ImpactBehavior::StrikerBomb(fuse) =
                    &self.projectiles[index].behavior else { continue };
                (fuse.contacted_ship_id(), fuse.shooter_fleet_ship_id(),
                    fuse.local_attachment_offset())
            };
            let target_alive = self.ship_by_id.get(&target_id).copied()
                .is_some_and(|target_index| {
                    self.physics.ships()[target_index].active &&
                    !self.ships[target_index].vitals.is_dead()
                });
            if target_alive {
                let target_index = *self.ship_by_id.get(&target_id)
                    .ok_or(CombatWorldError::UnknownShip)?;
                let target_motion = &self.physics.ships()[target_index].motion;
                self.physics.projectiles_mut()[index].motion.position =
                    Pose {
                        position: target_motion.position,
                        rotation_degrees: target_motion.rotation_degrees,
                    }.transform_point(attachment_offset);
            }
            let action = {
                let ImpactBehavior::StrikerBomb(fuse) =
                    &mut self.projectiles[index].behavior else { continue };
                fuse.advance(dt, target_alive)
            };
            match action {
                BombAction::None => {}
                BombAction::Detonated => {
                    self.events.push(CombatEvent::BombDetonated {
                        projectile_id: self.projectiles[index].entity_id,
                        target_id,
                    });
                }
                BombAction::EndWithoutDamage => self.end_projectile(index),
                BombAction::ApplyDamageAndEnd => {
                    // The fuse is still the original owner until DamageAndKill.
                    // Invalid shooter identity turns this into unattributed
                    // physical damage, not credit to a recycled ship wrapper.
                    let owner = self.projectiles[index].owner;
                    let identity_valid = self.fleet_id_by_ship
                        .get(&owner.shooter_id)
                        .is_some_and(|value| *value == shooter_fleet_id);
                    let power = self.projectiles[index].power;
                    let projectile_id = self.projectiles[index].entity_id;
                    let attribution = self.physics.projectiles()[index].motion.attribution;
                    if target_alive {
                        let target_index = *self.ship_by_id.get(&target_id)
                            .ok_or(CombatWorldError::UnknownShip)?;
                        let target = &mut self.ships[target_index];
                        if let Some(delta) = target.vitals.apply_damage(power, self.shots_harmless)? {
                            self.reservations.target_health_changed(target_id, target.vitals.health());
                            if identity_valid {
                                self.events.push(CombatEvent::Hit(HitEvent::new(
                                    projectile_id, attribution.root_shot_id,
                                    owner.shooter_id, target_id,
                                    owner.shooter_side, target.side,
                                    attribution.command_outcome_id,
                                    crate::combat::DamageSource::Bomb, delta, false,
                                )));
                            } else {
                                self.events.push(CombatEvent::UnattributedDamage { target_id, delta });
                            }
                            if delta.killed {
                                self.retire_dead_ship(
                                    target_index,
                                    if identity_valid { owner.shooter_id } else { 0 },
                                    if identity_valid { attribution.command_outcome_id } else { 0 },
                                )?;
                            }
                        }
                    }
                    self.end_projectile(index);
                }
            }
        }
        Ok(())
    }

    fn resolve_contact(&mut self, contact: TriggerEnter) -> Result<(), CombatWorldError> {
        let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
            return Ok(());
        };
        let Some(&ship_index) = self.ship_by_id.get(&contact.ship_id) else {
            return Ok(());
        };
        if !self.physics.projectiles()[shot_index].active ||
            !self.physics.ships()[ship_index].active {
            return Ok(());
        }

        // Scope mutable shot-state borrow before changing the world or
        // returning the projectile to its pool. This is also the source's
        // ContactTarget-before-LogAttackingDamage event ordering.
        let target = &self.ships[ship_index];
        let shot = &mut self.projectiles[shot_index];
        let owner = shot.owner;
        let shot_type = shot.projectile_type;
        let mut ends_projectile = false;
        let mut accepted_power = None;
        let mut rocket_explodes = false;
        let mut split_on_contact = false;

        match &mut shot.behavior {
            ImpactBehavior::StrikerBomb(_) => {},
            ImpactBehavior::Explosion { contacts: explosion, .. } => {
                if explosion.accept_ship(
                    target.entity_id, owner.shooter_side,
                    owner.shooter_is_fire_barge, target.side,
                    shot_type == 15, target.vitals.is_dead(),
                ) {
                    accepted_power = Some(shot.power);
                }
            }
            ImpactBehavior::Rocket { .. } => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    // Rocket.ShipCollision calls ContactTarget -> AddExplosion
                    // -> Kill, but never deals direct rocket damage.
                    rocket_explodes = true;
                }
            }
            ImpactBehavior::Splitter { .. } => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    // SplitterShot.ContactTarget calls KillSequence(target)
                    // before the original shot's damage is recorded.
                    accepted_power = Some(shot.power);
                    ends_projectile = true;
                    split_on_contact = true;
                }
            }
            ImpactBehavior::Direct => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    accepted_power = Some(shot.power);
                    ends_projectile = true;
                }
            }
            ImpactBehavior::Penetrating(piercing) |
            ImpactBehavior::Beam { piercing, .. } => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    if let Some(outcome) = piercing.contact_ship(
                        target.entity_id, target.vitals.health(),
                    ) {
                        accepted_power = Some(outcome.damage_requested);
                        ends_projectile = outcome.ends_projectile;
                        shot.power = outcome.remaining_power;
                    }
                }
            }
        }

        // Source EnemyContact marks the root before a rocket's early
        // Kill or a direct/split shot's Ended callback can finalize it.
        if (rocket_explodes || accepted_power.is_some()) &&
            owner.shooter_side != self.ships[ship_index].side {
            let root = self.physics.projectiles()[shot_index]
                .motion.attribution.root_shot_id;
            self.shot_outcomes.enemy_contact(root);
        }
        if rocket_explodes {
            self.spawn_rocket_explosion(shot_index)?;
            self.end_projectile(shot_index);
            return Ok(());
        }
        if split_on_contact {
            self.spawn_split_children(shot_index, Some(contact.ship_id))?;
        }
        if ends_projectile {
            self.end_projectile(shot_index);
        }
        if let Some(power) = accepted_power {
            let attribution = self.physics.projectiles()[shot_index].motion.attribution;
            let target = &mut self.ships[ship_index];
            if let Some(delta) = target.vitals.apply_damage(power, self.shots_harmless)? {
                self.reservations.target_health_changed(target.entity_id, target.vitals.health());
                let source = match shot_type {
                    14 if owner.self_detonation => DamageSource::Explosion,
                    14 => DamageSource::DeathExplosion,
                    _ => DamageSource::Gun,
                };
                self.events.push(CombatEvent::Hit(HitEvent::new(
                    contact.projectile_id, attribution.root_shot_id,
                    owner.shooter_id, target.entity_id,
                    owner.shooter_side, target.side,
                    attribution.command_outcome_id,
                    source, delta, false,
                )));
                if delta.killed {
                    self.retire_dead_ship(
                        ship_index, owner.shooter_id, attribution.command_outcome_id,
                    )?;
                }
            }
        }
        Ok(())
    }

    /// Projectile.ContactObstacle is distinct from ship contact. Ordinary
    /// projectiles process at most one obstacle per fixed step; explosions
    /// drain the full queue and damage each asteroid only once.
    fn resolve_obstacle_contact(
        &mut self,
        contact: ObstacleTriggerEnter,
    ) -> Result<(), CombatWorldError> {
        let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
            return Ok(());
        };
        let Some(&obstacle_index) = self.obstacle_by_id.get(&contact.obstacle_id) else {
            return Ok(());
        };
        if !self.physics.projectiles()[shot_index].active ||
            !self.physics.obstacles()[obstacle_index].active {
            return Ok(());
        }
        let kind = self.obstacles[obstacle_index].kind;
        if kind == ObstacleKind::MapObject {
            return self.resolve_map_object_contact(contact, shot_index, obstacle_index);
        }
        let power = self.projectiles[shot_index].power;
        if self.projectiles[shot_index].is_explosion() {
            // RocketExplosion.ContactObstacle tracks each obstacle at most
            // once, ignores map borders, and damages only collision asteroids.
            let accepted = {
                let ImpactBehavior::Explosion { contacts, .. } =
                    &mut self.projectiles[shot_index].behavior else {
                        unreachable!("checked explosion behavior")
                    };
                contacts.accept_obstacle(
                    contact.obstacle_id,
                    !self.obstacles[obstacle_index].live(),
                    kind == ObstacleKind::MapBorder,
                )
            };
            if accepted && kind == ObstacleKind::CollisionAsteroid {
                self.damage_collision_asteroid(contact, obstacle_index, power);
            }
        } else if matches!(
            &self.projectiles[shot_index].behavior,
            ImpactBehavior::Rocket { .. }
        ) {
            // Rocket.ContactObstacle ends the rocket, but only non-border
            // contacts continue into a new damage-dealing explosion.
            if kind != ObstacleKind::MapBorder {
                self.spawn_rocket_explosion(shot_index)?;
            }
            self.end_projectile(shot_index);
        } else {
            // Projectile.ContactObstacle skips already dead obstacles.
            if !self.obstacles[obstacle_index].live() {
                return Ok(());
            }
            match kind {
                ObstacleKind::MapBorder => {}
                ObstacleKind::CollisionAsteroid => {
                    self.damage_collision_asteroid(contact, obstacle_index, power);
                }
                ObstacleKind::StaticObstacle => {
                    self.events.push(CombatEvent::StaticObstacleImpact {
                        projectile_id: contact.projectile_id,
                        obstacle_id: contact.obstacle_id,
                    });
                }
                ObstacleKind::MiningAsteroid | ObstacleKind::AsteroidPiece => {}
                // Routed to the authoritative MapObject handler above.
                ObstacleKind::MapObject => unreachable!("handled MapObject contact"),
            }
            if kind != ObstacleKind::MapBorder &&
                matches!(&self.projectiles[shot_index].behavior, ImpactBehavior::Splitter { .. })
            {
                self.spawn_split_children(shot_index, None)?;
            }
            self.end_projectile(shot_index);
        }
        Ok(())
    }

    /// MapBorder reports entry when an asteroid first touches the border,
    /// but only its subsequent EXIT changes HasEnteredMap or removes it.
    /// These events are processed after the shared physics step and use the
    /// same authored hulls in rendered and headless worlds.
    fn asteroid_at_border(&self, pair: ObstaclePairContact) -> Option<i64> {
        let first = &self.obstacles[*self.obstacle_by_id.get(&pair.first_obstacle_id)?];
        let second = &self.obstacles[*self.obstacle_by_id.get(&pair.second_obstacle_id)?];
        match (first.kind, second.kind) {
            (ObstacleKind::CollisionAsteroid, ObstacleKind::MapBorder) => Some(first.entity_id),
            (ObstacleKind::MapBorder, ObstacleKind::CollisionAsteroid) => Some(second.entity_id),
            _ => None,
        }
    }

    fn process_map_border_contacts(&mut self) {
        for index in 0..self.physics.obstacle_pair_entries().len() {
            let pair = self.physics.obstacle_pair_entries()[index];
            if let Some(asteroid_id) = self.asteroid_at_border(pair) {
                if let Some(state) = self.asteroid_border_states.get_mut(&asteroid_id) {
                    state.has_touched_map_border = true;
                }
            }
        }
        for index in 0..self.physics.obstacle_pair_exits().len() {
            let pair = self.physics.obstacle_pair_exits()[index];
            let Some(asteroid_id) = self.asteroid_at_border(pair) else { continue };
            let Some(state) = self.asteroid_border_states.get_mut(&asteroid_id) else {
                continue;
            };
            if state.has_entered_map {
                let &obstacle_index = self.obstacle_by_id.get(&asteroid_id)
                    .expect("active asteroid has a world registry entry");
                self.retire_collision_asteroid(obstacle_index, false);
                self.events.push(CombatEvent::AsteroidExitedMap {
                    obstacle_id: asteroid_id,
                });
            } else {
                state.has_entered_map = true;
            }
        }
    }

    /// The child ProximityCollider (layer 20 in the authored prefabs)
    /// establishes nearby membership before the primary hull (layer 19)
    /// makes contact. The old Unity callbacks can arrive in either order;
    /// this uses a stable, deterministic order pending live parity checking.
    fn process_asteroid_hull_contacts(&mut self) {
        for index in 0..self.physics.proximity_obstacle_exits().len() {
            let contact = self.physics.proximity_obstacle_exits()[index];
            self.nearby_asteroid_pairs.remove(&(
                contact.source_obstacle_id, contact.other_obstacle_id,
            ));
        }
        for index in 0..self.physics.proximity_obstacle_entries().len() {
            let contact = self.physics.proximity_obstacle_entries()[index];
            let Some(&first) = self.obstacle_by_id.get(&contact.source_obstacle_id) else {
                continue;
            };
            let Some(&second) = self.obstacle_by_id.get(&contact.other_obstacle_id) else {
                continue;
            };
            if self.obstacles[first].kind == ObstacleKind::CollisionAsteroid &&
                self.obstacles[second].kind == ObstacleKind::CollisionAsteroid {
                self.nearby_asteroid_pairs.insert((
                    contact.source_obstacle_id, contact.other_obstacle_id,
                ));
            }
        }

        // Each collision destroys at least one asteroid's health. Later
        // contacts cannot damage that zero-health body a second time.
        for index in 0..self.physics.obstacle_pair_entries().len() {
            let contact = self.physics.obstacle_pair_entries()[index];
            self.resolve_asteroid_hull_contact(contact);
        }
    }

    fn resolve_asteroid_hull_contact(&mut self, contact: ObstaclePairContact) {
        let first_id = contact.first_obstacle_id;
        let second_id = contact.second_obstacle_id;
        let (Some(&first_index), Some(&second_index)) = (
            self.obstacle_by_id.get(&first_id),
            self.obstacle_by_id.get(&second_id),
        ) else { return };
        if first_index == second_index ||
            !self.physics.obstacles()[first_index].active ||
            !self.physics.obstacles()[second_index].active ||
            self.obstacles[first_index].kind != ObstacleKind::CollisionAsteroid ||
            self.obstacles[second_index].kind != ObstacleKind::CollisionAsteroid {
            return;
        }
        // Unity only resolves an asteroid's ObstacleCollision after that
        // asteroid has entered the map, and only against a partner which
        // has already touched a map border. Both orientations are eligible.
        let first_eligible = self.nearby_asteroid_pairs.contains(&(first_id, second_id)) &&
            self.asteroid_border_states.get(&first_id)
                .is_some_and(|state| state.has_entered_map) &&
            self.asteroid_border_states.get(&second_id)
                .is_some_and(|state| state.has_touched_map_border);
        let second_eligible = self.nearby_asteroid_pairs.contains(&(second_id, first_id)) &&
            self.asteroid_border_states.get(&second_id)
                .is_some_and(|state| state.has_entered_map) &&
            self.asteroid_border_states.get(&first_id)
                .is_some_and(|state| state.has_touched_map_border);
        if !first_eligible && !second_eligible {
            return;
        }
        // CollisionAsteroid.ObstacleCollision excludes every member of
        // ShardFamily; the same group ID captures that exclusion without
        // quadratic per-shard HashSets.
        if self.asteroid_shard_family.get(&first_id).is_some() &&
            self.asteroid_shard_family.get(&first_id) ==
                self.asteroid_shard_family.get(&second_id) {
            return;
        }

        let (first, second) = if first_index < second_index {
            let (left, right) = self.obstacles.split_at_mut(second_index);
            (&mut left[first_index], &mut right[0])
        } else {
            let (left, right) = self.obstacles.split_at_mut(first_index);
            (&mut right[0], &mut left[second_index])
        };
        let (Some(first_vitals), Some(second_vitals)) =
            (first.vitals.as_mut(), second.vitals.as_mut()) else { return };
        let Some((first_damage, second_damage)) = resolve_asteroid_pair_damage(
            first_vitals, first.size_class, second_vitals, second.size_class,
        ) else { return };
        self.events.push(CombatEvent::AsteroidsCollided {
            first_obstacle_id: first_id,
            second_obstacle_id: second_id,
            first_damage, second_damage,
        });
        if first_damage.killed {
            self.asteroid_delayed_kills.entry(first_id)
                .or_insert_with(|| ScaledTimer::new(0.35, false, false));
        }
        if second_damage.killed {
            self.asteroid_delayed_kills.entry(second_id)
                .or_insert_with(|| ScaledTimer::new(0.35, false, false));
        }
    }

    /// The root asteroid script receives proximity and primary collider
    /// trigger callbacks separately. Proximity establishes NearbyShips,
    /// then a primary overlap applies CollisionAsteroid.ShipCollision's
    /// shared asteroid and ship damage rules.
    fn process_asteroid_ship_contacts(&mut self) -> Result<(), CombatWorldError> {
        let mut newly_nearby_ships = Vec::new();
        let exiting = self.physics.proximity_ship_exits().len();
        for index in 0..exiting {
            let contact = self.physics.proximity_ship_exits()[index];
            self.remove_nearby_asteroid(contact.ship_id, contact.obstacle_id);
            if !self.nearby_asteroids_by_ship.contains_key(&contact.ship_id) {
                self.asteroid_navigation_refresh.remove(&contact.ship_id);
            }
        }
        for index in 0..self.physics.proximity_ship_entries().len() {
            let contact = self.physics.proximity_ship_entries()[index];
            let (Some(&asteroid_index), Some(&ship_index)) = (
                self.obstacle_by_id.get(&contact.obstacle_id),
                self.ship_by_id.get(&contact.ship_id),
            ) else { continue };
            if self.obstacles[asteroid_index].kind == ObstacleKind::CollisionAsteroid &&
                self.physics.obstacles()[asteroid_index].active &&
                self.physics.ships()[ship_index].active {
                let nearby = self.nearby_asteroids_by_ship.entry(contact.ship_id).or_default();
                if !nearby.contains(&contact.obstacle_id) {
                    nearby.push(contact.obstacle_id);
                    newly_nearby_ships.push(contact.ship_id);
                    self.asteroid_navigation_refresh
                        .entry(contact.ship_id)
                        .or_insert_with(|| ScaledTimer::new(1.0, true, false));
                }
            }
        }
        for index in 0..self.physics.ship_obstacle_entries().len() {
            let contact = self.physics.ship_obstacle_entries()[index];
            let asteroid_id = contact.obstacle_id;
            let ship_id = contact.ship_id;
            if !self.nearby_asteroids_by_ship.get(&ship_id)
                .is_some_and(|nearby| nearby.contains(&asteroid_id))
            {
                continue;
            }
            let (Some(&asteroid_index), Some(&ship_index)) = (
                self.obstacle_by_id.get(&asteroid_id),
                self.ship_by_id.get(&ship_id),
            ) else { continue };
            if self.obstacles[asteroid_index].kind != ObstacleKind::CollisionAsteroid ||
                !self.physics.obstacles()[asteroid_index].active ||
                !self.physics.ships()[ship_index].active ||
                self.ships[ship_index].vitals.is_dead() ||
                self.obstacles[asteroid_index].vitals
                    .map_or(true, |vitals| vitals.is_dead || vitals.health <= 0) {
                continue;
            }
            let profile = *self.ship_collision_profiles.get(&ship_id)
                .ok_or(CombatWorldError::MissingShipCollisionProfile)?;
            let asteroid_size_class = self.obstacles[asteroid_index].size_class;
            let ship_health = self.ships[ship_index].vitals.health();
            let Some(asteroid_vitals) = self.obstacles[asteroid_index].vitals.as_mut() else {
                continue;
            };
            let Some((asteroid_damage, requested_ship_damage)) =
                resolve_asteroid_ship_damage(
                    asteroid_vitals, asteroid_size_class, profile.size_class,
                    profile.original_health, ship_health,
                ) else { continue };
            self.remove_nearby_asteroid(ship_id, asteroid_id);
            self.events.push(CombatEvent::AsteroidHitShip {
                obstacle_id: asteroid_id, ship_id, asteroid_damage,
            });
            // Ship.LogDamage is unattributed and never respects the
            // projectile-only shots_harmless flag.
            if let Some(delta) = self.ships[ship_index].vitals
                .apply_damage(requested_ship_damage, false)? {
                self.reservations.target_health_changed(
                    ship_id, self.ships[ship_index].vitals.health(),
                );
                self.events.push(CombatEvent::UnattributedDamage { target_id: ship_id, delta });
                if delta.killed {
                    self.retire_dead_ship(ship_index, 0, 0)?;
                }
            }
            if asteroid_damage.killed {
                // GotKilledInShipCollision uses the same 0.35s death timer.
                self.asteroid_delayed_kills.entry(asteroid_id)
                    .or_insert_with(|| ScaledTimer::new(0.35, false, false));
            }
        }

        // Unity CollisionAsteroid.ShipCollision invokes
        // Ship.FoundNearbyAsteroid on a new nearby-ship contact.
        // A Queen, immobile ship or policy-controlled ship never reissues
        // path commands. Replan at most once per fixed tick, even when
        // multiple asteroid proximity colliders enter simultaneously.
        newly_nearby_ships.sort_unstable();
        newly_nearby_ships.dedup();
        for ship_id in newly_nearby_ships {
            let Some(&ship_index) = self.ship_by_id.get(&ship_id) else { continue };
            let body = &self.physics.ships()[ship_index];
            if !body.active || !body.motion.mobile ||
                self.ships[ship_index].ship_type == 17 ||
                (body.motion.is_policy_controlled && !body.motion.user_controlled) {
                continue;
            }
            let Some(mut order) = self.latest_game_point_orders.get(&ship_id).copied()
                else { continue };
            order.newly_found_obstacle = true;
            self.move_ship_to_point(ship_id, order)?;
        }
        Ok(())
    }

    /// CollisionAsteroid.GotKilledInCollision schedules DelayKill after 0.35s,
    /// unlike projectile damage which removes the asteroid immediately.
    /// Sort due IDs because HashMap iteration order must never change the
    /// simulation's observable destruction/fragment event order.
    fn advance_asteroid_deaths(&mut self, dt: f32) {
        self.expired_asteroid_ids.clear();
        for (&entity_id, timer) in &mut self.asteroid_delayed_kills {
            if let Some(due) = timer.begin_update(dt) {
                timer.finish_update(due);
                self.expired_asteroid_ids.push(entity_id);
            }
        }
        self.expired_asteroid_ids.sort_unstable();
        for index in 0..self.expired_asteroid_ids.len() {
            let entity_id = self.expired_asteroid_ids[index];
            if let Some(&slot) = self.obstacle_by_id.get(&entity_id) {
                self.retire_collision_asteroid(slot, true);
            }
        }
    }

    /// Unity's CollisionAsteroid.Kill(true) (teardown/map border) removes
    /// the body without spawning children. A normal kill emits the authored
    /// breakup request. Callers must not reuse a retired entity ID.
    pub fn kill_collision_asteroid(
        &mut self,
        entity_id: i64,
        end_kill: bool,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.obstacle_by_id.get(&entity_id)
            .ok_or(CombatWorldError::UnknownObstacle)?;
        if self.obstacles[index].kind != ObstacleKind::CollisionAsteroid {
            return Err(CombatWorldError::InvalidObstacle);
        }
        Ok(self.retire_collision_asteroid(index, !end_kill))
    }

    fn retire_collision_asteroid(&mut self, index: usize, with_breakup: bool) -> bool {
        if self.obstacles[index].kind != ObstacleKind::CollisionAsteroid ||
            !self.physics.obstacles()[index].active {
            return false;
        }
        let obstacle = &mut self.obstacles[index];
        let entity_id = obstacle.entity_id;
        let size_class = obstacle.size_class;
        if let Some(vitals) = &mut obstacle.vitals {
            vitals.is_dead = true;
        }
        self.physics.obstacles_mut()[index].active = false;
        self.discovery.retire(DiscoveryKind::Obstacle, entity_id);
        self.obstacle_by_id.remove(&entity_id);
        self.asteroid_border_states.remove(&entity_id);
        self.asteroid_shard_family.remove(&entity_id);
        // Ordinary pooled full asteroids are separate from the shard pool.
        // Retire exactly once, after the active collision body is disabled;
        // reactivation gives the profile a new world-local runtime ID.
        if let Some(prototype) = self.active_full_asteroid_profiles.remove(&entity_id) {
            self.pooled_full_asteroid_profiles.push(prototype);
        }
        if let Some(prototype) = self.active_shard_prototypes.remove(&entity_id) {
            self.pooled_shard_prototypes.push(prototype);
        }
        self.asteroid_delayed_kills.remove(&entity_id);
        self.nearby_asteroid_pairs.retain(|&(source, target)| {
            source != entity_id && target != entity_id
        });
        self.nearby_asteroids_by_ship.retain(|_, nearby| {
            nearby.retain(|&id| id != entity_id);
            !nearby.is_empty()
        });
        self.free_obstacle_slots.push(index);
        if with_breakup {
            if self.fragmentation.is_some() {
                let body = &self.physics.obstacles()[index];
                self.pending_asteroid_breakups.push(PendingAsteroidBreakup {
                    parent_id: entity_id, size_class,
                    position: body.pose.position,
                    angular_velocity_degrees: body.angular_velocity_degrees,
                });
            }
            self.events.push(CombatEvent::AsteroidDestroyed {
                obstacle_id: entity_id,
                size_class,
                breakup: AsteroidBreakup::for_size_class(size_class),
            });
        }
        true
    }

    fn damage_collision_asteroid(
        &mut self,
        contact: ObstacleTriggerEnter,
        obstacle_index: usize,
        power: i32,
    ) {
        let obstacle = &mut self.obstacles[obstacle_index];
        let Some(vitals) = &mut obstacle.vitals else { return; };
        let Some(damage) = vitals.apply_asteroid_projectile_damage(power) else {
            return;
        };
        self.events.push(CombatEvent::AsteroidDamaged {
            projectile_id: contact.projectile_id,
            obstacle_id: contact.obstacle_id,
            damage,
        });
        if damage.killed {
            // Normal destruction drops the registry entry and frees the
            // physical slot; future fragments need distinct entity IDs.
            self.retire_collision_asteroid(obstacle_index, true);
        }
    }

    /// Pieces are visual/non-colliding ObstacleType.AsteroidPiece entities.
    /// They still have world-local IDs, motion, fade state and pooling.
    fn advance_asteroid_pieces(&mut self, dt: f32) {
        for index in 0..self.pieces.len() {
            if !self.pieces[index].active { continue; }
            self.pieces[index].advance(dt);
            if !self.pieces[index].active {
                let piece = &self.pieces[index];
                self.pooled_piece_prototypes.push(piece.prototype);
                self.free_piece_slots.push(index);
                self.events.push(CombatEvent::AsteroidPieceEnded {
                    piece_id: piece.entity_id,
                });
            }
        }
    }

    /// Real child asteroids join this CombatWorld, using the SAME exact
    /// polygon/health/physics path as ordinary collision asteroids. The
    /// original prefab and speed are selected only on first pool creation;
    /// reactivated shard and debris objects retain that immutable profile.
    /// This runs after physics contacts so newly spawned objects take their
    /// first movement step on the following fixed tick.
    fn spawn_pending_asteroid_fragments(
        &mut self,
        rng: &mut RandomStream,
    ) -> Result<(), CombatWorldError> {
        if self.pending_asteroid_breakups.is_empty() {
            return Ok(());
        }
        let config = self.fragmentation.as_ref()
            .ok_or(CombatWorldError::InvalidFragmentationConfiguration)?;
        let catalog = Arc::clone(&config.catalog);
        let settings = config.settings;

        // Avoid exhausting world IDs partway through a breakup. The same
        // monotonically increasing ID sequence also includes visual pieces.
        let needed = self.pending_asteroid_breakups.iter().try_fold(
            0usize,
            |used, breakup| {
                let counts = AsteroidBreakup::for_size_class(breakup.size_class);
                used.checked_add(usize::from(counts.collision_asteroid_shards) +
                    usize::from(counts.asteroid_pieces))
            },
        ).ok_or(CombatWorldError::EntityIdExhausted)?;
        let id_count = i64::try_from(needed)
            .map_err(|_| CombatWorldError::EntityIdExhausted)?;
        self.next_entity_id.checked_add(id_count)
            .ok_or(CombatWorldError::EntityIdExhausted)?;

        let pending = std::mem::take(&mut self.pending_asteroid_breakups);
        for parent in pending {
            let breakup = AsteroidBreakup::for_size_class(parent.size_class);
            for _ in 0..breakup.collision_asteroid_shards {
                let profile = if let Some(old) = self.pooled_shard_prototypes.pop() {
                    old
                } else {
                    let palette = &self.fragmentation.as_ref()
                        .expect("validated fragmentation config").palette;
                    new_shard_prototype(palette, settings, rng)
                };
                let (pose, velocity, angular_velocity) = spawn_fragment_motion(
                    rng, settings, parent.position,
                    parent.angular_velocity_degrees, profile.speed,
                );
                let shard_id = self.next_entity_id;
                let effective_health = self.fragmentation.as_ref()
                    .expect("validated fragmentation config")
                    .effective_shard_health(profile.prefab_number)?;
                self.spawn_collision_asteroid(
                    &catalog, profile.prefab_number, shard_id,
                    pose, velocity, angular_velocity, effective_health,
                )?;
                self.register_asteroid_shard(shard_id, parent.parent_id)?;
                // Unity's Split BreakAwayAsteroids marks both flags true even
                // when the parent has not yet crossed a map border.
                if let Some(border) = self.asteroid_border_states.get_mut(&shard_id) {
                    border.has_entered_map = true;
                    border.has_touched_map_border = true;
                }
                self.active_shard_prototypes.insert(shard_id, profile);
                self.events.push(CombatEvent::AsteroidShardSpawned {
                    parent_id: parent.parent_id,
                    shard_id,
                    prefab_number: profile.prefab_number,
                });
            }

            for _ in 0..breakup.asteroid_pieces {
                let profile = if let Some(old) = self.pooled_piece_prototypes.pop() {
                    old
                } else {
                    let palette = &self.fragmentation.as_ref()
                        .expect("validated fragmentation config").palette;
                    new_piece_prototype(palette, settings, rng)
                };
                let (pose, velocity, angular_velocity) = spawn_fragment_motion(
                    rng, settings, parent.position,
                    parent.angular_velocity_degrees, profile.speed,
                );
                let piece_id = self.next_entity_id;
                self.next_entity_id = piece_id.checked_add(1)
                    .ok_or(CombatWorldError::EntityIdExhausted)?;
                let piece = AsteroidPieceState::new(
                    piece_id, profile, pose, velocity, angular_velocity,
                );
                if let Some(slot) = self.free_piece_slots.pop() {
                    self.pieces[slot] = piece;
                } else {
                    self.pieces.push(piece);
                }
                self.events.push(CombatEvent::AsteroidPieceSpawned {
                    parent_id: parent.parent_id,
                    piece_id,
                    prefab_number: profile.prefab_number,
                });
            }
        }
        Ok(())
    }

    /// This is a real collision -> health -> event path for direct, piercing
    /// and explosion projectiles. It is NOT a complete game/episode step.
    /// Contacts generated during physics are queued for the following step.
    pub fn step_fixed(
        &mut self,
        dt: f32,
        collision_matrix: &CollisionMatrix,
    ) -> Result<StepCounts, CombatWorldError> {
        self.step_fixed_inner(dt, collision_matrix, None, None, None)
    }

    /// The shared multi-world scheduler owns the deterministic RNG. Fragment
    /// selection and movement consume that world stream, not a process-global
    /// or separate simulation-only random source.
    pub fn step_fixed_with_random(
        &mut self,
        dt: f32,
        collision_matrix: &CollisionMatrix,
        random: &mut RandomStream,
    ) -> Result<StepCounts, CombatWorldError> {
        self.step_fixed_inner(dt, collision_matrix, Some(random), None, None)
    }

    /// One source of truth for policy decisions and the shared fixed-step
    /// combat transaction. Rendered gameplay and headless Level rules
    /// both call this, so late post-terminal inference cannot reactivate
    /// movement, turret fire or specials. The actual decisions processed
    /// are counted for the multi-world telemetry sink.
    pub fn step_policy_combat_with_random(
        &mut self,
        decisions: &[Decision],
        dt: f32,
        collision_matrix: &CollisionMatrix,
        random: &mut RandomStream,
    ) -> Result<StepCounts, CombatWorldError> {
        self.step_combat_with_command_attribution(decisions, dt, collision_matrix, random, &|_| None)
    }

    /// The owning Level resolves an ordinary shot's current command at launch
    /// time. No command copy is stored in weapon state or refreshed per frame.
    pub fn step_combat_with_command_attribution(
        &mut self, decisions: &[Decision], dt: f32, collision_matrix: &CollisionMatrix,
        random: &mut RandomStream, command: &dyn Fn(i64) -> Option<i64>,
    ) -> Result<StepCounts, CombatWorldError> {
        self.step_combat_with_targeting_inner(decisions, dt, collision_matrix, random, command, None)
    }

    pub fn step_combat_with_weapon_targeting(&mut self, decisions: &[Decision], dt: f32,
        collision_matrix: &CollisionMatrix, random: &mut RandomStream,
        command: &dyn Fn(i64) -> Option<i64>, context: &dyn NativeAutomaticWeaponContext,
    ) -> Result<StepCounts, CombatWorldError> {
        self.step_combat_with_targeting_inner(decisions, dt, collision_matrix, random, command, Some(context))
    }

    fn step_combat_with_targeting_inner(&mut self, decisions: &[Decision], dt: f32,
        collision_matrix: &CollisionMatrix, random: &mut RandomStream,
        command: &dyn Fn(i64) -> Option<i64>, context: Option<&dyn NativeAutomaticWeaponContext>,
    ) -> Result<StepCounts, CombatWorldError> {
        if !self.frozen_after_elimination {
            self.apply_policy_decisions(decisions)?;
        }
        let mut counts = self.step_fixed_inner(dt, collision_matrix, Some(random), Some(command), context)?;
        counts.decisions = if self.frozen_after_elimination {
            0
        } else {
            decisions.len() as u64
        };
        Ok(counts)
    }

    fn step_fixed_inner(
        &mut self,
        dt: f32,
        collision_matrix: &CollisionMatrix,
        mut random: Option<&mut RandomStream>,
        command: Option<&dyn Fn(i64) -> Option<i64>>,
        context: Option<&dyn NativeAutomaticWeaponContext>,
    ) -> Result<StepCounts, CombatWorldError> {
        // Reject bad deltas before advancing delayed timers or modifying
        // health. Physics validates dt too, but that is too late here.
        if !dt.is_finite() || dt <= 0.0 {
            return Err(CombatWorldError::Physics(PhysicsError::InvalidStep));
        }
        if self.fragmentation.is_some() && random.is_none() {
            return Err(CombatWorldError::FragmentRandomRequired);
        }
        self.events.clear();
        if self.frozen_after_elimination {
            return Ok(StepCounts {
                active_ships: self.live_ship_total as u32,
                active_projectiles: self.total_active_projectiles() as u32,
                ..StepCounts::default()
            });
        }
        self.advance_combat_activity(dt);
        self.begin_weapon_rank_tick();
        self.apply_pending_game_warps()?;
        self.apply_pending_game_fire_barges()?;
        self.apply_pending_player_scout_beacons()?;
        self.apply_pending_player_barge_charges()?;
        self.advance_policy_capability_cooldowns(dt);
        self.apply_pending_policy_mining()?;
        self.apply_pending_policy_ship_specials(collision_matrix)?;
        self.apply_pending_policy_healing()?;
        self.apply_pending_policy_warp()?;
        self.advance_scout_beacon_cooldowns(dt);
        self.advance_game_warp_gate_animations(dt);
        self.advance_policy_barge_phases(dt)?;
        self.advance_game_barge_phases(dt)?;
        self.advance_asteroid_deaths(dt);
        self.advance_asteroid_pieces(dt);
        // Swap buffers instead of allocating a queue for every frame.
        std::mem::swap(&mut self.processing_contacts, &mut self.pending_contacts);
        self.pending_contacts.clear();
        self.processed_this_tick.clear();
        std::mem::swap(
            &mut self.processing_obstacle_contacts,
            &mut self.pending_obstacle_contacts,
        );
        self.pending_obstacle_contacts.clear();
        self.processed_obstacles_this_tick.clear();

        for index in 0..self.processing_contacts.len() {
            let contact = self.processing_contacts[index];
            let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
                continue;
            };
            if !self.projectiles[shot_index].is_explosion() &&
                !self.processed_this_tick.insert(contact.projectile_id)
            {
                // Unity's ordinary Projectile.FixedUpdate consumes one ship
                // queue item; defer any remaining colliders.
                self.pending_contacts.push(contact);
                continue;
            }
            self.resolve_contact(contact)?;
        }
        self.processing_contacts.clear();

        // A base Projectile.FixedUpdate handles its queued ship contact
        // before its queued obstacle contact, then checks range.
        // Dedicated RocketExplosion.FixedUpdate drains both kinds.
        for index in 0..self.processing_obstacle_contacts.len() {
            let contact = self.processing_obstacle_contacts[index];
            let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
                continue;
            };
            if !self.projectiles[shot_index].is_explosion() &&
                !self.processed_obstacles_this_tick.insert(contact.projectile_id)
            {
                self.pending_obstacle_contacts.push(contact);
                continue;
            }
            self.resolve_obstacle_contact(contact)?;
        }
        self.processing_obstacle_contacts.clear();

        // Base Projectile.FixedUpdate checks strict distance > Range after
        // contact processing, not on a newly fired shot or as a swept ray.
        // Rocket explosions override FixedUpdate and never range-expire here.
        for index in 0..self.projectiles.len() {
            if self.projectiles[index].is_explosion() ||
                matches!(&self.projectiles[index].behavior, ImpactBehavior::StrikerBomb(_)) {
                continue;
            }
            if self.physics.projectiles()[index].active &&
                self.physics.projectiles()[index].motion.has_exceeded_range() {
                self.end_projectile(index);
            }
        }

        self.apply_unlocked_game_point_orders()?;
        self.advance_failed_game_paths(dt)?;
        self.advance_asteroid_navigation_refresh(dt)?;
        for ship in self.physics.ships_mut() {
            if !ship.active { continue; }
            // A switch to neural-policy ownership must drop stale gameplay
            // waypoints before the RL turn-suppression check reads
            // HasTargetCoordinates. Do not stop the ship or clear its
            // policy direction: game and training share this same motion.
            if ship.motion.is_policy_controlled && !ship.motion.user_controlled {
                self.pending_game_point_orders.remove(&ship.id);
                self.latest_game_point_orders.remove(&ship.id);
                self.asteroid_navigation_refresh.remove(&ship.id);
                if self.navigation_requests.is_pathfinding(ship.id) {
                    self.navigation_requests.invalidate(ship.id)?;
                }
                if let Some(steering) = self.game_ship_steering.get_mut(&ship.id) {
                    if steering.has_game_order() {
                        steering.await_path(&mut ship.motion);
                    }
                }
            }
            let policy_step = ship.motion.fixed_step_rl(dt);
            if matches!(policy_step, MotionUpdate::NotPolicyControlled) {
                if let Some(steering) = self.game_ship_steering.get_mut(&ship.id) {
                    steering.fixed_step(&mut ship.motion, dt);
                }
            }
        }
        // Turrets use the same ship pose updated above, and create real
        // projectile bodies before the subsequent shared physics step.
        self.consume_weapon_rank_events()?;
        let automatic_before = self.automatic_weapon_measurements;
        let special_before = self.special_weapon_measurements;
        self.advance_mounted_turrets(dt, command, random.as_deref_mut(), context)?;
        // LaserBeam.ExtendBeam follows the same real shooter/turret and
        // resizes the physical beam collider before the native broadphase.
        self.advance_beam_projectiles()?;
        self.update_rocket_timers(dt);
        self.update_striker_bombs(dt)?;
        self.update_explosion_timelines(dt)?;
        let new_contacts = self.physics.step(dt, collision_matrix)?;
        self.pending_contacts.extend_from_slice(new_contacts);
        let weapon_range_counts = self.refresh_weapon_range_contacts(collision_matrix);
        self.refresh_special_weapon_contacts(collision_matrix);
        self.update_policy_projectile_near_passes(dt);
        // Source MapBorder.OnTriggerEnter2D can end Barge.ChargeForward
        // immediately. Reuse the physics ship/obstacle entry stream before
        // accepting any later enemy-hull contacts in this fixed step.
        self.resolve_game_barge_border_contacts()?;
        self.resolve_policy_barge_contacts(collision_matrix)?;
        self.resolve_game_barge_contacts(collision_matrix)?;
        // Striker.CheckCarrierReload runs on the same Level timer clock.
        // A policy can request another bomb only after a real carrier
        // proximity check has rearmed its authored bomb state.
        self.advance_policy_striker_reloads(dt);
        self.advance_game_striker_reloads(dt);
        self.pending_obstacle_contacts
            .extend_from_slice(self.physics.obstacle_trigger_entries());
        self.process_map_border_contacts();
        self.process_asteroid_ship_contacts()?;
        self.process_asteroid_hull_contacts();
        if let Some(random) = random {
            self.spawn_pending_asteroid_fragments(random)?;
        }

        self.consume_weapon_rank_events()?;
        let physics_counts = self.physics.counts();
        // Ship insertion and retirement already maintain this exact active
        // population. Avoid an additional O(historical hulls) scan on every
        // fixed tick, including long battles with many retired ships.
        Ok(StepCounts {
            active_ships: self.live_ship_total as u32,
            active_projectiles: self.total_active_projectiles() as u32,
            events: self.events.len() as u64,
            automatic_weapon_target_queries: self.automatic_weapon_measurements.target_queries - automatic_before.target_queries,
            automatic_weapon_queue_builds: self.automatic_weapon_measurements.queue_builds - automatic_before.queue_builds,
            automatic_weapon_distance_queries: self.automatic_weapon_measurements.distance_queries - automatic_before.distance_queries,
            special_weapon_grid_rebuilds: self.special_weapon_measurements.grid_rebuilds - special_before.grid_rebuilds,
            special_weapon_candidates: self.special_weapon_measurements.candidates - special_before.candidates,
            special_weapon_trigger_hull_tests: self.special_weapon_measurements.trigger_hull_tests - special_before.trigger_hull_tests,
            special_weapon_trigger_entries: self.special_weapon_measurements.trigger_entries - special_before.trigger_entries,
            special_weapon_trigger_exits: self.special_weapon_measurements.trigger_exits - special_before.trigger_exits,
            special_weapon_body_hull_tests: self.special_weapon_measurements.body_hull_tests - special_before.body_hull_tests,
            special_weapon_body_entries: self.special_weapon_measurements.body_entries - special_before.body_entries,
            automatic_weapon_shots: self.automatic_weapon_measurements.shots - automatic_before.shots,
            integrated_bodies: physics_counts.integrated_bodies,
            broadphase_candidates: physics_counts.broadphase_candidates,
            ship_grid_overflow_bodies: physics_counts.ship_grid_overflow_bodies,
            obstacle_grid_overflow_bodies: physics_counts.obstacle_grid_overflow_bodies,
            ship_grid_rebuilds: physics_counts.ship_grid_rebuilds,
            obstacle_grid_rebuilds: physics_counts.obstacle_grid_rebuilds,
            ship_grid_cells_indexed: physics_counts.ship_grid_cells_indexed,
            obstacle_grid_cells_indexed: physics_counts.obstacle_grid_cells_indexed,
            narrowphase_tests: physics_counts.narrowphase_tests,
            trigger_enters: physics_counts.trigger_enters +
                physics_counts.obstacle_trigger_enters +
                physics_counts.ship_obstacle_enters +
                physics_counts.obstacle_pair_enters +
                physics_counts.proximity_ship_enters +
                physics_counts.proximity_obstacle_enters,
            weapon_range_exits: weapon_range_counts.weapon_range_exits + std::mem::take(&mut self.pending_weapon_range_exits),
            ..weapon_range_counts
        })
    }
}

impl CombatWorld {
    /// Record the actual outcome events in a successfully completed fixed
    /// step. Both the physics-only and full Level drivers use this one
    /// definition; only the latter can append late command callbacks.
    /// No independent damage ledger or per-projectile history is created.
    pub fn count_step_outcomes(events: &[CombatEvent], counts: &mut StepCounts) {
        for event in events {
            match event {
                CombatEvent::Hit(hit) => {
                    let tsv = u64::try_from(hit.delta.tsv_loss)
                        .expect("physical hit TSV loss cannot be negative");
                    if hit.attacker_side == hit.target_side {
                        counts.friendly_hits += 1;
                        counts.friendly_hit_tsv_loss += tsv;
                    } else {
                        counts.enemy_hits += 1;
                        counts.enemy_hit_tsv_loss += tsv;
                    }
                }
                CombatEvent::UnattributedDamage { delta, .. } => {
                    counts.unattributed_damage_events += 1;
                    counts.unattributed_damage_tsv_loss +=
                        u64::try_from(delta.tsv_loss)
                            .expect("physical damage TSV loss cannot be negative");
                }
                CombatEvent::ProjectileRootCompleted(outcome) => {
                    counts.policy_roots_completed += 1;
                    if outcome.enemy_contact {
                        counts.policy_roots_contacted += 1;
                    } else if outcome.near_enemy {
                        counts.policy_roots_near_missed += 1;
                    }
                }
                _ => {}
            }
        }
    }
}

/// Shared native gameplay adapter. Valid policy actions drive real
/// movement and supported authored rotating turrets in this same world;
/// unported weapon subclasses and special abilities remain masked.

pub struct CombatWorldRules {
    pub collision_matrix: CollisionMatrix,
}

impl GameRules<CombatWorld> for CombatWorldRules {
    type Error = CombatWorldError;

    fn step(
        &self,
        world: &mut World<CombatWorld>,
        decisions: &[Decision],
        dt_seconds: f32,
    ) -> Result<StepCounts, Self::Error> {
        let mut counts = world.state.step_policy_combat_with_random(
            decisions, dt_seconds, &self.collision_matrix,
            &mut world.random,
        )?;
        CombatWorld::count_step_outcomes(world.state.events(), &mut counts);
        Ok(counts)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::Vec2;
    use crate::movement::ShipMotion;
    use crate::projectile::ProjectileMotion;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::projectile_assets::ProjectileAssetCatalog;

    #[test]
    fn live_hivemind_geometry_records_only_first_sightings_and_keeps_memory() {
        let projectile_assets = Arc::new(
            ProjectileAssetCatalog::embedded().unwrap(),
        );
        let ship_assets = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-250.0, -100.0),
                max: Vec2::new(250.0, 100.0),
            }, 8.0, projectile_assets,
        ).unwrap();
        for (id, side, x) in [(1, 1, -100.0), (2, 2, 0.0), (3, 1, -10.0)] {
            world.spawn_ship(
                ship_assets.by_type(21).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(Vec2::new(x, 0.0), 0.0, 100.0, 0.0, 4.0),
            ).unwrap();
        }
        // Collider closest-point visibility: this distant center belongs
        // to a wide static obstacle whose left edge is within sight.
        world.add_obstacle(
            CombatObstacle {
                entity_id: 4,
                kind: ObstacleKind::StaticObstacle,
                vitals: None,
                size_class: 0,
            },
            ObstacleBody::new(
                4,
                Pose {
                    position: Vec2::new(40.0, 0.0),
                    rotation_degrees: 0.0,
                },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(45.0, 2.0),
                },
                Vec2::ZERO,
                0.0,
            ).unwrap(),
        ).unwrap();

        let seen = [
            HiveMindObserver {
                ship_id: 1, effective_range: 1,
                registered: true, enabled: true,
            },
            HiveMindObserver {
                ship_id: 3, effective_range: 30,
                registered: true, enabled: true,
            },
        ];
        let skipped = world.refresh_hivemind_vision(&seen, true).unwrap();
        assert_eq!(skipped.first_ship_discoveries, 0);
        assert!(!world.has_live_enemy_visible_to_hivemind(1));
        assert!(world.discovery().ship_ids(1).is_none());

        let first = world.refresh_hivemind_vision(&seen, false).unwrap();
        assert_eq!(first.active_observers, 2);
        assert_eq!(first.first_ship_discoveries, 1);
        assert_eq!(first.first_obstacle_discoveries, 1);
        assert!(world.has_live_enemy_visible_to_hivemind(1));
        assert!(first.visible_enemy_pairs >= 1);
        assert!(first.aabb_rejections >= 1);
        assert!(world.discovery().has(1, DiscoveryKind::Obstacle, 4));
        assert!(world.discovery().has(1, DiscoveryKind::Ship, 2));

        // Leaving range, disabling or retiring the observer never removes
        // the faction's already learned enemy memory.
        let disabled = [HiveMindObserver {
            ship_id: 3, effective_range: 0,
            registered: false, enabled: false,
        }];
        let second = world.refresh_hivemind_vision(&disabled, false).unwrap();
        assert_eq!(second.first_ship_discoveries, 0);
        assert!(!world.has_live_enemy_visible_to_hivemind(1));
        assert!(world.discovery().has(1, DiscoveryKind::Ship, 2));

        let duplicates = [seen[0], seen[0]];
        assert_eq!(
            world.refresh_hivemind_vision(&duplicates, false),
            Err(VisionError::DuplicatedObserver(1)),
        );
    }

    #[test]
    fn striker_bomb_resolves_delayed_damage_and_releases_weapon_lease() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, assets,
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(-8.0, 0.0), 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 2, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.set_ship_fleet_identity(1, 501).unwrap();
        // Target selection already reserved 20 damage under weapon ID 100.
        world.reservations.reserve(100, 1, 2, 100, 20).unwrap();
        world.spawn_striker_bomb(
            10,
            ShotOwner {
                shooter_id: 1, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            2, 501, 20,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 10, 77),
        ).unwrap();
        assert_eq!(world.transfer_damage_reservation(100, 10), Ok(true));
        assert!(world.reservations.held_by(100).is_none());

        let mask = CollisionMatrix::from_rows([0; 32]);
        world.physics.ships_mut()[1].motion.position =
            Vec2::new(2.0, 3.0);
        world.step_fixed(1.51, &mask).unwrap();
        assert_eq!(
            world.physics.projectiles()[0].motion.position,
            Vec2::new(2.0, 3.0),
        );
        assert!(matches!(
            world.events()[0],
            CombatEvent::BombDetonated { projectile_id: 10, target_id: 2 }
        ));
        assert_eq!(world.ships()[1].vitals.health(), 100);
        world.step_fixed(0.51, &mask).unwrap();
        let CombatEvent::Hit(hit) = &world.events()[0] else {
            panic!("expected bomb hit");
        };
        assert_eq!(hit.delta.applied_damage, 20);
        assert_eq!(hit.root_shot_id, 10);
        assert_eq!(hit.command_outcome_id, 77);
        assert_eq!(hit.source, DamageSource::Bomb);
        assert!(matches!(
            world.events()[1],
            CombatEvent::ProjectileEnded { projectile_id: 10 }
        ));
        assert!(world.reservations.held_by(10).is_none());
    }

    #[test]
    fn striker_bomb_shooter_reuse_does_not_reassign_damage_credit() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, assets,
        ).unwrap();
        for (id, side) in [(1, 1), (2, 2)] {
            world.spawn_ship(
                ships.by_type(13).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        world.set_ship_fleet_identity(1, 501).unwrap();
        world.spawn_striker_bomb(
            3,
            ShotOwner {
                shooter_id: 1, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            2, 501, 15,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3, 88),
        ).unwrap();

        // A pooled source wrapper now represents another persistent ship.
        world.set_ship_fleet_identity(1, 999).unwrap();
        let mask = CollisionMatrix::from_rows([0; 32]);
        world.step_fixed(1.51, &mask).unwrap();
        world.step_fixed(0.51, &mask).unwrap();
        assert!(matches!(
            world.events()[0],
            CombatEvent::UnattributedDamage { target_id: 2, .. }
        ));
        assert_eq!(world.ships()[1].vitals.health(), 85);
    }

    #[test]
    fn frigate_animation_disables_damage_then_returns_explosion_to_pool() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, assets.clone(),
        ).unwrap();
        world.spawn_projectile(
            assets.by_type(13).unwrap(), 5,
            ShotOwner {
                shooter_id: 2, shooter_side: 2,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            20, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 5, 77),
            0.0,
        ).unwrap();
        let layers = CollisionMatrix::from_rows([0; 32]);
        world.step_fixed(0.51, &layers).unwrap();
        assert!(world.physics.projectiles()[0].active);
        let ImpactBehavior::Explosion { contacts, .. } = &world.projectiles[0].behavior else {
            panic!("expected explosion");
        };
        assert!(contacts.harmless);

        world.step_fixed(0.3, &layers).unwrap();
        assert!(!world.physics.projectiles()[0].active);
        assert!(matches!(
            world.events()[0], CombatEvent::ProjectileEnded { projectile_id: 5 }
        ));
        assert_eq!(world.free_projectile_slots, [0]);
    }

    #[test]
    fn fire_barge_animation_grows_live_physics_collider_then_ends() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-80.0, -80.0), max: Vec2::new(80.0, 80.0) },
            8.0, assets.clone(),
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 5, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(30.0, 0.0), 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.spawn_projectile(
            assets.by_type(14).unwrap(), 10,
            ShotOwner {
                shooter_id: 1, shooter_side: 1,
                shooter_is_fire_barge: true, self_detonation: false,
            },
            20, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 10, 99),
            0.0,
        ).unwrap();
        world.reserve_projectile_damage(10, 5).unwrap();
        let mut rows = [0u32; 32];
        rows[20] = 1u32 << 12;
        let layers = CollisionMatrix::from_rows(rows);
        world.step_fixed(1.42, &layers).unwrap();
        assert!(matches!(
            world.physics.projectiles()[0].shape(),
            Some(ShipShape::Circle { radius: 34.0, .. })
        ));
        // Contacts generated during physics are consumed on the following
        // game tick; the projectile is still within its damaging interval.
        world.step_fixed(0.02, &layers).unwrap();
        assert_eq!(world.ships()[0].vitals.health(), 80);

        world.step_fixed(0.7, &layers).unwrap();
        let ImpactBehavior::Explosion { contacts, .. } = &world.projectiles[0].behavior else {
            panic!("expected explosion");
        };
        assert!(contacts.harmless);
        assert!(matches!(
            world.physics.projectiles()[0].shape(),
            Some(ShipShape::Circle { radius: 56.0, .. })
        ));
        world.step_fixed(0.8, &layers).unwrap();
        assert!(!world.physics.projectiles()[0].active);
        assert!(world.reservations.held_by(10).is_none());
        assert_eq!(world.target_damage_status(1, 5).unwrap().total_reserved, 0);
    }

    /// Synthetic collision shapes isolate event dispatch; the real obstacle
    /// prefabs must be imported before claiming Unity geometry parity.
    fn obstacle_for_test(id: i64, radius: f32) -> ObstacleBody {
        ObstacleBody::new(
            id,
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 },
            13,
            ShipShape::Circle { offset: Vec2::ZERO, radius },
            Vec2::ZERO, 0.0,
        ).unwrap()
    }

    #[test]
    fn authored_map_border_and_static_wall_spawn_using_shared_asset_geometry() {
        let boxes = BoxObstacleCatalog::embedded().unwrap();
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-320.0, -320.0),
                max: Vec2::new(320.0, 320.0),
            }, 8.0, assets,
        ).unwrap();
        world.spawn_box_obstacle(&boxes, "Top Border", 1, None).unwrap();
        world.spawn_box_obstacle(
            &boxes, "1x1", 2,
            Some(Pose { position: Vec2::new(0.0, 0.0),
                rotation_degrees: 0.0 }),
        ).unwrap();
        assert_eq!(world.obstacles()[0].kind, ObstacleKind::MapBorder);
        assert_eq!(world.obstacles()[1].kind, ObstacleKind::StaticObstacle);
        assert_eq!(world.physics.obstacles()[0].pose.position,
            Vec2::new(0.0, 268.0));
        assert_eq!(world.physics.obstacles()[1].pose.position, Vec2::ZERO);
        assert!(matches!(world.physics.obstacles()[0].shape(),
            ShipShape::Box { .. }));
        assert!(matches!(world.physics.obstacles()[1].shape(),
            ShipShape::Box { .. }));
    }

    #[test]
    fn authored_asteroid_spawn_keeps_exact_collider_and_training_durability() {
        let catalog = CollisionAsteroidCatalog::embedded().unwrap();
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-32.0, -32.0),
                max: Vec2::new(32.0, 32.0),
            }, 4.0, assets,
        ).unwrap();
        world.spawn_collision_asteroid(
            &catalog, 11, 1,
            Pose { position: Vec2::new(3.0, 4.0), rotation_degrees: 45.0 },
            Vec2::ZERO, 0.0, Some(5),
        ).unwrap();
        assert_eq!(world.obstacles()[0].kind, ObstacleKind::CollisionAsteroid);
        assert_eq!(world.obstacles()[0].size_class, 6);
        assert_eq!(world.obstacles()[0].vitals.unwrap().health, 5);
        assert_eq!(world.physics.obstacles()[0].pose.position, Vec2::new(3.0, 4.0));
        assert_eq!(world.physics.obstacles()[0].layer, 19);
        assert!(matches!(
            world.physics.obstacles()[0].shape(),
            ShipShape::Polygon(_)
        ));
    }

    #[test]
    fn direct_projectile_destroys_collision_asteroid_and_emits_lifecycle_events() {
        use crate::obstacle::ObstacleVitals;
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-16.0, -16.0), max: Vec2::new(16.0, 16.0) },
            4.0, assets.clone(),
        ).unwrap();
        world.add_obstacle(CombatObstacle {
            entity_id: 1, kind: ObstacleKind::CollisionAsteroid,
            vitals: Some(ObstacleVitals::new(15, 15).unwrap()),
            size_class: 6,
        }, obstacle_for_test(1, 2.0)).unwrap();
        world.spawn_projectile(
            assets.by_type(1).unwrap(), 2,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            20,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 20.0, 2, 44),
            0.0,
        ).unwrap();
        let mut rows = [0u32; 32];
        rows[10] = 1u32 << 13;
        let matrix = CollisionMatrix::from_rows(rows);
        world.step_fixed(0.02, &matrix).unwrap();
        assert!(world.events().is_empty());
        world.step_fixed(0.02, &matrix).unwrap();
        assert_eq!(world.events().len(), 3);
        assert!(matches!(world.events()[0], CombatEvent::AsteroidDamaged {
            projectile_id: 2,
            obstacle_id: 1,
            damage: AsteroidDamage { killed: true, health_after: -5, .. },
        }));
        assert!(matches!(world.events()[1], CombatEvent::AsteroidDestroyed {
            obstacle_id: 1, size_class: 6,
            breakup: AsteroidBreakup {
                collision_asteroid_shards: 2, asteroid_pieces: 9,
            },
        }));
        assert!(matches!(world.events()[2], CombatEvent::ProjectileEnded {
            projectile_id: 2,
        }));
        assert!(!world.physics.obstacles()[0].active);
    }

    #[test]
    fn rocket_ignores_border_but_continues_into_explosion_on_static_obstacle() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        for (kind, should_explode) in [
            (ObstacleKind::MapBorder, false),
            (ObstacleKind::StaticObstacle, true),
        ] {
            let mut world = CombatWorld::new(
                Aabb { min: Vec2::new(-16.0, -16.0), max: Vec2::new(16.0, 16.0) },
                4.0, assets.clone(),
            ).unwrap();
            world.add_obstacle(CombatObstacle {
                entity_id: 1, kind, vitals: None, size_class: 0,
            }, obstacle_for_test(1, 2.0)).unwrap();
            world.spawn_projectile(
                assets.by_type(5).unwrap(), 2,
                ShotOwner {
                    shooter_id: 4, shooter_side: 1,
                    shooter_is_fire_barge: false, self_detonation: false,
                }, 20,
                ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 20.0, 2, 77),
                0.0,
            ).unwrap();
            let mut rows = [0u32; 32];
            rows[18] = 1u32 << 13;
            let matrix = CollisionMatrix::from_rows(rows);
            world.step_fixed(0.02, &matrix).unwrap();
            world.step_fixed(0.02, &matrix).unwrap();
            assert!(matches!(
                world.events().last(),
                Some(CombatEvent::ProjectileEnded { projectile_id: 2 })
            ));
            assert_eq!(
                world.events().iter().any(|event| matches!(
                    event, CombatEvent::ProjectileContinued { parent_id: 2, .. }
                )),
                should_explode
            );
        }
    }

    #[test]
    fn explosion_obstacle_contacts_do_not_double_damage_asteroids() {
        use crate::obstacle::ObstacleVitals;
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-16.0, -16.0), max: Vec2::new(16.0, 16.0) },
            4.0, assets.clone(),
        ).unwrap();
        world.add_obstacle(CombatObstacle {
            entity_id: 1, kind: ObstacleKind::CollisionAsteroid,
            vitals: Some(ObstacleVitals::new(50, 50).unwrap()), size_class: 4,
        }, obstacle_for_test(1, 2.0)).unwrap();
        world.spawn_projectile(
            assets.by_type(13).unwrap(), 2,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            }, 10,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 2, 77),
            0.0,
        ).unwrap();
        let mut rows = [0u32; 32];
        rows[18] = 1u32 << 13;
        let matrix = CollisionMatrix::from_rows(rows);
        world.step_fixed(0.02, &matrix).unwrap();
        world.step_fixed(0.02, &matrix).unwrap();
        assert_eq!(world.obstacles()[0].vitals.unwrap().health, 40);
        world.physics.projectiles_mut()[0].motion.position = Vec2::new(12.0, 12.0);
        world.step_fixed(0.02, &matrix).unwrap();
        world.physics.projectiles_mut()[0].motion.position = Vec2::ZERO;
        world.step_fixed(0.02, &matrix).unwrap();
        world.step_fixed(0.02, &matrix).unwrap();
        assert_eq!(world.obstacles()[0].vitals.unwrap().health, 40);
    }

    #[test]
    fn rocket_impact_transfers_one_damage_reservation_to_its_explosion() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, assets.clone(),
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 5, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();

        let mut rows = [0u32; 32];
        rows[18] = 1u32 << 12;
        let layers = CollisionMatrix::from_rows(rows);
        world.spawn_projectile(
            assets.by_type(5).unwrap(), 10,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            }, 20,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 40.0, 10, 77),
            0.0,
        ).unwrap();
        assert_eq!(world.reserve_projectile_damage(10, 5).unwrap().total_reserved, 20);

        world.step_fixed(0.02, &layers).unwrap();
        world.step_fixed(0.02, &layers).unwrap();
        assert_eq!(world.ships()[0].vitals.health(), 100);
        assert!(matches!(
            world.events()[0],
            CombatEvent::ProjectileContinued { parent_id: 10, continuation_id: 11 }
        ));
        assert!(matches!(
            world.events()[1], CombatEvent::ProjectileEnded { projectile_id: 10 }
        ));
        assert_eq!(world.reservations().held_by(10), None);
        assert_eq!(world.reservations().held_by(11).unwrap().amount, 20);

        world.step_fixed(0.02, &layers).unwrap();
        let CombatEvent::Hit(hit) = &world.events()[0] else { panic!("expected explosion hit") };
        assert_eq!(hit.projectile_id, 11);
        assert_eq!(hit.root_shot_id, 10);
        assert_eq!(hit.command_outcome_id, 77);
        assert_eq!(world.ships()[0].vitals.health(), 80);
        assert_eq!(world.target_damage_status(1, 5).unwrap().total_reserved, 20);

        // The explosion's visual/animation completion is a separate event.
        world.end_projectile_by_id(11).unwrap();
        assert!(world.reservations().held_by(11).is_none());
        assert_eq!(world.target_damage_status(1, 5).unwrap().total_reserved, 0);
        // Two ended physical slots may be reused for subsequently fired shots.
        let total_slots = world.physics().projectiles().len();
        world.spawn_projectile(
            assets.by_type(1).unwrap(), 12,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            }, 5,
            ProjectileMotion::new(Vec2::new(10.0, 10.0), 0.0, 0.0, 10.0, 12, 88),
            0.0,
        ).unwrap();
        assert_eq!(world.physics().projectiles().len(), total_slots);
        assert_eq!(world.projectile_storage_metrics().slots_reused, 1);
    }

    #[test]
    fn live_collision_debits_tsv_and_keeps_original_command_owner() {
        let ships = ShipAssetCatalog::embedded().unwrap();
        let projectile_assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, projectile_assets.clone(),
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 5, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();

        let mut rows = [0u32; 32];
        rows[10] |= 1u32 << 12;
        let matrix = CollisionMatrix::from_rows(rows);
        let motion = ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 40.0, 9, 77);
        world.spawn_projectile(
            projectile_assets.by_type(1).unwrap(),
            9,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            20, motion, 0.0,
        ).unwrap();

        // Physics dispatch first, then Unity-style queued impact resolution.
        assert!(world.step_fixed(0.02, &matrix).unwrap().events == 0);
        world.step_fixed(0.02, &matrix).unwrap();
        let events = world.events();
        assert_eq!(events.len(), 2);
        assert!(matches!(events[0], CombatEvent::ProjectileEnded { projectile_id: 9 }));
        let CombatEvent::Hit(hit) = &events[1] else { panic!("expected hit") };
        assert_eq!(hit.delta.applied_damage, 20);
        assert_eq!(hit.delta.tsv_loss, 100);
        assert_eq!(hit.command_outcome_id, 77);
        assert_eq!(hit.root_shot_id, 9);
        assert_eq!(world.ships()[0].vitals.health(), 80);
    }
}
