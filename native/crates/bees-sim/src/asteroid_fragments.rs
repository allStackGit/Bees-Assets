//! Source-grounded collision-asteroid fragment palettes and movement.
//!
//! The scene selects shard and debris prefabs by serialized GUID. The
//! importer resolves those GUIDs into the immutable IDs below. This module
//! is shared by the rendered game and headless simulation, never a visual-only
//! or training-only substitute for the actual collision-asteroid shards.
use serde::Deserialize;

use crate::collision::{Aabb, Pose};
use crate::geometry::Vec2;
use crate::simulation::RandomStream;
use crate::timer::ScaledTimer;

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"), "/../../assets/asteroid-fragments.json"
));

#[derive(Clone, Debug)]
pub struct AsteroidFragmentPalette {
    shards: Vec<u8>,
    pieces: Vec<u8>,
}

#[derive(Debug)]
pub enum FragmentError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidPalette,
}

#[derive(Deserialize)]
struct RawPalette {
    schema_version: u32,
    shard_prefab_numbers: Vec<u8>,
    piece_prefab_numbers: Vec<u8>,
}

impl AsteroidFragmentPalette {
    pub fn embedded() -> Result<Self, FragmentError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(text: &str) -> Result<Self, FragmentError> {
        let raw: RawPalette = serde_json::from_str(text)
            .map_err(|error| FragmentError::Malformed(error.to_string()))?;
        if raw.schema_version != 1 {
            return Err(FragmentError::InvalidSchema(raw.schema_version));
        }
        let distinct = |numbers: &[u8], max: u8| {
            !numbers.is_empty() &&
                numbers.iter().all(|&number| number > 0 && number <= max) &&
                {
                    let mut sorted = numbers.to_vec();
                    sorted.sort_unstable();
                    sorted.dedup();
                    sorted.len() == numbers.len()
                }
        };
        if !distinct(&raw.shard_prefab_numbers, 28) ||
            !distinct(&raw.piece_prefab_numbers, 18) {
            return Err(FragmentError::InvalidPalette);
        }
        Ok(Self {
            shards: raw.shard_prefab_numbers,
            pieces: raw.piece_prefab_numbers,
        })
    }

    pub fn shard_prefabs(&self) -> &[u8] { &self.shards }
    pub fn piece_prefabs(&self) -> &[u8] { &self.pieces }

    pub fn choose_shard(&self, rng: &mut RandomStream) -> u8 {
        self.shards[rng.next_below(self.shards.len() as u64) as usize]
    }

    pub fn choose_piece(&self, rng: &mut RandomStream) -> u8 {
        self.pieces[rng.next_below(self.pieces.len() as u64) as usize]
    }
}

/// Parameters supplied by the owning Level and Stage. Unity uses
/// Stage.AsteroidMaxSpeed, Level.HalfMapWidth/Height and strict map bounds
/// when assigning an asteroid's movement. Keep these explicit rather than
/// using the broader spatial-grid bounds as a substitute.
#[derive(Clone, Copy, Debug)]
pub struct FragmentSpawnSettings {
    pub map_bounds: Aabb,
    pub half_map_extents: Vec2,
    pub asteroid_max_speed_exclusive: u32,
    /// Unity computes ceil(OriginalHealth * CurrentHealthRatio), clamped
    /// to at least one, independently for each newly set up shard.
    pub training_health_ratio: Option<f32>,
}

impl FragmentSpawnSettings {
    pub fn valid(self) -> bool {
        self.map_bounds.min.is_finite() &&
        self.map_bounds.max.is_finite() &&
        self.map_bounds.min.x < self.map_bounds.max.x &&
        self.map_bounds.min.y < self.map_bounds.max.y &&
        self.half_map_extents.is_finite() &&
        self.half_map_extents.x > 0.0 &&
        self.half_map_extents.y > 0.0 &&
        self.asteroid_max_speed_exclusive <= (i32::MAX as u32 - 2) &&
        self.training_health_ratio.is_none_or(|ratio|
            ratio.is_finite() && ratio >= 0.0)
    }
}

/// Unity creates a pooled object only once; its randomly selected prefab
/// and Speed are retained when the same pooled object is reused.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FragmentPrototype {
    pub prefab_number: u8,
    pub speed: i32,
}

pub fn new_shard_prototype(
    palette: &AsteroidFragmentPalette,
    settings: FragmentSpawnSettings,
    rng: &mut RandomStream,
) -> FragmentPrototype {
    let prefab_number = palette.choose_shard(rng);
    let speed = 2 + if settings.asteroid_max_speed_exclusive == 0 {
        0
    } else {
        rng.next_below(u64::from(settings.asteroid_max_speed_exclusive)) as i32
    };
    FragmentPrototype { prefab_number, speed }
}

