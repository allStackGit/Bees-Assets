//! Immutable source-authored static box and map-border prefab catalog.
//!
//! Only primary Obstacle.Collider BoxCollider2D triggers are imported.
//! Prefab-default positions and orientations are retained, but level/map
//! placement may replace them without modifying immutable collider geometry.

use serde::Deserialize;

use crate::collider_asset_shape::RawShape;
use crate::collision::Pose;
use crate::geometry::Vec2;
use crate::obstacle::{CombatObstacle, ObstacleKind};
use crate::physics::{ObstacleBody, PhysicsError, ShipShape};

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/box-obstacle-colliders.json"
));

pub const BOX_OBSTACLE_COUNT: usize = 10;

#[derive(Clone, Debug)]
pub struct BoxObstacleAsset {
    pub name: String,
    pub kind: ObstacleKind,
    pub collision_layer: u8,
    pub authored_health: i32,
    pub default_pose: Pose,
    pub shape: ShipShape,
}

impl BoxObstacleAsset {
    pub fn prepare_spawn(
        &self,
        entity_id: i64,
        override_pose: Option<Pose>,
    ) -> Result<(CombatObstacle, ObstacleBody), PhysicsError> {
        let body = ObstacleBody::new(
            entity_id,
            override_pose.unwrap_or(self.default_pose),
            self.collision_layer,
            self.shape.clone(),
            Vec2::ZERO,
            0.0,
        )?;
        // The projectile/obstacle rules never damage StaticObstacle or
        // MapBorder. Authored health is retained in asset metadata but is
        // not represented as CollisionAsteroid-style damageable vitality.
        let combat = CombatObstacle {
            entity_id,
            kind: self.kind,
            vitals: None,
            size_class: 0,
        };
        Ok((combat, body))
    }
}

#[derive(Debug)]
pub enum BoxObstacleAssetError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidAsset(usize),
    DuplicateName(String),
    MissingAsset,
}

#[derive(Debug)]
pub struct BoxObstacleCatalog {
    assets: Vec<BoxObstacleAsset>,
}

#[derive(Deserialize)]
struct RawManifest {
    schema_version: u32,
    obstacles: Vec<RawObstacle>,
}

#[derive(Deserialize)]
struct RawObstacle {
    name: String,
    obstacle_type: u8,
    layer: u8,
    authored_health: i32,
    default_pose: RawPose,
    shape: RawShape,
}

#[derive(Deserialize)]
struct RawPose {
    position: [f32; 2],
    rotation_degrees: f32,
}

impl BoxObstacleCatalog {
    pub fn embedded() -> Result<Self, BoxObstacleAssetError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(data: &str) -> Result<Self, BoxObstacleAssetError> {
        let raw: RawManifest = serde_json::from_str(data)
            .map_err(|error| BoxObstacleAssetError::Malformed(error.to_string()))?;
        if raw.schema_version != 1 {
            return Err(BoxObstacleAssetError::InvalidSchema(raw.schema_version));
        }
        if raw.obstacles.len() != BOX_OBSTACLE_COUNT {
            return Err(BoxObstacleAssetError::MissingAsset);
        }
        let mut assets = Vec::with_capacity(raw.obstacles.len());
        for (index, entry) in raw.obstacles.into_iter().enumerate() {
            if entry.name.is_empty() || entry.layer >= 32 ||
                entry.authored_health < 0 ||
                !entry.default_pose.rotation_degrees.is_finite() ||
                entry.default_pose.position.iter().any(|v| !v.is_finite()) {
                return Err(BoxObstacleAssetError::InvalidAsset(index));
            }
            let kind = match entry.obstacle_type {
                0 => ObstacleKind::StaticObstacle,
                1 => ObstacleKind::MapBorder,
                _ => return Err(BoxObstacleAssetError::InvalidAsset(index)),
            };
            let shape = entry.shape.into_shape()
                .ok_or(BoxObstacleAssetError::InvalidAsset(index))?;
            if !matches!(&shape, ShipShape::Box { .. }) {
                return Err(BoxObstacleAssetError::InvalidAsset(index));
            }
            if assets.iter().any(|a: &BoxObstacleAsset| a.name == entry.name) {
                return Err(BoxObstacleAssetError::DuplicateName(entry.name));
            }
            assets.push(BoxObstacleAsset {
                name: entry.name, kind,
                collision_layer: entry.layer,
                authored_health: entry.authored_health,
                default_pose: Pose {
                    position: Vec2::new(entry.default_pose.position[0],
                                        entry.default_pose.position[1]),
                    rotation_degrees: entry.default_pose.rotation_degrees,
                },
                shape,
            });
        }
        Ok(Self { assets })
    }

    pub fn by_name(&self, name: &str) -> Option<&BoxObstacleAsset> {
        self.assets.iter().find(|asset| asset.name == name)
    }

    pub fn assets(&self) -> &[BoxObstacleAsset] {
        &self.assets
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn authored_obstacle_geometry_is_shared_by_native_spawns() {
        let catalog = BoxObstacleCatalog::embedded().unwrap();
        assert_eq!(catalog.assets().len(), 10);
        let slanted = catalog.by_name("Slanted").unwrap();
        assert!(slanted.default_pose.rotation_degrees > 122.99);
        assert!(slanted.default_pose.rotation_degrees < 123.01);
        assert!(matches!(&slanted.shape, ShipShape::Box { .. }));
        let (combat, physics) = slanted.prepare_spawn(17, None).unwrap();
        assert_eq!(combat.kind, ObstacleKind::StaticObstacle);
        assert_eq!(physics.id, 17);
        assert_eq!(physics.layer, 19);

        let top = catalog.by_name("Top Border").unwrap();
        let left = catalog.by_name("Left Border Variant").unwrap();
        assert_eq!(top.kind, ObstacleKind::MapBorder);
        assert_eq!(left.kind, ObstacleKind::MapBorder);
        assert!(matches!(&top.shape, ShipShape::Box { half_extents, .. }
            if half_extents.x == 280.0 && half_extents.y == 12.0));
        assert!(matches!(&left.shape, ShipShape::Box { half_extents, .. }
            if half_extents.x == 12.0 && half_extents.y == 280.0));
    }
}
