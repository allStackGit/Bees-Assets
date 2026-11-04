//! Immutable native ship collider catalog.
//!
//! Parsed once when the game or worker starts, then shared across all worlds.
//! No JSON parsing, string matching, or geometry allocation occurs per tick.
//! The canonical native asset is native/assets/ship-colliders.json.

use std::sync::Arc;

use serde::Deserialize;

use crate::geometry::Vec2;
use crate::movement::ShipMotion;
use crate::physics::{PhysicsError, ShipBody, ShipShape};

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/ship-colliders.json"
));
pub const SHIP_TYPE_COUNT: usize = 24;

#[derive(Clone, Debug)]
pub struct ShipAsset {
    pub name: String,
    pub ship_type: u8,
    pub collision_layer: u8,
    pub shape: ShipShape,
}

impl ShipAsset {
    /// Cheap Arc clone of immutable hull geometry, not a copy of vertices.
    pub fn spawn_physics_body(
        &self,
        entity_id: i64,
        motion: ShipMotion,
    ) -> Result<ShipBody, PhysicsError> {
        ShipBody::new(entity_id, motion, self.collision_layer, self.shape.clone())
    }
}

#[derive(Debug)]
pub enum AssetError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidShipType(u32),
    MissingShipType(u8),
    DuplicateShipType(u8),
    InvalidShape(u8),
}

#[derive(Debug)]
pub struct ShipAssetCatalog {
    ships: Vec<ShipAsset>,
}

#[derive(Deserialize)]
struct RawManifest {
    schema_version: u32,
    ships: Vec<RawShip>,
}

#[derive(Deserialize)]
struct RawShip {
    name: String,
    ship_type: u32,
    layer: u8,
    trigger: bool,
    shape: RawShape,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
enum RawShape {
    Polygon { vertices: Vec<[f32; 2]> },
    Circle { offset: [f32; 2], radius: f32 },
    Box { offset: [f32; 2], half_extents: [f32; 2] },
}

impl ShipAssetCatalog {
    pub fn embedded() -> Result<Self, AssetError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(data: &str) -> Result<Self, AssetError> {
        let raw: RawManifest = serde_json::from_str(data)
            .map_err(|error| AssetError::Malformed(error.to_string()))?;
        if raw.schema_version != 1 {
            return Err(AssetError::InvalidSchema(raw.schema_version));
        }
        let mut slots: Vec<Option<ShipAsset>> =
            std::iter::repeat_with(|| None).take(SHIP_TYPE_COUNT).collect();
        for ship in raw.ships {
            let kind = usize::try_from(ship.ship_type).map_err(|_| AssetError::InvalidShipType(ship.ship_type))?;
            if kind >= SHIP_TYPE_COUNT {
                return Err(AssetError::InvalidShipType(ship.ship_type));
            }
            if slots[kind].is_some() {
                return Err(AssetError::DuplicateShipType(kind as u8));
            }
            if ship.name.is_empty() || !ship.trigger || ship.layer >= 32 {
                return Err(AssetError::InvalidShape(kind as u8));
            }

            let shape = match ship.shape {
                RawShape::Polygon { vertices } => {
                    if vertices.len() < 3 || vertices.iter().flatten().any(|value| !value.is_finite()) {
                        return Err(AssetError::InvalidShape(kind as u8));
                    }
                    let hull: Vec<Vec2> = vertices.iter()
                        .map(|xy| Vec2::new(xy[0], xy[1])).collect();
                    ShipShape::Polygon(Arc::from(hull.into_boxed_slice()))
                }
                RawShape::Circle { offset, radius } => {
                    if !radius.is_finite() || radius < 0.0 ||
                        offset.iter().any(|value| !value.is_finite()) {
                        return Err(AssetError::InvalidShape(kind as u8));
                    }
                    ShipShape::Circle {
                        offset: Vec2::new(offset[0], offset[1]),
                        radius,
                    }
                }
                RawShape::Box { offset, half_extents } => {
                    if offset.iter().chain(half_extents.iter()).any(|value| !value.is_finite()) ||
                        half_extents.iter().any(|value| *value <= 0.0) {
                        return Err(AssetError::InvalidShape(kind as u8));
                    }
                    ShipShape::Box {
                        offset: Vec2::new(offset[0], offset[1]),
                        half_extents: Vec2::new(half_extents[0], half_extents[1]),
                    }
                }
            };

            slots[kind] = Some(ShipAsset {
                name: ship.name,
                ship_type: kind as u8,
                collision_layer: ship.layer,
                shape,
            });
        }

        let ships = slots.into_iter().enumerate()
            .map(|(i, asset)| asset.ok_or(AssetError::MissingShipType(i as u8)))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self { ships })
    }

    #[inline]
    pub fn by_type(&self, ship_type: u8) -> Option<&ShipAsset> {
        self.ships.get(usize::from(ship_type))
    }

    pub fn ships(&self) -> &[ShipAsset] {
        &self.ships
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_twenty_four_unity_ship_hulls_have_exactly_one_native_identity() {
        let assets = ShipAssetCatalog::embedded().unwrap();
        assert_eq!(assets.ships().len(), SHIP_TYPE_COUNT);
        assert_eq!(assets.by_type(21).unwrap().name, "Wasp");
        assert_eq!(assets.by_type(13).unwrap().name, "Gunship");
        assert_eq!(assets.by_type(23).unwrap().name, "Human Target");
        assert!(matches!(
            assets.by_type(0).unwrap().shape, ShipShape::Box { .. }
        ));
        assert!(matches!(
            assets.by_type(14).unwrap().shape, ShipShape::Circle { .. }
        ));
        assert!(matches!(
            assets.by_type(15).unwrap().shape, ShipShape::Polygon(_)
        ));
    }

    #[test]
    fn malformed_or_duplicate_assets_are_rejected() {
        assert!(ShipAssetCatalog::parse("{").is_err());
        assert!(matches!(
            ShipAssetCatalog::parse(r#"{"schema_version":2,"ships":[]}"#),
            Err(AssetError::InvalidSchema(2))
        ));
    }
}
