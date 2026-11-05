//! Native v26 mining, map-object and collision-asteroid observation blocks.
//!
//! Source: RlCombatPerception.CollectVisibleMiningAsteroids,
//! CollectVisibleMapObjects, CollectVisibleEnvironment, and Add*Slots.
//! Callers supply actual HiveMind-visible, alive objects and level-local
//! Collider2D bounds. The native encoder does not invent discoveries or
//! substitute dynamic obstacles for source-defined static navigation cells.

use crate::control::PolicyFrame;
use crate::geometry::Vec2;
use crate::perception::{
    ASTEROID_LEN, ASTEROID_SLOTS, ASTEROIDS_START, MAP_OBJECT_LEN,
    MAP_OBJECT_SLOTS, MAP_OBJECTS_START, MINING_LEN, MINING_SLOTS, MINING_START,
    PerceptionError, normalize_positive, policy_heading, squash_signed_distance,
    transform_extents,
};
use crate::policy::OBSERVATION_FLOATS;

#[derive(Clone, Copy, Debug)]
pub struct MiningAsteroid {
    pub id: i64,
    pub position: Vec2,
    pub half_extents: Vec2,
    /// Health / OriginalHealth from the actual mining asteroid.
    pub resource_fraction: f32,
    /// Unity's SquadsMining.Count, not a guessed rate.
    pub squads_mining: u32,
    pub alive: bool,
}

// Preserve the original encoder-facing path while owning the type in
// gameplay. Its discriminators are source-defined observation map bits.
pub use crate::map_object::MapObjectKind;

#[derive(Clone, Copy, Debug)]
pub struct MapObject {
    pub id: i64,
    pub kind: MapObjectKind,
    pub position: Vec2,
    pub half_extents: Vec2,
    pub health_fraction: f32,
    pub alive: bool,
}

#[derive(Clone, Copy, Debug)]
pub struct CollisionAsteroid {
    pub id: i64,
    pub position: Vec2,
    pub half_extents: Vec2,
    /// Unity's localEulerAngles.z of the asteroid transform.
    pub rotation_degrees: f32,
    /// Inverse-transformed level-local body velocity, not world velocity.
    pub level_velocity: Vec2,
    pub health_fraction: f32,
    pub alive: bool,
}

fn geometry_valid(position: Vec2, extents: Vec2) -> bool {
    position.is_finite() && extents.is_finite() &&
        extents.x >= 0.0 && extents.y >= 0.0
}

fn valid_fraction(value: f32) -> bool {
    value.is_finite() && (0.0..=1.0).contains(&value)
}

fn valid_row(row: &[f32], origin: Vec2) -> Result<(), PerceptionError> {
    if row.len() != OBSERVATION_FLOATS {
        return Err(PerceptionError::WrongRowSize { actual: row.len() });
    }
    if !origin.is_finite() {
        return Err(PerceptionError::InvalidSource);
    }
    Ok(())
}

#[inline]
fn center_distance(position: Vec2, origin: Vec2) -> f32 {
    (position - origin).length_squared()
}

#[inline]
fn distance_to_bounds_squared(asteroid: &CollisionAsteroid, origin: Vec2) -> f32 {
    let relative = asteroid.position - origin;
    let dx = (relative.x.abs() - asteroid.half_extents.x).max(0.0);
    let dy = (relative.y.abs() - asteroid.half_extents.y).max(0.0);
    dx * dx + dy * dy
}

/// Reuse candidate sorting storage across decisions. All zero padding here
/// represents source-defined absent objects, not missing simulator features.
#[derive(Default)]
pub struct EnvironmentSlotEncoder {
    mining: Vec<MiningAsteroid>,
    objects: Vec<MapObject>,
    asteroids: Vec<CollisionAsteroid>,
}

impl EnvironmentSlotEncoder {
    pub fn new() -> Self { Self::default() }

