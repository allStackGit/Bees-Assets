//! Immutable source-authored projectile collider assets. Parsed once per
//! process from native/assets/projectile-colliders.json. Rendered gameplay,
//! training and evaluation all construct physics bodies through this catalog.

use serde::Deserialize;

use crate::collider_asset_shape::RawShape;
use crate::physics::{PhysicsError, ProjectileBody, ShipShape};
use crate::projectile::ProjectileMotion;

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/projectile-colliders.json"
));
pub const PROJECTILE_TYPE_COUNT: usize = 15;

#[derive(Clone, Debug)]
pub struct ProjectileAsset {
    pub name: String,
    pub projectile_type: u8,
    pub collision_layer: u8,
    pub has_body: bool,
    pub rocket_max_speed: Option<f32>,
    pub shape: Option<ShipShape>,
}

impl ProjectileAsset {
    /// Initial rotation follows Projectile.Setup: -(Angle * Rad2Deg).
    /// Some special projectile classes override that on later game ticks.
    pub fn spawn_physics_body(
        &self,
        entity_id: i64,
        motion: ProjectileMotion,
        rotation_degrees: f32,
    ) -> Result<ProjectileBody, PhysicsError> {
        ProjectileBody::from_shape(
            entity_id, motion, self.collision_layer, self.shape.clone(),
            self.has_body, rotation_degrees,
        )
    }
}

#[derive(Debug)]
pub enum ProjectileAssetError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidType(u32),
    DuplicateType(u8),
    MissingType(u8),
    InvalidShape(u8),
}

#[derive(Debug)]
pub struct ProjectileAssetCatalog {
    /// Type zero is ConfigData.ProjectileTypes.None.
    projectiles: Vec<ProjectileAsset>,
}

#[derive(Deserialize)]
struct RawManifest {
    schema_version: u32,
    projectiles: Vec<RawProjectile>,
}

#[derive(Deserialize)]
struct RawProjectile {
    name: String,
    projectile_type: u32,
    layer: u8,
    has_body: bool,
    rocket_max_speed: Option<f32>,
    shape: Option<RawShape>,
}

impl ProjectileAssetCatalog {
    pub fn embedded() -> Result<Self, ProjectileAssetError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(data: &str) -> Result<Self, ProjectileAssetError> {
        let raw: RawManifest = serde_json::from_str(data)
            .map_err(|error| ProjectileAssetError::Malformed(error.to_string()))?;
        if raw.schema_version != 1 {
            return Err(ProjectileAssetError::InvalidSchema(raw.schema_version));
        }
        let mut slots: Vec<Option<ProjectileAsset>> =
            std::iter::repeat_with(|| None).take(PROJECTILE_TYPE_COUNT).collect();
        for entry in raw.projectiles {
            if entry.projectile_type == 0 ||
                entry.projectile_type > PROJECTILE_TYPE_COUNT as u32
            {
                return Err(ProjectileAssetError::InvalidType(entry.projectile_type));
            }
            let index = (entry.projectile_type - 1) as usize;
            if slots[index].is_some() {
                return Err(ProjectileAssetError::DuplicateType(entry.projectile_type as u8));
            }
            if entry.name.is_empty() || entry.layer >= 32 ||
                (entry.projectile_type == 5 && !entry.rocket_max_speed.is_some_and(|speed| speed.is_finite() && speed > 0.0)) ||
                (entry.projectile_type != 5 && entry.rocket_max_speed.is_some()) {
                return Err(ProjectileAssetError::InvalidShape(entry.projectile_type as u8));
            }
            let shape = entry.shape
                .map(|value| value.into_shape()
                    .ok_or(ProjectileAssetError::InvalidShape(entry.projectile_type as u8)))
                .transpose()?;
            // A projectile without a collider is a script-driven entity
            // (currently StrikerBomb), not a ghost circle of radius zero.
            slots[index] = Some(ProjectileAsset {
                name: entry.name,
                projectile_type: entry.projectile_type as u8,
                collision_layer: entry.layer,
                has_body: entry.has_body,
                rocket_max_speed: entry.rocket_max_speed,
                shape,
            });
        }
        let projectiles = slots.into_iter().enumerate()
            .map(|(i, asset)| asset.ok_or(ProjectileAssetError::MissingType((i + 1) as u8)))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self { projectiles })
    }

    #[inline]
    pub fn by_type(&self, projectile_type: u8) -> Option<&ProjectileAsset> {
        projectile_type.checked_sub(1)
            .and_then(|index| self.projectiles.get(usize::from(index)))
    }

    pub fn projectiles(&self) -> &[ProjectileAsset] {
        &self.projectiles
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn projectile_assets_use_authored_colliders_and_all_types() {
        let catalog = ProjectileAssetCatalog::embedded().unwrap();
        assert_eq!(catalog.projectiles().len(), PROJECTILE_TYPE_COUNT);
        assert!(matches!(&catalog.by_type(4).unwrap().shape, Some(ShipShape::Box { .. })));
        assert!(matches!(&catalog.by_type(8).unwrap().shape, Some(ShipShape::Box { .. })));
        assert!(!catalog.by_type(12).unwrap().has_body);
        assert!(catalog.by_type(12).unwrap().shape.is_none());
        assert!(!catalog.by_type(13).unwrap().has_body);
        assert_eq!(catalog.by_type(14).unwrap().collision_layer, 20);
        assert_eq!(catalog.by_type(5).unwrap().rocket_max_speed, Some(160.0));
        assert!(catalog.by_type(0).is_none());
    }
}
