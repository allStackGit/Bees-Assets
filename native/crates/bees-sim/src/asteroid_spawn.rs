//! Source-owned initial CollisionAsteroid.Create / Setup / SetMoving motion.
//!
//! Normal asteroids spawn beyond the map edge and fly toward a random point
//! inside the map. Fragment child asteroids are a separate, already ported
//! pool lifecycle: their location and angular velocity are inherited.
//! This module samples only scene-authored motion; collider/health/contact
//! rules remain in the canonical CombatWorld and asset catalogs.
//!
//! Each spawned ordinary asteroid retains its prefab and Create-time Speed
//! while pooled by the owning Stage. Re-running Setup resamples position,
//! heading, velocity and spin, but does NOT redraw Create-time Speed.

use crate::asteroid_assets::CollisionAsteroidCatalog;
use std::sync::Arc;
use crate::asteroid_fragments::{
    FragmentSpawnSettings, random_map_destination, random_unit_float,
};
use crate::collision::Pose;
use crate::geometry::Vec2;
use crate::simulation::RandomStream;

pub const MINIMUM_ASTEROID_SPAWN_DISTANCE: f32 = 100.0;
pub const MINIMUM_ASTEROID_SPEED: i32 = 2;
pub const MINIMUM_ASTEROID_ANGULAR_SPEED_MULTIPLIER: f32 = 5.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FullAsteroidSpawnError {
    InvalidStageOrMap,
    InvalidPrefabPalette,
    MissingAuthoredPrefab(u8),
    InvalidPrototype,
}

/// Pooled prefab identity and Create-time randomized Speed. The owning Level
/// selects an actual prefab from its Stage.Pool; we do not invent a new
/// distribution of asteroid types.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FullAsteroidPrototype {
    pub prefab_number: u8,
    pub speed: i32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FullAsteroidMotion {
    pub pose: Pose,
    pub velocity: Vec2,
    pub angular_velocity_degrees: f32,
    /// Number of candidate interior positions drawn by RandomCoordinate.
    /// Normally one; 35 indicates the source's placement retry budget ended.
    pub destination_attempts: u8,
}

/// One immutable Stage selection and map configuration, shared by all
/// ordinary full asteroid spawns within this Level. The prefab numbers
/// are imported from Stage.Prefabs.CollisionAsteroidPrefabs, not presumed
/// to be all 28 available authored assets.
#[derive(Clone, Debug)]
pub struct FullAsteroidStage {
    pub catalog: Arc<CollisionAsteroidCatalog>,
    pub prefabs: Vec<u8>,
    pub settings: FragmentSpawnSettings,
}

impl FullAsteroidStage {
    pub fn valid(&self) -> bool {
        valid_settings(self.settings) && !self.prefabs.is_empty() &&
            self.prefabs.iter().all(|&id| self.catalog.by_number(id).is_some())
    }

    /// The same original/effective health split as native shard spawning.
    pub fn effective_health(
        &self, prefab_number: u8,
    ) -> Result<Option<i32>, FullAsteroidSpawnError> {
        let Some(asset) = self.catalog.by_number(prefab_number) else {
            return Err(FullAsteroidSpawnError::MissingAuthoredPrefab(prefab_number));
        };
        let Some(ratio) = self.settings.training_health_ratio else {
            return Ok(None);
        };
        let original = i32::from(asset.size_class)
            .checked_mul(self.catalog.health_increment())
            .ok_or(FullAsteroidSpawnError::InvalidStageOrMap)?;
        let adjusted = (original as f32 * ratio).ceil();
        if !adjusted.is_finite() || adjusted > i32::MAX as f32 {
            return Err(FullAsteroidSpawnError::InvalidStageOrMap);
        }
        Ok(Some(adjusted.max(1.0) as i32))
    }
}

fn valid_settings(settings: FragmentSpawnSettings) -> bool {
    // Unity Level.HalfMapWidth/Height are positive INTEGERS derived from
    // SpriteRenderer bounds; do not turn a fractional training map into a
    // guessed integer spawn range.
    let dimensions = settings.half_map_extents;
    let is_integer = |x: f32| x.is_finite() && x >= 1.0 &&
        x <= i32::MAX as f32 && x.fract() == 0.0;
    settings.valid() && is_integer(dimensions.x) &&
        is_integer(dimensions.y) &&
        // Both Unity RandomInt(max) ranges and external spawn offsets must
        // remain finite and exactly representable in the native f32 world.
        dimensions.x + MINIMUM_ASTEROID_SPAWN_DISTANCE < i32::MAX as f32 &&
        dimensions.y + MINIMUM_ASTEROID_SPAWN_DISTANCE < i32::MAX as f32
}