    /// Inputs come from GetMiningAsteroidsVisibleToHiveMind(side). Sort by
    /// squared center distance, then runtime ID, and encode nearest eight.
    pub fn write_mining(
        &mut self,
        row: &mut [f32],
        candidates: &[MiningAsteroid],
        observer: Vec2,
        frame: PolicyFrame,
    ) -> Result<usize, PerceptionError> {
        valid_row(row, observer)?;
        if candidates.iter().any(|item| !geometry_valid(item.position, item.half_extents) ||
            !valid_fraction(item.resource_fraction)) {
            return Err(PerceptionError::InvalidSource);
        }
        self.mining.clear();
        self.mining.extend(candidates.iter().copied().filter(|item| item.alive));
        self.mining.sort_unstable_by(|left, right| {
            center_distance(left.position, observer).total_cmp(
                &center_distance(right.position, observer)
            ).then_with(|| left.id.cmp(&right.id))
        });
        let count = self.mining.len().min(MINING_SLOTS);
        for (slot, &item) in self.mining.iter().take(count).enumerate() {
            let relative = frame.world_to_policy(item.position - observer);
            let extents = transform_extents(item.half_extents, frame);
            let values: [f32; MINING_LEN] = [
                1.0, squash_signed_distance(relative.x),
                squash_signed_distance(relative.y),
                item.resource_fraction,
                normalize_positive(extents.x, 20.0),
                normalize_positive(extents.y, 20.0),
                normalize_positive(item.squads_mining as f32, 4.0),
            ];
            let start = MINING_START + slot * MINING_LEN;
            row[start..start + MINING_LEN].copy_from_slice(&values);
        }
        row[MINING_START + count * MINING_LEN..MAP_OBJECTS_START].fill(0.0);
        Ok(count)
    }

    /// Current Unity map objects are generic map objects or fire tanks.
    /// Both are always targetable, with activity zero, per source code.
    pub fn write_map_objects(
        &mut self,
        row: &mut [f32],
        candidates: &[MapObject],
        observer: Vec2,
        frame: PolicyFrame,
    ) -> Result<usize, PerceptionError> {
        valid_row(row, observer)?;
        if candidates.iter().any(|item| !geometry_valid(item.position, item.half_extents) ||
            !valid_fraction(item.health_fraction)) {
            return Err(PerceptionError::InvalidSource);
        }
        self.objects.clear();
        self.objects.extend(candidates.iter().copied().filter(|item| item.alive));
        self.objects.sort_unstable_by(|left, right| {
            center_distance(left.position, observer).total_cmp(
                &center_distance(right.position, observer)
            ).then_with(|| left.kind.cmp(&right.kind))
                .then_with(|| left.id.cmp(&right.id))
        });
        let count = self.objects.len().min(MAP_OBJECT_SLOTS);
        for (slot, &item) in self.objects.iter().take(count).enumerate() {
            let relative = frame.world_to_policy(item.position - observer);
            let extents = transform_extents(item.half_extents, frame);
            let bits = item.kind as u8;
            let values: [f32; MAP_OBJECT_LEN] = [
                1.0,
                if bits & 1 != 0 { 1.0 } else { 0.0 },
                if bits & 2 != 0 { 1.0 } else { 0.0 },
                if bits & 4 != 0 { 1.0 } else { 0.0 },
                if bits & 8 != 0 { 1.0 } else { 0.0 },
                squash_signed_distance(relative.x),
                squash_signed_distance(relative.y),
                item.health_fraction,
                normalize_positive(extents.x, 20.0),
                normalize_positive(extents.y, 20.0),
                1.0, // current source map objects are point-fire targets
                0.0, // current source map-object activity is zero
            ];
            let start = MAP_OBJECTS_START + slot * MAP_OBJECT_LEN;
            row[start..start + MAP_OBJECT_LEN].copy_from_slice(&values);
        }
        row[MAP_OBJECTS_START + count * MAP_OBJECT_LEN..ASTEROIDS_START].fill(0.0);
        Ok(count)
    }

