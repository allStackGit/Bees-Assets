//! Immutable collider catalog for the 28 authored collision-asteroid prefabs.
//!
//! Each entry derives from the active primary Obstacle.Collider polygon,
//! including prefab-variant point-array overrides. Inactive inherited
//! m_Points paths are deliberately NOT part of these shapes.
//! Parse once per process; share the Arc-backed geometry across game worlds.

use serde::Deserialize;

use crate::collider_asset_shape::RawShape;
use crate::collision::Pose;
use crate::geometry::Vec2;
use crate::obstacle::{CombatObstacle, ObstacleKind, ObstacleVitals};
use crate::physics::{ObstacleBody, PhysicsError, ShipShape};

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/asteroid-colliders.json"
));
pub const COLLISION_ASTEROID_COUNT: usize = 28;

#[derive(Clone, Debug)]
pub struct CollisionAsteroidAsset {
    pub name: String,
    pub prefab_number: u8,
    pub size_class: u8,
    pub collision_layer: u8,
    pub shape: ShipShape,
    pub proximity_layer: u8,
    pub proximity_shape: ShipShape,
}

#[derive(Debug)]
pub enum AsteroidAssetError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidIncrement(i32),
    InvalidPrefab(u8),
    MissingPrefab(u8),
    DuplicatePrefab(u8),
    InvalidGeometry(u8),
    InvalidHealth,
    Physics(PhysicsError),
}

impl From<PhysicsError> for AsteroidAssetError {
    fn from(error: PhysicsError) -> Self {
        Self::Physics(error)
    }
}

#[derive(Debug)]
pub struct CollisionAsteroidCatalog {
    asteroids: Vec<CollisionAsteroidAsset>,
    health_increment: i32,
}

#[derive(Deserialize)]
struct RawManifest {
    schema_version: u32,
    collision_asteroid_health_increment: i32,
    asteroids: Vec<RawAsteroid>,
}

#[derive(Deserialize)]
struct RawAsteroid {
    name: String,
    size_class: u8,
    layer: u8,
    shape: RawShape,
    proximity_layer: u8,
    proximity_shape: RawShape,
}