/// Source CollisionAsteroid.Create, called only for newly allocated pool
/// objects. A reused object retains the original speed, just like Unity.
pub fn create_full_asteroid_prototype(
    catalog: &CollisionAsteroidCatalog,
    prefab_number: u8,
    settings: FragmentSpawnSettings,
    rng: &mut RandomStream,
) -> Result<FullAsteroidPrototype, FullAsteroidSpawnError> {
    if !valid_settings(settings) {
        return Err(FullAsteroidSpawnError::InvalidStageOrMap);
    }
    if catalog.by_number(prefab_number).is_none() {
        return Err(FullAsteroidSpawnError::MissingAuthoredPrefab(prefab_number));
    }
    let speed = MINIMUM_ASTEROID_SPEED +
        if settings.asteroid_max_speed_exclusive == 0 { 0 } else {
            rng.next_below(u64::from(settings.asteroid_max_speed_exclusive)) as i32
        };
    Ok(FullAsteroidPrototype { prefab_number, speed })
}

/// Source CollisionAsteroid.Setup then SetMoving, on every pool acquisition.
/// Spawn edge choice and sign/RandomInt order preserve the actual C# call
/// order, including the initially sampled Y edge before CoinToss may replace
/// it with an X-edge spawn. The native RandomStream is reproducible but is
/// not numerically identical to System.Random.
pub fn sample_full_asteroid_motion(
    settings: FragmentSpawnSettings,
    prototype: FullAsteroidPrototype,
    rng: &mut RandomStream,
) -> Result<FullAsteroidMotion, FullAsteroidSpawnError> {
    if !valid_settings(settings) {
        return Err(FullAsteroidSpawnError::InvalidStageOrMap);
    }
    let max_speed = MINIMUM_ASTEROID_SPEED as u32 +
        settings.asteroid_max_speed_exclusive;
    if prototype.speed < MINIMUM_ASTEROID_SPEED ||
        prototype.speed as u32 > max_speed ||
        prototype.prefab_number == 0 {
        return Err(FullAsteroidSpawnError::InvalidPrototype);
    }

    let half_x = settings.half_map_extents.x as u64;
    let half_y = settings.half_map_extents.y as u64;
    let sign = |rng: &mut RandomStream| -> f32 {
        if rng.next_below(2) == 0 { -1.0 } else { 1.0 }
    };
    // Unity constructs the first (X edge, arbitrary Y) candidate:
    // RandomSign(), then RandomSign(), then RandomInt(HalfMapHeight).
    let x = sign(rng) *
        (settings.half_map_extents.x + MINIMUM_ASTEROID_SPAWN_DISTANCE);
    let y = sign(rng) * rng.next_below(half_y) as f32;
    let mut position = Vec2::new(x, y);
    // Unity CoinToss can replace the first candidate with a Y-edge spawn.
    if rng.next_below(2) == 0 {
        position = Vec2::new(
            sign(rng) * rng.next_below(half_x) as f32,
            sign(rng) *
                (settings.half_map_extents.y + MINIMUM_ASTEROID_SPAWN_DISTANCE),
        );
    }
    let rotation_degrees = rng.next_below(360) as f32;
    let (destination, destination_attempts) = random_map_destination(
        rng, settings.map_bounds, settings.half_map_extents,
    );
    // Unity: Body.linearVelocity = Speed * -DirectionBetweenPoints(position,
    // destination) => velocity TOWARD destination. Spin is nonnegative.
    let velocity = (destination - position).normalized_or_zero() *
        prototype.speed as f32;
    let angular_velocity_degrees = prototype.speed as f32 *
        random_unit_float(rng) * MINIMUM_ASTEROID_ANGULAR_SPEED_MULTIPLIER;
    Ok(FullAsteroidMotion {
        pose: Pose { position, rotation_degrees },
        velocity, angular_velocity_degrees, destination_attempts,
    })
}