    /// Sort collision asteroids by nearest AABB boundary, NOT center
    /// distance. The source then breaks ties by runtime entity ID.
    pub fn write_collision_asteroids(
        &mut self,
        row: &mut [f32],
        candidates: &[CollisionAsteroid],
        observer: Vec2,
        frame: PolicyFrame,
    ) -> Result<usize, PerceptionError> {
        valid_row(row, observer)?;
        if candidates.iter().any(|item| !geometry_valid(item.position, item.half_extents) ||
            !item.rotation_degrees.is_finite() || !item.level_velocity.is_finite() ||
            !valid_fraction(item.health_fraction)) {
            return Err(PerceptionError::InvalidSource);
        }
        self.asteroids.clear();
        self.asteroids.extend(candidates.iter().copied().filter(|item| item.alive));
        self.asteroids.sort_unstable_by(|left, right| {
            distance_to_bounds_squared(left, observer).total_cmp(
                &distance_to_bounds_squared(right, observer)
            ).then_with(|| left.id.cmp(&right.id))
        });
        let count = self.asteroids.len().min(ASTEROID_SLOTS);
        for (slot, &item) in self.asteroids.iter().take(count).enumerate() {
            let relative = frame.world_to_policy(item.position - observer);
            let extents = transform_extents(item.half_extents, frame);
            let velocity = frame.world_to_policy(item.level_velocity);
            let heading = policy_heading(item.rotation_degrees, frame);
            let values: [f32; ASTEROID_LEN] = [
                1.0,
                squash_signed_distance(relative.x),
                squash_signed_distance(relative.y),
                normalize_positive(extents.x, 20.0),
                normalize_positive(extents.y, 20.0),
                heading.x, heading.y,
                squash_signed_distance(velocity.x),
                squash_signed_distance(velocity.y),
                item.health_fraction,
                1.0, // source: destructible point-fire target
            ];
            let start = ASTEROIDS_START + slot * ASTEROID_LEN;
            row[start..start + ASTEROID_LEN].copy_from_slice(&values);
        }
        row[ASTEROIDS_START + count * ASTEROID_LEN..
            ASTEROIDS_START + ASTEROID_SLOTS * ASTEROID_LEN].fill(0.0);
        Ok(count)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::perception::OBJECTIVE_START;

    #[test]
    fn environment_slots_preserve_bits_and_boundary_distance_order() {
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        let mut encoder = EnvironmentSlotEncoder::new();
        let frame = PolicyFrame::new(0);
        let origin = Vec2::ZERO;
        assert_eq!(encoder.write_mining(&mut row, &[MiningAsteroid {
            id: 12, position: Vec2::new(10.0, 0.0),
            half_extents: Vec2::new(2.0, 3.0),
            resource_fraction: 0.25, squads_mining: 2, alive: true,
        }], origin, frame).unwrap(), 1);
        assert_eq!(row[MINING_START], 1.0);
        assert_eq!(row[MINING_START + 3], 0.25);
        assert_eq!(row[MINING_START + MINING_LEN], 0.0);

        assert_eq!(encoder.write_map_objects(&mut row, &[MapObject {
            id: 4, kind: MapObjectKind::FireTank,
            position: Vec2::new(20.0, 0.0),
            half_extents: Vec2::new(1.0, 1.0),
            health_fraction: 1.0, alive: true,
        }], origin, frame).unwrap(), 1);
        assert_eq!(&row[MAP_OBJECTS_START + 1..MAP_OBJECTS_START + 5],
            &[1.0, 1.0, 0.0, 0.0]);
        assert_eq!(row[MAP_OBJECTS_START + 10], 1.0);
        assert_eq!(row[MAP_OBJECTS_START + 11], 0.0);

        let close_bounds = CollisionAsteroid {
            id: 99, position: Vec2::new(30.0, 0.0),
            half_extents: Vec2::new(28.0, 1.0),
            rotation_degrees: 0.0,
            level_velocity: Vec2::new(10.0, -10.0),
            health_fraction: 0.75, alive: true,
        };
        let close_center = CollisionAsteroid {
            id: 55, position: Vec2::new(10.0, 0.0),
            half_extents: Vec2::new(1.0, 1.0),
            rotation_degrees: 0.0,
            level_velocity: Vec2::ZERO,
            health_fraction: 1.0, alive: true,
        };
        assert_eq!(encoder.write_collision_asteroids(&mut row,
            &[close_center, close_bounds], origin, frame).unwrap(), 2);
        assert_eq!(row[ASTEROIDS_START + 9], 0.75);
        assert_eq!(row[ASTEROIDS_START + ASTEROID_LEN + 9], 1.0);
        assert_eq!(row[ASTEROIDS_START + 2 * ASTEROID_LEN], 0.0);
        assert!(row[OBJECTIVE_START..].iter().all(|v| v.is_nan()));
    }
}