pub fn new_piece_prototype(
    palette: &AsteroidFragmentPalette,
    settings: FragmentSpawnSettings,
    rng: &mut RandomStream,
) -> FragmentPrototype {
    let prefab_number = palette.choose_piece(rng);
    let speed = 2 + if settings.asteroid_max_speed_exclusive == 0 {
        0
    } else {
        rng.next_below(u64::from(settings.asteroid_max_speed_exclusive)) as i32
    };
    FragmentPrototype { prefab_number, speed }
}

/// One fragment's movement. Unity positions the shard/piece at the killed
/// parent's position, gives it a random heading, and points its velocity at
/// a RandomCoordinate sampled near (0, 0); it inherits the parent's angular
/// velocity. The native seed stream is deterministic but is not .NET Random,
/// so the numeric draw sequence cannot be assumed identical to Unity.
pub fn spawn_fragment_motion(
    rng: &mut RandomStream,
    settings: FragmentSpawnSettings,
    start: Vec2,
    parent_angular_velocity: f32,
    speed: i32,
) -> (Pose, Vec2, f32) {
    let heading = rng.next_below(360) as f32;
    let (destination, _, _) = random_map_destination(
        rng, settings.map_bounds, settings.half_map_extents,
    );
    let velocity = (destination - start).normalized_or_zero() * speed as f32;
    (Pose { position: start, rotation_degrees: heading },
        velocity, parent_angular_velocity)
}

/// Shared Unity Utilities.RandomCoordinate behavior for ordinary asteroids
/// and their live shard children. Returns its actual draw count so the
/// owning Level can measure exhausted/expensive placement attempts without
/// a per-frame event log. The last draw remains authoritative after 35
/// unsuccessful attempts, matching the Unity source.
pub(crate) fn random_map_destination(
    rng: &mut RandomStream,
    bounds: Aabb,
    half_extents: Vec2,
) -> (Vec2, u8, bool) {
    let mut destination = Vec2::ZERO;
    for attempt in 1..=35 {
        destination = Vec2::new(
            random_coordinate(rng, half_extents.x),
            random_coordinate(rng, half_extents.y),
        );
        if destination != Vec2::ZERO &&
            destination.x > bounds.min.x &&
            destination.x < bounds.max.x &&
            destination.y > bounds.min.y &&
            destination.y < bounds.max.y {
            return (destination, attempt, true);
        }
    }
    (destination, 35, false)
}

pub(crate) fn random_unit_float(rng: &mut RandomStream) -> f32 {
    // Same 24-bit uniform source as random_coordinate; world-local and
    // deterministic, but intentionally not Unity's System.Random stream.
    (rng.next_u64() >> 40) as f32 / 16_777_216.0
}

fn random_coordinate(rng: &mut RandomStream, half_extent: f32) -> f32 {
    // A 24-bit float fraction suffices for native f32 world positions.
    let fraction = random_unit_float(rng);
    let sign = if rng.next_below(2) == 0 { -1.0 } else { 1.0 };
    fraction * half_extent * sign
}

/// The Unity AsteroidPiece has no gameplay collider. It is kept separately
/// from KinematicPhysics but remains accessible to rendering and snapshots.
/// The 0.5-second timer starts immediately; its ninth invocation removes
/// the piece, matching AsteroidPiece.DeathTimer at HalfSeconds == 8.
#[derive(Clone, Debug)]
pub struct AsteroidPieceState {
    pub entity_id: i64,
    pub prototype: FragmentPrototype,
    pub pose: Pose,
    pub velocity: Vec2,
    pub angular_velocity_degrees: f32,
    pub half_seconds: u8,
    pub active: bool,
    timer: ScaledTimer,
}

impl AsteroidPieceState {
    pub fn new(
        entity_id: i64,
        prototype: FragmentPrototype,
        pose: Pose,
        velocity: Vec2,
        angular_velocity_degrees: f32,
    ) -> Self {
        Self {
            entity_id, prototype, pose, velocity, angular_velocity_degrees,
            half_seconds: 0, active: true,
            timer: ScaledTimer::new(0.5, true, true),
        }
    }

    /// ConfigData.FadingAsteroidPiecesColors is white at alpha 0.9..0.1.
    /// DeathTimer kills at HalfSeconds == 8, so the last (0.1) entry is
    /// never applied by the current Unity source.
    pub fn sprite_opacity(&self) -> f32 {
        if !self.active { return 0.0; }
        if self.half_seconds == 0 { 1.0 }
        else { 1.0 - (self.half_seconds as f32 / 10.0) }
    }

    /// Returns the authored fade-color index, if one was reached this step.
    /// The piece deactivates when the ninth scheduled callback is due.
    pub fn advance(&mut self, dt: f32) -> Option<u8> {
        if !self.active { return None; }
        self.pose.position += self.velocity * dt;
        self.pose.rotation_degrees += self.angular_velocity_degrees * dt;
        let due = self.timer.begin_update(dt)?;
        let fade = if self.half_seconds == 8 {
            self.active = false;
            self.timer.cancel();
            None
        } else {
            let index = self.half_seconds;
            self.half_seconds += 1;
            Some(index)
        };
        self.timer.finish_update(due);
        fade
    }
}