impl CollisionAsteroidCatalog {
    pub fn embedded() -> Result<Self, AsteroidAssetError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(data: &str) -> Result<Self, AsteroidAssetError> {
        let raw: RawManifest = serde_json::from_str(data)
            .map_err(|error| AsteroidAssetError::Malformed(error.to_string()))?;
        if raw.schema_version != 2 {
            return Err(AsteroidAssetError::InvalidSchema(raw.schema_version));
        }
        if raw.collision_asteroid_health_increment <= 0 {
            return Err(AsteroidAssetError::InvalidIncrement(
                raw.collision_asteroid_health_increment,
            ));
        }
        let mut slots: Vec<Option<CollisionAsteroidAsset>> =
            std::iter::repeat_with(|| None).take(COLLISION_ASTEROID_COUNT).collect();
        for entry in raw.asteroids {
            let Some(number_string) = entry.name.strip_prefix("Asteroid ") else {
                return Err(AsteroidAssetError::InvalidPrefab(0));
            };
            let number: u8 = number_string.parse()
                .map_err(|_| AsteroidAssetError::InvalidPrefab(0))?;
            if number == 0 || usize::from(number) > COLLISION_ASTEROID_COUNT {
                return Err(AsteroidAssetError::InvalidPrefab(number));
            }
            let index = usize::from(number - 1);
            if slots[index].is_some() {
                return Err(AsteroidAssetError::DuplicatePrefab(number));
            }
            if entry.name != format!("Asteroid {number}") ||
                entry.layer >= 32 || entry.proximity_layer >= 32 ||
                entry.size_class == 0 {
                return Err(AsteroidAssetError::InvalidGeometry(number));
            }
            let shape = entry.shape.into_shape()
                .ok_or(AsteroidAssetError::InvalidGeometry(number))?;
            // All active authored primary asteroid colliders are single
            // polygons. Do not silently accept future multishape assets
            // without making physics account for those extra paths.
            if !matches!(&shape, ShipShape::Polygon(_)) {
                return Err(AsteroidAssetError::InvalidGeometry(number));
            }
            let proximity_shape = entry.proximity_shape.into_shape()
                .ok_or(AsteroidAssetError::InvalidGeometry(number))?;
            if !matches!(&proximity_shape, ShipShape::Polygon(_)) {
                return Err(AsteroidAssetError::InvalidGeometry(number));
            }
            slots[index] = Some(CollisionAsteroidAsset {
                name: entry.name,
                prefab_number: number,
                size_class: entry.size_class,
                collision_layer: entry.layer,
                shape,
                proximity_layer: entry.proximity_layer,
                proximity_shape,
            });
        }
        let asteroids = slots.into_iter().enumerate()
            .map(|(index, entry)| entry.ok_or(
                AsteroidAssetError::MissingPrefab((index + 1) as u8),
            ))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self {
            asteroids,
            health_increment: raw.collision_asteroid_health_increment,
        })
    }

    pub fn health_increment(&self) -> i32 {
        self.health_increment
    }

    pub fn by_number(&self, number: u8) -> Option<&CollisionAsteroidAsset> {
        number.checked_sub(1)
            .and_then(|index| self.asteroids.get(usize::from(index)))
    }

    pub fn asteroids(&self) -> &[CollisionAsteroidAsset] {
        &self.asteroids
    }

    /// Unity CollisionAsteroid.Create sets OriginalHealth to SizeClass *
    /// CollisionAsteroidHealthIncrement. Training may replace effective
    /// max health at Setup; callers pass that exact training result here.
    ///
    /// The returned obstacle and physics body must enter the same world.
    /// Gameplay RNG still determines spawn position/orientation/velocity.
    pub fn prepare_spawn(
        &self,
        prefab_number: u8,
        entity_id: i64,
        pose: Pose,
        linear_velocity: Vec2,
        angular_velocity_degrees: f32,
        effective_max_health: Option<i32>,
    ) -> Result<(CombatObstacle, ObstacleBody), AsteroidAssetError> {
        let asset = self.by_number(prefab_number)
            .ok_or(AsteroidAssetError::InvalidPrefab(prefab_number))?;
        let original_health = i32::from(asset.size_class)
            .checked_mul(self.health_increment)
            .ok_or(AsteroidAssetError::InvalidHealth)?;
        let active_health = effective_max_health.unwrap_or(original_health);
        let vitals = ObstacleVitals::with_original_health(
            active_health, active_health, original_health,
        ).ok_or(AsteroidAssetError::InvalidHealth)?;
        let mut body = ObstacleBody::new(
            entity_id, pose, asset.collision_layer, asset.shape.clone(),
            linear_velocity, angular_velocity_degrees,
        )?;
        body.set_proximity_collider(
            asset.proximity_layer, asset.proximity_shape.clone(),
        )?;
        Ok((
            CombatObstacle {
                entity_id,
                kind: ObstacleKind::CollisionAsteroid,
                vitals: Some(vitals),
                size_class: asset.size_class,
            },
            body,
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_authored_asteroid_shapes_and_training_health_are_available() {
        let catalog = CollisionAsteroidCatalog::embedded().unwrap();
        assert_eq!(catalog.asteroids().len(), COLLISION_ASTEROID_COUNT);
        assert_eq!(catalog.health_increment(), 250);
        assert_eq!(catalog.by_number(1).unwrap().size_class, 3);
        assert_eq!(catalog.by_number(11).unwrap().size_class, 6);
        assert!(catalog.by_number(0).is_none());
        assert!(catalog.by_number(29).is_none());
        let (obstacle, body) = catalog.prepare_spawn(
            11, 73,
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 },
            Vec2::ZERO, 0.0, Some(1),
        ).unwrap();
        assert_eq!(obstacle.vitals.unwrap().health, 1);
        assert_eq!(body.id, 73);
        assert!(matches!(body.shape(), ShipShape::Polygon(_)));
    }
}
