//! Source-authored standalone Polygon Obstacle gameplay collider.
//! The primary Obstacle.Collider is a root PolygonCollider2D, distinct
//! from the child ProximityCollider used by obstacle sensing.
//! The 208-vertex concave outline is imported with its prefab root scale.

use serde::Deserialize;

use crate::collider_asset_shape::RawShape;
use crate::collision::Pose;
use crate::geometry::Vec2;
use crate::obstacle::{CombatObstacle, ObstacleKind};
use crate::physics::{ObstacleBody, PhysicsError, ShipShape};

const MANIFEST: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/polygon-obstacle-colliders.json"
));

#[derive(Debug)]
pub enum PolygonObstacleAssetError {
    Malformed(String),
    InvalidAsset,
    Physics(PhysicsError),
}

impl From<PhysicsError> for PolygonObstacleAssetError {
    fn from(error: PhysicsError) -> Self { Self::Physics(error) }
}

pub struct PolygonObstacleCatalog {
    shape: ShipShape,
    collision_layer: u8,
    default_pose: Pose,
    pub authored_health: i32,
}

#[derive(Deserialize)]
struct Manifest {
    schema_version: u32,
    obstacles: Vec<RawPolygonObstacle>,
}
#[derive(Deserialize)]
struct RawPolygonObstacle {
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

impl PolygonObstacleCatalog {
    pub fn embedded() -> Result<Self, PolygonObstacleAssetError> {
        let mut manifest: Manifest = serde_json::from_str(MANIFEST)
            .map_err(|error| PolygonObstacleAssetError::Malformed(error.to_string()))?;
        if manifest.schema_version != 1 || manifest.obstacles.len() != 1 {
            return Err(PolygonObstacleAssetError::InvalidAsset);
        }
        let raw = manifest.obstacles.pop().ok_or(PolygonObstacleAssetError::InvalidAsset)?;
        if raw.name != "Polygon Obstacle" || raw.obstacle_type != 0 ||
            raw.layer >= 32 || raw.authored_health < 0 ||
            !raw.default_pose.rotation_degrees.is_finite() ||
            raw.default_pose.position.iter().any(|v| !v.is_finite()) {
            return Err(PolygonObstacleAssetError::InvalidAsset);
        }
        let shape = raw.shape.into_shape()
            .ok_or(PolygonObstacleAssetError::InvalidAsset)?;
        if !matches!(&shape, ShipShape::Polygon(_)) {
            return Err(PolygonObstacleAssetError::InvalidAsset);
        }
        Ok(Self {
            shape,
            collision_layer: raw.layer,
            default_pose: Pose {
                position: Vec2::new(
                    raw.default_pose.position[0], raw.default_pose.position[1],
                ),
                rotation_degrees: raw.default_pose.rotation_degrees,
            },
            authored_health: raw.authored_health,
        })
    }

    /// All worlds share the same immutable collider vertices. The level
    /// supplies optional placement, never a substitute approximate hull.
    pub fn prepare_spawn(
        &self,
        entity_id: i64,
        override_pose: Option<Pose>,
    ) -> Result<(CombatObstacle, ObstacleBody), PolygonObstacleAssetError> {
        let body = ObstacleBody::new(
            entity_id,
            override_pose.unwrap_or(self.default_pose),
            self.collision_layer,
            self.shape.clone(),
            Vec2::ZERO,
            0.0,
        )?;
        let obstacle = CombatObstacle {
            entity_id,
            kind: ObstacleKind::StaticObstacle,
            vitals: None,
            size_class: 0,
        };
        Ok((obstacle, body))
    }
}
